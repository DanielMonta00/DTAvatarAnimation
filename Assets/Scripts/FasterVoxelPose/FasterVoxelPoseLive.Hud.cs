using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

// The corner widget (FvpHud, one per display) and the numbers it shows: estimate rate, latency, Unity's own frame rate, what this
// component costs per frame, and for each person found the network's confidence and its error against the ground truth.
//
// Why the estimate trails the picture in real time. An estimate is for the frame that was captured; it can only be drawn on a frame
// after it. With Unity running at U ms a frame the earliest is two frames later (captured at the end of frame n, answered during
// frame n + 1, consumed by the Update of frame n + 2), so the lag is at least 2 U whatever the network does, and the estimate
// rate is bounded by the frame rate too. A heavy scene (many cameras, many renderers) makes U large; the widget says so.
public partial class FasterVoxelPoseLive
{
    [Header("Widget")]
    [Tooltip("The status card in the corner of every display: state, rate, latency, Unity's frame rate, and the people found with their confidence and error against the ground truth. H toggles it.")]
    public bool showHud = true;
    [Tooltip("Size of the status card and the transport bar. Text is only sharp when the Game view shows one render pixel per screen pixel (Scale 1x, Low Resolution Aspect Ratios off); on a display with Windows scaling at 125 % use 1.25 there rather than magnifying the Game view.")]
    [Range(0.75f, 3f)] public float uiScale = 1f;

    // ---------------- cost of this component, measured ----------------

    struct Prof { public long ticks; public int calls; }
    Prof profFrame, profCapture;                       // everything per frame / one capture (including the wait for the GPU)
    float smoothDt, windowMaxDt;
    int windowFrames;
    float fvpMsPerFrame, captureMsAverage, worstFrameMs;
    float windowStart;
    float nextHudTime;
    int hudHash;

    public FvpHudData Hud => hudData;
    // What this component costs the main thread per rendered frame, per capture (the wait for the GPU included), and Unity's own frame time.
    public float ComponentMsPerFrame => fvpMsPerFrame;
    public float CaptureMs => captureMsAverage;
    public float UnityFrameMs => smoothDt * 1000f;

    static long Stamp() => Stopwatch.GetTimestamp();
    static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    // Called first thing in Update.
    void NoteFrame()
    {
        float dt = Time.unscaledDeltaTime;
        smoothDt = smoothDt <= 0f ? dt : Mathf.Lerp(smoothDt, dt, 0.05f);
        if (dt > windowMaxDt) windowMaxDt = dt;
        windowFrames++;

        float now = Time.unscaledTime;
        if (windowStart <= 0f) windowStart = now;
        if (now - windowStart >= 1f)
        {
            fvpMsPerFrame = windowFrames > 0 ? (float)TicksToMs(profFrame.ticks) / windowFrames : 0f;
            captureMsAverage = profCapture.calls > 0 ? (float)TicksToMs(profCapture.ticks) / profCapture.calls : captureMsAverage;
            momentMsAverage = profMoment.calls > 0 ? (float)TicksToMs(profMoment.ticks) / profMoment.calls : momentMsAverage;
            worstFrameMs = windowMaxDt * 1000f;
            profFrame = default; profCapture = default; profMoment = default;
            windowFrames = 0; windowMaxDt = 0f; windowStart = now;
        }
    }

    // ---------------- ground truth comparison ----------------

    // For each estimated person: the mean distance of its joints to the closest ground-truth person (greedy, nearest first),
    // taken at the same instant (the ground truth was read when the frame was captured). No partner within a metre while there
    // IS ground truth means a ghost.
    static readonly List<(float cost, int e, int g)> matchPairs = new List<(float, int, int)>();
    static readonly List<bool> usedE = new List<bool>(), usedG = new List<bool>();

    internal static void CompareWithGroundTruth(List<FvpPerson> estimates, List<FvpPerson> truth)
    {
        for (int e = 0; e < estimates.Count; e++) { estimates[e].errMm = -1f; estimates[e].ghost = false; }
        if (truth == null || truth.Count == 0) return;

        matchPairs.Clear();
        for (int e = 0; e < estimates.Count; e++)
            for (int g = 0; g < truth.Count; g++)
            {
                float sum = 0f; int n = 0;
                for (int j = 0; j < FvpSkeleton.Count; j++)
                {
                    if (!truth[g].IsValid(j) || !estimates[e].IsValid(j)) continue;
                    sum += Vector3.Distance(estimates[e].joints[j], truth[g].joints[j]); n++;
                }
                if (n >= 6) matchPairs.Add((sum / n, e, g));
            }
        matchPairs.Sort((a, b) => a.cost.CompareTo(b.cost));

        usedE.Clear(); usedG.Clear();
        for (int e = 0; e < estimates.Count; e++) usedE.Add(false);
        for (int g = 0; g < truth.Count; g++) usedG.Add(false);
        foreach (var (cost, e, g) in matchPairs)
        {
            if (usedE[e] || usedG[g] || cost > 1.0f) continue;
            usedE[e] = usedG[g] = true;
            estimates[e].errMm = cost * 1000f;
        }
        for (int e = 0; e < estimates.Count; e++) estimates[e].ghost = !usedE[e];
    }

    // ---------------- widget content ----------------

    readonly FvpHudData hudData = new FvpHudData();
    readonly StringBuilder hudLabels = new StringBuilder(), hudValues = new StringBuilder(), hudWarn = new StringBuilder();
    string[] displayTitles = new string[8];

    // The camera(s) whose image a display shows.
    string TitleOf(int display) => display >= 0 && display < displayTitles.Length ? displayTitles[display] : "";

    void RefreshDisplayTitles()
    {
        for (int d = 0; d < displayTitles.Length; d++) displayTitles[d] = "";
        if (cameras == null) return;
        foreach (Camera c in cameras)
        {
            if (c == null) continue;
            int d = Mathf.Clamp(c.targetDisplay, 0, displayTitles.Length - 1);
            string n = c.name.Trim();
            displayTitles[d] = displayTitles[d].Length == 0 ? n : displayTitles[d] + ", " + n;
        }
    }

    // Where the transport is on the timeline, in words.
    void AppendTimelineRow(StringBuilder sb, FvpFrame f)
    {
        if (!HasTimeline) { sb.Append('-'); return; }
        if (IsLive) sb.Append("newest");
        else sb.Append('-').Append(SecondsBehind.ToString("F2")).Append(" s");
        sb.Append("   ·   t = ").Append(viewTime.ToString("F2")).Append(" s   ·   ");
        if (f != null && f.placeholder)
        {
            int before = EstimateBefore;
            if (before < 0) sb.Append("before the first estimate");
            else if (before + 1 >= history.Count) sb.Append("after estimate ").Append(before + 1);
            else sb.Append("between estimates ").Append(before + 1).Append(" and ").Append(before + 2);
        }
        else sb.Append("estimate ").Append(cursor + 1).Append(" of ").Append(history.Count);
        sb.Append("   ·   ").Append(moments.Count).Append(" frames recorded");
    }

    // A few times a second; the widget only re-lays itself out when the content really changed.
    void RefreshHud()
    {
        hudData.visible = showHud && PanelsVisible;
        if (!hudData.visible) { hudData.stamp++; return; }

        float now = Time.unscaledTime;
        if (now < nextHudTime) return;
        nextHudTime = now + 0.25f;

        FvpFrame f = IsReady ? displayed : null;
        RefreshDisplayTitles();

        // state + legend
        string state;
        Color stateColor;
        if (!IsReady) { state = "WAITING"; stateColor = new Color(0.7f, 0.75f, 0.8f); }
        else if (paused)
        {
            state = autoDir < 0 ? "REWIND" : autoDir > 0 ? "REPLAY"
                  : ShowsMoment ? (Estimating || estimateDue > 0f ? "PAUSED · estimating this frame" : "PAUSED · no estimate for this frame")
                  : Estimating ? "PAUSED · estimating" : "PAUSED";
            stateColor = new Color(1f, 0.82f, 0.45f);
        }
        else if (!followHead) { state = "BEHIND"; stateColor = new Color(1f, 0.82f, 0.45f); }
        else { state = "LIVE"; stateColor = new Color(0.55f, 0.95f, 0.6f); }
        hudData.state = state;
        hudData.stateColor = stateColor;

        string mode = overlayImage == OverlayImage.SyncedFrame ? "synced frame" : latencyCompensation ? "real-time, estimate advanced" : "real-time";
        hudData.legend = (showGroundTruth && GroundTruthVisible ? "<color=#7dff7d>■</color> ground truth    " : "") +
                         (showEstimate && OverlayVisible ? "<color=#ff9a2e>■</color> estimate    " : "") +
                         "<color=#9aa7b5>" + mode + "</color>";

        hudLabels.Length = 0; hudValues.Length = 0; hudWarn.Length = 0;
        hudData.people.Clear();
        hudData.minScore = minScore;

        if (f == null)
        {
            hudLabels.Append("status");
            hudValues.Append(string.IsNullOrEmpty(status) ? "starting" : status);
            hudData.footer = "";
        }
        else
        {
            float fps = smoothDt > 1e-4f ? 1f / smoothDt : 0f;
            hudLabels.Append("estimates\nlatency\nserver\nUnity\nthis tool\ntimeline\nrender");
            hudValues.Append(estimateFps.ToString("F1")).Append(" /s\n");
            hudValues.Append(f.latencyMs.ToString("F0")).Append(" ms from capture to answer\n");
            hudValues.Append(f.totalMs.ToString("F0")).Append(" ms");
            if (f.backboneMs > 0f)
                hudValues.Append("  =  backbone ").Append(f.backboneMs.ToString("F0")).Append(" + root ").Append(f.rootMs.ToString("F0")).Append(" + joints ").Append(f.jointMs.ToString("F0"))
                         .Append("  (").Append(f.proposals).Append(f.proposals == 1 ? " person" : " people").Append(" localised)");
            hudValues.Append('\n');
            hudValues.Append(fps.ToString("F0")).Append(" fps  (").Append((smoothDt * 1000f).ToString("F0")).Append(" ms, worst ").Append(worstFrameMs.ToString("F0")).Append(")");
            if (appliedRate > 0) hudValues.Append("  ·  held to ").Append(appliedRate).Append(" fps to leave the GPU to the models");
            hudValues.Append('\n');
            hudValues.Append(fvpMsPerFrame.ToString("F2")).Append(" ms per frame   ·   ").Append(captureMsAverage.ToString("F0")).Append(" ms per capture");
            if (momentMsAverage > 0f) hudValues.Append("   ·   ").Append(momentMsAverage.ToString("F2")).Append(" ms per recorded frame");
            hudValues.Append('\n');
            AppendTimelineRow(hudValues, f);
            hudValues.Append('\n');
            // The size Unity renders this display at: if it is smaller than the window shows it in, the Game view is magnifying it.
            hudValues.Append(Screen.width).Append(" × ").Append(Screen.height).Append(" px");

            List<FvpPerson> people = f.people;
            for (int i = 0; i < people.Count; i++)
            {
                FvpPerson p = people[i];
                hudData.people.Add(new FvpHudData.PersonRow
                {
                    id = p.id, score = p.score, errMm = p.errMm, ghost = p.ghost, color = new Color(1f, 0.6f, 0.18f),
                    detail = p.phantom ? $"{p.score:F2}   <color=#ff7a6b>phantom? 2D support {p.meanSupport:F2}</color>" : null,
                });
            }
            hudData.emptyText = f.placeholder ? "no estimate for this frame yet (the faint skeleton is the nearest one)" : "nobody found";
            AppendPhantomLine(hudWarn, f);

            if (smoothDt > 0.045f)
                hudWarn.Append(hudWarn.Length > 0 ? "\n" : "").Append("Unity runs at ").Append(fps.ToString("F0")).Append(" fps: an estimate shows up at least\n2 frames (")
                       .Append((2f * smoothDt * 1000f).ToString("F0")).Append(" ms) after it was captured.");
            int rendering = Camera.allCamerasCount;
            if (rendering > cameras.Count + 3)
            {
                if (hudWarn.Length > 0) hudWarn.Append('\n');
                hudWarn.Append(rendering).Append(" cameras render every frame; FVP reads ").Append(cameras.Count).Append('.');
            }
            hudData.footer = "score: how sharp the joint heatmaps are, 0-1 (bar = 0.5, tick = Min Score).\nerr: mean joint distance to the green skeleton at the same instant.";
        }
        hudData.statLabels = hudLabels.ToString();
        hudData.statValues = hudValues.ToString();
        hudData.warn = hudWarn.ToString();

        // only bump the stamp when something shown actually differs
        int h = state.GetHashCode() ^ hudData.legend.GetHashCode() ^ hudData.statLabels.GetHashCode() ^ (hudData.statValues.GetHashCode() * 31) ^
                (hudData.warn.GetHashCode() * 17) ^ hudData.people.Count ^ (int)(minScore * 1000f);
        for (int i = 0; i < hudData.people.Count; i++)
            h = h * 397 ^ hudData.people[i].id ^ (int)(hudData.people[i].score * 1000f) * 13 ^ (int)(hudData.people[i].errMm) * 7 ^ (hudData.people[i].ghost ? 1 : 0);
        if (h != hudHash) { hudHash = h; hudData.stamp++; }
    }
}
