#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates.Motion;

/// <summary>Position series of a plate quad (per frame, null = not measured) and the arithmetic the T-014 comparisons are made of. Units: plate source px.</summary>
internal static class Chan
{
    public static Quad?[] Of(IReadOnlyList<Quad> track) => track.Select(q => (Quad?)q).ToArray();

    public static Quad?[] Of(PlateMeasurement m) => m.Quad.ToArray();

    /// <summary>Keeps only frames in [from, to] (inclusive).</summary>
    public static Quad?[] Window(Quad?[] q, int from, int to) => q.Select((v, i) => i >= from && i <= to ? v : null).ToArray();

    /// <summary>Corner-wise difference a - b where both exist.</summary>
    public static Quad?[] Minus(Quad?[] a, Quad?[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        var o = new Quad?[n];
        for (int i = 0; i < n; i++)
        {
            if (a[i] is { } x && b[i] is { } y)
                o[i] = new Quad(x.T, x.X0 - y.X0, x.Y0 - y.Y0, x.X1 - y.X1, x.Y1 - y.Y1, x.X2 - y.X2, x.Y2 - y.Y2, x.X3 - y.X3, x.Y3 - y.Y3);
        }

        return o;
    }

    /// <summary>Mask: both exist.</summary>
    public static Quad?[] Both(Quad?[] a, Quad?[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        var o = new Quad?[n];
        for (int i = 0; i < n; i++)
            o[i] = a[i] is not null && b[i] is not null ? a[i] : null;
        return o;
    }

    /// <summary>The eight coordinate series (corner-major: c0x, c0y, c1x, ...), null where the quad is missing.</summary>
    public static double?[][] Coords(Quad?[] q)
    {
        var o = new double?[8][];
        for (int k = 0; k < 8; k++)
        {
            o[k] = new double?[q.Length];
            for (int i = 0; i < q.Length; i++)
            {
                if (q[i] is { } v)
                {
                    (double x, double y) = v.Corner(k / 2);
                    o[k][i] = k % 2 == 0 ? x : y;
                }
            }
        }

        return o;
    }

    /// <summary>Frame-to-frame change per coordinate (velocity, px/frame); entry i = q[i+1] - q[i], null unless both exist.</summary>
    public static double?[][] Velocity(Quad?[] q)
    {
        double?[][] c = Coords(q);
        var o = new double?[8][];
        for (int k = 0; k < 8; k++)
        {
            o[k] = new double?[Math.Max(0, q.Length - 1)];
            for (int i = 0; i + 1 < q.Length; i++)
            {
                if (c[k][i] is { } a && c[k][i + 1] is { } b)
                    o[k][i] = b - a;
            }
        }

        return o;
    }

    /// <summary>x minus its centred moving average over <paramref name="width"/> samples; null unless the whole window exists.</summary>
    public static double?[] HighPass(double?[] x, int width)
    {
        int half = width / 2;
        var o = new double?[x.Length];
        for (int i = half; i < x.Length - half; i++)
        {
            double s = 0;
            bool full = true;
            for (int j = i - half; j <= i + half && full; j++)
            {
                if (x[j] is { } v)
                    s += v;
                else
                    full = false;
            }

            if (full && x[i] is { } xi)
                o[i] = xi - s / width;
        }

        return o;
    }

    public static double?[][] HighPass(double?[][] x, int width) => x.Select(s => HighPass(s, width)).ToArray();
}

/// <summary>Pairwise statistics of two velocity (or high-passed velocity) channels: how big each is, how big their difference is, and how one maps onto the other.</summary>
internal sealed record PairStats(int N, double RmsA, double RmsB, double RmsDiff, double SlopeAOnB, double Corr)
{
    public static readonly PairStats Empty = new(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

    /// <summary>All samples (the eight coordinates) where both exist; RMS is per coordinate sample (corner x or y), so a 2-D corner vector counts as two samples.</summary>
    public static PairStats Of(double?[][] a, double?[][] b)
    {
        double saa = 0, sbb = 0, sab = 0, sdd = 0;
        int n = 0;
        for (int k = 0; k < Math.Min(a.Length, b.Length); k++)
        {
            int len = Math.Min(a[k].Length, b[k].Length);
            for (int i = 0; i < len; i++)
            {
                if (a[k][i] is { } x && b[k][i] is { } y)
                {
                    saa += x * x;
                    sbb += y * y;
                    sab += x * y;
                    sdd += (x - y) * (x - y);
                    n++;
                }
            }
        }

        if (n == 0)
            return Empty;
        return new PairStats(n, Math.Sqrt(saa / n), Math.Sqrt(sbb / n), Math.Sqrt(sdd / n), sbb > 0 ? sab / sbb : double.NaN, saa > 0 && sbb > 0 ? sab / Math.Sqrt(saa * sbb) : double.NaN);
    }
}

/// <summary>Pooled cross-spectra of two position channels (Welch: 2 s segments, 50 % overlap, quadratic detrend, Hann), per frequency bin.</summary>
internal sealed record CrossSpectrum(double[] Sxx, double[] Syy, double[] SxyRe, double[] SxyIm, int Segments, int Seg, double Fps)
{
    public double Hz(int bin) => bin * Fps / Seg;

    /// <summary>Variance (px²) of x in bins whose centre frequency lies in [lo, hi) Hz.</summary>
    public double Power(double[] s, double lo, double hi)
    {
        double sum = 0;
        for (int b = 1; b < s.Length; b++)
        {
            if (Hz(b) >= lo && Hz(b) < hi)
                sum += s[b];
        }

        return sum;
    }

    public double RmsX(double lo, double hi) => Math.Sqrt(Power(Sxx, lo, hi));

    public double RmsY(double lo, double hi) => Math.Sqrt(Power(Syy, lo, hi));

    /// <summary>Band-averaged magnitude-squared coherence.</summary>
    public double Coherence(double lo, double hi)
    {
        double xy2 = Math.Pow(Power(SxyRe, lo, hi), 2) + Math.Pow(Power(SxyIm, lo, hi), 2);
        double d = Power(Sxx, lo, hi) * Power(Syy, lo, hi);
        return d > 0 ? xy2 / d : double.NaN;
    }

    /// <summary>Real part of the cross-variance (px²): the part of x and y that moves together in phase.</summary>
    public double SharedPower(double lo, double hi) => Power(SxyRe, lo, hi);

    /// <summary>Slope of x on y (x = s y + noise) in the band: Re Sxy / Syy.</summary>
    public double Slope(double lo, double hi)
    {
        double d = Power(Syy, lo, hi);
        return d > 0 ? Power(SxyRe, lo, hi) / d : double.NaN;
    }

    /// <summary>
    /// With <paramref name="whiten"/> (default) the spectra are taken of the frame-to-frame differences and divided by the difference filter's power response
    /// 4 sin^2(pi b / seg) afterwards: the position spectrum is unchanged in theory, but the large slow motion of a push or a travel (a leak of the Hann window
    /// into the next bins otherwise) is tamed by 20-30 dB before it can leak.
    /// </summary>
    public static CrossSpectrum? Compute(double?[][] x, double?[][] y, double fps = PromoInputs.Fps, int seg = 48, bool whiten = true)
    {
        if (whiten)
        {
            double?[][] Diff(double?[][] a) => a.Select(c => Enumerable.Range(0, Math.Max(0, c.Length - 1)).Select(i => c[i] is { } u && c[i + 1] is { } v ? (double?)(v - u) : null).ToArray()).ToArray();
            CrossSpectrum? d = Compute(Diff(x), Diff(y), fps, seg, false);
            if (d is null)
                return null;
            for (int b = 1; b < d.Sxx.Length; b++)
            {
                double h2 = 4 * Math.Pow(Math.Sin(Math.PI * b / seg), 2);
                d.Sxx[b] /= h2;
                d.Syy[b] /= h2;
                d.SxyRe[b] /= h2;
                d.SxyIm[b] /= h2;
            }

            return d;
        }

        int bins = seg / 2 + 1;
        var sxx = new double[bins];
        var syy = new double[bins];
        var re = new double[bins];
        var im = new double[bins];
        int segments = 0;
        var w = new double[seg];
        double sw2 = 0;
        for (int i = 0; i < seg; i++)
        {
            w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / seg);
            sw2 += w[i] * w[i];
        }

        var cosT = new double[bins, seg];
        var sinT = new double[bins, seg];
        for (int b = 0; b < bins; b++)
        {
            for (int i = 0; i < seg; i++)
            {
                cosT[b, i] = Math.Cos(2 * Math.PI * b * i / seg);
                sinT[b, i] = Math.Sin(2 * Math.PI * b * i / seg);
            }
        }

        for (int k = 0; k < Math.Min(x.Length, y.Length); k++)
        {
            int len = Math.Min(x[k].Length, y[k].Length);
            for (int start = 0; start + seg <= len; start += seg / 2)
            {
                bool ok = true;
                var xs = new double[seg];
                var ys = new double[seg];
                for (int i = 0; i < seg && ok; i++)
                {
                    if (x[k][start + i] is { } a && y[k][start + i] is { } b2)
                    {
                        xs[i] = a;
                        ys[i] = b2;
                    }
                    else
                    {
                        ok = false;
                    }
                }

                if (!ok)
                    continue;
                xs = PromoSeries.DetrendPoly(xs, 1);
                ys = PromoSeries.DetrendPoly(ys, 1);
                segments++;
                for (int b = 1; b < bins; b++)
                {
                    double xr = 0, xi = 0, yr = 0, yi = 0;
                    for (int i = 0; i < seg; i++)
                    {
                        xr += w[i] * xs[i] * cosT[b, i];
                        xi -= w[i] * xs[i] * sinT[b, i];
                        yr += w[i] * ys[i] * cosT[b, i];
                        yi -= w[i] * ys[i] * sinT[b, i];
                    }

                    double scale = (b == seg / 2 ? 1.0 : 2.0) / (seg * sw2);
                    sxx[b] += scale * (xr * xr + xi * xi);
                    syy[b] += scale * (yr * yr + yi * yi);
                    re[b] += scale * (xr * yr + xi * yi);
                    im[b] += scale * (xi * yr - xr * yi);
                }
            }
        }

        if (segments == 0)
            return null;
        for (int b = 0; b < bins; b++)
        {
            sxx[b] /= segments;
            syy[b] /= segments;
            re[b] /= segments;
            im[b] /= segments;
        }

        // `segments` counts (coordinate, segment) pairs: the spectra are means over them.
        return new CrossSpectrum(sxx, syy, re, im, segments, seg, fps);
    }
}
