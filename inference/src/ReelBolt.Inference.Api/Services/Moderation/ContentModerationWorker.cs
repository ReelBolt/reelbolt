using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Moderation;

/// <summary>
/// Drains the ingest-moderation queue with two workers and sweeps rows a previous process or a lost
/// queue offer left behind. No HTTP request ever waits for it: the upload path only offers a file id.
/// </summary>
/// <remarks>
/// The row's status is the durable source of truth (<see cref="FileModerationStatus.Unmoderated"/> is
/// the only state claimed), so the in-memory queue is a fast path. On start, rows left
/// <see cref="FileModerationStatus.Scanning"/> by a crashed process are reset, and every five minutes
/// the sweeper re-offers <c>Unmoderated</c> rows older than ten minutes.
/// </remarks>
public sealed class ContentModerationWorker : BackgroundService
{
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan StaleUnmoderatedAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A Scanning row older than this is abandoned (a scan takes seconds to a few minutes). The
    /// sweeper returns it to Unmoderated and re-offers it, so no row can sit Scanning forever.
    /// </summary>
    internal static readonly TimeSpan StaleScanningAge = TimeSpan.FromMinutes(30);
    internal const int WorkerCount = 2;

    private readonly IBackgroundTaskQueue<ContentModerationTask> _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContentModerationWorker> _logger;
    private readonly bool _csamEnabled;

    public ContentModerationWorker(
        IBackgroundTaskQueue<ContentModerationTask> queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ContentModerationWorker> logger,
        IOptions<CsamOptions>? csamOptions = null)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Only the master switch decides whether a CSAM verdict is expected. A stopped sweeper is a
        // deployment fault, not a licence to treat an unresolved row as settled: the gate stays
        // fail-closed for Pending/Scanning. The one-shot normalization settles only rows older than
        // the normal grace/claim windows so ingest is not permanently blocked.
        _csamEnabled = csamOptions?.Value.Arachnid.Enabled ?? true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResetStaleScanningAsync(stoppingToken);

        _logger.LogInformation(
            "Content moderation service started with {Workers} worker(s)", WorkerCount);

        List<Task> loops = [SweepLoopAsync(stoppingToken)];
        for (int i = 0; i < WorkerCount; i++)
            loops.Add(WorkerLoopAsync(stoppingToken));

        await Task.WhenAll(loops);
    }

    private async Task WorkerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ContentModerationTask task;
            try
            {
                task = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            // No in-flight dedupe on the file id: a re-upload offers a second scan for the SAME id
            // while the first is still running, and dropping it would leave the replacement bytes
            // unclassified. The processor re-reads the row's storage key before every verdict, so a
            // stale scan writes nothing and the newer one classifies the newer bytes.
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                ContentModerationProcessor processor =
                    scope.ServiceProvider.GetRequiredService<ContentModerationProcessor>();
                await processor.ProcessAsync(task.FileId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The processor already records Error for its own failures; this catches only a
                // container/scope failure, which must not kill the worker.
                _logger.LogError(ex, "Content moderation worker failed on file {FileId}", task.FileId);
            }
            finally
            {
                _queue.Complete(task);
            }
        }
    }

    private async Task SweepLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int queued = await SweepOnceAsync(stoppingToken);
                if (queued > 0)
                    _logger.LogInformation("Content moderation sweep queued {Count} file(s)", queued);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Content moderation sweep failed; will retry after the interval");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One sweep, exposed for tests: offers every <c>Unmoderated</c> row older than
    /// <see cref="StaleUnmoderatedAge"/> that still fits in the queue. Returns how many were queued.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        int room = _queue.FreeCapacity;
        if (room <= 0) return 0;

        using IServiceScope scope = _scopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        ITenantContext? tenant = scope.ServiceProvider.GetService<ITenantContext>();

        DateTime unmoderatedCutoff = DateTime.UtcNow - StaleUnmoderatedAge;
        DateTime scanningCutoff = DateTime.UtcNow - StaleScanningAge;
        List<Guid> ids;
        using (tenant.System())
        {
            // Unmoderated rows left behind by a lost offer, AND Scanning rows a dead/abandoned scan
            // never finished. The latter are returned to Unmoderated first so the offer processes them.
            IQueryable<ProjectFile> candidates = db.ProjectFiles
                .Where(f => (f.ModerationStatus == FileModerationStatus.Unmoderated && f.UploadedAt < unmoderatedCutoff)
                            || (f.ModerationStatus == FileModerationStatus.Scanning && f.UploadedAt < scanningCutoff))
                // Never offer a CSAM-held file to general moderation.
                .Where(f => f.CsamStatus != CsamScanStatus.Quarantined && f.CsamQuarantineKey == null);
            if (_csamEnabled)
            {
                // Wait for a Pending/Scanning CSAM verdict before sending the bytes anywhere. With
                // the CSAM control disabled there is no verdict coming, so pending rows proceed.
                candidates = candidates.Where(
                    f => f.CsamStatus != CsamScanStatus.Pending
                         && f.CsamStatus != CsamScanStatus.Scanning);
            }

            List<ProjectFile> rows = await candidates
                .OrderBy(f => f.UploadedAt)
                .Take(room)
                .ToListAsync(ct);

            bool reset = false;
            foreach (ProjectFile row in rows)
            {
                if (row.ModerationStatus == FileModerationStatus.Scanning)
                {
                    row.ModerationStatus = FileModerationStatus.Unmoderated;
                    reset = true;
                }
            }

            if (reset) await db.SaveChangesAsync(ct);
            ids = rows.Select(r => r.Id).ToList();
        }

        int queued = 0;
        foreach (Guid id in ids)
        {
            if (_queue.TryQueue(new ContentModerationTask(id)))
                queued++;
        }

        return queued;
    }

    /// <summary>A <c>Scanning</c> row belongs to a process that is gone (this is startup, before any
    /// worker runs), so it is safe to return it to <c>Unmoderated</c>.</summary>
    private async Task ResetStaleScanningAsync(CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            ITenantContext? tenant = scope.ServiceProvider.GetService<ITenantContext>();

            using (tenant.System())
            {
                List<ProjectFile> stale = await db.ProjectFiles
                    .Where(f => f.ModerationStatus == FileModerationStatus.Scanning)
                    .ToListAsync(ct);

                if (stale.Count == 0) return;

                foreach (ProjectFile file in stale)
                    file.ModerationStatus = FileModerationStatus.Unmoderated;

                await db.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "Reset {Count} file(s) left in Scanning by a previous moderation process", stale.Count);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reset stale Scanning moderation rows at startup");
        }
    }
}
