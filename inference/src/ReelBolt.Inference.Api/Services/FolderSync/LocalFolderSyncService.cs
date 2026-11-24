using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Inference.Api.Services.SfxLibrary;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.FolderSync;

/// <summary>The outcome of creating a link: the link, or a refusal code with a plain message.</summary>
public sealed record FolderLinkCreateResult(ProjectLocalFolderLink? Link, int StatusCode, string? Error, string? Message);

/// <summary>
/// The server half of computer folder sync (docs/design/local-folder-sync-and-storage.md, Part 1): links,
/// the manifest reconciliation with its mass-delete guard, and deleting a link's files. Authorization is
/// the caller's job (<see cref="DeviceProjectAccess"/> for the device, the project access check for the
/// dashboard); every method here assumes it was done.
/// </summary>
public sealed class LocalFolderSyncService(
    InferenceApiDbContext db,
    IFileStorageService storage,
    IOrganizationEntitlements entitlements,
    ILogger<LocalFolderSyncService> logger,
    TimeProvider? clock = null,
    DesignDriftQueue? driftQueue = null)
{
    public const string SourceKind = "local_folder";

    /// <summary>A link holding at least this many files is guarded against losing more than half of them at once.</summary>
    public const int GuardMinimumFiles = 20;

    public const int MaxDeletePaths = 1_000;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // ------------------------------------------------------------------ links

    /// <summary>
    /// Creates a link for <paramref name="device"/> in <paramref name="project"/>. The target folder is the
    /// requested one or the local folder's own name; it must not equal, contain or lie inside another
    /// link's folder in the project (<c>409 target_folder_taken</c>).
    /// </summary>
    public async Task<FolderLinkCreateResult> CreateLinkAsync(
        DeviceContext device,
        Project project,
        string? requestedTargetFolder,
        string? displayPath,
        CancellationToken ct)
    {
        string display = (displayPath ?? string.Empty).Trim();
        if (display.Length > FolderSyncPaths.MaxDisplayPathLength || display.Any(char.IsControl))
            return Refused(400, "invalid_display_path", $"The folder's name on the computer is at most {FolderSyncPaths.MaxDisplayPathLength} characters.");

        string? target = FolderSyncPaths.ResolveTargetFolder(requestedTargetFolder, display, out string? reason);
        if (target is null)
            return Refused(400, "invalid_target_folder", reason ?? "That folder name cannot be used in a project.");

        List<string> taken = await db.ProjectLocalFolderLinks
            .Where(l => l.ProjectId == project.Id)
            .Select(l => l.TargetFolder)
            .ToListAsync(ct);
        if (taken.Any(t => FolderSyncPaths.Overlaps(t, target)))
            return Refused(409, "target_folder_taken", $"The project already syncs a computer folder into \"{target}\". Choose another folder name.");

        DateTime now = Now();
        ProjectLocalFolderLink link = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            DeviceId = device.DeviceId,
            TargetFolder = target,
            DisplayPath = display,
            CreatedByUserId = device.PairingUserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ProjectLocalFolderLinks.Add(link);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two links for the same folder at once: the unique index caught the second.
            db.Entry(link).State = EntityState.Detached;
            return Refused(409, "target_folder_taken", $"The project already syncs a computer folder into \"{target}\". Choose another folder name.");
        }

        logger.LogInformation("Device {DeviceId} linked a folder into project {ProjectId} as {LinkId}", device.DeviceId, project.Id, link.Id);
        return new FolderLinkCreateResult(link, 201, null, null);
    }

    /// <summary>This device's link, or null (another device's link is answered like a missing one).</summary>
    public Task<ProjectLocalFolderLink?> FindDeviceLinkAsync(DeviceContext device, Guid linkId, CancellationToken ct) =>
        db.ProjectLocalFolderLinks.FirstOrDefaultAsync(
            l => l.Id == linkId && l.DeviceId == device.DeviceId && l.OrganizationId == device.OrganizationId, ct);

    /// <summary>
    /// Removes the link. Its files stay in the project and become ordinary files (<c>source_link_id</c> is
    /// nulled — by the database's ON DELETE SET NULL, and explicitly here so every provider agrees).
    /// </summary>
    public async Task UnlinkAsync(ProjectLocalFolderLink link, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            await db.ProjectFiles
                .Where(f => f.SourceLinkId == link.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.SourceLinkId, (Guid?)null), ct);
        }
        else
        {
            foreach (ProjectFile file in await db.ProjectFiles.Where(f => f.SourceLinkId == link.Id).ToListAsync(ct))
                file.SourceLinkId = null;
        }

        db.ProjectLocalFolderLinks.Remove(link);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Folder link {LinkId} of project {ProjectId} removed; its files stay", link.Id, link.ProjectId);
    }

    public void Pause(ProjectLocalFolderLink link, string reason, string? message = null)
    {
        link.PausedAt ??= Now();
        link.PauseReason = reason;
        if (message is not null) link.LastError = message;
        link.UpdatedAt = Now();
    }

    public void Resume(ProjectLocalFolderLink link)
    {
        link.PausedAt = null;
        link.PauseReason = null;
        link.LastError = null;
        link.UpdatedAt = Now();
    }

    // ------------------------------------------------------------------ reconciliation

    /// <summary>
    /// Diffs a complete manifest against the link's rows (the catch-up after the app starts or reconnects,
    /// and its 15-minute safety pass). <c>upload</c> = paths missing in the project or at another hash;
    /// <c>delete</c> = link rows the manifest no longer has. Nothing is written or deleted here: the desktop
    /// app uploads and deletes through the ordinary endpoints. <b>Mass-delete guard:</b> a manifest that is
    /// empty while the link holds files, or that would delete more than half of a link holding at least
    /// <see cref="GuardMinimumFiles"/> files, returns no deletions and pauses the link with a plain
    /// explanation, unless the app confirmed the deletions (<paramref name="confirmLargeDelete"/>). A guard
    /// pause lifts by itself once the folder looks whole again.
    /// </summary>
    public async Task<ManifestResultResponse> ReconcileAsync(
        ProjectLocalFolderLink link,
        DeviceContext device,
        IReadOnlyList<ManifestFileEntry> files,
        bool confirmLargeDelete,
        CancellationToken ct)
    {
        List<ProjectFile> projectFiles = await db.ProjectFiles.Where(f => f.ProjectId == link.ProjectId).ToListAsync(ct);
        Dictionary<string, ProjectFile> byPath = projectFiles
            .GroupBy(f => f.OriginalPath ?? f.OriginalFileName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.SourceLinkId == link.Id ? 0 : 1).First(), StringComparer.Ordinal);
        List<ProjectFile> linkRows = projectFiles.Where(f => f.SourceLinkId == link.Id).ToList();

        List<string> upload = [];
        List<ManifestSkippedPath> skipped = [];
        HashSet<string> present = new(StringComparer.Ordinal);

        foreach (ManifestFileEntry entry in files)
        {
            if (!FolderSyncPaths.IsValidRelativePath(entry.Path) || !FolderSyncPaths.IsSha256Hex(entry.Sha256) || entry.Size < 0)
            {
                skipped.Add(new ManifestSkippedPath(Truncate(entry.Path ?? string.Empty, FolderSyncPaths.MaxPathLength), "invalid_path"));
                continue;
            }

            string path = entry.Path!;
            if (!present.Add(path))
            {
                skipped.Add(new ManifestSkippedPath(path, "duplicate_path"));
                continue;
            }

            string stored = FolderSyncPaths.StoredPath(link.TargetFolder, path);
            if (byPath.TryGetValue(stored, out ProjectFile? row))
            {
                if (row.SourceLinkId != link.Id)
                {
                    skipped.Add(new ManifestSkippedPath(path, "occupied"));
                    continue;
                }

                if (string.Equals(row.SourceRevision, entry.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            }

            upload.Add(path);
        }

        List<string> delete = linkRows
            .Select(r => FolderSyncPaths.RelativeTo(link.TargetFolder, r.OriginalPath ?? r.OriginalFileName))
            .Where(p => p is not null && !present.Contains(p))
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        StorageUsageResponse usage = await StorageUsageAsync(link.OrganizationId, ct);
        DateTime now = Now();

        if (link.PausedAt is not null && link.PauseReason != ProjectLocalFolderLink.PauseReasonGuard)
        {
            // Paused by a person: the computer's view is not acted on until it is resumed.
            return new ManifestResultResponse([], [], skipped, true, link.PauseReason ?? ProjectLocalFolderLink.PauseReasonUser, link.LastError, usage);
        }

        bool guardTrips = linkRows.Count > 0 &&
            (files.Count == 0 || (linkRows.Count >= GuardMinimumFiles && delete.Count * 2 > linkRows.Count));

        if (guardTrips && !confirmLargeDelete)
        {
            string message =
                $"The folder on {device.DeviceName} looks empty or mostly gone — sync is paused so nothing is deleted. "
                + "If you really removed those files, resume it from the desktop app.";
            Pause(link, ProjectLocalFolderLink.PauseReasonGuard, message);
            link.FileCount = linkRows.Count;
            await db.SaveChangesAsync(ct);
            logger.LogWarning(
                "Folder link {LinkId}: the manifest would delete {Delete} of {Total} files; paused by the mass-delete guard",
                link.Id, delete.Count, linkRows.Count);
            return new ManifestResultResponse([], [], skipped, true, ProjectLocalFolderLink.PauseReasonGuard, link.LastError, usage);
        }

        if (link.PauseReason == ProjectLocalFolderLink.PauseReasonGuard)
        {
            // Confirmed from the computer, or the folder looks whole again.
            Resume(link);
        }

        link.LastReconciledAt = now;
        link.FileCount = linkRows.Count;
        link.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        return new ManifestResultResponse(upload, delete, skipped, false, null, link.LastError, usage);
    }

    // ------------------------------------------------------------------ writes

    /// <summary>The row this link wrote at a folder-relative path, if any.</summary>
    public Task<ProjectFile?> FindLinkRowAsync(ProjectLocalFolderLink link, string relativePath, CancellationToken ct)
    {
        string stored = FolderSyncPaths.StoredPath(link.TargetFolder, relativePath);
        return db.ProjectFiles.FirstOrDefaultAsync(f => f.ProjectId == link.ProjectId && f.SourceLinkId == link.Id && f.OriginalPath == stored, ct);
    }

    /// <summary>True when a row from another source (an upload, an agent, GitHub, a cloud drive, another link) holds the path.</summary>
    public Task<bool> IsOccupiedAsync(ProjectLocalFolderLink link, string relativePath, CancellationToken ct)
    {
        string stored = FolderSyncPaths.StoredPath(link.TargetFolder, relativePath);
        return db.ProjectFiles.AnyAsync(
            f => f.ProjectId == link.ProjectId && f.OriginalPath == stored && (f.SourceLinkId == null || f.SourceLinkId != link.Id), ct);
    }

    /// <summary>
    /// After a completed upload: drops any other row of this link at the same path (two uploads of a new
    /// path that raced) and records the change on the link.
    /// </summary>
    public async Task RecordUploadAsync(ProjectLocalFolderLink link, ProjectFile written, CancellationToken ct)
    {
        // A quarantined row at the same path is evidence: never let it into the duplicate set, which
        // is passed to RemoveRowsAsync and PictureSourceCopies. The new row lives beside it.
        List<ProjectFile> duplicates = await db.ProjectFiles
            .Where(f => f.ProjectId == link.ProjectId && f.SourceLinkId == link.Id
                        && f.OriginalPath == written.OriginalPath && f.Id != written.Id
                        && f.CsamStatus != CsamScanStatus.Quarantined)
            .ToListAsync(ct);
        await RemoveRowsAsync(link, duplicates, ct);
        await TouchAsync(link, ct);
        string[]? uploadedPaths = string.IsNullOrEmpty(written.OriginalPath) ? null : [written.OriginalPath];
        DriftHooks.Notify(driftQueue, link.ProjectId, DesignDriftTrigger.FolderSync, uploadedPaths, logger, link.OrganizationId);
    }

    /// <summary>Deletes this link's rows (and stored objects) at the given folder-relative paths. Other rows are never touched.</summary>
    public async Task<int> DeleteFilesAsync(ProjectLocalFolderLink link, IReadOnlyCollection<string> relativePaths, CancellationToken ct) =>
        (await DeleteFilesWithSkippedAsync(link, relativePaths, ct)).Deleted;

    /// <summary>
    /// As <see cref="DeleteFilesAsync"/>, but a quarantined row is evidence: it is skipped, counted
    /// and reported, never deleted from storage or from the database.
    /// </summary>
    public async Task<FolderDeleteOutcome> DeleteFilesWithSkippedAsync(
        ProjectLocalFolderLink link, IReadOnlyCollection<string> relativePaths, CancellationToken ct)
    {
        HashSet<string> stored = relativePaths
            .Where(FolderSyncPaths.IsValidRelativePath)
            .Select(p => FolderSyncPaths.StoredPath(link.TargetFolder, p))
            .ToHashSet(StringComparer.Ordinal);
        if (stored.Count == 0) return new FolderDeleteOutcome(0, []);

        List<ProjectFile> rows = await db.ProjectFiles
            .Where(f => f.ProjectId == link.ProjectId && f.SourceLinkId == link.Id && f.OriginalPath != null && stored.Contains(f.OriginalPath))
            .ToListAsync(ct);

        List<string> skipped = rows
            .Where(f => f.CsamStatus == CsamScanStatus.Quarantined)
            .Select(f => f.OriginalPath ?? f.OriginalFileName)
            .ToList();
        rows = rows.Where(f => f.CsamStatus != CsamScanStatus.Quarantined).ToList();

        rows = await RemoveRowsAsync(link, rows, ct);
        await TouchAsync(link, ct);
        if (rows.Count > 0)
        {
            // Only a delete that removed something changed the project; the paths are the removed rows'.
            string[] deletedPaths = rows.Select(r => r.OriginalPath ?? r.OriginalFileName).ToArray();
            DriftHooks.Notify(driftQueue, link.ProjectId, DesignDriftTrigger.FolderSync, deletedPaths, logger, link.OrganizationId);
        }

        return new FolderDeleteOutcome(rows.Count, skipped);
    }

    /// <summary>Removes what may go and answers with the rows actually removed.</summary>
    private async Task<List<ProjectFile>> RemoveRowsAsync(ProjectLocalFolderLink link, List<ProjectFile> rows, CancellationToken ct)
    {
        // Belt and braces: no caller may pass a quarantined row here. Its object and audit are
        // evidence under legal hold, and PictureSourceCopies/DeleteManyAsync would destroy them.
        List<ProjectFile> quarantined = rows.Where(r => r.CsamStatus == CsamScanStatus.Quarantined).ToList();
        if (quarantined.Count != 0)
        {
            logger?.LogWarning(
                "Folder sync kept {Count} quarantined row(s) in project {ProjectId} out of a delete",
                quarantined.Count, link.ProjectId);
            rows = rows.Where(r => r.CsamStatus != CsamScanStatus.Quarantined).ToList();
        }
        // A file whose bytes are kept is not removed by a sync.
        HashSet<Guid> keptIds = await Outputs.OutputGate.HeldFileIdsAsync(db, link.ProjectId, rows, ct);
        if (keptIds.Count > 0) rows = rows.Where(r => !keptIds.Contains(r.Id)).ToList();
        // Belt and braces: an avatar never takes a synced file (the attach refuses a local_folder row), but a row an
        // avatar lists is kept rather than deleted from under it (the reference link is ON DELETE NO ACTION).
        HashSet<Guid> avatarIds = await ReelBolt.Shared.Avatars.AvatarFileUse.ReferencedFileIdsAsync(db, [.. rows.Select(r => r.Id)], ct);
        if (avatarIds.Count != 0)
        {
            logger?.LogWarning(
                "Folder sync kept {Count} row(s) in project {ProjectId} that an avatar lists as a reference",
                avatarIds.Count, link.ProjectId);
            rows = rows.Where(r => !avatarIds.Contains(r.Id)).ToList();
        }
        if (rows.Count == 0) return rows;
        List<string> keys = rows.Select(r => r.StorageKey).Where(k => !string.IsNullOrEmpty(k)).ToList();
        // D10: pictures using one of these photos keep their own copy before the objects go.
        await PictureSourceCopies.CopyBeforeDeleteAsync(db, storage, link.ProjectId, rows, logger, ct);
        if (keys.Count > 0) await storage.DeleteManyAsync(link.ProjectId, keys, ct);
        db.ProjectFiles.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows;
    }

    private async Task TouchAsync(ProjectLocalFolderLink link, CancellationToken ct)
    {
        DateTime now = Now();
        link.FileCount = await db.ProjectFiles.CountAsync(f => f.SourceLinkId == link.Id, ct);
        link.LastChangeAt = now;
        link.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ storage

    /// <summary>The workspace's storage use and limit (<c>null</c> = unlimited), measured as the plan gate measures it.</summary>
    public async Task<StorageUsageResponse> StorageUsageAsync(Guid organizationId, CancellationToken ct)
    {
        long used = await db.ProjectFiles
            .Where(f => db.Projects.Any(p => p.Id == f.ProjectId && p.OrganizationId == organizationId))
            .Where(f => f.SourceKind != SfxLibraryProjectAdder.SourceKind)
            .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;
        OrganizationEntitlementSet set = await entitlements.GetAsync(organizationId, ct);
        long? limit = set.StorageGb == OrganizationEntitlementSet.UnlimitedInt ? null : (long)set.StorageGb * 1024L * 1024L * 1024L;
        return new StorageUsageResponse(used, limit);
    }

    private static FolderLinkCreateResult Refused(int status, string error, string message) => new(null, status, error, message);

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}
