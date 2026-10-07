#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Tools > Calibration > Install cam107 cameras: creates cam107-main and cam107-sub
// (CalibratedCamera) under a "CalibratedCameras" root in the active scene, wired to
// the JSON copies in Assets/Calibration and to the scene's "MOCAPcenter".
// Safe to re-run: existing objects are reused and re-wired.
public static class CalibratedCameraInstaller
{
    // Calibration set for the PTZ pose the recorded datasets use (azimuth 2100 / elevation 350).
    const string Dir = "Assets/Calibration/pose_2100_350";
    // Exact name; the scene also has an unrelated "MOCAP Center" (with a space) that must stay ignored.
    const string FrameName = "MOCAPcenter";
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
        if (cc.distortionShader == null) cc.distortionShader = Shader.Find(CalibratedCamera.DistortionShaderName);
        cc.Apply();
        EditorUtility.SetDirty(cc);
        return go.transform;
    }


    const string AvatarParentName = "HumanDataset";
    const string AvatarName = "OperatorPete";
    const string RegistrationPath = "Assets/Calibration/test01_20261001_172733_registration.json";

    [MenuItem("Tools/Calibration/Place HumanDataset avatar (OperatorPete) at dataset registration")]
    public static void PlaceHumanDatasetAvatar()
    {
        AssetDatabase.Refresh();
        var registration = AssetDatabase.LoadAssetAtPath<TextAsset>(RegistrationPath);
        if (registration == null)
        {
            EditorUtility.DisplayDialog("Place avatar", $"Missing {RegistrationPath}.", "OK");
            return;
        }

        Scene scene = SceneManager.GetActiveScene();
        Transform frame = FindByName(scene, FrameName);
        Transform parent = FindByName(scene, AvatarParentName);
        if (frame == null || parent == null)
        {
            EditorUtility.DisplayDialog("Place avatar",
                $"Need '{FrameName}' and '{AvatarParentName}' in scene '{scene.name}'.", "OK");
            return;
        }

        // The component drives `parent`; the baked clip's frame is the parent's local frame, so
        // the avatar must sit at identity under it.
        Transform avatar = parent.Find(AvatarName);
        if (avatar == null)
            Debug.LogWarning($"[RokokoFramePlacement] '{AvatarParentName}' has no child '{AvatarName}'.");
        else if (avatar.localPosition.sqrMagnitude > 1e-6f || Quaternion.Angle(avatar.localRotation, Quaternion.identity) > 0.01f)
            Debug.LogWarning($"[RokokoFramePlacement] '{AvatarName}' is not at identity under '{AvatarParentName}' " +
                             $"(local {avatar.localPosition}, {avatar.localEulerAngles}); its offset adds to the placement.");

        var placement = parent.GetComponent<RokokoFramePlacement>();
        if (placement == null) placement = Undo.AddComponent<RokokoFramePlacement>(parent.gameObject);
        Undo.RecordObject(placement, "Wire placement");
        Undo.RecordObject(parent, "Place " + AvatarParentName);
        placement.registrationJson = registration;
        placement.worldFrame = frame;
        placement.Apply();
        EditorUtility.SetDirty(placement);

        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeTransform = parent;
        Debug.Log($"[RokokoFramePlacement] '{AvatarParentName}' placed at {parent.position}, yaw {parent.eulerAngles.y:F2} " +
                  $"(frame '{FrameName}' {frame.position}).");
    }

    // ---- Placeholder calibration for cam103 / cam120 ----

    const string PlaceholderDir = "Assets/Calibration/placeholder_103_120";

    // Tools > Calibration > Fabricate placeholder calibration (cam103 + cam120)
    //
    // For cameras whose CalibratedCamera was switched off so they could be dragged into place:
    // reads each camera's CURRENT placement, writes the extrinsics that would reproduce it
    // (relative to MOCAPcenter, OpenCV rvec/tvec in meters, flagged calibrated:false because they
    // are made up), wires the component to the real intrinsics copied into the same folder and
    // switches it back on. Cameras are found by a "103" or "120" in their name; if none match,
    // the selected cameras are used. Replace camera_extrinsics.json with a real export later.
    [MenuItem("Tools/Calibration/Fabricate placeholder calibration (cam103 + cam120)")]
    public static void FabricatePlaceholderCalibration()
    {
        AssetDatabase.Refresh();
        Scene scene = SceneManager.GetActiveScene();
        Transform frame = FindByName(scene, FrameName);
        if (frame == null)
        {
            EditorUtility.DisplayDialog("Placeholder calibration", $"No '{FrameName}' in scene '{scene.name}'.", "OK");
            return;
        }

        // key ("cam103"), stream ("main" / "sub") per camera.
        var cams = new List<(string key, string stream, CalibratedCamera cc)>();
        foreach (var cc in Object.FindObjectsByType<CalibratedCamera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            AddIfPlaceholderCamera(cams, cc);
        if (cams.Count == 0)
            foreach (var go in Selection.gameObjects)
                if (go.TryGetComponent(out CalibratedCamera selected)) AddIfPlaceholderCamera(cams, selected);
        if (cams.Count == 0)
        {
            EditorUtility.DisplayDialog("Placeholder calibration",
                "No CalibratedCamera with '103' or '120' in its name. Rename the two cameras (e.g. cam103-main, cam120-main) " +
                "or select them in the Hierarchy and run again.", "OK");
            return;
        }

        // 1) Capture each camera's pose BEFORE touching the components (Apply() moves the camera).
        var poses = new Dictionary<string, (Vector3 pos, Quaternion rot, CalibratedCamera.WorldMirror mirror)>();
        Quaternion fr = frame.rotation;
        foreach (var (key, stream, cc) in cams)
        {
            Vector3 p = Quaternion.Inverse(fr) * (cc.transform.position - frame.position);
            Quaternion q = Quaternion.Inverse(fr) * cc.transform.rotation;
            if (!poses.TryGetValue(key, out var first)) { poses[key] = (p, q, cc.handedness); continue; }
            if (Vector3.Distance(first.pos, p) > 0.02f || Quaternion.Angle(first.rot, q) > 0.5f)
                Debug.LogWarning($"[Placeholder calibration] '{cc.name}' is not where the other {key} camera is " +
                                 $"({Vector3.Distance(first.pos, p):F2} m apart); using the first one's placement for both streams.");
        }

        // 2) Write the extrinsics file.
        var sb = new StringBuilder(1024);
        sb.Append("{\n  \"_comment\": \"PLACEHOLDER extrinsics, fabricated from where the cameras were placed by hand in the Unity scene ")
          .Append("(relative to MOCAPcenter). Not a calibration: replace with a real PTZCalibration export. ")
          .Append("Same convention as camera_extrinsics.json: X_cam = R*X_world + t, R = cv2.Rodrigues(rvec), meters.\",\n  \"cameras\": {");
        bool firstKey = true;
        foreach (var kv in poses.OrderBy(k => k.Key))
        {
            CalibratedCamera.ExtrinsicsFromPose(kv.Value.pos, kv.Value.rot, kv.Value.mirror, out double[] rvec, out double[] tvec);
            sb.Append(firstKey ? "\n" : ",\n").Append("    \"").Append(kv.Key).Append("\": {\n");
            sb.Append("      \"rvec\": ").Append(Vec3Json(rvec)).Append(",\n");
            sb.Append("      \"tvec\": ").Append(Vec3Json(tvec)).Append(",\n");
            sb.Append("      \"calibrated\": false,\n      \"placeholder\": true\n    }");
            firstKey = false;
        }
        sb.Append("\n  }\n}\n");
        string extrinsicsPath = $"{PlaceholderDir}/camera_extrinsics.json";
        Directory.CreateDirectory(PlaceholderDir);
        File.WriteAllText(extrinsicsPath, sb.ToString(), new UTF8Encoding(false));
        AssetDatabase.ImportAsset(extrinsicsPath);
        var extrinsics = AssetDatabase.LoadAssetAtPath<TextAsset>(extrinsicsPath);

        // 3) Wire and re-enable each camera, then check it lands where it was.
        foreach (var (key, stream, cc) in cams)
        {
            var intrinsics = AssetDatabase.LoadAssetAtPath<TextAsset>($"{PlaceholderDir}/{key}-{stream}.json");
            if (intrinsics == null)
            {
                Debug.LogError($"[Placeholder calibration] missing {PlaceholderDir}/{key}-{stream}.json; '{cc.name}' skipped.");
                continue;
            }
            Vector3 beforePos = cc.transform.position;
            Quaternion beforeRot = cc.transform.rotation;

            Undo.RecordObject(cc, "Wire placeholder calibration");
            Undo.RecordObject(cc.transform, "Wire placeholder calibration");
            cc.intrinsicsJson = intrinsics;
            cc.extrinsicsJson = extrinsics;
            cc.cameraKey = key;
            cc.zoom = 10;
            cc.worldFrame = frame;
            if (cc.distortionShader == null) cc.distortionShader = Shader.Find(CalibratedCamera.DistortionShaderName);
            cc.enabled = true;
            cc.Apply();
            EditorUtility.SetDirty(cc);

            Debug.Log($"[Placeholder calibration] '{cc.name}' -> {key}-{stream}, back at its placement within " +
                      $"{Vector3.Distance(beforePos, cc.transform.position) * 1000f:F2} mm / {Quaternion.Angle(beforeRot, cc.transform.rotation):F3} deg.");
        }

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log($"[Placeholder calibration] wrote {extrinsicsPath} for {string.Join(", ", poses.Keys.OrderBy(k => k))} (calibrated:false).");
    }

    static void AddIfPlaceholderCamera(List<(string key, string stream, CalibratedCamera cc)> list, CalibratedCamera cc)
    {
        Match m = Regex.Match(cc.name, @"(?<!\d)(103|120)(?!\d)");
        if (!m.Success) return;
        string stream = Regex.IsMatch(cc.name, "sub", RegexOptions.IgnoreCase) ? "sub" : "main";
        list.Add(("cam" + m.Value, stream, cc));
    }

    static string Vec3Json(double[] v) =>
        "[" + string.Join(", ", v.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";

    // Includes inactive objects, which GameObject.Find skips.
    internal static Transform FindByName(Scene scene, string name)
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
