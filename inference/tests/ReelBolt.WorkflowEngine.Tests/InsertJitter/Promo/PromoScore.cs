#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo;

/// <summary>
/// Scoring of the promo measurements with the SAME definitions as <see cref="JitterMetrics"/>. Per-edge and per-side series (one scalar per sample
/// point) are fed through <see cref="JitterMetrics.Compute"/> as pseudo-quads: four sample points along the edge are stored as the x of the four
/// corners (y = 0), so SlipRms / SlipP95 / HfRms / ErrMean / ErrMax mean exactly what they mean for corners (pooled over the four points).
/// </summary>
internal static class PromoScore
{
    public const double Fps = PromoInputs.Fps;

    /// <summary>Fractions of an edge (of the trimmed measuring range) at which the scalar series are sampled.</summary>
    public static readonly double[] Fractions = [0.12, 0.37, 0.63, 0.88];

    public sealed record Row(string Label, int Frames, JitterStats? Stats, double OffsetMean, double HfMax = double.NaN, double SlipMax = double.NaN, int SlipMaxFrame = -1)
    {
        public static Row Empty(string label) => new(label, 0, null, double.NaN);
    }

    // ---------------------------------------------------------------- geometry of edges

    /// <summary>Corner index pairs of the quad's edges in the order top, right, bottom, left (clockwise from TL).</summary>
    public static readonly (int From, int To)[] EdgeCorners = [(0, 1), (1, 3), (3, 2), (2, 0)];

    private static (double X, double Y) Inward(Quad q, int edge, out (double X, double Y) origin)
    {
        (double X, double Y) a = q.Corner(EdgeCorners[edge].From), b = q.Corner(EdgeCorners[edge].To);
        double cx = (q.X0 + q.X1 + q.X2 + q.X3) / 4, cy = (q.Y0 + q.Y1 + q.Y2 + q.Y3) / 4;
        double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        double nx = -(b.Y - a.Y) / len, ny = (b.X - a.X) / len;
        if ((cx - a.X) * nx + (cy - a.Y) * ny < 0)
        {
            nx = -nx;
            ny = -ny;
        }

        origin = a;
        return (nx, ny);
    }

    /// <summary>Points of a measured edge at <see cref="Fractions"/>.</summary>
    public static (double X, double Y)[] Points(PromoEdge e) =>
        Fractions.Select(f => (e.A.X + f * (e.B.X - e.A.X), e.A.Y + f * (e.B.Y - e.A.Y))).ToArray();

    /// <summary>Points of a quad's edge at <see cref="Fractions"/>.</summary>
    public static (double X, double Y)[] Points(Quad q, int edge)
    {
        (double X, double Y) a = q.Corner(EdgeCorners[edge].From), b = q.Corner(EdgeCorners[edge].To);
        return Fractions.Select(f => (a.X + f * (b.X - a.X), a.Y + f * (b.Y - a.Y))).ToArray();
    }

    /// <summary>
    /// How far, along the inward normal, the measured edge lies INSIDE the track's edge line, at the four sample points of the measured edge
    /// (px of whatever space the inputs are in). Positive = the measured edge is further in than the track's.
    /// </summary>
    public static double[] MeasuredInsideTrack(Quad track, int edge, PromoEdge measured)
    {
        (double nx, double ny) = Inward(track, edge, out (double X, double Y) o);
        return Points(measured).Select(p => (p.X - o.X) * nx + (p.Y - o.Y) * ny).ToArray();
    }

    /// <summary>
    /// How far, along the inward normal of the SCREEN's measured edge, the card's edge lies inside it, at the four sample points of the card's edge
    /// (positive: the card edge is inside the screen edge, as it should be). Pure function of two measured/predicted lines.
    /// </summary>
    public static double[] GapInside((double X, double Y)[] cardPoints, PromoEdge screenLine, Quad screenQuadForOrientation, int edge)
    {
        double dx = screenLine.B.X - screenLine.A.X, dy = screenLine.B.Y - screenLine.A.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double nx = -dy / len, ny = dx / len;
        double cx = (screenQuadForOrientation.X0 + screenQuadForOrientation.X1 + screenQuadForOrientation.X2 + screenQuadForOrientation.X3) / 4;
        double cy = (screenQuadForOrientation.Y0 + screenQuadForOrientation.Y1 + screenQuadForOrientation.Y2 + screenQuadForOrientation.Y3) / 4;
        if ((cx - screenLine.A.X) * nx + (cy - screenLine.A.Y) * ny < 0)
        {
            nx = -nx;
            ny = -ny;
        }

        return cardPoints.Select(p => (p.X - screenLine.A.X) * nx + (p.Y - screenLine.A.Y) * ny).ToArray();
    }

    // ---------------------------------------------------------------- generic scoring of scalar series

    /// <summary>
    /// Scores per-frame four-point scalar series (null = frame not scored). Track = the series; truth = <paramref name="reference"/> (per point), or its
    /// per-point mean over the scored frames when null. The constant part is returned as <c>OffsetMean</c> (mean signed value of the series minus the reference).
    /// </summary>
    public static Row Scalar(string label, IReadOnlyList<double[]?> series, double[]? reference = null)
    {
        var track = new List<Quad>();
        var frames = new List<int>();
        for (int i = 0; i < series.Count; i++)
        {
            if (series[i] is not null)
                frames.Add(i);
        }

        if (frames.Count < 2)
            return Row.Empty(label);
        double[] mean = reference ?? Enumerable.Range(0, 4).Select(k => frames.Average(i => series[i]![k])).ToArray();
        var truth = new List<Quad>();
        foreach (int i in frames)
        {
            double[] s = series[i]!;
            track.Add(new Quad(i / Fps, s[0], 0, s[1], 0, s[2], 0, s[3], 0));
            truth.Add(new Quad(i / Fps, mean[0], 0, mean[1], 0, mean[2], 0, mean[3], 0));
        }

        JitterStats st = JitterMetrics.Compute(track, truth, Fps);
        double off = frames.Average(i => Enumerable.Range(0, 4).Average(k => series[i]![k]));
        double slipMax = 0;
        int slipMaxFrame = -1;
        for (int j = 1; j < frames.Count; j++)
        {
            if (frames[j] != frames[j - 1] + 1)
                continue;
            for (int k = 0; k < 4; k++)
            {
                double d = Math.Abs(series[frames[j]]![k] - series[frames[j - 1]]![k]);
                if (d > slipMax)
                {
                    slipMax = d;
                    slipMaxFrame = frames[j];
                }
            }
        }

        return new Row(label, frames.Count, st, off, HfMaxOf(series), slipMax, slipMaxFrame);
    }

    /// <summary>Largest |x - centred 1 s mean of x| over frames with a full window (the same window <see cref="JitterMetrics"/> uses for HfRms).</summary>
    private static double HfMaxOf(IReadOnlyList<double[]?> series)
    {
        const int half = 12;
        double best = double.NaN;
        for (int i = 0; i < series.Count; i++)
        {
            bool full = true;
            for (int m = i - half; m <= i + half && full; m++)
                full = m >= 0 && m < series.Count && series[m] is not null;
            if (!full)
                continue;
            for (int k = 0; k < 4; k++)
            {
                double mean = 0;
                for (int m = i - half; m <= i + half; m++)
                    mean += series[m]![k];
                double dev = Math.Abs(series[i]![k] - mean / (2 * half + 1));
                best = double.IsNaN(best) ? dev : Math.Max(best, dev);
            }
        }

        return best;
    }

    /// <summary>Scores quad tracks against quads (both per frame, null = not scored), through <see cref="JitterMetrics.Compute"/>.</summary>
    public static Row Quads(string label, IReadOnlyList<Quad?> track, IReadOnlyList<Quad?> truth)
    {
        var t = new List<Quad>();
        var g = new List<Quad>();
        for (int i = 0; i < Math.Min(track.Count, truth.Count); i++)
        {
            if (track[i] is { } a && truth[i] is { } b)
            {
                t.Add(a with { T = i / Fps });
                g.Add(b with { T = i / Fps });
            }
        }

        if (t.Count < 2)
            return Row.Empty(label);
        JitterStats st = JitterMetrics.Compute(t, g, Fps);
        // Constant part: mean error vector over corners and frames, magnitude.
        double mx = 0, my = 0;
        for (int i = 0; i < t.Count; i++)
        {
            for (int c = 0; c < 4; c++)
            {
                mx += t[i].Corner(c).X - g[i].Corner(c).X;
                my += t[i].Corner(c).Y - g[i].Corner(c).Y;
            }
        }

        mx /= 4 * t.Count;
        my /= 4 * t.Count;
        double slipMax = 0;
        for (int j = 1; j < t.Count; j++)
        {
            if (Math.Abs(t[j].T - t[j - 1].T - 1 / Fps) > 1e-6)
                continue;
            for (int c = 0; c < 4; c++)
            {
                double dx = t[j].Corner(c).X - g[j].Corner(c).X - (t[j - 1].Corner(c).X - g[j - 1].Corner(c).X);
                double dy = t[j].Corner(c).Y - g[j].Corner(c).Y - (t[j - 1].Corner(c).Y - g[j - 1].Corner(c).Y);
                slipMax = Math.Max(slipMax, Math.Sqrt(dx * dx + dy * dy));
            }
        }

        return new Row(label, t.Count, st, Math.Sqrt(mx * mx + my * my), double.NaN, slipMax);
    }

    /// <summary>The measured quad's own floor: pooled leave-one-out quadratic ResidualRms over its contiguous runs (>= 9 frames).</summary>
    public static (double Rms, double P95, int Frames) ResidualFloor(IReadOnlyList<Quad?> measured)
    {
        var all = new List<double>();
        int frames = 0;
        foreach ((int s, int e) in PromoSeries.Runs(measured.Select(q => q is not null).ToList()))
        {
            if (e - s < 9)
                continue;
            var run = new List<Quad>();
            for (int i = s; i < e; i++)
                run.Add(measured[i]!.Value with { T = i / Fps });
            JitterStats st = JitterMetrics.ComputeNoTruth(run, Fps);
            if (double.IsNaN(st.ResidualRms))
                continue;
            int n = (run.Count - 8) * 4;
            for (int i = 0; i < n; i++)
                all.Add(st.ResidualRms * st.ResidualRms);
            frames += run.Count - 8;
        }

        if (all.Count == 0)
            return (double.NaN, double.NaN, 0);
        return (Math.Sqrt(all.Average()), double.NaN, frames);
    }

    /// <summary>Per-sample residuals (px) of a scalar four-point series against its leave-one-out quadratic (+-4 frames), pooled over runs: the no-truth floor of a per-edge measurement.</summary>
    public static (double Rms, int Samples) ScalarResidual(IReadOnlyList<double[]?> series)
    {
        var sq = new List<double>();
        foreach ((int s, int e) in PromoSeries.Runs(series.Select(x => x is not null).ToList()))
        {
            if (e - s < 9)
                continue;
            for (int i = s + 4; i + 4 < e; i++)
            {
                for (int k = 0; k < 4; k++)
                {
                    double fit = LeaveOneOut(j => series[j]![k], i);
                    sq.Add((series[i]![k] - fit) * (series[i]![k] - fit));
                }
            }
        }

        return sq.Count == 0 ? (double.NaN, 0) : (Math.Sqrt(sq.Average()), sq.Count);
    }

    private static double LeaveOneOut(Func<int, double> v, int i)
    {
        double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, y0 = 0, y1 = 0, y2 = 0;
        for (int j = i - 4; j <= i + 4; j++)
        {
            if (j == i)
                continue;
            double x = j - i, y = v(j);
            s0 += 1; s1 += x; s2 += x * x; s3 += x * x * x; s4 += x * x * x * x;
            y0 += y; y1 += x * y; y2 += x * x * y;
        }

        double det = s0 * (s2 * s4 - s3 * s3) - s1 * (s1 * s4 - s3 * s2) + s2 * (s1 * s3 - s2 * s2);
        return (y0 * (s2 * s4 - s3 * s3) - s1 * (y1 * s4 - s3 * y2) + s2 * (y1 * s3 - s2 * y2)) / det;
    }

    // ---------------------------------------------------------------- spectrum

    /// <summary>Welch result: mean power per (segment, point) sample at bins 1..seg/2 (index = bin), the number of samples, and the peak.</summary>
    public sealed record Spectrum(double[] Power, int Samples, int Segments, int Seg)
    {
        public double PeriodOf(int bin) => Seg / (double)bin;

        public double HzOf(int bin) => bin * Fps / Seg;
    }

    /// <summary>The spectral bands used in the report: (name, first bin, last bin) for 64-frame segments (bin k = period 64/k frames = 0.375 k Hz).</summary>
    public static readonly (string Name, int From, int To)[] Bands =
    [
        ("64 frames (0.4 Hz)", 1, 1),
        ("32 frames (0.75 Hz)", 2, 2),
        ("21-16 frames (1.1-1.5 Hz)", 3, 4),
        ("12.8-8 frames (1.9-3 Hz)", 5, 8),
        ("7-4 frames (3.4-6 Hz)", 9, 16),
        ("3.8-2.1 frames (6.4-11.6 Hz)", 17, 31),
        ("2 frames (12 Hz, the staircase)", 32, 32)
    ];

    /// <summary>
    /// Welch periodogram (segments of <paramref name="seg"/> frames, 50 % overlap, quadratic detrend and Hann window per segment, segments only inside
    /// runs where the series exists) pooled over all four points of every series in <paramref name="channels"/>. Bin k has period seg/k frames.
    /// </summary>
    public static Spectrum? Welch(IReadOnlyList<IReadOnlyList<double[]?>> channels, int seg = 64)
    {
        var power = new double[seg / 2 + 1];
        int segments = 0;
        int samples = 0;
        foreach (IReadOnlyList<double[]?> series in channels)
        {
            foreach ((int s, int e) in PromoSeries.Runs(series.Select(x => x is not null).ToList()))
            {
                for (int start = s; start + seg <= e; start += seg / 2)
                {
                    segments++;
                    for (int k = 0; k < 4; k++)
                    {
                        double[] x = new double[seg];
                        for (int i = 0; i < seg; i++)
                            x[i] = series[start + i]![k];
                        double[] d = PromoSeries.DetrendPoly(x, 2);
                        double[] p = PromoSeries.Periodogram([d]);
                        for (int b = 1; b < p.Length; b++)
                            power[b] += p[b];
                        samples++;
                    }
                }
            }
        }

        if (segments == 0)
            return null;
        for (int b = 0; b < power.Length; b++)
            power[b] /= samples;
        return new Spectrum(power, samples, segments, seg);
    }

    public static double BandPower(Spectrum sp, (string Name, int From, int To) band) =>
        Enumerable.Range(band.From, band.To - band.From + 1).Sum(b => sp.Power[b]);

    public static string F(double v, string f = "0.000") => double.IsNaN(v) ? "-" : v.ToString(f, CultureInfo.InvariantCulture);

    public static string F(double? v, string f = "0.000") => v is null ? "-" : F(v.Value, f);
}
