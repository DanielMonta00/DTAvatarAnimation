using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// The bubble on the displays of the pose cameras. It is switched off for those cameras (Hide From Pose Cameras: in the twin the model reads
// renders of the scene, and a bubble in them hides the operator from it), so their displays would show nothing at all. It is put back AFTER
// the picture the model reads has been taken, in one of two ways (Bubble Display):
//
//   RenderedAfterwards  the real objects (their own materials and tint, lit by the scene's lights, glass and all) are rendered by a second,
//                       hidden camera that sees nothing else, through the pose camera's projection and lens distortion, and shown over the
//                       display (FvpBubbleRender): the real object, minus hiding behind objects in front of it. Needs a lens-distorted
//                       CalibratedCamera and a render pipeline; a camera that cannot do it falls back to the outline.
//   Outline             a flat silhouette per piece in the colour and transparency of its material (FvpDisplayLayer); nothing is rendered.
//
// Both are screen overlays on the camera's display (FvpDisplayLayer): not part of any camera image. Every other camera (the Scene view,
// other displays) shows the real object as usual.
public sealed partial class PoseEstimateFollower
{
    public enum BubbleDisplay { RenderedAfterwards = 0, Outline = 1 }

    const float FillAlphaScale = 0.85f, LineAlphaAdd = 0.4f, OutlineWidth = 2.5f;

    // One piece of the bubble: a mesh under this object. For the outline it is taken as a capsule in its own local space (axis along local Y,
    // the radius and length of its bounds): the built-in capsule is exactly that, a sphere is the same with no length, anything else is the
    // nearest capsule. For the afterwards render the mesh and its materials are drawn as they are.
    struct BubblePart
    {
        public Transform t;
        public Vector3 centre;       // local
        public float rho, half;      // local: end-sphere radius, distance from the centre to the centre of an end sphere
        public int queue;            // the material's render queue: drawn in the order the cameras draw them
        public Color color;          // the material's colour, alpha included
    }

    readonly List<BubblePart> parts = new List<BubblePart>();
    readonly Dictionary<CalibratedCamera, FvpBubbleRender> bubbles = new Dictionary<CalibratedCamera, FvpBubbleRender>();
    readonly List<CalibratedCamera> pruneBuf = new List<CalibratedCamera>();
    float nextPartsRefresh;
    Renderer[] truthRenderers = System.Array.Empty<Renderer>();
    Animator truthRenderersOf;

    // What hides the bubble: the list given, else the real operator's renderers (the twin knows them; the bubble is built around that body).
    IReadOnlyList<Renderer> OccluderList()
    {
        if (occluders != null && occluders.Count > 0) return occluders;
        if (truthAvatar == null) return null;
        if (truthRenderersOf != truthAvatar) { truthRenderers = truthAvatar.GetComponentsInChildren<Renderer>(true); truthRenderersOf = truthAvatar; }
        return truthRenderers;
    }

    FvpDisplayLayer display;
    float nextDisplaySync;
    string labelText = "";
    int labelTenths = -1;
    OperatorTrack.State labelState = OperatorTrack.State.Searching;
    int labelId = int.MinValue;

    // For tests: the overlay drawn on the display of pose camera number `index` (null before the first frame, or when it is not drawn), how many
    // pieces the bubble has (the fill's polygons come in the order they are drawn: dearest queue last), and the afterwards render of a camera.
    public FvpOverlay DisplayOverlayOf(int index) => display != null && index >= 0 && index < display.Count ? display.Views[index].overlay : null;
    public FvpOverlayFill DisplayFillOf(int index) => display != null && index >= 0 && index < display.Count ? display.Views[index].fill : null;
    public FvpDisplayLayer.View DisplayViewOf(int index) => display != null && index >= 0 && index < display.Count ? display.Views[index] : null;
    public FvpBubbleRender BubbleRenderOf(CalibratedCamera calibrated) => calibrated != null && bubbles.TryGetValue(calibrated, out FvpBubbleRender b) ? b : null;
    public int DisplayCount => display != null ? display.Count : 0;
    public int BubblePartCount => parts.Count;

#if UNITY_EDITOR
    void OnValidate()
    {
        if (bubbleShader == null) bubbleShader = Shader.Find(FvpBubbleRender.CompositeShaderName);      // referenced, so that a build includes it
        if (depthShader == null) depthShader = Shader.Find(FvpBubbleRender.DepthShaderName);
    }
#endif

    static Color MaterialColor(Material m)
    {
        if (m == null) return Color.white;
        if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor");      // URP
        if (m.HasProperty("_Color")) return m.GetColor("_Color");              // built-in
        return Color.white;
    }

    void RefreshParts()
    {
        parts.Clear();
        if (renderers == null) return;
        foreach (Renderer r in renderers)
        {
            if (r == null || !r.TryGetComponent(out MeshFilter mf) || mf.sharedMesh == null) continue;      // a mesh: a piece of the bubble
            Bounds b = mf.sharedMesh.bounds;
            Material m = r.sharedMaterial;
            var p = new BubblePart
            {
                t = r.transform, centre = b.center, rho = Mathf.Max(b.extents.x, b.extents.z),
                queue = m != null ? m.renderQueue : 3000, color = MaterialColor(m),
            };
            p.half = Mathf.Max(0f, b.extents.y - p.rho);
            if (p.rho > 1e-4f) parts.Add(p);
        }
        // as the cameras draw them: by render queue, the wider one first when the queues are equal (it is the outer one)
        parts.Sort((x, y) => x.queue != y.queue ? x.queue.CompareTo(y.queue) : y.rho.CompareTo(x.rho));
    }

    void HideDisplay()
    {
        display?.SetVisible(false);
        foreach (FvpBubbleRender b in bubbles.Values) b.SetActive(false);
    }

    void UpdateDisplay(OperatorTrack.State state, double now)
    {
        bool want = drawOnPoseCameras && hideFromPoseCameras && src != null;
        if (want && Time.unscaledTime >= nextPartsRefresh) { nextPartsRefresh = Time.unscaledTime + 1f; RefreshParts(); }
        if (!want || parts.Count == 0)
        {
            HideDisplay();
            return;
        }

        if (display == null) display = new FvpDisplayLayer("Capsule overlay", 180);
        if (Time.unscaledTime >= nextDisplaySync)
        {
            nextDisplaySync = Time.unscaledTime + 0.5f;
            display.Sync(src.PoseCameras, src.IsPoseCamera);        // only the cameras the capsule is hidden from need a stand-in
            PruneBubbles();
        }
        display.SetVisible(true);
        if (bubbleDisplay != BubbleDisplay.RenderedAfterwards && bubbles.Count > 0) DisposeBubbles();

        Color stateColor = state == OperatorTrack.State.Tracking ? followingColor : state == OperatorTrack.State.Lost ? lostColor : searchingColor;
        string text = drawLabel ? LabelFor(state, now) : "";

        foreach (FvpDisplayLayer.View v in display.Views)
        {
            if (v.cam == null) continue;                    // destroyed since the last Sync
            v.overlay.SetDisplay(v.cam.targetDisplay);

            // the real bubble, rendered afterwards, where the camera can do that; otherwise (or by choice) the flat outline
            bool rendered = bubbleDisplay == BubbleDisplay.RenderedAfterwards && v.calib != null && v.letterboxed && DrawRendered(v);
            if (!rendered) v.SetBubble(null, null);

            FvpOverlayGraphic g = v.overlay.graphic;
            g.lineWidth = OutlineWidth;
            g.Begin();
            v.fill.Begin();

            // the silhouettes: drawn when there is no render, and always the place of the caption
            float topY = float.MaxValue, topX = 0.5f;
            for (int k = 0; k < parts.Count; k++)
            {
                BubblePart part = parts[k];
                if (part.t == null) continue;
                int n = display.CapsuleOutline(v, part.t.localToWorldMatrix, part.t.worldToLocalMatrix, part.centre, part.rho, part.half, out Vector2[] poly);
                if (n < 3) continue;

                Color c = tinted ? notTrackingTint : part.color;        // grey while the operator is not followed, as the real renderers are
                Color fillColor = c, lineColor = c;
                fillColor.a = c.a * FillAlphaScale;
                lineColor.a = Mathf.Clamp01(c.a + LineAlphaAdd);
                if (!rendered) v.fill.AddPolygon(poly, n, fillColor);
                for (int i = 0; i < n; i++)
                {
                    Vector2 p = poly[i], q = poly[(i + 1) % n];
                    if (p.y < topY) { topY = p.y; topX = p.x; }
                    if (!rendered && !OnImageBorder(p, q)) g.AddLine(p, q, lineColor);      // the edge that only closes the cut-off outline is not drawn
                }
            }
            v.SetLabel(topY < float.MaxValue ? text : "", new Vector2(topX, Mathf.Max(topY, 0.05f)), stateColor);

            g.End();
            v.fill.End();
        }
    }

    // The bubble for this view's camera, rendered afterwards. False when this camera cannot (then the outline is drawn).
    bool DrawRendered(FvpDisplayLayer.View v)
    {
        if (!bubbles.TryGetValue(v.calib, out FvpBubbleRender br))
        {
            br = new FvpBubbleRender(v.calib);
            bubbles[v.calib] = br;
        }
        if (!br.Prepare(bubbleResolution)) return false;
        br.SetObjects(renderers);       // the real objects: their own materials and the grey tint they get while the operator is not followed
        br.SetOccluders(OccluderList(), depthShader);      // what hides it: the operator inside the bubble, whatever else is listed
        br.SetActive(true);
        return v.SetBubble(br.Output, bubbleShader);
    }

    // Afterwards renders of cameras that no longer have a view (the pose cameras changed): freed.
    void PruneBubbles()
    {
        if (bubbles.Count == 0) return;
        pruneBuf.Clear();
        foreach (KeyValuePair<CalibratedCamera, FvpBubbleRender> kv in bubbles)
        {
            bool used = kv.Key != null;
            if (used)
            {
                used = false;
                foreach (FvpDisplayLayer.View v in display.Views) if (v.calib == kv.Key) { used = true; break; }
            }
            if (!used) pruneBuf.Add(kv.Key);
        }
        foreach (CalibratedCamera key in pruneBuf) { bubbles[key].Dispose(); bubbles.Remove(key); }
    }

    void DisposeBubbles()
    {
        foreach (FvpBubbleRender b in bubbles.Values) b.Dispose();
        bubbles.Clear();
    }

    static bool OnImageBorder(Vector2 p, Vector2 q)
    {
        const float e = 1e-4f;
        return (p.x < e && q.x < e) || (p.x > 1f - e && q.x > 1f - e) || (p.y < e && q.y < e) || (p.y > 1f - e && q.y > 1f - e);
    }

    // The caption; rebuilt only when what it says changes (the time of a loss in tenths of a second).
    string LabelFor(OperatorTrack.State state, double now)
    {
        int tenths = state == OperatorTrack.State.Lost ? (int)(track.SinceMatchArrived(now) * 10.0) : 0;
        if (state == labelState && track.LockedId == labelId && tenths == labelTenths) return labelText;
        labelState = state; labelId = track.LockedId; labelTenths = tenths;
        labelText = state == OperatorTrack.State.Tracking ? "following #" + labelId
                  : state == OperatorTrack.State.Lost ? "lost #" + labelId + " for " + (tenths / 10f).ToString("F1", CultureInfo.InvariantCulture) + " s"
                  : "searching";
        return labelText;
    }

    void DestroyDisplay()
    {
        DisposeBubbles();
        display?.Destroy();
        display = null;
    }
}
