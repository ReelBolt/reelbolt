using System.Security.Cryptography;
using Amazon.S3;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using ReelBolt.Inference.Api.Services.Marketplace;
using ReelBolt.Inference.Api.Services.Storage;

namespace ReelBolt.Inference.Api.Controllers;

internal static class MarketplaceResultMapper
{
    public static IActionResult Map<T>(MarketplaceResult<T> result, int successStatus = StatusCodes.Status200OK)
    {
        if (result.Error is not { } error)
            return new ObjectResult(result.Value) { StatusCode = successStatus };
        if (error.Code is null)
            return new StatusCodeResult(error.Status);
        return new ObjectResult(new { code = error.Code, message = error.Message }) { StatusCode = error.Status };
    }
}

/// <summary>User reports of a listed recipe. Bare 404 while the flag is off; verified email; 10 per user per day.</summary>
[ApiController]
[Route("api/v1/marketplace/recipes/{id:guid}/report")]
[Authorize]
[TypeFilter(typeof(MarketplaceEnabledFilter))]
public sealed class MarketplaceReportsController(MarketplaceReportService service) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ReportRecipeResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Report(Guid id, [FromBody] ReportRecipeRequest request, CancellationToken ct) =>
        MarketplaceResultMapper.Map(await service.ReportAsync(id, request, ct));
}

/// <summary>
/// Platform-admin side of the marketplace, under <c>/api/v1/marketplace/admin</c> (NOT <c>/api/v1/admin/*</c>: nginx sends that prefix
/// to the Go API). Every action starts with an admin check re-read from the database; a non-admin gets a bare 403, the convention of
/// <c>/api/v1/moderation</c>. Bare 404 while the flag is off.
/// </summary>
[ApiController]
[Route("api/v1/marketplace/admin")]
[Authorize]
[TypeFilter(typeof(MarketplaceEnabledFilter))]
public sealed class MarketplaceAdminController(
    MarketplaceAdminService service,
    IAmazonS3 s3,
    IMarketplacePreviewObjects objects,
    IConfiguration configuration,
    ILogger<MarketplaceAdminController> logger) : ControllerBase
{
    private readonly string _bucket = configuration["MinIO:BucketName"] ?? "reelforge";

    /// <summary>The preview under review: that version's own final object, hash-checked, never cached. Any failure is a bare 404.</summary>
    [AcceptVerbs("GET", "HEAD")]
    [Route("versions/{versionId:guid}/preview")]
    public Task<IActionResult> VersionPreview(Guid versionId, CancellationToken ct) => ServeAsync(versionId, poster: false, ct);

    [AcceptVerbs("GET", "HEAD")]
    [Route("versions/{versionId:guid}/poster")]
    public Task<IActionResult> VersionPoster(Guid versionId, CancellationToken ct) => ServeAsync(versionId, poster: true, ct);

    private async Task<IActionResult> ServeAsync(Guid versionId, bool poster, CancellationToken ct)
    {
        MarketplaceResult<AdminMediaRef> media = await service.MediaAsync(versionId, poster, ct);
        if (media.Value is not { } m)
            return new StatusCodeResult(StatusCodes.Status404NotFound);

        try
        {
            // The preview has a recorded hash and is checked on every serve (a reviewer's view is rare and must be the real object).
            if (!poster)
            {
                if (string.IsNullOrWhiteSpace(m.Sha256))
                    return new StatusCodeResult(StatusCodes.Status404NotFound);
                await using Stream stream = await objects.OpenReadAsync(m.Key, ct);
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
                if (!string.Equals(actual, m.Sha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                {
                    logger.LogWarning("A marketplace preview under review does not match its stored hash and is not served");
                    return new StatusCodeResult(StatusCodes.Status404NotFound);
                }
            }

            Response.Headers[HeaderNames.CacheControl] = "private, no-store";
            return await RangedObjectStreamer.StreamAsync(
                this, s3, _bucket, m.Key, poster ? "image/jpeg" : "video/mp4", poster ? "poster.jpg" : "preview.mp4", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Response.Headers.Remove(HeaderNames.CacheControl);
            logger.LogWarning("A marketplace object under review could not be served ({Error})", ex.GetType().Name);
            return new StatusCodeResult(StatusCodes.Status404NotFound);
        }
    }

    [HttpGet("queue")]
    public async Task<IActionResult> Queue([FromQuery] string? state, [FromQuery] string? cursor, CancellationToken ct) =>
        MarketplaceResultMapper.Map(await service.QueueAsync(state, cursor, ct));

    [HttpGet("reports")]
    public async Task<IActionResult> Reports(CancellationToken ct) => MarketplaceResultMapper.Map(await service.ReportsAsync(ct));

    [HttpGet("recipes")]
    public async Task<IActionResult> Recipes([FromQuery] string? status, CancellationToken ct) =>
        MarketplaceResultMapper.Map(await service.RecipesAsync(status, ct));

    [HttpGet("recipes/{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct) => MarketplaceResultMapper.Map(await service.DetailAsync(id, ct));

    [HttpGet("recipes/{id:guid}/copies")]
    public async Task<IActionResult> Copies(Guid id, CancellationToken ct) => MarketplaceResultMapper.Map(await service.CopiesAsync(id, ct));

    [HttpPost("recipes/{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] AdminApproveRequest? request, CancellationToken ct) =>
        MarketplaceResultMapper.Map(await service.ApproveAsync(id, request ?? new AdminApproveRequest(null, null, null), ct));

    [HttpPost("recipes/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] AdminRejectRequest request, CancellationToken ct) =>
        MarketplaceResultMapper.Map(await service.RejectAsync(id, request, ct));

    [HttpPost("recipes/{id:guid}/remove")]
    public async Task<IActionResult> Remove(Guid id, [FromBody] AdminRemoveRequest request, CancellationToken ct) =>
        MarketplaceResultMapper.Map(await service.RemoveAsync(id, request, ct));

    [HttpPost("recipes/{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct) => MarketplaceResultMapper.Map(await service.RestoreAsync(id, ct));

    [HttpPost("reports/{id:guid}/dismiss")]
    public async Task<IActionResult> Dismiss(Guid id, CancellationToken ct) => MarketplaceResultMapper.Map(await service.DismissReportAsync(id, ct));

    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] Guid? recipeId, [FromQuery] int? limit, CancellationToken ct) =>
        MarketplaceResultMapper.Map(await service.AuditAsync(recipeId, limit, ct));
}
