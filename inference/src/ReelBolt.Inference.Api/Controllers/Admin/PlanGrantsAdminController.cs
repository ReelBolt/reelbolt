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
/// Platform-admin manual plan grants under <c>/api/v1/billing/admin</c>: gifting a paid plan
/// without Paddle, and ending that gift. Every action is platform-admin only — an org Owner never
/// reaches it — and 404s with billing off, like the credit grants beside it.
/// </summary>
[ApiController]
[Route("api/v1/billing/admin")]
[Authorize]
public class PlanGrantsAdminController(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    IOptions<BillingOptions> options,
    IPlanGrantService grants,
    TimeProvider? time = null) : ControllerBase
{
    private bool Disabled => !options.Value.Enabled;

    private DateTime UtcNow => (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    /// <summary>The plans this panel may gift: active plans, the Trial plan excluded.</summary>
    [HttpGet("plans")]
    public async Task<IActionResult> Plans(CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (Disabled) return NotFound();

        IReadOnlyList<Plan> plans = await grants.ListGrantablePlansAsync(ct);
        return Ok(new
        {
            items = plans.Select(p => new
            {
                key = p.Key,
                name = p.Name,
                kind = p.Kind.ToString(),
                priceMonthlyUsd = p.PriceMonthlyUsd
            })
        });
    }

    /// <summary>Every manual grant made to the organization, newest first.</summary>
    [HttpGet("orgs/{orgId:guid}/plan-grants")]
    public async Task<IActionResult> List(Guid orgId, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (Disabled) return NotFound();
        if (!await IsListableOrgAsync(orgId, ct)) return NotFound();

        return Ok(new { items = await ItemsAsync(orgId, ct) });
    }

    /// <summary>
    /// Grants the plan for the stated period. A replayed <c>requestId</c> answers 200 with the
    /// original grant; a live provider-backed subscription answers 409 PLAN_GRANT_PROVIDER_ACTIVE.
    /// </summary>
    [HttpPost("orgs/{orgId:guid}/plan-grants")]
    public async Task<IActionResult> Grant(Guid orgId, [FromBody] GrantPlanDto body, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (Disabled) return NotFound();

        PromotionOutcome<PlanGrantOutcome> outcome = await grants.GrantAsync(orgId, body, currentUser.UserId, ct);
        if (outcome.Error is { } error) return error.ToResult();

        PlanGrantOutcome done = outcome.Value!;
        object dto = (await DtosAsync([done.Grant], ct)).Single();
        return done.Replayed ? Ok(dto) : StatusCode(StatusCodes.Status201Created, dto);
    }

    /// <summary>Ends a manual grant now; a provider-backed subscription answers 409 PLAN_GRANT_NOT_MANUAL.</summary>
    [HttpPost("orgs/{orgId:guid}/plan-grants/{grantId:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid orgId, Guid grantId, [FromBody] RevokePlanGrantDto body, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (Disabled) return NotFound();

        PromotionOutcome<AdminSubscriptionGrant> outcome = await grants.RevokeAsync(orgId, grantId, body, currentUser.UserId, ct);
        if (outcome.Error is { } error) return error.ToResult();

        return Ok((await DtosAsync([outcome.Value!], ct)).Single());
    }

    private Task<bool> IsListableOrgAsync(Guid orgId, CancellationToken ct)
        => db.Organizations.AsNoTracking()
            .AnyAsync(o => o.Id == orgId && o.Kind != OrganizationKind.Platform && o.Id != WellKnownOrganizations.PlatformId, ct);

    private async Task<List<object>> ItemsAsync(Guid orgId, CancellationToken ct)
        => await DtosAsync(await grants.ListAsync(orgId, ct), ct);

    private async Task<List<object>> DtosAsync(IReadOnlyList<AdminSubscriptionGrant> rows, CancellationToken ct)
    {
        List<Guid> planIds = rows.Select(r => r.PlanId).Distinct().ToList();
        Dictionary<Guid, string> names = await db.Plans.AsNoTracking()
            .Where(p => planIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        DateTime now = UtcNow;
        return rows
            .Select(r => PlanGrantState.Dto(r, names.GetValueOrDefault(r.PlanId, r.PlanKey), now))
            .ToList();
    }
}
