using UnityEngine;
using UnityEngine.Experimental.Rendering; // GraphicsFormat
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// Writes rgb/{timestamp}.png for each captured frame. Camera-agnostic: the
// image source is chosen by RecordingSession based on what's attached to
// this GameObject.
//   - If an Avante.FulldomeCamera is present: the dome (domemasterFbo) is
//     the source, and `showPreview` draws it to the screen.
//   - Otherwise: the Camera component is rendered into a local
//     RenderTexture by RecordingSession.
// Toggle `recordRgb` to start/stop writing without affecting any other
// subscribers (e.g. KeypointsRecorder).
[DefaultExecutionOrder(1000)]
[RequireComponent(typeof(Camera))]
[RequireComponent(typeof(RecordingSession))]
public class RgbImageRecorder : MonoBehaviour, RecordingSession.IFrameSubscriber
{
    [Header("Recording")]
    public bool recordRgb = false;
    [Tooltip("Max PNG encodes queued on background threads. Drops frames past this.")]
    public int maxInFlightEncodes = 4;

    [Header("Preview")]
    [Tooltip("Draw the captured frame over the screen while recording. Useful because URP/HDRP blanks the Game view when RecordingSession binds the Camera's targetTexture for a regular camera.")]
    public bool showPreview = true;

    RecordingSession session;
    Avante.FulldomeCamera domeCam;
    string rgbDir;
    bool acquired;
    int _inFlight;

    public bool WantsFrame => recordRgb && acquired;
    public bool IsAtCapacity =>
        Interlocked.CompareExchange(ref _inFlight, 0, 0) >= maxInFlightEncodes;

    void Awake()
    {
        session = GetComponent<RecordingSession>();
        domeCam = GetComponent<Avante.FulldomeCamera>();
    }

    void OnEnable()
    {
        session.Register(this);
        if (recordRgb) BeginRecording();
    }

    void OnDisable()
    {
        if (acquired) EndRecording();
        session.Unregister(this);
    }

    void Update()
    {
        if (!acquired && recordRgb) BeginRecording();
        else if (acquired && !recordRgb) EndRecording();
    }

    void BeginRecording()
    {
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
    public void StartRecording() { recordRgb = true; BeginRecording(); }
    [ContextMenu("Stop Recording")]
    public void StopRecordingFromMenu() { recordRgb = false; EndRecording(); }

    // --- IFrameSubscriber ---

    public void OnSessionBegin(string sessionPath)
    {
        rgbDir = Path.Combine(sessionPath, "rgb");
        Directory.CreateDirectory(rgbDir);
    }

    public void OnFrameGather(int frameIndex, double timestamp, string timestampString, int width, int height) { }

    public void OnFrameDispatch(int frameIndex, double timestamp, string timestampString,
                                byte[] rgbTopDown, int width, int height)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 0, 0) >= maxInFlightEncodes) return;
        string path = Path.Combine(rgbDir, timestampString + ".png");
        int w = width, h = height;
        byte[] buf = rgbTopDown; // shared, treat read-only
        Interlocked.Increment(ref _inFlight);
        Task.Run(() =>
        {
            try
            {
                var png = ImageConversion.EncodeArrayToPNG(
                    buf, GraphicsFormat.R8G8B8_UNorm, (uint)w, (uint)h);
                File.WriteAllBytes(path, png);
            }
            catch (Exception e) { Debug.LogError("[RgbImageRecorder] " + e); }
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
    }

    void OnGUI()
    {
        if (!showPreview) return;
        Texture tex = null;
        if (domeCam != null && domeCam.domemasterFbo != null) tex = domeCam.domemasterFbo;
        else if (session != null && session.isActiveAndEnabled) tex = session.GetPreviewTexture();
        if (tex == null) return;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), tex, ScaleMode.ScaleToFit, false);
    }
}