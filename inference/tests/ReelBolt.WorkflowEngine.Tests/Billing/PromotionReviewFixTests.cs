#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// saas-promotions T-011: regression tests for the security review of the promotions
/// (S-01, S-03, S-04, S-09, S-11, S-13, S-16, S-24, S-26).
/// </summary>
public sealed class PromotionReviewFixTests : PromotionTestBase
{
    private async Task<BillingEventOutcome> Process(BillingEvent evt)
    {
        await using InferenceApiDbContext db = NewDb();
        return await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, Clock).ProcessAsync(evt, "{}");
    }

    private BillingEvent PackPaid(string eventId, string txn, Guid? org = null, int credits = 500, Guid? customDataBonus = null) => new()
    {
        Provider = "fake",
        EventId = eventId,
        EventType = "transaction.completed",
        OccurredAt = Now,
        Kind = BillingEventKind.TopUpPaid,
        OrganizationId = org ?? Org,
        TransactionId = txn,
        TopUpCredits = credits,
        Items = [new BillingLineItem($"pri_topup_{credits}", 1)],
        BonusPromotionId = customDataBonus,
    };

    private void AddCheckout(string txn, Guid? bonus = null, Guid? discount = null, Guid? org = null, string price = "pri_topup_500", int? credits = 500)
    {
        using InferenceApiDbContext db = NewDb();
        db.PromotionCheckouts.Add(new PromotionCheckout
        {
            Id = Guid.NewGuid(),
            ProviderTransactionId = txn,
            OrganizationId = org ?? Org,
            UserId = OwnerId,
            BonusPromotionId = bonus,
            DiscountPromotionId = discount,
            PriceId = price,
            TopUpCredits = credits,
            CreatedAt = Now,
            WithinCapsAtReservation = true,
        });
        db.SaveChanges();
    }

    private Guid AddOrg(string slug)
    {
        using InferenceApiDbContext db = NewDb();
        Guid id = Guid.NewGuid();
        db.Organizations.Add(new Organization { Id = id, Name = slug, Slug = slug, Kind = OrganizationKind.Team });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = id, UserId = OwnerId, Role = OrganizationRole.Owner });
        db.SaveChanges();
        return id;
    }

    private async Task<long> PromoGrantedMillisAsync(Guid? org = null)
    {
        await using InferenceApiDbContext db = NewDb();
        Guid o = org ?? Org;
        return (await db.CreditGrants.Where(g => g.OrganizationId == o && g.Source == CreditGrantSource.Promo).Select(g => g.AmountMillis).ToListAsync()).Sum();
    }

    private async Task<int> BonusRowsAsync()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.PromotionRedemptions.CountAsync(r => r.Kind == PromotionRedemptionKind.BonusGranted && r.Outcome == PromotionRedemptionOutcome.Granted);
    }

    private Task<IActionResult> Redeem(string code, Guid? user = null) => Promotions(user).Redeem(new RedeemPromotionDto(code), default);

    // ------------------------------------------------------------------ S-01: bonus only from the server's checkout record

    [Fact]
    public async Task S01_a_forged_bonus_promotion_id_on_a_transaction_the_server_did_not_create_grants_nothing()
    {
        Guid bonus = Seed(PromotionKind.PackBonus);

        (await Process(PackPaid("evt_1", "txn_forged", customDataBonus: bonus))).Should().Be(BillingEventOutcome.Applied);

        (await BalanceMillisAsync()).Should().Be(500_000, "the top-up itself is paid for; the bonus was never attached by this server");
        (await BonusRowsAsync()).Should().Be(0);
        await using InferenceApiDbContext db = NewDb();
        (await db.Promotions.SingleAsync(p => p.Id == bonus)).RedemptionCount.Should().Be(0);
    }

    [Fact]
    public async Task S01_checkout_records_the_promotions_and_the_webhook_grants_the_bonus_from_that_record()
    {
        await SubscribeAsync();
        Guid bonus = Seed(PromotionKind.PackBonus);

        IActionResult result = await BillingApi().Checkout(new CheckoutRequestDto(null, null, 500), default);
        StatusOf(result).Should().Be(200);
        string txn = JsonOf(result).GetProperty("transactionId").GetString()!;
        await using (InferenceApiDbContext db = NewDb())
        {
            PromotionCheckout row = await db.PromotionCheckouts.SingleAsync();
            row.ProviderTransactionId.Should().Be(txn);
            row.OrganizationId.Should().Be(Org);
            row.UserId.Should().Be(OwnerId);
            row.BonusPromotionId.Should().Be(bonus);
            row.DiscountPromotionId.Should().BeNull();
            row.PriceId.Should().Be("pri_topup_500");
            row.TopUpCredits.Should().Be(500);
            row.CompletedAt.Should().BeNull();
        }

        // The custom data no longer matters: the webhook reads the stored record.
        await Process(PackPaid("evt_1", txn, customDataBonus: null));

        (await PromoGrantedMillisAsync()).Should().Be(100_000);
        await using InferenceApiDbContext check = NewDb();
        (await check.PromotionCheckouts.SingleAsync()).CompletedAt.Should().NotBeNull();
        (await check.PromotionRedemptions.SingleAsync()).UserId.Should().Be(OwnerId);
    }

    [Fact]
    public async Task S01_a_checkout_without_any_promotion_writes_no_record()
    {
        await SubscribeAsync();

        StatusOf(await BillingApi().Checkout(new CheckoutRequestDto(null, null, 500), default)).Should().Be(200);

        await using InferenceApiDbContext db = NewDb();
        (await db.PromotionCheckouts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task S01_an_event_for_another_org_grants_no_bonus()
    {
        Guid bonus = Seed(PromotionKind.PackBonus);
        Guid other = AddOrg("other");
        AddCheckout("txn_a", bonus: bonus);

        await Process(PackPaid("evt_1", "txn_a", org: other, customDataBonus: bonus));

        (await PromoGrantedMillisAsync(other)).Should().Be(0);
        (await PromoGrantedMillisAsync()).Should().Be(0);
        (await BonusRowsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task S01_a_purchased_price_other_than_the_checkouts_grants_no_bonus()
    {
        Guid bonus = Seed(PromotionKind.PackBonus);
        AddCheckout("txn_a", bonus: bonus, price: "pri_topup_500", credits: 500);

        await Process(PackPaid("evt_1", "txn_a", credits: 1000, customDataBonus: bonus));

        (await PromoGrantedMillisAsync()).Should().Be(0);
    }

    [Fact]
    public async Task S01_a_deleted_promotion_grants_no_bonus()
    {
        AddCheckout("txn_a", bonus: Guid.NewGuid());

        await Process(PackPaid("evt_1", "txn_a"));

        (await PromoGrantedMillisAsync()).Should().Be(0);
        (await BalanceMillisAsync()).Should().Be(500_000);
    }

    // ------------------------------------------------------------------ S-03: caps reserved at checkout, re-checked in the webhook

    [Fact]
    public async Task S03_the_per_org_cap_is_rechecked_in_the_webhook()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => p.MaxRedemptionsPerOrg = 1);
        AddCheckout("txn_a", bonus: bonus);
        AddCheckout("txn_b", bonus: bonus);

        await Process(PackPaid("evt_a", "txn_a"));
        await Process(PackPaid("evt_b", "txn_b"));

        (await PromoGrantedMillisAsync()).Should().Be(100_000, "the second checkout's bonus would exceed the once-per-org limit");
        (await BonusRowsAsync()).Should().Be(1);
        (await BalanceMillisAsync()).Should().Be(1_000_000 + 100_000);
    }

    [Fact]
    public async Task S03_a_reserved_checkout_paid_over_the_global_cap_is_honoured_as_overshoot()
    {
        // Round 3 (C8) supersedes the round-2 "skip": the buyer reserved the bonus and paid for it.
        Guid bonus = Seed(PromotionKind.PackBonus, p => { p.MaxRedemptions = 1; p.RedemptionCount = 1; });
        AddCheckout("txn_a", bonus: bonus);

        await Process(PackPaid("evt_a", "txn_a"));

        (await PromoGrantedMillisAsync()).Should().Be(100_000);
        await using InferenceApiDbContext db = NewDb();
        (await db.Promotions.SingleAsync(p => p.Id == bonus)).RedemptionCount.Should().Be(2);
        (await db.PromotionRedemptions.SingleAsync()).Overshoot.Should().BeTrue();
    }

    [Fact]
    public async Task S03_a_second_checkout_by_another_member_supersedes_the_orgs_open_one()
    {
        // N-01 (C8) supersedes the round-2 rule that the org's first open checkout held its one use:
        // at most one open reservation per (promotion, org), and the newer checkout takes it.
        Seed(PromotionKind.PlanDiscount);
        Guid member = VerifiedUser();

        StatusOf(await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null), default)).Should().Be(200);
        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan");

        StatusOf(await BillingApi(userId: member, role: OrganizationRole.Billing).Checkout(new CheckoutRequestDto("creator", null, null), default)).Should().Be(200);
        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan", "the org's own open checkout is superseded, not held against it");

        await using InferenceApiDbContext db = NewDb();
        (await db.PromotionCheckouts.CountAsync(c => c.SupersededAt == null)).Should().Be(1);
        (await db.PromotionCheckouts.SingleAsync(c => c.SupersededAt == null)).UserId.Should().Be(member);
    }

    [Fact]
    public async Task S03_a_once_per_org_code_already_paid_for_by_the_org_is_400()
    {
        Seed(PromotionKind.PlanDiscount, p => p.Code = "ONCEONLY");

        IActionResult first = await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null, "ONCEONLY"), default);
        string txn = JsonOf(first).GetProperty("transactionId").GetString()!;
        await Process(new BillingEvent
        {
            Provider = "fake",
            EventId = "evt_paid",
            EventType = "transaction.completed",
            OccurredAt = Now,
            Kind = BillingEventKind.TransactionCompleted,
            OrganizationId = Org,
            TransactionId = txn,
            SubscriptionId = "sub_1",
            DiscountId = "dsc_plan",
        });
        IActionResult second = await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null, "ONCEONLY"), default);

        StatusOf(second).Should().Be(400);
        ErrorCode(second).Should().Be("promo_code_invalid");
    }

    [Fact]
    public async Task S03_open_checkouts_of_other_buyers_reserve_the_global_cap()
    {
        Seed(PromotionKind.PlanDiscount, p => { p.MaxRedemptions = 1; p.MaxRedemptionsPerOrg = 5; });
        Guid otherUser = VerifiedUser();
        Guid other = AddOrg("other-buyer");

        await BillingApi(userId: otherUser, orgId: other).Checkout(new CheckoutRequestDto("creator", null, null), default);
        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan");
        await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null), default);

        Provider.LastCheckout!.DiscountId.Should().BeNull();
    }

    [Fact]
    public async Task S03_an_abandoned_checkout_stops_reserving_after_the_reservation_lifetime()
    {
        Seed(PromotionKind.PlanDiscount, p => p.MaxRedemptions = 1);
        Guid otherUser = VerifiedUser();
        Guid other = AddOrg("other-buyer");
        await BillingApi(userId: otherUser, orgId: other).Checkout(new CheckoutRequestDto("creator", null, null), default);

        Clock.Advance(TimeSpan.FromMinutes(PromoOptions.CheckoutReservationMinutes + 1));
        await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null), default);

        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan");
    }

    [Fact]
    public async Task S03_a_retried_bonus_checkout_keeps_the_bonus_and_supersedes_the_first()
    {
        await SubscribeAsync();
        Guid bonus = Seed(PromotionKind.PackBonus);

        await BillingApi().Checkout(new CheckoutRequestDto(null, null, 500), default);
        Provider.LastCheckout!.BonusPromotionId.Should().Be(bonus);
        await BillingApi().Checkout(new CheckoutRequestDto(null, null, 500), default);

        Provider.LastCheckout!.BonusPromotionId.Should().Be(bonus);
        await using InferenceApiDbContext db = NewDb();
        (await db.PromotionCheckouts.CountAsync(c => c.SupersededAt == null)).Should().Be(1);
    }

    // ------------------------------------------------------------------ S-04: per-user limit on credit codes

    [Fact]
    public async Task S04_a_user_cannot_redeem_a_once_per_org_code_again_from_a_new_org()
    {
        Seed(PromotionKind.CreditCode);
        StatusOf(await Redeem("WELCOME50")).Should().Be(200);
        Guid farm = AddOrg("farm");
        PromotionsController inFarm = WithHttp(new PromotionsController(new PromoUser(OwnerId, farm, OrganizationRole.Owner, false), Billing(true), Service(NewDb()), NoPlanPrices));

        IActionResult result = await inFarm.Redeem(new RedeemPromotionDto("WELCOME50"), default);

        StatusOf(result).Should().Be(409);
        ErrorCode(result).Should().Be("promo_already_redeemed");
        (await PromoGrantedMillisAsync(farm)).Should().Be(0);
    }

    // ------------------------------------------------------------------ S-09 / S-16 / S-13: the limiter

    [Fact]
    public async Task S09_failures_by_one_member_limit_the_whole_org()
    {
        Seed(PromotionKind.CreditCode);
        for (int i = 0; i < 10; i++)
        {
            StatusOf(await Redeem($"WRONG{i:000}")).Should().Be(400);
        }

        IActionResult byAnotherMember = await Redeem("WELCOME50", VerifiedUser());

        StatusOf(byAnotherMember).Should().Be(429);
        ErrorCode(byAnotherMember).Should().Be("promo_rate_limited");
    }

    [Fact]
    public void S09_concurrent_attempts_cannot_overshoot_the_limit()
    {
        PromotionCodeAttemptLimiter limiter = new(Clock);
        Guid user = Guid.NewGuid();
        Guid org = Guid.NewGuid();
        ConcurrentBag<PromotionCodeAttemptLimiter.Attempt> admitted = [];

        Parallel.For(0, 200, _ =>
        {
            if (limiter.TryBegin(user, org) is { } attempt)
            {
                admitted.Add(attempt);
            }
        });

        admitted.Should().HaveCount(PromotionCodeAttemptLimiter.MaxFailures);
    }

    [Fact]
    public async Task S09_a_successful_redeem_does_not_count_against_the_limit()
    {
        Seed(PromotionKind.CreditCode);
        for (int i = 0; i < 9; i++)
        {
            await Redeem($"WRONG{i:000}");
        }

        StatusOf(await Redeem("WELCOME50")).Should().Be(200);

        StatusOf(await Redeem("WRONG999")).Should().Be(400, "the success was refunded, so this is the tenth counted attempt");
        StatusOf(await Redeem("WRONG998")).Should().Be(429);
    }

    [Fact]
    public void S16_idle_keys_are_evicted_and_queues_never_exceed_the_limit()
    {
        PromotionCodeAttemptLimiter limiter = new(Clock);
        Guid org = Guid.NewGuid();
        for (int i = 0; i < 500; i++)
        {
            limiter.TryBegin(Guid.NewGuid(), Guid.NewGuid());
        }

        for (int i = 0; i < 50; i++)
        {
            limiter.TryBegin(Guid.NewGuid(), org);
        }

        limiter.LargestQueue.Should().Be(PromotionCodeAttemptLimiter.MaxFailures);

        Clock.Advance(TimeSpan.FromHours(2));
        limiter.TryBegin(Guid.NewGuid(), Guid.NewGuid());

        limiter.TrackedKeys.Should().Be(2, "only the newest attempt's user and org keys are still in their window");
    }

    [Fact]
    public async Task S13_an_exhausted_code_answers_exactly_like_an_unknown_one()
    {
        Seed(PromotionKind.CreditCode, p => { p.MaxRedemptions = 1; p.RedemptionCount = 1; });
        string reference = Body(await Promotions(VerifiedUser()).Redeem(new RedeemPromotionDto("ZZZZZZZZ"), default));

        IActionResult result = await Redeem("WELCOME50");

        StatusOf(result).Should().Be(400);
        Body(result).Should().Be(reference);
    }

    [Fact]
    public async Task S13_exhausted_and_already_redeemed_attempts_count_as_failures()
    {
        Seed(PromotionKind.CreditCode, p => { p.Code = "SPENT001"; p.MaxRedemptions = 1; p.RedemptionCount = 1; });
        Seed(PromotionKind.CreditCode);
        StatusOf(await Redeem("WELCOME50")).Should().Be(200);
        for (int i = 0; i < 5; i++)
        {
            StatusOf(await Redeem("SPENT001")).Should().Be(400);
            StatusOf(await Redeem("WELCOME50")).Should().Be(409);
        }

        StatusOf(await Redeem("WELCOME50")).Should().Be(429);
    }

    // ------------------------------------------------------------------ S-11: discount redemptions per subscription, monthly prices only

    [Fact]
    public async Task S11_renewals_carrying_the_discount_record_one_redemption()
    {
        Guid id = Seed(PromotionKind.PlanDiscount);
        BillingEvent first = new()
        {
            Provider = "fake",
            EventId = "evt_first",
            EventType = "transaction.completed",
            OccurredAt = Now,
            Kind = BillingEventKind.TransactionCompleted,
            OrganizationId = Org,
            SubscriptionId = "sub_1",
            TransactionId = "txn_first",
            DiscountId = "dsc_plan",
            DiscountTotal = "580",
            CurrencyCode = "USD",
        };

        await Process(first);
        await Process(first with { EventId = "evt_renewal", TransactionId = "txn_renewal" });
        await Process(first with { EventId = "evt_renewal2", TransactionId = "txn_renewal2" });

        await using InferenceApiDbContext db = NewDb();
        PromotionRedemption row = await db.PromotionRedemptions.SingleAsync();
        row.ProviderSubscriptionId.Should().Be("sub_1");
        row.ProviderTransactionId.Should().Be("txn_first");
        (await db.Promotions.SingleAsync(p => p.Id == id)).RedemptionCount.Should().Be(1);
    }

    private void SetPrices(string planKey, string json)
    {
        using InferenceApiDbContext db = NewDb();
        db.Plans.Single(p => p.Key == planKey).ProviderPriceIdsJson = json;
        db.SaveChanges();
    }

    private static CreatePromotionDto PlanDiscount(string duration, int? months, params string[] planKeys) => new()
    {
        Kind = "planDiscount",
        Name = "Launch",
        DiscountType = "percentage",
        DiscountAmount = "20",
        Duration = duration,
        DurationMonths = months,
        PlanKeys = planKeys,
    };

    [Fact]
    public async Task A_months_discount_with_includeAnnual_is_a_validation_error()
    {
        SetPrices("creator", """{"fake":{"monthly":"pri_creator","yearly":"pri_creator_y"}}""");
        CreatePromotionDto dto = PlanDiscount("months", 3, "creator");
        dto.IncludeAnnual = true;

        StatusOf(await Admin().Create(dto, default)).Should().Be(400);
        Provider.DiscountsCreated.Should().BeEmpty();
    }

    [Fact]
    public async Task S11_a_months_discount_restricts_to_the_monthly_price_only()
    {
        SetPrices("creator", """{"fake":{"monthly":"pri_creator","yearly":"pri_creator_y"}}""");

        StatusOf(await Admin().Create(PlanDiscount("months", 3, "creator"), default)).Should().Be(201);

        Provider.DiscountsCreated.Single().RestrictToPriceIds.Should().Equal("pri_creator");
    }

    [Fact]
    public async Task S11_a_months_discount_for_every_plan_restricts_to_monthly_prices()
    {
        SetPrices("creator", """{"fake":{"monthly":"pri_creator","yearly":"pri_creator_y"}}""");
        SetPrices("team", """{"fake":{"yearly":"pri_team_y"}}""");

        StatusOf(await Admin().Create(PlanDiscount("months", 3), default)).Should().Be(201);

        Provider.DiscountsCreated.Single().RestrictToPriceIds.Should().Equal("pri_creator");
    }

    [Fact]
    public async Task A_plan_discount_without_an_explicit_annual_target_restricts_to_monthly_prices()
    {
        SetPrices("creator", """{"fake":{"monthly":"pri_creator","annual":"pri_creator_y"}}""");

        StatusOf(await Admin().Create(PlanDiscount("forever", null), default)).Should().Be(201);

        Provider.DiscountsCreated.Single().RestrictToPriceIds.Should().Contain("pri_creator").And.NotContain("pri_creator_y");
    }

    [Fact]
    public async Task A_plan_discount_that_lists_annual_explicitly_also_targets_the_annual_prices()
    {
        SetPrices("creator", """{"fake":{"monthly":"pri_creator","annual":"pri_creator_y"}}""");

        CreatePromotionDto dto = PlanDiscount("forever", null, "creator");
        dto.IncludeAnnual = true;

        StatusOf(await Admin().Create(dto, default)).Should().Be(201);

        Provider.DiscountsCreated.Single().RestrictToPriceIds.Should().Contain(["pri_creator", "pri_creator_y"]);
    }

    [Fact]
    public async Task S11_a_months_discount_on_a_plan_without_a_monthly_price_is_400()
    {
        SetPrices("team", """{"fake":{"yearly":"pri_team_y"}}""");

        IActionResult result = await Admin().Create(PlanDiscount("months", 3, "team"), default);

        StatusOf(result).Should().Be(400);
        ErrorCode(result).Should().Be("target_not_purchasable");
        Provider.DiscountsCreated.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ S-24: overlap re-checked when endsAt grows

    private static JsonElement Patch(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task S24_extending_or_clearing_endsAt_into_an_overlap_is_409()
    {
        Guid early = Seed(PromotionKind.PlanDiscount, p => p.EndsAt = Now.AddDays(2));
        Seed(PromotionKind.PlanDiscount, p => { p.StartsAt = Now.AddDays(3); p.TargetPriceIdsJson = """["pri_creator"]"""; });

        IActionResult extended = await Admin().Update(early, Patch($$"""{"endsAt":"{{Now.AddDays(10):O}}"}"""), default);
        IActionResult cleared = await Admin().Update(early, Patch("""{"clearEndsAt":true}"""), default);

        StatusOf(extended).Should().Be(409);
        ErrorCode(extended).Should().Be("automatic_promotion_overlap");
        StatusOf(cleared).Should().Be(409);
        Provider.DiscountUpdates.Should().BeEmpty();

        StatusOf(await Admin().Update(early, Patch($$"""{"endsAt":"{{Now.AddDays(1):O}}"}"""), default)).Should().Be(200, "shortening never creates an overlap");
    }

    // ------------------------------------------------------------------ S-10: chargebacks

    [Fact]
    public async Task S10_an_approved_chargeback_claws_back_the_top_up_and_its_bonus()
    {
        Guid bonus = Seed(PromotionKind.PackBonus);
        AddCheckout("txn_a", bonus: bonus);
        await Process(PackPaid("evt_1", "txn_a"));
        (await BalanceMillisAsync()).Should().Be(600_000);

        // What the Paddle parser makes of an approved "chargeback" adjustment (see PaddleBillingProviderTests).
        await Process(new BillingEvent
        {
            Provider = "fake",
            EventId = "evt_cb",
            EventType = "adjustment.created",
            OccurredAt = Now,
            Kind = BillingEventKind.PaymentRefunded,
            TransactionId = "txn_a",
            AdjustmentId = "adj_cb",
            FullRefund = true,
        });

        (await BalanceMillisAsync()).Should().Be(0);
    }

    [Fact]
    public async Task S10_an_ignored_event_with_a_reason_keeps_the_reason_on_the_webhook_row()
    {
        (await Process(new BillingEvent
        {
            Provider = "fake",
            EventId = "evt_cbr",
            EventType = "adjustment.created",
            OccurredAt = Now,
            IgnoredReason = "chargeback_reverse: not re-granted automatically",
        })).Should().Be(BillingEventOutcome.Applied);

        await using InferenceApiDbContext db = NewDb();
        (await db.BillingWebhookEvents.SingleAsync(e => e.EventId == "evt_cbr")).Error.Should().Contain("chargeback_reverse");
    }

    // ------------------------------------------------------------------ S-26: requestId replay with a different body

    [Fact]
    public async Task S26_a_replayed_request_id_with_a_different_body_is_409()
    {
        Guid request = Guid.NewGuid();
        DateTime expires = Now.AddDays(30);
        StatusOf(await Credits().Grant(Org, new GrantCreditsDto(request, 100, expires, "support"), default)).Should().Be(201);

        IActionResult credits = await Credits().Grant(Org, new GrantCreditsDto(request, 200, expires, "support"), default);
        IActionResult reason = await Credits().Grant(Org, new GrantCreditsDto(request, 100, expires, "other"), default);
        IActionResult expiry = await Credits().Grant(Org, new GrantCreditsDto(request, 100, expires.AddDays(1), "support"), default);
        IActionResult same = await Credits().Grant(Org, new GrantCreditsDto(request, 100, expires, " support "), default);

        foreach (IActionResult r in new[] { credits, reason, expiry })
        {
            StatusOf(r).Should().Be(409);
            ErrorCode(r).Should().Be("request_id_conflict");
        }

        StatusOf(same).Should().Be(200);
        (await BalanceMillisAsync()).Should().Be(100_000);
    }
}
