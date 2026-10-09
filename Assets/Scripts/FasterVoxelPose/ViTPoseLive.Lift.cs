using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

// The 3D side of ViTPose: its 2D people lifted by multi-view triangulation into the same kind of person FasterVoxelPose gives, so the
// two can be compared, drawn and used the same way.
public sealed partial class ViTPoseLive
{
    readonly FvpTracker tracker = new FvpTracker();

    // The 3D people of the result on show: the newest when live, the one attached to the shown frame otherwise.
    public IReadOnlyList<FvpPerson> People
    {
        get
        {
            ViTPoseResult r = host_ != null ? ShownResult() : latest;
            return r != null ? (IReadOnlyList<FvpPerson>)r.people3d : Array.Empty<FvpPerson>();
        }
    }

    void Lift(ViTPoseResult r, FvpFrame f)
    {
        if (!lift3D || host_ == null) return;
        FvpCameraModel[] cams = host_.CameraModelsFor(f);
        int views = cams != null ? Math.Min(cams.Length, r.views) : 0;
        if (views < 2) return;

        var watch = Stopwatch.StartNew();
        var perView = new IList<MvPerson2D>[views];
        for (int v = 0; v < views; v++) perView[v] = new List<MvPerson2D>();
        foreach (ViTPosePerson p in r.people)
        {
            if (p.view < 0 || p.view >= views) continue;
            var q = new MvPerson2D { view = p.view, x = new double[ViTPoseSkeleton.Count], y = new double[ViTPoseSkeleton.Count], conf = new double[ViTPoseSkeleton.Count] };
            for (int j = 0; j < ViTPoseSkeleton.Count; j++) { q.x[j] = p.kp[j].x; q.y[j] = p.kp[j].y; q.conf[j] = p.kp[j].z; }
            perView[p.view].Add(q);
        }

        FvpMultiViewLifter.Options o = FvpMultiViewLifter.Options.Default;
        o.minConf = keypointThreshold; o.groupGateMetres = matchGateMetres; o.inlierMetres = outlierMetres; o.minViews = minViews;
        List<MvPerson3D> lifted = FvpMultiViewLifter.Lift(perView, cams, o);

        r.cameras = cams;
        double reproj = 0; int counted = 0;
        foreach (MvPerson3D m in lifted)
        {
            r.lifted.Add(m);
            r.people3d.Add(ToPanoptic(m));
            if (m.reprojectionPx >= 0) { reproj += m.reprojectionPx; counted++; }
        }
        r.reprojectionPx = counted > 0 ? (float)(reproj / counted) : -1f;

        tracker.gateMetres = 1.0f;
        tracker.Assign(r.people3d, f.sceneTime);                          // ids and velocity, like FasterVoxelPose's people
        FasterVoxelPoseLive.CompareWithGroundTruth(r.people3d, f.groundTruth);   // 3D error in mm against the green skeleton
        watch.Stop();
        r.liftMs = (float)watch.Elapsed.TotalMilliseconds;
    }

    // COCO-17 (triangulated) -> Panoptic-15, the joints Faster-VoxelPose and the ground truth use. The neck is the middle of the
    // shoulders and the mid-hip the middle of the hips (COCO has neither); joints that could not be triangulated are marked invalid
    // and parked at the person's centroid, so the root and the bones stay sane.
    static FvpPerson ToPanoptic(MvPerson3D m)
    {
        var p = new FvpPerson { valid = new bool[FvpSkeleton.Count] };
        int pairs = ViTPoseSkeleton.ToPanoptic.GetLength(0);
        double conf = 0; int n = 0;
        Vector3 sum = Vector3.zero;
        for (int k = 0; k < pairs; k++)
        {
            int c = ViTPoseSkeleton.ToPanoptic[k, 0], j = ViTPoseSkeleton.ToPanoptic[k, 1];
            if (!m.valid[c]) continue;
            p.joints[j] = new Vector3((float)m.x[c], (float)m.y[c], (float)m.z[c]);
            p.valid[j] = true;
            conf += m.conf[c]; n++; sum += p.joints[j];
        }
        Middle(p, FvpSkeleton.Neck, FvpSkeleton.LShoulder, FvpSkeleton.RShoulder);
        Middle(p, FvpSkeleton.MidHip, FvpSkeleton.LHip, FvpSkeleton.RHip);
        Vector3 centroid = n > 0 ? sum / n : Vector3.zero;
        for (int j = 0; j < FvpSkeleton.Count; j++) if (!p.valid[j]) p.joints[j] = centroid;
        p.score = n > 0 ? (float)(conf / n) : 0f;
        return p;
    }

    static void Middle(FvpPerson p, int into, int a, int b)
    {
        if (!p.valid[a] || !p.valid[b]) return;
        p.joints[into] = 0.5f * (p.joints[a] + p.joints[b]);
        p.valid[into] = true;
    }
}
