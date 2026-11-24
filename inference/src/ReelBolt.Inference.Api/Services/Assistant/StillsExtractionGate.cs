using System.Collections.Concurrent;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>What <see cref="StillsExtractionGate.TryBegin"/> decided for one <c>LookAtOutput</c> ask.</summary>
public enum StillsRequestOutcome
{
    /// <summary>Nothing equivalent was asked for recently and the caller is under its bound: publish the request.</summary>
    Requested,

    /// <summary>
    /// The same stills set was asked for moments ago (another call — the same turn calling twice, or another member of
    /// the workspace): publish NOTHING and answer <c>pending</c>, because a second extraction produces the same pixels.
    /// </summary>
    AlreadyInFlight,

    /// <summary>The caller has asked for more stills sets than its window allows: refused, nothing published.</summary>
    Refused,
}

/// <summary>
/// Process-local bound on the stills extractions <c>LookAtOutput</c> may ask the derivative writer for (S-74).
///
/// <para>
/// The tool is free and its ask is a function of the <c>atSeconds</c> the caller sends, so without a bound any
/// member with View access can drive unbounded engine ffmpeg passes and unquota'd derivative storage simply by
/// varying the times. Two rules close that:
/// </para>
/// <list type="number">
/// <item><b>Dedupe.</b> One ask per (project, output, times) per <see cref="DedupeWindow"/>: a repeat publishes
/// nothing and reads as <c>pending</c>, which is what the caller would have got anyway. Keyed WITHOUT the user: the
/// extraction is the same work whichever member asked for it.</item>
/// <item><b>Per-caller bound.</b> At most <see cref="MaxRequestsPerUserPerWindow"/> extractions started by one user
/// in <see cref="Window"/>, counted only when the request is really published.</item>
/// </list>
///
/// <para>
/// Process-local on purpose, like <see cref="AssistantTurnRegistry"/>: this is a courtesy bound on expensive work,
/// not an authorization rule, and one replica's table is exactly as truthful as that replica is alive. Registered
/// as a singleton, so every scoped tool instance shares it.
/// </para>
/// </summary>
public sealed class StillsExtractionGate
{
    /// <summary>Extractions one caller may start in <see cref="Window"/>.</summary>
    public const int MaxRequestsPerUserPerWindow = 20;

    /// <summary>The rolling window the per-caller bound is counted over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>How long one stills ask suppresses an identical one.</summary>
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(30);

    /// <summary>Users tracked at most; the oldest window is dropped first. Keeps a long-lived process bounded.</summary>
    private const int MaxTrackedUsers = 10_000;

    private readonly object _sync = new();
    private readonly Dictionary<Guid, List<DateTimeOffset>> _startedByUser = [];
    private readonly ConcurrentDictionary<AskKey, DateTimeOffset> _asked = new();
    private readonly TimeProvider _clock;

    public StillsExtractionGate(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Decides whether <paramref name="userId"/>'s ask for the stills of <paramref name="outputId"/> at
    /// <paramref name="timesMs"/> may be published. <see cref="StillsRequestOutcome.Requested"/> is the only outcome
    /// that records anything (one extraction started, one dedupe entry), so a refused or a duplicate call costs the
    /// caller nothing.
    /// </summary>
    public StillsRequestOutcome TryBegin(Guid userId, Guid projectId, Guid outputId, IReadOnlyList<int> timesMs)
    {
        ArgumentNullException.ThrowIfNull(timesMs);

        DateTimeOffset now = _clock.GetUtcNow();
        AskKey key = new(projectId, outputId, string.Join(',', timesMs));

        lock (_sync)
        {
            Prune(now);

            if (_asked.TryGetValue(key, out DateTimeOffset askedAt) && now - askedAt < DedupeWindow)
                return StillsRequestOutcome.AlreadyInFlight;

            if (!_startedByUser.TryGetValue(userId, out List<DateTimeOffset>? started))
            {
                started = [];
                _startedByUser[userId] = started;
            }

            if (started.Count >= MaxRequestsPerUserPerWindow)
                return StillsRequestOutcome.Refused;

            started.Add(now);
            _asked[key] = now;
            return StillsRequestOutcome.Requested;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach ((Guid user, List<DateTimeOffset> started) in _startedByUser.ToList())
        {
            started.RemoveAll(at => now - at >= Window);
            if (started.Count == 0) _startedByUser.Remove(user);
        }

        foreach ((AskKey key, DateTimeOffset askedAt) in _asked)
            if (now - askedAt >= DedupeWindow) _asked.TryRemove(key, out _);

        // Nothing above bounds how MANY users a busy deployment accumulates inside one window; a caller is only ever
        // refused on its own count, so dropping the oldest entries can never let one user through twice.
        if (_startedByUser.Count > MaxTrackedUsers)
            foreach (Guid oldest in _startedByUser.OrderBy(u => u.Value[^1]).Take(_startedByUser.Count - MaxTrackedUsers).Select(u => u.Key).ToList())
                _startedByUser.Remove(oldest);
    }

    /// <summary>One ask: the output and the exact normalized times asked for.</summary>
    private readonly record struct AskKey(Guid ProjectId, Guid OutputId, string Times);
}
