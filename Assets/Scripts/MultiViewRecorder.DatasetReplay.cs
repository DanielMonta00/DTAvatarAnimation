using System;
using System.Globalization;
using System.Text;
using UnityEngine;

// Dataset replay: record the digital twin at exactly the instants of a recorded dataset's camera pictures.
//
// Played in real time the twin is only roughly in step with the dataset: the game clock jitters, a recording of 'every N-th rendered frame'
// samples the take at different instants than the real camera did, and the camera lags the mocap. Here the recorder takes over the time:
// for each picture of the dataset's camera (<dataset>/cam107/<unix time>.jpg) it freezes the avatar's Animator at the take time that picture
// was taken (see DatasetReplayTiming), renders and captures that, and only then moves on to the next picture. So frame k of the recording
// is the same instant as picture k of the dataset, one for one, whatever the machine's frame rate: the files are even named by the
// dataset's own picture names (rgb/1790868454.418153.jpg next to cam107/1790868454.418153.jpg), and the video has the dataset's frame rate.
// A frame the encoders cannot take yet waits (nothing is skipped), and the recording stops after the dataset's last picture.
//
// What it cannot do: the clip must be the take of this session, and the avatar must stand where the dataset's registration puts it
// (RokokoFramePlacement with the registration of the SAME fixed take: see Tools > Dataset Replay). Everything else in the scene that moves
// on its own clock (robot, videos, particles) still runs in real time.
public partial class MultiViewRecorder
{
    [Header("Dataset replay")]
    [Tooltip("Record the dataset frame by frame instead of in real time: one captured frame per picture of the dataset's camera, the avatar's animation " +
             "set to the instant of each picture, files named like the dataset's pictures. Max Frames still stops it early (0 = the whole dataset); " +
             "Record One Loop, Set Capture Framerate and the pacing are ignored. Needs Dataset Folder and an avatar whose Animator Controller plays the take.")]
    public bool replayDataset = false;
    [Tooltip("The dataset's session folder (HumanDatasetRecording/Datasets/<session>): it holds the camera's pictures (<camera>/<unix time>.jpg) and rokoko_skeleton.jsonl.")]
    public string datasetFolder = "";
    [Tooltip("The sub-folder of the dataset with the pictures to match, e.g. cam107.")]
    public string datasetCamera = "cam107";
    [Tooltip("take time = studio_timestamp + this. How far the clip the avatar plays starts before the live stream did; the dataset tools find it " +
             "(python -m recording.studio_take --session <folder> --take <the .anim>): -1.947 s for test01_20261001_172733, the same for every export of that take.")]
    public float takeTimeOffset = -1.947f;
    [Tooltip("Seconds the camera's pictures lag behind the mocap (camera_delay_s of recalage/rokoko_corrected.jsonl). The picture taken at T shows the person as the mocap had them at T - delay.")]
    public float cameraDelay = 0.2f;
    [Tooltip("The state of the avatar's Animator Controller (layer 0) that plays the take, e.g. 'test01_20261001_172733 (1)_fixed'. Empty = the state it is in when the recording starts.")]
    public string replayAnimatorState = "";
    [Tooltip("Index of the first dataset picture to record (0 = from the start).")]
    [Min(0)] public int replayStartFrame = 0;

    DatasetReplayTiming _replayTiming;
    DatasetReplayPlayer _replayPlayer;
    int _replayFirst;                    // dataset index of the first recorded picture
    int _replayHeld;                     // frames whose instant lies outside the clip (shown at its first / last pose)

    public bool ReplayActive => _replayPlayer != null;
    public int ReplayPictureCount => _replayTiming != null ? _replayTiming.Count : 0;

    // Called as the session starts. False = cannot replay (said why): the session is not started.
    bool BeginReplay()
    {
        _replayTiming = null; _replayPlayer = null; _replayHeld = 0;
        if (!replayDataset) return true;

        _replayTiming = DatasetReplayTiming.Load(datasetFolder, datasetCamera, out string error);
        if (_replayTiming == null) { Debug.LogError("[MultiViewRecorder] Dataset replay: " + error, this); return false; }

        Animator animator = null;
        foreach (Animator a in avatars)
            if (a != null && a.isActiveAndEnabled && a.runtimeAnimatorController != null) { animator = a; break; }
        if (animator == null) { Debug.LogError("[MultiViewRecorder] Dataset replay: no avatar with an Animator Controller (the take is one of its states).", this); _replayTiming = null; return false; }

        var player = new DatasetReplayPlayer();
        if (!player.Begin(animator, replayAnimatorState, out error))
        {
            Debug.LogError("[MultiViewRecorder] Dataset replay: " + error, this);
            _replayTiming = null;
            return false;
        }
        _replayPlayer = player;
        _replayFirst = Mathf.Clamp(replayStartFrame, 0, _replayTiming.Count - 1);

        int before = 0, after = 0;
        for (int k = _replayFirst; k < _replayTiming.Count; k++)
        {
            double t = _replayTiming.TakeTime(k, cameraDelay, takeTimeOffset);
            if (t < DatasetReplayPlayer.FirstUsableSeconds) before++; else if (t > player.length) after++;
        }
        _replayHeld = before + after;

        int left = _replayTiming.Count - _replayFirst;
        double span = _replayTiming.times[_replayTiming.Count - 1] - _replayTiming.times[_replayFirst];
        Debug.Log($"[MultiViewRecorder] Dataset replay: {left} pictures of {datasetCamera} ({span:F1} s, {(left > 1 ? (left - 1) / span : 0.0):F2} per second) from picture {_replayFirst}; " +
                  $"take '{player.stateName}' = clip '{player.clipName}' ({player.length:F2} s) at studio_timestamp {takeTimeOffset:+0.000;-0.000} s, camera delay {cameraDelay:F3} s; " +
                  $"mocap clock slope {_replayTiming.clockSlope:F6} (scatter {_replayTiming.clockRmsMs:F1} ms over {_replayTiming.mocapFrames} frames). " +
                  $"Picture {_replayFirst} is the take at {_replayTiming.TakeTime(_replayFirst, cameraDelay, takeTimeOffset):F3} s" +
                  (_replayHeld > 0 ? $"; {_replayHeld} pictures fall outside the usable part of the clip ({before} before {DatasetReplayPlayer.FirstUsableSeconds:F1} s, {after} after its end): its first / last pose is shown for them." : "."), this);
        if (maxFrames > 0 && maxFrames < left)
            Debug.LogWarning($"[MultiViewRecorder] Dataset replay: Max Frames ({maxFrames}) stops the recording before the end of the dataset ({left} pictures to go). Set it to 0 for the whole dataset.", this);
        if (recordOneLoop)
            Debug.LogWarning("[MultiViewRecorder] Dataset replay: Record One Loop is ignored (the recording ends with the dataset's last picture).", this);
        return true;
    }

    void EndReplay()
    {
        _replayPlayer?.End();
        _replayPlayer = null;
        _replayTiming = null;
    }

    // Dataset index of the picture the next captured frame stands for.
    int ReplayNextPicture => _replayFirst + frameCounter;

    bool ReplayFinished() => _replayTiming != null && ReplayNextPicture >= _replayTiming.Count;

    // Called right before a capture starts, in Update (so the render of this frame shows it): the avatar at the instant of the next picture.
    void ReplayPose()
    {
        int k = ReplayNextPicture;
        if (k >= _replayTiming.Count) return;
        _replayPlayer.Apply(_replayTiming.TakeTime(k, cameraDelay, takeTimeOffset));
    }

    // The frame being captured is the dataset's picture ReplayNextPicture - 1 (frameCounter has just moved on): its time and its file name.
    void ReplayFrameIdentity(int captured, out double relTime, out string name)
    {
        int k = _replayFirst + captured;
        relTime = _replayTiming.times[k] - _replayTiming.times[_replayFirst];
        name = _replayTiming.names[k];
    }

    // Extra fields of a frame's JSON entry (ends with a comma and a new line), or nothing when not replaying.
    string ReplayFrameJson(int captured)
    {
        if (_replayTiming == null) return "";
        int k = _replayFirst + captured;
        var sb = new StringBuilder(160);
        sb.Append("\n    \"dataset_picture\": ").Append(k).Append(",");
        sb.Append("\n    \"dataset_time\": ").Append(_replayTiming.times[k].ToString("F6", CultureInfo.InvariantCulture)).Append(",");
        sb.Append("\n    \"take_time\": ").Append(_replayTiming.TakeTime(k, cameraDelay, takeTimeOffset).ToString("F6", CultureInfo.InvariantCulture)).Append(",");
        return sb.ToString();
    }

    // Header block of keypoints_transforms.json describing the replay (ends with a comma and a new line), or nothing.
    string ReplayHeaderJson()
    {
        if (_replayTiming == null || _replayPlayer == null) return "";
        string folder = (_replayTiming.folder ?? "").Replace('\\', '/').Replace("\"", "\\\"");
        string state = (_replayPlayer.stateName ?? "").Replace("\"", "\\\"");
        string clip = (_replayPlayer.clipName ?? "").Replace("\"", "\\\"");
        var inv = CultureInfo.InvariantCulture;
        return "  \"dataset_replay\": { \"folder\": \"" + folder + "\", \"camera\": \"" + datasetCamera + "\", \"first_picture\": " + _replayFirst.ToString(inv) +
               ", \"pictures\": " + _replayTiming.Count.ToString(inv) + ", \"take_time_offset\": " + takeTimeOffset.ToString("R", inv) +
               ", \"camera_delay\": " + cameraDelay.ToString("R", inv) + ", \"clock_slope\": " + _replayTiming.clockSlope.ToString("R", inv) +
               ", \"clock_intercept\": " + _replayTiming.clockIntercept.ToString("R", inv) + ", \"animator_state\": \"" + state + "\", \"clip\": \"" + clip +
               "\", \"clip_length\": " + _replayPlayer.length.ToString("R", inv) + " },\n";
    }
}
