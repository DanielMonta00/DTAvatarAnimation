using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

// Moves this object with the operator as the pose estimators SEE them, never as the scene knows them: the position comes from the people
// a pose model finds in the camera images (FasterVoxelPoseLite, FasterVoxelPose or ViTPose), so it works exactly as it would with
// real cameras. Put it on CapsuleOperatorPete (Tools > FasterVoxelPose > Real-time operator capsule does it).
//
//   cameras --> pose model (people, ~100-300 ms old) --> OperatorTrack (which one is the operator, filter, extrapolation) --> this object
//
// Each estimate describes the scene as it was when the images were taken. The follower keeps a Kalman filter on the operator's
// horizontal position and, every rendered frame, puts the capsule where the filter says the operator is NOW (the filtered position moved on by the operator's
// velocity x the age of the estimate), so a walking operator is not trailed by the full pipeline latency. If the estimates stop for
// `holdSeconds` the capsule keeps its last position and shows it (tint, trackLost), and takes the operator back when they reappear.
//
// The scene's real operator is never used to move anything. Give `truthAvatar` and the follower only MEASURES against it (in the
// twin the truth is known): the error of the capsule, of the raw latest estimate, the latency, and how often the operator's body was
// inside the capsule. That is the answer to "can this be done from the images in real time": see BuildReport / the Inspector.
//
// The position follows the operator on the floor plane. Its SIZE follows the detected body: with Fit To Person on, the radius and the two
// tips are those of the smallest capsule that has the whole detected skeleton inside, whatever the pose (a hand held out or up, sitting,
// crouching), computed from the pose model's joints alone: see PoseEstimateFollower.Fit.cs and FvpCapsuleFit.
[DisallowMultipleComponent]
[DefaultExecutionOrder(1000)]   // after the Animators, RokokoFramePlacement and the other LateUpdates: the truth is read for the frame the cameras saw
public sealed partial class PoseEstimateFollower : MonoBehaviour
{
    public enum SourceChoice { Auto = 0, FasterVoxelPoseLite = 1, FasterVoxelPose = 2, ViTPose = 3 }

    public enum AnchorKind
    {
        Torso = 0,      // the middle of neck, shoulders, hips and mid-hip: the joints the network gets best, and the body's axis
        MidHip = 1,
        Neck = 2,
        AllJoints = 3,  // legs scatter by 20+ cm: noisier
    }

    public enum LostBehaviour { HoldLastPosition = 0, ReturnHome = 1 }

    [Header("Where the poses come from")]
    [Tooltip("Auto: FasterVoxelPoseLite if it is running, else FasterVoxelPose, else ViTPose. FasterVoxelPose / ViTPose only deliver while their scene runs live (not paused or rewound).")]
    public SourceChoice source = SourceChoice.Auto;
    [Tooltip("Any component that implements IPoseSource; used instead of Source when set.")]
    public MonoBehaviour sourceOverride;

    [Header("Who the operator is")]
    [Tooltip("While no one is followed: the person nearest to this object is taken, if inside this distance (m). 0 = anyone, the nearest. Several followers never take the same person.")]
    [Min(0f)] public float acquireRadius = 0f;
    [Tooltip("A person is only taken once the same body has been in this many estimates in a row: a ghost lasts one, a person lasts.")]
    [Range(1, 6)] public int confirmEstimates = 2;
    [Tooltip("An estimated person needs at least this many valid joints of the anchor to count.")]
    [Range(1, 6)] public int minAnchorJoints = 3;

    [Header("Where the object goes")]
    public AnchorKind anchor = AnchorKind.Torso;
    [Tooltip("Added to the followed point on the floor plane (world X, Z), metres.")]
    public Vector2 offset = Vector2.zero;

    [Header("Size of the capsule")]
    [Tooltip("Size this object to the person it follows, so that the whole body (a hand held out or up, sitting, crouching) is inside whatever the pose. The size is a function of the skeleton the pose model detected and nothing else: not the scene's real operator (Truth Avatar only measures), not the scene's colliders. The capsule's axis stays on the followed point. Works on an upright capsule (a CapsuleCollider along Y, or Unity's capsule mesh); children (a bigger outer bubble) scale with it. Off: the size stays as authored. While nobody is followed it goes back to the authored size.")]
    public bool fitToPerson = true;
    [Tooltip("Metres of clearance around every joint, on top of how far the body reaches around it (14 cm around a wrist, 15 around an ankle, 17 around the nose): the error of the estimate and of the capsule's place, and the time between two estimates.")]
    [Range(0f, 0.4f)] public float fitMargin = 0.12f;
    [Tooltip("The capsule is never narrower than this.")]
    [Range(0.1f, 1f)] public float fitMinRadius = 0.30f;
    [Tooltip("... and never wider than this: a joint of a bad estimate far from the body must not make a room-sized bubble. A body that needs more is flagged (Clamped, in the report) and sticks out.")]
    [Range(0.5f, 3f)] public float fitMaxRadius = 1.5f;
    [Tooltip("Tip to tip, the least the capsule is tall (a person lying down).")]
    [Range(0.3f, 2f)] public float fitMinHeight = 0.6f;
    [Tooltip("The capsule grows in the frame a limb goes out, and shrinks over this long when it comes back (a wrist that flickers in and out of the estimate must not make the capsule flicker). 0 = shrinks at once.")]
    [Range(0f, 3f)] public float fitShrinkSeconds = 0.5f;
    [Tooltip("On (default): both tips come from the detected skeleton, its lowest and highest points: the size is then a function of the skeleton alone (somebody on a platform or jumping gets a capsule that follows). Off: the lower tip is never higher than the height this object was authored at (a constant of the scene, not of the skeleton), so the capsule stands on the floor even if the detected feet are a few centimetres high.")]
    public bool fitBottom = true;

    [Header("Real time")]
    [Tooltip("Put the object where the operator is NOW: the filtered position moved on by their velocity x the age of the estimate. Off: where the newest estimate had them (smoothed).")]
    public bool predictAhead = true;
    [Tooltip("The most the velocity is extrapolated, seconds. Bounds the damage when the operator stops and the estimates are late.")]
    [Range(0.05f, 2f)] public float maxPredictSeconds = 0.6f;
    [Tooltip("Metres, 1 sigma of one estimate's horizontal position. Bigger = trusts the estimates less, smoother, slower to follow.")]
    [Range(0.01f, 0.5f)] public float measurementNoise = 0.06f;
    [Tooltip("m/s^2: how hard the operator can speed up, stop or turn. Bigger follows changes faster and smooths less.")]
    [Range(0.2f, 6f)] public float maneuverAccel = 1.5f;
    [Tooltip("Seconds of smoothing on the way to the target each frame. 0 = jump to it.")]
    [Range(0f, 0.5f)] public float smoothTime = 0.04f;
    [Tooltip("m/s, the fastest a person is taken to move.")]
    [Range(0.5f, 8f)] public float maxSpeed = 3f;

    [Header("When the operator is not seen")]
    [Tooltip("Without an estimate of the operator for this long the track is lost: the object stops where it is (or goes home) and shows it. Counted from the arrival of the last estimate that had them, so the delay of the pipeline does not count as time they were missing.")]
    [Range(0.2f, 5f)] public float holdSeconds = 1f;
    [Tooltip("... and at least this many intervals between two estimates: when the pipeline is slow (a loaded GPU: 2 estimates a second) one missing estimate is not a lost operator. 0 = only Hold Seconds.")]
    [Range(0f, 6f)] public float holdIntervals = 2.5f;
    [Tooltip("Lost for this long: forget the operator and look for someone again around the object.")]
    [Range(1f, 60f)] public float forgetSeconds = 8f;
    public LostBehaviour lostBehaviour = LostBehaviour.HoldLastPosition;
    [Tooltip("Tint the renderers of this object and its children while it does not know where the operator is (searching or lost), so a capsule that has stopped following is not mistaken for one that has.")]
    public bool tintWhenNotTracking = true;
    public Color notTrackingTint = new Color(0.62f, 0.62f, 0.62f, 0.45f);
    public UnityEvent trackAcquired = new UnityEvent();
    public UnityEvent trackLost = new UnityEvent();

    [Header("In the digital twin")]
    [Tooltip("The pose model reads the images of the cameras, and in the twin those are renders of the scene: a capsule drawn around the operator would hide the operator from them (an opaque one makes the model lose the operator completely, glass tints them) and a real camera would never see it. So this object's renderers are switched off while a camera that feeds the model renders, and on for every other camera (the Game and Scene views, other displays). Layers and physics are untouched.")]
    public bool hideFromPoseCameras = true;
    [Tooltip("The displays of the pose cameras then show no capsule (that is the point of hiding it from them), so its outline is drawn over those displays instead: a screen overlay, not part of the camera images the model, the recorder or a capture of the camera read. Only used while Hide From Pose Cameras is on.")]
    public bool drawOnPoseCameras = true;
    [Tooltip("RenderedAfterwards: the real objects are rendered for each pose camera by a second, hidden camera that sees nothing else, never into the picture the model reads, through the same projection and lens distortion, and shown over the display: the real object (colours, glass, lighting, tint), minus being hidden behind objects in front of it. Outline: a flat silhouette per piece in its material's colour (nothing is rendered). A camera that cannot render it afterwards (not lens-distorted, no render pipeline) shows the outline.")]
    public BubbleDisplay bubbleDisplay = BubbleDisplay.RenderedAfterwards;
    [Tooltip("Resolution of that second render relative to the camera's output: 0.5 is a quarter of the pixels, plenty for a translucent bubble and a quarter of the cost.")]
    [Range(0.25f, 1f)] public float bubbleResolution = 0.5f;
    [Tooltip("Puts the premultiplied bubble over the picture (Hidden/FasterVoxelPose/PremultipliedOver). Found by name when empty; referenced here so that a build includes it.")]
    public Shader bubbleShader;
    [Tooltip("Objects that hide the bubble behind them, as they do in the Scene view: the operator inside it (whose body hides the far side of the glass), a box in front of it. They are drawn depth-only when the bubble is rendered afterwards; anything not listed does not hide it. Empty = the Truth Avatar's renderers.")]
    public List<Renderer> occluders = new List<Renderer>();
    [Tooltip("Draws the occluders depth-only (Hidden/FasterVoxelPose/DepthOnly). Found by name when empty; referenced here so that a build includes it.")]
    public Shader depthShader;
    [Tooltip("A caption on the bubble: who is followed, or why not.")]
    public bool drawLabel = true;
    public Color followingColor = new Color(0.25f, 0.9f, 0.4f, 1f);
    public Color lostColor = new Color(1f, 0.6f, 0.15f, 1f);
    public Color searchingColor = new Color(0.7f, 0.7f, 0.7f, 1f);

    [Header("Measuring (never used to move anything)")]
    [Tooltip("The real operator's rig, to compare the capsule with. Optional.")]
    public Animator truthAvatar;
    [Tooltip("Half the width of a person: the body is inside the capsule when the body's axis is at most radius - this from the capsule's axis.")]
    [Range(0.1f, 0.5f)] public float bodyHalfWidth = 0.25f;
    public bool reportOnDisable = true;
    [Tooltip("Print the report to the Console every this many seconds. 0 = never.")]
    [Min(0f)] public float reportEverySeconds = 0f;
    [Tooltip("One Console line each time the operator is acquired or lost: who was in the last estimate and where the filter expected the operator. Answers 'why did the capsule stop following?'.")]
    public bool logTransitions = true;
    public bool drawGizmos = true;

    // ---------------- what the Inspector and other scripts can read ----------------

    public OperatorTrack.State TrackState => track.Current;
    public int LockedId => track.LockedId;
    public string SourceName => src != null ? src.PoseSourceName : "none";
    public string SourceStatus => src != null ? src.PoseSourceStatus : "no pose source is running";
    public bool HasSource => src != null;
    public float EstimatesPerSecond => estimateRate;
    public int EstimatesSeen => estimatesSeen;
    public OperatorTrack Track => track;
    public float CapsuleRadius => capsule != null ? capsule.radius * Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.z)) : 0.5f;
    public Vector2 LastMeasurement => track.LastMeasurement;

    // Pipeline latency of the estimates (capture -> people in hand), the age of the newest estimate when the object is placed
    // (adds the wait for the next one), and, with a truth avatar: how far the object is from the operator, and how far the raw newest estimate is.
    public readonly FvpHistogram PipelineMs = new FvpHistogram(0f, 2000f, 400);
    public readonly FvpHistogram AgeMs = new FvpHistogram(0f, 3000f, 600);
    public readonly FvpHistogram ErrorMetres = new FvpHistogram(0f, 3f, 600);
    public readonly FvpHistogram RawErrorMetres = new FvpHistogram(0f, 3f, 600);
    public long TruthFrames => truthFrames;
    public long InsideFrames => insideFrames;

    // ---------------- state ----------------

    readonly OperatorTrack track = new OperatorTrack();
    readonly List<OperatorTrack.Candidate> cands = new List<OperatorTrack.Candidate>();
    Func<int, bool> claimedFn;
    static readonly List<PoseEstimateFollower> Active = new List<PoseEstimateFollower>();

    IPoseSource src;
    UnityEngine.Object srcObject;
    float nextResolve;
    int lastSerial = int.MinValue;
    double lastCaptureTime = double.NaN;
    float lastEstimateReal, estimateRate;
    int estimatesSeen;

    CapsuleCollider capsule;
    Vector3 home, smoothVel;
    float startY;
    OperatorTrack.State shown = OperatorTrack.State.Searching;
    bool placed;

    FvpGroundTruth truthReader;
    Animator truthBound;
    readonly List<FvpPerson> truthPeople = new List<FvpPerson>();
    long truthFrames, insideFrames;
    double secondsSearching, secondsLost, secondsTracking;
    float startedReal, nextReport;
    bool hadTrack;

    Renderer[] renderers;
    MaterialPropertyBlock block;
    bool tinted;
    bool[] hideWas;
    bool hiddenNow, srpHooked, builtinHooked;

    static readonly int[] TorsoJoints = { FvpSkeleton.Neck, FvpSkeleton.MidHip, FvpSkeleton.LShoulder, FvpSkeleton.RShoulder, FvpSkeleton.LHip, FvpSkeleton.RHip };
    static readonly int[] MidHipJoints = { FvpSkeleton.MidHip };
    static readonly int[] NeckJoints = { FvpSkeleton.Neck };
    static readonly int[] AllJointsSet = BuildAll();

    static int[] BuildAll()
    {
        var a = new int[FvpSkeleton.Count];
        for (int i = 0; i < a.Length; i++) a[i] = i;
        return a;
    }

    // ---------------- lifecycle ----------------

    void OnEnable()
    {
        if (!Application.isPlaying) return;
        claimedFn = ClaimedByOther;
        Active.Add(this);
        capsule = GetComponent<CapsuleCollider>();
        home = transform.position;
        startY = transform.position.y;
        smoothVel = Vector3.zero;
        placed = false;
        track.Reset();
        shown = OperatorTrack.State.Searching;
        hadTrack = false;
        src = null; srcObject = null; nextResolve = 0f;
        lastSerial = int.MinValue; lastCaptureTime = double.NaN; lastEstimateReal = 0f; estimateRate = 0f; estimatesSeen = 0;
        ResetStatistics();
        startedReal = Time.realtimeSinceStartup;
        nextReport = reportEverySeconds > 0f ? startedReal + reportEverySeconds : float.MaxValue;
        ApplyTint(tintWhenNotTracking);      // nothing is followed yet
        FitBegin();

        CacheRenderers();
        hiddenNow = false;
        if (GraphicsSettings.currentRenderPipeline != null)
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
            srpHooked = true;
        }
        else
        {
            Camera.onPreCull += OnPreCullCamera;
            Camera.onPostRender += OnPostRenderCamera;
            builtinHooked = true;
        }
    }

    void OnDisable()
    {
        if (!Application.isPlaying) return;
        if (srpHooked) { RenderPipelineManager.beginCameraRendering -= OnBeginCamera; RenderPipelineManager.endCameraRendering -= OnEndCamera; srpHooked = false; }
        if (builtinHooked) { Camera.onPreCull -= OnPreCullCamera; Camera.onPostRender -= OnPostRenderCamera; builtinHooked = false; }
        ShowToAllCameras();
        DestroyDisplay();
        Active.Remove(this);
        ApplyTint(false);
        FitEnd();
        if (reportOnDisable && estimatesSeen > 0) Debug.Log(BuildReport(), this);
    }

    // ---------------- hidden from the cameras that feed the model ----------------

    void CacheRenderers()
    {
        renderers = GetComponentsInChildren<Renderer>(true);
        hideWas = new bool[renderers.Length];
    }

    void OnBeginCamera(ScriptableRenderContext context, Camera cam) => HideFor(cam);
    void OnEndCamera(ScriptableRenderContext context, Camera cam) => ShowToAllCameras();
    void OnPreCullCamera(Camera cam) => HideFor(cam);
    void OnPostRenderCamera(Camera cam) => ShowToAllCameras();

    void HideFor(Camera cam)
    {
        if (!hideFromPoseCameras || hiddenNow || src == null || cam == null || !src.IsPoseCamera(cam)) return;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            hideWas[i] = r != null && r.enabled;
            if (hideWas[i]) r.enabled = false;
        }
        hiddenNow = true;
    }

    void ShowToAllCameras()
    {
        if (!hiddenNow) return;
        for (int i = 0; i < renderers.Length; i++)
            if (hideWas[i] && renderers[i] != null) renderers[i].enabled = true;
        hiddenNow = false;
    }

    public void ResetStatistics()
    {
        PipelineMs.Clear(); AgeMs.Clear(); ErrorMetres.Clear(); RawErrorMetres.Clear();
        ResetFitStatistics();
        truthFrames = insideFrames = 0;
        secondsSearching = secondsLost = secondsTracking = 0.0;
    }

    // ---------------- per frame ----------------

    void LateUpdate()
    {
        if (!Application.isPlaying) return;
        float dt = Time.deltaTime;

        if (!SourceAlive())
        {
            if (Time.unscaledTime >= nextResolve) { nextResolve = Time.unscaledTime + 0.5f; Resolve(); }
        }

        double now = src != null ? src.PoseClockNow : Time.timeAsDouble;
        if (src != null && src.TryGetPose(out PoseSample sample) && (sample.serial != lastSerial || sample.captureTime != lastCaptureTime))
        {
            lastSerial = sample.serial;
            lastCaptureTime = sample.captureTime;
            OnEstimate(sample, now);
        }

        SyncSettings();
        OperatorTrack.State state = track.Tick(now);
        bool justAcquired = state == OperatorTrack.State.Tracking && shown != OperatorTrack.State.Tracking;
        bool justLost = state == OperatorTrack.State.Lost && shown == OperatorTrack.State.Tracking;
        bool wasSearching = shown == OperatorTrack.State.Searching;
        bool firstLock = justAcquired && !hadTrack;
        shown = state;

        Place(state, now, dt, justAcquired && wasSearching);
        FitStep(state, dt);
        ApplyTint(tintWhenNotTracking && state != OperatorTrack.State.Tracking);

        if (justAcquired) { hadTrack = true; trackAcquired.Invoke(); }
        if (justLost) { lostAt = now; trackLost.Invoke(); }
        if (logTransitions && (justAcquired || justLost)) LogTransition(justAcquired, firstLock, now);

        Measure(state, now);
        UpdateDisplay(state, now);
        if (Time.realtimeSinceStartup >= nextReport)
        {
            nextReport = Time.realtimeSinceStartup + reportEverySeconds;
            Debug.Log(BuildReport(), this);
        }
    }

    void SyncSettings()
    {
        track.measurementNoise = measurementNoise;
        track.maneuverAccel = maneuverAccel;
        track.maxSpeed = maxSpeed;
        track.holdSeconds = holdSeconds;
        track.holdIntervals = holdIntervals;
        track.forgetSeconds = forgetSeconds;
        track.acquireRadius = acquireRadius;
        track.confirmEstimates = confirmEstimates;
    }

    void OnEstimate(PoseSample s, double now)
    {
        float real = Time.realtimeSinceStartup;
        if (lastEstimateReal > 0f)
        {
            float inst = 1f / Mathf.Max(1e-3f, real - lastEstimateReal);
            estimateRate = estimateRate <= 0f ? inst : Mathf.Lerp(estimateRate, inst, 0.2f);
        }
        lastEstimateReal = real;
        estimatesSeen++;
        if (s.latencyMs > 0f) PipelineMs.Add(s.latencyMs);

        cands.Clear();
        if (s.people != null)
            for (int i = 0; i < s.people.Count; i++)
            {
                FvpPerson p = s.people[i];
                if (p == null || p.phantom) continue;
                if (TryAnchor(p, out Vector3 a))
                    cands.Add(new OperatorTrack.Candidate { id = p.id, pos = a, quality = p.meanSupport >= 0f ? p.meanSupport : Mathf.Clamp01(p.score) });
            }

        SyncSettings();
        bool matched = track.Update(cands, s.captureTime, new Vector2(transform.position.x, transform.position.z), claimedFn, now);
        if (matched) FitObserve(s);
        if (logTransitions) lastCandidates = Describe(cands, s.people != null ? s.people.Count : 0);
    }

    static string Describe(List<OperatorTrack.Candidate> list, int people)
    {
        if (list.Count == 0) return people == 0 ? "nobody" : people + " people, none with enough valid joints";
        var sb = new StringBuilder();
        for (int i = 0; i < list.Count && i < 4; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('#').Append(list[i].id).Append(" at (").Append(list[i].pos.x.ToString("F2", CultureInfo.InvariantCulture)).Append(", ").Append(list[i].pos.z.ToString("F2", CultureInfo.InvariantCulture)).Append(')');
        }
        return sb.ToString();
    }

    string lastCandidates = "";
    double lostAt;      // scene time of the last Tracking -> Lost

    void LogTransition(bool acquired, bool first, double now)
    {
        Vector2 at = track.PredictXZ(now, maxPredictSeconds);
        if (acquired && first)
            Debug.Log($"[PoseEstimateFollower] '{name}' follows person #{track.LockedId} at ({at.x:F2}, {at.y:F2}), {Time.realtimeSinceStartup - startedReal:F1} s after it started looking; last estimate: {lastCandidates}", this);
        else if (acquired)
            Debug.Log($"[PoseEstimateFollower] '{name}' follows person #{track.LockedId} again ({now - lostAt:F2} s after it called the operator lost) at ({at.x:F2}, {at.y:F2}); last estimate: {lastCandidates}", this);
        else
            Debug.Log($"[PoseEstimateFollower] '{name}' lost person #{track.LockedId}: nothing matched the operator for {track.LostAfterSeconds:F1} s (estimates come every {track.EstimateInterval * 1000.0:F0} ms). " +
                      $"The operator was expected at ({at.x:F2}, {at.y:F2}); last estimate had: {lastCandidates}", this);
    }

    // Where the object should be and how it gets there.
    void Place(OperatorTrack.State state, double now, float dt, bool snap)
    {
        if (state == OperatorTrack.State.Searching) { placed = false; return; }      // not following anyone: stay (the search looks around where it is)

        Vector3 goal;
        float smooth = smoothTime;
        if (state == OperatorTrack.State.Lost && lostBehaviour == LostBehaviour.ReturnHome)
        {
            goal = new Vector3(home.x, startY, home.z);
            smooth = 0.6f;
        }
        else
        {
            Vector2 p = predictAhead ? track.PredictXZ(now, maxPredictSeconds) : track.Position;
            goal = new Vector3(p.x + offset.x, startY, p.y + offset.y);
        }
        if (fitReady) goal.y = transform.position.y;      // the height belongs to the fit (FitStep), not to the smoothing

        Vector3 pos = transform.position;
        if (snap || !placed || smooth <= 0f) { pos = goal; smoothVel = Vector3.zero; }
        else if (dt > 0f) pos = Vector3.SmoothDamp(pos, goal, ref smoothVel, smooth, float.PositiveInfinity, dt);
        placed = true;
        transform.position = pos;
    }

    // ---------------- the source ----------------

    bool SourceAlive()
    {
        if (src == null) return false;
        if (srcObject == null) return false;                       // destroyed
        var mb = srcObject as MonoBehaviour;
        return mb != null && mb.isActiveAndEnabled;
    }

    void Resolve()
    {
        IPoseSource found = null;
        MonoBehaviour mb = null;
        if (sourceOverride is IPoseSource over) { found = over; mb = sourceOverride; }
        else
        {
            switch (source)
            {
                case SourceChoice.FasterVoxelPoseLite: mb = FindActive<FasterVoxelPoseLite>(); break;
                case SourceChoice.FasterVoxelPose: mb = FindActive<FasterVoxelPoseLive>(); break;
                case SourceChoice.ViTPose: mb = FindActive<ViTPoseLive>(); break;
                default:
                    mb = FindActive<FasterVoxelPoseLite>();
                    if (mb == null) mb = FindActive<FasterVoxelPoseLive>();
                    if (mb == null) mb = FindActive<ViTPoseLive>();
                    break;
            }
            found = mb as IPoseSource;
        }
        if (found == null || mb == null || !mb.isActiveAndEnabled) return;

        if (!ReferenceEquals(found, src))
        {
            src = found; srcObject = mb;
            track.Reset();                       // another model numbers its people differently
            lastSerial = int.MinValue; lastCaptureTime = double.NaN;
            Debug.Log($"[PoseEstimateFollower] '{name}' follows the people found by {found.PoseSourceName}.", this);
        }
    }

    static T FindActive<T>() where T : MonoBehaviour
    {
        T t = FindFirstObjectByType<T>();
        return t != null && t.isActiveAndEnabled ? t : null;
    }

    // Another follower on the same model already has this body.
    bool ClaimedByOther(int id)
    {
        for (int i = 0; i < Active.Count; i++)
        {
            PoseEstimateFollower f = Active[i];
            if (f == this || f.src == null || !ReferenceEquals(f.src, src)) continue;
            if (f.track.Current != OperatorTrack.State.Searching && f.track.LockedId == id) return true;
        }
        return false;
    }

    // ---------------- the followed point ----------------

    bool TryAnchor(FvpPerson p, out Vector3 point)
    {
        int[] set = anchor == AnchorKind.MidHip ? MidHipJoints : anchor == AnchorKind.Neck ? NeckJoints : anchor == AnchorKind.AllJoints ? AllJointsSet : TorsoJoints;
        Vector3 sum = Vector3.zero;
        int n = 0;
        for (int i = 0; i < set.Length; i++)
        {
            if (!p.IsValid(set[i])) continue;
            sum += p.joints[set[i]];
            n++;
        }
        point = n > 0 ? sum / n : default;
        return n >= Mathf.Min(minAnchorJoints, set.Length);
    }

    // ---------------- measuring ----------------

    bool TryTruth(out Vector3 point)
    {
        point = default;
        if (truthAvatar == null || !truthAvatar.isActiveAndEnabled) return false;
        if (truthReader == null || truthBound != truthAvatar)
        {
            truthReader = new FvpGroundTruth();
            truthReader.SetAvatars(new[] { truthAvatar });
            truthBound = truthAvatar;
        }
        truthReader.Read(truthPeople);
        return truthPeople.Count > 0 && TryAnchor(truthPeople[0], out point);
    }

    void Measure(OperatorTrack.State state, double now)
    {
        float unscaled = Time.unscaledDeltaTime;
        if (state == OperatorTrack.State.Searching) secondsSearching += unscaled;
        else if (state == OperatorTrack.State.Lost) secondsLost += unscaled;
        else
        {
            secondsTracking += unscaled;
            AgeMs.Add((float)track.Staleness(now) * 1000f);
        }

        if (!hadTrack || !TryTruth(out Vector3 truth)) return;
        Vector2 t2 = new Vector2(truth.x, truth.z);
        Vector2 here = new Vector2(transform.position.x - offset.x, transform.position.z - offset.y);
        float err = Vector2.Distance(here, t2);
        ErrorMetres.Add(err);
        if (state != OperatorTrack.State.Searching) RawErrorMetres.Add(Vector2.Distance(track.LastMeasurement, t2));
        truthFrames++;
        if (err <= CapsuleRadius - bodyHalfWidth) insideFrames++;
        if (truthPeople.Count > 0) MeasureFit(truthPeople[0], state);
    }

    // ---------------- the tint ----------------

    void ApplyTint(bool on)
    {
        if (on == tinted) return;
        if (renderers == null) CacheRenderers();
        if (on)
        {
            if (block == null) block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", notTrackingTint);     // URP Lit / Unlit
            block.SetColor("_Color", notTrackingTint);         // built-in shaders
        }
        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null) renderers[i].SetPropertyBlock(on ? block : null);
        tinted = on;
    }

    // ---------------- the answer to "is it real time?" ----------------

    public string BuildReport()
    {
        var sb = new StringBuilder(900);
        float radius = CapsuleRadius;
        sb.Append("PoseEstimateFollower on '").Append(name).Append("' - ").Append(SourceName).Append(": ")
          .Append(estimatesSeen).Append(" estimates, ").Append(estimateRate.ToString("F1", CultureInfo.InvariantCulture)).AppendLine("/s");

        if (PipelineMs.Count > 0)
            sb.AppendFormat(CultureInfo.InvariantCulture,"  latency, capture -> people in hand:  median {0:F0} ms, p95 {1:F0}, max {2:F0}\n",
                PipelineMs.Percentile(0.5f), PipelineMs.Percentile(0.95f), PipelineMs.Max);
        if (AgeMs.Count > 0)
            sb.AppendFormat(CultureInfo.InvariantCulture,"  age of what the capsule is placed from (latency + wait for the next estimate): median {0:F0} ms, p95 {1:F0}, max {2:F0}\n",
                AgeMs.Percentile(0.5f), AgeMs.Percentile(0.95f), AgeMs.Max);

        double total = secondsSearching + secondsTracking + secondsLost;
        sb.AppendFormat(CultureInfo.InvariantCulture,"  time: following {0:F1} s, searching {1:F1} s, operator lost {2:F1} s; acquired {3}x, lost {4}x\n",
            secondsTracking, secondsSearching, secondsLost, track.Acquisitions, track.Losses);

        if (truthFrames > 0)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture,"  capsule axis vs the real operator's {0}:  median {1:F1} cm, p95 {2:F1}, p99 {3:F1}, max {4:F1}\n",
                anchor.ToString().ToLowerInvariant(), ErrorMetres.Percentile(0.5f) * 100f, ErrorMetres.Percentile(0.95f) * 100f,
                ErrorMetres.Percentile(0.99f) * 100f, ErrorMetres.Max * 100f);
            if (RawErrorMetres.Count > 0)
                sb.AppendFormat(CultureInfo.InvariantCulture,"  the newest estimate as it is, no filter, no extrapolation:  median {0:F1} cm, p95 {1:F1}, p99 {2:F1}, max {3:F1}\n",
                    RawErrorMetres.Percentile(0.5f) * 100f, RawErrorMetres.Percentile(0.95f) * 100f, RawErrorMetres.Percentile(0.99f) * 100f, RawErrorMetres.Max * 100f);
            sb.AppendFormat(CultureInfo.InvariantCulture,"  operator's body (axis + {0:F2} m) inside the capsule (radius {1:F2} m): {2:F1} % of {3} frames since the first lock;\n" +
                            "  radius needed for 99 % of them: {4:F2} m\n",
                bodyHalfWidth, radius, 100.0 * insideFrames / truthFrames, truthFrames, ErrorMetres.Percentile(0.99f) + bodyHalfWidth);
        }
        else sb.Append("  (no truth avatar: nothing to compare the capsule with)\n");
        AppendFitReport(sb);
        if (total > 0.0) sb.AppendFormat(CultureInfo.InvariantCulture,"  of the session, the capsule knew where the operator was {0:F1} % of the time", 100.0 * secondsTracking / total);
        return sb.ToString();
    }

    [ContextMenu("Print report")]
    void PrintReport() { Debug.Log(BuildReport(), this); }

    [ContextMenu("Reset statistics")]
    void ResetStatsMenu() { ResetStatistics(); }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos || !Application.isPlaying) return;
        float y = transform.position.y;
        Vector3 here = transform.position;

        Gizmos.color = new Color(1f, 0.85f, 0.1f, 0.9f);       // yellow: the newest estimate exactly as it was
        Vector3 raw = new Vector3(track.LastMeasurement.x, y, track.LastMeasurement.y);
        Gizmos.DrawWireSphere(raw, 0.12f);
        Gizmos.DrawLine(here, raw);

        if (truthAvatar != null && TryTruth(out Vector3 truth))
        {
            Gizmos.color = new Color(0.2f, 1f, 0.3f, 0.9f);   // green: the real operator
            Vector3 t = new Vector3(truth.x, y, truth.z);
            Gizmos.DrawWireSphere(t, 0.12f);
            Gizmos.DrawLine(here, t);
        }
    }
}
