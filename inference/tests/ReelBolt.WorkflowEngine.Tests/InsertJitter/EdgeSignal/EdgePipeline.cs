#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;

internal enum SeedKind
{
    /// <summary>Production: probe stations and normals from this frame's integer extreme-point corners.</summary>
    Extreme,

    /// <summary>Probes seeded from the previous frame's fitted corners (falls back to the extreme corners on the first frame or a failed fit).</summary>
    Previous
}

internal enum HiResKind
{
    None,

    /// <summary>A second decode at twice the tracker grid, 8-bit luma only.</summary>
    Gray2x,

    /// <summary>A second decode at the source's native resolution, 8-bit luma only.</summary>
    GrayNative,

    /// <summary>The second look on the decoder's own gray plane at the GRID size (no extra resolution; isolates the luma signal from the resolution).</summary>
    GrayGrid
}

/// <summary>One detection-side candidate: which decode, which edge fit, which per-track constants, which second look.</summary>
internal sealed record Candidate(
    string Id,
    string Label,
    string DecodeId = EdgeDecode.BaseId,
    /// <summary>The S1 edge fit; <c>null</c> keeps production's own (nothing is re-fitted).</summary>
    EdgeFitOptions? S1Fit = null,
    LumaMatrix Matrix = LumaMatrix.Bt601,
    /// <summary>E4: the plate level the chroma threshold uses is the track median instead of each frame's own.</summary>
    bool TrackLevel = false,
    SeedKind Seed = SeedKind.Extreme,
    HiResKind HiRes = HiResKind.None,
    EdgeFitOptions? HiResFit = null,
    /// <summary>The S1 luma field is the decoder's own gray plane at the grid size (a second output of the same decode) instead of luma recomputed from the RGB grid.</summary>
    bool GrayLuma = false);

/// <summary>What one candidate produced on one clip, with stage outputs and the cost of the extra work.</summary>
internal sealed record EdgeRun(
    StageSet Stages,
    EdgeTelemetry S1Telemetry,
    EdgeTelemetry HiResTelemetry,
    double RefitMs,
    double HiResMs,
    double HiResDecodeCpuSeconds,
    long HiResFrameBytes);

/// <summary>
/// The stage replica of <see cref="StageCapture"/> with two hooks: a per-frame re-fit of the plate edges right after
/// detection (S1; the production fit is replaced by <see cref="EdgeFit"/> seeded from the same integer extreme corners)
/// and an optional hi-resolution second look after the temporal edge refiner (S2). Every other stage body is the
/// production method. With <c>Candidate.S1Fit == null</c> and no hi-res pass it is exactly <see cref="StageCapture"/>.
/// </summary>
internal static class EdgePipeline
{
    public static EdgeRun Run(
        Candidate cand, byte[] grid, int frames, int gw, int gh, double fps, ChromaQuadTracker.Options options, int sw, int sh, string clipPath, byte[]? grayGrid = null)
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

        string color = ChromaQuadTracker.NormalizeColorName(options.ColorName);
        var s1Tel = new EdgeTelemetry();
        var hiTel = new EdgeTelemetry();
        double refitMs = 0, hiMs = 0, hiDecodeCpu = 0;
        long hiBytes = 0;

        // ---- detection (+ S1 re-fit) ----------------------------------------------------------------
        bool refit = cand.S1Fit is not null;
        ChromaQuadTracker.Options detectOptions = options with { SolveChromaKey = refit };
        var perFrame = new List<ChromaQuadTracker.FrameQuad>[frames];
        var seeds = new (double X, double Y)[frames][];
        for (int f = 0; f < frames; f++)
        {
            perFrame[f] = ChromaQuadTracker.DetectQuads(grid, f * frameSize, gw, gh, f, detectOptions, refit);
            if (!refit || perFrame[f].Count == 0)
                continue;
            ChromaQuadTracker.FrameQuad q = perFrame[f][0];
            if (q.Component is { Length: > 0 } comp)
            {
                List<int> component = comp.ToList();
                double angle = ChromaQuadOrientation.DominantAngle(component, gw, gh);
                var (tl, tr, br, bl) = ChromaQuadOrientation.ExtremeCorners(component, gw, angle);
                seeds[f] = [tl, tr, br, bl];
            }

            // Drop the retained masks: only the seeds are needed from here.
            perFrame[f][0] = q with { Component = null, CoreMask = null, GrowMask = null };
        }

        if (refit)
        {
            double? trackLevel = null;
            if (cand.TrackLevel)
            {
                var levels = perFrame.Where(l => l.Count > 0 && l[0].PlateDominance > 1).Select(l => l[0].PlateDominance).ToList();
                trackLevel = levels.Count == 0 ? null : ChromaQuadTracker.Median(levels);
            }

            var sw0 = Stopwatch.StartNew();
            var dom = new float[gw * gh];
            float[]? lum = null;
            (double X, double Y)[]? previous = null;
            int previousFrame = -10;
            for (int f = 0; f < frames; f++)
            {
                s1Tel.Frames++;
                if (perFrame[f].Count == 0 || seeds[f] is null)
                {
                    previous = null;
                    continue;
                }

                ChromaQuadTracker.FrameQuad q = perFrame[f][0];
                int off = f * frameSize;
                for (int p = 0; p < dom.Length; p++)
                {
                    int b0 = off + p * 3;
                    dom[p] = ChromaEdgeLineFitter.Dominance(color, grid[b0], grid[b0 + 1], grid[b0 + 2]);
                }

                IScalarField domField = new FloatPlaneField(dom, gw, gh, clampNegative: true);
                IScalarField? lumField = null;
                if (cand.S1Fit!.Crossing != Crossing.Chroma)
                {
                    if (cand.GrayLuma)
                    {
                        lumField = new BytePlaneField(grayGrid ?? throw new InvalidOperationException("gray grid missing"), gw, gh, 1, 1, f * gw * gh);
                    }
                    else
                    {
                        lum = LumaPlane.FromRgb(grid, off, gw, gh, cand.Matrix, lum);
                        lumField = new FloatPlaneField(lum, gw, gh);
                    }
                }

                double level = trackLevel ?? q.PlateDominance;
                (double X, double Y)[] ext = seeds[f];
                EdgeFit.Result? fit = null;
                if (cand.Seed == SeedKind.Previous && previous is not null && previousFrame == f - 1)
                    fit = EdgeFit.Fit(domField, lumField, gw, gh, level, previous[0], previous[1], previous[2], previous[3], cand.S1Fit, s1Tel);
                fit ??= EdgeFit.Fit(domField, lumField, gw, gh, level, ext[0], ext[1], ext[2], ext[3], cand.S1Fit, s1Tel);

                if (fit is { } r)
                {
                    perFrame[f][0] = WithCorners(q, r.Corners, gw, gh);
                    previous = r.Corners;
                    previousFrame = f;
                    s1Tel.FramesFitted++;
                }
                else
                {
                    // The fit failed: keep the quad production's own detection produced.
                    perFrame[f][0] = q;
                    previous = null;
                }
            }

            refitMs = sw0.Elapsed.TotalMilliseconds;
        }

        var s1 = perFrame.Select(l => l.FirstOrDefault()).ToArray();

        // ---- assembly, temporal edge refiner ---------------------------------------------------------
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

            // ---- hi-resolution second look (E3) ------------------------------------------------------
            if (cand.HiRes == HiResKind.GrayGrid)
            {
                var swg = Stopwatch.StartNew();
                byte[] gray = grayGrid ?? throw new InvalidOperationException("gray grid missing");
                var updatedG = new Dictionary<int, ChromaQuadTracker.FrameQuad>();
                foreach (ChromaQuadTracker.FrameQuad q in s2)
                {
                    hiTel.Frames++;
                    if (q.Interpolated)
                        continue;
                    var field = new BytePlaneField(gray, gw, gh, 1, 1, q.FrameIndex * gw * gh);
                    (double X, double Y)[] c = Cycle(q, gw, gh);
                    EdgeFit.Result? r = EdgeFit.Refine(field, gw, gh, c[0], c[1], c[2], c[3], cand.HiResFit!, hiTel);
                    if (r is { } rr)
                    {
                        updatedG[q.FrameIndex] = WithCorners(q, rr.Corners, gw, gh);
                        hiTel.FramesFitted++;
                    }
                }

                s2 = s2.Select(q => updatedG.TryGetValue(q.FrameIndex, out ChromaQuadTracker.FrameQuad? u) ? u : q).ToList();
                hiMs += swg.Elapsed.TotalMilliseconds;
            }
            else if (cand.HiRes != HiResKind.None)
            {
                var swh = Stopwatch.StartNew();
                (int hw, int hh, bool native) = cand.HiRes == HiResKind.Gray2x ? (gw * 2, gh * 2, false) : (sw, sh, true);
                using var stream = new HiResLumaStream(clipPath, fps, hw, hh, native);
                hiBytes = Math.Max(hiBytes, stream.FrameBytes);
                var ordered = s2.OrderBy(q => q.FrameIndex).ToList();
                var updated = new Dictionary<int, ChromaQuadTracker.FrameQuad>();
                foreach (ChromaQuadTracker.FrameQuad q in ordered)
                {
                    hiTel.Frames++;
                    if (q.Interpolated)
                        continue;
                    byte[]? frame = stream.Frame(q.FrameIndex);
                    if (frame is null)
                        break;
                    var field = new BytePlaneField(frame, hw, hh, hw / (double)gw, hh / (double)gh);
                    (double X, double Y)[] c = Cycle(q, gw, gh);
                    EdgeFit.Result? r = EdgeFit.Refine(field, gw, gh, c[0], c[1], c[2], c[3], cand.HiResFit!, hiTel);
                    if (r is { } rr)
                    {
                        updated[q.FrameIndex] = WithCorners(q, rr.Corners, gw, gh);
                        hiTel.FramesFitted++;
                    }
                }

                s2 = s2.Select(q => updated.TryGetValue(q.FrameIndex, out ChromaQuadTracker.FrameQuad? u) ? u : q).ToList();
                hiMs += swh.Elapsed.TotalMilliseconds;
                hiDecodeCpu += stream.CpuSeconds;
            }

            // ---- the rest of the production chain ----------------------------------------------------
            (List<ChromaQuadTracker.FrameQuad> rejected, double noise) = ChromaQuadTracker.RejectImplausibleSamples(s2, fps, gw, gh, options);
            if (rejected.Count == 0)
                continue;
            List<ChromaQuadTracker.FrameQuad> s3 = ChromaQuadTracker.UprightTrackLabels(rejected, gw, gh);
            List<ChromaQuadTracker.FrameQuad> s4 = StageCapture.ProductionSmooth(s3, grid, frames, gw, gh, sw);
            if (best is null || s4.Count > best.S4.Count)
                best = new StageSet(options, tuning, s1, null, s2a, s2, s3, s4, noise, 0);
        }

        StageSet stages = (best ?? new StageSet(options, tuning, s1, null, [], [], [], [], 0, 0)) with { RunCount = runCount };
        return new EdgeRun(stages, s1Tel, hiTel, refitMs, hiMs, hiDecodeCpu, hiBytes);
    }

    /// <summary>Corners in grid pixels, TL, TR, BR, BL (the production <c>Cycle</c>).</summary>
    internal static (double X, double Y)[] Cycle(ChromaQuadTracker.FrameQuad q, int width, int height) =>
    [
        (q.X0 * width - 0.5, q.Y0 * height - 0.5),
        (q.X1 * width - 0.5, q.Y1 * height - 0.5),
        (q.X3 * width - 0.5, q.Y3 * height - 0.5),
        (q.X2 * width - 0.5, q.Y2 * height - 0.5)
    ];

    /// <summary>The quad with new corners (TL, TR, BR, BL in grid pixels); the silhouette surface follows only when it was the flat corners.</summary>
    internal static ChromaQuadTracker.FrameQuad WithCorners(ChromaQuadTracker.FrameQuad q, (double X, double Y)[] c, int width, int height)
    {
        double NX(double x) => (x + 0.5) / width;
        double NY(double y) => (y + 0.5) / height;
        var old = q.Surface;
        bool surfaceWasFlat = old != default
            && Math.Abs(old.X0 - q.X0) < 1e-9 && Math.Abs(old.Y0 - q.Y0) < 1e-9
            && Math.Abs(old.X1 - q.X1) < 1e-9 && Math.Abs(old.Y1 - q.Y1) < 1e-9
            && Math.Abs(old.X2 - q.X2) < 1e-9 && Math.Abs(old.Y2 - q.Y2) < 1e-9
            && Math.Abs(old.X3 - q.X3) < 1e-9 && Math.Abs(old.Y3 - q.Y3) < 1e-9;
        (double, double, double, double, double, double, double, double) surface = surfaceWasFlat
            ? (NX(c[0].X), NY(c[0].Y), NX(c[1].X), NY(c[1].Y), NX(c[3].X), NY(c[3].Y), NX(c[2].X), NY(c[2].Y))
            : old;
        return q with
        {
            X0 = NX(c[0].X),
            Y0 = NY(c[0].Y),
            X1 = NX(c[1].X),
            Y1 = NY(c[1].Y),
            X2 = NX(c[3].X),
            Y2 = NY(c[3].Y),
            X3 = NX(c[2].X),
            Y3 = NY(c[2].Y),
            Surface = surface
        };
    }
}
