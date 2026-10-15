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
                "Press Play: this object goes to the nearest person the pose model confirms and follows them using only what the cameras show. " +
                "Give 'Truth Avatar' the real operator's Animator and the Inspector (and the Console when Play stops) shows how well it kept up. " +
                "The real operator never moves the capsule.\n" +
                "The bubble is never in the images of the cameras the model reads (it would hide the operator from the model). On those cameras' " +
                "displays it is put back afterwards: the real objects are rendered by a second, hidden camera and shown over the display (or, " +
                "where a camera cannot do that, drawn as a flat outline); it goes grey while the operator is not followed. Everywhere else " +
                "(Scene view, other displays) you see the object itself.\n" +
                "Fit To Person sizes the capsule to the body it follows, in any pose: a hand held out or up widens / raises it, sitting lowers it. " +
                "The authored size is where it starts and what it goes back to when nobody is followed.", MessageType.Info);
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

        if (t.FitActive)
        {
            FvpCapsuleFit.Shape s = t.FitShape;
            EditorGUILayout.LabelField("Capsule size (fitted)", $"radius {F(s.radius, "F2")} m   tip to tip {F(s.top - s.bottom, "F2")} m   tips {F(s.bottom, "F2")} .. {F(s.top, "F2")}" +
                                                                 (t.FitClamped ? "   AT THE RADIUS LIMIT" : ""));
            if (t.FitFrames > 0)
                EditorGUILayout.LabelField("Whole body inside it", $"{F(100f * t.FitInsideFrames / t.FitFrames, "F1")} %  of {t.FitFrames} frames" +
                                                                    (t.FitShortfallMetres.Count > 0 ? $"   (when not: {F(t.FitShortfallMetres.Percentile(0.5f) * 100f, "F1")} cm short, worst {F(t.FitShortfallMetres.Max * 100f, "F1")})" : ""));
        }
        else if (t.fitToPerson && t.FitStatus != "off" && t.FitStatus.Length > 0)
            EditorGUILayout.HelpBox("Fit To Person is not used: " + t.FitStatus, MessageType.Warning);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Print report")) Debug.Log(t.BuildReport(), t);
            if (GUILayout.Button("Reset statistics")) t.ResetStatistics();
        }
        EditorGUILayout.Space();
    }
}
#endif
