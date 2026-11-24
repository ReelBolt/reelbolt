using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Dto;

namespace ReelBolt.Inference.Api.Services.Media;

/// <summary>
/// Runs <see cref="MediaDerivativeCoverageService.SweepOnceAsync"/> on a timer: the durable repair for
/// derivative requests that were lost, and the operator-readable coverage line.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a sweeper, not an outbox.</b> The reasoning lives on
/// <see cref="MediaDerivativeBackfillOptions"/>: the source rows already say the work exists, so the
/// repair re-derives it and cannot drift from the truth. A lost publish therefore costs at most one
/// interval, and the same selection fixes the whole pre-existing back catalogue.
/// </para>
/// <para>
/// <b>The log line is the observability surface.</b> Every sweep logs the coverage counts and what it
/// requested, so an operator sees "107 video sources without a proxy" in the log without touching SQL;
/// <c>GET /api/v1/media/admin/coverage</c> answers the same figures on demand. A sweep that cannot
/// publish logs at Warning, because that is the state that produced the buffering in the first place.
/// </para>
/// <para>
/// <b>Per process.</b> Every API replica runs its own timer. Two replicas can select the same source;
/// the duplicate is a no-op at the engine's cache key, and a cross-replica lease would add coordination
/// for nothing at this scale (the same trade the cloud-sync and drift sweepers make).
/// </para>
/// </remarks>
public sealed class MediaDerivativeBackfillService(
    IServiceScopeFactory scopeFactory,
    IOptions<MediaDerivativeBackfillOptions> options,
    ILogger<MediaDerivativeBackfillService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MediaDerivativeBackfillOptions settings = options.Value;

        if (!settings.Enabled || settings.IntervalMinutes <= 0)
        {
            logger.LogInformation(
                "The media derivative backfill sweep is disabled (MediaDerivatives:Backfill:Enabled = {Enabled}, " +
                "IntervalMinutes = {IntervalMinutes})",
                settings.Enabled, settings.IntervalMinutes);
            return;
        }

        TimeSpan interval = TimeSpan.FromMinutes(settings.IntervalMinutes);
        TimeSpan startupDelay = TimeSpan.FromSeconds(Math.Max(0, settings.StartupDelaySeconds));

        logger.LogInformation(
            "The media derivative backfill sweep runs every {IntervalMinutes} minute(s), at most {BatchSize} " +
            "source(s) per sweep",
            settings.IntervalMinutes, Math.Max(1, settings.BatchSize));

        try
        {
            await Task.Delay(startupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSweepAsync(stoppingToken);

                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// One sweep. Public so the timer and a test drive the same path. Swallows a failure of the sweep
    /// itself (a database outage, a malformed query) after logging it: the next tick tries again, and a
    /// background service that dies silently would be worse than one that retries.
    /// </summary>
    public async Task<MediaDerivativeSweepResult?> RunSweepAsync(CancellationToken ct)
    {
        MediaDerivativeBackfillOptions settings = options.Value;

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            MediaDerivativeCoverageService coverage =
                scope.ServiceProvider.GetRequiredService<MediaDerivativeCoverageService>();

            MediaDerivativeSweepResult result = await coverage.SweepOnceAsync(
                MediaDerivativeRequests.RequestedBySweep,
                Math.Max(1, settings.BatchSize),
                ct);

            MediaDerivativeCoverageReport report = result.Coverage;

            // One line, every sweep: the count of video sources that would still play their original,
            // which is the metric that was invisible while users were reporting buffering.
            logger.LogInformation(
                "Media derivative coverage: {VideoSources} video source(s), {WithProxy} with a proxy, " +
                "{WithoutProxy} without (project files {FileTotal}/{FileWithProxy}, step-result renders " +
                "{RenderTotal}/{RenderWithProxy}); requested {Requested}, deferred {Deferred}, failed {Failed}",
                report.VideoSources, report.WithProxy, report.WithoutProxy,
                report.VideoProjectFiles.Total, report.VideoProjectFiles.WithProxy,
                report.StepResultRenders.Total, report.StepResultRenders.WithProxy,
                result.Requested, result.Deferred, result.Failed);

            if (result.Failed > 0)
            {
                logger.LogWarning(
                    "Media derivative backfill could not publish {Failed} request(s) this sweep; those sources " +
                    "stay candidates and are retried on the next sweep",
                    result.Failed);
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The media derivative backfill sweep failed; retrying after the interval");
            return null;
        }
    }
}
