// Fixtures shaped from docs.stripe.com 2026-10-26, replace with redacted sandbox captures after the operator spike.
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Stripe;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

/// <summary>Hand-written Stripe webhook payloads (API 2026-09-30.endive layout) and a signer.</summary>
internal static class StripePayloads
{
    public const string Secret = "whsec_test_current_secret";
    public const string PreviousSecret = "whsec_test_previous_secret";
    public static readonly Guid OrgId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    public static long Unix(DateTime t) => new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeSeconds();

    public static StripeOptions Options(bool live = false, string? previous = null) => new()
    {
        ApiKey = live ? "rk_live_" + "dummydummy" : "rk_test_" + "dummydummy",
        WebhookSecret = Secret,
        WebhookSecretPrevious = previous
    };

    public static string Envelope(string type, JsonNode obj, string id = "evt_1", DateTime? created = null, bool livemode = false, string? apiVersion = StripeOptions.ApiVersion)
    {
        JsonObject env = new()
        {
            ["id"] = id,
            ["object"] = "event",
            ["type"] = type,
            ["created"] = Unix(created ?? Now),
            ["livemode"] = livemode,
            ["data"] = new JsonObject { ["object"] = obj.DeepClone() }
        };
        if (apiVersion is not null) env["api_version"] = apiVersion;
        return env.ToJsonString();
    }

    public static IReadOnlyDictionary<string, string> Signed(string body, string secret = Secret, DateTime? at = null)
        => new Dictionary<string, string> { ["stripe-signature"] = StripeSignature.Sign(body, secret, Unix(at ?? Now)) };

    public static JsonObject Metadata(Guid? org = null, int? credits = null, Guid? promo = null, Guid? bonus = null)
    {
        JsonObject m = new() { [StripeMetadataKeys.OrganizationId] = (org ?? OrgId).ToString() };
        if (credits is not null) m[StripeMetadataKeys.TopUpCredits] = credits.Value.ToString();
        if (promo is not null) m[StripeMetadataKeys.PromotionId] = promo.Value.ToString();
        if (bonus is not null) m[StripeMetadataKeys.BonusPromotionId] = bonus.Value.ToString();
        return m;
    }

    public static JsonObject TopUpSession(int credits = 1000, string paymentStatus = "paid", long amount = 1450, JsonNode? discounts = null, long discount = 0, Guid? promo = null, Guid? bonus = null, string mode = "payment") => new()
    {
        ["id"] = "cs_test_1",
        ["object"] = "checkout.session",
        ["mode"] = mode,
        ["status"] = "complete",
        ["payment_status"] = paymentStatus,
        ["client_reference_id"] = OrgId.ToString(),
        ["metadata"] = Metadata(OrgId, credits, promo, bonus),
        ["customer"] = "cus_1",
        ["payment_intent"] = "pi_1",
        ["amount_total"] = amount,
        ["currency"] = "eur",
        ["discounts"] = discounts ?? new JsonArray(),
        ["total_details"] = new JsonObject { ["amount_discount"] = discount },
        ["livemode"] = false
    };

    /// <summary>A basil-layout subscription: periods live on the item, not on the subscription.</summary>
    public static JsonObject Subscription(string status = "active", string price = "price_creator", int quantity = 1, DateTime? start = null, bool cancelAtPeriodEnd = false, DateTime? cancelAt = null, bool periodOnItem = true)
    {
        DateTime s = start ?? Now;
        JsonObject item = new() { ["id"] = "si_1", ["price"] = new JsonObject { ["id"] = price }, ["quantity"] = quantity };
        JsonObject sub = new()
        {
            ["id"] = "sub_1",
            ["object"] = "subscription",
            ["customer"] = "cus_1",
            ["status"] = status,
            ["metadata"] = Metadata(),
            ["cancel_at_period_end"] = cancelAtPeriodEnd,
            ["cancel_at"] = cancelAt is null ? null : Unix(cancelAt.Value),
            ["items"] = new JsonObject { ["data"] = new JsonArray(item) }
        };
        if (periodOnItem)
        {
            item["current_period_start"] = Unix(s);
            item["current_period_end"] = Unix(s.AddMonths(1));
        }
        else
        {
            sub["current_period_start"] = Unix(s);
            sub["current_period_end"] = Unix(s.AddMonths(1));
        }

        return sub;
    }

    public static JsonObject Line(long amount, string price, int quantity, DateTime start, DateTime? end = null) => new()
    {
        ["amount"] = amount,
        ["quantity"] = quantity,
        ["period"] = new JsonObject { ["start"] = Unix(start), ["end"] = Unix(end ?? start.AddMonths(1)) },
        ["pricing"] = new JsonObject { ["price_details"] = new JsonObject { ["price"] = price, ["product"] = "prod_1" } },
        ["parent"] = new JsonObject
        {
            ["type"] = "subscription_item_details",
            ["subscription_item_details"] = new JsonObject { ["subscription"] = "sub_1", ["subscription_item"] = "si_1", ["proration"] = true }
        }
    };

    public static JsonObject Invoice(string reason, long amountPaid, params JsonObject[] lines) => new()
    {
        ["id"] = "in_1",
        ["object"] = "invoice",
        ["billing_reason"] = reason,
        ["status"] = "paid",
        ["amount_paid"] = amountPaid,
        ["currency"] = "eur",
        ["customer"] = "cus_1",
        ["parent"] = new JsonObject
        {
            ["type"] = "subscription_details",
            ["subscription_details"] = new JsonObject { ["subscription"] = "sub_1", ["metadata"] = Metadata() }
        },
        ["lines"] = new JsonObject { ["data"] = new JsonArray(Array.ConvertAll(lines, l => (JsonNode?)l)) },
        ["total_discount_amounts"] = new JsonArray(),
        ["discounts"] = new JsonArray()
    };

    public static JsonObject Dispute(string status = "lost", long amount = 1450, string? paymentIntent = "pi_1") => new()
    {
        ["id"] = "dp_1",
        ["object"] = "dispute",
        ["status"] = status,
        ["amount"] = amount,
        ["currency"] = "eur",
        ["payment_intent"] = paymentIntent,
        ["charge"] = "ch_1",
        ["evidence"] = new JsonObject { ["customer_email_address"] = "buyer@example.com" }
    };

    public static JsonObject Refund(string status = "succeeded", long amount = 1450) => new()
    {
        ["id"] = "re_1",
        ["object"] = "refund",
        ["status"] = status,
        ["amount"] = amount,
        ["currency"] = "eur",
        ["payment_intent"] = "pi_1",
        ["charge"] = "ch_1"
    };
}
