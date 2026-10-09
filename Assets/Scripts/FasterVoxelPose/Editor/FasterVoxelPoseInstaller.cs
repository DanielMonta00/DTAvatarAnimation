#if UNITY_EDITOR
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

// Tools > FasterVoxelPose: sets the component up in the open scene and manages the Python server.
// The scene is gitignored and usually open with unsaved work, so nothing here saves it: the scene is only
// marked dirty, like the calibration installers do.
public static class FasterVoxelPoseInstaller
{
    const string ObjectName = "FasterVoxelPose";

    [MenuItem("Tools/FasterVoxelPose/Create FasterVoxelPose object (wire cameras, volume, animators)")]
    public static void Install()
    {
        Scene scene = SceneManager.GetActiveScene();
        GameObject go = FindObject(scene, ObjectName);
        bool created = go == null;
        if (created)
        {
            go = new GameObject(ObjectName);
            Undo.RegisterCreatedObjectUndo(go, "Create " + ObjectName);
        }

        var fvp = go.GetComponent<FasterVoxelPoseLive>();
        if (fvp == null)
        {
            fvp = Undo.AddComponent<FasterVoxelPoseLive>(go); // Reset() auto-wires
        }
        else
        {
            // Respect what is already set: AutoWire() re-derives everything, then the user's choices go back on top.
            Undo.RecordObject(fvp, "Wire FasterVoxelPose");
            var cams = fvp.cameras != null ? fvp.cameras.ToList() : new System.Collections.Generic.List<Camera>();
            var anchor = fvp.volumeAnchor;
            var anims = fvp.trackedAnimators != null ? fvp.trackedAnimators.Where(x => x != null).ToList() : new System.Collections.Generic.List<Animator>();
            fvp.AutoWire();
            if (cams.Count > 0 && cams.All(c => c != null)) fvp.cameras = cams;
            if (anchor != null) fvp.volumeAnchor = anchor;
            if (anims.Count > 0) fvp.trackedAnimators = anims;
        }
        EditorUtility.SetDirty(fvp);
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = go;

        Debug.Log($"[FasterVoxelPose] {(created ? "Created" : "Re-wired")} '{ObjectName}'.\n" +
                  $"  cameras: {(fvp.cameras.Count == 0 ? "NONE FOUND - assign them in the Inspector" : string.Join(", ", fvp.cameras.Select(Describe)))}\n" +
                  $"  volume anchor: {(fvp.volumeAnchor != null ? fvp.volumeAnchor.name : "none (world origin)")}, box {fvp.volumeSize} m at +{fvp.volumeOffset}\n" +
                  $"  animators put back on rewind: {(fvp.trackedAnimators.Count == 0 ? "none" : string.Join(", ", fvp.trackedAnimators.Select(a => a.name)))}\n" +
                  $"  ground-truth avatars (green): {(fvp.groundTruthAvatars.Count == 0 ? "NONE - assign them in the Inspector to see the green skeleton" : string.Join(", ", fvp.groundTruthAvatars.Select(a => a.name)))}\n" +
                  "  Press Play. The first run loads the model (~10 s); the skeletons appear over each camera's display.", go);
    }

    public const string ModelsParentName = "EstimationModels", ViTPoseObjectName = "ViTPose";

    [MenuItem("Tools/FasterVoxelPose/Add ViTPose + models hub (under 'EstimationModels')")]
    public static void InstallModels()
    {
        Scene scene = SceneManager.GetActiveScene();

        // the parent: each child object is another model running on the cameras
        GameObject parent = FindObject(scene, ModelsParentName);
        bool createdParent = parent == null;
        if (createdParent)
        {
            parent = new GameObject(ModelsParentName);
            Undo.RegisterCreatedObjectUndo(parent, "Create " + ModelsParentName);
        }

        // FasterVoxelPose goes under it (made first if there is none yet)
        GameObject fvpGo = FindObject(scene, ObjectName);
        if (fvpGo == null || fvpGo.GetComponent<FasterVoxelPoseLive>() == null) { Install(); fvpGo = FindObject(scene, ObjectName); }
        if (fvpGo != null && fvpGo.transform.parent != parent.transform)
            Undo.SetTransformParent(fvpGo.transform, parent.transform, "Move FasterVoxelPose under " + ModelsParentName);

        var hub = parent.GetComponent<EstimationModelsHub>();
        if (hub == null) hub = Undo.AddComponent<EstimationModelsHub>(parent);

        // ViTPose, a child of its own
        Transform vitT = parent.transform.Find(ViTPoseObjectName);
        GameObject vitGo = vitT != null ? vitT.gameObject : null;
        bool createdVit = vitGo == null;
        if (createdVit)
        {
            vitGo = new GameObject(ViTPoseObjectName);
            Undo.RegisterCreatedObjectUndo(vitGo, "Create " + ViTPoseObjectName);
            vitGo.transform.SetParent(parent.transform, false);
        }
        var vit = vitGo.GetComponent<ViTPoseLive>();
        if (vit == null) vit = Undo.AddComponent<ViTPoseLive>(vitGo);

        EditorUtility.SetDirty(hub);
        EditorUtility.SetDirty(vit);
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = parent;

        Debug.Log($"[FasterVoxelPose] {(createdParent ? "Created" : "Found")} '{ModelsParentName}' with the hub; models under it: " +
                  string.Join(", ", parent.GetComponentsInChildren<IEstimationModel>(true).Select(m => m.ModelName)) + ".\n" +
                  $"  ViTPose server: {vit.pythonExe}\n  ViTPose repo: {vit.vitposeRepo}\n  YOLO weights: {vit.yoloWeights}\n" +
                  "  Press Play: both servers start with the editor, a tab per model appears on every display (T cycles, click a tab to select, click its swatch to hide its skeletons).", parent);
    }

    static string Describe(Camera c) => c == null ? "(missing)" : $"{c.name.Trim()} [{HierarchyPath(c.transform)}]";

    static string HierarchyPath(Transform t)
    {
        string p = t.name.Trim();
        if (t.parent != null) p = t.parent.name.Trim() + "/" + p;
        return p;
    }

    static GameObject FindObject(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Transform hit = FindIn(root.transform, name);
            if (hit != null) return hit.gameObject;
        }
        return null;
    }

    static Transform FindIn(Transform t, string name)
    {
        if (t.name == name) return t;
        foreach (Transform c in t)
        {
            Transform hit = FindIn(c, name);
            if (hit != null) return hit;
        }
        return null;
    }

    // ---- the recorder's own live overlay ----

    [MenuItem("Tools/FasterVoxelPose/Show live ground truth on the MultiViewRecorder's cameras")]
    public static void AddRecorderLiveOverlay()
    {
        MultiViewRecorder[] recorders = Object.FindObjectsByType<MultiViewRecorder>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (recorders.Length == 0) { Debug.LogWarning("[RecorderLiveOverlay] no MultiViewRecorder in the open scene."); return; }
        int added = 0;
        foreach (MultiViewRecorder r in recorders)
        {
            if (r.TryGetComponent(out RecorderLiveOverlay _)) continue;
            Undo.AddComponent<RecorderLiveOverlay>(r.gameObject);
            EditorSceneManager.MarkSceneDirty(r.gameObject.scene);   // not saved: the scene is usually open with unsaved work
            added++;
        }
        Debug.Log(added > 0
            ? $"[RecorderLiveOverlay] added to {added} MultiViewRecorder(s): in Play the recorder's keypoints (what keyrgb shows) are drawn live over its cameras' displays. Save the scene to keep it."
            : "[RecorderLiveOverlay] every MultiViewRecorder already has it.");
    }

    // ---- server ----

    [MenuItem("Tools/FasterVoxelPose/Stop inference server")]
    public static void StopServer()
    {
        bool any = false;
        foreach (string n in FvpServerProcess.AllNames) any |= FvpServerProcess.IsRunningOf(n);
        FvpServerProcess.StopAll();
        Debug.Log(any ? "[FasterVoxelPose] inference servers stopped (Faster-VoxelPose and ViTPose); they start again by themselves in Play." : "[FasterVoxelPose] no server of this editor session is running.");
    }

    [MenuItem("Tools/FasterVoxelPose/Open ViTPose server log")]
    public static void OpenViTPoseLog()
    {
        string p = FvpServerProcess.LogPathOf(FvpServerProcess.ViTPoseName);
        if (!File.Exists(p)) { Debug.Log("[FasterVoxelPose] no ViTPose server log yet: " + p); return; }
        Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
    }

    [MenuItem("Tools/FasterVoxelPose/Open server log")]
    public static void OpenLog()
    {
        string p = FvpServerProcess.LogPath;
        if (!File.Exists(p)) { Debug.Log("[FasterVoxelPose] no server log yet: " + p); return; }
        Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
    }

    public static void StartServer(FasterVoxelPoseLive c)
    {
        string script = string.IsNullOrWhiteSpace(c.serverScript) ? FvpServerProcess.DefaultScriptPath : c.serverScript;
        if (!FvpServerProcess.TryLaunch(c.pythonExe, script, c.fvpRepo, c.host, c.port, c.serverIdleExitSeconds, c.ServerArguments, out string err))
            Debug.LogError("[FasterVoxelPose] cannot start the server: " + err, c);
    }
}

// The server this editor session started must not outlive it. And it should be up BEFORE Play: the model load, the voxel grids
// and the kernel tuning of the last configuration then happen while you are still editing, not after you press Play.
[InitializeOnLoad]
static class FasterVoxelPoseEditorHooks
{
    static FasterVoxelPoseEditorHooks()
    {
        EditorApplication.quitting -= OnQuit;
        EditorApplication.quitting += OnQuit;
        EditorApplication.playModeStateChanged -= OnPlayMode;
        EditorApplication.playModeStateChanged += OnPlayMode;
        EditorApplication.delayCall += () => EnsureServer("the editor opened");
    }

    static void OnQuit() => FvpServerProcess.StopAll();

    static void OnPlayMode(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingEditMode) EnsureServer("entering Play");
    }

    static void EnsureServer(string why)
    {
        FasterVoxelPoseLive c = UnityEngine.Object.FindFirstObjectByType<FasterVoxelPoseLive>(FindObjectsInactive.Include);
        if (c != null && c.enabled && c.autoLaunchServer && c.startServerWithEditor && !FvpServerProcess.IsRunning && !PortOpen(c.host, c.port))
        {
            string script = string.IsNullOrWhiteSpace(c.serverScript) ? FvpServerProcess.DefaultScriptPath : c.serverScript;
            if (FvpServerProcess.TryLaunch(c.pythonExe, script, c.fvpRepo, c.host, c.port, c.EffectiveIdleExitSeconds, c.ServerArguments, out string err))
                Debug.Log($"[FasterVoxelPose] server started ({why}) so that Play finds it ready.");
            else
                Debug.LogWarning("[FasterVoxelPose] could not start the server: " + err);
        }

        ViTPoseLive v = UnityEngine.Object.FindFirstObjectByType<ViTPoseLive>(FindObjectsInactive.Include);
        if (v != null && v.enabled && v.autoLaunchServer && v.startServerWithEditor && !FvpServerProcess.IsRunningOf(FvpServerProcess.ViTPoseName) && !PortOpen(v.host, v.port))
        {
            if (FvpServerProcess.Launch(FvpServerProcess.ViTPoseName, v.pythonExe, v.ServerScript, "--vitpose-dir \"" + v.vitposeRepo + "\" --yolo \"" + v.yoloWeights + "\"",
                                        v.host, v.port, v.EffectiveIdleExitSeconds, v.ExtraLaunchArguments, out string err))
                Debug.Log($"[ViTPose] server started ({why}) so that Play finds it ready.");
            else
                Debug.LogWarning("[ViTPose] could not start the server: " + err);
        }
    }

    // Something (a server of an earlier editor session, one started by hand) already answers on the port.
    static bool PortOpen(string host, int port)
    {
        try
        {
            using (var client = new System.Net.Sockets.TcpClient())
            {
                System.IAsyncResult r = client.BeginConnect(host, port, null, null);
                bool ok = r.AsyncWaitHandle.WaitOne(150) && client.Connected;
                return ok;
            }
        }
        catch { return false; }
    }
}
#endif
