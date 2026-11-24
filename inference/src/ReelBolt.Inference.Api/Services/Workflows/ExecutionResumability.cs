using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// What a run needs for <c>POST …/executions/{id}/resume</c> (plan decision 5), and the 409 codes of Resume and
/// Retry. One place, so the route and the execution DTO's <c>canResume</c> cannot disagree.
/// </summary>
public static class ExecutionResumability
{
    /// <summary>409: the run is not Failed or Cancelled (or another resume/retry queued it first).</summary>
    public const string NotResumableCode = "RUN_NOT_RESUMABLE";

    public const string NotResumableMessage = "Only a failed or stopped run can be resumed.";

    public const string AlreadyQueuedMessage = "This run was already queued again.";

    /// <summary>409 (Retry): the run changed between the route's checks and its re-queue; reload it.</summary>
    public const string RunChangedCode = "RUN_CHANGED";

    public const string RunChangedMessage = "This run changed while you asked. Reload it and try again.";

    public const string RunGoneMessage = "This run no longer exists. Reload the page.";

    public const string WorkflowDeletedMessage = "This run's workflow was deleted while you asked. Reload the run and try again.";

    public const string StillStoppingCode = "RUN_STILL_STOPPING";

    public const string StillStoppingMessage = "This run is still stopping. Try again in a moment.";

    /// <summary>409: the run has no engine-taken snapshot (a legacy run): only Retry can run it again.</summary>
    public const string UnavailableCode = "RESUME_UNAVAILABLE";

    public const string UnavailableMessage = "This run started before runs could be resumed. Use Retry to run it again from the start.";

    /// <summary>409: Resume of a change-request or composition-revision run (a replay): it is asked for again instead.</summary>
    public const string ReplayResumeCode = "REVISION_RESUME_NOT_SUPPORTED";

    public const string ReplayResumeMessage = "Request the change again from the original video instead.";

    /// <summary>409: Retry of a run whose workflow was deleted and whose only snapshot is a legacy archive.</summary>
    public const string RetryUnavailableCode = "RETRY_UNAVAILABLE";

    public const string RetryUnavailableMessage = "This run's workflow was deleted and the run only has an archive of its old steps, which cannot be run again.";

    /// <summary>
    /// Security SQFU-S6: takes the run's workflow definition row FOR SHARE inside the caller's transaction, before
    /// a Retry or Resume re-queues the run. <c>WorkflowStepReplacer.DeleteAsync</c> holds the same row FOR UPDATE
    /// from its "no Queued/Running run" check to its commit, so the two serialize: a re-queue that comes first makes
    /// the delete see an active run and refuse; one that comes during the delete waits, then finds the run filed
    /// under its snapshot (its conditional UPDATE matches nothing) and answers 409. Two re-queues share the lock.
    /// Postgres only; SQLite serializes writers by itself.
    /// </summary>
    public static async Task LockDefinitionForShareAsync(InferenceApiDbContext db, Guid workflowDefinitionId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM workflow_definitions WHERE id = {workflowDefinitionId} FOR SHARE", ct);
    }

    /// <summary>
    /// The 409 of a Retry or Resume whose conditional UPDATE matched nothing, from a fresh read of the row, so it says
    /// what actually changed: <c>RUN_STILL_STOPPING</c> only while the run is still under the route, not active and its
    /// lease is live (a renewal landed after the check); otherwise <paramref name="changedCode"/> with the reason: the
    /// run is gone, its workflow was deleted (the run is filed under its snapshot), or it was queued again.
    /// </summary>
    public static async Task<ConflictObjectResult> LostRequeueAsync(
        InferenceApiDbContext db, Guid executionId, Guid projectId, Guid workflowId, DateTime now, string changedCode, CancellationToken ct)
    {
        var current = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == executionId)
            .Select(e => new { e.Status, e.LeaseExpiresAt, e.ProjectId, e.WorkflowDefinitionId })
            .FirstOrDefaultAsync(ct);

        if (current is null)
            return new ConflictObjectResult(new { code = changedCode, message = RunGoneMessage });
        if (current.ProjectId != projectId || current.WorkflowDefinitionId != workflowId)
            return new ConflictObjectResult(new { code = changedCode, message = WorkflowDeletedMessage });
        if (current.Status is ExecutionStatus.Queued or ExecutionStatus.Running)
            return new ConflictObjectResult(new { code = changedCode, message = AlreadyQueuedMessage });
        if (current.LeaseExpiresAt > now)
            return new ConflictObjectResult(new { code = StillStoppingCode, message = StillStoppingMessage });
        return new ConflictObjectResult(new { code = changedCode, message = RunChangedMessage });
    }

    /// <summary>The statuses a run may be resumed from.</summary>
    public static bool HasResumableStatus(ExecutionStatus status) =>
        status is ExecutionStatus.Failed or ExecutionStatus.Cancelled;

    /// <summary>
    /// Every resume precondition but credits, plan and lease: Failed/Cancelled, an engine-taken snapshot
    /// (<paramref name="engineTakenSnapshot"/>) and the agents' snapshot recorded with it.
    /// </summary>
    public static bool CanResume(ExecutionStatus status, bool engineTakenSnapshot, bool hasAgentSnapshot) =>
        HasResumableStatus(status) && engineTakenSnapshot && hasAgentSnapshot;

    /// <summary>True when <paramref name="execution"/>'s snapshot row is one the engine took for it.</summary>
    public static async Task<bool> HasEngineTakenSnapshotAsync(InferenceApiDbContext db, WorkflowExecution execution, CancellationToken ct)
    {
        if (execution.SnapshotDefinitionId is not { } snapshotId)
            return false;
        IReadOnlySet<Guid> owned = await EngineTakenAsync(db, [(execution.Id, snapshotId)], ct);
        return owned.Contains(execution.Id);
    }

    /// <summary>
    /// The executions among <paramref name="runs"/> (execution id, snapshot definition id) whose snapshot is
    /// engine-taken, in one read. The owner column is engine-only (a shadow property of the engine's model, no
    /// mapping in the API's, whose migrations never touch workflow_* tables), so it is read with SQL; a
    /// non-relational provider (the InMemory unit tests) has no such column and answers none.
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> EngineTakenAsync(
        InferenceApiDbContext db, IReadOnlyCollection<(Guid ExecutionId, Guid SnapshotId)> runs, CancellationToken ct)
    {
        if (runs.Count == 0 || !db.Database.IsRelational())
            return new HashSet<Guid>();

        List<Guid> snapshotIds = [.. runs.Select(r => r.SnapshotId).Distinct()];
        List<SnapshotOwnerRow> rows = await db.Database
            .SqlQueryRaw<SnapshotOwnerRow>(SnapshotOwnersSql)
            .Where(r => snapshotIds.Contains(r.Id))
            .ToListAsync(ct);
        Dictionary<Guid, SnapshotOwnerRow> byId = rows.ToDictionary(r => r.Id);

        return runs
            .Where(r => byId.TryGetValue(r.SnapshotId, out SnapshotOwnerRow? row)
                && ExecutionSnapshotOwnership.IsEngineTakenSnapshot(r.ExecutionId, row.Kind, row.Owner))
            .Select(r => r.ExecutionId)
            .ToHashSet();
    }

    /// <summary>Constant SQL (no input): composed by EF with the id filter below.</summary>
    private const string SnapshotOwnersSql =
        "SELECT id AS \"Id\", kind AS \"Kind\", " + ExecutionSnapshotOwnership.OwnerColumn + " AS \"Owner\" FROM workflow_definitions";

    /// <summary>One <c>workflow_definitions</c> row as the ownership check reads it.</summary>
    public sealed class SnapshotOwnerRow
    {
        public Guid Id { get; set; }

        public WorkflowDefinitionKind Kind { get; set; }

        public Guid? Owner { get; set; }
    }
}
