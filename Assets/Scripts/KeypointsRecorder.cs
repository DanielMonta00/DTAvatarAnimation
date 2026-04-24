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
// Mapping follows the 17-keypoint COCO layout used by ViTPose
// (see ViTPose/configs/_base_/datasets/coco.py). Attach to the Camera that
// should render the subject and assign the avatar Animator.
[RequireComponent(typeof(Camera))]
public class KeypointsRecorder : MonoBehaviour
{
    public static readonly string[] CocoKeypointNames =
    {
        "nose", "left_eye", "right_eye", "left_ear", "right_ear",
        "left_shoulder", "right_shoulder",
        "left_elbow", "right_elbow",
        "left_wrist", "right_wrist",
        "left_hip", "right_hip",
        "left_knee", "right_knee",
        "left_ankle", "right_ankle",
    };

    // COCO skeleton edges (pairs of keypoint indices) used for overlay drawing.
    static readonly int[,] Skeleton =
    {
        {0,1},{0,2},{1,3},{2,4},
        {5,6},{5,7},{7,9},{6,8},{8,10},
        {5,11},{6,12},{11,12},
        {11,13},{13,15},{12,14},{14,16}
    };

    [Header("Subject")]
    [Tooltip("Humanoid Animator of the avatar whose keypoints are exported.")]
    public Animator avatar;

    [Header("Face keypoint overrides (optional)")]
    [Tooltip("Assign to override the head-based heuristic for that keypoint.")]
    public Transform noseOverride;
    public Transform leftEyeOverride;
    public Transform rightEyeOverride;
    public Transform leftEarOverride;
    public Transform rightEarOverride;

    [Header("Head heuristic offsets (meters, used when overrides are null)")]
    public float noseForwardOffset = 0.11f;
    public float eyeForwardOffset = 0.08f;
    public float eyeSideOffset = 0.035f;
    public float earSideOffset = 0.085f;
    public float earUpOffset = 0.0f;

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

    readonly Transform[] keypointTransforms = new Transform[17];
    // When true, the transform at that slot is the Head bone and we compute
    // the keypoint from a local offset rather than reading Head.position directly.
    readonly bool[] useHeadOffset = new bool[17];

    static readonly Color32[] keypointColors =
    {
        new Color32( 51,153,255,255), // nose
        new Color32( 51,153,255,255), // l eye
        new Color32( 51,153,255,255), // r eye
        new Color32( 51,153,255,255), // l ear
        new Color32( 51,153,255,255), // r ear
        new Color32(  0,255,  0,255), // l shoulder
        new Color32(255,128,  0,255), // r shoulder
        new Color32(  0,255,  0,255), // l elbow
        new Color32(255,128,  0,255), // r elbow
        new Color32(  0,255,  0,255), // l wrist
        new Color32(255,128,  0,255), // r wrist
        new Color32(  0,255,  0,255), // l hip
        new Color32(255,128,  0,255), // r hip
        new Color32(  0,255,  0,255), // l knee
        new Color32(255,128,  0,255), // r knee
        new Color32(  0,255,  0,255), // l ankle
        new Color32(255,128,  0,255), // r ankle
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
        Transform head = avatar.GetBoneTransform(HumanBodyBones.Head);
        Transform lEye = avatar.GetBoneTransform(HumanBodyBones.LeftEye);
        Transform rEye = avatar.GetBoneTransform(HumanBodyBones.RightEye);

        // Face keypoints: prefer explicit override, then dedicated bone (eyes only),
        // then fall back to the Head bone with a positional offset.
        keypointTransforms[0] = noseOverride != null ? noseOverride : head;
        useHeadOffset[0] = (noseOverride == null);

        keypointTransforms[1] = leftEyeOverride != null ? leftEyeOverride : (lEye != null ? lEye : head);
        useHeadOffset[1] = (leftEyeOverride == null && lEye == null);

        keypointTransforms[2] = rightEyeOverride != null ? rightEyeOverride : (rEye != null ? rEye : head);
        useHeadOffset[2] = (rightEyeOverride == null && rEye == null);

        keypointTransforms[3] = leftEarOverride != null ? leftEarOverride : head;
        useHeadOffset[3] = (leftEarOverride == null);

        keypointTransforms[4] = rightEarOverride != null ? rightEarOverride : head;
        useHeadOffset[4] = (rightEarOverride == null);

        keypointTransforms[5]  = avatar.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        keypointTransforms[6]  = avatar.GetBoneTransform(HumanBodyBones.RightUpperArm);
        keypointTransforms[7]  = avatar.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        keypointTransforms[8]  = avatar.GetBoneTransform(HumanBodyBones.RightLowerArm);
        keypointTransforms[9]  = avatar.GetBoneTransform(HumanBodyBones.LeftHand);
        keypointTransforms[10] = avatar.GetBoneTransform(HumanBodyBones.RightHand);
        keypointTransforms[11] = avatar.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        keypointTransforms[12] = avatar.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        keypointTransforms[13] = avatar.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
        keypointTransforms[14] = avatar.GetBoneTransform(HumanBodyBones.RightLowerLeg);
        keypointTransforms[15] = avatar.GetBoneTransform(HumanBodyBones.LeftFoot);
        keypointTransforms[16] = avatar.GetBoneTransform(HumanBodyBones.RightFoot);

        for (int i = 0; i < 17; i++)
            if (keypointTransforms[i] == null)
                Debug.LogWarning($"[KeypointsRecorder] No transform resolved for '{CocoKeypointNames[i]}'.");
    }

    Vector3 GetKeypointWorldPosition(int i)
    {
        Transform t = keypointTransforms[i];
        if (t == null) return Vector3.zero;
        if (!useHeadOffset[i]) return t.position;

        Vector3 fwd = t.forward, right = t.right, up = t.up;
        switch (i)
        {
            case 0: return t.position + fwd * noseForwardOffset;
            case 1: return t.position + fwd * eyeForwardOffset + right * eyeSideOffset;
            case 2: return t.position + fwd * eyeForwardOffset - right * eyeSideOffset;
            case 3: return t.position + right * earSideOffset + up * earUpOffset;
            case 4: return t.position - right * earSideOffset + up * earUpOffset;
            default: return t.position;
        }
    }

    Quaternion GetKeypointWorldRotation(int i)
    {
        Transform t = keypointTransforms[i];
        return t != null ? t.rotation : Quaternion.identity;
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

        Vector3[] worldPos = new Vector3[17];
        Quaternion[] worldRot = new Quaternion[17];
        Vector3[] localPos = new Vector3[17];
        Vector2[] imagePos = new Vector2[17];
        float[] imageDepth = new float[17];
        bool[] visible = new bool[17];

        Transform root = avatar.transform;
        for (int i = 0; i < 17; i++)
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
        for (int i = 0; i < 17; i++)
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
        for (int i = 0; i < 17; i++)
        {
            sb.Append(i == 0 ? "\n      " : ",\n      ");
            sb.Append("{ \"id\": ").Append(i);
            sb.Append(", \"name\": \"").Append(CocoKeypointNames[i]).Append("\"");
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
        jsonWriter.Write("  \"dataset\": \"coco17\",\n");
        jsonWriter.Write("  \"image_size\": { \"width\": ");
        jsonWriter.Write(cam.pixelWidth.ToString(CultureInfo.InvariantCulture));
        jsonWriter.Write(", \"height\": ");
        jsonWriter.Write(cam.pixelHeight.ToString(CultureInfo.InvariantCulture));
        jsonWriter.Write(" },\n");
        jsonWriter.Write("  \"coordinate_convention\": { \"image_origin\": \"top_left\", \"unity_screen_origin\": \"bottom_left\", \"world\": \"unity_left_handed_y_up\" },\n");
        jsonWriter.Write("  \"keypoint_names\": [");
        for (int i = 0; i < CocoKeypointNames.Length; i++)
        {
            if (i > 0) jsonWriter.Write(",");
            jsonWriter.Write("\""); jsonWriter.Write(CocoKeypointNames[i]); jsonWriter.Write("\"");
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
