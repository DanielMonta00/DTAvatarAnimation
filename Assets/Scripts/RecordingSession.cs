using UnityEngine;
using UnityEngine.Rendering;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

// Shared capture/session coordinator.
//
// Attach to the same Camera GameObject as RgbImageRecorder and/or
// KeypointsRecorder. Owns the session folder, the per-frame RGB readback,
// and dispatch to subscribed recorders so that rgb/ and keyrgb/ are
// bit-identical. Works for both fulldome cameras (reads domemasterFbo) and
// regular cameras (renders the Camera into a local RT).
[RequireComponent(typeof(Camera))]
[DefaultExecutionOrder(900)] // before RgbImageRecorder's 1000
public class RecordingSession : MonoBehaviour
{
    public interface IFrameSubscriber
    {
        // True while this subscriber wants the session to capture frames.
        // The session captures if *any* subscriber returns true.
        bool WantsFrame { get; }

        // Called once on the main thread when a new session starts.
        // Subscribers should create their own subfolders / files here.
        void OnSessionBegin(string sessionPath);

        // Called on the main thread before the GPU readback, with the
        // frame index / timestamp and the image dimensions the readback
        // will carry. Subscribers can gather any per-frame state
        // (e.g. keypoint transforms projected to image pixels).
        void OnFrameGather(int frameIndex, double timestamp, string timestampString,
                           int width, int height);

        // Called on the main thread from inside the AsyncGPUReadback
        // callback. `rgbTopDown` is in RGB24 top-down rows, ready for PNG.
        // It is shared with other subscribers — treat as read-only. If you
        // need to modify, Clone() it first. Kick off your Task.Run encode
        // here; do not block.
        void OnFrameDispatch(int frameIndex, double timestamp, string timestampString,
                             byte[] rgbTopDown, int width, int height);

        // Called on the main thread when the session ends. Subscribers
        // should flush any pending per-session state (e.g. close JSON).
        void OnSessionEnd();
    }

    [Header("Session")]
    public string outputFolder = "Recordings";
    public string sessionName = "Session";

    [Header("Capture")]
    [Tooltip("Max AsyncGPUReadbacks in flight. Drops frames past this.")]
    public int maxInFlightReadbacks = 4;
    [Tooltip("If true, this component owns Time.captureFramerate.")]
    public bool setCaptureFramerate = false;
    public int frameRate = 30;
    [Tooltip("Flip rows so saved PNGs follow top-down image convention.")]
    public bool flipOutputY = true;

    public string sessionPath { get; private set; }

    Camera cam;
    Avante.FulldomeCamera domeCam;
    RenderTexture fallbackRT;        // used when no dome fbo is available
    RenderTexture prevCamTargetTex;  // camera's original targetTexture (restored on Release)
    bool camRetargeted;              // true while we own cam.targetTexture

    readonly List<IFrameSubscriber> subs = new List<IFrameSubscriber>();
    int refCount = 0;
    int frameCounter = 0;
    double recordingStartTime = -1.0;
    int lastCapturedUnityFrame = -1;
    bool _capturing = false;
    int _inFlight = 0;

    void Awake()
    {
        cam = GetComponent<Camera>();
        domeCam = GetComponent<Avante.FulldomeCamera>();
        EnsureDomemasterFbo();
    }

    // When a FulldomeCamera is present the dome pipeline needs an
    // externally-provided RenderTexture as `domemasterFbo` to Blit into.
    // We own that lifecycle here so any subscriber can rely on the RT
    // being ready during capture.
    void EnsureDomemasterFbo()
    {
        if (domeCam == null) return;
        if (domeCam.domemasterFbo == null)
        {
            int size = (int)domeCam.domemasterResolution;
            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.filterMode = FilterMode.Bilinear;
            rt.Create();
            domeCam.domemasterFbo = rt;
        }
        else if (!domeCam.domemasterFbo.IsCreated())
        {
            domeCam.domemasterFbo.Create();
        }
    }

    public void Register(IFrameSubscriber s)
    {
        if (!subs.Contains(s)) subs.Add(s);
        if (refCount > 0) s.OnSessionBegin(sessionPath);
    }

    public void Unregister(IFrameSubscriber s) { subs.Remove(s); }

    public bool IsActive => refCount > 0;

    // Returns the current capture RT (for preview UI) when recording a regular
    // camera. Null for dome cameras or when not recording.
    public RenderTexture GetPreviewTexture() => camRetargeted ? fallbackRT : null;

    public void Acquire()
    {
        if (refCount == 0)
        {
            sessionPath = Path.Combine(Application.dataPath, "..", outputFolder,
                sessionName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(sessionPath);
            frameCounter = 0;
            recordingStartTime = -1.0;
            lastCapturedUnityFrame = -1;
            if (setCaptureFramerate) Time.captureFramerate = frameRate;

            // On non-dome cameras, bind the camera's target to our RT so
            // URP/HDRP renders into it on each frame. This avoids calling
            // cam.Render() from WaitForEndOfFrame — which stalls URP's
            // simulation loop (observed as the scene freezing on frame 0).
            if (domeCam == null && cam != null)
            {
                int w = Mathf.Max(16, cam.pixelWidth);
                int h = Mathf.Max(16, cam.pixelHeight);
                if (fallbackRT == null || fallbackRT.width != w || fallbackRT.height != h)
                {
                    if (fallbackRT != null) { fallbackRT.Release(); Destroy(fallbackRT); }
                    fallbackRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                    fallbackRT.Create();
                }
                prevCamTargetTex = cam.targetTexture;
                cam.targetTexture = fallbackRT;
                camRetargeted = true;
            }

            for (int i = 0; i < subs.Count; i++) subs[i].OnSessionBegin(sessionPath);
            Debug.Log($"[RecordingSession] Session started at {sessionPath}");
        }
        refCount++;
    }

    public void Release()
    {
        if (refCount <= 0) return;
        refCount--;
        if (refCount > 0) return;

        // Drain in-flight readbacks so subscribers' OnFrameDispatch have all fired.
        int waited = 0;
        while (Interlocked.CompareExchange(ref _inFlight, 0, 0) > 0 && waited < 5000)
        {
            Thread.Sleep(20);
            waited += 20;
        }

        for (int i = 0; i < subs.Count; i++)
        {
            try { subs[i].OnSessionEnd(); }
            catch (Exception e) { Debug.LogError("[RecordingSession] OnSessionEnd: " + e); }
        }

        if (camRetargeted && cam != null)
        {
            cam.targetTexture = prevCamTargetTex;
            camRetargeted = false;
            prevCamTargetTex = null;
        }
        if (fallbackRT != null) { fallbackRT.Release(); Destroy(fallbackRT); fallbackRT = null; }
        if (setCaptureFramerate) Time.captureFramerate = 0;
        Debug.Log($"[RecordingSession] Session ended at {sessionPath}");
        sessionPath = null;
    }

    void OnApplicationQuit() { while (refCount > 0) Release(); }
    void OnDisable() { while (refCount > 0) Release(); }

    bool AnySubscriberWantsFrame()
    {
        for (int i = 0; i < subs.Count; i++)
            if (subs[i].WantsFrame) return true;
        return false;
    }

    void Update()
    {
        if (domeCam != null) EnsureDomemasterFbo();
        if (refCount == 0) return;
        if (_capturing) return;
        if (!AnySubscriberWantsFrame()) return;
        if (Time.frameCount == lastCapturedUnityFrame) return;
        StartCoroutine(CaptureRoutine());
    }

    IEnumerator CaptureRoutine()
    {
        _capturing = true;
        yield return new WaitForEndOfFrame();
        try
        {
            if (refCount == 0) yield break;
            if (!AnySubscriberWantsFrame()) yield break;
            if (Interlocked.CompareExchange(ref _inFlight, 0, 0) >= maxInFlightReadbacks) yield break;

            // Source selection: dome fbo if available, else render Camera into our RT.
            RenderTexture src = null;
            int w = 0, h = 0;

            if (domeCam != null && domeCam.domemasterFbo != null && domeCam.domemasterFbo.IsCreated())
            {
                src = domeCam.domemasterFbo;
                w = src.width; h = src.height;
            }
            else if (fallbackRT != null && fallbackRT.IsCreated())
            {
                // URP/HDRP has already rendered this frame into fallbackRT
                // because we own cam.targetTexture for the whole session.
                src = fallbackRT;
                w = fallbackRT.width; h = fallbackRT.height;
            }
            else yield break;

            if (recordingStartTime < 0.0) recordingStartTime = Time.realtimeSinceStartup;
            int idx = frameCounter++;
            double relTime = Time.realtimeSinceStartup - recordingStartTime;
            string ts = relTime.ToString("F6", CultureInfo.InvariantCulture);
            lastCapturedUnityFrame = Time.frameCount;

            for (int i = 0; i < subs.Count; i++)
            {
                if (!subs[i].WantsFrame) continue;
                try { subs[i].OnFrameGather(idx, relTime, ts, w, h); }
                catch (Exception e) { Debug.LogError("[RecordingSession] OnFrameGather: " + e); }
            }

            bool flip = flipOutputY;
            Interlocked.Increment(ref _inFlight);
            AsyncGPUReadback.Request(src, 0, TextureFormat.RGB24, req =>
            {
                try
                {
                    if (req.hasError) return;
                    byte[] bytes = req.GetData<byte>().ToArray();
                    byte[] dispatch = flip ? FlipRowsRGB24(bytes, w, h) : bytes;
                    for (int i = 0; i < subs.Count; i++)
                    {
                        if (!subs[i].WantsFrame) continue;
                        try { subs[i].OnFrameDispatch(idx, relTime, ts, dispatch, w, h); }
                        catch (Exception e) { Debug.LogError("[RecordingSession] OnFrameDispatch: " + e); }
                    }
                }
                finally { Interlocked.Decrement(ref _inFlight); }
            });
        }
        finally { _capturing = false; }
    }

    public static byte[] FlipRowsRGB24(byte[] src, int w, int h)
    {
        int stride = w * 3;
        byte[] dst = new byte[src.Length];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(src, (h - 1 - y) * stride, dst, y * stride, stride);
        return dst;
    }
}
