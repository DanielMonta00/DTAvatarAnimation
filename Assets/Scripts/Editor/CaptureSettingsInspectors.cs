#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// Hides customWidth/customHeight unless captureResolution == Custom on
// RecordingSession and MultiViewRecorder. Everything else falls through to
// the default Inspector layout.

[CustomEditor(typeof(RecordingSession))]
public class RecordingSessionEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawWithConditionalCustomSize(serializedObject, "captureResolution");
    }

    public static void DrawWithConditionalCustomSize(SerializedObject so, string resolutionFieldName)
    {
        so.Update();
        SerializedProperty prop = so.GetIterator();
        bool enterChildren = true;
        while (prop.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (prop.name == "m_Script")
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.PropertyField(prop);
                continue;
            }

            if (prop.name == "customWidth" || prop.name == "customHeight")
            {
                var resProp = so.FindProperty(resolutionFieldName);
                if (resProp == null) { EditorGUILayout.PropertyField(prop, true); continue; }
                // CaptureResolution.Custom is the last value in the enum;
                // we compare via enumValueIndex against the named entry.
                int customIdx = System.Array.IndexOf(System.Enum.GetNames(typeof(CaptureResolution)), "Custom");
                if (resProp.enumValueIndex != customIdx) continue; // skip drawing
            }

            EditorGUILayout.PropertyField(prop, true);
        }
        so.ApplyModifiedProperties();
    }
}

[CustomEditor(typeof(MultiViewRecorder))]
public class MultiViewRecorderEditor : Editor
{
    public override void OnInspectorGUI()
    {
        RecordingSessionEditor.DrawWithConditionalCustomSize(serializedObject, "captureResolution");
    }
}
#endif
