using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

// Pinhole camera driven directly by PTZCalibration exports (the JSON copies in
// Assets/Calibration):
//   intrinsics  <camera>-<stream>.json   per_zoom[zoom] -> camera_matrix, dist_coeffs, image_size
//   extrinsics  camera_extrinsics.json   cameras.<key>.{rvec, tvec}: X_cam = R * X_world + t,
//                                        OpenCV convention, meters
//
// The extrinsics' world frame is taken to be the MOCAPcenter frame (worldFrame):
// position and rotation of that Transform only, never its scale, so the camera pose
// stays rigid even if the frame object is ever scaled.
// The calibration world is right-handed and Unity is left-handed, so one axis is
// mirrored; the HumanDatasetRecording tools use the same convention (negate x).
//
// Intrinsics become an exact off-axis projection matrix (fx, fy, cx, cy, including
// the non-square pixels of the sub stream), independent of the Game view size.
//
// Lens distortion (dist_coeffs) is rendered in Play mode when modelDistortion is on, the
// same way the dome camera works: the scene is rendered with a plain, overscanned
// projection into a texture, then resampled through the OpenCV lens model
// (LensDistortionRenderer). The result is DistortedTexture, the image the real camera
// would produce. In that mode the Camera itself no longer draws to a display, so the
// distorted image is presented on the camera's Target Display through a screen-space
// canvas (showOnDisplay).
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public class CalibratedCamera : MonoBehaviour
{
    public const string DistortionShaderName = "Calibration/LensDistortionRemap";

    public enum WorldMirror
    {
        NegateX, // Unity x = -calibration x (HumanDatasetRecording's Rokoko convention)
        NegateZ, // Unity z = -calibration z
    }

    [Header("Calibration files (copies in Assets/Calibration)")]
    [Tooltip("<camera>-<stream>.json: camera_matrix / dist_coeffs / image_size per zoom level.")]
    public TextAsset intrinsicsJson;
    [Tooltip("camera_extrinsics.json: cameras.<key>.{rvec,tvec} = world -> camera, OpenCV, meters.")]
    public TextAsset extrinsicsJson;
    [Tooltip("Key under 'cameras' in the extrinsics file (base camera name, no -main/-sub suffix).")]
    public string cameraKey = "cam107";
    [Tooltip("Zoom level whose intrinsics are used (key of per_zoom).")]
    public int zoom = 10;

    [Header("World frame")]
    [Tooltip("Frame the extrinsics are expressed in (MOCAPcenter). Only its position and rotation are used.")]
    public Transform worldFrame;
    [Tooltip("Axis negated to go from the right-handed calibration world to Unity's left-handed one.")]
    public WorldMirror handedness = WorldMirror.NegateX;

    [Header("Lens distortion")]
    [Tooltip("Render the lens distortion from dist_coeffs (Play mode). The camera then renders to a texture instead of the screen.")]
    public bool modelDistortion = true;
    [Tooltip("Remap shader (" + DistortionShaderName + "). Found automatically when empty.")]
    public Shader distortionShader;
    [Tooltip("While the distortion is rendered the Camera draws to a texture, not a display. Show that texture on the " +
             "Camera's Target Display (fit, letterboxed). Cameras sharing a display overlap: give each its own Target Display.")]
    public bool showOnDisplay = true;

    [Header("Clip planes")]
    public float nearClip = 0.1f;
    public float farClip = 1000f;

    [Header("Loaded from JSON (overwritten on reload)")]
    public int imageWidth;
    public int imageHeight;
    public float fx, fy, cx, cy;
    [Tooltip("k1, k2, p1, p2, k3.")]
    public float[] distCoeffs = new float[5];
    [Tooltip("Camera position in the world frame, in Unity handedness.")]
    public Vector3 positionInFrame;

    Camera cam;
    Matrix4x4 plainProjection;   // the calibrated K frustum, image-sized
    Matrix4x4 renderProjection;  // what the Camera renders with: plainProjection, or its overscanned version
    Quaternion rotationInFrame = Quaternion.identity;
    double[] dist = new double[5];
    double usableRadius2;
    LensDistortionRenderer lens;
    Coroutine renderLoop;
    GameObject presenter;
    Canvas presenterCanvas;
    Camera presenterCamera;
    RawImage presenterImage;
    bool loaded;

    public bool IsApplied => loaded;
    // The calibrated K frustum (no overscan), for cameras that render the image directly.
    public Matrix4x4 Projection => plainProjection;
    // True while the lens distortion is being rendered (Play mode, modelDistortion, non-zero coefficients).
    public bool DistortionActive => lens != null;
    public RenderTexture DistortedTexture => lens?.DistortedTexture;

    void OnEnable() { Apply(); }

    void OnDisable()
    {
        DisposeLens();
        if (cam != null) cam.targetTexture = null;
        loaded = false;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (distortionShader == null) distortionShader = Shader.Find(DistortionShaderName);
        // Transform/projection writes aren't allowed from OnValidate directly.
        UnityEditor.EditorApplication.delayCall += () => { if (this != null) Apply(); };
    }
#endif

    // The camera is a calibrated instrument: keep its pose locked to the frame and
    // its projection locked to K even if something else touches fov/aspect/near/far.
    void LateUpdate()
    {
        if (!loaded) return;
        if (cam.projectionMatrix != renderProjection) cam.projectionMatrix = renderProjection;
        ApplyPose();
        if (presenterCanvas != null && presenterCanvas.targetDisplay != cam.targetDisplay)
        {
            presenterCanvas.targetDisplay = cam.targetDisplay;
            presenterCamera.targetDisplay = cam.targetDisplay;
        }
    }

    [ContextMenu("Reload calibration from JSON")]
    public void Apply()
    {
        loaded = false;
        DisposeLens();
        cam = GetComponent<Camera>();
        cam.targetTexture = null;

        if (intrinsicsJson == null || extrinsicsJson == null)
        {
            Debug.LogWarning($"[CalibratedCamera] '{name}': assign intrinsics and extrinsics JSON.", this);
            return;
        }

        double kfx, kfy, kcx, kcy;
        try
        {
            ParseIntrinsics(intrinsicsJson.text, zoom, out int w, out int h,
                            out kfx, out kfy, out kcx, out kcy, out dist);
            ParseExtrinsics(extrinsicsJson.text, cameraKey, out double[] rvec, out double[] tvec);

            imageWidth = w; imageHeight = h;
            fx = (float)kfx; fy = (float)kfy; cx = (float)kcx; cy = (float)kcy;
            distCoeffs = Array.ConvertAll(dist, d => (float)d);
            PoseInFrame(rvec, tvec, handedness, out positionInFrame, out rotationInFrame);
            plainProjection = BuildProjection(kfx, kfy, kcx, kcy, w, h, nearClip, farClip);
        }
        catch (Exception e)
        {
            Debug.LogError($"[CalibratedCamera] '{name}': failed to load calibration: {e.Message}", this);
            return;
        }

        renderProjection = plainProjection;
        int renderW = imageWidth, renderH = imageHeight;

        if (modelDistortion && Application.isPlaying && LensDistortion.HasDistortion(dist))
        {
            Shader shader = distortionShader != null ? distortionShader : Shader.Find(DistortionShaderName);
            if (shader == null)
                Debug.LogWarning($"[CalibratedCamera] '{name}': shader '{DistortionShaderName}' not found; rendering without lens distortion.", this);
            else
            {
                lens = new LensDistortionRenderer(kfx, kfy, kcx, kcy, imageWidth, imageHeight, dist, shader);
                double usable = LensDistortion.UsableRadius(dist);
                usableRadius2 = usable * usable;
                renderW = lens.IdealWidth; renderH = lens.IdealHeight;
                // Overscanned frustum: same focal length and principal point, a bigger image around it.
                renderProjection = BuildProjection(kfx, kfy, kcx + lens.MarginLeft, kcy + lens.MarginTop,
                                                   renderW, renderH, nearClip, farClip);
            }
        }

        cam.usePhysicalProperties = false;
        cam.nearClipPlane = nearClip;
        cam.farClipPlane = farClip;
        // These setters reset the projection, so they go before the override. fieldOfView
        // / aspect only keep Camera's own readers sensible; the matrix is what renders.
        cam.fieldOfView = 2f * Mathf.Atan(renderH / (2f * fy)) * Mathf.Rad2Deg;
        cam.aspect = (float)renderW / renderH;
        cam.projectionMatrix = renderProjection;

        loaded = true;
        ApplyPose();

        if (lens != null)
        {
            EnsureDistortedOutput(imageWidth, imageHeight);
            renderLoop = StartCoroutine(RenderLoop());
        }
    }

    void DisposeLens()
    {
        if (renderLoop != null) { StopCoroutine(renderLoop); renderLoop = null; }
        DestroyPresenter();
        if (lens == null) return;
        if (cam != null) cam.targetTexture = null; // never leave the camera pointing at a released texture
        lens.Dispose();
        lens = null;
    }

    // (Re)creates the output at outW x outH and points the camera at the matching ideal render.
    // MultiViewRecorder calls this with its capture resolution.
    public void EnsureDistortedOutput(int outW, int outH)
    {
        if (lens == null) return;
        if (lens.DistortedTexture == null || lens.DistortedTexture.width != outW || lens.DistortedTexture.height != outH)
        {
            cam.targetTexture = null;
            lens.EnsureOutput(outW, outH);
        }
        cam.targetTexture = lens.IdealTexture;
        RefreshPresenter();
    }

    // Resamples the latest ideal render into DistortedTexture. Runs by itself at the end of
    // every frame; callers that read the texture back can call it first to be certain.
    public void RenderDistorted() { lens?.Render(); }

    IEnumerator RenderLoop()
    {
        while (lens != null)
        {
            yield return new WaitForEndOfFrame();
            lens?.Render();
        }
    }

    // Presents DistortedTexture on the camera's Target Display with a screen-space overlay
    // canvas: it needs no camera, and follows Camera.targetDisplay like a normal camera would.
    void RefreshPresenter()
    {
        if (!showOnDisplay || lens == null || lens.DistortedTexture == null) { DestroyPresenter(); return; }
        if (presenter == null)
        {
            presenter = new GameObject($"{name} display") { hideFlags = HideFlags.HideAndDontSave };
            presenterCanvas = presenter.AddComponent<Canvas>();
            presenterCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            presenterCanvas.sortingOrder = 100 + transform.GetSiblingIndex();

            // Unity's Game view prints "No cameras rendering" for a display unless a Camera targets
            // it, and ours render to textures. This one draws nothing (empty culling mask, black
            // clear, lowest depth); the canvas on top shows the distorted image.
            presenterCamera = presenter.AddComponent<Camera>();
            presenterCamera.clearFlags = CameraClearFlags.SolidColor;
            presenterCamera.backgroundColor = Color.black;
            presenterCamera.cullingMask = 0;
            presenterCamera.depth = -100f;
            presenterCamera.orthographic = true;
            presenterCamera.allowHDR = false;
            presenterCamera.allowMSAA = false;
            presenterCamera.useOcclusionCulling = false;

            var image = new GameObject("Image", typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
            image.transform.SetParent(presenter.transform, false);
            var rt = (RectTransform)image.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            presenterImage = image.AddComponent<RawImage>();
            var fit = image.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fit.aspectRatio = (float)imageWidth / imageHeight;
        }
        presenterCanvas.targetDisplay = cam.targetDisplay;
        presenterCamera.targetDisplay = cam.targetDisplay;
        presenterImage.texture = lens.DistortedTexture;
    }

    void DestroyPresenter()
    {
        if (presenter != null)
        {
            if (Application.isPlaying) Destroy(presenter); else DestroyImmediate(presenter);
        }
        presenter = null; presenterCanvas = null; presenterCamera = null; presenterImage = null;
    }

    void ApplyPose()
    {
        Vector3 p; Quaternion r;
        if (worldFrame != null)
        {
            // Position/rotation only: TransformPoint would apply the frame's scale.
            Quaternion fr = worldFrame.rotation;
            p = worldFrame.position + fr * positionInFrame;
            r = fr * rotationInFrame;
        }
        else { p = positionInFrame; r = rotationInFrame; }

        if (transform.position != p || transform.rotation != r) transform.SetPositionAndRotation(p, r);
    }

    // Intrinsics (pixels) for an output of outW x outH. The calibrated image size maps
    // onto the output, so the sub stream's 704x576 K also works if rendered at 1920x1080.
    public void GetIntrinsics(int outW, int outH, out float ofx, out float ofy, out float ocx, out float ocy)
    {
        float sx = (float)outW / imageWidth, sy = (float)outH / imageHeight;
        ofx = fx * sx;
        ofy = fy * sy;
        // OpenCV puts pixel centres on integers, so scale about the pixel edges.
        ocx = (cx + 0.5f) * sx - 0.5f;
        ocy = (cy + 0.5f) * sy - 0.5f;
    }

    // Where a world point lands in the image this camera produces, as a viewport position
    // (0..1, origin bottom-left, independent of resolution) plus depth along the view axis.
    // It goes through the calibrated K and, while the distortion is rendered, the lens model,
    // so labels match the distorted image. z <= 0 means the point cannot be imaged: behind
    // the camera, or beyond the radius where the distortion model is valid.
    public Vector3 WorldToViewport(Vector3 world)
    {
        Vector4 clip = plainProjection * (cam.worldToCameraMatrix * new Vector4(world.x, world.y, world.z, 1f));
        if (clip.w <= 1e-6f) return new Vector3(0f, 0f, -1f);
        float vx = clip.x / clip.w * 0.5f + 0.5f;
        float vy = clip.y / clip.w * 0.5f + 0.5f;

        if (lens != null)
        {
            double xu = ((vx * imageWidth - 0.5) - cx) / fx;
            double yu = (((1f - vy) * imageHeight - 0.5) - cy) / fy;
            if (xu * xu + yu * yu > usableRadius2) return new Vector3(0f, 0f, -1f);
            LensDistortion.Distort(xu, yu, dist, out double xd, out double yd);
            vx = (float)((fx * xd + cx + 0.5) / imageWidth);
            vy = (float)(1.0 - (fy * yd + cy + 0.5) / imageHeight);
        }
        return new Vector3(vx, vy, clip.w);
    }

    // ---------------- JSON ----------------

    static void ParseIntrinsics(string json, int zoom, out int w, out int h,
                                out double fx, out double fy, out double cx, out double cy, out double[] dist)
    {
        var root = (Dictionary<string, object>)MiniJson.Parse(json);
        var perZoom = (Dictionary<string, object>)root["per_zoom"];
        string key = zoom.ToString(CultureInfo.InvariantCulture);
        if (!perZoom.TryGetValue(key, out object entries))
            throw new Exception($"no per_zoom['{key}'] (available: {string.Join(", ", perZoom.Keys)})");

        // Prefer the entry flagged active; otherwise the first one.
        Dictionary<string, object> chosen = null;
        foreach (object o in (List<object>)entries)
        {
            var e = (Dictionary<string, object>)o;
            if (chosen == null) chosen = e;
            if (e.TryGetValue("active", out object a) && a is bool b && b) { chosen = e; break; }
        }
        if (chosen == null) throw new Exception($"per_zoom['{key}'] is empty");

        var k = (List<object>)chosen["camera_matrix"];
        double K(int r, int c) => (double)((List<object>)k[r])[c];
        fx = K(0, 0); fy = K(1, 1); cx = K(0, 2); cy = K(1, 2);

        var size = (List<object>)chosen["image_size"];
        w = (int)(double)size[0];
        h = (int)(double)size[1];

        var d = (List<object>)chosen["dist_coeffs"];
        dist = new double[d.Count];
        for (int i = 0; i < dist.Length; i++) dist[i] = (double)d[i];
    }

    static void ParseExtrinsics(string json, string key, out double[] rvec, out double[] tvec)
    {
        var root = (Dictionary<string, object>)MiniJson.Parse(json);
        var cams = (Dictionary<string, object>)root["cameras"];
        if (!cams.TryGetValue(key, out object o))
            throw new Exception($"no cameras['{key}'] (available: {string.Join(", ", cams.Keys)})");
        var c = (Dictionary<string, object>)o;
        if (c.TryGetValue("calibrated", out object cal) && cal is bool b && !b)
            Debug.LogWarning($"[CalibratedCamera] extrinsics for '{key}' are marked calibrated:false (placeholder pose).");
        rvec = ToVec3((List<object>)c["rvec"]);
        tvec = ToVec3((List<object>)c["tvec"]);
    }

    static double[] ToVec3(List<object> l) => new[] { (double)l[0], (double)l[1], (double)l[2] };

    // ---------------- Math ----------------

    // Camera pose in the world frame, in Unity handedness, from OpenCV rvec/tvec
    // (X_cam = R * X_world + t). The camera centre is C = -R^T t and the camera's
    // right / down / forward axes are the rows of R.
    public static void PoseInFrame(double[] rvec, double[] tvec, WorldMirror mirror,
                                   out Vector3 position, out Quaternion rotation)
    {
        double[,] R = Rodrigues(rvec);
        double c0 = -(R[0, 0] * tvec[0] + R[1, 0] * tvec[1] + R[2, 0] * tvec[2]);
        double c1 = -(R[0, 1] * tvec[0] + R[1, 1] * tvec[1] + R[2, 1] * tvec[2]);
        double c2 = -(R[0, 2] * tvec[0] + R[1, 2] * tvec[1] + R[2, 2] * tvec[2]);

        Vector3 Mirror(double x, double y, double z) => mirror == WorldMirror.NegateX
            ? new Vector3((float)-x, (float)y, (float)z)
            : new Vector3((float)x, (float)y, (float)-z);

        position = Mirror(c0, c1, c2);
        Vector3 forward = Mirror(R[2, 0], R[2, 1], R[2, 2]);
        Vector3 up = -Mirror(R[1, 0], R[1, 1], R[1, 2]); // OpenCV y points down
        rotation = Quaternion.LookRotation(forward, up);
    }

    static double[,] Rodrigues(double[] r)
    {
        double th = Math.Sqrt(r[0] * r[0] + r[1] * r[1] + r[2] * r[2]);
        if (th < 1e-12) return new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        double kx = r[0] / th, ky = r[1] / th, kz = r[2] / th;
        double c = Math.Cos(th), s = Math.Sin(th), v = 1.0 - c;
        return new double[,]
        {
            { c + kx * kx * v,      kx * ky * v - kz * s, kx * kz * v + ky * s },
            { ky * kx * v + kz * s, c + ky * ky * v,      ky * kz * v - kx * s },
            { kz * kx * v - ky * s, kz * ky * v + kx * s, c + kz * kz * v      },
        };
    }

    // OpenGL-style projection (what Camera.projectionMatrix expects: camera looks down
    // -z, y up) equivalent to the OpenCV pinhole K for a w x h image with the origin at
    // the top-left. The +0.5 moves OpenCV's pixel-centre-at-integer convention to the
    // continuous pixel-edge convention of the render target.
    public static Matrix4x4 BuildProjection(double fx, double fy, double cx, double cy,
                                            int w, int h, float near, float far)
    {
        var m = Matrix4x4.zero;
        m[0, 0] = (float)(2.0 * fx / w);
        m[0, 2] = (float)(1.0 - 2.0 * (cx + 0.5) / w);
        m[1, 1] = (float)(2.0 * fy / h);
        m[1, 2] = (float)(2.0 * (cy + 0.5) / h - 1.0);
        m[2, 2] = -(far + near) / (far - near);
        m[2, 3] = -2f * far * near / (far - near);
        m[3, 2] = -1f;
        return m;
    }
}
