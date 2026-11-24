#nullable enable
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Admin;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Reconcile;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Billing.Stripe.StripePayloads;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

/// <summary>
/// billing-revenue-discount T-004: a 100%-off Stripe subscription (first invoice and renewals) is a recorded
/// payment of 0, so the admin overview shows 0 collected and 0 committed for it instead of its list price.
/// </summary>
public sealed class StripeRevenueDiscountTests : IDisposable
{
    private static readonly DateTime Now = new(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public StripeRevenueDiscountTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = OrgId, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.Plans.Add(new Plan
        {
            Id = Guid.NewGuid(),
            Key = "creator",
            Name = "Creator",
            Kind = PlanKind.Subscription,
            PriceMonthlyUsd = 29,
            MonthlyCreditsMillis = 1_000_000,
            IsActive = true,
            ProviderPriceIdsJson = """{"stripe":{"monthly":"price_creator"}}"""
        });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private sealed class Admin : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "a@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => true;
        public Guid? OrganizationId => null;
        public OrganizationRole? OrgRole => null;
    }

    private sealed class NoReconciler : IBillingReconciler
    {
        public Task<ReconcileResult> ReconcileAsync(Guid organizationId, ReconcileTrigger trigger, bool force, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReconcileResult> ReconcileHeldAsync(Guid organizationId, ReconcileTrigger trigger, bool force, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordChangeRequestedAsync(Guid organizationId, string changeKind, Guid? userId, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A real-shaped <c>invoice.paid</c> object: parent.subscription_details, discounts and totals.</summary>
    private static JsonObject PaidInvoice(string id, string reason, long subtotal, long discount, long paid, long tax = 0)
    {
        JsonObject inv = Invoice(reason, paid, Line(paid, "price_creator", 1, Now.AddDays(-9)));
        inv["id"] = id;
        inv["currency"] = "usd";
        inv["subtotal"] = subtotal;
        inv["total"] = subtotal - discount + tax;
        inv["total_excluding_tax"] = subtotal - discount;
        inv["total_discount_amounts"] = discount > 0
            ? new JsonArray(new JsonObject { ["amount"] = discount, ["discount"] = "di_1" })
            : new JsonArray();
        if (discount > 0) inv["discounts"] = new JsonArray("di_1");
        return inv;
    }

    private async Task<BillingEventOutcome> Deliver(string type, JsonNode obj, string id, DateTime at, IServiceScopeFactory? scopes = null)
    {
        string body = Envelope(type, obj, id: id, created: at);
        BillingEvent evt = new StripeEventParser(Options(), new FixedClock(at)).Parse(body, Signed(body, at: at));
        await using InferenceApiDbContext db = NewDb();
        return await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, new FixedClock(at), scopeFactory: scopes).ProcessAsync(evt, body);
    }

    private async Task Subscribe() =>
        await Deliver("customer.subscription.created", Subscription(start: Now.AddDays(-10)), "evt_sub", Now.AddDays(-10));

    private async Task<BillingAdminRevenue> Revenue()
    {
        await using InferenceApiDbContext db = NewDb();
        BillingAdminController controller = new(db, new Admin(), new NoReconciler(),
            Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = true }), Microsoft.Extensions.Options.Options.Create(new BillingReconcileOptions()));
        ActionResult<BillingAdminOverviewResponse> result = await controller.Overview(null, null, CancellationToken.None);
        return ((BillingAdminOverviewResponse)((OkObjectResult)result.Result!).Value!).Revenue;
    }

    [Fact]
    public void A_zero_first_invoice_carries_the_whole_breakdown()
    {
        string body = Envelope("invoice.paid", PaidInvoice("in_1", "subscription_create", 2900, 2900, 0), created: Now);
        BillingEvent e = new StripeEventParser(Options(), new FixedClock(Now)).Parse(body, Signed(body, at: Now));

        e.IsPaidTransaction.Should().BeTrue();
        (e.AmountMinor, e.SubtotalMinor, e.DiscountMinor, e.TaxMinor, e.TotalMinor).Should().Be((0L, 2900L, 2900L, 0L, 0L));
    }

    [Theory]
    [InlineData("subscription_cycle")]
    [InlineData("subscription_update")]
    public void A_zero_renewal_or_upgrade_invoice_is_recorded_but_takes_no_credit_action(string reason)
    {
        string body = Envelope("invoice.paid", PaidInvoice("in_1", reason, 2900, 2900, 0), created: Now);
        BillingEvent e = new StripeEventParser(Options(), new FixedClock(Now)).Parse(body, Signed(body, at: Now));

        e.Kind.Should().Be(BillingEventKind.Ignored);
        e.IgnoredReason.Should().Be("zero amount invoice");
        e.IsPaidTransaction.Should().BeTrue();
        e.SubscriptionId.Should().Be("sub_1");
        (e.AmountMinor, e.SubtotalMinor, e.DiscountMinor).Should().Be((0L, 2900L, 2900L));
    }

    [Fact]
    public async Task A_100_percent_off_stripe_subscription_reads_zero_collected_and_zero_committed()
    {
        await Subscribe();
        await Deliver("invoice.paid", PaidInvoice("in_1", "subscription_create", 2900, 2900, 0), "evt_i1", Now.AddDays(-9));
        await Deliver("invoice.paid", PaidInvoice("in_2", "subscription_cycle", 2900, 2900, 0), "evt_i2", Now.AddDays(-2));

        await using (InferenceApiDbContext db = NewDb())
        {
            (await db.BillingPayments.CountAsync()).Should().Be(2);
            (await db.BillingPayments.AllAsync(p => p.AmountMinor == 0 && p.SubtotalMinor == 2900 && p.DiscountMinor == 2900)).Should().BeTrue();
        }

        BillingAdminRevenue revenue = await Revenue();
        revenue.ListPriceMonthlyUsd.Should().Be(29m);
        revenue.CommittedMonthlyUsd.Should().Be(0m);
        revenue.FullyDiscountedSubscriptions.Should().Be(1);
        revenue.SubscriptionsWithoutPayment.Should().Be(0);
        BillingCollectedCurrency usd = revenue.Collected!.Single();
        (usd.CurrencyCode, usd.Payments, usd.ZeroAmountPayments, usd.GrossMinor, usd.NetMinor).Should().Be(("USD", 2, 2, 0L, 0L));
    }

    [Fact]
    public async Task A_full_price_renewal_after_a_once_only_100_percent_coupon_restores_the_list_price()
    {
        await Subscribe();
        await Deliver("invoice.paid", PaidInvoice("in_1", "subscription_create", 2900, 2900, 0), "evt_i1", Now.AddDays(-9));
        await Deliver("invoice.paid", PaidInvoice("in_2", "subscription_cycle", 2900, 0, 2900), "evt_i2", Now.AddDays(-1));

        BillingAdminRevenue revenue = await Revenue();
        revenue.CommittedMonthlyUsd.Should().Be(29m);
        revenue.FullyDiscountedSubscriptions.Should().Be(0);
        revenue.Collected!.Single().GrossMinor.Should().Be(2900);
    }

    [Fact]
    public async Task A_replayed_zero_invoice_is_recorded_once()
    {
        await Subscribe();
        JsonObject invoice = PaidInvoice("in_1", "subscription_cycle", 2900, 2900, 0);
        await Deliver("invoice.paid", invoice, "evt_i1", Now.AddDays(-2));
        await Deliver("invoice.paid", invoice, "evt_i1", Now.AddDays(-2));
        await Deliver("invoice.paid", invoice, "evt_i9", Now.AddDays(-2));

        await using InferenceApiDbContext db = NewDb();
        (await db.BillingPayments.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_zero_amount_payment_does_not_use_up_the_first_payment_funnel_row()
    {
        var services = new ServiceCollection();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IProductEventLog>(sp => new ProductEventLog(sp.GetRequiredService<InferenceApiDbContext>()));
        using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();

        await Subscribe();
        await Deliver("invoice.paid", PaidInvoice("in_1", "subscription_create", 2900, 2900, 0), "evt_i1", Now.AddDays(-9), scopes);
        await using (InferenceApiDbContext db = NewDb())
        {
            (await db.ProductEvents.CountAsync()).Should().Be(0, "no money moved");
        }

        await Deliver("invoice.paid", PaidInvoice("in_2", "subscription_cycle", 2900, 0, 2900), "evt_i2", Now.AddDays(-1), scopes);
        await using (InferenceApiDbContext db = NewDb())
        {
            (await db.ProductEvents.CountAsync()).Should().Be(1, "the first real payment is the activation");
        }
    }

    [Fact]
    public async Task Plan_margin_commits_only_paying_provider_subscriptions()
    {
        Guid planId;
        await using (InferenceApiDbContext db = NewDb())
        {
            planId = (await db.Plans.SingleAsync()).Id;
            Guid trialOrg = Guid.NewGuid();
            Guid grantOrg = Guid.NewGuid();
            db.Organizations.AddRange(
                new Organization { Id = trialOrg, Name = "t", Slug = "t", Kind = OrganizationKind.Team },
                new Organization { Id = grantOrg, Name = "g", Slug = "g", Kind = OrganizationKind.Team });
            db.Subscriptions.AddRange(
                new Subscription { Id = Guid.NewGuid(), OrganizationId = trialOrg, PlanId = planId, Provider = "stripe", ProviderSubscriptionId = "sub_t", Status = SubscriptionStatus.Trialing, CreatedAt = Now.AddDays(-5) },
                new Subscription { Id = Guid.NewGuid(), OrganizationId = grantOrg, PlanId = planId, Provider = null, Status = SubscriptionStatus.Active, CreatedAt = Now.AddDays(-5) });
            await db.SaveChangesAsync();
        }

        await using InferenceApiDbContext read = NewDb();
        BillingAdminController controller = new(read, new Admin(), new NoReconciler(),
            Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = true }), Microsoft.Extensions.Options.Options.Create(new BillingReconcileOptions()));
        var body = (BillingAdminOverviewResponse)((OkObjectResult)(await controller.Overview(null, null, CancellationToken.None)).Result!).Value!;

        BillingPlanMargin row = body.Plans.Single();
        row.Organizations.Should().Be(2, "they still count as organizations");
        row.CommittedMonthlyUsd.Should().Be(0m);
        row.WindowCommittedUsd.Should().Be(0m);
        row.ListPriceMonthlyUsd.Should().Be(0m);
    }
}
