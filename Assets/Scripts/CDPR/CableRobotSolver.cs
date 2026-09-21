using UnityEngine;

/// <summary>
/// Kinematics and statics for a 4-cable suspended ("SkyCam") cable-driven parallel robot.
///
/// The platform is modelled as a point mass with 3 translational DOF. Orientation is
/// not solved for. Four cables driving three DOF gives one degree of actuation
/// redundancy, and that redundancy is exactly what lets all four cables stay taut.
///
/// Two facts drive everything here:
///
///   1. Inverse kinematics is exact and closed-form: l_i = ||a_i - p_i||, where p_i is
///      cable i's attachment point on the platform. Position in, four cable lengths
///      out. No iteration, no singularities.
///
///   2. The real constraint is that a cable can pull but never push. The reachable set
///      is therefore not "inside the box" but the *wrench-feasible workspace*: the
///      positions where the force balance
///
///          sum_i ( t_i * u_i )  =  m * (accel - gravity)
///
///      admits a solution with every t_i inside [tensionMin, tensionMax]. With the four
///      anchors coplanar on the ceiling the cable directions cannot positively span 3D
///      space by themselves, so gravity supplies the missing downward pull. That is why
///      this design cannot accelerate downward faster than g, and why its workspace is
///      roughly the pyramid hanging under the anchor rectangle.
///
/// Solving (2): W is the 3x4 matrix whose columns are the unit cable directions. It has
/// rank 3, so its null space is one-dimensional and the tension solutions form a line,
/// t = t_particular + lambda * n. We take the minimum-norm particular solution, then pick
/// lambda at the centre of the interval that keeps all four tensions within bounds.
/// Centring maximises the margin against both slack and overload. This is the standard
/// closed-form tension distribution for a cable robot with one redundant actuator.
///
/// Moment balance is deliberately not solved. When all four cables converge on a single
/// point (a spreader or hook) they produce no moment and the model is exact. When they
/// instead attach to spread-out points such as the corners of a platform, the platform is
/// under-constrained in 6 DOF and this force-only model assumes it hangs level.
///
/// Everything is allocation-free: the caller owns the arrays.
/// </summary>
public static class CableRobotSolver
{
    public const int CableCount = 4;

    public enum Status
    {
        /// <summary>All four tensions lie within bounds. The pose can be held.</summary>
        Feasible,
        /// <summary>Cable directions do not span 3D space (platform on the anchor plane, or anchors collinear). No tension solution exists.</summary>
        Degenerate,
        /// <summary>Tensions exist but at least one is outside [tensionMin, tensionMax]. Outside the wrench-feasible workspace.</summary>
        Infeasible,
    }

    /// <summary>
    /// Cable lengths and unit directions (attachment -> anchor). Never fails; this is the
    /// closed-form inverse kinematics. <paramref name="attachPoints"/> may all be the same
    /// world position, which is the exact single-point (4-1) model.
    /// </summary>
    public static void InverseKinematics(Vector3[] attachPoints, Vector3[] anchors, float[] lengths, Vector3[] dirs)
    {
        for (int i = 0; i < CableCount; i++)
        {
            Vector3 d = anchors[i] - attachPoints[i];
            float l = d.magnitude;
            lengths[i] = l;
            // Only degenerate when the platform sits exactly on an anchor. The tension
            // solve will flag that, so just pick a harmless direction here.
            dirs[i] = l > 1e-6f ? d / l : Vector3.up;
        }
    }

    /// <summary>
    /// Distributes <paramref name="requiredForce"/> — the resultant the cables must produce,
    /// i.e. mass * (accel - gravity) — over the four cables. Tensions are written even when
    /// infeasible, so the caller can see which cable went slack or overloaded.
    /// </summary>
    public static Status SolveTensions(Vector3[] dirs, Vector3 requiredForce, float tensionMin, float tensionMax, float[] tensions)
    {
        // Normal matrix W * W^T, symmetric 3x3, accumulated as the sum of u_i u_i^T.
        float xx = 0f, xy = 0f, xz = 0f, yy = 0f, yz = 0f, zz = 0f;
        for (int i = 0; i < CableCount; i++)
        {
            Vector3 u = dirs[i];
            xx += u.x * u.x; xy += u.x * u.y; xz += u.x * u.z;
            yy += u.y * u.y; yz += u.y * u.z; zz += u.z * u.z;
        }

        // Inverse via adjugate; first-row cofactors also give the determinant.
        float c00 = yy * zz - yz * yz;
        float c01 = xz * yz - xy * zz;
        float c02 = xy * yz - xz * yy;
        float det = xx * c00 + xy * c01 + xz * c02;

        if (Mathf.Abs(det) < 1e-9f)
        {
            for (int i = 0; i < CableCount; i++) tensions[i] = 0f;
            return Status.Degenerate;
        }

        float c11 = xx * zz - xz * xz;
        float c12 = xz * xy - xx * yz;
        float c22 = xx * yy - xy * xy;
        float invDet = 1f / det;

        // y = (W W^T)^-1 * requiredForce, then t_particular = W^T y, the minimum-norm solution.
        Vector3 f = requiredForce;
        Vector3 y = new Vector3(
            invDet * (c00 * f.x + c01 * f.y + c02 * f.z),
            invDet * (c01 * f.x + c11 * f.y + c12 * f.z),
            invDet * (c02 * f.x + c12 * f.y + c22 * f.z));

        for (int i = 0; i < CableCount; i++) tensions[i] = Vector3.Dot(dirs[i], y);

        // Null-space direction of W: the alternating 3x3 minors. Adding any multiple of this
        // leaves the resultant force untouched, so it is the only freedom available to push
        // all four tensions inside their bounds.
        float n0 =  Det(dirs[1], dirs[2], dirs[3]);
        float n1 = -Det(dirs[0], dirs[2], dirs[3]);
        float n2 =  Det(dirs[0], dirs[1], dirs[3]);
        float n3 = -Det(dirs[0], dirs[1], dirs[2]);

        float nNorm = Mathf.Sqrt(n0 * n0 + n1 * n1 + n2 * n2 + n3 * n3);
        if (nNorm < 1e-9f) return Status.Degenerate; // every minor vanishes => rank < 3
        n0 /= nNorm; n1 /= nNorm; n2 /= nNorm; n3 /= nNorm;

        // Intersect the four per-cable admissible intervals for lambda.
        float lo = float.NegativeInfinity, hi = float.PositiveInfinity;
        bool feasible = true;
        for (int i = 0; i < CableCount; i++)
        {
            float n = i == 0 ? n0 : i == 1 ? n1 : i == 2 ? n2 : n3;
            if (Mathf.Abs(n) < 1e-9f)
            {
                // lambda cannot move this cable at all, so it must already be in range.
                if (tensions[i] < tensionMin || tensions[i] > tensionMax) feasible = false;
                continue;
            }
            float a = (tensionMin - tensions[i]) / n;
            float b = (tensionMax - tensions[i]) / n;
            if (n < 0f) (a, b) = (b, a);
            lo = Mathf.Max(lo, a);
            hi = Mathf.Min(hi, b);
        }

        if (lo > hi) feasible = false;

        // Centre of the interval sits as far as possible from both bounds. When the interval
        // is empty the midpoint still spreads the violation evenly, keeping the reported
        // tensions useful for diagnostics.
        float lambda = 0f;
        if (!float.IsInfinity(lo) && !float.IsInfinity(hi)) lambda = 0.5f * (lo + hi);
        else if (!float.IsInfinity(lo)) lambda = lo;
        else if (!float.IsInfinity(hi)) lambda = hi;

        tensions[0] += lambda * n0;
        tensions[1] += lambda * n1;
        tensions[2] += lambda * n2;
        tensions[3] += lambda * n3;

        return feasible ? Status.Feasible : Status.Infeasible;
    }

    /// <summary>Cable length to winch drum angle. The only place the motor enters the kinematics.</summary>
    public static float DrumAngleDegrees(float cableLength, float drumRadius)
    {
        if (drumRadius < 1e-6f) return 0f;
        return cableLength / drumRadius * Mathf.Rad2Deg;
    }

    static float Det(Vector3 a, Vector3 b, Vector3 c) => Vector3.Dot(a, Vector3.Cross(b, c));
}
