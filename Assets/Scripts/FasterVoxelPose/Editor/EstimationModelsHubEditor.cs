#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// The hub's Inspector: the models found under it with their state, and (while playing) buttons to select the one whose panels
// are shown and to hide / show each model's skeletons.
[CustomEditor(typeof(EstimationModelsHub))]
public sealed class EstimationModelsHubEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var hub = (EstimationModelsHub)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Models (the children of this object)", EditorStyles.boldLabel);
        IEstimationModel[] found = hub.GetComponentsInChildren<IEstimationModel>(true);
        if (found.Length == 0)
        {
            EditorGUILayout.HelpBox("No model under this object. Put each model on its own child, e.g. FasterVoxelPose and ViTPose (Tools > FasterVoxelPose > Add ViTPose + models hub).", MessageType.Info);
            return;
        }

        for (int i = 0; i < found.Length; i++)
        {
            IEstimationModel m = found[i];
            using (new EditorGUILayout.HorizontalScope())
            {
                Color prev = GUI.color;
                GUI.color = m.ModelColor;
                GUILayout.Label("■", GUILayout.Width(16));
                GUI.color = prev;
                EditorGUILayout.LabelField(m.ModelName, GUILayout.Width(120));
                EditorGUILayout.LabelField(Application.isPlaying ? m.Summary : "(plays with the scene)", EditorStyles.miniLabel);
                if (Application.isPlaying)
                {
                    bool sel = i == hub.selected;
                    if (GUILayout.Toggle(sel, "panels", EditorStyles.miniButton, GUILayout.Width(56)) && !sel) hub.Select(i);
                    bool on = hub.OverlayOn(m);
                    bool now = GUILayout.Toggle(on, "skeletons", EditorStyles.miniButton, GUILayout.Width(66));
                    if (now != on) hub.SetOverlay(m, now);
                }
            }
        }
        if (Application.isPlaying && hub.Fvp != null)
        {
            IEstimationModel gt = hub.Fvp.GroundTruthEntry;     // not a child: the last tab of the strip, only switches the green skeletons
            using (new EditorGUILayout.HorizontalScope())
            {
                Color prev = GUI.color;
                GUI.color = gt.ModelColor;
                GUILayout.Label("■", GUILayout.Width(16));
                GUI.color = prev;
                EditorGUILayout.LabelField(gt.ModelName, GUILayout.Width(120));
                EditorGUILayout.LabelField(gt.Summary, EditorStyles.miniLabel);
                bool on = hub.OverlayOn(gt);
                bool now = GUILayout.Toggle(on, "skeletons", EditorStyles.miniButton, GUILayout.Width(66));
                if (now != on) hub.SetOverlay(gt, now);
            }
        }
        if (Application.isPlaying) Repaint();
    }
}
#endif
