using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

// Replays the REAL estimates of the recorded session (capsule_eval.py --csv) through OperatorTrack with a simulated pipeline
// (a latency, an estimate rate) and scores where the track puts the person against where the person really is at that instant.
static class R
{
    sealed class Series
    {
        public List<double> t = new List<double>(); public List<Vector2> gt = new List<Vector2>();
        public List<bool> has = new List<bool>(); public List<Vector2> est = new List<Vector2>();
        public Vector2 GtAt(double time)
        {
            if (time <= t[0]) return gt[0];
            int lo = 0, hi = t.Count - 1;
            if (time >= t[hi]) return gt[hi];
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (t[mid] <= time) lo = mid; else hi = mid; }
            double k = (time - t[lo]) / (t[hi] - t[lo]);
            return Vector2.Lerp(gt[lo], gt[hi], (float)k);
        }
    }

    static Dictionary<int, Series> Load(string path)
    {
        var d = new Dictionary<int, Series>();
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            var c = line.Split(',');
            int person = int.Parse(c[2], CultureInfo.InvariantCulture);
            if (!d.TryGetValue(person, out Series s)) d[person] = s = new Series();
            s.t.Add(double.Parse(c[1], CultureInfo.InvariantCulture));
            s.gt.Add(new Vector2(float.Parse(c[3], CultureInfo.InvariantCulture), float.Parse(c[4], CultureInfo.InvariantCulture)));
            bool has = c[5] == "1";
            s.has.Add(has);
            s.est.Add(new Vector2(float.Parse(c[6], CultureInfo.InvariantCulture), float.Parse(c[7], CultureInfo.InvariantCulture)));
        }
        return d;
    }

    struct Result { public List<double> pred, filt, raw; public double tracking, total; public int wrong, samples; }

    static Result Run(Dictionary<int, Series> data, int follow, int stride, double latency, Action<OperatorTrack> cfg, bool useIds)
    {
        var res = new Result { pred = new List<double>(), filt = new List<double>(), raw = new List<double>() };
        Series me = data[follow];
        int n = me.t.Count;
        var tr = new OperatorTrack { confirmEstimates = 2, acquireRadius = 1.0f };
        cfg?.Invoke(tr);
        // the estimates that exist: every stride-th frame, available at t + latency
        var events = new List<(double tc, double arr, int idx)>();
        for (int i = 0; i < n; i += stride) events.Add((me.t[i], me.t[i] + latency, i));
        int next = 0;
        double t0 = me.t[0], tEnd = me.t[n - 1];
        double firstLock = double.NaN;
        for (double now = t0; now <= tEnd; now += 1.0 / 60.0)
        {
            while (next < events.Count && events[next].arr <= now)
            {
                var e = events[next++];
                var cands = new List<OperatorTrack.Candidate>();
                foreach (var kv in data)
                {
                    Series s = kv.Value;
                    int i = e.idx;
                    if (i >= s.t.Count || !s.has[i]) continue;
                    cands.Add(new OperatorTrack.Candidate { id = useIds ? kv.Key + 1 : 100 + cands.Count, pos = new Vector3(s.est[i].x, 1f, s.est[i].y) });
                }
                tr.Update(cands, e.tc, me.GtAt(t0), null);
            }
            tr.Tick(now);
            if (tr.Current == OperatorTrack.State.Tracking && double.IsNaN(firstLock)) firstLock = now;
            if (double.IsNaN(firstLock) || now < firstLock + 1.0) continue;
            res.total += 1.0 / 60.0;
            if (tr.Current != OperatorTrack.State.Tracking) continue;
            res.tracking += 1.0 / 60.0;
            Vector2 truth = me.GtAt(now);
            Vector2 p = tr.PredictXZ(now, 0.6f);
            res.pred.Add(Vector2.Distance(p, truth));
            res.filt.Add(Vector2.Distance(tr.Position, truth));
            res.raw.Add(Vector2.Distance(tr.LastMeasurement, truth));
            // the other person's truth, to see whether the track stayed on the right body
            foreach (var kv in data) if (kv.Key != follow && Vector2.Distance(p, kv.Value.GtAt(now)) < Vector2.Distance(p, truth)) { res.wrong++; break; }
            res.samples++;
        }
        return res;
    }

    static double Pct(List<double> v, double p) { if (v.Count == 0) return double.NaN; var s = v.OrderBy(x => x).ToList(); return s[Math.Min(s.Count - 1, (int)(p * s.Count))]; }

    static void Main(string[] args)
    {
        string path = args[0];
        var data = Load(path);
        var dts = new List<double>(); for (int i = 1; i < data[0].t.Count; i++) if (data[0].t[i] > data[0].t[i - 1]) dts.Add(data[0].t[i] - data[0].t[i - 1]);
        dts.Sort(); double fps = 1.0 / dts[dts.Count / 2];
        Console.WriteLine($"{Path.GetFileName(path)}: {data.Count} people, {data[0].t.Count} frames at about {fps:F1} per second, {data[0].t.Last():F1} s");
        foreach (var kv in data)
        {
            var s = kv.Value; int has = s.has.Count(b => b);
            double len = 0; for (int i = 1; i < s.gt.Count; i++) len += Vector2.Distance(s.gt[i], s.gt[i - 1]);
            Console.WriteLine($"  person {kv.Key}: estimates in {has} of {s.t.Count} frames ({100.0 * has / s.t.Count:F0} %), walked {len:F0} m ({len / s.t.Last():F2} m/s average)");
        }

        float noise = args.Length > 1 ? float.Parse(args[1], CultureInfo.InvariantCulture) : 0.06f;
        float accel = args.Length > 2 ? float.Parse(args[2], CultureInfo.InvariantCulture) : 1.2f;
        Console.WriteLine($"\ntracker: measurement noise {noise * 100:F0} cm, maneuver {accel:F1} m/s^2");
        Console.WriteLine("pipeline                       | predicted (median/p95/p99/max cm) | raw newest estimate (median/p95/p99) | tracked | wrong body");
        foreach (var (stride, label) in new[] { 1, 2, 3, 6 }.Select(k => (k, (fps / k).ToString("F1", CultureInfo.InvariantCulture) + "/s")))
            foreach (double lat in new[] { 0.05, 0.10, 0.15, 0.25, 0.35, 0.50 })
            {
                var all = new List<double>(); var allRaw = new List<double>(); double tracking = 0, total = 0; int wrong = 0, samples = 0;
                foreach (int person in data.Keys)
                {
                    var r = Run(data, person, stride, lat, t => { t.measurementNoise = noise; t.maneuverAccel = accel; }, true);
                    all.AddRange(r.pred); allRaw.AddRange(r.raw); tracking += r.tracking; total += r.total; wrong += r.wrong; samples += r.samples;
                }
                Console.WriteLine($"{label,5} estimates, {lat * 1000,3:F0} ms late    | {Pct(all, .5) * 100,5:F1} /{Pct(all, .95) * 100,5:F1} /{Pct(all, .99) * 100,5:F1} /{all.Max() * 100,5:F0}        | {Pct(allRaw, .5) * 100,5:F1} /{Pct(allRaw, .95) * 100,5:F1} /{Pct(allRaw, .99) * 100,5:F1}               | {100 * tracking / total,4:F0} %  | {100.0 * wrong / Math.Max(1, samples),4:F1} %");
            }
    }
}
