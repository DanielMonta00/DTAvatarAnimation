using System.Globalization;
using System.Text;
using UnityEngine;

// The size of the capsule. The follower decides WHERE the capsule is; this part decides how big, from the body the pose model found.
// THE SIZE IS A FUNCTION OF THE DETECTED SKELETON ONLY. Inputs: the 15 joints of the followed person in the pose source's estimates, and the
// settings on this component (margin, limits, shrink time). Not the Truth Avatar (it only measures, in MeasureFit, and moves nothing), not the
// scene's colliders or renderers, not the authored size (except as the size to go back to while no skeleton is followed, and, with Fit
// Bottom off, as a lower limit for the bottom tip). Tested: the same joints give the same capsule whatever the authored size
// (Server~/track_eval, "fit", scenario K), and the Unity run keeps Truth Avatar and the scene's body out of it.
//
//   estimate --> the followed person's 15 joints --> FvpCapsuleSizer (smallest capsule with every joint's reach inside, memory of dropped
//                 joints, grows at once / shrinks slowly) --> this object's scale and height, every rendered frame
//
// The joints are measured from the capsule's axis, not from where the estimate put the body: a capsule that is 2 cm off its person does not
// let their hand out, and the latency of the pipeline does not change how far a hand is from the torso. What the position error costs is
// covered by Fit Margin. The object is scaled through its transform (a child, like the scene's bigger outer bubble, scales with it); its
// authored scale and height are put back when the component is disabled. See FvpCapsuleFit for the geometry and Server~/track_eval ("fit").
public sealed partial class PoseEstimateFollower
{
    readonly FvpCapsuleSizer sizer = new FvpCapsuleSizer();
    readonly FvpCapsuleFit.Ball[] truthBalls = new FvpCapsuleFit.Ball[FvpSkeleton.Count];
    bool fitReady;
    string fitWhy = "off";
    Vector3 fitAuthoredScale;
    float fitAuthoredY;
    int fitBodyId = int.MinValue;
    long fitFrames, fitInsideFrames, fitClampedFrames;

    // The size drawn (radius of the cylinder, tip to tip) while a person is followed; and, with a truth avatar, how far the real body
    // stuck out of it when it did (extra radius it would have needed).
    public readonly FvpHistogram FitRadiusMetres = new FvpHistogram(0f, 2f, 400);
    public readonly FvpHistogram FitHeightMetres = new FvpHistogram(0f, 4f, 400);
    public readonly FvpHistogram FitShortfallMetres = new FvpHistogram(0f, 1f, 200);

    public bool FitActive => fitReady;
    public string FitStatus => fitWhy;
    public FvpCapsuleFit.Shape FitShape => sizer.Current;
    public bool FitClamped => sizer.Clamped;
    public long FitFrames => fitFrames;
    public long FitInsideFrames => fitInsideFrames;

    void FitBegin()
    {
        fitReady = false;
        if (!fitToPerson) { fitWhy = "off"; return; }

        bool upright = Vector3.Dot(transform.up, Vector3.up) > 0.999f;
        bool capsuleMesh = TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null && mf.sharedMesh.name == "Capsule";
        bool capsuleCollider = capsule != null && capsule.direction == 1;
        if (!upright) fitWhy = "the object is tilted: the fit needs a capsule whose axis is the world's up";
        else if (!capsuleMesh && !capsuleCollider) fitWhy = "the object is not a capsule (no CapsuleCollider along Y, and not Unity's capsule mesh)";
        else fitWhy = "";
        if (fitWhy.Length > 0)
        {
            Debug.LogWarning($"[PoseEstimateFollower] '{name}': Fit To Person is on but not used: {fitWhy}. Untick it to silence this.", this);
            return;
        }
        if (capsule != null && (Mathf.Abs(capsule.radius - FvpCapsuleFit.MeshRadius) > 0.01f || Mathf.Abs(capsule.height - 2f * FvpCapsuleFit.MeshHalfHeight) > 0.01f))
            Debug.LogWarning($"[PoseEstimateFollower] '{name}': its CapsuleCollider is radius {capsule.radius:F2} / height {capsule.height:F2}, not the 0.5 / 2 of Unity's capsule mesh. " +
                             "The fit sizes the MESH (the bubble you see) so that the body is inside it; the collider scales with it but is not the same solid.", this);

        Vector3 ls = transform.lossyScale;
        float halfHeight = FvpCapsuleFit.MeshHalfHeight * Mathf.Abs(ls.y);
        float cy = transform.position.y;
        sizer.Reset(new FvpCapsuleFit.Shape { bottom = cy - halfHeight, top = cy + halfHeight, radius = FvpCapsuleFit.MeshRadius * Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.z)) });
        fitAuthoredScale = transform.localScale;
        fitAuthoredY = cy;
        fitBodyId = int.MinValue;
        fitReady = true;
        fitWhy = "fitting";
        ResetFitStatistics();
    }

    // Disabled: the object goes back to the size and height it was authored with.
    void FitEnd()
    {
        if (!fitReady) return;
        fitReady = false;
        if (this == null || transform == null) return;
        transform.localScale = fitAuthoredScale;
        Vector3 p = transform.position;
        p.y = fitAuthoredY;
        transform.position = p;
    }

    void ResetFitStatistics()
    {
        FitRadiusMetres.Clear(); FitHeightMetres.Clear(); FitShortfallMetres.Clear();
        fitFrames = fitInsideFrames = fitClampedFrames = 0;
    }

    void SyncFitSettings()
    {
        sizer.margin = fitMargin;
        sizer.minRadius = fitMinRadius;
        sizer.maxRadius = Mathf.Max(fitMaxRadius, fitMinRadius);
        sizer.minHeight = fitMinHeight;
        sizer.shrinkSeconds = fitShrinkSeconds;
        sizer.followBottom = fitBottom;
    }

    // A new estimate that matched the followed person: take that person's joints.
    void FitObserve(PoseSample s)
    {
        if (!fitReady || s.people == null) return;
        int id = track.LockedId;
        FvpPerson person = null;
        for (int i = 0; i < s.people.Count; i++)
        {
            FvpPerson p = s.people[i];
            if (p != null && !p.phantom && p.id == id) { person = p; break; }
        }
        if (person == null || !TryAnchor(person, out Vector3 anchor)) return;

        if (id != fitBodyId) { sizer.ClearBody(); fitBodyId = id; }      // somebody else's joints are not this body's memory
        SyncFitSettings();
        sizer.Observe(person.joints, person.valid, new Vector2(anchor.x + offset.x, anchor.z + offset.y), Time.realtimeSinceStartup);
    }

    // Every rendered frame, after the object has been placed: the size for this frame, written to the transform.
    void FitStep(OperatorTrack.State state, float dt)
    {
        if (!fitReady) return;
        SyncFitSettings();
        bool following = state != OperatorTrack.State.Searching;
        FvpCapsuleFit.Shape s = sizer.Step(following, dt);

        Vector3 parent = transform.parent != null ? transform.parent.lossyScale : Vector3.one;
        float sx = s.radius / FvpCapsuleFit.MeshRadius, sy = s.HalfHeight / FvpCapsuleFit.MeshHalfHeight;
        transform.localScale = new Vector3(sx / Mathf.Max(1e-4f, Mathf.Abs(parent.x)), sy / Mathf.Max(1e-4f, Mathf.Abs(parent.y)), sx / Mathf.Max(1e-4f, Mathf.Abs(parent.z)));
        Vector3 p = transform.position;
        p.y = s.Centre;
        transform.position = p;

        if (following)
        {
            FitRadiusMetres.Add(s.radius);
            FitHeightMetres.Add(s.top - s.bottom);
            if (sizer.Clamped) fitClampedFrames++;
        }
    }

    // With a truth avatar: is the real operator's whole body inside the capsule that is drawn, in this frame? The balls are the joints'
    // reach WITHOUT the margin, measured from where the capsule really is, so the margin is what absorbs the position error.
    void MeasureFit(FvpPerson truth, OperatorTrack.State state)
    {
        if (!fitReady || state == OperatorTrack.State.Searching) return;
        FvpCapsuleFit.Shape s = sizer.Current;
        Vector3 axis = transform.position;
        int n = 0;
        for (int j = 0; j < FvpSkeleton.Count; j++)
        {
            if (!truth.IsValid(j)) continue;
            float dx = truth.joints[j].x - axis.x, dz = truth.joints[j].z - axis.z;
            truthBalls[n++] = new FvpCapsuleFit.Ball(Mathf.Sqrt(dx * dx + dz * dz), truth.joints[j].y, FvpCapsuleFit.JointRadius[j]);
        }
        if (n == 0) return;
        float need = FvpCapsuleFit.RequiredRadius(truthBalls, n, 0f, s.bottom, s.top);
        fitFrames++;
        if (need <= s.radius + 1e-4f) fitInsideFrames++;
        else FitShortfallMetres.Add(float.IsInfinity(need) ? 1f : need - s.radius);
    }

    void AppendFitReport(StringBuilder sb)
    {
        if (!fitToPerson) return;
        if (!fitReady) { sb.Append("  capsule size: not fitted - ").Append(fitWhy).Append('\n'); return; }
        if (FitRadiusMetres.Count == 0) { sb.Append("  capsule size: fitted, but nobody was followed\n"); return; }
        CultureInfo inv = CultureInfo.InvariantCulture;
        sb.AppendFormat(inv, "  capsule size, fitted to the estimated body: radius median {0:F2} m, p95 {1:F2}, max {2:F2};  tip to tip median {3:F2} m, p95 {4:F2}, max {5:F2};  at the radius limit {6:F1} % of the time\n",
            FitRadiusMetres.Percentile(0.5f), FitRadiusMetres.Percentile(0.95f), FitRadiusMetres.Max,
            FitHeightMetres.Percentile(0.5f), FitHeightMetres.Percentile(0.95f), FitHeightMetres.Max,
            100.0 * fitClampedFrames / System.Math.Max(1L, FitRadiusMetres.Count));
        if (fitFrames > 0)
        {
            sb.AppendFormat(inv, "  the real operator's whole body (every joint with its reach, no margin) inside the capsule that is drawn: {0:F1} % of {1} frames",
                100.0 * fitInsideFrames / fitFrames, fitFrames);
            if (FitShortfallMetres.Count > 0)
                sb.AppendFormat(inv, ";  when it was not, it needed {0:F1} cm more radius (median), {1:F1} cm at p95, {2:F1} cm at worst",
                    FitShortfallMetres.Percentile(0.5f) * 100f, FitShortfallMetres.Percentile(0.95f) * 100f, FitShortfallMetres.Max * 100f);
            sb.Append('\n');
        }
    }
}
