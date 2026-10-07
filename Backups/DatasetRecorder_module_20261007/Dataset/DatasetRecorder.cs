using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

// Records a session in Unity with the same on-disk layout HumanDatasetRecording produces
// (docs/session_format.md), so its tools (check_sync, export_dataset, view_session, ...) can read it:
//
//   <outputFolder>/<sessionName>_<YYYYMMDD_HHMMSS>/
//     cam107/<epoch_seconds>.jpg      one JPEG per camera per frame, filename = timestamp (6 decimals)
//     cam103/...  cam120/...          folder = CalibratedCamera.cameraKey
//     rokoko_skeleton.jsonl           the avatar's joints, one line per frame, in the Rokoko frame
//                                     (left-handed Unity data, same schema and bone names as the real file)
//     ptz_pose.jsonl                  PTZ pose per camera, for cameras whose extrinsics carry a "ptz_pose"
//     session_meta.json               run summary
//     rokoko_studio_export/           empty, like the real one
//     recalage/rokoko_corrected.jsonl GROUND TRUTH (not produced by a tool here): the same joints in the
//                                     right-handed calibration world (MOCAPcenter frame), exactly what
//                                     tools/07_recalage.py tries to estimate from AprilTags
//     gt/                             optional ground-truth layout, like MultiViewRecorder's: every image with
//                                     the joints drawn over it (gt/<cam>/keyrgb/<t>.jpg), the pixel positions
//                                     behind the drawing (gt/keypoints_2d.jsonl), YOLO boxes, skeleton.json
//
// Add it once, to the "CalibratedCameras" object (or use Tools > Calibration > Add dataset recorder).
// Each camera is captured at its calibrated resolution through its lens-distortion output, so the
// images match what the real camera would have saved. Joint values are the avatar's ACTUAL bone
// transforms (positions in meters, global rotations), not the raw suit data it was retargeted from.
[DisallowMultipleComponent]
public class DatasetRecorder : MonoBehaviour
{
    [Header("Subject")]
    [Tooltip("The animated avatar (Humanoid). Its bones become the skeleton.")]
    public Animator avatar;
    [Tooltip("Frame the avatar's clip lives in (its parent, e.g. HumanDataset). rokoko_skeleton.jsonl is expressed in it.")]
    public Transform rokokoFrame;

    [Header("World")]
    [Tooltip("MOCAPcenter: the calibration world used by the cameras.")]
    public Transform worldFrame;
    [Tooltip("Must match the CalibratedCamera components.")]
    public CalibratedCamera.WorldMirror handedness = CalibratedCamera.WorldMirror.NegateX;

    [Header("Cameras")]
    [Tooltip("One entry per camera folder. Use 'Refresh cameras' to fill from the children.")]
    public List<CalibratedCamera> cameras = new List<CalibratedCamera>();

    [Header("Session")]
    [Tooltip("Relative to the project folder, or an absolute path (e.g. HumanDatasetRecording\\Datasets).")]
    public string outputFolder = "Datasets";
    public string sessionName = "unity_test01";

    [Header("Recording")]
    public bool record;
    [Tooltip("Start with the first frame of Play mode, so the avatar's clip and the recording begin together.")]
    public bool recordOnPlay;
    public float frameRate = 30f;
    [Tooltip("Deterministic capture: game time advances exactly 1/frameRate per frame whatever the encode speed.")]
    public bool setCaptureFramerate = true;
    [Range(1, 100)] public int jpegQuality = 90;
    [Tooltip("Stop after this many seconds of recording. 0 = no limit.")]
    public float maxSeconds = 0f;
    [Tooltip("Stop when the avatar's (non-looping) animation reaches its end.")]
    public bool stopWhenAnimationEnds = true;
    public bool exitPlayModeWhenDone = false;

    [Header("Optional files")]
    [Tooltip("recalage/rokoko_corrected.jsonl: the true world-frame skeleton (ground truth).")]
    public bool writeWorldSkeleton = true;
    public bool writeSessionMeta = true;
    [Tooltip("Seconds between ptz_pose.jsonl lines.")]
    public float ptzLogInterval = 2f;
    [Tooltip("Encodes allowed in flight before capture waits for the disk.")]
    public int maxPendingEncodes = 24;

    [Header("Ground truth overlay (gt/)")]
    [Tooltip("gt/<cam>/keyrgb/<t>.jpg = each image with the avatar's joints and bones drawn over it, plus " +
             "gt/keypoints_2d.jsonl (the pixel positions behind the drawing) and gt/skeleton.json.")]
    public bool writeGtOverlay = true;
    [Tooltip("On: the 23 joints recorded in rokoko_skeleton.jsonl. Off: the keypoints of 'Gt Format', derived from the " +
             "rig exactly as MultiViewRecorder does (AIC, COCO, MPII, SMPL...).")]
    public bool gtUsesRecordedJoints = true;
    public SkeletonFormat gtFormat = SkeletonFormat.AIC_14;
    [Tooltip("gt/<cam>/bboxes/<t>.txt: one Ultralytics YOLO line ('class xc yc w h', normalized) around the visible joints.")]
    public bool writeGtBoxes = true;
    public float gtBoxPadding = 0.1f;
    public int pointRadiusPx = 4;
    public int lineThicknessPx = 2;
    public bool drawSkeleton = true;

    // Rokoko bone name -> Unity humanoid bone (same 23 bones, same order as the real rokoko_skeleton.jsonl).
    // LastBone marks the toe tips, which Unity's humanoid has no bone for.
    static readonly (string name, HumanBodyBones bone)[] BoneTable =
    {
        ("hip", HumanBodyBones.Hips), ("spine", HumanBodyBones.Spine), ("chest", HumanBodyBones.Chest),
        ("neck", HumanBodyBones.Neck), ("head", HumanBodyBones.Head),
        ("leftShoulder", HumanBodyBones.LeftShoulder), ("leftUpperArm", HumanBodyBones.LeftUpperArm),
        ("leftLowerArm", HumanBodyBones.LeftLowerArm), ("leftHand", HumanBodyBones.LeftHand),
        ("rightShoulder", HumanBodyBones.RightShoulder), ("rightUpperArm", HumanBodyBones.RightUpperArm),
        ("rightLowerArm", HumanBodyBones.RightLowerArm), ("rightHand", HumanBodyBones.RightHand),
        ("leftUpLeg", HumanBodyBones.LeftUpperLeg), ("leftLeg", HumanBodyBones.LeftLowerLeg),
        ("leftFoot", HumanBodyBones.LeftFoot), ("leftToe", HumanBodyBones.LeftToes), ("leftToeEnd", HumanBodyBones.LastBone),
        ("rightUpLeg", HumanBodyBones.RightUpperLeg), ("rightLeg", HumanBodyBones.RightLowerLeg),
        ("rightFoot", HumanBodyBones.RightFoot), ("rightToe", HumanBodyBones.RightToes), ("rightToeEnd", HumanBodyBones.LastBone),
    };

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    class CamSlot
    {
        public CalibratedCamera cc;
        public string name;
        public int width, height;
        public string dir;
        public string gtKeyDir, gtBoxDir;
        public int frames;
        public RenderTexture shadowRt;     // fallback when the lens distortion isn't rendered
        public GameObject shadowGo;
        public Camera shadowCam;
        public (int azimuth, int elevation, int zoom)? ptz;
    }

    class BoneSlot
    {
        public string name;
        public Transform tr;       // null for a missing bone
        public Transform foot;     // toe tips only: the foot, to extrapolate when the toe has no child
        public bool tip;
        public Quaternion prevRokoko = Quaternion.identity, prevWorld = Quaternion.identity;
        public bool hasPrevRokoko, hasPrevWorld;
    }

    readonly List<CamSlot> slots = new List<CamSlot>();
    readonly List<BoneSlot> bones = new List<BoneSlot>();
    StreamWriter skeletonWriter, worldWriter, ptzWriter;
    string sessionPath;
    bool recording;
    Coroutine loop;
    bool contextHooked;
    double nextCaptureGameTime;
    int lastCaptureUnityFrame = -1;
    double startGameTime, epochStart, nextPtzElapsed;
    int frameIndex;
    int pendingEncodes;
    float hipHeight;
    bool anyPtz;
    int ptzLines;

    // Ground-truth overlay state.
    SkeletonFormats.SkeletonDef gtDef;
    Color32[] gtColors;
    SkeletonFormats.RigBones gtRig;
    SkeletonFormats.Offsets gtOffsets;
    Vector3[] gtPos;      // this frame's keypoints, Unity world space
    bool[] gtValid;
    StreamWriter gtWriter;

    public bool IsRecording => recording;
    public string SessionPath => sessionPath;

    // ---------------- Inspector helpers ----------------

    void Reset()
    {
        RefreshCameras();
        if (avatar == null) avatar = FindAvatar();
        if (avatar != null && rokokoFrame == null) rokokoFrame = avatar.transform.parent;
    }

    // OperatorPete's Animator, active or not. Falls back to the first Animator under the Rokoko frame.
    Animator FindAvatar()
    {
        foreach (var a in UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (a.name == "OperatorPete") return a;
        if (rokokoFrame != null)
            foreach (var a in rokokoFrame.GetComponentsInChildren<Animator>(true))
                return a;
        return null;
    }

    // One camera per cameraKey (cam107, cam103, ...), the "main" stream when there is a choice.
    [ContextMenu("Refresh cameras")]
    public void RefreshCameras()
    {
        CalibratedCamera[] found = GetComponentsInChildren<CalibratedCamera>(true);
        if (found.Length == 0)
            found = UnityEngine.Object.FindObjectsByType<CalibratedCamera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        cameras = found
            .Where(c => c.enabled)
            .GroupBy(c => c.cameraKey)
            .Select(g => g.OrderBy(c => c.name.IndexOf("sub", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0).First())
            .OrderBy(c => c.cameraKey, StringComparer.Ordinal)
            .ToList();
        if (worldFrame == null && cameras.Count > 0) worldFrame = cameras[0].worldFrame;
        if (cameras.Count > 0) handedness = cameras[0].handedness;
    }

    [ContextMenu("Start recording")]
    public void StartFromMenu() { record = true; }
    [ContextMenu("Stop recording")]
    public void StopFromMenu() { record = false; }

    // ---------------- Lifecycle ----------------

    void Start() { if (recordOnPlay) record = true; }

    void Update()
    {
        if (record && !recording) StartRecording();
        else if (!record && recording) StopRecording();
    }

    void OnDisable() { if (recording) StopRecording(); }
    void OnApplicationQuit() { if (recording) StopRecording(); }

    void StartRecording()
    {
        if (!ValidateSetup()) { record = false; return; }

        slots.Clear();
        anyPtz = false;
        foreach (CalibratedCamera cc in cameras)
        {
            if (cc == null) continue;
            if (slots.Any(s => s.name == cc.cameraKey))
            {
                Debug.LogWarning($"[DatasetRecorder] two cameras share the key '{cc.cameraKey}'; only the first is recorded.");
                continue;
            }
            var slot = new CamSlot { cc = cc, name = cc.cameraKey, width = cc.imageWidth, height = cc.imageHeight, ptz = ReadPtz(cc) };
            slots.Add(slot);
            anyPtz |= slot.ptz.HasValue;
        }

        ResolveBones();

        string root = Path.IsPathRooted(outputFolder) ? outputFolder : Path.Combine(Application.dataPath, "..", outputFolder);
        sessionPath = Path.GetFullPath(Path.Combine(root, sessionName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv)));
        Directory.CreateDirectory(sessionPath);
        foreach (CamSlot s in slots)
        {
            s.dir = Path.Combine(sessionPath, s.name);
            Directory.CreateDirectory(s.dir);
            s.frames = 0;
            PrepareCapture(s);
        }
        Directory.CreateDirectory(Path.Combine(sessionPath, "rokoko_studio_export"));

        skeletonWriter = OpenWriter(Path.Combine(sessionPath, "rokoko_skeleton.jsonl"));
        if (writeWorldSkeleton)
        {
            Directory.CreateDirectory(Path.Combine(sessionPath, "recalage"));
            worldWriter = OpenWriter(Path.Combine(sessionPath, "recalage", "rokoko_corrected.jsonl"));
        }
        if (anyPtz) ptzWriter = OpenWriter(Path.Combine(sessionPath, "ptz_pose.jsonl"));
        if (writeGtOverlay) SetupGt();

        hipHeight = EstimateHipHeight();
        frameIndex = 0;
        ptzLines = 0;
        nextPtzElapsed = 0;
        pendingEncodes = 0;
        startGameTime = Time.timeAsDouble;
        epochStart = (DateTime.UtcNow - Epoch).TotalSeconds;
        if (setCaptureFramerate) Time.captureFramerate = Mathf.RoundToInt(frameRate);
        Application.runInBackground = true;

        nextCaptureGameTime = startGameTime;
        lastCaptureUnityFrame = -1;
        recording = true;
        // Capture right after the cameras have rendered. The render pipeline's callback fires whenever the
        // cameras render; WaitForEndOfFrame only fires while a Game view is presenting, so it is the fallback
        // for the built-in pipeline only.
        if (GraphicsSettings.currentRenderPipeline != null)
        {
            RenderPipelineManager.endContextRendering += OnEndContextRendering;
            contextHooked = true;
        }
        else loop = StartCoroutine(RecordLoop());
        Debug.Log($"[DatasetRecorder] Recording {slots.Count} cameras + skeleton to {sessionPath}");
    }

    bool ValidateSetup()
    {
        // Self-heal the usual cause of a refused start: the avatar field was never filled (or was lost on reload).
        if (avatar == null) avatar = FindAvatar();
        if (avatar == null)
        {
            Debug.LogWarning("[DatasetRecorder] Not recording: no avatar assigned and no 'OperatorPete' Animator found in the scene.", this);
            return false;
        }
        if (!avatar.isHuman)
            Debug.Log($"[DatasetRecorder] '{avatar.name}' has no humanoid Avatar (Generic rig): finding its bones by name.");
        if (worldFrame == null) { Debug.LogWarning("[DatasetRecorder] Not recording: assign the world frame (MOCAPcenter)."); return false; }
        if (rokokoFrame == null) rokokoFrame = avatar.transform.parent != null ? avatar.transform.parent : avatar.transform;
        if (cameras.Count == 0) RefreshCameras();
        if (!cameras.Any(c => c != null && c.IsApplied))
        {
            Debug.LogWarning("[DatasetRecorder] Not recording: no enabled CalibratedCamera with a loaded calibration.");
            return false;
        }
        cameras.RemoveAll(c => c == null);
        foreach (var c in cameras)
            if (!c.IsApplied) Debug.LogWarning($"[DatasetRecorder] '{c.name}' has no loaded calibration; it will be skipped.");
        cameras = cameras.Where(c => c.IsApplied).ToList();
        return true;
    }

    // Gives each camera a texture holding the image the real camera would save.
    void PrepareCapture(CamSlot s)
    {
        if (s.cc.DistortionActive)
        {
            s.cc.EnsureDistortedOutput(s.width, s.height);
            return;
        }
        // Lens distortion not rendered: capture the plain calibrated pinhole through a shadow camera.
        s.shadowRt = new RenderTexture(s.width, s.height, 24, RenderTextureFormat.ARGB32);
        s.shadowRt.Create();
        s.shadowGo = new GameObject($"_DatasetRecorder_{s.name}") { hideFlags = HideFlags.HideAndDontSave };
        s.shadowGo.transform.SetParent(s.cc.transform, false);
        s.shadowCam = s.shadowGo.AddComponent<Camera>();
        s.shadowCam.CopyFrom(s.cc.GetComponent<Camera>());
        s.shadowCam.projectionMatrix = s.cc.Projection;
        s.shadowCam.targetTexture = s.shadowRt;
        s.shadowCam.depth = s.cc.GetComponent<Camera>().depth + 100f;
    }

    static StreamWriter OpenWriter(string path) =>
        new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };

    // ---------------- Record loop ----------------

    IEnumerator RecordLoop()
    {
        while (recording)
        {
            yield return new WaitForEndOfFrame();
            StepCapture();
        }
    }

    void OnEndContextRendering(ScriptableRenderContext context, List<Camera> renderedCameras)
    {
        if (!recording || Time.frameCount == lastCaptureUnityFrame) return;
        // Scene-view-only renders also raise this callback; wait for the frame our cameras rendered in.
        bool ours = false;
        foreach (CamSlot s in slots)
            if (s.cc != null && renderedCameras.Contains(s.cc.GetComponent<Camera>())) { ours = true; break; }
        if (!ours) return;
        lastCaptureUnityFrame = Time.frameCount;
        StepCapture();
    }

    // One recording tick: capture a frame when one is due, then check the stop conditions.
    void StepCapture()
    {
        if (!recording) return;
        double interval = 1.0 / Math.Max(1.0, frameRate);
        double now = Time.timeAsDouble;
        if (now + 1e-4 < nextCaptureGameTime) return;

        double elapsed = now - startGameTime;
        CaptureFrame(elapsed);
        nextCaptureGameTime += interval;
        if (nextCaptureGameTime < now) nextCaptureGameTime = now + interval; // never burst after a stall

        if ((maxSeconds > 0f && elapsed >= maxSeconds) || (stopWhenAnimationEnds && AnimationFinished(elapsed)))
        {
            record = false;
            StopRecording();
            if (exitPlayModeWhenDone)
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            }
        }
    }

    bool AnimationFinished(double elapsed)
    {
        if (elapsed < 0.5 || avatar == null || avatar.runtimeAnimatorController == null || avatar.IsInTransition(0)) return false;
        AnimatorStateInfo s = avatar.GetCurrentAnimatorStateInfo(0);
        return !s.loop && s.normalizedTime >= 1f;
    }

    void CaptureFrame(double elapsed)
    {
        double t = epochStart + elapsed;
        string ts = t.ToString("F6", Inv);
        frameIndex++;

        // Everything below is sampled in the same frame: joints first, then each camera's image.
        WriteSkeleton(t, elapsed);

        if (ptzWriter != null && elapsed >= nextPtzElapsed)
        {
            foreach (CamSlot s in slots)
            {
                if (!s.ptz.HasValue) continue;
                ptzWriter.Write("{\"t\": " + t.ToString("R", Inv) + ", \"camera\": \"" + s.name + "\", \"azimuth\": " +
                                s.ptz.Value.azimuth + ", \"elevation\": " + s.ptz.Value.elevation +
                                ", \"absoluteZoom\": " + s.ptz.Value.zoom + "}\n");
                ptzLines++;
            }
            nextPtzElapsed += Math.Max(0.1f, ptzLogInterval);
        }

        // The disk can fall behind the render: wait here (not on a readback) before queueing more.
        while (Volatile.Read(ref pendingEncodes) > maxPendingEncodes) Thread.Sleep(2);

        StringBuilder gtLine = writeGtOverlay ? BeginGtLine(t) : null;
        bool firstGtCamera = true;

        foreach (CamSlot s in slots)
        {
            RenderTexture src;
            if (s.shadowRt != null) src = s.shadowRt;
            else { s.cc.RenderDistorted(); src = s.cc.DistortedTexture; }
            if (src == null) continue;

            AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(src, 0, TextureFormat.RGB24);
            req.WaitForCompletion();
            if (req.hasError) { Debug.LogWarning($"[DatasetRecorder] readback failed for {s.name} at {ts}."); continue; }

            // GPU rows come bottom-up; JPEGs are top-down.
            var rgb = new byte[s.width * s.height * 3];
            var data = req.GetData<byte>();
            int stride = s.width * 3;
            for (int y = 0; y < s.height; y++)
                Unity.Collections.NativeArray<byte>.Copy(data, (s.height - 1 - y) * stride, rgb, y * stride, stride);

            string path = Path.Combine(s.dir, ts + ".jpg");
            int w = s.width, h = s.height, q = jpegQuality;
            Interlocked.Increment(ref pendingEncodes);
            Task.Run(() =>
            {
                try
                {
                    byte[] jpg = ImageConversion.EncodeArrayToJPG(rgb, GraphicsFormat.R8G8B8_UNorm, (uint)w, (uint)h, 0, q);
                    File.WriteAllBytes(path, jpg);
                }
                catch (Exception e) { Debug.LogError("[DatasetRecorder] jpg: " + e); }
                finally { Interlocked.Decrement(ref pendingEncodes); }
            });
            s.frames++;

            if (gtLine != null) WriteGtForCamera(s, ts, rgb, gtLine, ref firstGtCamera);
        }

        if (gtLine != null)
        {
            gtLine.Append("}}");
            gtWriter.Write(gtLine.ToString()); gtWriter.Write('\n');
        }
    }

    // ---------------- Skeleton ----------------

    void ResolveBones()
    {
        bones.Clear();
        bool human = avatar.isHuman;
        Transform[] all = avatar.GetComponentsInChildren<Transform>(true);
        foreach (var (name, bone) in BoneTable)
        {
            var slot = new BoneSlot { name = name };
            if (bone == HumanBodyBones.LastBone)
            {
                slot.tip = true;
                bool left = name.StartsWith("left", StringComparison.Ordinal);
                slot.tr = human ? avatar.GetBoneTransform(left ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes) : null;
                slot.foot = human ? avatar.GetBoneTransform(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot) : null;
                if (slot.tr == null) slot.tr = FindByName(all, NameCandidates(left ? "leftToe" : "rightToe"));
                if (slot.foot == null) slot.foot = FindByName(all, NameCandidates(left ? "leftFoot" : "rightFoot"));
            }
            else
            {
                slot.tr = human ? avatar.GetBoneTransform(bone) : null;
                if (slot.tr == null && human && bone == HumanBodyBones.Chest) slot.tr = avatar.GetBoneTransform(HumanBodyBones.UpperChest);
                if (slot.tr == null && human && bone == HumanBodyBones.Chest) slot.tr = avatar.GetBoneTransform(HumanBodyBones.Spine);
                if (slot.tr == null) slot.tr = FindByName(all, NameCandidates(name));
            }
            if (slot.tr == null) Debug.LogWarning($"[DatasetRecorder] avatar has no bone for '{name}'; it is left out of the skeleton.");
            bones.Add(slot);
        }
    }

    // Bone names for rigs without a humanoid Avatar (Mixamo "prefix:Name" and the usual variants).
    static string[] NameCandidates(string rokokoName)
    {
        switch (rokokoName)
        {
            case "hip": return new[] { "Hips", "Pelvis", "Hip" };
            case "spine": return new[] { "Spine" };
            case "chest": return new[] { "Chest", "Spine1", "UpperChest", "Spine2" };
            case "neck": return new[] { "Neck" };
            case "head": return new[] { "Head" };
        }
        bool left = rokokoName.StartsWith("left", StringComparison.Ordinal);
        string part = rokokoName.Substring(left ? 4 : 5);
        string[] parts;
        switch (part)
        {
            case "Shoulder": parts = new[] { "Shoulder", "Clavicle", "Collar" }; break;
            case "UpperArm": parts = new[] { "Arm", "UpperArm" }; break;
            case "LowerArm": parts = new[] { "ForeArm", "LowerArm" }; break;
            case "UpLeg": parts = new[] { "UpLeg", "UpperLeg", "Thigh" }; break;
            case "Leg": parts = new[] { "Leg", "LowerLeg", "Calf" }; break;
            case "Toe": parts = new[] { "ToeBase", "Toes", "Toe" }; break;
            default: parts = new[] { part }; break; // Hand, Foot
        }
        var list = new List<string>();
        foreach (string p in parts) { list.Add((left ? "Left" : "Right") + p); list.Add((left ? "L_" : "R_") + p); }
        return list.ToArray();
    }

    static Transform FindByName(Transform[] all, string[] candidates)
    {
        foreach (string cand in candidates)
            foreach (Transform t in all)
            {
                string n = t.name;
                int colon = n.LastIndexOf(':');
                if (colon >= 0) n = n.Substring(colon + 1);
                if (string.Equals(n, cand, StringComparison.OrdinalIgnoreCase)) return t;
            }
        return null;
    }

    // Humanoid rigs know their scale. For a Generic rig: the hips' height above the lower foot in the pose
    // the recording starts from (an approximation: the avatar may not be standing).
    float EstimateHipHeight()
    {
        if (avatar.isHuman) return avatar.humanScale;
        Transform hips = bones.FirstOrDefault(b => b.name == "hip")?.tr;
        Transform lf = bones.FirstOrDefault(b => b.name == "leftFoot")?.tr;
        Transform rf = bones.FirstOrDefault(b => b.name == "rightFoot")?.tr;
        if (hips == null || (lf == null && rf == null)) return 1f;
        float floor = Mathf.Min(lf != null ? lf.position.y : float.MaxValue, rf != null ? rf.position.y : float.MaxValue);
        return Mathf.Max(0.1f, hips.position.y - floor);
    }

    // Writes this frame's rokoko_skeleton.jsonl line (Rokoko frame, left-handed) and, if enabled,
    // the matching ground-truth line of recalage/rokoko_corrected.jsonl (world frame, right-handed).
    void WriteSkeleton(double t, double elapsed)
    {
        Quaternion invRf = Quaternion.Inverse(rokokoFrame.rotation);
        Quaternion invWf = Quaternion.Inverse(worldFrame.rotation);

        var raw = new StringBuilder(4096);
        raw.Append("{\"frame_idx\": ").Append(frameIndex)
           .Append(", \"t_recv\": ").Append(Num(t))
           .Append(", \"version\": 3, \"fps\": ").Append(Num(frameRate))
           .Append(", \"studio_timestamp\": ").Append(Num(elapsed))
           .Append(", \"actors\": [{\"name\": \"").Append(Esc(avatar.name))
           .Append("\", \"hip_height\": ").Append(Num(hipHeight)).Append(", \"bones\": {");

        StringBuilder world = writeWorldSkeleton ? new StringBuilder(4096) : null;
        if (world != null)
        {
            RegistrationOf(out float yaw, out Vector3 tvec);
            world.Append("{\"t\": ").Append(Num(t))
                 .Append(", \"source\": {\"camera\": \"unity_ground_truth\", \"n_events\": 0, \"nearest_event_age_s\": 0.0, \"tag_locations\": {}}")
                 .Append(", \"registration\": {\"yaw_deg\": ").Append(Num(yaw))
                 .Append(", \"tvec\": [").Append(Num(tvec.x)).Append(", ").Append(Num(tvec.y)).Append(", ").Append(Num(tvec.z))
                 .Append("], \"torso_thickness_m\": null, \"camera_delay_s\": 0.0}")
                 .Append(", \"actors\": [{\"name\": \"").Append(Esc(avatar.name))
                 .Append("\", \"hip_height\": ").Append(Num(hipHeight)).Append(", \"bones\": {");
        }

        bool first = true;
        bool recordedGt = writeGtOverlay && gtUsesRecordedJoints;
        for (int bi = 0; bi < bones.Count; bi++)
        {
            BoneSlot b = bones[bi];
            if (b.tr == null) { if (recordedGt) gtValid[bi] = false; continue; }
            JointWorldPose(b, out Vector3 pos, out Quaternion rot);
            if (recordedGt) { gtPos[bi] = pos; gtValid[bi] = true; }

            // Rokoko frame (what rokoko_skeleton.jsonl holds): left-handed, in the clip's frame.
            Vector3 pR = invRf * (pos - rokokoFrame.position);
            Quaternion qR = Continuous(invRf * rot, ref b.prevRokoko, ref b.hasPrevRokoko);

            raw.Append(first ? "" : ", ").Append('"').Append(b.name).Append("\": ");
            AppendJoint(raw, pR, qR);

            if (world != null)
            {
                Vector3 pL = invWf * (pos - worldFrame.position);
                Quaternion qL = invWf * rot;
                Quaternion qW = Continuous(MirrorRotation(qL), ref b.prevWorld, ref b.hasPrevWorld);
                world.Append(first ? "" : ", ").Append('"').Append(b.name).Append("\": ");
                AppendJoint(world, MirrorPoint(pL), qW);
            }
            first = false;
        }
        if (writeGtOverlay && !gtUsesRecordedJoints) UpdateGtFromFormat();
        raw.Append("}}]}");
        skeletonWriter.Write(raw.ToString()); skeletonWriter.Write('\n');
        if (world != null)
        {
            world.Append("}}], \"placeholder_extrinsics\": false}");
            worldWriter.Write(world.ToString()); worldWriter.Write('\n');
        }
    }

    // The avatar's actual joint pose in Unity world space.
    static void JointWorldPose(BoneSlot b, out Vector3 pos, out Quaternion rot)
    {
        if (!b.tip) { pos = b.tr.position; rot = b.tr.rotation; return; }
        // Toe tip: the toe's end transform if the rig has one, else a short step along foot -> toe.
        if (b.tr.childCount > 0) { Transform end = b.tr.GetChild(0); pos = end.position; rot = end.rotation; return; }
        Vector3 dir = b.foot != null ? (b.tr.position - b.foot.position).normalized : b.tr.forward;
        pos = b.tr.position + dir * 0.05f; rot = b.tr.rotation;
    }

    // q and -q are the same rotation; keep the sign continuous frame to frame so later SLERP/diffs behave.
    static Quaternion Continuous(Quaternion q, ref Quaternion prev, ref bool hasPrev)
    {
        if (hasPrev && Quaternion.Dot(q, prev) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        prev = q;
        hasPrev = true;
        return q;
    }

    Vector3 MirrorPoint(Vector3 p) => handedness == CalibratedCamera.WorldMirror.NegateX
        ? new Vector3(-p.x, p.y, p.z) : new Vector3(p.x, p.y, -p.z);

    // Rotation seen through a mirror: S R S, i.e. (x, -y, -z, w) for a flipped x axis, (-x, -y, z, w) for z.
    Quaternion MirrorRotation(Quaternion q) => handedness == CalibratedCamera.WorldMirror.NegateX
        ? new Quaternion(q.x, -q.y, -q.z, q.w) : new Quaternion(-q.x, -q.y, q.z, q.w);

    // The Rokoko -> world registration this placement amounts to: world = Ry(yaw) * Sx * rokoko + tvec,
    // the same quantity tools/07_recalage.py estimates (yaw in degrees, tvec in meters).
    void RegistrationOf(out float yawDeg, out Vector3 tvec)
    {
        Quaternion invWf = Quaternion.Inverse(worldFrame.rotation);
        tvec = MirrorPoint(invWf * (rokokoFrame.position - worldFrame.position));

        Matrix4x4 m = Matrix4x4.Rotate(invWf * rokokoFrame.rotation); // Rokoko frame in the (Unity-handed) world frame
        bool nx = handedness == CalibratedCamera.WorldMirror.NegateX;
        // M = Sw * R * Sx, with Sw the world mirror and Sx the Rokoko left -> right-handed flip.
        float[,] r = new float[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                float sw = (nx && i == 0) || (!nx && i == 2) ? -1f : 1f;
                float sx = j == 0 ? -1f : 1f;
                r[i, j] = sw * m[i, j] * sx;
            }
        yawDeg = Mathf.Atan2(r[0, 2] - r[2, 0], r[0, 0] + r[2, 2]) * Mathf.Rad2Deg;
    }

    // ---------------- Ground-truth overlay (gt/) ----------------

    void SetupGt()
    {
        gtDef = gtUsesRecordedJoints ? RecordedJointsDef() : SkeletonFormats.Build(gtFormat);
        gtColors = SkeletonFormats.Colors(gtDef);
        gtPos = new Vector3[gtDef.Count];
        gtValid = new bool[gtDef.Count];
        if (!gtUsesRecordedJoints)
        {
            gtRig = new SkeletonFormats.RigBones();
            SkeletonFormats.Resolve(avatar, gtRig);
            gtOffsets = SkeletonFormats.Offsets.Default;
            if (!SkeletonFormats.AnyResolved(gtRig, gtDef, gtOffsets))
                Debug.LogWarning($"[DatasetRecorder] no bones resolved for the '{gtFormat}' keypoints; the overlay will be empty.");
        }

        string gt = Path.Combine(sessionPath, "gt");
        Directory.CreateDirectory(gt);
        foreach (CamSlot s in slots)
        {
            s.gtKeyDir = Path.Combine(gt, s.name, "keyrgb");
            Directory.CreateDirectory(s.gtKeyDir);
            if (writeGtBoxes)
            {
                s.gtBoxDir = Path.Combine(gt, s.name, "bboxes");
                Directory.CreateDirectory(s.gtBoxDir);
            }
        }

        var sb = new StringBuilder(1024);
        sb.Append("{\n  \"format\": \"").Append(gtDef.tag).Append("\",\n");
        sb.Append("  \"keypoint_names\": [").Append(string.Join(", ", gtDef.names.Select(n => "\"" + n + "\""))).Append("],\n");
        sb.Append("  \"skeleton\": [");
        for (int e = 0; e < gtDef.edges.GetLength(0); e++)
            sb.Append(e > 0 ? ", " : "").Append('[').Append(gtDef.edges[e, 0]).Append(", ").Append(gtDef.edges[e, 1]).Append(']');
        sb.Append("],\n");
        sb.Append("  \"image_origin\": \"top_left\",\n");
        sb.Append("  \"pixel_convention\": \"OpenCV: u right, v down, pixel centres at integer coordinates; images are the lens-distorted camera images\",\n");
        sb.Append("  \"world_frame\": \"right-handed, Y up, meters, MOCAPcenter (same world as Calibration/camera_extrinsics.json)\",\n");
        sb.Append("  \"visibility\": \"in front of the camera and inside the image; occlusion by the scene is not modelled\",\n");
        sb.Append("  \"files\": {\"overlay\": \"<camera>/keyrgb/<t>.jpg\", \"boxes\": \"<camera>/bboxes/<t>.txt\", \"keypoints\": \"keypoints_2d.jsonl\"}\n}\n");
        File.WriteAllText(Path.Combine(gt, "skeleton.json"), sb.ToString(), new UTF8Encoding(false));

        gtWriter = OpenWriter(Path.Combine(gt, "keypoints_2d.jsonl"));
    }

    // The 23 joints of rokoko_skeleton.jsonl, joined along the body (indices follow BoneTable).
    static SkeletonFormats.SkeletonDef RecordedJointsDef() => new SkeletonFormats.SkeletonDef
    {
        tag = "rokoko_23",
        names = BoneTable.Select(b => b.name).ToArray(),
        joints = new SkeletonFormats.Joint[BoneTable.Length],
        edges = new int[,]
        {
            { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 4 },                                  // hip - spine - chest - neck - head
            { 2, 5 }, { 5, 6 }, { 6, 7 }, { 7, 8 },                                  // left arm
            { 2, 9 }, { 9, 10 }, { 10, 11 }, { 11, 12 },                             // right arm
            { 0, 13 }, { 13, 14 }, { 14, 15 }, { 15, 16 }, { 16, 17 },               // left leg
            { 0, 18 }, { 18, 19 }, { 19, 20 }, { 20, 21 }, { 21, 22 },               // right leg
        },
    };

    // Dataset-format keypoints (AIC, COCO, ...) from the rig, the way MultiViewRecorder derives them.
    void UpdateGtFromFormat()
    {
        for (int k = 0; k < gtDef.Count; k++)
            gtPos[k] = SkeletonFormats.WorldPosition(gtRig, gtDef.joints[k], gtOffsets, out gtValid[k]);
    }

    // Start of this frame's keypoints_2d.jsonl line: time, frame index, and the keypoints in the world frame.
    StringBuilder BeginGtLine(double t)
    {
        Quaternion invWf = Quaternion.Inverse(worldFrame.rotation);
        var sb = new StringBuilder(2048);
        sb.Append("{\"t\": ").Append(Num(t)).Append(", \"frame_idx\": ").Append(frameIndex).Append(", \"world_xyz\": [");
        for (int k = 0; k < gtDef.Count; k++)
        {
            sb.Append(k > 0 ? ", " : "");
            if (!gtValid[k]) { sb.Append("null"); continue; }
            Vector3 p = MirrorPoint(invWf * (gtPos[k] - worldFrame.position));
            sb.Append('[').Append(Num(p.x)).Append(", ").Append(Num(p.y)).Append(", ").Append(Num(p.z)).Append(']');
        }
        sb.Append("], \"cameras\": {");
        return sb;
    }

    // Projects the keypoints through this camera (calibrated K and, when rendered, the lens distortion),
    // appends them to the frame's line, writes the YOLO box, and queues the image with the joints drawn on it.
    void WriteGtForCamera(CamSlot s, string ts, byte[] rgb, StringBuilder line, ref bool firstCamera)
    {
        int n = gtDef.Count;
        var px = new Vector2[n];
        var vis = new bool[n];
        var depth = new float[n];
        for (int k = 0; k < n; k++)
        {
            if (!gtValid[k]) continue;
            Vector3 vp = s.cc.WorldToViewport(gtPos[k]);
            depth[k] = vp.z;
            if (vp.z <= 0f) continue; // behind the camera or past the distortion model's reach
            float uc = vp.x * s.width, vc = (1f - vp.y) * s.height;
            px[k] = new Vector2(uc - 0.5f, vc - 0.5f);
            vis[k] = vp.z > s.cc.nearClip && uc >= 0f && uc < s.width && vc >= 0f && vc < s.height;
        }

        line.Append(firstCamera ? "" : ", ").Append('"').Append(s.name).Append("\": {\"image\": \"")
            .Append(s.name).Append('/').Append(ts).Append(".jpg\", \"keypoints\": [");
        for (int k = 0; k < n; k++)
        {
            line.Append(k > 0 ? ", " : "");
            if (!gtValid[k] || depth[k] <= 0f) { line.Append("{\"u\": null, \"v\": null, \"depth\": null, \"visible\": false}"); continue; }
            line.Append("{\"u\": ").Append(Num(px[k].x)).Append(", \"v\": ").Append(Num(px[k].y))
                .Append(", \"depth\": ").Append(Num(depth[k])).Append(", \"visible\": ").Append(vis[k] ? "true" : "false").Append('}');
        }
        line.Append("]}");
        firstCamera = false;

        if (s.gtBoxDir != null)
            File.WriteAllText(Path.Combine(s.gtBoxDir, ts + ".txt"), YoloBox(px, vis, s.width, s.height), new UTF8Encoding(false));

        string keyPath = Path.Combine(s.gtKeyDir, ts + ".jpg");
        int w = s.width, h = s.height, q = jpegQuality, radius = pointRadiusPx, thickness = lineThicknessPx;
        bool skeleton = drawSkeleton;
        Color32[] colors = gtColors;
        int[,] edges = gtDef.edges;
        Interlocked.Increment(ref pendingEncodes);
        Task.Run(() =>
        {
            try
            {
                byte[] buf = (byte[])rgb.Clone(); // rgb is shared with the plain image's encode
                MultiViewRecorder.DrawOverlay(buf, w, h, px, vis, colors, edges, radius, thickness, skeleton);
                File.WriteAllBytes(keyPath, ImageConversion.EncodeArrayToJPG(buf, GraphicsFormat.R8G8B8_UNorm, (uint)w, (uint)h, 0, q));
            }
            catch (Exception e) { Debug.LogError("[DatasetRecorder] keyrgb: " + e); }
            finally { Interlocked.Decrement(ref pendingEncodes); }
        });
    }

    // "0 xc yc w h" (normalized) around the visible joints, or nothing when fewer than two are visible.
    string YoloBox(Vector2[] px, bool[] vis, int w, int h)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        int count = 0;
        for (int k = 0; k < px.Length; k++)
        {
            if (!vis[k]) continue;
            count++;
            x0 = Mathf.Min(x0, px[k].x); x1 = Mathf.Max(x1, px[k].x);
            y0 = Mathf.Min(y0, px[k].y); y1 = Mathf.Max(y1, px[k].y);
        }
        if (count < 2) return "";
        float padX = (x1 - x0) * gtBoxPadding, padY = (y1 - y0) * gtBoxPadding;
        x0 = Mathf.Clamp(x0 - padX, 0f, w - 1f); x1 = Mathf.Clamp(x1 + padX, 0f, w - 1f);
        y0 = Mathf.Clamp(y0 - padY, 0f, h - 1f); y1 = Mathf.Clamp(y1 + padY, 0f, h - 1f);
        if (x1 <= x0 || y1 <= y0) return "";
        return "0 " + ((x0 + x1) / 2f / w).ToString("F6", Inv) + " " + ((y0 + y1) / 2f / h).ToString("F6", Inv) + " " +
               ((x1 - x0) / w).ToString("F6", Inv) + " " + ((y1 - y0) / h).ToString("F6", Inv) + "\n";
    }

    // ---------------- PTZ ----------------

    // cameras.<key>.ptz_pose in the extrinsics JSON, when present (the real pose the extrinsics were solved at).
    static (int, int, int)? ReadPtz(CalibratedCamera cc)
    {
        try
        {
            var root = (Dictionary<string, object>)MiniJson.Parse(cc.extrinsicsJson.text);
            var cam = (Dictionary<string, object>)((Dictionary<string, object>)root["cameras"])[cc.cameraKey];
            if (!cam.TryGetValue("ptz_pose", out object o)) return null;
            var p = (Dictionary<string, object>)o;
            return ((int)(double)p["azimuth"], (int)(double)p["elevation"], (int)(double)p["absoluteZoom"]);
        }
        catch { return null; }
    }

    // ---------------- Stop ----------------

    void StopRecording()
    {
        if (!recording) return;
        recording = false;
        if (loop != null) { StopCoroutine(loop); loop = null; }
        if (contextHooked) { RenderPipelineManager.endContextRendering -= OnEndContextRendering; contextHooked = false; }

        // Let the encoders finish before the files are declared done.
        for (int waited = 0; Volatile.Read(ref pendingEncodes) > 0 && waited < 60000; waited += 10) Thread.Sleep(10);

        double duration = Time.timeAsDouble - startGameTime;
        skeletonWriter?.Dispose(); skeletonWriter = null;
        worldWriter?.Dispose(); worldWriter = null;
        ptzWriter?.Dispose(); ptzWriter = null;
        gtWriter?.Dispose(); gtWriter = null;

        if (writeSessionMeta) WriteSessionMeta(duration);

        foreach (CamSlot s in slots)
        {
            if (s.shadowGo != null) Destroy(s.shadowGo);
            if (s.shadowRt != null) { s.shadowRt.Release(); Destroy(s.shadowRt); }
        }
        if (setCaptureFramerate) Time.captureFramerate = 0;

        Debug.Log($"[DatasetRecorder] Done: {frameIndex} frames, {slots.Count} cameras -> {sessionPath}");
        record = false;
    }

    void WriteSessionMeta(double duration)
    {
        var sb = new StringBuilder(1024);
        sb.Append("{\n  \"cameras\": [").Append(string.Join(", ", slots.Select(s => "\"" + s.name + " unity\""))).Append("],\n");
        sb.Append("  \"duration_s\": ").Append(Num(duration)).Append(",\n");
        sb.Append("  \"frame_counts\": [").Append(string.Join(", ", slots.Select(s => s.frames))).Append("],\n");
        if (ptzLines > 0)
            sb.Append("  \"ptz_pose\": {\"file\": \"ptz_pose.jsonl\", \"poll_interval_s\": ").Append(Num(ptzLogInterval))
              .Append(", \"samples\": ").Append(ptzLines / Math.Max(1, slots.Count(s => s.ptz.HasValue)))
              .Append(", \"cameras_logged\": [").Append(string.Join(", ", slots.Where(s => s.ptz.HasValue).Select(s => "\"" + s.name + "\""))).Append("]},\n");
        else sb.Append("  \"ptz_pose\": null,\n");
        sb.Append("  \"rokoko\": {\"listen_ip\": null, \"listen_port\": null, \"packets_received\": ").Append(frameIndex)
          .Append(", \"decode_errors\": 0, \"frame_count\": ").Append(frameIndex).Append("},\n");
        sb.Append("  \"studio_streaming\": null,\n  \"studio_recording\": null,\n");
        sb.Append("  \"notes\": \"Synthetic session rendered in Unity (DatasetRecorder): images through the calibrated cameras with ")
          .Append("their lens distortion; rokoko_skeleton.jsonl holds the avatar's actual joint transforms in the Rokoko frame; ")
          .Append("recalage/rokoko_corrected.jsonl is the ground-truth world-frame skeleton (MOCAPcenter, right-handed).\"\n}\n");
        File.WriteAllText(Path.Combine(sessionPath, "session_meta.json"), sb.ToString(), new UTF8Encoding(false));
    }

    // ---------------- JSON helpers ----------------

    static void AppendJoint(StringBuilder sb, Vector3 p, Quaternion q)
    {
        sb.Append("{\"position\": {\"x\": ").Append(Num(p.x)).Append(", \"y\": ").Append(Num(p.y)).Append(", \"z\": ").Append(Num(p.z))
          .Append("}, \"rotation\": {\"x\": ").Append(Num(q.x)).Append(", \"y\": ").Append(Num(q.y)).Append(", \"z\": ").Append(Num(q.z))
          .Append(", \"w\": ").Append(Num(q.w)).Append("}}");
    }

    // Python-style floats: whole numbers keep a ".0".
    static string Num(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "0.0" : Dot0(v.ToString("R", Inv));
    static string Num(float v) => float.IsNaN(v) || float.IsInfinity(v) ? "0.0" : Dot0(v.ToString("R", Inv));
    static string Dot0(string n) => n.IndexOfAny(new[] { '.', 'E', 'e' }) >= 0 ? n : n + ".0";
    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
