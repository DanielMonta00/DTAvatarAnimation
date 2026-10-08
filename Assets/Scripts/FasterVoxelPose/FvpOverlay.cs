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
    readonly Text label;
    readonly List<Text> tags = new List<Text>();
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

        label = NewText("Status", root.transform, TextAnchor.UpperLeft, 15);
        var lr = label.rectTransform;
        lr.anchorMin = lr.anchorMax = new Vector2(0f, 1f);
        lr.pivot = new Vector2(0f, 1f);
        lr.anchoredPosition = new Vector2(10f, -8f);
        lr.sizeDelta = new Vector2(1400f, 44f);
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    internal static Text NewText(string name, Transform parent, TextAnchor align, int size, bool shadow = true)
    {
        var go = new GameObject(name, typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        if (font == null)
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) font = Font.CreateDynamicFontFromOSFont("Arial", size);
        }
        t.font = font;
        t.fontSize = size;
        t.fontStyle = FontStyle.Bold;
        t.alignment = align;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        if (shadow)
        {
            var sh = go.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.9f);
            sh.effectDistance = new Vector2(1.5f, -1.5f);
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

    public void SetLabel(string text, Color color)
    {
        if (label.text != text) label.text = text;
        label.color = color;
    }

    // A tag next to a point (normalized image coordinates).
    public void Tag(int index, Vector2 uv, string text, Color color)
    {
        while (tags.Count <= index)
        {
            Text t = NewText("Tag", fit, TextAnchor.MiddleLeft, 14);
            t.rectTransform.pivot = new Vector2(0f, 0.5f);
            t.rectTransform.sizeDelta = new Vector2(200f, 22f);
            tags.Add(t);
        }
        Text tag = tags[index];
        if (!tag.gameObject.activeSelf) tag.gameObject.SetActive(true);
        tag.rectTransform.anchorMin = tag.rectTransform.anchorMax = new Vector2(uv.x, 1f - uv.y);
        tag.rectTransform.anchoredPosition = new Vector2(10f, 14f);
        if (tag.text != text) tag.text = text;
        tag.color = color;
    }

    public void HideTagsFrom(int index)
    {
        for (int i = index; i < tags.Count; i++)
            if (tags[i].gameObject.activeSelf) tags[i].gameObject.SetActive(false);
    }

    public void Destroy()
    {
        if (root == null) return;
        if (Application.isPlaying) Object.Destroy(root); else Object.DestroyImmediate(root);
    }
}
