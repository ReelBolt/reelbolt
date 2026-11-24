using System.Collections.Concurrent;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// Per-step state kept in a singleton tool: at most <c>capacity</c> entries, and when full the OLDEST entry is dropped
/// (never all of them, which would hand every running step a fresh allowance).
/// </summary>
internal sealed class StepKeyedState<T>(int capacity)
{
    private readonly ConcurrentDictionary<Guid, T> _map = new();
    private readonly ConcurrentQueue<Guid> _order = new();

    public int Count => _map.Count;

    public T GetOrAdd(Guid key, Func<T> create)
    {
        if (_map.TryGetValue(key, out T? existing))
            return existing;
        T created = _map.GetOrAdd(key, _ => create());
        _order.Enqueue(key);
        while (_map.Count > capacity && _order.TryDequeue(out Guid oldest))
        {
            if (oldest != key)
                _map.TryRemove(oldest, out _);
        }

        return created;
    }
}

/// <summary>
/// One step's footage reads: a call counter and the loop guard. A read is keyed WITHOUT its cursor; a cursor is honoured only when
/// it is exactly the <c>next</c> the previous answer to that same read gave. A cursor on a first call, or on a read whose
/// answer was complete, is ignored, so the model cannot page through an answer that has no more pages.
/// </summary>
internal sealed class StepCallCounter
{
    /// <summary>Distinct reads remembered per step; past this the guard answers with the read-limit note.</summary>
    internal const int MaxTrackedReads = 64;

    private int _calls;
    private readonly Dictionary<string, string?> _lastNext = [];

    public int Increment() => Interlocked.Increment(ref _calls);

    /// <summary>What to do with a call: run it (with the cursor to use), or answer that it was already returned.</summary>
    public (bool Run, string? Cursor) Admit(string readKey, string? cursor)
    {
        lock (_lastNext)
        {
            if (!_lastNext.TryGetValue(readKey, out string? next))
            {
                if (_lastNext.Count >= MaxTrackedReads)
                    return (false, null);
                _lastNext[readKey] = null;
                return (true, null);
            }

            // Seen before: only the exact next of the previous answer continues it.
            return next is not null && string.Equals(cursor?.Trim(), next, StringComparison.Ordinal) ? (true, next) : (false, null);
        }
    }

    /// <summary>Records the <c>next</c> cursor (null when the answer was complete) of the answer just given.</summary>
    public void Record(string readKey, string? next)
    {
        lock (_lastNext)
            _lastNext[readKey] = next;
    }
}
