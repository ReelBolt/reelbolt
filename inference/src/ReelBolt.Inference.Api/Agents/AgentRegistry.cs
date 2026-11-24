using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Agents;

/// <summary>
/// Agent registry for the API service (file summarization only).
/// </summary>
public class AgentRegistry : IAgentRegistry
{
    private readonly Dictionary<AgentType, IReelBoltAgent> _agents;
    private readonly List<IReelBoltAgent> _allAgents;

    public AgentRegistry(IEnumerable<IReelBoltAgent> agents)
    {
        _allAgents = agents.ToList();
        _agents = new Dictionary<AgentType, IReelBoltAgent>();
        foreach (IReelBoltAgent agent in _allAgents)
        {
            if (agent.AgentType != AgentType.Custom && agent.OutputSchemaType == null)
            {
                throw new InvalidOperationException(
                    $"Built-in agent '{agent.Name}' ({agent.AgentType}) must declare an output schema type.");
            }

            _agents.TryAdd(agent.AgentType, agent);
        }
    }

    public IReelBoltAgent? GetByType(AgentType agentType)
    {
        _agents.TryGetValue(agentType, out IReelBoltAgent? agent);
        return agent;
    }

    public IReadOnlyList<IReelBoltAgent> GetAll() => _allAgents.AsReadOnly();
}
