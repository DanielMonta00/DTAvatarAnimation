using UnityEngine;

// ViTPose (its people lifted to 3D by triangulation) as a source of poses for whatever reacts to people (see PoseSource.cs).
public sealed partial class ViTPoseLive : IPoseSource
{
    public string PoseSourceName => "ViTPose (3D by triangulation)";
    public bool PoseSourceRunning => IsRunning && linkUp && host_ != null && lift3D;
    public double PoseClockNow => host_ != null ? host_.SceneNow : Time.timeAsDouble;
    public string PoseSourceStatus => host_ == null ? "no FasterVoxelPose to take the camera frames from" : status;

    // ViTPose reads the frames FasterVoxelPose captures: those are its cameras.
    public bool IsPoseCamera(Camera camera) => host_ != null && host_.IsPoseCamera(camera);

    // Only the 3D people (lift3D), and only while the camera host is running live: a paused or rewound host shows history.
    public bool TryGetPose(out PoseSample sample)
    {
        ViTPoseResult r = latest;
        if (r == null || host_ == null || !host_.IsLive || !lift3D) { sample = default; return false; }
        sample = new PoseSample { people = r.people3d, captureTime = r.sceneTime, serial = r.frameId, latencyMs = r.latencyMs };
        return true;
    }
}
