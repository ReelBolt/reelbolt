#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>Annual billing building blocks: the catalog price, the derived interval, interval-aware price ids and direction.</summary>
public class AnnualIntervalTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Plan Catalog(string key) =>
        PlanCatalogSeeder.Load(File.ReadAllText(PlanCatalogSeeder.DefaultPath)).Single(p => p.Key == key);

    private static Plan WithIds(string json, decimal monthly = 29, PlanKind kind = PlanKind.Subscription, string key = "creator")
        => new() { Id = Guid.NewGuid(), Key = key, Name = key, Kind = kind, PriceMonthlyUsd = monthly, MinSeats = kind == PlanKind.PerSeat ? 3 : null, ProviderPriceIdsJson = json };

    [Theory]
    [InlineData("creator")]
    [InlineData("pro")]
    [InlineData("team")]
    public void Catalog_AnnualPrice_IsTenTimesMonthly(string key)
    {
        Plan plan = Catalog(key);
        PlanPricing.AnnualPriceUsd(plan).Should().Be(plan.PriceMonthlyUsd!.Value * 10);
    }

    [Theory]
    [InlineData("trial")]
    [InlineData("enterprise")]
    public void Catalog_TrialAndEnterprise_HaveNoAnnualPrice(string key)
        => PlanPricing.AnnualPriceUsd(Catalog(key)).Should().BeNull();

    [Fact]
    public void Catalog_AnnualPrice_IsOnlyOnSubscriptionAndPerSeatPlans()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(PlanCatalogSeeder.DefaultPath));
        foreach (JsonElement p in doc.RootElement.GetProperty("plans").EnumerateArray())
        {
            bool paid = p.GetProperty("kind").GetString() is "subscription" or "perSeat";
            p.TryGetProperty("priceAnnual", out JsonElement annual).Should().Be(paid, p.GetProperty("id").GetString());
            if (paid) annual.GetDecimal().Should().Be(p.GetProperty("priceMonthly").GetDecimal() * 10);
        }
    }

    [Fact]
    public void Catalog_VendoredSiteCopy_IsByteIdentical()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "site", "lib", "pricing", "plans.catalog.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null) return; // not running inside the repo checkout
        File.ReadAllBytes(Path.Combine(dir, "site", "lib", "pricing", "plans.catalog.json"))
            .Should().Equal(File.ReadAllBytes(PlanCatalogSeeder.DefaultPath));
    }

    [Theory]
    [InlineData(31, BillingInterval.Monthly)]
    [InlineData(40, BillingInterval.Monthly)]
    [InlineData(41, BillingInterval.Annual)]
    [InlineData(365, BillingInterval.Annual)]
    public void Interval_IsAnnual_WhenThePeriodSpansMoreThanFortyDays(int days, BillingInterval expected)
        => SubscriptionInterval.Of(T0, T0.AddDays(days)).Should().Be(expected);

    [Fact]
    public void Interval_WithAnUnknownBound_IsMonthly()
    {
        SubscriptionInterval.Of(null, T0).Should().Be(BillingInterval.Monthly);
        SubscriptionInterval.Of(T0, null).Should().Be(BillingInterval.Monthly);
    }

    [Theory]
    [InlineData(null, true, BillingInterval.Monthly)]
    [InlineData("", true, BillingInterval.Monthly)]
    [InlineData("monthly", true, BillingInterval.Monthly)]
    [InlineData("ANNUAL", true, BillingInterval.Annual)]
    [InlineData("yearly", false, BillingInterval.Monthly)]
    public void Interval_Parse(string? text, bool ok, BillingInterval expected)
    {
        SubscriptionInterval.TryParse(text, out BillingInterval parsed).Should().Be(ok);
        parsed.Should().Be(expected);
    }

    [Fact]
    public void PriceId_IsLookedUpPerInterval()
    {
        Plan both = WithIds("""{"stripe":{"monthly":"price_m","annual":"price_y"},"paddle":{"monthly":"pri_m"}}""");

        BillingEventProcessor.PriceIdFor(both, "stripe", BillingInterval.Monthly).Should().Be("price_m");
        BillingEventProcessor.PriceIdFor(both, "stripe", BillingInterval.Annual).Should().Be("price_y");
        BillingEventProcessor.PriceIdFor(both, "paddle", BillingInterval.Monthly).Should().Be("pri_m");
        BillingEventProcessor.PriceIdFor(both, "paddle", BillingInterval.Annual).Should().BeNull();
        BillingEventProcessor.PriceIdFor(both, "other", BillingInterval.Monthly).Should().BeNull();
    }

    [Fact]
    public void MonthlyPriceId_NeverFallsBackToTheAnnualId_AndKeepsTheLegacyFirstIdFallback()
    {
        // The annual key listed first must not become "the" price id.
        BillingEventProcessor.PriceIdFor(WithIds("""{"stripe":{"annual":"price_y","monthly":"price_m"}}"""), "stripe", BillingInterval.Monthly).Should().Be("price_m");
        // A map with no "monthly" key keeps resolving to its first id as before.
        BillingEventProcessor.PriceIdFor(WithIds("""{"paddle":{"default":"pri_x"}}"""), "paddle", BillingInterval.Monthly).Should().Be("pri_x");
        BillingEventProcessor.PriceIdFor(WithIds("not json"), "paddle", BillingInterval.Monthly).Should().BeNull();
    }

    [Fact]
    public void ResolveItems_ResolvesPlanSeatsAndInterval_OfAnAnnualPriceId()
    {
        Plan creator = WithIds("""{"stripe":{"monthly":"price_m","annual":"price_y"}}""");
        Plan team = WithIds("""{"stripe":{"monthly":"price_tm","annual":"price_ty"}}""", 49, PlanKind.PerSeat, "team");
        IReadOnlyList<Plan> plans = [creator, team];

        (Plan? p1, int s1, BillingInterval? i1) = BillingEventProcessor.ResolveItemsWithInterval([new BillingLineItem("price_y", 1)], plans, "stripe");
        p1.Should().BeSameAs(creator);
        s1.Should().Be(1);
        i1.Should().Be(BillingInterval.Annual);

        (Plan? p2, int s2, BillingInterval? i2) = BillingEventProcessor.ResolveItemsWithInterval([new BillingLineItem("price_ty", 5)], plans, "stripe");
        p2.Should().BeSameAs(team);
        s2.Should().Be(5);
        i2.Should().Be(BillingInterval.Annual);

        BillingEventProcessor.ResolveItemsWithInterval([new BillingLineItem("price_tm", 1)], plans, "stripe").Interval.Should().Be(BillingInterval.Monthly);
        BillingEventProcessor.ResolveItemsWithInterval([new BillingLineItem("nope", 1)], plans, "stripe").Interval.Should().BeNull();

        // The two-value form (webhook, drift) resolves an annual id exactly like a monthly one.
        BillingEventProcessor.ResolveItems([new BillingLineItem("price_y", 1)], plans, "stripe").Plan.Should().BeSameAs(creator);
    }

    [Fact]
    public void Direction_SameInterval_ComparesPerMonthEquivalents_AndMonthlyIsUnchanged()
    {
        Plan creator = Catalog("creator");
        Plan pro = Catalog("pro");

        BillingEventProcessor.IsDowngrade(pro, 1, creator, 1).Should().BeTrue();
        BillingEventProcessor.IsDowngrade(creator, 1, pro, 1).Should().BeFalse();
        BillingEventProcessor.IsDowngrade(pro, 1, creator, 1, BillingInterval.Annual, BillingInterval.Annual).Should().BeTrue();
        BillingEventProcessor.IsDowngrade(creator, 1, pro, 1, BillingInterval.Annual, BillingInterval.Annual).Should().BeFalse();
        BillingEventProcessor.MonthlyEquivalentUsd(creator, 1, BillingInterval.Annual).Should().BeApproximately(290m / 12m, 0.0001m);
        BillingEventProcessor.MonthlyEquivalentUsd(Catalog("team"), 4, BillingInterval.Annual).Should().BeApproximately(490m / 12m * 4, 0.0001m);
    }

    [Fact]
    public void Direction_MonthlyToAnnual_IsNeverADowngrade()
    {
        Plan creator = Catalog("creator");
        BillingEventProcessor.IsDowngrade(creator, 1, creator, 1, BillingInterval.Monthly, BillingInterval.Annual).Should().BeFalse();
        BillingEventProcessor.IsDowngrade(Catalog("pro"), 1, creator, 1, BillingInterval.Monthly, BillingInterval.Annual).Should().BeFalse();
    }
}
