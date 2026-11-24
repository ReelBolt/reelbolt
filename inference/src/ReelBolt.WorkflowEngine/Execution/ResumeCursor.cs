using System.Net;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Replay;

namespace ReelBolt.WorkflowEngine.Execution;

/// <summary>
/// The reuse half of a RESUME (plan decision 6): walks this execution's recorded results in the order they were
/// written while the run re-dispatches its steps, handing back each completed result instead of running the step
/// again, until the first dispatch that has no usable result. From that miss on the run is live for good.
///
/// <para>
/// <b>What is matched.</b> The run's non-superseded <c>workflow_step_results</c> rows, oldest first: its
/// trajectory. Every row is one top-level dispatch (the loop in <see cref="WorkflowExecutorService"/> is the only
/// writer of this execution's rows: ForEach, Parallel, the rooms and a ReviewLoop iteration each produce ONE row
/// for the whole container, never per child), so the k-th dispatch of a step meets the k-th row of that step.
/// A row is reused only when it is <see cref="StepStatus.Completed"/> or <see cref="StepStatus.Skipped"/> and
/// every storage object it names still answers a HEAD; anything else (Failed, a row a lost engine left Running,
/// a gone render, a step this snapshot does not have) is a miss. "When in doubt, re-run."
/// </para>
///
/// <para>
/// <b>Branches.</b> A Conditional's or ReviewLoop's decision is not on its row, so it is read from the trajectory:
/// the step dispatched next is the step of the next row. With no next row the decision is unknown and the row is
/// a miss. Every other step type always advances by one.
/// </para>
///
/// <para>
/// <b>Rows a previous resume replaced.</b> When a dispatch was a miss, the live re-run writes a second row of the
/// same step right after the first, so a run of consecutive rows of one step means only the LAST of them is the
/// result the run went on with; the earlier ones (a failed attempt, a row whose storage was gone) are skipped.
/// </para>
/// </summary>
public sealed class ResumeCursor
{
    private readonly IReadOnlyList<WorkflowStepResult> _trajectory;
    private readonly Func<string, CancellationToken, Task<bool>> _objectExists;
    private int _position;

    private readonly string _projectKeyPrefix;

    private ResumeCursor(IReadOnlyList<WorkflowStepResult> trajectory, Func<string, CancellationToken, Task<bool>> objectExists, Guid projectId)
    {
        _trajectory = trajectory;
        _objectExists = objectExists;
        _projectKeyPrefix = $"projects/{projectId}/";
    }

    /// <summary>True until the first miss; afterwards every dispatch runs live.</summary>
    public bool IsReusing { get; private set; } = true;

    /// <summary>How many dispatches were served from recorded results.</summary>
    public int ReusedCount { get; private set; }

    /// <summary>Why reuse stopped (null while still reusing).</summary>
    public string? MissReason { get; private set; }

    /// <summary>
    /// Loads <paramref name="executionId"/>'s recorded, non-superseded results. Untracked: a reused row is read,
    /// never written (its charge, cache entry and completion event stay the original dispatch's).
    /// </summary>
    public static async Task<ResumeCursor> LoadAsync(
        WorkflowEngineDbContext db,
        Guid executionId,
        Guid projectId,
        Func<string, CancellationToken, Task<bool>> objectExists,
        CancellationToken ct)
    {
        List<WorkflowStepResult> rows = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == executionId && r.SupersededAt == null)
            .ToListAsync(ct);

        // Written order. ExecutedAt is stamped when the dispatch inserts its row; the rest only break exact ties.
        List<WorkflowStepResult> trajectory = rows
            .OrderBy(r => r.ExecutedAt)
            .ThenBy(r => r.CompletedAt ?? DateTime.MaxValue)
            .ThenBy(r => r.Id)
            .ToList();
        return new ResumeCursor(trajectory, objectExists, projectId);
    }

    /// <summary>
    /// The recorded outcome of this dispatch of <paramref name="step"/>, or null when it is a miss (which ends
    /// reuse for the rest of the run). Consumes the matched row.
    /// <paramref name="accept"/>, when given, is asked about the row's recorded output BEFORE the row is consumed; a
    /// false answer is an ordinary miss (this row and everything after it is what the live run replaces).
    /// </summary>
    public async Task<ResumedStep?> TryReuseAsync(
        WorkflowStep step, IReadOnlyList<WorkflowStep> steps, int currentStepIndex, int iterationCount, CancellationToken ct,
        Func<string, CancellationToken, Task<bool>>? accept = null)
    {
        if (!IsReusing)
            return null;

        // Skip the earlier rows of a run of consecutive rows of this step: only the last one carried the run on.
        while (_position + 1 < _trajectory.Count
            && _trajectory[_position].WorkflowStepId == step.Id
            && _trajectory[_position + 1].WorkflowStepId == step.Id)
        {
            _position++;
        }

        if (_position >= _trajectory.Count)
            return Miss($"no recorded result for step {step.StepOrder} (the run never got this far)");

        WorkflowStepResult row = _trajectory[_position];
        if (row.WorkflowStepId != step.Id)
            return Miss($"step {step.StepOrder} was not the next step this run recorded");

        if (row.Status is not (StepStatus.Completed or StepStatus.Skipped))
            return Miss($"step {step.StepOrder}'s recorded result is {row.Status}");

        int nextStepIndex = currentStepIndex + 1;
        if (step.StepType is StepType.Conditional or StepType.ReviewLoop)
        {
            if (_position + 1 >= _trajectory.Count)
                return Miss($"step {step.StepOrder} ({step.StepType}) has no recorded next step, so its decision is unknown");

            Guid nextStepId = _trajectory[_position + 1].WorkflowStepId;
            nextStepIndex = IndexOf(steps, nextStepId);
            if (nextStepIndex < 0)
                return Miss($"step {step.StepOrder} ({step.StepType}) led to a step this run does not have");
        }

        foreach (string? key in new[] { row.OutputStorageKey, row.ArtifactStorageKey, row.TimelineStorageKey })
        {
            if (string.IsNullOrEmpty(key))
                continue;
            // SQFU-S5: a reused key is handed on as this run's output, so it must be this project's object (the
            // rule ProjectFileWorkspace.ValidateProjectScope enforces); anything else re-runs, and is never HEADed.
            if (!key.StartsWith(_projectKeyPrefix, StringComparison.Ordinal))
                return Miss($"step {step.StepOrder}'s stored object {key} is outside this project's storage");
            if (!await _objectExists(key, ct))
                return Miss($"step {step.StepOrder}'s stored object {key} no longer exists");
        }

        string recordedOutput = CompositionReplaySession.OriginalOutput(row);
        if (accept is not null && !await accept(recordedOutput, ct))
            return Miss($"step {step.StepOrder}'s recorded result can no longer be reused");

        _position++;
        ReusedCount++;
        return new ResumedStep(row, new StepExecutionResult
        {
            Output = recordedOutput,
            NextStepIndex = nextStepIndex,
            NewIterationCount = step.StepType == StepType.ReviewLoop ? row.IterationNumber ?? iterationCount : iterationCount,
            Status = row.Status,
            // Deliberately null: the iteration's review score was recorded by the dispatch that ran it, and the
            // executor records a score only for a result carrying an iteration number. The count itself travels
            // in NewIterationCount above.
            IterationNumber = null,
            OutputStorageKey = row.OutputStorageKey,
            ArtifactStorageKey = row.ArtifactStorageKey,
            TimelineStorageKey = row.TimelineStorageKey,
            ChatTranscriptJson = row.ChatTranscriptJson
        });
    }

    /// <summary>
    /// True when <paramref name="executionId"/>'s snapshot definition is one the ENGINE took for that run: a
    /// <see cref="WorkflowDefinitionKind.ExecutionSnapshot"/> row owned by the run (<c>snapshot_of_execution_id</c>).
    /// A kind-5 row with no owner is a T-002 archive of a legacy run's edited steps: never resumed or retried from.
    /// Delegates to <see cref="ExecutionSnapshotOwnership.IsEngineTakenSnapshot"/>, the one definition the API uses too.
    /// </summary>
    public static bool IsEngineTakenSnapshot(Guid executionId, WorkflowDefinitionKind snapshotKind, Guid? snapshotOfExecutionId) =>
        ExecutionSnapshotOwnership.IsEngineTakenSnapshot(executionId, snapshotKind, snapshotOfExecutionId);

    /// <summary><see cref="IsEngineTakenSnapshot(Guid, WorkflowDefinitionKind, Guid?)"/> for the run's current snapshot row.</summary>
    public static async Task<bool> IsEngineTakenSnapshotAsync(WorkflowEngineDbContext db, Guid executionId, Guid snapshotDefinitionId, CancellationToken ct)
    {
        var snapshot = await db.WorkflowDefinitions.AsNoTracking()
            .Where(d => d.Id == snapshotDefinitionId)
            .Select(d => new { d.Kind, Owner = EF.Property<Guid?>(d, WorkflowEngineDbContext.SnapshotOfExecutionIdColumn) })
            .FirstOrDefaultAsync(ct);
        return snapshot is not null && IsEngineTakenSnapshot(executionId, snapshot.Kind, snapshot.Owner);
    }

    private ResumedStep? Miss(string reason)
    {
        IsReusing = false;
        MissReason = reason;
        // Everything the old attempt recorded from here on is what the live run is about to redo. Left current,
        // a later resume would meet those stale rows first, miss on them again and re-run (and re-charge) the
        // steps this live run completes.
        _replacedRowIds = [.. _trajectory.Skip(_position).Select(r => r.Id)];
        return null;
    }

    private List<Guid> _replacedRowIds = [];

    /// <summary>
    /// After the miss: stamps <see cref="WorkflowStepResult.SupersededAt"/> on the old attempt's rows at and after
    /// the miss point, which the live run replaces (retry-supersede semantics: kept for audit and billing, ignored by
    /// matching, the live view and lineage). Returns how many rows it stamped; nothing before the miss is touched.
    /// </summary>
    public async Task<int> SupersedeReplacedAsync(WorkflowEngineDbContext db, CancellationToken ct)
    {
        if (_replacedRowIds.Count == 0)
            return 0;

        List<Guid> ids = _replacedRowIds;
        _replacedRowIds = [];
        List<WorkflowStepResult> rows = await db.WorkflowStepResults
            .Where(r => ids.Contains(r.Id) && r.SupersededAt == null)
            .ToListAsync(ct);
        DateTime now = DateTime.UtcNow;
        foreach (WorkflowStepResult row in rows)
            row.SupersededAt = now;
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private static int IndexOf(IReadOnlyList<WorkflowStep> steps, Guid stepId)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Id == stepId)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// The storage check a reused row must pass: a HEAD per key, the same probe the step cache runs on a hit
    /// (<c>StepResultCache.StorageArtifactsStillExistAsync</c>). No S3 client wired (unit tests) trusts the row, as
    /// the cache does. Unlike the cache, any failure other than the caller's cancellation is a miss: re-running a
    /// step is always safe, pointing at nothing is not.
    /// </summary>
    public static Func<string, CancellationToken, Task<bool>> StorageProbe(IServiceProvider services)
    {
        IAmazonS3? s3 = services.GetService<IAmazonS3>();
        if (s3 is null)
            return (_, _) => Task.FromResult(true);

        string bucket = services.GetService<IConfiguration>()?["MinIO:BucketName"] ?? "reelforge";
        return async (key, ct) =>
        {
            try
            {
                await s3.GetObjectMetadataAsync(bucket, key, ct);
                return true;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }
        };
    }
}

/// <summary>A dispatch served from a recorded result: the row it came from and the result the run proceeds with.</summary>
public sealed record ResumedStep(WorkflowStepResult Row, StepExecutionResult Result);
