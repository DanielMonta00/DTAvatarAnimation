using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Generates a position trajectory for a 4-cable robot, validates it against the machine's
/// wrench-feasible workspace, plays it back, and saves it as JSON.
///
/// Modelled on FPDatasetSimulator/TrajectoryGenerator, with one important difference: a camera
/// trajectory only has to exist, whereas a cable robot trajectory has to be *executable*. Every
/// sample is run through CableRobotSolver, so a path that strays out of the workspace — too close
/// to the ceiling, past a ceiling anchor, or demanding more tension than the motors can deliver —
/// is flagged before it ever reaches a motor controller.
///
/// The saved JSON carries the cable lengths, tensions and drum angles per frame, not just the
/// positions. Those four drum angles are the actual command stream for the real rig.
///
/// Attach next to a CableRobot. Configure, then use the context menus or call Generate().
/// </summary>
[RequireComponent(typeof(CableRobot))]
public class CableRobotTrajectory : MonoBehaviour
{
    const int N = CableRobotSolver.CableCount;

    public enum TrajectoryType
    {
        /// <summary>Horizontal circle at a fixed height.</summary>
        Orbital,
        /// <summary>Circle while rising, sweeping a cylinder of the workspace.</summary>
        Helicoidal,
        /// <summary>Circle with a vertical sinusoidal wiggle. Exercises the vertical axis hardest.</summary>
        OrbitSinusoidalY,
        /// <summary>Classic three-axis Lissajous figure. The standard workspace-coverage test for cable robots.</summary>
        Lissajous,
        /// <summary>Boustrophedon sweep of a rectangle at fixed height, as a 3D printer or scanner would move.</summary>
        RasterScan,
    }

    public enum PlaybackMode
    {
        /// <summary>Platform is driven through CableRobot.StepTowards, so cable speed limits apply and the robot lags a trajectory it cannot keep up with. This is what the real machine does.</summary>
        RespectMotorLimits,
        /// <summary>Platform is placed exactly on each waypoint. Shows the commanded path, ignoring whether the motors could achieve it.</summary>
        IdealTracking,
    }

    [Header("Trajectory")]
    public TrajectoryType trajectoryType = TrajectoryType.Lissajous;
    [Tooltip("Number of samples. With playbackFPS this also sets the duration.")]
    public int numFrames = 600;
    [Tooltip("Centre of the trajectory. Leave empty to use the centroid of the four anchors at 'height'.")]
    public Transform center;
    [Tooltip("Height of the trajectory centre, in world metres. Ignored when 'center' is assigned.")]
    public float height = 1.2f;

    [Header("Orbital / Helicoidal / Sinusoidal")]
    [Tooltip("A circle is limited by the NARROWEST horizontal axis of the workspace, so on a non-square anchor rectangle it wastes reach along the long axis. Use Lissajous if you want to fill the workspace. Default suits the 4x3 m rig from the rig builder.")]
    public float radius = 0.9f;
    [Tooltip("Full revolutions over the whole trajectory.")]
    public int orbits = 3;
    [Tooltip("Total rise over the trajectory, in metres. Helicoidal only. Negative descends. The circle must stay feasible at its HIGHEST point, where tensions are largest.")]
    public float helicoidalRise = 0.5f;
    [Tooltip("Amplitude of the vertical wiggle, in metres. OrbitSinusoidalY only.")]
    public float wiggleAmplitude = 0.3f;
    [Tooltip("Vertical oscillations per revolution. OrbitSinusoidalY only.")]
    public float wiggleFrequency = 3f;

    [Header("Lissajous")]
    [Tooltip("Half-extent along X, Y, Z in metres.")]
    public Vector3 lissajousAmplitude = new Vector3(1.2f, 0.4f, 0.9f);
    [Tooltip("Frequency ratio per axis. Keep these coprime (e.g. 3,2,5) or the figure closes early and covers less.")]
    public Vector3 lissajousFrequency = new Vector3(3f, 2f, 5f);
    [Tooltip("Phase offset per axis, in degrees.")]
    public Vector3 lissajousPhase = new Vector3(90f, 0f, 0f);

    [Header("Raster Scan")]
    public Vector2 rasterSize = new Vector2(2.4f, 1.8f);
    [Range(2, 40)] public int rasterLines = 8;

    [Header("Playback")]
    public bool playOnStart = true;
    public PlaybackMode playbackMode = PlaybackMode.RespectMotorLimits;
    [Tooltip("Waypoints consumed per second. Combined with numFrames this sets how fast the commanded path moves — and therefore whether the motors can keep up.")]
    public float playbackFPS = 60f;
    public bool loop = true;

    [Header("Output")]
    public string outputFolder = "Trajectories";
    public int trajectoryIndex = 1;

    [Header("Visualization")]
    public bool showTrajectoryGizmo = true;
    [Tooltip("Colour of path segments the robot can execute.")]
    public Color feasibleColor = new Color(0.2f, 1f, 0.4f);
    [Tooltip("Colour of path segments outside the wrench-feasible workspace. If you see any, the real machine would fail there.")]
    public Color infeasibleColor = new Color(1f, 0.25f, 0.2f);

    // ---------------- State ----------------

    CableRobot robot;
    readonly List<Vector3> waypoints = new List<Vector3>();
    readonly List<bool> feasible = new List<bool>();

    // Scratch, so sampling never allocates.
    readonly float[] sLengths = new float[N];
    readonly float[] sTensions = new float[N];
    readonly float[] sAngles = new float[N];

    bool playing;
    float playTime;
    // Set by OnValidate so the Scene view rebuilds the path when an inspector value changes.
    bool dirty = true;

    public int InfeasibleCount { get { int c = 0; foreach (bool f in feasible) if (!f) c++; return c; } }

    // ---------------- Unity ----------------

    void Reset() => robot = GetComponent<CableRobot>();

    void Start()
    {
        robot = GetComponent<CableRobot>();
        // The robot's own target-following would fight us for the platform transform.
        robot.followTarget = false;

        if (waypoints.Count == 0) Build();
        playing = playOnStart;
        playTime = 0f;
    }

    void Update()
    {
        if (!Application.isPlaying || !playing || waypoints.Count == 0 || robot == null || robot.platform == null) return;

        playTime += Time.deltaTime;
        float exact = playTime * playbackFPS;

        if (exact >= waypoints.Count - 1)
        {
            if (!loop) { playing = false; return; }
            playTime = 0f;
            exact = 0f;
        }

        // Interpolate between waypoints so playbackFPS need not match the frame rate.
        int i = Mathf.FloorToInt(exact);
        Vector3 commanded = Vector3.Lerp(waypoints[i], waypoints[Mathf.Min(i + 1, waypoints.Count - 1)], exact - i);

        robot.platform.position = playbackMode == PlaybackMode.IdealTracking
            ? commanded
            : robot.StepTowards(robot.platform.position, commanded, Time.deltaTime);
    }

    // ---------------- Public API ----------------

    [ContextMenu("Preview Trajectory (no save)")]
    public void PreviewTrajectory()
    {
        Build();
        int bad = InfeasibleCount;
        if (bad == 0)
            Debug.Log($"CableRobotTrajectory: {numFrames} waypoints for {trajectoryType}, all inside the wrench-feasible workspace.", this);
        else
            Debug.LogWarning($"CableRobotTrajectory: {bad}/{numFrames} waypoints ({100f * bad / numFrames:F1}%) are OUTSIDE the workspace — the robot cannot execute this path. Shown in red in the Scene view. Reduce radius/amplitude, lower the trajectory away from the ceiling, or raise tensionMax.", this);
    }

    [ContextMenu("Generate and Save JSON")]
    public void Generate()
    {
        Build();
        if (robot == null || robot.platform == null)
        {
            Debug.LogError("CableRobotTrajectory: no CableRobot / platform assigned.", this);
            return;
        }

        var file = new TrajectoryFile
        {
            trajectory_type = trajectoryType.ToString(),
            num_frames = waypoints.Count,
            playback_fps = playbackFPS,
            duration_seconds = waypoints.Count / Mathf.Max(1f, playbackFPS),
            drum_radius_m = robot.drumRadius,
            payload_mass_kg = robot.payloadMass,
            infeasible_frames = InfeasibleCount,
            waypoints = new List<Waypoint>(waypoints.Count),
        };

        for (int i = 0; i < waypoints.Count; i++)
        {
            var status = robot.Sample(waypoints[i], sLengths, sTensions, sAngles);
            file.waypoints.Add(new Waypoint
            {
                frame = i,
                time = i / Mathf.Max(1f, playbackFPS),
                position = new[] { waypoints[i].x, waypoints[i].y, waypoints[i].z },
                cable_lengths_m = (float[])sLengths.Clone(),
                cable_tensions_n = (float[])sTensions.Clone(),
                drum_angles_deg = (float[])sAngles.Clone(),
                feasible = status == CableRobotSolver.Status.Feasible,
            });
        }

        string dir = Path.Combine(Application.dataPath, "..", outputFolder);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"cdpr_{trajectoryType.ToString().ToLower()}_{trajectoryIndex}.json");
        File.WriteAllText(path, JsonUtility.ToJson(file, true));

        Debug.Log($"CableRobotTrajectory: saved {waypoints.Count} waypoints to {path} ({file.infeasible_frames} infeasible).", this);
    }

    [ContextMenu("Generate All Trajectory Types")]
    public void GenerateAll()
    {
        var original = trajectoryType;
        foreach (TrajectoryType t in Enum.GetValues(typeof(TrajectoryType)))
        {
            trajectoryType = t;
            Generate();
        }
        trajectoryType = original;
        Build();
    }

    // ---------------- Trajectory construction ----------------

    /// <summary>Rebuilds the waypoint list and re-runs the feasibility check on every sample.</summary>
    public void Build()
    {
        if (robot == null) robot = GetComponent<CableRobot>();

        waypoints.Clear();
        feasible.Clear();
        dirty = false;
        if (numFrames < 2) return;

        Vector3 c = ResolveCenter();
        for (int i = 0; i < numFrames; i++)
        {
            float t = i / (float)(numFrames - 1);
            Vector3 p = trajectoryType switch
            {
                TrajectoryType.Helicoidal        => Helicoidal(t, c),
                TrajectoryType.OrbitSinusoidalY  => OrbitSinusoidalY(t, c),
                TrajectoryType.Lissajous         => Lissajous(t, c),
                TrajectoryType.RasterScan        => RasterScan(t, c),
                _                                => Orbital(t, c),
            };
            waypoints.Add(p);
            feasible.Add(robot != null && robot.IsFeasible(p));
        }
    }

    // With coplanar ceiling anchors the workspace is the pyramid hanging beneath them, so the
    // anchor centroid is the widest place to centre a trajectory.
    Vector3 ResolveCenter()
    {
        if (center != null) return center.position;
        if (robot == null || robot.anchors == null) return new Vector3(0f, height, 0f);

        Vector3 sum = Vector3.zero;
        int n = 0;
        foreach (var a in robot.anchors)
            if (a != null) { sum += a.position; n++; }

        if (n == 0) return new Vector3(0f, height, 0f);
        Vector3 centroid = sum / n;
        return new Vector3(centroid.x, height, centroid.z);
    }

    Vector3 Orbital(float t, Vector3 c)
    {
        float a = t * orbits * 2f * Mathf.PI;
        return c + new Vector3(Mathf.Sin(a) * radius, 0f, Mathf.Cos(a) * radius);
    }

    Vector3 Helicoidal(float t, Vector3 c)
    {
        float a = t * orbits * 2f * Mathf.PI;
        return c + new Vector3(Mathf.Sin(a) * radius, t * helicoidalRise, Mathf.Cos(a) * radius);
    }

    Vector3 OrbitSinusoidalY(float t, Vector3 c)
    {
        float a = t * orbits * 2f * Mathf.PI;
        float y = Mathf.Sin(a * wiggleFrequency) * wiggleAmplitude;
        return c + new Vector3(Mathf.Sin(a) * radius, y, Mathf.Cos(a) * radius);
    }

    Vector3 Lissajous(float t, Vector3 c)
    {
        float a = t * 2f * Mathf.PI;
        Vector3 ph = lissajousPhase * Mathf.Deg2Rad;
        return c + new Vector3(
            lissajousAmplitude.x * Mathf.Sin(lissajousFrequency.x * a + ph.x),
            lissajousAmplitude.y * Mathf.Sin(lissajousFrequency.y * a + ph.y),
            lissajousAmplitude.z * Mathf.Sin(lissajousFrequency.z * a + ph.z));
    }

    // Boustrophedon: alternate lines run in opposite directions, so the path is continuous and
    // the platform never has to fly back across the workspace between lines.
    Vector3 RasterScan(float t, Vector3 c)
    {
        float u = t * rasterLines;               // which line we are on, plus progress along it
        int line = Mathf.Min(Mathf.FloorToInt(u), rasterLines - 1);
        float along = u - line;
        if (line % 2 == 1) along = 1f - along;   // reverse every other line

        float x = Mathf.Lerp(-rasterSize.x * 0.5f, rasterSize.x * 0.5f, along);
        float z = rasterLines > 1
            ? Mathf.Lerp(-rasterSize.y * 0.5f, rasterSize.y * 0.5f, line / (float)(rasterLines - 1))
            : 0f;
        return c + new Vector3(x, 0f, z);
    }

    // ---------------- Gizmos ----------------

    void OnValidate()
    {
        numFrames = Mathf.Max(2, numFrames);
        orbits = Mathf.Max(1, orbits);
        if (robot == null) robot = GetComponent<CableRobot>();
        dirty = true;
    }

    void OnDrawGizmos()
    {
        if (!showTrajectoryGizmo) return;
        // Never rebuild mid-playback: Build() would stall on numFrames feasibility solves,
        // and the path cannot change while it is being executed anyway.
        if ((dirty || waypoints.Count < 2) && !playing) Build();
        if (waypoints.Count < 2) return;

        // Colour each segment by whether the robot could actually hold both of its endpoints.
        for (int i = 0; i < waypoints.Count - 1; i++)
        {
            bool ok = feasible[i] && feasible[i + 1];
            Gizmos.color = ok ? feasibleColor : infeasibleColor;
            Gizmos.DrawLine(waypoints[i], waypoints[i + 1]);
        }

        Gizmos.color = Color.green;
        Gizmos.DrawSphere(waypoints[0], 0.04f);
        Gizmos.color = Color.red;
        Gizmos.DrawSphere(waypoints[waypoints.Count - 1], 0.04f);

        Gizmos.color = new Color(1f, 0f, 1f, 0.6f);
        Gizmos.DrawWireSphere(ResolveCenter(), 0.06f);
    }

    // ---------------- Serialization ----------------

    [Serializable]
    public class Waypoint
    {
        public int frame;
        public float time;
        public float[] position;          // x, y, z world metres
        public float[] cable_lengths_m;   // one per cable, anchor order
        public float[] cable_tensions_n;
        public float[] drum_angles_deg;   // theta = l / r; the motor command stream
        public bool feasible;
    }

    [Serializable]
    public class TrajectoryFile
    {
        public string trajectory_type;
        public int num_frames;
        public float playback_fps;
        public float duration_seconds;
        public float drum_radius_m;
        public float payload_mass_kg;
        public int infeasible_frames;
        public List<Waypoint> waypoints;
    }
}
