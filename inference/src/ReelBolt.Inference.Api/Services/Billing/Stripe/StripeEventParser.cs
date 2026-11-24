using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Services.Billing.Stripe;

/// <summary>
/// Verifies a Stripe webhook and turns it into the neutral <see cref="BillingEvent"/>. Pure: no HTTP, no state.
/// </summary>
/// <remarks>
/// <para>Payload shapes are for API <see cref="StripeOptions.ApiVersion"/> (basil+ layout: subscription periods live on
/// the subscription items, invoice lines carry <c>pricing.price_details</c> and <c>parent.*</c>). An event whose
/// <c>api_version</c> differs means the operator created the endpoint with the wrong version; that is a loud 400, not a guess.</para>
/// <para>Identity (DEC-4): a top-up's <c>TransactionId</c> is the PaymentIntent id and its <c>CheckoutId</c> the Checkout Session id;
/// subscription and upgrade payments use the invoice id. A <c>refund.*</c> event carries only the PaymentIntent, so its
/// <c>TransactionId</c> is the payment intent id and <c>FullRefund</c> is false here: <c>StripeBillingProvider.ResolveAsync</c> (T-004)
/// rewrites <c>TransactionId</c> to the invoice id for subscription payments and sets <c>FullRefund</c> after reading the original amount.</para>
/// <para>Subscription status mapping (total): active=Active, trialing=Trialing, past_due=PastDue, unpaid=Paused (DEC-19: Stripe's guidance is
/// to revoke access once retries are exhausted, so it is non-entitling like incomplete), paused=Paused, canceled=Canceled,
/// incomplete_expired=Canceled, incomplete=Paused (the closest non-entitling state: the first payment has not succeeded, and the enum has
/// no "pending"). Unknown words are Ignored.</para>
/// <para>Disputes (DEC-18): <c>charge.dispute.closed</c> with <c>status=lost</c> is a <see cref="BillingEventKind.PaymentRefunded"/> whose
/// <c>AdjustmentId</c> is the dispute id (<c>du_…</c> in a real sandbox) and <c>TransactionId</c> the dispute's PaymentIntent (<c>FullRefund</c> is set by
/// <c>ResolveAsync</c>); <c>charge.dispute.created</c> is Ignored with a Warning log (no automatic action); any other dispute event is Ignored.</para>
/// </remarks>
public sealed class StripeEventParser(StripeOptions options, TimeProvider? time = null, Func<int, bool>? isConfiguredPack = null, Microsoft.Extensions.Logging.ILogger? logger = null)
{
    public const string ProviderName = "stripe";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Verifies the signature over the exact raw body, then parses it. Throws <see cref="WebhookVerificationException"/>.</summary>
    public BillingEvent Parse(string rawBody, IReadOnlyDictionary<string, string> headers)
    {
        string? header = headers.FirstOrDefault(h => string.Equals(h.Key, StripeSignature.HeaderName, StringComparison.OrdinalIgnoreCase)).Value;
        StripeSignature.Verify(rawBody, header, options.WebhookSecrets, options.ToleranceSeconds, _time.GetUtcNow());

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawBody);
            return ParseVerified(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new WebhookVerificationException("Malformed webhook body.");
        }
    }

    /// <summary>
    /// Maps an event envelope whose authenticity the caller already established (the signature path above, or an envelope this
    /// platform built itself around an object it just fetched). Same livemode and api_version checks, same mapping; no signature step.
    /// </summary>
    internal BillingEvent ParseVerified(JsonElement envelope) => Map(envelope);

    /// <summary>
    /// Maps a Subscription object read from the API (not a webhook) the way a <c>customer.subscription.*</c> event would be,
    /// under the caller's event id, type and time. No signature or livemode check: the caller fetched it itself.
    /// </summary>
    public BillingEvent MapSubscriptionObject(JsonElement subscription, string eventId, string eventType, DateTime occurredAt, bool deleted = false)
        => Subscription(subscription, deleted ? "customer.subscription.deleted" : "customer.subscription.updated", SyntheticBase(eventId, eventType, occurredAt));

    /// <summary>Maps an Invoice object read from the API the way an <c>invoice.paid</c> event would be (paid upgrade, subscription payment, or Ignored).</summary>
    public BillingEvent MapInvoiceObject(JsonElement invoice, string eventId, string eventType, DateTime occurredAt)
        => Invoice(invoice, SyntheticBase(eventId, eventType, occurredAt));

    private static Func<BillingEventKind, string?, BillingEvent> SyntheticBase(string eventId, string eventType, DateTime occurredAt)
        => (kind, reason) => new BillingEvent
        {
            Provider = ProviderName,
            EventId = eventId,
            EventType = eventType,
            OccurredAt = occurredAt,
            Kind = kind,
            IgnoredReason = reason
        };

    /// <summary>The coupon id behind a Discount object (<c>source.coupon</c> on current API versions, <c>coupon</c> on older ones); null when unknown.</summary>
    internal static string? CouponIdOf(JsonElement? discount)
        => IdOf(Prop(Prop(discount, "source"), "coupon")) ?? IdOf(Prop(discount, "coupon"));

    private BillingEvent Map(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Envelope is not an object.");

        string id = Str(root, "id") ?? throw new InvalidOperationException("No event id.");
        string type = Str(root, "type") ?? throw new InvalidOperationException("No event type.");
        long created = Long(root, "created") ?? throw new InvalidOperationException("No created.");

        bool? livemode = Prop(root, "livemode") is { ValueKind: JsonValueKind.True or JsonValueKind.False } lv ? lv.GetBoolean() : null;
        if (livemode != options.IsLiveMode)
        {
            throw new WebhookVerificationException("livemode mismatch");
        }

        if (Str(root, "api_version") is { } apiVersion && !string.Equals(apiVersion, StripeOptions.ApiVersion, StringComparison.Ordinal))
        {
            throw new WebhookVerificationException(
                $"Webhook endpoint api_version '{apiVersion}' does not match the pinned '{StripeOptions.ApiVersion}'; recreate the endpoint with the pinned version.");
        }

        DateTime occurredAt = DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime;
        BillingEvent Base(BillingEventKind kind, string? reason = null) => new()
        {
            Provider = ProviderName,
            EventId = id,
            EventType = type,
            OccurredAt = occurredAt,
            Kind = kind,
            IgnoredReason = reason
        };

        JsonElement? obj = Prop(Prop(root, "data"), "object");
        if (obj is not { } o)
        {
            return Base(BillingEventKind.Ignored, "event carries no data.object");
        }

        return type switch
        {
            "checkout.session.completed" or "checkout.session.async_payment_succeeded" => Checkout(o, Base),
            "checkout.session.async_payment_failed" => Base(BillingEventKind.Ignored, "async payment failed; nothing was granted"),
            "checkout.session.expired" => Base(BillingEventKind.Ignored, "checkout session expired unpaid"),
            "customer.subscription.created" or "customer.subscription.updated" or "customer.subscription.deleted"
                or "customer.subscription.paused" or "customer.subscription.resumed" => Subscription(o, type, Base),
            "invoice.paid" => Invoice(o, Base),
            "refund.created" or "refund.updated" => Refund(o, Base),
            "charge.refunded" => Base(BillingEventKind.Ignored, "handled via refund.created"),
            "invoice.payment_failed" => Base(BillingEventKind.Ignored, "invoice payment failed; the subscription event is the authority"),
            "invoice.payment_action_required" => Base(BillingEventKind.Ignored, "invoice payment needs customer action; the subscription event is the authority"),
            "charge.dispute.closed" => DisputeClosed(o, Base),
            "charge.dispute.created" => DisputeOpened(id, o, Base),
            _ when type.StartsWith("charge.dispute.", StringComparison.Ordinal) => Base(BillingEventKind.Ignored, $"dispute event {type} is not acted on"),
            _ => Base(BillingEventKind.Ignored, $"event type {type} is not acted on")
        };
    }

    // ---- checkout.session ----

    private BillingEvent Checkout(JsonElement s, Func<BillingEventKind, string?, BillingEvent> baseEvent)
    {
        string? mode = Str(s, "mode");
        if (mode == "subscription")
        {
            return baseEvent(BillingEventKind.Ignored, "subscription lifecycle arrives via customer.subscription.* and invoice.paid");
        }

        if (mode != "payment")
        {
            return baseEvent(BillingEventKind.Ignored, $"checkout mode {mode ?? "(none)"} is not acted on");
        }

        string? paymentStatus = Str(s, "payment_status");
        if (paymentStatus != "paid")
        {
            return baseEvent(BillingEventKind.Ignored, $"payment_status {paymentStatus ?? "(none)"}: not paid yet, nothing granted");
        }

        JsonElement? metadata = Prop(s, "metadata");
        int? credits = PositiveInt(Str(metadata, StripeMetadataKeys.TopUpCredits));
        if (credits is { } pack && isConfiguredPack is not null && !isConfiguredPack(pack))
        {
            return baseEvent(BillingEventKind.Ignored, "topup_pack_not_configured");
        }

        // Only metadata.organization_id (our checkout always sets it), as the confirm path does: a Payment Link lets
        // a buyer choose client_reference_id, so it never names the organization that is credited.
        Guid? org = Guid.TryParse(Str(metadata, StripeMetadataKeys.OrganizationId), out Guid g) ? g : null;
        long discount = Long(Prop(s, "total_details"), "amount_discount") ?? 0;
        return baseEvent(BillingEventKind.TopUpPaid, null) with
        {
            OrganizationId = org,
            CustomerId = IdOf(Prop(s, "customer")),
            TransactionId = IdOf(Prop(s, "payment_intent")),
            CheckoutId = Str(s, "id"),
            TopUpCredits = credits,
            AmountMinor = Long(s, "amount_total") ?? 0,
            IsPaidTransaction = Long(s, "amount_total") is not null,
            CurrencyCode = Str(s, "currency")?.ToUpperInvariant(),
            DiscountId = FirstDiscountId(Prop(s, "discounts")),
            DiscountTotal = discount > 0 ? discount.ToString(CultureInfo.InvariantCulture) : null,
            SubtotalMinor = Long(s, "amount_subtotal"),
            DiscountMinor = Long(Prop(s, "total_details"), "amount_discount"),
            TaxMinor = Long(Prop(s, "total_details"), "amount_tax"),
            TotalMinor = Long(s, "amount_total"),
            PromotionId = Guid.TryParse(Str(metadata, StripeMetadataKeys.PromotionId), out Guid p) ? p : null,
            BonusPromotionId = Guid.TryParse(Str(metadata, StripeMetadataKeys.BonusPromotionId), out Guid b) ? b : null
        };
    }

    // ---- customer.subscription.* ----

    private BillingEvent Subscription(JsonElement s, string type, Func<BillingEventKind, string?, BillingEvent> baseEvent)
    {
        SubscriptionStatus? status = type == "customer.subscription.deleted" ? SubscriptionStatus.Canceled : MapStatus(Str(s, "status"));
        if (status is null)
        {
            return baseEvent(BillingEventKind.Ignored, $"unknown subscription status '{Str(s, "status")}'");
        }

        JsonElement? first = Items(s).FirstOrDefault();
        DateTime? start = Time(Long(first, "current_period_start") ?? Long(s, "current_period_start"));
        DateTime? end = Time(Long(first, "current_period_end") ?? Long(s, "current_period_end"));
        DateTime? cancelAt = Time(Long(s, "cancel_at"));
        bool atPeriodEnd = Prop(s, "cancel_at_period_end") is { ValueKind: JsonValueKind.True };

        JsonElement? metadata = Prop(s, "metadata");
        List<BillingLineItem> items = [];
        foreach (JsonElement? item in Items(s))
        {
            string? price = IdOf(Prop(item, "price"));
            if (price is not null)
            {
                items.Add(new BillingLineItem(price, (int)(Long(item, "quantity") ?? 1)));
            }
        }

        return baseEvent(BillingEventKind.SubscriptionChanged, null) with
        {
            OrganizationId = Guid.TryParse(Str(metadata, StripeMetadataKeys.OrganizationId), out Guid org) ? org : null,
            CustomerId = IdOf(Prop(s, "customer")),
            SubscriptionId = Str(s, "id"),
            Status = status.Value,
            PeriodStart = start,
            PeriodEnd = end,
            CancelAtPeriodEnd = atPeriodEnd || cancelAt is not null,
            CancelEffectiveAt = atPeriodEnd ? end : cancelAt,
            Items = items
        };
    }

    /// <summary>Total mapping of Stripe's subscription status words; null for an unknown one.</summary>
    private static SubscriptionStatus? MapStatus(string? status) => status switch
    {
        "active" => SubscriptionStatus.Active,
        "trialing" => SubscriptionStatus.Trialing,
        "past_due" => SubscriptionStatus.PastDue,
        "unpaid" => SubscriptionStatus.Paused,
        "paused" => SubscriptionStatus.Paused,
        "canceled" => SubscriptionStatus.Canceled,
        "incomplete_expired" => SubscriptionStatus.Canceled,
        "incomplete" => SubscriptionStatus.Paused,
        _ => null
    };

    // ---- invoice.paid ----

    private BillingEvent Invoice(JsonElement inv, Func<BillingEventKind, string?, BillingEvent> baseEvent)
    {
        BillingEvent evt = InvoiceCore(inv, baseEvent);

        // A zero-amount invoice that is not the first period (a 100% off renewal, a zero upgrade) takes no
        // credit or promotion action - exactly as before - but the payment is still recorded (amount 0), so
        // revenue reads what was charged. Its Kind stays Ignored.
        return (Long(inv, "amount_paid") ?? 0) == 0 && evt.Kind != BillingEventKind.Ignored && Str(inv, "billing_reason") != "subscription_create"
            ? evt with { Kind = BillingEventKind.Ignored, IgnoredReason = "zero amount invoice" }
            : evt;
    }

    private BillingEvent InvoiceCore(JsonElement inv, Func<BillingEventKind, string?, BillingEvent> baseEvent)
    {
        long amountPaid = Long(inv, "amount_paid") ?? 0;
        string? reason = Str(inv, "billing_reason");

        BillingEventKind kind = reason switch
        {
            "subscription_update" => BillingEventKind.UpgradePaid,
            "subscription_create" or "subscription_cycle" => BillingEventKind.TransactionCompleted,
            _ => BillingEventKind.Ignored
        };
        if (kind == BillingEventKind.Ignored)
        {
            return baseEvent(BillingEventKind.Ignored, $"invoice billing_reason {reason ?? "(none)"} is not acted on");
        }

        List<JsonElement> lines = Prop(Prop(inv, "lines"), "data") is { ValueKind: JsonValueKind.Array } arr ? arr.EnumerateArray().ToList() : [];
        JsonElement? details = Prop(Prop(inv, "parent"), "subscription_details");
        string? subscriptionId = IdOf(Prop(details, "subscription"))
            ?? lines.Select(l => IdOf(Prop(Prop(Prop(l, "parent"), "subscription_item_details"), "subscription"))).FirstOrDefault(x => x is not null);
        JsonElement? metadata = Prop(details, "metadata");

        long discount = 0;
        if (Prop(inv, "total_discount_amounts") is { ValueKind: JsonValueKind.Array } tda)
        {
            foreach (JsonElement d in tda.EnumerateArray()) discount += Long(d, "amount") ?? 0;
        }

        BillingEvent evt = baseEvent(kind, null) with
        {
            OrganizationId = Guid.TryParse(Str(metadata, StripeMetadataKeys.OrganizationId), out Guid org) ? org : null,
            CustomerId = IdOf(Prop(inv, "customer")),
            SubscriptionId = subscriptionId,
            TransactionId = Str(inv, "id"),
            AmountMinor = amountPaid,
            IsPaidTransaction = true,
            FirstSubscriptionInvoice = reason == "subscription_create",
            CurrencyCode = Str(inv, "currency")?.ToUpperInvariant(),
            DiscountId = FirstDiscountId(Prop(inv, "discounts")),
            DiscountTotal = discount > 0 ? discount.ToString(CultureInfo.InvariantCulture) : null,
            SubtotalMinor = Long(inv, "subtotal"),
            DiscountMinor = Prop(inv, "total_discount_amounts") is { ValueKind: JsonValueKind.Array } ? discount : null,
            TaxMinor = Long(inv, "total") is { } invoiceTotal && Long(inv, "total_excluding_tax") is { } excludingTax ? invoiceTotal - excludingTax : null,
            TotalMinor = Long(inv, "total"),
            PromotionId = Guid.TryParse(Str(metadata, StripeMetadataKeys.PromotionId), out Guid p) ? p : null
        };

        if (kind == BillingEventKind.UpgradePaid)
        {
            // Only lines charged above 0: the new price(s). The negative credit line for unused time is never an item (R-2).
            Dictionary<string, int> quantities = [];
            long? earliest = null;
            foreach (JsonElement line in lines.Where(l => (Long(l, "amount") ?? 0) > 0))
            {
                string? price = IdOf(Prop(Prop(Prop(line, "pricing"), "price_details"), "price"));
                if (price is null) continue;
                quantities[price] = quantities.GetValueOrDefault(price) + (int)(Long(line, "quantity") ?? 1);
                if (Long(Prop(line, "period"), "start") is { } ps && (earliest is null || ps < earliest)) earliest = ps;
            }

            return evt with
            {
                Items = quantities.Select(kv => new BillingLineItem(kv.Key, kv.Value)).ToList(),
                ProrationStartsAt = Time(earliest)
            };
        }

        List<BillingLineItem> all = [];
        foreach (JsonElement line in lines.Where(l => (Long(l, "amount") ?? 0) > 0))
        {
            if (IdOf(Prop(Prop(Prop(line, "pricing"), "price_details"), "price")) is { } price)
            {
                all.Add(new BillingLineItem(price, (int)(Long(line, "quantity") ?? 1)));
            }
        }

        JsonElement? firstLine = lines.Count > 0 ? lines[0] : null;
        return evt with
        {
            PeriodStart = Time(Long(Prop(firstLine, "period"), "start")),
            PeriodEnd = Time(Long(Prop(firstLine, "period"), "end")),
            Items = all
        };
    }

    // ---- refund.* ----

    private BillingEvent Refund(JsonElement r, Func<BillingEventKind, string?, BillingEvent> baseEvent)
    {
        string? status = Str(r, "status");
        switch (status)
        {
            case "succeeded":
                return baseEvent(BillingEventKind.PaymentRefunded, null) with
                {
                    AdjustmentId = Str(r, "id"),
                    TransactionId = IdOf(Prop(r, "payment_intent")),
                    AmountMinor = Long(r, "amount") ?? 0,
                    RefundAmountMinor = Long(r, "amount"),
                    CurrencyCode = Str(r, "currency")?.ToUpperInvariant()
                };
            case "pending" or "requires_action":
                return baseEvent(BillingEventKind.Ignored, "refund not final");
            case "failed" or "canceled":
                return baseEvent(BillingEventKind.Ignored, $"refund {status}; nothing to claw back");
            default:
                return baseEvent(BillingEventKind.Ignored, $"refund status {status ?? "(none)"} is not acted on");
        }
    }

    // ---- charge.dispute.* (DEC-18) ----

    private BillingEvent DisputeClosed(JsonElement d, Func<BillingEventKind, string?, BillingEvent> baseEvent)
    {
        string? status = Str(d, "status");
        if (status != "lost")
        {
            return baseEvent(BillingEventKind.Ignored, $"dispute closed with status {status ?? "(none)"}: nothing to claw back");
        }

        string? paymentIntent = IdOf(Prop(d, "payment_intent"));
        if (paymentIntent is null)
        {
            return baseEvent(BillingEventKind.Ignored, "lost dispute carries no payment_intent: cannot find the payment to claw back");
        }

        return baseEvent(BillingEventKind.PaymentRefunded, null) with
        {
            AdjustmentId = Str(d, "id"),
            TransactionId = paymentIntent,
            AmountMinor = Long(d, "amount") ?? 0,
            RefundAmountMinor = Long(d, "amount"),
            CurrencyCode = Str(d, "currency")?.ToUpperInvariant()
        };
    }

    private BillingEvent DisputeOpened(string eventId, JsonElement d, Func<BillingEventKind, string?, BillingEvent> baseEvent)
    {
        // No PII: ids only. This line is the operator's alert; the dispute itself is answered in the Stripe dashboard.
        logger?.LogWarning("Stripe dispute opened: event {EventId}, dispute {DisputeId}. No automatic action; respond in the Stripe dashboard.", eventId, Str(d, "id"));
        return baseEvent(BillingEventKind.Ignored, "dispute opened: no automatic action");
    }

    // ---- JSON helpers (tolerant: missing, null or wrong-typed reads as absent) ----

    internal static JsonElement? Prop(JsonElement? e, string name)
        => e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null ? v : null;

    internal static string? Str(JsonElement? e, string name)
        => Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    internal static long? Long(JsonElement? e, string name)
        => Prop(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out long l) ? l : null;

    /// <summary>An id that Stripe sends either as a string or as an expanded object carrying <c>id</c>.</summary>
    internal static string? IdOf(JsonElement? e) => e switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Object } o => Str(o, "id"),
        _ => null
    };

    internal static IEnumerable<JsonElement?> Items(JsonElement subscription)
        => Prop(Prop(subscription, "items"), "data") is { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray().Select(x => (JsonElement?)x)
            : [];

    internal static DateTime? Time(long? unixSeconds)
        => unixSeconds is { } s ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : null;

    private static int? PositiveInt(string? value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i > 0 ? i : null;

    /// <summary>
    /// The first discount's id: a promotion code or coupon id for a Checkout Session discount, a coupon id (or the
    /// discount id when only that is present) for an invoice discount. Null when there is none.
    /// </summary>
    private static string? FirstDiscountId(JsonElement? discounts)
    {
        if (discounts is not { ValueKind: JsonValueKind.Array } arr || arr.GetArrayLength() == 0) return null;
        JsonElement first = arr[0];
        if (first.ValueKind == JsonValueKind.String) return first.GetString();
        return IdOf(Prop(first, "promotion_code")) ?? CouponIdOf(first) ?? Str(first, "id");
    }
}
