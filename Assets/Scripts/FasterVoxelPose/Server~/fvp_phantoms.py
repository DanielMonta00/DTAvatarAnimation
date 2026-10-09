#!/usr/bin/env python
"""What tells a phantom (a skeleton where nobody is) from a real person, measured on a recorded session.

Replays frames of a MultiViewSession through fvp_server.py, labels every skeleton the network returns as real (a ground-truth person
within 1 m mean joint distance, nearest first) or phantom (none left), and saves what the server knows about each one: its score, how
strongly every view's own 2D heatmaps back its joints ("support", see Engine.person_support), its joints. Then fvp_phantom_rules.py
tries rules on that file.

    python fvp_phantoms.py --spawn --views 1,2,3 --frames 0:2000:2 --out phantoms_3views.npz
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
from fvp_selftest import JOINT_MAP, camera_message, parse_frames, to_zup_mm, wait_port  # noqa: E402
from fvp_server import read_packet, send_packet  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--repo', default=r'C:/Users/vdmontanacuellar/Documents/Daniel/AProjectPTZCameras/Models/Faster-VoxelPose')
    ap.add_argument('--dataset', default=r'C:/Users/vdmontanacuellar/Documents/Daniel/AProjectPTZCameras/Datasets/MultiViewSession_20260430_172237')
    ap.add_argument('--port', type=int, default=5597)
    ap.add_argument('--frames', default='0:2000:2')
    ap.add_argument('--views', default='1,2,3', help='1-based camera indices (the live scene has 3 cameras)')
    ap.add_argument('--spawn', action='store_true')
    ap.add_argument('--server-args', default='--fp16')
    ap.add_argument('--min-score', type=float, default=0.1)
    ap.add_argument('--out', required=True)
    args = ap.parse_args()

    proc = None
    if args.spawn:
        here = os.path.dirname(os.path.abspath(__file__))
        cmd = [sys.executable, os.path.join(here, 'fvp_server.py'), '--repo', args.repo, '--port', str(args.port)] + args.server_args.split()
        proc = subprocess.Popen(cmd)
        if not wait_port('127.0.0.1', args.port, 180):
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
            proc.wait(timeout=20) if proc.poll() is None else None


def run(args):
    ds = args.dataset
    cams_all = json.load(open(os.path.join(ds, 'cameras.json')))['cameras']
    kt = json.load(open(os.path.join(ds, 'keypoints_transforms.json')))
    frames = kt['frames']
    views = [int(v) - 1 for v in args.views.split(',')]
    cams = [cams_all[v] for v in views]
    w, h = cams[0]['image_size']

    allW = np.array([kp['position_in_world'] for f in frames for pr in f['persons'] for kp in pr['keypoints']], float)
    P = to_zup_mm(allW)
    lo, hi = P.min(0), P.max(0)
    centre = (lo + hi) / 2.0
    centre[2] = hi[2] / 2.0
    centre = np.round(centre)

    gt_idx = {n: i for i, n in enumerate(kt['keypoint_names'])}
    pred_sel = [p for p, _ in JOINT_MAP]
    gt_sel = [gt_idx[n] for _, n in JOINT_MAP]

    sock = socket.create_connection(('127.0.0.1', args.port), 10)
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    read_packet(sock)
    cfg = {'type': 'config', 'config_id': 1, 'width': w, 'height': h, 'min_score': args.min_score,
           'space_center': centre.tolist(), 'space_size': [8000.0, 8000.0, 2000.0],
           'voxels_per_axis': [80, 80, 20], 'max_people': 10, 'cameras': [camera_message(c) for c in cams]}
    send_packet(sock, cfg)
    reply, _ = read_packet(sock)
    assert reply['type'] == 'config_ok', reply

    rec = {k: [] for k in ('frame', 'score', 'support', 'joints', 'real', 'err', 'dist', 'ngt', 'rows_in_frame')}
    misses = 0          # ground-truth people nobody was found for
    n_gt = 0
    times = []
    for fi in parse_frames(args.frames, len(frames)):
        fr = frames[fi]
        imgs = []
        for c in cams:
            bgr = cv2.imread(os.path.join(ds, fr['rgb_paths'][c['id']]), cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
            imgs.append(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))
        send_packet(sock, {'type': 'frame', 'id': fi, 'config_id': 1, 'views': len(imgs), 'width': w, 'height': h, 'min_score': args.min_score},
                    np.stack(imgs).tobytes())
        res, binary = read_packet(sock)
        if res['type'] != 'result':
            print('frame', fi, '->', res)
            continue
        times.append((res['t_net'], res['t_total']))
        poses = np.frombuffer(binary[:res.get('poses_bytes', len(binary))], '<f4').reshape(res['people'], res['joints'], 5)
        support = {s[0]: s[1:] for s in res.get('support', [])}
        rows = np.nonzero(poses[:, 0, 3] >= 0)[0]
        people = poses[rows]
        gt = np.array([to_zup_mm([kp['position_in_world'] for kp in pr['keypoints']])[gt_sel] for pr in fr['persons']])
        n_gt += len(gt)
        used_p, used_g, err = {}, set(), {}
        if len(people) and len(gt):
            pj = people[:, pred_sel, :3]
            cost = np.linalg.norm(pj[:, None] - gt[None], axis=3).mean(-1)
            for c_, p, g in sorted((cost[p, g], p, g) for p in range(len(pj)) for g in range(len(gt))):
                if p in used_p or g in used_g or c_ > 1000.0:
                    continue
                used_p[p] = g
                used_g.add(g)
                err[p] = c_
        misses += len(gt) - len(used_g)
        for i, row in enumerate(rows):
            d = min((np.linalg.norm(people[i, 0, :2] - g[0, :2]) for g in gt), default=float('inf'))
            rec['frame'].append(fi)
            rec['score'].append(float(people[i, :, 4].mean()))
            rec['support'].append(np.array(support.get(int(row), [-1.0] * len(views)), np.float32))
            rec['joints'].append(people[i, :, :3])
            rec['real'].append(i in used_p)
            rec['err'].append(err.get(i, np.nan))
            rec['dist'].append(d)
            rec['ngt'].append(len(gt))
            rec['rows_in_frame'].append(len(rows))
    out = {k: np.array(v) for k, v in rec.items()}
    out['misses'] = np.array(misses)
    out['n_gt'] = np.array(n_gt)
    np.savez(args.out, **out)
    t = np.array(times)
    real = out['real'].astype(bool)
    print('%d frames, %d skeletons: %d real, %d phantom; ground-truth people with nobody found: %d of %d' % (
        len(times), len(real), real.sum(), (~real).sum(), misses, n_gt))
    print('server: net %.1f ms, total %.1f ms (mean over %d frames)' % (t[:, 0].mean(), t[:, 1].mean(), len(t)))
    sup = out['support']
    for name, m in (('real', real), ('phantom', ~real)):
        if m.sum() == 0:
            continue
        s = sup[m]
        print('%-8s score median %.3f | support per view median %s | min over views median %.3f' % (
            name, np.median(out['score'][m]), np.round(np.nanmedian(np.where(s >= 0, s, np.nan), axis=0), 3).tolist(),
            np.median(np.where(s >= 0, s, np.nan).min(axis=1)) if (s >= 0).all(axis=1).any() else float('nan')))
    sock.close()


if __name__ == '__main__':
    main()
