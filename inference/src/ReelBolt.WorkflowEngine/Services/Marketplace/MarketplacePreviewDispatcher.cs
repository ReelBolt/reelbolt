using System.Collections.Concurrent;
using MassTransit;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Marketplace;

/// <summary>
/// Hands a preview request to a process-lifetime task and returns at once, like <c>MediaDerivativeDispatcher</c>: the broker
/// delivery is acknowledged in milliseconds while the encode runs. Runs on the derivative lane: ffmpeg goes through
/// <see cref="IVideoToolRunner"/> at <see cref="VideoToolPriority.Low"/> (the shared low-priority admission gate, which yields to any
/// user-facing job), and the whole run (download included) holds one of <c>VideoEditing:DerivativeMaxConcurrentJobs</c> slots, the
/// same cap derivatives use. Idempotent per run (version and nonce): a repeat while a run is in flight, or after it finished in this process, is dropped.
/// </summary>
public sealed class MarketplacePreviewDispatcher : IHostedService
{
    private readonly MarketplacePreviewService _service;
    private readonly IBus _bus;
    private readonly ILogger<MarketplacePreviewDispatcher> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly ConcurrentDictionary<Guid, string> _latest = new();
    private readonly ConcurrentDictionary<string, byte> _done = new();
    private readonly CancellationTokenSource _lifetime = new();

    public MarketplacePreviewDispatcher(
        MarketplacePreviewService service,
        IBus bus,
        IOptions<VideoEditingOptions> video,
        ILogger<MarketplacePreviewDispatcher> logger)
    {
        _service = service;
        _bus = bus;
        _logger = logger;
        int slots = Math.Max(1, video.Value.DerivativeMaxConcurrentJobs);
        _slots = new SemaphoreSlim(slots, slots);
    }

    private static string RunKey(MarketplacePreviewRequested m) => $"{m.VersionId:N}:{m.RunNonce}";

    public int InFlightCount => _inFlight.Count;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_lifetime.IsCancellationRequested)
            _lifetime.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>Starts the run and returns. False when the version was already handled (the repeat is dropped).</summary>
    public bool Dispatch(MarketplacePreviewRequested message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string run = RunKey(message);
        if (_done.ContainsKey(run) || !_inFlight.TryAdd(run, 0))
            return false;

        // A newer dispatch of the same version supersedes any older run still working: that run must not upload.
        _latest[message.VersionId] = message.RunNonce;

        _ = Task.Run(() => RunAsync(message, _lifetime.Token), CancellationToken.None);
        return true;
    }

    /// <summary>The run itself, awaitable for tests. Never throws; always ends in one <see cref="MarketplacePreviewFinished"/> unless shutting down.</summary>
    public async Task RunAsync(MarketplacePreviewRequested message, CancellationToken ct)
    {
        bool holdsSlot = false;
        try
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            holdsSlot = true;
            MarketplacePreviewFinished finished = await _service.RunAsync(
                message, ct, () => _latest.TryGetValue(message.VersionId, out string? latest) && latest != message.RunNonce).ConfigureAwait(false);
            await _bus.Publish(finished, CancellationToken.None).ConfigureAwait(false);
            _done[RunKey(message)] = 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the API sweeper re-dispatches a version still preparing.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Marketplace preview run for version {VersionId} ended unexpectedly", message.VersionId);
            try
            {
                await _bus.Publish(
                    new MarketplacePreviewFinished(message.VersionId, message.RecipeId, false, MarketplacePreviewFailureCodes.Internal, RunNonce: message.RunNonce),
                    CancellationToken.None).ConfigureAwait(false);
                _done[RunKey(message)] = 0;
            }
            catch (Exception publishEx)
            {
                _logger.LogError(publishEx, "Could not report the failed marketplace preview for version {VersionId}", message.VersionId);
            }
        }
        finally
        {
            if (holdsSlot)
                _slots.Release();
            _inFlight.TryRemove(RunKey(message), out _);
        }
    }
}
