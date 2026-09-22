# Skeletal Retargeting: The Math

A skeleton is a hierarchy of bones (a kinematic chain). Each bone has:

- A **local rotation**: its rotation relative to its parent bone.
- A **world rotation**: its rotation relative to the world origin, obtained via
  **forward kinematics** by walking up the hierarchy:

```
worldRotation(bone) = worldRotation(parent) * localRotation(bone)
```

When a source skeleton and a target skeleton have identical bone hierarchies, bone
lengths, and rest poses, `localRotation` values can be copied directly frame by frame.
In every other case, one or more of the following differ:

- Limb proportions (longer arms, shorter torso, etc.)
- Rest poses (T-pose, A-pose, or a relaxed/neutral pose)
- Bone-axis conventions (a bone's local X axis may point down the limb in one skeleton
  and up in another, depending on how it was authored)

Copying local rotations directly under these conditions produces broken, twisted, or
sliding limbs. The technique below — sometimes called the **intermediate / auxiliary
skeleton** method — corrects for this by retargeting rotation *offsets* rather than raw
rotations.

## 1. Retargeting through a common reference pose

1. Both skeletons are placed in the same reference pose (typically a T-pose). In this
   pose, corresponding bones point in the same world-space direction even though their
   lengths differ. This reference pose is the **bind pose**, also called the
   **auxiliary pose**.
2. For every animated frame, the source bone's world rotation is compared against its
   own bind pose, producing a pure rotation — the **offset rotation** — that is
   independent of the source skeleton's proportions.
3. That offset rotation is applied on top of the target skeleton's bind pose.
4. The result is converted into the target bone's local space (relative to its own
   parent), which is the value that drives the target skeleton's pose.

Because the offset is computed and re-applied in world space, and both skeletons share
the same reference orientation in world space at the bind pose, the offset transfers
correctly even though bone lengths and hierarchies differ.

## 2. Notation

- `src` = source skeleton
- `trg` = target skeleton
- `bind` = a transform captured while the skeleton is in the reference T-pose
- `World` = rotation relative to the world origin (via forward kinematics)
- `Local` = rotation relative to the immediate parent bone
- `WorldParent` = the world rotation of a bone's parent (`World` one level up the
  hierarchy)
- `inv(q)` = quaternion inverse (for unit quaternions, this is the conjugate)
- `*` = quaternion multiplication (composition of rotations)

All rotations are represented as unit quaternions, never as Euler angles (§5).

## 3. The core formula

For a single bone, on a single animation frame, the retargeted local rotation for the
target bone is:

```
trgLocal = invBindTrgWorldParent * bindSrcWorldParent * srcLocal * invBindSrcWorld * bindTrgWorld
```

Read right to left, in four stages:

### Stage 1 — Reconstruct the source's animated world rotation

```
srcWorld = bindSrcWorldParent * srcLocal
```

The bind pose's parent world rotation is combined with the current frame's local
rotation. Processed root-to-leaf (§4), the parent term here is the parent's *already
retargeted* world rotation from the same frame, not a static bind-pose value.

### Stage 2 — Extract the pure offset rotation

```
offsetWorld = srcWorld * invBindSrcWorld
```

`invBindSrcWorld` is the inverse of the source bone's world rotation at rest (T-pose).
Multiplying it away leaves only how far the bone has rotated away from its own rest
orientation, expressed in world space. This offset carries no information about the
source skeleton's bone lengths — only about rotation.

### Stage 3 — Apply the offset to the target's rest pose

```
trgWorld = offsetWorld * bindTrgWorld
```

The target skeleton's rest-pose world rotation is rotated by the same offset the
source bone experienced. Both skeletons share the same reference T-pose, so
`bindSrcWorld` and `bindTrgWorld` represent the same world-space direction, and
applying the same offset produces a matching pose on the target regardless of the
target's actual bone length.

### Stage 4 — Convert back to the target's local space

```
trgLocal = invTrgWorldParent * trgWorld
```

Since the target's world rotation is `trgWorldParent * trgLocal` (forward kinematics),
`trgLocal` is solved by left-multiplying by the inverse of the parent's world rotation
— `invBindTrgWorldParent` combined with the Stage 3 result, matching the formula above.

`trgLocal` is the value written into the target skeleton's pose for this bone, this
frame.

## 4. Processing order: root to leaf

Each bone's world rotation depends on its parent's world rotation, so the skeleton is
processed top-down — root first, then its children, and so on — using the
*already-retargeted* parent world rotation (not the bind-pose parent) so offsets
accumulate correctly down the chain. Per bone `b`:

```
srcWorld[b]     = srcWorld[parent(b)] * srcLocal[b]
offsetWorld[b]  = srcWorld[b] * invBindSrcWorld[b]
trgWorld[b]     = offsetWorld[b] * bindTrgWorld[b]
trgLocal[b]     = invTrgWorld[parent(b)] * trgWorld[b]
```

`trgWorld[parent(b)]` is the parent's result from the same frame, computed earlier in
the same top-down pass.

## 5. Quaternions versus Euler angles

- Quaternion composition (`q1 * q2`) directly represents "rotate by q2, then by q1,"
  matching how forward kinematics chains rotations up a hierarchy.
- Quaternions avoid gimbal lock, a singularity in Euler-angle representations where two
  rotation axes align and a degree of freedom is lost. Wrists and shoulders regularly
  pass through orientations where Euler angles misbehave, and unconstrained
  full-range-of-motion capture data hits this often.
- Quaternion inversion (`inv(q)`) is cheap — conjugate plus normalize for unit
  quaternions — making the offset-extraction step (Stage 2) numerically trivial and
  stable.
- Spherical interpolation (`slerp`) between quaternions behaves predictably when
  smoothing or blending retargeted poses; the equivalent Euler-angle interpolation does
  not.

## 6. Root translation

Rotation offsets carry no positional information. The root/hip bone's translation is
retargeted separately, by scaling the source's root displacement by a ratio of
target-to-source skeleton size (height or leg length):

```
trgRootPosition = srcRootPosition * (targetLegLength / sourceLegLength)
```

Without this scaling, a target skeleton taller or shorter than the source floats above
or sinks into the ground, and step length is wrong.

## 7. Twist and roll distribution

A single rotation offset applied at a wrist or ankle can concentrate all of a limb's
twist at one joint, producing unnatural skin deformation along the forearm or shin in
a skinned mesh. Redistributing a percentage of a limb's twist rotation across
intermediate roll/twist bones corrects this. It is a secondary deformation pass on top
of the rotation already produced by §3, and only applies to rigs that have dedicated
roll/twist bones.

## 8. Scale constraints

The formulas in §3–§4 assume proportional, positive scale on every bone. Non-uniform
scale does not commute cleanly with quaternion rotation composition — rotating a
non-uniformly-scaled space distorts angles — and negative scale flips handedness,
breaking the chain of multiplications. A rig with non-uniform or mirrored-negative
scale on its bones must be normalized before use as a source or target.

## 9. Bone correspondence

The formulas above require that each source bone already be paired with a target bone
(for example, `RightForeArm` paired with `RightLowerArm`). This correspondence is
established by matching bone names, with manual override for mismatches, and determines
which `bindSrc*` / `bindTrg*` values are paired in §3.

## 10. Worked numeric example

A single elbow joint rotating around one axis, represented as a plain angle rather than
a full quaternion for readability (the algorithm itself always uses quaternions in 3D):

- Source bind pose (T-pose): elbow angle = 0°
- Source animated frame: elbow angle = 60° (forearm raised 60° from rest)
- Target bind pose (T-pose): elbow angle = 0°

Offset = animated − bind = 60° − 0° = **60°**

Target animated angle = target bind + offset = 0° + 60° = **60°**

The rotation applied is identical regardless of the target's forearm length, because
the offset is computed relative to each skeleton's own rest pose and re-applied to the
other skeleton's rest pose. This is the §3 principle without the parent-chain
bookkeeping quaternions require in full 3D.

## 11. Embedded / container rotations

A bone can carry a "container" or "helper" orientation that differs from its actual
skinning orientation — for example, an extra offset node added so a control appears
visually aligned in a viewport without changing how the mesh is skinned. This is
accounted for with extra terms:

```
trgLocal = invBindTrgWorldParent * invTrgEmbedded * srcEmbedded * bindSrcWorldParent
         * srcLocal * invBindSrcWorld * invSrcEmbedded * trgEmbedded * bindTrgWorld
```

`srcEmbedded` / `trgEmbedded` are the extra container rotations for the source/target
bone respectively. When a bone has no such container offset, these terms are identity
quaternions and the formula collapses to §3.

## 12. Sources

- Monzani, Baerlocher, Boulic, Thalmann, *"Using an Intermediate Skeleton and Inverse
  Kinematics for Motion Retargeting"*, Eurographics 2000 — formalized the
  bind-pose/auxiliary-skeleton offset approach in §1–§4.
- Gleicher, *"Retargetting Motion to New Characters"*, SIGGRAPH 1998 — adds
  spacetime/IK constraints on top of rotation retargeting, such as keeping feet
  planted.
- [retargeting-threejs — Algorithm.md](https://github.com/upf-gti/retargeting-threejs/blob/main/docs/Algorithm.md) —
  reference implementation of the formulas in §3 and §11.
