using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// One scored candidate moment for a thumbnail, resolved entirely from a <c>VideoAnalyze</c>
/// artifact's own measurements. <see cref="Scores"/> carries the individual terms so the step's
/// output can explain WHY this frame was chosen rather than asserting it.
/// </summary>
/// <param name="ShotId">The artifact's shot id (<c>s{n}</c>) this candidate came from.</param>
/// <param name="SourceIndex">Which analysed source clip the shot belongs to.</param>
/// <param name="AtSec">The moment to extract, in seconds on that source's own timeline.</param>
/// <param name="Score">The composite score, 0..1 — the ranking key.</param>
/// <param name="Scores">The named terms <see cref="Score"/> is composed of.</param>
/// <param name="DurationSec">The shot's own length.</param>
/// <param name="Sharpness">The shot's measured sharpness, when visual analysis ran.</param>
/// <param name="IsBestTake">True when the analyze step marked this shot its duplicate group's best take.</param>
/// <param name="DuplicateGroupId">The duplicate group this shot belongs to, if any.</param>
/// <param name="GroupRank">Rank within that group (0 is best), if any.</param>
/// <param name="Reason">A short human-readable line naming why this candidate outranks the others.</param>
public sealed record ThumbnailCandidate(
    string ShotId,
    int SourceIndex,
    double AtSec,
    double Score,
    IReadOnlyDictionary<string, double> Scores,
    double DurationSec,
    double? Sharpness,
    bool IsBestTake,
    string? DuplicateGroupId,
    int? GroupRank,
    string Reason);

/// <summary>
/// Picks WHICH moment of a video becomes its thumbnail, from the <c>VideoAnalyze</c> artifact alone.
/// Pure, deterministic, no I/O and no model — the same discipline as
/// <see cref="KeyframeSelector"/> and <see cref="FrameGridAnalyzer"/>, and for the same reason: the
/// interesting part of "choose a good cover frame" is a rule, and a rule can be tested directly.
/// <para>
/// <b>Why this reuses the analyze step rather than measuring anything itself.</b> The analyze step
/// has already decoded the source, split it into shots, and measured every one of them: take quality
/// (<see cref="FrameGridAnalyzer.ComputeTakeQuality"/>), sharpness, brightness, motion, stillness
/// windows, and which shots are near-duplicate retakes of each other. Re-deriving any of that here
/// would be a second implementation that could disagree with the first, and would decode the footage
/// a second time for no gain. This type's whole job is to turn measurements that already exist into
/// an ordering.
/// </para>
/// <para>
/// <b>The strategy, stated so it can be argued with.</b> Score each eligible shot on the analyze
/// step's own take-quality composite, then:
/// </para>
/// <list type="number">
/// <item>Drop shots shorter than the configured floor — a 0.2 s shot is usually a flash frame or a
/// transition artefact, not a scene.</item>
/// <item>Prefer a duplicate group's designated best take, and demote the other members of the same
/// group. Ten retakes of one setup should not fill the candidate list with the same picture.</item>
/// <item>Add a stillness bonus for a shot that has a calm window at all, and sample the frame from
/// the MIDDLE of its longest such window (<see cref="KeyframeSelector.ChooseKeyframeSec"/>). A calm
/// moment makes a cleaner, less motion-blurred frame than a point picked arbitrarily.</item>
/// <item>Break ties toward the earlier shot. A cover that opens the video is the conventional and
/// safer choice, and it makes the ordering total, so the same artifact always yields the same
/// answer.</item>
/// </list>
/// </summary>
public static class ThumbnailCandidateSelector
{
    /// <summary>Bonus a shot receives for being its duplicate group's designated best take.</summary>
    public const double BestTakeBonus = 0.15;

    /// <summary>Penalty a shot receives for being a non-best member of a duplicate group.</summary>
    public const double NonBestTakePenalty = 0.25;

    /// <summary>Bonus a shot receives for having at least one measured still window.</summary>
    public const double StillnessBonus = 0.10;

    /// <summary>Tie-break weight favouring earlier shots; small on purpose — it must never outrank a real quality difference.</summary>
    public const double EarlierIsBetterWeight = 0.02;

    /// <summary>
    /// Ranks every eligible shot of <paramref name="artifact"/>, best first, capped at
    /// <paramref name="maxCandidates"/>. Never throws for a thin or degraded artifact: a shot with no
    /// visual analysis at all still scores (through the analyze step's own neutral placeholders), so
    /// an artifact produced with visuals off still yields a usable thumbnail.
    /// </summary>
    public static IReadOnlyList<ThumbnailCandidate> Rank(
        VideoAnalysisArtifact artifact,
        int maxCandidates,
        double minShotSeconds,
        bool preferBestTake,
        int? onlySourceIndex = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        int cap = Math.Clamp(maxCandidates, 1, 500);
        double floor = Math.Max(0, minShotSeconds);

        List<VideoAnalysisShot> eligible = artifact.Shots
            .Where(s => s is not null)
            .Where(s => onlySourceIndex is null || s.SourceIndex == onlySourceIndex.Value)
            .Where(s => (s.EndSec - s.StartSec) >= floor)
            .ToList();

        if (eligible.Count == 0)
            return [];

        double firstStart = eligible.Min(s => s.StartSec);
        double lastEnd = eligible.Max(s => s.EndSec);
        double span = Math.Max(0, lastEnd - firstStart);

        var scored = new List<ThumbnailCandidate>(eligible.Count);
        foreach (VideoAnalysisShot shot in eligible)
        {
            double duration = Math.Max(0, shot.EndSec - shot.StartSec);
            VideoAnalysisShotVisual? visual = shot.Visual;

            double takeQuality = FrameGridAnalyzer.ComputeTakeQuality(
                visual?.MotionStdDev ?? 0,
                shot.Audio?.RmsDbfs,
                visual?.BrightnessMean ?? 0.5,
                duration,
                visual?.Sharpness);

            double bestTake = 0;
            if (visual is not null)
            {
                if (visual.IsBestTake)
                    bestTake = BestTakeBonus;
                else if (preferBestTake && visual.GroupRank is > 0)
                    bestTake = -NonBestTakePenalty;
            }

            bool hasStillWindow = (visual?.StillWindows?.Count ?? 0) > 0;
            double stillness = hasStillWindow ? StillnessBonus : 0;

            // A shot near the front of the piece reads as the cover more often than one at the end.
            double earliest = span > 0 ? 1 - Normalize(shot.StartSec - firstStart, 0, span) : 1;
            double recency = EarlierIsBetterWeight * earliest;

            double score = Math.Clamp(takeQuality + bestTake + stillness + recency, 0, 1);

            var terms = new Dictionary<string, double>
            {
                ["takeQuality"] = Math.Round(takeQuality, 4),
                ["bestTake"] = bestTake,
                ["stillness"] = stillness,
                ["earliest"] = Math.Round(recency, 4)
            };

            scored.Add(new ThumbnailCandidate(
                shot.Id,
                shot.SourceIndex,
                KeyframeSelector.ChooseKeyframeSec(shot),
                Math.Round(score, 4),
                terms,
                Math.Round(duration, 3),
                visual?.Sharpness,
                visual?.IsBestTake ?? false,
                visual?.DuplicateGroupId,
                visual?.GroupRank,
                Describe(visual, hasStillWindow, takeQuality)));
        }

        return scored
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.AtSec)
            .ThenBy(c => c.ShotId, StringComparer.Ordinal)
            .Take(cap)
            .ToList();
    }

    /// <summary>
    /// The single best candidate, or null when the artifact has no shot long enough to qualify. Callers
    /// that want to explain the choice should use <see cref="Rank"/> and take the head, which is
    /// exactly what this does.
    /// </summary>
    public static ThumbnailCandidate? Best(
        VideoAnalysisArtifact artifact,
        int maxCandidates,
        double minShotSeconds,
        bool preferBestTake)
        => Rank(artifact, maxCandidates, minShotSeconds, preferBestTake).FirstOrDefault();

    /// <summary>
    /// A one-line, first-party explanation of a shot's standing — assembled from the same measured
    /// facts the score is, so the step output can say "the flattest, stillest, best take" without a
    /// model writing prose about a number.
    /// </summary>
    private static string Describe(VideoAnalysisShotVisual? visual, bool hasStillWindow, double takeQuality)
    {
        if (visual is null)
            return "no visual analysis for this shot; scored on duration alone";

        var parts = new List<string>(4);
        if (visual.IsBestTake)
            parts.Add("best take of its group");
        else if (visual.GroupRank is > 0)
            parts.Add($"take {visual.GroupRank + 1} of its group");

        if (hasStillWindow)
            parts.Add("has a calm window");
        if (visual.Sharpness is { } sharpness)
            parts.Add($"sharpness {sharpness:0.###}");
        if (visual.BrightnessMean is var brightness && (brightness < 0.15 || brightness > 0.85))
            parts.Add($"exposure is off ({brightness:0.##})");

        parts.Add($"take quality {takeQuality:0.###}");
        return string.Join(", ", parts);
    }

    private static double Normalize(double value, double min, double max)
    {
        if (max <= min)
            return 0;
        return Math.Clamp((value - min) / (max - min), 0, 1);
    }
}
