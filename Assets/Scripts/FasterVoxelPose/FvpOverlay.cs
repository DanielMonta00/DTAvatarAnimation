using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Skeleton lines and dots drawn over ONE camera display.
//
// A screen-space overlay canvas on the camera's Target Display (IMGUI could only ever draw on Display 1). For a
// CalibratedCamera whose lens-distorted image is presented on that display, the canvas uses the same letterbox the
// presenter uses (an AspectRatioFitter, FitInParent, aspect = calibrated image size), so a point at image position
// (u, v) lands exactly on the pixel the presenter draws there. Other cameras render straight to their display and get
// a full-screen overlay driven by the camera's own viewport.
//
// Everything is positioned in normalized image coordinates (0..1, origin top-left), so it follows the display size.
public sealed class FvpOverlay
{
    public readonly GameObject root;
    public readonly Canvas canvas;
    public readonly RectTransform fit;
    public readonly FvpOverlayGraphic graphic;
    public readonly bool letterboxed;

    readonly RawImage image;
    static Font font;

    public FvpOverlay(string name, int sortingOrder, bool letterboxed, float imageAspect)
    {
        this.letterboxed = letterboxed;

        root = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder; // above the presenter's canvas (100 + sibling index)
        // No CanvasScaler: one canvas unit is one screen pixel, like the presenter this sits on.

        var fitGo = new GameObject("Fit", typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
        fitGo.transform.SetParent(root.transform, false);
        fit = (RectTransform)fitGo.transform;
        Stretch(fit);
        if (letterboxed)
        {
            var ratio = fitGo.AddComponent<AspectRatioFitter>();
            ratio.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            ratio.aspectRatio = imageAspect;
        }

        // Under the skeletons: a saved frame that covers the live image the presenter below shows.
        var imageGo = new GameObject("Frame", typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
        imageGo.transform.SetParent(fit, false);
        Stretch((RectTransform)imageGo.transform);
        image = imageGo.AddComponent<RawImage>();
        image.raycastTarget = false;
        image.enabled = false;

        var lines = new GameObject("Skeletons", typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
        lines.transform.SetParent(fit, false);
        Stretch((RectTransform)lines.transform);
        graphic = lines.AddComponent<FvpOverlayGraphic>();
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    // The UI font: Segoe UI where it exists (Windows), else Arial, else Unity's built-in one. A regular face, never a synthesized
    // bold: LegacyRuntime has no bold face, so Unity smears the regular one, and small smeared text is what reads as low quality.
    internal static Font UiFont
    {
        get
        {
            if (font != null) return font;
            try { font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial" }, 14); } catch { font = null; }
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return font;
        }
    }

    internal static Text NewText(string name, Transform parent, TextAnchor align, int size, bool shadow = true)
    {
        var go = new GameObject(name, typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.font = UiFont;
        t.fontSize = size;
        t.fontStyle = FontStyle.Normal;
        t.alignment = align;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        t.supportRichText = true;
        if (shadow)
        {
            var sh = go.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.75f);
            sh.effectDistance = new Vector2(1f, -1f); // one whole pixel: a fractional offset blurs the glyphs
        }
        return t;
    }

    // A saved frame to show instead of the live image (null = show the live image).
    public void SetImage(Texture texture)
    {
        if (texture == null) { if (image.enabled) image.enabled = false; return; }
        if (image.texture != texture) image.texture = texture;
        if (!image.enabled) image.enabled = true;
    }

    public Texture Image => image.enabled ? image.texture : null;

    public void SetDisplay(int display) { if (canvas.targetDisplay != display) canvas.targetDisplay = display; }
    public void SetVisible(bool visible) { if (canvas.enabled != visible) canvas.enabled = visible; }

    public void Destroy()
    {
        if (root == null) return;
        if (Application.isPlaying) Object.Destroy(root); else Object.DestroyImmediate(root);
    }
}
