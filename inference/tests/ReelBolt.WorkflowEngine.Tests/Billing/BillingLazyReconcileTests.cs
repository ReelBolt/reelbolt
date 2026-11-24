#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Reconcile;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The controller's use of the reconciler: a change that would be <c>409 change_pending</c> because a webhook
/// was lost heals lazily (under the claim it already holds) and re-runs once; every accepted change records a
/// watch; the webhook receiver counts and logs signature failures, processing failures and outcomes.
/// The controller, processor and reconciler share one DbContext, as in a request scope.
/// </summary>
public sealed class BillingLazyReconcileTests : IDisposable
{
    private const string P = "fake";
    private const string SubId = "sub_1";

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

    public BillingLazyReconcileTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.ApplicationUsers.Add(new ApplicationUser { Id = _user, Email = "u@example.test", DisplayName = "u", EmailVerifiedAt = DateTime.UtcNow });
        db.Organizations.Add(new Organization { Id = _org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.Plans.AddRange(
            new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, PriceMonthlyUsd = 29, MonthlyCreditsMillis = 1_000_000, TopUpAllowed = true, SortOrder = 1, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_creator","annual":"pri_creator_y"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "pro", Name = "Pro", Kind = PlanKind.Subscription, PriceMonthlyUsd = 79, MonthlyCreditsMillis = 3_500_000, TopUpAllowed = true, SortOrder = 2, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_pro","annual":"pri_pro_y"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "team", Name = "Team", Kind = PlanKind.PerSeat, MinSeats = 3, PriceMonthlyUsd = 49, MonthlyCreditsMillis = 1_500_000, TopUpAllowed = true, SortOrder = 3, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_team","annual":"pri_team_y"}}""" });
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

    private sealed class FakeUser(Guid userId, Guid org) : ICurrentUser
    {
        public Guid UserId => userId;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => org;
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool IsPlatformAdmin => false;
    }

    private sealed class FakeTenant : ITenantContext
    {
        public Guid? OrganizationId => null;
        public bool IsSystem => true;
        public IDisposable BeginSystemScope() => new Nop();
        public IDisposable BeginOrganizationScope(Guid organizationId) => new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }
    }

    /// <summary>Paddle as the test sets it: <see cref="Live"/> is what <c>GET /subscriptions</c> answers, <see cref="Snapshot"/> what the reconciler reads.</summary>
    private sealed class FakeProvider(string name = P) : IBillingProvider
    {
        public string Name => name;
        public bool IsConfigured => true;
        public IReadOnlyList<int> TopUpPacks => [500];
        public string? TopUpPriceId(int credits) => credits == 500 ? "pri_topup_500" : null;

        public ProviderSubscriptionState? Live { get; set; }
        public Exception? GetFail { get; set; }
        public BillingSnapshot? Snapshot { get; set; }
        public string? LiveSubscriptionId { get; set; }
        public bool HasLive { get; set; }
        public List<string> SnapshotCalls { get; } = [];
        public List<(string Sub, IReadOnlyList<BillingLineItem> Items, PlanChangeTiming Timing, PlanChangeDiscount Discount)> PlanChanges { get; } = [];
        public List<string> Cancels { get; } = [];
        public List<string> Resumes { get; } = [];
        public int Checkouts { get; private set; }

        /// <summary>Runs once Paddle has accepted a plan change (e.g. the client aborts right after the PATCH).</summary>
        public Action? AfterChangePlan { get; set; }

        public Task<ProviderSubscriptionState> GetSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
        {
            if (GetFail is not null) throw GetFail;
            return Task.FromResult(Live ?? throw new InvalidOperationException("No live state set."));
        }

        public Task<BillingSnapshot> SnapshotSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
        {
            SnapshotCalls.Add(providerSubscriptionId);
            if (GetFail is not null) throw GetFail;
            return Task.FromResult(Snapshot ?? throw new InvalidOperationException("No snapshot set."));
        }

        public Task<IReadOnlyList<BillingSnapshot>> CompletedUpgradeTransactionsAsync(string providerSubscriptionId, DateTime since, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<BillingSnapshot>>([]);

        public Task<string?> FindLiveSubscriptionIdAsync(string providerCustomerId, Guid organizationId, CancellationToken ct)
            => Task.FromResult(LiveSubscriptionId);

        public Task<bool> HasLiveSubscriptionAsync(string providerCustomerId, Guid organizationId, CancellationToken ct)
            => Task.FromResult(HasLive);

        public Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct)
        {
            Checkouts++;
            return Task.FromResult(new CheckoutSession("https://pay.example/c/1", "txn_1", request.ExistingCustomerId));
        }

        public Task<PortalSession> CreatePortalSessionAsync(string providerCustomerId, string? providerSubscriptionId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<string> CreateDiscountAsync(ProviderDiscountSpec spec, CancellationToken ct) => throw new NotSupportedException();

        public Task UpdateDiscountAsync(string discountId, bool? active, DateTime? expiresAt, bool clearExpiry, CancellationToken ct) => throw new NotSupportedException();

        public Task<PricePreview> PreviewPriceAsync(IReadOnlyList<BillingLineItem> items, string? discountId, CancellationToken ct) => throw new NotSupportedException();

        public Task<ProviderTransactionCancel> CancelTransactionAsync(string providerTransactionId, CancellationToken ct) => throw new NotSupportedException();

        public Task CancelSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
        {
            Cancels.Add(providerSubscriptionId);
            return Task.CompletedTask;
        }

        public Task ResumeSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
        {
            Resumes.Add(providerSubscriptionId);
            return Task.CompletedTask;
        }

        public Task ChangePlanAsync(string providerSubscriptionId, IReadOnlyList<BillingLineItem> items, PlanChangeTiming timing, PlanChangeDiscount discount, CancellationToken ct)
        {
            PlanChanges.Add((providerSubscriptionId, items, timing, discount));
            if (Live is not null) Live = Live with { Items = items, Discount = discount == PlanChangeDiscount.Remove ? null : Live.Discount };
            AfterChangePlan?.Invoke();
            return Task.CompletedTask;
        }

        public BillingEvent VerifyAndParseWebhook(string rawBody, IReadOnlyDictionary<string, string> headers)
        {
            if (!headers.TryGetValue("X-Fake-Signature", out string? sig) || sig != "ok")
            {
                throw new WebhookVerificationException("bad signature");
            }

            return JsonSerializer.Deserialize<BillingEvent>(rawBody)!;
        }
    }

    /// <summary>Delegates to the real reconciler; can fail the watch write.</summary>
    private sealed class FlakyRecorder(IBillingReconciler inner) : IBillingReconciler
    {
        public int Records { get; private set; }

        public Task<ReconcileResult> ReconcileHeldAsync(Guid organizationId, ReconcileTrigger trigger, bool force, CancellationToken ct)
            => inner.ReconcileHeldAsync(organizationId, trigger, force, ct);

        public Task<ReconcileResult> ReconcileAsync(Guid organizationId, ReconcileTrigger trigger, bool force, CancellationToken ct)
            => inner.ReconcileAsync(organizationId, trigger, force, ct);

        public Task RecordChangeRequestedAsync(Guid organizationId, string changeKind, Guid? userId, CancellationToken ct)
        {
            Records++;
            throw new InvalidOperationException("watch table unavailable");
        }
    }

    private sealed class CapturingLogger : ILogger<BillingController>
    {
        public List<(LogLevel Level, EventId Id)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId));
    }

    private BillingReconciler Reconciler(InferenceApiDbContext db)
        => new(
            db,
            [_provider],
            new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, _clock),
            _lock,
            Options.Create(new BillingOptions { Enabled = true }),
            Options.Create(new BillingReconcileOptions()),
            NullLogger<BillingReconciler>.Instance,
            tenant: null,
            time: _clock);

    private BillingController Controller(
        InferenceApiDbContext? db = null,
        IBillingProvider? provider = null,
        Func<InferenceApiDbContext, IBillingReconciler>? reconciler = null,
        BillingEventProcessor? processor = null,
        ILogger<BillingController>? logger = null,
        string body = "",
        string? signature = null,
        IBillingChangeLock? changeLock = null)
    {
        db ??= NewDb();
        BillingController c = new(
            db,
            new FakeUser(_user, _org),
            Options.Create(new BillingOptions { Enabled = true }),
            [provider ?? _provider],
            processor ?? new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, _clock),
            new FakeTenant(),
            Options.Create(new PaddleOptions()),
            logger ?? NullLogger<BillingController>.Instance,
            time: _clock,
            changeLock: changeLock ?? _lock,
            reconciler: (reconciler ?? Reconciler)(db));
        DefaultHttpContext http = new();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (signature is not null) http.Request.Headers["X-Fake-Signature"] = signature;
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return c;
    }

    private BillingEvent SubEvent(string eventId, DateTime at, string price, int qty, bool cancel = false) => new()
    {
        Provider = P,
        EventId = eventId,
        EventType = "subscription.updated",
        OccurredAt = at,
        Kind = BillingEventKind.SubscriptionChanged,
        OrganizationId = _org,
        CustomerId = "ctm_1",
        SubscriptionId = SubId,
        Status = SubscriptionStatus.Active,
        PeriodStart = PS,
        PeriodEnd = PE,
        CancelAtPeriodEnd = cancel,
        CancelEffectiveAt = cancel ? PE : null,
        Items = [new BillingLineItem(price, qty)]
    };

    /// <summary>The row as the last delivered webhook left it (at the period start).</summary>
    private async Task Subscribe(string price, int qty, bool cancel = false)
    {
        await using InferenceApiDbContext db = NewDb();
        (await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, _clock)
            .ProcessAsync(SubEvent("evt_created", PS, price, qty, cancel), "{}")).Should().Be(BillingEventOutcome.Applied);
    }

    /// <summary>Paddle's live state (both the change endpoints' read and the reconciler's snapshot), changed at <paramref name="updatedAt"/>.</summary>
    private void PaddleNow(string price, int qty, DateTime updatedAt, bool cancel = false)
    {
        _provider.Live = new ProviderSubscriptionState("active", [new BillingLineItem(price, qty)], cancel, PE, PS);
        _provider.Snapshot = new BillingSnapshot(
            SubEvent($"reconcile:sub:{SubId}:{updatedAt.Ticks}", updatedAt, price, qty, cancel) with { EventType = "subscription.reconciled" },
            "{\"event_type\":\"subscription.updated\"}",
            updatedAt);
    }

    private async Task<Subscription> Row()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.Subscriptions.AsNoTracking().SingleAsync(s => s.OrganizationId == _org && s.Status != SubscriptionStatus.Canceled);
    }

    private async Task<BillingReconcileWatch?> Watch()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.BillingReconcileWatches.AsNoTracking().SingleOrDefaultAsync(w => w.OrganizationId == _org);
    }

    private async Task<int> SyntheticEventRows()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.BillingWebhookEvents.AsNoTracking().CountAsync(e => e.EventId.StartsWith("reconcile:"));
    }

    private static JsonElement Json(IActionResult result)
        => JsonSerializer.SerializeToElement(((ObjectResult)result).Value);

    private static void ShouldBe(IActionResult result, int status, string error)
    {
        ((ObjectResult)result).StatusCode.Should().Be(status);
        Json(result).GetProperty("error").GetString().Should().Be(error);
    }

    private const string ChangePendingMessage = "Your last change is still being confirmed. Refresh this page in a minute, then try again.";

    private static void ShouldBeChangePending(IActionResult result)
    {
        ShouldBe(result, 409, "change_pending");
        Json(result).GetProperty("message").GetString().Should().Be(ChangePendingMessage);
    }

    private sealed class CounterRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, long Value, Dictionary<string, object?> Tags)> _measurements = [];

        public CounterRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == BillingWebhookDiagnostics.MeterName) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                Dictionary<string, object?> map = [];
                foreach (KeyValuePair<string, object?> tag in tags) map[tag.Key] = tag.Value;
                lock (_measurements) _measurements.Add((instrument.Name, value, map));
            });
            _listener.Start();
        }

        public long Sum(string instrument, string provider, string? tag = null, string? tagValue = null)
        {
            lock (_measurements)
            {
                return _measurements
                    .Where(m => m.Instrument == instrument && Equals(m.Tags.GetValueOrDefault("provider"), provider))
                    .Where(m => tag is null || Equals(m.Tags.GetValueOrDefault(tag), tagValue))
                    .Sum(m => m.Value);
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    // ------------------------------------------------------------------ lazy heal: seats

    [Fact]
    public async Task SeatChange_AfterLostWebhook_HealsLazily_And202()
    {
        await Subscribe("pri_team", 3);
        PaddleNow("pri_team", 5, TU); // Paddle took 3 → 5 at TU; that webhook never came.

        IActionResult result = await Controller().ChangeSeats(new ChangeSeatsRequestDto(7), default);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        Json(result).GetProperty("direction").GetString().Should().Be("upgrade");
        _provider.SnapshotCalls.Should().ContainSingle();
        _provider.PlanChanges.Should().ContainSingle();
        _provider.PlanChanges[0].Items.Should().BeEquivalentTo([new BillingLineItem("pri_team", 7)]);
        _provider.PlanChanges[0].Timing.Should().Be(PlanChangeTiming.Immediately);
        (await Row()).Seats.Should().Be(5, "the lost webhook's state was applied through the processor");
        BillingReconcileWatch? watch = await Watch();
        watch.Should().NotBeNull();
        watch!.ChangeKind.Should().Be("seats");
        watch.ChangeRequestedAt.Should().Be(_clock.Now);
        watch.ChangeRequestedBy.Should().Be(_user);
    }

    [Fact]
    public async Task SeatChange_RecentDrift_Still409()
    {
        await Subscribe("pri_team", 3);
        PaddleNow("pri_team", 5, TU);
        _clock.Now = TU.AddMinutes(1); // the webhook may still be on its way

        IActionResult result = await Controller().ChangeSeats(new ChangeSeatsRequestDto(7), default);

        ShouldBeChangePending(result);
        _provider.SnapshotCalls.Should().ContainSingle();
        _provider.PlanChanges.Should().BeEmpty();
        (await SyntheticEventRows()).Should().Be(0);
        (await Row()).Seats.Should().Be(3);
    }

    [Fact]
    public async Task SeatChange_PaddleDown_502_NothingSent()
    {
        await Subscribe("pri_team", 3);
        PaddleNow("pri_team", 5, TU);
        _provider.GetFail = new HttpRequestException("paddle down");

        IActionResult result = await Controller().ChangeSeats(new ChangeSeatsRequestDto(7), default);

        ShouldBe(result, 502, "billing_provider_error");
        _provider.PlanChanges.Should().BeEmpty();
        _provider.SnapshotCalls.Should().BeEmpty();
        (await Watch()).Should().BeNull();
    }

    [Fact]
    public async Task SeatChange_LockContention_409_WithoutReconcile()
    {
        await Subscribe("pri_team", 3);
        PaddleNow("pri_team", 5, TU);
        await using IAsyncDisposable? held = await _lock.TryAcquireAsync(_org, default);

        IActionResult result = await Controller().ChangeSeats(new ChangeSeatsRequestDto(7), default);

        ShouldBeChangePending(result);
        _provider.SnapshotCalls.Should().BeEmpty();
        _provider.PlanChanges.Should().BeEmpty();
    }

    /// <summary>S-4/S-23: every lock connection slot of this replica is taken.</summary>
    private sealed class AtCapacityLock : IBillingChangeLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(Guid organizationId, CancellationToken ct)
            => throw new BillingChangeLockCapacityException();
    }

    [Fact]
    public async Task Change_route_answers_503_billing_busy_with_retry_after_on_capacity()
    {
        await Subscribe("pri_team", 3);
        PaddleNow("pri_team", 3, TU);
        BillingController controller = Controller(changeLock: new AtCapacityLock());

        IActionResult result = await controller.ChangeSeats(new ChangeSeatsRequestDto(7), default);

        ShouldBe(result, 503, "billing_busy");
        Json(result).GetProperty("message").GetString().Should().NotBe(ChangePendingMessage);
        Json(result).GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        controller.HttpContext.Response.Headers.RetryAfter.ToString().Should().Be("5");
        _provider.SnapshotCalls.Should().BeEmpty();
        _provider.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_route_answers_503_billing_busy_on_capacity()
    {
        await Subscribe("pri_team", 3);
        PaddleNow("pri_team", 3, TU);
        BillingController controller = Controller(changeLock: new AtCapacityLock());

        IActionResult result = await controller.CancelSubscription(default);

        ShouldBe(result, 503, "billing_busy");
        controller.HttpContext.Response.Headers.RetryAfter.ToString().Should().Be("5");
    }

    [Fact]
    public async Task SeatChange_StillDriftingAfterHeal_409_ReconcilesOnce()
    {
        await Subscribe("pri_team", 3);
        PaddleNow("pri_team", 5, TU);
        // The live read disagrees with what the snapshot healed to: the re-run still sees drift.
        _provider.Live = _provider.Live! with { Items = [new BillingLineItem("pri_team", 6)] };

        IActionResult result = await Controller().ChangeSeats(new ChangeSeatsRequestDto(7), default);

        ShouldBeChangePending(result);
        _provider.SnapshotCalls.Should().ContainSingle("the decision re-runs once, without a second reconcile");
        _provider.PlanChanges.Should().BeEmpty();
        (await Watch())?.ChangeKind.Should().NotBe("seats");
    }

    [Fact]
    public async Task SeatChange_WatchWriteFails_Still202()
    {
        await Subscribe("pri_team", 3);
        PaddleNow("pri_team", 3, PS);
        FlakyRecorder? flaky = null;

        IActionResult result = await Controller(reconciler: db => flaky = new FlakyRecorder(Reconciler(db)))
            .ChangeSeats(new ChangeSeatsRequestDto(5), default);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        _provider.PlanChanges.Should().ContainSingle();
        flaky!.Records.Should().Be(1);
    }

    // ------------------------------------------------------------------ lazy heal: change-plan

    [Fact]
    public async Task ChangePlan_AfterLostWebhook_HealsLazily()
    {
        await Subscribe("pri_creator", 1);
        PaddleNow("pri_pro", 1, TU); // upgraded creator → pro at Paddle; webhook lost

        IActionResult result = await Controller().ChangePlan(new ChangePlanRequestDto("team", 3), default);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        Json(result).GetProperty("direction").GetString().Should().Be("upgrade");
        _provider.SnapshotCalls.Should().ContainSingle();
        _provider.PlanChanges.Should().ContainSingle();
        _provider.PlanChanges[0].Items.Should().BeEquivalentTo([new BillingLineItem("pri_team", 3)]);
        await using InferenceApiDbContext db = NewDb();
        Guid pro = (await db.Plans.SingleAsync(p => p.Key == "pro")).Id;
        (await Row()).PlanId.Should().Be(pro);
        (await Watch())!.ChangeKind.Should().Be("change_plan");
    }

    // ------------------------------------------------------------------ watches

    [Fact]
    public async Task Cancel_RecordsWatch()
    {
        await Subscribe("pri_creator", 1);
        PaddleNow("pri_creator", 1, PS);

        IActionResult result = await Controller().CancelSubscription(default);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        _provider.Cancels.Should().ContainSingle();
        BillingReconcileWatch? watch = await Watch();
        watch!.ChangeKind.Should().Be("cancel");
        watch.ChangeRequestedAt.Should().Be(_clock.Now);
        watch.ChangeRequestedBy.Should().Be(_user);
    }

    [Fact]
    public async Task Cancel_AlreadyScheduled_200_RecordsNothing()
    {
        await Subscribe("pri_creator", 1, cancel: true);
        PaddleNow("pri_creator", 1, PS, cancel: true);

        IActionResult result = await Controller().CancelSubscription(default);

        ((ObjectResult)result).StatusCode.Should().Be(200);
        (await Watch()).Should().BeNull();
    }

    [Fact]
    public async Task Resume_NothingSent_RecordsWatch()
    {
        await Subscribe("pri_creator", 1, cancel: true);
        PaddleNow("pri_creator", 1, PS, cancel: false); // resumed at Paddle; webhook not landed

        IActionResult result = await Controller().ResumeSubscription(default);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        _provider.Resumes.Should().BeEmpty();
        (await Watch())!.ChangeKind.Should().Be("resume");
    }

    /// <summary>The watch as a change accepted by Paddle at <paramref name="at"/> left it.</summary>
    private async Task RecordedAt(string kind, DateTime at)
    {
        DateTime was = _clock.Now;
        _clock.Now = at;
        await using (InferenceApiDbContext db = NewDb())
        {
            await Reconciler(db).RecordChangeRequestedAsync(_org, kind, _user, default);
        }

        _clock.Now = was;
    }

    [Fact]
    public async Task Resume_NothingSent_DoesNotPushWatchOut()
    {
        // QA-3: resumed at Paddle at TU (webhook not landed); the member clicks resume again 8 minutes later.
        await Subscribe("pri_creator", 1, cancel: true);
        await RecordedAt("resume", TU);
        PaddleNow("pri_creator", 1, TU, cancel: false);
        _clock.Now = TU.AddMinutes(8);

        IActionResult result = await Controller().ResumeSubscription(default);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        _provider.Resumes.Should().BeEmpty();
        BillingReconcileWatch watch = (await Watch())!;
        watch.ChangeRequestedAt.Should().Be(TU, "the stuck clock runs from the first request, not the repeat");
        watch.ChangeKind.Should().Be("resume");
        _provider.SnapshotCalls.Should().ContainSingle("a reconcile is attempted under the claim");
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.TooRecent));
        (await Row()).CancelAtPeriodEnd.Should().BeTrue("too recent: its webhook may still come");
    }

    [Fact]
    public async Task Resume_NothingSent_HealsWhenStuck()
    {
        await Subscribe("pri_creator", 1, cancel: true);
        PaddleNow("pri_creator", 1, TU, cancel: false); // resumed at TU; the webhook was lost

        IActionResult result = await Controller().ResumeSubscription(default);

        ((ObjectResult)result).StatusCode.Should().Be(202, "the answer does not depend on the reconcile");
        Json(result).GetProperty("status").GetString().Should().Be("resume_requested");
        (await Row()).CancelAtPeriodEnd.Should().BeFalse("the lost resume webhook was applied");
        BillingReconcileWatch watch = (await Watch())!;
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.Healed));
        watch.ChangeRequestedAt.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_AlreadyScheduledAtPaddle_RecordsWatch_AndHealsWhenStuck()
    {
        await Subscribe("pri_creator", 1);
        PaddleNow("pri_creator", 1, TU, cancel: true); // canceled at Paddle at TU; the webhook was lost

        IActionResult result = await Controller().CancelSubscription(default);

        ((ObjectResult)result).StatusCode.Should().Be(200);
        Json(result).GetProperty("status").GetString().Should().Be("cancel_scheduled");
        _provider.Cancels.Should().BeEmpty("nothing is sent twice");
        BillingReconcileWatch? watch = await Watch();
        watch.Should().NotBeNull("the cancel already at Paddle opens a watch");
        watch!.ChangeKind.Should().Be("cancel");
        watch.ChangeRequestedBy.Should().Be(_user);
        watch.LastOutcome.Should().Be(nameof(ReconcileOutcome.Healed));
        watch.ChangeRequestedAt.Should().BeNull();
        (await Row()).CancelAtPeriodEnd.Should().BeTrue("the lost cancel webhook was applied");
    }

    [Fact]
    public async Task Cancel_AlreadyScheduledAtPaddle_TooRecent_KeepsWatchDue()
    {
        await Subscribe("pri_creator", 1);
        PaddleNow("pri_creator", 1, TU, cancel: true);
        _clock.Now = TU.AddMinutes(2);

        IActionResult result = await Controller().CancelSubscription(default);

        ((ObjectResult)result).StatusCode.Should().Be(200);
        BillingReconcileWatch? watch = await Watch();
        watch.Should().NotBeNull("the cancel already at Paddle opens a watch");
        watch!.ChangeKind.Should().Be("cancel");
        watch.ChangeRequestedAt.Should().Be(_clock.Now, "the sweep comes back to it");
        (await Row()).CancelAtPeriodEnd.Should().BeFalse();
    }

    [Fact]
    public async Task ChangePlan_Keep_RecordsKeepWatch()
    {
        await Subscribe("pri_team", 5);
        // A downgrade to 3 seats is held on the row; Paddle already bills 3 from the next period.
        await using (InferenceApiDbContext db = NewDb())
        {
            Subscription sub = await db.Subscriptions.SingleAsync(s => s.OrganizationId == _org);
            sub.ScheduledPlanId = sub.PlanId;
            sub.ScheduledSeats = 3;
            await db.SaveChangesAsync();
        }

        PaddleNow("pri_team", 3, PS);

        IActionResult result = await Controller().ChangePlan(new ChangePlanRequestDto("team", 5), default);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        Json(result).GetProperty("direction").GetString().Should().Be("keep");
        (await Watch())!.ChangeKind.Should().Be("keep");
    }

    // ------------------------------------------------------------------ checkout

    [Fact]
    public async Task Checkout_SessionCreated_RecordsCheckoutWatch()
    {
        IActionResult result = await Controller().Checkout(new CheckoutRequestDto("creator", null, null), default);

        ((ObjectResult)result).StatusCode.Should().Be(200);
        (await Watch())!.ChangeKind.Should().Be(BillingReconciler.CheckoutKind);
    }

    [Fact]
    public async Task Checkout_SubscriptionPending_Healed_AnswersAlreadySubscribed()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, _clock).RememberCustomerAsync(_org, P, "ctm_1");
        }

        _provider.HasLive = true;
        _provider.LiveSubscriptionId = SubId;
        PaddleNow("pri_creator", 1, TU); // paid at TU; subscription.created never arrived

        IActionResult result = await Controller().Checkout(new CheckoutRequestDto("creator", null, null), default);

        ShouldBe(result, 409, "already_subscribed");
        Json(result).GetProperty("message").GetString().Should().Be(
            "This workspace already has a subscription. Change plan from the Plan & billing page instead of starting a second checkout.");
        _provider.Checkouts.Should().Be(0);
        (await Row()).ProviderSubscriptionId.Should().Be(SubId);
    }

    [Fact]
    public async Task Checkout_SubscriptionPending_TooRecent_StaysPending()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, _clock).RememberCustomerAsync(_org, P, "ctm_1");
        }

        _provider.HasLive = true;
        _provider.LiveSubscriptionId = SubId;
        PaddleNow("pri_creator", 1, TU);
        _clock.Now = TU.AddMinutes(1);

        IActionResult result = await Controller().Checkout(new CheckoutRequestDto("creator", null, null), default);

        ShouldBe(result, 409, "subscription_pending");
        _provider.SnapshotCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task Checkout_SubscriptionPending_ClaimBusy_StaysPending_WithoutReconcile()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, _clock).RememberCustomerAsync(_org, P, "ctm_1");
        }

        _provider.HasLive = true;
        _provider.LiveSubscriptionId = SubId;
        PaddleNow("pri_creator", 1, TU);
        await using IAsyncDisposable? held = await _lock.TryAcquireAsync(_org, default);

        IActionResult result = await Controller().Checkout(new CheckoutRequestDto("creator", null, null), default);

        ShouldBe(result, 409, "subscription_pending");
        _provider.SnapshotCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Checkout_SubscriptionPending_AtCapacity_StaysPending_WithoutReconcile()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, _clock).RememberCustomerAsync(_org, P, "ctm_1");
        }

        _provider.HasLive = true;
        _provider.LiveSubscriptionId = SubId;
        PaddleNow("pri_creator", 1, TU);

        IActionResult result = await Controller(changeLock: new AtCapacityLock()).Checkout(new CheckoutRequestDto("creator", null, null), default);

        ShouldBe(result, 409, "subscription_pending");
        _provider.SnapshotCalls.Should().BeEmpty();
        _provider.Checkouts.Should().Be(0);
    }

    // ------------------------------------------------------------------ webhook telemetry

    [Fact]
    public async Task Webhook_BadSignature_CountsMetric()
    {
        string name = "sig-" + Guid.NewGuid().ToString("N")[..8];
        FakeProvider provider = new(name);
        CapturingLogger logger = new();
        using CounterRecorder counters = new();
        long totalBefore = BillingWebhookDiagnostics.Totals.SignatureFailures;

        IActionResult result = await Controller(provider: provider, logger: logger, body: "{}", signature: "bad").Webhook(name, default);

        result.Should().BeOfType<BadRequestObjectResult>();
        Json(result).GetProperty("error").GetString().Should().Be("invalid_webhook");
        counters.Sum("reelbolt.billing.webhook.signature_failures", name).Should().Be(1);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Id.Name == "billing.webhook_signature_failed");
        BillingWebhookDiagnostics.Totals.SignatureFailures.Should().BeGreaterThan(totalBefore, "the process total the sweep alerts on (QA-2)");
    }

    [Fact]
    public async Task Webhook_ReservedReconcileEventId_Rejected()
    {
        // A correctly signed webhook may not claim the reconciler's own event ids: one that did could pre-empt
        // (as a Duplicate) the synthetic event a later reconcile feeds for the same state.
        string name = "rsv-" + Guid.NewGuid().ToString("N")[..8];
        FakeProvider provider = new(name);
        CapturingLogger logger = new();
        using CounterRecorder counters = new();
        string body = JsonSerializer.Serialize(SubEvent($"reconcile:sub:{SubId}:{TU.Ticks}", TU, "pri_pro", 1) with { Provider = name });

        IActionResult result = await Controller(provider: provider, logger: logger, body: body, signature: "ok").Webhook(name, default);

        result.Should().BeOfType<BadRequestObjectResult>();
        Json(result).GetProperty("error").GetString().Should().Be("invalid_webhook");
        counters.Sum("reelbolt.billing.webhook.signature_failures", name).Should().Be(0, "the signature was valid");
        counters.Sum("reelbolt.billing.webhook.outcomes", name, "outcome", "applied").Should().Be(0);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Id.Name == "billing.webhook_rejected");
        logger.Entries.Should().NotContain(e => e.Id.Name == "billing.webhook_signature_failed");
        await using InferenceApiDbContext db = NewDb();
        (await db.BillingWebhookEvents.CountAsync()).Should().Be(0, "it never reaches the processor");
    }

    [Fact]
    public async Task Webhook_ProcessorThrows_CountsAndReturns500()
    {
        string name = "proc-" + Guid.NewGuid().ToString("N")[..8];
        FakeProvider provider = new(name);
        CapturingLogger logger = new();
        using CounterRecorder counters = new();
        InferenceApiDbContext broken = NewDb();
        await broken.DisposeAsync(); // any processing failure: the provider must retry
        string body = JsonSerializer.Serialize(SubEvent("evt_x", TU, "pri_creator", 1) with { Provider = name });
        long totalBefore = BillingWebhookDiagnostics.Totals.ProcessingFailures;

        BillingController c = Controller(
            provider: provider,
            logger: logger,
            processor: new BillingEventProcessor(broken, NullLogger<BillingEventProcessor>.Instance, _clock),
            body: body,
            signature: "ok");
        Func<Task> act = () => c.Webhook(name, default);

        await act.Should().ThrowAsync<ObjectDisposedException>("a rethrown exception is the 500 that makes Paddle retry");
        counters.Sum("reelbolt.billing.webhook.processing_failures", name, "kind", "subscriptionchanged").Should().Be(1);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Id.Name == "billing.webhook_processing_failed");
        BillingWebhookDiagnostics.Totals.ProcessingFailures.Should().BeGreaterThan(totalBefore, "the process total the sweep alerts on (QA-2)");
    }

    [Fact]
    public async Task Webhook_Unresolved_CountsOutcomeAndWarns()
    {
        string name = "out-" + Guid.NewGuid().ToString("N")[..8];
        FakeProvider provider = new(name);
        CapturingLogger logger = new();
        using CounterRecorder counters = new();
        BillingEvent unknownOrg = SubEvent("evt_u", TU, "pri_creator", 1) with { Provider = name, OrganizationId = Guid.NewGuid() };

        IActionResult result = await Controller(provider: provider, logger: logger, body: JsonSerializer.Serialize(unknownOrg), signature: "ok").Webhook(name, default);

        Json(result).GetProperty("outcome").GetString().Should().Be("Unresolved");
        counters.Sum("reelbolt.billing.webhook.outcomes", name, "outcome", "unresolved").Should().Be(1);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Id.Name == "billing.webhook_unresolved");
    }

    // ------------------------------------------------------------------ Promo discounts: promo discounts across a heal and a change

    private const string DiscountId = "dsc_1";
    private readonly Guid _promotion = Guid.NewGuid();
    private readonly Guid _redemption = Guid.NewGuid();

    /// <summary>A recurring promotion (Paddle discount dsc_1) on <paramref name="targetPriceIdsJson"/> that sub_1 was bought with.</summary>
    private async Task SeedPromoRedemption(string targetPriceIdsJson, DateTime? removedAt = null, string? removedForPriceId = null, PromotionDuration duration = PromotionDuration.Forever)
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
            DurationKind = duration,
            DurationMonths = duration == PromotionDuration.Months ? 3 : null,
            TargetPlanKeysJson = """["creator","pro"]""",
            TargetPriceIdsJson = targetPriceIdsJson,
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

    /// <summary>Paddle's live subscription (as both the change endpoints and the reconciler read it) still carries dsc_1.</summary>
    private void PaddleKeepsDiscount()
    {
        _provider.Live = _provider.Live! with { Discount = new ProviderSubscriptionDiscount(DiscountId, null) };
        _provider.Snapshot = _provider.Snapshot! with
        {
            Event = _provider.Snapshot.Event with { DiscountId = DiscountId, DiscountTotal = "1580", CurrencyCode = "USD" },
            RawJson = "{\"event_type\":\"subscription.updated\",\"data\":{\"discount\":{\"id\":\"dsc_1\",\"ends_at\":null}}}"
        };
    }

    private async Task<PromotionRedemption> Redemption()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.PromotionRedemptions.AsNoTracking().SingleAsync(r => r.Id == _redemption);
    }

    private async Task<int> Redemptions()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.PromotionRedemptions.CountAsync();
    }

    [Fact]
    public async Task SeatChange_LazyHealWithLiveDiscount_SendsUnchanged_AndRecordsNoRemoval()
    {
        await Subscribe("pri_team", 3);
        await SeedPromoRedemption("""["pri_team"]""");
        PaddleNow("pri_team", 5, TU); // 3 → 5 at TU; that webhook was lost. The discount stays on.
        PaddleKeepsDiscount();

        IActionResult result = await Controller().ChangeSeats(new ChangeSeatsRequestDto(7), default);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        _provider.SnapshotCalls.Should().ContainSingle("the lost webhook was healed under the claim");
        (await Row()).Seats.Should().Be(5);
        _provider.PlanChanges.Should().ContainSingle("only the member's own seat change is sent; the heal sends nothing");
        _provider.PlanChanges[0].Discount.Should().Be(PlanChangeDiscount.Unchanged);
        PromotionRedemption row = await Redemption();
        row.DiscountRemovedAt.Should().BeNull();
        row.DiscountRemovedForPriceId.Should().BeNull();
        (await Redemptions()).Should().Be(1, "the heal never re-records the discount");
        (await Watch())!.ChangeKind.Should().Be("seats");
    }

    [Fact]
    public async Task ChangePlan_LazyHealAfterDiscountRemoval_KeepsTheRemovalRecord()
    {
        // Creator → Team at TU left the promotion's targets: the PATCH removed dsc_1 and the removal was recorded;
        // Paddle's webhook for it was lost, so the row is still on Creator.
        await Subscribe("pri_creator", 1);
        DateTime removedAt = TU.AddSeconds(-3);
        await SeedPromoRedemption("""["pri_creator","pri_pro"]""", removedAt, "pri_team");
        PaddleNow("pri_team", 3, TU);

        IActionResult result = await Controller().ChangePlan(new ChangePlanRequestDto("team", 5), default);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        _provider.SnapshotCalls.Should().ContainSingle("the lost webhook was healed under the claim");
        await using (InferenceApiDbContext db = NewDb())
        {
            (await Row()).PlanId.Should().Be((await db.Plans.SingleAsync(p => p.Key == "team")).Id);
        }

        _provider.PlanChanges.Should().ContainSingle();
        _provider.PlanChanges[0].Discount.Should().Be(PlanChangeDiscount.Unchanged, "Paddle carries no discount any more");
        PromotionRedemption row = await Redemption();
        row.DiscountRemovedAt.Should().Be(removedAt, "a heal never rewrites the first removal record");
        row.DiscountRemovedForPriceId.Should().Be("pri_team");
        (await Redemptions()).Should().Be(1);
    }

    [Fact]
    public async Task ChangePlan_LeavingPromoTargets_SendsRemove_AndRecordsWatch()
    {
        await Subscribe("pri_creator", 1);
        await SeedPromoRedemption("""["pri_creator","pri_pro"]""");
        PaddleNow("pri_creator", 1, PS);
        PaddleKeepsDiscount();

        IActionResult result = await Controller().ChangePlan(new ChangePlanRequestDto("team", 3), default);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        Json(result).GetProperty("promoDiscountRemoved").GetBoolean().Should().BeTrue();
        _provider.PlanChanges.Should().ContainSingle();
        _provider.PlanChanges[0].Items.Should().BeEquivalentTo([new BillingLineItem("pri_team", 3)]);
        _provider.PlanChanges[0].Discount.Should().Be(PlanChangeDiscount.Remove);
        BillingReconcileWatch watch = (await Watch())!;
        watch.ChangeKind.Should().Be("change_plan");
        watch.ChangeRequestedAt.Should().Be(_clock.Now);
        watch.ChangeRequestedBy.Should().Be(_user);
        PromotionRedemption row = await Redemption();
        row.DiscountRemovedAt.Should().Be(_clock.Now);
        row.DiscountRemovedForPriceId.Should().Be("pri_team");
    }

    private async Task<IActionResult> SwitchWithPromo(string price, string plan, string interval, string targets, PromotionDuration duration, int seats = 1)
    {
        await Subscribe(price, seats);
        await SeedPromoRedemption(targets, duration: duration);
        PaddleNow(price, seats, PS);
        PaddleKeepsDiscount();
        return await Controller().ChangePlan(new ChangePlanRequestDto(plan, seats == 1 ? null : seats, interval), default);
    }

    [Fact]
    public async Task ChangePlan_MonthlyToAnnualSamePlan_DropsMonthlyOnlyPromo_AndSaysSo()
    {
        IActionResult result = await SwitchWithPromo("pri_creator", "creator", "annual", """["pri_creator","pri_pro"]""", PromotionDuration.Forever);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        Json(result).GetProperty("promoDiscountRemoved").GetBoolean().Should().BeTrue();
        Json(result).GetProperty("message").GetString().Should().Contain("monthly offer does not carry over to the yearly price");
        _provider.PlanChanges.Should().ContainSingle();
        _provider.PlanChanges[0].Items.Should().BeEquivalentTo([new BillingLineItem("pri_creator_y", 1)]);
        _provider.PlanChanges[0].Discount.Should().Be(PlanChangeDiscount.Remove);
    }

    [Fact]
    public async Task ChangePlan_MonthlyToAnnualOtherPlan_DropsMonthlyOnlyPromo()
    {
        IActionResult result = await SwitchWithPromo("pri_creator", "pro", "annual", """["pri_creator","pri_pro"]""", PromotionDuration.Forever);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        _provider.PlanChanges[0].Items.Should().BeEquivalentTo([new BillingLineItem("pri_pro_y", 1)]);
        _provider.PlanChanges[0].Discount.Should().Be(PlanChangeDiscount.Remove);
    }

    [Fact]
    public async Task ChangePlan_MonthsPromoIsDroppedOnAnnualEvenWhenTargetsListTheYearlyPrice()
    {
        IActionResult result = await SwitchWithPromo("pri_creator", "creator", "annual", """["pri_creator","pri_creator_y"]""", PromotionDuration.Months);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        _provider.PlanChanges[0].Discount.Should().Be(PlanChangeDiscount.Remove);
    }

    [Fact]
    public async Task ChangePlan_MonthlyToAnnual_KeepsAnIncludeAnnualPromo()
    {
        IActionResult result = await SwitchWithPromo("pri_creator", "creator", "annual", """["pri_creator","pri_creator_y"]""", PromotionDuration.Forever);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        Json(result).GetProperty("promoDiscountRemoved").GetBoolean().Should().BeFalse();
        _provider.PlanChanges[0].Discount.Should().Be(PlanChangeDiscount.Unchanged);
    }

    [Fact]
    public async Task ChangePlan_MonthlyToMonthlyOtherTargetedPlan_LeavesPromoUnchanged()
    {
        IActionResult result = await SwitchWithPromo("pri_creator", "pro", "monthly", """["pri_creator","pri_pro"]""", PromotionDuration.Forever);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        _provider.PlanChanges.Should().ContainSingle();
        _provider.PlanChanges[0].Discount.Should().Be(PlanChangeDiscount.Unchanged);
    }

    /// <summary>Fails every save that writes a reconcile watch; any other save goes through.</summary>
    private sealed class FailingWatchSave(Func<Exception> fail) : SaveChangesInterceptor
    {
        public int Failures { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            ThrowOnWatch(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            ThrowOnWatch(eventData.Context);
            return ValueTask.FromResult(result);
        }

        private void ThrowOnWatch(DbContext? context)
        {
            if (context is not null && context.ChangeTracker.Entries<BillingReconcileWatch>().Any(e => e.State is EntityState.Added or EntityState.Modified))
            {
                Failures++;
                throw fail();
            }
        }
    }

    [Fact]
    public async Task ChangePlan_LeavingPromoTargets_ClientAbortsAfterThePatch_StillRecordsRemovalAndWatch()
    {
        await Subscribe("pri_creator", 1);
        await SeedPromoRedemption("""["pri_creator","pri_pro"]""");
        PaddleNow("pri_creator", 1, PS);
        PaddleKeepsDiscount();
        using CancellationTokenSource request = new();
        _provider.AfterChangePlan = request.Cancel; // Paddle accepted the PATCH, then the client went away.

        IActionResult result = await Controller().ChangePlan(new ChangePlanRequestDto("team", 3), request.Token);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        _provider.PlanChanges.Should().ContainSingle();
        _provider.PlanChanges[0].Discount.Should().Be(PlanChangeDiscount.Remove);
        (await Watch())!.ChangeKind.Should().Be("change_plan", "the change reached Paddle, so its watch is written regardless of the abort");
        PromotionRedemption row = await Redemption();
        row.DiscountRemovedAt.Should().Be(_clock.Now, "the discount is gone at Paddle, so the removal is recorded regardless of the abort");
        row.DiscountRemovedForPriceId.Should().Be("pri_team");
    }

    public static TheoryData<string> WatchSaveFailures => ["db_update", "other"];

    [Theory]
    [MemberData(nameof(WatchSaveFailures))]
    public async Task ChangePlan_LeavingPromoTargets_WatchSaveFails_StillRecordsRemoval_AndTracksNoWatch(string failure)
    {
        await Subscribe("pri_creator", 1);
        await SeedPromoRedemption("""["pri_creator","pri_pro"]""");
        PaddleNow("pri_creator", 1, PS);
        PaddleKeepsDiscount();
        FailingWatchSave interceptor = new(() => failure == "db_update"
            ? new DbUpdateException("watch insert failed")
            : new InvalidOperationException("watch table unavailable"));
        await using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(_connection).AddInterceptors(interceptor).Options);

        IActionResult result = await Controller(db).ChangePlan(new ChangePlanRequestDto("team", 3), default);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        Json(result).GetProperty("promoDiscountRemoved").GetBoolean().Should().BeTrue();
        interceptor.Failures.Should().BeGreaterThan(0, "the watch write was attempted and failed");
        PromotionRedemption row = await Redemption();
        row.DiscountRemovedAt.Should().Be(_clock.Now, "the removal's save must not re-attempt the failed watch write");
        row.DiscountRemovedForPriceId.Should().Be("pri_team");
        (await Watch()).Should().BeNull();
        db.ChangeTracker.Entries<BillingReconcileWatch>().Should().BeEmpty("a failed watch write leaves nothing for the next save in the scope to re-attempt");
    }

    [Fact]
    public async Task ChangePlan_WithinPromoTargets_SendsUnchanged_AndRecordsWatch()
    {
        await Subscribe("pri_creator", 1);
        await SeedPromoRedemption("""["pri_creator","pri_pro"]""");
        PaddleNow("pri_creator", 1, PS);
        PaddleKeepsDiscount();

        IActionResult result = await Controller().ChangePlan(new ChangePlanRequestDto("pro", 1), default);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        _provider.PlanChanges.Should().ContainSingle();
        _provider.PlanChanges[0].Discount.Should().Be(PlanChangeDiscount.Unchanged);
        (await Watch())!.ChangeKind.Should().Be("change_plan");
        (await Redemption()).DiscountRemovedAt.Should().BeNull();
    }

    [Fact]
    public async Task ChangePlan_KeepWithLiveDiscount_SendsUnchanged_AndRecordsKeepWatch()
    {
        await Subscribe("pri_team", 5);
        await using (InferenceApiDbContext db = NewDb())
        {
            Subscription sub = await db.Subscriptions.SingleAsync(s => s.OrganizationId == _org);
            sub.ScheduledPlanId = sub.PlanId;
            sub.ScheduledSeats = 3;
            await db.SaveChangesAsync();
        }

        // Team is not among the promotion's targets, yet "keep" changes no plan: the discount must stay.
        await SeedPromoRedemption("""["pri_creator","pri_pro"]""");
        PaddleNow("pri_team", 3, PS);
        PaddleKeepsDiscount();

        IActionResult result = await Controller().ChangePlan(new ChangePlanRequestDto("team", 5), default);

        ((ObjectResult)result).StatusCode.Should().Be(202);
        Json(result).GetProperty("direction").GetString().Should().Be("keep");
        _provider.PlanChanges.Should().ContainSingle();
        _provider.PlanChanges[0].Discount.Should().Be(PlanChangeDiscount.Unchanged);
        (await Watch())!.ChangeKind.Should().Be("keep");
        (await Redemption()).DiscountRemovedAt.Should().BeNull();
    }
}
