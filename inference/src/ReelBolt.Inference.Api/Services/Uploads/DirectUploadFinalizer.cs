using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Uploads;

/// <summary>How a finished direct upload ended, before any HTTP shape is chosen.</summary>
public enum DirectUploadFinalizeOutcome
{
    /// <summary>The file is recorded now (201 on the dashboard).</summary>
    Created,

    /// <summary>A retry of a completion that already succeeded; the same row (200 on the dashboard).</summary>
    Replayed,

    /// <summary>The service refused (<see cref="DirectUploadFinalizeResult.Refused"/>).</summary>
    Refused,

    /// <summary>The recorded row's bytes are held, so a replay must not touch it.</summary>
    Held,

    /// <summary>The scan matched: the answer says nothing but "cannot be used".</summary>
    Quarantined
}

public sealed record DirectUploadFinalizeResult(
    DirectUploadFinalizeOutcome Outcome,
    ProjectFile? File = null,
    DirectUploadRefusedException? Refused = null);

/// <summary>
/// Step 3 of a direct upload, after the caller has been authorised and the ticket verified: the one path
/// the dashboard's <c>upload-complete</c> and the public API's <c>uploads/{id}/complete</c> share, so the
/// replay rule, the held/quarantine answers, the post-add effects and the moderation re-scan cannot drift.
/// </summary>
public interface IDirectUploadFinalizer
{
    Task<DirectUploadFinalizeResult> FinalizeAsync(
        Guid projectId, Guid userId, DirectUploadTicket ticket, DirectUploadCompletion completion, CancellationToken ct);
}

/// <inheritdoc />
public sealed class DirectUploadFinalizer(
    InferenceApiDbContext db,
    IDirectUploadService uploads,
    IProjectFileAddedEffects fileAddedEffects,
    IContentModerationEnqueuer? moderationEnqueuer = null) : IDirectUploadFinalizer
{
    public async Task<DirectUploadFinalizeResult> FinalizeAsync(
        Guid projectId, Guid userId, DirectUploadTicket ticket, DirectUploadCompletion completion, CancellationToken ct)
    {
        // A completion for a row that already exists is the retry of one that succeeded.
        bool alreadyRecorded = await db.ProjectFiles
            .AnyAsync(f => f.Id == ticket.FileId && f.ProjectId == projectId, ct);
        if (alreadyRecorded)
        {
            // A replay rewrites the recorded row, so a row whose bytes are kept is not touched.
            ProjectFile existing = await db.ProjectFiles.AsNoTracking()
                .FirstAsync(f => f.Id == ticket.FileId && f.ProjectId == projectId, ct);
            if (await OutputGate.IsFileHeldAsync(db, existing, ct))
                return new DirectUploadFinalizeResult(DirectUploadFinalizeOutcome.Held);
        }

        ProjectFile projectFile;
        try
        {
            projectFile = await uploads.CompleteAsync(ticket, completion, ct);
        }
        catch (DirectUploadRefusedException refused)
        {
            return new DirectUploadFinalizeResult(DirectUploadFinalizeOutcome.Refused, Refused: refused);
        }

        if (alreadyRecorded)
        {
            // A quarantined row is never handed back to its uploader, even on a replay.
            if (projectFile.CsamStatus == CsamScanStatus.Quarantined)
                return new DirectUploadFinalizeResult(DirectUploadFinalizeOutcome.Quarantined);

            // The service reset the verdict on a replay; offer the row for a fresh moderation scan
            // only once its CSAM verdict is settled and not a quarantine.
            if (CsamScanGate.AllowsModeration(projectFile))
                moderationEnqueuer?.TryQueue(projectFile.Id);
            return new DirectUploadFinalizeResult(DirectUploadFinalizeOutcome.Replayed, projectFile);
        }

        // R22 and the summarizer, exactly as the multipart form action applies them (the service leaves a
        // text upload Pending and a media upload Done/NotIndexed), then the activity feed.
        await fileAddedEffects.RunAsync(projectFile, userId, ct);

        if (projectFile.CsamStatus == CsamScanStatus.Quarantined)
            return new DirectUploadFinalizeResult(DirectUploadFinalizeOutcome.Quarantined);

        return new DirectUploadFinalizeResult(DirectUploadFinalizeOutcome.Created, projectFile);
    }
}
