#!/usr/bin/env python
"""Protocol + accuracy check of fvp_server.py without Unity.

Replays frames of a recorded MultiViewSession (cameras.json + keypoints_transforms.json) through the
same wire format the Unity component uses, and scores the answer against the session's ground truth
(absolute MPJPE over the 13 joints Panoptic-15 and the AIC-14 GT share, like the experiment notebook).

    python fvp_selftest.py --repo <Models/Faster-VoxelPose> --dataset <Datasets/MultiViewSession_...> --spawn
    python fvp_selftest.py --frames 0:600:50 --port 5577          # against a server that is already running
"""
import argparse
import json
import os
import socket
import struct
import subprocess
import sys
import time

import cv2
import numpy as np

sys.dont_write_bytecode = True  # importing fvp_server must not leave a __pycache__ in the Unity project
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fvp_server import HEADER, MAGIC, read_packet, send_packet  # noqa: E402

S = np.diag([1.0, -1.0, -1.0, 1.0])                          # OpenGL camera -> OpenCV camera
A = np.array([[1, 0, 0], [0, 0, -1], [0, 1, 0]], float)      # Rot_x(90): Unity Y-up -> model Z-up

# (index into the Panoptic-15 prediction, GT joint name)
JOINT_MAP = [(0, 'neck'), (3, 'left_shoulder'), (4, 'left_elbow'), (5, 'left_wrist'), (6, 'left_hip'),
             (7, 'left_knee'), (8, 'left_ankle'), (9, 'right_shoulder'), (10, 'right_elbow'),
             (11, 'right_wrist'), (12, 'right_hip'), (13, 'right_knee'), (14, 'right_ankle')]


def to_zup_mm(x_m):
    return (A @ (np.asarray(x_m, float).T * 1000.0)).T


def camera_message(c):
    """The 'cameras' entry the Unity client sends for one camera, from a dataset world_to_camera matrix."""
    E = S @ np.array(c['world_to_camera'], float)
    E[:3, 3] *= 1000.0
    R = E[:3, :3]
    T = -R.T @ E[:3, 3]
    R, T = R @ A.T, A @ T
    return {'R': R.reshape(-1).tolist(), 'T': T.tolist(), 'fx': c['fx'], 'fy': c['fy'], 'cx': c['cx'], 'cy': c['cy'],
            'k': [0, 0, 0], 'p': [0, 0]}


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
    ap.add_argument('--repo', default=r'C:/Users/vdmontanacuellar/Documents/Daniel/AProjectPTZCameras/Models/Faster-VoxelPose')
    ap.add_argument('--dataset', default=r'C:/Users/vdmontanacuellar/Documents/Daniel/AProjectPTZCameras/Datasets/MultiViewSession_20260430_172237')
    ap.add_argument('--port', type=int, default=5577)
    ap.add_argument('--frames', default='120', help='"120", "0,50,100" or "start:stop:step"')
    ap.add_argument('--spawn', action='store_true', help='start fvp_server.py for the run, stop it afterwards')
    ap.add_argument('--server-args', default='', help='extra arguments for the spawned server, e.g. "--preprocess stretch --no-mask"')
    ap.add_argument('--views', default='1,2,3,4', help='1-based camera indices to send')
    ap.add_argument('--notebook-poses', default=r'C:/Users/vdmontanacuellar/Documents/Daniel/AProjectPTZCameras/DanielExperiments/Estimation/Faster-VoxelPose/output_multiview/fused_poses_frame120.npy')
    args = ap.parse_args()

    proc = None
    if args.spawn:
        here = os.path.dirname(os.path.abspath(__file__))
        cmd = [sys.executable, os.path.join(here, 'fvp_server.py'), '--repo', args.repo, '--port', str(args.port)] + args.server_args.split()
        print('spawning:', ' '.join(cmd))
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
    cams = json.load(open(os.path.join(ds, 'cameras.json')))['cameras']
    kt = json.load(open(os.path.join(ds, 'keypoints_transforms.json')))
    frames = kt['frames']
    views = [int(v) - 1 for v in args.views.split(',')]
    cams = [cams[v] for v in views]
    w, h = cams[0]['image_size']

    # Capture volume exactly as the notebook derives it from the GT extent.
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
    hello, _ = read_packet(sock)
    print('hello:', hello)

    cfg = {'type': 'config', 'config_id': 1, 'width': w, 'height': h, 'min_score': 0.1,
           'space_center': centre.tolist(), 'space_size': [8000.0, 8000.0, 2000.0],
           'voxels_per_axis': [80, 80, 20], 'max_people': 10,
           'cameras': [camera_message(c) for c in cams]}
    t0 = time.time()
    send_packet(sock, cfg)
    reply, _ = read_packet(sock)
    print('config reply:', reply, '(%.1f s round trip)' % (time.time() - t0))
    assert reply['type'] == 'config_ok', reply

    all_abs, times, signed = [], [], []
    matched_scores, ghosts = [], []
    first = None
    for fi in parse_frames(args.frames, len(frames)):
        fr = frames[fi]
        imgs = []
        for c in cams:
            bgr = cv2.imread(os.path.join(ds, fr['rgb_paths'][c['id']]), cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
            imgs.append(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))
        payload = np.stack(imgs).tobytes()
        t0 = time.time()
        send_packet(sock, {'type': 'frame', 'id': fi, 'config_id': 1, 'views': len(imgs), 'width': w, 'height': h,
                           'min_score': 0.1}, payload)
        res, binary = read_packet(sock)
        rt = (time.time() - t0) * 1000
        if res['type'] != 'result':
            print('frame', fi, '->', res)
            continue
        poses = np.frombuffer(binary, '<f4').reshape(res['people'], res['joints'], 5)
        if first is None and fi == 120:
            first = poses
        people = poses[poses[:, 0, 3] >= 0]
        gt = np.array([to_zup_mm([kp['position_in_world'] for kp in pr['keypoints']])[gt_sel] for pr in fr['persons']])
        errs = []
        used_p, used_g = set(), set()
        if len(people) and len(gt):
            pj = people[:, pred_sel, :3]
            cost = np.linalg.norm(pj[:, None] - gt[None], axis=3).mean(-1)
            for _, p, g in sorted((cost[p, g], p, g) for p in range(len(pj)) for g in range(len(gt))):
                if p in used_p or g in used_g:
                    continue
                used_p.add(p); used_g.add(g)
                errs.append(np.linalg.norm(pj[p] - gt[g], axis=1))
                signed.append(pj[p] - gt[g])
        # Who is real and who is a ghost: every estimated person that got no ground-truth partner, with its score and
        # how far (horizontally, mm) its neck is from the nearest real person's neck.
        for p in range(len(people)):
            score = float(people[p, :, 4].mean())
            if p in used_p:
                matched_scores.append(score)
            else:
                d = min((np.linalg.norm(people[p, 0, :2] - g[0, :2]) for g in gt), default=float('inf'))
                ghosts.append((fi, score, d, float(people[p, 0, 2])))
        if errs:
            all_abs.append(np.concatenate(errs))
        times.append(rt)
        print('frame %4d: %d person(s) (GT %d)  MPJPE %s  net %.0f ms  total %.0f ms  roundtrip %.0f ms' % (
            fi, len(people), len(gt), ('%.1f mm' % np.concatenate(errs).mean()) if errs else 'n/a',
            res['t_net'], res['t_total'], rt))

    if all_abs:
        a = np.concatenate(all_abs)
        print('\nMPJPE absolute over %d joints: %.1f mm (median %.1f)  PCK@150 %.1f %%   mean round trip %.0f ms' % (
            len(a), a.mean(), np.median(a), (a < 150).mean() * 100, np.mean(times)))
    if matched_scores or ghosts:
        ms = np.array(matched_scores) if matched_scores else np.zeros(0)
        gs = np.array([g[1] for g in ghosts]) if ghosts else np.zeros(0)
        n_frames = len(times)
        print('\nestimated people with a ground-truth partner: %d, without (ghosts): %d over %d frames (%.0f %% of frames have one)' % (
            len(ms), len(gs), n_frames, 100.0 * len({g[0] for g in ghosts}) / max(1, n_frames)))
        if len(ms): print('  score of real people   min %.3f  median %.3f  max %.3f' % (ms.min(), np.median(ms), ms.max()))
        if len(gs):
            print('  score of ghosts        min %.3f  median %.3f  max %.3f' % (gs.min(), np.median(gs), gs.max()))
            print('  ghosts: distance of the neck to the nearest real person (m): ' + ' '.join('%.1f' % (g[2] / 1000.0) for g in ghosts[:40]))
            print('  ghosts: neck height (m): ' + ' '.join('%.2f' % (g[3] / 1000.0) for g in ghosts[:40]))
            for thr in (0.1, 0.15, 0.2, 0.3, 0.4):
                print('  min score %.2f keeps %3d %% of real people and %3d %% of ghosts' % (
                    thr, 100 * (ms >= thr).mean() if len(ms) else 0, 100 * (gs >= thr).mean()))
    if signed:
        # Is the error a constant offset (a calibration / definition problem) or scatter (the network)?
        # signed = estimate - GT in the model frame (mm, Z up), one row per matched person: (joint, xyz).
        s = np.stack(signed)                                     # (matches, 13, 3)
        bias = s.mean(0)                                         # per joint, the mean offset
        scatter = np.linalg.norm(s - bias, axis=2).mean(0)       # per joint, what is left once the offset is removed
        print('\nsigned error estimate - GT (mm, model frame x, y, z up) over %d matches:' % len(s))
        print('  %-15s %8s %8s %8s | %6s %8s' % ('joint', 'dx', 'dy', 'dz', '|bias|', 'scatter'))
        for k, (_, name) in enumerate(JOINT_MAP):
            print('  %-15s %8.1f %8.1f %8.1f | %6.1f %8.1f' % (name, *bias[k], np.linalg.norm(bias[k]), scatter[k]))
        mean_bias = bias.mean(0)
        print('  all joints: mean offset (%.1f, %.1f, %.1f) mm = %.1f mm; mean per-joint |bias| %.1f mm, scatter %.1f mm' % (
            *mean_bias, np.linalg.norm(mean_bias), np.linalg.norm(bias, axis=1).mean(), scatter.mean()))
    if first is not None and os.path.isfile(args.notebook_poses) and len(views) == 4:
        nb = np.load(args.notebook_poses)
        mine = first
        nb_valid, my_valid = nb[nb[:, 0, 3] >= 0], mine[mine[:, 0, 3] >= 0]
        print('\nframe 120 vs the notebook output: %d vs %d people' % (len(my_valid), len(nb_valid)))
        if len(nb_valid) and len(my_valid):
            d = np.linalg.norm(my_valid[:, None, :, :3] - nb_valid[None, :, :, :3], axis=3).mean(-1)
            print('  mean joint distance of the closest pairs (mm):', np.round(d.min(0), 1).tolist())
    sock.close()


if __name__ == '__main__':
    main()
