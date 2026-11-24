using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>Removes a file's bytes from the project's history (contract section 6).</summary>
public interface IProjectHistoryEraser
{
    /// <summary>
    /// "Delete forever" and quarantine: marks every history item of the file erased (and every other
    /// file item that shares one of its contents), then deletes the protected copies of those
    /// contents. Idempotent. While the project's output hold is active or unreadable a user-driven
    /// erase keeps every content a platform-made item names; a quarantine erase always proceeds.
    /// </summary>
    Task EraseFileAsync(Guid projectId, Guid fileId, CancellationToken ct);

    /// <summary>
    /// Project delete: removes every object under the project's <c>history/</c> prefix, not only the
    /// keys blob rows name. A listing finds objects a failed/raced erase or a lost row left behind.
    /// The rows go with the project (cascade); a failure propagates so the caller can keep the
    /// project and retry.
    /// </summary>
    Task DeleteProjectObjectsAsync(Guid projectId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class ProjectHistoryEraser : IProjectHistoryEraser
{
    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly ILogger<ProjectHistoryEraser> _logger;
    private readonly TimeProvider _clock;
    private readonly IObjectListing? _listing;

    public ProjectHistoryEraser(
        InferenceApiDbContext db,
        IFileStorageService storage,
        ILogger<ProjectHistoryEraser> logger,
        IObjectListing? listing = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
        _listing = listing;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task EraseFileAsync(Guid projectId, Guid fileId, CancellationToken ct)
    {
        string itemKey = fileId.ToString("D");
        DateTime now = _clock.GetUtcNow().UtcDateTime;

        List<ProjectVersionItem> own = await _db.ProjectVersionItems
            .Where(i => i.ProjectId == projectId
                        && i.Kind == ProjectVersionItemKind.File
                        && i.ItemKey == itemKey)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Two removals share this method. A quarantine erase (CSAM or moderation callers, and the
        // GC retry of their failed erase) is the platform removing bytes it has already hidden:
        // it always proceeds and wins over the output hold. "Delete forever" is a user request, so
        // while the project's output hold is active or unreadable the contents of platform-made
        // items are evidence and are neither marked erased nor deleted, whatever key captured them.
        bool quarantined = await IsQuarantinedAsync(projectId, fileId, ct).ConfigureAwait(false);

        HashSet<string> heldContentIds = [];
        if (!quarantined)
        {
            ProjectOutputHoldState hold = await ProjectOutputHold.ReadAsync(_db, projectId, ct)
                .ConfigureAwait(false);
            if (hold.IsActive)
            {
                heldContentIds = (await _db.ProjectVersionItems
                        .AsNoTracking()
                        .Where(i => i.ProjectId == projectId
                                    && i.PlatformMade
                                    && i.ContentId != null)
                        .Select(i => i.ContentId!)
                        .Distinct()
                        .ToListAsync(ct)
                        .ConfigureAwait(false))
                    .ToHashSet(StringComparer.Ordinal);
            }
        }

        List<string> contentIds = own
            .Where(i => i.ContentId is not null && !heldContentIds.Contains(i.ContentId!))
            .Select(i => i.ContentId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (ProjectVersionItem item in own)
        {
            if (item.PlatformMade
                || (item.ContentId is not null && heldContentIds.Contains(item.ContentId)))
            {
                continue;
            }

            item.ErasedAt ??= now;
        }

        if (contentIds.Count > 0)
        {
            List<ProjectVersionItem> sharing = await _db.ProjectVersionItems
                .Where(i => i.ProjectId == projectId
                            && i.Kind == ProjectVersionItemKind.File
                            && i.ErasedAt == null
                            && i.ContentId != null
                            && contentIds.Contains(i.ContentId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (ProjectVersionItem item in sharing)
                item.ErasedAt = now;
        }

        // Mark first: from here no restore can use the bytes, even if the object delete below fails.
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (contentIds.Count == 0)
        {
            if (heldContentIds.Count > 0)
            {
                _logger.LogInformation(
                    "Project {ProjectId}: file {FileId} is kept in history under the output hold",
                    projectId, fileId);
            }

            return;
        }

        List<ProjectHistoryBlob> blobs = await _db.ProjectHistoryBlobs
            .Where(b => b.ProjectId == projectId && contentIds.Contains(b.ContentId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (blobs.Count == 0)
            return;

        string[] keys = blobs.Select(b => b.StorageKey).ToArray();
        int deleted = await _storage.DeleteManyAsync(projectId, keys, ct).ConfigureAwait(false);
        if (deleted < keys.Length)
        {
            // Keep the rows: they are the only map back to the surviving objects, and the GC retries
            // (the items are erased, so the blobs are unreferenced).
            throw new InvalidOperationException(
                $"Project {projectId}: {keys.Length - deleted} protected object(s) could not be deleted");
        }

        _db.ProjectHistoryBlobs.RemoveRange(blobs);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Project {ProjectId}: erased file {FileId} from history ({Blobs} object(s))",
            projectId, fileId, blobs.Count);
    }

    /// <summary>
    /// True when the file row is quarantined by either scanner. Then the platform's own removal wins
    /// over the output hold (see <see cref="EraseFileAsync"/>).
    /// </summary>
    private async Task<bool> IsQuarantinedAsync(Guid projectId, Guid fileId, CancellationToken ct) =>
        await _db.ProjectFiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(f => f.Id == fileId
                           && f.ProjectId == projectId
                           && (f.ModerationStatus == FileModerationStatus.Quarantined
                               || f.CsamStatus == CsamScanStatus.Quarantined), ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task DeleteProjectObjectsAsync(Guid projectId, CancellationToken ct)
    {
        List<string> keys = await _db.ProjectHistoryBlobs
            .AsNoTracking()
            .Where(b => b.ProjectId == projectId)
            .Select(b => b.StorageKey)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // The rows are not the whole truth: a failed or raced erase, or a row lost with an earlier
        // partial delete, can leave objects nothing names. List the whole prefix as well (QA low).
        if (_listing is not null)
        {
            IReadOnlyList<StoredObjectEntry> listed = await _listing
                .ListAsync(projectId, ProjectHistoryStorageKeys.HistoryFolder, ct)
                .ConfigureAwait(false);
            keys.AddRange(listed.Select(e => e.StorageKey));
        }

        keys = keys
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (keys.Count == 0)
            return;

        // The history/ prefix: the decorator passes these keys straight through.
        int deleted = await _storage.DeleteManyAsync(projectId, keys, ct).ConfigureAwait(false);
        if (deleted < keys.Count)
        {
            // The project row is about to go (its history rows cascade), so a surviving object would
            // be orphaned with nothing left to name it. Callers must keep the project and retry.
            throw new InvalidOperationException(
                $"Project {projectId}: {keys.Count - deleted} history object(s) could not be deleted");
        }
    }
}
