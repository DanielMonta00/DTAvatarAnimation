#if UNITY_EDITOR
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor utility: samples an AnimationClip against a Humanoid Animator's GameObject at a fixed
/// rate using AnimationClip.SampleAnimation -- the same evaluation Unity itself uses, so muscle
/// curves go through the real Avatar retargeting and generic per-bone curves go through the real
/// hierarchy -- and writes each resulting Humanoid bone's world position to CSV.
///
/// Exists to get comparable joint-position tracks out of two differently-shaped clips (a
/// recorded reference clip with per-bone Transform curves, and a Rokoko-baked clip with Humanoid
/// muscle curves) for the MPJPE comparison in documentation/rokoko-mpjpe-eval.ipynb -- reusing
/// SampleAnimation instead of re-deriving Humanoid muscle retargeting in Python.
/// </summary>
public class RokokoClipJointExporter : EditorWindow
{
    GameObject target;
    AnimationClip clip;
    string outputPath = "documentation/mpjpe/clip.csv";
    float sampleRate = 30f;

    [MenuItem("Rokoko/Export Clip Joint Positions to CSV...")]
    static void Open() => GetWindow<RokokoClipJointExporter>("Export Joint Positions");

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Samples an AnimationClip on the given GameObject's Animator at a fixed rate and "
            + "writes every Humanoid bone's resulting world position to CSV -- point this at the "
            + "same character GameObject the clip was recorded/baked on. Run it once per clip "
            + "you want to compare (reference, baked, baked+smoothing), using the same Sample "
            + "Rate each time so the notebook can line frames up directly.",
            MessageType.None);
        EditorGUILayout.Space();

        target = (GameObject)EditorGUILayout.ObjectField("Character (Animator)", target, typeof(GameObject), true);
        clip = (AnimationClip)EditorGUILayout.ObjectField("Clip", clip, typeof(AnimationClip), false);
        sampleRate = EditorGUILayout.FloatField("Sample Rate (Hz)", sampleRate);
        outputPath = EditorGUILayout.TextField("Output CSV Path", outputPath);

        Animator animator = target != null ? target.GetComponent<Animator>() : null;
        string blockReason = null;
        if (target == null)
            blockReason = "Assign a character GameObject.";
        else if (animator == null)
            blockReason = "That GameObject has no Animator.";
        else if (!animator.isHuman)
            blockReason = "That Animator isn't Humanoid.";
        else if (clip == null)
            blockReason = "Assign an AnimationClip.";
        else if (sampleRate <= 0f)
            blockReason = "Sample Rate must be > 0.";

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(blockReason != null))
        {
            if (GUILayout.Button("Export", GUILayout.Height(28)))
                ExportToCsv(animator, clip, outputPath, sampleRate);
        }
        if (blockReason != null)
            EditorGUILayout.HelpBox(blockReason, MessageType.Info);
    }

    /// <summary>
    /// Samples `clip` on `animator`'s GameObject at `sampleRate` Hz and writes every Humanoid
    /// bone's resulting world position to a CSV at `outputPath` (project-root-relative unless
    /// rooted). Shared by this window and RokokoMpjpeEval's one-click pipeline so both write
    /// identical CSVs.
    /// </summary>
    public static void ExportToCsv(Animator animator, AnimationClip clip, string outputPath, float sampleRate)
    {
        // Sampling the clip for export mutates the live scene rig frame-by-frame (same as
        // BakeToClip's own evaluation does) -- snapshot every Transform under the Animator now
        // and restore it below so this tool never permanently moves the character.
        Transform[] allTransforms = animator.transform.GetComponentsInChildren<Transform>(true);
        var snapLocalPos = new Vector3[allTransforms.Length];
        var snapLocalRot = new Quaternion[allTransforms.Length];
        for (int t = 0; t < allTransforms.Length; t++)
        {
            snapLocalPos[t] = allTransforms[t].localPosition;
            snapLocalRot[t] = allTransforms[t].localRotation;
        }

        var sb = new StringBuilder();
        sb.AppendLine("time,joint,x,y,z");
        int sampleCount = 0;

        try
        {
            int frameCount = Mathf.Max(1, Mathf.CeilToInt(clip.length * sampleRate));
            for (int i = 0; i <= frameCount; i++)
            {
                float time = Mathf.Min(i / sampleRate, clip.length);
                clip.SampleAnimation(animator.gameObject, time);

                for (HumanBodyBones bone = 0; bone < HumanBodyBones.LastBone; bone++)
                {
                    Transform boneTransform = animator.GetBoneTransform(bone);
                    if (boneTransform == null)
                        continue;

                    Vector3 p = boneTransform.position;
                    sb.AppendLine(string.Join(",",
                        time.ToString("R", CultureInfo.InvariantCulture),
                        bone.ToString(),
                        p.x.ToString("R", CultureInfo.InvariantCulture),
                        p.y.ToString("R", CultureInfo.InvariantCulture),
                        p.z.ToString("R", CultureInfo.InvariantCulture)));
                }
                sampleCount++;

                if (time >= clip.length)
                    break;
            }
        }
        finally
        {
            for (int t = 0; t < allTransforms.Length; t++)
            {
                allTransforms[t].localPosition = snapLocalPos[t];
                allTransforms[t].localRotation = snapLocalRot[t];
            }
        }

        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string fullPath = Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(projectRoot, outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        File.WriteAllText(fullPath, sb.ToString());

        Debug.Log($"[Rokoko Export] Wrote {sampleCount} sample(s) of \"{clip.name}\" -> {fullPath}");
    }
}
#endif
