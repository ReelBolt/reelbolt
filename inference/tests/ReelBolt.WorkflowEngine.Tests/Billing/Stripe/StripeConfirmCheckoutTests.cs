using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Billing.Stripe.StripePayloads;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

/// <summary>
/// POST /billing/checkout/confirm: the provider reads the finished Checkout Session back and the real processor applies the
/// same events the webhooks carry. Stripe's API is faked; Sqlite; the webhook path is replayed before and after.
/// </summary>
public sealed class StripeConfirmCheckoutTests : IDisposable
{
    private const string SubSession = "cs_test_sub";
    private const string TopUpSessionId = "cs_test_topup";
    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid OtherOrg = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private CheckoutConfirmLimiter _limiter = new();

    public StripeConfirmCheckoutTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = OrgId, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.Plans.Add(new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, PriceMonthlyUsd = 29, MonthlyCreditsMillis = 1_000_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"stripe":{"monthly":"price_creator"}}""" });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private sealed class User(Guid? org, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId => UserA;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => org;
        public OrganizationRole? OrgRole => role;
        public bool IsPlatformAdmin => false;
    }

    private sealed class SystemTenant : ITenantContext
    {
        public Guid? OrganizationId => null;
        public bool IsSystem => true;
        public IDisposable BeginSystemScope() => new Nop();
        public IDisposable BeginOrganizationScope(Guid organizationId) => new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }
    }

    private static JsonObject SubscriptionSession(string status = "complete", string paymentStatus = "no_payment_required", Guid? org = null) => new()
    {
        ["id"] = SubSession,
        ["object"] = "checkout.session",
        ["mode"] = "subscription",
        ["status"] = status,
        ["payment_status"] = paymentStatus,
        ["client_reference_id"] = (org ?? OrgId).ToString(),
        ["metadata"] = Metadata(org ?? OrgId),
        ["customer"] = "cus_1",
        ["subscription"] = "sub_1"
    };

    private static JsonObject SubWithInvoice()
    {
        JsonObject sub = Subscription(start: Now);
        sub["latest_invoice"] = "in_1";
        return sub;
    }

    private static StripeFake SubscriptionStripe(JsonObject? session = null, long amount = 0)
        => new StripeFake()
            .On("GET", $"/v1/checkout/sessions/{SubSession}", (session ?? SubscriptionSession()).ToJsonString())
            .On("GET", "/v1/subscriptions/sub_1", SubWithInvoice().ToJsonString())
            .On("GET", "/v1/invoices/in_1", Invoice("subscription_create", amount, amount > 0 ? [Line(amount, "price_creator", 1, Now)] : []).ToJsonString());

    private static StripeFake TopUpStripe(JsonObject? session = null)
        => new StripeFake()
            .On("GET", $"/v1/checkout/sessions/{TopUpSessionId}", (session ?? TopUpSessionWithId()).ToJsonString())
            .On("GET", "/v1/payment_intents/pi_1", new { id = "pi_1", latest_charge = new { balance_transaction = new { fee = 61 } } });

    private static JsonObject TopUpSessionWithId()
    {
        JsonObject s = TopUpSession(credits: 1000);
        s["id"] = TopUpSessionId;
        return s;
    }

    private BillingController Controller(StripeFake fake, Guid? org, OrganizationRole? role, bool enabled = true)
    {
        InferenceApiDbContext db = NewDb();
        StripeBillingProvider stripe = StripeKit.Provider(fake, StripeKit.Options());
        return new BillingController(
            db, new User(org, role), Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = enabled }),
            [stripe],
            new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, new StripeKit.FixedClock(Now)),
            new SystemTenant(), Microsoft.Extensions.Options.Options.Create(new PaddleOptions()), NullLogger<BillingController>.Instance,
            time: new StripeKit.FixedClock(Now),
            providerSelection: Microsoft.Extensions.Options.Options.Create(new BillingProviderOptions { Provider = "stripe" }),
            confirmLimiter: _limiter);
    }

    private async Task<IActionResult> Confirm(StripeFake fake, string? sessionId, Guid? org = null, OrganizationRole? role = OrganizationRole.Owner)
        => await Controller(fake, org ?? OrgId, role).ConfirmCheckout(new ConfirmCheckoutDto(sessionId), default);

    private async Task<IActionResult> Webhook(StripeFake fake, string body)
    {
        BillingController c = Controller(fake, null, null);
        DefaultHttpContext http = new();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        http.Request.Headers[StripeSignature.HeaderName] = StripeSignature.Sign(body, Secret, Unix(Now));
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return await c.Webhook("stripe", default);
    }

    private static string Json(IActionResult result, int status)
    {
        ObjectResult o = result.Should().BeAssignableTo<ObjectResult>().Subject;
        (o.StatusCode ?? 200).Should().Be(status);
        return System.Text.Json.JsonSerializer.Serialize(o.Value);
    }

    private async Task<(long Balance, int Payments, int Subs, int Redemptions, string State)> Snapshot()
    {
        await using InferenceApiDbContext db = NewDb();
        long balance = (await new CreditLedger(db).GetBalanceAsync(OrgId)).BalanceMillis;
        string state = string.Join(',', await db.Subscriptions.Select(s => s.Status + ":" + s.PeriodEnd).ToListAsync());
        return (balance, await db.BillingPayments.CountAsync(), await db.Subscriptions.CountAsync(), await db.PromotionRedemptions.CountAsync(), state);
    }

    // ---- provider level

    [Fact]
    public async Task Provider_ReadsTheExactPaths_AndBuildsConfirmEventsInOrder()
    {
        StripeFake fake = SubscriptionStripe(amount: 2900);

        CheckoutConfirmation c = await StripeKit.Provider(fake).ConfirmCheckoutAsync(SubSession, OrgId, default);

        c.Status.Should().Be(CheckoutConfirmationStatus.Confirmed);
        c.Kind.Should().Be("subscription");
        c.Events.Select(e => e.Kind).Should().Equal(BillingEventKind.SubscriptionChanged, BillingEventKind.TransactionCompleted);
        c.Events.Should().OnlyContain(e => e.Provider == "stripe" && e.EventId.StartsWith("confirm:", StringComparison.Ordinal));
        c.Events[1].EventId.Should().Be("confirm:in_1");
        c.Events[1].CheckoutId.Should().Be(SubSession);
        fake.Calls.Select(x => x.Path).Should().Contain(["/v1/checkout/sessions/cs_test_sub", "/v1/subscriptions/sub_1", "/v1/invoices/in_1"]);
        fake.Calls.Should().OnlyContain(x => x.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Provider_EventIdsAreDeterministic_SoARepeatDedupes()
    {
        CheckoutConfirmation a = await StripeKit.Provider(SubscriptionStripe(amount: 2900)).ConfirmCheckoutAsync(SubSession, OrgId, default);
        CheckoutConfirmation b = await StripeKit.Provider(SubscriptionStripe(amount: 2900)).ConfirmCheckoutAsync(SubSession, OrgId, default);

        a.Events.Select(e => e.EventId).Should().Equal(b.Events.Select(e => e.EventId));
    }

    [Fact]
    public async Task Provider_AnotherOrganizationsSession_IsNotFound_WithoutReadingTheSubscription()
    {
        StripeFake fake = SubscriptionStripe(SubscriptionSession(org: OtherOrg));

        CheckoutConfirmation c = await StripeKit.Provider(fake).ConfirmCheckoutAsync(SubSession, OrgId, default);

        c.Status.Should().Be(CheckoutConfirmationStatus.NotFound);
        c.Events.Should().BeEmpty();
        fake.To("/v1/subscriptions/sub_1").Should().BeEmpty();
    }

    [Fact]
    public async Task Provider_AnUnknownSession_IsNotFound()
    {
        CheckoutConfirmation c = await StripeKit.Provider(new StripeFake()).ConfirmCheckoutAsync("cs_nope", OrgId, default);

        c.Status.Should().Be(CheckoutConfirmationStatus.NotFound);
    }

    [Theory]
    [InlineData("open", "unpaid", CheckoutConfirmationStatus.Pending)]
    [InlineData("complete", "unpaid", CheckoutConfirmationStatus.Pending)]
    [InlineData("expired", "unpaid", CheckoutConfirmationStatus.Abandoned)]
    [InlineData("complete", "paid", CheckoutConfirmationStatus.Confirmed)]
    [InlineData("complete", "no_payment_required", CheckoutConfirmationStatus.Confirmed)]
    public async Task Provider_MapsEachSessionStatus(string status, string paymentStatus, CheckoutConfirmationStatus expected)
    {
        StripeFake fake = SubscriptionStripe(SubscriptionSession(status, paymentStatus));

        CheckoutConfirmation c = await StripeKit.Provider(fake).ConfirmCheckoutAsync(SubSession, OrgId, default);

        c.Status.Should().Be(expected);
        if (expected != CheckoutConfirmationStatus.Confirmed)
        {
            c.Events.Should().BeEmpty();
            fake.To("/v1/subscriptions/sub_1").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Provider_ClientReferenceIdAlone_IsNotFound_BuyersCanSetItOnAPaymentLink()
    {
        JsonObject session = SubscriptionSession();
        session["metadata"] = new JsonObject();
        StripeFake fake = SubscriptionStripe(session);

        CheckoutConfirmation c = await StripeKit.Provider(fake).ConfirmCheckoutAsync(SubSession, OrgId, default);

        c.Status.Should().Be(CheckoutConfirmationStatus.NotFound);
        fake.To("/v1/subscriptions/sub_1").Should().BeEmpty();
    }

    [Fact]
    public async Task Provider_AnEventResolvedToAnotherOrganization_IsNotFound()
    {
        JsonObject sub = SubWithInvoice();
        sub["metadata"] = Metadata(OtherOrg);
        JsonObject invoice = Invoice("subscription_create", 0);
        invoice["parent"]!["subscription_details"]!["metadata"] = Metadata(OtherOrg);
        StripeFake fake = new StripeFake()
            .On("GET", $"/v1/checkout/sessions/{SubSession}", SubscriptionSession().ToJsonString())
            .On("GET", "/v1/subscriptions/sub_1", sub.ToJsonString())
            .On("GET", "/v1/invoices/in_1", invoice.ToJsonString());

        CheckoutConfirmation c = await StripeKit.Provider(fake).ConfirmCheckoutAsync(SubSession, OrgId, default);

        c.Status.Should().Be(CheckoutConfirmationStatus.NotFound);
        c.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Provider_TheSessionsOwnInvoice_IsUsedWhenPresent()
    {
        JsonObject session = SubscriptionSession();
        session["invoice"] = "in_1";
        JsonObject sub = Subscription(start: Now);
        sub["latest_invoice"] = "in_renewal";
        StripeFake fake = new StripeFake()
            .On("GET", $"/v1/checkout/sessions/{SubSession}", session.ToJsonString())
            .On("GET", "/v1/subscriptions/sub_1", sub.ToJsonString())
            .On("GET", "/v1/invoices/in_1", Invoice("subscription_create", 0).ToJsonString());

        CheckoutConfirmation c = await StripeKit.Provider(fake).ConfirmCheckoutAsync(SubSession, OrgId, default);

        c.Events.Select(e => e.EventId).Should().Contain("confirm:in_1");
        fake.To("/v1/invoices/in_renewal").Should().BeEmpty();
    }

    [Fact]
    public async Task Provider_ARenewalLatestInvoice_IsNeverAttachedToTheCheckout()
    {
        JsonObject renewal = Invoice("subscription_cycle", 2900, Line(2900, "price_creator", 1, Now));
        renewal["id"] = "in_1";
        StripeFake fake = new StripeFake()
            .On("GET", $"/v1/checkout/sessions/{SubSession}", SubscriptionSession().ToJsonString())
            .On("GET", "/v1/subscriptions/sub_1", SubWithInvoice().ToJsonString())
            .On("GET", "/v1/invoices/in_1", renewal.ToJsonString());

        CheckoutConfirmation c = await StripeKit.Provider(fake).ConfirmCheckoutAsync(SubSession, OrgId, default);

        c.Events.Select(e => e.Kind).Should().Equal(BillingEventKind.SubscriptionChanged);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("draft")]
    [InlineData("uncollectible")]
    public async Task Provider_AnInvoiceThatIsNotPaid_EmitsNoInvoiceEvent(string status)
    {
        JsonObject invoice = Invoice("subscription_create", 2900, Line(2900, "price_creator", 1, Now));
        invoice["status"] = status;
        StripeFake fake = new StripeFake()
            .On("GET", $"/v1/checkout/sessions/{SubSession}", SubscriptionSession().ToJsonString())
            .On("GET", "/v1/subscriptions/sub_1", SubWithInvoice().ToJsonString())
            .On("GET", "/v1/invoices/in_1", invoice.ToJsonString());

        CheckoutConfirmation c = await StripeKit.Provider(fake).ConfirmCheckoutAsync(SubSession, OrgId, default);

        c.Events.Select(e => e.Kind).Should().Equal(BillingEventKind.SubscriptionChanged);
    }

    [Fact]
    public async Task Provider_AStripeOutage_IsAnHttpRequestException()
    {
        StripeFake fake = new StripeFake().Error("GET", $"/v1/checkout/sessions/{SubSession}", 500, "api_error");

        Func<Task> act = () => StripeKit.Provider(fake, delays: []).ConfirmCheckoutAsync(SubSession, OrgId, default);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ParseVerified_IsTheSameMappingAsTheSignedPath()
    {
        string body = Envelope("customer.subscription.updated", Subscription(start: Now), id: "evt_x");
        StripeEventParser parser = new(Options(), new StripeKit.FixedClock(Now));

        BillingEvent signed = parser.Parse(body, Signed(body));
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
        BillingEvent verified = parser.ParseVerified(doc.RootElement);

        verified.Should().BeEquivalentTo(signed);
    }

    // ---- controller level

    [Fact]
    public async Task ZeroDollarCouponCheckout_Confirms_ActivatesTheSubscription_AndGrantsPlanCreditsOnce()
    {
        StripeFake fake = SubscriptionStripe();

        string first = Json(await Confirm(fake, SubSession), 200);
        (long balance, int payments, int subs, _, _) = await Snapshot();
        string second = Json(await Confirm(fake, SubSession), 200);
        var after = await Snapshot();

        first.Should().Contain("\"confirmed\"").And.Contain("\"subscription\"");
        second.Should().Be(first);
        subs.Should().Be(1);
        balance.Should().Be(1_000_000);
        after.Balance.Should().Be(balance);
        after.Payments.Should().Be(payments);
        after.Subs.Should().Be(1);
        await using InferenceApiDbContext db = NewDb();
        (await db.Subscriptions.SingleAsync()).Status.Should().Be(SubscriptionStatus.Active);
        (await db.BillingWebhookEvents.ToListAsync()).Should().OnlyContain(e => e.Provider == "stripe" && e.EventId.StartsWith("confirm:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ZeroDollarCouponCheckout_RecordsThePromotionRedemptionOnce_AndClosesTheReservation()
    {
        Guid promo = Guid.NewGuid();
        Guid buyer = Guid.NewGuid();
        await using (InferenceApiDbContext seed = NewDb())
        {
            seed.Promotions.Add(new Promotion
            {
                Id = promo,
                Kind = PromotionKind.PlanDiscount,
                Name = "100",
                Status = PromotionStatus.Active,
                CreatedByUserId = buyer,
                DiscountType = PromotionDiscountType.Percentage,
                DiscountAmount = "100",
                DurationKind = PromotionDuration.Once,
                ProviderDiscountId = "rb_promo_100"
            });
            seed.PromotionCheckouts.Add(new PromotionCheckout
            {
                Id = Guid.NewGuid(),
                ProviderTransactionId = SubSession,
                OrganizationId = OrgId,
                UserId = buyer,
                DiscountPromotionId = promo,
                PriceId = "price_creator",
                CreatedAt = Now,
                WithinCapsAtReservation = true
            });
            await seed.SaveChangesAsync();
        }

        JsonObject invoice = Invoice("subscription_create", 0);
        invoice["discounts"] = new JsonArray("rb_promo_100");
        invoice["total_discount_amounts"] = new JsonArray(new JsonObject { ["amount"] = 2900 });
        StripeFake fake = new StripeFake()
            .On("GET", $"/v1/checkout/sessions/{SubSession}", SubscriptionSession().ToJsonString())
            .On("GET", "/v1/subscriptions/sub_1", SubWithInvoice().ToJsonString())
            .On("GET", "/v1/invoices/in_1", invoice.ToJsonString());

        Json(await Confirm(fake, SubSession), 200).Should().Contain("confirmed");
        Json(await Confirm(fake, SubSession), 200).Should().Contain("confirmed");

        await using InferenceApiDbContext check = NewDb();
        (await check.PromotionRedemptions.CountAsync(r => r.PromotionId == promo)).Should().Be(1);
        (await check.PromotionCheckouts.SingleAsync()).CompletedAt.Should().NotBeNull();
        (await Snapshot()).Balance.Should().Be(1_000_000);
    }

    private async Task<string[]> RealWebhooks(StripeFake fake, long amount)
    {
        JsonObject invoice = Invoice("subscription_create", amount, amount > 0 ? [Line(amount, "price_creator", 1, Now)] : []);
        string[] outcomes =
        [
            Json(await Webhook(fake, Envelope("customer.subscription.created", SubWithInvoice(), id: "evt_s")), 200),
            Json(await Webhook(fake, Envelope("invoice.paid", invoice, id: "evt_i")), 200),
            Json(await Webhook(fake, Envelope("checkout.session.completed", SubscriptionSession("complete", amount > 0 ? "paid" : "no_payment_required"), id: "evt_c")), 200)
        ];
        return outcomes;
    }

    [Theory]
    [InlineData(2900)]
    [InlineData(0)]
    public async Task ConfirmThenTheRealWebhooks_ChangesNothing(long amount)
    {
        StripeFake fake = SubscriptionStripe(amount: amount);
        Json(await Confirm(fake, SubSession), 200).Should().Contain("\"confirmed\"");
        var before = await Snapshot();

        string[] outcomes = await RealWebhooks(fake, amount);

        outcomes.Should().OnlyContain(o => o.Contains("Applied") || o.Contains("Stale") || o.Contains("Duplicate"));
        outcomes.Should().NotContain(o => o.Contains("Unresolved") || o.Contains("invalid_webhook"));
        (await Snapshot()).Should().Be(before);
        before.Balance.Should().Be(1_000_000);
        before.Subs.Should().Be(1);
        await using InferenceApiDbContext db = NewDb();
        (await db.BillingWebhookEvents.CountAsync(e => e.EventId.StartsWith("evt_"))).Should().Be(3);
    }

    [Theory]
    [InlineData(2900)]
    [InlineData(0)]
    public async Task TheRealWebhooksThenConfirm_ChangesNothing(long amount)
    {
        StripeFake fake = SubscriptionStripe(amount: amount);
        string[] outcomes = await RealWebhooks(fake, amount);
        var before = await Snapshot();

        Json(await Confirm(fake, SubSession), 200).Should().Contain("\"confirmed\"");

        outcomes.Should().NotContain(o => o.Contains("Unresolved") || o.Contains("invalid_webhook"));
        (await Snapshot()).Should().Be(before);
        before.Balance.Should().Be(1_000_000);
        before.Subs.Should().Be(1);
    }

    [Fact]
    public async Task AllIgnoredEvents_ArePending_NotConfirmed()
    {
        // A $0 payment-mode top-up of a pack nobody configured: the event is Ignored (topup_pack_not_configured).
        JsonObject session = TopUpSessionWithId();
        session["metadata"]!["topup_credits"] = "7";
        StripeFake fake = TopUpStripe(session);

        string body = Json(await Confirm(fake, TopUpSessionId), 200);

        body.Should().Contain("\"pending\"");
        (await Snapshot()).Balance.Should().Be(0);
    }

    [Fact]
    public async Task AnUnresolvedEvent_IsPending_NotConfirmed()
    {
        // The organization no longer exists: the top-up is recorded but not actionable.
        StripeFake fake = TopUpStripe();
        await using (InferenceApiDbContext db = NewDb())
        {
            db.Organizations.RemoveRange(db.Organizations);
            await db.SaveChangesAsync();
        }

        Json(await Confirm(fake, TopUpSessionId), 200).Should().Contain("\"pending\"");
    }

    [Fact]
    public async Task ASecondConfirm_OfAnAlreadyProcessedCheckout_IsConfirmed_FromTheDuplicateRows()
    {
        StripeFake fake = SubscriptionStripe(amount: 2900);
        await Confirm(fake, SubSession);
        // A fresh limiter: no cached answer, so the events are processed again and come back Duplicate.
        _limiter = new CheckoutConfirmLimiter();

        Json(await Confirm(fake, SubSession), 200).Should().Contain("\"confirmed\"");
    }

    [Fact]
    public async Task ASettledAnswer_IsReplayedFromTheCache_WithoutAnyStripeCall()
    {
        StripeFake fake = SubscriptionStripe(amount: 2900);
        Json(await Confirm(fake, SubSession), 200).Should().Contain("\"confirmed\"");
        int calls = fake.Calls.Count;

        Json(await Confirm(fake, SubSession), 200).Should().Contain("\"confirmed\"");

        fake.Calls.Count.Should().Be(calls);
    }

    [Fact]
    public async Task APendingAnswer_IsNeverCached()
    {
        StripeFake fake = SubscriptionStripe(SubscriptionSession("open", "unpaid"));
        await Confirm(fake, SubSession);
        int calls = fake.Calls.Count;

        await Confirm(fake, SubSession);

        fake.Calls.Count.Should().BeGreaterThan(calls);
    }

    [Fact]
    public void TheLimiter_CapsPerUser_Globally_AndEvictsIdleEntries_AndExpiresResults()
    {
        CheckoutConfirmLimiter l = new();
        for (int i = 0; i < 10; i++) l.IsLimited(Guid.NewGuid(), Now, UserA).Should().BeFalse();
        l.IsLimited(Guid.NewGuid(), Now, UserA).Should().BeTrue("the same user across many organizations");
        l.IsLimited(Guid.NewGuid(), Now, Guid.NewGuid()).Should().BeFalse();

        CheckoutConfirmLimiter g = new();
        for (int i = 0; i < 120; i++) g.IsLimited(Guid.NewGuid(), Now, Guid.NewGuid()).Should().BeFalse();
        g.IsLimited(Guid.NewGuid(), Now, Guid.NewGuid()).Should().BeTrue("the process-wide cap");
        g.IsLimited(Guid.NewGuid(), Now.AddMinutes(1), Guid.NewGuid()).Should().BeFalse();

        CheckoutConfirmLimiter e = new();
        Guid org = Guid.NewGuid();
        e.IsLimited(org, Now, UserA);
        e.StoreResult(org, "cs_x", "confirmed", "subscription", Now);
        e.TryGetResult(org, "cs_x", Now.AddSeconds(59), out _).Should().BeTrue();
        e.TryGetResult(org, "cs_x", Now.AddSeconds(61), out _).Should().BeFalse();
        e.StoreResult(org, "cs_p", "pending", "subscription", Now);
        e.TryGetResult(org, "cs_p", Now, out _).Should().BeFalse();
        e.IsLimited(Guid.NewGuid(), Now.AddMinutes(5), null);
        e.TrackedEntries.Should().Be(1, "only the new organization remains");
    }

    [Fact]
    public async Task TopUpSession_GrantsCreditsOnce_AndIsKindTopUp()
    {
        StripeFake fake = TopUpStripe();

        string first = Json(await Confirm(fake, TopUpSessionId), 200);
        await Confirm(fake, TopUpSessionId);
        await Webhook(fake, Envelope("checkout.session.completed", TopUpSession(credits: 1000), id: "evt_t"));

        first.Should().Contain("\"confirmed\"").And.Contain("\"topUp\"");
        (await Snapshot()).Balance.Should().Be(1_000_000);
        (await Snapshot()).Payments.Should().Be(1);
    }

    [Fact]
    public async Task AnOpenSession_IsPending_AndNothingIsProcessed()
    {
        StripeFake fake = SubscriptionStripe(SubscriptionSession("open", "unpaid"));

        Json(await Confirm(fake, SubSession), 200).Should().Contain("\"pending\"");

        await using InferenceApiDbContext db = NewDb();
        (await db.BillingWebhookEvents.CountAsync()).Should().Be(0);
        (await Snapshot()).Balance.Should().Be(0);
    }

    [Fact]
    public async Task AnExpiredSession_IsAbandoned()
    {
        Json(await Confirm(SubscriptionStripe(SubscriptionSession("expired", "unpaid")), SubSession), 200).Should().Contain("\"abandoned\"");
    }

    [Fact]
    public async Task AnotherOrganizationsSession_IsA404_IdenticalToAnUnknownOne()
    {
        string other = Json(await Confirm(SubscriptionStripe(SubscriptionSession(org: OtherOrg)), SubSession), 404);
        string unknown = Json(await Confirm(new StripeFake(), "cs_unknown"), 404);

        other.Should().Be(unknown).And.Contain("not_found");
        (await Snapshot()).Balance.Should().Be(0);
    }

    [Fact]
    public async Task TheCallersOrganization_NeverTheBody_DecidesOwnership()
    {
        // Same session, but the caller is the other organization: 404 even though the body names nothing else.
        Json(await Confirm(SubscriptionStripe(), SubSession, org: OtherOrg), 404).Should().Contain("not_found");
    }

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(OrganizationRole.Admin)]
    public async Task ANonManagerRole_Is403(OrganizationRole role)
    {
        (await Confirm(SubscriptionStripe(), SubSession, role: role)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task TheBillingRole_IsAllowed()
    {
        Json(await Confirm(SubscriptionStripe(), SubSession, role: OrganizationRole.Billing), 200).Should().Contain("confirmed");
    }

    [Fact]
    public async Task BillingDisabled_Is404()
    {
        (await Controller(SubscriptionStripe(), OrgId, OrganizationRole.Owner, enabled: false).ConfirmCheckout(new ConfirmCheckoutDto(SubSession), default))
            .Should().BeOfType<NotFoundResult>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc_123")]
    [InlineData("cs_bad-char")]
    [InlineData("cs_has space")]
    public async Task AMissingOrMalformedId_Is400_invalid_session(string? id)
    {
        Json(await Confirm(new StripeFake(), id), 400).Should().Contain("invalid_session");
    }

    [Fact]
    public async Task AnOverlongId_Is400()
    {
        Json(await Confirm(new StripeFake(), "cs_" + new string('a', 200)), 400).Should().Contain("invalid_session");
    }

    [Fact]
    public async Task APaddleOwnedId_IsA409_confirm_unsupported()
    {
        InferenceApiDbContext db = NewDb();
        BillingController c = new(
            db, new User(OrgId, OrganizationRole.Owner), Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = true }),
            [ReelBolt.WorkflowEngine.Tests.Billing.PaddlePayloads.Provider(), StripeKit.Provider(new StripeFake(), StripeKit.Options())],
            new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, new StripeKit.FixedClock(Now)),
            new SystemTenant(), Microsoft.Extensions.Options.Options.Create(new PaddleOptions()), NullLogger<BillingController>.Instance,
            confirmLimiter: _limiter);

        Json(await c.ConfirmCheckout(new ConfirmCheckoutDto("txn_01abc"), default), 409).Should().Contain("confirm_unsupported");
    }

    [Fact]
    public async Task AProviderOutage_Is503_provider_unavailable_WithoutStripeDetails()
    {
        StripeFake fake = new StripeFake().Error("GET", $"/v1/checkout/sessions/{SubSession}", 500, "api_error", message: "secret internal stripe detail");

        string body = Json(await Confirm(fake, SubSession), 503);

        body.Should().Contain("provider_unavailable").And.NotContain("secret internal").And.NotContain(StripeKit.ApiKey);
    }

    [Fact]
    public async Task AStripeRefusal_NeverEchoesTheStripeErrorBody()
    {
        StripeFake fake = new StripeFake().Error("GET", $"/v1/checkout/sessions/{SubSession}", 403, "permission_denied", message: "secret internal stripe detail");

        string body = Json(await Confirm(fake, SubSession), 503);

        body.Should().NotContain("secret internal").And.NotContain("permission_denied");
    }

    [Fact]
    public async Task MoreThanTenCallsPerOrganizationPerMinute_Is429_rate_limited()
    {
        StripeFake fake = SubscriptionStripe(SubscriptionSession("open", "unpaid"));

        for (int i = 0; i < 10; i++) Json(await Confirm(fake, SubSession), 200);

        Json(await Confirm(fake, SubSession), 429).Should().Contain("rate_limited");
        // another organization has its own window
        _limiter.IsLimited(OtherOrg, Now).Should().BeFalse();
    }
}
