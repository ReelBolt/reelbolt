#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter;

/// <summary>
/// The intermediate products of one tracker pass, replicated in the order <c>ChromaQuadTracker.Track</c>
/// runs them (frameScale, self-tuned growth bar, per-frame detection, assembly, trim, edge refinement,
/// outlier rejection, label upright, smoothing). Only the stage hand-offs are re-implemented — every
/// stage body is the production method itself. <see cref="Track"/>'s own output is compared with
/// <see cref="S4"/> by the caller to prove the replica is faithful.
/// </summary>
internal sealed record StageSet(
    ChromaQuadTracker.Options Options,
    VideoInsertTrackTuning? Tuning,
    ChromaQuadTracker.FrameQuad?[] S1,
    IReadOnlyList<Quad>? S0,
    List<ChromaQuadTracker.FrameQuad> S2a,
    List<ChromaQuadTracker.FrameQuad> S2,
    List<ChromaQuadTracker.FrameQuad> S3,
    List<ChromaQuadTracker.FrameQuad> S4,
    double Noise,
    int RunCount);

internal static class StageCapture
{
    /// <summary>Source-px quads of a stage's frame list, stamped at <c>FrameIndex / fps</c>.</summary>
    /// <summary>The production smoothing stage (<c>InsertTrackSmoother</c>: held-frame detection on this grid, then the robust adaptive smoother) on an S3 track.</summary>
    public static List<ChromaQuadTracker.FrameQuad> ProductionSmooth(List<ChromaQuadTracker.FrameQuad> s3, byte[] grid, int frames, int gw, int gh, int sw) =>
        InsertTrackSmoother.Smooth(s3, InsertTrackSmoother.DetectHeld(grid, frames, gw, gh, s3), sw / (double)gw, gw, gh);

    public static List<Quad> ToQuads(IEnumerable<ChromaQuadTracker.FrameQuad?> frames, int sw, int sh, double fps, bool preferSurface = true) =>
        frames.Where(q => q is not null)
            .Select(q => Quad.FromFrameQuad(q!, sw, sh, q!.FrameIndex / fps, preferSurface))
            .ToList();

    /// <summary>
    /// Runs the pipeline stage by stage on one raw RGB24 grid. <paramref name="options"/> are the options
    /// <c>VideoAnalyzeStepExecutor</c> hands <c>Track</c>; <paramref name="withS0"/> additionally recovers each
    /// frame's integer extreme-point corners (one more detection pass).
    /// </summary>
    public static StageSet Capture(
        byte[] grid, int frames, int gw, int gh, double fps, ChromaQuadTracker.Options options, int sw, int sh, bool withS0)
    {
        int frameSize = gw * gh * 3;
        double frameScale = Math.Max(1.0, fps / ChromaQuadTracker.ReferenceSampleFps);
        options = options with
        {
            MaxGapFrames = ChromaQuadTracker.ScaleFrames(options.MaxGapFrames, frameScale),
            MaxOccludedFrames = ChromaQuadTracker.ScaleFrames(options.MaxOccludedFrames, frameScale)
        };

        VideoInsertTrackTuning? tuning = null;
        if (options.GrowExcessFraction is null)
        {
            tuning = ChromaTrackerTuner.Tune(grid, frames, gw, gh, options);
            options = options with { GrowExcessFraction = tuning.Parameters[ChromaTrackerTuner.GrowExcessFractionKey] };
        }

        // Geometry does not depend on the chroma-key solve, which only retains masks.
        ChromaQuadTracker.Options detectOptions = options with { SolveChromaKey = false };
        var perFrame = new List<ChromaQuadTracker.FrameQuad>[frames];
        for (int f = 0; f < frames; f++)
            perFrame[f] = ChromaQuadTracker.DetectQuads(grid, f * frameSize, gw, gh, f, detectOptions, false);

        var s1 = perFrame.Select(l => l.FirstOrDefault()).ToArray();

        List<Quad>? s0 = null;
        if (withS0)
        {
            ChromaQuadTracker.Options retain = options with { SolveChromaKey = true };
            s0 = [];
            for (int f = 0; f < frames; f++)
            {
                ChromaQuadTracker.FrameQuad? q = ChromaQuadTracker.DetectQuads(grid, f * frameSize, gw, gh, f, retain, true).FirstOrDefault();
                if (q?.Component is not { Length: > 0 } comp)
                    continue;
                List<int> component = comp.ToList();
                double angle = ChromaQuadOrientation.DominantAngle(component, gw, gh);
                var (tl, tr, br, bl) = ChromaQuadOrientation.ExtremeCorners(component, gw, angle);
                (double X, double Y) N((double X, double Y) p) => ((p.X + 0.5) / gw * sw, (p.Y + 0.5) / gh * sh);
                s0.Add(Quad.From(f / fps, N(tl), N(tr), N(bl), N(br)));
            }
        }

        string color = ChromaQuadTracker.NormalizeColorName(options.ColorName);
        StageSet? best = null;
        int runCount = 0;
        foreach (List<ChromaQuadTracker.FrameQuad> assembled in ChromaQuadTracker.AssembleTracks(perFrame, options, frameScale))
        {
            List<ChromaQuadTracker.FrameQuad> s2a = ChromaQuadTracker.TrimWeakEnds(assembled);
            if (s2a.Count == 0)
                continue;
            runCount++;
            List<ChromaQuadTracker.FrameQuad> s2 = options.RefineEdgesFromNeighbours
                ? ChromaEdgeTemporalRefiner.Refine(s2a, grid, gw, gh, color)
                : s2a;
            (List<ChromaQuadTracker.FrameQuad> rejected, double noise) = ChromaQuadTracker.RejectImplausibleSamples(s2, fps, gw, gh, options);
            if (rejected.Count == 0)
                continue;
            List<ChromaQuadTracker.FrameQuad> s3 = ChromaQuadTracker.UprightTrackLabels(rejected, gw, gh);
            List<ChromaQuadTracker.FrameQuad> s4 = ProductionSmooth(s3, grid, frames, gw, gh, sw);
            if (best is null || s4.Count > best.S4.Count)
                best = new StageSet(options, tuning, s1, s0, s2a, s2, s3, s4, noise, 0);
        }

        return (best ?? new StageSet(options, tuning, s1, s0, [], [], [], [], 0, 0)) with { RunCount = runCount };
    }
}
