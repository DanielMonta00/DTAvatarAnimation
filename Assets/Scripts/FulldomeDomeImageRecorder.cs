using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering; // GraphicsFormat
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;


[DefaultExecutionOrder(1000)]
[RequireComponent(typeof(Avante.FulldomeCamera))]
public class FulldomeDomeImageRecorder : MonoBehaviour
{
    [Header("Recording Settings")]
    public bool recordDomeImage = false;
    public int frameRate = 30;
    public string outputFolder = "Recordings";
    public string sessionName = "DomeImageSession";
    [Tooltip("Max PNG encodes queued on background threads. Drops frames past this.")]
    public int maxInFlightEncodes = 4;

    [Header("Preview")]
    public bool showPreview = true;

    private Avante.FulldomeCamera domeCam;
    private int frameCount = 0;
    private double recordingStartTime = -1.0;
    private string sessionPath;
    private bool isRecording = false;
    private int lastWidth = -1;
    private int lastHeight = -1;
    private bool _capturing = false;
    private int _inFlight = 0;
    private CancellationTokenSource _cts;

    void Awake() { domeCam = GetComponent<Avante.FulldomeCamera>(); }

    void Start()
    {
        if (recordDomeImage) InitializeRecording();
    }

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

    void InitializeRecording()
    {
        if (domeCam == null) domeCam = GetComponent<Avante.FulldomeCamera>();
        EnsureDomemasterFbo();

        sessionPath = Path.Combine(Application.dataPath, "..", outputFolder,
            sessionName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(sessionPath);
        Directory.CreateDirectory(Path.Combine(sessionPath, "dome"));

        lastWidth  = domeCam.domemasterFbo.width;
        lastHeight = domeCam.domemasterFbo.height;

        // Only this script sets captureFramerate; remove if something else does.
        Time.captureFramerate = frameRate;
        recordingStartTime = -1.0;
        frameCount = 0;
        _cts = new CancellationTokenSource();
        isRecording = true;
        Debug.Log($"Dome image recording started at: {sessionPath}");
    }

    void Update()
    {
        if (!_capturing && isRecording && recordDomeImage)
            StartCoroutine(CaptureFrameCoroutine());
    }

    IEnumerator CaptureFrameCoroutine()
    {
        _capturing = true;
        yield return new WaitForEndOfFrame();
        try
        {
            if (domeCam == null) yield break;
            EnsureDomemasterFbo();
            var rt = domeCam.domemasterFbo;
            if (rt == null || !rt.IsCreated()) yield break;

            int w = rt.width, h = rt.height;
            if (w <= 8 || h <= 8) yield break;

            // Back-pressure: drop this frame if too many encodes are queued.
            if (Interlocked.CompareExchange(ref _inFlight, 0, 0) >= maxInFlightEncodes)
                yield break;

            if (recordingStartTime < 0.0) recordingStartTime = Time.realtimeSinceStartup;
            double relativeTime = Time.realtimeSinceStartup - recordingStartTime;
            string ts = relativeTime.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            string framePath = Path.Combine(sessionPath, "dome", $"{ts}.png");

            Interlocked.Increment(ref _inFlight);
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGB24, req =>
            {
                try
                {
                    if (req.hasError) return;
                    var data = req.GetData<byte>().ToArray();
                    // Encode and write on a background thread; main thread stays smooth.
                    Task.Run(() =>
                    {
                        try
                        {
                            var png = ImageConversion.EncodeArrayToPNG(
                                data, GraphicsFormat.R8G8B8_UNorm, (uint)w, (uint)h);
                            File.WriteAllBytes(framePath, png);
                        }
                        catch (Exception e) { Debug.LogError("[DomeRecorder] " + e); }
                        finally { Interlocked.Decrement(ref _inFlight); }
                    });
                }
                catch { Interlocked.Decrement(ref _inFlight); }
            });
            frameCount++;
        }
        finally { _capturing = false; }
    }

    // Safer than Graphics.Blit(rt, null) at WaitForEndOfFrame — works on BiRP and URP.
    void OnGUI()
    {
        if (!showPreview || domeCam == null || domeCam.domemasterFbo == null) return;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), domeCam.domemasterFbo,
                        ScaleMode.ScaleToFit, false);
    }

    void OnApplicationQuit() { if (isRecording) StopRecording(); }
    void OnDisable() { if (isRecording) StopRecording(); }

    public void StopRecording()
    {
        if (!isRecording) return;
        isRecording = false;
        _cts?.Cancel();
        Debug.Log($"Dome image recording stopped. {frameCount} frames queued, path: {sessionPath}");
    }

    [ContextMenu("Start Recording")]
    public void StartRecording() { recordDomeImage = true; InitializeRecording(); }
    [ContextMenu("Stop Recording")]
    public void StopRecordingFromMenu() => StopRecording();
}
