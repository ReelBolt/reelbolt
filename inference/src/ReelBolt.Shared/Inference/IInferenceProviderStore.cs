using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Reads configured model capabilities and per-agent overrides. Implemented once per service
/// over that service's own DbContext (the model catalog tables and <c>agent_definitions</c>
/// are owned by the Inference API; the WorkflowEngine's implementation reads the same tables
/// through its read-only, excluded-from-migrations mapping).
/// </summary>
public interface IInferenceProviderStore
{
    /// <summary>
    /// Enabled model capabilities (capability, model and provider account all enabled), flattened into
    /// <see cref="ModelCapabilityView"/>, visible to <paramref name="organizationId"/>: every
    /// platform-managed row (<c>OrganizationId</c> null) plus that organization's own rows. A null id
    /// returns platform rows only. Implementations must filter explicitly, never rely on an ambient
    /// tenant filter, because the caller's organization is not necessarily the ambient one.
    /// </summary>
    Task<IReadOnlyList<ModelCapabilityView>> LoadEnabledAsync(Guid? organizationId, CancellationToken ct);

    /// <summary>
    /// Maps agentDefinitionId -&gt; inferenceProviderId (possibly null) for the CUSTOM agent
    /// definitions owned by <paramref name="organizationId"/>. Built-in definitions are excluded:
    /// their override is shared by every org and is read per type through
    /// <see cref="LoadBuiltInAgentOverridesAsync"/> / <see cref="LoadOrgAgentOverridesAsync"/>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid?>> LoadAgentOverridesAsync(Guid? organizationId, CancellationToken ct);

    /// <summary>
    /// The organization's own provider choice per BUILT-IN agent type
    /// (<c>organization_agent_provider_overrides</c>). Empty for a null organization.
    /// </summary>
    Task<IReadOnlyDictionary<AgentType, Guid?>> LoadOrgAgentOverridesAsync(Guid? organizationId, CancellationToken ct);

    /// <summary>
    /// Platform-level: maps <see cref="AgentType"/> -&gt; inferenceProviderId (possibly null) for the
    /// BUILT-IN agent definition of each type. Needed because several callers know only which built-in
    /// agent type is running, not the <c>agent_definitions</c> row id driving it — a room step's
    /// seats and director are the motivating case (see
    /// <see cref="IInferenceProviderResolver.ResolveAsync"/>). <see cref="AgentType.Custom"/> is
    /// deliberately excluded: it has no single built-in row, only per-definition ones already
    /// reachable through <see cref="LoadAgentOverridesAsync"/>.
    /// </summary>
    Task<IReadOnlyDictionary<AgentType, Guid?>> LoadBuiltInAgentOverridesAsync(CancellationToken ct);

    /// <summary>
    /// The stored <c>agent_definitions.reasoning_effort</c> of every BUILT-IN definition (by id and by
    /// type, the same two doors <see cref="LoadBuiltInAgentOverridesAsync"/> serves) plus the CUSTOM
    /// definitions owned by <paramref name="organizationId"/>. A null value means "use the code default".
    /// The default implementation returns nothing, so a test double needs no change; both real stores
    /// override it.
    /// </summary>
    Task<AgentReasoningEfforts> LoadAgentReasoningEffortsAsync(Guid? organizationId, CancellationToken ct)
        => Task.FromResult(AgentReasoningEfforts.Empty);

    /// <summary>
    /// The ids of the enabled PLATFORM capabilities priced from their table (<see cref="ModelPriceSource.Table"/>)
    /// that have no effective <c>model_capability_prices</c> row at <paramref name="at"/>
    /// (<c>effective_from &lt;= at &lt; effective_to</c>, or open-ended) for some unit their capability meters
    /// (T-032: priced only when every metered unit is, the rule of <c>PriceCoverage.Find</c>). With <c>Billing:Enabled</c> the resolver
    /// treats them as absent (docs/metering-and-billing.md, "Unpriced models fail closed"). Organization (BYO) and
    /// oracle-priced capabilities are never in it. The default implementation returns nothing, so a test double needs
    /// no change; both real stores override it (<see cref="ModelCapabilityView.UnpricedPlatformIdsAsync"/>).
    /// </summary>
    Task<IReadOnlySet<Guid>> LoadUnpricedCapabilityIdsAsync(DateTime at, CancellationToken ct)
        => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
}

/// <summary>Stored per-agent reasoning efforts, keyed the two ways a run can name its agent.</summary>
/// <param name="ById">Definition id to stored value (possibly null), built-ins and the organization's custom agents.</param>
/// <param name="BuiltInByType">Built-in agent type to its single built-in row's stored value (possibly null).</param>
public sealed record AgentReasoningEfforts(
    IReadOnlyDictionary<Guid, string?> ById,
    IReadOnlyDictionary<AgentType, string?> BuiltInByType)
{
    public static readonly AgentReasoningEfforts Empty = new(
        new Dictionary<Guid, string?>(), new Dictionary<AgentType, string?>());
}
