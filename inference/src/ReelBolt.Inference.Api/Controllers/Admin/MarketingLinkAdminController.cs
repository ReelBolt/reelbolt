using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Marketing.Links;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Controllers.Admin;

/// <summary>The body of <c>POST /api/v1/marketing/admin/links</c>. Persona and cost are optional.</summary>
public sealed record MarketingLinkCreateRequest(
    string? Code,
    string? Channel,
    string? Medium,
    string? Campaign,
    string? Placement,
    string? DestinationPath,
    string? Persona,
    long? CostMinor,
    string? CostCurrency);

/// <summary>A tracked link as the admin API returns it (list item, create and patch response).</summary>
public sealed record MarketingLinkResponse(
    Guid Id,
    string Code,
    string Channel,
    string Medium,
    string Campaign,
    string Placement,
    string DestinationPath,
    string? Persona,
    long? CostMinor,
    string? CostCurrency,
    DateTime CreatedAt,
    DateTime? ArchivedAt,
    string ShortPath,
    string LongPath,
    long TotalClicks);

/// <summary>The list response of <c>GET /api/v1/marketing/admin/links</c>.</summary>
public sealed record MarketingLinkListResponse(IReadOnlyList<MarketingLinkResponse> Items);

/// <summary>One link's row of <c>GET /api/v1/marketing/admin/links/funnel</c>.</summary>
public sealed record MarketingLinkFunnelItem(
    Guid LinkId,
    string Code,
    string Channel,
    string Medium,
    string Campaign,
    string Placement,
    string DestinationPath,
    bool Archived,
    long? CostMinor,
    string? CostCurrency,
    long Clicks,
    long BotHits,
    long Visitors,
    long Signups,
    long Verified,
    long Onboarded,
    long FirstProject,
    long FirstRun,
    long FirstVideo,
    long Paid);

/// <summary>
/// The accounts of the window that belong to no link: no link code, or a <c>utm_content</c> that is no
/// link's code. With the link rows it is every account of the window, each counted once.
/// </summary>
public sealed record MarketingLinkFunnelUnattributed(
    long Signups, long Verified, long Onboarded, long FirstProject, long FirstRun, long FirstVideo, long Paid);

/// <summary>
/// The response of <c>GET /api/v1/marketing/admin/links/funnel</c>. The two switches say how to read the
/// counts, so a zero that a switch caused is never mistaken for "nobody came":
/// <list type="bullet">
/// <item><description><paramref name="UrlCaptureEnabled"/>: whether an account can carry a link code without
/// a consented cookie (<c>ATTRIBUTION_URL_CAPTURE_ENABLED</c>). Off: account counts per link are a floor.</description></item>
/// <item><description><paramref name="VisitIngestEnabled"/>: whether the visit beacon is accepted at all
/// (<c>TELEMETRY_VISIT_INGEST_ENABLED</c>). Off, the shipped default: no visitor row is ever written, so
/// every link's <c>visitors</c> is 0 whatever people accepted on the site.</description></item>
/// </list>
/// </summary>
public sealed record MarketingLinkFunnelResponse(
    int Days,
    DateTimeOffset GeneratedAt,
    bool UrlCaptureEnabled,
    bool VisitIngestEnabled,
    IReadOnlyList<MarketingLinkFunnelItem> Items,
    MarketingLinkFunnelUnattributed Unattributed);

/// <summary>
/// Tracked links, platform admin only (re-checked first in every action; an organization Owner never
/// satisfies it). List, create and edit links, and read each link's funnel
/// (docs/design/new-user-funnel.md section 6). Links are never deleted: a posted link is archived, after
/// which it redirects to the home page and counts nothing, and its code is never reused.
/// </summary>
[ApiController]
[Route("api/v1/marketing/admin/links")]
[Authorize]
public class MarketingLinkAdminController(
    TrackedLinkService links,
    ITrackedLinkFunnelReader funnel,
    IOptions<AttributionOptions> attribution,
    IOptions<AlphaTelemetryOptions> telemetry,
    ICurrentUser currentUser,
    TimeProvider? clock = null) : ControllerBase
{
    /// <summary>The funnel window when <c>days</c> is not given.</summary>
    public const int DefaultFunnelDays = 30;

    /// <summary>The widest funnel window.</summary>
    public const int MaxFunnelDays = 365;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    [HttpGet("")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        IReadOnlyList<TrackedLinkWithClicks> rows = await links.ListAsync(ct);
        return Ok(new MarketingLinkListResponse([.. rows.Select(r => View(r.Link, r.TotalClicks))]));
    }

    [HttpPost("")]
    public async Task<IActionResult> Create([FromBody] MarketingLinkCreateRequest request, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        try
        {
            MarketingLink link = await links.CreateAsync(
                new TrackedLinkInput(
                    request.Code, request.Channel, request.Medium, request.Campaign, request.Placement,
                    request.DestinationPath, request.Persona, request.CostMinor, request.CostCurrency),
                currentUser.UserId,
                ct);
            return Ok(View(link, totalClicks: 0));
        }
        catch (TrackedLinkRefusal refusal)
        {
            return Refusal(refusal);
        }
    }

    /// <summary>
    /// Edits <c>placement</c>, <c>destinationPath</c>, <c>persona</c>, <c>costMinor</c>, <c>costCurrency</c>
    /// and <c>archived</c>. The body is read as JSON so an absent field (left alone) is told from an
    /// explicit <c>null</c> (cleared). Code, channel, medium and campaign are immutable.
    /// </summary>
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Patch(Guid id, [FromBody] JsonElement body, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        try
        {
            TrackedLinkWithClicks? row = await links.PatchAsync(id, body, ct);
            return row is null ? NotFound() : Ok(View(row.Link, row.TotalClicks));
        }
        catch (TrackedLinkRefusal refusal)
        {
            return Refusal(refusal);
        }
    }

    /// <summary>
    /// One row per link for the last <paramref name="days"/> UTC calendar days, today included (default 30,
    /// clamped to 1..365), plus the accounts of the window that belong to no link. The counts and their
    /// definitions are <see cref="TrackedLinkFunnelReader"/>'s. Platform admins and API-key service accounts
    /// are in no count.
    /// </summary>
    [HttpGet("funnel")]
    public async Task<IActionResult> Funnel([FromQuery] int days = DefaultFunnelDays, CancellationToken ct = default)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        days = Math.Clamp(days, 1, MaxFunnelDays);
        DateTimeOffset now = _clock.GetUtcNow();
        DateOnly sinceDay = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-(days - 1));

        TrackedLinkFunnel result = await funnel.ReadAsync(sinceDay, ct);
        return Ok(new MarketingLinkFunnelResponse(
            days,
            now,
            attribution.Value.UrlCaptureEnabled,
            telemetry.Value.VisitIngestEnabled,
            [.. result.Links.Select(r => new MarketingLinkFunnelItem(
                r.LinkId, r.Code, r.Channel, r.Medium, r.Campaign, r.Placement, r.DestinationPath, r.Archived,
                r.CostMinor, r.CostCurrency, r.Clicks, r.BotHits, r.Visitors,
                r.Accounts.Signups, r.Accounts.Verified, r.Accounts.Onboarded, r.Accounts.FirstProject,
                r.Accounts.FirstRun, r.Accounts.FirstVideo, r.Accounts.Paid))],
            new MarketingLinkFunnelUnattributed(
                result.Unattributed.Signups, result.Unattributed.Verified, result.Unattributed.Onboarded,
                result.Unattributed.FirstProject, result.Unattributed.FirstRun, result.Unattributed.FirstVideo,
                result.Unattributed.Paid)));
    }

    private ObjectResult Refusal(TrackedLinkRefusal r) => StatusCode(
        r.Code == "link_code_taken" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest,
        r.Field is null ? new { error = r.Code } : (object)new { error = r.Code, field = r.Field });

    private static MarketingLinkResponse View(MarketingLink link, long totalClicks) => new(
        link.Id,
        link.Code,
        link.Channel,
        link.Medium,
        link.Campaign,
        link.Placement,
        link.DestinationPath,
        link.Persona,
        link.CostMinor,
        link.CostCurrency,
        link.CreatedAt,
        link.ArchivedAt,
        TrackedLinkRules.ShortPath(link.Code),
        TrackedLinkRules.LongPath(link),
        totalClicks);
}
