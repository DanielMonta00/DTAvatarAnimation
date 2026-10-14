using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Scenarios for OperatorTrack, outside Unity: a person walks, an "estimator" reports him every ~150 ms with noise and latency, and the
// track answers 60 times a second. Everything is compared with where the person really is at that instant.
static class P
{
    static int fails;
    static readonly System.Random rnd = new System.Random(12345);

    static double Gauss() { double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble(); return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2); }

    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: {detail}");
        if (!ok) fails++;
    }

    static double Pct(List<double> v, double p)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, (int)(p * s.Count))];
    }

    // A path: positions at the corner times, linear in between.
    sealed class Path
    {
        readonly (double t, Vector2 p)[] pts;
        public Path(params (double, Vector2)[] pts) { this.pts = pts; }
        public Vector2 At(double t)
        {
            if (t <= pts[0].t) return pts[0].p;
            for (int i = 1; i < pts.Length; i++)
                if (t <= pts[i].t)
                {
                    double k = (t - pts[i - 1].t) / (pts[i].t - pts[i - 1].t);
                    return Vector2.Lerp(pts[i - 1].p, pts[i].p, (float)k);
                }
            return pts[pts.Length - 1].p;
        }
    }

    static OperatorTrack.Candidate C(int id, Vector2 p, double noise = 0.0)
        => new OperatorTrack.Candidate { id = id, pos = new Vector3(p.x + (float)(noise * Gauss()), 1.0f, p.y + (float)(noise * Gauss())) };

    // Runs one walker through the track. Returns errors (m) of: predicted output, filtered-no-prediction, raw newest estimate.
    static (List<double> pred, List<double> filt, List<double> raw) Walk(Path path, double duration, double period, double latency, double noise,
                                                                       Action<OperatorTrack> configure = null, double jitter = 0.02)
    {
        var tr = new OperatorTrack { confirmEstimates = 2, measurementNoise = (float)Math.Max(0.02, noise) };     // tuned to the noise of the scenario, as a user sets it
        configure?.Invoke(tr);
        var pred = new List<double>(); var filt = new List<double>(); var raw = new List<double>();

        // estimates: capture time, arrival time
        var est = new List<(double tc, double arr)>();
        for (double tc = 0.0; tc < duration; tc += period) est.Add((tc, tc + latency + jitter * rnd.NextDouble()));
        int next = 0;
        for (double now = 0.0; now < duration; now += 1.0 / 60.0)
        {
            while (next < est.Count && est[next].arr <= now)
            {
                double tc = est[next].tc;
                var list = new List<OperatorTrack.Candidate> { C(7, path.At(tc), noise) };
                tr.Update(list, tc, path.At(0), null);
                next++;
            }
            tr.Tick(now);
            if (tr.Current != OperatorTrack.State.Tracking || now < 2.0) continue;      // let the filter settle for 2 s
            Vector2 truth = path.At(now);
            pred.Add(Vector2.Distance(tr.PredictXZ(now, 0.6f), truth));
            filt.Add(Vector2.Distance(tr.Position, truth));
            raw.Add(Vector2.Distance(tr.LastMeasurement, truth));
        }
        return (pred, filt, raw);
    }

    static void Main()
    {
        Console.WriteLine("OperatorTrack scenarios\n");

        // ------------------------------------------------------------------ A: walking with turns
        {
            var path = new Path((0, new Vector2(0, 0)), (6, new Vector2(7.2f, 0)), (12, new Vector2(7.2f, 7.2f)), (18, new Vector2(0, 7.2f)), (24, new Vector2(0, 0)), (30, new Vector2(7.2f, 0)));
            Console.WriteLine("A. a person walks at 1.2 m/s with 90-degree corners; estimates every 150 ms, 120 ms late, 8 cm noise");
            var (pred, filt, raw) = Walk(path, 30, 0.15, 0.12, 0.08);
            Console.WriteLine($"     predicted  median {Pct(pred, .5) * 100:F1} cm  p95 {Pct(pred, .95) * 100:F1}  max {pred.Max() * 100:F1}");
            Console.WriteLine($"     filtered   median {Pct(filt, .5) * 100:F1} cm  p95 {Pct(filt, .95) * 100:F1}  max {filt.Max() * 100:F1}");
            Console.WriteLine($"     raw newest median {Pct(raw, .5) * 100:F1} cm  p95 {Pct(raw, .95) * 100:F1}  max {raw.Max() * 100:F1}");
            Check("prediction beats the raw newest estimate (median)", Pct(pred, .5) < Pct(raw, .5), $"{Pct(pred, .5) * 100:F1} < {Pct(raw, .5) * 100:F1} cm");
            Check("prediction beats the raw newest estimate (p95)", Pct(pred, .95) < Pct(raw, .95), $"{Pct(pred, .95) * 100:F1} < {Pct(raw, .95) * 100:F1} cm");
            Check("the median of the prediction is under 65 % of the raw median", Pct(pred, .5) < 0.65 * Pct(raw, .5), $"{Pct(pred, .5) * 100:F1} vs {Pct(raw, .5) * 100:F1} cm");
            Check("p95 of the prediction under 35 cm even with sharp 90-degree corners", Pct(pred, .95) < 0.35, $"{Pct(pred, .95) * 100:F1} cm");
        }

        // ------------------------------------------------------------------ A1: a straight line (no corners): what the noise alone costs
        {
            var path = new Path((0, new Vector2(0, 0)), (30, new Vector2(36, 0)));
            Console.WriteLine("\nA1. a straight walk at 1.2 m/s, same pipeline: only the noise and the latency are left");
            var (pred, filt, raw) = Walk(path, 30, 0.15, 0.12, 0.08);
            Console.WriteLine($"     predicted  median {Pct(pred, .5) * 100:F1} cm  p95 {Pct(pred, .95) * 100:F1}  max {pred.Max() * 100:F1}");
            Console.WriteLine($"     raw newest median {Pct(raw, .5) * 100:F1} cm  p95 {Pct(raw, .95) * 100:F1}  max {raw.Max() * 100:F1}");
            Check("on a straight line the prediction is within 16 cm (median) and 32 cm (p95) with 8 cm of noise", Pct(pred, .5) < 0.16 && Pct(pred, .95) < 0.32, $"{Pct(pred, .5) * 100:F1} / {Pct(pred, .95) * 100:F1} cm");
        }

        // ------------------------------------------------------------------ A2: slow estimates and long latency (heavy GPU)
        {
            var path = new Path((0, new Vector2(0, 0)), (10, new Vector2(12, 0)), (20, new Vector2(12, 12)), (30, new Vector2(0, 12)));
            Console.WriteLine("\nA2. the same walker, estimates every 400 ms, 350 ms late (a GPU shared with a heavy scene)");
            var (pred, filt, raw) = Walk(path, 30, 0.40, 0.35, 0.08);
            Console.WriteLine($"     predicted  median {Pct(pred, .5) * 100:F1} cm  p95 {Pct(pred, .95) * 100:F1}  max {pred.Max() * 100:F1}");
            Console.WriteLine($"     raw newest median {Pct(raw, .5) * 100:F1} cm  p95 {Pct(raw, .95) * 100:F1}  max {raw.Max() * 100:F1}");
            Check("prediction beats the raw newest estimate (p95)", Pct(pred, .95) < Pct(raw, .95), $"{Pct(pred, .95) * 100:F1} < {Pct(raw, .95) * 100:F1} cm");
        }

        // ------------------------------------------------------------------ B: stop and go
        {
            var path = new Path((0, new Vector2(0, 0)), (6, new Vector2(7.2f, 0)), (6.001, new Vector2(7.2f, 0)), (9, new Vector2(7.2f, 0)), (15, new Vector2(14.4f, 0)));
            Console.WriteLine("\nB. walks 6 s, stops dead for 3 s, walks on");
            var (pred, filt, raw) = Walk(path, 15, 0.15, 0.12, 0.05);
            // the worst overshoot after the stop is what matters: the capsule runs ahead of a person who stopped
            Console.WriteLine($"     predicted  median {Pct(pred, .5) * 100:F1} cm  p95 {Pct(pred, .95) * 100:F1}  max {pred.Max() * 100:F1}");
            Check("overshoot after a dead stop stays under 45 cm", pred.Max() < 0.45, $"max {pred.Max() * 100:F1} cm");
        }

        // ------------------------------------------------------------------ C: ghost for one estimate
        {
            Console.WriteLine("\nC. searching: a ghost for ONE estimate nearer than the real person must not be taken; the person (in two estimates) is");
            var tr = new OperatorTrack { confirmEstimates = 2 };
            var centre = new Vector2(0, 0);
            double t = 0;
            // estimate 1: ghost at 0.4 m, person at 2 m
            tr.Update(new List<OperatorTrack.Candidate> { C(1, new Vector2(0.4f, 0)), C(2, new Vector2(2, 0)) }, t += 0.15, centre, null);
            Check("first sight of anyone does not lock (confirm = 2)", tr.Current == OperatorTrack.State.Searching, tr.Current.ToString());
            // estimate 2: the ghost is gone, the person is there
            tr.Update(new List<OperatorTrack.Candidate> { C(2, new Vector2(2, 0)) }, t += 0.15, centre, null);
            Check("the ghost left: the one nearest is the person, seen once", tr.Current == OperatorTrack.State.Searching, tr.Current.ToString());
            tr.Update(new List<OperatorTrack.Candidate> { C(2, new Vector2(2.05f, 0)) }, t += 0.15, centre, null);
            Check("the person, seen twice in a row, is taken", tr.Current == OperatorTrack.State.Tracking && tr.LockedId == 2, $"{tr.Current} id {tr.LockedId}");
        }

        // ------------------------------------------------------------------ D: acquire radius
        {
            Console.WriteLine("\nD. acquire radius");
            var tr = new OperatorTrack { confirmEstimates = 1, acquireRadius = 1.0f };
            tr.Update(new List<OperatorTrack.Candidate> { C(1, new Vector2(3, 0)) }, 0.1, Vector2.zero, null);
            Check("a person outside the radius is not taken", tr.Current == OperatorTrack.State.Searching, tr.Current.ToString());
            tr.Update(new List<OperatorTrack.Candidate> { C(1, new Vector2(0.8f, 0)) }, 0.2, Vector2.zero, null);
            Check("a person inside it is", tr.Current == OperatorTrack.State.Tracking, tr.Current.ToString());
        }

        // ------------------------------------------------------------------ E: loss and reacquisition
        {
            Console.WriteLine("\nE. lost for 2 s then back, and gone for 12 s");
            var tr = new OperatorTrack { confirmEstimates = 1, holdSeconds = 1.0f, forgetSeconds = 8.0f };
            double t = 0;
            for (int i = 0; i < 10; i++) { tr.Update(new List<OperatorTrack.Candidate> { C(5, new Vector2((float)(1.0 * t), 0)) }, t, Vector2.zero, null); tr.Tick(t); t += 0.15; }
            Check("following", tr.Current == OperatorTrack.State.Tracking, $"at x = {tr.Position.x:F2}");
            double tLast = t - 0.15;
            // nothing for 2 s
            for (double tt = t; tt < tLast + 2.0; tt += 1.0 / 60) tr.Tick(tt);
            Check("no estimate for 2 s: lost", tr.Current == OperatorTrack.State.Lost, tr.Current.ToString());
            Vector2 held = tr.PredictXZ(tLast + 2.0, 0.6f);
            Check("while lost the prediction holds still (does not run away)", Vector2.Distance(held, tr.PredictXZ(tLast + 9.0, 0.6f)) < 1e-4f, $"{held.x:F2} vs {tr.PredictXZ(tLast + 9.0, 0.6f).x:F2}");
            // he reappears 0.5 m beyond the held position
            double tBack = tLast + 2.1;
            bool used = tr.Update(new List<OperatorTrack.Candidate> { C(9, new Vector2(held.x + 0.5f, 0)) }, tBack, Vector2.zero, null);
            tr.Tick(tBack);
            Check("a body near the held position is taken back (even with a new id)", used && tr.Current == OperatorTrack.State.Tracking && tr.Reacquisitions == 1, $"used {used}, {tr.Current}, reacq {tr.Reacquisitions}");

            // gone for 12 s: forgotten
            for (double tt = tBack; tt < tBack + 12.0; tt += 1.0 / 60) tr.Tick(tt);
            Check("gone for 12 s: forgotten, searching again", tr.Current == OperatorTrack.State.Searching, tr.Current.ToString());
        }

        // ------------------------------------------------------------------ F: two followers never share a body
        {
            Console.WriteLine("\nF. two tracks and two people: each takes a different one");
            var a = new OperatorTrack { confirmEstimates = 1 };
            var b = new OperatorTrack { confirmEstimates = 1 };
            Func<int, bool> claimedByA = id => a.Current != OperatorTrack.State.Searching && a.LockedId == id;
            var both = new List<OperatorTrack.Candidate> { C(1, new Vector2(0, 0)), C(2, new Vector2(1.5f, 0)) };
            a.Update(both, 0.1, new Vector2(0, 0), null);
            b.Update(both, 0.1, new Vector2(0, 0), claimedByA);        // b also searches around the origin: the nearest is a's person
            Check("a takes the nearest (id 1)", a.LockedId == 1, $"{a.LockedId}");
            Check("b takes the other (id 2), not a's body", b.LockedId == 2, $"{b.LockedId}");
        }

        // ------------------------------------------------------------------ G: the clock goes back
        {
            Console.WriteLine("\nG. the scene clock goes back (rewind)");
            var tr = new OperatorTrack { confirmEstimates = 1 };
            tr.Update(new List<OperatorTrack.Candidate> { C(1, new Vector2(1, 1)) }, 10.0, Vector2.zero, null);
            tr.Update(new List<OperatorTrack.Candidate> { C(1, new Vector2(1.1f, 1)) }, 10.2, Vector2.zero, null);
            tr.Update(new List<OperatorTrack.Candidate> { C(1, new Vector2(1.1f, 1)) }, 4.0, Vector2.zero, null);   // 6 s earlier
            Check("the track starts over (and takes the body again)", tr.Acquisitions == 2, $"acquisitions {tr.Acquisitions}, state {tr.Current}");
        }

        // ------------------------------------------------------------------ H: one bad estimate
        {
            Console.WriteLine("\nH. one estimate 0.9 m off among good ones (a limb taken for the body)");
            var tr = new OperatorTrack { confirmEstimates = 1 };
            double t = 0;
            for (int i = 0; i < 20; i++) { tr.Update(new List<OperatorTrack.Candidate> { C(1, new Vector2(2.0f, 1.0f), 0.02) }, t, Vector2.zero, null); t += 0.15; }
            Vector2 before = tr.Position;
            tr.Update(new List<OperatorTrack.Candidate> { C(1, new Vector2(2.9f, 1.0f)) }, t, Vector2.zero, null);
            float shift = Vector2.Distance(before, tr.Position);
            Check("the outlier moves the filtered position less than 40 cm", shift < 0.40f, $"{shift * 100:F1} cm");
            t += 0.15;
            for (int i = 0; i < 4; i++) { tr.Update(new List<OperatorTrack.Candidate> { C(1, new Vector2(2.0f, 1.0f), 0.02) }, t, Vector2.zero, null); t += 0.15; }
            Check("and a few good estimates later it is back within 12 cm", Vector2.Distance(tr.Position, new Vector2(2, 1)) < 0.12f, $"{Vector2.Distance(tr.Position, new Vector2(2, 1)) * 100:F1} cm");
        }

        // ------------------------------------------------------------------ I: two people crossing
        {
            Console.WriteLine("\nI. two people walk past each other in lanes 1.0 m apart (they pass at t = 2.5 s), the tracker's ids stay right");
            var pa = new Path((0, new Vector2(-3, 0.0f)), (5, new Vector2(3, 0.0f)));
            var pb = new Path((0, new Vector2(3, 1.0f)), (5, new Vector2(-3, 1.0f)));
            var tr = new OperatorTrack { confirmEstimates = 2 };
            int wrong = 0, total = 0;
            for (double tc = 0; tc < 5.0; tc += 0.15)
            {
                var list = new List<OperatorTrack.Candidate> { C(1, pa.At(tc), 0.06), C(2, pb.At(tc), 0.06) };
                tr.Update(list, tc, pa.At(0), null);
                if (tr.Current == OperatorTrack.State.Tracking && tc > 1.0) { total++; if (tr.LockedId != 1) wrong++; }
            }
            Check("keeps the same person through the crossing (ids correct)", total > 10 && wrong == 0, $"{wrong} of {total}");

            Console.WriteLine("   ... and when the tracker swaps the ids at the crossing (ids 1 and 2 exchange at t = 2.5 s)");
            var tr2 = new OperatorTrack { confirmEstimates = 2 };
            int onA = 0, tot2 = 0;
            for (double tc = 0; tc < 5.0; tc += 0.15)
            {
                int ida = tc < 2.5 ? 1 : 2, idb = tc < 2.5 ? 2 : 1;
                var list = new List<OperatorTrack.Candidate> { C(ida, pa.At(tc), 0.06), C(idb, pb.At(tc), 0.06) };
                tr2.Update(list, tc, pa.At(0), null);
                if (tr2.Current == OperatorTrack.State.Tracking && tc > 3.5) { tot2++; if (Vector2.Distance(tr2.Position, pa.At(tc)) < 0.5f) onA++; }
            }
            Console.WriteLine($"     the track stays on person A in {onA} of {tot2} estimates after the swap (a swap of ids costs the id bonus; the prediction has to carry it)");
            Check("after an id swap at a crossing the track stays on the person it was following", tot2 > 0 && onA == tot2, $"{onA}/{tot2}");
        }

        // ------------------------------------------------------------------ J: dropout while a neighbour passes
        {
            Console.WriteLine("\nJ. the followed person's estimate drops out while another person passes 0.9 m from him");
            var tr = new OperatorTrack { confirmEstimates = 2 };
            double t = 0;
            for (int i = 0; i < 8; i++)
            {
                tr.Update(new List<OperatorTrack.Candidate> { C(1, new Vector2(0, 0)), C(2, new Vector2(3, 0)) }, t, Vector2.zero, null);
                t += 0.15;
            }
            Check("following person 1, person 2 seen beside him", tr.Current == OperatorTrack.State.Tracking && tr.LockedId == 1, $"{tr.Current} {tr.LockedId}");
            bool taken = false;
            for (int i = 0; i < 6; i++)     // person 1 vanishes; person 2 walks up to 0.9 m from where person 1 stands
            {
                float x = 3f - (2.1f * (i + 1) / 6f);
                taken |= tr.Update(new List<OperatorTrack.Candidate> { C(2, new Vector2(x, 0)) }, t, Vector2.zero, null);
                t += 0.15;
            }
            Check("the neighbour (a known other person) is not taken for him", !taken && tr.LockedId == 1, $"taken {taken}, locked {tr.LockedId}");
        }

        // ------------------------------------------------------------------ K: renumbered after a dropout
        {
            Console.WriteLine("\nK. the estimator loses the person and numbers him again (id 1 -> id 9), at the place he would be");
            var tr = new OperatorTrack { confirmEstimates = 2 };
            double t = 0;
            for (int i = 0; i < 8; i++) { tr.Update(new List<OperatorTrack.Candidate> { C(1, new Vector2((float)(1.0 * t), 0)) }, t, Vector2.zero, null); t += 0.15; }
            bool first = tr.Update(new List<OperatorTrack.Candidate> { C(9, new Vector2((float)(1.0 * (t + 0.15)), 0)) }, t + 0.15, Vector2.zero, null);
            Check("a new id at the right place is NOT taken at its first sighting", !first && tr.LockedId == 1, $"used {first}, locked {tr.LockedId}");
            bool second = tr.Update(new List<OperatorTrack.Candidate> { C(9, new Vector2((float)(1.0 * (t + 0.30)), 0)) }, t + 0.30, Vector2.zero, null);
            Check("... and is, at its second", second && tr.LockedId == 9, $"used {second}, locked {tr.LockedId}");
        }

        // ------------------------------------------------------------------ L: one-estimate ghost beside the person
        {
            Console.WriteLine("\nL. the followed id is there, and a ghost with a new id appears right on the prediction for one estimate");
            var tr = new OperatorTrack { confirmEstimates = 2 };
            double t = 0;
            for (int i = 0; i < 8; i++) { tr.Update(new List<OperatorTrack.Candidate> { C(1, new Vector2(0, 0), 0.01) }, t, Vector2.zero, null); t += 0.15; }
            tr.Update(new List<OperatorTrack.Candidate> { C(1, new Vector2(0.15f, 0)), C(77, new Vector2(0.02f, 0)) }, t, Vector2.zero, null);
            Check("the ghost does not replace the followed body (he is only 15 cm off)", tr.LockedId == 1, $"locked {tr.LockedId}");
        }

        // ------------------------------------------------------------------ M: a second, fading skeleton on the same body (seen in the scratch Unity run)
        {
            Console.WriteLine("\nM. the model reports the same person twice: the followed id turns into a weak duplicate, and the sharp skeleton has a NEW id");
            var tr = new OperatorTrack { confirmEstimates = 2 };
            OperatorTrack.Candidate P(int id, float x, float z, float q) => new OperatorTrack.Candidate { id = id, pos = new Vector3(x, 1, z), quality = q };
            double t = 0;
            float x0 = 0f;
            for (int i = 0; i < 8; i++) { tr.Update(new List<OperatorTrack.Candidate> { P(15, x0 += 0.08f, 0f, 0.84f) }, t, Vector2.zero, null); t += 0.067; }
            for (int i = 0; i < 4; i++)
            {
                x0 += 0.08f;
                tr.Update(new List<OperatorTrack.Candidate> { P(52, x0, 0f, 0.82f), P(15, x0 - 0.05f - 0.03f * i, 0.12f + 0.03f * i, 0.65f - 0.1f * i) }, t, Vector2.zero, null);
                t += 0.067;
            }
            Check("the sharp skeleton represents the body at once (no waiting for the old id to vanish)", tr.LockedId == 52, $"locked {tr.LockedId}");
            bool used = false;
            for (int i = 0; i < 12; i++) { x0 += 0.08f; used = tr.Update(new List<OperatorTrack.Candidate> { P(52, x0, 0f, 0.84f) }, t, Vector2.zero, null); t += 0.067; }
            Check("and the track follows him on", used && tr.LockedId == 52 && Vector2.Distance(tr.Position, new Vector2(x0, 0f)) < 0.05f, $"{Vector2.Distance(tr.Position, new Vector2(x0, 0f)) * 100:F1} cm off");
        }

        // ------------------------------------------------------------------ N: a real neighbour is never merged into the followed body
        {
            Console.WriteLine("\nN. two real people 0.8 m apart stay two bodies (the neighbour is not merged into the followed person)");
            var tr = new OperatorTrack { confirmEstimates = 2 };
            double t = 0;
            for (int i = 0; i < 8; i++) { tr.Update(new List<OperatorTrack.Candidate> { C(1, new Vector2(0, 0)), C(2, new Vector2(0.8f, 0)) }, t, Vector2.zero, null); t += 0.1; }
            Check("follows person 1", tr.LockedId == 1, $"{tr.LockedId}");
            bool taken = tr.Update(new List<OperatorTrack.Candidate> { C(2, new Vector2(0.8f, 0)) }, t, Vector2.zero, null);
            Check("with person 1 missing, person 2 at 0.8 m is not taken for him", !taken && tr.LockedId == 1, $"taken {taken}, locked {tr.LockedId}");
        }

        Console.WriteLine($"\n{(fails == 0 ? "ALL PASSED" : fails + " FAILED")}");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
