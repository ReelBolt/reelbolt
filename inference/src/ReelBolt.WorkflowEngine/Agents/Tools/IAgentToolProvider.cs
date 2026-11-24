using Microsoft.Extensions.AI;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

public interface IAgentToolProvider
{
    IReadOnlyList<AIFunction> GetTools(AgentType agentType);
}
