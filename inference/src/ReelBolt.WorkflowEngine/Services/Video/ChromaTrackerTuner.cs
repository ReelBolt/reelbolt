using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Per-source parameter search for <see cref="ChromaQuadTracker"/>: tries every candidate value
/// of the tracker's footage-dependent knobs against a sample of the clip's own frames, scores each
/// on evidence the frames themselves provide, and hands the winner to the full tracking pass.
///
/// <para>
/// <b>Why search instead of a constant.</b> Hysteresis growth exists because a dim, unevenly lit
/// plate is only partly caught by the strict mask; but how far growth may reach is a property of
/// the footage. A dim laptop panel needs it to reach well down in purity, a bright phone screen in
/// front of a lawn needs it held high or the plate floods into the grass. Any single value is
/// right for one and wrong for the other, so the tracker measures instead of guessing.
/// </para>
///
/// <para>
/// <b>The objective</b> has to reward a fit for being RIGHT, not for being conservative — "grow
/// less" always looks safer by any measure computed from the segmentation itself (fill, chroma
/// margin), and that is exactly what returned a confidently-wrong 66% fit on the laptop. So it
/// asks the image instead, on two independent axes:
/// <list type="bullet">
/// <item><b>Edge contrast</b> — a real plate's boundary is a physical edge: plate on the inside,
/// bezel or scenery on the outside. Sampled just inside and just outside each fitted edge, the
/// chroma purity difference is high only when the edge sits on that boundary. A fit that leaked
/// into foliage has foliage on both sides of its far edge; one that under-grew a dim plate has
/// plate on both sides. Both score low; only the correct fit scores high.</item>
/// <item><b>Temporal consistency</b> — <see cref="ChromaQuadTracker.TemporalConsistency"/> over
/// contiguous blocks of frames: a physical plate's corners move continuously, a leak flickers.</item>
/// </list>
/// The score is <c>detectionRate × edgeContrast × consistency</c>, ties going to the candidate
/// earlier in <see cref="GrowCandidates"/> (the more conservative one) so the search is
/// deterministic.
/// </para>
/// </summary>
public static class ChromaTrackerTuner
{
    /// <summary>Name the growth bar is recorded under in <see cref="VideoInsertTrackTuning.Parameters"/>.</summary>
    public const string GrowExcessFractionKey = "growExcessFraction";

    /// <summary>Objective name recorded on the tuning result.</summary>
    public const string Objective = "detectionRate*edgeContrast*temporalConsistency";

    /// <summary>
    /// Growth purity bars searched, as fractions of each seed's own median purity. Ordered most
    /// to least conservative so ties resolve toward less growth. <c>1.5</c> is effectively "no
    /// growth" (nothing in a seed's neighbourhood beats 1.5x its median), kept so footage where
    /// the strict core already is the whole plate can say so.
    /// </summary>
    internal static readonly double[] GrowCandidates = [1.5, 0.8, 0.65, 0.5, 0.4, 0.3, 0.2, 0.0];

    /// <summary>Contiguous frames per evaluation block — long enough for the consistency window to mean something.</summary>
    internal const int BlockFrames = 18;

    /// <summary>Evaluation blocks spread evenly across the clip.</summary>
    internal const int MaxBlocks = 4;

    /// <summary>Points sampled along each fitted edge for the contrast measurement.</summary>
    private const int EdgeSamples = 12;

    /// <summary>
    /// How far inside/outside the fitted edge (in grid pixels) contrast is searched: the purest
    /// pixel within this band inside and the least pure within it outside. Wide enough to clear
    /// the blend band and a rounded corner's inset, narrow enough that a fit cut through the
    /// middle of a dim plate cannot reach the real boundary and borrow its contrast.
    /// </summary>
    private const int EdgeProbeBandPx = 4;

    /// <summary>
    /// Runs the search over sampled blocks of the clip and returns the winning parameters with
    /// every candidate's score. Never throws on degenerate input: with nothing to measure it
    /// returns <see cref="ChromaQuadTracker.DefaultGrowExcessFraction"/> at score 0.
    /// </summary>
    public static VideoInsertTrackTuning Tune(
        byte[] pixelData, int frameCount, int width, int height, ChromaQuadTracker.Options options)
    {
        List<int[]> blocks = SampleBlocks(frameCount, pixelData.Length / Math.Max(1, width * height * 3));
        string color = ChromaQuadTracker.NormalizeColorName(options.ColorName);

        var candidates = new List<VideoInsertTuningCandidate>(GrowCandidates.Length);
        double bestScore = -1;
        double bestValue = ChromaQuadTracker.DefaultGrowExcessFraction;

        foreach (double candidate in GrowCandidates)
        {
            ChromaQuadTracker.Options trial = options with
            {
                GrowExcessFraction = candidate,
                MaxPlatesPerFrame = 1,
                MeasureCurvature = false,
                SolveChromaKey = false
            };

            int sampled = 0, detected = 0;
            double contrastSum = 0, consistencySum = 0;
            int consistencyBlocks = 0;

            foreach (int[] block in blocks)
            {
                var run = new List<ChromaQuadTracker.FrameQuad>(block.Length);
                foreach (int f in block)
                {
                    sampled++;
                    ChromaQuadTracker.FrameQuad? q = ChromaQuadTracker.DetectQuad(
                        pixelData, f * width * height * 3, width, height, f, trial);
                    if (q is null)
                        continue;

                    detected++;
                    contrastSum += EdgeContrast(pixelData, f * width * height * 3, width, height, color, q);
                    run.Add(q);
                }

                if (run.Count > 0)
                {
                    consistencySum += ChromaQuadTracker.TemporalConsistency(run);
                    consistencyBlocks++;
                }
            }

            double detectionRate = sampled == 0 ? 0 : detected / (double)sampled;
            double edgeContrast = detected == 0 ? 0 : contrastSum / detected;
            double consistency = consistencyBlocks == 0 ? 0 : consistencySum / consistencyBlocks;
            double score = detectionRate * edgeContrast * consistency;

            candidates.Add(new VideoInsertTuningCandidate(
                new Dictionary<string, double> { [GrowExcessFractionKey] = candidate },
                Math.Round(score, 4),
                new Dictionary<string, double>
                {
                    ["detectionRate"] = Math.Round(detectionRate, 4),
                    ["edgeContrast"] = Math.Round(edgeContrast, 4),
                    ["temporalConsistency"] = Math.Round(consistency, 4)
                }));

            // Strictly greater: an equal score keeps the earlier, more conservative candidate.
            if (score > bestScore + 1e-9)
            {
                bestScore = score;
                bestValue = candidate;
            }
        }

        return new VideoInsertTrackTuning(
            Objective,
            new Dictionary<string, double> { [GrowExcessFractionKey] = bestValue },
            Math.Round(Math.Max(0, bestScore), 4),
            candidates,
            $"growth purity bar {bestValue:0.##}x the plate's own");
    }

    /// <summary>
    /// Up to <see cref="MaxBlocks"/> contiguous runs of <see cref="BlockFrames"/> frames, evenly
    /// spread. A short clip collapses into one block covering every frame.
    /// </summary>
    internal static List<int[]> SampleBlocks(int frameCount, int framesInBuffer)
    {
        int usable = Math.Min(frameCount, framesInBuffer);
        if (usable <= 0)
            return [];

        if (usable <= BlockFrames * MaxBlocks)
        {
            int blockCount = Math.Max(1, (int)Math.Ceiling(usable / (double)BlockFrames));
            return Enumerable.Range(0, blockCount)
                .Select(b => Enumerable.Range(b * BlockFrames, Math.Min(BlockFrames, usable - b * BlockFrames)).ToArray())
                .Where(b => b.Length > 0)
                .ToList();
        }

        var blocks = new List<int[]>(MaxBlocks);
        double spacing = (usable - BlockFrames) / (double)(MaxBlocks - 1);
        for (int b = 0; b < MaxBlocks; b++)
        {
            int start = (int)Math.Round(b * spacing);
            blocks.Add(Enumerable.Range(start, BlockFrames).ToArray());
        }

        return blocks;
    }

    /// <summary>
    /// Mean, over points along the four fitted edges, of (purity just inside − purity just
    /// outside), normalized by the purity at the quad's centre and clamped to 0..1. Edges are
    /// sampled away from the corners (10%..90%) so a rounded-corner screen's corner arcs do not
    /// read as missing contrast.
    /// </summary>
    internal static double EdgeContrast(
        byte[] pixelData, int frameOffset, int width, int height, string color, ChromaQuadTracker.FrameQuad q)
    {
        (double X, double Y)[] corners =
        [
            (q.X0 * width - 0.5, q.Y0 * height - 0.5),
            (q.X1 * width - 0.5, q.Y1 * height - 0.5),
            (q.X3 * width - 0.5, q.Y3 * height - 0.5),
            (q.X2 * width - 0.5, q.Y2 * height - 0.5)
        ]; // TL, TR, BR, BL — perimeter order

        double cx = corners.Average(c => c.X), cy = corners.Average(c => c.Y);
        double reference = Math.Max(0.05, Purity(cx, cy));

        double sum = 0;
        int count = 0;
        for (int e = 0; e < 4; e++)
        {
            (double ax, double ay) = corners[e];
            (double bx, double by) = corners[(e + 1) % 4];
            double ex = bx - ax, ey = by - ay;
            double len = Math.Sqrt(ex * ex + ey * ey);
            if (len < 1e-6)
                continue;

            // Unit normal, flipped to point away from the centroid.
            double nx = -ey / len, ny = ex / len;
            double mx = (ax + bx) / 2, my = (ay + by) / 2;
            if (nx * (mx - cx) + ny * (my - cy) < 0)
            {
                nx = -nx;
                ny = -ny;
            }

            for (int i = 0; i < EdgeSamples; i++)
            {
                double t = 0.1 + 0.8 * i / (EdgeSamples - 1);
                double px = ax + ex * t, py = ay + ey * t;
                // Best contrast within a small band either side: the fitted line of a
                // rounded-corner screen sits a pixel or two inside the true edge, and a single
                // fixed probe distance lands on plate on BOTH sides of it.
                double inside = 0, outside = 1;
                for (int d = 1; d <= EdgeProbeBandPx; d++)
                {
                    inside = Math.Max(inside, Purity(px - nx * d, py - ny * d));
                    outside = Math.Min(outside, Purity(px + nx * d, py + ny * d));
                }
                sum += Math.Clamp((inside - outside) / reference, 0, 1);
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;

        double Purity(double x, double y)
        {
            int ix = Math.Clamp((int)Math.Round(x), 0, width - 1);
            int iy = Math.Clamp((int)Math.Round(y), 0, height - 1);
            int o = frameOffset + (iy * width + ix) * 3;
            return Math.Max(0, ChromaQuadTracker.ChromaExcess(color, pixelData[o], pixelData[o + 1], pixelData[o + 2]));
        }
    }
}
