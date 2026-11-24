namespace ReelBolt.Shared.Inference;

/// <summary>
/// One clip's priced identity: everything an <c>/estimate</c> call is parameterised by. The four
/// values are exactly the ones a <c>VideoGenerate</c> step submits, so a quote is only ever valid
/// for the configuration it was asked about.
/// </summary>
/// <param name="Model">
/// The provider's model identity — for Higgsfield the vendor's per-workflow <b>Endpoint ID</b>, which
/// is what <c>POST /estimate/{endpoint_id}</c> is addressed by.
/// </param>
/// <param name="Resolution">The step's resolution token (the key the shared price table is indexed by).</param>
/// <param name="Ratio">The step's aspect ratio.</param>
/// <param name="DurationSeconds">The clip duration in seconds; must be positive.</param>
public sealed record VideoGenerationPriceQuery(
    string Model,
    string Resolution,
    string Ratio,
    int DurationSeconds);

/// <summary>Where a <see cref="VideoGenerationPriceQuote"/> came from.</summary>
public enum VideoGenerationPriceSource
{
    /// <summary>
    /// Derived from the provider's own published, compiled-in price table
    /// (<see cref="IVideoGenerationClient.Describe"/>) — no network, ever. MiniMax answers this way.
    /// </summary>
    StaticTable,

    /// <summary>
    /// The vendor's authenticated price oracle. One bounded, cached network call to
    /// <c>POST /estimate/{endpoint_id}</c>, which is the ONLY authoritative price for a provider that
    /// publishes no table (Higgsfield).
    /// </summary>
    VendorEstimate
}

/// <summary>
/// The price of ONE clip at ONE exact configuration.
/// <para>
/// <b>There is no value of this type that means "free" or "unknown".</b> The constructor rejects a
/// non-positive <see cref="TotalUsd"/> or <see cref="PricePerSecondUsd"/>, and a provider that cannot
/// price a configuration throws instead of returning one. That restates
/// <see cref="VideoGenerationPricing"/>'s fail-closed invariant on the type itself, so an oracle
/// failure can never be mistaken for a <c>$0</c> price anywhere the value travels — which is the
/// "no silent price of zero" condition of the oracle-pricing rule, enforced structurally rather than by
/// convention.
/// </para>
/// </summary>
public sealed record VideoGenerationPriceQuote
{
    /// <summary>
    /// Creates a quote. Every argument is validated: there is deliberately no way to construct an
    /// unpriced, zero-priced or negative-priced quote.
    /// </summary>
    /// <param name="model">The model/endpoint id this quote was asked about.</param>
    /// <param name="resolution">The resolution token this quote was asked about.</param>
    /// <param name="ratio">The aspect ratio this quote was asked about.</param>
    /// <param name="durationSeconds">The clip duration this quote was asked about; must be positive.</param>
    /// <param name="pricePerSecondUsd">
    /// The derived per-second rate. For <see cref="VideoGenerationPriceSource.StaticTable"/> this IS
    /// the table's own value; for <see cref="VideoGenerationPriceSource.VendorEstimate"/> it is
    /// <paramref name="totalUsd"/> divided by <paramref name="durationSeconds"/>, so the shared
    /// <see cref="VideoGenerationPricing"/> path can be reused unchanged. The vendor's own number is
    /// always <paramref name="totalUsd"/>, never this.
    /// </param>
    /// <param name="totalUsd">
    /// The vendor's OWN total price for this clip, in USD, unmodified. This — not a re-multiplied
    /// per-second rate — is what a budget gate must charge.
    /// </param>
    /// <param name="credits">The vendor's credit figure, when it reports one (Higgsfield does); <c>null</c> otherwise.</param>
    /// <param name="source">Which of the two pricing paths produced this quote.</param>
    /// <exception cref="ArgumentException">A string argument is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="durationSeconds"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="totalUsd"/> or <paramref name="pricePerSecondUsd"/> is not strictly positive.</exception>
    public VideoGenerationPriceQuote(
        string model,
        string resolution,
        string ratio,
        int durationSeconds,
        decimal pricePerSecondUsd,
        decimal totalUsd,
        int? credits,
        VideoGenerationPriceSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolution);
        ArgumentException.ThrowIfNullOrWhiteSpace(ratio);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationSeconds);

        if (totalUsd <= 0m)
        {
            throw new InvalidOperationException(
                $"A price quote is never free or unknown; got a total of {totalUsd} for model "
                + $"'{model}' at {durationSeconds}s. An unpriceable configuration must throw instead.");
        }

        if (pricePerSecondUsd <= 0m)
        {
            throw new InvalidOperationException(
                $"A price quote is never free; got {pricePerSecondUsd}/s for model '{model}'. An "
                + "unpriceable configuration must throw instead.");
        }

        Model = model;
        Resolution = resolution;
        Ratio = ratio;
        DurationSeconds = durationSeconds;
        PricePerSecondUsd = pricePerSecondUsd;
        TotalUsd = totalUsd;
        Credits = credits;
        Source = source;
    }

    /// <summary>The model/endpoint id this quote was asked about.</summary>
    public string Model { get; init; }

    /// <summary>The resolution token this quote was asked about.</summary>
    public string Resolution { get; init; }

    /// <summary>The aspect ratio this quote was asked about.</summary>
    public string Ratio { get; init; }

    /// <summary>The clip duration this quote was asked about.</summary>
    public int DurationSeconds { get; init; }

    /// <summary>The derived per-second rate; see the constructor for what it is and is not.</summary>
    public decimal PricePerSecondUsd { get; init; }

    /// <summary>The vendor's own total price for this clip, in USD. Always strictly positive.</summary>
    public decimal TotalUsd { get; init; }

    /// <summary>The vendor's credit figure when it reports one, else <c>null</c>.</summary>
    public int? Credits { get; init; }

    /// <summary>Which pricing path produced this quote.</summary>
    public VideoGenerationPriceSource Source { get; init; }

    /// <summary>
    /// Re-expresses this quote as <see cref="VideoGenerationCapabilities"/> so callers that already
    /// speak that type — <c>VideoGenerateStepConfig.Validate(capabilities)</c>,
    /// <see cref="ShotPlanCostEstimator"/> — keep working without a signature change.
    /// <para>
    /// <b>The duration range is a point, and deliberately so.</b> Min and Max duration are both the
    /// duration this quote was actually taken at, and the resolution and ratio lists each contain
    /// exactly the one value asked about. That is the honest statement of what was priced: a provider
    /// with no published capability data is offerable at precisely the configuration its oracle
    /// answered for, and at nothing else. Widening the range here would be inventing a vendor fact the
    /// oracle never supplied — the failure mode this path exists to prevent — and narrowing it further
    /// is impossible.
    /// </para>
    /// </summary>
    public VideoGenerationCapabilities ToCapabilities()
    {
        return new VideoGenerationCapabilities(
            PricePerSecondByResolution: new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                [Resolution] = PricePerSecondUsd
            },
            MinDurationSeconds: DurationSeconds,
            MaxDurationSeconds: DurationSeconds,
            AllowedRatios: new[] { Ratio });
    }
}

/// <summary>
/// The ASYNC, CACHED price oracle — implemented ONLY by a provider whose price cannot be known
/// offline (Higgsfield), and deliberately NOT a member of <see cref="IVideoGenerationClient"/>.
/// <para>
/// <b>Why a separate interface rather than a member of <see cref="IVideoGenerationClient"/>.</b>
/// Three reasons, in order of weight. (1) That interface is frozen — its own consumers' note records
/// it, and every fake and mock in the suite is built against it; a new required member would break all
/// of them, and a defaulted one would silently return <c>null</c> under Moq, which is worse than a
/// compile error. (2) Membership would imply every provider has a network price, and most do not:
/// MiniMax's price is a published table. (3) A caller should be able to ask "price this clip" without
/// knowing which kind of provider it holds — which is what
/// <see cref="VideoGenerationPriceOracleExtensions.QuoteClipAsync"/> is for.
/// </para>
/// <para>
/// <b>The purity contract, redefined rather than quietly broken.</b>
/// <see cref="IVideoGenerationClient.Describe"/> is still pure AND no-network, and this interface does
/// not change that. What changes is that the PRICING PATH is now permitted one bounded, cached network
/// call, and this is the only place in the codebase where such a call can originate. The contract an
/// implementation must honour:
/// </para>
/// <list type="bullet">
/// <item><b>Bounded.</b> At most ONE HTTP attempt per call, under the implementation's own timeout
/// (see <c>HiggsfieldVideoGenerationClient.DefaultPriceOracleTimeout</c>) and never the provider row's
/// long generation timeout, always linked to the caller's <see cref="CancellationToken"/>.</item>
/// <item><b>Cached.</b> A successful quote is served from cache for its TTL, so pricing a whole plan
/// costs one call per distinct configuration and no hot path can price per call.</item>
/// <item><b>Fail-closed.</b> Any failure — network error, timeout, non-2xx, unparseable body, or a body
/// carrying a non-positive <c>usd</c> — throws <see cref="NotSupportedException"/>, the same exception
/// <see cref="IVideoGenerationClient.Describe"/> already throws for an unpriceable model. Callers
/// therefore need no new catch, and the existing <c>VideoGenerateStepExecutor</c> mapping to
/// <c>CONFIG_INVALID</c> keeps producing exactly today's outcome. An implementation must NEVER return
/// a zero, a null, or a "no price" sentinel: <see cref="VideoGenerationPriceQuote"/> has no
/// representation of "unpriced", on purpose.</item>
/// <item><b>No new price table.</b> The oracle contributes one per-clip USD figure; the arithmetic that
/// turns a price into a spend estimate stays owned by <see cref="VideoGenerationPricing"/>.</item>
/// </list>
/// </summary>
public interface IVideoGenerationPriceOracle
{
    /// <summary>
    /// Prices one clip at one exact configuration.
    /// </summary>
    /// <param name="query">The configuration to price. Must not be null.</param>
    /// <param name="ct">Cancellation for the whole call, including the single HTTP attempt.</param>
    /// <returns>A quote whose <see cref="VideoGenerationPriceQuote.TotalUsd"/> is strictly positive.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="query"/> is malformed (blank model, blank resolution or ratio, non-positive duration).</exception>
    /// <exception cref="NotSupportedException">
    /// The oracle could not price this configuration. Fail-closed: the caller must treat the
    /// configuration as unofferable. The message is bounded and free of credentials, and is safe to
    /// surface to an admin or into a step's <c>CONFIG_INVALID</c> detail.
    /// </exception>
    Task<VideoGenerationPriceQuote> QuoteAsync(VideoGenerationPriceQuery query, CancellationToken ct);
}

/// <summary>
/// The provider-agnostic way to price a clip, and the surface a consumer should call.
/// </summary>
public static class VideoGenerationPriceOracleExtensions
{
    /// <summary>
    /// Prices one clip through whichever path the client supports: the client's own async oracle when
    /// it implements <see cref="IVideoGenerationPriceOracle"/>, otherwise the client's static
    /// <see cref="IVideoGenerationClient.Describe"/> table.
    /// <para>
    /// This is deliberately an extension method rather than another interface member. A client that
    /// does NOT implement <see cref="IVideoGenerationPriceOracle"/> — MiniMax, and every
    /// <c>Mock&lt;IVideoGenerationClient&gt;</c> in the test suite — falls back to the static table, so
    /// this call is safe to make unconditionally and costs no network for a provider whose prices are
    /// already known. Asking the client its own type is the single place that decision is made.
    /// </para>
    /// <para>
    /// The static branch routes through <see cref="VideoGenerationPricing.TryGetPricePerSecond"/> and
    /// <see cref="VideoGenerationPricing.EstimateClipCostUsd"/> exactly as the budget path always has,
    /// so an unpriced resolution still throws the same <see cref="ArgumentException"/> it always threw
    /// rather than becoming a zero.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="query"/> is null.</exception>
    /// <exception cref="NotSupportedException">The client has no static table for <paramref name="query"/>'s model, or its oracle could not price it.</exception>
    public static Task<VideoGenerationPriceQuote> QuoteClipAsync(
        this IVideoGenerationClient client,
        VideoGenerationPriceQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(query);

        return client is IVideoGenerationPriceOracle oracle
            ? oracle.QuoteAsync(query, ct)
            : Task.FromResult(QuoteFromStaticTable(client.Describe(query.Model), query));
    }

    /// <summary>
    /// Builds a quote from a provider's published price table. The per-second value comes straight
    /// from the table (ordinal lookup, no case folding — the same rule
    /// <see cref="VideoGenerationPricing"/> applies) and the total comes from
    /// <see cref="VideoGenerationPricing.EstimateClipCostUsd"/>, so this cannot drift from what the
    /// budget path computes.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ArgumentException">The table has no price for the query's resolution.</exception>
    /// <exception cref="InvalidOperationException">The table yields a non-positive estimate.</exception>
    public static VideoGenerationPriceQuote QuoteFromStaticTable(
        VideoGenerationCapabilities capabilities,
        VideoGenerationPriceQuery query)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(query);

        if (!VideoGenerationPricing.TryGetPricePerSecond(capabilities, query.Resolution, out decimal perSecond))
        {
            throw new ArgumentException(
                $"Resolution '{query.Resolution}' has no price for model '{query.Model}'.",
                nameof(query));
        }

        decimal total = VideoGenerationPricing.EstimateClipCostUsd(
            capabilities, query.Resolution, query.DurationSeconds);

        return new VideoGenerationPriceQuote(
            model: query.Model,
            resolution: query.Resolution,
            ratio: query.Ratio,
            durationSeconds: query.DurationSeconds,
            pricePerSecondUsd: perSecond,
            totalUsd: total,
            credits: null,
            source: VideoGenerationPriceSource.StaticTable);
    }
}

/// <summary>
/// The oracle could not price a configuration RIGHT NOW (the vendor or the network did not answer usefully). It is a
/// <see cref="NotSupportedException"/> so existing fail-closed handlers keep working, but unlike a plain
/// <see cref="NotSupportedException"/> (the model or setting is not supported) the person did nothing wrong: a step
/// that catches it first fails with a platform code and stays retryable.
/// </summary>
public sealed class VideoGenerationPriceUnavailableException(string? message) : NotSupportedException(message);
