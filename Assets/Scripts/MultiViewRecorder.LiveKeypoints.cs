using System.Collections.Generic;
using UnityEngine;

// The recorder's ground-truth keypoints, live: the "Draw Keypoint Gizmos" checkbox switches all of it.
//
//   - In Play mode the keypoints are drawn over each recorder camera's own display (the Game view), the same joints, colours, offsets and sizes
//     the recorder writes into keyrgb/ (Skeleton Format, the head / toe offsets, Point Radius Px, Line Thickness Px, Draw Skeleton), projected
//     the way ProjectPinhole does (a CalibratedCamera's lens included). A screen overlay: nothing is drawn into the cameras' render, so rgb/ and
//     keyrgb/ are untouched. Fisheye (FulldomeCamera) cameras are not drawn.
//   - The 3D gizmos (Scene view, Game view with Gizmos on) draw before recording starts as well: they used to need a session.
// None of it needs a session, FasterVoxelPose, a server or any UI. It stands down while a FasterVoxelPose component is active (that draws the
// ground truth itself).
public partial class MultiViewRecorder
{
    sealed class LiveRig
    {
        public Animator animator;
        public readonly SkeletonFormats.RigBones rb = new SkeletonFormats.RigBones();
        public bool resolved;
        public Vector3[] world;
        public bool[] valid;
    }

    sealed class LiveView
    {
        public Camera cam;
        public FvpOverlay overlay;
    }

    SkeletonFormats.SkeletonDef liveDef;
    SkeletonFormat liveFormat = (SkeletonFormat)(-1);
    Color[] liveColors;
    readonly List<LiveRig> liveRigs = new List<LiveRig>();
    LiveView[] liveViews;
    Vector2[] liveUv;
    bool[] liveOk;
    float liveNextResolve;
    bool liveWarnedDome;

    // For tests and tools: the overlay drawn on the display of camera `index` (null for one that is not drawn), and how many avatars are posed.
    public FvpOverlay LiveOverlayOf(int index) => liveViews != null && index >= 0 && index < liveViews.Length && liveViews[index] != null ? liveViews[index].overlay : null;
    public int LiveAvatarCount => liveRigs.Count;

    void LateUpdate()
    {
        if (!Application.isPlaying) return;
        UpdateLiveKeypoints();
    }

    void UpdateLiveKeypoints()
    {
        bool other = FasterVoxelPoseLive.Instance != null && FasterVoxelPoseLive.Instance.isActiveAndEnabled;
        if (!drawKeypointGizmos || other || cameras == null || avatars == null) { HideLiveViews(); return; }

        EnsureLiveDefinition();
        EnsureLiveRigs();
        PoseLiveRigs();
        EnsureLiveViews();

        for (int i = 0; i < liveViews.Length; i++)
        {
            LiveView v = liveViews[i];
            if (v == null || v.overlay == null) continue;
            FvpOverlay ov = v.overlay;
            ov.SetVisible(true);
            ov.SetDisplay(v.cam.targetDisplay);
            ov.graphic.lineWidth = Mathf.Max(1, lineThicknessPx);
            ov.graphic.dotRadius = Mathf.Max(1, pointRadiusPx);

            ov.graphic.Begin();
            bool calibrated = v.cam.TryGetComponent(out CalibratedCamera cc) && cc.IsApplied;
            int n = liveDef.Count;
            foreach (LiveRig rig in liveRigs)
            {
                if (!rig.resolved) continue;
                for (int k = 0; k < n; k++)
                {
                    liveOk[k] = false;
                    if (!rig.valid[k]) continue;
                    // ProjectPinhole: the camera's viewport (the lens model of a calibrated one), top-left origin
                    Vector3 vp = calibrated ? cc.WorldToViewport(rig.world[k]) : v.cam.WorldToViewportPoint(rig.world[k]);
                    if (vp.z <= v.cam.nearClipPlane || vp.x < 0f || vp.x >= 1f || vp.y < 0f || vp.y >= 1f) continue;
                    liveUv[k] = new Vector2(vp.x, 1f - vp.y);
                    liveOk[k] = true;
                }
                if (drawSkeleton && liveDef.edges != null)
                    for (int e = 0; e < liveDef.edges.GetLength(0); e++)
                    {
                        int a = liveDef.edges[e, 0], b = liveDef.edges[e, 1];
                        if (a < 0 || b < 0 || a >= n || b >= n || !liveOk[a] || !liveOk[b]) continue;
                        ov.graphic.AddLine(liveUv[a], liveUv[b], Color.Lerp(liveColors[a], liveColors[b], 0.5f));
                    }
                for (int k = 0; k < n; k++)
                    if (liveOk[k]) ov.graphic.AddDot(liveUv[k], liveColors[k]);
            }
            ov.graphic.End();
        }
    }

    // The 3D gizmos before a session exists (while recording, OnDrawGizmos uses the session's own data).
    void DrawLiveGizmos()
    {
        if (!Application.isPlaying || liveDef == null) return;
        int n = liveDef.Count;
        foreach (LiveRig rig in liveRigs)
        {
            if (!rig.resolved) continue;
            if (drawSkeleton && liveDef.edges != null)
                for (int e = 0; e < liveDef.edges.GetLength(0); e++)
                {
                    int a = liveDef.edges[e, 0], b = liveDef.edges[e, 1];
                    if (a < 0 || b < 0 || a >= n || b >= n || !rig.valid[a] || !rig.valid[b]) continue;
                    Gizmos.color = Color.Lerp(liveColors[a], liveColors[b], 0.5f);
                    Gizmos.DrawLine(rig.world[a], rig.world[b]);
                }
            for (int k = 0; k < n; k++)
            {
                if (!rig.valid[k]) continue;
                Gizmos.color = liveColors[k];
                Gizmos.DrawSphere(rig.world[k], gizmoJointRadius);
            }
        }
    }

    // The joint set of the Skeleton Format dropdown (rebuilt when it changes).
    void EnsureLiveDefinition()
    {
        if (liveDef != null && liveFormat == skeletonFormat) return;
        liveDef = SkeletonFormats.Build(skeletonFormat);
        liveFormat = skeletonFormat;
        Color32[] c32 = SkeletonFormats.Colors(liveDef);
        liveColors = new Color[c32.Length];
        for (int i = 0; i < c32.Length; i++) liveColors[i] = c32[i];
        liveUv = new Vector2[liveDef.Count];
        liveOk = new bool[liveDef.Count];
        liveRigs.Clear();
    }

    SkeletonFormats.Offsets LiveOffsets() => new SkeletonFormats.Offsets
    {
        headTopUp = headTopUpOffset,
        nose = noseHeadOffset, leftEye = leftEyeHeadOffset, rightEye = rightEyeHeadOffset,
        leftEar = leftEarHeadOffset, rightEar = rightEarHeadOffset, toeForward = toeForwardOffset,
    };

    // One rig per avatar of the list; bones are looked up again until they resolve (an avatar can be built late).
    void EnsureLiveRigs()
    {
        int count = 0;
        foreach (Animator a in avatars) if (a != null) count++;
        bool same = count == liveRigs.Count;
        if (same)
        {
            int i = 0;
            foreach (Animator a in avatars)
            {
                if (a == null) continue;
                if (liveRigs[i].animator != a) { same = false; break; }
                i++;
            }
        }
        if (!same)
        {
            liveRigs.Clear();
            foreach (Animator a in avatars)
                if (a != null) liveRigs.Add(new LiveRig { animator = a, world = new Vector3[liveDef.Count], valid = new bool[liveDef.Count] });
        }

        if (Time.unscaledTime < liveNextResolve) return;
        liveNextResolve = Time.unscaledTime + 0.5f;
        SkeletonFormats.Offsets o = LiveOffsets();
        foreach (LiveRig rig in liveRigs)
        {
            if (rig.resolved || rig.animator == null) continue;
            SkeletonFormats.Resolve(rig.animator, rig.rb);
            rig.resolved = SkeletonFormats.AnyResolved(rig.rb, liveDef, o);
        }
    }

    void PoseLiveRigs()
    {
        SkeletonFormats.Offsets o = LiveOffsets();
        foreach (LiveRig rig in liveRigs)
        {
            if (!rig.resolved || rig.animator == null || !rig.animator.isActiveAndEnabled)
            {
                for (int k = 0; k < rig.valid.Length; k++) rig.valid[k] = false;
                continue;
            }
            for (int k = 0; k < liveDef.Count; k++)
                rig.world[k] = SkeletonFormats.WorldPosition(rig.rb, liveDef.joints[k], o, out rig.valid[k]);
        }
    }

    // One overlay per camera, on that camera's Target Display; a calibrated camera whose lens-distorted image is presented on its display uses
    // the presenter's letterbox, like FasterVoxelPose's overlay does.
    void EnsureLiveViews()
    {
        int n = cameras.Count;
        bool same = liveViews != null && liveViews.Length == n;
        if (same)
            for (int i = 0; i < n && same; i++)
            {
                if (liveViews[i] == null) { if (cameras[i] != null && !IsDomeCamera(cameras[i])) same = false; }
                else if (liveViews[i].cam != cameras[i]) same = false;
            }
        if (!same)
        {
            DestroyLiveOverlay();
            liveViews = new LiveView[n];
        }
        for (int i = 0; i < n; i++)
        {
            Camera cam = cameras[i];
            if (cam == null) { liveViews[i] = null; continue; }
            if (IsDomeCamera(cam))
            {
                if (!liveWarnedDome) { liveWarnedDome = true; Debug.Log("[MultiViewRecorder] fisheye (FulldomeCamera) cameras are not drawn live: " + cam.name, this); }
                liveViews[i] = null;
                continue;
            }
            bool letterbox = cam.TryGetComponent(out CalibratedCamera cc) && cc.IsApplied && cc.DistortionActive && cc.showOnDisplay && cc.imageHeight > 0;
            LiveView v = liveViews[i];
            if (v == null || v.cam != cam || v.overlay == null || v.overlay.letterboxed != letterbox)
            {
                v?.overlay?.Destroy();
                float aspect = letterbox ? (float)cc.imageWidth / cc.imageHeight : 1f;
                liveViews[i] = new LiveView { cam = cam, overlay = new FvpOverlay("Recorder live keypoints " + cam.name.Trim(), 190 + i, letterbox, aspect) };
            }
        }
    }

    static bool IsDomeCamera(Camera cam) => cam.GetComponent<Avante.FulldomeCamera>() != null;

    void HideLiveViews()
    {
        if (liveViews == null) return;
        foreach (LiveView v in liveViews) v?.overlay?.SetVisible(false);
    }

    void DestroyLiveOverlay()
    {
        if (liveViews == null) return;
        foreach (LiveView v in liveViews) v?.overlay?.Destroy();
        liveViews = null;
    }
}
