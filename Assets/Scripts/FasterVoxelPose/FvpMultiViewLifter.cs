using System;
using System.Collections.Generic;

// Lifts per-view 2D skeletons to 3D by multi-view triangulation. Pure maths (no UnityEngine), like FvpCameraModel.
//
// A 2D estimator such as ViTPose gives, for every camera view on its own, the people it found and their joints in pixels. With the
// cameras calibrated, a joint seen in two or more views is a point where their rays meet. Two steps:
//
//   1. Who is who across views. Every person of one view is compared with every person of another: for the joints both have, the
//      distance between the two joint rays (which would intersect if it were one person's joint); the median over the joints is the
//      cost. People are then grouped greedily, cheapest pairs first, a group taking at most one person per view and only when every
//      member fits every other (a clique).
//   2. Triangulation. For every joint of a group, the point nearest to all of its rays (least squares weighted by the 2D confidence),
//      robustified: the ray that disagrees most is dropped while it is more than `inlierMetres` away and at least two remain, and a
//      joint whose rays do not agree, or that falls behind a camera, is left out.
//
// Everything is in the Unity world (metres), the frame FvpCameraModel works in.
public sealed class MvPerson2D
{
    public int view;
    public double[] x, y, conf;                 // pixels of the frame sent, per joint; conf below the threshold = not seen
    public int Count => x.Length;
}

public sealed class MvPerson3D
{
    public double[] x, y, z;                    // metres, Unity world
    public bool[] valid;                        // the joint was triangulated
    public double[] conf;                       // mean 2D confidence of the rays used
    public int[] raysUsed;                      // how many views the joint came from
    public readonly List<MvPerson2D> members = new List<MvPerson2D>();   // the 2D people it was made from, one per view
    public double reprojectionPx = -1;          // mean distance of the 3D joints, projected back, from the 2D detections they came from
    public int ValidCount { get { int n = 0; foreach (bool v in valid) if (v) n++; return n; } }
}

public static class FvpMultiViewLifter
{
    public struct Options
    {
        public double minConf;                  // 2D joints below this are ignored
        public double groupGateMetres;          // two people of different views are one person when the median ray distance is below this
        public double inlierMetres;             // a ray farther than this from the triangulated joint is an outlier
        public int minViews;                    // a 3D person needs this many views
        public int minJoints;                   // shared joints needed to compare two people, and valid joints to keep a 3D person

        public static Options Default => new Options { minConf = 0.3, groupGateMetres = 0.30, inlierMetres = 0.08, minViews = 2, minJoints = 6 };
    }

    // perView[v]: the people of view v. cams[v]: its camera model. Returns the 3D people (each from >= minViews views).
    public static List<MvPerson3D> Lift(IList<MvPerson2D>[] perView, FvpCameraModel[] cams, Options o)
    {
        int views = Math.Min(perView.Length, cams.Length);
        // nodes: every 2D person, with the rays of its confident joints
        var nodes = new List<Node>();
        for (int v = 0; v < views; v++)
            foreach (MvPerson2D p in perView[v]) nodes.Add(MakeNode(p, cams[v], o));

        // pairwise costs between people of different views
        int n = nodes.Count;
        var cost = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) cost[i, j] = double.PositiveInfinity;
        var edges = new List<(double c, int a, int b)>();
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (nodes[i].person.view == nodes[j].person.view) continue;
                double c = PairCost(nodes[i], nodes[j], o);
                cost[i, j] = cost[j, i] = c;
                if (c <= o.groupGateMetres) edges.Add((c, i, j));
            }
        edges.Sort((p, q) => p.c.CompareTo(q.c));

        // group, cheapest first; a group keeps one person per view and every member must fit every other one
        var groupOf = new int[n];
        var groups = new List<List<int>>();
        for (int i = 0; i < n; i++) { groupOf[i] = i; groups.Add(new List<int> { i }); }
        foreach (var e in edges)
        {
            int ga = groupOf[e.a], gb = groupOf[e.b];
            if (ga == gb) continue;
            List<int> A = groups[ga], B = groups[gb];
            if (ViewsOverlap(nodes, A, B)) continue;
            bool clique = true;
            foreach (int i in A) { foreach (int j in B) if (cost[i, j] > o.groupGateMetres) { clique = false; break; } if (!clique) break; }
            if (!clique) continue;
            foreach (int j in B) { groupOf[j] = ga; A.Add(j); }
            B.Clear();
        }

        var result = new List<MvPerson3D>();
        foreach (List<int> g in groups)
        {
            if (g.Count < o.minViews) continue;
            var members = new List<Node>();
            foreach (int i in g) members.Add(nodes[i]);
            MvPerson3D p3 = Triangulate(members, cams, o);
            if (p3 != null && p3.ValidCount >= o.minJoints) result.Add(p3);
        }
        return result;
    }

    // ---------------------------------------------------------------- internals

    sealed class Node
    {
        public MvPerson2D person;
        public FvpCameraModel cam;
        public bool[] has;                      // joint k has a ray
        public double[][] dir;                  // unit direction per joint
    }

    static Node MakeNode(MvPerson2D p, FvpCameraModel cam, Options o)
    {
        int k = p.Count;
        var nd = new Node { person = p, cam = cam, has = new bool[k], dir = new double[k][] };
        var origin = new double[3];
        for (int j = 0; j < k; j++)
        {
            if (p.conf[j] < o.minConf) continue;
            var d = new double[3];
            if (cam.TryRay(p.x[j], p.y[j], origin, d)) { nd.has[j] = true; nd.dir[j] = d; }
        }
        return nd;
    }

    static bool ViewsOverlap(List<Node> nodes, List<int> A, List<int> B)
    {
        foreach (int i in A) foreach (int j in B) if (nodes[i].person.view == nodes[j].person.view) return true;
        return false;
    }

    // median over the shared joints of the distance between the two joint rays; infinite when they share too few
    static double PairCost(Node a, Node b, Options o)
    {
        int k = Math.Min(a.person.Count, b.person.Count);
        var d = new List<double>(k);
        for (int j = 0; j < k; j++)
        {
            if (!a.has[j] || !b.has[j]) continue;
            double dist = RayDistance(a.cam.centre, a.dir[j], b.cam.centre, b.dir[j]);
            if (!double.IsNaN(dist)) d.Add(dist);
        }
        if (d.Count < o.minJoints) return double.PositiveInfinity;
        d.Sort();
        return d.Count % 2 == 1 ? d[d.Count / 2] : 0.5 * (d[d.Count / 2 - 1] + d[d.Count / 2]);
    }

    // Distance between two lines o + t d, counted as long as their nearest points are in front of both cameras (t > 0); NaN otherwise.
    static double RayDistance(double[] o1, double[] d1, double[] o2, double[] d2)
    {
        double wx = o1[0] - o2[0], wy = o1[1] - o2[1], wz = o1[2] - o2[2];
        double a = Dot(d1, d1), b = Dot(d1, d2), c = Dot(d2, d2);
        double d = d1[0] * wx + d1[1] * wy + d1[2] * wz, e = d2[0] * wx + d2[1] * wy + d2[2] * wz;
        double den = a * c - b * b;
        if (den < 1e-9) return double.NaN;                       // parallel: no usable meeting point
        double t1 = (b * e - c * d) / den, t2 = (a * e - b * d) / den;
        if (t1 <= 0 || t2 <= 0) return double.NaN;               // they meet behind a camera
        double px = (o1[0] + t1 * d1[0]) - (o2[0] + t2 * d2[0]);
        double py = (o1[1] + t1 * d1[1]) - (o2[1] + t2 * d2[1]);
        double pz = (o1[2] + t1 * d1[2]) - (o2[2] + t2 * d2[2]);
        return Math.Sqrt(px * px + py * py + pz * pz);
    }

    static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    static MvPerson3D Triangulate(List<Node> members, FvpCameraModel[] cams, Options o)
    {
        int k = members[0].person.Count;
        var p3 = new MvPerson3D { x = new double[k], y = new double[k], z = new double[k], valid = new bool[k], conf = new double[k], raysUsed = new int[k] };
        foreach (Node m in members) p3.members.Add(m.person);

        var used = new List<int>();
        var X = new double[3];
        for (int j = 0; j < k; j++)
        {
            used.Clear();
            for (int m = 0; m < members.Count; m++) if (members[m].has[j]) used.Add(m);
            if (used.Count < 2) continue;

            bool ok = false;
            while (used.Count >= 2)
            {
                if (!Solve(members, used, j, X)) break;
                // residual of every ray, and whether the point lies in front of the camera
                int worst = -1; double worstDist = 0; bool behind = false;
                for (int u = 0; u < used.Count; u++)
                {
                    Node nd = members[used[u]];
                    double[] d = nd.dir[j];
                    double rx = X[0] - nd.cam.centre[0], ry = X[1] - nd.cam.centre[1], rz = X[2] - nd.cam.centre[2];
                    double t = rx * d[0] + ry * d[1] + rz * d[2];
                    if (t <= 0) behind = true;
                    double px = rx - t * d[0], py = ry - t * d[1], pz = rz - t * d[2];
                    double dist = Math.Sqrt(px * px + py * py + pz * pz);
                    if (dist > worstDist) { worstDist = dist; worst = u; }
                }
                if (worstDist <= o.inlierMetres && !behind) { ok = true; break; }
                if (used.Count <= 2) break;                       // two rays that do not meet: nothing to keep
                used.RemoveAt(worst);                             // drop the ray that disagrees most and try again
            }
            if (!ok) continue;

            p3.valid[j] = true;
            p3.x[j] = X[0]; p3.y[j] = X[1]; p3.z[j] = X[2];
            p3.raysUsed[j] = used.Count;
            double c = 0; foreach (int m in used) c += members[m].person.conf[j];
            p3.conf[j] = c / used.Count;
        }

        // how well the 3D joints reproject onto the 2D detections they came from
        double sum = 0; int n = 0;
        foreach (Node nd in members)
            for (int j = 0; j < k; j++)
            {
                if (!p3.valid[j] || !nd.has[j]) continue;
                if (!nd.cam.Project(p3.x[j], p3.y[j], p3.z[j], out double u, out double v)) continue;
                double du = u - nd.person.x[j], dv = v - nd.person.y[j];
                sum += Math.Sqrt(du * du + dv * dv); n++;
            }
        p3.reprojectionPx = n > 0 ? sum / n : -1;
        return p3;
    }

    // the point nearest to the chosen rays, least squares weighted by confidence: (sum w (I - d d^T)) X = sum w (I - d d^T) o
    static bool Solve(List<Node> members, List<int> used, int j, double[] X)
    {
        double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, b0 = 0, b1 = 0, b2 = 0;
        foreach (int m in used)
        {
            Node nd = members[m];
            double w = Math.Max(1e-3, nd.person.conf[j]);
            double[] d = nd.dir[j]; double[] o = nd.cam.centre;
            double m00 = 1 - d[0] * d[0], m01 = -d[0] * d[1], m02 = -d[0] * d[2], m11 = 1 - d[1] * d[1], m12 = -d[1] * d[2], m22 = 1 - d[2] * d[2];
            a00 += w * m00; a01 += w * m01; a02 += w * m02; a11 += w * m11; a12 += w * m12; a22 += w * m22;
            b0 += w * (m00 * o[0] + m01 * o[1] + m02 * o[2]);
            b1 += w * (m01 * o[0] + m11 * o[1] + m12 * o[2]);
            b2 += w * (m02 * o[0] + m12 * o[1] + m22 * o[2]);
        }
        // solve the symmetric 3x3 system by its adjugate
        double c00 = a11 * a22 - a12 * a12, c01 = a02 * a12 - a01 * a22, c02 = a01 * a12 - a02 * a11;
        double det = a00 * c00 + a01 * c01 + a02 * c02;
        if (Math.Abs(det) < 1e-12) return false;                  // (nearly) parallel rays
        double c11 = a00 * a22 - a02 * a02, c12 = a01 * a02 - a00 * a12, c22 = a00 * a11 - a01 * a01;
        X[0] = (c00 * b0 + c01 * b1 + c02 * b2) / det;
        X[1] = (c01 * b0 + c11 * b1 + c12 * b2) / det;
        X[2] = (c02 * b0 + c12 * b1 + c22 * b2) / det;
        return true;
    }
}
