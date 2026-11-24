#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo;

/// <summary>The card (the chat window's outer rectangle) in the inserted content's own unit coordinates, and how stable its measurement was.</summary>
internal sealed record CardRect(double U0, double V0, double U1, double V1, double SpreadU, double SpreadV, int Frames)
{
    /// <summary>Unit-coordinate corner of the card, order TL, TR, BL, BR.</summary>
    public (double U, double V) Corner(int c) => c switch
    {
        0 => (U0, V0),
        1 => (U1, V0),
        2 => (U0, V1),
        _ => (U1, V1)
    };
}

/// <summary>Per-frame measurement of the screen quad and the card quad on one clip (the film, or a synthetic stand-in), output px.</summary>
internal sealed class ClipMeasurement
{
    public int N { get; init; }

    public Quad?[] Screen { get; init; } = [];

    public PromoEdge[][] ScreenEdges { get; init; } = [];

    public Quad?[] Card { get; init; } = [];

    public PromoEdge[][] CardEdges { get; init; } = [];

    public int EdgeValid(bool card, int edge) => (card ? CardEdges : ScreenEdges).Count(e => e is not null && e[edge].Valid);
}

internal static class PromoMeasure
{
    /// <summary>Bump when the fitting algorithm or its options change: cached measurements carry it in their file name.</summary>
    public const string AlgoVersion = "v1";

    public static readonly PromoEdgeOptions ScreenOptions = new(Reach1: 9, Reach2: 6, MinSpan: 12, MaxResidualRms: 0.6, MinInlier: 0.85);

    public static readonly PromoEdgeOptions CardOptions = new(Reach1: 9, Reach2: 6, LocalLevels: true, MinSpan: 3, MaxResidualRms: 1.0, MinInlier: 0.8);

    /// <summary>
    /// The card's rectangle in content coordinates, fitted on the 960x540 proxy of the inserted content (edge integral, local levels) at several
    /// frames. The card is static (only the text inside animates), so the spread over frames is the proxy measurement's own repeatability.
    /// </summary>
    public static CardRect MeasureCardInProxy(PromoInputs inp, int every = 20)
    {
        const int w = 960, h = 540;
        var seed = Quad.From(0, (48, 27), (912, 27), (48, 513), (912, 513));
        var opts = new PromoEdgeOptions(Reach1: 7, Reach2: 5, LocalLevels: true, MinSpan: 3, MaxResidualRms: 0.5, MinInlier: 0.8);
        var rects = new List<double[]>();
        foreach ((int index, float[] luma) in PromoVideo.Luma(inp.ProxyPath, w, h))
        {
            if (index % every != 0)
                continue;
            PromoQuadFit fit = PromoEdgeFit.FitQuad(luma, w, h, seed, opts);
            if (fit.Quad is not { } q)
                continue;
            // The card is axis-aligned: the mean over each pair of corners.
            rects.Add([(q.X0 + q.X2) / 2 / w, (q.Y0 + q.Y1) / 2 / h, (q.X1 + q.X3) / 2 / w, (q.Y2 + q.Y3) / 2 / h]);
        }

        if (rects.Count == 0)
            throw new InvalidOperationException("card not found in the proxy");
        double M(int i) => rects.Average(r => r[i]);
        return new CardRect(M(0), M(1), M(2), M(3),
            Math.Max(rects.Max(r => r[0]) - rects.Min(r => r[0]), rects.Max(r => r[2]) - rects.Min(r => r[2])) * w,
            Math.Max(rects.Max(r => r[1]) - rects.Min(r => r[1]), rects.Max(r => r[3]) - rects.Min(r => r[3])) * h,
            rects.Count);
    }

    /// <summary>The card's corners in the film canvas given the plate quad the content is pinned to (stretch fit: the content frame IS the plate quad).</summary>
    public static Quad PredictCard(Quad plateOut, CardRect card)
    {
        Homography hm = Homography.FromUnitSquare(plateOut);
        (double X, double Y) P(int c)
        {
            (double u, double v) = card.Corner(c);
            return hm.Apply(u, v);
        }

        return Quad.From(plateOut.T, P(0), P(1), P(2), P(3));
    }

    /// <summary>Fits the screen quad (when seeds are given) and the card quad on every frame of a clip, seeded per frame; an iterative re-seed makes the answer independent of the seed's error (within the first pass' reach).</summary>
    public static ClipMeasurement MeasureClip(string clipPath, IReadOnlyList<Quad>? screenSeeds, IReadOnlyList<Quad> cardSeeds, PromoEdgeOptions screenOpt, PromoEdgeOptions cardOpt, int w = PromoInputs.OutW, int h = PromoInputs.OutH) =>
        MeasureFrames(PromoVideo.Luma(clipPath, w, h, cardSeeds.Count), screenSeeds, cardSeeds, screenOpt, cardOpt, w, h);

    public static ClipMeasurement MeasureFrames(IEnumerable<(int Index, float[] Luma)> frames, IReadOnlyList<Quad>? screenSeeds, IReadOnlyList<Quad> cardSeeds, PromoEdgeOptions screenOpt, PromoEdgeOptions cardOpt, int w = PromoInputs.OutW, int h = PromoInputs.OutH)
    {
        int n = cardSeeds.Count;
        var screen = new Quad?[n];
        var screenEdges = new PromoEdge[n][];
        var card = new Quad?[n];
        var cardEdges = new PromoEdge[n][];
        foreach ((int index, float[] luma) in frames)
        {
            if (index >= n)
                break;
            if (screenSeeds is not null)
            {
                PromoQuadFit s = PromoEdgeFit.FitQuad(luma, w, h, screenSeeds[index], screenOpt);
                screen[index] = s.Quad;
                screenEdges[index] = s.Edges;
            }

            PromoQuadFit c = PromoEdgeFit.FitQuad(luma, w, h, cardSeeds[index], cardOpt);
            card[index] = c.Quad;
            cardEdges[index] = c.Edges;
        }

        return new ClipMeasurement { N = n, Screen = screen, ScreenEdges = screenEdges, Card = card, CardEdges = cardEdges };
    }
}
