#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Reconcile;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Billing.Stripe.StripeKit;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

/// <summary>
/// QA regression (stripe-billing-provider): the lost-webhook reconciler fed by the REAL
/// <see cref="StripeBillingProvider"/>. A Stripe subscription whose upgrade webhook was lost must be
/// healed by an admin/forced reconcile the way a Paddle one is (brief outcome 2, "lost-webhook
/// BillingReconciler parity"; docs/billing.md "Reconcile (Stripe)").
/// </summary>
public sealed class StripeReconcileQaTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _org = Guid.NewGuid();

    public StripeReconcileQaTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = _org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.Plans.AddRange(
            new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, PriceMonthlyUsd = 29, MonthlyCreditsMillis = 1_000_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"stripe":{"monthly":"price_creator"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "pro", Name = "Pro", Kind = PlanKind.Subscription, PriceMonthlyUsd = 79, MonthlyCreditsMillis = 3_500_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"stripe":{"monthly":"price_pro"}}""" });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private async Task SeedCreatorViaWebhook()
    {
        await using InferenceApiDbContext db = NewDb();
        BillingEvent created = new()
        {
            Provider = StripeBillingProvider.ProviderName,
            EventId = "evt_created",
            EventType = "customer.subscription.created",
            OccurredAt = Now.AddDays(-10),
            Kind = BillingEventKind.SubscriptionChanged,
            OrganizationId = _org,
            CustomerId = "cus_1",
            SubscriptionId = "sub_1",
            Status = SubscriptionStatus.Active,
            PeriodStart = Now.AddDays(-10),
            PeriodEnd = Now.AddDays(20),
            Items = [new BillingLineItem("price_creator", 1)]
        };
        (await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, new FixedClock(Now)).ProcessAsync(created, "{}"))
            .Should().Be(BillingEventOutcome.Applied);
    }

    private StripeFake ProFake() => new StripeFake()
        .On("GET", "/v1/subscriptions/sub_1", SubscriptionJson(price: "price_pro", org: _org.ToString()))
        .On("GET", "/v1/invoices", new { data = Array.Empty<object>(), has_more = false });

    private async Task<ReconcileResult> ReconcileAs(IBillingProvider[] providers, ReconcileTrigger trigger, bool force, BillingProviderOptions? selection = null)
    {
        await using InferenceApiDbContext db = NewDb();
        BillingReconciler reconciler = new(
            db,
            providers,
            new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, new FixedClock(Now)),
            new InProcessBillingChangeLock(),
            Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = true }),
            Microsoft.Extensions.Options.Options.Create(new BillingReconcileOptions()),
            NullLogger<BillingReconciler>.Instance,
            tenant: null,
            time: new FixedClock(Now),
            providerOptions: selection is null ? null : Microsoft.Extensions.Options.Options.Create(selection));
        return await reconciler.ReconcileAsync(_org, trigger, force, CancellationToken.None);
    }

    private async Task WatchChangeAt(DateTime requestedAt)
    {
        await using InferenceApiDbContext db = NewDb();
        db.BillingReconcileWatches.Add(new BillingReconcileWatch { OrganizationId = _org, ChangeKind = "change_plan", ChangeRequestedAt = requestedAt });
        await db.SaveChangesAsync();
    }

    private async Task AssertOnPro()
    {
        await using InferenceApiDbContext check = NewDb();
        Guid pro = (await check.Plans.SingleAsync(p => p.Key == "pro")).Id;
        (await check.Subscriptions.AsNoTracking().SingleAsync(s => s.ProviderSubscriptionId == "sub_1")).PlanId.Should().Be(pro);
    }

    private async Task AssertStillOnCreator()
    {
        await using InferenceApiDbContext check = NewDb();
        Guid creator = (await check.Plans.SingleAsync(p => p.Key == "creator")).Id;
        (await check.Subscriptions.AsNoTracking().SingleAsync(s => s.ProviderSubscriptionId == "sub_1")).PlanId.Should().Be(creator);
    }

    [Fact]
    public async Task ForcedReconcile_OfAStripeSubscriptionWhoseUpgradeWebhookWasLost_HealsTheRow()
    {
        await SeedCreatorViaWebhook();

        ReconcileResult result = await ReconcileAs([Provider(ProFake())], ReconcileTrigger.Admin, force: true);

        result.Error.Should().NotBe(BillingReconciler.NoUpdatedAtError, "a Stripe snapshot must be reconcilable even though Stripe has no updated_at");
        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        await AssertOnPro();
    }

    [Fact]
    public async Task LazyReconcile_OfAStripeSubscription_Heals_WhenNoChangeRequestIsRecorded()
    {
        await SeedCreatorViaWebhook();

        (await ReconcileAs([Provider(ProFake())], ReconcileTrigger.Lazy, force: false)).Outcome.Should().Be(ReconcileOutcome.Healed);
        await AssertOnPro();
    }

    [Fact]
    public async Task SweepReconcile_OfAStripeSubscription_HealsOnceTheWatchedChangeIsOlderThanTheStuckThreshold()
    {
        await SeedCreatorViaWebhook();
        await WatchChangeAt(Now.AddHours(-2));

        (await ReconcileAs([Provider(ProFake())], ReconcileTrigger.Sweep, force: false)).Outcome.Should().Be(ReconcileOutcome.Healed);
        await AssertOnPro();
    }

    [Fact]
    public async Task StripeDrift_YoungerThanTheStuckThreshold_IsLeftToItsWebhook_UnlessForced()
    {
        await SeedCreatorViaWebhook();
        await WatchChangeAt(Now.AddSeconds(-5));

        ReconcileResult recent = await ReconcileAs([Provider(ProFake())], ReconcileTrigger.Sweep, force: false);
        recent.Outcome.Should().Be(ReconcileOutcome.TooRecent);
        await AssertStillOnCreator();

        (await ReconcileAs([Provider(ProFake())], ReconcileTrigger.Admin, force: true)).Outcome.Should().Be(ReconcileOutcome.Healed);
        await AssertOnPro();
    }

    [Fact]
    public async Task WithNoSubscriptionRow_TheSelectedProviderIsAsked_NotTheFirstConfiguredOne()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            db.BillingCustomers.Add(new BillingCustomer { OrganizationId = _org, Provider = StripeBillingProvider.ProviderName, ProviderCustomerId = "cus_1" });
            await db.SaveChangesAsync();
        }

        StripeFake fake = new StripeFake()
            .On("GET", "/v1/subscriptions", new { data = new[] { new { id = "sub_1", status = "active", metadata = new { organization_id = _org.ToString() } } }, has_more = false })
            .On("GET", "/v1/subscriptions/sub_1", SubscriptionJson(price: "price_pro", org: _org.ToString()))
            .On("GET", "/v1/invoices", new { data = Array.Empty<object>(), has_more = false });
        PaddlePayloads.StubHandler paddleHandler = new(_ => throw new InvalidOperationException("Paddle must not be asked"));
        IBillingProvider[] providers = [PaddlePayloads.Provider(paddleHandler), Provider(fake)];

        ReconcileResult result = await ReconcileAs(providers, ReconcileTrigger.Admin, force: true, new BillingProviderOptions { Provider = "stripe" });

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        paddleHandler.Calls.Should().BeEmpty();
        await AssertOnPro();
    }

    // ------------------------------------------------------------------ review round 2

    private static readonly DateTime Paid = Now.AddHours(-2);

    private object LongPeriodSubscription(string price) => new
    {
        id = "sub_1",
        customer = "cus_1",
        status = "active",
        metadata = new { organization_id = _org.ToString() },
        cancel_at_period_end = false,
        cancel_at = (long?)null,
        discounts = Array.Empty<object>(),
        items = new { data = new[] { new { id = "si_1", price = new { id = price }, quantity = 1, current_period_start = Unix(Now.AddDays(-10)), current_period_end = Unix(Now.AddDays(20)) } } }
    };

    private static System.Text.Json.Nodes.JsonObject UpgradeInvoice(DateTime paidAt)
    {
        System.Text.Json.Nodes.JsonObject inv = StripePayloads.Invoice("subscription_update", 2500,
            StripePayloads.Line(-2900, "price_creator", 1, paidAt),
            StripePayloads.Line(5400, "price_pro", 1, paidAt));
        inv["created"] = Unix(paidAt);
        return inv;
    }

    private async Task PayUpgradeInvoice(System.Text.Json.Nodes.JsonObject invoice)
    {
        string body = StripePayloads.Envelope("invoice.paid", invoice, "evt_paid", Paid);
        BillingEvent evt = new StripeEventParser(StripePayloads.Options(), new FixedClock(Paid)).Parse(body, StripePayloads.Signed(body, at: Paid));
        evt.Kind.Should().Be(BillingEventKind.UpgradePaid);
        await using InferenceApiDbContext db = NewDb();
        (await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, new FixedClock(Now)).ProcessAsync(evt, body)).Should().Be(BillingEventOutcome.Applied);
    }

    private async Task<long> BalanceAsync()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.CreditAccounts.AsNoTracking().Where(a => a.OrganizationId == _org).Select(a => a.BalanceMillis).SingleOrDefaultAsync();
    }

    private StripeFake LongFake(string price, System.Text.Json.Nodes.JsonObject? invoice)
    {
        System.Text.Json.Nodes.JsonArray data = invoice is null ? [] : [invoice.DeepClone()];
        return new StripeFake()
            .On("GET", "/v1/subscriptions/sub_1", LongPeriodSubscription(price))
            .On("GET", "/v1/invoices", new System.Text.Json.Nodes.JsonObject { ["data"] = data, ["has_more"] = false }.ToJsonString());
    }

    [Theory]
    [InlineData(ReconcileTrigger.Sweep)]
    [InlineData(ReconcileTrigger.Lazy)]
    public async Task ReconcileHealedUpgrade_TwoHoursAfterThePayment_PairsWithIt_AndGrantsTheCreditsExactlyOnce(ReconcileTrigger trigger)
    {
        await SeedCreatorViaWebhook();
        await PayUpgradeInvoice(UpgradeInvoice(Paid));
        (await BalanceAsync()).Should().Be(1_000_000, "the payment alone grants nothing until its subscription change arrives");
        await WatchChangeAt(Paid);

        // The customer.subscription.updated webhook was lost; the reconcile sees the subscription on Pro and the paid invoice.
        (await ReconcileAs([Provider(LongFake("price_pro", UpgradeInvoice(Paid)))], trigger, force: false)).Outcome.Should().Be(ReconcileOutcome.Healed);
        await AssertOnPro();
        long healed = await BalanceAsync();
        healed.Should().BeGreaterThan(1_000_000).And.BeLessThanOrEqualTo(3_500_000, "Creator's 1,000 plus the prorated difference to Pro, granted by the paired payment");

        // Nothing drifted any more, and even a forced run grants nothing twice.
        await ReconcileAs([Provider(LongFake("price_pro", UpgradeInvoice(Paid)))], ReconcileTrigger.Admin, force: true);
        (await BalanceAsync()).Should().Be(healed);
    }

    [Fact]
    public async Task Snapshot_LeavesChangedAtNull_WhenNoPaidUpgradeInvoiceMatchesTheCurrentItems()
    {
        StripeBillingProvider p = Provider(LongFake("price_pro", null));
        (await p.SnapshotSubscriptionAsync("sub_1", default)).Event.ChangedAt.Should().BeNull();

        System.Text.Json.Nodes.JsonObject other = StripePayloads.Invoice("subscription_update", 2500,
            StripePayloads.Line(-2900, "price_creator", 1, Paid), StripePayloads.Line(5400, "price_other", 1, Paid));
        (await Provider(LongFake("price_pro", other)).SnapshotSubscriptionAsync("sub_1", default)).Event.ChangedAt.Should().BeNull();
        (await Provider(LongFake("price_pro", UpgradeInvoice(Paid))).SnapshotSubscriptionAsync("sub_1", default)).Event.ChangedAt.Should().Be(Paid);
    }

    [Fact]
    public async Task ReconcileRoundTrip_AToBToA_HealsEveryTime_BecauseTheIdCarriesTheFetchTime()
    {
        await SeedCreatorViaWebhook();
        StripeBillingProvider At(bool cancel, int hours) => new(
            new System.Net.Http.HttpClient(new StripeFake()
                .On("GET", "/v1/subscriptions/sub_1", SubscriptionJson(price: "price_creator", org: _org.ToString(), cancelAtPeriodEnd: cancel))
                .On("GET", "/v1/invoices", new { data = Array.Empty<object>(), has_more = false })),
            Microsoft.Extensions.Options.Options.Create(Options()), new FixedClock(Now.AddHours(hours)), null, (_, _) => Task.CompletedTask);
        async Task<bool> CancelFlag()
        {
            await using InferenceApiDbContext check = NewDb();
            return (await check.Subscriptions.AsNoTracking().SingleAsync(s => s.ProviderSubscriptionId == "sub_1")).CancelAtPeriodEnd;
        }

        (await ReconcileAs([At(true, 1)], ReconcileTrigger.Admin, force: true)).Outcome.Should().Be(ReconcileOutcome.Healed);
        (await CancelFlag()).Should().BeTrue();
        (await ReconcileAs([At(false, 2)], ReconcileTrigger.Admin, force: true)).Outcome.Should().Be(ReconcileOutcome.Healed);
        (await CancelFlag()).Should().BeFalse();
        (await ReconcileAs([At(true, 3)], ReconcileTrigger.Admin, force: true)).Outcome.Should().Be(ReconcileOutcome.Healed, "the second cancel snapshot must not collide with the first applied id");
        (await CancelFlag()).Should().BeTrue();
    }
}
