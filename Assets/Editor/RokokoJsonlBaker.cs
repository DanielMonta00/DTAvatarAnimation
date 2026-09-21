using System;
using System.IO;
using Rokoko.Core;
using Rokoko.Inputs;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Offline: replays a rokoko_skeleton.jsonl (from HumanDatasetRecording's
/// tools/04_receive_rokoko.py --record) through a scene Actor's own
/// UpdateActor() -- reusing its Smartsuit T-pose correction unchanged --
/// and bakes the result into a standard AnimationClip. No network, no
/// Studio, no live streaming involved; Unity is only open for this bake
/// step, not during the actual mocap capture.
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

    [MenuItem("Tools/Rokoko/Bake Recorded JSONL to Animation Clip")]
    static void Bake()
    {
        GameObject go = Selection.activeGameObject;
        Actor actor = go != null ? go.GetComponent<Actor>() : null;
        if (actor == null)
        {
            EditorUtility.DisplayDialog("Rokoko Bake",
                "Select the GameObject with the Actor component first -- the character "
                + "you already set up for live Rokoko streaming (Animator assigned, "
                + "Humanoid rig, T-pose calculated via Actor's \"CalculateTPose\").", "OK");
            return;
        }
        if (actor.animator == null || !actor.animator.isHuman)
        {
            EditorUtility.DisplayDialog("Rokoko Bake",
                "This Actor's Animator isn't assigned or isn't marked Humanoid.", "OK");
            return;
        }

        string jsonlPath = EditorUtility.OpenFilePanel("Select rokoko_skeleton.jsonl", "", "jsonl");
        if (string.IsNullOrEmpty(jsonlPath)) return;

        string outPath = EditorUtility.SaveFilePanelInProject(
            "Save Animation Clip", Path.GetFileNameWithoutExtension(jsonlPath), "anim",
            "Choose where to save the baked clip (lives in the Unity project only -- "
            + "the source JSONL in Datasets/ is read, never modified).");
        if (string.IsNullOrEmpty(outPath)) return;

        var recorder = new GameObjectRecorder(actor.gameObject);
        recorder.BindComponentsOfType<Transform>(actor.gameObject, true);

        string[] lines = File.ReadAllLines(jsonlPath);
        float lastTs = -1f;
        int applied = 0;

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
                recorder.TakeSnapshot(dt);
                lastTs = raw.studio_timestamp;
                applied++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(outPath) };
        recorder.SaveToClip(clip);
        AssetDatabase.CreateAsset(clip, outPath);
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(clip);

        Debug.Log($"[Rokoko Bake] Wrote {applied} frame(s) from {jsonlPath} -> {outPath}");
    }
}
