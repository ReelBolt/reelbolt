namespace ReelBolt.Shared.Moderation;

/// <summary>
/// Turns a <see cref="ModerationClassification"/> into an allow / review / block verdict using the
/// configured thresholds. Deterministic and vendor-agnostic, so the API, the engine and the tests
/// agree on what "flagged enough" means.
/// </summary>
/// <remarks>
/// <para>
/// Order of checks, fixed by the plan:
/// <list type="number">
///   <item><description>any configured hard-block category is flagged, or scores at or above the
///   review threshold, blocks unconditionally;</description></item>
///   <item><description>the maximum score at or above <see cref="ContentModerationOptions.BlockThreshold"/>
///   blocks;</description></item>
///   <item><description>the maximum score at or above <see cref="ContentModerationOptions.ReviewThreshold"/>
///   is a review (which blocks nothing);</description></item>
///   <item><description>a <c>flagged</c> result with no score above our thresholds is still a
///   review — never an allow;</description></item>
///   <item><description>a response with neither scores nor flags is
///   <see cref="ModerationAction.Unavailable"/>, never an allow;</description></item>
///   <item><description>everything else allows.</description></item>
/// </list>
/// </para>
/// <para>
/// The thresholds are clamped at this consumer: block/review into <c>[0,1]</c> and review never
/// above block, so a configuration typo cannot make the mid band swallow the block band.
/// </para>
/// </remarks>
public static class ModerationPolicy
{
    public static ModerationPolicyVerdict Evaluate(
        ModerationClassification classification,
        ContentModerationOptions options,
        ModerationContentKind kind)
    {
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(options);

        // A non-positive threshold is a configuration error, not "block everything": fall back to
        // the shipped default. Values above 1 are impossible to reach and clamp to 1.
        double blockThreshold = options.BlockThreshold > 0
            ? Math.Min(options.BlockThreshold, 1d)
            : 0.85d;
        double reviewThreshold = options.ReviewThreshold > 0
            ? Math.Min(options.ReviewThreshold, blockThreshold)
            : Math.Min(0.50d, blockThreshold);

        string? topCategory = null;
        double topScore = 0d;
        foreach ((string category, double score) in classification.Scores)
        {
            if (topCategory is null || score > topScore
                || (score == topScore && string.CompareOrdinal(category, topCategory) < 0))
            {
                topCategory = category;
                topScore = score;
            }
        }

        foreach (string hardBlock in options.EffectiveHardBlockCategories)
        {
            bool flagged = classification.FlaggedCategories.TryGetValue(hardBlock, out bool isFlagged) && isFlagged;
            bool scoresHigh = classification.Scores.TryGetValue(hardBlock, out double score) && score >= reviewThreshold;
            if (flagged || scoresHigh)
                return Verdict(ModerationAction.Block, hardBlock, topScore, kind);
        }

        if (topScore >= blockThreshold)
            return Verdict(ModerationAction.Block, topCategory, topScore, kind);
        if (topScore >= reviewThreshold)
            return Verdict(ModerationAction.Review, topCategory, topScore, kind);

        // A flagged result is never "clean": the model's own threshold fired even if no score sits
        // above ours (or it returned no usable scores at all). It goes to the review band, which
        // writes an audit row and blocks nothing. A hard-block flag was already handled above.
        bool anyCategoryFlagged = classification.Flagged
            || classification.FlaggedCategories.Values.Any(flagged => flagged);
        if (anyCategoryFlagged)
            return Verdict(ModerationAction.Review, topCategory, topScore, kind);

        // No scores and no flags is not a classification: treating it as Allow would make an empty
        // or malformed results[] array look clean. The gate maps Unavailable to its fail-open /
        // fail-closed stance; the publish surface must refuse it.
        if (classification.Scores.Count == 0)
            return new ModerationPolicyVerdict(
                ModerationAction.Unavailable,
                TopCategory: null,
                TopScore: 0,
                ReasonCode: ModerationReasonCodes.Unclassified,
                UserMessage: string.Empty);

        return new ModerationPolicyVerdict(
            ModerationAction.Allow,
            topCategory,
            topScore,
            ReasonCode: null,
            UserMessage: string.Empty);
    }

    private static ModerationPolicyVerdict Verdict(
        ModerationAction action,
        string? topCategory,
        double topScore,
        ModerationContentKind kind) =>
        new(action, topCategory, topScore, ReasonCode: topCategory, ModerationReasons.For(topCategory, kind));
}
