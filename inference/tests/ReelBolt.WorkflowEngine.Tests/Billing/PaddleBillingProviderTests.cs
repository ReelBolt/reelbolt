using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Paddle;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.WorkflowEngine.Tests.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>Synthetic Paddle payloads, shaped after the public Paddle Billing webhook and API docs (docs/billing.md).</summary>
internal static class PaddlePayloads
{
    public const string Secret = "pdl_ntfset_test_secret";
    public static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public static string Sign(string body, DateTimeOffset? at = null, string secret = Secret)
    {
        long ts = (at ?? Now).ToUnixTimeSeconds();
        byte[] h = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{ts}:{body}"));
        return $"ts={ts};h1={Convert.ToHexStringLower(h)}";
    }

    public static string Envelope(string id, string type, DateTimeOffset occurred, string data) =>
        $$"""{"event_id":"{{id}}","event_type":"{{type}}","occurred_at":"{{occurred:yyyy-MM-ddTHH:mm:ss.ffffffZ}}","notification_id":"ntf_{{id}}","data":{{data}}}""";

    public static string Subscription(string id, DateTimeOffset occurred, Guid org, string status = "active", string price = "pri_creator",
        int qty = 1, DateTimeOffset? start = null, string? scheduledChange = null, string type = "subscription.updated", string subId = "sub_01")
    {
        DateTimeOffset s = start ?? Now;
        string period = status == "canceled"
            ? "null"
            : $$$"""{"starts_at":"{{{s:yyyy-MM-ddTHH:mm:ss.ffffffZ}}}","ends_at":"{{{s.AddMonths(1):yyyy-MM-ddTHH:mm:ss.ffffffZ}}}"}""";
        string change = scheduledChange is null ? "null" : $$"""{"action":"{{scheduledChange}}","effective_at":"{{s.AddMonths(1):O}}","resume_at":null}""";
        return Envelope(id, type, occurred, $$$"""
            {"id":"{{{subId}}}","status":"{{{status}}}","customer_id":"ctm_01","address_id":"add_01","currency_code":"USD","collection_mode":"automatic",
             "started_at":"{{{Now:O}}}","current_billing_period":{{{period}}},"scheduled_change":{{{change}}},
             "items":[{"status":"active","quantity":{{{qty}}},"recurring":true,"price":{"id":"{{{price}}}","product_id":"pro_01","description":"Monthly"}}],
             "custom_data":{"organization_id":"{{{org}}}","kind":"subscription","plan_key":"creator"}}
            """);
    }

    /// <summary>The real <c>details.totals</c> block Paddle sends on a completed transaction, in minor units.</summary>
    public const string Totals = """{"grand_total":"1450","currency_code":"USD","fee":"123","earnings":"1065","tax":"262","subtotal":"2377","discount":"1189"}""";

    public static string TopUpTransaction(string id, DateTimeOffset occurred, Guid org, string txn = "txn_01", string price = "pri_topup_1000", string? subscriptionId = null, string? totals = null)
    {
        string details = totals is null ? "" : $"\"details\":{{\"totals\":{totals}}},";
        return Envelope(id, "transaction.completed", occurred, $$$"""
            {"id":"{{{txn}}}","status":"completed","customer_id":"ctm_01","subscription_id":{{{(subscriptionId is null ? "null" : $"\"{subscriptionId}\"")}}},
             "origin":"web","currency_code":"USD",{{{details}}}
             "items":[{"price_id":"{{{price}}}","price":{"id":"{{{price}}}","product_id":"pro_02"},"quantity":1}],
             "custom_data":{"organization_id":"{{{org}}}","kind":"topup","credits":1000}}
            """);
    }

    /// <summary>A completed transaction carrying Paddle's top-level <c>discount_id</c> and <c>details.totals.discount</c>.</summary>
    public static string DiscountedTransaction(string id, DateTimeOffset occurred, Guid org, string kind, string price, string? subscriptionId,
        string? discountId, string discountTotal, string customExtra = "", string txn = "txn_01") =>
        Envelope(id, "transaction.completed", occurred, $$$"""
            {"id":"{{{txn}}}","status":"completed","customer_id":"ctm_01","subscription_id":{{{(subscriptionId is null ? "null" : $"\"{subscriptionId}\"")}}},
             "discount_id":{{{(discountId is null ? "null" : $"\"{discountId}\"")}}},
             "origin":"web","currency_code":"EUR",
             "details":{"totals":{"subtotal":"2000","discount":"{{{discountTotal}}}","tax":"0","total":"1600","grand_total":"1450","currency_code":"EUR"}},
             "items":[{"price_id":"{{{price}}}","price":{"id":"{{{price}}}","product_id":"pro_02"},"quantity":1}],
             "custom_data":{"organization_id":"{{{org}}}","kind":"{{{kind}}}"{{{customExtra}}}}}
            """);

    /// <summary>
    /// <c>details.line_items</c> of a Creator → Pro <c>prorated_immediately</c> change, shaped after the
    /// Paddle Billing transaction docs: the removed item is credited (a negative total), the new one charged.
    /// </summary>
    public const string ProrationLineItems = """
        [{"id":"txnitm_01","price_id":"pri_creator","quantity":1,"totals":{"subtotal":"-1450","tax":"0","discount":"0","total":"-1450"},"product":{"id":"pro_01"}},
         {"id":"txnitm_02","price_id":"pri_pro","quantity":1,"totals":{"subtotal":"3950","tax":"0","discount":"0","total":"3950"},"product":{"id":"pro_01"}}]
        """;

    public static string Refund(string id, DateTimeOffset occurred, string adj = "adj_01", string txn = "txn_01", string type = "full", string status = "approved", string action = "refund", string? subscriptionId = null) =>
        Envelope(id, "adjustment.updated", occurred, $$"""
            {"id":"{{adj}}","action":"{{action}}","type":"{{type}}","status":"{{status}}","transaction_id":"{{txn}}","subscription_id":{{(subscriptionId is null ? "null" : $"\"{subscriptionId}\"")}},"customer_id":"ctm_01","reason":"requested by customer"}
            """);

    /// <summary>
    /// The transaction a <c>prorated_immediately</c> subscription PATCH creates (origin
    /// <c>subscription_update</c>). Its items carry the proration window; <paramref name="prorationStart"/>
    /// null leaves <c>proration</c> out. <paramref name="grandTotal"/> is <c>details.totals.grand_total</c>
    /// (minor units, a string as Paddle sends it); null leaves <c>totals</c> out. <paramref name="lineItems"/>
    /// is the JSON array of <c>details.line_items</c> (default: a credit for the removed Creator item and a
    /// positive charge for the new Pro item); null leaves <c>line_items</c> out. Neither: no <c>details</c>.
    /// <paramref name="fee"/> is <c>details.totals.fee</c>; null leaves the fee out.
    /// </summary>
    public static string UpgradeTransaction(string id, DateTimeOffset occurred, Guid org, string type = "transaction.completed", string txn = "txn_up",
        string subId = "sub_01", string origin = "subscription_update", DateTimeOffset? prorationStart = null, string? grandTotal = "2500",
        string? lineItems = ProrationLineItems, string? fee = null)
    {
        List<string> parts = [];
        if (grandTotal is not null)
        {
            string feeEntry = fee is null ? "" : $"\"fee\":\"{fee}\",";
            parts.Add($"\"totals\":{{{feeEntry}\"subtotal\":\"{grandTotal}\",\"tax\":\"0\",\"total\":\"{grandTotal}\",\"grand_total\":\"{grandTotal}\",\"currency_code\":\"USD\"}}");
        }

        if (lineItems is not null)
        {
            parts.Add($"\"line_items\":{lineItems}");
        }

        string details = parts.Count == 0 ? "" : $"\"details\":{{{string.Join(",", parts)}}},";
        string proration = prorationStart is { } ps
            ? $$$"""{"rate":"0.5","billing_period":{"starts_at":"{{{ps:yyyy-MM-ddTHH:mm:ss.ffffffZ}}}","ends_at":"{{{Now.AddMonths(1):yyyy-MM-ddTHH:mm:ss.ffffffZ}}}"}}"""
            : "null";
        string status = type == "transaction.completed" ? "completed" : "past_due";
        return Envelope(id, type, occurred, $$$"""
            {"id":"{{{txn}}}","status":"{{{status}}}","customer_id":"ctm_01","subscription_id":"{{{subId}}}",
             "origin":"{{{origin}}}","currency_code":"USD",{{{details}}}
             "items":[{"price":{"id":"pri_pro","product_id":"pro_01"},"quantity":1,"proration":{{{proration}}}}],
             "custom_data":{"organization_id":"{{{org}}}","kind":"subscription"}}
            """);
    }

    /// <summary>A plain <c>transaction.completed</c> for a subscription: a subscription id, no discount id.</summary>
    public static string PlainSubscriptionTransaction(string id, DateTimeOffset occurred, Guid org, string txn = "txn_sub", string grandTotal = "1450") =>
        Envelope(id, "transaction.completed", occurred, $$$"""
            {"id":"{{{txn}}}","status":"completed","customer_id":"ctm_01","subscription_id":"sub_01",
             "origin":"web","currency_code":"USD",
             "details":{"totals":{"grand_total":"{{{grandTotal}}}","currency_code":"USD","fee":"123"}},
             "items":[{"price_id":"pri_pro","price":{"id":"pri_pro","product_id":"pro_01"},"quantity":1}],
             "custom_data":{"organization_id":"{{{org}}}","kind":"subscription"}}
            """);

    public static PaddleBillingProvider Provider(HttpMessageHandler? handler = null, string environment = "sandbox", int tolerance = 5, DateTimeOffset? now = null,
        bool discountsEnabledForCheckout = false, ILogger<PaddleBillingProvider> logger = null)
    {
        PaddleOptions o = new()
        {
            ApiKey = "pdl_sdbx_apikey_test",
            WebhookSecret = Secret,
            Environment = environment,
            ToleranceSeconds = tolerance,
            DiscountsEnabledForCheckout = discountsEnabledForCheckout,
            TopUpPriceIds = new Dictionary<string, string> { ["500"] = "pri_topup_500", ["1000"] = "pri_topup_1000", ["3000"] = "" }
        };
        return new PaddleBillingProvider(new HttpClient(handler ?? new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))), Options.Create(o), new FixedTime(now ?? Now), logger);
    }

    public sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Auth, string? Version, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Paddle-Version", out IEnumerable<string>? v) ? v.Single() : null, body));
            return respond(request);
        }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class PaddleBillingProviderTests
{
    private static readonly Guid Org = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = PaddlePayloads.Now;

    private static IReadOnlyDictionary<string, string> Headers(string sig) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Paddle-Signature"] = sig };

    private static BillingEvent Parse(string body) => PaddlePayloads.Provider().VerifyAndParseWebhook(body, Headers(PaddlePayloads.Sign(body)));

    // ------------------------------------------------------------------ signature

    [Fact]
    public void ValidSignature_Parses()
    {
        string body = PaddlePayloads.Subscription("evt_1", Now, Org);

        BillingEvent evt = PaddlePayloads.Provider().VerifyAndParseWebhook(body, Headers(PaddlePayloads.Sign(body)));

        evt.EventId.Should().Be("evt_1");
        evt.Provider.Should().Be("paddle");
    }

    [Fact]
    public void SignatureCoversTheExactRawBody_AnyByteChangeIsRejected()
    {
        string body = PaddlePayloads.Subscription("evt_1", Now, Org);
        string sig = PaddlePayloads.Sign(body);

        Action tampered = () => PaddlePayloads.Provider().VerifyAndParseWebhook(body.Replace("active", "actives"), Headers(sig));
        Action whitespace = () => PaddlePayloads.Provider().VerifyAndParseWebhook(body + " ", Headers(sig));

        tampered.Should().Throw<WebhookVerificationException>();
        whitespace.Should().Throw<WebhookVerificationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("ts=abc;h1=00")]
    [InlineData("ts=1759752000")]
    [InlineData("h1=deadbeef")]
    [InlineData("ts=1759752000;h1=not-hex")]
    public void MissingOrMalformedHeader_IsRejected(string? header)
    {
        string body = PaddlePayloads.Subscription("evt_1", Now, Org);

        Action act = () => PaddlePayloads.Provider().VerifyAndParseWebhook(body, header is null ? new Dictionary<string, string>() : Headers(header));

        act.Should().Throw<WebhookVerificationException>();
    }

    [Fact]
    public void WrongSecret_IsRejected()
    {
        string body = PaddlePayloads.Subscription("evt_1", Now, Org);

        Action act = () => PaddlePayloads.Provider().VerifyAndParseWebhook(body, Headers(PaddlePayloads.Sign(body, secret: "someone_else")));

        act.Should().Throw<WebhookVerificationException>();
    }

    [Fact]
    public void StaleTimestamp_IsRejected_ToBoundReplay_EvenWithAValidHmac()
    {
        string body = PaddlePayloads.Subscription("evt_1", Now, Org);
        string old = PaddlePayloads.Sign(body, Now.AddSeconds(-60));

        Action act = () => PaddlePayloads.Provider().VerifyAndParseWebhook(body, Headers(old));
        Action withinTolerance = () => PaddlePayloads.Provider(tolerance: 120).VerifyAndParseWebhook(body, Headers(old));

        act.Should().Throw<WebhookVerificationException>().WithMessage("*tolerance*");
        withinTolerance.Should().NotThrow();
    }

    [Fact]
    public void SecretRotation_AnyOfSeveralH1Values_Matches()
    {
        string body = PaddlePayloads.Subscription("evt_1", Now, Org);
        string good = PaddlePayloads.Sign(body);
        string rotated = PaddlePayloads.Sign(body, secret: "the_previous_secret").Replace("ts=", "ts=", StringComparison.Ordinal);
        string headerValue = rotated + ";" + good[good.IndexOf("h1=", StringComparison.Ordinal)..];

        Action act = () => PaddlePayloads.Provider().VerifyAndParseWebhook(body, Headers(headerValue));

        act.Should().NotThrow();
    }

    [Fact]
    public void NoWebhookSecretConfigured_RejectsEverything()
    {
        string body = PaddlePayloads.Subscription("evt_1", Now, Org);
        PaddleBillingProvider p = new(new HttpClient(), Options.Create(new PaddleOptions { ApiKey = "k" }), new PaddlePayloads.FixedTime(Now));

        p.IsConfigured.Should().BeFalse();
        Action act = () => p.VerifyAndParseWebhook(body, Headers(PaddlePayloads.Sign(body)));
        act.Should().Throw<WebhookVerificationException>();
    }

    [Fact]
    public void ValidSignatureOverAMalformedBody_IsRejectedNotCrashed()
    {
        const string body = "{\"hello\":1}";

        Action act = () => PaddlePayloads.Provider().VerifyAndParseWebhook(body, Headers(PaddlePayloads.Sign(body)));

        act.Should().Throw<WebhookVerificationException>().WithMessage("*Malformed*");
    }

    // ------------------------------------------------------------------ parsing

    [Fact]
    public void SubscriptionCreated_MapsOrgPeriodItemsAndStatus()
    {
        BillingEvent e = Parse(PaddlePayloads.Subscription("evt_1", Now, Org, type: "subscription.created", price: "pri_team", qty: 5));

        e.Kind.Should().Be(BillingEventKind.SubscriptionChanged);
        e.OrganizationId.Should().Be(Org);
        e.CustomerId.Should().Be("ctm_01");
        e.SubscriptionId.Should().Be("sub_01");
        e.Status.Should().Be(SubscriptionStatus.Active);
        e.PeriodStart.Should().Be(Now.UtcDateTime);
        e.PeriodEnd.Should().Be(Now.AddMonths(1).UtcDateTime);
        e.OccurredAt.Should().Be(Now.UtcDateTime);
        e.Items.Should().ContainSingle().Which.Should().Be(new BillingLineItem("pri_team", 5));
        e.CancelAtPeriodEnd.Should().BeFalse();
    }

    [Theory]
    [InlineData("active", SubscriptionStatus.Active)]
    [InlineData("trialing", SubscriptionStatus.Trialing)]
    [InlineData("past_due", SubscriptionStatus.PastDue)]
    [InlineData("paused", SubscriptionStatus.Paused)]
    [InlineData("canceled", SubscriptionStatus.Canceled)]
    public void SubscriptionStatus_IsMapped(string paddle, SubscriptionStatus expected)
        => Parse(PaddlePayloads.Subscription("evt_1", Now, Org, status: paddle)).Status.Should().Be(expected);

    /// <summary>G-4: an unknown, empty or missing status (a malformed but signed payload) is never Active, so no paid-upgrade grant gate opens on it.</summary>
    [Theory]
    [InlineData("frozen")]
    [InlineData("ACTIVE")]
    [InlineData("")]
    [InlineData(null)]
    public void SubscriptionStatus_UnknownOrMissing_IsNotActive(string? paddle)
    {
        string body = PaddlePayloads.Subscription("evt_1", Now, Org, status: paddle ?? "drop");
        if (paddle is null)
        {
            body = body.Replace("\"status\":\"drop\",", "", StringComparison.Ordinal);
        }

        Parse(body).Status.Should().NotBe(SubscriptionStatus.Active);
    }

    [Fact]
    public void CanceledSubscription_HasNoBillingPeriod_AndStillParses()
    {
        BillingEvent e = Parse(PaddlePayloads.Subscription("evt_1", Now, Org, status: "canceled", type: "subscription.canceled"));

        e.Status.Should().Be(SubscriptionStatus.Canceled);
        e.PeriodStart.Should().BeNull();
    }

    [Fact]
    public void ScheduledCancel_SetsCancelAtPeriodEnd()
        => Parse(PaddlePayloads.Subscription("evt_1", Now, Org, scheduledChange: "cancel")).CancelAtPeriodEnd.Should().BeTrue();

    [Fact]
    public void TopUpTransaction_ResolvesThePackFromTheConfiguredPriceId_NotFromCustomData()
    {
        BillingEvent e = Parse(PaddlePayloads.TopUpTransaction("evt_t", Now, Org, price: "pri_topup_500"));

        e.Kind.Should().Be(BillingEventKind.TopUpPaid);
        e.TopUpCredits.Should().Be(500, "custom_data says 1000 but the paid price is the 500 pack");
        e.TransactionId.Should().Be("txn_01");
        e.OrganizationId.Should().Be(Org);
    }

    [Fact]
    public void TransactionThatIsNotAConfiguredTopUp_OrBelongsToASubscription_IsIgnored()
    {
        Parse(PaddlePayloads.TopUpTransaction("evt_a", Now, Org, price: "pri_unknown")).Kind.Should().Be(BillingEventKind.Ignored);
        Parse(PaddlePayloads.TopUpTransaction("evt_b", Now, Org, subscriptionId: "sub_01")).Kind.Should().Be(BillingEventKind.Ignored);
    }

    [Fact]
    public void ApprovedRefundAdjustment_IsARefund_PendingOrNonRefundIsNot()
    {
        BillingEvent full = Parse(PaddlePayloads.Refund("evt_r", Now));
        full.Kind.Should().Be(BillingEventKind.PaymentRefunded);
        full.FullRefund.Should().BeTrue();
        full.TransactionId.Should().Be("txn_01");
        full.AdjustmentId.Should().Be("adj_01");
        full.SubscriptionId.Should().BeNull("a top-up refund names no subscription");

        Parse(PaddlePayloads.Refund("evt_p", Now, type: "partial")).FullRefund.Should().BeFalse();
        Parse(PaddlePayloads.Refund("evt_q", Now, status: "pending_approval")).Kind.Should().Be(BillingEventKind.Ignored);
    }

    /// <summary>F-1 re-review: a refund of an upgrade payment names its subscription, so its clawback is serialised with that subscription's events.</summary>
    [Fact]
    public void RefundOfASubscriptionPayment_CarriesTheSubscriptionId()
    {
        Parse(PaddlePayloads.Refund("evt_rs", Now, subscriptionId: "sub_01")).SubscriptionId.Should().Be("sub_01");
    }

    [Fact]
    public void ApprovedChargebackAdjustment_IsARefund()
    {
        BillingEvent e = Parse(PaddlePayloads.Refund("evt_cb", Now, adj: "adj_cb", action: "chargeback"));

        e.Kind.Should().Be(BillingEventKind.PaymentRefunded);
        e.FullRefund.Should().BeTrue();
        e.AdjustmentId.Should().Be("adj_cb");
        Parse(PaddlePayloads.Refund("evt_cr", Now, action: "chargeback_reverse")).Kind.Should().Be(BillingEventKind.Ignored);
        Parse(PaddlePayloads.Refund("evt_cw", Now, action: "chargeback_warning")).Kind.Should().Be(BillingEventKind.Ignored);
    }

    [Fact]
    public void CompletedSubscriptionUpdateTransaction_IsAnUpgradePayment_WithItsProrationStart()
    {
        DateTimeOffset changedAt = Now.AddDays(10);
        BillingEvent e = Parse(PaddlePayloads.UpgradeTransaction("evt_u", changedAt.AddMinutes(1), Org, prorationStart: changedAt));

        e.Kind.Should().Be(BillingEventKind.UpgradePaid);
        e.SubscriptionId.Should().Be("sub_01");
        e.TransactionId.Should().Be("txn_up");
        e.OrganizationId.Should().Be(Org);
        e.CustomerId.Should().Be("ctm_01");
        e.ProrationStartsAt.Should().Be(changedAt.UtcDateTime);
    }

    /// <summary>R-2: the upgrade payment carries the items Paddle charged positively (the new price and quantity), not the credited ones.</summary>
    [Fact]
    public void UpgradePayment_CarriesThePositivelyChargedLineItems()
    {
        BillingEvent e = Parse(PaddlePayloads.UpgradeTransaction("evt_u", Now, Org, lineItems: """
            [{"price_id":"pri_team","quantity":3,"totals":{"total":"-2000"}},
             {"price_id":"pri_team","quantity":5,"totals":{"total":"3300"}},
             {"price_id":"pri_addon","quantity":2,"totals":{"total":"0"}}]
            """));

        e.Kind.Should().Be(BillingEventKind.UpgradePaid);
        e.Items.Should().Equal(new BillingLineItem("pri_team", 5));
        Parse(PaddlePayloads.UpgradeTransaction("evt_d", Now, Org)).Items.Should().Equal(new BillingLineItem("pri_pro", 1));
    }

    /// <summary>R-2 fail closed: no line items, none charged above 0, or totals missing/unparseable → no items, so the payment never pairs.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("""[{"price_id":"pri_pro","quantity":1,"totals":{"total":"-5"}}]""")]
    [InlineData("""[{"price_id":"pri_pro","quantity":1}]""")]
    [InlineData("""[{"price_id":"pri_pro","quantity":1,"totals":{"total":"12.50"}}]""")]
    [InlineData("""[{"quantity":1,"totals":{"total":"300"}}]""")]
    [InlineData("""[{"price_id":"pri_pro","quantity":0,"totals":{"total":"300"}}]""")]
    [InlineData("""{"price_id":"pri_pro"}""")]
    public void UpgradePayment_WithoutUsablePositiveLineItems_CarriesNoItems(string? lineItems)
        => Parse(PaddlePayloads.UpgradeTransaction("evt_u", Now, Org, lineItems: lineItems)).Items.Should().BeEmpty();

    [Fact]
    public void UpgradePayment_WithoutProration_HasNoProrationStart()
        => Parse(PaddlePayloads.UpgradeTransaction("evt_u", Now, Org)).ProrationStartsAt.Should().BeNull();

    [Fact]
    public void FailedUpgradePayment_AndRenewalTransactions_AreIgnored()
    {
        Parse(PaddlePayloads.UpgradeTransaction("evt_f", Now, Org, type: "transaction.payment_failed", prorationStart: Now)).Kind.Should().Be(BillingEventKind.Ignored);
        Parse(PaddlePayloads.UpgradeTransaction("evt_r", Now, Org, origin: "subscription_recurring")).Kind.Should().Be(BillingEventKind.Ignored);
    }

    /// <summary>F-3: the charged amount (<c>details.totals.grand_total</c>, minor units) travels with the upgrade payment.</summary>
    [Fact]
    public void UpgradePayment_CarriesTheChargedGrandTotal_InMinorUnits()
    {
        BillingEvent e = Parse(PaddlePayloads.UpgradeTransaction("evt_u", Now, Org, grandTotal: "2500"));

        e.Kind.Should().Be(BillingEventKind.UpgradePaid);
        e.AmountMinor.Should().Be(2500);
    }

    /// <summary>F-3: a missing, empty or unparseable grand total is read as 0, so the payment can never grant (fail closed).</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("12.50")]
    [InlineData("abc")]
    [InlineData("-300")]
    public void UpgradePayment_WithoutAPositiveParseableGrandTotal_HasAZeroAmount(string? grandTotal)
    {
        BillingEvent e = Parse(PaddlePayloads.UpgradeTransaction("evt_u", Now, Org, grandTotal: grandTotal));

        e.Kind.Should().Be(BillingEventKind.UpgradePaid);
        e.AmountMinor.Should().Be(0);
    }

    /// <summary>F-3: only the proration of a subscription PATCH is an upgrade payment; a renewal, a one-time charge and a top-up are not.</summary>
    [Fact]
    public void RenewalOneTimeChargeAndTopUpTransactions_AreNotUpgradePayments()
    {
        Parse(PaddlePayloads.UpgradeTransaction("evt_r", Now, Org, origin: "subscription_recurring", grandTotal: "7900")).Kind.Should().Be(BillingEventKind.Ignored);
        Parse(PaddlePayloads.UpgradeTransaction("evt_c", Now, Org, origin: "subscription_charge", grandTotal: "500")).Kind.Should().Be(BillingEventKind.Ignored);
        Parse(PaddlePayloads.UpgradeTransaction("evt_w", Now, Org, origin: "web", grandTotal: "500")).Kind.Should().Be(BillingEventKind.Ignored);
        Parse(PaddlePayloads.TopUpTransaction("evt_t", Now, Org)).Kind.Should().Be(BillingEventKind.TopUpPaid);
    }

    // ------------------------------------------------------------------ paid amount, currency and fee

    [Fact]
    public void A_completed_top_up_carries_its_paid_amount_currency_and_fee()
    {
        BillingEvent e = Parse(PaddlePayloads.TopUpTransaction("evt_t", Now, Org, totals: PaddlePayloads.Totals));

        e.Kind.Should().Be(BillingEventKind.TopUpPaid);
        e.AmountMinor.Should().Be(1450);
        e.CurrencyCode.Should().Be("USD");
        e.FeeMinor.Should().Be(123);
        e.IsPaidTransaction.Should().BeTrue();
    }

    [Fact]
    public void A_completed_upgrade_carries_its_paid_amount_currency_and_fee()
    {
        BillingEvent e = Parse(PaddlePayloads.UpgradeTransaction("evt_u", Now, Org, grandTotal: "1450", fee: "123"));

        e.Kind.Should().Be(BillingEventKind.UpgradePaid);
        e.AmountMinor.Should().Be(1450);
        e.CurrencyCode.Should().Be("USD");
        e.FeeMinor.Should().Be(123);
        e.IsPaidTransaction.Should().BeTrue();
    }

    [Fact]
    public void A_plain_subscription_payment_carries_its_paid_amount()
    {
        BillingEvent e = Parse(PaddlePayloads.PlainSubscriptionTransaction("evt_s", Now, Org));

        e.Kind.Should().Be(BillingEventKind.Ignored, "a plain subscription payment still takes no credit action");
        e.IsPaidTransaction.Should().BeTrue();
        e.AmountMinor.Should().Be(1450);
    }

    [Fact]
    public void A_discounted_subscription_payment_carries_its_paid_amount()
    {
        BillingEvent e = Parse(PaddlePayloads.DiscountedTransaction("evt_d", Now, Org, "subscription", "pri_pro", "sub_01", "dsc_01", "400"));

        e.Kind.Should().Be(BillingEventKind.TransactionCompleted);
        e.IsPaidTransaction.Should().BeTrue();
        e.AmountMinor.Should().Be(1450);
    }

    [Fact]
    public void A_completed_transaction_without_totals_states_no_amount()
    {
        BillingEvent e = Parse(PaddlePayloads.TopUpTransaction("evt_n", Now, Org));

        e.AmountMinor.Should().Be(0);
        e.FeeMinor.Should().BeNull();
        e.IsPaidTransaction.Should().BeFalse("an absent amount is never read as $0");
    }

    [Fact]
    public void Card_details_are_never_read()
    {
        string withTotals = PaddlePayloads.TopUpTransaction("evt_c", Now, Org, totals: PaddlePayloads.Totals);
        string withCard = withTotals.Replace("\"customer_id\":\"ctm_01\",",
            "\"customer_id\":\"ctm_01\",\"method_details\":{\"card\":{\"type\":\"visa\",\"last4\":\"4242\",\"expiry_month\":\"01\",\"expiry_year\":\"2030\",\"cardholder_name\":\"A Buyer\"}},",
            StringComparison.Ordinal);

        withCard.Should().NotBe(withTotals, "the two payloads must differ or the test proves nothing");
        // A record's list members compare by reference, so equality is asserted structurally: every field,
        // including the parsed items, must match. A card read into any field would make them differ.
        Parse(withCard).Should().BeEquivalentTo(Parse(withTotals));
    }

    [Theory]
    [InlineData("USD", "1450")]
    [InlineData("JPY", "1450")]
    public void An_amount_is_stored_as_the_exact_minor_units_the_provider_sent(string currency, string grandTotal)
    {
        string totals = $$$"""{"grand_total":"{{{grandTotal}}}","currency_code":"{{{currency}}}","fee":"123"}""";

        BillingEvent e = Parse(PaddlePayloads.TopUpTransaction("evt_a", Now, Org, totals: totals));

        e.AmountMinor.Should().Be(1450, "minor units exactly as sent: never divided, never rescaled per currency");
    }

    /// <summary>
    /// The live path must read a JSON number exactly as the jsonb backfill does (<c>#&gt;&gt;</c> renders a
    /// number to its text form), or a numeric amount would be recorded by the backfill and dropped here.
    /// </summary>
    [Fact]
    public void A_numeric_amount_is_read_the_same_as_a_string_one()
    {
        string totals = """{"grand_total":1450,"currency_code":"USD","fee":123}""";

        BillingEvent e = Parse(PaddlePayloads.TopUpTransaction("evt_n", Now, Org, totals: totals));

        e.AmountMinor.Should().Be(1450);
        e.IsPaidTransaction.Should().BeTrue();
    }

    [Fact]
    public void A_numeric_fee_is_read_not_dropped()
    {
        string totals = """{"grand_total":"1450","currency_code":"USD","fee":123}""";

        Parse(PaddlePayloads.TopUpTransaction("evt_f", Now, Org, totals: totals)).FeeMinor.Should().Be(123);
    }

    [Fact]
    public void ApprovedChargeback_IsAFullRefund_EvenWhenTypedPartial()
    {
        // T-011 S-10: a chargeback returns the money to the buyer, so the top-up and its bonus go with it.
        foreach (string type in new[] { "full", "partial" })
        {
            BillingEvent chargeback = Parse(PaddlePayloads.Refund("evt_cb_" + type, Now, adj: "adj_cb", type: type, action: "chargeback"));
            chargeback.Kind.Should().Be(BillingEventKind.PaymentRefunded);
            chargeback.FullRefund.Should().BeTrue();
            chargeback.TransactionId.Should().Be("txn_01");
            chargeback.AdjustmentId.Should().Be("adj_cb");
        }

        Parse(PaddlePayloads.Refund("evt_cb_p", Now, status: "pending_approval", action: "chargeback")).Kind.Should().Be(BillingEventKind.Ignored);
    }

    [Fact]
    public void ChargebackReverse_IsIgnoredWithAReason()
    {
        BillingEvent reverse = Parse(PaddlePayloads.Refund("evt_cbr", Now, action: "chargeback_reverse"));

        reverse.Kind.Should().Be(BillingEventKind.Ignored);
        reverse.IgnoredReason.Should().Contain("chargeback_reverse");
    }

    [Fact]
    public void UnrelatedEventTypes_AreIgnoredButParsed()
        => Parse(PaddlePayloads.Envelope("evt_c", "customer.created", Now, """{"id":"ctm_01"}""")).Kind.Should().Be(BillingEventKind.Ignored);

    // ------------------------------------------------------------------ API calls

    [Fact]
    public async Task Checkout_Plan_PostsATransactionWithOrgCustomData_AndReturnsTheCheckoutUrl()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"data":{"id":"txn_77","checkout":{"url":"https://pay.example.com/?_ptxn=txn_77"}}}"""));
        Plan team = new() { Key = "team", Kind = PlanKind.PerSeat, MinSeats = 3, ProviderPriceIdsJson = """{"paddle":{"monthly":"pri_team"}}""" };

        CheckoutSession s = await PaddlePayloads.Provider(h).CreateCheckoutAsync(
            new CheckoutRequest(Org, Guid.NewGuid(), "a@b.c", team, 5, null, "ctm_01"), default);

        s.Url.Should().Contain("_ptxn=txn_77");
        s.TransactionId.Should().Be("txn_77");
        var call = h.Calls.Single();
        call.Method.Should().Be(HttpMethod.Post);
        call.Url.Should().Be("https://sandbox-api.paddle.com/transactions");
        call.Auth.Should().Be("Bearer pdl_sdbx_apikey_test");
        call.Version.Should().Be("1");
        using JsonDocument body = JsonDocument.Parse(call.Body);
        JsonElement root = body.RootElement;
        root.GetProperty("items")[0].GetProperty("price_id").GetString().Should().Be("pri_team");
        root.GetProperty("items")[0].GetProperty("quantity").GetInt32().Should().Be(5);
        root.GetProperty("customer_id").GetString().Should().Be("ctm_01");
        root.GetProperty("custom_data").GetProperty("organization_id").GetString().Should().Be(Org.ToString());
        root.GetProperty("custom_data").GetProperty("kind").GetString().Should().Be("subscription");
    }

    [Fact]
    public async Task Checkout_TopUp_UsesTheConfiguredPackPrice_AndMarksCustomData()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"data":{"id":"txn_78","checkout":{"url":"https://pay.example.com/?_ptxn=txn_78"}}}"""));

        await PaddlePayloads.Provider(h).CreateCheckoutAsync(new CheckoutRequest(Org, Guid.NewGuid(), null, null, 1, 500, null), default);

        using JsonDocument body = JsonDocument.Parse(h.Calls.Single().Body);
        body.RootElement.GetProperty("items")[0].GetProperty("price_id").GetString().Should().Be("pri_topup_500");
        body.RootElement.GetProperty("custom_data").GetProperty("kind").GetString().Should().Be("topup");
        body.RootElement.TryGetProperty("customer_id", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Checkout_WithoutADefaultPaymentLink_FailsLoudly()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"data":{"id":"txn_79","checkout":{"url":null}}}"""));

        Func<Task> act = () => PaddlePayloads.Provider(h).CreateCheckoutAsync(new CheckoutRequest(Org, Guid.NewGuid(), null, null, 1, 500, null), default);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*payment link*");
    }

    [Fact]
    public async Task ApiError_SurfacesAsHttpRequestException_WithoutTheKey()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"error":{"code":"forbidden"}}""", HttpStatusCode.Forbidden));

        Func<Task> act = () => PaddlePayloads.Provider(h).CreateCheckoutAsync(new CheckoutRequest(Org, Guid.NewGuid(), null, null, 1, 500, null), default);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("403").And.NotContain("pdl_sdbx");
    }

    [Fact]
    public async Task Live_UsesTheLiveBaseUrl()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"data":{"urls":{"general":{"overview":"https://portal"}}}}"""));

        await PaddlePayloads.Provider(h, environment: "live").CreatePortalSessionAsync("ctm_01", null, default);

        h.Calls.Single().Url.Should().StartWith("https://api.paddle.com/");
    }

    [Fact]
    public async Task Portal_PostsToTheCustomersPortalSessions_ScopedToTheSubscription()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"data":{"id":"cpls_1","urls":{"general":{"overview":"https://customer-portal.paddle.com/cpl_1?token=abc"},"subscriptions":[]}}}"""));

        PortalSession session = await PaddlePayloads.Provider(h).CreatePortalSessionAsync("ctm_01", "sub_01", default);

        session.OverviewUrl.Should().StartWith("https://customer-portal.paddle.com/");
        h.Calls.Single().Url.Should().Be("https://sandbox-api.paddle.com/customers/ctm_01/portal-sessions");
        using JsonDocument body = JsonDocument.Parse(h.Calls.Single().Body);
        body.RootElement.GetProperty("subscription_ids")[0].GetString().Should().Be("sub_01");
    }

    // ------------------------------------------------------------------ cancel / resume / change plan

    [Fact]
    public async Task Cancel_PostsToTheCancelEndpoint_EffectiveFromTheNextBillingPeriod()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"data":{"id":"sub_01"}}"""));

        await PaddlePayloads.Provider(h).CancelSubscriptionAsync("sub_01", default);

        var call = h.Calls.Single();
        call.Method.Should().Be(HttpMethod.Post);
        call.Url.Should().Be("https://sandbox-api.paddle.com/subscriptions/sub_01/cancel");
        using JsonDocument body = JsonDocument.Parse(call.Body);
        body.RootElement.GetProperty("effective_from").GetString().Should().Be("next_billing_period");
    }

    [Fact]
    public async Task Resume_PatchesTheSubscription_WithAnExplicitNullScheduledChange()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"data":{"id":"sub_01"}}"""));

        await PaddlePayloads.Provider(h).ResumeSubscriptionAsync("sub_01", default);

        var call = h.Calls.Single();
        call.Method.Should().Be(HttpMethod.Patch);
        call.Url.Should().Be("https://sandbox-api.paddle.com/subscriptions/sub_01");
        call.Body.Replace(" ", "").Should().Contain("\"scheduled_change\":null");
    }

    [Theory]
    [InlineData(PlanChangeTiming.Immediately, "prorated_immediately")]
    [InlineData(PlanChangeTiming.NextBillingPeriod, "full_next_billing_period")]
    public async Task ChangePlan_PatchesTheItems_WithTheProrationModeOfItsTiming(PlanChangeTiming timing, string mode)
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"data":{"id":"sub_01"}}"""));

        await PaddlePayloads.Provider(h).ChangePlanAsync("sub_01", [new BillingLineItem("pri_pro", 1)], timing, PlanChangeDiscount.Unchanged, default);

        var call = h.Calls.Single();
        call.Method.Should().Be(HttpMethod.Patch);
        call.Url.Should().Be("https://sandbox-api.paddle.com/subscriptions/sub_01");
        using JsonDocument body = JsonDocument.Parse(call.Body);
        body.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
        body.RootElement.GetProperty("items")[0].GetProperty("price_id").GetString().Should().Be("pri_pro");
        body.RootElement.GetProperty("items")[0].GetProperty("quantity").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("proration_billing_mode").GetString().Should().Be(mode);
    }

    [Fact]
    public async Task ChangePlan_RemoveDiscount_SendsNullInTheSamePatch()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"data":{"id":"sub_01"}}"""));

        await PaddlePayloads.Provider(h).ChangePlanAsync("sub_01", [new BillingLineItem("pri_team", 1)], PlanChangeTiming.Immediately, PlanChangeDiscount.Remove, default);

        var call = h.Calls.Single();
        call.Method.Should().Be(HttpMethod.Patch);
        call.Url.Should().Be("https://sandbox-api.paddle.com/subscriptions/sub_01");
        using JsonDocument body = JsonDocument.Parse(call.Body);
        body.RootElement.TryGetProperty("discount", out JsonElement discount).Should().BeTrue();
        discount.ValueKind.Should().Be(JsonValueKind.Null);
        body.RootElement.GetProperty("items")[0].GetProperty("price_id").GetString().Should().Be("pri_team");
        body.RootElement.GetProperty("proration_billing_mode").GetString().Should().Be("prorated_immediately");
    }

    [Fact]
    public async Task ChangePlan_Unchanged_OmitsDiscount()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"data":{"id":"sub_01"}}"""));

        await PaddlePayloads.Provider(h).ChangePlanAsync("sub_01", [new BillingLineItem("pri_pro", 1)], PlanChangeTiming.NextBillingPeriod, PlanChangeDiscount.Unchanged, default);

        using JsonDocument body = JsonDocument.Parse(h.Calls.Single().Body);
        body.RootElement.TryGetProperty("discount", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Portal_ReturnsTheDeepLinksOfTheMatchingSubscription()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""
            {"data":{"id":"cpls_1","urls":{"general":{"overview":"https://portal/overview"},"subscriptions":[
              {"id":"sub_other","cancel_subscription":"https://portal/other/cancel","update_subscription_payment_method":"https://portal/other/pm"},
              {"id":"sub_01","cancel_subscription":"https://portal/sub_01/cancel","update_subscription_payment_method":"https://portal/sub_01/pm"}]}}}
            """));

        PortalSession s = await PaddlePayloads.Provider(h).CreatePortalSessionAsync("ctm_01", "sub_01", default);

        s.OverviewUrl.Should().Be("https://portal/overview");
        s.CancelUrl.Should().Be("https://portal/sub_01/cancel");
        s.UpdatePaymentMethodUrl.Should().Be("https://portal/sub_01/pm");
    }

    [Fact]
    public async Task Portal_ToleratesMissingDeepLinks()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"data":{"urls":{"general":{"overview":"https://portal/overview"}}}}"""));

        PortalSession s = await PaddlePayloads.Provider(h).CreatePortalSessionAsync("ctm_01", "sub_01", default);

        s.OverviewUrl.Should().Be("https://portal/overview");
        s.CancelUrl.Should().BeNull();
        s.UpdatePaymentMethodUrl.Should().BeNull();
    }

    [Fact]
    public async Task Checkout_ReturnsTheCustomerItCreated()
    {
        PaddlePayloads.StubHandler h = new(req => req.RequestUri!.AbsolutePath == "/customers"
            ? PaddlePayloads.Json("""{"data":{"id":"ctm_new"}}""")
            : PaddlePayloads.Json("""{"data":{"id":"txn_80","checkout":{"url":"https://pay.example.com/?_ptxn=txn_80"}}}"""));
        Plan creator = new() { Key = "creator", Kind = PlanKind.Subscription, ProviderPriceIdsJson = """{"paddle":{"monthly":"pri_creator"}}""" };

        CheckoutSession s = await PaddlePayloads.Provider(h).CreateCheckoutAsync(new CheckoutRequest(Org, Guid.NewGuid(), "a@b.c", creator, 1, null, null), default);

        s.CustomerId.Should().Be("ctm_new");
    }

    [Fact]
    public async Task Checkout_ReturnsTheExistingCustomer_WhenOneWasGiven()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"data":{"id":"txn_81","checkout":{"url":"https://pay.example.com/?_ptxn=txn_81"}}}"""));

        CheckoutSession s = await PaddlePayloads.Provider(h).CreateCheckoutAsync(new CheckoutRequest(Org, Guid.NewGuid(), null, null, 1, 500, "ctm_01"), default);

        s.CustomerId.Should().Be("ctm_01");
    }

    private static string SubscriptionList(params string?[] orgs)
    {
        IEnumerable<string> subs = orgs.Select((o, i) => o is null
            ? "{\"id\":\"sub_" + i + "\",\"status\":\"active\",\"custom_data\":null}"
            : "{\"id\":\"sub_" + i + "\",\"status\":\"active\",\"custom_data\":{\"organization_id\":\"" + o + "\"}}");
        return "{\"data\":[" + string.Join(",", subs) + "],\"meta\":{\"pagination\":{\"per_page\":200,\"has_more\":false}}}";
    }

    [Fact]
    public async Task HasLiveSubscription_ListsTheCustomersLiveSubscriptions_AndMatchesThisOrg()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(SubscriptionList(Org.ToString())));

        bool live = await PaddlePayloads.Provider(h).HasLiveSubscriptionAsync("ctm_01", Org, default);

        live.Should().BeTrue();
        var call = h.Calls.Single();
        call.Method.Should().Be(HttpMethod.Get);
        call.Url.Should().StartWith("https://sandbox-api.paddle.com/subscriptions?customer_id=ctm_01&status=active,trialing,past_due,paused");
        call.Body.Should().BeEmpty();
        call.Version.Should().Be("1");
    }

    [Fact]
    public async Task HasLiveSubscription_IgnoresAnotherOrgsSubscription_OnTheSameCustomer()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(SubscriptionList(Guid.NewGuid().ToString())));

        (await PaddlePayloads.Provider(h).HasLiveSubscriptionAsync("ctm_01", Org, default)).Should().BeFalse();
    }

    [Fact]
    public async Task HasLiveSubscription_CountsASubscriptionWithoutAnOrganization()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(SubscriptionList(Guid.NewGuid().ToString(), null)));

        (await PaddlePayloads.Provider(h).HasLiveSubscriptionAsync("ctm_01", Org, default)).Should().BeTrue();
    }

    [Fact]
    public async Task HasLiveSubscription_NoneLive_IsFalse()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(SubscriptionList()));

        (await PaddlePayloads.Provider(h).HasLiveSubscriptionAsync("ctm_01", Org, default)).Should().BeFalse();
    }

    [Fact]
    public async Task HasLiveSubscription_ProviderError_Throws()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(
            """{"error":{"type":"api_error","code":"internal_error","detail":"boom"}}""", HttpStatusCode.InternalServerError));

        Func<Task> act = () => PaddlePayloads.Provider(h).HasLiveSubscriptionAsync("ctm_01", Org, default);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetSubscription_ReadsTheLiveItemsStatusScheduledCancelAndPeriod()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""
            {"data":{"id":"sub_01","status":"active",
              "items":[{"quantity":200,"price":{"id":"pri_team"}}],
              "scheduled_change":{"action":"cancel","effective_at":"2026-11-06T12:00:00Z","resume_at":null},
              "current_billing_period":{"starts_at":"2026-10-06T12:00:00Z","ends_at":"2026-11-06T12:00:00Z"}}}
            """));

        ProviderSubscriptionState state = await PaddlePayloads.Provider(h).GetSubscriptionAsync("sub_01", default);

        state.Status.Should().Be("active");
        state.Items.Should().Equal(new BillingLineItem("pri_team", 200));
        state.CancelScheduled.Should().BeTrue();
        state.PeriodEnd.Should().Be(new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));
        state.PeriodStart.Should().Be(new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc));
        var call = h.Calls.Single();
        call.Method.Should().Be(HttpMethod.Get);
        call.Url.Should().Be("https://sandbox-api.paddle.com/subscriptions/sub_01");
    }

    [Fact]
    public async Task GetSubscription_WithoutAScheduledChangeOrPeriod_IsNotCancelScheduled()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(
            """{"data":{"id":"sub_01","status":"past_due","items":[{"quantity":1,"price":{"id":"pri_creator"}}],"scheduled_change":null,"current_billing_period":null}}"""));

        ProviderSubscriptionState state = await PaddlePayloads.Provider(h).GetSubscriptionAsync("sub_01", default);

        state.Status.Should().Be("past_due");
        state.CancelScheduled.Should().BeFalse();
        state.PeriodEnd.Should().BeNull();
        state.PeriodStart.Should().BeNull();
    }

    [Fact]
    public async Task GetSubscription_ReadsTheDiscount()
    {
        PaddlePayloads.StubHandler withDiscount = new(_ => PaddlePayloads.Json("""
            {"data":{"id":"sub_01","status":"active","items":[{"quantity":1,"price":{"id":"pri_creator"}}],
              "discount":{"id":"dsc_1","starts_at":"2026-10-06T12:00:00Z","ends_at":null}}}
            """));
        PaddlePayloads.StubHandler ending = new(_ => PaddlePayloads.Json("""
            {"data":{"id":"sub_01","status":"active","items":[{"quantity":1,"price":{"id":"pri_creator"}}],
              "discount":{"id":"dsc_2","starts_at":"2026-10-06T12:00:00Z","ends_at":"2027-01-06T12:00:00Z"}}}
            """));
        PaddlePayloads.StubHandler without = new(_ => PaddlePayloads.Json(
            """{"data":{"id":"sub_01","status":"active","items":[{"quantity":1,"price":{"id":"pri_creator"}}],"discount":null}}"""));

        ProviderSubscriptionState state = await PaddlePayloads.Provider(withDiscount).GetSubscriptionAsync("sub_01", default);
        ProviderSubscriptionState ends = await PaddlePayloads.Provider(ending).GetSubscriptionAsync("sub_01", default);
        ProviderSubscriptionState none = await PaddlePayloads.Provider(without).GetSubscriptionAsync("sub_01", default);

        state.Discount.Should().Be(new ProviderSubscriptionDiscount("dsc_1", null));
        ends.Discount.Should().Be(new ProviderSubscriptionDiscount("dsc_2", new DateTime(2027, 1, 6, 12, 0, 0, DateTimeKind.Utc)));
        none.Discount.Should().BeNull();
    }

    [Fact]
    public async Task GetSubscription_ProviderError_Throws()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(
            """{"error":{"type":"api_error","code":"internal_error","detail":"boom"}}""", HttpStatusCode.InternalServerError));

        Func<Task> act = () => PaddlePayloads.Provider(h).GetSubscriptionAsync("sub_01", default);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private static PaddlePayloads.StubHandler ConflictThenLookup(string customersJson) => new(req =>
        (req.Method.Method, req.RequestUri!.AbsolutePath) switch
        {
            ("POST", "/customers") => PaddlePayloads.Json(
                """{"error":{"type":"request_error","code":"customer_already_exists","detail":"customer email conflicts with customer of id ctm_x"}}""",
                HttpStatusCode.Conflict),
            ("GET", "/customers") => PaddlePayloads.Json(customersJson),
            _ => PaddlePayloads.Json("""{"data":{"id":"txn_90","checkout":{"url":"https://pay.example.com/?_ptxn=txn_90"}}}""")
        });

    [Fact]
    public async Task Checkout_OnACustomerConflict_ReusesThisOrgsExistingCustomer()
    {
        PaddlePayloads.StubHandler h = ConflictThenLookup($$$"""
            {"data":[{"id":"ctm_other","email":"a+b@c.d","custom_data":{"organization_id":"{{{Guid.NewGuid()}}}"}},
                     {"id":"ctm_mine","email":"a+b@c.d","custom_data":{"organization_id":"{{{Org}}}"}}],"meta":{"pagination":{"has_more":false} } }
            """);
        Plan creator = new() { Key = "creator", Kind = PlanKind.Subscription, ProviderPriceIdsJson = """{"paddle":{"monthly":"pri_creator"}}""" };

        CheckoutSession s = await PaddlePayloads.Provider(h).CreateCheckoutAsync(new CheckoutRequest(Org, Guid.NewGuid(), "a+b@c.d", creator, 1, null, null), default);

        s.CustomerId.Should().Be("ctm_mine");
        var lookup = h.Calls.Single(c => c.Method == HttpMethod.Get);
        lookup.Url.Should().Be("https://sandbox-api.paddle.com/customers?email=a%2Bb%40c.d");
        using JsonDocument txn = JsonDocument.Parse(h.Calls.Single(c => c.Url.EndsWith("/transactions")).Body);
        txn.RootElement.GetProperty("customer_id").GetString().Should().Be("ctm_mine");
    }

    [Fact]
    public async Task Checkout_OnACustomerConflict_NeverBindsAnotherOrgsCustomer()
    {
        PaddlePayloads.StubHandler h = ConflictThenLookup($$$"""
            {"data":[{"id":"ctm_other","email":"a@b.c","custom_data":{"organization_id":"{{{Guid.NewGuid()}}}"}},
                     {"id":"ctm_bare","email":"a@b.c","custom_data":null}],"meta":{"pagination":{"has_more":false} } }
            """);
        Plan creator = new() { Key = "creator", Kind = PlanKind.Subscription, ProviderPriceIdsJson = """{"paddle":{"monthly":"pri_creator"}}""" };

        CheckoutSession s = await PaddlePayloads.Provider(h).CreateCheckoutAsync(new CheckoutRequest(Org, Guid.NewGuid(), "a@b.c", creator, 1, null, null), default);

        s.CustomerId.Should().BeNull();
        using JsonDocument txn = JsonDocument.Parse(h.Calls.Single(c => c.Url.EndsWith("/transactions")).Body);
        txn.RootElement.TryGetProperty("customer_id", out _).Should().BeFalse();
    }

    [Fact]
    public void ScheduledCancel_CarriesItsEffectiveDate()
    {
        BillingEvent e = Parse(PaddlePayloads.Subscription("evt_1", Now, Org, scheduledChange: "cancel"));

        e.CancelEffectiveAt.Should().Be(Now.AddMonths(1).UtcDateTime);
        Parse(PaddlePayloads.Subscription("evt_2", Now, Org)).CancelEffectiveAt.Should().BeNull();
        Parse(PaddlePayloads.Subscription("evt_3", Now, Org, scheduledChange: "pause")).CancelEffectiveAt.Should().BeNull();
    }

    [Fact]
    public async Task ApiError_CarriesPaddlesErrorCode()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(
            """{"error":{"type":"request_error","code":"subscription_locked_pending_changes","detail":"Subscription has pending changes."},"meta":{"request_id":"r1"}}""",
            HttpStatusCode.BadRequest));

        Func<Task> act = () => PaddlePayloads.Provider(h).ChangePlanAsync("sub_01", [new BillingLineItem("pri_pro", 1)], PlanChangeTiming.Immediately, PlanChangeDiscount.Unchanged, default);

        BillingProviderException ex = (await act.Should().ThrowAsync<BillingProviderException>()).Which;
        ex.Code.Should().Be("subscription_locked_pending_changes");
        ex.Status.Should().Be(400);
        ex.Detail.Should().Be("Subscription has pending changes.");
        ex.Should().BeAssignableTo<HttpRequestException>("existing callers catch HttpRequestException");
    }

    // ------------------------------------------------------------------ reconcile reads

    /// <summary>The <c>data</c> of a webhook fixture, with Paddle's API-only fields added, as <c>GET</c> returns it.</summary>
    private static string ApiEntity(string webhookEnvelope, params (string Key, string? Value)[] extra)
    {
        System.Text.Json.Nodes.JsonObject data = System.Text.Json.Nodes.JsonNode.Parse(webhookEnvelope)!["data"]!.AsObject().DeepClone().AsObject();
        foreach ((string key, string? value) in extra)
        {
            data[key] = value;
        }

        return data.ToJsonString();
    }

    [Fact]
    public async Task Snapshot_ParsesLikeTheWebhook()
    {
        DateTimeOffset updatedAt = Now.AddMinutes(-7);
        string webhook = PaddlePayloads.Subscription("evt_1", updatedAt, Org, price: "pri_team", qty: 5, scheduledChange: "cancel");
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(
            "{\"data\":" + ApiEntity(webhook, ("updated_at", updatedAt.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"))) + "}"));

        BillingSnapshot snap = await PaddlePayloads.Provider(h).SnapshotSubscriptionAsync("sub_01", default);

        BillingEvent expected = Parse(webhook) with
        {
            EventId = $"reconcile:sub:sub_01:{updatedAt.UtcTicks}",
            EventType = "subscription.reconciled",
            OccurredAt = updatedAt.UtcDateTime
        };
        snap.Event.Should().BeEquivalentTo(expected);
        snap.Event.Kind.Should().Be(BillingEventKind.SubscriptionChanged);
        snap.Event.Items.Should().Equal(new BillingLineItem("pri_team", 5));
        snap.Event.CancelAtPeriodEnd.Should().BeTrue();
        snap.Event.OrganizationId.Should().Be(Org);
        snap.ProviderUpdatedAt.Should().Be(updatedAt.UtcDateTime);
        using JsonDocument raw = JsonDocument.Parse(snap.RawJson);
        raw.RootElement.GetProperty("event_type").GetString().Should().Be("subscription.updated");
        raw.RootElement.GetProperty("event_id").GetString().Should().Be(expected.EventId);
        raw.RootElement.GetProperty("data").GetProperty("id").GetString().Should().Be("sub_01");
        var call = h.Calls.Single();
        call.Method.Should().Be(HttpMethod.Get);
        call.Url.Should().Be("https://sandbox-api.paddle.com/subscriptions/sub_01");
    }

    [Fact]
    public async Task Snapshot_WithoutUpdatedAt_IsStampedNow()
    {
        string webhook = PaddlePayloads.Subscription("evt_1", Now.AddDays(-1), Org);
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("{\"data\":" + ApiEntity(webhook) + "}"));

        BillingSnapshot snap = await PaddlePayloads.Provider(h).SnapshotSubscriptionAsync("sub_01", default);

        snap.ProviderUpdatedAt.Should().BeNull();
        snap.Event.OccurredAt.Should().Be(Now.UtcDateTime);
        snap.Event.EventId.Should().Be($"reconcile:sub:sub_01:{Now.UtcTicks}");
    }

    [Fact]
    public async Task Snapshot_404_ThrowsBillingProviderException()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(
            """{"error":{"type":"request_error","code":"not_found","detail":"Entity sub_gone not found"}}""", HttpStatusCode.NotFound));

        Func<Task> act = () => PaddlePayloads.Provider(h).SnapshotSubscriptionAsync("sub_gone", default);

        BillingProviderException ex = (await act.Should().ThrowAsync<BillingProviderException>()).Which;
        ex.Status.Should().Be(404);
        ex.Code.Should().Be("not_found");
    }

    [Fact]
    public async Task UpgradeTransactions_QueryAndKeepOnlyUpgradePaid()
    {
        DateTimeOffset billedAt = Now.AddMinutes(-30);
        DateTimeOffset changedAt = Now.AddMinutes(-31);
        string upgrade = PaddlePayloads.UpgradeTransaction("evt_9", billedAt, Org, prorationStart: changedAt);
        string topUp = PaddlePayloads.TopUpTransaction("evt_8", billedAt, Org, txn: "txn_top");
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(
            "{\"data\":[" + ApiEntity(topUp, ("billed_at", billedAt.ToString("O")), ("updated_at", Now.ToString("O")))
            + "," + ApiEntity(upgrade, ("billed_at", billedAt.ToString("O")), ("updated_at", Now.ToString("O")))
            + "],\"meta\":{\"pagination\":{\"per_page\":50,\"has_more\":false}}}"));

        IReadOnlyList<BillingSnapshot> found = await PaddlePayloads.Provider(h).CompletedUpgradeTransactionsAsync(
            "sub_01", new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc), default);

        var call = h.Calls.Single();
        call.Method.Should().Be(HttpMethod.Get);
        call.Url.Should().Be("https://sandbox-api.paddle.com/transactions?subscription_id=sub_01&status=completed&origin=subscription_update"
            + "&billed_at%5BGTE%5D=2026-10-05T12%3A00%3A00Z&per_page=50");
        BillingSnapshot only = found.Should().ContainSingle().Subject;
        BillingEvent expected = Parse(upgrade) with
        {
            EventId = "reconcile:txn:txn_up",
            EventType = "transaction.reconciled",
            OccurredAt = billedAt.UtcDateTime
        };
        only.Event.Should().BeEquivalentTo(expected);
        only.Event.Kind.Should().Be(BillingEventKind.UpgradePaid);
        only.Event.Items.Should().Equal(new BillingLineItem("pri_pro", 1));
        only.Event.AmountMinor.Should().Be(2500);
        only.Event.ProrationStartsAt.Should().Be(changedAt.UtcDateTime);
        only.ProviderUpdatedAt.Should().Be(Now.UtcDateTime);
        using JsonDocument raw = JsonDocument.Parse(only.RawJson);
        raw.RootElement.GetProperty("event_type").GetString().Should().Be("transaction.completed");
    }

    [Fact]
    public async Task UpgradeTransactions_WithoutBilledAt_OccurAtUpdatedAt()
    {
        string upgrade = PaddlePayloads.UpgradeTransaction("evt_9", Now, Org);
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(
            "{\"data\":[" + ApiEntity(upgrade, ("billed_at", null), ("updated_at", Now.AddMinutes(-2).ToString("O"))) + "]}"));

        IReadOnlyList<BillingSnapshot> found = await PaddlePayloads.Provider(h).CompletedUpgradeTransactionsAsync("sub_01", Now.UtcDateTime, default);

        found.Should().ContainSingle().Which.Event.OccurredAt.Should().Be(Now.AddMinutes(-2).UtcDateTime);
    }

    [Fact]
    public async Task UpgradeTransactions_SkipsBilledOrPastDueOrForeignSubscription()
    {
        // Paddle is asked for completed transactions of sub_01 only; a response that ignores those filters
        // must not turn a billed (unpaid), past_due or another subscription's payment into an upgrade grant.
        string billed = PaddlePayloads.UpgradeTransaction("evt_1", Now, Org, txn: "txn_billed");
        string pastDue = PaddlePayloads.UpgradeTransaction("evt_2", Now, Org, txn: "txn_past_due");
        string foreign = PaddlePayloads.UpgradeTransaction("evt_3", Now, Org, txn: "txn_foreign", subId: "sub_other");
        string missing = PaddlePayloads.UpgradeTransaction("evt_4", Now, Org, txn: "txn_no_status");
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(
            "{\"data\":[" + ApiEntity(billed, ("status", "billed"), ("billed_at", Now.ToString("O")))
            + "," + ApiEntity(pastDue, ("status", "past_due"), ("billed_at", Now.ToString("O")))
            + "," + ApiEntity(foreign, ("billed_at", Now.ToString("O")))
            + "," + ApiEntity(missing, ("status", null), ("billed_at", Now.ToString("O")))
            + "]}"));

        IReadOnlyList<BillingSnapshot> found = await PaddlePayloads.Provider(h).CompletedUpgradeTransactionsAsync("sub_01", Now.UtcDateTime, default);

        found.Should().BeEmpty();
    }

    [Fact]
    public async Task UpgradeTransactions_ProviderError_Throws()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(
            """{"error":{"type":"api_error","code":"internal_error","detail":"boom"}}""", HttpStatusCode.InternalServerError));

        Func<Task> act = () => PaddlePayloads.Provider(h).CompletedUpgradeTransactionsAsync("sub_01", Now.UtcDateTime, default);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task FindLiveSubscriptionId_MatchesOrg()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json(SubscriptionList(Guid.NewGuid().ToString(), null, Org.ToString())));

        string? id = await PaddlePayloads.Provider(h).FindLiveSubscriptionIdAsync("ctm_01", Org, default);

        id.Should().Be("sub_2", "a subscription naming this organization wins over one naming none");
        h.Calls.Single().Url.Should().Be("https://sandbox-api.paddle.com/subscriptions?customer_id=ctm_01&status=active,trialing,past_due,paused&per_page=200");
        PaddlePayloads.StubHandler bare = new(_ => PaddlePayloads.Json(SubscriptionList(Guid.NewGuid().ToString(), null)));
        (await PaddlePayloads.Provider(bare).FindLiveSubscriptionIdAsync("ctm_01", Org, default)).Should().Be("sub_1");
        PaddlePayloads.StubHandler other = new(_ => PaddlePayloads.Json(SubscriptionList(Guid.NewGuid().ToString())));
        (await PaddlePayloads.Provider(other).FindLiveSubscriptionIdAsync("ctm_01", Org, default)).Should().BeNull();
    }

    [Fact]
    public void TopUpPacks_ListOnlyThosWithAPriceId()
    {
        PaddleBillingProvider p = PaddlePayloads.Provider();

        p.TopUpPacks.Should().Equal(500, 1000);
        p.TopUpPriceId(3000).Should().BeNull();
        p.TopUpPriceId(1000).Should().Be("pri_topup_1000");
    }

    // ------------------------------------------------------------------ discounts and pricing preview

    private static readonly Guid Promo = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BonusPromo = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static PaddlePayloads.StubHandler DiscountCreated()
        => new(_ => PaddlePayloads.Json("""{"data":{"id":"dsc_01","status":"active"}}"""));

    [Fact]
    public async Task CreateDiscount_posts_percentage_recurring_without_code()
    {
        PaddlePayloads.StubHandler h = DiscountCreated();
        ProviderDiscountSpec spec = new(Promo, "Launch 20%", PromotionDiscountType.Percentage, "20", null, true, 3, 100, ["pri_pro"],
            new DateTime(2026, 12, 31, 23, 0, 0, DateTimeKind.Utc));

        string id = await PaddlePayloads.Provider(h).CreateDiscountAsync(spec, default);

        id.Should().Be("dsc_01");
        var call = h.Calls.Single();
        call.Method.Should().Be(HttpMethod.Post);
        call.Url.Should().Be("https://sandbox-api.paddle.com/discounts");
        call.Auth.Should().Be("Bearer pdl_sdbx_apikey_test");
        call.Version.Should().Be("1");
        using JsonDocument body = JsonDocument.Parse(call.Body);
        JsonElement root = body.RootElement;
        root.GetProperty("description").GetString().Should().Be("Launch 20%");
        root.GetProperty("type").GetString().Should().Be("percentage");
        root.GetProperty("amount").GetString().Should().Be("20");
        root.GetProperty("recur").GetBoolean().Should().BeTrue();
        root.GetProperty("maximum_recurring_intervals").GetInt32().Should().Be(3);
        root.GetProperty("usage_limit").GetInt32().Should().Be(100);
        root.GetProperty("restrict_to").EnumerateArray().Select(e => e.GetString()).Should().Equal("pri_pro");
        root.GetProperty("enabled_for_checkout").GetBoolean().Should().BeFalse();
        root.GetProperty("expires_at").GetString().Should().Be("2026-12-31T23:00:00Z");
        root.GetProperty("custom_data").GetProperty("promotion_id").GetString().Should().Be(Promo.ToString());
        root.TryGetProperty("code", out _).Should().BeFalse("codes are ours; Paddle never sees one");
        root.TryGetProperty("currency_code", out _).Should().BeFalse("a percentage has no currency");
    }

    [Fact]
    public async Task CreateDiscount_forever_sends_null_max_intervals()
    {
        PaddlePayloads.StubHandler h = DiscountCreated();
        ProviderDiscountSpec spec = new(Promo, "Forever 10%", PromotionDiscountType.Percentage, "10", null, true, null, null, [], null);

        await PaddlePayloads.Provider(h).CreateDiscountAsync(spec, default);

        using JsonDocument body = JsonDocument.Parse(h.Calls.Single().Body);
        JsonElement root = body.RootElement;
        root.GetProperty("recur").GetBoolean().Should().BeTrue();
        root.GetProperty("maximum_recurring_intervals").ValueKind.Should().Be(JsonValueKind.Null);
        root.TryGetProperty("restrict_to", out _).Should().BeFalse("an empty restriction list means every price");
        root.TryGetProperty("usage_limit", out _).Should().BeFalse();
        root.TryGetProperty("expires_at", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CreateDiscount_flat_sends_minor_units_and_currency_and_honours_the_checkout_flag()
    {
        PaddlePayloads.StubHandler h = DiscountCreated();
        ProviderDiscountSpec spec = new(Promo, "5 off", PromotionDiscountType.Flat, "500", "EUR", false, null, null, ["pri_topup_500"], null);

        await PaddlePayloads.Provider(h, discountsEnabledForCheckout: true).CreateDiscountAsync(spec, default);

        using JsonDocument body = JsonDocument.Parse(h.Calls.Single().Body);
        JsonElement root = body.RootElement;
        root.GetProperty("type").GetString().Should().Be("flat");
        root.GetProperty("amount").GetString().Should().Be("500");
        root.GetProperty("currency_code").GetString().Should().Be("EUR");
        root.GetProperty("recur").GetBoolean().Should().BeFalse();
        root.GetProperty("enabled_for_checkout").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Checkout_sends_discount_id_and_promotion_custom_data()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"data":{"id":"txn_80","checkout":{"url":"https://pay.example.com/?_ptxn=txn_80"}}}"""));
        PaddleBillingProvider p = PaddlePayloads.Provider(h);

        await p.CreateCheckoutAsync(new CheckoutRequest(Org, Guid.NewGuid(), null, null, 1, 500, null, "dsc_01", Promo, BonusPromo), default);
        await p.CreateCheckoutAsync(new CheckoutRequest(Org, Guid.NewGuid(), null, null, 1, 500, null), default);

        using JsonDocument discounted = JsonDocument.Parse(h.Calls[0].Body);
        discounted.RootElement.GetProperty("discount_id").GetString().Should().Be("dsc_01");
        JsonElement custom = discounted.RootElement.GetProperty("custom_data");
        custom.GetProperty("promotion_id").GetString().Should().Be(Promo.ToString());
        custom.GetProperty("bonus_promotion_id").GetString().Should().Be(BonusPromo.ToString());
        custom.GetProperty("kind").GetString().Should().Be("topup");

        using JsonDocument plain = JsonDocument.Parse(h.Calls[1].Body);
        plain.RootElement.TryGetProperty("discount_id", out _).Should().BeFalse();
        plain.RootElement.GetProperty("custom_data").TryGetProperty("promotion_id", out _).Should().BeFalse();
        plain.RootElement.GetProperty("custom_data").TryGetProperty("bonus_promotion_id", out _).Should().BeFalse();
    }

    [Fact]
    public async Task PreviewPrice_reads_paddle_formatted_unit_totals_for_a_multi_seat_line()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""
            {"data":{"currency_code":"USD","discount_id":"dsc_01",
             "items":[{"price_id":"pri_team","quantity":3}],
             "details":{"line_items":[{"price":{"id":"pri_team"},"quantity":3,
               "totals":{"subtotal":"14700","discount":"7350","tax":"0","total":"7350"},
               "formatted_totals":{"subtotal":"$147.00","discount":"-$73.50","tax":"$0.00","total":"$73.50"},
               "formatted_unit_totals":{"subtotal":"$49.00","discount":"-$24.50","tax":"$0.00","total":"$24.50"}}]}}}
            """));

        PricePreview preview = await PaddlePayloads.Provider(h).PreviewPriceAsync([new BillingLineItem("pri_team", 3)], "dsc_01", default);

        preview.UnitSubtotal.Should().Be("$49.00");
        preview.UnitTotal.Should().Be("$24.50");
        preview.Total.Should().Be("$73.50");
        using JsonDocument body = JsonDocument.Parse(h.Calls.Single().Body);
        body.RootElement.GetProperty("items")[0].GetProperty("quantity").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task PreviewPrice_returns_paddle_formatted_totals()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""
            {"data":{"customer_id":null,"address_id":null,"currency_code":"EUR","discount_id":"dsc_01",
             "items":[{"price_id":"pri_pro","quantity":1}],
             "details":{"line_items":[{"price":{"id":"pri_pro"},"quantity":1,
               "totals":{"subtotal":"2000","discount":"400","tax":"0","total":"1600"},
               "formatted_totals":{"subtotal":"€20.00","discount":"-€4.00","tax":"€0.00","total":"€16.00"}}]}}}
            """));

        PricePreview preview = await PaddlePayloads.Provider(h).PreviewPriceAsync([new BillingLineItem("pri_pro", 1)], "dsc_01", default);

        preview.Should().Be(new PricePreview("EUR", "€20.00", "-€4.00", "€16.00", "0", "400"));
        var call = h.Calls.Single();
        call.Method.Should().Be(HttpMethod.Post);
        call.Url.Should().Be("https://sandbox-api.paddle.com/pricing-preview");
        using JsonDocument body = JsonDocument.Parse(call.Body);
        body.RootElement.GetProperty("items")[0].GetProperty("price_id").GetString().Should().Be("pri_pro");
        body.RootElement.GetProperty("items")[0].GetProperty("quantity").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("discount_id").GetString().Should().Be("dsc_01");
    }

    [Fact]
    public async Task PreviewPrice_without_a_discount_omits_discount_id()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""
            {"data":{"currency_code":"USD","details":{"line_items":[{"formatted_totals":{"subtotal":"$20.00","discount":"$0.00","tax":"$0.00","total":"$20.00"}}]}}}
            """));

        PricePreview preview = await PaddlePayloads.Provider(h).PreviewPriceAsync([new BillingLineItem("pri_pro", 1)], null, default);

        preview.Total.Should().Be("$20.00");
        using JsonDocument body = JsonDocument.Parse(h.Calls.Single().Body);
        body.RootElement.TryGetProperty("discount_id", out _).Should().BeFalse();
    }

    [Fact]
    public void TransactionCompleted_parses_discount_id_and_total_for_topup_and_subscription()
    {
        string promoExtra = $$""","promotion_id":"{{Promo}}","bonus_promotion_id":"{{BonusPromo}}" """;
        BillingEvent topUp = Parse(PaddlePayloads.DiscountedTransaction("evt_d1", Now, Org, "topup", "pri_topup_500", null, "dsc_01", "100", promoExtra));

        topUp.Kind.Should().Be(BillingEventKind.TopUpPaid);
        topUp.TopUpCredits.Should().Be(500);
        topUp.DiscountId.Should().Be("dsc_01");
        topUp.DiscountTotal.Should().Be("100");
        topUp.CurrencyCode.Should().Be("EUR");
        topUp.PromotionId.Should().Be(Promo);
        topUp.BonusPromotionId.Should().Be(BonusPromo);

        BillingEvent sub = Parse(PaddlePayloads.DiscountedTransaction("evt_d2", Now, Org, "subscription", "pri_pro", "sub_01", "dsc_02", "400",
            $$""","promotion_id":"{{Promo}}" """, txn: "txn_02"));

        sub.Kind.Should().Be(BillingEventKind.TransactionCompleted);
        sub.OrganizationId.Should().Be(Org);
        sub.SubscriptionId.Should().Be("sub_01");
        sub.TransactionId.Should().Be("txn_02");
        sub.CustomerId.Should().Be("ctm_01");
        sub.DiscountId.Should().Be("dsc_02");
        sub.DiscountTotal.Should().Be("400");
        sub.CurrencyCode.Should().Be("EUR");
        sub.PromotionId.Should().Be(Promo);
        sub.BonusPromotionId.Should().BeNull();
    }

    [Fact]
    public void TransactionCompleted_for_a_subscription_without_a_discount_stays_ignored()
        => Parse(PaddlePayloads.DiscountedTransaction("evt_d3", Now, Org, "subscription", "pri_pro", "sub_01", null, "0"))
            .Kind.Should().Be(BillingEventKind.Ignored);

    [Fact]
    public void TransactionCompleted_with_an_unparsable_promotion_id_ignores_it()
    {
        BillingEvent e = Parse(PaddlePayloads.DiscountedTransaction("evt_d4", Now, Org, "topup", "pri_topup_500", null, "dsc_01", "100",
            ""","promotion_id":"not-a-guid","bonus_promotion_id":42"""));

        e.Kind.Should().Be(BillingEventKind.TopUpPaid);
        e.PromotionId.Should().BeNull();
        e.BonusPromotionId.Should().BeNull();
    }

    [Fact]
    public void TransactionCompletedKind_is_appended_after_the_existing_kinds()
    {
        // UpgradePaid (subscription management) took 4 first; the promotions kind is appended after it.
        ((int)BillingEventKind.UpgradePaid).Should().Be(4);
        ((int)BillingEventKind.TransactionCompleted).Should().Be(5);
    }

    [Fact]
    public async Task UpdateDiscount_inactive_patches_status_archived()
    {
        PaddlePayloads.StubHandler h = DiscountCreated();

        await PaddlePayloads.Provider(h).UpdateDiscountAsync("dsc_01", false, null, false, default);

        var call = h.Calls.Single();
        call.Method.Should().Be(HttpMethod.Patch);
        call.Url.Should().Be("https://sandbox-api.paddle.com/discounts/dsc_01");
        using JsonDocument body = JsonDocument.Parse(call.Body);
        body.RootElement.GetProperty("status").GetString().Should().Be("archived");
        body.RootElement.TryGetProperty("expires_at", out _).Should().BeFalse("only the provided fields are patched");
    }

    [Fact]
    public async Task UpdateDiscount_active_with_expiry_patches_status_and_expires_at()
    {
        PaddlePayloads.StubHandler h = DiscountCreated();

        await PaddlePayloads.Provider(h).UpdateDiscountAsync("dsc_01", true, new DateTime(2027, 1, 15, 8, 30, 0, DateTimeKind.Utc), false, default);

        using JsonDocument body = JsonDocument.Parse(h.Calls.Single().Body);
        body.RootElement.GetProperty("status").GetString().Should().Be("active");
        body.RootElement.GetProperty("expires_at").GetString().Should().Be("2027-01-15T08:30:00Z");
    }

    [Fact]
    public async Task UpdateDiscount_clear_expiry_sends_an_explicit_null()
    {
        PaddlePayloads.StubHandler h = DiscountCreated();

        await PaddlePayloads.Provider(h).UpdateDiscountAsync("dsc_01", null, null, true, default);

        using JsonDocument body = JsonDocument.Parse(h.Calls.Single().Body);
        body.RootElement.GetProperty("expires_at").ValueKind.Should().Be(JsonValueKind.Null);
        body.RootElement.TryGetProperty("status", out _).Should().BeFalse();
    }
    // ------------------------------------------------------------------ C10: cancel a superseded or expired checkout transaction

    [Fact]
    public async Task CancelTransaction_patches_the_status_to_canceled()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"data":{"id":"txn_01","status":"canceled"}}"""));

        ProviderTransactionCancel result = await PaddlePayloads.Provider(h).CancelTransactionAsync("txn_01", default);

        result.Should().Be(ProviderTransactionCancel.Cancelled);
        var call = h.Calls.Single();
        call.Method.Should().Be(HttpMethod.Patch);
        call.Url.Should().Be("https://sandbox-api.paddle.com/transactions/txn_01");
        using JsonDocument body = JsonDocument.Parse(call.Body);
        body.RootElement.GetProperty("status").GetString().Should().Be("canceled");
        body.RootElement.EnumerateObject().Should().ContainSingle("nothing but the status is changed");
    }

    [Fact]
    public async Task CancelTransaction_that_paddle_refuses_as_immutable_is_left_alone()
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"error":{"type":"request_error","code":"transaction_immutable","detail":"buyer victim@example.com already paid"}}""", HttpStatusCode.BadRequest));
        RecordingLogger<PaddleBillingProvider> log = new();

        ProviderTransactionCancel result = await PaddlePayloads.Provider(h, logger: log).CancelTransactionAsync("txn_01", default);

        result.Should().Be(ProviderTransactionCancel.NotCancellable);
        log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning, "Paddle's documented answer for a paid, billed or canceled transaction is expected");
    }

    /// <summary>
    /// N-17: a 4xx that does not prove the transaction is past cancelling (a contract error, a wrong
    /// path, an unknown code) must not settle the row forever: it throws to be retried, at Warning.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Conflict, "transaction_status_invalid")]
    [InlineData(HttpStatusCode.NotFound, "not_found")]
    [InlineData(HttpStatusCode.BadRequest, "invalid_field")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "unknown")]
    public async Task CancelTransaction_unexpected_4xx_is_logged_at_warning_and_retried_not_settled(HttpStatusCode code, string error)
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json($$$"""{"error":{"type":"request_error","code":"{{{error}}}","detail":"buyer victim@example.com"}}""", code));
        RecordingLogger<PaddleBillingProvider> log = new();

        Func<Task> act = () => PaddlePayloads.Provider(h, logger: log).CancelTransactionAsync("txn_01", default);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(code);
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("txn_01").And.Contain(((int)code).ToString(System.Globalization.CultureInfo.InvariantCulture)).And.NotContain("@");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task CancelTransaction_auth_rate_limit_and_server_errors_throw_to_be_retried(HttpStatusCode code)
    {
        PaddlePayloads.StubHandler h = new(_ => PaddlePayloads.Json("""{"error":{"code":"x","detail":"victim@example.com"}}""", code));

        Func<Task> act = () => PaddlePayloads.Provider(h).CancelTransactionAsync("txn_01", default);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().NotContain("@").And.NotContain("pdl_sdbx");
    }

    [Fact]
    public void A_plain_subscription_transaction_carries_its_billing_period_start()
    {
        string payload = PaddlePayloads.Envelope("evt_bp", "transaction.completed", Now, """
            {"id":"txn_bp","status":"completed","customer_id":"ctm_01","subscription_id":"sub_01","origin":"subscription_recurring","currency_code":"USD",
             "billing_period":{"starts_at":"2026-01-15T12:00:00Z","ends_at":"2027-01-15T12:00:00Z"},
             "details":{"totals":{"grand_total":"29000","currency_code":"USD"}},
             "items":[{"price":{"id":"pri_creator_y","product_id":"pro_01"},"quantity":1}],
             "custom_data":{"organization_id":"ORG","kind":"subscription"}}
            """.Replace("ORG", Org.ToString()));

        Parse(payload).BillingPeriodStart.Should().Be(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc));
    }
}
