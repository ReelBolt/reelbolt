using ReelBolt.WorkflowEngine.Agents;

namespace ReelBolt.WorkflowEngine.Execution.Decision.CostRouting;

/// <summary>
/// Cost routing for <c>Agent</c> steps: ask a Decision model whether the cheap provider tier will
/// pass validation and review for the step about to run, run on the cheap tier when it says yes,
/// escalate to the expensive tier when it is unsure, and record both the decision and the run's real
/// outcome in <c>decision_observations</c>.
///
/// <para>
/// <b>Never allowed to affect the step's control flow.</b> Every failure mode — no Decision provider
/// configured, a transport error, a malformed response, a missing answer, an unexpected exception —
/// degrades to "no decision", and the run proceeds exactly as it would have without this feature.
/// The same discipline as <c>IDecisionGate</c>, for the same reason: this is an observation and a
/// best-effort optimisation layered on a real agent run, not a gate in the enforcing sense.
/// </para>
/// </summary>
public interface ICostRoutingGate
{
    /// <summary>
    /// Records the routing decision for the step about to run and returns it, armed, as a
    /// <see cref="CostRoutingDecision"/> — or <c>null</c> when there is nothing to route (disabled,
    /// unconfigured, no Decision provider, or any failure along the way).
    /// </summary>
    /// <param name="context">The step about to execute; supplies ids, step type and cancellation.</param>
    /// <param name="agent">The agent about to run — its <c>Name</c> is the <c>Agents:{name}:</c>
    /// config key, and its tool count / output schema are part of the bounded descriptor.</param>
    /// <param name="resolvedAgentInput">The exact prompt the agent is about to receive. Used ONLY for
    /// its length and hash — see <see cref="CostRoutingDescriptor"/>.</param>
    /// <returns>
    /// The decision, to be handed to <see cref="CostRoutingScope.Enter"/> — which MUST happen in the
    /// CALLER's own frame, not here. See <see cref="CostRoutingDecision"/> for why that is
    /// load-bearing.
    /// </returns>
    Task<CostRoutingDecision?> BeginStepAsync(
        StepExecutionContext context, IReelBoltAgent agent, string resolvedAgentInput);
}

/// <summary>
/// One routing decision, ready to be armed.
///
/// <para>
/// <b>Why this is data and not a ready-made scope.</b> Arming the decision means writing the
/// <see cref="CostRoutingAmbientState"/>'s <c>AsyncLocal</c>, and an <c>AsyncLocal</c> write only
/// reaches the caller if it happens in the caller's own frame: a write performed inside an
/// <c>async</c> method (this gate) is visible to that method's callees but is discarded when control
/// returns to the caller, because the caller's continuation resumes under the execution context it
/// captured at the <c>await</c> — before the write. Returning the decision and letting
/// <see cref="CostRoutingScope.Enter"/> arm it synchronously in <c>AgentStepExecutor</c>'s frame is
/// what makes the intent visible to the agent's chat-client resolution downstream. (Verified by
/// <c>AgentStepExecutorCostRoutingTests.Routing_intent_is_visible_inside_the_agent_run</c>, which
/// fails if the arming ever moves back across an await.)
/// </para>
/// </summary>
public sealed class CostRoutingDecision
{
    public required CostRoutingRunState RunState { get; init; }

    public required CostRoutingAmbientState Ambient { get; init; }

    /// <summary>
    /// Disposal-time half of the two-phase observation write, owned by the gate that created the
    /// decision (it closes over the scope factory and the step's cancellation token).
    /// </summary>
    public required Func<CostRoutingRunState, ValueTask> Complete { get; init; }
}

/// <summary>
/// The lifetime of one routing decision: entered before <c>agent.RunAsync(...)</c>, disposed after it
/// returns or throws. That disposal is the "after the run" hook the outcome update needs — it is the
/// only point in the step that is both guaranteed to execute (the compiler emits it as a
/// <c>finally</c>) and guaranteed to be after the run, without asking the executor for a second
/// statement.
/// </summary>
public sealed class CostRoutingScope : IAsyncDisposable
{
    /// <summary>
    /// The no-op scope every non-routed step gets. Shared and stateless — concurrent disposals are
    /// harmless because there is nothing to dispose.
    /// </summary>
    public static CostRoutingScope Disabled { get; } = new();

    private readonly Func<CostRoutingRunState, ValueTask>? _onDispose;
    private readonly CostRoutingRunState? _runState;
    private readonly IDisposable? _ambientToken;
    private bool _disposed;

    private CostRoutingScope()
    {
    }

    private CostRoutingScope(
        CostRoutingRunState runState, Func<CostRoutingRunState, ValueTask> onDispose, IDisposable ambientToken)
    {
        _runState = runState;
        _onDispose = onDispose;
        _ambientToken = ambientToken;
    }

    /// <summary>
    /// Arms <paramref name="decision"/> for the run about to start and returns the scope whose
    /// disposal completes it, or <see cref="Disabled"/> when there is nothing to route.
    ///
    /// <para>
    /// Synchronous, and deliberately so: this is called from the step executor's own frame, which is
    /// the only place an <c>AsyncLocal</c> write reaches the run. See <see cref="CostRoutingDecision"/>.
    /// </para>
    /// </summary>
    public static CostRoutingScope Enter(CostRoutingDecision? decision)
    {
        if (decision is null)
            return Disabled;

        IDisposable token = decision.Ambient.Begin(decision.RunState);

        return new CostRoutingScope(decision.RunState, decision.Complete, token);
    }

    /// <summary>True when a real routing decision was made and this scope has an outcome to write.</summary>
    public bool IsActive => _onDispose is not null;

    /// <summary>The run state this scope armed, for callers that need to observe it (null when disabled).</summary>
    public CostRoutingRunState? RunState => _runState;

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately NOT an <c>async</c> method. The ambient clear below must be observable in the
    /// frame that disposes this scope — an <c>AsyncLocal</c> write performed inside an <c>async</c>
    /// method never reaches its caller, and this scope is disposed by the compiler-generated
    /// <c>finally</c> of the executor's own <c>await using</c>. Clearing from a plain synchronous
    /// method called in that frame is what actually restores it; the (possibly asynchronous)
    /// outcome write is simply returned rather than awaited here.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;

        // Clear the ambient intent first: the routing decision applies to the run that just ended,
        // and anything resolving a chat client after this point (a retry attempt, a later step on
        // the same async flow) must not inherit a stale one.
        _ambientToken?.Dispose();

        return _onDispose is not null && _runState is not null
            ? _onDispose(_runState)
            : ValueTask.CompletedTask;
    }
}
