using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video.Tracking;

/// <summary>
/// Per-target parameter search for <see cref="PlanarTracker"/>: motion model, LK window and
/// pyramid depth are footage properties — a large, textured plate wants the full homography and a
/// small window; a tiny, blurry licence plate wants a similarity and a wide window — so they are
/// measured, not assumed.
///
/// <para>
/// <b>Objective — round-trip consistency.</b> Each candidate tracks the seed forward for up to
/// <see cref="ProbeFrames"/> frames, then tracks the quad it ended on BACK to the seed frame. A
/// correct tracker returns to the seed quad; drift, a slide onto the background or a degenerate
/// model does not. Score is <c>coverage × meanConfidence × exp(−roundTripError / 0.03)</c>, the
/// error being the worst corner's distance as a fraction of the seed quad's diagonal. Ties keep
/// the earlier (cheaper, more constrained-last) candidate so the search is deterministic.
/// </para>
/// </summary>
public static class PlanarTrackerTuner
{
    /// <summary>Frames each way a candidate is probed over.</summary>
    internal const int ProbeFrames = 16;

    internal static readonly TrackMotionModel[] Models =
        [TrackMotionModel.Homography, TrackMotionModel.Affine, TrackMotionModel.Similarity];

    internal static readonly int[] WindowRadii = [5, 7, 10, 13];

    /// <summary>
    /// Pyramid depth is not searched: measured on the synthetic-plate clip, depths 3 and 4 gave
    /// bit-identical tracks for every model and window, so the axis only doubled the search cost.
    /// Window size and motion model are what move the round-trip error (0.0013 to 0.063).
    /// </summary>
    internal static readonly int[] PyramidDepths = [4];

    public const string Objective = "coverage*meanConfidence*exp(-roundTripError/0.03)";

    public static (PlanarTrackerOptions Options, VideoInsertTrackTuning Tuning) Tune(
        GrayFrames frames, int seedFrame, (double X, double Y)[] seedCorners, PlanarTrackerOptions baseOptions)
    {
        // Probe in whichever direction has more footage.
        int direction = frames.Count - 1 - seedFrame >= seedFrame ? +1 : -1;
        int available = direction > 0 ? frames.Count - 1 - seedFrame : seedFrame;
        int probe = Math.Min(ProbeFrames, available);
        double diag = Math.Max(
            PlanarTracker.Dist(seedCorners[0], seedCorners[2]),
            PlanarTracker.Dist(seedCorners[1], seedCorners[3]));

        var candidates = new List<VideoInsertTuningCandidate>();
        PlanarTrackerOptions best = baseOptions;
        double bestScore = -1;

        foreach (TrackMotionModel model in Models)
            foreach (int radius in WindowRadii)
                foreach (int depth in PyramidDepths)
                {
                    PlanarTrackerOptions trial = baseOptions with
                    {
                        Model = model,
                        WindowRadius = radius,
                        PyramidLevels = depth,
                        MaxFramesPerDirection = probe
                    };

                    double coverage = 0, meanConfidence = 0, roundTrip = 1;
                    if (probe > 0)
                    {
                        List<TrackedQuad> outbound = PlanarTracker.TrackDirection(frames, seedFrame, seedCorners, trial, direction);
                        coverage = outbound.Count / (double)probe;
                        if (outbound.Count > 0)
                        {
                            meanConfidence = outbound.Average(q => q.Confidence);
                            TrackedQuad end = outbound[^1];
                            List<TrackedQuad> back = PlanarTracker.TrackDirection(
                                frames, end.FrameIndex, end.Corners,
                                trial with { MaxFramesPerDirection = Math.Abs(end.FrameIndex - seedFrame) }, -direction);
                            TrackedQuad? home = back.FirstOrDefault(q => q.FrameIndex == seedFrame);
                            roundTrip = home is null || diag <= 0
                                ? 1
                                : Enumerable.Range(0, 4).Max(i => PlanarTracker.Dist(home.Corners[i], seedCorners[i])) / diag;
                        }
                    }
                    else
                    {
                        // A single-frame clip has nothing to measure; every candidate ties.
                        coverage = 1;
                        meanConfidence = 1;
                        roundTrip = 0;
                    }

                    double score = coverage * meanConfidence * Math.Exp(-roundTrip / 0.03);
                    var parameters = new Dictionary<string, double>
                    {
                        ["model"] = (int)model,
                        ["windowRadius"] = radius,
                        ["pyramidLevels"] = depth
                    };
                    candidates.Add(new VideoInsertTuningCandidate(
                        parameters,
                        Math.Round(score, 4),
                        new Dictionary<string, double>
                        {
                            ["coverage"] = Math.Round(coverage, 4),
                            ["meanConfidence"] = Math.Round(meanConfidence, 4),
                            ["roundTripError"] = Math.Round(roundTrip, 5)
                        }));

                    if (score > bestScore + 1e-9)
                    {
                        bestScore = score;
                        best = baseOptions with { Model = model, WindowRadius = radius, PyramidLevels = depth };
                    }
                }

        var tuning = new VideoInsertTrackTuning(
            Objective,
            new Dictionary<string, double>
            {
                ["model"] = (int)best.Model,
                ["windowRadius"] = best.WindowRadius,
                ["pyramidLevels"] = best.PyramidLevels
            },
            Math.Round(Math.Max(0, bestScore), 4),
            candidates,
            $"{best.Model.ToString().ToLowerInvariant()}, {best.WindowRadius * 2 + 1}px window, {best.PyramidLevels} pyramid levels");
        return (best, tuning);
    }
}
