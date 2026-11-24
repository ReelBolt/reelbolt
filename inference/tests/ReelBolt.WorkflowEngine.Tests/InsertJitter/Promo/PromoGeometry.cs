#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo;

/// <summary>
/// A 3x3 projective map. <see cref="FromUnitSquare"/> sends (0,0), (1,0), (0,1), (1,1) to a quad's TL, TR, BL, BR (same convention
/// as <see cref="Quad.ToMap"/>), solved as an 8x8 linear system so it can be inverted (the screen's own coordinates of a card corner).
/// </summary>
internal readonly struct Homography
{
    private readonly double[] _m; // row major, m[8] == 1 for a map built from a quad

    private Homography(double[] m) => _m = m;

    public static Homography FromUnitSquare(Quad q)
    {
        (double X, double Y)[] dst = [q.Corner(0), q.Corner(1), q.Corner(2), q.Corner(3)];
        (double U, double V)[] src = [(0, 0), (1, 0), (0, 1), (1, 1)];
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            (double u, double v) = src[i];
            (double x, double y) = dst[i];
            // x = (h0 u + h1 v + h2) / (h6 u + h7 v + 1), y = (h3 u + h4 v + h5) / (h6 u + h7 v + 1)
            a[2 * i, 0] = u; a[2 * i, 1] = v; a[2 * i, 2] = 1; a[2 * i, 6] = -u * x; a[2 * i, 7] = -v * x; a[2 * i, 8] = x;
            a[2 * i + 1, 3] = u; a[2 * i + 1, 4] = v; a[2 * i + 1, 5] = 1; a[2 * i + 1, 6] = -u * y; a[2 * i + 1, 7] = -v * y; a[2 * i + 1, 8] = y;
        }

        double[] h = Solve(a);
        return new Homography([h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1.0]);
    }

    public (double X, double Y) Apply(double u, double v)
    {
        double w = _m[6] * u + _m[7] * v + _m[8];
        return ((_m[0] * u + _m[1] * v + _m[2]) / w, (_m[3] * u + _m[4] * v + _m[5]) / w);
    }

    public Homography Inverse()
    {
        double a = _m[0], b = _m[1], c = _m[2], d = _m[3], e = _m[4], f = _m[5], g = _m[6], h = _m[7], i = _m[8];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Math.Abs(det) < 1e-300)
            throw new InvalidOperationException("singular homography");
        double[] r =
        [
            (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
            (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
            (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det
        ];
        return new Homography(r);
    }

    private static double[] Solve(double[,] a)
    {
        int n = 8;
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col]))
                    piv = r;
            }

            if (Math.Abs(a[piv, col]) < 1e-14)
                throw new InvalidOperationException("degenerate quad");
            if (piv != col)
            {
                for (int k = 0; k <= n; k++)
                    (a[col, k], a[piv, k]) = (a[piv, k], a[col, k]);
            }

            for (int r = 0; r < n; r++)
            {
                if (r == col)
                    continue;
                double f = a[r, col] / a[col, col];
                for (int k = col; k <= n; k++)
                    a[r, k] -= f * a[col, k];
            }
        }

        var x = new double[n];
        for (int i = 0; i < n; i++)
            x[i] = a[i, n] / a[i, i];
        return x;
    }
}

/// <summary>Small numeric helpers shared by the promo measurements: runs of valid frames, detrending, a periodogram.</summary>
internal static class PromoSeries
{
    /// <summary>Maximal runs [start, endExclusive) of consecutive true entries.</summary>
    public static List<(int Start, int End)> Runs(IReadOnlyList<bool> valid)
    {
        var runs = new List<(int, int)>();
        int s = -1;
        for (int i = 0; i <= valid.Count; i++)
        {
            bool v = i < valid.Count && valid[i];
            if (v && s < 0)
                s = i;
            else if (!v && s >= 0)
            {
                runs.Add((s, i));
                s = -1;
            }
        }

        return runs;
    }

    /// <summary>x minus its centred moving average over +-<paramref name="half"/> samples (window truncated at the ends).</summary>
    public static double[] HighPass(double[] x, int half)
    {
        var o = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            int a = Math.Max(0, i - half), b = Math.Min(x.Length - 1, i + half);
            double s = 0;
            for (int j = a; j <= b; j++)
                s += x[j];
            o[i] = x[i] - s / (b - a + 1);
        }

        return o;
    }

    /// <summary>x minus its least-squares polynomial of the given degree in the sample index.</summary>
    public static double[] DetrendPoly(double[] x, int degree)
    {
        int n = x.Length;
        if (n <= degree + 1)
            return x.Select(v => v - x.Average()).ToArray();
        int m = degree + 1;
        var ata = new double[m, m + 1];
        for (int i = 0; i < n; i++)
        {
            double t = (i - (n - 1) / 2.0) / n;
            var p = new double[m];
            p[0] = 1;
            for (int k = 1; k < m; k++)
                p[k] = p[k - 1] * t;
            for (int r = 0; r < m; r++)
            {
                for (int c = 0; c < m; c++)
                    ata[r, c] += p[r] * p[c];
                ata[r, m] += p[r] * x[i];
            }
        }

        for (int col = 0; col < m; col++)
        {
            int piv = col;
            for (int r = col + 1; r < m; r++)
            {
                if (Math.Abs(ata[r, col]) > Math.Abs(ata[piv, col]))
                    piv = r;
            }

            for (int k = 0; k <= m; k++)
                (ata[col, k], ata[piv, k]) = (ata[piv, k], ata[col, k]);
            for (int r = 0; r < m; r++)
            {
                if (r == col)
                    continue;
                double f = ata[r, col] / ata[col, col];
                for (int k = col; k <= m; k++)
                    ata[r, k] -= f * ata[col, k];
            }
        }

        var coef = new double[m];
        for (int i = 0; i < m; i++)
            coef[i] = ata[i, m] / ata[i, i];
        var o = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = (i - (n - 1) / 2.0) / n, fit = 0, pw = 1;
            for (int k = 0; k < m; k++)
            {
                fit += coef[k] * pw;
                pw *= t;
            }

            o[i] = x[i] - fit;
        }

        return o;
    }

    /// <summary>
    /// Power spectrum of the summed series (Hann window, direct DFT, mean removed). Returns power per bin k = 1..n/2 (cycles per n samples);
    /// the period of bin k is n / k samples.
    /// </summary>
    public static double[] Periodogram(IReadOnlyList<double[]> series)
    {
        int n = series[0].Length;
        var p = new double[n / 2 + 1];
        foreach (double[] s in series)
        {
            double mean = s.Average();
            var w = new double[n];
            for (int i = 0; i < n; i++)
                w[i] = (s[i] - mean) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / n));
            for (int k = 1; k <= n / 2; k++)
            {
                double re = 0, im = 0;
                for (int i = 0; i < n; i++)
                {
                    double ang = 2 * Math.PI * k * i / n;
                    re += w[i] * Math.Cos(ang);
                    im -= w[i] * Math.Sin(ang);
                }

                p[k] += (re * re + im * im) / n;
            }
        }

        return p;
    }

    public static double Rms(IEnumerable<double> v)
    {
        double[] a = v.ToArray();
        return a.Length == 0 ? double.NaN : Math.Sqrt(a.Select(x => x * x).Average());
    }
}
