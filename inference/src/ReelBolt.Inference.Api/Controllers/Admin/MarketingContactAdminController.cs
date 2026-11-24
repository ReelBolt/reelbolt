using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Controllers.Admin;

/// <summary>Brevo sync state of one contact in the admin list.</summary>
public sealed record MarketingContactBrevoDto(string State, DateTime? SyncedAt, string? ErrorCode);

/// <summary>One row of the admin contact list.</summary>
public sealed record MarketingContactDto(
    Guid Id, string Email, string Status, string? FirstName, string? LastName, string? NameSource, MarketingContactBrevoDto Brevo);

/// <summary>The contact list page.</summary>
public sealed record MarketingContactListResponse(IReadOnlyList<MarketingContactDto> Items, int Total);

/// <summary>PATCH body: both names optional; clearing both means "no name".</summary>
public sealed record MarketingNameRequest(string? FirstName, string? LastName);

/// <summary>
/// The platform-admin contact list with names and Brevo sync state, and the admin name edit. Under
/// <c>/api/v1/marketing/</c> like the consent admin controller. Logs only the admin id and the consent id.
/// </summary>
[ApiController]
[Route("api/v1/marketing/admin")]
[Authorize]
public class MarketingContactAdminController(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    ILogger<MarketingContactAdminController> logger,
    TimeProvider? clock = null) : ControllerBase
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    [HttpGet("contacts")]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] string? q, [FromQuery] int limit = 50, [FromQuery] int offset = 0, CancellationToken ct = default)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        IQueryable<MarketingConsent> query = db.MarketingConsents.AsNoTracking();
        switch ((status ?? "optedIn").Trim().ToLowerInvariant())
        {
            case "optedin": query = query.Where(c => c.Status == MarketingConsentStatus.OptedIn); break;
            case "pending": query = query.Where(c => c.Status == MarketingConsentStatus.Pending); break;
            case "withdrawn": query = query.Where(c => c.Status == MarketingConsentStatus.Withdrawn); break;
            case "all": break;
            default: return BadRequest(new { code = "invalid_status" });
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            if (q.Trim().Length > 200)
            {
                return BadRequest(new { code = "invalid_query" });
            }

            string needle = q.Trim().ToLowerInvariant();
            query = query.Where(c => c.Email.ToLower().Contains(needle)
                || (c.FirstName != null && c.FirstName.ToLower().Contains(needle))
                || (c.LastName != null && c.LastName.ToLower().Contains(needle)));
        }

        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);
        int total = await query.CountAsync(ct);
        List<MarketingConsent> page = await query.OrderBy(c => c.Email).Skip(offset).Take(limit).ToListAsync(ct);
        return Ok(new MarketingContactListResponse(page.Select(ToDto).ToList(), total));
    }

    [HttpPatch("consents/{id:guid}/name")]
    public async Task<IActionResult> SetName(Guid id, [FromBody] MarketingNameRequest request, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        string? first = MarketingName.Clean(request.FirstName);
        string? last = MarketingName.Clean(request.LastName);
        if ((!string.IsNullOrWhiteSpace(request.FirstName) && first is null)
            || (!string.IsNullOrWhiteSpace(request.LastName) && last is null))
        {
            return BadRequest(new { code = "invalid_name" });
        }

        MarketingConsent? row = await db.MarketingConsents.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null) return NotFound();

        // A withdrawn contact keeps no name.
        if (row.Status == MarketingConsentStatus.Withdrawn) return Conflict(new { code = "withdrawn" });

        row.FirstName = first;
        row.LastName = last;
        row.NameSource = MarketingNameSource.Admin;
        row.NameUpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Admin {AdminId} set the contact name of consent {ConsentId}", currentUser.UserId, id);
        return Ok(ToDto(row));
    }

    private static MarketingContactDto ToDto(MarketingConsent c)
    {
        bool sendable = c.Status == MarketingConsentStatus.OptedIn && c.ConfirmedAt != null && c.UndeliverableAt == null;
        string state;
        if (c.BrevoPushErrorCode != null)
        {
            state = "failed";
        }
        else if (c.BrevoPushedAt is { } pushed && pushed >= c.UpdatedAt && (c.NameUpdatedAt is not { } nameAt || pushed >= nameAt))
        {
            state = "synced";
        }
        else
        {
            state = sendable ? "pending" : "notAtBrevo";
        }

        return new MarketingContactDto(
            c.Id,
            c.Email,
            MarketingConsentService.StatusName(c.Status),
            c.FirstName,
            c.LastName,
            c.NameSource?.ToString(),
            new MarketingContactBrevoDto(state, c.BrevoPushedAt, c.BrevoPushErrorCode));
    }
}
