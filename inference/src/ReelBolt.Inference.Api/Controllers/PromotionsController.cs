using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Customer-facing promotions: redeeming a free-credit code, and the public list of automatic
/// promotions running now. With <c>Billing:Enabled=false</c> both answer 404, like the rest of
/// <c>/api/v1/billing</c>.
/// </summary>
[ApiController]
[Route("api/v1/billing/promotions")]
public class PromotionsController(
    ICurrentUser currentUser,
    IOptions<BillingOptions> options,
    IPromotionService promotions,
    IPromotionPlanPriceService planPrices) : ControllerBase
{
    private bool Disabled => !options.Value.Enabled;

    /// <summary>Redeems a CreditCode for the active organization. Owner or Billing role.</summary>
    [HttpPost("redeem")]
    [Authorize]
    public async Task<IActionResult> Redeem([FromBody] RedeemPromotionDto body, CancellationToken ct)
    {
        if (Disabled || currentUser.OrganizationId is not { } orgId || currentUser.OrgRole is null) return NotFound();
        if (currentUser.OrgRole is not (OrganizationRole.Owner or OrganizationRole.Billing)) return Forbid();

        PromotionOutcome<RedeemResult> outcome = await promotions.RedeemAsync(orgId, currentUser.UserId, body.Code, ct);
        return outcome.Error is { } error ? error.ToResult() : Ok(outcome.Value);
    }

    /// <summary>
    /// Active, automatic, in-window promotions, for the billing page and the marketing site. Anonymous,
    /// and never carries a code or an id.
    /// </summary>
    [HttpGet("active")]
    [AllowAnonymous]
    public async Task<IActionResult> Active(CancellationToken ct)
    {
        if (Disabled) return NotFound();

        IReadOnlyList<Promotion> active = await promotions.ActiveAutomaticAsync(ct);
        return Ok(new
        {
            items = active.Select(p =>
            {
                IReadOnlyList<string> planKeys = PromotionRules.ReadStrings(p.TargetPlanKeysJson);
                IReadOnlyList<int> packCredits = PromotionRules.ReadInts(p.TargetPackCreditsJson);
                bool percentage = p.DiscountType == PromotionDiscountType.Percentage;
                bool flat = p.DiscountType == PromotionDiscountType.Flat;
                return new
                {
                    kind = PromotionRules.Camel(p.Kind),
                    headline = p.PublicHeadline,
                    endsAt = PromotionRules.Utc(p.EndsAt),
                    discountType = p.DiscountType is { } t ? PromotionRules.Camel(t) : null,
                    percentOff = percentage && decimal.TryParse(p.DiscountAmount, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out decimal pct) ? (double?)pct : null,
                    amountOffMinor = flat && long.TryParse(p.DiscountAmount, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long minor) ? (long?)minor : null,
                    currencyCode = flat ? p.CurrencyCode : null,
                    bonusPercent = p.BonusPercent,
                    planKeys = p.Kind == PromotionKind.PlanDiscount ? planKeys : null,
                    packCredits = p.Kind == PromotionKind.PlanDiscount ? null : packCredits,
                    durationKind = p.DurationKind is { } d ? PromotionRules.Camel(d) : null,
                    durationMonths = p.DurationMonths
                };
            })
        });
    }

    /// <summary>
    /// What each plan on sale costs with its automatic discount, as the billing provider's own formatted
    /// strings. Anonymous and cached for 60 s; a plan whose price cannot be shown exactly is left out.
    /// Never carries a code or an id.
    /// </summary>
    [HttpGet("plan-prices")]
    [AllowAnonymous]
    public async Task<IActionResult> PlanPrices(CancellationToken ct)
    {
        if (Disabled) return NotFound();

        IReadOnlyList<PlanPriceItem> items = await planPrices.GetAsync(ct);
        Response.Headers.CacheControl = "public, max-age=60";
        return Ok(new
        {
            items = items.Select(i =>
            {
                Dictionary<string, object?> item = new()
                {
                    ["planKey"] = i.PlanKey,
                    ["perSeat"] = i.PerSeat,
                    ["currencyCode"] = i.CurrencyCode,
                    ["original"] = i.Original,
                    ["discounted"] = i.Discounted,
                    ["headline"] = i.Headline,
                    ["durationKind"] = i.DurationKind,
                    ["durationMonths"] = i.DurationMonths,
                    ["endsAt"] = i.EndsAt
                };
                // Additive raw amounts, only from a provider that returns integer totals (not Paddle).
                if (i.OriginalMinor is { } originalMinor) item["originalMinor"] = originalMinor;
                if (i.DiscountedMinor is { } discountedMinor) item["discountedMinor"] = discountedMinor;
                if (i.MinorUnitExponent is { } exponent) item["minorUnitExponent"] = exponent;
                return item;
            })
        });
    }

    /// <summary>
    /// Whether the first-video grant is on, so the sign-up page only promises it when it is real. Anonymous,
    /// cached for 30 s, and carries no figure, code or id.
    /// </summary>
    [HttpGet("first-video")]
    [AllowAnonymous]
    public IActionResult FirstVideo([FromServices] IConfiguration configuration)
    {
        if (Disabled) return NotFound();

        Response.Headers.CacheControl = "public, max-age=30";
        return Ok(new { enabled = configuration.GetValue(CreditAccountProvisioner.FirstVideoEnabledKey, true) });
    }
}
