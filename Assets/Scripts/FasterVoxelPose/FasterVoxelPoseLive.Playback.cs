using System;
using System.Collections.Generic;
using UnityEngine;

// Transport controls: pause, step, jump, rewind.
//
// The timeline has two kinds of places (see FasterVoxelPoseLive.Moments.cs):
//   estimate   a frame the network was run on: the camera images, the skeletons it found, the state of the scene at that instant.
//              About one a second.
//   moment     an instant of the scene recorded between the estimates (30 a second): enough to put the scene back there, no skeletons.
// Any control lands on one or the other. On a moment the scene is exactly that instant (the cameras show it, the ground truth is
// read from the restored rig) and an estimate is made for it on demand once you stop moving; until it arrives the nearest estimate
// is drawn dim, because it is not for this frame.
//
//   Pause        freezes the scene (Time.timeScale = 0) and estimates the frozen frame once, so what is on show is
//                exactly the paused scene.
//   -1f / +1f    one frame (Step Seconds, 1/30 s) back / forward. At the newest frame forward lets the scene advance by that much and
//                estimates it.  -10f / +10f ten frames,  -1s / +1s one second.  <E / E> the previous / next estimate.
//   Rewind <<    keeps going back at the recorded speed (rewindSpeed x) through every recorded instant; Replay >> forwards.
//   Play         resumes. After going back the scene continues from the instant on show (what came after it is dropped), provided
//                Animators are tracked; otherwise it continues from the newest frame.
//
// The scene only rewinds as far as its Animators do. Physics, particle systems and scripts keep their present
// state while the images and skeletons come from the past.
public partial class FasterVoxelPoseLive
{
    enum StepState { Idle, Armed, Rendering }

    bool paused;
    int autoDir;                  // -1 rewinding, +1 replaying history forwards, 0 neither
    double playhead;              // scene time the auto replay has reached
    readonly List<float> stepQueue = new List<float>();   // scene steps asked for while the newest frame is on show (seconds each)
    StepState stepState;
    int stepArmedFrame;
    float stepRenderingSince;
    double stepStartTime;
    float stepLength;             // seconds the step in progress advances the scene by
    bool stepLengthWarned;
    bool wantOneShot;             // estimate the (frozen) scene once even though paused
    float userTimeScale = 1f;
    bool timeScaleHeld;
    float pausedAtReal;
    Action deferred;              // navigation asked for while the pause was still settling

    // Where the transport is, in scene time. For an estimate it is the time of its capture; for a moment, the moment's.
    double viewTime;
    FvpFrame staleFrom;           // while a moment without an estimate is on show: the nearest estimate, drawn dim
    float estimateDue;            // unscaled time at which an estimate is made for the moment on show (0 = none due)
    const double MaxStaleSeconds = 1.5;

    public bool SceneRewindable
    {
        get
        {
            if (trackedAnimators == null) return false;
            foreach (Animator a in trackedAnimators) if (a != null) return true;
            return false;
        }
    }

    // After Pause the frozen frame is still being estimated; navigating before it lands would restore the
    // animators and then capture the wrong state.
    public bool Settling => paused && Time.realtimeSinceStartup - pausedAtReal < 2f && (wantOneShot || building != null || inFlight != null);

    public bool CanStepBack => HasTimeline && viewTime > TimelineStart + SameInstant;
    public bool AtNewest => !HasTimeline || (!paused && followHead) || viewTime >= TimelineEnd - SameInstant;
    public int Rewinding => autoDir;
    public double ViewTime => viewTime;
    // The frame on show is a moment of the timeline that has no estimate (yet).
    public bool ShowsMoment => displayed != null && displayed.placeholder;
    // The estimate drawn dim next to a moment that has none.
    public FvpFrame StaleFrame => ShowsMoment ? staleFrom : null;
    // How far behind the newest recorded instant the transport is, in seconds (0 live).
    public double SecondsBehind => HasTimeline && !(IsLive) ? Math.Max(0.0, TimelineEnd - viewTime) : 0.0;
    // The estimate that is, or comes just before, the place on show (index into the history).
    public int EstimateBefore => EstimateIndexAtOrBefore(viewTime + SameInstant);

    // ---------------- commands ----------------

    public void TogglePause() { if (paused) Resume(); else Pause(); }

    public void Pause()
    {
        if (paused) return;
        EnsureSceneState(); // the video players have to be known before they can be paused
        paused = true;
        autoDir = 0;
        pausedAtReal = Time.realtimeSinceStartup;
        if (freezeSceneWhenPaused)
        {
            if (!timeScaleHeld) { userTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f; timeScaleHeld = true; }
            Time.timeScale = 0f;
            sceneState.PauseMedia(); // video does not follow timeScale
        }
        wantOneShot = IsReady;
    }

    public void Resume()
    {
        if (!paused) return;
        autoDir = 0; stepQueue.Clear(); deferred = null; estimateDue = 0f;
        if (stepState != StepState.Idle) { stepState = StepState.Idle; Time.captureDeltaTime = 0f; }

        bool behind = HasTimeline && viewTime < TimelineEnd - SameInstant;
        if (behind)
        {
            // Whatever is still being estimated belongs to the timeline we are leaving.
            DropInFlight();
            if (building != null) { FvpFrame b = building; building = null; b.evicted = true; }
            if (SceneRewindable)
            {
                int keep = EstimateIndexAtOrBefore(viewTime + SameInstant);
                if (displayed != null && displayed.placeholder) RestoreState(displayed.anims, displayed.scene);
                else if (keep >= 0) RestoreScene(history[keep]);
                if (keep >= 0) { cursor = keep; TruncateAfter(keep); }
                else { TruncateAfter(-1); cursor = 0; }
                TruncateMomentsAfter(viewTime);
                clockOffset = Time.timeAsDouble - viewTime;      // the scene goes on from there, and so does the timeline's clock
                tracker.Reset(keep >= 0 ? history[keep].people : null, keep >= 0 ? history[keep].sceneTime : viewTime);
                if (displayed != null && displayed.placeholder) Show(keep >= 0 ? history[keep] : null);
            }
            else
            {
                cursor = Mathf.Max(0, history.Count - 1);
                Show(history.Count > 0 ? history[cursor] : null);
            }
        }
        followHead = true;
        staleFrom = null;
        paused = false;
        wantOneShot = false;
        if (timeScaleHeld) { Time.timeScale = userTimeScale; timeScaleHeld = false; }
        sceneState.ResumeMedia();
    }

    // One frame (Step Seconds) forward / back.
    public void StepForward() => Jump(stepSeconds);
    public void StepBackward() => Jump(-stepSeconds);

    // Forward / back by an amount of scene time; lands on the nearest recorded place, or, at the newest frame, lets the scene run on by that much.
    public void Jump(double seconds)
    {
        if (seconds == 0.0) return;
        if (!paused) { Pause(); if (seconds > 0.0) return; }   // the first forward press stops the scene on its current frame
        RunWhenSettled(() =>
        {
            autoDir = 0;
            if (!HasTimeline) return;
            if (seconds > 0.0 && AtNewest)
            {
                // The scene runs on from the newest frame in steps of at most Time.maximumDeltaTime (a frame longer than that is cut
                // short by the engine), each one estimated.
                double chunk = Math.Max(stepSeconds, Math.Min(Time.maximumDeltaTime, MaxSceneStep)), left = Math.Min(seconds, MaxSceneStep);
                while (left > 1e-6 && stepQueue.Count < 8) { double part = Math.Min(left, chunk); stepQueue.Add((float)part); left -= part; }
                return;
            }
            double target = Math.Max(TimelineStart, Math.Min(viewTime + seconds, TimelineEnd));
            int dir = seconds < 0.0 ? -1 : 1;
            TimelinePos p = Math.Abs(seconds) <= 1.01 * stepSeconds ? NeighbourPosition(viewTime, dir) : NearestPosition(target);
            if (!p.Valid || Math.Abs(p.time - viewTime) < SameInstant && displayed != null) p = NeighbourPosition(viewTime, dir);
            if (p.Valid) ShowPosition(p);
        });
    }

    const double MaxSceneStep = 1.0;   // the longest single step the scene is made to take when going forward from the newest frame

    // The previous / next estimate, whatever lies between.
    public void PreviousEstimate()
    {
        if (!paused) Pause();
        RunWhenSettled(() =>
        {
            autoDir = 0;
            int at = EstimateIndexAtOrBefore(viewTime - SameInstant);
            if (at >= 0) GoTo(at);
        });
    }

    public void NextEstimate()
    {
        if (!paused) { Pause(); return; }
        RunWhenSettled(() =>
        {
            autoDir = 0;
            int at = EstimateIndexAtOrBefore(viewTime + SameInstant) + 1;
            if (at < history.Count) GoTo(at);
            else if (AtNewest && stepQueue.Count < 8) stepQueue.Add(stepSeconds);
        });
    }

    public void ToggleRewind()
    {
        if (autoDir < 0) { autoDir = 0; return; }
        if (!paused) Pause();
        RunWhenSettled(() =>
        {
            if (!CanStepBack) return;
            autoDir = -1;
            playhead = viewTime;
        });
    }

    public void ToggleReplay()
    {
        if (autoDir > 0) { autoDir = 0; return; }
        if (!paused) return;
        RunWhenSettled(() =>
        {
            if (AtNewest) return;
            autoDir = 1;
            playhead = viewTime;
        });
    }

    public void GoToStart()
    {
        if (!paused) Pause();
        RunWhenSettled(() => { autoDir = 0; if (HasTimeline) ShowPosition(NearestPosition(TimelineStart)); });
    }

    public void GoToNewest()
    {
        if (!paused) return;
        RunWhenSettled(() => { autoDir = 0; if (HasTimeline) ShowPosition(NearestPosition(TimelineEnd)); });
    }

    // The history index is an estimate's number (the inspector's slider, tests).
    public void Scrub(int index)
    {
        if (!paused) Pause();
        RunWhenSettled(() => { autoDir = 0; if (history.Count > 0 && (index != cursor || ShowsMoment)) GoTo(Mathf.Clamp(index, 0, history.Count - 1)); });
    }

    // Anywhere on the timeline: the slider, a drag on the picture.
    public void ScrubToTime(double time)
    {
        if (!paused) Pause();
        RunWhenSettled(() =>
        {
            autoDir = 0;
            if (!HasTimeline) return;
            TimelinePos p = NearestPosition(Math.Max(TimelineStart, Math.Min(time, TimelineEnd)));
            if (p.Valid && (Math.Abs(p.time - viewTime) >= SameInstant || displayed == null)) ShowPosition(p);
        });
    }

    // 0 = the oldest recorded instant, 1 = the newest.
    public float TimelineFraction
    {
        get
        {
            if (!HasTimeline) return 0f;
            double a = TimelineStart, b = TimelineEnd;
            return b - a < 1e-9 ? 1f : (float)Math.Max(0.0, Math.Min(1.0, ((IsLive ? b : viewTime) - a) / (b - a)));
        }
    }

    public void ScrubToFraction(float fraction)
    {
        if (!HasTimeline) return;
        ScrubToTime(TimelineStart + Mathf.Clamp01(fraction) * (TimelineEnd - TimelineStart));
    }

    void RunWhenSettled(Action a)
    {
        if (Settling) deferred = a; else a();
    }

    // ---------------- internals ----------------

    void ShowPosition(TimelinePos p)
    {
        if (p.estimate >= 0) GoTo(p.estimate);
        else if (p.moment != null) GoToMoment(p.moment);
    }

    void GoTo(int index)
    {
        if (history.Count == 0) return;
        cursor = Mathf.Clamp(index, 0, history.Count - 1);
        followHead = cursor == history.Count - 1 && history[cursor].sceneTime >= TimelineEnd - SameInstant;
        estimateDue = 0f;
        Show(history[cursor]);
        if (paused) RestoreScene(history[cursor]);
    }

    // A recorded instant without an estimate: the scene goes back there; an estimate for it follows when the user stops.
    void GoToMoment(FvpMoment m)
    {
        cursor = Mathf.Max(0, EstimateIndexAtOrBefore(m.sceneTime + SameInstant));
        followHead = false;
        RestoreState(m.anims, m.scene);

        var f = new FvpFrame
        {
            id = 0, sceneTime = m.sceneTime, unityFrame = m.unityFrame, realtime = Time.realtimeSinceStartup, width = frameW, height = frameH,
            cameras = sentModels, anims = m.anims, scene = m.scene, placeholder = true, evicted = true,
        };
        EnsureGroundTruth();
        groundTruth.Read(f.groundTruth);     // the rig as the restored scene has it: exactly this instant
        displayed = f;
        viewTime = m.sceneTime;

        int near = EstimateIndexNearest(m.sceneTime);
        staleFrom = near >= 0 && Math.Abs(history[near].sceneTime - m.sceneTime) <= MaxStaleSeconds ? history[near] : null;
        estimateDue = IsReady ? Time.unscaledTime + estimateAfterSeconds : 0f;
    }

    void Show(FvpFrame f)
    {
        displayed = f;
        if (f != null && !f.placeholder) { viewTime = f.sceneTime; staleFrom = null; }
    }

    // The whole scene as it was in that frame: everything that moves on its own, then the Animators.
    void RestoreScene(FvpFrame f)
    {
        if (f == null) return;
        RestoreState(f.anims, f.scene);
    }

    void RestoreState(FvpAnimState[] anims, FvpSceneSnapshot scene)
    {
        sceneState.Restore(scene);
        if (anims == null) return;
        foreach (FvpAnimState s in anims) s.Restore();
    }

    void TruncateAfter(int index)
    {
        for (int i = history.Count - 1; i > index; i--) Evict(history[i]);
        if (history.Count > index + 1) history.RemoveRange(index + 1, history.Count - index - 1);
    }

    void UpdateTransport()
    {
        if (!paused) return;
        if (Settling) return;

        if (deferred != null) { Action a = deferred; deferred = null; a(); }

        // Rewind / replay: advance a playhead in scene time and show the place it has reached.
        if (autoDir != 0 && HasTimeline)
        {
            playhead += autoDir * Time.unscaledDeltaTime * rewindSpeed;
            double start = TimelineStart, end = TimelineEnd;
            TimelinePos p = PositionAtOrBefore(Math.Max(start, Math.Min(playhead, end)));
            if (p.Valid && (Math.Abs(p.time - viewTime) >= SameInstant || displayed == null)) ShowPosition(p);
            if ((autoDir < 0 && playhead <= start) || (autoDir > 0 && playhead >= end)) autoDir = 0;
        }

        // A moment without an estimate, left alone for a moment: make one for it.
        if (estimateDue > 0f && autoDir == 0 && Time.unscaledTime >= estimateDue && displayed != null && displayed.placeholder &&
            IsReady && inFlight == null && building == null && !wantOneShot && stepState == StepState.Idle && stepQueue.Count == 0)
        {
            estimateDue = 0f;
            wantOneShot = true;
        }

        switch (stepState)
        {
            case StepState.Idle:
                if (stepQueue.Count > 0 && IsReady && inFlight == null && building == null && !wantOneShot)
                {
                    float seconds = stepQueue[0];
                    stepQueue.RemoveAt(0);
                    BeginSceneStep(seconds);
                }
                break;

            case StepState.Armed:
                // The frame in which the step was requested already ran with a zero delta. The next one carries
                // exactly the step; stop the clock again inside it so the one after gets zero.
                if (Time.frameCount > stepArmedFrame)
                {
                    Time.timeScale = 0f;
                    Time.captureDeltaTime = 0f;
                    stepState = StepState.Rendering;
                    stepRenderingSince = Time.realtimeSinceStartup;
                    wantOneShot = true;
                }
                break;

            case StepState.Rendering:
                if (Time.realtimeSinceStartup - stepRenderingSince > 2f) stepState = StepState.Idle; // capture never came
                break;
        }
    }

    void BeginSceneStep(float seconds)
    {
        if (!timeScaleHeld)
        {
            // The scene is not frozen (freezeSceneWhenPaused is off): there is nothing to advance, just estimate it again.
            wantOneShot = true;
            return;
        }
        // One frame at exactly the step, whatever timeScale the scene normally runs at.
        stepLength = Mathf.Max(1e-4f, seconds);
        Time.captureDeltaTime = stepLength;
        Time.timeScale = 1f;
        sceneState.AdvanceMedia(stepLength);
        stepStartTime = Time.timeAsDouble;
        stepArmedFrame = Time.frameCount;
        stepState = StepState.Armed;
    }

    // Called when the stepped frame is captured: it should be exactly one step after the frozen one. If something
    // else owns the frame time (Time.captureFramerate set elsewhere, a fixed-step editor mode...) say so once.
    void CheckStepLength()
    {
        double advanced = Time.timeAsDouble - stepStartTime;
        if (stepLengthWarned || Math.Abs(advanced - stepLength) <= 0.1 * stepLength) return;
        stepLengthWarned = true;
        Debug.LogWarning($"[FasterVoxelPose] a step advanced the scene by {advanced * 1000:F1} ms instead of {stepLength * 1000:F1} ms: " +
                         "Time.captureDeltaTime is not being honoured here. Steps are still exactly one frame.", this);
    }

    // Leave the scene's clock as we found it.
    void ReleaseTransportState()
    {
        sceneState.ResumeMedia();
        if (timeScaleHeld) { Time.timeScale = userTimeScale; timeScaleHeld = false; }
        Time.captureDeltaTime = 0f;
        paused = false; autoDir = 0; stepQueue.Clear(); stepState = StepState.Idle; wantOneShot = false; deferred = null; estimateDue = 0f;
        moments.Clear(); lastMoment = null; staleFrom = null;
    }
}
