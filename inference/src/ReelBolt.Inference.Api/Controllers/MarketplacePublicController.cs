using System.Collections.Concurrent;
using System.Security.Cryptography;
using Amazon.S3;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using ReelBolt.Inference.Api.Services.Marketplace;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Anonymous read side of the recipe marketplace (<c>/api/v1/public/marketplace</c>; GET and HEAD only, nothing read from a body,
/// nothing written). Every failure that could reveal whether a recipe exists (flag off, not listed, preview not ready, missing or
/// altered object) is the same bare 404 with no body and no extra headers. No author, organization, project or storage id is
/// ever in a response.
/// </summary>
[AllowAnonymous]
[Route("api/v1/public/marketplace")]
[TypeFilter(typeof(MarketplaceEnabledFilter))]
public sealed class MarketplacePublicController(
    MarketplacePublicQuery query,
    IAmazonS3 s3,
    IMarketplacePreviewObjects objects,
    IConfiguration configuration,
    ILogger<MarketplacePublicController> logger) : ControllerBase
{
    // A version is immutable, so a preview that hashed right once keeps hashing right: remember it per (version, sha).
    private static readonly ConcurrentDictionary<(Guid Version, string Kind, string Sha), bool> VerifiedMedia = new();
    private static readonly ConcurrentDictionary<(Guid Version, string Kind, string Sha), DateTime> BrokenMedia = new();

    /// <summary>Preview and poster lifetime in caches: short, so a removed or replaced object leaves them quickly.</summary>
    public const int MediaMaxAgeSeconds = 120;

    private readonly string _bucket = configuration["MinIO:BucketName"] ?? "reelforge";

    [AcceptVerbs("GET", "HEAD")]
    [Route("recipes")]
    public async Task<IActionResult> List(
        [FromQuery] string? q, [FromQuery] string? category, [FromQuery] string? cost, [FromQuery] string? stepType,
        [FromQuery] string? language, [FromQuery] string? sort, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        try
        {
            RecipeListDto result = await query.ListAsync(
                new RecipeListQuery(q, category, cost, stepType, language, sort, cursor, limit ?? MarketplacePublicQuery.DefaultLimit), ct);
            return Json(result, 30);
        }
        catch (MarketplaceBadRequestException)
        {
            return StatusCode(StatusCodes.Status400BadRequest);
        }
    }

    [AcceptVerbs("GET", "HEAD")]
    [Route("recipes/{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        RecipeDetailDto? detail = await query.DetailAsync(id, ct);
        return detail is null ? Bare404() : Json(detail, 60);
    }

    [AcceptVerbs("GET", "HEAD")]
    [Route("recipes/{id:guid}/preview")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct)
    {
        RecipeMedia? media = await query.MediaAsync(id, ct);
        if (media?.PreviewKey is not { } key || !MarketplacePreviewKeys.IsVersionObject(media.RecipeId, media.VersionId, key))
            return Bare404();
        if (!await IntactAsync(media.VersionId, "preview", media.PreviewSha256, key, ct))
            return Bare404();
        return await ServeAsync(key, media.PreviewSha256!, "video/mp4", "preview.mp4", ct);
    }

    [AcceptVerbs("GET", "HEAD")]
    [Route("recipes/{id:guid}/poster")]
    public async Task<IActionResult> Poster(Guid id, CancellationToken ct)
    {
        RecipeMedia? media = await query.MediaAsync(id, ct);
        if (media?.PosterKey is not { } key || !MarketplacePreviewKeys.IsVersionObject(media.RecipeId, media.VersionId, key))
            return Bare404();
        if (!await IntactAsync(media.VersionId, "poster", media.PosterSha256, key, ct))
            return Bare404();
        return await ServeAsync(key, media.PosterSha256!, "image/jpeg", "poster.jpg", ct);
    }

    [AcceptVerbs("GET", "HEAD")]
    [Route("facets")]
    public async Task<IActionResult> Facets(CancellationToken ct) => Json(await query.FacetsAsync(ct), 60);

    private ObjectResult Json<T>(T value, int maxAgeSeconds)
    {
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers[HeaderNames.CacheControl] = $"public, max-age={maxAgeSeconds}";
        return new ObjectResult(value) { StatusCode = StatusCodes.Status200OK, ContentTypes = { "application/json" } };
    }

    private IActionResult Bare404()
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        return new EmptyResult();
    }

    private async Task<bool> IntactAsync(Guid versionId, string kind, string? sha, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            logger.LogWarning("A marketplace {Kind} has no stored hash and is not served", kind);
            return false;
        }

        (Guid, string, string) cacheKey = (versionId, kind, sha.Trim().ToLowerInvariant());
        if (VerifiedMedia.ContainsKey(cacheKey))
            return true;
        // A mismatch is remembered for a short while so a broken object is not re-downloaded and re-hashed on every request.
        if (BrokenMedia.TryGetValue(cacheKey, out DateTime until) && until > DateTime.UtcNow)
            return false;
        try
        {
            await using Stream stream = await objects.OpenReadAsync(key, ct);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (!string.Equals(actual, cacheKey.Item3, StringComparison.Ordinal))
            {
                BrokenMedia[cacheKey] = DateTime.UtcNow.AddMinutes(1);
                logger.LogWarning("A marketplace {Kind} does not match its stored hash and is not served", kind);
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("A marketplace {Kind} object could not be read and is not served ({Error})", kind, ex.GetType().Name);
            return false;
        }

        BrokenMedia.TryRemove(cacheKey, out _);
        VerifiedMedia[cacheKey] = true;
        return true;
    }

    private async Task<IActionResult> ServeAsync(string key, string sha, string contentType, string fileName, CancellationToken ct)
    {
        string etag = $"\"{sha.Trim().ToLowerInvariant()}\"";
        if (Request.Headers.IfNoneMatch.ToString() is { Length: > 0 } inm
            && inm.Split(',').Select(t => t.Trim()).Any(t => t == "*" || t == etag || t == "W/" + etag))
        {
            Response.Headers[HeaderNames.ETag] = etag;
            Response.Headers[HeaderNames.CacheControl] = $"public, max-age={MediaMaxAgeSeconds}";
            return StatusCode(StatusCodes.Status304NotModified);
        }

        try
        {
            Response.Headers[HeaderNames.CacheControl] = $"public, max-age={MediaMaxAgeSeconds}";
            Response.Headers[HeaderNames.ETag] = etag;
            return await RangedObjectStreamer.StreamAsync(this, s3, _bucket, key, contentType, fileName, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.ErrorCode is "NoSuchKey" or "NotFound")
        {
            Response.Headers.Remove(HeaderNames.CacheControl);
            Response.Headers.Remove(HeaderNames.ETag);
            return Bare404();
        }
    }
}
