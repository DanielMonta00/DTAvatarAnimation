#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Tools > Calibration > Install cam107 cameras: creates cam107-main and cam107-sub
// (CalibratedCamera) under a "CalibratedCameras" root in the active scene, wired to
// the JSON copies in Assets/Calibration and to the scene's "MOCAP Center".
// Safe to re-run: existing objects are reused and re-wired.
public static class CalibratedCameraInstaller
{
    const string Dir = "Assets/Calibration";
    const string FrameName = "MOCAP Center";
    const string RootName = "CalibratedCameras";
    const string CameraKey = "cam107";

    [MenuItem("Tools/Calibration/Install cam107 cameras (main + sub)")]
    public static void InstallCam107()
    {
        AssetDatabase.Refresh();
        var extrinsics = AssetDatabase.LoadAssetAtPath<TextAsset>($"{Dir}/camera_extrinsics.json");
        var main = AssetDatabase.LoadAssetAtPath<TextAsset>($"{Dir}/{CameraKey}-main.json");
        var sub = AssetDatabase.LoadAssetAtPath<TextAsset>($"{Dir}/{CameraKey}-sub.json");
        if (extrinsics == null || main == null || sub == null)
        {
            EditorUtility.DisplayDialog("Install cam107 cameras",
                $"Missing calibration JSON in {Dir} (need camera_extrinsics.json, {CameraKey}-main.json, {CameraKey}-sub.json).", "OK");
            return;
        }

        Scene scene = SceneManager.GetActiveScene();
        Transform frame = FindByName(scene, FrameName);
        if (frame == null)
        {
            EditorUtility.DisplayDialog("Install cam107 cameras",
                $"No '{FrameName}' GameObject in scene '{scene.name}'. Open the mocap scene and run again.", "OK");
            return;
        }

        Transform root = FindByName(scene, RootName);
        if (root == null)
        {
            var go = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(go, "Create " + RootName);
            root = go.transform;
        }

        Transform camMain = Install(root, $"{CameraKey}-main", main, extrinsics, frame);
        Install(root, $"{CameraKey}-sub", sub, extrinsics, frame);

        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeTransform = camMain;
        Debug.Log($"[CalibratedCamera] Installed {CameraKey}-main / {CameraKey}-sub under '{RootName}', frame = '{FrameName}' ({frame.position}).");
    }

    static Transform Install(Transform root, string name, TextAsset intrinsics, TextAsset extrinsics, Transform frame)
    {
        Transform t = root.Find(name);
        GameObject go;
        if (t != null) go = t.gameObject;
        else
        {
            go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(root, false);
        }

        var cam = go.GetComponent<Camera>();
        if (cam == null) cam = Undo.AddComponent<Camera>(go);
        // Below every scene camera, so these never take over the Game view.
        cam.depth = -20f;

        var cc = go.GetComponent<CalibratedCamera>();
        if (cc == null) cc = Undo.AddComponent<CalibratedCamera>(go);
        Undo.RecordObject(cc, "Wire " + name);
        cc.intrinsicsJson = intrinsics;
        cc.extrinsicsJson = extrinsics;
        cc.cameraKey = CameraKey;
        cc.worldFrame = frame;
        cc.Apply();
        EditorUtility.SetDirty(cc);
        return go.transform;
    }

    // Includes inactive objects, which GameObject.Find skips.
    static Transform FindByName(Scene scene, string name)
    {
        foreach (GameObject r in scene.GetRootGameObjects())
        {
            Transform hit = FindIn(r.transform, name);
            if (hit != null) return hit;
        }
        return null;
    }

    static Transform FindIn(Transform t, string name)
    {
        if (t.name == name) return t;
        foreach (Transform child in t)
        {
            Transform hit = FindIn(child, name);
            if (hit != null) return hit;
        }
        return null;
    }
}

// Re-exporting from PTZCalibration and dropping the files into Assets/Calibration
// updates every CalibratedCamera in the open scene.
class CalibrationJsonPostprocessor : AssetPostprocessor
{
    static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        bool touched = false;
        foreach (string p in imported)
            if (p.StartsWith("Assets/Calibration/") && p.EndsWith(".json")) { touched = true; break; }
        if (!touched) return;

        EditorApplication.delayCall += () =>
        {
            foreach (var cc in Object.FindObjectsByType<CalibratedCamera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                cc.Apply();
        };
    }
}
#endif
