#if UNITY_EDITOR
using System.IO;
using Rokoko.Inputs;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-click pipeline for the MPJPE comparison in documentation/rokoko-mpjpe-eval.ipynb: bakes
/// one jsonl recording twice (temporal smoothing off, then on) via RokokoJsonlBaker.BakeToClip,
/// and exports the reference clip plus both bakes to CSV via
/// RokokoClipJointExporter.ExportToCsv -- all three at the same sample rate, from the same
/// Animator, in one pass, so the resulting CSVs are directly comparable without re-running each
/// step by hand through the separate Bake and Export windows.
/// </summary>
public class RokokoMpjpeEval : EditorWindow
{
    GameObject target;
    string jsonlPath = "documentation/rokoko_skeleton.jsonl";
    AnimationClip referenceClip;
    string outputCsvDir = "documentation/mpjpe";
    int smoothingRadius = 3;
    float sampleRate = 30f;

    [MenuItem("Rokoko/Run MPJPE Pipeline...")]
    static void Open()
    {
        var window = GetWindow<RokokoMpjpeEval>("Rokoko MPJPE Pipeline");
        window.AutoFindTarget();
    }

    void OnEnable() => AutoFindTarget();

    // Looks for a Humanoid character with a Rokoko Actor already set up in the open scene(s) --
    // this only works if the scene that has one (e.g. JN_with_robot.unity) is currently loaded.
    void AutoFindTarget()
    {
        if (target != null)
            return;

        foreach (Actor actor in Object.FindObjectsByType<Actor>(FindObjectsSortMode.None))
        {
            if (actor.animator != null && actor.animator.isHuman)
            {
                target = actor.gameObject;
                break;
            }
        }

        if (referenceClip == null)
            referenceClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(
                "Assets/Animations/RecordedAnimations/test001.anim");
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Bakes the given jsonl recording twice -- once with smoothing off, once with the "
            + "radius below -- and exports the reference clip plus both bakes to CSV at the same "
            + "sample rate, ready for documentation/rokoko-mpjpe-eval.ipynb.",
            MessageType.None);
        EditorGUILayout.Space();

        target = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Character", "GameObject with the Humanoid Animator + Rokoko Actor "
                + "this recording was captured on."),
            target, typeof(GameObject), true);
        jsonlPath = EditorGUILayout.TextField(
            new GUIContent("Recording (.jsonl)", "Project-root-relative or absolute path."), jsonlPath);
        referenceClip = (AnimationClip)EditorGUILayout.ObjectField(
            "Reference Clip", referenceClip, typeof(AnimationClip), false);
        outputCsvDir = EditorGUILayout.TextField("Output CSV Folder", outputCsvDir);
        smoothingRadius = EditorGUILayout.IntSlider("Smoothing Radius (baked pass)", smoothingRadius, 1, 10);
        sampleRate = EditorGUILayout.FloatField("Sample Rate (Hz)", sampleRate);

        Actor actor = target != null ? target.GetComponent<Actor>() : null;
        string blockReason = null;
        if (target == null)
            blockReason = "Assign the character GameObject (or open the scene it lives in and reopen this window).";
        else if (actor == null)
            blockReason = "That GameObject has no Rokoko Actor component.";
        else
        {
            blockReason = RokokoJsonlBaker.Validate(actor);
            if (blockReason == null && string.IsNullOrEmpty(jsonlPath))
                blockReason = "Set a recording path.";
            else if (blockReason == null && referenceClip == null)
                blockReason = "Assign the reference AnimationClip.";
        }

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(blockReason != null))
        {
            if (GUILayout.Button("Run Full Pipeline", GUILayout.Height(32)))
                RunPipeline(actor, jsonlPath, referenceClip, outputCsvDir, smoothingRadius, sampleRate);
        }
        if (blockReason != null)
            EditorGUILayout.HelpBox(blockReason, MessageType.Info);
    }

    static void RunPipeline(Actor actor, string jsonlPath, AnimationClip referenceClip,
        string outputCsvDir, int smoothingRadius, float sampleRate)
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string jsonlFullPath = Path.IsPathRooted(jsonlPath) ? jsonlPath : Path.Combine(projectRoot, jsonlPath);
        if (!File.Exists(jsonlFullPath))
        {
            EditorUtility.DisplayDialog("Rokoko MPJPE Pipeline", $"Recording not found:\n{jsonlFullPath}", "OK");
            return;
        }

        const string bakeFolder = "Assets/Animations/RokokoBakes";
        if (!Directory.Exists(bakeFolder))
        {
            Directory.CreateDirectory(bakeFolder);
            AssetDatabase.Refresh();
        }

        AnimationClip rawClip = BakeAndLoad(actor, jsonlFullPath, bakeFolder,
            "mpjpe_raw", new RokokoJsonlBaker.BakeOptions { temporalSmoothingRadius = 0 });
        if (rawClip == null)
            return;

        AnimationClip smoothedClip = BakeAndLoad(actor, jsonlFullPath, bakeFolder,
            "mpjpe_smoothed", new RokokoJsonlBaker.BakeOptions { temporalSmoothingRadius = smoothingRadius });
        if (smoothedClip == null)
            return;

        string csvDirFull = Path.IsPathRooted(outputCsvDir) ? outputCsvDir : Path.Combine(projectRoot, outputCsvDir);
        Directory.CreateDirectory(csvDirFull);

        RokokoClipJointExporter.ExportToCsv(actor.animator, referenceClip,
            Path.Combine(csvDirFull, "test001_reference.csv"), sampleRate);
        RokokoClipJointExporter.ExportToCsv(actor.animator, rawClip,
            Path.Combine(csvDirFull, "test001_baked_raw.csv"), sampleRate);
        RokokoClipJointExporter.ExportToCsv(actor.animator, smoothedClip,
            Path.Combine(csvDirFull, "test001_baked_smoothed.csv"), sampleRate);

        Debug.Log($"[Rokoko MPJPE Pipeline] Wrote reference/raw/smoothed CSVs to {csvDirFull}");
        EditorUtility.DisplayDialog("Rokoko MPJPE Pipeline",
            $"Done. CSVs written to:\n{csvDirFull}\n\nRun documentation/rokoko-mpjpe-eval.ipynb to compare.", "OK");
    }

    static AnimationClip BakeAndLoad(Actor actor, string jsonlFullPath, string bakeFolder,
        string suffix, RokokoJsonlBaker.BakeOptions options)
    {
        string outPath = AssetDatabase.GenerateUniqueAssetPath($"{bakeFolder}/{actor.gameObject.name}_{suffix}.anim");
        int applied;
        try
        {
            applied = RokokoJsonlBaker.BakeToClip(actor, jsonlFullPath, outPath, options);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (applied == 0)
        {
            EditorUtility.DisplayDialog("Rokoko MPJPE Pipeline",
                $"Baking \"{suffix}\" produced no frames -- aborting.", "OK");
            return null;
        }

        return AssetDatabase.LoadAssetAtPath<AnimationClip>(outPath);
    }
}
#endif
