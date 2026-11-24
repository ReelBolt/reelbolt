using System;
using System.Collections.Generic;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>B9a: the controller surface over a fake provider: gating, roles, webhook status codes.</summary>
public sealed class BillingControllerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _org = Guid.NewGuid();
    private readonly FakeProvider _provider = new();

    public BillingControllerTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = _org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.Plans.AddRange(
            new Plan { Id = Guid.NewGuid(), Key = "trial", Name = "Trial", Kind = PlanKind.Trial, SortOrder = 0 },
            new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, PriceMonthlyUsd = 29, MonthlyCreditsMillis = 1_000_000, TopUpAllowed = true, SortOrder = 1, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_creator"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "team", Name = "Team", Kind = PlanKind.PerSeat, MinSeats = 3, PriceMonthlyUsd = 49, MonthlyCreditsMillis = 1_500_000, TopUpAllowed = true, SortOrder = 2, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_team"}}""" },
            // On sale in the catalog, but with no price id for any provider: the summary must not
            // offer it, so this row is what the "omitted" assertions below are about.
            new Plan { Id = Guid.NewGuid(), Key = "pro", Name = "Pro", Kind = PlanKind.Subscription, PriceMonthlyUsd = 79, MonthlyCreditsMillis = 3_500_000, TopUpAllowed = true, SortOrder = 3 });
        db.SaveChanges();
        _provider.Mirror = RowAsProviderState;
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>The local row as Paddle would report it once every webhook has landed (the default fake state).</summary>
    private ProviderSubscriptionState RowAsProviderState()
    {
        using InferenceApiDbContext db = NewDb();
        Subscription sub = db.Subscriptions.AsNoTracking().Single(s => s.Provider != null && s.Status != SubscriptionStatus.Canceled);
        Plan plan = db.Plans.AsNoTracking().Single(p => p.Id == sub.PlanId);
        return new ProviderSubscriptionState(
            "active",
            [new BillingLineItem(BillingEventProcessor.PriceIdsOf(plan, "fake").Single(), sub.Seats)],
            sub.CancelAtPeriodEnd,
            sub.PeriodEnd,
            sub.PeriodStart);
    }

    private static ProviderSubscriptionState PaddleHas(string priceId, int quantity = 1, bool cancelScheduled = false)
        => new("active", [new BillingLineItem(priceId, quantity)], cancelScheduled, null);

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private sealed class FakeUser(Guid? org, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => org;
        public OrganizationRole? OrgRole => role;
        public bool IsPlatformAdmin => false;
    }

    internal sealed class FakeTenant : ITenantContext
    {
        public Guid? OrganizationId => null;
        public bool IsSystem => true;
        public IDisposable BeginSystemScope() => new Nop();
        public IDisposable BeginOrganizationScope(Guid organizationId) => new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }
    }

    /// <summary>Verifies "X-Fake-Signature: ok" and parses the body as a serialized <see cref="BillingEvent"/>.</summary>
    internal sealed class FakeProvider(string name = "fake", bool configured = true) : IBillingProvider
    {
        public string Name => name;
        public bool IsConfigured => configured;
        public IReadOnlyList<int> TopUpPacks => [500, 1000];
        public string? TopUpPriceId(int credits) => TopUpPacks.Contains(credits) ? $"pri_topup_{credits}" : null;
        public CheckoutRequest? LastCheckout { get; private set; }
        private int _checkouts;
        public string? CheckoutCustomerId { get; set; }
        public List<string> Cancels { get; } = [];
        public List<string> Resumes { get; } = [];
        public List<(string Sub, IReadOnlyList<BillingLineItem> Items, PlanChangeTiming Timing, PlanChangeDiscount Discount)> PlanChanges { get; } = [];
        public Exception? Fail { get; set; }
        public bool LiveSubscription { get; set; }
        public Exception? ListFail { get; set; }
        public List<(string Customer, Guid Org)> LiveChecks { get; } = [];

        public Task<bool> HasLiveSubscriptionAsync(string providerCustomerId, Guid organizationId, CancellationToken ct)
        {
            LiveChecks.Add((providerCustomerId, organizationId));
            if (ListFail is not null) throw ListFail;
            return Task.FromResult(LiveSubscription);
        }

        /// <summary>When set, checkout creation throws it (a provider refusal).</summary>
        public Exception? CheckoutFailure { get; set; }

        /// <summary>
        /// When set, awaited inside <see cref="CreateCheckoutAsync"/> with the checkout's number (1, 2, ...)
        /// before the provider answers: the window in which a parallel request can interleave (F-5).
        /// </summary>
        public Func<int, Task> BeforeCheckoutReturns { get; set; }

        public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct)
        {
            if (CheckoutFailure is not null) throw CheckoutFailure;
            LastCheckout = request;
            // A fresh transaction per checkout, as the provider does (txn_1, txn_2, ...).
            int n = Interlocked.Increment(ref _checkouts);
            if (BeforeCheckoutReturns is { } hook)
            {
                await hook(n);
            }

            return new CheckoutSession($"https://pay.example/c/{n}", $"txn_{n}", CheckoutCustomerId ?? request.ExistingCustomerId);
        }

        /// <summary>When set, portal creation throws it.</summary>
        public Exception? PortalFailure { get; set; }

        public Task<PortalSession> CreatePortalSessionAsync(string providerCustomerId, string? providerSubscriptionId, CancellationToken ct)
            => PortalFailure is not null ? throw PortalFailure : Task.FromResult(new PortalSession("https://pay.example/portal", "https://pay.example/portal/cancel", "https://pay.example/portal/pm"));

        public async Task CancelSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
        {
            await GateAsync();
            if (Fail is not null) throw Fail;
            Cancels.Add(providerSubscriptionId);
        }

        public Task ResumeSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
        {
            if (Fail is not null) throw Fail;
            Resumes.Add(providerSubscriptionId);
            return Task.CompletedTask;
        }

        public List<ProviderDiscountSpec> DiscountsCreated { get; } = [];
        public List<(string DiscountId, bool? Active, DateTime? ExpiresAt, bool ClearExpiry)> DiscountUpdates { get; } = [];

        /// <summary>When set, discount create/update calls throw it (a provider 4xx is an <see cref="System.Net.Http.HttpRequestException"/>).</summary>
        public Exception? DiscountFailure { get; set; }

        public int PreviewCalls { get; private set; }
        public string? LastPreviewDiscountId { get; private set; }

        public Task<string> CreateDiscountAsync(ProviderDiscountSpec spec, CancellationToken ct)
        {
            if (DiscountFailure is not null) throw DiscountFailure;
            DiscountsCreated.Add(spec);
            return Task.FromResult($"dsc_{DiscountsCreated.Count}");
        }

        public Task UpdateDiscountAsync(string discountId, bool? active, DateTime? expiresAt, bool clearExpiry, CancellationToken ct)
        {
            if (DiscountFailure is not null) throw DiscountFailure;
            DiscountUpdates.Add((discountId, active, expiresAt, clearExpiry));
            return Task.CompletedTask;
        }

        /// <summary>When set, the preview answers this instead of the canned totals.</summary>
        public PricePreview? Preview { get; set; }

        public Task<PricePreview> PreviewPriceAsync(IReadOnlyList<BillingLineItem> items, string? discountId, CancellationToken ct)
        {
            PreviewCalls++;
            LastPreviewDiscountId = discountId;
            if (Preview is not null) return Task.FromResult(Preview);
            return Task.FromResult(discountId is null
                ? new PricePreview("USD", "$20.00", "$0.00", "$20.00")
                : new PricePreview("USD", "$20.00", "-$4.00", "$16.00"));
        }

        /// <summary>Every transaction id <see cref="CancelTransactionAsync"/> was called with, in order (C10).</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> CancelCalls { get; } = new();

        /// <summary>When set, the next cancel throws it and clears itself (a transient provider failure).</summary>
        public Exception? CancelFailureOnce
        {
            get => Volatile.Read(ref _cancelFailure);
            set => Volatile.Write(ref _cancelFailure, value);
        }

        /// <summary>When set, every cancel of a transaction it maps to an exception throws that exception (a lasting failure).</summary>
        public Func<string, Exception> CancelFailureFor { get; set; }

        /// <summary>What a cancel answers when it does not fail.</summary>
        public ProviderTransactionCancel CancelResult { get; set; } = ProviderTransactionCancel.Cancelled;

        public Task<ProviderTransactionCancel> CancelTransactionAsync(string providerTransactionId, CancellationToken ct)
        {
            CancelCalls.Enqueue(providerTransactionId);
            if (Interlocked.Exchange(ref _cancelFailure, null) is { } failure)
            {
                throw failure;
            }

            if (CancelFailureFor?.Invoke(providerTransactionId) is { } persistent)
            {
                throw persistent;
            }

            return Task.FromResult(CancelResult);
        }

        private Exception? _cancelFailure;

        public async Task ChangePlanAsync(string providerSubscriptionId, IReadOnlyList<BillingLineItem> items, PlanChangeTiming timing, PlanChangeDiscount discount, CancellationToken ct)
        {
            await GateAsync();
            if (Fail is not null) throw Fail;
            PlanChanges.Add((providerSubscriptionId, items, timing, discount));
            // Paddle moves the items (and drops a removed discount) at once; the local row only follows when the webhook lands.
            if (Live is not null) Live = Live with { Items = items, Discount = discount == PlanChangeDiscount.Remove ? null : Live.Discount };
        }

        /// <summary>Completed when a PATCH/POST reaches the provider while <see cref="Hold"/> is set.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>While set, the next PATCH/POST waits on it (after signalling <see cref="Entered"/>).</summary>
        public TaskCompletionSource? Hold { get; set; }

        private async Task GateAsync()
        {
            if (Hold is not { } hold) return;
            Hold = null;
            Entered.TrySetResult();
            await hold.Task;
        }

        /// <summary>Paddle's own view of the subscription. Null: Paddle agrees with the local row (<see cref="Mirror"/>).</summary>
        public ProviderSubscriptionState? Live { get; set; }

        /// <summary>Paddle's view when <see cref="Live"/> is unset: the local row's plan and seats, i.e. every webhook has landed.</summary>
        public Func<ProviderSubscriptionState>? Mirror { get; set; }

        public Exception? GetFail { get; set; }
        public List<string> SubscriptionReads { get; } = [];

        public Task<ProviderSubscriptionState> GetSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
        {
            SubscriptionReads.Add(providerSubscriptionId);
            if (GetFail is not null) throw GetFail;
            return Task.FromResult(Live ?? Mirror?.Invoke() ?? throw new InvalidOperationException("No provider state."));
        }

        public Task<BillingSnapshot> SnapshotSubscriptionAsync(string providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<BillingSnapshot>> CompletedUpgradeTransactionsAsync(string providerSubscriptionId, DateTime since, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<string?> FindLiveSubscriptionIdAsync(string providerCustomerId, Guid organizationId, CancellationToken ct) => throw new NotSupportedException();

        public int Resolves { get; private set; }

        /// <summary>When set, <see cref="ResolveAsync"/> throws it (the provider API could not be read).</summary>
        public Exception? ResolveFail { get; set; }

        public Task<BillingEvent> ResolveAsync(BillingEvent evt, CancellationToken ct)
        {
            Resolves++;
            if (ResolveFail is not null) throw ResolveFail;
            return Task.FromResult(evt);
        }

        /// <summary>The fake stands in for Paddle's customer-facing name unless a test says otherwise.</summary>
        public string DisplayName { get; set; } = "Paddle";

        public int MaxSeats { get; set; } = 200;
        public int MaxSelfServeSeats => MaxSeats;

        public BillingEvent VerifyAndParseWebhook(string rawBody, IReadOnlyDictionary<string, string> headers)
        {
            if (!headers.TryGetValue("X-Fake-Signature", out string? sig) || sig != "ok")
            {
                throw new WebhookVerificationException("bad signature");
            }

            return JsonSerializer.Deserialize<BillingEvent>(rawBody)!;
        }
    }

    private BillingController Controller(
        bool enabled,
        Guid? org,
        OrganizationRole? role,
        string? signature = null,
        string body = "",
        IBillingProvider? provider = null,
        PaddleOptions? paddle = null,
        IAssistantAllowanceService? allowance = null,
        TimeProvider? time = null,
        IOrganizationEntitlements? entitlements = null,
        bool noProvider = false,
        IReadOnlyList<IBillingProvider>? providers = null,
        BillingProviderOptions? selection = null,
        ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? productEvents = null)
    {
        // The seat cap now comes from the provider; the Paddle options argument keeps setting it for the default fake.
        if (provider is null) _provider.MaxSeats = paddle?.MaxSelfServeSeats ?? 200;
        InferenceApiDbContext db = NewDb();
        FakeUser fakeUser = new(org, role);
        // Checkout reads email verification from the database, so the caller exists there as verified.
        db.ApplicationUsers.Add(new ApplicationUser { Id = fakeUser.UserId, Email = $"{fakeUser.UserId:N}@example.test", EmailVerifiedAt = DateTime.UtcNow });
        db.SaveChanges();
        BillingController c = new(
            db,
            fakeUser,
            Options.Create(new BillingOptions { Enabled = enabled }),
            noProvider ? [] : providers ?? [provider ?? _provider],
            new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance),
            new FakeTenant(),
            Options.Create(paddle ?? new PaddleOptions()),
            NullLogger<BillingController>.Instance,
            assistantAllowance: allowance,
            time: time,
            entitlements: entitlements,
            providerSelection: selection is null ? null : Options.Create(selection),
            productEvents: productEvents);
        DefaultHttpContext http = new();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (signature is not null) http.Request.Headers["X-Fake-Signature"] = signature;
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return c;
    }

    private async Task Subscribe(string planKey = "creator")
    {
        await using InferenceApiDbContext db = NewDb();
        Plan plan = await db.Plans.SingleAsync(p => p.Key == planKey);
        await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance).ProcessAsync(new BillingEvent
        {
            Provider = "fake",
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "subscription.created",
            OccurredAt = DateTime.UtcNow,
            Kind = BillingEventKind.SubscriptionChanged,
            OrganizationId = _org,
            CustomerId = "ctm_1",
            SubscriptionId = "sub_1",
            Status = SubscriptionStatus.Active,
            PeriodStart = DateTime.UtcNow,
            PeriodEnd = DateTime.UtcNow.AddMonths(1),
            Items = [new BillingLineItem(plan.Key == "team" ? "pri_team" : "pri_creator", 3)]
        }, "{}");
    }

    private async Task SubscribeOrg(Guid org, string subscriptionId)
    {
        await using InferenceApiDbContext db = NewDb();
        db.Organizations.Add(new Organization { Id = org, Name = "o2", Slug = "o2-" + org.ToString("N"), Kind = OrganizationKind.Team });
        await db.SaveChangesAsync();
        await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance).ProcessAsync(new BillingEvent
        {
            Provider = "fake",
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "subscription.created",
            OccurredAt = DateTime.UtcNow,
            Kind = BillingEventKind.SubscriptionChanged,
            OrganizationId = org,
            CustomerId = "ctm_" + subscriptionId,
            SubscriptionId = subscriptionId,
            Status = SubscriptionStatus.Active,
            PeriodStart = DateTime.UtcNow,
            PeriodEnd = DateTime.UtcNow.AddMonths(1),
            Items = [new BillingLineItem("pri_team", 5)]
        }, "{}");
    }

    private static string EventBody(string id, Guid org) => JsonSerializer.Serialize(new BillingEvent
    {
        Provider = "fake",
        EventId = id,
        EventType = "transaction.completed",
        OccurredAt = DateTime.UtcNow,
        Kind = BillingEventKind.TopUpPaid,
        OrganizationId = org,
        TransactionId = "txn_" + id,
        TopUpCredits = 500
    });

    [Fact]
    public async Task Disabled_EveryEndpointIs404()
    {
        BillingController c = Controller(false, _org, OrganizationRole.Owner, "ok", "{}");

        (await c.Summary(default)).Should().BeOfType<NotFoundResult>();
        (await c.Checkout(new CheckoutRequestDto("creator", null, null), default)).Should().BeOfType<NotFoundResult>();
        (await c.Portal(default)).Should().BeOfType<NotFoundResult>();
        (await c.CancelSubscription(default)).Should().BeOfType<NotFoundResult>();
        (await c.ResumeSubscription(default)).Should().BeOfType<NotFoundResult>();
        (await c.ChangePlan(new ChangePlanRequestDto("creator", null), default)).Should().BeOfType<NotFoundResult>();
        (await c.Webhook("fake", default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Summary_ReportsFirstVideoGrantStatusUnavailable_WhenTheClaimHasADeniedReason()
    {
        InferenceApiDbContext db = NewDb();
        Guid user = Guid.NewGuid();
        Guid personal = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = "denied@example.com", EmailVerifiedAt = DateTime.UtcNow });
        db.Organizations.Add(new Organization { Id = personal, Name = "p", Slug = "p-denied", Kind = OrganizationKind.Personal });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = personal, UserId = user, Role = OrganizationRole.Owner });
        db.TrialClaims.Add(new TrialClaim
        {
            Id = Guid.NewGuid(),
            EmailSha256 = CreditAccountProvisioner.HashEmail("denied@example.com"),
            OrganizationId = personal,
            FirstVideoDeniedReason = "vpn_proxy_hosting"
        });
        db.SaveChanges();

        OkObjectResult summary = (OkObjectResult)await SummaryController(true, personal, OrganizationRole.Owner, user, BuildProvisioner(db));

        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(summary.Value));
        json.RootElement.GetProperty("firstVideoGrantStatus").GetString().Should().Be("unavailable");
        json.RootElement.GetProperty("firstVideoGrant").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    /// <summary>
    /// C6: the dashboard's billing page reads <c>GET /api/v1/billing/summary</c> as its first
    /// balance-bearing call, so the one-time trial grant must already have landed by the time it
    /// answers. This is the wiring the live deployment was missing: the grant used to happen only
    /// on a workflow execute, so a user who signed up, verified, and opened the billing page saw
    /// "Credits available: 0.00" forever. Asserting the summary path is what keeps the trigger on
    /// the read the page actually makes.
    /// </summary>
    [Fact]
    public async Task Summary_TriggersTheOneTimeTrialGrant_ForAVerifiedPersonalOwner()
    {
        // Seed an eligible Personal workspace + verified Owner on a fresh account.
        InferenceApiDbContext db = NewDb();
        Guid user = Guid.NewGuid();
        Guid personal = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = "trial@example.com", EmailVerifiedAt = DateTime.UtcNow });
        db.Organizations.Add(new Organization { Id = personal, Name = "p", Slug = "p", Kind = OrganizationKind.Personal });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = personal, UserId = user, Role = OrganizationRole.Owner });
        db.SaveChanges();

        ICreditAccountProvisioner provisioner = BuildProvisioner(db);

        // Before any Summary: no account, no claim, no ledger entry.
        await using (InferenceApiDbContext pre = NewDb())
        {
            (await new CreditLedger(pre).GetBalanceAsync(personal)).BalanceMillis.Should().Be(0);
            (await pre.TrialClaims.CountAsync()).Should().Be(0);
        }

        // A verified Personal Owner calling Summary (billing on) must see the grant land.
        OkObjectResult summary = (OkObjectResult)await SummaryController(true, personal, OrganizationRole.Owner, user, provisioner);
        object? payload = summary!.Value;
        payload.Should().NotBeNull();

        // The grant landed exactly once, on the Personal org, and the summary reports it.
        await using InferenceApiDbContext post = NewDb();
        CreditLedgerResult balance = await new CreditLedger(post).GetBalanceAsync(personal);
        balance.BalanceMillis.Should().Be(850_000); // 100 trial credits + the 750-credit first-video grant.
        (await post.TrialClaims.CountAsync()).Should().Be(1);
        (await post.CreditGrants.CountAsync(g => g.Source == CreditGrantSource.Trial)).Should().Be(1);

        // The additive summary block reports the first-video grant (credits, expiresAt, remaining).
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(payload));
        System.Text.Json.JsonElement fv = json.RootElement.GetProperty("firstVideoGrant");
        json.RootElement.GetProperty("firstVideoGrantStatus").GetString().Should().Be("granted");
        fv.GetProperty("credits").GetDouble().Should().Be(750);
        fv.GetProperty("remaining").GetDouble().Should().Be(750);
        fv.GetProperty("expiresAt").ValueKind.Should().Be(System.Text.Json.JsonValueKind.String);

        // A repeat call is an idempotent no-op: the balance does not double.
        await SummaryController(true, personal, OrganizationRole.Owner, user, provisioner);
        await using InferenceApiDbContext repeat = NewDb();
        (await new CreditLedger(repeat).GetBalanceAsync(personal)).BalanceMillis.Should().Be(850_000);
        (await repeat.TrialClaims.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Summary_TrialGrantDoesNotFire_WhenBillingIsOff()
    {
        InferenceApiDbContext db = NewDb();
        Guid user = Guid.NewGuid();
        Guid personal = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = "off@example.com", EmailVerifiedAt = DateTime.UtcNow });
        db.Organizations.Add(new Organization { Id = personal, Name = "p", Slug = "p", Kind = OrganizationKind.Personal });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = personal, UserId = user, Role = OrganizationRole.Owner });
        db.SaveChanges();

        ICreditAccountProvisioner provisioner = BuildProvisioner(db);

        // Billing off: the endpoint 404s and the provisioner must not run (Disabled outcome).
        (await SummaryController(false, personal, OrganizationRole.Owner, user, provisioner)).Should().BeOfType<NotFoundResult>();

        await using InferenceApiDbContext post = NewDb();
        (await new CreditLedger(post).GetBalanceAsync(personal)).BalanceMillis.Should().Be(0);
        (await post.TrialClaims.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// QA-7: a repeat summary call must not make the database reject a duplicate trial claim
    /// (IX_trial_claims_email_sha256): EF logs every failed SaveChanges as an error, so the claim
    /// is checked before it is inserted and a repeat call writes nothing.
    /// </summary>
    [Fact]
    public async Task Summary_RepeatCalls_LogNoDuplicateKeyError()
    {
        InferenceApiDbContext db = NewDb();
        Guid user = Guid.NewGuid();
        Guid personal = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = "again@example.com", EmailVerifiedAt = DateTime.UtcNow });
        db.Organizations.Add(new Organization { Id = personal, Name = "p", Slug = "p", Kind = OrganizationKind.Personal });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = personal, UserId = user, Role = OrganizationRole.Owner });
        db.SaveChanges();
        ErrorLog errors = new();
        ICreditAccountProvisioner provisioner = BuildProvisioner(db, errors);

        for (int i = 0; i < 3; i++)
        {
            (await SummaryController(true, personal, OrganizationRole.Owner, user, provisioner)).Should().BeOfType<OkObjectResult>();
        }

        errors.Entries.Should().BeEmpty();
        await using InferenceApiDbContext post = NewDb();
        (await new CreditLedger(post).GetBalanceAsync(personal)).BalanceMillis.Should().Be(850_000);
        (await post.TrialClaims.CountAsync()).Should().Be(1);
    }

    /// <summary>Collects every warning-or-worse log line any category writes.</summary>
    private sealed class ErrorLog : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Entries { get; } = new();

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose() { }

        private sealed class Logger(ErrorLog owner, string category) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
                Exception exception, Func<TState, Exception, string> formatter)
            {
                if (IsEnabled(logLevel)) owner.Entries.Enqueue($"{logLevel} {category}: {formatter(state, exception)}");
            }
        }
    }

    /// <summary>
    /// QA-1: billing on but no provider configured (no Paddle keys): the summary still answers,
    /// with no checkout to offer, instead of throwing over the missing provider.
    /// </summary>
    [Fact]
    public async Task Summary_WithBillingOnAndNoConfiguredProvider_AnswersWithoutCheckout()
    {
        IActionResult result = await Controller(true, _org, OrganizationRole.Owner, provider: new FakeProvider("paddle", configured: false))
            .Summary(default);

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Subject;
        JsonElement root = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement;
        root.GetProperty("billingProvider").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("paddle").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("plans").GetArrayLength().Should().Be(0, "no provider can sell a plan, so no price id reaches the browser");
        root.GetProperty("topUp").GetProperty("available").GetBoolean().Should().BeFalse();
        root.GetProperty("credits").GetProperty("available").GetDouble().Should().Be(0);
    }

    private Task<IActionResult> SummaryController(
        bool enabled, Guid? org, OrganizationRole? role, Guid user, ICreditAccountProvisioner provisioner)
    {
        InferenceApiDbContext db = NewDb();
        BillingController c = new(
            db,
            new FakeUserWithId(user, org, role),
            Options.Create(new BillingOptions { Enabled = enabled }),
            [_provider],
            new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance),
            new FakeTenant(),
            Options.Create(new PaddleOptions()),
            NullLogger<BillingController>.Instance,
            provisioner);
        DefaultHttpContext http = new();
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return c.Summary(CancellationToken.None);
    }

    private ICreditAccountProvisioner BuildProvisioner(InferenceApiDbContext db, ErrorLog log = null)
    {
        ServiceCollection services = new();
        Microsoft.Extensions.Logging.ILoggerFactory logs = log is null ? null : Microsoft.Extensions.Logging.LoggerFactory.Create(l => l.AddProvider(log));
        services.AddScoped(_ =>
        {
            DbContextOptionsBuilder<InferenceApiDbContext> b = new();
            b.UseSqlite(_connection);
            if (logs is not null) b.UseLoggerFactory(logs);
            return new InferenceApiDbContext(b.Options);
        });
        Dictionary<string, string?> cfg = new() { ["Billing:Enabled"] = "true" };
        return new CreditAccountProvisioner(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(cfg).Build(),
            NullLogger<CreditAccountProvisioner>.Instance);
    }

    private sealed class FakeUserWithId(Guid id, Guid? org, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId => id;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => org;
        public OrganizationRole? OrgRole => role;
        public bool IsPlatformAdmin => false;
    }

    [Fact]
    public async Task Webhook_BadOrMissingSignature_Is400_AndStoresNothing()
    {
        string body = EventBody("e1", _org);

        (await Controller(true, null, null, "wrong", body).Webhook("fake", default)).Should().BeOfType<BadRequestObjectResult>();
        (await Controller(true, null, null, null, body).Webhook("fake", default)).Should().BeOfType<BadRequestObjectResult>();

        await using InferenceApiDbContext db = NewDb();
        (await db.BillingWebhookEvents.CountAsync()).Should().Be(0);
        (await new CreditLedger(db).GetBalanceAsync(_org)).BalanceMillis.Should().Be(0);
    }

    [Fact]
    public async Task Webhook_UnknownProvider_Is404()
        => (await Controller(true, null, null, "ok", "{}").Webhook("stripe", default)).Should().BeOfType<NotFoundResult>();

    [Fact]
    public async Task Webhook_ValidIsAnonymousAndGrantsOnce_ReplayIs200WithoutAGrant()
    {
        string body = EventBody("e1", _org);

        OkObjectResult first = (OkObjectResult)await Controller(true, null, null, "ok", body).Webhook("fake", default);
        OkObjectResult second = (OkObjectResult)await Controller(true, null, null, "ok", body).Webhook("fake", default);

        first.Value!.ToString().Should().Contain("Applied");
        second.Value!.ToString().Should().Contain("Duplicate");
        await using InferenceApiDbContext db = NewDb();
        (await new CreditLedger(db).GetBalanceAsync(_org)).BalanceMillis.Should().Be(500_000);
    }

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(OrganizationRole.Admin)]
    public async Task Checkout_And_Portal_RequireOwnerOrBilling(OrganizationRole role)
    {
        (await Controller(true, _org, role).Checkout(new CheckoutRequestDto("creator", null, null), default)).Should().BeOfType<ForbidResult>();
        (await Controller(true, _org, role).Portal(default)).Should().BeOfType<ForbidResult>();
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Billing)]
    public async Task Checkout_OwnerAndBilling_StartAPlanCheckout(OrganizationRole role)
    {
        OkObjectResult ok = (OkObjectResult)await Controller(true, _org, role).Checkout(new CheckoutRequestDto("creator", null, null), default);

        ok.Value!.ToString().Should().Contain("https://pay.example/c/1");
        _provider.LastCheckout!.Plan!.Key.Should().Be("creator");
        _provider.LastCheckout.OrganizationId.Should().Be(_org);
    }

    [Fact]
    public async Task Checkout_RecordsCheckoutStartedOncePerUser_AndNothingWhenRefused()
    {
        var log = new ReelBolt.WorkflowEngine.Tests.Services.Telemetry.RecordingProductEventLog();
        var emitter = new ReelBolt.Inference.Api.Services.Telemetry.ProductEventEmitter(
            log, NullLogger<ReelBolt.Inference.Api.Services.Telemetry.ProductEventEmitter>.Instance);

        (await Controller(true, _org, OrganizationRole.Member, productEvents: emitter)
            .Checkout(new CheckoutRequestDto("creator", null, null), default)).Should().BeOfType<ForbidResult>();
        log.Rows.Should().BeEmpty("a refused checkout never started");

        (await Controller(true, _org, OrganizationRole.Owner, productEvents: emitter)
            .Checkout(new CheckoutRequestDto("creator", null, null), default)).Should().BeOfType<OkObjectResult>();
        log.Rows.Should().ContainSingle().Which.Event.Should().Be("billing.checkout_started");
        log.Rows[0].OrganizationId.Should().Be(_org);
    }

    [Fact]
    public async Task Checkout_NeedsExactlyOneOfPlanOrTopUp_AndAPurchasablePlan()
    {
        (await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto(null, null, null), default)).Should().BeOfType<BadRequestObjectResult>();
        (await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("creator", null, 500), default)).Should().BeOfType<BadRequestObjectResult>();
        (await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("trial", null, null), default)).Should().BeOfType<BadRequestObjectResult>();
        (await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("enterprise", null, null), default)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Checkout_TeamPlan_EnforcesTheThreeSeatMinimum_AndDefaultsToIt()
    {
        (await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("team", 2, null), default)).Should().BeOfType<BadRequestObjectResult>();

        await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("team", null, null), default);
        _provider.LastCheckout!.Seats.Should().Be(3);
        await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("team", 7, null), default);
        _provider.LastCheckout!.Seats.Should().Be(7);
    }

    [Fact]
    public async Task TopUp_IsRefusedWithoutAPaidPlan_AndAllowedWithOne()
    {
        // Trial / no subscription: D9 says paid plans only.
        ObjectResult refused = (ObjectResult)await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto(null, null, 500), default);
        refused.StatusCode.Should().Be(403);
        _provider.LastCheckout.Should().BeNull();

        await Subscribe();

        (await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto(null, null, 500), default)).Should().BeOfType<OkObjectResult>();
        _provider.LastCheckout!.TopUpCredits.Should().Be(500);
        (await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto(null, null, 777), default)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Checkout_WhileSubscribed_IsAConflict_NotASecondSubscription()
    {
        await Subscribe();

        (await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("team", null, null), default)).Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Portal_NeedsACustomer_ThenReturnsTheProviderLink()
    {
        (await Controller(true, _org, OrganizationRole.Owner).Portal(default)).Should().BeOfType<ConflictObjectResult>();

        await Subscribe();

        OkObjectResult ok = (OkObjectResult)await Controller(true, _org, OrganizationRole.Billing).Portal(default);
        JsonElement body = Json(ok);
        body.GetProperty("url").GetString().Should().Be("https://pay.example/portal");
        body.GetProperty("cancelUrl").GetString().Should().Be("https://pay.example/portal/cancel");
        body.GetProperty("updatePaymentMethodUrl").GetString().Should().Be("https://pay.example/portal/pm");
    }

    [Theory]
    [InlineData("stripe_not_configured", 503, "billing_not_configured")]
    [InlineData("anything_else", 503, "billing_provider_error")]
    public async Task Checkout_Maps503ToNotConfigured_OnlyForTheStripeNotConfiguredCode(string code, int _, string expected)
    {
        _provider.CheckoutFailure = new BillingProviderException(code, 503, "d");

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("creator", null, null), default);

        Json(result is ObjectResult o ? new OkObjectResult(o.Value) : result).GetProperty("error").GetString().Should().Be(expected);
        ((ObjectResult)result).StatusCode.Should().Be(expected == "billing_not_configured" ? 503 : 502);
    }

    [Theory]
    [InlineData("stripe_not_configured", "billing_not_configured", 503)]
    [InlineData("anything_else", "billing_provider_error", 502)]
    public async Task Portal_Maps503ToNotConfigured_OnlyForTheStripeNotConfiguredCode(string code, string expected, int status)
    {
        await Subscribe();
        _provider.PortalFailure = new BillingProviderException(code, 503, "d");

        ObjectResult result = (ObjectResult)await Controller(true, _org, OrganizationRole.Owner).Portal(default);

        result.StatusCode.Should().Be(status);
        Json(new OkObjectResult(result.Value)).GetProperty("error").GetString().Should().Be(expected);
    }

    // ------------------------------------------------------------------ cancel / resume / change plan

    private static JsonElement Json(IActionResult result)
        => JsonDocument.Parse(JsonSerializer.Serialize(((ObjectResult)result).Value)).RootElement;

    private async Task Mutate(Action<Subscription> change)
    {
        await using InferenceApiDbContext db = NewDb();
        Subscription sub = await db.Subscriptions.SingleAsync(s => s.Provider != null);
        change(sub);
        await db.SaveChangesAsync();
    }

    /// <summary>Gives the fixture's Pro plan a price id so it is sellable (it has none by default, see the ctor).</summary>
    private async Task SellPro()
    {
        await using InferenceApiDbContext db = NewDb();
        (await db.Plans.SingleAsync(p => p.Key == "pro")).ProviderPriceIdsJson = """{"fake":{"monthly":"pri_pro"}}""";
        await db.SaveChangesAsync();
    }

    private async Task<Guid> PlanId(string key)
    {
        await using InferenceApiDbContext db = NewDb();
        return (await db.Plans.SingleAsync(p => p.Key == key)).Id;
    }

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(OrganizationRole.Admin)]
    public async Task CancelResumeAndChangePlan_RequireOwnerOrBilling(OrganizationRole role)
    {
        await Subscribe();

        (await Controller(true, _org, role).CancelSubscription(default)).Should().BeOfType<ForbidResult>();
        (await Controller(true, _org, role).ResumeSubscription(default)).Should().BeOfType<ForbidResult>();
        (await Controller(true, _org, role).ChangePlan(new ChangePlanRequestDto("team", 3), default)).Should().BeOfType<ForbidResult>();
        _provider.Cancels.Should().BeEmpty();
        _provider.Resumes.Should().BeEmpty();
        _provider.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelResumeAndChangePlan_WithoutAPaidSubscription_Are409NoSubscription()
    {
        foreach (IActionResult result in new[]
        {
            await Controller(true, _org, OrganizationRole.Owner).CancelSubscription(default),
            await Controller(true, _org, OrganizationRole.Owner).ResumeSubscription(default),
            await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("team", 3), default)
        })
        {
            result.Should().BeOfType<ConflictObjectResult>();
            Json(result).GetProperty("error").GetString().Should().Be("no_subscription");
        }
    }

    [Fact]
    public async Task Cancel_AsksTheProvider_AndAnswers202WithThePeriodEnd()
    {
        await Subscribe();
        DateTime periodEnd = DateTime.UtcNow.AddMonths(1);
        await Mutate(s => s.PeriodEnd = periodEnd);

        IActionResult result = await Controller(true, _org, OrganizationRole.Billing).CancelSubscription(default);

        result.Should().BeOfType<AcceptedResult>();
        Json(result).GetProperty("status").GetString().Should().Be("cancel_requested");
        Json(result).GetProperty("effectiveAt").GetDateTime().Should().Be(periodEnd);
        _provider.Cancels.Should().Equal("sub_1");
    }

    [Fact]
    public async Task Cancel_WhenAlreadyScheduled_Is200_AndDoesNotCallTheProvider()
    {
        await Subscribe();
        DateTime at = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        await Mutate(s => { s.CancelAtPeriodEnd = true; s.CancelEffectiveAt = at; });

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).CancelSubscription(default);

        result.Should().BeOfType<OkObjectResult>();
        Json(result).GetProperty("status").GetString().Should().Be("cancel_scheduled");
        Json(result).GetProperty("effectiveAt").GetDateTime().Should().Be(at);
        _provider.Cancels.Should().BeEmpty();
    }

    [Fact]
    public async Task Resume_WhenNothingIsScheduled_Is200Active_ElseAsksTheProvider()
    {
        await Subscribe();

        IActionResult idle = await Controller(true, _org, OrganizationRole.Owner).ResumeSubscription(default);
        idle.Should().BeOfType<OkObjectResult>();
        Json(idle).GetProperty("status").GetString().Should().Be("active");
        _provider.Resumes.Should().BeEmpty();

        await Mutate(s => s.CancelAtPeriodEnd = true);
        IActionResult resumed = await Controller(true, _org, OrganizationRole.Owner).ResumeSubscription(default);
        resumed.Should().BeOfType<AcceptedResult>();
        Json(resumed).GetProperty("status").GetString().Should().Be("resume_requested");
        _provider.Resumes.Should().Equal("sub_1");
    }

    /// <summary>
    /// Security F-2: a seat change Paddle already applied but whose webhook has not landed is still in
    /// flight; a cancel (or resume) now would be judged against a row Paddle no longer matches.
    /// </summary>
    [Fact]
    public async Task Cancel_AfterAnUnconfirmedChange_Is409ChangePending_AndSendsNothing()
    {
        await Subscribe("team");
        await Mutate(s => s.Seats = 5);
        _provider.Live = PaddleHas("pri_team", 8);

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).CancelSubscription(default);

        result.Should().BeOfType<ConflictObjectResult>();
        Json(result).GetProperty("error").GetString().Should().Be("change_pending");
        Json(result).GetProperty("message").GetString().Should().Be(ChangePendingMessage);
        _provider.Cancels.Should().BeEmpty();
    }

    [Fact]
    public async Task Resume_AfterAnUnconfirmedChange_Is409ChangePending_AndSendsNothing()
    {
        await Subscribe("team");
        await Mutate(s => { s.Seats = 5; s.CancelAtPeriodEnd = true; });
        _provider.Live = PaddleHas("pri_team", 8, cancelScheduled: true);

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).ResumeSubscription(default);

        result.Should().BeOfType<ConflictObjectResult>();
        Json(result).GetProperty("error").GetString().Should().Be("change_pending");
        _provider.Resumes.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_WhenPaddleAlreadyHasTheCancel_Is200_AndSendsNothing()
    {
        await Subscribe();
        DateTime periodEnd = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
        _provider.Live = new ProviderSubscriptionState("active", [new BillingLineItem("pri_creator", 1)], CancelScheduled: true, periodEnd);

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).CancelSubscription(default);

        result.Should().BeOfType<OkObjectResult>();
        Json(result).GetProperty("status").GetString().Should().Be("cancel_scheduled");
        Json(result).GetProperty("effectiveAt").GetDateTime().Should().Be(periodEnd);
        _provider.Cancels.Should().BeEmpty();
    }

    [Fact]
    public async Task Resume_WhenPaddleAlreadyDroppedTheCancel_Is202_AndSendsNothing()
    {
        await Subscribe();
        await Mutate(s => s.CancelAtPeriodEnd = true);
        _provider.Live = PaddleHas("pri_creator");

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).ResumeSubscription(default);

        result.Should().BeOfType<AcceptedResult>();
        Json(result).GetProperty("status").GetString().Should().Be("resume_requested");
        _provider.Resumes.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_WithADowngradeHeld_StillAsksTheProviderOnce()
    {
        await Subscribe("team");
        Guid team;
        await using (InferenceApiDbContext db = NewDb())
        {
            team = (await db.Plans.SingleAsync(p => p.Key == "team")).Id;
        }

        await Mutate(s => { s.Seats = 6; s.ScheduledPlanId = team; s.ScheduledSeats = 4; s.ScheduledChangeAt = DateTime.UtcNow.AddDays(10); });
        _provider.Live = PaddleHas("pri_team", 4);

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).CancelSubscription(default);

        result.Should().BeOfType<AcceptedResult>();
        _provider.Cancels.Should().Equal("sub_1");
    }

    [Fact]
    public async Task CancelAndResume_WhenPaddleCannotBeRead_Are502_AndSendNothing()
    {
        await Subscribe();
        _provider.GetFail = new HttpRequestException("down");

        IActionResult cancel = await Controller(true, _org, OrganizationRole.Owner).CancelSubscription(default);
        await Mutate(s => s.CancelAtPeriodEnd = true);
        IActionResult resume = await Controller(true, _org, OrganizationRole.Owner).ResumeSubscription(default);

        foreach (IActionResult result in new[] { cancel, resume })
        {
            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
            Json(result).GetProperty("error").GetString().Should().Be("billing_provider_error");
        }

        _provider.Cancels.Should().BeEmpty();
        _provider.Resumes.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangePlan_Upgrade_IsImmediate_AndAnnouncesTheCreditDifference()
    {
        await SellPro();
        await Subscribe("creator");
        DateTime start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        await Mutate(s => { s.PeriodStart = start; s.PeriodEnd = start.AddMonths(1); });

        // At the period start the whole period remains, so the whole difference is announced.
        IActionResult result = await Controller(true, _org, OrganizationRole.Owner, time: new FixedClock(start))
            .ChangePlan(new ChangePlanRequestDto("pro", null), default);

        result.Should().BeOfType<AcceptedResult>();
        JsonElement body = Json(result);
        body.GetProperty("direction").GetString().Should().Be("upgrade");
        body.GetProperty("effective").GetString().Should().Be("now");
        body.GetProperty("creditGrantMillis").GetInt64().Should().Be(2_500_000);
        var change = _provider.PlanChanges.Single();
        change.Sub.Should().Be("sub_1");
        change.Timing.Should().Be(PlanChangeTiming.Immediately);
        change.Items.Should().Equal(new BillingLineItem("pri_pro", 1));
    }

    [Fact]
    public async Task ChangePlan_Downgrade_WaitsForThePeriodEnd()
    {
        await SellPro();
        await Subscribe("creator");
        DateTime periodEnd = DateTime.UtcNow.AddMonths(1);
        Guid pro = await PlanId("pro");
        await Mutate(s => { s.PlanId = pro; s.PeriodEnd = periodEnd; });

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("creator", null), default);

        result.Should().BeOfType<AcceptedResult>();
        JsonElement body = Json(result);
        body.GetProperty("direction").GetString().Should().Be("downgrade");
        body.GetProperty("effective").GetString().Should().Be("period_end");
        body.GetProperty("effectiveAt").GetDateTime().Should().Be(periodEnd);
        body.GetProperty("creditGrantMillis").GetInt64().Should().Be(0);
        _provider.PlanChanges.Single().Timing.Should().Be(PlanChangeTiming.NextBillingPeriod);
    }

    /// <summary>Gives Creator an annual price id and mirrors the row with the id of its interval (annual period = more than 40 days).</summary>
    private async Task SellCreatorAnnual()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            (await db.Plans.SingleAsync(p => p.Key == "creator")).ProviderPriceIdsJson = """{"fake":{"monthly":"pri_creator","annual":"pri_creator_y"}}""";
            (await db.Plans.SingleAsync(p => p.Key == "pro")).ProviderPriceIdsJson = """{"fake":{"monthly":"pri_pro","annual":"pri_pro_y"}}""";
            await db.SaveChangesAsync();
        }

        _provider.Mirror = () =>
        {
            using InferenceApiDbContext db = NewDb();
            Subscription sub = db.Subscriptions.AsNoTracking().Single(x => x.Provider != null && x.Status != SubscriptionStatus.Canceled);
            Plan plan = db.Plans.AsNoTracking().Single(x => x.Id == sub.PlanId);
            string id = BillingEventProcessor.PriceIdFor(plan, "fake", SubscriptionInterval.Of(sub.PeriodStart, sub.PeriodEnd))!;
            return new ProviderSubscriptionState("active", [new BillingLineItem(id, sub.Seats)], sub.CancelAtPeriodEnd, sub.PeriodEnd, sub.PeriodStart);
        };
    }

    private static string ErrorOf(IActionResult r) => Json(r).GetProperty("error").GetString()!;

    [Fact]
    public async Task Checkout_Annual_UsesTheAnnualPriceId_AndDefaultsToMonthly()
    {
        await SellCreatorAnnual();

        await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("creator", null, null, Interval: "annual"), default);
        _provider.LastCheckout!.Interval.Should().Be(BillingInterval.Annual);

        await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("creator", null, null), default);
        _provider.LastCheckout!.Interval.Should().Be(BillingInterval.Monthly);
    }

    [Fact]
    public async Task Checkout_Annual_IsRefused_WithoutACatalogPriceOrAProviderPriceId()
    {
        // Creator has an annual catalog price but no annual price id configured here.
        IActionResult noId = await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("creator", null, null, Interval: "annual"), default);
        noId.Should().BeOfType<BadRequestObjectResult>();
        ErrorOf(noId).Should().Be("annual_unavailable");

        IActionResult bad = await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("creator", null, null, Interval: "weekly"), default);
        ErrorOf(bad).Should().Be("invalid_interval");
        _provider.LastCheckout.Should().BeNull();
    }

    [Fact]
    public async Task Checkout_TopUp_IgnoresTheInterval()
    {
        await Subscribe("creator");
        IActionResult r = await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto(null, null, 500, Interval: "annual"), default);
        r.Should().BeOfType<OkObjectResult>();
        _provider.LastCheckout!.TopUpCredits.Should().Be(500);
    }

    [Fact]
    public async Task ChangePlan_MonthlyToAnnual_IsImmediate_WithTheAnnualPriceId()
    {
        await SellCreatorAnnual();
        await Subscribe("creator");

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("creator", null, "annual"), default);

        result.Should().BeOfType<AcceptedResult>();
        Json(result).GetProperty("direction").GetString().Should().Be("upgrade");
        var change = _provider.PlanChanges.Single();
        change.Timing.Should().Be(PlanChangeTiming.Immediately);
        change.Items.Should().Equal(new BillingLineItem("pri_creator_y", 1));
    }

    [Fact]
    public async Task ChangePlan_AnnualToMonthly_IsRefused409()
    {
        await SellCreatorAnnual();
        await Subscribe("creator");
        await Mutate(s => { s.PeriodStart = DateTime.UtcNow; s.PeriodEnd = s.PeriodStart!.Value.AddYears(1); });

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("pro", null, "monthly"), default);

        result.Should().BeOfType<ConflictObjectResult>();
        ErrorOf(result).Should().Be("interval_change_not_supported");
        _provider.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangePlan_OnAnAnnualSubscription_DefaultsToAnnual_AndUpgradesWithTheAnnualId()
    {
        await SellCreatorAnnual();
        await Subscribe("creator");
        await Mutate(s => { s.PeriodStart = DateTime.UtcNow; s.PeriodEnd = s.PeriodStart!.Value.AddYears(1); });

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("pro", null), default);

        result.Should().BeOfType<AcceptedResult>();
        Json(result).GetProperty("direction").GetString().Should().Be("upgrade");
        var change = _provider.PlanChanges.Single();
        change.Timing.Should().Be(PlanChangeTiming.Immediately);
        change.Items.Should().Equal(new BillingLineItem("pri_pro_y", 1));
    }

    [Fact]
    public async Task ChangePlan_AnnualDowngrade_WaitsForTheTermEnd()
    {
        await SellCreatorAnnual();
        await Subscribe("creator");
        Guid pro = await PlanId("pro");
        await Mutate(s => { s.PlanId = pro; s.PeriodStart = DateTime.UtcNow; s.PeriodEnd = s.PeriodStart!.Value.AddYears(1); });

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("creator", null), default);

        Json(result).GetProperty("direction").GetString().Should().Be("downgrade");
        _provider.PlanChanges.Single().Timing.Should().Be(PlanChangeTiming.NextBillingPeriod);
        _provider.PlanChanges.Single().Items.Should().Equal(new BillingLineItem("pri_creator_y", 1));
    }

    [Fact]
    public async Task ChangePlan_Annual_IsRefusedWhenTheTargetHasNoAnnualPriceId()
    {
        await SellPro(); // monthly id only
        await Subscribe("creator");

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("pro", null, "annual"), default);

        ErrorOf(result).Should().Be("annual_unavailable");
        _provider.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task Summary_ExposesTheAnnualPrice_AndTheSubscriptionInterval()
    {
        await SellCreatorAnnual();
        await Subscribe("creator");
        await Mutate(s => { s.PeriodStart = DateTime.UtcNow; s.PeriodEnd = s.PeriodStart!.Value.AddYears(1); });

        JsonElement body = Json(await Controller(true, _org, OrganizationRole.Owner).Summary(default));

        body.GetProperty("subscription").GetProperty("interval").GetString().Should().Be("annual");
        JsonElement creator = body.GetProperty("plans").EnumerateArray().Single(p => p.GetProperty("key").GetString() == "creator");
        creator.GetProperty("priceAnnualUsd").GetDecimal().Should().Be(290);
        creator.GetProperty("annualPriceId").GetString().Should().Be("pri_creator_y");
        creator.GetProperty("priceId").GetString().Should().Be("pri_creator");
        JsonElement team = body.GetProperty("plans").EnumerateArray().Single(p => p.GetProperty("key").GetString() == "team");
        team.GetProperty("annualPriceId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ChangePlan_PerSeatTarget_SendsTheSeats_AndEnforcesTheMinimum()
    {
        await Subscribe("creator");

        IActionResult low = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("team", 2), default);
        low.Should().BeOfType<BadRequestObjectResult>();
        Json(low).GetProperty("error").GetString().Should().Be("below_min_seats");

        await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("team", 4), default);
        _provider.PlanChanges.Single().Items.Should().Equal(new BillingLineItem("pri_team", 4));
    }

    [Fact]
    public async Task ChangePlan_RefusesUnknownAndNonSelfServePlans()
    {
        await Subscribe("creator");

        IActionResult unknown = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("enterprise", null), default);
        IActionResult trial = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("trial", null), default);
        IActionResult unpriced = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("pro", null), default);

        Json(unknown).GetProperty("error").GetString().Should().Be("unknown_plan");
        Json(trial).GetProperty("error").GetString().Should().Be("plan_not_self_serve");
        Json(unpriced).GetProperty("error").GetString().Should().Be("plan_not_self_serve");
        _provider.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangePlan_WhileACancelIsScheduled_Is409CancelScheduled()
    {
        await SellPro();
        await Subscribe("creator");
        await Mutate(s => s.CancelAtPeriodEnd = true);

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("pro", null), default);

        result.Should().BeOfType<ConflictObjectResult>();
        Json(result).GetProperty("error").GetString().Should().Be("cancel_scheduled");
        _provider.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangePlan_ToTheCurrentPlan_IsNoChange_UnlessADowngradeIsHeld_ThenItKeepsThePlan()
    {
        await SellPro();
        await Subscribe("creator");
        Guid pro = await PlanId("pro");
        Guid creator = await PlanId("creator");
        await Mutate(s => s.PlanId = pro);

        IActionResult same = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("pro", null), default);
        same.Should().BeOfType<ConflictObjectResult>();
        Json(same).GetProperty("error").GetString().Should().Be("no_change");

        await Mutate(s => { s.ScheduledPlanId = creator; s.ScheduledSeats = 1; s.ScheduledChangeAt = DateTime.UtcNow.AddDays(10); });
        IActionResult keep = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("pro", null), default);

        keep.Should().BeOfType<AcceptedResult>();
        Json(keep).GetProperty("direction").GetString().Should().Be("keep");
        var change = _provider.PlanChanges.Single();
        change.Items.Should().Equal(new BillingLineItem("pri_pro", 1));
        change.Timing.Should().Be(PlanChangeTiming.NextBillingPeriod, "Pro is already paid for this period");
    }

    [Fact]
    public async Task ProviderRefusal_IsMapped_ProviderOutageIs502()
    {
        await SellPro();
        await Subscribe("creator");

        _provider.Fail = new BillingProviderException("subscription_locked_pending_changes", 400, "pending changes");
        IActionResult refused = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("pro", null), default);
        ((ObjectResult)refused).StatusCode.Should().Be(409);
        Json(refused).GetProperty("error").GetString().Should().Be("billing_provider_refused");
        Json(refused).GetProperty("providerCode").GetString().Should().Be("subscription_locked_pending_changes");
        Json(refused).GetProperty("message").GetString().Should().Be(
            "This subscription already has a change being processed at Paddle. Wait a few minutes, then try again.");

        _provider.Fail = new System.Net.Http.HttpRequestException("down");
        IActionResult down = await Controller(true, _org, OrganizationRole.Owner).CancelSubscription(default);
        ((ObjectResult)down).StatusCode.Should().Be(502);
        Json(down).GetProperty("error").GetString().Should().Be("billing_provider_error");
    }

    /// <summary>Audit L1: the provider's own wording never reaches the client, only our fixed text per code.</summary>
    [Theory]
    [InlineData("subscription_locked_pending_changes")]
    [InlineData("some_code_we_do_not_know")]
    public async Task ProviderRefusal_NeverEchoesTheProvidersDetail(string code)
    {
        await SellPro();
        await Subscribe("creator");
        const string detail = "Internal note: customer ctm_secret has pending items <script>";
        _provider.Fail = new BillingProviderException(code, 400, detail);

        IActionResult refused = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("pro", null), default);

        JsonSerializer.Serialize(((ObjectResult)refused).Value).Should().NotContain("ctm_secret").And.NotContain("Internal note");
        Json(refused).GetProperty("providerCode").GetString().Should().Be(code);
        Json(refused).GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ProviderRefusal_UnknownCode_GetsTheGenericMessage()
    {
        await Subscribe("creator");
        _provider.Fail = new BillingProviderException("brand_new_code", 422, "whatever Paddle said");

        IActionResult refused = await Controller(true, _org, OrganizationRole.Owner).CancelSubscription(default);

        Json(refused).GetProperty("message").GetString().Should().Be(
            "Paddle refused this change. Try again later or open Manage in Paddle.");
    }

    /// <summary>
    /// Audit H1: buying 997 seats for a few minutes and dropping back must not mint credits. The increase
    /// is prorated and granted pro rata; the decrease is billed from the next period (Paddle refunds
    /// nothing), and the webhook holds it, so no credits are taken back for seats still paid for.
    /// </summary>
    [Fact]
    public async Task Seats_IncreaseIsImmediate_DecreaseWaitsForThePeriodEnd_AndTheWebhookHoldsIt()
    {
        await Subscribe("team");
        DateTime periodEnd = DateTime.UtcNow.AddMonths(1);
        await Mutate(s => s.PeriodEnd = periodEnd);
        PaddleOptions bigCap = new() { MaxSelfServeSeats = 1000 };

        IActionResult up = await Controller(true, _org, OrganizationRole.Owner, paddle: bigCap).ChangeSeats(new ChangeSeatsRequestDto(1000), default);

        up.Should().BeOfType<AcceptedResult>();
        Json(up).GetProperty("effective").GetString().Should().Be("now");
        Json(up).GetProperty("creditGrantMillis").GetInt64().Should().BeGreaterThan(0);
        _provider.PlanChanges[0].Timing.Should().Be(PlanChangeTiming.Immediately);
        await SeatsWebhook("evt_up", 1000);
        long afterUp = await BalanceMillis();

        IActionResult down = await Controller(true, _org, OrganizationRole.Owner, paddle: bigCap).ChangeSeats(new ChangeSeatsRequestDto(3), default);

        down.Should().BeOfType<AcceptedResult>();
        JsonElement body = Json(down);
        body.GetProperty("direction").GetString().Should().Be("downgrade");
        body.GetProperty("effective").GetString().Should().Be("period_end");
        body.GetProperty("effectiveAt").GetDateTime().Should().Be(periodEnd);
        body.GetProperty("creditGrantMillis").GetInt64().Should().Be(0);
        body.GetProperty("message").GetString().Should().Contain("end of the current billing period");
        _provider.PlanChanges[1].Timing.Should().Be(PlanChangeTiming.NextBillingPeriod);
        _provider.PlanChanges[1].Items.Should().Equal(new BillingLineItem("pri_team", 3));

        await SeatsWebhook("evt_down", 3);
        await using InferenceApiDbContext db = NewDb();
        Subscription row = await db.Subscriptions.SingleAsync(s => s.Provider != null);
        row.Seats.Should().Be(1000);
        row.ScheduledSeats.Should().Be(3);
        (await BalanceMillis()).Should().Be(afterUp);
    }

    [Fact]
    public async Task SeatsAndChangePlan_AboveTheSelfServeCap_Are400TooManySeats()
    {
        await Subscribe("team");

        IActionResult seats = await Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(201), default);
        IActionResult plan = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("team", 201), default);
        IActionResult configured = await Controller(true, _org, OrganizationRole.Owner, paddle: new PaddleOptions { MaxSelfServeSeats = 10 })
            .ChangeSeats(new ChangeSeatsRequestDto(11), default);

        foreach ((IActionResult result, int max) in new[] { (seats, 200), (plan, 200), (configured, 10) })
        {
            result.Should().BeOfType<BadRequestObjectResult>();
            Json(result).GetProperty("error").GetString().Should().Be("too_many_seats");
            Json(result).GetProperty("maxSeats").GetInt32().Should().Be(max);
        }

        _provider.PlanChanges.Should().BeEmpty();
        (await Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(200), default)).Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task ChangePlan_UpgradeEstimate_IsProratedOverTheRemainingPeriod()
    {
        await SellPro();
        await Subscribe("creator");
        DateTime now = new(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);
        await Mutate(s => { s.PeriodStart = now.AddDays(-15); s.PeriodEnd = now.AddDays(15); });

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner, time: new FixedClock(now))
            .ChangePlan(new ChangePlanRequestDto("pro", null), default);

        Json(result).GetProperty("creditGrantMillis").GetInt64().Should().Be(1_250_000);
        Json(result).GetProperty("effectiveAt").GetDateTime().Should().Be(now);
    }

    [Fact]
    public async Task ChangePlan_ToAPlanWithoutAPrice_IsNotSelfServe()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            db.Plans.Add(new Plan { Id = Guid.NewGuid(), Key = "custom", Name = "Custom", Kind = PlanKind.Subscription, PriceMonthlyUsd = null, MonthlyCreditsMillis = 9_000_000, IsActive = true, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_custom"}}""" });
            await db.SaveChangesAsync();
        }

        await Subscribe("creator");

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("custom", null), default);

        Json(result).GetProperty("error").GetString().Should().Be("plan_not_self_serve");
        _provider.PlanChanges.Should().BeEmpty();
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private async Task<long> BalanceMillis()
    {
        await using InferenceApiDbContext db = NewDb();
        return (await new CreditLedger(db).GetBalanceAsync(_org)).BalanceMillis;
    }

    private async Task SeatsWebhook(string eventId, int seats)
    {
        await using InferenceApiDbContext db = NewDb();
        Subscription sub = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Provider != null);
        (await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance).ProcessAsync(new BillingEvent
        {
            Provider = "fake",
            EventId = eventId,
            EventType = "subscription.updated",
            OccurredAt = DateTime.UtcNow,
            Kind = BillingEventKind.SubscriptionChanged,
            OrganizationId = _org,
            CustomerId = "ctm_1",
            SubscriptionId = "sub_1",
            Status = SubscriptionStatus.Active,
            PeriodStart = sub.PeriodStart,
            PeriodEnd = sub.PeriodEnd,
            Items = [new BillingLineItem("pri_team", seats)]
        }, "{}")).Should().Be(BillingEventOutcome.Applied);
    }

    private const string ChangePendingMessage = "Your last change is still being confirmed. Refresh this page in a minute, then try again.";

    /// <summary>
    /// Re-audit F1: 5 → 200 seats is charged at once, but the row says 5 until the webhook lands. A
    /// second request (6 seats) judged against the row would be an "upgrade", sent prorated_immediately,
    /// and Paddle would refund ~194 seats while webhook 1 still grants 5 → 200. The live Paddle items
    /// (200) disagree with the row (5), so the second change is refused and nothing is sent.
    /// </summary>
    [Fact]
    public async Task Seats_WhileTheLastChangeIsUnconfirmed_Is409ChangePending_AndSendsNothing()
    {
        await Subscribe("team");
        await Mutate(s => { s.Seats = 5; s.PeriodEnd = DateTime.UtcNow.AddMonths(1); });
        _provider.Live = PaddleHas("pri_team", 5);

        IActionResult up = await Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(200), default);
        up.Should().BeOfType<AcceptedResult>();
        _provider.PlanChanges.Single().Timing.Should().Be(PlanChangeTiming.Immediately);

        IActionResult second = await Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(6), default);

        second.Should().BeOfType<ConflictObjectResult>();
        Json(second).GetProperty("error").GetString().Should().Be("change_pending");
        Json(second).GetProperty("message").GetString().Should().Be(ChangePendingMessage);
        _provider.PlanChanges.Should().HaveCount(1, "the second change must never reach Paddle");
    }

    /// <summary>
    /// Security R1: two PARALLEL changes ({200} and {6}) both read row = 5 and Paddle = 5, so the
    /// live-state check alone lets both through as upgrades. The per-org claim refuses the second one
    /// while the first is still talking to Paddle, and nothing of it is sent.
    /// </summary>
    [Fact]
    public async Task Seats_TwoParallelChangesForOneOrg_TheSecondIs409ChangePending_AndSendsNothing()
    {
        await Subscribe("team");
        await Mutate(s => { s.Seats = 5; s.PeriodEnd = DateTime.UtcNow.AddMonths(1); });
        _provider.Live = PaddleHas("pri_team", 5);
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.Hold = hold;

        Task<IActionResult> first = Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(200), default);
        await _provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        IActionResult second = await Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(6), default);
        IActionResult cancel = await Controller(true, _org, OrganizationRole.Owner).CancelSubscription(default);
        IActionResult resume = await Controller(true, _org, OrganizationRole.Owner).ResumeSubscription(default);
        IActionResult plan = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("team", 7), default);

        foreach (IActionResult refused in new[] { second, cancel, resume, plan })
        {
            refused.Should().BeOfType<ConflictObjectResult>();
            Json(refused).GetProperty("error").GetString().Should().Be("change_pending");
            Json(refused).GetProperty("message").GetString().Should().Be(ChangePendingMessage);
        }

        hold.SetResult();
        (await first.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeOfType<AcceptedResult>();
        _provider.PlanChanges.Should().ContainSingle().Which.Items.Should().Equal(new BillingLineItem("pri_team", 200));
        _provider.Cancels.Should().BeEmpty();
        _provider.SubscriptionReads.Should().HaveCount(1, "the refused change never reads Paddle either");
    }

    [Fact]
    public async Task Seats_ParallelChangesForDifferentOrgs_BothProceed()
    {
        Guid other = Guid.NewGuid();
        await Subscribe("team");
        await SubscribeOrg(other, "sub_2");
        _provider.Live = PaddleHas("pri_team", 3);
        FakeProvider otherProvider = new() { Live = PaddleHas("pri_team", 5) };
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.Hold = hold;

        Task<IActionResult> first = Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(10), default);
        await _provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        IActionResult second = await Controller(true, other, OrganizationRole.Owner, provider: otherProvider).ChangeSeats(new ChangeSeatsRequestDto(9), default);

        second.Should().BeOfType<AcceptedResult>();
        otherProvider.PlanChanges.Should().ContainSingle().Which.Items.Should().Equal(new BillingLineItem("pri_team", 9));
        hold.SetResult();
        (await first.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task Seats_AfterTheProviderThrows_TheClaimIsReleased_AndTheNextChangeGoesThrough()
    {
        await Subscribe("team");
        _provider.Live = PaddleHas("pri_team", 3);
        _provider.Fail = new InvalidOperationException("boom");

        Func<Task> crash = () => Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(10), default);
        await crash.Should().ThrowAsync<InvalidOperationException>();

        _provider.Fail = null;
        IActionResult next = await Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(10), default);

        next.Should().BeOfType<AcceptedResult>();
        _provider.PlanChanges.Should().ContainSingle();
    }

    [Fact]
    public async Task Seats_AfterTheWebhookLands_TheNextChangeIsJudgedAgainstTheNewSeats()
    {
        await Subscribe("team");
        await Mutate(s => { s.Seats = 5; s.PeriodEnd = DateTime.UtcNow.AddMonths(1); });
        _provider.Live = PaddleHas("pri_team", 5);
        await Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(200), default);
        await SeatsWebhook("evt_200", 200);

        IActionResult down = await Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(6), default);

        down.Should().BeOfType<AcceptedResult>();
        Json(down).GetProperty("direction").GetString().Should().Be("downgrade");
        _provider.PlanChanges[1].Timing.Should().Be(PlanChangeTiming.NextBillingPeriod);
        _provider.PlanChanges[1].Items.Should().Equal(new BillingLineItem("pri_team", 6));
    }

    /// <summary>Re-audit F1, plans: Creator → Team (charged now), then Pro before the webhook would be an "upgrade" from the row's Creator.</summary>
    [Fact]
    public async Task ChangePlan_WhileTheLastChangeIsUnconfirmed_Is409ChangePending_ThenAcceptedOnceItLands()
    {
        await SellPro();
        await Subscribe("creator");
        _provider.Live = PaddleHas("pri_creator");

        (await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("team", 3), default)).Should().BeOfType<AcceptedResult>();
        IActionResult pending = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("pro", null), default);

        pending.Should().BeOfType<ConflictObjectResult>();
        Json(pending).GetProperty("error").GetString().Should().Be("change_pending");
        _provider.PlanChanges.Should().HaveCount(1);

        await SeatsWebhook("evt_team", 3);
        IActionResult landed = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("pro", null), default);

        landed.Should().BeOfType<AcceptedResult>();
        Json(landed).GetProperty("direction").GetString().Should().Be("downgrade", "Pro ($79) is below Team × 3 ($147)");
        _provider.PlanChanges[1].Timing.Should().Be(PlanChangeTiming.NextBillingPeriod);
    }

    /// <summary>
    /// Re-audit F2: with a downgrade held (row Pro, Paddle already on Creator), an upgrade to Team would
    /// be charged Creator → Team but credited Pro → Team. Only "keep" is allowed until the hold is gone.
    /// </summary>
    [Fact]
    public async Task ChangePlan_WhileADowngradeIsHeld_Is409DowngradeScheduled_ButKeepStillWorks()
    {
        await SellPro();
        await Subscribe("creator");
        Guid pro = await PlanId("pro");
        Guid creator = await PlanId("creator");
        await Mutate(s => { s.PlanId = pro; s.ScheduledPlanId = creator; s.ScheduledSeats = 1; s.ScheduledChangeAt = DateTime.UtcNow.AddDays(10); });
        _provider.Live = PaddleHas("pri_creator");

        IActionResult upgrade = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("team", 3), default);

        upgrade.Should().BeOfType<ConflictObjectResult>();
        Json(upgrade).GetProperty("error").GetString().Should().Be("downgrade_scheduled");
        Json(upgrade).GetProperty("message").GetString().Should().Be("Keep your current plan first, then change it.");
        _provider.PlanChanges.Should().BeEmpty();

        IActionResult keep = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("pro", null), default);

        keep.Should().BeOfType<AcceptedResult>();
        Json(keep).GetProperty("direction").GetString().Should().Be("keep");
        _provider.PlanChanges.Single().Items.Should().Equal(new BillingLineItem("pri_pro", 1));
        _provider.PlanChanges.Single().Timing.Should().Be(PlanChangeTiming.NextBillingPeriod);
    }

    [Fact]
    public async Task ChangePlan_WhenPaddleCannotBeAsked_Is502_AndSendsNothing()
    {
        await SellPro();
        await Subscribe("creator");
        _provider.GetFail = new HttpRequestException("Paddle 500");

        ObjectResult result = (ObjectResult)await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("pro", null), default);

        result.StatusCode.Should().Be(502);
        Json(result).GetProperty("error").GetString().Should().Be("billing_provider_error");
        _provider.SubscriptionReads.Should().Equal("sub_1");
        _provider.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangePlan_WhenPaddleHasACancelScheduled_Is409CancelScheduled()
    {
        await SellPro();
        await Subscribe("creator");
        _provider.Live = PaddleHas("pri_creator", cancelScheduled: true);

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("pro", null), default);

        result.Should().BeOfType<ConflictObjectResult>();
        Json(result).GetProperty("error").GetString().Should().Be("cancel_scheduled");
        _provider.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task Checkout_PersistsTheProviderCustomer_Once()
    {
        _provider.CheckoutCustomerId = "ctm_new";

        await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("creator", null, null), default);
        await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("creator", null, null), default);

        _provider.LastCheckout!.ExistingCustomerId.Should().Be("ctm_new", "the retried checkout reuses the stored customer");
        await using InferenceApiDbContext db = NewDb();
        BillingCustomer customer = await db.BillingCustomers.SingleAsync();
        customer.OrganizationId.Should().Be(_org);
        customer.Provider.Should().Be("fake");
        customer.ProviderCustomerId.Should().Be("ctm_new");
    }

    private async Task RememberCustomer(string customerId = "ctm_gap")
    {
        await using InferenceApiDbContext db = NewDb();
        await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance).RememberCustomerAsync(_org, "fake", customerId, default);
    }

    [Fact]
    public async Task Checkout_DuringTheWebhookGap_IsSubscriptionPending_NotASecondSubscription()
    {
        // F-04: paid at the provider, the webhook has not landed: a customer, no local subscription.
        await RememberCustomer();
        _provider.LiveSubscription = true;

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("creator", null, null), default);

        result.Should().BeOfType<ConflictObjectResult>();
        Json(result).GetProperty("error").GetString().Should().Be("subscription_pending");
        Json(result).GetProperty("message").GetString().Should().Be(
            "This workspace's new plan is still being confirmed. Refresh this page in a minute.");
        _provider.LiveChecks.Should().Equal(("ctm_gap", _org));
        _provider.LastCheckout.Should().BeNull();
    }

    [Fact]
    public async Task Checkout_WhenTheProviderCannotListSubscriptions_Is502_AndStartsNothing()
    {
        await RememberCustomer();
        _provider.ListFail = new HttpRequestException("Paddle 500");

        ObjectResult result = (ObjectResult)await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("creator", null, null), default);

        result.StatusCode.Should().Be(502);
        Json(result).GetProperty("error").GetString().Should().Be("billing_provider_error");
        _provider.LastCheckout.Should().BeNull();
    }

    [Fact]
    public async Task Checkout_WithACustomerAndNothingLiveAtTheProvider_Proceeds()
    {
        await RememberCustomer();

        (await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("creator", null, null), default)).Should().BeOfType<OkObjectResult>();

        _provider.LiveChecks.Should().Equal(("ctm_gap", _org));
        _provider.LastCheckout!.ExistingCustomerId.Should().Be("ctm_gap");
    }

    [Fact]
    public async Task Checkout_NeverListsProviderSubscriptions_ForATopUp_OrWithoutACustomer()
    {
        // No customer yet: nothing could be pending at the provider.
        (await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("creator", null, null), default)).Should().BeOfType<OkObjectResult>();
        _provider.LiveChecks.Should().BeEmpty();

        await Subscribe();
        _provider.LiveSubscription = true;
        (await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto(null, null, 500), default)).Should().BeOfType<OkObjectResult>();
        _provider.LiveChecks.Should().BeEmpty();
    }

    [Fact]
    public async Task Checkout_WhileSubscribed_PointsAtThePlanPage()
    {
        await Subscribe();

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).Checkout(new CheckoutRequestDto("team", null, null), default);

        Json(result).GetProperty("error").GetString().Should().Be("already_subscribed");
        Json(result).GetProperty("message").GetString().Should().Be(
            "This workspace already has a subscription. Change plan from the Plan & billing page instead of starting a second checkout.");
    }

    [Fact]
    public async Task Summary_ExposesTheScheduledCancelAndTheHeldDowngrade()
    {
        await Subscribe("team");
        DateTime at = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        Guid creator = await PlanId("creator");

        JsonElement before = Json(await Controller(true, _org, OrganizationRole.Owner).Summary(default)).GetProperty("subscription");
        before.GetProperty("cancelEffectiveAt").ValueKind.Should().Be(JsonValueKind.Null);
        before.GetProperty("scheduledChange").ValueKind.Should().Be(JsonValueKind.Null);

        await Mutate(s =>
        {
            s.CancelAtPeriodEnd = true;
            s.CancelEffectiveAt = at;
            s.ScheduledPlanId = creator;
            s.ScheduledSeats = 1;
            s.ScheduledChangeAt = at;
        });
        JsonElement sub = Json(await Controller(true, _org, OrganizationRole.Owner).Summary(default)).GetProperty("subscription");

        sub.GetProperty("cancelEffectiveAt").GetDateTime().Should().Be(at);
        JsonElement scheduled = sub.GetProperty("scheduledChange");
        scheduled.GetProperty("planKey").GetString().Should().Be("creator");
        scheduled.GetProperty("planName").GetString().Should().Be("Creator");
        scheduled.GetProperty("seats").GetInt32().Should().Be(1);
        scheduled.GetProperty("effectiveAt").GetDateTime().Should().Be(at);
    }

    [Fact]
    public async Task Seats_OnlyForAPerSeatSubscription_AboveTheMinimum()
    {
        await Subscribe("creator");
        (await Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(5), default)).Should().BeOfType<ConflictObjectResult>();

        await using (InferenceApiDbContext db = NewDb())
        {
            await db.Subscriptions.ExecuteDeleteAsync();
        }

        await Subscribe("team");
        (await Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(2), default)).Should().BeOfType<BadRequestObjectResult>();
        (await Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(5), default)).Should().BeOfType<AcceptedResult>();
        _provider.PlanChanges.Single().Items.Should().Equal(new BillingLineItem("pri_team", 5));
    }

    [Fact]
    public async Task Summary_ReportsPlanBalancePacksAndPurchasablePlans()
    {
        await Subscribe();

        OkObjectResult ok = (OkObjectResult)await Controller(true, _org, OrganizationRole.Member).Summary(default);

        string json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"planKey\":\"creator\"").And.Contain("\"balance\":1000").And.Contain("500").And.Contain("\"canManageBilling\":false");
        JsonDocument.Parse(json).RootElement.GetProperty("plans").EnumerateArray()
            .Select(p => p.GetProperty("key").GetString()).Should().BeEquivalentTo("creator", "team");
    }

    private sealed class FixedAllowance(AssistantAllowance allowance) : IAssistantAllowanceService
    {
        public Guid? AskedFor { get; private set; }

        public Task<AssistantAllowance> GetAsync(Guid organizationId, CancellationToken ct = default)
        {
            AskedFor = organizationId;
            return Task.FromResult(allowance);
        }

        public Task<long?> GetIncludedMillisAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult(allowance.IncludedMillis);

        public Task<AssistantAllowanceTerms> GetTermsAsync(Guid organizationId, CancellationToken ct = default)
            => Task.FromResult(new AssistantAllowanceTerms(allowance.IncludedMillis, allowance.PeriodStart, allowance.PeriodEnd));
    }

    [Fact]
    public async Task Summary_ReportsTheAssistantAllowance_InCredits()
    {
        DateTime start = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        DateTime end = new(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
        FixedAllowance allowance = new(new AssistantAllowance(100_000, 7_500, start, end));

        OkObjectResult ok = (OkObjectResult)await Controller(true, _org, OrganizationRole.Member, allowance: allowance).Summary(default);

        JsonElement meter = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement.GetProperty("assistantAllowance");
        meter.GetProperty("includedCredits").GetDouble().Should().Be(100);
        meter.GetProperty("usedCredits").GetDouble().Should().Be(7.5);
        meter.GetProperty("periodStart").GetDateTime().Should().Be(start);
        meter.GetProperty("periodEnd").GetDateTime().Should().Be(end);
        allowance.AskedFor.Should().Be(_org);
    }

    [Fact]
    public async Task Summary_ReportsAnUnlimitedAssistantAllowance_AsNullIncludedCredits()
    {
        FixedAllowance allowance = new(new AssistantAllowance(null, 2_000, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(29)));

        OkObjectResult ok = (OkObjectResult)await Controller(true, _org, OrganizationRole.Member, allowance: allowance).Summary(default);

        JsonElement meter = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement.GetProperty("assistantAllowance");
        meter.GetProperty("includedCredits").ValueKind.Should().Be(JsonValueKind.Null);
        meter.GetProperty("usedCredits").GetDouble().Should().Be(2);
    }

    [Fact]
    public async Task Summary_WithoutABillingProvider_ListsNoPurchasablePlans_InsteadOfThrowing()
    {
        // Billing on, no Paddle keys (a self-host or e2e stack): nothing is purchasable, but the page
        // still needs the plan, the balance and the entitlements. The plan list is projected lazily,
        // so the response is serialized here exactly as MVC would.
        await Subscribe();

        OkObjectResult ok = (OkObjectResult)await Controller(true, _org, OrganizationRole.Member,
            entitlements: new FixedEntitlements(new OrganizationEntitlementSet { Tier = "creator", MaxOutputHeight = 1080 }),
            noProvider: true).Summary(default);

        JsonElement root = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement;
        root.GetProperty("plans").GetArrayLength().Should().Be(0);
        root.GetProperty("entitlements").GetProperty("maxOutputHeight").GetInt32().Should().Be(1080);
    }

    [Fact]
    public async Task Summary_ExposesTheMaxOutputHeightEntitlement_NullWhenUnlimited()
    {
        await Subscribe();

        OkObjectResult capped = (OkObjectResult)await Controller(true, _org, OrganizationRole.Member,
            entitlements: new FixedEntitlements(new OrganizationEntitlementSet { Tier = "creator", MaxOutputHeight = 1080 })).Summary(default);
        JsonDocument.Parse(JsonSerializer.Serialize(capped.Value)).RootElement
            .GetProperty("entitlements").GetProperty("maxOutputHeight").GetInt32().Should().Be(1080);

        OkObjectResult unlimited = (OkObjectResult)await Controller(true, _org, OrganizationRole.Member,
            entitlements: new FixedEntitlements(OrganizationEntitlementSet.Unlimited)).Summary(default);
        JsonDocument.Parse(JsonSerializer.Serialize(unlimited.Value)).RootElement
            .GetProperty("entitlements").GetProperty("maxOutputHeight").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private sealed class FixedEntitlements(OrganizationEntitlementSet set) : IOrganizationEntitlements
    {
        public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
            => new(set);
    }

    /// <summary>
    /// The plans array carries, per plan, the price id its checkout will actually use — and a plan
    /// the active provider cannot sell is not in it at all. A blank price id must never be handed to
    /// the browser, because the browser hands it straight to Paddle's price preview.
    /// </summary>
    [Fact]
    public async Task Summary_ListsOnlySellablePlans_EachWithItsProvidersPriceId()
    {
        OkObjectResult ok = (OkObjectResult)await Controller(true, _org, OrganizationRole.Owner).Summary(default);

        JsonElement[] listed = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value))
            .RootElement.GetProperty("plans").EnumerateArray().ToArray();

        // "pro" is active and on sale in the catalog but has no price id, so it is absent rather than
        // present with an empty one; "trial" is not a plan anyone can buy.
        listed.Select(p => p.GetProperty("key").GetString()).Should().BeEquivalentTo("creator", "team");
        listed.Should().OnlyContain(p => p.GetProperty("priceId").GetString()!.StartsWith("pri_", StringComparison.Ordinal));
        listed.Single(p => p.GetProperty("key").GetString() == "creator")
            .GetProperty("priceId").GetString().Should().Be("pri_creator");
    }

    /// <summary>
    /// The dashboard is given the publishable token and the environment, because it cannot build a
    /// Paddle.js checkout without them — and it is given NOTHING else from that section. The API key
    /// and the webhook secret are the credentials that move money and trust notifications; neither
    /// may ever appear in a response the browser reads.
    /// </summary>
    [Theory]
    [InlineData("sandbox", "sandbox")]
    [InlineData("live", "production")]
    public async Task Summary_ServesTheClientTokenAndEnvironment_AndNeverTheSecrets(string configured, string expected)
    {
        PaddleOptions paddle = new()
        {
            ApiKey = "pdl_sdbx_apikey_THESECRET",
            WebhookSecret = "pdl_ntfset_THESECRET",
            ClientToken = "test_87d07575c0b6730d5305e81cff12",
            Environment = configured
        };

        OkObjectResult ok = (OkObjectResult)await Controller(
            true, _org, OrganizationRole.Owner, provider: new FakeProvider("paddle"), paddle: paddle).Summary(default);
        string json = JsonSerializer.Serialize(ok.Value);
        JsonElement client = JsonDocument.Parse(json).RootElement.GetProperty("paddle");

        client.GetProperty("clientToken").GetString().Should().Be("test_87d07575c0b6730d5305e81cff12");
        // Paddle.js's own vocabulary, not the configuration's: it takes 'sandbox' or 'production'.
        client.GetProperty("environment").GetString().Should().Be(expected);
        json.Should().NotContain("THESECRET");
    }

    /// <summary>
    /// With no client token configured there is no embedded checkout to offer, and the payload says
    /// so by omission rather than by an empty string the browser would try to initialize with.
    /// </summary>
    [Fact]
    public async Task Summary_OmitsTheClientToken_WhenNoneIsConfigured()
    {
        PaddleOptions paddle = new() { ApiKey = "pdl_sdbx_apikey_THESECRET", WebhookSecret = "w", Environment = "sandbox" };

        OkObjectResult ok = (OkObjectResult)await Controller(
            true, _org, OrganizationRole.Owner, provider: new FakeProvider("paddle"), paddle: paddle).Summary(default);

        JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement
            .GetProperty("paddle").TryGetProperty("clientToken", out JsonElement token).Should().BeTrue();
        token.ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// The paddle block carries the active organization's Paddle customer id, so the browser can tell
    /// Paddle.js which buyer is signed in (Paddle Retain). It is the provider customer id (ctm_…),
    /// never the internal organization id.
    /// </summary>
    [Fact]
    public async Task Summary_ServesThePaddleCustomerIdOfTheActiveOrganization()
    {
        await RememberPaddleCustomer("ctm_paddle_buyer");

        OkObjectResult ok = (OkObjectResult)await Controller(
            true, _org, OrganizationRole.Owner,
            provider: new FakeProvider("paddle"),
            paddle: new PaddleOptions { Environment = "sandbox" }).Summary(default);

        JsonElement paddle = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement.GetProperty("paddle");
        paddle.TryGetProperty("customerId", out JsonElement customerId).Should().BeTrue();
        customerId.GetString().Should().Be("ctm_paddle_buyer");
    }

    /// <summary>
    /// An organization that has never checked out has no customer row: a brand-new workspace is a
    /// normal case, so customerId is null, not an error and not the organization id.
    /// </summary>
    [Fact]
    public async Task Summary_ServesNullPaddleCustomerId_WhenTheOrganizationHasNoCustomerRow()
    {
        OkObjectResult ok = (OkObjectResult)await Controller(
            true, _org, OrganizationRole.Owner,
            provider: new FakeProvider("paddle"),
            paddle: new PaddleOptions { Environment = "sandbox" }).Summary(default);

        JsonElement paddle = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement.GetProperty("paddle");
        paddle.TryGetProperty("customerId", out JsonElement customerId).Should().BeTrue();
        customerId.ValueKind.Should().Be(JsonValueKind.Null);
    }

    private async Task RememberPaddleCustomer(string customerId)
    {
        await using InferenceApiDbContext db = NewDb();
        await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance)
            .RememberCustomerAsync(_org, "paddle", customerId, default);
    }

    /// <summary>
    /// The Paddle customer id is served only to roles that can manage billing (Owner, Billing). A
    /// Member or Admin — who may read the summary but cannot act on billing — gets it as null even
    /// when the organization has a customer row. The environment and publishable client token beside
    /// it are unaffected: they are for every role (the token is what lets the billing page show
    /// Paddle's localized prices).
    ///
    /// This is least-privilege hygiene matching <c>canManageBilling</c>, **not** a confidentiality or
    /// tenant boundary: the identifier is the organization's own data, and the organization's account
    /// export (<c>AccountExportService</c>) deliberately includes it for Art. 15/20 portability. The
    /// assertion below is about which response carries it, not about who may hold it.
    /// </summary>
    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(OrganizationRole.Admin)]
    public async Task Summary_HidesThePaddleCustomerId_FromRolesThatCannotManageBilling(OrganizationRole role)
    {
        await RememberPaddleCustomer("ctm_paddle_buyer");

        OkObjectResult ok = (OkObjectResult)await Controller(
            true, _org, role,
            provider: new FakeProvider("paddle"),
            paddle: new PaddleOptions { Environment = "sandbox", ClientToken = "test_publishable" }).Summary(default);

        JsonElement paddle = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement.GetProperty("paddle");
        paddle.TryGetProperty("customerId", out JsonElement customerId).Should().BeTrue();
        customerId.ValueKind.Should().Be(JsonValueKind.Null);
        // The block itself is still served: only the customer id is withheld.
        paddle.GetProperty("environment").GetString().Should().Be("sandbox");
        paddle.GetProperty("clientToken").GetString().Should().Be("test_publishable");
    }

    // ------------------------------------------------------------------ Security R-3: period drift

    /// <summary>
    /// Paddle's live view one period ahead of the row: the renewal (or reactivation) webhook has not
    /// landed, so the row's plan, seats and period are last period's. Same items, newer period.
    /// </summary>
    private async Task<ProviderSubscriptionState> PaddleIsAPeriodAhead(bool cancelScheduled = false)
    {
        await using InferenceApiDbContext db = NewDb();
        Subscription sub = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Provider != null && s.Status != SubscriptionStatus.Canceled);
        Plan plan = await db.Plans.AsNoTracking().SingleAsync(p => p.Id == sub.PlanId);
        DateTime nextStart = sub.PeriodEnd!.Value;
        return new ProviderSubscriptionState(
            "active",
            [new BillingLineItem(BillingEventProcessor.PriceIdsOf(plan, "fake").Single(), sub.Seats)],
            cancelScheduled,
            nextStart.AddMonths(1),
            PeriodStart: nextStart);
    }

    private static void ShouldBeChangePending(IActionResult result)
    {
        result.Should().BeOfType<ConflictObjectResult>();
        Json(result).GetProperty("error").GetString().Should().Be("change_pending");
        Json(result).GetProperty("message").GetString().Should().Be(ChangePendingMessage);
    }

    [Fact]
    public async Task ChangePlan_WhilePaddlesLivePeriodIsNewerThanTheRow_Is409ChangePending_AndSendsNothing()
    {
        await Subscribe();
        _provider.Live = await PaddleIsAPeriodAhead();

        ShouldBeChangePending(await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("team", 3), default));

        _provider.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task Seats_WhilePaddlesLivePeriodIsNewerThanTheRow_Is409ChangePending_AndSendsNothing()
    {
        await Subscribe("team");
        _provider.Live = await PaddleIsAPeriodAhead();

        ShouldBeChangePending(await Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(5), default));

        _provider.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task KeepMyPlan_WhilePaddlesLivePeriodIsNewerThanTheRow_Is409ChangePending_AndSendsNothing()
    {
        await Subscribe("team");
        Guid team;
        await using (InferenceApiDbContext db = NewDb())
        {
            team = (await db.Plans.SingleAsync(p => p.Key == "team")).Id;
        }

        await Mutate(s => { s.Seats = 5; s.ScheduledPlanId = team; s.ScheduledSeats = 3; });
        _provider.Live = await PaddleIsAPeriodAhead() with { Items = [new BillingLineItem("pri_team", 3)] };

        ShouldBeChangePending(await Controller(true, _org, OrganizationRole.Owner).ChangeSeats(new ChangeSeatsRequestDto(5), default));

        _provider.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_WhilePaddlesLivePeriodIsNewerThanTheRow_Is409ChangePending_AndSendsNothing()
    {
        await Subscribe();
        _provider.Live = await PaddleIsAPeriodAhead();

        ShouldBeChangePending(await Controller(true, _org, OrganizationRole.Owner).CancelSubscription(default));

        _provider.Cancels.Should().BeEmpty();
    }

    [Fact]
    public async Task Resume_WhilePaddlesLivePeriodIsNewerThanTheRow_Is409ChangePending_AndSendsNothing()
    {
        await Subscribe();
        await Mutate(s => s.CancelAtPeriodEnd = true);
        _provider.Live = await PaddleIsAPeriodAhead(cancelScheduled: true);

        ShouldBeChangePending(await Controller(true, _org, OrganizationRole.Owner).ResumeSubscription(default));

        _provider.Resumes.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangePlan_WhenPaddlesLivePeriodMatchesTheRow_StillProceeds()
    {
        await Subscribe();

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner).ChangePlan(new ChangePlanRequestDto("team", 3), default);

        result.Should().BeOfType<AcceptedResult>();
        _provider.PlanChanges.Should().ContainSingle();
    }

    // ------------------------------------------------------------------ provider selection (stripe-billing-provider T-002)

    private static BillingProviderOptions Select(string name) => new() { Provider = name };

    [Fact]
    public async Task Checkout_UsesTheActiveProvider_AndCancelOnAnExistingRowUsesTheRowsProvider()
    {
        // The org subscribed through "fake" (the row's provider); "stripe" is the active one for new business.
        await Subscribe();
        FakeProvider stripe = new("stripe");
        IReadOnlyList<IBillingProvider> both = [_provider, stripe];

        IActionResult checkout = await Controller(true, _org, OrganizationRole.Owner, providers: both, selection: Select("Stripe"))
            .Checkout(new CheckoutRequestDto(null, null, 500), default);
        IActionResult cancel = await Controller(true, _org, OrganizationRole.Owner, providers: both, selection: Select("Stripe"))
            .CancelSubscription(default);

        checkout.Should().BeOfType<OkObjectResult>();
        stripe.LastCheckout.Should().NotBeNull();
        _provider.LastCheckout.Should().BeNull();
        cancel.Should().BeOfType<AcceptedResult>();
        _provider.Cancels.Should().ContainSingle().Which.Should().Be("sub_1");
        stripe.Cancels.Should().BeEmpty();
    }

    [Fact]
    public async Task Summary_NamesTheActiveProvider_AndItsDisplayName()
    {
        FakeProvider stripe = new("stripe") { DisplayName = "Stripe" };

        IActionResult result = await Controller(true, _org, OrganizationRole.Owner, providers: [_provider, stripe], selection: Select("stripe"))
            .Summary(default);

        JsonElement json = Json(result);
        json.GetProperty("billingProvider").GetString().Should().Be("stripe");
        json.GetProperty("billingProviderDisplayName").GetString().Should().Be("Stripe");
    }

    [Fact]
    public async Task SelectingAProviderThatIsNotRegistered_IsNoConfiguredProvider()
    {
        IActionResult result = await Controller(true, _org, OrganizationRole.Owner, selection: Select("Stripe"))
            .Checkout(new CheckoutRequestDto("creator", null, null), default);

        ((ObjectResult)result).StatusCode.Should().Be(503);
        Json(result).GetProperty("error").GetString().Should().Be("billing_not_configured");
    }

    [Fact]
    public async Task Webhook_CallsResolveBeforeProcessing_AndAFailureIs500Material_RecordingNothing()
    {
        string body = EventBody("e_resolve", _org);
        _provider.ResolveFail = new HttpRequestException("provider down");

        Func<Task> act = async () => await Controller(true, null, null, "ok", body).Webhook("fake", default);

        await act.Should().ThrowAsync<HttpRequestException>();
        _provider.Resolves.Should().Be(1);
        await using InferenceApiDbContext db = NewDb();
        (await db.BillingWebhookEvents.CountAsync()).Should().Be(0);

        _provider.ResolveFail = null;
        (await Controller(true, null, null, "ok", body).Webhook("fake", default)).Should().BeOfType<OkObjectResult>();
        _provider.Resolves.Should().Be(2);
    }

    [Fact]
    public void RefusalMessages_AreByteIdenticalForPaddle_AndNameTheOtherProvider()
    {
        BillingController.ProviderRefusalMessage("subscription_not_active", "Paddle")
            .Should().Be(BillingController.ProviderRefusalMessage("subscription_not_active"));
        BillingController.ProviderRefusalMessage("unknown_code", "Paddle").Should().Be(BillingController.GenericProviderRefusalMessage);
        BillingController.ProviderRefusalMessage("unknown_code", "Stripe").Should().Be("Stripe refused this change. Try again later or open Manage in Stripe.");
    }

    [Fact]
    public async Task Preview_PassesTheRawMinorUnitsThrough_OnlyWhenTheProviderSendsThem()
    {
        FakeProvider withMinor = new("stripe") { Preview = new PricePreview("USD", "$29.00", "$0.00", "$29.00", SubtotalMinor: 2900, DiscountMinor: 0, TotalMinor: 2900, MinorUnitExponent: 2) };
        FakeProvider plain = new("plain") { Preview = new PricePreview("USD", "$29.00", "$0.00", "$29.00") };
        // Plan prices are looked up per provider name.
        await using (InferenceApiDbContext db = NewDb())
        {
            Plan creator = await db.Plans.SingleAsync(p => p.Key == "creator");
            creator.ProviderPriceIdsJson = """{"fake":{"monthly":"pri_creator"},"stripe":{"monthly":"price_s"},"plain":{"monthly":"price_p"}}""";
            await db.SaveChangesAsync();
        }

        JsonElement rich = Json(await Controller(true, _org, OrganizationRole.Owner, provider: withMinor)
            .Preview(new CheckoutPreviewRequestDto("creator", null, null, null), default));
        JsonElement bare = Json(await Controller(true, _org, OrganizationRole.Owner, provider: plain)
            .Preview(new CheckoutPreviewRequestDto("creator", null, null, null), default));

        rich.GetProperty("subtotalMinor").GetInt64().Should().Be(2900);
        rich.GetProperty("totalMinor").GetInt64().Should().Be(2900);
        rich.GetProperty("minorUnitExponent").GetInt32().Should().Be(2);
        rich.TryGetProperty("unitTotalMinor", out _).Should().BeFalse();
        bare.TryGetProperty("subtotalMinor", out _).Should().BeFalse();
        bare.TryGetProperty("minorUnitExponent", out _).Should().BeFalse();
        bare.GetProperty("total").GetString().Should().Be("$29.00");
    }
}
