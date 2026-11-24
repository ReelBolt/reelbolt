using System.Threading;
using Microsoft.Extensions.Options;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// D6: the engine-side serialisation of runner sessions per workflow execution.
/// <para>
/// <b>Why this exists (the D5b landmine).</b> <c>runner_sessions</c> carries a partial
/// <c>UNIQUE(execution_id) WHERE closed_at IS NULL</c>, so at most ONE session may be open for an
/// execution at a time. The gateway opens that row when a runner is ASSIGNED a job, deliberately
/// tolerates a violation (a savepoint, logged, and the job still runs without a session row of its
/// own) and does not try to fix it, because the constraint belongs to the schema. The consequence is
/// the engine's to own: a second concurrent session for the same execution gets no
/// <c>runner_sessions</c> row, and D5c's op routing — which finds the gateway replica that owns a
/// session through <c>runner_sessions.gateway_instance</c> — then has nothing to route with.
/// </para>
/// <para>
/// So the engine must never have two runner sessions outstanding in one execution. This gate is that
/// rule, expressed once: <see cref="AcquireAsync"/> hands out at most one lease per execution id, and
/// a lease is held for the whole window in which the session can be open — from the moment the job is
/// created (a runner may be assigned it at any instant) until the job reaches a terminal state. A
/// caller that releases early re-opens the window the schema closes.
/// </para>
/// <para>
/// <b>Scope of the guarantee.</b> In-process, and that is sufficient: an execution is run by exactly
/// one engine instance, which is what the execution lease (<c>WorkflowEngine:Lease</c>,
/// <c>ExecutionLeaseService</c>) establishes — a second instance cannot be running the same
/// execution's steps concurrently. The gateway's own tolerance stays the backstop for everything this
/// gate cannot see (a job created by an older build, a run migrated mid-flight).
/// </para>
/// <para>
/// <b>Different executions never block each other</b>, so one busy tenant cannot starve another: the
/// key is the execution, never the organization or the runner.
/// </para>
/// </summary>
public interface IExecutionSessionGate
{
    /// <summary>
    /// Wait for, and take, the one session slot of <paramref name="executionId"/>.
    /// <para>
    /// Returns a lease whose <see cref="ExecutionSessionLease.Acquired"/> is false when the wait
    /// expired — never an exception, because "another unit of this execution holds the session" is an
    /// ordinary answer the caller turns into a coded step failure. The wait is bounded on purpose: a
    /// flow that needed two sessions in one execution would otherwise wait for itself forever, so the
    /// bound converts a deadlock into a diagnosable failure.
    /// </para>
    /// </summary>
    /// <param name="executionId">The execution whose single session slot is being taken.</param>
    /// <param name="wait">How long to wait. Null uses <see cref="ComputeOptions.SessionWaitSeconds"/>.</param>
    /// <param name="cancellationToken">Cancels the wait; a cancelled acquisition never holds the slot.</param>
    ValueTask<ExecutionSessionLease> AcquireAsync(Guid executionId, TimeSpan? wait = null, CancellationToken cancellationToken = default);

    /// <summary>Take the slot only if it is free right now. Null means busy — the caller is expected to reorder or defer rather than wait.</summary>
    ValueTask<ExecutionSessionLease?> TryAcquireAsync(Guid executionId, CancellationToken cancellationToken = default);

    /// <summary>Whether <paramref name="executionId"/> currently has its session slot held. Diagnostics and tests.</summary>
    bool IsHeld(Guid executionId);

    /// <summary>
    /// How many executions currently hold their slot. Returns to zero once every lease is disposed,
    /// which is how a test proves the gate neither leaks an entry nor wedges an execution.
    /// </summary>
    int HeldExecutionCount { get; }
}

/// <summary>
/// One hold on an execution's session slot. Disposing it is what lets the next session open, so it
/// must be held for the job's whole life and disposed exactly once (<c>await using</c>).
/// </summary>
public sealed class ExecutionSessionLease : IAsyncDisposable
{
    private readonly Action? _release;
    private int _released;

    internal ExecutionSessionLease(Guid executionId, bool acquired, Action? release)
    {
        ExecutionId = executionId;
        Acquired = acquired;
        _release = release;
    }

    /// <summary>The execution this lease serialises.</summary>
    public Guid ExecutionId { get; }

    /// <summary>False when the wait expired: the caller must NOT open a session, and should fail the step with <c>RUNNER_SESSION_BUSY</c>.</summary>
    public bool Acquired { get; }

    /// <summary>A lease that was never acquired releases nothing, and disposing twice releases the slot once.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release?.Invoke();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The process-wide <see cref="IExecutionSessionGate"/>: one <see cref="SemaphoreSlim"/> per
/// execution, created on first use and dropped as soon as its last holder (or waiter) goes away, so
/// an engine that has run thousands of executions keeps no per-execution state afterwards.
/// </summary>
public sealed class ExecutionSessionGate : IExecutionSessionGate
{
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly object _sync = new();
    private readonly TimeSpan _defaultWait;

    public ExecutionSessionGate(IOptions<ComputeOptions> options)
        => _defaultWait = TimeSpan.FromSeconds(Math.Max(1, options.Value.SessionWaitSeconds));

    /// <inheritdoc />
    public async ValueTask<ExecutionSessionLease> AcquireAsync(
        Guid executionId,
        TimeSpan? wait = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan effective = wait ?? _defaultWait;
        Entry entry = Rent(executionId);
        bool taken;
        try
        {
            taken = effective <= TimeSpan.Zero
                ? await entry.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)
                : await entry.Gate.WaitAsync(effective, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Return(entry);
            throw;
        }

        if (!taken)
        {
            Return(entry);
            return new ExecutionSessionLease(executionId, acquired: false, release: null);
        }

        return new ExecutionSessionLease(executionId, acquired: true, release: () => Release(entry));
    }

    /// <inheritdoc />
    public async ValueTask<ExecutionSessionLease?> TryAcquireAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        Entry entry = Rent(executionId);
        bool taken;
        try
        {
            taken = await entry.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Return(entry);
            throw;
        }

        if (!taken)
        {
            Return(entry);
            return null;
        }

        return new ExecutionSessionLease(executionId, acquired: true, release: () => Release(entry));
    }

    /// <inheritdoc />
    public bool IsHeld(Guid executionId)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(executionId, out Entry? entry) && entry.Gate.CurrentCount == 0;
        }
    }

    /// <inheritdoc />
    public int HeldExecutionCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count(pair => pair.Value.Gate.CurrentCount == 0);
            }
        }
    }

    /// <summary>
    /// Takes a reference on an execution's entry, creating it if needed. The reference is what keeps a
    /// WAITER's entry alive: without it, a holder releasing just as a waiter is about to queue would
    /// drop the entry and hand the waiter a semaphore nobody else can see — i.e. two live sessions.
    /// </summary>
    private Entry Rent(Guid executionId)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(executionId, out Entry? entry))
            {
                entry = new Entry(executionId);
                _entries[executionId] = entry;
            }

            entry.References++;
            return entry;
        }
    }

    private void Release(Entry entry)
    {
        entry.Gate.Release();
        Return(entry);
    }

    private void Return(Entry entry)
    {
        lock (_sync)
        {
            entry.References--;
            if (entry.References <= 0)
            {
                _entries.Remove(entry.ExecutionId);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class Entry(Guid executionId)
    {
        public Guid ExecutionId { get; } = executionId;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int References { get; set; }
    }
}
