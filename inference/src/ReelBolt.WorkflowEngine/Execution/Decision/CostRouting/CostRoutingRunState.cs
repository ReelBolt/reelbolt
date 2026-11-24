using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Execution.Decision.CostRouting;

/// <summary>
/// The outcome of one routed agent run, as observed at the ONE layer routing actually controls: the
/// chat client the run was handed.
///
/// <para>
/// The ticket confines the <c>AgentStepExecutor</c> change to a single statement before
/// <c>agent.RunAsync(...)</c>, so there is no second hook after the run that could report the
/// step's terminal <c>StepStatus</c>. What IS observable without one is what the model itself did:
/// how many chat calls the run made, whether the last one returned or threw, and whether its text
/// carried a JSON object. That last field is not a proxy invented for convenience — it is the exact
/// signal <c>AgentStepExecutor</c>'s own schema check fails a run on
/// (<c>RobustJsonExtractor.ExtractLastJsonObject(result.Output) == null</c>), so "did the tier
/// produce a usable structured answer" is literally the validation the cheap tier was predicted to
/// pass or fail.
/// </para>
/// </summary>
public sealed record CostRoutingOutcome(
    string RoutedTo,
    Guid? RoutedProviderId,
    string? RoutedModel,
    int ChatCalls,
    bool LastCallSucceeded,
    bool LastResponseHadJsonObject,
    string? Error);

/// <summary>
/// Mutable, per-run routing state. One instance per routed agent step, created by the gate BEFORE
/// the run, read by <see cref="CostRoutingChatClientProvider"/> while the run builds its chat client,
/// written to by <see cref="CostRoutingRecordingChatClient"/> as model calls happen, and finally
/// serialized into the observation row's <c>DownstreamOutcomeJson</c> when the run's scope is
/// disposed.
///
/// <para>
/// Mutable by design: the run's outcome is not known when the row is first written, and the two
/// permitted write points (the chat client and the scope's disposal) are on either side of the same
/// async flow. The chat-call counter uses <see cref="Interlocked"/> because a tool-calling run may
/// interleave calls on the same instance; the last-call fields are plain because a torn read of them
/// would only ever mislabel one observation, never the run.
/// </para>
/// </summary>
public sealed class CostRoutingRunState
{
    public required Guid ObservationId { get; init; }
    public required Guid ExecutionId { get; init; }
    public required int StepOrder { get; init; }
    public required AgentType AgentType { get; init; }
    public required bool Escalated { get; init; }
    public required Guid? CheapProviderId { get; init; }
    public required Guid? ExpensiveProviderId { get; init; }

    private int _chatCalls;
    private bool _lastCallSucceeded;
    private bool _lastResponseHadJsonObject;
    private string? _error;
    private Guid? _routedProviderId;
    private string? _routedModel;

    public int ChatCalls => Volatile.Read(ref _chatCalls);

    public bool LastCallSucceeded => _lastCallSucceeded;

    public bool LastResponseHadJsonObject => _lastResponseHadJsonObject;

    public string? Error => _error;

    public Guid? RoutedProviderId => _routedProviderId;

    public string? RoutedModel => _routedModel;

    public void RecordChatCallSucceeded(bool responseHadJsonObject)
    {
        Interlocked.Increment(ref _chatCalls);
        _lastCallSucceeded = true;
        _lastResponseHadJsonObject = responseHadJsonObject;
        _error = null;
    }

    public void RecordChatCallFailed(string? error)
    {
        Interlocked.Increment(ref _chatCalls);
        _lastCallSucceeded = false;
        _lastResponseHadJsonObject = false;
        _error = error;
    }

    /// <summary>
    /// Records the provider the run was ACTUALLY handed, which is not always the tier the decision
    /// asked for: an escalated run whose expensive provider fails to resolve or to build falls back
    /// to the cheap path, and the observation must say what happened rather than what was intended.
    /// </summary>
    public void RecordRoutedProvider(Guid? providerId, string? model)
    {
        _routedProviderId = providerId;
        _routedModel = model;
    }

    public CostRoutingOutcome Snapshot() => new(
        RoutedTo: Escalated ? CostRoutingChatClientProvider.ExpensiveTier : CostRoutingChatClientProvider.CheapTier,
        RoutedProviderId: _routedProviderId,
        RoutedModel: _routedModel,
        ChatCalls: ChatCalls,
        LastCallSucceeded: _lastCallSucceeded,
        LastResponseHadJsonObject: _lastResponseHadJsonObject,
        Error: _error);
}

/// <summary>
/// Ambient (async-flow-local) routing intent: set by <c>CostRoutingGate</c> for the duration of one
/// agent step, read by <see cref="CostRoutingChatClientProvider"/> when the agent builds its chat
/// client inside <c>RunAsync</c>.
///
/// <para>
/// <see cref="AsyncLocal{T}"/> rather than a scoped service because the reader and the writer are on
/// opposite sides of a call the executor does not control: the gate sets the intent before
/// <c>agent.RunAsync(...)</c>, and the value must still be visible inside that awaited call —
/// including for the many registered agents, which are singletons and therefore cannot take a
/// per-step dependency. <c>WorkflowExecutionContextAccessor</c> uses the identical mechanism for the
/// identical reason, and <see cref="Begin"/> likewise restores the previous value rather than
/// clearing to null, so nesting (a routed step inside anything else that sets one) degrades to the
/// outer intent instead of losing it.
/// </para>
/// </summary>
public sealed class CostRoutingAmbientState
{
    private readonly AsyncLocal<CostRoutingRunState?> _current = new();

    public CostRoutingRunState? Current => _current.Value;

    public IDisposable Begin(CostRoutingRunState state)
    {
        CostRoutingRunState? previous = _current.Value;
        _current.Value = state;
        return new RestoreScope(() => _current.Value = previous);
    }

    private sealed class RestoreScope(Action restore) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            restore();
        }
    }
}
