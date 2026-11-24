using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;

namespace ReelBolt.WorkflowEngine.Execution;

/// <summary>
/// The engine's entry point for an agent step's effective context mode. The table itself lives in
/// <see cref="AgentInputContextDefaults"/> (Shared) so the dependency analysis a composition
/// revision's replay is planned from reads exactly what the prompt builder reads.
/// </summary>
public static class AgentInputContextResolver
{
    public static AgentInputContextMode ResolveEffectiveMode(WorkflowStep step) =>
        AgentInputContextDefaults.ResolveEffectiveMode(step);

    public static AgentInputContextMode GetDefaultMode(AgentType agentType) =>
        AgentInputContextDefaults.GetDefaultMode(agentType);
}
