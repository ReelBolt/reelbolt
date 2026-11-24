#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates;

/// <summary>Film A's predicted perceived wobble, as T-007 defines it, for any plate track (M2: the content card's margin against the screen edge).</summary>
internal sealed record PredictedWobble(PromoScore.Row Ideal, PromoScore.Row Corrected, PromoScore.Row VersusPlate);

/// <summary>
/// The T-007 instrument with the film's measured screen edges held fixed. The film (<c>final-cold-open-ac30a89f.mp4</c>) was compiled from the shipped keyframes
/// of artifact-65fe46f0 (the same track as artifact-d0fd129e); the screen's outline in the film is the plate's own outline (the insert content is matted on top
/// of the plate, the bezel stays), so it does not depend on which keyframes are scored. For a track T, the card the compile WOULD pin (the content card pushed through
/// T's plate quad, <see cref="PromoMeasure.PredictCard"/>) is compared with those measured screen edges along each edge's inward normal, exactly as M4b/M4c of the
/// T-007 report do (<c>ideal</c> = the render adds nothing; <c>corrected</c> = ideal plus the film's own card-vs-keyframes error, the pessimistic bound).
/// <c>VersusPlate</c> is the same prediction against the RAW PLATE's measured edges (mapped to output px) instead of the film's: no re-encoded film involved.
/// </summary>
internal sealed class PlatesWobble
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>Bump when the film measurement's algorithm changes (<see cref="PromoMeasure.AlgoVersion"/> is part of the cache name too).</summary>
    public const string CacheVersion = "w1";

    private readonly PromoInputs _inp = new();
    private readonly CardRect _card;
    private readonly ClipMeasurement _film;
    private readonly List<Quad> _shippedOut;
    private readonly IReadOnlyList<double[]?>[] _filmGap;
    private readonly IReadOnlyList<double[]?>[] _shippedIdeal;
    private readonly int _n;

    public PlatesWobble(string cacheDir)
    {
        _n = _inp.Surface.Count;
        _card = PromoMeasure.MeasureCardInProxy(_inp);
        _shippedOut = _inp.Surface.Select((q, i) => _inp.ToOut(q) with { T = i / PromoInputs.Fps }).ToList();
        List<Quad> cardPred = _shippedOut.Select(q => PromoMeasure.PredictCard(q, _card)).ToList();
        _film = Cached(Path.Combine(cacheDir, $"film-{CacheVersion}-{PromoMeasure.AlgoVersion}.json"),
            () => PromoMeasure.MeasureClip(_inp.FilmPath, _shippedOut, cardPred, PromoMeasure.ScreenOptions, PromoMeasure.CardOptions));
        ClipAnalysis fa = PromoAnalysis.Analyze(_film, _shippedOut, cardPred);
        _filmGap = fa.M2Series;
        _shippedIdeal = IdealGap(_shippedOut);
    }

    public CardRect Card => _card;

    public int FilmScreenQuads => _film.Screen.Count(q => q is not null);

    /// <summary>The film's M2 as measured (the headline 0.711 out px of T-007), pooled over the four sides.</summary>
    public PromoScore.Row FilmMeasured()
    {
        ClipAnalysis fa = PromoAnalysis.Analyze(_film, _shippedOut, _shippedOut.Select(q => PromoMeasure.PredictCard(q, _card)).ToList());
        return PromoAnalysis.Pool("film, measured", fa.M2Side);
    }

    /// <summary><paramref name="trackSrc"/> is one plate-px quad per frame (the keyframes' <c>surfaceCorners</c>).</summary>
    public PredictedWobble Predict(IReadOnlyList<Quad> trackSrc, PlateMeasurement? plate)
    {
        List<Quad> outQ = trackSrc.Take(_n).Select((q, i) => _inp.ToOut(q) with { T = i / PromoInputs.Fps }).ToList();
        IReadOnlyList<double[]?>[] ideal = IdealGap(outQ);
        var idealRows = new List<PromoScore.Row>();
        var corrRows = new List<PromoScore.Row>();
        for (int e = 0; e < 4; e++)
        {
            var si = new double[]?[_n];
            var sc = new double[]?[_n];
            for (int i = 0; i < Math.Min(_n, outQ.Count); i++)
            {
                if (ideal[e][i] is { } g && _filmGap[e][i] is { } gf && _shippedIdeal[e][i] is { } gs)
                {
                    si[i] = g;
                    sc[i] = Enumerable.Range(0, 4).Select(k => g[k] + (gf[k] - gs[k])).ToArray();
                }
            }

            idealRows.Add(PromoScore.Scalar($"ideal {e}", si));
            corrRows.Add(PromoScore.Scalar($"corrected {e}", sc));
        }

        PromoScore.Row versus = PromoScore.Row.Empty("vs plate");
        if (plate is not null)
        {
            var rows = new List<PromoScore.Row>();
            for (int e = 0; e < 4; e++)
            {
                var s = new double[]?[_n];
                for (int i = 0; i < Math.Min(_n, outQ.Count); i++)
                {
                    if (i < plate.N && plate.Edges[i] is { } es && es[e].Valid)
                        s[i] = PromoScore.GapInside(PromoScore.Points(PromoMeasure.PredictCard(outQ[i], _card), e), ToOut(es[e]), outQ[i], e);
                }

                rows.Add(PromoScore.Scalar($"vs plate {e}", s));
            }

            versus = PromoAnalysis.Pool("predicted M2 against the raw plate's measured edges", rows);
        }

        return new PredictedWobble(PromoAnalysis.Pool("predicted M2, ideal render", idealRows), PromoAnalysis.Pool("predicted M2, M3-corrected", corrRows), versus);
    }

    /// <summary>The card the track's plate quad predicts, against the film's measured screen edges (frames where the film's screen edge is clean).</summary>
    private IReadOnlyList<double[]?>[] IdealGap(IReadOnlyList<Quad> plateOut)
    {
        var o = new IReadOnlyList<double[]?>[4];
        for (int e = 0; e < 4; e++)
        {
            var s = new double[]?[_n];
            for (int i = 0; i < Math.Min(_n, plateOut.Count); i++)
            {
                if (_film.ScreenEdges[i] is { } es && es[e].Valid)
                    s[i] = PromoScore.GapInside(PromoScore.Points(PromoMeasure.PredictCard(plateOut[i], _card), e), es[e], _shippedOut[i], e);
            }

            o[e] = s;
        }

        return o;
    }

    private PromoEdge ToOut(PromoEdge e)
    {
        (double X, double Y) P((double X, double Y) p) => (_inp.Fit.OffsetX + p.X / _inp.SrcW * _inp.Fit.Width, _inp.Fit.OffsetY + p.Y / _inp.SrcH * _inp.Fit.Height);
        return e with { A = P(e.A), B = P(e.B) };
    }

    private static ClipMeasurement Cached(string path, Func<ClipMeasurement> compute)
    {
        if (File.Exists(path))
        {
            try
            {
                ClipMeasurement? hit = JsonSerializer.Deserialize<ClipMeasurement>(File.ReadAllText(path), Json);
                if (hit is not null && hit.N > 0)
                    return hit;
            }
            catch (JsonException)
            {
            }
        }

        ClipMeasurement m = compute();
        static PromoEdge[] Strip(PromoEdge[]? es) => es is null ? null! : es.Select(e => e with { Ts = null, Ds = null, Keep = null }).ToArray();
        var stripped = new ClipMeasurement
        {
            N = m.N,
            Screen = m.Screen,
            Card = m.Card,
            ScreenEdges = m.ScreenEdges.Select(Strip).ToArray(),
            CardEdges = m.CardEdges.Select(Strip).ToArray()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(stripped, Json));
        return stripped;
    }
}
