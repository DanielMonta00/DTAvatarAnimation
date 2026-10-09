#!/usr/bin/env python
"""Protocol + accuracy check of vitpose_server.py without Unity: replays a recorded MultiViewSession through the wire format and
scores ViTPose's 2D joints against the session's true 2D positions, per joint, and checks that its heatmaps lie where they should.

    <vitpose env>/python.exe vitpose_selftest.py --spawn [--frames 0:600:20] [--scale 0.75] [--heat-joint 15]
"""
import argparse
import json
import os
import socket
import subprocess
import sys
import time

import cv2
import numpy as np

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fvp_server import read_packet, send_packet  # noqa: E402

AIA = r'C:/Users/vdmontanacuellar/Documents/Daniel/AProjectPTZCameras'
COCO_NAMES = ['nose', 'left_eye', 'right_eye', 'left_ear', 'right_ear', 'left_shoulder', 'right_shoulder', 'left_elbow',
              'right_elbow', 'left_wrist', 'right_wrist', 'left_hip', 'right_hip', 'left_knee', 'right_knee', 'left_ankle',
              'right_ankle']
# the COCO joints the recorded ground truth also has (same names)
SHARED = [n for n in COCO_NAMES if n in ('left_shoulder', 'right_shoulder', 'left_elbow', 'right_elbow', 'left_wrist', 'right_wrist',
                                         'left_hip', 'right_hip', 'left_knee', 'right_knee', 'left_ankle', 'right_ankle')]
STRIDE = 57


def parse_frames(spec, n):
    if ':' in spec:
        a, b, s = (int(x) for x in spec.split(':'))
        return list(range(a, min(b, n), s))
    return [int(x) for x in spec.split(',')]


def wait_port(host, port, timeout):
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            socket.create_connection((host, port), 0.5).close()
            return True
        except OSError:
            time.sleep(0.5)
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--vitpose-dir', default=AIA + '/Models/ViTPose')
    ap.add_argument('--yolo', default=AIA + '/yolov8m.pt')
    ap.add_argument('--dataset', default=AIA + '/Datasets/MultiViewSession_20260430_172237')
    ap.add_argument('--port', type=int, default=5599)
    ap.add_argument('--frames', default='0:600:20')
    ap.add_argument('--scale', type=float, default=0.75, help='send the frames at this fraction of their size (Unity sends 960x540 of 1280x720)')
    ap.add_argument('--views', default='1,2,3,4')
    ap.add_argument('--spawn', action='store_true')
    ap.add_argument('--server-args', default='')
    ap.add_argument('--heat-joint', type=int, default=15, help='COCO joint whose heatmap is checked (15 = left ankle, 5 = left shoulder, -1 = all, not checked)')
    args = ap.parse_args()

    proc = None
    if args.spawn:
        cmd = [sys.executable, os.path.join(os.path.dirname(os.path.abspath(__file__)), 'vitpose_server.py'), '--vitpose-dir', args.vitpose_dir,
               '--yolo', args.yolo, '--port', str(args.port)] + args.server_args.split()
        print('spawning:', ' '.join(cmd))
        proc = subprocess.Popen(cmd)
        if not wait_port('127.0.0.1', args.port, 240):
            proc.kill()
            sys.exit('server did not come up')
    try:
        run(args)
    finally:
        if proc is not None:
            try:
                with socket.create_connection(('127.0.0.1', args.port), 2) as s:
                    read_packet(s)
                    send_packet(s, {'type': 'shutdown'})
            except OSError:
                pass
            proc.wait(timeout=30) if proc.poll() is None else None


def run(args):
    ds = args.dataset
    cams = json.load(open(os.path.join(ds, 'cameras.json')))['cameras']
    kt = json.load(open(os.path.join(ds, 'keypoints_transforms.json')))
    frames = kt['frames']
    views = [int(v) - 1 for v in args.views.split(',')]
    cams = [cams[v] for v in views]
    W0, H0 = cams[0]['image_size']
    w, h = int(round(W0 * args.scale)), int(round(H0 * args.scale))
    gt_idx = {n: i for i, n in enumerate(kt['keypoint_names'])}

    sock = socket.create_connection(('127.0.0.1', args.port), 10)
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    hello, _ = read_packet(sock)
    print('hello:', hello)
    send_packet(sock, {'type': 'config', 'config_id': 1, 'views': len(cams), 'width': w, 'height': h})
    reply, _ = read_packet(sock)
    assert reply['type'] == 'config_ok', reply

    errs = {n: [] for n in SHARED}
    heat_errs, times, persons_found, persons_true = [], [], 0, 0
    det_ms, pose_ms = [], []
    for fi in parse_frames(args.frames, len(frames)):
        fr = frames[fi]
        imgs = []
        for c in cams:
            bgr = cv2.imread(os.path.join(ds, fr['rgb_paths'][c['id']]), cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
            rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
            if (w, h) != (W0, H0):
                rgb = cv2.resize(rgb, (w, h), interpolation=cv2.INTER_AREA)
            imgs.append(rgb)
        frame_msg = {'type': 'frame', 'id': fi, 'config_id': 1, 'views': len(imgs), 'width': w, 'height': h, 'heat': 1, 'heat_joint': args.heat_joint}
        t0 = time.time()
        send_packet(sock, frame_msg, np.stack(imgs).tobytes())
        res, binary = read_packet(sock)
        times.append((time.time() - t0) * 1000)
        if res['type'] != 'result':
            print('frame', fi, '->', res)
            continue
        det_ms.append(res['t_det']); pose_ms.append(res['t_pose'])
        pb = res['poses_bytes']
        rows = np.frombuffer(binary[:pb], '<f4').reshape(res['persons'], res['stride'])
        heat = np.frombuffer(binary[pb:], np.uint8).reshape(res['heat_views'], res['heat_h'], res['heat_w']) if 'heat_views' in res else None

        for vi, c in enumerate(cams):
            truth = {}   # person -> {joint: (x, y)}
            for pi, pr in enumerate(fr['persons']):
                d = {}
                for n in SHARED:
                    pc = pr['keypoints'][gt_idx[n]]['per_camera'][c['index'] - 1]
                    if pc['visible']:
                        d[n] = np.array([pc['image_position'][0], H0 - pc['image_position'][1]]) * args.scale   # bottom-left origin
                if len(d) >= 6:
                    truth[pi] = d
            persons_true += len(truth)
            mine = rows[rows[:, 0] == vi]
            # greedy match: the detected skeleton closest to each true person
            cost = []
            for ri, r in enumerate(mine):
                kp = r[6:].reshape(17, 3)
                for pi, d in truth.items():
                    dist = np.mean([np.linalg.norm(kp[COCO_NAMES.index(n), :2] - p) for n, p in d.items()])
                    cost.append((dist, ri, pi))
            used_r, used_p = set(), set()
            for dist, ri, pi in sorted(cost):
                if ri in used_r or pi in used_p or dist > 150 * args.scale:
                    continue
                used_r.add(ri); used_p.add(pi)
                persons_found += 1
                kp = mine[ri][6:].reshape(17, 3)
                for n, p in truth[pi].items():
                    errs[n].append(np.linalg.norm(kp[COCO_NAMES.index(n), :2] - p) / args.scale)    # in pixels of the original image
            if heat is not None and args.heat_joint >= 0 and COCO_NAMES[args.heat_joint] in SHARED:
                y, x = np.unravel_index(np.argmax(heat[vi]), heat[vi].shape)
                if heat[vi][y, x] >= 20:
                    fx, fy = w / heat.shape[2], h / heat.shape[1]
                    peak = np.array([x * fx + (fx - 1) / 2.0, y * fy + (fy - 1) / 2.0])
                    ds_ = [np.linalg.norm(peak - d[COCO_NAMES[args.heat_joint]]) / args.scale for d in truth.values() if COCO_NAMES[args.heat_joint] in d]
                    if ds_:
                        heat_errs.append(min(ds_))

    print('\nViTPose-B + YOLOv8 on the recorded session, frames sent at %dx%d (the dataset is %dx%d), %d views' % (w, h, W0, H0, len(cams)))
    print('people found %d of %d (matched within 150 px)' % (persons_found, persons_true))
    print('%-15s %8s %8s %8s  (2D error in pixels of the original %dx%d image)' % ('joint', 'median', 'p90', '<20px', W0, H0))
    for n in SHARED:
        e = np.array(errs[n])
        if len(e):
            print('%-15s %8.1f %8.1f %7.0f%%' % (n, np.median(e), np.percentile(e, 90), 100 * (e < 20).mean()))
    allv = np.concatenate([np.array(v) for v in errs.values() if len(v)])
    print('%-15s %8.1f %8.1f %7.0f%%' % ('all', np.median(allv), np.percentile(allv, 90), 100 * (allv < 20).mean()))
    if heat_errs:
        e = np.array(heat_errs)
        print('\nheatmap of %s laid over the frame: strongest peak vs the true 2D joint: median %.1f px, p90 %.1f px, %.0f %% within 20 px (%d view-frames)' % (
            COCO_NAMES[args.heat_joint], np.median(e), np.percentile(e, 90), 100 * (e < 20).mean(), len(e)))
    print('\nspeed: round trip %.0f ms (detector %.0f + ViTPose %.0f ms on the server) per synced set of %d views' % (np.mean(times), np.mean(det_ms), np.mean(pose_ms), len(cams)))
    sock.close()


if __name__ == '__main__':
    main()
