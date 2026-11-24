using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>Startup binding for <c>Assistant:ExecutionGuardLockWaitMilliseconds</c> (validated on start).</summary>
public sealed class AssistantExecutionGuardOptions
{
    public const string SectionName = "Assistant";

    public int ExecutionGuardLockWaitMilliseconds { get; set; } = 5000;
}

/// <summary>
/// The two-bucket run limit: per user per project, a rolling hour, counted from the
/// <c>run_rate_events</c> ledger. Paid runs (they hold model or vendor credits, and revisions) default to
/// 10 an hour; free deterministic runs to 60. Chat, MCP and the assistant REST path share it.
/// </summary>
public class AssistantExecutionGuard : IAssistantExecutionGuard
{
    /// <summary>Legacy single cap; honoured as the paid limit when <see cref="MaxPaidExecutionsPerHourKey"/> is not set.</summary>
    internal const string MaxExecutionsPerHourKey = "Assistant:MaxExecutionsPerHour";
    internal const string MaxPaidExecutionsPerHourKey = "Assistant:MaxPaidExecutionsPerHour";
    internal const string MaxFreeExecutionsPerHourKey = "Assistant:MaxFreeExecutionsPerHour";
    internal const int DefaultPaidPerHour = 10;
    internal const int DefaultFreePerHour = 60;
    internal const int WindowSeconds = 3600;
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(24);
    internal const string LockWaitMillisecondsKey = "Assistant:ExecutionGuardLockWaitMilliseconds";
    internal const int DefaultLockWaitMilliseconds = 5000;
    internal const int MaxLockWaitMilliseconds = 60_000;
    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(50);

    // One replica's process-local semaphores. They run on every provider: off Postgres they are the
    // only serialisation; on Postgres they keep one replica from sending more than one waiter per key
    // to the database.
    private static readonly AssistantExecutionGuardProcessLocks SharedProcessLocks = new();

    private readonly InferenceApiDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly AssistantExecutionGuardProcessLocks _processLocks;

    public AssistantExecutionGuard(InferenceApiDbContext context, IConfiguration configuration, ILogger<AssistantExecutionGuard>? logger = null)
        : this(context, configuration, logger, SharedProcessLocks)
    {
    }

    /// <summary>A guard with its own process-local semaphores, i.e. a separate replica (tests).</summary>
    internal AssistantExecutionGuard(InferenceApiDbContext context, IConfiguration configuration, ILogger? logger, AssistantExecutionGuardProcessLocks processLocks)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger ?? NullLogger.Instance;
        _processLocks = processLocks;
    }

    /// <summary>
    /// Counts the caller's runs of <paramref name="bucket"/> in the rolling hour and, below the limit, runs
    /// <paramref name="insertCallback"/> while still holding the (user, project) lock, then records the run.
    /// </summary>
    /// <remarks>
    /// <para><b>Ledger.</b> <c>run_rate_events</c>, one row per run the callback started (a callback that returns
    /// <c>null</c> started nothing and records nothing). The row is written BEFORE the callback runs (S-11) and removed
    /// again when the callback starts nothing or fails other than by cancellation. Rows older than a day are pruned
    /// with the insert.</para>
    /// <para><b>Postgres.</b> A SESSION advisory lock on the context's own opened connection
    /// (<c>pg_try_advisory_lock</c> polled every 50 ms up to <c>Assistant:ExecutionGuardLockWaitMilliseconds</c>,
    /// default 5000; on timeout the call is refused as <see cref="RunRateDecision.Busy"/>, fail closed). It holds across
    /// the count, the callback and the event insert, so it serialises every replica (S-8). No transaction is opened:
    /// the callback's SaveChanges autocommits before it publishes, so nothing is announced for a row that could still
    /// roll back (S-10). The lock is released in <c>finally</c>; if that fails the pool is cleared and the
    /// session ended, so it (and its lock) is closed instead of reused.</para>
    /// <para><b>Process lock</b> (every provider, first). This replica's (user, project) semaphore, waited for at most
    /// <c>Assistant:ExecutionGuardLockWaitMilliseconds</c> (QA-8); past it the call is refused as busy, fail closed.</para>
    /// <para><b>Other providers</b> (SQLite, in-memory: tests). The process semaphore only, no transaction.</para>
    /// </remarks>
    public Task<RunRateDecision> TryAcquireAsync(
        Guid userId, Guid projectId, RunRateBucket bucket, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(insertCallback);
        return AcquireAsync(userId, projectId, () => Task.FromResult(bucket), bucket, stampAdmission: false, source, insertCallback, ct);
    }

    /// <inheritdoc />
    public Task<RunRateDecision> TryAcquireAsync(
        Guid userId, Guid projectId, Func<Task<RunRateBucket>> classify, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(classify);
        ArgumentNullException.ThrowIfNull(insertCallback);
        return AcquireAsync(userId, projectId, classify, RunRateBucket.Paid, stampAdmission: true, source, insertCallback, ct);
    }

    /// <param name="classify">Runs under the lock; its answer is the bucket counted and recorded.</param>
    /// <param name="busyBucket">The bucket a busy refusal reports, when the lock was not taken and nothing was classified.</param>
    /// <param name="stampAdmission">Expose the classified bucket to the callback through <see cref="RunRateAdmission"/>.</param>
    private async Task<RunRateDecision> AcquireAsync(
        Guid userId, Guid projectId, Func<Task<RunRateBucket>> classify, RunRateBucket busyBucket, bool stampAdmission,
        string source, Func<Task<Guid?>> insertCallback, CancellationToken ct)
    {
        int busyLimit = ReadLimit(busyBucket);
        var lockWaitMilliseconds = ReadLockWaitMilliseconds();
        var isNpgsql = _context.Database.IsNpgsql();

        var semaphore = _processLocks.Get(ComputeLockKeyString(userId, projectId));

        // QA-8: the in-process wait is bounded by the same lock wait as the database lock. Past it, fail closed.
        if (!await semaphore.WaitAsync(TimeSpan.FromMilliseconds(lockWaitMilliseconds), ct))
        {
            _logger.LogWarning(
                "Run limit process lock not acquired within {WaitMs} ms (UserId={UserId}, ProjectId={ProjectId}); refusing the run",
                lockWaitMilliseconds, userId, projectId);
            return await BusyDecisionAsync(userId, projectId, busyBucket, busyLimit, ct);
        }

        try
        {
            if (!isNpgsql)
            {
                return await ClassifyCountThenInsertAsync(userId, projectId, classify, stampAdmission, source, insertCallback, ct);
            }

            return await UnderSessionLockAsync(
                userId, projectId, classify, busyBucket, busyLimit, stampAdmission, source, lockWaitMilliseconds, insertCallback, ct);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// QA-P0-14: a busy refusal counted nothing, so it reports the bucket's real count in the window (read without the
    /// lock: informational, it decides nothing) and the real limit, never a fabricated "limit of limit".
    /// QA-P0-30: the read is informational, so a database failure while taking it must answer this refusal, never a 500.
    /// </summary>
    private async Task<RunRateDecision> BusyDecisionAsync(Guid userId, Guid projectId, RunRateBucket bucket, int limit, CancellationToken ct)
    {
        int used = await TryCountInWindowAsync(userId, projectId, bucket, ct);
        return new RunRateDecision(false, bucket, used, limit, WindowSeconds, 5, Busy: true);
    }

    /// <summary>
    /// QA-P0-30: the busy refusal's count. Best effort by design — it decides nothing, so a database failure
    /// (a lock timeout, a broken connection) leaves the count at zero and the busy refusal stands. The busy path
    /// is exactly where the database is contended, which is why this must not turn the refusal into a 500.
    /// Cancellation is not swallowed: it is the request's, not the count's.
    /// </summary>
    private async Task<int> TryCountInWindowAsync(Guid userId, Guid projectId, RunRateBucket bucket, CancellationToken ct)
    {
        try
        {
            return await InWindow(userId, projectId, BucketName(bucket), DateTime.UtcNow.AddSeconds(-WindowSeconds)).CountAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Busy run refusal could not count the window (UserId={UserId}, ProjectId={ProjectId}); refusing as busy",
                userId, projectId);
            return 0;
        }
    }

    private static string BucketName(RunRateBucket bucket) => bucket == RunRateBucket.Free ? RunRateEvent.FreeBucket : RunRateEvent.PaidBucket;

    private IQueryable<RunRateEvent> InWindow(Guid userId, Guid projectId, string bucketName, DateTime windowStart) =>
        _context.RunRateEvents.AsNoTracking()
            .Where(e => e.UserId == userId && e.ProjectId == projectId && e.Bucket == bucketName && e.CreatedAt >= windowStart);

    private int ReadLimit(RunRateBucket bucket)
    {
        if (bucket == RunRateBucket.Free)
        {
            return _configuration.GetValue(MaxFreeExecutionsPerHourKey, DefaultFreePerHour);
        }

        // The legacy single cap is the paid limit when it is the only one set.
        return _configuration.GetValue<int?>(MaxPaidExecutionsPerHourKey)
            ?? _configuration.GetValue<int?>(MaxExecutionsPerHourKey)
            ?? DefaultPaidPerHour;
    }

    private async Task<RunRateDecision> UnderSessionLockAsync(
        Guid userId, Guid projectId, Func<Task<RunRateBucket>> classify, RunRateBucket busyBucket, int busyLimit, bool stampAdmission,
        string source, int lockWaitMilliseconds, Func<Task<Guid?>> insertCallback, CancellationToken ct)
    {
        var lockKey = ComputeLockKey(userId, projectId);

        // The session lock lives on this physical connection, so it must stay open (and be the one the
        // callback's SaveChanges uses) from the lock to the unlock.
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            if (!await TryTakeSessionLockAsync(lockKey, lockWaitMilliseconds, ct))
            {
                _logger.LogWarning(
                    "Assistant run cap lock not acquired within {WaitMs} ms (UserId={UserId}, ProjectId={ProjectId}); refusing the run",
                    lockWaitMilliseconds, userId, projectId);
                return await BusyDecisionAsync(userId, projectId, busyBucket, busyLimit, ct);
            }

            try
            {
                return await ClassifyCountThenInsertAsync(userId, projectId, classify, stampAdmission, source, insertCallback, ct);
            }
            finally
            {
                await ReleaseSessionLockAsync(lockKey, userId, projectId);
            }
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    private async Task<bool> TryTakeSessionLockAsync(long lockKey, int lockWaitMilliseconds, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            bool acquired;
            try
            {
                acquired = await _context.Database
                    .SqlQuery<bool>($"SELECT pg_try_advisory_lock({lockKey}) AS \"Value\"")
                    .SingleAsync(ct);
            }
            catch
            {
                // Whether the server took the lock is unknown: never hand this session back to the pool.
                await DiscardPooledSessionAsync();
                throw;
            }

            if (acquired)
            {
                return true;
            }

            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= lockWaitMilliseconds)
            {
                return false;
            }

            await Task.Delay(LockPollInterval, ct);
        }
    }

    private async Task ReleaseSessionLockAsync(long lockKey, Guid userId, Guid projectId)
    {
        try
        {
            // Not the request token: a cancelled request must still release its lock.
            var released = await _context.Database
                .SqlQuery<bool>($"SELECT pg_advisory_unlock({lockKey}) AS \"Value\"")
                .SingleAsync(CancellationToken.None);
            if (released)
            {
                return;
            }

            _logger.LogError(
                "Assistant run cap lock was not held at release (UserId={UserId}, ProjectId={ProjectId}); clearing the connection pool",
                userId, projectId);
        }
        catch (Exception ex)
        {
            // The run (if any) is already committed and published; the result stands. Only the
            // session must not go back to the pool still holding the lock.
            _logger.LogError(ex,
                "Assistant run cap lock release failed (UserId={UserId}, ProjectId={ProjectId}); clearing the connection pool",
                userId, projectId);
        }

        await DiscardPooledSessionAsync();
    }

    /// <summary>
    /// Makes sure the context's session, which may still hold the lock, is never reused: clears its
    /// pool (closes it on return when the connection came from the global pool) and ends the backend,
    /// because <see cref="NpgsqlConnection.ClearPool"/> does not reach a connection EF opened from its
    /// own <see cref="NpgsqlDataSource"/> (the production configuration). Ending the backend releases
    /// every session lock it holds; Npgsql then discards the broken connection instead of pooling it.
    /// </summary>
    private async Task DiscardPooledSessionAsync()
    {
        if (_context.Database.GetDbConnection() is NpgsqlConnection connection)
        {
            NpgsqlConnection.ClearPool(connection);
        }

        try
        {
            await _context.Database
                .SqlQuery<bool>($"SELECT pg_terminate_backend(pg_backend_pid()) AS \"Value\"")
                .SingleAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Expected: the server ends this session mid-statement (57P01), or it was already gone.
            _logger.LogDebug(ex, "Assistant run cap session ended after a lock failure");
        }
    }

    /// <summary>Under the lock: classify, then count and start in that bucket (T-012: one bucket, read once, under the lock).</summary>
    private async Task<RunRateDecision> ClassifyCountThenInsertAsync(
        Guid userId, Guid projectId, Func<Task<RunRateBucket>> classify, bool stampAdmission, string source,
        Func<Task<Guid?>> insertCallback, CancellationToken ct)
    {
        RunRateBucket bucket = await classify();
        int limit = ReadLimit(bucket);
        if (!stampAdmission)
        {
            return await CountThenInsertAsync(userId, projectId, bucket, source, limit, insertCallback, ct);
        }

        return await CountThenInsertAsync(userId, projectId, bucket, source, limit, async () =>
        {
            using IDisposable admitted = RunRateAdmission.Enter(bucket);
            return await insertCallback();
        }, ct);
    }

    private async Task<RunRateDecision> CountThenInsertAsync(
        Guid userId, Guid projectId, RunRateBucket bucket, string source, int limit, Func<Task<Guid?>> insertCallback, CancellationToken ct)
    {
        string bucketName = BucketName(bucket);
        var now = DateTime.UtcNow;
        var windowStart = now.AddSeconds(-WindowSeconds);

        // Chat, MCP and the assistant REST path all count here, on purpose: the limit is about "this user
        // asked for a run in this project", and switching surface must not be a way around it.
        IQueryable<RunRateEvent> inWindow = InWindow(userId, projectId, bucketName, windowStart);
        int used = await inWindow.CountAsync(ct);

        if (used >= limit)
        {
            DateTime? oldest = await inWindow.OrderBy(e => e.CreatedAt).Select(e => (DateTime?)e.CreatedAt).FirstOrDefaultAsync(ct);
            double wait = oldest is { } o ? (o.AddSeconds(WindowSeconds) - now).TotalSeconds : WindowSeconds;
            return new RunRateDecision(false, bucket, used, limit, WindowSeconds, Math.Max(1, (int)Math.Ceiling(wait)));
        }

        // S-11: the event is committed BEFORE the callback queues and publishes the run, still under the lock, so a
        // request aborted after the publish cannot leave a started run uncounted. If this write fails nothing starts.
        var rateEvent = new RunRateEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProjectId = projectId,
            Bucket = bucketName,
            Source = source,
            CreatedAt = DateTime.UtcNow,
        };
        _context.RunRateEvents.Add(rateEvent);
        DateTime retention = now - RetentionWindow;
        _context.RunRateEvents.RemoveRange(await _context.RunRateEvents
            .Where(e => e.UserId == userId && e.ProjectId == projectId && e.CreatedAt < retention)
            .ToListAsync(ct));
        await _context.SaveChangesAsync(ct);

        Guid? executionId;
        RunRateAdmission.CommittedRun committed = new();
        try
        {
            using (RunRateAdmission.TrackCommit(committed))
            {
                executionId = await insertCallback();
            }
        }
        catch (OperationCanceledException)
        {
            // QA-P0-30: the request was aborted. If the Queued row never committed (nothing was reported
            // through RunRateAdmission), no run will ever start, so the user's own cancel must not consume
            // their rate limit — release the event. If it did commit, the recovery sweeper re-publishes the
            // run even though this request was aborted, so the event stays counted and linked (S-11).
            if (committed.ExecutionId is { } abortedRun)
                await LinkEventAsync(rateEvent, abortedRun, userId, projectId);
            else
                await DeleteEventAsync(rateEvent, userId, projectId);
            throw;
        }
        catch when (committed.ExecutionId is { } queuedRun)
        {
            // S-11: the Queued row is committed, so the recovery sweeper re-publishes it and the run WILL execute even
            // though this publish failed: it stays counted, linked to its row.
            await LinkEventAsync(rateEvent, queuedRun, userId, projectId);
            throw;
        }
        catch
        {
            // Failed before any execution row was committed: no run will ever start, so none is counted.
            await DeleteEventAsync(rateEvent, userId, projectId);
            throw;
        }

        if (executionId is null)
        {
            // The callback started nothing (a plan refusal): it consumes nothing.
            await DeleteEventAsync(rateEvent, userId, projectId);
            return new RunRateDecision(true, bucket, used, limit, WindowSeconds, 0);
        }

        try
        {
            // Not the request token: the run is started; only the link to it is recorded here.
            rateEvent.ExecutionId = executionId;
            await _context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The run is counted either way; only its execution id is missing from the event.
            _logger.LogError(ex, "Run limit event not linked to its run (UserId={UserId}, ProjectId={ProjectId}, ExecutionId={ExecutionId})",
                userId, projectId, executionId);
        }

        return new RunRateDecision(true, bucket, used + 1, limit, WindowSeconds, 0);
    }

    /// <summary>
    /// Links a kept event to the committed run after the callback failed. A relational store updates the row by id
    /// without saving whatever else the failed callback left in the change tracker. Best effort, never the request
    /// token: the event is counted whether or not the link lands.
    /// </summary>
    private async Task LinkEventAsync(RunRateEvent rateEvent, Guid executionId, Guid userId, Guid projectId)
    {
        try
        {
            if (_context.Database.IsRelational())
            {
                Guid id = rateEvent.Id;
                await _context.RunRateEvents.Where(e => e.Id == id)
                    .ExecuteUpdateAsync(u => u.SetProperty(e => e.ExecutionId, executionId), CancellationToken.None);
                rateEvent.ExecutionId = executionId;
                _context.Entry(rateEvent).State = EntityState.Unchanged;
                return;
            }

            rateEvent.ExecutionId = executionId;
            await _context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run limit event not linked to its run (UserId={UserId}, ProjectId={ProjectId}, ExecutionId={ExecutionId})",
                userId, projectId, executionId);
        }
    }

    /// <summary>
    /// Removes an event whose run did not start. A relational store deletes the row by id without touching the change
    /// tracker (the failed callback may have left its own unsaved entities there); the in-memory store has no
    /// ExecuteDelete. Never the request token, and best effort: a leftover row over-counts, it never under-counts.
    /// </summary>
    private async Task DeleteEventAsync(RunRateEvent rateEvent, Guid userId, Guid projectId)
    {
        try
        {
            if (_context.Database.IsRelational())
            {
                Guid id = rateEvent.Id;
                await _context.RunRateEvents.Where(e => e.Id == id).ExecuteDeleteAsync(CancellationToken.None);
                _context.Entry(rateEvent).State = EntityState.Detached;
                return;
            }

            _context.RunRateEvents.Remove(rateEvent);
            await _context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run limit event of a run that did not start was not removed (UserId={UserId}, ProjectId={ProjectId})",
                userId, projectId);
        }
    }

    private int ReadLockWaitMilliseconds()
    {
        var raw = _configuration[LockWaitMillisecondsKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultLockWaitMilliseconds;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 0 || value > MaxLockWaitMilliseconds)
        {
            throw new InvalidOperationException(
                $"{LockWaitMillisecondsKey} must be an integer from 0 to {MaxLockWaitMilliseconds}.");
        }

        return value;
    }

    /// <summary>
    /// Computes a stable string key of the userId and projectId pair for use with the in-memory semaphore cache.
    /// </summary>
    private static string ComputeLockKeyString(Guid userId, Guid projectId)
    {
        return $"{userId:N}:{projectId:N}";
    }

    /// <summary>
    /// Computes a stable 64-bit hash of the userId and projectId pair, the PostgreSQL advisory lock key.
    /// </summary>
    internal static long ComputeLockKey(Guid userId, Guid projectId)
    {
        var combined = userId.ToString("N") + projectId.ToString("N");
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return BitConverter.ToInt64(hashBytes, 0);
    }
}

/// <summary>The process-local semaphores of one replica, one per (user, project) key.</summary>
internal sealed class AssistantExecutionGuardProcessLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public SemaphoreSlim Get(string key) => _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
}

/// <summary>The words every surface uses for a refused run, so REST, the assistant and the MCP say the same thing.</summary>
public static class RunRateRefusal
{
    public const string Code = "RUN_RATE_LIMITED";

    public static string BucketName(RunRateBucket bucket) => bucket == RunRateBucket.Free ? "free" : "paid";

    /// <summary>Plain sentence: <c>run limit reached — 10 of 10 paid runs in the last hour on this project; try again in 13 minutes</c>.</summary>
    public static string Sentence(RunRateDecision decision)
    {
        if (decision.Busy)
        {
            return "the run limiter is busy right now; try again in a few seconds";
        }

        return $"run limit reached — {decision.Used} of {decision.Limit} {BucketName(decision.Bucket)} runs in the last hour on this project; try again in {Wait(decision.RetryAfterSeconds)}";
    }

    /// <summary>A wait in whole minutes, rounded up, at least one: <c>1 minute</c>, <c>13 minutes</c>. Every hourly limit's sentence uses it.</summary>
    public static string Wait(int retryAfterSeconds)
    {
        int minutes = Math.Max(1, (int)Math.Ceiling(retryAfterSeconds / 60.0));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }
}
