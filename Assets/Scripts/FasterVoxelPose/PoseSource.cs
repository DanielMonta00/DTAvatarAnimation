using System.Collections.Generic;
using UnityEngine;

// One estimate of who is where, in the form every pose estimator of this project can hand to a consumer (PoseEstimateFollower
// today; Bulles, PontRoulant... later) without the consumer knowing which model made it.
public struct PoseSample
{
    public IReadOnlyList<FvpPerson> people;   // Unity world, metres, Panoptic-15 joints; the model's own phantom filter has already run
    public double captureTime;                // on the source's clock (PoseClockNow): the instant the camera images were taken
    public int serial;                        // changes with every new estimate
    public float latencyMs;                   // capture -> estimate in hand, real milliseconds (0 = not known)
}

// Implemented by FasterVoxelPoseLite, FasterVoxelPoseLive and ViTPoseLive (see their *.PoseSource.cs parts).
public interface IPoseSource
{
    string PoseSourceName { get; }
    bool PoseSourceRunning { get; }            // enabled, in Play mode and answering (not only trying to connect)
    double PoseClockNow { get; }               // the clock captureTime is on, now. Scene time: it stops with Time.timeScale = 0
    string PoseSourceStatus { get; }           // one line for the Inspector
    // The newest estimate. False until there is one, and while the model is showing a past frame instead (FasterVoxelPose paused or rewound).
    bool TryGetPose(out PoseSample sample);
    // Is this camera one whose images go to the model (its own cameras, and the shadow cameras it makes for them)? Anything drawn in
    // the digital twin that a real camera would not see (the protection capsule) has to stay out of those images.
    bool IsPoseCamera(Camera camera);
}
