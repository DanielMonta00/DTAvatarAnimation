using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// 3D view of the estimate: the skeletons of the frame on show (live, or the history frame under the cursor) and the
// capture volume the network searches. Same colours as the displays: the estimate in orange, the avatar's ground truth in
// green (lighter = left, darker = right). Gizmos draw in the Scene view, and in the Game view with its Gizmos toggle on.
public partial class FasterVoxelPoseLive
{
    void OnDrawGizmos()
    {
        if (drawCaptureVolume) DrawVolume();
        if (!Application.isPlaying || !drawSkeleton || displayed == null) return;

        if (showGroundTruth && GroundTruthVisible)
            foreach (FvpPerson p in displayed.groundTruth) DrawGizmoSkeleton(p, true);

        if (!showEstimate || !OverlayVisible) return;
        foreach (FvpPerson p in displayed.people)
        {
            DrawGizmoSkeleton(p, false);

            // Where the person stands, straight down from the root.
            Vector3 root = p.Root;
            Gizmos.color = FvpSkeleton.EstimateColor(FvpSkeleton.MidHip);
            Gizmos.DrawLine(root, new Vector3(root.x, VolumeFloorY(), root.z));
            Gizmos.DrawWireSphere(new Vector3(root.x, VolumeFloorY(), root.z), 0.12f);

#if UNITY_EDITOR
            if (drawLabels)
            {
                var style = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = FvpSkeleton.EstimateColor(FvpSkeleton.MidHip) } };
                Handles.Label(p.joints[FvpSkeleton.Nose] + Vector3.up * 0.18f, $"#{p.id}  {p.score:F2}", style);
            }
#endif
        }
    }

    void DrawGizmoSkeleton(FvpPerson p, bool isGroundTruth)
    {
        Color Col(int j) => isGroundTruth ? FvpSkeleton.GroundTruthColor(j) : FvpSkeleton.EstimateColor(j);
        for (int e = 0; e < FvpSkeleton.Edges.GetLength(0); e++)
        {
            int a = FvpSkeleton.Edges[e, 0], b = FvpSkeleton.Edges[e, 1];
            if (!p.IsValid(a) || !p.IsValid(b)) continue;
            Gizmos.color = Color.Lerp(Col(a), Col(b), 0.5f);
            Gizmos.DrawLine(p.joints[a], p.joints[b]);
        }
        float radius = isGroundTruth ? jointRadius * 0.7f : jointRadius;
        for (int j = 0; j < FvpSkeleton.Count; j++)
        {
            if (!p.IsValid(j)) continue;
            Gizmos.color = Col(j);
            Gizmos.DrawSphere(p.joints[j], j == FvpSkeleton.MidHip ? radius * 1.6f : radius);
        }
    }

    float VolumeFloorY() => VolumeCentreWorld.y - volumeSize.y * 0.5f;

    void DrawVolume()
    {
        Vector3 c = VolumeCentreWorld;
        Gizmos.color = volumeColor;
        Gizmos.DrawWireCube(c, volumeSize);

        // Voxel grid on the floor of the box (every metre), so the extent is readable at a glance.
        Color faint = volumeColor; faint.a *= 0.35f;
        Gizmos.color = faint;
        float y = c.y - volumeSize.y * 0.5f;
        for (float x = -volumeSize.x * 0.5f; x <= volumeSize.x * 0.5f + 1e-3f; x += 1f)
            Gizmos.DrawLine(new Vector3(c.x + x, y, c.z - volumeSize.z * 0.5f), new Vector3(c.x + x, y, c.z + volumeSize.z * 0.5f));
        for (float z = -volumeSize.z * 0.5f; z <= volumeSize.z * 0.5f + 1e-3f; z += 1f)
            Gizmos.DrawLine(new Vector3(c.x - volumeSize.x * 0.5f, y, c.z + z), new Vector3(c.x + volumeSize.x * 0.5f, y, c.z + z));
    }
}
