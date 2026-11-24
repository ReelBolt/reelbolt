using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Services.Inference;

/// <summary>
/// The platform's price rows (docs/design/model-catalog.md §2, §6): platform-admin only, reached only through an
/// authorized parent capability. There is no org scope here on purpose — BYO is never priced, and the org controller
/// has no price routes.
/// </summary>
public sealed partial class InferenceProviderAdminService
{
    /// <summary>The longest variant a price row stores (<c>model_capability_prices.variant varchar(64)</c>).</summary>
    public const int MaxVariantLength = 64;

    /// <summary>The price rows of some capabilities plus the pricing settings their effective credits are computed with.</summary>
    private sealed class PriceContext(ILookup<Guid, ModelCapabilityPrice> rows, decimal markup, decimal creditUsdValue)
    {
        public IEnumerable<PriceResponse> For(Guid capabilityId) => rows[capabilityId]
            .OrderBy(p => p.Unit)
            .ThenBy(p => p.Variant ?? string.Empty, StringComparer.Ordinal)
            .ThenByDescending(p => p.EffectiveFrom)
            .Select(p => MapPrice(p, markup, creditUsdValue));
    }

    private async Task<PriceContext> LoadPricesAsync(IEnumerable<Guid> capabilityIds, CancellationToken ct)
    {
        List<Guid> ids = [.. capabilityIds];
        List<ModelCapabilityPrice> rows = ids.Count == 0
            ? []
            : await _db.ModelCapabilityPrices.AsNoTracking().Where(p => ids.Contains(p.ModelCapabilityId)).ToListAsync(ct);
        (decimal markup, decimal creditUsdValue) = await LoadPricingSettingsAsync(ct);
        return new PriceContext(rows.ToLookup(p => p.ModelCapabilityId), markup, creditUsdValue);
    }

    /// <summary>The live markup and credit value (the defaults until the settings row exists).</summary>
    private async Task<(decimal Markup, decimal CreditUsdValue)> LoadPricingSettingsAsync(CancellationToken ct)
    {
        PricingSettings? settings = await _db.PricingSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == PricingSettings.SingletonId, ct);
        return (settings?.Markup ?? PricingDefaults.DefaultMarkup, settings?.CreditUsdValue ?? PricingDefaults.CreditUsdValue);
    }

    /// <summary>
    /// One price row with what it charges: <c>effectiveCreditsMillisPerUnit = override ?? cost × markup × 1000 ÷
    /// creditUsdValue</c> and <c>effectiveMarginPercent = (credits × creditUsdValue ÷ 1000 − cost) ÷ cost × 100</c>
    /// (null when the cost is zero: there is no margin on nothing).
    /// </summary>
    internal static PriceResponse MapPrice(ModelCapabilityPrice p, decimal markup, decimal creditUsdValue)
    {
        decimal credits = PricingDefaults.CreditsMillisPerUnit(p.CostUsdPerUnit, p.CreditsMillisPerUnitOverride, markup, creditUsdValue);
        decimal? margin = p.CostUsdPerUnit == 0m
            ? null
            : (credits * creditUsdValue / 1000m - p.CostUsdPerUnit) / p.CostUsdPerUnit * 100m;
        return new PriceResponse(
            p.Id, p.Unit.ToString(), p.Variant, p.CostUsdPerUnit, p.CreditsMillisPerUnitOverride, credits, margin,
            p.EffectiveFrom, p.EffectiveTo, p.Notes);
    }

    /// <summary>
    /// Adds a price row to a PLATFORM capability (the system compute capability included). The unit must be one the
    /// capability meters; a new row for the same (unit, variant) closes the open predecessor at its start, exactly like
    /// rate cards did; two rows may not start at the same instant (409).
    /// </summary>
    public async Task<AdminOutcome<PriceResponse>> CreatePriceAsync(
        Guid id, Guid modelId, Guid capId, CreatePriceRequest request, CancellationToken ct)
    {
        ModelCapability? capability = await FindCapabilityAsync(ProviderScope.Platform, id, modelId, capId, ct);
        if (capability is null) return AdminOutcome<PriceResponse>.NotFound();

        UsageKind[] units = ProviderCapabilityUsage.ByCapability.TryGetValue(capability.Capability, out UsageKind[]? u) ? u : [];
        if (string.IsNullOrWhiteSpace(request.Unit) || int.TryParse(request.Unit, out _)
            || !Enum.TryParse(request.Unit, ignoreCase: true, out UsageKind unit) || !Enum.IsDefined(unit)
            || !units.Contains(unit))
            return AdminOutcome<PriceResponse>.BadRequest(
                $"Unit must be one of the units {capability.Capability} is metered in: {string.Join(", ", units)}.");

        if (request.CostUsdPerUnit is not { } cost)
            return AdminOutcome<PriceResponse>.BadRequest("CostUsdPerUnit is required.");
        if (cost < 0m)
            return AdminOutcome<PriceResponse>.BadRequest("CostUsdPerUnit cannot be negative.");
        if (request.CreditsMillisPerUnitOverride is < 0m)
            return AdminOutcome<PriceResponse>.BadRequest("CreditsMillisPerUnitOverride cannot be negative; leave it empty to derive credits from the cost.");

        string? variant = string.IsNullOrWhiteSpace(request.Variant) ? null : request.Variant.Trim();
        if (variant is { Length: > MaxVariantLength })
            return AdminOutcome<PriceResponse>.BadRequest($"Variant must be at most {MaxVariantLength} characters.");

        string? notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        if (notes is not null && notes.StartsWith(SeedNotesPrefix, StringComparison.OrdinalIgnoreCase))
            return AdminOutcome<PriceResponse>.BadRequest("Notes cannot start with 'seed:'; that prefix marks rows the catalog seed writes.");

        DateTime from = NormalizeUtc(request.EffectiveFrom ?? Now);

        List<ModelCapabilityPrice> sameSeries = await _db.ModelCapabilityPrices
            .Where(p => p.ModelCapabilityId == capability.Id && p.Unit == unit)
            .ToListAsync(ct);
        sameSeries = [.. sameSeries.Where(p => string.Equals(p.Variant, variant, StringComparison.Ordinal))];
        if (sameSeries.Any(p => p.EffectiveFrom == from))
            return AdminOutcome<PriceResponse>.Conflict("A price for that unit and variant already starts at that instant.");

        foreach (ModelCapabilityPrice previous in sameSeries.Where(p => p.EffectiveFrom < from && p.EffectiveTo == null))
        {
            previous.EffectiveTo = from;
        }

        ModelCapabilityPrice row = new()
        {
            Id = Guid.NewGuid(),
            ModelCapabilityId = capability.Id,
            Unit = unit,
            Variant = variant,
            CostUsdPerUnit = cost,
            CreditsMillisPerUnitOverride = request.CreditsMillisPerUnitOverride,
            EffectiveFrom = from,
            Notes = notes,
            CreatedAt = Now
        };
        _db.ModelCapabilityPrices.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            return AdminOutcome<PriceResponse>.Conflict("A price for that unit and variant already starts at that instant.");
        }

        _prices?.Invalidate();
        (decimal markup, decimal creditUsdValue) = await LoadPricingSettingsAsync(ct);
        return AdminOutcome<PriceResponse>.Ok(MapPrice(row, markup, creditUsdValue));
    }

    /// <summary>Ends a price row at <paramref name="at"/> (now when omitted). An already-ended row is a 409.</summary>
    public async Task<AdminOutcome<PriceResponse>> RetirePriceAsync(
        Guid id, Guid modelId, Guid capId, Guid priceId, DateTime? at, CancellationToken ct)
    {
        ModelCapability? capability = await FindCapabilityAsync(ProviderScope.Platform, id, modelId, capId, ct);
        if (capability is null) return AdminOutcome<PriceResponse>.NotFound();

        ModelCapabilityPrice? row = await _db.ModelCapabilityPrices
            .FirstOrDefaultAsync(p => p.Id == priceId && p.ModelCapabilityId == capability.Id, ct);
        if (row is null) return AdminOutcome<PriceResponse>.NotFound();

        if (row.EffectiveTo is not null)
            return AdminOutcome<PriceResponse>.Conflict("This price has already ended.");

        DateTime end = NormalizeUtc(at ?? Now);
        if (end < row.EffectiveFrom)
            return AdminOutcome<PriceResponse>.BadRequest("A price cannot end before it starts.");

        row.EffectiveTo = end;
        await _db.SaveChangesAsync(ct);
        _prices?.Invalidate();
        (decimal markup, decimal creditUsdValue) = await LoadPricingSettingsAsync(ct);
        return AdminOutcome<PriceResponse>.Ok(MapPrice(row, markup, creditUsdValue));
    }

    /// <summary>
    /// The notes prefix that marks a row the catalog seed wrote (the seed owns those rows, so an admin may not forge one);
    /// matched case-insensitively after trimming (T-110), so "SEED:" or " Seed:" is refused too.
    /// </summary>
    private const string SeedNotesPrefix = "seed:";

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
}
