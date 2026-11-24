using System.Threading;
using ReelBolt.Shared.Billing;

namespace ReelBolt.WorkflowEngine.Execution;

public sealed class WorkflowExecutionContext
{
    public required Guid ExecutionId { get; init; }
    public required Guid ProjectId { get; init; }
    public required string CorrelationId { get; init; }

    /// <summary>
    /// The organization the execution belongs to (<c>WorkflowExecution.OrganizationId</c>). Read by
    /// the inference tenant accessor so every provider resolution made while the execution runs,
    /// from an agent, an executor or a gate, sees that organization's providers and nobody else's.
    /// Null only for hand-built scopes in tests, which resolve platform providers.
    /// </summary>
    public Guid? OrganizationId { get; init; }

    /// <summary>
    /// The run's plan-driven queue-priority class (B8c, <c>ReelBolt.Shared.Billing.RunPriority</c>),
    /// resolved from the organization's entitlement when the execution starts. The execution has
    /// already been dequeued by the time anything reads this, so it is not used to reorder the
    /// broker: it is the value the compute fabric writes to <c>runner_jobs.priority</c> when it
    /// creates work for a runner, which the gateway's claim orders by (D5b: <c>priority DESC,
    /// created_at ASC, id</c>). Resolving it once per run rather than per job keeps one run at one
    /// class for its whole lifetime; <see cref="RunPriority.Standard"/> is the value for a self-host
    /// deployment (whose entitlement set is unlimited) and for any execution started before this
    /// scope field existed.
    /// </summary>
    public int RunPriority { get; init; }

    /// <summary>
    /// The id of the step result the code running under this scope belongs to
    /// (<c>WorkflowStepResult.Id</c>), or null on a run-level scope and on any path with no step
    /// result (graphic design, composition revisions, tests). The render tool keys its upload on it
    /// so each review-loop iteration keeps its own object; null keeps the pre-existing
    /// <c>outputFiles/{executionId}/{file}</c> key.
    /// </summary>
    public Guid? StepResultId { get; init; }

    /// <summary>
    /// Set by the RenderVideoAndUploadToStorage tool when it produces a video artifact.
    /// The workflow executor reads this after agent completion and persists it to WorkflowStepResult.
    /// </summary>
    public string? PendingOutputStorageKey { get; set; }

    /// <summary>
    /// Set by <c>WorkflowControlAgentTools.FailWorkflow</c> when an agent asks to abort. Recorded
    /// as STATE rather than relying solely on the <c>AgentWorkflowException</c> that tool throws,
    /// because that exception does not escape the agent run: <c>FunctionInvokingChatClient</c>
    /// catches exceptions thrown by a tool and feeds them back to the model as a tool result, so
    /// the abort signal is swallowed and the model simply keeps going.
    ///
    /// <para>
    /// Observed live: a Colorist step called fail_workflow, was handed its own exception back as a
    /// tool result, and looped read_project_file -> fail_workflow for over forty minutes without
    /// ever failing the step -- while the tool's own documentation promised it would "abort the
    /// entire workflow immediately". Checking this flag after the run is what actually honors that
    /// contract.
    /// </para>
    /// </summary>
    public string? AbortReason { get; set; }

    /// <summary>
    /// Composition revisions: which workspace lineage a render captured in this scope belongs to,
    /// set by <c>CompositionRevisionRunner</c> before it runs the reviser agent. Null in every other
    /// scope, where a capture starts (or continues) the lineage of the execution itself. The capture
    /// advances <see cref="CompositionLineage.ParentVersionId"/> after each version it appends, so a
    /// second render in the same run chains onto the first instead of forking from the base.
    /// </summary>
    public Services.Compositions.CompositionLineage? CompositionLineage { get; set; }

    /// <summary>
    /// The run-wide count of WebSearch tool calls. One instance per execution: a nested scope for the
    /// same execution (every step opens one) inherits its parent's counter, so the per-run cap holds
    /// across steps and parallel branches. Not reset by a step retry. In-process only: a run resumed
    /// by another process starts a fresh count.
    /// </summary>
    public WebSearchCallCounter RunWebSearchCalls { get; init; } = new();

    private int _stepWebSearchCalls;
    private readonly object _suggestionsLock = new();
    private readonly List<string> _searchSuggestionsHtml = [];

    /// <summary>
    /// Takes one WebSearch call from this scope (the step: every scope is one attempt of one step, so a retry
    /// starts at zero) and from the run. False, and nothing taken, when either cap is reached. Failed provider
    /// calls keep their slot.
    /// </summary>
    public bool TryReserveWebSearchCall(int maxPerStep, int maxPerRun)
    {
        if (Interlocked.Increment(ref _stepWebSearchCalls) > maxPerStep)
        {
            Interlocked.Decrement(ref _stepWebSearchCalls);
            return false;
        }

        if (Interlocked.Increment(ref RunWebSearchCalls.Count) > maxPerRun)
        {
            Interlocked.Decrement(ref RunWebSearchCalls.Count);
            Interlocked.Decrement(ref _stepWebSearchCalls);
            return false;
        }

        return true;
    }

    /// <summary>WebSearch calls made in this scope so far.</summary>
    public int StepWebSearchCalls => Volatile.Read(ref _stepWebSearchCalls);

    /// <summary>
    /// The run-wide count of LookAtMoment calls that reached the picture model. One instance per execution, inherited by
    /// nested scopes like <see cref="RunWebSearchCalls"/>. In-process only: a run resumed by another process starts a
    /// fresh count.
    /// </summary>
    public MomentLookCounter RunMomentLooks { get; init; } = new();

    private int _stepMomentLooks;

    /// <summary>
    /// Takes one look at a moment from this scope (one attempt of one step) and from the run. False, and nothing taken,
    /// when either cap is reached. A look that fails at the provider keeps its slot.
    /// </summary>
    public bool TryReserveMomentLook(int maxPerStep, int maxPerRun)
    {
        if (Interlocked.Increment(ref _stepMomentLooks) > maxPerStep)
        {
            Interlocked.Decrement(ref _stepMomentLooks);
            return false;
        }

        if (Interlocked.Increment(ref RunMomentLooks.Count) > maxPerRun)
        {
            Interlocked.Decrement(ref RunMomentLooks.Count);
            Interlocked.Decrement(ref _stepMomentLooks);
            return false;
        }

        return true;
    }

    /// <summary>Looks at moments taken in this scope so far.</summary>
    public int StepMomentLooks => Volatile.Read(ref _stepMomentLooks);

    private int _stepMomentLookCalls;

    /// <summary>
    /// Takes one LookAtMoment call from this scope (one attempt of one step). Every call that names an event id takes one,
    /// whether or not it reaches the picture model, so a loop of misses ends too. False, and nothing taken, at the cap.
    /// </summary>
    public bool TryTakeMomentLookCall(int maxPerStep)
    {
        if (Interlocked.Increment(ref _stepMomentLookCalls) > maxPerStep)
        {
            Interlocked.Decrement(ref _stepMomentLookCalls);
            return false;
        }

        return true;
    }

    /// <summary>LookAtMoment calls taken in this scope so far, misses included.</summary>
    public int StepMomentLookCalls => Volatile.Read(ref _stepMomentLookCalls);

    /// <summary>
    /// Side channel for a restricted (Gemini) search: the Search Suggestions HTML the provider's terms require
    /// to be displayed to the user. It never goes to the model. Nothing persists it yet; the run page picks it
    /// up from here.
    /// </summary>
    public IReadOnlyList<string> SearchSuggestionsHtml
    {
        get { lock (_suggestionsLock) return [.. _searchSuggestionsHtml]; }
    }

    internal void AddSearchSuggestionsHtml(string html)
    {
        lock (_suggestionsLock) _searchSuggestionsHtml.Add(html);
    }
}

/// <summary>The shared, interlocked WebSearch call count of one execution.</summary>
public sealed class WebSearchCallCounter
{
    public int Count;
}

/// <summary>The shared, interlocked LookAtMoment call count of one execution.</summary>
public sealed class MomentLookCounter
{
    public int Count;
}

public interface IWorkflowExecutionContextAccessor
{
    WorkflowExecutionContext? Current { get; }
    IDisposable BeginScope(Guid executionId, Guid projectId, string correlationId, Guid? organizationId = null, int runPriority = 0, Guid? stepResultId = null);
}

public class WorkflowExecutionContextAccessor : IWorkflowExecutionContextAccessor
{
    private readonly AsyncLocal<WorkflowExecutionContext?> _current = new();
    public WorkflowExecutionContext? Current => _current.Value;

    public IDisposable BeginScope(Guid executionId, Guid projectId, string correlationId, Guid? organizationId = null, int runPriority = 0, Guid? stepResultId = null)
    {
        WorkflowExecutionContext? previous = _current.Value;
        _current.Value = new WorkflowExecutionContext
        {
            ExecutionId = executionId,
            ProjectId = projectId,
            CorrelationId = correlationId,
            OrganizationId = organizationId,
            RunPriority = runPriority,
            StepResultId = stepResultId,
            RunWebSearchCalls = previous is not null && previous.ExecutionId == executionId ? previous.RunWebSearchCalls : new(),
            RunMomentLooks = previous is not null && previous.ExecutionId == executionId ? previous.RunMomentLooks : new()
        };
        return new RestoreScope(() => _current.Value = previous);
    }

    private sealed class RestoreScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
