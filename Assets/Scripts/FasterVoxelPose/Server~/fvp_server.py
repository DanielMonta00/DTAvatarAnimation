#!/usr/bin/env python
"""Faster-VoxelPose inference server for the Unity digital twin.

Unity (FasterVoxelPoseLive) renders the calibrated cameras, sends the synced frames over a
localhost TCP socket and gets the fused 3D poses back. The model code is the unmodified
Faster-VoxelPose repo (Ye et al., ECCV 2022); this file only wraps the inference path of
faster_voxelpose_multiviewsession.ipynb behind a socket.

Run it by hand (Unity launches it for you when "Auto Launch Server" is on):

    <env>/python.exe fvp_server.py --repo <Models/Faster-VoxelPose> --port 5577

Wire format, little-endian, one packet per message in both directions:

    uint32 magic 'FVP1' | uint32 json_len | uint32 bin_len | json (utf-8) | bin

    client -> server   config   cameras + capture volume (rebuilds the model head, ~1 s)
                       frame    one synced set of RGB views, top-left origin, bin = V*H*W*3 bytes
                       ping / shutdown
    server -> client   hello    sent on connect
                       config_ok, result (bin = float32 [max_people, 15, 5]), pong, error

A pose row is [x, y, z, valid, score] in the model frame: millimetres, Z up (Unity world
rotated by A = Rot_x(90), see the Unity component). valid >= 0 means a detected person.
"""
import argparse
import json
import os
import socket
import struct
import sys
import time
import traceback

import numpy as np

MAGIC = 0x31505646  # b'FVP1'
HEADER = struct.Struct('<III')
PROTOCOL = 1
SEQ = 'unity'  # the model caches its sampling grids per sequence name; one config = one sequence


# ----------------------------------------------------------------------------- wire

def recv_exact(sock, n):
    buf = bytearray(n)
    view = memoryview(buf)
    got = 0
    while got < n:
        k = sock.recv_into(view[got:], n - got)
        if k == 0:
            raise ConnectionError('peer closed')
        got += k
    return buf


def read_packet(sock):
    magic, jlen, blen = HEADER.unpack(recv_exact(sock, HEADER.size))
    if magic != MAGIC:
        raise ValueError('bad magic 0x%08x' % magic)
    msg = json.loads(bytes(recv_exact(sock, jlen)).decode('utf-8')) if jlen else {}
    binary = recv_exact(sock, blen) if blen else b''
    return msg, binary


def send_packet(sock, msg, binary=b''):
    j = json.dumps(msg).encode('utf-8')
    sock.sendall(HEADER.pack(MAGIC, len(j), len(binary)) + j)
    if binary:
        sock.sendall(binary)


def log(*a):
    print(*a, flush=True)


# ----------------------------------------------------------------------------- engine

class Engine:
    def __init__(self, args):
        self.args = args
        repo = os.path.abspath(args.repo)
        lib = os.path.join(repo, 'lib')
        for p in (repo, lib):
            if not os.path.isdir(p):
                raise FileNotFoundError('Faster-VoxelPose repo not found: %s' % p)
        if lib not in sys.path:
            sys.path.insert(0, lib)

        import torch
        import cv2
        self.torch, self.cv2 = torch, cv2
        import models  # noqa: F401  (registers resnet / faster_voxelpose / project_* submodules)
        from core.config import config, update_config
        from utils import cameras as cam_utils
        from utils.transforms import get_affine_transform, get_scale
        self.models, self.config, self.update_config = models, config, update_config
        self.cam_utils = cam_utils
        self.get_affine_transform, self.get_scale = get_affine_transform, get_scale

        cfg_file = args.config or os.path.join(repo, 'demo', 'config.yaml')
        self.backbone_file = args.backbone or os.path.join(repo, 'backbone', 'pose_resnet50_panoptic.pth.tar')
        self.model_file = args.model or os.path.join(repo, 'output', 'panoptic', 'jln64', 'model_best.pth.tar')
        for p in (cfg_file, self.backbone_file, self.model_file):
            if not os.path.isfile(p):
                raise FileNotFoundError(p)
        update_config(cfg_file)

        device = args.device
        if device.startswith('cuda') and not torch.cuda.is_available():
            log('[fvp] CUDA not available, falling back to CPU (expect seconds per frame)')
            device = 'cpu'
        config.DEVICE = device
        self.device = torch.device(device)
        torch.backends.cudnn.benchmark = bool(config.CUDNN.BENCHMARK)
        torch.backends.cudnn.deterministic = bool(config.CUDNN.DETERMINISTIC)
        torch.backends.cudnn.enabled = bool(config.CUDNN.ENABLED)

        log('[fvp] loading backbone ...')
        self.backbone = models.resnet.get(config)
        self.backbone.load_state_dict(torch.load(self.backbone_file, map_location='cpu'))
        self.backbone = self.backbone.to(self.device).eval()
        self.head_state = torch.load(self.model_file, map_location='cpu')

        if not args.no_mask:
            self._install_masked_projection()

        self.model = None
        self.config_id = None
        self.cameras = None
        self.resize_transform = None
        self.trans_np = None
        self.view_count = 0
        self.frame_size = (0, 0)
        self.mean = torch.tensor([0.485, 0.456, 0.406], device=self.device).view(1, 3, 1, 1)
        self.std = torch.tensor([0.229, 0.224, 0.225], device=self.device).view(1, 3, 1, 1)
        self.gpu_name = torch.cuda.get_device_name(self.device) if self.device.type == 'cuda' else 'cpu'

    # The repo projects voxel centres with the raw lens polynomial and no visibility test. In a lab
    # with cameras on every side that is wrong in two ways: voxels BEHIND a camera divide by a
    # negative depth and land mirrored inside the image, and with strong barrel distortion the
    # polynomial folds back past its valid radius (the same radius CalibratedCamera stops
    # rendering at). Both put false evidence into the cubes. Such voxels are sent far outside the
    # image so they sample the zero-padded border, like any other out-of-view voxel already does.
    def _install_masked_projection(self):
        torch = self.torch
        cu = self.cam_utils
        import models.project_whole as project_whole
        import models.project_individual as project_individual

        def project_pose_masked(x, camera):
            R, T, f, c, k, p = cu.unfold_camera_param(camera, x.device)
            out = cu.project_point(x, R, T, f, c, k, p)
            rmax = camera.get('max_radius')
            xcam = torch.mm(R, x.T - T)
            z = xcam[2]
            bad = z <= 1e-6
            if rmax is not None:
                y = xcam[:2] / (z + 1e-5)
                bad = bad | ((y ** 2).sum(0) > float(rmax) ** 2)
            return torch.where(bad.unsqueeze(1), torch.full_like(out, -1.0e4), out)

        project_whole.project_pose = project_pose_masked
        project_individual.project_pose = project_pose_masked

    # (Re)builds everything that depends on the cameras / capture volume / frame size. The head's
    # constructor bakes the voxel grids in, so a new volume needs a new head; the backbone is kept.
    def configure(self, msg):
        torch, config = self.torch, self.config
        cams = msg['cameras']
        w, h = int(msg['width']), int(msg['height'])
        config.DATASET.CAMERA_NUM = len(cams)
        config.DATASET.ORI_IMAGE_SIZE = np.array([w, h])
        config.CAPTURE_SPEC.SPACE_CENTER = np.array(msg['space_center'], dtype=float)
        config.CAPTURE_SPEC.SPACE_SIZE = np.array(msg['space_size'], dtype=float)
        config.CAPTURE_SPEC.VOXELS_PER_AXIS = np.array(msg.get('voxels_per_axis', [80, 80, 20]))
        config.CAPTURE_SPEC.MAX_PEOPLE = int(msg.get('max_people', 10))
        config.CAPTURE_SPEC.MIN_SCORE = float(msg.get('min_score', 0.1))

        cameras = []
        for c in cams:
            R = np.array(c['R'], dtype=float).reshape(3, 3)
            cameras.append({
                'R': R.tolist(),
                'T': np.array(c['T'], dtype=float).reshape(3, 1).tolist(),
                'fx': float(c['fx']), 'fy': float(c['fy']),
                'cx': float(c['cx']), 'cy': float(c['cy']),
                'k': np.array(c.get('k', [0, 0, 0]), dtype=float).reshape(3, 1).tolist(),
                'p': np.array(c.get('p', [0, 0]), dtype=float).reshape(2, 1).tolist(),
                'max_radius': c.get('max_radius'),
            })
        self.cameras = {SEQ: cameras}

        centre = np.array([w / 2.0, h / 2.0])
        scale = self.get_scale((w, h), config.DATASET.IMAGE_SIZE)
        self.trans_np = self.get_affine_transform(centre, scale, 0, config.DATASET.IMAGE_SIZE)
        self.resize_transform = torch.as_tensor(self.trans_np, dtype=torch.float, device=self.device)

        t0 = time.perf_counter()
        self.model = None
        if self.device.type == 'cuda':
            torch.cuda.empty_cache()
        model = self.models.faster_voxelpose.get(config)
        model.load_state_dict(self.head_state)
        self.model = model.to(self.device).eval()

        self.view_count = len(cams)
        self.frame_size = (w, h)
        self.config_id = msg.get('config_id')
        # One blank pass builds the per-camera sampling grids and lets cudnn pick its kernels,
        # so the first real frame is not the slow one.
        blank = np.zeros((self.view_count, h, w, 3), np.uint8)
        self.infer(blank, float(config.CAPTURE_SPEC.MIN_SCORE))
        return (time.perf_counter() - t0) * 1000.0

    def _preprocess(self, views):
        cv2, torch = self.cv2, self.torch
        net_w, net_h = int(self.config.DATASET.IMAGE_SIZE[0]), int(self.config.DATASET.IMAGE_SIZE[1])
        out = np.empty((len(views), net_h, net_w, 3), np.uint8)
        for i, v in enumerate(views):
            if self.args.preprocess == 'warp':
                # What the repo's preprocess.py bakes into the training images: aspect-preserving
                # fit into the network size, centred, zero padded. resize_transform assumes this.
                out[i] = cv2.warpAffine(v, self.trans_np, (net_w, net_h), flags=cv2.INTER_LINEAR)
            else:
                out[i] = cv2.resize(v, (net_w, net_h))  # what the notebooks do (stretches 16:9)
        t = torch.from_numpy(out).to(self.device).permute(0, 3, 1, 2).float().div_(255.0)
        t = (t - self.mean) / self.std
        return t.unsqueeze(0)  # (1, V, 3, H, W)

    def infer(self, views, min_score):
        torch = self.torch
        t0 = time.perf_counter()
        inputs = self._preprocess(views)
        self.model.pose_net.proposal_layer.min_score = float(min_score)
        if self.device.type == 'cuda':
            torch.cuda.synchronize()
        t1 = time.perf_counter()
        with torch.no_grad():
            fused, _, centers, _, _ = self.model(
                backbone=self.backbone, views=inputs, meta={'seq': [SEQ]},
                cameras=self.cameras, resize_transform=self.resize_transform)
            poses = fused[0].detach().cpu().numpy().astype('<f4')
        t2 = time.perf_counter()
        self.last_timing = ((t1 - t0) * 1000.0, (t2 - t1) * 1000.0)
        return poses


# ----------------------------------------------------------------------------- server

def handle_client(conn, engine, args):
    conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    conn.settimeout(None)
    send_packet(conn, {'type': 'hello', 'protocol': PROTOCOL, 'device': str(engine.device),
                       'gpu': engine.gpu_name, 'joints': 15,
                       'preprocess': args.preprocess, 'masked_projection': not args.no_mask})
    while True:
        msg, binary = read_packet(conn)
        kind = msg.get('type')
        try:
            if kind == 'config':
                ms = engine.configure(msg)
                log('[fvp] config %s: %d views %dx%d, volume centre %s size %s  (%.0f ms)' % (
                    msg.get('config_id'), engine.view_count, engine.frame_size[0], engine.frame_size[1],
                    np.round(engine.config.CAPTURE_SPEC.SPACE_CENTER).tolist(),
                    engine.config.CAPTURE_SPEC.SPACE_SIZE.tolist(), ms))
                send_packet(conn, {'type': 'config_ok', 'config_id': msg.get('config_id'), 'ms': ms})
            elif kind == 'frame':
                if engine.model is None or msg.get('config_id') != engine.config_id:
                    send_packet(conn, {'type': 'error', 'id': msg.get('id'), 'code': 'no_config',
                                       'message': 'frame for config %s, server has %s' % (msg.get('config_id'), engine.config_id)})
                    continue
                v, h, w = int(msg['views']), int(msg['height']), int(msg['width'])
                if (v, w, h) != (engine.view_count, *engine.frame_size) or len(binary) != v * h * w * 3:
                    send_packet(conn, {'type': 'error', 'id': msg.get('id'), 'code': 'bad_frame',
                                       'message': 'expected %d views %dx%d (%d bytes), got %d views %dx%d (%d bytes)' % (
                                           engine.view_count, engine.frame_size[0], engine.frame_size[1],
                                           engine.view_count * engine.frame_size[0] * engine.frame_size[1] * 3,
                                           v, w, h, len(binary))})
                    continue
                views = np.frombuffer(binary, np.uint8).reshape(v, h, w, 3)
                t0 = time.perf_counter()
                poses = engine.infer(views, float(msg.get('min_score', 0.1)))
                total = (time.perf_counter() - t0) * 1000.0
                send_packet(conn, {'type': 'result', 'id': msg.get('id'), 'config_id': engine.config_id,
                                   'people': int(poses.shape[0]), 'joints': int(poses.shape[1]),
                                   't_pre': engine.last_timing[0], 't_net': engine.last_timing[1],
                                   't_total': total}, poses.tobytes())
            elif kind == 'ping':
                send_packet(conn, {'type': 'pong'})
            elif kind == 'shutdown':
                log('[fvp] shutdown requested')
                send_packet(conn, {'type': 'bye'})
                return True
            else:
                send_packet(conn, {'type': 'error', 'code': 'unknown_type', 'message': 'unknown type %r' % kind})
        except (ConnectionError, OSError):
            raise
        except Exception as e:  # keep serving: a bad frame must not take the model down
            traceback.print_exc()
            sys.stdout.flush()
            send_packet(conn, {'type': 'error', 'id': msg.get('id'), 'code': 'exception',
                               'message': '%s: %s' % (type(e).__name__, e)})


def serve(engine, args):
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((args.host, args.port))
    srv.listen(1)
    srv.settimeout(1.0)
    log('[fvp] ready on %s:%d (%s, %s)' % (args.host, args.port, engine.gpu_name, engine.device))
    last_client = time.time()
    while True:
        try:
            conn, addr = srv.accept()
        except socket.timeout:
            if args.idle_exit > 0 and time.time() - last_client > args.idle_exit:
                log('[fvp] no client for %d s, exiting' % args.idle_exit)
                return
            continue
        log('[fvp] client connected %s:%d' % addr)
        try:
            if handle_client(conn, engine, args):
                return
        except (ConnectionError, OSError) as e:
            log('[fvp] client gone (%s)' % e)
        finally:
            conn.close()
            last_client = time.time()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--repo', required=True, help='Faster-VoxelPose repo (contains lib/, demo/, backbone/, output/)')
    ap.add_argument('--host', default='127.0.0.1')
    ap.add_argument('--port', type=int, default=5577)
    ap.add_argument('--device', default='cuda:0')
    ap.add_argument('--config', help='YAML config (default: <repo>/demo/config.yaml)')
    ap.add_argument('--backbone', help='2D backbone checkpoint (default: <repo>/backbone/pose_resnet50_panoptic.pth.tar)')
    ap.add_argument('--model', help='head checkpoint (default: <repo>/output/panoptic/jln64/model_best.pth.tar)')
    ap.add_argument('--preprocess', choices=['warp', 'stretch'], default='warp',
                    help='warp: aspect-preserving affine like the training data; stretch: plain resize like the notebooks')
    ap.add_argument('--no-mask', action='store_true',
                    help='project voxels exactly like the original repo (no behind-camera / lens-validity mask)')
    ap.add_argument('--idle-exit', type=int, default=900,
                    help='exit after this many seconds without a client (0 = never)')
    ap.add_argument('--log-file', help='append stdout + stderr to this file (Unity tails it into its Console)')
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

    t0 = time.time()
    engine = Engine(args)
    log('[fvp] models loaded in %.1f s' % (time.time() - t0))
    serve(engine, args)


if __name__ == '__main__':
    main()
