using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Marker for a step executor that applies a step change request (docs/design/change-requests.md).
/// The drift guard requires one registered implementation for every
/// <c>StepChangeCapabilities.For(type).Supported</c> type.
/// </summary>
public interface IChangeRequestAwareStepExecutor
{
    StepType StepType { get; }

    /// <summary>
    /// For an executor that hosts many agents (<see cref="StepType.Agent"/>): the agent types it applies a request for.
    /// The drift guard requires every <c>StepChangeCapabilities.ForAgent</c> Supported type to be listed by one.
    /// </summary>
    IReadOnlyCollection<AgentType> SupportedAgentTypes => [];
}
