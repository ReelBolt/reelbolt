using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Agents;

public class AgentRegistry : IAgentRegistry
{
    private readonly Dictionary<AgentType, IReelBoltAgent> _agents;
    private readonly Dictionary<Guid, IReelBoltAgent> _customAgentsById;
    private readonly List<IReelBoltAgent> _allAgents;

    public AgentRegistry(IEnumerable<IReelBoltAgent> agents)
    {
        _allAgents = agents.ToList();
        _agents = new Dictionary<AgentType, IReelBoltAgent>();
        _customAgentsById = new Dictionary<Guid, IReelBoltAgent>();
        foreach (IReelBoltAgent agent in _allAgents)
        {
            if (agent.AgentType != AgentType.Custom && agent.OutputSchemaType == null)
            {
                throw new InvalidOperationException(
                    $"Built-in agent '{agent.Name}' ({agent.AgentType}) must declare an output schema type.");
            }

            _agents.TryAdd(agent.AgentType, agent);

            if (agent.AgentType == AgentType.Custom && agent.AgentId.HasValue)
            {
                _customAgentsById[agent.AgentId.Value] = agent;
            }
        }
    }

    public IReelBoltAgent? GetByType(AgentType agentType, Guid? agentId = null)
    {
        if (agentType == AgentType.Custom)
        {
            if (!agentId.HasValue)
            {
                return null;
            }

            _customAgentsById.TryGetValue(agentId.Value, out IReelBoltAgent? customAgent);
            return customAgent;
        }

        _agents.TryGetValue(agentType, out IReelBoltAgent? agent);
        return agent;
    }

    public IReadOnlyList<IReelBoltAgent> GetAll() => _allAgents.AsReadOnly();
}
