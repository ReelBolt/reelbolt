using System;
using System.Collections.Generic;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The geometry behind <see cref="ChromaPlatePoseTracker"/>: a pinhole camera over the detection
/// grid, a rigid plate (a rectangle, optionally bent into a cylinder section), and the 6-DoF pose
/// that places one in front of the other. Pure and allocation-light; nothing here reads pixels.
///
/// <para>
/// Grid coordinates have pixel centres at integer positions, as everywhere in the chroma tracker.
/// A pose is six numbers: a rotation vector (axis times angle) and a translation, taking plate
/// coordinates to camera coordinates. The plate's own unit is its HEIGHT: a flat plate of aspect
/// <c>a</c> spans <c>[-a/2, a/2] x [-1/2, 1/2]</c> in the plane <c>z = 0</c>.
/// </para>
/// </summary>
internal static class PlatePoseMath
{
    /// <summary>A pinhole camera: principal point at the grid's centre, one focal length (square pixels).</summary>
    internal readonly record struct Camera(double Cx, double Cy, double F)
    {
        public static Camera ForGrid(int width, int height, double focal) => new(width / 2.0 - 0.5, height / 2.0 - 0.5, focal);
    }

    /// <summary>
    /// The rigid plate. <see cref="Aspect"/> is its width over its height, measured ALONG the surface
    /// (arc length); <see cref="Kappa"/> is the arc over the radius of a cylinder whose axis runs
    /// along the plate's height, positive when the side edges come toward the camera, and 0 for a
    /// flat plate. Edges are 3D polylines in the order top, right, bottom, left, each running
    /// clockwise as seen by the camera; corners are TL, TR, BL, BR.
    /// </summary>
    internal sealed class Plate
    {
        public double Aspect { get; }
        public double Kappa { get; }
        public bool IsFlat => Math.Abs(Kappa) < 1e-6;
        public (double X, double Y, double Z)[][] Edges { get; }
        public (double X, double Y, double Z)[] Corners { get; }

        /// <summary>Points per bent edge. Thirteen keeps the chord error of a one-radian arc under a twentieth of a grid pixel at any plate size the grid can hold.</summary>
        internal const int BentEdgePoints = 13;

        public Plate(double aspect, double kappa = 0)
        {
            Aspect = aspect;
            Kappa = kappa;
            int m = Math.Abs(kappa) < 1e-6 ? 2 : BentEdgePoints;
            var top = new (double, double, double)[m];
            var bottom = new (double, double, double)[m];
            for (int i = 0; i < m; i++)
            {
                double u = i / (double)(m - 1);
                top[i] = Point(u, 0);
                bottom[i] = Point(1 - u, 1);
            }

            Edges = [top, [Point(1, 0), Point(1, 1)], bottom, [Point(0, 1), Point(0, 0)]];
            Corners = [Point(0, 0), Point(1, 0), Point(0, 1), Point(1, 1)];
        }

        /// <summary>The surface point at plate coordinates (u, v) in 0..1, u along the width.</summary>
        public (double X, double Y, double Z) Point(double u, double v)
        {
            double s = (u - 0.5) * Aspect;
            if (IsFlat)
                return (s, v - 0.5, 0);
            double r = Aspect / Kappa;
            return (r * Math.Sin(s / r), v - 0.5, -r * (1 - Math.Cos(s / r)));
        }
    }

    /// <summary>Rotation matrix (row-major 3x3) of a rotation vector.</summary>
    internal static void Rodrigues(ReadOnlySpan<double> rv, Span<double> r)
    {
        double th = Math.Sqrt(rv[0] * rv[0] + rv[1] * rv[1] + rv[2] * rv[2]);
        if (th < 1e-12)
        {
            r[0] = 1; r[1] = 0; r[2] = 0; r[3] = 0; r[4] = 1; r[5] = 0; r[6] = 0; r[7] = 0; r[8] = 1;
            return;
        }

        double kx = rv[0] / th, ky = rv[1] / th, kz = rv[2] / th;
        double c = Math.Cos(th), s = Math.Sin(th), v = 1 - c;
        r[0] = c + kx * kx * v; r[1] = kx * ky * v - kz * s; r[2] = kx * kz * v + ky * s;
        r[3] = ky * kx * v + kz * s; r[4] = c + ky * ky * v; r[5] = ky * kz * v - kx * s;
        r[6] = kz * kx * v - ky * s; r[7] = kz * ky * v + kx * s; r[8] = c + kz * kz * v;
    }

    /// <summary>Rotation vector of a rotation matrix (row-major 3x3).</summary>
    internal static (double X, double Y, double Z) RotationVector(ReadOnlySpan<double> r)
    {
        double c = Math.Clamp((r[0] + r[4] + r[8] - 1) / 2, -1, 1);
        double th = Math.Acos(c);
        if (th < 1e-9)
            return (0, 0, 0);
        double s = 2 * Math.Sin(th);
        if (Math.Abs(s) < 1e-9)
        {
            // A half turn: the axis is the eigenvector of R for eigenvalue 1, read off the diagonal.
            double ax = Math.Sqrt(Math.Max(0, (r[0] + 1) / 2)), ay = Math.Sqrt(Math.Max(0, (r[4] + 1) / 2)), az = Math.Sqrt(Math.Max(0, (r[8] + 1) / 2));
            if (r[1] < 0) ay = -ay;
            if (r[2] < 0) az = -az;
            return (ax * th, ay * th, az * th);
        }

        return ((r[7] - r[5]) / s * th, (r[2] - r[6]) / s * th, (r[3] - r[1]) / s * th);
    }

    /// <summary>Projects plate points through a pose. <paramref name="depths"/>, when given, receives each point's camera depth.</summary>
    internal static void Project(
        in Camera cam, ReadOnlySpan<double> pose, ReadOnlySpan<(double X, double Y, double Z)> points, Span<(double X, double Y)> image,
        Span<double> depths = default)
    {
        Span<double> r = stackalloc double[9];
        Rodrigues(pose[..3], r);
        for (int i = 0; i < points.Length; i++)
        {
            (double x, double y, double z) = points[i];
            double cx = r[0] * x + r[1] * y + r[2] * z + pose[3];
            double cy = r[3] * x + r[4] * y + r[5] * z + pose[4];
            double cz = r[6] * x + r[7] * y + r[8] * z + pose[5];
            if (!depths.IsEmpty)
                depths[i] = cz;
            double d = Math.Max(cz, 1e-6);
            image[i] = (cam.F * cx / d + cam.Cx, cam.F * cy / d + cam.Cy);
        }
    }

    /// <summary>The plate's four edges as image polylines, top, right, bottom, left.</summary>
    internal static (double X, double Y)[][] ProjectEdges(in Camera cam, ReadOnlySpan<double> pose, Plate plate)
    {
        var polys = new (double X, double Y)[4][];
        for (int k = 0; k < 4; k++)
        {
            polys[k] = new (double X, double Y)[plate.Edges[k].Length];
            Project(cam, pose, plate.Edges[k], polys[k]);
        }

        return polys;
    }

    /// <summary>The plate's corners in the image, TL, TR, BL, BR.</summary>
    internal static (double X, double Y)[] ProjectCorners(in Camera cam, ReadOnlySpan<double> pose, Plate plate)
    {
        var c = new (double X, double Y)[4];
        Project(cam, pose, plate.Corners, c);
        return c;
    }

    /// <summary>
    /// The homography taking the unit square centred on the origin (corners at ±1/2, in the order
    /// TL, TR, BL, BR) to <paramref name="quad"/>, row-major with <c>h[8] = 1</c>; <c>null</c> for a
    /// degenerate quad.
    /// </summary>
    internal static double[]? CentredSquareHomography(ReadOnlySpan<(double X, double Y)> quad)
    {
        ReadOnlySpan<(double X, double Y)> src = [(-0.5, -0.5), (0.5, -0.5), (-0.5, 0.5), (0.5, 0.5)];
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            (double x, double y) = src[i];
            (double u, double v) = quad[i];
            int r0 = 2 * i, r1 = r0 + 1;
            a[r0, 0] = x; a[r0, 1] = y; a[r0, 2] = 1; a[r0, 6] = -u * x; a[r0, 7] = -u * y; a[r0, 8] = u;
            a[r1, 3] = x; a[r1, 4] = y; a[r1, 5] = 1; a[r1, 6] = -v * x; a[r1, 7] = -v * y; a[r1, 8] = v;
        }

        var h = new double[9];
        if (!SolveInPlace(a, 8, h))
            return null;
        h[8] = 1;
        return h;
    }

    /// <summary>
    /// What a quad says about the plate behind it, for a camera of known focal length: the plate's
    /// aspect, the cosine between its two axes as recovered (0 for a true rectangle seen by this
    /// camera), and the pose that puts a plate of that aspect on the quad.
    /// </summary>
    internal static bool Decompose(in Camera cam, ReadOnlySpan<(double X, double Y)> quad, out double aspect, out double axesCosine, Span<double> pose)
    {
        aspect = 0;
        axesCosine = 0;
        double[]? h = CentredSquareHomography(quad);
        if (h is null)
            return false;

        // M = K^-1 H, column by column.
        Span<double> m = stackalloc double[9];
        for (int c = 0; c < 3; c++)
        {
            m[c] = (h[c] - cam.Cx * h[6 + c]) / cam.F;
            m[3 + c] = (h[3 + c] - cam.Cy * h[6 + c]) / cam.F;
            m[6 + c] = h[6 + c];
        }

        if (m[8] < 0)
        {
            for (int i = 0; i < 9; i++)
                m[i] = -m[i];
        }

        double n1 = Math.Sqrt(m[0] * m[0] + m[3] * m[3] + m[6] * m[6]);
        double n2 = Math.Sqrt(m[1] * m[1] + m[4] * m[4] + m[7] * m[7]);
        if (!(n1 > 1e-12) || !(n2 > 1e-12))
            return false;

        double ax = m[0] / n1, ay = m[3] / n1, az = m[6] / n1;
        double bx = m[1] / n2, by = m[4] / n2, bz = m[7] / n2;
        aspect = n1 / n2;
        axesCosine = ax * bx + ay * by + az * bz;

        // The nearest orthonormal pair to (a, b): their bisectors are orthogonal by construction.
        double sx = ax + bx, sy = ay + by, sz = az + bz;
        double dx = ax - bx, dy = ay - by, dz = az - bz;
        double sn = Math.Sqrt(sx * sx + sy * sy + sz * sz), dn = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!(sn > 1e-9) || !(dn > 1e-9))
            return false;
        sx /= sn; sy /= sn; sz /= sn;
        dx /= dn; dy /= dn; dz /= dn;
        const double h2 = 0.70710678118654752;
        double r1x = (sx + dx) * h2, r1y = (sy + dy) * h2, r1z = (sz + dz) * h2;
        double r2x = (sx - dx) * h2, r2y = (sy - dy) * h2, r2z = (sz - dz) * h2;
        double r3x = r1y * r2z - r1z * r2y, r3y = r1z * r2x - r1x * r2z, r3z = r1x * r2y - r1y * r2x;

        ReadOnlySpan<double> rot = [r1x, r2x, r3x, r1y, r2y, r3y, r1z, r2z, r3z];
        (double X, double Y, double Z) rv = RotationVector(rot);
        pose[0] = rv.X; pose[1] = rv.Y; pose[2] = rv.Z;
        pose[3] = m[2] / n2; pose[4] = m[5] / n2; pose[5] = m[8] / n2;
        return true;
    }

    /// <summary>
    /// <paramref name="pose"/> moved rigidly in front of the camera without changing its shape on
    /// screen: a slide parallel to the image plane (<c>q[0]</c>, <c>q[1]</c>, as fractions of the
    /// depth), a turn about the optical axis (<c>q[2]</c>), and — when <c>q</c> has a fourth entry —
    /// a move along the axis (scale).
    /// </summary>
    internal static void Restricted(ReadOnlySpan<double> pose, ReadOnlySpan<double> q, Span<double> result)
    {
        Span<double> r = stackalloc double[9];
        Rodrigues(pose[..3], r);
        double c = Math.Cos(q[2]), s = Math.Sin(q[2]);
        Span<double> rr = stackalloc double[9];
        for (int j = 0; j < 3; j++)
        {
            rr[j] = c * r[j] - s * r[3 + j];
            rr[3 + j] = s * r[j] + c * r[3 + j];
            rr[6 + j] = r[6 + j];
        }

        (double X, double Y, double Z) rv = RotationVector(rr);
        double tx = c * pose[3] - s * pose[4], ty = s * pose[3] + c * pose[4], tz = pose[5];
        result[0] = rv.X; result[1] = rv.Y; result[2] = rv.Z;
        result[3] = tx + q[0] * tz;
        result[4] = ty + q[1] * tz;
        result[5] = tz + (q.Length > 3 ? q[3] * tz : 0);
    }

    /// <summary>
    /// Solves the <paramref name="n"/> x <paramref name="n"/> system held in the left of the
    /// augmented matrix <paramref name="a"/> (n rows, n + 1 columns) by Gaussian elimination with
    /// partial pivoting. <c>false</c> when singular.
    /// </summary>
    internal static bool SolveInPlace(double[,] a, int n, Span<double> x)
    {
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            double best = Math.Abs(a[col, col]);
            for (int row = col + 1; row < n; row++)
            {
                double v = Math.Abs(a[row, col]);
                if (v > best)
                {
                    best = v;
                    pivot = row;
                }
            }

            if (!(best > 1e-14))
                return false;
            if (pivot != col)
            {
                for (int k = col; k <= n; k++)
                    (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
            }

            for (int row = col + 1; row < n; row++)
            {
                double f = a[row, col] / a[col, col];
                if (f == 0)
                    continue;
                for (int k = col; k <= n; k++)
                    a[row, k] -= f * a[col, k];
            }
        }

        for (int row = n - 1; row >= 0; row--)
        {
            double sum = a[row, n];
            for (int k = row + 1; k < n; k++)
                sum -= a[row, k] * x[k];
            x[row] = sum / a[row, row];
        }

        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(x[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Signed distance of a point from a polyline, positive on the side away from
    /// <paramref name="centre"/>. The first and last segments extend past the polyline's ends, so a
    /// point beyond a corner is measured against the edge's own line rather than against the corner.
    /// </summary>
    internal static double SignedDistance((double X, double Y) p, ReadOnlySpan<(double X, double Y)> poly, (double X, double Y) centre)
    {
        double bestAbs = double.MaxValue, best = 0;
        int last = poly.Length - 2;
        for (int k = 0; k <= last; k++)
        {
            (double ax, double ay) = poly[k];
            double dx = poly[k + 1].X - ax, dy = poly[k + 1].Y - ay;
            double l2 = Math.Max(dx * dx + dy * dy, 1e-12);
            double t = ((p.X - ax) * dx + (p.Y - ay) * dy) / l2;
            if (last > 0)
            {
                if (k == 0)
                    t = Math.Min(t, 1);
                else if (k == last)
                    t = Math.Max(t, 0);
                else
                    t = Math.Clamp(t, 0, 1);
            }

            double fx = p.X - (ax + t * dx), fy = p.Y - (ay + t * dy);
            double dist = Math.Sqrt(fx * fx + fy * fy);
            if (dist < bestAbs)
            {
                double len = Math.Sqrt(l2);
                double nx = dy / len, ny = -dx / len;
                if ((centre.X - ax) * nx + (centre.Y - ay) * ny > 0)
                {
                    nx = -nx;
                    ny = -ny;
                }

                bestAbs = dist;
                best = fx * nx + fy * ny;
            }
        }

        return best;
    }

    /// <summary>
    /// Tukey's loss with one scale inward (negative residuals: an occluder's boundary) and another
    /// outward. An outlier costs a CONSTANT, which is the point: a pose can never lower its cost by
    /// walking away from evidence it used to explain.
    /// </summary>
    internal static double Rho(double r, double inward, double outward)
    {
        double c = r < 0 ? inward : outward;
        double x = Math.Min(Math.Abs(r) / c, 1);
        double k = 1 - x * x;
        return c * c / 6.0 * (1 - k * k * k);
    }

    /// <summary>The weight that goes with <see cref="Rho"/> in the reweighted normal equations.</summary>
    internal static double Weight(double r, double inward, double outward)
    {
        double c = r < 0 ? inward : outward;
        double x = Math.Min(Math.Abs(r) / c, 1);
        double k = 1 - x * x;
        return k * k;
    }
}
