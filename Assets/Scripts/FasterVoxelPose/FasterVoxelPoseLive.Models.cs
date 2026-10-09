using System;
using System.Collections.Generic;
using UnityEngine;

// FasterVoxelPose as one of the estimation models (IEstimationModel), and the host of the cameras for the others (IFvpFrameConsumer).
public partial class FasterVoxelPoseLive
{
    // ---------------- IEstimationModel ----------------

    public string ModelName => "FasterVoxelPose";
    public Color ModelColor => new Color(1f, 0.6f, 0.18f);
    public bool IsRunning => isActiveAndEnabled && Application.isPlaying;
    public bool HasPanels => true;
    public bool PanelsVisible { get; set; } = true;
    public bool OverlayVisible { get; set; } = true;   // the estimate's skeletons; the ground truth has its own switch below

    // The avatars' ground truth (green) is not part of the estimate: it has its own tab in the hub, and shows with or without any model.
    public bool GroundTruthVisible { get; set; } = true;
    GroundTruthTab groundTruthTab;
    public IEstimationModel GroundTruthEntry => groundTruthTab ?? (groundTruthTab = new GroundTruthTab(this));

    sealed class GroundTruthTab : IEstimationModel
    {
        readonly FasterVoxelPoseLive host;
        public GroundTruthTab(FasterVoxelPoseLive host) { this.host = host; }
        public string ModelName => "Ground truth";
        public Color ModelColor => new Color(0.49f, 1f, 0.49f);
        public string Summary => host.groundTruthAvatars != null ? host.groundTruthAvatars.Count + " avatar(s) read from their rigs" : "none";
        public bool IsRunning => host != null && host.isActiveAndEnabled && Application.isPlaying;
        public bool HasPanels => false;
        public bool PanelsVisible { get; set; } = true;
        public bool OverlayVisible { get => host.GroundTruthVisible; set => host.GroundTruthVisible = value; }
        public void StepJoint(int direction) { }
        public void ToggleHeatmaps() { }
    }
    public void StepJoint(int direction) => NextHeatJoint(direction);
    public void ToggleHeatmaps() => showHeatmaps = !showHeatmaps;

    // Set by an EstimationModelsHub: the J / Shift+J and G keys then drive the model whose tab is selected, not this one.
    public static Action<int> StepJointOverride;
    public static Action ToggleHeatmapsOverride;

    public string Summary
    {
        get
        {
            if (!IsReady) return "waiting: " + (string.IsNullOrEmpty(status) ? "starting" : status);
            FvpFrame f = displayed;
            return $"{(paused ? "paused" : "live")}, {f?.people.Count ?? 0} person(s), {estimateFps:F1}/s, {(f != null ? f.latencyMs : 0f):F0} ms";
        }
    }

    // ---------------- sharing the cameras ----------------

    readonly List<IFvpFrameConsumer> frameConsumers = new List<IFvpFrameConsumer>();

    public void AddFrameConsumer(IFvpFrameConsumer c)
    {
        if (c != null && !frameConsumers.Contains(c)) frameConsumers.Add(c);
    }

    public void RemoveFrameConsumer(IFvpFrameConsumer c) => frameConsumers.Remove(c);

    bool ConsumersWant
    {
        get
        {
            for (int i = 0; i < frameConsumers.Count; i++) if (frameConsumers[i].WantsFrame) return true;
            return false;
        }
    }

    // A running model that has not answered the previous frame yet.
    bool ConsumerBusy
    {
        get
        {
            for (int i = 0; i < frameConsumers.Count; i++)
                if (frameConsumers[i].Participates && !frameConsumers[i].WantsFrame) return true;
            return false;
        }
    }

    void NotifyConsumers(FvpFrame f)
    {
        for (int i = 0; i < frameConsumers.Count; i++)
        {
            if (frameConsumers[i].WantsFrame) frameConsumers[i].OnFrameCaptured(f);
            else if (frameConsumers[i].Participates) frameConsumers[i].OnFrameMissed();
        }
    }

    // For the models that draw next to it: the size of the UI and what each display shows.
    public float UiScale => barLayout.uiScale;
    public string DisplayTitle(int display) => TitleOf(display);

    // The pose of the camera views as the other models see them: the models of the frame in hand, or built now.
    public FvpCameraModel[] CameraModelsFor(FvpFrame f) => f?.cameras ?? sentModels;

    // What the transport shows right now, for the models that draw next to it.
    public bool ShowsSavedImage(int view) => overlayViews != null && view >= 0 && view < overlayViews.Length && overlayViews[view].overlay != null && overlayViews[view].overlay.Image != null;
    public bool IsLetterboxed(int view) => overlayViews != null && view >= 0 && view < overlayViews.Length && overlayViews[view].overlay != null && overlayViews[view].overlay.letterboxed;

    // A pointer press the hub's tabs (or any other widget of the models) own: not a click on the picture.
    public static Func<Vector2, bool> ExtraPointerBlocker;
}
