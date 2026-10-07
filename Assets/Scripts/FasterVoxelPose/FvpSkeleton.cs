using UnityEngine;

// Panoptic-15, the joint set Faster-VoxelPose predicts.
public static class FvpSkeleton
{
    public const int Count = 15;

    public const int Neck = 0, Nose = 1, MidHip = 2,
        LShoulder = 3, LElbow = 4, LWrist = 5, LHip = 6, LKnee = 7, LAnkle = 8,
        RShoulder = 9, RElbow = 10, RWrist = 11, RHip = 12, RKnee = 13, RAnkle = 14;

    public static readonly string[] Names =
    {
        "neck", "nose", "mid-hip", "l-shoulder", "l-elbow", "l-wrist", "l-hip", "l-knee", "l-ankle",
        "r-shoulder", "r-elbow", "r-wrist", "r-hip", "r-knee", "r-ankle",
    };

    public static readonly int[,] Edges =
    {
        { 0, 1 }, { 0, 2 }, { 0, 3 }, { 0, 9 }, { 3, 4 }, { 4, 5 }, { 9, 10 }, { 10, 11 },
        { 2, 6 }, { 2, 12 }, { 6, 7 }, { 7, 8 }, { 12, 13 }, { 13, 14 },
    };

    // One colour per tracked person; a person keeps theirs for as long as their id lives.
    static readonly Color[] Palette =
    {
        new Color(1.00f, 0.55f, 0.10f), new Color(0.20f, 0.85f, 1.00f), new Color(0.55f, 1.00f, 0.25f),
        new Color(1.00f, 0.30f, 0.65f), new Color(1.00f, 0.90f, 0.20f), new Color(0.70f, 0.50f, 1.00f),
        new Color(0.25f, 1.00f, 0.75f), new Color(1.00f, 0.45f, 0.40f),
    };

    public static Color ColorFor(int personId) => Palette[((personId % Palette.Length) + Palette.Length) % Palette.Length];
}
