using System;

// Keeps the GPU shared fairly between Unity and the inference server by choosing Unity's frame-rate cap from what the server reports.
//
// Why it is needed: Unity and the server share one GPU. While Unity's frames take a modest share of it the server answers in 35-90 ms; past
// a point it does not degrade, it collapses (measured with a synthetic scene load: 56 ms at 15 fps, 90 ms at 20 fps, 486 ms at 30 fps,
// and the capsule follower then loses the operator). The point depends on how heavy the scene is, so a fixed cap is a guess: this lowers
// the cap when the server's own time (its t_total, which the GPU load shows in and Unity's frame time does not) goes over `targetServerMs`
// and tries a slightly higher one again later. A cap that failed is not tried again for a while, and every failed try doubles the wait,
// so a scene that really is too heavy is not tested to destruction every minute.
public sealed class FvpFrameRateGovernor
{
    public int maxRate = 30;                // the cap when all is well (the component's Live Frame Rate)
    public int minRate = 12;                // never lower than this
    public float targetServerMs = 150f;     // over this the server is in trouble (35-60 ms on a free GPU)
    public float lowerCooldownSeconds = 2.5f;      // between two decreases: the estimates must have caught up with the last one
    public float raiseAfterSeconds = 12f;          // of comfortable answers before a higher cap is tried
    public float ceilingHoldSeconds = 60f;         // a cap that failed is not tried again for this long (doubled for each failed try in a row, at most 16 x)
    const float ProbeVerdictSeconds = 30f;         // a higher cap that held this long was a success

    float ema;
    bool haveEma;
    double lastChange = double.NegativeInfinity;
    double lastOverload = double.NegativeInfinity;
    int ceiling = int.MaxValue;
    double ceilingUntil = double.NegativeInfinity;
    bool probing;
    double probeStart;
    int failedProbes;

    public int Rate { get; private set; }
    public int Changes { get; private set; }
    public float ServerMs => ema;

    public void Reset(int rate)
    {
        Rate = rate; ceiling = int.MaxValue; ceilingUntil = double.NegativeInfinity; haveEma = false; ema = 0f;
        lastChange = double.NegativeInfinity; lastOverload = double.NegativeInfinity; Changes = 0; probing = false; failedProbes = 0;
    }

    // One call per answer of the server (its own total time, ms) at `now` seconds; returns the cap to hold.
    public int Observe(float serverMs, double now)
    {
        if (Rate <= 0) return Rate;
        ema = haveEma ? ema + 0.3f * (serverMs - ema) : serverMs;
        haveEma = true;
        int top = now < ceilingUntil ? Math.Min(maxRate, ceiling) : maxRate;
        if (Rate > top) { Rate = Math.Max(minRate, top); Changes++; lastChange = now; }

        if (ema > targetServerMs)
        {
            lastOverload = now;
            if (Rate > minRate && now - lastChange >= lowerCooldownSeconds)
            {
                // a try that failed soon after it began doubles the wait; a new overload at a cap that had held does not
                failedProbes = probing && now - probeStart < ProbeVerdictSeconds ? Math.Min(failedProbes + 1, 4) : 0;
                probing = false;
                ceiling = Rate - 1;                        // the cap that was too much
                ceilingUntil = now + ceilingHoldSeconds * (1 << failedProbes);
                int lower = Math.Max(minRate, (int)Math.Floor(Rate * 0.8));
                if (lower == Rate) lower = Math.Max(minRate, Rate - 1);
                Rate = lower; Changes++; lastChange = now;
                haveEma = false;                           // the old answers belong to the old cap
            }
        }
        else if (ema < 0.6f * targetServerMs && Rate < maxRate && now - lastChange >= raiseAfterSeconds && now - lastOverload >= raiseAfterSeconds)
        {
            int higher = Math.Min(maxRate, (int)Math.Ceiling(Rate * 1.1));
            if (higher == Rate) higher = Math.Min(maxRate, Rate + 1);
            if (higher <= top)
            {
                Rate = higher; Changes++; lastChange = now;
                probing = true; probeStart = now;
                haveEma = false;
            }
        }
        return Rate;
    }
}
