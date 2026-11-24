using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared;
using ReelBolt.Shared.Archives;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Archives;

/// <summary>Fixed failure codes an unpack can end with (the list the project API returns).</summary>
public static class ArchiveUnpackFailure
{
    public const string SourceGone = "SOURCE_GONE";
    public const string UnpackBlocked = "UNPACK_BLOCKED";
    public const string ArchiveTooLarge = "ARCHIVE_TOO_LARGE";
    public const string NotAZip = "NOT_A_ZIP";
    public const string NothingToUnpack = "NOTHING_TO_UNPACK";
    public const string StorageQuotaExceeded = "STORAGE_QUOTA_EXCEEDED";
    public const string WorkerUnavailable = "WORKER_UNAVAILABLE";
    public const string WorkerBadResponse = "WORKER_BAD_RESPONSE";
    public const string WriteFailed = "WRITE_FAILED";
    public const string Interrupted = "UNPACK_INTERRUPTED";
    public const string ArchiveInvalid = "ARCHIVE_INVALID";
    public const string PasswordProtected = "ARCHIVE_PASSWORD_PROTECTED";
}

/// <summary>
/// Runs one unpack: streams the zip to the import worker, writes the validated files as ordinary project files, and on full
/// success removes the zip. Nothing the worker returns is trusted beyond its validated manifest and per-file digests; every
/// stored path goes through <see cref="ArchiveUnpackPaths"/>. A failure after writing started removes what was written and
/// keeps the zip.
/// </summary>
public sealed class ArchiveUnpackProcessor
{
    public const int MaxAttempts = 5;
    public const int BatchSize = 100;
    public static readonly TimeSpan StaleClaim = TimeSpan.FromMinutes(20);

    private const string EncryptedReason = "ENTRY_ENCRYPTED";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly OrganizationStorageLock _storageLock;
    private readonly IPlanGateService _planGate;
    private readonly IArchiveUnpackGateway _gateway;
    private readonly IPublishEndpoint _publish;
    private readonly ILogger<ArchiveUnpackProcessor>? _logger;
    private readonly ITenantContext? _tenant;
    private readonly IUserActivityNotifier? _notifier;
    private readonly TimeProvider _clock;
    private readonly ICsmaHashMatcher? _csam;
    private readonly ICsamQuarantineService? _csamQuarantine;
    private readonly IUploadAnalysisSetting? _uploadAnalysis;

    // The claim this run holds (the row's ClaimedAt as stored at the start of ProcessAsync). Every write that changes the row
    // or the extracted files checks it still holds, and extracted rows carry it in SourceRevision, so a second run after a
    // stale reclaim can never wipe or finish the other run's work.
    private DateTime? _claim;

    // The claim as first taken: extracted rows keep this revision for the whole run even though the claim time is refreshed.
    private DateTime? _revisionClaim;

    public ArchiveUnpackProcessor(
        InferenceApiDbContext db,
        IFileStorageService storage,
        OrganizationStorageLock storageLock,
        IPlanGateService planGate,
        IArchiveUnpackGateway gateway,
        IPublishEndpoint publish,
        ILogger<ArchiveUnpackProcessor>? logger = null,
        ITenantContext? tenant = null,
        IUserActivityNotifier? notifier = null,
        TimeProvider? clock = null,
        ICsmaHashMatcher? csam = null,
        ICsamQuarantineService? csamQuarantine = null,
        IUploadAnalysisSetting? uploadAnalysis = null)
    {
        _db = db;
        _storage = storage;
        _storageLock = storageLock;
        _planGate = planGate;
        _gateway = gateway;
        _publish = publish;
        _logger = logger;
        _tenant = tenant;
        _notifier = notifier;
        _clock = clock ?? TimeProvider.System;
        _csam = csam;
        _csamQuarantine = csamQuarantine;
        _uploadAnalysis = uploadAnalysis;
    }

    /// <summary>
    /// Claims the oldest due Pending unpack, or a Running one whose claim went stale, with a conditional update so two
    /// replicas never run one row. Null when nothing is due.
    /// </summary>
    public async Task<Guid?> ClaimNextAsync(CancellationToken ct)
    {
        using IDisposable system = _tenant.System();
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateTime staleBefore = now - StaleClaim;

        var candidate = await _db.ProjectArchiveUnpacks.AsNoTracking()
            .Where(u => (u.Status == ArchiveUnpackRunStatus.Pending && (u.NextAttemptAt == null || u.NextAttemptAt <= now))
                        || (u.Status == ArchiveUnpackRunStatus.Running && (u.ClaimedAt == null || u.ClaimedAt < staleBefore)))
            .OrderBy(u => u.CreatedAt)
            .Select(u => new { u.Id, u.Status, u.ClaimedAt, u.Attempts })
            .FirstOrDefaultAsync(ct);
        if (candidate is null) return null;

        bool stale = candidate.Status == ArchiveUnpackRunStatus.Running;
        int attempts = candidate.Attempts + (stale ? 1 : 0);
        bool claimed;
        if (_db.Database.IsRelational())
        {
            ArchiveUnpackRunStatus observed = candidate.Status;
            DateTime? observedClaim = candidate.ClaimedAt;
            int updated = await _db.ProjectArchiveUnpacks
                .Where(u => u.Id == candidate.Id && u.Status == observed && u.ClaimedAt == observedClaim)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.Status, ArchiveUnpackRunStatus.Running)
                    .SetProperty(u => u.ClaimedAt, now)
                    .SetProperty(u => u.StartedAt, now)
                    .SetProperty(u => u.NextAttemptAt, (DateTime?)null)
                    .SetProperty(u => u.Attempts, attempts), ct);
            claimed = updated == 1;
        }
        else
        {
            // The in-memory store has no conditional update: load, check, save (single process, tests only).
            ProjectArchiveUnpack? row = await _db.ProjectArchiveUnpacks.FirstOrDefaultAsync(u => u.Id == candidate.Id, ct);
            claimed = row is not null && row.Status == candidate.Status && row.ClaimedAt == candidate.ClaimedAt;
            if (claimed)
            {
                row!.Status = ArchiveUnpackRunStatus.Running;
                row.ClaimedAt = now;
                row.StartedAt = now;
                row.NextAttemptAt = null;
                row.Attempts = attempts;
                await _db.SaveChangesAsync(ct);
            }
        }

        _db.ChangeTracker.Clear();
        return claimed ? candidate.Id : null;
    }

    /// <summary>Runs a claimed unpack to a final state (Done, Failed) or back to Pending for a later attempt.</summary>
    public async Task ProcessAsync(Guid unpackId, CancellationToken ct)
    {
        using IDisposable system = _tenant.System();
        ProjectArchiveUnpack? unpack = await _db.ProjectArchiveUnpacks.FirstOrDefaultAsync(u => u.Id == unpackId, ct);
        if (unpack is null || unpack.Status != ArchiveUnpackRunStatus.Running) return;
        _claim = unpack.ClaimedAt;
        _revisionClaim = unpack.ClaimedAt;

        try
        {
            // A previous attempt that died after writing left rows behind: they go before anything is retried.
            await RemoveWrittenAsync(unpack, allClaims: true, ct);
            if (unpack.Attempts >= MaxAttempts)
            {
                await FailAsync(unpack, ArchiveUnpackFailure.Interrupted, null, ct);
                return;
            }

            await RunAsync(unpack, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: the claim goes stale and the sweep cleans up and retries.
            throw;
        }
        catch (ClaimLostException)
        {
            // Another run owns this unpack now; it cleans up and finishes. This run touches nothing more.
        }
        catch (UnpackStopException stop)
        {
            await AbandonAsync(unpackId, stop, ct);
        }
        catch (ArchiveUnpackViolationException)
        {
            await AbandonAsync(unpackId, new UnpackStopException(ArchiveUnpackFailure.WorkerBadResponse, retry: true), ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unpack {UnpackId} failed while writing", unpackId);
            await AbandonAsync(unpackId, new UnpackStopException(ArchiveUnpackFailure.WriteFailed), ct);
        }
    }

    private async Task RunAsync(ProjectArchiveUnpack unpack, CancellationToken ct)
    {
        ProjectFile? zip = unpack.SourceFileId is { } zipId
            ? await _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == zipId && f.ProjectId == unpack.ProjectId, ct)
            : null;
        if (zip is null || CsamScanGate.IsHeld(zip))
        {
            await FailAsync(unpack, ArchiveUnpackFailure.SourceGone, null, ct);
            return;
        }

        // The zip's own verdict must be settled before anything is extracted from it (the zip is deleted on success and
        // would otherwise escape its scan). Scan it now; a quarantine ends the unpack, an unsettled verdict waits.
        if (zip.CsamStatus is CsamScanStatus.Pending or CsamScanStatus.Scanning && _csam is not null)
        {
            CsamScanOutcome outcome = await _csam.ScanAsync(zip, unpack.RequestedByUserId, ct);
            if (outcome.Quarantined || CsamScanGate.IsHeld(zip))
            {
                await FailAsync(unpack, ArchiveUnpackFailure.SourceGone, null, ct);
                return;
            }
        }

        if (zip.CsamStatus is CsamScanStatus.Pending or CsamScanStatus.Scanning)
        {
            await RetryOrFailAsync(unpack, ArchiveUnpackFailure.Interrupted, ct);
            return;
        }

        if (zip.SizeBytes > UnpackLimits.MaxArchiveBytes)
        {
            await FailAsync(unpack, ArchiveUnpackFailure.ArchiveTooLarge, null, ct);
            return;
        }

        Stream source;
        try
        {
            source = await _storage.DownloadAsync(unpack.ProjectId, zip.StorageKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (StorageFailure.IsTransient(ex))
                await RetryOrFailAsync(unpack, ArchiveUnpackFailure.WorkerUnavailable, ct);
            else
                await FailAsync(unpack, ArchiveUnpackFailure.SourceGone, null, ct);
            return;
        }

        await using (source)
        {
            using ArchiveUnpackSession session = await _gateway.OpenAsync(source, zip.SizeBytes, ct);
            switch (session.Status)
            {
                case ArchiveUnpackStatus.Busy:
                    await RetryLaterAsync(unpack, TimeSpan.FromSeconds(Math.Clamp(session.RetryAfterSeconds, 1, 60)), countAttempt: false, ct);
                    return;
                case ArchiveUnpackStatus.Unavailable:
                    await RetryOrFailAsync(unpack, ArchiveUnpackFailure.WorkerUnavailable, ct);
                    return;
                case ArchiveUnpackStatus.BadResponse:
                    await RetryOrFailAsync(unpack, ArchiveUnpackFailure.WorkerBadResponse, ct);
                    return;
                case ArchiveUnpackStatus.TooLarge:
                    await FailAsync(unpack, ArchiveUnpackFailure.ArchiveTooLarge, null, ct);
                    return;
                case ArchiveUnpackStatus.NotZip:
                    await FailAsync(unpack, ArchiveUnpackFailure.NotAZip, null, ct);
                    return;
                case ArchiveUnpackStatus.Aborted:
                    await FailAsync(unpack, AbortCode(session.Manifest?.AbortReason), session.Manifest, ct);
                    return;
            }

            await WriteAsync(unpack, zip, session, ct);
        }
    }

    private static string AbortCode(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return ArchiveUnpackFailure.ArchiveInvalid;
        string code = reason.Trim();
        return code.Length > 64 ? code[..64] : code;
    }

    private async Task WriteAsync(ProjectArchiveUnpack unpack, ProjectFile zip, ArchiveUnpackSession session, CancellationToken ct)
    {
        UnpackManifest manifest = session.Manifest!;
        if (manifest.Files.Count == 0)
        {
            await FailAsync(
                unpack,
                manifest.SkippedCounts.GetValueOrDefault(EncryptedReason) > 0
                    ? ArchiveUnpackFailure.PasswordProtected
                    : ArchiveUnpackFailure.NothingToUnpack,
                manifest,
                ct);
            return;
        }

        // Plan every path before a single byte is written: the folder, the stripped top folder, the name rules.
        string? strip = ArchiveUnpackPaths.SharedTopFolder([.. manifest.Files.Select(f => f.Path)]);
        string target = await PickTargetAsync(unpack.ProjectId, zip.DirectoryPath, ArchiveUnpackPaths.SanitizeFolderName(zip.OriginalFileName), ct);
        Dictionary<string, int> counts = new(manifest.SkippedCounts, StringComparer.Ordinal);
        List<UnpackSkippedSample> samples = [.. manifest.SkippedSamples];
        string?[] finalPaths = new string?[manifest.Files.Count];
        HashSet<string> seen = new(StringComparer.Ordinal);
        long total = 0;
        int kept = 0;
        for (int i = 0; i < manifest.Files.Count; i++)
        {
            UnpackManifestEntry entry = manifest.Files[i];
            string? reason = null;
            if (!ArchiveUnpackPaths.TryBuildPath(target, entry.Path, strip, out string path)) reason = "PATH_INVALID";
            else if (!seen.Add(path)) reason = "PATH_DUPLICATE";

            if (reason is not null)
            {
                counts[reason] = counts.GetValueOrDefault(reason) + 1;
                samples.Add(new UnpackSkippedSample(entry.Path, reason));
                continue;
            }

            finalPaths[i] = path;
            total += entry.Size;
            kept++;
        }

        if (kept == 0)
        {
            await FailAsync(
                unpack, counts.GetValueOrDefault(EncryptedReason) > 0 ? ArchiveUnpackFailure.PasswordProtected : ArchiveUnpackFailure.NothingToUnpack,
                counts, samples, ct);
            return;
        }

        unpack.TargetDirectory = target;
        unpack.SkippedJson = SerializeSkipped(counts, samples);
        await _db.SaveChangesAsync(ct);

        // Decide the quota on the net growth (the zip goes on success) under the organization's storage lock.
        if (await QuotaViolatesAsync(unpack.OrganizationId, total, zip.SizeBytes, ct))
        {
            await FailAsync(unpack, ArchiveUnpackFailure.StorageQuotaExceeded, counts, samples, ct);
            return;
        }

        List<ProjectFile> batch = [];
        List<ProjectFile> media = [];
        HashSet<Guid> noDerivatives = [];
        long committedBytes = 0;
        int index = -1;
        try
        {
            await foreach ((UnpackManifestEntry entry, Stream content) in session.ReadFilesAsync(ct))
            {
                await using Stream stream = content;
                index++;
                string? path = finalPaths[index];
                if (path is null) continue;

                ProjectFile row = await StoreAsync(unpack, path, stream, ct);
                batch.Add(row);
                if (ArchiveUnpackPaths.IsMedia(row.MimeType)) media.Add(row);
                if (batch.Count >= BatchSize)
                {
                    committedBytes += await CommitBatchAsync(unpack, zip, batch, total - committedBytes, ct);
                    List<ProjectFile> committed = batch;
                    batch = [];
                    await ScanRowsAsync(unpack, committed, noDerivatives, ct);
                }
            }

            if (batch.Count > 0)
            {
                committedBytes += await CommitBatchAsync(unpack, zip, batch, total - committedBytes, ct);
                List<ProjectFile> committed = batch;
                batch = [];
                await ScanRowsAsync(unpack, committed, noDerivatives, ct);
            }
        }
        catch
        {
            await DeleteObjectsQuietlyAsync(unpack.ProjectId, batch, ct);
            throw;
        }

        // Fully successful: the zip goes in the same save that marks the unpack done.
        if (!await CompleteAsync(unpack, zip, ct)) return;

        foreach (ProjectFile row in media.Where(m => !noDerivatives.Contains(m.Id)))
            await UploadDerivativeRequest.PublishAsync(_publish, _storage, _uploadAnalysis, row, row.MimeType, _logger, ct);

        try
        {
            if (_notifier is not null)
                await _notifier.FileCreatedAsync(unpack.ProjectId, unpack.RequestedByUserId, $"{unpack.TargetDirectory} ({unpack.FilesCreated} files)", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Best-effort: a notification failure never undoes an unpack.
        }
    }

    /// <summary>
    /// Scans every committed row for CSAM right away, the same matcher an upload uses, so no extracted file waits for the
    /// sweeper. A held (quarantined) or unsettled row is added to <paramref name="noDerivatives"/>: nothing is derived from it.
    /// </summary>
    private async Task ScanRowsAsync(ProjectArchiveUnpack unpack, List<ProjectFile> rows, HashSet<Guid> noDerivatives, CancellationToken ct)
    {
        if (_csam is null) return;
        foreach (ProjectFile row in rows)
        {
            try
            {
                await _csam.ScanAsync(row, unpack.RequestedByUserId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger?.LogWarning(ex, "CSAM scan of unpacked file {FileId} failed; its derivatives wait", row.Id);
                noDerivatives.Add(row.Id);
                continue;
            }

            if (CsamScanGate.IsHeld(row) || row.CsamStatus is CsamScanStatus.Pending or CsamScanStatus.Scanning)
                noDerivatives.Add(row.Id);
        }

        // The original upload is held the moment a member is held, before a replace or delete can take it.
        if (rows.Any(CsamScanGate.IsHeld)) await QuarantineIfHeldAsync(unpack);
    }

    private string Revision(ProjectArchiveUnpack unpack) => $"{unpack.Id:D}:{_revisionClaim?.Ticks ?? 0}";

    /// <summary>
    /// Refreshes the claim time so a long unpack is not reclaimed as stale. The fencing check and the refresh are one
    /// conditional update: it matches only while this run still holds the claim, otherwise the claim is lost.
    /// </summary>
    private async Task HeartbeatAsync(Guid unpackId)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateTime? held = _claim;
        if (_db.Database.IsRelational())
        {
            int updated = await _db.ProjectArchiveUnpacks
                .Where(u => u.Id == unpackId && u.Status == ArchiveUnpackRunStatus.Running && u.ClaimedAt == held)
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.ClaimedAt, now), CancellationToken.None);
            if (updated != 1) throw new ClaimLostException();
            _claim = await _db.ProjectArchiveUnpacks.AsNoTracking()
                .Where(u => u.Id == unpackId).Select(u => u.ClaimedAt).FirstOrDefaultAsync(CancellationToken.None);
            ProjectArchiveUnpack? tracked = _db.ChangeTracker.Entries<ProjectArchiveUnpack>().FirstOrDefault(e => e.Entity.Id == unpackId)?.Entity;
            if (tracked is not null)
            {
                tracked.ClaimedAt = _claim;
                Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry<ProjectArchiveUnpack, DateTime?> entry = _db.Entry(tracked).Property(u => u.ClaimedAt);
                entry.OriginalValue = _claim;
                entry.IsModified = false;
            }

            return;
        }

        // The in-memory store has no conditional update: check, then save at once (single process, tests only).
        var stored = await _db.ProjectArchiveUnpacks.AsNoTracking().Where(u => u.Id == unpackId)
            .Select(u => new { u.Status, u.ClaimedAt }).FirstOrDefaultAsync(CancellationToken.None);
        if (stored is null || stored.Status != ArchiveUnpackRunStatus.Running || stored.ClaimedAt != held) throw new ClaimLostException();
        ProjectArchiveUnpack row = await _db.ProjectArchiveUnpacks.FirstAsync(u => u.Id == unpackId, CancellationToken.None);
        row.ClaimedAt = now;
        await _db.SaveChangesAsync(CancellationToken.None);
        _claim = now;
    }

    /// <summary>Throws <see cref="ClaimLostException"/> unless this run still holds the unpack's claim.</summary>
    private async Task EnsureClaimAsync(Guid unpackId)
    {
        var current = await _db.ProjectArchiveUnpacks.AsNoTracking()
            .Where(u => u.Id == unpackId)
            .Select(u => new { u.Status, u.ClaimedAt })
            .FirstOrDefaultAsync(CancellationToken.None);
        if (current is null || current.Status != ArchiveUnpackRunStatus.Running || current.ClaimedAt != _claim)
            throw new ClaimLostException();
    }

    private async Task<ProjectFile> StoreAsync(ProjectArchiveUnpack unpack, string path, Stream content, CancellationToken ct)
    {
        string name = ProjectFilePath.GetFileName(path);
        Guid fileId = Guid.NewGuid();
        string mime = ArchiveUnpackPaths.ResolveMimeType(name);
        mime = await TransportStreamMimeSniffer.ResolveAsync(mime, name, content, ct) ?? mime;
        long size = content.Length;

        Dictionary<string, string> metadata = new()
        {
            ["project-id"] = unpack.ProjectId.ToString(),
            ["org-id"] = unpack.OrganizationId.ToString(),
            ["project-file-id"] = fileId.ToString(),
            ["archive-unpack"] = unpack.Id.ToString(),
        };
        StoredFileObject stored = await _storage.UploadAsync(
            unpack.ProjectId, fileId, content, name, mime, metadata, ct, category: "userFiles", originalPath: path);

        return new ProjectFile
        {
            Id = fileId,
            ProjectId = unpack.ProjectId,
            OriginalFileName = stored.OriginalFileName,
            OriginalPath = stored.OriginalPath,
            DirectoryPath = stored.DirectoryPath,
            Category = "userFiles",
            StorageKey = stored.StorageKey,
            StorageFileName = stored.StorageFileName,
            StorageBucket = stored.BucketName,
            StoragePrefix = stored.StoragePrefix,
            StorageMetadataJson = stored.StorageMetadataJson,
            MimeType = mime,
            SizeBytes = size,
            SourceKind = "zip",
            SourceRevision = Revision(unpack),
            // Not summarised or indexed (the same as a synced repository file), but scanned like any upload.
            SummaryStatus = SummaryStatus.Done,
            IndexingStatus = FileIndexingStatus.NotIndexed,
            CsamStatus = CsamScanStatus.Pending,
            ModerationStatus = FileModerationStatus.Unmoderated,
            UploadedAt = _clock.GetUtcNow().UtcDateTime,
        };
    }

    /// <summary>Rechecks the quota under the lock, then saves one batch of rows and the running counts.</summary>
    private async Task<long> CommitBatchAsync(ProjectArchiveUnpack unpack, ProjectFile zip, List<ProjectFile> batch, long remainingBytes, CancellationToken ct)
    {
        // Outside the lock's transaction: a quota stop rolls that back, and the refresh must not go with it.
        await HeartbeatAsync(unpack.Id);
        await using OrganizationStorageGuard guard = await _storageLock.AcquireAsync(_db, unpack.OrganizationId, ct);
        if (await QuotaViolatesCoreAsync(unpack.OrganizationId, remainingBytes, zip.SizeBytes, ct))
            throw new UnpackStopException(ArchiveUnpackFailure.StorageQuotaExceeded);

        long bytes = batch.Sum(f => f.SizeBytes);
        _db.ProjectFiles.AddRange(batch);
        unpack.FilesCreated += batch.Count;
        unpack.BytesCreated += bytes;
        await _db.SaveChangesAsync(ct);
        await guard.CommitAsync(ct);
        return bytes;
    }

    private async Task<bool> QuotaViolatesAsync(Guid organizationId, long total, long zipSize, CancellationToken ct)
    {
        await using OrganizationStorageGuard guard = await _storageLock.AcquireAsync(_db, organizationId, ct);
        bool violates = await QuotaViolatesCoreAsync(organizationId, total, zipSize, ct);
        await guard.CommitAsync(ct);
        return violates;
    }

    private async Task<bool> QuotaViolatesCoreAsync(Guid organizationId, long remainingBytes, long zipSize, CancellationToken ct)
    {
        long net = remainingBytes - zipSize;
        if (net <= 0) return false;
        return await _planGate.CheckUploadAsync(organizationId, net, ct) is not null;
    }

    /// <summary>The target folder: the zip's name, or "name (2)", "name (3)" while any existing file lives at or under it.</summary>
    private async Task<string> PickTargetAsync(Guid projectId, string? zipDirectory, string folderName, CancellationToken ct)
    {
        for (int n = 1; n < 1000; n++)
        {
            string candidate = ArchiveUnpackPaths.CombineDirectory(zipDirectory, n == 1 ? folderName : $"{folderName} ({n})");
            string prefix = candidate + "/";
            bool taken = await _db.ProjectFiles.AnyAsync(
                f => f.ProjectId == projectId
                     && (f.DirectoryPath == candidate
                         || (f.DirectoryPath != null && f.DirectoryPath.StartsWith(prefix))
                         || f.OriginalPath == candidate),
                ct);
            if (!taken) return candidate;
        }

        throw new UnpackStopException(ArchiveUnpackFailure.WriteFailed);
    }

    /// <summary>False when a held extracted file rolled the unpack back: nothing was published for it, so the caller publishes and notifies nothing.</summary>
    private async Task<bool> CompleteAsync(ProjectArchiveUnpack unpack, ProjectFile zip, CancellationToken ct)
    {
        await HeartbeatAsync(unpack.Id);

        // A held extracted file means the zip is the original upload containing the match: it is quarantined like any match
        // (kept as evidence, never deleted), with the held rows. Only the clean extracted rows are rolled back, and the
        // unpack ends as failed.
        // Any attempt of this unpack counts: a row an earlier attempt left held (its revision prefixed "held:") still
        // means the zip contained a match, even if this attempt's rescan failed open.
        string idText = unpack.Id.ToString("D");
        string heldIdText = HeldRevisionPrefix + idText;
        bool anyHeld = await _db.ProjectFiles.AnyAsync(f => f.ProjectId == unpack.ProjectId && f.SourceKind == "zip"
            && f.SourceRevision != null && (f.SourceRevision.StartsWith(idText) || f.SourceRevision.StartsWith(heldIdText))
            && (f.CsamStatus == CsamScanStatus.Quarantined || f.CsamQuarantineKey != null), CancellationToken.None);
        if (anyHeld)
        {
            _db.ChangeTracker.Clear();
            ProjectArchiveUnpack? fresh = await _db.ProjectArchiveUnpacks.FirstOrDefaultAsync(u => u.Id == unpack.Id, CancellationToken.None);
            if (fresh is null) return false;
            await QuarantineIfHeldAsync(fresh);
            await RemoveWrittenAsync(fresh, allClaims: false, ct);
            await FailAsync(fresh, ArchiveUnpackFailure.SourceGone, null, ct);
            return false;
        }

        // Re-read the zip: it may have been deleted, quarantined or replaced while it was unpacking. Only the very zip that
        // was unpacked (same id, object and upload) may be removed, and never a quarantined one (it is evidence).
        var current = await _db.ProjectFiles.AsNoTracking()
            .Where(f => f.Id == zip.Id && f.ProjectId == zip.ProjectId)
            .Select(f => new { f.StorageKey, f.SizeBytes, f.UploadedAt, f.CsamStatus, f.CsamQuarantineKey })
            .FirstOrDefaultAsync(CancellationToken.None);
        if (current is null || current.CsamStatus == CsamScanStatus.Quarantined || current.CsamQuarantineKey is not null)
            throw new UnpackStopException(ArchiveUnpackFailure.SourceGone);
        if (current.StorageKey != zip.StorageKey || current.SizeBytes != zip.SizeBytes || current.UploadedAt != zip.UploadedAt)
            throw new UnpackStopException(ArchiveUnpackFailure.Interrupted, requeue: true);

        List<StorageRetentionMark> marks = string.IsNullOrEmpty(zip.StorageKey)
            ? []
            :
            [
                new StorageRetentionMark
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = unpack.OrganizationId,
                    ProjectId = zip.ProjectId,
                    StorageKey = zip.StorageKey,
                    Category = zip.Category,
                    MarkedAt = _clock.GetUtcNow().UtcDateTime,
                }
            ];
        _db.StorageRetentionMarks.AddRange(marks);
        _db.OutputModerationVerdicts.RemoveRange(await _db.OutputModerationVerdicts
            .Where(v => v.ProjectId == zip.ProjectId && v.StorageKey == zip.StorageKey).ToListAsync(ct));
        _db.ProjectFiles.Remove(zip);
        unpack.SourceFileId = null;
        unpack.Status = ArchiveUnpackRunStatus.Done;
        unpack.FailureCode = null;
        unpack.ClaimedAt = null;
        unpack.FinishedAt = _clock.GetUtcNow().UtcDateTime;
        try
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The zip row went away between the check and the save.
            throw new UnpackStopException(ArchiveUnpackFailure.SourceGone);
        }

        try
        {
            if (marks.Count > 0)
            {
                await _storage.DeleteAsync(zip.ProjectId, zip.StorageKey, CancellationToken.None);
                _db.StorageRetentionMarks.RemoveRange(marks);
                await _db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception ex) when (StorageFailure.IsTransient(ex))
        {
            // The durable mark makes the retention sweep retry the object delete.
            _logger?.LogWarning(ex, "Could not remove the object of unpacked zip {FileId}; its retention mark makes the sweep retry it", zip.Id);
        }

        await _publish.Publish(new ProjectFileIndexingRequested
        {
            ProjectId = zip.ProjectId,
            FileId = zip.Id,
            Operation = "Delete",
            RequestedAt = DateTime.UtcNow,
        }, CancellationToken.None);
        return true;
    }

    private const string HeldRevisionPrefix = "held:";

    /// <summary>
    /// Run by every transition that ends or rolls back an attempt: when any row this unpack wrote is held, the source zip
    /// is quarantined and the project hold is set. Idempotent; never deletes anything and never fails the transition.
    /// </summary>
    private async Task<bool> QuarantineIfHeldAsync(ProjectArchiveUnpack unpack)
    {
        try
        {
            if (unpack.SourceFileId is not Guid zipId) return false;
            string idText = unpack.Id.ToString("D");
            string heldIdText = HeldRevisionPrefix + idText;
            bool anyHeld = await _db.ProjectFiles.AnyAsync(f => f.ProjectId == unpack.ProjectId && f.SourceKind == "zip"
                && f.SourceRevision != null && (f.SourceRevision.StartsWith(idText) || f.SourceRevision.StartsWith(heldIdText))
                && (f.CsamStatus == CsamScanStatus.Quarantined || f.CsamQuarantineKey != null), CancellationToken.None);
            if (anyHeld) await QuarantineZipAsync(unpack, zipId);
            return anyHeld;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Could not quarantine the source zip of unpack {UnpackId} after a held file", unpack.Id);
            return true;
        }
    }

    /// <summary>
    /// Holds the source zip the way a real match does: its object moves to the project's quarantine prefix, the row is
    /// Quarantined and the project gets its quarantine marker and output hold. The zip is never deleted.
    /// </summary>
    private async Task QuarantineZipAsync(ProjectArchiveUnpack unpack, Guid zipId)
    {
        ProjectFile? zip = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == zipId && f.ProjectId == unpack.ProjectId, CancellationToken.None);
        if (zip is null) return;

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        // The verdict is recorded first: a failed move still leaves the row held, as the matcher does.
        zip.CsamStatus = CsamScanStatus.Quarantined;
        zip.CsamQuarantinedAt ??= now;
        await ProjectContentQuarantineMarker.StageAsync(_db, unpack.ProjectId, now, CancellationToken.None);
        await ProjectContentQuarantineMarker.StageCsamAsync(_db, unpack.ProjectId, now, CancellationToken.None);
        // The held verdict and the project hold are saved before the object moves: a failed save can never leave an unheld
        // row pointing at a moved key.
        await _db.SaveChangesAsync(CancellationToken.None);

        if (_csamQuarantine is not null && zip.CsamQuarantineKey is null)
        {
            try
            {
                await _csamQuarantine.MoveAsync(zip, CancellationToken.None);
                await _db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Could not move held zip {FileId} to quarantine; the row stays Quarantined", zip.Id);
            }
        }
    }

    /// <summary>Removes every row (and its object) an earlier or failed attempt of this unpack wrote, except rows the CSAM control holds.</summary>
    private async Task RemoveWrittenAsync(ProjectArchiveUnpack unpack, bool allClaims, CancellationToken ct)
    {
        // allClaims (the start of a run that holds the claim now): rows of every earlier claim go. Otherwise only this
        // run's own rows, so a run that lost its claim to another can never remove the other run's files.
        string revision = Revision(unpack);
        string idText = unpack.Id.ToString("D");
        string prefix = idText + ":";
        List<ProjectFile> written = await _db.ProjectFiles
            .Where(f => f.ProjectId == unpack.ProjectId && f.SourceKind == "zip"
                        && (allClaims ? f.SourceRevision == idText || (f.SourceRevision != null && f.SourceRevision.StartsWith(prefix)) : f.SourceRevision == revision))
            .ToListAsync(ct);
        if (written.Count == 0) return;

        // A row the CSAM control holds is evidence: it is never rolled back, its quarantined object is never deleted and
        // no retention mark is added for it. It is detached from this unpack's revision so no later cleanup can match it,
        // and the project's quarantine marker/hold is left exactly as it is.
        // The CSAM sweeper may have held a row since it was loaded: read the verdicts fresh right before anything is removed.
        List<Guid> writtenIds = [.. written.Select(f => f.Id)];
        HashSet<Guid> heldNow = [.. await _db.ProjectFiles.AsNoTracking()
            .Where(f => writtenIds.Contains(f.Id) && (f.CsamStatus == CsamScanStatus.Quarantined || f.CsamQuarantineKey != null))
            .Select(f => f.Id).ToListAsync(CancellationToken.None)];
        List<ProjectFile> held = [.. written.Where(f => CsamScanGate.IsHeld(f) || heldNow.Contains(f.Id))];
        foreach (ProjectFile f in held)
            f.SourceRevision = HeldRevisionPrefix + f.SourceRevision;
        written = [.. written.Except(held)];

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        List<StorageRetentionMark> marks =
        [
            .. written.Where(f => !string.IsNullOrEmpty(f.StorageKey)).Select(f => new StorageRetentionMark
            {
                Id = Guid.NewGuid(),
                OrganizationId = unpack.OrganizationId,
                ProjectId = f.ProjectId,
                StorageKey = f.StorageKey,
                Category = f.Category,
                MarkedAt = now,
            })
        ];
        _db.StorageRetentionMarks.AddRange(marks);
        _db.ProjectFiles.RemoveRange(written);
        unpack.FilesCreated = held.Count;
        unpack.BytesCreated = held.Sum(f => f.SizeBytes);
        await _db.SaveChangesAsync(CancellationToken.None);

        List<StorageRetentionMark> done = [];
        foreach (StorageRetentionMark mark in marks)
        {
            try
            {
                await _storage.DeleteAsync(unpack.ProjectId, mark.StorageKey, CancellationToken.None);
                done.Add(mark);
            }
            catch (Exception ex) when (StorageFailure.IsTransient(ex))
            {
                _logger?.LogWarning(ex, "Could not remove an object of a rolled-back unpack; its retention mark makes the sweep retry it");
            }
        }

        if (done.Count > 0)
        {
            _db.StorageRetentionMarks.RemoveRange(done);
            await _db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task DeleteObjectsQuietlyAsync(Guid projectId, IEnumerable<ProjectFile> rows, CancellationToken ct)
    {
        foreach (ProjectFile row in rows)
        {
            try
            {
                await _storage.DeleteAsync(projectId, row.StorageKey, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger?.LogWarning(ex, "Could not remove an object of an unpack that failed before its rows were saved");
            }
        }
    }

    /// <summary>After a failure while writing: forget the tracked state, remove what was written, then retry or fail.</summary>
    private async Task AbandonAsync(Guid unpackId, UnpackStopException stop, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        try
        {
            await EnsureClaimAsync(unpackId);
            ProjectArchiveUnpack? unpack = await _db.ProjectArchiveUnpacks.FirstOrDefaultAsync(u => u.Id == unpackId, CancellationToken.None);
            if (unpack is null) return;

            await RemoveWrittenAsync(unpack, allClaims: false, ct);
            if (stop.Requeue)
                await RetryLaterAsync(unpack, TimeSpan.Zero, countAttempt: false, ct);
            else if (stop.Retry)
                await RetryOrFailAsync(unpack, stop.Code, ct);
            else
                await FailAsync(unpack, stop.Code, null, ct);
        }
        catch (ClaimLostException)
        {
            // Another run owns this unpack now and cleans up after it.
        }
    }

    private Task FailAsync(ProjectArchiveUnpack unpack, string code, UnpackManifest? manifest, CancellationToken ct) =>
        manifest is null
            ? FailAsync(unpack, code, null, null, ct)
            : FailAsync(unpack, code, manifest.SkippedCounts, manifest.SkippedSamples, ct);

    private async Task FailAsync(
        ProjectArchiveUnpack unpack, string code, IReadOnlyDictionary<string, int>? counts, IReadOnlyList<UnpackSkippedSample>? samples, CancellationToken ct)
    {
        _ = ct;
        await EnsureClaimAsync(unpack.Id);
        // A held member ends the unpack with its own neutral code, whatever the exit was.
        if (await QuarantineIfHeldAsync(unpack)) code = ArchiveUnpackFailure.UnpackBlocked;
        unpack.Status = ArchiveUnpackRunStatus.Failed;
        unpack.FailureCode = code;
        unpack.ClaimedAt = null;
        unpack.NextAttemptAt = null;
        unpack.FinishedAt = _clock.GetUtcNow().UtcDateTime;
        if (counts is not null) unpack.SkippedJson = SerializeSkipped(counts, samples ?? []);
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task RetryOrFailAsync(ProjectArchiveUnpack unpack, string code, CancellationToken ct)
    {
        int attempts = unpack.Attempts + 1;
        if (attempts >= MaxAttempts)
        {
            unpack.Attempts = attempts;
            await FailAsync(unpack, code, null, null, ct);
            return;
        }

        unpack.Attempts = attempts;
        await RetryLaterAsync(unpack, TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, attempts - 1))), countAttempt: false, ct);
    }

    private async Task RetryLaterAsync(ProjectArchiveUnpack unpack, TimeSpan delay, bool countAttempt, CancellationToken ct)
    {
        _ = ct;
        await EnsureClaimAsync(unpack.Id);
        if (await QuarantineIfHeldAsync(unpack))
        {
            // A zip with a held member is never unpacked again: retrying would only hit the quarantined zip.
            await FailAsync(unpack, ArchiveUnpackFailure.UnpackBlocked, null, null, ct);
            return;
        }

        if (countAttempt) unpack.Attempts++;
        unpack.Status = ArchiveUnpackRunStatus.Pending;
        unpack.ClaimedAt = null;
        unpack.NextAttemptAt = _clock.GetUtcNow().UtcDateTime + delay;
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>The skipped summary: counts by reason and at most <see cref="ProjectArchiveUnpack.MaxSkippedSamples"/> samples.</summary>
    public static string SerializeSkipped(IReadOnlyDictionary<string, int> counts, IReadOnlyList<UnpackSkippedSample> samples) =>
        JsonSerializer.Serialize(new
        {
            counts = counts.Where(c => c.Value > 0).ToDictionary(c => c.Key, c => c.Value),
            samples = samples.Take(ProjectArchiveUnpack.MaxSkippedSamples)
                .Select(s => new { path = s.Path.Length > 300 ? s.Path[..300] : s.Path, reason = s.Reason }),
        }, Json);

    private sealed class UnpackStopException(string code, bool retry = false, bool requeue = false) : Exception(code)
    {
        public string Code { get; } = code;
        public bool Retry { get; } = retry;

        /// <summary>The zip was replaced while it unpacked: undo this run and queue the unpack again for the new content.</summary>
        public bool Requeue { get; } = requeue;
    }

    private sealed class ClaimLostException : Exception;
}
