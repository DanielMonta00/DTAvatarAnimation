using UnityEngine;

// Keypoint/skeleton dataset definitions and rig->joint resolution used by
// MultiViewRecorder. Single source of truth for all supported formats.
//
// Dataset orderings transcribed from:
//   ViTPose          configs/_base_/datasets/{aic,coco,crowdpose,mpii,h36m,mpi_inf_3dhp}.py
//   Faster-VoxelPose lib/dataset/panoptic.py (CMU Panoptic 15)
//   SMPL 24 / Kinect v2 25 / LSP 14 : canonical orderings (metrabs skeletons).
//
// A rig is mapped to each dataset by first resolving a superset of semantic
// joints from its bones (SkeletonFormats.Resolve), then reading out only the
// joints the chosen format asks for, in that format's order. Joints a rig can't
// supply report valid=false so callers can mark them not-visible.

// Dataset skeleton formats — the inspector dropdown on both recorders.
// NOTE: enum order is serialized by index; the first four (AIC, COCO, MPII,
// CrowdPose) keep their original MultiViewRecorder values so existing scenes
// don't silently shift. Append new formats at the end only.
public enum SkeletonFormat
{
    AIC_14 = 0,
    COCO_17 = 1,
    MPII_16 = 2,
    CrowdPose_14 = 3,
    H36M_17 = 4,
    MPI_INF_3DHP_17 = 5,
    CMU_Panoptic_15 = 6,
    SMPL_24 = 7,
    KinectV2_25 = 8,
    LSP_14 = 9,
}

public static class SkeletonFormats
{
    // Semantic joints — the superset we know how to extract from a Unity rig.
    // Every dataset keypoint maps to one of these.
    public enum Joint
    {
        Nose, LeftEye, RightEye, LeftEar, RightEar,
        HeadTop, Head, Neck, Thorax, Chest, Spine, Pelvis,
        LeftCollar, RightCollar,
        LeftShoulder, RightShoulder, LeftElbow, RightElbow,
        LeftWrist, RightWrist, LeftHandTip, RightHandTip, LeftThumb, RightThumb,
        LeftHip, RightHip, LeftKnee, RightKnee, LeftAnkle, RightAnkle, LeftToe, RightToe,
        JOINT_COUNT
    }
    public const int JointCount = (int)Joint.JOINT_COUNT;

    // A dataset layout: JSON tag, keypoint names (export order), the semantic
    // source for each keypoint, and the skeleton edges (keypoint-index pairs).
    public class SkeletonDef
    {
        public string tag;
        public string[] names;
        public Joint[] joints;
        public int[,] edges;
        public int Count => joints.Length;
    }

    // Tunable heuristics for joints that aren't a plain bone position.
    public struct Offsets
    {
        public float headTopUp;     // along Head.up to skull top
        public Vector3 nose;        // head-local
        public Vector3 leftEye;     // head-local (used when no eye bone)
        public Vector3 rightEye;    // head-local
        public Vector3 leftEar;     // head-local
        public Vector3 rightEar;    // head-local
        public float toeForward;    // along Foot.forward to toe tip (no toe bone)

        public static Offsets Default => new Offsets
        {
            headTopUp = 0.14f,
            nose       = new Vector3( 0f,     0.04f, 0.09f),
            leftEye    = new Vector3( 0.035f, 0.05f, 0.085f),
            rightEye   = new Vector3(-0.035f, 0.05f, 0.085f),
            leftEar    = new Vector3( 0.08f,  0.03f, 0f),
            rightEar   = new Vector3(-0.08f,  0.03f, 0f),
            toeForward = 0.12f,
        };
    }

    // Per-rig resolved bones (indexed by Joint) plus optional exact markers.
    public class RigBones
    {
        public Animator animator;
        public readonly Transform[] bone = new Transform[JointCount];
        public Transform headTopMarker;
        public Transform neckMarker;
    }

    // Side colors follow the ViTPose convention: left=green, right=orange, center=blue.
    static readonly Color32 ColLeft   = new Color32(  0, 255,   0, 255);
    static readonly Color32 ColRight  = new Color32(255, 128,   0, 255);
    static readonly Color32 ColCenter = new Color32( 51, 153, 255, 255);

    // ---- Bone-name candidates (Generic rigs / Mixamo "prefix:Name"). ----
    static readonly string[] N_Hips          = { "Hips", "Pelvis", "Hip" };
    static readonly string[] N_Spine         = { "Spine", "Spine1" };
    static readonly string[] N_Chest         = { "Chest", "Spine2" };
    static readonly string[] N_UpperChest    = { "UpperChest", "Spine3", "Chest2" };
    static readonly string[] N_Neck          = { "Neck" };
    static readonly string[] N_Head          = { "Head" };
    static readonly string[] N_LeftEye       = { "LeftEye", "L_Eye" };
    static readonly string[] N_RightEye      = { "RightEye", "R_Eye" };
    static readonly string[] N_LeftCollar    = { "LeftShoulder", "LeftClavicle", "L_Clavicle", "LeftCollar" };
    static readonly string[] N_RightCollar   = { "RightShoulder", "RightClavicle", "R_Clavicle", "RightCollar" };
    static readonly string[] N_LeftUpperArm  = { "LeftArm",      "LeftUpperArm",  "L_UpperArm"  };
    static readonly string[] N_RightUpperArm = { "RightArm",     "RightUpperArm", "R_UpperArm"  };
    static readonly string[] N_LeftLowerArm  = { "LeftForeArm",  "LeftLowerArm",  "L_ForeArm"   };
    static readonly string[] N_RightLowerArm = { "RightForeArm", "RightLowerArm", "R_ForeArm"   };
    static readonly string[] N_LeftHand      = { "LeftHand",     "L_Hand"                       };
    static readonly string[] N_RightHand     = { "RightHand",    "R_Hand"                       };
    static readonly string[] N_LeftMiddle    = { "LeftHandMiddle1", "LeftMiddleProximal" };
    static readonly string[] N_RightMiddle   = { "RightHandMiddle1", "RightMiddleProximal" };
    static readonly string[] N_LeftThumb     = { "LeftHandThumb1", "LeftThumbProximal" };
    static readonly string[] N_RightThumb    = { "RightHandThumb1", "RightThumbProximal" };
    static readonly string[] N_LeftUpperLeg  = { "LeftUpLeg",    "LeftUpperLeg",  "L_UpperLeg", "LeftThigh" };
    static readonly string[] N_RightUpperLeg = { "RightUpLeg",   "RightUpperLeg", "R_UpperLeg", "RightThigh" };
    static readonly string[] N_LeftLowerLeg  = { "LeftLeg",      "LeftLowerLeg",  "L_LowerLeg", "LeftCalf"  };
    static readonly string[] N_RightLowerLeg = { "RightLeg",     "RightLowerLeg", "R_LowerLeg", "RightCalf" };
    static readonly string[] N_LeftFoot      = { "LeftFoot",     "L_Foot"                       };
    static readonly string[] N_RightFoot     = { "RightFoot",    "R_Foot"                       };
    static readonly string[] N_LeftToes      = { "LeftToeBase",  "LeftToes", "L_Toe"            };
    static readonly string[] N_RightToes     = { "RightToeBase", "RightToes", "R_Toe"           };

    static readonly string[] MarkerNames_HeadTop = { "head_top", "HeadTop", "headtop", "skull_top" };
    static readonly string[] MarkerNames_Neck    = { "neck_marker", "NeckMarker" };

    // ---- Resolution ----

    // Resolve every semantic joint we can from the rig into rb.bone[].
    public static void Resolve(Animator a, RigBones rb)
    {
        rb.animator = a;
        bool h = a.isHuman;
        var b = rb.bone;

        b[(int)Joint.Pelvis]        = ResolveBone(a, h, HumanBodyBones.Hips,          N_Hips);
        b[(int)Joint.Spine]         = ResolveBone(a, h, HumanBodyBones.Spine,         N_Spine);
        b[(int)Joint.Chest]         = ResolveBone(a, h, HumanBodyBones.Chest,         N_Chest);
        b[(int)Joint.Thorax]        = ResolveBone(a, h, HumanBodyBones.UpperChest,    N_UpperChest)
                                      ?? b[(int)Joint.Chest];
        b[(int)Joint.Neck]          = ResolveBone(a, h, HumanBodyBones.Neck,          N_Neck);
        b[(int)Joint.Head]          = ResolveBone(a, h, HumanBodyBones.Head,          N_Head);
        b[(int)Joint.LeftEye]       = ResolveBone(a, h, HumanBodyBones.LeftEye,       N_LeftEye);
        b[(int)Joint.RightEye]      = ResolveBone(a, h, HumanBodyBones.RightEye,      N_RightEye);

        b[(int)Joint.LeftCollar]    = ResolveBone(a, h, HumanBodyBones.LeftShoulder,  N_LeftCollar);
        b[(int)Joint.RightCollar]   = ResolveBone(a, h, HumanBodyBones.RightShoulder, N_RightCollar);
        b[(int)Joint.LeftShoulder]  = ResolveBone(a, h, HumanBodyBones.LeftUpperArm,  N_LeftUpperArm);
        b[(int)Joint.RightShoulder] = ResolveBone(a, h, HumanBodyBones.RightUpperArm, N_RightUpperArm);
        b[(int)Joint.LeftElbow]     = ResolveBone(a, h, HumanBodyBones.LeftLowerArm,  N_LeftLowerArm);
        b[(int)Joint.RightElbow]    = ResolveBone(a, h, HumanBodyBones.RightLowerArm, N_RightLowerArm);
        b[(int)Joint.LeftWrist]     = ResolveBone(a, h, HumanBodyBones.LeftHand,      N_LeftHand);
        b[(int)Joint.RightWrist]    = ResolveBone(a, h, HumanBodyBones.RightHand,     N_RightHand);
        b[(int)Joint.LeftHandTip]   = ResolveBone(a, h, HumanBodyBones.LeftMiddleProximal,  N_LeftMiddle);
        b[(int)Joint.RightHandTip]  = ResolveBone(a, h, HumanBodyBones.RightMiddleProximal, N_RightMiddle);
        b[(int)Joint.LeftThumb]     = ResolveBone(a, h, HumanBodyBones.LeftThumbProximal,   N_LeftThumb);
        b[(int)Joint.RightThumb]    = ResolveBone(a, h, HumanBodyBones.RightThumbProximal,  N_RightThumb);

        b[(int)Joint.LeftHip]       = ResolveBone(a, h, HumanBodyBones.LeftUpperLeg,  N_LeftUpperLeg);
        b[(int)Joint.RightHip]      = ResolveBone(a, h, HumanBodyBones.RightUpperLeg, N_RightUpperLeg);
        b[(int)Joint.LeftKnee]      = ResolveBone(a, h, HumanBodyBones.LeftLowerLeg,  N_LeftLowerLeg);
        b[(int)Joint.RightKnee]     = ResolveBone(a, h, HumanBodyBones.RightLowerLeg, N_RightLowerLeg);
        b[(int)Joint.LeftAnkle]     = ResolveBone(a, h, HumanBodyBones.LeftFoot,      N_LeftFoot);
        b[(int)Joint.RightAnkle]    = ResolveBone(a, h, HumanBodyBones.RightFoot,     N_RightFoot);
        b[(int)Joint.LeftToe]       = ResolveBone(a, h, HumanBodyBones.LeftToes,      N_LeftToes);
        b[(int)Joint.RightToe]      = ResolveBone(a, h, HumanBodyBones.RightToes,     N_RightToes);

        rb.headTopMarker = FindBoneByName(a.transform, MarkerNames_HeadTop);
        rb.neckMarker    = FindBoneByName(a.transform, MarkerNames_Neck);
    }

    // True if at least one keypoint of `def` resolves for this rig.
    public static bool AnyResolved(RigBones rb, SkeletonDef def, in Offsets o)
    {
        for (int i = 0; i < def.Count; i++)
        {
            WorldPosition(rb, def.joints[i], o, out bool valid);
            if (valid) return true;
        }
        return false;
    }

    // Resolve a semantic joint to a world position. `valid` reports whether the
    // rig could actually supply it (drives the exported "visible" flag).
    public static Vector3 WorldPosition(RigBones rb, Joint joint, in Offsets o, out bool valid)
    {
        var b = rb.bone;
        Transform head = b[(int)Joint.Head];

        switch (joint)
        {
            case Joint.HeadTop:
                if (rb.headTopMarker != null) { valid = true; return rb.headTopMarker.position; }
                valid = head != null;
                return head != null ? head.position + head.up * o.headTopUp : Vector3.zero;

            case Joint.Nose:
                valid = head != null;
                return head != null ? head.TransformPoint(o.nose) : Vector3.zero;

            case Joint.LeftEye:
                if (b[(int)Joint.LeftEye] != null) { valid = true; return b[(int)Joint.LeftEye].position; }
                valid = head != null;
                return head != null ? head.TransformPoint(o.leftEye) : Vector3.zero;

            case Joint.RightEye:
                if (b[(int)Joint.RightEye] != null) { valid = true; return b[(int)Joint.RightEye].position; }
                valid = head != null;
                return head != null ? head.TransformPoint(o.rightEye) : Vector3.zero;

            case Joint.LeftEar:
                valid = head != null;
                return head != null ? head.TransformPoint(o.leftEar) : Vector3.zero;

            case Joint.RightEar:
                valid = head != null;
                return head != null ? head.TransformPoint(o.rightEar) : Vector3.zero;

            case Joint.Neck:
            {
                if (rb.neckMarker != null) { valid = true; return rb.neckMarker.position; }
                Transform t = b[(int)Joint.Neck];
                if (t != null) { valid = true; return t.position; }
                return MidShoulder(rb, out valid);
            }

            case Joint.Thorax:
            {
                Transform t = b[(int)Joint.Thorax] ?? b[(int)Joint.Chest] ?? b[(int)Joint.Neck];
                if (t != null) { valid = true; return t.position; }
                return MidShoulder(rb, out valid);
            }

            case Joint.Spine:
            {
                Transform t = b[(int)Joint.Spine] ?? b[(int)Joint.Chest];
                if (t != null) { valid = true; return t.position; }
                Transform pelvis = b[(int)Joint.Pelvis];
                Vector3 top = WorldPosition(rb, Joint.Thorax, o, out bool topOk);
                if (pelvis != null && topOk) { valid = true; return 0.5f * (pelvis.position + top); }
                valid = false; return Vector3.zero;
            }

            case Joint.LeftToe:  return ToeOrFoot(b[(int)Joint.LeftToe],  b[(int)Joint.LeftAnkle],  o.toeForward, out valid);
            case Joint.RightToe: return ToeOrFoot(b[(int)Joint.RightToe], b[(int)Joint.RightAnkle], o.toeForward, out valid);

            case Joint.LeftHandTip:  return TipOrWrist(b[(int)Joint.LeftHandTip],  b[(int)Joint.LeftWrist],  out valid);
            case Joint.RightHandTip: return TipOrWrist(b[(int)Joint.RightHandTip], b[(int)Joint.RightWrist], out valid);
            case Joint.LeftThumb:    return TipOrWrist(b[(int)Joint.LeftThumb],    b[(int)Joint.LeftWrist],  out valid);
            case Joint.RightThumb:   return TipOrWrist(b[(int)Joint.RightThumb],   b[(int)Joint.RightWrist], out valid);

            default:
            {
                Transform t = b[(int)joint];
                valid = t != null;
                return t != null ? t.position : Vector3.zero;
            }
        }
    }

    public static Quaternion WorldRotation(RigBones rb, Joint joint)
    {
        Transform t = rb.bone[(int)joint];
        if (t != null) return t.rotation;
        switch (joint)
        {
            case Joint.HeadTop:
            case Joint.Nose:
            case Joint.LeftEar:
            case Joint.RightEar:
                Transform head = rb.bone[(int)Joint.Head];
                if (head != null) return head.rotation;
                break;
        }
        return rb.animator != null ? rb.animator.transform.rotation : Quaternion.identity;
    }

    static Vector3 MidShoulder(RigBones rb, out bool valid)
    {
        Transform l = rb.bone[(int)Joint.LeftShoulder];
        Transform r = rb.bone[(int)Joint.RightShoulder];
        valid = l != null && r != null;
        return valid ? 0.5f * (l.position + r.position) : Vector3.zero;
    }

    static Vector3 ToeOrFoot(Transform toe, Transform foot, float fwd, out bool valid)
    {
        if (toe != null) { valid = true; return toe.position; }
        if (foot != null) { valid = true; return foot.position + foot.forward * fwd; }
        valid = false; return Vector3.zero;
    }

    static Vector3 TipOrWrist(Transform tip, Transform wrist, out bool valid)
    {
        if (tip != null) { valid = true; return tip.position; }
        if (wrist != null) { valid = true; return wrist.position; }
        valid = false; return Vector3.zero;
    }

    static Transform ResolveBone(Animator anim, bool isHumanoid, HumanBodyBones bone, string[] nameCandidates)
    {
        Transform t = FindBoneByName(anim.transform, nameCandidates);
        if (t != null) return t;
        if (isHumanoid && anim.avatar != null)
        {
            try { return anim.GetBoneTransform(bone); }
            catch (System.InvalidOperationException) { /* no avatar bound */ }
        }
        return null;
    }

    static Transform FindBoneByName(Transform root, string[] candidates)
    {
        for (int i = 0; i < candidates.Length; i++)
        {
            Transform found = FindBoneRecursive(root, candidates[i]);
            if (found != null) return found;
        }
        return null;
    }

    static Transform FindBoneRecursive(Transform node, string name)
    {
        if (node.name == name || (node.name.Length > name.Length && node.name.EndsWith(":" + name)))
            return node;
        for (int i = 0; i < node.childCount; i++)
        {
            Transform result = FindBoneRecursive(node.GetChild(i), name);
            if (result != null) return result;
        }
        return null;
    }

    // ---- Colors ----

    public static Color32[] Colors(SkeletonDef d)
    {
        var c = new Color32[d.Count];
        for (int i = 0; i < d.Count; i++) c[i] = SideColor(d.names[i]);
        return c;
    }

    static Color32 SideColor(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.StartsWith("left") || n.StartsWith("l-") || n.StartsWith("l_")) return ColLeft;
        if (n.StartsWith("right") || n.StartsWith("r-") || n.StartsWith("r_")) return ColRight;
        return ColCenter;
    }

    // ---- Registry ----

    public static SkeletonDef Build(SkeletonFormat f)
    {
        switch (f)
        {
            case SkeletonFormat.COCO_17:
                return new SkeletonDef {
                    tag = "coco",
                    names = new[] {
                        "nose","left_eye","right_eye","left_ear","right_ear",
                        "left_shoulder","right_shoulder","left_elbow","right_elbow",
                        "left_wrist","right_wrist","left_hip","right_hip",
                        "left_knee","right_knee","left_ankle","right_ankle" },
                    joints = new[] {
                        Joint.Nose, Joint.LeftEye, Joint.RightEye, Joint.LeftEar, Joint.RightEar,
                        Joint.LeftShoulder, Joint.RightShoulder, Joint.LeftElbow, Joint.RightElbow,
                        Joint.LeftWrist, Joint.RightWrist, Joint.LeftHip, Joint.RightHip,
                        Joint.LeftKnee, Joint.RightKnee, Joint.LeftAnkle, Joint.RightAnkle },
                    edges = new[,] {
                        {15,13},{13,11},{16,14},{14,12},{11,12},{5,11},{6,12},{5,6},
                        {5,7},{6,8},{7,9},{8,10},{1,2},{0,1},{0,2},{1,3},{2,4},{3,5},{4,6} },
                };

            case SkeletonFormat.MPII_16:
                return new SkeletonDef {
                    tag = "mpii",
                    names = new[] {
                        "right_ankle","right_knee","right_hip","left_hip","left_knee","left_ankle",
                        "pelvis","thorax","upper_neck","head_top",
                        "right_wrist","right_elbow","right_shoulder",
                        "left_shoulder","left_elbow","left_wrist" },
                    joints = new[] {
                        Joint.RightAnkle, Joint.RightKnee, Joint.RightHip, Joint.LeftHip, Joint.LeftKnee, Joint.LeftAnkle,
                        Joint.Pelvis, Joint.Thorax, Joint.Neck, Joint.HeadTop,
                        Joint.RightWrist, Joint.RightElbow, Joint.RightShoulder,
                        Joint.LeftShoulder, Joint.LeftElbow, Joint.LeftWrist },
                    edges = new[,] {
                        {0,1},{1,2},{2,6},{6,3},{3,4},{4,5},{6,7},{7,8},{8,9},
                        {8,12},{12,11},{11,10},{8,13},{13,14},{14,15} },
                };

            case SkeletonFormat.CrowdPose_14:
                return new SkeletonDef {
                    tag = "crowdpose",
                    names = new[] {
                        "left_shoulder","right_shoulder","left_elbow","right_elbow",
                        "left_wrist","right_wrist","left_hip","right_hip",
                        "left_knee","right_knee","left_ankle","right_ankle","top_head","neck" },
                    joints = new[] {
                        Joint.LeftShoulder, Joint.RightShoulder, Joint.LeftElbow, Joint.RightElbow,
                        Joint.LeftWrist, Joint.RightWrist, Joint.LeftHip, Joint.RightHip,
                        Joint.LeftKnee, Joint.RightKnee, Joint.LeftAnkle, Joint.RightAnkle,
                        Joint.HeadTop, Joint.Neck },
                    edges = new[,] {
                        {10,8},{8,6},{11,9},{9,7},{6,7},{0,6},{1,7},{0,1},
                        {0,2},{1,3},{2,4},{3,5},{12,13},{1,13},{0,13} },
                };

            case SkeletonFormat.H36M_17:
                return new SkeletonDef {
                    tag = "h36m",
                    names = new[] {
                        "root","right_hip","right_knee","right_foot","left_hip","left_knee","left_foot",
                        "spine","thorax","neck_base","head",
                        "left_shoulder","left_elbow","left_wrist","right_shoulder","right_elbow","right_wrist" },
                    joints = new[] {
                        Joint.Pelvis, Joint.RightHip, Joint.RightKnee, Joint.RightAnkle,
                        Joint.LeftHip, Joint.LeftKnee, Joint.LeftAnkle,
                        Joint.Spine, Joint.Thorax, Joint.Neck, Joint.Head,
                        Joint.LeftShoulder, Joint.LeftElbow, Joint.LeftWrist,
                        Joint.RightShoulder, Joint.RightElbow, Joint.RightWrist },
                    edges = new[,] {
                        {0,4},{4,5},{5,6},{0,1},{1,2},{2,3},{0,7},{7,8},
                        {8,9},{9,10},{8,11},{11,12},{12,13},{8,14},{14,15},{15,16} },
                };

            case SkeletonFormat.MPI_INF_3DHP_17:
                return new SkeletonDef {
                    tag = "mpi_inf_3dhp",
                    names = new[] {
                        "head_top","neck","right_shoulder","right_elbow","right_wrist",
                        "left_shoulder","left_elbow","left_wrist",
                        "right_hip","right_knee","right_ankle","left_hip","left_knee","left_ankle",
                        "root","spine","head" },
                    joints = new[] {
                        Joint.HeadTop, Joint.Neck, Joint.RightShoulder, Joint.RightElbow, Joint.RightWrist,
                        Joint.LeftShoulder, Joint.LeftElbow, Joint.LeftWrist,
                        Joint.RightHip, Joint.RightKnee, Joint.RightAnkle, Joint.LeftHip, Joint.LeftKnee, Joint.LeftAnkle,
                        Joint.Pelvis, Joint.Spine, Joint.Head },
                    edges = new[,] {
                        {1,2},{2,3},{3,4},{1,5},{5,6},{6,7},{14,8},{8,9},{9,10},
                        {14,11},{11,12},{12,13},{0,16},{16,1},{1,15},{15,14} },
                };

            case SkeletonFormat.CMU_Panoptic_15:
                return new SkeletonDef {
                    tag = "panoptic",
                    names = new[] {
                        "neck","nose","mid_hip","l_shoulder","l_elbow","l_wrist",
                        "l_hip","l_knee","l_ankle","r_shoulder","r_elbow","r_wrist",
                        "r_hip","r_knee","r_ankle" },
                    joints = new[] {
                        Joint.Neck, Joint.Nose, Joint.Pelvis, Joint.LeftShoulder, Joint.LeftElbow, Joint.LeftWrist,
                        Joint.LeftHip, Joint.LeftKnee, Joint.LeftAnkle, Joint.RightShoulder, Joint.RightElbow, Joint.RightWrist,
                        Joint.RightHip, Joint.RightKnee, Joint.RightAnkle },
                    edges = new[,] {
                        {0,1},{0,2},{0,3},{3,4},{4,5},{0,9},{9,10},{10,11},
                        {2,6},{6,7},{7,8},{2,12},{12,13},{13,14} },
                };

            case SkeletonFormat.SMPL_24:
                return new SkeletonDef {
                    tag = "smpl",
                    names = new[] {
                        "pelvis","left_hip","right_hip","spine1","left_knee","right_knee",
                        "spine2","left_ankle","right_ankle","spine3","left_foot","right_foot",
                        "neck","left_collar","right_collar","head","left_shoulder","right_shoulder",
                        "left_elbow","right_elbow","left_wrist","right_wrist","left_hand","right_hand" },
                    joints = new[] {
                        Joint.Pelvis, Joint.LeftHip, Joint.RightHip, Joint.Spine, Joint.LeftKnee, Joint.RightKnee,
                        Joint.Chest, Joint.LeftAnkle, Joint.RightAnkle, Joint.Thorax, Joint.LeftToe, Joint.RightToe,
                        Joint.Neck, Joint.LeftCollar, Joint.RightCollar, Joint.Head, Joint.LeftShoulder, Joint.RightShoulder,
                        Joint.LeftElbow, Joint.RightElbow, Joint.LeftWrist, Joint.RightWrist, Joint.LeftHandTip, Joint.RightHandTip },
                    edges = new[,] {
                        {0,1},{0,2},{0,3},{1,4},{2,5},{3,6},{4,7},{5,8},{6,9},{7,10},{8,11},{9,12},
                        {9,13},{9,14},{12,15},{13,16},{14,17},{16,18},{17,19},{18,20},{19,21},{20,22},{21,23} },
                };

            case SkeletonFormat.KinectV2_25:
                return new SkeletonDef {
                    tag = "kinect_v2",
                    names = new[] {
                        "spine_base","spine_mid","neck","head","shoulder_left","elbow_left","wrist_left","hand_left",
                        "shoulder_right","elbow_right","wrist_right","hand_right","hip_left","knee_left","ankle_left","foot_left",
                        "hip_right","knee_right","ankle_right","foot_right","spine_shoulder",
                        "hand_tip_left","thumb_left","hand_tip_right","thumb_right" },
                    joints = new[] {
                        Joint.Pelvis, Joint.Spine, Joint.Neck, Joint.Head, Joint.LeftShoulder, Joint.LeftElbow, Joint.LeftWrist, Joint.LeftHandTip,
                        Joint.RightShoulder, Joint.RightElbow, Joint.RightWrist, Joint.RightHandTip, Joint.LeftHip, Joint.LeftKnee, Joint.LeftAnkle, Joint.LeftToe,
                        Joint.RightHip, Joint.RightKnee, Joint.RightAnkle, Joint.RightToe, Joint.Thorax,
                        Joint.LeftHandTip, Joint.LeftThumb, Joint.RightHandTip, Joint.RightThumb },
                    edges = new[,] {
                        {0,1},{1,20},{20,2},{2,3},
                        {20,4},{4,5},{5,6},{6,7},{7,21},{7,22},
                        {20,8},{8,9},{9,10},{10,11},{11,23},{11,24},
                        {0,12},{12,13},{13,14},{14,15},
                        {0,16},{16,17},{17,18},{18,19} },
                };

            case SkeletonFormat.LSP_14:
                return new SkeletonDef {
                    tag = "lsp",
                    names = new[] {
                        "right_ankle","right_knee","right_hip","left_hip","left_knee","left_ankle",
                        "right_wrist","right_elbow","right_shoulder","left_shoulder","left_elbow","left_wrist",
                        "neck","head_top" },
                    joints = new[] {
                        Joint.RightAnkle, Joint.RightKnee, Joint.RightHip, Joint.LeftHip, Joint.LeftKnee, Joint.LeftAnkle,
                        Joint.RightWrist, Joint.RightElbow, Joint.RightShoulder, Joint.LeftShoulder, Joint.LeftElbow, Joint.LeftWrist,
                        Joint.Neck, Joint.HeadTop },
                    edges = new[,] {
                        {0,1},{1,2},{2,3},{3,4},{4,5},{6,7},{7,8},{8,9},{9,10},{10,11},{8,12},{9,12},{12,13} },
                };

            case SkeletonFormat.AIC_14:
            default:
                return new SkeletonDef {
                    tag = "aic",
                    names = new[] {
                        "right_shoulder","right_elbow","right_wrist",
                        "left_shoulder","left_elbow","left_wrist",
                        "right_hip","right_knee","right_ankle",
                        "left_hip","left_knee","left_ankle","head_top","neck" },
                    joints = new[] {
                        Joint.RightShoulder, Joint.RightElbow, Joint.RightWrist,
                        Joint.LeftShoulder, Joint.LeftElbow, Joint.LeftWrist,
                        Joint.RightHip, Joint.RightKnee, Joint.RightAnkle,
                        Joint.LeftHip, Joint.LeftKnee, Joint.LeftAnkle, Joint.HeadTop, Joint.Neck },
                    edges = new[,] {
                        {2,1},{1,0},{0,13},{13,3},{3,4},{4,5},
                        {8,7},{7,6},{6,9},{9,10},{10,11},{12,13},{0,6},{3,9} },
                };
        }
    }
}
