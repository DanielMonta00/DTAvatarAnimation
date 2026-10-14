#if UNITY_EDITOR
using System.Globalization;
using UnityEditor;
using UnityEngine;

// Inspector for PoseEstimateFollower: in Play mode the live numbers that answer "does the capsule keep up with the operator?"
[CustomEditor(typeof(PoseEstimateFollower))]
public class PoseEstimateFollowerEditor : Editor
{
    public override bool RequiresConstantRepaint() => Application.isPlaying;

    static string F(float v, string fmt = "F0") => v.ToString(fmt, CultureInfo.InvariantCulture);

    public override void OnInspectorGUI()
    {
        var t = (PoseEstimateFollower)target;

        if (Application.isPlaying) DrawLive(t);
        else
        {
            EditorGUILayout.HelpBox(
                "Press Play: this object goes to the nearest person the pose model confirms and follows him using only what the cameras show. " +
                "Give 'Truth Avatar' the real operator's Animator and the Inspector (and the Console when Play stops) shows how well it kept up. " +
                "The real operator never moves the capsule.", MessageType.Info);
        }

        DrawDefaultInspector();
    }

    static void DrawLive(PoseEstimateFollower t)
    {
        var state = t.TrackState;
        string what = state == OperatorTrack.State.Tracking ? $"FOLLOWING person #{t.LockedId}"
                    : state == OperatorTrack.State.Lost ? $"LOST person #{t.LockedId} (holding the last position)"
                    : "SEARCHING for the operator";
        EditorGUILayout.HelpBox($"{what}\nsource: {t.SourceName} - {t.SourceStatus}\n{F(t.EstimatesPerSecond, "F1")} estimates/s, {t.EstimatesSeen} so far",
            state == OperatorTrack.State.Tracking ? MessageType.None : MessageType.Warning);

        if (t.PipelineMs.Count > 0)
            EditorGUILayout.LabelField("Latency (capture -> people)", $"median {F(t.PipelineMs.Percentile(0.5f))} ms   p95 {F(t.PipelineMs.Percentile(0.95f))}   max {F(t.PipelineMs.Max)}");
        if (t.AgeMs.Count > 0)
            EditorGUILayout.LabelField("Age of the placement", $"median {F(t.AgeMs.Percentile(0.5f))} ms   p95 {F(t.AgeMs.Percentile(0.95f))}   max {F(t.AgeMs.Max)}");
        if (t.TruthFrames > 0)
        {
            EditorGUILayout.LabelField("Capsule vs the operator", $"median {F(t.ErrorMetres.Percentile(0.5f) * 100f, "F1")} cm   p95 {F(t.ErrorMetres.Percentile(0.95f) * 100f, "F1")}   max {F(t.ErrorMetres.Max * 100f, "F1")}");
            if (t.RawErrorMetres.Count > 0)
                EditorGUILayout.LabelField("Newest estimate as it is", $"median {F(t.RawErrorMetres.Percentile(0.5f) * 100f, "F1")} cm   p95 {F(t.RawErrorMetres.Percentile(0.95f) * 100f, "F1")}   max {F(t.RawErrorMetres.Max * 100f, "F1")}");
            EditorGUILayout.LabelField("Body inside the capsule", $"{F(100f * t.InsideFrames / t.TruthFrames, "F1")} %  of {t.TruthFrames} frames (radius {F(t.CapsuleRadius, "F2")} m)");
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Print report")) Debug.Log(t.BuildReport(), t);
            if (GUILayout.Button("Reset statistics")) t.ResetStatistics();
        }
        EditorGUILayout.Space();
    }
}
#endif
