using UnityEngine;
#if UNITY_EDITOR
using System.IO;
using Rokoko.Inputs;
using UnityEditor;
#endif

/// <summary>
/// Attach this directly to a character's GameObject (the one with its Humanoid Animator).
/// Drag a recorded rokoko_skeleton.jsonl onto this component's Inspector and click Bake --
/// adding the required Rokoko Actor component, assigning its T-pose reference and running
/// the actual retargeting bake (see RokokoJsonlBaker.BakeToClip) all happen automatically
/// from here. Produces a Humanoid AnimationClip you can drop straight into an Animator
/// Controller for this character.
///
/// The fields below are plain data so this component can stay on a character prefab
/// without side effects; all of the baking logic is editor-only and stripped from
/// player builds.
/// </summary>
public class RokokoMocapBaker : MonoBehaviour
{
    [Tooltip("Recorded rokoko_skeleton.jsonl to bake. Drag a file onto the Inspector below, or Browse.")]
    public string jsonlPath = "";

    [Tooltip("Project folder the baked .anim clip is written into.")]
    public string outputFolder = "Assets/Animations/RokokoBakes";
}

#if UNITY_EDITOR
[CustomEditor(typeof(RokokoMocapBaker))]
public class RokokoMocapBakerEditor : Editor
{
    void OnEnable()
    {
        EnsureActorSetUp();
    }

    public override void OnInspectorGUI()
    {
        var baker = (RokokoMocapBaker)target;
        Actor actor = EnsureActorSetUp();

        EditorGUILayout.HelpBox(
            "Drop a recorded rokoko_skeleton.jsonl below and click Bake. Actor setup, "
            + "T-pose assignment and Humanoid retargeting are handled automatically.",
            MessageType.None);
        EditorGUILayout.Space();

        Animator animator = baker.GetComponent<Animator>();
        if (animator == null)
        {
            EditorGUILayout.HelpBox("This GameObject has no Animator component.", MessageType.Error);
            return;
        }
        if (!animator.isHuman)
        {
            EditorGUILayout.HelpBox(
                "This Animator isn't Humanoid. Select the model asset -> Rig tab -> "
                + "Animation Type: Humanoid, then come back here.", MessageType.Error);
            return;
        }

        EditorGUILayout.Space();
        bool hasTPose = actor.characterTPose.Count > 0;
        EditorGUILayout.LabelField("T-pose reference", hasTPose ? "Assigned" : "Not assigned");
        if (!hasTPose)
            EditorGUILayout.HelpBox("Pose the character in T-pose right now, then click below.", MessageType.Warning);
        if (GUILayout.Button(hasTPose ? "Recalculate T-Pose" : "Assign T-Pose Now"))
        {
            Undo.RecordObject(actor, "Assign Actor T-Pose");
            actor.CalculateTPose();
            if (!actor.isValidTpose)
                EditorUtility.DisplayDialog("Rokoko Bake",
                    "Doesn't look like a T-pose (arms/spine aren't aligned). Rotate the "
                    + "character to match a T-pose and try again.", "OK");
        }

        EditorGUILayout.Space();
        DrawDropArea(baker);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Browse...", GUILayout.Width(80)))
        {
            string picked = EditorUtility.OpenFilePanel("Select rokoko_skeleton.jsonl", "", "jsonl");
            if (!string.IsNullOrEmpty(picked))
            {
                Undo.RecordObject(baker, "Set Rokoko JSONL Path");
                baker.jsonlPath = picked;
            }
        }
        if (GUILayout.Button("Clear", GUILayout.Width(60)))
        {
            Undo.RecordObject(baker, "Clear Rokoko JSONL Path");
            baker.jsonlPath = "";
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        EditorGUI.BeginChangeCheck();
        string newOutputFolder = EditorGUILayout.TextField("Output Folder", baker.outputFolder);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(baker, "Change Rokoko Output Folder");
            baker.outputFolder = newOutputFolder;
        }

        EditorGUILayout.Space();
        string blockReason = RokokoJsonlBaker.Validate(actor);
        if (blockReason == null && string.IsNullOrEmpty(baker.jsonlPath))
            blockReason = "Drop a .jsonl file above first.";

        using (new EditorGUI.DisabledScope(blockReason != null))
        {
            if (GUILayout.Button("Bake Animation Clip", GUILayout.Height(32)))
                Bake(baker, actor);
        }
        if (blockReason != null)
            EditorGUILayout.HelpBox(blockReason, MessageType.Info);

        if (GUI.changed)
            EditorUtility.SetDirty(baker);
    }

    // Adds the Rokoko Actor component and wires its Animator if missing, and computes a
    // T-pose reference the first time one hasn't been assigned yet. Safe to call every
    // repaint -- each step only runs once (idempotent once actor/animator/T-pose exist).
    Actor EnsureActorSetUp()
    {
        var baker = (RokokoMocapBaker)target;
        Animator animator = baker.GetComponent<Animator>();
        if (animator == null || !animator.isHuman)
            return baker.GetComponent<Actor>();

        Actor actor = baker.GetComponent<Actor>();
        if (actor == null)
        {
            actor = Undo.AddComponent<Actor>(baker.gameObject);
            actor.animator = animator;
        }
        else if (actor.animator == null)
        {
            Undo.RecordObject(actor, "Assign Actor Animator");
            actor.animator = animator;
        }

        // Only auto-calculate once -- if a T-pose is already stored, leave it alone even
        // if the character happens to be posed differently right now (e.g. left mid-motion
        // after a previous bake); don't silently clobber a good reference.
        if (actor.characterTPose.Count == 0)
            actor.CalculateTPose();

        return actor;
    }

    void DrawDropArea(RokokoMocapBaker baker)
    {
        Rect dropArea = GUILayoutUtility.GetRect(0, 44, GUILayout.ExpandWidth(true));
        GUI.Box(dropArea, string.IsNullOrEmpty(baker.jsonlPath) ? "Drag rokoko_skeleton.jsonl here" : baker.jsonlPath);

        Event evt = Event.current;
        if (!dropArea.Contains(evt.mousePosition))
            return;

        if (evt.type == EventType.DragUpdated)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            evt.Use();
        }
        else if (evt.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            foreach (string path in DragAndDrop.paths)
            {
                Undo.RecordObject(baker, "Set Rokoko JSONL Path");
                baker.jsonlPath = path;
                break;
            }
            evt.Use();
        }
    }

    void Bake(RokokoMocapBaker baker, Actor actor)
    {
        string folder = baker.outputFolder.Replace('\\', '/');
        if (!folder.StartsWith("Assets"))
        {
            EditorUtility.DisplayDialog("Rokoko Bake", "Output Folder must be a path inside this project's Assets folder.", "OK");
            return;
        }
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
        }

        string baseName = Path.GetFileNameWithoutExtension(baker.jsonlPath);
        string outPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{baker.gameObject.name}_{baseName}.anim");

        RokokoJsonlBaker.RunBake(actor, baker.jsonlPath, outPath);
    }
}
#endif
