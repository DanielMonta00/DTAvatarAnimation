using System;

// OpenCV lens model (k1, k2, p1, p2, k3) on normalized image coordinates
// x = (u - cx) / fx, y = (v - cy) / fy, as produced by PTZCalibration.
//
// The radial polynomial r * (1 + k1 r^2 + k2 r^4 + k3 r^6) is only monotonic up to a
// "fold" radius; past it the model is a fit artefact and cannot be inverted. Everything
// here works inside UsableRadius (a fraction of the fold) and treats the rest as invalid.
public static class LensDistortion
{
    // Fraction of the fold radius still trusted. Past this the mapping is so steep that
    // a fraction of a distorted pixel spans many ideal pixels (the extreme image corners).
    const double UsableFraction = 0.95;

    // Ideal (undistorted) normalized point -> where the lens puts it.
    public static void Distort(double x, double y, double[] d, out double xd, out double yd)
    {
        double k1 = d[0], k2 = d[1], p1 = d.Length > 2 ? d[2] : 0, p2 = d.Length > 3 ? d[3] : 0, k3 = d.Length > 4 ? d[4] : 0;
        double r2 = x * x + y * y;
        double radial = 1.0 + r2 * (k1 + r2 * (k2 + r2 * k3));
        xd = x * radial + 2.0 * p1 * x * y + p2 * (r2 + 2.0 * x * x);
        yd = y * radial + p1 * (r2 + 2.0 * y * y) + 2.0 * p2 * x * y;
    }

    public static bool HasDistortion(double[] d)
    {
        if (d == null) return false;
        foreach (double v in d) if (Math.Abs(v) > 1e-12) return true;
        return false;
    }

    static double K(double[] d, int i) => i < d.Length ? d[i] : 0.0;

    // g(r): distorted radius of an ideal point at radius r (radial terms only).
    static double Radial(double r, double[] d)
    {
        double r2 = r * r;
        return r * (1.0 + r2 * (K(d, 0) + r2 * (K(d, 1) + r2 * K(d, 4))));
    }

    // First radius where g'(r) = 1 + 3 k1 r^2 + 5 k2 r^4 + 7 k3 r^6 stops being positive.
    static double FoldRadius(double[] d)
    {
        const double rMax = 6.0, step = 1e-3;
        double k1 = K(d, 0), k2 = K(d, 1), k3 = K(d, 4);
        double Gp(double r) { double r2 = r * r; return 1.0 + r2 * (3.0 * k1 + r2 * (5.0 * k2 + r2 * 7.0 * k3)); }
        for (double r = step; r <= rMax; r += step)
        {
            if (Gp(r) > 0) continue;
            double lo = r - step, hi = r;
            for (int i = 0; i < 50; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Gp(mid) > 0) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }
        return rMax;
    }

    // Largest ideal radius (normalized) that is still mapped and inverted.
    public static double UsableRadius(double[] d) => UsableFraction * FoldRadius(d);

    // Distorted normalized point -> ideal one. Returns false (and the clamped point on the
    // usable circle) when the target lies beyond what the model reaches.
    public static bool Undistort(double xd, double yd, double[] d, double usableRadius, out double x, out double y)
    {
        double rd = Math.Sqrt(xd * xd + yd * yd);
        if (rd < 1e-12) { x = 0; y = 0; return true; }

        // Radial-only inverse by bisection on the monotonic branch [0, usableRadius].
        bool inside = rd < Radial(usableRadius, d);
        double ru;
        if (!inside) ru = usableRadius;
        else
        {
            double lo = 0, hi = usableRadius;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Radial(mid, d) < rd) lo = mid; else hi = mid;
            }
            ru = 0.5 * (lo + hi);
        }
        x = xd / rd * ru;
        y = yd / rd * ru;
        if (!inside) return false;

        // The tangential terms (p1, p2) are tiny: a few damped Newton steps on the full model.
        for (int it = 0; it < 10; it++)
        {
            Distort(x, y, d, out double ex, out double ey);
            double rx = xd - ex, ry = yd - ey;
            double res = rx * rx + ry * ry;
            if (res < 1e-20) break;

            const double h = 1e-6;
            Distort(x + h, y, d, out double axp, out double ayp);
            Distort(x - h, y, d, out double axm, out double aym);
            Distort(x, y + h, d, out double bxp, out double byp);
            Distort(x, y - h, d, out double bxm, out double bym);
            double j00 = (axp - axm) / (2 * h), j10 = (ayp - aym) / (2 * h);
            double j01 = (bxp - bxm) / (2 * h), j11 = (byp - bym) / (2 * h);
            double det = j00 * j11 - j01 * j10;
            if (Math.Abs(det) < 1e-12) break;
            double sx = (j11 * rx - j01 * ry) / det;
            double sy = (-j10 * rx + j00 * ry) / det;

            bool improved = false;
            double s = 1.0;
            for (int k = 0; k < 5; k++, s *= 0.5)
            {
                double nx = x + s * sx, ny = y + s * sy;
                if (nx * nx + ny * ny > usableRadius * usableRadius) continue;
                Distort(nx, ny, d, out double fx, out double fy);
                double nres = (xd - fx) * (xd - fx) + (yd - fy) * (yd - fy);
                if (nres < res) { x = nx; y = ny; improved = true; break; }
            }
            if (!improved) break;
        }
        return true;
    }
}
