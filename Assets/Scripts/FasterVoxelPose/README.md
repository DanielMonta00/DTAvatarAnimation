# FasterVoxelPose — live multi-view 3D pose estimation in Unity

Runs [Faster-VoxelPose](https://github.com/AlvinYH/Faster-VoxelPose) (Ye et al., ECCV 2022) on the calibrated
cameras of the scene and shows what it finds, live:

* **ground truth (green) and estimate (orange) drawn over each camera's own display** (cam107 / cam103 / cam120 each
  on the Target Display its `CalibratedCamera` already shows its image on), in the style of the dataset images: thin
  lines, round dots, line colour = average of its two joints; the person's left side lighter and the right side
  darker in both colours; a status line per display,
* the **3D reconstruction as gizmos** (Scene view, or Game view with *Gizmos* on) plus the capture volume box,
* **pause / frame-by-frame / rewind** controls (mouse: click, wheel, drag) that put the whole scene back, robot included,
* the poses as plain C# objects (`FvpPerson`) for whatever they will drive next (Bulles, PontRoulant…).

## Quick start

1. **Tools ▸ FasterVoxelPose ▸ Create FasterVoxelPose object (wire cameras, volume, animators)** – creates the `FasterVoxelPose` GameObject with
   `FasterVoxelPoseLive` and wires it from the open scene (cam107 / cam103 / cam120 *main* cameras, the
   `MOCAPcenter` anchor, the scene's Animators). Check the Console line it prints.
   (Or add the component to your own object and press **Auto-wire from scene**.)
2. Press **Play**. The first time, the Python server starts and loads the model (~10 s; the status line says so).
   The server then stays up, so the next Play starts instantly.
3. Walk someone into the **cyan box** (the capture volume). Resize / move it in the Inspector until it covers the
   floor space you care about: people outside it are not found.

Nothing else to install in Unity. The server needs the `fastervoxelpose` conda env (torch + the repo's
requirements) and the Faster-VoxelPose checkout with its two checkpoints — the paths are Inspector fields
(`Python Exe`, `Fvp Repo`) and default to this machine's locations.

## Controls

Runs **in real time** by default: the displays show the live cameras with the ground truth exactly on them and the
estimate a few tens of milliseconds behind (see below). Pause whenever you want to look closer.

**Mouse** (on any of the three displays; the Game view must be focused):

| Mouse | Does |
|---|---|
| left click | Pause / play |
| wheel | One frame at a time: down = forward, up = back (the first notch pauses) |
| left drag | Scrub the history: drag left = back in time, one frame per `Drag Pixels Per Frame` (default 12 px) |

A click on the transport bar belongs to its buttons and is not also read as "pause".

**Keyboard and transport bar** (the bar is at the bottom of **every** display, so it is there whichever display the Game view
shows; it shrinks to fit a narrow Game view):

| Key | Button | Does |
|---|---|---|
| Space | Pause / Play | Pause freezes the scene (`Time.timeScale = 0`) and estimates the frozen frame; Play resumes |
| → | `>` | Pause (first press), then advance the scene **one frame** (`Step Seconds`, default 1/30 s) and estimate it. When you are behind the newest frame it walks forward through the history instead. Hold to repeat |
| ← | `<` | One estimate back. Hold to repeat |
| R | `<<` | Rewind continuously (speed: `Rewind Speed`); press again to stop |
| | `>>` | Replay forward through the history |
| Home / End | `\|<` `>\|` | Oldest / newest frame |
| M | Hide | Show / hide the transport bar |
| | slider, `Mode` | Scrub anywhere in the history; switch real-time / synced display |

Input comes from the Input System (the project's `Active Input Handling`); the legacy Input Manager is used if that is what
a project has.

A *frame* is one estimate: the skeletons, the camera models that were used and the state of the scene.
History keeps the last `History Frames` (default 2000).

**What rewinds.** Stepping back, scrubbing and rewinding put the **whole scene** back to what it was in that frame, and
**Play** continues from the frame under the cursor (the frames after it are dropped):

| Thing | How | Setting |
|---|---|---|
| Animators (the avatars) | state and normalised time | `Tracked Animators` |
| Anything that moves: the cable robot's platform, drums and cables, conveyors, props | every transform of the scene is watched at each estimate; one that has changed is remembered from then on, no list to maintain | `Rewind Moving Objects` |
| Scripts running something from a clock of their own | their private runtime fields (e.g. `CableRobotTrajectory.playTime`, `RandomWalker` timers) | `Rewind Scripts` (auto-wired: `CableRobotTrajectory`, `CableRobot`, `RandomWalker`) |
| Video players | paused with the scene (they run on unscaled time), stepped by `Step Seconds`, seeked back | `Rewind Video` |
| Physics | Rigidbody velocities, ArticulationBody joint positions and velocities | `Rewind Physics` |

**What cannot rewind:** anything computed from the absolute clock (`Time.time`) instead of from its own state (a
`sin(Time.time)` is simply what time it is now), anything outside the scene (a robot controller, a network or live mocap
stream) and particle systems. For those the skeleton comes from the past while the camera image shows the present.
Animators and video players set to *Unscaled Time* ignore `timeScale`; the video ones are handled, the Animators are not.
A script's *serialized* fields are treated as configuration and are not restored: a clock that is a `[SerializeField]` or
`public` field has to be moved to a private one to rewind.

### Keeping image, ground truth and estimate in sync

An estimate describes the frame that was *captured*, which is older than the live camera by the time it took to read the
frame back, send it, and run the network (the latency printed on each display). Drawn on the live image that shows up
as a skeleton trailing a walking avatar. `Overlay Image` picks the trade-off (also the **Mode** button on the bar):

* **RealTime** (default) - the display keeps the live camera, nothing is delayed. The ground truth is read from the rig
  *now* (exact) and the estimate is moved forward by its root's horizontal velocity x latency (`Latency Compensation`,
  velocity from a small tracker over the last estimates). Good for a walking person; limbs still trail a little and it
  jumps when someone starts or stops. Paused, the display stays on the live camera, which is frozen on that frame.
* **SyncedFrame** - each display shows *the very frame the estimate was made from*: a full-resolution GPU copy of the
  camera image taken at capture, with the ground truth read from the rig at that same instant. Image, green and
  orange always agree; the picture is as old as the latency and updates at the estimate rate.

Two settings shorten the latency itself: `Low Latency Readback` (collect the camera images straight after rendering
instead of 2-3 rendered frames later; a short main-thread stall per estimate) and `Fast Backbone` (the ResNet in half
precision: ~15 ms less per frame, identical accuracy on the recorded session: 147.5 mm either way; restart the warm server
once - **Tools ▸ FasterVoxelPose ▸ Stop inference server** - for it to apply).

The **ground truth** is the avatars' rigs read through the dataset recorder's own `SkeletonFormats` in the
`CMU_Panoptic_15` layout, i.e. exactly the 15 joints and bones Faster-VoxelPose predicts. `Ground Truth Avatars` is
auto-wired from the `MultiViewRecorder` that records these cameras.

### The lens is in the projection

Every skeleton on a lens-distorted display goes through the camera's full model - K, pose and the OpenCV
k1, k2, p1, p2, k3 distortion, with the same valid-radius cut-off `CalibratedCamera` uses - never a plain pinhole. In a
test with spheres placed near the image corners, where the distortion is strongest, the overlay landed within 0.1 px of
the rendered sphere while a pinhole projection would have been 69 px off. A calibrated camera shown without that
presenter still uses `CalibratedCamera.WorldToViewport` (the same lens model); only an uncalibrated camera uses the
engine's pinhole viewport.

## How it works

```
Unity (FasterVoxelPoseLive)                                  Python (Server~/fvp_server.py)
 CalibratedCamera.DistortedTexture ─ Blit ─▶ 960×540 RGB ─┐
 (or a shadow camera for plain cameras)                    ├─ localhost TCP ─▶ warpAffine → ResNet-50 heatmaps
 camera poses + K + lens distortion + capture volume ──────┘                   → voxel projection → 3D CNNs
 ◀── people × 15 joints × (x, y, z, valid, score) ◀────────────────────────────  (unmodified Faster-VoxelPose)
```

* **Why a Python server?** The network is PyTorch with custom projection code; porting it to an in-process runtime
  would be a research project of its own. The server runs the repo's code as it is. Measured on the dev RTX 5000 Ada:
  ~50–90 ms per frame with 3–4 views (≈10–15 estimates/s); Unity keeps rendering while it works.
* **Newest frame first.** While the server is busy the cameras are not read, so the estimate always describes the
  latest scene a little late (the latency is shown on each display).
* **Overlay.** One screen-space canvas per camera on that camera's Target Display. For a `CalibratedCamera` showing
  its lens-distorted image it uses the same letterbox as that presenter and projects through the camera model that
  was sent to the server (K, pose, lens distortion), so a joint lands on the pixel where the presenter draws it.
  Other cameras get a full-screen overlay projected with the camera's own viewport (through the calibrated lens model
  when the camera is a `CalibratedCamera`).
* **Calibration.** Each view's K and lens distortion (`CalibratedCamera`), position and orientation go to the server,
  which projects its voxels with the *same* lens model, so the heatmaps are sampled where the distorted image really
  has the person. Voxels behind a camera or past the radius where the lens polynomial stops being valid are masked
  (the original repo would mirror them into the image).
* **Frames.** Unity world (metres, Y up) ↔ network frame (mm, Z up) is `A = Rot_x(90°)` — the conversion validated
  against `MultiViewSession_20260430_172237` to < 0.002 px. Joint order is Panoptic-15
  (`FvpSkeleton.Names`).
* **Moving a camera or the volume** while playing re-sends the configuration (≈1 s pause in estimation).

### Pre-processing differs from the experiment notebooks on purpose

The notebooks `cv2.resize` the 16:9 frames to 960×512, but the network was trained on images fitted with an
aspect-preserving affine (`preprocess.py`), and its projection assumes exactly that. The server does the latter.
On `MultiViewSession_20260430_172237` (4 views, 38-frame stride) this brings absolute MPJPE from 165.7 mm to
147.5 mm (PCK@150: 63.8 % → 70.3 %). `Extra Server Args = --preprocess stretch --no-mask` reproduces the notebooks
exactly (frame 120: 163.1 mm, joints within 0.2 mm of `fused_poses_frame120.npy`).

## Using the poses from other scripts

```csharp
var fvp = FasterVoxelPoseLive.Instance;
fvp.FrameEstimated += frame =>              // every new estimate, main thread
{
    foreach (FvpPerson p in frame.people)   // p.id is stable while the person stays in view
    {
        Vector3 pelvis = p.Root;            // Unity world, metres
        Vector3 head   = p.joints[FvpSkeleton.Nose];
    }
};
// or poll: fvp.People (frame on show), fvp.IsLive (false while paused / rewound)
```

`FrameEstimated` is not raised while browsing history; check `IsLive` before driving anything physical from `People`.

## Files

| | |
|---|---|
| `FasterVoxelPoseLive*.cs` | the component (core + capture, playback, mouse / keyboard input, overlay + transport bar, gizmos) |
| `FvpOverlay.cs`, `FvpOverlayGraphic.cs` | the per-display overlay canvas and its skeleton mesh |
| `FvpTransportBar.cs`, `FasterVoxelPoseLive.Bar.cs` | the transport bar (uGUI, one canvas per display) and its hit-testing |
| `FvpClient.cs`, `FvpProtocol.cs` | socket link and wire format (shared with the server) |
| `FvpCameraModel.cs` | camera model + the projection behind the overlay (UnityEngine-free) |
| `FvpHistory.cs`, `FvpSkeleton.cs` | frames / people / tracker, joint data |
| `FvpSceneState.cs` | what rewinds besides the Animators: moving transforms, script clocks, video, physics |
| `FvpGroundTruth.cs` | the green skeletons, read from the avatar rigs |
| `FvpServerProcess.cs` | launches / stops the server, echoes its log into the Console |
| `Editor/` | the Tools menu installer and the Inspector |
| `Server~/fvp_server.py` | the inference server (the `~` keeps Unity from importing it) |
| `Server~/fvp_selftest.py` | replays a recorded session through the server and prints MPJPE — no Unity needed |

## Troubleshooting

* **"Cannot start the server"** – a path in *Server* is wrong. **Tools ▸ FasterVoxelPose ▸ Open server log**
  (`Logs/fvp_server.log`) has Python's own error. Run the server by hand with
  `python Server~/fvp_server.py --repo <Faster-VoxelPose>` to see it live.
* **No people** – raise the volume to cover where they stand, lower *Min Score*, and check the gizmo box: the
  network only sees people whose feet-to-nose range lies inside it vertically (default floor − 0.2 m … 1.8 m).
  Synthetic avatars score lower (0.1–0.3) than the real footage the network was trained on.
* **Orange skeleton offset from the green one** – three different causes, in the order to rule them out:
  1. *Timing* (real-time mode only): switch **Mode** to *synced*. There image, green and orange come from the same
     instant, so whatever offset is left is not latency.
  2. *The network and its joint definitions*, which is most of it. Faster-VoxelPose was trained on Panoptic's human
     annotations and finds people on a 100 mm root grid and a 31 mm joint grid. On the recorded session
     (`python Server~/fvp_selftest.py --spawn --frames 0:2000:25`, real cameras, same protocol) it scores 156 mm MPJPE
     (median 117 mm) and the signed error is a *bias per joint*, not a shift of the whole body: the neck is within
     2 cm, shoulders / elbows / hips sit 5-9 cm higher than the avatar rig's bone origins, knees and ankles scatter by
     20+ cm. A calibration error would move every joint the same way, the neck included.
  3. *The camera calibration*: not a cause for rendered images. The images are rendered from the very cameras that
     are described to the server (checked against rendered spheres to 0.1 px, lens distortion included), so even the
     *placeholder* extrinsics of cam103 / cam120 are consistent with what they show. They would only matter for real
     footage shot by the real cameras.
* **An extra person that is not there ("ghost")** – a skeleton with a low score (0.1-0.2) standing where nobody is,
  usually 1-2 m from a real person, for a frame or a few. The network lifts people out of a 3D grid built from all
  views, and body parts of one person seen by two cameras can add up to a second person where their lines of sight
  cross; fewer views means more of them. On the recorded session (4 views, `fvp_selftest.py --frames 0:2000:8`) 9 ghosts
  appeared against 150 real people, in 12 % of the frames. Scores do not separate them: real people scored 0.10-0.23
  (median 0.18) and ghosts 0.11-0.18 (median 0.13); `Min Score` 0.15 removes two thirds of the ghosts and 8 % of the
  real people. To see which one it is, look at the same person on the other two displays and at the 3D gizmos: a real
  person is on a body in every view. Do not drive anything from a person that has not been there for several estimates.
* **No overlay on a display** – the overlay is a screen-space canvas on the camera's *Target Display*; make sure the
  Game view shows that display (Display 1 / 2 / 3) and that *Show Overlay* is on. The transport bar does not depend on
  it: it is drawn on all 8 displays (a canvas each) and only needs the Game view focused to take the mouse.
* **Estimates/s is low** – the GPU is shared with Unity's rendering of three 1080p lens-distorted cameras; lower
  `Frame Width` or the cameras' resolution.
* **A lens-distorted camera view is black** (typically right after a Library rebuild) – `LensDistortionRenderer` sets
  the shader uniform `_LutMap` once when it is created, and Unity can drop that call for a uniform the shader does not
  declare while the shader is still uncompiled. Declaring it in `LensDistortionRemap.shader` fixes it:
  add `_LutMap ("Lut map", Vector) = (1,1,0,0)` to the `Properties` block. (Found in a fresh test project; not changed
  in this repo.)
* **Going back does not move something** - look at the Console line "rewind covers: ..." at the start of Play. A moving
  object is found the first time it moves, so it is restored from the second estimate on; a script's clock needs to be in
  `Rewind Scripts` and be a private non-serialized field; anything driven by `Time.time` cannot be put back.
* **"a step advanced the scene by X ms instead of Step Seconds"** – something else owns the frame time
  (`Time.captureFramerate`, a fixed-step mode); steps are still exactly one frame, just not `Step Seconds` long.
* Stop the server: **Tools ▸ FasterVoxelPose ▸ Stop inference server** (it also quits by itself after 15 idle minutes
  and when the editor closes).
