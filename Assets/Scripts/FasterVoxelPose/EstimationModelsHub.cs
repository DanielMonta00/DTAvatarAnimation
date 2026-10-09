using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// The parent of the estimation models. Arrange the scene like this - each child is another model running on the cameras:
//
//     EstimationModels            <- this component
//         FasterVoxelPose          (FasterVoxelPoseLive: 3D, multi-view; hosts the camera capture, the history and the transport)
//         ViTPose                  (ViTPoseLive: 2D, each view on its own)
//
// The hub finds the children that implement IEstimationModel by itself and puts a tab for each at the top of every display:
//   click a tab        select it: its corner panels (status card, 2D heatmaps) are the ones shown
//   click its swatch   show / hide its skeletons over the camera displays (every model's skeletons are drawn together otherwise,
//                      in their own colours: green = ground truth, orange = FasterVoxelPose, cyan = ViTPose)
//   T                  next model       J / Shift+J, G   next / previous joint and hide for the selected model's heatmaps
// Transport (pause, step, rewind, the bar at the bottom) stays the one of FasterVoxelPose and covers every model.
public sealed class EstimationModelsHub : MonoBehaviour
{
    [Tooltip("The strip of tabs at the top of every display.")]
    public bool showTabs = true;
    [Tooltip("Which model's corner panels are shown (index in the list below). Click a tab or press T to change it while playing.")]
    public int selected;
    [Range(0.75f, 3f)]
    [Tooltip("Size of the tabs. Text is only sharp when the Game view shows one render pixel per screen pixel (Scale 1x, Low Resolution Aspect Ratios off).")]
    public float uiScale = 1f;
    [Tooltip("T cycles the models, J / Shift+J / G drive the selected model's heatmaps. Needs the Game view focused.")]
    public bool enableKeys = true;

    readonly List<IEstimationModel> models = new List<IEstimationModel>();
    readonly Dictionary<IEstimationModel, bool> overlayOn = new Dictionary<IEstimationModel, bool>();
    float nextScan;

    public IReadOnlyList<IEstimationModel> Models => models;
    public FasterVoxelPoseLive Fvp { get; private set; }
    public IEstimationModel Selected => selected >= 0 && selected < models.Count ? models[selected] : null;

    // The next model with panels at or after `index` (a tab without panels, the ground truth, cannot be selected).
    public void Select(int index)
    {
        int n = models.Count;
        if (n == 0) return;
        for (int k = 0; k < n; k++)
        {
            int i = (((index + k) % n) + n) % n;
            if (models[i].HasPanels) { selected = i; return; }
        }
    }

    void SelectNext()
    {
        int n = models.Count;
        for (int k = 1; k <= n; k++)
        {
            int i = (selected + k) % n;
            if (models[i].HasPanels) { selected = i; return; }
        }
    }

    public bool OverlayOn(IEstimationModel m) => !overlayOn.TryGetValue(m, out bool on) || on;
    public void SetOverlay(IEstimationModel m, bool on) => overlayOn[m] = on;

    // ---------------- lifecycle ----------------

    void OnEnable()
    {
        Rescan();
        if (!Application.isPlaying) return;
        FasterVoxelPoseLive.ExtraPointerBlocker = BlocksPointer;
        FasterVoxelPoseLive.StepJointOverride = d => Selected?.StepJoint(d);
        FasterVoxelPoseLive.ToggleHeatmapsOverride = () => Selected?.ToggleHeatmaps();
    }

    void OnDisable()
    {
        foreach (IEstimationModel m in models)
            if (m != null) { m.PanelsVisible = true; m.OverlayVisible = true; }
        if (FasterVoxelPoseLive.ExtraPointerBlocker == (Func<Vector2, bool>)BlocksPointer) FasterVoxelPoseLive.ExtraPointerBlocker = null;
        FasterVoxelPoseLive.StepJointOverride = null;
        FasterVoxelPoseLive.ToggleHeatmapsOverride = null;
        DestroyStrips();
    }

    // The models are the children of this object that implement IEstimationModel (found again every few seconds, so one added
    // while playing joins the tabs).
    void Rescan()
    {
        var found = GetComponentsInChildren<IEstimationModel>(true);
        models.Clear();
        foreach (IEstimationModel m in found) if (m != null) models.Add(m);
        Fvp = GetComponentInChildren<FasterVoxelPoseLive>(true);
        if (Fvp != null) models.Add(Fvp.GroundTruthEntry);                 // the ground truth is a tab of its own, last
        if (selected >= models.Count || selected < 0 || !models[selected].HasPanels) { selected = 0; Select(0); }
    }

    void Update()
    {
        if (!Application.isPlaying) return;
        if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 2f; int before = models.Count; Rescan(); if (models.Count != before) tabsDirty = true; }

        for (int i = 0; i < models.Count; i++)
        {
            models[i].PanelsVisible = i == selected && models[i].HasPanels;
            models[i].OverlayVisible = OverlayOn(models[i]);
        }
        PollInput();
    }

    void LateUpdate()
    {
        if (Application.isPlaying) UpdateTabs();
    }

    // ---------------- layout (screen pixels, origin bottom-left) ----------------

    public const float TabW = 150f, TabH = 28f, SwatchW = 26f, Gap = 6f, Margin = 8f;
    Rect[] tabRect = Array.Empty<Rect>(), swatchRect = Array.Empty<Rect>();
    int laidOutW = -1, laidOutH = -1, laidOutCount = -1;
    float laidOutScale = -1f;
    bool tabsDirty = true;

    public float Scale => uiScale * FvpBarLayout.BaseScale * Mathf.Max(1, Mathf.RoundToInt(Screen.height / 1080f));
    public Rect TabRect(int i) { EnsureLayout(); return i >= 0 && i < tabRect.Length ? tabRect[i] : default; }
    public Rect SwatchRect(int i) { EnsureLayout(); return i >= 0 && i < swatchRect.Length ? swatchRect[i] : default; }

    void EnsureLayout()
    {
        float u = Scale;
        if (laidOutW == Screen.width && laidOutH == Screen.height && laidOutCount == models.Count && laidOutScale == u) return;
        laidOutW = Screen.width; laidOutH = Screen.height; laidOutCount = models.Count; laidOutScale = u;
        int n = models.Count;
        tabRect = new Rect[n]; swatchRect = new Rect[n];
        float w = Mathf.Round(TabW * u), h = Mathf.Round(TabH * u), gap = Mathf.Round(Gap * u), sw = Mathf.Round(SwatchW * u);
        float total = n * w + Mathf.Max(0, n - 1) * gap;
        float x = Mathf.Round((Screen.width - total) * 0.5f), y = Screen.height - Mathf.Round(Margin * u) - h;
        for (int i = 0; i < n; i++)
        {
            tabRect[i] = new Rect(x, y, w, h);
            swatchRect[i] = new Rect(x, y, sw, h);
            x += w + gap;
        }
        tabsDirty = true;
    }

    // True when a pointer position is on a tab: FasterVoxelPose must not read that press as a click on the picture.
    bool BlocksPointer(Vector2 p)
    {
        if (!isActiveAndEnabled || !showTabs || models.Count == 0) return false;
        EnsureLayout();
        for (int i = 0; i < tabRect.Length; i++) if (tabRect[i].Contains(p)) return true;
        return false;
    }

    int TabAt(Vector2 p)
    {
        if (!showTabs) return -1;
        EnsureLayout();
        for (int i = 0; i < tabRect.Length; i++) if (tabRect[i].Contains(p)) return i;
        return -1;
    }

    // ---------------- input ----------------

    bool prevButton, prevT;
    int pressedTab = -1;
    bool pressedSwatch;
    int hoverTab = -1;

    void PollInput()
    {
#if ENABLE_INPUT_SYSTEM
        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            bool held = mouse.leftButton.isPressed;
            bool pressed = mouse.leftButton.wasPressedThisFrame || (held && !prevButton);
            bool released = mouse.leftButton.wasReleasedThisFrame || (!held && prevButton);
            prevButton = held;
            HandlePointer(mouse.position.ReadValue(), pressed, released);
        }
        Keyboard kb = Keyboard.current;
        if (kb != null && enableKeys)
        {
            bool t = kb.tKey.isPressed || kb.tKey.wasPressedThisFrame;
            if (t && !prevT) SelectNext();
            prevT = t;
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        HandlePointer(Input.mousePosition, Input.GetMouseButtonDown(0), Input.GetMouseButtonUp(0));
        if (enableKeys && Input.GetKeyDown(KeyCode.T)) SelectNext();
#endif
    }

    // Public so a test can feed it events (screen pixels, origin bottom-left).
    public void HandlePointer(Vector2 pos, bool pressed, bool released)
    {
        int tab = TabAt(pos);
        if (hoverTab != tab) { hoverTab = tab; tabsDirty = true; }
        if (pressed && tab >= 0)
        {
            pressedTab = tab;
            pressedSwatch = swatchRect[tab].Contains(pos);
        }
        if (released)
        {
            if (pressedTab >= 0 && pressedTab == tab && pressedTab < models.Count)
            {
                if ((pressedSwatch && swatchRect[tab].Contains(pos)) || !models[tab].HasPanels) SetOverlay(models[tab], !OverlayOn(models[tab]));
                else Select(tab);
                tabsDirty = true;
            }
            pressedTab = -1;
        }
    }

    // ---------------- drawing: one strip of tabs per display ----------------

    sealed class Strip
    {
        public readonly Canvas canvas;
        readonly GameObject root;
        readonly List<Image> bg = new List<Image>(), swatch = new List<Image>(), underline = new List<Image>();
        readonly List<Text> label = new List<Text>();

        public Strip(int display)
        {
            root = new GameObject($"Estimation models tabs display {display + 1}") { hideFlags = HideFlags.HideAndDontSave };
            canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 330;
            canvas.targetDisplay = display;
            canvas.pixelPerfect = true;
        }

        static Image NewImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            img.rectTransform.anchorMin = img.rectTransform.anchorMax = Vector2.zero;
            img.rectTransform.pivot = Vector2.zero;
            return img;
        }

        static void Place(RectTransform rt, Rect r)
        {
            rt.anchoredPosition = r.position;
            rt.sizeDelta = r.size;
        }

        public void Apply(EstimationModelsHub hub, bool visible, float u)
        {
            if (canvas.enabled != visible) canvas.enabled = visible;
            if (!visible) return;
            int n = hub.models.Count;
            while (bg.Count < n)
            {
                Image b = NewImage("Tab", root.transform, Color.black);
                swatch.Add(NewImage("Swatch", b.transform, Color.white));
                underline.Add(NewImage("Underline", b.transform, Color.white));
                Text t = FvpOverlay.NewText("Label", b.transform, TextAnchor.MiddleLeft, 14, shadow: false);
                label.Add(t);
                bg.Add(b);
            }
            int font = Mathf.Max(11, Mathf.RoundToInt(14f * u));
            for (int i = 0; i < bg.Count; i++)
            {
                bool on = i < n;
                if (bg[i].gameObject.activeSelf != on) bg[i].gameObject.SetActive(on);
                if (!on) continue;
                IEstimationModel m = hub.models[i];
                Rect r = hub.TabRect(i), s = hub.SwatchRect(i);
                bool sel = i == hub.selected && m.HasPanels, hover = i == hub.hoverTab;
                Place(bg[i].rectTransform, r);
                bg[i].color = sel ? new Color(0.24f, 0.27f, 0.33f, 0.97f) : hover ? new Color(0.2f, 0.23f, 0.28f, 0.95f) : new Color(0.12f, 0.14f, 0.17f, 0.9f);

                Color mc = m.ModelColor;
                bool ov = hub.OverlayOn(m);
                Place(swatch[i].rectTransform, new Rect(Mathf.Round(6f * u), Mathf.Round((r.height - 12f * u) * 0.5f), Mathf.Round(12f * u), Mathf.Round(12f * u)));
                swatch[i].color = ov ? mc : new Color(mc.r, mc.g, mc.b, 0.22f);

                float uh = Mathf.Max(2f, Mathf.Round(3f * u));
                Place(underline[i].rectTransform, new Rect(0f, 0f, r.width, uh));
                underline[i].color = sel ? mc : new Color(mc.r, mc.g, mc.b, 0f);

                RectTransform lr = label[i].rectTransform;
                lr.anchorMin = lr.anchorMax = Vector2.zero; lr.pivot = Vector2.zero;
                lr.anchoredPosition = new Vector2(s.width, 0f);
                lr.sizeDelta = new Vector2(r.width - s.width, r.height);
                if (label[i].fontSize != font) label[i].fontSize = font;
                string name = m.ModelName + (m.IsRunning ? "" : "  (off)");
                if (label[i].text != name) label[i].text = name;
                label[i].color = sel ? Color.white : new Color(0.72f, 0.77f, 0.83f);
            }
        }

        public void Destroy()
        {
            if (root == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(root); else UnityEngine.Object.DestroyImmediate(root);
        }
    }

    Strip[] strips;
    int appliedSelected = -1;

    public Canvas TabsCanvasOn(int display) => strips != null && display >= 0 && display < strips.Length ? strips[display].canvas : null;

    void UpdateTabs()
    {
        if (strips == null)
        {
            strips = new Strip[8];
            for (int d = 0; d < strips.Length; d++) strips[d] = new Strip(d);
            tabsDirty = true;
        }
        EnsureLayout();
        if (appliedSelected != selected) { appliedSelected = selected; tabsDirty = true; }
        // the swatches and names change with the models' state: cheap to keep current, but only touch uGUI when something changed
        int sig = models.Count * 31 + selected;
        foreach (IEstimationModel m in models) sig = sig * 7 + (OverlayOn(m) ? 1 : 0) + (m.IsRunning ? 2 : 0);
        if (sig != tabSignature) { tabSignature = sig; tabsDirty = true; }
        if (!tabsDirty) return;
        tabsDirty = false;
        bool visible = showTabs && models.Count > 0;
        foreach (Strip s in strips) s.Apply(this, visible, Scale);
    }

    int tabSignature;

    void DestroyStrips()
    {
        if (strips == null) return;
        foreach (Strip s in strips) s.Destroy();
        strips = null;
    }
}
