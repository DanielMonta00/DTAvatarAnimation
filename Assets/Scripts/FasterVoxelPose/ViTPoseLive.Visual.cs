using System.Collections.Generic;
using System.Text;
using UnityEngine;

// What ViTPose shows: its 2D skeletons over the camera displays, a status card and its heatmaps in the corners (when its tab is the
// selected one in the hub). Everything is read from the frame the transport of FasterVoxelPose shows: live, the newest result;
// paused, rewound or on a saved image, the result attached to that frame.
public sealed partial class ViTPoseLive
{
    sealed class View
    {
        public Camera cam;
        public CalibratedCamera calib;
        public FvpOverlay overlay;
    }

    View[] views;
    FvpModelPanels[] panels;
    readonly FvpHeatTextures heatTextures = new FvpHeatTextures();
    readonly FvpHudData hudData = new FvpHudData();
    readonly FvpHeatData heatData = new FvpHeatData();
    readonly StringBuilder hudLabels = new StringBuilder(), hudValues = new StringBuilder();
    float nextHudTime;
    int hudHash, heatKey;
    static Color[] jointColors;

    public FvpHudData HudData => hudData;
    public FvpHeatData HeatData => heatData;
    // For tests and tools: the panels drawn on a display (0-based), the overlay of a view.
    public FvpModelPanels PanelsOn(int display) => panels != null && display >= 0 && display < panels.Length ? panels[display] : null;
    public FvpOverlay OverlayOf(int view) => views != null && view >= 0 && view < views.Length ? views[view].overlay : null;

    void LateUpdate()
    {
        if (!Application.isPlaying || host_ == null) return;
        UpdateOverlays();
        RefreshHud();
        RefreshHeat();
        UpdatePanels();
    }

    // The result belonging to what the transport shows: the newest when live, the one attached to the shown frame otherwise.
    ViTPoseResult FrameResult()
    {
        FvpFrame shown = host_ != null ? host_.DisplayedFrame : null;
        if (shown != null && shown.placeholder) shown = host_.StaleFrame;     // a moment without results of its own: the nearest frame's, drawn dim
        return shown != null && shown.modelResults.TryGetValue(ResultKey, out object o) ? o as ViTPoseResult : null;
    }

    ViTPoseResult ShownResult() => host_ == null || host_.IsLive ? latest : FrameResult();

    // ---------------- overlay ----------------

    void EnsureViews()
    {
        List<Camera> cams = host_.cameras;
        bool same = views != null && views.Length == cams.Count;
        if (same)
            for (int i = 0; i < views.Length; i++)
                if (views[i].cam != cams[i]) { same = false; break; }
        if (same) return;

        DestroyOverlays();
        views = new View[cams.Count];
        for (int i = 0; i < views.Length; i++)
        {
            views[i] = new View { cam = cams[i] };
            if (cams[i] != null) cams[i].TryGetComponent(out views[i].calib);
        }
    }

    void UpdateOverlays()
    {
        if (!showOverlay || !OverlayVisible || host_.cameras == null || host_.cameras.Count == 0) { HideOverlays(); return; }
        EnsureViews();

        ViTPoseResult frameResult = FrameResult();
        bool live = host_.IsLive;
        for (int i = 0; i < views.Length; i++)
        {
            View v = views[i];
            if (v.cam == null) continue;

            // presented through a CalibratedCamera's letterboxed canvas, or straight on the camera's display (like FasterVoxelPose's overlay)
            bool letterbox = v.calib != null && v.calib.IsApplied && v.calib.DistortionActive && v.calib.showOnDisplay && v.calib.imageHeight > 0;
            if (v.overlay == null || v.overlay.letterboxed != letterbox)
            {
                v.overlay?.Destroy();
                float aspect = letterbox ? (float)v.calib.imageWidth / v.calib.imageHeight : 1f;
                v.overlay = new FvpOverlay($"ViTPose overlay {v.cam.name.Trim()}", 210 + i, letterbox, aspect);
            }
            FvpOverlay ov = v.overlay;
            ov.SetVisible(true);
            ov.SetDisplay(v.cam.targetDisplay);
            ov.graphic.lineWidth = overlayLineWidth;
            ov.graphic.dotRadius = overlayDotRadius;

            // on a saved image or while browsing, the result of THAT frame; on the live image, the newest
            ViTPoseResult r = host_.ShowsSavedImage(i) || !live ? frameResult : latest;
            ov.graphic.Begin();
            ov.graphic.alpha = host_.ShowsMoment ? 0.4f : 1f;
            if (r != null)
            {
                if (show2DSkeletons)
                    for (int k = 0; k < r.people.Count; k++)
                        if (r.people[k].view == i) DrawPerson(ov, r, r.people[k]);
                if (showTriangulated && r.cameras != null && i < r.cameras.Length)
                    for (int k = 0; k < r.people3d.Count; k++) DrawLifted(ov, r.cameras[i], r.people3d[k]);
            }
            ov.graphic.End();
        }
    }

    void DrawPerson(FvpOverlay ov, ViTPoseResult r, ViTPosePerson p)
    {
        if (jointColors == null)
        {
            jointColors = new Color[ViTPoseSkeleton.Count];
            for (int j = 0; j < jointColors.Length; j++) jointColors[j] = ViTPoseSkeleton.ColorFor(j);
        }
        float w = Mathf.Max(1, r.width), h = Mathf.Max(1, r.height);   // joints are in pixels of the sent frame, centres on integers

        if (showBoxes)
        {
            var a = new Vector2((p.x1 + 0.5f) / w, (p.y1 + 0.5f) / h);
            var b = new Vector2((p.x2 + 0.5f) / w, (p.y1 + 0.5f) / h);
            var c = new Vector2((p.x2 + 0.5f) / w, (p.y2 + 0.5f) / h);
            var d = new Vector2((p.x1 + 0.5f) / w, (p.y2 + 0.5f) / h);
            Color box = new Color(0.3f, 0.85f, 1f, 0.55f);
            ov.graphic.AddLine(a, b, box); ov.graphic.AddLine(b, c, box); ov.graphic.AddLine(c, d, box); ov.graphic.AddLine(d, a, box);
        }
        for (int e = 0; e < ViTPoseSkeleton.Edges.GetLength(0); e++)
        {
            int a = ViTPoseSkeleton.Edges[e, 0], b = ViTPoseSkeleton.Edges[e, 1];
            if (p.kp[a].z < keypointThreshold || p.kp[b].z < keypointThreshold) continue;
            ov.graphic.AddLine(new Vector2((p.kp[a].x + 0.5f) / w, (p.kp[a].y + 0.5f) / h), new Vector2((p.kp[b].x + 0.5f) / w, (p.kp[b].y + 0.5f) / h),
                               Color.Lerp(jointColors[a], jointColors[b], 0.5f));
        }
        for (int j = 0; j < ViTPoseSkeleton.Count; j++)
            if (p.kp[j].z >= keypointThreshold) ov.graphic.AddDot(new Vector2((p.kp[j].x + 0.5f) / w, (p.kp[j].y + 0.5f) / h), jointColors[j]);
    }

    static readonly Vector2[] liftUv = new Vector2[FvpSkeleton.Count];
    static readonly bool[] liftOk = new bool[FvpSkeleton.Count];

    // A lifted 3D person projected back into a view through that camera's lens model, the way FasterVoxelPose's skeletons are.
    void DrawLifted(FvpOverlay ov, FvpCameraModel cam, FvpPerson p)
    {
        float w = Mathf.Max(1, cam.width), h = Mathf.Max(1, cam.height);
        for (int j = 0; j < FvpSkeleton.Count; j++)
        {
            double u = 0, v = 0;
            liftOk[j] = p.IsValid(j) && cam.Project(p.joints[j].x, p.joints[j].y, p.joints[j].z, out u, out v);
            if (liftOk[j]) liftUv[j] = new Vector2((float)((u + 0.5) / w), (float)((v + 0.5) / h));
        }
        for (int e = 0; e < FvpSkeleton.Edges.GetLength(0); e++)
        {
            int a = FvpSkeleton.Edges[e, 0], b = FvpSkeleton.Edges[e, 1];
            if (liftOk[a] && liftOk[b]) ov.graphic.AddLine(liftUv[a], liftUv[b], Color.Lerp(LiftedColor(a), LiftedColor(b), 0.5f));
        }
        for (int j = 0; j < FvpSkeleton.Count; j++)
            if (liftOk[j]) ov.graphic.AddDot(liftUv[j], LiftedColor(j));
    }

    void HideOverlays()
    {
        if (views == null) return;
        foreach (View v in views) v.overlay?.SetVisible(false);
    }

    void DestroyOverlays()
    {
        if (views == null) return;
        foreach (View v in views) v.overlay?.Destroy();
        views = null;
    }

    // ---------------- status card ----------------

    // A few times a second; the card only lays itself out again when what it shows changed.
    void RefreshHud()
    {
        hudData.visible = showHud && PanelsVisible;
        if (!hudData.visible) { hudData.stamp++; return; }
        float now = Time.unscaledTime;
        if (now < nextHudTime) return;
        nextHudTime = now + 0.25f;

        ViTPoseResult r = ShownResult();
        string state; Color stateColor;
        if (!linkUp) { state = "WAITING"; stateColor = new Color(0.7f, 0.75f, 0.8f); }
        else if (host_.IsPaused) { state = host_.ShowsMoment ? "PAUSED · result of a nearby frame" : "PAUSED"; stateColor = new Color(1f, 0.82f, 0.45f); }
        else if (!host_.IsLive) { state = "BEHIND"; stateColor = new Color(1f, 0.82f, 0.45f); }
        else { state = "LIVE"; stateColor = new Color(0.55f, 0.95f, 0.6f); }
        hudData.state = state; hudData.stateColor = stateColor;
        hudData.legend = "<color=#4dd9ff>■</color> ViTPose    <color=#9aa7b5>YOLOv8 + ViTPose-B, 2D, each view on its own</color>";
        hudData.people.Clear();
        hudData.minScore = keypointThreshold;
        hudData.scoreFullScale = 1f;
        hudLabels.Length = 0; hudValues.Length = 0;

        if (r == null)
        {
            hudLabels.Append("status");
            hudValues.Append(string.IsNullOrEmpty(status) ? Summary : status);
            hudData.warn = serverError != null ? "Cannot start the server:\n" + serverError : "";
            hudData.footer = "";
        }
        else
        {
            hudLabels.Append("estimates\nlatency\nframe\n2D\n3D");
            hudValues.Append(estimateFps.ToString("F1")).Append(" /s   ·   took ").Append(framesTaken).Append(" of ").Append(framesTaken + framesMissed)
                     .Append(" captured frames").Append(framesMissed > 0 ? " (" + framesMissed + " missed while busy)" : "").Append('\n');
            hudValues.Append(r.latencyMs.ToString("F0")).Append(" ms from capture to answer   ·   detector ").Append(r.detMs.ToString("F0"))
                     .Append(" + ViTPose ").Append(r.poseMs.ToString("F0")).Append(" ms").Append(lift3D ? "  + lifting " + r.liftMs.ToString("F1") + " ms" : "").Append('\n');
            hudValues.Append("t = ").Append(r.sceneTime.ToString("F2")).Append(" s   ·   frame ").Append(r.frameId).Append('\n');

            float err2d = 0f; int err2dN = 0;
            for (int i = 0; i < r.people.Count; i++) if (r.people[i].errPx >= 0f) { err2d += r.people[i].errPx; err2dN++; }
            hudValues.Append(r.views).Append(" views   ·   ").Append(r.people.Count).Append(" people (");
            for (int v = 0; v < r.views; v++) { if (v > 0) hudValues.Append(" · "); hudValues.Append(r.CountIn(v)); }
            hudValues.Append(')');
            if (err2dN > 0) hudValues.Append("   ·   err ").Append((err2d / err2dN).ToString("F0")).Append(" px");
            hudValues.Append('\n');

            if (!lift3D) hudValues.Append("off (Lift 3D is unticked)");
            else
            {
                hudValues.Append(r.people3d.Count).Append(" people from at least ").Append(minViews).Append(" views");
                if (r.reprojectionPx >= 0f) hudValues.Append("   ·   reprojection ").Append(r.reprojectionPx.ToString("F1")).Append(" px");
            }
            for (int i = 0; i < r.people3d.Count; i++)
            {
                FvpPerson p3 = r.people3d[i];
                hudData.people.Add(new FvpHudData.PersonRow { id = p3.id, score = p3.score, errMm = p3.errMm, ghost = p3.ghost, color = ModelColor });
            }
            hudData.emptyText = lift3D ? $"no 3D person: needs one seen in {minViews}+ views" : "3D is off";
            hudData.warn = "";
            hudData.footer = "bar: mean confidence of the 2D joints behind each 3D person (tick = keypoint threshold).\nerr: mean 3D distance of the joints to the green skeleton, same instant (2D err: pixels in the detection's view).";
        }
        hudData.statLabels = hudLabels.ToString();
        hudData.statValues = hudValues.ToString();

        int h = state.GetHashCode() ^ hudData.statLabels.GetHashCode() ^ (hudData.statValues.GetHashCode() * 31) ^ hudData.people.Count ^ hudData.warn.GetHashCode();
        for (int i = 0; i < hudData.people.Count; i++)
            h = h * 397 ^ hudData.people[i].id ^ (int)(hudData.people[i].score * 1000f) * 13 ^ (int)hudData.people[i].errMm * 7 ^ (hudData.people[i].ghost ? 1 : 0);
        if (h != hudHash) { hudHash = h; hudData.stamp++; }
    }

    // ---------------- heatmaps ----------------

    // A new result: keep the window of heatmaps bounded and put the new one (and the image it was computed on) on the textures.
    void OnHeatResult(ViTPoseResult r, FvpFrame f)
    {
        if (r.heat != null) resultsWithHeat.Enqueue(r);
        while (resultsWithHeat.Count > heatHistoryFrames) resultsWithHeat.Dequeue().heat = null;
        if (!showHeatmaps || r.heat == null) return;
        heatTextures.FillHeat(r.heat, r.heatW, r.heatH, r);
        heatTextures.UploadImage(f.raw, f.width, f.height, r);
    }

    static string JointLabel(int joint) => joint < 0 ? "all joints" : joint < ViTPoseSkeleton.Names.Length ? ViTPoseSkeleton.Names[joint] : "joint " + joint;

    void RefreshHeat()
    {
        heatData.visible = showHeatmaps && PanelsVisible;
        if (!showHeatmaps) return;

        ViTPoseResult r = ShownResult();
        if (r != null && r.heat != null && heatTextures.heatOwner != (object)r) heatTextures.FillHeat(r.heat, r.heatW, r.heatH, r);   // browsing the history
        bool imageOk = r != null && heatTextures.imageOwner == (object)r;

        int key = r == null ? 0 : r.frameId * 31 + (r.heat != null ? 1 : 2) + (imageOk ? 4 : 0) + (heatmapJoint + 2) * 97 + (host_.IsPaused ? 8 : 0);
        key ^= (host_.cameras != null ? host_.cameras.Count : 0) << 20;
        if (key == heatKey) return;
        heatKey = key;
        heatData.stamp++;

        if (r == null) { heatData.views = 0; heatData.message = ""; heatData.hint = ""; return; }
        if (r.heat == null)
        {
            heatData.views = 0;
            heatData.message = "No ViTPose heatmap for this frame (older than the kept window, or heatmaps were off).";
            heatData.hint = "";
            return;
        }

        int views = Mathf.Min(r.heat.Length, FvpHeatData.MaxViews);
        heatData.views = views;
        heatData.aspect = r.heatW > 0 ? (float)r.heatH / r.heatW : 9f / 16f;
        for (int v = 0; v < views; v++)
        {
            Camera cam = host_.cameras != null && v < host_.cameras.Count ? host_.cameras[v] : null;
            heatData.display[v] = cam != null ? cam.targetDisplay : -1;
            heatData.heat[v] = heatTextures.heat != null && v < heatTextures.heat.Length ? heatTextures.heat[v] : null;
            heatData.image[v] = imageOk && heatTextures.image != null && v < heatTextures.image.Length ? heatTextures.image[v] : null;
            heatData.label[v] = $"{(cam != null ? cam.name.Trim() : "view " + v)}  ·  {JointLabel(r.heatJoint)}  ·  peak {heatTextures.peak[v]:F2}";
        }
        heatData.message = "";
        heatData.hint = "ViTPose's own heatmaps (64 x 48 per person crop), laid back over the frame." + (imageOk ? "" : "\n(image kept for the newest result only)") +
                        "\nJ / Shift+J joint   G hide" + (r.heatJoint != HeatRequest ? "\n(a change applies to the next estimate)" : "");
    }

    // ---------------- panels on the displays ----------------

    void UpdatePanels()
    {
        if (panels == null)
        {
            panels = new FvpModelPanels[8];
            for (int d = 0; d < panels.Length; d++) panels[d] = new FvpModelPanels("ViTPose", d, 320);
        }
        float u = host_.UiScale;
        foreach (FvpModelPanels p in panels)
        {
            p.hud.Apply(hudData, host_.DisplayTitle(p.display), u, "ViTPose");
            p.heat.Apply(heatData, p.display, u);
        }
    }

    void DestroyVisuals()
    {
        DestroyOverlays();
        if (panels != null) foreach (FvpModelPanels p in panels) p.Destroy();
        panels = null;
        heatTextures.DestroyAll();
    }
}
