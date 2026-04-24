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
// keypoints_transforms.json. The GPU readback and session folder are owned
// by RecordingSession; this component only projects keypoints and draws/
// encodes the overlay. When both this and RgbImageRecorder are
// active, keyrgb/ pixels are bit-identical to rgb/ except for the drawn
// overlay, because both subscribers consume the same RGB byte buffer.
//
// Keypoint layout: AIC 14 (see ViTPose/configs/_base_/datasets/aic.py).
// 2D projection automatically switches between pinhole (regular Camera)
// and equidistant fisheye/dome (when an Avante.FulldomeCamera is on the
// same GameObject), so keypoint pixel coords always match the saved image.
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

    [Header("Subject")]
    [Tooltip("Humanoid Animator of the avatar whose keypoints are exported.")]
    public Animator avatar;

    [Header("Head/Neck overrides (optional)")]
    public Transform headTopOverride;
    public Transform neckOverride;
    [Tooltip("Distance in meters along Head bone's up axis from Head origin to the top of the skull.")]
    public float headTopUpOffset = 0.14f;

    [Header("Recording")]
    public bool recordKeypoints = false;
    [Tooltip("Max PNG encodes queued on background threads. Drops frames past this.")]
    public int maxInFlightEncodes = 4;

    [Header("Overlay drawing")]
    public int pointRadiusPx = 4;
    public int lineThicknessPx = 2;
    public bool drawSkeleton = true;

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

    readonly Transform[] keypointTransforms = new Transform[KP_COUNT];
    readonly KpMode[] keypointModes = new KpMode[KP_COUNT];
    Transform leftShoulderBone;
    Transform rightShoulderBone;

    // Per-frame snapshot populated in OnFrameGather and consumed in OnFrameDispatch.
    // Keyed by frameIndex since multiple frames can be in flight simultaneously.
    struct FrameSnapshot
    {
        public Vector2[] imagePos;
        public bool[] visible;
    }
    readonly Dictionary<int, FrameSnapshot> pending = new Dictionary<int, FrameSnapshot>();
    readonly object pendingLock = new object();

    public bool WantsFrame => recordKeypoints && acquired;

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
    }

    void BeginRecording()
    {
        if (avatar == null) { Debug.LogError("[KeypointsRecorder] Avatar Animator is not assigned."); recordKeypoints = false; return; }
        if (!avatar.isHuman) { Debug.LogError("[KeypointsRecorder] Avatar Animator must be humanoid."); recordKeypoints = false; return; }

        ResolveKeypointTransforms();
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

    void ResolveKeypointTransforms()
    {
        leftShoulderBone  = avatar.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        rightShoulderBone = avatar.GetBoneTransform(HumanBodyBones.RightUpperArm);
        Transform head = avatar.GetBoneTransform(HumanBodyBones.Head);
        Transform neck = avatar.GetBoneTransform(HumanBodyBones.Neck);

        keypointTransforms[0]  = rightShoulderBone;
        keypointTransforms[1]  = avatar.GetBoneTransform(HumanBodyBones.RightLowerArm);
        keypointTransforms[2]  = avatar.GetBoneTransform(HumanBodyBones.RightHand);
        keypointTransforms[3]  = leftShoulderBone;
        keypointTransforms[4]  = avatar.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        keypointTransforms[5]  = avatar.GetBoneTransform(HumanBodyBones.LeftHand);
        keypointTransforms[6]  = avatar.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        keypointTransforms[7]  = avatar.GetBoneTransform(HumanBodyBones.RightLowerLeg);
        keypointTransforms[8]  = avatar.GetBoneTransform(HumanBodyBones.RightFoot);
        keypointTransforms[9]  = avatar.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        keypointTransforms[10] = avatar.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
        keypointTransforms[11] = avatar.GetBoneTransform(HumanBodyBones.LeftFoot);

        if (headTopOverride != null) { keypointTransforms[12] = headTopOverride; keypointModes[12] = KpMode.Bone; }
        else                         { keypointTransforms[12] = head;             keypointModes[12] = KpMode.HeadTopOffset; }

        if (neckOverride != null) { keypointTransforms[13] = neckOverride; keypointModes[13] = KpMode.Bone; }
        else if (neck != null)    { keypointTransforms[13] = neck;         keypointModes[13] = KpMode.Bone; }
        else                      { keypointTransforms[13] = null;         keypointModes[13] = KpMode.MidShoulder; }

        for (int i = 0; i < KP_COUNT; i++)
        {
            if (keypointModes[i] == KpMode.MidShoulder)
            {
                if (leftShoulderBone == null || rightShoulderBone == null)
                    Debug.LogWarning("[KeypointsRecorder] Cannot compute 'neck' via mid-shoulder: shoulder bones missing.");
            }
            else if (keypointTransforms[i] == null)
            {
                Debug.LogWarning($"[KeypointsRecorder] No transform resolved for '{AicKeypointNames[i]}'.");
            }
        }
    }

    Vector3 GetKeypointWorldPosition(int i)
    {
        switch (keypointModes[i])
        {
            case KpMode.HeadTopOffset:
            {
                Transform t = keypointTransforms[i];
                return t != null ? t.position + t.up * headTopUpOffset : Vector3.zero;
            }
            case KpMode.MidShoulder:
            {
                if (leftShoulderBone == null || rightShoulderBone == null) return Vector3.zero;
                return 0.5f * (leftShoulderBone.position + rightShoulderBone.position);
            }
            default:
            {
                Transform t = keypointTransforms[i];
                return t != null ? t.position : Vector3.zero;
            }
        }
    }

    Quaternion GetKeypointWorldRotation(int i)
    {
        Transform t = keypointTransforms[i];
        if (t != null) return t.rotation;
        return avatar != null ? avatar.transform.rotation : Quaternion.identity;
    }

    // --- Projection ---

    // Project world position to image pixel coords (top-left origin) and
    // report camera-space depth + visibility. Uses the dome equidistant
    // projection when FulldomeCamera is present, else a standard pinhole
    // via Camera.WorldToScreenPoint.
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

            // Invert the shader's cameraRot = cam * R_x(-π/2)[fulldome] * R_x(domeTilt).
            Vector3 dDome = dCamLocal;
            if (domeCam.orientation == Avante.Orientation.Fulldome)
                dDome = Quaternion.Euler(90f, 0f, 0f) * dDome;         // inverse of R_x(-π/2)
            if (domeCam.domeTilt != 0f)
                dDome = Quaternion.Euler(-domeCam.domeTilt, 0f, 0f) * dDome;

            // Dome-local: zenith = +Z, phi from +Z, theta in xy-plane.
            float horiz = Mathf.Sqrt(dDome.x * dDome.x + dDome.y * dDome.y);
            float phi = Mathf.Atan2(horiz, dDome.z);
            float theta = Mathf.Atan2(dDome.y, dDome.x);
            float horizonRad = domeCam.horizon * Mathf.Deg2Rad;
            float r = phi / (horizonRad * 0.5f);

            float uSt = r * Mathf.Cos(theta);   // [-1, 1] when r <= 1
            float vSt = r * Mathf.Sin(theta);
            float uTex = (uSt + 1f) * 0.5f;     // [0, 1]
            float vTex = (vSt + 1f) * 0.5f;

            float px = uTex * imgW;
            float py = (1f - vTex) * imgH;      // top-left origin
            pixel = new Vector2(px, py);
            visible = r <= 1f && px >= 0f && px < imgW && py >= 0f && py < imgH;
        }
        else
        {
            Vector3 sp = cam.WorldToScreenPoint(worldPos);
            depth = sp.z;
            float u = sp.x;
            float v = (imgH - 1) - sp.y;
            pixel = new Vector2(u, v);
            visible = sp.z > cam.nearClipPlane && u >= 0f && u < imgW && v >= 0f && v < imgH;
        }
    }

    // --- IFrameSubscriber ---

    public void OnSessionBegin(string sessionPath)
    {
        keyrgbDir = Path.Combine(sessionPath, "keyrgb");
        Directory.CreateDirectory(keyrgbDir);
        jsonPath = Path.Combine(sessionPath, "keypoints_transforms.json");
        OpenJsonWriter();
        sessionReady = true;
    }

    public void OnFrameGather(int frameIndex, double timestamp, string timestampString, int width, int height)
    {
        if (!sessionReady) return;

        int imgW = width;
        int imgH = height;

        Vector3[] worldPos = new Vector3[KP_COUNT];
        Quaternion[] worldRot = new Quaternion[KP_COUNT];
        Vector3[] localPos = new Vector3[KP_COUNT];
        Vector2[] imagePos = new Vector2[KP_COUNT];
        float[] imageDepth = new float[KP_COUNT];
        bool[] visible = new bool[KP_COUNT];

        Transform root = avatar.transform;
        for (int i = 0; i < KP_COUNT; i++)
        {
            Vector3 wp = GetKeypointWorldPosition(i);
            worldPos[i] = wp;
            worldRot[i] = GetKeypointWorldRotation(i);
            localPos[i] = root.InverseTransformPoint(wp);
            ProjectToImage(wp, imgW, imgH, out imagePos[i], out imageDepth[i], out visible[i]);
        }

        string frameJson = BuildFrameJson(frameIndex, timestamp, timestampString,
            worldPos, worldRot, localPos, imagePos, imageDepth, visible, imgW, imgH);
        lock (jsonLock)
        {
            if (jsonWriter != null)
            {
                if (!jsonFirstFrame) jsonWriter.Write(",\n");
                jsonWriter.Write(frameJson);
                jsonFirstFrame = false;
            }
        }

        lock (pendingLock)
        {
            pending[frameIndex] = new FrameSnapshot { imagePos = imagePos, visible = visible };
        }
    }

    public void OnFrameDispatch(int frameIndex, double timestamp, string timestampString,
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

        if (Interlocked.CompareExchange(ref _inFlight, 0, 0) >= maxInFlightEncodes) return;

        string path = Path.Combine(keyrgbDir, timestampString + ".png");
        int w = width, h = height;
        int pointR = pointRadiusPx;
        int lineT = lineThicknessPx;
        bool drawSkel = drawSkeleton;

        // We must NOT mutate the shared byte buffer — clone before drawing.
        byte[] buf = (byte[])rgbTopDown.Clone();
        Vector2[] pts = snap.imagePos;
        bool[] vis = snap.visible;

        Interlocked.Increment(ref _inFlight);
        Task.Run(() =>
        {
            try
            {
                DrawOverlay(buf, w, h, pts, vis, pointR, lineT, drawSkel);
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
        // Drain encodes.
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

    // --- JSON ---

    string BuildFrameJson(int idx, double relTime, string ts,
        Vector3[] wp, Quaternion[] wr, Vector3[] lp, Vector2[] ip, float[] iz, bool[] vis,
        int imgW, int imgH)
    {
        string rgbRel = $"rgb/{ts}.png";
        string keyRel = $"keyrgb/{ts}.png";

        var sb = new StringBuilder(4096);
        sb.Append("  {");
        sb.Append("\n    \"frame_index\": ").Append(idx).Append(",");
        sb.Append("\n    \"timestamp\": ").Append(F(relTime)).Append(",");
        sb.Append("\n    \"rgb_path\": \"").Append(rgbRel).Append("\",");
        sb.Append("\n    \"keyrgb_path\": \"").Append(keyRel).Append("\",");
        sb.Append("\n    \"image_size\": [").Append(imgW).Append(",").Append(imgH).Append("],");
        sb.Append("\n    \"camera\": ").Append(BuildCameraJson(imgW, imgH)).Append(",");
        sb.Append("\n    \"keypoints\": [");
        for (int i = 0; i < KP_COUNT; i++)
        {
            sb.Append(i == 0 ? "\n      " : ",\n      ");
            sb.Append("{ \"id\": ").Append(i);
            sb.Append(", \"name\": \"").Append(AicKeypointNames[i]).Append("\"");
            sb.Append(", \"world_position\": ").Append(V3(wp[i]));
            sb.Append(", \"world_rotation\": ").Append(Q(wr[i]));
            sb.Append(", \"local_position\": ").Append(V3(lp[i]));
            sb.Append(", \"image_position\": [").Append(F(ip[i].x)).Append(",").Append(F(ip[i].y)).Append("]");
            sb.Append(", \"image_depth\": ").Append(F(iz[i]));
            sb.Append(", \"visible\": ").Append(vis[i] ? "true" : "false");
            sb.Append(" }");
        }
        sb.Append("\n    ]");
        sb.Append("\n  }");
        return sb.ToString();
    }

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