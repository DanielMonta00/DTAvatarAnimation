using UnityEngine;

// The lifted 3D people of ViTPose in the Scene view (and the Game view with Gizmos on), in blue-violet: the skeletons of the
// result on show, like FasterVoxelPose's orange ones, so the two can be compared in space.
public sealed partial class ViTPoseLive
{
    public static Color LiftedColor(int joint) =>
        FvpSkeleton.IsLeft(joint) ? new Color(0.66f, 0.66f, 1f) : FvpSkeleton.IsRight(joint) ? new Color(0.38f, 0.36f, 0.95f) : new Color(0.5f, 0.5f, 1f);

    void OnDrawGizmos()
    {
        if (!Application.isPlaying || !drawGizmos || !OverlayVisible || host_ == null) return;
        ViTPoseResult r = ShownResult();
        if (r == null) return;
        foreach (FvpPerson p in r.people3d)
        {
            for (int e = 0; e < FvpSkeleton.Edges.GetLength(0); e++)
            {
                int a = FvpSkeleton.Edges[e, 0], b = FvpSkeleton.Edges[e, 1];
                if (!p.IsValid(a) || !p.IsValid(b)) continue;
                Gizmos.color = Color.Lerp(LiftedColor(a), LiftedColor(b), 0.5f);
                Gizmos.DrawLine(p.joints[a], p.joints[b]);
            }
            for (int j = 0; j < FvpSkeleton.Count; j++)
            {
                if (!p.IsValid(j)) continue;
                Gizmos.color = LiftedColor(j);
                Gizmos.DrawSphere(p.joints[j], j == FvpSkeleton.MidHip ? jointRadius * 1.6f : jointRadius);
            }
        }
    }
}
