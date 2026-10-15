using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

// The capsule's size (FvpCapsuleFit / FvpCapsuleSizer), outside Unity. A body in hand-made and random poses, an "estimator" that reports it
// every 100 ms, a capsule that is redrawn 60 times a second. The question: is the body inside the capsule that is drawn, at EVERY frame?
//
// "Inside" is not asked of the code under test: it is checked here with two predicates written from the solids themselves (the unit capsule
// mesh scaled by (R/0.5, sy, R/0.5), and Unity's capsule collider as a distance to a segment), against a full circle of 360 points per
// ball, not the five the fit uses.
static class FitSim
{
    static int fails;
    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: {detail}");
        if (!ok) fails++;
    }

    // ---------------------------------------------------------------- the solids, from their definitions
    // Unity's built-in capsule mesh: radius 0.5, height 2, centred, axis Y. Scaled by (R / 0.5, sy, R / 0.5).
    static bool InMesh(FvpCapsuleFit.Shape s, double x, double y)
    {
        double sy = s.HalfHeight, R = s.radius;
        double xl = x * 0.5 / R, yl = (y - s.Centre) / sy;           // back to the unit mesh
        if (Math.Abs(yl) <= 0.5) return xl <= 0.5 + 1e-9;
        return (Math.Abs(yl) - 0.5) * (Math.Abs(yl) - 0.5) + xl * xl <= 0.25 + 1e-9;
    }

    // Unity's CapsuleCollider (radius 0.5, height 2, Y) under that scale: radius R, height 2 sy, but never under 2 R (then it is a sphere).
    static bool InCollider(FvpCapsuleFit.Shape s, double x, double y)
    {
        double sy = s.HalfHeight, R = s.radius, u = Math.Abs(y - s.Centre);
        double h = Math.Max(0.0, sy - R);
        double d = Math.Sqrt(x * x + Math.Max(0.0, u - h) * Math.Max(0.0, u - h));
        return d <= R + 1e-9;
    }

    // Is the whole ball (radius r + extra, at horizontal distance x from the axis, height y) inside both solids? A full circle of points.
    static bool BallInside(FvpCapsuleFit.Shape s, double x, double y, double r, out double worst)
    {
        worst = 0.0;
        bool ok = true;
        for (int i = 0; i < 360; i++)
        {
            double a = i * Math.PI / 180.0;
            double px = Math.Abs(x + r * Math.Cos(a)), py = y + r * Math.Sin(a);
            if (!(InMesh(s, px, py) && InCollider(s, px, py)))
            {
                ok = false;
                // how far this point is outside, measured as the extra radius that would take it in
                worst = Math.Max(worst, 1.0);
            }
        }
        return ok;
    }

    // ---------------------------------------------------------------- bodies
    // Floor at y = -0.4 (the scene's: the authored capsule is 1.656 m tall from -0.4 to 1.256).
    const float Floor = -0.4f;
    static readonly FvpCapsuleFit.Shape Authored = new FvpCapsuleFit.Shape { bottom = -0.4f, top = 1.256f, radius = 0.5f };

    sealed class Body
    {
        public readonly Vector3[] j = new Vector3[15];
        public Vector2 Axis => new Vector2((j[0].x + j[2].x + j[3].x + j[9].x + j[6].x + j[12].x) / 6f, (j[0].z + j[2].z + j[3].z + j[9].z + j[6].z + j[12].z) / 6f);
        public Body Moved(float dx, float dz) { var b = new Body(); for (int i = 0; i < 15; i++) b.j[i] = j[i] + new Vector3(dx, 0, dz); return b; }
    }

    // Panoptic-15 from a torso axis, a lateral axis and limb directions. Lengths in metres, a 1.75 m person.
    static Body Make(Vector3 hip, Vector3 spine, Vector3 lateral, Vector3 armL, Vector3 foreL, Vector3 armR, Vector3 foreR,
                     Vector3 thighL, Vector3 shankL, Vector3 thighR, Vector3 shankR, Vector3 headDir)
    {
        var b = new Body();
        spine = spine.normalized; lateral = lateral.normalized;
        b.j[2] = hip;
        b.j[0] = hip + spine * 0.55f;
        b.j[1] = b.j[0] + headDir.normalized * 0.20f;
        b.j[3] = b.j[0] + lateral * 0.19f; b.j[9] = b.j[0] - lateral * 0.19f;
        b.j[4] = b.j[3] + armL.normalized * 0.30f; b.j[5] = b.j[4] + foreL.normalized * 0.26f;
        b.j[10] = b.j[9] + armR.normalized * 0.30f; b.j[11] = b.j[10] + foreR.normalized * 0.26f;
        b.j[6] = hip + lateral * 0.10f; b.j[12] = hip - lateral * 0.10f;
        b.j[7] = b.j[6] + thighL.normalized * 0.43f; b.j[8] = b.j[7] + shankL.normalized * 0.43f;
        b.j[13] = b.j[12] + thighR.normalized * 0.43f; b.j[14] = b.j[13] + shankR.normalized * 0.43f;
        return b;
    }

    // Put the body on the floor: its lowest joint's centre is 8 cm above it (an ankle).
    static Body OnFloor(Body b, float x = 0f, float z = 0f)
    {
        float lowest = b.j.Min(p => p.y);
        var o = new Body();
        for (int i = 0; i < 15; i++) o.j[i] = b.j[i] + new Vector3(x, Floor + 0.08f - lowest, z);
        return o;
    }

    static readonly Vector3 Up = Vector3.up, Down = Vector3.down, L = Vector3.right, F = Vector3.forward;

    static Body Standing() => OnFloor(Make(new Vector3(0, 0.95f, 0), Up, L, Down, Down, Down, Down, Down, Down, Down, Down, F * 0.4f + Up));
    static Body TPose() => OnFloor(Make(new Vector3(0, 0.95f, 0), Up, L, L, L, -L, -L, Down, Down, Down, Down, F * 0.4f + Up));
    static Body HandUp() => OnFloor(Make(new Vector3(0, 0.95f, 0), Up, L, Up, Up, Down, Down, Down, Down, Down, Down, F * 0.4f + Up));
    static Body ReachForward() => OnFloor(Make(new Vector3(0, 0.95f, 0), Up, L, F, F, Down, Down, Down, Down, Down, Down, F * 0.4f + Up));
    static Body ReachSideHigh() => OnFloor(Make(new Vector3(0, 0.95f, 0), Up, L, L + Up * 0.7f, L + Up, Down, Down, Down, Down, Down, Down, F * 0.4f + Up));
    // seated on a chair: thighs forward, shanks down, hands on the knees
    static Body Sitting() => OnFloor(Make(new Vector3(0, 0.50f, 0), Up, L, Down + F * 0.6f, F * 0.5f + Down * 0.5f, Down + F * 0.6f, F * 0.5f + Down * 0.5f, F, Down, F, Down, F * 0.3f + Up));
    static Body SittingLegsOut() => OnFloor(Make(new Vector3(0, 0.50f, 0), Up, L, Down + F * 0.6f, F * 0.5f + Down * 0.5f, Down + F * 0.6f, F * 0.5f + Down * 0.5f, F, F + Down * 0.2f, F, F + Down * 0.2f, F * 0.3f + Up));
    static Body Crouching() => OnFloor(Make(new Vector3(0, 0.45f, -0.05f), Up + F * 0.4f, L, F, F, F, F, F + Up * 0.3f, Down + F * -0.3f, F + Up * 0.3f, Down + F * -0.3f, F * 0.5f + Up));
    static Body Lying() => OnFloor(Make(new Vector3(0, 0.1f, 0), F, L, L, L, -L, -L, -F, -F, -F, -F, Up));
    static Body Bowing() => OnFloor(Make(new Vector3(0, 0.95f, 0), F + Up * 0.15f, L, Down + F * 0.2f, Down + F * 0.2f, Down + F * 0.2f, Down + F * 0.2f, Down, Down, Down, Down, F + Down * 0.3f));

    static readonly System.Random rnd = new System.Random(2024);
    static double Gauss() { double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble(); return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2); }
    static Vector3 RandomDir()
    {
        while (true)
        {
            var v = new Vector3((float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1);
            if (v.sqrMagnitude > 0.05f && v.sqrMagnitude <= 1f) return v.normalized;
        }
    }

    // The engine's Quaternion / Slerp are native calls, which a stand-alone program does not have: two rotations by hand.
    static Vector3 RotX(Vector3 v, float deg) { float c = Mathf.Cos(deg * Mathf.Deg2Rad), s = Mathf.Sin(deg * Mathf.Deg2Rad); return new Vector3(v.x, c * v.y - s * v.z, s * v.y + c * v.z); }
    static Vector3 RotY(Vector3 v, float deg) { float c = Mathf.Cos(deg * Mathf.Deg2Rad), s = Mathf.Sin(deg * Mathf.Deg2Rad); return new Vector3(c * v.x + s * v.z, v.y, -s * v.x + c * v.z); }
    static float AngleDeg(Vector3 a, Vector3 b) => Mathf.Acos(Mathf.Clamp(Vector3.Dot(a.normalized, b.normalized), -1f, 1f)) * Mathf.Rad2Deg;

    // Any pose a person could be in, and a few they could not: the torso tilted anywhere up to lying down, any yaw, the arms and legs in any direction.
    static Body Random()
    {
        float tilt = (float)(rnd.NextDouble() * rnd.NextDouble() * 90.0), yaw = (float)(rnd.NextDouble() * 360.0);
        Vector3 spine = RotY(RotX(Up, tilt), yaw);
        Vector3 lateral = RotY(L, yaw);
        Vector3 forward = RotY(F, yaw);
        Vector3 Limb(Vector3 around, float maxDeg) { Vector3 d = RandomDir(); return AngleDeg(d, around) <= maxDeg ? d : (around + d).normalized; }
        Vector3 aL = RandomDir(), aR = RandomDir();
        Vector3 fL = Limb(aL, 130), fR = Limb(aR, 130);
        Vector3 tL = Limb(-spine, 110), tR = Limb(-spine, 110);
        Vector3 sL = Limb(tL, 110), sR = Limb(tR, 110);
        float hy = 0.1f + (float)rnd.NextDouble() * 0.9f;
        return OnFloor(Make(new Vector3(0, hy, 0), spine, lateral, aL, fL, aR, fR, tL, sL, tR, sR, spine + forward * 0.4f));
    }

    // ---------------------------------------------------------------- running a sizer
    static FvpCapsuleSizer NewSizer(float shrink = 0.5f, bool followBottom = false)
        => NewSizerAt(Authored, shrink, followBottom);

    static FvpCapsuleSizer NewSizerAt(FvpCapsuleFit.Shape authored, float shrink, bool followBottom)
    {
        var s = new FvpCapsuleSizer { shrinkSeconds = shrink, followBottom = followBottom };
        s.Reset(authored);
        return s;
    }

    // Is every ball of the body (the real radii, no margin) inside the sizer's current capsule, the axis being at `axis`?
    static bool Contained(FvpCapsuleSizer s, Body b, Vector2 axis, out int ball)
    {
        ball = -1;
        for (int i = 0; i < 15; i++)
        {
            double x = Math.Sqrt((b.j[i].x - axis.x) * (b.j[i].x - axis.x) + (b.j[i].z - axis.y) * (b.j[i].z - axis.y));
            if (!BallInside(s.Current, x, b.j[i].y, FvpCapsuleFit.JointRadius[i], out _)) { ball = i; return false; }
        }
        return true;
    }

    static void Settle(FvpCapsuleSizer s, Body b, float seconds = 4f)
    {
        s.Observe(b.j, null, b.Axis, 0f);
        for (float t = 0; t < seconds; t += 1f / 60f) s.Step(true, 1f / 60f);
    }

    static float Height(FvpCapsuleSizer s) => s.Current.top - s.Current.bottom;

    static void Main()
    {
        Console.WriteLine("Capsule fit scenarios (FvpCapsuleFit / FvpCapsuleSizer)\n");

        // ------------------------------------------------------------------ A: the poses, one by one
        Console.WriteLine("A. hand-made poses: the whole body is inside the capsule once it has settled (floor at -0.40, authored capsule -0.40 .. 1.26, R 0.50)");
        var named = new (string name, Body body)[]
        {
            ("standing, arms down", Standing()), ("T-pose", TPose()), ("one hand straight up", HandUp()), ("reaching forward", ReachForward()),
            ("reaching out and up", ReachSideHigh()), ("sitting on a chair", Sitting()), ("sitting, legs out", SittingLegsOut()),
            ("crouching", Crouching()), ("lying on the floor", Lying()), ("bowing", Bowing()),
        };
        var size = new Dictionary<string, (float radius, float top, float bottom, float height)>();
        foreach (var (name, body) in named)
        {
            var s = NewSizer();
            Settle(s, body);
            bool ok = Contained(s, body, body.Axis, out int ball);
            float handUp = body.j.Max(p => p.y);
            size[name] = (s.Current.radius, s.Current.top, s.Current.bottom, Height(s));
            Check($"{name,-22}", ok && !s.Clamped,
                $"R {s.Current.radius:F2} m, tips {s.Current.bottom:F2} .. {s.Current.top:F2} (head/hand at {handUp:F2}){(ok ? "" : ", NOT inside: joint " + ball)}{(s.Clamped ? ", clamped" : "")}");
        }

        // ------------------------------------------------------------------ B: smaller when sitting
        Console.WriteLine("\nB. smaller when sitting or crouching, bigger with an arm out");
        {
            var st = size["standing, arms down"]; var si = size["sitting on a chair"]; var cr = size["crouching"]; var tp = size["T-pose"]; var hu = size["one hand straight up"];
            Check("sitting: the top is at least 40 cm lower than standing", st.top - si.top >= 0.40f, $"{st.top:F2} -> {si.top:F2} m ({(st.top - si.top) * 100:F0} cm lower)");
            Check("crouching: the top is lower than sitting's... and than standing's by 60 cm", st.top - cr.top >= 0.60f, $"{st.top:F2} -> {cr.top:F2} m");
            Check("a hand straight up raises the top above the head's", hu.top > st.top + 0.35f, $"{hu.top:F2} vs {st.top:F2} m");
            Check("T-pose: the radius covers the arm span (0.19 + 0.56 + hand 0.14 = 0.89 m at least)", tp.radius >= 0.89f, $"R {tp.radius:F2} m");
            Check("standing, arms down: about the authored size (R within 0.35 .. 0.65 m)", st.radius >= 0.35f && st.radius <= 0.65f, $"R {st.radius:F2} m");
            Check("the lower tip stays where it was authored (or lower): the capsule stands on the floor", named.All(n => size[n.name].bottom <= Authored.bottom + 1e-4f), $"tips {size.Values.Min(v => v.bottom):F2} .. {size.Values.Max(v => v.bottom):F2}");
        }

        // ------------------------------------------------------------------ C: random poses, every frame
        Console.WriteLine("\nC. 4000 random poses, an estimate every 100 ms; the capsule is redrawn at 60 fps with a 0.5 s shrink; the body must be inside at EVERY frame");
        {
            var s = NewSizer();
            int frames = 0, bad = 0, clamped = 0, poses = 4000, worstBall = -1;
            float maxR = 0, sumR = 0, maxTop = 0;
            Body b = null;
            var timer = Stopwatch.StartNew();
            double observeMs = 0, stepMs = 0;
            for (int p = 0; p < poses; p++)
            {
                b = Random().Moved((float)rnd.NextDouble() * 4 - 2, (float)rnd.NextDouble() * 4 - 2);
                var t0 = Stopwatch.GetTimestamp();
                s.Observe(b.j, null, b.Axis, p * 0.1f);
                observeMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                for (int f = 0; f < 6; f++)                                    // 100 ms of frames at 60 fps (the sixth is the next estimate's)
                {
                    var t1 = Stopwatch.GetTimestamp();
                    s.Step(true, 1f / 60f);
                    stepMs += (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
                    frames++;
                    if (s.Clamped) { clamped++; continue; }
                    if (!Contained(s, b, b.Axis, out int ball)) { bad++; worstBall = ball; }
                }
                maxR = Math.Max(maxR, s.Current.radius); sumR += s.Current.radius; maxTop = Math.Max(maxTop, s.Current.top);
            }
            Check("the body was inside in every frame", bad == 0, $"{bad} of {frames - clamped} frames outside{(bad > 0 ? " (joint " + worstBall + ")" : "")}; {clamped} frames skipped because the pose needs more than the maximum radius");
            Console.WriteLine($"     radius mean {sumR / poses:F2} m, max {maxR:F2} m; highest top {maxTop:F2} m; cost per estimate {observeMs / poses * 1000:F0} us, per frame {stepMs / frames * 1000:F1} us");
            Check("cheap: under 0.25 ms per estimate and 0.05 ms per frame", observeMs / poses < 0.25 && stepMs / frames < 0.05, $"{observeMs / poses:F3} / {stepMs / frames:F4} ms");
        }

        // ------------------------------------------------------------------ D: grows at once, shrinks slowly
        Console.WriteLine("\nD. the capsule grows in the frame a hand goes up, and shrinks over the shrink time when it comes down");
        {
            var s = NewSizer();
            Settle(s, Standing());
            float topStanding = s.Current.top, rStanding = s.Current.radius;
            Body up = HandUp();
            s.Observe(up.j, null, up.Axis, 10f);
            s.Step(true, 1f / 60f);
            Check("the first frame after the estimate already holds the raised hand", Contained(s, up, up.Axis, out int ball), $"top {topStanding:F2} -> {s.Current.top:F2} m");
            for (int i = 0; i < 120; i++) s.Step(true, 1f / 60f);
            float topUp = s.Current.top;
            Body down = Standing();
            s.Observe(down.j, null, down.Axis, 12f);
            s.Step(true, 1f / 60f);
            float afterOne = s.Current.top;
            for (int i = 0; i < 30; i++) s.Step(true, 1f / 60f);              // half a second
            float afterHalf = s.Current.top;
            for (int i = 0; i < 240; i++) s.Step(true, 1f / 60f);
            float afterLong = s.Current.top;
            Check("one frame after the hand came down the capsule has barely moved", topUp - afterOne < 0.15f * (topUp - topStanding), $"{topUp:F2} -> {afterOne:F2} m");
            Check("after one shrink time it has covered about 63 % of the way (40 .. 80 %)", (topUp - afterHalf) / (topUp - topStanding) > 0.40f && (topUp - afterHalf) / (topUp - topStanding) < 0.80f,
                $"{(topUp - afterHalf) / (topUp - topStanding) * 100:F0} %");
            Check("and after 5 s it is back to the standing size", Math.Abs(afterLong - topStanding) < 0.02f, $"{afterLong:F3} vs {topStanding:F3} m");
        }

        // ------------------------------------------------------------------ E: a joint the model drops
        Console.WriteLine("\nE. a wrist the model stops reporting keeps its place for a second, and is then forgotten");
        {
            var s = NewSizer(shrink: 0.05f);
            Body t = TPose();
            Settle(s, t, 0.5f);
            float rT = s.Current.radius;
            var valid = Enumerable.Repeat(true, 15).ToArray(); valid[5] = false; valid[11] = false;
            Body arms = Standing();
            s.Observe(arms.j, valid, arms.Axis, 0.4f);                        // both wrists missing: they were at 0.75 m in the last estimate that had them (t = 0)
            for (int i = 0; i < 30; i++) s.Step(true, 1f / 60f);
            float rHeld = s.Current.radius;
            s.Observe(arms.j, valid, arms.Axis, 0.9f);
            for (int i = 0; i < 30; i++) s.Step(true, 1f / 60f);
            float rHeld2 = s.Current.radius;
            s.Observe(arms.j, valid, arms.Axis, 1.6f);                        // 1.6 s after they were seen
            for (int i = 0; i < 120; i++) s.Step(true, 1f / 60f);
            float rGone = s.Current.radius;
            Check("0.4 s and 0.9 s after the wrists vanished, the capsule still covers where they were", rHeld > 0.8f && rHeld2 > 0.8f, $"R {rT:F2} -> {rHeld:F2} / {rHeld2:F2} m");
            Check("after more than a second without them it has shrunk to the rest of the body", rGone < 0.65f, $"R {rGone:F2} m");
        }

        // ------------------------------------------------------------------ F: the limits
        Console.WriteLine("\nF. an estimate with a joint far away (a ghost limb) cannot make the capsule huge");
        {
            var s = NewSizer();
            Body b = Standing();
            b.j[5] = b.j[5] + new Vector3(3.5f, 0, 0);                        // a wrist 3.5 m off
            Settle(s, b, 1f);
            Check("capped at the maximum radius, and flagged", Math.Abs(s.Current.radius - s.maxRadius) < 1e-3f && s.Clamped, $"R {s.Current.radius:F2} m, clamped {s.Clamped}");
            Body ok = Standing();
            s.Observe(ok.j, null, ok.Axis, 5f);
            for (int i = 0; i < 300; i++) s.Step(true, 1f / 60f);
            Check("the next sane estimate takes it back, unflagged", s.Current.radius < 0.65f && !s.Clamped, $"R {s.Current.radius:F2} m, clamped {s.Clamped}");
        }

        // ------------------------------------------------------------------ G: the bottom
        Console.WriteLine("\nG. the lower tip");
        {
            var s = NewSizer(followBottom: false);
            Body j = Standing().Moved(0, 0);
            for (int i = 0; i < 15; i++) j.j[i] += Vector3.up * 0.6f;         // a person standing on a 60 cm platform
            Settle(s, j, 1f);
            Check("not following the bottom: the tip stays on the floor, the body is inside", Math.Abs(s.Current.bottom - Authored.bottom) < 0.25f && Contained(s, j, j.Axis, out _), $"bottom {s.Current.bottom:F2}");
            var f = NewSizer(followBottom: true);
            Settle(f, j, 1f);
            Check("following the bottom: the tip rises with the feet, and the capsule is shorter", f.Current.bottom > Authored.bottom + 0.2f && Height(f) < Height(s) - 0.25f && Contained(f, j, j.Axis, out _),
                $"bottom {f.Current.bottom:F2}, height {Height(f):F2} vs {Height(s):F2} m");
            var low = NewSizer(followBottom: false);
            Body deep = Standing();
            for (int i = 0; i < 15; i++) deep.j[i] += Vector3.down * 0.5f;    // lower than the authored tip (a pit)
            Settle(low, deep, 1f);
            Check("a body lower than the authored tip still gets inside", low.Current.bottom < Authored.bottom && Contained(low, deep, deep.Axis, out _), $"bottom {low.Current.bottom:F2}");
        }

        // ------------------------------------------------------------------ H: nobody there
        Console.WriteLine("\nH. nobody followed: back to the authored size");
        {
            var s = NewSizer();
            Settle(s, TPose());
            for (int i = 0; i < 400; i++) s.Step(false, 1f / 60f);
            var c = s.Current;
            Check("authored again after a few seconds", Math.Abs(c.radius - Authored.radius) < 0.01f && Math.Abs(c.top - Authored.top) < 0.01f && Math.Abs(c.bottom - Authored.bottom) < 0.01f,
                $"R {c.radius:F2}, tips {c.bottom:F2} .. {c.top:F2}");
        }

        // ------------------------------------------------------------------ I: another capsule (a scene with different authoring)
        Console.WriteLine("\nI. a capsule authored differently (R 0.8, tips 0 .. 2.4) is fitted the same way");
        {
            var s = NewSizerAt(new FvpCapsuleFit.Shape { bottom = 0f, top = 2.4f, radius = 0.8f }, 0.5f, false);
            Body b = Standing(); for (int i = 0; i < 15; i++) b.j[i] += Vector3.up * (0.4f);
            Settle(s, b);
            Check("shrinks to the person, body inside", Contained(s, b, b.Axis, out _) && s.Current.top < 2.2f && s.Current.radius < 0.7f, $"R {s.Current.radius:F2}, tips {s.Current.bottom:F2} .. {s.Current.top:F2}");
        }

        // ------------------------------------------------------------------ K: only the skeleton decides
        Console.WriteLine("\nK. the size is a function of the skeleton alone: the same joints give the same capsule whatever the capsule was authored as");
        {
            var authoredAs = new[]
            {
                Authored,
                new FvpCapsuleFit.Shape { bottom = 5f, top = 9f, radius = 3f },        // somewhere else entirely, enormous
                new FvpCapsuleFit.Shape { bottom = -3f, top = -2.9f, radius = 0.05f },  // below the floor, tiny
            };
            double worstDiff = 0.0; string worstPose = "";
            foreach (var (name, body) in named)
            {
                var shapes = new List<FvpCapsuleFit.Shape>();
                foreach (var a in authoredAs)
                {
                    var s = NewSizerAt(a, 0.5f, true);                                  // Fit Bottom on: both tips from the skeleton
                    Settle(s, body, 10f);
                    shapes.Add(s.Current);
                }
                double d = 0.0;
                for (int i = 1; i < shapes.Count; i++)
                    d = Math.Max(d, Math.Max(Math.Abs(shapes[i].radius - shapes[0].radius), Math.Max(Math.Abs(shapes[i].top - shapes[0].top), Math.Abs(shapes[i].bottom - shapes[0].bottom))));
                if (d > worstDiff) { worstDiff = d; worstPose = name; }
            }
            Check("three capsules authored very differently end up the same for every pose", worstDiff < 1e-3, $"worst difference {worstDiff * 1000:F3} mm ({(worstDiff > 0 ? worstPose : "none")})");

            // and the history: a person who was in a T-pose a moment ago, one who was sitting, one never seen: the same standing body gives the same capsule after the shrink time has passed
            var ends = new List<FvpCapsuleFit.Shape>();
            foreach (var before in new Body[] { TPose(), Sitting(), null })
            {
                var s = NewSizerAt(Authored, 0.5f, true);
                if (before != null) Settle(s, before, 1f);
                Settle(s, Standing(), 10f);
                ends.Add(s.Current);
            }
            double h = Math.Max(Math.Abs(ends[0].top - ends[2].top), Math.Max(Math.Abs(ends[1].top - ends[2].top), Math.Max(Math.Abs(ends[0].radius - ends[2].radius), Math.Abs(ends[1].radius - ends[2].radius))));
            Check("... and whatever pose came before (once the shrink time has passed)", h < 1e-3, $"worst difference {h * 1000:F3} mm");
        }

        // ------------------------------------------------------------------ J: a person standing still, an estimator with noise
        Console.WriteLine("\nJ. standing still for 30 s, an estimate every 100 ms with 3 cm of noise on every joint; then the same with a wrist 20 cm off in 1 estimate of 20");
        foreach (bool spikes in new[] { false, true })
        {
            var s = NewSizer();
            Body still = Standing();
            var r = new List<float>(); var top = new List<float>();
            int bad = 0, frames = 0;
            for (int p = 0; p < 300; p++)
            {
                Body n = new Body();
                for (int i = 0; i < 15; i++)
                    n.j[i] = still.j[i] + new Vector3((float)(0.03 * Gauss()), (float)(0.03 * Gauss()), (float)(0.03 * Gauss()));
                if (spikes && p % 20 == 7) n.j[5] += new Vector3(0.20f, 0.05f, 0f);
                s.Observe(n.j, null, n.Axis, p * 0.1f);
                for (int f = 0; f < 6; f++)
                {
                    s.Step(true, 1f / 60f);
                    if (p >= 30) { r.Add(s.Current.radius); top.Add(s.Current.top); }
                    frames++;
                    if (!Contained(s, n, n.Axis, out _)) bad++;
                }
            }
            r.Sort(); top.Sort();
            float rMed = r[r.Count / 2], rP95 = r[(int)(r.Count * 0.95)], tMed = top[top.Count / 2], tP95 = top[(int)(top.Count * 0.95)];
            Console.WriteLine($"     {(spikes ? "with spikes   " : "noise only   ")} radius {r[0]:F2} .. {r[r.Count - 1]:F2} m (median {rMed:F2}), top {top[0]:F2} .. {top[top.Count - 1]:F2} m (median {tMed:F2})");
            Check($"{(spikes ? "with spikes" : "noise only")}: the noisy estimate itself is always inside", bad == 0, $"{bad} of {frames} frames outside");
            float limitR = spikes ? 0.15f : 0.08f, limitT = spikes ? 0.15f : 0.08f;
            Check($"{(spikes ? "with spikes" : "noise only")}: radius and top stay within {limitR * 100:F0} cm of their medians 95 % of the time", rP95 - rMed < limitR && tP95 - tMed < limitT,
                $"p95 - median: radius {(rP95 - rMed) * 100:F1} cm, top {(tP95 - tMed) * 100:F1} cm");
        }

        Console.WriteLine($"\n{(fails == 0 ? "ALL PASSED" : fails + " FAILED")}");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
