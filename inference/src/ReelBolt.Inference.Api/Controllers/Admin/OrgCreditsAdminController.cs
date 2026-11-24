using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers.Admin;

/// <summary>
/// Platform-admin organization lookup and manual Promo credit grants, under
/// <c>/api/v1/billing/admin</c>. The Platform org is never listed nor granted to. 404 with billing off.
/// </summary>
[ApiController]
[Route("api/v1/billing/admin")]
[Authorize]
public class OrgCreditsAdminController(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    IOptions<BillingOptions> options,
    IPromotionService promotions) : ControllerBase
{
    private const int MaxOrgs = 50;

    private bool Disabled => !options.Value.Enabled;

    /// <summary>Organizations whose name or owner email contains <paramref name="search"/> (case-insensitive), at most 50.</summary>
    [HttpGet("orgs")]
    public async Task<IActionResult> SearchOrgs([FromQuery] string? search, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (Disabled) return NotFound();

        IQueryable<Organization> orgs = Listable();
        string term = search?.Trim().ToLowerInvariant() ?? string.Empty;
        if (term.Length > 0)
        {
            orgs = orgs.Where(o => o.Name.ToLower().Contains(term)
                || db.OrganizationMemberships.Any(m => m.OrganizationId == o.Id && m.Role == OrganizationRole.Owner
                    && db.ApplicationUsers.Any(u => u.Id == m.UserId && !u.IsServiceAccount && u.Email.ToLower().Contains(term))));
        }

        List<Organization> page = await orgs.OrderBy(o => o.Name).Take(MaxOrgs).ToListAsync(ct);
        return Ok(new { items = await ItemsAsync(page, ct) });
    }

    /// <summary>The organizations a user is a member of, in the same shape as the search.</summary>
    [HttpGet("users/{userId:guid}/orgs")]
    public async Task<IActionResult> UserOrgs(Guid userId, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (Disabled) return NotFound();

        List<Organization> page = await Listable()
            .Where(o => db.OrganizationMemberships.Any(m => m.OrganizationId == o.Id && m.UserId == userId
                && !db.ApplicationUsers.Any(u => u.Id == userId && u.IsServiceAccount)))
            .OrderBy(o => o.Name)
            .Take(MaxOrgs)
            .ToListAsync(ct);
        return Ok(new { items = await ItemsAsync(page, ct) });
    }

    [HttpGet("orgs/{orgId:guid}/credit-grants")]
    public async Task<IActionResult> ListGrants(Guid orgId, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (Disabled) return NotFound();

        List<AdminCreditGrant> grants = await db.AdminCreditGrants.AsNoTracking()
            .Where(g => g.OrganizationId == orgId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(ct);
        return Ok(new { items = grants.Select(GrantDto) });
    }

    /// <summary>Grants Promo credits with an expiry. A replayed <c>requestId</c> answers 200 with the original row.</summary>
    [HttpPost("orgs/{orgId:guid}/credit-grants")]
    public async Task<IActionResult> Grant(Guid orgId, [FromBody] GrantCreditsDto body, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (Disabled) return NotFound();

        PromotionOutcome<AdminGrantOutcome> outcome = await promotions.GrantCreditsAsync(orgId, body, currentUser.UserId, ct);
        if (outcome.Error is { } error) return error.ToResult();

        AdminGrantOutcome done = outcome.Value!;
        return done.Replayed ? Ok(GrantDto(done.Grant)) : StatusCode(StatusCodes.Status201Created, GrantDto(done.Grant));
    }

    private IQueryable<Organization> Listable()
        => db.Organizations.AsNoTracking()
            .Where(o => o.Kind != OrganizationKind.Platform && o.Id != WellKnownOrganizations.PlatformId);

    private async Task<List<object>> ItemsAsync(List<Organization> page, CancellationToken ct)
    {
        List<Guid> ids = page.Select(o => o.Id).ToList();
        var owners = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => ids.Contains(m.OrganizationId) && m.Role == OrganizationRole.Owner && !m.User.IsServiceAccount)
            .Join(db.ApplicationUsers.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { m.OrganizationId, u.Email, m.CreatedAt, u.IsServiceAccount })
            .ToListAsync(ct);
        var subscriptions = await db.Subscriptions.AsNoTracking()
            .Where(s => ids.Contains(s.OrganizationId) && s.Status != SubscriptionStatus.Canceled)
            .Join(db.Plans.AsNoTracking(), s => s.PlanId, p => p.Id, (s, p) => new { s.OrganizationId, p.Key, s.CreatedAt })
            .ToListAsync(ct);
        Dictionary<Guid, long> balances = await db.CreditAccounts.AsNoTracking()
            .Where(a => ids.Contains(a.OrganizationId))
            .ToDictionaryAsync(a => a.OrganizationId, a => a.BalanceMillis, ct);

        return page.Select(o => (object)new
        {
            id = o.Id,
            name = o.Name,
            kind = o.Kind.ToString(),
            ownerEmail = owners.Where(x => x.OrganizationId == o.Id).OrderBy(x => x.CreatedAt).Select(x => x.Email).FirstOrDefault(),
            planKey = subscriptions.Where(x => x.OrganizationId == o.Id).OrderByDescending(x => x.CreatedAt).Select(x => x.Key).FirstOrDefault(),
            balance = balances.GetValueOrDefault(o.Id) / 1000.0
        }).ToList();
    }

    private static object GrantDto(AdminCreditGrant g) => new
    {
        id = g.Id,
        credits = g.CreditsMillis / 1000.0,
        expiresAt = PromotionRules.Utc(g.ExpiresAt),
        reason = g.Reason,
        grantedByUserId = g.GrantedByUserId,
        createdAt = PromotionRules.Utc(g.CreatedAt)
    };
}
