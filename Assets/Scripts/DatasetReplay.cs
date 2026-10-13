using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

// The timeline of a recorded dataset (HumanDatasetRecording/Datasets/<session>), for replaying it frame by frame.
//
// Every picture of the dataset's camera (<session>/cam107/<unix time>.jpg) is one moment of the PC clock. The person on that picture was
// where the motion capture had them 'camera delay' seconds earlier (the camera's pictures lag: 0.2 s, camera_delay_s in
// recalage/rokoko_corrected.jsonl), and the mocap runs on Rokoko Studio's own clock, which the live stream (rokoko_skeleton.jsonl) ties to
// the PC clock: t_recv = slope * studio_timestamp + intercept (a straight-line fit: t_recv carries the network jitter, studio_timestamp does not).
// A Studio take exported from the same recording (the .anim the avatar plays) starts a fixed time before the live stream did:
// take time = studio_timestamp + 'take offset' (the tools find it: python -m recording.studio_take --session ... --take ...; -1.947 s for this session).
//
//     take time of picture k  =  (T_k - camera delay - intercept) / slope + take offset
//
// Checked against the dataset's own pairing of pictures and mocap frames (recalage/heuristics.jsonl): T_k - 0.2 is exactly the time it interpolates.
public sealed class DatasetReplayTiming
{
    public string folder, camera;
    public double[] times;            // PC-clock time of every picture, ascending
    public string[] names;            // the pictures' file names without extension, as the dataset wrote them ("1790868454.418153")
    public double clockSlope = 1.0;   // PC time = slope * studio time + intercept
    public double clockIntercept;
    public int mocapFrames;           // frames of the live stream the clock was fitted on
    public double clockRmsMs;         // how far t_recv scatters around that line (network jitter)

    public int Count => times.Length;

    // Where the take (the clip the avatar plays) is when picture k was taken, in seconds from the start of the clip. May lie outside the clip.
    public double TakeTime(int k, double cameraDelay, double takeOffset)
        => (times[k] - cameraDelay - clockIntercept) / clockSlope + takeOffset;

    // folder: the session folder; camera: the sub-folder with the pictures ("cam107"). On failure returns null and says why.
    public static DatasetReplayTiming Load(string folder, string camera, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) { error = "dataset folder not found: '" + folder + "'"; return null; }
        string camDir = Path.Combine(folder, camera ?? "");
        if (!Directory.Exists(camDir)) { error = "no '" + camera + "' folder with the camera pictures in " + folder; return null; }
        string skeleton = Path.Combine(folder, "rokoko_skeleton.jsonl");
        if (!File.Exists(skeleton)) { error = "no rokoko_skeleton.jsonl in " + folder + " (it ties the mocap clock to the camera's)"; return null; }

        // the pictures
        var pics = new SortedDictionary<double, string>();
        foreach (string f in Directory.GetFiles(camDir))
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") continue;
            string stem = Path.GetFileNameWithoutExtension(f);
            if (double.TryParse(stem, NumberStyles.Float, CultureInfo.InvariantCulture, out double t)) pics[t] = stem;
        }
        if (pics.Count == 0) { error = "no pictures named by their time (<unix time>.jpg) in " + camDir; return null; }

        // the clock: t_recv against studio_timestamp over the live stream
        var st = new List<double>(2048);
        var tr = new List<double>(2048);
        foreach (string line in File.ReadLines(skeleton))
        {
            if (!TryNumber(line, "\"t_recv\":", out double a) || !TryNumber(line, "\"studio_timestamp\":", out double b)) continue;
            tr.Add(a); st.Add(b);
        }
        if (st.Count < 10) { error = "rokoko_skeleton.jsonl has too few frames with t_recv and studio_timestamp"; return null; }

        double mx = 0, my = 0;
        for (int i = 0; i < st.Count; i++) { mx += st[i]; my += tr[i]; }
        mx /= st.Count; my /= st.Count;
        double sxx = 0, sxy = 0;
        for (int i = 0; i < st.Count; i++) { double dx = st[i] - mx; sxx += dx * dx; sxy += dx * (tr[i] - my); }
        if (sxx <= 0.0) { error = "studio_timestamp never changes in rokoko_skeleton.jsonl"; return null; }
        double slope = sxy / sxx, icpt = my - slope * mx;
        double ss = 0;
        for (int i = 0; i < st.Count; i++) { double r = tr[i] - (slope * st[i] + icpt); ss += r * r; }

        var timing = new DatasetReplayTiming
        {
            folder = folder, camera = camera,
            times = new double[pics.Count], names = new string[pics.Count],
            clockSlope = slope, clockIntercept = icpt, mocapFrames = st.Count, clockRmsMs = Math.Sqrt(ss / st.Count) * 1000.0,
        };
        int n = 0;
        foreach (var kv in pics) { timing.times[n] = kv.Key; timing.names[n] = kv.Value; n++; }
        return timing;
    }

    // The number after `key` in a JSON line (no full parse: the lines are ~4 kB of bones).
    static bool TryNumber(string line, string key, out double value)
    {
        value = 0;
        int i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return false;
        i += key.Length;
        while (i < line.Length && line[i] == ' ') i++;
        int j = i;
        while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '.' || line[j] == '-' || line[j] == '+' || line[j] == 'e' || line[j] == 'E')) j++;
        return j > i && double.TryParse(line.Substring(i, j - i), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

// Puts an Animator at an exact moment of its clip, whatever the game clock does: the avatar then shows the dataset's take at the instant
// of the picture being recorded, not wherever real time has taken it. The Animator is frozen (speed 0) so it cannot move on by itself
// between the pose being set and the frame being captured; End() gives it its speed back.
public sealed class DatasetReplayPlayer
{
    // The first frame of a Studio take is a default pose (T-pose with the suit's hip height), and the dataset tools do not use the take before
    // 0.2 s either (recording/studio_take.py FIRST_USABLE_S): earlier instants show the clip at this time instead (the person is standing there).
    public const double FirstUsableSeconds = 0.2;

    public Animator animator;
    public string stateName;          // what plays (for the log)
    public string clipName;
    public float length;              // seconds of the state
    int stateHash;
    float savedSpeed;
    bool begun;

    // stateName: a state of layer 0 (empty: the state it is in now). On failure returns false and says why.
    public bool Begin(Animator a, string state, out string error)
    {
        error = null;
        if (a == null) { error = "no animator"; return false; }
        if (a.runtimeAnimatorController == null) { error = "'" + a.name + "' has no Animator Controller (the take is a state of it)"; return false; }
        if (!a.isActiveAndEnabled) { error = "'" + a.name + "' is not active"; return false; }

        int hash;
        if (string.IsNullOrWhiteSpace(state)) hash = a.GetCurrentAnimatorStateInfo(0).fullPathHash;
        else
        {
            hash = Animator.StringToHash(state.Trim());
            if (!a.HasState(0, hash))
            {
                hash = Animator.StringToHash("Base Layer." + state.Trim());
                if (!a.HasState(0, hash)) { error = "'" + a.name + "' has no state '" + state + "' in layer 0"; return false; }
            }
        }

        a.Play(hash, 0, 0f);
        a.Update(0f);
        AnimatorStateInfo st = a.GetCurrentAnimatorStateInfo(0);
        AnimatorClipInfo[] clips = a.GetCurrentAnimatorClipInfo(0);
        length = clips.Length == 1 && clips[0].clip != null ? clips[0].clip.length : st.length;
        clipName = clips.Length > 0 && clips[0].clip != null ? clips[0].clip.name : "?";
        if (length <= 0f) { error = "the state has no length (is a clip assigned?)"; return false; }

        animator = a; stateHash = hash; stateName = string.IsNullOrWhiteSpace(state) ? "(current state)" : state.Trim();
        savedSpeed = a.speed;
        a.speed = 0f;
        begun = true;
        return true;
    }

    // The pose at `seconds` into the clip (held at its first usable / last pose outside it). Returns true when the moment lies outside that range.
    public bool Apply(double seconds)
    {
        if (!begun || animator == null) return false;
        bool outside = seconds < FirstUsableSeconds || seconds > length;
        float t = (float)Math.Min(Math.Max(seconds, FirstUsableSeconds), length);
        animator.Play(stateHash, 0, Math.Min(t / length, 0.999999f));      // never 1.0: a looping state would wrap to its first pose
        animator.Update(0f);
        return outside;
    }

    public void End()
    {
        if (begun && animator != null) animator.speed = savedSpeed;
        begun = false;
    }
}
