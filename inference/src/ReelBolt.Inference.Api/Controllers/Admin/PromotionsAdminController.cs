using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Controllers.Admin;

/// <summary>
/// Platform-admin CRUD over promotions (<c>/api/v1/billing/admin/promotions</c>). Value fields are
/// immutable after create; only name, headline, status and end date can change, and a discount's
/// status and end date are synced to the provider before the row is saved. 404 with billing off.
/// </summary>
[ApiController]
[Route("api/v1/billing/admin/promotions")]
[Authorize]
public class PromotionsAdminController(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    IOptions<BillingOptions> options,
    IPromotionService promotions) : ControllerBase
{
    private const int MaxRedemptionRows = 500;

    private bool Disabled => !options.Value.Enabled;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (Disabled) return NotFound();

        List<Promotion> all = await db.Promotions.AsNoTracking().OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
        // Uses only: a NotGranted row (C10) is a refused grant, listed but never counted.
        Dictionary<Guid, int> counts = await db.PromotionRedemptions.AsNoTracking()
            .Where(r => r.Outcome == PromotionRedemptionOutcome.Granted)
            .GroupBy(r => r.PromotionId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return Ok(new { items = all.Select(p => PromotionDto.From(p, counts.GetValueOrDefault(p.Id))) });
    }

    /// <summary>Preset bodies for <c>POST</c> (so the admin form can prefill them), keyed by preset name.</summary>
    [HttpGet("presets")]
    public IActionResult Presets()
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (Disabled) return NotFound();

        return Ok(new { founderPrice = PromotionPresets.FounderPrice });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (Disabled) return NotFound();

        Promotion? p = await db.Promotions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return p is null ? NotFound() : Ok(await DtoAsync(p, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePromotionDto body, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (Disabled) return NotFound();

        PromotionOutcome<Promotion> outcome = await promotions.CreateAsync(body, currentUser.UserId, ct);
        if (outcome.Error is { } error) return error.ToResult();

        PromotionDto dto = PromotionDto.From(outcome.Value!, 0);
        return Created($"/api/v1/billing/admin/promotions/{dto.Id}", dto);
    }

    /// <summary>
    /// Edits <c>name</c>, <c>publicHeadline</c>, <c>status</c>, <c>endsAt</c> or <c>clearEndsAt</c>.
    /// Any other field is a 400: value fields are immutable after create.
    /// </summary>
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] JsonElement body, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (Disabled) return NotFound();

        if (!PromotionPatch.TryParse(body, out PromotionPatch patch, out List<string> errors))
        {
            return PromotionError.Validation(errors).ToResult();
        }

        PromotionOutcome<Promotion> outcome = await promotions.UpdateAsync(id, patch, ct);
        return outcome.Error is { } error ? error.ToResult() : Ok(await DtoAsync(outcome.Value!, ct));
    }

    [HttpGet("{id:guid}/redemptions")]
    public async Task<IActionResult> Redemptions(Guid id, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (Disabled) return NotFound();

        List<PromotionRedemption> rows = await db.PromotionRedemptions.AsNoTracking()
            .Where(r => r.PromotionId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Take(MaxRedemptionRows)
            .ToListAsync(ct);
        List<Guid> orgIds = rows.Select(r => r.OrganizationId).Distinct().ToList();
        Dictionary<Guid, string> names = await db.Organizations.AsNoTracking().IgnoreQueryFilters()
            .Where(o => orgIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Name, ct);

        return Ok(new
        {
            items = rows.Select(r => new
            {
                id = r.Id,
                kind = PromotionRules.Camel(r.Kind),
                organizationId = r.OrganizationId,
                organizationName = names.GetValueOrDefault(r.OrganizationId),
                userId = r.UserId,
                providerTransactionId = r.ProviderTransactionId,
                credits = r.CreditsMillis / 1000.0,
                discountTotal = r.DiscountTotal,
                currencyCode = r.CurrencyCode,
                // C8: a paid, reserved checkout honoured past a cap.
                overshoot = r.Overshoot,
                // C10: granted, or a refused grant/count support may need to make good, with its reason.
                outcome = PromotionRules.Camel(r.Outcome),
                notGrantedReason = r.NotGrantedReason is { } reason ? PromotionRules.Camel(reason) : null,
                // when a plan change outside the promotion's targets ended the subscription's discount.
                discountRemovedAt = PromotionRules.Utc(r.DiscountRemovedAt),
                createdAt = PromotionRules.Utc(r.CreatedAt)
            })
        });
    }

    private async Task<PromotionDto> DtoAsync(Promotion p, CancellationToken ct)
        => PromotionDto.From(p, await db.PromotionRedemptions.CountAsync(r => r.PromotionId == p.Id && r.Outcome == PromotionRedemptionOutcome.Granted, ct));
}
