using UnityEngine;

/// <summary>
/// Drives a 4-cable suspended cable-driven parallel robot (CDPR) from a target position,
/// enforcing the constraints a real machine has: cables can only pull, motors have finite
/// tension and finite payout speed.
///
/// The platform is moved kinematically. Rather than letting physics find the position, we
/// pick a candidate position, run the inverse kinematics, and reject it if any cable would
/// go slack, overload, or have to spool faster than its motor allows. What survives is a
/// motion that a real rig could actually execute.
///
/// Cable attachment:
///   - Leave <see cref="cableAttachPoints"/> empty and all four cables converge on the
///     platform origin. This is the exact single-point (4-1) model: no moments, the point
///     mass assumption holds, and the platform is free to swing and spin.
///   - Assign them (e.g. to the four corners of a plate) and cable lengths are measured
///     from those real attachment points. Force balance is unchanged; the moment balance
///     is approximated by assuming the plate hangs level. Good enough to look and behave
///     right, but it is an approximation — see CableRobotSolver's summary.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class CableRobot : MonoBehaviour
{
    const int N = CableRobotSolver.CableCount;

    [Header("Geometry")]
    [Tooltip("The four cable exit points, normally pulleys at the ceiling corners. Order defines cable index 0..3.")]
    public Transform[] anchors = new Transform[N];
    [Tooltip("The moving platform. Its position is what the robot controls.")]
    public Transform platform;
    [Tooltip("Optional. Where each cable meets the platform, e.g. the corners of a plate. Leave empty for the exact single-point model where all cables converge on the platform origin.")]
    public Transform[] cableAttachPoints = new Transform[N];
    [Tooltip("Optional. Winch drums, spun to visualise cable payout.")]
    public Transform[] drums = new Transform[N];
    [Tooltip("Local axis each drum spins about. Unity's built-in Cylinder mesh has its axis along Y, so leave this as (0,1,0) for spools built from primitives.")]
    public Vector3 drumSpinAxis = Vector3.up;

    [Header("Payload and motors")]
    [Tooltip("Total suspended mass in kg (plate + actuator + payload). Sets the weight the cables must carry.")]
    public float payloadMass = 5f;
    [Tooltip("Winch drum radius in metres. Converts cable length to motor angle: theta = l / r.")]
    public float drumRadius = 0.04f;
    [Tooltip("Minimum cable tension in newtons. Keep above zero: a cable at exactly 0 N is slack and the platform is momentarily uncontrolled.")]
    public float tensionMin = 5f;
    [Tooltip("Maximum cable tension in newtons, set by motor torque / cable breaking strength. This is what trims the workspace near the ceiling corners.")]
    public float tensionMax = 500f;
    [Tooltip("Maximum cable payout/reel-in speed in m/s, set by motor RPM and drum radius. This limits how fast the platform can move, direction by direction.")]
    public float maxCableSpeed = 1.5f;
    [Tooltip("Shortest allowed cable length in metres. Stops the platform being commanded into a pulley.")]
    public float minCableLength = 0.3f;
    [Tooltip("Longest allowed cable length in metres, i.e. how much cable is on the drum.")]
    public float maxCableLength = 30f;

    [Header("Target following (play mode)")]
    [Tooltip("Platform moves toward this transform each frame, as far as the cable speed limit and the workspace allow.")]
    public Transform target;
    public bool followTarget = true;
    [Tooltip("Upper bound on platform speed in m/s, applied before the per-cable speed limit.")]
    public float maxPlatformSpeed = 2f;

    [Header("Visualization")]
    public bool drawCables = true;
    public float cableWidth = 0.008f;
    public Color feasibleColor = new Color(0.2f, 1f, 0.4f);
    public Color infeasibleColor = new Color(1f, 0.25f, 0.2f);
    [Tooltip("Sample a horizontal grid at the platform's height and mark the positions the robot can actually hold. Editor gizmo only.")]
    public bool drawWorkspaceSlice = false;
    [Range(4, 48)] public int workspaceSliceResolution = 20;

    // ---------------- Live state (read-only, useful for logging or a controller) ----------------

    public CableRobotSolver.Status Status { get; private set; } = CableRobotSolver.Status.Degenerate;
    /// <summary>Current cable lengths in metres, indexed by anchor.</summary>
    public float[] CableLengths => lengths;
    /// <summary>Current cable tensions in newtons. Meaningful even when infeasible: look for the entry outside [tensionMin, tensionMax].</summary>
    public float[] CableTensions => tensions;
    /// <summary>Absolute winch drum angles in degrees, theta = l / r. Proportional to total unspooled cable, not accumulated per-frame. This is what you would send to the motors.</summary>
    public float[] DrumAnglesDegrees => drumAngles;

    // Scratch buffers. Reused every frame and by the gizmo sampler, so nothing allocates.
    readonly Vector3[] anchorPos = new Vector3[N];
    readonly Vector3[] attachOffset = new Vector3[N];
    readonly Vector3[] attachPos = new Vector3[N];
    readonly Vector3[] dirs = new Vector3[N];
    readonly float[] lengths = new float[N];
    readonly float[] tensions = new float[N];
    readonly float[] drumAngles = new float[N];
    readonly Vector3[] probeAttach = new Vector3[N];
    readonly Vector3[] probeDirs = new Vector3[N];
    readonly float[] probeLengths = new float[N];
    readonly float[] probeTensions = new float[N];

    LineRenderer[] cableLines;
    Material cableMaterial;
    Quaternion[] drumRestRotation;

    void OnValidate()
    {
        if (anchors == null || anchors.Length != N) System.Array.Resize(ref anchors, N);
        if (cableAttachPoints == null || cableAttachPoints.Length != N) System.Array.Resize(ref cableAttachPoints, N);
        if (drums == null || drums.Length != N) System.Array.Resize(ref drums, N);
        tensionMax = Mathf.Max(tensionMax, tensionMin + 1f);
        maxCableLength = Mathf.Max(maxCableLength, minCableLength + 0.1f);
    }

    void OnEnable()
    {
        BuildCableLines();
        CacheDrumRest();
        if (IsConfigured())
        {
            ReadTransforms();
            Evaluate(platform.position, lengths, dirs, tensions);
        }
    }

    void OnDisable()
    {
        DestroyCableLines();
    }

    void Update()
    {
        if (!IsConfigured()) return;

        ReadTransforms();

        if (Application.isPlaying && followTarget && target != null)
            platform.position = StepTowards(platform.position, target.position, Time.deltaTime);

        Status = Evaluate(platform.position, lengths, dirs, tensions);

        for (int i = 0; i < N; i++)
            drumAngles[i] = CableRobotSolver.DrumAngleDegrees(lengths[i], drumRadius);

        ApplyDrumRotation();

        // Toggling drawCables (or a domain reload dropping the renderers) is reconciled here
        // rather than in OnValidate, which is not allowed to create or destroy GameObjects.
        if (drawCables && cableLines == null) BuildCableLines();
        else if (!drawCables && cableLines != null) DestroyCableLines();
        UpdateCableLines();
    }

    // ---------------- Public API ----------------

    /// <summary>True when the robot can hold the platform statically at <paramref name="worldPos"/>.</summary>
    public bool IsFeasible(Vector3 worldPos)
    {
        if (!IsConfigured()) return false;
        // Refresh cached transforms: callers may be editor tooling running outside Update.
        ReadTransforms();
        return Evaluate(worldPos, probeLengths, probeDirs, probeTensions) == CableRobotSolver.Status.Feasible;
    }

    /// <summary>
    /// Everything a trajectory logger needs about one candidate position, without touching any
    /// transform. Buffers must be length 4 and owned by the caller.
    /// </summary>
    public CableRobotSolver.Status Sample(Vector3 worldPos, float[] outLengths, float[] outTensions, float[] outDrumAngles)
    {
        if (!IsConfigured()) return CableRobotSolver.Status.Degenerate;
        ReadTransforms();
        var status = Evaluate(worldPos, outLengths, probeDirs, outTensions);
        for (int i = 0; i < N; i++)
            outDrumAngles[i] = CableRobotSolver.DrumAngleDegrees(outLengths[i], drumRadius);
        return status;
    }

    /// <summary>
    /// Full evaluation at an arbitrary world position, writing into caller-supplied buffers.
    /// Combines the wrench-feasibility test with the cable length limits, since a pose the
    /// tensions allow may still ask for more cable than the drum holds.
    /// </summary>
    public CableRobotSolver.Status Evaluate(Vector3 worldPos, float[] outLengths, Vector3[] outDirs, float[] outTensions)
    {
        for (int i = 0; i < N; i++) probeAttach[i] = worldPos + attachOffset[i];
        CableRobotSolver.InverseKinematics(probeAttach, anchorPos, outLengths, outDirs);

        for (int i = 0; i < N; i++)
            if (outLengths[i] < minCableLength || outLengths[i] > maxCableLength)
            {
                CableRobotSolver.SolveTensions(outDirs, RequiredForce(), tensionMin, tensionMax, outTensions);
                return CableRobotSolver.Status.Infeasible;
            }

        return CableRobotSolver.SolveTensions(outDirs, RequiredForce(), tensionMin, tensionMax, outTensions);
    }

    /// <summary>The resultant the cables must supply to hold a static platform: m * (0 - g).</summary>
    Vector3 RequiredForce() => payloadMass * -Physics.gravity;

    /// <summary>
    /// Moves toward <paramref name="goal"/> by at most one frame's worth of travel, backing off
    /// until the step is one the machine could really execute. Bisection converges on the current
    /// position, so a fully blocked robot simply stays put rather than teleporting or jittering.
    /// </summary>
    public Vector3 StepTowards(Vector3 current, Vector3 goal, float dt)
    {
        Vector3 desired = Vector3.MoveTowards(current, goal, maxPlatformSpeed * dt);
        float maxDeltaLength = maxCableSpeed * dt;

        // Cable lengths at the current (already valid) position, to measure payout against.
        for (int i = 0; i < N; i++) probeAttach[i] = current + attachOffset[i];
        CableRobotSolver.InverseKinematics(probeAttach, anchorPos, probeLengths, probeDirs);
        float l0 = probeLengths[0], l1 = probeLengths[1], l2 = probeLengths[2], l3 = probeLengths[3];

        Vector3 candidate = desired;
        for (int iter = 0; iter < 10; iter++)
        {
            if (Admissible(candidate, l0, l1, l2, l3, maxDeltaLength)) return candidate;
            candidate = Vector3.Lerp(current, candidate, 0.5f);
        }
        return current;
    }

    bool Admissible(Vector3 candidate, float l0, float l1, float l2, float l3, float maxDeltaLength)
    {
        if (Evaluate(candidate, probeLengths, probeDirs, probeTensions) != CableRobotSolver.Status.Feasible)
            return false;

        const float eps = 1e-5f;
        return Mathf.Abs(probeLengths[0] - l0) <= maxDeltaLength + eps
            && Mathf.Abs(probeLengths[1] - l1) <= maxDeltaLength + eps
            && Mathf.Abs(probeLengths[2] - l2) <= maxDeltaLength + eps
            && Mathf.Abs(probeLengths[3] - l3) <= maxDeltaLength + eps;
    }

    // ---------------- Internals ----------------

    bool IsConfigured()
    {
        if (platform == null || anchors == null || anchors.Length != N) return false;
        for (int i = 0; i < N; i++) if (anchors[i] == null) return false;
        return true;
    }

    // Attachment offsets are captured relative to the platform so a candidate position can be
    // evaluated without actually moving any transform.
    void ReadTransforms()
    {
        for (int i = 0; i < N; i++)
        {
            anchorPos[i] = anchors[i].position;
            Transform a = cableAttachPoints != null && cableAttachPoints.Length == N ? cableAttachPoints[i] : null;
            attachOffset[i] = a != null ? a.position - platform.position : Vector3.zero;
            attachPos[i] = platform.position + attachOffset[i];
        }
    }

    void CacheDrumRest()
    {
        if (drums == null || drums.Length != N) return;
        drumRestRotation = new Quaternion[N];
        for (int i = 0; i < N; i++)
            drumRestRotation[i] = drums[i] != null ? drums[i].localRotation : Quaternion.identity;
    }

    void ApplyDrumRotation()
    {
        if (drums == null || drumRestRotation == null || drums.Length != N) return;
        Vector3 axis = drumSpinAxis.sqrMagnitude > 1e-6f ? drumSpinAxis.normalized : Vector3.up;
        for (int i = 0; i < N; i++)
            if (drums[i] != null)
                drums[i].localRotation = drumRestRotation[i] * Quaternion.AngleAxis(drumAngles[i], axis);
    }

    // Cables are real renderers, not gizmos, so they show in the Game view and in a build. They are
    // created in edit mode too, which is when you are positioning anchors and most want to see them.
    // HideAndDontSave keeps them out of the hierarchy and out of the saved scene.
    void BuildCableLines()
    {
        DestroyCableLines();
        if (!drawCables) return;

        cableLines = new LineRenderer[N];
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        cableMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

        for (int i = 0; i < N; i++)
        {
            var go = new GameObject($"_Cable{i}") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(transform, worldPositionStays: false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.widthMultiplier = cableWidth;
            lr.sharedMaterial = cableMaterial;
            cableLines[i] = lr;
        }
    }

    void UpdateCableLines()
    {
        if (cableLines == null) return;
        Color c = Status == CableRobotSolver.Status.Feasible ? feasibleColor : infeasibleColor;
        for (int i = 0; i < N; i++)
        {
            if (cableLines[i] == null) continue;
            cableLines[i].SetPosition(0, anchorPos[i]);
            cableLines[i].SetPosition(1, attachPos[i]);
            cableLines[i].widthMultiplier = cableWidth;
            cableLines[i].startColor = cableLines[i].endColor = c;
        }
    }

    void DestroyCableLines()
    {
        if (cableLines != null)
        {
            foreach (var lr in cableLines)
            {
                if (lr == null) continue;
                if (Application.isPlaying) Destroy(lr.gameObject);
                else                       DestroyImmediate(lr.gameObject);
            }
            cableLines = null;
        }

        // The material is owned by this component, so it must go too, or every rebuild leaks one.
        if (cableMaterial != null)
        {
            if (Application.isPlaying) Destroy(cableMaterial);
            else                       DestroyImmediate(cableMaterial);
            cableMaterial = null;
        }
    }

    // ---------------- Gizmos ----------------

    void OnDrawGizmos()
    {
        if (!IsConfigured()) return;
        ReadTransforms();

        // Anchor rectangle, in index order, so a mis-ordered anchor array is obvious at a glance.
        Gizmos.color = new Color(1f, 1f, 1f, 0.5f);
        for (int i = 0; i < N; i++)
        {
            Gizmos.DrawLine(anchorPos[i], anchorPos[(i + 1) % N]);
            Gizmos.DrawWireSphere(anchorPos[i], 0.05f);
        }

        var status = Evaluate(platform.position, probeLengths, probeDirs, probeTensions);
        Gizmos.color = status == CableRobotSolver.Status.Feasible ? feasibleColor : infeasibleColor;
        // Only draw gizmo cables as a fallback: with drawCables on, the LineRenderers already
        // show them, and drawing both puts a thin gizmo line inside every cable.
        if (!drawCables)
            for (int i = 0; i < N; i++) Gizmos.DrawLine(anchorPos[i], attachPos[i]);
        Gizmos.DrawWireSphere(platform.position, 0.06f);

        if (drawWorkspaceSlice) DrawWorkspaceSlice();
    }

    // Samples a horizontal grid at the platform's height and dots every position the robot can
    // hold. Because the anchors are coplanar, this slice shrinks toward the ceiling and widens
    // below — the visible signature of a gravity-closed (suspended) cable robot.
    void DrawWorkspaceSlice()
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < N; i++)
        {
            minX = Mathf.Min(minX, anchorPos[i].x); maxX = Mathf.Max(maxX, anchorPos[i].x);
            minZ = Mathf.Min(minZ, anchorPos[i].z); maxZ = Mathf.Max(maxZ, anchorPos[i].z);
        }

        int r = workspaceSliceResolution;
        float y = platform.position.y;
        float cell = Mathf.Min(maxX - minX, maxZ - minZ) / r * 0.35f;
        Gizmos.color = new Color(feasibleColor.r, feasibleColor.g, feasibleColor.b, 0.35f);

        for (int ix = 0; ix <= r; ix++)
        for (int iz = 0; iz <= r; iz++)
        {
            Vector3 p = new Vector3(
                Mathf.Lerp(minX, maxX, ix / (float)r), y,
                Mathf.Lerp(minZ, maxZ, iz / (float)r));
            if (Evaluate(p, probeLengths, probeDirs, probeTensions) == CableRobotSolver.Status.Feasible)
                Gizmos.DrawCube(p, Vector3.one * cell);
        }
    }
}
