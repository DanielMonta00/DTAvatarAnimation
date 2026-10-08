using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Video;

// Everything in the scene that is not an Animator but still moves with time, so that stepping back and rewinding puts the whole
// scene back, not just the avatars.
//
//   moving transforms   Found by themselves: every transform of the scene is watched at each estimate, and one that has changed
//                       is remembered from then on (with the value it had before it first moved). A frame stores those only.
//                       This covers the cable robot's platform and drums, conveyors, props, rigidbodies... without listing them.
//   script state        The private runtime fields (not [SerializeField], not read-only) of the scripts that drive things from
//                       their own clock: CableRobotTrajectory's playTime, RandomWalker's timers... Listed on the component.
//   video players       Time and playing flag. A VideoPlayer on "unscaled game time" ignores Time.timeScale, so it is paused,
//                       stepped and seeked together with the scene.
//   physics             Rigidbody velocities and ArticulationBody joint positions / velocities.
//
// What cannot come back: anything computed from the absolute clock (Time.time) instead of its own state, and anything outside the
// scene (a robot controller, a network stream).
public sealed class FvpSceneSnapshot
{
    public int dynamicCount;               // how many entries of the recorder's moving-transform list this snapshot covers
    public Vector3[] position, scale;
    public Quaternion[] rotation;
    public object[][] scriptValues;
    public double[] videoTime;
    public Vector3[] bodyVelocity, bodyAngularVelocity;
    public float[][] jointPositions, jointVelocities;
}

public sealed class FvpSceneState
{
    // what to watch
    public bool trackTransforms = true, trackPhysics = true, trackVideo = true;

    Transform[] all;
    Vector3[] lastPos, lastScale, restPos, restScale;
    Quaternion[] lastRot, restRot;
    bool[] isMoving;
    readonly List<int> moving = new List<int>();
    bool primed;

    readonly List<MonoBehaviour> scripts = new List<MonoBehaviour>();
    readonly List<FieldInfo[]> scriptFields = new List<FieldInfo[]>();
    readonly List<VideoPlayer> videos = new List<VideoPlayer>();
    bool[] videoWasPlaying = Array.Empty<bool>();
    readonly List<Rigidbody> bodies = new List<Rigidbody>();
    readonly List<ArticulationBody> articulations = new List<ArticulationBody>();
    readonly List<float> scratch = new List<float>();

    public bool Initialized { get; private set; }
    public int MovingCount => moving.Count;
    public int VideoCount => videos.Count;
    public int ScriptCount => scripts.Count;

    // `drivenElsewhere`: transforms the caller restores itself (the bones of the tracked Animators).
    public void Initialize(ICollection<Transform> drivenElsewhere, IList<MonoBehaviour> scriptList)
    {
        moving.Clear(); scripts.Clear(); scriptFields.Clear(); videos.Clear(); bodies.Clear(); articulations.Clear();
        primed = false;

        if (trackTransforms)
        {
            var list = new List<Transform>();
            foreach (Transform t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t.hideFlags != HideFlags.None || t is RectTransform) continue; // our own overlays, UI
                if (drivenElsewhere != null && drivenElsewhere.Contains(t)) continue;
                list.Add(t);
            }
            all = list.ToArray();
            int n = all.Length;
            lastPos = new Vector3[n]; lastScale = new Vector3[n]; lastRot = new Quaternion[n];
            restPos = new Vector3[n]; restScale = new Vector3[n]; restRot = new Quaternion[n];
            isMoving = new bool[n];
        }

        if (scriptList != null)
            foreach (MonoBehaviour m in scriptList)
            {
                if (m == null) continue;
                scripts.Add(m);
                scriptFields.Add(StateFields(m.GetType()));
            }

        if (trackVideo)
        {
            videos.AddRange(UnityEngine.Object.FindObjectsByType<VideoPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            videoWasPlaying = new bool[videos.Count];
        }

        if (trackPhysics)
        {
            foreach (Rigidbody rb in UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (rb.hideFlags == HideFlags.None) bodies.Add(rb);
            foreach (ArticulationBody ab in UnityEngine.Object.FindObjectsByType<ArticulationBody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (ab.isRoot && ab.dofCount > 0) articulations.Add(ab);
        }
        Initialized = true;
    }

    // The runtime state of a script: its non-public, non-serialized, writable fields of plain value types.
    static FieldInfo[] StateFields(Type type)
    {
        var fields = new List<FieldInfo>();
        for (Type t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.IsInitOnly || f.IsLiteral || !IsPlainValue(f.FieldType)) continue;
                if (f.GetCustomAttribute<SerializeField>() != null) continue; // configuration, not state
                fields.Add(f);
            }
        return fields.ToArray();
    }

    static bool IsPlainValue(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4) ||
        t == typeof(Quaternion) || t == typeof(Color);

    // ---------------- capture ----------------

    public FvpSceneSnapshot Capture()
    {
        if (!Initialized) return null;
        if (trackTransforms) WatchTransforms();

        var s = new FvpSceneSnapshot { dynamicCount = moving.Count };
        if (trackTransforms)
        {
            s.position = new Vector3[moving.Count]; s.rotation = new Quaternion[moving.Count]; s.scale = new Vector3[moving.Count];
            for (int k = 0; k < moving.Count; k++)
            {
                int i = moving[k];
                s.position[k] = lastPos[i]; s.rotation[k] = lastRot[i]; s.scale[k] = lastScale[i];
            }
        }

        s.scriptValues = new object[scripts.Count][];
        for (int k = 0; k < scripts.Count; k++)
        {
            FieldInfo[] fields = scriptFields[k];
            var values = new object[fields.Length];
            if (scripts[k] != null)
                for (int f = 0; f < fields.Length; f++) values[f] = fields[f].GetValue(scripts[k]);
            s.scriptValues[k] = values;
        }

        s.videoTime = new double[videos.Count];
        for (int k = 0; k < videos.Count; k++) s.videoTime[k] = videos[k] != null ? videos[k].time : 0.0;

        s.bodyVelocity = new Vector3[bodies.Count]; s.bodyAngularVelocity = new Vector3[bodies.Count];
        for (int k = 0; k < bodies.Count; k++)
            if (bodies[k] != null) { s.bodyVelocity[k] = bodies[k].linearVelocity; s.bodyAngularVelocity[k] = bodies[k].angularVelocity; }

        s.jointPositions = new float[articulations.Count][]; s.jointVelocities = new float[articulations.Count][];
        for (int k = 0; k < articulations.Count; k++)
        {
            if (articulations[k] == null) { s.jointPositions[k] = s.jointVelocities[k] = Array.Empty<float>(); continue; }
            articulations[k].GetJointPositions(scratch); s.jointPositions[k] = scratch.ToArray();
            articulations[k].GetJointVelocities(scratch); s.jointVelocities[k] = scratch.ToArray();
        }
        return s;
    }

    // One pass over the scene: whatever differs from the last pass is moving, and stays on the list.
    void WatchTransforms()
    {
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null) continue;
            t.GetLocalPositionAndRotation(out Vector3 p, out Quaternion r);
            Vector3 sc = t.localScale;

            if (primed && !isMoving[i] &&
                ((p - lastPos[i]).sqrMagnitude > 1e-12f || RotationDiffers(r, lastRot[i]) || (sc - lastScale[i]).sqrMagnitude > 1e-12f))
            {
                isMoving[i] = true;
                restPos[i] = lastPos[i]; restRot[i] = lastRot[i]; restScale[i] = lastScale[i]; // where it stood until now
                moving.Add(i);
            }
            lastPos[i] = p; lastRot[i] = r; lastScale[i] = sc;
        }
        primed = true;
    }

    // Component-wise: the dot product of a quaternion with itself is 1 only up to float rounding, so it cannot tell "unchanged" from "moved".
    static bool RotationDiffers(Quaternion a, Quaternion b) =>
        Mathf.Abs(a.x - b.x) > 1e-6f || Mathf.Abs(a.y - b.y) > 1e-6f || Mathf.Abs(a.z - b.z) > 1e-6f || Mathf.Abs(a.w - b.w) > 1e-6f;

    // ---------------- restore ----------------

    public void Restore(FvpSceneSnapshot s)
    {
        if (!Initialized || s == null) return;

        if (trackTransforms)
        {
            for (int k = 0; k < moving.Count; k++)
            {
                int i = moving[k];
                Transform t = all[i];
                if (t == null) continue;
                bool known = k < s.dynamicCount;     // not yet moving at that frame: it was where it first stood
                Vector3 p = known ? s.position[k] : restPos[i], sc = known ? s.scale[k] : restScale[i];
                Quaternion r = known ? s.rotation[k] : restRot[i];
                t.SetLocalPositionAndRotation(p, r);
                t.localScale = sc;
                lastPos[i] = p; lastRot[i] = r; lastScale[i] = sc;
            }
        }

        for (int k = 0; k < scripts.Count && k < s.scriptValues.Length; k++)
        {
            if (scripts[k] == null) continue;
            FieldInfo[] fields = scriptFields[k];
            for (int f = 0; f < fields.Length && f < s.scriptValues[k].Length; f++)
                fields[f].SetValue(scripts[k], s.scriptValues[k][f]);
        }

        for (int k = 0; k < videos.Count && k < s.videoTime.Length; k++)
        {
            VideoPlayer vp = videos[k];
            if (vp != null && vp.isPrepared && vp.canSetTime) vp.time = s.videoTime[k];
        }

        for (int k = 0; k < articulations.Count && k < s.jointPositions.Length; k++)
        {
            if (articulations[k] == null || s.jointPositions[k].Length == 0) continue;
            scratch.Clear(); scratch.AddRange(s.jointPositions[k]); articulations[k].SetJointPositions(scratch);
            scratch.Clear(); scratch.AddRange(s.jointVelocities[k]); articulations[k].SetJointVelocities(scratch);
        }

        Physics.SyncTransforms(); // the bodies pick up the poses just written
        for (int k = 0; k < bodies.Count && k < s.bodyVelocity.Length; k++)
            if (bodies[k] != null) { bodies[k].linearVelocity = s.bodyVelocity[k]; bodies[k].angularVelocity = s.bodyAngularVelocity[k]; }
    }

    // ---------------- video (does not follow Time.timeScale) ----------------

    public void PauseMedia()
    {
        for (int k = 0; k < videos.Count; k++)
        {
            if (videos[k] == null) continue;
            videoWasPlaying[k] = videos[k].isPlaying;
            if (videos[k].isPlaying) videos[k].Pause();
        }
    }

    public void ResumeMedia()
    {
        for (int k = 0; k < videos.Count; k++)
            if (videos[k] != null && videoWasPlaying[k]) { videos[k].Play(); videoWasPlaying[k] = false; }
    }

    // One scene step while paused: the picture moves on by the same amount of time.
    public void AdvanceMedia(double seconds)
    {
        foreach (VideoPlayer vp in videos)
        {
            if (vp == null || !vp.isPrepared || !vp.canSetTime) continue;
            double t = vp.time + seconds;
            if (vp.length > 0.0) t = vp.isLooping ? t % vp.length : Math.Min(t, vp.length);
            vp.time = t;
        }
    }
}
