// Fixtures shaped from docs.stripe.com 2026-10-26, replace with redacted sandbox captures after the operator spike.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Billing.Stripe.StripePayloads;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

public sealed class StripeEventParserTests
{
    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private static BillingEvent Parse(string type, JsonNode obj, StripeOptions? options = null, Func<int, bool>? pack = null, bool livemode = false, string? apiVersion = StripeOptions.ApiVersion, DateTime? created = null)
    {
        string body = Envelope(type, obj, created: created, livemode: livemode, apiVersion: apiVersion);
        return new StripeEventParser(options ?? Options(), new FixedClock(Now), pack).Parse(body, Signed(body));
    }

    private static readonly DateTime Start = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    // ---- envelope ----

    [Fact]
    public void Envelope_CarriesIdTypeAndCreated()
    {
        BillingEvent e = Parse("invoice.payment_failed", new JsonObject(), created: Now.AddMinutes(-3));
        e.Provider.Should().Be("stripe");
        e.EventId.Should().Be("evt_1");
        e.EventType.Should().Be("invoice.payment_failed");
        e.OccurredAt.Should().Be(Now.AddMinutes(-3));
    }

    [Fact]
    public void HeaderLookup_IsCaseInsensitive()
    {
        string body = Envelope("x.y", new JsonObject());
        var headers = new System.Collections.Generic.Dictionary<string, string> { ["STRIPE-SIGNATURE"] = Signed(body)["stripe-signature"] };
        new StripeEventParser(Options(), new FixedClock(Now)).Parse(body, headers).Kind.Should().Be(BillingEventKind.Ignored);
    }

    [Fact]
    public void LivemodeMismatch_IsRejected()
    {
        FluentActions.Invoking(() => Parse("x.y", new JsonObject(), livemode: true)).Should().Throw<WebhookVerificationException>().WithMessage("livemode mismatch");
        FluentActions.Invoking(() => Parse("x.y", new JsonObject(), Options(live: true), livemode: false)).Should().Throw<WebhookVerificationException>().WithMessage("livemode mismatch");
        Parse("x.y", new JsonObject(), Options(live: true), livemode: true).Kind.Should().Be(BillingEventKind.Ignored);
    }

    [Fact]
    public void WrongApiVersion_IsRejected()
        => FluentActions.Invoking(() => Parse("x.y", new JsonObject(), apiVersion: "2025-03-31.basil"))
            .Should().Throw<WebhookVerificationException>().WithMessage("*2026-09-30.endive*");

    [Fact]
    public void MalformedJson_IsRejected()
    {
        const string body = "{not json";
        FluentActions.Invoking(() => new StripeEventParser(Options(), new FixedClock(Now)).Parse(body, Signed(body)))
            .Should().Throw<WebhookVerificationException>().WithMessage("Malformed webhook body.");
    }

    [Fact]
    public void ValidSignatureButNoEnvelopeFields_IsRejected()
    {
        const string body = """{"hello":1}""";
        FluentActions.Invoking(() => new StripeEventParser(Options(), new FixedClock(Now)).Parse(body, Signed(body)))
            .Should().Throw<WebhookVerificationException>();
    }

    [Fact]
    public void BadSignature_IsRejectedBeforeAnythingIsRead()
    {
        string body = Envelope("x.y", new JsonObject());
        FluentActions.Invoking(() => new StripeEventParser(Options(), new FixedClock(Now)).Parse(body, Signed(body, "whsec_other")))
            .Should().Throw<WebhookVerificationException>().WithMessage("Signature mismatch.");
    }

    [Fact]
    public void RotationSecret_VerifiesThroughTheParser()
    {
        string body = Envelope("x.y", new JsonObject());
        new StripeEventParser(Options(previous: PreviousSecret), new FixedClock(Now)).Parse(body, Signed(body, PreviousSecret)).Kind.Should().Be(BillingEventKind.Ignored);
    }

    [Fact]
    public void DuplicateJsonFields_AreTolerated()
    {
        string body = Envelope("x.y", new JsonObject()).Replace("\"livemode\":false", "\"livemode\":false,\"livemode\":false");
        new StripeEventParser(Options(), new FixedClock(Now)).Parse(body, Signed(body)).Kind.Should().Be(BillingEventKind.Ignored);
    }

    [Fact]
    public void ExtraFields_AreTolerated()
    {
        JsonObject s = Subscription();
        s["something_new"] = new JsonObject { ["deep"] = 1 };
        Parse("customer.subscription.updated", s).Kind.Should().Be(BillingEventKind.SubscriptionChanged);
    }

    // ---- checkout.session ----

    [Fact]
    public void TopUpSession_Paid_MapsToTopUpPaid()
    {
        BillingEvent e = Parse("checkout.session.completed", TopUpSession(discounts: new JsonArray(new JsonObject { ["coupon"] = "co_1", ["promotion_code"] = "promo_1" }), discount: 145, promo: Guid.Parse("22222222-2222-2222-2222-222222222222"), bonus: Guid.Parse("33333333-3333-3333-3333-333333333333")));
        e.Kind.Should().Be(BillingEventKind.TopUpPaid);
        e.OrganizationId.Should().Be(OrgId);
        e.CustomerId.Should().Be("cus_1");
        e.TransactionId.Should().Be("pi_1");
        e.CheckoutId.Should().Be("cs_test_1");
        e.TopUpCredits.Should().Be(1000);
        e.AmountMinor.Should().Be(1450);
        e.IsPaidTransaction.Should().BeTrue();
        e.CurrencyCode.Should().Be("EUR");
        e.DiscountId.Should().Be("promo_1");
        e.DiscountTotal.Should().Be("145");
        e.PromotionId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        e.BonusPromotionId.Should().Be(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    }

    [Fact]
    public void TopUpSession_DiscountFallsBackToTheCoupon_AndAcceptsExpandedObjects()
    {
        Parse("checkout.session.completed", TopUpSession(discounts: new JsonArray(new JsonObject { ["coupon"] = new JsonObject { ["id"] = "co_9" } }))).DiscountId.Should().Be("co_9");
    }

    [Fact]
    public void TopUpSession_OrgIsNeverReadFromClientReferenceId_AndPaymentIntentMayBeExpanded()
    {
        // AUTHZ-IB-6: a Payment Link lets the buyer choose client_reference_id, so only our own metadata names the organization.
        JsonObject s = TopUpSession();
        s["client_reference_id"].Should().NotBeNull();
        s["metadata"] = new JsonObject { ["topup_credits"] = "1000" };
        s["payment_intent"] = new JsonObject { ["id"] = "pi_expanded" };
        BillingEvent e = Parse("checkout.session.async_payment_succeeded", s);
        e.OrganizationId.Should().BeNull();
        e.TransactionId.Should().Be("pi_expanded");
        e.Kind.Should().Be(BillingEventKind.TopUpPaid);
    }

    [Fact]
    public void TopUpSession_Unpaid_AndAsyncFailed_AreIgnoredWithAReason()
    {
        BillingEvent unpaid = Parse("checkout.session.completed", TopUpSession(paymentStatus: "unpaid"));
        unpaid.Kind.Should().Be(BillingEventKind.Ignored);
        unpaid.IgnoredReason.Should().Contain("unpaid");
        Parse("checkout.session.async_payment_failed", TopUpSession(paymentStatus: "unpaid")).IgnoredReason.Should().NotBeNullOrEmpty();
        Parse("checkout.session.expired", TopUpSession(paymentStatus: "unpaid")).Kind.Should().Be(BillingEventKind.Ignored);
    }

    [Fact]
    public void SubscriptionModeSession_IsIgnored()
        => Parse("checkout.session.completed", TopUpSession(mode: "subscription")).IgnoredReason.Should().Contain("customer.subscription.*");

    [Fact]
    public void TopUp_UnconfiguredPack_IsIgnored_ConfiguredPackIsGranted()
    {
        Parse("checkout.session.completed", TopUpSession(credits: 777), pack: c => c == 1000).IgnoredReason.Should().Be("topup_pack_not_configured");
        Parse("checkout.session.completed", TopUpSession(credits: 1000), pack: c => c == 1000).Kind.Should().Be(BillingEventKind.TopUpPaid);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void TopUp_WithAnUnreadableCreditCount_StaysATopUpWithoutCredits_SoTheProcessorRecordsItUnresolved(string credits)
    {
        JsonObject s = TopUpSession();
        s["metadata"]![StripeMetadataKeys.TopUpCredits] = credits;
        BillingEvent e = Parse("checkout.session.completed", s);
        e.Kind.Should().Be(BillingEventKind.TopUpPaid);
        e.TopUpCredits.Should().BeNull();
    }

    // ---- customer.subscription.* ----

    [Fact]
    public void Subscription_BasilPeriodsOnTheItem()
    {
        BillingEvent e = Parse("customer.subscription.created", Subscription(quantity: 3, price: "price_team", start: Start));
        e.Kind.Should().Be(BillingEventKind.SubscriptionChanged);
        e.OrganizationId.Should().Be(OrgId);
        e.CustomerId.Should().Be("cus_1");
        e.SubscriptionId.Should().Be("sub_1");
        e.Status.Should().Be(SubscriptionStatus.Active);
        e.PeriodStart.Should().Be(Start);
        e.PeriodEnd.Should().Be(Start.AddMonths(1));
        e.CancelAtPeriodEnd.Should().BeFalse();
        e.CancelEffectiveAt.Should().BeNull();
        e.Items.Should().BeEquivalentTo([new BillingLineItem("price_team", 3)]);
    }

    [Fact]
    public void Subscription_FallsBackToSubscriptionLevelPeriods()
    {
        BillingEvent e = Parse("customer.subscription.updated", Subscription(start: Start, periodOnItem: false));
        e.PeriodStart.Should().Be(Start);
        e.PeriodEnd.Should().Be(Start.AddMonths(1));
    }

    [Fact]
    public void Subscription_CancelAtPeriodEnd_EffectiveAtTheItemPeriodEnd()
    {
        BillingEvent e = Parse("customer.subscription.updated", Subscription(start: Start, cancelAtPeriodEnd: true));
        e.CancelAtPeriodEnd.Should().BeTrue();
        e.CancelEffectiveAt.Should().Be(Start.AddMonths(1));
    }

    [Fact]
    public void Subscription_CancelAt_IsAScheduledCancellationToo()
    {
        DateTime at = Start.AddDays(40);
        BillingEvent e = Parse("customer.subscription.updated", Subscription(start: Start, cancelAt: at));
        e.CancelAtPeriodEnd.Should().BeTrue();
        e.CancelEffectiveAt.Should().Be(at);
    }

    [Theory]
    [InlineData("active", SubscriptionStatus.Active)]
    [InlineData("trialing", SubscriptionStatus.Trialing)]
    [InlineData("past_due", SubscriptionStatus.PastDue)]
    [InlineData("unpaid", SubscriptionStatus.Paused)]
    [InlineData("paused", SubscriptionStatus.Paused)]
    [InlineData("canceled", SubscriptionStatus.Canceled)]
    [InlineData("incomplete_expired", SubscriptionStatus.Canceled)]
    [InlineData("incomplete", SubscriptionStatus.Paused)]
    public void Subscription_StatusMapping(string status, SubscriptionStatus expected)
        => Parse("customer.subscription.updated", Subscription(status: status)).Status.Should().Be(expected);

    [Fact]
    public void Subscription_UnknownStatus_IsIgnoredNotThrown()
    {
        BillingEvent e = Parse("customer.subscription.updated", Subscription(status: "brand_new_word"));
        e.Kind.Should().Be(BillingEventKind.Ignored);
        e.IgnoredReason.Should().Contain("brand_new_word");
    }

    [Fact]
    public void SubscriptionDeleted_ForcesCanceled()
        => Parse("customer.subscription.deleted", Subscription(status: "active")).Status.Should().Be(SubscriptionStatus.Canceled);

    [Theory]
    [InlineData("customer.subscription.paused")]
    [InlineData("customer.subscription.resumed")]
    public void SubscriptionPausedAndResumed_AreSubscriptionChanges(string type)
        => Parse(type, Subscription(status: type.EndsWith("paused", StringComparison.Ordinal) ? "paused" : "active")).Kind.Should().Be(BillingEventKind.SubscriptionChanged);

    // ---- invoice.paid ----

    [Theory]
    [InlineData("subscription_cycle")]
    [InlineData("subscription_update")]
    public void Invoice_ZeroAmount_IsIgnored(string reason)
        => Parse("invoice.paid", Invoice(reason, 0, Line(0, "price_creator", 1, Start))).IgnoredReason.Should().Be("zero amount invoice");

    [Fact]
    public void Invoice_ZeroAmountFirstInvoice_CompletesTheReservation_AndIsARecordedZeroPayment()
    {
        BillingEvent e = Parse("invoice.paid", Invoice("subscription_create", 0, Line(0, "price_creator", 1, Start)));

        e.Kind.Should().Be(BillingEventKind.TransactionCompleted);
        e.IsPaidTransaction.Should().BeTrue("a 100%-off invoice is a payment of 0 and is recorded");
        e.AmountMinor.Should().Be(0);
        e.FirstSubscriptionInvoice.Should().BeTrue();
        e.SubscriptionId.Should().Be("sub_1");
    }

    [Fact]
    public void Invoice_OtherBillingReasons_AreIgnored()
    {
        foreach (string reason in new[] { "manual", "upcoming", "subscription_threshold" })
        {
            Parse("invoice.paid", Invoice(reason, 500, Line(500, "price_creator", 1, Start))).Kind.Should().Be(BillingEventKind.Ignored);
        }
    }

    [Fact]
    public void Invoice_Upgrade_ThreeLines_CreditLineNeverPairs()
    {
        DateTime proration = Start.AddDays(3);
        BillingEvent e = Parse("invoice.paid", Invoice("subscription_update", 4100,
            Line(-2900, "price_creator", 1, proration),
            Line(7900, "price_pro", 1, proration),
            Line(-900, "price_credit_only", 1, proration.AddHours(1))));
        e.Kind.Should().Be(BillingEventKind.UpgradePaid);
        e.TransactionId.Should().Be("in_1");
        e.SubscriptionId.Should().Be("sub_1");
        e.OrganizationId.Should().Be(OrgId);
        e.CustomerId.Should().Be("cus_1");
        e.Items.Should().BeEquivalentTo([new BillingLineItem("price_pro", 1)]);
        e.ProrationStartsAt.Should().Be(proration);
        e.AmountMinor.Should().Be(4100, "the customer was charged amount_paid, not the sum of the lines");
        e.IsPaidTransaction.Should().BeTrue();
        e.CurrencyCode.Should().Be("EUR");
    }

    [Fact]
    public void Invoice_Upgrade_SumsQuantityPerPrice_AndTakesTheEarliestPositiveLineStart()
    {
        BillingEvent e = Parse("invoice.paid", Invoice("subscription_update", 3000,
            Line(1000, "price_team", 2, Start.AddDays(2)),
            Line(2000, "price_team", 3, Start.AddDays(1))));
        e.Items.Should().BeEquivalentTo([new BillingLineItem("price_team", 5)]);
        e.ProrationStartsAt.Should().Be(Start.AddDays(1));
    }

    [Fact]
    public void Invoice_Upgrade_WithoutPositiveLines_HasNoItems()
    {
        BillingEvent e = Parse("invoice.paid", Invoice("subscription_update", 100, Line(-500, "price_creator", 1, Start)));
        e.Items.Should().BeEmpty();
        e.ProrationStartsAt.Should().BeNull();
    }

    [Fact]
    public void Invoice_SubscriptionIdFallsBackToTheFirstLine()
    {
        JsonObject inv = Invoice("subscription_update", 100, Line(100, "price_pro", 1, Start));
        inv["parent"]!["subscription_details"]!.AsObject().Remove("subscription");
        Parse("invoice.paid", inv).SubscriptionId.Should().Be("sub_1");
    }

    [Fact]
    public void Invoice_DiscountFields()
    {
        JsonObject inv = Invoice("subscription_cycle", 2610, Line(2900, "price_creator", 1, Start));
        inv["total_discount_amounts"] = new JsonArray(new JsonObject { ["amount"] = 200, ["discount"] = "di_1" }, new JsonObject { ["amount"] = 90, ["discount"] = "di_2" });
        inv["discounts"] = new JsonArray("di_1");
        BillingEvent e = Parse("invoice.paid", inv);
        e.DiscountTotal.Should().Be("290");
        e.DiscountId.Should().Be("di_1");
    }

    [Theory]
    [InlineData("subscription_create")]
    [InlineData("subscription_cycle")]
    public void Invoice_CreateAndCycle_AreTransactionCompleted(string reason)
    {
        BillingEvent e = Parse("invoice.paid", Invoice(reason, 2900, Line(2900, "price_creator", 1, Start)));
        e.Kind.Should().Be(BillingEventKind.TransactionCompleted);
        e.PeriodStart.Should().Be(Start);
        e.PeriodEnd.Should().Be(Start.AddMonths(1));
        e.Items.Should().BeEquivalentTo([new BillingLineItem("price_creator", 1)]);
        e.AmountMinor.Should().Be(2900);
        e.TransactionId.Should().Be("in_1");
        e.DiscountId.Should().BeNull();
        e.DiscountTotal.Should().BeNull();
    }

    // ---- refunds and the rest ----

    [Fact]
    public void Refund_Succeeded_IsPaymentRefunded_KeyedByTheRefundId()
    {
        foreach (string type in new[] { "refund.created", "refund.updated" })
        {
            BillingEvent e = Parse(type, Refund());
            e.Kind.Should().Be(BillingEventKind.PaymentRefunded);
            e.AdjustmentId.Should().Be("re_1");
            e.TransactionId.Should().Be("pi_1");
            e.AmountMinor.Should().Be(1450);
            e.CurrencyCode.Should().Be("EUR");
            e.FullRefund.Should().BeFalse("the provider's ResolveAsync decides this after reading the original payment");
        }
    }

    [Theory]
    [InlineData("pending", "refund not final")]
    [InlineData("requires_action", "refund not final")]
    [InlineData("failed", "refund failed; nothing to claw back")]
    [InlineData("canceled", "refund canceled; nothing to claw back")]
    public void Refund_NotSucceeded_IsIgnored(string status, string reason)
    {
        BillingEvent e = Parse("refund.updated", Refund(status));
        e.Kind.Should().Be(BillingEventKind.Ignored);
        e.IgnoredReason.Should().Be(reason);
    }

    [Theory]
    [InlineData("charge.refunded", "handled via refund.created")]
    [InlineData("charge.dispute.created", "dispute opened: no automatic action")]
    [InlineData("charge.dispute.funds_withdrawn", "dispute event charge.dispute.funds_withdrawn is not acted on")]
    [InlineData("charge.dispute.updated", "dispute event charge.dispute.updated is not acted on")]
    public void SpecificIgnoredReasons(string type, string reason)
        => Parse(type, new JsonObject()).IgnoredReason.Should().Be(reason);

    // ---- disputes (DEC-18) ----

    [Fact]
    public void DisputeClosedLost_IsAPaymentRefund_KeyedByTheDisputeId()
    {
        BillingEvent e = Parse("charge.dispute.closed", Dispute());
        e.Kind.Should().Be(BillingEventKind.PaymentRefunded);
        e.AdjustmentId.Should().Be("dp_1");
        e.TransactionId.Should().Be("pi_1");
        e.AmountMinor.Should().Be(1450);
        e.CurrencyCode.Should().Be("EUR");
        e.FullRefund.Should().BeFalse("ResolveAsync decides it from the charge amount");
    }

    [Theory]
    [InlineData("won")]
    [InlineData("warning_closed")]
    [InlineData("needs_response")]
    public void DisputeClosedNotLost_IsIgnored(string status)
    {
        BillingEvent e = Parse("charge.dispute.closed", Dispute(status));
        e.Kind.Should().Be(BillingEventKind.Ignored);
        e.IgnoredReason.Should().Contain(status);
    }

    [Fact]
    public void DisputeClosedLost_WithoutAPaymentIntent_IsIgnoredWithAReason()
    {
        BillingEvent e = Parse("charge.dispute.closed", Dispute(paymentIntent: null));
        e.Kind.Should().Be(BillingEventKind.Ignored);
        e.IgnoredReason.Should().Contain("payment_intent");
    }

    [Fact]
    public void DisputeCreated_IsIgnored_AndLogsAWarningWithIdsOnly()
    {
        CapturingLogger log = new();
        string body = Envelope("charge.dispute.created", Dispute("needs_response"), id: "evt_dispute");
        BillingEvent e = new StripeEventParser(Options(), new FixedClock(Now), null, log).Parse(body, Signed(body));

        e.Kind.Should().Be(BillingEventKind.Ignored);
        log.Entries.Should().ContainSingle();
        (Microsoft.Extensions.Logging.LogLevel level, string text) = log.Entries[0];
        level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning);
        text.Should().Contain("evt_dispute").And.Contain("dp_1").And.NotContain("buyer@example.com");
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Theory]
    [InlineData("invoice.payment_failed")]
    [InlineData("invoice.payment_action_required")]
    [InlineData("checkout.session.expired")]
    [InlineData("customer.updated")]
    [InlineData("some.future.event")]
    public void OtherEvents_AreIgnoredWithAReason_NeverThrow(string type)
    {
        BillingEvent e = Parse(type, new JsonObject());
        e.Kind.Should().Be(BillingEventKind.Ignored);
        e.IgnoredReason.Should().NotBeNullOrWhiteSpace();
    }
}
