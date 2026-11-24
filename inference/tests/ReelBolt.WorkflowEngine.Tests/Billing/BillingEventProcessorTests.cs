using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Paddle;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// B9a: provider-neutral webhook effects. Exactly-once (replay, and a differently-numbered event for
/// the same period or payment), ordering by <c>occurred_at</c>, plan refill on renewal, seat scaling,
/// top-up grant, refund clawback and cancel. Sqlite; the processor owns its transaction.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class BillingEventProcessorTests : IDisposable
{
    private const string P = "fake";
    private static readonly DateTime T0 = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _org = Guid.NewGuid();

    public BillingEventProcessorTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = _org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.Plans.AddRange(
            new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, PriceMonthlyUsd = 29, MonthlyCreditsMillis = 1_000_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_creator"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "pro", Name = "Pro", Kind = PlanKind.Subscription, PriceMonthlyUsd = 79, MonthlyCreditsMillis = 3_500_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_pro"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "team", Name = "Team", Kind = PlanKind.PerSeat, MinSeats = 3, PriceMonthlyUsd = 49, MonthlyCreditsMillis = 1_500_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_team"}}""" });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private async Task<BillingEventOutcome> Process(BillingEvent evt, DateTime? now = null, IProductEventLog? productEvents = null, IServiceScopeFactory? scopeFactory = null)
    {
        await using InferenceApiDbContext db = NewDb();
        return await new BillingEventProcessor(db, _log, new FixedClock(now ?? T0.AddDays(1)), productEvents: productEvents, scopeFactory: scopeFactory)
            .ProcessAsync(evt, "{}");
    }

    /// <summary>
    /// The scope factory production supplies: a fresh <see cref="InferenceApiDbContext"/> and the registered
    /// <see cref="IProductEventLog"/> per scope, so the payment step's funnel write happens off the money
    /// context after the transaction commits.
    /// </summary>
    private ServiceProvider ScopeProvider(Func<IServiceProvider, IProductEventLog>? writer = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InferenceApiDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped<IProductEventLog>(sp => writer is null
            ? new ProductEventLog(sp.GetRequiredService<InferenceApiDbContext>())
            : writer(sp));
        return services.BuildServiceProvider();
    }

    /// <summary>An <see cref="IProductEventLog"/> that always throws: a missing table, a deadlock, an exhausted pool.</summary>
    private sealed class ThrowingProductEventLog : IProductEventLog
    {
        public Task AppendAsync(ProductEvent entry, CancellationToken ct)
            => Task.FromException(new InvalidOperationException("product_events is unavailable"));

        public Task<bool> ClaimAndAppendAsync(Guid userId, Guid? organizationId, string eventName, string source, CancellationToken ct)
            => Task.FromException<bool>(new InvalidOperationException("product_events is unavailable"));

        public Task<bool> ClaimAndAppendFirstPaymentAsync(Guid organizationId, Guid? userId, CancellationToken ct)
            => Task.FromException<bool>(new InvalidOperationException("product_events is unavailable"));
    }

    /// <summary>
    /// A writer whose append runs real, failing SQL on its own context — a statement failure, not a mock
    /// throw — so the test proves isolation from an actual database error, not a hand-rolled exception.
    /// </summary>
    private sealed class FailingSqlProductEventLog(InferenceApiDbContext context) : IProductEventLog
    {
        public Task AppendAsync(ProductEvent entry, CancellationToken ct)
            => context.Database.ExecuteSqlRawAsync("INSERT INTO product_events_that_does_not_exist (id) VALUES (1)", ct);

        public Task<bool> ClaimAndAppendAsync(Guid userId, Guid? organizationId, string eventName, string source, CancellationToken ct)
            => Task.FromException<bool>(new NotSupportedException("the payment path never claims"));

        public async Task<bool> ClaimAndAppendFirstPaymentAsync(Guid organizationId, Guid? userId, CancellationToken ct)
        {
            await context.Database.ExecuteSqlRawAsync("INSERT INTO product_events_that_does_not_exist (id) VALUES (1)", ct);
            return false;
        }
    }

    private readonly ListLogger _log = new();

    /// <summary>Keeps every log entry so a test can assert the processor's warnings for manual review.</summary>
    private sealed class ListLogger : ILogger<BillingEventProcessor>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private async Task<long> Balance()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.CreditAccounts.AsNoTracking().Where(a => a.OrganizationId == _org).Select(a => a.BalanceMillis).SingleOrDefaultAsync();
    }

    private BillingEvent Sub(string id, DateTime at, string sub = "sub_1", SubscriptionStatus status = SubscriptionStatus.Active,
        DateTime? start = null, string price = "pri_creator", int qty = 1, bool cancelAtEnd = false, Guid? org = null) => new()
        {
            Provider = P,
            EventId = id,
            EventType = "subscription.updated",
            OccurredAt = at,
            Kind = BillingEventKind.SubscriptionChanged,
            OrganizationId = org ?? _org,
            CustomerId = "ctm_1",
            SubscriptionId = sub,
            Status = status,
            PeriodStart = start ?? T0,
            PeriodEnd = (start ?? T0).AddMonths(1),
            CancelAtPeriodEnd = cancelAtEnd,
            Items = [new BillingLineItem(price, qty)]
        };

    private BillingEvent TopUp(string id, string txn, int credits = 1000, long amount = 1450,
        string currency = "USD", long? fee = null, bool paid = true) => new()
        {
            Provider = P,
            EventId = id,
            EventType = "transaction.completed",
            OccurredAt = T0,
            Kind = BillingEventKind.TopUpPaid,
            OrganizationId = _org,
            CustomerId = "ctm_1",
            TransactionId = txn,
            TopUpCredits = credits,
            AmountMinor = amount,
            CurrencyCode = currency,
            FeeMinor = fee,
            IsPaidTransaction = paid
        };

    private BillingEvent Refund(string id, string txn, string adj, bool full = true) => new()
    {
        Provider = P,
        EventId = id,
        EventType = "adjustment.updated",
        OccurredAt = T0,
        Kind = BillingEventKind.PaymentRefunded,
        TransactionId = txn,
        AdjustmentId = adj,
        FullRefund = full
    };

    [Fact]
    public async Task SubscriptionCreated_CreatesRowGrantsPlanCreditsAndRemembersTheCustomer()
    {
        (await Process(Sub("evt_1", T0))).Should().Be(BillingEventOutcome.Applied);

        await using InferenceApiDbContext db = NewDb();
        Subscription sub = await db.Subscriptions.SingleAsync();
        sub.Status.Should().Be(SubscriptionStatus.Active);
        sub.Provider.Should().Be(P);
        sub.PeriodEnd.Should().Be(T0.AddMonths(1));
        (await Balance()).Should().Be(1_000_000);
        (await db.CreditGrants.SingleAsync()).ExpiresAt.Should().Be(T0.AddMonths(1));
        (await db.BillingCustomers.SingleAsync()).ProviderCustomerId.Should().Be("ctm_1");
    }

    [Fact]
    public async Task ReplayingTheSameEvent_GrantsOnce_AndIsADuplicate()
    {
        BillingEvent evt = Sub("evt_1", T0);
        (await Process(evt)).Should().Be(BillingEventOutcome.Applied);
        (await Process(evt)).Should().Be(BillingEventOutcome.Duplicate);
        (await Process(evt)).Should().Be(BillingEventOutcome.Duplicate);

        (await Balance()).Should().Be(1_000_000);
        await using InferenceApiDbContext db = NewDb();
        (await db.BillingWebhookEvents.CountAsync()).Should().Be(1);
        (await db.BillingWebhookEvents.SingleAsync()).ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ADifferentEventDescribingTheSamePeriod_DoesNotGrantAgain()
    {
        await Process(Sub("evt_created", T0));
        await Process(Sub("evt_updated", T0.AddMinutes(1)));
        await Process(Sub("evt_activated", T0.AddMinutes(2)));

        (await Balance()).Should().Be(1_000_000);
    }

    [Fact]
    public async Task Renewal_RefillsOncePerPeriod()
    {
        await Process(Sub("evt_1", T0));
        DateTime next = T0.AddMonths(1);
        await Process(Sub("evt_2", next, start: next));
        await Process(Sub("evt_3", next.AddMinutes(1), start: next));

        (await Balance()).Should().Be(2_000_000);
        await using InferenceApiDbContext db = NewDb();
        (await db.CreditGrants.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task OutOfOrder_AnOlderEventNeverOverwritesANewerOne()
    {
        await Process(Sub("evt_new", T0.AddHours(2), status: SubscriptionStatus.Canceled));
        BillingEventOutcome late = await Process(Sub("evt_old", T0.AddHours(1), status: SubscriptionStatus.Active));

        late.Should().Be(BillingEventOutcome.Stale);
        await using InferenceApiDbContext db = NewDb();
        (await db.Subscriptions.SingleAsync()).Status.Should().Be(SubscriptionStatus.Canceled);
        (await db.BillingWebhookEvents.CountAsync()).Should().Be(2, "the stale event is still recorded");
    }

    [Fact]
    public async Task OutOfOrder_UpdateBeforeCreate_StillEndsInTheNewestState()
    {
        // The update arrives first and creates the row; the create (older) arrives second and is stale.
        await Process(Sub("evt_updated", T0.AddHours(1), cancelAtEnd: true));
        (await Process(Sub("evt_created", T0))).Should().Be(BillingEventOutcome.Stale);

        await using InferenceApiDbContext db = NewDb();
        (await db.Subscriptions.SingleAsync()).CancelAtPeriodEnd.Should().BeTrue();
        (await Balance()).Should().Be(1_000_000);
    }

    [Fact]
    public async Task PerSeatPlan_ScalesTheRefillBySeats_AndEnforcesTheMinimum()
    {
        await Process(Sub("evt_1", T0, price: "pri_team", qty: 5));
        (await Balance()).Should().Be(7_500_000);
        await using (InferenceApiDbContext db = NewDb())
        {
            (await db.Subscriptions.SingleAsync()).Seats.Should().Be(5);
        }

        await Process(Sub("evt_2", T0.AddMonths(1), sub: "sub_2", price: "pri_team", qty: 1, start: T0.AddMonths(1)));
        await using InferenceApiDbContext db2 = NewDb();
        (await db2.Subscriptions.SingleAsync(s => s.ProviderSubscriptionId == "sub_2")).Seats.Should().Be(3, "Team has a 3-seat minimum");
    }

    [Fact]
    public async Task Cancel_MarksCanceled_GrantsNothing_AndKeepsTheRemainingCredits()
    {
        await Process(Sub("evt_1", T0));
        await Process(Sub("evt_2", T0.AddDays(2), status: SubscriptionStatus.Canceled));

        await using InferenceApiDbContext db = NewDb();
        (await db.Subscriptions.SingleAsync()).Status.Should().Be(SubscriptionStatus.Canceled);
        (await Balance()).Should().Be(1_000_000);
    }

    [Fact]
    public async Task PastDue_DoesNotRefill()
    {
        await Process(Sub("evt_1", T0));
        DateTime next = T0.AddMonths(1);
        await Process(Sub("evt_2", next, status: SubscriptionStatus.PastDue, start: next));

        (await Balance()).Should().Be(1_000_000);
        await using InferenceApiDbContext db = NewDb();
        (await db.Subscriptions.SingleAsync()).Status.Should().Be(SubscriptionStatus.PastDue);
    }

    [Fact]
    public async Task NewSubscription_ReplacesTheInternalTrialRow()
    {
        await using (InferenceApiDbContext seed = NewDb())
        {
            Plan trial = new() { Id = Guid.NewGuid(), Key = "trial", Name = "Trial", Kind = PlanKind.Trial };
            seed.Plans.Add(trial);
            seed.Subscriptions.Add(new Subscription { Id = Guid.NewGuid(), OrganizationId = _org, PlanId = trial.Id, Status = SubscriptionStatus.Active });
            await seed.SaveChangesAsync();
        }

        await Process(Sub("evt_1", T0));

        await using InferenceApiDbContext db = NewDb();
        List<Subscription> subs = await db.Subscriptions.ToListAsync();
        subs.Count(s => s.Status != SubscriptionStatus.Canceled).Should().Be(1);
        subs.Single(s => s.Provider == P).Status.Should().Be(SubscriptionStatus.Active);
    }

    [Fact]
    public async Task TopUp_GrantsOnce_ExpiresInTwelveMonths_EvenForADifferentEventOfTheSamePayment()
    {
        (await Process(TopUp("evt_a", "txn_1"))).Should().Be(BillingEventOutcome.Applied);
        await Process(TopUp("evt_a", "txn_1"));
        await Process(TopUp("evt_b", "txn_1"));

        (await Balance()).Should().Be(1_000_000);
        await using InferenceApiDbContext db = NewDb();
        CreditGrant grant = await db.CreditGrants.SingleAsync();
        grant.Source.Should().Be(CreditGrantSource.TopUp);
        grant.ExpiresAt.Should().Be(T0.AddDays(1).AddMonths(12));

        await Process(TopUp("evt_c", "txn_2", 500));
        (await Balance()).Should().Be(1_500_000);
    }

    [Fact]
    public async Task TopUp_ForAnUnknownOrganization_IsRecordedAndNotGranted()
    {
        BillingEvent evt = TopUp("evt_a", "txn_1") with { OrganizationId = Guid.NewGuid() };

        (await Process(evt)).Should().Be(BillingEventOutcome.Unresolved);

        (await Balance()).Should().Be(0);
        await using InferenceApiDbContext db = NewDb();
        (await db.BillingWebhookEvents.SingleAsync()).Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FullRefundOfATopUp_ClawsBackOnce_PartialRefundNeedsAHuman()
    {
        await Process(TopUp("evt_a", "txn_1"));

        (await Process(Refund("evt_r0", "txn_1", "adj_0", full: false))).Should().Be(BillingEventOutcome.Applied);
        (await Balance()).Should().Be(1_000_000, "a partial refund is flagged, not guessed at");

        await Process(Refund("evt_r1", "txn_1", "adj_1"));
        await Process(Refund("evt_r2", "txn_1", "adj_1"));
        (await Balance()).Should().Be(0);
    }

    [Fact]
    public async Task RefundOfSomethingThatIsNotATopUp_ChangesNoCredits()
    {
        await Process(Sub("evt_1", T0));

        await Process(Refund("evt_r", "txn_subscription", "adj_1"));

        (await Balance()).Should().Be(1_000_000);
    }

    [Fact]
    public async Task UnknownPrice_OnANewSubscription_IsRecordedUnresolved()
    {
        (await Process(Sub("evt_1", T0, price: "pri_nope"))).Should().Be(BillingEventOutcome.Unresolved);

        await using InferenceApiDbContext db = NewDb();
        (await db.Subscriptions.CountAsync()).Should().Be(0);
        (await Balance()).Should().Be(0);
    }

    [Fact]
    public async Task OrganizationIsFoundFromTheStoredCustomer_WhenTheEventCarriesNoCustomData()
    {
        await Process(Sub("evt_1", T0));
        BillingEvent renewal = Sub("evt_2", T0.AddMonths(1), sub: "sub_other", start: T0.AddMonths(1)) with { OrganizationId = null };

        (await Process(renewal)).Should().Be(BillingEventOutcome.Applied);

        await using InferenceApiDbContext db = NewDb();
        (await db.Subscriptions.CountAsync(s => s.OrganizationId == _org)).Should().BeGreaterThan(1);
    }

    // ------------------------------------------------------------------ plan changes inside a period

    private async Task<Guid> PlanId(string key)
    {
        await using InferenceApiDbContext db = NewDb();
        return (await db.Plans.SingleAsync(p => p.Key == key)).Id;
    }

    private async Task<Subscription> Row()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.Subscriptions.AsNoTracking().SingleAsync();
    }

    private async Task<List<CreditGrant>> Grants()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.CreditGrants.AsNoTracking().OrderBy(g => g.ExpiresAt).ThenByDescending(g => g.AmountMillis).ToListAsync();
    }

    /// <summary>
    /// The paid proration transaction of a same-period upgrade (Paddle <c>transaction.completed</c>, origin
    /// <c>subscription_update</c>). <paramref name="price"/>/<paramref name="qty"/> is the item it charged
    /// positively (the upgrade's target); it pairs only with an upgrade to exactly those items (R-2).
    /// </summary>
    private BillingEvent Paid(string id, string txn, DateTime at, string sub = "sub_1", DateTime? prorationStart = null, long amount = 2500,
        string price = "pri_pro", int qty = 1, string currency = "USD", long? fee = null, bool paid = true) => new()
        {
            Items = [new BillingLineItem(price, qty)],
            Provider = P,
            EventId = id,
            EventType = "transaction.completed",
            OccurredAt = at,
            Kind = BillingEventKind.UpgradePaid,
            OrganizationId = _org,
            CustomerId = "ctm_1",
            SubscriptionId = sub,
            TransactionId = txn,
            ProrationStartsAt = prorationStart,
            AmountMinor = amount,
            CurrencyCode = currency,
            FeeMinor = fee,
            IsPaidTransaction = paid
        };

    /// <summary>
    /// A plain subscription payment (Paddle <c>transaction.completed</c> for a subscription, no discount):
    /// the kind takes no credit action, but the money is still recorded.
    /// </summary>
    private BillingEvent SubscriptionPayment(string id, string txn, DateTime at, string sub = "sub_1",
        long amount = 2900, string currency = "USD", long? fee = null) => new()
        {
            Provider = P,
            EventId = id,
            EventType = "transaction.completed",
            OccurredAt = at,
            Kind = BillingEventKind.Ignored,
            OrganizationId = _org,
            CustomerId = "ctm_1",
            SubscriptionId = sub,
            TransactionId = txn,
            AmountMinor = amount,
            CurrencyCode = currency,
            FeeMinor = fee,
            IsPaidTransaction = true
        };

    /// <summary>Every <c>billing_payments</c> row, in insertion order.</summary>
    private async Task<List<BillingPayment>> Payments()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.BillingPayments.AsNoTracking().OrderBy(p => p.CreatedAt).ThenBy(p => p.ProviderTransactionId).ToListAsync();
    }

    private async Task<List<BillingPendingUpgradeGrant>> Pending()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.BillingPendingUpgradeGrants.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync();
    }

    /// <summary>A period of exactly 30 days (Nov 6 → Dec 6), so "one day left" is exactly 1/30.</summary>
    private static readonly DateTime S30 = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime E30 = S30.AddMonths(1);

    [Fact]
    public async Task SamePeriodUpgrade_AppliesNow_ButGrantsNothingUntilTheProrationIsPaid()
    {
        await Process(Sub("evt_1", T0.AddMinutes(-5)));

        (await Process(Sub("evt_2", T0, price: "pri_pro"))).Should().Be(BillingEventOutcome.Applied);

        (await Row()).PlanId.Should().Be(await PlanId("pro"));
        (await Balance()).Should().Be(1_000_000, "no paid proration transaction yet");
        BillingPendingUpgradeGrant pending = (await Pending()).Single();
        pending.OrganizationId.Should().Be(_org);
        pending.Provider.Should().Be(P);
        pending.ProviderSubscriptionId.Should().Be("sub_1");
        pending.PeriodStart.Should().Be(T0);
        pending.PeriodEnd.Should().Be(T0.AddMonths(1));
        pending.OldEntitlementMillis.Should().Be(1_000_000);
        pending.NewEntitlementMillis.Should().Be(3_500_000);
        pending.EffectiveAt.Should().Be(T0);
        pending.TransactionId.Should().BeNull();
        pending.GrantedAt.Should().BeNull();
    }

    [Fact]
    public async Task SamePeriodUpgrade_GrantsTheDifferenceOnceItIsPaid_ExpiringAtPeriodEnd_KeyedByTheTransaction()
    {
        await Process(Sub("evt_1", T0.AddMinutes(-5)));
        await Process(Sub("evt_2", T0, price: "pri_pro"));

        (await Process(Paid("evt_p", "txn_up", T0.AddMinutes(1)))).Should().Be(BillingEventOutcome.Applied);
        // The provider's retry of the update, and the payment again under another event id, grant nothing more.
        await Process(Sub("evt_3", T0.AddHours(2), price: "pri_pro"));
        await Process(Paid("evt_p2", "txn_up", T0.AddMinutes(2)));

        (await Balance()).Should().Be(3_500_000, "Creator's 1,000 plus the 2,500 difference to Pro");
        CreditGrant upgrade = (await Grants()).Single(g => g.AmountMillis == 2_500_000);
        upgrade.Source.Should().Be(CreditGrantSource.Plan);
        upgrade.ExpiresAt.Should().Be(T0.AddMonths(1));
        await using InferenceApiDbContext db = NewDb();
        (await db.CreditLedgerEntries.SingleAsync(e => e.GrantId == upgrade.Id)).IdempotencyKey.Should().Be("plan-upgrade:fake:txn_up");
        BillingPendingUpgradeGrant row = (await Pending()).Single();
        row.TransactionId.Should().Be("txn_up");
        row.GrantedMillis.Should().Be(2_500_000);
    }

    [Fact]
    public async Task UpgradePayment_DoesNotMoveTheSubscriptionsEventClock()
    {
        await Process(Sub("evt_1", T0));
        await Process(Sub("evt_2", T0.AddHours(1), price: "pri_pro"));

        await Process(Paid("evt_p", "txn_up", T0.AddHours(3)));

        (await Row()).LastEventAt.Should().Be(T0.AddHours(1));
        (await Process(Sub("evt_3", T0.AddHours(2), price: "pri_pro", cancelAtEnd: true))).Should().Be(BillingEventOutcome.Applied);
    }

    [Fact]
    public async Task SamePeriodUpgrade_AtMidPeriod_GrantsHalfTheDifference()
    {
        await Process(Sub("evt_1", T0));
        DateTime end = T0.AddMonths(1);
        DateTime mid = T0 + (end - T0) / 2;

        await Process(Sub("evt_2", mid, price: "pri_pro"));
        await Process(Paid("evt_p", "txn_up", mid.AddMinutes(1)));

        (await Balance()).Should().Be(1_000_000 + 1_250_000);
        (await Row()).PlanId.Should().Be(await PlanId("pro"));
    }

    /// <summary>F-1: one day of a 30-day period left grants floor(difference / 30), and only after payment.</summary>
    [Fact]
    public async Task LastDayUpgrade_GrantsAThirtiethOfTheDifference_AfterPayment()
    {
        await Process(Sub("evt_1", S30, start: S30));
        DateTime lastDay = E30.AddDays(-1);

        await Process(Sub("evt_2", lastDay, price: "pri_pro", start: S30));
        (await Balance()).Should().Be(1_000_000);
        await Process(Paid("evt_p", "txn_up", lastDay.AddMinutes(1)));

        (await Grants()).Select(g => g.AmountMillis).Should().Equal(1_000_000, 2_500_000 / 30);
    }

    [Fact]
    public async Task SamePeriodUpgrade_InTheLastHour_GrantsOneHoursShare_AndAReplayGrantsNothingMore()
    {
        await Process(Sub("evt_1", T0));
        DateTime end = T0.AddMonths(1);

        await Process(Sub("evt_2", end.AddHours(-1), price: "pri_pro"));
        await Process(Paid("evt_p", "txn_up", end.AddMinutes(-59)));
        await Process(Sub("evt_3", end.AddMinutes(-30), price: "pri_pro"));
        await Process(Paid("evt_p2", "txn_up", end.AddMinutes(-20)));

        // The period is 31 days (744 h): 2,500,000 / 744 = 3,360.2, rounded down to whole millicredits.
        (await Grants()).Select(g => g.AmountMillis).Should().Equal(1_000_000, 3_360);
    }

    [Fact]
    public async Task ThePaymentsProrationStart_IsTheInstantTheGrantIsProratedFrom()
    {
        await Process(Sub("evt_1", S30, start: S30));
        // The update is stamped a moment before Paddle's proration start and the charge only completes a
        // day later: Paddle's instant wins over both (and is never earlier than the upgrade itself).
        await Process(Sub("evt_2", S30.AddDays(15).AddMinutes(-2), price: "pri_pro", start: S30));

        await Process(Paid("evt_p", "txn_up", S30.AddDays(16), prorationStart: S30.AddDays(15)));

        (await Balance()).Should().Be(1_000_000 + 1_250_000);
    }

    [Fact]
    public async Task FailedUpgradePayment_GrantsNothing()
    {
        await Process(Sub("evt_1", T0));
        await Process(Sub("evt_2", T0.AddHours(1), price: "pri_pro"));

        BillingEvent failed = new() { Provider = P, EventId = "evt_f", EventType = "transaction.payment_failed", OccurredAt = T0.AddHours(1).AddMinutes(1) };
        (await Process(failed)).Should().Be(BillingEventOutcome.Applied);

        (await Balance()).Should().Be(1_000_000);
        (await Pending()).Single().GrantedAt.Should().BeNull();
    }

    [Fact]
    public async Task OrderIndependence_PaidBeforeTheUpdate_IsRemembered_AndTheUpdateGrantsAtOnce_Once()
    {
        await Process(Sub("evt_1", S30, start: S30));

        (await Process(Paid("evt_p", "txn_up", S30.AddDays(15).AddMinutes(1), prorationStart: S30.AddDays(15)))).Should().Be(BillingEventOutcome.Applied);
        (await Balance()).Should().Be(1_000_000, "a payment with no upgrade to match grants nothing yet");
        (await Process(Paid("evt_p", "txn_up", S30.AddDays(15).AddMinutes(1)))).Should().Be(BillingEventOutcome.Duplicate);
        await Process(Paid("evt_p2", "txn_up", S30.AddDays(15).AddMinutes(2)));
        (await Pending()).Should().ContainSingle();

        await Process(Sub("evt_2", S30.AddDays(15), price: "pri_pro", start: S30));
        (await Balance()).Should().Be(1_000_000 + 1_250_000);

        (await Process(Sub("evt_2", S30.AddDays(15), price: "pri_pro", start: S30))).Should().Be(BillingEventOutcome.Duplicate);
        await Process(Sub("evt_3", S30.AddDays(15).AddMinutes(5), price: "pri_pro", start: S30));
        await Process(Paid("evt_p3", "txn_up", S30.AddDays(15).AddMinutes(6)));
        (await Balance()).Should().Be(1_000_000 + 1_250_000);
        (await Pending()).Should().ContainSingle().Which.GrantedMillis.Should().Be(1_250_000);
    }

    [Fact]
    public async Task OrderIndependence_UpdateThenPaid_GrantsOnce_UnderRedeliveryOfEither()
    {
        await Process(Sub("evt_1", S30, start: S30));

        await Process(Sub("evt_2", S30.AddDays(15), price: "pri_pro", start: S30));
        (await Process(Sub("evt_2", S30.AddDays(15), price: "pri_pro", start: S30))).Should().Be(BillingEventOutcome.Duplicate);
        await Process(Paid("evt_p", "txn_up", S30.AddDays(15).AddMinutes(1)));
        (await Process(Paid("evt_p", "txn_up", S30.AddDays(15).AddMinutes(1)))).Should().Be(BillingEventOutcome.Duplicate);
        await Process(Paid("evt_p2", "txn_up", S30.AddDays(15).AddMinutes(2)));
        await Process(Sub("evt_3", S30.AddDays(15).AddMinutes(3), price: "pri_pro", start: S30));

        (await Balance()).Should().Be(1_000_000 + 1_250_000);
        (await Pending()).Should().ContainSingle();
    }

    // ------------------------------------------------------------------ F-1 re-review: correlated pairing

    /// <summary>
    /// Auditor case A. An early upgrade's <c>subscription.updated</c> is reordered behind a newer event at
    /// the period boundary and dropped as stale, so its paid proration transaction (prorated from the
    /// start of the period) stays unmatched. A last-day upgrade must pair with its own payment and grant
    /// about 1/30 of the difference, never the ~29/30 the stranger's proration start would give.
    /// </summary>
    [Fact]
    public Task ReorderAtAPeriodBoundary_LastDayUpgrade_DoesNotPairWithAStartOfPeriodPayment() => ReorderAtAPeriodBoundaryThenLastDayUpgrade();

    private async Task ReorderAtAPeriodBoundaryThenLastDayUpgrade()
    {
        await Process(Sub("evt_1", S30, start: S30));
        await Process(Sub("evt_keep", S30.AddHours(2), start: S30));
        (await Process(Sub("evt_up0", S30.AddHours(1), price: "pri_pro", start: S30))).Should().Be(BillingEventOutcome.Stale);
        await Process(Paid("evt_p0", "txn_early", S30.AddHours(1).AddMinutes(1), prorationStart: S30.AddHours(1)));
        (await Balance()).Should().Be(1_000_000);

        DateTime lastDay = E30.AddDays(-1);
        await Process(Sub("evt_2", lastDay, price: "pri_pro", start: S30));
        (await Balance()).Should().Be(1_000_000, "the last-day upgrade's own payment has not arrived yet");
        await Process(Paid("evt_p", "txn_last", lastDay.AddMinutes(1), prorationStart: lastDay));

        (await Balance()).Should().Be(1_000_000 + 2_500_000 / 30);
        List<BillingPendingUpgradeGrant> rows = await Pending();
        rows.Single(r => r.TransactionId == "txn_last").GrantedMillis.Should().Be(2_500_000 / 30);
        rows.Single(r => r.TransactionId == "txn_early").GrantedAt.Should().BeNull("a payment never grants for a stranger's upgrade");
    }

    /// <summary>
    /// Auditor case B. A day-1 seat upgrade's <c>subscription.updated</c> and <c>transaction.completed</c>
    /// ran concurrently without serialisation, so each committed its own unpaired row. A last-day 4 → 40
    /// seat upgrade must not pair with that day-1 payment (which would grant ~29/30 of the 36-seat
    /// difference); it grants from its own payment's instant.
    /// </summary>
    [Fact]
    public async Task ConcurrentlyCommittedUnpairedRows_LastDaySeatUpgrade_DoesNotGrantFromTheDayOneInstant()
    {
        DateTime day1 = S30.AddDays(1), lastDay = E30.AddDays(-1);
        await Process(Sub("evt_1", S30, price: "pri_team", qty: 3, start: S30));
        await Process(Sub("evt_2", day1, price: "pri_team", qty: 4, start: S30));
        await using (InferenceApiDbContext db = NewDb())
        {
            // What the concurrent transaction.completed committed: it saw no upgrade row yet.
            db.BillingPendingUpgradeGrants.Add(new BillingPendingUpgradeGrant
            {
                Id = Guid.NewGuid(),
                OrganizationId = _org,
                Provider = P,
                ProviderSubscriptionId = "sub_1",
                TransactionId = "txn_day1",
                PaidAt = day1.AddMinutes(1),
                ProrationStartsAt = day1,
                CreatedAt = day1.AddMinutes(1)
            });
            await db.SaveChangesAsync();
        }

        await Process(Sub("evt_3", lastDay, price: "pri_team", qty: 40, start: S30));
        await Process(Paid("evt_p", "txn_last", lastDay.AddMinutes(1), prorationStart: lastDay, price: "pri_team", qty: 40));

        long seats3 = 4_500_000, seats40 = 60_000_000;
        (await Balance()).Should().Be(seats3 + BillingEventProcessor.ProratedUpgradeGrantMillis(seats3, seats40, S30, E30, lastDay));
        (await Pending()).Single(r => r.TransactionId == "txn_day1").GrantedAt.Should().BeNull();
    }

    /// <summary>A payment whose proration start is within the tolerance of its upgrade pairs, in either order, exactly once.</summary>
    [Theory]
    [InlineData(true, 4)]
    [InlineData(false, 4)]
    [InlineData(true, -4)]
    [InlineData(false, -4)]
    public async Task PaymentWithinTheTolerance_PairsAndGrantsOnce_InEitherOrder(bool paidFirst, int offsetMinutes)
    {
        DateTime upgradeAt = S30.AddDays(15);
        BillingEvent upgrade = Sub("evt_2", upgradeAt, price: "pri_pro", start: S30);
        BillingEvent paid = Paid("evt_p", "txn_up", upgradeAt.AddMinutes(6), prorationStart: upgradeAt.AddMinutes(offsetMinutes));
        await Process(Sub("evt_1", S30, start: S30));

        await Process(paidFirst ? paid : upgrade);
        await Process(paidFirst ? upgrade : paid);
        await Process(Paid("evt_p2", "txn_up", upgradeAt.AddMinutes(7), prorationStart: upgradeAt.AddMinutes(offsetMinutes)));
        await Process(Sub("evt_3", upgradeAt.AddMinutes(8), price: "pri_pro", start: S30));

        // Backstop: the grant runs from max(proration start, upgrade instant).
        DateTime instant = offsetMinutes > 0 ? upgradeAt.AddMinutes(offsetMinutes) : upgradeAt;
        (await Balance()).Should().Be(1_000_000 + BillingEventProcessor.ProratedUpgradeGrantMillis(1_000_000, 3_500_000, S30, E30, instant));
        (await Pending()).Should().ContainSingle().Which.TransactionId.Should().Be("txn_up");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PaymentOutsideTheTolerance_StaysUnmatched_AndGrantsNothing(bool paidFirst)
    {
        DateTime upgradeAt = S30.AddDays(15);
        BillingEvent upgrade = Sub("evt_2", upgradeAt, price: "pri_pro", start: S30);
        BillingEvent paid = Paid("evt_p", "txn_other", upgradeAt.AddHours(3), prorationStart: upgradeAt.AddHours(3));
        await Process(Sub("evt_1", S30, start: S30));

        await Process(paidFirst ? paid : upgrade);
        await Process(paidFirst ? upgrade : paid);

        (await Balance()).Should().Be(1_000_000);
        (await Pending()).Should().HaveCount(2).And.OnlyContain(r => r.GrantedAt == null);
    }

    /// <summary>A refund follows the corrected pairing: it claws back its own transaction's grant, never another one.</summary>
    [Fact]
    public async Task RefundOfAStrangerPayment_ClawsBackNothing_AndTheRealPaymentsRefundTakesItsOwnGrant()
    {
        await ReorderAtAPeriodBoundaryThenLastDayUpgrade();

        await Process(Refund("evt_r0", "txn_early", "adj_0"));
        (await Balance()).Should().Be(1_000_000 + 2_500_000 / 30);

        await Process(Refund("evt_r1", "txn_last", "adj_1"));
        (await Balance()).Should().Be(1_000_000);
    }

    [Fact]
    public void SubscriptionLockKey_IsStable_AndDistinctPerProviderAndSubscription()
    {
        BillingEventProcessor.SubscriptionLockKey("paddle", "sub_1").Should().Be(BillingEventProcessor.SubscriptionLockKey("paddle", "sub_1"));
        BillingEventProcessor.SubscriptionLockKey("paddle", "sub_1").Should().NotBe(BillingEventProcessor.SubscriptionLockKey("paddle", "sub_2"));
        BillingEventProcessor.SubscriptionLockKey("paddle", "sub_1").Should().NotBe(BillingEventProcessor.SubscriptionLockKey("fake", "sub_1"));
    }

    /// <summary>N-3: the per-transaction lock key is the first 8 bytes of SHA-256 of <c>billing-transaction:{provider}:{transactionId}</c>, never a subscription's key.</summary>
    [Fact]
    public void TransactionLockKey_IsStable_DistinctPerProviderAndTransaction_AndFromTheSubscriptionKey()
    {
        long expected = BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("billing-transaction:paddle:txn_1")), 0);
        BillingEventProcessor.TransactionLockKey("paddle", "txn_1").Should().Be(expected);
        BillingEventProcessor.TransactionLockKey("paddle", "txn_1").Should().NotBe(BillingEventProcessor.TransactionLockKey("paddle", "txn_2"));
        BillingEventProcessor.TransactionLockKey("paddle", "txn_1").Should().NotBe(BillingEventProcessor.TransactionLockKey("fake", "txn_1"));
        BillingEventProcessor.TransactionLockKey("paddle", "x").Should().NotBe(BillingEventProcessor.SubscriptionLockKey("paddle", "x"));
    }

    /// <summary>
    /// N-3 on a real Postgres (set <c>REELBOLT_TEST_PG</c>; skipped silently otherwise): a top-up's
    /// <c>transaction.completed</c> and its full refund delivered at the same moment on two connections
    /// run one after the other under the per-transaction lock, so the top-up never both grants and is
    /// tombstoned: whichever order they take, the balance ends at zero.
    /// </summary>
    [Fact]
    public async Task ConcurrentTopUpAndItsRefund_OnARealPostgres_NeverLeaveTheGrantStanding()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_n3_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options;
            const int Payments = 32;
            List<Guid> orgs = Enumerable.Range(0, Payments).Select(_ => Guid.NewGuid()).ToList();
            await using (InferenceApiDbContext setup = new(options))
            {
                await setup.GetService<IMigrator>().MigrateAsync();
                setup.Organizations.AddRange(orgs.Select(o => new Organization { Id = o, Name = "o", Slug = "o-" + o.ToString("N"), Kind = OrganizationKind.Team }));
                await setup.SaveChangesAsync();
            }

            async Task Run(BillingEvent evt)
            {
                await using InferenceApiDbContext db = new(options);
                await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, new FixedClock(T0)).ProcessAsync(evt, "{}");
            }

            await Task.WhenAll(Enumerable.Range(0, Payments).SelectMany(i => new[]
            {
                Task.Run(() => Run(TopUp($"evt_t{i}", $"txn_{i}") with { OrganizationId = orgs[i], CustomerId = null })),
                Task.Run(() => Run(Refund($"evt_r{i}", $"txn_{i}", $"adj_{i}") with { OrganizationId = orgs[i] }))
            }));

            await using InferenceApiDbContext verify = new(options);
            foreach (Guid org in orgs)
            {
                (await verify.CreditAccounts.AsNoTracking().Where(a => a.OrganizationId == org).Select(a => a.BalanceMillis).SingleOrDefaultAsync()).Should().Be(0, "a refunded top-up never leaves its grant standing");
            }
        }
        finally
        {
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Auditor case B on a real Postgres (set <c>REELBOLT_TEST_PG</c>; skipped silently otherwise): a
    /// <c>subscription.updated</c> and the <c>transaction.completed</c> of the same upgrade, delivered at
    /// the same moment on two connections, pair on one row and grant once — the per-subscription advisory
    /// lock makes the second see the first's row instead of both inserting an unpaired one.
    /// </summary>
    [Fact]
    public async Task ConcurrentUpdateAndPayment_OnARealPostgres_PairOnOneRow_AndGrantOnce()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_f1_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options;
            const int Subscriptions = 24;
            List<Guid> orgs = Enumerable.Range(0, Subscriptions).Select(_ => Guid.NewGuid()).ToList();
            string suffix = Guid.NewGuid().ToString("N")[..8];
            await using (InferenceApiDbContext setup = new(options))
            {
                await setup.GetService<IMigrator>().MigrateAsync();
                setup.Organizations.AddRange(orgs.Select(o => new Organization { Id = o, Name = "o", Slug = "o-" + o.ToString("N"), Kind = OrganizationKind.Team }));
                setup.Plans.AddRange(
                    new Plan { Id = Guid.NewGuid(), Key = "creator-" + suffix, Name = "Creator", Kind = PlanKind.Subscription, PriceMonthlyUsd = 29, MonthlyCreditsMillis = 1_000_000, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_creator"}}""" },
                    new Plan { Id = Guid.NewGuid(), Key = "pro-" + suffix, Name = "Pro", Kind = PlanKind.Subscription, PriceMonthlyUsd = 79, MonthlyCreditsMillis = 3_500_000, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_pro"}}""" });
                await setup.SaveChangesAsync();
            }

            async Task Run(BillingEvent evt)
            {
                await using InferenceApiDbContext db = new(options);
                await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, new FixedClock(S30.AddDays(15))).ProcessAsync(evt, "{}");
            }

            DateTime upgradeAt = S30.AddDays(15);
            for (int i = 0; i < Subscriptions; i++)
            {
                await Run(Sub($"evt_c{i}", S30, sub: $"sub_{i}", start: S30, org: orgs[i]));
            }

            await Task.WhenAll(Enumerable.Range(0, Subscriptions).SelectMany(i => new[]
            {
                Task.Run(() => Run(Sub($"evt_u{i}", upgradeAt, sub: $"sub_{i}", price: "pri_pro", start: S30, org: orgs[i]))),
                Task.Run(() => Run(Paid($"evt_p{i}", $"txn_{i}", upgradeAt.AddMinutes(1), sub: $"sub_{i}", prorationStart: upgradeAt) with { OrganizationId = orgs[i] }))
            }));

            await using InferenceApiDbContext verify = new(options);
            List<BillingPendingUpgradeGrant> rows = await verify.BillingPendingUpgradeGrants.AsNoTracking().ToListAsync();
            rows.Should().HaveCount(Subscriptions, "one row per upgrade: the update and its payment met on it");
            rows.Should().OnlyContain(r => r.TransactionId != null && r.NewEntitlementMillis != null && r.GrantedMillis == 1_250_000);
            foreach (Guid org in orgs)
            {
                (await verify.CreditAccounts.AsNoTracking().Where(a => a.OrganizationId == org).Select(a => a.BalanceMillis).SingleOrDefaultAsync()).Should().Be(1_000_000 + 1_250_000);
            }
        }
        finally
        {
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// F-1 cap: Creator → Pro → (downgrade to Creator, held) → keep → Team → (seat decrease, held) inside
    /// one period never grants more than going Creator → Team at the first upgrade's instant, and the
    /// Pro → Team step adds only the prorated Team − Pro difference.
    /// </summary>
    [Fact]
    public async Task UpgradeDowngradeCycles_InOnePeriod_AreCappedAtTheHighestPlansProratedDifference()
    {
        long creator = 1_000_000, pro = 3_500_000, team4 = 6_000_000;
        DateTime first = S30.AddDays(5), second = S30.AddDays(10);
        await Process(Sub("evt_1", S30, start: S30));

        await Process(Sub("evt_2", first, price: "pri_pro", start: S30));
        await Process(Paid("evt_p1", "txn_1", first.AddMinutes(1)));
        await Process(Sub("evt_3", S30.AddDays(6), price: "pri_creator", start: S30));
        await Process(Sub("evt_4", S30.AddDays(7), price: "pri_pro", start: S30));
        long afterPro = await Balance();
        await Process(Sub("evt_5", second, price: "pri_team", qty: 4, start: S30));
        await Process(Paid("evt_p2", "txn_2", second.AddMinutes(1), price: "pri_team", qty: 4));
        await Process(Sub("evt_6", S30.AddDays(12), price: "pri_team", qty: 3, start: S30));

        long upgrades = await Balance() - creator;
        upgrades.Should().BeLessThanOrEqualTo(BillingEventProcessor.ProratedUpgradeGrantMillis(creator, team4, S30, E30, first));
        (await Balance() - afterPro).Should().Be(BillingEventProcessor.ProratedUpgradeGrantMillis(pro, team4, S30, E30, second));
        (await Row()).Seats.Should().Be(4);
    }

    /// <summary>
    /// A price-up that lowers the credits (Pro → Studio) must not reset the high-water mark: the next
    /// upgrade (Studio → Team) only earns what Team adds over the Pro already granted this period.
    /// </summary>
    [Fact]
    public async Task UpgradeCap_HoldsAcrossAPriceUpThatLowersTheCredits()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            db.Plans.Add(new Plan { Id = Guid.NewGuid(), Key = "studio", Name = "Studio", Kind = PlanKind.Subscription, PriceMonthlyUsd = 99, MonthlyCreditsMillis = 500_000, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_studio"}}""" });
            await db.SaveChangesAsync();
        }

        await Process(Sub("evt_1", S30, start: S30));
        await Process(Sub("evt_2", S30.AddDays(5), price: "pri_pro", start: S30));
        await Process(Paid("evt_p1", "txn_1", S30.AddDays(5).AddMinutes(1)));
        await Process(Sub("evt_3", S30.AddDays(6), price: "pri_studio", start: S30));
        await Process(Paid("evt_p2", "txn_2", S30.AddDays(6).AddMinutes(1), price: "pri_studio"));
        long beforeTeam = await Balance();

        await Process(Sub("evt_4", S30.AddDays(10), price: "pri_team", qty: 3, start: S30));
        await Process(Paid("evt_p3", "txn_3", S30.AddDays(10).AddMinutes(1), price: "pri_team", qty: 3));

        (await Balance() - beforeTeam).Should().Be(BillingEventProcessor.ProratedUpgradeGrantMillis(3_500_000, 4_500_000, S30, E30, S30.AddDays(10)));
    }

    /// <summary>
    /// Re-audit F3: a trialing subscription has paid nothing yet, so an upgrade during the trial applies
    /// the plan but grants no difference (an org could otherwise take the grant and cancel before paying).
    /// </summary>
    [Fact]
    public async Task SamePeriodUpgrade_WhileTrialing_AppliesThePlan_AndGrantsNothing()
    {
        await Process(Sub("evt_1", T0, status: SubscriptionStatus.Trialing));

        await Process(Sub("evt_2", T0.AddHours(1), status: SubscriptionStatus.Trialing, price: "pri_pro"));

        (await Row()).PlanId.Should().Be(await PlanId("pro"));
        (await Grants()).Select(g => g.AmountMillis).Should().Equal(1_000_000);
    }

    [Fact]
    public async Task SamePeriodUpgrade_WithoutAPeriodEnd_AppliesThePlan_AndGrantsNothing()
    {
        await Process(Sub("evt_1", T0));

        await Process(Sub("evt_2", T0.AddHours(1), price: "pri_pro") with { PeriodEnd = null });
        await Process(Paid("evt_p", "txn_up", T0.AddHours(1).AddMinutes(1)));

        (await Row()).PlanId.Should().Be(await PlanId("pro"));
        (await Balance()).Should().Be(1_000_000);
    }

    // ------------------------------------------------------------------ upgrade refunds

    private async Task Spend(long millis, DateTime at)
    {
        await using InferenceApiDbContext db = NewDb();
        (await new CreditLedger(db, new FixedClock(at)).AdjustAsync(_org, -millis, $"spend:{Guid.NewGuid()}")).Ok.Should().BeTrue();
    }

    private BillingEvent Chargeback(string id, string txn, string adj) => Refund(id, txn, adj) with { EventType = "adjustment.created" };

    /// <summary>
    /// Creator, then Pro at mid-period paid with txn_up: a 1,000,000 refill plus a 1,250,000 upgrade grant.
    /// Each event is processed at its own time, so the refill is the older bucket and is drawn first.
    /// </summary>
    private async Task MidPeriodPaidUpgrade()
    {
        await Process(Sub("evt_1", S30, start: S30), now: S30);
        await Process(Sub("evt_2", S30.AddDays(15), price: "pri_pro", start: S30), now: S30.AddDays(15));
        await Process(Paid("evt_p", "txn_up", S30.AddDays(15).AddMinutes(1)), now: S30.AddDays(15).AddMinutes(1));
        (await Balance()).Should().Be(2_250_000);
    }

    [Fact]
    public async Task FullRefundOfAnUpgradePayment_AfterPartialSpend_ClawsBackTheUnspentPart_Once()
    {
        await MidPeriodPaidUpgrade();
        // The refill is drawn first (same expiry, older), so 500,000 of the 1,250,000 upgrade grant is spent.
        await Spend(1_500_000, S30.AddDays(16));
        (await Grants()).Single(g => g.AmountMillis == 1_250_000).RemainingMillis.Should().Be(750_000);

        (await Process(Refund("evt_r", "txn_up", "adj_1"))).Should().Be(BillingEventOutcome.Applied);
        (await Process(Refund("evt_r", "txn_up", "adj_1"))).Should().Be(BillingEventOutcome.Duplicate);
        await Process(Refund("evt_r2", "txn_up", "adj_1"));

        (await Balance()).Should().Be(0, "750,000 left of the upgrade grant is taken back, the spent 500,000 is not");
        await using InferenceApiDbContext db = NewDb();
        CreditLedgerEntry clawback = await db.CreditLedgerEntries.SingleAsync(e => e.IdempotencyKey == "refund:fake:adj_1");
        clawback.BalanceDeltaMillis.Should().Be(-750_000);
    }

    [Fact]
    public async Task ChargebackOfAnUpgradePayment_ClawsBackTheWholeUnspentGrant_AndALaterRefundTakesNothingMore()
    {
        await MidPeriodPaidUpgrade();

        (await Process(Chargeback("evt_cb", "txn_up", "adj_cb"))).Should().Be(BillingEventOutcome.Applied);
        (await Balance()).Should().Be(1_000_000);

        await Process(Refund("evt_r", "txn_up", "adj_2"));
        (await Balance()).Should().Be(1_000_000, "the grant was already taken back once");
    }

    [Fact]
    public async Task PartialRefundOfAnUpgradePayment_ChangesNothing_AndAsksForAReview()
    {
        await MidPeriodPaidUpgrade();

        (await Process(Refund("evt_r", "txn_up", "adj_1", full: false))).Should().Be(BillingEventOutcome.Applied);

        (await Balance()).Should().Be(2_250_000);
        await using InferenceApiDbContext db = NewDb();
        (await db.BillingWebhookEvents.SingleAsync(e => e.EventId == "evt_r")).Error.Should().Contain("review manually");
    }

    [Theory]
    [InlineData(0, 2_000)]          // at the start: the full difference
    [InlineData(15, 1_000)]         // half way
    [InlineData(30, 0)]             // at the end: nothing left
    [InlineData(-5, 2_000)]         // before the start clamps to the full period
    [InlineData(45, 0)]             // after the end clamps to nothing
    public void ProratedUpgradeGrantMillis_ScalesTheDifferenceByTheRemainingPeriod(int day, long expected)
    {
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        BillingEventProcessor.ProratedUpgradeGrantMillis(1_000, 3_000, start, start.AddDays(30), start.AddDays(day))
            .Should().Be(expected);
    }

    [Fact]
    public void ProratedUpgradeGrantMillis_IsZero_ForAFallOrAMissingBound_AndDoesNotOverflow()
    {
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        BillingEventProcessor.ProratedUpgradeGrantMillis(3_000, 1_000, start, start.AddDays(30), start).Should().Be(0);
        BillingEventProcessor.ProratedUpgradeGrantMillis(1_000, 3_000, null, start.AddDays(30), start).Should().Be(0);
        BillingEventProcessor.ProratedUpgradeGrantMillis(1_000, 3_000, start, null, start).Should().Be(0);
        BillingEventProcessor.ProratedUpgradeGrantMillis(0, 1_500_000_000, start, start.AddDays(31), start.AddDays(31) - TimeSpan.FromDays(31) / 3)
            .Should().Be(500_000_000);
    }

    [Fact]
    public async Task SamePeriodPriceUp_WithFewerCredits_AppliesNow_AndGrantsNothing()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            db.Plans.Add(new Plan { Id = Guid.NewGuid(), Key = "studio", Name = "Studio", Kind = PlanKind.Subscription, PriceMonthlyUsd = 99, MonthlyCreditsMillis = 500_000, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_studio"}}""" });
            await db.SaveChangesAsync();
        }

        await Process(Sub("evt_1", T0));
        await Process(Sub("evt_2", T0.AddHours(1), price: "pri_studio"));
        await Process(Paid("evt_p", "txn_up", T0.AddHours(1).AddMinutes(1), price: "pri_studio"));

        (await Row()).PlanId.Should().Be(await PlanId("studio"));
        (await Balance()).Should().Be(1_000_000);
    }

    [Fact]
    public async Task SamePeriodDowngrade_KeepsThePaidPlan_ThenTheRenewalAppliesItAndRefillsTheLowerAmount()
    {
        await Process(Sub("evt_1", T0, price: "pri_pro"));

        await Process(Sub("evt_2", T0.AddHours(1), price: "pri_creator"));

        Subscription held = await Row();
        held.PlanId.Should().Be(await PlanId("pro"), "Pro was paid for until the period ends");
        held.ScheduledPlanId.Should().Be(await PlanId("creator"));
        held.ScheduledSeats.Should().Be(1);
        held.ScheduledChangeAt.Should().Be(T0.AddMonths(1));
        (await Balance()).Should().Be(3_500_000);

        DateTime next = T0.AddMonths(1);
        await Process(Sub("evt_3", next, price: "pri_creator", start: next));

        Subscription renewed = await Row();
        renewed.PlanId.Should().Be(await PlanId("creator"));
        renewed.ScheduledPlanId.Should().BeNull();
        renewed.ScheduledSeats.Should().BeNull();
        renewed.ScheduledChangeAt.Should().BeNull();
        List<CreditGrant> grants = await Grants();
        grants.Select(g => g.AmountMillis).Should().Equal(3_500_000, 1_000_000);
        grants[1].ExpiresAt.Should().Be(next.AddMonths(1));
    }

    [Fact]
    public async Task OutOfOrder_RenewalBeforeTheDowngradeUpdate_AppliesTheLowerPlanOnce_AndDropsTheStaleUpdate()
    {
        await Process(Sub("evt_1", T0, price: "pri_pro"));
        DateTime next = T0.AddMonths(1);

        await Process(Sub("evt_3", next, price: "pri_creator", start: next));
        (await Process(Sub("evt_2", T0.AddHours(1), price: "pri_creator"))).Should().Be(BillingEventOutcome.Stale);

        Subscription row = await Row();
        row.PlanId.Should().Be(await PlanId("creator"));
        row.ScheduledPlanId.Should().BeNull();
        (await Grants()).Select(g => g.AmountMillis).Should().Equal(3_500_000, 1_000_000);
    }

    [Fact]
    public async Task KeepMyPlan_ASamePeriodEventBackOnTheCurrentPlan_ClearsTheScheduledDowngrade()
    {
        await Process(Sub("evt_1", T0, price: "pri_pro"));
        await Process(Sub("evt_2", T0.AddHours(1), price: "pri_creator"));

        await Process(Sub("evt_3", T0.AddHours(2), price: "pri_pro"));

        Subscription row = await Row();
        row.PlanId.Should().Be(await PlanId("pro"));
        row.ScheduledPlanId.Should().BeNull();
        row.ScheduledSeats.Should().BeNull();
        row.ScheduledChangeAt.Should().BeNull();
        (await Balance()).Should().Be(3_500_000, "going back to the plan already paid for grants nothing");
    }

    [Fact]
    public async Task SamePeriodSeatIncrease_GrantsTheExtraSeatsCredits()
    {
        await Process(Sub("evt_1", T0.AddMinutes(-5), price: "pri_team", qty: 3));
        (await Balance()).Should().Be(4_500_000);

        await Process(Sub("evt_2", T0, price: "pri_team", qty: 5));
        (await Balance()).Should().Be(4_500_000, "a seat increase is an upgrade: nothing until it is paid");
        await Process(Paid("evt_p", "txn_seats", T0.AddMinutes(1), prorationStart: T0, price: "pri_team", qty: 5));

        (await Balance()).Should().Be(7_500_000, "two more seats at 1,500 credits each");
        (await Row()).Seats.Should().Be(5);
    }

    [Fact]
    public async Task SamePeriodSeatDecrease_IsHeldUntilThePeriodEnds()
    {
        await Process(Sub("evt_1", T0, price: "pri_team", qty: 5));

        await Process(Sub("evt_2", T0.AddHours(1), price: "pri_team", qty: 3));

        Subscription row = await Row();
        row.Seats.Should().Be(5);
        row.ScheduledPlanId.Should().Be(await PlanId("team"));
        row.ScheduledSeats.Should().Be(3);
        (await Balance()).Should().Be(7_500_000);
    }

    [Fact]
    public async Task ScheduledCancel_RecordsItsEffectiveDate_AndResumeClearsIt()
    {
        await Process(Sub("evt_1", T0) with { CancelAtPeriodEnd = true, CancelEffectiveAt = T0.AddMonths(1) });
        (await Row()).CancelEffectiveAt.Should().Be(T0.AddMonths(1));

        await Process(Sub("evt_2", T0.AddHours(1)));

        Subscription row = await Row();
        row.CancelAtPeriodEnd.Should().BeFalse();
        row.CancelEffectiveAt.Should().BeNull();
    }

    [Fact]
    public async Task IgnoredEvents_AreRecordedAndChangeNothing()
    {
        BillingEvent evt = new() { Provider = P, EventId = "evt_x", EventType = "customer.created", OccurredAt = T0 };

        (await Process(evt)).Should().Be(BillingEventOutcome.Applied);
        (await Process(evt)).Should().Be(BillingEventOutcome.Duplicate);

        await using InferenceApiDbContext db = NewDb();
        (await db.Subscriptions.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------ Security R-1: refund before payment

    /// <summary>A Paddle refund adjustment as it arrives: it names the customer and (for a subscription payment) the subscription.</summary>
    private BillingEvent RefundOf(string id, string txn, string adj, string? sub = "sub_1", bool full = true)
        => Refund(id, txn, adj, full) with { SubscriptionId = sub, CustomerId = "ctm_1" };

    private async Task<string?> Note(string eventId)
    {
        await using InferenceApiDbContext db = NewDb();
        return (await db.BillingWebhookEvents.AsNoTracking().SingleAsync(e => e.EventId == eventId)).Error;
    }

    private async Task<int> LedgerEntryCount()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.CreditLedgerEntries.CountAsync();
    }

    /// <summary>
    /// R-1: the refund of an upgrade's proration transaction is processed before that transaction's
    /// <c>transaction.completed</c>. The refund is remembered, so the late payment grants nothing (whether
    /// the upgrade event came before the refund or only after the payment).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefundBeforeItsUpgradePayment_IsRemembered_AndTheLatePaymentGrantsNothing(bool upgradeFirst)
    {
        DateTime upgradeAt = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        if (upgradeFirst) await Process(Sub("evt_2", upgradeAt, price: "pri_pro", start: S30));
        int entries = await LedgerEntryCount();

        (await Process(RefundOf("evt_r", "txn_up", "adj_1"))).Should().Be(BillingEventOutcome.Applied);
        (await Process(Paid("evt_p", "txn_up", upgradeAt.AddMinutes(1), prorationStart: upgradeAt))).Should().Be(BillingEventOutcome.Applied);
        if (!upgradeFirst) await Process(Sub("evt_2", upgradeAt, price: "pri_pro", start: S30));

        (await Balance()).Should().Be(1_000_000, "a refunded payment never pays for an upgrade");
        (await LedgerEntryCount()).Should().Be(entries);
        BillingPendingUpgradeGrant tombstone = (await Pending()).Single(r => r.TransactionId == "txn_up");
        tombstone.ClawedBackAt.Should().NotBeNull();
        tombstone.GrantedAt.Should().BeNull();
        (await Note("evt_p")).Should().Contain("refunded");
        (await Row()).PlanId.Should().Be(await PlanId("pro"), "the plan change itself still applies; only its credits are withheld");
    }

    /// <summary>R-1: the same for a top-up: refunded before its <c>transaction.completed</c>, the top-up never grants.</summary>
    [Fact]
    public async Task RefundBeforeItsTopUpPayment_IsRemembered_AndTheLateTopUpGrantsNothing()
    {
        await Process(Sub("evt_1", T0));
        int entries = await LedgerEntryCount();

        (await Process(RefundOf("evt_r", "txn_t", "adj_t", sub: null))).Should().Be(BillingEventOutcome.Applied);
        (await Process(TopUp("evt_t", "txn_t"))).Should().Be(BillingEventOutcome.Applied);

        (await Balance()).Should().Be(1_000_000);
        (await LedgerEntryCount()).Should().Be(entries);
        (await Note("evt_t")).Should().Contain("refunded");
    }

    /// <summary>R-1: redelivering the refund of an unseen transaction (same event, or another event of the same adjustment) stays a no-op.</summary>
    [Fact]
    public async Task RefundOfAnUnseenTransaction_Redelivered_IsRememberedOnce_AndChangesNoCredits()
    {
        await Process(Sub("evt_1", S30, start: S30));
        int entries = await LedgerEntryCount();

        (await Process(RefundOf("evt_r1", "txn_up", "adj_1"))).Should().Be(BillingEventOutcome.Applied);
        (await Process(RefundOf("evt_r1", "txn_up", "adj_1"))).Should().Be(BillingEventOutcome.Duplicate);
        (await Process(RefundOf("evt_r2", "txn_up", "adj_1") with { EventType = "adjustment.created" })).Should().Be(BillingEventOutcome.Applied);

        (await Pending()).Should().ContainSingle(r => r.TransactionId == "txn_up").Which.ClawedBackAt.Should().NotBeNull();
        (await LedgerEntryCount()).Should().Be(entries);
        (await Balance()).Should().Be(1_000_000);
    }

    // ------------------------------------------------------------------ Security R-2: ambiguous pairing

    private bool WarnedAbout(params string[] ids)
        => _log.Entries.Any(e => e.Level == LogLevel.Warning && ids.All(id => e.Message.Contains(id, StringComparison.Ordinal)));

    /// <summary>A plan priced like Pro (so Pro → Pro Alt → Pro are both upgrades) with its own price id.</summary>
    private async Task AddProAltPlan()
    {
        await using InferenceApiDbContext db = NewDb();
        db.Plans.Add(new Plan { Id = Guid.NewGuid(), Key = "pro_alt", Name = "Pro Alt", Kind = PlanKind.Subscription, PriceMonthlyUsd = 79, MonthlyCreditsMillis = 3_500_000, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_pro_alt"}}""" });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// R-2 (product rule): two unpaid upgrades to IDENTICAL items (Creator → Pro, then Pro → Pro Alt → Pro)
    /// inside the tolerance of one payment for those items. Time cannot tell them apart and is never used
    /// to guess: the payment pairs with neither, 0 is granted, and a warning names it and both rows. The
    /// Pro Alt upgrade in between targets other items, so it is not a candidate at all.
    /// </summary>
    [Fact]
    public async Task TwoUpgradesWithIdenticalTargetsInsideTheTolerance_PairNothing_WarnAndGrantZero()
    {
        await AddProAltPlan();
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        await Process(Sub("evt_u1", t, price: "pri_pro", start: S30));
        await Process(Sub("evt_ua", t.AddMinutes(1), price: "pri_pro_alt", start: S30));
        await Process(Sub("evt_u2", t.AddMinutes(3), price: "pri_pro", start: S30));
        List<BillingPendingUpgradeGrant> upgrades = await Pending();
        upgrades.Should().HaveCount(3);
        Guid u1 = upgrades[0].Id, ua = upgrades[1].Id, u2 = upgrades[2].Id;

        await Process(Paid("evt_p", "txn_a", t.AddMinutes(2), prorationStart: t.AddMinutes(2)));

        (await Balance()).Should().Be(1_000_000);
        (await UpgradeGrantEntries()).Should().Be(0);
        (await Pending()).Should().OnlyContain(r => r.GrantedAt == null);
        (await Pending()).Single(r => r.TransactionId == "txn_a").NewEntitlementMillis.Should().BeNull("the payment stays unmatched");
        WarnedAbout("txn_a", u1.ToString(), u2.ToString()).Should().BeTrue();
        _log.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("txn_a", StringComparison.Ordinal))
            .Should().OnlyContain(e => !e.Message.Contains(ua.ToString(), StringComparison.Ordinal), "the Pro Alt upgrade's items differ");
    }

    /// <summary>
    /// R-2, the other order: two unmatched payments within the tolerance of one upgrade. The upgrade pairs
    /// with neither and a warning names both transactions; a later payment close to that upgrade does not
    /// take it either.
    /// </summary>
    [Fact]
    public async Task AnUpgradeWithTwoPaymentsWithinTheTolerance_PairsWithNeither_AndWarnsForReview()
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        await Process(Paid("evt_pa", "txn_a", t, prorationStart: t));
        await Process(Paid("evt_pb", "txn_b", t.AddMinutes(2), prorationStart: t.AddMinutes(2)));

        await Process(Sub("evt_u", t.AddMinutes(1), price: "pri_pro", start: S30));

        (await Balance()).Should().Be(1_000_000);
        (await Pending()).Should().OnlyContain(r => r.GrantedAt == null);
        (await Pending()).Where(r => r.TransactionId != null).Should().OnlyContain(r => r.NewEntitlementMillis == null);
        WarnedAbout("txn_a", "txn_b").Should().BeTrue();

        await Process(Paid("evt_pc", "txn_c", t.AddMinutes(11), prorationStart: t.AddMinutes(11)));

        (await Balance()).Should().Be(1_000_000);
        (await Pending()).Should().OnlyContain(r => r.GrantedAt == null);
    }

    // ------------------------------------------------------------------ Security N-1 / N-2: refunded payments

    /// <summary>
    /// N-1: upgrade u1 (Creator → Pro) is paid by p1, which is refunded — before its
    /// <c>transaction.completed</c> (tombstone), or after it arrived unmatched. Whichever order u1, the
    /// refund and p1 take, u1 is retired (paid and clawed back, 0 granted) once its refunded payment is
    /// known, so a neighbouring upgrade u2 (Pro → Team) within the tolerance, whose payment p2 lands
    /// before u2's webhook, pairs with p2 and never p2 with u1. The grant is prorated(Team − Pro).
    /// </summary>
    [Theory]
    [InlineData("upgrade,refund,payment")]
    [InlineData("refund,payment,upgrade")]
    [InlineData("payment,refund,upgrade")]
    public async Task ARefundedUpgradePayment_RetiresItsUpgrade_SoANeighbouringPaymentNeverPairsWithIt(string order)
    {
        DateTime t = S30.AddDays(15), t2 = t.AddMinutes(5);
        await Process(Sub("evt_1", S30, start: S30));
        foreach (string step in order.Split(','))
        {
            await Process(step switch
            {
                "upgrade" => Sub("evt_u1", t, price: "pri_pro", start: S30),
                "refund" => RefundOf("evt_r1", "txn_1", "adj_1"),
                _ => Paid("evt_p1", "txn_1", t.AddMinutes(1), prorationStart: t)
            });
        }

        await Process(Paid("evt_p2", "txn_2", t2.AddMinutes(1), prorationStart: t2, price: "pri_team", qty: 3));
        await Process(Sub("evt_u2", t2, price: "pri_team", qty: 3, start: S30));

        long teamOverPro = BillingEventProcessor.ProratedUpgradeGrantMillis(3_500_000, 4_500_000, S30, E30, t2);
        (await Balance()).Should().Be(1_000_000 + teamOverPro, "only p2 was paid, for Team − Pro");
        List<BillingPendingUpgradeGrant> rows = await Pending();
        rows.Single(r => r.TransactionId == "txn_2").NewEntitlementMillis.Should().Be(4_500_000, "p2 pairs with u2");
        BillingPendingUpgradeGrant u1 = rows.Single(r => r.NewEntitlementMillis == 3_500_000);
        u1.GrantedMillis.Should().Be(0);
        u1.ClawedBackAt.Should().NotBeNull("u1 is retired as paid and clawed back");
    }

    /// <summary>
    /// N-2: a full refund of a transaction not seen yet that names a known subscription may be a renewal
    /// or initial subscription payment, which the processor never claws back: the webhook row asks for a
    /// review and a warning is logged. A refund naming no subscription (a top-up) asks for none.
    /// </summary>
    [Fact]
    public async Task RefundOfAnUnseenTransaction_OfAKnownSubscription_AsksForAReview_AndWarns()
    {
        await Process(Sub("evt_1", S30, start: S30));

        (await Process(RefundOf("evt_r", "txn_renewal", "adj_1"))).Should().Be(BillingEventOutcome.Applied);
        (await Process(RefundOf("evt_rt", "txn_topup", "adj_2", sub: null))).Should().Be(BillingEventOutcome.Applied);

        (await Note("evt_r")).Should().Contain("review manually (subscription payment?)");
        WarnedAbout("txn_renewal", "sub_1").Should().BeTrue();
        (await Note("evt_rt")).Should().NotContain("subscription payment?");
        WarnedAbout("txn_topup").Should().BeFalse();
    }

    // ------------------------------------------------------------------ Security R-3: refill vs. early upgrade

    /// <summary>
    /// R-3: the renewal webhook lags and the first Active event of the new period is an early-period upgrade.
    /// The period's refill is the plan the period started on (Creator), and the upgrade's own payment adds
    /// only the prorated difference; the late renewal is stale and adds nothing.
    /// </summary>
    [Fact]
    public async Task UpgradeAsTheFirstActiveEventOfANewPeriod_RefillsAtTheOldPlan_AndGrantsOnlyTheProratedDifference()
    {
        DateTime next = E30, nextEnd = E30.AddMonths(1), upgradeAt = E30.AddDays(2);
        await Process(Sub("evt_1", S30, start: S30));

        await Process(Sub("evt_up", upgradeAt, price: "pri_pro", start: next));
        await Process(Paid("evt_p", "txn_up", upgradeAt.AddMinutes(1), prorationStart: upgradeAt));
        (await Process(Sub("evt_renewal", next, start: next))).Should().Be(BillingEventOutcome.Stale);

        long difference = BillingEventProcessor.ProratedUpgradeGrantMillis(1_000_000, 3_500_000, next, nextEnd, upgradeAt);
        (await Balance()).Should().Be(1_000_000 + 1_000_000 + difference);
        (await Row()).PlanId.Should().Be(await PlanId("pro"));
    }

    /// <summary>
    /// R-3, same period: the subscription was past due (no refill yet), its upgrade's payment arrived first,
    /// and the upgrade is the first Active event. The refill is the old plan's and the upgrade base is that
    /// refill: refill + prorated(new - old), never the new plan's refill on top.
    /// </summary>
    [Fact]
    public async Task UpgradeAsTheFirstActiveEventOfAPeriodWithoutARefill_RefillsAtTheOldPlanFirst()
    {
        DateTime upgradeAt = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30, status: SubscriptionStatus.PastDue));
        await Process(Paid("evt_p", "txn_up", upgradeAt.AddMinutes(1), prorationStart: upgradeAt));

        await Process(Sub("evt_2", upgradeAt, price: "pri_pro", start: S30));

        (await Grants()).Select(g => g.AmountMillis).Should().BeEquivalentTo(new[] { 1_000_000L, 1_250_000L });
    }

    // ------------------------------------------------------------------ paid-status and amount gates

    private async Task<int> UpgradeGrantEntries()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.CreditLedgerEntries.CountAsync(e => e.IdempotencyKey!.StartsWith("plan-upgrade:"));
    }

    /// <summary>
    /// F-3: an upgrade during a trial, even with a completed proration charge above 0, grants nothing in
    /// either webhook order; the row is paired and closed with 0, and the trial turning Active (a new
    /// period) still grants nothing for it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpgradeWhileTrialing_WithAPaidProration_GrantsNothing_InEitherOrder(bool paidFirst)
    {
        DateTime t = S30.AddDays(5);
        await Process(Sub("evt_1", S30, start: S30, status: SubscriptionStatus.Trialing));
        BillingEvent upgrade = Sub("evt_2", t, price: "pri_pro", start: S30, status: SubscriptionStatus.Trialing);
        BillingEvent paid = Paid("evt_p", "txn_up", t.AddMinutes(1), prorationStart: t, amount: 5000);

        await Process(paidFirst ? paid : upgrade);
        await Process(paidFirst ? upgrade : paid);

        (await Balance()).Should().Be(1_000_000, "only the trial's refill");
        BillingPendingUpgradeGrant row = (await Pending()).Single();
        row.TransactionId.Should().Be("txn_up");
        row.NewEntitlementMillis.Should().Be(3_500_000, "the payment paired with its upgrade");
        row.GrantedMillis.Should().Be(0);

        await Process(Sub("evt_3", E30, price: "pri_pro", start: E30));
        await Process(Paid("evt_p2", "txn_up", E30.AddMinutes(1), prorationStart: t, amount: 5000));

        (await Balance()).Should().Be(1_000_000 + 3_500_000, "the first paid period refills Pro; the trial's upgrade never grants");
        (await UpgradeGrantEntries()).Should().Be(0);
    }

    /// <summary>
    /// F-3: a $0 proration while Active pairs with its upgrade and closes it with 0 (both orders); a later
    /// paid transaction inside the tolerance does not pair with that closed upgrade.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ZeroProrationWhileActive_ClosesItsUpgradeWithZero_AndALaterPaymentDoesNotPairWithIt(bool paidFirst)
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        BillingEvent upgrade = Sub("evt_2", t, price: "pri_pro", start: S30);
        BillingEvent zero = Paid("evt_p0", "txn_zero", t.AddMinutes(1), prorationStart: t, amount: 0);

        await Process(paidFirst ? zero : upgrade);
        await Process(paidFirst ? upgrade : zero);

        (await Balance()).Should().Be(1_000_000);
        BillingPendingUpgradeGrant closed = (await Pending()).Single();
        closed.TransactionId.Should().Be("txn_zero");
        closed.NewEntitlementMillis.Should().Be(3_500_000);
        closed.GrantedMillis.Should().Be(0);
        closed.GrantedAt.Should().NotBeNull();

        await Process(Paid("evt_p1", "txn_other", t.AddMinutes(3), prorationStart: t.AddMinutes(2), amount: 2500));

        (await Balance()).Should().Be(1_000_000);
        (await Pending()).Single(r => r.TransactionId == "txn_other").NewEntitlementMillis.Should().BeNull("it stays unmatched");
        (await UpgradeGrantEntries()).Should().Be(0);
    }

    /// <summary>
    /// F-3: an upgrade closed with 0 by a $0 proration (either order) is paired and has GrantedAt set; a
    /// later Active event of the same period must not treat it as a waiting row and grant it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ZeroProration_ThenActiveInTheSamePeriod_DoesNotGrantTheClosedUpgrade(bool paidFirst)
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        BillingEvent upgrade = Sub("evt_2", t, price: "pri_pro", start: S30);
        BillingEvent zero = Paid("evt_p0", "txn_zero", t.AddMinutes(1), prorationStart: t, amount: 0);

        await Process(paidFirst ? zero : upgrade);
        await Process(paidFirst ? upgrade : zero);
        BillingPendingUpgradeGrant closed = (await Pending()).Single();
        closed.TransactionId.Should().Be("txn_zero");
        closed.NewEntitlementMillis.Should().Be(3_500_000);
        closed.GrantedMillis.Should().Be(0);
        closed.GrantedAt.Should().NotBeNull();

        (await Process(Sub("evt_3", t.AddMinutes(5), price: "pri_pro", start: S30))).Should().Be(BillingEventOutcome.Applied);

        (await Balance()).Should().Be(1_000_000, "the closed upgrade is not waiting; Active grants nothing for it");
        (await Pending()).Single().GrantedMillis.Should().Be(0);
        (await UpgradeGrantEntries()).Should().Be(0);
    }

    /// <summary>
    /// F-3: a renewal (subscription_recurring), a one-time charge (subscription_charge) and a top-up on the
    /// same subscription are not upgrade payments: with an unpaid upgrade pending, none pays for it.
    /// </summary>
    [Fact]
    public async Task RenewalOneTimeChargeAndTopUp_DoNotPayAPendingUpgrade()
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        await Process(Sub("evt_2", t, price: "pri_pro", start: S30));
        DateTimeOffset at = new(t.AddMinutes(1));
        PaddleBillingProvider paddle = PaddlePayloads.Provider(now: at);
        BillingEvent Parsed(string body)
        {
            BillingEvent parsed = paddle.VerifyAndParseWebhook(body, new Dictionary<string, string> { ["Paddle-Signature"] = PaddlePayloads.Sign(body, at) });
            return parsed with { Provider = P, OrganizationId = _org, SubscriptionId = "sub_1" };
        }

        foreach (BillingEvent evt in new[]
        {
            Parsed(PaddlePayloads.UpgradeTransaction("evt_renew", at, _org, txn: "txn_renew", subId: "sub_1", origin: "subscription_recurring", prorationStart: at, grandTotal: "7900")),
            Parsed(PaddlePayloads.UpgradeTransaction("evt_charge", at, _org, txn: "txn_charge", subId: "sub_1", origin: "subscription_charge", prorationStart: at, grandTotal: "500")),
            Parsed(PaddlePayloads.TopUpTransaction("evt_topup", at, _org, txn: "txn_topup")) with { SubscriptionId = null }
        })
        {
            (await Process(evt)).Should().Be(BillingEventOutcome.Applied);
        }

        BillingPendingUpgradeGrant upgrade = (await Pending()).Single();
        upgrade.TransactionId.Should().BeNull();
        upgrade.GrantedAt.Should().BeNull();
        (await UpgradeGrantEntries()).Should().Be(0);
        (await Balance()).Should().Be(1_000_000 + 1_000_000, "the refill and the 1,000-credit top-up only");
    }

    /// <summary>F-3: an upgrade followed by a cancellation, with no proration payment ever, grants nothing.</summary>
    [Fact]
    public async Task UpgradeThenCancel_WithNoPayment_GrantsNothing()
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        await Process(Sub("evt_2", t, price: "pri_pro", start: S30));

        await Process(Sub("evt_3", t.AddMinutes(2), price: "pri_pro", start: S30, status: SubscriptionStatus.Canceled));

        (await Balance()).Should().Be(1_000_000);
        (await Pending()).Single().GrantedAt.Should().BeNull();
        (await UpgradeGrantEntries()).Should().Be(0);
    }

    /// <summary>F-3: the subscription is Canceled by the time the upgrade's proration completes, so the payment pairs and grants 0.</summary>
    [Fact]
    public async Task UpgradeThenCanceled_ThenItsPayment_GrantsNothing()
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        await Process(Sub("evt_2", t, price: "pri_pro", start: S30));
        await Process(Sub("evt_3", t.AddMinutes(2), price: "pri_pro", start: S30, status: SubscriptionStatus.Canceled));

        await Process(Paid("evt_p", "txn_up", t.AddMinutes(3), prorationStart: t));

        (await Balance()).Should().Be(1_000_000);
        BillingPendingUpgradeGrant row = (await Pending()).Single();
        row.TransactionId.Should().Be("txn_up");
        row.GrantedMillis.Should().Be(0);
        (await UpgradeGrantEntries()).Should().Be(0);
    }

    /// <summary>
    /// F-3: the proration completes while the subscription is PastDue (a retried charge). The pair waits,
    /// nothing granted; once the subscription is Active again in the same period the difference is granted,
    /// exactly once, whichever of the payment and the Active event lands first. Replays add nothing.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PastDuePayment_ThenActiveInTheSamePeriod_GrantsExactlyOnce_InEitherOrder(bool paidFirst)
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        await Process(Sub("evt_2", t, price: "pri_pro", start: S30));
        await Process(Sub("evt_3", t.AddMinutes(1), price: "pri_pro", start: S30, status: SubscriptionStatus.PastDue));
        BillingEvent paid = Paid("evt_p", "txn_up", t.AddMinutes(30), prorationStart: t);
        BillingEvent active = Sub("evt_4", t.AddMinutes(31), price: "pri_pro", start: S30);

        if (paidFirst)
        {
            await Process(paid);
            (await Balance()).Should().Be(1_000_000, "paired while PastDue: nothing granted yet");
            BillingPendingUpgradeGrant waiting = (await Pending()).Single();
            waiting.TransactionId.Should().Be("txn_up");
            waiting.GrantedAt.Should().BeNull();
            await Process(Paid("evt_p_again", "txn_up", t.AddMinutes(30), prorationStart: t));
            (await Balance()).Should().Be(1_000_000, "a replay of the payment while waiting grants nothing");
            await Process(active);
        }
        else
        {
            await Process(active);
            (await Balance()).Should().Be(1_000_000, "Active again, but the upgrade is not paid yet");
            BillingPendingUpgradeGrant unpaid = (await Pending()).Single();
            unpaid.TransactionId.Should().BeNull();
            unpaid.GrantedAt.Should().BeNull();
            await Process(paid);
        }

        long difference = BillingEventProcessor.ProratedUpgradeGrantMillis(1_000_000, 3_500_000, S30, E30, t);
        (await Balance()).Should().Be(1_000_000 + difference);

        // Replays: the same events, another event of the same payment, another Active event of the period.
        (await Process(active)).Should().Be(BillingEventOutcome.Duplicate);
        await Process(Paid("evt_p2", "txn_up", t.AddMinutes(30), prorationStart: t));
        await Process(Sub("evt_5", t.AddHours(2), price: "pri_pro", start: S30));

        (await Balance()).Should().Be(1_000_000 + difference);
        (await UpgradeGrantEntries()).Should().Be(1);
        (await Pending()).Single().GrantedMillis.Should().Be(difference);
    }

    /// <summary>
    /// F-3: the proration completes while the subscription is Paused. The pair waits, nothing granted; the
    /// subscription Active again in the same period grants the difference exactly once.
    /// </summary>
    [Fact]
    public async Task PausedPayment_ThenActiveInTheSamePeriod_GrantsExactlyOnce()
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        await Process(Sub("evt_2", t, price: "pri_pro", start: S30));
        await Process(Sub("evt_3", t.AddMinutes(1), price: "pri_pro", start: S30, status: SubscriptionStatus.Paused));

        await Process(Paid("evt_p", "txn_up", t.AddMinutes(30), prorationStart: t));

        (await Balance()).Should().Be(1_000_000, "paired while Paused: nothing granted yet");
        BillingPendingUpgradeGrant waiting = (await Pending()).Single();
        waiting.TransactionId.Should().Be("txn_up");
        waiting.NewEntitlementMillis.Should().Be(3_500_000);
        waiting.GrantedAt.Should().BeNull("a Paused pairing waits instead of closing with 0");

        await Process(Sub("evt_4", t.AddMinutes(31), price: "pri_pro", start: S30));
        await Process(Sub("evt_5", t.AddHours(2), price: "pri_pro", start: S30));

        long difference = BillingEventProcessor.ProratedUpgradeGrantMillis(1_000_000, 3_500_000, S30, E30, t);
        (await Balance()).Should().Be(1_000_000 + difference);
        (await UpgradeGrantEntries()).Should().Be(1);
        (await Pending()).Single().GrantedMillis.Should().Be(difference);
    }

    /// <summary>
    /// F-3: paid while Paused and never Active again in that period: nothing is granted, and the next
    /// period's Active renewal refills the new plan without granting the old period's waiting upgrade.
    /// </summary>
    [Fact]
    public async Task PausedPayment_NeverActiveInThatPeriod_GrantsNothing()
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        await Process(Sub("evt_2", t, price: "pri_pro", start: S30));
        await Process(Sub("evt_3", t.AddMinutes(1), price: "pri_pro", start: S30, status: SubscriptionStatus.Paused));
        await Process(Paid("evt_p", "txn_up", t.AddMinutes(30), prorationStart: t));

        await Process(Sub("evt_4", t.AddDays(1), price: "pri_pro", start: S30, status: SubscriptionStatus.Paused));
        (await Balance()).Should().Be(1_000_000, "still Paused: nothing granted");
        (await Pending()).Single().GrantedAt.Should().BeNull();

        await Process(Sub("evt_5", E30, price: "pri_pro", start: E30));

        (await Balance()).Should().Be(1_000_000 + 3_500_000, "the new period refills Pro; the Paused period's upgrade never grants");
        (await UpgradeGrantEntries()).Should().Be(0);
    }

    /// <summary>F-3: a waiting (paired, PastDue) row whose payment is refunded is retired with 0, so a later Active grants nothing.</summary>
    [Fact]
    public async Task RefundOfAWaitingUpgradePayment_RetiresIt_SoALaterActiveGrantsNothing()
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        await Process(Sub("evt_2", t, price: "pri_pro", start: S30));
        await Process(Sub("evt_3", t.AddMinutes(1), price: "pri_pro", start: S30, status: SubscriptionStatus.PastDue));
        await Process(Paid("evt_p", "txn_up", t.AddMinutes(30), prorationStart: t));

        (await Process(RefundOf("evt_r", "txn_up", "adj_1"))).Should().Be(BillingEventOutcome.Applied);
        await Process(Sub("evt_4", t.AddMinutes(31), price: "pri_pro", start: S30));

        (await Balance()).Should().Be(1_000_000);
        BillingPendingUpgradeGrant row = (await Pending()).Single();
        row.GrantedMillis.Should().Be(0);
        row.ClawedBackAt.Should().NotBeNull();
        (await UpgradeGrantEntries()).Should().Be(0);
    }

    // ------------------------------------------------------------------ Security R-2 (T-011): pairing on items

    /// <summary>
    /// R-2: u1 (Creator → Pro) and u2 (Pro → Team ×3) three minutes apart, their payments delivered
    /// swapped or interleaved. Each payment pairs with the upgrade whose target items it charged, never
    /// with the neighbour, whatever the order (time alone handed p2 to u1, or paired nothing).
    /// </summary>
    [Theory]
    [InlineData("u1,p2,u2,p1")]
    [InlineData("u1,u2,p2,p1")]
    [InlineData("p2,p1,u1,u2")]
    [InlineData("p2,u1,p1,u2")]
    public async Task SwappedPaymentsOfTwoUpgradesWithDifferentTargets_EachPairWithItsOwnUpgrade(string order)
    {
        DateTime t1 = S30.AddDays(15), t2 = t1.AddMinutes(3);
        await Process(Sub("evt_1", S30, start: S30));
        foreach (string step in order.Split(','))
        {
            await Process(step switch
            {
                "u1" => Sub("evt_u1", t1, price: "pri_pro", start: S30),
                "u2" => Sub("evt_u2", t2, price: "pri_team", qty: 3, start: S30),
                "p1" => Paid("evt_p1", "txn_1", t1.AddMinutes(1), prorationStart: t1),
                _ => Paid("evt_p2", "txn_2", t2.AddMinutes(1), prorationStart: t2, price: "pri_team", qty: 3)
            });
        }

        List<BillingPendingUpgradeGrant> rows = await Pending();
        rows.Should().HaveCount(2, "each payment met its own upgrade on one row");
        rows.Single(r => r.TransactionId == "txn_1").NewEntitlementMillis.Should().Be(3_500_000, "p1 paid Creator → Pro");
        rows.Single(r => r.TransactionId == "txn_2").NewEntitlementMillis.Should().Be(4_500_000, "p2 paid Pro → Team");
        rows.Should().OnlyContain(r => r.GrantedAt != null);
        (await Balance() - 1_000_000).Should().BeGreaterThan(0)
            .And.BeLessThanOrEqualTo(BillingEventProcessor.ProratedUpgradeGrantMillis(1_000_000, 4_500_000, S30, E30, t1), "the period cap still holds");
    }

    /// <summary>R-2: a payment whose charged items match no upgrade (Team ×3 paid, Pro upgraded) never pairs, in either order; a warning names it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APaymentWhoseItemsMatchNoUpgrade_GrantsNothing_InEitherOrder(bool paidFirst)
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        BillingEvent upgrade = Sub("evt_u", t, price: "pri_pro", start: S30);
        BillingEvent paid = Paid("evt_p", "txn_x", t.AddMinutes(1), prorationStart: t, price: "pri_team", qty: 3);

        await Process(paidFirst ? paid : upgrade);
        await Process(paidFirst ? upgrade : paid);

        (await Balance()).Should().Be(1_000_000);
        (await UpgradeGrantEntries()).Should().Be(0);
        List<BillingPendingUpgradeGrant> rows = await Pending();
        rows.Should().HaveCount(2).And.OnlyContain(r => r.GrantedAt == null);
        rows.Single(r => r.TransactionId == "txn_x").NewEntitlementMillis.Should().BeNull();
        WarnedAbout("txn_x").Should().BeTrue();
    }

    /// <summary>R-2 fail closed: a payment that carries no usable charged items never pairs, even with the only upgrade in the window.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APaymentWithoutChargedItems_NeverPairs_InEitherOrder(bool paidFirst)
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        BillingEvent upgrade = Sub("evt_u", t, price: "pri_pro", start: S30);
        BillingEvent paid = Paid("evt_p", "txn_x", t.AddMinutes(1), prorationStart: t) with { Items = [] };

        await Process(paidFirst ? paid : upgrade);
        await Process(paidFirst ? upgrade : paid);

        (await Balance()).Should().Be(1_000_000);
        (await Pending()).Should().HaveCount(2).And.OnlyContain(r => r.GrantedAt == null);
        WarnedAbout("txn_x").Should().BeTrue();
    }

    /// <summary>
    /// R-2, payment first: a Team ×3 payment and a Pro payment both wait inside the tolerance; the Pro
    /// upgrade pairs with the Pro payment only (time alone saw two candidates and paired nothing).
    /// </summary>
    [Fact]
    public async Task PaymentFirst_TheUpgradePairsOnlyWithThePaymentOfItsExactItems()
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        await Process(Paid("evt_pt", "txn_team", t.AddMinutes(1), prorationStart: t.AddMinutes(1), price: "pri_team", qty: 3));
        await Process(Paid("evt_pp", "txn_pro", t.AddMinutes(1), prorationStart: t));

        await Process(Sub("evt_u", t, price: "pri_pro", start: S30));

        (await Balance()).Should().Be(1_000_000 + BillingEventProcessor.ProratedUpgradeGrantMillis(1_000_000, 3_500_000, S30, E30, t));
        List<BillingPendingUpgradeGrant> rows = await Pending();
        rows.Single(r => r.TransactionId == "txn_pro").NewEntitlementMillis.Should().Be(3_500_000);
        rows.Single(r => r.TransactionId == "txn_team").NewEntitlementMillis.Should().BeNull("its items match no upgrade");
    }

    /// <summary>
    /// N-5: every retire of an upgrade by its refunded payment logs a warning naming the upgrade row and the
    /// transaction, in each order the upgrade, the refund and the payment can arrive.
    /// </summary>
    [Theory]
    [InlineData("upgrade,refund,payment")]
    [InlineData("refund,payment,upgrade")]
    [InlineData("payment,refund,upgrade")]
    public async Task EveryRetire_WarnsNamingTheUpgradeRowAndTheRefundedTransaction(string order)
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        foreach (string step in order.Split(','))
        {
            await Process(step switch
            {
                "upgrade" => Sub("evt_u1", t, price: "pri_pro", start: S30),
                "refund" => RefundOf("evt_r1", "txn_1", "adj_1"),
                _ => Paid("evt_p1", "txn_1", t.AddMinutes(1), prorationStart: t)
            });
        }

        BillingPendingUpgradeGrant u1 = (await Pending()).Single(r => r.NewEntitlementMillis == 3_500_000);
        u1.ClawedBackAt.Should().NotBeNull("u1 is retired");
        WarnedAbout("txn_1", u1.Id.ToString()).Should().BeTrue();
    }

    /// <summary>
    /// G-4: a signed Paddle subscription event whose status is unknown (or empty) is not read as Active, so
    /// the upgrade it carries never grants on its paid proration.
    /// </summary>
    [Theory]
    [InlineData("frozen")]
    [InlineData("")]
    public async Task AnUpgradeEventWithAnUnknownPaddleStatus_GrantsNothing(string status)
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        DateTimeOffset at = new(t);
        PaddleBillingProvider paddle = PaddlePayloads.Provider(now: at);
        string body = PaddlePayloads.Subscription("evt_2", at, _org, status: status, price: "pri_pro", start: new DateTimeOffset(S30));
        BillingEvent upgrade = paddle.VerifyAndParseWebhook(body, new Dictionary<string, string> { ["Paddle-Signature"] = PaddlePayloads.Sign(body, at) })
            with
        { Provider = P, SubscriptionId = "sub_1" };

        await Process(upgrade);
        await Process(Paid("evt_p", "txn_up", t.AddMinutes(1), prorationStart: t));

        (await Balance()).Should().Be(1_000_000);
        (await UpgradeGrantEntries()).Should().Be(0);
        (await Row()).Status.Should().NotBe(SubscriptionStatus.Active);
    }

    /// <summary>
    /// G-1: an upgrade paid while Paused waits for Active in its own period. Once the subscription renews into
    /// a new period, or is canceled, the waiting row is closed with 0, so a later Active event that still names
    /// the old period (with a newer occurred_at) grants nothing.
    /// </summary>
    [Theory]
    [InlineData("renewal")]
    [InlineData("cancel")]
    public async Task AWaitingUpgrade_IsClosedAtRenewalOrCancel_SoALateActiveOfItsPeriodGrantsNothing(string end)
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        await Process(Sub("evt_2", t, price: "pri_pro", start: S30));
        await Process(Sub("evt_3", t.AddMinutes(1), price: "pri_pro", start: S30, status: SubscriptionStatus.Paused));
        await Process(Paid("evt_p", "txn_up", t.AddMinutes(30), prorationStart: t));
        (await Pending()).Single().GrantedAt.Should().BeNull("waiting for Active");

        DateTime lateAt = end == "renewal" ? E30.AddHours(1) : t.AddHours(2);
        await Process(end == "renewal"
            ? Sub("evt_4", E30, price: "pri_pro", start: E30)
            : Sub("evt_4", t.AddHours(1), price: "pri_pro", start: S30, status: SubscriptionStatus.Canceled));
        await Process(Sub("evt_5", lateAt, price: "pri_pro", start: S30));

        (await UpgradeGrantEntries()).Should().Be(0);
        BillingPendingUpgradeGrant row = (await Pending()).Single();
        row.GrantedMillis.Should().Be(0);
        row.GrantedAt.Should().NotBeNull("closed with 0");
    }

    /// <summary>
    /// G-2: a partial refund of a waiting (paired, not granted) upgrade payment closes it with 0 and asks for a
    /// review, so the next Active event does not grant the full prorated difference.
    /// </summary>
    [Fact]
    public async Task PartialRefundOfAWaitingUpgradePayment_ClosesItWithZero_SoALaterActiveGrantsNothing()
    {
        DateTime t = S30.AddDays(15);
        await Process(Sub("evt_1", S30, start: S30));
        await Process(Sub("evt_2", t, price: "pri_pro", start: S30));
        await Process(Sub("evt_3", t.AddMinutes(1), price: "pri_pro", start: S30, status: SubscriptionStatus.PastDue));
        await Process(Paid("evt_p", "txn_up", t.AddMinutes(30), prorationStart: t));

        (await Process(RefundOf("evt_r", "txn_up", "adj_1", full: false))).Should().Be(BillingEventOutcome.Applied);
        await Process(Sub("evt_4", t.AddMinutes(31), price: "pri_pro", start: S30));

        (await Balance()).Should().Be(1_000_000);
        (await UpgradeGrantEntries()).Should().Be(0);
        (await Pending()).Single().GrantedMillis.Should().Be(0);
        (await Note("evt_r")).Should().Contain("review manually");
        WarnedAbout("txn_up").Should().BeTrue();
    }

    // ------------------------------------------------------------------ billing_payments (T-004)

    [Fact]
    public async Task A_completed_top_up_records_the_money_it_took()
    {
        (await Process(TopUp("evt_pay", "txn_pay", amount: 1450, currency: "USD", fee: 123)))
            .Should().Be(BillingEventOutcome.Applied);

        BillingPayment row = (await Payments()).Single();
        row.Provider.Should().Be(P);
        row.ProviderTransactionId.Should().Be("txn_pay");
        row.OrganizationId.Should().Be(_org);
        row.AmountMinor.Should().Be(1450);
        row.CurrencyCode.Should().Be("USD");
        row.FeeMinor.Should().Be(123);
    }

    /// <summary>
    /// A redelivered webhook leaves exactly one payment row, but by the processor's <c>(provider, event_id)</c>
    /// webhook dedupe: the second delivery is short-circuited to <see cref="BillingEventOutcome.Duplicate"/>
    /// before <c>ApplyAsync</c>, so it never reaches the payments write. This proves the delivery is not
    /// counted twice, not that the payments table is idempotent. The payments table's own idempotency — two
    /// differently-numbered events for one transaction, both reaching the write — is exercised by
    /// <see cref="A_second_event_for_the_same_transaction_records_the_payment_once"/>.
    /// </summary>
    [Fact]
    public async Task A_redelivered_webhook_leaves_one_payment_row_via_the_webhook_level_dedupe()
    {
        (await Process(TopUp("evt_pay", "txn_pay", amount: 1450, currency: "USD", fee: 123)))
            .Should().Be(BillingEventOutcome.Applied);
        (await Process(TopUp("evt_pay", "txn_pay", amount: 1450, currency: "USD", fee: 123)))
            .Should().Be(BillingEventOutcome.Duplicate);

        (await Payments()).Should().HaveCount(1, "the same event id is the same delivery");
    }

    [Fact]
    public async Task A_second_event_for_the_same_transaction_records_the_payment_once()
    {
        await Process(TopUp("evt_a", "txn_pay", amount: 1450, currency: "USD", fee: 123));
        await Process(TopUp("evt_b", "txn_pay", amount: 1450, currency: "USD", fee: 123));

        (await Payments()).Should().HaveCount(1, "a differently-numbered event for the same transaction is the same money");
    }

    [Fact]
    public async Task An_upgrade_payment_and_a_plain_subscription_payment_are_both_recorded()
    {
        await Process(Paid("evt_up", "txn_up", T0.AddMinutes(1), amount: 2500));
        await Process(SubscriptionPayment("evt_sub", "txn_sub", T0.AddMinutes(2), amount: 2900));

        List<BillingPayment> rows = await Payments();
        rows.Should().HaveCount(2);
        rows.Single(r => r.ProviderTransactionId == "txn_up").AmountMinor.Should().Be(2500);
        rows.Single(r => r.ProviderTransactionId == "txn_sub").AmountMinor.Should().Be(2900);
    }

    [Fact]
    public async Task A_payment_naming_no_organization_is_recorded_unattributed()
    {
        BillingEvent evt = TopUp("evt_pay", "txn_pay") with { OrganizationId = null, CustomerId = null };

        (await Process(evt)).Should().Be(BillingEventOutcome.Unresolved);

        BillingPayment row = (await Payments()).Single();
        row.OrganizationId.Should().BeNull("money with no resolvable owner is still recorded");
        row.AmountMinor.Should().Be(1450);
    }

    [Fact]
    public async Task A_payment_naming_an_unknown_organization_is_recorded_unattributed()
    {
        BillingEvent evt = TopUp("evt_pay", "txn_pay") with { OrganizationId = Guid.NewGuid(), CustomerId = null };

        (await Process(evt)).Should().Be(BillingEventOutcome.Unresolved);

        BillingPayment row = (await Payments()).Single();
        row.OrganizationId.Should().BeNull("an organization that does not exist is never written");
    }

    [Fact]
    public async Task The_subscriptions_organization_wins_over_a_contradicting_payload_claim()
    {
        Guid orgA = Guid.NewGuid();
        await using (InferenceApiDbContext db = NewDb())
        {
            Guid planId = await db.Plans.Select(p => p.Id).FirstAsync();
            db.Organizations.Add(new Organization { Id = orgA, Name = "a", Slug = "a", Kind = OrganizationKind.Team });
            db.Subscriptions.Add(new Subscription
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgA,
                PlanId = planId,
                Provider = P,
                ProviderSubscriptionId = "sub_contested",
                CreatedAt = T0
            });
            await db.SaveChangesAsync();
        }

        // The payload claims _org (org B); our own subscriptions row says org A.
        await Process(Paid("evt_p", "txn_contested", T0.AddMinutes(1), sub: "sub_contested", amount: 2500)
            with
        { OrganizationId = _org });

        BillingPayment row = (await Payments()).Single();
        row.OrganizationId.Should().Be(orgA, "our own subscriptions row corroborates the owner, not the payload's claim");
    }

    [Fact]
    public async Task A_completed_transaction_the_runtime_does_not_apply_is_still_recorded()
    {
        BillingEvent evt = Paid("evt_p", "txn_unresolved", T0.AddMinutes(1), sub: "sub_absent", amount: 2500)
            with
        { OrganizationId = null, CustomerId = null };

        (await Process(evt)).Should().Be(BillingEventOutcome.Unresolved);

        BillingPayment row = (await Payments()).Single();
        row.OrganizationId.Should().BeNull("Unresolved means recorded but not actionable, not refused: Paddle captured that money");
        row.AmountMinor.Should().Be(2500);
    }

    [Fact]
    public async Task A_completed_transaction_with_no_stated_amount_is_not_recorded()
    {
        (await Process(TopUp("evt_pay", "txn_pay", paid: false))).Should().Be(BillingEventOutcome.Applied);

        (await Payments()).Should().BeEmpty("no stated amount means no payment row, never a $0 one");
    }

    [Fact]
    public async Task A_refund_records_no_payment()
    {
        await Process(Refund("evt_r", "txn_r", "adj_r"));

        (await Payments()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_amount_is_stored_as_the_exact_minor_units_the_provider_sent()
    {
        await Process(TopUp("evt_jpy", "txn_jpy", amount: 1450, currency: "JPY"));

        BillingPayment row = (await Payments()).Single();
        row.AmountMinor.Should().Be(1450, "JPY has no minor unit: the integer the provider sent is stored untouched");
        row.CurrencyCode.Should().Be("JPY");
    }

    // ------------------------------------------------------------------ alpha.first_payment (T-004)

    /// <summary>Every <c>product_events</c> row, oldest first.</summary>
    private async Task<List<ProductEvent>> ProductEvents()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.ProductEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    /// <summary>Seeds one Owner membership (and the user it names) for <paramref name="org"/> at <paramref name="at"/>.</summary>
    private async Task AddOwner(Guid org, Guid user, DateTime at)
    {
        await using InferenceApiDbContext db = NewDb();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = $"{user:N}@x.y" });
        db.OrganizationMemberships.Add(new OrganizationMembership
        {
            OrganizationId = org,
            UserId = user,
            Role = OrganizationRole.Owner,
            CreatedAt = at
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_paid_transaction_writes_one_first_payment_funnel_row_attributed_to_the_organization_owner()
    {
        Guid owner = Guid.NewGuid();
        await AddOwner(_org, owner, T0);

        (await Process(TopUp("evt_pay", "txn_pay"))).Should().Be(BillingEventOutcome.Applied);

        ProductEvent row = (await ProductEvents()).Single();
        row.Event.Should().Be("alpha.first_payment");
        row.Source.Should().Be("server");
        row.OrganizationId.Should().Be(_org);
        row.UserId.Should().Be(owner, "the payer is the organization's Owner");
    }

    /// <summary>
    /// launch-soft-open T-003: "payment succeeded" must be observable for BOTH ways of paying. A plan payment
    /// (an invoice that names a subscription) writes the same once-per-organization activation row as a top-up,
    /// and the money row keeps the subscription id while a top-up keeps none: that column is how
    /// <c>infra/powerbi/soft-open-funnel.sql</c> tells plan money from top-up money.
    /// </summary>
    [Fact]
    public async Task A_paid_plan_payment_writes_the_first_payment_row_and_a_plan_money_row_while_a_top_up_money_row_has_no_subscription()
    {
        Guid owner = Guid.NewGuid();
        await AddOwner(_org, owner, T0);

        (await Process(SubscriptionPayment("evt_plan", "txn_plan", T0, sub: "sub_1"))).Should().Be(BillingEventOutcome.Applied);

        ProductEvent row = (await ProductEvents()).Single();
        row.Event.Should().Be("alpha.first_payment");
        row.OrganizationId.Should().Be(_org);
        row.UserId.Should().Be(owner);

        (await Process(TopUp("evt_topup", "txn_topup"))).Should().Be(BillingEventOutcome.Applied);

        (await ProductEvents()).Should().ContainSingle("a second payment, of the other kind, is not a second activation");
        List<BillingPayment> payments = await Payments();
        payments.Single(p => p.ProviderTransactionId == "txn_plan").ProviderSubscriptionId.Should().Be("sub_1");
        payments.Single(p => p.ProviderTransactionId == "txn_topup").ProviderSubscriptionId.Should().BeNull();
    }

    /// <summary>
    /// A zero-amount first invoice (a 100 percent promotion) reaches the processor with
    /// <c>IsPaidTransaction = false</c>: nothing was paid, so there is no money row and no activation row.
    /// The funnel must not count it as a payment.
    /// </summary>
    [Fact]
    public async Task A_first_invoice_that_took_no_money_writes_neither_a_payment_nor_a_first_payment_row()
    {
        Guid owner = Guid.NewGuid();
        await AddOwner(_org, owner, T0);

        (await Process(SubscriptionPayment("evt_free", "txn_free", T0, sub: "sub_1", amount: 0) with { IsPaidTransaction = false }))
            .Should().Be(BillingEventOutcome.Applied);

        (await Payments()).Should().BeEmpty();
        (await ProductEvents()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_funnel_rows_person_is_the_earliest_owner_membership_by_created_at()
    {
        Guid laterOwner = Guid.NewGuid();
        Guid earlierOwner = Guid.NewGuid();
        await AddOwner(_org, laterOwner, T0.AddDays(1));
        await AddOwner(_org, earlierOwner, T0);

        await Process(TopUp("evt_pay", "txn_pay"));

        (await ProductEvents()).Single().UserId.Should().Be(earlierOwner, "the lowest created_at wins");
    }

    [Fact]
    public async Task A_payment_with_no_owner_membership_still_writes_the_funnel_row_with_no_person()
    {
        (await Process(TopUp("evt_pay", "txn_pay"))).Should().Be(BillingEventOutcome.Applied);

        ProductEvent row = (await ProductEvents()).Single();
        row.OrganizationId.Should().Be(_org);
        row.UserId.Should().BeNull("a payment belongs to an organization and may have no resolvable person");
    }

    [Fact]
    public async Task A_replay_of_the_same_transaction_writes_no_second_first_payment_row()
    {
        await Process(TopUp("evt_a", "txn_pay"));
        await Process(TopUp("evt_b", "txn_pay"));

        (await ProductEvents()).Should().HaveCount(1, "the payment row's own idempotency is the funnel row's too");
    }

    [Fact]
    public async Task Two_different_paid_transactions_for_one_organization_write_exactly_one_first_payment_row()
    {
        await Process(TopUp("evt_a", "txn_a"));
        await Process(TopUp("evt_b", "txn_b"));

        (await ProductEvents()).Should().HaveCount(
            1, "alpha.first_payment is the organization's activation, not one row per transaction");
    }

    [Fact]
    public async Task A_payment_with_no_resolvable_organization_writes_no_funnel_row_and_logs_the_omission()
    {
        BillingEvent evt = TopUp("evt_pay", "txn_pay") with { OrganizationId = null, CustomerId = null };

        (await Process(evt)).Should().Be(BillingEventOutcome.Unresolved);

        (await ProductEvents()).Should().BeEmpty("the funnel row is derived; money with no owner stays in billing_payments alone");
        _log.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("alpha.first_payment", StringComparison.Ordinal),
            "the omission is logged so a reader can tell it from a code path that never ran");
    }

    [Fact]
    public async Task A_failing_funnel_write_never_fails_the_payment_record()
    {
        (await Process(TopUp("evt_pay", "txn_pay"), productEvents: new ThrowingProductEventLog()))
            .Should().Be(BillingEventOutcome.Applied, "the payment is money; the funnel row is a metric");

        (await Payments()).Should().HaveCount(1, "the money is recorded even when the funnel row fails");
        (await ProductEvents()).Should().BeEmpty();
        _log.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("alpha.first_payment", StringComparison.Ordinal),
            "the failure is logged and visible");
    }

    [Fact]
    public async Task The_funnel_row_is_written_on_the_fresh_scope_production_uses()
    {
        Guid owner = Guid.NewGuid();
        await AddOwner(_org, owner, T0);
        using ServiceProvider provider = ScopeProvider();

        (await Process(TopUp("evt_pay", "txn_pay"), scopeFactory: provider.GetRequiredService<IServiceScopeFactory>()))
            .Should().Be(BillingEventOutcome.Applied);

        ProductEvent row = (await ProductEvents()).Single();
        row.OrganizationId.Should().Be(_org);
        row.UserId.Should().Be(owner);
    }

    /// <summary>
    /// Review Focus 3, post-commit: a real database failure in the funnel write — not a mock throwing — after
    /// the money has committed leaves both the <c>billing_payments</c> row and the webhook outcome committed.
    /// Before the fix the write ran inside the money transaction, where the same failure aborted it.
    /// </summary>
    [Fact]
    public async Task A_real_failing_funnel_write_after_commit_leaves_the_payment_and_the_webhook_outcome_committed()
    {
        using ServiceProvider provider = ScopeProvider(
            sp => new FailingSqlProductEventLog(sp.GetRequiredService<InferenceApiDbContext>()));

        (await Process(TopUp("evt_pay", "txn_pay"), scopeFactory: provider.GetRequiredService<IServiceScopeFactory>()))
            .Should().Be(BillingEventOutcome.Applied, "the funnel row is a metric; the payment is money");

        (await Payments()).Should().HaveCount(1, "the payment commits before the funnel write is attempted");
        (await ProductEvents()).Should().BeEmpty("the failed write wrote nothing");
        await using InferenceApiDbContext db = NewDb();
        (await db.BillingWebhookEvents.SingleAsync()).Outcome.Should().Be("Applied", "the webhook outcome committed too");
        _log.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("alpha.first_payment", StringComparison.Ordinal),
            "the failure is logged and visible");
    }

    /// <summary>
    /// QA-2: the payment the processor stashes inside the money transaction is owed a funnel row only once
    /// that transaction commits. A call that throws after stashing it (here a real refusal inside the
    /// transaction, not a mock) must leave nothing behind, or the next event on the same instance would
    /// emit an <c>alpha.first_payment</c> row for a payment that was rolled back — a false activation.
    /// </summary>
    [Fact]
    public async Task A_thrown_process_leaves_no_pending_activation_for_the_next_event_on_the_same_instance()
    {
        // The top-up's ledger idempotency key is already held by another organization, so CreditLedger
        // refuses the grant mid-transaction and ProcessAsync rethrows after RecordPaymentAsync stashed the
        // pending activation.
        Guid other = Guid.NewGuid();
        await using (InferenceApiDbContext seed = NewDb())
        {
            seed.Organizations.Add(new Organization { Id = other, Name = "other", Slug = "other", Kind = OrganizationKind.Team });
            await seed.SaveChangesAsync();
            await new CreditLedger(seed).GrantAsync(
                other, CreditGrantSource.TopUp, 1_000_000, null, BillingEventProcessor.TopUpKey(P, "txn_pay"));
        }

        await using InferenceApiDbContext db = NewDb();
        var processor = new BillingEventProcessor(db, _log, new FixedClock(T0.AddDays(1)));

        await FluentActions.Awaiting(() => processor.ProcessAsync(TopUp("evt_pay", "txn_pay"), "{}"))
            .Should().ThrowAsync<InvalidOperationException>("the grant is refused inside the money transaction");

        (await Payments()).Should().BeEmpty("the refused transaction rolled the payment back");

        // A second event on the SAME instance, not itself a paid transaction: nothing it does owes a
        // funnel row, so any alpha.first_payment it writes would be the rolled-back payment's.
        await processor.ProcessAsync(Sub("evt_sub", T0.AddHours(1)), "{}");

        (await ProductEvents()).Should().BeEmpty("a rolled-back payment owes no alpha.first_payment row");
    }
}
