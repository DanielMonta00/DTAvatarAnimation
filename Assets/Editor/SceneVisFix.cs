using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
static class SceneVisFix {
    const string KEY = "SceneVisFix.hiddenPaths";

    static SceneVisFix() {
        EditorApplication.playModeStateChanged += OnChange;
    }

    static HashSet<string> Load() {
        var s = SessionState.GetString(KEY, "");
        return string.IsNullOrEmpty(s)
            ? new HashSet<string>()
            : new HashSet<string>(s.Split('\n'));
    }

    static void Save(HashSet<string> set) {
        SessionState.SetString(KEY, string.Join("\n", set));
    }

    static void OnChange(PlayModeStateChange s) {
        if (s == PlayModeStateChange.ExitingEditMode) {
            var hidden = new HashSet<string>();
            ForEach(go => {
                if (SceneVisibilityManager.instance.IsHidden(go, false))
                    hidden.Add(PathOf(go));
            });
            Save(hidden);
            Debug.Log($"[SceneVisFix] snapshot: {hidden.Count} hidden");
        }
        else if (s == PlayModeStateChange.EnteredEditMode) {
            EditorApplication.delayCall += Apply;
        }
    }

    static void Apply() {
        var hidden = Load();
        SceneVisibilityManager.instance.ShowAll();
        int n = 0;
        ForEach(go => {
            if (hidden.Contains(PathOf(go))) {
                SceneVisibilityManager.instance.Hide(go, false);
                n++;
            }
        });
        Debug.Log($"[SceneVisFix] restored: {n}/{hidden.Count}");
    }

    static void ForEach(System.Action<GameObject> action) {
        for (int i = 0; i < SceneManager.sceneCount; i++)
            foreach (var root in SceneManager.GetSceneAt(i).GetRootGameObjects())
                Recurse(root, action);
    }

    static void Recurse(GameObject go, System.Action<GameObject> a) {
        a(go);
        foreach (Transform c in go.transform) Recurse(c.gameObject, a);
    }

    static string PathOf(GameObject go) {
        var sb = new StringBuilder(go.scene.name).Append("://");
        var stack = new Stack<string>();
        for (var t = go.transform; t != null; t = t.parent) stack.Push(t.name);
        sb.Append(string.Join("/", stack));
        return sb.ToString();
    }
}
