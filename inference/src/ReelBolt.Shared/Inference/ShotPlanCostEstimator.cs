using ReelBolt.Shared.Data.OutputSchemas;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Prices a whole <c>ShotDirector</c> plan before a single clip is submitted, so the step's
/// existing spend gate can refuse an unaffordable plan as a whole instead of discovering it
/// half-way through — after some clips have already been paid for and cannot be un-paid.
/// </summary>
/// <remarks>
/// <para>
/// WHY this delegates instead of carrying a price table: <see cref="VideoGenerationPricing"/> is the
/// ONE pricing path in the codebase, and a second table would drift from it silently — the estimate
/// is what the budget gate charges against every cap, so two tables that disagree by anything at
/// all means the gate and the ledger disagree about what was spent. This class adds only the
/// plan-level arithmetic (which duration each word snaps to, and how many takes of each shot); the
/// per-clip price, the resolution lookup and the non-positive-estimate guard all stay owned by
/// <see cref="VideoGenerationPricing"/>.
/// </para>
/// <para>
/// Fail-closed by construction: an unpriceable resolution or an unresolvable duration word throws
/// rather than contributing <c>0</c>, because a zero estimate passes every cap and, once persisted,
/// credits the daily ledger with spend that never happened. An EMPTY plan is the one legitimate
/// zero — there is genuinely nothing to buy.
/// </para>
/// </remarks>
public static class ShotPlanCostEstimator
{
    /// <summary>
    /// The USD cost of generating the entire plan: every shot, <paramref name="takes"/> times over.
    /// </summary>
    /// <param name="capabilities">The resolved model's capabilities (<c>Describe(model)</c>).</param>
    /// <param name="resolution">The resolution token the step will request.</param>
    /// <param name="shots">The plan's shots; an empty plan costs <c>0m</c>.</param>
    /// <param name="takes">How many takes of each shot the step will order (its <c>Takes</c> setting).</param>
    /// <returns>The summed estimate, multiplied by <paramref name="takes"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> or <paramref name="shots"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="takes"/> is below 1.</exception>
    /// <exception cref="ArgumentException">A shot's duration word is unresolvable, or
    /// <paramref name="resolution"/> has no price for this model.</exception>
    /// <exception cref="InvalidOperationException">A per-clip price is not strictly positive.</exception>
    public static decimal EstimateWholePlanUsd(
        VideoGenerationCapabilities capabilities,
        string resolution,
        IReadOnlyList<ShotPlan> shots,
        int takes)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(shots);

        // Checked before anything is summed: `takes < 1` means the step would order nothing, so a
        // "0m" answer would read as "this plan is free" and sail through the gate. It is a config
        // fault and belongs at the surface, not in the ledger.
        if (takes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(takes), takes, "takes must be at least 1; a plan is never priced as free.");
        }

        decimal perTake = 0m;
        for (int i = 0; i < shots.Count; i++)
        {
            var shot = shots[i];
            if (shot is null)
            {
                throw new ArgumentException(
                    $"The plan's shot at index {i} is null; an unreadable shot cannot be priced and "
                    + "must not be counted as free.",
                    nameof(shots));
            }

            int durationSeconds = ShotPlanParameterMapper.SnapDurationSeconds(capabilities, shot.Duration);
            perTake += VideoGenerationPricing.EstimateClipCostUsd(capabilities, resolution, durationSeconds);
        }

        return perTake * takes;
    }
}
