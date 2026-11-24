namespace ReelBolt.WorkflowEngine.Agents;

/// <summary>
/// The agent run timeout, made visible to the tools the agent calls, so a tool whose duration is
/// legitimately unbounded by the agent's own thinking — a Remotion render that takes hours for a long
/// film — can stop that time counting against <c>Agents:&lt;name&gt;:RunTimeoutSeconds</c>.
/// </summary>
/// <remarks>
/// <see cref="ReelBoltAgentBase.RunAsync"/> installs one per run as <see cref="Current"/> (an
/// <see cref="AsyncLocal{T}"/>, so it flows into tool invocations and nowhere else). A tool wraps its
/// long call in <see cref="Exclude"/>: the deadline is pushed out by <c>maxDuration</c> while the call
/// runs (so the run cannot be cancelled mid-render) and, when it ends, settles at the original
/// deadline plus the time actually spent. The tool's own deadline still bounds the call.
/// </remarks>
public sealed class AgentRunBudget
{
    private static readonly AsyncLocal<AgentRunBudget?> CurrentBudget = new();

    private readonly CancellationTokenSource _timeout;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private DateTimeOffset _deadline;

    public AgentRunBudget(CancellationTokenSource timeout, TimeSpan budget, TimeProvider? time = null)
    {
        _timeout = timeout;
        _time = time ?? TimeProvider.System;
        _deadline = _time.GetUtcNow() + budget;
    }

    /// <summary>The budget of the agent run this async flow belongs to, if any.</summary>
    public static AgentRunBudget? Current
    {
        get => CurrentBudget.Value;
        internal set => CurrentBudget.Value = value;
    }

    /// <summary>The run's current deadline (for logs and tests).</summary>
    public DateTimeOffset Deadline
    {
        get { lock (_gate) return _deadline; }
    }

    /// <summary>
    /// Stops the time until the returned scope is disposed from counting against the run's budget.
    /// <paramref name="maxDuration"/> is the call's own upper bound.
    /// </summary>
    public IDisposable Exclude(TimeSpan maxDuration)
    {
        DateTimeOffset started = _time.GetUtcNow();
        lock (_gate)
        {
            Reschedule(_deadline + maxDuration);
        }
        return new Scope(this, started, maxDuration);
    }

    private void End(DateTimeOffset started, TimeSpan maxDuration)
    {
        TimeSpan spent = _time.GetUtcNow() - started;
        lock (_gate)
        {
            // Undo the provisional extension, then credit back exactly the time spent.
            _deadline = _deadline - maxDuration + spent;
            Reschedule(_deadline);
        }
    }

    private void Reschedule(DateTimeOffset deadline)
    {
        _deadline = deadline;
        TimeSpan remaining = deadline - _time.GetUtcNow();
        try
        {
            _timeout.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
        catch (ObjectDisposedException)
        {
            // The run already ended; nothing to reschedule.
        }
    }

    private sealed class Scope(AgentRunBudget budget, DateTimeOffset started, TimeSpan maxDuration) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                budget.End(started, maxDuration);
        }
    }
}
