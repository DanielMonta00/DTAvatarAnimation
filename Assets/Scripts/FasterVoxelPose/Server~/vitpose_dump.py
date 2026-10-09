"""python vitpose_dump.py --out lift_input.json [--step 8] [--scale 0.75]   (in the vitpose environment)

Dumps, for the recorded session: the cameras, the 3D ground truth, and ViTPose's 2D detections of every view (frames sent at
960x540 like Unity does, reported in pixels of the original 1280x720 image), to one JSON for the C# lifting harness."""
import argparse, json, os, sys
import numpy as np, cv2

sys.dont_write_bytecode = True
SRV = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, SRV)
import vitpose_server as vs

AIA = r'C:/Users/vdmontanacuellar/Documents/Daniel/AProjectPTZCameras'
ap = argparse.ArgumentParser()
ap.add_argument('--dataset', default=AIA + '/Datasets/MultiViewSession_20260430_172237')
ap.add_argument('--step', type=int, default=8)
ap.add_argument('--scale', type=float, default=0.75)
ap.add_argument('--out', required=True)
ap.add_argument('--det-thr', type=float, default=0.4)
a = ap.parse_args()

args = argparse.Namespace(vitpose_dir=AIA + '/Models/ViTPose', yolo=AIA + '/yolov8m.pt', device='cuda:0', config=None, checkpoint=None, det_thr=a.det_thr)
eng = vs.ViTEngine(args)

cams = json.load(open(a.dataset + '/cameras.json'))['cameras']
kt = json.load(open(a.dataset + '/keypoints_transforms.json'))
frames = kt['frames']
names = {n: i for i, n in enumerate(kt['keypoint_names'])}
SHARED = ['left_shoulder', 'right_shoulder', 'left_elbow', 'right_elbow', 'left_wrist', 'right_wrist', 'left_hip', 'right_hip',
          'left_knee', 'right_knee', 'left_ankle', 'right_ankle']
S = np.diag([1.0, -1.0, -1.0, 1.0])

out = {'cameras': [], 'shared': SHARED, 'frames': []}
for c in cams:
    E = S @ np.array(c['world_to_camera'], float)
    out['cameras'].append({'name': c['name'], 'w': c['image_size'][0], 'h': c['image_size'][1], 'fx': c['fx'], 'fy': c['fy'], 'cx': c['cx'], 'cy': c['cy'],
                           'pos': c['position_in_world'], 'rot': E[:3, :3].reshape(-1).tolist()})
W, H = cams[0]['image_size']
w, h = int(round(W * a.scale)), int(round(H * a.scale))
for fi in range(0, len(frames), a.step):
    fr = frames[fi]
    imgs = []
    for c in cams:
        bgr = cv2.imread(os.path.join(a.dataset, fr['rgb_paths'][c['id']]), cv2.IMREAD_COLOR | cv2.IMREAD_IGNORE_ORIENTATION)
        rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
        imgs.append(cv2.resize(rgb, (w, h), interpolation=cv2.INTER_AREA))
    rows = eng.infer(np.stack(imgs), a.det_thr, None)
    dets = [[] for _ in cams]
    for r in rows:
        v = int(r[0])
        kp = r[6:].reshape(17, 3).copy()
        kp[:, :2] /= a.scale
        dets[v].append({'box': (r[1:5] / a.scale).tolist(), 'det': float(r[5]), 'kp': kp.tolist()})
    gt = []
    for pr in fr['persons']:
        gt.append({n: pr['keypoints'][names[n]]['position_in_world'] for n in SHARED})
    out['frames'].append({'index': fi, 'gt': gt, 'dets': dets})
json.dump(out, open(a.out, 'w'))
print('wrote', a.out, len(out['frames']), 'frames')
