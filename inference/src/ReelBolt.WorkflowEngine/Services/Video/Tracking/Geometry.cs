namespace ReelBolt.WorkflowEngine.Services.Video.Tracking;

/// <summary>How the tracked region may deform between frames.</summary>
public enum TrackMotionModel
{
    /// <summary>Full perspective (8 DOF) — a flat surface seen from a moving camera: plates, signs, screens.</summary>
    Homography,
    /// <summary>Rotation, scale, shear and translation (6 DOF) — robust on small or weakly textured targets where perspective is negligible.</summary>
    Affine,
    /// <summary>Rotation, uniform scale and translation (4 DOF) — the most stable for tiny targets.</summary>
    Similarity,
    /// <summary>Translation only (2 DOF).</summary>
    Translation
}

/// <summary>A 3x3 projective transform, row-major, <c>h[8]</c> normalized to 1 where possible.</summary>
public readonly record struct Transform2D(
    double H0, double H1, double H2,
    double H3, double H4, double H5,
    double H6, double H7, double H8)
{
    public static Transform2D Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public (double X, double Y) Apply(double x, double y)
    {
        double w = H6 * x + H7 * y + H8;
        if (Math.Abs(w) < 1e-12)
            w = 1e-12;
        return ((H0 * x + H1 * y + H2) / w, (H3 * x + H4 * y + H5) / w);
    }

    /// <summary><c>this</c> applied after <paramref name="first"/>.</summary>
    public Transform2D After(Transform2D first) => new(
        H0 * first.H0 + H1 * first.H3 + H2 * first.H6,
        H0 * first.H1 + H1 * first.H4 + H2 * first.H7,
        H0 * first.H2 + H1 * first.H5 + H2 * first.H8,
        H3 * first.H0 + H4 * first.H3 + H5 * first.H6,
        H3 * first.H1 + H4 * first.H4 + H5 * first.H7,
        H3 * first.H2 + H4 * first.H5 + H5 * first.H8,
        H6 * first.H0 + H7 * first.H3 + H8 * first.H6,
        H6 * first.H1 + H7 * first.H4 + H8 * first.H7,
        H6 * first.H2 + H7 * first.H5 + H8 * first.H8);
}

/// <summary>
/// Motion-model estimation and small geometric helpers for the planar tracker. Every estimator is
/// least squares over a normalized point set (Hartley normalization for the homography), and
/// RANSAC uses a fixed-seed generator so a tracking run is bit-for-bit reproducible.
/// </summary>
public static class Geometry
{
    public static int MinimalSampleSize(TrackMotionModel model) => model switch
    {
        TrackMotionModel.Homography => 4,
        TrackMotionModel.Affine => 3,
        TrackMotionModel.Similarity => 2,
        _ => 1
    };

    /// <summary>
    /// RANSAC fit of <paramref name="model"/> mapping <paramref name="src"/> onto
    /// <paramref name="dst"/>; returns the refit-on-inliers transform and the inlier mask, or
    /// <c>null</c> when no hypothesis gathers at least <paramref name="minInliers"/>.
    /// </summary>
    public static (Transform2D Transform, bool[] Inliers, int InlierCount)? EstimateRansac(
        IReadOnlyList<(double X, double Y)> src,
        IReadOnlyList<(double X, double Y)> dst,
        TrackMotionModel model,
        double threshold,
        int minInliers,
        int maxIterations = 200,
        int seed = 12345)
    {
        int n = src.Count;
        int k = MinimalSampleSize(model);
        if (n < Math.Max(k, minInliers))
            return null;

        var rng = new Random(seed);
        double t2 = threshold * threshold;
        bool[] bestMask = new bool[n];
        int bestCount = -1;
        var sampleSrc = new (double, double)[k];
        var sampleDst = new (double, double)[k];
        var idx = new int[k];
        int iterations = maxIterations;

        for (int it = 0; it < iterations; it++)
        {
            // Draw k distinct indices.
            for (int s = 0; s < k; s++)
            {
                int pick;
                bool dup;
                do
                {
                    pick = rng.Next(n);
                    dup = false;
                    for (int q = 0; q < s; q++)
                        if (idx[q] == pick) { dup = true; break; }
                } while (dup);
                idx[s] = pick;
                sampleSrc[s] = src[pick];
                sampleDst[s] = dst[pick];
            }

            Transform2D? hypo = Fit(sampleSrc, sampleDst, model);
            if (hypo is null)
                continue;

            int count = 0;
            var mask = new bool[n];
            for (int i = 0; i < n; i++)
            {
                (double mx, double my) = hypo.Value.Apply(src[i].X, src[i].Y);
                double ex = mx - dst[i].X, ey = my - dst[i].Y;
                if (ex * ex + ey * ey <= t2)
                {
                    mask[i] = true;
                    count++;
                }
            }

            if (count > bestCount)
            {
                bestCount = count;
                bestMask = mask;

                // Adaptive iteration count for 99% confidence.
                double ratio = count / (double)n;
                double pAllIn = Math.Pow(ratio, k);
                if (pAllIn > 1e-9 && pAllIn < 1)
                {
                    int needed = (int)Math.Ceiling(Math.Log(0.01) / Math.Log(1 - pAllIn));
                    iterations = Math.Min(maxIterations, Math.Max(it + 1, needed));
                }
                else if (pAllIn >= 1)
                {
                    break;
                }
            }
        }

        if (bestCount < Math.Max(k, minInliers))
            return null;

        var inSrc = new List<(double, double)>(bestCount);
        var inDst = new List<(double, double)>(bestCount);
        for (int i = 0; i < n; i++)
        {
            if (bestMask[i])
            {
                inSrc.Add(src[i]);
                inDst.Add(dst[i]);
            }
        }

        Transform2D? refit = Fit(inSrc, inDst, model);
        if (refit is null)
            return null;

        // Re-score with the refit so the mask matches the returned transform.
        int finalCount = 0;
        var finalMask = new bool[n];
        for (int i = 0; i < n; i++)
        {
            (double mx, double my) = refit.Value.Apply(src[i].X, src[i].Y);
            double ex = mx - dst[i].X, ey = my - dst[i].Y;
            if (ex * ex + ey * ey <= t2)
            {
                finalMask[i] = true;
                finalCount++;
            }
        }

        return finalCount < Math.Max(k, minInliers) ? null : (refit.Value, finalMask, finalCount);
    }

    /// <summary>Least-squares fit of <paramref name="model"/>; <c>null</c> when degenerate.</summary>
    public static Transform2D? Fit(
        IReadOnlyList<(double X, double Y)> src, IReadOnlyList<(double X, double Y)> dst, TrackMotionModel model)
    {
        int n = src.Count;
        if (n < MinimalSampleSize(model))
            return null;

        // Normalize both sets: centroid to origin, mean distance sqrt(2).
        (double scx, double scy, double ss) = NormalizationOf(src);
        (double dcx, double dcy, double ds) = NormalizationOf(dst);
        if (ss <= 0 || ds <= 0)
        {
            if (model != TrackMotionModel.Translation)
                return null;
        }

        Transform2D? normalized = model switch
        {
            TrackMotionModel.Homography => FitHomography(src, dst, scx, scy, ss, dcx, dcy, ds),
            TrackMotionModel.Affine => FitAffine(src, dst, scx, scy, ss, dcx, dcy, ds),
            TrackMotionModel.Similarity => FitSimilarity(src, dst, scx, scy, ss, dcx, dcy, ds),
            _ => FitTranslation(src, dst)
        };

        if (normalized is null || model == TrackMotionModel.Translation)
            return normalized;

        // Denormalize: H = Td^-1 * Hn * Ts.
        var ts = new Transform2D(ss, 0, -ss * scx, 0, ss, -ss * scy, 0, 0, 1);
        var tdInv = new Transform2D(1 / ds, 0, dcx, 0, 1 / ds, dcy, 0, 0, 1);
        Transform2D h = tdInv.After(normalized.Value.After(ts));
        if (Math.Abs(h.H8) > 1e-12)
            h = new Transform2D(h.H0 / h.H8, h.H1 / h.H8, h.H2 / h.H8, h.H3 / h.H8, h.H4 / h.H8, h.H5 / h.H8, h.H6 / h.H8, h.H7 / h.H8, 1);
        return h;
    }

    private static (double Cx, double Cy, double Scale) NormalizationOf(IReadOnlyList<(double X, double Y)> pts)
    {
        double cx = 0, cy = 0;
        foreach ((double x, double y) in pts)
        {
            cx += x;
            cy += y;
        }

        cx /= pts.Count;
        cy /= pts.Count;
        double mean = 0;
        foreach ((double x, double y) in pts)
            mean += Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
        mean /= pts.Count;
        return (cx, cy, mean > 1e-12 ? Math.Sqrt(2) / mean : 0);
    }

    private static Transform2D? FitHomography(
        IReadOnlyList<(double X, double Y)> src, IReadOnlyList<(double X, double Y)> dst,
        double scx, double scy, double ss, double dcx, double dcy, double ds)
    {
        // h8 = 1; normal equations of the 2n x 8 DLT system.
        var ata = new double[8, 8];
        var atb = new double[8];
        var row = new double[8];
        for (int i = 0; i < src.Count; i++)
        {
            double x = (src[i].X - scx) * ss, y = (src[i].Y - scy) * ss;
            double u = (dst[i].X - dcx) * ds, v = (dst[i].Y - dcy) * ds;

            row[0] = x; row[1] = y; row[2] = 1; row[3] = 0; row[4] = 0; row[5] = 0; row[6] = -u * x; row[7] = -u * y;
            Accumulate(ata, atb, row, u);
            row[0] = 0; row[1] = 0; row[2] = 0; row[3] = x; row[4] = y; row[5] = 1; row[6] = -v * x; row[7] = -v * y;
            Accumulate(ata, atb, row, v);
        }

        double[]? h = Solve(ata, atb, 8);
        return h is null ? null : new Transform2D(h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1);
    }

    private static Transform2D? FitAffine(
        IReadOnlyList<(double X, double Y)> src, IReadOnlyList<(double X, double Y)> dst,
        double scx, double scy, double ss, double dcx, double dcy, double ds)
    {
        var ata = new double[6, 6];
        var atb = new double[6];
        var row = new double[6];
        for (int i = 0; i < src.Count; i++)
        {
            double x = (src[i].X - scx) * ss, y = (src[i].Y - scy) * ss;
            double u = (dst[i].X - dcx) * ds, v = (dst[i].Y - dcy) * ds;
            row[0] = x; row[1] = y; row[2] = 1; row[3] = 0; row[4] = 0; row[5] = 0;
            Accumulate(ata, atb, row, u);
            row[0] = 0; row[1] = 0; row[2] = 0; row[3] = x; row[4] = y; row[5] = 1;
            Accumulate(ata, atb, row, v);
        }

        double[]? a = Solve(ata, atb, 6);
        return a is null ? null : new Transform2D(a[0], a[1], a[2], a[3], a[4], a[5], 0, 0, 1);
    }

    private static Transform2D? FitSimilarity(
        IReadOnlyList<(double X, double Y)> src, IReadOnlyList<(double X, double Y)> dst,
        double scx, double scy, double ss, double dcx, double dcy, double ds)
    {
        // [u] = [a -b][x] + [tx]
        // [v]   [b  a][y]   [ty]
        var ata = new double[4, 4];
        var atb = new double[4];
        var row = new double[4];
        for (int i = 0; i < src.Count; i++)
        {
            double x = (src[i].X - scx) * ss, y = (src[i].Y - scy) * ss;
            double u = (dst[i].X - dcx) * ds, v = (dst[i].Y - dcy) * ds;
            row[0] = x; row[1] = -y; row[2] = 1; row[3] = 0;
            Accumulate(ata, atb, row, u);
            row[0] = y; row[1] = x; row[2] = 0; row[3] = 1;
            Accumulate(ata, atb, row, v);
        }

        double[]? p = Solve(ata, atb, 4);
        return p is null ? null : new Transform2D(p[0], -p[1], p[2], p[1], p[0], p[3], 0, 0, 1);
    }

    private static Transform2D? FitTranslation(IReadOnlyList<(double X, double Y)> src, IReadOnlyList<(double X, double Y)> dst)
    {
        double tx = 0, ty = 0;
        for (int i = 0; i < src.Count; i++)
        {
            tx += dst[i].X - src[i].X;
            ty += dst[i].Y - src[i].Y;
        }

        return new Transform2D(1, 0, tx / src.Count, 0, 1, ty / src.Count, 0, 0, 1);
    }

    private static void Accumulate(double[,] ata, double[] atb, double[] row, double rhs)
    {
        int m = row.Length;
        for (int r = 0; r < m; r++)
        {
            if (row[r] == 0)
                continue;
            atb[r] += row[r] * rhs;
            for (int c = 0; c < m; c++)
                ata[r, c] += row[r] * row[c];
        }
    }

    /// <summary>Gaussian elimination with partial pivoting; <c>null</c> when singular.</summary>
    internal static double[]? Solve(double[,] a, double[] b, int n)
    {
        var m = new double[n, n + 1];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
                m[r, c] = a[r, c];
            m[r, n] = b[r];
        }

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            double best = Math.Abs(m[col, col]);
            for (int r = col + 1; r < n; r++)
            {
                double v = Math.Abs(m[r, col]);
                if (v > best)
                {
                    best = v;
                    pivot = r;
                }
            }

            if (best < 1e-12)
                return null;

            if (pivot != col)
            {
                for (int c = col; c <= n; c++)
                    (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            }

            for (int r = col + 1; r < n; r++)
            {
                double f = m[r, col] / m[col, col];
                if (f == 0)
                    continue;
                for (int c = col; c <= n; c++)
                    m[r, c] -= f * m[col, c];
            }
        }

        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double sum = m[r, n];
            for (int c = r + 1; c < n; c++)
                sum -= m[r, c] * x[c];
            x[r] = sum / m[r, r];
            if (double.IsNaN(x[r]) || double.IsInfinity(x[r]))
                return null;
        }

        return x;
    }

    /// <summary>Ray-casting point-in-polygon.</summary>
    public static bool PointInPolygon(IReadOnlyList<(double X, double Y)> poly, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            (double xi, double yi) = poly[i];
            (double xj, double yj) = poly[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi + 1e-300) + xi)
                inside = !inside;
        }

        return inside;
    }

    /// <summary>Signed shoelace area of a polygon in perimeter order.</summary>
    public static double Area(IReadOnlyList<(double X, double Y)> poly)
    {
        double a = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            a += (poly[j].X * poly[i].Y) - (poly[i].X * poly[j].Y);
        return a / 2;
    }

    /// <summary>True when the perimeter-ordered quad is convex and not self-intersecting.</summary>
    public static bool IsConvex(IReadOnlyList<(double X, double Y)> quad)
    {
        int sign = 0;
        for (int i = 0; i < quad.Count; i++)
        {
            (double ax, double ay) = quad[i];
            (double bx, double by) = quad[(i + 1) % quad.Count];
            (double cx, double cy) = quad[(i + 2) % quad.Count];
            double cross = (bx - ax) * (cy - by) - (by - ay) * (cx - bx);
            if (Math.Abs(cross) < 1e-9)
                continue;
            int s = Math.Sign(cross);
            if (sign == 0)
                sign = s;
            else if (s != sign)
                return false;
        }

        return sign != 0;
    }
}
