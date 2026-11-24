namespace ReelBolt.Inference.Api.Services.Archives;

/// <summary>
/// Runs queued zip unpacks one at a time. A nudge from the upload hook starts work at once; every
/// <see cref="SweepInterval"/> it also claims Pending rows that came due (retries, a lost nudge, another replica's
/// leftovers) and Running rows whose claim went stale (a crashed process). Rows are claimed with a conditional update, so
/// two replicas never run one unpack.
/// </summary>
public sealed class ArchiveUnpackWorker : BackgroundService
{
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ArchiveUnpackSignal _signal;
    private readonly ILogger<ArchiveUnpackWorker> _logger;

    public ArchiveUnpackWorker(IServiceScopeFactory scopeFactory, ArchiveUnpackSignal signal, ILogger<ArchiveUnpackWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _signal = signal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                await _signal.WaitAsync(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Archive unpack sweep failed; will retry after the interval");
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
    }

    /// <summary>Claims and runs every unpack that is due right now; returns how many were run. Exposed for tests.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        int ran = 0;
        while (!ct.IsCancellationRequested)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            ArchiveUnpackProcessor processor = scope.ServiceProvider.GetRequiredService<ArchiveUnpackProcessor>();
            Guid? id = await processor.ClaimNextAsync(ct);
            if (id is null) break;

            try
            {
                await processor.ProcessAsync(id.Value, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The claim goes stale and the sweep cleans up and retries; one bad unpack never stops the worker.
                _logger.LogError(ex, "Archive unpack {UnpackId} crashed", id);
            }

            ran++;
        }

        return ran;
    }
}
