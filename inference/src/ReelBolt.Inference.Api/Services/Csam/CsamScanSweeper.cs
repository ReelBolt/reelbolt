using System.Collections.Concurrent;
using System.Threading.Channels;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Csam;

/// <summary>
/// Asks the CSAM sweeper to scan one project's waiting rows now instead of at its next interval. The output-moderation
/// lane asks when a project's outputs are held by a scan that is still pending: the lane may send nothing to the vendor
/// until every such file has a verdict (docs/csam.md), and the interval (15 minutes by default) was the wait every new
/// render had. Never blocks; one waiting request per project.
/// </summary>
public interface ICsamScanRequests
{
    /// <summary>Queues the project. False when it is already waiting or the queue is full (the interval sweep still runs).</summary>
    bool Request(Guid projectId);
}

/// <inheritdoc />
public sealed class CsamScanRequests : ICsamScanRequests
{
    /// <summary>How many projects may wait at once; more are left to the interval sweep.</summary>
    public const int Capacity = 256;

    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly ConcurrentDictionary<Guid, byte> _waiting = new();

    public bool Request(Guid projectId)
    {
        if (!_waiting.TryAdd(projectId, 0)) return false;
        if (_channel.Writer.TryWrite(projectId)) return true;

        _waiting.TryRemove(projectId, out _);
        return false;
    }

    /// <summary>Takes the next waiting project, if any; it may then be requested again.</summary>
    public bool TryTake(out Guid projectId)
    {
        if (!_channel.Reader.TryRead(out projectId)) return false;
        _waiting.TryRemove(projectId, out _);
        return true;
    }

    /// <summary>Waits until a request may be taken.</summary>
    public ValueTask<bool> WaitToTakeAsync(CancellationToken ct) => _channel.Reader.WaitToReadAsync(ct);
}

/// <summary>
/// Scans rows no in-request path reached: cloud drive and GitHub sync, assistant output
/// registration, and WorkflowEngine output registration all insert a <see cref="ProjectFile"/> with
/// the <see cref="CsamScanStatus.Pending"/> initializer and never run the API's post-add effects.
/// <para>
/// One batch per interval, one DI scope per file (so one failure cannot abort the batch), and
/// <c>IgnoreQueryFilters()</c> on the lookup because there is no tenant context in a background
/// process. Optionally retries <see cref="CsamScanStatus.NotScanned"/> rows after 24 hours.
/// </para>
/// <para>
/// Also serves <see cref="ICsamScanRequests"/> (<see cref="ScanProjectAsync"/>): a project whose outputs the moderation
/// lane holds for a pending scan is scanned at once instead of at the next interval, and the lane is woken afterwards.
/// </para>
/// </summary>
public sealed class CsamScanSweeper : BackgroundService
{
    /// <summary>Lets migrations and seeding finish before the first sweep.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

    /// <summary>A row must be this old before it is swept, so the in-request scan wins the race.</summary>
    public static readonly TimeSpan PendingGrace = TimeSpan.FromMinutes(1);

    /// <summary>How old a <see cref="CsamScanStatus.NotScanned"/> row must be before a retry.</summary>
    public static readonly TimeSpan NotScannedRescanAge = TimeSpan.FromHours(24);

    /// <summary>A claim older than this belongs to a crashed worker and may be taken again.</summary>
    public static readonly TimeSpan ClaimStaleAfter = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<CsamOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<CsamScanSweeper> _logger;
    private readonly CsamScanRequests? _requests;
    private readonly IOutputModerationWake? _wake;
    private readonly TimeSpan _startupDelay;

    public CsamScanSweeper(
        IServiceScopeFactory scopeFactory,
        IOptions<CsamOptions> options,
        ILogger<CsamScanSweeper> logger,
        TimeProvider? clock = null,
        CsamScanRequests? requests = null,
        IOutputModerationWake? wake = null,
        TimeSpan? startupDelay = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _requests = requests;
        _wake = wake;
        _startupDelay = startupDelay ?? StartupDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int minutes = _options.Value.Sweeper.IntervalMinutes;
        if (minutes <= 0)
        {
            // A sweeper that never runs must not leave rows Pending forever: general moderation and
            // other readers would wait for a verdict that can never arrive. Settle them once, with
            // the same disabled/not-scanned audit a disabled in-request matcher writes.
            try
            {
                await NormalizeUnscannedAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "The CSAM sweep is disabled and its one-shot normalization failed");
            }

            _logger.LogInformation("The CSAM scan sweep is disabled (Csam:Sweeper:IntervalMinutes = {Minutes})", minutes);
            return;
        }

        TimeSpan interval = TimeSpan.FromMinutes(minutes);
        try
        {
            await Task.Delay(_startupDelay, stoppingToken);
            await Task.WhenAll(IntervalLoopAsync(interval, stoppingToken), RequestLoopAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task IntervalLoopAsync(TimeSpan interval, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "The CSAM scan sweep failed; retrying after the interval");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    /// <summary>Serves <see cref="ICsamScanRequests"/>: one project at a time, on this sweeper's own task.</summary>
    private async Task RequestLoopAsync(CancellationToken stoppingToken)
    {
        if (_requests is null) return;

        while (await _requests.WaitToTakeAsync(stoppingToken))
        {
            while (_requests.TryTake(out Guid projectId))
            {
                try
                {
                    await ScanProjectAsync(projectId, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        "The requested CSAM scan of project {ProjectId} failed ({ExceptionType}); the interval sweep will cover it.",
                        projectId, ex.GetType().Name);
                }
            }
        }
    }

    /// <summary>
    /// Scans one project's <see cref="CsamScanStatus.Pending"/> rows (and its stale claims) now, up to one batch, then wakes
    /// the output-moderation lane. Unlike the interval sweep there is no grace: the caller is a held output that waits for
    /// exactly these rows, and an engine- or assistant-written row has no in-request scan to wait for. An upload whose
    /// in-request scan is still running may be scanned twice; that is safe (the matcher never undoes a quarantine, and each
    /// attempt is audited). Claims are the same conditional update, so two scanners never scan one row. Public for tests:
    /// returns how many files were scanned. Does nothing while the control or its sweeper is off.
    /// </summary>
    public async Task<int> ScanProjectAsync(Guid projectId, CancellationToken ct)
    {
        CsamSweeperOptions options = _options.Value.Sweeper;
        if (options.IntervalMinutes <= 0) return 0;

        // A disabled control will never produce a verdict: settle this project's waiting rows now (no grace) so
        // their derivatives, summaries and moderation are not held for the interval sweep.
        if (!_options.Value.Arachnid.Enabled)
        {
            int settled = await NormalizeUnscannedAsync(ct, projectId);
            if (settled > 0) _wake?.Wake();
            return settled;
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateTime staleClaimBefore = now - ClaimStaleAfter;

        List<Guid> candidates;
        using (IServiceScope lookupScope = _scopeFactory.CreateScope())
        {
            using IDisposable? system = lookupScope.ServiceProvider.GetService<ITenantContext>()?.System();
            InferenceApiDbContext db = lookupScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

            candidates = await db.ProjectFiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(f => f.ProjectId == projectId
                            && (f.CsamStatus == CsamScanStatus.Pending
                                || (f.CsamStatus == CsamScanStatus.Scanning && f.CsamScanClaimedAt < staleClaimBefore)))
                .OrderBy(f => f.UploadedAt)
                .Take(Math.Max(1, options.BatchSize))
                .Select(f => f.Id)
                .ToListAsync(ct);
        }

        if (candidates.Count == 0) return 0;

        // Every Pending row qualifies here, whatever its age: the "pending before" bound is past any upload time (a plain
        // value, not DateTime.MaxValue, which a timestamp column may read as infinity). NotScanned rows are not rescanned.
        DateTime anyPending = now.AddDays(1);
        List<Guid> claimed = await ClaimAsync(
            candidates, anyPending, staleClaimBefore, now, rescanNotScanned: false, now, ct);
        int scanned = await ScanClaimedAsync(claimed, ct);
        _wake?.Wake();
        return scanned;
    }

    /// <summary>One batch. Public for tests: it returns how many files were scanned.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        CsamSweeperOptions options = _options.Value.Sweeper;

        // No scan can ever settle a Pending row when the control is off or the sweeper cannot run.
        // Settle a batch to NotScanned with a disabled audit so moderation and readers are not
        // blocked forever; a later re-enable re-scans only rows the operator chooses to rescan.
        if (!_options.Value.Arachnid.Enabled || options.IntervalMinutes <= 0)
            return await NormalizeUnscannedAsync(ct);

        int batchSize = Math.Max(1, options.BatchSize);
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateTime pendingBefore = now - PendingGrace;
        DateTime staleClaimBefore = now - ClaimStaleAfter;
        DateTime notScannedBefore = now - NotScannedRescanAge;

        List<Guid> candidates;
        using (IServiceScope lookupScope = _scopeFactory.CreateScope())
        {
            using IDisposable? system = lookupScope.ServiceProvider.GetService<ITenantContext>()?.System();
            InferenceApiDbContext db = lookupScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

            candidates = await db.ProjectFiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(f => (f.CsamStatus == CsamScanStatus.Pending && f.UploadedAt < pendingBefore)
                            || (f.CsamStatus == CsamScanStatus.Scanning && f.CsamScanClaimedAt < staleClaimBefore))
                .OrderBy(f => f.UploadedAt)
                .Take(batchSize)
                .Select(f => f.Id)
                .ToListAsync(ct);

            if (options.RescanNotScanned && candidates.Count < batchSize)
            {
                List<Guid> retry = await db.ProjectFiles
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(f => f.CsamStatus == CsamScanStatus.NotScanned
                                && (f.CsamScannedAt ?? f.UploadedAt) < notScannedBefore)
                    .OrderBy(f => f.CsamScannedAt ?? f.UploadedAt)
                    .Take(batchSize - candidates.Count)
                    .Select(f => f.Id)
                    .ToListAsync(ct);
                candidates.AddRange(retry);
            }
        }

        List<Guid> claimed = await ClaimAsync(
            candidates, pendingBefore, staleClaimBefore, notScannedBefore, options.RescanNotScanned, now, ct);
        int scanned = await ScanClaimedAsync(claimed, ct);

        // Outputs held by these rows may be judged now.
        if (scanned > 0) _wake?.Wake();
        return scanned;
    }

    /// <summary>
    /// Claim before scanning. One conditional UPDATE per candidate: a sweeper scans only a row it
    /// won, so two instances can never scan (and write) the same row.
    /// </summary>
    private async Task<List<Guid>> ClaimAsync(
        List<Guid> candidates, DateTime pendingBefore, DateTime staleClaimBefore, DateTime notScannedBefore,
        bool rescanNotScanned, DateTime now, CancellationToken ct)
    {
        List<Guid> claimed = [];
        foreach (Guid id in candidates)
        {
            if (ct.IsCancellationRequested) break;

            using IServiceScope claimScope = _scopeFactory.CreateScope();
            using IDisposable? system = claimScope.ServiceProvider.GetService<ITenantContext>()?.System();
            InferenceApiDbContext db = claimScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

            if (db.Database.IsRelational())
            {
                int affected = await db.ProjectFiles
                    .IgnoreQueryFilters()
                    .Where(f => f.Id == id &&
                                ((f.CsamStatus == CsamScanStatus.Pending && f.UploadedAt < pendingBefore)
                                 || (f.CsamStatus == CsamScanStatus.Scanning && f.CsamScanClaimedAt < staleClaimBefore)
                                 || (rescanNotScanned
                                     && f.CsamStatus == CsamScanStatus.NotScanned
                                     && (f.CsamScannedAt ?? f.UploadedAt) < notScannedBefore)))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(f => f.CsamStatus, CsamScanStatus.Scanning)
                        .SetProperty(f => f.CsamScanClaimedAt, (DateTime?)now), ct);
                if (affected == 1) claimed.Add(id);
            }
            else
            {
                ProjectFile? row = await db.ProjectFiles
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(f => f.Id == id, ct);
                bool claimable = row is not null &&
                    ((row.CsamStatus == CsamScanStatus.Pending && row.UploadedAt < pendingBefore)
                     || (row.CsamStatus == CsamScanStatus.Scanning && row.CsamScanClaimedAt < staleClaimBefore)
                     || (rescanNotScanned
                         && row.CsamStatus == CsamScanStatus.NotScanned
                         && (row.CsamScannedAt ?? row.UploadedAt) < notScannedBefore));
                if (claimable)
                {
                    row!.CsamStatus = CsamScanStatus.Scanning;
                    row.CsamScanClaimedAt = now;
                    await db.SaveChangesAsync(ct);
                    claimed.Add(id);
                }
            }
        }

        return claimed;
    }

    private async Task<int> ScanClaimedAsync(List<Guid> claimed, CancellationToken ct)
    {
        int scanned = 0;
        foreach (Guid id in claimed)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                using IServiceScope fileScope = _scopeFactory.CreateScope();
                using IDisposable? system = fileScope.ServiceProvider.GetService<ITenantContext>()?.System();
                InferenceApiDbContext fileDb = fileScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

                ProjectFile? file = await fileDb.ProjectFiles
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(f => f.Id == id && f.CsamStatus == CsamScanStatus.Scanning, ct);
                if (file is null) continue;

                ICsmaHashMatcher matcher = fileScope.ServiceProvider.GetRequiredService<ICsmaHashMatcher>();
                await matcher.ScanAsync(file, actorUserId: null, ct);
                scanned++;
                await RequestDerivativesAsync(fileScope.ServiceProvider, file, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The matcher already fails open; this is the belt around a persistence or scope
                // failure. Only the file id and the exception type are logged — never the exception
                // object, whose message or data could carry provider text.
                _logger.LogWarning(
                    "The CSAM sweeper could not scan project file {FileId} ({ExceptionType}); continuing.",
                    id, ex.GetType().Name);
            }
        }

        return scanned;
    }

    /// <summary>
    /// The disabled/stopped-control path: move a batch of Pending/Scanning rows to NotScanned and
    /// write the same "disabled" audit row the in-request matcher writes. Returns the count settled.
    /// </summary>
    private async Task<int> NormalizeUnscannedAsync(CancellationToken ct, Guid? projectId = null)
    {
        CsamSweeperOptions options = _options.Value.Sweeper;
        int batchSize = Math.Max(1, options.BatchSize);
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateTime pendingBefore = projectId is null ? now - PendingGrace : now.AddDays(1);
        DateTime staleClaimBefore = now - ClaimStaleAfter;
        string errorCode = _options.Value.Arachnid.Enabled ? "SWEEPER_DISABLED" : "DISABLED";

        using IServiceScope scope = _scopeFactory.CreateScope();
        using IDisposable? system = scope.ServiceProvider.GetService<ITenantContext>()?.System();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        // Select ids only. The verdict write is a conditional UPDATE per row (same transaction as its
        // check), so a concurrent inline scan that quarantines a candidate between this read and the
        // write wins: the update affects 0 rows, no audit is written, and the quarantine stands.
        List<Guid> ids = await db.ProjectFiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            // The normal sweeper conditions: never settle a row still inside its grace/claim window,
            // and never touch a row the CSAM control already holds.
            .Where(f => (projectId == null || f.ProjectId == projectId)
                        && f.CsamQuarantineKey == null
                        && ((f.CsamStatus == CsamScanStatus.Pending && f.UploadedAt < pendingBefore)
                            || (f.CsamStatus == CsamScanStatus.Scanning && f.CsamScanClaimedAt < staleClaimBefore)))
            .OrderBy(f => f.UploadedAt)
            .Take(batchSize)
            .Select(f => f.Id)
            .ToListAsync(ct);
        if (ids.Count == 0) return 0;

        int settled = 0;
        List<Guid> settledIds = [];
        foreach (Guid id in ids)
        {
            ProjectFile? row = await db.ProjectFiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == id, ct);
            if (row is null) continue;

            if (await TryNormalizeAsync(db, id, pendingBefore, staleClaimBefore, ct))
            {
                settled++;
                settledIds.Add(id);
                db.CsamScanAudits.Add(new CsamScanAudit
                {
                    Id = Guid.NewGuid(),
                    ProjectId = row.ProjectId,
                    ProjectFileId = row.Id,
                    ProjectFileName = row.OriginalFileName,
                    Status = "disabled",
                    Source = "none",
                    Quarantined = false,
                    QuarantineSucceeded = false,
                    ErrorCode = errorCode,
                    CreatedAt = now
                });
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("CSAM settled {Count} pending row(s) as not-scanned ({Reason})", settled, errorCode);

        foreach (Guid id in settledIds)
        {
            ProjectFile? row = await db.ProjectFiles.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
            if (row is not null) await RequestDerivativesAsync(scope.ServiceProvider, row, ct);
        }

        return settled;
    }

    /// <summary>
    /// A file that was Pending when it was written (cloud or GitHub sync, an assistant or engine output) never asked
    /// for its media derivative set, because no derivative may be made before the CSAM verdict. Now that this
    /// sweeper has one, a settled, un-held video/audio row asks for it, best effort, exactly as an upload does.
    /// A quarantine, or a row still unsettled, asks for nothing.
    /// </summary>
    private async Task RequestDerivativesAsync(IServiceProvider services, ProjectFile file, CancellationToken ct)
    {
        if (!UploadDerivativeRequest.IsMediaMimeType(file.MimeType) || !CsamScanGate.AllowsDerivatives(file)) return;

        IPublishEndpoint? publish = services.GetService<IPublishEndpoint>();
        IFileStorageService? storage = services.GetService<IFileStorageService>();
        if (publish is null || storage is null) return;

        await UploadDerivativeRequest.PublishAsync(
            publish, storage, services.GetService<IUploadAnalysisSetting>(), file, file.MimeType!, _logger, ct);
    }

    /// <summary>
    /// The monotonic, conditional half of the disabled-path normalization. A relational provider gets
    /// one UPDATE whose WHERE still names Pending/Scanning, so it cannot overwrite a concurrent
    /// Quarantined verdict. The InMemory provider (tests) gets the same check on a tracked row.
    /// Returns true only when this call settled the row.
    /// </summary>
    internal static async Task<bool> TryNormalizeAsync(
        InferenceApiDbContext db, Guid id, DateTime pendingBefore, DateTime staleClaimBefore, CancellationToken ct)
    {
        // The SAME status + age + held-key predicate in the candidate read and the write.
        if (db.Database.IsRelational())
        {
            int affected = await db.ProjectFiles
                .IgnoreQueryFilters()
                .Where(f => f.Id == id
                            && f.CsamQuarantineKey == null
                            && ((f.CsamStatus == CsamScanStatus.Pending && f.UploadedAt < pendingBefore)
                                || (f.CsamStatus == CsamScanStatus.Scanning && f.CsamScanClaimedAt < staleClaimBefore)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(f => f.CsamStatus, CsamScanStatus.NotScanned)
                    .SetProperty(f => f.CsamScanClaimedAt, (DateTime?)null), ct);
            return affected == 1;
        }

        ProjectFile? row = await db.ProjectFiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == id, ct);
        bool settle = row is not null
            && row.CsamQuarantineKey is null
            && ((row.CsamStatus == CsamScanStatus.Pending && row.UploadedAt < pendingBefore)
                || (row.CsamStatus == CsamScanStatus.Scanning && row.CsamScanClaimedAt < staleClaimBefore));
        if (!settle) return false;
        row!.CsamStatus = CsamScanStatus.NotScanned;
        row.CsamScanClaimedAt = null;
        return true;
    }
}
