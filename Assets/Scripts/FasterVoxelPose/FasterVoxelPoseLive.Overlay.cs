using UnityEngine;

// What is shown while playing:
//
//   - the skeletons of the frame on show, drawn over each camera's own display (FvpOverlay, one canvas per camera on that
//     camera's Target Display, projected through the camera model that went to the server), with a status line per display;
//   - a slim transport bar at the bottom of Display 1 (IMGUI) and the hotkeys.
//
// Keys (Game view focused): Space pause / play, Left / Right step, R rewind, Home / End first / newest frame, M bar.
//
// The overlay sits on top of the live camera image. While live the skeleton is the newest estimate, a few frames
// behind the image; paused or rewound, the tracked Animators have been put back to that frame, so the camera image
// shows the same moment as the skeleton (anything not driven by those Animators will not match).
public partial class FasterVoxelPoseLive
{
    sealed class OverlayView
    {
        public Camera cam;
        public CalibratedCamera calib;
        public FvpOverlay overlay;
    }

    OverlayView[] overlayViews;
    GUIStyle barLabel;
    static readonly Color Dim = new Color(0f, 0f, 0f, 0.6f);

    // The overlay drawn on camera `view`'s display (null before the first frame).
    public FvpOverlay OverlayOf(int view) => overlayViews != null && view >= 0 && view < overlayViews.Length ? overlayViews[view].overlay : null;

    void LateUpdate()
    {
        if (Application.isPlaying) UpdateOverlays();
    }

    // ---------------- hotkeys ----------------

    void HandleHotkeys()
    {
        if (!enableHotkeys) return;
        Event e = Event.current;
        if (e.type != EventType.KeyDown) return;
        switch (e.keyCode)
        {
            case KeyCode.Space: TogglePause(); break;
            case KeyCode.RightArrow: StepForward(); break;
            case KeyCode.LeftArrow: StepBackward(); break;
            case KeyCode.R: ToggleRewind(); break;
            case KeyCode.Home: GoToStart(); break;
            case KeyCode.End: GoToNewest(); break;
            case KeyCode.M: showControls = !showControls; break;
            default: return;
        }
        e.Use();
    }

    // ---------------- transport bar ----------------

    void OnGUI()
    {
        if (!Application.isPlaying || !isActiveAndEnabled) return;
        HandleHotkeys();

        const float pad = 8f, h = 26f;
        float y = Screen.height - h - pad;
        if (!showControls)
        {
            if (GUI.Button(new Rect(pad, y, 46f, h), "FVP")) showControls = true;
            return;
        }
        if (barLabel == null)
            barLabel = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };

        const float bw = 40f, sliderW = 280f, labelW = 120f;
        float totalW = 7 * (bw + 4f) + 24f + 52f + sliderW + labelW + 8f;
        if (Event.current.type == EventType.Repaint)
        {
            GUI.color = Dim;
            GUI.DrawTexture(new Rect(pad - 4f, y - 4f, totalW + 8f, h + 8f), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        float x = pad;
        if (Button(ref x, y, bw, h, "|<", false)) GoToStart();
        if (Button(ref x, y, bw, h, "<<", autoDir < 0)) ToggleRewind();
        if (Button(ref x, y, bw, h, "<", false)) StepBackward();
        if (Button(ref x, y, bw + 14f, h, paused ? "Play" : "Pause", paused)) TogglePause();
        if (Button(ref x, y, bw, h, ">", false)) StepForward();
        if (Button(ref x, y, bw, h, ">>", autoDir > 0)) ToggleReplay();
        if (Button(ref x, y, bw, h, ">|", false)) GoToNewest();
        x += 8f;

        int last = Mathf.Max(0, history.Count - 1);
        GUI.enabled = history.Count > 1;
        GUI.changed = false;
        float v = GUI.HorizontalSlider(new Rect(x, y + 6f, sliderW, 14f), Mathf.Min(cursor, last), 0, Mathf.Max(1, last));
        if (GUI.changed) Scrub(Mathf.Clamp(Mathf.RoundToInt(v), 0, last));
        GUI.enabled = true;
        x += sliderW + 8f;

        GUI.Label(new Rect(x, y, labelW, h), history.Count > 0 ? $"{cursor + 1} / {history.Count}" : "-", barLabel);
        x += labelW;
        if (Button(ref x, y, 44f, h, "Hide", false)) showControls = false;
    }

    bool Button(ref float x, float y, float w, float h, string text, bool active)
    {
        Color prev = GUI.backgroundColor;
        if (active) GUI.backgroundColor = new Color(1f, 0.75f, 0.25f);
        bool hit = GUI.Button(new Rect(x, y, w, h), text);
        GUI.backgroundColor = prev;
        x += w + 4f;
        return hit;
    }

    // ---------------- overlays on the camera displays ----------------

    void UpdateOverlays()
    {
        if (!showOverlay || cameras == null || cameras.Count == 0) { HideOverlays(); return; }
        EnsureOverlayViews();

        FvpFrame f = IsReady ? displayed : null;
        for (int i = 0; i < overlayViews.Length; i++)
        {
            OverlayView v = overlayViews[i];
            if (v.cam == null) continue;

            // Presented through a CalibratedCamera's letterboxed canvas, or straight on the camera's display?
            bool letterbox = v.calib != null && v.calib.IsApplied && v.calib.DistortionActive && v.calib.showOnDisplay && v.calib.imageHeight > 0;
            if (v.overlay == null || v.overlay.letterboxed != letterbox)
            {
                v.overlay?.Destroy();
                float aspect = letterbox ? (float)v.calib.imageWidth / v.calib.imageHeight : 1f;
                v.overlay = new FvpOverlay($"FVP overlay {v.cam.name.Trim()}", 200 + i, letterbox, aspect);
            }

            FvpOverlay ov = v.overlay;
            ov.SetVisible(true);
            ov.SetDisplay(v.cam.targetDisplay);
            ov.graphic.lineWidth = overlayLineWidth;
            ov.graphic.dotSize = overlayDotSize;

            ov.graphic.Begin();
            int tag = 0;
            if (f != null)
                foreach (FvpPerson p in f.people)
                    DrawPerson(ov, v, f, i, p, ref tag);
            ov.graphic.End();
            ov.HideTagsFrom(tag);
            ov.SetLabel(OverlayLabel(v, f), paused ? new Color(1f, 0.75f, 0.25f) : new Color(0.75f, 1f, 0.8f));
        }
    }

    void DrawPerson(FvpOverlay ov, OverlayView v, FvpFrame f, int view, FvpPerson p, ref int tag)
    {
        var uv = new Vector2[FvpSkeleton.Count];
        var ok = new bool[FvpSkeleton.Count];
        for (int j = 0; j < FvpSkeleton.Count; j++)
            ok[j] = TryImagePoint(ov, v, f, view, p.joints[j], out uv[j]);

        Color c = p.Color;
        for (int e = 0; e < FvpSkeleton.Edges.GetLength(0); e++)
        {
            int a = FvpSkeleton.Edges[e, 0], b = FvpSkeleton.Edges[e, 1];
            if (ok[a] && ok[b]) ov.graphic.AddLine(uv[a], uv[b], c);
        }
        for (int j = 0; j < FvpSkeleton.Count; j++)
            if (ok[j]) ov.graphic.AddDot(uv[j], c);

        if (overlayTags && ok[FvpSkeleton.Neck])
            ov.Tag(tag++, uv[FvpSkeleton.Neck], $"#{p.id}  {p.score:F2}", c);
    }

    // A world point -> normalized position on the image the camera's display shows (0..1, origin top-left).
    bool TryImagePoint(FvpOverlay ov, OverlayView v, FvpFrame f, int view, Vector3 w, out Vector2 uv)
    {
        uv = default;
        if (ov.letterboxed)
        {
            // The calibrated image, through the camera model that was sent (K, pose, lens distortion).
            if (f.cameras == null || view >= f.cameras.Length) return false;
            FvpCameraModel m = f.cameras[view];
            if (!m.Project(w.x, w.y, w.z, out double px, out double py)) return false;
            // +0.5: pixel centres sit on integers in the camera model, the display draws the image edge to edge.
            uv = new Vector2((float)((px + 0.5) / m.width), (float)((py + 0.5) / m.height));
            return true;
        }

        Vector3 vp = v.cam.WorldToViewportPoint(w);
        if (vp.z <= v.cam.nearClipPlane) return false;
        uv = new Vector2(vp.x, 1f - vp.y);
        return uv.x > -0.5f && uv.x < 1.5f && uv.y > -0.5f && uv.y < 1.5f;
    }

    string OverlayLabel(OverlayView v, FvpFrame f)
    {
        string cam = v.cam.name.Trim();
        string state = paused ? (autoDir < 0 ? "REWIND" : autoDir > 0 ? "REPLAY" : "PAUSED") : "LIVE";
        if (f == null) return $"{cam}   {state}   {status}";
        string where = history.Count > 0 ? $"{cursor + 1}/{history.Count}" : "-";
        return $"{cam}   {state}   frame {where}   t = {f.sceneTime:F2} s   {f.people.Count} person(s)   {f.netMs:F0} ms   {estimateFps:F1}/s";
    }

    void EnsureOverlayViews()
    {
        bool same = overlayViews != null && overlayViews.Length == cameras.Count;
        if (same)
            for (int i = 0; i < overlayViews.Length; i++)
                if (overlayViews[i].cam != cameras[i]) { same = false; break; }
        if (same) return;

        DestroyOverlays();
        overlayViews = new OverlayView[cameras.Count];
        for (int i = 0; i < overlayViews.Length; i++)
        {
            overlayViews[i] = new OverlayView { cam = cameras[i] };
            if (cameras[i] != null) cameras[i].TryGetComponent(out overlayViews[i].calib);
        }
    }

    void HideOverlays()
    {
        if (overlayViews == null) return;
        foreach (OverlayView v in overlayViews) v.overlay?.SetVisible(false);
    }

    void DestroyOverlays()
    {
        if (overlayViews == null) return;
        foreach (OverlayView v in overlayViews) v.overlay?.Destroy();
        overlayViews = null;
    }
}
