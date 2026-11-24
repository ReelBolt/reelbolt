using Microsoft.Extensions.AI;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Agents;

/// <summary>
/// Interface for ReelBolt agents in the API service (file summarization only).
/// </summary>
public interface IReelBoltAgent
{
    string Name { get; }
    string Description { get; }
    string SystemPrompt { get; }
    AgentType AgentType { get; }
    IReadOnlyList<AIFunction> Tools { get; }
    string? OutputSchemaJson { get; }
    Type? OutputSchemaType { get; }
    /// <param name="agentDefinitionId">
    /// The concrete AgentDefinition.Id driving this run, when known to the caller. Passed
    /// through to IAgentChatClientProvider so a per-agent InferenceProvider override actually
    /// applies (found by Copilot review — every caller previously passed null unconditionally,
    /// making the override dead code).
    /// </param>
    Task<AgentRunResult> RunAsync(string prompt, Guid? agentDefinitionId = null, CancellationToken ct = default);
}
