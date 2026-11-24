#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.WorkflowEngine.Tests.Metering;

/// <summary>
/// The real <see cref="ModelPriceService"/> rule over the committed <c>model-catalog.seed.json</c> rows of ONE
/// capability, filed under a caller-chosen capability id, so a test prices exactly what an install seeded.
/// </summary>
internal static class SeedPrices
{
    public static readonly DateTime At = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    public static ModelPriceSnapshot Snapshot(
        Guid capabilityId, InferenceProviderKind kind, string model, InferenceProviderCapability capability, UsageKind unit)
    {
        List<ModelCapabilityPrice> prices = [.. Rows(kind, model, capability, unit).Select(row => new ModelCapabilityPrice
        {
            Id = Guid.NewGuid(),
            ModelCapabilityId = capabilityId,
            Unit = row.Unit,
            Variant = row.Variant,
            CostUsdPerUnit = row.CostUsdPerUnit,
            EffectiveFrom = row.EffectiveFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            EffectiveTo = row.EffectiveTo?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
        })];
        if (prices.Count == 0) throw new InvalidOperationException($"The seed prices nothing for {kind}/{model}/{capability}/{unit}.");
        return new ModelPriceSnapshot(
            [new PricedCapability(capabilityId, capabilityId, null, kind, capability, model)], prices, []);
    }

    /// <summary>Prices <paramref name="request"/> at <see cref="At"/>.</summary>
    public static PriceQuote Price(ModelPriceSnapshot snapshot, PriceRequest request) =>
        ModelPriceService.Price(snapshot, request with { At = At }, At);

    public static IEnumerable<SeedPriceRow> Rows(
        InferenceProviderKind kind, string model, InferenceProviderCapability capability, UsageKind unit) =>
        ModelCatalogSeed.LoadDefault().Rows.Where(r => r.Kind == kind && r.ModelCode == model
            && r.Capability == capability.ToString() && r.Unit == unit && r.EffectiveTo is null);

    public static decimal Cost(InferenceProviderKind kind, string model, InferenceProviderCapability capability, UsageKind unit, string? variant) =>
        Rows(kind, model, capability, unit).Single(r => r.Variant == variant).CostUsdPerUnit;
}
