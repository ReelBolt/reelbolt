using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Pricing;

/// <summary>Drift guard: every usage kind a provider capability meters has a wildcard fallback in the legacy seed.</summary>
public sealed class LegacySeedWildcardCoverageTests
{
    [Fact]
    public void Every_required_usage_kind_has_a_wildcard_card_in_the_legacy_seed()
    {
        var cards = LegacyRateCardSeed.Cards(LegacyCardPrices.SeedEffective);
        foreach (var required in ProviderCapabilityUsage.RequiredCards())
        {
            cards.Should().Contain(
                c => c.ProviderKind == required.Kind && c.UsageKind == required.UsageKind && c.ModelPattern == "*",
                $"{required.Kind}/{required.UsageKind} needs a wildcard fallback; add it to LegacyRateCardSeed.FallbackUsdPerUnit");
        }
    }

    [Fact]
    public void Rerank_wildcard_is_a_real_zero_not_an_invented_price()
    {
        LegacyRateCardSeed.Cards(LegacyCardPrices.SeedEffective)
            .Where(c => c.UsageKind == ReelBolt.Shared.Data.Models.UsageKind.RerankSearches && c.ModelPattern == "*")
            .Should().NotBeEmpty().And.OnlyContain(c => c.CostUsdPerUnit == 0m);
    }
}
