#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo;

/// <summary>All the per-clip numbers of the measurement, in output px (the film canvas).</summary>
internal sealed class ClipAnalysis
{
    /// <summary>M1: the reference track's edge vs the measured screen edge, per edge (top, right, bottom, left): track minus measured along the inward normal.</summary>
    public PromoScore.Row[] M1Edge { get; init; } = [];

    public PromoScore.Row M1Quad { get; init; } = PromoScore.Row.Empty("M1 quad");

    /// <summary>M2: card edge to screen edge distance, per side (both measured).</summary>
    public PromoScore.Row[] M2Side { get; init; } = [];

    /// <summary>M2 through the screen's own normalised coordinates (inverse homography of the measured screen quad), card corners detrended, scaled back by the screen size.</summary>
    public PromoScore.Row M2Quad { get; init; } = PromoScore.Row.Empty("M2 quad");

    /// <summary>M3: measured card edge vs the card the reference keyframes predict, per edge.</summary>
    public PromoScore.Row[] M3Edge { get; init; } = [];

    public PromoScore.Row M3Quad { get; init; } = PromoScore.Row.Empty("M3 quad");

    public IReadOnlyList<double[]?>[] M1Series { get; init; } = [];

    public IReadOnlyList<double[]?>[] M2Series { get; init; } = [];

    public IReadOnlyList<double[]?>[] M3Series { get; init; } = [];
}

internal static class PromoAnalysis
{
    public static readonly string[] Sides = PromoQuadFit.EdgeNames;

    private static bool Ok(PromoEdge[][] edges, int i, int e) => i < edges.Length && edges[i] is { } es && es[e].Valid;

    /// <summary>
    /// Computes M1, M2, M3 of a clip measurement against reference tracks. <paramref name="screenRef"/> is the track the screen is compared to (the shipped keyframes
    /// for the film, the known truth for a synthetic clip), <paramref name="cardRef"/> the card those keyframes predict. <paramref name="mask"/> restricts every row to the
    /// frames in which the matching element was valid in that other measurement (the film), so synthetic floors are scored on the same frames.
    /// </summary>
    public static ClipAnalysis Analyze(ClipMeasurement m, IReadOnlyList<Quad> screenRef, IReadOnlyList<Quad> cardRef, ClipMeasurement? mask = null)
    {
        ClipMeasurement mk = mask ?? m;
        int n = Math.Min(m.N, screenRef.Count);
        var m1 = new IReadOnlyList<double[]?>[4];
        var m3 = new IReadOnlyList<double[]?>[4];
        var m2 = new IReadOnlyList<double[]?>[4];
        var m1Rows = new PromoScore.Row[4];
        var m3Rows = new PromoScore.Row[4];
        var m2Rows = new PromoScore.Row[4];
        for (int e = 0; e < 4; e++)
        {
            var s1 = new double[]?[n];
            var s3 = new double[]?[n];
            var s2 = new double[]?[n];
            for (int i = 0; i < n; i++)
            {
                bool screenOk = Ok(m.ScreenEdges, i, e) && Ok(mk.ScreenEdges, i, e);
                bool cardOk = Ok(m.CardEdges, i, e) && Ok(mk.CardEdges, i, e);
                if (screenOk)
                    s1[i] = PromoScore.MeasuredInsideTrack(screenRef[i], e, m.ScreenEdges[i][e]).Select(v => -v).ToArray();
                if (cardOk)
                    s3[i] = PromoScore.MeasuredInsideTrack(cardRef[i], e, m.CardEdges[i][e]).Select(v => -v).ToArray();
                if (screenOk && cardOk)
                    s2[i] = PromoScore.GapInside(PromoScore.Points(m.CardEdges[i][e]), m.ScreenEdges[i][e], screenRef[i], e);
            }

            m1[e] = s1;
            m3[e] = s3;
            m2[e] = s2;
            m1Rows[e] = PromoScore.Scalar($"M1 {Sides[e]}", s1);
            m3Rows[e] = PromoScore.Scalar($"M3 {Sides[e]}", s3);
            m2Rows[e] = PromoScore.Scalar($"M2 {Sides[e]}", s2);
        }

        var screenQ = new Quad?[n];
        var screenT = new Quad?[n];
        var cardQ = new Quad?[n];
        var cardT = new Quad?[n];
        for (int i = 0; i < n; i++)
        {
            if (m.Screen[i] is { } sq && mk.Screen[i] is not null)
            {
                screenQ[i] = screenRef[i];
                screenT[i] = sq;
            }

            if (m.Card[i] is { } cq && mk.Card[i] is not null)
            {
                cardQ[i] = cardRef[i];
                cardT[i] = cq;
            }
        }

        // Quad-level M2: the card's corners expressed in the measured screen quad's unit square.
        var cardInScreen = new Quad?[n];
        var sizes = new (double W, double H)[n];
        for (int i = 0; i < n; i++)
        {
            if (m.Screen[i] is not { } s || m.Card[i] is not { } c || mk.Screen[i] is null || mk.Card[i] is null)
                continue;
            Homography inv = Homography.FromUnitSquare(s).Inverse();
            (double u0, double v0) = inv.Apply(c.X0, c.Y0);
            (double u1, double v1) = inv.Apply(c.X1, c.Y1);
            (double u2, double v2) = inv.Apply(c.X2, c.Y2);
            (double u3, double v3) = inv.Apply(c.X3, c.Y3);
            cardInScreen[i] = new Quad(i / PromoInputs.Fps, u0, v0, u1, v1, u2, v2, u3, v3);
            double sw = (Dist(s.X0, s.Y0, s.X1, s.Y1) + Dist(s.X2, s.Y2, s.X3, s.Y3)) / 2;
            double sh = (Dist(s.X0, s.Y0, s.X2, s.Y2) + Dist(s.X1, s.Y1, s.X3, s.Y3)) / 2;
            sizes[i] = (sw, sh);
        }

        PromoScore.Row m2Quad = QuadInScreen(cardInScreen, sizes);
        return new ClipAnalysis
        {
            M1Edge = m1Rows,
            M1Quad = PromoScore.Quads("M1 quad", screenQ, screenT),
            M2Side = m2Rows,
            M2Quad = m2Quad,
            M3Edge = m3Rows,
            M3Quad = PromoScore.Quads("M3 quad", cardQ, cardT),
            M1Series = m1,
            M2Series = m2,
            M3Series = m3
        };
    }

    private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    /// <summary>Card corners in screen units, detrended by their per-corner mean (constant if nothing wobbles), scaled back to px by each frame's screen size, scored against zero.</summary>
    private static PromoScore.Row QuadInScreen(IReadOnlyList<Quad?> cardUv, (double W, double H)[] sizes)
    {
        int n = cardUv.Count;
        var idx = Enumerable.Range(0, n).Where(i => cardUv[i] is not null).ToList();
        if (idx.Count < 2)
            return PromoScore.Row.Empty("M2 quad");
        var mean = new (double U, double V)[4];
        for (int c = 0; c < 4; c++)
            mean[c] = (idx.Average(i => cardUv[i]!.Value.Corner(c).X), idx.Average(i => cardUv[i]!.Value.Corner(c).Y));
        var track = new Quad?[n];
        var zero = new Quad?[n];
        foreach (int i in idx)
        {
            double[] v = new double[8];
            for (int c = 0; c < 4; c++)
            {
                v[2 * c] = (cardUv[i]!.Value.Corner(c).X - mean[c].U) * sizes[i].W;
                v[2 * c + 1] = (cardUv[i]!.Value.Corner(c).Y - mean[c].V) * sizes[i].H;
            }

            track[i] = new Quad(i / PromoInputs.Fps, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]);
            zero[i] = new Quad(i / PromoInputs.Fps, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        return PromoScore.Quads("M2 quad", track, zero);
    }

    /// <summary>Count-weighted RMS of one statistic across rows (rows without stats skipped); the frames column is the sum.</summary>
    public static PromoScore.Row Pool(string label, IReadOnlyList<PromoScore.Row> rows)
    {
        List<PromoScore.Row> ok = rows.Where(r => r.Stats is not null).ToList();
        if (ok.Count == 0)
            return PromoScore.Row.Empty(label);
        double W(PromoScore.Row r) => r.Frames;
        double wsum = ok.Sum(W);
        double Rms(Func<JitterStats, double> f) => Math.Sqrt(ok.Where(r => !double.IsNaN(f(r.Stats!))).Sum(r => W(r) * f(r.Stats!) * f(r.Stats!)) / ok.Where(r => !double.IsNaN(f(r.Stats!))).Sum(W));
        double Mean(Func<JitterStats, double> f) => ok.Where(r => !double.IsNaN(f(r.Stats!))).Sum(r => W(r) * f(r.Stats!)) / ok.Where(r => !double.IsNaN(f(r.Stats!))).Sum(W);
        var st = new JitterStats(
            (int)wsum, Mean(s => s.ErrMean), ok.Max(r => r.Stats!.ErrP95), ok.Max(r => r.Stats!.ErrMax),
            Rms(s => s.SlipRms), ok.Max(r => r.Stats!.SlipP95), Rms(s => s.HfRms), double.NaN, null, null, null, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        return new PromoScore.Row(label, (int)wsum, st, Mean2(ok, r => r.OffsetMean), ok.Where(r => !double.IsNaN(r.HfMax)).Select(r => r.HfMax).DefaultIfEmpty(double.NaN).Max(), ok.Where(r => !double.IsNaN(r.SlipMax)).Select(r => r.SlipMax).DefaultIfEmpty(double.NaN).Max());

        static double Mean2(List<PromoScore.Row> rs, Func<PromoScore.Row, double> f) => rs.Where(r => !double.IsNaN(f(r))).Sum(r => r.Frames * f(r)) / rs.Where(r => !double.IsNaN(f(r))).Sum(r => r.Frames);
    }
}
