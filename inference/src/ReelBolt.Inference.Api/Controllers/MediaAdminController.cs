using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>One bucket of the coverage report, as the admin surface publishes it.</summary>
public sealed record MediaDerivativeCoverageBucket(int Total, int WithProxy, int WithoutProxy);

/// <summary>
/// The platform-wide answer to "how many video sources still play their original".
/// </summary>
/// <remarks>
/// <b>Routed under <c>/api/v1/media/admin</c>, not <c>/api/v1/admin/*</c>.</b> nginx sends the latter
/// prefix to the Go API (the same constraint that puts <c>BillingAdminController</c> under
/// <c>/api/v1/billing/admin</c>), so a coverage route there would never reach this service.
/// </remarks>
public sealed record MediaDerivativeCoverageResponse(
    int VideoSources,
    int WithProxy,
    int WithoutProxy,
    MediaDerivativeCoverageBucket ProjectFiles,
    MediaDerivativeCoverageBucket StepResultRenders,
    bool SweepEnabled,
    int SweepIntervalMinutes,
    DateTime AsOf);

/// <summary>Body of <c>POST /api/v1/media/admin/backfill</c>; every field is optional.</summary>
public sealed record MediaDerivativeBackfillRequest(int? BatchSize = null);

/// <summary>What one backfill run did. <see cref="Coverage"/> is the state as the sweep observed it.</summary>
public sealed record MediaDerivativeBackfillResponse(
    int Requested,
    int Deferred,
    int Failed,
    bool BatchFull,
    MediaDerivativeCoverageResponse Coverage);

/// <summary>
/// The operator surface for the derivative backlog: read the coverage, or repair it now.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both actions are platform-admin only and answer <c>403</c> otherwise.</b> This reads across every
/// organization and enqueues work for all of them, so it is not an org role and not a project
/// permission — the same authority discipline as the other admin surfaces, and deliberately no
/// enumeration-safe <c>404</c>: there is nothing to hide.
/// </para>
/// <para>
/// <b>Backfill does not wait for the engine.</b> It publishes the requests and answers <c>202</c>; the engine
/// produces the derivatives on its own derivative slots, and the next coverage read is how an operator
/// confirms the backlog drained. Re-running it is safe: a source with a live proxy is no longer a
/// candidate, and a repeat that races the engine is a cache hit.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/media/admin")]
[Authorize]
public class MediaAdminController(
    MediaDerivativeCoverageService coverage,
    ICurrentUser currentUser,
    IOptions<MediaDerivativeBackfillOptions> options) : ControllerBase
{
    /// <summary>Most sources one manual backfill may ask for, so a call cannot stampede the engine.</summary>
    public const int MaxBackfillBatch = 5000;

    /// <summary>The coverage counts: video sources, how many have a proxy, and the per-table split.</summary>
    [HttpGet("coverage")]
    public async Task<ActionResult<MediaDerivativeCoverageResponse>> GetCoverage(CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        MediaDerivativeCoverageReport report = await coverage.GetCoverageAsync(ct);
        return Ok(ToResponse(report));
    }

    /// <summary>
    /// Request the derivative set for every video source with no proxy — one batch per call, oldest
    /// source first. Idempotent: a re-run requests only what is still missing.
    /// </summary>
    [HttpPost("backfill")]
    public async Task<ActionResult<MediaDerivativeBackfillResponse>> Backfill(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] MediaDerivativeBackfillRequest? request,
        CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        MediaDerivativeBackfillOptions settings = options.Value;

        int batchSize = request?.BatchSize is > 0
            ? Math.Min(request.BatchSize.Value, MaxBackfillBatch)
            : Math.Max(1, settings.BatchSize);

        MediaDerivativeSweepResult result = await coverage.SweepOnceAsync(
            MediaDerivativeRequests.RequestedByBackfill,
            batchSize,
            ct);

        return Accepted(new MediaDerivativeBackfillResponse(
            result.Requested,
            result.Deferred,
            result.Failed,
            result.BatchFull,
            ToResponse(result.Coverage)));
    }

    private MediaDerivativeCoverageResponse ToResponse(MediaDerivativeCoverageReport report)
    {
        MediaDerivativeBackfillOptions settings = options.Value;

        return new MediaDerivativeCoverageResponse(
            report.VideoSources,
            report.WithProxy,
            report.WithoutProxy,
            new MediaDerivativeCoverageBucket(
                report.VideoProjectFiles.Total,
                report.VideoProjectFiles.WithProxy,
                report.VideoProjectFiles.WithoutProxy),
            new MediaDerivativeCoverageBucket(
                report.StepResultRenders.Total,
                report.StepResultRenders.WithProxy,
                report.StepResultRenders.WithoutProxy),
            settings.Enabled && settings.IntervalMinutes > 0,
            settings.IntervalMinutes,
            report.AsOf);
    }
}
