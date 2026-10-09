using UnityEngine;

// The textures behind a heatmap panel (FvpHeatPanel): per camera one colour-mapped RGBA heatmap and, under it, the picture it was
// computed from. Shared by every model that shows heatmaps (FasterVoxelPose, ViTPose), so they paint identically.
//
// A heatmap arrives as one byte per cell (255 = 1.0) in the geometry of the frame that was sent, at 1/4 of its size, rows from the
// top; the picture is the RGB24 frame that was sent, also top row first. Both are therefore shown with a vertically flipped uvRect.
public sealed class FvpHeatTextures
{
    public Texture2D[] heat, image;
    public readonly float[] peak = new float[FvpHeatData.MaxViews];   // the strongest cell of each view's map, 0..1
    public object heatOwner, imageOwner;                              // what the textures currently hold (a frame / a result)

    int heatW, heatH, imgW, imgH;
    Color32[] pixels;
    static Color32[] lut;

    static void BuildLut()
    {
        // inferno-like: transparent where the network sees nothing, then purple, red, orange, yellow-white
        (float t, Color32 c)[] stops =
        {
            (0.00f, new Color32(20, 0, 60, 0)), (0.10f, new Color32(70, 10, 120, 70)), (0.30f, new Color32(190, 40, 100, 165)),
            (0.55f, new Color32(255, 130, 20, 210)), (0.80f, new Color32(255, 225, 70, 232)), (1.00f, new Color32(255, 255, 225, 245)),
        };
        lut = new Color32[256];
        for (int i = 0; i < 256; i++)
        {
            float t = Mathf.Clamp01(i / 255f * 1.35f); // the maps peak at 0.4-0.8, so stretch them
            int s = 0;
            while (s < stops.Length - 2 && t > stops[s + 1].t) s++;
            float k = Mathf.InverseLerp(stops[s].t, stops[s + 1].t, t);
            lut[i] = Color32.Lerp(stops[s].c, stops[s + 1].c, k);
        }
    }

    // `maps`: [view][y * w + x]. `owner`: whatever it belongs to, so the caller can tell what is on the textures.
    public void FillHeat(byte[][] maps, int w, int h, object owner)
    {
        if (lut == null) BuildLut();
        int views = Mathf.Min(maps.Length, FvpHeatData.MaxViews);
        if (heat == null || heat.Length != views || heatW != w || heatH != h)
        {
            DestroyHeat();
            heat = new Texture2D[views];
            for (int v = 0; v < views; v++)
                heat[v] = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = $"FVP heat {v}", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            pixels = new Color32[w * h];
            heatW = w; heatH = h;
        }
        for (int v = 0; v < views; v++)
        {
            byte[] src = maps[v];
            if (src == null || src.Length != w * h) { peak[v] = 0f; continue; }
            byte top = 0;
            for (int i = 0; i < src.Length; i++)
            {
                byte b = src[i];
                if (b > top) top = b;
                pixels[i] = lut[b];
            }
            heat[v].SetPixels32(pixels);
            heat[v].Apply(false);
            peak[v] = top / 255f;
        }
        heatOwner = owner;
    }

    // `raw`: [view] RGB24, w x h, top row first. False when a buffer is gone (already given back to the pool).
    public bool UploadImage(byte[][] raw, int w, int h, object owner)
    {
        if (raw == null) return false;
        if (image == null || image.Length != raw.Length || imgW != w || imgH != h)
        {
            DestroyImages();
            image = new Texture2D[raw.Length];
            for (int v = 0; v < image.Length; v++)
                image[v] = new Texture2D(w, h, TextureFormat.RGB24, false) { name = $"FVP heat image {v}", filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            imgW = w; imgH = h;
        }
        for (int v = 0; v < raw.Length; v++)
        {
            byte[] r = raw[v];
            if (r == null || r.Length != w * h * 3) return false;
            image[v].LoadRawTextureData(r);
            image[v].Apply(false);
        }
        imageOwner = owner;
        return true;
    }

    public void DestroyHeat()
    {
        if (heat != null) foreach (Texture2D t in heat) if (t != null) Object.Destroy(t);
        heat = null; heatOwner = null;
    }

    public void DestroyImages()
    {
        if (image != null) foreach (Texture2D t in image) if (t != null) Object.Destroy(t);
        image = null; imageOwner = null;
    }

    public void DestroyAll()
    {
        DestroyHeat();
        DestroyImages();
    }
}
