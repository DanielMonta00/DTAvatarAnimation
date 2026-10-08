using UnityEngine;

// What is shown while playing:
//
//   - on each camera's own display, drawn like the dataset images (thin lines, round dots, line colour = average of its
//     two joints): the avatar's ground truth in GREEN and the Faster-VoxelPose estimate in ORANGE, with the person's left
//     side lighter and the right side darker in both (FvpOverlay, one canvas per camera on that camera's Target Display);
//   - a slim transport bar at the bottom of every display (FasterVoxelPoseLive.Bar.cs) and the mouse / hotkeys.
//
// Keys (Game view focused): Space pause / play, Left / Right step, R rewind, Home / End first / newest frame, M bar.
//
// Synchronisation. An estimate describes the frame that was captured, which is a little older than what the live camera
// shows when it arrives (GPU readback + network + inference, the latency printed on the display). So:
//   SyncedFrame:   the display shows that very frame, a saved GPU copy of the camera image taken at capture, with the
//                  ground truth read at the same instant. Image, green and orange always agree; the picture is as old as the
//                  latency. Paused or rewound it falls back to the live camera, which the tracked Animators have put back to
//                  that frame.
//   RealTime (default): the display keeps showing the live camera at full rate. The ground truth is read from the rig now (exact), and the estimate
//                  is moved forward by its root velocity x latency (a plain horizontal shift of the whole body).
// Stepping while paused holds the previous frame's saved image until the stepped frame's estimate is in, in either mode, so
// the new image never appears under the old skeleton. A camera that does not present a lens-distorted image on its display
// cannot freeze it and always behaves like RealTime.
public partial class FasterVoxelPoseLive
{
    sealed class OverlayView
    {
        public Camera cam;
        public CalibratedCamera calib;
        public FvpOverlay overlay;
    }

    OverlayView[] overlayViews;
    static readonly string GtTag = "<color=#7dff7d>GT</color>", FvpTag = "<color=#ff9a2e>FVP</color>";

    // The overlay drawn on camera `view`'s display (null before the first frame).
    public FvpOverlay OverlayOf(int view) => overlayViews != null && view >= 0 && view < overlayViews.Length ? overlayViews[view].overlay : null;

    void LateUpdate()
    {
        if (!Application.isPlaying) return;
        UpdateOverlays();
        UpdateBars();
    }

    // ---------------- overlays on the camera displays ----------------

    // Something is on its way to replace the frame on show (a step, the first estimate after pausing, a live frame).
    bool Estimating => stepState != StepState.Idle || wantOneShot || inFlight != null || building != null;

    // A single step is waiting for its estimate: the camera already shows the new frame, the skeleton is still the old one.
    bool SteppingPending => stepState != StepState.Idle || (inFlight != null && inFlight.fromStep) || (building != null && building.fromStep);

    void UpdateOverlays()
    {
        if (!showOverlay || cameras == null || cameras.Count == 0) { HideOverlays(); return; }
        EnsureOverlayViews();
        EnsureGroundTruth();

        FvpFrame f = IsReady ? displayed : null;
        bool live = IsLive;
        bool liveRigRead = false;

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
            ov.graphic.dotRadius = overlayDotRadius;

            // Which image sits under the skeletons decides which instant they are drawn for.
            Texture frozen = null;
            if (letterbox && f != null)
            {
                bool want = overlayImage == OverlayImage.SyncedFrame ? (!paused || Estimating) : (paused && SteppingPending);
                if (want) frozen = FrozenImage(f, i);
            }
            bool frozenShown = frozen != null;
            ov.SetImage(frozen);

            FvpPerson[] none = System.Array.Empty<FvpPerson>();
            System.Collections.Generic.IList<FvpPerson> gt = none;
            if (showGroundTruth)
            {
                if (f != null && (frozenShown || !live)) gt = f.groundTruth;   // that frame's instant
                else
                {
                    if (!liveRigRead) { groundTruth.Read(liveGroundTruth); liveRigRead = true; }
                    gt = liveGroundTruth;                                      // the live image: the rig as it is now
                }
            }

            FvpCameraModel model = f != null && f.cameras != null && i < f.cameras.Length ? f.cameras[i]
                                 : sentModels != null && i < sentModels.Length ? sentModels[i] : null;

            ov.graphic.Begin();
            foreach (FvpPerson p in gt) DrawSkeleton(ov, v, model, p, Vector3.zero, true, null);

            int tag = 0;
            if (f != null && showEstimate)
            {
                // Estimate older than the live image it is drawn on: bring it forward by what the person moved meanwhile.
                float lag = live && !frozenShown && latencyCompensation ? Mathf.Clamp((float)(Time.timeAsDouble - f.sceneTime), 0f, 0.5f) : 0f;
                foreach (FvpPerson p in f.people) DrawSkeleton(ov, v, model, p, p.velocity * lag, false, ov, ref tag);
            }
            ov.graphic.End();
            ov.HideTagsFrom(tag);
            ov.SetLabel(OverlayLabel(v, f, frozenShown, live), paused ? new Color(1f, 0.82f, 0.45f) : new Color(0.85f, 1f, 0.9f));
        }
    }

    void DrawSkeleton(FvpOverlay ov, OverlayView v, FvpCameraModel model, FvpPerson p, Vector3 shift, bool isGroundTruth, FvpOverlay tags)
    {
        int unused = 0;
        DrawSkeleton(ov, v, model, p, shift, isGroundTruth, tags, ref unused);
    }

    void DrawSkeleton(FvpOverlay ov, OverlayView v, FvpCameraModel model, FvpPerson p, Vector3 shift, bool isGroundTruth, FvpOverlay tags, ref int tag)
    {
        var uv = new Vector2[FvpSkeleton.Count];
        var ok = new bool[FvpSkeleton.Count];
        var col = new Color[FvpSkeleton.Count];
        for (int j = 0; j < FvpSkeleton.Count; j++)
        {
            col[j] = isGroundTruth ? FvpSkeleton.GroundTruthColor(j) : FvpSkeleton.EstimateColor(j);
            ok[j] = p.IsValid(j) && TryImagePoint(ov, v, model, p.joints[j] + shift, out uv[j]);
        }

        for (int e = 0; e < FvpSkeleton.Edges.GetLength(0); e++)
        {
            int a = FvpSkeleton.Edges[e, 0], b = FvpSkeleton.Edges[e, 1];
            if (ok[a] && ok[b]) ov.graphic.AddLine(uv[a], uv[b], Color.Lerp(col[a], col[b], 0.5f)); // like the dataset images
        }
        for (int j = 0; j < FvpSkeleton.Count; j++)
            if (ok[j]) ov.graphic.AddDot(uv[j], col[j]);

        if (!isGroundTruth && overlayTags && tags != null && ok[FvpSkeleton.Neck])
            tags.Tag(tag++, uv[FvpSkeleton.Neck], $"#{p.id}  {p.score:F2}", col[FvpSkeleton.Neck]);
    }

    // A world point -> normalized position on the image the camera's display shows (0..1, origin top-left).
    bool TryImagePoint(FvpOverlay ov, OverlayView v, FvpCameraModel m, Vector3 w, out Vector2 uv)
    {
        uv = default;
        if (ov.letterboxed)
        {
            if (m != null)
            {
                // The calibrated image, through the camera model that went to the server (K, pose, lens distortion).
                if (!m.Project(w.x, w.y, w.z, out double px, out double py)) return false;
                // +0.5: pixel centres sit on integers in the camera model, the display draws the image edge to edge.
                uv = new Vector2((float)((px + 0.5) / m.width), (float)((py + 0.5) / m.height));
                return true;
            }
            // Not configured yet: ask the calibrated camera itself (what the dataset recorder does for its keypoints).
            Vector3 cv = v.calib.WorldToViewport(w);
            if (cv.z <= 0f) return false;
            uv = new Vector2(cv.x, 1f - cv.y);
            return true;
        }

        // Straight on the camera's display. A calibrated camera still goes through its own lens model (WorldToViewport applies
        // the OpenCV distortion whenever it is being rendered, and is the plain calibrated projection otherwise); only a camera
        // without calibration uses the engine's pinhole viewport.
        Vector3 vp = v.calib != null && v.calib.IsApplied ? v.calib.WorldToViewport(w) : v.cam.WorldToViewportPoint(w);
        if (vp.z <= v.cam.nearClipPlane) return false;
        uv = new Vector2(vp.x, 1f - vp.y);
        return uv.x > -0.5f && uv.x < 1.5f && uv.y > -0.5f && uv.y < 1.5f;
    }

    string OverlayLabel(OverlayView v, FvpFrame f, bool frozenShown, bool live)
    {
        string cam = v.cam.name.Trim();
        string state = paused ? (autoDir < 0 ? "REWIND" : autoDir > 0 ? "REPLAY" : "PAUSED") : "LIVE";
        string legend = (showGroundTruth ? GtTag + " " : "") + (showEstimate ? FvpTag : "");
        if (f == null) return $"{cam}   {state}   {legend}   {status}";

        string sync = frozenShown ? (live || !paused ? $"synced frame, {f.latencyMs:F0} ms behind live" : "stepped frame")
                    : live && latencyCompensation ? $"real-time, estimate advanced {f.latencyMs:F0} ms"
                    : "real-time";
        string where = history.Count > 0 ? $"{cursor + 1}/{history.Count}" : "-";
        return $"{cam}   {state}   {legend}   frame {where}   t = {f.sceneTime:F2} s   {f.people.Count} person(s)   {sync}   {estimateFps:F1}/s" +
               (enableMouse ? "\n<size=11><color=#b8c4d0>click: pause / play    wheel: step a frame    drag: scrub</color></size>" : "");
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
