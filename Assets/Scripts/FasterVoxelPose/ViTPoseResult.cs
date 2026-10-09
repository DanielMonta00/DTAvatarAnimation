using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

// What ViTPose made of one captured frame: for every camera view, the people it found and their 17 COCO joints in pixels of the
// frame that was sent, plus (when asked for) the network's own heatmaps laid over that frame. ViTPose is a top-down 2D method:
// a person detector finds each person in each view, ViTPose reads the joints off a 256 x 192 crop of the box.
public sealed class ViTPosePerson
{
    public int view;
    public float x1, y1, x2, y2, detScore;                  // the detector's box, pixels of the sent frame
    public readonly Vector3[] kp = new Vector3[ViTPoseSkeleton.Count];   // x, y (pixels), confidence 0..1
    public float score;                                     // mean confidence of the joints
    public float errPx = -1f;                               // mean 2D distance of the shared joints to the ground truth projected into this view; < 0 = none
    public bool ghost;                                      // there is ground truth in this view, but nobody near this skeleton
}

public sealed class ViTPoseResult
{
    public int frameId;
    public double sceneTime;
    public int views, width, height;                        // the frame the joints refer to
    public readonly List<ViTPosePerson> people = new List<ViTPosePerson>();
    // Lifted to 3D by multi-view triangulation (FvpMultiViewLifter): the same kind of person FasterVoxelPose gives (15 Panoptic joints
    // in the Unity world, metres, id, velocity, error against the ground truth), from the people seen in two or more views.
    public readonly List<FvpPerson> people3d = new List<FvpPerson>();
    public readonly List<MvPerson3D> lifted = new List<MvPerson3D>();       // the same people with all 17 COCO joints, same order
    public FvpCameraModel[] cameras;                        // the view models the lifting used
    public float liftMs, reprojectionPx = -1;               // reprojectionPx: mean distance of the 3D joints, projected back, from the 2D detections
    public byte[][] heat;                                   // [view][y * heatW + x], 255 = 1.0; null when not asked for / too old
    public int heatW, heatH, heatJoint = -2;                // heatJoint: COCO index, -1 = strongest of all
    public float detMs, poseMs, serverMs, latencyMs;

    public int CountIn(int view)
    {
        int n = 0;
        for (int i = 0; i < people.Count; i++) if (people[i].view == view) n++;
        return n;
    }
}

public static class ViTPoseSkeleton
{
    public const int Count = 17;

    public static readonly string[] Names =
    {
        "nose", "l-eye", "r-eye", "l-ear", "r-ear", "l-shoulder", "r-shoulder", "l-elbow", "r-elbow", "l-wrist", "r-wrist",
        "l-hip", "r-hip", "l-knee", "r-knee", "l-ankle", "r-ankle",
    };

    // mmpose's COCO skeleton
    public static readonly int[,] Edges =
    {
        { 15, 13 }, { 13, 11 }, { 16, 14 }, { 14, 12 }, { 11, 12 }, { 5, 11 }, { 6, 12 }, { 5, 6 }, { 5, 7 }, { 6, 8 }, { 7, 9 }, { 8, 10 },
        { 1, 2 }, { 0, 1 }, { 0, 2 }, { 1, 3 }, { 2, 4 }, { 3, 5 }, { 4, 6 },
    };

    // COCO joint -> Panoptic-15 joint (what the ground truth and Faster-VoxelPose use), for the ones both have
    public static readonly int[,] ToPanoptic =
    {
        { 0, FvpSkeleton.Nose }, { 5, FvpSkeleton.LShoulder }, { 7, FvpSkeleton.LElbow }, { 9, FvpSkeleton.LWrist }, { 11, FvpSkeleton.LHip },
        { 13, FvpSkeleton.LKnee }, { 15, FvpSkeleton.LAnkle }, { 6, FvpSkeleton.RShoulder }, { 8, FvpSkeleton.RElbow }, { 10, FvpSkeleton.RWrist },
        { 12, FvpSkeleton.RHip }, { 14, FvpSkeleton.RKnee }, { 16, FvpSkeleton.RAnkle },
    };

    public static bool IsLeft(int j) => j == 1 || j == 3 || (j >= 5 && (j & 1) == 1);
    public static bool IsRight(int j) => j == 2 || j == 4 || (j >= 6 && (j & 1) == 0);

    // cyan family: the person's left lighter, right darker, like the green and orange skeletons of the others
    public static Color ColorFor(int j) => IsLeft(j) ? new Color(0.55f, 0.95f, 1f) : IsRight(j) ? new Color(0.05f, 0.62f, 0.85f) : new Color(0.3f, 0.8f, 1f);
}

// Wire format of Server~/vitpose_server.py (same framing as the other server, see FvpProtocol).
public static class ViTPoseProtocol
{
    public const int Stride = 57;   // view, x1, y1, x2, y2, detector score, 17 x (x, y, confidence)

    public static string BuildConfigJson(int configId, int views, int width, int height) =>
        "{\"type\":\"config\",\"config_id\":" + configId + ",\"views\":" + views + ",\"width\":" + width + ",\"height\":" + height + "}";

    // heatJoint: < -1 = no heatmaps; -1 = the strongest of all joints; 0..16 = that COCO joint
    public static string BuildFrameJson(int id, int configId, int views, int width, int height, float detThreshold, int heatJoint) =>
        "{\"type\":\"frame\",\"id\":" + id + ",\"config_id\":" + configId + ",\"views\":" + views + ",\"width\":" + width + ",\"height\":" + height +
        ",\"det_thr\":" + detThreshold.ToString("R", CultureInfo.InvariantCulture) +
        (heatJoint >= -1 ? ",\"heat\":1,\"heat_joint\":" + heatJoint : "") + "}";

    // float32 [persons, Stride] -> people
    public static void DecodePeople(byte[] bin, int bytes, int stride, List<ViTPosePerson> into)
    {
        into.Clear();
        if (stride != Stride || bytes <= 0) return;
        int rows = bytes / (stride * 4);
        var f = new float[rows * stride];
        System.Buffer.BlockCopy(bin, 0, f, 0, f.Length * 4);
        for (int r = 0; r < rows; r++)
        {
            int o = r * stride;
            var p = new ViTPosePerson { view = (int)f[o], x1 = f[o + 1], y1 = f[o + 2], x2 = f[o + 3], y2 = f[o + 4], detScore = f[o + 5] };
            float sum = 0f;
            for (int j = 0; j < ViTPoseSkeleton.Count; j++)
            {
                int k = o + 6 + j * 3;
                p.kp[j] = new Vector3(f[k], f[k + 1], f[k + 2]);
                sum += f[k + 2];
            }
            p.score = sum / ViTPoseSkeleton.Count;
            into.Add(p);
        }
    }
}
