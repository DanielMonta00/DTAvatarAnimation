using System;
using UnityEngine;

// The 2D side of Faster-VoxelPose. The network is a lifted-2D method: a ResNet gives every camera one heatmap per joint, and the
// 3D stages only ever see those maps, back-projected into the voxel volume. The server hands them back already in the geometry of
// the frame that was sent (see FvpProtocol), and this paints them over the very image they were computed from, in a corner panel
// on every display (FvpHeatPanel). A weak or misplaced blob there is where a bad 3D joint comes from.
//
//   J / Shift+J   next / previous joint (-1 = the strongest of all joints)      G   show / hide
//
// The joint is chosen per estimate (the server only sends the one asked for), so a change applies from the next estimate on; the
// frames in the history keep the map they were made with. The image under the map is kept for the newest estimate only.
public partial class FasterVoxelPoseLive
{
    [Header("2D heatmaps")]
    [Tooltip("The 2D joint heatmaps of every camera painted over the image they were computed from, in the top-right corner of each display. G toggles it. Costs about 100 KB per estimate over the socket and a couple of milliseconds on the server.")]
    public bool showHeatmaps = true;
    [Tooltip("Which joint: -1 = the strongest of all joints, 0..14 = one joint (neck, nose, mid-hip, l-shoulder, l-elbow, l-wrist, l-hip, l-knee, l-ankle, r-shoulder, r-elbow, r-wrist, r-hip, r-knee, r-ankle). J / Shift+J step through them. Applies from the next estimate.")]
    [Range(-1, 14)] public int heatmapJoint = -1;
    [Tooltip("How many of the newest estimates keep their heatmaps for browsing the history (32 KB per camera each).")]
    [Min(10)] public int heatHistoryFrames = 300;

    // what the next frame asks the server for
    int HeatRequest => showHeatmaps ? Mathf.Clamp(heatmapJoint, -1, FvpSkeleton.Count - 1) : -2;

    readonly FvpHeatTextures heatTextures = new FvpHeatTextures();
    readonly FvpHeatData heatData = new FvpHeatData();
    int heatKey;

    public FvpHeatData HeatData => heatData;

    public void NextHeatJoint(int direction)
    {
        showHeatmaps = true;
        int k = heatmapJoint + 1;                       // -1 .. 14  ->  0 .. 15
        k = ((k + direction) % (FvpSkeleton.Count + 1) + FvpSkeleton.Count + 1) % (FvpSkeleton.Count + 1);
        heatmapJoint = k - 1;
    }

    static string JointLabel(int joint) => joint < 0 ? "all joints" : joint < FvpSkeleton.Names.Length ? FvpSkeleton.Names[joint] : "joint " + joint;

    // ---------------- receiving ----------------

    void ReadHeat(FvpFrame f, FvpMessage m)
    {
        int views = (int)m.Num("heat_views"), hw = (int)m.Num("heat_w"), hh = (int)m.Num("heat_h");
        if (views <= 0 || hw <= 0 || hh <= 0 || views > FvpHeatData.MaxViews) return;
        int poseBytes = (int)m.Num("poses_bytes", m.bin.Length), each = hw * hh;
        if (m.bin.Length < poseBytes + views * each) return;

        f.heat = new byte[views][];
        for (int v = 0; v < views; v++)
        {
            f.heat[v] = new byte[each];
            Buffer.BlockCopy(m.bin, poseBytes + v * each, f.heat[v], 0, each);
        }
        f.heatW = hw; f.heatH = hh;
        f.heatJoint = (int)m.Num("heat_joint", -1);
    }

    // A new estimate is in the history: keep the window of heatmaps bounded and put the new one (and its image) on the textures.
    void OnHeatFrame(FvpFrame f)
    {
        int drop = history.Count - 1 - heatHistoryFrames;
        if (drop >= 0 && drop < history.Count) history[drop].heat = null;

        if (!showHeatmaps || f.heat == null) return;
        FillHeatTexture(f);
        UploadHeatImage(f);
    }

    // ---------------- textures ----------------

    void FillHeatTexture(FvpFrame f) => heatTextures.FillHeat(f.heat, f.heatW, f.heatH, f);

    // The picture the maps belong to: exactly what was sent to the network.
    void UploadHeatImage(FvpFrame f)
    {
        if (f.raw != null && f.raw.Length == f.heat.Length) heatTextures.UploadImage(f.raw, f.width, f.height, f);
    }

    void DestroyHeat() => heatTextures.DestroyAll();

    // ---------------- what the panels show ----------------

    // Every frame, cheap: only rebuilds when the frame on show, the joint or the settings changed.
    void RefreshHeat()
    {
        heatData.visible = showHeatmaps && PanelsVisible;
        if (!showHeatmaps) return;

        FvpFrame f = IsReady ? displayed : null;
        if (f != null && f.heat != null && heatTextures.heatOwner != f) FillHeatTexture(f);   // browsing the history
        bool imageOk = f != null && f == heatTextures.imageOwner;

        int key = f == null ? 0 : f.id * 31 + (f.heat != null ? 1 : 2) + (imageOk ? 4 : 0) + (heatmapJoint + 2) * 97 + (paused ? 8 : 0);
        key ^= (cameras != null ? cameras.Count : 0) << 20;
        if (key == heatKey) return;
        heatKey = key;
        heatData.stamp++;

        if (f == null) { heatData.views = 0; heatData.message = ""; heatData.hint = ""; return; }
        if (f.heat == null)
        {
            heatData.views = 0;
            heatData.message = f.placeholder ? "No estimate for this frame yet: its 2D maps come with it."
                             : f.estimated && f.id > 0 ? "No 2D heatmap for this frame (older than the kept window, or the server needs a restart)." : "";
            heatData.hint = "";
            return;
        }

        int views = Mathf.Min(f.heat.Length, FvpHeatData.MaxViews);
        heatData.views = views;
        heatData.aspect = f.heatW > 0 ? (float)f.heatH / f.heatW : 9f / 16f;
        for (int v = 0; v < views; v++)
        {
            Camera cam = cameras != null && v < cameras.Count ? cameras[v] : null;
            heatData.display[v] = cam != null ? cam.targetDisplay : -1;
            heatData.heat[v] = heatTextures.heat != null && v < heatTextures.heat.Length ? heatTextures.heat[v] : null;
            heatData.image[v] = imageOk && heatTextures.image != null && v < heatTextures.image.Length ? heatTextures.image[v] : null;
            heatData.label[v] = $"{(cam != null ? cam.name.Trim() : "view " + v)}  ·  {JointLabel(f.heatJoint)}  ·  peak {heatTextures.peak[v]:F2}";
        }
        heatData.message = "";
        heatData.hint = "2D network output: all the 3D stage sees." + (imageOk ? "" : "\n(image kept for the newest estimate only)") +
                        "\nJ / Shift+J joint   G hide" + (f.heatJoint != HeatRequest ? "\n(a change applies to the next estimate)" : "");
    }
}
