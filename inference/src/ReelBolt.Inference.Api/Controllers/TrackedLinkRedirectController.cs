using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ReelBolt.Inference.Api.Services.Marketing.Links;
using ReelBolt.Shared.Acquisition;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// <c>GET /api/v1/l/{code}</c>: the tracked link (docs/design/new-user-funnel.md section 6). The public
/// host maps <c>/l/{code}</c> here. An active link counts the hit and answers <c>302</c> to its landing
/// page with the link's UTM parameters; anything else answers <c>302</c> to <c>/</c> and counts nothing.
/// </summary>
/// <remarks>
/// <para><b>A posted link must keep working.</b> An unknown, malformed or archived code is never a 404:
/// the link is out in the world and the person who clicked it still deserves the site. A failure to read
/// the link falls back to <c>/</c> the same way, and a failure to count never touches the redirect. The
/// only 404 is the operator's own switch, <c>Marketing:Links:Enabled = false</c>.</para>
/// <para><b>No cookie, nothing about the caller.</b> The counter is a number per link and UTC day. The
/// user agent and the browser's own request-kind headers (<c>Sec-Fetch-Mode</c>, <c>Sec-Fetch-Dest</c>,
/// <c>Sec-Purpose</c>, <c>Purpose</c>, <c>X-Purpose</c>, <c>X-Moz</c>) are read once to choose between
/// <c>clicks</c> and <c>bot_hits</c> (<see cref="LinkPreviewAgents"/>) and dropped; no IP address, user
/// agent, header, referrer or identifier is stored or logged here, and nothing is written to or read from
/// the device.</para>
/// <para><b>What a click is.</b> A <c>GET</c> that looks like a person opening the link as a page: not a
/// known fetcher, library or headless browser, not a prefetch or preview, and, when the browser sends
/// fetch metadata, a top-level navigation. Everything else is a bot hit. An edge in front of this API
/// must forward those headers for the last two rules to apply; without them the user agent decides.</para>
/// <para><b>Never an open redirect.</b> <c>Location</c> is always relative (a path on the host the request
/// came to) and is built from a stored destination that is checked again here
/// (<see cref="TrackedLinkRules.IsDestinationPath"/>) and from closed-set tokens. <c>Cache-Control: no-store</c>
/// keeps a browser or proxy from replaying the redirect without the count.</para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("api/v1/l")]
public sealed class TrackedLinkRedirectController(
    IOptions<MarketingLinksOptions> options,
    ITrackedLinkDirectory links,
    ILogger<TrackedLinkRedirectController> logger,
    TimeProvider? clock = null) : ControllerBase
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Follows a tracked link. <c>HEAD</c> is answered like <c>GET</c> (link checkers use it) and always
    /// counts as a bot hit: a browser navigating to a link sends <c>GET</c>. The code is matched without
    /// regard to letter case (<see cref="TrackedLinkRules.NormalizeCode"/>). The route takes everything
    /// after <c>/l/</c>, so a mangled address (nothing after the slash, or more path segments) is a
    /// malformed code and redirects to the home page instead of falling out of routing as a 404.
    /// </summary>
    [AcceptVerbs("GET", "HEAD")]
    [Route("{**code}")]
    public async Task<IActionResult> Follow(string? code, CancellationToken ct)
    {
        if (!options.Value.Enabled) return NotFound();

        Response.Headers[HeaderNames.CacheControl] = "no-store";

        // A link code is lower-case, but a link is typed, pasted and re-cased by the places it is posted
        // in. One edge (the Pages Function) lowers the code before it calls here and the other (nginx)
        // forwards it as typed, so the code is lowered here: /l/HN-Launch is the same link on every edge.
        code = TrackedLinkRules.NormalizeCode(code);
        if (code is null || !AcquisitionVocabulary.IsLinkCode(code)) return Found(TrackedLinkRules.FallbackPath);

        MarketingLink? link;
        try
        {
            link = await links.FindActiveAsync(code, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Tracked link {Code} could not be read; redirecting to the home page with the code", code);
            // The code is well formed (checked above), so it is safe in a query. The visit still joins the
            // link, as it does when nginx or the Pages Function cannot reach the API.
            return Found(TrackedLinkRules.FallbackPath + "?utm_content=" + code);
        }

        if (link is null || !TrackedLinkRules.IsDestinationPath(link.DestinationPath))
            return Found(TrackedLinkRules.FallbackPath);

        bool bot = HttpMethods.IsHead(Request.Method) || LinkPreviewAgents.IsBot(Signals(Request.Headers));
        try
        {
            await links.RecordHitAsync(link.Id, DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime), bot, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The count is best effort; the person who clicked still gets the page.
            logger.LogWarning(ex, "Tracked link {Code}: the hit was not counted", code);
        }

        return Found(TrackedLinkRules.LongPath(link));
    }

    /// <summary>The headers the click rule reads. Nothing else of the request is looked at, and none of this is kept.</summary>
    private static LinkRequestSignals Signals(IHeaderDictionary headers) => new(
        headers.UserAgent.ToString(),
        headers["Sec-Fetch-Mode"].ToString(),
        headers["Sec-Fetch-Dest"].ToString(),
        headers["Sec-Purpose"].ToString(),
        headers["Purpose"].ToString(),
        headers["X-Purpose"].ToString(),
        headers["X-Moz"].ToString());

    /// <summary>A <c>302</c> whose <c>Location</c> is exactly <paramref name="relativePath"/>: never rewritten to an absolute URL.</summary>
    private StatusCodeResult Found(string relativePath)
    {
        Response.Headers[HeaderNames.Location] = relativePath;
        return StatusCode(StatusCodes.Status302Found);
    }
}
