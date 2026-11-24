using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// The one owner of "replace this workflow's steps" and "delete this workflow" (plan decisions 3-4), so no
/// edit or delete can destroy a past run's results through the <c>workflow_step_results → workflow_steps</c>
/// cascade, including runs from before engine snapshots (no <c>snapshot_definition_id</c>, results
/// referencing the workflow's own steps).
/// <list type="bullet">
/// <item>Before step rows are deleted, every one still referenced by a result is re-parented into a hidden
/// <see cref="WorkflowDefinitionKind.ExecutionSnapshot"/> archive (one per call, same project), and the
/// snapshot-less runs of the workflow that reference them get that archive as their snapshot.
/// Unreferenced steps are deleted as before.</item>
/// <item>A delete is refused while a run is Queued or Running; otherwise every run is re-pointed to its own
/// snapshot (engine-taken, or the archive) so the workflow's cascade reaches none of them.</item>
/// </list>
/// Each call is one transaction that first locks the workflow row and its step rows (<c>FOR UPDATE</c>, on
/// Postgres). Every writer that could collide already takes the conflicting lock without code of its own:
/// inserting a run takes <c>FOR KEY SHARE</c> on its <c>workflow_definitions</c> row and inserting a result
/// takes it on its <c>workflow_steps</c> row (the FK checks), so a run being queued or a result being written
/// is either seen here or waits until this call commits. Engine-snapshotted runs need nothing here: their
/// results reference their snapshot's steps. Archives and snapshots carry the source workflow's plain name
/// (<see cref="WorkflowDefinitionKind"/> alone hides them), so a run's name never changes when it is
/// re-pointed.
/// </summary>
public sealed class WorkflowStepReplacer(InferenceApiDbContext db)
{
    /// <summary>The 409 code a delete answers while a run of the workflow is Queued or Running.</summary>
    public const string RunActiveCode = "WORKFLOW_RUN_ACTIVE";

    public const string RunActiveMessage = "The workflow has a run in progress. Wait for it to finish or cancel it, then delete the workflow.";

    /// <summary>The name limit the API applies to workflow names.</summary>
    private const int MaxNameLength = 200;

    /// <summary>
    /// Replaces <paramref name="workflow"/>'s steps with <paramref name="newSteps"/> and saves every pending
    /// change of the context (the caller's rename, <c>UpdatedAt</c>) in the same transaction. The caller must
    /// not have added <paramref name="newSteps"/> to the context itself.
    /// </summary>
    /// <exception cref="DbUpdateConcurrencyException">The workflow was changed or removed meanwhile.</exception>
    public Task ReplaceStepsAsync(WorkflowDefinition workflow, IEnumerable<WorkflowStep> newSteps, CancellationToken ct)
    {
        List<WorkflowStep> steps = [.. newSteps];
        return InTransactionAsync(async () =>
        {
            await LockAsync(workflow.Id, ct);
            await ArchiveReferencedStepsAsync(workflow, ct);
            await db.WorkflowSteps
                .Where(s => s.WorkflowDefinitionId == workflow.Id)
                .ExecuteDeleteAsync(ct);
            db.WorkflowSteps.AddRange(steps);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    /// <summary>True while any run of the workflow is Queued or Running.</summary>
    public Task<bool> HasActiveRunAsync(Guid workflowId, CancellationToken ct) =>
        db.WorkflowExecutions.AnyAsync(
            e => e.WorkflowDefinitionId == workflowId
                && (e.Status == ExecutionStatus.Queued || e.Status == ExecutionStatus.Running), ct);

    /// <summary>
    /// Deletes <paramref name="workflow"/> (tracked by the context) after re-pointing every run of it to its
    /// own snapshot. Returns false, having written nothing, while a run of it is Queued or Running.
    /// </summary>
    public Task<bool> DeleteAsync(WorkflowDefinition workflow, CancellationToken ct) =>
        InTransactionAsync(async () =>
        {
            await LockAsync(workflow.Id, ct);
            if (await HasActiveRunAsync(workflow.Id, ct))
                return false;

            Guid? archiveId = await ArchiveReferencedStepsAsync(workflow, ct);

            // A run that never reached a step (cancelled while queued, failed at start) has neither a
            // snapshot nor a result: it is filed under the archive too, so it outlives the workflow and
            // its detail serves (empty) steps of its own.
            IQueryable<WorkflowExecution> unsnapshotted = db.WorkflowExecutions
                .Where(e => e.WorkflowDefinitionId == workflow.Id && e.SnapshotDefinitionId == null);
            if (await unsnapshotted.AnyAsync(ct))
            {
                Guid archive = archiveId ?? await CreateArchiveAsync(workflow, ct);
                await unsnapshotted.ExecuteUpdateAsync(set => set.SetProperty(e => e.SnapshotDefinitionId, (Guid?)archive), ct);
            }

            IQueryable<WorkflowExecution> snapshotted = db.WorkflowExecutions
                .Where(e => e.WorkflowDefinitionId == workflow.Id && e.SnapshotDefinitionId != null);
            if (await snapshotted.AnyAsync(ct))
                await snapshotted.ExecuteUpdateAsync(set => set.SetProperty(e => e.WorkflowDefinitionId, e => e.SnapshotDefinitionId ?? e.WorkflowDefinitionId), ct);

            db.WorkflowDefinitions.Remove(workflow);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    /// <summary>
    /// Takes the workflow row and its step rows FOR UPDATE (Postgres; Sqlite serializes writers by itself).
    /// Always the definition first, then its steps in id order, so two calls on one workflow cannot deadlock.
    /// </summary>
    private async Task LockAsync(Guid workflowId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM workflow_definitions WHERE id = {workflowId} FOR UPDATE", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM workflow_steps WHERE workflow_definition_id = {workflowId} ORDER BY id FOR UPDATE", ct);
    }

    /// <summary>
    /// Moves the workflow's steps that results still reference into a new archive and attaches the
    /// snapshot-less runs of the workflow that reference them. While a snapshot-less run of it is still
    /// Running (a run from before snapshots, its steps held in the engine's memory), ALL the steps move and
    /// that run is attached too, so the results it has yet to write still have their step rows. Null
    /// (nothing written) when there is nothing to keep.
    /// </summary>
    private async Task<Guid?> ArchiveReferencedStepsAsync(WorkflowDefinition workflow, CancellationToken ct)
    {
        bool legacyRunInFlight = await db.WorkflowExecutions.AnyAsync(
            e => e.WorkflowDefinitionId == workflow.Id && e.SnapshotDefinitionId == null && e.Status == ExecutionStatus.Running, ct);
        List<Guid> referenced = await db.WorkflowSteps
            .Where(s => s.WorkflowDefinitionId == workflow.Id
                && (legacyRunInFlight || db.WorkflowStepResults.Any(r => r.WorkflowStepId == s.Id)))
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (referenced.Count == 0)
            return null;

        Guid archiveId = await CreateArchiveAsync(workflow, ct);
        await db.WorkflowSteps
            .Where(s => referenced.Contains(s.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.WorkflowDefinitionId, archiveId), ct);
        await db.WorkflowExecutions
            .Where(e => e.WorkflowDefinitionId == workflow.Id
                && e.SnapshotDefinitionId == null
                && (e.Status == ExecutionStatus.Running || e.StepResults.Any(r => referenced.Contains(r.WorkflowStepId))))
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.SnapshotDefinitionId, (Guid?)archiveId), ct);
        return archiveId;
    }

    private async Task<Guid> CreateArchiveAsync(WorkflowDefinition workflow, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        string name = workflow.Name;
        WorkflowDefinition archive = new()
        {
            Id = Guid.NewGuid(),
            Name = name.Length <= MaxNameLength ? name : name[..MaxNameLength],
            ProjectId = workflow.ProjectId,
            Kind = WorkflowDefinitionKind.ExecutionSnapshot,
            RequiresUserInput = workflow.RequiresUserInput,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.WorkflowDefinitions.Add(archive);
        await db.SaveChangesAsync(ct);
        return archive.Id;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction (or the caller's, when one is open). A store without
    /// transactions (the in-memory provider) runs it as is: a workflow with no runs needs no bulk write there.
    /// </summary>
    private async Task<bool> InTransactionAsync(Func<Task<bool>> work, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || !db.Database.IsRelational())
            return await work();

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            bool result = await work();
            await transaction.CommitAsync(ct);
            return result;
        });
    }
}
