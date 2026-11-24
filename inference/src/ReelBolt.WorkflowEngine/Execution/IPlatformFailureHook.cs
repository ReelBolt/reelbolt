using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Execution;

/// <summary>
/// Called when the PLATFORM, not the user or the workflow, ended an execution (today: the recovery
/// sweeper finding an orphaned run). The one place a credit refund for a platform failure attaches.
///
/// <para>B6a: <c>CreditPlatformFailureHook</c> releases the execution's open credit holds. It is
/// registered before this file's no-op default (<c>TryAddSingleton</c>), so it wins.</para>
/// </summary>
public interface IPlatformFailureHook
{
    Task OnPlatformFailureAsync(WorkflowExecution execution, string reason, CancellationToken ct);
}

/// <summary>Default: nothing to refund yet.</summary>
public sealed class NoOpPlatformFailureHook : IPlatformFailureHook
{
    public Task OnPlatformFailureAsync(WorkflowExecution execution, string reason, CancellationToken ct) => Task.CompletedTask;
}
