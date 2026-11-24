#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo;

/// <summary>Knobs of the per-edge meter. Units: pixels of the frame the field belongs to.</summary>
internal sealed record PromoEdgeOptions(
    /// <summary>Half-width of the profile window of the first pass (the seed may be a few px off).</summary>
    double Reach1 = 9.0,
    /// <summary>Half-width of the second, re-seeded pass.</summary>
    double Reach2 = 6.0,
    double Step = 0.25,
    /// <summary>Fraction of an edge, from each end, left out (rounded corners, neighbouring-edge bleed).</summary>
    double EndTrim = 0.12,
    /// <summary>Spacing of profile positions along the edge.</summary>
    double AlongStep = 1.0,
    /// <summary>Per-position background/foreground levels (moving average along the edge) instead of one pair per edge.</summary>
    bool LocalLevels = false,
    int LocalWindow = 31,
    /// <summary>|foreground - background| below this (field units) is not an edge.</summary>
    double MinSpan = 6.0,
    /// <summary>RMS of the robust-fit residual above this (px) marks the edge as not clean.</summary>
    double MaxResidualRms = 0.6,
    double MinInlier = 0.85,
    /// <summary>Every profile sample must lie this far (px) inside the frame.</summary>
    double FrameMargin = 1.0,
    double OutlierSigma = 3.0,
    double OutlierFloorPx = 0.3);

/// <summary>One fitted edge: the segment it passes through at the seed's end points (field px) and how clean the fit was.</summary>
internal sealed record PromoEdge(
    bool Valid, string Reason,
    (double X, double Y) A, (double X, double Y) B,
    double ResidualRms, double InlierFraction, double Span, double ShiftPx, int Positions)
{
    /// <summary>Diagnostics: along-edge position (px), fitted offset d (px, along the inward normal from the pass' seed edge) and whether the sample was kept by the robust fit.</summary>
    public double[]? Ts { get; init; }

    public double[]? Ds { get; init; }

    public bool[]? Keep { get; init; }

    /// <summary>Sagitta of the fitted parabola (px): how far the middle of the edge sits from its chord.</summary>
    public double Bow { get; init; }
}

internal sealed record PromoQuadFit(Quad? Quad, PromoEdge[] Edges)
{
    /// <summary>Edge order: top (TL to TR), right (TR to BR), bottom (BR to BL), left (BL to TL).</summary>
    public static readonly string[] EdgeNames = ["top", "right", "bottom", "left"];
}

/// <summary>
/// Test-only sub-pixel edge meter adapted from <c>RenderPath/EdgeMeter.cs</c> (same area-conserving profile integral: along the inward normal the linear
/// field is sampled bilinearly and summed, <c>d = reach - integral</c> is the edge position), with what a measurement on a real film needs:
/// two passes (the second re-seeded on the first's line so the answer does not depend on the seed's error), a robust line fit, per-edge validity
/// (fully inside the frame, enough contrast, clean fit) so a quad is only reported when all four edges pass, and optional per-position levels.
/// Continuous coordinates: pixel i covers [i, i+1), value at i + 0.5; quads TL, TR, BL, BR as <see cref="Quad"/>.
/// </summary>
internal static class PromoEdgeFit
{
    public static PromoQuadFit FitQuad(float[] field, int w, int h, Quad seed, PromoEdgeOptions o)
    {
        (double X, double Y)[] c = [seed.Corner(0), seed.Corner(1), seed.Corner(3), seed.Corner(2)];
        double cx = c.Average(p => p.X), cy = c.Average(p => p.Y);
        var edges = new PromoEdge[4];
        for (int e = 0; e < 4; e++)
            edges[e] = FitEdge(field, w, h, c[e], c[(e + 1) % 4], (cx, cy), o);

        if (edges.Any(e => !e.Valid))
            return new PromoQuadFit(null, edges);
        (double X, double Y)? tl = Intersect(edges[3], edges[0]), tr = Intersect(edges[0], edges[1]);
        (double X, double Y)? br = Intersect(edges[1], edges[2]), bl = Intersect(edges[2], edges[3]);
        if (tl is null || tr is null || br is null || bl is null)
            return new PromoQuadFit(null, edges);
        return new PromoQuadFit(Quad.From(seed.T, tl.Value, tr.Value, bl.Value, br.Value), edges);
    }

    public static PromoEdge FitEdge(float[] field, int w, int h, (double X, double Y) a, (double X, double Y) b, (double X, double Y) inside, PromoEdgeOptions o)
    {
        (double X, double Y) pa = a, pb = b;
        PromoEdge last = Fail("not run", a, b);
        for (int pass = 0; pass < 2; pass++)
        {
            last = Pass(field, w, h, pa, pb, inside, pass == 0 ? o.Reach1 : o.Reach2, o);
            if (!last.Valid)
                return last with { A = pa, B = pb };
            pa = last.A;
            pb = last.B;
        }

        return last with { ShiftPx = Math.Sqrt((pa.X - a.X) * (pa.X - a.X) + (pa.Y - a.Y) * (pa.Y - a.Y)) };
    }

    private static PromoEdge Fail(string reason, (double X, double Y) a, (double X, double Y) b) =>
        new(false, reason, a, b, double.NaN, double.NaN, double.NaN, 0, 0);

    private static PromoEdge Pass(float[] f, int w, int h, (double X, double Y) a, (double X, double Y) b, (double X, double Y) inside, double reach, PromoEdgeOptions o)
    {
        double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        if (len < 16)
            return Fail("short", a, b);
        double dx = (b.X - a.X) / len, dy = (b.Y - a.Y) / len;
        double nx = -dy, ny = dx;
        if ((inside.X - a.X) * nx + (inside.Y - a.Y) * ny < 0)
        {
            nx = -nx;
            ny = -ny;
        }

        int n = (int)Math.Clamp(len * (1 - 2 * o.EndTrim) / o.AlongStep, 8, 800);
        int steps = (int)Math.Round(2 * reach / o.Step);
        int cal = Math.Max(1, (int)Math.Round(1.0 / o.Step));
        var ts = new double[n];
        var samples = new double[n][];
        for (int i = 0; i < n; i++)
        {
            double t = len * (o.EndTrim + (1 - 2 * o.EndTrim) * (i + 0.5) / n);
            double px = a.X + t * dx, py = a.Y + t * dy;
            // Whole profile (both extremes of the normal sweep) must lie inside the frame.
            foreach (double off in new[] { -reach, reach })
            {
                double sx = px + off * nx, sy = py + off * ny;
                if (sx < o.FrameMargin || sy < o.FrameMargin || sx > w - o.FrameMargin || sy > h - o.FrameMargin)
                    return Fail("outside the frame", a, b);
            }

            var s = new double[steps + 1];
            for (int k = 0; k <= steps; k++)
            {
                double off = -reach + k * o.Step;
                s[k] = Bilinear(f, w, h, px + off * nx, py + off * ny);
            }

            samples[i] = s;
            ts[i] = t;
        }

        // Levels: background = the outer 1 px of the window, foreground = the inner end.
        var bg = new double[n];
        var fg = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sb = 0, sf = 0;
            for (int k = 0; k <= cal; k++)
            {
                sb += samples[i][k];
                sf += samples[i][steps - k];
            }

            bg[i] = sb / (cal + 1);
            fg[i] = sf / (cal + 1);
        }

        if (o.LocalLevels)
        {
            bg = Smooth(bg, o.LocalWindow / 2);
            fg = Smooth(fg, o.LocalWindow / 2);
        }
        else
        {
            double mb = bg.Average(), mf = fg.Average();
            Array.Fill(bg, mb);
            Array.Fill(fg, mf);
        }

        double spanMean = 0;
        var d = new double[n];
        for (int i = 0; i < n; i++)
        {
            double span = fg[i] - bg[i];
            spanMean += Math.Abs(span);
            if (Math.Abs(span) < o.MinSpan)
                return Fail($"low contrast ({Math.Abs(span):0.0})", a, b);
            double integral = 0;
            for (int k = 0; k <= steps; k++)
            {
                double wgt = k == 0 || k == steps ? 0.5 : 1.0;
                integral += wgt * (samples[i][k] - bg[i]) / span * o.Step;
            }

            d[i] = reach - integral;
        }

        spanMean /= n;

        // Robust fit. Real screen edges are slightly bowed (lens, glass), so cleanliness is judged on the residual of d = p + q u + c u^2
        // (u = t/len - 0.5) and outliers are flagged against it; the edge itself is then the straight least-squares line through the kept samples
        // (the chord of the bow: it moves with the edge, and its bias scales with the slowly changing bow, not with frame-to-frame noise).
        var keep = new bool[n];
        Array.Fill(keep, true);
        var us = new double[n];
        for (int i = 0; i < n; i++)
            us[i] = ts[i] / len - 0.5;
        for (int iter = 0; iter < 5; iter++)
        {
            QuadFit(us, d, keep, out double qp, out double qq, out double qc);
            var res = new double[n];
            for (int i = 0; i < n; i++)
                res[i] = d[i] - (qp + qq * us[i] + qc * us[i] * us[i]);
            double[] abs = res.Where((_, i) => keep[i]).Select(Math.Abs).OrderBy(v => v).ToArray();
            double sigma = abs.Length == 0 ? 0 : 1.4826 * abs[abs.Length / 2];
            double cut = Math.Max(o.OutlierSigma * sigma, o.OutlierFloorPx);
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                bool k = Math.Abs(res[i]) <= cut;
                changed |= k != keep[i];
                keep[i] = k;
            }

            if (!changed)
                break;
        }

        QuadFit(us, d, keep, out double fp, out double fq, out double fc);
        int inl = keep.Count(k => k);
        double ss = 0;
        for (int i = 0; i < n; i++)
        {
            if (keep[i])
                ss += Math.Pow(d[i] - (fp + fq * us[i] + fc * us[i] * us[i]), 2);
        }

        double resRms = inl > 0 ? Math.Sqrt(ss / inl) : double.NaN;
        double frac = inl / (double)n;
        LineFit(ts, d, keep, out double p0, out double q0);
        (double X, double Y) na = (a.X + nx * p0, a.Y + ny * p0);
        (double X, double Y) nb = (b.X + nx * (p0 + q0 * len), b.Y + ny * (p0 + q0 * len));
        string reason = "ok";
        bool valid = true;
        if (frac < o.MinInlier)
        {
            valid = false;
            reason = $"unclean (inliers {frac:0.00})";
        }
        else if (resRms > o.MaxResidualRms)
        {
            valid = false;
            reason = $"unclean (residual {resRms:0.00} px)";
        }

        return new PromoEdge(valid, reason, na, nb, resRms, frac, spanMean, Math.Abs(p0 + q0 * len / 2), n) { Ts = ts, Ds = d, Keep = keep, Bow = Math.Abs(fc) * 0.25 };
    }

    private static void QuadFit(double[] u, double[] d, bool[] keep, out double p, out double q, out double c)
    {
        var m = new double[3, 4];
        for (int i = 0; i < u.Length; i++)
        {
            if (!keep[i])
                continue;
            double[] b = [1, u[i], u[i] * u[i]];
            for (int r = 0; r < 3; r++)
            {
                for (int k = 0; k < 3; k++)
                    m[r, k] += b[r] * b[k];
                m[r, 3] += b[r] * d[i];
            }
        }

        for (int col = 0; col < 3; col++)
        {
            int piv = col;
            for (int r = col + 1; r < 3; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col]))
                    piv = r;
            }

            for (int k = 0; k < 4; k++)
                (m[col, k], m[piv, k]) = (m[piv, k], m[col, k]);
            if (Math.Abs(m[col, col]) < 1e-12)
            {
                p = q = c = 0;
                return;
            }

            for (int r = 0; r < 3; r++)
            {
                if (r == col)
                    continue;
                double f = m[r, col] / m[col, col];
                for (int k = col; k < 4; k++)
                    m[r, k] -= f * m[col, k];
            }
        }

        p = m[0, 3] / m[0, 0];
        q = m[1, 3] / m[1, 1];
        c = m[2, 3] / m[2, 2];
    }

    private static void LineFit(double[] t, double[] d, bool[] keep, out double p, out double q)
    {
        double s0 = 0, s1 = 0, s2 = 0, y0 = 0, y1 = 0;
        for (int i = 0; i < t.Length; i++)
        {
            if (!keep[i])
                continue;
            s0 += 1; s1 += t[i]; s2 += t[i] * t[i]; y0 += d[i]; y1 += t[i] * d[i];
        }

        double det = s0 * s2 - s1 * s1;
        if (s0 < 2 || Math.Abs(det) < 1e-12)
        {
            p = s0 > 0 ? y0 / s0 : 0;
            q = 0;
            return;
        }

        q = (s0 * y1 - s1 * y0) / det;
        p = (y0 - q * s1) / s0;
    }

    private static double[] Smooth(double[] x, int half)
    {
        var o = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            int lo = Math.Max(0, i - half), hi = Math.Min(x.Length - 1, i + half);
            double s = 0;
            for (int j = lo; j <= hi; j++)
                s += x[j];
            o[i] = s / (hi - lo + 1);
        }

        return o;
    }

    private static (double X, double Y)? Intersect(PromoEdge e1, PromoEdge e2)
    {
        double d1x = e1.B.X - e1.A.X, d1y = e1.B.Y - e1.A.Y, d2x = e2.B.X - e2.A.X, d2y = e2.B.Y - e2.A.Y;
        double den = d1x * d2y - d1y * d2x;
        if (Math.Abs(den) < 1e-9)
            return null;
        double t = ((e2.A.X - e1.A.X) * d2y - (e2.A.Y - e1.A.Y) * d2x) / den;
        return (e1.A.X + t * d1x, e1.A.Y + t * d1y);
    }

    /// <summary>Bilinear sample at continuous (x, y), pixel centres at i + 0.5, clamped at the frame edge.</summary>
    public static double Bilinear(float[] f, int w, int h, double x, double y)
    {
        double fx = x - 0.5, fy = y - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double ax = fx - x0, ay = fy - y0;
        int xa = Math.Clamp(x0, 0, w - 1), xb = Math.Clamp(x0 + 1, 0, w - 1);
        int ya = Math.Clamp(y0, 0, h - 1), yb = Math.Clamp(y0 + 1, 0, h - 1);
        double v00 = f[ya * w + xa], v10 = f[ya * w + xb], v01 = f[yb * w + xa], v11 = f[yb * w + xb];
        return (v00 * (1 - ax) + v10 * ax) * (1 - ay) + (v01 * (1 - ax) + v11 * ax) * ay;
    }
}
