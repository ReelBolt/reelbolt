using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Billing.Stripe.StripeKit;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

public sealed class StripeBillingProviderTests
{
    private static readonly Guid Org = StripePayloads.OrgId;
    private static readonly Guid User = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Plan Creator => new() { Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, ProviderPriceIdsJson = """{"stripe":{"monthly":"price_creator"}}""" };
    private static Plan Team => new() { Key = "team", Name = "Team", Kind = PlanKind.PerSeat, MinSeats = 3, ProviderPriceIdsJson = """{"stripe":{"monthly":"price_team"}}""" };

    private static StripeFake CheckoutFake() => new StripeFake()
        .On("POST", "/v1/customers", new { id = "cus_new" })
        .On("POST", "/v1/checkout/sessions", new { id = "cs_test_1", url = "https://checkout.stripe.com/c/pay/cs_test_1" });

    // ------------------------------------------------------------------ identity

    [Fact]
    public void Identity_ComesFromOptions()
    {
        StripeBillingProvider p = Provider(new StripeFake());

        p.Name.Should().Be("stripe");
        p.DisplayName.Should().Be("Stripe");
        p.IsConfigured.Should().BeTrue();
        p.MaxSelfServeSeats.Should().Be(200);
        p.TopUpPacks.Should().Equal(1000, 5000);
        p.TopUpPriceId(1000).Should().Be("price_topup_1000");
        p.TopUpPriceId(42).Should().BeNull();
        Provider(new StripeFake(), new StripeOptions()).IsConfigured.Should().BeFalse();
    }

    // ------------------------------------------------------------------ checkout

    [Fact]
    public async Task Checkout_Subscription_CreatesTheCustomerThenTheSession_WithExactFields()
    {
        StripeFake fake = CheckoutFake();
        Guid promo = Guid.NewGuid();

        CheckoutSession s = await Provider(fake).CreateCheckoutAsync(
            new CheckoutRequest(Org, User, "a@b.test", Team, 5, null, null, DiscountId: "rb_founder", PromotionId: promo), default);

        s.Should().Be(new CheckoutSession("https://checkout.stripe.com/c/pay/cs_test_1", "cs_test_1", "cus_new"));
        StripeCall customer = fake.To("/v1/customers").Single();
        customer.F("email").Should().Be("a@b.test");
        customer.F("metadata[organization_id]").Should().Be(Org.ToString());
        customer.IdempotencyKey.Should().Be($"customer:{Org}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("a@b.test")))[..16].ToLowerInvariant()}");
        customer.IdempotencyKey.Should().NotContain("a@b.test", "the raw email never goes into a key");

        StripeCall c = fake.To("/v1/checkout/sessions").Single();
        c.IdempotencyKey.Should().BeNull("a user may retry a checkout deliberately");
        c.Authorization.Should().Be($"Bearer {ApiKey}");
        c.Version.Should().Be("2026-09-30.endive");
        c.F("mode").Should().Be("subscription");
        c.F("ui_mode").Should().Be("hosted_page");
        c.F("integration_identifier").Should().Be("reelbolt-checkout-qxmvbtrk");
        c.F("customer").Should().Be("cus_new");
        c.HasF("customer_email").Should().BeFalse();
        c.F("client_reference_id").Should().Be(Org.ToString());
        c.F("line_items[0][price]").Should().Be("price_team");
        c.F("line_items[0][quantity]").Should().Be("5");
        c.F("success_url").Should().Be("https://app.example.com/app/checkout?session_id={CHECKOUT_SESSION_ID}");
        c.F("cancel_url").Should().Be("https://app.example.com/app/settings/billing");
        c.F("discounts[0][coupon]").Should().Be("rb_founder");
        foreach (string prefix in new[] { "metadata", "subscription_data[metadata]" })
        {
            c.F($"{prefix}[organization_id]").Should().Be(Org.ToString());
            c.F($"{prefix}[user_id]").Should().Be(User.ToString());
            c.F($"{prefix}[promotion_id]").Should().Be(promo.ToString());
            c.HasF($"{prefix}[bonus_promotion_id]").Should().BeFalse();
            c.HasF($"{prefix}[topup_credits]").Should().BeFalse();
        }

        c.HasF("payment_intent_data[metadata][organization_id]").Should().BeFalse();
        c.HasF("payment_method_types[]").Should().BeFalse();
        c.FKeys.Should().NotContain(k => k.StartsWith("payment_method_types", StringComparison.Ordinal));
        c.HasF("allow_promotion_codes").Should().BeFalse();
        c.F("automatic_tax[enabled]").Should().Be("true");
        c.F("customer_update[address]").Should().Be("auto", "automatic tax needs the address saved back to the customer");
        c.F("customer_update[name]").Should().Be("auto");
        c.HasF("managed_payments[enabled]").Should().BeFalse();
    }

    [Fact]
    public async Task Checkout_WithoutReturnBaseUrl_IsA503NotConfigured_NotAnInvalidOperation()
    {
        StripeOptions o = Options();
        o.ReturnBaseUrl = null;
        StripeBillingProvider p = Provider(CheckoutFake(), o);

        BillingProviderException ex = (await p.Invoking(x => x.CreateCheckoutAsync(new CheckoutRequest(Org, User, null, Creator, 1, null, "cus_1"), default))
            .Should().ThrowAsync<BillingProviderException>()).Which;
        ex.Status.Should().Be(503);
        ex.Code.Should().Be("stripe_not_configured");

        BillingProviderException portal = (await p.Invoking(x => x.CreatePortalSessionAsync("cus_1", null, default))
            .Should().ThrowAsync<BillingProviderException>()).Which;
        portal.Status.Should().Be(503);
    }

    [Fact]
    public async Task Checkout_NonSeatPlan_UsesQuantityOne_AndPromotionCodeIdsGoToPromotionCode()
    {
        StripeFake fake = CheckoutFake();

        await Provider(fake).CreateCheckoutAsync(new CheckoutRequest(Org, User, null, Creator, 7, null, "cus_1", DiscountId: "promo_ABC"), default);

        fake.To("/v1/customers").Should().BeEmpty("the org already has a customer");
        StripeCall c = fake.To("/v1/checkout/sessions").Single();
        c.F("customer").Should().Be("cus_1");
        c.F("line_items[0][quantity]").Should().Be("1");
        c.F("discounts[0][promotion_code]").Should().Be("promo_ABC");
        c.HasF("discounts[0][coupon]").Should().BeFalse();
    }

    [Fact]
    public async Task Checkout_SeatPlan_RaisesTooFewSeatsToTheMinimum()
    {
        StripeFake fake = CheckoutFake();

        await Provider(fake).CreateCheckoutAsync(new CheckoutRequest(Org, User, null, Team, 1, null, "cus_1"), default);

        fake.To("/v1/checkout/sessions").Single().F("line_items[0][quantity]").Should().Be("3");
    }

    [Fact]
    public async Task Checkout_TopUp_IsPaymentMode_WithPaymentIntentMetadata()
    {
        StripeFake fake = CheckoutFake();
        Guid bonus = Guid.NewGuid();

        await Provider(fake).CreateCheckoutAsync(new CheckoutRequest(Org, User, null, null, 0, 1000, "cus_1", BonusPromotionId: bonus), default);

        StripeCall c = fake.To("/v1/checkout/sessions").Single();
        c.F("mode").Should().Be("payment");
        c.F("line_items[0][price]").Should().Be("price_topup_1000");
        c.F("line_items[0][quantity]").Should().Be("1");
        foreach (string prefix in new[] { "metadata", "payment_intent_data[metadata]" })
        {
            c.F($"{prefix}[organization_id]").Should().Be(Org.ToString());
            c.F($"{prefix}[topup_credits]").Should().Be("1000");
            c.F($"{prefix}[bonus_promotion_id]").Should().Be(bonus.ToString());
        }

        c.HasF("subscription_data[metadata][organization_id]").Should().BeFalse();
        c.HasF("customer_creation").Should().BeFalse("a customer is always passed");
    }

    [Fact]
    public async Task Checkout_ManagedPayments_SendsTheFlag_AndOmitsTheForbiddenParameters()
    {
        StripeFake fake = CheckoutFake();

        await Provider(fake, Options(managedPayments: true)).CreateCheckoutAsync(new CheckoutRequest(Org, User, null, Creator, 1, null, "cus_1"), default);

        StripeCall c = fake.To("/v1/checkout/sessions").Single();
        c.F("managed_payments[enabled]").Should().Be("true");
        c.FKeys.Should().NotContain(k => k.StartsWith("automatic_tax", StringComparison.Ordinal)
            || k.StartsWith("tax_id_collection", StringComparison.Ordinal)
            || k.StartsWith("invoice_creation", StringComparison.Ordinal)
            || k.StartsWith("customer_update", StringComparison.Ordinal)
            || k.StartsWith("payment_method_types", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Checkout_UnknownPackOrMissingPrice_Throws()
    {
        StripeBillingProvider p = Provider(CheckoutFake());

        await p.Invoking(x => x.CreateCheckoutAsync(new CheckoutRequest(Org, User, null, null, 0, 777, "cus_1"), default)).Should().ThrowAsync<InvalidOperationException>();
        await p.Invoking(x => x.CreateCheckoutAsync(new CheckoutRequest(Org, User, null, new Plan { Key = "none", Name = "n", ProviderPriceIdsJson = "{}" }, 1, null, "cus_1"), default)).Should().ThrowAsync<InvalidOperationException>();
    }

    // ------------------------------------------------------------------ live subscription lookup

    [Fact]
    public async Task HasLive_PagesThrough_AndCountsOnlyLiveStatusesOfTheOrgOrUnnamed()
    {
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/subscriptions", new { has_more = true, data = new object[] { SubscriptionJson("canceled", org: Org.ToString()).with2("sub_a") } })
            .On("GET", "/v1/subscriptions", new { has_more = false, data = new object[] { SubscriptionJson("active", org: Guid.NewGuid().ToString()).with2("sub_b"), SubscriptionJson("past_due", org: Org.ToString()).with2("sub_c") } });

        bool live = await Provider(fake).HasLiveSubscriptionAsync("cus_1", Org, default);

        live.Should().BeTrue();
        StripeCall first = fake.Calls[0];
        first.Q("customer").Should().Be("cus_1");
        first.Q("status").Should().Be("all");
        first.Q("limit").Should().Be("100");
        first.Q("starting_after").Should().BeNull();
        fake.Calls[1].Q("starting_after").Should().Be("sub_a");
    }

    [Fact]
    public async Task FindLive_PrefersTheOneNamingTheOrg_ElseTheUnnamed_ElseNull()
    {
        object Page(params object[] subs) => new { has_more = false, data = subs };
        string other = Guid.NewGuid().ToString();

        StripeFake named = new StripeFake().On("GET", "/v1/subscriptions", Page(SubscriptionJson("active").with2("sub_unnamed"), SubscriptionJson("trialing", org: Org.ToString()).with2("sub_named")));
        StripeFake unnamed = new StripeFake().On("GET", "/v1/subscriptions", Page(SubscriptionJson("paused").with2("sub_unnamed")));
        StripeFake foreign = new StripeFake().On("GET", "/v1/subscriptions", Page(SubscriptionJson("active", org: other).with2("sub_x"), SubscriptionJson("canceled").with2("sub_y"), SubscriptionJson("incomplete_expired").with2("sub_z")));

        (await Provider(named).FindLiveSubscriptionIdAsync("cus_1", Org, default)).Should().Be("sub_named");
        (await Provider(unnamed).FindLiveSubscriptionIdAsync("cus_1", Org, default)).Should().Be("sub_unnamed");
        (await Provider(foreign).FindLiveSubscriptionIdAsync("cus_1", Org, default)).Should().BeNull();
        (await Provider(foreign).HasLiveSubscriptionAsync("cus_1", Org, default)).Should().BeFalse();
    }

    // ------------------------------------------------------------------ portal

    [Fact]
    public async Task Portal_MintsTheOverviewAndBothDeepLinks_WithTheConfiguration()
    {
        int n = 0;
        StripeFake fake = new();
        fake.On("POST", "/v1/billing_portal/sessions", new { url = "https://billing.stripe.com/s/overview" })
            .On("POST", "/v1/billing_portal/sessions", new { url = "https://billing.stripe.com/s/cancel" })
            .On("POST", "/v1/billing_portal/sessions", new { url = "https://billing.stripe.com/s/pm" });

        PortalSession s = await Provider(fake).CreatePortalSessionAsync("cus_1", "sub_1", default);

        s.Should().Be(new PortalSession("https://billing.stripe.com/s/overview", "https://billing.stripe.com/s/cancel", "https://billing.stripe.com/s/pm"));
        IReadOnlyList<StripeCall> calls = fake.To("/v1/billing_portal/sessions");
        calls.Should().HaveCount(3);
        foreach (StripeCall c in calls)
        {
            c.F("customer").Should().Be("cus_1");
            c.F("configuration").Should().Be("bpc_1");
            c.F("return_url").Should().Be("https://app.example.com/app/settings/billing");
            n++;
        }

        calls[0].HasF("flow_data[type]").Should().BeFalse();
        calls[1].F("flow_data[type]").Should().Be("subscription_cancel");
        calls[1].F("flow_data[subscription_cancel][subscription]").Should().Be("sub_1");
        calls[2].F("flow_data[type]").Should().Be("payment_method_update");
        n.Should().Be(3);
    }

    [Fact]
    public async Task Portal_WithoutASubscription_IsOnlyTheOverview_AndNoConfigurationWhenUnset()
    {
        StripeFake fake = new StripeFake().On("POST", "/v1/billing_portal/sessions", new { url = "https://billing.stripe.com/s/overview" });

        PortalSession s = await Provider(fake, Options(portalConfig: null)).CreatePortalSessionAsync("cus_1", null, default);

        s.Should().Be(new PortalSession("https://billing.stripe.com/s/overview", null, null));
        fake.Calls.Should().ContainSingle();
        fake.Calls[0].HasF("configuration").Should().BeFalse();
    }

    [Fact]
    public async Task Portal_ADeepLinkFailure_YieldsNullForThatLink_NeverFailsTheCall()
    {
        StripeFake fake = new StripeFake()
            .On("POST", "/v1/billing_portal/sessions", new { url = "https://billing.stripe.com/s/overview" })
            .Error("POST", "/v1/billing_portal/sessions", 400, "parameter_invalid_empty");
        fake.On("POST", "/v1/billing_portal/sessions", new { url = "https://billing.stripe.com/s/overview" });

        // overview ok, cancel link refused (400), payment link answers with the repeated last response (ok).
        PortalSession s = await Provider(fake).CreatePortalSessionAsync("cus_1", "sub_1", default);

        s.OverviewUrl.Should().Be("https://billing.stripe.com/s/overview");
        s.CancelUrl.Should().BeNull();
        s.UpdatePaymentMethodUrl.Should().NotBeNull();
    }

    // ------------------------------------------------------------------ cancel / resume

    [Fact]
    public async Task CancelAndResume_SetCancelAtPeriodEnd()
    {
        StripeFake fake = new StripeFake().On("POST", "/v1/subscriptions/sub_1", new { id = "sub_1" });
        StripeBillingProvider p = Provider(fake);

        await p.CancelSubscriptionAsync("sub_1", default);
        await p.ResumeSubscriptionAsync("sub_1", default);

        fake.Calls[0].F("cancel_at_period_end").Should().Be("true");
        fake.Calls[1].F("cancel_at_period_end").Should().Be("false");
    }

    // ------------------------------------------------------------------ change plan

    private static StripeFake SubscriptionFake(object? sub = null) => new StripeFake()
        .On("GET", "/v1/subscriptions/sub_1", sub ?? SubscriptionJson(price: "price_creator", quantity: 1))
        .On("POST", "/v1/subscriptions/sub_1", new { id = "sub_1" });

    [Fact]
    public async Task ChangePlan_Upgrade_UpdatesTheExistingItemByPosition_AndInvoicesNow()
    {
        StripeFake fake = SubscriptionFake();

        await Provider(fake).ChangePlanAsync("sub_1", [new BillingLineItem("price_pro", 2)], PlanChangeTiming.Immediately, PlanChangeDiscount.Unchanged, default);

        StripeCall get = fake.Calls[0];
        get.Method.Should().Be(HttpMethod.Get);
        get.Q("expand[]").Should().BeNull("nothing is expanded");
        StripeCall post = fake.Calls[1];
        post.F("items[0][id]").Should().Be("si_1");
        post.F("items[0][price]").Should().Be("price_pro");
        post.F("items[0][quantity]").Should().Be("2");
        post.F("proration_behavior").Should().Be("always_invoice");
        post.HasF("discounts").Should().BeFalse();
        post.F("payment_behavior").Should().Be("error_if_incomplete", "an unpaid upgrade must fail instead of moving the plan (DEC-6)");
    }

    [Fact]
    public async Task ChangePlan_Downgrade_IsProrationNone_AndRemoveClearsDiscountsWithAnEmptyString()
    {
        StripeFake fake = SubscriptionFake();

        await Provider(fake).ChangePlanAsync("sub_1", [new BillingLineItem("price_basic", 1)], PlanChangeTiming.NextBillingPeriod, PlanChangeDiscount.Remove, default);

        StripeCall post = fake.Calls[1];
        post.F("proration_behavior").Should().Be("none");
        post.HasF("payment_behavior").Should().BeFalse("a change from the next period charges nothing now");
        post.Form.Should().Contain(new KeyValuePair<string, string>("discounts", ""));
    }

    [Fact]
    public async Task ChangePlan_SurplusExistingItemsAreDeleted_SurplusNewItemsHaveNoId()
    {
        object twoItems = new
        {
            id = "sub_1",
            status = "active",
            items = new { data = new object[] { new { id = "si_a", price = new { id = "p_a" }, quantity = 1 }, new { id = "si_b", price = new { id = "p_b" }, quantity = 4 } } }
        };
        StripeFake shrink = SubscriptionFake(twoItems);
        await Provider(shrink).ChangePlanAsync("sub_1", [new BillingLineItem("p_a", 1)], PlanChangeTiming.NextBillingPeriod, PlanChangeDiscount.Unchanged, default);
        shrink.Calls[1].F("items[1][id]").Should().Be("si_b");
        shrink.Calls[1].F("items[1][deleted]").Should().Be("true");
        shrink.Calls[1].HasF("items[1][price]").Should().BeFalse();

        StripeFake grow = SubscriptionFake();
        await Provider(grow).ChangePlanAsync("sub_1", [new BillingLineItem("price_creator", 1), new BillingLineItem("price_addon", 3)], PlanChangeTiming.Immediately, PlanChangeDiscount.Unchanged, default);
        grow.Calls[1].HasF("items[1][id]").Should().BeFalse();
        grow.Calls[1].F("items[1][price]").Should().Be("price_addon");
        grow.Calls[1].F("items[1][quantity]").Should().Be("3");
    }

    [Fact]
    public async Task ChangePlan_IdempotencyKey_IsOneNoncePerCall_SharedByTheRetriesInsideIt()
    {
        BillingLineItem[] up = [new BillingLineItem("price_pro", 1)];
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/subscriptions/sub_1", SubscriptionJson(price: "price_creator"))
            .Error("POST", "/v1/subscriptions/sub_1", 500, "api_error")
            .On("POST", "/v1/subscriptions/sub_1", new { id = "sub_1" });

        await Provider(fake).ChangePlanAsync("sub_1", up, PlanChangeTiming.Immediately, PlanChangeDiscount.Unchanged, default);

        IReadOnlyList<StripeCall> posts = fake.Calls.Where(c => c.Method == HttpMethod.Post).ToList();
        posts.Should().HaveCount(2, "the 500 was retried");
        posts[0].IdempotencyKey.Should().StartWith("change:sub_1:").And.HaveLength("change:sub_1:".Length + 32);
        posts[1].IdempotencyKey.Should().Be(posts[0].IdempotencyKey, "the retry inside one call reuses the key");
    }

    [Fact]
    public async Task ChangePlan_TwoCalls_NeverShareAKey_NotEvenForTheSameChange_OrAnAbaRoundTrip()
    {
        BillingLineItem[] a = [new BillingLineItem("price_creator", 1)];
        BillingLineItem[] b = [new BillingLineItem("price_pro", 1)];
        List<string?> keys = [];
        // A -> B -> A -> B: each call sees the subscription as the previous call left it.
        foreach ((string current, BillingLineItem[] target) in new[] { ("price_creator", b), ("price_pro", a), ("price_creator", b), ("price_pro", a), ("price_creator", b) })
        {
            StripeFake fake = SubscriptionFake(SubscriptionJson(price: current));
            await Provider(fake).ChangePlanAsync("sub_1", target, PlanChangeTiming.Immediately, PlanChangeDiscount.Unchanged, default);
            keys.Add(fake.Calls[1].IdempotencyKey);
        }

        keys.Should().OnlyHaveUniqueItems().And.OnlyContain(k => k!.StartsWith("change:sub_1:", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ get / snapshot

    [Fact]
    public async Task GetSubscription_ReturnsStatusItemsPeriodAndTheCouponIdOfTheFirstDiscount()
    {
        object sub = SubscriptionJson("past_due", price: "price_team", quantity: 4, cancelAtPeriodEnd: true,
            discounts: new object[] { new { id = "di_1", source = new { type = "coupon", coupon = "rb_promo_x" }, end = Unix(Now.AddMonths(3)) } });
        StripeFake fake = new StripeFake().On("GET", "/v1/subscriptions/sub_1", sub);

        ProviderSubscriptionState s = await Provider(fake).GetSubscriptionAsync("sub_1", default);

        fake.Calls[0].Q("expand[]").Should().Be("discounts");
        s.Status.Should().Be("past_due");
        s.Items.Should().Equal(new BillingLineItem("price_team", 4));
        s.CancelScheduled.Should().BeTrue();
        s.PeriodStart.Should().Be(Now);
        s.PeriodEnd.Should().Be(Now.AddMonths(1));
        s.Discount.Should().Be(new ProviderSubscriptionDiscount("rb_promo_x", Now.AddMonths(3)));
    }

    [Fact]
    public async Task GetSubscription_CancelAtAlsoCountsAsScheduled_AndNoDiscountIsNull()
    {
        object sub = new { id = "sub_1", status = "active", cancel_at_period_end = false, cancel_at = Unix(Now.AddDays(5)), items = new { data = Array.Empty<object>() } };
        StripeFake fake = new StripeFake().On("GET", "/v1/subscriptions/sub_1", sub);

        ProviderSubscriptionState s = await Provider(fake).GetSubscriptionAsync("sub_1", default);

        s.CancelScheduled.Should().BeTrue();
        s.Discount.Should().BeNull();
        s.PeriodEnd.Should().BeNull();
    }

    [Fact]
    public async Task Snapshot_BuildsTheReconcileEvent_WithAStableIdForUnchangedState()
    {
        StripeFake fake = new StripeFake().On("GET", "/v1/subscriptions/sub_1", SubscriptionJson(org: Org.ToString()));
        StripeBillingProvider p = Provider(fake);

        BillingSnapshot a = await p.SnapshotSubscriptionAsync("sub_1", default);
        BillingSnapshot b = await p.SnapshotSubscriptionAsync("sub_1", default);

        // DEC-17 (revised): the id carries the fetch ticks, so an A->B->A round trip never collides with an earlier applied id.
        a.Event.EventId.Should().Be($"reconcile:sub:sub_1:{a.Event.EventId.Split(':')[3]}:{Now.Ticks}").And.MatchRegex("^reconcile:sub:sub_1:[0-9a-f]{12}:[0-9]+$");
        b.Event.EventId.Should().Be(a.Event.EventId, "the same state at the same fetch time");
        StripeBillingProvider later = new(new System.Net.Http.HttpClient(fake), Microsoft.Extensions.Options.Options.Create(Options()), new FixedClock(Now.AddHours(1)), null, (_, _) => System.Threading.Tasks.Task.CompletedTask);
        (await later.SnapshotSubscriptionAsync("sub_1", default)).Event.EventId.Should().NotBe(a.Event.EventId, "a later fetch of the same state is a new id");
        a.Event.EventType.Should().Be("subscription.reconciled");
        a.Event.OccurredAt.Should().Be(Now);
        a.Event.Kind.Should().Be(BillingEventKind.SubscriptionChanged);
        a.Event.OrganizationId.Should().Be(Org);
        a.Event.SubscriptionId.Should().Be("sub_1");
        a.ProviderUpdatedAt.Should().BeNull();
        a.RawJson.Should().Contain("\"type\":\"subscription.reconciled\"").And.Contain("sub_1");
    }

    [Fact]
    public async Task Snapshot_IdChangesWithStatusItemsOrCancelFlag_And404IsATypedRefusal()
    {
        async Task<string> Id(object sub) => (await Provider(new StripeFake().On("GET", "/v1/subscriptions/sub_1", sub)).SnapshotSubscriptionAsync("sub_1", default)).Event.EventId;
        string baseId = await Id(SubscriptionJson());

        (await Id(SubscriptionJson("past_due"))).Should().NotBe(baseId);
        (await Id(SubscriptionJson(price: "price_pro"))).Should().NotBe(baseId);
        (await Id(SubscriptionJson(quantity: 3))).Should().NotBe(baseId);
        (await Id(SubscriptionJson(cancelAtPeriodEnd: true))).Should().NotBe(baseId);

        StripeFake gone = new StripeFake().Error("GET", "/v1/subscriptions/sub_1", 404, "resource_missing");
        BillingProviderException ex = (await Provider(gone).Invoking(x => x.SnapshotSubscriptionAsync("sub_1", default)).Should().ThrowAsync<BillingProviderException>()).Which;
        ex.Status.Should().Be(404);
        ex.Code.Should().Be("resource_missing");
    }

    [Fact]
    public async Task CompletedUpgradeTransactions_KeepsOnlyPaidSubscriptionUpdates_AsUpgradePaidEvents()
    {
        DateTime paid = Now.AddMinutes(-3);
        object Invoice(string id, string reason, long amountPaid) => new
        {
            id,
            billing_reason = reason,
            amount_paid = amountPaid,
            currency = "eur",
            customer = "cus_1",
            created = Unix(Now.AddMinutes(-4)),
            status_transitions = new { paid_at = Unix(paid) },
            parent = new { subscription_details = new { subscription = "sub_1", metadata = new { organization_id = Org.ToString() } } },
            lines = new { data = new object[] { new { amount = amountPaid, quantity = 1, period = new { start = Unix(paid), end = Unix(paid.AddMonths(1)) }, pricing = new { price_details = new { price = "price_pro" } } } } },
            total_discount_amounts = Array.Empty<object>(),
            discounts = Array.Empty<object>()
        };
        StripeFake fake = new StripeFake().On("GET", "/v1/invoices", new
        {
            has_more = false,
            data = new[] { Invoice("in_up", "subscription_update", 2500), Invoice("in_cycle", "subscription_cycle", 2900), Invoice("in_zero", "subscription_update", 0) }
        });

        IReadOnlyList<BillingSnapshot> result = await Provider(fake).CompletedUpgradeTransactionsAsync("sub_1", Now.AddDays(-1), default);

        StripeCall q = fake.Calls.Single();
        q.Q("subscription").Should().Be("sub_1");
        q.Q("status").Should().Be("paid");
        q.Q("limit").Should().Be("100");
        q.Q("created[gte]").Should().Be(Unix(Now.AddDays(-1)).ToString());
        BillingSnapshot only = result.Should().ContainSingle().Which;
        only.Event.EventId.Should().Be("reconcile:txn:in_up");
        only.Event.EventType.Should().Be("transaction.reconciled");
        only.Event.OccurredAt.Should().Be(paid);
        only.Event.Kind.Should().Be(BillingEventKind.UpgradePaid);
        only.Event.AmountMinor.Should().Be(2500);
        only.Event.Items.Should().Equal(new BillingLineItem("price_pro", 1));
        only.Event.SubscriptionId.Should().Be("sub_1");
    }

    // ------------------------------------------------------------------ discounts

    private static ProviderDiscountSpec Spec(PromotionDiscountType type = PromotionDiscountType.Percentage, string amount = "50", bool recur = true, int? intervals = null, int? limit = null, DateTime? expires = null, string[]? prices = null, string? currency = null, string description = "Founder offer")
        => new(Guid.Parse("33333333-3333-3333-3333-333333333333"), description, type, amount, currency, recur, intervals, limit, prices ?? [], expires);

    [Fact]
    public async Task CreateDiscount_PercentForever_MapsToACouponWithOurId()
    {
        StripeFake fake = new StripeFake().On("POST", "/v1/coupons", new { id = "rb_promo_33333333-3333-3333-3333-333333333333" });

        string id = await Provider(fake).CreateDiscountAsync(Spec(), default);

        id.Should().Be("rb_promo_33333333-3333-3333-3333-333333333333");
        StripeCall c = fake.Calls.Single();
        c.F("id").Should().Be(id);
        c.F("percent_off").Should().Be("50");
        c.F("duration").Should().Be("forever");
        c.HasF("duration_in_months").Should().BeFalse();
        c.HasF("amount_off").Should().BeFalse();
        c.HasF("applies_to[products][]").Should().BeFalse("no restriction means every product");
        c.F("name").Should().Be("Founder offer");
        c.F("metadata[promotion_id]").Should().Be("33333333-3333-3333-3333-333333333333");
        c.F("metadata[reelbolt_catalog]").Should().Be("1");
        c.IdempotencyKey.Should().BeNull();
    }

    [Fact]
    public async Task CreateDiscount_FlatOnce_RepeatingWithIntervals_LimitExpiryAndProducts()
    {
        StripeFake once = new StripeFake().On("POST", "/v1/coupons", new { id = "c1" });
        await Provider(once).CreateDiscountAsync(Spec(PromotionDiscountType.Flat, "500", recur: false, currency: "EUR"), default);
        once.Calls[0].F("amount_off").Should().Be("500");
        once.Calls[0].F("currency").Should().Be("eur");
        once.Calls[0].F("duration").Should().Be("once");
        once.Calls[0].HasF("percent_off").Should().BeFalse();

        StripeFake repeating = new StripeFake()
            .On("GET", "/v1/prices/price_a", new { id = "price_a", product = "prod_1" })
            .On("GET", "/v1/prices/price_b", new { id = "price_b", product = "prod_1" })
            .On("GET", "/v1/prices/price_c", new { id = "price_c", product = new { id = "prod_2" } })
            .On("POST", "/v1/coupons", new { id = "c2" });
        DateTime expires = new(2027, 1, 31, 22, 59, 0, DateTimeKind.Utc);
        await Provider(repeating).CreateDiscountAsync(Spec(intervals: 4, limit: 100, expires: expires, prices: ["price_a", "price_b", "price_c"], description: new string('x', 60)), default);
        StripeCall c = repeating.To("/v1/coupons").Single();
        c.F("duration").Should().Be("repeating");
        c.F("duration_in_months").Should().Be("4");
        c.F("max_redemptions").Should().Be("100");
        c.F("redeem_by").Should().Be(Unix(expires).ToString());
        c.Form.Where(p => p.Key == "applies_to[products][]").Select(p => p.Value).Should().Equal("prod_1", "prod_2");
        c.F("name").Should().HaveLength(40);
    }

    [Fact]
    public async Task CreateDiscount_AlreadyExists_ReturnsTheIdOnlyWhenItCarriesOurMarker()
    {
        string couponId = "rb_promo_33333333-3333-3333-3333-333333333333";
        StripeFake ours = new StripeFake()
            .Error("POST", "/v1/coupons", 400, "resource_already_exists")
            .On("GET", $"/v1/coupons/{couponId}", new { id = couponId, metadata = new { reelbolt_catalog = "1" } });
        (await Provider(ours).CreateDiscountAsync(Spec(), default)).Should().Be(couponId);

        StripeFake foreign = new StripeFake()
            .Error("POST", "/v1/coupons", 400, "resource_already_exists")
            .On("GET", $"/v1/coupons/{couponId}", new { id = couponId, metadata = new { } });
        BillingProviderException ex = (await Provider(foreign).Invoking(p => p.CreateDiscountAsync(Spec(), default)).Should().ThrowAsync<BillingProviderException>()).Which;
        ex.Code.Should().Be("resource_already_exists");
    }

    [Fact]
    public async Task UpdateDiscount_IsALoggedNoOp_NothingIsSent()
    {
        StripeFake fake = new();

        await Provider(fake).UpdateDiscountAsync("rb_promo_x", false, Now, true, default);

        fake.Calls.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ preview

    [Fact]
    public async Task Preview_BuildsFormattedAndRawFields_FromStripesIntegerTotals()
    {
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/prices/price_team", new { id = "price_team", currency = "eur" })
            .On("POST", "/v1/invoices/create_preview", new
            {
                currency = "eur",
                subtotal = 5800,
                total = 2900,
                total_discount_amounts = new[] { new { amount = 2900 } },
                total_taxes = Array.Empty<object>(),
                lines = new { data = new[] { new { amount = 5800, quantity = 2, discount_amounts = new[] { new { amount = 2900 } } } } }
            });

        PricePreview p = await Provider(fake).PreviewPriceAsync([new BillingLineItem("price_team", 2)], "rb_founder", default);

        StripeCall c = fake.To("/v1/invoices/create_preview").Single();
        c.F("currency").Should().Be("eur");
        c.F("subscription_details[items][0][price]").Should().Be("price_team");
        c.F("subscription_details[items][0][quantity]").Should().Be("2");
        c.F("discounts[0][coupon]").Should().Be("rb_founder");
        p.CurrencyCode.Should().Be("EUR");
        p.Subtotal.Should().Be("€58.00");
        p.Discount.Should().Be("€29.00");
        p.Total.Should().Be("€29.00");
        p.RawTax.Should().Be("0");
        p.RawDiscount.Should().Be("2900");
        p.UnitSubtotal.Should().Be("€29.00");
        p.UnitTotal.Should().Be("€14.50");
        (p.SubtotalMinor, p.DiscountMinor, p.TotalMinor, p.UnitSubtotalMinor, p.UnitTotalMinor, p.MinorUnitExponent).Should().Be((5800L, 2900L, 2900L, 2900L, 1450L, 2));
    }

    [Fact]
    public async Task Preview_WithoutDiscountOrTaxArrays_LeavesRawFieldsNull_AndAnInexactUnitTotalNull()
    {
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/prices/price_x", new { id = "price_x", currency = "jpy" })
            .On("POST", "/v1/invoices/create_preview", new
            {
                currency = "jpy",
                subtotal = 1000,
                total = 666,
                lines = new { data = new[] { new { amount = 1000, quantity = 3, discount_amounts = new[] { new { amount = 334 } } } } }
            });

        PricePreview p = await Provider(fake).PreviewPriceAsync([new BillingLineItem("price_x", 3)], null, default);

        fake.To("/v1/invoices/create_preview").Single().HasF("discounts[0][coupon]").Should().BeFalse();
        p.Total.Should().Be("JPY 666");
        p.RawTax.Should().BeNull();
        p.RawDiscount.Should().BeNull();
        p.UnitSubtotal.Should().BeNull("1000 is not divisible by 3");
        p.UnitTotal.Should().Be("JPY 222", "(1000-334)/3 = 222 is exact");
        p.MinorUnitExponent.Should().Be(0);
    }

    // ------------------------------------------------------------------ cancel transaction

    [Fact]
    public async Task CancelTransaction_ExpiresTheSession()
    {
        StripeFake fake = new StripeFake().On("POST", "/v1/checkout/sessions/cs_1/expire", new { id = "cs_1", status = "expired" });

        (await Provider(fake).CancelTransactionAsync("cs_1", default)).Should().Be(ProviderTransactionCancel.Cancelled);
    }

    [Theory]
    [InlineData(400, "checkout_session_not_expirable")]
    [InlineData(404, "resource_missing")]
    public async Task CancelTransaction_NotExpirable_IsNotCancellable(int status, string code)
    {
        StripeFake fake = new StripeFake().Error("POST", "/v1/checkout/sessions/cs_1/expire", status, code);

        (await Provider(fake).CancelTransactionAsync("cs_1", default)).Should().Be(ProviderTransactionCancel.NotCancellable);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task CancelTransaction_AuthRateLimitOrServerFailure_ThrowsSoTheSweeperRetries(int status)
    {
        StripeFake fake = new StripeFake().Error("POST", "/v1/checkout/sessions/cs_1/expire", status, "whatever");

        Func<Task> act = async () => await Provider(fake).CancelTransactionAsync("cs_1", default);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task CancelTransaction_NetworkFailure_Throws()
    {
        StripeBillingProvider p = new(new HttpClient(new ThrowingHandler()), Microsoft.Extensions.Options.Options.Create(Options()), new FixedClock(Now), null, (_, _) => Task.CompletedTask);

        Func<Task> act = async () => await p.CancelTransactionAsync("cs_1", default);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().NotContain(ApiKey);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }

    // ------------------------------------------------------------------ refunds

    [Fact]
    public async Task Refund_ByInvoice_ResolvesThePaymentIntent_AndPostsTheRefund()
    {
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/invoice_payments", new { data = new[] { new { invoice = "in_1", payment = new { type = "payment_intent", payment_intent = "pi_9" } } } })
            .On("POST", "/v1/refunds", new { id = "re_1", amount = 2900, currency = "eur", status = "succeeded" });

        ProviderRefund r = await Provider(fake).RefundTransactionAsync("in_1", null, "money_back_14d", "refund:org:in_1", default);

        r.Should().Be(new ProviderRefund("re_1", 2900, "EUR", "succeeded"));
        fake.Calls[0].Q("invoice").Should().Be("in_1");
        fake.Calls[0].Q("status").Should().Be("paid");
        StripeCall post = fake.To("/v1/refunds").Single();
        post.F("payment_intent").Should().Be("pi_9");
        post.HasF("amount").Should().BeFalse("omitted means the full amount");
        post.F("reason").Should().Be("requested_by_customer");
        post.F("metadata[reelbolt_reason]").Should().Be("money_back_14d");
        post.IdempotencyKey.Should().Be("refund:org:in_1");
        post.Authorization.Should().Be($"Bearer {ApiKey}");
    }

    [Fact]
    public async Task Refund_BySessionAndByPaymentIntent_AndAPartialAmount()
    {
        StripeFake session = new StripeFake()
            .On("GET", "/v1/checkout/sessions/cs_1", new { id = "cs_1", payment_intent = "pi_5" })
            .On("POST", "/v1/refunds", new { id = "re_2", amount = 500, currency = "eur", status = "pending" });
        ProviderRefund r = await Provider(session).RefundTransactionAsync("cs_1", 500, "support", "k1", default);
        r.Status.Should().Be("pending");
        session.To("/v1/refunds").Single().F("payment_intent").Should().Be("pi_5");
        session.To("/v1/refunds").Single().F("amount").Should().Be("500");

        StripeFake direct = new StripeFake().On("POST", "/v1/refunds", new { id = "re_3", amount = 100, currency = "eur", status = "succeeded" });
        await Provider(direct).RefundTransactionAsync("pi_7", null, "x", "k2", default);
        direct.Calls.Should().ContainSingle().Which.F("payment_intent").Should().Be("pi_7");
    }

    [Fact]
    public async Task Refund_NoPaidPayment_IsATypedNotFound_AndPaddleKeepsTheNotSupportedDefault()
    {
        StripeFake fake = new StripeFake().On("GET", "/v1/invoice_payments", new { data = Array.Empty<object>() });
        BillingProviderException ex = (await Provider(fake).Invoking(p => p.RefundTransactionAsync("in_1", null, "x", "k", default)).Should().ThrowAsync<BillingProviderException>()).Which;
        ex.Code.Should().Be("payment_not_found");

        IBillingProvider paddle = new ReelBolt.Inference.Api.Services.Billing.Paddle.PaddleBillingProvider(new HttpClient(), Microsoft.Extensions.Options.Options.Create(new PaddleOptions()));
        await paddle.Invoking(p => p.RefundTransactionAsync("txn_1", null, "x", "k", default)).Should().ThrowAsync<NotSupportedException>();
    }

    // ------------------------------------------------------------------ webhook delegation

    [Fact]
    public void VerifyAndParseWebhook_DelegatesToTheParser_WithTheConfiguredPacks()
    {
        StripeBillingProvider p = Provider(new StripeFake());
        string body = StripePayloads.Envelope("checkout.session.completed", StripePayloads.TopUpSession(credits: 1000));

        BillingEvent ok = p.VerifyAndParseWebhook(body, StripePayloads.Signed(body));
        ok.Kind.Should().Be(BillingEventKind.TopUpPaid);

        string unknown = StripePayloads.Envelope("checkout.session.completed", StripePayloads.TopUpSession(credits: 777));
        p.VerifyAndParseWebhook(unknown, StripePayloads.Signed(unknown)).Kind.Should().Be(BillingEventKind.Ignored);

        p.Invoking(x => x.VerifyAndParseWebhook(body, new Dictionary<string, string>())).Should().Throw<WebhookVerificationException>();
    }
}

internal static class StripeJsonShape
{
    /// <summary>Test sugar: the same subscription JSON under another id.</summary>
    public static object with2(this object sub, string id)
    {
        System.Text.Json.Nodes.JsonObject node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(sub))!.AsObject();
        node["id"] = id;
        return node;
    }
}
