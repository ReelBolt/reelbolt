using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Telemetry;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The anonymous, consent-gated visit beacon (docs/design/new-user-funnel.md sections 4 and 6): the five
/// visitor events that exist BEFORE an account does (<see cref="ProductEventNames.VisitEvents"/>). Off by
/// default (404). An event row carries a random visitor id and one closed token; the visitor's first touch
/// (sanitized campaign parameters, referrer host, landing path) is stored once per visitor in
/// <c>visitor_attribution</c>. No user, no organization, no IP, no user agent, no free text.
/// </summary>
[ApiController]
[Route("api/v1/telemetry")]
public class TelemetryVisitController(
    IOptions<AlphaTelemetryOptions> options,
    IProductEventLog productEvents) : ControllerBase
{
    /// <summary>The largest request body accepted, in bytes.</summary>
    public const int MaxBodyBytes = 2048;

    /// <summary>The window in which a repeat of the same (visitor, event, detail) is dropped.</summary>
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Records one visitor event. The body is read here, not model-bound, so a disabled endpoint answers
    /// 404 even for a malformed body. A repeat within <see cref="DedupeWindow"/> answers 204 and writes no
    /// event row.
    /// <para>
    /// What the stored <c>detail</c> is depends on the event. <c>site.visit</c> and <c>signup.started</c>:
    /// the source label, computed here from the attribution fields (a <c>detail</c> in the body is
    /// ignored). <c>site.page_view</c> and <c>site.cta_click</c>: the body's own <c>detail</c> (the page
    /// key, the CTA location), which must be one closed token; without it the row would mean nothing, so
    /// a missing or malformed one is a 400. <c>signup.submitted</c>: none.
    /// </para>
    /// </summary>
    [HttpPost("visit")]
    [AllowAnonymous]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Visit(CancellationToken ct)
    {
        if (!options.Value.VisitIngestEnabled) return NotFound();
        if (Request.ContentLength is > MaxBodyBytes) return BadRequest(new { error = "body_too_large" });

        byte[] buffer = new byte[MaxBodyBytes + 1];
        int read = 0;
        try
        {
            while (read < buffer.Length)
            {
                int n = await Request.Body.ReadAsync(buffer.AsMemory(read), ct);
                if (n == 0) break;
                read += n;
            }
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            return BadRequest(new { error = "body_too_large" });
        }

        if (read > MaxBodyBytes) return BadRequest(new { error = "body_too_large" });
        VisitEventDto? body;
        try
        {
            body = System.Text.Json.JsonSerializer.Deserialize<VisitEventDto>(
                buffer.AsSpan(0, read), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        if (body is null || !ProductEventNames.IsVisitEvent(body.Event))
            return BadRequest(new { error = "unknown_event" });
        if (AttributionSanitizer.VisitorId(body.VisitorId) is not { } visitorId)
            return BadRequest(new { error = "invalid_visitor_id" });

        string eventName = body.Event!;
        string? ownDetail = null;
        if (eventName is ProductEventNames.SitePageView or ProductEventNames.SiteCtaClick)
        {
            ownDetail = AttributionSanitizer.DetailToken(body.Detail);
            if (ownDetail is null) return BadRequest(new { error = "invalid_detail" });
        }

        // Own hosts: the configured ones plus the host this request arrived on. Invalid attribution
        // fields are dropped, never rejected: a bad utm must not lose the visit itself. Sanitized
        // exactly as the Go API sanitizes user_attribution: only the source is refused for being an own
        // host (the site may send its own label there); the other utm values are plain tokens.
        List<string> own = [.. options.Value.OwnHosts ?? []];
        if (!string.IsNullOrEmpty(Request.Host.Host)) own.Add(Request.Host.Host);
        string? utmSource = AttributionSanitizer.Utm(body.Source, own);
        string? referrerHost = AttributionSanitizer.ReferrerHost(body.ReferrerHost, own);
        string label = AttributionSanitizer.SourceLabel(utmSource, referrerHost);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        VisitorAttribution firstTouch = new()
        {
            VisitorId = visitorId,
            SourceLabel = label,
            UtmSource = utmSource,
            UtmMedium = AttributionSanitizer.Utm(body.Medium),
            UtmCampaign = AttributionSanitizer.Utm(body.Campaign),
            UtmContent = AttributionSanitizer.Utm(body.Content),
            UtmTerm = AttributionSanitizer.Utm(body.Term),
            ReferrerHost = referrerHost,
            LandingPath = AttributionSanitizer.LandingPath(body.LandingPath),
            FirstTouchAt = now,
            CreatedAt = now,
        };

        // One statement: the first touch (kept only if the visitor has none yet) and the event row
        // (skipped when the same visitor, event and detail was recorded inside the window).
        await productEvents.RecordVisitAsync(
            firstTouch,
            new ProductEvent
            {
                UserId = null,
                OrganizationId = null,
                VisitorId = visitorId,
                Event = eventName,
                OccurredAt = now,
                Source = ProductEventSources.Client,
                Detail = eventName switch
                {
                    ProductEventNames.SiteVisit or ProductEventNames.SignupStarted => label,
                    ProductEventNames.SitePageView or ProductEventNames.SiteCtaClick => ownDetail,
                    _ => null,
                },
            },
            now - DedupeWindow,
            ct);
        return NoContent();
    }
}
