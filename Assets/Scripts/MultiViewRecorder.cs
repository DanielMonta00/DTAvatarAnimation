using UnityEngine;
using UnityEngine.Experimental.Rendering; // GraphicsFormat
using UnityEngine.Rendering;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Multi-view dataset recorder. Attach to a GameObject whose children contain
// the Camera components you want to record from. The Cameras list is auto-
// populated from descendants on Reset / via the context menu, and is editable
// in the Inspector if you want to add/remove/reorder.
//
// Per session, writes:
//   <session>/cameras.json                       Intrinsics + extrinsics per camera
//   <session>/keypoints_transforms.json          Per-frame keypoint transforms with
//                                                per-camera projections.
//   <session>/cam_N/rgb/{ts}.{jpg|png}           RGB image from camera N
//   <session>/cam_N/keyrgb/{ts}.{jpg|png}        RGB + keypoint overlay (if enabled)
//   <session>/cam_N/bboxes/{ts}.json             Per-frame bbox JSON (if enabled)
//   <session>/cam_N/bbox_vis/{ts}.png            Bboxes drawn on RGB (debug, if enabled)
//
// Layout is symmetric per camera so cam_N is a self-contained folder you can
// copy / rsync / inspect independently. cameras.json is the single calibration
// file consumers should read first to know what's in each cam_N/.
//
// Frame alignment: all per-camera outputs share the same timestamp filename. A
// frame is committed only after every camera's GPU readback succeeds, so the
// JSON entry count equals the file count in each cam_N/rgb/ folder.
[DefaultExecutionOrder(900)]
public class MultiViewRecorder : MonoBehaviour
{
    // Skeleton format enum for ViTPose datasets
    public enum SkeletonFormat
    {
        AIC,       // 14 keypoints
        COCO,      // 17 keypoints
        MPII,      // 16 keypoints
        CrowdPose  // 14 keypoints
    }

    // Skeleton format definitions
    [System.Serializable]
    public class SkeletonFormatDef
    {
        public string[] keypointNames;
        public int[,] skeleton;
        public string[] boneNameLeftUpperArm;
        public string[] boneNameRightUpperArm;
        public string[] boneNameLeftLowerArm;
        public string[] boneNameRightLowerArm;
        public string[] boneNameLeftHand;
        public string[] boneNameRightHand;
        public string[] boneNameLeftUpperLeg;
        public string[] boneNameRightUpperLeg;
        public string[] boneNameLeftLowerLeg;
        public string[] boneNameRightLowerLeg;
        public string[] boneNameLeftFoot;
        public string[] boneNameRightFoot;
        public string[] boneNameHead;
        public string[] boneNameNeck;
        public string[] boneNameNose;
        public string[] boneNameLeftEye;
        public string[] boneNameRightEye;
        public string[] boneNameLeftEar;
        public string[] boneNameRightEar;
        public string[] boneNamePelvis;
        public string[] boneNameThorax;
        public string[] boneNameUpperNeck;
    }

    public int GetKeypointCount(SkeletonFormat format)
    {
        return format switch
        {
            SkeletonFormat.AIC => 14,
            SkeletonFormat.COCO => 17,
            SkeletonFormat.MPII => 16,
            SkeletonFormat.CrowdPose => 14,
            _ => 14
        };
    }

    public SkeletonFormatDef GetSkeletonFormat(SkeletonFormat format)
    {
        return format switch
        {
            SkeletonFormat.COCO => CreateCOCOFormat(),
            SkeletonFormat.MPII => CreateMPIIFormat(),
            SkeletonFormat.CrowdPose => CreateCrowdPoseFormat(),
            _ => CreateAICFormat()
        };
    }

    static SkeletonFormatDef CreateAICFormat()
    {
        return new SkeletonFormatDef
        {
            keypointNames = new[]
            {
                "right_shoulder", "right_elbow", "right_wrist",
                "left_shoulder",  "left_elbow",  "left_wrist",
                "right_hip", "right_knee", "right_ankle",
                "left_hip",  "left_knee",  "left_ankle",
                "head_top", "neck",
            },
            skeleton = new[,]
            {
                {2,1},{1,0},{0,13},{13,3},{3,4},{4,5},
                {8,7},{7,6},{6,9},{9,10},{10,11},
                {12,13},{0,6},{3,9}
            },
            boneNameLeftUpperArm = new[] { "LeftArm", "LeftUpperArm", "L_UpperArm" },
            boneNameRightUpperArm = new[] { "RightArm", "RightUpperArm", "R_UpperArm" },
            boneNameLeftLowerArm = new[] { "LeftForeArm", "LeftLowerArm", "L_ForeArm" },
            boneNameRightLowerArm = new[] { "RightForeArm", "RightLowerArm", "R_ForeArm" },
            boneNameLeftHand = new[] { "LeftHand", "L_Hand" },
            boneNameRightHand = new[] { "RightHand", "R_Hand" },
            boneNameLeftUpperLeg = new[] { "LeftUpLeg", "LeftUpperLeg", "L_UpperLeg", "LeftThigh" },
            boneNameRightUpperLeg = new[] { "RightUpLeg", "RightUpperLeg", "R_UpperLeg", "RightThigh" },
            boneNameLeftLowerLeg = new[] { "LeftLeg", "LeftLowerLeg", "L_LowerLeg", "LeftCalf" },
            boneNameRightLowerLeg = new[] { "RightLeg", "RightLowerLeg", "R_LowerLeg", "RightCalf" },
            boneNameLeftFoot = new[] { "LeftFoot", "L_Foot" },
            boneNameRightFoot = new[] { "RightFoot", "R_Foot" },
            boneNameHead = new[] { "Head" },
            boneNameNeck = new[] { "Neck" },
            boneNameNose = new string[] { },
            boneNameLeftEye = new string[] { },
            boneNameRightEye = new string[] { },
            boneNameLeftEar = new string[] { },
            boneNameRightEar = new string[] { },
            boneNamePelvis = new string[] { },
            boneNameThorax = new string[] { },
            boneNameUpperNeck = new string[] { }
        };
    }

    static SkeletonFormatDef CreateCOCOFormat()
    {
        return new SkeletonFormatDef
        {
            keypointNames = new[]
            {
                "nose",
                "left_eye", "right_eye",
                "left_ear", "right_ear",
                "left_shoulder", "right_shoulder",
                "left_elbow", "right_elbow",
                "left_wrist", "right_wrist",
                "left_hip", "right_hip",
                "left_knee", "right_knee",
                "left_ankle", "right_ankle"
            },
            skeleton = new[,]
            {
                {0,1},{0,2},{1,3},{2,4},{3,5},{4,6},
                {5,7},{7,9},{6,8},{8,10},
                {5,11},{6,12},{11,13},{13,15},{12,14},{14,16},
                {11,12}
            },
            boneNameLeftUpperArm = new[] { "LeftArm", "LeftUpperArm", "L_UpperArm" },
            boneNameRightUpperArm = new[] { "RightArm", "RightUpperArm", "R_UpperArm" },
            boneNameLeftLowerArm = new[] { "LeftForeArm", "LeftLowerArm", "L_ForeArm" },
            boneNameRightLowerArm = new[] { "RightForeArm", "RightLowerArm", "R_ForeArm" },
            boneNameLeftHand = new[] { "LeftHand", "L_Hand" },
            boneNameRightHand = new[] { "RightHand", "R_Hand" },
            boneNameLeftUpperLeg = new[] { "LeftUpLeg", "LeftUpperLeg", "L_UpperLeg", "LeftThigh" },
            boneNameRightUpperLeg = new[] { "RightUpLeg", "RightUpperLeg", "R_UpperLeg", "RightThigh" },
            boneNameLeftLowerLeg = new[] { "LeftLeg", "LeftLowerLeg", "L_LowerLeg", "LeftCalf" },
            boneNameRightLowerLeg = new[] { "RightLeg", "RightLowerLeg", "R_LowerLeg", "RightCalf" },
            boneNameLeftFoot = new[] { "LeftFoot", "L_Foot" },
            boneNameRightFoot = new[] { "RightFoot", "R_Foot" },
            boneNameHead = new[] { "Head" },
            boneNameNeck = new[] { "Neck" },
            boneNameNose = new[] { "Nose", "nose" },
            boneNameLeftEye = new[] { "LeftEye", "Eye_L" },
            boneNameRightEye = new[] { "RightEye", "Eye_R" },
            boneNameLeftEar = new[] { "LeftEar", "Ear_L" },
            boneNameRightEar = new[] { "RightEar", "Ear_R" },
            boneNamePelvis = new string[] { },
            boneNameThorax = new string[] { },
            boneNameUpperNeck = new string[] { }
        };
    }

    static SkeletonFormatDef CreateMPIIFormat()
    {
        return new SkeletonFormatDef
        {
            keypointNames = new[]
            {
                "right_ankle", "right_knee", "right_hip",
                "left_hip", "left_knee", "left_ankle",
                "pelvis", "thorax", "upper_neck", "head",
                "right_wrist", "right_elbow", "right_shoulder",
                "left_shoulder", "left_elbow", "left_wrist"
            },
            skeleton = new[,]
            {
                {0,1},{1,2},{2,6},{3,4},{4,5},{6,3},{7,6},{8,7},
                {9,8},{10,11},{11,12},{12,2},{13,3},{14,13},{15,14},{12,13}
            },
            boneNameLeftUpperArm = new[] { "LeftArm", "LeftUpperArm", "L_UpperArm" },
            boneNameRightUpperArm = new[] { "RightArm", "RightUpperArm", "R_UpperArm" },
            boneNameLeftLowerArm = new[] { "LeftForeArm", "LeftLowerArm", "L_ForeArm" },
            boneNameRightLowerArm = new[] { "RightForeArm", "RightLowerArm", "R_ForeArm" },
            boneNameLeftHand = new[] { "LeftHand", "L_Hand" },
            boneNameRightHand = new[] { "RightHand", "R_Hand" },
            boneNameLeftUpperLeg = new[] { "LeftUpLeg", "LeftUpperLeg", "L_UpperLeg", "LeftThigh" },
            boneNameRightUpperLeg = new[] { "RightUpLeg", "RightUpperLeg", "R_UpperLeg", "RightThigh" },
            boneNameLeftLowerLeg = new[] { "LeftLeg", "LeftLowerLeg", "L_LowerLeg", "LeftCalf" },
            boneNameRightLowerLeg = new[] { "RightLeg", "RightLowerLeg", "R_LowerLeg", "RightCalf" },
            boneNameLeftFoot = new[] { "LeftFoot", "L_Foot" },
            boneNameRightFoot = new[] { "RightFoot", "R_Foot" },
            boneNameHead = new[] { "Head" },
            boneNameNeck = new[] { "Neck" },
            boneNameNose = new string[] { },
            boneNameLeftEye = new string[] { },
            boneNameRightEye = new string[] { },
            boneNameLeftEar = new string[] { },
            boneNameRightEar = new string[] { },
            boneNamePelvis = new[] { "Pelvis", "Hips", "Spine" },
            boneNameThorax = new[] { "Thorax", "Spine1", "Chest" },
            boneNameUpperNeck = new[] { "UpperNeck", "UpperSpine" }
        };
    }

    static SkeletonFormatDef CreateCrowdPoseFormat()
    {
        return new SkeletonFormatDef
        {
            keypointNames = new[]
            {
                "left_shoulder", "right_shoulder",
                "left_elbow", "right_elbow",
                "left_wrist", "right_wrist",
                "left_hip", "right_hip",
                "left_knee", "right_knee",
                "left_ankle", "right_ankle",
                "top_head", "neck"
            },
            skeleton = new[,]
            {
                {0,2},{2,4},{1,3},{3,5},
                {6,7},{6,8},{8,10},{7,9},{9,11},
                {0,1},{0,6},{1,7},{13,12},{0,13},{1,13}
            },
            boneNameLeftUpperArm = new[] { "LeftArm", "LeftUpperArm", "L_UpperArm" },
            boneNameRightUpperArm = new[] { "RightArm", "RightUpperArm", "R_UpperArm" },
            boneNameLeftLowerArm = new[] { "LeftForeArm", "LeftLowerArm", "L_ForeArm" },
            boneNameRightLowerArm = new[] { "RightForeArm", "RightLowerArm", "R_ForeArm" },
            boneNameLeftHand = new[] { "LeftHand", "L_Hand" },
            boneNameRightHand = new[] { "RightHand", "R_Hand" },
            boneNameLeftUpperLeg = new[] { "LeftUpLeg", "LeftUpperLeg", "L_UpperLeg", "LeftThigh" },
            boneNameRightUpperLeg = new[] { "RightUpLeg", "RightUpperLeg", "R_UpperLeg", "RightThigh" },
            boneNameLeftLowerLeg = new[] { "LeftLeg", "LeftLowerLeg", "L_LowerLeg", "LeftCalf" },
            boneNameRightLowerLeg = new[] { "RightLeg", "RightLowerLeg", "R_LowerLeg", "RightCalf" },
            boneNameLeftFoot = new[] { "LeftFoot", "L_Foot" },
            boneNameRightFoot = new[] { "RightFoot", "R_Foot" },
            boneNameHead = new[] { "Head" },
            boneNameNeck = new[] { "Neck" },
            boneNameNose = new string[] { },
            boneNameLeftEye = new string[] { },
            boneNameRightEye = new string[] { },
            boneNameLeftEar = new string[] { },
            boneNameRightEar = new string[] { },
            boneNamePelvis = new string[] { },
            boneNameThorax = new string[] { },
            boneNameUpperNeck = new string[] { }
        };
    }

    [Header("Subjects")]
    [Tooltip("Animators to track (multi-person). Each becomes one entry in the per-frame 'persons' array.")]
    public List<Animator> avatars = new List<Animator>();

    [Header("Skeleton Format")]
    [Tooltip("Select skeleton format for ViTPose datasets (AIC, COCO, MPII, CrowdPose). Missing joints will be skipped.")]
    public SkeletonFormat skeletonFormat = SkeletonFormat.AIC;

    [Header("Cameras")]
    [Tooltip("Cameras to capture. Auto-populated from descendant GameObjects on Reset and via the context menu. Editable.")]
    public List<Camera> cameras = new List<Camera>();

    [Header("Subject heuristic")]
    [Tooltip("Distance in meters along Head bone's up axis from Head origin to the top of the skull.")]
    public float headTopUpOffset = 0.14f;
    [Tooltip("Head-local offset for nose when no Nose bone is found on the avatar.")]
    public Vector3 noseHeadOffset    = new Vector3( 0f,    0.08f, 0.09f);
    [Tooltip("Head-local offset for left eye when no LeftEye bone is found.")]
    public Vector3 leftEyeHeadOffset  = new Vector3( 0f,0f,0f);
    [Tooltip("Head-local offset for right eye when no RightEye bone is found.")]
    public Vector3 rightEyeHeadOffset = new Vector3(0f,0f,0f);
    [Tooltip("Head-local offset for left ear when no LeftEar bone is found.")]
    public Vector3 leftEarHeadOffset  = new Vector3( 0.09f, 0.08f, 0f);
    [Tooltip("Head-local offset for right ear when no RightEar bone is found.")]
    public Vector3 rightEarHeadOffset = new Vector3(-0.09f, 0.08f, 0f);

    [Header("Session")]
    public string outputFolder = "Recordings";
    public string sessionName = "MultiViewSession";

    [Header("Recording")]
    public bool record = false;
    [Tooltip("Image encoding for cam_N/rgb/ and cam_N/keyrgb/. JPEG (~10x smaller) is the standard ML format; PNG is lossless.")]
    public CaptureImageFormat imageFormat = CaptureImageFormat.JPEG;
    [Tooltip("JPEG quality 1-100. 90 visually indistinguishable from PNG.")]
    [Range(1, 100)] public int jpegQuality = 90;
    [Tooltip("Capture target resolution applied to every camera's RT. Native = each camera uses its own pixelWidth/Height; Custom = use customWidth/customHeight below.")]
    public CaptureResolution captureResolution = CaptureResolution.Native;
    public int customWidth = 1920;
    public int customHeight = 1080;
    [Tooltip("Write cam_N/keyrgb/{t}.{png|jpg} with keypoint overlays. Disable for fastest captures.")]
    public bool writeKeyrgbOverlay = true;
    [Tooltip("Write cam_N/bboxes/{t}.json with one entry per visible avatar (YOLO normalized + pixel xyxy + person/avatar metadata).")]
    public bool writeYoloBboxes = true;
    [Tooltip("Debug: write cam_N/bbox_vis/{t}.png with rectangles drawn over the captured frame.")]
    public bool writeBboxVis = false;
    public int yoloClassId = 0;
    public float bboxPaddingFraction = 0.1f;
    [Tooltip("Hard cap on the encoder queue across all cameras. New captures are skipped while the queue is full.")]
    public int maxInFlightEncodes = 8;

    [Header("Image orientation")]
    [Tooltip("Flip rows before encoding PNG so the saved image is top-down.")]
    public bool flipOutputY = true;
    [Tooltip("Flip keypoint Y when projecting. Toggle if overlay appears upside-down vs the saved image on your URP build.")]
    public bool flipKeypointY = false;

    [Header("Overlay drawing")]
    public int pointRadiusPx = 4;
    public int lineThicknessPx = 2;
    public bool drawSkeleton = true;
    public Color bboxVisColor = new Color(1f, 1f, 0f, 1f);

    enum KpMode { Bone, HeadTopOffset, MidShoulder, HeadOffset }

    class AvatarRig
    {
        public Animator animator;
        public SkeletonFormat format;
        public int keypointCount;
        public Transform[] keypointTransforms;
        public KpMode[] keypointModes;
        public Vector3[] keypointLocalOffsets;  // used by KpMode.HeadOffset
        public Transform leftShoulderBone;
        public Transform rightShoulderBone;
        public bool bonesResolved;

        public AvatarRig(SkeletonFormat format, int kpCount)
        {
            this.format = format;
            this.keypointCount = kpCount;
            this.keypointTransforms = new Transform[kpCount];
            this.keypointModes = new KpMode[kpCount];
            this.keypointLocalOffsets = new Vector3[kpCount];
            this.bonesResolved = false;
        }
    }

    class CameraSlot
    {
        public Camera cam;
        public RenderTexture rt;
        public RenderTexture prevTarget;
        public string rgbDir, keyrgbDir, bboxDir, bboxVisDir;
        public int width, height;
        public int index1; // 1-based folder index (cam_1/, cam_2/, ...)
    }

    // Bone-name candidates for Generic rigs (mirrors KeypointsRecorder).
    // Note: Individual bone candidates are now stored in SkeletonFormatDef classes per format.
    
    // Optional marker GameObjects (empty children on the prefab) that pin
    // a keypoint at an exact world position, bypassing the bone+offset
    // heuristic for the head_top / neck cases.
    static readonly string[] MarkerNames_HeadTop = { "head_top", "HeadTop", "headtop", "skull_top" };
    static readonly string[] MarkerNames_Neck    = { "neck_marker", "NeckMarker" };

    readonly List<AvatarRig> rigs = new List<AvatarRig>();
    readonly List<CameraSlot> slots = new List<CameraSlot>();

    string sessionPath;
    StreamWriter jsonWriter;
    readonly object jsonLock = new object();
    bool jsonFirstFrame = true;

    int frameCounter;
    double recordingStartTime = -1.0;
    int lastCapturedUnityFrame = -1;
    bool _capturing;
    int _inFlightEncodes;
    bool acquired;

    // ---------------- Inspector helpers ----------------

    void Reset() { RefreshCamerasFromChildren(); }

    [ContextMenu("Refresh cameras from children")]
    public void RefreshCamerasFromChildren()
    {
        cameras.Clear();
        var found = GetComponentsInChildren<Camera>(true);
        foreach (var c in found)
        {
            if (c.gameObject == gameObject) continue;   // skip self if user attaches to a Camera GameObject
            cameras.Add(c);
        }
    }

    [ContextMenu("Start Recording")]
    public void StartRecordingFromMenu() { record = true; BeginRecording(); }
    [ContextMenu("Stop Recording")]
    public void StopRecordingFromMenu() { record = false; EndRecording(); }

    // ---------------- Lifecycle ----------------

    void OnEnable()  { if (record) BeginRecording(); }
    void OnDisable() { if (acquired) EndRecording(); }
    void OnApplicationQuit() { if (acquired) EndRecording(); }

    void Update()
    {
        if (!acquired && record) BeginRecording();
        else if (acquired && !record) EndRecording();

        if (!acquired) return;

        // Retry name-based bone resolve if the rig wasn't bound at begin time.
        for (int r = 0; r < rigs.Count; r++)
        {
            var rig = rigs[r];
            if (rig.bonesResolved || rig.animator == null) continue;
            ResolveRig(rig);
            rig.bonesResolved = HasAnyResolvedBone(rig);
        }

        // Back-pressure: skip new captures when the encoder queue is full.
        // Threshold scales with the number of cameras so we don't block at
        // multi-view setups where every frame enqueues N PNGs.
        int cap = Mathf.Max(1, maxInFlightEncodes) * Mathf.Max(1, slots.Count);
        if (_capturing) return;
        if (Time.frameCount == lastCapturedUnityFrame) return;
        if (Interlocked.CompareExchange(ref _inFlightEncodes, 0, 0) >= cap) return;

        StartCoroutine(CaptureRoutine());
    }

    void BeginRecording()
    {
        // Resolve cameras list — drop nulls and any FulldomeCameras (unsupported here).
        var validCams = new List<Camera>();
        for (int i = 0; i < cameras.Count; i++)
        {
            var c = cameras[i];
            if (c == null) continue;
            if (c.GetComponent<Avante.FulldomeCamera>() != null)
            {
                Debug.LogWarning($"[MultiViewRecorder] Skipping fulldome camera '{c.name}'. MultiViewRecorder only supports pinhole cameras.");
                continue;
            }
            validCams.Add(c);
        }
        if (validCams.Count == 0)
        {
            Debug.LogError("[MultiViewRecorder] No cameras to record. Add child Cameras and click 'Refresh cameras from children' in the context menu.");
            record = false;
            return;
        }

        // Build avatar rigs.
        rigs.Clear();
        int kpCount = GetKeypointCount(skeletonFormat);
        foreach (var anim in avatars)
        {
            if (anim == null) continue;
            var rig = new AvatarRig(skeletonFormat, kpCount) { animator = anim };
            ResolveRig(rig);
            rig.bonesResolved = HasAnyResolvedBone(rig);
            if (!rig.bonesResolved)
                Debug.LogWarning($"[MultiViewRecorder] No bones resolved for '{anim.name}'.");
            rigs.Add(rig);
        }
        if (rigs.Count == 0)
        {
            Debug.LogError("[MultiViewRecorder] Avatars list is empty.");
            record = false;
            return;
        }

        // Session folder.
        sessionPath = Path.Combine(Application.dataPath, "..", outputFolder,
            sessionName + "_" + skeletonFormat.ToString() + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(sessionPath);

        // Per-camera setup.
        slots.Clear();
        for (int i = 0; i < validCams.Count; i++)
        {
            var c = validCams[i];
            int idx1 = i + 1;
            CaptureSettings.Resolve(captureResolution,
                c.pixelWidth, c.pixelHeight, customWidth, customHeight,
                out int slotW, out int slotH);
            var slot = new CameraSlot
            {
                cam = c,
                index1 = idx1,
                width = slotW,
                height = slotH,
            };
            string camRoot = Path.Combine(sessionPath, $"cam_{idx1}");
            Directory.CreateDirectory(camRoot);
            slot.rgbDir = Path.Combine(camRoot, "rgb");
            Directory.CreateDirectory(slot.rgbDir);
            if (writeKeyrgbOverlay)
            {
                slot.keyrgbDir = Path.Combine(camRoot, "keyrgb");
                Directory.CreateDirectory(slot.keyrgbDir);
            }
            if (writeYoloBboxes)
            {
                slot.bboxDir = Path.Combine(camRoot, "bboxes");
                Directory.CreateDirectory(slot.bboxDir);
            }
            if (writeBboxVis)
            {
                slot.bboxVisDir = Path.Combine(camRoot, "bbox_vis");
                Directory.CreateDirectory(slot.bboxVisDir);
            }
            slot.rt = new RenderTexture(slot.width, slot.height, 24, RenderTextureFormat.ARGB32);
            slot.rt.Create();
            slot.prevTarget = c.targetTexture;
            c.targetTexture = slot.rt;
            slots.Add(slot);
        }

        OpenJsonWriter();
        WriteCamerasJson();

        frameCounter = 0;
        recordingStartTime = -1.0;
        jsonFirstFrame = true;
        lastCapturedUnityFrame = -1;
        acquired = true;
        Debug.Log($"[MultiViewRecorder] Session started at {sessionPath} with {slots.Count} cameras");
    }

    void EndRecording()
    {
        if (!acquired) return;
        acquired = false;

        // Drain in-flight encodes so JSON close + RT release happen safely.
        int waited = 0;
        while (Interlocked.CompareExchange(ref _inFlightEncodes, 0, 0) > 0 && waited < 5000)
        {
            Thread.Sleep(20);
            waited += 20;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.cam != null) slot.cam.targetTexture = slot.prevTarget;
            if (slot.rt != null) { slot.rt.Release(); Destroy(slot.rt); }
        }
        slots.Clear();

        CloseJsonWriter();
        Debug.Log($"[MultiViewRecorder] Session ended at {sessionPath} ({frameCounter} frames)");
        sessionPath = null;
    }

    // ---------------- Capture pipeline ----------------

    IEnumerator CaptureRoutine()
    {
        _capturing = true;
        yield return new WaitForEndOfFrame();
        try
        {
            if (!acquired) yield break;

            int slotCount = slots.Count;
            if (slotCount == 0) yield break;

            int idx = frameCounter++;
            if (recordingStartTime < 0.0) recordingStartTime = Time.realtimeSinceStartup;
            double relTime = Time.realtimeSinceStartup - recordingStartTime;
            string ts = relTime.ToString("F6", CultureInfo.InvariantCulture);
            lastCapturedUnityFrame = Time.frameCount;

            // Compute world-space keypoint data once (camera-agnostic).
            int personCount = rigs.Count;
            var allWorldPos = new Vector3[personCount][];
            var allWorldRot = new Quaternion[personCount][];
            var allLocalPos = new Vector3[personCount][];
            for (int p = 0; p < personCount; p++)
            {
                var rig = rigs[p];
                Transform root = rig.animator.transform;
                int kpCount = rig.keypointCount;
                var wp = new Vector3[kpCount];
                var wr = new Quaternion[kpCount];
                var lp = new Vector3[kpCount];
                for (int k = 0; k < kpCount; k++)
                {
                    wp[k] = GetKeypointWorldPosition(rig, k);
                    wr[k] = GetKeypointWorldRotation(rig, k);
                    lp[k] = root.InverseTransformPoint(wp[k]);
                }
                allWorldPos[p] = wp;
                allWorldRot[p] = wr;
                allLocalPos[p] = lp;
            }

            // Per-camera projection & per-camera-frame data.
            var imgPosByCam   = new Vector2[slotCount][][];
            var visByCam      = new bool[slotCount][][];
            var camPosCvByCam = new Vector3[slotCount][][];
            var camRotCvByCam = new Quaternion[slotCount][][];
            var imgDepthByCam = new float[slotCount][][];

            for (int c = 0; c < slotCount; c++)
            {
                var slot = slots[c];
                Camera cam = slot.cam;
                Matrix4x4 w2cGL = cam.worldToCameraMatrix;
                Quaternion invCamRot = Quaternion.Inverse(cam.transform.rotation);

                imgPosByCam[c]   = new Vector2[personCount][];
                visByCam[c]      = new bool[personCount][];
                camPosCvByCam[c] = new Vector3[personCount][];
                camRotCvByCam[c] = new Quaternion[personCount][];
                imgDepthByCam[c] = new float[personCount][];

                for (int p = 0; p < personCount; p++)
                {
                    var rig = rigs[p];
                    int kpCount = rig.keypointCount;
                    var imgPos   = new Vector2[kpCount];
                    var vis      = new bool[kpCount];
                    var camPosCv = new Vector3[kpCount];
                    var camRotCv = new Quaternion[kpCount];
                    var imgDepth = new float[kpCount];

                    for (int k = 0; k < kpCount; k++)
                    {
                        Vector3 wp = allWorldPos[p][k];
                        Vector3 pCamGL = w2cGL.MultiplyPoint3x4(wp);
                        camPosCv[k] = new Vector3(pCamGL.x, -pCamGL.y, -pCamGL.z);
                        Quaternion qBoneCamLH = invCamRot * allWorldRot[p][k];
                        camRotCv[k] = new Quaternion(qBoneCamLH.x, -qBoneCamLH.y, qBoneCamLH.z, qBoneCamLH.w);
                        ProjectPinhole(cam, slot.width, slot.height, wp,
                                       out imgPos[k], out imgDepth[k], out vis[k]);
                        // Force invisible for unresolved keypoints so null bones
                        // (e.g. COCO face kps, MPII pelvis/thorax) don't project
                        // to Vector3.zero and draw wild off-body skeleton lines.
                        if (!IsKeypointResolved(rig, k)) vis[k] = false;
                    }
                    imgPosByCam[c][p]   = imgPos;
                    visByCam[c][p]      = vis;
                    camPosCvByCam[c][p] = camPosCv;
                    camRotCvByCam[c][p] = camRotCv;
                    imgDepthByCam[c][p] = imgDepth;
                }
            }

            // Build the JSON entry now, while world transforms are current.
            string frameJson = BuildFrameJson(idx, relTime, ts,
                allWorldPos, allWorldRot, allLocalPos,
                camPosCvByCam, camRotCvByCam, imgPosByCam, imgDepthByCam, visByCam);

            // Issue one AsyncGPUReadback per camera. The last callback to
            // arrive runs the commit (writes JSON entry + per-camera files).
            // Frame is dropped only if at least one readback errors.
            int pendingReadbacks = slotCount;
            byte[][] allBytes = new byte[slotCount][];
            int[] hadError = new int[slotCount];
            bool flip = flipOutputY;

            for (int c = 0; c < slotCount; c++)
            {
                int ci = c;
                var slot = slots[ci];
                int w = slot.width, h = slot.height;

                AsyncGPUReadback.Request(slot.rt, 0, TextureFormat.RGB24, req =>
                {
                    try
                    {
                        if (req.hasError) { hadError[ci] = 1; return; }
                        byte[] bytes = req.GetData<byte>().ToArray();
                        if (flip) bytes = FlipRowsRGB24(bytes, w, h);
                        allBytes[ci] = bytes;
                    }
                    finally
                    {
                        int remaining = Interlocked.Decrement(ref pendingReadbacks);
                        if (remaining == 0)
                            CommitFrame(idx, relTime, ts, frameJson, allBytes, hadError, imgPosByCam, visByCam);
                    }
                });
            }
        }
        finally { _capturing = false; }
    }

    void CommitFrame(int frameIndex, double timestamp, string ts, string frameJson,
                     byte[][] allBytes, int[] hadError,
                     Vector2[][][] imgPosByCam, bool[][][] visByCam)
    {
        // Drop the entire frame if any camera's readback failed; that keeps
        // JSON entries and per-camera file counts aligned.
        for (int c = 0; c < slots.Count; c++)
        {
            if (hadError[c] != 0 || allBytes[c] == null)
            {
                Debug.LogWarning($"[MultiViewRecorder] Frame {ts} dropped (camera {slots[c].index1} readback failed).");
                return;
            }
        }

        lock (jsonLock)
        {
            if (jsonWriter != null)
            {
                if (!jsonFirstFrame) jsonWriter.Write(",\n");
                jsonWriter.Write(frameJson);
                jsonFirstFrame = false;
            }
        }

        for (int c = 0; c < slots.Count; c++)
        {
            var slot = slots[c];
            byte[] bytes = allBytes[c];
            int w = slot.width, h = slot.height;
            Vector2[][] allPts = imgPosByCam[c];
            bool[][]    allVis = visByCam[c];

            // Per-frame bbox JSON. Carries normalized YOLO + pixel xyxy + person
            // metadata; richer than the old single-line .txt and uses .json for
            // dataset symmetry.
            if (writeYoloBboxes && slot.bboxDir != null)
            {
                var sb = new StringBuilder(256 + allPts.Length * 256);
                sb.Append("{\n");
                sb.Append("  \"frame_index\": ").Append(frameIndex).Append(",\n");
                sb.Append("  \"timestamp\": ").Append(F(timestamp)).Append(",\n");
                sb.Append("  \"image_size\": [").Append(w).Append(", ").Append(h).Append("],\n");
                sb.Append("  \"camera_id\": \"cam_").Append(slot.index1).Append("\",\n");
                sb.Append("  \"boxes\": [");
                int written = 0;
                for (int p = 0; p < allPts.Length; p++)
                {
                    if (!TryComputePersonBbox(allPts[p], allVis[p], w, h, bboxPaddingFraction,
                                              out float xc, out float yc, out float bw, out float bh))
                        continue;
                    float x1 = (xc - 0.5f * bw) * w;
                    float y1 = (yc - 0.5f * bh) * h;
                    float x2 = (xc + 0.5f * bw) * w;
                    float y2 = (yc + 0.5f * bh) * h;
                    sb.Append(written == 0 ? "\n    " : ",\n    ");
                    sb.Append("{ \"class_id\": ").Append(yoloClassId);
                    sb.Append(", \"class_name\": \"person\"");
                    sb.Append(", \"person_index\": ").Append(p);
                    if (p < rigs.Count && rigs[p].animator != null)
                        sb.Append(", \"avatar_name\": \"").Append(rigs[p].animator.name).Append("\"");
                    sb.Append(", \"yolo\": [").Append(F(xc)).Append(", ").Append(F(yc))
                      .Append(", ").Append(F(bw)).Append(", ").Append(F(bh)).Append("]");
                    sb.Append(", \"xyxy_px\": [").Append(F(x1)).Append(", ").Append(F(y1))
                      .Append(", ").Append(F(x2)).Append(", ").Append(F(y2)).Append("]");
                    sb.Append(" }");
                    written++;
                }
                sb.Append(written > 0 ? "\n  ]\n}\n" : "]\n}\n");
                File.WriteAllText(Path.Combine(slot.bboxDir, ts + ".json"), sb.ToString());
            }

            // RGB encode.
            string rgbExt = CaptureSettings.Extension(imageFormat);
            string rgbPath = Path.Combine(slot.rgbDir, ts + rgbExt);
            byte[] rgbBuf = bytes;
            int wRgb = w, hRgb = h;
            var fmt = imageFormat;
            var q = jpegQuality;
            Interlocked.Increment(ref _inFlightEncodes);
            Task.Run(() =>
            {
                try
                {
                    byte[] enc = (fmt == CaptureImageFormat.JPEG)
                        ? ImageConversion.EncodeArrayToJPG(rgbBuf, GraphicsFormat.R8G8B8_UNorm, (uint)wRgb, (uint)hRgb, 0, q)
                        : ImageConversion.EncodeArrayToPNG(rgbBuf, GraphicsFormat.R8G8B8_UNorm, (uint)wRgb, (uint)hRgb);
                    File.WriteAllBytes(rgbPath, enc);
                }
                catch (Exception e) { Debug.LogError("[MultiViewRecorder] rgb: " + e); }
                finally { Interlocked.Decrement(ref _inFlightEncodes); }
            });

            // keyrgb (overlay): clone before drawing so we don't corrupt rgb buffer.
            if (writeKeyrgbOverlay && slot.keyrgbDir != null)
            {
                int pointR = pointRadiusPx;
                int lineT = lineThicknessPx;
                bool drawSkel = drawSkeleton;
                SkeletonFormatDef skeletonDef = GetSkeletonFormat(skeletonFormat);
                Color32[] colors = GetKeypointColors(skeletonFormat, GetKeypointCount(skeletonFormat));
                byte[] krBuf = (byte[])bytes.Clone();
                string krPath = Path.Combine(slot.keyrgbDir, ts + rgbExt);
                int wK = w, hK = h;
                var krFmt = imageFormat;
                var krQ = jpegQuality;
                Interlocked.Increment(ref _inFlightEncodes);
                Task.Run(() =>
                {
                    try
                    {
                        for (int p = 0; p < allPts.Length; p++)
                            DrawOverlay(krBuf, wK, hK, allPts[p], allVis[p], pointR, lineT, drawSkel, skeletonDef, colors);
                        byte[] enc = (krFmt == CaptureImageFormat.JPEG)
                            ? ImageConversion.EncodeArrayToJPG(krBuf, GraphicsFormat.R8G8B8_UNorm, (uint)wK, (uint)hK, 0, krQ)
                            : ImageConversion.EncodeArrayToPNG(krBuf, GraphicsFormat.R8G8B8_UNorm, (uint)wK, (uint)hK);
                        File.WriteAllBytes(krPath, enc);
                    }
                    catch (Exception e) { Debug.LogError("[MultiViewRecorder] keyrgb: " + e); }
                    finally { Interlocked.Decrement(ref _inFlightEncodes); }
                });
            }

            // bbox_vis (debug rectangles).
            if (writeBboxVis && slot.bboxVisDir != null)
            {
                Color32 bc = bboxVisColor;
                int lt = Mathf.Max(1, lineThicknessPx);
                float pad = bboxPaddingFraction;
                byte[] bvBuf = (byte[])bytes.Clone();
                string bvPath = Path.Combine(slot.bboxVisDir, ts + ".png");
                int wB = w, hB = h;
                Interlocked.Increment(ref _inFlightEncodes);
                Task.Run(() =>
                {
                    try
                    {
                        for (int p = 0; p < allPts.Length; p++)
                        {
                            if (TryComputePersonBboxPx(allPts[p], allVis[p], wB, hB, pad,
                                                       out int xmin, out int ymin, out int xmax, out int ymax))
                                DrawRect(bvBuf, wB, hB, xmin, ymin, xmax, ymax, lt, bc);
                        }
                        var png = ImageConversion.EncodeArrayToPNG(bvBuf, GraphicsFormat.R8G8B8_UNorm, (uint)wB, (uint)hB);
                        File.WriteAllBytes(bvPath, png);
                    }
                    catch (Exception e) { Debug.LogError("[MultiViewRecorder] bbox_vis: " + e); }
                    finally { Interlocked.Decrement(ref _inFlightEncodes); }
                });
            }
        }
    }

    // ---------------- Bone resolve & projection ----------------

    void ResolveRig(AvatarRig rig)
    {
        Animator a = rig.animator;
        bool isHumanoid = a.isHuman;
        SkeletonFormatDef format = GetSkeletonFormat(rig.format);

        rig.leftShoulderBone  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftUpperArm,  format.boneNameLeftUpperArm);
        rig.rightShoulderBone = ResolveBone(a, isHumanoid, HumanBodyBones.RightUpperArm, format.boneNameRightUpperArm);
        Transform head = ResolveBone(a, isHumanoid, HumanBodyBones.Head, format.boneNameHead);
        Transform neck = ResolveBone(a, isHumanoid, HumanBodyBones.Neck, format.boneNameNeck);

        // Resolve keypoints based on skeleton format
        switch (rig.format)
        {
            case SkeletonFormat.AIC:
                ResolveAICRig(a, isHumanoid, rig, format, head, neck);
                break;
            case SkeletonFormat.COCO:
                ResolveCOCORig(a, isHumanoid, rig, format, head, neck);
                break;
            case SkeletonFormat.MPII:
                ResolveMPIIRig(a, isHumanoid, rig, format, head, neck);
                break;
            case SkeletonFormat.CrowdPose:
                ResolveCrowdPoseRig(a, isHumanoid, rig, format, head, neck);
                break;
        }

        rig.bonesResolved = true;
    }

    void ResolveAICRig(Animator a, bool isHumanoid, AvatarRig rig, SkeletonFormatDef format, Transform head, Transform neck)
    {
        rig.keypointTransforms[0]  = rig.rightShoulderBone;
        rig.keypointTransforms[1]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightLowerArm, format.boneNameRightLowerArm);
        rig.keypointTransforms[2]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightHand,     format.boneNameRightHand);
        rig.keypointTransforms[3]  = rig.leftShoulderBone;
        rig.keypointTransforms[4]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftLowerArm,  format.boneNameLeftLowerArm);
        rig.keypointTransforms[5]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftHand,      format.boneNameLeftHand);
        rig.keypointTransforms[6]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightUpperLeg, format.boneNameRightUpperLeg);
        rig.keypointTransforms[7]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightLowerLeg, format.boneNameRightLowerLeg);
        rig.keypointTransforms[8]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightFoot,     format.boneNameRightFoot);
        rig.keypointTransforms[9]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftUpperLeg,  format.boneNameLeftUpperLeg);
        rig.keypointTransforms[10] = ResolveBone(a, isHumanoid, HumanBodyBones.LeftLowerLeg,  format.boneNameLeftLowerLeg);
        rig.keypointTransforms[11] = ResolveBone(a, isHumanoid, HumanBodyBones.LeftFoot,      format.boneNameLeftFoot);

        if (head != null) { rig.keypointTransforms[12] = head; rig.keypointModes[12] = KpMode.HeadTopOffset; }
        else              { rig.keypointTransforms[12] = null; rig.keypointModes[12] = KpMode.Bone; }

        if (neck != null) { rig.keypointTransforms[13] = neck; rig.keypointModes[13] = KpMode.Bone; }
        else              { rig.keypointTransforms[13] = null; rig.keypointModes[13] = KpMode.MidShoulder; }

        ApplyMarkerOverrides(rig, format);
    }

    void ResolveCOCORig(Animator a, bool isHumanoid, AvatarRig rig, SkeletonFormatDef format, Transform head, Transform neck)
    {
        // 0: nose — no HumanBodyBones equivalent; offset from Head as fallback
        Transform noseBone = FindBoneByName(a.transform, format.boneNameNose);
        if (noseBone != null) { rig.keypointTransforms[0] = noseBone; }
        else if (head != null) { rig.keypointTransforms[0] = head; rig.keypointModes[0] = KpMode.HeadOffset; rig.keypointLocalOffsets[0] = noseHeadOffset; }

        // 1: left_eye — HumanBodyBones.LeftEye on humanoid rigs; offset fallback
        Transform leftEyeBone = ResolveBone(a, isHumanoid, HumanBodyBones.LeftEye, format.boneNameLeftEye);
        if (leftEyeBone != null) { rig.keypointTransforms[1] = leftEyeBone; }
        else if (head != null) { rig.keypointTransforms[1] = head; rig.keypointModes[1] = KpMode.HeadOffset; rig.keypointLocalOffsets[1] = leftEyeHeadOffset; }

        // 2: right_eye — HumanBodyBones.RightEye on humanoid rigs; offset fallback
        Transform rightEyeBone = ResolveBone(a, isHumanoid, HumanBodyBones.RightEye, format.boneNameRightEye);
        if (rightEyeBone != null) { rig.keypointTransforms[2] = rightEyeBone; }
        else if (head != null) { rig.keypointTransforms[2] = head; rig.keypointModes[2] = KpMode.HeadOffset; rig.keypointLocalOffsets[2] = rightEyeHeadOffset; }

        // 3: left_ear — no HumanBodyBones equivalent; offset from Head as fallback
        Transform leftEarBone = FindBoneByName(a.transform, format.boneNameLeftEar);
        if (leftEarBone != null) { rig.keypointTransforms[3] = leftEarBone; }
        else if (head != null) { rig.keypointTransforms[3] = head; rig.keypointModes[3] = KpMode.HeadOffset; rig.keypointLocalOffsets[3] = leftEarHeadOffset; }

        // 4: right_ear — no HumanBodyBones equivalent; offset from Head as fallback
        Transform rightEarBone = FindBoneByName(a.transform, format.boneNameRightEar);
        if (rightEarBone != null) { rig.keypointTransforms[4] = rightEarBone; }
        else if (head != null) { rig.keypointTransforms[4] = head; rig.keypointModes[4] = KpMode.HeadOffset; rig.keypointLocalOffsets[4] = rightEarHeadOffset; }

        // 5: left_shoulder, 6: right_shoulder
        rig.keypointTransforms[5]  = rig.leftShoulderBone;
        rig.keypointTransforms[6]  = rig.rightShoulderBone;
        // 7: left_elbow, 8: right_elbow
        rig.keypointTransforms[7]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftLowerArm,  format.boneNameLeftLowerArm);
        rig.keypointTransforms[8]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightLowerArm, format.boneNameRightLowerArm);
        // 9: left_wrist, 10: right_wrist
        rig.keypointTransforms[9]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftHand,      format.boneNameLeftHand);
        rig.keypointTransforms[10] = ResolveBone(a, isHumanoid, HumanBodyBones.RightHand,     format.boneNameRightHand);
        // 11: left_hip, 12: right_hip
        rig.keypointTransforms[11] = ResolveBone(a, isHumanoid, HumanBodyBones.LeftUpperLeg,  format.boneNameLeftUpperLeg);
        rig.keypointTransforms[12] = ResolveBone(a, isHumanoid, HumanBodyBones.RightUpperLeg, format.boneNameRightUpperLeg);
        // 13: left_knee, 14: right_knee
        rig.keypointTransforms[13] = ResolveBone(a, isHumanoid, HumanBodyBones.LeftLowerLeg,  format.boneNameLeftLowerLeg);
        rig.keypointTransforms[14] = ResolveBone(a, isHumanoid, HumanBodyBones.RightLowerLeg, format.boneNameRightLowerLeg);
        // 15: left_ankle, 16: right_ankle
        rig.keypointTransforms[15] = ResolveBone(a, isHumanoid, HumanBodyBones.LeftFoot,      format.boneNameLeftFoot);
        rig.keypointTransforms[16] = ResolveBone(a, isHumanoid, HumanBodyBones.RightFoot,     format.boneNameRightFoot);

        ApplyMarkerOverrides(rig, format);
    }

    void ResolveMPIIRig(Animator a, bool isHumanoid, AvatarRig rig, SkeletonFormatDef format, Transform head, Transform neck)
    {
        // 0: right_ankle, 1: right_knee, 2: right_hip
        rig.keypointTransforms[0]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightFoot,     format.boneNameRightFoot);
        rig.keypointTransforms[1]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightLowerLeg, format.boneNameRightLowerLeg);
        rig.keypointTransforms[2]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightUpperLeg, format.boneNameRightUpperLeg);
        // 3: left_hip, 4: left_knee, 5: left_ankle
        rig.keypointTransforms[3]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftUpperLeg,  format.boneNameLeftUpperLeg);
        rig.keypointTransforms[4]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftLowerLeg,  format.boneNameLeftLowerLeg);
        rig.keypointTransforms[5]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftFoot,      format.boneNameLeftFoot);
        // 6: pelvis, 7: thorax, 8: upper_neck, 9: head
        rig.keypointTransforms[6]  = ResolveBone(a, isHumanoid, HumanBodyBones.Hips, format.boneNamePelvis);
        rig.keypointTransforms[7]  = ResolveBone(a, isHumanoid, HumanBodyBones.Spine, format.boneNameThorax);
        rig.keypointTransforms[8]  = ResolveBone(a, isHumanoid, HumanBodyBones.Neck, format.boneNameUpperNeck);
        rig.keypointTransforms[9]  = head;
        // 10: right_wrist, 11: right_elbow, 12: right_shoulder
        rig.keypointTransforms[10] = ResolveBone(a, isHumanoid, HumanBodyBones.RightHand,     format.boneNameRightHand);
        rig.keypointTransforms[11] = ResolveBone(a, isHumanoid, HumanBodyBones.RightLowerArm, format.boneNameRightLowerArm);
        rig.keypointTransforms[12] = rig.rightShoulderBone;
        // 13: left_shoulder, 14: left_elbow, 15: left_wrist
        rig.keypointTransforms[13] = rig.leftShoulderBone;
        rig.keypointTransforms[14] = ResolveBone(a, isHumanoid, HumanBodyBones.LeftLowerArm,  format.boneNameLeftLowerArm);
        rig.keypointTransforms[15] = ResolveBone(a, isHumanoid, HumanBodyBones.LeftHand,      format.boneNameLeftHand);

        ApplyMarkerOverrides(rig, format);
    }

    void ResolveCrowdPoseRig(Animator a, bool isHumanoid, AvatarRig rig, SkeletonFormatDef format, Transform head, Transform neck)
    {
        // 0: left_shoulder, 1: right_shoulder
        rig.keypointTransforms[0]  = rig.leftShoulderBone;
        rig.keypointTransforms[1]  = rig.rightShoulderBone;
        // 2: left_elbow, 3: right_elbow
        rig.keypointTransforms[2]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftLowerArm,  format.boneNameLeftLowerArm);
        rig.keypointTransforms[3]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightLowerArm, format.boneNameRightLowerArm);
        // 4: left_wrist, 5: right_wrist
        rig.keypointTransforms[4]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftHand,      format.boneNameLeftHand);
        rig.keypointTransforms[5]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightHand,     format.boneNameRightHand);
        // 6: left_hip, 7: right_hip
        rig.keypointTransforms[6]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftUpperLeg,  format.boneNameLeftUpperLeg);
        rig.keypointTransforms[7]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightUpperLeg, format.boneNameRightUpperLeg);
        // 8: left_knee, 9: right_knee
        rig.keypointTransforms[8]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftLowerLeg,  format.boneNameLeftLowerLeg);
        rig.keypointTransforms[9]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightLowerLeg, format.boneNameRightLowerLeg);
        // 10: left_ankle, 11: right_ankle
        rig.keypointTransforms[10] = ResolveBone(a, isHumanoid, HumanBodyBones.LeftFoot,      format.boneNameLeftFoot);
        rig.keypointTransforms[11] = ResolveBone(a, isHumanoid, HumanBodyBones.RightFoot,     format.boneNameRightFoot);
        // 12: top_head, 13: neck
        if (head != null) { rig.keypointTransforms[12] = head; rig.keypointModes[12] = KpMode.HeadTopOffset; }
        else              { rig.keypointTransforms[12] = null; rig.keypointModes[12] = KpMode.Bone; }

        if (neck != null) { rig.keypointTransforms[13] = neck; rig.keypointModes[13] = KpMode.Bone; }
        else              { rig.keypointTransforms[13] = null; rig.keypointModes[13] = KpMode.MidShoulder; }

        ApplyMarkerOverrides(rig, format);
    }

    void ApplyMarkerOverrides(AvatarRig rig, SkeletonFormatDef format)
    {
        // Marker overrides — empty GameObjects added on the prefab at the
        // exact world position. If found, they win over the heuristic.
        Transform headTopMarker = FindBoneByName(rig.animator.transform, MarkerNames_HeadTop);
        if (headTopMarker != null)
        {
            // Find head_top keypoint index based on format
            int headTopIdx = rig.format == SkeletonFormat.AIC ? 12 : 
                             rig.format == SkeletonFormat.CrowdPose ? 12 : -1;
            if (headTopIdx >= 0 && headTopIdx < rig.keypointCount)
            {
                rig.keypointTransforms[headTopIdx] = headTopMarker;
                rig.keypointModes[headTopIdx] = KpMode.Bone;
            }
        }

        Transform neckMarker = FindBoneByName(rig.animator.transform, MarkerNames_Neck);
        if (neckMarker != null)
        {
            // Find neck keypoint index based on format
            int neckIdx = rig.format == SkeletonFormat.AIC ? 13 :
                          rig.format == SkeletonFormat.CrowdPose ? 13 : -1;
            if (neckIdx >= 0 && neckIdx < rig.keypointCount)
            {
                rig.keypointTransforms[neckIdx] = neckMarker;
                rig.keypointModes[neckIdx] = KpMode.Bone;
            }
        }
    }

    static Transform ResolveBone(Animator anim, bool isHumanoid, HumanBodyBones bone, string[] nameCandidates)
    {
        Transform t = FindBoneByName(anim.transform, nameCandidates);
        if (t != null) return t;
        if (isHumanoid && anim.avatar != null)
        {
            try { return anim.GetBoneTransform(bone); }
            catch (System.InvalidOperationException) { /* no avatar bound */ }
        }
        return null;
    }

    static Transform FindBoneByName(Transform root, string[] candidates)
    {
        for (int i = 0; i < candidates.Length; i++)
        {
            Transform found = FindBoneRecursive(root, candidates[i]);
            if (found != null) return found;
        }
        return null;
    }

    static Transform FindBoneRecursive(Transform node, string name)
    {
        if (node.name == name || (node.name.Length > name.Length && node.name.EndsWith(":" + name)))
            return node;
        for (int i = 0; i < node.childCount; i++)
        {
            Transform result = FindBoneRecursive(node.GetChild(i), name);
            if (result != null) return result;
        }
        return null;
    }

    bool IsKeypointResolved(AvatarRig rig, int i)
    {
        switch (rig.keypointModes[i])
        {
            case KpMode.HeadTopOffset: return rig.keypointTransforms[i] != null;
            case KpMode.HeadOffset:    return rig.keypointTransforms[i] != null;
            case KpMode.MidShoulder:   return rig.leftShoulderBone != null && rig.rightShoulderBone != null;
            default:                   return rig.keypointTransforms[i] != null;
        }
    }

    static bool HasAnyResolvedBone(AvatarRig rig)
    {
        for (int i = 0; i < rig.keypointCount; i++)
            if (rig.keypointTransforms[i] != null) return true;
        return false;
    }

    Vector3 GetKeypointWorldPosition(AvatarRig rig, int i)
    {
        switch (rig.keypointModes[i])
        {
            case KpMode.HeadTopOffset:
            {
                Transform t = rig.keypointTransforms[i];
                return t != null ? t.position + t.up * headTopUpOffset : Vector3.zero;
            }
            case KpMode.HeadOffset:
            {
                Transform t = rig.keypointTransforms[i];
                return t != null ? t.TransformPoint(rig.keypointLocalOffsets[i]) : Vector3.zero;
            }
            case KpMode.MidShoulder:
            {
                if (rig.leftShoulderBone == null || rig.rightShoulderBone == null) return Vector3.zero;
                return 0.5f * (rig.leftShoulderBone.position + rig.rightShoulderBone.position);
            }
            default:
            {
                Transform t = rig.keypointTransforms[i];
                return t != null ? t.position : Vector3.zero;
            }
        }
    }

    Quaternion GetKeypointWorldRotation(AvatarRig rig, int i)
    {
        Transform t = rig.keypointTransforms[i];
        if (t != null) return t.rotation;
        return rig.animator != null ? rig.animator.transform.rotation : Quaternion.identity;
    }

    void ProjectPinhole(Camera cam, int imgW, int imgH, Vector3 worldPos,
                        out Vector2 pixel, out float depth, out bool visible)
    {
        Vector3 sp = cam.WorldToScreenPoint(worldPos);
        depth = sp.z;
        float u = sp.x;
        float v = flipKeypointY ? sp.y : (imgH - 1) - sp.y;
        pixel = new Vector2(u, v);
        visible = sp.z > cam.nearClipPlane && u >= 0f && u < imgW && v >= 0f && v < imgH;
    }

    // ---------------- Bbox helpers ----------------

    bool TryComputePersonBbox(Vector2[] pts, bool[] vis, int imgW, int imgH,
                              float padFraction,
                              out float xCenter, out float yCenter, out float w, out float h)
    {
        xCenter = yCenter = w = h = 0f;
        // YOLO is always top-left origin. If image_position is stored in
        // bottom-left (flipKeypointY=true), convert each y here so the
        // exported normalized bbox matches the cv2/Ultralytics expectation.
        float xmin = float.PositiveInfinity, ymin = float.PositiveInfinity;
        float xmax = float.NegativeInfinity, ymax = float.NegativeInfinity;
        int count = 0;
        for (int i = 0; i < pts.Length; i++)
        {
            if (!vis[i]) continue;
            count++;
            float yTL = flipKeypointY ? (imgH - 1f - pts[i].y) : pts[i].y;
            if (pts[i].x < xmin) xmin = pts[i].x;
            if (yTL < ymin) ymin = yTL;
            if (pts[i].x > xmax) xmax = pts[i].x;
            if (yTL > ymax) ymax = yTL;
        }
        if (count < 2) return false;
        float padW = (xmax - xmin) * padFraction;
        float padH = (ymax - ymin) * padFraction;
        xmin = Mathf.Clamp(xmin - padW, 0f, imgW - 1f);
        ymin = Mathf.Clamp(ymin - padH, 0f, imgH - 1f);
        xmax = Mathf.Clamp(xmax + padW, 0f, imgW - 1f);
        ymax = Mathf.Clamp(ymax + padH, 0f, imgH - 1f);
        float bw = xmax - xmin;
        float bh = ymax - ymin;
        if (bw <= 0f || bh <= 0f) return false;
        xCenter = (xmin + 0.5f * bw) / imgW;
        yCenter = (ymin + 0.5f * bh) / imgH;
        w = bw / imgW;
        h = bh / imgH;
        return true;
    }

    static bool TryComputePersonBboxPx(Vector2[] pts, bool[] vis, int imgW, int imgH,
                                       float padFraction,
                                       out int xmin, out int ymin, out int xmax, out int ymax)
    {
        xmin = ymin = xmax = ymax = 0;
        float fxmin = float.PositiveInfinity, fymin = float.PositiveInfinity;
        float fxmax = float.NegativeInfinity, fymax = float.NegativeInfinity;
        int count = 0;
        for (int i = 0; i < pts.Length; i++)
        {
            if (!vis[i]) continue;
            count++;
            if (pts[i].x < fxmin) fxmin = pts[i].x;
            if (pts[i].y < fymin) fymin = pts[i].y;
            if (pts[i].x > fxmax) fxmax = pts[i].x;
            if (pts[i].y > fymax) fymax = pts[i].y;
        }
        if (count < 2) return false;
        float padW = (fxmax - fxmin) * padFraction;
        float padH = (fymax - fymin) * padFraction;
        fxmin = Mathf.Clamp(fxmin - padW, 0f, imgW - 1f);
        fymin = Mathf.Clamp(fymin - padH, 0f, imgH - 1f);
        fxmax = Mathf.Clamp(fxmax + padW, 0f, imgW - 1f);
        fymax = Mathf.Clamp(fymax + padH, 0f, imgH - 1f);
        if (fxmax - fxmin <= 0f || fymax - fymin <= 0f) return false;
        xmin = Mathf.RoundToInt(fxmin); ymin = Mathf.RoundToInt(fymin);
        xmax = Mathf.RoundToInt(fxmax); ymax = Mathf.RoundToInt(fymax);
        return true;
    }

    // ---------------- Image utilities ----------------

    static byte[] FlipRowsRGB24(byte[] src, int w, int h)
    {
        int stride = w * 3;
        byte[] dst = new byte[src.Length];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(src, (h - 1 - y) * stride, dst, y * stride, stride);
        return dst;
    }

    static Color32[] GetKeypointColors(SkeletonFormat format, int keypointCount)
    {
        // Return a color array for visualization
        var colors = new Color32[keypointCount];
        
        // Right side (orange), Left side (green), Head (blue)
        for (int i = 0; i < keypointCount; i++)
        {
            // Default: orange for most points
            colors[i] = new Color32(255, 128, 0, 255);
        }

        // Format-specific color assignments
        switch (format)
        {
            case SkeletonFormat.AIC:
                // Right shoulder-elbow-wrist (0-2): orange
                // Left shoulder-elbow-wrist (3-5): green
                // Right hip-knee-ankle (6-8): orange
                // Left hip-knee-ankle (9-11): green
                // Head top (12): blue, Neck (13): blue
                for (int i = 3; i <= 5; i++) colors[i] = new Color32(0, 255, 0, 255);
                for (int i = 9; i <= 11; i++) colors[i] = new Color32(0, 255, 0, 255);
                colors[12] = new Color32(51, 153, 255, 255);
                colors[13] = new Color32(51, 153, 255, 255);
                break;

            case SkeletonFormat.COCO:
                // nose (0): blue, eyes & ears (1-4): blue
                // Right shoulder-elbow-wrist (6,8,10): orange
                // Left shoulder-elbow-wrist (5,7,9): green
                // Right hip-knee-ankle (12,14,16): orange
                // Left hip-knee-ankle (11,13,15): green
                for (int i = 0; i <= 4; i++) colors[i] = new Color32(51, 153, 255, 255);
                for (int i = 5; i <= 7; i++) colors[i] = i % 2 == 1 ? new Color32(0, 255, 0, 255) : new Color32(255, 128, 0, 255);
                for (int i = 8; i <= 10; i++) colors[i] = new Color32(255, 128, 0, 255);
                for (int i = 11; i <= 16; i++) colors[i] = i % 2 == 1 ? new Color32(0, 255, 0, 255) : new Color32(255, 128, 0, 255);
                break;

            case SkeletonFormat.MPII:
                // Right leg (0-2): orange, Left leg (3-5): green
                // Pelvis (6): yellow, Thorax (7): yellow, Upper_neck (8): blue, Head (9): blue
                // Right arm (10-12): orange, Left arm (13-15): green
                for (int i = 3; i <= 5; i++) colors[i] = new Color32(0, 255, 0, 255);
                colors[6] = new Color32(255, 255, 0, 255);
                colors[7] = new Color32(255, 255, 0, 255);
                colors[8] = new Color32(51, 153, 255, 255);
                colors[9] = new Color32(51, 153, 255, 255);
                for (int i = 13; i <= 15; i++) colors[i] = new Color32(0, 255, 0, 255);
                break;

            case SkeletonFormat.CrowdPose:
                // Left shoulder-elbow-wrist (0,2,4): green
                // Right shoulder-elbow-wrist (1,3,5): orange
                // Left hip-knee-ankle (6,8,10): green
                // Right hip-knee-ankle (7,9,11): orange
                // Head (12): blue, Neck (13): blue
                for (int i = 0; i <= 5; i++) colors[i] = i % 2 == 0 ? new Color32(0, 255, 0, 255) : new Color32(255, 128, 0, 255);
                for (int i = 6; i <= 11; i++) colors[i] = i % 2 == 0 ? new Color32(0, 255, 0, 255) : new Color32(255, 128, 0, 255);
                colors[12] = new Color32(51, 153, 255, 255);
                colors[13] = new Color32(51, 153, 255, 255);
                break;
        }

        return colors;
    }

    static void DrawOverlay(byte[] buf, int w, int h, Vector2[] pts, bool[] vis,
                            int pointR, int lineT, bool drawSkel,
                            SkeletonFormatDef format, Color32[] colors)
    {
        if (drawSkel && format.skeleton != null)
        {
            for (int s = 0; s < format.skeleton.GetLength(0); s++)
            {
                int a = format.skeleton[s, 0];
                int b = format.skeleton[s, 1];
                if (a < 0 || a >= vis.Length || b < 0 || b >= vis.Length) continue;
                if (!vis[a] || !vis[b]) continue;
                Color32 ca = colors[a], cb = colors[b];
                Color32 cc = new Color32((byte)((ca.r + cb.r) / 2), (byte)((ca.g + cb.g) / 2), (byte)((ca.b + cb.b) / 2), 255);
                DrawLine(buf, w, h, Mathf.RoundToInt(pts[a].x), Mathf.RoundToInt(pts[a].y),
                                    Mathf.RoundToInt(pts[b].x), Mathf.RoundToInt(pts[b].y), lineT, cc);
            }
        }
        for (int i = 0; i < pts.Length; i++)
        {
            if (!vis[i] || i >= colors.Length) continue;
            DrawFilledCircle(buf, w, h, Mathf.RoundToInt(pts[i].x), Mathf.RoundToInt(pts[i].y), pointR, colors[i]);
        }
    }

    static void SetPixel(byte[] buf, int w, int h, int x, int y, Color32 c)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        int i = (y * w + x) * 3;
        buf[i] = c.r; buf[i + 1] = c.g; buf[i + 2] = c.b;
    }

    static void DrawFilledCircle(byte[] buf, int w, int h, int cx, int cy, int r, Color32 c)
    {
        int r2 = r * r;
        for (int y = -r; y <= r; y++)
        for (int x = -r; x <= r; x++)
            if (x * x + y * y <= r2) SetPixel(buf, w, h, cx + x, cy + y, c);
    }

    static void DrawLine(byte[] buf, int w, int h, int x0, int y0, int x1, int y1, int thickness, Color32 c)
    {
        int dx = Mathf.Abs(x1 - x0);
        int dy = -Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int t = Mathf.Max(0, (thickness - 1) / 2);
        while (true)
        {
            for (int oy = -t; oy <= t; oy++)
            for (int ox = -t; ox <= t; ox++)
                SetPixel(buf, w, h, x0 + ox, y0 + oy, c);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    static void DrawRect(byte[] buf, int w, int h, int xmin, int ymin, int xmax, int ymax,
                         int thickness, Color32 c)
    {
        DrawLine(buf, w, h, xmin, ymin, xmax, ymin, thickness, c);
        DrawLine(buf, w, h, xmin, ymax, xmax, ymax, thickness, c);
        DrawLine(buf, w, h, xmin, ymin, xmin, ymax, thickness, c);
        DrawLine(buf, w, h, xmax, ymin, xmax, ymax, thickness, c);
    }

    // ---------------- JSON ----------------

    string BuildFrameJson(int idx, double relTime, string ts,
        Vector3[][] worldPos, Quaternion[][] worldRot, Vector3[][] localPos,
        Vector3[][][] camPosCv, Quaternion[][][] camRotCv,
        Vector2[][][] imagePos, float[][][] imageDepth, bool[][][] visible)
    {
        int slotCount = slots.Count;
        int personCount = worldPos.Length;
        
        // Get format from the first rig
        SkeletonFormatDef format = personCount > 0 && rigs.Count > 0 ? 
            GetSkeletonFormat(rigs[0].format) : GetSkeletonFormat(skeletonFormat);
        int kpCount = personCount > 0 && rigs.Count > 0 ? rigs[0].keypointCount : GetKeypointCount(skeletonFormat);
        
        var sb = new StringBuilder(8192 + slotCount * personCount * 1024);

        sb.Append("  {");
        sb.Append("\n    \"frame_index\": ").Append(idx).Append(",");
        sb.Append("\n    \"timestamp\": ").Append(F(relTime)).Append(",");
        // rgb_paths / keyrgb_paths as { "cam_N": "cam_N/rgb/<t>.<ext>" } dicts —
        // safer than positional arrays if a camera is dropped or reordered later.
        string ext = CaptureSettings.Extension(imageFormat);
        sb.Append("\n    \"rgb_paths\": {");
        for (int c = 0; c < slotCount; c++)
        {
            if (c > 0) sb.Append(",");
            sb.Append("\n      \"cam_").Append(slots[c].index1).Append("\": ");
            sb.Append("\"cam_").Append(slots[c].index1).Append("/rgb/").Append(ts).Append(ext).Append("\"");
        }
        sb.Append(slotCount > 0 ? "\n    }," : "},");
        if (writeKeyrgbOverlay)
        {
            sb.Append("\n    \"keyrgb_paths\": {");
            for (int c = 0; c < slotCount; c++)
            {
                if (c > 0) sb.Append(",");
                sb.Append("\n      \"cam_").Append(slots[c].index1).Append("\": ");
                sb.Append("\"cam_").Append(slots[c].index1).Append("/keyrgb/").Append(ts).Append(ext).Append("\"");
            }
            sb.Append(slotCount > 0 ? "\n    }," : "},");
        }
        if (writeYoloBboxes)
        {
            sb.Append("\n    \"bbox_paths\": {");
            for (int c = 0; c < slotCount; c++)
            {
                if (c > 0) sb.Append(",");
                sb.Append("\n      \"cam_").Append(slots[c].index1).Append("\": ");
                sb.Append("\"cam_").Append(slots[c].index1).Append("/bboxes/").Append(ts).Append(".json\"");
            }
            sb.Append(slotCount > 0 ? "\n    }," : "},");
        }
        sb.Append("\n    \"persons\": [");

        for (int p = 0; p < personCount; p++)
        {
            sb.Append(p == 0 ? "\n      " : ",\n      ");
            sb.Append("{");
            sb.Append("\n        \"person_index\": ").Append(p).Append(",");
            sb.Append("\n        \"avatar_name\": \"").Append(rigs[p].animator.name).Append("\",");
            sb.Append("\n        \"keypoints\": [");
            for (int k = 0; k < kpCount; k++)
            {
                sb.Append(k == 0 ? "\n          " : ",\n          ");
                sb.Append("{ \"id\": ").Append(k);
                sb.Append(", \"name\": \"").Append(format.keypointNames[k]).Append("\"");
                sb.Append(", \"position_in_world\": ").Append(V3(worldPos[p][k]));
                sb.Append(", \"rotation_in_world\": ").Append(Q(worldRot[p][k]));
                sb.Append(", \"position_in_local\": ").Append(V3(localPos[p][k]));
                sb.Append(", \"per_camera\": [");
                for (int c = 0; c < slotCount; c++)
                {
                    if (c > 0) sb.Append(", ");
                    sb.Append("{ \"camera_index\": ").Append(slots[c].index1);
                    sb.Append(", \"position_in_camera_opencv\": ").Append(V3(camPosCv[c][p][k]));
                    sb.Append(", \"rotation_in_camera_opencv\": ").Append(Q(camRotCv[c][p][k]));
                    sb.Append(", \"image_position\": [").Append(F(imagePos[c][p][k].x)).Append(",").Append(F(imagePos[c][p][k].y)).Append("]");
                    sb.Append(", \"image_depth\": ").Append(F(imageDepth[c][p][k]));
                    sb.Append(", \"visible\": ").Append(visible[c][p][k] ? "true" : "false");
                    sb.Append(" }");
                }
                sb.Append("] }");
            }
            sb.Append("\n        ]");
            sb.Append("\n      }");
        }

        sb.Append("\n    ]");
        sb.Append("\n  }");
        return sb.ToString();
    }

    void WriteCamerasJson()
    {
        if (string.IsNullOrEmpty(sessionPath)) return;
        var sb = new StringBuilder(1024 + slots.Count * 256);
        sb.Append("{\n");
        sb.Append("  \"cameras\": [\n");
        for (int c = 0; c < slots.Count; c++)
        {
            var slot = slots[c];
            Camera cam = slot.cam;
            int w = slot.width, h = slot.height;
            float vFovRad = cam.fieldOfView * Mathf.Deg2Rad;
            float fy = 0.5f * h / Mathf.Tan(vFovRad * 0.5f);
            float fx = fy;
            float cx = w * 0.5f;
            float cy = h * 0.5f;

            if (c > 0) sb.Append(",\n");
            sb.Append("    {");
            sb.Append(" \"id\": \"cam_").Append(slot.index1).Append("\"");
            sb.Append(", \"index\": ").Append(slot.index1);
            sb.Append(", \"name\": \"").Append(cam.name).Append("\"");
            sb.Append(", \"folder\": \"cam_").Append(slot.index1).Append("\"");
            sb.Append(", \"rgb_dir\": \"cam_").Append(slot.index1).Append("/rgb\"");
            if (writeKeyrgbOverlay) sb.Append(", \"keyrgb_dir\": \"cam_").Append(slot.index1).Append("/keyrgb\"");
            if (writeYoloBboxes)    sb.Append(", \"bboxes_dir\": \"cam_").Append(slot.index1).Append("/bboxes\"");
            sb.Append(", \"model\": \"pinhole\"");
            sb.Append(", \"image_size\": [").Append(w).Append(", ").Append(h).Append("]");
            sb.Append(", \"fx\": ").Append(F(fx));
            sb.Append(", \"fy\": ").Append(F(fy));
            sb.Append(", \"cx\": ").Append(F(cx));
            sb.Append(", \"cy\": ").Append(F(cy));
            sb.Append(", \"K\": [[").Append(F(fx)).Append(", 0, ").Append(F(cx)).Append("],")
                       .Append(" [0, ").Append(F(fy)).Append(", ").Append(F(cy)).Append("],")
                       .Append(" [0, 0, 1]]");
            sb.Append(", \"position_in_world\": ").Append(V3(cam.transform.position));
            sb.Append(", \"rotation_in_world\": ").Append(Q(cam.transform.rotation));
            sb.Append(", \"world_to_camera\": ").Append(M4(cam.worldToCameraMatrix));
            sb.Append(", \"projection\": ").Append(M4(cam.projectionMatrix));
            sb.Append(", \"near\": ").Append(F(cam.nearClipPlane));
            sb.Append(", \"far\": ").Append(F(cam.farClipPlane));
            sb.Append(" }");
        }
        sb.Append("\n  ]\n}\n");
        File.WriteAllText(Path.Combine(sessionPath, "cameras.json"), sb.ToString());
    }

    static string V3(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "[{0:F6},{1:F6},{2:F6}]", v.x, v.y, v.z);
    static string Q(Quaternion q) => string.Format(CultureInfo.InvariantCulture, "[{0:F6},{1:F6},{2:F6},{3:F6}]", q.x, q.y, q.z, q.w);
    static string F(float f) => f.ToString("F6", CultureInfo.InvariantCulture);
    static string F(double d) => d.ToString("F6", CultureInfo.InvariantCulture);
    static string M4(Matrix4x4 m)
    {
        var sb = new StringBuilder(128);
        sb.Append("[");
        for (int r = 0; r < 4; r++)
        {
            if (r > 0) sb.Append(",");
            sb.Append("[");
            for (int c = 0; c < 4; c++)
            {
                if (c > 0) sb.Append(",");
                sb.Append(F(m[r, c]));
            }
            sb.Append("]");
        }
        sb.Append("]");
        return sb.ToString();
    }

    void OpenJsonWriter()
    {
        string path = Path.Combine(sessionPath, "keypoints_transforms.json");
        jsonWriter = new StreamWriter(path, false, new UTF8Encoding(false));
        jsonWriter.NewLine = "\n";
        jsonFirstFrame = true;

        SkeletonFormatDef format = GetSkeletonFormat(skeletonFormat);
        string datasetName = skeletonFormat.ToString().ToLower();

        jsonWriter.Write("{\n");
        jsonWriter.Write($"  \"dataset\": \"{datasetName}\",\n");
        jsonWriter.Write("  \"multi_view\": true,\n");
        jsonWriter.Write("  \"coordinate_convention\": { \"image_origin\": \"top_left\", \"unity_screen_origin\": \"bottom_left\", \"world\": \"unity_left_handed_y_up\", \"camera_opencv\": \"x_right_y_down_z_forward_meters\" },\n");
        jsonWriter.Write("  \"keypoint_names\": [");
        for (int i = 0; i < format.keypointNames.Length; i++)
        {
            if (i > 0) jsonWriter.Write(",");
            jsonWriter.Write("\""); jsonWriter.Write(format.keypointNames[i]); jsonWriter.Write("\"");
        }
        jsonWriter.Write("],\n");
        jsonWriter.Write("  \"skeleton\": [");
        for (int i = 0; i < format.skeleton.GetLength(0); i++)
        {
            if (i > 0) jsonWriter.Write(",");
            jsonWriter.Write("[");
            jsonWriter.Write(format.skeleton[i, 0].ToString(CultureInfo.InvariantCulture));
            jsonWriter.Write(",");
            jsonWriter.Write(format.skeleton[i, 1].ToString(CultureInfo.InvariantCulture));
            jsonWriter.Write("]");
        }
        jsonWriter.Write("],\n");
        // Camera summary mirrored here so consumers can do everything from
        // keypoints_transforms.json alone (cameras.json is the canonical source).
        jsonWriter.Write("  \"cameras\": [");
        for (int c = 0; c < slots.Count; c++)
        {
            if (c > 0) jsonWriter.Write(",");
            jsonWriter.Write(" { \"id\": \"cam_");
            jsonWriter.Write(slots[c].index1.ToString(CultureInfo.InvariantCulture));
            jsonWriter.Write("\", \"index\": ");
            jsonWriter.Write(slots[c].index1.ToString(CultureInfo.InvariantCulture));
            jsonWriter.Write(", \"name\": \"");
            jsonWriter.Write(slots[c].cam.name);
            jsonWriter.Write("\", \"folder\": \"cam_");
            jsonWriter.Write(slots[c].index1.ToString(CultureInfo.InvariantCulture));
            jsonWriter.Write("\", \"image_size\": [");
            jsonWriter.Write(slots[c].width.ToString(CultureInfo.InvariantCulture));
            jsonWriter.Write(", ");
            jsonWriter.Write(slots[c].height.ToString(CultureInfo.InvariantCulture));
            jsonWriter.Write("] }");
        }
        jsonWriter.Write("],\n");
        jsonWriter.Write("  \"frames\": [\n");
        jsonWriter.Flush();
    }

    void CloseJsonWriter()
    {
        lock (jsonLock)
        {
            if (jsonWriter == null) return;
            try
            {
                jsonWriter.Write("\n  ]\n}\n");
                jsonWriter.Flush();
            }
            finally
            {
                jsonWriter.Dispose();
                jsonWriter = null;
            }
        }
    }
}
