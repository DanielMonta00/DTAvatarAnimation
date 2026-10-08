using System;
using UnityEngine;

// Transport controls: pause, step, rewind.
//
// "Frame" here means one estimate in the history: the camera images the network saw, the skeletons it found and
// the state of the tracked Animators at that moment.
//
//   Pause        freezes the scene (Time.timeScale = 0) and estimates the frozen frame once, so what is on show is
//                exactly the paused scene.
//   Step >       at the newest frame: lets the scene advance by stepSeconds and estimates it; further back: walks
//                forward through the history.
//   Step <       walks back through the history. The tracked Animators are put back to that frame as it goes, so
//                the 3D scene follows the cursor.
//   Rewind <<    keeps stepping back at the recorded speed (rewindSpeed x); Replay >> does the same forwards.
//   Play         resumes. After a rewind the scene continues from the frame under the cursor (the frames after
//                it are dropped), provided Animators are tracked; otherwise it continues from the newest frame.
//
// The scene only rewinds as far as its Animators do. Physics, particle systems and scripts keep their present
// state while the images and skeletons come from the past.
public partial class FasterVoxelPoseLive
{
    enum StepState { Idle, Armed, Rendering }

    bool paused;
    int autoDir;                  // -1 rewinding, +1 replaying history forwards, 0 neither
    double playhead;              // scene time the auto replay has reached
    int stepQueue;
    StepState stepState;
    int stepArmedFrame;
    float stepRenderingSince;
    double stepStartTime;
    bool stepLengthWarned;
    bool wantOneShot;             // estimate the (frozen) scene once even though paused
    float userTimeScale = 1f;
    bool timeScaleHeld;
    float pausedAtReal;
    Action deferred;              // navigation asked for while the pause was still settling

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

    public bool CanStepBack => history.Count > 0 && cursor > 0;
    public bool AtNewest => history.Count == 0 || cursor >= history.Count - 1;
    public int Rewinding => autoDir;

    // ---------------- commands ----------------

    public void TogglePause() { if (paused) Resume(); else Pause(); }

    public void Pause()
    {
        if (paused) return;
        paused = true;
        autoDir = 0;
        pausedAtReal = Time.realtimeSinceStartup;
        if (freezeSceneWhenPaused)
        {
            if (!timeScaleHeld) { userTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f; timeScaleHeld = true; }
            Time.timeScale = 0f;
        }
        wantOneShot = IsReady;
    }

    public void Resume()
    {
        if (!paused) return;
        autoDir = 0; stepQueue = 0; deferred = null;
        if (stepState != StepState.Idle) { stepState = StepState.Idle; Time.captureDeltaTime = 0f; }

        bool behind = history.Count > 0 && cursor < history.Count - 1;
        if (behind)
        {
            // Whatever is still being estimated belongs to the timeline we are leaving.
            DropInFlight();
            if (building != null) { FvpFrame b = building; building = null; b.evicted = true; }
            if (SceneRewindable)
            {
                RestoreScene(history[cursor]);
                TruncateAfter(cursor);
                tracker.Reset(history[cursor].people);
            }
            else
            {
                cursor = history.Count - 1;
                Show(history[cursor]);
            }
        }
        followHead = true;
        paused = false;
        wantOneShot = false;
        if (timeScaleHeld) { Time.timeScale = userTimeScale; timeScaleHeld = false; }
    }

    public void StepForward()
    {
        if (!paused) { Pause(); return; } // the first press stops the scene on its current frame
        RunWhenSettled(() =>
        {
            autoDir = 0;
            if (!AtNewest) GoTo(cursor + 1);
            else if (stepQueue < 8) stepQueue++;
        });
    }

    public void StepBackward()
    {
        if (!paused) Pause();
        RunWhenSettled(() =>
        {
            autoDir = 0;
            if (cursor > 0) GoTo(cursor - 1);
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
            playhead = history[cursor].sceneTime;
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
            playhead = history[cursor].sceneTime;
        });
    }

    public void GoToStart()
    {
        if (!paused) Pause();
        RunWhenSettled(() => { autoDir = 0; if (history.Count > 0) GoTo(0); });
    }

    public void GoToNewest()
    {
        if (!paused) return;
        RunWhenSettled(() => { autoDir = 0; if (history.Count > 0) GoTo(history.Count - 1); });
    }

    public void Scrub(int index)
    {
        if (!paused) Pause();
        RunWhenSettled(() => { autoDir = 0; if (history.Count > 0 && index != cursor) GoTo(index); });
    }

    void RunWhenSettled(Action a)
    {
        if (Settling) deferred = a; else a();
    }

    // ---------------- internals ----------------

    void GoTo(int index)
    {
        if (history.Count == 0) return;
        cursor = Mathf.Clamp(index, 0, history.Count - 1);
        followHead = cursor == history.Count - 1;
        Show(history[cursor]);
        if (paused) RestoreScene(history[cursor]);
    }

    void Show(FvpFrame f) { displayed = f; }

    static void RestoreScene(FvpFrame f)
    {
        if (f == null || f.anims == null) return;
        foreach (FvpAnimState s in f.anims) s.Restore();
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

        // Rewind / replay: advance a playhead in scene time and show the frame it has reached.
        if (autoDir != 0 && history.Count > 0)
        {
            playhead += autoDir * Time.unscaledDeltaTime * rewindSpeed;
            int c = cursor;
            if (autoDir < 0) { while (c > 0 && history[c].sceneTime > playhead) c--; }
            else { while (c < history.Count - 1 && history[c + 1].sceneTime <= playhead) c++; }
            if (c != cursor) GoTo(c);
            if ((autoDir < 0 && cursor <= 0) || (autoDir > 0 && cursor >= history.Count - 1)) autoDir = 0;
        }

        switch (stepState)
        {
            case StepState.Idle:
                if (stepQueue > 0 && IsReady && inFlight == null && building == null && !wantOneShot)
                {
                    stepQueue--;
                    BeginSceneStep();
                }
                break;

            case StepState.Armed:
                // The frame in which the step was requested already ran with a zero delta. The next one carries
                // exactly stepSeconds; stop the clock again inside it so the one after gets zero.
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

    void BeginSceneStep()
    {
        if (!timeScaleHeld)
        {
            // The scene is not frozen (freezeSceneWhenPaused is off): there is nothing to advance, just estimate it again.
            wantOneShot = true;
            return;
        }
        // One frame at exactly stepSeconds, whatever timeScale the scene normally runs at.
        Time.captureDeltaTime = Mathf.Max(1e-4f, stepSeconds);
        Time.timeScale = 1f;
        stepStartTime = Time.timeAsDouble;
        stepArmedFrame = Time.frameCount;
        stepState = StepState.Armed;
    }

    // Called when the stepped frame is captured: it should be exactly stepSeconds after the frozen one. If something
    // else owns the frame time (Time.captureFramerate set elsewhere, a fixed-step editor mode...) say so once.
    void CheckStepLength()
    {
        double advanced = Time.timeAsDouble - stepStartTime;
        if (stepLengthWarned || Math.Abs(advanced - stepSeconds) <= 0.1 * stepSeconds) return;
        stepLengthWarned = true;
        Debug.LogWarning($"[FasterVoxelPose] a step advanced the scene by {advanced * 1000:F1} ms instead of Step Seconds ({stepSeconds * 1000:F1} ms): " +
                         "Time.captureDeltaTime is not being honoured here. Steps are still exactly one frame.", this);
    }

    // Leave the scene's clock as we found it.
    void ReleaseTransportState()
    {
        if (timeScaleHeld) { Time.timeScale = userTimeScale; timeScaleHeld = false; }
        Time.captureDeltaTime = 0f;
        paused = false; autoDir = 0; stepQueue = 0; stepState = StepState.Idle; wantOneShot = false; deferred = null;
    }
}
