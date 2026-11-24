using MassTransit;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Billing;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;

namespace ReelBolt.WorkflowEngine.Services;

/// <summary>
/// Runs on every engine replica and ends executions whose engine instance died, so a crashed or
/// killed engine never leaves a row <c>Running</c> forever and nothing needs a human to clean up.
///
/// <list type="bullet">
/// <item>A <c>Running</c> row whose lease has lapsed (and that this process is not itself running) is
/// marked <c>Failed</c> with a provenance message, its <c>Running</c> step results are marked
/// <c>Failed</c>, the failure event is published (the Go SSE hub relays it) and the platform-failure
/// hook runs. Claiming uses <c>FOR UPDATE SKIP LOCKED</c>, so two replicas sweeping at once split the
/// work and never double-process a row; rows with a live lease, including every other instance's, are
/// never touched.</item>
/// <item>in the same transaction, a claimed run that was never auto-resumed, has an engine-taken
/// snapshot and had no paid VideoGenerate work in flight is instead re-queued ONCE as a resume
/// (<c>Queued</c>, <c>pending_resume</c>, <c>auto_resume_count = 1</c>, <c>resume_count += 1</c>): its lost
/// step rows are still failed and its holds still released (before the request is published), but no
/// Failed event goes out. Every other claimed run is failed as above and waits for a manual Resume.</item>
/// <item>A <c>Queued</c> row older than the stale threshold that has no lease is re-published: its
/// original message was lost (queue purge, broker restart). The executor only starts a <c>Queued</c>
/// row, so a duplicate delivery is harmless.</item>
/// </list>
///
/// <para>The message deliberately says the PLATFORM interrupted the run. Never blame the user here
/// (see <c>ExecutionCancellationRegistry</c> and the "Cancellation carries provenance" rule).</para>
/// </summary>
public sealed class ExecutionRecoverySweeper : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExecutionLeaseService _leases;
    private readonly ILogger<ExecutionRecoverySweeper> _logger;
    private readonly TimeProvider _time;

    public ExecutionRecoverySweeper(
        IServiceScopeFactory scopeFactory,
        ExecutionLeaseService leases,
        ILogger<ExecutionRecoverySweeper> logger,
        TimeProvider? time = null)
    {
        _scopeFactory = scopeFactory;
        _leases = leases;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            using PeriodicTimer timer = new(_leases.Options.SweepInterval);
            do
            {
                try
                {
                    await SweepOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The table may not exist yet on a brand-new database, or the DB may be briefly away.
                    _logger.LogWarning(ex, "Execution recovery sweep failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>One pass. Returns how many lost executions were failed (auto-resumed ones are not counted) and how many stale queued requests were re-published.</summary>
    public async Task<(int Failed, int Republished)> SweepOnceAsync(CancellationToken ct)
    {
        int failed = await RecoverExpiredAsync(ct);
        int republished = await RepublishStaleQueuedAsync(ct);
        return (failed, republished);
    }

    private async Task<int> RecoverExpiredAsync(CancellationToken ct)
    {
        int total = 0;
        while (true)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

            DateTime now = _time.GetUtcNow().UtcDateTime;
            // A Running row with no lease predates leases: give it two lease windows from its start.
            DateTime legacyCutoff = now - (_leases.Options.LeaseDuration * 2);
            Guid[] mine = _leases.TrackedIds.ToArray();
            int batch = _leases.Options.SweepBatchSize;

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            List<WorkflowExecution> claimed;
            if (db.Database.IsNpgsql())
            {
                claimed = await db.WorkflowExecutions.FromSqlInterpolated($@"
                    SELECT * FROM workflow_executions
                    WHERE status = 'Running'
                      AND NOT (id = ANY({mine}))
                      AND ((lease_expires_at IS NOT NULL AND lease_expires_at < {now})
                        OR (lease_expires_at IS NULL AND (started_at IS NULL OR started_at < {legacyCutoff})))
                    ORDER BY started_at
                    LIMIT {batch}
                    FOR UPDATE SKIP LOCKED").ToListAsync(ct);
            }
            else
            {
                claimed = await db.WorkflowExecutions
                    .Where(e => e.Status == ExecutionStatus.Running
                        && !mine.Contains(e.Id)
                        && ((e.LeaseExpiresAt != null && e.LeaseExpiresAt < now)
                            || (e.LeaseExpiresAt == null && (e.StartedAt == null || e.StartedAt < legacyCutoff))))
                    .OrderBy(e => e.StartedAt)
                    .Take(batch)
                    .ToListAsync(ct);
            }

            if (claimed.Count == 0)
            {
                return total;
            }

            Guid[] ids = claimed.Select(e => e.Id).ToArray();
            List<WorkflowStepResult> running = await db.WorkflowStepResults
                .Where(r => ids.Contains(r.WorkflowExecutionId) && r.Status == StepStatus.Running)
                .ToListAsync(ct);

            HashSet<Guid> resumable = await FindAutoResumableAsync(db, claimed, ct);

            Dictionary<Guid, string> reasons = new();
            Dictionary<Guid, (string? Owner, DateTime? Expired)> lostLeases = new();
            foreach (WorkflowExecution execution in claimed)
            {
                string reason = BuildReason(execution);
                reasons[execution.Id] = reason;
                lostLeases[execution.Id] = (execution.LeaseOwner, execution.LeaseExpiresAt);
                if (resumable.Contains(execution.Id))
                {
                    // the one automatic resume. The intent lives on the row, so the stale-queue
                    // re-publish (or any redelivery) resumes rather than restarts; StartedAt is kept.
                    execution.Status = ExecutionStatus.Queued;
                    execution.PendingResume = true;
                    execution.AutoResumeCount = 1;
                    execution.ResumeCount += 1;
                    execution.LeaseOwner = null;
                    execution.LeaseExpiresAt = null;
                    execution.ErrorMessage = null;
                    execution.CompletedAt = null;
                    execution.QueuedAt = now;
                }
                else
                {
                    execution.Status = ExecutionStatus.Failed;
                    execution.ErrorMessage = reason;
                    execution.CompletedAt = now;
                    // SQFU-S1: the lost instance may only be stalled. Clearing the owner is what makes its next
                    // ownership check (ExecutionLeaseService.TryHoldAsync) fail, so it never runs on.
                    execution.LeaseOwner = null;
                }
            }

            foreach (WorkflowStepResult step in running)
            {
                step.Status = StepStatus.Failed;
                step.FailureCode = FailureMessages.RunInterruptedCode;
                step.ErrorDetails = reasons[step.WorkflowExecutionId];
                step.CompletedAt = now;
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            IWorkflowEventPublisher? publisher = scope.ServiceProvider.GetService<IWorkflowEventPublisher>();
            IPlatformFailureHook? hook = scope.ServiceProvider.GetService<IPlatformFailureHook>();
            IPublishEndpoint? publish = scope.ServiceProvider.GetService<IPublishEndpoint>();
            IOrganizationEntitlements? entitlements = scope.ServiceProvider.GetService<IOrganizationEntitlements>();
            foreach (WorkflowExecution execution in claimed)
            {
                if (resumable.Contains(execution.Id))
                {
                    _logger.LogInformation(
                        "Auto-resuming execution {ExecutionId} once: its engine instance was lost (lease owner {Owner}, expired {Expired:O}), "
                        + "it has an engine-taken snapshot and no paid video generation was in flight; re-queued as a resume",
                        execution.Id, lostLeases[execution.Id].Owner, lostLeases[execution.Id].Expired);

                    // Release the lost dispatch's holds BEFORE the resume can start and reserve its own.
                    try
                    {
                        if (hook != null)
                        {
                            await hook.OnPlatformFailureAsync(execution, reasons[execution.Id], ct);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Releasing the holds of auto-resumed execution {ExecutionId} failed", execution.Id);
                    }

                    try
                    {
                        if (publish != null)
                        {
                            await PublishRequestAsync(publish, entitlements, execution, now, ct);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The row is Queued with pending_resume committed: the stale-queue re-publish retries it.
                        _logger.LogError(ex, "Publishing the auto-resume of execution {ExecutionId} failed", execution.Id);
                    }

                    continue;
                }

                _logger.LogWarning(
                    "Recovered orphaned execution {ExecutionId} (lease owner {Owner}, expired {Expired:O}); marked Failed",
                    execution.Id, lostLeases[execution.Id].Owner, lostLeases[execution.Id].Expired);
                try
                {
                    if (publisher != null)
                    {
                        await publisher.PublishExecutionFailedAsync(execution, ct);
                    }

                    if (hook != null)
                    {
                        await hook.OnPlatformFailureAsync(execution, reasons[execution.Id], ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The row is already Failed and committed; a lost event is only a stale UI until refresh.
                    _logger.LogError(ex, "Publishing the recovery failure for execution {ExecutionId} failed", execution.Id);
                }
            }

            total += claimed.Count - resumable.Count;
            if (claimed.Count < batch)
            {
                return total;
            }
        }
    }

    private async Task<int> RepublishStaleQueuedAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        DateTime now = _time.GetUtcNow().UtcDateTime;
        DateTime cutoff = now - _leases.Options.StaleQueuedAfter;
        int batch = _leases.Options.SweepBatchSize;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        List<WorkflowExecution> stale;
        if (db.Database.IsNpgsql())
        {
            stale = await db.WorkflowExecutions.FromSqlInterpolated($@"
                SELECT * FROM workflow_executions
                WHERE status = 'Queued' AND lease_owner IS NULL
                  AND queued_at IS NOT NULL AND queued_at < {cutoff}
                ORDER BY queued_at
                LIMIT {batch}
                FOR UPDATE SKIP LOCKED").ToListAsync(ct);
        }
        else
        {
            stale = await db.WorkflowExecutions
                .Where(e => e.Status == ExecutionStatus.Queued && e.LeaseOwner == null
                    && e.QueuedAt != null && e.QueuedAt < cutoff)
                .OrderBy(e => e.QueuedAt)
                .Take(batch)
                .ToListAsync(ct);
        }

        if (stale.Count == 0)
        {
            return 0;
        }

        // Bump first so a publish failure retries after another full window rather than every sweep.
        foreach (WorkflowExecution execution in stale)
        {
            execution.QueuedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        IPublishEndpoint? publish = scope.ServiceProvider.GetService<IPublishEndpoint>();
        if (publish == null)
        {
            return 0;
        }

        // B8c: a stranded run re-enters the SAME queue as the message it lost, so it is re-published
        // at its organization's plan priority. A null service (minimal test scopes) or an unreadable
        // plan degrades to standard, which is also the pre-B8c behaviour.
        IOrganizationEntitlements? entitlements = scope.ServiceProvider.GetService<IOrganizationEntitlements>();

        int published = 0;
        foreach (WorkflowExecution execution in stale)
        {
            try
            {
                int runPriority = await PublishRequestAsync(publish, entitlements, execution, now, ct);
                published++;
                _logger.LogWarning(
                    "Re-published stranded queued execution {ExecutionId} at run priority {RunPriority}",
                    execution.Id,
                    runPriority);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Re-publishing queued execution {ExecutionId} failed", execution.Id);
            }
        }

        return published;
    }

    /// <summary>
    /// which of the just-claimed lost runs get their one automatic resume. A run qualifies when it was
    /// never auto-resumed, points at a snapshot the ENGINE took for it (<see cref="ResumeCursor.IsEngineTakenSnapshot"/>;
    /// a legacy run or a T-002 archive never does) and no paid VideoGenerate work is in flight: any
    /// <c>external_generation_jobs</c> row of the run still <c>Reserved</c>/<c>Submitted</c> (or <c>Running</c>) may
    /// be charged by the vendor, so that run is failed and waits for a manual Resume. The ledger is checked for the
    /// whole run rather than only the lost step rows: a retry reusing a ledger row keeps the reserving attempt's
    /// step-result id, and a false "in flight" only costs an automatic resume, never a double charge.
    /// </summary>
    private static async Task<HashSet<Guid>> FindAutoResumableAsync(
        WorkflowEngineDbContext db, List<WorkflowExecution> claimed, CancellationToken ct)
    {
        List<WorkflowExecution> candidates = claimed
            .Where(e => e.AutoResumeCount == 0 && e.SnapshotDefinitionId != null)
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        Guid[] snapshotIds = candidates.Select(e => e.SnapshotDefinitionId!.Value).Distinct().ToArray();
        var snapshots = await db.WorkflowDefinitions.AsNoTracking()
            .Where(d => snapshotIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Kind, Owner = EF.Property<Guid?>(d, WorkflowEngineDbContext.SnapshotOfExecutionIdColumn) })
            .ToListAsync(ct);

        // A change-request or composition-revision replay run is never resumed: the replay decides each step afresh
        // and a resume would copy the reused steps a second time and run the revised step again. It fails instead.
        Guid[] definitionIds = candidates.Select(e => e.WorkflowDefinitionId).Distinct().ToArray();
        HashSet<Guid> replayDefinitions = (await db.WorkflowDefinitions.AsNoTracking()
            .Where(d => definitionIds.Contains(d.Id) && d.Kind == WorkflowDefinitionKind.CompositionReplay)
            .Select(d => d.Id)
            .ToListAsync(ct)).ToHashSet();

        Guid[] candidateIds = candidates.Select(e => e.Id).ToArray();
        ExternalGenerationJobStatus[] inFlight =
            [ExternalGenerationJobStatus.Reserved, ExternalGenerationJobStatus.Submitted, ExternalGenerationJobStatus.Running];
        HashSet<Guid> paidInFlight = (await db.ExternalGenerationJobs.AsNoTracking()
            .Where(j => candidateIds.Contains(j.ExecutionId) && inFlight.Contains(j.Status))
            .Select(j => j.ExecutionId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        HashSet<Guid> resumable = [];
        foreach (WorkflowExecution execution in candidates)
        {
            var snapshot = snapshots.FirstOrDefault(s => s.Id == execution.SnapshotDefinitionId);
            if (snapshot is not null
                && !replayDefinitions.Contains(execution.WorkflowDefinitionId)
                && ResumeCursor.IsEngineTakenSnapshot(execution.Id, snapshot.Kind, snapshot.Owner)
                && !paidInFlight.Contains(execution.Id))
            {
                resumable.Add(execution.Id);
            }
        }

        return resumable;
    }

    /// <summary>
    /// Publishes <paramref name="execution"/>'s <see cref="WorkflowExecutionRequested"/> at its org's run priority
    /// (B8c) and returns that priority. The executor only starts a <c>Queued</c> row, so a duplicate is harmless.
    /// </summary>
    private static async Task<int> PublishRequestAsync(
        IPublishEndpoint publish, IOrganizationEntitlements? entitlements, WorkflowExecution execution, DateTime now, CancellationToken ct)
    {
        int runPriority = await ResolveRunPriorityAsync(entitlements, execution.OrganizationId, ct);
        WorkflowExecutionRequested request = new()
        {
            ExecutionId = execution.Id,
            WorkflowDefinitionId = execution.WorkflowDefinitionId,
            ProjectId = execution.ProjectId,
            OrganizationId = execution.OrganizationId,
            InitiatedByUserId = execution.InitiatedByUserId ?? Guid.Empty,
            CorrelationId = execution.CorrelationId,
            RequestedAt = now,
            UserRequest = execution.UserRequest,
            StyleConfigJson = execution.StyleConfigJson,
            RunBriefJson = execution.RunBriefJson,
            DesignSystemPinJson = execution.DesignSystemPinJson,
            OutputFormat = execution.OutputFormatOverride?.ToString(),
            OutputResolution = execution.OutputResolution,
            // T-015: a hint only; the engine's RUN_CLASS_CHANGED re-check reads the row's own column.
            AdmittedRunRateBucket = execution.AdmittedRunRateBucket
        };

        if (RunPriority.AmqpPriorityToStamp(runPriority) is { } amqpPriority)
        {
            await publish.Publish(request, ctx => ctx.SetPriority(amqpPriority), ct);
        }
        else
        {
            await publish.Publish(request, ct);
        }

        return runPriority;
    }

    /// <summary>
    /// The org's queue-priority class (B8c), best-effort: a missing service, an empty org or a failed
    /// read yields <see cref="RunPriority.Standard"/> rather than stopping the re-publish — the whole
    /// point of this sweeper is that a stranded run gets another chance.
    /// </summary>
    private static async Task<int> ResolveRunPriorityAsync(
        IOrganizationEntitlements? entitlements, Guid organizationId, CancellationToken ct)
    {
        if (entitlements is null || organizationId == Guid.Empty)
        {
            return RunPriority.Standard;
        }

        try
        {
            return await RunPriority.OfAsync(entitlements, organizationId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return RunPriority.Standard;
        }
    }

    private static string BuildReason(WorkflowExecution execution) =>
        "Interrupted: engine instance lost. The workflow engine instance that was running this execution"
        + (string.IsNullOrWhiteSpace(execution.LeaseOwner) ? "" : $" ({execution.LeaseOwner})")
        + " stopped responding, so the platform ended the run. This was a platform interruption, not a cancellation.";
}
