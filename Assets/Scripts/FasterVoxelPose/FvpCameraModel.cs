using System;

// One calibrated view as Faster-VoxelPose wants it, plus the projection used to draw its overlay.
//
// Two frames are in play:
//   Unity world  left-handed, Y up, metres. The camera pose and every displayed point live here.
//   model frame  what the network works in: millimetres with Z up, A = Rot_x(90): (x, y, z) -> (x, -z, y),
//                the conversion validated against the recorded MultiViewSession (reprojection < 0.002 px).
// The network projects voxel centres with x_cam = R * (x - T): R rotates the model frame into the
// OpenCV camera frame (x right, y down, z forward) and T is the camera centre in the model frame.
//
// Pixels follow OpenCV: origin top-left, pixel centres on integers, so a point lands at (u + 0.5, v + 0.5)
// on a screen that draws the image edge-to-edge.
public sealed class FvpCameraModel
{
    public string name;
    public int width, height;        // size of the frames sent to the server
    public double fx, fy, cx, cy;    // intrinsics at that size
    public double k1, k2, p1, p2, k3; // OpenCV lens model of the image as sent (all zero for an ideal pinhole)
    // Normalized radius up to which the lens polynomial is valid (LensDistortion.UsableRadius); 0 = unlimited.
    public double maxRadius;

    // Camera centre in the Unity world (metres), and the rows of the OpenCV rotation in Unity world axes:
    // x_cam = rot * (x - centre), rows = right, -up, forward.
    public readonly double[] centre = new double[3];
    public readonly double[] rot = new double[9];

    // pos / right / up / forward are the Unity Transform's position and axes in world space.
    // dist = k1, k2, p1, p2, k3 (OpenCV order) or null for an ideal pinhole.
    public static FvpCameraModel FromUnityAxes(string name, int width, int height,
        double[] pos, double[] right, double[] up, double[] forward,
        double fx, double fy, double cx, double cy, double[] dist, double maxRadius)
    {
        var m = new FvpCameraModel { name = name, width = width, height = height, fx = fx, fy = fy, cx = cx, cy = cy, maxRadius = maxRadius };
        for (int i = 0; i < 3; i++) m.centre[i] = pos[i];

        // OpenCV axes in Unity world coordinates: x right, y DOWN (= -up), z forward.
        Set(m.rot, 0, right[0], right[1], right[2]);
        Set(m.rot, 1, -up[0], -up[1], -up[2]);
        Set(m.rot, 2, forward[0], forward[1], forward[2]);

        if (dist != null)
        {
            m.k1 = dist.Length > 0 ? dist[0] : 0; m.k2 = dist.Length > 1 ? dist[1] : 0;
            m.p1 = dist.Length > 2 ? dist[2] : 0; m.p2 = dist.Length > 3 ? dist[3] : 0;
            m.k3 = dist.Length > 4 ? dist[4] : 0;
        }
        return m;
    }

    static void Set(double[] r, int row, double a, double b, double c) { r[row * 3] = a; r[row * 3 + 1] = b; r[row * 3 + 2] = c; }

    // R and T for the network: R = R_cv * A^T (row (a, b, c) -> (a, -c, b)), T = A * centre * 1000.
    public void ToModelFrame(out double[] R, out double[] T)
    {
        R = new double[9];
        for (int i = 0; i < 3; i++)
        {
            double a = rot[i * 3], b = rot[i * 3 + 1], c = rot[i * 3 + 2];
            R[i * 3] = a; R[i * 3 + 1] = -c; R[i * 3 + 2] = b;
        }
        T = new[] { centre[0] * 1000.0, -centre[2] * 1000.0, centre[1] * 1000.0 };
    }

    // Unity world point (metres) -> pixel of the frame sent to the server. False when the point is behind
    // the camera or past the radius where the lens model holds (it cannot be imaged).
    public bool Project(double x, double y, double z, out double u, out double v)
    {
        u = v = 0;
        double dx = x - centre[0], dy = y - centre[1], dz = z - centre[2];
        double xc = rot[0] * dx + rot[1] * dy + rot[2] * dz;
        double yc = rot[3] * dx + rot[4] * dy + rot[5] * dz;
        double zc = rot[6] * dx + rot[7] * dy + rot[8] * dz;
        if (zc <= 1e-6) return false;

        double xn = xc / zc, yn = yc / zc;
        double r2 = xn * xn + yn * yn;
        if (maxRadius > 0 && r2 > maxRadius * maxRadius) return false;

        double radial = 1.0 + r2 * (k1 + r2 * (k2 + r2 * k3));
        double xd = xn * radial + 2.0 * p1 * xn * yn + p2 * (r2 + 2.0 * xn * xn);
        double yd = yn * radial + p1 * (r2 + 2.0 * yn * yn) + 2.0 * p2 * xn * yn;
        u = fx * xd + cx;
        v = fy * yd + cy;
        return true;
    }

    // Everything the server would notice: used to decide whether the cameras moved and the model head
    // must be rebuilt. Rounded like the config JSON so Transform float noise does not count.
    public bool SameAs(FvpCameraModel o)
    {
        if (o == null || width != o.width || height != o.height) return false;
        for (int i = 0; i < 3; i++) if (Math.Abs(centre[i] - o.centre[i]) > 5e-4) return false;
        for (int i = 0; i < 9; i++) if (Math.Abs(rot[i] - o.rot[i]) > 2e-5) return false;
        return Math.Abs(fx - o.fx) < 1e-2 && Math.Abs(fy - o.fy) < 1e-2 && Math.Abs(cx - o.cx) < 1e-2 && Math.Abs(cy - o.cy) < 1e-2 &&
               k1 == o.k1 && k2 == o.k2 && k3 == o.k3 && p1 == o.p1 && p2 == o.p2 && Math.Abs(maxRadius - o.maxRadius) < 1e-5;
    }

    // ---- capture volume (Unity metres <-> model frame) ----

    public static double[] UnityPointToModelMm(double x, double y, double z) => new[] { x * 1000.0, -z * 1000.0, y * 1000.0 };

    // Axis sizes: model x = Unity x, model y = Unity z, model z = Unity y.
    public static double[] UnitySizeToModelMm(double sx, double sy, double sz) => new[] { sx * 1000.0, sz * 1000.0, sy * 1000.0 };

    // Network output (mm, Z up) -> Unity world (metres): A^T * x / 1000.
    public static void ModelMmToUnity(double x, double y, double z, out double ux, out double uy, out double uz)
    {
        ux = x / 1000.0; uy = z / 1000.0; uz = -y / 1000.0;
    }
}
