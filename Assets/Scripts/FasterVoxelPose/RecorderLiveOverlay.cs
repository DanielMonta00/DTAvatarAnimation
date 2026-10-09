using System.Collections.Generic;
using UnityEngine;

// The skeletons the MultiViewRecorder writes into keyrgb/, drawn live over each of its cameras' own displays while the scene plays.
//
// It needs nothing but the recorder's own object: no FasterVoxelPose, no servers, no status card, no bar, no tabs. It reads the recorder's
// settings (Avatars, Cameras, Skeleton Format, the head / toe offsets, the point radius and line thickness) and does what the recorder does
// for a frame: the joints of every avatar's rig in the chosen format (SkeletonFormats), projected through each camera (a CalibratedCamera's
// lens included, exactly as the recorder's ProjectPinhole does), the same joint colours (left / right / centre), each line the average of
// its two joints' colours. Recording or not, so the keyrgb picture can be watched live.
//
// It is a screen overlay (one canvas per display, see FvpOverlay): nothing is drawn into the cameras' render, so rgb/ and keyrgb/ are
// untouched. Fisheye (FulldomeCamera) cameras are not drawn: their display is not the image the recorder projects into.
// Add it with Tools > FasterVoxelPose > Show live ground truth on the MultiViewRecorder's cameras, or by hand on the recorder's object.
[DisallowMultipleComponent]
[RequireComponent(typeof(MultiViewRecorder))]
[DefaultExecutionOrder(950)]
public sealed class RecorderLiveOverlay : MonoBehaviour
{
    [Tooltip("Draw the recorder's keypoints over each camera's display. Off: nothing is drawn and nothing is computed.")]
    public bool show = true;
    [Tooltip("Stay out of the way while a FasterVoxelPose component is active: it draws the ground truth itself.")]
    public bool yieldToFasterVoxelPose = true;

    sealed class Rig
    {
        public Animator animator;
        public readonly SkeletonFormats.RigBones rb = new SkeletonFormats.RigBones();
        public bool resolved;
        public Vector3[] world;
        public bool[] valid;
    }

    sealed class View
    {
        public Camera cam;
        public FvpOverlay overlay;
    }

    MultiViewRecorder rec;
    SkeletonFormats.SkeletonDef def;
    SkeletonFormat defFormat = (SkeletonFormat)(-1);
    Color[] colors;
    readonly List<Rig> rigs = new List<Rig>();
    View[] views;
    Vector2[] uv;
    bool[] ok;
    float nextResolve;
    bool warnedDome;

    public int DrawnPeople { get; private set; }

    // The overlay drawn on the display of recorder camera `index` (null for a camera that is not drawn). For tests and tools.
    public FvpOverlay OverlayOf(int index) => views != null && index >= 0 && index < views.Length && views[index] != null ? views[index].overlay : null;

    void OnDisable() => DestroyViews();

    void LateUpdate()
    {
        if (!Application.isPlaying) return;
        if (rec == null) rec = GetComponent<MultiViewRecorder>();
        bool other = yieldToFasterVoxelPose && FasterVoxelPoseLive.Instance != null && FasterVoxelPoseLive.Instance.isActiveAndEnabled;
        if (!show || rec == null || other || rec.cameras == null || rec.avatars == null) { HideViews(); DrawnPeople = 0; return; }

        EnsureDefinition();
        EnsureRigs();
        EnsureViews();
        PoseRigs();

        for (int i = 0; i < views.Length; i++)
        {
            View v = views[i];
            if (v == null) continue;
            FvpOverlay ov = v.overlay;
            if (ov == null) continue;
            ov.SetVisible(true);
            ov.SetDisplay(v.cam.targetDisplay);
            ov.graphic.lineWidth = Mathf.Max(1, rec.lineThicknessPx);
            ov.graphic.dotRadius = Mathf.Max(1, rec.pointRadiusPx);

            ov.graphic.Begin();
            bool calibrated = v.cam.TryGetComponent(out CalibratedCamera cc) && cc.IsApplied;
            for (int r = 0; r < rigs.Count; r++)
            {
                Rig rig = rigs[r];
                if (!rig.resolved) continue;
                int n = def.Count;
                for (int k = 0; k < n; k++)
                {
                    ok[k] = false;
                    if (!rig.valid[k]) continue;
                    // the recorder's ProjectPinhole: viewport of the camera (the lens model of a calibrated one), top-left origin
                    Vector3 vp = calibrated ? cc.WorldToViewport(rig.world[k]) : v.cam.WorldToViewportPoint(rig.world[k]);
                    if (vp.z <= v.cam.nearClipPlane || vp.x < 0f || vp.x >= 1f || vp.y < 0f || vp.y >= 1f) continue;
                    uv[k] = new Vector2(vp.x, 1f - vp.y);
                    ok[k] = true;
                }
                if (rec.drawSkeleton && def.edges != null)
                    for (int e = 0; e < def.edges.GetLength(0); e++)
                    {
                        int a = def.edges[e, 0], b = def.edges[e, 1];
                        if (a < 0 || b < 0 || a >= n || b >= n || !ok[a] || !ok[b]) continue;
                        ov.graphic.AddLine(uv[a], uv[b], Color.Lerp(colors[a], colors[b], 0.5f));
                    }
                for (int k = 0; k < n; k++)
                    if (ok[k]) ov.graphic.AddDot(uv[k], colors[k]);
            }
            ov.graphic.End();
        }
        DrawnPeople = rigs.Count;
    }

    // The joint set of the recorder's Skeleton Format (rebuilt when the dropdown changes).
    void EnsureDefinition()
    {
        if (def != null && defFormat == rec.skeletonFormat) return;
        def = SkeletonFormats.Build(rec.skeletonFormat);
        defFormat = rec.skeletonFormat;
        Color32[] c32 = SkeletonFormats.Colors(def);
        colors = new Color[c32.Length];
        for (int i = 0; i < c32.Length; i++) colors[i] = c32[i];
        uv = new Vector2[def.Count];
        ok = new bool[def.Count];
        rigs.Clear();
    }

    SkeletonFormats.Offsets Offsets() => new SkeletonFormats.Offsets
    {
        headTopUp = rec.headTopUpOffset,
        nose = rec.noseHeadOffset, leftEye = rec.leftEyeHeadOffset, rightEye = rec.rightEyeHeadOffset,
        leftEar = rec.leftEarHeadOffset, rightEar = rec.rightEarHeadOffset, toeForward = rec.toeForwardOffset,
    };

    // One rig per avatar of the recorder's list; the bones are looked up again until they resolve (an avatar can be built late).
    void EnsureRigs()
    {
        bool same = true;
        int count = 0;
        foreach (Animator a in rec.avatars) if (a != null) count++;
        if (count != rigs.Count) same = false;
        else
        {
            int i = 0;
            foreach (Animator a in rec.avatars)
            {
                if (a == null) continue;
                if (rigs[i].animator != a) { same = false; break; }
                i++;
            }
        }
        if (!same)
        {
            rigs.Clear();
            foreach (Animator a in rec.avatars)
                if (a != null) rigs.Add(new Rig { animator = a, world = new Vector3[def.Count], valid = new bool[def.Count] });
        }

        if (Time.unscaledTime < nextResolve) return;
        nextResolve = Time.unscaledTime + 0.5f;
        SkeletonFormats.Offsets o = Offsets();
        foreach (Rig rig in rigs)
        {
            if (rig.resolved || rig.animator == null) continue;
            SkeletonFormats.Resolve(rig.animator, rig.rb);
            rig.resolved = SkeletonFormats.AnyResolved(rig.rb, def, o);
        }
    }

    void PoseRigs()
    {
        SkeletonFormats.Offsets o = Offsets();
        foreach (Rig rig in rigs)
        {
            if (!rig.resolved || rig.animator == null || !rig.animator.isActiveAndEnabled)
            {
                for (int k = 0; k < rig.valid.Length; k++) rig.valid[k] = false;
                continue;
            }
            for (int k = 0; k < def.Count; k++)
                rig.world[k] = SkeletonFormats.WorldPosition(rig.rb, def.joints[k], o, out rig.valid[k]);
        }
    }

    // One overlay per recorded camera, on that camera's Target Display; a calibrated camera whose lens-distorted image is presented on its
    // display uses the presenter's letterbox, like FasterVoxelPose's overlay does.
    void EnsureViews()
    {
        int n = rec.cameras.Count;
        bool same = views != null && views.Length == n;
        if (same)
            for (int i = 0; i < n; i++)
            {
                if (views[i] == null) { if (rec.cameras[i] != null && !IsDome(rec.cameras[i])) same = false; }
                else if (views[i].cam != rec.cameras[i]) same = false;
                if (!same) break;
            }
        if (!same)
        {
            DestroyViews();
            views = new View[n];
        }
        for (int i = 0; i < n; i++)
        {
            Camera cam = rec.cameras[i];
            if (cam == null) { views[i] = null; continue; }
            if (IsDome(cam))
            {
                if (!warnedDome) { warnedDome = true; Debug.Log("[RecorderLiveOverlay] fisheye (FulldomeCamera) cameras are not drawn live: " + cam.name, this); }
                views[i] = null;
                continue;
            }
            bool letterbox = cam.TryGetComponent(out CalibratedCamera cc) && cc.IsApplied && cc.DistortionActive && cc.showOnDisplay && cc.imageHeight > 0;
            View v = views[i];
            if (v == null || v.cam != cam || v.overlay == null || v.overlay.letterboxed != letterbox)
            {
                v?.overlay?.Destroy();
                float aspect = letterbox ? (float)cc.imageWidth / cc.imageHeight : 1f;
                views[i] = new View { cam = cam, overlay = new FvpOverlay("Recorder live overlay " + cam.name.Trim(), 190 + i, letterbox, aspect) };
            }
        }
    }

    static bool IsDome(Camera cam) => cam.GetComponent<Avante.FulldomeCamera>() != null;

    void HideViews()
    {
        if (views == null) return;
        foreach (View v in views) v?.overlay?.SetVisible(false);
    }

    void DestroyViews()
    {
        if (views == null) return;
        foreach (View v in views) v?.overlay?.Destroy();
        views = null;
    }
}
