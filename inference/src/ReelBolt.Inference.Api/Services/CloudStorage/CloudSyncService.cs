using MassTransit;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.CloudStorage;

/// <summary>
/// What one sync did. <see cref="Status"/>: <c>synced</c>; <c>skipped</c> (a non-forced sync of a link
/// synced moments ago); <c>refused</c> (a limit the owner can fix, see <see cref="Code"/>); <c>failed</c>
/// (the provider or the connection failed; <see cref="ProjectCloudLink.LastError"/> says why);
/// <c>not_found</c> (the link no longer exists).
/// </summary>
public sealed record CloudSyncOutcome(
    string Status,
    int Written = 0,
    int Deleted = 0,
    int Unchanged = 0,
    int Skipped = 0,
    string? Code = null);

/// <summary>
/// The generic reconcile of a project with one linked cloud folder (docs/design/cloud-drive-sync.md
/// "Core services"). The shape of <see cref="GitHubRepositoryPullService"/>, with the lessons of the
/// GitHub sync failure built in:
///
/// <list type="bullet">
/// <item><b>It reconciles, it does not append</b>, and it touches only rows whose
/// <see cref="ProjectFile.SourceKind"/> is this provider's: an occupied path is skipped and counted.</item>
/// <item><b>Keyed on the provider's revision</b>: a file whose stored revision matches is not downloaded.</item>
/// <item><b>Progress is saved as it goes</b> (every <see cref="SaveEvery"/> written files, and on any
/// failure), so an interrupted sync keeps what it wrote and the next one resumes. Deleting vanished
/// files and stamping <see cref="ProjectCloudLink.LastSyncedAt"/> happen only after a complete listing
/// and a complete pass.</item>
/// <item><b>A zero-byte file is a file.</b></item>
/// <item><b>One unreadable file is skipped, not the sync failed</b>: a per-file NotFound/Forbidden is
/// counted and the pass continues. An auth, rate-limit, server or protocol failure fails the sync with a
/// plain-language <see cref="ProjectCloudLink.LastError"/>.</item>
/// </list>
///
/// Runs on <see cref="CloudSyncWorker"/>, never inside an HTTP request.
/// </summary>
public interface ICloudSyncService
{
    /// <summary>
    /// Syncs the link. <paramref name="force"/> false (the sweep) skips a link synced successfully less
    /// than <see cref="CloudSyncService.RecentWindow"/> ago; true (linking, a changed selection, "Sync now")
    /// always runs. Records the outcome on the link itself; never throws for a provider failure.
    /// </summary>
    Task<CloudSyncOutcome> SyncAsync(Guid linkId, bool force, CancellationToken ct);
}

/// <inheritdoc />
public sealed class CloudSyncService : ICloudSyncService
{
    /// <summary>Written files between two saves.</summary>
    public const int SaveEvery = 25;

    /// <summary>A non-forced sync of a link that succeeded this recently does nothing.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(1);

    /// <summary>Above this size a download is buffered in a temporary file rather than in memory.</summary>
    internal const int InMemoryBufferLimit = 8 * 1024 * 1024;

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly InferenceApiDbContext _db;
    private readonly ICloudStorageProviderRegistry _providers;
    private readonly ICloudStorageConnectionService _connections;
    private readonly IFileStorageService _storage;
    private readonly CloudStorageOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<CloudSyncService> _logger;
    private readonly IPlanGateService? _planGate;
    private readonly DesignDriftQueue? _driftQueue;
    private readonly IPublishEndpoint? _publishEndpoint;
    private readonly IContentModerationEnqueuer? _moderation;
    private readonly ICsmaHashMatcher? _csamMatcher;
    private readonly IUploadAnalysisSetting? _uploadAnalysis;

    public CloudSyncService(
        InferenceApiDbContext db,
        ICloudStorageProviderRegistry providers,
        ICloudStorageConnectionService connections,
        IFileStorageService storage,
        IOptions<CloudStorageOptions> options,
        ILogger<CloudSyncService> logger,
        TimeProvider? clock = null,
        IPlanGateService? planGate = null,
        DesignDriftQueue? driftQueue = null,
        IPublishEndpoint? publishEndpoint = null,
        IContentModerationEnqueuer? moderation = null,
        ICsmaHashMatcher? csamMatcher = null,
        IUploadAnalysisSetting? uploadAnalysis = null)
    {
        _csamMatcher = csamMatcher;
        _uploadAnalysis = uploadAnalysis;
        _planGate = planGate;
        _publishEndpoint = publishEndpoint;
        _db = db;
        _providers = providers;
        _connections = connections;
        _storage = storage;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _driftQueue = driftQueue;
        _moderation = moderation;
    }

    /// <summary>True for a file a sync never mirrors: OS/Office litter that is never project content.</summary>
    public static bool IsJunk(string relativePath)
    {
        string name = ProjectFilePath.GetFileName(relativePath);
        return string.Equals(name, ".DS_Store", StringComparison.Ordinal)
            || string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("~$", StringComparison.Ordinal);
    }

    public async Task<CloudSyncOutcome> SyncAsync(Guid linkId, bool force, CancellationToken ct)
    {
        ProjectCloudLink? link = await _db.ProjectCloudLinks.FirstOrDefaultAsync(l => l.Id == linkId, ct);
        if (link is null) return new CloudSyncOutcome("not_found");

        if (!force && link.LastError is null && link.LastSyncedAt is { } last && last > Now() - RecentWindow)
            return new CloudSyncOutcome("skipped");

        string provider = CloudStorageProviders.DisplayName(link.Provider);
        try
        {
            return await ReconcileAsync(link, provider, ct);
        }
        catch (CloudSyncRefusedException ex)
        {
            _logger.LogWarning("Cloud sync of link {LinkId} refused: {Code}", link.Id, ex.Code);
            await RecordAsync(link, ex.Message);
            return new CloudSyncOutcome("refused", Code: ex.Code);
        }
        catch (CloudStorageApiException ex)
        {
            _logger.LogWarning("Cloud sync of link {LinkId} ({Provider}) failed: {Kind} {Message}", link.Id, link.Provider, ex.Kind, ex.Message);
            await RecordAsync(link, InPlainWords(provider, ex.Kind));
            return new CloudSyncOutcome("failed", Code: ex.Kind.ToString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: keep what was written, so the next sync resumes from there.
            await SaveQuietlyAsync();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cloud sync of link {LinkId} failed unexpectedly", link.Id);
            await RecordAsync(link, "The sync stopped because of a problem on ReelBolt's side. Nothing was deleted. Sync again in a few minutes.");
            return new CloudSyncOutcome("failed", Code: "internal_error");
        }
    }

    private async Task<CloudSyncOutcome> ReconcileAsync(ProjectCloudLink link, string provider, CancellationToken ct)
    {
        if (link.ConnectionId is not { } connectionId)
            throw new CloudSyncRefusedException("connection_missing", DisconnectedMessage(provider));

        CloudStorageConnection? connection = await _db.CloudStorageConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (connection is null)
            throw new CloudSyncRefusedException("connection_missing", DisconnectedMessage(provider));

        ICloudStorageProvider? client = _providers.Get(link.Provider);
        if (client is not { IsConfigured: true })
            throw new CloudStorageApiException($"{link.Provider} is not configured.", CloudStorageErrorKind.NotConfigured);

        CloudLocation root = new(link.DriveId, link.FolderId);
        int maxFiles = Math.Max(1, _options.MaxFilesPerSync);

        CloudTreeListing listing = await _connections.CallAsync(
            connection, token => client.ListTreeAsync(token, root, maxFiles + 1, ct), ct);

        if (listing.Truncated || listing.Files.Count > maxFiles)
        {
            throw new CloudSyncRefusedException(
                "too_many_files",
                $"The linked folder holds more than {maxFiles} files, which is the most ReelBolt can sync. "
                + "Choose fewer files or a smaller folder. Nothing was changed.",
                listing.Files.Count,
                maxFiles);
        }

        IReadOnlyList<string> includePaths;
        try
        {
            includePaths = GitHubIncludePaths.Parse(link.IncludePathsJson);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogError(ex, "The stored file selection of cloud link {LinkId} is unreadable", link.Id);
            throw new CloudSyncRefusedException(
                "selection_unreadable",
                "The saved choice of files to sync could not be read, so nothing was changed. Choose the files again.");
        }

        (List<(string Path, CloudFileEntry Entry)> selected, int skipped) = Select(listing.Files, includePaths);

        if (selected.Count > maxFiles)
        {
            throw new CloudSyncRefusedException(
                "too_many_files",
                $"Too many files are chosen to sync: {selected.Count}, and the limit is {maxFiles}. "
                + "Choose fewer files or folders. Nothing was changed.",
                selected.Count,
                maxFiles);
        }

        long declaredBytes = selected.Sum(s => Math.Max(0, s.Entry.Size));
        if (declaredBytes > _options.MaxTotalBytes)
        {
            throw new CloudSyncRefusedException(
                "selection_too_large",
                $"The chosen files are too large to sync together: {GitHubRepositoryPullService.FormatBytes(declaredBytes)}, "
                + $"and the limit is {GitHubRepositoryPullService.FormatBytes(_options.MaxTotalBytes)}. "
                + "Choose fewer files or folders. Nothing was changed.",
                declaredBytes,
                _options.MaxTotalBytes);
        }

        string sourceKind = CloudStorageProviders.SourceKind(link.Provider);

        // EVERY file of the project: a path an upload, an agent, GitHub or another provider wrote is
        // occupied and is skipped rather than overwritten or duplicated.
        List<ProjectFile> existing = await _db.ProjectFiles.Where(f => f.ProjectId == link.ProjectId).ToListAsync(ct);
        Dictionary<string, ProjectFile> byPath = existing
            .GroupBy(f => f.OriginalPath ?? f.OriginalFileName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        int written = 0;
        int unchanged = 0;
        HashSet<string> changedPaths = new(StringComparer.Ordinal);

        // The plan's storage quota, file by file on the NET growth (docs/design/local-folder-sync-and-storage.md
        // Part 2): a file that does not fit is skipped and counted, the sync carries on — deletions and
        // unchanged files still apply — and it ends with the plain sentence on the link.
        SyncStorageQuota quota = new(_planGate, link.OrganizationId);
        List<ProjectFile> writtenMedia = [];
        List<ProjectFile> writtenFiles = [];

        foreach ((string path, CloudFileEntry entry) in selected)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Size > _options.MaxFileBytes)
            {
                skipped++;
                continue;
            }

            byPath.TryGetValue(path, out ProjectFile? current);

            // A quarantined row is evidence: never overwrite it, never delete its object, never reuse
            // its row. The sync reports it as skipped and carries on.
            if (current?.CsamStatus == CsamScanStatus.Quarantined)
            {
                skipped++;
                continue;
            }

            if (current is not null && !string.Equals(current.SourceKind, sourceKind, StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            if (current is not null && string.Equals(current.SourceRevision, StoredRevision(entry.Revision), StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            // Checked on the declared size before downloading (a native document's export declares 0,
            // so it is checked on its real size after), and again when the bytes are larger than declared.
            long oldSize = ReelBolt.Shared.Billing.ProjectFileQuota.CountedSize(current);
            if (entry.Size > 0 && !await quota.FitsAsync(entry.Size - oldSize, ct))
            {
                skipped++;
                continue;
            }

            await using Stream? content = await DownloadAsync(connection, client, root, entry, ct);
            if (content is null)
            {
                skipped++;
                continue;
            }

            if (content.Length > Math.Max(0, entry.Size) && !await quota.FitsAsync(content.Length - oldSize, ct))
            {
                skipped++;
                continue;
            }

            // An overwrite of a file whose bytes are kept is skipped.
            if (current is not null && await Outputs.OutputGate.IsFileHeldAsync(_db, current, ct))
            {
                skipped++;
                continue;
            }

            ProjectFile row = current ?? NewRow(link.ProjectId, path, sourceKind);
            long writtenBytes = content.Length;
            await WriteAsync(link, row, path, content, entry, sourceKind, ct);
            quota.Wrote(writtenBytes - oldSize);
            writtenFiles.Add(row);
            if (UploadDerivativeRequest.IsMediaMimeType(row.MimeType)) writtenMedia.Add(row);
            if (current is null)
            {
                _db.ProjectFiles.Add(row);
                byPath[path] = row;
            }

            written++;
            changedPaths.Add(path);
            if (written % SaveEvery == 0)
            {
                await _db.SaveChangesAsync(ct);
                quota.Saved();
            }
        }

        // Only after a complete listing and a complete pass: a file that left the folder or the selection
        // goes. A selected file that was skipped is still "present" and keeps its row. A quarantined row
        // is never deleted (it is reported as skipped instead), whatever the provider says.
        HashSet<string> present = selected.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        List<ProjectFile> vanished = byPath.Values
            .Where(f => string.Equals(f.SourceKind, sourceKind, StringComparison.Ordinal))
            .Where(f => !present.Contains(f.OriginalPath ?? f.OriginalFileName))
            .ToList();

        int quarantinedKept = vanished.Count(f => f.CsamStatus == CsamScanStatus.Quarantined);
        skipped += quarantinedKept;
        vanished = vanished.Where(f => f.CsamStatus != CsamScanStatus.Quarantined).ToList();
        // A file whose bytes are kept is not removed by a sync.
        HashSet<Guid> keptIds = await Outputs.OutputGate.HeldFileIdsAsync(_db, link.ProjectId, vanished, ct);
        if (keptIds.Count > 0)
        {
            skipped += keptIds.Count;
            vanished = vanished.Where(f => !keptIds.Contains(f.Id)).ToList();
        }
        // Belt and braces: an avatar never takes a synced file (the attach refuses any owned source_kind), but if a
        // row an avatar lists ever reached here it is kept, never deleted from under the avatar (NO ACTION link).
        HashSet<Guid> avatarIds = await ReelBolt.Shared.Avatars.AvatarFileUse.ReferencedFileIdsAsync(_db, [.. vanished.Select(f => f.Id)], ct);
        if (avatarIds.Count > 0)
        {
            _logger.LogWarning("Cloud sync of link {LinkId} kept {Count} file(s) an avatar lists as a reference", link.Id, avatarIds.Count);
            skipped += avatarIds.Count;
            vanished = vanished.Where(f => !avatarIds.Contains(f.Id)).ToList();
        }

        if (vanished.Count > 0)
        {
            List<string> keys = vanished.Select(v => v.StorageKey).Where(k => !string.IsNullOrEmpty(k)).ToList();
            // D10: pictures using a vanished photo keep their own copy before the objects go.
            await PictureSourceCopies.CopyBeforeDeleteAsync(_db, _storage, link.ProjectId, vanished, _logger, ct);
            if (keys.Count > 0) await _storage.DeleteManyAsync(link.ProjectId, keys, ct);
            _db.ProjectFiles.RemoveRange(vanished);
            foreach (ProjectFile removed in vanished)
                changedPaths.Add(removed.OriginalPath ?? removed.OriginalFileName);
        }

        DateTime now = Now();
        link.LastSyncedAt = now;
        link.LastSyncedFileCount = byPath.Values.Count(f => string.Equals(f.SourceKind, sourceKind, StringComparison.Ordinal)) - vanished.Count;
        // Null after a complete pass, unless files were left out because storage is full.
        link.LastError = quota.Message;
        link.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        // Synced footage is footage like an upload: once its row is committed, each new or rewritten
        // video/audio file asks for its derivative set and stored probe (P2-LAZY-04), best effort. But
        // only after the CSAM verdict (docs/csam.md): the row is scanned here first (this runs on the
        // sync worker, not in a request), a quarantine publishes nothing, and a row left unsettled
        // (no matcher, scan failed to persist) waits for CsamScanSweeper, which requests its derivatives
        // after its own verdict.
        // Images are scanned here too: they are what hash matching is for, and a Pending image would otherwise be
        // open to the picture editor and the engine until the sweeper reaches it.
        if (_csamMatcher is not null)
        {
            foreach (ProjectFile scannable in writtenFiles.Where(f => !vanished.Contains(f))
                         .Where(f => f.CsamStatus is CsamScanStatus.Pending or CsamScanStatus.Scanning)
                         .Where(f => UploadDerivativeRequest.IsMediaMimeType(f.MimeType)
                                     || (f.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                try
                {
                    await _csamMatcher.ScanAsync(scannable, actorUserId: null, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // The matcher fails open itself; this is the belt. The row stays unsettled for the sweeper.
                    _logger.LogWarning("Cloud sync could not scan project file {FileId} ({ExceptionType}); the sweeper will", scannable.Id, ex.GetType().Name);
                }
            }
        }

        if (_publishEndpoint is not null)
        {
            foreach (ProjectFile media in writtenMedia.Where(m => !vanished.Contains(m)))
            {
                if (CsamScanGate.AllowsDerivatives(media))
                    await UploadDerivativeRequest.PublishAsync(_publishEndpoint, _storage, _uploadAnalysis, media, media.MimeType!, _logger, ct);
            }
        }

        // The commit is done: every written row whose CSAM verdict has settled and is not a quarantine
        // is offered to ingest moderation. A Pending row waits for the CSAM sweeper; the moderation
        // sweep offers it again after the verdict.
        if (_moderation is not null)
        {
            foreach (ProjectFile writtenFile in writtenFiles.Where(f => !vanished.Contains(f))
                         .Where(CsamScanGate.AllowsModeration))
                _moderation.TryQueue(writtenFile.Id);
        }

        if (written > 0 || vanished.Count > 0)
        {
            // The upsert is committed; ask for a drift check off this request. Never throws into the sync.
            DriftHooks.Notify(_driftQueue, link.ProjectId, DesignDriftTrigger.CloudSync, changedPaths, _logger, link.OrganizationId);
        }

        _logger.LogInformation(
            "Cloud sync of {Provider} link {LinkId} into project {ProjectId}: {Written} written, {Deleted} deleted, {Unchanged} unchanged, {Skipped} skipped, {Unsupported} unsupported",
            link.Provider, link.Id, link.ProjectId, written, vanished.Count, unchanged, skipped, listing.Unsupported);

        return new CloudSyncOutcome("synced", written, vanished.Count, unchanged, skipped);
    }

    /// <summary>
    /// The files of a sync: inside the selection (matched on the folder-relative path), not OS/Office
    /// litter, with a path the project can store. A path that normalizes to one already taken (two
    /// same-named files in one Drive folder) keeps the entry with the smallest item id and skips the rest.
    /// A file left out because it was not chosen is not "skipped"; litter and unusable paths are.
    /// </summary>
    internal static (List<(string Path, CloudFileEntry Entry)> Selected, int Skipped) Select(
        IReadOnlyList<CloudFileEntry> files,
        IReadOnlyList<string> includePaths)
    {
        Dictionary<string, CloudFileEntry> byPath = new(StringComparer.Ordinal);
        int skipped = 0;

        foreach (CloudFileEntry entry in files)
        {
            // A backslash is never turned into a folder separator: on the provider it is part of a name,
            // so a path carrying one cannot be mirrored faithfully and is skipped instead.
            if (entry.RelativePath.Contains('\\'))
            {
                skipped++;
                continue;
            }

            string path;
            try
            {
                path = ProjectFilePath.NormalizeRelativePath(entry.RelativePath);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                skipped++;
                continue;
            }

            if (path.Any(char.IsControl))
            {
                skipped++;
                continue;
            }

            if (!GitHubIncludePaths.Matches(includePaths, path)) continue;
            if (IsJunk(path)) continue;

            if (byPath.TryGetValue(path, out CloudFileEntry? taken))
            {
                skipped++;
                if (string.CompareOrdinal(entry.ItemId, taken.ItemId) < 0) byPath[path] = entry;
                continue;
            }

            byPath[path] = entry;
        }

        return (byPath.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value)).ToList(), skipped);
    }

    /// <summary>
    /// Downloads one file into a seekable buffer, enforcing <see cref="CloudStorageOptions.MaxFileBytes"/>
    /// while reading (a provider's declared size is a hint, the bytes are the fact). Null when the file is
    /// skipped: gone or forbidden since the listing, over the limit, or unreadable mid-stream.
    /// </summary>
    private async Task<Stream?> DownloadAsync(
        CloudStorageConnection connection,
        ICloudStorageProvider client,
        CloudLocation root,
        CloudFileEntry entry,
        CancellationToken ct)
    {
        CloudDownload download;
        try
        {
            download = await _connections.CallAsync(connection, token => client.OpenDownloadAsync(token, root, entry, ct), ct);
        }
        catch (CloudStorageApiException ex) when (ex.Kind is CloudStorageErrorKind.NotFound or CloudStorageErrorKind.Forbidden)
        {
            _logger.LogInformation("Skipped {Provider} item {ItemId}: {Kind}", connection.Provider, entry.ItemId, ex.Kind);
            return null;
        }

        await using (download)
        {
            if (download.Length is { } declared && declared > _options.MaxFileBytes) return null;
            try
            {
                return await CappedCopyAsync(download.Content, _options.MaxFileBytes, ct);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                _logger.LogWarning("Reading {Provider} item {ItemId} failed mid-stream: {Message}", connection.Provider, entry.ItemId, ex.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// Copies at most <paramref name="maxBytes"/> bytes into a seekable stream positioned at 0 (memory,
    /// spilling to a delete-on-close temporary file past <see cref="InMemoryBufferLimit"/>). Null when the
    /// source holds more than the limit. A zero-byte source yields an empty stream, never null.
    /// </summary>
    internal static async Task<Stream?> CappedCopyAsync(Stream source, long maxBytes, CancellationToken ct)
    {
        Stream buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        long total = 0;
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(chunk, ct);
                if (read == 0) break;
                total += read;
                if (total > maxBytes)
                {
                    await buffer.DisposeAsync();
                    return null;
                }

                if (buffer is MemoryStream memory && total > InMemoryBufferLimit)
                {
                    FileStream file = new(
                        Path.Combine(Path.GetTempPath(), $"reelbolt-cloudsync-{Guid.NewGuid():N}.tmp"),
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        81920,
                        FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                    memory.Position = 0;
                    await memory.CopyToAsync(file, ct);
                    await memory.DisposeAsync();
                    buffer = file;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            }

            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            await buffer.DisposeAsync();
            throw;
        }
    }

    private static ProjectFile NewRow(Guid projectId, string path, string sourceKind) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        OriginalFileName = ProjectFilePath.GetFileName(path),
        OriginalPath = path,
        DirectoryPath = ProjectFilePath.GetDirectoryPath(path),
        Category = "userFiles",
        SourceKind = sourceKind,
        UploadedAt = DateTime.UtcNow
    };

    /// <summary>
    /// Stores one file and fills in the row, as the GitHub pull does: no summarizer and no embedder, so a
    /// background sync never runs up an LLM bill nobody asked for.
    /// </summary>
    private async Task WriteAsync(
        ProjectCloudLink link,
        ProjectFile row,
        string path,
        Stream content,
        CloudFileEntry entry,
        string sourceKind,
        CancellationToken ct)
    {
        string contentType = ContentTypes.TryGetContentType(path, out string? detected)
            ? detected
            : entry.ExportMimeType ?? entry.MimeType ?? "application/octet-stream";
        if (contentType.Length > 200) contentType = "application/octet-stream";
        // Issue 8: the extension map calls every .ts a transport stream; a TypeScript source is text.
        contentType = await TransportStreamMimeSniffer.ResolveAsync(contentType, path, content, ct) ?? contentType;

        Dictionary<string, string> metadata = new()
        {
            ["project-id"] = link.ProjectId.ToString(),
            ["org-id"] = link.OrganizationId.ToString(),
            ["project-file-id"] = row.Id.ToString(),
            ["cloud-provider"] = CloudStorageProviders.Slug(link.Provider),
            ["cloud-link"] = link.Id.ToString()
        };

        long size = content.Length;
        StoredFileObject stored = await _storage.UploadAsync(
            link.ProjectId,
            row.Id,
            content,
            ProjectFilePath.GetFileName(path),
            contentType,
            metadata,
            ct,
            category: "userFiles",
            originalPath: path);

        if (!string.IsNullOrEmpty(row.StorageKey) &&
            !string.Equals(row.StorageKey, stored.StorageKey, StringComparison.Ordinal))
        {
            await _storage.DeleteAsync(link.ProjectId, row.StorageKey, ct);
        }

        row.OriginalFileName = stored.OriginalFileName;
        row.OriginalPath = stored.OriginalPath;
        row.DirectoryPath = stored.DirectoryPath;
        row.Category = "userFiles";
        // New bytes are a new CSAM question; the sweeper re-scans before moderation may read them.
        row.CsamStatus = CsamScanStatus.Pending;
        row.CsamScannedAt = null;
        row.StorageKey = stored.StorageKey;
        row.StorageFileName = stored.StorageFileName;
        row.StorageBucket = stored.BucketName;
        row.StoragePrefix = stored.StoragePrefix;
        row.StorageMetadataJson = stored.StorageMetadataJson;
        row.MimeType = contentType;
        row.SizeBytes = size;
        row.SourceKind = sourceKind;
        row.SourceRevision = StoredRevision(entry.Revision);
        // A rewritten file's old summary and vectors describe bytes that no longer exist.
        row.AgentSummary = null;
        row.SummaryStatus = SummaryStatus.Done;
        row.IndexingStatus = FileIndexingStatus.NotIndexed;
        row.IndexedAt = null;
        row.IndexingError = null;
        row.UploadedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// The revision as stored in <c>project_files.source_revision</c> (100 characters): verbatim when it
    /// fits, otherwise a SHA-256 of it, so a long etag still compares equal from one sync to the next
    /// instead of being truncated into something that never matches.
    /// </summary>
    internal static string StoredRevision(string revision) =>
        revision.Length <= 100
            ? revision
            : "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(revision))).ToLowerInvariant();

    /// <summary>A provider failure, in words the project's owner can act on.</summary>
    internal static string InPlainWords(string provider, CloudStorageErrorKind kind) => kind switch
    {
        CloudStorageErrorKind.Unauthorized =>
            $"{provider} no longer accepts ReelBolt's access to the account this folder was linked with. "
            + "Reconnect the account in Settings, then sync again.",
        CloudStorageErrorKind.Forbidden =>
            $"The account this folder was linked with is no longer allowed to open it on {provider}. "
            + "Ask the folder's owner to share it with that account again, or link another folder.",
        CloudStorageErrorKind.NotFound =>
            $"The linked folder could not be found on {provider}. It may have been deleted, or it is no longer "
            + "shared with the account it was linked with. Link another folder.",
        CloudStorageErrorKind.RateLimited =>
            $"{provider} is limiting how many requests ReelBolt may make right now. Wait a few minutes, then sync again.",
        CloudStorageErrorKind.ServerError =>
            $"{provider} had a problem answering. This is usually temporary: sync again in a few minutes.",
        CloudStorageErrorKind.NotConfigured =>
            $"{provider} syncing is not set up on this server. Ask your administrator to finish the setup.",
        _ =>
            $"{provider} answered in a way ReelBolt did not expect. Sync again in a few minutes; if it keeps "
            + "happening, unlink the folder and link it again."
    };

    private static string DisconnectedMessage(string provider) =>
        $"The {provider} account this folder was linked with has been disconnected. Link the folder again.";

    /// <summary>Writes the failure on the link and saves it together with whatever was written so far.</summary>
    private async Task RecordAsync(ProjectCloudLink link, string message)
    {
        link.LastError = message.Length > 2000 ? message[..2000] : message;
        link.UpdatedAt = Now();
        await SaveQuietlyAsync();
    }

    /// <summary>
    /// Saves without the caller's token (a stopping token may already be cancelled, and progress must
    /// still land). A save that fails is logged, never thrown over the original failure.
    /// </summary>
    private async Task SaveQuietlyAsync()
    {
        try
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not save the outcome of a cloud sync");
        }
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}

/// <summary>
/// A sync refused before it changed anything, for a reason the owner can act on (too many files, too
/// much data, an unreadable selection, a disconnected account). <see cref="Exception.Message"/> is the
/// plain-language sentence recorded as the link's <c>LastError</c>.
/// </summary>
public sealed class CloudSyncRefusedException : Exception
{
    public CloudSyncRefusedException(string code, string message, long? actual = null, long? limit = null)
        : base(message)
    {
        Code = code;
        Actual = actual;
        Limit = limit;
    }

    public string Code { get; }

    public long? Actual { get; }

    public long? Limit { get; }
}
