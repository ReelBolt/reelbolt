#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Reconcile;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The periodic reconcile sweep: it only picks candidates (a change whose webhook is overdue, a renewal or a
/// cancellation the provider never confirmed) and hands each to <see cref="IBillingReconciler.ReconcileAsync"/>
/// in its own scope. The reconciler keeps the watch row; these tests pin the selection, the backoff through
/// the watch's <c>LastCheckedAt</c>, the claim (a second replica is Busy) and per-org isolation. Sqlite in-memory.
/// </summary>
public sealed class BillingReconcileSweeperTests : IDisposable
{
    private const string P = "fake";

    /// <summary>The billing period the seeded subscriptions live in.</summary>
    private static readonly DateTime PS = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime PE = PS.AddMonths(1);

    /// <summary>Paddle's <c>updated_at</c> of a change whose webhook was lost.</summary>
    private static readonly DateTime TU = PS.AddDays(10);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeProvider _provider = new();
    private readonly MutableClock _clock = new(TU.AddMinutes(15));
    private readonly InProcessBillingChangeLock _lock = new();
    private readonly CapturingLoggerProvider _logs = new();

    public BillingReconcileSweeperTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Plans.AddRange(
            new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, PriceMonthlyUsd = 29, MonthlyCreditsMillis = 1_000_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_creator"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "pro", Name = "Pro", Kind = PlanKind.Subscription, PriceMonthlyUsd = 79, MonthlyCreditsMillis = 3_500_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_pro"}}""" });
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

    /// <summary>A provider whose live view per subscription id the test sets.</summary>
    private sealed class FakeProvider : IBillingProvider
    {
        public string Name => P;
        public bool IsConfigured => true;
        public IReadOnlyList<int> TopUpPacks => [];
        public string? TopUpPriceId(int credits) => null;

        public Dictionary<string, BillingSnapshot> Snapshots { get; } = [];
        public Dictionary<string, Exception> Failures { get; } = [];
        public List<string> SnapshotCalls { get; } = [];
        public List<string> FindLiveCalls { get; } = [];

        public Task<BillingSnapshot> SnapshotSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
        {
            SnapshotCalls.Add(providerSubscriptionId);
            if (Failures.TryGetValue(providerSubscriptionId, out Exception? fail)) throw fail;
            return Task.FromResult(Snapshots.TryGetValue(providerSubscriptionId, out BillingSnapshot? snap)
                ? snap
                : throw new InvalidOperationException($"No snapshot set for {providerSubscriptionId}."));
        }

        public Task<IReadOnlyList<BillingSnapshot>> CompletedUpgradeTransactionsAsync(string providerSubscriptionId, DateTime since, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<BillingSnapshot>>([]);

        public Task<string?> FindLiveSubscriptionIdAsync(string providerCustomerId, Guid organizationId, CancellationToken ct)
        {
            FindLiveCalls.Add(providerCustomerId);
            if (Failures.TryGetValue(providerCustomerId, out Exception? fail)) throw fail;
            return Task.FromResult(LiveSubscriptions.TryGetValue(providerCustomerId, out string? subId) ? subId : null);
        }

        /// <summary>Per customer id, the live subscription a checkout produced (none: the checkout was abandoned).</summary>
        public Dictionary<string, string> LiveSubscriptions { get; } = [];
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

    /// <summary>Keeps every log entry (level, event id, rendered message) so a test can read the sweep's warnings.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, EventId Id, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries) owner.Entries.Add((logLevel, eventId, formatter(state, exception)));
            }
        }
    }

    /// <summary>The API's own wiring of the reconciler, over the shared Sqlite connection and the test's clock and provider.</summary>
    private ServiceProvider Services(BillingReconcileOptions? options = null, bool billingEnabled = true, RecordingHandler? alertHook = null)
    {
        ServiceCollection services = new();
        if (alertHook is not null)
        {
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(alertHook));
            services.AddSingleton(Options.Create(new SpendReportOptions { AlertWebhookUrl = "https://alerts.internal/billing" }));
        }

        services.AddLogging(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        services.AddDbContext<InferenceApiDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<IBillingProvider>(_provider);
        services.AddSingleton<IBillingChangeLock>(_lock);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(Options.Create(new BillingOptions { Enabled = billingEnabled }));
        services.AddSingleton(Options.Create(options ?? new BillingReconcileOptions()));
        services.AddScoped(sp => new BillingEventProcessor(sp.GetRequiredService<InferenceApiDbContext>(), NullLogger<BillingEventProcessor>.Instance, _clock));
        services.AddScoped<IBillingReconciler>(sp => new BillingReconciler(
            sp.GetRequiredService<InferenceApiDbContext>(),
            sp.GetServices<IBillingProvider>(),
            sp.GetRequiredService<BillingEventProcessor>(),
            sp.GetRequiredService<IBillingChangeLock>(),
            sp.GetRequiredService<IOptions<BillingOptions>>(),
            sp.GetRequiredService<IOptions<BillingReconcileOptions>>(),
            sp.GetRequiredService<ILogger<BillingReconciler>>(),
            tenant: null,
            time: _clock));
        return services.BuildServiceProvider();
    }

    private static BillingReconcileSweeper Sweeper(ServiceProvider sp, BillingWebhookFailureTotals? failureTotals = null)
        => new(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<BillingOptions>>(),
            sp.GetRequiredService<IOptions<BillingReconcileOptions>>(),
            sp.GetRequiredService<ILogger<BillingReconcileSweeper>>(),
            failureTotals: failureTotals ?? new BillingWebhookFailureTotals());

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>The alert webhook: records every POSTed body.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
        }

        public SpendReportAlert Alert(int index)
            => System.Text.Json.JsonSerializer.Deserialize<List<SpendReportAlert>>(Bodies[index], new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Single();
    }

    private async Task<BillingSweepResult> Sweep(ServiceProvider sp, DateTime at)
    {
        _clock.Now = at;
        return await Sweeper(sp).SweepOnceAsync(at, CancellationToken.None);
    }

    private static BillingEvent Sub(Guid org, string subId, string eventId, DateTime at, string price = "pri_creator",
        SubscriptionStatus status = SubscriptionStatus.Active, DateTime? start = null, bool cancel = false) => new()
        {
            Provider = P,
            EventId = eventId,
            EventType = "subscription.updated",
            OccurredAt = at,
            Kind = BillingEventKind.SubscriptionChanged,
            OrganizationId = org,
            CustomerId = "ctm_" + subId,
            SubscriptionId = subId,
            Status = status,
            PeriodStart = start ?? PS,
            PeriodEnd = (start ?? PS).AddMonths(1),
            CancelAtPeriodEnd = cancel,
            CancelEffectiveAt = cancel ? (start ?? PS).AddMonths(1) : null,
            Items = [new BillingLineItem(price, 1)]
        };

    private void LiveState(Guid org, string subId, DateTime updatedAt, string price = "pri_creator",
        SubscriptionStatus status = SubscriptionStatus.Active, DateTime? start = null, bool cancel = false)
        => _provider.Snapshots[subId] = new BillingSnapshot(
            Sub(org, subId, $"reconcile:sub:{subId}:{updatedAt.Ticks}", updatedAt, price, status, start, cancel) with { EventType = "subscription.reconciled" },
            "{\"event_type\":\"subscription.updated\"}",
            updatedAt);

    /// <summary>A new organization on Creator since <see cref="PS"/>, through a real, delivered webhook.</summary>
    private async Task<Guid> SeedOrg(string subId, bool cancel = false)
    {
        Guid org = Guid.NewGuid();
        await using (InferenceApiDbContext db = NewDb())
        {
            db.Organizations.Add(new Organization { Id = org, Name = "o-" + subId, Slug = "o-" + subId, Kind = OrganizationKind.Team });
            await db.SaveChangesAsync();
        }

        await using (InferenceApiDbContext db = NewDb())
        {
            BillingEventOutcome outcome = await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, _clock)
                .ProcessAsync(Sub(org, subId, "evt_created_" + subId, PS, cancel: cancel), "{}");
            outcome.Should().Be(BillingEventOutcome.Applied);
        }

        return org;
    }

    /// <summary>A change accepted by Paddle at <paramref name="at"/> (what the controller records after its 202).</summary>
    private async Task RecordChange(ServiceProvider sp, Guid org, string kind, DateTime at)
    {
        DateTime was = _clock.Now;
        _clock.Now = at;
        using (IServiceScope scope = sp.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBillingReconciler>().RecordChangeRequestedAsync(org, kind, Guid.NewGuid(), CancellationToken.None);
        }

        _clock.Now = was;
    }

    private async Task<BillingReconcileWatch?> Watch(Guid org)
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.BillingReconcileWatches.AsNoTracking().SingleOrDefaultAsync(w => w.OrganizationId == org);
    }

    private async Task<Subscription> Row(string subId)
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.Subscriptions.AsNoTracking().SingleAsync(s => s.ProviderSubscriptionId == subId);
    }

    private async Task<string> PlanKey(string subId)
    {
        Subscription row = await Row(subId);
        await using InferenceApiDbContext db = NewDb();
        return (await db.Plans.SingleAsync(p => p.Id == row.PlanId)).Key;
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Sweep_HealsDueWatch()
    {
        using ServiceProvider sp = Services();
        Guid org = await SeedOrg("sub_a");
        await RecordChange(sp, org, "change_plan", TU);
        LiveState(org, "sub_a", TU, price: "pri_pro");

        BillingSweepResult result = await Sweep(sp, TU.AddMinutes(15));

        result.Candidates.Should().Be(1);
        _provider.SnapshotCalls.Should().Equal("sub_a");
        (await PlanKey("sub_a")).Should().Be("pro");
        BillingReconcileWatch watch = (await Watch(org))!;
        watch.ChangeRequestedAt.Should().BeNull("the lost webhook was healed");
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.Healed));
        result.Stuck.Should().Be(0);
    }

    [Fact]
    public async Task Sweep_SkipsTooRecent()
    {
        using ServiceProvider sp = Services();
        Guid org = await SeedOrg("sub_a");
        await RecordChange(sp, org, "change_plan", TU);
        LiveState(org, "sub_a", TU, price: "pri_pro");

        // 5 minutes after the change: its webhook may still be on the way.
        BillingSweepResult result = await Sweep(sp, TU.AddMinutes(5));

        result.Candidates.Should().Be(0);
        _provider.SnapshotCalls.Should().BeEmpty();
        (await PlanKey("sub_a")).Should().Be("creator");
        (await Watch(org))!.LastCheckedAt.Should().BeNull();
    }

    [Fact]
    public async Task Sweep_RenewalOverdue_Reconciled_ThenBackoff()
    {
        var options = new BillingReconcileOptions();
        using ServiceProvider sp = Services(options);
        Guid org = await SeedOrg("sub_a");
        // The period ended an hour ago and no renewal webhook came: Paddle says the renewal failed (past_due).
        DateTime now = PE.AddHours(1);
        LiveState(org, "sub_a", PE.AddMinutes(1), status: SubscriptionStatus.PastDue);

        BillingSweepResult first = await Sweep(sp, now);

        first.Candidates.Should().Be(1);
        _provider.SnapshotCalls.Should().HaveCount(1);
        (await Row("sub_a")).Status.Should().Be(SubscriptionStatus.PastDue);
        BillingReconcileWatch watch = (await Watch(org))!;
        watch.ChangeKind.Should().Be(BillingReconciler.OverdueKind);
        watch.ChangeRequestedAt.Should().BeNull();
        watch.LastCheckedAt.Should().Be(now);

        // Still past its period end (PastDue keeps the old period), but just checked: no provider call.
        BillingSweepResult second = await Sweep(sp, now.AddHours(1));

        second.Candidates.Should().Be(0);
        _provider.SnapshotCalls.Should().HaveCount(1, "an overdue org is not re-read inside RecheckAfter");

        // Once RecheckAfter has passed it is looked at again.
        BillingSweepResult third = await Sweep(sp, now + options.RecheckAfter + TimeSpan.FromMinutes(1));

        third.Candidates.Should().Be(1);
        _provider.SnapshotCalls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Sweep_CancelOverdue_IsACandidate()
    {
        using ServiceProvider sp = Services();
        Guid org = await SeedOrg("sub_a", cancel: true);
        // The cancellation took effect at PE; Paddle says canceled, its webhook was lost.
        LiveState(org, "sub_a", PE, status: SubscriptionStatus.Canceled, cancel: true);

        BillingSweepResult result = await Sweep(sp, PE.AddHours(1));

        result.Candidates.Should().Be(1);
        (await Row("sub_a")).Status.Should().Be(SubscriptionStatus.Canceled);
    }

    [Fact]
    public async Task Sweep_PaddleDown_KeepsWatch_CountsFailure()
    {
        using ServiceProvider sp = Services();
        Guid org = await SeedOrg("sub_a");
        await RecordChange(sp, org, "change_plan", TU);
        _provider.Failures["sub_a"] = new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable);

        DateTime now = TU.AddMinutes(15);
        BillingSweepResult result = await Sweep(sp, now);

        result.Candidates.Should().Be(1);
        (await PlanKey("sub_a")).Should().Be("creator", "a provider failure applies nothing");
        BillingReconcileWatch watch = (await Watch(org))!;
        watch.ChangeRequestedAt.Should().Be(TU, "the change is still due");
        watch.ConsecutiveFailures.Should().Be(1);
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.ProviderUnavailable));
        watch.LastError.Should().NotBeNullOrEmpty();

        result.Stuck.Should().Be(1);
        _logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning
            && e.Id.Name == "billing.change_stuck"
            && e.Message.Contains(org.ToString()));
    }

    [Fact]
    public async Task Sweep_ChangeWatch_ProviderDown_RetriedNextSweep()
    {
        var options = new BillingReconcileOptions();
        using ServiceProvider sp = Services(options);
        Guid org = await SeedOrg("sub_a");
        await RecordChange(sp, org, "change_plan", TU);
        _provider.Failures["sub_a"] = new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable);

        DateTime first = TU.AddMinutes(15);
        (await Sweep(sp, first)).Candidates.Should().Be(1);
        (await Watch(org))!.LastOutcome.Should().Be(nameof(ReconcileOutcome.ProviderUnavailable));

        // Paddle is back by the next sweep: the change is read again then, not after RecheckAfter.
        _provider.Failures.Clear();
        LiveState(org, "sub_a", TU, price: "pri_pro");
        BillingSweepResult second = await Sweep(sp, first + options.SweepInterval);

        second.Candidates.Should().Be(1);
        _provider.SnapshotCalls.Should().HaveCount(2);
        (await PlanKey("sub_a")).Should().Be("pro");
        BillingReconcileWatch watch = (await Watch(org))!;
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.Healed));
        watch.ChangeRequestedAt.Should().BeNull();
        watch.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public async Task Sweep_ChangeWatch_TooRecent_RetriedNextSweep()
    {
        var options = new BillingReconcileOptions();
        using ServiceProvider sp = Services(options);
        Guid org = await SeedOrg("sub_a");
        await RecordChange(sp, org, "change_plan", TU);
        // Paddle updated the subscription again 8 minutes after the change: at the first sweep that is too recent.
        LiveState(org, "sub_a", TU.AddMinutes(8), price: "pri_pro");

        DateTime first = TU.AddMinutes(15);
        (await Sweep(sp, first)).Candidates.Should().Be(1);
        (await Watch(org))!.LastOutcome.Should().Be(nameof(ReconcileOutcome.TooRecent));
        (await PlanKey("sub_a")).Should().Be("creator");

        BillingSweepResult second = await Sweep(sp, first + options.SweepInterval);

        second.Candidates.Should().Be(1);
        _provider.SnapshotCalls.Should().HaveCount(2);
        (await PlanKey("sub_a")).Should().Be("pro");
        (await Watch(org))!.LastOutcome.Should().Be(nameof(ReconcileOutcome.Healed));
    }

    [Fact]
    public async Task Sweep_CheckoutWatch_NotLive_RecheckedAfter30Min()
    {
        var options = new BillingReconcileOptions();
        using ServiceProvider sp = Services(options);
        Guid org = Guid.NewGuid();
        await using (InferenceApiDbContext db = NewDb())
        {
            db.Organizations.Add(new Organization { Id = org, Name = "o-checkout", Slug = "o-checkout", Kind = OrganizationKind.Team });
            db.BillingCustomers.Add(new BillingCustomer { Id = Guid.NewGuid(), OrganizationId = org, Provider = P, ProviderCustomerId = "ctm_checkout" });
            await db.SaveChangesAsync();
        }

        await RecordChange(sp, org, BillingReconciler.CheckoutKind, TU);

        // The checkout was started but its subscription is not live yet: still watched.
        DateTime first = TU.AddMinutes(15);
        (await Sweep(sp, first)).Candidates.Should().Be(1);
        _provider.FindLiveCalls.Should().HaveCount(1);
        BillingReconcileWatch watch = (await Watch(org))!;
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.NothingToReconcile));
        watch.ChangeRequestedAt.Should().Be(TU, "a young checkout stays watched");

        // Not re-read every sweep ...
        (await Sweep(sp, first.AddMinutes(5))).Candidates.Should().Be(0);
        _provider.FindLiveCalls.Should().HaveCount(1);

        // ... but once CheckoutRecheck (30 min) has passed, not after RecheckAfter.
        BillingSweepResult later = await Sweep(sp, first + options.CheckoutRecheck);

        later.Candidates.Should().Be(1);
        _provider.FindLiveCalls.Should().HaveCount(2);
    }

    /// <summary>An organization with a Paddle customer and no subscription, that started a checkout at <paramref name="at"/>.</summary>
    private async Task<Guid> SeedCheckout(ServiceProvider sp, string customerId, DateTime at)
    {
        Guid org = Guid.NewGuid();
        await using (InferenceApiDbContext db = NewDb())
        {
            db.Organizations.Add(new Organization { Id = org, Name = "o-" + customerId, Slug = "o-" + customerId, Kind = OrganizationKind.Team });
            db.BillingCustomers.Add(new BillingCustomer { Id = Guid.NewGuid(), OrganizationId = org, Provider = P, ProviderCustomerId = customerId });
            await db.SaveChangesAsync();
        }

        await RecordChange(sp, org, BillingReconciler.CheckoutKind, at);
        return org;
    }

    [Fact]
    public async Task Sweep_AbandonedCheckout_NotStuck_NoChangeStuckLog()
    {
        var options = new BillingReconcileOptions();
        using ServiceProvider sp = Services(options);
        Guid org = await SeedCheckout(sp, "ctm_abandoned", TU);

        // Past the stuck threshold, and Paddle has no live subscription: the member walked away.
        BillingSweepResult result = await Sweep(sp, TU.AddMinutes(15));

        result.Candidates.Should().Be(1);
        result.Stuck.Should().Be(0, "an abandoned checkout is not a lost webhook");
        BillingReconcileWatch watch = (await Watch(org))!;
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.NothingToReconcile));
        watch.ChangeRequestedAt.Should().Be(TU, "it is still rechecked until CheckoutWatchHours");
        _logs.Entries.Should().NotContain(e => e.Id == BillingWebhookDiagnostics.ChangeStuck);
        _logs.Entries.Should().Contain(e => e.Message.Contains("0 change(s) still waiting"));

        // The quiet sweeps in between count it nowhere either.
        (await Sweep(sp, TU.AddMinutes(20))).Should().Be(new BillingSweepResult(0, 0, 0, 0));
        _logs.Entries.Should().NotContain(e => e.Id == BillingWebhookDiagnostics.ChangeStuck);

        // ... and the CheckoutRecheck cadence still applies.
        (await Sweep(sp, TU.AddMinutes(15) + options.CheckoutRecheck)).Candidates.Should().Be(1);
        _provider.FindLiveCalls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Sweep_CheckoutProviderDown_StillStuck()
    {
        using ServiceProvider sp = Services();
        Guid org = await SeedCheckout(sp, "ctm_down", TU);
        _provider.Failures["ctm_down"] = new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable);

        BillingSweepResult result = await Sweep(sp, TU.AddMinutes(15));

        result.Stuck.Should().Be(1, "a checkout Paddle could not be asked about may well be paid");
        (await Watch(org))!.LastOutcome.Should().Be(nameof(ReconcileOutcome.ProviderUnavailable));
        _logs.Entries.Should().Contain(e => e.Id == BillingWebhookDiagnostics.ChangeStuck && e.Message.Contains("checkout"));
    }

    [Fact]
    public async Task Sweep_CheckoutPaidLater_StillHealed()
    {
        var options = new BillingReconcileOptions();
        using ServiceProvider sp = Services(options);
        Guid org = await SeedCheckout(sp, "ctm_sub_late", TU);

        // First recheck: nothing live yet, so nothing to alert on.
        DateTime first = TU.AddMinutes(15);
        (await Sweep(sp, first)).Stuck.Should().Be(0);
        (await Watch(org))!.LastOutcome.Should().Be(nameof(ReconcileOutcome.NothingToReconcile));

        // The member pays a little later; the subscription.created webhook is lost.
        _provider.LiveSubscriptions["ctm_sub_late"] = "sub_late";
        LiveState(org, "sub_late", TU.AddMinutes(20));

        BillingSweepResult later = await Sweep(sp, first + options.CheckoutRecheck);

        later.Candidates.Should().Be(1);
        later.Stuck.Should().Be(0);
        BillingReconcileWatch watch = (await Watch(org))!;
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.Healed));
        watch.ChangeRequestedAt.Should().BeNull();
        (await PlanKey("sub_late")).Should().Be("creator");
    }

    [Fact]
    public async Task Sweep_OverdueFailed_RetriedNextSweep()
    {
        var options = new BillingReconcileOptions();
        using ServiceProvider sp = Services(options);
        Guid org = await SeedOrg("sub_a");
        // The period ended an hour ago, no renewal webhook came, and Paddle is down at the first look.
        DateTime now = PE.AddHours(1);
        _provider.Failures["sub_a"] = new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable);

        (await Sweep(sp, now)).Candidates.Should().Be(1);
        BillingReconcileWatch watch = (await Watch(org))!;
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.ProviderUnavailable));
        watch.ChangeRequestedAt.Should().NotBeNull("a failed overdue check stays due");

        _provider.Failures.Clear();
        LiveState(org, "sub_a", PE.AddMinutes(1), status: SubscriptionStatus.PastDue);
        BillingSweepResult second = await Sweep(sp, now + options.SweepInterval);

        second.Candidates.Should().Be(1);
        _provider.SnapshotCalls.Should().HaveCount(2);
        (await Row("sub_a")).Status.Should().Be(SubscriptionStatus.PastDue);
        (await Watch(org))!.ChangeRequestedAt.Should().BeNull();
    }

    [Fact]
    public async Task Sweep_OverdueFailed_OnAnEarlierChangesWatch_RetriedNextSweep()
    {
        var options = new BillingReconcileOptions();
        using ServiceProvider sp = Services(options);
        Guid org = await SeedOrg("sub_a");
        // An earlier plan change, settled: the org keeps a watch row of that kind with nothing due.
        await RecordChange(sp, org, "change_plan", TU);
        LiveState(org, "sub_a", PS);
        (await Sweep(sp, TU.AddMinutes(15))).Candidates.Should().Be(1);
        (await Watch(org))!.ChangeRequestedAt.Should().BeNull();

        // Its renewal is overdue and Paddle is down at the first look.
        DateTime now = PE.AddHours(1);
        _provider.Failures["sub_a"] = new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable);
        (await Sweep(sp, now)).Candidates.Should().Be(1);
        BillingReconcileWatch watch = (await Watch(org))!;
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.ProviderUnavailable));
        watch.ChangeKind.Should().Be(BillingReconciler.OverdueKind, "what is due now is the overdue renewal");

        _provider.Failures.Clear();
        LiveState(org, "sub_a", PE.AddMinutes(1), status: SubscriptionStatus.PastDue);
        BillingSweepResult second = await Sweep(sp, now + options.SweepInterval);

        second.Candidates.Should().Be(1);
        (await Row("sub_a")).Status.Should().Be(SubscriptionStatus.PastDue);
    }

    [Fact]
    public async Task Sweep_BusyOrg_Skipped()
    {
        using ServiceProvider sp = Services();
        Guid busy = await SeedOrg("sub_a");
        Guid free = await SeedOrg("sub_b");
        await RecordChange(sp, busy, "change_plan", TU);
        await RecordChange(sp, free, "change_plan", TU);
        LiveState(busy, "sub_a", TU, price: "pri_pro");
        LiveState(free, "sub_b", TU, price: "pri_pro");

        // Another replica (or a member's request) holds the busy org's change claim.
        await using (IAsyncDisposable? claim = await _lock.TryAcquireAsync(busy, CancellationToken.None))
        {
            claim.Should().NotBeNull();
            await Sweep(sp, TU.AddMinutes(15));
        }

        _provider.SnapshotCalls.Should().Equal("sub_b");
        (await PlanKey("sub_a")).Should().Be("creator", "a Busy org is neither read nor written");
        (await Watch(busy))!.ChangeRequestedAt.Should().Be(TU, "it stays due for the next sweep");
        (await PlanKey("sub_b")).Should().Be("pro");
    }

    [Fact]
    public async Task Sweep_OneOrgThrows_OthersStillRun()
    {
        using ServiceProvider sp = Services();
        Guid broken = await SeedOrg("sub_a");
        Guid fine = await SeedOrg("sub_b");
        await RecordChange(sp, broken, "change_plan", TU);
        await RecordChange(sp, fine, "change_plan", TU.AddMinutes(1));
        // Not a malformed body (those are ProviderUnavailable): an unexpected exception the reconciler rethrows.
        _provider.Failures["sub_a"] = new NotSupportedException("boom");
        LiveState(fine, "sub_b", TU, price: "pri_pro");

        BillingSweepResult result = await Sweep(sp, TU.AddMinutes(20));

        result.Candidates.Should().Be(2);
        result.Failed.Should().Be(1);
        _provider.SnapshotCalls.Should().Equal("sub_a", "sub_b");
        (await PlanKey("sub_b")).Should().Be("pro");
        _logs.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains(broken.ToString()));
    }

    [Fact]
    public async Task Sweep_TakesOldestFirst_AtMostMaxPerSweep()
    {
        using ServiceProvider sp = Services(new BillingReconcileOptions { MaxPerSweep = 1 });
        Guid younger = await SeedOrg("sub_a");
        Guid older = await SeedOrg("sub_b");
        await RecordChange(sp, younger, "change_plan", TU.AddMinutes(1));
        await RecordChange(sp, older, "change_plan", TU);
        LiveState(younger, "sub_a", TU, price: "pri_pro");
        LiveState(older, "sub_b", TU, price: "pri_pro");

        BillingSweepResult result = await Sweep(sp, TU.AddMinutes(20));

        result.Candidates.Should().Be(1);
        _provider.SnapshotCalls.Should().Equal("sub_b");
    }

    /// <summary>Sets an org's watch to a change due since <paramref name="since"/> that failed <paramref name="failures"/> times, last at <paramref name="lastChecked"/>.</summary>
    private async Task SetFailingWatch(Guid org, DateTime since, int failures, DateTime? lastChecked, string kind = "change_plan")
    {
        await using InferenceApiDbContext db = NewDb();
        BillingReconcileWatch? watch = await db.BillingReconcileWatches.SingleOrDefaultAsync(w => w.OrganizationId == org);
        if (watch is null)
        {
            watch = new BillingReconcileWatch { OrganizationId = org };
            db.BillingReconcileWatches.Add(watch);
        }

        watch.ChangeKind = kind;
        watch.ChangeRequestedAt = since;
        watch.LastCheckedAt = lastChecked;
        watch.ConsecutiveFailures = failures;
        watch.LastOutcome = failures > 0 ? nameof(ReconcileOutcome.ProviderUnavailable) : null;
        // As the reconciler leaves it (its last write was that check), not the entity's wall-clock default.
        watch.UpdatedAt = lastChecked ?? since;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sweep_FailingWatches_DoNotStarveNewOnes()
    {
        using ServiceProvider sp = Services(new BillingReconcileOptions { MaxPerSweep = 2 });
        DateTime now = TU.AddMinutes(15);
        // Three old watches that keep failing (Paddle refuses them), each due again by any backoff …
        List<Guid> failing = [];
        foreach (string subId in new[] { "sub_f1", "sub_f2", "sub_f3" })
        {
            Guid org = await SeedOrg(subId);
            await SetFailingWatch(org, TU.AddDays(-1), failures: 6, lastChecked: now.AddHours(-2));
            _provider.Failures[subId] = new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable);
            failing.Add(org);
        }

        // … and one change whose webhook was lost just now, never checked.
        Guid fresh = await SeedOrg("sub_new");
        await RecordChange(sp, fresh, "change_plan", TU);
        LiveState(fresh, "sub_new", TU, price: "pri_pro");

        BillingSweepResult result = await Sweep(sp, now);

        result.Candidates.Should().Be(2);
        _provider.SnapshotCalls.Should().HaveCount(2);
        _provider.SnapshotCalls[0].Should().Be("sub_new", "a watch with no failures goes before the failing ones");
        _provider.SnapshotCalls[1].Should().BeOneOf(["sub_f1", "sub_f2", "sub_f3"], "the remaining slot goes to a failing watch");
        (await PlanKey("sub_new")).Should().Be("pro");
    }

    [Theory]
    [InlineData(1, 4, false)]
    [InlineData(1, 5, true)]
    [InlineData(2, 9, false)]
    [InlineData(2, 10, true)]
    [InlineData(3, 19, false)]
    [InlineData(3, 20, true)]
    [InlineData(5, 59, false)]
    [InlineData(5, 60, true)]
    [InlineData(10, 59, false)]
    [InlineData(10, 60, true)]
    [InlineData(400, 60, true)]
    public async Task Sweep_FailingWatch_BacksOffExponentially_CappedAtOneHour(int failures, int minutesSinceCheck, bool picked)
    {
        // SweepInterval 5 min: retried after 5, 10, 20, 40 min, then every hour.
        using ServiceProvider sp = Services(new BillingReconcileOptions { SweepIntervalSeconds = 300 });
        Guid org = await SeedOrg("sub_a");
        DateTime now = TU.AddDays(1);
        await SetFailingWatch(org, TU, failures, now.AddMinutes(-minutesSinceCheck));
        _provider.Failures["sub_a"] = new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable);

        BillingSweepResult result = await Sweep(sp, now);

        result.Candidates.Should().Be(picked ? 1 : 0);
    }

    [Fact]
    public async Task Sweep_FailingWatch_NewChangeRecorded_IsNotBackedOff()
    {
        using ServiceProvider sp = Services();
        Guid org = await SeedOrg("sub_a");
        DateTime now = TU.AddDays(1);
        await SetFailingWatch(org, TU, failures: 8, lastChecked: now.AddMinutes(-30));
        // The member changes the plan again after that failed check: the new change is due like any other.
        await RecordChange(sp, org, "change_plan", now.AddMinutes(-20));
        _provider.Failures["sub_a"] = new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable);

        (await Sweep(sp, now)).Candidates.Should().Be(1);
    }

    [Fact]
    public async Task Sweep_CheckoutWatches_TakeAtMostAQuarterOfMaxPerSweep()
    {
        using ServiceProvider sp = Services(new BillingReconcileOptions { MaxPerSweep = 4 });
        // Three abandoned checkouts, older than the two lost plan changes.
        foreach (string customer in new[] { "ctm_c1", "ctm_c2", "ctm_c3" })
        {
            await SeedCheckout(sp, customer, TU.AddMinutes(-30));
        }

        Guid a = await SeedOrg("sub_a");
        Guid b = await SeedOrg("sub_b");
        await RecordChange(sp, a, "change_plan", TU);
        await RecordChange(sp, b, "change_plan", TU);
        LiveState(a, "sub_a", TU, price: "pri_pro");
        LiveState(b, "sub_b", TU, price: "pri_pro");

        BillingSweepResult result = await Sweep(sp, TU.AddMinutes(15));

        result.Candidates.Should().Be(3);
        _provider.FindLiveCalls.Should().ContainSingle("checkouts get at most MaxPerSweep / 4 (here 1) of a pass");
        _provider.SnapshotCalls.Should().BeEquivalentTo(["sub_a", "sub_b"]);
    }

    [Fact]
    public async Task Sweep_Disabled_NoOp()
    {
        Guid org = await SeedOrg("sub_a");
        using (ServiceProvider off = Services(new BillingReconcileOptions { Enabled = false }))
        {
            await RecordChange(off, org, "change_plan", TU);
        }

        // RecordChange is itself a no-op when disabled; open the watch directly.
        await using (InferenceApiDbContext db = NewDb())
        {
            db.BillingReconcileWatches.Add(new BillingReconcileWatch { OrganizationId = org, ChangeKind = "change_plan", ChangeRequestedAt = TU });
            await db.SaveChangesAsync();
        }

        LiveState(org, "sub_a", TU, price: "pri_pro");

        using (ServiceProvider reconcileOff = Services(new BillingReconcileOptions { Enabled = false }))
        {
            (await Sweep(reconcileOff, TU.AddMinutes(30))).Candidates.Should().Be(0);
        }

        using (ServiceProvider billingOff = Services(billingEnabled: false))
        {
            (await Sweep(billingOff, TU.AddMinutes(30))).Candidates.Should().Be(0);
        }

        _provider.SnapshotCalls.Should().BeEmpty();
        (await Watch(org))!.LastCheckedAt.Should().BeNull();
        (await PlanKey("sub_a")).Should().Be("creator");
    }

    // ------------------------------------------------------------------ T-003f

    /// <summary>Delivers a real webhook for an org's subscription (the state the row then holds).</summary>
    private async Task Deliver(Guid org, string subId, string eventId, DateTime at, string price)
    {
        await using InferenceApiDbContext db = NewDb();
        (await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, _clock)
            .ProcessAsync(Sub(org, subId, eventId, at, price), "{}")).Should().Be(BillingEventOutcome.Applied);
    }

    [Fact]
    public async Task Sweep_LostKeep_Healed()
    {
        // QA-1: Pro with a downgrade to Creator held; "keep my plan" went back to Pro at Paddle, its webhook lost.
        using ServiceProvider sp = Services();
        Guid org = await SeedOrg("sub_a");
        await Deliver(org, "sub_a", "evt_up", PS.AddDays(1), "pri_pro");
        await Deliver(org, "sub_a", "evt_down", PS.AddDays(2), "pri_creator");
        (await Row("sub_a")).ScheduledPlanId.Should().NotBeNull();
        await RecordChange(sp, org, "keep", TU);
        LiveState(org, "sub_a", TU, price: "pri_pro");

        BillingSweepResult result = await Sweep(sp, TU.AddMinutes(15));

        result.Candidates.Should().Be(1);
        Subscription row = await Row("sub_a");
        (await PlanKey("sub_a")).Should().Be("pro");
        row.ScheduledPlanId.Should().BeNull("the hold is released, so changes are no longer 409 downgrade_scheduled");
        row.ScheduledSeats.Should().BeNull();
        BillingReconcileWatch watch = (await Watch(org))!;
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.Healed));
        watch.ChangeRequestedAt.Should().BeNull();
        result.Stuck.Should().Be(0);
    }

    [Fact]
    public async Task Sweep_SignatureFailures_RaiseAlert_OncePerHour()
    {
        RecordingHandler hook = new();
        using ServiceProvider sp = Services(alertHook: hook);
        BillingWebhookFailureTotals totals = new();
        BillingReconcileSweeper sweeper = Sweeper(sp, totals);

        await sweeper.SweepOnceAsync(TU, CancellationToken.None);
        hook.Bodies.Should().BeEmpty("nothing failed");

        totals.RecordSignatureFailure();
        totals.RecordSignatureFailure();
        totals.RecordProcessingFailure();
        await sweeper.SweepOnceAsync(TU.AddMinutes(5), CancellationToken.None);

        hook.Bodies.Should().ContainSingle();
        SpendReportAlert first = hook.Alert(0);
        first.Scope.Should().Be("billing_webhooks");
        first.OrganizationId.Should().BeNull();
        first.To.Should().Be(TU.AddMinutes(5));
        first.Detail.Should().Contain("2 signature failure(s)").And.Contain("1 processing failure(s)");
        _logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Id.Id == 7108 && e.Id.Name == "billing.webhook_failures_alert");

        // More failures inside the hour: no second alert yet.
        totals.RecordSignatureFailure();
        await sweeper.SweepOnceAsync(TU.AddMinutes(35), CancellationToken.None);
        hook.Bodies.Should().HaveCount(1, "at most one alert per hour");

        // An hour after the first alert: the counts since that alert.
        await sweeper.SweepOnceAsync(TU.AddMinutes(65), CancellationToken.None);
        hook.Bodies.Should().HaveCount(2);
        SpendReportAlert second = hook.Alert(1);
        second.From.Should().Be(TU.AddMinutes(5));
        second.Detail.Should().Contain("1 signature failure(s)").And.Contain("0 processing failure(s)");

        // Nothing new since: quiet.
        await sweeper.SweepOnceAsync(TU.AddHours(5), CancellationToken.None);
        hook.Bodies.Should().HaveCount(2);
        _logs.Entries.Count(e => e.Id == BillingWebhookDiagnostics.WebhookFailuresAlert).Should().Be(2);
    }

    [Fact]
    public async Task Sweep_CheckoutTooRecent_RetriedNextSweep()
    {
        // QA-4: a checkout whose subscription Paddle shows but too recently is a change still due, not an abandoned one.
        var options = new BillingReconcileOptions();
        using ServiceProvider sp = Services(options);
        Guid org = await SeedCheckout(sp, "ctm_sub_paid", TU);
        _provider.LiveSubscriptions["ctm_sub_paid"] = "sub_paid";
        LiveState(org, "sub_paid", TU.AddMinutes(8));

        DateTime first = TU.AddMinutes(15);
        (await Sweep(sp, first)).Candidates.Should().Be(1);
        (await Watch(org))!.LastOutcome.Should().Be(nameof(ReconcileOutcome.TooRecent));

        BillingSweepResult second = await Sweep(sp, first + options.SweepInterval);

        second.Candidates.Should().Be(1, "retried on the change-watch cadence, not after CheckoutRecheck");
        BillingReconcileWatch watch = (await Watch(org))!;
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.Healed));
        watch.ChangeRequestedAt.Should().BeNull();
        (await PlanKey("sub_paid")).Should().Be("creator");
    }

    [Fact]
    public async Task Sweep_ManyAbandonedCheckouts_DoNotStarveAChangeWatch()
    {
        // Security S-2: more abandoned checkouts than the candidate window (4 * MaxPerSweep), all older than the change.
        using ServiceProvider sp = Services(new BillingReconcileOptions { MaxPerSweep = 2 });
        DateTime now = TU.AddMinutes(15);
        for (int i = 0; i < 10; i++)
        {
            Guid checkout = await SeedCheckout(sp, $"ctm_abandoned_{i}", TU.AddHours(-3));
            await using InferenceApiDbContext db = NewDb();
            BillingReconcileWatch w = await db.BillingReconcileWatches.SingleAsync(x => x.OrganizationId == checkout);
            w.LastCheckedAt = now.AddHours(-1);
            w.LastOutcome = nameof(ReconcileOutcome.NothingToReconcile);
            await db.SaveChangesAsync();
        }

        Guid org = await SeedOrg("sub_a");
        await RecordChange(sp, org, "change_plan", TU);
        LiveState(org, "sub_a", TU, price: "pri_pro");

        BillingSweepResult result = await Sweep(sp, now);

        _provider.SnapshotCalls.Should().Equal("sub_a");
        (await PlanKey("sub_a")).Should().Be("pro");
        _provider.FindLiveCalls.Should().ContainSingle("checkouts still take at most a quarter of the pass (at least one)");
        result.Candidates.Should().Be(2);
    }

    [Fact]
    public async Task Sweep_CheckoutWithoutCustomer_StillStuck()
    {
        // Security low: no billing_customers row means the platform cannot ask Paddle, not that the checkout was abandoned.
        using ServiceProvider sp = Services();
        Guid org = Guid.NewGuid();
        await using (InferenceApiDbContext db = NewDb())
        {
            db.Organizations.Add(new Organization { Id = org, Name = "o-nocust", Slug = "o-nocust", Kind = OrganizationKind.Team });
            await db.SaveChangesAsync();
        }

        await RecordChange(sp, org, BillingReconciler.CheckoutKind, TU);

        BillingSweepResult result = await Sweep(sp, TU.AddMinutes(15));

        result.Candidates.Should().Be(1);
        result.Stuck.Should().Be(1);
        BillingReconcileWatch watch = (await Watch(org))!;
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.NothingToReconcile));
        watch.LastError.Should().Be("no_customer");
        _logs.Entries.Should().Contain(e => e.Id == BillingWebhookDiagnostics.ChangeStuck && e.Message.Contains(org.ToString()));
    }

    // ------------------------------------------------------------------ Promo discounts: the sweep leaves promo discounts alone

    /// <summary>A recurring promotion (Paddle discount <paramref name="discountId"/>) on <paramref name="targetPriceIdsJson"/> that <paramref name="subId"/> was bought with.</summary>
    private async Task<Guid> SeedPromoRedemption(Guid org, string subId, string discountId, string targetPriceIdsJson, DateTime? removedAt = null, string? removedForPriceId = null)
    {
        Guid promotion = Guid.NewGuid();
        await using InferenceApiDbContext db = NewDb();
        db.Promotions.Add(new Promotion
        {
            Id = promotion,
            Kind = PromotionKind.PlanDiscount,
            Name = "Autumn",
            Code = discountId.ToUpperInvariant(),
            DiscountType = PromotionDiscountType.Percentage,
            DiscountAmount = "20",
            DurationKind = PromotionDuration.Forever,
            TargetPriceIdsJson = targetPriceIdsJson,
            ProviderDiscountId = discountId,
            RedemptionCount = 1
        });
        db.PromotionRedemptions.Add(new PromotionRedemption
        {
            Id = Guid.NewGuid(),
            PromotionId = promotion,
            OrganizationId = org,
            Kind = PromotionRedemptionKind.DiscountApplied,
            ProviderTransactionId = "txn_first",
            ProviderSubscriptionId = subId,
            DiscountTotal = "580",
            CurrencyCode = "USD",
            CreatedAt = PS,
            DiscountRemovedAt = removedAt,
            DiscountRemovedForPriceId = removedForPriceId
        });
        await db.SaveChangesAsync();
        return promotion;
    }

    private async Task<string> PromoState()
    {
        await using InferenceApiDbContext db = NewDb();
        List<Promotion> promotions = await db.Promotions.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
        List<PromotionRedemption> rows = await db.PromotionRedemptions.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        return string.Join(";", promotions.Select(p => $"{p.Id}|{p.RedemptionCount}|{p.Status}|{p.ProviderDiscountId}")) + "||"
            + string.Join(";", rows.Select(r => $"{r.Id}|{r.Kind}|{r.Outcome}|{r.ProviderSubscriptionId}|{r.DiscountRemovedAt:O}|{r.DiscountRemovedForPriceId}"));
    }

    [Fact]
    public async Task Sweep_HealsADiscountedSubscription_WithoutTouchingTheDiscount()
    {
        using ServiceProvider sp = Services();
        Guid kept = await SeedOrg("sub_a");
        Guid removed = await SeedOrg("sub_b");
        await SeedPromoRedemption(kept, "sub_a", "dsc_1", """["pri_creator","pri_pro"]""");
        DateTime removedAt = TU.AddSeconds(-3);
        await SeedPromoRedemption(removed, "sub_b", "dsc_2", """["pri_creator"]""", removedAt, "pri_pro");
        await RecordChange(sp, kept, "change_plan", TU);
        await RecordChange(sp, removed, "change_plan", TU);
        string before = await PromoState();
        // Both moved Creator → Pro at TU and both webhooks were lost. Pro is a target of sub_a's promotion, so
        // Paddle kept dsc_1; it is not one of sub_b's, so that PATCH removed dsc_2 and the controller recorded it.
        LiveState(kept, "sub_a", TU, price: "pri_pro");
        _provider.Snapshots["sub_a"] = _provider.Snapshots["sub_a"] with
        {
            Event = _provider.Snapshots["sub_a"].Event with { DiscountId = "dsc_1", DiscountTotal = "1580", CurrencyCode = "USD" },
            RawJson = "{\"event_type\":\"subscription.updated\",\"data\":{\"discount\":{\"id\":\"dsc_1\",\"ends_at\":null}}}"
        };
        LiveState(removed, "sub_b", TU, price: "pri_pro");

        BillingSweepResult result = await Sweep(sp, TU.AddMinutes(15));

        result.Candidates.Should().Be(2);
        (await PlanKey("sub_a")).Should().Be("pro");
        (await PlanKey("sub_b")).Should().Be("pro");
        (await Watch(kept))!.LastOutcome.Should().Be(nameof(ReconcileOutcome.Healed));
        (await Watch(removed))!.LastOutcome.Should().Be(nameof(ReconcileOutcome.Healed));
        _provider.PlanChanges.Should().BeEmpty("the sweep only reads Paddle; it never PATCHes a subscription or its discount");
        (await PromoState()).Should().Be(before, "a heal neither records nor clears a discount removal, nor adds a redemption");
    }

    // ------------------------------------------------------------------ B12: the periodic drift report

    /// <summary>A <c>usage_events</c> row that ran outside any usage scope: on the platform org, source Unattributed.</summary>
    private async Task SeedUnattributedUsage(DateTime at)
    {
        await using InferenceApiDbContext db = NewDb();
        if (!await db.Organizations.AnyAsync(o => o.Id == WellKnownOrganizations.PlatformId))
        {
            db.Organizations.Add(new Organization { Id = WellKnownOrganizations.PlatformId, Name = "Platform", Slug = "platform", Kind = OrganizationKind.Team });
        }

        db.UsageEvents.Add(new UsageEvent
        {
            Id = Guid.NewGuid(),
            OrganizationId = WellKnownOrganizations.PlatformId,
            Source = UsageSource.Unattributed,
            Kind = UsageKind.LlmInputTokens,
            Quantity = 1m,
            ProviderKind = InferenceProviderKind.Anthropic,
            ModelName = "claude-x",
            CreditsMillis = 100,
            CostUsd = 0.01m,
            IdempotencyKey = Guid.NewGuid().ToString(),
            OccurredAt = at
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sweep_A_ledger_subscription_the_provider_lacks_is_named_drift_and_alerted()
    {
        RecordingHandler hook = new();
        using ServiceProvider sp = Services(alertHook: hook);
        Guid org = await SeedOrg("sub_a");
        await RecordChange(sp, org, "change_plan", TU);
        // The ledger holds sub_a; Paddle answers 404 for it. The reverse drift, named, not an anonymous failure.
        _provider.Failures["sub_a"] = new BillingProviderException("subscription_not_found", 404, "no such subscription");

        BillingSweepResult result = await Sweep(sp, TU.AddMinutes(15));

        result.Candidates.Should().Be(1);
        _logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning
            && e.Id.Name == "billing.drift_detected"
            && e.Message.Contains("ledger_only"));
        hook.Bodies.Should().ContainSingle("a drift finding is surfaced where an operator will see it");
        SpendReportAlert alert = hook.Alert(0);
        alert.Scope.Should().Be(BillingReconcileSweeper.BillingDriftScope);
        alert.Detail.Should().Contain("ledger_only");
    }

    [Fact]
    public async Task Sweep_Unattributed_usage_is_counted_and_reported()
    {
        RecordingHandler hook = new();
        using ServiceProvider sp = Services(alertHook: hook);
        await SeedUnattributedUsage(TU);

        // No reconcile candidate at all: the report still counts the ledger's unattributed rows.
        BillingSweepResult result = await Sweep(sp, TU.AddMinutes(15));

        result.Candidates.Should().Be(0);
        _logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning
            && e.Id.Name == "billing.usage_unattributed"
            && e.Message.Contains("1"));
        hook.Bodies.Should().ContainSingle();
        hook.Alert(0).Detail.Should().Contain("no attributable organization");
    }

    [Fact]
    public async Task Sweep_Healed_and_clean_raises_no_drift_alert()
    {
        RecordingHandler hook = new();
        using ServiceProvider sp = Services(alertHook: hook);
        Guid org = await SeedOrg("sub_a");
        await RecordChange(sp, org, "change_plan", TU);
        LiveState(org, "sub_a", TU, price: "pri_pro");

        BillingSweepResult result = await Sweep(sp, TU.AddMinutes(15));

        result.Candidates.Should().Be(1);
        _logs.Entries.Should().NotContain(e => e.Id.Name == "billing.drift_detected");
        _logs.Entries.Should().NotContain(e => e.Id.Name == "billing.usage_unattributed");
        hook.Bodies.Should().BeEmpty("a healed sweep with no unattributed rows is not drift");
    }

    [Fact]
    public async Task Sweep_Drift_alert_is_at_most_once_an_hour()
    {
        RecordingHandler hook = new();
        using ServiceProvider sp = Services(alertHook: hook);
        Guid org = await SeedOrg("sub_a");
        await RecordChange(sp, org, "change_plan", TU);
        _provider.Failures["sub_a"] = new BillingProviderException("subscription_not_found", 404, "no such subscription");

        await Sweep(sp, TU.AddMinutes(15));
        hook.Bodies.Should().ContainSingle();

        // The change is still due, so the next sweep reconciles it again: the alert is throttled.
        await Sweep(sp, TU.AddMinutes(16));
        hook.Bodies.Should().HaveCount(1, "at most one drift alert per hour");

        // Past the hour: the counts since the previous alert go out.
        await Sweep(sp, TU.AddHours(2));
        hook.Bodies.Should().HaveCount(2);
    }
}
