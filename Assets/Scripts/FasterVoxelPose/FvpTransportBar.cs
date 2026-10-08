using UnityEngine;
using UnityEngine.UI;

// Where the controls are on the screen. Pure geometry, shared by what is drawn and by what the pointer hits, so the two cannot
// disagree. Screen pixels, origin bottom-left (the input system's pointer convention and uGUI's). Every edge is a whole pixel:
// text laid out on a fractional position is resampled and comes out blurry.
public sealed class FvpBarLayout
{
    public enum Part { None, ToStart, Rewind, StepBack, PlayPause, StepForward, Replay, ToNewest, Slider, Counter, Mode, Hide, Show }
    public const int PartCount = 13;
    public const float Pad = 8f, ButtonH = 28f, Gap = 4f, Margin = 4f, ThumbW = 12f;
    public const int FontSize = 14;

    // (part, width, space after) at scale 1
    static readonly (Part part, float width, float after)[] Row =
    {
        (Part.ToStart, 44f, Gap), (Part.Rewind, 44f, Gap), (Part.StepBack, 44f, Gap), (Part.PlayPause, 60f, Gap),
        (Part.StepForward, 44f, Gap), (Part.Replay, 44f, Gap), (Part.ToNewest, 44f, Gap + 8f),
        (Part.Slider, 240f, 8f), (Part.Counter, 86f, 0f), (Part.Mode, 128f, Gap), (Part.Hide, 50f, 0f),
    };
    const float ShowWidth = 50f;

    public readonly Rect[] rect = new Rect[PartCount];
    public Rect strip;            // the dim backing, margin included
    public float scale = 1f;      // whole multiple for big displays, then shrunk (in eighths) to fit a narrow one
    public float uiScale = 1f;    // the size of everything: the user's setting, doubled from 1620 px of height up (4K)
    public bool expanded;
    public int version;           // bumped whenever the geometry changed
    public int Font => Mathf.Max(11, Mathf.RoundToInt(FontSize * scale));

    int width = -1, height = -1;
    float userScale = -1f;

    static Rect R(float x, float y, float w, float h) => new Rect(Mathf.Round(x), Mathf.Round(y), Mathf.Round(w), Mathf.Round(h));

    // Returns true when the geometry changed.
    public bool Compute(int w, int h, bool expanded, float userScale = 1f)
    {
        userScale = Mathf.Clamp(userScale, 0.5f, 4f);
        if (w == width && h == height && expanded == this.expanded && userScale == this.userScale && version > 0) return false;
        width = w; height = h; this.expanded = expanded; this.userScale = userScale;
        System.Array.Clear(rect, 0, rect.Length);

        uiScale = userScale * Mathf.Max(1, Mathf.RoundToInt(h / 1080f));
        float pad = Pad * uiScale, margin = Margin * uiScale;
        if (!expanded)
        {
            scale = uiScale;
            rect[(int)Part.Show] = R(pad, pad, ShowWidth * scale, ButtonH * scale);
            strip = R(pad - margin, pad - margin, rect[(int)Part.Show].width + 2f * margin, rect[(int)Part.Show].height + 2f * margin);
        }
        else
        {
            float total = 0f;
            foreach (var r in Row) total += r.width + r.after;
            float fit = Mathf.Clamp((w - 2f * pad) / (total * uiScale), 0.5f, 1f);
            fit = Mathf.Floor(fit * 8f) / 8f;
            scale = uiScale * fit;
            float x = pad, bh = Mathf.Round(ButtonH * scale);
            foreach (var r in Row)
            {
                rect[(int)r.part] = R(x, pad, r.width * scale, bh);
                x += Mathf.Round((r.width + r.after) * scale);
            }
            strip = R(pad - margin, pad - margin, x - pad + 2f * margin, bh + 2f * margin);
        }
        version++;
        return true;
    }

    public bool Contains(Vector2 p) => strip.Contains(p);

    public Part PartAt(Vector2 p)
    {
        for (int i = 1; i < PartCount; i++)
            if ((Part)i != Part.Counter && rect[i].width > 0f && rect[i].Contains(p)) return (Part)i;
        return Part.None;
    }

    // The slider's thumb centre for a cursor in a history of `count` frames, and the frame a pointer x selects.
    public float ThumbX(int cursor, int count)
    {
        Rect s = rect[(int)Part.Slider];
        float half = ThumbW * scale * 0.5f;
        float t = count > 1 ? Mathf.Clamp01(cursor / (float)(count - 1)) : 0f;
        return Mathf.Round(Mathf.Lerp(s.xMin + half, s.xMax - half, t));
    }

    public int IndexAt(float x, int count)
    {
        Rect s = rect[(int)Part.Slider];
        float half = ThumbW * scale * 0.5f;
        float t = Mathf.Clamp01(Mathf.InverseLerp(s.xMin + half, s.xMax - half, x));
        return Mathf.Clamp(Mathf.RoundToInt(t * (count - 1)), 0, Mathf.Max(0, count - 1));
    }
}

// What the bar has to show this frame.
public struct FvpBarView
{
    public bool paused, synced;
    public int autoDir;                 // -1 rewinding, +1 replaying
    public int cursor, count;
    public FvpBarLayout.Part hover, down;

    public bool Equals(in FvpBarView o) =>
        paused == o.paused && synced == o.synced && autoDir == o.autoDir && cursor == o.cursor && count == o.count && hover == o.hover && down == o.down;
}

// The transport bar of ONE display, drawn as uGUI. IMGUI only ever draws on Display 1, so the component makes one of these per
// display and the controls follow whichever display the Game view shows. It is only drawn: the pointer is read through the
// input system by the component and hit-tested against the same FvpBarLayout (no EventSystem needed).
public sealed class FvpTransportBar
{
    static readonly Color Dim = new Color(0f, 0f, 0f, 0.6f);
    static readonly Color Normal = new Color(0.22f, 0.24f, 0.27f, 0.96f), Hover = new Color(0.34f, 0.37f, 0.43f, 0.98f),
                          Pressed = new Color(0.12f, 0.13f, 0.15f, 1f), Active = new Color(1f, 0.75f, 0.25f, 0.98f),
                          TrackColor = new Color(1f, 1f, 1f, 0.28f), FillColor = new Color(1f, 0.6f, 0.18f, 0.9f), ThumbColor = new Color(0.92f, 0.92f, 0.92f, 1f);

    public readonly Canvas canvas;
    public readonly int display;
    public readonly FvpHud hud;
    public readonly FvpHeatPanel heat;
    readonly GameObject root;
    readonly Image strip, track, fill, thumb;
    readonly Image[] bg = new Image[FvpBarLayout.PartCount];
    readonly Text[] label = new Text[FvpBarLayout.PartCount];
    int appliedVersion;
    bool appliedAny;
    FvpBarView appliedView;

    public FvpTransportBar(int display)
    {
        this.display = display;
        root = new GameObject($"FVP bar display {display + 1}") { hideFlags = HideFlags.HideAndDontSave };
        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 300;            // above the skeleton overlays (200 + view)
        canvas.targetDisplay = display;
        canvas.pixelPerfect = true;           // snap UI elements and glyphs to whole pixels

        hud = new FvpHud(root.transform);
        heat = new FvpHeatPanel(root.transform);

        strip = NewImage("Strip", root.transform, Dim);
        track = NewImage("Track", strip.transform, TrackColor);
        fill = NewImage("Fill", strip.transform, FillColor);
        thumb = NewImage("Thumb", strip.transform, ThumbColor);

        string[] names = { "", "|<", "<<", "<", "Pause", ">", ">>", ">|", "", "", "Mode", "Hide", "FVP" };
        for (int i = 1; i < FvpBarLayout.PartCount; i++)
        {
            var part = (FvpBarLayout.Part)i;
            if (part == FvpBarLayout.Part.Slider) continue;
            bg[i] = NewImage(part.ToString(), strip.transform, Normal);
            label[i] = FvpOverlay.NewText("Label", bg[i].transform, part == FvpBarLayout.Part.Counter ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter, FvpBarLayout.FontSize, shadow: false);
            Fill(label[i].rectTransform);
            label[i].text = names[i];
            if (part == FvpBarLayout.Part.Counter) bg[i].enabled = false; // text only
        }
        // The slider's reach is a plain rectangle, so the thumb and fill sit over the track
        thumb.transform.SetAsLastSibling();
    }

    static Image NewImage(string name, Transform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        var rt = img.rectTransform;
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = Vector2.zero;
        return img;
    }

    static void Fill(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    static void Place(RectTransform rt, Rect r)
    {
        rt.anchoredPosition = r.position;
        rt.sizeDelta = r.size;
    }

    public void SetVisible(bool visible) { if (canvas.enabled != visible) canvas.enabled = visible; }

    public void Apply(FvpBarLayout lay, in FvpBarView v)
    {
        // Nothing changed since the last frame: leave every uGUI element alone (each write can dirty the canvas).
        if (appliedAny && appliedVersion == lay.version && appliedView.Equals(v)) return;
        appliedAny = true;
        appliedView = v;

        if (appliedVersion != lay.version)
        {
            appliedVersion = lay.version;
            Place(strip.rectTransform, lay.strip);
            strip.enabled = true;
            // the children are positioned relative to the strip's corner: the strip only paints
            for (int i = 1; i < FvpBarLayout.PartCount; i++)
            {
                if (bg[i] == null) continue;
                bool on = lay.rect[i].width > 0f;
                if (bg[i].gameObject.activeSelf != on) bg[i].gameObject.SetActive(on);
                if (on) Place(bg[i].rectTransform, Local(lay.rect[i], lay.strip));
                if (on && label[i] != null) label[i].fontSize = lay.Font;
            }
            Rect s = lay.rect[(int)FvpBarLayout.Part.Slider];
            bool slider = s.width > 0f;
            track.gameObject.SetActive(slider);
            fill.gameObject.SetActive(slider);
            thumb.gameObject.SetActive(slider);
            if (slider)
            {
                float th = Mathf.Max(2f, Mathf.Round(4f * lay.scale / 2f) * 2f); // even, so it centres on a pixel edge
                Place(track.rectTransform, Local(new Rect(s.xMin, Mathf.Round(s.center.y - th * 0.5f), s.width, th), lay.strip));
            }
        }

        for (int i = 1; i < FvpBarLayout.PartCount; i++)
        {
            if (bg[i] == null || lay.rect[i].width <= 0f) continue;
            var part = (FvpBarLayout.Part)i;
            bool active = part == FvpBarLayout.Part.PlayPause ? v.paused
                        : part == FvpBarLayout.Part.Rewind ? v.autoDir < 0
                        : part == FvpBarLayout.Part.Replay ? v.autoDir > 0 : false;
            Color c = active ? Active : v.down == part && v.hover == part ? Pressed : v.hover == part ? Hover : Normal;
            if (part != FvpBarLayout.Part.Counter && bg[i].color != c) bg[i].color = c;
            Color tc = active ? new Color(0.1f, 0.08f, 0.02f) : Color.white;
            if (label[i].color != tc) label[i].color = tc;

            string text = part == FvpBarLayout.Part.PlayPause ? (v.paused ? "Play" : "Pause")
                        : part == FvpBarLayout.Part.Mode ? (v.synced ? "Mode: synced" : "Mode: real-time")
                        : part == FvpBarLayout.Part.Counter ? (v.count > 0 ? $"{v.cursor + 1} / {v.count}" : "-")
                        : null;
            if (text != null && label[i].text != text) label[i].text = text;
        }

        Rect sl = lay.rect[(int)FvpBarLayout.Part.Slider];
        if (sl.width > 0f)
        {
            bool usable = v.count > 1;
            float x = lay.ThumbX(v.cursor, v.count);
            float tw = Mathf.Round(FvpBarLayout.ThumbW * lay.scale), th = Mathf.Round((FvpBarLayout.ButtonH - 6f) * lay.scale);
            thumb.enabled = usable;
            fill.enabled = usable;
            Place(thumb.rectTransform, Local(new Rect(Mathf.Round(x - tw * 0.5f), Mathf.Round(sl.center.y - th * 0.5f), tw, th), lay.strip));
            float h = Mathf.Max(2f, Mathf.Round(4f * lay.scale / 2f) * 2f);
            Place(fill.rectTransform, Local(new Rect(sl.xMin, Mathf.Round(sl.center.y - h * 0.5f), Mathf.Max(0f, x - sl.xMin), h), lay.strip));
            Color tcol = TrackColor; if (!usable) tcol.a *= 0.4f;
            if (track.color != tcol) track.color = tcol;
        }
    }

    // The children hang off the strip, so rects in screen space are expressed relative to its corner.
    static Rect Local(Rect screen, Rect strip) => new Rect(screen.position - strip.position, screen.size);

    public void Destroy()
    {
        if (root == null) return;
        if (Application.isPlaying) Object.Destroy(root); else Object.DestroyImmediate(root);
    }
}
