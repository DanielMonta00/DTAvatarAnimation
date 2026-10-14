using System;

// A fixed-range histogram: the mean, the largest value and percentiles of a whole session without keeping the samples (a few
// hundred bins, one increment per value). Percentiles are as fine as one bin; values past the range count in the last bin and still
// show in Max.
public sealed class FvpHistogram
{
    readonly float min, binWidth;
    readonly int[] bins;
    long count;
    double sum;
    float peak, lastValue;

    public FvpHistogram(float min, float max, int binCount)
    {
        this.min = min;
        bins = new int[Math.Max(2, binCount)];
        binWidth = (max - min) / bins.Length;
    }

    public long Count => count;
    public float Mean => count > 0 ? (float)(sum / count) : 0f;
    public float Max => count > 0 ? peak : 0f;
    public float Last => lastValue;

    public void Add(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return;
        int b = (int)((v - min) / binWidth);
        bins[b < 0 ? 0 : b >= bins.Length ? bins.Length - 1 : b]++;
        if (count == 0 || v > peak) peak = v;
        sum += v;
        count++;
        lastValue = v;
    }

    // p in 0..1. The upper edge of the bin that holds that fraction of the values (the exact maximum when it falls in the last bin).
    public float Percentile(float p)
    {
        if (count == 0) return 0f;
        long target = (long)Math.Ceiling(Math.Max(0f, Math.Min(1f, p)) * count);
        if (target < 1) target = 1;
        long seen = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            seen += bins[i];
            if (seen >= target) return i == bins.Length - 1 ? peak : Math.Min(peak, min + (i + 1) * binWidth);
        }
        return peak;
    }

    public void Clear()
    {
        Array.Clear(bins, 0, bins.Length);
        count = 0; sum = 0; peak = 0; lastValue = 0;
    }
}
