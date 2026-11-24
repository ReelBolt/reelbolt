using Amazon.S3;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// <c>GET /api/v1/output-links/{token}</c>: the bytes behind a short-lived output link minted by
/// <see cref="IOutputLinkService"/> (the assistant's <c>GetOutput</c>), so an MCP client can download
/// or look at a render without a session cookie and without ever seeing a storage key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anonymous, but not unauthenticated.</b> The token is the credential: it is HMAC'd and bound to
/// the user it was minted for. After the signature and expiry check, <see cref="IOutputLinkResolver"/>
/// re-reads the user, their membership and role in the project's organization, and the row the link
/// names, and resolves the storage key from that row — the token never carries one.
/// </para>
/// <para>
/// <b>Every failure is the same empty 404</b> — bad signature, expired, user gone, no access, wrong
/// project, key outside its prefix, object missing — so a probe learns nothing. Responses are
/// <c>Cache-Control: private, no-store</c> and served under <see cref="SafeContentPolicy"/> with Range
/// support (<see cref="RangedObjectStreamer"/>). The token is never logged here.
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("api/v1/output-links")]
public sealed class OutputLinksController : ControllerBase
{
    private readonly IOutputLinkService _links;
    private readonly IOutputLinkResolver _resolver;
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly TimeProvider _clock;

    private readonly ReelBolt.Inference.Api.Services.Moderation.PublishModeration? _publishModeration;

    public OutputLinksController(
        IOutputLinkService links,
        IOutputLinkResolver resolver,
        IAmazonS3 s3,
        IConfiguration configuration,
        TimeProvider? clock = null,
        ReelBolt.Inference.Api.Services.Moderation.PublishModeration? publishModeration = null)
    {
        _publishModeration = publishModeration;
        _links = links;
        _resolver = resolver;
        _s3 = s3;
        _bucket = configuration["MinIO:BucketName"] ?? "reelforge";
        _clock = clock ?? TimeProvider.System;
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> Get(string token, CancellationToken ct)
    {
        Response.Headers[HeaderNames.CacheControl] = "private, no-store";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";

        if (!_links.TryVerify(token, _clock.GetUtcNow(), out OutputLinkClaims claims)) return NotFound();

        ResolvedOutputObject? resolved = await _resolver.ResolveAsync(claims, ct);
        if (resolved is null) return NotFound();
        // The link was minted against an earlier verdict; the bytes served now need a current one, by the very rule
        // the mint applies (wordless audio and the FailClosedAtPublish stance included), with the same uniform 404.
        if (_publishModeration is not null
            && !(await _publishModeration.EvaluateObjectAsync(
                resolved.ProjectId,
                resolved.StorageKey,
                resolved.ContentType,
                new ReelBolt.Shared.Moderation.ModerationAttribution(
                    Guid.Empty, resolved.ProjectId, null, null, null,
                    ReelBolt.Shared.Moderation.ModerationSurface.Publish, ReelBolt.Shared.Moderation.ModerationContentKind.Text),
                ct)).Allowed)
            return NotFound();

        try
        {
            return await RangedObjectStreamer.StreamAsync(
                this, _s3, _bucket, resolved.StorageKey, resolved.ContentType, resolved.FileName, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound ||
                                           ex.ErrorCode is "NoSuchKey" or "NotFound")
        {
            return NotFound();
        }
    }
}
