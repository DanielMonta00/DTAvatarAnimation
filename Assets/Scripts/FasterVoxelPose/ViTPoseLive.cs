using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

// ViTPose (Xu et al., NeurIPS 2022) on every camera view, next to FasterVoxelPose.
//
// A top-down 2D method: YOLOv8 finds the people in each view, ViTPose-B reads 17 COCO joints off a crop of every box. It has no
// notion of the other views, of calibration or of 3D: each view is estimated on its own. It runs in a Python server of its own
// (Server~/vitpose_server.py, in your `vitpose` conda environment) over the same kind of localhost socket as FasterVoxelPose.
//
// It runs on the SAME synced frames FasterVoxelPoseLive captures (that component hosts the cameras and keeps capturing for it
// even when its own server is down), so the two are compared on identical images, and the history, pause, step and rewind of
// FasterVoxelPose cover ViTPose too: its result is attached to the frame it belongs to (FvpFrame.modelResults["vitpose"]).
//
// Put it on a child of an "EstimationModels" object that carries an EstimationModelsHub, next to the FasterVoxelPose object; the
// hub then lists it in the tab strip and switches which model's corner panels (status card, 2D heatmaps) are shown.
public sealed partial class ViTPoseLive : MonoBehaviour, IEstimationModel, IFvpFrameConsumer
{
    public const string ResultKey = "vitpose";

    [Header("Server")]
    [Tooltip("Start vitpose_server.py when nothing answers on the port.")]
    public bool autoLaunchServer = true;
    [Tooltip("Start the server as soon as the editor opens this scene (and again when entering Play if it is not running), so the models are loaded before Play. It stays up until the editor closes.")]
    public bool startServerWithEditor = true;
    [Tooltip("python.exe of the environment that has mmpose 0.24 (the ViTPose fork), mmcv-full and ultralytics.")]
    public string pythonExe = @"C:\ProgramData\miniconda3\envs\vitpose\python.exe";
    [Tooltip("The ViTPose checkout (has configs/ and checkpoints/vitpose-b-simple.pth).")]
    public string vitposeRepo = @"C:\Users\vdmontanacuellar\Documents\Daniel\AProjectPTZCameras\Models\ViTPose";
    [Tooltip("YOLOv8 person detector weights.")]
    public string yoloWeights = @"C:\Users\vdmontanacuellar\Documents\Daniel\AProjectPTZCameras\yolov8m.pt";
    [Tooltip("Empty = Assets/Scripts/FasterVoxelPose/Server~/vitpose_server.py.")]
    public string serverScript = "";
    public string host = "127.0.0.1";
    public int port = 5578;
    [Tooltip("The server exits after this many seconds without a client. 0 = never. Ignored (never) while Start Server With Editor is on.")]
    public int serverIdleExitSeconds = 900;
    [Tooltip("Extra arguments for vitpose_server.py (e.g. --det-thr 0.3).")]
    public string extraServerArgs = "";

    [Header("Estimation")]
    [Range(0.05f, 0.95f)]
    [Tooltip("Minimum confidence of the person detector for a box to be given to ViTPose.")]
    public float detectionThreshold = 0.4f;
    [Range(0.05f, 0.95f)]
    [Tooltip("Joints with a lower confidence than this are not drawn (and are not counted as 'found' in the status card).")]
    public float keypointThreshold = 0.3f;

    [Header("3D: multi-view triangulation")]
    [Tooltip("Lift ViTPose's per-view joints to 3D people: the same kind of output as FasterVoxelPose (ids, 15 Panoptic joints in the Unity world, error against the ground truth in mm), by pairing the people of different views and triangulating each joint through the calibrated cameras.")]
    public bool lift3D = true;
    [Range(2, 6)]
    [Tooltip("A 3D person needs to be seen in at least this many views.")]
    public int minViews = 2;
    [Range(0.1f, 1f)]
    [Tooltip("Two people of different views are the same person when the rays of their shared joints pass this close to each other (median over the joints, metres).")]
    public float matchGateMetres = 0.3f;
    [Range(0.02f, 0.3f)]
    [Tooltip("A view whose ray passes farther than this from the triangulated joint is an outlier and is dropped for that joint.")]
    public float outlierMetres = 0.08f;

    [Header("Overlay on the camera displays")]
    [Tooltip("Draw ViTPose's skeletons over each camera's display (master switch of the two below).")]
    public bool showOverlay = true;
    [Tooltip("The raw 2D detections, per view (cyan; lighter = left, darker = right).")]
    public bool show2DSkeletons = true;
    [Tooltip("The lifted 3D people projected back into every view, through the lens (blue-violet): also in the views that did not see them.")]
    public bool showTriangulated = true;
    [Tooltip("The lifted 3D people in the Scene view (and the Game view with Gizmos on).")]
    public bool drawGizmos = true;
    [Range(0.005f, 0.1f)] public float jointRadius = 0.03f;
    [Tooltip("Also draw the detector's boxes.")]
    public bool showBoxes = true;
    [Range(1f, 8f)] public float overlayLineWidth = 2f;
    [Range(1f, 12f)] public float overlayDotRadius = 4f;

    [Header("Widget and heatmaps")]
    [Tooltip("The status card (when this model's tab is selected in the hub): state, rate, latency, people per view and their 2D error against the ground truth.")]
    public bool showHud = true;
    [Tooltip("ViTPose's own 2D joint heatmaps painted over each camera's image (when this model's tab is selected). J / Shift+J step through the joints, G hides them.")]
    public bool showHeatmaps = true;
    [Range(-1, 16)]
    [Tooltip("Which joint: -1 = the strongest of all joints, 0..16 = nose, l-eye, r-eye, l-ear, r-ear, l-shoulder, r-shoulder, l-elbow, r-elbow, l-wrist, r-wrist, l-hip, r-hip, l-knee, r-knee, l-ankle, r-ankle. Applies from the next estimate.")]
    public int heatmapJoint = -1;
    [Min(10)]
    [Tooltip("How many of the newest results keep their heatmaps for browsing the history.")]
    public int heatHistoryFrames = 300;

    // ---------------- IEstimationModel ----------------

    public string ModelName => "ViTPose";
    public Color ModelColor => new Color(0.3f, 0.85f, 1f);
    public bool IsRunning => isActiveAndEnabled && Application.isPlaying;
    public bool HasPanels => true;
    public bool PanelsVisible { get; set; } = true;
    public bool OverlayVisible { get; set; } = true;

    public string Summary
    {
        get
        {
            if (host_ == null) return "no FasterVoxelPose to take the camera frames from";
            if (!linkUp) return "waiting: " + (string.IsNullOrEmpty(status) ? "server starting" : status);
            ViTPoseResult r = latest;
            return r == null ? "waiting for the first frame"
                 : $"{(lift3D ? r.people3d.Count + " 3D person(s) from " : "")}{r.people.Count} detection(s) in {r.views} views, {estimateFps:F1}/s, {r.latencyMs:F0} ms";
        }
    }

    public void StepJoint(int direction)
    {
        showHeatmaps = true;
        int n = ViTPoseSkeleton.Count + 1;
        heatmapJoint = ((heatmapJoint + 1 + direction) % n + n) % n - 1;
    }

    public void ToggleHeatmaps() => showHeatmaps = !showHeatmaps;

    // ---------------- state ----------------

    public ViTPoseResult Latest => latest;
    public float EstimateFps => estimateFps;
    public string Status => status;
    public bool LinkUp => linkUp;

    FasterVoxelPoseLive host_;
    FvpClient client;
    bool linkUp;
    int configId, sentViews, sentW, sentH;
    FvpFrame inFlight;
    bool inFlightHeld;
    float inFlightSince;
    ViTPoseResult latest;
    float estimateFps, lastResultReal;
    string status = "";
    bool serverLaunched;
    float serverLaunchReal, enabledAt;
    string serverError;
    FvpLogTail logTail;
    float nextLogPoll;
    readonly ConcurrentQueue<string> workerLogs = new ConcurrentQueue<string>();
    readonly Queue<ViTPoseResult> resultsWithHeat = new Queue<ViTPoseResult>();

    string ServerFixedArguments => $"--vitpose-dir \"{vitposeRepo}\" --yolo \"{yoloWeights}\"";
    public string ServerArguments => ServerFixedArguments + (string.IsNullOrWhiteSpace(extraServerArgs) ? "" : " " + extraServerArgs);
    // the priority class is the one set on FasterVoxelPose's component (both servers share the GPU with Unity the same way)
    public string ExtraLaunchArguments
    {
        get
        {
            FasterVoxelPoseLive fvp = host_ != null ? host_ : FindFirstObjectByType<FasterVoxelPoseLive>();
            return (FvpServerProcess.PriorityArguments(fvp != null ? fvp.serverPriority : FvpServerProcess.Priority.Normal) + " " + extraServerArgs).Trim();
        }
    }
    public int EffectiveIdleExitSeconds => startServerWithEditor ? 0 : serverIdleExitSeconds;
    public string ServerScript => string.IsNullOrWhiteSpace(serverScript) ? FvpServerProcess.ScriptPath("vitpose_server.py") : serverScript;

    // what the next frame asks the server for
    int HeatRequest => showHeatmaps ? Mathf.Clamp(heatmapJoint, -1, ViTPoseSkeleton.Count - 1) : -2;

    // ---------------- lifecycle ----------------

    void OnEnable()
    {
        if (!Application.isPlaying) return;
        enabledAt = Time.realtimeSinceStartup;
        serverLaunched = false; serverError = null;
        linkUp = false; configId = 0; sentViews = 0;
        inFlight = null; inFlightHeld = false;
        latest = null; resultsWithHeat.Clear();
        framesTaken = framesMissed = 0;

        var hub = GetComponentInParent<EstimationModelsHub>();
        host_ = hub != null && hub.Fvp != null ? hub.Fvp : FindFirstObjectByType<FasterVoxelPoseLive>();
        if (host_ == null) { status = "no FasterVoxelPose in the scene to take the camera frames from"; Debug.LogWarning("[ViTPose] " + status, this); }
        else host_.AddFrameConsumer(this);

        logTail = new FvpLogTail(FvpServerProcess.LogPathOf(FvpServerProcess.ViTPoseName));
        client = new FvpClient(host, port, s => workerLogs.Enqueue(s));
        client.Start();
        status = "connecting to the ViTPose server...";
    }

    void OnDisable()
    {
        if (!Application.isPlaying) return;
        if (host_ != null) host_.RemoveFrameConsumer(this);
        ReleaseHold(inFlight);
        inFlight = null;
        client?.Dispose();
        client = null;
        linkUp = false;
        DestroyVisuals();
    }

    // ---------------- per frame ----------------

    void Update()
    {
        if (!Application.isPlaying) return;
        while (workerLogs.TryDequeue(out string s)) Debug.Log("[ViTPose] " + s);
        if (logTail != null && Time.realtimeSinceStartup >= nextLogPoll)
        {
            nextLogPoll = Time.realtimeSinceStartup + 0.5f;
            logTail.Poll(line => Debug.Log("[ViTPose server] " + line));
        }
        PumpClient();
        ManageServer();
        if (inFlight != null && Time.realtimeSinceStartup - inFlightSince > 15f)
        {
            Debug.LogWarning("[ViTPose] the server has not answered for 15 s; giving that frame up.", this);
            DropInFlight();
        }
        if (host_ != null && linkUp) status = latest == null ? "waiting for the first frame" : "";
    }

    void ManageServer()
    {
        if (linkUp || !autoLaunchServer || serverLaunched || serverError != null) return;
        if (Time.realtimeSinceStartup - enabledAt < 1.5f) return; // a warm server may answer in a moment
        if (FvpServerProcess.Launch(FvpServerProcess.ViTPoseName, pythonExe, ServerScript, ServerFixedArguments, host, port, EffectiveIdleExitSeconds, ExtraLaunchArguments, out string err))
        {
            serverLaunched = true;
            serverLaunchReal = Time.realtimeSinceStartup;
            status = "starting the ViTPose server (loading YOLOv8 and ViTPose)...";
        }
        else
        {
            serverError = err;
            status = "cannot start the server: " + err;
            Debug.LogError("[ViTPose] cannot start the server: " + err, this);
        }
    }

    void PumpClient()
    {
        if (client == null) return;
        int budget = 32;
        while (budget-- > 0 && client.Inbox.TryDequeue(out FvpMessage m))
        {
            switch (m.type)
            {
                case "link_up": linkUp = true; sentViews = 0; break;
                case "link_down": linkUp = false; sentViews = 0; DropInFlight(); break;
                case "hello": Debug.Log($"[ViTPose] server ready on {m.Str("gpu")}: {m.Str("model")}."); break;
                case "config_ok": break;
                case "result": OnResult(m); break;
                case "error":
                    Debug.LogWarning($"[ViTPose] server error '{m.Str("code")}': {m.Str("message")}", this);
                    DropInFlight();
                    break;
            }
        }
    }

    // ---------------- frames ----------------

    // idle and connected: the camera host may hand over its next frame
    public bool WantsFrame => isActiveAndEnabled && linkUp && inFlight == null && client != null;
    // running and connected: a frame it cannot take (it is still busy) is a frame it misses; the host waits for it when the models run in lockstep
    public bool Participates => isActiveAndEnabled && linkUp && client != null;

    // Frames the host captured while this model was busy, and frames it took: with Models In Lockstep on, the first stays at zero.
    public int FramesTaken => framesTaken;
    public int FramesMissed => framesMissed;
    int framesTaken, framesMissed;

    public void OnFrameMissed() { framesMissed++; }

    public void OnFrameCaptured(FvpFrame f)
    {
        if (!WantsFrame || f.raw == null) return;
        foreach (byte[] b in f.raw) if (b == null) return;
        framesTaken++;

        Interlocked.Increment(ref f.pendingOps);          // the pixel buffers stay until the answer is in (ReleaseHold)
        inFlight = f; inFlightHeld = true; inFlightSince = Time.realtimeSinceStartup;

        // a config first whenever the views or the frame size changed (and after every reconnect): the server handles it before the frame
        if (sentViews != f.raw.Length || sentW != f.width || sentH != f.height)
        {
            configId++;
            sentViews = f.raw.Length; sentW = f.width; sentH = f.height;
            client.Send(ViTPoseProtocol.BuildConfigJson(configId, sentViews, sentW, sentH), null, null);
        }
        var parts = new List<ArraySegment<byte>>(f.raw.Length);
        foreach (byte[] b in f.raw) parts.Add(new ArraySegment<byte>(b, 0, b.Length));
        string json = ViTPoseProtocol.BuildFrameJson(f.id, configId, f.raw.Length, f.width, f.height, detectionThreshold, HeatRequest);
        if (!client.Send(json, parts, null)) DropInFlight();
    }

    void ReleaseHold(FvpFrame f)
    {
        if (f != null && inFlightHeld && ReferenceEquals(f, inFlight)) { Interlocked.Decrement(ref f.pendingOps); inFlightHeld = false; }
    }

    void DropInFlight()
    {
        ReleaseHold(inFlight);
        inFlight = null;
    }

    void OnResult(FvpMessage m)
    {
        int id = (int)m.Num("id");
        if (inFlight == null || inFlight.id != id) return;     // the answer to a frame we gave up on
        FvpFrame f = inFlight;

        var r = new ViTPoseResult
        {
            frameId = f.id, sceneTime = f.sceneTime, views = f.raw != null ? f.raw.Length : sentViews, width = f.width, height = f.height,
            detMs = (float)m.Num("t_det"), poseMs = (float)m.Num("t_pose"), serverMs = (float)m.Num("t_total"),
            latencyMs = (Time.realtimeSinceStartup - f.realtime) * 1000f,
        };
        int poseBytes = (int)m.Num("poses_bytes", m.bin.Length);
        ViTPoseProtocol.DecodePeople(m.bin, poseBytes, (int)m.Num("stride", ViTPoseProtocol.Stride), r.people);
        ReadHeat(r, m, poseBytes);
        Accept(r, f);
        float now = Time.realtimeSinceStartup;
        if (lastResultReal > 0f)
        {
            float inst = 1f / Mathf.Max(1e-3f, now - lastResultReal);
            estimateFps = estimateFps <= 0f ? inst : Mathf.Lerp(estimateFps, inst, 0.2f);
        }
        lastResultReal = now;

        OnHeatResult(r, f);            // the pixel buffers are still held here: the heatmap tiles show the very image it was computed on
        ReleaseHold(f);
        inFlight = null;
    }

    // A result in hand (from the server, or injected by a test): against the ground truth in 2D, lifted to 3D, attached to its frame.
    void Accept(ViTPoseResult r, FvpFrame f)
    {
        CompareWithGroundTruth(r, f);
        Lift(r, f);
        f.modelResults[ResultKey] = r;
        latest = r;
        ResultReady?.Invoke(r);
    }

    // For tests and tools: puts a result through everything a result from the server goes through.
    internal void Inject(ViTPoseResult r, FvpFrame f) => Accept(r, f);

    // Raised for every new result, on the main thread.
    public event Action<ViTPoseResult> ResultReady;

    void ReadHeat(ViTPoseResult r, FvpMessage m, int poseBytes)
    {
        int views = (int)m.Num("heat_views"), hw = (int)m.Num("heat_w"), hh = (int)m.Num("heat_h");
        if (views <= 0 || hw <= 0 || hh <= 0 || views > FvpHeatData.MaxViews) return;
        int each = hw * hh;
        if (m.bin.Length < poseBytes + views * each) return;
        r.heat = new byte[views][];
        for (int v = 0; v < views; v++)
        {
            r.heat[v] = new byte[each];
            Buffer.BlockCopy(m.bin, poseBytes + v * each, r.heat[v], 0, each);
        }
        r.heatW = hw; r.heatH = hh;
        r.heatJoint = (int)m.Num("heat_joint", -1);
    }

    // ---------------- against the ground truth ----------------

    static readonly List<(float cost, int e, int g)> pairs = new List<(float, int, int)>();

    // 2D: the ground-truth skeletons (read from the rigs when the frame was captured) are projected into every view through the same
    // camera model that went to the other server, then each ViTPose skeleton is paired with the nearest one.
    void CompareWithGroundTruth(ViTPoseResult r, FvpFrame f)
    {
        if (host_ != null) CompareWithGroundTruth(r, f.groundTruth, host_.CameraModelsFor(f));
    }

    internal static void CompareWithGroundTruth(ViTPoseResult r, IList<FvpPerson> truth, FvpCameraModel[] cams)
    {
        if (truth == null || truth.Count == 0 || cams == null) return;

        int pairCount = ViTPoseSkeleton.ToPanoptic.GetLength(0);
        for (int v = 0; v < r.views && v < cams.Length; v++)
        {
            // ground truth in this view
            var gtPts = new List<Vector2[]>();
            var gtOk = new List<bool[]>();
            foreach (FvpPerson g in truth)
            {
                var pts = new Vector2[pairCount]; var ok = new bool[pairCount];
                for (int k = 0; k < pairCount; k++)
                {
                    int pj = ViTPoseSkeleton.ToPanoptic[k, 1];
                    if (!g.IsValid(pj)) continue;
                    Vector3 w = g.joints[pj];
                    if (cams[v].Project(w.x, w.y, w.z, out double px, out double py)) { pts[k] = new Vector2((float)px, (float)py); ok[k] = true; }
                }
                gtPts.Add(pts); gtOk.Add(ok);
            }

            pairs.Clear();
            var mine = new List<ViTPosePerson>();
            for (int i = 0; i < r.people.Count; i++) if (r.people[i].view == v) mine.Add(r.people[i]);
            for (int e = 0; e < mine.Count; e++)
                for (int g = 0; g < gtPts.Count; g++)
                {
                    float sum = 0f; int n = 0;
                    for (int k = 0; k < pairCount; k++)
                    {
                        if (!gtOk[g][k]) continue;
                        Vector3 kp = mine[e].kp[ViTPoseSkeleton.ToPanoptic[k, 0]];
                        sum += Vector2.Distance(new Vector2(kp.x, kp.y), gtPts[g][k]); n++;
                    }
                    if (n >= 5) pairs.Add((sum / n, e, g));
                }
            pairs.Sort((a, b) => a.cost.CompareTo(b.cost));
            var usedE = new bool[mine.Count]; var usedG = new bool[gtPts.Count];
            foreach (var (cost, e, g) in pairs)
            {
                float gate = Mathf.Max(40f, 0.35f * (mine[e].y2 - mine[e].y1));
                if (usedE[e] || usedG[g] || cost > gate) continue;
                usedE[e] = usedG[g] = true;
                mine[e].errPx = cost;
            }
            for (int e = 0; e < mine.Count; e++) mine[e].ghost = !usedE[e];
        }
    }
}
