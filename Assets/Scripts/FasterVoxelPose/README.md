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
* the poses as plain C# objects (`FvpPerson`) for whatever they will drive next (Bulles, PontRoulant…),
* a **lean copy** (`FasterVoxelPoseLite`) with none of the displays, and a **follower** (`PoseEstimateFollower`) that moves
  `CapsuleOperatorPete` with the operator as the model sees them, in real time, with the measurements of how well it keeps up
  (see *Real time: the lean estimator and the capsule follower*).

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

One card in the top-left corner of every display (**H** hides it, `Show Hud` in the Inspector; `UI Scale` sizes it together with the bar, the heatmap panels and the hub's tabs - 1 is now 1.5 x the first design, which was too small to read on a 1920 x 1080 display) replaces the text that
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
`Server Priority` (Normal / Above / High) asks Windows for a higher CPU class and GPU scheduling class for the servers
(`Server~/proc_priority.py`; applies when a server starts). Windows accepts it without administrator rights (status 0), but whether it
helps is **not established**: the one experiment that tried it (a graphics load of long frames, with Unity's GPU class lowered, or the servers' raised) had
a load generator that had not reached full load when the replay started, and the machine was in use, so its numbers cannot be compared. Left at
Normal; try it, and read the card's backbone / root / joints figures before and after.
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

## The recorder's keypoints, live, with no models

To watch what `MultiViewRecorder` writes into `keyrgb/` while the scene plays (recording or not) you need none of the above: no FasterVoxelPose,
no servers, no card, no bar, no tabs. It is part of the recorder itself (`MultiViewRecorder.LiveKeypoints.cs`) and **Draw Keypoint Gizmos**, under
"Gizmos - Ground Truth Keypoints", switches all of it:

* **In the Game view**, in Play mode, the keypoints are drawn over each of the recorder's cameras' own displays: the joints of the recorder's
  **Skeleton Format** (AIC-14 by default), its head / toe offsets, **Point Radius Px**, **Line Thickness Px** and **Draw Skeleton**, projected the
  way the recorder projects them (a `CalibratedCamera`'s lens included), with the recorder's colours: left light, right dark, centre, each line
  the average of its two joints. It is a screen overlay, so `rgb/` and `keyrgb/` are untouched.
* **In the Scene view** (and the Game view with its Gizmos toggle on) the 3D gizmos draw as before, with **Gizmo Joint Radius**, and now also
  before recording starts (they used to need a session).
* Fisheye (`FulldomeCamera`) cameras are not drawn on screen. The overlay stands down while a `FasterVoxelPose` component is active, because that
  draws the ground truth itself (in the 15 Panoptic joints rather than the recorder's format). Leave the models' objects deactivated and none of
  their code runs.

(An earlier version was a separate `RecorderLiveOverlay` component. If a scene still has it, it shows as a missing script on the recorder's object:
**Tools ▸ FasterVoxelPose ▸ Remove the old live-overlay component from the MultiViewRecorders** removes it.)

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

## Real time: the lean estimator and the capsule follower

**The question:** can `CapsuleOperatorPete` follow the operator using nothing but what the cameras see, fast enough to matter? Three pieces answer it:

| | |
|---|---|
| `FasterVoxelPoseLite` | the same Faster-VoxelPose server and models, with everything that is for *looking at* the model taken out and the loop from picture to people shortened |
| `PoseEstimateFollower` (+ `OperatorTrack`) | puts an object where the operator is **now**: which person is the operator, a filter, and an extrapolation over the pipeline's delay |
| `IPoseSource` | what the follower reads: the lean estimator, `FasterVoxelPoseLive` or `ViTPoseLive` (lifted to 3D). *Source* = Auto picks the lean one while it runs, else the full one, else ViTPose |

### Set-up

1. **Tools ▸ FasterVoxelPose ▸ Real-time operator capsule (lean estimator + follower on CapsuleOperatorPete)** creates `FasterVoxelPoseLite` (cameras, volume and server settings copied from the `FasterVoxelPose` object, so the server reuses its grids), puts a `PoseEstimateFollower` on `CapsuleOperatorPete` and wires its *Truth Avatar* to `OperatorPete`'s Animator. The scene is marked as changed, not saved. **Attach pose follower to the selected object** puts one on any other capsule (followers never take the same person).
2. Press **Play**. The server answers one client at a time, so the lean estimator switches the full `FasterVoxelPoseLive` off for that session (*Take Over From Full Component*; the scene is not changed). Disable the lean one to get the displays, overlays and the hub back.
3. The capsule goes to the nearest person the model confirms (two estimates in a row), follows them, goes **grey** while it does not know where they are, and the Console prints a report when Play stops (the Inspector shows it live; *Print report* any time).

**The real operator never moves the capsule.** *Truth Avatar* is only read to measure the capsule against (the twin knows the truth; a real installation would not).

**Hidden from the pose cameras (on by default).** In the twin the model reads *renders* of the scene, so a bubble around the operator is in the very pictures it estimates from, and a real camera would never see it. Measured with the scene's own bubble (`CapsuleOperatorPete` in red glass and its child `SubCapsuleOperatorPete`, x1.2, in yellow glass: the project's two `Bboxglass` materials, alpha 0.45) in the scratch scene, 50 s of walking, same server: **hidden from the pose cameras** the model found the operator in 100 % of 481 estimates and the capsule was 5.0 cm median / 13.5 cm p95 from the real torso (the operator inside it 100 % of the time, never lost); **visible to them** it found the operator in only **19 %** of 718 estimates (8 % only as a phantom, 73 % nobody near), the capsule was **25 cm median / 67 cm p95** off, the operator inside it 50 % of the time, lost 19 times. (An opaque capsule: 15-23 %.) Two nested glass shells tint the person to something the network has not learnt to read. So the follower switches its renderers off while a camera that feeds the model renders, and on for every other camera (Scene view, other displays). Layers and physics are untouched. Untick *Hide From Pose Cameras* only for a demonstration where the estimates do not matter.

**What you see in the Game view: the bubble is put back afterwards.** If every display of the Game view is one of the pose cameras (the usual setup here), the real bubble is on none of them, because it must not be in the pictures the model reads. It is drawn **after** the model's picture has been taken (*Bubble Display* = *Rendered Afterwards*, the default): for each pose camera a second, hidden camera renders **the real objects themselves** (the red and the yellow glass with their own materials, the scene's lighting, and the grey tint they get while the operator is not followed) through the same projection and the same lens distortion into a texture of its own, and that texture is shown over the camera's display (`FvpBubbleRender`; the shader that puts it over the picture is `Hidden/FasterVoxelPose/PremultipliedOver`). The picture the model reads, the recorder's and everything else's is not touched. This costs the bubble's meshes and two full-frame passes at *Bubble Resolution* (0.5 = a quarter of the pixels), not a second render of the scene. A caption says who is followed (`following #1`, `lost #1 for 2.3 s`, `searching`).

How the second camera gets the bubble and nothing else: for the duration of *its* render only, the bubble's GameObjects are moved to a private (unnamed) layer that only it renders, and moved back, so the Scene view and every other camera see them where they always were. (Measured in this project: `Graphics.DrawMesh` / `RenderMesh` naming a camera draw nothing under URP, without a camera they are drawn by every camera that includes the layer, the Scene view among them, and `Camera.SetTargetBuffers` is ignored.) **Hiding behind things:** the second camera has no scene, so the glass would be drawn over everything, including the operator inside the bubble, whose body hides the far side of the red shell (both its faces are drawn) in the real render. The objects listed in *Occluders*, by default the *Truth Avatar*'s renderers, are drawn into the second render depth-only, so the glass is hidden behind them as in the Scene view. What is not listed does not hide it, and the scene's shadows do not fall on it. A camera that cannot do this (not lens-distorted, no render pipeline) shows *Outline* instead: a flat silhouette per piece in the colour and transparency of its material.

The lean estimator draws **the skeletons it found** over the same displays, with one status line per display (*Draw Skeletons*, *Draw Status*; *Draw Phantoms* adds the ones the phantom filter took out, faint), and, in the Scene view, as orange 3D lines (*Draw Scene Gizmos*) next to the green ground-truth skeleton (green = the avatar's own joints, orange = what the model estimated: the project's colour convention). All of these are screen overlays on the camera's Target Display, over the image the display shows (lens distortion and letterbox included, like the other overlays here). The Game view has to show that display (Display 1 / 2 / 3 in its drop-down); the real object itself is only seen in the Scene view, or on a display whose camera is not one of the pose cameras. (The recorder's own live keypoints, *Draw Keypoint Gizmos*, are the ground truth; they stand down only while `FasterVoxelPoseLive` is active, so they come back when the lean estimator switches it off: then the two can be compared on the same display.)

*Checked* in the scratch scene (the project's three calibrated cameras, the scene's own red + yellow glass bubble, a walking avatar) against the real render, i.e. the same scene with the real bubble drawn into the pose camera: the bubble rendered afterwards, put over the raw picture, is **the same picture to within 0.35-0.8 out of 255 on average** (95th percentile 1-5; with the bubble's texture at half the resolution, the default, 0.8-1.7 on average), its extent matches (intersection over union 0.97-0.99) and nothing outside it differs; composited through a UI canvas with the overlay's own shader it gives the same numbers; and the picture the model reads is **bit for bit what it is without the bubble** (largest difference 0 out of 255). Without the avatar as an occluder the same comparison was 5-10 out of 255 on average (the far side of the glass showed over the operator). 50 s of walking with it on: the model found the operator in 100 % of 548 estimates, the capsule was 4.5 cm median / 14 cm p95 from the real torso, the operator inside it 99 % of the time, never lost: the same as with the bubble hidden (and not the 19 % / 25 cm of having the real glass in the model's pictures). It costs **0.6 ± 0.2 ms per rendered frame** for the three cameras at half resolution (unthrottled stepping in the scratch scene, which is far lighter than yours: measure it in yours); the *Outline* style costs 0.05-0.2 ms. Of the outline style, checked the same way: it covers 99 % of each piece's silhouette (IoU 0.99 for the yellow shell, 0.92-0.98 for the red one), because each piece is taken as a capsule in its own local space, where its squashed end caps are spheres. A joint of the drawn skeleton lies 5-15 px from the real hips in the 1920 px images. I could not look at the real canvas in your Game view from here: the composite was checked through a UI canvas with the same shader, in a render texture, not on your display.

### What the lean estimator drops, and what it does differently

| | `FasterVoxelPoseLive` | `FasterVoxelPoseLite` |
|---|---|---|
| transport bar, status card, hub tabs, 2D heatmaps (and asking the server for them) | yes | no |
| skeletons over the displays | yes, also the ground truth, the saved frame under them, the history | the people of the newest estimate and a status line (a mesh rebuilt only when an estimate arrives or something moves) |
| history, rewind, a snapshot of the scene's moving objects 30 times a second, ground truth read from the rigs at every capture, full-resolution copies of the images | yes | no |
| image read-back | async when Unity runs above 25 fps (the result arrives 2-3 rendered frames later) | **blocking** by default: read in the frame the picture is taken (the main thread waits for the GPU: 3 ms when it is idle) |
| answer -> next picture | the answer is used in the next `Update`, the next picture is taken at the end of that frame | the answer is used at the end of the frame it arrives in and the next picture is taken in that same frame |
| vertical flip of the picture | row by row on the CPU | by the GPU while it downscales (checked against a known image) |
| Unity's frame-rate cap | fixed (`Live Frame Rate`) | `Live Frame Rate` is the most; **lowered by itself** when the server's own time per frame goes over `Slow Server Ms` and tried higher again later (see below) |
| what it hands out | `People`, `FrameEstimated`, `IPoseSource` | `People`, `EstimateReady`, `IPoseSource`, `BuildReport()` |

The network is the same and so is the server, so **what the lean one cannot do is make the model itself faster**: 30 ms of network (34 ms for the whole answer) for 3 views on an idle GPU, whatever the client. What it removes is everything around it.

### How the capsule follows

* **The followed point** is the middle of neck, shoulders and hips (*Anchor* Torso; MidHip, Neck, AllJoints also there): the joints the network gets best, and the body's axis. The position on the floor is followed; the size follows the detected body (see *Fit To Person* below).
* **A body, not an id.** The list of people is not clean (see the measurements): a person can come twice, a second fading skeleton on the same body for a few estimates (seen in the scratch Unity run below: the old id turned into a weak duplicate and the sharp skeleton got a new one), the model numbers the person again each time it loses and finds them, and now and then a skeleton stands where nobody is. So skeletons closer than 0.35 m are one body (the sharper one stands for it), the followed id keeps it unless another is clearly sharper, a new body replaces the followed one only after the same place has been seen in two estimates in a row, and a body that stood 0.6 m or more away from the followed person at the same time is **somebody else** and is only taken if it is practically on the prediction (a neighbour walking past while the person's estimate drops out is not mistaken for them). `OperatorTrack` has the rules: plain C#, no scene code, and `Server~/track_eval/run.ps1` runs its scenarios (and the replay of recorded estimates below) outside Unity.
* **A Kalman filter** per horizontal axis (position and velocity). An estimate describes the scene as it was when the pictures were taken; every rendered frame the capsule is put where the filter says the operator is *now*: the filtered position moved on by their velocity x the age of the estimate (at most *Max Predict Seconds*). One bad estimate has a bounded pull (an innovation counts as at most 3 sigmas, and the velocity cannot change faster than a person accelerates).
* **When the operator is not seen**: after *Hold Seconds* (1 s) without a match the capsule keeps its last position, goes grey (*Tint When Not Tracking*), and `trackLost` fires; a body that shows up near it is taken back (`trackAcquired`); after *Forget Seconds* (8 s) it searches again around itself. *Log Transitions* prints one line each time, with who was in the last estimate and where the filter expected the operator. **The time without a match is counted from when the last matching estimate *arrived*, and is at least *Hold Intervals* (2.5) intervals between estimates.** Counted from the instant its pictures were taken (the first version), the pipeline's own delay was held against the operator: on a loaded GPU (2 estimates a second, 0.4-1.4 s old when they arrive) the capsule flickered between lost and following 15 times in 45 s while the model saw the operator in every estimate (your Editor.log of the first Play); in the replay of that pipeline (`track_eval` scenario O) the old rule calls a standing person lost for 13 % of the time and the new one never.
* **The capsule's size follows the detected body (*Fit To Person*, on by default).** The radius and both tips are those of the smallest capsule that has the whole **detected skeleton** inside it, in any pose: a hand held out or up widens or raises it, sitting or crouching lowers it. **The size is a function of the pose model's joints and nothing else**: not the scene's real operator (*Truth Avatar* only counts frames), not the scene's colliders, not the authored size (that is only what it goes back to while nobody is followed). `FvpCapsuleFit.cs` has the geometry, `PoseEstimateFollower.Fit.cs` the glue.
  * *The body* is 15 balls, one per joint, as far as the body reaches around it (wrist 14 cm: the hand; ankle 15: the foot; nose 17: the head; hips 15, ...) plus *Fit Margin* (12 cm: the error of the estimate and of the capsule's place, and the time between two estimates). The joints are measured from the capsule's own axis (the followed point), so the pipeline's delay does not change how far a hand is from the torso.
  * *Both solids.* The scene's capsule is Unity's capsule mesh scaled 1 x 0.828 x 1 (half-ellipsoid caps) with a CapsuleCollider (hemispheres, a sphere when it is fatter than tall). They are not the same solid, so the radius is the larger one of what each needs. The child (*SubCapsuleOperatorPete*, x1.2) scales with its parent and keeps its 20 %.
  * *Which tips.* The two tips are searched for the smallest volume (radius^2 x height). With *Fit Bottom* on (default) both come from the skeleton; off, the lower tip is never higher than the height the object was authored at (a constant of the scene, not of the skeleton). Deeper than the floor is normal and invisible: feet far from the axis (sitting, legs out) need a deeper lower cap.
  * *In time.* It **grows in the frame a limb goes out** and shrinks over *Fit Shrink Seconds* (0.5 s) when it comes back; a joint the model drops keeps its last place for 1 s; the radius is capped at *Fit Max Radius* (1.5 m, so a joint of a bad estimate cannot make a room-sized bubble; the report says how often it was at the limit). Every rendered frame the radius is re-derived from the tips as drawn, so the remembered body is inside whatever the smoothing does to the tips.
  * *What it is not.* The axis stays on the followed torso point: a person sitting with the legs out gets a wider capsule, not a shifted one. Between two estimates (100-300 ms) a fast hand can leave the margin: with a Truth Avatar the report says how often the real body was inside the capsule that is drawn and how far it stuck out when it was not.
  * *Tested* (`Server~/track_eval/run.ps1 -What fit`, no Unity): ten hand-made poses and 4,000 random ones (arms and legs in any direction, torso from upright to lying), the capsule redrawn at 60 fps against an estimate every 100 ms: the body is inside in every frame, checked against the two solids written from their definitions and a full circle of points per joint; sitting is 46 cm lower than standing, a raised hand grows it in the same frame, 3 cm of noise on a still person moves the radius 3 cm (p95) and the top 7 cm, and three capsules authored very differently converge to the same size for the same joints (scenario K). Cost: 0.1 ms per estimate, 1 microsecond per frame. In the Unity scratch project (`FitTestRun`, the real component on a copy of the scene's pair, a scripted pose source, Pete's rig): the body inside the real collider and mesh in every frame of the scripted poses, the transform's scale and height equal the fit to the millimetre (renderer bounds), the authored size comes back when the component is disabled, and with Pete and the Truth Avatar removed the same pose gives the same capsule.
* Settings you may want: *Acquire Radius* (only people within this of the capsule can be taken; 0 = nearest), *Confirm Estimates*, *Measurement Noise* (0.06 m; the measured scatter is 3-4 cm), *Maneuver Accel* (1.5 m/s²: larger follows turns faster and smooths less), *Smooth Time*, *Lost Behaviour* (hold or go home).

### Is it real time? What was measured

All of it is on this laptop (RTX 5000 Ada). The first two tables are the recorded session (`MultiViewSession_20260430_172237`: renders of this scene, two people, one of them walking 56 m in 36 s); the last two are a scratch Unity project (the three cameras' own calibration, `PeteCharacter` walking a figure-8 at 1.2 m/s with a 3 s stop, real server, Unity stepped at exactly real time). **None of it is real camera footage, and the scratch scene is much lighter than yours** (see the last table for what a heavy scene does).

**1. How good is the point the capsule follows?** (`python Server~/capsule_eval.py --spawn --views 1,2,3 --csv x.csv`; matched to the avatars' rigs, same phantom rule as live)

| | 3 views (like the live scene) | 4 views |
|---|---|---|
| torso point vs truth, horizontally (1,089 / 1,177 matched people) | median **3.5 cm**, p95 6.9, p99 8.5, max 75 | median 3.7, p95 8.0, p99 10.7, max 13.7 |
| change from one estimate to the next (jitter) | median 0.7 cm, p95 2.7 | median 0.7, p95 2.5 |
| constant offset / scatter around it | 1 cm / 2-4 cm | 2 cm / 3 cm |
| people with a kept estimate (of 1,216) | **90 %** | **97 %** |
| ghosts left after the 2D-support rule | 29 estimates, 5 % of frames (44 % of frames before the rule) | 0 (14 % before) |
| server time per frame, whole answer (network alone) | 34 ms, p95 38 (30 ms) | 40 ms, p95 45 (35 ms) |

The per-joint error of the whole body (150-160 mm MPJPE) is much worse than this: the torso is what the network finds best. **Dropouts, not accuracy, are what a follower has to survive**: with 3 views the walker had no estimate in 21 % of the frames (in gaps of 1-2 s) and 18 real people were taken for phantoms by the 2D-support rule. The people of this session move: 1.55 m/s on average for the walker, 2 m/s at the 95th percentile of a frame step.

**2. The real estimates through the tracker, with a delay put in** (`replay` of that CSV with a latency and an estimate rate; cm between the capsule axis and the person's real torso point, 4 views, the two people together, 0.0 % of the time on the wrong person)

| estimates | delay | capsule: median / p95 | the newest estimate as it is: median / p95 |
|---|---|---|---|
| 9.9 /s | 100 ms | 4.5 / 18.5 | 8.7 / 32.7 |
| 9.9 /s | 250 ms | 6.0 / 34.2 | 13.1 / 56.5 |
| 6.6 /s | 150 ms | 5.3 / 26.3 | 11.5 / 48.1 |
| 3.3 /s | 350 ms | 6.5 / 64.9 | 5.1 / 98.9 |

The prediction roughly halves the median and cuts p95 by 40-45 % up to a quarter of a second of delay. At 3.3 estimates a second it no longer helps the median (6.5 against 5.1 cm: half of the samples are the person who hardly moves, and the raw estimate is already right for them) but still cuts the tail from 99 to 65 cm; the person has moved too far between two estimates for any prediction to hold it tight.

**3. End to end in Unity, paced at real time** (50 s, Pete walking, three calibrated cameras, the capsule following; `inside` = the operator's body, torso axis + 25 cm, inside the 0.5 m capsule)

| | Unity | estimates /s | capture -> people | capsule: median / p95 / max | inside |
|---|---|---|---|---|---|
| **lean, blocking read-back** | 30 fps | 12.4 | **70 ms** (p95 100) | 4.5 / 11.0 / 21.5 cm | 100 % |
| lean, async read-back | 30 fps | 5.4 | 170 ms (p95 205) | 6.0 / 24.0 / 38.5 cm | 96 % |
| `FasterVoxelPoseLive` as it is | 30 fps | 5.8 | 165 ms (p95 200) | 5.5 / 22.5 / 38.2 cm | 97 % |
| **lean, blocking** | 60 fps | 15.0 | **70 ms** (p95 85) | 5.0 / 11.0 / 17.5 cm | 100 % |
| `FasterVoxelPoseLive` as it is | 60 fps | 9.0 | 115 ms (p95 130) | 5.5 / 16.0 / 23.8 cm | 100 % |
| ViTPose lifted to 3D (follower *Source* = ViTPose, FasterVoxelPose capturing for it and running too) | 30 fps | 4.3 | 230 ms (p95 265) | 7.5 / 34.0 / 50.3 cm | 87 % |

At the same Unity frame rate the lean path is about **2.4 times lower in latency and twice the update rate** (30 fps), which is what turns into 11 cm instead of 22 cm at p95. ViTPose (YOLOv8 + ViTPose-B on every view, then triangulated) is the slow one here, 230 ms, and shares the GPU with FasterVoxelPose which has to run to capture the frames for it; the prediction still brings its raw estimate (37 cm median behind the walker) down to 7.5 cm. The capture itself costs 3 ms of the main thread in this light scene (see the next table for a heavy one); the component 0.04 ms per frame, the full one 0.2-0.5 ms per frame. The 70 ms is the server (56-58 ms in this loop), the capture (3 ms), the socket (0.5 ms) and the time until the end of the frame at which the answer is used. Only the *newest* picture is ever sent, so nothing queues.

**4. What a heavy scene does: the GPU is shared.** The same test with an in-scene GPU load (alpha quads drawn into a 4096 x 4096 target every frame) standing in for a heavy scene:

| load (lean estimator unless noted) | Unity | server time per frame | capture stall | estimates /s | capture -> people | capsule: median / p95 | inside | operator lost |
|---|---|---|---|---|---|---|---|---|
| none | 30 fps | 58 ms | 3 ms | 12.4 | 70 ms | 4.5 / 11.0 cm | 100 % | 0 s |
| 150 quads | 30 fps | 75 ms | 33 ms | 7.4 | 135 ms | 5.0 / 17.0 cm | 99.8 % | 0 s |
| 150 quads | **60 fps** | **652 ms** | 45 ms | 2.1 | 615 ms | 152 / 392 cm | 6 % | 37 s |
| 300 quads | **30 fps** | **486 ms** | 61 ms | 1.6 | 565 ms | 73 / 472 cm | 18 % | 14 s |
| 300 quads | 20 fps | 90 ms | 55 ms | 8.2 | 150 ms | 5.5 / 22.0 cm | 97 % | 0 s |
| 300 quads | 15 fps | 56 ms | 57 ms | 7.4 | 135 ms | 4.5 / 16.0 cm | 99.6 % | 0 s |
| 300 quads, `FasterVoxelPoseLive` | 30 fps | (not measured) | (not measured) | 1.4 | 665 ms | 100 / 456 cm | 7 % | 29 s |

(The *capture stall* is the blocking read-back waiting for the GPU to finish the frame's work: 3 ms when the GPU is idle, 30-60 ms when Unity keeps it busy. It is time the main thread would otherwise spend waiting for the frame cap, not extra work, but it is why a heavy scene shows it.)

**Past a point the server does not slow down, it collapses** (58 ms to 486-652 ms), and the capsule loses the operator. Holding Unity's frame rate down gives the GPU back: 20 or 15 fps took the same heavy scene from 486 ms to 90 or 56 ms. This is what the server logs of your own live runs show (214-664 ms), so for your real scene (16 cameras, 1,360 renderers) **expect to be on the wrong side of this cliff unless Unity's frame rate is held down or the scene is made lighter** (fewer rendering cameras, a smaller Game view, lower camera resolutions). The lean estimator's governor does the first by itself: it lowers the cap while the server's own time is over *Slow Server Ms* (150 ms) - by 20 % at a time, by 30 % or 45 % when the server is 1.6 or 2.5 times over, and never on a single answer (the first answer after a start is always slow) - and tries a slightly higher one every 12 s of comfortable answers, with an exponential back-off after a try that failed. The highest cap that was seen to hold is remembered (`PlayerPrefs`, this machine) and the next Play starts from it, so the 10 s of late estimates it takes to find the cap are paid once, not at every Play. Tested against a simulated GPU with this cliff (`track_eval\run.ps1 -What governor`): heavy scene -> at a good cap within 1 s of the third answer and settled; the next start from the remembered cap -> nothing to find; light scene -> stays at 30; load goes away -> climbs back to 30.

**5. Your own scene, first Plays** (the lean estimator's reports in `Editor.log`; 3 cameras, the real 16-camera scene with its video, the operator mostly standing still, so the capsule's accuracy says little here, the *rates* and *delays* say a lot):

| | Unity | server time per frame | estimates /s | capture -> people | operator lost | capsule vs the real operator |
|---|---|---|---|---|---|---|
| first Play | 30 -> 24 -> 19 -> 15 -> **12 fps** (the governor stepped down four times) | 221-318 ms, still **280 ms at 12 fps** | **2.1** | median **420 ms**, p95 660, max 1,418 | flickered 15 times in 45 s (the capture-time hold rule, fixed above), 78.8 % of the time known | median 4.0 cm, p95 35, p99 54; body inside the capsule 88 % |
| a later Play | 30 -> 24 -> 19 -> 15 fps | 155, 151, 150 ms (marginal), 124 ms at 15 fps | **6.1** | median **190 ms**, p95 245 | 0 times | median 2.0 cm, p95 4.5; inside 98.6 % |

So the cliff described above is real in this scene (the first Play was on the wrong side of it, the governor found its way out), and what separates the two Plays is not the code but how much of the GPU the rest of the machine was using. That Play also ran against a **server of an older version** (see *Troubleshooting*), which sent no 2D support: the phantom filter did nothing, and none of the server's stage times were shown.

**What this does and does not show.** It shows that the *method* works and where it breaks, with the delays measured here. It does not show how the model does on real photographs of the real lab (never measured: every image so far is a render of this scene), nor what your scene costs the GPU (measure it: the Inspector shows the server's time and the cap). It is not a safety function: the tails (p99 of 30-60 cm with the dropouts of 3 views, ghosts, a collapsed server, a lost track) are what a protective system with real consequences would have to be designed around.

### Reading the report

When Play stops, or *Print report*:

```
PoseEstimateFollower on 'CapsuleOperatorPete' - FasterVoxelPoseLite: 763 estimates, 12.4/s
  latency, capture -> people in hand:  median 70 ms, p95 100, max 212
  age of what the capsule is placed from (latency + wait for the next estimate): median 105 ms, p95 170, max 233
  time: following 60.0 s, searching 0.0 s, operator lost 0.0 s; acquired 1x, lost 0x
  capsule axis vs the real operator's torso:  median 4.5 cm, p95 11.5, p99 14.5, max 17.4
  the newest estimate as it is, no filter, no extrapolation:  median 12.0 cm, p95 20.5, p99 22.5, max 30.3
  operator's body (axis + 0.25 m) inside the capsule (radius 0.50 m): 100.0 % of 1801 frames since the first lock;
  radius needed for 99 % of them: 0.40 m
  of the session, the capsule knew where the operator was 100.0 % of the time
```

(That is the scratch scene's report. `FasterVoxelPoseLite` prints a second block when Play stops: the same latency broken into capture / socket / server time, the server's stages, and the cap Unity is held to.)

*Radius needed for 99 %* is what to compare with the capsule's real radius (0.5 m, the yellow one 0.6 m): if it is larger, the protection leaks that often at the measured delay.

**If you cannot see the capsule or the skeletons in the Game view:** the Game view must show a display of one of the pose cameras (Display 1 / 2 / 3 in its drop-down); there the real capsule is deliberately not drawn and its green outline is (*Draw On Pose Cameras*, with *Hide From Pose Cameras* on), and the skeletons come from the lean estimator (*Draw Skeletons*); the full component's skeletons are not there while the lean estimator has switched it off. No outline at all: the capsule is not in front of that camera, or the camera has no calibration loaded (FasterVoxelPoseLite's status line in the Inspector says so).

**If the capsule does not follow:** *searching* for ever - nobody confirmed near it (raise *Acquire Radius*, check *Min Score* / the phantom filter, check the volume), or the capsule is on the cameras' pictures (*Hide From Pose Cameras*); *lost* often - read the transition lines (who was in the estimate), look at estimates per second and at the server's time (a server over 150 ms means the GPU is saturated); *another person* - raise *Confirm Estimates*, set *Acquire Radius*; the **server is busy** - FasterVoxelPoseLive is enabled on the same port (tick *Take Over From Full Component*).

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

To follow people without caring which model made them, read an `IPoseSource` (implemented by `FasterVoxelPoseLite`, `FasterVoxelPoseLive` and
`ViTPoseLive`): `TryGetPose(out PoseSample s)` gives the people of the newest estimate, the scene time they were captured at (on the source's
`PoseClockNow`, which stops with `Time.timeScale = 0`), a serial that changes with every estimate, and the pipeline latency. The lean estimator
also raises `EstimateReady` (in the frame the answer arrives in) and has `BuildReport()`. `PoseCameras` lists the cameras the model reads and
`IsPoseCamera(cam)` says whether a camera's *image* goes to it (anything the twin draws that a real camera would not see has to stay out of those
images: hide it from them as `PoseEstimateFollower` does and draw it over their displays with `FvpDisplayLayer`). `OperatorTrack` (which of the
people is *the* operator, and where they are now) can be used on its own; `PoseEstimateFollower` is the Unity part around it.

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
| `FasterVoxelPoseLite.cs`, `FasterVoxelPoseLite.Capture.cs`, `FasterVoxelPoseLite.Display.cs`, `FvpLiveStats.cs`, `FvpFrameRateGovernor.cs` | the lean estimator (capture, link, people, report), the skeletons and status line over the displays, its histograms, and the governor that holds Unity's frame rate where the server stays fast |
| `FvpBubbleRender.cs`, `Shaders/FvpPremultipliedOver.shader`, `Shaders/FvpDepthOnly.shader` | the protection bubble drawn AFTER the model's picture is taken: a second, hidden camera per pose camera renders the real objects through the same projection and lens distortion into a texture of its own, shown over the display; the two shaders composite it and draw the occluders depth-only |
| `FvpDisplayLayer.cs`, `FvpOverlayFill.cs` | drawings over the displays of the pose cameras (one overlay per camera, over the image the display shows): projection, the capsule's silhouette, filled polygons. Built on `FvpOverlay`, which is unchanged |
| `FvpServerHygiene.cs` | who listens on a port, asked of the operating system (never of the server: connecting takes its one client slot) |
| `PoseSource.cs`, `*.PoseSource.cs` | `IPoseSource` and its three implementations (new partial parts of `FasterVoxelPoseLive` and `ViTPoseLive`) |
| `PoseEstimateFollower.cs`, `PoseEstimateFollower.Display.cs`, `OperatorTrack.cs` | moves an object with the operator (hidden from the pose cameras and drawn over their displays instead, tinted when lost, with measurements), and the logic behind it: bodies, Kalman filter, loss and re-acquisition |
| `PoseEstimateFollower.Fit.cs`, `FvpCapsuleFit.cs` | sizes that object to the detected skeleton in any pose (*Fit To Person*): the geometry and the per-person memory / smoothing (plain C#, tested outside Unity), and the glue |
| `Editor/FasterVoxelPoseLiteInstaller.cs`, `Editor/*Editor.cs` | the Tools menu items for the lean estimator and the follower, its server pre-warm hook, the two Inspectors |
| `Server~/capsule_eval.py` | replays a recorded session through the server and measures the torso point (error, jitter, recall, ghosts, timing); `--csv` keeps the real estimates |
| `Server~/track_eval/` | the follower's logic outside Unity: `run.ps1 -What scenarios \| governor \| fit \| replay -Csv x.csv` |
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

* **"the server answering on port 5577 (python pid N) is version 1"** – a server from an earlier editor session is still running and keeps the port (the Console log of FasterVoxelPoseLite says which process), or two servers share the port (the message then lists both: Windows lets them, and the connection goes to either; this machine had exactly that, a server of 8 Oct and a second one started beside it). **Tools ▸ FasterVoxelPose ▸ Stop inference server** only stops the servers *this* editor session started, so it does not reach it; **Stop ALL inference servers (also ones left by earlier editor sessions)** does (it stops the python processes that listen on the project's server ports, and the ones this session started). The next Play starts a current server (15 s to load the model once). Why it matters: a version-1 server sends no 2D support, so the phantom filter does nothing, and no stage times. The servers are never started next to one that already holds the port (on Windows two servers can bind the same port without an error and the clients then land on either): the editor hooks and the lean estimator ask the operating system who listens before starting one.
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
