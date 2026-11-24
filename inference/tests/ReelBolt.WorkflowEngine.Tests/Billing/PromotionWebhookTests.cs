#nullable enable
using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>saas-promotions T-003: what <see cref="BillingEventProcessor"/> does with promotion data on webhooks.</summary>
public sealed class PromotionWebhookTests : PromotionTestBase
{
    private async Task<BillingEventOutcome> Process(BillingEvent evt)
    {
        await using InferenceApiDbContext db = NewDb();
        return await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, Clock).ProcessAsync(evt, "{}");
    }

    private BillingEvent TopUp(string eventId, Guid? bonus, string? discountId = null) => new()
    {
        Provider = "fake",
        EventId = eventId,
        EventType = "transaction.completed",
        OccurredAt = Now,
        Kind = BillingEventKind.TopUpPaid,
        OrganizationId = Org,
        TransactionId = "txn_pack",
        TopUpCredits = 500,
        Items = [new BillingLineItem("pri_topup_500", 1)],
        BonusPromotionId = bonus,
        DiscountId = discountId,
        DiscountTotal = discountId is null ? null : "250",
        CurrencyCode = "USD",
    };

    /// <summary>What checkout writes when it attaches a bonus (T-011 S-01: the webhook trusts only this record).</summary>
    private void CheckoutWithBonus(Guid bonus, string reservation = "txn_pack")
    {
        using InferenceApiDbContext db = NewDb();
        db.PromotionCheckouts.Add(new PromotionCheckout
        {
            Id = Guid.NewGuid(),
            ProviderTransactionId = reservation,
            OrganizationId = Org,
            UserId = OwnerId,
            BonusPromotionId = bonus,
            PriceId = "pri_topup_500",
            TopUpCredits = 500,
            CreatedAt = Now,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task The_bonus_is_granted_once_across_a_replay()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => p.BonusPercent = 20);
        CheckoutWithBonus(bonus);

        (await Process(TopUp("evt_1", bonus))).Should().Be(BillingEventOutcome.Applied);
        (await Process(TopUp("evt_1", bonus))).Should().Be(BillingEventOutcome.Duplicate);
        // A differently-numbered event for the same transaction is still one bonus.
        (await Process(TopUp("evt_2", bonus))).Should().Be(BillingEventOutcome.Applied);

        (await BalanceMillisAsync()).Should().Be(500_000 + 100_000);
        await using InferenceApiDbContext db = NewDb();
        CreditGrant promo = await db.CreditGrants.SingleAsync(g => g.Source == CreditGrantSource.Promo);
        promo.AmountMillis.Should().Be(100_000);
        promo.ExpiresAt!.Value.Should().BeCloseTo(Now.AddMonths(12), TimeSpan.FromSeconds(1));
        PromotionRedemption row = await db.PromotionRedemptions.SingleAsync();
        row.Kind.Should().Be(PromotionRedemptionKind.BonusGranted);
        row.ProviderTransactionId.Should().Be("txn_pack");
        row.CreditsMillis.Should().Be(100_000);
        (await db.CreditLedgerEntries.CountAsync(e => e.IdempotencyKey == "topup-bonus:fake:txn_pack")).Should().Be(1);
    }

    [Fact]
    public async Task A_top_up_whose_transaction_id_differs_from_its_checkout_id_still_finds_its_reservation()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => p.BonusPercent = 20);
        // The reservation is stored under the checkout reference (a session id); the payment is a PaymentIntent.
        CheckoutWithBonus(bonus, reservation: "cs_session");

        (await Process(TopUp("evt_1", bonus) with { TransactionId = "pi_payment", CheckoutId = "cs_session" }))
            .Should().Be(BillingEventOutcome.Applied);

        (await BalanceMillisAsync()).Should().Be(500_000 + 100_000);
        await using InferenceApiDbContext db = NewDb();
        (await db.PromotionCheckouts.SingleAsync()).CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task The_bonus_is_honoured_after_the_promotion_ended()
    {
        Guid bonus = Seed(PromotionKind.PackBonus, p => { p.Status = PromotionStatus.Archived; p.EndsAt = Now.AddDays(-1); });
        CheckoutWithBonus(bonus);

        await Process(TopUp("evt_1", bonus));

        (await BalanceMillisAsync()).Should().Be(600_000);
    }

    [Fact]
    public async Task A_bonus_id_naming_another_kind_grants_nothing_extra()
    {
        Guid notBonus = Seed(PromotionKind.PackDiscount);
        CheckoutWithBonus(notBonus);

        await Process(TopUp("evt_1", notBonus));

        (await BalanceMillisAsync()).Should().Be(500_000);
    }

    [Fact]
    public async Task DiscountApplied_is_recorded_for_a_subscription_transaction_once()
    {
        Guid id = Seed(PromotionKind.PlanDiscount);
        BillingEvent evt = new()
        {
            Provider = "fake",
            EventId = "evt_sub",
            EventType = "transaction.completed",
            OccurredAt = Now,
            Kind = BillingEventKind.TransactionCompleted,
            OrganizationId = Org,
            SubscriptionId = "sub_1",
            TransactionId = "txn_sub",
            DiscountId = "dsc_plan",
            DiscountTotal = "580",
            CurrencyCode = "USD",
            PromotionId = id,
        };

        (await Process(evt)).Should().Be(BillingEventOutcome.Applied);
        (await Process(evt with { EventId = "evt_sub_again" })).Should().Be(BillingEventOutcome.Applied);

        await using InferenceApiDbContext db = NewDb();
        PromotionRedemption row = await db.PromotionRedemptions.SingleAsync();
        row.Kind.Should().Be(PromotionRedemptionKind.DiscountApplied);
        row.PromotionId.Should().Be(id);
        row.OrganizationId.Should().Be(Org);
        row.ProviderTransactionId.Should().Be("txn_sub");
        row.DiscountTotal.Should().Be("580");
        row.CurrencyCode.Should().Be("USD");
        (await db.Promotions.SingleAsync()).RedemptionCount.Should().Be(1);
    }

    [Fact]
    public async Task DiscountApplied_is_recorded_for_a_discounted_top_up()
    {
        Guid id = Seed(PromotionKind.PackDiscount);

        await Process(TopUp("evt_1", null, "dsc_pack"));

        await using InferenceApiDbContext db = NewDb();
        PromotionRedemption row = await db.PromotionRedemptions.SingleAsync();
        row.PromotionId.Should().Be(id);
        row.Kind.Should().Be(PromotionRedemptionKind.DiscountApplied);
        row.DiscountTotal.Should().Be("250");
    }

    [Fact]
    public async Task An_unknown_discount_id_records_nothing()
    {
        await Process(TopUp("evt_1", null, "dsc_unknown"));

        await using InferenceApiDbContext db = NewDb();
        (await db.PromotionRedemptions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_full_refund_claws_back_the_bonus_too()
    {
        Guid bonus = Seed(PromotionKind.PackBonus);
        CheckoutWithBonus(bonus);
        await Process(TopUp("evt_1", bonus));
        (await BalanceMillisAsync()).Should().Be(600_000);

        BillingEvent refund = new()
        {
            Provider = "fake",
            EventId = "evt_refund",
            EventType = "adjustment.updated",
            OccurredAt = Now,
            Kind = BillingEventKind.PaymentRefunded,
            OrganizationId = Org,
            TransactionId = "txn_pack",
            AdjustmentId = "adj_1",
            FullRefund = true,
        };
        (await Process(refund)).Should().Be(BillingEventOutcome.Applied);
        (await Process(refund with { EventId = "evt_refund_2" })).Should().Be(BillingEventOutcome.Applied);

        (await BalanceMillisAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_partial_refund_leaves_the_bonus()
    {
        Guid bonus = Seed(PromotionKind.PackBonus);
        CheckoutWithBonus(bonus);
        await Process(TopUp("evt_1", bonus));

        await Process(new BillingEvent
        {
            Provider = "fake",
            EventId = "evt_refund",
            EventType = "adjustment.updated",
            OccurredAt = Now,
            Kind = BillingEventKind.PaymentRefunded,
            OrganizationId = Org,
            TransactionId = "txn_pack",
            AdjustmentId = "adj_1",
            FullRefund = false,
        });

        (await BalanceMillisAsync()).Should().Be(600_000);
    }
}
