using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.Inference.Api.Services.Projects;

/// <summary>
/// The one project-file deletion path: the dashboard's <c>DELETE /api/v1/projects/{p}/files/{id}</c> and
/// the public API's <c>DELETE /api/public/v1/projects/{p}/files/{id}</c> both call it, so picture copies,
/// the plan quota preflight, the storage guard, the durable retention mark and the vector-index delete
/// cannot drift. Authority (Edit on the project) and the quarantine/held gates are the caller's.
/// </summary>
public interface IProjectFileDeletionService
{
    /// <summary>Null on success; a 409 <c>Conflict</c> when a picture's copy would not fit the plan.</summary>
    Task<IActionResult?> DeleteAsync(Project project, ProjectFile file, CancellationToken ct);
}

/// <inheritdoc />
public sealed class ProjectFileDeletionService(
    InferenceApiDbContext db,
    IFileStorageService fileStorage,
    IPlanGateService? planGate = null,
    PublishModeration? publishModeration = null,
    IPublishEndpoint? publishEndpoint = null,
    OrganizationStorageLock? storageLock = null,
    ILogger? logger = null) : IProjectFileDeletionService
{
    public async Task<IActionResult?> DeleteAsync(Project project, ProjectFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(file);
        Guid projectId = project.Id;

        // D10: every picture using this photo gets its own copy first (saved), then the original goes; a delete
        // whose copies would not fit the plan's storage is refused before anything is copied (409). S-09: the
        // preflight, the copy and the record are one critical section under the organization's storage guard,
        // whose cross-replica guarantee is the advisory lock, so parallel deletes cannot each pass a check only
        // one of them fits. A refusal returns before the commit and the guard rolls back.
        OrganizationStorageLock guard = storageLock ?? new OrganizationStorageLock();
        await using OrganizationStorageGuard storageGuard = await guard.AcquireAsync(db, project.OrganizationId, ct);
        if (await PictureSourceCopies.CheckCopiesFitAsync(
                db, planGate, project.OrganizationId, projectId, [file], folder: false, ct) is { } overQuota)
            return new ConflictObjectResult(overQuota.ToBody());
        await PictureSourceCopies.CopyBeforeDeleteAsync(
            db, fileStorage, projectId, [file], logger, ct,
            async (key, token) => publishModeration is null
                || await publishModeration.GateStoredCopyAsync(projectId, key, token) is null);

        // The row and its durable deletion mark go in one transaction, then the object - same reasoning as
        // DeleteFolder (QA-SQC-1): a commit failure is retryable, and a failed object delete is retried by the
        // retention sweep because the mark is already durable.
        List<StorageRetentionMark> marks = MarkForDeletion([file], project.OrganizationId, DateTime.UtcNow);
        db.StorageRetentionMarks.AddRange(marks);
        db.ProjectFiles.Remove(file);
        await ForgetVerdictsAsync(projectId, [file.StorageKey], ct);
        await db.SaveChangesAsync(CancellationToken.None);
        await storageGuard.CommitAsync(CancellationToken.None);

        try
        {
            await DeleteMarkedObjectsAsync(projectId, marks, [file], CancellationToken.None);
        }
        catch
        {
            // The row is already gone, so the file's vector chunks must still go: queue the delete before
            // surfacing the object-store failure, or they are left behind with no row to name them.
            await PublishIndexDeletesAsync(projectId, [file.Id]);
            throw;
        }

        await PublishIndexDeletesAsync(projectId, [file.Id]);
        return null;
    }

    private static List<StorageRetentionMark> MarkForDeletion(
        IEnumerable<ProjectFile> files, Guid organizationId, DateTime now) =>
        [.. files
            .Where(f => !string.IsNullOrEmpty(f.StorageKey))
            .Select(f => new StorageRetentionMark
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                ProjectId = f.ProjectId,
                StorageKey = f.StorageKey,
                Category = f.Category,
                MarkedAt = now
            })];

    /// <summary>The stored moderation verdict belongs to the object at its key: it goes when the object is deleted.</summary>
    private async Task ForgetVerdictsAsync(Guid projectId, IReadOnlyCollection<string> keys, CancellationToken ct) =>
        db.OutputModerationVerdicts.RemoveRange(await db.OutputModerationVerdicts
            .Where(v => v.ProjectId == projectId && keys.Contains(v.StorageKey)).ToListAsync(ct));

    /// <summary>Queues the vector-chunk removal for a row that is already gone. Uncancellable on purpose.</summary>
    private async Task PublishIndexDeletesAsync(Guid projectId, IReadOnlyCollection<Guid> fileIds)
    {
        if (publishEndpoint is null) return;
        foreach (Guid fileId in fileIds)
        {
            await publishEndpoint.Publish(new ProjectFileIndexingRequested
            {
                ProjectId = projectId,
                FileId = fileId,
                Operation = "Delete",
                RequestedAt = DateTime.UtcNow
            }, CancellationToken.None);
        }
    }

    /// <summary>Deletes the object of the committed row and drops its mark; a transient failure keeps the mark.</summary>
    private async Task DeleteMarkedObjectsAsync(
        Guid projectId, IReadOnlyList<StorageRetentionMark> marks, IReadOnlyList<ProjectFile> files, CancellationToken ct)
    {
        Dictionary<string, StorageRetentionMark> byKey = [];
        foreach (StorageRetentionMark mark in marks) byKey.TryAdd(mark.StorageKey, mark);

        List<StorageRetentionMark> done = [];
        foreach (ProjectFile file in files)
        {
            if (!byKey.TryGetValue(file.StorageKey, out StorageRetentionMark? mark)) continue;
            try
            {
                await fileStorage.DeleteAsync(projectId, file.StorageKey, ct);
                done.Add(mark);
            }
            catch (Exception ex) when (StorageFailure.IsTransient(ex))
            {
                logger?.LogWarning(
                    ex, "Could not remove the object of deleted project file {FileId}; its retention mark makes the sweep retry it", file.Id);
            }
        }

        if (done.Count > 0)
        {
            db.StorageRetentionMarks.RemoveRange(done);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
