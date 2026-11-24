using Microsoft.Extensions.AI;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// The single seam agents depend on to obtain an <see cref="IChatClient"/> for a given run —
/// a trivial composition of <see cref="IInferenceProviderResolver"/> and <see cref="IChatClientFactory"/>.
/// </summary>
public interface IAgentChatClientProvider
{
    ValueTask<IChatClient> GetAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct);

    /// <summary>
    /// Which provider row and model <see cref="GetAsync"/> resolves to, for usage attribution. The
    /// default returns null (unknown) so a test double needs no change; the real provider resolves
    /// through the same TTL-cached resolver, so this is cheap.
    /// </summary>
    ValueTask<ResolvedInferenceProvider?> ResolveProviderAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
        => ValueTask.FromResult<ResolvedInferenceProvider?>(null);

    /// <summary>
    /// The reasoning effort stored on the agent definition driving this run (see
    /// <see cref="IInferenceProviderResolver.ResolveAgentReasoningEffortAsync"/>), or null when none is.
    /// A wrapper around another provider MUST forward this, or its agents silently lose the setting.
    /// </summary>
    ValueTask<string?> ResolveReasoningEffortAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
        => ValueTask.FromResult<string?>(null);
}
