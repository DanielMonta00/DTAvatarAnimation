# FasterVoxelPose — live multi-view 3D pose estimation in Unity

Runs [Faster-VoxelPose](https://github.com/AlvinYH/Faster-VoxelPose) (Ye et al., ECCV 2022) on the calibrated
cameras of the scene and shows what it finds, live:

* the **estimated skeletons drawn over each camera's own display** (cam107 / cam103 / cam120 each on the Target
  Display its `CalibratedCamera` already shows its image on), with a status line per display,
* the **3D reconstruction as gizmos** (Scene view, or Game view with *Gizmos* on) plus the capture volume box,
* **pause / frame-by-frame / rewind** controls,
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

On the slim transport bar at the bottom of Display 1, or with the **Game view focused**:

| Key | Button | Does |
|---|---|---|
| Space | Pause / Play | Pause freezes the scene (`Time.timeScale = 0`) and estimates the frozen frame; Play resumes |
| → | `>` | Pause (first press), then advance the scene **one frame** (`Step Seconds`, default 1/30 s) and estimate it. When you are behind the newest frame it walks forward through the history instead |
| ← | `<` | One estimate back |
| R | `<<` | Rewind continuously (speed: `Rewind Speed`); press again to stop |
| | `>>` | Replay forward through the history |
| Home / End | `\|<` `>\|` | Oldest / newest frame |
| M | Hide | Show / hide the transport bar |
| | slider | Scrub anywhere in the history |

A *frame* is one estimate: the skeletons, the camera models that were used and the state of the tracked Animators.
History keeps the last `History Frames` (default 2000, a few kB each).

**What rewinds.** While you scrub, the *tracked Animators* are put back to the state they had in that frame, so the 3D
avatar - and therefore the camera image under the overlay - follows the skeleton. **Play** then continues from the
frame under the cursor (the frames after it are dropped). Physics, particle systems and scripts are paused but not
rewound, and a live mocap stream cannot be rewound: for those the skeleton comes from the past while the camera
image shows the present. Animators set to *Unscaled Time* ignore the pause.

The overlay is drawn over the **live** camera image. While live, the skeleton is the newest estimate, i.e. a
few frames (the latency in the status line) behind the image; paused or rewound they show the same moment.

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
  ~65–110 ms per frame with 3–4 views (≈10 estimates/s); Unity keeps rendering while it works.
* **Newest frame first.** While the server is busy the cameras are not read, so the estimate always describes the
  latest scene a little late (shown in the status line).
* **Overlay.** One screen-space canvas per camera on that camera's Target Display. For a `CalibratedCamera` showing
  its lens-distorted image it uses the same letterbox as that presenter and projects through the camera model that
  was sent to the server (K, pose, lens distortion), so a joint lands on the pixel where the presenter draws it.
  Other cameras get a full-screen overlay projected with the camera's own viewport.
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
| `FasterVoxelPoseLive*.cs` | the component (core + capture, playback, overlay + transport bar, gizmos) |
| `FvpOverlay.cs`, `FvpOverlayGraphic.cs` | the per-display overlay canvas and its skeleton mesh |
| `FvpClient.cs`, `FvpProtocol.cs` | socket link and wire format (shared with the server) |
| `FvpCameraModel.cs` | camera model + the projection behind the overlay (UnityEngine-free) |
| `FvpHistory.cs`, `FvpSkeleton.cs` | frames / people / tracker, joint data |
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
* **Skeletons offset from the avatar on the displays** – the camera calibration, not the network: the overlay uses the
  exact camera model that was sent. cam103 / cam120 currently use *placeholder* extrinsics (fabricated from where the
  cameras were dragged to), so expect errors until they are really calibrated.
* **No overlay on a display** – the overlay is a screen-space canvas on the camera's *Target Display*; make sure the
  Game view shows that display (Display 1 / 2 / 3) and that *Show Overlay* is on. The transport bar is IMGUI, which
  only exists on Display 1.
* **Estimates/s is low** – the GPU is shared with Unity's rendering of three 1080p lens-distorted cameras; lower
  `Frame Width` or the cameras' resolution.
* **A lens-distorted camera view is black** (typically right after a Library rebuild) – `LensDistortionRenderer` sets
  the shader uniform `_LutMap` once when it is created, and Unity can drop that call for a uniform the shader does not
  declare while the shader is still uncompiled. Declaring it in `LensDistortionRemap.shader` fixes it:
  add `_LutMap ("Lut map", Vector) = (1,1,0,0)` to the `Properties` block. (Found in a fresh test project; not changed
  in this repo.)
* **"a step advanced the scene by X ms instead of Step Seconds"** – something else owns the frame time
  (`Time.captureFramerate`, a fixed-step mode); steps are still exactly one frame, just not `Step Seconds` long.
* Stop the server: **Tools ▸ FasterVoxelPose ▸ Stop inference server** (it also quits by itself after 15 idle minutes
  and when the editor closes).
