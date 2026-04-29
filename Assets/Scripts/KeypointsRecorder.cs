using UnityEngine;
using UnityEngine.Experimental.Rendering;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Writes keyrgb/{timestamp}.png (rgb with keypoint overlay) and
// keypoints_transforms.json. Supports multi-person: add N avatars to the
// Avatars list and every frame exports a "persons" array with N entries.
// Keypoint layout: AIC 14 (see ViTPose/configs/_base_/datasets/aic.py).
// 2D projection automatically switches between pinhole (regular Camera)
// and equidistant fisheye/dome (Avante.FulldomeCamera on the same GameObject).
[RequireComponent(typeof(Camera))]
[RequireComponent(typeof(RecordingSession))]
public class KeypointsRecorder : MonoBehaviour, RecordingSession.IFrameSubscriber
{
    public const int KP_COUNT = 14;

    public static readonly string[] AicKeypointNames =
    {
        "right_shoulder", "right_elbow", "right_wrist",
        "left_shoulder",  "left_elbow",  "left_wrist",
        "right_hip", "right_knee", "right_ankle",
        "left_hip",  "left_knee",  "left_ankle",
        "head_top", "neck",
    };

    static readonly int[,] Skeleton =
    {
        {2,1},{1,0},{0,13},{13,3},{3,4},{4,5},
        {8,7},{7,6},{6,9},{9,10},{10,11},
        {12,13},{0,6},{3,9}
    };

    [Header("Subjects")]
    [Tooltip("Animators to track. Add one entry per avatar (multi-person supported). " +
             "Each avatar is exported as a separate entry in the 'persons' array per frame.")]
    public List<Animator> avatars = new List<Animator>();

    [Header("Head heuristic")]
    [Tooltip("Distance in meters along Head bone's up axis from Head origin to the top of the skull. Applied uniformly to all avatars.")]
    public float headTopUpOffset = 0.14f;

    [Header("Recording")]
    public bool recordKeypoints = false;
    [Tooltip("If enabled, write keyrgb/{t}.png with the keypoint overlay drawn on the captured image. Disable when you only need the JSON.")]
    public bool writeKeyrgbOverlay = true;
    [Tooltip("Max PNG encodes queued on background threads. Drops frames past this.")]
    public int maxInFlightEncodes = 4;

    [Header("Overlay drawing")]
    public int pointRadiusPx = 4;
    public int lineThicknessPx = 2;
    public bool drawSkeleton = true;
    [Tooltip("Flip keypoint Y when drawing. If overlay appears upside-down vs the saved image, toggle this.")]
    public bool flipKeypointY = false;

    static readonly Color32[] keypointColors =
    {
        new Color32(255,128,  0,255), // 0  right_shoulder
        new Color32(255,128,  0,255), // 1  right_elbow
        new Color32(255,128,  0,255), // 2  right_wrist
        new Color32(  0,255,  0,255), // 3  left_shoulder
        new Color32(  0,255,  0,255), // 4  left_elbow
        new Color32(  0,255,  0,255), // 5  left_wrist
        new Color32(255,128,  0,255), // 6  right_hip
        new Color32(255,128,  0,255), // 7  right_knee
        new Color32(255,128,  0,255), // 8  right_ankle
        new Color32(  0,255,  0,255), // 9  left_hip
        new Color32(  0,255,  0,255), // 10 left_knee
        new Color32(  0,255,  0,255), // 11 left_ankle
        new Color32( 51,153,255,255), // 12 head_top
        new Color32( 51,153,255,255), // 13 neck
    };

    enum KpMode { Bone, HeadTopOffset, MidShoulder }

    // One per tracked avatar — resolved bone refs, per-rig cached state.
    class AvatarRig
    {
        public Animator animator;
        public readonly Transform[] keypointTransforms = new Transform[KP_COUNT];
        public readonly KpMode[] keypointModes = new KpMode[KP_COUNT];
        public Transform leftShoulderBone;
        public Transform rightShoulderBone;
        public bool bonesResolved;
    }
    readonly List<AvatarRig> rigs = new List<AvatarRig>();

    // Candidate bone names used as a fallback when the rig is Generic.
    static readonly string[] BoneNames_LeftUpperArm  = { "LeftArm",      "LeftUpperArm",  "L_UpperArm"  };
    static readonly string[] BoneNames_RightUpperArm = { "RightArm",     "RightUpperArm", "R_UpperArm"  };
    static readonly string[] BoneNames_LeftLowerArm  = { "LeftForeArm",  "LeftLowerArm",  "L_ForeArm"   };
    static readonly string[] BoneNames_RightLowerArm = { "RightForeArm", "RightLowerArm", "R_ForeArm"   };
    static readonly string[] BoneNames_LeftHand      = { "LeftHand",     "L_Hand"                       };
    static readonly string[] BoneNames_RightHand     = { "RightHand",    "R_Hand"                       };
    static readonly string[] BoneNames_LeftUpperLeg  = { "LeftUpLeg",    "LeftUpperLeg",  "L_UpperLeg", "LeftThigh" };
    static readonly string[] BoneNames_RightUpperLeg = { "RightUpLeg",   "RightUpperLeg", "R_UpperLeg", "RightThigh" };
    static readonly string[] BoneNames_LeftLowerLeg  = { "LeftLeg",      "LeftLowerLeg",  "L_LowerLeg", "LeftCalf"  };
    static readonly string[] BoneNames_RightLowerLeg = { "RightLeg",     "RightLowerLeg", "R_LowerLeg", "RightCalf" };
    static readonly string[] BoneNames_LeftFoot      = { "LeftFoot",     "L_Foot"                       };
    static readonly string[] BoneNames_RightFoot     = { "RightFoot",    "R_Foot"                       };
    static readonly string[] BoneNames_Head          = { "Head"                                         };
    static readonly string[] BoneNames_Neck          = { "Neck"                                         };

    Transform ResolveBone(Animator anim, bool isHumanoid, HumanBodyBones bone, string[] nameCandidates)
    {
        Transform t = FindBoneByName(anim.transform, nameCandidates);
        if (t != null) return t;
        if (isHumanoid && anim.avatar != null)
        {
            try { return anim.GetBoneTransform(bone); }
            catch (System.InvalidOperationException) { /* no avatar bound */ }
        }
        return null;
    }

    static Transform FindBoneByName(Transform root, string[] candidates)
    {
        for (int i = 0; i < candidates.Length; i++)
        {
            Transform found = FindBoneRecursive(root, candidates[i]);
            if (found != null) return found;
        }
        return null;
    }

    static Transform FindBoneRecursive(Transform node, string name)
    {
        if (node.name == name || (node.name.Length > name.Length && node.name.EndsWith(":" + name)))
            return node;
        for (int i = 0; i < node.childCount; i++)
        {
            Transform result = FindBoneRecursive(node.GetChild(i), name);
            if (result != null) return result;
        }
        return null;
    }

    RecordingSession session;
    Camera cam;
    Avante.FulldomeCamera domeCam;
    string keyrgbDir;
    string jsonPath;
    StreamWriter jsonWriter;
    readonly object jsonLock = new object();
    bool jsonFirstFrame = true;
    bool acquired;
    bool sessionReady;
    int _inFlight;

    // Per-frame snapshot: jagged arrays indexed [personIndex][keypointIndex],
    // plus the pre-built JSON entry. JSON is written in OnFrameDispatch (not
    // OnFrameGather) so a frame whose readback errors or whose dispatch is
    // skipped will not leave an orphan JSON entry without a matching PNG.
    struct FrameSnapshot
    {
        public Vector2[][] imagePos;
        public bool[][]    visible;
        public string      frameJson;
    }
    readonly Dictionary<int, FrameSnapshot> pending = new Dictionary<int, FrameSnapshot>();
    readonly object pendingLock = new object();

    public bool WantsFrame => recordKeypoints && acquired;
    // Only the keyrgb PNG encode queue can back-pressure us. JSON-only mode
    // (writeKeyrgbOverlay = false) never throttles.
    public bool IsAtCapacity =>
        writeKeyrgbOverlay &&
        Interlocked.CompareExchange(ref _inFlight, 0, 0) >= maxInFlightEncodes;

    void Awake()
    {
        session = GetComponent<RecordingSession>();
        cam = GetComponent<Camera>();
        domeCam = GetComponent<Avante.FulldomeCamera>();
    }

    void OnEnable()
    {
        session.Register(this);
        if (recordKeypoints) BeginRecording();
    }

    void OnDisable()
    {
        if (acquired) EndRecording();
        session.Unregister(this);
    }

    void Update()
    {
        if (!acquired && recordKeypoints) BeginRecording();
        else if (acquired && !recordKeypoints) EndRecording();

        // Retry name-based resolve for any rig whose bones weren't bound yet at begin time.
        if (acquired)
        {
            for (int r = 0; r < rigs.Count; r++)
            {
                var rig = rigs[r];
                if (rig.bonesResolved || rig.animator == null) continue;
                ResolveRig(rig);
                rig.bonesResolved = HasAnyResolvedBone(rig);
            }
        }
    }

    void BeginRecording()
    {
        rigs.Clear();
        if (avatars == null || avatars.Count == 0)
        {
            Debug.LogError("[KeypointsRecorder] Avatars list is empty. Add at least one Animator.");
            recordKeypoints = false;
            return;
        }
        int nullCount = 0;
        for (int i = 0; i < avatars.Count; i++)
        {
            var anim = avatars[i];
            if (anim == null) { nullCount++; continue; }
            var rig = new AvatarRig { animator = anim };
            ResolveRig(rig);
            rig.bonesResolved = HasAnyResolvedBone(rig);
            if (!rig.bonesResolved)
                Debug.LogWarning($"[KeypointsRecorder] No bones resolved for '{anim.name}'. " +
                                 "Check that its skeleton has standard or Mixamo-style bone names.");
            rigs.Add(rig);
        }
        if (rigs.Count == 0)
        {
            Debug.LogError($"[KeypointsRecorder] All {nullCount} avatar entries are null.");
            recordKeypoints = false;
            return;
        }

        session.Acquire();
        acquired = true;
    }

    void EndRecording()
    {
        if (!acquired) return;
        acquired = false;
        session.Release();
    }

    [ContextMenu("Start Recording")]
    public void StartRecordingFromMenu() { recordKeypoints = true; BeginRecording(); }
    [ContextMenu("Stop Recording")]
    public void StopRecordingFromMenu() { recordKeypoints = false; EndRecording(); }

    void ResolveRig(AvatarRig rig)
    {
        Animator a = rig.animator;
        bool isHumanoid = a.isHuman;

        rig.leftShoulderBone  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftUpperArm,  BoneNames_LeftUpperArm);
        rig.rightShoulderBone = ResolveBone(a, isHumanoid, HumanBodyBones.RightUpperArm, BoneNames_RightUpperArm);
        Transform head = ResolveBone(a, isHumanoid, HumanBodyBones.Head, BoneNames_Head);
        Transform neck = ResolveBone(a, isHumanoid, HumanBodyBones.Neck, BoneNames_Neck);

        rig.keypointTransforms[0]  = rig.rightShoulderBone;
        rig.keypointTransforms[1]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightLowerArm, BoneNames_RightLowerArm);
        rig.keypointTransforms[2]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightHand,     BoneNames_RightHand);
        rig.keypointTransforms[3]  = rig.leftShoulderBone;
        rig.keypointTransforms[4]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftLowerArm,  BoneNames_LeftLowerArm);
        rig.keypointTransforms[5]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftHand,      BoneNames_LeftHand);
        rig.keypointTransforms[6]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightUpperLeg, BoneNames_RightUpperLeg);
        rig.keypointTransforms[7]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightLowerLeg, BoneNames_RightLowerLeg);
        rig.keypointTransforms[8]  = ResolveBone(a, isHumanoid, HumanBodyBones.RightFoot,     BoneNames_RightFoot);
        rig.keypointTransforms[9]  = ResolveBone(a, isHumanoid, HumanBodyBones.LeftUpperLeg,  BoneNames_LeftUpperLeg);
        rig.keypointTransforms[10] = ResolveBone(a, isHumanoid, HumanBodyBones.LeftLowerLeg,  BoneNames_LeftLowerLeg);
        rig.keypointTransforms[11] = ResolveBone(a, isHumanoid, HumanBodyBones.LeftFoot,      BoneNames_LeftFoot);

        if (head != null) { rig.keypointTransforms[12] = head; rig.keypointModes[12] = KpMode.HeadTopOffset; }
        else              { rig.keypointTransforms[12] = null; rig.keypointModes[12] = KpMode.Bone; }

        if (neck != null) { rig.keypointTransforms[13] = neck; rig.keypointModes[13] = KpMode.Bone; }
        else              { rig.keypointTransforms[13] = null; rig.keypointModes[13] = KpMode.MidShoulder; }
    }

    static bool HasAnyResolvedBone(AvatarRig rig)
    {
        for (int i = 0; i < KP_COUNT; i++)
            if (rig.keypointTransforms[i] != null) return true;
        return false;
    }

    Vector3 GetKeypointWorldPosition(AvatarRig rig, int i)
    {
        switch (rig.keypointModes[i])
        {
            case KpMode.HeadTopOffset:
            {
                Transform t = rig.keypointTransforms[i];
                return t != null ? t.position + t.up * headTopUpOffset : Vector3.zero;
            }
            case KpMode.MidShoulder:
            {
                if (rig.leftShoulderBone == null || rig.rightShoulderBone == null) return Vector3.zero;
                return 0.5f * (rig.leftShoulderBone.position + rig.rightShoulderBone.position);
            }
            default:
            {
                Transform t = rig.keypointTransforms[i];
                return t != null ? t.position : Vector3.zero;
            }
        }
    }

    Quaternion GetKeypointWorldRotation(AvatarRig rig, int i)
    {
        Transform t = rig.keypointTransforms[i];
        if (t != null) return t.rotation;
        return rig.animator != null ? rig.animator.transform.rotation : Quaternion.identity;
    }

    // --- Projection ---

    void ProjectToImage(Vector3 worldPos, int imgW, int imgH,
                        out Vector2 pixel, out float depth, out bool visible)
    {
        if (domeCam != null)
        {
            Vector3 d = worldPos - cam.transform.position;
            depth = d.magnitude;
            if (depth <= 1e-6f) { pixel = Vector2.zero; visible = false; return; }

            Vector3 dUnit = d / depth;
            Vector3 dCamLocal = Quaternion.Inverse(cam.transform.rotation) * dUnit;

            Vector3 dDome = dCamLocal;
            if (domeCam.orientation == Avante.Orientation.Fulldome)
                dDome = Quaternion.Euler(90f, 0f, 0f) * dDome;
            if (domeCam.domeTilt != 0f)
                dDome = Quaternion.Euler(-domeCam.domeTilt, 0f, 0f) * dDome;

            float horiz = Mathf.Sqrt(dDome.x * dDome.x + dDome.y * dDome.y);
            float phi = Mathf.Atan2(horiz, dDome.z);
            float theta = Mathf.Atan2(dDome.y, dDome.x);
            float horizonRad = domeCam.horizon * Mathf.Deg2Rad;
            float r = phi / (horizonRad * 0.5f);

            float uSt = r * Mathf.Cos(theta);
            float vSt = r * Mathf.Sin(theta);
            float uTex = (uSt + 1f) * 0.5f;
            float vTex = (vSt + 1f) * 0.5f;

            float px = uTex * imgW;
            float py = (1f - vTex) * imgH;
            pixel = new Vector2(px, py);
            visible = r <= 1f && px >= 0f && px < imgW && py >= 0f && py < imgH;
        }
        else
        {
            Vector3 sp = cam.WorldToScreenPoint(worldPos);
            depth = sp.z;
            float u = sp.x;
            float v = flipKeypointY ? sp.y : (imgH - 1) - sp.y;
            pixel = new Vector2(u, v);
            visible = sp.z > cam.nearClipPlane && u >= 0f && u < imgW && v >= 0f && v < imgH;
        }
    }

    // --- IFrameSubscriber ---

    public void OnSessionBegin(string sessionPath)
    {
        if (writeKeyrgbOverlay)
        {
            keyrgbDir = Path.Combine(sessionPath, "keyrgb");
            Directory.CreateDirectory(keyrgbDir);
        }
        else { keyrgbDir = null; }
        jsonPath = Path.Combine(sessionPath, "keypoints_transforms.json");
        OpenJsonWriter();
        sessionReady = true;
    }

    public void OnFrameGather(int frameIndex, double timestamp, string ts, int width, int height)
    {
        if (!sessionReady) return;

        int personCount = rigs.Count;
        var allImagePos = new Vector2[personCount][];
        var allVisible  = new bool[personCount][];

        var sb = new StringBuilder(4096 + personCount * 2048);
        sb.Append("  {");
        sb.Append("\n    \"frame_index\": ").Append(frameIndex).Append(",");
        sb.Append("\n    \"timestamp\": ").Append(F(timestamp)).Append(",");
        sb.Append("\n    \"rgb_path\": \"").Append($"rgb/{ts}.png").Append("\",");
        if (writeKeyrgbOverlay)
            sb.Append("\n    \"keyrgb_path\": \"").Append($"keyrgb/{ts}.png").Append("\",");
        sb.Append("\n    \"image_size\": [").Append(width).Append(",").Append(height).Append("],");
        sb.Append("\n    \"camera\": ").Append(BuildCameraJson(width, height)).Append(",");
        sb.Append("\n    \"persons\": [");

        for (int p = 0; p < personCount; p++)
        {
            var rig = rigs[p];
            var worldPos  = new Vector3[KP_COUNT];
            var worldRot  = new Quaternion[KP_COUNT];
            var localPos  = new Vector3[KP_COUNT];
            var imagePos  = new Vector2[KP_COUNT];
            var imageDepth = new float[KP_COUNT];
            var visible    = new bool[KP_COUNT];

            Transform root = rig.animator.transform;
            for (int i = 0; i < KP_COUNT; i++)
            {
                Vector3 wp = GetKeypointWorldPosition(rig, i);
                worldPos[i] = wp;
                worldRot[i] = GetKeypointWorldRotation(rig, i);
                localPos[i] = root.InverseTransformPoint(wp);
                ProjectToImage(wp, width, height, out imagePos[i], out imageDepth[i], out visible[i]);
            }

            sb.Append(p == 0 ? "\n      " : ",\n      ");
            sb.Append("{");
            sb.Append("\n        \"person_index\": ").Append(p).Append(",");
            sb.Append("\n        \"avatar_name\": \"").Append(rig.animator.name).Append("\",");
            sb.Append("\n        \"keypoints\": [");
            for (int i = 0; i < KP_COUNT; i++)
            {
                sb.Append(i == 0 ? "\n          " : ",\n          ");
                sb.Append("{ \"id\": ").Append(i);
                sb.Append(", \"name\": \"").Append(AicKeypointNames[i]).Append("\"");
                sb.Append(", \"world_position\": ").Append(V3(worldPos[i]));
                sb.Append(", \"world_rotation\": ").Append(Q(worldRot[i]));
                sb.Append(", \"local_position\": ").Append(V3(localPos[i]));
                sb.Append(", \"image_position\": [").Append(F(imagePos[i].x)).Append(",").Append(F(imagePos[i].y)).Append("]");
                sb.Append(", \"image_depth\": ").Append(F(imageDepth[i]));
                sb.Append(", \"visible\": ").Append(visible[i] ? "true" : "false");
                sb.Append(" }");
            }
            sb.Append("\n        ]");
            sb.Append("\n      }");

            allImagePos[p] = imagePos;
            allVisible[p]  = visible;
        }

        sb.Append("\n    ]");
        sb.Append("\n  }");

        string frameJson = sb.ToString();

        // Stash the JSON + per-person pixel coords; OnFrameDispatch writes
        // them only when the readback actually delivers bytes, so a frame
        // whose readback errors or never dispatches doesn't leave an orphan
        // JSON entry.
        lock (pendingLock)
        {
            pending[frameIndex] = new FrameSnapshot {
                imagePos = allImagePos,
                visible  = allVisible,
                frameJson = frameJson,
            };
        }
    }

    public void OnFrameDispatch(int frameIndex, double timestamp, string ts,
                                byte[] rgbTopDown, int width, int height)
    {
        if (!sessionReady) return;

        FrameSnapshot snap;
        bool found;
        lock (pendingLock)
        {
            found = pending.TryGetValue(frameIndex, out snap);
            if (found) pending.Remove(frameIndex);
        }
        if (!found) return;

        // Commit the JSON entry now that we know the readback succeeded.
        // No drop here — the session-level IsAtCapacity throttles new captures
        // instead, so JSON and PNG counts stay aligned.
        lock (jsonLock)
        {
            if (jsonWriter != null)
            {
                if (!jsonFirstFrame) jsonWriter.Write(",\n");
                jsonWriter.Write(snap.frameJson);
                jsonFirstFrame = false;
            }
        }

        if (!writeKeyrgbOverlay) return;

        string path = Path.Combine(keyrgbDir, ts + ".png");
        int w = width, h = height;
        int pointR = pointRadiusPx;
        int lineT = lineThicknessPx;
        bool drawSkel = drawSkeleton;

        byte[] buf = (byte[])rgbTopDown.Clone();
        Vector2[][] allPts = snap.imagePos;
        bool[][]    allVis = snap.visible;

        Interlocked.Increment(ref _inFlight);
        Task.Run(() =>
        {
            try
            {
                for (int p = 0; p < allPts.Length; p++)
                    DrawOverlay(buf, w, h, allPts[p], allVis[p], pointR, lineT, drawSkel);
                var png = ImageConversion.EncodeArrayToPNG(
                    buf, GraphicsFormat.R8G8B8_UNorm, (uint)w, (uint)h);
                File.WriteAllBytes(path, png);
            }
            catch (Exception e) { Debug.LogError("[KeypointsRecorder] " + e); }
            finally { Interlocked.Decrement(ref _inFlight); }
        });
    }

    public void OnSessionEnd()
    {
        int waited = 0;
        while (Interlocked.CompareExchange(ref _inFlight, 0, 0) > 0 && waited < 5000)
        {
            Thread.Sleep(20);
            waited += 20;
        }
        lock (pendingLock) { pending.Clear(); }
        CloseJsonWriter();
        sessionReady = false;
    }

    // --- Overlay ---

    static void DrawOverlay(byte[] buf, int w, int h, Vector2[] pts, bool[] vis,
                            int pointR, int lineT, bool drawSkel)
    {
        if (drawSkel)
        {
            for (int s = 0; s < Skeleton.GetLength(0); s++)
            {
                int a = Skeleton[s, 0];
                int b = Skeleton[s, 1];
                if (!vis[a] || !vis[b]) continue;
                Color32 ca = keypointColors[a], cb = keypointColors[b];
                Color32 c = new Color32((byte)((ca.r + cb.r) / 2), (byte)((ca.g + cb.g) / 2), (byte)((ca.b + cb.b) / 2), 255);
                DrawLine(buf, w, h, Mathf.RoundToInt(pts[a].x), Mathf.RoundToInt(pts[a].y),
                                    Mathf.RoundToInt(pts[b].x), Mathf.RoundToInt(pts[b].y), lineT, c);
            }
        }
        for (int i = 0; i < KP_COUNT; i++)
        {
            if (!vis[i]) continue;
            DrawFilledCircle(buf, w, h, Mathf.RoundToInt(pts[i].x), Mathf.RoundToInt(pts[i].y), pointR, keypointColors[i]);
        }
    }

    static void SetPixel(byte[] buf, int w, int h, int x, int y, Color32 c)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        int i = (y * w + x) * 3;
        buf[i] = c.r; buf[i + 1] = c.g; buf[i + 2] = c.b;
    }

    static void DrawFilledCircle(byte[] buf, int w, int h, int cx, int cy, int r, Color32 c)
    {
        int r2 = r * r;
        for (int y = -r; y <= r; y++)
        for (int x = -r; x <= r; x++)
            if (x * x + y * y <= r2) SetPixel(buf, w, h, cx + x, cy + y, c);
    }

    static void DrawLine(byte[] buf, int w, int h, int x0, int y0, int x1, int y1, int thickness, Color32 c)
    {
        int dx = Mathf.Abs(x1 - x0);
        int dy = -Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int t = Mathf.Max(0, (thickness - 1) / 2);
        while (true)
        {
            for (int oy = -t; oy <= t; oy++)
            for (int ox = -t; ox <= t; ox++)
                SetPixel(buf, w, h, x0 + ox, y0 + oy, c);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    // --- JSON helpers ---

    string BuildCameraJson(int imgW, int imgH)
    {
        Vector3 p = cam.transform.position;
        Quaternion q = cam.transform.rotation;
        var sb = new StringBuilder(512);
        sb.Append("{ \"position\": ").Append(V3(p))
          .Append(", \"rotation_quat\": ").Append(Q(q));
        if (domeCam != null)
        {
            sb.Append(", \"model\": \"fisheye_equidistant\"")
              .Append(", \"horizon_deg\": ").Append(F(domeCam.horizon))
              .Append(", \"is_fulldome\": ").Append(domeCam.orientation == Avante.Orientation.Fulldome ? "true" : "false")
              .Append(", \"dome_tilt_deg\": ").Append(F(domeCam.domeTilt));
        }
        else
        {
            float vFovRad = cam.fieldOfView * Mathf.Deg2Rad;
            float fy = 0.5f * imgH / Mathf.Tan(vFovRad * 0.5f);
            float fx = fy;
            float cx = imgW * 0.5f;
            float cy = imgH * 0.5f;
            sb.Append(", \"model\": \"pinhole\"")
              .Append(", \"fx\": ").Append(F(fx))
              .Append(", \"fy\": ").Append(F(fy))
              .Append(", \"cx\": ").Append(F(cx))
              .Append(", \"cy\": ").Append(F(cy))
              .Append(", \"near\": ").Append(F(cam.nearClipPlane))
              .Append(", \"far\": ").Append(F(cam.farClipPlane))
              .Append(", \"world_to_camera\": ").Append(M4(cam.worldToCameraMatrix))
              .Append(", \"projection\": ").Append(M4(cam.projectionMatrix));
        }
        sb.Append(" }");
        return sb.ToString();
    }

    static string V3(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "[{0:F6},{1:F6},{2:F6}]", v.x, v.y, v.z);
    static string Q(Quaternion q) => string.Format(CultureInfo.InvariantCulture, "[{0:F6},{1:F6},{2:F6},{3:F6}]", q.x, q.y, q.z, q.w);
    static string F(float f) => f.ToString("F6", CultureInfo.InvariantCulture);
    static string F(double d) => d.ToString("F6", CultureInfo.InvariantCulture);
    static string M4(Matrix4x4 m)
    {
        var sb = new StringBuilder(128);
        sb.Append("[");
        for (int r = 0; r < 4; r++)
        {
            if (r > 0) sb.Append(",");
            sb.Append("[");
            for (int c = 0; c < 4; c++)
            {
                if (c > 0) sb.Append(",");
                sb.Append(F(m[r, c]));
            }
            sb.Append("]");
        }
        sb.Append("]");
        return sb.ToString();
    }

    void OpenJsonWriter()
    {
        jsonWriter = new StreamWriter(jsonPath, false, new UTF8Encoding(false));
        jsonWriter.NewLine = "\n";
        jsonFirstFrame = true;

        jsonWriter.Write("{\n");
        jsonWriter.Write("  \"dataset\": \"aic\",\n");
        jsonWriter.Write("  \"coordinate_convention\": { \"image_origin\": \"top_left\", \"unity_screen_origin\": \"bottom_left\", \"world\": \"unity_left_handed_y_up\" },\n");
        jsonWriter.Write("  \"keypoint_names\": [");
        for (int i = 0; i < AicKeypointNames.Length; i++)
        {
            if (i > 0) jsonWriter.Write(",");
            jsonWriter.Write("\""); jsonWriter.Write(AicKeypointNames[i]); jsonWriter.Write("\"");
        }
        jsonWriter.Write("],\n");
        jsonWriter.Write("  \"skeleton\": [");
        for (int i = 0; i < Skeleton.GetLength(0); i++)
        {
            if (i > 0) jsonWriter.Write(",");
            jsonWriter.Write("[");
            jsonWriter.Write(Skeleton[i, 0].ToString(CultureInfo.InvariantCulture));
            jsonWriter.Write(",");
            jsonWriter.Write(Skeleton[i, 1].ToString(CultureInfo.InvariantCulture));
            jsonWriter.Write("]");
        }
        jsonWriter.Write("],\n");
        jsonWriter.Write("  \"frames\": [\n");
        jsonWriter.Flush();
    }

    void CloseJsonWriter()
    {
        lock (jsonLock)
        {
            if (jsonWriter == null) return;
            try
            {
                jsonWriter.Write("\n  ]\n}\n");
                jsonWriter.Flush();
            }
            finally
            {
                jsonWriter.Dispose();
                jsonWriter = null;
            }
        }
    }
}