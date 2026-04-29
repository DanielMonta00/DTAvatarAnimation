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
//   <session>/rgb_N/{ts}.png         RGB image from camera N
//   <session>/keyrgb_N/{ts}.png      RGB + keypoint overlay (if enabled)
//   <session>/bboxes_N/{ts}.txt      YOLO labels for camera N (if enabled)
//   <session>/bbox_vis_N/{ts}.png    YOLO bboxes drawn on RGB (debug, if enabled)
//   <session>/K.json                 Intrinsics + image size for every camera
//   <session>/keypoints_transforms.json
//                                    Per-frame keypoint transforms with per-camera
//                                    projections (image_position, depth, visibility,
//                                    OpenCV camera-space pose).
//
// Frame alignment: all per-camera outputs share the same timestamp filename. A
// frame is committed only after every camera's GPU readback succeeds, so the
// JSON entry count equals the file count in each rgb_N/ folder.
[DefaultExecutionOrder(900)]
public class MultiViewRecorder : MonoBehaviour
{
    public const int KP_COUNT = 14;

    public static readonly string[] AicKeypointNames =
    {
        "right_shoulder", "right_elbow", "right_wrist",
        "left_shoulder",  "left_elbow",  "left_wrist",
        "right_hip", "right_knee", "right_ankle",
        "left_hip",  "left_knee",  "left_ankle",
        "head_top", "neck",
    };

    static readonly int[,] Skeleton =
    {
        {2,1},{1,0},{0,13},{13,3},{3,4},{4,5},
        {8,7},{7,6},{6,9},{9,10},{10,11},
        {12,13},{0,6},{3,9}
    };

    [Header("Subjects")]
    [Tooltip("Animators to track (multi-person). Each becomes one entry in the per-frame 'persons' array.")]
    public List<Animator> avatars = new List<Animator>();

    [Header("Cameras")]
    [Tooltip("Cameras to capture. Auto-populated from descendant GameObjects on Reset and via the context menu. Editable.")]
    public List<Camera> cameras = new List<Camera>();

    [Header("Subject heuristic")]
    [Tooltip("Distance in meters along Head bone's up axis from Head origin to the top of the skull.")]
    public float headTopUpOffset = 0.14f;

    [Header("Session")]
    public string outputFolder = "Recordings";
    public string sessionName = "MultiViewSession";

    [Header("Recording")]
    public bool record = false;
    [Tooltip("Write keyrgb_N/{t}.png with keypoint overlays. Disable for fastest captures.")]
    public bool writeKeyrgbOverlay = true;
    [Tooltip("Write bboxes_N/{t}.txt with YOLO-format person bboxes (one line per visible avatar).")]
    public bool writeYoloBboxes = true;
    [Tooltip("Debug: write bbox_vis_N/{t}.png with rectangles drawn over the captured frame.")]
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

    static readonly Color32[] keypointColors =
    {
        new Color32(255,128,  0,255), new Color32(255,128,  0,255), new Color32(255,128,  0,255),
        new Color32(  0,255,  0,255), new Color32(  0,255,  0,255), new Color32(  0,255,  0,255),
        new Color32(255,128,  0,255), new Color32(255,128,  0,255), new Color32(255,128,  0,255),
        new Color32(  0,255,  0,255), new Color32(  0,255,  0,255), new Color32(  0,255,  0,255),
        new Color32( 51,153,255,255), new Color32( 51,153,255,255),
    };

    enum KpMode { Bone, HeadTopOffset, MidShoulder }

    class AvatarRig
    {
        public Animator animator;
        public readonly Transform[] keypointTransforms = new Transform[KP_COUNT];
        public readonly KpMode[] keypointModes = new KpMode[KP_COUNT];
        public Transform leftShoulderBone;
        public Transform rightShoulderBone;
        public bool bonesResolved;
    }

    class CameraSlot
    {
        public Camera cam;
        public RenderTexture rt;
        public RenderTexture prevTarget;
        public string rgbDir, keyrgbDir, bboxDir, bboxVisDir;
        public int width, height;
        public int index1; // 1-based folder index (rgb_1/, rgb_2/, ...)
    }

    // Bone-name candidates for Generic rigs (mirrors KeypointsRecorder).
    static readonly string[] BoneNames_LeftUpperArm  = { "LeftArm",      "LeftUpperArm",  "L_UpperArm"  };
    static readonly string[] BoneNames_RightUpperArm = { "RightArm",     "RightUpperArm", "R_UpperArm"  };
    static readonly string[] BoneNames_LeftLowerArm  = { "LeftForeArm",  "LeftLowerArm",  "L_ForeArm"   };
    static readonly string[] BoneNames_RightLowerArm = { "RightForeArm", "RightLowerArm", "R_ForeArm"   };
    static readonly string[] BoneNames_LeftHand      = { "LeftHand",     "L_Hand"                       };
    static readonly string[] BoneNames_RightHand     = { "RightHand",    "R_Hand"                       };
    static readonly string[] BoneNames_LeftUpperLeg  = { "LeftUpLeg",    "LeftUpperLeg",  "L_UpperLeg", "LeftThigh" };
    static readonly string[] BoneNames_RightUpperLeg = { "RightUpLeg",   "RightUpperLeg", "R_UpperLeg", "RightThigh" };
    static readonly string[] BoneNames_LeftLowerLeg  = { "LeftLeg",      "LeftLowerLeg",  "L_LowerLeg", "LeftCalf"  };
    static readonly string[] BoneNames_RightLowerLeg = { "RightLeg",     "RightLowerLeg", "R_LowerLeg", "RightCalf" };
    static readonly string[] BoneNames_LeftFoot      = { "LeftFoot",     "L_Foot"                       };
    static readonly string[] BoneNames_RightFoot     = { "RightFoot",    "R_Foot"                       };
    static readonly string[] BoneNames_Head          = { "Head"                                         };
    static readonly string[] BoneNames_Neck          = { "Neck"                                         };

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
        foreach (var anim in avatars)
        {
            if (anim == null) continue;
            var rig = new AvatarRig { animator = anim };
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
            sessionName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(sessionPath);

        // Per-camera setup.
        slots.Clear();
        for (int i = 0; i < validCams.Count; i++)
        {
            var c = validCams[i];
            int idx1 = i + 1;
            var slot = new CameraSlot
            {
                cam = c,
                index1 = idx1,
                width = Mathf.Max(16, c.pixelWidth),
                height = Mathf.Max(16, c.pixelHeight),
            };
            slot.rgbDir = Path.Combine(sessionPath, $"rgb_{idx1}");
            Directory.CreateDirectory(slot.rgbDir);
            if (writeKeyrgbOverlay)
            {
                slot.keyrgbDir = Path.Combine(sessionPath, $"keyrgb_{idx1}");
                Directory.CreateDirectory(slot.keyrgbDir);
            }
            if (writeYoloBboxes)
            {
                slot.bboxDir = Path.Combine(sessionPath, $"bboxes_{idx1}");
                Directory.CreateDirectory(slot.bboxDir);
            }
            if (writeBboxVis)
            {
                slot.bboxVisDir = Path.Combine(sessionPath, $"bbox_vis_{idx1}");
                Directory.CreateDirectory(slot.bboxVisDir);
            }
            slot.rt = new RenderTexture(slot.width, slot.height, 24, RenderTextureFormat.ARGB32);
            slot.rt.Create();
            slot.prevTarget = c.targetTexture;
            c.targetTexture = slot.rt;
            slots.Add(slot);
        }

        OpenJsonWriter();
        WriteKJson();

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
                var wp = new Vector3[KP_COUNT];
                var wr = new Quaternion[KP_COUNT];
                var lp = new Vector3[KP_COUNT];
                for (int k = 0; k < KP_COUNT; k++)
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
                    var imgPos   = new Vector2[KP_COUNT];
                    var vis      = new bool[KP_COUNT];
                    var camPosCv = new Vector3[KP_COUNT];
                    var camRotCv = new Quaternion[KP_COUNT];
                    var imgDepth = new float[KP_COUNT];

                    for (int k = 0; k < KP_COUNT; k++)
                    {
                        Vector3 wp = allWorldPos[p][k];
                        Vector3 pCamGL = w2cGL.MultiplyPoint3x4(wp);
                        camPosCv[k] = new Vector3(pCamGL.x, -pCamGL.y, -pCamGL.z);
                        Quaternion qBoneCamLH = invCamRot * allWorldRot[p][k];
                        camRotCv[k] = new Quaternion(qBoneCamLH.x, -qBoneCamLH.y, qBoneCamLH.z, qBoneCamLH.w);
                        ProjectPinhole(cam, slot.width, slot.height, wp,
                                       out imgPos[k], out imgDepth[k], out vis[k]);
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
                            CommitFrame(ts, frameJson, allBytes, hadError, imgPosByCam, visByCam);
                    }
                });
            }
        }
        finally { _capturing = false; }
    }

    void CommitFrame(string ts, string frameJson, byte[][] allBytes, int[] hadError,
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

            // YOLO bboxes: tiny synchronous text write.
            if (writeYoloBboxes && slot.bboxDir != null)
            {
                var sb = new StringBuilder(allPts.Length * 64);
                for (int p = 0; p < allPts.Length; p++)
                {
                    if (TryComputePersonBbox(allPts[p], allVis[p], w, h, bboxPaddingFraction,
                                             out float xc, out float yc, out float bw, out float bh))
                    {
                        sb.Append(yoloClassId).Append(' ')
                          .Append(F(xc)).Append(' ').Append(F(yc)).Append(' ')
                          .Append(F(bw)).Append(' ').Append(F(bh)).Append('\n');
                    }
                }
                File.WriteAllText(Path.Combine(slot.bboxDir, ts + ".txt"), sb.ToString());
            }

            // RGB encode.
            string rgbPath = Path.Combine(slot.rgbDir, ts + ".png");
            byte[] rgbBuf = bytes;
            int wRgb = w, hRgb = h;
            Interlocked.Increment(ref _inFlightEncodes);
            Task.Run(() =>
            {
                try
                {
                    var png = ImageConversion.EncodeArrayToPNG(rgbBuf, GraphicsFormat.R8G8B8_UNorm, (uint)wRgb, (uint)hRgb);
                    File.WriteAllBytes(rgbPath, png);
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
                byte[] krBuf = (byte[])bytes.Clone();
                string krPath = Path.Combine(slot.keyrgbDir, ts + ".png");
                int wK = w, hK = h;
                Interlocked.Increment(ref _inFlightEncodes);
                Task.Run(() =>
                {
                    try
                    {
                        for (int p = 0; p < allPts.Length; p++)
                            DrawOverlay(krBuf, wK, hK, allPts[p], allVis[p], pointR, lineT, drawSkel);
                        var png = ImageConversion.EncodeArrayToPNG(krBuf, GraphicsFormat.R8G8B8_UNorm, (uint)wK, (uint)hK);
                        File.WriteAllBytes(krPath, png);
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

        rig.leftShoulderBone  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftUpperArm,  BoneNames_LeftUpperArm);
        rig.rightShoulderBone = ResolveBone(a, isHumanoid, HumanBodyBones.RightUpperArm, BoneNames_RightUpperArm);
        Transform head = ResolveBone(a, isHumanoid, HumanBodyBones.Head, BoneNames_Head);
        Transform neck = ResolveBone(a, isHumanoid, HumanBodyBones.Neck, BoneNames_Neck);

        rig.keypointTransforms[0]  = rig.rightShoulderBone;
        rig.keypointTransforms[1]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightLowerArm, BoneNames_RightLowerArm);
        rig.keypointTransforms[2]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightHand,     BoneNames_RightHand);
        rig.keypointTransforms[3]  = rig.leftShoulderBone;
        rig.keypointTransforms[4]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftLowerArm,  BoneNames_LeftLowerArm);
        rig.keypointTransforms[5]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftHand,      BoneNames_LeftHand);
        rig.keypointTransforms[6]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightUpperLeg, BoneNames_RightUpperLeg);
        rig.keypointTransforms[7]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightLowerLeg, BoneNames_RightLowerLeg);
        rig.keypointTransforms[8]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightFoot,     BoneNames_RightFoot);
        rig.keypointTransforms[9]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftUpperLeg,  BoneNames_LeftUpperLeg);
        rig.keypointTransforms[10] = ResolveBone(a, isHumanoid, HumanBodyBones.LeftLowerLeg,  BoneNames_LeftLowerLeg);
        rig.keypointTransforms[11] = ResolveBone(a, isHumanoid, HumanBodyBones.LeftFoot,      BoneNames_LeftFoot);

        if (head != null) { rig.keypointTransforms[12] = head; rig.keypointModes[12] = KpMode.HeadTopOffset; }
        else              { rig.keypointTransforms[12] = null; rig.keypointModes[12] = KpMode.Bone; }

        if (neck != null) { rig.keypointTransforms[13] = neck; rig.keypointModes[13] = KpMode.Bone; }
        else              { rig.keypointTransforms[13] = null; rig.keypointModes[13] = KpMode.MidShoulder; }
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

    static bool HasAnyResolvedBone(AvatarRig rig)
    {
        for (int i = 0; i < KP_COUNT; i++)
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

    static void DrawOverlay(byte[] buf, int w, int h, Vector2[] pts, bool[] vis,
                            int pointR, int lineT, bool drawSkel)
    {
        if (drawSkel)
        {
            for (int s = 0; s < Skeleton.GetLength(0); s++)
            {
                int a = Skeleton[s, 0];
                int b = Skeleton[s, 1];
                if (!vis[a] || !vis[b]) continue;
                Color32 ca = keypointColors[a], cb = keypointColors[b];
                Color32 cc = new Color32((byte)((ca.r + cb.r) / 2), (byte)((ca.g + cb.g) / 2), (byte)((ca.b + cb.b) / 2), 255);
                DrawLine(buf, w, h, Mathf.RoundToInt(pts[a].x), Mathf.RoundToInt(pts[a].y),
                                    Mathf.RoundToInt(pts[b].x), Mathf.RoundToInt(pts[b].y), lineT, cc);
            }
        }
        for (int i = 0; i < KP_COUNT; i++)
        {
            if (!vis[i]) continue;
            DrawFilledCircle(buf, w, h, Mathf.RoundToInt(pts[i].x), Mathf.RoundToInt(pts[i].y), pointR, keypointColors[i]);
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
        var sb = new StringBuilder(8192 + slotCount * personCount * 1024);

        sb.Append("  {");
        sb.Append("\n    \"frame_index\": ").Append(idx).Append(",");
        sb.Append("\n    \"timestamp\": ").Append(F(relTime)).Append(",");
        sb.Append("\n    \"rgb_paths\": [");
        for (int c = 0; c < slotCount; c++)
        {
            if (c > 0) sb.Append(", ");
            sb.Append("\"rgb_").Append(slots[c].index1).Append("/").Append(ts).Append(".png\"");
        }
        sb.Append("],");
        if (writeKeyrgbOverlay)
        {
            sb.Append("\n    \"keyrgb_paths\": [");
            for (int c = 0; c < slotCount; c++)
            {
                if (c > 0) sb.Append(", ");
                sb.Append("\"keyrgb_").Append(slots[c].index1).Append("/").Append(ts).Append(".png\"");
            }
            sb.Append("],");
        }
        sb.Append("\n    \"persons\": [");

        for (int p = 0; p < personCount; p++)
        {
            sb.Append(p == 0 ? "\n      " : ",\n      ");
            sb.Append("{");
            sb.Append("\n        \"person_index\": ").Append(p).Append(",");
            sb.Append("\n        \"avatar_name\": \"").Append(rigs[p].animator.name).Append("\",");
            sb.Append("\n        \"keypoints\": [");
            for (int k = 0; k < KP_COUNT; k++)
            {
                sb.Append(k == 0 ? "\n          " : ",\n          ");
                sb.Append("{ \"id\": ").Append(k);
                sb.Append(", \"name\": \"").Append(AicKeypointNames[k]).Append("\"");
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

    void WriteKJson()
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
            sb.Append(" \"index\": ").Append(slot.index1);
            sb.Append(", \"name\": \"").Append(cam.name).Append("\"");
            sb.Append(", \"rgb_dir\": \"rgb_").Append(slot.index1).Append("\"");
            if (writeYoloBboxes) sb.Append(", \"bboxes_dir\": \"bboxes_").Append(slot.index1).Append("\"");
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
        File.WriteAllText(Path.Combine(sessionPath, "K.json"), sb.ToString());
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

        jsonWriter.Write("{\n");
        jsonWriter.Write("  \"dataset\": \"aic\",\n");
        jsonWriter.Write("  \"multi_view\": true,\n");
        jsonWriter.Write("  \"coordinate_convention\": { \"image_origin\": \"top_left\", \"unity_screen_origin\": \"bottom_left\", \"world\": \"unity_left_handed_y_up\", \"camera_opencv\": \"x_right_y_down_z_forward_meters\" },\n");
        jsonWriter.Write("  \"keypoint_names\": [");
        for (int i = 0; i < AicKeypointNames.Length; i++)
        {
            if (i > 0) jsonWriter.Write(",");
            jsonWriter.Write("\""); jsonWriter.Write(AicKeypointNames[i]); jsonWriter.Write("\"");
        }
        jsonWriter.Write("],\n");
        jsonWriter.Write("  \"skeleton\": [");
        for (int i = 0; i < Skeleton.GetLength(0); i++)
        {
            if (i > 0) jsonWriter.Write(",");
            jsonWriter.Write("[");
            jsonWriter.Write(Skeleton[i, 0].ToString(CultureInfo.InvariantCulture));
            jsonWriter.Write(",");
            jsonWriter.Write(Skeleton[i, 1].ToString(CultureInfo.InvariantCulture));
            jsonWriter.Write("]");
        }
        jsonWriter.Write("],\n");
        // Camera summary in this file too, so consumers don't need K.json.
        jsonWriter.Write("  \"cameras\": [");
        for (int c = 0; c < slots.Count; c++)
        {
            if (c > 0) jsonWriter.Write(",");
            jsonWriter.Write(" { \"index\": ");
            jsonWriter.Write(slots[c].index1.ToString(CultureInfo.InvariantCulture));
            jsonWriter.Write(", \"name\": \"");
            jsonWriter.Write(slots[c].cam.name);
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
