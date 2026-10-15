#if UNITY_EDITOR
using System.Globalization;
using UnityEditor;
using UnityEngine;

// Inspector for the lean estimator: the status line and the numbers of the session in Play mode, the usual buttons otherwise.
[CustomEditor(typeof(FasterVoxelPoseLite))]
public class FasterVoxelPoseLiteEditor : Editor
{
    public override bool RequiresConstantRepaint() => Application.isPlaying;

    static string F(float v, string fmt = "F0") => v.ToString(fmt, CultureInfo.InvariantCulture);

    public override void OnInspectorGUI()
    {
        var t = (FasterVoxelPoseLite)target;

        if (Application.isPlaying)
        {
            EditorGUILayout.HelpBox($"{t.Status}\n{t.People.Count} person(s)   {F(t.EstimateFps, "F1")} estimates/s   {t.Estimates} so far" +
                                    (t.LatencyMs.Count > 0 ? $"\ncapture -> people: median {F(t.LatencyMs.Percentile(0.5f))} ms, p95 {F(t.LatencyMs.Percentile(0.95f))} ms" : "") +
                                    (t.PhantomsHidden > 0 ? $"\n{t.PhantomsHidden} phantoms hidden so far" : ""),
                t.IsReady ? MessageType.None : MessageType.Warning);
            if (GUILayout.Button("Print report")) Debug.Log(t.BuildReport(), t);
        }
        else
        {
            EditorGUILayout.HelpBox(
                "The lean twin of FasterVoxelPose: the same server and models, none of the displays, history or rewind. It hands the people to " +
                "PoseEstimateFollower (and any script: People, EstimateReady). While it is enabled it switches the full FasterVoxelPose component off for each " +
                "Play session (the server answers one client at a time). It draws the skeletons it finds over each camera's display (a light screen " +
                "overlay, not in the camera images); the transport bar, heatmaps and rewind are the full component's.", MessageType.Info);
        }

        DrawDefaultInspector();

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Auto-wire from scene"))
            {
                Undo.RecordObject(t, "Auto-wire FasterVoxelPoseLite");
                t.AutoWire();
                EditorUtility.SetDirty(t);
            }
            if (GUILayout.Button("Stop server")) FasterVoxelPoseInstaller.StopServer();
            if (GUILayout.Button(new GUIContent("Stop ALL servers", "Also the servers left running by earlier editor sessions, which 'Stop server' does not reach."))) FasterVoxelPoseLiteInstaller.StopAllServers();
            if (GUILayout.Button("Open log")) FasterVoxelPoseInstaller.OpenLog();
        }
    }
}
#endif
