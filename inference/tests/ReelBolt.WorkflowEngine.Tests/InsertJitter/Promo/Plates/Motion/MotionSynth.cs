#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates.Motion;

/// <summary>Everything measured on one known-truth synthetic plate.</summary>
internal sealed record SynthResult(
    SynthClip Clip, PlateMeasurement Chroma, PlateMeasurement Luma, IReadOnlyList<RingPair> Ring, Quad?[] RingPos, double?[][] RingVel,
    Quad?[] Raw, Quad?[] Smoothed, Quad?[] Truth);

internal static class MotionSynth
{
    public static readonly ShakeSpec NoShake = new("none", 0, 0, 0, true);
    public static readonly ShakeSpec Rigid01 = new("rigid0.1px-2to8Hz", 0.1, 2, 8, true);
    public static readonly ShakeSpec Rigid03 = new("rigid0.3px-2to8Hz", 0.3, 2, 8, true);
    public static readonly ShakeSpec Plate03 = new("plateonly0.3px-2to8Hz", 0.3, 2, 8, false);
    public static readonly ShakeSpec Low15 = new("rigid1.5px-0.5to3Hz", 1.5, 0.5, 3, true);

    public static readonly WallSpec Rich = new("rich2", 70, 15, 50, 60);
    public static readonly WallSpec Poor = new("poor3", 30, 4, 150, 90, 20, 60);

    public static RingOptions RingFor(SynthSpec s) => s.Wall.Id.StartsWith("rich", StringComparison.Ordinal) ? new RingOptions(Params: 6) : new RingOptions(Params: 6, DMax: 250, GradMin: 1.5, MinPixels: 3000);

    public static IReadOnlyList<SynthSpec> Matrix { get; } =
    [
        new("val", "filmB", Rich, NoShake, 24),
        new("val", "filmB", Rich, Rigid01, 24),
        new("val", "filmB", Rich, Rigid03, 24),
        new("val", "filmB", Rich, Plate03, 24),
        new("val", "filmB", Rich, Low15, 24),
        new("val", "filmA", Poor, NoShake, 21, Frames: 150),
        new("val", "filmA", Poor, Rigid03, 21, Frames: 150)
    ];

    /// <summary>The truth as a "measurement" (quads and edges exactly) so tracks can be scored against it with the same scorer as the real references.</summary>
    public static PlateMeasurement TruthMeasurement(IReadOnlyList<Quad> truth)
    {
        PromoEdge Edge((double X, double Y) a, (double X, double Y) b) => new(true, "ok", a, b, 0, 1, 100, 0, 100);
        var quads = new Quad?[truth.Count];
        var edges = new PromoEdge[truth.Count][];
        for (int i = 0; i < truth.Count; i++)
        {
            Quad q = truth[i];
            quads[i] = q;
            edges[i] = [Edge(q.Corner(0), q.Corner(1)), Edge(q.Corner(1), q.Corner(3)), Edge(q.Corner(3), q.Corner(2)), Edge(q.Corner(2), q.Corner(0))];
        }

        return new PlateMeasurement(truth.Count, quads, edges);
    }

    public static SynthResult Measure(SynthSpec spec, Action<string> log)
    {
        SynthClip clip = SyntheticPlate.Render(spec, log);
        log($"   {spec.Film}/{spec.Wall.Id}/{spec.Shake.Id}: {clip.Kbps:0} kbit/s");
        string name = $"synth-{spec.Film}-{spec.Wall.Id}-{spec.Shake.Id}-f{spec.Frames}";
        IReadOnlyList<Quad> seeds = clip.Base;
        int n = clip.Truth.Count;

        // Seeds are the smooth path (no shake): a rough start the two-pass meters correct, as the staging keyframes were for the real plates.
        string cacheClip = clip.Path;
        PlateMeasurement chroma = MeasureChromaCached(name, cacheClip, seeds);
        PlateMeasurement luma = MotionMeasure.Luma(name, cacheClip, seeds, SyntheticPlate.W, SyntheticPlate.H);
        IReadOnlyList<RingPair> ring = MotionMeasure.Ring(name, cacheClip, seeds, SyntheticPlate.W, SyntheticPlate.H, RingFor(spec), log);
        Quad?[] g = Chan.Of(seeds);
        (Quad?[] raw, Quad?[] smoothed) = Track(clip, spec, log);
        return new SynthResult(clip, chroma, luma, ring, MotionAnalysis.RingPositions(ring, g), MotionAnalysis.RingVelocity(ring, g), raw, smoothed, Chan.Of(clip.Truth));
    }

    private static PlateMeasurement MeasureChromaCached(string name, string clip, IReadOnlyList<Quad> seeds)
    {
        string path = System.IO.Path.Combine(MotionEnv.Cache, $"chroma-{name}-{MotionMeasure.Fingerprint(clip).GetHashCode():x}.json");
        var json = new System.Text.Json.JsonSerializerOptions { IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        if (System.IO.File.Exists(path))
        {
            try
            {
                PlateMeasurement? hit = System.Text.Json.JsonSerializer.Deserialize<PlateMeasurement>(System.IO.File.ReadAllText(path), json);
                if (hit is not null && hit.N == seeds.Count)
                    return hit;
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        PlateMeasurement m = PlatesMeter.Measure(clip, seeds, SyntheticPlate.W, SyntheticPlate.H);
        var stripped = new PlateMeasurement(m.N, m.Quad, m.Edges.Select(es => es is null ? null! : es.Select(e => e with { Ts = null, Ds = null, Keep = null }).ToArray()).ToArray());
        System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(stripped, json));
        return stripped;
    }

    /// <summary>The production tracker on the clip's grid with the F1 decode: S3 (after the hinge outlier pass, before smoothing) and S4 (F1 + F2, what ships).</summary>
    public static (Quad?[] Raw, Quad?[] Smoothed) Track(SynthClip clip, SynthSpec spec, Action<string> log)
    {
        const double fps = PromoInputs.Fps;
        (int gw, int gh) = ChromaQuadTracker.DeriveGridSize(SyntheticPlate.W, SyntheticPlate.H, 0, 0);
        DecodeResult dec = EdgeDecode.Decode(clip.Path, EdgeDecode.Get("e1b-bilinear-fast"), fps, gw, gh);
        int frames = dec.Grid.Length / (gw * gh * 3);
        StageSet st = StageCapture.Capture(dec.Grid, frames, gw, gh, fps, BenchSupport.ProductionOptions(BenchSupport.DefaultConfig), SyntheticPlate.W, SyntheticPlate.H, false);
        Quad?[] Place(IEnumerable<ChromaQuadTracker.FrameQuad> run)
        {
            var o = new Quad?[frames];
            foreach (ChromaQuadTracker.FrameQuad q in run)
            {
                if (q.FrameIndex >= 0 && q.FrameIndex < frames)
                    o[q.FrameIndex] = Quad.FromFrameQuad(q, SyntheticPlate.W, SyntheticPlate.H, q.FrameIndex / fps);
            }

            return o;
        }

        log($"   tracker: {st.S3.Count} frames tracked of {frames}");
        return (Place(st.S3), Place(st.S4));
    }

    // ------------------------------------------------------------------------------------------------ reporting

    private static string Row(params string[] c) => "| " + string.Join(" | ", c) + " |";

    private static string F(double v, string f = "0.000") => MotionAnalysis.F(v, f);

    public static string Render(SynthResult r)
    {
        var sb = new StringBuilder();
        SynthSpec s = r.Clip.Spec;
        double o = MotionAnalysis.Out;
        sb.AppendLine($"### {s.Film} geometry, wall {s.Wall.Id}, shake {s.Shake.Id}{(s.Shake.WallFollows ? string.Empty : " (the wall does NOT share it)")}");
        sb.AppendLine();
        sb.AppendLine($"{r.Truth.Length} frames, {r.Clip.Kbps:0} kbit/s (real plates 650-940). Truth shake: {F(s.Shake.RmsPx * o)} out px RMS per axis, {s.Shake.LoHz}-{s.Shake.HiHz} Hz. Errors vs truth in output px; edges = 4 edges pooled, quad = corners.");
        sb.AppendLine();
        PlateMeasurement truthM = TruthMeasurement(r.Clip.Truth);
        sb.AppendLine(Row("channel", "frames", "quad slip", "quad hf", "edges slip", "edges hf", "edges mean err", ">2 Hz err rms/axis", ">2 Hz slope on truth", ">2 Hz coh"));
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        double?[][] truthV = Chan.Velocity(r.Truth);
        foreach ((string name, Quad?[]? q, PlateMeasurement? m) in new (string, Quad?[]?, PlateMeasurement?)[]
                 {
                     ("chroma meter", null, r.Chroma), ("luma meter", null, r.Luma), ("ring (corners)", r.RingPos, null),
                     ("tracker raw (S3, no smoother)", r.Raw, null), ("tracker F1+F2 (S4)", r.Smoothed, null)
                 })
        {
            Quad?[] x = q ?? Chan.Of(m!);
            Quad?[] t = Chan.Both(r.Truth, x);
            Quad?[] xx = Chan.Both(x, t);
            TrackScore? sc = null;
            if (m is not null)
                sc = PlateScorer.Score(name, r.Clip.Truth, m);
            else if (name.StartsWith("tracker", StringComparison.Ordinal))
                sc = PlateScorer.Score(name, xx.Select(v => v ?? default).ToList(), truthM);
            PromoScore.Row quad = sc?.Quad.Row ?? PromoScore.Quads(name, xx, t);
            double?[][] ev = Chan.Velocity(Chan.Minus(xx, t));
            double?[][] hp = Chan.HighPass(ev, 13);
            CrossSpectrum? err = CrossSpectrum.Compute(Chan.Coords(Chan.Minus(xx, t)), Chan.Coords(Chan.Minus(xx, t)));
            CrossSpectrum? xt = CrossSpectrum.Compute(Chan.Coords(xx), Chan.Coords(t));
            double slope = xt?.Slope(2, 12.01) ?? double.NaN, coh = xt?.Coherence(2, 12.01) ?? double.NaN;
            sb.AppendLine(Row(
                name, xx.Count(v => v is not null).ToString(CultureInfo.InvariantCulture),
                F((quad.Stats?.SlipRms ?? double.NaN) * o), F((quad.Stats?.HfRms ?? double.NaN) * o),
                sc is null ? "-" : F((sc.EdgesPooled.Row.Stats?.SlipRms ?? double.NaN) * o), sc is null ? "-" : F((sc.EdgesPooled.Row.Stats?.HfRms ?? double.NaN) * o),
                sc is null ? "-" : F((sc.EdgesPooled.Row.Stats?.ErrMean ?? double.NaN) * o),
                err is null ? "-" : F(Math.Sqrt(2 * err.Power(err.Sxx, 2, 12.01)) * o), F(slope, "0.00"), F(coh, "0.00")));
            _ = hp;
            _ = truthV;
        }

        sb.AppendLine();
        // What the smoother removed, against the truth: slope of (truth - smoothed) on (raw - smoothed).
        Quad?[] common = Chan.Both(Chan.Both(r.Raw, r.Smoothed), r.Truth);
        Quad?[] rem = Chan.Minus(Chan.Both(r.Raw, common), Chan.Both(r.Smoothed, common));
        Quad?[] res = Chan.Minus(Chan.Both(r.Truth, common), Chan.Both(r.Smoothed, common));
        CrossSpectrum? rr = CrossSpectrum.Compute(Chan.Coords(rem), Chan.Coords(rem)), rt = CrossSpectrum.Compute(Chan.Coords(rem), Chan.Coords(res));
        sb.AppendLine("F2's removal against the truth: `removed` = raw (S3) minus F1+F2 (S4); `real share` = slope of (truth minus S4) on `removed` (1 = everything F2 removed was real motion, 0 = all noise); `truth hf` = RMS per axis of the truth's own >band motion.");
        sb.AppendLine();
        sb.AppendLine(Row("band", "removed rms/axis", "real share (slope)", "coh", "truth rms/axis", "S4 error rms/axis", "S3 error rms/axis"));
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        CrossSpectrum? tt = CrossSpectrum.Compute(Chan.Coords(Chan.Both(r.Truth, common)), Chan.Coords(Chan.Both(r.Truth, common)));
        Quad?[] e4 = Chan.Minus(Chan.Both(r.Smoothed, common), Chan.Both(r.Truth, common)), e3 = Chan.Minus(Chan.Both(r.Raw, common), Chan.Both(r.Truth, common));
        CrossSpectrum? s4 = CrossSpectrum.Compute(Chan.Coords(e4), Chan.Coords(e4)), s3 = CrossSpectrum.Compute(Chan.Coords(e3), Chan.Coords(e3));
        foreach ((double lo, double hi, string bn) in MotionAnalysis.Bands)
        {
            if (rr is null || rt is null || tt is null || s4 is null || s3 is null)
            {
                sb.AppendLine(Row(bn, "-", "-", "-", "-", "-", "-"));
                continue;
            }

            double rrp = rr.Power(rr.Sxx, lo, hi);
            sb.AppendLine(Row(bn, F(Math.Sqrt(2 * rrp) * o), F(rrp > 0 ? rt.Power(rt.SxyRe, lo, hi) / rrp : double.NaN, "0.00"), F(rt.Coherence(lo, hi), "0.00"),
                F(Math.Sqrt(2 * tt.Power(tt.Sxx, lo, hi)) * o), F(Math.Sqrt(2 * s4.Power(s4.Sxx, lo, hi)) * o), F(Math.Sqrt(2 * s3.Power(s3.Sxx, lo, hi)) * o)));
        }

        sb.AppendLine();
        return sb.ToString();
    }
}
