#nullable enable
using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Billing.Stripe.StripePayloads;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

/// <summary>
/// billing-revenue-discount T-005: <c>alpha.first_payment</c> for Stripe, with real payload shapes, in either
/// arrival order of invoice.paid and customer.subscription.created, and for a one-off top-up.
/// </summary>
public sealed class StripeFirstPaymentTests : IDisposable
{
    private static readonly DateTime Now = new(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _provider;

    public StripeFirstPaymentTests()
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
            TopUpAllowed = true,
            IsActive = true,
            ProviderPriceIdsJson = """{"stripe":{"monthly":"price_creator"}}"""
        });
        db.SaveChanges();

        var services = new ServiceCollection();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IProductEventLog>(sp => new ProductEventLog(sp.GetRequiredService<InferenceApiDbContext>()));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private static JsonObject PositiveInvoice(string id, string reason, bool withMetadata = true)
    {
        JsonObject inv = Invoice(reason, 2900, Line(2900, "price_creator", 1, Now.AddDays(-1)));
        inv["id"] = id;
        inv["currency"] = "usd";
        inv["subtotal"] = 2900;
        inv["total"] = 2900;
        inv["total_excluding_tax"] = 2900;
        if (!withMetadata) inv["parent"]!["subscription_details"]!["metadata"] = new JsonObject();
        return inv;
    }

    private async Task Deliver(string type, JsonNode obj, string id, DateTime at)
    {
        string body = Envelope(type, obj, id: id, created: at);
        BillingEvent evt = new StripeEventParser(Options(), new FixedClock(at)).Parse(body, Signed(body, at: at));
        await using InferenceApiDbContext db = NewDb();
        await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, new FixedClock(at),
            scopeFactory: _provider.GetRequiredService<IServiceScopeFactory>()).ProcessAsync(evt, body);
    }

    private Task SubscriptionCreated(string id = "evt_sub") =>
        Deliver("customer.subscription.created", Subscription(start: Now.AddDays(-1)), id, Now.AddDays(-1));

    private async Task FirstPaymentEvents(int expected, string because)
    {
        await using InferenceApiDbContext db = NewDb();
        (await db.ProductEvents.CountAsync(e => e.Event == ProductEventLog.FirstPaymentEvent)).Should().Be(expected, because);
    }

    [Fact]
    public async Task Subscription_first_then_the_positive_first_invoice_emits_once()
    {
        await SubscriptionCreated();
        await Deliver("invoice.paid", PositiveInvoice("in_1", "subscription_create"), "evt_i1", Now);
        await Deliver("invoice.paid", PositiveInvoice("in_1", "subscription_create"), "evt_i1b", Now);
        await Deliver("invoice.paid", PositiveInvoice("in_2", "subscription_cycle"), "evt_i2", Now.AddMinutes(1));
        await FirstPaymentEvents(1, "one activation per organization, replays and renewals add nothing");
    }

    [Fact]
    public async Task Invoice_before_the_subscription_event_emits_once_via_the_invoice_metadata()
    {
        await Deliver("invoice.paid", PositiveInvoice("in_1", "subscription_create"), "evt_i1", Now);
        await FirstPaymentEvents(1, "the payload's own organization claim attributes it");
        await SubscriptionCreated();
        await FirstPaymentEvents(1, "the later subscription event adds nothing");
    }

    [Fact]
    public async Task Invoice_without_metadata_is_attributed_through_billing_customers()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            db.BillingCustomers.Add(new BillingCustomer { Id = Guid.NewGuid(), OrganizationId = OrgId, Provider = "stripe", ProviderCustomerId = "cus_1" });
            await db.SaveChangesAsync();
        }

        await Deliver("invoice.paid", PositiveInvoice("in_1", "subscription_create", withMetadata: false), "evt_i1", Now);
        await FirstPaymentEvents(1, "customer row resolves the organization");
    }

    [Fact]
    public async Task Invoice_with_no_metadata_and_no_customer_is_attributed_when_the_subscription_lands()
    {
        await Deliver("invoice.paid", PositiveInvoice("in_1", "subscription_create", withMetadata: false), "evt_i1", Now);
        await FirstPaymentEvents(0, "nothing can attribute it yet");

        await SubscriptionCreated();
        await FirstPaymentEvents(1, "the subscription row now names the organization of the recorded payment");
        await using InferenceApiDbContext db = NewDb();
        (await db.BillingPayments.SingleAsync()).OrganizationId.Should().Be(OrgId);

        await Deliver("customer.subscription.updated", Subscription(start: Now.AddDays(-1)), "evt_sub2", Now);
        await FirstPaymentEvents(1, "claimed once");
    }

    [Fact]
    public async Task A_one_off_top_up_checkout_emits_the_first_payment()
    {
        await Deliver("checkout.session.completed", TopUpSession(credits: 1000), "evt_t1", Now);
        await FirstPaymentEvents(1, "a top-up is money too");
    }
}
