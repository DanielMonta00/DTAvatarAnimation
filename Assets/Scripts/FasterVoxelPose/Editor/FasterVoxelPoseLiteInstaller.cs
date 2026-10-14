#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

// Tools > FasterVoxelPose: the lean estimator and the follower that puts CapsuleOperatorPete on the operator as the models see him.
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
                  "  Press Play: the capsule goes to the nearest person the model confirms, follows him, and the Console prints what it measured when Play stops (or use the Inspector's Print report).\n" +
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
        if (FvpServerProcess.IsRunning || PortOpen(c.host, c.port)) return;

        string script = string.IsNullOrWhiteSpace(c.serverScript) ? FvpServerProcess.DefaultScriptPath : c.serverScript;
        if (FvpServerProcess.TryLaunch(c.pythonExe, script, c.fvpRepo, c.host, c.port, c.EffectiveIdleExitSeconds, c.ServerArguments, out string err))
            Debug.Log($"[FasterVoxelPoseLite] server started ({why}) so that Play finds it ready.");
        else
            Debug.LogWarning("[FasterVoxelPoseLite] could not start the server: " + err);
    }

    // Something (a server of an earlier editor session, one started by hand) already answers on the port.
    static bool PortOpen(string host, int port)
    {
        try
        {
            using (var client = new System.Net.Sockets.TcpClient())
            {
                System.IAsyncResult r = client.BeginConnect(host, port, null, null);
                return r.AsyncWaitHandle.WaitOne(150) && client.Connected;
            }
        }
        catch { return false; }
    }
}
#endif
