using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.EditTimelines;

public enum EditCommitStatus
{
    Committed,

    /// <summary>A concurrent writer got there first; nothing was written. <see cref="EditCommitResult.Revision"/> is the revision it left.</summary>
    Conflict,

    /// <summary>The timeline vanished under the writer; nothing was written.</summary>
    Gone
}

public sealed record EditCommitResult(EditCommitStatus Status, int Revision);

/// <summary>
/// The write core of a timeline edit, shared by <c>PATCH edit-timelines/{id}</c> and <see cref="IEditTimelineWriter"/>: the revision
/// bump, the op-log row, the automatic snapshots (the revision this edit replaces after an idle gap or a restore, and every
/// <see cref="SnapshotIntervalRevisions"/>th revision), the snapshot cap and the optimistic-concurrency handling. Everything before it
/// (size guards, the base-revision check, validation) belongs to the caller, because each entry answers those in its own shape.
/// </summary>
public static class EditTimelineCommit
{
    /// <summary>How often a full document snapshot is retained alongside the op log: every 20th revision.</summary>
    public const int SnapshotIntervalRevisions = 20;

    /// <summary>An edit arriving this long after the previous revision snapshots that revision (cause idle).</summary>
    public static readonly TimeSpan IdleSnapshotGap = TimeSpan.FromMinutes(2);

    /// <param name="db">The context the <paramref name="timeline"/> entity is tracked by.</param>
    /// <param name="timeline">The tracked row, as read at <paramref name="baseRevision"/>.</param>
    /// <param name="baseRevision">The revision the edit was made from; the new revision is one more.</param>
    /// <param name="documentJson">The new document, serialized once by the caller (the bytes it measured).</param>
    /// <param name="opsJson">The op-log entry for the new revision.</param>
    /// <param name="restore">True when the ops carry a restore marker (the replaced revision is kept as <c>beforeRestore</c>).</param>
    /// <param name="createdByUserId">Recorded on snapshots; null for a server-side edit with no signed-in person.</param>
    public static async Task<EditCommitResult> CommitAsync(
        InferenceApiDbContext db,
        EditTimelineEntity timeline,
        int baseRevision,
        string documentJson,
        string opsJson,
        bool restore,
        DateTime now,
        Guid? createdByUserId,
        TimeSpan autoCheckpointWindow,
        CancellationToken ct)
    {
        Guid id = timeline.Id;
        int newRevision = baseRevision + 1;

        // Server-side checkpoints of the revision this edit replaces (D14): a restore keeps what it overwrites (beforeRestore), and
        // the first edit after a pause of IdleSnapshotGap snapshots the previous revision (idle). One snapshot per revision.
        string? priorCause = restore
            ? EditTimelineSnapshotCause.BeforeRestore
            : now - timeline.UpdatedAt >= IdleSnapshotGap ? EditTimelineSnapshotCause.Idle : null;
        string priorDocumentJson = timeline.DocumentJson;
        bool snapshotPrior = priorCause is not null
            && !await db.EditTimelineSnapshots.AnyAsync(s => s.TimelineId == id && s.Revision == baseRevision, ct);

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);

        if (snapshotPrior)
        {
            db.EditTimelineSnapshots.Add(NewSnapshot(id, baseRevision, priorDocumentJson, priorCause!, now, createdByUserId, autoCheckpointWindow));
        }

        timeline.DocumentJson = documentJson;
        timeline.Revision = newRevision;
        timeline.UpdatedAt = now;

        db.EditTimelineOps.Add(new EditTimelineOp
        {
            Id = Guid.NewGuid(),
            TimelineId = timeline.Id,
            Revision = newRevision,
            OpsJson = opsJson,
            CreatedAt = now
        });

        if (newRevision % SnapshotIntervalRevisions == 0)
        {
            db.EditTimelineSnapshots.Add(NewSnapshot(timeline.Id, newRevision, documentJson, EditTimelineSnapshotCause.Milestone, now, createdByUserId, autoCheckpointWindow));
        }

        int automaticAdded = (snapshotPrior ? 1 : 0) + (newRevision % SnapshotIntervalRevisions == 0 ? 1 : 0);
        if (automaticAdded > 0)
        {
            await TimelineSnapshotRetention.EnforceCapAsync(db, timeline.Id, automaticAdded, ct);
        }

        try
        {
            // The Revision column is a concurrency token, so this emits `WHERE id = @id AND revision = @original`: a writer that
            // advanced the revision between the caller's check and this statement fails here instead of silently clobbering.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || IsUniqueViolation(ex))
        {
            // Two shapes of the same race, one answer (see PatchEditTimeline): nothing of this edit was written and the caller rebases.
            await transaction.RollbackAsync(CancellationToken.None);
            DropPendingTimelineWrites(db);
            int? current = await ReadCurrentRevisionAsync(db, timeline.ProjectId, id, ct);
            return current is null
                ? new EditCommitResult(EditCommitStatus.Gone, 0)
                : new EditCommitResult(EditCommitStatus.Conflict, current.Value);
        }

        await transaction.CommitAsync(ct);
        return new EditCommitResult(EditCommitStatus.Committed, newRevision);
    }

    public static EditTimelineSnapshot NewSnapshot(
        Guid timelineId, int revision, string documentJson, string cause, DateTime now, Guid? createdByUserId, TimeSpan autoCheckpointWindow, string? name = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TimelineId = timelineId,
            Revision = revision,
            DocumentJson = documentJson,
            CreatedAt = now,
            Name = name,
            Cause = cause,
            CreatedByUserId = createdByUserId,
            ExpiresAt = TimelineSnapshotRetention.ExpiryFor(cause, revision, now, autoCheckpointWindow)
        };

    /// <summary>
    /// The revision a racing writer left behind. <c>AsNoTracking</c> because the tracked entity still carries the rejected edit.
    /// </summary>
    public static Task<int?> ReadCurrentRevisionAsync(InferenceApiDbContext db, Guid projectId, Guid id, CancellationToken ct) =>
        db.EditTimelines.AsNoTracking()
            .Where(t => t.Id == id && t.ProjectId == projectId)
            .Select(t => (int?)t.Revision)
            .FirstOrDefaultAsync(ct);

    /// <summary>True for a unique-index violation: Postgres SQLSTATE 23505, or the SQLite equivalent the test harnesses use.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
        || (ex.InnerException?.GetType().Name == "SqliteException"
            && ex.InnerException.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase));

    /// <summary>A rolled-back commit leaves its rows tracked as pending; a context that goes on to write must not replay them.</summary>
    private static void DropPendingTimelineWrites(InferenceApiDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries()
                     .Where(e => e.Entity is EditTimelineEntity or EditTimelineOp or EditTimelineSnapshot)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }
}
