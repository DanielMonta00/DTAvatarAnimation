using System;
using UnityEngine;

// Turns an ideal pinhole render into the lens-distorted image of a calibrated camera.
//
// Same idea as the dome camera: the scene is rendered with a plain projection, then a
// Graphics.Blit through a custom shader resamples it. Here the shader reads a remap grid
// instead of computing the lens per pixel: for every node of a coarse grid over the
// OUTPUT image, the grid holds where to read in the ideal render (the OpenCV model
// inverted on the CPU), and the GPU interpolates between nodes.
//
// A barrel-distorted image sees farther out than the ideal image of the same K, so the
// ideal render is overscanned (Margin*); the camera must render with
// IdealWidth x IdealHeight and a principal point shifted by MarginLeft / MarginTop.
public sealed class LensDistortionRenderer : IDisposable
{
    const int GridStep = 4; // calibrated pixels between remap-grid nodes (~0.03 px interpolation error)

    readonly int imageW, imageH;
    readonly Material material;
    Texture2D lut;

    public int MarginLeft { get; private set; }
    public int MarginRight { get; private set; }
    public int MarginTop { get; private set; }
    public int MarginBottom { get; private set; }
    public int IdealWidth => imageW + MarginLeft + MarginRight;
    public int IdealHeight => imageH + MarginTop + MarginBottom;

    // The camera renders into IdealTexture; Render() fills DistortedTexture.
    public RenderTexture IdealTexture { get; private set; }
    public RenderTexture DistortedTexture { get; private set; }

    public LensDistortionRenderer(double fx, double fy, double cx, double cy, int w, int h, double[] dist, Shader shader)
    {
        imageW = w; imageH = h;
        double usable = LensDistortion.UsableRadius(dist);

        int gw = (w + GridStep - 1) / GridStep + 1;
        int gh = (h + GridStep - 1) / GridStep + 1;
        var ideal = new Vector2[gw * gh]; // ideal-image pixel (calibrated image coordinates, continuous)
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        for (int j = 0; j < gh; j++)
        {
            // Row j runs bottom -> top (texture row 0 is the bottom), like the output uv.
            double vCv = (1.0 - j / (double)(gh - 1)) * h - 0.5; // OpenCV: pixel centres on integers
            for (int i = 0; i < gw; i++)
            {
                double uCv = i / (double)(gw - 1) * w - 0.5;
                LensDistortion.Undistort((uCv - cx) / fx, (vCv - cy) / fy, dist, usable, out double xu, out double yu);
                double ui = fx * xu + cx + 0.5, vi = fy * yu + cy + 0.5;
                ideal[j * gw + i] = new Vector2((float)ui, (float)vi);
                if (ui < minU) minU = ui; if (ui > maxU) maxU = ui;
                if (vi < minV) minV = vi; if (vi > maxV) maxV = vi;
            }
        }

        MarginLeft = Math.Max(0, (int)Math.Ceiling(-minU)) + 2;
        MarginRight = Math.Max(0, (int)Math.Ceiling(maxU - w)) + 2;
        MarginTop = Math.Max(0, (int)Math.Ceiling(-minV)) + 2;
        MarginBottom = Math.Max(0, (int)Math.Ceiling(maxV - h)) + 2;

        // Grid values: uv inside the ideal render (v up, like any Unity texture).
        float iw = IdealWidth, ih = IdealHeight;
        var uv = new Vector2[gw * gh];
        for (int n = 0; n < uv.Length; n++)
            uv[n] = new Vector2((ideal[n].x + MarginLeft) / iw, 1f - (ideal[n].y + MarginTop) / ih);

        lut = new Texture2D(gw, gh, TextureFormat.RGFloat, false, true)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
        lut.SetPixelData(uv, 0);
        lut.Apply(false, true);

        material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        material.SetTexture("_Lut", lut);
        // Output uv 0 / 1 must land on the first / last node centre of the grid.
        material.SetVector("_LutMap", new Vector4((gw - 1f) / gw, (gh - 1f) / gh, 0.5f / gw, 0.5f / gh));
    }

    // (Re)creates the textures for an output of outW x outH. The ideal render keeps the
    // output's pixel density, so a smaller output is also cheaper to render.
    public void EnsureOutput(int outW, int outH)
    {
        if (DistortedTexture != null && DistortedTexture.width == outW && DistortedTexture.height == outH) return;
        ReleaseTextures();

        int idealW = Mathf.Max(16, Mathf.RoundToInt(IdealWidth * (float)outW / imageW));
        int idealH = Mathf.Max(16, Mathf.RoundToInt(IdealHeight * (float)outH / imageH));

        IdealTexture = new RenderTexture(idealW, idealH, 24, RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "LensDistortion Ideal",
        };
        IdealTexture.Create();

        DistortedTexture = new RenderTexture(outW, outH, 0, RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "LensDistortion Output",
        };
        DistortedTexture.Create();
    }

    public void Render()
    {
        if (IdealTexture == null || DistortedTexture == null) return;
        Graphics.Blit(IdealTexture, DistortedTexture, material);
    }

    void ReleaseTextures()
    {
        Destroy(IdealTexture); IdealTexture = null;
        Destroy(DistortedTexture); DistortedTexture = null;
    }

    public void Dispose()
    {
        ReleaseTextures();
        Destroy(material);
        Destroy(lut);
        lut = null;
    }

    static void Destroy(UnityEngine.Object o)
    {
        if (o == null) return;
        if (o is RenderTexture rt) rt.Release();
        if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
    }
}
