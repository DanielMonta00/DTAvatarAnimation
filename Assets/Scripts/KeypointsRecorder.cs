using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Records a synchronised dataset of (RGB frame, RGB-with-keypoints overlay,
// per-frame keypoint transforms) for evaluating human-pose-estimation models.
// Mapping follows the 14-keypoint AI Challenger (AIC) layout used by ViTPose
// (see ViTPose/configs/_base_/datasets/aic.py). AIC is used here instead of
// COCO because its keypoints correspond 1:1 to Mecanim humanoid bones; only
// head_top and neck need minor computation.
[RequireComponent(typeof(Camera))]
public class KeypointsRecorder : MonoBehaviour
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

    // AIC skeleton edges (pairs of keypoint indices) used for overlay drawing.
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
    [Tooltip("If assigned, used as-is for the head_top keypoint (otherwise: Head bone + up offset).")]
    public Transform headTopOverride;
    [Tooltip("If assigned, used as-is for the neck keypoint (otherwise: Neck bone, falling back to mid-shoulder).")]
    public Transform neckOverride;

    [Header("Head heuristic")]
    [Tooltip("Distance in meters along Head bone's up axis from Head origin to the top of the skull.")]
    public float headTopUpOffset = 0.14f;

    [Header("Recording")]
    public bool record = false;
    public string outputFolder = "Recordings";
    public string sessionName = "KeypointsSession";
    [Tooltip("Max PNG encodes queued on background threads. Drops frames past this.")]
    public int maxInFlightEncodes = 4;
    [Tooltip("If true, this recorder owns Time.captureFramerate. Leave false when another recorder already sets it.")]
    public bool setCaptureFramerate = false;
    public int frameRate = 30;

    [Header("Overlay drawing")]
    public int pointRadiusPx = 4;
    public int lineThicknessPx = 2;
    public bool drawSkeleton = true;

    [Header("Image orientation")]
    [Tooltip("Flip rows before encoding PNGs. Default true matches standard top-down PNG convention on DX/Metal.")]
    public bool flipOutputY = true;

    Camera cam;
    bool isRecording;
    bool _capturing;
    int _inFlight;
    int frameIndex;
    double recordingStartTime = -1.0;
    string sessionPath;
    RenderTexture rt;

    StreamWriter jsonWriter;
    readonly object jsonLock = new object();
    bool jsonFirstFrame = true;

    // Per-keypoint resolution.
    enum KpMode { Bone, HeadTopOffset, MidShoulder }
    readonly Transform[] keypointTransforms = new Transform[KP_COUNT];
    readonly KpMode[] keypointModes = new KpMode[KP_COUNT];

    // Cached refs used by the MidShoulder mode for 'neck'.
    Transform leftShoulderBone;
    Transform rightShoulderBone;

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

    void Awake() { cam = GetComponent<Camera>(); }
    void Start() { if (record) BeginRecording(); }
    void OnApplicationQuit() { if (isRecording) EndRecording(); }
    void OnDisable() { if (isRecording) EndRecording(); }

    [ContextMenu("Start Recording")]
    public void StartRecordingFromMenu() { record = true; BeginRecording(); }
    [ContextMenu("Stop Recording")]
    public void StopRecordingFromMenu() { EndRecording(); }

    void BeginRecording()
    {
        if (cam == null) cam = GetComponent<Camera>();
        if (avatar == null) { Debug.LogError("[KeypointsRecorder] Avatar Animator is not assigned."); record = false; return; }
        if (!avatar.isHuman) { Debug.LogError("[KeypointsRecorder] Avatar Animator must be humanoid."); record = false; return; }

        ResolveKeypointTransforms();

        sessionPath = Path.Combine(Application.dataPath, "..", outputFolder,
            sessionName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(sessionPath);
        Directory.CreateDirectory(Path.Combine(sessionPath, "rgb"));
        Directory.CreateDirectory(Path.Combine(sessionPath, "keyrgb"));

        EnsureRT(Mathf.Max(16, cam.pixelWidth), Mathf.Max(16, cam.pixelHeight));
        if (setCaptureFramerate) Time.captureFramerate = frameRate;

        OpenJsonWriter();

        frameIndex = 0;
        recordingStartTime = -1.0;
        jsonFirstFrame = true;
        isRecording = true;

        Debug.Log($"[KeypointsRecorder] Recording started at {sessionPath}");
    }

    void EndRecording()
    {
        if (!isRecording) return;
        isRecording = false;
        CloseJsonWriter();
        if (rt != null) { rt.Release(); Destroy(rt); rt = null; }
        Debug.Log($"[KeypointsRecorder] Recording stopped at {sessionPath} ({frameIndex} frames).");
    }

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

        // head_top: either an explicit override, or Head bone + up offset.
        if (headTopOverride != null)
        {
            keypointTransforms[12] = headTopOverride;
            keypointModes[12] = KpMode.Bone;
        }
        else
        {
            keypointTransforms[12] = head;
            keypointModes[12] = KpMode.HeadTopOffset;
        }

        // neck: override > Neck bone > mid-shoulder fallback.
        if (neckOverride != null)
        {
            keypointTransforms[13] = neckOverride;
            keypointModes[13] = KpMode.Bone;
        }
        else if (neck != null)
        {
            keypointTransforms[13] = neck;
            keypointModes[13] = KpMode.Bone;
        }
        else
        {
            keypointTransforms[13] = null; // computed from L/R shoulders
            keypointModes[13] = KpMode.MidShoulder;
        }

        for (int i = 0; i < KP_COUNT; i++)
        {
            if (keypointModes[i] == KpMode.MidShoulder)
            {
                if (leftShoulderBone == null || rightShoulderBone == null)
                    Debug.LogWarning("[KeypointsRecorder] Cannot resolve 'neck' via mid-shoulder: shoulder bones missing.");
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
        // MidShoulder: inherit rotation from the mid-shoulder frame is ambiguous;
        // fall back to the avatar root so downstream code always has a valid quat.
        return avatar != null ? avatar.transform.rotation : Quaternion.identity;
    }

    void EnsureRT(int w, int h)
    {
        if (rt != null && rt.width == w && rt.height == h) return;
        if (rt != null) { rt.Release(); Destroy(rt); }
        rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        rt.Create();
    }

    void Update()
    {
        if (!isRecording && record) BeginRecording();
        else if (isRecording && !record) EndRecording();
        if (isRecording && !_capturing) StartCoroutine(CaptureCoroutine());
    }

    IEnumerator CaptureCoroutine()
    {
        _capturing = true;
        yield return new WaitForEndOfFrame();
        try
        {
            if (!isRecording) yield break;

            int w = Mathf.Max(16, cam.pixelWidth);
            int h = Mathf.Max(16, cam.pixelHeight);
            EnsureRT(w, h);

            if (Interlocked.CompareExchange(ref _inFlight, 0, 0) >= maxInFlightEncodes) yield break;

            if (recordingStartTime < 0.0) recordingStartTime = Time.realtimeSinceStartup;
            double relTime = Time.realtimeSinceStartup - recordingStartTime;
            int idx = frameIndex++;
            string ts = relTime.ToString("F6", CultureInfo.InvariantCulture);
            string rgbRel = $"rgb/{ts}.png";
            string keyRel = $"keyrgb/{ts}.png";
            string rgbPath = Path.Combine(sessionPath, rgbRel);
            string keyPath = Path.Combine(sessionPath, keyRel);

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

                Vector3 sp = cam.WorldToScreenPoint(wp);
                imageDepth[i] = sp.z;
                // Unity screen-space has origin at bottom-left; image convention is top-left.
                float u = sp.x;
                float v = (h - 1) - sp.y;
                imagePos[i] = new Vector2(u, v);
                visible[i] = sp.z > cam.nearClipPlane && u >= 0f && u < w && v >= 0f && v < h;
            }

            Vector3 camPos = cam.transform.position;
            Quaternion camRot = cam.transform.rotation;
            float vFovRad = cam.fieldOfView * Mathf.Deg2Rad;
            float fy = 0.5f * h / Mathf.Tan(vFovRad * 0.5f);
            float fx = fy; // square pixels: hFov derived from vFov*aspect gives fx == fy
            float cx = w * 0.5f;
            float cy = h * 0.5f;
            Matrix4x4 proj = cam.projectionMatrix;
            Matrix4x4 worldToCam = cam.worldToCameraMatrix;

            string frameJson = BuildFrameJson(idx, relTime, rgbRel, keyRel,
                worldPos, worldRot, localPos, imagePos, imageDepth, visible,
                camPos, camRot, fx, fy, cx, cy, cam.nearClipPlane, cam.farClipPlane,
                worldToCam, proj);
            lock (jsonLock)
            {
                if (jsonWriter != null)
                {
                    if (!jsonFirstFrame) jsonWriter.Write(",\n");
                    jsonWriter.Write(frameJson);
                    jsonFirstFrame = false;
                }
            }

            // Render camera into our RT (without leaving the camera bound to it).
            var prevTarget = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prevTarget;

            Interlocked.Increment(ref _inFlight);

            bool flipY = flipOutputY;
            int pointR = pointRadiusPx;
            int lineT = lineThicknessPx;
            bool drawSkel = drawSkeleton;

            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGB24, req =>
            {
                try
                {
                    if (req.hasError) { Interlocked.Decrement(ref _inFlight); return; }
                    var data = req.GetData<byte>().ToArray();
                    Task.Run(() =>
                    {
                        try
                        {
                            byte[] buf = flipY ? FlipRowsRGB24(data, w, h) : data;

                            byte[] rgbPng = ImageConversion.EncodeArrayToPNG(
                                buf, GraphicsFormat.R8G8B8_UNorm, (uint)w, (uint)h);
                            File.WriteAllBytes(rgbPath, rgbPng);

                            // Keypoint image-space uses top-left origin (v = h-1-sp.y),
                            // so we always draw on a top-down buffer. If we did not flip
                            // above, flip here for drawing then encode.
                            byte[] drawBuf = flipY ? (byte[])buf.Clone() : FlipRowsRGB24(buf, w, h);
                            DrawOverlay(drawBuf, w, h, imagePos, visible, pointR, lineT, drawSkel);
                            if (!flipY) drawBuf = FlipRowsRGB24(drawBuf, w, h);
                            byte[] keyPng = ImageConversion.EncodeArrayToPNG(
                                drawBuf, GraphicsFormat.R8G8B8_UNorm, (uint)w, (uint)h);
                            File.WriteAllBytes(keyPath, keyPng);
                        }
                        catch (Exception e) { Debug.LogError("[KeypointsRecorder] " + e); }
                        finally { Interlocked.Decrement(ref _inFlight); }
                    });
                }
                catch { Interlocked.Decrement(ref _inFlight); }
            });
        }
        finally { _capturing = false; }
    }

    static byte[] FlipRowsRGB24(byte[] src, int w, int h)
    {
        int stride = w * 3;
        byte[] dst = new byte[src.Length];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(src, (h - 1 - y) * stride, dst, y * stride, stride);
        return dst;
    }

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

    string BuildFrameJson(int idx, double relTime, string rgbRel, string keyRel,
        Vector3[] wp, Quaternion[] wr, Vector3[] lp, Vector2[] ip, float[] iz, bool[] vis,
        Vector3 camPos, Quaternion camRot, float fx, float fy, float cx, float cy,
        float near, float far, Matrix4x4 worldToCam, Matrix4x4 proj)
    {
        var sb = new StringBuilder(4096);
        sb.Append("  {");
        sb.Append("\n    \"frame_index\": ").Append(idx).Append(",");
        sb.Append("\n    \"timestamp\": ").Append(F(relTime)).Append(",");
        sb.Append("\n    \"rgb_path\": \"").Append(rgbRel).Append("\",");
        sb.Append("\n    \"keyrgb_path\": \"").Append(keyRel).Append("\",");
        sb.Append("\n    \"camera\": { \"position\": ").Append(V3(camPos))
          .Append(", \"rotation_quat\": ").Append(Q(camRot))
          .Append(", \"fx\": ").Append(F(fx))
          .Append(", \"fy\": ").Append(F(fy))
          .Append(", \"cx\": ").Append(F(cx))
          .Append(", \"cy\": ").Append(F(cy))
          .Append(", \"near\": ").Append(F(near))
          .Append(", \"far\": ").Append(F(far))
          .Append(", \"world_to_camera\": ").Append(M4(worldToCam))
          .Append(", \"projection\": ").Append(M4(proj))
          .Append(" },");
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
        string path = Path.Combine(sessionPath, "keypoints_transforms.json");
        jsonWriter = new StreamWriter(path, false, new UTF8Encoding(false));
        jsonWriter.NewLine = "\n";

        jsonWriter.Write("{\n");
        jsonWriter.Write("  \"dataset\": \"aic\",\n");
        jsonWriter.Write("  \"image_size\": { \"width\": ");
        jsonWriter.Write(cam.pixelWidth.ToString(CultureInfo.InvariantCulture));
        jsonWriter.Write(", \"height\": ");
        jsonWriter.Write(cam.pixelHeight.ToString(CultureInfo.InvariantCulture));
        jsonWriter.Write(" },\n");
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
        // Drain background writers so the last few JSON entries aren't truncated.
        int waitMs = 0;
        while (Interlocked.CompareExchange(ref _inFlight, 0, 0) > 0 && waitMs < 5000)
        {
            Thread.Sleep(20);
            waitMs += 20;
        }
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