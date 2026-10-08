"""Where does the error come from? (python fvp_diagnose.py [--repo R] [--dataset D] [--step N]) Splits Faster-VoxelPose's error on the recorded session into
  - the 2D detector (the ResNet heatmaps): how far is the nearest heatmap peak from the true 2D joint, per joint,
  - the 3D result: error of the fused 3D joints, per joint, with 4 views and with every 3-view subset,
  - the 3D result reprojected into the images (is the 3D answer consistent with what the 2D detector saw?).
"""
import argparse, itertools, json, os, sys, time
import numpy as np, cv2, torch

SRV = os.path.dirname(os.path.abspath(__file__))
sys.dont_write_bytecode = True
sys.path.insert(0, SRV)
import fvp_server, fvp_selftest as st  # noqa

ap = argparse.ArgumentParser()
ap.add_argument('--repo', default=r'C:/Users/vdmontanacuellar/Documents/Daniel/AProjectPTZCameras/Models/Faster-VoxelPose')
ap.add_argument('--dataset', default=r'C:/Users/vdmontanacuellar/Documents/Daniel/AProjectPTZCameras/Datasets/MultiViewSession_20260430_172237')
ap.add_argument('--step', type=int, default=16, help='use every Nth frame')
cli = ap.parse_args()
REPO, DS = cli.repo, cli.dataset

args = argparse.Namespace(repo=REPO, config=None, backbone=None, model=None, device='cuda:0', fp16=False, no_mask=False,
                          preprocess='warp', cudnn='off', warm_people=0, cache_dir=None)
engine = fvp_server.Engine(args)

cams_all = json.load(open(os.path.join(DS, 'cameras.json')))['cameras']
kt = json.load(open(os.path.join(DS, 'keypoints_transforms.json')))
frames = kt['frames']
gt_idx = {n: i for i, n in enumerate(kt['keypoint_names'])}
NAMES = [n for _, n in st.JOINT_MAP]
PRED = [p for p, _ in st.JOINT_MAP]
w, h = cams_all[0]['image_size']

allW = np.array([kp['position_in_world'] for f in frames for pr in f['persons'] for kp in pr['keypoints']], float)
P = st.to_zup_mm(allW)
lo, hi = P.min(0), P.max(0)
centre = (lo + hi) / 2.0
centre[2] = hi[2] / 2.0
centre = np.round(centre)


def msg_for(views):
    return {'type': 'config', 'config_id': 1, 'width': w, 'height': h, 'min_score': 0.1,
            'space_center': centre.tolist(), 'space_size': [8000.0, 8000.0, 2000.0], 'voxels_per_axis': [80, 80, 20],
            'max_people': 10, 'cameras': [st.camera_message(cams_all[v]) for v in views]}


def project(cam, pts_world_m):
    E = st.S @ np.array(cam['world_to_camera'], float)
    pc = (E @ np.c_[pts_world_m, np.ones(len(pts_world_m))].T).T[:, :3]
    return np.c_[cam['fx'] * pc[:, 0] / pc[:, 2] + cam['cx'], cam['fy'] * pc[:, 1] / pc[:, 2] + cam['cy']]


FRAMES = list(range(0, len(frames), cli.step))
images = {}
for fi in FRAMES:
    for c in cams_all:
        bgr = cv2.imread(os.path.join(DS, frames[fi]['rgb_paths'][c['id']]), cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
        images[(fi, c['index'] - 1)] = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)


def gt_world(fi):
    """(persons, 13, 3) Unity metres, and 2D (persons, 13, cams, 2) + visibility"""
    xyz, uv, vis = [], [], []
    for pr in frames[fi]['persons']:
        kps = pr['keypoints']
        xyz.append([kps[gt_idx[n]]['position_in_world'] for n in NAMES])
        uv.append([[pc['image_position'] for pc in kps[gt_idx[n]]['per_camera']] for n in NAMES])
        vis.append([[pc['visible'] for pc in kps[gt_idx[n]]['per_camera']] for n in NAMES])
    uv = np.array(uv, float)
    uv[..., 1] = h - uv[..., 1]   # the dataset's image_position has its origin at the bottom-left
    return np.array(xyz, float), uv, np.array(vis, bool)


def estimate(fi, views):
    imgs = np.stack([images[(fi, v)] for v in views])
    poses = engine.infer(imgs, 0.1)
    people = poses[poses[:, 0, 3] >= 0]
    return people


def match(people, gt_mm):
    """greedy match estimated persons to GT persons; returns list of (pred_idx_person, gt_idx_person)"""
    if len(people) == 0:
        return []
    pj = people[:, PRED, :3]
    cost = np.linalg.norm(pj[:, None] - gt_mm[None], axis=3).mean(-1)
    used_p, used_g, out = set(), set(), []
    for _, p, g in sorted((cost[p, g], p, g) for p in range(len(pj)) for g in range(len(gt_mm))):
        if p in used_p or g in used_g:
            continue
        used_p.add(p); used_g.add(g); out.append((p, g))
    return out


# ---------------------------------------------------------------- 3D error: all four views, then every 3-view subset
def run_views(views):
    engine.configure(msg_for(views))
    errs = [[] for _ in NAMES]
    reproj = [[] for _ in NAMES]
    for fi in FRAMES:
        gxyz, guv, gvis = gt_world(fi)
        gt_mm = np.stack([st.to_zup_mm(g) for g in gxyz])
        people = estimate(fi, views)
        for p, g in match(people, gt_mm):
            d = np.linalg.norm(people[p, PRED, :3] - gt_mm[g], axis=1)
            for j in range(len(NAMES)):
                errs[j].append(d[j])
            # reproject the estimated 3D joints into each used view and compare with the true 2D position
            est_world_m = (st.A.T @ people[p, PRED, :3].T).T / 1000.0
            for v in views:
                uv = project(cams_all[v], est_world_m)
                for j in range(len(NAMES)):
                    if gvis[g, j, v]:
                        reproj[j].append(np.linalg.norm(uv[j] - guv[g, j, v]))
    return [np.array(e) for e in errs], [np.array(r) for r in reproj]


t0 = time.time()
e4, r4 = run_views([0, 1, 2, 3])
subsets = list(itertools.combinations(range(4), 3))
sub_results = {s: run_views(list(s)) for s in subsets}
print('3D runs done in %.0f s' % (time.time() - t0))

# ---------------------------------------------------------------- 2D detector: nearest heatmap peak to the true 2D joint
def heat_peaks(fi, v, topk=4):
    imgs = np.stack([images[(fi, v)]])
    inputs = engine._preprocess(imgs)             # (1, 1, 3, 512, 960)
    with torch.no_grad():
        hm = engine.backbone(inputs[:, 0])        # (1, 15, 128, 240)
    hm = hm[0].float()
    pooled = torch.nn.functional.max_pool2d(hm[None], 7, 1, 3)[0]
    nms = (hm == pooled) & (hm > 0.02)
    inv = cv2.invertAffineTransform(engine.trans_np)
    out = []
    for j in range(hm.shape[0]):
        ys, xs = torch.nonzero(nms[j], as_tuple=True)
        vals = hm[j][ys, xs]
        order = torch.argsort(vals, descending=True)[:topk]
        pts = []
        for k in order.tolist():
            xn, yn = xs[k].item() * 4.0 + 1.5, ys[k].item() * 4.0 + 1.5
            pts.append((inv @ np.array([xn, yn, 1.0]), vals[k].item()))
        out.append(pts)
    return out


# the 2D detector is run on the same warped input; the config of the last 3-view run does not matter for it
d2 = [[] for _ in NAMES]       # distance (px, original image) of the nearest of the strongest peaks to the true 2D joint
v2 = [[] for _ in NAMES]       # that peak's heatmap value
miss = [0] * len(NAMES)
tot = [0] * len(NAMES)
for fi in FRAMES:
    gxyz, guv, gvis = gt_world(fi)
    for v in range(4):
        peaks = heat_peaks(fi, v)
        for pj_i, j in enumerate(PRED):
            for g in range(len(gxyz)):
                if not gvis[g, pj_i, v]:
                    continue
                tot[pj_i] += 1
                best = min(peaks[j], key=lambda t: np.linalg.norm(t[0] - guv[g, pj_i, v]), default=None)
                if best is None:
                    miss[pj_i] += 1
                    continue
                d2[pj_i].append(np.linalg.norm(best[0] - guv[g, pj_i, v]))
                v2[pj_i].append(best[1])

print('\nframes: %d (every 16th), 2 people, 4 cameras, 1280x720, pinhole\n' % len(FRAMES))
print('%-15s | %-34s | %-27s | %s' % ('joint', '2D detector (px at 1280x720)', '3D error (mm)', '3D answer back in the image (px)'))
print('%-15s | %8s %8s %8s %6s | %8s %8s %8s | %8s %8s' % ('', 'median', 'p90', '<20px', 'peak', '4 views', '3 views', 'worst3', '4 views', '3 views'))
for j, n in enumerate(NAMES):
    d = np.array(d2[j])
    s3 = [np.mean(sub_results[s][0][j]) for s in subsets]
    r3 = np.concatenate([sub_results[s][1][j] for s in subsets])
    print('%-15s | %8.1f %8.1f %7.0f%% %6.2f | %8.0f %8.0f %8.0f | %8.1f %8.1f' % (
        n, np.median(d), np.percentile(d, 90), 100 * (d < 20).mean(), np.mean(v2[j]), e4[j].mean(), np.mean(s3), max(s3),
        np.median(r4[j]), np.median(r3)))
print()
print('3D mean over joints:  4 views %.0f mm   3-view subsets %s mm' % (
    np.mean([e.mean() for e in e4]), ', '.join('%d:%.0f' % (i, np.mean([np.mean(sub_results[s][0][j]) for j in range(len(NAMES))])) for i, s in enumerate(subsets))))
print('subsets are the views kept: ' + ', '.join('%d=%s' % (i, [x + 1 for x in s]) for i, s in enumerate(subsets)))
print('camera positions (Unity world, m):')
for c in cams_all:
    print('  cam %d  %s  height %.2f' % (c['index'], np.round(c['position_in_world'], 2).tolist(), c['position_in_world'][1]))
