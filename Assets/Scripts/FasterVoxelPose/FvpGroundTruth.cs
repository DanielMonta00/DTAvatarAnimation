using System.Collections.Generic;
using UnityEngine;

// Ground truth for the comparison: the avatar rigs read out as the same 15 joints Faster-VoxelPose predicts.
//
// It is the code the dataset recorder uses for its keypoints (SkeletonFormats, CMU_Panoptic_15 layout: identical joint
// order and bones to FvpSkeleton), so what is drawn in green is what a recorded dataset would hold as ground truth.
public sealed class FvpGroundTruth
{
    readonly SkeletonFormats.SkeletonDef def = SkeletonFormats.Build(SkeletonFormat.CMU_Panoptic_15);
    readonly SkeletonFormats.Offsets offsets = SkeletonFormats.Offsets.Default;
    readonly List<SkeletonFormats.RigBones> rigs = new List<SkeletonFormats.RigBones>();

    // Re-resolves the bones when the avatar list changes.
    public void SetAvatars(IList<Animator> avatars)
    {
        rigs.Clear();
        if (avatars == null) return;
        foreach (Animator a in avatars)
        {
            if (a == null) continue;
            var rb = new SkeletonFormats.RigBones();
            SkeletonFormats.Resolve(a, rb);
            rigs.Add(rb);
        }
    }

    public int Count => rigs.Count;

    public bool Matches(IList<Animator> avatars)
    {
        int n = 0;
        if (avatars != null)
            foreach (Animator a in avatars)
            {
                if (a == null) continue;
                if (n >= rigs.Count || rigs[n].animator != a) return false;
                n++;
            }
        return n == rigs.Count;
    }

    // One FvpPerson per avatar, joints in world space right now. Avatars whose rig supplies nothing are skipped.
    public void Read(List<FvpPerson> into)
    {
        into.Clear();
        for (int r = 0; r < rigs.Count; r++)
        {
            SkeletonFormats.RigBones rb = rigs[r];
            if (rb.animator == null || !rb.animator.isActiveAndEnabled) continue;

            var p = new FvpPerson { id = r, score = 1f, valid = new bool[FvpSkeleton.Count] };
            int ok = 0;
            for (int j = 0; j < FvpSkeleton.Count; j++)
            {
                p.joints[j] = SkeletonFormats.WorldPosition(rb, def.joints[j], offsets, out bool valid);
                p.valid[j] = valid;
                if (valid) ok++;
            }
            if (ok > 0) into.Add(p);
        }
    }
}
