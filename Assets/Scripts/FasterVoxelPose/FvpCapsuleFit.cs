using UnityEngine;

// Sizes the scene's protection capsule to the person inside it, in any pose. No scene code in here: joints in, a capsule out, so it can be
// tested outside the editor (Server~/track_eval, "fit").
//
// The capsule is Unity's built-in capsule (mesh radius 0.5, height 2, axis Y) scaled by (s, h, s) and upright. Scaled like that its shape is
// a cylinder of radius R between two caps that are half-ellipsoids (semi-axes R and half the half-height), and its CapsuleCollider
// (radius 0.5, height 2) is a true capsule with hemispheres of radius R between the same two tips. They are not the same solid, and the body
// has to be inside BOTH, so the fit asks for the radius each one needs and takes the larger.
//
// The body is 15 balls, one per joint (a joint is the middle of a limb; the ball is how far the body reaches around it: a wrist is 14 cm
// from the end of the fingers, an ankle 15 cm from the toes, the nose 17 cm from the top of the head). A ball is inside a convex solid when a
// ring of points around it is, so five points per ball are tested (the half circle that faces away from the axis, the other half is inside
// when this one is: the solid is symmetric and convex), on a circle 8 % wider than the ball so the polygon through them covers the ball.
public static class FvpCapsuleFit
{
    public const float MeshRadius = 0.5f, MeshHalfHeight = 1f;       // the built-in capsule mesh, before any scale
    public const float TipClearance = 0.005f;                        // a ball must end this far under a tip
    const float Circumscribe = 1.0824f;                              // 1 / cos(22.5 degrees): five points 45 degrees apart cover the whole circle
    static readonly float[] Cos = { 0f, 0.70710678f, 1f, 0.70710678f, 0f };
    static readonly float[] Sin = { -1f, -0.70710678f, 0f, 0.70710678f, 1f };

    // How far the body reaches around each Panoptic-15 joint (neck, nose, mid-hip, l-shoulder, l-elbow, l-wrist, l-hip, l-knee, l-ankle, r-...), metres.
    public static readonly float[] JointRadius =
    {
        0.12f, 0.17f, 0.16f,
        0.12f, 0.09f, 0.14f, 0.15f, 0.11f, 0.15f,
        0.12f, 0.09f, 0.14f, 0.15f, 0.11f, 0.15f,
    };

    // x: horizontal distance from the capsule's axis, y: height (world), r: the radius of the body around that point
    public struct Ball
    {
        public float x, y, r;
        public Ball(float x, float y, float r) { this.x = x; this.y = y; this.r = r; }
    }

    // The two tips (world height) and the radius of the cylinder between them.
    public struct Shape
    {
        public float bottom, top, radius;
        public float Centre => 0.5f * (bottom + top);
        public float HalfHeight => 0.5f * (top - bottom);
    }

    // The smallest radius that makes the capsule between these two tips contain every ball (each grown by `extra`). +Infinity when a ball
    // cannot be between the tips at any radius.
    public static float RequiredRadius(Ball[] balls, int count, float extra, float bottom, float top)
    {
        float sy = 0.5f * (top - bottom), c = 0.5f * (top + bottom), a = 0.5f * sy;
        if (!(sy > 1e-4f)) return float.PositiveInfinity;
        float need = 0f;
        for (int i = 0; i < count; i++)
        {
            float rr = (balls[i].r + extra) * Circumscribe;
            for (int k = 0; k < 5; k++)
            {
                float px = balls[i].x + rr * Cos[k];
                float u = Mathf.Abs(balls[i].y + rr * Sin[k] - c);
                float e = sy - u;                                    // how far under the nearer tip
                if (!(e > TipClearance)) return float.PositiveInfinity;
                float mesh = px;                                     // the cylinder part of the mesh
                if (u > a)                                           // a cap: half-ellipsoid, x^2 / R^2 + ((u - a) / a)^2 <= 1
                {
                    float t = (u - a) / a;
                    mesh = px / Mathf.Sqrt(Mathf.Max(1e-6f, 1f - t * t));
                }
                // the collider: hemispheres of radius R over the tips, so the cap starts at u = sy - R. In the cylinder part R >= px is all it takes;
                // in the cap, x^2 + (u - (sy - R))^2 <= R^2  <=>  R >= (x^2 + e^2) / (2e). That holds while R <= sy; a fatter capsule than it is tall
                // is a sphere of radius R for Unity (the height is never less than 2 R), and then the point only has to be inside that sphere.
                float collider = e >= px ? px : (px * px + e * e) / (2f * e);
                if (collider > sy) collider = Mathf.Sqrt(px * px + u * u);
                need = Mathf.Max(need, Mathf.Max(mesh, collider));
            }
        }
        return need;
    }

    // Is the point inside the mesh AND the collider of this shape (radius R, tips top / bottom)? Used by the tests and to measure.
    public static bool Inside(Shape s, float x, float y)
    {
        float sy = s.HalfHeight, a = 0.5f * sy, u = Mathf.Abs(y - s.Centre), R = s.radius;
        if (u > sy || x > R) return false;
        float t = u <= a ? 0f : (u - a) / a;
        if ((x * x) / (R * R) + t * t > 1f) return false;            // the mesh
        float h = Mathf.Max(0f, sy - R);                             // the collider: a segment of this half-length, thickened by R (never under 2 R tall)
        float over = Mathf.Max(0f, u - h);
        return x * x + over * over <= R * R;
    }

    // The smallest capsule (R^2 x height) around the balls, found by trying the two tips on a grid. fixedBottom: the lower tip is never
    // higher than that (the capsule stands where it was put; it goes lower when the body is lower, or when feet far from the axis need
    // the lower cap to be deeper); NaN: the lower tip follows the body.
    // `clamped`: the body does not fit in maxRadius (the radius is capped and part of the body sticks out).
    public static Shape Fit(Ball[] balls, int count, float extra, float fixedBottom, float minRadius, float maxRadius, float minHeight, out bool clamped)
    {
        float lowest = float.PositiveInfinity, highest = float.NegativeInfinity, reach = 0f;
        for (int i = 0; i < count; i++)
        {
            float rr = (balls[i].r + extra) * Circumscribe;
            lowest = Mathf.Min(lowest, balls[i].y - rr);
            highest = Mathf.Max(highest, balls[i].y + rr);
            reach = Mathf.Max(reach, balls[i].x + rr);                // no capsule is narrower than the farthest ball's outer edge
        }
        float floorRadius = Mathf.Max(minRadius, reach);
        clamped = false;
        if (count == 0) return default;

        bool follow = float.IsNaN(fixedBottom);
        var best = new Shape { radius = float.PositiveInfinity };
        float bestCost = float.PositiveInfinity;
        float lowestTip = lowest - 2f * TipClearance;
        float baseBottom = follow ? lowestTip : Mathf.Min(fixedBottom, lowestTip);
        for (int ib = 0; ib < 16; ib++)
        {
            float bottom = baseBottom - ib * 0.04f;
            float top0 = Mathf.Max(highest + 2f * TipClearance, bottom + minHeight);
            if (floorRadius * floorRadius * (top0 - bottom) >= bestCost) break;            // deeper tips only make it taller
            for (int it = 0; it < 25; it++)
            {
                float top = top0 + it * 0.04f;
                if (floorRadius * floorRadius * (top - bottom) >= bestCost) break;         // so does a higher top
                float R = Mathf.Max(RequiredRadius(balls, count, extra, bottom, top), minRadius);
                if (float.IsInfinity(R) || float.IsNaN(R)) continue;
                float cost = R * R * (top - bottom);
                if (cost < bestCost) { bestCost = cost; best = new Shape { bottom = bottom, top = top, radius = R }; }
            }
        }
        if (float.IsInfinity(best.radius))                           // NaN joints: nothing sensible to say
            return new Shape { bottom = Mathf.Min(lowest, 0f), top = Mathf.Max(highest, 1f), radius = maxRadius };
        clamped = best.radius > maxRadius;
        if (clamped) best.radius = maxRadius;
        return best;
    }
}

// The capsule of one followed person over time: remembers the joints of the newest estimate (a joint the model drops for a moment keeps its
// last place for memorySeconds), fits the capsule to them, and every rendered frame moves the capsule towards that fit: it GROWS at once
// (never lets the body out) and SHRINKS over shrinkSeconds (a wrist that flickers must not make the capsule flicker).
// The radius is also re-derived every frame from the CURRENT tips, so that the remembered body is inside whatever the smoothing does to the tips.
public sealed class FvpCapsuleSizer
{
    public float margin = 0.12f;            // clearance around every ball: the estimate's error and the time between two estimates
    public float minRadius = 0.30f, maxRadius = 1.50f, minHeight = 0.60f;
    public float shrinkSeconds = 0.5f;
    public float memorySeconds = 1.0f;
    public bool followBottom;               // false: the lower tip stays where the capsule was authored (on the floor)

    public FvpCapsuleFit.Shape Authored { get; private set; }
    public FvpCapsuleFit.Shape Current { get; private set; }
    public FvpCapsuleFit.Shape Target { get; private set; }
    public bool Clamped { get; private set; }       // the body is bigger than maxRadius allows
    public int BallCount => count;

    const int Joints = 15;
    readonly float[] slotX = new float[Joints], slotY = new float[Joints], seen = new float[Joints];
    readonly bool[] has = new bool[Joints];
    readonly FvpCapsuleFit.Ball[] balls = new FvpCapsuleFit.Ball[Joints];
    int count;
    float fittedMargin;
    bool fittedBottom;

    public void Reset(FvpCapsuleFit.Shape authored)
    {
        Authored = Current = Target = authored;
        Clamped = false;
        ClearBody();
    }

    public void ClearBody()
    {
        for (int j = 0; j < Joints; j++) has[j] = false;
        count = 0;
    }

    // The joints of a new estimate of the followed person (world, metres). `axis`: where the capsule's axis stands for it (world X, Z).
    public void Observe(Vector3[] joints, bool[] valid, Vector2 axis, float time)
    {
        for (int j = 0; j < Joints; j++)
        {
            bool ok = (valid == null || valid[j]) && IsFinite(joints[j]);
            if (ok)
            {
                float dx = joints[j].x - axis.x, dz = joints[j].z - axis.y;
                slotX[j] = Mathf.Sqrt(dx * dx + dz * dz);
                slotY[j] = joints[j].y;
                seen[j] = time;
                has[j] = true;
            }
            else if (has[j] && time - seen[j] > memorySeconds) has[j] = false;
        }
        Refit();
    }

    static bool IsFinite(Vector3 v) => !(float.IsNaN(v.x + v.y + v.z) || float.IsInfinity(v.x + v.y + v.z));

    void Refit()
    {
        count = 0;
        for (int j = 0; j < Joints; j++)
            if (has[j]) balls[count++] = new FvpCapsuleFit.Ball(slotX[j], slotY[j], FvpCapsuleFit.JointRadius[j]);
        fittedMargin = margin;
        fittedBottom = followBottom;
        if (count == 0) return;
        Target = FvpCapsuleFit.Fit(balls, count, margin, followBottom ? float.NaN : Authored.bottom, minRadius, maxRadius, minHeight, out bool clamped);
        Clamped = clamped;
    }

    // One rendered frame. following: a person is followed (or held after a loss); otherwise the capsule goes back to how it was authored.
    public FvpCapsuleFit.Shape Step(bool following, float dt)
    {
        if (!following && count > 0) ClearBody();
        if (count > 0 && (margin != fittedMargin || followBottom != fittedBottom)) Refit();

        FvpCapsuleFit.Shape tgt = count > 0 ? Target : Authored;
        FvpCapsuleFit.Shape cur = Current;
        float k = shrinkSeconds > 1e-4f ? 1f - Mathf.Exp(-Mathf.Max(0f, dt) / shrinkSeconds) : 1f;
        cur.top = tgt.top >= cur.top ? tgt.top : Mathf.Lerp(cur.top, tgt.top, k);
        cur.bottom = tgt.bottom <= cur.bottom ? tgt.bottom : Mathf.Lerp(cur.bottom, tgt.bottom, k);
        cur.radius = tgt.radius >= cur.radius ? tgt.radius : Mathf.Lerp(cur.radius, tgt.radius, k);

        if (count > 0)
        {
            float need = FvpCapsuleFit.RequiredRadius(balls, count, margin, cur.bottom, cur.top);
            if (float.IsInfinity(need) || float.IsNaN(need))        // the tips cut a ball: take the fitted ones
            {
                cur.top = Mathf.Max(cur.top, Target.top);
                cur.bottom = Mathf.Min(cur.bottom, Target.bottom);
                need = FvpCapsuleFit.RequiredRadius(balls, count, margin, cur.bottom, cur.top);
                if (float.IsInfinity(need) || float.IsNaN(need)) { cur = Target; need = Target.radius; }
            }
            cur.radius = Mathf.Max(cur.radius, Mathf.Max(need, minRadius));
            Clamped = cur.radius > maxRadius + 1e-4f;
            cur.radius = Mathf.Min(cur.radius, maxRadius);
        }
        else Clamped = false;

        Current = cur;
        return cur;
    }
}
