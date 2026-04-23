using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering; // GraphicsFormat
using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

[RequireComponent(typeof(Camera))]
public class VideoFlowCameraRecorder : MonoBehaviour
{
    [Header("Recording Settings")]
    public bool recordVideo = false;
    public int frameRate = 30;
    public string outputFolder = "Recordings";
    public string sessionName = "VideoFlowSession";
    [Tooltip("Max PNG encodes queued on background threads. Drops frames past this.")]
    public int maxInFlightEncodes = 4;

    private Camera cam;
    private int frameCount = 0;
    private double recordingStartTime = -1.0;
    private string sessionPath;
    private bool isRecording = false;
    private RenderTexture renderTexture;
    private int lastWidth = -1;
    private int lastHeight = -1;
    private bool _capturing = false;
    private int _inFlight = 0;

    void Awake() { cam = GetComponent<Camera>(); }

    void Start()
    {
        if (recordVideo) InitializeRecording();
    }

    void EnsureRenderTexture(int w, int h)
    {
        if (renderTexture != null && renderTexture.width == w && renderTexture.height == h) return;
        if (renderTexture != null) { renderTexture.Release(); Destroy(renderTexture); }
        renderTexture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        renderTexture.Create();
        lastWidth = w;
        lastHeight = h;
    }

    void InitializeRecording()
    {
        if (cam == null) cam = GetComponent<Camera>();
        sessionPath = Path.Combine(Application.dataPath, "..", outputFolder,
            sessionName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(sessionPath);
        Directory.CreateDirectory(Path.Combine(sessionPath, "rgb"));

        EnsureRenderTexture(Mathf.Max(16, cam.pixelWidth), Mathf.Max(16, cam.pixelHeight));

        // DO NOT set Time.captureFramerate here if another recorder already owns it.
        // Time.captureFramerate = frameRate;

        recordingStartTime = -1.0;
        frameCount = 0;
        isRecording = true;
        Debug.Log($"VideoFlow recording started at: {sessionPath}");
    }

    void Update()
    {
        if (!_capturing && isRecording && recordVideo)
            StartCoroutine(CaptureFrameCoroutine());
    }

    IEnumerator CaptureFrameCoroutine()
    {
        _capturing = true;
        yield return new WaitForEndOfFrame();
        try
        {
            int w = Mathf.Max(16, cam.pixelWidth);
            int h = Mathf.Max(16, cam.pixelHeight);
            EnsureRenderTexture(w, h);
            if (Interlocked.CompareExchange(ref _inFlight, 0, 0) >= maxInFlightEncodes) yield break;

            // One extra render into our RT. Don't leave targetTexture assigned
            // or you'll double-render every frame.
            var prevTarget = cam.targetTexture;
            cam.targetTexture = renderTexture;
            cam.Render();
            cam.targetTexture = prevTarget;

            if (recordingStartTime < 0.0) recordingStartTime = Time.realtimeSinceStartup;
            double relativeTime = Time.realtimeSinceStartup - recordingStartTime;
            string ts = relativeTime.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            string framePath = Path.Combine(sessionPath, "rgb", $"{ts}.png");

            Interlocked.Increment(ref _inFlight);
            AsyncGPUReadback.Request(renderTexture, 0, TextureFormat.RGB24, req =>
            {
                try
                {
                    if (req.hasError) return;
                    var data = req.GetData<byte>().ToArray();
                    Task.Run(() =>
                    {
                        try
                        {
                            var png = ImageConversion.EncodeArrayToPNG(
                                data, GraphicsFormat.R8G8B8_UNorm, (uint)w, (uint)h);
                            File.WriteAllBytes(framePath, png);
                        }
                        catch (Exception e) { Debug.LogError("[VideoFlow] " + e); }
                        finally { Interlocked.Decrement(ref _inFlight); }
                    });
                }
                catch { Interlocked.Decrement(ref _inFlight); }
            });
            frameCount++;
        }
        finally { _capturing = false; }
    }

    void OnApplicationQuit() { if (isRecording) StopRecording(); }
    void OnDisable() { if (isRecording) StopRecording(); }

    public void StopRecording()
    {
        if (!isRecording) return;
        isRecording = false;
        if (renderTexture != null) { renderTexture.Release(); Destroy(renderTexture); renderTexture = null; }
        Debug.Log($"VideoFlow recording stopped. Path: {sessionPath}");
    }

    [ContextMenu("Start Recording")]
    public void StartRecording() { recordVideo = true; InitializeRecording(); }
    [ContextMenu("Stop Recording")]
    public void StopRecordingFromMenu() { StopRecording(); }
}
