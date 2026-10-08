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
                       config_ok, result (bin = float32 [max_people, 15, 5], then, when the frame asked for it with
                       "heat": 1, uint8 [views, h, w] 2D joint heatmaps: see Engine.heat_maps), pong, error

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

        if args.fp16 and self.device.type == 'cuda':
            self._backbone_fp16()
        if not args.no_mask:
            self._install_masked_projection()
        if args.cudnn != 'config':
            torch.backends.cudnn.benchmark = (args.cudnn == 'on')
        log('[fvp] cudnn autotune %s' % ('on' if torch.backends.cudnn.benchmark else 'off'))

        self.model = None
        self.config_id = None
        self.config_sig = None       # what the current head was built for (see same_config)
        self.stats = []              # (pre ms, net ms, total ms) of the last frames, logged every STATS_EVERY
        self.last_heat = None
        self.cameras = None
        self.resize_transform = None
        self.trans_np = None
        self.view_count = 0
        self.frame_size = (0, 0)
        self.mean = torch.tensor([0.485, 0.456, 0.406], device=self.device).view(1, 3, 1, 1)
        self.std = torch.tensor([0.229, 0.224, 0.225], device=self.device).view(1, 3, 1, 1)
        self.gpu_name = torch.cuda.get_device_name(self.device) if self.device.type == 'cuda' else 'cpu'

    # The ResNet-50 backbone is about half of the per-frame time. Running it in half precision (heatmaps cast back to
    # float32 for everything after it) takes ~15 ms off a frame; on the recorded session the joints move by < 1 mm on
    # average.
    def _backbone_fp16(self):
        torch = self.torch
        backbone, forward = self.backbone, self.backbone.forward

        def forward_fp16(x):
            with torch.autocast('cuda', dtype=torch.float16):
                return forward(x).float()

        backbone.forward = forward_fp16
        log('[fvp] backbone in float16')

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

    # What a config message boils down to, to tell whether the head and its voxel grids are still valid for it.
    @staticmethod
    def _signature(msg):
        cams = []
        for c in msg['cameras']:
            cams.append(np.concatenate([
                np.asarray(c['R'], float).ravel(), np.asarray(c['T'], float).ravel(),
                [c['fx'], c['fy'], c['cx'], c['cy']],
                np.asarray(c.get('k', [0, 0, 0]), float).ravel(), np.asarray(c.get('p', [0, 0]), float).ravel(),
                [float(c.get('max_radius') or 0.0)]]))
        return {'w': int(msg['width']), 'h': int(msg['height']), 'people': int(msg.get('max_people', 10)),
                'vox': np.asarray(msg.get('voxels_per_axis', [80, 80, 20]), int),
                'centre': np.asarray(msg['space_center'], float), 'size': np.asarray(msg['space_size'], float),
                'cams': np.stack(cams) if cams else np.zeros((0, 0))}

    # Per-entry tolerances of one camera vector: R(9) T(3, mm) f/c(4, px) k(3) p(2) radius
    _CAM_TOL = np.array([1e-6] * 9 + [0.01] * 3 + [0.01] * 4 + [1e-8] * 3 + [1e-8] * 2 + [1e-4])

    @classmethod
    def _same_signature(cls, a, b):
        if a is None or b is None:
            return False
        if (a['w'], a['h'], a['people']) != (b['w'], b['h'], b['people']) or not np.array_equal(a['vox'], b['vox']):
            return False
        if np.abs(a['centre'] - b['centre']).max() > 0.5 or np.abs(a['size'] - b['size']).max() > 0.5:
            return False
        if a['cams'].shape != b['cams'].shape or a['cams'].shape[1] != len(cls._CAM_TOL):
            return False
        return bool((np.abs(a['cams'] - b['cams']) <= cls._CAM_TOL).all())

    # The same cameras, volume and frame size as the head in memory was built for: its voxel grids are still right.
    def same_config(self, msg):
        return self.model is not None and self._same_signature(self.config_sig, self._signature(msg))

    # Where the last config is kept, so a fresh server can have its grids and kernels ready before anybody asks.
    def _cache_file(self):
        d = self.args.cache_dir
        return os.path.join(d, 'last_config.json') if d else None

    def _save_config(self, msg):
        path = self._cache_file()
        if not path:
            return
        try:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, 'w', encoding='utf-8') as fh:
                json.dump({k: v for k, v in msg.items() if k != 'type'}, fh)
        except OSError as e:
            log('[fvp] could not save the config cache: %s' % e)

    # Builds the head, the voxel grids and warms the kernels for the last config seen, before the first client connects.
    def prewarm(self):
        path = self._cache_file()
        if not path or not os.path.isfile(path):
            log('[fvp] no cached config yet: the first Play builds the grids once')
            return
        try:
            with open(path, encoding='utf-8') as fh:
                msg = json.load(fh)
            msg['type'] = 'config'
            msg['config_id'] = 0
            ms, _ = self.configure(msg)
            log('[fvp] grids and kernels ready from the cached config: %d views %dx%d  (%.0f ms)' % (
                self.view_count, self.frame_size[0], self.frame_size[1], ms))
        except Exception as e:  # a stale or damaged cache must never keep the server from starting
            traceback.print_exc()
            log('[fvp] cached config ignored: %s' % e)
            self.model = None
            self.config_sig = None

    # The 3D networks run once per detected person, as one batch: cudnn tunes (and the allocator sizes) every new batch
    # size on first use, which is a stall of up to seconds the first time somebody steps into view. Do them now, for 1..k.
    def _warm_people(self, heatmaps, centers, k_max):
        torch = self.torch
        pc0 = centers.detach().clone()
        centre = torch.as_tensor(np.asarray(self.config.CAPTURE_SPEC.SPACE_CENTER, dtype=np.float32), device=self.device)
        with torch.no_grad():
            for k in range(1, min(k_max, pc0.shape[1]) + 1):
                pc = pc0.clone()
                pc[:, :, 3] = -1.0
                pc[:, :k, 0:3] = centre
                pc[:, :k, 3] = 0.0
                pc[:, :k, 4] = 0.5
                pc[:, :k, 5:7] = 0.5
                mask = pc[:, :, 3] >= 0
                self.model.joint_net({'seq': [SEQ]}, heatmaps, pc, mask, self.cameras, self.resize_transform)
        if self.device.type == 'cuda':
            torch.cuda.synchronize()

    # (Re)builds everything that depends on the cameras / capture volume / frame size. The head's
    # constructor bakes the voxel grids in, so a new volume needs a new head; the backbone is kept.
    # Returns (milliseconds, reused): `reused` when nothing changed since the last config and the grids were kept.
    def configure(self, msg):
        torch, config = self.torch, self.config
        if self.same_config(msg):
            self.config_id = msg.get('config_id')
            config.CAPTURE_SPEC.MIN_SCORE = float(msg.get('min_score', 0.1))
            return 0.0, True
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
        if self.args.warm_people > 0:
            try:
                inputs = self._preprocess(blank)
                with torch.no_grad():
                    _, _, centers, heat, _ = self.model(backbone=self.backbone, views=inputs, meta={'seq': [SEQ]},
                                                        cameras=self.cameras, resize_transform=self.resize_transform)
                self._warm_people(heat, centers, self.args.warm_people)
            except Exception as e:  # warming is an optimisation; never fail a config over it
                traceback.print_exc()
                log('[fvp] could not pre-tune for several people: %s' % e)
        self.config_sig = self._signature(msg)
        self._save_config(msg)
        return (time.perf_counter() - t0) * 1000.0, False

    STATS_EVERY = 50

    # One line every STATS_EVERY frames (and at the end of a session): what the server spends per frame. It tells a slow
    # network apart from a GPU that is busy with something else (Unity rendering): the same frames take ~50 ms alone.
    def record(self, pre, net, total):
        self.stats.append((pre, net, total))
        if len(self.stats) >= self.STATS_EVERY:
            self.flush_stats()

    def flush_stats(self):
        if len(self.stats) < 5:
            self.stats = []
            return
        a = np.array(self.stats)
        log('[fvp] last %d frames: pre %.1f ms, net %.1f ms (max %.0f), total %.1f ms (p95 %.0f)' % (
            len(a), a[:, 0].mean(), a[:, 1].mean(), a[:, 1].max(), a[:, 2].mean(), np.percentile(a[:, 2], 95)))
        self.stats = []

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

    # The 2D stage of the network (the ResNet) gives every view one heatmap per joint; the 3D stages only ever see those, lifted
    # into the voxel volume. They live at 1/4 of the network input (240 x 128), in the network's letterboxed geometry. This
    # returns them in the geometry of the frame that was sent, at 1/4 of its size (e.g. 240 x 135), so a client can lay them
    # straight over the picture: uint8 [views, h, w], 255 = a heatmap value of 1. `joint` < 0 = the strongest of all joints.
    def heat_maps(self, heat_in, joint):
        torch, cv2 = self.torch, self.cv2
        hm = heat_in[0].detach()                                   # (views, joints, 128, 240)
        hm = hm.max(dim=1).values if joint < 0 else hm[:, min(int(joint), hm.shape[1] - 1)]
        hm = (hm.float().clamp(0.0, 1.0) * 255.0).round().to(torch.uint8).cpu().numpy()
        fw, fh = self.frame_size
        ow, oh = max(1, fw // 4), max(1, fh // 4)
        out = np.empty((hm.shape[0], oh, ow), np.uint8)
        M = self._heat_matrix(fw, fh, ow, oh, hm.shape[2], hm.shape[1])
        for v in range(hm.shape[0]):
            out[v] = cv2.warpAffine(hm[v], M, (ow, oh), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=0)
        return out

    # heatmap cell -> network input pixel -> frame pixel -> output cell, all on pixel centres
    def _heat_matrix(self, fw, fh, ow, oh, hw, hh):
        net_w, net_h = int(self.config.DATASET.IMAGE_SIZE[0]), int(self.config.DATASET.IMAGE_SIZE[1])
        sx, sy = net_w / float(hw), net_h / float(hh)                # 4, 4
        heat_to_net = np.array([[sx, 0, (sx - 1) / 2.0], [0, sy, (sy - 1) / 2.0], [0, 0, 1]])
        net_to_frame = np.linalg.inv(np.vstack([self.trans_np, [0, 0, 1]]))
        fx, fy = fw / float(ow), fh / float(oh)
        frame_to_out = np.array([[1 / fx, 0, -(fx - 1) / (2 * fx)], [0, 1 / fy, -(fy - 1) / (2 * fy)], [0, 0, 1]])
        return (frame_to_out @ net_to_frame @ heat_to_net)[:2]

    def infer(self, views, min_score, heat_joint=None):
        torch = self.torch
        t0 = time.perf_counter()
        inputs = self._preprocess(views)
        self.model.pose_net.proposal_layer.min_score = float(min_score)
        if self.device.type == 'cuda':
            torch.cuda.synchronize()
        t1 = time.perf_counter()
        self.last_heat = None
        with torch.no_grad():
            fused, _, centers, heat_in, _ = self.model(
                backbone=self.backbone, views=inputs, meta={'seq': [SEQ]},
                cameras=self.cameras, resize_transform=self.resize_transform)
            poses = fused[0].detach().cpu().numpy().astype('<f4')
            if heat_joint is not None:
                self.last_heat = self.heat_maps(heat_in, heat_joint)
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
                ms, reused = engine.configure(msg)
                log('[fvp] config %s: %d views %dx%d, volume centre %s size %s  (%.0f ms%s)' % (
                    msg.get('config_id'), engine.view_count, engine.frame_size[0], engine.frame_size[1],
                    np.round(engine.config.CAPTURE_SPEC.SPACE_CENTER).tolist(),
                    engine.config.CAPTURE_SPEC.SPACE_SIZE.tolist(), ms, ', grids reused' if reused else ''))
                send_packet(conn, {'type': 'config_ok', 'config_id': msg.get('config_id'), 'ms': ms, 'reused': reused})
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
                want_heat = bool(msg.get('heat'))
                poses = engine.infer(views, float(msg.get('min_score', 0.1)),
                                     heat_joint=int(msg.get('heat_joint', -1)) if want_heat else None)
                total = (time.perf_counter() - t0) * 1000.0
                engine.record(engine.last_timing[0], engine.last_timing[1], total)
                reply = {'type': 'result', 'id': msg.get('id'), 'config_id': engine.config_id,
                         'people': int(poses.shape[0]), 'joints': int(poses.shape[1]),
                         't_pre': engine.last_timing[0], 't_net': engine.last_timing[1],
                         't_total': total, 'poses_bytes': int(poses.nbytes)}
                payload = poses.tobytes()
                if engine.last_heat is not None:
                    hv, hh, hw = engine.last_heat.shape
                    reply.update({'heat_views': hv, 'heat_h': hh, 'heat_w': hw, 'heat_joint': int(msg.get('heat_joint', -1))})
                    payload += engine.last_heat.tobytes()
                send_packet(conn, reply, payload)
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
            engine.flush_stats()
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
    ap.add_argument('--fp16', action='store_true', help='run the 2D backbone in half precision (~15 ms faster per frame)')
    ap.add_argument('--cudnn', choices=['config', 'on', 'off'], default='off',
                    help="cudnn autotuning: 'off' (default) picks kernels by heuristic: same speed and accuracy here, no stall the first "
                         "time a new shape appears and a cold config about half as long; 'config' follows the repo's yaml (on)")
    ap.add_argument('--warm-people', type=int, default=3,
                    help='pre-tune the per-person 3D networks for 1..N people when a config is built (0 = skip)')
    ap.add_argument('--cache-dir', help='keeps the last config here; the next server start builds its grids from it before any client asks')
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
    t1 = time.time()
    engine.prewarm()
    if engine.model is not None:
        log('[fvp] warm in %.1f s' % (time.time() - t1))
    serve(engine, args)


if __name__ == '__main__':
    main()
