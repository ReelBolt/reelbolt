#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;
using FrameQuad = ReelBolt.WorkflowEngine.Services.Video.ChromaQuadTracker.FrameQuad;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Combined;

/// <summary>
/// One configuration of the recommended fix stack (T-006). <see cref="DecodeId"/> names an <see cref="EdgeDecode"/> variant (T-003);
/// <see cref="Smoothing"/> swaps the shipped outlier pass + <c>Smooth</c> for hinge rejection + cadence-aware K4ra (T-004).
/// </summary>
internal sealed record CombinedConfig(string Id, string Label, string DecodeId, bool Smoothing, double SigmaGridFraction = 0.1);

internal static class CombinedConfigs
{
    /// <summary>The ticket's E1b chain: <c>scale=iw:ih:flags=bilinear+full_chroma_inp,format=yuv444p,scale=W:H:flags=area+full_chroma_int,format=rgb24</c> (T-003's <c>e1b-bilinear-fast</c>).</summary>
    public const string E1b = "e1b-bilinear-fast";

    /// <summary>The ticket's E1a chain: <c>scale=W:H:flags=area+full_chroma_int</c> (T-003's <c>e1a-int</c>; identical output to its <c>e1a</c>).</summary>
    public const string E1a = "e1a-int";

    public static readonly CombinedConfig C0 = new("C0", "baseline (shipped decode, shipped reject + Smooth)", EdgeDecode.BaseId, false);
    public static readonly CombinedConfig C1 = new("C1", "E1b decode only", E1b, false);
    public static readonly CombinedConfig C2 = new("C2", "hinge reject + cadence + K4ra only", EdgeDecode.BaseId, true);
    public static readonly CombinedConfig C3 = new("C3", "E1b + hinge reject + cadence + K4ra", E1b, true);
    public static readonly CombinedConfig C3a = new("C3a", "E1a (cheap decode) + hinge reject + cadence + K4ra", E1a, true);

    public static readonly IReadOnlyList<CombinedConfig> All = [C0, C1, C2, C3, C3a];
}

/// <summary>What the width test did on one track: the sigma it was given and where it settled.</summary>
internal sealed record WidthDiag(double SigmaSrc, double SigmaOwn, double SigmaPriorFloor, int Samples, int[] WidthCounts, int[] Widths)
{
    public double MeanWidth => Samples == 0 ? 0 : Enumerable.Range(0, Widths.Length).Sum(i => (double)Widths[i] * WidthCounts[i]) / Samples;

    public double RawFraction => Samples == 0 ? 0 : WidthCounts[0] / (double)Samples;
}

/// <summary>
/// The same arithmetic as <see cref="LocalPolySmoother"/> (it calls the same <see cref="LocalPolyCore"/>), additionally recording the resolved sigma and the
/// chosen half-width histogram. Test-only diagnostic; a unit test pins its output to <see cref="LocalPolySmoother"/>.
/// </summary>
internal sealed class ProbedPolySmoother : ITrackSmoother
{
    private readonly LocalPolyOptions _o;
    private readonly SigmaPolicy _sigma;

    public ProbedPolySmoother(LocalPolyOptions o, SigmaPolicy sigma)
    {
        _o = o;
        _sigma = sigma;
    }

    public string Name => "probed K4ra";

    public WidthDiag? Diag { get; private set; }

    public IReadOnlyList<Quad> Smooth(IReadOnlyList<Quad> track, double fps, double noisePx)
    {
        if (track.Count < 5)
            return track;
        double[][] s = TrackSeries.ToSeries(track);
        double sigma = _sigma.Resolve(s, noisePx);
        double[][] o = LocalPolyCore.Run(TrackSeries.Times(track, fps), s, sigma, _o, out int[] chosen);
        int[] widths = [0, .. _o.Widths.Where(w => w > 0)];
        var counts = new int[widths.Length];
        foreach (int c in chosen)
            counts[Array.IndexOf(widths, c)]++;
        Diag = new WidthDiag(sigma, TrackSeries.SigmaThirdDiff(s), Math.Max(_sigma.Floor, _sigma.Prior), chosen.Length, counts, widths);
        return TrackSeries.Build(track, o);
    }
}

/// <summary>One config's result on one scene.</summary>
internal sealed record CombinedResult(
    CombinedConfig Config, string Scene, List<Quad> Track, JitterStats Stats, int RunCount, int Rejected, int HeldFrames, WidthDiag? Width,
    double PostMs, double ActualErrSigma);

/// <summary>
/// Glue only: the T-003 stage replica (<see cref="EdgePipeline"/>, production detection/assembly/edge refiner with a swappable decode) up to S2, then
/// either the shipped S3/S4 (<see cref="StageSet.S4"/>) or the T-004 chain (<see cref="RejectPrototype"/> hinge, production upright, held-frame detection on
/// THIS config's own grid and S3, <see cref="CadenceSmoother"/> around K4ra). No prototype algorithm is re-implemented here.
/// </summary>
internal static class CombinedPipeline
{
    public static LocalPolyOptions K4raOptions { get; } = new(2, [4, 6, 8, 12], 2.0, RawAbove: 6.0);

    public static SigmaPolicy Prior(int sceneWidth, int gridWidth, double gridFraction) =>
        new(0.05, gridFraction * (sceneWidth / (double)gridWidth), UseGivenNoise: false, UseOwn: true);

    /// <summary>The shipped tracker run on a decoded grid: no re-fit, production stages up to and including <c>Smooth</c>.</summary>
    public static EdgeRun RunStages(JitterScene scene, byte[] grid, BenchSupport.GridPlan plan, string clipPath)
    {
        ChromaQuadTracker.Options options = BenchSupport.ProductionOptions(BenchSupport.DefaultConfig);
        int frames = grid.Length / (plan.GridW * plan.GridH * 3);
        return EdgePipeline.Run(new EdgeSignal.Candidate("run", "stages", EdgeDecode.BaseId), grid, frames, plan.GridW, plan.GridH, plan.SampleFps, options, scene.Width, scene.Height, clipPath);
    }

    /// <summary>The T-004 recommendation applied to the S2 of a stage run: hinge reject, upright, cadence-aware K4ra.</summary>
    public static (List<Quad> Track, int Rejected, int Held, WidthDiag? Width, double PostMs, double ActualErrSigma) SmoothChain(
        JitterScene scene, StageSet st, byte[] grid, BenchSupport.GridPlan plan, double gridFraction, IReadOnlyList<Quad>? truth)
    {
        var sw = Stopwatch.StartNew();
        int frames = grid.Length / (plan.GridW * plan.GridH * 3);
        if (st.S2.Count == 0)
            return ([], 0, 0, null, 0, double.NaN);
        (List<FrameQuad> rejected, double noiseGrid, int rejectedCount, _) = RejectPrototype.Run(st.S2, plan.SampleFps, plan.GridW, plan.GridH, st.Options, RejectPrototype.Mode.Hinge);
        if (rejected.Count == 0)
            return ([], rejectedCount, 0, null, sw.Elapsed.TotalMilliseconds, double.NaN);
        List<FrameQuad> s3 = ChromaQuadTracker.UprightTrackLabels(rejected, plan.GridW, plan.GridH);
        HeldFrameDetector.MeasureEdgeDiff(grid, frames, plan.GridW, plan.GridH, s3, out double[] edge, out _);
        bool[] held = HeldFrameDetector.FromEdgeDiff(edge);
        List<Quad> s3q = StageCapture.ToQuads(s3.Cast<FrameQuad?>(), scene.Width, scene.Height, plan.SampleFps);
        var smoother = new ProbedPolySmoother(K4raOptions, Prior(scene.Width, plan.GridW, gridFraction));
        var cad = new CadenceSmoother(smoother, held);
        IReadOnlyList<Quad> output = cad.Smooth(s3q, plan.SampleFps, noiseGrid * scene.Width / plan.GridW);
        double ms = sw.Elapsed.TotalMilliseconds;
        double actual = truth is null ? double.NaN : ErrSigma(s3q, truth, plan.SampleFps);
        return (output.ToList(), rejectedCount, held.Count(h => h), smoother.Diag, ms, actual);
    }

    /// <summary>The detrended (third-difference) per-coordinate error sigma of a track against the truth: what the width test's sigma should be, if it knew.</summary>
    public static double ErrSigma(IReadOnlyList<Quad> track, IReadOnlyList<Quad> truth, double fps)
    {
        var err = new double[8][];
        for (int c = 0; c < 8; c++)
            err[c] = new double[track.Count];
        for (int i = 0; i < track.Count; i++)
        {
            int f = Math.Clamp((int)Math.Round(track[i].T * fps), 0, truth.Count - 1);
            Quad g = truth[f];
            Quad q = track[i];
            err[0][i] = q.X0 - g.X0; err[1][i] = q.Y0 - g.Y0; err[2][i] = q.X1 - g.X1; err[3][i] = q.Y1 - g.Y1;
            err[4][i] = q.X2 - g.X2; err[5][i] = q.Y2 - g.Y2; err[6][i] = q.X3 - g.X3; err[7][i] = q.Y3 - g.Y3;
        }

        return TrackSeries.SigmaThirdDiff(err);
    }
}
