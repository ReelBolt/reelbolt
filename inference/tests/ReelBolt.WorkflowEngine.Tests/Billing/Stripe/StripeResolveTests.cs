using System.Linq;
using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Billing.Stripe.StripeKit;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

/// <summary><see cref="StripeBillingProvider.ResolveAsync"/>: the reads a webhook cannot carry (DEC-4, DEC-5).</summary>
public sealed class StripeResolveTests
{
    private static BillingEvent Parse(string type, JsonObject obj, string id = "evt_1")
    {
        string body = StripePayloads.Envelope(type, obj, id: id);
        return new StripeEventParser(StripePayloads.Options(), new FixedClock(Now)).Parse(body, StripePayloads.Signed(body));
    }

    private static JsonObject InvoiceWithDiscount()
    {
        JsonObject inv = StripePayloads.Invoice("subscription_update", 2500, StripePayloads.Line(2500, "price_pro", 1, Now));
        inv["discounts"] = new JsonArray("di_1");
        inv["total_discount_amounts"] = new JsonArray(new JsonObject { ["amount"] = 500, ["discount"] = "di_1" });
        return inv;
    }

    // ------------------------------------------------------------------ subscription events

    [Fact]
    public async Task SubscriptionChanged_IsRebuiltFromAFreshRead_StampedNow_SameIdAndType()
    {
        DateTime stale = Now.AddHours(-6);
        string body = StripePayloads.Envelope("customer.subscription.updated", StripePayloads.Subscription(quantity: 1), id: "evt_old", created: stale);
        BillingEvent old = new StripeEventParser(StripePayloads.Options(), new FixedClock(stale)).Parse(body, StripePayloads.Signed(body, at: stale));
        StripeFake fake = new StripeFake().On("GET", "/v1/subscriptions/sub_1", SubscriptionJson(price: "price_pro", quantity: 5, org: StripePayloads.OrgId.ToString()));

        BillingEvent fresh = await Provider(fake).ResolveAsync(old, default);

        fake.Calls.Should().ContainSingle().Which.Q("expand[]").Should().BeNull();
        fresh.EventId.Should().Be("evt_old");
        fresh.EventType.Should().Be("customer.subscription.updated");
        fresh.OccurredAt.Should().Be(Now, "the fetch time orders the event, so a delayed old event cannot regress the state");
        fresh.ChangedAt.Should().Be(stale, "the webhook's own created time is when the change happened (DEC-5)");
        fresh.Items.Should().Equal(new BillingLineItem("price_pro", 5));
        fresh.Kind.Should().Be(BillingEventKind.SubscriptionChanged);
        fresh.OrganizationId.Should().Be(StripePayloads.OrgId);
    }

    [Fact]
    public async Task SubscriptionChanged_KeepsTheEventsOrganization_WhenTheFreshReadNamesNone()
    {
        BillingEvent evt = Parse("customer.subscription.updated", StripePayloads.Subscription());
        StripeFake fake = new StripeFake().On("GET", "/v1/subscriptions/sub_1", SubscriptionJson());

        (await Provider(fake).ResolveAsync(evt, default)).OrganizationId.Should().Be(StripePayloads.OrgId);
    }

    [Fact]
    public async Task DeletedSubscription_Stripe404_KeepsThePayloadEvent()
    {
        BillingEvent evt = Parse("customer.subscription.deleted", StripePayloads.Subscription(status: "canceled"));
        StripeFake fake = new StripeFake().Error("GET", "/v1/subscriptions/sub_1", 404, "resource_missing");

        BillingEvent result = await Provider(fake).ResolveAsync(evt, default);

        result.Should().Be(evt with { OccurredAt = Now, ChangedAt = evt.OccurredAt });
        result.Status.Should().Be(SubscriptionStatus.Canceled);
    }

    [Fact]
    public async Task DeletedSubscription_Stripe404_IsStampedNow_WithChangedAtTheWebhooksCreated()
    {
        DateTime created = Now.AddHours(-2);
        string body = StripePayloads.Envelope("customer.subscription.deleted", StripePayloads.Subscription(status: "canceled"), created: created);
        BillingEvent evt = new StripeEventParser(StripePayloads.Options(), new FixedClock(created)).Parse(body, StripePayloads.Signed(body, at: created));
        StripeFake fake = new StripeFake().Error("GET", "/v1/subscriptions/sub_1", 404, "resource_missing");

        BillingEvent result = await Provider(fake).ResolveAsync(evt, default);

        result.OccurredAt.Should().Be(Now);
        result.ChangedAt.Should().Be(created);
    }

    [Fact]
    public async Task SubscriptionChanged_OccurredAtIsCapturedBeforeTheGet()
    {
        AdvancingClock clock = new(Now);
        BillingEvent evt = Parse("customer.subscription.updated", StripePayloads.Subscription());
        StripeFake fake = new StripeFake().On("GET", "/v1/subscriptions/sub_1", SubscriptionJson(price: "price_pro"));
        StripeBillingProvider provider = new(new HttpClient(fake), Microsoft.Extensions.Options.Options.Create(Options()), clock);

        BillingEvent r = await provider.ResolveAsync(evt, default);

        r.OccurredAt.Should().Be(Now, "the clock was read once, before the request was sent");
    }

    private sealed class AdvancingClock(DateTime start) : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow() => new(start.AddMinutes(_reads++), TimeSpan.Zero);
    }

    // ------------------------------------------------------------------ first subscription invoice (DEC-20)

    [Fact]
    public async Task SubscriptionCreateInvoice_ResolvesTheCheckoutSessionOfTheSubscription()
    {
        BillingEvent evt = Parse("invoice.paid", StripePayloads.Invoice("subscription_create", 2900, StripePayloads.Line(2900, "price_creator", 1, Now)));
        evt.Kind.Should().Be(BillingEventKind.TransactionCompleted);
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/checkout/sessions", new { data = new[] { new { id = "cs_sub_1" } } })
            .On("GET", "/v1/invoice_payments", new { data = Array.Empty<object>() });

        BillingEvent r = await Provider(fake).ResolveAsync(evt, default);

        r.CheckoutId.Should().Be("cs_sub_1");
        StripeCall lookup = fake.To("/v1/checkout/sessions").Single();
        lookup.Q("subscription").Should().Be("sub_1");
        lookup.Q("limit").Should().Be("1");
    }

    [Fact]
    public async Task SubscriptionCreateInvoice_SessionLookupFailure_IsSwallowed_CheckoutIdStaysNull()
    {
        BillingEvent evt = Parse("invoice.paid", StripePayloads.Invoice("subscription_create", 2900, StripePayloads.Line(2900, "price_creator", 1, Now)));
        StripeFake fake = new StripeFake()
            .Error("GET", "/v1/checkout/sessions", 500, "api_error")
            .On("GET", "/v1/invoice_payments", new { data = Array.Empty<object>() });

        BillingEvent r = await Provider(fake).ResolveAsync(evt, default);

        r.CheckoutId.Should().BeNull();
    }

    [Fact]
    public async Task RenewalInvoice_DoesNotLookUpACheckoutSession()
    {
        BillingEvent evt = Parse("invoice.paid", StripePayloads.Invoice("subscription_cycle", 2900, StripePayloads.Line(2900, "price_creator", 1, Now)));
        StripeFake fake = new StripeFake().On("GET", "/v1/invoice_payments", new { data = Array.Empty<object>() });

        BillingEvent r = await Provider(fake).ResolveAsync(evt, default);

        r.CheckoutId.Should().BeNull();
        fake.To("/v1/checkout/sessions").Should().BeEmpty();
    }

    // ------------------------------------------------------------------ disputes (DEC-18)

    [Theory]
    [InlineData(1450, true)]
    [InlineData(2000, true)]
    [InlineData(500, false)]
    public async Task LostDispute_OfASubscriptionPayment_MapsToTheInvoice_FullWhenTheDisputeCoversTheCharge(long disputed, bool full)
    {
        BillingEvent evt = Parse("charge.dispute.closed", StripePayloads.Dispute(amount: disputed));
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/invoice_payments", new { data = new[] { new { invoice = "in_7", payment = new { type = "payment_intent", payment_intent = "pi_1" } } } })
            .On("GET", "/v1/invoices/in_7", new { id = "in_7", parent = new { subscription_details = new { subscription = "sub_9" } } })
            .On("GET", "/v1/payment_intents/pi_1", new { id = "pi_1", latest_charge = new { amount = 1450, amount_refunded = 0 } });

        BillingEvent r = await Provider(fake).ResolveAsync(evt, default);

        r.TransactionId.Should().Be("in_7");
        r.SubscriptionId.Should().Be("sub_9");
        r.AdjustmentId.Should().Be("dp_1");
        r.FullRefund.Should().Be(full);
    }

    [Fact]
    public async Task LostDispute_ForTheRemainderAfterAPartialRefund_IsFull()
    {
        BillingEvent evt = Parse("charge.dispute.closed", StripePayloads.Dispute(amount: 950));
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/invoice_payments", new { data = new[] { new { invoice = "in_7", payment = new { type = "payment_intent", payment_intent = "pi_1" } } } })
            .On("GET", "/v1/invoices/in_7", new { id = "in_7", parent = new { subscription_details = new { subscription = "sub_9" } } })
            .On("GET", "/v1/payment_intents/pi_1", new { id = "pi_1", latest_charge = new { amount = 1450, amount_refunded = 500 } });

        (await Provider(fake).ResolveAsync(evt, default)).FullRefund.Should().BeTrue("500 refunded + 950 disputed covers the 1,450 charge");
    }

    [Fact]
    public async Task Refund_OfAnUpgradeInvoice_WithOnlyALineLevelSubscription_StillMapsToTheInvoice()
    {
        // The parser records such an invoice under its in_ id (line-level subscription fallback): the refund must find it.
        BillingEvent evt = Parse("refund.created", StripePayloads.Refund(amount: 3050));
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/invoice_payments", new { data = new[] { new { invoice = "in_7", payment = new { type = "payment_intent", payment_intent = "pi_1" } } } })
            .On("GET", "/v1/invoices/in_7", new { id = "in_7", billing_reason = "subscription_update", lines = new { data = new[] { new { parent = new { subscription_item_details = new { subscription = "sub_9" } } } } } })
            .On("GET", "/v1/payment_intents/pi_1", new { id = "pi_1", latest_charge = new { amount = 3050, amount_refunded = 3050 } });

        BillingEvent r = await Provider(fake).ResolveAsync(evt, default);

        r.TransactionId.Should().Be("in_7");
        r.SubscriptionId.Should().Be("sub_9");
        r.FullRefund.Should().BeTrue();
    }

    [Fact]
    public async Task Refund_OfASubscriptionInvoice_ByBillingReasonAlone_MapsToTheInvoice()
    {
        BillingEvent evt = Parse("refund.created", StripePayloads.Refund(amount: 2900));
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/invoice_payments", new { data = new[] { new { invoice = "in_7", payment = new { type = "payment_intent", payment_intent = "pi_1" } } } })
            .On("GET", "/v1/invoices/in_7", new { id = "in_7", billing_reason = "subscription_cycle" })
            .On("GET", "/v1/payment_intents/pi_1", new { id = "pi_1", latest_charge = new { amount = 2900, amount_refunded = 2900 } });

        (await Provider(fake).ResolveAsync(evt, default)).TransactionId.Should().Be("in_7");
    }

    [Fact]
    public async Task LostDispute_WithARealDisputeId_DuPrefix_IsFull_WhenItCoversTheCharge()
    {
        // A real sandbox names disputes du_..., not dp_...: the lost chargeback of a top-up must still claw back.
        BillingEvent evt = (Parse("charge.dispute.closed", StripePayloads.Dispute(amount: 2196))) with { AdjustmentId = "du_1UUpC7Dw6DA4uVER3Va1g2Hr" };
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/invoice_payments", new { data = Array.Empty<object>() })
            .On("GET", "/v1/payment_intents/pi_1", new { id = "pi_1", latest_charge = new { amount = 2196, amount_refunded = 0 } });

        BillingEvent r = await Provider(fake).ResolveAsync(evt, default);

        r.FullRefund.Should().BeTrue();
        r.AdjustmentId.Should().StartWith("du_");
    }

    [Fact]
    public async Task LostDispute_OfATopUp_KeepsThePaymentIntent()
    {
        BillingEvent evt = Parse("charge.dispute.closed", StripePayloads.Dispute());
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/invoice_payments", new { data = Array.Empty<object>() })
            .On("GET", "/v1/payment_intents/pi_1", new { id = "pi_1", latest_charge = new { amount = 1450, amount_refunded = 0 } });

        BillingEvent r = await Provider(fake).ResolveAsync(evt, default);

        r.TransactionId.Should().Be("pi_1");
        r.FullRefund.Should().BeTrue();
    }

    [Fact]
    public async Task ALiveSubscription_Stripe404_IsNotSwallowed()
    {
        BillingEvent evt = Parse("customer.subscription.updated", StripePayloads.Subscription());
        StripeFake fake = new StripeFake().Error("GET", "/v1/subscriptions/sub_1", 404, "resource_missing");

        await Provider(fake).Invoking(p => p.ResolveAsync(evt, default)).Should().ThrowAsync<BillingProviderException>();
    }

    [Fact]
    public async Task SubscriptionChanged_AStripeOutage_ThrowsSoTheWebhookAnswers500()
    {
        BillingEvent evt = Parse("customer.subscription.updated", StripePayloads.Subscription());
        StripeFake fake = new StripeFake().Error("GET", "/v1/subscriptions/sub_1", 503, "api_error");

        await Provider(fake).Invoking(p => p.ResolveAsync(evt, default)).Should().ThrowAsync<HttpRequestException>();
    }

    // ------------------------------------------------------------------ invoices

    [Fact]
    public async Task UpgradePaid_ReplacesTheDiscountObjectIdByTheCouponId_AndReadsTheFee()
    {
        BillingEvent evt = Parse("invoice.paid", InvoiceWithDiscount());
        evt.DiscountId.Should().Be("di_1");
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/invoices/in_1", new { id = "in_1", discounts = new object[] { new { id = "di_1", source = new { type = "coupon", coupon = "rb_promo_x" } } } })
            .On("GET", "/v1/invoice_payments", new { data = new[] { new { payment = new { type = "payment_intent", payment_intent = "pi_77" } } } })
            .On("GET", "/v1/payment_intents/pi_77", new { id = "pi_77", latest_charge = new { balance_transaction = new { fee = 87 } } });

        BillingEvent r = await Provider(fake).ResolveAsync(evt, default);

        r.DiscountId.Should().Be("rb_promo_x");
        r.FeeMinor.Should().Be(87);
        fake.To("/v1/invoices/in_1").Single().Q("expand[]").Should().Be("discounts");
        StripeCall pay = fake.To("/v1/invoice_payments").Single();
        pay.Q("invoice").Should().Be("in_1");
        // Stripe refuses an expansion deeper than four levels (found against a real sandbox): two shallow reads instead.
        pay.Q("expand[]").Should().BeNull();
        fake.To("/v1/payment_intents/pi_77").Single().Q("expand[]").Should().Be("latest_charge.balance_transaction");
    }

    [Fact]
    public async Task TransactionCompleted_WithoutADiscountObject_OnlyLooksUpTheFee()
    {
        BillingEvent evt = Parse("invoice.paid", StripePayloads.Invoice("subscription_cycle", 2900, StripePayloads.Line(2900, "price_creator", 1, Now)));
        StripeFake fake = new StripeFake().On("GET", "/v1/invoice_payments", new { data = Array.Empty<object>() });

        BillingEvent r = await Provider(fake).ResolveAsync(evt, default);

        fake.To("/v1/invoices/in_1").Should().BeEmpty();
        r.FeeMinor.Should().BeNull("no balance transaction found");
        r.Kind.Should().Be(BillingEventKind.TransactionCompleted);
    }

    [Fact]
    public async Task TheFeeLookupFailing_IsSwallowed_FeeStaysNull()
    {
        BillingEvent evt = Parse("invoice.paid", StripePayloads.Invoice("subscription_update", 2500, StripePayloads.Line(2500, "price_pro", 1, Now)));
        StripeFake fake = new StripeFake().Error("GET", "/v1/invoice_payments", 500, "api_error");

        BillingEvent r = await Provider(fake).ResolveAsync(evt, default);

        r.FeeMinor.Should().BeNull();
        r.Kind.Should().Be(BillingEventKind.UpgradePaid);
    }

    [Fact]
    public async Task TheDiscountLookupFailing_Throws_NotSwallowed()
    {
        BillingEvent evt = Parse("invoice.paid", InvoiceWithDiscount());
        StripeFake fake = new StripeFake().Error("GET", "/v1/invoices/in_1", 500, "api_error");

        await Provider(fake).Invoking(p => p.ResolveAsync(evt, default)).Should().ThrowAsync<HttpRequestException>();
    }

    // ------------------------------------------------------------------ top-ups

    [Fact]
    public async Task TopUpPaid_ReadsTheFeeFromThePaymentIntentsChargeBalanceTransaction()
    {
        BillingEvent evt = Parse("checkout.session.completed", StripePayloads.TopUpSession(credits: 1000));
        StripeFake fake = new StripeFake().On("GET", "/v1/payment_intents/pi_1", new { id = "pi_1", latest_charge = new { balance_transaction = new { fee = 61 } } });

        BillingEvent r = await Provider(fake).ResolveAsync(evt, default);

        r.FeeMinor.Should().Be(61);
        fake.Calls.Should().ContainSingle().Which.Q("expand[]").Should().Be("latest_charge.balance_transaction");
        r.TransactionId.Should().Be("pi_1");
        r.CheckoutId.Should().Be("cs_test_1");
    }

    [Fact]
    public async Task TopUpPaid_FeeFailure_IsSwallowed()
    {
        BillingEvent evt = Parse("checkout.session.completed", StripePayloads.TopUpSession(credits: 1000));
        StripeFake fake = new StripeFake().Error("GET", "/v1/payment_intents/pi_1", 404, "resource_missing");

        (await Provider(fake).ResolveAsync(evt, default)).FeeMinor.Should().BeNull();
    }

    // ------------------------------------------------------------------ refunds

    [Fact]
    public async Task Refund_OfASubscriptionPayment_BecomesTheInvoiceAndSubscription_FullWhenFullyRefunded()
    {
        BillingEvent evt = Parse("refund.created", StripePayloads.Refund());
        evt.TransactionId.Should().Be("pi_1");
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/invoice_payments", new { data = new[] { new { invoice = "in_7", payment = new { type = "payment_intent", payment_intent = "pi_1" } } } })
            .On("GET", "/v1/invoices/in_7", new { id = "in_7", parent = new { subscription_details = new { subscription = "sub_9" } } })
            .On("GET", "/v1/payment_intents/pi_1", new { id = "pi_1", latest_charge = new { amount = 1450, amount_refunded = 1450 } });

        BillingEvent r = await Provider(fake).ResolveAsync(evt, default);

        r.TransactionId.Should().Be("in_7");
        r.SubscriptionId.Should().Be("sub_9");
        r.FullRefund.Should().BeTrue();
        r.AdjustmentId.Should().Be("re_1");
        StripeCall lookup = fake.To("/v1/invoice_payments").Single();
        lookup.Q("payment[type]").Should().Be("payment_intent");
        lookup.Q("payment[payment_intent]").Should().Be("pi_1");
        lookup.Q("status").Should().Be("paid");
    }

    [Fact]
    public async Task TopUpPaid_FeeLeavesOutTheTaxStripeWithholds()
    {
        BillingEvent evt = Parse("checkout.session.completed", StripePayloads.TopUpSession());
        StripeFake fake = new StripeFake().On("GET", "/v1/payment_intents/pi_1", new
        {
            id = "pi_1",
            latest_charge = new
            {
                balance_transaction = new
                {
                    fee = 393,
                    fee_details = new object[] { new { type = "stripe_fee", amount = 75 }, new { type = "stripe_fee", amount = 32 }, new { type = "withheld_tax", amount = 286 } }
                }
            }
        });

        (await Provider(fake).ResolveAsync(evt, default)).FeeMinor.Should().Be(107, "the 286 withheld tax is not Stripe's fee");
    }

    [Fact]
    public async Task Refund_OfATopUp_WhoseManagedPaymentsInvoiceHasNoSubscription_KeepsThePaymentIntent()
    {
        // Managed Payments creates an invoice (billing_reason manual) for a one-time payment; it is not the
        // subscription invoice the platform recorded, so the refund must stay under the PaymentIntent.
        BillingEvent evt = Parse("refund.created", StripePayloads.Refund(amount: 1450));
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/invoice_payments", new { data = new[] { new { invoice = "in_manual", payment = new { type = "payment_intent", payment_intent = "pi_1" } } } })
            .On("GET", "/v1/invoices/in_manual", new { id = "in_manual", billing_reason = "manual" })
            .On("GET", "/v1/payment_intents/pi_1", new { id = "pi_1", latest_charge = new { amount = 1450, amount_refunded = 1450 } });

        BillingEvent r = await Provider(fake).ResolveAsync(evt, default);

        r.TransactionId.Should().Be("pi_1");
        r.SubscriptionId.Should().BeNull();
        r.FullRefund.Should().BeTrue();
    }

    [Fact]
    public async Task Refund_OfATopUp_KeepsThePaymentIntent_PartialIsNotFull()
    {
        BillingEvent evt = Parse("refund.created", StripePayloads.Refund(amount: 500));
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/invoice_payments", new { data = Array.Empty<object>() })
            .On("GET", "/v1/payment_intents/pi_1", new { id = "pi_1", latest_charge = new { amount = 1450, amount_refunded = 500 } });

        BillingEvent r = await Provider(fake).ResolveAsync(evt, default);

        r.TransactionId.Should().Be("pi_1");
        r.SubscriptionId.Should().BeNull();
        r.FullRefund.Should().BeFalse();
    }

    [Theory]
    [InlineData("/v1/invoice_payments")]
    [InlineData("/v1/payment_intents/pi_1")]
    public async Task Refund_AnyLookupFailure_Throws_SoStripeRetriesTheWebhook(string failingPath)
    {
        BillingEvent evt = Parse("refund.created", StripePayloads.Refund());
        StripeFake failing = new();
        failing.Error("GET", failingPath, 500, "api_error");
        if (failingPath != "/v1/invoice_payments") failing.On("GET", "/v1/invoice_payments", new { data = Array.Empty<object>() });

        await Provider(failing).Invoking(p => p.ResolveAsync(evt, default)).Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task IgnoredAndOtherKinds_PassThroughUntouched_WithoutAnyCall()
    {
        BillingEvent ignored = Parse("invoice.payment_failed", StripePayloads.Invoice("subscription_cycle", 1));
        StripeFake fake = new();

        (await Provider(fake).ResolveAsync(ignored, default)).Should().Be(ignored);
        fake.Calls.Should().BeEmpty();
    }
}
