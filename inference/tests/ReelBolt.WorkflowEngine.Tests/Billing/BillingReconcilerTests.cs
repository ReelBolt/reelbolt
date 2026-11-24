#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Paddle;
using ReelBolt.Inference.Api.Services.Billing.Reconcile;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The lost-webhook reconciler: it reads Paddle's live subscription (and recent upgrade payments) through
/// the provider and feeds them, as synthetic webhook events, through the very same
/// <see cref="BillingEventProcessor.ProcessAsync"/>. Idempotency, ordering and upgrade pairing are therefore
/// the webhook's own — these tests pin that a late real webhook changes nothing, that a provider failure
/// applies nothing, and that credits only follow a real paid transaction. Sqlite in-memory, settable clock.
/// </summary>
public sealed class BillingReconcilerTests : IDisposable
{
    private const string P = "fake";
    private const string SubId = "sub_1";

    /// <summary>The billing period every scenario lives in (starts with the seeding webhook).</summary>
    private static readonly DateTime PS = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime PE = PS.AddMonths(1);

    /// <summary>Paddle's <c>updated_at</c> of the change whose webhook was lost.</summary>
    private static readonly DateTime TU = PS.AddDays(10);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly FakeProvider _provider = new();
    private readonly MutableClock _clock = new(TU.AddMinutes(15));
    private readonly InProcessBillingChangeLock _lock = new();

    public BillingReconcilerTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = _org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.Plans.AddRange(
            new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, PriceMonthlyUsd = 29, MonthlyCreditsMillis = 1_000_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_creator"},"paddle":{"monthly":"pri_creator"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "pro", Name = "Pro", Kind = PlanKind.Subscription, PriceMonthlyUsd = 79, MonthlyCreditsMillis = 3_500_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_pro"},"paddle":{"monthly":"pri_pro"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "team", Name = "Team", Kind = PlanKind.PerSeat, MinSeats = 3, PriceMonthlyUsd = 49, MonthlyCreditsMillis = 1_500_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_team"}}""" });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ------------------------------------------------------------------ harness

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private sealed class MutableClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    /// <summary>A provider whose live view the test sets; everything else is out of scope here.</summary>
    private sealed class FakeProvider : IBillingProvider
    {
        public string Name => P;
        public bool IsConfigured => true;
        public IReadOnlyList<int> TopUpPacks => [];
        public string? TopUpPriceId(int credits) => null;

        public BillingSnapshot? Snapshot { get; set; }
        public List<BillingSnapshot> Transactions { get; } = [];
        public Exception? SnapshotFail { get; set; }
        public Exception? TransactionsFail { get; set; }
        public string? LiveSubscriptionId { get; set; }
        public List<(string Customer, Guid Org)> Listings { get; } = [];
        public List<string> SnapshotCalls { get; } = [];
        public List<(string Sub, DateTime Since)> TransactionCalls { get; } = [];

        /// <summary>Runs while the subscription is read, i.e. in the middle of a reconcile.</summary>
        public Func<Task>? DuringSnapshot { get; set; }

        public async Task<BillingSnapshot> SnapshotSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
        {
            SnapshotCalls.Add(providerSubscriptionId);
            if (DuringSnapshot is not null) await DuringSnapshot();
            if (SnapshotFail is not null) throw SnapshotFail;
            return Snapshot ?? throw new InvalidOperationException("No snapshot set.");
        }

        public Task<IReadOnlyList<BillingSnapshot>> CompletedUpgradeTransactionsAsync(string providerSubscriptionId, DateTime since, CancellationToken ct)
        {
            TransactionCalls.Add((providerSubscriptionId, since));
            if (TransactionsFail is not null) throw TransactionsFail;
            return Task.FromResult<IReadOnlyList<BillingSnapshot>>(Transactions.ToList());
        }

        public Task<string?> FindLiveSubscriptionIdAsync(string providerCustomerId, Guid organizationId, CancellationToken ct)
        {
            Listings.Add((providerCustomerId, organizationId));
            return Task.FromResult(LiveSubscriptionId);
        }

        public Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> HasLiveSubscriptionAsync(string providerCustomerId, Guid organizationId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PortalSession> CreatePortalSessionAsync(string providerCustomerId, string? providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();
        public BillingEvent VerifyAndParseWebhook(string rawBody, IReadOnlyDictionary<string, string> headers) => throw new NotSupportedException();
        public Task CancelSubscriptionAsync(string providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();
        public Task ResumeSubscriptionAsync(string providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();

        /// <summary>Every plan change asked of the provider, with the discount argument; a reconcile must never add one.</summary>
        public List<(string Sub, IReadOnlyList<BillingLineItem> Items, PlanChangeTiming Timing, PlanChangeDiscount Discount)> PlanChanges { get; } = [];

        public Task ChangePlanAsync(string providerSubscriptionId, IReadOnlyList<BillingLineItem> items, PlanChangeTiming timing, PlanChangeDiscount discount, CancellationToken ct)
        {
            PlanChanges.Add((providerSubscriptionId, items, timing, discount));
            return Task.CompletedTask;
        }

        public Task<ProviderSubscriptionState> GetSubscriptionAsync(string providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> CreateDiscountAsync(ProviderDiscountSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateDiscountAsync(string discountId, bool? active, DateTime? expiresAt, bool clearExpiry, CancellationToken ct) => throw new NotSupportedException();
        public Task<PricePreview> PreviewPriceAsync(IReadOnlyList<BillingLineItem> items, string? discountId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderTransactionCancel> CancelTransactionAsync(string providerTransactionId, CancellationToken ct) => throw new NotSupportedException();
    }

    private BillingReconciler Reconciler(InferenceApiDbContext db, BillingReconcileOptions? options = null, bool billingEnabled = true, IBillingProvider? provider = null, IBillingChangeLock? changeLock = null)
        => new(
            db,
            [provider ?? _provider],
            new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, _clock),
            changeLock ?? _lock,
            Options.Create(new BillingOptions { Enabled = billingEnabled }),
            Options.Create(options ?? new BillingReconcileOptions()),
            NullLogger<BillingReconciler>.Instance,
            tenant: null,
            time: _clock);

    private async Task<ReconcileResult> Reconcile(bool force = false, ReconcileTrigger trigger = ReconcileTrigger.Lazy)
    {
        await using InferenceApiDbContext db = NewDb();
        return await Reconciler(db).ReconcileAsync(_org, trigger, force, CancellationToken.None);
    }

    private async Task<BillingEventOutcome> Webhook(BillingEvent evt)
    {
        await using InferenceApiDbContext db = NewDb();
        return await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, _clock).ProcessAsync(evt, "{}");
    }

    private async Task RecordChange(string kind, DateTime at)
    {
        DateTime was = _clock.Now;
        _clock.Now = at;
        await using (InferenceApiDbContext db = NewDb())
        {
            await Reconciler(db).RecordChangeRequestedAsync(_org, kind, _user, CancellationToken.None);
        }

        _clock.Now = was;
    }

    /// <summary>A subscription webhook exactly as Paddle would deliver it (the real one, not synthetic).</summary>
    private BillingEvent Sub(string eventId, DateTime at, string price = "pri_creator", int qty = 1, SubscriptionStatus status = SubscriptionStatus.Active,
        DateTime? start = null, bool cancel = false) => new()
        {
            Provider = P,
            EventId = eventId,
            EventType = "subscription.updated",
            OccurredAt = at,
            Kind = BillingEventKind.SubscriptionChanged,
            OrganizationId = _org,
            CustomerId = "ctm_1",
            SubscriptionId = SubId,
            Status = status,
            PeriodStart = start ?? PS,
            PeriodEnd = (start ?? PS).AddMonths(1),
            CancelAtPeriodEnd = cancel,
            CancelEffectiveAt = cancel ? (start ?? PS).AddMonths(1) : null,
            Items = [new BillingLineItem(price, qty)]
        };

    /// <summary>What <c>SnapshotSubscriptionAsync</c> returns for that same live state, read at <paramref name="updatedAt"/>.</summary>
    private BillingSnapshot Snap(DateTime updatedAt, string price = "pri_creator", int qty = 1, SubscriptionStatus status = SubscriptionStatus.Active,
        DateTime? start = null, bool cancel = false)
        => new(
            Sub($"reconcile:sub:{SubId}:{updatedAt.Ticks}", updatedAt, price, qty, status, start, cancel) with { EventType = "subscription.reconciled" },
            "{\"event_type\":\"subscription.updated\"}",
            updatedAt);

    /// <summary>A paid proration transaction (Paddle <c>transaction.completed</c>, origin subscription_update).</summary>
    private BillingEvent Txn(string eventId, string txn, DateTime billedAt, long amount = 2500, string price = "pri_pro", int qty = 1) => new()
    {
        Provider = P,
        EventId = eventId,
        EventType = "transaction.completed",
        OccurredAt = billedAt,
        Kind = BillingEventKind.UpgradePaid,
        OrganizationId = _org,
        CustomerId = "ctm_1",
        SubscriptionId = SubId,
        TransactionId = txn,
        ProrationStartsAt = billedAt,
        AmountMinor = amount,
        Items = [new BillingLineItem(price, qty)]
    };

    private BillingSnapshot TxnSnap(string txn, DateTime billedAt, long amount = 2500, string price = "pri_pro", int qty = 1)
        => new(Txn($"reconcile:txn:{txn}", txn, billedAt, amount, price, qty) with { EventType = "transaction.reconciled" }, "{\"event_type\":\"transaction.completed\"}", billedAt);

    /// <summary>The org on Creator since <see cref="PS"/> through a real, delivered webhook (1 000 credits).</summary>
    private async Task SeedCreator(string price = "pri_creator")
        => (await Webhook(Sub("evt_created", PS, price))).Should().Be(BillingEventOutcome.Applied);

    private async Task<long> Balance()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.CreditAccounts.AsNoTracking().Where(a => a.OrganizationId == _org).Select(a => a.BalanceMillis).SingleOrDefaultAsync();
    }

    private async Task<Guid> PlanId(string key)
    {
        await using InferenceApiDbContext db = NewDb();
        return (await db.Plans.SingleAsync(p => p.Key == key)).Id;
    }

    private async Task<Subscription> Row()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.Subscriptions.AsNoTracking().SingleAsync(s => s.ProviderSubscriptionId == SubId);
    }

    private async Task<List<BillingPendingUpgradeGrant>> Pending()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.BillingPendingUpgradeGrants.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync();
    }

    private async Task<List<BillingWebhookEvent>> EventRows()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.BillingWebhookEvents.AsNoTracking().OrderBy(r => r.ReceivedAt).ToListAsync();
    }

    private async Task<int> LedgerEntries()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.CreditLedgerEntries.CountAsync();
    }

    private async Task<BillingReconcileWatch?> Watch()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.BillingReconcileWatches.AsNoTracking().SingleOrDefaultAsync(w => w.OrganizationId == _org);
    }

    /// <summary>Everything a late webhook could touch, to compare before and after.</summary>
    private async Task<(long Balance, int Ledger, string Pending, string Row)> State()
    {
        Subscription row = await Row();
        string pending = string.Join(";", (await Pending()).Select(r =>
            $"{r.Id}|{r.TransactionId}|{r.GrantedMillis}|{r.GrantedAt:O}|{r.NewEntitlementMillis}|{r.EffectiveAt:O}|{r.ClawedBackAt:O}"));
        // LastEventAt/UpdatedAt are bookkeeping of "the newest event seen", not subscription state.
        string sub = $"{row.PlanId}|{row.Seats}|{row.Status}|{row.PeriodStart:O}|{row.PeriodEnd:O}|{row.CancelAtPeriodEnd}|{row.CancelEffectiveAt:O}|{row.ScheduledPlanId}|{row.ScheduledSeats}|{row.ScheduledChangeAt:O}";
        return (await Balance(), await LedgerEntries(), pending, sub);
    }

    /// <summary>Creator → Pro at <see cref="TU"/>; both the subscription and the transaction webhook were lost.</summary>
    private async Task<ReconcileResult> HealLostUpgrade()
    {
        await SeedCreator();
        _provider.Snapshot = Snap(TU, "pri_pro");
        _provider.Transactions.Add(TxnSnap("txn_up", TU));
        return await Reconcile();
    }

    private static long UpgradeGrant() => BillingEventProcessor.ProratedUpgradeGrantMillis(1_000_000, 3_500_000, PS, PE, TU);

    // ------------------------------------------------------------------ heal

    [Fact]
    public async Task LostUpgradeWebhook_Healed()
    {
        await RecordChange("change_plan", TU);

        ReconcileResult result = await HealLostUpgrade();

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        result.Drift.Should().Equal("plan");
        result.SubscriptionEventsApplied.Should().Be(1);
        result.TransactionsApplied.Should().Be(1);
        result.Error.Should().BeNull();
        (await Row()).PlanId.Should().Be(await PlanId("pro"));
        UpgradeGrant().Should().BePositive();
        (await Balance()).Should().Be(1_000_000 + UpgradeGrant(), "the upgrade is granted once its real paid proration transaction pairs");
        BillingPendingUpgradeGrant pending = (await Pending()).Single();
        pending.TransactionId.Should().Be("txn_up");
        pending.GrantedAt.Should().NotBeNull();
        pending.EffectiveAt.Should().Be(TU, "the synthetic event carries Paddle's updated_at, not now");

        List<BillingWebhookEvent> rows = await EventRows();
        rows.Select(r => r.EventId).Should().BeEquivalentTo([$"reconcile:sub:{SubId}:{TU.Ticks}", "reconcile:txn:txn_up", "evt_created"]);
        rows.Single(r => r.EventId.StartsWith("reconcile:sub:", StringComparison.Ordinal)).EventType.Should().Be("subscription.reconciled");

        BillingReconcileWatch watch = (await Watch())!;
        watch.LastOutcome.Should().Be("Healed");
        watch.ChangeRequestedAt.Should().BeNull("the change it was waiting on has landed");
        watch.ConsecutiveFailures.Should().Be(0);
        watch.LastCheckedAt.Should().Be(_clock.Now);
        _provider.TransactionCalls.Single().Since.Should().Be(PS.AddDays(-1));
    }

    [Fact]
    public async Task TooRecent_AppliesNothing()
    {
        _clock.Now = TU.AddMinutes(2);

        ReconcileResult result = await HealLostUpgrade();

        result.Outcome.Should().Be(ReconcileOutcome.TooRecent);
        result.Drift.Should().Equal("plan");
        result.SubscriptionEventsApplied.Should().Be(0);
        _provider.TransactionCalls.Should().BeEmpty();
        (await Row()).PlanId.Should().Be(await PlanId("creator"));
        (await EventRows()).Should().ContainSingle("only the seeding webhook");
        (await Watch())!.LastOutcome.Should().Be("TooRecent");
    }

    [Fact]
    public async Task Force_IgnoresThreshold()
    {
        _clock.Now = TU.AddMinutes(2);
        await SeedCreator();
        _provider.Snapshot = Snap(TU, "pri_pro");
        _provider.Transactions.Add(TxnSnap("txn_up", TU));

        ReconcileResult result = await Reconcile(force: true, trigger: ReconcileTrigger.Admin);

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        (await Row()).PlanId.Should().Be(await PlanId("pro"));
        (await Balance()).Should().Be(1_000_000 + UpgradeGrant());
    }

    // ------------------------------------------------------------------ the real webhook arrives after all

    [Fact]
    public async Task LateRealWebhook_SameInstant_IsStale_NoChange()
    {
        (await HealLostUpgrade()).Outcome.Should().Be(ReconcileOutcome.Healed);
        var before = await State();

        (await Webhook(Sub("evt_upgrade_real", TU, "pri_pro"))).Should().Be(BillingEventOutcome.Stale);

        (await State()).Should().Be(before);
    }

    [Fact]
    public async Task LateRealWebhook_OneSecondLater_ChangesNothing()
    {
        (await HealLostUpgrade()).Outcome.Should().Be(ReconcileOutcome.Healed);
        var before = await State();

        // Paddle stamps occurred_at a moment after updated_at: the real event is newer, so it is applied…
        (await Webhook(Sub("evt_upgrade_real", TU.AddSeconds(1), "pri_pro"))).Should().Be(BillingEventOutcome.Applied);
        // …and so is the real payment webhook, already on its upgrade row.
        (await Webhook(Txn("evt_txn_real", "txn_up", TU))).Should().Be(BillingEventOutcome.Applied);

        // …but it describes what is already there: no second refill, no second upgrade row, no second grant.
        (await State()).Should().Be(before);
        (await Pending()).Should().ContainSingle();
    }

    [Fact]
    public async Task OlderRealWebhook_NoFlipBack()
    {
        (await HealLostUpgrade()).Outcome.Should().Be(ReconcileOutcome.Healed);
        var before = await State();

        // A delayed update from before the upgrade (still Creator) must not undo it.
        (await Webhook(Sub("evt_old_real", TU.AddMinutes(-3)))).Should().Be(BillingEventOutcome.Stale);

        (await State()).Should().Be(before);
        (await Row()).PlanId.Should().Be(await PlanId("pro"));
    }

    // ------------------------------------------------------------------ fail closed

    [Fact]
    public async Task ProviderDown_KeepsWatch_NoEventRows()
    {
        await SeedCreator();
        await RecordChange("change_plan", TU);
        _provider.SnapshotFail = new HttpRequestException("connection refused (secret-token-xyz)");

        ReconcileResult first = await Reconcile();
        ReconcileResult second = await Reconcile();

        first.Outcome.Should().Be(ReconcileOutcome.ProviderUnavailable);
        first.Error.Should().NotBeNullOrWhiteSpace();
        first.Error.Should().NotContain("secret-token-xyz", "no raw provider text is stored");
        second.Outcome.Should().Be(ReconcileOutcome.ProviderUnavailable);
        (await EventRows()).Should().ContainSingle("only the seeding webhook");
        (await Row()).PlanId.Should().Be(await PlanId("creator"));

        BillingReconcileWatch watch = (await Watch())!;
        watch.ChangeRequestedAt.Should().Be(TU, "the watch is kept for the next try");
        watch.ChangeKind.Should().Be("change_plan");
        watch.ChangeRequestedBy.Should().Be(_user);
        watch.ConsecutiveFailures.Should().Be(2);
        watch.LastOutcome.Should().Be("ProviderUnavailable");
        watch.LastError.Should().Be(first.Error);
        watch.LastError!.Length.Should().BeLessThanOrEqualTo(500);
    }

    [Fact]
    public async Task TransactionsFail_NothingApplied()
    {
        await SeedCreator();
        await RecordChange("change_plan", TU);
        _provider.Snapshot = Snap(TU, "pri_pro");
        _provider.TransactionsFail = new HttpRequestException("timeout");

        ReconcileResult result = await Reconcile();

        result.Outcome.Should().Be(ReconcileOutcome.ProviderUnavailable);
        result.SubscriptionEventsApplied.Should().Be(0);
        (await Row()).PlanId.Should().Be(await PlanId("creator"), "every provider read happens before any write");
        (await EventRows()).Should().ContainSingle();
        (await Pending()).Should().BeEmpty();
        (await Watch())!.ChangeRequestedAt.Should().Be(TU);
    }

    [Fact]
    public async Task Provider404_FailsClosed()
    {
        await SeedCreator();
        await RecordChange("cancel", TU);
        _provider.SnapshotFail = new BillingProviderException("not_found", 404, "Subscription sub_1 not found (raw body)");

        ReconcileResult result = await Reconcile(force: true, trigger: ReconcileTrigger.Admin);

        result.Outcome.Should().Be(ReconcileOutcome.ProviderUnavailable);
        result.Error.Should().Contain("404");
        result.Error.Should().NotContain("raw body");
        (await EventRows()).Should().ContainSingle();
        BillingReconcileWatch watch = (await Watch())!;
        watch.ChangeRequestedAt.Should().Be(TU);
        watch.ConsecutiveFailures.Should().Be(1);
        watch.LastError.Should().Contain("404");
    }

    [Fact]
    public async Task Provider404_OnTheRowsOwnSubscription_NamesTheReverseDrift()
    {
        // B12: the ledger holds a subscription Paddle no longer has. Same fail-closed outcome, but the
        // difference is named (ledger_only) rather than folded into an anonymous provider failure.
        await SeedCreator();
        await RecordChange("cancel", TU);
        _provider.SnapshotFail = new BillingProviderException("subscription_not_found", 404, "Subscription sub_1 not found");

        ReconcileResult result = await Reconcile(force: true, trigger: ReconcileTrigger.Admin);

        result.Outcome.Should().Be(ReconcileOutcome.ProviderUnavailable);
        result.Drift.Should().Contain(SubscriptionDrift.LedgerOnlyField);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconcile_SnapshotWithoutUpdatedAt_FailsClosed(bool watched)
    {
        // Without Paddle's updated_at the synthetic event would be stamped "now" and outrank any real webhook,
        // and nothing says how old the change is: not reconciled, with or without a watch to anchor on.
        await SeedCreator();
        if (watched) await RecordChange("change_plan", TU);
        _provider.Snapshot = Snap(TU, "pri_pro") with { ProviderUpdatedAt = null };
        _provider.Transactions.Add(TxnSnap("txn_up", TU));

        ReconcileResult result = await Reconcile(force: true, trigger: ReconcileTrigger.Admin);

        result.Outcome.Should().Be(ReconcileOutcome.ProviderUnavailable);
        result.Error.Should().Be("provider returned no updated_at");
        result.SubscriptionEventsApplied.Should().Be(0);
        result.TransactionsApplied.Should().Be(0);
        (await Row()).PlanId.Should().Be(await PlanId("creator"));
        (await Balance()).Should().Be(1_000_000);
        (await EventRows()).Should().ContainSingle("only the seeding webhook");
        (await Pending()).Should().BeEmpty();
        BillingReconcileWatch watch = (await Watch())!;
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.ProviderUnavailable));
        watch.LastError.Should().Be("provider returned no updated_at");
        watch.ConsecutiveFailures.Should().Be(1);
    }

    /// <summary>A Paddle provider whose <c>GET</c>s answer with the given bodies (subscription, transactions).</summary>
    private static PaddleBillingProvider Paddle(Func<string, string> body)
        => PaddlePayloads.Provider(new PaddlePayloads.StubHandler(req => PaddlePayloads.Json(body(req.RequestUri!.AbsolutePath))));

    /// <summary>The <c>data</c> of a Paddle webhook fixture as the API returns it, with fields overridden.</summary>
    private static string ApiEntity(string webhookEnvelope, params (string Key, string? Value)[] extra)
    {
        System.Text.Json.Nodes.JsonObject data = System.Text.Json.Nodes.JsonNode.Parse(webhookEnvelope)!["data"]!.AsObject().DeepClone().AsObject();
        foreach ((string key, string? value) in extra)
        {
            data[key] = value;
        }

        return data.ToJsonString();
    }

    [Theory]
    [InlineData("<html>bad gateway</html>")]
    [InlineData("""{"meta":{}}""")]
    [InlineData("""[{"data":{}}]""")]
    [InlineData("""{"data":{"id":"sub_1","status":"active","items":"not-a-list","custom_data":{"organization_id":42}}}""")]
    public async Task Reconcile_MalformedProviderBody_IsProviderUnavailable(string subscriptionBody)
    {
        await SeedCreator();
        await RecordChange("change_plan", TU);
        PaddleBillingProvider paddle = Paddle(_ => subscriptionBody);
        await using (InferenceApiDbContext seed = NewDb())
        {
            Subscription row = await seed.Subscriptions.SingleAsync(s => s.ProviderSubscriptionId == SubId);
            row.Provider = paddle.Name;
            await seed.SaveChangesAsync();
        }

        ReconcileResult result;
        await using (InferenceApiDbContext db = NewDb())
        {
            result = await Reconciler(db, provider: paddle).ReconcileAsync(_org, ReconcileTrigger.Admin, true, CancellationToken.None);
        }

        result.Outcome.Should().Be(ReconcileOutcome.ProviderUnavailable);
        result.Error.Should().Be("provider returned a malformed response");
        (await Row()).PlanId.Should().Be(await PlanId("creator"));
        (await EventRows()).Should().ContainSingle();
        BillingReconcileWatch watch = (await Watch())!;
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.ProviderUnavailable));
        watch.ConsecutiveFailures.Should().Be(1);
        watch.ChangeRequestedAt.Should().Be(TU, "the change stays due");
    }

    [Theory]
    [InlineData(typeof(System.Text.Json.JsonException))]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Reconcile_MalformedTransactionsBody_IsProviderUnavailable(Type exception)
    {
        await SeedCreator();
        _provider.Snapshot = Snap(TU, "pri_pro");
        _provider.TransactionsFail = (Exception)Activator.CreateInstance(exception, "bad body")!;

        ReconcileResult result = await Reconcile();

        result.Outcome.Should().Be(ReconcileOutcome.ProviderUnavailable);
        result.Error.Should().Be("provider returned a malformed response");
        (await Row()).PlanId.Should().Be(await PlanId("creator"), "every provider read happens before any write");
        (await Watch())!.ConsecutiveFailures.Should().Be(1);
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":null}""")]
    [InlineData("""{"data":"none"}""")]
    [InlineData("""{"meta":{}}""")]
    public async Task Reconcile_TransactionsDataNotArray_IsProviderUnavailable(string transactionsBody)
    {
        // Security S-4: a 2xx list whose data is not an array is not "no payments": nothing is applied.
        await using (InferenceApiDbContext seed = NewDb())
        {
            (await new BillingEventProcessor(seed, NullLogger<BillingEventProcessor>.Instance, _clock)
                .ProcessAsync(Sub("evt_created", PS) with { Provider = "paddle" }, "{}")).Should().Be(BillingEventOutcome.Applied);
        }

        await RecordChange("change_plan", TU);
        DateTimeOffset tu = new(TU, TimeSpan.Zero);
        string subscription = ApiEntity(PaddlePayloads.Subscription("evt_s", tu, _org, price: "pri_pro", start: new DateTimeOffset(PS, TimeSpan.Zero), subId: SubId),
            ("updated_at", tu.ToString("O")));
        PaddleBillingProvider paddle = Paddle(path => path.EndsWith("/transactions", StringComparison.Ordinal)
            ? transactionsBody
            : "{\"data\":" + subscription + "}");

        ReconcileResult result;
        await using (InferenceApiDbContext db = NewDb())
        {
            result = await Reconciler(db, provider: paddle).ReconcileAsync(_org, ReconcileTrigger.Lazy, false, CancellationToken.None);
        }

        result.Outcome.Should().Be(ReconcileOutcome.ProviderUnavailable);
        result.Error.Should().Be(BillingReconciler.MalformedError);
        (await Row()).PlanId.Should().Be(await PlanId("creator"), "every provider read happens before any write");
        (await EventRows()).Should().ContainSingle();
        BillingReconcileWatch watch = (await Watch())!;
        watch.ChangeRequestedAt.Should().Be(TU);
        watch.ConsecutiveFailures.Should().Be(1);
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":null}""")]
    public async Task Reconcile_LiveListingDataNotArray_IsProviderUnavailable(string listingBody)
    {
        // The checkout path: no row, so the customer's live subscriptions are listed. A non-array listing is not "none".
        await using (InferenceApiDbContext seed = NewDb())
        {
            seed.BillingCustomers.Add(new BillingCustomer { Id = Guid.NewGuid(), OrganizationId = _org, Provider = "paddle", ProviderCustomerId = "ctm_1" });
            await seed.SaveChangesAsync();
        }

        await RecordChange("checkout", TU);
        PaddleBillingProvider paddle = Paddle(_ => listingBody);

        ReconcileResult result;
        await using (InferenceApiDbContext db = NewDb())
        {
            result = await Reconciler(db, provider: paddle).ReconcileAsync(_org, ReconcileTrigger.Sweep, false, CancellationToken.None);
        }

        result.Outcome.Should().Be(ReconcileOutcome.ProviderUnavailable);
        result.Error.Should().Be(BillingReconciler.MalformedError);
        BillingReconcileWatch watch = (await Watch())!;
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.ProviderUnavailable));
        watch.ConsecutiveFailures.Should().Be(1);
    }

    [Fact]
    public async Task Reconcile_NoBillingCustomer_IsNothingToReconcile_WithNoCustomerError()
    {
        // Security low: "no customer row" is not "Paddle has no subscription" — the watch says which.
        await RecordChange("checkout", TU);

        ReconcileResult result = await Reconcile(trigger: ReconcileTrigger.Sweep);

        result.Outcome.Should().Be(ReconcileOutcome.NothingToReconcile);
        result.Error.Should().Be(BillingReconciler.NoCustomerError);
        BillingReconcileWatch watch = (await Watch())!;
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.NothingToReconcile));
        watch.LastError.Should().Be("no_customer");
        watch.ConsecutiveFailures.Should().Be(0);
        watch.ChangeRequestedAt.Should().Be(TU, "a young checkout stays watched");
        _provider.Listings.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordChangeRequested_KeepsAnOlderDueChange()
    {
        // QA-3: a second request (a repeated resume, a cancel already at Paddle) must not push the stuck clock out.
        await RecordChange("resume", TU);
        await RecordChange("cancel", TU.AddMinutes(8));

        BillingReconcileWatch watch = (await Watch())!;
        watch.ChangeRequestedAt.Should().Be(TU, "the older change is still due");
        watch.ChangeKind.Should().Be("cancel");
        watch.ChangeRequestedBy.Should().Be(_user);
    }

    [Fact]
    public async Task RecordChangeRequested_AfterTheLastChangeSettled_StartsANewOne()
    {
        await SeedCreator();
        await RecordChange("cancel", TU);
        _provider.Snapshot = Snap(PS);
        (await Reconcile()).Outcome.Should().Be(ReconcileOutcome.InSync);
        (await Watch())!.ChangeRequestedAt.Should().BeNull();

        await RecordChange("resume", TU.AddHours(1));

        (await Watch())!.ChangeRequestedAt.Should().Be(TU.AddHours(1));
    }

    // ------------------------------------------------------------------ the watch during a run

    [Fact]
    public async Task Reconcile_DoesNotClearWatchRecordedDuringRun()
    {
        await SeedCreator();
        await RecordChange("change_plan", TU);
        _provider.Snapshot = Snap(TU, "pri_pro");
        _provider.Transactions.Add(TxnSnap("txn_up", TU));
        // While the reconciler reads Paddle, the member starts a cancel: that change is not the one healed.
        _provider.DuringSnapshot = () => RecordChange("cancel", TU.AddMinutes(14));

        ReconcileResult result = await Reconcile();

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        BillingReconcileWatch watch = (await Watch())!;
        watch.ChangeRequestedAt.Should().Be(TU, "a change recorded during the run is still due (from the older due time, QA-3)");
        watch.ChangeKind.Should().Be("cancel");
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.Healed));
        watch.LastCheckedAt.Should().Be(_clock.Now);
    }

    [Fact]
    public async Task Reconcile_WatchInsertedDuringRun_IsKept()
    {
        await SeedCreator();
        _provider.Snapshot = Snap(TU, "pri_pro");
        // No watch when the run starts; a checkout records the org's first one meanwhile.
        _provider.DuringSnapshot = () => RecordChange("checkout", TU.AddMinutes(14));

        ReconcileResult result = await Reconcile();

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        BillingReconcileWatch watch = (await Watch())!;
        watch.ChangeRequestedAt.Should().Be(TU.AddMinutes(14));
        watch.ChangeKind.Should().Be("checkout");
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.Healed));
    }

    /// <summary>Inserts a competing watch for the org right before the context's first save.</summary>
    private sealed class InsertWatchFirst(Func<Task> insert) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await insert();
            }

            return result;
        }
    }

    [Fact]
    public async Task RecordChangeRequested_InsertRace_UpdatesTheConcurrentRow()
    {
        InsertWatchFirst race = new(async () =>
        {
            await using InferenceApiDbContext other = NewDb();
            other.BillingReconcileWatches.Add(new BillingReconcileWatch { OrganizationId = _org, ChangeKind = "cancel", ChangeRequestedAt = TU, LastOutcome = "InSync" });
            await other.SaveChangesAsync();
        });
        await using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).AddInterceptors(race).Options);
        _clock.Now = TU.AddMinutes(5);

        await Reconciler(db).RecordChangeRequestedAsync(_org, "change_plan", _user, CancellationToken.None);

        BillingReconcileWatch watch = (await Watch())!;
        watch.ChangeKind.Should().Be("change_plan");
        watch.ChangeRequestedAt.Should().Be(TU, "the concurrent row's change is older and still due (QA-3)");
        watch.ChangeRequestedBy.Should().Be(_user);
        watch.LastOutcome.Should().Be("InSync", "the concurrent row was updated, not replaced");
    }

    // ------------------------------------------------------------------ never credits by itself

    [Fact]
    public async Task NoPaidTransaction_NoUpgradeCredits()
    {
        await SeedCreator();
        _provider.Snapshot = Snap(TU, "pri_pro");

        ReconcileResult result = await Reconcile();

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        result.TransactionsApplied.Should().Be(0);
        (await Row()).PlanId.Should().Be(await PlanId("pro"));
        (await Balance()).Should().Be(1_000_000);
        BillingPendingUpgradeGrant pending = (await Pending()).Single();
        pending.TransactionId.Should().BeNull("the upgrade waits for its payment");
        pending.GrantedAt.Should().BeNull();
    }

    [Fact]
    public async Task Reconcile_BilledTransactionListed_GrantsNothing()
    {
        // The org on Creator through a delivered Paddle webhook; then upgraded at TU, both webhooks lost.
        await using (InferenceApiDbContext seed = NewDb())
        {
            (await new BillingEventProcessor(seed, NullLogger<BillingEventProcessor>.Instance, _clock)
                .ProcessAsync(Sub("evt_created", PS) with { Provider = "paddle" }, "{}")).Should().Be(BillingEventOutcome.Applied);
        }

        DateTimeOffset tu = new(TU, TimeSpan.Zero);
        string subscription = ApiEntity(PaddlePayloads.Subscription("evt_s", tu, _org, price: "pri_pro", start: new DateTimeOffset(PS, TimeSpan.Zero), subId: SubId),
            ("updated_at", tu.ToString("O")));
        // Paddle lists the proration transaction although it is only billed (unpaid), plus a past_due one and
        // another subscription's completed one: none of them is this subscription's paid upgrade.
        string billed = ApiEntity(PaddlePayloads.UpgradeTransaction("evt_t1", tu, _org, txn: "txn_billed", subId: SubId, prorationStart: tu),
            ("status", "billed"), ("billed_at", tu.ToString("O")));
        string pastDue = ApiEntity(PaddlePayloads.UpgradeTransaction("evt_t2", tu, _org, txn: "txn_past_due", subId: SubId, prorationStart: tu),
            ("status", "past_due"), ("billed_at", tu.ToString("O")));
        string foreign = ApiEntity(PaddlePayloads.UpgradeTransaction("evt_t3", tu, _org, txn: "txn_foreign", subId: "sub_other", prorationStart: tu),
            ("billed_at", tu.ToString("O")));
        PaddleBillingProvider paddle = Paddle(path => path.EndsWith("/transactions", StringComparison.Ordinal)
            ? "{\"data\":[" + billed + "," + pastDue + "," + foreign + "]}"
            : "{\"data\":" + subscription + "}");

        ReconcileResult result;
        await using (InferenceApiDbContext db = NewDb())
        {
            result = await Reconciler(db, provider: paddle).ReconcileAsync(_org, ReconcileTrigger.Lazy, false, CancellationToken.None);
        }

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        result.SubscriptionEventsApplied.Should().Be(1);
        result.TransactionsApplied.Should().Be(0);
        (await Row()).PlanId.Should().Be(await PlanId("pro"));
        (await Balance()).Should().Be(1_000_000, "an unpaid or foreign transaction never pays for the upgrade");
        BillingPendingUpgradeGrant pending = (await Pending()).Single();
        pending.TransactionId.Should().BeNull("the upgrade still waits for its payment");
        pending.GrantedAt.Should().BeNull();
        (await EventRows()).Should().NotContain(r => r.EventId.StartsWith("reconcile:txn:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ZeroAmountTransaction_NoCredits()
    {
        await SeedCreator();
        _provider.Snapshot = Snap(TU, "pri_pro");
        _provider.Transactions.Add(TxnSnap("txn_zero", TU, amount: 0));

        (await Reconcile()).Outcome.Should().Be(ReconcileOutcome.Healed);

        (await Balance()).Should().Be(1_000_000);
        BillingPendingUpgradeGrant pending = (await Pending()).Single();
        pending.TransactionId.Should().Be("txn_zero");
        pending.GrantedMillis.Should().Be(0);
    }

    [Fact]
    public async Task ItemsMismatch_NoCredits()
    {
        await SeedCreator();
        _provider.Snapshot = Snap(TU, "pri_pro");
        _provider.Transactions.Add(TxnSnap("txn_other", TU, price: "pri_team", qty: 3));

        (await Reconcile()).Outcome.Should().Be(ReconcileOutcome.Healed);

        (await Balance()).Should().Be(1_000_000);
        (await Pending()).Should().OnlyContain(r => r.GrantedMillis == null || r.GrantedMillis == 0);
        (await Pending()).Single(r => r.NewEntitlementMillis != null).TransactionId.Should().BeNull();
    }

    [Fact]
    public async Task PastDue_NoCredits()
    {
        await SeedCreator();
        _provider.Snapshot = Snap(TU, "pri_pro", status: SubscriptionStatus.PastDue);
        _provider.Transactions.Add(TxnSnap("txn_up", TU));

        ReconcileResult result = await Reconcile();

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        result.Drift.Should().BeEquivalentTo(["plan", "status"]);
        (await Row()).Status.Should().Be(SubscriptionStatus.PastDue);
        (await Balance()).Should().Be(1_000_000, "an upgrade paid while past due waits until the subscription is Active again");
        (await Pending()).Single().GrantedAt.Should().BeNull();
    }

    [Fact]
    public async Task PairedTransaction_GrantsOnce_LateTransactionWebhookIsNoOp()
    {
        (await HealLostUpgrade()).Outcome.Should().Be(ReconcileOutcome.Healed);
        var before = await State();

        (await Webhook(Txn("evt_txn_real", "txn_up", TU))).Should().Be(BillingEventOutcome.Applied);
        ReconcileResult again = await Reconcile();

        again.Outcome.Should().Be(ReconcileOutcome.InSync, "the payment is already on its upgrade row");
        again.TransactionsApplied.Should().Be(0);
        (await State()).Should().Be(before);
        (await Balance()).Should().Be(1_000_000 + UpgradeGrant());
    }

    [Fact]
    public async Task LostTransactionWebhookAlone_IsHealedWithoutDrift()
    {
        await SeedCreator();
        (await Webhook(Sub("evt_upgrade_real", TU, "pri_pro"))).Should().Be(BillingEventOutcome.Applied);
        _provider.Snapshot = Snap(TU, "pri_pro");
        _provider.Transactions.Add(TxnSnap("txn_up", TU));

        ReconcileResult result = await Reconcile();

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        result.Drift.Should().BeEmpty();
        result.SubscriptionEventsApplied.Should().Be(0);
        result.TransactionsApplied.Should().Be(1);
        (await Balance()).Should().Be(1_000_000 + UpgradeGrant());
    }

    // ------------------------------------------------------------------ what "in sync" means

    [Fact]
    public async Task AllWebhooksDelivered_IsInSync_NoEventRows()
    {
        await SeedCreator();
        (await Webhook(Sub("evt_upgrade_real", TU, "pri_pro"))).Should().Be(BillingEventOutcome.Applied);
        (await Webhook(Txn("evt_txn_real", "txn_up", TU))).Should().Be(BillingEventOutcome.Applied);
        var before = await State();
        int rows = (await EventRows()).Count;
        _provider.Snapshot = Snap(TU, "pri_pro");
        _provider.Transactions.Add(TxnSnap("txn_up", TU));

        ReconcileResult result = await Reconcile(trigger: ReconcileTrigger.Sweep);

        result.Outcome.Should().Be(ReconcileOutcome.InSync, "a payment already on its upgrade row is not fed again");
        result.TransactionsApplied.Should().Be(0);
        (await EventRows()).Should().HaveCount(rows);
        (await State()).Should().Be(before);
    }

    [Fact]
    public async Task HeldDowngrade_IsInSync()
    {
        await SeedCreator("pri_pro");
        // Pro → Creator, delivered: the row keeps Pro and holds Creator until the period ends.
        (await Webhook(Sub("evt_downgrade_real", TU))).Should().Be(BillingEventOutcome.Applied);
        Subscription held = await Row();
        held.ScheduledPlanId.Should().Be(await PlanId("creator"));
        var before = await State();
        int rows = (await EventRows()).Count;
        _provider.Snapshot = Snap(TU.AddMinutes(1));
        _clock.Now = TU.AddHours(2);

        ReconcileResult result = await Reconcile();

        result.Outcome.Should().Be(ReconcileOutcome.InSync);
        result.Drift.Should().BeEmpty("Paddle billing the held plan is the settled state");
        (await State()).Should().Be(before);
        (await Row()).PlanId.Should().Be(await PlanId("pro"), "the plan is not flipped early");
        (await EventRows()).Should().HaveCount(rows);
    }

    [Fact]
    public async Task LostKeepWebhook_HoldReleased_Healed()
    {
        // QA-1: Pro with a held downgrade to Creator; the member clicked "keep my plan", Paddle's items went back
        // to Pro, and that webhook was lost. The row still holds Creator, so every change would answer
        // 409 downgrade_scheduled until renewal.
        await SeedCreator("pri_pro");
        (await Webhook(Sub("evt_downgrade_real", TU))).Should().Be(BillingEventOutcome.Applied);
        (await Row()).ScheduledPlanId.Should().Be(await PlanId("creator"));
        long balance = await Balance();
        DateTime keptAt = TU.AddHours(1);
        await RecordChange("keep", keptAt);
        _provider.Snapshot = Snap(keptAt, "pri_pro");
        _clock.Now = keptAt.AddMinutes(15);

        ReconcileResult result = await Reconcile(trigger: ReconcileTrigger.Sweep);

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        result.Drift.Should().Equal(SubscriptionDrift.ScheduledChange);
        result.SubscriptionEventsApplied.Should().Be(1);
        Subscription row = await Row();
        row.PlanId.Should().Be(await PlanId("pro"));
        row.ScheduledPlanId.Should().BeNull("the processor treats Paddle back on the row's own plan as \"keep\"");
        row.ScheduledSeats.Should().BeNull();
        row.ScheduledChangeAt.Should().BeNull();
        (await Balance()).Should().Be(balance, "releasing a hold grants nothing");
        (await Watch())!.ChangeRequestedAt.Should().BeNull();
    }

    [Fact]
    public void Drift_ScheduledChange_DoesNotUnsettle()
    {
        Plan pro = new() { Id = Guid.NewGuid(), Key = "pro" };
        Subscription row = new() { PlanId = pro.Id, Seats = 1, ScheduledPlanId = Guid.NewGuid(), ScheduledSeats = 1, PeriodStart = PS, PeriodEnd = PE };

        SubscriptionDrift drift = SubscriptionDrift.Compute(row, pro, 1, PS, PE);

        drift.Fields.Should().Equal(SubscriptionDrift.ScheduledChange);
        drift.InSync.Should().BeFalse();
        drift.IsSettled.Should().BeTrue("the change endpoints' 409 rule is unchanged");
    }

    [Fact]
    public async Task CancelThenResume_SameState_SecondReconcileNotDuplicate()
    {
        await SeedCreator();
        DateTime canceledAt = TU;
        DateTime resumedAt = TU.AddHours(1);

        _provider.Snapshot = Snap(canceledAt, cancel: true);
        _clock.Now = canceledAt.AddMinutes(15);
        ReconcileResult cancel = await Reconcile();
        cancel.Outcome.Should().Be(ReconcileOutcome.Healed);
        cancel.Drift.Should().Equal("cancel_at_period_end");
        (await Row()).CancelAtPeriodEnd.Should().BeTrue();

        // Back to exactly the state before the cancel (whose own webhook was never delivered either).
        _provider.Snapshot = Snap(resumedAt);
        _clock.Now = resumedAt.AddMinutes(15);
        ReconcileResult resume = await Reconcile();

        resume.Outcome.Should().Be(ReconcileOutcome.Healed, "the event key carries updated_at, so the same state later is a new event");
        resume.SubscriptionEventsApplied.Should().Be(1);
        (await Row()).CancelAtPeriodEnd.Should().BeFalse();
        (await Row()).CancelEffectiveAt.Should().BeNull();
        (await Balance()).Should().Be(1_000_000);
        (await EventRows()).Select(r => r.Outcome).Should().Equal("Applied", "Applied", "Applied");
    }

    [Fact]
    public async Task SameStateTwice_SecondIsDuplicate_NoDoubleApply()
    {
        (await HealLostUpgrade()).Outcome.Should().Be(ReconcileOutcome.Healed);
        var before = await State();
        int rows = (await EventRows()).Count;

        ReconcileResult second = await Reconcile(force: true);
        // The same synthetic events fed again (another replica, a retried sweep) are the webhook's duplicates.
        (await Webhook(_provider.Snapshot!.Event)).Should().Be(BillingEventOutcome.Duplicate);
        (await Webhook(_provider.Transactions[0].Event)).Should().Be(BillingEventOutcome.Duplicate);

        second.Outcome.Should().Be(ReconcileOutcome.InSync);
        second.SubscriptionEventsApplied.Should().Be(0);
        second.TransactionsApplied.Should().Be(0);
        (await State()).Should().Be(before);
        (await EventRows()).Should().HaveCount(rows);
    }

    [Fact]
    public async Task RenewalLost_PeriodAdvanced_RefillOnce_LateWebhookNoDoubleRefill()
    {
        await SeedCreator();
        _provider.Snapshot = Snap(PE, start: PE);
        _clock.Now = PE.AddMinutes(20);

        ReconcileResult result = await Reconcile(trigger: ReconcileTrigger.Sweep);

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        result.Drift.Should().Equal("period");
        (await Row()).PeriodStart.Should().Be(PE);
        (await Balance()).Should().Be(2_000_000);
        var before = await State();

        (await Webhook(Sub("evt_renewal_real", PE.AddSeconds(1), start: PE))).Should().Be(BillingEventOutcome.Applied);

        (await State()).Should().Be(before);
        (await Balance()).Should().Be(2_000_000, "the period refill key is the same");
    }

    [Fact]
    public async Task CheckoutCreatedLost_RowCreatedFromCustomerListing()
    {
        await using (InferenceApiDbContext seed = NewDb())
        {
            seed.BillingCustomers.Add(new BillingCustomer { Id = Guid.NewGuid(), OrganizationId = _org, Provider = P, ProviderCustomerId = "ctm_1" });
            await seed.SaveChangesAsync();
        }

        await RecordChange("checkout", PS);
        _provider.LiveSubscriptionId = SubId;
        _provider.Snapshot = Snap(PS);
        _clock.Now = PS.AddMinutes(15);

        ReconcileResult result = await Reconcile();

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        result.Drift.Should().Equal("subscription_missing");
        _provider.Listings.Should().Equal(("ctm_1", _org));
        _provider.SnapshotCalls.Should().Equal(SubId);
        Subscription row = await Row();
        row.OrganizationId.Should().Be(_org);
        row.PlanId.Should().Be(await PlanId("creator"));
        (await Balance()).Should().Be(1_000_000);
        (await Watch())!.ChangeRequestedAt.Should().BeNull();
    }

    [Fact]
    public async Task NoLiveSubscription_YoungCheckoutWatchKept_OldOneCleared()
    {
        await using (InferenceApiDbContext seed = NewDb())
        {
            seed.BillingCustomers.Add(new BillingCustomer { Id = Guid.NewGuid(), OrganizationId = _org, Provider = P, ProviderCustomerId = "ctm_1" });
            await seed.SaveChangesAsync();
        }

        await RecordChange("checkout", PS);
        _clock.Now = PS.AddHours(1);

        (await Reconcile()).Outcome.Should().Be(ReconcileOutcome.NothingToReconcile);
        (await Watch())!.ChangeRequestedAt.Should().Be(PS, "a checkout may still be paid");

        _clock.Now = PS.AddHours(49);
        (await Reconcile()).Outcome.Should().Be(ReconcileOutcome.NothingToReconcile);
        (await Watch())!.ChangeRequestedAt.Should().BeNull("an abandoned checkout stops being watched after CheckoutWatchHours");
        _provider.SnapshotCalls.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ claim, switches, watch, options, metrics

    [Fact]
    public async Task ReconcileAsync_ClaimHeld_IsBusy()
    {
        await SeedCreator();
        _provider.Snapshot = Snap(TU, "pri_pro");
        await using IAsyncDisposable? claim = await _lock.TryAcquireAsync(_org, CancellationToken.None);

        ReconcileResult result = await Reconcile();

        result.Outcome.Should().Be(ReconcileOutcome.Busy);
        _provider.SnapshotCalls.Should().BeEmpty();
        (await Row()).PlanId.Should().Be(await PlanId("creator"));
    }

    /// <summary>S-4/S-23: every lock connection slot of this replica is taken.</summary>
    private sealed class AtCapacityLock : IBillingChangeLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(Guid organizationId, CancellationToken ct)
            => throw new BillingChangeLockCapacityException();
    }

    [Fact]
    public async Task Reconcile_reports_busy_on_capacity()
    {
        await SeedCreator();
        _provider.Snapshot = Snap(TU, "pri_pro");

        await using InferenceApiDbContext db = NewDb();
        ReconcileResult result = await Reconciler(db, changeLock: new AtCapacityLock())
            .ReconcileAsync(_org, ReconcileTrigger.Sweep, false, CancellationToken.None);

        result.Outcome.Should().Be(ReconcileOutcome.Busy);
        _provider.SnapshotCalls.Should().BeEmpty();
        (await Row()).PlanId.Should().Be(await PlanId("creator"));
    }

    [Fact]
    public async Task ReconcileHeldAsync_DoesNotTakeTheClaim()
    {
        await SeedCreator();
        _provider.Snapshot = Snap(TU, "pri_pro");
        await using IAsyncDisposable? claim = await _lock.TryAcquireAsync(_org, CancellationToken.None);

        await using InferenceApiDbContext db = NewDb();
        ReconcileResult result = await Reconciler(db).ReconcileHeldAsync(_org, ReconcileTrigger.Lazy, false, CancellationToken.None);

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Disabled_IsANoOp(bool billingEnabled, bool reconcileEnabled)
    {
        await SeedCreator();
        _provider.Snapshot = Snap(TU, "pri_pro");

        await using InferenceApiDbContext db = NewDb();
        BillingReconciler reconciler = Reconciler(db, new BillingReconcileOptions { Enabled = reconcileEnabled }, billingEnabled);
        ReconcileResult result = await reconciler.ReconcileAsync(_org, ReconcileTrigger.Admin, true, CancellationToken.None);
        await reconciler.RecordChangeRequestedAsync(_org, "cancel", _user, CancellationToken.None);

        result.Outcome.Should().Be(ReconcileOutcome.NothingToReconcile);
        _provider.SnapshotCalls.Should().BeEmpty();
        (await Watch()).Should().BeNull();
    }

    [Fact]
    public async Task RecordChangeRequested_UpsertsAndKeepsLastChecked()
    {
        await SeedCreator();
        _provider.SnapshotFail = new HttpRequestException("down");
        await RecordChange("cancel", TU);
        await Reconcile();
        BillingReconcileWatch failed = (await Watch())!;

        await RecordChange("resume", TU.AddMinutes(30));

        BillingReconcileWatch watch = (await Watch())!;
        watch.ChangeKind.Should().Be("resume");
        watch.ChangeRequestedAt.Should().Be(TU, "the failed cancel is still due: a later request never pushes it out (QA-3)");
        watch.ChangeRequestedBy.Should().Be(_user);
        watch.LastCheckedAt.Should().Be(failed.LastCheckedAt);
        watch.LastOutcome.Should().Be("ProviderUnavailable");
        watch.ConsecutiveFailures.Should().Be(1);
    }

    [Fact]
    public void Options_HaveThePlanDefaults_AndRejectNonPositiveValues()
    {
        BillingReconcileOptions defaults = new();
        defaults.Enabled.Should().BeTrue();
        defaults.StuckAfterSeconds.Should().Be(600);
        defaults.SweepIntervalSeconds.Should().Be(300);
        defaults.StartupDelaySeconds.Should().Be(60);
        defaults.MaxPerSweep.Should().Be(50);
        defaults.RecheckAfterMinutes.Should().Be(360);
        defaults.CheckoutWatchHours.Should().Be(48);
        defaults.CheckoutRecheckMinutes.Should().Be(30);
        defaults.CheckoutRecheck.Should().Be(TimeSpan.FromMinutes(30));
        BillingReconcileOptions.SectionName.Should().Be("Billing:Reconcile");

        BillingReconcileOptionsValidator validator = new();
        validator.Validate(null, defaults).Succeeded.Should().BeTrue();
        validator.Validate(null, new BillingReconcileOptions { MaxPerSweep = 0 }).Failed.Should().BeTrue();
        validator.Validate(null, new BillingReconcileOptions { StuckAfterSeconds = -1 }).Failed.Should().BeTrue();
        validator.Validate(null, new BillingReconcileOptions { CheckoutWatchHours = 0 }).Failed.Should().BeTrue();
        validator.Validate(null, new BillingReconcileOptions { CheckoutRecheckMinutes = 0 }).Failed.Should().BeTrue();
    }

    [Fact]
    public async Task ReconcileRun_IsCounted_WithoutAnOrganizationTag()
    {
        List<(string Instrument, KeyValuePair<string, object?>[] Tags)> seen = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BillingWebhookDiagnostics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            lock (seen) seen.Add((instrument.Name, tags.ToArray()));
        });
        listener.Start();

        (await HealLostUpgrade()).Outcome.Should().Be(ReconcileOutcome.Healed);

        BillingWebhookDiagnostics.MeterName.Should().Be("ReelBolt.Inference.Billing.Webhooks");
        List<KeyValuePair<string, object?>[]> runs;
        lock (seen) runs = seen.Where(s => s.Instrument == "reelbolt.billing.reconcile.runs").Select(s => s.Tags).ToList();
        runs.Should().Contain(tags => tags.Any(t => t.Key == "trigger" && (string?)t.Value == "lazy") && tags.Any(t => t.Key == "outcome" && (string?)t.Value == "healed"));
        lock (seen) seen.SelectMany(s => s.Tags).Should().NotContain(t => t.Value is Guid || t.Key.Contains("org", StringComparison.OrdinalIgnoreCase));

        BillingWebhookDiagnostics.ReportBacklog(3, 7);
        listener.RecordObservableInstruments();
        lock (seen)
        {
            seen.Should().Contain(s => s.Instrument == "reelbolt.billing.reconcile.stuck_watches");
            seen.Should().Contain(s => s.Instrument == "reelbolt.billing.webhook.unresolved_24h");
        }
    }

    // ------------------------------------------------------------------ the webhook row's outcome

    [Fact]
    public async Task ProcessAsync_WritesOutcome()
    {
        (await Webhook(Sub("evt_applied", PS))).Should().Be(BillingEventOutcome.Applied);
        (await Webhook(Sub("evt_applied", PS))).Should().Be(BillingEventOutcome.Duplicate);
        (await Webhook(Sub("evt_stale", PS.AddMinutes(-1)))).Should().Be(BillingEventOutcome.Stale);
        (await Webhook(Sub("evt_unresolved", PS.AddMinutes(1)) with { SubscriptionId = "sub_other", Items = [new BillingLineItem("pri_nope", 1)] }))
            .Should().Be(BillingEventOutcome.Unresolved);

        Dictionary<string, string?> outcomes = (await EventRows()).ToDictionary(r => r.EventId, r => r.Outcome);
        outcomes.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["evt_applied"] = "Applied",
            ["evt_stale"] = "Stale",
            ["evt_unresolved"] = "Unresolved"
        }, "a duplicate writes no second row and leaves the first one's outcome");
        (await EventRows()).Should().OnlyContain(r => r.ProcessedAt != null);
    }

    // ------------------------------------------------------------------ Promo discounts: the reconcile leaves promo discounts alone

    private const string DiscountId = "dsc_1";
    private readonly Guid _promotion = Guid.NewGuid();
    private readonly Guid _redemption = Guid.NewGuid();

    /// <summary>A recurring 20% promotion on Creator and Pro (Paddle discount dsc_1) whose discount sub_1 was bought with.</summary>
    private async Task SeedPromoRedemption(DateTime? removedAt = null, string? removedForPriceId = null)
    {
        await using InferenceApiDbContext db = NewDb();
        db.Promotions.Add(new Promotion
        {
            Id = _promotion,
            Kind = PromotionKind.PlanDiscount,
            Name = "Autumn",
            Code = "AUTUMN20",
            DiscountType = PromotionDiscountType.Percentage,
            DiscountAmount = "20",
            DurationKind = PromotionDuration.Forever,
            TargetPlanKeysJson = """["creator","pro"]""",
            TargetPriceIdsJson = """["pri_creator","pri_pro"]""",
            ProviderDiscountId = DiscountId,
            RedemptionCount = 1
        });
        db.PromotionRedemptions.Add(new PromotionRedemption
        {
            Id = _redemption,
            PromotionId = _promotion,
            OrganizationId = _org,
            Kind = PromotionRedemptionKind.DiscountApplied,
            ProviderTransactionId = "txn_first",
            ProviderSubscriptionId = SubId,
            DiscountTotal = "580",
            CurrencyCode = "USD",
            CreatedAt = PS,
            DiscountRemovedAt = removedAt,
            DiscountRemovedForPriceId = removedForPriceId
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Every promotion-side fact a reconcile could touch: the promotion's counter and every redemption row.</summary>
    private async Task<string> PromoState()
    {
        await using InferenceApiDbContext db = NewDb();
        Promotion promotion = await db.Promotions.AsNoTracking().SingleAsync(p => p.Id == _promotion);
        List<PromotionRedemption> rows = await db.PromotionRedemptions.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        return $"{promotion.RedemptionCount}|{promotion.Status}|{promotion.ProviderDiscountId}||" + string.Join(";", rows.Select(r =>
            $"{r.Id}|{r.Kind}|{r.Outcome}|{r.ProviderTransactionId}|{r.ProviderSubscriptionId}|{r.DiscountTotal}|{r.DiscountRemovedAt:O}|{r.DiscountRemovedForPriceId}"));
    }

    private async Task<PromotionRedemption> Redemption()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.PromotionRedemptions.AsNoTracking().SingleAsync(r => r.Id == _redemption);
    }

    /// <summary>Paddle's live subscription (and its upgrade payment) as read with its discount still on it.</summary>
    private BillingSnapshot DiscountedSnap(DateTime updatedAt, string price)
        => Snap(updatedAt, price) with
        {
            Event = Snap(updatedAt, price).Event with { DiscountId = DiscountId, DiscountTotal = "1580", CurrencyCode = "USD" },
            RawJson = "{\"event_type\":\"subscription.updated\",\"data\":{\"discount\":{\"id\":\"dsc_1\",\"ends_at\":null}}}"
        };

    [Theory]
    [InlineData(ReconcileTrigger.Lazy, false)]
    [InlineData(ReconcileTrigger.Sweep, false)]
    [InlineData(ReconcileTrigger.Admin, true)]
    public async Task Heal_WithALiveDiscount_NeitherStripsNorReappliesIt(ReconcileTrigger trigger, bool force)
    {
        // Creator → Pro at TU (Pro is a target: Paddle kept dsc_1); both webhooks were lost.
        await SeedCreator();
        await SeedPromoRedemption();
        await RecordChange("change_plan", TU);
        string before = await PromoState();
        _provider.Snapshot = DiscountedSnap(TU, "pri_pro");
        _provider.Transactions.Add(TxnSnap("txn_up", TU) with
        {
            Event = TxnSnap("txn_up", TU).Event with { DiscountId = DiscountId, DiscountTotal = "500", CurrencyCode = "USD" }
        });

        ReconcileResult result = await Reconcile(force, trigger);

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        (await Row()).PlanId.Should().Be(await PlanId("pro"));
        _provider.PlanChanges.Should().BeEmpty("a reconcile only reads Paddle; it never PATCHes the subscription or its discount");
        PromotionRedemption row = await Redemption();
        row.DiscountRemovedAt.Should().BeNull("only a member's plan change records a removal");
        row.DiscountRemovedForPriceId.Should().BeNull();
        (await PromoState()).Should().Be(before, "no redemption is added, counted or rewritten by a heal");
    }

    [Theory]
    [InlineData(ReconcileTrigger.Lazy, false)]
    [InlineData(ReconcileTrigger.Sweep, false)]
    [InlineData(ReconcileTrigger.Admin, true)]
    public async Task Heal_AfterAPlanChangeRemovedTheDiscount_KeepsTheRemovalRecord(ReconcileTrigger trigger, bool force)
    {
        // Creator → Team at TU left the promotion's targets: the PATCH removed dsc_1 and the controller recorded
        // it. Paddle's webhook for that change was lost, so the row is still on Creator.
        await SeedCreator();
        DateTime removedAt = TU.AddSeconds(-3);
        await SeedPromoRedemption(removedAt, "pri_team");
        await RecordChange("change_plan", TU);
        string before = await PromoState();
        _provider.Snapshot = Snap(TU, "pri_team", qty: 3);

        ReconcileResult result = await Reconcile(force, trigger);

        result.Outcome.Should().Be(ReconcileOutcome.Healed);
        (await Row()).PlanId.Should().Be(await PlanId("team"));
        _provider.PlanChanges.Should().BeEmpty("the heal never re-sends the change, with or without the discount");
        PromotionRedemption row = await Redemption();
        row.DiscountRemovedAt.Should().Be(removedAt);
        row.DiscountRemovedForPriceId.Should().Be("pri_team");
        (await PromoState()).Should().Be(before);
    }
}
