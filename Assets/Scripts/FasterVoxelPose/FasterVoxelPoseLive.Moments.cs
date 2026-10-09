using System;
using System.Collections.Generic;
using UnityEngine;

// The dense timeline.
//
// An estimate is made about once a second (it takes the network that long, GPU shared with Unity), so a history made of estimates
// alone can only be stepped through one estimate at a time: that is the "big interval between frames". To step by a single frame,
// or a tenth of a second, the scene has to be recorded between the estimates too. That is cheap compared with estimating: a
// moment is the state that puts the scene back (the Animators and everything else that moves, see FvpSceneState), taken at the end
// of a rendered frame about 30 times a second. Stepping back to a moment puts the scene there; the cameras then show that frame, the
// ground truth is read from the restored rig (exact), and an estimate for it is made on demand once you stop moving.
//
// Moments exist only for the last `momentWindowSeconds`; before that the history is estimates only, as before.
public partial class FasterVoxelPoseLive
{
    [Header("Timeline (smooth stepping)")]
    [Tooltip("Record the scene's state about 30 times a second while it runs, not only when an estimate is made. Stepping back by one frame (or 10 frames, or a second) then has a frame to land on, and an estimate for it is made on demand when you stop. Off: stepping goes from estimate to estimate (about one a second).")]
    public bool recordMoments = true;
    [Range(5f, 60f)]
    [Tooltip("How many moments are recorded per second of scene time (at most one per rendered frame). 30 matches the dataset's frame rate and the step size.")]
    public float momentsPerSecond = 30f;
    [Range(10f, 900f)]
    [Tooltip("How far back the dense timeline reaches, in seconds of scene time. A moment is a few kB (more with many moving objects), so 120 s at 30 a second is a few tens of MB.")]
    public float momentWindowSeconds = 120f;
    [Range(0.05f, 2f)]
    [Tooltip("After you stop on a frame that has no estimate, how long before one is made for it (stepping on quickly does not queue estimates).")]
    public float estimateAfterSeconds = 0.25f;

    // The timeline's clock. It is Time.time, except that when you resume from the past the scene goes on from that instant and the
    // clock goes on with it, so the timeline has no hole where the abandoned future was.
    double clockOffset;
    public double SceneNow => Time.timeAsDouble - clockOffset;

    readonly List<FvpMoment> moments = new List<FvpMoment>();
    FvpMoment lastMoment;
    float momentMsAverage;
    Prof profMoment;

    public int MomentCount => moments.Count;
    public FvpMoment MomentAt(int index) => index >= 0 && index < moments.Count ? moments[index] : null;
    public float MomentMs => momentMsAverage;

    // ---------------- the extent of the timeline, in scene time ----------------

    public bool HasTimeline => history.Count > 0 || moments.Count > 0;

    public double TimelineStart
    {
        get
        {
            double t = double.PositiveInfinity;
            if (history.Count > 0) t = history[0].sceneTime;
            if (moments.Count > 0) t = Math.Min(t, moments[0].sceneTime);
            return t;
        }
    }

    public double TimelineEnd
    {
        get
        {
            double t = double.NegativeInfinity;
            if (history.Count > 0) t = history[history.Count - 1].sceneTime;
            if (moments.Count > 0) t = Math.Max(t, moments[moments.Count - 1].sceneTime);
            return t;
        }
    }

    // ---------------- recording ----------------

    // End of a rendered frame, scene running: the state the cameras just drew.
    void RecordMomentIfDue()
    {
        if (!recordMoments || paused || !Application.isPlaying) return;
        if (Time.realtimeSinceStartup - enabledAt < 1f) return;     // the first pass builds the list of what to watch: let the scene finish being set up
        double t = SceneNow;
        if (lastMoment != null && t - lastMoment.sceneTime < 0.999 / Mathf.Max(1f, momentsPerSecond)) return;
        long start = Stamp();
        AddMoment(new FvpMoment { sceneTime = t, unityFrame = Time.frameCount, anims = SnapshotAnimators(), scene = SnapshotScene() });
        profMoment.ticks += Stamp() - start;
        profMoment.calls++;
    }

    // A captured frame took its own snapshot of the scene: that is a moment too (no second pass over the scene).
    void RecordMomentFromFrame(FvpFrame f)
    {
        if (!recordMoments || f == null || f.anims == null && f.scene == null) return;
        AddMoment(new FvpMoment { sceneTime = f.sceneTime, unityFrame = f.unityFrame, anims = f.anims, scene = f.scene });
    }

    void AddMoment(FvpMoment m)
    {
        int n = moments.Count;
        if (n == 0 || m.sceneTime > moments[n - 1].sceneTime + 1e-6) moments.Add(m);
        else
        {
            int at = MomentIndexAtOrBefore(m.sceneTime);
            if (at >= 0 && m.sceneTime - moments[at].sceneTime < 1e-6) return;   // already have that instant
            moments.Insert(at + 1, m);
        }
        lastMoment = moments[moments.Count - 1];

        double oldest = lastMoment.sceneTime - momentWindowSeconds;
        int drop = 0;
        while (drop < moments.Count - 1 && moments[drop].sceneTime < oldest) drop++;
        if (drop > 0) moments.RemoveRange(0, drop);
    }

    // Resuming from the past: the future of the timeline is no more.
    void TruncateMomentsAfter(double time)
    {
        int keep = MomentIndexAtOrBefore(time + 1e-6) + 1;
        if (keep < moments.Count) moments.RemoveRange(keep, moments.Count - keep);
        lastMoment = moments.Count > 0 ? moments[moments.Count - 1] : null;
    }

    // ---------------- lookups (scene time) ----------------

    // Index of the last moment at or before t; -1 when there is none.
    int MomentIndexAtOrBefore(double t)
    {
        int lo = 0, hi = moments.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (moments[mid].sceneTime <= t) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best;
    }

    int EstimateIndexAtOrBefore(double t)
    {
        int lo = 0, hi = history.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (history[mid].sceneTime <= t) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best;
    }

    int MomentIndexNearest(double t)
    {
        int a = MomentIndexAtOrBefore(t), b = a + 1;
        if (a < 0) return moments.Count > 0 ? 0 : -1;
        if (b >= moments.Count) return a;
        return t - moments[a].sceneTime <= moments[b].sceneTime - t ? a : b;
    }

    int EstimateIndexNearest(double t)
    {
        int a = EstimateIndexAtOrBefore(t), b = a + 1;
        if (a < 0) return history.Count > 0 ? 0 : -1;
        if (b >= history.Count) return a;
        return t - history[a].sceneTime <= history[b].sceneTime - t ? a : b;
    }

    // A place on the timeline: an estimate (estimate >= 0) or a moment that has none.
    struct TimelinePos
    {
        public double time;
        public int estimate;
        public FvpMoment moment;
        public bool Valid => estimate >= 0 || moment != null;
        public static TimelinePos Estimate(double t, int index) => new TimelinePos { time = t, estimate = index };
        public static TimelinePos Moment(FvpMoment m) => new TimelinePos { time = m.sceneTime, estimate = -1, moment = m };
        public static TimelinePos None => new TimelinePos { estimate = -1 };
    }

    const double SameInstant = 1e-5;

    // The recorded place nearest to t. An estimate wins a tie (it has more to show).
    TimelinePos NearestPosition(double t)
    {
        int ei = EstimateIndexNearest(t), mi = MomentIndexNearest(t);
        double de = ei >= 0 ? Math.Abs(history[ei].sceneTime - t) : double.PositiveInfinity;
        double dm = mi >= 0 ? Math.Abs(moments[mi].sceneTime - t) : double.PositiveInfinity;
        if (ei >= 0 && de <= dm + SameInstant) return TimelinePos.Estimate(history[ei].sceneTime, ei);
        if (mi >= 0) return TimelinePos.Moment(moments[mi]);
        return TimelinePos.None;
    }

    // The last recorded place at or before t (the playhead of a rewind / replay).
    TimelinePos PositionAtOrBefore(double t)
    {
        int ei = EstimateIndexAtOrBefore(t), mi = MomentIndexAtOrBefore(t);
        double te = ei >= 0 ? history[ei].sceneTime : double.NegativeInfinity;
        double tm = mi >= 0 ? moments[mi].sceneTime : double.NegativeInfinity;
        if (ei >= 0 && te >= tm - SameInstant) return TimelinePos.Estimate(te, ei);
        if (mi >= 0) return TimelinePos.Moment(moments[mi]);
        return TimelinePos.None;
    }

    // The nearest recorded place strictly beyond `from` in direction dir (-1 / +1): one step along the timeline, whatever the spacing.
    TimelinePos NeighbourPosition(double from, int dir)
    {
        int ei, mi;
        if (dir < 0)
        {
            ei = EstimateIndexAtOrBefore(from - SameInstant); mi = MomentIndexAtOrBefore(from - SameInstant);
        }
        else
        {
            ei = EstimateIndexAtOrBefore(from + SameInstant) + 1; if (ei >= history.Count) ei = -1;
            mi = MomentIndexAtOrBefore(from + SameInstant) + 1; if (mi >= moments.Count) mi = -1;
        }
        double de = ei >= 0 ? Math.Abs(history[ei].sceneTime - from) : double.PositiveInfinity;
        double dm = mi >= 0 ? Math.Abs(moments[mi].sceneTime - from) : double.PositiveInfinity;
        if (ei >= 0 && de <= dm + SameInstant) return TimelinePos.Estimate(history[ei].sceneTime, ei);
        if (mi >= 0) return TimelinePos.Moment(moments[mi]);
        return TimelinePos.None;
    }
}
