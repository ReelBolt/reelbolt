#nullable enable
using System;
using System.Collections.Generic;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

/// <summary>
/// Test-only sub-pixel edge meter for a flat quad (T-005). Unlike the production
/// <c>ChromaEdgeLineFitter</c> (40 probes per edge, 50 % level crossing) it integrates the edge profile: along the
/// inward normal at many positions the field is sampled bilinearly and summed, and for a box-filtered step
/// <c>d = reach - integral</c> is the edge position exactly (area is conserved by any linear resampler). The
/// positions are then fitted with a least-squares line per edge and the corners are the lines' intersections.
/// It has no tunable threshold, so its own floor is a property of the sampling (measured against an analytic
/// anti-aliased control in <c>RenderPathTests</c>), not of a level choice.
///
/// <para>Coordinates are continuous source pixels (pixel i covers [i, i+1), value sampled at i + 0.5), quad corners
/// TL, TR, BL, BR as in <see cref="Quad"/>.</para>
/// </summary>
internal static class EdgeMeter
{
    public const double Reach = 6.0;
    public const double Step = 0.25;

    /// <summary>Fraction of an edge, from each end, left out (rounded corners, neighbouring-edge bleed).</summary>
    public const double EndTrim = 0.12;

    public static Quad? Measure(float[] field, int w, int h, Quad seed, double reach = Reach)
    {
        // Edges as (start, end) in clockwise order TL->TR->BR->BL->TL.
        (double X, double Y)[] c = [seed.Corner(0), seed.Corner(1), seed.Corner(3), seed.Corner(2)];
        double cx = (c[0].X + c[1].X + c[2].X + c[3].X) / 4, cy = (c[0].Y + c[1].Y + c[2].Y + c[3].Y) / 4;
        var lines = new (double Px, double Py, double Dx, double Dy)[4];
        for (int e = 0; e < 4; e++)
        {
            (double ax, double ay) = c[e];
            (double bx, double by) = c[(e + 1) % 4];
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            if (len < 8)
                return null;
            double dx = (bx - ax) / len, dy = (by - ay) / len;
            double nx = -dy, ny = dx; // candidate normal
            if ((cx - ax) * nx + (cy - ay) * ny < 0)
            {
                nx = -nx;
                ny = -ny;
            }

            int n = (int)Math.Clamp(len * (1 - 2 * EndTrim) / 1.0, 8, 400);
            var ts = new double[n];
            var ds = new double[n];
            int steps = (int)Math.Round(2 * reach / Step);
            var profile = new double[steps + 1];
            double bgSum = 0, fgSum = 0;
            int cal = (int)Math.Round(1.0 / Step);
            var samples = new double[n][];
            for (int i = 0; i < n; i++)
            {
                double t = len * (EndTrim + (1 - 2 * EndTrim) * (i + 0.5) / n);
                double px = ax + t * dx, py = ay + t * dy;
                var s = new double[steps + 1];
                for (int k = 0; k <= steps; k++)
                {
                    double off = -reach + k * Step;
                    s[k] = Bilinear(field, w, h, px + off * nx, py + off * ny);
                }

                samples[i] = s;
                ts[i] = t;
                for (int k = 0; k <= cal; k++)
                {
                    bgSum += s[k];
                    fgSum += s[steps - k];
                }
            }

            double bg = bgSum / (n * (cal + 1)), fg = fgSum / (n * (cal + 1));
            double span = fg - bg;
            if (Math.Abs(span) < 1e-6)
                return null;
            for (int i = 0; i < n; i++)
            {
                double integral = 0;
                double[] s = samples[i];
                for (int k = 0; k <= steps; k++)
                {
                    double wgt = k == 0 || k == steps ? 0.5 : 1.0;
                    integral += wgt * (s[k] - bg) / span * Step;
                }

                ds[i] = reach - integral; // edge offset along the inward normal from the seed edge
            }

            // d = a + b t
            double mt = 0, md = 0;
            for (int i = 0; i < n; i++)
            {
                mt += ts[i];
                md += ds[i];
            }

            mt /= n;
            md /= n;
            double stt = 0, std = 0;
            for (int i = 0; i < n; i++)
            {
                stt += (ts[i] - mt) * (ts[i] - mt);
                std += (ts[i] - mt) * (ds[i] - md);
            }

            double b = stt > 0 ? std / stt : 0;
            double a = md - b * mt;
            // Line: P(t) = A + t*dir + (a + b t)*n
            lines[e] = (ax + a * nx, ay + a * ny, dx + b * nx, dy + b * ny);
        }

        (double X, double Y)? tl = Intersect(lines[3], lines[0]);
        (double X, double Y)? tr = Intersect(lines[0], lines[1]);
        (double X, double Y)? br = Intersect(lines[1], lines[2]);
        (double X, double Y)? bl = Intersect(lines[2], lines[3]);
        if (tl is null || tr is null || br is null || bl is null)
            return null;
        return Quad.From(seed.T, tl.Value, tr.Value, bl.Value, br.Value);
    }

    private static (double X, double Y)? Intersect((double Px, double Py, double Dx, double Dy) l1, (double Px, double Py, double Dx, double Dy) l2)
    {
        double den = l1.Dx * l2.Dy - l1.Dy * l2.Dx;
        if (Math.Abs(den) < 1e-9)
            return null;
        double t = ((l2.Px - l1.Px) * l2.Dy - (l2.Py - l1.Py) * l2.Dx) / den;
        return (l1.Px + t * l1.Dx, l1.Py + t * l1.Dy);
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

    /// <summary>
    /// Exact-ish anti-aliased coverage of a convex quad (0..1 per pixel) via <paramref name="ss"/> x <paramref name="ss"/>
    /// supersampling near the boundary, analytic elsewhere. The meter's own floor control and the unit-test ground truth.
    /// </summary>
    public static void Coverage(float[] dst, int w, int h, Quad q, int ss = 8)
    {
        Array.Clear(dst);
        (double X, double Y)[] c = [q.Corner(0), q.Corner(1), q.Corner(3), q.Corner(2)];
        double cx = (c[0].X + c[1].X + c[2].X + c[3].X) / 4, cy = (c[0].Y + c[1].Y + c[2].Y + c[3].Y) / 4;
        var ex = new double[4];
        var ey = new double[4];
        var enx = new double[4];
        var eny = new double[4];
        for (int e = 0; e < 4; e++)
        {
            (double ax, double ay) = c[e];
            (double bx, double by) = c[(e + 1) % 4];
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            double dx = (bx - ax) / len, dy = (by - ay) / len;
            double nx = -dy, ny = dx;
            if ((cx - ax) * nx + (cy - ay) * ny < 0)
            {
                nx = -nx;
                ny = -ny;
            }

            ex[e] = ax;
            ey[e] = ay;
            enx[e] = nx;
            eny[e] = ny;
        }

        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(Math.Min(c[0].X, c[1].X), Math.Min(c[2].X, c[3].X))) - 2);
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(Math.Max(c[0].X, c[1].X), Math.Max(c[2].X, c[3].X))) + 2);
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(Math.Min(c[0].Y, c[1].Y), Math.Min(c[2].Y, c[3].Y))) - 2);
        int y1 = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(Math.Max(c[0].Y, c[1].Y), Math.Max(c[2].Y, c[3].Y))) + 2);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double min = double.MaxValue;
                for (int e = 0; e < 4; e++)
                    min = Math.Min(min, (px - ex[e]) * enx[e] + (py - ey[e]) * eny[e]);
                if (min > 0.75)
                {
                    dst[y * w + x] = 1;
                    continue;
                }

                if (min < -0.75)
                    continue;
                int hit = 0;
                for (int sy = 0; sy < ss; sy++)
                {
                    for (int sx = 0; sx < ss; sx++)
                    {
                        double qx = x + (sx + 0.5) / ss, qy = y + (sy + 0.5) / ss;
                        bool inside = true;
                        for (int e = 0; e < 4 && inside; e++)
                            inside = (qx - ex[e]) * enx[e] + (qy - ey[e]) * eny[e] >= 0;
                        if (inside)
                            hit++;
                    }
                }

                dst[y * w + x] = hit / (float)(ss * ss);
            }
        }
    }
}
