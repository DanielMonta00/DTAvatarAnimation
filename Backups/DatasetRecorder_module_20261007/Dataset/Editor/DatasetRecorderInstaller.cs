#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Tools > Calibration > Add dataset recorder: puts a DatasetRecorder on the "CalibratedCameras"
// object and fills it in (avatar = OperatorPete, Rokoko frame = its parent, world = MOCAPcenter,
// cameras = the enabled calibrated cameras, one per camera key). Safe to re-run.
public static class DatasetRecorderInstaller
{
    const string RootName = "CalibratedCameras";
    const string FrameName = "MOCAPcenter";
    const string AvatarName = "OperatorPete";

    [MenuItem("Tools/Calibration/Add dataset recorder to CalibratedCameras")]
    public static void Install()
    {
        Scene scene = SceneManager.GetActiveScene();
        Transform root = CalibratedCameraInstaller.FindByName(scene, RootName);
        Transform frame = CalibratedCameraInstaller.FindByName(scene, FrameName);
        if (root == null || frame == null)
        {
            EditorUtility.DisplayDialog("Dataset recorder", $"Need '{RootName}' and '{FrameName}' in scene '{scene.name}'.", "OK");
            return;
        }

        var rec = root.GetComponent<DatasetRecorder>();
        if (rec == null) rec = Undo.AddComponent<DatasetRecorder>(root.gameObject);
        Undo.RecordObject(rec, "Wire dataset recorder");

        Transform avatarTr = CalibratedCameraInstaller.FindByName(scene, AvatarName);
        Animator animator = avatarTr != null ? avatarTr.GetComponent<Animator>() : null;
        if (animator == null)
            Debug.LogWarning($"[DatasetRecorder] no Animator on '{AvatarName}'; assign the avatar by hand.");
        else
        {
            rec.avatar = animator;
            rec.rokokoFrame = avatarTr.parent != null ? avatarTr.parent : avatarTr;
        }
        rec.worldFrame = frame;
        rec.RefreshCameras();
        EditorUtility.SetDirty(rec);
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = root.gameObject;

        Debug.Log($"[DatasetRecorder] on '{RootName}': avatar '{(animator != null ? animator.name : "-")}', " +
                  $"{rec.cameras.Count} cameras ({string.Join(", ", rec.cameras.ConvertAll(c => c.cameraKey))}). " +
                  "Tick 'Record' (or 'Record On Play') and press Play.");
    }
}
#endif
