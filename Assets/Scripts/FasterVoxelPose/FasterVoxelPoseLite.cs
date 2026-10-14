using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

// What the lean estimator hands out for each frame it has estimated.
public sealed class FvpLiteEstimate
{
    public int id;
    public double captureTime;      // scene clock (Time.timeAsDouble) when the camera images were taken
    public float captureReal;       // Time.realtimeSinceStartup then
    public float arrivalReal;       // ... and when the answer was in hand
    public float captureMs;         // main-thread time of the capture: downscale + the wait for the GPU + copy
    public float sendMs;            // end of the capture -> the last byte written to the socket
    public float netMs, totalMs, backboneMs, rootMs, jointMs, supportMs;     // the server's own timing of this frame
    public int proposals = -1;      // people the server localised, before the confidence filter
    public readonly List<FvpPerson> people = new List<FvpPerson>();         // after the phantom filter
    public readonly List<FvpPerson> phantoms = new List<FvpPerson>();       // what the filter took out (with Hide)

    public float LatencyMs => (arrivalReal - captureReal) * 1000f;
}

// Faster-VoxelPose for whatever has to react to the people in real time, with everything else taken out.
//
// FasterVoxelPoseLive is a tool for looking at the model: skeletons painted on every display, a transport bar, a status card, 2D
// heatmaps, a history with rewind (it snapshots the scene's moving objects 30 times a second) and the ground truth read from the
// rigs. This is the same client of the same server (the same warm process, port and cached configuration) that does only the part a
// consumer needs:
//
//   cameras --(downscale on the GPU, blocking read-back)--> server --(people)--> People / EstimateReady / IPoseSource
//
// and it is arranged for the shortest path from the pictures to the people:
//   * no overlay, card, bar, heatmaps, history, moments, rewind, ground truth, frozen copies of the images, no heatmap round trip;
//   * the read-back waits for the GPU (ReadbackMode.Blocking) instead of collecting the callbacks 2-3 rendered frames later;
//   * an answer is used at the end of the frame it arrives in, and the next frame is captured in that same frame (the full component
//     does both one Update later), so the server is idle for as little as possible;
//   * the vertical flip of the read-back is done by the GPU while it downscales.
//
// The server answers ONE client at a time. With Take Over From Full Component on (default) this switches FasterVoxelPoseLive off for
// the Play session, so the two never fight over it (nothing in the scene is changed). ViTPose needs the full component's camera frames, so it
// stops while this runs: to drive something from ViTPose, leave this one off.
[DisallowMultipleComponent]
public sealed partial class FasterVoxelPoseLite : MonoBehaviour, IPoseSource
{
    public enum ReadbackMode
    {
        Blocking = 0,   // wait for the GPU in the frame the images are taken: the shortest path, a short stall of the main thread
        Async = 1,      // the callbacks come 2-3 rendered frames later: no stall, that much more lag
    }

    [Header("Cameras (view order)")]
    [Tooltip("The views fed to the network. CalibratedCameras use their calibrated K and lens distortion; plain Cameras a pinhole from their field of view. At least two. Auto-wired (copied from the FasterVoxelPose object when there is one).")]
    public List<Camera> cameras = new List<Camera>();

    [Header("Capture volume (Unity world)")]
    [Tooltip("People are only found inside this box. Its centre is this anchor's position plus the offset; the box stays axis-aligned with the world. Keep it equal to FasterVoxelPose's so the server reuses its voxel grids.")]
    public Transform volumeAnchor;
    public Vector3 volumeOffset = new Vector3(0f, 0.8f, 0f);
    public Vector3 volumeSize = new Vector3(8f, 2f, 8f);

    [Header("Estimation")]
    [Range(0.02f, 1f)] public float minScore = 0.1f;
    [Tooltip("Width of the frames sent to the network (960 is its own input). Keep it equal to FasterVoxelPose's so the server reuses its voxel grids.")]
    [Min(320)] public int frameWidth = 960;
    [Tooltip("Cap on estimates per second. 0 = as fast as the server answers.")]
    [Min(0f)] public float maxFps = 0f;
    [Tooltip("Most people the network reports per frame. Keep it equal to FasterVoxelPose's so the server reuses its voxel grids.")]
    [Range(1, 10)] public int maxPeople = 10;
    [Tooltip("A person keeps their id when their root moves less than this between two estimates.")]
    public float trackGateMetres = 1.0f;
    [Tooltip("Run the 2D ResNet in half precision on the server (same accuracy, ~15 ms faster).")]
    public bool fastBackbone = true;

    [Header("Phantoms (skeletons of people who are not there)")]
    [Tooltip("Hide: out of People (kept in the estimate's phantoms). Mark: kept in People, flagged (FvpPerson.phantom). Off: no filter. See the phantom section of the FasterVoxelPose README. The server works out the 2D support (~5 ms) for every frame whatever this is set to.")]
    public FasterVoxelPoseLive.PhantomMode phantomFilter = FasterVoxelPoseLive.PhantomMode.Hide;
    [Range(0.05f, 0.8f)] public float minSupport = 0.35f;

    [Header("Latency")]
    [Tooltip("Blocking: the images are read back in the frame they are taken (the shortest path to the server; the main thread waits for the GPU for a few milliseconds). Async: no wait, but the images arrive 2-3 rendered frames later.")]
    public ReadbackMode readback = ReadbackMode.Blocking;
    [Tooltip("Frame rate Unity is held to (Application.targetFrameRate; V-Sync off for it) so the servers get the GPU time Unity does not use. 0 = leave Unity alone. A lower rate frees GPU time for the network but each Unity frame is a step of latency: try both.")]
    [Min(0)] public int liveFrameRate = 30;
    [Tooltip("Unity and the server share one GPU, and past a point the server does not slow down, it collapses (measured: 56 ms at 15 fps, 486 ms at 30 fps under a heavy scene). So Live Frame Rate is only the most Unity is allowed: when the server's own time per frame goes over Slow Server Ms the cap is lowered step by step, and a higher one is tried again later (FvpFrameRateGovernor). Off: Live Frame Rate stays as it is.")]
    public bool adaptiveFrameRate = true;
    [Min(1)] public int minFrameRate = 12;
    [Tooltip("The server's own time for a frame (it is 35-60 ms on a free GPU) above which Unity is slowed down.")]
    public float slowServerMs = 150f;
    [Tooltip("The server answers one client at a time. On start, switch FasterVoxelPoseLive off for this Play session (the scene is not changed; it is switched back on when this stops).")]
    public bool takeOverFromFullComponent = true;

    [Header("Server")]
    [Tooltip("Start fvp_server.py when nothing answers on the port. It is the same server as FasterVoxelPose's (the same port): when that one is warm, this finds it ready.")]
    public bool autoLaunchServer = true;
    public string pythonExe = @"C:\ProgramData\miniconda3\envs\fastervoxelpose\python.exe";
    public string fvpRepo = @"C:\Users\vdmontanacuellar\Documents\Daniel\AProjectPTZCameras\Models\Faster-VoxelPose";
    [Tooltip("Empty = Assets/Scripts/FasterVoxelPose/Server~/fvp_server.py.")]
    public string serverScript = "";
    public string host = "127.0.0.1";
    public int port = 5577;
    [Tooltip("Start the server as soon as the editor opens the scene, so Play finds the model loaded and the voxel grids built.")]
    public bool startServerWithEditor = true;
    [Tooltip("The server exits after this many seconds without a client. 0 = never. Ignored (never) while Start Server With Editor is on.")]
    public int serverIdleExitSeconds = 900;
    public int EffectiveIdleExitSeconds => startServerWithEditor ? 0 : serverIdleExitSeconds;
    [Tooltip("Also stop the server when Play ends.")]
    public bool stopServerWhenDone = false;
    public string extraServerArgs = "";
    public FvpServerProcess.Priority serverPriority = FvpServerProcess.Priority.Normal;
    [Tooltip("Show the server's log lines (timing every 50 frames) in the Console.")]
    public bool echoServerLog = true;
    [Tooltip("Print the report (latency broken into capture / socket / server, the frame-rate cap) to the Console when Play stops.")]
    public bool reportOnDisable = true;

    public string ServerArguments => ((fastBackbone ? "--fp16 " : "") + FvpServerProcess.PriorityArguments(serverPriority) + " " + extraServerArgs).Trim();

    // ---------------- public results ----------------

    public static FasterVoxelPoseLite Instance { get; private set; }

    // The people of the newest estimate (Unity world, metres, Panoptic-15), phantoms taken out.
    public IReadOnlyList<FvpPerson> People => latest != null ? (IReadOnlyList<FvpPerson>)latest.people : Array.Empty<FvpPerson>();
    public FvpLiteEstimate Latest => latest;
    // Raised on the main thread for every new estimate, in the frame it arrives in.
    public event Action<FvpLiteEstimate> EstimateReady;

    public string Status => status;
    public bool IsReady => linkUp && configAcked && slots != null;
    public float EstimateFps => estimateFps;
    public int Estimates => estimateCount;
    public int PhantomsHidden => phantomsHidden;

    // What the estimates cost, over the whole session: capture -> answer in hand, the main-thread time of a capture, the server's total.
    public readonly FvpHistogram LatencyMs = new FvpHistogram(0f, 2000f, 400);
    public readonly FvpHistogram CaptureMs = new FvpHistogram(0f, 200f, 400);
    public readonly FvpHistogram SendMs = new FvpHistogram(0f, 200f, 400);
    public readonly FvpHistogram ServerMs = new FvpHistogram(0f, 1000f, 400);
    // Main-thread milliseconds this component used per rendered frame over the last second (a capture is counted in CaptureMs, not here).
    public float ComponentMsPerFrame => msPerFrame;
    // The cap Unity is held to now (0: none), and how many times the governor changed it.
    public int FrameRateCap => appliedRate > 0 ? appliedRate : 0;
    public int FrameRateChanges => governor.Changes;

    // ---------------- IPoseSource ----------------

    public string PoseSourceName => "FasterVoxelPoseLite";
    public bool PoseSourceRunning => isActiveAndEnabled && Application.isPlaying && IsReady;
    public double PoseClockNow => Time.timeAsDouble;
    public string PoseSourceStatus => status;

    public bool IsPoseCamera(Camera camera)
    {
        if (camera == null) return false;
        if (slots != null)
            foreach (ViewSlot s in slots) if (s.cam == camera || s.shadow == camera) return true;
        return cameras != null && cameras.Contains(camera);
    }

    public bool TryGetPose(out PoseSample sample)
    {
        FvpLiteEstimate e = latest;
        if (e == null) { sample = default; return false; }
        sample = new PoseSample { people = e.people, captureTime = e.captureTime, serial = e.id, latencyMs = e.LatencyMs };
        return true;
    }

    // ---------------- state ----------------

    sealed class ViewSlot
    {
        public Camera cam;
        public CalibratedCamera calib;
        public RenderTexture small;    // the downscaled, flipped copy that is read back
        public Camera shadow;          // renders plain / undistorted cameras into shadowRT
        public RenderTexture shadowRT;

        public bool Distorted => calib != null && calib.IsApplied && calib.DistortionActive;
    }

    sealed class LiteFrame
    {
        public int id;
        public double captureTime;
        public float captureReal;
        public long captureStartTicks, captureEndTicks;
        public float captureMs;
        public byte[][] raw;
        public long sentTicks;         // written by the sender thread (System.Threading.Volatile)
    }

    ViewSlot[] slots;
    int frameW, frameH;
    string problem;

    FvpClient client;
    bool linkUp, configAcked, helloSeen;
    float linkUpReal;
    int configId;
    FvpCameraModel[] sentModels;
    double[] sentCentre, sentSize;
    int sentW, sentH, sentPeople;
    double lastConfigTime = -10;
    float nextConfigCheck;
    int serverVersion;
    string serverGpu = "";

    LiteFrame building, inFlight;
    float inFlightSince;
    int readbackPending;
    bool readbackFailed;
    int frameCounter;
    int generation;
    bool captureArmed;
    double lastCaptureTime = -1.0;
    float lastCaptureReal = -10f;
    float enabledAt;

    FvpLiteEstimate latest;
    readonly FvpTracker tracker = new FvpTracker();
    float estimateFps, lastResultReal;
    int estimateCount, phantomsHidden;

    string status = "";
    bool serverLaunched, busyWarned;
    float serverLaunchReal;
    string serverError;
    FvpLogTail logTail;
    float nextLogPoll;
    bool prevRunInBackground, quitting;
    Coroutine endOfFrameLoop;
    bool rpHooked;
    FasterVoxelPoseLive takenOver;
    readonly ConcurrentQueue<string> workerLogs = new ConcurrentQueue<string>();

    // the cost of this component, measured
    long frameTicks;
    int frameCount;
    float msPerFrame, windowStart;

    static long Stamp() => Stopwatch.GetTimestamp();
    static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    // ---------------- inspector helpers ----------------

    void Reset() { AutoWire(); }

    // The cameras and the volume of the FasterVoxelPose object when the scene has one (so the server reuses its grids), otherwise
    // cam107 / cam103 / cam120 main streams, as FasterVoxelPoseLive finds them.
    [ContextMenu("Auto-wire from scene")]
    public void AutoWire()
    {
        FasterVoxelPoseLive full = FindFirstObjectByType<FasterVoxelPoseLive>(FindObjectsInactive.Include);
        if (full != null) { CopyFrom(full); return; }

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
    }

    // Everything that decides what the server has to build and how it is run, from the full component: the same configuration means
    // the server's grids are reused when you switch between the two.
    public void CopyFrom(FasterVoxelPoseLive full)
    {
        cameras = new List<Camera>(full.cameras);
        volumeAnchor = full.volumeAnchor; volumeOffset = full.volumeOffset; volumeSize = full.volumeSize;
        minScore = full.minScore; frameWidth = full.frameWidth; maxPeople = full.maxPeople; trackGateMetres = full.trackGateMetres;
        fastBackbone = full.fastBackbone; phantomFilter = full.phantomFilter; minSupport = full.minSupport;
        liveFrameRate = full.liveFrameRate;
        pythonExe = full.pythonExe; fvpRepo = full.fvpRepo; serverScript = full.serverScript; host = full.host; port = full.port;
        startServerWithEditor = full.startServerWithEditor; serverIdleExitSeconds = full.serverIdleExitSeconds;
        stopServerWhenDone = full.stopServerWhenDone; extraServerArgs = full.extraServerArgs; serverPriority = full.serverPriority;
        autoLaunchServer = full.autoLaunchServer;
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

    Vector3 VolumeCentreWorld => (volumeAnchor != null ? volumeAnchor.position : Vector3.zero) + volumeOffset;

    // ---------------- lifecycle ----------------

    void OnEnable()
    {
        if (!Application.isPlaying) return;
        Instance = this;
        generation++;
        latest = null; building = inFlight = null; readbackPending = 0;
        configAcked = false; helloSeen = false; sentModels = null; configId = 0; serverVersion = 0;
        serverLaunched = false; serverError = null; busyWarned = false;
        captureArmed = false; lastCaptureTime = -1.0; estimateFps = 0f; lastResultReal = 0f; estimateCount = 0; phantomsHidden = 0;
        LatencyMs.Clear(); CaptureMs.Clear(); SendMs.Clear(); ServerMs.Clear();
        tracker.gateMetres = trackGateMetres;
        tracker.Reset(null, 0);
        msPerFrame = 0f; frameTicks = 0; frameCount = 0; windowStart = 0f;
        governor.Reset(liveFrameRate);

        if (takeOverFromFullComponent) TakeOver();

        enabledAt = Time.realtimeSinceStartup;
        prevRunInBackground = Application.runInBackground;
        Application.runInBackground = true;       // keep estimating while another window has focus

        logTail = echoServerLog ? new FvpLogTail(FvpServerProcess.LogPath) : null;
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
        if (reportOnDisable && estimateCount > 0) Debug.Log(BuildReport(), this);

        client?.Dispose();
        client = null;
        linkUp = false;
        DestroySlots();
        ReleaseBudget();
        FvpBufferPool.Clear();

        Application.runInBackground = prevRunInBackground;
        if (stopServerWhenDone) FvpServerProcess.Stop();
        if (takenOver != null)
        {
            if (!quitting) takenOver.enabled = true;
            takenOver = null;
        }
        if (Instance == this) Instance = null;
    }

    void OnApplicationQuit() { quitting = true; }

    void TakeOver()
    {
        takenOver = null;
        FasterVoxelPoseLive full = FasterVoxelPoseLive.Instance != null ? FasterVoxelPoseLive.Instance : FindFirstObjectByType<FasterVoxelPoseLive>();
        if (full == null || !full.enabled || full.port != port || full.host != host) return;
        full.enabled = false;
        takenOver = full;
        Debug.Log("[FasterVoxelPoseLite] FasterVoxelPoseLive is switched off for this Play session: the server answers one client at a time. " +
                  "(Scene unchanged; untick Take Over From Full Component to keep it running and use another port, or disable this component.)", this);
    }

    // ---------------- per frame ----------------

    void Update()
    {
        if (!Application.isPlaying) return;
        long start = Stamp();
        NoteFrame();
        ApplyBudget();

        PollLogs();
        ManageServer();
        PumpClient();
        if (inFlight != null && Time.realtimeSinceStartup - inFlightSince > 15f)
        {
            Debug.LogWarning("[FasterVoxelPoseLite] the server has not answered for 15 s; giving that frame up.", this);
            DropInFlight();
        }
        if (client != null && linkUp) UpdateConfig();
        else if (slots == null && cameras != null && cameras.Count > 0) EnsureSlots();

        if (WantsCapture()) captureArmed = true;
        UpdateStatus();
        frameTicks += Stamp() - start;
    }

    void NoteFrame()
    {
        frameCount++;
        float now = Time.unscaledTime;
        if (windowStart <= 0f) windowStart = now;
        if (now - windowStart >= 1f)
        {
            msPerFrame = frameCount > 0 ? (float)TicksToMs(frameTicks) / frameCount : 0f;
            frameTicks = 0; frameCount = 0; windowStart = now;
        }
    }

    void PollLogs()
    {
        while (workerLogs.TryDequeue(out string s)) Debug.Log("[FasterVoxelPoseLite] " + s);
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
        if (Time.realtimeSinceStartup - enabledAt < 1.5f) return;     // let an already-running (warm) server answer first

        string script = string.IsNullOrWhiteSpace(serverScript) ? FvpServerProcess.DefaultScriptPath : serverScript;
        if (FvpServerProcess.TryLaunch(pythonExe, script, fvpRepo, host, port, EffectiveIdleExitSeconds, ServerArguments, out string err))
        {
            serverLaunched = true;
            serverLaunchReal = Time.realtimeSinceStartup;
        }
        else
        {
            serverError = err;
            Debug.LogError("[FasterVoxelPoseLite] cannot start the server: " + err, this);
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
                    linkUp = true; linkUpReal = Time.realtimeSinceStartup; helloSeen = false;
                    configAcked = false; sentModels = null;
                    break;
                case "link_down":
                    linkUp = false; helloSeen = false;
                    configAcked = false; sentModels = null;
                    DropInFlight();
                    break;
                case "hello":
                    helloSeen = true;
                    serverVersion = (int)m.Num("server_version", 1);
                    serverGpu = m.Str("gpu") ?? "";
                    Debug.Log($"[FasterVoxelPoseLite] server ready on {serverGpu} ({m.Str("device")}), preprocess '{m.Str("preprocess")}', version {serverVersion}.");
                    if (serverVersion < FasterVoxelPoseLive.RequiredServerVersion && phantomFilter != FasterVoxelPoseLive.PhantomMode.Off)
                        Debug.LogWarning($"[FasterVoxelPoseLite] the running server is version {serverVersion}: it does not send the 2D support the phantom filter needs, so the filter does nothing. " +
                                         "Tools > FasterVoxelPose > Stop inference server, then Play again.", this);
                    break;
                case "config_ok":
                    if ((int)m.Num("config_id") == configId)
                    {
                        configAcked = true;
                        Debug.Log($"[FasterVoxelPoseLite] configured in {m.Num("ms"):F0} ms ({sentModels?.Length ?? 0} views, {frameW}x{frameH}){(m.Bool("reused") ? ": voxel grids and kernels were already built" : "")}.");
                    }
                    break;
                case "result":
                    OnResult(m);
                    break;
                case "error":
                    Debug.LogWarning($"[FasterVoxelPoseLite] server error '{m.Str("code")}': {m.Str("message")}", this);
                    if (m.Str("code") == "no_config") { configAcked = false; sentModels = null; }
                    DropInFlight();
                    break;
            }
        }
    }

    void DropInFlight() { inFlight = null; }

    // ---------------- config ----------------

    void UpdateConfig()
    {
        if (Time.unscaledTime < nextConfigCheck) return;
        nextConfigCheck = Time.unscaledTime + 0.25f;          // a camera moving is the only reason to look again, four times a second is plenty

        if (!EnsureSlots()) return;
        FvpCameraModel[] models = BuildModels();
        if (models == null) return;

        Vector3 c = VolumeCentreWorld;
        double[] centre = FvpCameraModel.UnityPointToModelMm(c.x, c.y, c.z);
        double[] size = FvpCameraModel.UnitySizeToModelMm(volumeSize.x, volumeSize.y, volumeSize.z);
        if (SameConfig(models, centre, size)) return;

        // Rebuilding the model head takes about a second: only when idle, and not in a rapid burst (a camera being dragged).
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
            s.small = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = $"FVP lite view {i}", filterMode = FilterMode.Bilinear };
            s.small.Create();
            slots[i] = s;
        }
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
        }
        slots = null;
    }

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

    // ---------------- results ----------------

    void OnResult(FvpMessage m)
    {
        int id = (int)m.Num("id");
        if (inFlight == null || inFlight.id != id) return;      // the answer to a frame we gave up on
        LiteFrame f = inFlight;
        inFlight = null;

        float[] poses = FvpProtocol.DecodePoses(m.bin, (int)m.Num("poses_bytes", m.bin.Length));
        int rows = poses.Length / (FvpProtocol.JointCount * FvpProtocol.PoseStride);

        var e = new FvpLiteEstimate
        {
            id = f.id, captureTime = f.captureTime, captureReal = f.captureReal, captureMs = f.captureMs,
            sendMs = System.Threading.Volatile.Read(ref f.sentTicks) > 0 ? (float)TicksToMs(System.Threading.Volatile.Read(ref f.sentTicks) - f.captureEndTicks) : 0f,
            netMs = (float)m.Num("t_net"), totalMs = (float)m.Num("t_total"),
            backboneMs = (float)m.Num("t_backbone"), rootMs = (float)m.Num("t_root"), jointMs = (float)m.Num("t_joint"),
            supportMs = (float)m.Num("t_support"), proposals = (int)m.Num("proposals", -1),
        };
        BuildPeople(e, poses, rows, ReadSupport(m, rows));
        EvaluatePhantoms(e);
        tracker.gateMetres = trackGateMetres;
        tracker.Assign(e.people, e.captureTime);
        e.arrivalReal = Time.realtimeSinceStartup;

        if (lastResultReal > 0f)
        {
            float inst = 1f / Mathf.Max(1e-3f, e.arrivalReal - lastResultReal);
            estimateFps = estimateFps <= 0f ? inst : Mathf.Lerp(estimateFps, inst, 0.2f);
        }
        lastResultReal = e.arrivalReal;
        estimateCount++;
        LatencyMs.Add(e.LatencyMs); CaptureMs.Add(e.captureMs); ServerMs.Add(e.totalMs);
        if (e.sendMs > 0f) SendMs.Add(e.sendMs);

        latest = e;
        if (adaptiveFrameRate && liveFrameRate > 0) ObserveServerTime(e.totalMs);
        EstimateReady?.Invoke(e);
    }

    void ObserveServerTime(float serverMs)
    {
        governor.maxRate = liveFrameRate;
        governor.minRate = Mathf.Min(minFrameRate, liveFrameRate);
        governor.targetServerMs = slowServerMs;
        int before = governor.Rate;
        int now = governor.Observe(serverMs, Time.realtimeSinceStartup);
        if (now != before)
            Debug.Log(now < before
                ? $"[FasterVoxelPoseLite] the server needs {governor.ServerMs:F0} ms a frame (over {slowServerMs:F0}): the GPU is shared with Unity's rendering, so Unity is held to {now} fps instead of {before}."
                : $"[FasterVoxelPoseLite] the server answers comfortably again: trying {now} fps for Unity (was {before}).", this);
    }

    void BuildPeople(FvpLiteEstimate e, float[] poses, int rows, float[][] support)
    {
        for (int p = 0; p < rows; p++)
        {
            int b = p * FvpProtocol.JointCount * FvpProtocol.PoseStride;
            float valid = poses[b + 3], score = poses[b + 4];
            if (valid < 0f || score < minScore) continue;

            var person = new FvpPerson { score = score };
            if (support != null && p < support.Length && support[p] != null) { person.support = support[p]; person.meanSupport = MeanSupport(support[p]); }
            for (int j = 0; j < FvpProtocol.JointCount; j++)
            {
                int o = b + j * FvpProtocol.PoseStride;
                FvpCameraModel.ModelMmToUnity(poses[o], poses[o + 1], poses[o + 2], out double ux, out double uy, out double uz);
                person.joints[j] = new Vector3((float)ux, (float)uy, (float)uz);
            }
            e.people.Add(person);
        }
    }

    // A skeleton whose 2D support (how strongly each view's own heatmaps back its joints) averages under minSupport is a phantom.
    void EvaluatePhantoms(FvpLiteEstimate e)
    {
        if (phantomFilter == FasterVoxelPoseLive.PhantomMode.Off) return;
        for (int i = e.people.Count - 1; i >= 0; i--)
        {
            FvpPerson p = e.people[i];
            if (p.meanSupport < 0f || p.meanSupport >= minSupport) continue;
            p.phantom = true;
            if (phantomFilter == FasterVoxelPoseLive.PhantomMode.Hide)
            {
                e.people.RemoveAt(i);
                e.phantoms.Insert(0, p);
                phantomsHidden++;
            }
        }
    }

    // [[row, view0, view1, ...], ...] -> support values by row of the poses table (null where the server sent none)
    static float[][] ReadSupport(FvpMessage m, int rows)
    {
        if (m.json == null || !m.json.TryGetValue("support", out object o) || !(o is List<object> list)) return null;
        var byRow = new float[rows][];
        foreach (object entry in list)
        {
            if (!(entry is List<object> e) || e.Count < 2 || !(e[0] is double r)) continue;
            int row = (int)r;
            if (row < 0 || row >= rows) continue;
            var v = new float[e.Count - 1];
            for (int k = 1; k < e.Count; k++) v[k - 1] = e[k] is double d ? (float)d : -1f;
            byRow[row] = v;
        }
        return byRow;
    }

    static float MeanSupport(float[] v)
    {
        float sum = 0f; int n = 0;
        for (int i = 0; i < v.Length; i++) if (v[i] >= 0f) { sum += v[i]; n++; }
        return n > 0 ? sum / n : -1f;
    }

    // ---------------- frame-rate budget ----------------

    int appliedRate = -1, previousTarget, previousVSync;
    bool budgetHeld;

    readonly FvpFrameRateGovernor governor = new FvpFrameRateGovernor();

    void ApplyBudget()
    {
        int want = liveFrameRate;
        if (want > 0 && adaptiveFrameRate) want = Mathf.Clamp(governor.Rate, Mathf.Min(minFrameRate, liveFrameRate), liveFrameRate);
        if (want == appliedRate) return;
        if (!budgetHeld) { previousTarget = Application.targetFrameRate; previousVSync = QualitySettings.vSyncCount; budgetHeld = true; }
        if (want > 0)
        {
            QualitySettings.vSyncCount = 0;          // V-Sync would override the target
            Application.targetFrameRate = want;
        }
        else
        {
            Application.targetFrameRate = previousTarget;
            QualitySettings.vSyncCount = previousVSync;
        }
        appliedRate = want;
    }

    void ReleaseBudget()
    {
        if (!budgetHeld) return;
        Application.targetFrameRate = previousTarget;
        QualitySettings.vSyncCount = previousVSync;
        budgetHeld = false;
        appliedRate = -1;
    }

    // ---------------- status + report ----------------

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
        else if (!helloSeen && Time.realtimeSinceStartup - linkUpReal > 3f)
        {
            status = "The server is busy with another program (FasterVoxelPose?): it answers one client at a time.";
            if (!busyWarned)
            {
                busyWarned = true;
                Debug.LogWarning($"[FasterVoxelPoseLite] connected to {host}:{port} but the server has not said hello for 3 s: another client holds it " +
                                 "(FasterVoxelPoseLive enabled on the same port? Take Over From Full Component switches it off).", this);
            }
        }
        else if (!configAcked) status = "Setting up the voxel grids (about a second)...";
        else status = "Live";
    }

    // The numbers a real-time decision needs, in words.
    public string BuildReport()
    {
        var sb = new StringBuilder(512);
        sb.Append("FasterVoxelPoseLite - ").Append(estimateCount).Append(" estimates, ").Append(estimateFps.ToString("F1", CultureInfo.InvariantCulture)).Append("/s");
        if (!string.IsNullOrEmpty(serverGpu)) sb.Append(" on ").Append(serverGpu);
        sb.AppendLine();
        if (LatencyMs.Count == 0) return sb.Append("  nothing estimated yet: ").Append(status).ToString();
        sb.AppendFormat(CultureInfo.InvariantCulture,"  capture -> people in hand:  mean {0:F0} ms, median {1:F0}, p95 {2:F0}, max {3:F0}\n", LatencyMs.Mean, LatencyMs.Percentile(0.5f), LatencyMs.Percentile(0.95f), LatencyMs.Max);
        sb.AppendFormat(CultureInfo.InvariantCulture,"    capture (main thread):    mean {0:F1} ms, p95 {1:F1}  (downscale + GPU wait + copy)\n", CaptureMs.Mean, CaptureMs.Percentile(0.95f));
        if (SendMs.Count > 0) sb.AppendFormat(CultureInfo.InvariantCulture,"    send to the socket:       mean {0:F1} ms, p95 {1:F1}\n", SendMs.Mean, SendMs.Percentile(0.95f));
        sb.AppendFormat(CultureInfo.InvariantCulture,"    server (all stages):      mean {0:F0} ms, p95 {1:F0}, max {2:F0}\n", ServerMs.Mean, ServerMs.Percentile(0.95f), ServerMs.Max);
        FvpLiteEstimate l = latest;
        if (l != null) sb.AppendFormat(CultureInfo.InvariantCulture,"    last frame, server:       backbone {0:F0} + root {1:F0} + joints {2:F0} + support {3:F0} ms\n", l.backboneMs, l.rootMs, l.jointMs, l.supportMs);
        sb.AppendFormat(CultureInfo.InvariantCulture, "  this component costs the main thread {0:F2} ms per rendered frame; {1} phantom(s) hidden", msPerFrame, phantomsHidden);
        if (liveFrameRate > 0)
            sb.AppendFormat(CultureInfo.InvariantCulture, "\n  Unity held to {0} fps (at most {1}){2}", FrameRateCap, liveFrameRate,
                adaptiveFrameRate ? "; the governor changed the cap " + governor.Changes + " time(s)" : "; fixed");
        return sb.ToString();
    }

    [ContextMenu("Print report")]
    void PrintReport() { Debug.Log(BuildReport(), this); }
}
