#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>GET /api/v1/billing/promotions/plan-prices: Paddle-priced automatic plan discounts for the public site.</summary>
public sealed class PromotionPlanPricesTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Mock<IBillingProvider> _provider = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly List<(string PriceId, int Quantity, string? DiscountId)> _calls = [];
    private readonly object _gate = new();

    public PromotionPlanPricesTests()
    {
        _provider.SetupGet(p => p.Name).Returns("paddle");
        _provider.SetupGet(p => p.IsConfigured).Returns(true);
        _provider.Setup(p => p.PreviewPriceAsync(It.IsAny<IReadOnlyList<BillingLineItem>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<BillingLineItem> items, string? discount, CancellationToken _) =>
            {
                lock (_gate) _calls.Add((items[0].PriceId, items[0].Quantity, discount));
                return Task.FromResult(new PricePreview("USD", $"${items[0].PriceId}-full", "-$1.00", $"${items[0].PriceId}-sale", "0", "100", $"${items[0].PriceId}-unit-full", $"${items[0].PriceId}-unit-sale"));
            });
        using InferenceApiDbContext db = NewDb();
        db.Plans.AddRange(
            new Plan { Id = Guid.NewGuid(), Key = "trial", Name = "Trial", Kind = PlanKind.Trial, ProviderPriceIdsJson = """{"paddle":{"monthly":"pri_trial"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, IsActive = true, SortOrder = 1, ProviderPriceIdsJson = """{"paddle":{"monthly":"pri_creator"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "pro", Name = "Pro", Kind = PlanKind.Subscription, IsActive = true, SortOrder = 2, ProviderPriceIdsJson = """{"paddle":{"monthly":"pri_pro"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "team", Name = "Team", Kind = PlanKind.PerSeat, MinSeats = 3, IsActive = true, SortOrder = 3, ProviderPriceIdsJson = """{"paddle":{"monthly":"pri_team"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "enterprise", Name = "Enterprise", Kind = PlanKind.Contact, IsActive = true, SortOrder = 4 });
        db.SaveChanges();
    }

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private void Promo(
        string[] priceIds,
        PromotionStatus status = PromotionStatus.Active,
        DateTime? endsAt = null,
        string? code = null,
        PromotionDuration duration = PromotionDuration.Forever,
        int? months = null,
        string? headline = "Launch offer")
    {
        using InferenceApiDbContext db = NewDb();
        db.Promotions.Add(new Promotion
        {
            Id = Guid.NewGuid(),
            Kind = PromotionKind.PlanDiscount,
            Name = "n",
            PublicHeadline = headline,
            Code = code,
            Status = status,
            EndsAt = endsAt,
            DiscountType = PromotionDiscountType.Percentage,
            DiscountAmount = "50",
            DurationKind = duration,
            DurationMonths = months,
            TargetPriceIdsJson = JsonSerializer.Serialize(priceIds),
            ProviderDiscountId = "dsc_promo",
            CreatedAt = Now.AddDays(-1)
        });
        db.SaveChanges();
    }

    private (PromotionService Promotions, PromotionPlanPriceService PlanPrices) Services(IBillingProvider? provider, InferenceApiDbContext db)
    {
        FakeTime time = new(Now);
        IBillingProvider[] providers = provider is null ? [] : [provider];
        PromotionService promotions = new(db, providers, new PromotionCodeAttemptLimiter(time), NullLogger<PromotionService>.Instance, time);
        return (promotions, new PromotionPlanPriceService(db, providers, promotions, _cache, NullLogger<PromotionPlanPriceService>.Instance));
    }

    private async Task<JsonElement> Get(bool enabled = true, bool withProvider = true)
    {
        InferenceApiDbContext db = NewDb();
        (PromotionService promotions, PromotionPlanPriceService planPrices) = Services(withProvider ? _provider.Object : null, db);
        PromotionsController controller = PromotionTestBase.WithHttp(new PromotionsController(
            new PromoUser(Guid.Empty, null, null, false), PromotionTestBase.Billing(enabled), promotions, planPrices));
        IActionResult result = await controller.PlanPrices(default);
        if (!enabled)
        {
            result.Should().BeOfType<NotFoundResult>();
            return default;
        }

        controller.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=60");
        return PromotionTestBase.JsonOf(result).GetProperty("items");
    }

    private static string[] Keys(JsonElement items) => items.EnumerateArray().Select(i => i.GetProperty("planKey").GetString()!).OrderBy(k => k).ToArray();

    [Fact]
    public async Task All_purchasable_plans_when_the_promotion_lists_every_price()
    {
        Promo(["pri_creator", "pri_pro", "pri_team", "pri_trial"]);
        Keys(await Get()).Should().Equal("creator", "pro", "team");
    }

    [Fact]
    public async Task Only_the_listed_plan_is_included()
    {
        Promo(["pri_pro"]);
        Keys(await Get()).Should().Equal("pro");
    }

    [Fact]
    public async Task Expired_promotion_gives_no_items()
    {
        Promo(["pri_pro"], endsAt: Now.AddMinutes(-1));
        (await Get()).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Paused_promotion_gives_no_items()
    {
        Promo(["pri_pro"], status: PromotionStatus.Paused);
        (await Get()).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Code_only_promotion_is_never_used()
    {
        Promo(["pri_pro"], code: "SECRET50");
        (await Get()).GetArrayLength().Should().Be(0);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_throw_drops_only_that_plan()
    {
        Promo(["pri_creator", "pri_pro"]);
        _provider.Setup(p => p.PreviewPriceAsync(It.Is<IReadOnlyList<BillingLineItem>>(i => i[0].PriceId == "pri_pro"), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("boom"));
        Keys(await Get()).Should().Equal("creator");
    }

    [Fact]
    public async Task Non_zero_tax_drops_the_item()
    {
        Promo(["pri_creator", "pri_pro"]);
        Returns("pri_pro", new PricePreview("USD", "$79.00", "-$39.50", "$45.00", "550", "3950"));
        Keys(await Get()).Should().Equal("creator");
    }

    [Fact]
    public async Task Zero_discount_drops_the_item()
    {
        Promo(["pri_creator", "pri_pro"]);
        Returns("pri_pro", new PricePreview("USD", "$79.00", "$0.00", "$79.00", "0", "0"));
        Keys(await Get()).Should().Equal("creator");
    }

    [Fact]
    public async Task Unknown_raw_totals_drop_the_item()
    {
        Promo(["pri_pro"]);
        Returns("pri_pro", new PricePreview("USD", "$79.00", "-$39.50", "$39.50"));
        (await Get()).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task An_empty_string_drops_the_item()
    {
        Promo(["pri_pro"]);
        Returns("pri_pro", new PricePreview("USD", "", "-$39.50", "$39.50", "0", "3950"));
        (await Get()).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_second_request_within_a_minute_is_served_from_the_cache()
    {
        Promo(["pri_pro"]);
        await Get();
        await Get();
        _calls.Should().HaveCount(1);
        _cache.TryGetValue("promotions:plan-prices", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_misses_make_one_round_of_provider_calls()
    {
        Promo(["pri_creator", "pri_pro"]);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Get()));
        _calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_empty_result_is_cached_too()
    {
        await Get();
        Promo(["pri_pro"]);
        (await Get()).GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData(PromotionDuration.Once, null, "once")]
    [InlineData(PromotionDuration.Months, 3, "months")]
    [InlineData(PromotionDuration.Forever, null, "forever")]
    public async Task Duration_passes_through(PromotionDuration duration, int? months, string expected)
    {
        DateTime ends = Now.AddDays(10);
        Promo(["pri_pro"], duration: duration, months: months, endsAt: ends);
        JsonElement item = (await Get())[0];
        item.GetProperty("durationKind").GetString().Should().Be(expected);
        item.GetProperty("durationMonths").ValueKind.Should().Be(months is null ? JsonValueKind.Null : JsonValueKind.Number);
        if (months is not null) item.GetProperty("durationMonths").GetInt32().Should().Be(months);
        item.GetProperty("endsAt").GetDateTime().Should().Be(ends);
    }

    [Fact]
    public async Task Per_seat_plan_is_previewed_at_its_minimum_seats_and_shows_paddles_per_seat_figures()
    {
        Promo(["pri_team"]);
        JsonElement item = (await Get())[0];
        item.GetProperty("planKey").GetString().Should().Be("team");
        item.GetProperty("perSeat").GetBoolean().Should().BeTrue();
        _calls.Should().ContainSingle().Which.Should().Be(("pri_team", 3, "dsc_promo"), "Team's MinSeats is 3; Paddle refuses one seat");
        item.GetProperty("original").GetString().Should().Be("$pri_team-unit-full", "the per-seat figure is Paddle's formatted_unit_totals");
        item.GetProperty("discounted").GetString().Should().Be("$pri_team-unit-sale");
    }

    [Fact]
    public async Task A_per_seat_plan_without_paddle_unit_totals_is_left_out()
    {
        Promo(["pri_team"]);
        Returns("pri_team", new PricePreview("USD", "$147.00", "-$73.50", "$73.50", "0", "7350"));
        (await Get()).GetArrayLength().Should().Be(0, "a line total is never divided into a per-seat price");
    }

    [Fact]
    public void A_paddle_error_detail_is_sanitised_for_the_log()
    {
        PromotionPlanPriceService.SanitiseDetail("Quantity 1 is below the minimum 3 for\nprice pri_team (owner ops@acme.example)")
            .Should().Be("Quantity 1 is below the minimum 3 forprice pri_team (owner [email])");
        PromotionPlanPriceService.SanitiseDetail(new string('x', 500)).Should().HaveLength(200);
        PromotionPlanPriceService.SanitiseDetail(null).Should().Be("-");
    }

    [Fact]
    public async Task A_promotion_without_a_public_headline_still_shows_paddle_prices()
    {
        Promo(["pri_pro"], headline: null);
        JsonElement item = (await Get())[0];
        item.GetProperty("discounted").GetString().Should().Be("$pri_pro-sale");
        item.GetProperty("headline").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_item_carries_paddle_strings_and_no_ids()
    {
        Promo(["pri_pro"]);
        JsonElement item = (await Get())[0];
        item.GetProperty("original").GetString().Should().Be("$pri_pro-full");
        item.GetProperty("discounted").GetString().Should().Be("$pri_pro-sale");
        item.GetProperty("currencyCode").GetString().Should().Be("USD");
        item.GetProperty("headline").GetString().Should().Be("Launch offer");
        item.GetRawText().Should().NotContain("pri_pro\"").And.NotContain("dsc_").And.NotContain("promotionId");
    }

    [Fact]
    public async Task A_promotion_whose_global_cap_is_used_up_is_not_shown()
    {
        Promo(["pri_pro"]);
        using (InferenceApiDbContext db = NewDb())
        {
            Promotion promo = db.Promotions.Single();
            promo.MaxRedemptions = 5;
            promo.RedemptionCount = 5;
            db.SaveChanges();
        }

        (await Get()).GetArrayLength().Should().Be(0);
        _calls.Should().BeEmpty("checkout would refuse it, so Paddle is not even asked");
    }

    [Fact]
    public async Task A_caller_that_aborts_mid_build_still_leaves_the_result_cached()
    {
        Promo(["pri_creator", "pri_pro", "pri_team"]);
        _provider.Setup(p => p.PreviewPriceAsync(It.IsAny<IReadOnlyList<BillingLineItem>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<BillingLineItem> items, string? discount, CancellationToken token) =>
            {
                lock (_gate) _calls.Add((items[0].PriceId, items[0].Quantity, discount));
                await Task.Delay(200, token);
                return new PricePreview("USD", "$full", "-$1.00", "$sale", "0", "100", "$unit-full", "$unit-sale");
            });
        InferenceApiDbContext db = NewDb();
        (_, PromotionPlanPriceService planPrices) = Services(_provider.Object, db);
        using CancellationTokenSource aborted = new();
        aborted.CancelAfter(20);

        IReadOnlyList<PlanPriceItem> first = await planPrices.GetAsync(aborted.Token);
        first.Should().HaveCount(3, "the build ignores the caller's token and only honours the preview timeout");

        (await Get()).GetArrayLength().Should().Be(3);
        _calls.Should().HaveCount(3, "the next caller is served from the cache, not a second round of previews");
    }

    [Fact]
    public async Task A_failing_promotion_lookup_shows_no_discount_and_never_throws()
    {
        Mock<IPromotionService> broken = new();
        broken.Setup(p => p.ActiveAutomaticAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        PromotionPlanPriceService planPrices = new(NewDb(), [_provider.Object], broken.Object, _cache, NullLogger<PromotionPlanPriceService>.Instance);

        (await planPrices.GetAsync(default)).Should().BeEmpty();
        (await planPrices.GetAsync(default)).Should().BeEmpty();
        broken.Verify(p => p.ActiveAutomaticAsync(It.IsAny<CancellationToken>()), Times.Once(), "the empty result is cached like any other");
    }

    [Fact]
    public async Task Billing_disabled_is_404()
        => await Get(enabled: false);

    [Fact]
    public async Task No_provider_gives_empty_items()
    {
        Promo(["pri_pro"]);
        (await Get(withProvider: false)).GetArrayLength().Should().Be(0);
    }

    private void Returns(string priceId, PricePreview preview)
        => _provider.Setup(p => p.PreviewPriceAsync(It.Is<IReadOnlyList<BillingLineItem>>(i => i[0].PriceId == priceId), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(preview);

    private sealed class FakeTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
