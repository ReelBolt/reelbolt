#nullable enable
using System;
using System.Linq;
using System.Net.Http;
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
/// saas-promotions T-023 (fix round 3, contract C8): checkout reservations, per-user caps, overshoot,
/// verified-email gate, promo checkout rate limit and clawback dedupe (F-5, N-01, N-02, N-03, N-11,
/// S-03, S-04, S-11). The advisory-lock races are in <see cref="PromotionPostgresConcurrencyTests"/>.
/// </summary>
public sealed class PromotionRound3Tests : PromotionTestBase
{
    private async Task<BillingEventOutcome> Process(BillingEvent evt)
    {
        await using InferenceApiDbContext db = NewDb();
        return await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, Clock).ProcessAsync(evt, "{}");
    }

    private BillingEvent PackPaid(string eventId, string txn, Guid? org = null) => new()
    {
        Provider = "fake",
        EventId = eventId,
        EventType = "transaction.completed",
        OccurredAt = Now,
        Kind = BillingEventKind.TopUpPaid,
        OrganizationId = org ?? Org,
        TransactionId = txn,
        TopUpCredits = 500,
        Items = [new BillingLineItem("pri_topup_500", 1)],
    };

    private BillingEvent PlanPaid(string eventId, string txn, string sub, Guid? org = null) => new()
    {
        Provider = "fake",
        EventId = eventId,
        EventType = "transaction.completed",
        OccurredAt = Now,
        Kind = BillingEventKind.TransactionCompleted,
        OrganizationId = org ?? Org,
        TransactionId = txn,
        SubscriptionId = sub,
        DiscountId = "dsc_plan",
        DiscountTotal = "580",
        CurrencyCode = "USD",
    };

    private BillingEvent Refund(string eventId, string txn, string adjustment) => new()
    {
        Provider = "fake",
        EventId = eventId,
        EventType = "adjustment.created",
        OccurredAt = Now,
        Kind = BillingEventKind.PaymentRefunded,
        TransactionId = txn,
        AdjustmentId = adjustment,
        FullRefund = true,
    };

    private void AddCheckout(string txn, Guid? bonus = null, Guid? discount = null, Guid? org = null, Guid? user = null, DateTime? createdAt = null, DateTime? supersededAt = null)
    {
        using InferenceApiDbContext db = NewDb();
        db.PromotionCheckouts.Add(new PromotionCheckout
        {
            Id = Guid.NewGuid(),
            ProviderTransactionId = txn,
            OrganizationId = org ?? Org,
            UserId = user ?? OwnerId,
            BonusPromotionId = bonus,
            DiscountPromotionId = discount,
            PriceId = "pri_topup_500",
            TopUpCredits = 500,
            CreatedAt = createdAt ?? Now,
            SupersededAt = supersededAt,
            WithinCapsAtReservation = true,
        });
        db.SaveChanges();
    }

    /// <summary>A Team org; <paramref name="owner"/> (default the fixture owner) is its Owner.</summary>
    private Guid AddOrg(string slug, Guid? owner = null)
    {
        using InferenceApiDbContext db = NewDb();
        Guid id = Guid.NewGuid();
        db.Organizations.Add(new Organization { Id = id, Name = slug, Slug = slug, Kind = OrganizationKind.Team });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = id, UserId = owner ?? OwnerId, Role = OrganizationRole.Owner });
        db.SaveChanges();
        return id;
    }

    private Guid AddUser(string email, bool verified = true)
    {
        using InferenceApiDbContext db = NewDb();
        Guid id = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = id, Email = email, DisplayName = email, EmailVerifiedAt = verified ? DateTime.UtcNow : null });
        db.SaveChanges();
        return id;
    }

    /// <summary>A second, verified buyer with an org of their own.</summary>
    private (Guid User, Guid OrgId) OtherBuyer(string slug)
    {
        Guid user = AddUser($"{slug}@other.test");
        return (user, AddOrg(slug, user));
    }

    private Task<IActionResult> PlanCheckout(Guid? user = null, Guid? org = null, string? code = null)
        => BillingApi(userId: user, orgId: org).Checkout(new CheckoutRequestDto("creator", null, null, code), default);

    private async Task<long> PromoGrantedMillisAsync(Guid? org = null)
    {
        await using InferenceApiDbContext db = NewDb();
        Guid o = org ?? Org;
        return (await db.CreditGrants.Where(g => g.OrganizationId == o && g.Source == CreditGrantSource.Promo).Select(g => g.AmountMillis).ToListAsync()).Sum();
    }

    // ------------------------------------------------------------------ N-01: a new checkout supersedes the caller's own

    [Fact]
    public async Task N01_a_second_checkout_by_the_same_buyer_supersedes_the_first_and_keeps_the_automatic_discount()
    {
        Seed(PromotionKind.PlanDiscount);

        StatusOf(await PlanCheckout()).Should().Be(200);
        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan");
        StatusOf(await PlanCheckout()).Should().Be(200);

        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan", "the abandoned first checkout must not hold the org's one use against its own retry");
        await using InferenceApiDbContext db = NewDb();
        var rows = await db.PromotionCheckouts.OrderBy(c => c.ProviderTransactionId).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Count(c => c.SupersededAt == null).Should().Be(1, "at most one open reservation per (promotion, user) and per (promotion, org)");
        rows.Single(c => c.SupersededAt == null).ProviderTransactionId.Should().Be("txn_2");
    }

    [Fact]
    public async Task N01_a_retried_checkout_with_a_once_per_org_code_supersedes_the_first()
    {
        Seed(PromotionKind.PlanDiscount, p => p.Code = "ONCEONLY");

        StatusOf(await PlanCheckout(code: "ONCEONLY")).Should().Be(200);
        IActionResult second = await PlanCheckout(code: "ONCEONLY");

        StatusOf(second).Should().Be(200);
        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan");
    }

    // ------------------------------------------------------------------ F-5: reservations cannot exhaust a capped promotion

    [Fact]
    public async Task F5_N_unpaid_checkouts_from_one_user_cannot_exhaust_a_capped_promotion_for_another_user()
    {
        Seed(PromotionKind.PlanDiscount, p => p.MaxRedemptions = 2);
        Guid farm1 = AddOrg("farm1");
        Guid farm2 = AddOrg("farm2");
        foreach (Guid org in new[] { Org, farm1, farm2, Org, farm1, farm2 })
        {
            StatusOf(await PlanCheckout(org: org)).Should().Be(200);
        }

        (Guid buyer, Guid buyerOrg) = OtherBuyer("buyer");
        StatusOf(await PlanCheckout(buyer, buyerOrg)).Should().Be(200);

        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan", "one user holds at most one open reservation, whatever the number of orgs");
    }

    [Fact]
    public async Task F5_an_open_checkout_by_another_buyer_still_reserves_the_global_cap()
    {
        Seed(PromotionKind.PlanDiscount, p => { p.MaxRedemptions = 1; p.MaxRedemptionsPerOrg = 5; });
        (Guid buyer, Guid buyerOrg) = OtherBuyer("buyer");

        await PlanCheckout(buyer, buyerOrg);
        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan");
        await PlanCheckout();

        Provider.LastCheckout!.DiscountId.Should().BeNull();
    }

    [Fact]
    public async Task F5_a_stale_reservation_frees_up_after_the_configured_lifetime()
    {
        Seed(PromotionKind.PlanDiscount, p => p.MaxRedemptions = 1);
        (Guid buyer, Guid buyerOrg) = OtherBuyer("buyer");
        await PlanCheckout(buyer, buyerOrg);

        Clock.Advance(TimeSpan.FromMinutes(PromoOptions.CheckoutReservationMinutes - 1));
        await PlanCheckout();
        Provider.LastCheckout!.DiscountId.Should().BeNull("the other buyer's checkout is still open");

        Clock.Advance(TimeSpan.FromMinutes(2));
        await PlanCheckout();
        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan", "a reservation older than the checkout lifetime never counts");
    }

    [Fact]
    public void F5_the_reservation_lifetime_defaults_to_sixty_minutes()
        => new PromotionOptions().CheckoutReservationMinutes.Should().Be(60);

    [Fact]
    public async Task F5_a_second_org_owned_by_the_same_user_gets_no_second_discount_use()
    {
        Seed(PromotionKind.PlanDiscount);
        IActionResult first = await PlanCheckout();
        string txn = JsonOf(first).GetProperty("transactionId").GetString()!;
        await Process(PlanPaid("evt_1", txn, "sub_1"));

        Guid farm = AddOrg("farm");
        StatusOf(await PlanCheckout(org: farm)).Should().Be(200);

        Provider.LastCheckout!.DiscountId.Should().BeNull("the once-per-org discount is once per user as well");
        await using InferenceApiDbContext db = NewDb();
        (await db.PromotionRedemptions.SingleAsync()).UserId.Should().Be(OwnerId, "the discount redemption carries the buyer from the checkout record");
    }

    [Fact]
    public async Task F5_a_second_org_owned_by_the_same_user_gets_no_second_bonus()
    {
        Guid bonus = Seed(PromotionKind.PackBonus);
        await using (InferenceApiDbContext db = NewDb())
        {
            db.PromotionRedemptions.Add(new PromotionRedemption { Id = Guid.NewGuid(), PromotionId = bonus, OrganizationId = Org, UserId = OwnerId, Kind = PromotionRedemptionKind.BonusGranted, ProviderTransactionId = "txn_old", CreatedAt = Now });
            await db.SaveChangesAsync();
        }

        Guid farm = AddOrg("farm");
        IActionResult preview = await BillingApi(orgId: farm).Preview(new CheckoutPreviewRequestDto(null, null, 500, null), default);

        StatusOf(preview).Should().Be(200);
        JsonOf(preview).GetProperty("bonusCredits").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task F5_an_unverified_account_gets_no_automatic_promotion()
    {
        Seed(PromotionKind.PlanDiscount);
        Guid unverified = AddUser("new@acme.test", verified: false);

        StatusOf(await PlanCheckout(unverified)).Should().Be(403, "checkout itself now refuses an unverified account");

        Provider.LastCheckout.Should().BeNull();
        await using InferenceApiDbContext db = NewDb();
        (await db.PromotionCheckouts.CountAsync()).Should().Be(0, "an unverified account reserves nothing");
    }

    [Fact]
    public async Task F5_an_explicit_code_from_an_unverified_account_is_403_EMAIL_NOT_VERIFIED()
    {
        Seed(PromotionKind.PlanDiscount, p => p.Code = "SPRING");
        Guid unverified = AddUser("new@acme.test", verified: false);

        IActionResult result = await PlanCheckout(unverified, code: "SPRING");

        StatusOf(result).Should().Be(403);
        JsonOf(result).GetProperty("code").GetString().Should().Be("EMAIL_NOT_VERIFIED");
        Provider.LastCheckout.Should().BeNull();
    }

    [Fact]
    public async Task F5_redeeming_a_credit_code_from_an_unverified_account_is_403_EMAIL_NOT_VERIFIED()
    {
        Seed(PromotionKind.CreditCode);
        Guid unverified = AddUser("new@acme.test", verified: false);

        IActionResult result = await Promotions(unverified).Redeem(new RedeemPromotionDto("WELCOME50"), default);

        StatusOf(result).Should().Be(403);
        JsonOf(result).GetProperty("code").GetString().Should().Be("EMAIL_NOT_VERIFIED");
        (await PromoGrantedMillisAsync()).Should().Be(0);
    }

    [Fact]
    public async Task F5_promo_carrying_checkouts_are_rate_limited_per_user_per_hour()
    {
        PromoOptions.CheckoutsPerUserPerHour = 2;
        StatusOf(await PlanCheckout()).Should().Be(200);
        StatusOf(await PlanCheckout()).Should().Be(200);
        StatusOf(await PlanCheckout()).Should().Be(200);
        Counters.Counts.Should().BeEmpty("a checkout that carries no promotion is not counted");

        Seed(PromotionKind.PlanDiscount);
        StatusOf(await PlanCheckout()).Should().Be(200);
        StatusOf(await PlanCheckout()).Should().Be(200);
        IActionResult third = await PlanCheckout();

        StatusOf(third).Should().Be(429);
        ErrorCode(third).Should().Be("promo_checkout_rate_limited");
        Counters.Counts.Keys.Select(k => k.Bucket).Should().AllBe($"promo:checkout:user:{OwnerId}");
    }

    /// <summary>Moves the clock to the next whole hour, so the sliding window's elapsed fraction is known.</summary>
    private DateTime AlignToHour()
    {
        DateTime next = new(Now.Ticks - (Now.Ticks % TimeSpan.TicksPerHour) + TimeSpan.TicksPerHour, DateTimeKind.Utc);
        Clock.Advance(next - Now);
        return next;
    }

    [Fact]
    public async Task F5_the_eleventh_promo_checkout_within_an_hour_is_429_and_refused_attempts_do_not_count()
    {
        Seed(PromotionKind.PlanDiscount);
        DateTime hour = AlignToHour();

        for (int i = 0; i < 10; i++)
        {
            StatusOf(await PlanCheckout()).Should().Be(200, $"checkout {i + 1} of 10 is within Billing:Promotions:CheckoutsPerUserPerHour");
        }

        for (int i = 0; i < 5; i++)
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            IActionResult refused = await PlanCheckout();
            StatusOf(refused).Should().Be(429);
            ErrorCode(refused).Should().Be("promo_checkout_rate_limited");
        }

        Counters.Counts[($"promo:checkout:user:{OwnerId}", hour)].Should().Be(10, "a refused attempt increments nothing (C5)");
    }

    [Fact]
    public async Task F5_the_promo_checkout_limit_slides_over_the_previous_hour_counting_only_allowed_checkouts()
    {
        Seed(PromotionKind.PlanDiscount);
        AlignToHour();
        for (int i = 0; i < 10; i++)
        {
            StatusOf(await PlanCheckout()).Should().Be(200);
        }

        for (int i = 0; i < 5; i++)
        {
            StatusOf(await PlanCheckout()).Should().Be(429);
        }

        // Halfway through the next hour the previous hour weighs 10 x 0.5 = 5 (the five refusals weigh
        // nothing), so exactly five more fit; a fixed window would admit ten.
        Clock.Advance(TimeSpan.FromMinutes(90));
        for (int i = 0; i < 5; i++)
        {
            StatusOf(await PlanCheckout()).Should().Be(200, $"checkout {i + 1} of 5 fits the sliding estimate");
        }

        IActionResult sixth = await PlanCheckout();
        StatusOf(sixth).Should().Be(429);
        ErrorCode(sixth).Should().Be("promo_checkout_rate_limited");
    }

    [Fact]
    public async Task F5_a_provider_failure_releases_the_reservation()
    {
        Seed(PromotionKind.PlanDiscount, p => p.MaxRedemptions = 1);
        Provider.CheckoutFailure = new HttpRequestException("422");

        StatusOf(await PlanCheckout()).Should().Be(502);

        Provider.CheckoutFailure = null;
        (Guid buyer, Guid buyerOrg) = OtherBuyer("buyer");
        await PlanCheckout(buyer, buyerOrg);
        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan");
    }

    // ------------------------------------------------------------------ N-03 / N-11: a reserved, paid checkout is honoured and flagged

    [Fact]
    public async Task N11_a_reserved_bonus_paid_after_the_cap_was_reached_is_granted_with_overshoot()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => { p.MaxRedemptions = 1; p.RedemptionCount = 1; });
        // C10: a live reservation (an expired one is refused, see PromotionRound4Tests).
        AddCheckout("txn_a", bonus: bonus);

        await Process(PackPaid("evt_a", "txn_a"));

        (await PromoGrantedMillisAsync()).Should().Be(100_000, "the buyer was promised the bonus and paid");
        await using InferenceApiDbContext db = NewDb();
        PromotionRedemption row = await db.PromotionRedemptions.SingleAsync();
        row.Overshoot.Should().BeTrue();
        (await db.Promotions.SingleAsync(p => p.Id == bonus)).RedemptionCount.Should().Be(2);
    }

    [Fact]
    public async Task N11_a_bonus_within_the_caps_is_not_flagged()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => p.MaxRedemptions = 5);
        AddCheckout("txn_a", bonus: bonus);

        await Process(PackPaid("evt_a", "txn_a"));

        await using InferenceApiDbContext db = NewDb();
        (await db.PromotionRedemptions.SingleAsync()).Overshoot.Should().BeFalse();
    }

    [Fact]
    public async Task N11_a_superseded_checkout_paid_over_the_cap_grants_nothing()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => { p.MaxRedemptions = 1; p.RedemptionCount = 1; });
        AddCheckout("txn_a", bonus: bonus, supersededAt: Now);

        await Process(PackPaid("evt_a", "txn_a"));

        (await PromoGrantedMillisAsync()).Should().Be(0);
        (await BalanceMillisAsync()).Should().Be(500_000);
    }

    [Fact]
    public async Task N11_paying_one_checkout_supersedes_the_buyers_other_open_ones()
    {
        Guid bonus = Seed(PromotionKind.PackBonus);
        AddCheckout("txn_a", bonus: bonus);
        AddCheckout("txn_b", bonus: bonus);

        await Process(PackPaid("evt_a", "txn_a"));
        await Process(PackPaid("evt_b", "txn_b"));

        (await PromoGrantedMillisAsync()).Should().Be(100_000);
        await using InferenceApiDbContext db = NewDb();
        (await db.PromotionCheckouts.SingleAsync(c => c.ProviderTransactionId == "txn_b")).SupersededAt.Should().NotBeNull();
    }

    [Fact]
    public async Task N03_a_reserved_discount_paid_after_the_cap_was_reached_is_recorded_with_overshoot()
    {
        // C10: only a live reservation within every cap is an overshoot; an unreserved payment is NotGranted.
        Guid discount = Seed(PromotionKind.PlanDiscount, p => { p.MaxRedemptions = 1; p.RedemptionCount = 1; });
        AddCheckout("txn_late", discount: discount);

        await Process(PlanPaid("evt_1", "txn_late", "sub_late"));

        await using InferenceApiDbContext db = NewDb();
        PromotionRedemption row = await db.PromotionRedemptions.SingleAsync();
        row.Overshoot.Should().BeTrue();
        (await db.Promotions.SingleAsync(p => p.Id == discount)).RedemptionCount.Should().Be(2);
    }

    [Fact]
    public async Task N03_the_admin_redemption_list_exposes_overshoot()
    {
        Guid id = Seed(PromotionKind.PlanDiscount);
        await using (InferenceApiDbContext db = NewDb())
        {
            db.PromotionRedemptions.Add(new PromotionRedemption { Id = Guid.NewGuid(), PromotionId = id, OrganizationId = Org, Kind = PromotionRedemptionKind.DiscountApplied, ProviderTransactionId = "txn_1", Overshoot = true, CreatedAt = Now });
            await db.SaveChangesAsync();
        }

        JsonElement item = JsonOf(await Admin().Redemptions(id, default)).GetProperty("items")[0];

        item.GetProperty("overshoot").GetBoolean().Should().BeTrue();
    }

    // ------------------------------------------------------------------ S-11: one discount row per (promotion, subscription)

    [Fact]
    public async Task S11_a_second_discount_row_for_the_same_subscription_violates_the_unique_index()
    {
        Guid id = Seed(PromotionKind.PlanDiscount);
        await using InferenceApiDbContext db = NewDb();
        db.PromotionRedemptions.AddRange(
            new PromotionRedemption { Id = Guid.NewGuid(), PromotionId = id, OrganizationId = Org, Kind = PromotionRedemptionKind.DiscountApplied, ProviderTransactionId = "txn_1", ProviderSubscriptionId = "sub_1", CreatedAt = Now },
            new PromotionRedemption { Id = Guid.NewGuid(), PromotionId = id, OrganizationId = Org, Kind = PromotionRedemptionKind.DiscountApplied, ProviderTransactionId = "txn_2", ProviderSubscriptionId = "sub_1", CreatedAt = Now });

        Func<Task> save = () => db.SaveChangesAsync();

        await save.Should().ThrowAsync<DbUpdateException>();
    }

    // ------------------------------------------------------------------ N-02: clawback once per transaction

    [Fact]
    public async Task N02_a_refund_then_a_chargeback_of_the_same_top_up_claws_back_once()
    {
        Guid bonus = Seed(PromotionKind.PackBonus);
        AddCheckout("txn_a", bonus: bonus);
        await Process(PackPaid("evt_1", "txn_a"));
        (await BalanceMillisAsync()).Should().Be(600_000);

        await Process(Refund("evt_refund", "txn_a", "adj_refund"));
        (await BalanceMillisAsync()).Should().Be(0);
        await Process(Refund("evt_chargeback", "txn_a", "adj_chargeback"));

        (await BalanceMillisAsync()).Should().Be(0, "the chargeback of an already refunded top-up takes nothing more");
    }
}
