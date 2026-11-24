using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Workflows;

namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>Knobs for AI-assist proposals (<c>EditorProposals:*</c>).</summary>
public sealed class EditProposalOptions
{
    public const string SectionName = "EditorProposals";

    /// <summary>Proposals one user may ask for per project in a rolling hour (like <c>GraphicDesign:MaxPerHourPerUserProject</c>).</summary>
    public int MaxPerHourPerUserProject { get; set; } = 20;

    /// <summary>Wall clock (seconds) of one background proposal job; past it the model call is cancelled, the hold released and nothing charged.</summary>
    public double TimeoutSeconds { get; set; } = 300;

    /// <summary>Wall clock (seconds) of the synchronous route; must answer before the 120 s nginx location and the 100 s Cloudflare cut (pre-work runs before the bound).</summary>
    public double SyncTimeoutSeconds { get; set; } = 75;

    /// <summary>A background job nobody polled for this long (seconds) is cancelled (the tab was closed) and its hold released.</summary>
    public double AbandonAfterSeconds { get; set; } = 45;

    /// <summary>How long (seconds) a finished job's answer can still be read.</summary>
    public double ResultRetentionSeconds { get; set; } = 600;

    /// <summary>Background jobs one user may have running at once.</summary>
    public int MaxRunningPerUser { get; set; } = 3;

    /// <summary>Background jobs running at once store-wide (plus reserved slots); finished results do not count. Over it new ones are refused.</summary>
    public int MaxJobs { get; set; } = 500;

    /// <summary>Jobs one user may have stored at once (running and finished); the oldest delivered results are evicted first.</summary>
    public int MaxStoredPerUser { get; set; } = 10;

    /// <summary>How long (seconds) a delivered (or released) result is kept, so a re-read after a network blip still works.</summary>
    public double DeliveredRetentionSeconds { get; set; } = 60;
}

/// <summary>The state of one user+project window: how many proposals were asked in the last hour and, when full, the wait.</summary>
public sealed record EditProposalRateState(int Used, int Limit, int RetryAfterSeconds)
{
    public bool Full => Used >= Limit;

    /// <summary>The plain refusal sentence (no codes, no jargon).</summary>
    public string Message =>
        $"Suggestion limit reached: {Used} of {Limit} suggestions in this project in the last hour. Try again in {RunRateRefusal.Wait(RetryAfterSeconds)}.";
}

/// <summary>
/// Rolling-hour cap on proposals per user+project. A process-local sliding log (a singleton): the cap is a guard against
/// runaway spend, not a billing record, so each replica enforcing it on its own is acceptable; credits are still metered
/// centrally. Attempts count when they reach the model call, so a refused (402/429) or invalid request never uses one up.
/// </summary>
public sealed class EditProposalRateLimiter
{
    private readonly ConcurrentDictionary<(Guid User, Guid Project), Queue<DateTime>> _log = new();
    private readonly IOptions<EditProposalOptions> _options;
    private readonly TimeProvider _clock;

    public EditProposalRateLimiter(IOptions<EditProposalOptions> options, TimeProvider? clock = null)
    {
        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    private int Limit => Math.Max(1, _options.Value.MaxPerHourPerUserProject);

    /// <summary>The current state without counting anything (used by the estimate).</summary>
    public EditProposalRateState Peek(Guid user, Guid project)
    {
        Queue<DateTime> q = _log.GetOrAdd((user, project), _ => new Queue<DateTime>());
        lock (q) return Snapshot(q);
    }

    /// <summary>Counts one proposal when the window has room; otherwise answers the full state and counts nothing.</summary>
    public bool TryAcquire(Guid user, Guid project, out EditProposalRateState state)
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

    private EditProposalRateState Snapshot(Queue<DateTime> q)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        while (q.Count > 0 && q.Peek() <= now.AddHours(-1)) q.Dequeue();
        int retry = q.Count == 0 ? 0 : Math.Max(1, (int)Math.Ceiling((q.Peek().AddHours(1) - now).TotalSeconds));
        return new EditProposalRateState(q.Count, Limit, retry);
    }
}
