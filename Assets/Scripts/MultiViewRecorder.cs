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
// The keypoint layout is selectable via the "Skeleton Format" dropdown. All
// dataset definitions and the rig->joint resolution live in the shared
// SkeletonFormats helper.
//
// Frame alignment: all per-camera outputs share the same timestamp filename. A
// frame is committed only after every camera's GPU readback succeeds, so the
// JSON entry count equals the file count in each cam_N/rgb/ folder.
[DefaultExecutionOrder(900)]
public class MultiViewRecorder : MonoBehaviour
{
    [Header("Subjects")]
    [Tooltip("Animators to track (multi-person). Each becomes one entry in the per-frame 'persons' array.")]
    public List<Animator> avatars = new List<Animator>();

    [Header("Dataset")]
    [Tooltip("Keypoint convention to export. The recorder adapts the same avatar rig to each " +
             "dataset's joint set as best it can; joints a rig can't supply are written with visible:false.")]
    public SkeletonFormat skeletonFormat = SkeletonFormat.AIC_14;

    [Header("Cameras")]
    [Tooltip("Cameras to capture. Auto-populated from descendant GameObjects on Reset and via the context menu. Editable.")]
    public List<Camera> cameras = new List<Camera>();

    [Header("Subject heuristic")]
    [Tooltip("Distance in meters along Head bone's up axis from Head origin to the top of the skull.")]
    public float headTopUpOffset = 0.14f;
    [Tooltip("Head-local offset for nose. +X=subject left, +Y=up, +Z=forward.")]
    public Vector3 noseHeadOffset    = new Vector3( 0f,     0.04f, 0.09f);
    [Tooltip("Head-local offset for left eye when no LeftEye bone is found.")]
    public Vector3 leftEyeHeadOffset  = new Vector3( 0.035f, 0.05f, 0.085f);
    [Tooltip("Head-local offset for right eye when no RightEye bone is found.")]
    public Vector3 rightEyeHeadOffset = new Vector3(-0.035f, 0.05f, 0.085f);
    [Tooltip("Head-local offset for left ear.")]
    public Vector3 leftEarHeadOffset  = new Vector3( 0.08f, 0.03f, 0f);
    [Tooltip("Head-local offset for right ear.")]
    public Vector3 rightEarHeadOffset = new Vector3(-0.08f, 0.03f, 0f);
    [Tooltip("Distance in meters along the Foot bone's forward axis to the toe tip when no Toes bone exists.")]
    public float toeForwardOffset = 0.12f;

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
    [Tooltip("Write bboxes/{t}.json with one entry per visible avatar (YOLO normalized + pixel xyxy + person/avatar metadata).")]
    public bool writeYoloBboxes = true;
    [Tooltip("Write bboxes/{t}.txt with one Ultralytics YOLO line per avatar ('class xc yc w h', all normalized). This is the format YOLO training ingests directly.")]
    public bool writeYoloTxt = true;
    [Tooltip("Debug: write bbox_vis/{t}.png with rectangles drawn over the captured frame.")]
    public bool writeBboxVis = false;
    public int yoloClassId = 0;
    public float bboxPaddingFraction = 0.1f;
    [Tooltip("Hard cap on the encoder queue across all cameras. New captures are skipped while the queue is full.")]
    public int maxInFlightEncodes = 8;
    [Tooltip("Auto-stop: end the recording and exit Play mode after this many frames have been captured. 0 = record until stopped manually.")]
    public int maxFrames = 0;
    [Tooltip("When exactly one camera is recorded, write a flat session layout (rgb/, keyrgb/, bboxes/, keypoints_transforms.json, K.json at the session root) instead of nesting under cam_1/. Multi-camera sessions always use cam_N/.")]
    public bool flatLayoutForSingleCamera = true;

    [Header("Frame timing")]
    [Tooltip("Deterministic capture: drive Time.captureFramerate so game time advances a fixed step per frame and every frame is captured regardless of encode speed. NOTE: incompatible with a live VideoPlayer in the scene (starves its decoder). Leave off for real-time capture with frame-dropping.")]
    public bool setCaptureFramerate = false;
    [Tooltip("Fixed frames-per-second used when Set Capture Framerate is enabled.")]
    public int frameRate = 30;

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

    // Active format + derived data, resolved when recording begins.
    SkeletonFormats.SkeletonDef def;
    Color32[] kpColors;
    SkeletonFormats.Offsets offsets;
    SkeletonFormat activeFormat;

    SkeletonFormats.Offsets BuildOffsets() => new SkeletonFormats.Offsets {
        headTopUp = headTopUpOffset,
        nose = noseHeadOffset, leftEye = leftEyeHeadOffset, rightEye = rightEyeHeadOffset,
        leftEar = leftEarHeadOffset, rightEar = rightEarHeadOffset, toeForward = toeForwardOffset,
    };

    class AvatarRig
    {
        public Animator animator;
        public readonly SkeletonFormats.RigBones rb = new SkeletonFormats.RigBones();
        public bool bonesResolved;
    }

    class CameraSlot
    {
        public Camera cam;
        public Avante.FulldomeCamera domeCam; // non-null => equidistant fisheye capture
        public RenderTexture rt;              // pinhole: the shadow camera's target
        public RenderTexture source;          // readback source (pinhole rt or dome domemasterFbo)
        public bool ownsDomeFbo;              // true if we created the domemasterFbo and must release it
        public Camera captureCam;             // shadow camera that renders cam's view into rt (pinhole only)
        public GameObject captureCamGO;       // owns captureCam; destroyed on EndRecording
        public string rgbDir, keyrgbDir, bboxDir, bboxVisDir;
        public int width, height;
        public int index1; // 1-based folder index (cam_1/, cam_2/, ...)
        public bool IsDome => domeCam != null;
    }

    readonly List<AvatarRig> rigs = new List<AvatarRig>();
    readonly List<CameraSlot> slots = new List<CameraSlot>();
    bool flatLayout; // single-camera flat session layout for this session

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
    bool _stopping;

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
        // Once an auto-stop is underway, StopAndExitRoutine owns shutdown;
        // don't start new captures or re-trigger EndRecording here.
        if (_stopping) return;

        if (!acquired && record) BeginRecording();
        else if (acquired && !record) EndRecording();
        // Changing the dataset mid-recording restarts the session so the JSON
        // header and per-frame layout stay consistent.
        else if (acquired && skeletonFormat != activeFormat) { EndRecording(); BeginRecording(); }

        if (!acquired) return;

        // Auto-stop after the configured number of captured frames, then leave
        // Play mode. frameCounter counts captures already initiated this session.
        if (maxFrames > 0 && frameCounter >= maxFrames)
        {
            _stopping = true;
            StartCoroutine(StopAndExitRoutine());
            return;
        }

        // Retry name-based bone resolve if the rig wasn't bound at begin time.
        for (int r = 0; r < rigs.Count; r++)
        {
            var rig = rigs[r];
            if (rig.bonesResolved || rig.animator == null) continue;
            SkeletonFormats.Resolve(rig.animator, rig.rb);
            rig.bonesResolved = SkeletonFormats.AnyResolved(rig.rb, def, offsets);
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
        def = SkeletonFormats.Build(skeletonFormat);
        activeFormat = skeletonFormat;
        kpColors = SkeletonFormats.Colors(def);
        offsets = BuildOffsets();

        // Resolve cameras list — drop nulls. Cameras with a sibling
        // Avante.FulldomeCamera are captured as equidistant fisheye; the rest
        // as pinhole.
        var validCams = new List<Camera>();
        for (int i = 0; i < cameras.Count; i++)
        {
            var c = cameras[i];
            if (c == null) continue;
            validCams.Add(c);
        }
        if (validCams.Count == 0)
        {
            Debug.LogError("[MultiViewRecorder] No cameras to record. Add child Cameras and click 'Refresh cameras from children' in the context menu.");
            record = false;
            return;
        }

        // One camera + opt-in => flat KeypointsRecorder-compatible layout
        // (rgb/, keyrgb/, bboxes/, keypoints_transforms.json, K.json at root).
        flatLayout = flatLayoutForSingleCamera && validCams.Count == 1;

        // Build avatar rigs.
        rigs.Clear();
        foreach (var anim in avatars)
        {
            if (anim == null) continue;
            var rig = new AvatarRig { animator = anim };
            SkeletonFormats.Resolve(anim, rig.rb);
            rig.bonesResolved = SkeletonFormats.AnyResolved(rig.rb, def, offsets);
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
            var slot = new CameraSlot
            {
                cam = c,
                domeCam = c.GetComponent<Avante.FulldomeCamera>(),
                index1 = idx1,
            };

            // Output folder root: flat session root for single-cam, else cam_N/.
            string camRoot = flatLayout ? sessionPath : Path.Combine(sessionPath, $"cam_{idx1}");
            Directory.CreateDirectory(camRoot);
            slot.rgbDir = Path.Combine(camRoot, "rgb");
            Directory.CreateDirectory(slot.rgbDir);
            if (writeKeyrgbOverlay)
            {
                slot.keyrgbDir = Path.Combine(camRoot, "keyrgb");
                Directory.CreateDirectory(slot.keyrgbDir);
            }
            if (writeYoloBboxes || writeYoloTxt)
            {
                slot.bboxDir = Path.Combine(camRoot, "bboxes");
                Directory.CreateDirectory(slot.bboxDir);
            }
            if (writeBboxVis)
            {
                slot.bboxVisDir = Path.Combine(camRoot, "bbox_vis");
                Directory.CreateDirectory(slot.bboxVisDir);
            }

            if (slot.IsDome)
            {
                // Dome pipeline renders the fisheye domemaster into an
                // externally-owned RenderTexture. Ensure it exists and read
                // back from it directly — no shadow camera.
                EnsureDomemasterFbo(slot);
                slot.source = slot.domeCam.domemasterFbo;
                slot.width = slot.source.width;
                slot.height = slot.source.height;
            }
            else
            {
                CaptureSettings.Resolve(captureResolution,
                    c.pixelWidth, c.pixelHeight, customWidth, customHeight,
                    out int slotW, out int slotH);
                slot.width = slotW;
                slot.height = slotH;
                slot.rt = new RenderTexture(slot.width, slot.height, 24, RenderTextureFormat.ARGB32);
                slot.rt.Create();
                slot.source = slot.rt;

                // Capture through a shadow camera that mirrors this camera's pose +
                // settings and renders the scene into slot.rt. The original camera
                // is left untouched so its display keeps showing the scene — hijacking
                // the original's targetTexture would blank whatever screen it drives.
                slot.captureCamGO = new GameObject($"_MultiViewRecorder_CaptureCam_{idx1}");
                slot.captureCamGO.hideFlags = HideFlags.HideAndDontSave;
                slot.captureCamGO.transform.SetParent(c.transform, false);
                slot.captureCam = slot.captureCamGO.AddComponent<Camera>();
                slot.captureCam.CopyFrom(c);
                slot.captureCam.targetTexture = slot.rt;
                // Render just after the source camera so it sees the same frame state.
                slot.captureCam.depth = c.depth + 100f;
            }
            slots.Add(slot);
        }

        if (setCaptureFramerate) Time.captureFramerate = frameRate;

        // Keep capturing while the Editor/app window is unfocused — otherwise
        // clicking outside Unity pauses the game loop (and this recorder).
        Application.runInBackground = true;

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
            if (slot.captureCamGO != null) Destroy(slot.captureCamGO);
            if (slot.rt != null) { slot.rt.Release(); Destroy(slot.rt); }
            // Release only the domemaster FBOs we created ourselves.
            if (slot.ownsDomeFbo && slot.domeCam != null && slot.domeCam.domemasterFbo != null)
            {
                slot.domeCam.domemasterFbo.Release();
                Destroy(slot.domeCam.domemasterFbo);
                slot.domeCam.domemasterFbo = null;
            }
        }
        slots.Clear();

        if (setCaptureFramerate) Time.captureFramerate = 0;

        CloseJsonWriter();
        Debug.Log($"[MultiViewRecorder] Session ended at {sessionPath} ({frameCounter} frames)");
        sessionPath = null;
    }

    // Dome pipeline needs an externally-provided RenderTexture to Blit the
    // fisheye domemaster into. Create it if the dome camera doesn't already
    // have one (marking it ours to release on EndRecording).
    void EnsureDomemasterFbo(CameraSlot slot)
    {
        var dc = slot.domeCam;
        if (dc.domemasterFbo == null)
        {
            int size = (int)dc.domemasterResolution;
            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.filterMode = FilterMode.Bilinear;
            rt.Create();
            dc.domemasterFbo = rt;
            slot.ownsDomeFbo = true;
        }
        else if (!dc.domemasterFbo.IsCreated())
        {
            dc.domemasterFbo.Create();
        }
    }

    // Graceful auto-stop: let the last in-flight GPU readbacks land and commit,
    // then end the session and leave Play mode (quit in a build).
    IEnumerator StopAndExitRoutine()
    {
        yield return new WaitForSecondsRealtime(0.25f);
        record = false;
        EndRecording();
        _stopping = false;
        Debug.Log($"[MultiViewRecorder] Reached maxFrames ({maxFrames}); stopping Play mode.");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
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

            int kpCount = def.Count;

            // Compute world-space keypoint data once (camera-agnostic).
            int personCount = rigs.Count;
            var allWorldPos = new Vector3[personCount][];
            var allWorldRot = new Quaternion[personCount][];
            var allLocalPos = new Vector3[personCount][];
            var allValid    = new bool[personCount][];
            for (int p = 0; p < personCount; p++)
            {
                var rig = rigs[p];
                Transform root = rig.animator.transform;
                var wp = new Vector3[kpCount];
                var wr = new Quaternion[kpCount];
                var lp = new Vector3[kpCount];
                var vd = new bool[kpCount];
                for (int k = 0; k < kpCount; k++)
                {
                    var j = def.joints[k];
                    wp[k] = SkeletonFormats.WorldPosition(rig.rb, j, offsets, out bool valid);
                    vd[k] = valid;
                    wr[k] = SkeletonFormats.WorldRotation(rig.rb, j);
                    lp[k] = root.InverseTransformPoint(wp[k]);
                }
                allWorldPos[p] = wp;
                allWorldRot[p] = wr;
                allLocalPos[p] = lp;
                allValid[p]    = vd;
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
                        bool projVisible;
                        if (slot.IsDome)
                            ProjectDome(slot.domeCam, cam, slot.width, slot.height, wp,
                                        out imgPos[k], out imgDepth[k], out projVisible);
                        else
                            ProjectPinhole(cam, slot.width, slot.height, wp,
                                           out imgPos[k], out imgDepth[k], out projVisible);
                        // Force invisible for unresolved keypoints so missing joints
                        // (e.g. COCO face kps on a rig without eye bones) don't draw
                        // wild off-body skeleton lines.
                        vis[k] = projVisible && allValid[p][k];
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

                AsyncGPUReadback.Request(slot.source, 0, TextureFormat.RGB24, req =>
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

            // Per-frame Ultralytics YOLO .txt: one 'class xc yc w h' line per
            // visible avatar (all normalized), the format YOLO training reads.
            if (writeYoloTxt && slot.bboxDir != null)
            {
                var txt = new StringBuilder(allPts.Length * 48);
                for (int p = 0; p < allPts.Length; p++)
                {
                    if (!TryComputePersonBbox(allPts[p], allVis[p], w, h, bboxPaddingFraction,
                                              out float xc, out float yc, out float bw, out float bh))
                        continue;
                    txt.Append(yoloClassId).Append(' ')
                       .Append(F(xc)).Append(' ').Append(F(yc)).Append(' ')
                       .Append(F(bw)).Append(' ').Append(F(bh)).Append('\n');
                }
                File.WriteAllText(Path.Combine(slot.bboxDir, ts + ".txt"), txt.ToString());
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
                Color32[] colors = kpColors;
                int[,] edges = def.edges;
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
                            DrawOverlay(krBuf, wK, hK, allPts[p], allVis[p], colors, edges, pointR, lineT, drawSkel);
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

    // ---------------- Projection ----------------

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

    // Equidistant fisheye / fulldome projection (mirrors KeypointsRecorder's
    // dome branch). `cam` is the FulldomeCamera's underlying camera used for
    // pose; the domemaster is a square image so imgW == imgH.
    void ProjectDome(Avante.FulldomeCamera dome, Camera cam, int imgW, int imgH,
                     Vector3 worldPos, out Vector2 pixel, out float depth, out bool visible)
    {
        Vector3 d = worldPos - cam.transform.position;
        depth = d.magnitude;
        if (depth <= 1e-6f) { pixel = Vector2.zero; visible = false; return; }

        Vector3 dUnit = d / depth;
        Vector3 dCamLocal = Quaternion.Inverse(cam.transform.rotation) * dUnit;

        Vector3 dDome = dCamLocal;
        if (dome.orientation == Avante.Orientation.Fulldome)
            dDome = Quaternion.Euler(90f, 0f, 0f) * dDome;
        if (dome.domeTilt != 0f)
            dDome = Quaternion.Euler(-dome.domeTilt, 0f, 0f) * dDome;

        float horiz = Mathf.Sqrt(dDome.x * dDome.x + dDome.y * dDome.y);
        float phi = Mathf.Atan2(horiz, dDome.z);
        float theta = Mathf.Atan2(dDome.y, dDome.x);
        float horizonRad = dome.horizon * Mathf.Deg2Rad;
        float r = phi / (horizonRad * 0.5f);

        float uSt = r * Mathf.Cos(theta);
        float vSt = r * Mathf.Sin(theta);
        float uTex = (uSt + 1f) * 0.5f;
        float vTex = (vSt + 1f) * 0.5f;

        float px = uTex * imgW;
        float py = (1f - vTex) * imgH; // dome uses a fixed top-down convention
        pixel = new Vector2(px, py);
        visible = r <= 1f && px >= 0f && px < imgW && py >= 0f && py < imgH;
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
                            Color32[] colors, int[,] edges, int pointR, int lineT, bool drawSkel)
    {
        if (drawSkel && edges != null)
        {
            for (int s = 0; s < edges.GetLength(0); s++)
            {
                int a = edges[s, 0];
                int b = edges[s, 1];
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
        if (flatLayout)
            return BuildFrameJsonFlat(idx, relTime, ts, worldPos, worldRot, localPos,
                camPosCv, camRotCv, imagePos, imageDepth, visible);

        int slotCount = slots.Count;
        int personCount = worldPos.Length;
        int kpCount = def.Count;

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
                sb.Append(", \"name\": \"").Append(def.names[k]).Append("\"");
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

    // Single-camera flat frame entry, matching the KeypointsRecorder schema
    // (flat rgb_path/keyrgb_path, a per-frame "camera" block, and keypoints
    // with the camera-space fields inlined instead of a per_camera array).
    string BuildFrameJsonFlat(int idx, double relTime, string ts,
        Vector3[][] worldPos, Quaternion[][] worldRot, Vector3[][] localPos,
        Vector3[][][] camPosCv, Quaternion[][][] camRotCv,
        Vector2[][][] imagePos, float[][][] imageDepth, bool[][][] visible)
    {
        var slot = slots[0];
        int c = 0;
        int personCount = worldPos.Length;
        int kpCount = def.Count;
        string ext = CaptureSettings.Extension(imageFormat);

        var sb = new StringBuilder(4096 + personCount * 2048);
        sb.Append("  {");
        sb.Append("\n    \"frame_index\": ").Append(idx).Append(",");
        sb.Append("\n    \"timestamp\": ").Append(F(relTime)).Append(",");
        sb.Append("\n    \"rgb_path\": \"rgb/").Append(ts).Append(ext).Append("\",");
        if (writeKeyrgbOverlay)
            sb.Append("\n    \"keyrgb_path\": \"keyrgb/").Append(ts).Append(ext).Append("\",");
        sb.Append("\n    \"image_size\": [").Append(slot.width).Append(",").Append(slot.height).Append("],");
        sb.Append("\n    \"camera\": ").Append(BuildCameraBlock(slot)).Append(",");
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
                sb.Append(", \"name\": \"").Append(def.names[k]).Append("\"");
                sb.Append(", \"position_in_world\": ").Append(V3(worldPos[p][k]));
                sb.Append(", \"rotation_in_world\": ").Append(Q(worldRot[p][k]));
                sb.Append(", \"position_in_local\": ").Append(V3(localPos[p][k]));
                sb.Append(", \"position_in_camera_opencv\": ").Append(V3(camPosCv[c][p][k]));
                sb.Append(", \"rotation_in_camera_opencv\": ").Append(Q(camRotCv[c][p][k]));
                sb.Append(", \"image_position\": [").Append(F(imagePos[c][p][k].x)).Append(",").Append(F(imagePos[c][p][k].y)).Append("]");
                sb.Append(", \"image_depth\": ").Append(F(imageDepth[c][p][k]));
                sb.Append(", \"visible\": ").Append(visible[c][p][k] ? "true" : "false");
                sb.Append(" }");
            }
            sb.Append("\n        ]");
            sb.Append("\n      }");
        }

        sb.Append("\n    ]");
        sb.Append("\n  }");
        return sb.ToString();
    }

    // Per-frame camera block (position/rotation + intrinsics), pinhole or
    // fisheye depending on the slot. Used by the flat single-cam schema.
    string BuildCameraBlock(CameraSlot slot)
    {
        Camera cam = slot.cam;
        var sb = new StringBuilder(512);
        sb.Append("{ \"position\": ").Append(V3(cam.transform.position))
          .Append(", \"rotation_quat\": ").Append(Q(cam.transform.rotation));
        if (slot.IsDome)
        {
            var dc = slot.domeCam;
            sb.Append(", \"model\": \"fisheye_equidistant\"")
              .Append(", \"horizon_deg\": ").Append(F(dc.horizon))
              .Append(", \"is_fulldome\": ").Append(dc.orientation == Avante.Orientation.Fulldome ? "true" : "false")
              .Append(", \"dome_tilt_deg\": ").Append(F(dc.domeTilt));
        }
        else
        {
            float vFovRad = cam.fieldOfView * Mathf.Deg2Rad;
            float fy = 0.5f * slot.height / Mathf.Tan(vFovRad * 0.5f);
            float fx = fy;
            float cx = slot.width * 0.5f;
            float cy = slot.height * 0.5f;
            sb.Append(", \"model\": \"pinhole\"")
              .Append(", \"fx\": ").Append(F(fx))
              .Append(", \"fy\": ").Append(F(fy))
              .Append(", \"cx\": ").Append(F(cx))
              .Append(", \"cy\": ").Append(F(cy))
              .Append(", \"near\": ").Append(F(cam.nearClipPlane))
              .Append(", \"far\": ").Append(F(cam.farClipPlane))
              .Append(", \"world_to_camera\": ").Append(M4(cam.worldToCameraMatrix))
              .Append(", \"projection\": ").Append(M4(cam.projectionMatrix));
        }
        sb.Append(" }");
        return sb.ToString();
    }

    void WriteCamerasJson()
    {
        if (string.IsNullOrEmpty(sessionPath)) return;

        // Flat single-camera: write K.json (KeypointsRecorder-compatible).
        if (flatLayout)
        {
            WriteKJson(slots[0]);
            return;
        }

        var sb = new StringBuilder(1024 + slots.Count * 256);
        sb.Append("{\n");
        sb.Append("  \"cameras\": [\n");
        for (int c = 0; c < slots.Count; c++)
        {
            var slot = slots[c];
            Camera cam = slot.cam;
            int w = slot.width, h = slot.height;

            if (c > 0) sb.Append(",\n");
            sb.Append("    {");
            sb.Append(" \"id\": \"cam_").Append(slot.index1).Append("\"");
            sb.Append(", \"index\": ").Append(slot.index1);
            sb.Append(", \"name\": \"").Append(cam.name).Append("\"");
            sb.Append(", \"folder\": \"cam_").Append(slot.index1).Append("\"");
            sb.Append(", \"rgb_dir\": \"cam_").Append(slot.index1).Append("/rgb\"");
            if (writeKeyrgbOverlay) sb.Append(", \"keyrgb_dir\": \"cam_").Append(slot.index1).Append("/keyrgb\"");
            if (writeYoloBboxes || writeYoloTxt) sb.Append(", \"bboxes_dir\": \"cam_").Append(slot.index1).Append("/bboxes\"");
            sb.Append(", \"image_size\": [").Append(w).Append(", ").Append(h).Append("]");
            if (slot.IsDome)
            {
                var dc = slot.domeCam;
                sb.Append(", \"model\": \"fisheye_equidistant\"");
                sb.Append(", \"horizon_deg\": ").Append(F(dc.horizon));
                sb.Append(", \"is_fulldome\": ").Append(dc.orientation == Avante.Orientation.Fulldome ? "true" : "false");
                sb.Append(", \"dome_tilt_deg\": ").Append(F(dc.domeTilt));
            }
            else
            {
                float vFovRad = cam.fieldOfView * Mathf.Deg2Rad;
                float fy = 0.5f * h / Mathf.Tan(vFovRad * 0.5f);
                float fx = fy;
                float cx = w * 0.5f;
                float cy = h * 0.5f;
                sb.Append(", \"model\": \"pinhole\"");
                sb.Append(", \"fx\": ").Append(F(fx));
                sb.Append(", \"fy\": ").Append(F(fy));
                sb.Append(", \"cx\": ").Append(F(cx));
                sb.Append(", \"cy\": ").Append(F(cy));
                sb.Append(", \"K\": [[").Append(F(fx)).Append(", 0, ").Append(F(cx)).Append("],")
                           .Append(" [0, ").Append(F(fy)).Append(", ").Append(F(cy)).Append("],")
                           .Append(" [0, 0, 1]]");
                sb.Append(", \"projection\": ").Append(M4(cam.projectionMatrix));
                sb.Append(", \"near\": ").Append(F(cam.nearClipPlane));
                sb.Append(", \"far\": ").Append(F(cam.farClipPlane));
            }
            sb.Append(", \"position_in_world\": ").Append(V3(cam.transform.position));
            sb.Append(", \"rotation_in_world\": ").Append(Q(cam.transform.rotation));
            sb.Append(", \"world_to_camera\": ").Append(M4(cam.worldToCameraMatrix));
            sb.Append(" }");
        }
        sb.Append("\n  ]\n}\n");
        File.WriteAllText(Path.Combine(sessionPath, "cameras.json"), sb.ToString());
    }

    // Flat single-camera intrinsics file (KeypointsRecorder-compatible).
    void WriteKJson(CameraSlot slot)
    {
        Camera cam = slot.cam;
        int w = slot.width, h = slot.height;
        var sb = new StringBuilder(512);
        sb.Append("{\n");
        if (slot.IsDome)
        {
            var dc = slot.domeCam;
            sb.Append("  \"model\": \"fisheye_equidistant\",\n");
            sb.Append("  \"image_size\": [").Append(w).Append(", ").Append(h).Append("],\n");
            sb.Append("  \"horizon_deg\": ").Append(F(dc.horizon)).Append(",\n");
            sb.Append("  \"is_fulldome\": ").Append(dc.orientation == Avante.Orientation.Fulldome ? "true" : "false").Append(",\n");
            sb.Append("  \"dome_tilt_deg\": ").Append(F(dc.domeTilt)).Append("\n");
        }
        else
        {
            float vFovRad = cam.fieldOfView * Mathf.Deg2Rad;
            float fy = 0.5f * h / Mathf.Tan(vFovRad * 0.5f);
            float fx = fy;
            float cx = w * 0.5f;
            float cy = h * 0.5f;
            sb.Append("  \"model\": \"pinhole\",\n");
            sb.Append("  \"image_size\": [").Append(w).Append(", ").Append(h).Append("],\n");
            sb.Append("  \"fx\": ").Append(F(fx)).Append(",\n");
            sb.Append("  \"fy\": ").Append(F(fy)).Append(",\n");
            sb.Append("  \"cx\": ").Append(F(cx)).Append(",\n");
            sb.Append("  \"cy\": ").Append(F(cy)).Append(",\n");
            sb.Append("  \"K\": [[").Append(F(fx)).Append(", 0, ").Append(F(cx)).Append("],")
                       .Append(" [0, ").Append(F(fy)).Append(", ").Append(F(cy)).Append("],")
                       .Append(" [0, 0, 1]]\n");
        }
        sb.Append("}\n");
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
        jsonWriter.Write($"  \"dataset\": \"{def.tag}\",\n");
        jsonWriter.Write("  \"num_keypoints\": "); jsonWriter.Write(def.Count.ToString(CultureInfo.InvariantCulture)); jsonWriter.Write(",\n");
        if (!flatLayout) jsonWriter.Write("  \"multi_view\": true,\n");
        jsonWriter.Write("  \"coordinate_convention\": { \"image_origin\": \"top_left\", \"unity_screen_origin\": \"bottom_left\", \"world\": \"unity_left_handed_y_up\", \"camera_opencv\": \"x_right_y_down_z_forward_meters\" },\n");
        jsonWriter.Write("  \"keypoint_names\": [");
        for (int i = 0; i < def.names.Length; i++)
        {
            if (i > 0) jsonWriter.Write(",");
            jsonWriter.Write("\""); jsonWriter.Write(def.names[i]); jsonWriter.Write("\"");
        }
        jsonWriter.Write("],\n");
        jsonWriter.Write("  \"skeleton\": [");
        for (int i = 0; i < def.edges.GetLength(0); i++)
        {
            if (i > 0) jsonWriter.Write(",");
            jsonWriter.Write("[");
            jsonWriter.Write(def.edges[i, 0].ToString(CultureInfo.InvariantCulture));
            jsonWriter.Write(",");
            jsonWriter.Write(def.edges[i, 1].ToString(CultureInfo.InvariantCulture));
            jsonWriter.Write("]");
        }
        jsonWriter.Write("],\n");
        // Camera summary mirrored here so consumers can do everything from
        // keypoints_transforms.json alone (cameras.json / K.json is canonical).
        // Omitted in flat single-camera mode where a per-frame "camera" block
        // carries the intrinsics (KeypointsRecorder-compatible schema).
        if (!flatLayout)
        {
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
        }
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
