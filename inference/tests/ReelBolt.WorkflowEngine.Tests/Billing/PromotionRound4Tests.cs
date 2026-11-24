#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// saas-promotions T-031 (fix round 4, contract C10): superseded and expired reservations cancel their
/// provider transaction (F-5); overshoot only for a live reservation that was within every cap, once per
/// user and org, under a global bound (F-8); every refused grant or count is a NotGranted row (N-11).
/// The concurrent versions are in <see cref="PromotionPostgresConcurrencyTests"/>.
/// </summary>
public sealed class PromotionRound4Tests : PromotionTestBase
{
    private readonly RecordingLogger<BillingEventProcessor> _webhookLog = new();

    private async Task<BillingEventOutcome> Process(BillingEvent evt)
    {
        await using InferenceApiDbContext db = NewDb();
        return await new BillingEventProcessor(db, _webhookLog, Clock, Options.Create(PromoOptions)).ProcessAsync(evt, "{}");
    }

    private BillingEvent PackPaid(string txn, Guid? org = null) => new()
    {
        Provider = "fake",
        EventId = "evt_" + Guid.NewGuid().ToString("N"),
        EventType = "transaction.completed",
        OccurredAt = Now,
        Kind = BillingEventKind.TopUpPaid,
        OrganizationId = org ?? Org,
        TransactionId = txn,
        TopUpCredits = 500,
        Items = [new BillingLineItem("pri_topup_500", 1)],
    };

    private BillingEvent PlanPaid(string txn, string sub, Guid? org = null) => new()
    {
        Provider = "fake",
        EventId = "evt_" + Guid.NewGuid().ToString("N"),
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

    private void AddCheckout(string txn, Guid? bonus = null, Guid? discount = null, Guid? org = null, Guid? user = null,
        DateTime? createdAt = null, DateTime? supersededAt = null, bool withinCaps = true, string price = "pri_topup_500")
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
            PriceId = price,
            TopUpCredits = 500,
            CreatedAt = createdAt ?? Now,
            SupersededAt = supersededAt,
            WithinCapsAtReservation = withinCaps,
        });
        db.SaveChanges();
    }

    private void AddRedemption(Guid promotion, Guid org, Guid? user, PromotionRedemptionKind kind, bool overshoot = false,
        PromotionRedemptionOutcome outcome = PromotionRedemptionOutcome.Granted, PromotionNotGrantedReason? reason = null)
    {
        using InferenceApiDbContext db = NewDb();
        db.PromotionRedemptions.Add(new PromotionRedemption
        {
            Id = Guid.NewGuid(),
            PromotionId = promotion,
            OrganizationId = org,
            UserId = user,
            Kind = kind,
            ProviderTransactionId = "txn_prior_" + Guid.NewGuid().ToString("N")[..8],
            Overshoot = overshoot,
            Outcome = outcome,
            NotGrantedReason = reason,
            CreatedAt = Now,
        });
        db.SaveChanges();
    }

    private (Guid User, Guid OrgId) OtherBuyer(string slug)
    {
        Guid user = VerifiedUser();
        using InferenceApiDbContext db = NewDb();
        Guid id = Guid.NewGuid();
        db.Organizations.Add(new Organization { Id = id, Name = slug, Slug = slug, Kind = OrganizationKind.Team });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = id, UserId = user, Role = OrganizationRole.Owner });
        db.SaveChanges();
        return (user, id);
    }

    private Task<IActionResult> PlanCheckout(Guid? user = null, Guid? org = null)
        => BillingApi(userId: user, orgId: org).Checkout(new CheckoutRequestDto("creator", null, null), default);

    private Task<IActionResult> PackCheckout() => BillingApi().Checkout(new CheckoutRequestDto(null, null, 500), default);

    private PromotionCheckoutSweeper Sweeper()
    {
        ServiceCollection services = new();
        services.AddScoped(_ => NewDb());
        services.AddSingleton<IBillingProvider>(Provider);
        ServiceProvider sp = services.BuildServiceProvider();
        return new PromotionCheckoutSweeper(sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(PromoOptions), NullLogger<PromotionCheckoutSweeper>.Instance, Clock);
    }

    private async Task<PromotionCheckout> CheckoutRowAsync(string txn)
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.PromotionCheckouts.AsNoTracking().SingleAsync(c => c.ProviderTransactionId == txn);
    }

    private async Task<PromotionRedemption[]> RowsAsync(Guid promotion)
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.PromotionRedemptions.AsNoTracking().Where(r => r.PromotionId == promotion).ToArrayAsync();
    }

    private async Task<long> PromoGrantedMillisAsync(Guid? org = null)
    {
        await using InferenceApiDbContext db = NewDb();
        Guid o = org ?? Org;
        return (await db.CreditGrants.Where(g => g.OrganizationId == o && g.Source == CreditGrantSource.Promo).Select(g => g.AmountMillis).ToListAsync()).Sum();
    }

    private async Task<int> RedemptionCountAsync(Guid promotion)
    {
        await using InferenceApiDbContext db = NewDb();
        return (await db.Promotions.AsNoTracking().SingleAsync(p => p.Id == promotion)).RedemptionCount;
    }

    private void AssertOneRefusalWarning(PromotionNotGrantedReason reason)
        => _webhookLog.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains(reason.ToString(), StringComparison.Ordinal))
            .Should().ContainSingle().Which.Message.Should().NotContain("@");

    // ------------------------------------------------------------------ F-5: superseded and expired transactions are cancelled

    [Fact]
    public async Task Superseding_a_checkout_cancels_its_provider_transaction()
    {
        Seed(PromotionKind.PlanDiscount);

        await PlanCheckout();
        Provider.CancelCalls.Should().BeEmpty();
        StatusOf(await PlanCheckout()).Should().Be(200);

        Provider.CancelCalls.Should().Equal("txn_1");
        PromotionCheckout old = await CheckoutRowAsync("txn_1");
        old.ProviderCancelledAt.Should().NotBeNull();
        old.ProviderCancelAttempts.Should().Be(1);
        (await CheckoutRowAsync("txn_2")).ProviderCancelledAt.Should().BeNull("the live reservation keeps its transaction");
    }

    [Fact]
    public async Task A_failed_cancel_never_blocks_the_new_checkout_and_the_sweeper_retries_it()
    {
        Seed(PromotionKind.PlanDiscount);
        await PlanCheckout();
        Provider.CancelFailureOnce = new HttpRequestException("Paddle 503", null, System.Net.HttpStatusCode.ServiceUnavailable);

        StatusOf(await PlanCheckout()).Should().Be(200);

        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan");
        PromotionCheckout owed = await CheckoutRowAsync("txn_1");
        owed.ProviderCancelledAt.Should().BeNull();
        owed.ProviderCancelAttempts.Should().Be(1);

        // N-17: retried once its backoff has passed, not on the very next sweep.
        Clock.Advance(PromotionCheckoutCancellation.FirstBackoff);
        (await Sweeper().SweepOnceAsync(Now, default)).Should().Be(1);

        Provider.CancelCalls.Should().Equal("txn_1", "txn_1");
        (await CheckoutRowAsync("txn_1")).ProviderCancelledAt.Should().NotBeNull();
        (await CheckoutRowAsync("txn_1")).ProviderCancelAttempts.Should().Be(2);
    }

    [Fact]
    public async Task The_sweeper_cancels_expired_reservations_and_retries_after_a_failure()
    {
        Seed(PromotionKind.PlanDiscount);
        await PlanCheckout();
        Clock.Advance(TimeSpan.FromMinutes(PromoOptions.CheckoutReservationMinutes + 1));
        (Guid buyer, Guid buyerOrg) = OtherBuyer("fresh");
        await PlanCheckout(buyer, buyerOrg);
        AddCheckout("txn_paid", discount: Guid.NewGuid(), createdAt: Now.AddHours(-5));
        await using (InferenceApiDbContext db = NewDb())
        {
            await db.PromotionCheckouts.Where(c => c.ProviderTransactionId == "txn_paid").ExecuteUpdateAsync(s => s.SetProperty(c => c.CompletedAt, Now));
        }

        Provider.CancelFailureOnce = new HttpRequestException("timeout");
        (await Sweeper().SweepOnceAsync(Now, default)).Should().Be(0);
        (await CheckoutRowAsync("txn_1")).ProviderCancelAttempts.Should().Be(1);
        (await CheckoutRowAsync("txn_1")).ProviderCancelledAt.Should().BeNull();

        Clock.Advance(PromotionCheckoutCancellation.FirstBackoff);
        (await Sweeper().SweepOnceAsync(Now, default)).Should().Be(1);

        Provider.CancelCalls.Should().Equal("txn_1", "txn_1");
        (await CheckoutRowAsync("txn_1")).ProviderCancelledAt.Should().NotBeNull();
        (await CheckoutRowAsync("txn_2")).ProviderCancelledAt.Should().BeNull("a live reservation is not swept");
        (await CheckoutRowAsync("txn_paid")).ProviderCancelAttempts.Should().Be(0, "a paid checkout is never cancelled");
    }

    [Fact]
    public async Task A_transaction_the_provider_will_not_cancel_is_left_alone_and_never_retried()
    {
        Seed(PromotionKind.PlanDiscount);
        await PlanCheckout();
        Provider.CancelResult = ProviderTransactionCancel.NotCancellable;
        await PlanCheckout();

        (await CheckoutRowAsync("txn_1")).ProviderCancelledAt.Should().NotBeNull();
        (await Sweeper().SweepOnceAsync(Now, default)).Should().Be(0);
        Provider.CancelCalls.Where(t => t == "txn_1").Should().HaveCount(1);
    }

    // ------------------------------------------------------------------ F-8: bounded, once-only overshoot

    [Fact]
    public async Task A_reservation_records_that_it_was_within_every_cap()
    {
        Seed(PromotionKind.PlanDiscount, p => p.MaxRedemptions = 3);

        await PlanCheckout();

        (await CheckoutRowAsync("txn_1")).WithinCapsAtReservation.Should().BeTrue();
    }

    [Fact]
    public async Task F8_4_one_user_paying_five_checkouts_of_a_per_user_cap_1_bonus_gets_exactly_one_bonus()
    {
        await SubscribeAsync();
        Guid bonus = Seed(PromotionKind.PackBonus, p => p.MaxRedemptionsPerOrg = 1);
        for (int i = 0; i < 5; i++)
        {
            StatusOf(await PackCheckout()).Should().Be(200);
        }

        // The live one last, so four superseded checkouts are paid first.
        foreach (int n in new[] { 1, 2, 3, 4, 5 })
        {
            await Process(PackPaid($"txn_{n}"));
        }

        (await PromoGrantedMillisAsync()).Should().Be(100_000, "exactly one 20 % bonus on a 500-credit pack");
        PromotionRedemption[] rows = await RowsAsync(bonus);
        rows.Count(r => r.Outcome == PromotionRedemptionOutcome.Granted).Should().Be(1);
        rows.Where(r => r.Outcome == PromotionRedemptionOutcome.NotGranted).Should().HaveCount(4)
            .And.OnlyContain(r => r.NotGrantedReason == PromotionNotGrantedReason.Superseded && r.LedgerEntryId == null);
        (await RedemptionCountAsync(bonus)).Should().Be(1);
    }

    [Fact]
    public async Task Two_users_paying_for_the_last_global_use_get_at_most_cap_plus_one_with_overshoot()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => p.MaxRedemptions = 1);
        (Guid user, Guid org) = OtherBuyer("second");
        AddCheckout("txn_a", bonus: bonus);
        AddCheckout("txn_b", bonus: bonus, org: org, user: user);

        await Process(PackPaid("txn_a"));
        await Process(PackPaid("txn_b", org));

        PromotionRedemption[] rows = await RowsAsync(bonus);
        rows.Should().HaveCount(2).And.OnlyContain(r => r.Outcome == PromotionRedemptionOutcome.Granted);
        rows.Single(r => r.ProviderTransactionId == "txn_b").Overshoot.Should().BeTrue();
        (await RedemptionCountAsync(bonus)).Should().Be(2);
    }

    [Fact]
    public async Task Beyond_the_global_overshoot_bound_a_reserved_payment_is_a_not_granted_row_with_one_warning()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => { p.MaxRedemptions = 1; p.RedemptionCount = 2; });
        (Guid earlier, Guid earlierOrg) = OtherBuyer("earlier");
        AddRedemption(bonus, earlierOrg, earlier, PromotionRedemptionKind.BonusGranted);
        AddRedemption(bonus, earlierOrg, earlier, PromotionRedemptionKind.BonusGranted, overshoot: true);
        AddCheckout("txn_a", bonus: bonus);

        await Process(PackPaid("txn_a"));

        (await PromoGrantedMillisAsync()).Should().Be(0);
        PromotionRedemption row = (await RowsAsync(bonus)).Single(r => r.ProviderTransactionId == "txn_a");
        row.Outcome.Should().Be(PromotionRedemptionOutcome.NotGranted);
        row.NotGrantedReason.Should().Be(PromotionNotGrantedReason.OvershootBoundReached);
        row.Overshoot.Should().BeFalse();
        row.UserId.Should().Be(OwnerId);
        (await RedemptionCountAsync(bonus)).Should().Be(2, "a refused grant is not counted as a use");
        AssertOneRefusalWarning(PromotionNotGrantedReason.OvershootBoundReached);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(99, 1)]
    [InlineData(100, 1)]
    [InlineData(250, 2)]
    [InlineData(500, 5)]
    [InlineData(100_000, 5)]
    [InlineData(null, 5)]
    public void The_global_overshoot_bound_is_min_5_and_1_percent_but_at_least_1(int? max, int bound)
        => BillingEventProcessor.OvershootBound(max).Should().Be(bound);

    [Fact]
    public async Task An_expired_unsuperseded_reservation_paid_at_the_cap_is_not_granted()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => { p.MaxRedemptions = 1; p.RedemptionCount = 1; });
        AddCheckout("txn_a", bonus: bonus, createdAt: Now.AddMinutes(-(PromoOptions.CheckoutReservationMinutes + 1)));

        await Process(PackPaid("txn_a"));

        (await PromoGrantedMillisAsync()).Should().Be(0, "its expiry freed the slot for another buyer (F-8)");
        PromotionRedemption row = (await RowsAsync(bonus)).Single();
        (row.Outcome, row.NotGrantedReason).Should().Be((PromotionRedemptionOutcome.NotGranted, PromotionNotGrantedReason.Expired));
        row.CreditsMillis.Should().Be(100_000, "support sees what the buyer was promised");
        AssertOneRefusalWarning(PromotionNotGrantedReason.Expired);
    }

    [Fact]
    public async Task A_superseded_reservation_paid_at_the_cap_is_a_not_granted_row_in_the_admin_list()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => { p.MaxRedemptions = 1; p.RedemptionCount = 1; });
        AddCheckout("txn_a", bonus: bonus, supersededAt: Now);

        await Process(PackPaid("txn_a"));

        JsonElement item = JsonOf(await Admin().Redemptions(bonus, default)).GetProperty("items")[0];
        item.GetProperty("outcome").GetString().Should().Be("notGranted");
        item.GetProperty("notGrantedReason").GetString().Should().Be("superseded");
        item.GetProperty("overshoot").GetBoolean().Should().BeFalse();
        AssertOneRefusalWarning(PromotionNotGrantedReason.Superseded);
    }

    [Fact]
    public async Task A_reservation_not_within_every_cap_when_created_is_never_honoured_past_a_cap()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => { p.MaxRedemptions = 1; p.RedemptionCount = 1; });
        AddCheckout("txn_a", bonus: bonus, withinCaps: false);

        await Process(PackPaid("txn_a"));

        (await PromoGrantedMillisAsync()).Should().Be(0);
        (await RowsAsync(bonus)).Single().NotGrantedReason.Should().Be(PromotionNotGrantedReason.ReservedOverCap);
    }

    [Fact]
    public async Task Overshoot_is_granted_at_most_once_per_user()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => { p.MaxRedemptions = 1000; p.RedemptionCount = 1000; });
        (_, Guid secondOrg) = OtherBuyer("second-org");
        AddRedemption(bonus, secondOrg, OwnerId, PromotionRedemptionKind.BonusGranted, overshoot: true);
        AddCheckout("txn_a", bonus: bonus);

        await Process(PackPaid("txn_a"));

        (await RowsAsync(bonus)).Single(r => r.ProviderTransactionId == "txn_a").NotGrantedReason.Should().Be(PromotionNotGrantedReason.UserCapReached);
    }

    [Fact]
    public async Task Overshoot_is_granted_at_most_once_per_org()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => { p.MaxRedemptions = 1000; p.RedemptionCount = 1000; });
        Guid member = VerifiedUser();
        AddRedemption(bonus, Org, member, PromotionRedemptionKind.BonusGranted, overshoot: true);
        AddCheckout("txn_a", bonus: bonus);

        await Process(PackPaid("txn_a"));

        (await RowsAsync(bonus)).Single(r => r.ProviderTransactionId == "txn_a").NotGrantedReason.Should().Be(PromotionNotGrantedReason.OrgCapReached);
    }

    [Fact]
    public async Task Not_granted_rows_never_count_against_a_cap()
    {
        await SubscribeAsync();
        Guid bonus = Seed(PromotionKind.PackBonus, p => { p.MaxRedemptionsPerOrg = 1; p.MaxRedemptions = 5; });
        AddRedemption(bonus, Org, OwnerId, PromotionRedemptionKind.BonusGranted, outcome: PromotionRedemptionOutcome.NotGranted, reason: PromotionNotGrantedReason.Superseded);

        StatusOf(await PackCheckout()).Should().Be(200);
        Provider.LastCheckout!.BonusPromotionId.Should().Be(bonus, "a refused row is not a use of the per-user or per-org cap");
        await Process(PackPaid("txn_1"));

        PromotionRedemption granted = (await RowsAsync(bonus)).Single(r => r.ProviderTransactionId == "txn_1");
        (granted.Outcome, granted.Overshoot).Should().Be((PromotionRedemptionOutcome.Granted, false));
        JsonOf(await Admin().Get(bonus, default)).GetProperty("redemptions").GetInt32().Should().Be(1, "the admin count shows uses only");
    }

    // ------------------------------------------------------------------ discount analogues (F-5 residual)

    [Fact]
    public async Task A_discount_paid_on_an_expired_reservation_at_the_cap_is_not_counted()
    {
        Guid discount = Seed(PromotionKind.PlanDiscount, p => { p.MaxRedemptions = 1; p.RedemptionCount = 1; });
        AddCheckout("txn_a", discount: discount, price: "pri_creator", createdAt: Now.AddHours(-2));

        await Process(PlanPaid("txn_a", "sub_a"));

        PromotionRedemption row = (await RowsAsync(discount)).Single();
        (row.Outcome, row.NotGrantedReason).Should().Be((PromotionRedemptionOutcome.NotGranted, PromotionNotGrantedReason.Expired));
        row.DiscountTotal.Should().Be("580");
        (await RedemptionCountAsync(discount)).Should().Be(1);
    }

    [Fact]
    public async Task A_discount_without_a_reservation_paid_at_the_cap_is_not_counted()
    {
        Guid discount = Seed(PromotionKind.PlanDiscount, p => { p.MaxRedemptions = 1; p.RedemptionCount = 1; });

        await Process(PlanPaid("txn_late", "sub_late"));

        (await RowsAsync(discount)).Single().NotGrantedReason.Should().Be(PromotionNotGrantedReason.NotReserved);
        (await RedemptionCountAsync(discount)).Should().Be(1);
    }

    [Fact]
    public async Task A_live_discount_reservation_paid_at_the_cap_is_an_overshoot_until_the_bound()
    {
        Guid discount = Seed(PromotionKind.PlanDiscount, p => { p.MaxRedemptions = 1; p.RedemptionCount = 1; });
        (Guid user, Guid org) = OtherBuyer("late");
        AddCheckout("txn_a", discount: discount, price: "pri_creator");
        AddCheckout("txn_b", discount: discount, price: "pri_creator", org: org, user: user);

        await Process(PlanPaid("txn_a", "sub_a"));
        await Process(PlanPaid("txn_b", "sub_b", org));

        PromotionRedemption[] rows = await RowsAsync(discount);
        rows.Single(r => r.ProviderTransactionId == "txn_a").Overshoot.Should().BeTrue();
        rows.Single(r => r.ProviderTransactionId == "txn_b").NotGrantedReason.Should().Be(PromotionNotGrantedReason.OvershootBoundReached);
        (await RedemptionCountAsync(discount)).Should().Be(2);
    }

    // ------------------------------------------------------------------ T-034 F-5: a reservation superseded while its checkout is created

    [Fact]
    public async Task F5_superseded_and_cancel_ran_before_the_attach_answers_409_and_cancels_its_transaction()
    {
        Seed(PromotionKind.PlanDiscount);
        IActionResult? second = null;
        // Request 2 runs start to finish while request 1 waits on the provider: it supersedes request 1's
        // reservation (no transaction id yet), so its supersede-time cancel has nothing to cancel.
        Provider.BeforeCheckoutReturns = async n =>
        {
            if (n == 1)
            {
                second = await PlanCheckout();
            }
        };

        IActionResult first = await PlanCheckout();

        StatusOf(second!).Should().Be(200);
        StatusOf(first).Should().Be(409, "a superseded reservation must not hand out a payable discounted checkout");
        ErrorCode(first).Should().Be("promo_checkout_superseded");
        JsonOf(first).TryGetProperty("checkoutUrl", out _).Should().BeFalse();
        Provider.CancelCalls.Should().Equal("txn_1");
        PromotionCheckout loser = await CheckoutRowAsync("txn_1");
        loser.SupersededAt.Should().NotBeNull();
        loser.ProviderCancelledAt.Should().NotBeNull();
        (await CheckoutRowAsync("txn_2")).SupersededAt.Should().BeNull("the newest checkout keeps its reservation");
    }

    [Fact]
    public async Task F5_superseded_before_the_attach_but_cancelled_after_it_still_answers_409()
    {
        Seed(PromotionKind.PlanDiscount);
        TaskCompletionSource secondCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IActionResult>? second = null;
        Provider.BeforeCheckoutReturns = async n =>
        {
            if (n == 1)
            {
                // Request 2 reserves (superseding request 1) and gets its transaction, then waits.
                second = Task.Run(() => PlanCheckout());
                await secondCreated.Task;
            }
            else
            {
                secondCreated.SetResult();
                await firstDone.Task;
            }
        };

        IActionResult first = await PlanCheckout();
        firstDone.SetResult();
        IActionResult secondResult = await second!;

        StatusOf(secondResult).Should().Be(200);
        StatusOf(first).Should().Be(409);
        ErrorCode(first).Should().Be("promo_checkout_superseded");
        (await CheckoutRowAsync("txn_1")).ProviderCancelledAt.Should().NotBeNull();
        Provider.CancelCalls.Should().OnlyContain(t => t == "txn_1").And.NotBeEmpty();
    }

    [Fact]
    public async Task F5_a_failed_immediate_cancel_of_a_superseded_attach_is_left_to_the_sweeper()
    {
        Seed(PromotionKind.PlanDiscount);
        Provider.BeforeCheckoutReturns = async n =>
        {
            if (n == 1)
            {
                await PlanCheckout();
                Provider.CancelFailureOnce = new HttpRequestException("Paddle 503", null, HttpStatusCode.ServiceUnavailable);
            }
        };

        StatusOf(await PlanCheckout()).Should().Be(409);

        PromotionCheckout owed = await CheckoutRowAsync("txn_1");
        owed.ProviderCancelledAt.Should().BeNull();
        owed.ProviderCancelAttempts.Should().Be(1);
        Clock.Advance(PromotionCheckoutCancellation.FirstBackoff);
        (await Sweeper().SweepOnceAsync(Now, default)).Should().Be(1, "the transaction id was recorded, so the sweeper owes its cancel");
        Provider.CancelCalls.Should().Equal("txn_1", "txn_1");
    }

    // ------------------------------------------------------------------ T-034 N-17: backoff, attempt cap, no starvation

    private PromotionCheckoutSweeper Sweeper(ILogger<PromotionCheckoutSweeper> log)
    {
        ServiceCollection services = new();
        services.AddScoped(_ => NewDb());
        services.AddSingleton<IBillingProvider>(Provider);
        ServiceProvider sp = services.BuildServiceProvider();
        return new PromotionCheckoutSweeper(sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(PromoOptions), log, Clock);
    }

    private static HttpRequestException Unavailable() => new("Paddle 503", null, HttpStatusCode.ServiceUnavailable);

    [Fact]
    public async Task N17_a_failing_cancel_is_retried_with_exponential_backoff_not_on_every_sweep()
    {
        AddCheckout("txn_s", discount: Guid.NewGuid(), supersededAt: Now);
        Provider.CancelFailureFor = _ => Unavailable();
        PromotionCheckoutSweeper sweeper = Sweeper(NullLogger<PromotionCheckoutSweeper>.Instance);

        await sweeper.SweepOnceAsync(Now, default);
        await sweeper.SweepOnceAsync(Now, default);
        Provider.CancelCalls.Should().HaveCount(1, "a failed row is not retried before its backoff");

        Clock.Advance(TimeSpan.FromMinutes(5));
        await sweeper.SweepOnceAsync(Now, default);
        Provider.CancelCalls.Should().HaveCount(2);

        Clock.Advance(TimeSpan.FromMinutes(5));
        await sweeper.SweepOnceAsync(Now, default);
        Provider.CancelCalls.Should().HaveCount(2, "the second failure doubles the wait to 10 minutes");

        Clock.Advance(TimeSpan.FromMinutes(5));
        await sweeper.SweepOnceAsync(Now, default);
        Provider.CancelCalls.Should().HaveCount(3);
    }

    [Fact]
    public async Task N17_after_the_last_allowed_attempt_one_error_is_logged_and_the_row_is_never_retried()
    {
        AddCheckout("txn_s", discount: Guid.NewGuid(), supersededAt: Now);
        await using (InferenceApiDbContext db = NewDb())
        {
            await db.PromotionCheckouts.ExecuteUpdateAsync(s => s.SetProperty(c => c.ProviderCancelAttempts, 19));
        }

        Provider.CancelFailureFor = _ => Unavailable();
        RecordingLogger<PromotionCheckoutSweeper> log = new();
        PromotionCheckoutSweeper sweeper = Sweeper(log);

        await sweeper.SweepOnceAsync(Now, default);
        for (int i = 0; i < 5; i++)
        {
            Clock.Advance(TimeSpan.FromDays(2));
            await sweeper.SweepOnceAsync(Now, default);
        }

        Provider.CancelCalls.Should().Equal("txn_s");
        (await CheckoutRowAsync("txn_s")).ProviderCancelAttempts.Should().Be(20);
        log.Entries.Where(e => e.Level == LogLevel.Error).Should().ContainSingle()
            .Which.Message.Should().Contain("txn_s");
    }

    [Fact]
    public async Task N17_rows_that_keep_failing_do_not_starve_a_newer_owed_row()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            for (int i = 0; i < 200; i++)
            {
                db.PromotionCheckouts.Add(new PromotionCheckout
                {
                    Id = Guid.NewGuid(),
                    ProviderTransactionId = $"txn_old_{i:D3}",
                    OrganizationId = Org,
                    UserId = OwnerId,
                    PriceId = "pri_creator",
                    CreatedAt = Now.AddHours(-3).AddSeconds(i),
                    SupersededAt = Now.AddHours(-2),
                });
            }

            await db.SaveChangesAsync();
        }

        AddCheckout("txn_new", discount: Guid.NewGuid(), createdAt: Now.AddMinutes(-1), supersededAt: Now);
        Provider.CancelFailureFor = t => t.StartsWith("txn_old_", StringComparison.Ordinal) ? Unavailable() : null;
        PromotionCheckoutSweeper sweeper = Sweeper(NullLogger<PromotionCheckoutSweeper>.Instance);

        await sweeper.SweepOnceAsync(Now, default);
        await sweeper.SweepOnceAsync(Now, default);

        Provider.CancelCalls.Should().Contain("txn_new");
        (await CheckoutRowAsync("txn_new")).ProviderCancelledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task N17_a_rate_limited_cancel_stops_the_rest_of_the_batch()
    {
        AddCheckout("txn_a", discount: Guid.NewGuid(), createdAt: Now.AddMinutes(-10), supersededAt: Now);
        AddCheckout("txn_b", discount: Guid.NewGuid(), createdAt: Now.AddMinutes(-5), supersededAt: Now);
        Provider.CancelFailureFor = t => t == "txn_a" ? new HttpRequestException("Paddle 429", null, HttpStatusCode.TooManyRequests) : null;

        await Sweeper(NullLogger<PromotionCheckoutSweeper>.Instance).SweepOnceAsync(Now, default);

        Provider.CancelCalls.Should().Equal("txn_a");
        (await CheckoutRowAsync("txn_b")).ProviderCancelAttempts.Should().Be(0);
    }

    // ------------------------------------------------------------------ T-034 N-20: overshoot-bound metric

    [Fact]
    public async Task N20_reaching_the_overshoot_bound_increments_the_bound_hit_counter_tagged_with_the_kind()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => { p.MaxRedemptions = 1; p.RedemptionCount = 2; });
        (Guid earlier, Guid earlierOrg) = OtherBuyer("earlier-metric");
        AddRedemption(bonus, earlierOrg, earlier, PromotionRedemptionKind.BonusGranted);
        AddRedemption(bonus, earlierOrg, earlier, PromotionRedemptionKind.BonusGranted, overshoot: true);
        AddCheckout("txn_a", bonus: bonus);

        List<string?> kinds = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "reelbolt.billing.promotion_overshoot_bound_hit")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        string promotionTag = bonus.ToString("D");
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            Dictionary<string, object?> t = [];
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                t[tag.Key] = tag.Value;
            }

            // Only this test's promotion: tests running in parallel cannot match.
            if (t.TryGetValue("promotion_id", out object? id) && (id as string) == promotionTag)
            {
                lock (kinds)
                {
                    kinds.Add(t.TryGetValue("kind", out object? k) ? k as string : null);
                }
            }
        });
        listener.Start();

        await Process(PackPaid("txn_a"));

        kinds.Should().Equal("PackBonus");
        AssertOneRefusalWarning(PromotionNotGrantedReason.OvershootBoundReached);
        _webhookLog.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.EventId.Id == 41_701);
    }
}
