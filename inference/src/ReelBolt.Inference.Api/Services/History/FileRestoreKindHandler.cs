using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// Restores one file item (contract section 8 step 5): bytes from the protected blob (or the live
/// object when its ETag still matches) copied into the live key through the decorated storage, so the
/// bytes a restore overwrites are protected for the safety version; a deleted file is recreated with
/// its old id and path. Quota is checked on the net growth only, so a shrinking or equal rewrite always
/// passes.
/// </summary>
public sealed class FileRestoreKindHandler : IProjectRestoreKindHandler
{
    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly IPlanGateService? _planGate;
    private readonly IProjectFileAddedEffects? _fileAddedEffects;
    private readonly IPublishEndpoint? _publishEndpoint;
    private readonly TimeProvider _clock;
    private readonly ILogger<FileRestoreKindHandler>? _logger;

    public FileRestoreKindHandler(
        InferenceApiDbContext db,
        IFileStorageService storage,
        IPlanGateService? planGate = null,
        IProjectFileAddedEffects? fileAddedEffects = null,
        IPublishEndpoint? publishEndpoint = null,
        TimeProvider? clock = null,
        ILogger<FileRestoreKindHandler>? logger = null)
    {
        _db = db;
        _storage = storage;
        _planGate = planGate;
        _fileAddedEffects = fileAddedEffects;
        _publishEndpoint = publishEndpoint;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    public ProjectVersionItemKind Kind => ProjectVersionItemKind.File;

    public async Task<ProjectRestoreOutcome> ApplyAsync(
        ProjectRestoreWriteContext context, ProjectVersionItem item, CancellationToken ct)
    {
        Project project = context.Project;
        if (!Guid.TryParse(item.ItemKey, out Guid fileId))
            return Skip(item, ProjectHistoryRestoreReasons.ContentGone);

        ProjectFile? live = await _db.ProjectFiles
            .FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == project.Id, ct);

        if (live is not null && IsQuarantined(live))
            return Skip(item, ProjectHistoryRestoreReasons.Quarantined);

        // An avatar's reference carries a consent attestation; a restore may not swap its bytes underneath it.
        if (live is not null
            && await ReelBolt.Shared.Avatars.AvatarFileUse.FirstUseAsync(_db, project.Id, [live.Id], ct) is not null)
        {
            return Skip(item, ProjectHistoryRestoreReasons.FileInUseByAvatar);
        }

        // The same shared guard every history byte read passes: a held live output stays held, and a
        // version captured before the project's quarantine marker cannot be used as a restore source.
        if (await context.ContentGuard.RefusalAsync(
                project, context.Version.CreatedAt, live, ct,
                platformMade: item.PlatformMade, outputProducedAt: item.OutputProducedAt) is { } refusal)
        {
            return Skip(item, refusal);
        }

        string category = item.Category is "userFiles" or "agentFiles" ? item.Category : "userFiles";
        string? capturedPath = string.IsNullOrWhiteSpace(item.Name) ? null : item.Name;
        string normalizedPath = NormalizePath(capturedPath);

        // A recreated file may not take a path another live file holds; project scope removes the
        // version's absent files first, so here a hit is a real collision either way.
        bool pathTaken = await _db.ProjectFiles.AnyAsync(
            f => f.ProjectId == project.Id
                 && f.Id != fileId
                 && f.OriginalPath == normalizedPath
                 && (f.Category == "userFiles" || f.Category == "agentFiles"),
            ct);
        if (pathTaken)
            return Skip(item, ProjectHistoryRestoreReasons.PathTaken);

        string fileName = ProjectFilePath.GetFileName(normalizedPath);
        S3FileStorageService.UploadTarget target = S3FileStorageService.BuildUploadTarget(
            project.Id, fileId, fileName, category, normalizedPath);
        string targetKey = live?.StorageKey is { Length: > 0 } liveKey ? liveKey : target.StorageKey;

        long growth = item.SizeBytes - ReelBolt.Shared.Billing.ProjectFileQuota.CountedSize(live);
        if (growth > 0 && _planGate is not null
            && await _planGate.CheckUploadAsync(project.OrganizationId, growth, ct) is not null)
        {
            return Skip(item, ProjectHistoryRestoreReasons.StorageQuotaExceeded);
        }

        string? skip = await CopyBytesAsync(project.Id, context.Version.CreatedAt, item, live, targetKey, ct);
        if (skip is not null)
            return Skip(item, skip);

        ProjectFile file = live ?? new ProjectFile { Id = fileId, ProjectId = project.Id };
        if (live is null)
        {
            _db.ProjectFiles.Add(file);
            file.StorageKey = target.StorageKey;
            file.StorageBucket = _storage.BucketName;
            file.StoragePrefix = target.StoragePrefix;
            file.StorageFileName = target.StorageFileName;
            file.StorageMetadataJson = null;
        }

        file.OriginalPath = normalizedPath;
        file.OriginalFileName = fileName;
        file.DirectoryPath = target.DirectoryPath;
        file.Category = category;
        file.MimeType = string.IsNullOrWhiteSpace(item.MimeType)
            ? "application/octet-stream"
            : item.MimeType;
        file.SizeBytes = item.SizeBytes;
        // Restored bytes are not the exempt library sound any more.
        if (file.SourceKind == ReelBolt.Shared.Billing.ProjectFileQuota.SfxLibrarySourceKind) file.SourceKind = null;
        // New bytes at the key: neither column may vouch for the old ones any more.
        file.ContentSha256 = null;
        file.ContentSha256ETag = null;

        bool pending = !DirectUploadService.IsVideoOrAudio(file.MimeType);
        file.AgentSummary = null;
        file.SummaryStatus = pending ? SummaryStatus.Pending : SummaryStatus.Done;
        file.IndexingStatus = pending ? FileIndexingStatus.Pending : FileIndexingStatus.NotIndexed;
        file.IndexedAt = null;
        file.IndexingError = null;

        // A restore writes different bytes at the key, so the earlier verdicts no longer apply.
        file.ModerationStatus = FileModerationStatus.Unmoderated;
        file.CsamStatus = CsamScanStatus.Pending;
        file.CsamScannedAt = null;
        file.CsamScanClaimedAt = null;
        file.CsamClassification = null;
        file.CsamMatchType = null;
        file.CsamQuarantineKey = null;
        file.CsamQuarantinedAt = null;
        file.UploadedAt = _clock.GetUtcNow().UtcDateTime;
        // The captured production stamp goes back on the row so a restored output stays judgeable by the hold.
        file.OutputProducedAt = item.OutputProducedAt;

        await _db.SaveChangesAsync(ct);
        context.MovedAside.Remove(fileId);

        // The same post-write path every file-adding writer uses: CSAM scan, moderation, indexing,
        // summarization (or a media derivative request) and the activity feed.
        if (_fileAddedEffects is not null)
            await _fileAddedEffects.RunAsync(file, context.UserId ?? Guid.Empty, ct);

        return ProjectRestoreOutcome.Restored(item.Kind, item.ItemKey, file.OriginalPath);
    }

    public async Task<IReadOnlyList<ProjectRestoreOutcome>> RemoveAbsentAsync(
        ProjectRestoreWriteContext context, IReadOnlySet<string> presentKeys, CancellationToken ct)
    {
        Project project = context.Project;
        List<ProjectFile> live = await _db.ProjectFiles
            .Where(f => f.ProjectId == project.Id
                        && (f.Category == "userFiles" || f.Category == "agentFiles"))
            .ToListAsync(ct);

        List<ProjectRestoreOutcome> outcomes = new();
        List<Guid> removedIds = new();

        foreach (ProjectFile file in live)
        {
            if (presentKeys.Contains(file.Id.ToString("D")))
                continue;

            // Quarantine owns its own rows and bytes; the normal delete path refuses them too.
            if (IsQuarantined(file))
                continue;

            // A held output is never deleted by a restore: the hold owns its bytes and its row.
            if (await OutputGate.IsFileHeldAsync(_db, file, ct))
            {
                outcomes.Add(ProjectRestoreOutcome.Skipped(
                    ProjectVersionItemKind.File, file.Id.ToString("D"),
                    file.OriginalPath ?? file.OriginalFileName, ProjectHistoryRestoreReasons.OutputHeld));
                continue;
            }

            // The row removal is saved first, then the bytes go: a failed save leaves the row and its bytes intact,
            // and a failed byte delete leaves only orphan bytes (the history copy still protects them).
            string? storageKey = file.StorageKey;
            try
            {
                _db.ProjectFiles.Remove(file);
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Project {ProjectId}: removing file {FileId} failed; skipped", project.Id, file.Id);
                if (_db.Entry(file).State == EntityState.Deleted)
                    _db.Entry(file).State = EntityState.Unchanged;
                outcomes.Add(ProjectRestoreOutcome.Skipped(
                    ProjectVersionItemKind.File, file.Id.ToString("D"),
                    file.OriginalPath ?? file.OriginalFileName, ProjectHistoryRestoreReasons.RestoreFailed));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(storageKey))
            {
                try
                {
                    // Through the decorator: the current bytes are protected for the safety version.
                    await _storage.DeleteAsync(project.Id, storageKey, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogError(ex, "Project {ProjectId}: file {FileId} row removed but its bytes were not deleted; orphan bytes left", project.Id, file.Id);
                }
            }

            removedIds.Add(file.Id);
            outcomes.Add(ProjectRestoreOutcome.Removed(
                ProjectVersionItemKind.File, file.Id.ToString("D"), file.OriginalPath ?? file.OriginalFileName));
        }

        if (outcomes.Count == 0)
            return outcomes;

        if (_publishEndpoint is not null)
        {
            foreach (Guid fileId in removedIds)
            {
                await _publishEndpoint.Publish(new ProjectFileIndexingRequested
                {
                    ProjectId = project.Id,
                    FileId = fileId,
                    Operation = "Delete",
                    RequestedAt = _clock.GetUtcNow().UtcDateTime
                }, ct);
            }
        }

        return outcomes;
    }

    /// <summary>
    /// Project scope: parks every live file of the version that sits on a path another version file is
    /// going to take (swap or rename chain) at a unique temporary path, so each file then reaches its
    /// final path without PATH_TAKEN. Storage keys do not depend on the path, so only rows change.
    /// </summary>
    public async Task PrepareProjectScopeAsync(ProjectRestoreWriteContext context, CancellationToken ct)
    {
        Project project = context.Project;
        List<ProjectVersionItem> fileItems = context.VersionItems
            .Where(i => i.Kind == ProjectVersionItemKind.File && i.ErasedAt is null && Guid.TryParse(i.ItemKey, out _))
            .ToList();
        if (fileItems.Count < 2)
            return;

        Dictionary<Guid, string> targetPaths = fileItems.ToDictionary(
            i => Guid.Parse(i.ItemKey),
            i => NormalizePath(string.IsNullOrWhiteSpace(i.Name) ? null : i.Name));
        List<Guid> ids = targetPaths.Keys.ToList();

        List<ProjectFile> live = await _db.ProjectFiles
            .Where(f => f.ProjectId == project.Id
                        && ids.Contains(f.Id)
                        && (f.Category == "userFiles" || f.Category == "agentFiles"))
            .ToListAsync(ct);

        foreach (ProjectFile file in live)
        {
            if (IsQuarantined(file) || string.IsNullOrEmpty(file.OriginalPath))
                continue;

            // Parked only when another version file will land on this exact path.
            bool wanted = targetPaths.Any(t => t.Key != file.Id
                && string.Equals(t.Value, file.OriginalPath, StringComparison.Ordinal));
            if (!wanted)
                continue;

            context.MovedAside[file.Id] = new MovedAsideFile(file.OriginalPath, file.DirectoryPath);
            file.OriginalPath = $"{TempDirectory}/{file.Id:N}";
            file.DirectoryPath = TempDirectory;
        }

        if (context.MovedAside.Count > 0)
            await _db.SaveChangesAsync(ct);
    }

    /// <summary>Anything still parked (its item was skipped) goes back, or to a free numbered path.</summary>
    public async Task CompleteProjectScopeAsync(ProjectRestoreWriteContext context, CancellationToken ct)
    {
        if (context.MovedAside.Count == 0)
            return;

        Project project = context.Project;
        List<Guid> ids = context.MovedAside.Keys.ToList();
        List<ProjectFile> parked = await _db.ProjectFiles
            .Where(f => f.ProjectId == project.Id && ids.Contains(f.Id))
            .ToListAsync(ct);

        foreach (ProjectFile file in parked)
        {
            MovedAsideFile home = context.MovedAside[file.Id];
            string path = home.OriginalPath;
            string directory = home.DirectoryPath ?? string.Empty;
            for (int attempt = 1; await PathHeldAsync(project.Id, file.Id, path, ct); attempt++)
            {
                string name = ProjectFilePath.GetFileName(home.OriginalPath);
                string stem = Path.GetFileNameWithoutExtension(name);
                string ext = Path.GetExtension(name);
                string dir = home.OriginalPath.Length > name.Length ? home.OriginalPath[..(home.OriginalPath.Length - name.Length)] : string.Empty;
                path = $"{dir}{stem} ({attempt}){ext}";
            }

            file.OriginalPath = path;
            file.DirectoryPath = directory;
        }

        context.MovedAside.Clear();
        await _db.SaveChangesAsync(ct);
    }

    private const string TempDirectory = ".restore-tmp";

    private Task<bool> PathHeldAsync(Guid projectId, Guid fileId, string path, CancellationToken ct) =>
        _db.ProjectFiles.AnyAsync(
            f => f.ProjectId == projectId
                 && f.Id != fileId
                 && f.OriginalPath == path
                 && (f.Category == "userFiles" || f.Category == "agentFiles"),
            ct);

    /// <summary>
    /// Puts the captured bytes at <paramref name="targetKey"/>, or vouches that they are already
    /// there. Null on success; otherwise the skip reason.
    /// </summary>
    private async Task<string?> CopyBytesAsync(
        Guid projectId, DateTime versionCreatedAt, ProjectVersionItem item, ProjectFile? live, string targetKey, CancellationToken ct)
    {
        if (live is not null && !string.IsNullOrEmpty(item.StorageETag))
        {
            string? liveEtag = live.ContentSha256ETag;
            if (string.IsNullOrEmpty(liveEtag) && !string.IsNullOrWhiteSpace(live.StorageKey))
                liveEtag = await _storage.GetObjectETagAsync(projectId, live.StorageKey, ct);

            if (string.Equals(liveEtag, item.StorageETag, StringComparison.Ordinal))
                return null;
        }

        string? blobKey = item.ContentId is null
            ? null
            : await _db.ProjectHistoryBlobs
                .AsNoTracking()
                .Where(b => b.ProjectId == projectId && b.ContentId == item.ContentId)
                .Select(b => b.StorageKey)
                .FirstOrDefaultAsync(ct);

        if (blobKey is not null)
        {
            StoredObjectMetadata? metadata = await _storage.GetMetadataAsync(projectId, blobKey, ct);
            if (metadata is null)
            {
                _logger?.LogWarning(
                    "Project {ProjectId}: history blob for item {ItemKey} is missing; the file cannot be restored",
                    projectId, item.ItemKey);
                return ProjectHistoryRestoreReasons.ContentGone;
            }

            await _storage.CopyAsync(projectId, blobKey, targetKey, ct);
            return null;
        }

        if (ContentId.IsUnverified(item.ContentId))
        {
            // The contract only promises the bytes while the live file is still there and unchanged. No ETag
            // was captured, so "unchanged" is the best the row can prove: same size and not rewritten since
            // the version was taken (every overwrite path restamps UploadedAt). Otherwise the live bytes may
            // be newer than the version, so nothing is touched (the caller skips before any row write).
            if (live is null)
                return ProjectHistoryRestoreReasons.ContentGone;

            return live.SizeBytes == item.SizeBytes && live.UploadedAt <= versionCreatedAt
                ? null
                : ProjectHistoryRestoreReasons.Unverified;
        }

        return ProjectHistoryRestoreReasons.ContentGone;
    }

    private static bool IsQuarantined(ProjectFile file) =>
        file.ModerationStatus == FileModerationStatus.Quarantined
        || file.CsamStatus == CsamScanStatus.Quarantined;

    private static string NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "file" : ProjectFilePath.NormalizeRelativePath(path);

    private static ProjectRestoreOutcome Skip(ProjectVersionItem item, string reason) =>
        ProjectRestoreOutcome.Skipped(item.Kind, item.ItemKey, item.Name, reason);
}
