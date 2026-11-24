using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;

namespace ReelBolt.WorkflowEngine.Services.VideoGeneration;

/// <summary>
/// What one <c>VideoGenerate</c> step attempt prices against: the model's capability set, plus the
/// vendor's own per-clip quote when the price came from an async oracle rather than a published
/// table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> A provider that publishes no price table —
/// Higgsfield — is priced by <see cref="IVideoGenerationPriceOracle"/>, an authenticated
/// <c>POST /estimate/{endpoint_id}</c> whose answer is a per-clip USD figure. That figure is
/// parameter-specific, so the oracle can only answer for the exact (model, resolution, ratio,
/// duration) a step submits, whereas every existing consumer of pricing speaks
/// <see cref="VideoGenerationCapabilities"/> — a whole range of durations and a table of
/// resolution rates. This type carries BOTH: the capabilities a caller validates and maps against,
/// and the quote the money path must charge.
/// </para>
/// <para>
/// <b>The two arms are not interchangeable, and the difference is deliberate.</b> For a client that
/// is NOT an oracle, <see cref="ResolveAsync"/> takes the FULL published capability set from
/// <see cref="IVideoGenerationClient.Describe"/> and leaves <see cref="Quote"/> null — MiniMax's
/// path, byte-identical to what it always was. For a client that IS an oracle, the capabilities are
/// <see cref="VideoGenerationPriceQuote.ToCapabilities"/>, whose duration range is a POINT at the
/// priced duration and whose resolution/ratio lists hold exactly the one value asked about. Applied
/// to the non-oracle arm that narrowing would be a regression: every plan shot would snap to the
/// step's inline <c>DurationSeconds</c> instead of Short/Medium/Long, and a plan config whose inline
/// duration sits outside the model's range — which the plan path deliberately permits today — would
/// start throwing.
/// </para>
/// <para>
/// <b>Bounded.</b> <see cref="ResolveAsync"/> prices ONE distinct configuration per step attempt, so
/// the oracle's own cache makes a whole plan cost one call, and no hot path — the per-take, per-clip
/// or budget-gate paths — ever reaches the network. Never call the oracle from
/// <see cref="ClipCostUsd"/> or <see cref="EstimateWholePlanUsd"/>; they are pure arithmetic over
/// what <see cref="ResolveAsync"/> already resolved.
/// </para>
/// <para>
/// <b>Fail-closed.</b> An oracle that cannot price throws <see cref="NotSupportedException"/> — the
/// exception <see cref="IVideoGenerationClient.Describe"/> already throws for an unpriceable model —
/// so the model stays unofferable, the reason is surfaced through the executor's existing
/// <c>CONFIG_INVALID</c> mapping, and no ledger row can be written. There is no value of this type
/// that means "free": <see cref="VideoGenerationPriceQuote"/> cannot represent a non-positive price,
/// and the rate-derived arm throws rather than returning <c>0</c>.
/// </para>
/// </remarks>
/// <param name="Capabilities">The capability set a caller validates and maps plan words against.</param>
/// <param name="Quote">
/// The vendor's own quote for this step's configuration when the price came from an oracle;
/// <c>null</c> when it came from the provider's published per-second table.
/// </param>
public sealed record VideoGenerationPricingBasis(
    VideoGenerationCapabilities Capabilities,
    VideoGenerationPriceQuote? Quote = null)
{
    /// <summary>
    /// Resolves the basis for one step attempt: the provider's static table for a client that
    /// publishes one, otherwise the client's async cached oracle for the exact configuration the
    /// step will submit.
    /// </summary>
    /// <remarks>
    /// The arm is chosen by asking the client its own type — the ONE place that decision is made —
    /// exactly as <see cref="VideoGenerationPriceOracleExtensions.QuoteClipAsync"/> does. The static
    /// arm deliberately calls <see cref="IVideoGenerationClient.Describe"/> rather than
    /// <c>QuoteClipAsync</c>: on a non-oracle client the two agree on the price, but only
    /// <c>Describe</c> yields the full published capability set. The extension's static branch would
    /// collapse MiniMax's published duration range to a point and can throw on a plan config whose
    /// inline <c>DurationSeconds</c> is outside the model's range, which today is legal.
    /// </remarks>
    /// <param name="client">The resolved provider client.</param>
    /// <param name="query">The exact configuration this step attempt will submit.</param>
    /// <param name="ct">Cancellation for the whole call, including the oracle's single HTTP attempt.</param>
    /// <returns>The resolved basis.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="query"/> is null.</exception>
    /// <exception cref="NotSupportedException">The provider cannot price this configuration at all.</exception>
    /// <exception cref="ArgumentException">The query is malformed, or the static table has no price for the resolution.</exception>
    public static async Task<VideoGenerationPricingBasis> ResolveAsync(
        IVideoGenerationClient client,
        VideoGenerationPriceQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(query);

        if (client is not IVideoGenerationPriceOracle oracle)
        {
            return new VideoGenerationPricingBasis(client.Describe(query.Model), Quote: null);
        }

        var quote = await oracle.QuoteAsync(query, ct);
        return new VideoGenerationPricingBasis(quote.ToCapabilities(), quote);
    }

    /// <summary>
    /// Whether this basis is oracle-priced, i.e. <see cref="Quote"/> is non-null and the money path
    /// must charge the vendor's own figure rather than a re-multiplied per-second rate.
    /// </summary>
    public bool IsOraclePriced => Quote is not null;

    /// <summary>
    /// The USD cost of ONE clip at <paramref name="resolution"/>, <paramref name="ratio"/> and
    /// <paramref name="durationSeconds"/>.
    /// </summary>
    /// <remarks>
    /// On the rate-derived arm this is <see cref="VideoGenerationPricing.EstimateClipCostUsd"/>
    /// unchanged. On the oracle arm it is <see cref="VideoGenerationPriceQuote.TotalUsd"/> for the
    /// configuration the quote was taken at, and an <see cref="InvalidOperationException"/> for
    /// anything else: a re-multiplied per-second rate would drift from the vendor's own figure, and
    /// substituting a default would be the silent zero the oracle-pricing rule forbids. The comparison is
    /// ordinal, with no case folding and no trimming, matching every other resolution/ratio lookup
    /// in this feature.
    /// </remarks>
    /// <param name="resolution">The resolution token the step will request.</param>
    /// <param name="ratio">The aspect ratio the step will request.</param>
    /// <param name="durationSeconds">The clip duration in seconds.</param>
    /// <returns>A strictly positive USD figure.</returns>
    /// <exception cref="ArgumentNullException"><see cref="Capabilities"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">No quote is held and <paramref name="durationSeconds"/> is not positive.</exception>
    /// <exception cref="ArgumentException">No quote is held and <paramref name="resolution"/> has no price in the table.</exception>
    /// <exception cref="InvalidOperationException">
    /// A quote is held and the requested configuration is not the one it priced.
    /// </exception>
    public decimal ClipCostUsd(string resolution, string ratio, int durationSeconds)
    {
        if (Quote is not { } quote)
        {
            return VideoGenerationPricing.EstimateClipCostUsd(Capabilities, resolution, durationSeconds);
        }

        if (!string.Equals(quote.Resolution, resolution, StringComparison.Ordinal)
            || !string.Equals(quote.Ratio, ratio, StringComparison.Ordinal)
            || quote.DurationSeconds != durationSeconds)
        {
            throw new InvalidOperationException(
                $"Model '{quote.Model}' is priced by the vendor's own quote and was quoted at "
                + $"{quote.Resolution}/{quote.Ratio}/{quote.DurationSeconds}s (${quote.TotalUsd}), so the "
                + $"configuration {resolution}/{ratio}/{durationSeconds}s has no price. An oracle-priced model is "
                + "never charged a re-multiplied per-second rate; price the configuration the step will submit.");
        }

        return quote.TotalUsd;
    }

    /// <summary>
    /// The USD cost of generating an entire shot plan: every shot,
    /// <paramref name="takes"/> times over.
    /// </summary>
    /// <remarks>
    /// On the rate-derived arm this delegates to
    /// <see cref="ShotPlanCostEstimator.EstimateWholePlanUsd"/> verbatim. On the oracle arm it
    /// mirrors that estimator's own guards — null list, <c>takes &lt; 1</c>, a null shot entry, an
    /// unresolvable duration word — and sums <see cref="ClipCostUsd"/> over each shot's snapped
    /// duration, so the whole-plan figure is an exact sum of the vendor's own quotes and
    /// <c>meta.planEstimated</c> stays the whole-plan USD sum converted once to credits.
    /// </remarks>
    /// <param name="resolution">The resolution token the step will request.</param>
    /// <param name="ratio">The aspect ratio the step will request.</param>
    /// <param name="shots">The plan's shots; an empty plan costs <c>0m</c>.</param>
    /// <param name="takes">How many takes of each shot the step will order.</param>
    /// <returns>The summed cost, multiplied by <paramref name="takes"/>.</returns>
    /// <exception cref="ArgumentNullException"><see cref="Capabilities"/> or <paramref name="shots"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="takes"/> is below 1 (oracle arm only).</exception>
    /// <exception cref="ArgumentException">A shot is null or its duration word is unresolvable, or
    /// <paramref name="resolution"/> has no price for this model.</exception>
    /// <exception cref="InvalidOperationException">A per-clip cost is not strictly positive, or a
    /// quoted configuration was not the one priced.</exception>
    public decimal EstimateWholePlanUsd(
        string resolution,
        string ratio,
        IReadOnlyList<ShotPlan> shots,
        int takes)
    {
        if (Quote is null)
        {
            return ShotPlanCostEstimator.EstimateWholePlanUsd(Capabilities, resolution, shots, takes);
        }

        ArgumentNullException.ThrowIfNull(shots);

        // Checked before anything is summed: `takes < 1` means the step would order nothing, so a
        // "0m" answer would read as "this plan is free" and sail through the gate. Same rule and
        // same message shape as ShotPlanCostEstimator's own guard.
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

            int durationSeconds = ShotPlanParameterMapper.SnapDurationSeconds(Capabilities, shot.Duration);
            perTake += ClipCostUsd(resolution, ratio, durationSeconds);
        }

        return perTake * takes;
    }
}
