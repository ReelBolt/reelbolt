using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.CloudStorage;

/// <summary>
/// Re-syncs every link periodically, because none of the three platforms pushes changes to us without
/// webhook infrastructure (docs/design/cloud-drive-sync.md). Every
/// <see cref="CloudStorageOptions.SweepIntervalMinutes"/> it queues the links whose last successful sync
/// is older than the interval (or that never synced). <c>0</c> disables it: "Sync now" only.
///
/// <para>
/// Skipped: links with no connection (they need to be linked again, and a sync would only repeat that),
/// links whose connection is marked broken (the person must reconnect first; reconnecting clears the
/// mark), and links of a provider that is not configured. Per process, like the queue.
/// </para>
/// </summary>
public sealed class CloudSyncSweeper : BackgroundService
{
    /// <summary>Lets migrations and seeding finish before the first sweep.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

    private readonly CloudSyncQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<CloudStorageOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<CloudSyncSweeper> _logger;

    public CloudSyncSweeper(
        CloudSyncQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptions<CloudStorageOptions> options,
        ILogger<CloudSyncSweeper> logger,
        TimeProvider? clock = null)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int minutes = _options.Value.SweepIntervalMinutes;
        if (minutes <= 0)
        {
            _logger.LogInformation("The cloud sync sweep is disabled (CloudStorage:SweepIntervalMinutes = {Minutes})", minutes);
            return;
        }

        TimeSpan interval = TimeSpan.FromMinutes(minutes);
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "The cloud sync sweep failed; retrying after the interval");
                }

                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One sweep: queues the due links and returns how many were queued. Public for tests.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        int minutes = _options.Value.SweepIntervalMinutes;
        if (minutes <= 0) return 0;

        using IServiceScope scope = _scopeFactory.CreateScope();
        using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        ICloudStorageProviderRegistry providers = scope.ServiceProvider.GetRequiredService<ICloudStorageProviderRegistry>();

        List<CloudStorageProviderKind> configured = providers.Configured.Select(p => p.Kind).ToList();
        if (configured.Count == 0) return 0;

        DateTime dueBefore = _clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(minutes);
        List<Guid> due = await db.ProjectCloudLinks
            .Where(l => l.ConnectionId != null && configured.Contains(l.Provider))
            .Where(l => l.LastSyncedAt == null || l.LastSyncedAt < dueBefore)
            .Where(l => db.CloudStorageConnections.Any(c => c.Id == l.ConnectionId && c.LastError == null))
            .Select(l => l.Id)
            .ToListAsync(ct);

        int queued = 0;
        foreach (Guid linkId in due)
        {
            // A link already queued or running is not asked to run again afterwards by the sweep.
            if (_queue.IsSyncing(linkId)) continue;
            if (_queue.Enqueue(linkId, force: false, manual: false, out _) == CloudSyncEnqueueResult.Queued) queued++;
        }

        if (queued > 0) _logger.LogInformation("The cloud sync sweep queued {Count} link(s)", queued);
        return queued;
    }
}
