using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>Who a wake turn runs as, read from the database at wake time (never from anything stored on the watch).</summary>
public sealed record WakeIdentity(Guid UserId, string Email, Guid OrganizationId, OrganizationRole Role, bool IsPlatformAdmin, bool EmailVerified);

public enum WakeOutcomeKind { Fired, Retry, Cancelled }

/// <summary>What happened when a wake turn ran. <c>Reason</c> is a short code for Cancelled/Retry.</summary>
public sealed record WakeOutcome(WakeOutcomeKind Kind, Guid? MessageId = null, string? Reason = null)
{
    public static WakeOutcome Fired(Guid messageId) => new(WakeOutcomeKind.Fired, messageId);
    public static WakeOutcome Retry(string reason) => new(WakeOutcomeKind.Retry, null, reason);
    public static WakeOutcome Cancelled(string reason) => new(WakeOutcomeKind.Cancelled, null, reason);
}

/// <summary>Runs one wake turn in the watch's thread as a normal assistant turn. Seam so the sweeper is testable without a model.</summary>
public interface IAssistantWakeTurnRunner
{
    Task<WakeOutcome> RunAsync(AssistantRunWatch watch, WakeIdentity identity, string wakeText, AssistantWakeRun run, CancellationToken ct);
}

/// <summary>The terminal run a wake is about.</summary>
public sealed record AssistantWakeRun(Guid ExecutionId, ExecutionStatus Status, string RunLabel);

/// <summary>
/// One sweep of the run watches: expire, release lapsed claims, find watches whose run is terminal, claim each with a
/// single conditional UPDATE (so replicas never double-fire), re-check authority from the database and wake the thread.
/// </summary>
/// <remarks>
/// While a watch is Pending, <c>ClaimedUntil</c> doubles as "do not retry before" after a transient failure.
/// </remarks>
public sealed class AssistantRunWakeSweeper(
    InferenceApiDbContext db,
    IAssistantWakeTurnRunner runner,
    ILogger<AssistantRunWakeSweeper> logger,
    AssistantTurnRegistry? registry = null,
    AssistantTurnHub? hub = null,
    IConfiguration? configuration = null,
    TimeProvider? clock = null,
    ITenantContext? tenant = null,
    Func<(InferenceApiDbContext Db, IDisposable Lease)>? workerContexts = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Wake turns running in this process, and the threads they run in: the concurrency bound and the one-turn-per-thread rule
    /// hold across overlapping sweeps.
    /// </summary>
    private static int s_running;

    /// <summary>Wake turns running in this process right now (drain activeWork).</summary>
    public static int RunningWakeTurns => Math.Max(0, Volatile.Read(ref s_running));
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> s_threads = new();

    /// <summary>
    /// Housekeeping (expire, release lapsed claims), then the due wakes. With <c>workerContexts</c> the wakes run in parallel,
    /// each on its own context, bounded by <c>MaxConcurrentWakes</c> and one per thread; without it they run one after the other
    /// on this context. Housekeeping never waits for a turn. Returns how many watches fired.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;

        await db.AssistantRunWatches
            .Where(w => w.Status == AssistantRunWatchStatus.Pending && w.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, AssistantRunWatchStatus.Expired)
                .SetProperty(w => w.EndReason, "expired"), ct);

        // A replica that died holding a claim: back to Pending, attempts kept.
        await db.AssistantRunWatches
            .Where(w => w.Status == AssistantRunWatchStatus.Claimed && w.ClaimedUntil != null && w.ClaimedUntil < now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, AssistantRunWatchStatus.Pending)
                .SetProperty(w => w.ClaimedUntil, (DateTime?)null), ct);

        var terminal = new[] { ExecutionStatus.Passed, ExecutionStatus.Failed, ExecutionStatus.Cancelled };
        var due = await (
            from w in db.AssistantRunWatches.AsNoTracking()
            where w.Status == AssistantRunWatchStatus.Pending && (w.ClaimedUntil == null || w.ClaimedUntil <= now)
            join e in db.WorkflowExecutions.AsNoTracking().IgnoreQueryFilters() on w.ExecutionId equals e.Id
            where terminal.Contains(e.Status)
            orderby w.CreatedAt
            select new { w.Id, w.ThreadId, ExecutionStatus = e.Status })
            .Take(AssistantRunWakeLimits.BatchSize(configuration) * 4)
            .ToListAsync(ct);

        // At most one wake per thread in a sweep; the rest wait for the next one.
        var items = due.GroupBy(d => d.ThreadId).Select(g => g.First())
            .Take(AssistantRunWakeLimits.BatchSize(configuration)).ToList();

        if (workerContexts is null)
        {
            int fired = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (await WakeAsync(item.Id, item.ThreadId, item.ExecutionStatus, ct))
                    fired++;
            }

            return fired;
        }

        var tasks = items.Select(item => Task.Run(async () =>
        {
            (InferenceApiDbContext workerDb, IDisposable lease) = workerContexts();
            using (lease)
            {
                var worker = new AssistantRunWakeSweeper(workerDb, runner, logger, registry, hub, configuration, _clock, tenant);
                return await worker.WakeAsync(item.Id, item.ThreadId, item.ExecutionStatus, ct);
            }
        }, ct)).ToList();
        return (await Task.WhenAll(tasks)).Count(f => f);
    }

    private bool ThreadBusy(Guid threadId) =>
        (registry?.IsInFlight(threadId) ?? false)
        || (hub ?? AssistantTurnHub.Shared).Find(threadId) is { State: AssistantTurnState.Running or AssistantTurnState.AwaitingApproval };

    private async Task<bool> WakeAsync(Guid watchId, Guid threadId, ExecutionStatus runStatus, CancellationToken ct)
    {
        // A person typing (or another turn writing) in the thread: leave it Pending, spend no attempt.
        if (ThreadBusy(threadId))
            return false;

        // The concurrency bound and the one-per-thread rule, admitted before anything is claimed.
        if (Interlocked.Increment(ref s_running) > AssistantRunWakeLimits.MaxConcurrentWakes(configuration))
        {
            Interlocked.Decrement(ref s_running);
            return false;
        }

        if (!s_threads.TryAdd(threadId, 0))
        {
            Interlocked.Decrement(ref s_running);
            return false;
        }

        try
        {
            return await WakeClaimedAsync(watchId, runStatus, ct);
        }
        finally
        {
            s_threads.TryRemove(threadId, out _);
            Interlocked.Decrement(ref s_running);
        }
    }

    private async Task<bool> WakeClaimedAsync(Guid watchId, ExecutionStatus runStatus, CancellationToken ct)
    {
        // The lease is computed at claim time, per item, so a long sweep never hands out a lease that is already short.
        DateTime claimedAt = _clock.GetUtcNow().UtcDateTime;
        DateTime leaseEnd = claimedAt + AssistantRunWakeLimits.ClaimLease(configuration);

        // The ONE conditional UPDATE: only a replica that sees Pending wins the claim.
        int claimed = await db.AssistantRunWatches
            .Where(w => w.Id == watchId && w.Status == AssistantRunWatchStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, AssistantRunWatchStatus.Claimed)
                .SetProperty(w => w.ClaimedUntil, leaseEnd)
                .SetProperty(w => w.Attempts, w => w.Attempts + 1), ct);
        if (claimed != 1)
            return false;

        AssistantRunWatch watch = await db.AssistantRunWatches.AsNoTracking().FirstAsync(w => w.Id == watchId, ct);
        int attempt = watch.Attempts;

        // Every later write is guarded by Status == Claimed AND this claim's attempt number, so a stale replica (one whose lease
        // lapsed and whose row was re-claimed by another) cannot overwrite the newer claim's outcome.
        IQueryable<AssistantRunWatch> mine = db.AssistantRunWatches
            .Where(w => w.Id == watchId && w.Status == AssistantRunWatchStatus.Claimed && w.Attempts == attempt);

        (WakeIdentity? identity, string? denied) = await AuthorizeAsync(watch, ct);
        if (identity is null)
        {
            await EndAsync(mine, watchId, AssistantRunWatchStatus.Cancelled, denied!, ct);
            return false;
        }

        if (await CapReachedAsync(watch, claimedAt, ct))
        {
            await EndAsync(mine, watchId, AssistantRunWatchStatus.Cancelled, "wake_cap", ct);
            return false;
        }

        string runLabel = AssistantRunWakeText.Name(watch.WorkflowName);
        string text = AssistantRunWakeText.Build(watch.ExecutionId, runStatus);
        WakeOutcome outcome;
        try
        {
            // The sweep's bookkeeping runs in the system tenant scope (it spans every organization), but the TURN must not:
            // it runs as the user's own organization, so the tenant query filters apply to everything the turn reads.
            using (tenant.Organization(watch.OrganizationId))
            {
                outcome = await runner.RunAsync(watch, identity, text, new AssistantWakeRun(watch.ExecutionId, runStatus, runLabel), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: give the claim back, spend nothing further.
            await mine.ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, AssistantRunWatchStatus.Pending)
                .SetProperty(w => w.ClaimedUntil, (DateTime?)null), CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Run wake {Watch} failed to run its turn", Short(watchId));
            outcome = WakeOutcome.Retry("turn_failed");
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        switch (outcome.Kind)
        {
            case WakeOutcomeKind.Fired:
                int wrote = await mine.ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.Status, AssistantRunWatchStatus.Fired)
                        .SetProperty(w => w.FiredAt, now)
                        .SetProperty(w => w.WakeMessageId, outcome.MessageId)
                        .SetProperty(w => w.ClaimedUntil, (DateTime?)null), ct);
                if (wrote != 1)
                    return false;
                logger.LogInformation("Run wake {Watch} fired in thread {Thread} ({Status})", Short(watchId), Short(watch.ThreadId), AssistantRunWakeText.Word(runStatus));
                return true;
            case WakeOutcomeKind.Cancelled:
                await EndAsync(mine, watchId, AssistantRunWatchStatus.Cancelled, outcome.Reason ?? "cancelled", ct);
                return false;
            default:
                // A turn that RAN but left no reply may already have done tool work, so re-running it is capped lower
                // (MaxNoReplyReruns) than a turn that failed before doing anything (MaxAttempts). A turn that did produce a
                // reply is never a Retry: the runner reports it as Fired.
                int maxAttempts = outcome.Reason == "no_reply"
                    ? Math.Min(AssistantRunWakeLimits.MaxAttempts(configuration), 1 + AssistantRunWakeLimits.MaxNoReplyReruns)
                    : AssistantRunWakeLimits.MaxAttempts(configuration);
                if (attempt >= maxAttempts)
                {
                    // The wake user message stays unanswered: the existing "No reply yet / Ask again" path applies.
                    await EndAsync(mine, watchId, AssistantRunWatchStatus.Abandoned, outcome.Reason ?? "max_attempts", ct);
                }
                else
                {
                    DateTime notBefore = now + TimeSpan.FromSeconds(30 * attempt);
                    await mine.ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.Status, AssistantRunWatchStatus.Pending)
                        .SetProperty(w => w.ClaimedUntil, (DateTime?)notBefore), ct);
                }

                return false;
        }
    }

    private Task<int> EndAsync(IQueryable<AssistantRunWatch> mine, Guid watchId, AssistantRunWatchStatus status, string reason, CancellationToken ct)
    {
        logger.LogInformation("Run wake {Watch} ended {Status}: {Reason}", Short(watchId), status, reason);
        return mine.ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, status)
                .SetProperty(w => w.EndReason, reason)
                .SetProperty(w => w.ClaimedUntil, (DateTime?)null), ct);
    }

    /// <summary>Re-reads everything the wake relies on from the database; a short denial code when any of it no longer holds.</summary>
    internal async Task<(WakeIdentity? Identity, string? Denied)> AuthorizeAsync(AssistantRunWatch watch, CancellationToken ct)
    {
        bool threadOk = await db.AssistantThreads.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(t => t.Id == watch.ThreadId && t.ArchivedAt == null
                && t.UserId == watch.UserId && t.OrganizationId == watch.OrganizationId, ct);
        if (!threadOk)
            return (null, "thread_gone");

        var user = await db.ApplicationUsers.AsNoTracking()
            .Where(u => u.Id == watch.UserId)
            .Select(u => new { u.Email, u.IsAdmin, u.DeletionRequestedAt, u.EmailVerifiedAt, u.TokensInvalidatedAt, u.MustChangePassword })
            .FirstOrDefaultAsync(ct);
        if (user is null || user.DeletionRequestedAt is not null)
            return (null, "user_gone");

        // The wake-time check is the authority for session revocation: the Go API invalidates sessions (password reset or
        // change, deletion request) by stamping tokens_invalidated_at and cannot touch this table. A session that began
        // before the invalidation, or an account that must change its password, no longer authorizes an unattended turn.
        if (user.MustChangePassword || (user.TokensInvalidatedAt is { } revoked && revoked > watch.CreatedAt))
            return (null, "session_revoked");

        OrganizationRole? role = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.OrganizationId == watch.OrganizationId && m.UserId == watch.UserId)
            .Select(m => (OrganizationRole?)m.Role)
            .FirstOrDefaultAsync(ct);
        bool orgLive = await db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(o => o.Id == watch.OrganizationId && o.DeletedAt == null, ct);
        if (role is null || !orgLive)
            return (null, "not_a_member");

        bool projectOk = await db.Projects.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(p => p.Id == watch.ProjectId && p.OrganizationId == watch.OrganizationId, ct);
        if (!projectOk)
            return (null, "project_gone");

        return (new WakeIdentity(watch.UserId, user.Email, watch.OrganizationId, role.Value, user.IsAdmin, user.EmailVerifiedAt is not null), null);
    }

    private async Task<bool> CapReachedAsync(AssistantRunWatch watch, DateTime now, CancellationToken ct)
    {
        DateTime since = now.AddHours(-24);
        int firedToday = await db.AssistantRunWatches.CountAsync(
            w => w.ThreadId == watch.ThreadId && w.Status == AssistantRunWatchStatus.Fired && w.FiredAt >= since, ct);
        if (firedToday >= AssistantRunWakeLimits.MaxWakesPerThreadPerDay(configuration))
            return true;

        // Wake turns since the last message a person typed; a human message resets the count. The one row skipped is the
        // thread's LAST message when it is itself an unanswered wake row (a retry of this very wake), never a human message.
        int lastSequence = await db.AssistantMessages.Where(m => m.ThreadId == watch.ThreadId)
            .Select(m => (int?)m.Sequence).MaxAsync(ct) ?? 0;
        var recentUser = (await db.AssistantMessages.AsNoTracking()
                .Where(m => m.ThreadId == watch.ThreadId && m.Role == "user")
                .OrderByDescending(m => m.Sequence)
                .Select(m => new { m.Sequence, m.MetadataJson })
                .Take(AssistantRunWakeLimits.MaxConsecutiveWakes(configuration) + 2)
                .ToListAsync(ct))
            .Where(m => !(m.Sequence == lastSequence && IsWake(m.MetadataJson)))
            .Select(m => m.MetadataJson)
            .Take(AssistantRunWakeLimits.MaxConsecutiveWakes(configuration) + 1)
            .ToList();
        int consecutive = 0;
        foreach (string? meta in recentUser)
        {
            if (!IsWake(meta))
                break;
            consecutive++;
        }

        return consecutive >= AssistantRunWakeLimits.MaxConsecutiveWakes(configuration);
    }

    internal static bool IsWake(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
            return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(metadataJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("kind", out JsonElement kind)
                && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "run_wake";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A short, non-reversible tag for logs (no ids, no PII).</summary>
    private static string Short(Guid id) => id.ToString("N")[..8];
}
