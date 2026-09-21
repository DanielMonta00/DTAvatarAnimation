# CDPR — 4-cable suspended parallel robot

A cable-driven parallel robot (CDPR) in the SkyCam configuration: four cables run from four
coplanar ceiling anchors down to a platform. Position is controlled in 3D; orientation is not.

Code lives here, in `Assets/Scripts/CDPR/`. Meshes, materials and textures live in `Assets/CDPR/`,
which holds assets only — see `Assets/CDPR/ASSET_SOURCES.md` for provenance and licences.

## Quick start

`GameObject > CDPR > Create 4-Cable Robot Rig` builds a complete working rig from Unity
primitives — four pulleys, four winch drums, a wooden plate with an actuator stud, and a target
sphere. Press Play and drag the Target around: the platform follows it only as far as the cables
and motors actually allow.

Turn on **Draw Workspace Slice** on the `CableRobot` component to see the reachable region at the
platform's current height. Raise the platform toward the ceiling and watch the region collapse.

## Why four cables is the right number

Fully constraining `m` degrees of freedom takes `m + 1` cables, so 3-DOF position control needs
exactly four. The extra cable is the redundancy that lets all four stay in tension at once.

Inverse kinematics is closed-form and exact — `l_i = ||a_i − p_i||`, one length per cable, no
iteration. The interesting constraint is that **a cable can pull but never push**. The reachable
set is not "inside the box" but the *wrench-feasible workspace*: positions where

```
sum_i ( t_i · u_i )  =  m · (accel − gravity)
```

has a solution with every `t_i` inside `[tensionMin, tensionMax]`.

Because the four anchors are coplanar, the cable directions cannot positively span 3D space on
their own — gravity supplies the missing downward pull. Three consequences, all reproduced by the
solver:

- The platform **cannot accelerate downward faster than g**.
- Tension **diverges as the platform approaches the ceiling plane** (the cables go horizontal and
  their vertical component vanishes), so `tensionMax` trims the top of the workspace.
- Feasibility is **lost before the platform reaches the footprint edge**, because `tensionMin`
  bites while the far cable still has a little tension left.

Sitting exactly on the anchor plane is reported as `Degenerate`: no tension solution exists at all.

## Cable attachment: point vs. plate

The "point" in the maths is the point where the four cables *meet*, not the whole robot.

- **Leave `cableAttachPoints` empty** → all four cables converge on the platform origin. This is
  the exact single-point (4-1) model. Hang a plate below that point from a spreader or hook and
  the physics is exactly right. The plate is free to swing and spin.
- **Assign them to the plate's corners** (what the rig builder does) → cable lengths are measured
  from the real corners. Force balance is unchanged, but the cables now exert moments, and the
  platform is under-constrained in 6 DOF with only 4 motors. This code assumes the plate hangs
  level. It looks and behaves right; it is an approximation, and yaw is genuinely uncontrolled on
  a real machine.

## Trajectories

`CableRobotTrajectory` sits next to `CableRobot` and generates a simulated path instead of chasing
a target. Modelled on `FPDatasetSimulator/TrajectoryGenerator`, with one difference that matters: a
camera trajectory only has to *exist*, whereas a cable robot trajectory has to be **executable**.
Every sample is run through the solver, and the Scene-view gizmo draws infeasible segments in red.

Context menus: **Preview Trajectory (no save)**, **Generate and Save JSON**, **Generate All Trajectory Types**.

Five types: `Orbital`, `Helicoidal`, `OrbitSinusoidalY`, `Lissajous`, `RasterScan`.

Playback has two modes. `RespectMotorLimits` drives the platform through `StepTowards`, so the
robot visibly *lags* a path its motors cannot keep up with — this is what the real machine does.
`IdealTracking` snaps the platform to each waypoint, showing the commanded path regardless.

The JSON carries cable lengths, tensions and **drum angles** per frame, not just positions. Those
four drum angles are the command stream you would send to the real motors.

### The workspace is anisotropic — circles are a trap

With the 4 × 3 m rig from the rig builder, at y = 1.2 m the platform can reach **±1.52 m in X but
only ±1.07 m in Z**. A circle is limited by the narrow axis, so `Orbital` wastes most of the long
axis. `Lissajous` has independent per-axis amplitudes and fills the workspace properly, which is
why it is the default.

The feasible circle also *grows* with height (1.07 m at y = 1.2, 1.26 m at y = 2.4) because the
workspace is a cone hanging beneath the anchor rectangle — until `tensionMax` chops off the top
near the ceiling. `Helicoidal` therefore has to fit at its **highest** point, not its lowest.

Defaults (`radius = 0.9`, `helicoidalRise = 0.5`) are verified to keep all five trajectory types
fully inside the workspace of the rig builder's geometry, with tensions in a 7–43 N band. Change
the room size and re-run **Preview Trajectory** — the console reports the infeasible fraction.

## Files

| File | Purpose |
|---|---|
| `CableRobotSolver.cs` | Pure static maths. No Unity dependencies beyond `Vector3`. Allocation-free. |
| `CableRobot.cs` | MonoBehaviour: drives the platform, enforces tension / cable-speed / cable-length limits, draws cables and the workspace slice. |
| `CableRobotTrajectory.cs` | Generates, validates, plays back and exports simulated trajectories. |
| `Editor/CableRobotRigBuilder.cs` | Menu item that builds the primitive rig. |

The tension distribution in `CableRobotSolver` is the standard closed form for a robot with one
redundant actuator: `W` (3×4, columns = unit cable directions) has rank 3, so its null space is
one-dimensional and the solutions form a line `t = t_particular + λ·n`. We take the minimum-norm
particular solution and pick `λ` at the centre of the interval that keeps all four tensions in
bounds, maximising the margin against both slack and overload.

## Swapping in real meshes

`CableRobot` only ever reads `Transform` positions, so replacing a primitive's MeshFilter and
MeshRenderer with imported geometry changes nothing. Keep the `Platform` root unscaled and put the
scale on its `Plate` child; the attachment points and actuator hang off the unscaled root.

**Unity does not import STL or STEP natively** — its mesh formats are FBX, OBJ and DAE. Route STL
through Blender (imports directly, export FBX). STEP needs FreeCAD or CAD Exchanger first. Set
Scale Factor to `0.001` on import, since CAD is in millimetres and Unity is in metres.

Sources worth using, none of which are vendored into this repo:

- [OpenSourcePlanarCableRobot](https://github.com/LionelBirglen/OpenSourcePlanarCableRobot) — a real
  built 4-motor cable robot, with editable CAD for winches, pulleys and motor mounts. Most relevant.
- GrabCAD for the motors: [NEMA 17](https://grabcad.com/library/nema-17-stepper-motor-22),
  [NEMA 23](https://grabcad.com/library/nema23-stepper-motor-1),
  [NEMA 34](https://grabcad.com/library/step-motor-nema-34). Free account; **licence is set per-upload
  by the uploader**, so check each model's page before it enters a commercial deliverable.
- [ambientCG](https://ambientcg.com/) or [Poly Haven](https://polyhaven.com/textures) for CC0 wood
  PBR textures. For a wooden square you want a texture, not a model.

Any downloaded meshes belong in `Assets/CDPR/`, not here.

## Prior art

- [CaRoSim](https://github.com/aau-cns/CaRoSim) — Unity CDPR sim with flexible cables (needs the
  paid Obi Rope asset) and an RL controller. BSD-2-Clause, **no commercial use**.
- [CASPR](https://github.com/darwinlau/CASPR) — the MATLAB research reference platform.
- [cdpyr](https://github.com/cable-robots/cdpyr) — Python analysis and design framework.
