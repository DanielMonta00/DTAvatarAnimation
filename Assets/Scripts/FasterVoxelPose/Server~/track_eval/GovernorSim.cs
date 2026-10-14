using System;
using System.Collections.Generic;
using System.Linq;

// The frame-rate governor against a simulated GPU: server time as a function of Unity's cap, with the cliff that was measured
// (15 fps: 56 ms, 20 fps: 90, 30 fps: 486 under a heavy scene), plus noise, and a scene load that changes during the run.
static class G
{
    static int fails;
    static void Check(string name, bool ok, string detail) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: {detail}"); if (!ok) fails++; }
    static readonly Random rnd = new Random(5);

    // heavy scene: measured points, linear in between
    static float Heavy(int rate)
    {
        var pts = new (int r, float ms)[] { (10, 50), (15, 56), (20, 90), (24, 200), (30, 486), (60, 650) };
        if (rate <= pts[0].r) return pts[0].ms;
        for (int i = 1; i < pts.Length; i++)
            if (rate <= pts[i].r) { float k = (rate - pts[i - 1].r) / (float)(pts[i].r - pts[i - 1].r); return pts[i - 1].ms + k * (pts[i].ms - pts[i - 1].ms); }
        return pts[pts.Length - 1].ms;
    }
    static float Light(int rate) => 40f + rate * 0.3f;

    static (List<float> ms, List<int> rates, int changes) Run(Func<double, int, float> plant, double seconds, int start = 30)
    {
        var g = new FvpFrameRateGovernor { maxRate = start, minRate = 12 };
        g.Reset(start);
        var ms = new List<float>(); var rates = new List<int>();
        int rate = start;
        for (double t = 0; t < seconds; t += 0.12)       // an answer every 120 ms at best
        {
            float s = plant(t, rate) * (0.9f + 0.2f * (float)rnd.NextDouble());
            // the server cannot answer faster than it computes: fewer answers when it is slow
            rate = g.Observe(s, t);
            ms.Add(s); rates.Add(rate);
            t += Math.Max(0.0, s / 1000.0 - 0.12);
        }
        return (ms, rates, g.Changes);
    }

    static void Main()
    {
        Console.WriteLine("Frame-rate governor\n");

        Console.WriteLine("A. a light scene: nothing to do");
        var a = Run((t, r) => Light(r), 300);
        Check("stays at 30 fps, no change at all", a.rates.All(r => r == 30) && a.changes == 0, $"changes {a.changes}, min {a.rates.Min()}");

        Console.WriteLine("\nB. a heavy scene from the start (30 fps would collapse the server)");
        var b = Run((t, r) => Heavy(r), 300);
        var tail = b.rates.Skip(b.rates.Count / 2).ToList();
        var tailMs = b.ms.Skip(b.ms.Count / 2).ToList();
        Console.WriteLine($"     rate over the second half: min {tail.Min()} max {tail.Max()}; server ms mean {tailMs.Average():F0}; changes {b.changes}");
        Check("settles on a cap where the server is under 150 ms nearly always", tailMs.Count(x => x > 150) < tailMs.Count * 0.05, $"{100.0 * tailMs.Count(x => x > 150) / tailMs.Count:F1} % over 150 ms");
        Check("the cap ends up in the measured good range (12..21)", tail.Count(r => r > 21) < tail.Count * 0.05, $"{tail.Min()}..{tail.Max()}");
        Check("it does not hunt: at most 7 changes in 5 minutes", b.changes <= 7, $"{b.changes}");

        Console.WriteLine("\nC. the load goes away after two minutes");
        var c = Run((t, r) => t < 120 ? Heavy(r) : Light(r), 400);
        int at = c.rates.Count;
        var last = c.rates.Skip(c.rates.Count * 9 / 10).ToList();
        Console.WriteLine($"     rate in the last tenth: {last.Min()}..{last.Max()}, changes {c.changes}");
        Check("it climbs back to 30", last.All(r => r == 30), $"{last.Min()}..{last.Max()}");

        Console.WriteLine("\nD. a short load spike (20 s) in a light scene");
        var d = Run((t, r) => (t > 60 && t < 80) ? Heavy(r) : Light(r), 300);
        var dEnd = d.rates.Skip(d.rates.Count * 8 / 10).ToList();
        Check("it is back at 30 by the end", dEnd.All(r => r == 30), $"{dEnd.Min()}..{dEnd.Max()} (changes {d.changes})");

        Console.WriteLine($"\n{(fails == 0 ? "ALL PASSED" : fails + " FAILED")}");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
