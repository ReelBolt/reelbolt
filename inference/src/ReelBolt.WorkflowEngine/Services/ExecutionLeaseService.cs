using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;

namespace ReelBolt.WorkflowEngine.Services;

/// <summary>
/// Tunables of the execution lease and its recovery sweeper (config section
/// <c>WorkflowEngine:Lease</c>). Defaults: 60 s lease renewed every 15 s, a sweep every 30 s, and a
/// <c>Queued</c> row is considered stranded after 10 minutes.
/// </summary>
public sealed record ExecutionLeaseOptions(
    TimeSpan LeaseDuration,
    TimeSpan RenewInterval,
    TimeSpan SweepInterval,
    TimeSpan StaleQueuedAfter,
    int SweepBatchSize)
{
    public static ExecutionLeaseOptions From(IConfiguration configuration)
    {
        IConfigurationSection s = configuration.GetSection("WorkflowEngine:Lease");
        return new ExecutionLeaseOptions(
            TimeSpan.FromSeconds(Math.Max(5, s.GetValue("LeaseSeconds", 60))),
            TimeSpan.FromSeconds(Math.Max(1, s.GetValue("RenewIntervalSeconds", 15))),
            TimeSpan.FromSeconds(Math.Max(1, s.GetValue("SweepIntervalSeconds", 30))),
            TimeSpan.FromMinutes(Math.Max(1, s.GetValue("StaleQueuedMinutes", 10))),
            Math.Clamp(s.GetValue("SweepBatchSize", 25), 1, 500));
    }
}

/// <summary>
/// Liveness for the executions this process is still executing, whatever their row says (a stop
/// handled on another replica writes <c>Cancelled</c>, but the run goes on here). Every execution
/// this process runs holds a lease on its
/// <c>workflow_executions</c> row (<see cref="WorkflowExecution.LeaseOwner"/>, <c>LeaseExpiresAt</c>,
/// <c>HeartbeatAt</c>); ONE timer per process renews all of them every 15 s in a single
/// <c>UPDATE … WHERE id = ANY(…)</c>, so the cost is one statement per interval regardless of how
/// many runs are in flight. A row whose lease lapses belongs to an engine instance that is gone, and
/// <see cref="ExecutionRecoverySweeper"/> ends it.
///
/// <para>This is the other half of the "Workflow Execution Liveness" design: the runner acks the
/// delivery immediately and runs on a background task, so a hard process kill leaves the row
/// <c>Running</c> with no message to redeliver. The lease is what turns that into a bounded outage
/// (about 90 s) instead of a row stuck forever, and what lets the Inference API restart without
/// cancelling healthy runs (decision D21).</para>
/// </summary>
public sealed class ExecutionLeaseService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExecutionLeaseService> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, byte> _tracked = new();
    private readonly ConcurrentDictionary<Guid, byte> _lost = new();
    private readonly ExecutionCancellationRegistry? _cancellations;

    /// <summary>
    /// SQFU-S1: the reason a run is cancelled with when this instance finds it no longer owns the run's lease (the
    /// sweeper recovered it after a stall, and it may already run on another instance). Platform-class: never the
    /// user's stop, so the step's hold is refunded.
    /// </summary>
    public const string LeaseLostReason =
        "Interrupted: this engine instance lost the run's lease (it stalled past the lease window and the platform "
        + "handed the run to another instance), so it stopped working on it. This was a platform interruption, not a cancellation.";

    public ExecutionLeaseService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ExecutionLeaseService> logger,
        TimeProvider? time = null,
        ExecutionCancellationRegistry? cancellations = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _cancellations = cancellations;
        Options = ExecutionLeaseOptions.From(configuration);
        InstanceId = ResolveInstanceId(configuration);
    }

    /// <summary>This engine instance: the pod name in a cluster, the container hostname under compose.</summary>
    public string InstanceId { get; }

    public ExecutionLeaseOptions Options { get; }

    /// <summary>Execution ids this process currently holds a lease on.</summary>
    public IReadOnlyCollection<Guid> TrackedIds => _tracked.Keys.ToArray();

    internal static string ResolveInstanceId(IConfiguration configuration)
    {
        string? id = configuration["WorkflowEngine:InstanceId"];
        if (string.IsNullOrWhiteSpace(id)) id = Environment.GetEnvironmentVariable("POD_NAME");
        if (string.IsNullOrWhiteSpace(id)) id = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrWhiteSpace(id)) id = Environment.MachineName;
        return id.Length <= 200 ? id : id[..200];
    }

    /// <summary>A fresh lease for this instance: what a claim writes to the row.</summary>
    public LeaseStamp NewStamp()
    {
        DateTime now = _time.GetUtcNow().UtcDateTime;
        return new LeaseStamp(InstanceId, now, now + Options.LeaseDuration);
    }

    /// <summary>
    /// Sets the lease fields on the row being moved to <c>Running</c>, so the lease is written by the
    /// same <c>SaveChanges</c> that publishes the status, and starts renewing it.
    /// </summary>
    public void StampLease(WorkflowExecution execution) => Adopt(execution, NewStamp());

    /// <summary>
    /// Records <paramref name="stamp"/> (already written to the row by a conditional claim) on the tracked entity and
    /// starts renewing it.
    /// </summary>
    public void Adopt(WorkflowExecution execution, LeaseStamp stamp)
    {
        execution.LeaseOwner = stamp.Owner;
        execution.HeartbeatAt = stamp.HeartbeatAt;
        execution.LeaseExpiresAt = stamp.ExpiresAt;
        Track(execution.Id);
    }

    /// <summary>Starts renewing the lease of an execution this process is running.</summary>
    public void Track(Guid executionId)
    {
        _lost.TryRemove(executionId, out _);
        _tracked[executionId] = 0;
    }

    /// <summary>True once this instance found it no longer owns <paramref name="executionId"/>'s lease (SQFU-S1).</summary>
    public bool IsLost(Guid executionId) => _lost.ContainsKey(executionId);

    /// <summary>
    /// SQFU-S1's ownership check, run before a step is dispatched, before credits are reserved or settled, before a
    /// paid submit and before a terminal write. True when the row still names this instance as its lease owner; the
    /// lease is then pushed out a full window, so no sweep can claim the row right behind the check. False when the
    /// sweeper recovered the run (it clears the owner) or another instance claimed it: the run is marked lost, no
    /// longer renewed, and its token cancelled with <see cref="LeaseLostReason"/>. An expired lease nobody claimed is
    /// still this instance's: only a claim hands a run over. A run this instance never leased passes.
    /// </summary>
    public async Task<bool> TryHoldAsync(Guid executionId, CancellationToken ct)
    {
        if (_lost.ContainsKey(executionId))
        {
            return false;
        }

        if (!_tracked.ContainsKey(executionId))
        {
            return true;
        }

        DateTime now = _time.GetUtcNow().UtcDateTime;
        DateTime expires = now + Options.LeaseDuration;
        string owner = InstanceId;
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        int held;
        if (db.Database.IsRelational())
        {
            held = await db.WorkflowExecutions
                .Where(e => e.Id == executionId && e.LeaseOwner == owner)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.LeaseExpiresAt, expires).SetProperty(e => e.HeartbeatAt, now), ct);
        }
        else
        {
            // InMemory harnesses cannot run ExecuteUpdate; the same check, read then written.
            WorkflowExecution? row = await db.WorkflowExecutions.FirstOrDefaultAsync(e => e.Id == executionId && e.LeaseOwner == owner, ct);
            if (row is not null)
            {
                row.LeaseExpiresAt = expires;
                row.HeartbeatAt = now;
                await db.SaveChangesAsync(ct);
            }

            held = row is null ? 0 : 1;
        }

        if (held > 0)
        {
            return true;
        }

        MarkLost(executionId);
        return false;
    }

    /// <summary>
    /// <see cref="TryHoldAsync"/>, throwing <see cref="OperationCanceledException"/> (message
    /// <see cref="LeaseLostReason"/>) when the lease is lost, so the run unwinds as a platform interruption.
    /// </summary>
    public async Task ThrowIfLeaseLostAsync(Guid executionId, CancellationToken ct)
    {
        if (!await TryHoldAsync(executionId, ct))
        {
            throw new OperationCanceledException(LeaseLostReason, ct);
        }
    }

    private void MarkLost(Guid executionId)
    {
        if (!_lost.TryAdd(executionId, 0))
        {
            return;
        }

        _tracked.TryRemove(executionId, out _);
        _logger.LogWarning(
            "Instance {InstanceId} no longer owns the lease of execution {ExecutionId} (recovered by the sweeper or claimed by another instance); stopping it here",
            InstanceId, executionId);
        if (_cancellations is not null && _cancellations.Contains(executionId))
        {
            _cancellations.TryCancel(executionId, LeaseLostReason);
        }
    }

    /// <summary>Stops renewing. Called when the execution ends, on any outcome.</summary>
    public void Release(Guid executionId) => _tracked.TryRemove(executionId, out _);

    /// <summary>Ends this instance's live lease of an execution whose run has exited, so a retry need not wait out the lease. Returns rows changed.</summary>
    public async Task<int> EndLeaseAsync(Guid executionId, CancellationToken ct)
    {
        _lost.TryRemove(executionId, out _);
        DateTime now = _time.GetUtcNow().UtcDateTime;
        string owner = InstanceId;
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        return await db.WorkflowExecutions
            .Where(e => e.Id == executionId && e.LeaseOwner == owner && e.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LeaseExpiresAt, (DateTime?)now), ct);
    }

    /// <summary>
    /// Renews the lease of every execution this process is still executing, in one statement, until
    /// <see cref="Release"/> is called (the runner's finally, on any outcome). That includes an execution
    /// a user stopped through another replica: its row is already <c>Cancelled</c>, but this process is
    /// still running its step, and the unsettled-hold sweep trusts this lease to know that. Renewal never
    /// writes <c>status</c>, and only rows this instance owns are touched. A tracked row that no longer names this
    /// instance (SQFU-S1: recovered by the sweeper, or claimed by another instance) is marked lost and its run cancelled with
    /// <see cref="LeaseLostReason"/>. Returns the number of rows renewed.
    /// </summary>
    public async Task<int> RenewAsync(CancellationToken ct)
    {
        Guid[] ids = _tracked.Keys.ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        DateTime now = _time.GetUtcNow().UtcDateTime;
        DateTime expires = now + Options.LeaseDuration;
        string owner = InstanceId;

        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        int renewed = await db.WorkflowExecutions
            .Where(e => ids.Contains(e.Id) && e.LeaseOwner == owner)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.LeaseExpiresAt, expires).SetProperty(e => e.HeartbeatAt, now),
                ct);

        // SQFU-S1: a tracked run whose row no longer names this instance was recovered by the sweeper (which clears
        // the owner) or claimed by another instance. Stop it here; it must never write over the new owner's run.
        if (renewed < ids.Length)
        {
            HashSet<Guid> owned = (await db.WorkflowExecutions.AsNoTracking()
                .Where(e => ids.Contains(e.Id) && e.LeaseOwner == owner)
                .Select(e => e.Id)
                .ToListAsync(ct)).ToHashSet();
            foreach (Guid id in ids)
            {
                if (!owned.Contains(id) && _tracked.ContainsKey(id))
                {
                    MarkLost(id);
                }
            }
        }

        return renewed;
    }

    /// <summary>
    /// Delivers a Stop that another replica consumed to the run it stops. The Stop message is taken by
    /// whichever replica receives it, and that replica can only write <c>Cancelled</c> to the row: the
    /// token lives in this process. Without this, the owning replica runs the step on and its final save
    /// can write <c>Passed</c> over the user's <c>Cancelled</c>. After each renewal, every tracked row this
    /// instance owns that is <c>Cancelled</c> has its run's token signalled, with the reason the row records
    /// (the <c>Stopped by user</c> wording a local stop records too, so the step is classified
    /// <c>UserCancelled</c>). Returns how many runs were signalled here.
    /// </summary>
    public async Task<int> PropagateStopsAsync(CancellationToken ct)
    {
        if (_cancellations is null || _tracked.IsEmpty)
        {
            return 0;
        }

        Guid[] ids = _tracked.Keys.ToArray();
        string owner = InstanceId;

        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        var stopped = await db.WorkflowExecutions
            .Where(e => ids.Contains(e.Id) && e.LeaseOwner == owner && e.Status == ExecutionStatus.Cancelled)
            .Select(e => new { e.Id, e.ErrorMessage })
            .ToListAsync(ct);

        int signalled = 0;
        foreach (var row in stopped)
        {
            _logger.LogInformation("Execution {ExecutionId} was stopped through another engine instance; cancelling it here", row.Id);
            if (_cancellations.TryCancel(row.Id, row.ErrorMessage ?? "Stopped by user"))
            {
                signalled++;
            }
        }

        return signalled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Options.RenewInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RenewAsync(stoppingToken);
                    await PropagateStopsAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A missed renewal is survivable (the lease is four intervals long); the next tick retries.
                    _logger.LogWarning(ex, "Execution lease renewal failed for instance {InstanceId}", InstanceId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}

/// <summary>The lease a claim writes: this instance, the heartbeat and the expiry.</summary>
public sealed record LeaseStamp(string Owner, DateTime HeartbeatAt, DateTime ExpiresAt);
