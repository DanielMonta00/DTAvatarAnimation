using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Drawings over the displays of the cameras that feed a pose model: one screen overlay (FvpOverlay) per camera, on that camera's Target
// Display, exactly over the image the display shows (a CalibratedCamera's lens-distorted image is letterboxed like the presenter does).
// Nothing is drawn into the cameras' own render, so the pose model, the dataset recorder and any capture of the camera never see it:
// this is how the digital twin shows things (the skeletons, the protection capsule) that a real camera image would not contain.
public sealed class FvpDisplayLayer
{
    public sealed class View
    {
        public Camera cam;
        public CalibratedCamera calib;
        public FvpOverlay overlay;
        public FvpOverlayFill fill;       // under overlay.graphic's lines
        public RawImage bubble;           // a premultiplied picture (FvpBubbleRender) over the display, under the fill and the lines
        public bool letterboxed;
        Text status, label;               // made on first use
        string statusShown = "", labelShown = "";
        Material bubbleMaterial;

        // The bubble drawn afterwards (FvpBubbleRender.Output) over the image this display shows; null hides it. `shader` composites a premultiplied
        // picture (FvpPremultipliedOver). Returns false when that shader cannot be found.
        public bool SetBubble(Texture texture, Shader shader)
        {
            if (texture == null) { if (bubble.enabled) bubble.enabled = false; return true; }
            if (bubbleMaterial == null)
            {
                if (shader == null) shader = Shader.Find(FvpBubbleRender.CompositeShaderName);
                if (shader == null) return false;
                bubbleMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                bubble.material = bubbleMaterial;
            }
            if (bubble.texture != texture) bubble.texture = texture;
            if (!bubble.enabled) bubble.enabled = true;
            return true;
        }

        // A small line at the top-left corner of the image.
        public void SetStatus(string text, Color color)
        {
            if (status == null)
            {
                if (string.IsNullOrEmpty(text)) return;
                status = FvpOverlay.NewText("Status", overlay.fit, TextAnchor.UpperLeft, 13);
                var rt = status.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2(8f, -6f);
                rt.sizeDelta = new Vector2(1200f, 22f);
            }
            if (statusShown != text) { status.text = text; statusShown = text; }
            if (status.color != color) status.color = color;
            if (status.enabled != !string.IsNullOrEmpty(text)) status.enabled = !string.IsNullOrEmpty(text);
        }

        // A caption standing on a point of the image (uv: 0..1, origin top-left). Empty text hides it.
        public void SetLabel(string text, Vector2 uv, Color color)
        {
            if (label == null)
            {
                if (string.IsNullOrEmpty(text)) return;
                label = FvpOverlay.NewText("Label", overlay.fit, TextAnchor.LowerCenter, 14);
                var rt = label.rectTransform;
                rt.pivot = new Vector2(0.5f, 0f);
                rt.sizeDelta = new Vector2(400f, 22f);
                rt.anchoredPosition = new Vector2(0f, 4f);
            }
            bool show = !string.IsNullOrEmpty(text);
            if (label.enabled != show) label.enabled = show;
            if (!show) return;
            if (labelShown != text) { label.text = text; labelShown = text; }
            if (label.color != color) label.color = color;
            var r = label.rectTransform;
            r.anchorMin = r.anchorMax = new Vector2(uv.x, 1f - uv.y);
        }

        public void DestroyMaterial()
        {
            if (bubbleMaterial == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(bubbleMaterial); else UnityEngine.Object.DestroyImmediate(bubbleMaterial);
            bubbleMaterial = null;
        }

        public void Clear()
        {
            overlay.graphic.Begin(); overlay.graphic.End();
            fill.Begin(); fill.End();
            SetLabel("", default, Color.white);
        }
    }

    const int Samples = 24;                  // points on the limb circle of each end sphere of a capsule

    readonly string name;
    readonly int sortingOrder;
    readonly List<View> views = new List<View>();
    readonly List<Camera> wantedBuf = new List<Camera>();
    readonly Vector2[] pts = new Vector2[2 * Samples];
    readonly Vector2[] hull = new Vector2[2 * Samples + 2];
    readonly Vector2[] clipA = new Vector2[2 * Samples + 16];
    readonly Vector2[] clipB = new Vector2[2 * Samples + 16];

    public IReadOnlyList<View> Views => views;
    public int Count => views.Count;

    public FvpDisplayLayer(string name, int sortingOrder)
    {
        this.name = name;
        this.sortingOrder = sortingOrder;
    }

    static bool IsLetterboxed(CalibratedCamera cc) =>
        cc != null && cc.IsApplied && cc.DistortionActive && cc.showOnDisplay && cc.imageHeight > 0;

    // One view per camera that is on and passes `wanted` (null = all), in the order of the list. Does nothing when nothing changed, so it can
    // be called every frame; a camera whose calibration has just been applied (its image is now letterboxed) gets a new overlay.
    public void Sync(IReadOnlyList<Camera> cameras, Func<Camera, bool> wanted)
    {
        wantedBuf.Clear();
        if (cameras != null)
            for (int i = 0; i < cameras.Count; i++)
            {
                Camera c = cameras[i];
                if (c == null || !c.isActiveAndEnabled) continue;
                if (wanted != null && !wanted(c)) continue;
                wantedBuf.Add(c);
            }

        bool same = wantedBuf.Count == views.Count;
        for (int i = 0; same && i < views.Count; i++)
        {
            View v = views[i];
            if (v.cam != wantedBuf[i] || v.letterboxed != IsLetterboxed(v.calib)) same = false;
        }
        if (same) return;

        Destroy();
        for (int i = 0; i < wantedBuf.Count; i++)
        {
            Camera cam = wantedBuf[i];
            var v = new View { cam = cam };
            cam.TryGetComponent(out v.calib);
            v.letterboxed = IsLetterboxed(v.calib);
            float aspect = v.letterboxed ? (float)v.calib.imageWidth / v.calib.imageHeight : 1f;
            v.overlay = new FvpOverlay($"{name} {cam.name.Trim()}", sortingOrder + i, v.letterboxed, aspect);
            v.overlay.SetDisplay(cam.targetDisplay);

            // the bubble drawn afterwards: over the saved-frame image, under the fill and the lines; the image stretches over the same rectangle
            // the display's picture occupies (letterboxed like the presenter), so a point of the texture lands on the pixel the presenter draws there
            var bgo = new GameObject("Bubble", typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
            bgo.transform.SetParent(v.overlay.fit, false);
            var brt = (RectTransform)bgo.transform;
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
            bgo.transform.SetSiblingIndex(1);
            v.bubble = bgo.AddComponent<RawImage>();
            v.bubble.raycastTarget = false;
            v.bubble.enabled = false;

            var go = new GameObject("Fill", typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(v.overlay.fit, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            go.transform.SetSiblingIndex(2);                          // over the bubble, under the lines
            v.fill = go.AddComponent<FvpOverlayFill>();
            views.Add(v);
        }
    }

    public void SetVisible(bool visible)
    {
        foreach (View v in views) v.overlay.SetVisible(visible);
    }

    public void Destroy()
    {
        foreach (View v in views) { v.DestroyMaterial(); v.overlay.Destroy(); }
        views.Clear();
    }

    // ---------------- projection ----------------

    // A world point -> where it lands on the image this view's display shows (0..1, origin top-left). The calibrated camera's own lens model
    // when it has one (what the display shows is the lens-distorted image), the engine's pinhole otherwise. False: behind the camera, or past
    // the radius where the lens model is valid.
    public static bool TryProject(View v, Vector3 world, out Vector2 uv)
    {
        Vector3 vp = v.calib != null && v.calib.IsApplied ? v.calib.WorldToViewport(world) : v.cam.WorldToViewportPoint(world);
        uv = new Vector2(vp.x, 1f - vp.y);
        return vp.z > v.cam.nearClipPlane;
    }

    // ---------------- a capsule as seen by a camera ----------------

    // The outline, on the image, of a capsule given in the local space of an object: axis along local Y through `centre`, end-sphere radius
    // `rho`, `half` from the centre to the centre of each end sphere; `localToWorld` / `worldToLocal` are the object's own matrices, scale
    // included. A scale that is not uniform (the scene's capsule is squashed to 0.83 of its height) turns the end caps into ellipsoids, so the
    // silhouette is worked out in the local space, where they are spheres: the limb circle of each end sphere as the camera's eye sees it (the
    // exact silhouette of a sphere under perspective), mapped back to the world, projected, and the convex hull of both clipped to the image.
    // Returns the number of vertices (0: not in view) and the polygon in `poly`, valid until the next call. A camera inside the capsule, or one
    // that sees none of it, gives 0.
    public int CapsuleOutline(View v, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 centre, float rho, float half, out Vector2[] poly)
    {
        poly = clipB;
        Vector3 eye = worldToLocal.MultiplyPoint3x4(v.cam.transform.position);
        int n = 0;
        for (int end = 0; end < 2; end++)
        {
            Vector3 c = centre + (end == 0 ? -half : half) * Vector3.up;
            Vector3 toEye = eye - c;
            float d = toEye.magnitude;
            if (d <= rho * 1.02f) return 0;
            Vector3 axis = toEye / d;
            Vector3 mid = c + axis * (rho * rho / d);                      // the plane of the limb circle
            float rad = rho * Mathf.Sqrt(Mathf.Max(0f, 1f - (rho * rho) / (d * d)));
            Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 w = Vector3.Cross(axis, u);
            for (int k = 0; k < Samples; k++)
            {
                float ang = k * (Mathf.PI * 2f / Samples);
                Vector3 p = localToWorld.MultiplyPoint3x4(mid + (u * Mathf.Cos(ang) + w * Mathf.Sin(ang)) * rad);
                if (TryProject(v, p, out Vector2 uv)) pts[n++] = uv;
            }
        }
        if (n < 3) return 0;

        int h = ConvexHull(pts, n, hull);
        if (h < 3) return 0;
        // clip to the image rectangle: 0 <= x <= 1, 0 <= y <= 1
        int m = ClipEdge(hull, h, clipA, 0, 0f, true);
        m = ClipEdge(clipA, m, clipB, 0, 1f, false);
        m = ClipEdge(clipB, m, clipA, 1, 0f, true);
        m = ClipEdge(clipA, m, clipB, 1, 1f, false);
        return m >= 3 ? m : 0;
    }

    static bool Less(Vector2 p, Vector2 q) => p.x < q.x || (p.x == q.x && p.y < q.y);
    static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

    // Andrew's monotone chain. Sorts p[0..n) in place. h needs room for 2n entries; returns the hull's vertex count.
    static int ConvexHull(Vector2[] p, int n, Vector2[] h)
    {
        for (int i = 1; i < n; i++)
        {
            Vector2 key = p[i];
            int j = i - 1;
            while (j >= 0 && Less(key, p[j])) { p[j + 1] = p[j]; j--; }
            p[j + 1] = key;
        }
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            while (k >= 2 && Cross(h[k - 2], h[k - 1], p[i]) <= 0f) k--;
            h[k++] = p[i];
        }
        for (int i = n - 2, t = k + 1; i >= 0; i--)
        {
            while (k >= t && Cross(h[k - 2], h[k - 1], p[i]) <= 0f) k--;
            h[k++] = p[i];
        }
        return k - 1;     // the last point is the first again
    }

    // Sutherland-Hodgman against one half-plane of the image rectangle: axis 0/1 = x/y, keep the side >= bound (or <= bound).
    static int ClipEdge(Vector2[] src, int n, Vector2[] dst, int axis, float bound, bool keepGreater)
    {
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            Vector2 cur = src[i], prev = src[(i + n - 1) % n];
            float cv = axis == 0 ? cur.x : cur.y, pv = axis == 0 ? prev.x : prev.y;
            bool curIn = keepGreater ? cv >= bound : cv <= bound;
            bool prevIn = keepGreater ? pv >= bound : pv <= bound;
            if (curIn != prevIn)
            {
                float t = (bound - pv) / (cv - pv);
                dst[m++] = prev + (cur - prev) * t;
            }
            if (curIn) dst[m++] = cur;
        }
        return m;
    }
}
