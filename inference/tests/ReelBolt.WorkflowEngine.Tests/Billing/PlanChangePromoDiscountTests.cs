using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// Promo rule: a plan change keeps the subscription's recurring promo discount only while the new
/// plan's price is among the promotion's target prices; otherwise the same PATCH removes it and the
/// subscription's redemption row records the removal.
/// </summary>
public sealed class PlanChangePromoDiscountTests : IDisposable
{
    private const string DiscountId = "dsc_1";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _promotion = Guid.NewGuid();
    private readonly Guid _redemption = Guid.NewGuid();
    private readonly BillingControllerTests.FakeProvider _provider = new();
    private readonly DateTime _periodStart = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    public PlanChangePromoDiscountTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = _org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.Plans.AddRange(
            new Plan { Id = Guid.NewGuid(), Key = "trial", Name = "Trial", Kind = PlanKind.Trial, SortOrder = 0 },
            new Plan { Id = Guid.NewGuid(), Key = "starter", Name = "Starter", Kind = PlanKind.Subscription, PriceMonthlyUsd = 9, MonthlyCreditsMillis = 300_000, SortOrder = 1, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_starter"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, PriceMonthlyUsd = 29, MonthlyCreditsMillis = 1_000_000, SortOrder = 2, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_creator"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "pro", Name = "Pro", Kind = PlanKind.Subscription, PriceMonthlyUsd = 79, MonthlyCreditsMillis = 3_500_000, SortOrder = 3, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_pro"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "team", Name = "Team", Kind = PlanKind.PerSeat, MinSeats = 3, PriceMonthlyUsd = 49, MonthlyCreditsMillis = 1_500_000, SortOrder = 4, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_team"}}""" });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb(IInterceptor? interceptor = null)
    {
        DbContextOptionsBuilder<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    private sealed class ListLogger : ILogger<BillingController>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Fails any save that writes a promotion redemption: the plan-change record step.</summary>
    private sealed class FailRedemptionSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<PromotionRedemption>().Any(e => e.State == EntityState.Modified))
            {
                throw new DbUpdateException("redemption save failed");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class FakeUser(Guid org) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => org;
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool IsPlatformAdmin => false;
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private BillingController Controller(DateTime? now = null, ILogger<BillingController>? logger = null, IInterceptor? interceptor = null)
    {
        InferenceApiDbContext db = NewDb(interceptor);
        BillingController c = new(
            db,
            new FakeUser(_org),
            Options.Create(new BillingOptions { Enabled = true }),
            [_provider],
            new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance),
            new BillingControllerTests.FakeTenant(),
            Options.Create(new PaddleOptions()),
            logger ?? NullLogger<BillingController>.Instance,
            time: new FixedClock(now ?? _periodStart.AddDays(3)));
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return c;
    }

    /// <summary>
    /// A Creator subscription (sub_1) bought with a 20% plan discount targeting Creator and Pro; Paddle's
    /// live subscription carries <paramref name="liveDiscount"/> (dsc_1 by default).
    /// </summary>
    private async Task SubscribeWithPromo(
        string planKey = "creator",
        int seats = 1,
        ProviderSubscriptionDiscount? liveDiscount = null,
        bool noLiveDiscount = false,
        PromotionDuration duration = PromotionDuration.Months,
        int? months = 3,
        DateTime? redeemedAt = null)
    {
        await using InferenceApiDbContext db = NewDb();
        Plan plan = await db.Plans.SingleAsync(p => p.Key == planKey);
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = _org,
            PlanId = plan.Id,
            Status = SubscriptionStatus.Active,
            Seats = seats,
            Provider = "fake",
            ProviderSubscriptionId = "sub_1",
            PeriodStart = _periodStart,
            PeriodEnd = _periodStart.AddMonths(1)
        });
        db.Promotions.Add(new Promotion
        {
            Id = _promotion,
            Kind = PromotionKind.PlanDiscount,
            Name = "Autumn internal name",
            Code = "AUTUMN20",
            DiscountType = PromotionDiscountType.Percentage,
            DiscountAmount = "20",
            DurationKind = duration,
            DurationMonths = months,
            TargetPlanKeysJson = """["creator","pro"]""",
            TargetPriceIdsJson = """["pri_creator","pri_pro"]""",
            ProviderDiscountId = DiscountId
        });
        db.PromotionRedemptions.Add(new PromotionRedemption
        {
            Id = _redemption,
            PromotionId = _promotion,
            OrganizationId = _org,
            Kind = PromotionRedemptionKind.DiscountApplied,
            ProviderTransactionId = "txn_1",
            ProviderSubscriptionId = "sub_1",
            DiscountTotal = "580",
            CurrencyCode = "USD",
            CreatedAt = redeemedAt ?? _periodStart
        });
        await db.SaveChangesAsync();

        string priceId = BillingEventProcessor.PriceIdsOf(plan, "fake").Single();
        _provider.Live = new ProviderSubscriptionState(
            "active",
            [new BillingLineItem(priceId, seats)],
            false,
            _periodStart.AddMonths(1),
            _periodStart,
            noLiveDiscount ? null : liveDiscount ?? new ProviderSubscriptionDiscount(DiscountId, null));
    }

    private async Task<PromotionRedemption> Redemption()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.PromotionRedemptions.AsNoTracking().SingleAsync(r => r.Id == _redemption);
    }

    private static JsonElement Json(IActionResult result)
        => JsonDocument.Parse(JsonSerializer.Serialize(((ObjectResult)result).Value)).RootElement;

    [Fact]
    public async Task ChangePlan_ToATargetedPlan_KeepsTheDiscount()
    {
        await SubscribeWithPromo();

        IActionResult result = await Controller().ChangePlan(new ChangePlanRequestDto("pro", null), default);

        result.Should().BeOfType<AcceptedResult>();
        Json(result).GetProperty("promoDiscountRemoved").GetBoolean().Should().BeFalse();
        _provider.PlanChanges.Single().Discount.Should().Be(PlanChangeDiscount.Unchanged);
        PromotionRedemption row = await Redemption();
        row.DiscountRemovedAt.Should().BeNull();
        row.DiscountRemovedForPriceId.Should().BeNull();
    }

    [Theory]
    [InlineData(PromotionDuration.Months)]
    [InlineData(PromotionDuration.Forever)]
    public async Task ChangePlan_ToANonTargetedPlan_RemovesTheDiscount(PromotionDuration duration)
    {
        await SubscribeWithPromo(duration: duration, months: duration == PromotionDuration.Months ? 3 : null);
        DateTime now = _periodStart.AddDays(3);

        IActionResult result = await Controller(now).ChangePlan(new ChangePlanRequestDto("team", 3), default);

        result.Should().BeOfType<AcceptedResult>();
        JsonElement body = Json(result);
        body.GetProperty("direction").GetString().Should().Be("upgrade");
        body.GetProperty("promoDiscountRemoved").GetBoolean().Should().BeTrue();
        var change = _provider.PlanChanges.Single();
        change.Discount.Should().Be(PlanChangeDiscount.Remove);
        change.Items.Should().Equal(new BillingLineItem("pri_team", 3));
        PromotionRedemption row = await Redemption();
        row.DiscountRemovedAt.Should().Be(now);
        row.DiscountRemovedForPriceId.Should().Be("pri_team");
    }

    [Fact]
    public async Task ChangePlan_DowngradeToANonTargetedPlan_RemovesTheDiscount()
    {
        await SubscribeWithPromo();

        IActionResult result = await Controller().ChangePlan(new ChangePlanRequestDto("starter", null), default);

        result.Should().BeOfType<AcceptedResult>();
        JsonElement body = Json(result);
        body.GetProperty("direction").GetString().Should().Be("downgrade");
        body.GetProperty("promoDiscountRemoved").GetBoolean().Should().BeTrue();
        var change = _provider.PlanChanges.Single();
        change.Discount.Should().Be(PlanChangeDiscount.Remove);
        change.Timing.Should().Be(PlanChangeTiming.NextBillingPeriod);
        PromotionRedemption row = await Redemption();
        row.DiscountRemovedAt.Should().NotBeNull();
        row.DiscountRemovedForPriceId.Should().Be("pri_starter");
    }

    /// <summary>Founder price: forever, no code, every sellable plan targeted, already past its endsAt.</summary>
    private async Task MakeFounderPrice()
    {
        await using InferenceApiDbContext db = NewDb();
        Promotion p = await db.Promotions.SingleAsync(x => x.Id == _promotion);
        p.Name = "Founder price";
        p.Code = null;
        p.DiscountAmount = "50";
        p.DurationKind = PromotionDuration.Forever;
        p.DurationMonths = null;
        p.EndsAt = _periodStart.AddDays(1);
        p.TargetPlanKeysJson = """["starter","creator","pro","team"]""";
        p.TargetPriceIdsJson = """["pri_starter","pri_creator","pri_pro","pri_team"]""";
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("pro", null, "upgrade")]
    [InlineData("starter", null, "downgrade")]
    [InlineData("team", 3, "upgrade")]
    public async Task FounderPrice_SurvivesUpgradeAndDowngrade_EvenAfterItsEndsAt(string target, int? seats, string direction)
    {
        await SubscribeWithPromo(duration: PromotionDuration.Forever, months: null, redeemedAt: _periodStart.AddDays(-30));
        await MakeFounderPrice();

        // The change happens after endsAt: the discount stays because the redemption row, not the date, carries it.
        IActionResult result = await Controller(_periodStart.AddDays(3)).ChangePlan(new ChangePlanRequestDto(target, seats), default);

        result.Should().BeOfType<AcceptedResult>();
        JsonElement body = Json(result);
        body.GetProperty("direction").GetString().Should().Be(direction);
        body.GetProperty("promoDiscountRemoved").GetBoolean().Should().BeFalse();
        _provider.PlanChanges.Single().Discount.Should().Be(PlanChangeDiscount.Unchanged);
        (await Redemption()).DiscountRemovedAt.Should().BeNull();

        JsonElement promo = Json(await Controller(_periodStart.AddDays(3)).Summary(default)).GetProperty("subscription").GetProperty("promoDiscount");
        promo.GetProperty("amount").GetString().Should().Be("50");
    }

    [Fact]
    public async Task FounderPrice_SurvivesASeatChange_AndShowsInTheSummaryAfterEndsAt()
    {
        await SubscribeWithPromo(planKey: "team", seats: 3, duration: PromotionDuration.Forever, months: null, redeemedAt: _periodStart.AddDays(-30));
        await MakeFounderPrice();

        IActionResult seats = await Controller(_periodStart.AddDays(10)).ChangeSeats(new ChangeSeatsRequestDto(5), default);

        seats.Should().BeOfType<AcceptedResult>();
        Json(seats).GetProperty("promoDiscountRemoved").GetBoolean().Should().BeFalse();
        _provider.PlanChanges.Single().Discount.Should().Be(PlanChangeDiscount.Unchanged);
        (await Redemption()).DiscountRemovedAt.Should().BeNull();
    }

    /// <summary>The founder discount is created through the real service with the PRESET (not a hand-set list), then a new plan is sold.</summary>
    [Fact]
    public async Task FounderPrice_CreatedFromThePreset_SurvivesAChangeToAPlanAddedAfterwards()
    {
        FixedClock clock = new(_periodStart);
        await using (InferenceApiDbContext db = NewDb())
        {
            PromotionService service = new(db, [_provider], new PromotionCodeAttemptLimiter(clock), NullLogger<PromotionService>.Instance, clock, Options.Create(new PromotionOptions()), new InMemoryMailRateCounterStore());
            (await service.CreateAsync(PromotionPresets.FounderPrice, Guid.NewGuid(), default)).Error.Should().BeNull();
        }

        await using (InferenceApiDbContext db = NewDb())
        {
            Plan creator = await db.Plans.SingleAsync(p => p.Key == "creator");
            Promotion founder = await db.Promotions.SingleAsync();
            founder.ProviderDiscountId.Should().Be(DiscountId);
            founder.TargetPriceIdsJson.Should().NotContain("pri_studio");
            db.Subscriptions.Add(new Subscription
            {
                Id = Guid.NewGuid(),
                OrganizationId = _org,
                PlanId = creator.Id,
                Status = SubscriptionStatus.Active,
                Seats = 1,
                Provider = "fake",
                ProviderSubscriptionId = "sub_1",
                PeriodStart = _periodStart,
                PeriodEnd = _periodStart.AddMonths(1)
            });
            db.PromotionRedemptions.Add(new PromotionRedemption
            {
                Id = _redemption,
                PromotionId = founder.Id,
                OrganizationId = _org,
                Kind = PromotionRedemptionKind.DiscountApplied,
                ProviderTransactionId = "txn_1",
                ProviderSubscriptionId = "sub_1",
                DiscountTotal = "1450",
                CurrencyCode = "USD",
                CreatedAt = _periodStart
            });
            // A plan sold only after the founder promotion was created.
            db.Plans.Add(new Plan { Id = Guid.NewGuid(), Key = "studio", Name = "Studio", Kind = PlanKind.Subscription, PriceMonthlyUsd = 149, MonthlyCreditsMillis = 7_000_000, SortOrder = 5, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_studio"}}""" });
            await db.SaveChangesAsync();
        }

        _provider.Live = new ProviderSubscriptionState("active", [new BillingLineItem("pri_creator", 1)], false, _periodStart.AddMonths(1), _periodStart, new ProviderSubscriptionDiscount(DiscountId, null));

        IActionResult result = await Controller(_periodStart.AddDays(3)).ChangePlan(new ChangePlanRequestDto("studio", null), default);

        result.Should().BeOfType<AcceptedResult>();
        Json(result).GetProperty("promoDiscountRemoved").GetBoolean().Should().BeFalse();
        _provider.PlanChanges.Single().Discount.Should().Be(PlanChangeDiscount.Unchanged);
        (await Redemption()).DiscountRemovedAt.Should().BeNull();
    }

    [Fact]
    public async Task ChangePlan_Replayed_RemovesAndRecordsOnce()
    {
        await SubscribeWithPromo();
        DateTime first = _periodStart.AddDays(3);

        IActionResult once = await Controller(first).ChangePlan(new ChangePlanRequestDto("team", 3), default);
        IActionResult twice = await Controller(first.AddMinutes(1)).ChangePlan(new ChangePlanRequestDto("team", 3), default);

        once.Should().BeOfType<AcceptedResult>();
        twice.Should().BeOfType<ConflictObjectResult>();
        Json(twice).GetProperty("error").GetString().Should().BeOneOf("change_pending", "no_change");
        _provider.PlanChanges.Should().ContainSingle();
        (await Redemption()).DiscountRemovedAt.Should().Be(first);
    }

    [Fact]
    public async Task ChangePlan_ProviderFails_RecordsNothing()
    {
        await SubscribeWithPromo();
        _provider.Fail = new HttpRequestException("Paddle 500");

        IActionResult result = await Controller().ChangePlan(new ChangePlanRequestDto("team", 3), default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
        PromotionRedemption row = await Redemption();
        row.DiscountRemovedAt.Should().BeNull();
        row.DiscountRemovedForPriceId.Should().BeNull();
    }

    [Theory]
    [InlineData("dsc_manual", false)]
    [InlineData(DiscountId, true)]
    public async Task ChangePlan_UnknownOrEndedDiscount_IsLeftAlone(string liveDiscountId, bool ended)
    {
        DateTime now = _periodStart.AddDays(3);
        await SubscribeWithPromo(liveDiscount: new ProviderSubscriptionDiscount(liveDiscountId, ended ? now.AddDays(-1) : null));

        IActionResult result = await Controller(now).ChangePlan(new ChangePlanRequestDto("team", 3), default);

        result.Should().BeOfType<AcceptedResult>();
        Json(result).GetProperty("promoDiscountRemoved").GetBoolean().Should().BeFalse();
        _provider.PlanChanges.Single().Discount.Should().Be(PlanChangeDiscount.Unchanged);
        (await Redemption()).DiscountRemovedAt.Should().BeNull();
    }

    [Fact]
    public async Task KeepPlan_AndSeatChange_NeverTouchTheDiscount()
    {
        // A team subscription whose promotion (somehow) does not target the team price: a seat change on
        // the same plan still leaves the discount alone.
        await SubscribeWithPromo(planKey: "team", seats: 3);

        IActionResult seats = await Controller().ChangeSeats(new ChangeSeatsRequestDto(5), default);

        seats.Should().BeOfType<AcceptedResult>();
        Json(seats).GetProperty("promoDiscountRemoved").GetBoolean().Should().BeFalse();
        _provider.PlanChanges.Single().Discount.Should().Be(PlanChangeDiscount.Unchanged);

        // "Keep my plan" while a downgrade is held: the current items go back, the discount is not touched.
        await using (InferenceApiDbContext db = NewDb())
        {
            Subscription sub = await db.Subscriptions.SingleAsync();
            Plan starter = await db.Plans.SingleAsync(p => p.Key == "starter");
            sub.ScheduledPlanId = starter.Id;
            sub.ScheduledSeats = 1;
            await db.SaveChangesAsync();
        }

        _provider.Live = _provider.Live! with { Items = [new BillingLineItem("pri_starter", 1)] };
        IActionResult keep = await Controller().ChangePlan(new ChangePlanRequestDto("team", 3), default);

        keep.Should().BeOfType<AcceptedResult>();
        Json(keep).GetProperty("direction").GetString().Should().Be("keep");
        Json(keep).GetProperty("promoDiscountRemoved").GetBoolean().Should().BeFalse();
        _provider.PlanChanges.Should().HaveCount(2);
        _provider.PlanChanges.Should().OnlyContain(c => c.Discount == PlanChangeDiscount.Unchanged);
        (await Redemption()).DiscountRemovedAt.Should().BeNull();
    }

    [Fact]
    public async Task Summary_ExposesTheActivePromoDiscount()
    {
        await SubscribeWithPromo();

        JsonElement promo = Json(await Controller().Summary(default)).GetProperty("subscription").GetProperty("promoDiscount");

        promo.ValueKind.Should().Be(JsonValueKind.Object);
        promo.GetProperty("discountType").GetString().Should().Be("Percentage");
        promo.GetProperty("amount").GetString().Should().Be("20");
        promo.GetProperty("currencyCode").ValueKind.Should().Be(JsonValueKind.Null);
        promo.GetProperty("targetPriceIds").EnumerateArray().Select(e => e.GetString()).Should().Equal("pri_creator", "pri_pro");
        // No promotion name or internal id reaches the customer.
        promo.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["discountType", "amount", "currencyCode", "targetPriceIds"]);

        // Removed on a plan change: gone from the summary.
        (await Controller().ChangePlan(new ChangePlanRequestDto("team", 3), default)).Should().BeOfType<AcceptedResult>();
        Json(await Controller().Summary(default)).GetProperty("subscription").GetProperty("promoDiscount").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Summary_PromoDiscount_IsNullOnceAMonthsPromoHasRunOut()
    {
        await SubscribeWithPromo(months: 3, redeemedAt: _periodStart.AddMonths(-4));

        JsonElement subscription = Json(await Controller().Summary(default)).GetProperty("subscription");

        subscription.GetProperty("promoDiscount").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Summary_PromoDiscount_ForeverPromoStaysAndFlatCarriesItsCurrency()
    {
        await SubscribeWithPromo(duration: PromotionDuration.Forever, months: null, redeemedAt: _periodStart.AddYears(-2));
        await using (InferenceApiDbContext db = NewDb())
        {
            Promotion p = await db.Promotions.SingleAsync();
            p.DiscountType = PromotionDiscountType.Flat;
            p.DiscountAmount = "500";
            p.CurrencyCode = "USD";
            await db.SaveChangesAsync();
        }

        JsonElement promo = Json(await Controller().Summary(default)).GetProperty("subscription").GetProperty("promoDiscount");

        promo.GetProperty("discountType").GetString().Should().Be("Flat");
        promo.GetProperty("amount").GetString().Should().Be("500");
        promo.GetProperty("currencyCode").GetString().Should().Be("USD");
    }

    private async Task UpdateRedemption(Action<PromotionRedemption> change)
    {
        await using InferenceApiDbContext db = NewDb();
        PromotionRedemption row = await db.PromotionRedemptions.SingleAsync(r => r.Id == _redemption);
        change(row);
        await db.SaveChangesAsync();
    }

    private async Task SetDuration(PromotionDuration? duration)
    {
        await using InferenceApiDbContext db = NewDb();
        Promotion p = await db.Promotions.SingleAsync(x => x.Id == _promotion);
        p.DurationKind = duration;
        p.DurationMonths = duration == PromotionDuration.Months ? 3 : null;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> OtherOrg()
    {
        Guid other = Guid.NewGuid();
        await using InferenceApiDbContext db = NewDb();
        db.Organizations.Add(new Organization { Id = other, Name = "other", Slug = "other", Kind = OrganizationKind.Team });
        await db.SaveChangesAsync();
        return other;
    }

    [Fact]
    public async Task ChangePlan_RowAlreadyRemoved_IsNotOverwrittenByALaterRemoval()
    {
        // QA-9 (a): the row already records a removal, yet Paddle still shows the discount; a later
        // non-target change sends the removal again but never rewrites the first record.
        await SubscribeWithPromo();
        DateTime firstRemoval = _periodStart.AddDays(1);
        await UpdateRedemption(r =>
        {
            r.DiscountRemovedAt = firstRemoval;
            r.DiscountRemovedForPriceId = "pri_starter";
        });
        ListLogger log = new();

        IActionResult result = await Controller(_periodStart.AddDays(3), log).ChangePlan(new ChangePlanRequestDto("team", 3), default);

        result.Should().BeOfType<AcceptedResult>();
        Json(result).GetProperty("promoDiscountRemoved").GetBoolean().Should().BeTrue();
        _provider.PlanChanges.Single().Discount.Should().Be(PlanChangeDiscount.Remove);
        PromotionRedemption row = await Redemption();
        row.DiscountRemovedAt.Should().Be(firstRemoval);
        row.DiscountRemovedForPriceId.Should().Be("pri_starter");
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("No open discount redemption"));
    }

    [Fact]
    public async Task ChangePlan_RecordSaveFails_StillAcceptedAndLogsAnError()
    {
        // QA-9 (b): Paddle already removed the discount, so a failed record is logged, never answered as an error.
        await SubscribeWithPromo();
        ListLogger log = new();

        IActionResult result = await Controller(logger: log, interceptor: new FailRedemptionSave()).ChangePlan(new ChangePlanRequestDto("team", 3), default);

        result.Should().BeOfType<AcceptedResult>();
        Json(result).GetProperty("promoDiscountRemoved").GetBoolean().Should().BeTrue();
        _provider.PlanChanges.Single().Discount.Should().Be(PlanChangeDiscount.Remove);
        log.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("Could not record"));
        (await Redemption()).DiscountRemovedAt.Should().BeNull();
    }

    [Fact]
    public async Task ChangePlan_NoOpenRedemptionRow_StillAcceptedAndLogsAWarning()
    {
        // QA-9 (c): the discount is ours but no DiscountApplied row exists to record on.
        await SubscribeWithPromo();
        await using (InferenceApiDbContext db = NewDb())
        {
            db.PromotionRedemptions.Remove(await db.PromotionRedemptions.SingleAsync());
            await db.SaveChangesAsync();
        }
        ListLogger log = new();

        IActionResult result = await Controller(logger: log).ChangePlan(new ChangePlanRequestDto("team", 3), default);

        result.Should().BeOfType<AcceptedResult>();
        Json(result).GetProperty("promoDiscountRemoved").GetBoolean().Should().BeTrue();
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("No open discount redemption"));
        log.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task ChangePlan_DiscountEndingExactlyNow_CountsAsEnded(long ticksAfterNow, bool removed)
    {
        // QA-12: EndsAt <= now is ended (left alone); one tick later it is still live and is removed.
        DateTime now = _periodStart.AddDays(3);
        await SubscribeWithPromo(liveDiscount: new ProviderSubscriptionDiscount(DiscountId, now.AddTicks(ticksAfterNow)));

        IActionResult result = await Controller(now).ChangePlan(new ChangePlanRequestDto("team", 3), default);

        result.Should().BeOfType<AcceptedResult>();
        Json(result).GetProperty("promoDiscountRemoved").GetBoolean().Should().Be(removed);
        _provider.PlanChanges.Single().Discount.Should().Be(removed ? PlanChangeDiscount.Remove : PlanChangeDiscount.Unchanged);
        (await Redemption()).DiscountRemovedAt.Should().Be(removed ? now : null);
    }

    [Fact]
    public async Task ChangePlan_ProviderRefusesTheRemoval_RecordsNothing()
    {
        // QA-12: a Paddle 4xx on the removing PATCH answers the existing refusal and records nothing.
        await SubscribeWithPromo();
        _provider.Fail = new BillingProviderException("invalid_field", 400, "discount");

        IActionResult result = await Controller().ChangePlan(new ChangePlanRequestDto("team", 3), default);

        result.Should().BeOfType<ConflictObjectResult>();
        Json(result).GetProperty("error").GetString().Should().Be("billing_provider_refused");
        _provider.PlanChanges.Should().BeEmpty();
        PromotionRedemption row = await Redemption();
        row.DiscountRemovedAt.Should().BeNull();
        row.DiscountRemovedForPriceId.Should().BeNull();
    }

    [Fact]
    public async Task Summary_PromoDiscount_LatestRedemptionWins()
    {
        // QA-12: two open DiscountApplied rows on the subscription; the later one is shown.
        await SubscribeWithPromo(redeemedAt: _periodStart);
        await using (InferenceApiDbContext db = NewDb())
        {
            Guid later = Guid.NewGuid();
            db.Promotions.Add(new Promotion
            {
                Id = later,
                Kind = PromotionKind.PlanDiscount,
                Name = "Later",
                Code = "LATER5",
                DiscountType = PromotionDiscountType.Flat,
                DiscountAmount = "500",
                CurrencyCode = "USD",
                DurationKind = PromotionDuration.Forever,
                TargetPlanKeysJson = """["pro"]""",
                TargetPriceIdsJson = """["pri_pro"]""",
                ProviderDiscountId = "dsc_2"
            });
            db.PromotionRedemptions.Add(new PromotionRedemption
            {
                Id = Guid.NewGuid(),
                PromotionId = later,
                OrganizationId = _org,
                Kind = PromotionRedemptionKind.DiscountApplied,
                ProviderTransactionId = "txn_2",
                ProviderSubscriptionId = "sub_1",
                DiscountTotal = "500",
                CurrencyCode = "USD",
                CreatedAt = _periodStart.AddDays(1)
            });
            await db.SaveChangesAsync();
        }

        JsonElement promo = Json(await Controller().Summary(default)).GetProperty("subscription").GetProperty("promoDiscount");

        promo.GetProperty("discountType").GetString().Should().Be("Flat");
        promo.GetProperty("amount").GetString().Should().Be("500");
        promo.GetProperty("targetPriceIds").EnumerateArray().Select(e => e.GetString()).Should().Equal("pri_pro");
    }

    [Fact]
    public async Task ChangePlan_RowOfAnotherOrgOnTheSameSubscription_IsNotUpdated()
    {
        // N-25: the record lookup is scoped to the subscription's organization.
        await SubscribeWithPromo();
        Guid other = await OtherOrg();
        await UpdateRedemption(r => r.OrganizationId = other);
        ListLogger log = new();

        IActionResult result = await Controller(logger: log).ChangePlan(new ChangePlanRequestDto("team", 3), default);

        result.Should().BeOfType<AcceptedResult>();
        PromotionRedemption row = await Redemption();
        row.OrganizationId.Should().Be(other);
        row.DiscountRemovedAt.Should().BeNull();
        row.DiscountRemovedForPriceId.Should().BeNull();
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("No open discount redemption"));
    }

    [Fact]
    public async Task Summary_RowOfAnotherOrgOnTheSameSubscription_IsNotShown()
    {
        // N-25: the summary lookup is scoped to the subscription's organization.
        await SubscribeWithPromo();
        Guid other = await OtherOrg();
        await UpdateRedemption(r => r.OrganizationId = other);

        JsonElement subscription = Json(await Controller().Summary(default)).GetProperty("subscription");

        subscription.GetProperty("promoDiscount").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(PromotionDuration.Once)]
    [InlineData(null)]
    public async Task ChangePlan_LiveDiscountOfANonRecurringPromotion_IsLeftAlone(PromotionDuration? duration)
    {
        // QA-13: only a recurring promotion's discount (Forever or Months) is acted on.
        await SubscribeWithPromo();
        await SetDuration(duration);

        IActionResult result = await Controller().ChangePlan(new ChangePlanRequestDto("team", 3), default);

        result.Should().BeOfType<AcceptedResult>();
        Json(result).GetProperty("promoDiscountRemoved").GetBoolean().Should().BeFalse();
        _provider.PlanChanges.Single().Discount.Should().Be(PlanChangeDiscount.Unchanged);
        PromotionRedemption row = await Redemption();
        row.DiscountRemovedAt.Should().BeNull();
        row.DiscountRemovedForPriceId.Should().BeNull();
    }
}
