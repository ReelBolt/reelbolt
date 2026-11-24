using System.Collections.Concurrent;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// Which assistant threads have a turn being answered right now, in this process. The thread list
/// and thread detail read it so the panel can tell a reply that is still being worked on ("Still
/// answering…") apart from one that will never come because its request died ("No reply yet —
/// ask again"). Both look identical in the database: a thread whose newest message is the user's.
/// </summary>
/// <remarks>
/// Process-local on purpose: the turn runs in this process, so this is exactly as truthful as the
/// process is alive — a restart forgets in-flight turns, and those really are dead.
/// </remarks>
public sealed class AssistantTurnRegistry
{
    private readonly ConcurrentDictionary<Guid, int> _inFlight = new();

    /// <summary>Marks a turn on <paramref name="threadId"/> as in flight until the handle is disposed.</summary>
    public IDisposable Begin(Guid threadId)
    {
        _inFlight.AddOrUpdate(threadId, 1, (_, count) => count + 1);
        return new Handle(this, threadId);
    }

    public bool IsInFlight(Guid threadId) => _inFlight.ContainsKey(threadId);

    private void End(Guid threadId)
    {
        while (_inFlight.TryGetValue(threadId, out int count))
        {
            if (count <= 1)
            {
                if (_inFlight.TryRemove(new KeyValuePair<Guid, int>(threadId, count)))
                    return;
            }
            else if (_inFlight.TryUpdate(threadId, count - 1, count))
            {
                return;
            }
        }
    }

    private sealed class Handle(AssistantTurnRegistry owner, Guid threadId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.End(threadId);
        }
    }
}
