namespace ReelBolt.WorkflowEngine.Execution;

/// <summary>
/// Process-wide admission control for workflow executions, replacing the single semaphore that used
/// to be <c>WorkflowExecutionRunner</c>'s concurrency limit (A12).
///
/// <para>
/// There are <c>capacity</c> slots. A caller registers as a waiter for its organization; when a slot
/// is free it goes to the <i>eligible</i> organization with the fewest executions currently running,
/// first-come-first-served among that organization's waiters and among organizations tied on running
/// count. An organization already running as many executions as its own cap is not eligible, even
/// if slots are free, so one tenant can neither monopolise the engine nor exceed its plan.
/// </para>
///
/// <para>
/// Semantics are those of the semaphore it replaces: <see cref="AcquireAsync"/> is awaited in the
/// consumer before any work starts, so cancelling a waiter costs nothing. A waiter that is
/// cancelled before it is granted owns no slot and releases nothing; a slot is only ever returned
/// by disposing the lease that represents it, and disposing is idempotent.
/// </para>
///
/// <para>Fairness is per engine process only; cross-replica fairness belongs to the lease/queue work.</para>
/// </summary>
public sealed class FairShareAdmission
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<Guid, int> _running = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private int _totalRunning;

    public FairShareAdmission(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>Total slots.</summary>
    public int Capacity => _capacity;

    /// <summary>Slots currently granted. Test/diagnostic hook.</summary>
    public int RunningCount { get { lock (_gate) { return _totalRunning; } } }

    /// <summary>Callers currently waiting for a slot. Test/diagnostic hook.</summary>
    public int WaitingCount { get { lock (_gate) { return _waiters.Count; } } }

    /// <summary>Slots currently held by <paramref name="organizationId"/>.</summary>
    public int RunningFor(Guid organizationId)
    {
        lock (_gate)
        {
            return _running.GetValueOrDefault(organizationId);
        }
    }

    /// <summary>
    /// Waits for a slot for <paramref name="organizationId"/>, which may hold at most
    /// <paramref name="organizationCap"/> (values below 1 act as 1) at once. Throws
    /// <see cref="OperationCanceledException"/> if cancelled first, owning nothing.
    /// </summary>
    public Task<IDisposable> AcquireAsync(Guid organizationId, int organizationCap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var waiter = new Waiter(organizationId, Math.Max(1, organizationCap));
        lock (_gate)
        {
            waiter.Node = _waiters.AddLast(waiter);
            Pump();
        }

        if (waiter.Completion.Task.IsCompleted || !cancellationToken.CanBeCanceled)
        {
            return waiter.Completion.Task;
        }

        CancellationTokenRegistration registration = cancellationToken.Register(static state =>
        {
            var (self, w) = ((FairShareAdmission, Waiter))state!;
            self.CancelWaiter(w);
        }, (this, waiter));

        // The registration only matters until the waiter settles.
        _ = waiter.Completion.Task.ContinueWith(
            _ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return waiter.Completion.Task;
    }

    private void CancelWaiter(Waiter waiter)
    {
        lock (_gate)
        {
            // Already granted: the lease exists and its owner releases it. Cancelling must not
            // touch a slot it was never going to own, nor one it now does.
            if (waiter.Granted || waiter.Node is null)
            {
                return;
            }

            _waiters.Remove(waiter.Node);
            waiter.Node = null;
        }

        // No pump needed: removing a waiter frees no slot, and an ineligible head never blocked others.
        waiter.Completion.TrySetCanceled();
    }

    private void Release(Guid organizationId)
    {
        lock (_gate)
        {
            int left = _running.GetValueOrDefault(organizationId) - 1;
            if (left <= 0)
            {
                _running.Remove(organizationId);
            }
            else
            {
                _running[organizationId] = left;
            }

            _totalRunning--;
            Pump();
        }
    }

    /// <summary>Grants free slots. Caller holds <see cref="_gate"/>.</summary>
    private void Pump()
    {
        while (_totalRunning < _capacity)
        {
            Waiter? best = null;
            int bestRunning = int.MaxValue;
            HashSet<Guid> seen = new();

            // Walking in arrival order means the first waiter seen per org is that org's head.
            for (LinkedListNode<Waiter>? node = _waiters.First; node is not null; node = node.Next)
            {
                Waiter w = node.Value;
                if (!seen.Add(w.OrganizationId))
                {
                    continue;
                }

                int running = _running.GetValueOrDefault(w.OrganizationId);
                if (running >= w.Cap)
                {
                    continue;
                }

                // Strictly fewer wins; on a tie the earlier arrival (visited first) keeps it.
                if (running < bestRunning)
                {
                    best = w;
                    bestRunning = running;
                }
            }

            if (best is null)
            {
                return;
            }

            _waiters.Remove(best.Node!);
            best.Node = null;
            best.Granted = true;
            _running[best.OrganizationId] = bestRunning + 1;
            _totalRunning++;
            best.Completion.TrySetResult(new Lease(this, best.OrganizationId));
        }
    }

    private sealed class Waiter(Guid organizationId, int cap)
    {
        public Guid OrganizationId { get; } = organizationId;
        public int Cap { get; } = cap;
        public bool Granted { get; set; }
        public LinkedListNode<Waiter>? Node { get; set; }

        public TaskCompletionSource<IDisposable> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(FairShareAdmission owner, Guid organizationId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(organizationId);
            }
        }
    }
}
