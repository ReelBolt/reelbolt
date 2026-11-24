using Microsoft.Extensions.AI;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Execution.Snapshots;

namespace ReelBolt.WorkflowEngine.Execution.Decision.CostRouting;

/// <summary>
/// Decorates <see cref="IAgentChatClientProvider"/> so that a <em>routed</em> agent step builds its
/// chat client against the tier cost routing selected, instead of whatever the ordinary resolver
/// precedence would have picked.
///
/// <para>
/// This is the whole reason the feature is a decorator and not an executor change. The executor's
/// single permitted statement arms the decision; the agent then resolves its chat client through
/// exactly the seam it always did (<see cref="Agents.ReelBoltAgentBase.CreateAgentAsync"/> →
/// <c>IAgentChatClientProvider.GetAsync</c>), so no agent, no agent base class and no second executor
/// statement needed to know cost routing exists.
/// </para>
///
/// <para>
/// <b>Default path is untouched.</b> With no ambient routing intent — every step of every workflow
/// where the gate is off, unconfigured, or has no Decision provider — this is a pure pass-through to
/// the inner provider, identical to registering <see cref="AgentChatClientProvider"/> alone.
/// </para>
///
/// <para>
/// <b>Soft-failing.</b> An escalated run whose expensive provider cannot be resolved (deleted,
/// disabled, or not <c>Capability == Chat</c>) or whose client cannot be built falls back to the
/// ordinary path and logs a warning. It must never fail the step: the decision to escalate is a
/// cost optimisation, and "run on the provider you would have used anyway" is always a valid
/// answer.
/// </para>
/// </summary>
public sealed class CostRoutingChatClientProvider : IAgentChatClientProvider
{
    /// <summary>Outcome vocabulary, shared with <see cref="CostRoutingRunState.Snapshot"/>.</summary>
    internal const string CheapTier = "cheap";
    internal const string ExpensiveTier = "expensive";

    private readonly IAgentChatClientProvider _inner;
    private readonly IInferenceProviderResolver _resolver;
    private readonly IChatClientFactory _factory;
    private readonly CostRoutingAmbientState _ambient;
    private readonly ILogger<CostRoutingChatClientProvider> _logger;

    public CostRoutingChatClientProvider(
        IAgentChatClientProvider inner,
        IInferenceProviderResolver resolver,
        IChatClientFactory factory,
        CostRoutingAmbientState ambient,
        ILogger<CostRoutingChatClientProvider> logger)
    {
        _inner = inner;
        _resolver = resolver;
        _factory = factory;
        _ambient = ambient;
        _logger = logger;
    }

    // Cost routing only changes WHICH provider answers; the agent's configured reasoning effort is the
    // same either way. Inside a run with an agent snapshot, the effort is the one the agent had when the
    // run started (a built-in that recorded none falls back as today).
    public ValueTask<string?> ResolveReasoningEffortAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
        => AgentSnapshotAmbient.Current?.Find(agentDefinitionId) is { } frozen && (frozen.ReasoningEffort is not null || !frozen.IsBuiltIn)
            ? ValueTask.FromResult(frozen.ReasoningEffort)
            : _inner.ResolveReasoningEffortAsync(agentType, agentDefinitionId, ct);

    public async ValueTask<IChatClient> GetAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
    {
        // An app's model input chose this agent's model for the run (docs/design/app-inputs.md 8.4): it wins over every
        // other source, built-in and custom agents alike, and over cost routing (a person's explicit choice is not
        // second-guessed). A choice that no longer resolves (disabled or deleted since the claim) falls through to the
        // ordinary path, exactly like a disabled override.
        if (AgentSnapshotAmbient.Current?.Find(agentDefinitionId)?.AppModelProviderId is Guid chosenId
            && await _resolver.ResolveChatAsync(chosenId, ct) is { } chosen)
        {
            return _factory.Get(chosen);
        }

        CostRoutingRunState? state = _ambient.Current;

        if (state is null)
            return await GetUnroutedAsync(agentType, agentDefinitionId, ct);

        Guid? pinnedProviderId = state.Escalated ? state.ExpensiveProviderId : state.CheapProviderId;

        IChatClient client = pinnedProviderId is Guid providerId
            ? await ResolvePinnedAsync(state, providerId, agentType, agentDefinitionId, ct)
            : await GetUnroutedAsync(agentType, agentDefinitionId, ct);

        // Recording is attached on BOTH tiers: an observation exists for every routing decision,
        // including the confident "stay cheap" ones, and each needs the run's real outcome.
        return new CostRoutingRecordingChatClient(client, state);
    }

    /// <summary>
    /// Builds the chat client for the tier the decision selected, falling back to the ordinary
    /// precedence path when that provider cannot be used. The fallback is deliberately silent
    /// behaviourally (the run proceeds) and loud in the log.
    /// </summary>
    private async Task<IChatClient> ResolvePinnedAsync(
        CostRoutingRunState state, Guid providerId, AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
    {
        try
        {
            ResolvedInferenceProvider? resolved = await _resolver.ResolveChatAsync(providerId, ct);

            if (resolved is null)
            {
                _logger.LogWarning(
                    "Cost routing: chat provider {ProviderId} did not resolve for {AgentType}; running on the default provider instead",
                    providerId,
                    agentType);
                return await GetUnroutedAsync(agentType, agentDefinitionId, ct);
            }

            IChatClient client = _factory.Get(resolved);

            // The state records the provider the run ACTUALLY used — which is the pinned one only
            // because the two calls above succeeded.
            state.RecordRoutedProvider(resolved.ProviderId, resolved.ModelName);

            return client;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Cost routing: building the pinned chat provider {ProviderId} failed for {AgentType}; running on the default provider instead",
                providerId,
                agentType);
            return await GetUnroutedAsync(agentType, agentDefinitionId, ct);
        }
    }

    /// <summary>
    /// The ordinary precedence path. Inside a run with an agent snapshot, a CUSTOM agent's provider is
    /// the one it had when the run started: pinned when it named one (a pin that no longer resolves
    /// falls through, exactly like a disabled override does), and the agent-type and default chain when
    /// it named none. Built-in agents carry no per-row override on this path and resolve as today; the
    /// provider rows themselves (keys, endpoints) are always live configuration.
    /// </summary>
    private async ValueTask<IChatClient> GetUnroutedAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
    {
        if (AgentSnapshotAmbient.Current?.Find(agentDefinitionId) is not { IsBuiltIn: false } frozen)
            return await _inner.GetAsync(agentType, agentDefinitionId, ct);

        if (frozen.InferenceProviderId is Guid providerId
            && await _resolver.ResolveChatAsync(providerId, ct) is { } pinned)
        {
            return _factory.Get(pinned);
        }

        // No (usable) recorded override: everything below the by-id override, i.e. the live chain
        // without the live row's current choice.
        return await _inner.GetAsync(agentType, null, ct);
    }
}
