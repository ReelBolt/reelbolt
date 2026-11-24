#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;

namespace ReelBolt.WorkflowEngine.Tests.Pricing;

/// <summary>
/// TEST FIXTURE: an <see cref="IModelPriceService"/> that prices exactly what a set of pre-catalog rate cards priced,
/// through the REAL pricing rule (<see cref="ModelPriceService.Price"/>: multipliers, rounding, the 1-millicredit floor,
/// Byo and Runner at zero). Each request is answered from a one-capability snapshot whose price row is the card the old
/// rule (<c>exact &gt; longest prefix &gt; *</c>) picks for the request's kind and model; a compute request reads the
/// card whose pattern is its tier variant. The row carries the card's own credits as its override, so a figure pinned
/// against the legacy seed (<see cref="LegacyRateCardSeed"/>) is unchanged by the markup.
/// <para>
/// For tests written against the rate cards (the pre-run estimate, the cold-start hold) that need a whole seed's worth of
/// prices without building a catalog per provider. <see cref="PortedCatalog"/> is the real upgraded-install path.
/// </para>
/// </summary>
internal sealed class LegacyCardPrices(IReadOnlyList<RateCard> cards, IReadOnlyList<RateMultiplier> multipliers) : IModelPriceService
{
    public static readonly DateTime SeedEffective = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The whole legacy seed, compute tiers included, with its multipliers.</summary>
    public static LegacyCardPrices Seed() => new(
        [.. LegacyRateCardSeed.Cards(SeedEffective), .. LegacyRateCardSeed.ComputeCards(SeedEffective)],
        LegacyRateCardSeed.Multipliers(SeedEffective));

    /// <summary>Only <paramref name="cards"/>, with the seed's multipliers.</summary>
    public static LegacyCardPrices Of(IReadOnlyList<RateCard> cards) => new(cards, LegacyRateCardSeed.Multipliers(SeedEffective));

    public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(Price(request));

    /// <summary>The synchronous core of <see cref="PriceAsync"/>, for pure pricers.</summary>
    public PriceQuote Price(PriceRequest request)
    {
        DateTime at = request.At ?? DateTime.UtcNow;
        Guid capabilityId = request.ModelCapabilityId ?? Guid.NewGuid();
        Guid accountId = request.AccountId ?? Guid.NewGuid();
        string code = request.ModelCode ?? string.Empty;
        ModelCapabilityPrice[] rows;
        if (request.Capability == InferenceProviderCapability.Compute)
        {
            rows = [.. cards
                .Where(c => c.ProviderKind == LegacyRateCardSeed.ComputeProviderKind && c.UsageKind == request.Unit
                            && c.ModelPattern is ComputeMetering.Tier720 or ComputeMetering.Tier1080 or ComputeMetering.TierUhd)
                .Select(c => Row(capabilityId, c, c.ModelPattern))];
        }
        else
        {
            rows = FindCard(request.Unit, request.Kind, code, at) is { } card ? [Row(capabilityId, card, null)] : [];
        }

        ModelPriceSnapshot snapshot = new(
            [new PricedCapability(capabilityId, accountId, null, request.Kind, request.Capability, code)],
            rows,
            multipliers);
        return ModelPriceService.Price(snapshot, request with { ModelCapabilityId = capabilityId }, at);
    }

    public ValueTask<PriceQuote> PriceQuotedUsdAsync(
        decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default)
        => ValueTask.FromResult(ModelPriceService.PriceQuotedUsd(
            new ModelPriceSnapshot([], [], multipliers), costUsd, unit, billingMode, at ?? DateTime.UtcNow));

    public void Invalidate()
    {
    }

    private static ModelCapabilityPrice Row(Guid capabilityId, RateCard card, string? variant) => new()
    {
        Id = card.Id == Guid.Empty ? Guid.NewGuid() : card.Id,
        ModelCapabilityId = capabilityId,
        Unit = card.UsageKind,
        Variant = variant,
        CostUsdPerUnit = card.CostUsdPerUnit,
        CreditsMillisPerUnitOverride = card.CreditsMillisPerUnit,
        EffectiveFrom = card.EffectiveFrom,
        EffectiveTo = card.EffectiveTo
    };

    /// <summary>The old rate-card rule: exact model name, else the longest matching prefix pattern, else <c>*</c>; later start breaks a tie.</summary>
    private RateCard? FindCard(UsageKind kind, InferenceProviderKind providerKind, string modelName, DateTime at)
    {
        RateCard? best = null;
        int bestScore = -1;
        foreach (RateCard card in cards)
        {
            if (card.UsageKind != kind || card.ProviderKind != providerKind) continue;
            if (card.EffectiveFrom > at || (card.EffectiveTo is { } to && at >= to)) continue;

            int score = Specificity(card.ModelPattern, modelName);
            if (score < 0) continue;
            if (score > bestScore || (score == bestScore && card.EffectiveFrom > best!.EffectiveFrom))
            {
                best = card;
                bestScore = score;
            }
        }

        return best;
    }

    private static int Specificity(string pattern, string modelName)
    {
        if (pattern == "*") return 0;
        if (string.Equals(pattern, modelName, StringComparison.OrdinalIgnoreCase)) return int.MaxValue;
        if (pattern.EndsWith('*') && modelName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)) return pattern.Length - 1;
        return -1;
    }
}
