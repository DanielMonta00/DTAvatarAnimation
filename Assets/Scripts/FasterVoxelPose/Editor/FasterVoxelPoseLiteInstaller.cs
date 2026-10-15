#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

// Tools > FasterVoxelPose: the lean estimator and the follower that puts CapsuleOperatorPete on the operator as the models see them.
// The scene is gitignored and usually open with unsaved work, so nothing here saves it: the scene is only marked dirty, like the
// other installers do (and everything is undoable).
public static class FasterVoxelPoseLiteInstaller
{
    public const string LiteName = "FasterVoxelPoseLite";
    public const string CapsuleName = "CapsuleOperatorPete";
    public const string OperatorName = "OperatorPete";

    [MenuItem("Tools/FasterVoxelPose/Real-time operator capsule (lean estimator + follower on CapsuleOperatorPete)")]
    public static void InstallAll()
    {
        Scene scene = SceneManager.GetActiveScene();
        GameObject capsule = FindObject(scene, CapsuleName);
        if (capsule == null)
        {
            EditorUtility.DisplayDialog("Real-time operator capsule", $"No '{CapsuleName}' in scene '{scene.name}'. Open the scene that has it, or use " +
                "Tools > FasterVoxelPose > Attach pose follower to the selected object.", "OK");
            return;
        }

        FasterVoxelPoseLite lite = EnsureLite(scene, out bool createdLite);
        PoseEstimateFollower follower = AttachFollower(capsule, out bool createdFollower);
        Animator pete = FindOperatorAnimator(scene);
        if (follower.truthAvatar == null && pete != null)
        {
            Undo.RecordObject(follower, "Wire the truth avatar");
            follower.truthAvatar = pete;
        }

        EditorUtility.SetDirty(lite);
        EditorUtility.SetDirty(follower);
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = capsule;

        FasterVoxelPoseLive full = Object.FindFirstObjectByType<FasterVoxelPoseLive>(FindObjectsInactive.Include);
        Debug.Log($"[PoseEstimateFollower] {(createdLite ? "Created" : "Found")} '{LiteName}' ({lite.cameras.Count} cameras: " +
                  $"{string.Join(", ", lite.cameras.Where(c => c != null).Select(c => c.name.Trim()))}; volume {lite.volumeSize} m at {(lite.volumeAnchor != null ? lite.volumeAnchor.name : "the world origin")} +{lite.volumeOffset}).\n" +
                  $"  {(createdFollower ? "Added" : "Found")} PoseEstimateFollower on '{CapsuleName}' (poses from: Auto = the lean estimator while it runs; " +
                  $"truth avatar for the measurements: {(follower.truthAvatar != null ? follower.truthAvatar.name : "NONE - assign the operator's Animator to see the capsule's error")}).\n" +
                  (full != null ? $"  '{full.name}' (the full FasterVoxelPoseLive) stays in the scene: while the lean estimator is enabled it is switched off for each Play session, because the server answers one client at a time " +
                                  "(the scene is not changed). To look at the skeletons again, disable the lean estimator.\n" : "") +
                  "  Press Play: the capsule goes to the nearest person the model confirms, follows them, and the Console prints what it measured when Play stops (or use the Inspector's Print report).\n" +
                  "  The scene is marked as changed, not saved.", capsule);
    }

    [MenuItem("Tools/FasterVoxelPose/Attach pose follower to the selected object")]
    public static void AttachToSelection()
    {
        if (Selection.gameObjects.Length == 0)
        {
            EditorUtility.DisplayDialog("Attach pose follower", "Select the object(s) that should follow a person first.", "OK");
            return;
        }
        Scene scene = SceneManager.GetActiveScene();
        EnsureLite(scene, out _);
        foreach (GameObject go in Selection.gameObjects)
        {
            AttachFollower(go, out _);
            EditorUtility.SetDirty(go);
        }
        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log($"[PoseEstimateFollower] follower on {string.Join(", ", Selection.gameObjects.Select(g => g.name))}. Several followers never take the same person.");
    }

    [MenuItem("Tools/FasterVoxelPose/Create the lean estimator only (FasterVoxelPoseLite)")]
    public static void CreateLiteOnly()
    {
        Scene scene = SceneManager.GetActiveScene();
        FasterVoxelPoseLite lite = EnsureLite(scene, out bool created);
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = lite.gameObject;
        Debug.Log($"[FasterVoxelPoseLite] {(created ? "Created" : "Found")} '{LiteName}'.", lite);
    }

    // "Stop inference server" stops the servers THIS editor session started. A server left running by an earlier session (an editor that
    // crashed or was closed with the server up, a server started by hand) is not tracked, keeps the port, and goes on answering; the new
    // server then either cannot start or sits beside it, and the clients land on whichever the system picks. This stops them all.
    [MenuItem("Tools/FasterVoxelPose/Stop ALL inference servers (also ones left by earlier editor sessions)")]
    public static void StopAllServers()
    {
        var ports = new System.Collections.Generic.SortedSet<int> { 5577, 5578 };
        foreach (FasterVoxelPoseLite c in Object.FindObjectsByType<FasterVoxelPoseLite>(FindObjectsInactive.Include, FindObjectsSortMode.None)) ports.Add(c.port);
        foreach (FasterVoxelPoseLive c in Object.FindObjectsByType<FasterVoxelPoseLive>(FindObjectsInactive.Include, FindObjectsSortMode.None)) ports.Add(c.port);
        foreach (ViTPoseLive c in Object.FindObjectsByType<ViTPoseLive>(FindObjectsInactive.Include, FindObjectsSortMode.None)) ports.Add(c.port);

        System.Collections.Generic.List<string> report = StopServers(ports, true, out int stopped);
        Debug.Log(report.Count == 0
            ? $"[FasterVoxelPose] no inference server is running (ports {string.Join(", ", ports)})."
            : $"[FasterVoxelPose] stopped {stopped} of {report.Count} server process(es):\n  " + string.Join("\n  ", report) +
              "\nThey start again by themselves in Play (the first Play after this loads the model: about 15 s).");
    }

    // The core of the menu item (public so that a test can aim it at ports of its own): stops the python processes that listen on `ports` and,
    // with includeTracked, the servers this editor session started (a second server on a port does not show in the listener table).
    // One line per process found.
    public static System.Collections.Generic.List<string> StopServers(System.Collections.Generic.IEnumerable<int> ports, bool includeTracked, out int stopped)
    {
        var found = new System.Collections.Generic.List<(int pid, string why)>();
        var seen = new System.Collections.Generic.HashSet<int> { System.Diagnostics.Process.GetCurrentProcess().Id };
        if (includeTracked)
            foreach (string name in FvpServerProcess.AllNames)
            {
                int pid = FvpServerProcess.Pid(name);
                if (pid != 0 && seen.Add(pid)) found.Add((pid, "started by this editor: " + name));
            }
        foreach (int port in ports)
            foreach (int pid in FvpServerHygiene.ListenerPids(port))
                if (pid > 0 && seen.Add(pid)) found.Add((pid, "listening on port " + port));

        // python processes only: something else that happens to listen there is not ours to stop
        var report = new System.Collections.Generic.List<string>();
        stopped = 0;
        foreach ((int pid, string why) in found)
        {
            try
            {
                System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById(pid);
                string line = $"pid {pid}, {p.ProcessName}, {p.WorkingSet64 >> 20} MB, {why}";
                if (!p.ProcessName.StartsWith("python", System.StringComparison.OrdinalIgnoreCase)) { report.Add(line + " - NOT stopped, it is not a python process"); continue; }
                p.Kill();
                p.WaitForExit(3000);
                report.Add(line);
                stopped++;
            }
            catch (System.Exception e) { report.Add($"pid {pid}, {why} - could not be stopped: {e.Message}"); }
        }
        if (includeTracked) FvpServerProcess.StopAll();      // forgets the pids this session tracked
        return report;
    }

    static FasterVoxelPoseLite EnsureLite(Scene scene, out bool created)
    {
        GameObject go = FindObject(scene, LiteName);
        created = go == null;
        if (created)
        {
            go = new GameObject(LiteName);
            Undo.RegisterCreatedObjectUndo(go, "Create " + LiteName);
        }
        var lite = go.GetComponent<FasterVoxelPoseLite>();
        if (lite == null) lite = Undo.AddComponent<FasterVoxelPoseLite>(go);    // Reset() wires it: from the FasterVoxelPose object when there is one
        return lite;
    }

    static PoseEstimateFollower AttachFollower(GameObject go, out bool created)
    {
        var f = go.GetComponent<PoseEstimateFollower>();
        created = f == null;
        if (created) f = Undo.AddComponent<PoseEstimateFollower>(go);
        return f;
    }

    // The real operator's rig: used only to measure the capsule against, never to move it.
    static Animator FindOperatorAnimator(Scene scene)
    {
        GameObject go = FindObject(scene, OperatorName);
        if (go == null) return null;
        Animator a = go.GetComponent<Animator>();
        return a != null ? a : go.GetComponentInChildren<Animator>(true);
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
}

// The lean estimator's server should be up BEFORE Play too (the existing hook only looks for FasterVoxelPoseLive): the model load, the
// voxel grids and the kernel tuning then happen while you are still editing. It is the same server and port as FasterVoxelPose's, so when
// both exist whichever hook runs first starts it and the other finds it running.
[InitializeOnLoad]
static class FasterVoxelPoseLiteEditorHooks
{
    static FasterVoxelPoseLiteEditorHooks()
    {
        EditorApplication.playModeStateChanged -= OnPlayMode;
        EditorApplication.playModeStateChanged += OnPlayMode;
        EditorApplication.delayCall += () => EnsureServer("the editor opened");
    }

    static void OnPlayMode(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingEditMode) EnsureServer("entering Play");
    }

    static void EnsureServer(string why)
    {
        FasterVoxelPoseLite c = Object.FindFirstObjectByType<FasterVoxelPoseLite>(FindObjectsInactive.Include);
        if (c == null || !c.enabled || !c.gameObject.activeInHierarchy || !c.autoLaunchServer || !c.startServerWithEditor) return;
        // Something (a server of an earlier editor session, one started by hand) already listens on the port: asked of the operating system, not
        // of the server. Connecting to it took its one client slot for a moment and timed out while it served somebody else, which is how a
        // second server once got started beside a busy first one.
        if (FvpServerProcess.IsRunning || FvpServerHygiene.PortInUse(c.port)) return;

        string script = string.IsNullOrWhiteSpace(c.serverScript) ? FvpServerProcess.DefaultScriptPath : c.serverScript;
        if (FvpServerProcess.TryLaunch(c.pythonExe, script, c.fvpRepo, c.host, c.port, c.EffectiveIdleExitSeconds, c.ServerArguments, out string err))
            Debug.Log($"[FasterVoxelPoseLite] server started ({why}) so that Play finds it ready.");
        else
            Debug.LogWarning("[FasterVoxelPoseLite] could not start the server: " + err);
    }
}
#endif
