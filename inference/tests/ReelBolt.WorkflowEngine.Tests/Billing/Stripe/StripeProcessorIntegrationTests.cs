// Fixtures shaped from docs.stripe.com 2026-10-26, replace with redacted sandbox captures after the operator spike.
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Billing.Stripe.StripePayloads;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

/// <summary>
/// Stripe-shaped webhooks parsed by <see cref="StripeEventParser"/> and fed through the unchanged
/// <see cref="BillingEventProcessor"/> (Sqlite, like <c>BillingEventProcessorTests</c>).
/// </summary>
public sealed class StripeProcessorIntegrationTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public StripeProcessorIntegrationTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = OrgId, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.Plans.AddRange(
            new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, PriceMonthlyUsd = 29, MonthlyCreditsMillis = 1_000_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"stripe":{"monthly":"price_creator"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "pro", Name = "Pro", Kind = PlanKind.Subscription, PriceMonthlyUsd = 79, MonthlyCreditsMillis = 3_500_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"stripe":{"monthly":"price_pro"}}""" });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    /// <summary>Signs, parses (clock = the event's own time) and processes one Stripe event.</summary>
    private async Task<BillingEventOutcome> Deliver(string type, JsonNode obj, string id, DateTime at, Func<BillingEvent, BillingEvent>? resolve = null)
    {
        string body = Envelope(type, obj, id: id, created: at);
        BillingEvent evt = new StripeEventParser(Options(), new FixedClock(at)).Parse(body, Signed(body, at: at));
        if (resolve is not null) evt = resolve(evt);
        await using InferenceApiDbContext db = NewDb();
        return await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, new FixedClock(T0.AddDays(1))).ProcessAsync(evt, body);
    }

    private async Task<long> Balance()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.CreditAccounts.AsNoTracking().Where(a => a.OrganizationId == OrgId).Select(a => a.BalanceMillis).SingleOrDefaultAsync();
    }

    [Fact]
    public async Task SubscriptionCreated_CreatesAnActiveRow_AndGrantsThePlanCredits()
    {
        (await Deliver("customer.subscription.created", Subscription(start: T0), "evt_s1", T0)).Should().Be(BillingEventOutcome.Applied);

        await using InferenceApiDbContext db = NewDb();
        Subscription sub = await db.Subscriptions.SingleAsync();
        sub.Provider.Should().Be("stripe");
        sub.ProviderSubscriptionId.Should().Be("sub_1");
        sub.Status.Should().Be(SubscriptionStatus.Active);
        sub.PeriodStart.Should().Be(T0);
        sub.PeriodEnd.Should().Be(T0.AddMonths(1));
        (await Balance()).Should().Be(1_000_000);
    }

    [Fact]
    public async Task TopUp_GrantsOnce_AndADuplicateEventIdIsIdempotent()
    {
        JsonObject session = TopUpSession(credits: 1000);

        (await Deliver("checkout.session.completed", session, "evt_t1", T0)).Should().Be(BillingEventOutcome.Applied);
        (await Deliver("checkout.session.completed", session, "evt_t1", T0)).Should().Be(BillingEventOutcome.Duplicate);
        // Stripe may also deliver async_payment_succeeded for the same session under a new event id: still one grant.
        await Deliver("checkout.session.async_payment_succeeded", session, "evt_t2", T0.AddMinutes(1));

        (await Balance()).Should().Be(1_000_000);
        await using InferenceApiDbContext db = NewDb();
        (await db.BillingPayments.SingleAsync()).ProviderTransactionId.Should().Be("pi_1");
    }

    [Fact]
    public async Task UpgradeInvoice_PairsWithThePendingGrant_AndGrantsTheDifferenceOnce()
    {
        await Deliver("customer.subscription.created", Subscription(start: T0), "evt_s1", T0.AddMinutes(-5));
        await Deliver("customer.subscription.updated", Subscription(price: "price_pro", start: T0), "evt_s2", T0);
        (await Balance()).Should().Be(1_000_000, "no paid proration yet");

        JsonObject invoice = Invoice("subscription_update", 2500,
            Line(-2900, "price_creator", 1, T0.AddMinutes(1)),
            Line(5400, "price_pro", 1, T0.AddMinutes(1)));
        (await Deliver("invoice.paid", invoice, "evt_i1", T0.AddMinutes(1))).Should().Be(BillingEventOutcome.Applied);
        long afterFirst = await Balance();
        await Deliver("invoice.paid", invoice, "evt_i2", T0.AddMinutes(2));

        // Prorated from the invoice's own line start (one minute into the period), so just under the full 2,500 difference.
        afterFirst.Should().BeInRange(3_499_000, 3_500_000, "Creator's 1,000 plus the (prorated) difference to Pro");
        (await Balance()).Should().Be(afterFirst, "the same invoice again grants nothing more");
        await using InferenceApiDbContext db = NewDb();
        (await db.BillingPendingUpgradeGrants.SingleAsync()).TransactionId.Should().Be("in_1");
    }

    [Fact]
    public async Task Refund_WithTheTopUpsPaymentIntent_ClawsBack()
    {
        await Deliver("checkout.session.completed", TopUpSession(credits: 1000), "evt_t1", T0);
        (await Balance()).Should().Be(1_000_000);

        // FullRefund is decided by StripeBillingProvider.ResolveAsync (T-004) after reading the original payment.
        (await Deliver("refund.created", Refund(), "evt_r1", T0.AddHours(1), e => e with { FullRefund = true })).Should().Be(BillingEventOutcome.Applied);
        await Deliver("refund.updated", Refund(), "evt_r2", T0.AddHours(2), e => e with { FullRefund = true });

        (await Balance()).Should().Be(0, "clawed back once, keyed by the refund id");
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task UpgradeWhoseSubscriptionEventIsProcessed30MinutesLate_PairsOnChangedAt_AndGrantsOnce(bool withChangedAt, int pairedGrants)
    {
        await Deliver("customer.subscription.created", Subscription(start: T0), "evt_s1", T0.AddMinutes(-5));
        JsonObject invoice = Invoice("subscription_update", 2500,
            Line(-2900, "price_creator", 1, T0.AddMinutes(1)),
            Line(5400, "price_pro", 1, T0.AddMinutes(1)));
        await Deliver("invoice.paid", invoice, "evt_i1", T0.AddMinutes(1));
        (await Balance()).Should().Be(1_000_000, "the payment alone grants nothing until its subscription change arrives");

        // ResolveAsync stamps OccurredAt with the (late) fetch time and keeps the webhook's own time in ChangedAt.
        (await Deliver("customer.subscription.updated", Subscription(price: "price_pro", start: T0), "evt_s2", T0,
            e => e with { OccurredAt = T0.AddMinutes(30), ChangedAt = withChangedAt ? e.OccurredAt : null })).Should().Be(BillingEventOutcome.Applied);
        long after = await Balance();
        await Deliver("customer.subscription.updated", Subscription(price: "price_pro", start: T0), "evt_s3", T0,
            e => e with { OccurredAt = T0.AddMinutes(31), ChangedAt = withChangedAt ? e.OccurredAt : null });

        (await Balance()).Should().Be(after, "a second delivery grants nothing more");
        if (pairedGrants == 1)
        {
            after.Should().BeInRange(3_499_000, 3_500_000, "Creator's 1,000 plus the prorated difference, granted once");
        }
        else
        {
            after.Should().Be(1_000_000, "without ChangedAt the 30-minute-late event no longer correlates with its payment");
        }
    }

    [Fact]
    public async Task LostChargeback_OfATopUp_ClawsTheCreditsBackOnce_AndAWonDisputeDoesNothing()
    {
        await Deliver("checkout.session.completed", TopUpSession(credits: 1000), "evt_t1", T0);
        (await Balance()).Should().Be(1_000_000);

        (await Deliver("charge.dispute.closed", Dispute("won"), "evt_d0", T0.AddDays(1))).Should().Be(BillingEventOutcome.Applied);
        (await Balance()).Should().Be(1_000_000, "a won dispute is ignored");

        // FullRefund is decided by StripeBillingProvider.ResolveAsync (dispute amount covers the charge).
        (await Deliver("charge.dispute.closed", Dispute("lost"), "evt_d1", T0.AddDays(2), e => e with { FullRefund = true })).Should().Be(BillingEventOutcome.Applied);
        await Deliver("charge.dispute.closed", Dispute("lost"), "evt_d2", T0.AddDays(3), e => e with { FullRefund = true });

        (await Balance()).Should().Be(0, "clawed back once, keyed by the dispute id");
    }
}
