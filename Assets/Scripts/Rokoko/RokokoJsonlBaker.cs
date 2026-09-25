#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Rokoko.Core;
using Rokoko.Helper;
using Rokoko.Inputs;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor-only baking engine: replays a rokoko_skeleton.jsonl (from HumanDatasetRecording's
/// tools/04_receive_rokoko.py --record) through a scene Actor's own UpdateActor() -- reusing
/// its Smartsuit T-pose correction unchanged -- and bakes the result into a Humanoid
/// AnimationClip (muscle + root motion curves via HumanPoseHandler, not raw Transform
/// curves), so it retargets and drops into an Animator Controller like any other Humanoid
/// clip. No network, no Studio, no live streaming involved; Unity is only open for this
/// bake step, not during the actual mocap capture.
///
/// This class has no UI of its own -- see RokokoMocapBaker.cs for the component you attach
/// to a character to actually drive this.
/// </summary>
public static class RokokoJsonlBaker
{
    [Serializable]
    class RawActorEntry
    {
        public string name;
        public float hip_height;
        public BodyFrame bones; // field names match rokoko_receiver.py's "bones" dict 1:1
    }

    [Serializable]
    class RawLine
    {
        public float studio_timestamp;
        public RawActorEntry[] actors;
    }

    /// <summary>
    /// Optional post-processing toggles layered on top of Actor.UpdateActor()'s own retargeting,
    /// approximating Rokoko Studio Character-retargeting features this Actor-driven bake doesn't
    /// have (see RokokoMocapBaker's Inspector for the matching checkboxes). Defaults leave the
    /// bake byte-identical to plain Actor.UpdateActor() output.
    /// </summary>
    public struct BakeOptions
    {
        public bool twistRedistribution;
        public float twistRedistributionAmount;
        public bool aimForThumbs;

        /// <summary>
        /// Symmetric moving-average window radius (in captured frames) applied to every curve's
        /// sampled values after baking. 0 disables it. A radius of N averages each frame with its
        /// N neighbors on both sides (2N+1 samples total) to remove per-frame mocap sensor jitter
        /// -- the "choppy" look tangent smoothing alone can't fix, since SmoothTangents only
        /// reshapes interpolation between keys without changing the keys' values.
        /// </summary>
        public int temporalSmoothingRadius;
    }

    // (outer limb-segment twist muscle, inner limb-segment twist muscle) pairs Twist
    // Redistribution moves a share of rotation between. Named by Unity's own HumanTrait.MuscleName
    // strings rather than by index, since those are stable across Unity versions.
    static readonly (string outer, string inner)[] TwistMuscleNamePairs =
    {
        ("Left Forearm Twist In-Out", "Left Arm Twist In-Out"),
        ("Right Forearm Twist In-Out", "Right Arm Twist In-Out"),
        ("Left Lower Leg Twist In-Out", "Left Upper Leg Twist In-Out"),
        ("Right Lower Leg Twist In-Out", "Right Upper Leg Twist In-Out"),
    };

    /// <summary>
    /// Checks the preconditions UpdateActor()/HumanPoseHandler need. Returns null when
    /// the Actor is ready to bake, otherwise a user-facing message describing what's missing.
    /// </summary>
    public static string Validate(Actor actor)
    {
        if (actor == null)
            return "No Actor component selected.";
        if (actor.animator == null || !actor.animator.isHuman)
            return "This Actor's Animator isn't assigned or isn't marked Humanoid.";
        if (actor.characterTPose.Count == 0)
            return "This Actor has no T-pose reference assigned yet -- click \"Assign T-Pose Now\" first.";
        return null;
    }

    /// <summary>
    /// Runs BakeToClip, showing/clearing the progress bar and reporting the result --
    /// the shared "do it and tell the user what happened" wrapper.
    /// </summary>
    public static void RunBake(Actor actor, string jsonlPath, string outPath, BakeOptions options = default)
    {
        int applied;
        try
        {
            applied = BakeToClip(actor, jsonlPath, outPath, options);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (applied == 0)
        {
            EditorUtility.DisplayDialog("Rokoko Bake",
                "No frames were baked -- the JSONL was empty, unreadable, or had no matching actor.", "OK");
            return;
        }

        Debug.Log($"[Rokoko Bake] Wrote {applied} frame(s) from {jsonlPath} -> {outPath}");
    }

    /// <summary>
    /// Core bake: replays every line of jsonlPath through actor.UpdateActor() (this is
    /// where Rokoko's own T-pose-corrected retargeting from the suit's skeleton onto
    /// `actor`'s specific rig happens), samples the resulting pose each frame via
    /// HumanPoseHandler, and writes a Humanoid AnimationClip asset to outClipPath.
    /// Returns the number of frames baked. Caller must call Validate(actor) first.
    /// </summary>
    public static int BakeToClip(Actor actor, string jsonlPath, string outClipPath, BakeOptions options = default)
    {
        // Actor.UpdateActor() reads a private bone-rotation-offset cache that's normally
        // built in Actor.Awake() -- which only runs in Play Mode. This baker calls
        // UpdateActor() directly in the Editor without ever entering Play Mode, so that
        // cache would otherwise stay empty and UpdateActor() throws KeyNotFoundException
        // on the first bone. Rather than touch the vendored Rokoko package, invoke the
        // same private initializers it calls, via reflection, once before baking.
        InvokeActorPrivateMethod(actor, "InitializeAnimatorHumanBones");
        InvokeActorPrivateMethod(actor, "InitializeBoneOffsets");

        // Resolved once, not per frame: looking up 8 names in a 95-entry array every frame is
        // wasted work, and warning once here (instead of once per frame) keeps the Console readable
        // if a Unity version ever renames one of these muscles.
        List<(int outer, int inner)> twistMuscleIndices = null;
        if (options.twistRedistribution)
            twistMuscleIndices = ResolveTwistMuscleIndices();

        // Sample the Animator's posed muscle values each frame instead of recording raw
        // Transform curves (GameObjectRecorder), so the resulting clip is a real Humanoid
        // clip: retargetable, and usable in an Animator Controller alongside other
        // Humanoid clips without the "already bound by a Humanoid avatar" binding warning.
        int muscleCount = HumanTrait.MuscleCount;
        var muscleKeys = new List<Keyframe>[muscleCount];
        for (int m = 0; m < muscleCount; m++)
            muscleKeys[m] = new List<Keyframe>();
        var rootTKeys = new List<Keyframe>[3] { new List<Keyframe>(), new List<Keyframe>(), new List<Keyframe>() };
        var rootQKeys = new List<Keyframe>[4] { new List<Keyframe>(), new List<Keyframe>(), new List<Keyframe>(), new List<Keyframe>() };

        string[] lines = File.ReadAllLines(jsonlPath);
        float lastTs = -1f;
        float time = 0f;
        int applied = 0;

        // Baking should only ever produce a clip, never permanently move the character.
        // UpdateActor() mutates the live scene rig frame-by-frame, so snapshot every
        // Transform under it now and restore it in the finally block below, regardless
        // of how far the loop gets (including on error).
        Transform[] allTransforms = actor.animator.transform.GetComponentsInChildren<Transform>(true);
        var snapLocalPos = new Vector3[allTransforms.Length];
        var snapLocalRot = new Quaternion[allTransforms.Length];
        for (int t = 0; t < allTransforms.Length; t++)
        {
            snapLocalPos[t] = allTransforms[t].localPosition;
            snapLocalRot[t] = allTransforms[t].localRotation;
        }

        var poseHandler = new HumanPoseHandler(actor.animator.avatar, actor.animator.transform);
        var pose = new HumanPose();
        Quaternion lastBodyRotation = Quaternion.identity;

        try
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (i % 20 == 0)
                    EditorUtility.DisplayProgressBar("Baking Rokoko clip",
                        $"Frame {i}/{lines.Length}", (float)i / lines.Length);

                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;

                RawLine raw = JsonUtility.FromJson<RawLine>(lines[i]);
                if (raw?.actors == null || raw.actors.Length == 0)
                    continue;

                RawActorEntry src = null;
                foreach (RawActorEntry a in raw.actors)
                {
                    if (a.name == actor.profileName)
                    {
                        src = a;
                        break;
                    }
                }
                if (src == null)
                    src = raw.actors[0];

                var frame = new ActorFrame
                {
                    name = src.name,
                    meta = new ActorFrame.Meta { hasBody = true },
                    dimensions = new ActorFrame.Dimensions { hipHeight = src.hip_height },
                    body = src.bones,
                };

                actor.UpdateActor(frame);

                if (options.aimForThumbs)
                    ApplyAimForThumbs(actor.animator, src.bones);

                float dt = lastTs < 0f ? 1f / 30f : Mathf.Max(raw.studio_timestamp - lastTs, 1f / 240f);
                if (applied > 0)
                    time += dt;
                lastTs = raw.studio_timestamp;

                poseHandler.GetHumanPose(ref pose);

                if (twistMuscleIndices != null)
                    ApplyTwistRedistribution(ref pose, twistMuscleIndices, options.twistRedistributionAmount);

                for (int m = 0; m < muscleCount; m++)
                    muscleKeys[m].Add(new Keyframe(time, pose.muscles[m]));

                rootTKeys[0].Add(new Keyframe(time, pose.bodyPosition.x));
                rootTKeys[1].Add(new Keyframe(time, pose.bodyPosition.y));
                rootTKeys[2].Add(new Keyframe(time, pose.bodyPosition.z));

                // q and -q are the same rotation, but GetHumanPose gives no guarantee of a
                // consistent sign frame to frame, and RootQ.x/y/z/w are stored as 4 independent
                // curves -- a sign flip between two consecutive keyframes makes Unity interpolate
                // through a large wrong swing before snapping back. Flipping the whole quaternion
                // whenever it points away from the previous frame keeps every stored keyframe on
                // the same side of that double-cover, so interpolation between them is continuous.
                Quaternion bodyRotation = pose.bodyRotation;
                if (applied > 0 && Quaternion.Dot(bodyRotation, lastBodyRotation) < 0f)
                    bodyRotation = new Quaternion(-bodyRotation.x, -bodyRotation.y, -bodyRotation.z, -bodyRotation.w);
                lastBodyRotation = bodyRotation;

                rootQKeys[0].Add(new Keyframe(time, bodyRotation.x));
                rootQKeys[1].Add(new Keyframe(time, bodyRotation.y));
                rootQKeys[2].Add(new Keyframe(time, bodyRotation.z));
                rootQKeys[3].Add(new Keyframe(time, bodyRotation.w));

                applied++;
            }
        }
        finally
        {
            poseHandler.Dispose();

            for (int t = 0; t < allTransforms.Length; t++)
            {
                allTransforms[t].localPosition = snapLocalPos[t];
                allTransforms[t].localRotation = snapLocalRot[t];
            }
        }

        if (applied == 0)
            return 0;

        // Runs once over the whole captured sequence rather than live/per-frame, so unlike a
        // causal filter it can average a frame against neighbors on BOTH sides -- that's what
        // removes jitter without adding the lag a live low-pass filter would.
        if (options.temporalSmoothingRadius > 0)
        {
            for (int m = 0; m < muscleCount; m++)
                SmoothKeyframeValues(muscleKeys[m], options.temporalSmoothingRadius);
            for (int a = 0; a < 3; a++)
                SmoothKeyframeValues(rootTKeys[a], options.temporalSmoothingRadius);
            SmoothAndRenormalizeQuaternionKeys(rootQKeys, options.temporalSmoothingRadius);
        }

        var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(outClipPath) };
        for (int m = 0; m < muscleCount; m++)
            clip.SetCurve("", typeof(Animator), HumanTrait.MuscleName[m], BuildSmoothedCurve(muscleKeys[m]));

        string[] rootTNames = { "RootT.x", "RootT.y", "RootT.z" };
        for (int a = 0; a < 3; a++)
            clip.SetCurve("", typeof(Animator), rootTNames[a], BuildSmoothedCurve(rootTKeys[a]));

        string[] rootQNames = { "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
        for (int a = 0; a < 4; a++)
            clip.SetCurve("", typeof(Animator), rootQNames[a], BuildSmoothedCurve(rootQKeys[a]));

        AssetDatabase.CreateAsset(clip, outClipPath);
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(clip);

        return applied;
    }

    // Every Keyframe pushed above defaults to a flat (zero) in/out tangent -- at ~30 keyframes
    // per second that forces a separate ease-in/ease-out S-curve on every single segment of every
    // curve, which reads as wobbly/jittery motion once played back, even though the underlying
    // per-frame values (verified against the raw recording) are smooth. SmoothTangents recomputes
    // each key's tangent from its neighbors (Catmull-Rom-like, matching the Curve editor's "Auto"
    // tangent), removing the artifact without changing any of the sampled values themselves.
    static AnimationCurve BuildSmoothedCurve(List<Keyframe> keys)
    {
        var curve = new AnimationCurve(keys.ToArray());
        for (int k = 0; k < curve.length; k++)
            curve.SmoothTangents(k, 0f);
        return curve;
    }

    // Replaces each keyframe's value with the average of itself and its `radius` neighbors on
    // both sides (clamped at the sequence's ends, so the window just shrinks near the edges
    // rather than wrapping or padding). Reads from a snapshot of the original values so each
    // output sample is computed from un-smoothed input, not from values this same pass already
    // overwrote.
    static void SmoothKeyframeValues(List<Keyframe> keys, int radius)
    {
        if (radius <= 0 || keys.Count < 2)
            return;

        var original = new float[keys.Count];
        for (int i = 0; i < keys.Count; i++)
            original[i] = keys[i].value;

        for (int i = 0; i < keys.Count; i++)
        {
            int lo = Mathf.Max(0, i - radius);
            int hi = Mathf.Min(original.Length - 1, i + radius);
            float sum = 0f;
            for (int j = lo; j <= hi; j++)
                sum += original[j];

            Keyframe k = keys[i];
            k.value = sum / (hi - lo + 1);
            keys[i] = k;
        }
    }

    // RootQ is stored as 4 independent float curves (x/y/z/w), so smoothing each one separately
    // -- like SmoothKeyframeValues does for every other curve -- would leave frames that are no
    // longer unit-length quaternions. Renormalizing per frame afterwards fixes that; this is a
    // cheap linear approximation of quaternion averaging rather than a proper SLERP-based
    // average, but it's a fine approximation for small windows since BakeToClip's rootQKeys are
    // already sign-continuous (see the Quaternion.Dot flip above) before this ever runs.
    static void SmoothAndRenormalizeQuaternionKeys(List<Keyframe>[] rootQKeys, int radius)
    {
        if (radius <= 0 || rootQKeys[0].Count < 2)
            return;

        for (int c = 0; c < 4; c++)
            SmoothKeyframeValues(rootQKeys[c], radius);

        for (int i = 0; i < rootQKeys[0].Count; i++)
        {
            var q = new Vector4(rootQKeys[0][i].value, rootQKeys[1][i].value,
                rootQKeys[2][i].value, rootQKeys[3][i].value);
            if (q.sqrMagnitude < 1e-8f)
                continue;
            q.Normalize();

            for (int c = 0; c < 4; c++)
            {
                Keyframe k = rootQKeys[c][i];
                k.value = q[c];
                rootQKeys[c][i] = k;
            }
        }
    }

    // Calls a private/protected no-arg instance method on Actor without modifying the
    // vendored Rokoko package. Silently no-ops if the plugin ever renames the method.
    static void InvokeActorPrivateMethod(Actor actor, string methodName)
    {
        MethodInfo method = typeof(Actor).GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        method?.Invoke(actor, null);
    }

    static List<(int outer, int inner)> ResolveTwistMuscleIndices()
    {
        var resolved = new List<(int outer, int inner)>();
        foreach ((string outerName, string innerName) in TwistMuscleNamePairs)
        {
            int outerIdx = Array.IndexOf(HumanTrait.MuscleName, outerName);
            int innerIdx = Array.IndexOf(HumanTrait.MuscleName, innerName);
            if (outerIdx < 0 || innerIdx < 0)
            {
                Debug.LogWarning($"[Rokoko Bake] Twist Redistribution: couldn't find muscle "
                    + $"\"{(outerIdx < 0 ? outerName : innerName)}\" in this Unity version's "
                    + "HumanTrait.MuscleName -- skipping this limb.");
                continue;
            }
            resolved.Add((outerIdx, innerIdx));
        }
        return resolved;
    }

    // Approximates Rokoko Studio's Roll Extraction: moves a share of each limb's outer-segment
    // twist muscle (forearm/lower leg) back into its inner segment's twist muscle (upper arm/
    // upper leg), so the whole limb absorbs a wrist/ankle twist instead of concentrating it at
    // the outer joint alone. Runs on the already-sampled HumanPose in muscle space -- this baker
    // writes muscle curves, not raw Transform curves (see BakeToClip's summary), so this can't
    // reach mesh-level roll/twist helper bones the way Rokoko Studio's own Roll Extraction can;
    // it only redistributes between the two twist DOFs Humanoid already models per limb.
    static void ApplyTwistRedistribution(ref HumanPose pose, List<(int outer, int inner)> muscleIndices, float amount)
    {
        foreach ((int outerIdx, int innerIdx) in muscleIndices)
        {
            float transferred = pose.muscles[outerIdx] * amount;
            pose.muscles[outerIdx] -= transferred;
            pose.muscles[innerIdx] += transferred;
        }
    }

    // Approximates Rokoko Studio's Use Aim For Thumbs: Actor.UpdateBone() only ever applies raw
    // *rotation* to non-hip bones (Actor.cs's shouldUpdatePosition is true for Hips only), so the
    // recording's position data for every other joint -- thumbs included -- is decoded into an
    // ActorFrame but never used. This reuses that otherwise-discarded position data to nudge each
    // thumb segment's already-retargeted rotation just enough that it points at the next recorded
    // joint, rather than replacing Rokoko's own retargeted rotation outright.
    static void ApplyAimForThumbs(Animator animator, BodyFrame bones)
    {
        ApplyAimForThumbSegment(animator, HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate,
            bones.leftThumbProximal, bones.leftThumbMedial);
        ApplyAimForThumbSegment(animator, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
            bones.leftThumbMedial, bones.leftThumbDistal);
        ApplyAimForThumbSegment(animator, HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate,
            bones.rightThumbProximal, bones.rightThumbMedial);
        ApplyAimForThumbSegment(animator, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
            bones.rightThumbMedial, bones.rightThumbDistal);
        // ThumbDistal has no further Humanoid-mapped bone to aim at (its recorded child is a
        // fingertip, which isn't part of the Humanoid bone set), so it's left as Rokoko's own
        // retargeted rotation -- two of each thumb's three segments get the aim correction.
    }

    static void ApplyAimForThumbSegment(Animator animator, HumanBodyBones bone, HumanBodyBones childBone,
        ActorJointFrame rawThis, ActorJointFrame rawChild)
    {
        Transform boneTransform = animator.GetBoneTransform(bone);
        Transform childTransform = animator.GetBoneTransform(childBone);
        if (boneTransform == null || childTransform == null)
            return;

        Vector3 currentDir = childTransform.position - boneTransform.position;
        Vector3 desiredDir = rawChild.position.ToVector3() - rawThis.position.ToVector3();
        if (currentDir.sqrMagnitude < 1e-8f || desiredDir.sqrMagnitude < 1e-8f)
            return;

        Quaternion correction = Quaternion.FromToRotation(currentDir.normalized, desiredDir.normalized);
        boneTransform.rotation = correction * boneTransform.rotation;
    }
}
#endif
