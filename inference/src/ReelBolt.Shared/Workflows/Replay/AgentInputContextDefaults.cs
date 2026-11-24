using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Replay;

/// <summary>
/// Which prior-step context an agent step's prompt is built from when the step does not say
/// (<see cref="WorkflowStep.AgentInputContextMode"/> null). Lives in Shared because two services need
/// the same answer: the engine builds the prompt from it (<c>AgentInputContextResolver</c> delegates
/// here) and the dependency analysis (<see cref="WorkflowStepDependencyGraph"/>) reads what a step
/// depends on from it, in the Inference API as well as the engine. One table, so the two can never
/// disagree about what a step reads.
/// </summary>
public static class AgentInputContextDefaults
{
    /// <summary>The step's own mode when set, otherwise its agent type's default.</summary>
    public static AgentInputContextMode ResolveEffectiveMode(WorkflowStep step)
    {
        if (step.AgentInputContextMode.HasValue)
            return step.AgentInputContextMode.Value;

        return GetDefaultMode(step.AgentDefinition?.AgentType ?? AgentType.Custom);
    }

    public static AgentInputContextMode GetDefaultMode(AgentType agentType)
    {
        return agentType switch
        {
            AgentType.CodeStructureAnalyzer => AgentInputContextMode.FullWorkflow,
            AgentType.DependencyAnalyzer => AgentInputContextMode.PreviousStepOnly,
            AgentType.ComponentInventoryAnalyzer => AgentInputContextMode.PreviousStepOnly,
            AgentType.RouteAndApiAnalyzer => AgentInputContextMode.PreviousStepOnly,
            AgentType.StyleAndThemeExtractor => AgentInputContextMode.PreviousStepOnly,
            AgentType.RemotionComponentTranslator => AgentInputContextMode.FullWorkflow,
            AgentType.AnimationStrategyAgent => AgentInputContextMode.PreviousStepOnly,
            AgentType.DirectorAgent => AgentInputContextMode.FullWorkflow,
            AgentType.ScriptwriterAgent => AgentInputContextMode.PreviousStepOnly,
            AgentType.AuthorAgent => AgentInputContextMode.FullWorkflow,
            AgentType.ReviewAgent => AgentInputContextMode.PreviousStepOnly,
            _ => AgentInputContextMode.FullWorkflow
        };
    }
}
