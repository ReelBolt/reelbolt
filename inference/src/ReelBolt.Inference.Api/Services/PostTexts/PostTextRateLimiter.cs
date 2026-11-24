using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Workflows;

namespace ReelBolt.Inference.Api.Services.PostTexts;

/// <summary>Knobs for post text (<c>PostTexts:*</c>).</summary>
public sealed class PostTextOptions
{
    public const string SectionName = "PostTexts";

    /// <summary>Post texts one user may ask for per project in a rolling hour.</summary>
    public int MaxPerHourPerUserProject { get; set; } = 20;

    /// <summary>Wall clock (seconds) of the model call; past it the call is cancelled, the hold released and nothing charged.</summary>
    public double TimeoutSeconds { get; set; } = 75;
}

/// <summary>How many post texts were asked in the last hour for one user and project and, when full, the wait.</summary>
public sealed record PostTextRateState(int Used, int Limit, int RetryAfterSeconds)
{
    public bool Full => Used >= Limit;

    public string Message =>
        $"Post text limit reached: {Used} of {Limit} requests in this project in the last hour. Try again in {RunRateRefusal.Wait(RetryAfterSeconds)}.";
}

/// <summary>
/// Rolling-hour cap on post text requests per user+project: a process-local sliding log (same trade-off as the
/// proposal limiter). A request counts when it reaches the model call, so a refused or invalid one never uses a slot.
/// </summary>
public sealed class PostTextRateLimiter
{
    private readonly ConcurrentDictionary<(Guid User, Guid Project), Queue<DateTime>> _log = new();
    private readonly IOptions<PostTextOptions> _options;
    private readonly TimeProvider _clock;

    public PostTextRateLimiter(IOptions<PostTextOptions> options, TimeProvider? clock = null)
    {
        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    private int Limit => Math.Max(1, _options.Value.MaxPerHourPerUserProject);

    public PostTextRateState Peek(Guid user, Guid project)
    {
        Queue<DateTime> q = _log.GetOrAdd((user, project), _ => new Queue<DateTime>());
        lock (q) return Snapshot(q);
    }

    public bool TryAcquire(Guid user, Guid project, out PostTextRateState state)
    {
        Queue<DateTime> q = _log.GetOrAdd((user, project), _ => new Queue<DateTime>());
        lock (q)
        {
            state = Snapshot(q);
            if (state.Full) return false;
            q.Enqueue(_clock.GetUtcNow().UtcDateTime);
            state = state with { Used = state.Used + 1 };
            return true;
        }
    }

    private PostTextRateState Snapshot(Queue<DateTime> q)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        while (q.Count > 0 && q.Peek() <= now.AddHours(-1)) q.Dequeue();
        int retry = q.Count == 0 ? 0 : Math.Max(1, (int)Math.Ceiling((q.Peek().AddHours(1) - now).TotalSeconds));
        return new PostTextRateState(q.Count, Limit, retry);
    }
}
