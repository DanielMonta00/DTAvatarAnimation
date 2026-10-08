using UnityEngine;
using UnityEngine.UI;

// What the heatmap panel shows, built by the component and shared by the panel of every display.
public sealed class FvpHeatData
{
    public const int MaxViews = 8;
    public bool visible = true;
    public int views;                                       // 0 = nothing to draw (see message)
    public float aspect = 9f / 16f;                         // height / width of a tile
    public readonly Texture[] image = new Texture[MaxViews]; // the frame the heatmap belongs to (null = not kept)
    public readonly Texture[] heat = new Texture[MaxViews];  // RGBA colour-mapped heatmap, same geometry as the image
    public readonly string[] label = new string[MaxViews];
    public readonly int[] display = new int[MaxViews];      // the display the view's camera shows on
    public string message = "";
    public string hint = "";
    public int stamp;
}

// The 2D stage of Faster-VoxelPose made visible: for each camera, the network's joint heatmap painted over the image it was
// computed from. The 3D result is built only from these maps (every voxel samples them in each view), so where they are weak or
// misplaced the 3D joint is too. Top-right corner of a display: the tile(s) of the camera(s) that display shows, or all of them
// when it shows none. Same canvas as the transport bar and the status widget; whole-pixel layout for sharp text.
public sealed class FvpHeatPanel
{
    sealed class Tile
    {
        public Image back;
        public RawImage image, heat;
        public Text label;
        public void SetActive(bool on) { back.gameObject.SetActive(on); }
    }

    static readonly Color BackColor = new Color(0.04f, 0.05f, 0.07f, 0.78f);

    readonly Image panel;
    readonly Text hint, message;
    readonly Tile[] tiles = new Tile[FvpHeatData.MaxViews];
    int appliedStamp = -1, appliedDisplay = -1;
    float appliedScale;

    public bool Visible => panel.gameObject.activeSelf;
    public int TileCount { get; private set; }

    public FvpHeatPanel(Transform canvasRoot)
    {
        panel = NewImage("Heatmaps", canvasRoot, new Color(0f, 0f, 0f, 0f));
        hint = FvpOverlay.NewText("Hint", panel.transform, TextAnchor.UpperLeft, 11, shadow: false);
        message = FvpOverlay.NewText("Message", panel.transform, TextAnchor.UpperLeft, 12, shadow: false);
        hint.color = new Color(0.72f, 0.77f, 0.83f);
        message.color = new Color(1f, 0.72f, 0.3f);
        for (int i = 0; i < tiles.Length; i++)
        {
            var t = new Tile { back = NewImage("Tile", panel.transform, BackColor) };
            t.image = NewRaw("Image", t.back.transform);
            t.heat = NewRaw("Heat", t.back.transform);
            t.label = FvpOverlay.NewText("Label", t.back.transform, TextAnchor.UpperLeft, 12);
            tiles[i] = t;
        }
        panel.gameObject.SetActive(false);
    }

    static Image NewImage(string name, Transform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    static RawImage NewRaw(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
        go.transform.SetParent(parent, false);
        var r = go.AddComponent<RawImage>();
        r.raycastTarget = false;
        r.uvRect = new Rect(0f, 1f, 1f, -1f); // the pixels are uploaded top row first
        var rt = r.rectTransform;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
        return r;
    }

    // Top-left anchored inside the panel, whole pixels, y measured down.
    static void Put(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(Mathf.Round(x), -Mathf.Round(y));
        rt.sizeDelta = new Vector2(Mathf.Round(w), Mathf.Round(h));
    }

    public void Apply(FvpHeatData d, int display, float u)
    {
        bool show = d.visible && (d.views > 0 || !string.IsNullOrEmpty(d.message));
        if (!show) { if (panel.gameObject.activeSelf) panel.gameObject.SetActive(false); TileCount = 0; return; }
        if (!panel.gameObject.activeSelf) { panel.gameObject.SetActive(true); appliedStamp = -1; }
        if (appliedStamp == d.stamp && appliedDisplay == display && appliedScale == u) return;
        appliedStamp = d.stamp; appliedDisplay = display; appliedScale = u;

        float margin = Mathf.Round(10f * u), gap = Mathf.Round(8f * u);
        float tileW = Mathf.Round(300f * u), tileH = Mathf.Round(tileW * d.aspect);
        int f12 = Mathf.RoundToInt(12f * u), f11 = Mathf.RoundToInt(11f * u);

        // the cameras this display shows; a display that shows none gets every view
        int mine = 0;
        for (int v = 0; v < d.views; v++) if (d.display[v] == display) mine++;
        bool all = mine == 0;

        float y = 0f;
        int shown = 0;
        for (int v = 0; v < FvpHeatData.MaxViews; v++)
        {
            Tile t = tiles[v];
            bool on = v < d.views && (all || d.display[v] == display);
            t.SetActive(on);
            if (!on) continue;
            Put(t.back.rectTransform, 0f, y, tileW, tileH);
            t.image.texture = d.image[v];
            t.image.enabled = d.image[v] != null;
            t.heat.texture = d.heat[v];
            t.heat.enabled = d.heat[v] != null;
            if (t.label.fontSize != f12) t.label.fontSize = f12;
            if (t.label.text != d.label[v]) t.label.text = d.label[v];
            Put(t.label.rectTransform, 6f * u, 4f * u, tileW - 12f * u, 20f * u);
            y += tileH + gap;
            shown++;
        }
        TileCount = shown;

        bool hasMessage = !string.IsNullOrEmpty(d.message);
        message.gameObject.SetActive(hasMessage);
        if (hasMessage)
        {
            if (message.fontSize != f12) message.fontSize = f12;
            message.text = d.message;
            float mh = Mathf.Ceil(message.preferredHeight);
            Put(message.rectTransform, 0f, y, tileW, mh);
            y += mh + 4f * u;
        }

        bool hasHint = !string.IsNullOrEmpty(d.hint) && shown > 0;
        hint.gameObject.SetActive(hasHint);
        if (hasHint)
        {
            if (hint.fontSize != f11) hint.fontSize = f11;
            hint.text = d.hint;
            float hh = Mathf.Ceil(hint.preferredHeight);
            Put(hint.rectTransform, 0f, y, tileW, hh);
            y += hh;
        }

        // the panel itself hangs from the top-right corner
        RectTransform rt = panel.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-margin, -margin);
        rt.sizeDelta = new Vector2(tileW, Mathf.Max(1f, y));
    }
}
