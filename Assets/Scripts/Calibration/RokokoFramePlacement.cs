using System;
using System.Collections.Generic;
using UnityEngine;

// Places the Rokoko frame of a recorded take (the parent of the avatar playing the baked
// clip) at its absolute position in the world frame, from the registration the
// HumanDatasetRecording tools computed for that take (tools/07_recalage.py):
//
//   world = rot_y(yaw) @ negate_x(rokoko) + tvec     (right-handed, Y up, meters)
//
// worldFrame is the same MOCAPcenter the CalibratedCamera extrinsics refer to, so the
// avatar and the cameras share one world. Only worldFrame's position and rotation are used.
// The registration JSON holds a single static pose (the take's median); the take's own
// registration drifts by about +-0.2 m / +-4 degrees around it.
//
// The transform of this GameObject is driven by the component. Put it on the parent of
// the avatar, with the avatar at identity under it: the baked clip's hips curves are in
// Rokoko's frame, so this transform IS Rokoko's frame. Disable the component to move it by hand.
[ExecuteAlways]
[DisallowMultipleComponent]
public class RokokoFramePlacement : MonoBehaviour
{
    [Tooltip("<dataset>_registration.json: yaw_deg and tvec of the Rokoko -> world registration.")]
    public TextAsset registrationJson;
    [Tooltip("Frame the registration is expressed in (MOCAPcenter). Only its position and rotation are used.")]
    public Transform worldFrame;
    [Tooltip("Must match the CalibratedCamera cameras so avatar and cameras agree.")]
    public CalibratedCamera.WorldMirror handedness = CalibratedCamera.WorldMirror.NegateX;

    [Header("Loaded from JSON (overwritten on reload)")]
    public float yawDeg;
    public Vector3 tvec;
    [Tooltip("Rokoko frame's position / yaw in the world frame, in Unity handedness.")]
    public Vector3 positionInFrame;
    public float yawInFrameDeg;

    Quaternion rotationInFrame = Quaternion.identity;
    bool loaded;

    void OnEnable() { Apply(); }
    void OnDisable() { loaded = false; }

#if UNITY_EDITOR
    void OnValidate()
    {
        UnityEditor.EditorApplication.delayCall += () => { if (this != null) Apply(); };
    }
#endif

    void LateUpdate() { if (loaded) ApplyPose(); }

    [ContextMenu("Reload registration from JSON")]
    public void Apply()
    {
        loaded = false;
        if (registrationJson == null)
        {
            Debug.LogWarning($"[RokokoFramePlacement] '{name}': assign the registration JSON.", this);
            return;
        }

        try
        {
            var root = (Dictionary<string, object>)MiniJson.Parse(registrationJson.text);
            yawDeg = (float)(double)root["yaw_deg"];
            var t = (List<object>)root["tvec"];
            tvec = new Vector3((float)(double)t[0], (float)(double)t[1], (float)(double)t[2]);
        }
        catch (Exception e)
        {
            Debug.LogError($"[RokokoFramePlacement] '{name}': failed to load registration: {e.Message}", this);
            return;
        }

        FrameInWorld(yawDeg, tvec, handedness, out positionInFrame, out rotationInFrame);
        yawInFrameDeg = rotationInFrame.eulerAngles.y;
        loaded = true;
        ApplyPose();
    }

    void ApplyPose()
    {
        Vector3 p; Quaternion r;
        if (worldFrame != null)
        {
            Quaternion fr = worldFrame.rotation;
            p = worldFrame.position + fr * positionInFrame;
            r = fr * rotationInFrame;
        }
        else { p = positionInFrame; r = rotationInFrame; }

        if (transform.position != p || transform.rotation != r) transform.SetPositionAndRotation(p, r);
    }

    // Pose of the Rokoko frame (Unity-handed) inside the world frame (Unity-handed).
    // A Rokoko point p_R goes to the right-handed world as Ry(yaw) * Sx * p_R + tvec (Sx
    // negates x: Rokoko streams left-handed), then into Unity's world frame through the
    // handedness mirror Sw. As one transform: rotation = Sw * Ry(yaw) * Sx, position = Sw * tvec.
    // With Sw = Sx this is a rotation by -yaw about Y and a mirrored translation.
    public static void FrameInWorld(float yawDeg, Vector3 tvec, CalibratedCamera.WorldMirror mirror,
                                    out Vector3 position, out Quaternion rotation)
    {
        double a = yawDeg * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a);
        double[,] ry = { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } };
        double[,] sx = { { -1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        double[,] sw = mirror == CalibratedCamera.WorldMirror.NegateX
            ? new double[,] { { -1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }
            : new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, -1 } };

        double[,] m = Mul(sw, Mul(ry, sx));
        // m is a proper rotation; its columns are the images of the Rokoko x / y / z axes.
        rotation = Quaternion.LookRotation(new Vector3((float)m[0, 2], (float)m[1, 2], (float)m[2, 2]),
                                           new Vector3((float)m[0, 1], (float)m[1, 1], (float)m[2, 1]));
        position = new Vector3((float)(sw[0, 0] * tvec.x), (float)(sw[1, 1] * tvec.y), (float)(sw[2, 2] * tvec.z));
    }

    static double[,] Mul(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
        for (int k = 0; k < 3; k++)
            r[i, j] += a[i, k] * b[k, j];
        return r;
    }
}
