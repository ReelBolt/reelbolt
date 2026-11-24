namespace ReelBolt.ImportWorker;

/// <summary>
/// The worker's concurrency limit. Zip and PDF handlers each buffer up to tens of MiB and burn CPU on
/// hostile input inside a 512 MiB container, so only <c>MaxConcurrency</c> of them run at once; the rest are
/// refused immediately (503 + Retry-After) rather than queued, because a queue of buffered bodies is itself the
/// exhaustion. The slot is taken BEFORE the request body is read.
/// </summary>
public sealed class WorkerGate
{
    public const int RetryAfterSeconds = 5;

    private readonly SemaphoreSlim _slots;

    public WorkerGate(int maxConcurrency) => _slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);

    /// <summary>Returns a lease that frees the slot on dispose, or null when every slot is busy.</summary>
    public Lease? TryEnter() => _slots.Wait(0) ? new Lease(_slots) : null;

    public static WorkerResult Busy() => new(503, new ErrorResponse("WORKER_BUSY"));

    public sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _slots;

        internal Lease(SemaphoreSlim slots) => _slots = slots;

        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
