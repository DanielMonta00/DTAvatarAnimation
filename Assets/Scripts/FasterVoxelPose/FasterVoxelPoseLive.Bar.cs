using UnityEngine;

// The transport bar on EVERY display, so the controls are there whichever display the Game view shows (IMGUI could only draw on
// Display 1). One uGUI canvas per display (FvpTransportBar); the pointer is read through the input system, like the rest of the
// mouse controls, and hit-tested against the same FvpBarLayout the bars are drawn from. The layout follows the Game view size.
public partial class FasterVoxelPoseLive
{
    const int BarDisplays = 8; // Unity's maximum
    const double LongJumpSeconds = 1.0;
    const int MidJumpFrames = 10;

    FvpTransportBar[] bars;
    readonly FvpBarLayout barLayout = new FvpBarLayout();
    FvpBarLayout.Part barHover, barDown;

    // For tests and tools: the geometry the bars use, and the bar drawn on a display (0-based).
    public FvpBarLayout BarLayout => barLayout;
    public FvpTransportBar BarOn(int display) => bars != null && display >= 0 && display < bars.Length ? bars[display] : null;

    void EnsureBarLayout() => barLayout.Compute(Screen.width, Screen.height, showControls, uiScale);

    void UpdateBars()
    {
        if (!Application.isPlaying || !isActiveAndEnabled) { DestroyBars(); return; }
        if (bars == null)
        {
            bars = new FvpTransportBar[BarDisplays];
            for (int d = 0; d < bars.Length; d++) bars[d] = new FvpTransportBar(d);
        }
        EnsureBarLayout();
        bool timeline = HasTimeline && TimelineEnd - TimelineStart > 1e-6;
        var view = new FvpBarView
        {
            paused = paused, synced = overlayImage == OverlayImage.SyncedFrame, autoDir = autoDir,
            pos = timeline ? Mathf.RoundToInt(TimelineFraction * 1000f) : 0,
            behindCs = timeline ? Mathf.RoundToInt((float)SecondsBehind * 100f) : 0,
            usable = timeline, live = IsLive, hover = barHover, down = barDown,
        };
        foreach (FvpTransportBar b in bars)
        {
            b.Apply(barLayout, view);
            b.hud.Apply(hudData, TitleOf(b.display), barLayout.uiScale);
            b.heat.Apply(heatData, b.display, barLayout.uiScale);
        }
    }

    void DestroyBars()
    {
        if (bars == null) return;
        foreach (FvpTransportBar b in bars) b.Destroy();
        bars = null;
        barHover = barDown = FvpBarLayout.Part.None;
    }

    // The bar's part of a pointer event (screen pixels, origin bottom-left). Runs even when the mouse gestures on the displays
    // are off: the buttons are the bar's, not the picture's.
    void HandleBar(Vector2 pos, bool pressed, bool released, bool held, bool inside)
    {
        EnsureBarLayout();
        barHover = inside ? barLayout.PartAt(pos) : FvpBarLayout.Part.None;

        if (pressed && inside && barLayout.Contains(pos))
        {
            barDown = barHover;
            if (barDown == FvpBarLayout.Part.Slider) ScrubTo(pos);
        }
        else if (barDown == FvpBarLayout.Part.Slider && held) ScrubTo(pos);

        if (released && barDown != FvpBarLayout.Part.None)
        {
            if (barHover == barDown) Activate(barDown);
            barDown = FvpBarLayout.Part.None;
        }
        else if (barDown != FvpBarLayout.Part.None && !held && !pressed) barDown = FvpBarLayout.Part.None; // released where we did not see it
    }

    void ScrubTo(Vector2 pos)
    {
        if (HasTimeline) ScrubToFraction(barLayout.FractionAt(pos.x));
    }

    void Activate(FvpBarLayout.Part part)
    {
        switch (part)
        {
            case FvpBarLayout.Part.ToStart: GoToStart(); break;
            case FvpBarLayout.Part.Rewind: ToggleRewind(); break;
            case FvpBarLayout.Part.PrevEstimate: PreviousEstimate(); break;
            case FvpBarLayout.Part.BackLong: Jump(-LongJumpSeconds); break;
            case FvpBarLayout.Part.BackMid: Jump(-MidJumpFrames * stepSeconds); break;
            case FvpBarLayout.Part.StepBack: StepBackward(); break;
            case FvpBarLayout.Part.PlayPause: TogglePause(); break;
            case FvpBarLayout.Part.StepForward: StepForward(); break;
            case FvpBarLayout.Part.FwdMid: Jump(MidJumpFrames * stepSeconds); break;
            case FvpBarLayout.Part.FwdLong: Jump(LongJumpSeconds); break;
            case FvpBarLayout.Part.NextEstimate: NextEstimate(); break;
            case FvpBarLayout.Part.Replay: ToggleReplay(); break;
            case FvpBarLayout.Part.ToNewest: GoToNewest(); break;
            case FvpBarLayout.Part.Mode:
                overlayImage = overlayImage == OverlayImage.SyncedFrame ? OverlayImage.RealTime : OverlayImage.SyncedFrame;
                break;
            case FvpBarLayout.Part.Hide: showControls = false; break;
            case FvpBarLayout.Part.Show: showControls = true; break;
        }
    }
}
