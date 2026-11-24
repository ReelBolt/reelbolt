namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// Bounds <c>POST billing/checkout/confirm</c> (in-process sliding windows): each confirmation costs several provider reads,
/// so a client polling it, many organizations, or many users must not turn into a provider-API storm. At most
/// <see cref="MaxPerMinute"/> calls per organization and per user per minute, and <see cref="MaxGlobalPerMinute"/> in
/// total. Idle entries are evicted. A short result cache (<see cref="ResultTtl"/>) replays the last settled answer
/// for an (organization, session) without any provider call.
/// </summary>
public sealed class CheckoutConfirmLimiter
{
    public const int MaxPerMinute = 10;
    public const int MaxGlobalPerMinute = 120;
    public static readonly TimeSpan ResultTtl = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>The instance a controller built without DI falls back to.</summary>
    public static CheckoutConfirmLimiter Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Queue<DateTime>> _orgs = [];
    private readonly Dictionary<Guid, Queue<DateTime>> _users = [];
    private readonly Queue<DateTime> _global = new();
    private readonly Dictionary<(Guid Org, string Session), (string Status, string Kind, DateTime At)> _results = [];
    private DateTime _lastSweep = DateTime.MinValue;

    /// <summary>
    /// True (and the call is not counted anywhere) when the organization, the user (when known) or the process already
    /// used its calls in the last minute.
    /// </summary>
    public bool IsLimited(Guid organizationId, DateTime utcNow, Guid? userId = null)
    {
        lock (_gate)
        {
            Sweep(utcNow);
            Trim(_global, utcNow);
            Queue<DateTime> org = Window_(_orgs, organizationId, utcNow);
            Queue<DateTime>? user = userId is { } u ? Window_(_users, u, utcNow) : null;
            if (_global.Count >= MaxGlobalPerMinute || org.Count >= MaxPerMinute || (user is not null && user.Count >= MaxPerMinute))
            {
                return true;
            }

            _global.Enqueue(utcNow);
            org.Enqueue(utcNow);
            user?.Enqueue(utcNow);
            return false;
        }
    }

    /// <summary>The last settled answer for this session within <see cref="ResultTtl"/>, if any.</summary>
    public bool TryGetResult(Guid organizationId, string sessionId, DateTime utcNow, out (string Status, string Kind) result)
    {
        lock (_gate)
        {
            if (_results.TryGetValue((organizationId, sessionId), out var hit) && utcNow - hit.At < ResultTtl)
            {
                result = (hit.Status, hit.Kind);
                return true;
            }

            result = default;
            return false;
        }
    }

    /// <summary>Remembers a settled (<c>confirmed</c>/<c>abandoned</c>) answer; anything else is never cached.</summary>
    public void StoreResult(Guid organizationId, string sessionId, string status, string kind, DateTime utcNow)
    {
        if (status is not ("confirmed" or "abandoned")) return;
        lock (_gate)
        {
            _results[(organizationId, sessionId)] = (status, kind, utcNow);
        }
    }

    private static void Trim(Queue<DateTime> q, DateTime now)
    {
        while (q.Count > 0 && now - q.Peek() >= Window) q.Dequeue();
    }

    private static Queue<DateTime> Window_(Dictionary<Guid, Queue<DateTime>> map, Guid key, DateTime now)
    {
        if (!map.TryGetValue(key, out Queue<DateTime>? q)) map[key] = q = new Queue<DateTime>();
        Trim(q, now);
        return q;
    }

    /// <summary>Drops idle windows and expired results, at most once a minute.</summary>
    private void Sweep(DateTime now)
    {
        if (now - _lastSweep < Window) return;
        _lastSweep = now;
        foreach (Dictionary<Guid, Queue<DateTime>> map in new[] { _orgs, _users })
        {
            foreach (Guid key in map.Keys.ToList())
            {
                Trim(map[key], now);
                if (map[key].Count == 0) map.Remove(key);
            }
        }

        foreach (var key in _results.Where(r => now - r.Value.At >= ResultTtl).Select(r => r.Key).ToList()) _results.Remove(key);
    }

    /// <summary>Entries currently held (for tests of the eviction).</summary>
    internal int TrackedEntries
    {
        get { lock (_gate) return _orgs.Count + _users.Count + _results.Count; }
    }
}
