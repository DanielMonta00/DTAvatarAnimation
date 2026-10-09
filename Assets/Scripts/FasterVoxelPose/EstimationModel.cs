using UnityEngine;

// A model that runs on the cameras of the digital twin and has something to look at. Put each one on its own child of an
// "EstimationModels" object that carries an EstimationModelsHub: the hub finds them, lists them in a tab strip on every display and
// switches which one's corner panels (status card, heatmaps) are shown. Models are MonoBehaviours implementing this.
public interface IEstimationModel
{
    string ModelName { get; }
    Color ModelColor { get; }             // the colour of its skeletons and of its tab
    string Summary { get; }               // one line for the hub: state, rate, latency, people found
    bool IsRunning { get; }               // enabled and on its way (not necessarily answering yet)
    bool HasPanels { get; }               // has corner panels to select; false: its tab only switches its skeletons on and off (the ground truth)
    bool PanelsVisible { get; set; }      // its corner panels (status card, heatmaps); set by the hub, true when there is none
    bool OverlayVisible { get; set; }     // its skeletons over the camera displays; set by the hub, true when there is none
    void StepJoint(int direction);        // J / Shift+J while its tab is selected: the next / previous joint of its heatmaps
    void ToggleHeatmaps();                // G
}

// A model that does not capture the cameras itself but runs on the synced images FasterVoxelPoseLive captures, so that every model
// sees exactly the same frames (and the history and the transport of FasterVoxelPose cover all of them). FasterVoxelPoseLive keeps
// capturing for its consumers even when its own server is not up.
public interface IFvpFrameConsumer
{
    bool WantsFrame { get; }                    // is it idle and ready for the next frame?
    bool Participates { get; }                  // is it running and connected (so a frame it cannot take is a frame it misses)?
    void OnFrameMissed();                       // a frame was captured while it was busy
    // Called on the main thread when a frame has been captured. The model that takes it must hold its pixel buffers:
    // Interlocked.Increment(ref frame.pendingOps) now, Interlocked.Decrement when it is done with frame.raw.
    void OnFrameCaptured(FvpFrame frame);
}
