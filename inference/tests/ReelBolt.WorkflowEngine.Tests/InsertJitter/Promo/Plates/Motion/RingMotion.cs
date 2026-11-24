#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates.Motion;

/// <summary>Knobs of the scene-ring motion estimator (units: pixels of the plate source, 1344x768).</summary>
internal sealed record RingOptions(
    /// <summary>2 = translation, 6 = affine, 8 = homography.</summary>
    int Params = 8,
    /// <summary>Inner radius of the ring: pixels closer to the plate's quad than this are ignored (the plate's own bowed edge, the green bleed).</summary>
    double DMin = 14,
    /// <summary>Outer radius of the ring.</summary>
    double DMax = 90,
    /// <summary>A ring pixel is used when the blurred gradient magnitude of the earlier frame is at least this (levels per px).</summary>
    double GradMin = 2.5,
    int Levels = 3,
    double BlurSigma = 1.0,
    int MaxIter = 12,
    /// <summary>Coordinate normalisation (px): u = (X - cx) / Scale.</summary>
    double Scale = 400,
    int MinPixels = 3000);

/// <summary>One consecutive pair: the homography (absolute plate px, row-major 3x3) that maps frame <see cref="Frame"/>'s coordinates onto frame <see cref="Frame"/> + 1's.</summary>
internal sealed record RingPair(int Frame, double[] H, int Pixels, double ResidualRms, double SeTx, double SeTy, double MeanGrad, bool Ok, string Why);

/// <summary>A float image pyramid with central-difference gradients; level 0 is gaussian blurred, every next level is a 2x2 box average.</summary>
internal sealed class Pyramid
{
    public int Levels;
    public int[] W = [];
    public int[] H = [];
    public float[][] I = [];
    public float[][] Gx = [];
    public float[][] Gy = [];

    public static Pyramid Build(byte[] gray, int w, int h, int levels, double sigma)
    {
        var p = new Pyramid { Levels = levels, W = new int[levels], H = new int[levels], I = new float[levels][], Gx = new float[levels][], Gy = new float[levels][] };
        var f = new float[w * h];
        for (int i = 0; i < f.Length; i++)
            f[i] = gray[i];
        p.I[0] = sigma > 0 ? Blur(f, w, h, sigma) : f;
        p.W[0] = w;
        p.H[0] = h;
        for (int l = 1; l < levels; l++)
        {
            int pw = p.W[l - 1], ph = p.H[l - 1], nw = pw / 2, nh = ph / 2;
            var g = new float[nw * nh];
            float[] s = p.I[l - 1];
            for (int y = 0; y < nh; y++)
            {
                for (int x = 0; x < nw; x++)
                    g[y * nw + x] = 0.25f * (s[2 * y * pw + 2 * x] + s[2 * y * pw + 2 * x + 1] + s[(2 * y + 1) * pw + 2 * x] + s[(2 * y + 1) * pw + 2 * x + 1]);
            }

            p.I[l] = g;
            p.W[l] = nw;
            p.H[l] = nh;
        }

        for (int l = 0; l < levels; l++)
        {
            int lw = p.W[l], lh = p.H[l];
            float[] s = p.I[l];
            var gx = new float[lw * lh];
            var gy = new float[lw * lh];
            for (int y = 0; y < lh; y++)
            {
                int ya = Math.Max(0, y - 1), yb = Math.Min(lh - 1, y + 1);
                for (int x = 0; x < lw; x++)
                {
                    int xa = Math.Max(0, x - 1), xb = Math.Min(lw - 1, x + 1);
                    gx[y * lw + x] = (s[y * lw + xb] - s[y * lw + xa]) / (xb - xa);
                    gy[y * lw + x] = (s[yb * lw + x] - s[ya * lw + x]) / (yb - ya);
                }
            }

            p.Gx[l] = gx;
            p.Gy[l] = gy;
        }

        return p;
    }

    public static float[] Blur(float[] f, int w, int h, double sigma)
    {
        int r = (int)Math.Ceiling(3 * sigma);
        var k = new float[2 * r + 1];
        double sum = 0;
        for (int i = -r; i <= r; i++)
        {
            double v = Math.Exp(-0.5 * i * i / (sigma * sigma));
            k[i + r] = (float)v;
            sum += v;
        }

        for (int i = 0; i < k.Length; i++)
            k[i] = (float)(k[i] / sum);
        var tmp = new float[f.Length];
        var o = new float[f.Length];
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int i = -r; i <= r; i++)
                    s += f[y * w + Math.Clamp(x + i, 0, w - 1)] * k[i + r];
                tmp[y * w + x] = s;
            }
        });
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int i = -r; i <= r; i++)
                    s += tmp[Math.Clamp(y + i, 0, h - 1) * w + x] * k[i + r];
                o[y * w + x] = s;
            }
        });
        return o;
    }

    /// <summary>Bilinear sample at continuous (x, y) of level <paramref name="l"/> (pixel centres at i + 0.5); NaN outside the usable area.</summary>
    public static float Sample(float[] f, int w, int h, double x, double y)
    {
        double fx = x - 0.5, fy = y - 0.5;
        if (fx < 0 || fy < 0 || fx >= w - 1 || fy >= h - 1)
            return float.NaN;
        int x0 = (int)fx, y0 = (int)fy;
        double ax = fx - x0, ay = fy - y0;
        int o = y0 * w + x0;
        return (float)((f[o] * (1 - ax) + f[o + 1] * ax) * (1 - ay) + (f[o + w] * (1 - ax) + f[o + w + 1] * ax) * ay);
    }
}

/// <summary>
/// Scene-ring motion: how the picture AROUND the plate (the monitor bezel and what is beyond it) moved from one frame to the next, independent of the plate's own
/// chroma and luma. A robust (Tukey) multi-scale inverse-free Lucas-Kanade on the luma of two consecutive frames, restricted to a ring of textured pixels at
/// distance [DMin, DMax] outside the plate's quad (geometry from any smooth track; it only places the ring), fitted with the same projective family as the
/// plate's quad. The estimate is the homography of that ring; <see cref="PredictCorners"/> carries it to the plate's corners.
/// </summary>
internal static class RingMotion
{
    /// <summary>Corner order TL, TR, BL, BR as <see cref="Quad"/>; quad edges as polygon TL, TR, BR, BL.</summary>
    private static (double X, double Y)[] Polygon(Quad q) => [q.Corner(0), q.Corner(1), q.Corner(3), q.Corner(2)];

    /// <summary>Largest outward distance (px) of (x, y) from the convex quad's edge lines; negative inside. Precomputed edge normals.</summary>
    private readonly record struct Edges(double[] Nx, double[] Ny, double[] C)
    {
        public static Edges Of(Quad q)
        {
            (double X, double Y)[] p = Polygon(q);
            double cx = p.Average(v => v.X), cy = p.Average(v => v.Y);
            var nx = new double[4];
            var ny = new double[4];
            var c = new double[4];
            for (int k = 0; k < 4; k++)
            {
                (double ax, double ay) = p[k];
                (double bx, double by) = p[(k + 1) % 4];
                double ex = bx - ax, ey = by - ay, len = Math.Sqrt(ex * ex + ey * ey);
                double ux = ey / len, uy = -ex / len; // candidate outward normal
                if ((cx - ax) * ux + (cy - ay) * uy > 0)
                {
                    ux = -ux;
                    uy = -uy;
                }

                nx[k] = ux;
                ny[k] = uy;
                c[k] = -(ax * ux + ay * uy);
            }

            return new Edges(nx, ny, c);
        }

        public double Outward(double x, double y)
        {
            double best = double.NegativeInfinity;
            for (int k = 0; k < 4; k++)
                best = Math.Max(best, x * Nx[k] + y * Ny[k] + C[k]);
            return best;
        }
    }

    /// <summary>How much texture the ring offers: pixel count, mean gradient magnitude, mean squared gradient (the information LK has to work with), share of pixels over the gradient threshold.</summary>
    public static (int N, double MeanGm, double MeanGm2, double FracTextured) TextureStats(Pyramid a, Quad q, RingOptions o)
    {
        Edges edges = Edges.Of(q);
        int w = a.W[0], h = a.H[0];
        double sg = 0, sg2 = 0;
        int n = 0, tex = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double d = edges.Outward(x + 0.5, y + 0.5);
                if (d < o.DMin || d > o.DMax)
                    continue;
                double gx = a.Gx[0][y * w + x], gy = a.Gy[0][y * w + x], gm2 = gx * gx + gy * gy;
                n++;
                sg += Math.Sqrt(gm2);
                sg2 += gm2;
                if (gm2 >= o.GradMin * o.GradMin)
                    tex++;
            }
        }

        return n == 0 ? (0, 0, 0, 0) : (n, sg / n, sg2 / n, tex / (double)n);
    }

    public static RingPair Estimate(Pyramid a, Pyramid b, Quad quadAtA, RingOptions o, int frame)
    {
        int np = o.Params;
        int[] active = np switch { 2 => [2, 5], 6 => [0, 1, 2, 3, 4, 5], _ => [0, 1, 2, 3, 4, 5, 6, 7] };
        (double X, double Y)[] poly = Polygon(quadAtA);
        double cx = poly.Average(v => v.X), cy = poly.Average(v => v.Y);
        Edges edges = Edges.Of(quadAtA);
        double S = o.Scale;
        var p = new double[8];
        int pixels = 0;
        double resRms = double.NaN, seX = double.NaN, seY = double.NaN, meanGrad = double.NaN;
        int levels = Math.Min(o.Levels, a.Levels);
        for (int l = levels - 1; l >= 0; l--)
        {
            double scale = 1 << l;
            int lw = a.W[l], lh = a.H[l];
            float[] i0 = a.I[l], gx0 = a.Gx[l], gy0 = a.Gy[l];
            float[] i1 = b.I[l], gx1 = b.Gx[l], gy1 = b.Gy[l];

            // Pixel list of this level: ring membership in level-0 px, textured in the earlier frame.
            var xs = new List<float>();
            var ys = new List<float>();
            var gmags = new List<float>();
            for (int y = 0; y < lh; y++)
            {
                double Y = (y + 0.5) * scale;
                for (int x = 0; x < lw; x++)
                {
                    double X = (x + 0.5) * scale;
                    double d = edges.Outward(X, Y);
                    if (d < o.DMin || d > o.DMax)
                        continue;
                    double gm = Math.Sqrt(gx0[y * lw + x] * (double)gx0[y * lw + x] + gy0[y * lw + x] * (double)gy0[y * lw + x]);
                    if (gm * 1.0 < o.GradMin)
                        continue;
                    xs.Add(x);
                    ys.Add(y);
                    gmags.Add((float)gm);
                }
            }

            int n = xs.Count;
            if (l == 0)
            {
                pixels = n;
                meanGrad = n == 0 ? 0 : gmags.Average();
            }

            if (n < 50)
                return Fail(frame, pixels, "too few textured ring pixels at level " + l);
            var u = new double[n];
            var v = new double[n];
            var t0 = new double[n];
            for (int i = 0; i < n; i++)
            {
                double X = (xs[i] + 0.5) * scale, Y = (ys[i] + 0.5) * scale;
                u[i] = (X - cx) / S;
                v[i] = (Y - cy) / S;
                t0[i] = i0[(int)ys[i] * lw + (int)xs[i]];
            }

            var r = new double[n];
            var w = new double[n];
            var jac = new double[n][];
            var ok = new bool[n];
            for (int i = 0; i < n; i++)
                jac[i] = new double[8];
            var ata = new double[8 * 8];
            var atb = new double[8];
            for (int iter = 0; iter < o.MaxIter; iter++)
            {
                int valid = 0;
                for (int i = 0; i < n; i++)
                {
                    double nxv = (1 + p[0]) * u[i] + p[1] * v[i] + p[2], nyv = p[3] * u[i] + (1 + p[4]) * v[i] + p[5], dd = p[6] * u[i] + p[7] * v[i] + 1;
                    double wx = nxv / dd, wy = nyv / dd;
                    double X1 = cx + S * wx, Y1 = cy + S * wy;
                    double sx = X1 / scale, sy = Y1 / scale;
                    float val = Pyramid.Sample(i1, lw, lh, sx, sy);
                    float gxv = Pyramid.Sample(gx1, lw, lh, sx, sy), gyv = Pyramid.Sample(gy1, lw, lh, sx, sy);
                    if (float.IsNaN(val) || float.IsNaN(gxv) || float.IsNaN(gyv))
                    {
                        ok[i] = false;
                        continue;
                    }

                    ok[i] = true;
                    valid++;
                    r[i] = val - t0[i];
                    double k = S / scale;
                    double ix = gxv * k, iy = gyv * k;
                    double id = 1.0 / dd, id2 = id * id;
                    double[] j = jac[i];
                    j[0] = ix * u[i] * id;
                    j[1] = ix * v[i] * id;
                    j[2] = ix * id;
                    j[3] = iy * u[i] * id;
                    j[4] = iy * v[i] * id;
                    j[5] = iy * id;
                    j[6] = -(ix * nxv + iy * nyv) * u[i] * id2;
                    j[7] = -(ix * nxv + iy * nyv) * v[i] * id2;
                }

                if (valid < 50)
                    return Fail(frame, pixels, "warp left the frame");

                // Tukey weights from the current residuals (first pass: all ones).
                double sigma;
                if (iter == 0)
                {
                    Array.Fill(w, 1.0);
                    sigma = 0;
                }
                else
                {
                    var abs = new List<double>(valid);
                    for (int i = 0; i < n; i++)
                    {
                        if (ok[i])
                            abs.Add(Math.Abs(r[i]));
                    }

                    abs.Sort();
                    sigma = Math.Max(1.4826 * abs[abs.Count / 2], 0.8);
                    for (int i = 0; i < n; i++)
                    {
                        double z = Math.Abs(r[i]) / (4.685 * sigma);
                        w[i] = !ok[i] || z >= 1 ? 0 : (1 - z * z) * (1 - z * z);
                    }
                }

                Array.Clear(ata);
                Array.Clear(atb);
                double sw = 0, sr2 = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!ok[i] || w[i] == 0)
                        continue;
                    double[] j = jac[i];
                    foreach (int ra in active)
                    {
                        double jw = j[ra] * w[i];
                        atb[ra] -= jw * r[i];
                        foreach (int ca in active)
                            ata[ra * 8 + ca] += jw * j[ca];
                    }

                    sw += w[i];
                    sr2 += w[i] * r[i] * r[i];
                }

                resRms = sw > 0 ? Math.Sqrt(sr2 / sw) : double.NaN;
                double tr = 0;
                foreach (int ra in active)
                    tr += ata[ra * 8 + ra];
                foreach (int ra in active)
                    ata[ra * 8 + ra] += 1e-9 * tr + 1e-12;
                double[]? dp = Solve(ata, atb, active);
                if (dp is null)
                    return Fail(frame, pixels, "singular");
                double stepPx = 0;
                foreach (int ra in active)
                {
                    p[ra] += dp[ra];
                    stepPx = Math.Max(stepPx, Math.Abs(dp[ra]) * S * (ra is 2 or 5 ? 1.0 : 0.5));
                }

                if (l == 0 && (iter == o.MaxIter - 1 || stepPx < 0.0005))
                {
                    // Nominal standard error of the translation (white-noise assumption; spatially correlated codec noise makes the truth larger).
                    double[]? cov = Invert(ata, active);
                    if (cov is not null)
                    {
                        double s2 = resRms * resRms;
                        seX = S * Math.Sqrt(Math.Max(0, cov[2 * 8 + 2]) * s2);
                        seY = S * Math.Sqrt(Math.Max(0, cov[5 * 8 + 5]) * s2);
                    }
                }

                if (stepPx < 0.0005)
                    break;
            }
        }

        // Absolute homography: X' = c + S * W((X - c) / S)  ==  T(c) * Hn * T(-c) with Hn = [[1+p0, p1, p2],[p3, 1+p4, p5],[p6, p7, 1]] on normalised coordinates.
        double[] hn = [1 + p[0], p[1], p[2], p[3], 1 + p[4], p[5], p[6], p[7], 1];
        double[] h = Compose(cx, cy, S, hn);
        if (double.IsNaN(resRms) || pixels < o.MinPixels)
            return new RingPair(frame, h, pixels, resRms, seX, seY, meanGrad, false, pixels < o.MinPixels ? "few pixels" : "no residual");
        return new RingPair(frame, h, pixels, resRms, seX, seY, meanGrad, true, "ok");
    }

    private static RingPair Fail(int frame, int pixels, string why) => new(frame, [1, 0, 0, 0, 1, 0, 0, 0, 1], pixels, double.NaN, double.NaN, double.NaN, double.NaN, false, why);

    /// <summary>Hn on normalised coordinates around (cx, cy) to the absolute homography.</summary>
    private static double[] Compose(double cx, double cy, double s, double[] hn)
    {
        // A = [[1,0,cx],[0,1,cy],[0,0,1]] * diag(s,s,1) ; absolute = A * Hn * A^-1.
        double[] a = [s, 0, cx, 0, s, cy, 0, 0, 1];
        double[] ai = [1 / s, 0, -cx / s, 0, 1 / s, -cy / s, 0, 0, 1];
        return Mul(Mul(a, hn), ai);
    }

    public static double[] Mul(double[] a, double[] b)
    {
        var o = new double[9];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
                o[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        }

        return o;
    }

    public static (double X, double Y) Apply(double[] h, double x, double y)
    {
        double w = h[6] * x + h[7] * y + h[8];
        return ((h[0] * x + h[1] * y + h[2]) / w, (h[3] * x + h[4] * y + h[5]) / w);
    }

    /// <summary>The plate's quad one frame later if it moved like the ring did.</summary>
    public static Quad PredictCorners(Quad q, double[] h)
    {
        (double X, double Y) P(int c) => Apply(h, q.Corner(c).X, q.Corner(c).Y);
        return Quad.From(q.T, P(0), P(1), P(2), P(3));
    }

    private static double[]? Solve(double[] a, double[] b, int[] active)
    {
        int m = active.Length;
        var w = new double[m, m + 1];
        for (int r = 0; r < m; r++)
        {
            for (int c = 0; c < m; c++)
                w[r, c] = a[active[r] * 8 + active[c]];
            w[r, m] = b[active[r]];
        }

        for (int col = 0; col < m; col++)
        {
            int piv = col;
            for (int r = col + 1; r < m; r++)
            {
                if (Math.Abs(w[r, col]) > Math.Abs(w[piv, col]))
                    piv = r;
            }

            if (Math.Abs(w[piv, col]) < 1e-14)
                return null;
            for (int k = 0; k <= m; k++)
                (w[col, k], w[piv, k]) = (w[piv, k], w[col, k]);
            for (int r = 0; r < m; r++)
            {
                if (r == col)
                    continue;
                double f = w[r, col] / w[col, col];
                for (int k = col; k <= m; k++)
                    w[r, k] -= f * w[col, k];
            }
        }

        var x = new double[8];
        for (int r = 0; r < m; r++)
            x[active[r]] = w[r, m] / w[r, r];
        return x;
    }

    private static double[]? Invert(double[] a, int[] active)
    {
        int m = active.Length;
        var w = new double[m, 2 * m];
        for (int r = 0; r < m; r++)
        {
            for (int c = 0; c < m; c++)
                w[r, c] = a[active[r] * 8 + active[c]];
            w[r, m + r] = 1;
        }

        for (int col = 0; col < m; col++)
        {
            int piv = col;
            for (int r = col + 1; r < m; r++)
            {
                if (Math.Abs(w[r, col]) > Math.Abs(w[piv, col]))
                    piv = r;
            }

            if (Math.Abs(w[piv, col]) < 1e-14)
                return null;
            for (int k = 0; k < 2 * m; k++)
                (w[col, k], w[piv, k]) = (w[piv, k], w[col, k]);
            double pv = w[col, col];
            for (int k = 0; k < 2 * m; k++)
                w[col, k] /= pv;
            for (int r = 0; r < m; r++)
            {
                if (r == col)
                    continue;
                double f = w[r, col];
                for (int k = 0; k < 2 * m; k++)
                    w[r, k] -= f * w[col, k];
            }
        }

        var inv = new double[64];
        for (int r = 0; r < m; r++)
        {
            for (int c = 0; c < m; c++)
                inv[active[r] * 8 + active[c]] = w[r, m + c];
        }

        return inv;
    }
}
