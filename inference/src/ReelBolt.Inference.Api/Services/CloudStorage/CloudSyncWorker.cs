using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.CloudStorage;

/// <summary>
/// Runs the syncs queued on <see cref="CloudSyncQueue"/>, <see cref="Concurrency"/> at a time
/// (docs/design/cloud-drive-sync.md "Core services").
///
/// <para>
/// Each sync runs in its own DI scope (its own DbContext) under an explicit system tenant scope — a
/// queued link belongs to whichever project asked, not to a request — and is cancelled only by the
/// host's stopping token, never by the token of the HTTP request that queued it. That is the GitHub
/// lesson: a sync that outlived nginx's 60 seconds was cancelled together with the aborted request.
/// </para>
/// </summary>
public sealed class CloudSyncWorker : BackgroundService
{
    public const int Concurrency = 2;

    private readonly CloudSyncQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CloudSyncWorker> _logger;

    public CloudSyncWorker(CloudSyncQueue queue, IServiceScopeFactory scopeFactory, ILogger<CloudSyncWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => DrainAsync(stoppingToken)));

    private async Task DrainAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (Guid linkId in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(linkId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Whatever was queued is lost with the process; the sweep re-queues it later.
        }
    }

    /// <summary>
    /// Runs one dequeued sync and always completes its queue entry. Public so a test can drive the worker
    /// without the host.
    /// </summary>
    public async Task ProcessAsync(Guid linkId, CancellationToken stoppingToken)
    {
        if (!_queue.TryBegin(linkId, out bool force)) return;

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
            ICloudSyncService sync = scope.ServiceProvider.GetRequiredService<ICloudSyncService>();

            CloudSyncOutcome outcome = await sync.SyncAsync(linkId, force, stoppingToken);
            _logger.LogInformation("Cloud sync of link {LinkId} finished: {Status}", linkId, outcome.Status);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The sync service records its own failures; this is the net under it, so one broken sync
            // never stops the worker.
            _logger.LogError(ex, "Cloud sync of link {LinkId} failed unexpectedly", linkId);
        }
        finally
        {
            _queue.Complete(linkId);
        }
    }
}
