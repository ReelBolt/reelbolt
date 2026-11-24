using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>
/// Runs the drift checks queued on <see cref="DesignDriftQueue"/>. Each check opens its own DI scope
/// (its own DbContext) under an explicit system tenant scope — the sync that noticed the change belongs
/// to a project, not to an HTTP request — and is cancelled only by the host's stopping token, never by
/// the token of the request that queued it.
///
/// <para>
/// This worker never throws out of the loop: the drift service records its own outcomes, and any
/// unexpected failure is logged and the queue entry released so one broken check never stops the rest.
/// </para>
/// </summary>
public sealed class DesignSystemDriftWorker : BackgroundService
{
    public const int Concurrency = 1;

    /// <summary>The per-check timeout when configuration is present and names none.</summary>
    public const double DefaultItemTimeoutSeconds = 600;

    private readonly DesignDriftQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DesignSystemDriftWorker> _logger;
    private readonly TimeSpan? _itemTimeout;

    /// <param name="configuration">
    /// When present, <c>DesignSystem:Drift:ItemTimeoutSeconds</c> (default
    /// <see cref="DefaultItemTimeoutSeconds"/>, <c>0</c> disables) bounds each check, so one hung check
    /// (a slow storage read, a pathological file) cannot park the single worker forever. Without
    /// configuration no timeout applies.
    /// </param>
    public DesignSystemDriftWorker(
        DesignDriftQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DesignSystemDriftWorker> logger,
        IConfiguration? configuration = null)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;

        if (configuration is not null)
        {
            double seconds = configuration.GetValue("DesignSystem:Drift:ItemTimeoutSeconds", DefaultItemTimeoutSeconds);
            if (seconds > 0)
                _itemTimeout = TimeSpan.FromSeconds(seconds);
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => DrainAsync(stoppingToken)));

    private async Task DrainAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (DesignDriftRequest request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(request, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Whatever was queued is lost with the process; the sweep re-queues it later.
        }
    }

    /// <summary>
    /// Runs one dequeued check and always completes its queue entry. Public so a test can drive the
    /// worker without the host.
    /// </summary>
    public async Task ProcessAsync(DesignDriftRequest request, CancellationToken stoppingToken)
    {
        if (!_queue.TryBegin(request.ProjectId, out DesignDriftRequest current))
            return;

        using CancellationTokenSource? timeout = _itemTimeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout?.CancelAfter(_itemTimeout!.Value);
        CancellationToken ct = timeout?.Token ?? stoppingToken;

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
            IDesignSystemDriftService drift = scope.ServiceProvider.GetRequiredService<IDesignSystemDriftService>();

            DesignDriftOutcome outcome = await drift.CheckAsync(
                current.ProjectId, current.Trigger, current.ChangedPaths, ct);
            _logger.LogInformation(
                "Design-system drift check of project {ProjectId} finished: {Result}", current.ProjectId, outcome.Result);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Not a shutdown, so it was the per-item timeout: abandon this check, keep the worker.
            _logger.LogWarning("Design-system drift check of project {ProjectId} timed out", current.ProjectId);
        }
        catch (Exception ex)
        {
            // The drift service records its own failures; this is the net under it, so one broken check
            // never stops the worker.
            _logger.LogError(ex, "Design-system drift check of project {ProjectId} failed unexpectedly", current.ProjectId);
        }
        finally
        {
            _queue.Complete(current.ProjectId);
        }
    }
}
