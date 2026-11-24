using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// One billable unit to record. Attribution (organization, project, execution, step result, user, source) is
/// not part of it: it comes from <see cref="UsageScope.Current"/> unless <paramref name="Scope"/> overrides it.
/// </summary>
/// <param name="CallSeq">
/// The per-scope call number shared by every row one model call produces (input, output, cached), giving the
/// idempotency key <c>{stepResultId}:{callSeq}:{kind}</c>. Null takes the next number from the scope.
/// </param>
/// <param name="IdempotencyKey">
/// Overrides the generated key when a unit has a natural identity that survives a retry (a video generation
/// take is <c>videogen:{ExternalGenerationJob.Id}</c>), so replaying the settle never double-charges.
/// </param>
/// <param name="ProviderId">The model capability that served the call (null for a config fallback).</param>
/// <param name="Capability">
/// The capability the call was made for (Chat vs Vision, ImageErase…); null infers it from the unit
/// (<see cref="ProviderCapabilityUsage.DefaultCapabilityFor"/>).
/// </param>
/// <param name="Variant">The price variant: a fal image tier, a video resolution, a compute tier.</param>
/// <param name="AccountId">The provider account serving <paramref name="ProviderId"/>.</param>
/// <param name="QuotedCostUsd">
/// Oracle-priced usage (Higgsfield): the USD the vendor quoted for exactly this usage. Credits are that cost at the
/// platform markup, and the row carries no price id (docs/design/model-catalog.md §5.3).
/// </param>
/// <param name="PromptTokens">
/// The whole prompt length of the call this unit belongs to (cached input included), so a long-context call prices
/// at its <c>gt{N}k</c> tier row (<see cref="PriceRequest.PromptTokens"/>); null = unknown.
/// </param>
public sealed record UsageRecordRequest(
    UsageKind Kind,
    decimal Quantity,
    Guid? ProviderId,
    InferenceProviderKind ProviderKind,
    string ModelName,
    UsageBillingMode BillingMode,
    long? CallSeq = null,
    UsageScopeState? Scope = null,
    string? IdempotencyKey = null,
    InferenceProviderCapability? Capability = null,
    string? Variant = null,
    Guid? AccountId = null,
    decimal? QuotedCostUsd = null,
    long? PromptTokens = null,
    bool InformationalWhenUnpriced = false);

/// <summary>
/// Prices and records usage. Recording never throws into the work being metered: the call already happened,
/// so a failure here is logged loudly, not allowed to fail a workflow step. The one deliberate exception is
/// <see cref="UnattributedUsageException"/> under <see cref="MeteringOptions.FailOnUnattributed"/> (a test tripwire).
/// </summary>
public interface IUsageRecorder
{
    /// <summary>
    /// Prices the unit, adds it to the scope's running totals and queues a <c>usage_events</c> row for the
    /// batched writer. Returns the millicredits charged.
    /// </summary>
    ValueTask<long> RecordAsync(UsageRecordRequest request, CancellationToken ct = default);
}

/// <summary>Where a batch of priced events is written (each service supplies its own context).</summary>
public interface IUsageEventSink
{
    Task WriteAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct);
}
