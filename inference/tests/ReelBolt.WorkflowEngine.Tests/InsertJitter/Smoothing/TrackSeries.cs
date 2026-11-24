#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

/// <summary>
/// A temporal smoother for an assembled quad track (initiative insert-tracking-smoothness, T-004).
/// The input is the S3 track — after outlier rejection and label uprighting, exactly what
/// <c>ChromaQuadTracker.Smooth</c> receives. Analysis is offline, so the whole track is known and
/// non-causal smoothers are fine. Everything is in SOURCE pixels; time is <c>Quad.T</c> seconds.
/// </summary>
public interface ITrackSmoother
{
    string Name { get; }

    /// <summary>
    /// <paramref name="noisePx"/> is the outlier pass' measurement-noise estimate (median over detections of the
    /// largest corner residual against a local line fit), converted to SOURCE pixels. It is 0 on clean tracks.
    /// </summary>
    IReadOnlyList<Quad> Smooth(IReadOnlyList<Quad> track, double fps, double noisePx);
}

/// <summary>Coordinate-series helpers: a quad is eight series (x0,y0,x1,y1,x2,y2,x3,y3 = TL,TR,BL,BR).</summary>
internal static class TrackSeries
{
    public const int Coords = 8;

    public static double[][] ToSeries(IReadOnlyList<Quad> q)
    {
        var s = new double[Coords][];
        for (int c = 0; c < Coords; c++)
            s[c] = new double[q.Count];
        for (int i = 0; i < q.Count; i++)
        {
            s[0][i] = q[i].X0; s[1][i] = q[i].Y0; s[2][i] = q[i].X1; s[3][i] = q[i].Y1;
            s[4][i] = q[i].X2; s[5][i] = q[i].Y2; s[6][i] = q[i].X3; s[7][i] = q[i].Y3;
        }

        return s;
    }

    /// <summary>Sample times in frame units (<c>T * fps</c>), so a step of 1 is one source frame.</summary>
    public static double[] Times(IReadOnlyList<Quad> q, double fps)
    {
        var t = new double[q.Count];
        for (int i = 0; i < q.Count; i++)
            t[i] = q[i].T * fps;
        return t;
    }

    public static List<Quad> Build(IReadOnlyList<Quad> template, double[][] s)
    {
        var o = new List<Quad>(template.Count);
        for (int i = 0; i < template.Count; i++)
            o.Add(new Quad(template[i].T, s[0][i], s[1][i], s[2][i], s[3][i], s[4][i], s[5][i], s[6][i], s[7][i]));
        return o;
    }

    public static double[][] Clone(double[][] s) => s.Select(a => (double[])a.Clone()).ToArray();

    public static double Median(IEnumerable<double> values)
    {
        double[] a = values.OrderBy(v => v).ToArray();
        if (a.Length == 0)
            return 0;
        int m = a.Length / 2;
        return a.Length % 2 == 1 ? a[m] : (a[m - 1] + a[m]) / 2;
    }

    /// <summary>
    /// The track's own white-noise sigma per coordinate (source px): the median over the eight series of
    /// 1.4826 * MAD(third difference) / sqrt(20). A polynomial of degree 2 has a zero third difference, so a
    /// smooth path contributes nothing; iid noise of sigma s gives a third-difference variance of 20 s^2; the MAD
    /// keeps a kink or a bridged gap out of the estimate. Correlated wobble (chroma subsampling) is
    /// under-estimated by construction. Needs consecutive frames: with gaps it is only approximate.
    /// </summary>
    public static double SigmaThirdDiff(double[][] s)
    {
        var per = new List<double>();
        foreach (double[] x in s)
        {
            if (x.Length < 8)
                continue;
            var d = new double[x.Length - 3];
            for (int i = 0; i < d.Length; i++)
                d[i] = x[i + 3] - 3 * x[i + 2] + 3 * x[i + 1] - x[i];
            double med = Median(d);
            double mad = Median(d.Select(v => Math.Abs(v - med)));
            per.Add(1.4826 * mad / Math.Sqrt(20.0));
        }

        return per.Count == 0 ? 0 : Median(per);
    }

    /// <summary>The noise unit the adaptive smoothers use: the larger of the outlier pass' estimate, the track's own, and a floor.</summary>
    public static double Sigma(double[][] s, double noisePx, double floor) =>
        new SigmaPolicy(floor).Resolve(s, noisePx);
}

/// <summary>
/// How an adaptive smoother turns what it knows into ONE per-coordinate error scale (source px).
/// Measured on the T-002 scenes the outlier pass' number (a max-over-corners distance) is 2-14x the true white sigma, the track's own
/// third-difference sigma sees only uncorrelated noise (it is blind to the correlated wobble that 4:2:0 sampling puts on a
/// moving plate), so a calibrated PRIOR for the tracker's accuracy is the third option: <c>Prior</c> is that accuracy in source px
/// (the callers derive it as a fraction of one detection-grid pixel).
/// </summary>
internal sealed record SigmaPolicy(double Floor, double Prior = 0, bool UseGivenNoise = true, bool UseOwn = true)
{
    public double Resolve(double[][] s, double noisePx)
    {
        double v = Math.Max(Floor, Prior);
        if (UseGivenNoise)
            v = Math.Max(v, noisePx);
        if (UseOwn)
            v = Math.Max(v, TrackSeries.SigmaThirdDiff(s));
        return v;
    }

    public string Label => $"sigma[floor {Floor:0.##}{(Prior > 0 ? $" prior {Prior:0.###}" : "")}{(UseGivenNoise ? " +noise" : "")}{(UseOwn ? " +own" : "")}]";
}

/// <summary>Weighted least-squares polynomial fits on irregular time stamps, shared by the local-polynomial smoothers.</summary>
internal static class PolyFit
{
    /// <summary>
    /// Fits y(t) = sum_k c_k (t - tc)^k over samples <c>from..to</c> with weights <paramref name="w"/> (indexed by sample)
    /// for each of the series in <paramref name="y"/>; returns false when the normal equations are singular
    /// (fewer than degree+1 effective samples). <paramref name="coef"/>[series][k].
    /// </summary>
    public static bool Fit(double[] t, double[][] y, int from, int to, double tc, double[] w, int degree, double[][] coef)
    {
        int m = degree + 1;
        Span<double> a = stackalloc double[m * m];
        Span<double> pw = stackalloc double[2 * m];
        a.Clear();
        for (int j = from; j <= to; j++)
        {
            double wj = w[j];
            if (wj <= 0)
                continue;
            double u = t[j] - tc, p = 1;
            for (int k = 0; k < 2 * m - 1; k++)
            {
                pw[k] = p;
                p *= u;
            }

            for (int r = 0; r < m; r++)
            {
                for (int c = 0; c < m; c++)
                    a[r * m + c] += wj * pw[r + c];
            }
        }

        // Invert via Gauss-Jordan once; apply to every series' right-hand side.
        Span<double> inv = stackalloc double[m * m];
        if (!Invert(a, inv, m))
            return false;
        Span<double> b = stackalloc double[m];
        for (int s = 0; s < y.Length; s++)
        {
            b.Clear();
            for (int j = from; j <= to; j++)
            {
                double wj = w[j];
                if (wj <= 0)
                    continue;
                double u = t[j] - tc, p = 1;
                for (int k = 0; k < m; k++)
                {
                    b[k] += wj * p * y[s][j];
                    p *= u;
                }
            }

            for (int k = 0; k < m; k++)
            {
                double v = 0;
                for (int c = 0; c < m; c++)
                    v += inv[k * m + c] * b[c];
                coef[s][k] = v;
            }
        }

        return true;
    }

    private static bool Invert(ReadOnlySpan<double> a, Span<double> inv, int m)
    {
        Span<double> w = stackalloc double[m * 2 * m];
        for (int r = 0; r < m; r++)
        {
            for (int c = 0; c < m; c++)
            {
                w[r * 2 * m + c] = a[r * m + c];
                w[r * 2 * m + m + c] = r == c ? 1 : 0;
            }
        }

        for (int col = 0; col < m; col++)
        {
            int piv = col;
            for (int r = col + 1; r < m; r++)
            {
                if (Math.Abs(w[r * 2 * m + col]) > Math.Abs(w[piv * 2 * m + col]))
                    piv = r;
            }

            double pv = w[piv * 2 * m + col];
            if (Math.Abs(pv) < 1e-10)
                return false;
            if (piv != col)
            {
                for (int c = 0; c < 2 * m; c++)
                    (w[col * 2 * m + c], w[piv * 2 * m + c]) = (w[piv * 2 * m + c], w[col * 2 * m + c]);
            }

            for (int c = 0; c < 2 * m; c++)
                w[col * 2 * m + c] /= pv;
            for (int r = 0; r < m; r++)
            {
                if (r == col)
                    continue;
                double f = w[r * 2 * m + col];
                if (f == 0)
                    continue;
                for (int c = 0; c < 2 * m; c++)
                    w[r * 2 * m + c] -= f * w[col * 2 * m + c];
            }
        }

        for (int r = 0; r < m; r++)
        {
            for (int c = 0; c < m; c++)
                inv[r * m + c] = w[r * 2 * m + m + c];
        }

        return true;
    }
}
