using System.Collections.Concurrent;
using System.Threading.Channels;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.WorkflowEngine.Services.Segmentation;

/// <summary>
/// Runs mask-preview encodes on a process-lifetime worker so the broker delivery is acknowledged in
/// milliseconds (same discipline as the media-derivative dispatcher). One encode at a time, de-duplicated by
/// (project, step result, track): the editor polls until the preview is ready, so the same request arrives
/// many times. A request that just failed is not retried for <see cref="FailureCooldown"/>.
/// </summary>
public sealed class SegmentationMaskPreviewDispatcher(
    SegmentationMaskPreviewService service,
    ILogger<SegmentationMaskPreviewDispatcher> logger) : BackgroundService
{
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);

    private const int Capacity = 256;

    private readonly Channel<SegmentationMaskPreviewRequested> _channel = Channel.CreateBounded<SegmentationMaskPreviewRequested>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly ConcurrentDictionary<(Guid, Guid, string), byte> _inFlight = new();
    private readonly ConcurrentDictionary<(Guid, Guid, string), DateTime> _failedAt = new();

    /// <summary>True when the request was queued; false when it is already queued, just failed, or the queue is full.</summary>
    public bool TryEnqueue(SegmentationMaskPreviewRequested request)
    {
        ArgumentNullException.ThrowIfNull(request);
        (Guid, Guid, string) key = (request.ProjectId, request.StepResultId, request.TrackId);
        if (_failedAt.TryGetValue(key, out DateTime failed) && DateTime.UtcNow - failed < FailureCooldown)
        {
            return false;
        }

        if (!_inFlight.TryAdd(key, 0))
        {
            return false;
        }

        if (_channel.Writer.TryWrite(request))
        {
            return true;
        }

        _inFlight.TryRemove(key, out _);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (SegmentationMaskPreviewRequested request in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                (Guid, Guid, string) key = (request.ProjectId, request.StepResultId, request.TrackId);
                try
                {
                    SegmentationMaskPreviewOutcome outcome = await service.ProduceAsync(request, stoppingToken).ConfigureAwait(false);
                    if (outcome == SegmentationMaskPreviewOutcome.Failed)
                    {
                        _failedAt[key] = DateTime.UtcNow;
                    }
                    else
                    {
                        _failedAt.TryRemove(key, out _);
                    }

                    logger.LogInformation(
                        "Mask preview for step result {StepResultId} track {TrackId}: {Outcome}",
                        request.StepResultId, request.TrackId, outcome);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _failedAt[key] = DateTime.UtcNow;
                    logger.LogError(ex, "Mask preview worker failed for step result {StepResultId}", request.StepResultId);
                }
                finally
                {
                    _inFlight.TryRemove(key, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
