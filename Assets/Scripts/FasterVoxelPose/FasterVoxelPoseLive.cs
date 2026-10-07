using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering; // GraphicsFormat
using UnityEngine.Rendering;

// Live multi-view 3D pose estimation with Faster-VoxelPose (Ye et al., ECCV 2022).
//
// Add this to an object (Tools > FasterVoxelPose > Create FasterVoxelPose object does it and wires the scene) and
// press Play. Each captured set of camera images goes to a Python server (Server~/fvp_server.py, which Unity
// launches) that runs the unmodified network on the GPU; the fused 3D skeletons come back and are
//   - drawn over each camera's image in a monitor strip on the Game view (what the network saw + what it found),
//   - drawn in 3D as gizmos (Scene view, or Game view with Gizmos on),
//   - published as FvpPerson lists (People, FrameEstimated) for the objects the poses will drive.
//
// Transport controls (monitor buttons, or Space / Left / Right / R / Home / End on the Game view) pause the scene,
// step it one frame at a time and rewind through everything estimated so far. See FasterVoxelPoseLive.Playback.cs.
//
// Frames are estimated one at a time, newest first: while the server is busy the cameras are not read, so the
// estimate always describes the latest scene, a little behind it (the latency shown in the monitor).
[DisallowMultipleComponent]
[DefaultExecutionOrder(800)]
public partial class FasterVoxelPoseLive : MonoBehaviour
{
    [Header("Cameras (view order)")]
    [Tooltip("The views fed to the network. CalibratedCameras use their calibrated K and lens distortion; plain Cameras a pinhole from their field of view. At least two. Auto-wired on Reset / via the context menu.")]
    public List<Camera> cameras = new List<Camera>();

    [Header("Capture volume (Unity world)")]
    [Tooltip("People are only found inside this box (the voxel space). Its centre is this anchor's position plus the offset; the box stays axis-aligned with the world.")]
    public Transform volumeAnchor;
    [Tooltip("Centre of the box relative to the anchor, metres. Y is the height of the middle of the box above the floor.")]
    public Vector3 volumeOffset = new Vector3(0f, 0.8f, 0f);
    [Tooltip("Box size in metres (x, height, z). The network was trained on 8 x 2 x 8 m with 10 cm voxels (80 x 80 x 20): keep the proportions when you can.")]
    public Vector3 volumeSize = new Vector3(8f, 2f, 8f);

    [Header("Estimation")]
    [Range(0.02f, 1f)]
    [Tooltip("Minimum confidence for a person to be shown. Applies live; synthetic scenes score lower (0.1-0.3) than the real data the network was trained on.")]
    public float minScore = 0.1f;
    [Tooltip("Width of the frames sent to the network. The height follows the first camera's aspect ratio. 960 matches the network's own input; more only costs transfer time.")]
    [Min(320)] public int frameWidth = 960;
    [Tooltip("Cap on estimates per second while live. 0 = as fast as the server answers.")]
    [Min(0f)] public float maxFps = 0f;
    [Tooltip("Most people the network reports per frame.")]
    [Range(1, 10)] public int maxPeople = 10;
    [Tooltip("A person keeps their colour / id when their root moves less than this between two estimates.")]
    public float trackGateMetres = 1.0f;

    [Header("Server")]
    [Tooltip("Start fvp_server.py when nothing answers on the port. It stays up after Play (the model takes ~10 s to load) and quits on its own when idle.")]
    public bool autoLaunchServer = true;
    [Tooltip("python.exe of the environment that has torch + the Faster-VoxelPose requirements.")]
    public string pythonExe = @"C:\ProgramData\miniconda3\envs\fastervoxelpose\python.exe";
    [Tooltip("Faster-VoxelPose checkout (has lib/, demo/config.yaml, backbone/ and output/panoptic/jln64/).")]
    public string fvpRepo = @"C:\Users\vdmontanacuellar\Documents\Daniel\AProjectPTZCameras\Models\Faster-VoxelPose";
    [Tooltip("Empty = Assets/Scripts/FasterVoxelPose/Server~/fvp_server.py.")]
    public string serverScript = "";
    public string host = "127.0.0.1";
    public int port = 5577;
    [Tooltip("The server exits after this many seconds without a client. 0 = never.")]
    public int serverIdleExitSeconds = 900;
    [Tooltip("Also stop the server when Play ends. Off keeps the model loaded for the next Play.")]
    public bool stopServerWhenDone = false;
    [Tooltip("Extra arguments for fvp_server.py, e.g. '--preprocess stretch --no-mask' to reproduce the experiment notebook exactly.")]
    public string extraServerArgs = "";

    [Header("Playback")]
    [Tooltip("Estimates kept for rewinding (each holds the camera images, as JPEG, and the skeletons).")]
    [Range(10, 3000)] public int historyFrames = 400;
    [Range(40, 100)] public int jpegQuality = 80;
    [Tooltip("How far one scene step advances, in seconds (the dataset's frame interval).")]
    public float stepSeconds = 1f / 30f;
    [Tooltip("Pausing also stops the scene (Time.timeScale = 0). Off pauses only the estimator.")]
    public bool freezeSceneWhenPaused = true;
    [Tooltip("Animators put back where they were when you rewind or scrub, and resumed from there. Auto-wired to the scene's Animators. Anything else in the scene (physics, scripts) is paused but not rewound.")]
    public List<Animator> trackedAnimators = new List<Animator>();
    [Tooltip("Speed of the rewind / replay buttons relative to the recorded scene time.")]
    [Range(0.1f, 8f)] public float rewindSpeed = 1f;
    public bool enableHotkeys = true;

    [Header("Monitor (Game view)")]
    public bool showMonitor = true;
    [Tooltip("Width of one camera tile, pixels (shrinks to fit the Game view).")]
    [Range(160, 960)] public int tileWidth = 420;
    public bool showJointNames = false;

    [Header("Gizmos")]
    public bool drawSkeleton = true;
    public bool drawLabels = true;
    public bool drawCaptureVolume = true;
    [Range(0.005f, 0.1f)] public float jointRadius = 0.03f;
    public Color volumeColor = new Color(0.2f, 0.9f, 1f, 0.9f);

    // ---------------- public results (for whatever the poses will drive) ----------------

    public static FasterVoxelPoseLive Instance { get; private set; }

    // People of the frame on show: the newest estimate while live, the history frame under the cursor otherwise.
    public IReadOnlyList<FvpPerson> People => displayed != null ? (IReadOnlyList<FvpPerson>)displayed.people : Array.Empty<FvpPerson>();
    public FvpFrame DisplayedFrame => displayed;
    // True while People is the newest estimate of a scene that is running.
    public bool IsLive => !paused && followHead;
    // Raised for every new estimate (live, or produced by a step), on the main thread. Not raised when browsing history.
    public event Action<FvpFrame> FrameEstimated;

    public string Status => status;
    public bool IsReady => linkUp && configAcked && slots != null;
    public int HistoryCount => history.Count;
    public FvpFrame FrameAt(int index) => index >= 0 && index < history.Count ? history[index] : null;
    public int Cursor => cursor;
    public bool IsPaused => paused;
    public float EstimateFps => estimateFps;

    // ---------------- state ----------------

    sealed class ViewSlot
    {
        public Camera cam;
        public CalibratedCamera calib;
        public RenderTexture small;    // downscaled copy that is read back
        public Camera shadow;          // renders plain / undistorted cameras into shadowRT
        public RenderTexture shadowRT;
        public Texture2D display;      // raw uploads for the monitor (RGB24, fixed size)
        public Texture2D decoded;      // JPEG decodes for history frames
        public Texture shown;          // whichever of the two holds the frame on show

        public bool Distorted => calib != null && calib.IsApplied && calib.DistortionActive;
    }

    ViewSlot[] slots;
    int frameW, frameH;

    FvpClient client;
    bool linkUp;
    bool configAcked;
    int configId;
    FvpCameraModel[] sentModels;
    double[] sentCentre, sentSize;
    int sentW, sentH, sentPeople;
    double lastConfigTime = -10;

    FvpFrame building;      // readbacks in flight
    FvpFrame inFlight;      // sent, waiting for the network
    int readbackPending;
    bool readbackFailed;
    int frameCounter;
    int generation;         // bumped on every (re)start so stale GPU callbacks can be ignored
    bool captureArmed;
    float lastCaptureReal = -10f;
    float enabledAt;

    readonly List<FvpFrame> history = new List<FvpFrame>();
    readonly List<FvpFrame> releaseWatch = new List<FvpFrame>();
    FvpFrame displayed;
    int cursor;
    bool followHead = true;
    readonly FvpTracker tracker = new FvpTracker();
    byte[] flipScratch;

    float estimateFps;
    float lastResultReal;
    string status = "";
    bool serverLaunched;
    float serverLaunchReal;
    string serverError;
    FvpLogTail logTail;
    float nextLogPoll;
    bool prevRunInBackground;
    Coroutine endOfFrameLoop;
    bool rpHooked;
    readonly ConcurrentQueue<string> workerLogs = new ConcurrentQueue<string>();

    // ---------------- inspector helpers ----------------

    void Reset() { AutoWire(); }

    [ContextMenu("Auto-wire from scene")]
    public void AutoWire()
    {
        // cam107 / cam103 / cam120 main streams. The scene has two sets with these names (under CalibratedCameras and
        // under MultiViewRecorderCalibrated); the recorder's set is the one with distinct Target Displays, so it wins.
        string[] wanted = { "107", "103", "120" };
        var found = new List<Camera>();
        CalibratedCamera[] all = FindObjectsByType<CalibratedCamera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (string id in wanted)
        {
            Camera best = null; int bestScore = -1;
            foreach (CalibratedCamera cc in all)
            {
                string n = cc.name.Trim().ToLowerInvariant();
                if (!n.Contains(id) || !n.Contains("main") || !cc.TryGetComponent(out Camera cam)) continue;
                int score = (PathOf(cc.transform).Contains("MultiViewRecorderCalibrated") ? 2 : 0) + (cc.isActiveAndEnabled ? 1 : 0);
                if (score > bestScore) { best = cam; bestScore = score; }
            }
            if (best != null) found.Add(best);
        }
        if (found.Count > 0) cameras = found;

        if (volumeAnchor == null)
        {
            volumeAnchor = FindInScene("MOCAPcenter");
            if (volumeAnchor == null && cameras.Count > 0 && cameras[0] != null && cameras[0].TryGetComponent(out CalibratedCamera c0))
                volumeAnchor = c0.worldFrame;
        }

        trackedAnimators.Clear();
        foreach (Animator a in FindObjectsByType<Animator>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (a.runtimeAnimatorController != null) trackedAnimators.Add(a);
    }

    static string PathOf(Transform t)
    {
        string p = t.name;
        for (Transform q = t.parent; q != null; q = q.parent) p = q.name + "/" + p;
        return p;
    }

    Transform FindInScene(string name)
    {
        foreach (GameObject root in gameObject.scene.GetRootGameObjects())
        {
            Transform hit = FindIn(root.transform, name);
            if (hit != null) return hit;
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

    // Centre / size of the voxel space in the model frame (mm, Z up).
    Vector3 VolumeCentreWorld => (volumeAnchor != null ? volumeAnchor.position : Vector3.zero) + volumeOffset;

    // ---------------- lifecycle ----------------

    void OnEnable()
    {
        if (!Application.isPlaying) return;
        Instance = this;
        generation++;
        history.Clear(); releaseWatch.Clear();
        displayed = null; cursor = 0; followHead = true;
        inFlight = building = null; readbackPending = 0;
        configAcked = false; sentModels = null; configId = 0;
        serverLaunched = false; serverError = null;
        paused = false; autoDir = 0; stepQueue = 0; stepState = StepState.Idle; wantOneShot = false;
        tracker.gateMetres = trackGateMetres;
        tracker.Reset(null);

        enabledAt = Time.realtimeSinceStartup;
        prevRunInBackground = Application.runInBackground;
        Application.runInBackground = true; // keep estimating while another window has focus

        logTail = new FvpLogTail(FvpServerProcess.LogPath);
        client = new FvpClient(host, port, s => workerLogs.Enqueue(s));
        client.Start();
        linkUp = false;

        if (GraphicsSettings.currentRenderPipeline != null)
        {
            RenderPipelineManager.endContextRendering += OnEndContextRendering;
            rpHooked = true;
        }
        else endOfFrameLoop = StartCoroutine(EndOfFrameLoop());

        status = "Connecting to the server...";
    }

    void OnDisable()
    {
        if (!Application.isPlaying) return;
        generation++;
        if (rpHooked) { RenderPipelineManager.endContextRendering -= OnEndContextRendering; rpHooked = false; }
        if (endOfFrameLoop != null) { StopCoroutine(endOfFrameLoop); endOfFrameLoop = null; }
        ReleaseTransportState();

        client?.Dispose();
        client = null;
        linkUp = false;

        foreach (FvpFrame f in history) f.evicted = true;
        history.Clear();
        DestroySlots();
        // Worker threads may still be sending / encoding: their buffers are simply left to the GC.
        releaseWatch.Clear();
        FvpBufferPool.Clear();

        Application.runInBackground = prevRunInBackground;
        if (stopServerWhenDone) FvpServerProcess.Stop();
        if (Instance == this) Instance = null;
    }

    // ---------------- per-frame ----------------

    void Update()
    {
        if (!Application.isPlaying) return;

        PollLogs();
        PumpClient();
        ManageServer();
        ReleaseFinishedBuffers();

        tracker.gateMetres = trackGateMetres;
        if (inFlight != null && Time.realtimeSinceStartup - inFlight.realtime > 15f)
        {
            Debug.LogWarning("[FasterVoxelPose] the server has not answered for 15 s; giving that frame up.", this);
            DropInFlight();
        }
        if (client != null && linkUp) UpdateConfig();
        UpdateTransport();

        if (WantsCapture()) captureArmed = true;
        UpdateStatus();
    }

    void PollLogs()
    {
        while (workerLogs.TryDequeue(out string s)) Debug.Log("[FasterVoxelPose] " + s);
        if (logTail != null && Time.realtimeSinceStartup >= nextLogPoll)
        {
            nextLogPoll = Time.realtimeSinceStartup + 0.5f;
            logTail.Poll(line => Debug.Log("[FVP server] " + line));
        }
    }

    // ---------------- server process ----------------

    void ManageServer()
    {
        if (linkUp || !autoLaunchServer || serverLaunched || serverError != null) return;
        // Give an already-running (warm) server a moment to answer before starting another one.
        if (Time.realtimeSinceStartup - enabledAt < 1.5f) return;

        string script = string.IsNullOrWhiteSpace(serverScript) ? FvpServerProcess.DefaultScriptPath : serverScript;
        if (FvpServerProcess.TryLaunch(pythonExe, script, fvpRepo, host, port, serverIdleExitSeconds, extraServerArgs, out string err))
        {
            serverLaunched = true;
            serverLaunchReal = Time.realtimeSinceStartup;
        }
        else
        {
            serverError = err;
            Debug.LogError("[FasterVoxelPose] cannot start the server: " + err, this);
        }
    }

    // ---------------- link ----------------

    void PumpClient()
    {
        if (client == null) return;
        int budget = 64;
        while (budget-- > 0 && client.Inbox.TryDequeue(out FvpMessage m))
        {
            switch (m.type)
            {
                case "link_up":
                    linkUp = true;
                    configAcked = false;
                    sentModels = null;
                    break;
                case "link_down":
                    linkUp = false;
                    configAcked = false;
                    sentModels = null;
                    DropInFlight();
                    break;
                case "hello":
                    Debug.Log($"[FasterVoxelPose] server ready on {m.Str("gpu")} ({m.Str("device")}), preprocess '{m.Str("preprocess")}'.");
                    break;
                case "config_ok":
                    if ((int)m.Num("config_id") == configId)
                    {
                        configAcked = true;
                        Debug.Log($"[FasterVoxelPose] configured in {m.Num("ms"):F0} ms ({sentModels?.Length ?? 0} views, {frameW}x{frameH}).");
                    }
                    break;
                case "result":
                    OnResult(m);
                    break;
                case "error":
                    OnServerError(m);
                    break;
            }
        }
    }

    void OnServerError(FvpMessage m)
    {
        string code = m.Str("code");
        Debug.LogWarning($"[FasterVoxelPose] server error '{code}': {m.Str("message")}", this);
        if (code == "no_config") { configAcked = false; sentModels = null; }
        DropInFlight();
    }

    void DropInFlight()
    {
        if (inFlight != null) { inFlight.evicted = true; inFlight = null; }
    }

    // ---------------- config ----------------

    void UpdateConfig()
    {
        if (!EnsureSlots()) return;
        FvpCameraModel[] models = BuildModels();
        if (models == null) return;

        // minScore is sent with every frame, so it is not part of what makes a new config.
        Vector3 c = VolumeCentreWorld;
        double[] centre = FvpCameraModel.UnityPointToModelMm(c.x, c.y, c.z);
        double[] size = FvpCameraModel.UnitySizeToModelMm(volumeSize.x, volumeSize.y, volumeSize.z);
        if (SameConfig(models, centre, size)) return;

        // Rebuilding the model head takes about a second: do it only when idle, and not in a rapid burst
        // (e.g. a camera being dragged).
        if (inFlight != null || building != null) return;
        if (Time.realtimeSinceStartup - lastConfigTime < 1.0f) return;

        configId++;
        sentModels = models; sentCentre = centre; sentSize = size;
        sentW = frameW; sentH = frameH; sentPeople = maxPeople;
        configAcked = false;
        lastConfigTime = Time.realtimeSinceStartup;
        client.Send(FvpProtocol.BuildConfigJson(configId, frameW, frameH, centre, size, 0.1f, maxPeople, models), null, null);
    }

    bool SameConfig(FvpCameraModel[] models, double[] centre, double[] size)
    {
        if (sentModels == null || sentModels.Length != models.Length || sentW != frameW || sentH != frameH || sentPeople != maxPeople) return false;
        for (int i = 0; i < models.Length; i++)
            if (!models[i].SameAs(sentModels[i])) return false;
        for (int i = 0; i < 3; i++)
            if (Math.Abs(centre[i] - sentCentre[i]) > 0.5 || Math.Abs(size[i] - sentSize[i]) > 0.5) return false;
        return true;
    }

    // The sent size comes from the first camera's aspect; every view is resampled to it, with K scaled to match.
    bool EnsureSlots()
    {
        if (cameras == null || cameras.Count < 1) { problem = "No cameras assigned."; return false; }
        foreach (Camera c in cameras)
            if (c == null) { problem = "A camera slot is empty."; return false; }

        int w = Mathf.Max(64, frameWidth);
        Camera first = cameras[0];
        float aspect = first.TryGetComponent(out CalibratedCamera cc0) && cc0.IsApplied && cc0.imageWidth > 0
            ? (float)cc0.imageHeight / cc0.imageWidth
            : (first.pixelWidth > 0 ? (float)first.pixelHeight / first.pixelWidth : 9f / 16f);
        int h = Mathf.Max(64, Mathf.RoundToInt(w * aspect));

        bool same = slots != null && slots.Length == cameras.Count && frameW == w && frameH == h;
        if (same)
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].cam != cameras[i]) { same = false; break; }
        if (same) { problem = null; return true; }

        DestroySlots();
        frameW = w; frameH = h;
        slots = new ViewSlot[cameras.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            var s = new ViewSlot { cam = cameras[i] };
            s.cam.TryGetComponent(out s.calib);
            s.small = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = $"FVP view {i}", filterMode = FilterMode.Bilinear };
            s.small.Create();
            s.display = new Texture2D(w, h, TextureFormat.RGB24, false) { name = $"FVP display {i}", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            slots[i] = s;
        }
        flipScratch = new byte[w * h * 3];
        problem = null;
        return true;
    }

    void DestroySlots()
    {
        if (slots == null) return;
        foreach (ViewSlot s in slots)
        {
            if (s.shadow != null) Destroy(s.shadow.gameObject);
            if (s.shadowRT != null) { s.shadowRT.Release(); Destroy(s.shadowRT); }
            if (s.small != null) { s.small.Release(); Destroy(s.small); }
            if (s.display != null) Destroy(s.display);
            if (s.decoded != null) Destroy(s.decoded);
        }
        slots = null;
    }

    string problem;

    FvpCameraModel[] BuildModels()
    {
        var models = new FvpCameraModel[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            ViewSlot s = slots[i];
            if (s.cam == null || !s.cam.isActiveAndEnabled) { problem = $"Camera '{(s.cam != null ? s.cam.name.Trim() : "?")}' is disabled."; return null; }
            if (s.calib != null && !s.calib.IsApplied) { problem = $"CalibratedCamera '{s.cam.name.Trim()}' has no calibration loaded."; return null; }

            double fx, fy, cx, cy, maxRadius = 0;
            double[] dist = null;
            if (s.calib != null)
            {
                s.calib.GetIntrinsics(frameW, frameH, out float ffx, out float ffy, out float fcx, out float fcy);
                fx = ffx; fy = ffy; cx = fcx; cy = fcy;
                if (s.Distorted)
                {
                    dist = new double[s.calib.distCoeffs.Length];
                    for (int k = 0; k < dist.Length; k++) dist[k] = s.calib.distCoeffs[k];
                    maxRadius = LensDistortion.UsableRadius(dist);
                }
            }
            else
            {
                fy = 0.5 * frameH / Math.Tan(s.cam.fieldOfView * Mathf.Deg2Rad * 0.5);
                fx = fy;
                cx = (frameW - 1) * 0.5; cy = (frameH - 1) * 0.5;
            }

            Transform t = s.cam.transform;
            models[i] = FvpCameraModel.FromUnityAxes(s.cam.name.Trim(), frameW, frameH,
                V(t.position), V(t.right), V(t.up), V(t.forward), fx, fy, cx, cy, dist, maxRadius);
        }
        return models;
    }

    static double[] V(Vector3 v) => new double[] { v.x, v.y, v.z };

    // ---------------- capture ----------------

    bool WantsCapture()
    {
        if (!IsReady || problem != null) return false;
        if (inFlight != null || building != null) return false;
        if (paused) return wantOneShot;
        if (maxFps > 0f && Time.realtimeSinceStartup - lastCaptureReal < 1f / maxFps) return false;
        return true;
    }

    // SRP: all cameras of the frame (and their lens-distortion remaps) are done when this fires. The Scene view is
    // rendered in a context of its own, so wait for the one that holds our cameras.
    void OnEndContextRendering(ScriptableRenderContext context, List<Camera> rendered)
    {
        if (!captureArmed || slots == null) return;
        if (!rendered.Contains(slots[0].cam)) return;
        CaptureNow();
    }

    IEnumerator EndOfFrameLoop()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();
            if (captureArmed) CaptureNow();
        }
    }

    void CaptureNow()
    {
        captureArmed = false;
        if (!IsReady || inFlight != null || building != null || slots == null) return;

        var f = new FvpFrame
        {
            id = ++frameCounter,
            sceneTime = Time.timeAsDouble,
            unityFrame = Time.frameCount,
            realtime = Time.realtimeSinceStartup,
            width = frameW,
            height = frameH,
            cameras = sentModels,
            anims = SnapshotAnimators(),
            raw = new byte[slots.Length][],
        };

        int gen = generation;
        building = f;
        readbackPending = slots.Length;
        readbackFailed = false;
        lastCaptureReal = Time.realtimeSinceStartup;
        wantOneShot = false;
        if (stepState == StepState.Rendering) { CheckStepLength(); stepState = StepState.Idle; }

        for (int i = 0; i < slots.Length; i++)
        {
            ViewSlot s = slots[i];
            Texture src = SourceTexture(s);
            if (src == null)
            {
                readbackFailed = true;
                if (--readbackPending == 0) FinishBuild(f);
                continue;
            }
            Graphics.Blit(src, s.small); // downscale (bilinear) to the sent size
            int view = i;
            AsyncGPUReadback.Request(s.small, 0, TextureFormat.RGB24, req => OnReadback(gen, f, view, req));
        }
    }

    // The image the camera produces: its lens-distorted output, or (no distortion) a shadow camera's render.
    Texture SourceTexture(ViewSlot s)
    {
        if (s.Distorted)
        {
            s.calib.RenderDistorted(); // make sure it holds this frame
            return s.calib.DistortedTexture;
        }

        if (s.shadow == null)
        {
            var go = new GameObject($"_FVP_ShadowCam_{s.cam.name.Trim()}") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(s.cam.transform, false);
            s.shadow = go.AddComponent<Camera>();
            s.shadow.CopyFrom(s.cam);
            if (s.calib != null) s.shadow.projectionMatrix = s.cam.projectionMatrix; // keep the calibrated K
            else s.shadow.aspect = (float)frameW / frameH; // the pinhole K sent to the server assumes the sent frame's aspect, not the Game view's
            s.shadowRT = new RenderTexture(frameW, frameH, 24, RenderTextureFormat.ARGB32) { name = "FVP shadow" };
            s.shadowRT.Create();
            s.shadow.targetTexture = s.shadowRT;
            s.shadow.depth = s.cam.depth + 100f; // after the source camera, same frame state
        }
        return s.shadowRT;
    }

    void OnReadback(int gen, FvpFrame f, int view, AsyncGPUReadbackRequest req)
    {
        if (gen != generation || building != f) { DiscardRaw(f); return; }

        if (req.hasError) readbackFailed = true;
        else
        {
            NativeArray<byte> data = req.GetData<byte>();
            int stride = frameW * 3;
            if (data.Length != stride * frameH) readbackFailed = true;
            else
            {
                byte[] buf = FvpBufferPool.Rent(stride * frameH);
                // GPU rows run bottom-up; the network and the monitor overlay want top-down.
                for (int y = 0; y < frameH; y++)
                    NativeArray<byte>.Copy(data, (frameH - 1 - y) * stride, buf, y * stride, stride);
                f.raw[view] = buf;
            }
        }
        if (--readbackPending == 0) FinishBuild(f);
    }

    void FinishBuild(FvpFrame f)
    {
        building = null;
        if (readbackFailed)
        {
            if (!failedWarned) { Debug.LogWarning("[FasterVoxelPose] a camera image could not be read back; frame skipped.", this); failedWarned = true; }
            DiscardRaw(f);
            return;
        }
        failedWarned = false;

        // Send and JPEG-encode in parallel; the buffers go back to the pool when both are done.
        f.pendingOps = 2;
        releaseWatch.Add(f);
        inFlight = f;

        var parts = new List<ArraySegment<byte>>(f.raw.Length);
        foreach (byte[] b in f.raw) parts.Add(new ArraySegment<byte>(b, 0, b.Length));
        string json = FvpProtocol.BuildFrameJson(f.id, configId, f.raw.Length, f.width, f.height, minScore);
        if (!client.Send(json, parts, () => Interlocked.Decrement(ref f.pendingOps)))
        {
            f.evicted = true; // link down or queue full: nothing is coming back for this one
            inFlight = null;
        }

        int quality = jpegQuality;
        Task.Run(() => EncodeJpegs(f, quality));
    }

    bool failedWarned;

    static void EncodeJpegs(FvpFrame f, int quality)
    {
        try
        {
            var jp = new byte[f.raw.Length][];
            for (int i = 0; i < jp.Length; i++)
                jp[i] = ImageConversion.EncodeArrayToJPG(f.raw[i], GraphicsFormat.R8G8B8_UNorm, (uint)f.width, (uint)f.height, 0, quality);
            f.jpeg = jp;
        }
        catch (Exception e) { Debug.LogError("[FasterVoxelPose] jpeg: " + e); }
        finally { Interlocked.Decrement(ref f.pendingOps); }
    }

    void DiscardRaw(FvpFrame f)
    {
        if (f == null || f.raw == null) return;
        foreach (byte[] b in f.raw) FvpBufferPool.Return(b);
        f.raw = null;
    }

    // The raw view buffers may only go back to the pool once the sender and the encoder are finished with them, and
    // (so the live monitor can upload them without decoding the JPEG again) once the frame has been estimated and
    // shown, or was given up on.
    void ReleaseFinishedBuffers()
    {
        for (int i = releaseWatch.Count - 1; i >= 0; i--)
        {
            FvpFrame f = releaseWatch[i];
            if (Volatile.Read(ref f.pendingOps) != 0) continue;
            if (!f.evicted && !f.estimated) continue;                   // its result is still on the way
            if (f == displayed && texFrame != f && showMonitor) continue; // the monitor has not uploaded it yet
            DiscardRaw(f);
            if (f.evicted) f.jpeg = null;
            releaseWatch.RemoveAt(i);
        }
    }

    FvpAnimState[] SnapshotAnimators()
    {
        if (trackedAnimators == null || trackedAnimators.Count == 0) return null;
        var list = new List<FvpAnimState>(trackedAnimators.Count);
        foreach (Animator a in trackedAnimators)
            if (a != null && a.isActiveAndEnabled && a.runtimeAnimatorController != null) list.Add(FvpAnimState.Capture(a));
        return list.Count > 0 ? list.ToArray() : null;
    }

    // ---------------- results ----------------

    void OnResult(FvpMessage m)
    {
        int id = (int)m.Num("id");
        if (inFlight == null || inFlight.id != id) return; // answer to a frame we already gave up on
        FvpFrame f = inFlight;
        inFlight = null;

        f.poses = FvpProtocol.DecodePoses(m.bin);
        f.netMs = (float)m.Num("t_net");
        f.totalMs = (float)m.Num("t_total");
        BuildPeople(f);
        tracker.Assign(f.people);
        f.estimated = true;

        float now = Time.realtimeSinceStartup;
        if (lastResultReal > 0f)
        {
            float inst = 1f / Mathf.Max(1e-3f, now - lastResultReal);
            estimateFps = estimateFps <= 0f ? inst : Mathf.Lerp(estimateFps, inst, 0.2f);
        }
        lastResultReal = now;

        AppendToHistory(f);
        FrameEstimated?.Invoke(f);
    }

    void BuildPeople(FvpFrame f)
    {
        f.people.Clear();
        int rows = f.poses.Length / (FvpProtocol.JointCount * FvpProtocol.PoseStride);
        for (int p = 0; p < rows; p++)
        {
            int b = p * FvpProtocol.JointCount * FvpProtocol.PoseStride;
            float valid = f.poses[b + 3], score = f.poses[b + 4];
            if (valid < 0f || score < minScore) continue;

            var person = new FvpPerson { score = score };
            for (int j = 0; j < FvpProtocol.JointCount; j++)
            {
                int o = b + j * FvpProtocol.PoseStride;
                FvpCameraModel.ModelMmToUnity(f.poses[o], f.poses[o + 1], f.poses[o + 2], out double ux, out double uy, out double uz);
                person.joints[j] = new Vector3((float)ux, (float)uy, (float)uz);
            }
            f.people.Add(person);
        }
    }

    void AppendToHistory(FvpFrame f)
    {
        history.Add(f);
        while (history.Count > historyFrames)
        {
            Evict(history[0]);
            history.RemoveAt(0);
            cursor = Mathf.Max(0, cursor - 1);
        }
        if (followHead)
        {
            cursor = history.Count - 1;
            Show(f);
        }
        else Show(history[cursor]); // the frame under the cursor may just have changed if the oldest one was dropped
    }

    static void Evict(FvpFrame f)
    {
        f.evicted = true;
        f.jpeg = null; // raw (if still pending) is released by ReleaseFinishedBuffers
    }

    // ---------------- status ----------------

    void UpdateStatus()
    {
        if (problem != null) status = problem;
        else if (serverError != null) status = "Cannot start the server: " + serverError;
        else if (!linkUp)
        {
            if (serverLaunched)
            {
                float waited = Time.realtimeSinceStartup - serverLaunchReal;
                status = waited < 120f ? $"Starting the server and loading the model... {waited:F0} s" : "The server did not come up - see Logs/fvp_server.log";
            }
            else status = autoLaunchServer ? "Connecting to the server..." : $"Waiting for a server on {host}:{port}...";
        }
        else if (!configAcked) status = "Setting up the voxel grids (about a second)...";
        else if (paused) status = (stepState != StepState.Idle || wantOneShot || inFlight != null || building != null) ? "Paused - estimating..." : "Paused";
        else status = "Live";
    }
}
