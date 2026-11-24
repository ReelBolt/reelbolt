using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Observability;

namespace ReelBolt.Inference.Api.Services.Uploads;

/// <summary>
/// Imports one file from a public https URL into a project's <c>userFiles</c>, synchronously, for a
/// caller that already holds Edit on the project. The REST route and the assistant's
/// <c>ApplyImportFileFromUrl</c> both call this, so the SSRF rules, caps and quota are one code path.
/// </summary>
public interface IUrlImportService
{
    Task<UrlImportResult> ImportAsync(Project project, Guid userId, string url, string? fileName, string? directoryPath, CancellationToken ct);
}

/// <summary><c>FileImport</c> configuration.</summary>
public sealed class UrlImportOptions
{
    public const string SectionName = "FileImport";

    /// <summary>The most bytes one import may store (default 512 MiB), judged on the declared length and while streaming.</summary>
    public long MaxBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>The whole import — every redirect hop and the full body — must finish within this (default 300 s).</summary>
    public double TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// S-32: the longest the upstream may send nothing — before the response headers, or between two reads of the
    /// body — before the import is refused IMPORT_TIMEOUT and its slot returned (default 30 s, well inside
    /// <see cref="TimeoutSeconds"/>), so a stalled upstream cannot hold a slot for the whole import deadline.
    /// </summary>
    public double StallTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// S-41: once <see cref="ThroughputGraceSeconds"/> have passed since the request was sent, the import's average
    /// throughput must stay at or above this many bytes per second (default 32 KiB/s), or it is refused IMPORT_TIMEOUT
    /// and its slot returned: a byte trickle re-arms the stall timer forever, but cannot keep up this average.
    /// A non-positive value means the default.
    /// </summary>
    public long MinBytesPerSecond { get; set; } = 32768;

    /// <summary>S-41: how long an import may run before <see cref="MinBytesPerSecond"/> applies (default 30 s).</summary>
    public double ThroughputGraceSeconds { get; set; } = 30;

    /// <summary>S-06: imports one organization may run at once on this process (default 2); more answer IMPORT_BUSY.</summary>
    public int MaxConcurrentPerOrg { get; set; } = 2;

    /// <summary>S-06: imports this process runs at once across all organizations (default 4); more answer IMPORT_BUSY.</summary>
    public int MaxConcurrent { get; set; } = 4;

    /// <summary>Where a body is spooled while it streams (default: the system temp directory).</summary>
    public string? SpoolDirectory { get; set; }

    /// <summary>
    /// Free space the spool directory must keep beyond what the running imports may still write
    /// (default 1 GiB); an import that would eat into it answers IMPORT_BUSY before any byte is fetched.
    /// </summary>
    public long SpoolMinFreeBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>
    /// QA-P0-27: the quota bytes one import reserves up front, before it knows its size. The stream cap can be the
    /// whole <see cref="MaxBytes"/> ceiling, and reserving the ceiling refuses a parallel import in the organization
    /// that has any less headroom than the ceiling even when this import's actual download is small. A bounded floor is
    /// held while the body streams; the commit-time re-check under the organization lock still fails closed, so the
    /// reservation is an early refusal, not the quota. The spool reservation stays at the cap (the most this import
    /// may write). A non-positive value means the default.
    /// </summary>
    public long ReservationFloorBytes { get; set; } = 16L * 1024 * 1024;
}

/// <summary>Stable machine codes of an import refusal.</summary>
public static class UrlImportCodes
{
    public const string UrlRefused = "IMPORT_URL_REFUSED";
    public const string TooLarge = "IMPORT_TOO_LARGE";
    public const string Failed = "IMPORT_FAILED";
    public const string Timeout = "IMPORT_TIMEOUT";
    public const string Busy = "IMPORT_BUSY";
    public const string InvalidPath = "INVALID_PATH";
}

/// <summary>
/// Why an import stored nothing. <see cref="StatusCode"/> is the REST status: 400 (URL or path), 413
/// (too large, quota), 422 (upstream failure, timeout) — never 502/504, which the CDN in front of the API
/// would replace with its own error page.
/// </summary>
public sealed record UrlImportRefusal(
    string Code,
    string Message,
    int StatusCode,
    int? UpstreamStatus = null,
    long? LimitBytes = null,
    long? UsedBytes = null)
{
    public static UrlImportRefusal UrlRefused(string message) => new(UrlImportCodes.UrlRefused, message, StatusCodes.Status400BadRequest);

    public static UrlImportRefusal InvalidPath(string message) => new(UrlImportCodes.InvalidPath, message, StatusCodes.Status400BadRequest);

    public static UrlImportRefusal TooLarge(long limit) =>
        new(UrlImportCodes.TooLarge, $"The file is larger than the {limit}-byte import limit. Upload it instead.", StatusCodes.Status413PayloadTooLarge, LimitBytes: limit);

    public static UrlImportRefusal Failed(string message, int? upstreamStatus = null) =>
        new(UrlImportCodes.Failed, message, StatusCodes.Status422UnprocessableEntity, upstreamStatus);

    public static UrlImportRefusal TimedOut(double seconds) =>
        new(UrlImportCodes.Timeout, $"The download did not finish within {seconds:0.#} seconds.", StatusCodes.Status422UnprocessableEntity);

    /// <summary>S-32: the upstream sent nothing for <paramref name="seconds"/> (no headers, or no further body bytes).</summary>
    public static UrlImportRefusal Stalled(double seconds) =>
        new(UrlImportCodes.Timeout, $"The server sent nothing for {seconds:0.#} seconds, so the download was stopped.", StatusCodes.Status422UnprocessableEntity);

    /// <summary>S-41: past the grace period, the average throughput fell under <paramref name="minBytesPerSecond"/>.</summary>
    public static UrlImportRefusal TooSlow(long minBytesPerSecond) =>
        new(UrlImportCodes.Timeout, $"The server sent the file too slowly (under {minBytesPerSecond} bytes per second), so the download was stopped.", StatusCodes.Status422UnprocessableEntity);

    /// <summary>Too many imports running, or no spool space: nothing was fetched; the caller should retry later (429).</summary>
    public static UrlImportRefusal Busy(string message) => new(UrlImportCodes.Busy, message, StatusCodes.Status429TooManyRequests);

    public static UrlImportRefusal Quota(PlanGateViolation violation) =>
        new(PlanGate.StorageCode, violation.Message, violation.StatusCode, LimitBytes: violation.Limit, UsedBytes: violation.Current);
}

/// <summary>The recorded file, or the refusal (and then nothing was stored).</summary>
public sealed record UrlImportResult(ProjectFile? File, UrlImportRefusal? Refusal)
{
    public static UrlImportResult Ok(ProjectFile file) => new(file, null);

    public static UrlImportResult Refused(UrlImportRefusal refusal) => new(null, refusal);
}

/// <summary>
/// <para>
/// <b>SSRF.</b> The URL must pass <see cref="UrlImportTarget.ValidateUrl"/> (https, no credentials, host
/// not private/internal/dotless). Redirects are followed HERE, at most <see cref="MaxRedirects"/>, and
/// every target is re-validated the same way before it is requested (so no downgrade to http and no
/// literal private hop). Underneath, <see cref="GeneratedClipUrlGuard.CreateUserUrlHandler"/> checks the
/// IP actually dialled on every connection (DNS rebinding) and uses no proxy. No cookie and no
/// Authorization header is ever sent.
/// </para>
/// <para>
/// <b>Size and quota.</b> A declared Content-Length above <see cref="UrlImportOptions.MaxBytes"/> or the
/// plan's headroom is refused before the body is read; the body is then streamed through a
/// <see cref="LimitedReadStream"/> into a delete-on-close temp file (never buffered in memory), the
/// quota is re-checked on the bytes actually received, and only then is the object written. A failure
/// while writing deletes the partial object; a failure recording the row deletes the object.
/// </para>
/// <para>
/// <b>Concurrency and reservation (S-06).</b> Each import takes a slot in <see cref="UrlImportLimiter"/>
/// (<see cref="UrlImportOptions.MaxConcurrentPerOrg"/>, <see cref="UrlImportOptions.MaxConcurrent"/>;
/// full = <c>IMPORT_BUSY</c>). Under the organization's limiter lock it measures the quota headroom left after
/// the other running imports' reservations, caps its stream at min(MaxBytes, that headroom) and reserves
/// it until it ends, so parallel imports on one replica cannot overshoot together. The object upload then
/// runs with no storage lock at all; the authoritative quota re-check on the real size and the row write run
/// under a short <see cref="OrganizationStorageGuard"/>, whose cross-replica guarantee is the PostgreSQL
/// advisory lock, so two replicas cannot both admit an import that only one fits (T-106). The spool
/// directory's free space is checked first.
/// </para>
/// <para>
/// <b>Logging.</b> Only scheme and host are logged: a signed URL's path and query are its secret.
/// </para>
/// </summary>
public sealed class UrlImportService : IUrlImportService
{
    public const int MaxRedirects = 3;

    private const string Category = "userFiles";

    // One pooled client over the guarded handler for the process (the handler validates per connection).
    private static readonly Lazy<HttpClient> SharedClient = new(() =>
        new HttpClient(GeneratedClipUrlGuard.CreateUserUrlHandler(), disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan });

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly IProjectFileAddedEffects _effects;
    private readonly UrlImportOptions _options;
    private readonly ILogger<UrlImportService> _logger;
    private readonly IPlanGateService? _planGate;
    private readonly HttpClient _http;
    private readonly UrlImportLimiter _limiter;
    private readonly OrganizationStorageLock _storageLock;

    public UrlImportService(
        InferenceApiDbContext db,
        IFileStorageService storage,
        IProjectFileAddedEffects effects,
        IOptions<UrlImportOptions> options,
        ILogger<UrlImportService> logger,
        IPlanGateService? planGate = null,
        OrganizationStorageLock? storageLock = null)
        : this(db, storage, effects, options, logger, planGate, SharedClient.Value, UrlImportLimiter.Shared, storageLock)
    {
    }

    /// <summary>Test seam: <paramref name="upstream"/> stands in for the network BELOW this class's own checks.</summary>
    internal UrlImportService(
        InferenceApiDbContext db,
        IFileStorageService storage,
        IProjectFileAddedEffects effects,
        IOptions<UrlImportOptions> options,
        ILogger<UrlImportService> logger,
        IPlanGateService? planGate,
        HttpMessageHandler upstream,
        UrlImportLimiter? limiter = null,
        OrganizationStorageLock? storageLock = null)
        : this(db, storage, effects, options, logger, planGate, new HttpClient(upstream, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }, limiter ?? new UrlImportLimiter(), storageLock)
    {
    }

    private UrlImportService(
        InferenceApiDbContext db,
        IFileStorageService storage,
        IProjectFileAddedEffects effects,
        IOptions<UrlImportOptions> options,
        ILogger<UrlImportService> logger,
        IPlanGateService? planGate,
        HttpClient http,
        UrlImportLimiter limiter,
        OrganizationStorageLock? storageLock = null)
    {
        _limiter = limiter;
        _db = db;
        _storage = storage;
        _effects = effects;
        _options = options.Value;
        _logger = logger;
        _planGate = planGate;
        _http = http;
        // Optional so every existing test keeps compiling; DI injects the singleton, so a URL import and
        // a delete of one organization share the same per-replica semaphore.
        _storageLock = storageLock ?? new OrganizationStorageLock();
    }

    public async Task<UrlImportResult> ImportAsync(
        Project project, Guid userId, string url, string? fileName, string? directoryPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (UrlImportTarget.ValidateUrl(url, out Uri? source) is { } badUrl)
            return Refuse(badUrl, origin: null);
        if (UrlImportTarget.ResolveTarget(source!, fileName, directoryPath, out UrlImportPath? target) is { } badPath)
            return Refuse(badPath, UrlImportTarget.Origin(source!));

        string origin = UrlImportTarget.Origin(source!);

        using UrlImportLimiter.Lease? lease = _limiter.TryEnter(project.OrganizationId, _options.MaxConcurrent, _options.MaxConcurrentPerOrg);
        if (lease is null)
            return Refuse(UrlImportRefusal.Busy("Too many imports are running right now; try again in a minute."), origin);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        CancellationToken token = timeout.Token;
        // S-32: the network calls also run under a stall timer, re-armed by every byte received, so an upstream that
        // goes quiet gives its slot back after StallTimeoutSeconds instead of holding it for the whole deadline.
        TimeSpan stallAfter = StallAfter();
        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(token);

        long cap = _options.MaxBytes;
        UrlImportRefusal? quotaCap = null;
        FileStream? spool = null;
        try
        {
            using (await lease.LockOrganizationAsync(token))
            {
                (cap, quotaCap, bool noHeadroom) = await ReserveAsync(project, lease, token);
                if (noHeadroom) return Refuse(quotaCap!, origin);
            }

            string spoolDirectory = _options.SpoolDirectory is { Length: > 0 } configured ? configured : Path.GetTempPath();
            if (!HasSpoolRoom(spoolDirectory, cap + lease.SpoolReservedByOthers + Math.Max(0, _options.SpoolMinFreeBytes)))
                return Refuse(UrlImportRefusal.Busy("The server has no room for another import right now; try again later."), origin);

            // S-41: the throughput floor is measured from the request, so header wait counts against it too.
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            long received = 0;
            stall.CancelAfter(stallAfter);
            (HttpResponseMessage? response, UrlImportRefusal? fetchRefusal) = await FetchAsync(source!, stall.Token);
            if (fetchRefusal is not null)
                return Refuse(fetchRefusal, origin);

            using (response)
            {
                // Above the stream cap: "too large" when the cap is the import limit, a quota refusal when
                // the cap is what is left of the plan.
                if (response!.Content.Headers.ContentLength is { } declared && declared > cap)
                    return Refuse(quotaCap ?? UrlImportRefusal.TooLarge(cap), origin);

                spool = new FileStream(
                    Path.Combine(spoolDirectory, $"reelbolt-import-{Guid.NewGuid():N}.part"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                    FileOptions.DeleteOnClose | FileOptions.Asynchronous);

                stall.CancelAfter(stallAfter);
                Stream body = await response.Content.ReadAsStreamAsync(stall.Token);
                await using (LimitedReadStream limited = new(body, cap, response))
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = await limited.ReadAsync(buffer, stall.Token)) > 0)
                    {
                        received += read;
                        if (BelowThroughputFloor(received, elapsed.Elapsed))
                            return Refuse(UrlImportRefusal.TooSlow(MinBytesPerSecond()), origin);
                        stall.CancelAfter(stallAfter);
                        await spool.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                }

                stall.CancelAfter(Timeout.InfiniteTimeSpan);

                string mimeType = ResolveMimeType(target!.FileName, response.Content.Headers.ContentType);
                // Issue 8: a .ts the extension map calls a transport stream is decided by its bytes.
                spool.Position = 0;
                mimeType = await TransportStreamMimeSniffer.ResolveAsync(mimeType, target.FileName, spool, token) ?? mimeType;
                // The byte streaming is over: the object upload runs with no storage lock at all, and the
                // authoritative quota re-check on the real size plus the row write run under the short guard
                // StoreAsync takes (D-2/D-3).
                return await StoreAsync(project, userId, target, mimeType, spool, origin, token);
            }
        }
        catch (StreamLimitExceededException)
        {
            return Refuse(quotaCap ?? UrlImportRefusal.TooLarge(cap), origin);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Refuse(timeout.IsCancellationRequested
                ? UrlImportRefusal.TimedOut(_options.TimeoutSeconds)
                : UrlImportRefusal.Stalled(stallAfter.TotalSeconds), origin);
        }
        catch (HttpRequestException ex) when (IsNonPublic(ex))
        {
            return Refuse(UrlImportRefusal.UrlRefused("The URL's host is not a public address."), origin);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Refuse(UrlImportRefusal.Failed("The file could not be downloaded."), origin, ex);
        }
        finally
        {
            if (spool is not null) await spool.DisposeAsync();
        }
    }

    /// <summary>S-41: a non-positive floor means the default.</summary>
    private long MinBytesPerSecond() =>
        _options.MinBytesPerSecond > 0 ? _options.MinBytesPerSecond : new UrlImportOptions().MinBytesPerSecond;

    /// <summary>S-41: true once the grace period is over and the average since the request is under the floor.</summary>
    private bool BelowThroughputFloor(long received, TimeSpan elapsed)
    {
        double grace = _options.ThroughputGraceSeconds >= 0 ? _options.ThroughputGraceSeconds : new UrlImportOptions().ThroughputGraceSeconds;
        return elapsed.TotalSeconds > grace && received < MinBytesPerSecond() * elapsed.TotalSeconds;
    }

    /// <summary>The stall timeout: never longer than the whole-import deadline; a non-positive setting means the default.</summary>
    private TimeSpan StallAfter()
    {
        double seconds = _options.StallTimeoutSeconds > 0 ? _options.StallTimeoutSeconds : new UrlImportOptions().StallTimeoutSeconds;
        return TimeSpan.FromSeconds(Math.Min(seconds, Math.Max(0.001, _options.TimeoutSeconds)));
    }

    /// <summary>GET with manual redirects: every hop is re-validated before it is requested.</summary>
    private async Task<(HttpResponseMessage? Response, UrlImportRefusal? Refusal)> FetchAsync(Uri source, CancellationToken ct)
    {
        Uri current = source;
        for (int hop = 0; ; hop++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("ReelBolt-FileImport/1.0");
            // S-08: the OTel client span of this hop exports the origin only, as the logs do.
            request.Options.Set(ReelBoltTelemetry.OriginOnlyUrl, UrlImportTarget.Origin(current));
            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (IsRedirect(response.StatusCode))
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                    return (null, UrlImportRefusal.Failed("The server answered with a redirect without a location."));
                if (hop >= MaxRedirects)
                    return (null, UrlImportRefusal.Failed($"The URL redirected more than {MaxRedirects} times."));

                Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (UrlImportTarget.ValidateUrl(next.AbsoluteUri, out Uri? allowed) is { } refusal)
                {
                    _logger.LogWarning("URL import refused a redirect from {Origin} to {Target}.", UrlImportTarget.Origin(current), SafeOrigin(next));
                    return (null, refusal with { Message = "The URL redirected to an address that is not allowed: " + refusal.Message });
                }

                current = allowed!;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                response.Dispose();
                return (null, UrlImportRefusal.Failed($"The server answered {status}.", status));
            }

            return (response, null);
        }
    }

    private async Task<UrlImportResult> StoreAsync(
        Project project, Guid userId, UrlImportPath target, string mimeType, FileStream spool, string origin, CancellationToken ct)
    {
        long size = spool.Length;
        // Pre-check with no lock, so a refusal still writes nothing (the pinned behaviour of this route). It is
        // not the guarantee: the authoritative check on the same real size runs under the guard below, after the
        // upload, so a replica that loses the race deletes the object it wrote rather than keeping it.
        if (await QuotaRefusalAsync(project, size, ct) is { } overQuota)
            return Refuse(overQuota, origin);

        spool.Position = 0;
        Guid fileId = Guid.NewGuid();
        Dictionary<string, string> metadata = new()
        {
            ["project-id"] = project.Id.ToString(),
            ["owner-id"] = project.OwnerId.ToString(),
            ["org-id"] = project.OrganizationId.ToString(),
            ["uploaded-by"] = userId.ToString(),
            ["project-file-id"] = fileId.ToString(),
            ["source-origin"] = origin
        };

        StoredFileObject stored;
        try
        {
            // No storage lock: the byte streaming of the upload must never hold one.
            stored = await _storage.UploadAsync(project.Id, fileId, spool, target.FileName, mimeType, metadata, ct, Category, target.OriginalPath);
        }
        catch (Exception ex)
        {
            // Whatever the write left behind (a partial object, or a whole one whose answer was lost) goes.
            if (DirectUploadService.DeriveKey(project.Id, fileId, target.FileName, target.OriginalPath) is { } partialKey)
                await DeleteQuietlyAsync(project, partialKey, Category);
            if (ex is OperationCanceledException) throw;
            return Refuse(UrlImportRefusal.Failed("The file could not be stored."), origin, ex);
        }

        bool media = DirectUploadService.IsVideoOrAudio(mimeType);
        ProjectFile row = new()
        {
            Id = fileId,
            ProjectId = project.Id,
            OriginalFileName = stored.OriginalFileName,
            OriginalPath = stored.OriginalPath,
            DirectoryPath = stored.DirectoryPath,
            Category = Category,
            StorageKey = stored.StorageKey,
            StorageFileName = stored.StorageFileName,
            StorageBucket = stored.BucketName,
            StoragePrefix = stored.StoragePrefix,
            StorageMetadataJson = stored.StorageMetadataJson,
            MimeType = mimeType,
            SizeBytes = size,
            SummaryStatus = media ? SummaryStatus.Done : SummaryStatus.Pending,
            IndexingStatus = media ? FileIndexingStatus.NotIndexed : FileIndexingStatus.Pending,
            UploadedAt = DateTime.UtcNow
        };

        // The authoritative re-check on the real size and the row write are one critical section under the
        // organization's storage guard, whose cross-replica guarantee is the advisory lock: a second replica's
        // re-check runs only after this one committed, so two replicas cannot both admit a quota that only one
        // fits. The upload above ran outside any lock, so no path holds one across the byte streaming.
        //
        // Every failure from here on deletes the object the upload already wrote - taking the guard can throw
        // (a connection, a cancellation) and so can the re-check (an unreachable plan gate), and neither may
        // leave an orphan behind. DeleteQuietlyAsync is tokenless, so cleanup itself cannot be cancelled.
        //
        // The cleanup runs after the guard is released on every path, including a refusal: a DELETE is one
        // bounded round trip, but it is still network I/O, and no part of a refusal needs the organization's
        // advisory lock (shared with every delete and import of that organization) held while the object
        // store answers.
        UrlImportRefusal? refusedUnderGuard = null;
        try
        {
            await using OrganizationStorageGuard storageGuard = await _storageLock.AcquireAsync(_db, project.OrganizationId, ct);
            refusedUnderGuard = await QuotaRefusalAsync(project, size, ct);
            if (refusedUnderGuard is null)
            {
                try
                {
                    _db.ProjectFiles.Add(row);
                    await _db.SaveChangesAsync(ct);
                    await storageGuard.CommitAsync(ct);
                }
                catch
                {
                    _db.Entry(row).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
                    throw;
                }
            }
        }
        catch
        {
            await DeleteQuietlyAsync(project, stored.StorageKey, stored.Category);
            throw;
        }

        if (refusedUnderGuard is not null)
        {
            await DeleteQuietlyAsync(project, stored.StorageKey, stored.Category);
            return Refuse(refusedUnderGuard, origin);
        }

        _logger.LogInformation(
            "Imported {SizeBytes} bytes from {Origin} into project {ProjectId} as file {ProjectFileId}.",
            size, origin, project.Id, row.Id);

        // Outside the guard: the row that records the object is durable before the side effects run.
        await _effects.RunAsync(row, userId, ct);
        return UrlImportResult.Ok(row);
    }

    /// <summary>
    /// The stream cap: <see cref="UrlImportOptions.MaxBytes"/>, or the plan's headroom net of the other
    /// running imports' reservations when that is smaller (then also the refusal to answer if the body
    /// reaches it). Reserves a bounded quota floor for this import (QA-P0-27) and the full cap of the spool.
    /// Runs under the organization's lock.
    /// </summary>
    private async Task<(long Cap, UrlImportRefusal? QuotaCap, bool NoHeadroom)> ReserveAsync(Project project, UrlImportLimiter.Lease lease, CancellationToken ct)
    {
        long cap = Math.Max(0, _options.MaxBytes);
        long others = lease.ReservedByOthers;
        UrlImportRefusal? quotaCap = null;
        if (_planGate is not null && await _planGate.CheckUploadAsync(project.OrganizationId, others + cap, ct) is { } violation)
        {
            // Fails closed: a violation without a limit or usage leaves no headroom.
            long headroom = violation.Limit is { } limit && violation.Current is { } used ? limit - used - others : 0;
            quotaCap = UrlImportRefusal.Quota(violation);
            if (headroom <= 0) return (0, quotaCap, true);
            cap = Math.Min(cap, headroom);
        }

        // QA-P0-27: hold only a bounded floor of the quota while the body streams (the actual size is unknown and the
        // commit re-checks it), so a parallel import is not refused just because this one's ceiling is the whole
        // MaxBytes. The spool still reserves the full cap, the most this import may write.
        lease.Reserve(Math.Min(cap, ReservationFloor()), cap);
        return (cap, quotaCap, false);
    }

    /// <summary>QA-P0-27: the quota floor one import reserves before its size is known; a non-positive setting means the default.</summary>
    private long ReservationFloor() =>
        _options.ReservationFloorBytes > 0 ? _options.ReservationFloorBytes : new UrlImportOptions().ReservationFloorBytes;

    private bool HasSpoolRoom(string directory, long requiredBytes)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return new DriveInfo(Path.GetFullPath(directory)).AvailableFreeSpace >= requiredBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Fails closed: a spool whose free space cannot be read takes no import.
            _logger.LogWarning("URL import spool directory is unusable ({Error}).", ex.GetType().Name);
            return false;
        }
    }

    private async Task<UrlImportRefusal?> QuotaRefusalAsync(Project project, long bytes, CancellationToken ct) =>
        _planGate is not null && bytes > 0 && await _planGate.CheckUploadAsync(project.OrganizationId, bytes, ct) is { } violation
            ? UrlImportRefusal.Quota(violation)
            : null;

    /// <summary>Extension first (what the file IS to the platform), then the server's type, then octet-stream.</summary>
    private static string ResolveMimeType(string fileName, MediaTypeHeaderValue? served)
    {
        if (ContentTypes.TryGetContentType(fileName, out string? byExtension))
            return byExtension;
        string? mediaType = served?.MediaType;
        return string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType.ToLowerInvariant();
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsNonPublic(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is NonPublicAddressException) return true;
        return false;
    }

    private static string SafeOrigin(Uri uri) => uri.IsAbsoluteUri ? UrlImportTarget.Origin(uri) : "(relative)";

    private UrlImportResult Refuse(UrlImportRefusal refusal, string? origin, Exception? cause = null)
    {
        // The exception's type only: an exception message can quote a URL, and a URL's path and query are secret.
        _logger.LogWarning(
            "URL import from {Origin} refused: {Code} (upstream {UpstreamStatus}){Cause}.",
            origin ?? "(invalid url)", refusal.Code, refusal.UpstreamStatus, cause is null ? string.Empty : $" after {cause.GetType().Name}");
        return UrlImportResult.Refused(refusal);
    }

    private async Task DeleteQuietlyAsync(Project project, string key, string category)
    {
        try
        {
            await _storage.DeleteAsync(project.Id, key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // SQCR-S-09: an object this import wrote and could not remove must not be left to nothing. A
            // durable mark makes the retention sweep retry it (the sweep purges a mark in any category).
            _logger.LogWarning(
                "Could not delete a partial import object in project {ProjectId} ({Error}); a retention mark will retry it.",
                project.Id, ex.GetType().Name);
            await MarkForSweepAsync(project, key, category);
        }
    }

    /// <summary>
    /// Records one durable deletion mark for an object no row names, so the retention sweep removes it.
    /// Never throws: a failed mark is logged, and the orphan sweep (which lists <c>userFiles</c>) is the
    /// second net.
    /// </summary>
    private async Task MarkForSweepAsync(Project project, string key, string category)
    {
        try
        {
            if (await _db.StorageRetentionMarks.AnyAsync(m => m.StorageKey == key, CancellationToken.None))
                return;   // the storage key is unique on the mark table; an existing mark already covers it

            _db.StorageRetentionMarks.Add(new StorageRetentionMark
            {
                Id = Guid.NewGuid(),
                OrganizationId = project.OrganizationId,
                ProjectId = project.Id,
                StorageKey = key,
                Category = category,
                MarkedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record a retention mark for an orphaned import object in project {ProjectId}.", project.Id);
        }
    }
}
