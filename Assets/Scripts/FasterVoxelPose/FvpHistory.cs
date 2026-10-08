using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

// A person Faster-VoxelPose found, in the Unity world.
public sealed class FvpPerson
{
    public int id;           // stable while the person stays in view (see FvpTracker); the network's own slot order is not
    public float score;
    public readonly Vector3[] joints = new Vector3[FvpSkeleton.Count]; // metres, Unity world, Panoptic-15 order
    public bool[] valid;     // null = every joint valid (ground truth from a rig can lack joints)
    public Vector3 velocity; // horizontal root velocity, m/s, from the last estimates of this id (zero for a new person)
    public float errMm = -1f; // mean joint distance to the ground truth at the same instant, mm; < 0 = nothing to compare with
    public bool ghost;       // there is ground truth, and nobody within a metre of this skeleton

    public Vector3 Root => joints[FvpSkeleton.MidHip];
    public Color Color => FvpSkeleton.ColorFor(id);
    public bool IsValid(int joint) => valid == null || valid[joint];
}

// Where an Animator was, so rewinding can put the scene back. Layer states are restored through the
// Animator itself (Play + Update(0)), which re-evaluates the pose from the clip instead of copying bones.
public sealed class FvpAnimState
{
    public Animator animator;
    public int[] stateHash;
    public float[] normalizedTime;
    public Vector3 localPosition;
    public Quaternion localRotation;

    public static FvpAnimState Capture(Animator a)
    {
        int layers = Mathf.Max(1, a.layerCount);
        var s = new FvpAnimState
        {
            animator = a,
            stateHash = new int[layers],
            normalizedTime = new float[layers],
            localPosition = a.transform.localPosition,
            localRotation = a.transform.localRotation,
        };
        for (int l = 0; l < layers; l++)
        {
            AnimatorStateInfo st = a.GetCurrentAnimatorStateInfo(l);
            s.stateHash[l] = st.fullPathHash;
            s.normalizedTime[l] = st.normalizedTime;
        }
        return s;
    }

    public void Restore()
    {
        if (animator == null || !animator.isActiveAndEnabled) return;
        animator.transform.localPosition = localPosition;
        animator.transform.localRotation = localRotation;
        for (int l = 0; l < stateHash.Length; l++)
            animator.Play(stateHash[l], l, normalizedTime[l]);
        animator.Update(0f); // evaluate now, whatever Time.timeScale is
    }
}

// One captured + estimated frame: what the cameras showed, what the network answered, and the scene state.
public sealed class FvpFrame
{
    public int id;
    public double sceneTime;       // Time.time when the images were taken
    public int unityFrame;
    public float realtime;         // Time.realtimeSinceStartup, for latency
    public int width, height;
    public FvpCameraModel[] cameras;
    public FvpAnimState[] anims;
    public FvpSceneSnapshot scene;   // everything else that moves (see FvpSceneState)

    // Per view, top-left origin RGB24 straight from the GPU readback (pooled): exactly what was sent to the network.
    // Valid until the frame has been answered, so read it from FrameEstimated if you need the images.
    public byte[][] raw;
    public int pendingOps;         // 1 while the sender thread still holds the buffers; touched via Interlocked

    public float[] poses;          // [maxPeople, 15, 5]: x, y, z (mm, model frame), valid, score
    public readonly List<FvpPerson> people = new List<FvpPerson>();
    // Ground truth at the instant of the capture: the avatar rigs read out as the same 15 joints (see FvpGroundTruth).
    public readonly List<FvpPerson> groundTruth = new List<FvpPerson>();
    // Which GPU copy of each camera's image belongs to this frame (see FasterVoxelPoseLive.FrozenImage).
    public int frozenBuffer = -1;
    public int[] frozenSerial;
    public float netMs, totalMs;
    // The 2D joint heatmaps of every view, laid over the sent frame (see FvpProtocol): [view][y * heatW + x], 255 = 1.0.
    // heatJoint is the joint they are for (-1 = the strongest of all). Null when not asked for or the server is older.
    public byte[][] heat;
    public int heatW, heatH, heatJoint = -2;
    public float latencyMs;        // capture -> estimate in hand
    public bool fromStep;          // captured by a single step while paused
    public bool estimated;
    public bool evicted;           // dropped from history while buffers were still in use
}

// Reuses the large per-view frame buffers: at ten frames a second they would otherwise churn the LOH.
public static class FvpBufferPool
{
    static readonly ConcurrentQueue<byte[]> free = new ConcurrentQueue<byte[]>();
    static int size;

    public static byte[] Rent(int bytes)
    {
        if (bytes != size) { while (free.TryDequeue(out _)) { } size = bytes; }
        return free.TryDequeue(out byte[] b) && b.Length == bytes ? b : new byte[bytes];
    }

    public static void Return(byte[] b)
    {
        if (b != null && b.Length == size && free.Count < 32) free.Enqueue(b);
    }

    public static void Clear() { while (free.TryDequeue(out _)) { } }
}

// Keeps a person's id from one estimate to the next: nearest root within a gate, greedy by distance.
public sealed class FvpTracker
{
    public float gateMetres = 1.0f;
    public float maxSpeed = 3.0f;      // m/s, a walking person never exceeds this; bigger jumps are estimate noise
    int nextId = 1;
    List<FvpPerson> previous = new List<FvpPerson>();
    double previousTime;

    public void Reset(IList<FvpPerson> from, double time)
    {
        previous = from != null ? new List<FvpPerson>(from) : new List<FvpPerson>();
        previousTime = time;
        foreach (FvpPerson p in previous) nextId = Mathf.Max(nextId, p.id + 1);
    }

    // Gives every person of `now` (estimated at scene time `time`) an id, and a smoothed horizontal root velocity.
    public void Assign(List<FvpPerson> now, double time)
    {
        var pairs = new List<(float d, int n, int p)>();
        for (int n = 0; n < now.Count; n++)
            for (int p = 0; p < previous.Count; p++)
            {
                float d = Vector3.Distance(now[n].Root, previous[p].Root);
                if (d <= gateMetres) pairs.Add((d, n, p));
            }
        pairs.Sort((a, b) => a.d.CompareTo(b.d));

        var usedNow = new bool[now.Count];
        var usedPrev = new bool[previous.Count];
        foreach (var (d, n, p) in pairs)
        {
            if (usedNow[n] || usedPrev[p]) continue;
            usedNow[n] = usedPrev[p] = true;
            now[n].id = previous[p].id;

            double dt = time - previousTime;
            Vector3 v = previous[p].velocity;
            if (dt > 0.02 && dt < 1.5)
            {
                Vector3 raw = (now[n].Root - previous[p].Root) / (float)dt;
                raw.y = 0f;
                if (raw.magnitude > maxSpeed) raw = raw.normalized * maxSpeed;
                v = Vector3.Lerp(previous[p].velocity, raw, dt > 0.3 ? 0.7f : 0.5f); // a little smoothing (the estimate is noisy), less when estimates are rare
            }
            now[n].velocity = v;
        }
        for (int n = 0; n < now.Count; n++)
            if (!usedNow[n]) now[n].id = nextId++;

        previous = new List<FvpPerson>(now);
        previousTime = time;
    }
}
