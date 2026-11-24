using Microsoft.Extensions.AI;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Agents;

public interface IReelBoltAgent
{
    Guid? AgentId { get; }
    string Name { get; }
    string Description { get; }
    string SystemPrompt { get; }
    AgentType AgentType { get; }
    IReadOnlyList<AIFunction> Tools { get; }
    string? OutputSchemaJson { get; }
    Type? OutputSchemaType { get; }
    /// <param name="agentDefinitionId">
    /// The concrete WorkflowStep.AgentDefinitionId driving this run, when known to the caller.
    /// Passed through to IAgentChatClientProvider so a per-agent InferenceProvider override
    /// actually applies — without it, provider resolution falls back to this agent's
    /// constructor-time AgentId (usually null), and a per-definition override can never be
    /// selected (found by Copilot review).
    /// </param>
    Task<AgentRunResult> RunAsync(string prompt, Guid? agentDefinitionId = null, CancellationToken ct = default);

    /// <summary>
    /// One structured-output repair turn: like <see cref="RunAsync"/> but with no tools, so it cannot
    /// repeat side effects. The default (for fakes and agents without a no-tools mode) returns null,
    /// and the caller then treats the repair as unavailable.
    /// </summary>
    Task<AgentRunResult?> RunRepairAsync(string prompt, Guid? agentDefinitionId = null, CancellationToken ct = default)
        => Task.FromResult<AgentRunResult?>(null);
}
