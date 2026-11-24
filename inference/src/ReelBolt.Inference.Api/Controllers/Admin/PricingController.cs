using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Controllers.Admin;

/// <summary>A billing-mode multiplier row. Enums travel by name (<c>"Platform"</c>, <c>"LlmInputTokens"</c>).</summary>
public sealed record RateMultiplierResponse(
    Guid Id,
    string BillingMode,
    string? UsageKind,
    decimal Multiplier,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo);

/// <summary>
/// Body of <c>POST /api/v1/pricing/multipliers</c>: <paramref name="BillingMode"/> is a <c>UsageBillingMode</c> name,
/// <paramref name="UsageKind"/> a <c>UsageKind</c> name or null for the mode's default.
/// </summary>
public sealed record CreateRateMultiplierRequest(
    string? BillingMode,
    string? UsageKind,
    decimal Multiplier,
    DateTime? EffectiveFrom);

/// <summary><c>GET|PUT /api/v1/pricing/settings</c>. Only <see cref="Markup"/> is editable; the credit value changes only by migration.</summary>
public sealed record PricingSettingsResponse(decimal Markup, decimal CreditUsdValue, DateTime UpdatedAt);

public sealed record UpdatePricingSettingsRequest(decimal? Markup);

/// <summary>One enabled platform capability that lacks an effective price for some of the units it meters.</summary>
public sealed record PriceCoverageGapResponse(
    Guid CapabilityId, Guid ProviderId, string ProviderName, string ModelDisplayName, string Capability, List<string> MissingUnits);

/// <summary>One price the price book proposes (the add-price form's pre-fill).</summary>
public sealed record PriceBookEntryResponse(string Unit, string? Variant, decimal CostUsdPerUnit, string? Notes);

/// <summary>
/// The platform's pricing administration (docs/design/model-catalog.md §6): the global markup, the billing-mode
/// multipliers (moved here from <c>/api/v1/rate-multipliers</c> with identical rules), the coverage check (enabled
/// platform capabilities with an unpriced unit) and the price book the add-price form is pre-filled from. Per-capability
/// price rows live under <c>/api/v1/inference-providers/{id}/models/{modelId}/capabilities/{capId}/prices</c>. Every
/// action is platform-admin only. Routed under <c>/api/v1</c>, not <c>/api/v1/admin/*</c>, because nginx sends the
/// latter to the Go API.
/// </summary>
[ApiController]
[Route("api/v1/pricing")]
[Authorize]
public class PricingController(InferenceApiDbContext db, ICurrentUser currentUser, IModelPriceService prices, TimeProvider? time = null)
    : ControllerBase
{
    /// <summary>The highest markup the settings accept: ten times cost is already far past any sane price.</summary>
    public const decimal MaxMarkup = 10m;

    private DateTime Now => (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ settings

    [HttpGet("settings")]
    public async Task<ActionResult<PricingSettingsResponse>> GetSettings(CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        PricingSettings? row = await db.PricingSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == PricingSettings.SingletonId, ct);
        return Ok(Map(row ?? new PricingSettings { UpdatedAt = DateTime.UnixEpoch }));
    }

    /// <summary>Sets the global markup (credits = cost × markup ÷ credit value). Must be above 0 and at most <see cref="MaxMarkup"/>.</summary>
    [HttpPut("settings")]
    public async Task<ActionResult<PricingSettingsResponse>> UpdateSettings(UpdatePricingSettingsRequest request, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (request.Markup is not { } markup || markup <= 0m || markup > MaxMarkup)
            return BadRequest(new { error = $"Markup must be greater than 0 and at most {MaxMarkup}." });

        PricingSettings? row = await db.PricingSettings.FirstOrDefaultAsync(s => s.Id == PricingSettings.SingletonId, ct);
        if (row is null)
        {
            row = new PricingSettings { Id = PricingSettings.SingletonId };
            db.PricingSettings.Add(row);
        }

        row.Markup = markup;
        row.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        prices.Invalidate();
        return Ok(Map(row));
    }

    private static PricingSettingsResponse Map(PricingSettings s) => new(s.Markup, s.CreditUsdValue, s.UpdatedAt);

    // ------------------------------------------------------------------ coverage

    /// <summary>Every enabled platform capability that has no effective price for a unit it meters (a Platform charge through it would be zero).</summary>
    [HttpGet("coverage")]
    public async Task<ActionResult<List<PriceCoverageGapResponse>>> Coverage(CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        IReadOnlyList<PriceCoverageGap> gaps = await PriceCoverage.FindAsync(db, Now, ct);
        List<Guid> ids = [.. gaps.Select(g => g.CapabilityId)];
        Dictionary<Guid, Guid> providerOf = await db.ModelCapabilities
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .AsNoTracking()
            .Where(c => c.OrganizationId == null && ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Model.ProviderId })
            .ToDictionaryAsync(c => c.Id, c => c.ProviderId, ct);

        return gaps
            .Where(g => providerOf.ContainsKey(g.CapabilityId))
            .Select(g => new PriceCoverageGapResponse(
                g.CapabilityId, providerOf[g.CapabilityId], g.ProviderName, g.ModelDisplayName, g.Capability.ToString(),
                [.. g.MissingUnits.Select(u => u.ToString())]))
            .ToList();
    }

    // ------------------------------------------------------------------ multipliers

    [HttpGet("multipliers")]
    public async Task<ActionResult<List<RateMultiplierResponse>>> ListMultipliers(CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        List<RateMultiplier> rows = await db.RateMultipliers.AsNoTracking().ToListAsync(ct);
        return rows
            .OrderBy(m => m.BillingMode.ToString()).ThenBy(m => m.UsageKind?.ToString() ?? string.Empty)
            .ThenByDescending(m => m.EffectiveFrom)
            .Select(Map)
            .ToList();
    }

    /// <summary>Adds a multiplier row; it supersedes the open row of the same billing mode and usage kind by date.</summary>
    [HttpPost("multipliers")]
    public async Task<ActionResult<RateMultiplierResponse>> CreateMultiplier(CreateRateMultiplierRequest request, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (!TryParseName(request.BillingMode, out UsageBillingMode billingMode))
            return BadRequest(new { message = "billingMode must be one of: " + string.Join(", ", Enum.GetNames<UsageBillingMode>()) + "." });
        UsageKind? usageKind = null;
        if (!string.IsNullOrWhiteSpace(request.UsageKind))
        {
            if (!TryParseName(request.UsageKind, out UsageKind kind))
                return BadRequest(new { message = "usageKind must be empty or one of: " + string.Join(", ", Enum.GetNames<UsageKind>()) + "." });
            usageKind = kind;
        }

        if (request.Multiplier < 0) return BadRequest(new { message = "A multiplier cannot be negative." });

        DateTime from = NormalizeUtc(request.EffectiveFrom ?? Now);

        if (await db.RateMultipliers.AnyAsync(m =>
                m.BillingMode == billingMode && m.UsageKind == usageKind && m.EffectiveFrom == from, ct))
            return Conflict(new { message = "A multiplier already starts at that instant for this billing mode and usage kind." });

        List<RateMultiplier> open = await db.RateMultipliers
            .Where(m => m.BillingMode == billingMode && m.UsageKind == usageKind
                && m.EffectiveFrom < from && m.EffectiveTo == null)
            .ToListAsync(ct);
        foreach (RateMultiplier previous in open) previous.EffectiveTo = from;

        RateMultiplier row = new()
        {
            Id = Guid.NewGuid(),
            BillingMode = billingMode,
            UsageKind = usageKind,
            Multiplier = request.Multiplier,
            EffectiveFrom = from
        };
        db.RateMultipliers.Add(row);
        await db.SaveChangesAsync(ct);
        prices.Invalidate();
        return CreatedAtAction(nameof(ListMultipliers), Map(row));
    }

    private static RateMultiplierResponse Map(RateMultiplier m) =>
        new(m.Id, m.BillingMode.ToString(), m.UsageKind?.ToString(), m.Multiplier, m.EffectiveFrom, m.EffectiveTo);

    private static bool TryParseName<TEnum>(string? value, out TEnum parsed) where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _);

    // ------------------------------------------------------------------ price book

    /// <summary>The prices the book proposes for one capability of one model (the add-price form's pre-fill). Read only.</summary>
    [HttpGet("price-book")]
    public ActionResult<List<PriceBookEntryResponse>> PriceBookFor(
        [FromQuery] string? kind, [FromQuery] string? capability, [FromQuery] string? model)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        if (!InferenceProvidersController.TryParseKind(kind, out InferenceProviderKind parsedKind) || !Enum.IsDefined(parsedKind))
            return BadRequest(new { error = $"kind must be one of: {InferenceProvidersController.KnownKinds}." });
        if (!InferenceProvidersController.TryParseCapability(capability, out InferenceProviderCapability parsedCapability)
            || !Enum.IsDefined(parsedCapability))
            return BadRequest(new { error = "capability must be one of: " + string.Join(", ", Enum.GetNames<InferenceProviderCapability>()) });

        return PriceBook.SuggestFor(parsedKind, parsedCapability, model)
            .Select(s => new PriceBookEntryResponse(s.Unit.ToString(), s.Variant, s.CostUsdPerUnit, s.Notes))
            .ToList();
    }

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
}
