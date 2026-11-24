using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Agents;

/// <summary>
/// Registry for resolving agents by type (API-side, file summarization only).
/// </summary>
public interface IAgentRegistry
{
    IReelBoltAgent? GetByType(AgentType agentType);
    IReadOnlyList<IReelBoltAgent> GetAll();
}
