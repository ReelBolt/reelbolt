using System.Collections.Concurrent;

namespace ReelBolt.Inference.Api.Services.Editing.ReviewLinks;

/// <summary>
/// A per-link sliding window over guest writes (comments and decisions). <b>In-memory, so per API replica</b>:
/// with N replicas a link can see up to N times the configured rate. The durable bounds are the database
/// counts (200 guest comments per link, 20 decisions per link) and the nginx write zone in front of the API.
/// </summary>
public sealed class ReviewLinkWriteLimiter
{
    private const int MaxTrackedLinks = 10_000;

    private readonly ConcurrentDictionary<Guid, Queue<DateTime>> _windows = new();

    /// <summary>
    /// Records one write for <paramref name="linkId"/> and returns true, or returns false (without recording)
    /// when the link already made <paramref name="limit"/> writes in the last minute.
    /// </summary>
    public bool TryAcquire(Guid linkId, DateTime nowUtc, int limit)
    {
        if (limit <= 0) return false;
        if (_windows.Count > MaxTrackedLinks) Sweep(nowUtc);

        Queue<DateTime> window = _windows.GetOrAdd(linkId, _ => new Queue<DateTime>());
        lock (window)
        {
            DateTime cutoff = nowUtc.AddMinutes(-1);
            while (window.Count > 0 && window.Peek() <= cutoff) window.Dequeue();
            if (window.Count >= limit) return false;
            window.Enqueue(nowUtc);
            return true;
        }
    }

    private void Sweep(DateTime nowUtc)
    {
        DateTime cutoff = nowUtc.AddMinutes(-1);
        foreach (KeyValuePair<Guid, Queue<DateTime>> entry in _windows)
        {
            lock (entry.Value)
            {
                while (entry.Value.Count > 0 && entry.Value.Peek() <= cutoff) entry.Value.Dequeue();
                if (entry.Value.Count == 0) _windows.TryRemove(entry.Key, out _);
            }
        }
    }
}
