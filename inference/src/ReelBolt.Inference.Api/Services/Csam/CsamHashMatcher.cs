using System.Security.Cryptography;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.Inference.Api.Services.Csam;

/// <summary>
/// Computes a local PDQ hash for an image and asks Project Arachnid Shield about it; video (and an
/// image the local hasher cannot decode, or a file whose declared type is unknown) goes to the
/// provider's media endpoint. Any provider or configuration failure fails open. Nothing except the
/// configured Arachnid base URL is ever called — the general moderation capability is deliberately
/// not on this path.
/// <para>
/// The final row write is monotonic: a non-quarantine outcome is an atomic conditional update
/// (<c>csam_status &lt;&gt; 'Quarantined'</c>), so a concurrent scan can never downgrade a row another
/// scan quarantined. A quarantine writes unconditionally.
/// </para>
/// </summary>
public sealed class CsamHashMatcher : ICsmaHashMatcher
{
    private const string ClassificationCsam = "csam";
    private const string ClassificationHarmful = "harmful-abusive-material";

    private const string StatusClean = "clean";
    private const string StatusMatch = "match";
    private const string StatusNotScanned = "not-scanned";
    private const string StatusDisabled = "disabled";

    private const string SourcePdq = "pdq";
    private const string SourceMedia = "media";
    private const string SourceNone = "none";

    // Provider-controlled strings are clamped to the audit columns: a longer 2xx field must not make
    // the verdict write throw (which would break fail-open and return 500 to the uploader).
    private const int MaxClassificationChars = 100;
    private const int MaxMatchTypeChars = 50;
    private const int MaxProviderVersionChars = 100;
    private const int MaxErrorCodeChars = 50;

    private static string? Clamp(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly IPdqHasher _hasher;
    private readonly IArachnidShieldClient _client;
    private readonly ICsamQuarantineService _quarantine;
    private readonly IOptions<CsamOptions> _options;
    private readonly ILogger<CsamHashMatcher>? _logger;
    private readonly TimeProvider _clock;
    private readonly IPublishEndpoint? _publisher;

    public CsamHashMatcher(
        InferenceApiDbContext db,
        IFileStorageService storage,
        IPdqHasher hasher,
        IArachnidShieldClient client,
        ICsamQuarantineService quarantine,
        IOptions<CsamOptions> options,
        ILogger<CsamHashMatcher>? logger = null,
        TimeProvider? clock = null,
        IPublishEndpoint? publisher = null)
    {
        _db = db;
        _storage = storage;
        _hasher = hasher;
        _client = client;
        _quarantine = quarantine;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _publisher = publisher;
    }

    public async Task<CsamScanOutcome> ScanAsync(ProjectFile file, Guid? actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        // Quarantine is final until a human releases it: no outcome of this method may silently
        // downgrade a row whose bytes are already under quarantine/.
        bool wasQuarantined = file.CsamStatus == CsamScanStatus.Quarantined;

        ArachnidOptions options = _options.Value.Arachnid;

        if (!options.Enabled)
        {
            // The master switch is off: no request and no scanned-at timestamp, but an audit row
            // says plainly that the control was disabled rather than that a scan happened. A
            // quarantined row is never downgraded just because the control was switched off.
            AddAudit(file, actorUserId, StatusDisabled, result: null, providerVersion: null,
                new Attempt { Source = SourceNone },
                quarantined: wasQuarantined,
                quarantineSucceeded: wasQuarantined && file.CsamQuarantineKey is not null,
                errorCode: null);

            CsamScanStatus disabledFinal = await PersistAsync(file, CsamScanStatus.NotScanned, null, null,
                quarantine: false, markScannedAt: false, ct);
            _logger?.LogInformation(
                "CSAM scan of project file {FileId} was not performed: the control is disabled.", file.Id);
            return new CsamScanOutcome(disabledFinal, file.CsamClassification, file.CsamMatchType, wasQuarantined, "DISABLED");
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || !Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out Uri? baseUri)
            || baseUri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(options.Username)
            || string.IsNullOrWhiteSpace(options.Password))
        {
            return await FailOpenAsync(file, actorUserId, "CONFIG_MISSING", SourceNone, null, null, null, wasQuarantined, ct);
        }

        Attempt attempt = new();
        ScanKind kind;
        try
        {
            kind = await ResolveKindAsync(file, attempt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The head read is transport: fail open exactly like a provider outage.
            return await FailOpenAsync(file, actorUserId, "TRANSPORT", SourceNone, null, null, null, wasQuarantined, ct);
        }

        if (kind == ScanKind.Unsupported)
            return await FailOpenAsync(file, actorUserId, "UNSUPPORTED_MEDIA_TYPE", SourceNone, null, null, null, wasQuarantined, ct);

        ArachnidScanResult? result = null;
        string? errorCode = null;
        try
        {
            result = await ScanProviderAsync(file, options, kind, attempt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller is going away (client disconnect): not a provider outage.
            throw;
        }
        catch (OperationCanceledException)
        {
            errorCode = "TIMEOUT";
        }
        catch (HttpRequestException ex)
        {
            errorCode = ex.StatusCode is { } status ? $"HTTP_{(int)status}" : "TRANSPORT";
        }
        catch (ArachnidProtocolException)
        {
            errorCode = "INVALID_RESPONSE";
        }
        catch (Exception)
        {
            // Transport (DNS, socket, TLS), an unreadable stream, anything else: never block the upload.
            errorCode = "TRANSPORT";
        }

        if (errorCode is not null || result is null)
        {
            return await FailOpenAsync(file, actorUserId, errorCode ?? "TRANSPORT", attempt.Source,
                attempt.PdqHash, attempt.PdqQuality, attempt.ContentSha256Hex, wasQuarantined, ct);
        }

        string? providerVersion = await TryProviderVersionAsync(ct);
        bool isMatch = IsMatch(result.Classification);

        if (isMatch)
        {
            // The key the derivatives were built from, captured before the move. A render can be
            // addressed both as a project file and as a step result; its step-result derivative rows
            // carry SourceProjectFileId = null, so the move must not make them unfindable.
            string originalKey = file.StorageKey;
            string? classification = Clamp(result.Classification, MaxClassificationChars);
            string? matchType = Clamp(result.MatchType, MaxMatchTypeChars);
            string? version = Clamp(providerVersion, MaxProviderVersionChars);

            // Delete the derivative ROWS in the same save as the verdict. A failed object-store
            // delete must not leave a row that the media routes can still serve: the row is the
            // address, and it goes before any byte ever moves.
            List<MediaDerivative> derivatives = await _db.MediaDerivatives
                .Where(d => d.ProjectId == file.ProjectId
                            && (d.SourceProjectFileId == file.Id
                                || d.SourceStorageKey == originalKey))
                .ToListAsync(ct);
            if (derivatives.Count != 0)
                _db.MediaDerivatives.RemoveRange(derivatives);

            // Persist the verdict BEFORE the move: a save failure must not leave the bytes servable
            // under an un-quarantined row. If this write fails, the move is still attempted and the
            // second write below retries the row state.
            try
            {
                await PersistAsync(file, CsamScanStatus.Quarantined, classification, matchType,
                    quarantine: true, markScannedAt: true, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    "CSAM could not persist the quarantine verdict for project file {FileId} ({ExceptionType}); attempting the move.",
                    file.Id, ex.GetType().Name);
            }

            bool moved = true;
            try
            {
                await _quarantine.MoveAsync(file, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The audit must survive a failed move: the row is still Quarantined, so no serving
                // path returns the bytes, and the evidence is re-scanned later.
                moved = false;
                _logger?.LogWarning(
                    "CSAM quarantine move failed for project file {FileId} ({ExceptionType}); the row stays Quarantined.",
                    file.Id, ex.GetType().Name);
            }

            AddAudit(file, actorUserId, StatusMatch, result, version, attempt,
                quarantined: true, quarantineSucceeded: moved, errorCode: null);
            try
            {
                await PersistAsync(file, CsamScanStatus.Quarantined, classification, matchType,
                    quarantine: true, markScannedAt: true, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    "CSAM quarantine verdict for project file {FileId} was not re-saved after the move ({ExceptionType}).",
                    file.Id, ex.GetType().Name);
            }

            // The rows are gone; the objects go best-effort. A failed delete cannot expose them
            // because nothing addresses them any more.
            await PurgeDerivativeObjectsAsync(file, derivatives, ct);
            await PublishIndexDeleteAsync(file, ct);

            _logger?.LogWarning(
                "CSAM scan of project file {FileId} returned a match ({Classification}/{MatchType}); quarantined={Moved}.",
                file.Id, classification, matchType, moved);

            return new CsamScanOutcome(CsamScanStatus.Quarantined, classification, matchType, true, null);
        }

        AddAudit(file, actorUserId, StatusClean, result, providerVersion, attempt,
            quarantined: false, quarantineSucceeded: false, errorCode: null);
        CsamScanStatus final = await PersistAsync(file, CsamScanStatus.Clean, result.Classification, result.MatchType,
            quarantine: false, markScannedAt: true, ct);

        return new CsamScanOutcome(final, file.CsamClassification, file.CsamMatchType, final == CsamScanStatus.Quarantined, null);
    }

    // ------------------------------------------------------------------ content kind

    private enum ScanKind
    {
        Image,
        Media,
        Unsupported
    }

    /// <summary>
    /// The caller's MIME is a hint. A declared image/video routes directly; a declared known document
    /// is refused; anything else is classified from its first bytes, so a lying octet-stream cannot
    /// bypass the control. Unknown binary is media (only Arachnid sees it).
    /// </summary>
    private async Task<ScanKind> ResolveKindAsync(ProjectFile file, Attempt attempt, CancellationToken ct)
    {
        string mime = file.MimeType ?? string.Empty;
        bool declaredImage = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        bool declaredVideo = mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

        // The declared type is only a hint — including image/* and video/*: a mismatched label must
        // not force the bytes down a path that fails open unscanned. The first bytes decide when
        // they name image/media/text; an unrecognised binary falls back to the declared type.
        byte[] head = await ReadHeadAsync(file, ct);
        CsamSniffedKind sniffed = CsamMimeSniffer.Sniff(head);
        if (sniffed == CsamSniffedKind.Text)
        {
            // A declared image/video whose first 8 KiB look like text is still scanned: a crafted
            // prefix must not turn the file into an unscanned UNSUPPORTED that the uploader can
            // download. PDQ is attempted on the whole object, then /v1/media with the declared type.
            if (declaredImage)
            {
                attempt.ContentType = mime;
                return ScanKind.Image;
            }
            if (declaredVideo)
            {
                attempt.ContentType = mime;
                return ScanKind.Media;
            }

            return ScanKind.Unsupported;
        }

        if (sniffed is CsamSniffedKind.Image or CsamSniffedKind.Media)
        {
            attempt.ContentType = CsamMimeSniffer.DetectContentType(head)
                ?? (declaredImage || declaredVideo ? mime : "application/octet-stream");
            return sniffed == CsamSniffedKind.Image ? ScanKind.Image : ScanKind.Media;
        }

        if (declaredImage)
        {
            attempt.ContentType = mime;
            return ScanKind.Image;
        }
        if (declaredVideo)
        {
            attempt.ContentType = mime;
            return ScanKind.Media;
        }

        attempt.ContentType = CsamMimeSniffer.DetectContentType(head) ?? "application/octet-stream";
        return ScanKind.Media;
    }

    private async Task<byte[]> ReadHeadAsync(ProjectFile file, CancellationToken ct)
    {
        await using Stream stream = await _storage.DownloadAsync(file.ProjectId, file.StorageKey, ct);
        byte[] buffer = new byte[CsamMimeSniffer.HeadBytes];
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (chunk == 0) break;
            read += chunk;
        }

        return buffer.AsSpan(0, read).ToArray();
    }

    /// <summary>The provider call only. Everything persisted happens after it returns (or fails).</summary>
    private async Task<ArachnidScanResult> ScanProviderAsync(
        ProjectFile file, ArachnidOptions options, ScanKind kind, Attempt attempt, CancellationToken ct)
    {
        if (kind == ScanKind.Image && file.SizeBytes <= Math.Max(0, options.MaxInlineHashBytes))
        {
            byte[]? bytes = await ReadBoundedAsync(file, options.MaxInlineHashBytes, ct);
            if (bytes is not null)
            {
                attempt.ContentSha256Hex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                using MemoryStream image = new(bytes, writable: false);
                if (_hasher.TryHash(image, out string base64Hash, out int quality))
                {
                    attempt.PdqHash = base64Hash;
                    attempt.PdqQuality = quality;
                    attempt.Source = SourcePdq;
                    return await _client.ScanPdqAsync(base64Hash, ct);
                }
            }
        }

        // Video, a too-large image, or bytes the local hasher could not decode.
        attempt.Source = SourceMedia;
        await using Stream stream = await _storage.DownloadAsync(file.ProjectId, file.StorageKey, ct);
        return await _client.ScanMediaAsync(
            stream, attempt.ContentType ?? file.MimeType ?? "application/octet-stream", ct);
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/>. Returns null when the object is larger (the caller
    /// sends it to the media endpoint instead).
    /// </summary>
    private async Task<byte[]?> ReadBoundedAsync(ProjectFile file, long maxBytes, CancellationToken ct)
    {
        await using Stream stream = await _storage.DownloadAsync(file.ProjectId, file.StorageKey, ct);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private async Task<string?> TryProviderVersionAsync(CancellationToken ct)
    {
        try
        {
            return await _client.GetDatabaseVersionAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The version is evidence, not a verdict: never let it change the scan outcome.
            return null;
        }
    }

    private static bool IsMatch(string? classification) =>
        string.Equals(classification, ClassificationCsam, StringComparison.OrdinalIgnoreCase)
        || string.Equals(classification, ClassificationHarmful, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ persistence

    /// <summary>
    /// Writes the verdict and returns the state the row actually ended in. A non-quarantine outcome
    /// never overwrites a quarantined row: relational providers get an atomic conditional UPDATE,
    /// and the InMemory provider (tests) gets the same guard by re-reading before it applies.
    /// </summary>
    private async Task<CsamScanStatus> PersistAsync(
        ProjectFile file,
        CsamScanStatus intended,
        string? classification,
        string? matchType,
        bool quarantine,
        bool markScannedAt,
        CancellationToken ct)
    {
        // Central clamp: no caller (clean, fail-open, quarantine) can write provider text wider than
        // the audit columns and turn a successful scan into a 500.
        classification = Clamp(classification, MaxClassificationChars);
        matchType = Clamp(matchType, MaxMatchTypeChars);
        DateTime? scannedAt = markScannedAt ? Now() : null;

        if (_db.Database.IsRelational())
        {
            if (quarantine)
            {
                // The project's marker and the verdict commit together: a verdict never lands without it.
                async Task WriteVerdictAsync()
                {
                    await ProjectContentQuarantineMarker.StampAsync(_db, file.ProjectId, Now(), ct, stampLibrary: false);
                    await ProjectContentQuarantineMarker.StampCsamAsync(_db, file.ProjectId, Now(), ct, stampLibrary: false);
                    await _db.ProjectFiles
                        .IgnoreQueryFilters()
                        .Where(f => f.Id == file.Id)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(f => f.CsamStatus, CsamScanStatus.Quarantined)
                            .SetProperty(f => f.CsamScannedAt, scannedAt)
                            .SetProperty(f => f.CsamClassification, classification)
                            .SetProperty(f => f.CsamMatchType, matchType)
                            .SetProperty(f => f.CsamScanClaimedAt, (DateTime?)null)
                            .SetProperty(f => f.CsamQuarantineKey, file.CsamQuarantineKey)
                            .SetProperty(f => f.CsamQuarantinedAt, file.CsamQuarantinedAt)
                            .SetProperty(f => f.StorageKey, file.StorageKey), ct);
                    // The workspace avatar library's hold marker, in this same transaction: a verdict never lands without it.
                    await Avatars.Library.AvatarLibraryHoldMarker.StampProjectSetBasedAsync(_db, file.ProjectId, ct);
                }

                if (_db.Database.CurrentTransaction is not null)
                {
                    await WriteVerdictAsync();
                }
                else
                {
                    await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                    {
                        await using var tx = await _db.Database.BeginTransactionAsync(ct);
                        await WriteVerdictAsync();
                        await tx.CommitAsync(ct);
                    });
                }

                Detach(file);
                ApplyLocal(file, CsamScanStatus.Quarantined, classification, matchType, scannedAt);
                await _db.SaveChangesAsync(ct);
                return CsamScanStatus.Quarantined;
            }

            IQueryable<ProjectFile> guard = _db.ProjectFiles
                .IgnoreQueryFilters()
                .Where(f => f.Id == file.Id && f.CsamStatus != CsamScanStatus.Quarantined);

            int affected = markScannedAt
                ? await guard.ExecuteUpdateAsync(setters => setters
                    .SetProperty(f => f.CsamStatus, intended)
                    .SetProperty(f => f.CsamScannedAt, scannedAt)
                    .SetProperty(f => f.CsamClassification, classification)
                    .SetProperty(f => f.CsamMatchType, matchType)
                    .SetProperty(f => f.CsamScanClaimedAt, (DateTime?)null), ct)
                : await guard.ExecuteUpdateAsync(setters => setters
                    .SetProperty(f => f.CsamStatus, intended)
                    .SetProperty(f => f.CsamClassification, classification)
                    .SetProperty(f => f.CsamMatchType, matchType)
                    .SetProperty(f => f.CsamScanClaimedAt, (DateTime?)null), ct);

            ProjectFile? current = await _db.ProjectFiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == file.Id, ct);

            Detach(file);
            if (affected == 0 && current is not null)
            {
                // Another scan quarantined the row after this one started: keep its verdict and
                // fields, only the audit records this attempt.
                CopyCurrent(file, current);
            }
            else
            {
                ApplyLocal(file, intended, classification, matchType, scannedAt);
            }

            await _db.SaveChangesAsync(ct);
            return current?.CsamStatus ?? intended;
        }

        // InMemory (tests) and any other provider without ExecuteUpdate: the same guard, re-read
        // under the guard. There is no real concurrency here, but the semantics are identical.
        ProjectFile? inMemoryCurrent = await _db.ProjectFiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == file.Id, ct);

        if (!quarantine && inMemoryCurrent is not null && inMemoryCurrent.CsamStatus == CsamScanStatus.Quarantined)
        {
            Detach(file);
            CopyCurrent(file, inMemoryCurrent);
            await _db.SaveChangesAsync(ct);
            return CsamScanStatus.Quarantined;
        }

        if (quarantine)
        {
            file.CsamStatus = CsamScanStatus.Quarantined;
            file.CsamQuarantinedAt ??= Now();
            await ProjectContentQuarantineMarker.StageAsync(_db, file.ProjectId, Now(), ct);
            await ProjectContentQuarantineMarker.StageCsamAsync(_db, file.ProjectId, Now(), ct);
        }
        else
        {
            file.CsamStatus = intended;
        }

        file.CsamClassification = classification;
        file.CsamMatchType = matchType;
        file.CsamScanClaimedAt = null;
        if (markScannedAt) file.CsamScannedAt = scannedAt;

        await _db.SaveChangesAsync(ct);
        return file.CsamStatus;
    }

    private void Detach(ProjectFile file)
    {
        if (_db.Entry(file).State != EntityState.Detached)
            _db.Entry(file).State = EntityState.Detached;
    }

    private static void ApplyLocal(
        ProjectFile file, CsamScanStatus status, string? classification, string? matchType, DateTime? scannedAt)
    {
        file.CsamStatus = status;
        file.CsamClassification = classification;
        file.CsamMatchType = matchType;
        file.CsamScanClaimedAt = null;
        if (scannedAt is not null) file.CsamScannedAt = scannedAt;
    }

    private static void CopyCurrent(ProjectFile file, ProjectFile current)
    {
        file.CsamStatus = current.CsamStatus;
        file.CsamScannedAt = current.CsamScannedAt;
        file.CsamScanClaimedAt = current.CsamScanClaimedAt;
        file.CsamClassification = current.CsamClassification;
        file.CsamMatchType = current.CsamMatchType;
        file.CsamQuarantineKey = current.CsamQuarantineKey;
        file.CsamQuarantinedAt = current.CsamQuarantinedAt;
        file.StorageKey = current.StorageKey;
    }

    private async Task<CsamScanOutcome> FailOpenAsync(
        ProjectFile file,
        Guid? actorUserId,
        string errorCode,
        string source,
        string? pdqHash,
        int? pdqQuality,
        string? contentSha256Hex,
        bool wasQuarantined,
        CancellationToken ct)
    {
        AddAudit(file, actorUserId, StatusNotScanned, result: null, providerVersion: null,
            new Attempt { Source = source, PdqHash = pdqHash, PdqQuality = pdqQuality ?? 0, ContentSha256Hex = contentSha256Hex },
            quarantined: wasQuarantined,
            quarantineSucceeded: wasQuarantined && file.CsamQuarantineKey is not null,
            errorCode: errorCode);

        CsamScanStatus final = await PersistAsync(file, CsamScanStatus.NotScanned, null, null,
            quarantine: false, markScannedAt: true, ct);

        // One warning, no content and no token: exactly the file id and the machine-readable reason.
        _logger?.LogWarning(
            "CSAM scan of project file {FileId} failed open ({ErrorCode}); the upload proceeds.",
            file.Id, errorCode);

        return new CsamScanOutcome(final, file.CsamClassification, file.CsamMatchType, final == CsamScanStatus.Quarantined, errorCode);
    }

    private void AddAudit(
        ProjectFile file,
        Guid? actorUserId,
        string status,
        ArachnidScanResult? result,
        string? providerVersion,
        Attempt attempt,
        bool quarantined,
        bool quarantineSucceeded,
        string? errorCode)
    {
        _db.CsamScanAudits.Add(new CsamScanAudit
        {
            Id = Guid.NewGuid(),
            ProjectId = file.ProjectId,
            ProjectFileId = file.Id,
            ProjectFileName = file.OriginalFileName,
            Status = status,
            Classification = Clamp(result?.Classification, MaxClassificationChars),
            MatchType = Clamp(result?.MatchType, MaxMatchTypeChars),
            NearMatchCount = result?.NearMatchCount,
            PdqHashBase64 = attempt.PdqHash,
            PdqQuality = attempt.PdqHash is null ? null : attempt.PdqQuality,
            ContentSha256Hex = Clamp(attempt.ContentSha256Hex ?? result?.Sha256Hex, 64),
            Source = attempt.Source,
            ProviderVersion = Clamp(providerVersion, MaxProviderVersionChars),
            Quarantined = quarantined,
            QuarantineSucceeded = quarantineSucceeded,
            ErrorCode = Clamp(errorCode, MaxErrorCodeChars),
            ActorUserId = actorUserId,
            CreatedAt = Now()
        });
    }

    // ------------------------------------------------------------------ after a match

    /// <summary>
    /// Best effort: delete the derivative objects whose ROWS were already removed in the verdict
    /// save. A failed object delete is harmless — no row addresses the object any more.
    /// </summary>
    private async Task PurgeDerivativeObjectsAsync(
        ProjectFile file, IReadOnlyList<MediaDerivative> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        try
        {
            List<string> keys = rows
                .SelectMany(d => new[] { d.StorageKey, d.IndexStorageKey })
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (keys.Count > 0) await _storage.DeleteManyAsync(file.ProjectId, keys, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                "Could not delete the media derivative objects of quarantined project file {FileId} ({ExceptionType}); their rows are already gone.",
                file.Id, ex.GetType().Name);
        }
    }

    /// <summary>Best effort: remove any pre-quarantine chunks from the vector index.</summary>
    private async Task PublishIndexDeleteAsync(ProjectFile file, CancellationToken ct)
    {
        if (_publisher is null) return;

        try
        {
            await _publisher.Publish(new ProjectFileIndexingRequested
            {
                ProjectId = file.ProjectId,
                FileId = file.Id,
                Operation = "Delete",
                RequestedAt = Now()
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never fail the scan for a broker outage; the sweeper's re-scan republishes it.
            _logger?.LogWarning(
                "Could not publish the CSAM index delete for project file {FileId} ({ExceptionType}).",
                file.Id, ex.GetType().Name);
        }
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>What the provider call resolved to, carried out for the audit.</summary>
    private sealed class Attempt
    {
        public string Source { get; set; } = SourceNone;
        public string? ContentType { get; set; }
        public string? PdqHash { get; set; }
        public int PdqQuality { get; set; }
        public string? ContentSha256Hex { get; set; }
    }
}
