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
                  "  Press Play. The first run loads the model (~10 s); the monitor appears on the Game view.", go);
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

    // ---- server ----

    [MenuItem("Tools/FasterVoxelPose/Stop inference server")]
    public static void StopServer()
    {
        bool was = FvpServerProcess.IsRunning;
        FvpServerProcess.Stop();
        Debug.Log(was ? "[FasterVoxelPose] server stopped." : "[FasterVoxelPose] no server of this editor session is running.");
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
        if (!FvpServerProcess.TryLaunch(c.pythonExe, script, c.fvpRepo, c.host, c.port, c.serverIdleExitSeconds, c.extraServerArgs, out string err))
            Debug.LogError("[FasterVoxelPose] cannot start the server: " + err, c);
    }
}

// The server this editor session started must not outlive it.
[InitializeOnLoad]
static class FasterVoxelPoseEditorHooks
{
    static FasterVoxelPoseEditorHooks()
    {
        EditorApplication.quitting -= OnQuit;
        EditorApplication.quitting += OnQuit;
    }

    static void OnQuit() => FvpServerProcess.Stop();
}
#endif
