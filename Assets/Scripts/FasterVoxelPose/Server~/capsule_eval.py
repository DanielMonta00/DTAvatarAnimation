#!/usr/bin/env python
"""How well does Faster-VoxelPose's torso position match the real person? (what PoseEstimateFollower moves a capsule with)

Replays a recorded MultiViewSession through fvp_server.py exactly like the Unity client does (same wire format, same phantom rule), and for
every estimated person that matches a ground-truth person measures the point the capsule follows: the middle of neck, shoulders and hips.

  * error     estimate - truth of that point, horizontally (what decides whether the operator is inside the capsule) and vertically;
  * jitter    how much that error changes from one estimate to the next (the part a filter can average away; the rest is a slowly
              changing offset that no filter can);
  * recall    how many ground-truth people had an estimate, and how many estimates had nobody behind them (ghosts), before and after the
              server's 2D-support phantom rule;
  * timing    what the server needed per frame (the network alone, not Unity).

    python capsule_eval.py --spawn --port 5591 --views 1,2,3 --frames 0:608:2
    python capsule_eval.py --port 5577 --views 1,2,3,4 --csv errors.csv        # against a server that is already running

--csv writes one row per ground-truth person and frame (time, truth point, and the estimate where the person had one, in Unity world axes,
metres): the real noise, dropouts and motion to replay against a tracker (see the README, "Driving objects from the poses").
"""
import argparse
import csv
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
from fvp_selftest import camera_message, to_zup_mm, parse_frames, wait_port, JOINT_MAP  # noqa: E402

# The followed point: neck, shoulders, hips (Panoptic-15 index, name in the session's ground truth). The Unity follower also averages the
# model's own mid-hip, which sits halfway between the hips, so it is the same point.
ANCHOR = [(0, 'neck'), (3, 'left_shoulder'), (9, 'right_shoulder'), (6, 'left_hip'), (12, 'right_hip')]


def to_unity(p_mm):
    """model frame (mm, Z up: x, y, z) -> Unity world (m): x = x, y = z, z = -y"""
    p = np.asarray(p_mm, float) / 1000.0
    return np.array([p[0], p[2], -p[1]])


def mean_support(row):
    v = [x for x in row[1:] if x is not None and x >= 0]
    return float(np.mean(v)) if v else -1.0


def stats(a, scale=1.0, unit='cm'):
    a = np.asarray(a, float) * scale
    if len(a) == 0:
        return 'n/a'
    return 'n %d   mean %.1f  median %.1f  p90 %.1f  p95 %.1f  p99 %.1f  max %.1f %s' % (
        len(a), a.mean(), np.median(a), np.percentile(a, 90), np.percentile(a, 95), np.percentile(a, 99), a.max(), unit)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--repo', default=r'C:/Users/vdmontanacuellar/Documents/Daniel/AProjectPTZCameras/Models/Faster-VoxelPose')
    ap.add_argument('--dataset', default=r'C:/Users/vdmontanacuellar/Documents/Daniel/AProjectPTZCameras/Datasets/MultiViewSession_20260430_172237')
    ap.add_argument('--port', type=int, default=5591)
    ap.add_argument('--frames', default='0:608:2', help='"start:stop:step" or a comma list')
    ap.add_argument('--views', default='1,2,3', help='1-based camera indices to send (the live scene has 3)')
    ap.add_argument('--min-score', type=float, default=0.1)
    ap.add_argument('--min-support', type=float, default=0.35, help='the phantom rule of FasterVoxelPoseLive / FasterVoxelPoseLite')
    ap.add_argument('--spawn', action='store_true', help='start fvp_server.py (fp16 backbone, like Unity) for the run and stop it afterwards')
    ap.add_argument('--server-args', default='--fp16')
    ap.add_argument('--csv', help='write the per-person, per-frame errors here')
    args = ap.parse_args()

    proc = None
    if args.spawn:
        here = os.path.dirname(os.path.abspath(__file__))
        cmd = [sys.executable, os.path.join(here, 'fvp_server.py'), '--repo', args.repo, '--port', str(args.port)] + args.server_args.split()
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
    anchor_pred = [p for p, _ in ANCHOR]
    anchor_gt = [gt_idx[n] for _, n in ANCHOR]

    sock = socket.create_connection(('127.0.0.1', args.port), 10)
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    hello, _ = read_packet(sock)
    cfg = {'type': 'config', 'config_id': 1, 'width': w, 'height': h, 'min_score': args.min_score,
           'space_center': centre.tolist(), 'space_size': [8000.0, 8000.0, 2000.0], 'voxels_per_axis': [80, 80, 20], 'max_people': 10,
           'cameras': [camera_message(c) for c in cams]}
    send_packet(sock, cfg)
    reply, _ = read_packet(sock)
    assert reply['type'] == 'config_ok', reply
    print('server:', hello.get('gpu'), 'version', hello.get('server_version'), '| views', args.views, '| frames', args.frames)

    rows = []                      # per matched person and frame
    truth_rows = []                # per ground-truth person and frame: (frame, time, person, truth x, z, has an estimate, estimate x, z)
    n_gt = n_match = 0
    n_est = n_ghost_raw = n_ghost_kept = n_phantom = n_phantom_real = 0
    ghost_frames_raw = ghost_frames_kept = 0
    t_net, t_total, t_round = [], [], []
    last = {}                      # person index -> (time, error vector) of the previous matched estimate
    jitter, speeds = [], []
    prev_gt = {}

    fidx = parse_frames(args.frames, len(frames))
    for fi in fidx:
        fr = frames[fi]
        imgs = []
        for c in cams:
            bgr = cv2.imread(os.path.join(ds, fr['rgb_paths'][c['id']]), cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
            imgs.append(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))
        payload = np.stack(imgs).tobytes()
        t0 = time.time()
        send_packet(sock, {'type': 'frame', 'id': fi, 'config_id': 1, 'views': len(imgs), 'width': w, 'height': h, 'min_score': args.min_score}, payload)
        res, binary = read_packet(sock)
        t_round.append((time.time() - t0) * 1000.0)
        if res['type'] != 'result':
            print('frame', fi, '->', res)
            continue
        t_net.append(res['t_net']); t_total.append(res['t_total'])
        pb = res.get('poses_bytes', len(binary))
        poses = np.frombuffer(binary[:pb], '<f4').reshape(res['people'], res['joints'], 5)
        support = {int(r[0]): mean_support(r) for r in res.get('support', [])}

        gt = np.array([to_zup_mm([kp['position_in_world'] for kp in pr['keypoints']]) for pr in fr['persons']])      # (people, 14, 3) mm
        n_gt += len(gt)

        est_rows = [r for r in range(poses.shape[0]) if poses[r, 0, 3] >= 0 and poses[r, 0, 4] >= args.min_score]
        n_est += len(est_rows)
        phantom = {r: (support.get(r, -1.0) >= 0 and support[r] < args.min_support) for r in est_rows}
        n_phantom += sum(phantom.values())

        # match every estimate to a ground-truth person: cheapest mean joint distance first, inside 1 m (the status card's rule)
        pairs = []
        for r in est_rows:
            for g in range(len(gt)):
                cost = np.linalg.norm(poses[r, pred_sel, :3] - gt[g][gt_sel], axis=1).mean()
                if cost <= 1000.0:
                    pairs.append((cost, r, g))
        pairs.sort()
        used_r, used_g, match = set(), set(), {}
        for cost, r, g in pairs:
            if r in used_r or g in used_g:
                continue
            used_r.add(r); used_g.add(g); match[r] = g

        ghosts_raw = [r for r in est_rows if r not in match]
        ghosts_kept = [r for r in ghosts_raw if not phantom[r]]
        n_ghost_raw += len(ghosts_raw); n_ghost_kept += len(ghosts_kept)
        ghost_frames_raw += bool(ghosts_raw); ghost_frames_kept += bool(ghosts_kept)
        n_phantom_real += sum(1 for r in match if phantom[r])

        t_s = fr['timestamp']
        got = set()
        est_of = {}
        for r, g in match.items():
            if phantom[r]:
                continue                    # the live filter hides it: it is not an estimate the capsule would see
            got.add(g)
            ea = to_unity(poses[r, anchor_pred, :3].mean(0))
            ga = to_unity(gt[g][anchor_gt].mean(0))
            err = ea - ga
            est_of[g] = ea
            rows.append((fi, t_s, g, ea[0], ea[2], ga[0], ga[2], err[0], err[2], err[1], float(poses[r, 0, 4]), support.get(r, -1.0)))
            if g in last and 0 < t_s - last[g][0] < 0.5:
                jitter.append(np.hypot(*(err[[0, 2]] - last[g][1])) )
            last[g] = (t_s, err[[0, 2]])
            if g in prev_gt and 0 < t_s - prev_gt[g][0] < 0.5:
                speeds.append(np.hypot(*(ga[[0, 2]] - prev_gt[g][1])) / (t_s - prev_gt[g][0]))
            prev_gt[g] = (t_s, ga[[0, 2]])
        for g in range(len(gt)):            # the truth of every person at every frame, estimate or not (a replay needs it where the estimate dropped out)
            gta = to_unity(gt[g][anchor_gt].mean(0))
            truth_rows.append((fi, t_s, g, gta[0], gta[2], g in est_of, est_of[g][0] if g in est_of else 0.0, est_of[g][2] if g in est_of else 0.0))
        n_match += len(got)

    sock.close()
    if not rows:
        print('nothing matched')
        return
    a = np.array(rows, float)
    horiz = np.hypot(a[:, 7], a[:, 8])
    nframes = len(fidx)
    print('\n%d frames, %d ground-truth people, %d with an estimate that the live phantom rule keeps (%.0f %%)' % (nframes, n_gt, n_match, 100.0 * n_match / max(1, n_gt)))
    print('estimates: %d, %d phantoms removed by the 2D-support rule (%d of them real people), ghosts left: %d (%.0f %% of frames) - before the rule: %d (%.0f %%)' % (
        n_est, n_phantom, n_phantom_real, n_ghost_kept, 100.0 * ghost_frames_kept / nframes, n_ghost_raw, 100.0 * ghost_frames_raw / nframes))
    print('\nthe torso point (neck, shoulders, hips) against the ground truth, horizontally:')
    print('  error          ', stats(horiz, 100))
    print('  jitter         ', stats(jitter, 100), '  (change of the error from one estimate to the next, <0.5 s apart)')
    print('  mean offset     x %.1f cm   z %.1f cm   (a constant offset is calibration / joint definition: no filter removes it)' % (100 * a[:, 7].mean(), 100 * a[:, 8].mean()))
    print('  scatter around that offset: x %.1f cm   z %.1f cm (1 sigma)' % (100 * a[:, 7].std(), 100 * a[:, 8].std()))
    print('  vertical error ', stats(np.abs(a[:, 9]), 100))
    print('  truth speed     ', stats(speeds, 1.0, 'm/s'), ' <- how much these people actually move')
    print('server per frame:  network %.0f ms (p95 %.0f), whole answer %.0f ms (p95 %.0f), round trip incl. sending %.0f MB of pictures %.0f ms' % (
        np.mean(t_net), np.percentile(t_net, 95), np.mean(t_total), np.percentile(t_total, 95), len(cams) * w * h * 3 / 1e6, np.mean(t_round)))

    if args.csv:
        with open(args.csv, 'w', newline='') as fh:
            wr = csv.writer(fh)
            wr.writerow(['frame', 'time_s', 'person', 'gt_x', 'gt_z', 'has_estimate', 'est_x', 'est_z'])
            for r in truth_rows:
                wr.writerow(['%d' % r[0], '%.6f' % r[1], '%d' % r[2], '%.5f' % r[3], '%.5f' % r[4], '1' if r[5] else '0', '%.5f' % r[6], '%.5f' % r[7]])
        print('wrote', args.csv, '(%d rows: the truth of every person at every frame, with the estimate where there was one)' % len(truth_rows))


if __name__ == '__main__':
    main()
