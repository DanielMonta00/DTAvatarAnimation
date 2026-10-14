using UnityEngine;

// FasterVoxelPose as a source of poses for whatever reacts to people (see PoseSource.cs): the estimate on show while the scene runs.
public partial class FasterVoxelPoseLive : IPoseSource
{
    public string PoseSourceName => "FasterVoxelPose";
    public bool PoseSourceRunning => IsRunning && IsReady;
    public double PoseClockNow => SceneNow;
    public string PoseSourceStatus => status;

    public bool IsPoseCamera(Camera camera)
    {
        if (camera == null) return false;
        if (slots != null)
            foreach (ViewSlot s in slots) if (s.cam == camera || s.shadow == camera) return true;
        return cameras != null && cameras.Contains(camera);
    }

    // Not while a past frame is on show (paused / rewound / stepping): then People is history, not what is happening.
    public bool TryGetPose(out PoseSample sample)
    {
        FvpFrame f = displayed;
        if (f == null || !IsLive || !f.estimated || f.placeholder) { sample = default; return false; }
        sample = new PoseSample { people = f.people, captureTime = f.sceneTime, serial = f.id, latencyMs = f.latencyMs };
        return true;
    }
}
