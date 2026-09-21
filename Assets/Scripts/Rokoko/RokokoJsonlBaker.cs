#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Rokoko.Core;
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
    public static void RunBake(Actor actor, string jsonlPath, string outPath)
    {
        int applied;
        try
        {
            applied = BakeToClip(actor, jsonlPath, outPath);
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
    public static int BakeToClip(Actor actor, string jsonlPath, string outClipPath)
    {
        // Actor.UpdateActor() reads a private bone-rotation-offset cache that's normally
        // built in Actor.Awake() -- which only runs in Play Mode. This baker calls
        // UpdateActor() directly in the Editor without ever entering Play Mode, so that
        // cache would otherwise stay empty and UpdateActor() throws KeyNotFoundException
        // on the first bone. Rather than touch the vendored Rokoko package, invoke the
        // same private initializers it calls, via reflection, once before baking.
        InvokeActorPrivateMethod(actor, "InitializeAnimatorHumanBones");
        InvokeActorPrivateMethod(actor, "InitializeBoneOffsets");

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

                float dt = lastTs < 0f ? 1f / 30f : Mathf.Max(raw.studio_timestamp - lastTs, 1f / 240f);
                if (applied > 0)
                    time += dt;
                lastTs = raw.studio_timestamp;

                poseHandler.GetHumanPose(ref pose);

                for (int m = 0; m < muscleCount; m++)
                    muscleKeys[m].Add(new Keyframe(time, pose.muscles[m]));

                rootTKeys[0].Add(new Keyframe(time, pose.bodyPosition.x));
                rootTKeys[1].Add(new Keyframe(time, pose.bodyPosition.y));
                rootTKeys[2].Add(new Keyframe(time, pose.bodyPosition.z));
                rootQKeys[0].Add(new Keyframe(time, pose.bodyRotation.x));
                rootQKeys[1].Add(new Keyframe(time, pose.bodyRotation.y));
                rootQKeys[2].Add(new Keyframe(time, pose.bodyRotation.z));
                rootQKeys[3].Add(new Keyframe(time, pose.bodyRotation.w));

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

        var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(outClipPath) };
        for (int m = 0; m < muscleCount; m++)
            clip.SetCurve("", typeof(Animator), HumanTrait.MuscleName[m], new AnimationCurve(muscleKeys[m].ToArray()));

        string[] rootTNames = { "RootT.x", "RootT.y", "RootT.z" };
        for (int a = 0; a < 3; a++)
            clip.SetCurve("", typeof(Animator), rootTNames[a], new AnimationCurve(rootTKeys[a].ToArray()));

        string[] rootQNames = { "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
        for (int a = 0; a < 4; a++)
            clip.SetCurve("", typeof(Animator), rootQNames[a], new AnimationCurve(rootQKeys[a].ToArray()));

        AssetDatabase.CreateAsset(clip, outClipPath);
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(clip);

        return applied;
    }

    // Calls a private/protected no-arg instance method on Actor without modifying the
    // vendored Rokoko package. Silently no-ops if the plugin ever renames the method.
    static void InvokeActorPrivateMethod(Actor actor, string methodName)
    {
        MethodInfo method = typeof(Actor).GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        method?.Invoke(actor, null);
    }
}
#endif
