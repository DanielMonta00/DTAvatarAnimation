using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// What the corner widget shows, built by the component a few times a second and shared by the widget of every display.
public sealed class FvpHudData
{
    public struct PersonRow
    {
        public int id;
        public float score;       // the network's confidence (see the footer of the widget)
        public float errMm;       // mean joint distance to the ground truth at the same instant; < 0 = none to compare with
        public bool ghost;        // there is ground truth, but nobody within a metre of this skeleton
        public Color color;
        public string label;      // replaces "#id" (another model's rows are not people with an id)
        public string detail;     // replaces the text after the score bar ("0.16   err 118 mm")
    }

    public bool visible = true;
    public string state = "";     // LIVE / PAUSED / REWIND / REPLAY / ...
    public Color stateColor = Color.white;
    public string legend = "";
    public string statLabels = "", statValues = "";
    public string warn = "";
    public string footer = "";
    public float minScore = 0.1f;
    public float scoreFullScale = 0.5f;
    public string emptyText = "nobody found";   // shown when there is no row
    public readonly List<PersonRow> people = new List<PersonRow>();
    public int stamp;             // bumped by the builder whenever anything above changed
}

// A small status card in the top-left corner of a display: what is being shown, how fast, how late, and who was found with
// what confidence and error. One per display, in the same canvas as the transport bar; replaces the text that used to sit over
// the pictures. Whole-pixel layout and a regular-weight font keep it sharp.
public sealed class FvpHud
{
    static readonly Color PanelColor = new Color(0.04f, 0.05f, 0.07f, 0.66f);
    static readonly Color Dimmed = new Color(0.72f, 0.77f, 0.83f, 1f);

    const int MaxRows = 6;

    readonly Image panel;
    readonly Text title, state, legend, labels, values, warn, footer, more;
    readonly Row[] rows = new Row[MaxRows];
    int appliedStamp = -1;
    float appliedScale;
    string appliedTitle, appliedBrand;

    sealed class Row
    {
        public Image swatch, track, fill, tick;
        public Text id, value;
        public void SetActive(bool on)
        {
            swatch.gameObject.SetActive(on); track.gameObject.SetActive(on); fill.gameObject.SetActive(on);
            tick.gameObject.SetActive(on); id.gameObject.SetActive(on); value.gameObject.SetActive(on);
        }
    }

    public FvpHud(Transform canvasRoot)
    {
        panel = NewImage("HUD", canvasRoot, PanelColor);
        Transform p = panel.transform;
        title = Label("Title", p, TextAnchor.MiddleLeft, 14);
        state = Label("State", p, TextAnchor.MiddleRight, 13);
        legend = Label("Legend", p, TextAnchor.MiddleLeft, 12);
        labels = Label("Labels", p, TextAnchor.UpperLeft, 12);
        values = Label("Values", p, TextAnchor.UpperLeft, 12);
        warn = Label("Warn", p, TextAnchor.UpperLeft, 12);
        footer = Label("Footer", p, TextAnchor.UpperLeft, 11);
        more = Label("More", p, TextAnchor.MiddleLeft, 12);
        labels.color = Dimmed; footer.color = new Color(0.58f, 0.63f, 0.7f); more.color = Dimmed;
        warn.color = new Color(1f, 0.72f, 0.3f);
        warn.horizontalOverflow = footer.horizontalOverflow = HorizontalWrapMode.Wrap;

        for (int i = 0; i < MaxRows; i++)
        {
            var r = new Row
            {
                swatch = NewImage("Swatch", p, Color.white),
                track = NewImage("Track", p, new Color(1f, 1f, 1f, 0.16f)),
                fill = NewImage("Fill", p, new Color(1f, 0.6f, 0.18f, 0.95f)),
                tick = NewImage("MinScore", p, new Color(1f, 1f, 1f, 0.85f)),
                id = Label("Id", p, TextAnchor.MiddleLeft, 13),
                value = Label("Value", p, TextAnchor.MiddleLeft, 12),
            };
            rows[i] = r;
        }
        panel.gameObject.SetActive(false);
    }

    // For tests and tools.
    public bool Visible => panel.gameObject.activeSelf;

    public int VisibleRows
    {
        get { int n = 0; foreach (Row r in rows) if (r.id.gameObject.activeSelf) n++; return n; }
    }

    public string AllText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (Text t in panel.GetComponentsInChildren<Text>(false)) sb.AppendLine(t.text);
        return sb.ToString();
    }

    static Text Label(string name, Transform parent, TextAnchor align, int size)
    {
        Text t = FvpOverlay.NewText(name, parent, align, size, shadow: false);
        t.color = Color.white;
        return t;
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

    // Top-left anchored, whole pixels, y measured down from the top of the parent.
    static void Put(Graphic g, float x, float y, float w, float h) => Put(g.rectTransform, x, y, w, h);

    static void Put(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(Mathf.Round(x), -Mathf.Round(y));
        rt.sizeDelta = new Vector2(Mathf.Round(w), Mathf.Round(h));
    }

    static void SetSize(Text t, int size) { if (t.fontSize != size) t.fontSize = size; }
    static void SetText(Text t, string s) { if (t.text != s) t.text = s; }

    // `cameras`: what this display shows (the title). `u`: the UI scale (fonts are whole pixel sizes, edges whole pixels).
    public void Apply(FvpHudData d, string cameras, float u, string brand = "FVP")
    {
        if (!d.visible) { if (panel.gameObject.activeSelf) panel.gameObject.SetActive(false); return; }
        if (!panel.gameObject.activeSelf) { panel.gameObject.SetActive(true); appliedStamp = -1; }
        if (appliedStamp == d.stamp && appliedScale == u && appliedTitle == cameras && appliedBrand == brand) return;
        appliedStamp = d.stamp; appliedScale = u; appliedTitle = cameras; appliedBrand = brand;

        float pad = 10f * u, margin = 10f * u, lh = Mathf.Round(20f * u);
        int f13 = Mathf.RoundToInt(13f * u), f12 = Mathf.RoundToInt(12f * u), f11 = Mathf.RoundToInt(11f * u), f14 = Mathf.RoundToInt(14f * u);
        SetSize(title, f14); SetSize(state, f13); SetSize(legend, f12); SetSize(labels, f12); SetSize(values, f12);
        SetSize(warn, f12); SetSize(footer, f11); SetSize(more, f12);

        // ---- measure: the card is as wide as its widest line (at least 340, at most 720 at scale 1), the long notes wrap
        SetText(title, string.IsNullOrEmpty(cameras) ? brand : brand + "  ·  " + ShortTitle(cameras));
        SetText(state, d.state);
        SetText(legend, d.legend);
        SetText(labels, d.statLabels);
        SetText(values, d.statValues);
        float labelW = 74f * u;
        bool labelled = false;
        for (int i = 0; i < d.people.Count; i++) if (d.people[i].label != null) labelled = true;
        float trackW = 104f * u, idW = (labelled ? 74f : 30f) * u, rowH = Mathf.Round(20f * u), sw = Mathf.Round(8f * u), sh = Mathf.Round(14f * u);
        float tx = pad + sw + 6f * u + idW;
        float valueX = tx + trackW + 8f * u;
        int shown = Mathf.Min(MaxRows, d.people.Count);

        float need = Mathf.Max(2f * pad + labelW + Mathf.Ceil(values.preferredWidth), 2f * pad + Mathf.Ceil(legend.preferredWidth));
        for (int i = 0; i < shown; i++)
        {
            Row r = rows[i];
            FvpHudData.PersonRow p = d.people[i];
            SetSize(r.id, f13); SetSize(r.value, f12);
            SetText(r.id, p.label ?? "#" + p.id);
            string err = p.ghost ? "<color=#ff7a6b>nobody there?</color>"
                       : p.errMm >= 0f ? $"<color={ErrColor(p.errMm)}>err {p.errMm:F0} mm</color>"
                       : "<color=#8a95a3>no ground truth</color>";
            SetText(r.value, p.detail ?? $"{p.score:F2}   {err}");
            need = Mathf.Max(need, valueX + Mathf.Ceil(r.value.preferredWidth) + pad);
        }
        float w = Mathf.Round(Mathf.Clamp(need, 340f * u, 720f * u));
        float inner = w - 2f * pad;

        // ---- lay out
        float y = pad;
        Put(title, pad, y, inner * 0.6f, lh);
        state.color = d.stateColor;
        Put(state, pad + inner * 0.6f, y, inner * 0.4f, lh);
        y += lh;

        Put(legend, pad, y, inner, lh);
        y += lh + 4f * u;

        float statH = Mathf.Ceil(Mathf.Max(labels.preferredHeight, values.preferredHeight));
        Put(labels, pad, y, labelW, statH);
        Put(values, pad + labelW, y, inner - labelW, statH);
        y += statH + 6f * u;

        for (int i = 0; i < MaxRows; i++)
        {
            Row r = rows[i];
            r.SetActive(i < shown);
            if (i >= shown) continue;
            FvpHudData.PersonRow p = d.people[i];

            r.swatch.color = p.color;
            Put(r.swatch, pad, y + (rowH - sh) * 0.5f, sw, sh);
            Put(r.id, pad + sw + 6f * u, y, idW, rowH);

            float th = Mathf.Round(6f * u / 2f) * 2f;
            float ty = y + (rowH - th) * 0.5f;
            Put(r.track, tx, ty, trackW, th);
            float frac = Mathf.Clamp01(p.score / Mathf.Max(1e-3f, d.scoreFullScale));
            Put(r.fill, tx, ty, Mathf.Max(1f, trackW * frac), th);
            float tickH = Mathf.Round(12f * u);
            Put(r.tick, tx + trackW * Mathf.Clamp01(d.minScore / Mathf.Max(1e-3f, d.scoreFullScale)), y + (rowH - tickH) * 0.5f, Mathf.Max(1f, Mathf.Round(u)), tickH);

            Put(r.value, valueX, y, w - valueX - pad, rowH);
            y += rowH;
        }
        if (d.people.Count == 0)
        {
            SetText(more, string.IsNullOrEmpty(d.emptyText) ? "nobody found" : d.emptyText);
            Put(more, pad, y, inner, rowH);
            more.gameObject.SetActive(true);
            y += rowH;
        }
        else if (d.people.Count > shown)
        {
            SetText(more, $"+ {d.people.Count - shown} more");
            Put(more, pad, y, inner, rowH);
            more.gameObject.SetActive(true);
            y += rowH;
        }
        else more.gameObject.SetActive(false);

        // the notes wrap at the card's width: the width is set before the height is read
        bool hasWarn = !string.IsNullOrEmpty(d.warn);
        warn.gameObject.SetActive(hasWarn);
        if (hasWarn)
        {
            y += 4f * u;
            SetText(warn, d.warn);
            Put(warn, pad, y, inner, lh);
            float wh = Mathf.Ceil(warn.preferredHeight);
            Put(warn, pad, y, inner, wh);
            y += wh;
        }

        bool hasFooter = !string.IsNullOrEmpty(d.footer);
        footer.gameObject.SetActive(hasFooter);
        if (hasFooter)
        {
            y += 6f * u;
            SetText(footer, d.footer);
            Put(footer, pad, y, inner, lh);
            float fh = Mathf.Ceil(footer.preferredHeight);
            Put(footer, pad, y, inner, fh);
            y += fh;
        }

        y += pad;
        Put(panel, margin, margin, w, y);
    }

    // Several cameras on one display: the first and how many more, so the title never runs into the state next to it.
    static string ShortTitle(string cameras)
    {
        int comma = cameras.IndexOf(',');
        if (comma < 0) return cameras.Length <= 22 ? cameras : cameras.Substring(0, 21) + "…";
        int more = 1;
        for (int i = comma + 1; i < cameras.Length; i++) if (cameras[i] == ',') more++;
        string first = cameras.Substring(0, comma);
        return (first.Length <= 18 ? first : first.Substring(0, 17) + "…") + " +" + more;
    }

    static string ErrColor(float mm) => mm < 120f ? "#8ef08e" : mm < 200f ? "#f2d36b" : "#ff8a6b";
}
