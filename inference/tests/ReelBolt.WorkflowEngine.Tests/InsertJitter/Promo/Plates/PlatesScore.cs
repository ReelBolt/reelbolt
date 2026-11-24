#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates;

/// <summary>A scored row plus the absolute (offset-included) error of an edge series, which <see cref="PromoScore.Row"/> deliberately does not carry.</summary>
internal sealed record ScoredRow(PromoScore.Row Row, double AbsMean = double.NaN, double AbsMax = double.NaN);

/// <summary>A track scored against the plate's measured edges (all numbers in plate source px; output px of the 1920x1080 staging canvas = x <see cref="PlateScorer.OutPerSrc"/>).</summary>
internal sealed record TrackScore(
    string Label,
    int Frames,
    ScoredRow Quad,
    ScoredRow[] Edges,
    ScoredRow EdgesPooled,
    ScoredRow VerticalEdges,
    ScoredRow HorizontalEdges,
    double QuadSlipX,
    double QuadSlipY,
    JitterStats NoTruth);

internal static class PlateScorer
{
    /// <summary>Output px per plate px of the staging compile (cover fit of 1344x768 into 1920x1080).</summary>
    public const double OutPerSrc = 1920.0 / 1344.0;

    public static readonly string[] EdgeNames = PromoQuadFit.EdgeNames;

    /// <summary>
    /// Scores <paramref name="track"/> (one quad per frame, plate px) against the measurement: M1 as defined by T-007, error = track edge minus measured edge along the
    /// inward normal, per edge and pooled (count-weighted RMS over the four edges); the quad row compares corners with the measured quad's corners; the
    /// no-truth row is the leave-one-out quadratic residual of the track itself (T-007's definition, the 0.994 / 0.873 of the baseline report).
    /// </summary>
    public static TrackScore Score(string label, IReadOnlyList<Quad> track, PlateMeasurement m)
    {
        int n = Math.Min(track.Count, m.N);
        var edgeRows = new ScoredRow[4];
        var series = new IReadOnlyList<double[]?>[4];
        for (int e = 0; e < 4; e++)
        {
            var s = new double[]?[n];
            for (int i = 0; i < n; i++)
            {
                if (m.Edges[i] is { } es && es[e].Valid)
                    s[i] = PromoScore.MeasuredInsideTrack(track[i], e, es[e]).Select(v => -v).ToArray();
            }

            series[e] = s;
            edgeRows[e] = Edge($"M1 {EdgeNames[e]}", s);
        }

        var tq = new Quad?[n];
        var mq = new Quad?[n];
        for (int i = 0; i < n; i++)
        {
            if (m.Quad[i] is { } q)
            {
                tq[i] = track[i];
                mq[i] = q;
            }
        }

        PromoScore.Row quad = PromoScore.Quads("M1 quad", tq, mq);
        (double sx, double sy) = AxisSlip(tq, mq);
        ScoredRow pooled = Pool("M1 edges pooled", edgeRows, series, [0, 1, 2, 3]);
        ScoredRow vertical = Pool("M1 left+right edges (horizontal position)", edgeRows, series, [1, 3]);
        ScoredRow horizontal = Pool("M1 top+bottom edges (vertical position)", edgeRows, series, [0, 2]);
        List<Quad> stamped = track.Select((q, i) => q with { T = i / PromoInputs.Fps }).ToList();
        JitterStats noTruth = JitterMetrics.ComputeNoTruth(stamped, PromoInputs.Fps);
        return new TrackScore(label, m.Valid, new ScoredRow(quad), edgeRows, pooled, vertical, horizontal, sx, sy, noTruth);
    }

    /// <summary>The floor of the meter on a clip with known truth: error = measured minus truth, same instrument.</summary>
    public static TrackScore ScoreAgainstTruth(string label, IReadOnlyList<Quad> truth, PlateMeasurement m) => Score(label, truth, m);

    private static ScoredRow Edge(string label, IReadOnlyList<double[]?> s)
    {
        PromoScore.Row r = PromoScore.Scalar(label, s);
        (double mean, double max) = Abs(s);
        return new ScoredRow(r, mean, max);
    }

    private static ScoredRow Pool(string label, ScoredRow[] rows, IReadOnlyList<double[]?>[] series, int[] which)
    {
        PromoScore.Row pooled = PromoAnalysis.Pool(label, which.Select(i => rows[i].Row).ToList());
        var all = which.SelectMany(i => series[i].Where(x => x is not null).SelectMany(x => x!)).ToList();
        double mean = all.Count == 0 ? double.NaN : all.Average(Math.Abs);
        double max = all.Count == 0 ? double.NaN : all.Max(Math.Abs);
        return new ScoredRow(pooled, mean, max);
    }

    private static (double Mean, double Max) Abs(IReadOnlyList<double[]?> s)
    {
        var all = s.Where(x => x is not null).SelectMany(x => x!).ToList();
        return all.Count == 0 ? (double.NaN, double.NaN) : (all.Average(Math.Abs), all.Max(Math.Abs));
    }

    /// <summary>Slip of the corner error split by axis (RMS over corners and consecutive scored frames).</summary>
    public static (double X, double Y) AxisSlip(IReadOnlyList<Quad?> track, IReadOnlyList<Quad?> measured)
    {
        double sx = 0, sy = 0;
        int cnt = 0;
        for (int i = 1; i < track.Count; i++)
        {
            if (track[i] is not { } a || measured[i] is not { } ma || track[i - 1] is not { } b || measured[i - 1] is not { } mb)
                continue;
            for (int c = 0; c < 4; c++)
            {
                double dx = a.Corner(c).X - ma.Corner(c).X - (b.Corner(c).X - mb.Corner(c).X);
                double dy = a.Corner(c).Y - ma.Corner(c).Y - (b.Corner(c).Y - mb.Corner(c).Y);
                sx += dx * dx;
                sy += dy * dy;
                cnt++;
            }
        }

        return cnt == 0 ? (double.NaN, double.NaN) : (Math.Sqrt(sx / cnt), Math.Sqrt(sy / cnt));
    }
}
