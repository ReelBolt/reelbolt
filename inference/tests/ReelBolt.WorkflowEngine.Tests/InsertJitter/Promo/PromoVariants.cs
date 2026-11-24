#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;
using FrameQuad = ReelBolt.WorkflowEngine.Services.Video.ChromaQuadTracker.FrameQuad;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo;

/// <summary>One counterfactual: the shipped keyframes re-smoothed, in plate source px (1344x768), one quad per film frame.</summary>
internal sealed record PromoVariant(string Id, string Label, string Note, IReadOnlyList<Quad> Track);

/// <summary>
/// The counterfactual smoothers of M4, applied to the keyframes the compile actually pinned to (these already went through the shipped K0 <c>Smooth</c>).
/// No detection grid exists for this film (the plate clip is not obtainable), so the post chain runs on the keyframes alone: the tracker's own corners
/// (<c>x0..y3</c>) feed the outlier pass, the refined <c>surfaceCorners</c> are what is smoothed and scored. Grid 480x274 is assumed (what the analyze step derives
/// for a 1344x768 plate), as in the earlier real-track tables.
/// </summary>
internal static class PromoVariants
{
    public const int GridW = 480;
    public const int GridH = 274;

    public static SceneData Stub() => new() { Name = "promo-real", W = 1344, H = 768, Fps = 24, Gw = GridW, Gh = GridH };

    public static List<FrameQuad> FrameQuads(PromoInputs inp)
    {
        var list = new List<FrameQuad>(inp.Surface.Count);
        for (int i = 0; i < inp.Surface.Count; i++)
        {
            Quad c = inp.Corners[i], s = inp.Surface[i];
            double w = inp.SrcW, h = inp.SrcH;
            double area = Math.Abs((s.X1 - s.X0) * (s.Y2 - s.Y0)) / (w * h);
            list.Add(new FrameQuad(
                i, c.X0 / w, c.Y0 / h, c.X1 / w, c.Y1 / h, c.X2 / w, c.Y2 / h, c.X3 / w, c.Y3 / h,
                area, 1.0, 1.0,
                Surface: (s.X0 / w, s.Y0 / h, s.X1 / w, s.Y1 / h, s.X2 / w, s.Y2 / h, s.X3 / w, s.Y3 / h)));
        }

        return list;
    }

    private static List<Quad> ToQuads(IEnumerable<FrameQuad> frames, PromoInputs inp, int n)
    {
        // One quad per film frame: frames the outlier pass removed come back interpolated, but guard against a gap anyway.
        Dictionary<int, Quad> byFrame = frames.ToDictionary(q => q.FrameIndex, q => Quad.FromFrameQuad(q, inp.SrcW, inp.SrcH, q.FrameIndex / PromoInputs.Fps));
        var o = new List<Quad>(n);
        for (int i = 0; i < n; i++)
            o.Add(byFrame.TryGetValue(i, out Quad q) ? q : inp.Surface[i] with { T = i / PromoInputs.Fps });
        return o;
    }

    /// <summary>Held-frame flags from the keyframes alone: the rule of <c>HeldFrameDetector.FromEdgeDiff</c> applied to the per-frame travel of the quad (px; &lt;= 0.05 px or &lt;= 30 % of the local peak).</summary>
    public static bool[] HeldFromKeyframes(IReadOnlyList<Quad> track)
    {
        var s = new double[track.Count];
        for (int f = 1; f < track.Count; f++)
            s[f] = Enumerable.Range(0, 4).Average(c => Math.Sqrt(Math.Pow(track[f].Corner(c).X - track[f - 1].Corner(c).X, 2) + Math.Pow(track[f].Corner(c).Y - track[f - 1].Corner(c).Y, 2)));
        var held = new bool[track.Count];
        for (int f = 1; f < track.Count; f++)
        {
            double peak = 0;
            for (int j = Math.Max(1, f - 2); j <= Math.Min(track.Count - 1, f + 2); j++)
            {
                if (j != f)
                    peak = Math.Max(peak, s[j]);
            }

            held[f] = s[f] <= 0.05 || s[f] <= 0.3 * peak;
        }

        return held;
    }

    public static IReadOnlyList<PromoVariant> Build(PromoInputs inp)
    {
        int n = inp.Surface.Count;
        SceneData stub = Stub();
        ChromaQuadTracker.Options options = BenchSupport.ProductionOptions(BenchSupport.DefaultConfig);
        IReadOnlyList<Quad> shipped = inp.Surface.Select((q, i) => q with { T = i / PromoInputs.Fps }).ToList();
        var result = new List<PromoVariant> { new("shipped", "shipped keyframes (what the compile pinned to; already K0-smoothed)", "baseline", shipped) };
        List<FrameQuad> frames = FrameQuads(inp);

        // (a) C2 post chain: hinge reject -> upright -> [cadence] -> K4ra(tau 2, g 0.10).
        (List<FrameQuad> hinge, double noiseGrid, int rejected, _) = RejectPrototype.Run(frames, PromoInputs.Fps, GridW, GridH, options, RejectPrototype.Mode.Hinge);
        List<FrameQuad> s3 = ChromaQuadTracker.UprightTrackLabels(hinge, GridW, GridH);
        List<Quad> s3q = ToQuads(s3, inp, n);
        double noiseSrc = noiseGrid * stub.W / stub.Gw;
        var k4ra = new LocalPolySmoother(new LocalPolyOptions(2, [4, 6, 8, 12], 2.0, RawAbove: 6.0), Candidates.Prior(stub, 0.10));
        bool[] none = new bool[n];
        result.Add(new PromoVariant("C2-held-off", "(a) C2 post chain: hinge reject + cadence (held detection OFF) + K4ra tau 2 g 0.10",
            $"hinge rejected {rejected} detections; cadence wrapper with no held frames = plain K4ra", new CadenceSmoother(k4ra, none).Smooth(s3q, PromoInputs.Fps, noiseSrc).ToList()));
        bool[] held = HeldFromKeyframes(s3q);
        result.Add(new PromoVariant("C2-held-derived", "(a2) C2 post chain with held frames derived from the keyframes' own travel",
            $"{held.Count(h => h)} of {n} frames flagged held", new CadenceSmoother(k4ra, held).Smooth(s3q, PromoInputs.Fps, noiseSrc).ToList()));

        // (b) plain K4ra on the shipped keyframes, g = 0.10.
        result.Add(new PromoVariant("K4ra-g0.10", "(b) plain K4ra tau 2, sigma prior g = 0.10 x 1344/480", "no reject, no cadence",
            k4ra.Smooth(shipped, PromoInputs.Fps, 0).ToList()));

        // (c) variant g 0.18 / tau 1.5.
        var k4raC = new LocalPolySmoother(new LocalPolyOptions(2, [4, 6, 8, 12], 1.5, RawAbove: 6.0), Candidates.Prior(stub, 0.18));
        result.Add(new PromoVariant("K4ra-g0.18-t1.5", "(c) K4ra tau 1.5, g 0.18", "no reject, no cadence", k4raC.Smooth(shipped, PromoInputs.Fps, 0).ToList()));

        // (d) K0 again (control): the shipped outlier pass + Smooth(3, gate 2x noise) applied to what is already K0-smoothed.
        (List<FrameQuad> prodRun, double prodNoise, int prodRej, _) = RejectPrototype.Run(frames, PromoInputs.Fps, GridW, GridH, options, RejectPrototype.Mode.Production);
        List<FrameQuad> prodS3 = ChromaQuadTracker.UprightTrackLabels(prodRun, GridW, GridH);
        var k0 = new ProductionSmoother(prodS3, GridW, GridH, stub.W, stub.H);
        result.Add(new PromoVariant("K0-again", "(d) K0 re-applied (control): shipped reject + Smooth(3, gate 2x noise)", $"production reject removed {prodRej}",
            k0.Smooth(ToQuads(prodS3, inp, n), PromoInputs.Fps, prodNoise * stub.W / stub.Gw).ToList()));

        // (e) oracle: a fixed 25-frame local quadratic (heavy smoothing), what "no frame-rate wobble at all" would look like.
        result.Add(new PromoVariant("K4fix-H12", "(e) oracle: fixed K4 local quadratic, half-width 12", "reference for what a heavy smoother reaches",
            new LocalPolySmoother(new LocalPolyOptions(2, [12], double.PositiveInfinity, 2, true, 0)).Smooth(shipped, PromoInputs.Fps, 0).ToList()));
        return result;
    }
}
