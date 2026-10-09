using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

// Offline accuracy of FvpMultiViewLifter on the recorded session: ViTPose's real 2D detections (dumped by vit_dump.py), the real
// calibrated cameras, the real 3D ground truth. Same C# code as the Unity component runs.
static class Program
{
    static double D(object o) => Convert.ToDouble(o);
    static double[] Vec(object o) => ((List<object>)o).Select(D).ToArray();
    static readonly string[] Shared = { "left_shoulder", "right_shoulder", "left_elbow", "right_elbow", "left_wrist", "right_wrist", "left_hip", "right_hip", "left_knee", "right_knee", "left_ankle", "right_ankle" };
    static readonly int[] CocoOf = { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

    sealed class Stats
    {
        public readonly List<double>[] err = Enumerable.Range(0, 12).Select(_ => new List<double>()).ToArray();
        public int gt, matched, persons3d, ghosts, frames, nViews;
        public readonly List<double> reproj = new List<double>();
        public double ms;
    }

    static int Main(string[] args)
    {
        var root = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(args[0]));
        double scale = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1.0;
        var opts = FvpMultiViewLifter.Options.Default;
        for (int i = 2; i + 1 < args.Length; i += 2)
        {
            double val = double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            switch (args[i])
            {
                case "--conf": opts.minConf = val; break;
                case "--gate": opts.groupGateMetres = val; break;
                case "--inlier": opts.inlierMetres = val; break;
                case "--minjoints": opts.minJoints = (int)val; break;
            }
        }

        var cams = new List<FvpCameraModel>();
        foreach (Dictionary<string, object> c in ((List<object>)root["cameras"]).Cast<Dictionary<string, object>>())
        {
            int w = (int)D(c["w"]), h = (int)D(c["h"]);
            var m = FvpCameraModel.FromUnityAxes((string)c["name"], w, h, Vec(c["pos"]), new double[3], new double[3], new double[3],
                                                 D(c["fx"]), D(c["fy"]), D(c["cx"]) - 0.5, D(c["cy"]) - 0.5, null, 0);
            double[] rot = Vec(c["rot"]);
            for (int i = 0; i < 9; i++) m.rot[i] = rot[i];
            cams.Add(m);
        }
        var frames = ((List<object>)root["frames"]).Cast<Dictionary<string, object>>().ToList();

        // camera import check: ground-truth 3D shoulders projected into each view should sit inside the image and agree with the rays
        double rayWorst = 0;
        var o = new double[3]; var d = new double[3];
        foreach (var fr in frames.Take(5))
            foreach (Dictionary<string, object> g in ((List<object>)fr["gt"]).Cast<Dictionary<string, object>>())
            {
                double[] p = Vec(g["left_shoulder"]);
                foreach (FvpCameraModel cm in cams)
                    if (cm.Project(p[0], p[1], p[2], out double u, out double v) && cm.TryRay(u, v, o, d))
                    {
                        // distance of the point from its own ray
                        double rx = p[0] - o[0], ry = p[1] - o[1], rz = p[2] - o[2];
                        double t = rx * d[0] + ry * d[1] + rz * d[2];
                        double dd = Math.Sqrt(Math.Max(0, rx * rx + ry * ry + rz * rz - t * t));
                        rayWorst = Math.Max(rayWorst, dd);
                    }
            }
        Console.WriteLine($"camera model check: a ground-truth point is {rayWorst * 1000:F3} mm from the ray cast through its own projection");

        // subsets of views: all, every 3, every 2
        int nCam = cams.Count;
        var subsets = new List<int[]> { Enumerable.Range(0, nCam).ToArray() };
        for (int a = 0; a < nCam; a++) subsets.Add(Enumerable.Range(0, nCam).Where(x => x != a).ToArray());
        for (int a = 0; a < nCam; a++) for (int b = a + 1; b < nCam; b++) subsets.Add(new[] { a, b });

        var results = new Dictionary<string, Stats>();
        foreach (int[] subset in subsets)
        {
            var st = new Stats { nViews = subset.Length };
            string key = string.Join("+", subset.Select(x => x + 1));
            results[key] = st;
            var swatch = new Stopwatch();
            foreach (var fr in frames)
            {
                var gts = ((List<object>)fr["gt"]).Cast<Dictionary<string, object>>().Select(g => Shared.Select(n => Vec(g[n])).ToArray()).ToList();
                var dets = ((List<object>)fr["dets"]).Cast<List<object>>().ToList();

                var perView = new IList<MvPerson2D>[subset.Length];
                var camSel = new FvpCameraModel[subset.Length];
                for (int s = 0; s < subset.Length; s++)
                {
                    camSel[s] = cams[subset[s]];
                    var list = new List<MvPerson2D>();
                    foreach (Dictionary<string, object> pd in dets[subset[s]].Cast<Dictionary<string, object>>())
                    {
                        var kp = ((List<object>)pd["kp"]).Cast<List<object>>().ToList();
                        list.Add(new MvPerson2D { view = s, x = kp.Select(k => D(k[0])).ToArray(), y = kp.Select(k => D(k[1])).ToArray(), conf = kp.Select(k => D(k[2])).ToArray() });
                    }
                    perView[s] = list;
                }

                swatch.Restart();
                List<MvPerson3D> persons = FvpMultiViewLifter.Lift(perView, camSel, opts);
                swatch.Stop();
                st.ms += swatch.Elapsed.TotalMilliseconds;
                st.frames++; st.gt += gts.Count; st.persons3d += persons.Count;

                // greedy match to the ground truth by the mean distance of the shared joints
                var pairs = new List<(double c, int p, int g)>();
                for (int p = 0; p < persons.Count; p++)
                    for (int g = 0; g < gts.Count; g++)
                    {
                        double sum = 0; int n = 0;
                        for (int j = 0; j < 12; j++)
                            if (persons[p].valid[CocoOf[j]]) { sum += Dist(persons[p], CocoOf[j], gts[g][j]); n++; }
                        if (n >= 6) pairs.Add((sum / n, p, g));
                    }
                pairs.Sort((x, y) => x.c.CompareTo(y.c));
                var usedP = new bool[persons.Count]; var usedG = new bool[gts.Count];
                foreach (var pr in pairs)
                {
                    if (usedP[pr.p] || usedG[pr.g] || pr.c > 1.0) continue;
                    usedP[pr.p] = usedG[pr.g] = true; st.matched++;
                    for (int j = 0; j < 12; j++)
                        if (persons[pr.p].valid[CocoOf[j]]) st.err[j].Add(Dist(persons[pr.p], CocoOf[j], gts[pr.g][j]) * 1000.0);
                    if (persons[pr.p].reprojectionPx >= 0) st.reproj.Add(persons[pr.p].reprojectionPx);
                }
                for (int p = 0; p < persons.Count; p++) if (!usedP[p]) st.ghosts++;
            }
        }

        Console.WriteLine($"\nrecorded session, {frames.Count} frames, 2 people each; ViTPose frames sent at x{scale:F2}; options conf {opts.minConf} gate {opts.groupGateMetres} m inlier {opts.inlierMetres} m");
        Console.WriteLine($"{"views",-8} {"people found",13} {"unmatched",10} {"MPJPE mm",9} {"median",7} {"reproj px",10} {"ms/frame",9}");
        foreach (var kv in results)
        {
            Stats s = kv.Value;
            var all = s.err.SelectMany(x => x).OrderBy(x => x).ToList();
            Console.WriteLine($"{kv.Key,-8} {s.matched,6}/{s.gt,-6} {s.ghosts,10} {all.Average(),9:F1} {all[all.Count / 2],7:F1} {(s.reproj.Count > 0 ? s.reproj.Average() : 0),10:F1} {s.ms / s.frames,9:F2}");
        }

        // per joint for all views and the mean over the 3-view subsets
        Console.WriteLine($"\n{"joint",-15} {"4 views",8} {"3 views",8}");
        var four = results[string.Join("+", Enumerable.Range(1, nCam))];
        var threes = results.Where(kv => kv.Value.nViews == nCam - 1).Select(kv => kv.Value).ToList();
        for (int j = 0; j < 12; j++)
            Console.WriteLine($"{Shared[j],-15} {four.err[j].Average(),8:F0} {threes.Average(t => t.err[j].Average()),8:F0}");
        return 0;
    }

    static double Dist(MvPerson3D p, int k, double[] g)
    {
        double dx = p.x[k] - g[0], dy = p.y[k] - g[1], dz = p.z[k] - g[2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
