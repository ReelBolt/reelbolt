using System.Collections.Concurrent;

namespace ReelBolt.WorkflowEngine.Execution.Replay;

/// <summary>
/// The change requests a review loop asked for INSIDE a run (docs/design/review-loop-adjustments.md "Change requests
/// inside the run"), handed to the step executors through the same <see cref="IStepChangeRequestContext"/> a customer
/// request uses. <see cref="WorkflowExecutorService"/> registers a scope just before it dispatches the target step and
/// removes it once the dispatch ends (every retry attempt of that dispatch sees the same scope), so an entry never
/// outlives the dispatch it was made for. Keyed by execution and step order: runs never see each other's entries.
/// </summary>
public sealed class ReviewStepChangeRegistry
{
    /// <summary>The one registry of this process (the executor service and the change-request context share it).</summary>
    public static ReviewStepChangeRegistry Instance { get; } = new();

    private readonly ConcurrentDictionary<(Guid ExecutionId, int StepOrder), StepChangeRequestScope> _active = new();

    /// <summary>Makes <paramref name="scope"/> the change the next dispatch of the step carries out.</summary>
    public void Set(Guid executionId, int stepOrder, StepChangeRequestScope scope) => _active[(executionId, stepOrder)] = scope;

    /// <summary>The change the current dispatch of the step carries out, or null.</summary>
    public StepChangeRequestScope? Find(Guid executionId, int stepOrder) =>
        _active.TryGetValue((executionId, stepOrder), out StepChangeRequestScope? scope) ? scope : null;

    /// <summary>Forgets the step's change (its dispatch ended).</summary>
    public void Clear(Guid executionId, int stepOrder) => _active.TryRemove((executionId, stepOrder), out _);

    /// <summary>Sets the step's change for as long as the returned lease is held (one dispatch).</summary>
    public IDisposable Lease(Guid executionId, int stepOrder, StepChangeRequestScope scope)
    {
        Set(executionId, stepOrder, scope);
        return new Release(this, executionId, stepOrder);
    }

    private sealed class Release(ReviewStepChangeRegistry registry, Guid executionId, int stepOrder) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                registry.Clear(executionId, stepOrder);
        }
    }
}
