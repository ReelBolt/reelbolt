namespace ReelBolt.Inference.Api.Services.Billing.Promotions;

/// <summary>
/// Slows down promo-code guessing: at most <see cref="MaxFailures"/> counted code attempts per user AND
/// per organization in a sliding <see cref="Window"/>, shared by redeem, checkout and preview.
/// <para>
/// Reserve-then-check: <see cref="TryBegin"/> counts the attempt atomically before the code is looked
/// up, so concurrent guesses cannot all pass a check made before any of them was recorded; a request
/// whose code turns out valid hands its slot back with <see cref="Succeeded"/>. The org key stops a
/// team from multiplying the budget by adding members.
/// </para>
/// <para>
/// Memory is bounded: a key's queue never holds more than <see cref="MaxFailures"/> entries, and keys
/// idle for a whole window are swept every <see cref="SweepInterval"/>. In memory and per process (a
/// singleton): a restart forgets and each replica counts on its own, which is acceptable for a brake on
/// guessing, not a lock; move to a shared store once the API runs more than one replica.
/// </para>
/// </summary>
public sealed class PromotionCodeAttemptLimiter(TimeProvider time)
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    /// <summary>One counted attempt; pass it to <see cref="Succeeded"/> when the code was valid.</summary>
    public sealed record Attempt(Guid UserId, Guid OrgId, DateTimeOffset At);

    private readonly Dictionary<(bool IsOrg, Guid Id), Queue<DateTimeOffset>> _attempts = [];
    private readonly Lock _gate = new();
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    /// <summary>Keys currently tracked (users and orgs); for tests and diagnostics.</summary>
    public int TrackedKeys
    {
        get
        {
            lock (_gate)
            {
                return _attempts.Count;
            }
        }
    }

    /// <summary>The longest queue held for any key; never above <see cref="MaxFailures"/>.</summary>
    public int LargestQueue
    {
        get
        {
            lock (_gate)
            {
                return _attempts.Count == 0 ? 0 : _attempts.Values.Max(q => q.Count);
            }
        }
    }

    /// <summary>
    /// Counts one attempt for the user and the org, or returns null (nothing counted) when either has
    /// already used its <see cref="MaxFailures"/> in the window.
    /// </summary>
    public Attempt? TryBegin(Guid userId, Guid orgId)
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_gate)
        {
            SweepIfDue(now);
            if (Count((false, userId), now) >= MaxFailures || Count((true, orgId), now) >= MaxFailures)
            {
                return null;
            }

            Push((false, userId), now);
            Push((true, orgId), now);
            return new Attempt(userId, orgId, now);
        }
    }

    /// <summary>Hands the attempt's slot back: a valid code is not a guess.</summary>
    public void Succeeded(Attempt attempt)
    {
        lock (_gate)
        {
            Remove((false, attempt.UserId), attempt.At);
            Remove((true, attempt.OrgId), attempt.At);
        }
    }

    private int Count((bool, Guid) key, DateTimeOffset now)
    {
        if (!_attempts.TryGetValue(key, out Queue<DateTimeOffset>? queue))
        {
            return 0;
        }

        DateTimeOffset cutoff = now - Window;
        while (queue.Count > 0 && queue.Peek() <= cutoff)
        {
            queue.Dequeue();
        }

        if (queue.Count == 0)
        {
            _attempts.Remove(key);
        }

        return queue.Count;
    }

    private void Push((bool, Guid) key, DateTimeOffset now)
    {
        if (!_attempts.TryGetValue(key, out Queue<DateTimeOffset>? queue))
        {
            queue = new Queue<DateTimeOffset>(MaxFailures);
            _attempts[key] = queue;
        }

        queue.Enqueue(now);
    }

    private void Remove((bool, Guid) key, DateTimeOffset at)
    {
        if (!_attempts.TryGetValue(key, out Queue<DateTimeOffset>? queue))
        {
            return;
        }

        bool removed = false;
        int n = queue.Count;
        for (int i = 0; i < n; i++)
        {
            DateTimeOffset entry = queue.Dequeue();
            if (!removed && entry == at)
            {
                removed = true;
                continue;
            }

            queue.Enqueue(entry);
        }

        if (queue.Count == 0)
        {
            _attempts.Remove(key);
        }
    }

    /// <summary>Drops every key with nothing left in its window, so idle users and orgs cost nothing.</summary>
    private void SweepIfDue(DateTimeOffset now)
    {
        if (now - _lastSweep < SweepInterval)
        {
            return;
        }

        _lastSweep = now;
        foreach ((bool, Guid) key in _attempts.Keys.ToList())
        {
            Count(key, now);
        }
    }
}
