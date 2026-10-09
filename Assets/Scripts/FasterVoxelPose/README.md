# FasterVoxelPose — live multi-view 3D pose estimation in Unity

Runs [Faster-VoxelPose](https://github.com/AlvinYH/Faster-VoxelPose) (Ye et al., ECCV 2022) on the calibrated
cameras of the scene and shows what it finds, live:

* **ground truth (green) and estimate (orange) drawn over each camera's own display** (cam107 / cam103 / cam120 each
  on the Target Display its `CalibratedCamera` already shows its image on), in the style of the dataset images: thin
  lines, round dots, line colour = average of its two joints; the person's left side lighter and the right side
  darker in both colours; a small **status widget** in the corner of every display (see below),
* the network's own **2D joint heatmaps painted over each camera's image** in a corner panel (see below),
* **several models side by side** under an `EstimationModels` parent: FasterVoxelPose (3D, multi-view) and ViTPose (2D, each
  view on its own), each with its skeletons, status card and heatmaps, switched from a tab strip on every display (see below);
  the **ground truth has its own tab** in that strip, independent of the models,
* the **3D reconstruction as gizmos** (Scene view, or Game view with *Gizmos* on) plus the capture volume box,
* **pause / frame-by-frame / rewind** controls (mouse: click, wheel, drag) that put the whole scene back, robot included,
  stepping by **one frame, ten frames, one second or one estimate** (the scene is recorded 30 times a second, not only when an
  estimate is made),
* a **phantom filter**: skeletons of people who are not there are taken out (see below),
* the poses as plain C# objects (`FvpPerson`) for whatever they will drive next (Bulles, PontRoulant…).

## Quick start

1. **Tools ▸ FasterVoxelPose ▸ Create FasterVoxelPose object (wire cameras, volume, animators)** – creates the `FasterVoxelPose` GameObject with
   `FasterVoxelPoseLive` and wires it from the open scene (cam107 / cam103 / cam120 *main* cameras, the
   `MOCAPcenter` anchor, the scene's Animators). Check the Console line it prints.
   (Or add the component to your own object and press **Auto-wire from scene**.)
2. Press **Play**. The Python server is started **when the editor opens the scene** (and again on entering Play if it is
   not running), not when Play begins: the model is loaded and the voxel grids and kernels of the last configuration are
   built while you are still editing, so Play finds it ready and the first configuration answers in 0 ms ("voxel grids
   and kernels were already built" in the Console). It stays up until the editor closes (`Start Server With Editor`).
   Only the very first run of a new camera / volume setup builds the grids once (about a second); it is remembered in
   `Library/FasterVoxelPose/last_config.json`.
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
| left drag | Scrub the timeline: drag left = back in time, one **frame** (`Step Seconds`) per `Drag Pixels Per Frame` (default 12 px) |

A click on the transport bar belongs to its buttons and is not also read as "pause".

**Keyboard and transport bar** (the bar is at the bottom of **every** display, so it is there whichever display the Game view
shows; it shrinks to fit a narrow Game view):

| Key | Button | Does |
|---|---|---|
| Space | `Pause` / `Play` | Pause freezes the scene (`Time.timeScale = 0`) and estimates the frozen frame; Play resumes |
| ← / → | `-1f` `+1f` | One **frame** (`Step Seconds`, default 1/30 s) back / forward. Hold to repeat. Forward from the newest frame lets the scene advance by that much and estimates it |
| | `-10f` `+10f` | Ten frames (1/3 s) |
| Shift + ← / → | `-1s` `+1s` | One second. Forward from the newest frame runs the scene on in steps of at most 1/3 s (`Time.maximumDeltaTime`), each one estimated |
| Ctrl + ← / → | `<E` `E>` | The previous / next **estimate** (what the arrows used to do) |
| R | `<<` | Rewind continuously through every recorded frame (speed: `Rewind Speed`); press again to stop |
| | `>>` | Replay forward |
| Home / End | `\|<` `>\|` | Oldest / newest frame |
| M | `Hide` | Show / hide the transport bar |
| | slider, `Mode` | Scrub anywhere on the timeline (the box to its right says how many seconds behind the newest frame you are); switch real-time / synced display |

Input comes from the Input System (the project's `Active Input Handling`); the legacy Input Manager is used if that is what
a project has.

### The timeline: estimates and recorded frames

An *estimate* is a frame the network was run on: the camera images, the skeletons, the state of the scene. It takes the
network some hundreds of milliseconds next to a rendering Unity, so there is one about every 0.7 s in a live session (1.4 a
second was measured: 21 frames of 1/30 s between two of them, 41 rendered frames at 59 fps). Stepping through estimates alone
therefore moves in jumps of most of a second, which is what the first version did.

So the scene is recorded **between** the estimates too (`Record Moments`, `Moments Per Second` 30, `Moment Window Seconds` 120):
a *recorded frame* is the state that puts the scene back (the Animators and everything in the table below), taken at the end of
a rendered frame, a few kB each and about 0.1 ms to take in the scene it was measured in (the widget shows the cost in your scene). Every control
lands on one or the other:

* **On an estimate** everything is as before: its image, its skeletons, its ground truth.
* **On a recorded frame without an estimate** the scene is exactly that instant: the cameras show it, the ground truth (green) is read
  from the restored rig, and the card says *no estimate for this frame yet*. The nearest estimate is drawn **faint** (40 %) beside
  it so you are not left with nothing, but it is not for this frame. When you stop for `Estimate After Seconds` (0.25 s) one is made
  for exactly that frame (both models: they share the capture) and put where it belongs in the history. Stepping on quickly does
  not queue estimates.
* Further back than `Moment Window Seconds` only the estimates remain, as before. `Record Moments` off gives the old behaviour.
* **Play** from any of them continues the scene from that instant, provided Animators are tracked (what came after is dropped).

A *frame* in the history count is one estimate; `History Frames` (default 2000) are kept.

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

### The status widget

One small card in the top-left corner of every display (**H** hides it, `Show Hud` in the Inspector) replaces the text that
used to sit over the pictures: the state (LIVE / PAUSED / REWIND), a legend, the estimates per second, the latency from
capture to answer, **where the server spent it** (backbone + root + joints, and how many people it localised), **Unity's own frame
rate** (and, when it is held to a rate, that), what this tool costs per frame, the place on the timeline (how far behind the
newest frame, which estimate, how many frames are recorded), and one row for each person found:

    #1  [bar | tick]  0.16   err 118 mm

* **The score (0.16)** is the network's confidence in that skeleton, between 0 and 1. It is *not* an accuracy: it is how
  sharp the network's own joint heatmaps are (the code is `SoftArgmaxLayer` in `joint_localization_net.py`: for each joint
  and each of the three planes it takes the highest softmax value of the heatmap, then averages over joints and planes).
  A confident joint puts nearly all of the probability in one cell (value near 1); a vague one spreads it out. The
  network was trained on photographs of people, so avatars come out vague: 0.10-0.23 here, where real footage would be
  well above that. Separately, a person must clear a gate inside the network (the product of the top-view and the
  height peaks of the root heatmap) before a skeleton is made at all; `Min Score` is applied to both. The bar is 0.5
  wide and the tick marks `Min Score`.
* **err** is the mean distance of the joints to the *green* skeleton of the nearest person, measured at the instant the
  frame was captured, so it does not suffer from the display lag. Green = good (< 12 cm), yellow, red (> 20 cm). It
  carries the joint-definition offsets described under Troubleshooting. *nobody there?* means there is ground truth in the
  scene but none within a metre of this skeleton: a ghost.

### Phantoms: skeletons of people who are not there

With few cameras the network answers with people who are not there: evidence from different people, or from one person along a
line of sight, adds up in the 3D volume at a place where nobody stands, and the 3D stages still return a full skeleton. Measured
on the recorded session (608 ground-truth people; `Server~/fvp_phantoms.py`, ground truth from the avatars' rigs):

| | skeletons | phantoms | frames with at least one |
|---|---|---|---|
| 3 views (like the live scene) | 830 | **254 (31 %)** | **59 %** |
| 4 views | 647 | 47 (7 %) | 15 % |

86 % of the phantoms stand within 1.5 m of a real person (median 1.1 m), and the network's own score does not separate them: it
would take `Min Score` 0.15 to remove 91 % of them, and that removes 10 % of the real people too.

What does separate them is the evidence the 3D stages started from. The server reads the 2D heatmaps back at the projection of
each skeleton's joints in every view (`Engine.person_support`: the strongest heatmap value within +-2 cells, averaged over the
joints inside the image) and sends that **2D support** per person and view. A real person has 0.5-0.7 in every view; a phantom is
backed in some views by somebody else's heatmaps and not at all in another (median 0.41 / 0.41 / 0.00 for three views). The rule is
one number: **mean support over the views under `Min Support` (0.35) = phantom**. On the same session:

| `Min Support` 0.35 | phantoms removed | real people removed | frames with a phantom |
|---|---|---|---|
| 3 views | 91 % (232 of 254) | 3.5 % (20 of 576) | 59 % -> 6 % |
| 4 views | 100 % (47 of 47) | 2.0 % (12 of 600) | 15 % -> 0 % |

The real people it removes are bad estimates anyway: their mean error is 441 mm (3 views) / 273 mm (4 views) against 160 / 154 mm
for the ones it keeps. (A pair of thresholds, or a threshold on the weakest view, did a little better on one set and not on the
other; one number is what these 304 frames justify.) `Phantom Filter`: **Hide** (default) takes them out of `People`,
`FrameEstimated`, the displays and the gizmos and keeps them in `FvpFrame.phantoms`; **Mark** keeps them, drawn faint and flagged in the card; **Off**.
The card says how many were hidden in the frame on show and why ("2D support 0.21 < 0.35, weakest cam120 0.00"). The numbers come from a 2000-frame
recording of this scene and can be checked or re-derived: `python Server~/fvp_phantoms.py --spawn --views 1,2,3 --out x.npz`,
then `python Server~/fvp_phantom_rules.py x.npz` prints this table for any rule. Not measured: real footage, other scenes,
and the effect of a threshold on a person who really is half hidden from a camera (they lose support in that view and
can be mistaken for a phantom; the 3.5 % above are those). The filter needs a server of this version (it sends `support`): an older
one left running is restarted once by the component, or use **Tools ▸ FasterVoxelPose ▸ Stop inference server**.

### The 2D heatmaps: what the 3D stage actually sees

VoxelPose and Faster-VoxelPose are *lifted 2D* methods. A ResNet-50 first gives every camera one 2D heatmap per joint (240 x
128 cells, a quarter of the network's 960 x 512 input). The 3D stages never see the pictures again: every voxel of the volume
is projected into each view, samples those heatmaps there and averages over the views, and the 3D CNNs read joints out of that
lifted evidence. So a joint that is weak or misplaced in the 2D maps is weak or misplaced in 3D.

The top-right corner of every display shows, for the camera(s) that display shows (all of them on a display that shows none),
the map painted over the very image it was computed from (inferno colours, transparent where the network sees nothing),
labelled with the joint and the strongest value in it. **J** steps to the next joint (**Shift+J** back; -1 is "the strongest
of all joints"), **G** hides the panel. `heatmapJoint`, `showHeatmaps` and `heatHistoryFrames` are Inspector fields.

* The server returns the maps already warped back into the geometry of the frame that was sent, at 1/4 of its size (240 x 135
  for 960 x 540), as one byte per cell: about 100 KB per estimate. Checked against the recording's true 2D joints through the
  real protocol: the neck map's peak lands a median of 6.4 px from the true neck in a 1280 x 720 frame (100 % within 20 px).
* The joint is chosen per estimate (the server sends only the one asked for): a change applies from the next estimate, and
  frames in the history keep the map they were made with (the newest `Heat History Frames`, 300 by
  default, keep theirs; older ones show a note). The image under the map is kept for the newest estimate only.
* How to read it: a tight bright blob on the body = the network is sure and right. A blob beside the joint = a 2D error of
  that many pixels, which at 4 m is 6.7 mm a pixel. A smeared or faint blob (peak under 0.4) = the 3D joint will be a guess.

### Keeping image, ground truth and estimate in sync

An estimate describes the frame that was *captured*, which is older than the live camera by the time it took to read the
frame back, send it, and run the network (the latency in the widget). Drawn on the live image that shows up as a
skeleton trailing a walking avatar. Paused, and scrolling back through the frames, everything agrees because the estimate,
the ground truth and the picture are all of the same frame.

**There is a floor under the lag that the network cannot lower: Unity's frame time.** The images are taken at the end of
frame *n*, the answer is consumed by the Update of frame *n + 2* at the earliest, so an estimate is on screen at least two
frames late, and the number of estimates per second can never exceed half the frame rate. At 60 fps that is 33 ms and
invisible; at 7 fps it is 285 ms and a walking person is about 30 cm ahead of their skeleton. The widget shows Unity's fps and
warns when this is the case.

**The server is slower next to Unity than alone.** Replaying recorded frames with the GPU otherwise idle, the server needs
48 ms for the network (53 ms in all, about 19 estimates a second for 3-4 views on the laptop RTX 5000 Ada). In a live session
with the editor rendering the 16-camera scene, the server's own log (`Logs/fvp_server.log`, one line per 100 frames) read
**pre 16 ms, net 286 ms (p95 470, max 613)**: about six times slower, because the two share one GPU and one CPU. So both
parts of the lag are properties of the digital-twin set-up on this laptop (Unity's frame time and a shared GPU), not of the
network. A real deployment with real cameras has no Unity rendering and keeps the 50-60 ms (plus the cameras' own capture
and decoding latency, which is not measured here).

**Why is inference slow when it should be the easy part?** The network is not slow: next to nothing else it needs 31 ms for
four views (backbone 14 + root 6 + joints 5 + the phantom filter's 2D support 5 ms; ViTPose 106 ms for four views). What slows it is the GPU being shared. Measured by replaying the recorded session through private servers while
a separate Unity process drew a synthetic load (100 alpha-blended full-screen quads into a 4096 x 4096 target, GPU at 100 %,
69 W; `scratchpad` experiment, 4 views):

| Unity draws | FasterVoxelPose network | (backbone + root + joints) | ViTPose round trip |
|---|---|---|---|
| nothing | 31 ms | 14 + 6 + 5 | 106 ms |
| as fast as it can (74 fps) | 86 ms | 43 + 21 + 22 | 292 ms |
| capped to 60 fps | 92 ms | 48 + 21 + 22 | 253 ms |
| capped to 30 fps | 70 ms | 33 + 18 + 18 | 232 ms |
| capped to 15 fps | 56 ms | 24 + 15 + 16 | 251 ms |

So a graphics load alone doubles the network and a lower Unity frame rate gives part of it back (`Live Frame Rate` 30,
`Paused Frame Rate` 15: a frozen scene needs few frames; a mouse move or key press brings the live rate back for a second;
0 leaves Unity alone, and the card says when Unity is being held). **It does not explain your sessions**: the server logs of your
own live runs read 214-664 ms for the network, 5-15 times the quiet figure, where this synthetic load gives 2 times. The rest is
not measured and not explained yet: candidates are that the real scene's frames are much longer than the synthetic ones
(the GPU is time-sliced between processes, so a server whose work is hundreds of small kernels waits for its turn between them),
and the CPU (the Python process has to feed the GPU while the editor's threads are busy). To find out in your session the card
now shows where the server's time goes (backbone / root / joints, from stage timers in the server): if all three grow in
proportion it is waiting for the GPU, not computing.
Also done: the 2D backbone runs on all views in **one** batch (a third of the kernel launches), and the camera images are read back
asynchronously whenever Unity runs above 25 fps (the blocking read that stalls Unity for the wait is kept for slow frames, where it
saves 2-3 frames of latency).

`Overlay Image` picks the trade-off (also the **Mode** button on the bar):

* **RealTime** (default) - the display keeps the live camera, nothing is delayed. The ground truth is read from the rig
  *now* (exact) and the estimate is moved forward by its root's horizontal velocity x its age, up to a second
  (`Latency Compensation`; the velocity comes from a small tracker over the last estimates, which accepts estimates up to
  1.5 s apart). Good for a person walking steadily; limbs still trail a little and it jumps when someone starts or stops.
  Paused, the display stays on the live camera, which is frozen on that frame.
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

## Several models side by side: the `EstimationModels` parent

Arrange the scene so that each child of one parent is another model running on the cameras:

```
EstimationModels          EstimationModelsHub
    FasterVoxelPose        FasterVoxelPoseLive   3D, multi-view; hosts the camera capture, the history and the transport
    ViTPose                ViTPoseLive           2D, every view on its own
```

**Tools ▸ FasterVoxelPose ▸ Add ViTPose + models hub (under 'EstimationModels')** builds it in the open scene: it finds or creates
the parent, moves the `FasterVoxelPose` object under it, adds the hub and a `ViTPose` child (the scene is only marked dirty,
not saved). The hub finds the children that implement `IEstimationModel` by itself; nothing is wired by hand.

**ViTPose** (Xu et al., NeurIPS 2022) is a *top-down 2D* method, the opposite of FasterVoxelPose: YOLOv8m finds each person in
each view, ViTPose-B (`vitpose-b-simple.pth`, COCO-17) reads 17 joints off a 256 x 192 crop of every box. On its own it knows
nothing of the other views, of calibration or of 3D: it gives joints in pixels, per view. To give the **same kind of output as
FasterVoxelPose** (3D people), those joints are **lifted by multi-view triangulation** through the calibrated cameras (below). It runs in `Server~/vitpose_server.py`, in your `vitpose` conda environment (mmpose 0.24,
mmcv-full, ultralytics: the pipeline of `cameras/camptz_vitpose_live.py`), on port 5578, started with the editor like the other
server. `Python Exe`, `Vitpose Repo` and `Yolo Weights` are Inspector fields.

* **Same frames.** ViTPose does not capture the cameras: it takes the synced frames `FasterVoxelPoseLive` captures
  (`IFvpFrameConsumer`), so both models see identical images, and the history, pause, step and rewind of FasterVoxelPose
  cover ViTPose: its result is attached to the frame it belongs to (`FvpFrame.modelResults["vitpose"]`). The camera host keeps
  capturing for ViTPose even when its own server is down.
* **What you see.** ViTPose's skeletons are cyan (the person's left lighter, the right darker) with the detector's boxes, drawn
  over each camera display next to the green ground truth and the orange FasterVoxelPose skeletons. On the live image the newest
  result is drawn; on a saved image, paused or rewound, the result of that frame.
* **3D by triangulation** (`FvpMultiViewLifter`, `Lift 3D`). Two steps, per frame: (1) *who is who across views*: for every pair
  of people from different views, the distance between the rays of their shared joints (the rays through the joint pixels,
  lens distortion taken out; they would intersect if it were one person); the median is the cost, and people are grouped cheapest
  first, one per view, only when every member fits every other; (2) *triangulation*: each joint is the point nearest to all its
  rays (least squares, weighted by the 2D confidence); a ray that passes farther than `Outlier Metres` (8 cm) from it is dropped
  and the joint recomputed, a joint whose rays disagree or that falls behind a camera is left out. A person needs `Min Views`
  (2) views. The result is the same type as FasterVoxelPose's: an `FvpPerson` with 15 Panoptic joints in the Unity world, an id and
  a velocity (the same tracker), and the error in millimetres against the green skeleton, in `ViTPoseLive.People` (and
  `FvpFrame.modelResults["vitpose"]`, so rewinding shows them). COCO has no neck and no mid-hip: the neck is the middle of the
  shoulders, the mid-hip the middle of the hips. They are drawn as **blue-violet** gizmos in the Scene view (and the Game view
  with Gizmos on) and projected back into **every** view through its lens (also the views that did not see them), next to the raw
  cyan 2D detections (`Show 2D Skeletons`, `Show Triangulated`, `Draw Gizmos`). The undistortion is a damped Newton solve: the
  usual fixed-point iteration fails for a strong barrel lens (k1 = -0.43 gave rays 26 px off at the corners).
* **Its status card and heatmaps** (top-left / top-right, when its tab is selected) are the same widgets as FasterVoxelPose's:
  estimates per second, latency (detector + ViTPose + lifting milliseconds), the 2D line (people per view and their **2D error in
  pixels** against the green skeleton projected into that view, same instant, COCO joints mapped to the rig's), the 3D line (people
  and the **reprojection error**, how far the 3D joints land from the 2D detections they came from, which needs no ground truth),
  one row per 3D person with its confidence and **3D error in mm**, and ViTPose's own heatmaps: the 64 x 48 map of each person crop laid back over the frame, for one joint or the strongest of
  all (`J` / `Shift+J`, `G`). Heatmaps of several people are merged by taking the strongest cell.
* **Every model answers every frame (`Models In Lockstep`, on).** A model takes a frame only when it is idle, and ViTPose
  (about 300 ms for three views next to a rendering Unity) is slower than FasterVoxelPose (about 100 ms): with each model
  grabbing whatever frame is current when it is free, ViTPose got every second or third frame, which is the "ViTPose shows a result
  every other frame" you saw. Now the camera host captures the next frame only when every running model has answered the last one, so
  they all see the same frames and the pace is that of the slowest. ViTPose's card says "took N of M captured frames" (a number
  below M means a frame was captured while it was busy: only possible with the option off, or with a server that is down).
  Switch it off to let FasterVoxelPose run at its own pace (it then estimates faster and ViTPose skips frames).
* **The hub's tabs** sit at the top of every display: click a tab to choose whose status card and heatmaps are shown
  (**T** cycles); click the coloured swatch of a tab to hide or show that model's skeletons. The last tab, **Ground truth**
  (green), is not a model: it has no panels, so a click anywhere on it only switches the green skeletons on or off, whatever the
  other tabs are doing (they used to disappear with FasterVoxelPose's). `J` / `Shift+J` and `G` drive the
  selected model's heatmaps. The Inspector of the hub lists the models with their state. A click on a tab is not also a
  pause / play click on the picture.
* **A third model** is a MonoBehaviour on another child that implements `IEstimationModel` (and `IFvpFrameConsumer` to take
  the camera frames); the hub picks it up within two seconds.

Measured on the recorded session (4 views, frames at 960 x 540, errors in pixels of the original 1280 x 720 image):

| 2D joint | ViTPose-B + YOLOv8m: median, within 20 px | FasterVoxelPose's 2D heatmaps: median, within 20 px |
|---|---|---|
| shoulders, elbows, wrists, hips | 5-9 px, 87-99 % | 13-18 px, 62-77 % |
| knees | 12-20 px, 50-52 % | 36-44 px, 26-34 % |
| ankles | 21-33 px, 47 % | 24-27 px, 40-42 % |

ViTPose's 2D joints are about twice as close to the truth on the upper body, two to three times on the knees and about equal on
the ankles. It matched 72 % of the people per view (the detector misses some) and costs about 160 ms for four views (detector 47 +
ViTPose 104 ms) against FasterVoxelPose's 50-60 ms. Run `python Server~/vitpose_selftest.py --spawn` (in the `vitpose` environment)
to repeat it.

**In 3D, after triangulation** (the same recorded session, ViTPose's real detections, the calibrated cameras, the 3D ground truth;
mean distance of 12 joints - shoulders, elbows, wrists, hips, knees, ankles - to the ground truth; the lifting itself takes
0.1-0.3 ms a frame):

| | MPJPE | median | people found | spurious |
|---|---|---|---|---|
| FasterVoxelPose, 4 views | about 163 mm | | | |
| ViTPose lifted, 4 views | **51.7 mm** | 39 mm | 148 of 152 | 0 |
| ViTPose lifted, any 3 views | 51-63 mm | 43-44 mm | 90-135 of 152 | 4-10 |
| ViTPose lifted, 2 views | 48-79 mm | 44-53 mm | 41-113 of 152 | 4-15 |

Per joint with four views (mm): shoulders 53-57, elbows 27-47, wrists 40, hips 43-48, knees 60-94, ankles 56-105. The weak spot is
recall, not accuracy: a person must be detected in two views at once, so with fewer cameras (or a detector that misses people in
some views) people go missing, whereas FasterVoxelPose always answers (with the larger error above, and its ghosts). These are
renders of this scene, not real footage, and both models inherit the joint-definition offsets between the avatar rig and their
training labels (see Troubleshooting). To repeat the table: `python Server~/vitpose_dump.py --out lift_input.json` in the
`vitpose` environment, then `Server~/lift_eval/run.ps1 -Json lift_input.json` (it compiles the lifter with the Roslyn that ships
with the Unity editor and prints the same figures, for all the views, every three and every pair).

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
| `FvpHud.cs`, `FasterVoxelPoseLive.Hud.cs` | the status widget, the cost counters and the comparison with the ground truth |
| `FvpHeatPanel.cs`, `FvpHeatTextures.cs`, `FasterVoxelPoseLive.Heat.cs` | the 2D heatmap panel: receiving the maps, colour map, textures, J / G |
| `EstimationModel.cs`, `EstimationModelsHub.cs`, `FasterVoxelPoseLive.Models.cs` | the model contract (`IEstimationModel`, `IFvpFrameConsumer`), the hub with its tabs, FasterVoxelPose as a model and the camera host |
| `ViTPoseLive.cs`, `ViTPoseLive.Visual.cs`, `ViTPoseResult.cs`, `FvpModelPanels.cs` | ViTPose: link, frames, results, ground-truth comparison, overlay, status card, heatmaps |
| `Server~/vitpose_server.py`, `Server~/vitpose_selftest.py` | the ViTPose server (YOLOv8 + ViTPose-B) and its replay test |
| `Server~/fvp_diagnose.py` | splits the error of a recorded session into 2D detector, 3D result and number of views |
| `FvpClient.cs`, `FvpProtocol.cs` | socket link and wire format (shared with the server) |
| `FvpCameraModel.cs` | camera model + the projection behind the overlay (UnityEngine-free) |
| `FvpHistory.cs`, `FvpSkeleton.cs` | frames / people / tracker, joint data |
| `FasterVoxelPoseLive.Moments.cs`, `FasterVoxelPoseLive.Playback.cs` | the dense timeline (recorded frames, lookups) and the transport on top of it (jumps, rewind, on-demand estimates, resume) |
| `FasterVoxelPoseLive.Phantoms.cs` | the phantom filter and the server-version check |
| `FasterVoxelPoseLive.Budget.cs` | the frame-rate budget that leaves GPU time to the models, and the lockstep setting |
| `Server~/fvp_phantoms.py`, `Server~/fvp_phantom_rules.py` | measure and tune the phantom rule on a recorded session |
| `Server~/proc_priority.py` | optional CPU / GPU scheduling class of a server process |
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
* **ViTPose: "cannot start the server" / no people** – `Python Exe` must be the `vitpose` environment's python (mmpose 0.24,
  mmcv-full, ultralytics), `Vitpose Repo` the checkout with `checkpoints/vitpose-b-simple.pth`, `Yolo Weights` an existing
  `yolov8m.pt`. **Tools ▸ FasterVoxelPose ▸ Open ViTPose server log** has Python's own error. A person is only given to
  ViTPose when the detector is at least `Detection Threshold` sure (0.4): lower it for small or partly hidden people. ViTPose
  needs the `FasterVoxelPose` object in the scene and enabled: it takes its camera frames from there.
* **Joints badly estimated even though nothing hides them** – the error is made in the 2D stage, not in the lifting, and it
  is large for a network that has never seen this kind of image. `python Server~/fvp_diagnose.py` splits it on the recorded
  session (38 frames, 2 people whose hips never rise above 0.56 m, i.e. seated or crouching, 4 cameras at 2.7 m, a person 170 px
  tall at 4.2 m, i.e. 6.7 mm a pixel):

  | joint | 2D heatmap peak, median error | heatmap value | 3D error (4 views) | 3D, 3 views |
  |---|---|---|---|---|
  | neck | 8 px (98 % within 20 px) | 0.79 | 37 mm | 55 mm |
  | shoulders, elbows, wrists, hips | 13-18 px (62-77 %) | 0.56-0.69 | 100-170 mm | 120-220 mm |
  | knees | 36-44 px (26-34 %) | 0.43-0.46 | 250-290 mm | 280-310 mm |
  | ankles | 24-27 px (40-42 %) | 0.38-0.39 | 210-280 mm | 250-300 mm |

  The 3D answer reprojected into the images is as close to the truth as the 2D peaks are (5-16 px for the neck and torso,
  25-36 px for the legs), so calibration and lifting add nothing: 15 px at 6.7 mm a pixel *is* the 10 cm of 3D error, and a
  40 px knee is 27 cm. The legs are worst because they are thin, dark against a grey floor, foreshortened and overlapping
  the chair when seen from 2.7 m up, and because this network (checkpoints trained on the CMU Panoptic dome: as far as I know
  real, mostly upright people, 5 views, larger in the frame; not measured here) has not been trained on avatars in PPE in
  these poses. Part of the torso error is the joint
  convention of the rig against Panoptic's labels (see the offset item above). Every camera you lose costs accuracy: the
  four 3-view subsets give 167, 174, 177 and 198 mm against 154 mm for four. What would fix it is fine-tuning the 2D
  backbone on renders from this scene (the recorder already writes the 2D joint positions it needs); moving cameras closer
  or lower, or adding a view, helps by making people larger and adding evidence. The heatmap panel shows it live.
* **An extra person that is not there ("ghost", "phantom")** – see *Phantoms* above: the filter (`Phantom Filter`, `Min
  Support`) takes them out and the card counts what it hid and why. With three cameras the raw network answers with one in 59 %
  of the frames. If a real person is hidden, set `Phantom Filter` to *Mark* to see which skeletons the rule would remove and
  their support, or lower `Min Support`. Do not drive anything from a person that has not been there for several estimates.
* **No overlay on a display** – the overlay is a screen-space canvas on the camera's *Target Display*; make sure the
  Game view shows that display (Display 1 / 2 / 3) and that *Show Overlay* is on. The transport bar does not depend on
  it: it is drawn on all 8 displays (a canvas each) and only needs the Game view focused to take the mouse.
* **Estimates/s is low, the estimate trails the avatar, the game is not smooth** – look at the widget: if *Unity* shows a
  low fps, that is the cause (see above), and *this tool* shows the part of it that is ours (a fraction of a millisecond per
  frame, and a few milliseconds per capture). What costs Unity time is rendering: each enabled camera renders the whole scene
  every frame (the scene file has 16 cameras and about 1,360 renderers), and every `CalibratedCamera` adds a full-screen lens
  remap on top of its render. The widget warns when many
  more cameras render than FVP reads: disable the ones nobody looks at (for instance the second set of cam107 / cam103 /
  cam120 under `CalibratedCameras` if only the recorder's set is used), lower the cameras' resolution, or set the Game
  view to a smaller size. The server shares the GPU with Unity: the *latency* line shows the server's part. Other
  applications do not matter: with Teams, Edge, VS Code and a PDF viewer open (GPU use under 2 %, CPU 6 %, AC power, High
  performance plan) the same replay ran at 31 ms for the network, steady to within 4 ms; the slowdown only appears while
  the editor renders. Inside the editor, a visible **Scene view** is a second full render of the scene next to the Game view:
  close it or tab away from it during Play (or use *Maximize On Play*), and switch the Game view's Gizmos off. The server
  logs `last N frames: pre .. net .. total ..` to `Logs/fvp_server.log` every 50 frames and at the end of every session, so
  every Play leaves its own number.
* **The text looks soft** – first check the *render* line of the widget (it is the size Unity renders that display at) against
  the size the Game view window really has. If the window is bigger, the Game view is **magnifying** the render and every
  pixel of text is stretched: a 14 px label comes out as 17-18 blurred pixels. A bar that measures 1.25 x its layout width
  (about 880 px at `UI Scale` 1) is the signature. Fix it in the Game view toolbar: **Scale** to 1x and **Low Resolution
  Aspect Ratios** off (on a display at 125 % Windows scaling that option renders at 80 % and stretches it back), and no
  fixed resolution smaller than the window. Then, if the text is now sharp but small, raise `UI Scale` (1.25 for a 125 %
  display): fonts are whole pixel sizes and every edge sits on a whole pixel, so a bigger UI stays crisp where a magnified
  Game view cannot. The text itself is a regular-weight Segoe UI with a pixel-snapped canvas; rendered 1:1 in the test
  project it is sharp.
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
* Stop the server: **Tools ▸ FasterVoxelPose ▸ Stop inference server** (it quits when the editor closes; with
  `Start Server With Editor` off it also quits by itself after `Server Idle Exit Seconds`, and Play starts it again).
  It starts again by itself the next time you enter Play.
* **Server flags** (`Extra Server Args`): `--cudnn on` restores the repo's kernel autotuning (default off: same speed and
  accuracy here, no stall the first time a new number of people appears, a cold configuration about half as long),
  `--warm-people N` (default 3) pre-tunes the per-person networks for 1..N people when a configuration is built,
  `--cache-dir <dir>` where the last configuration is kept. Running the 3D networks in half precision was tried and
  finds nobody, so only the backbone uses `--fp16`. Every 100 frames the server logs its own timing
  ("last 100 frames: pre ... net ... total ...") to `Logs/fvp_server.log`.
