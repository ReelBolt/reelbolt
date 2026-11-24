#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Pricing;

/// <summary>
/// The golden check of the upgrade's price change (decision 7): an install that ran the old seed is ported, and every
/// published catalogue price then charges exactly its catalogue credits (cost ÷ $0.006) times the 1.2 markup.
/// </summary>
public class CatalogPriceGoldenTests
{
    private const decimal Quantity = 1000m;

    [Fact]
    public async Task EveryPricedCatalogEntry_CostsCatalogCreditsTimesMarkup()
    {
        List<(ModelPrice Entry, string Code, string? Tier)> entries = [.. ModelPriceCatalog.Priced.Select(e =>
        {
            (string code, string? tier) = MeteringImageClient.SplitBillingKey(e.Model);
            return (e, code, tier);
        })];
        await using PortedCatalog catalog = await PortedCatalog.CreateAsync(
            entries.Select(e => (e.Entry.ProviderKind, e.Entry.Capability, e.Code)));

        List<string> wrong = [];
        int checkedUnits = 0;
        foreach ((ModelPrice entry, string code, string? tier) in entries)
        {
            foreach ((UsageKind unit, decimal usd) in entry.UsdPerUnit)
            {
                PriceQuote quote = await catalog.PriceAsync(entry.ProviderKind, entry.Capability, code, unit, Quantity, tier);
                decimal exact = usd * 1000m / 0.006m * 1.2m * Quantity;
                long expected = (long)Math.Round(exact, MidpointRounding.AwayFromZero);
                if (expected == 0 && exact > 0m) expected = 1;
                checkedUnits++;

                if (quote.CreditsMillis != expected || quote.CostUsd != usd * Quantity || quote.PriceId is null)
                {
                    wrong.Add($"{entry.ProviderKind}/{entry.Capability}/{entry.Model}/{unit}: expected {expected} millicredits " +
                              $"and ${usd * Quantity}, got {quote.CreditsMillis} and ${quote.CostUsd} (price {quote.PriceId})");
                }
            }
        }

        checkedUnits.Should().BeGreaterThan(100);
        wrong.Should().BeEmpty();
    }
}
