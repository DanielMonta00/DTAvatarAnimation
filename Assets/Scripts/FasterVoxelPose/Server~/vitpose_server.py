#!/usr/bin/env python
"""ViTPose inference server for the Unity digital twin: the same wire format and the same life cycle as fvp_server.py, for a
second estimator. ViTPose is a TOP-DOWN 2D method: a person detector (YOLOv8) finds each person in each camera view, ViTPose
reads the COCO-17 joints off a 256 x 192 crop of every box. It works on each view on its own: no 3D, no calibration.

Run it with the `vitpose` conda environment (mmpose 0.24 + mmcv-full + ultralytics); Unity starts it for you:

    <vitpose env>/python.exe vitpose_server.py --vitpose-dir <Models/ViTPose> --yolo <yolov8m.pt> --port 5578

Wire format: see fvp_server.py (uint32 'FVP1' | json_len | bin_len | json | bin, little-endian).

    client -> server   config   views + frame size            frame   V synced RGB views (top row first), "heat": 1 asks
                                                                       for the heatmaps of joint "heat_joint" (-1 = all)
                       ping / shutdown
    server -> client   hello, config_ok, pong, error
                       result   bin = float32 [persons, 57] then, if asked, uint8 [views, h, w]
                                one person = view, x1, y1, x2, y2, detector score, then 17 x (x, y, score) in pixels of the
                                sent frame (JSON: persons, stride, poses_bytes, t_det, t_pose, t_total)
                                the heatmaps are the network's own (64 x 48 per crop and joint), laid back over the sent frame
                                at 1/4 size (JSON: heat_views, heat_h, heat_w, heat_joint)
"""
import argparse
import os
import socket
import sys
import time
import traceback

import numpy as np

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from fvp_server import log, read_packet, send_packet  # noqa: E402  (the wire helpers; imports only numpy at module level)

PROTOCOL = 1
STRIDE = 57                      # view, x1, y1, x2, y2, det score, 17 * (x, y, score)
JOINTS = 17
COCO_NAMES = ['nose', 'left_eye', 'right_eye', 'left_ear', 'right_ear', 'left_shoulder', 'right_shoulder', 'left_elbow',
              'right_elbow', 'left_wrist', 'right_wrist', 'left_hip', 'right_hip', 'left_knee', 'right_knee', 'left_ankle',
              'right_ankle']


class ViTEngine:
    def __init__(self, args):
        self.args = args
        vit = os.path.abspath(args.vitpose_dir)
        if not os.path.isdir(vit):
            raise FileNotFoundError('ViTPose repo not found: %s' % vit)
        if vit not in sys.path:
            sys.path.insert(0, vit)

        import warnings
        warnings.filterwarnings('ignore')
        import torch
        import cv2
        self.torch, self.cv2 = torch, cv2
        from mmpose.apis import inference_top_down_pose_model, init_pose_model
        from mmpose.core.post_processing import get_affine_transform
        from mmpose.datasets import DatasetInfo
        self.infer_pose, self.get_affine_transform = inference_top_down_pose_model, get_affine_transform

        cfg = args.config or os.path.join(vit, 'configs/body/2d_kpt_sview_rgb_img/topdown_heatmap/coco/ViTPose_base_simple_coco_256x192.py')
        ckpt = args.checkpoint or os.path.join(vit, 'checkpoints/vitpose-b-simple.pth')
        for p in (cfg, ckpt, args.yolo):
            if not os.path.isfile(p):
                raise FileNotFoundError(p)

        device = args.device
        if device.startswith('cuda') and not torch.cuda.is_available():
            log('[vit] CUDA not available, falling back to CPU (expect seconds per frame)')
            device = 'cpu'
        self.device = device
        self.gpu_name = torch.cuda.get_device_name(torch.device(device)) if device.startswith('cuda') else 'cpu'

        log('[vit] loading the person detector (%s) ...' % os.path.basename(args.yolo))
        from ultralytics import YOLO
        self.yolo = YOLO(args.yolo)
        self.yolo.to(device)

        log('[vit] loading ViTPose (%s) ...' % os.path.basename(ckpt))
        self.pose_model = init_pose_model(cfg, ckpt, device=device)
        info = self.pose_model.cfg.data['test'].get('dataset_info', None)
        self.dataset_info = DatasetInfo(info) if info else None
        self.dataset_type = self.pose_model.cfg.data['test']['type']
        self.crop_w, self.crop_h = [int(v) for v in self.pose_model.cfg.data_cfg['image_size']]   # 192, 256
        self.heat_stride = 4

        self.view_count = 0
        self.frame_size = (0, 0)
        self.config_id = None
        self.stats = []
        self.last_heat = None
        self._warm()

    # one pass on a blank frame with a fake person, so the first real frame is not the slow one
    def _warm(self):
        blank = np.full((1, 540, 960, 3), 110, np.uint8)
        try:
            self.infer(blank, 0.3, None, warm_box=np.array([[380, 120, 560, 480, 0.9]], np.float32))
            self.stats = []
        except Exception as e:
            traceback.print_exc()
            log('[vit] warm-up failed: %s' % e)

    def configure(self, msg):
        self.view_count = int(msg['views'])
        self.frame_size = (int(msg['width']), int(msg['height']))
        self.config_id = msg.get('config_id')

    # ---------------------------------------------------------------- heatmaps laid over the frame
    def _heat_matrix(self, center, scale, ow, oh):
        fw, fh = self.frame_size
        trans = self.get_affine_transform(center, scale, 0, np.array([self.crop_w, self.crop_h]))   # frame px -> crop px
        s = float(self.heat_stride)
        heat_to_crop = np.array([[s, 0, (s - 1) / 2.0], [0, s, (s - 1) / 2.0], [0, 0, 1]])
        crop_to_frame = np.linalg.inv(np.vstack([trans, [0, 0, 1]]))
        fx, fy = fw / float(ow), fh / float(oh)
        frame_to_out = np.array([[1 / fx, 0, -(fx - 1) / (2 * fx)], [0, 1 / fy, -(fy - 1) / (2 * fy)], [0, 0, 1]])
        return (frame_to_out @ crop_to_frame @ heat_to_crop)[:2]

    @staticmethod
    def _box_to_cs(box_xywh, aspect):
        x, y, w, h = box_xywh[:4]
        center = np.array([x + w * 0.5, y + h * 0.5], dtype=np.float32)
        if w > aspect * h:
            h = w / aspect
        elif w < aspect * h:
            w = h * aspect
        return center, np.array([w / 200.0, h / 200.0], dtype=np.float32) * 1.25     # mmpose's _box2cs

    def _lay_heat(self, out, heatmaps, boxes_xyxy, joint):
        cv2 = self.cv2
        oh, ow = out.shape
        aspect = self.crop_w / float(self.crop_h)
        for k in range(len(boxes_xyxy)):
            x1, y1, x2, y2 = [float(v) for v in boxes_xyxy[k][:4]]
            center, scale = self._box_to_cs((x1, y1, x2 - x1, y2 - y1), aspect)
            hm = heatmaps[k]                                              # (17, 64, 48)
            hm = hm.max(axis=0) if joint < 0 else hm[min(int(joint), hm.shape[0] - 1)]
            hm = (np.clip(hm, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8)
            warped = cv2.warpAffine(hm, self._heat_matrix(center, scale, ow, oh), (ow, oh), flags=cv2.INTER_LINEAR,
                                    borderMode=cv2.BORDER_CONSTANT, borderValue=0)
            np.maximum(out, warped, out=out)

    # ---------------------------------------------------------------- inference
    def infer(self, views_rgb, det_thr, heat_joint, warm_box=None):
        cv2 = self.cv2
        t0 = time.perf_counter()
        bgr = [cv2.cvtColor(v, cv2.COLOR_RGB2BGR) for v in views_rgb]
        fw, fh = views_rgb.shape[2], views_rgb.shape[1]

        # 1. persons in every view (one batched call)
        if warm_box is not None:
            boxes = [warm_box for _ in bgr]
        else:
            res = self.yolo.predict(bgr, classes=[0], conf=float(det_thr), verbose=False, device=self.device)
            boxes = []
            for r in res:
                b = r.boxes
                if b is None or len(b) == 0:
                    boxes.append(np.zeros((0, 5), np.float32))
                else:
                    boxes.append(np.concatenate([b.xyxy.cpu().numpy(), b.conf.cpu().numpy()[:, None]], axis=1).astype(np.float32))
        t1 = time.perf_counter()

        # 2. ViTPose on the crops of every view
        rows = []
        ow, oh = max(1, fw // self.heat_stride), max(1, fh // self.heat_stride)
        heat = np.zeros((len(bgr), oh, ow), np.uint8) if heat_joint is not None else None
        for v, img in enumerate(bgr):
            if len(boxes[v]) == 0:
                continue
            persons = [{'bbox': b} for b in boxes[v]]
            results, outputs = self.infer_pose(self.pose_model, img, persons, bbox_thr=None, format='xyxy',
                                               dataset=self.dataset_type, dataset_info=self.dataset_info,
                                               return_heatmap=heat is not None)
            for pr in results:
                x1, y1, x2, y2, score = [float(a) for a in pr['bbox'][:5]]
                row = np.empty(STRIDE, np.float32)
                row[:6] = (v, x1, y1, x2, y2, score)
                row[6:] = np.asarray(pr['keypoints'], np.float32).reshape(-1)
                rows.append(row)
            if heat is not None and outputs:
                self._lay_heat(heat[v], outputs[0]['heatmap'], [r['bbox'] for r in results], heat_joint)
        t2 = time.perf_counter()

        self.last_heat = heat
        self.last_timing = ((t1 - t0) * 1000.0, (t2 - t1) * 1000.0)
        return np.stack(rows).astype('<f4') if rows else np.zeros((0, STRIDE), '<f4')

    STATS_EVERY = 50

    def record(self, det, pose, total):
        self.stats.append((det, pose, total))
        if len(self.stats) >= self.STATS_EVERY:
            self.flush_stats()

    def flush_stats(self):
        if len(self.stats) >= 5:
            a = np.array(self.stats)
            log('[vit] last %d frames: detector %.1f ms, ViTPose %.1f ms, total %.1f ms (p95 %.0f)' % (
                len(a), a[:, 0].mean(), a[:, 1].mean(), a[:, 2].mean(), np.percentile(a[:, 2], 95)))
        self.stats = []


# ----------------------------------------------------------------------------- server

def handle_client(conn, engine, args):
    conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    conn.settimeout(None)
    send_packet(conn, {'type': 'hello', 'protocol': PROTOCOL, 'model': 'ViTPose-B simple (COCO-17) + YOLOv8',
                       'device': engine.device, 'gpu': engine.gpu_name, 'keypoints': JOINTS})
    while True:
        msg, binary = read_packet(conn)
        kind = msg.get('type')
        try:
            if kind == 'config':
                t0 = time.perf_counter()
                engine.configure(msg)
                log('[vit] config %s: %d views %dx%d' % (msg.get('config_id'), engine.view_count, *engine.frame_size))
                send_packet(conn, {'type': 'config_ok', 'config_id': msg.get('config_id'), 'ms': (time.perf_counter() - t0) * 1000.0})
            elif kind == 'frame':
                if msg.get('config_id') != engine.config_id:
                    send_packet(conn, {'type': 'error', 'id': msg.get('id'), 'code': 'no_config', 'message': 'frame for config %s, server has %s' % (msg.get('config_id'), engine.config_id)})
                    continue
                v, h, w = int(msg['views']), int(msg['height']), int(msg['width'])
                if (v, w, h) != (engine.view_count, *engine.frame_size) or len(binary) != v * h * w * 3:
                    send_packet(conn, {'type': 'error', 'id': msg.get('id'), 'code': 'bad_frame', 'message': 'unexpected frame size'})
                    continue
                views = np.frombuffer(binary, np.uint8).reshape(v, h, w, 3)
                t0 = time.perf_counter()
                want_heat = bool(msg.get('heat'))
                rows = engine.infer(views, float(msg.get('det_thr', args.det_thr)), int(msg.get('heat_joint', -1)) if want_heat else None)
                total = (time.perf_counter() - t0) * 1000.0
                engine.record(engine.last_timing[0], engine.last_timing[1], total)
                reply = {'type': 'result', 'id': msg.get('id'), 'config_id': engine.config_id, 'persons': int(rows.shape[0]),
                         'stride': STRIDE, 'poses_bytes': int(rows.nbytes), 't_det': engine.last_timing[0],
                         't_pose': engine.last_timing[1], 't_total': total}
                payload = rows.tobytes()
                if engine.last_heat is not None:
                    hv, hh, hw = engine.last_heat.shape
                    reply.update({'heat_views': hv, 'heat_h': hh, 'heat_w': hw, 'heat_joint': int(msg.get('heat_joint', -1))})
                    payload += engine.last_heat.tobytes()
                send_packet(conn, reply, payload)
            elif kind == 'ping':
                send_packet(conn, {'type': 'pong'})
            elif kind == 'shutdown':
                log('[vit] shutdown requested')
                send_packet(conn, {'type': 'bye'})
                return True
            else:
                send_packet(conn, {'type': 'error', 'code': 'unknown_type', 'message': 'unknown type %r' % kind})
        except (ConnectionError, OSError):
            raise
        except Exception as e:  # keep serving: a bad frame must not take the models down
            traceback.print_exc()
            sys.stdout.flush()
            send_packet(conn, {'type': 'error', 'id': msg.get('id'), 'code': 'exception', 'message': '%s: %s' % (type(e).__name__, e)})


def serve(engine, args):
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((args.host, args.port))
    srv.listen(1)
    srv.settimeout(1.0)
    log('[vit] ready on %s:%d (%s, %s)' % (args.host, args.port, engine.gpu_name, engine.device))
    last_client = time.time()
    while True:
        try:
            conn, addr = srv.accept()
        except socket.timeout:
            if args.idle_exit > 0 and time.time() - last_client > args.idle_exit:
                log('[vit] no client for %d s, exiting' % args.idle_exit)
                return
            continue
        log('[vit] client connected %s:%d' % addr)
        try:
            if handle_client(conn, engine, args):
                return
        except (ConnectionError, OSError) as e:
            log('[vit] client gone (%s)' % e)
        finally:
            engine.flush_stats()
            conn.close()
            last_client = time.time()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--vitpose-dir', required=True, help='the ViTPose checkout (mmpose 0.24 version, with configs/ and checkpoints/)')
    ap.add_argument('--yolo', required=True, help='YOLOv8 person detector weights (yolov8m.pt)')
    ap.add_argument('--host', default='127.0.0.1')
    ap.add_argument('--port', type=int, default=5578)
    ap.add_argument('--device', default='cuda:0')
    ap.add_argument('--config', help='ViTPose mmpose config (default: ViTPose_base_simple_coco_256x192)')
    ap.add_argument('--checkpoint', help='ViTPose weights (default: <vitpose-dir>/checkpoints/vitpose-b-simple.pth)')
    ap.add_argument('--det-thr', type=float, default=0.4, help='person detector confidence threshold')
    ap.add_argument('--idle-exit', type=int, default=900, help='exit after this many seconds without a client (0 = never)')
    ap.add_argument('--cpu-priority', choices=['idle', 'below', 'normal', 'above', 'high'], default='normal',
                    help='Windows CPU priority class of this process (the other half of sharing the laptop with Unity)')
    ap.add_argument('--gpu-priority', choices=['idle', 'below', 'normal', 'above', 'high'], default='normal',
                    help='class the Windows GPU scheduler uses between processes (see proc_priority.py)')
    ap.add_argument('--log-file', help='append stdout + stderr to this file (Unity tails it into its Console)')
    ap.add_argument('--cache-dir', help='unused (kept so Unity can start both servers the same way)')
    args = ap.parse_args()

    if args.log_file:
        os.makedirs(os.path.dirname(os.path.abspath(args.log_file)), exist_ok=True)
        sys.stdout = sys.stderr = open(args.log_file, 'a', buffering=1, encoding='utf-8', errors='replace')
    else:
        for stream in (sys.stdout, sys.stderr):
            try:
                stream.reconfigure(encoding='utf-8', errors='replace')
            except Exception:
                pass

    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import proc_priority
    proc_priority.apply(args.cpu_priority, args.gpu_priority, log)

    t0 = time.time()
    engine = ViTEngine(args)
    log('[vit] models loaded and warm in %.1f s' % (time.time() - t0))
    serve(engine, args)


if __name__ == '__main__':
    main()
