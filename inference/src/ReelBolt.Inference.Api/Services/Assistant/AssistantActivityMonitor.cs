namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// Counts the interactive assistant turns in flight in this process, so BACKGROUND work that
/// shares a provider with the assistant (file summarization on the chat provider, file and docs
/// indexing on the embedding server) can step aside while someone is waiting for an answer.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a customer who uploaded a ~260-file repository and then asked the assistant
/// a question waited minutes for the reply: one summarization call per uploaded file was queued on
/// the same chat provider, and the per-file and documentation embeddings were queued on the same
/// CPU embedding server the assistant's documentation lookup needs. Nothing gave the person at the
/// keyboard priority.
/// </para>
/// <para>
/// Yielding is always BOUNDED (<see cref="YieldToInteractiveAsync"/> takes a maximum wait): a user
/// who keeps chatting all afternoon must slow background work down, never stop it for good.
/// A process-local counter is enough because a turn and the background work it competes with run in
/// the same Inference API process.
/// </para>
/// </remarks>
public sealed class AssistantActivityMonitor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private int _activeTurns;

    /// <summary>True while at least one assistant turn is being answered.</summary>
    public bool IsInteractiveBusy => Volatile.Read(ref _activeTurns) > 0;

    /// <summary>The number of assistant turns being answered right now.</summary>
    public int ActiveTurns => Volatile.Read(ref _activeTurns);

    /// <summary>Marks one turn as in flight until the returned handle is disposed (idempotent).</summary>
    public IDisposable BeginTurn()
    {
        Interlocked.Increment(ref _activeTurns);
        return new TurnHandle(this);
    }

    /// <summary>
    /// Waits while an assistant turn is in flight, for at most <paramref name="maxWait"/>. Returns
    /// immediately when nobody is chatting. Only <paramref name="ct"/> can make it throw.
    /// </summary>
    /// <returns>True when it actually waited (useful for logging), false when it did not need to.</returns>
    public async Task<bool> YieldToInteractiveAsync(TimeSpan maxWait, CancellationToken ct)
    {
        if (!IsInteractiveBusy || maxWait <= TimeSpan.Zero)
            return false;

        DateTime deadline = DateTime.UtcNow + maxWait;
        while (IsInteractiveBusy && DateTime.UtcNow < deadline)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, ct);
        }

        return true;
    }

    private void EndTurn() => Interlocked.Decrement(ref _activeTurns);

    private sealed class TurnHandle(AssistantActivityMonitor owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.EndTurn();
        }
    }
}
