using UnityEngine;

// Shared enums + helper for the recorder family. Kept tiny and self-contained
// so RgbImageRecorder, KeypointsRecorder, RecordingSession, and
// MultiViewRecorder can all reference the same options.

public enum CaptureImageFormat
{
    PNG,
    JPEG,
}

public enum CaptureResolution
{
    Native,             // use whatever the camera reports (cam.pixelWidth/Height)
    FullHD_1920x1080,
    HD_1280x720,
    qHD_960x540,
    nHD_640x360,
    VGA_640x480,
    Square_512x512,
    Square_416x416,     // common YOLO input size
    Custom,             // use customWidth / customHeight
}

public static class CaptureSettings
{
    // Resolves a CaptureResolution value to a (width, height) pair.
    // `nativeW/nativeH` are the camera's reported pixel dimensions; used for
    // CaptureResolution.Native. `customW/customH` are used for
    // CaptureResolution.Custom. Result is clamped to a 16-pixel minimum.
    public static void Resolve(CaptureResolution r, int nativeW, int nativeH,
                               int customW, int customH, out int w, out int h)
    {
        switch (r)
        {
            case CaptureResolution.FullHD_1920x1080: w = 1920; h = 1080; break;
            case CaptureResolution.HD_1280x720:      w = 1280; h = 720;  break;
            case CaptureResolution.qHD_960x540:      w = 960;  h = 540;  break;
            case CaptureResolution.nHD_640x360:      w = 640;  h = 360;  break;
            case CaptureResolution.VGA_640x480:      w = 640;  h = 480;  break;
            case CaptureResolution.Square_512x512:   w = 512;  h = 512;  break;
            case CaptureResolution.Square_416x416:   w = 416;  h = 416;  break;
            case CaptureResolution.Custom:           w = customW; h = customH; break;
            case CaptureResolution.Native:
            default:                                 w = nativeW; h = nativeH; break;
        }
        w = Mathf.Max(16, w);
        h = Mathf.Max(16, h);
    }

    public static string Extension(CaptureImageFormat fmt) => fmt == CaptureImageFormat.JPEG ? ".jpg" : ".png";
}
