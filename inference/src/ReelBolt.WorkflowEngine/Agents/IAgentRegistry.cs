using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Agents;

public interface IAgentRegistry
{
    IReelBoltAgent? GetByType(AgentType agentType, Guid? agentId = null);
    IReadOnlyList<IReelBoltAgent> GetAll();
}
