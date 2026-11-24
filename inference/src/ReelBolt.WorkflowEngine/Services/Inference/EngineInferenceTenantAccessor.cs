using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Execution;

namespace ReelBolt.WorkflowEngine.Services.Inference;

/// <summary>
/// The organization an engine-side provider resolution runs for: the one on the current
/// <see cref="WorkflowExecutionContext"/>, opened for the whole execution by
/// <c>WorkflowExecutorService</c> and re-opened by every nested agent scope. No execution in flight
/// means the platform-only view (fail closed to platform providers, never to another org's).
/// </summary>
public sealed class EngineInferenceTenantAccessor(IWorkflowExecutionContextAccessor executionContext)
    : InferenceTenantAccessorBase
{
    protected override Guid? AmbientOrganizationId => executionContext.Current?.OrganizationId;
}
