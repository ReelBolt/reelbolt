namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// Generic background task queue backed by System.Threading.Channels.
/// </summary>
/// <remarks>
/// There is deliberately NO awaiting enqueue. The old <c>QueueAsync</c> waited for room in a
/// bounded channel, and it was awaited inside the upload request: once 100 files were waiting for
/// LLM summarization, every further upload blocked until the summarizer freed a slot, so a
/// 233-file repository took ~15 minutes to upload and a 2-byte file took 28 seconds. Callers now
/// persist the work as a row (e.g. <c>SummaryStatus.Pending</c>) and offer it with the
/// non-blocking <see cref="TryQueue"/>; when the queue is full the row simply stays pending and a
/// sweeper picks it up later.
/// </remarks>
public interface IBackgroundTaskQueue<T>
{
    /// <summary>
    /// Offers a task without ever waiting. Returns false when the queue is full or when the same
    /// task is already queued or being processed (so a sweeper re-offering pending work can never
    /// duplicate it).
    /// </summary>
    bool TryQueue(T task);

    ValueTask<T> DequeueAsync(CancellationToken ct);

    /// <summary>Marks a dequeued task finished, so the same task may be queued again later.</summary>
    void Complete(T task);

    /// <summary>True while the task is queued or being processed.</summary>
    bool IsTracked(T task);

    /// <summary>How many more tasks <see cref="TryQueue"/> would accept right now (approximate).</summary>
    int FreeCapacity { get; }

    /// <summary>How many tasks are queued or being processed right now.</summary>
    int TrackedCount { get; }
}
