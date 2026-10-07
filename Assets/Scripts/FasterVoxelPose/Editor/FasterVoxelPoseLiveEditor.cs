#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// Inspector for FasterVoxelPoseLive: setup buttons, and in Play mode the status line plus the same transport
// controls as the monitor on the Game view (handy when the Scene view has the focus).
[CustomEditor(typeof(FasterVoxelPoseLive))]
public class FasterVoxelPoseLiveEditor : Editor
{
    public override bool RequiresConstantRepaint() => Application.isPlaying;

    public override void OnInspectorGUI()
    {
        var t = (FasterVoxelPoseLive)target;

        if (Application.isPlaying) DrawPlayControls(t);
        else
        {
            EditorGUILayout.HelpBox(
                "Press Play: the server starts (first time: ~10 s to load the model), the monitor appears on the Game view and the " +
                "skeletons are drawn as gizmos. The cyan box is the capture volume - people are only found inside it.",
                MessageType.Info);
        }

        DrawDefaultInspector();

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Auto-wire from scene"))
            {
                Undo.RecordObject(t, "Auto-wire FasterVoxelPose");
                t.AutoWire();
                EditorUtility.SetDirty(t);
            }
            if (GUILayout.Button("Start server")) FasterVoxelPoseInstaller.StartServer(t);
            if (GUILayout.Button("Stop server")) FasterVoxelPoseInstaller.StopServer();
            if (GUILayout.Button("Open log")) FasterVoxelPoseInstaller.OpenLog();
        }
    }

    static void DrawPlayControls(FasterVoxelPoseLive t)
    {
        string state = t.IsPaused ? (t.Rewinding < 0 ? "REWIND" : t.Rewinding > 0 ? "REPLAY" : "PAUSED") : "LIVE";
        var people = t.People;
        EditorGUILayout.HelpBox($"{state} - {t.Status}\n" +
                                $"{people.Count} person(s) on show   {t.EstimateFps:F1} estimates/s   history {t.HistoryCount} frames (cursor {t.Cursor + 1})",
                                t.IsReady ? MessageType.None : MessageType.Warning);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("|<")) t.GoToStart();
            if (GUILayout.Toggle(t.Rewinding < 0, "<<", "Button") != (t.Rewinding < 0)) t.ToggleRewind();
            if (GUILayout.Button("<")) t.StepBackward();
            if (GUILayout.Button(t.IsPaused ? "Play" : "Pause", GUILayout.MinWidth(70))) t.TogglePause();
            if (GUILayout.Button(">")) t.StepForward();
            if (GUILayout.Toggle(t.Rewinding > 0, ">>", "Button") != (t.Rewinding > 0)) t.ToggleReplay();
            if (GUILayout.Button(">|")) t.GoToNewest();
        }

        if (t.HistoryCount > 1)
        {
            int c = EditorGUILayout.IntSlider("Frame", t.Cursor + 1, 1, t.HistoryCount) - 1;
            if (c != t.Cursor) t.Scrub(c);
        }
        EditorGUILayout.Space();
    }
}
#endif
