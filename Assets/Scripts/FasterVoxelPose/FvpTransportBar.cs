using UnityEngine;
using UnityEngine.UI;

// Where the controls are on the screen. Pure geometry, shared by what is drawn and by what the pointer hits, so the two cannot
// disagree. Screen pixels, origin bottom-left (the input system's pointer convention and uGUI's).
public sealed class FvpBarLayout
{
    public enum Part { None, ToStart, Rewind, StepBack, PlayPause, StepForward, Replay, ToNewest, Slider, Counter, Mode, Hide, Show }
    public const int PartCount = 13;
    public const float Pad = 8f, ButtonH = 26f, Gap = 4f, Margin = 4f, ThumbW = 12f;

    // (part, width, space after) at scale 1
    static readonly (Part part, float width, float after)[] Row =
    {
        (Part.ToStart, 40f, Gap), (Part.Rewind, 40f, Gap), (Part.StepBack, 40f, Gap), (Part.PlayPause, 54f, Gap),
        (Part.StepForward, 40f, Gap), (Part.Replay, 40f, Gap), (Part.ToNewest, 40f, Gap + 8f),
        (Part.Slider, 240f, 8f), (Part.Counter, 80f, 0f), (Part.Mode, 112f, Gap), (Part.Hide, 44f, 0f),
    };

    public readonly Rect[] rect = new Rect[PartCount];
    public Rect strip;            // the dim backing, margin included
    public float scale = 1f;      // < 1 when the display is too narrow for the whole bar
    public bool expanded;
    public int version;           // bumped whenever the geometry changed

    int width = -1, height = -1;

    // Returns true when the geometry changed.
    public bool Compute(int w, int h, bool expanded)
    {
        if (w == width && h == height && expanded == this.expanded && version > 0) return false;
        width = w; height = h; this.expanded = expanded;
        System.Array.Clear(rect, 0, rect.Length);

        if (!expanded)
        {
            scale = 1f;
            rect[(int)Part.Show] = new Rect(Pad, Pad, 46f, ButtonH);
            strip = new Rect(Pad - Margin, Pad - Margin, 46f + 2f * Margin, ButtonH + 2f * Margin);
        }
        else
        {
            float total = 0f;
            foreach (var r in Row) total += r.width + r.after;
            scale = Mathf.Clamp((w - 2f * Pad) / total, 0.5f, 1f);
            float x = Pad, bh = ButtonH * scale;
            foreach (var r in Row)
            {
                rect[(int)r.part] = new Rect(x, Pad, r.width * scale, bh);
                x += (r.width + r.after) * scale;
            }
            strip = new Rect(Pad - Margin, Pad - Margin, x - Pad + 2f * Margin, bh + 2f * Margin);
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
        return Mathf.Lerp(s.xMin + half, s.xMax - half, t);
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
    readonly GameObject root;
    readonly Image strip, track, fill, thumb;
    readonly Image[] bg = new Image[FvpBarLayout.PartCount];
    readonly Text[] label = new Text[FvpBarLayout.PartCount];
    int appliedVersion;

    public FvpTransportBar(int display)
    {
        this.display = display;
        root = new GameObject($"FVP bar display {display + 1}") { hideFlags = HideFlags.HideAndDontSave };
        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 300;            // above the skeleton overlays (200 + view)
        canvas.targetDisplay = display;

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
            label[i] = FvpOverlay.NewText("Label", bg[i].transform, part == FvpBarLayout.Part.Counter ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter, 12, shadow: false); // on solid buttons a shadow only smudges
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
        if (appliedVersion != lay.version)
        {
            appliedVersion = lay.version;
            Place(strip.rectTransform, lay.strip);
            strip.enabled = true;
            // the children are positioned in the canvas' space, not the strip's: the strip only paints
            for (int i = 1; i < FvpBarLayout.PartCount; i++)
            {
                if (bg[i] == null) continue;
                bool on = lay.rect[i].width > 0f;
                if (bg[i].gameObject.activeSelf != on) bg[i].gameObject.SetActive(on);
                if (on) Place(bg[i].rectTransform, Local(lay.rect[i], lay.strip));
                if (on && label[i] != null) label[i].fontSize = Mathf.Max(9, Mathf.RoundToInt(12f * lay.scale));
            }
            Rect s = lay.rect[(int)FvpBarLayout.Part.Slider];
            bool slider = s.width > 0f;
            track.gameObject.SetActive(slider);
            fill.gameObject.SetActive(slider);
            thumb.gameObject.SetActive(slider);
            if (slider)
            {
                Rect t = Local(new Rect(s.xMin, s.center.y - 2f * lay.scale, s.width, 4f * lay.scale), lay.strip);
                Place(track.rectTransform, t);
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
            float tw = FvpBarLayout.ThumbW * lay.scale, th = (FvpBarLayout.ButtonH - 6f) * lay.scale;
            thumb.enabled = usable;
            fill.enabled = usable;
            Place(thumb.rectTransform, Local(new Rect(x - tw * 0.5f, sl.center.y - th * 0.5f, tw, th), lay.strip));
            float h = 4f * lay.scale;
            Place(fill.rectTransform, Local(new Rect(sl.xMin, sl.center.y - h * 0.5f - 0.0f, Mathf.Max(0f, x - sl.xMin), h), lay.strip));
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
