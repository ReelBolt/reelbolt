using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Services.Billing.Stripe;

/// <summary>
/// Stripe (optionally with Managed Payments) over plain <see cref="HttpClient"/> and <c>System.Text.Json</c>: no SDK.
/// Wire details live in <see cref="StripeHttp"/>; webhook mapping in <see cref="StripeEventParser"/>. See the
/// stripe-billing-provider initiative's decisions (DEC-1..DEC-17) for the rules this class implements.
/// </summary>
public sealed class StripeBillingProvider : IBillingProvider
{
    public const string ProviderName = StripeEventParser.ProviderName;

    /// <summary>The Stripe plugin's required Checkout integration identifier (API 2026-03-25.dahlia and later).</summary>
    public const string IntegrationIdentifier = "reelbolt-checkout-qxmvbtrk";

    private static readonly HashSet<string> LiveStatuses = new(StringComparer.Ordinal) { "active", "trialing", "past_due", "paused", "incomplete" };

    private readonly IOptions<StripeOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<StripeBillingProvider>? _logger;
    private readonly StripeHttp _http;
    private int _portalConfigWarned;

    public StripeBillingProvider(
        HttpClient http,
        IOptions<StripeOptions> options,
        TimeProvider? time = null,
        ILogger<StripeBillingProvider>? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _http = new StripeHttp(http, () => options.Value, delay, null, logger);
    }

    private StripeOptions Stripe => _options.Value;

    public string Name => ProviderName;

    public string DisplayName => "Stripe";

    public bool IsConfigured => Stripe.IsConfigured;

    public bool ReconcilesWithoutUpdatedAt => true;

    public bool OwnsTransaction(string providerTransactionId)
        => providerTransactionId.StartsWith("cs_", StringComparison.Ordinal)
            || providerTransactionId.StartsWith("pi_", StringComparison.Ordinal)
            || providerTransactionId.StartsWith("in_", StringComparison.Ordinal);

    public int MaxSelfServeSeats => Stripe.MaxSelfServeSeats;

    public IReadOnlyList<int> TopUpPacks => Stripe.TopUpPriceIds
        .Where(kv => int.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int c) && c > 0 && !string.IsNullOrWhiteSpace(kv.Value))
        .Select(kv => int.Parse(kv.Key, CultureInfo.InvariantCulture)).Order().ToList();

    public string? TopUpPriceId(int credits)
        => Stripe.TopUpPriceIds.TryGetValue(credits.ToString(CultureInfo.InvariantCulture), out string? id) && !string.IsNullOrWhiteSpace(id) ? id : null;

    private StripeEventParser Parser => new(Stripe, _time, credits => TopUpPriceId(credits) is not null, _logger);

    public BillingEvent VerifyAndParseWebhook(string rawBody, IReadOnlyDictionary<string, string> headers)
        => Parser.Parse(rawBody, headers);

    // ------------------------------------------------------------------ checkout

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct)
    {
        string returnBase = ReturnBase();
        bool topUp = request.TopUpCredits is not null;
        string priceId;
        int quantity;
        if (request.TopUpCredits is { } credits)
        {
            priceId = TopUpPriceId(credits) ?? throw new InvalidOperationException($"No Stripe price id is configured for the {credits}-credit top-up.");
            quantity = 1;
        }
        else
        {
            Plan plan = request.Plan ?? throw new ArgumentException("A checkout needs a plan or a top-up.", nameof(request));
            priceId = (request.Interval == BillingInterval.Annual ? BillingEventProcessor.AnnualPriceIdOf(plan, ProviderName) : MonthlyPriceId(plan))
                ?? throw new InvalidOperationException($"Plan {plan.Key} has no Stripe price id for the {request.Interval} interval.");
            quantity = plan.Kind == PlanKind.PerSeat ? Math.Max(request.Seats, plan.MinSeats ?? 1) : 1;
        }

        string customerId = request.ExistingCustomerId ?? await CreateCustomerAsync(request, ct);

        Dictionary<string, object?> metadata = new()
        {
            [StripeMetadataKeys.OrganizationId] = request.OrganizationId.ToString(),
            [StripeMetadataKeys.UserId] = request.UserId.ToString(),
            [StripeMetadataKeys.PromotionId] = request.PromotionId?.ToString(),
            [StripeMetadataKeys.BonusPromotionId] = request.BonusPromotionId?.ToString(),
            [StripeMetadataKeys.TopUpCredits] = request.TopUpCredits?.ToString(CultureInfo.InvariantCulture)
        };

        StripeForm form = new StripeForm()
            .Add("mode", topUp ? "payment" : "subscription")
            .Add("ui_mode", "hosted_page")
            .Add("integration_identifier", IntegrationIdentifier)
            .Add("customer", customerId)
            .Add("client_reference_id", request.OrganizationId.ToString())
            .Add("line_items", new[] { new Dictionary<string, object?> { ["price"] = priceId, ["quantity"] = quantity } })
            .Add("metadata", metadata)
            .Add(topUp ? "payment_intent_data" : "subscription_data", new Dictionary<string, object?> { ["metadata"] = metadata })
            .Add("success_url", $"{returnBase}/app/checkout?session_id={{CHECKOUT_SESSION_ID}}")
            .Add("cancel_url", $"{returnBase}/app/settings/billing");

        if (!string.IsNullOrWhiteSpace(request.DiscountId))
        {
            form.Add("discounts", new[]
            {
                new Dictionary<string, object?> { [request.DiscountId.StartsWith("promo_", StringComparison.Ordinal) ? "promotion_code" : "coupon"] = request.DiscountId }
            });
        }

        if (Stripe.ManagedPayments)
        {
            form.Add("managed_payments", new Dictionary<string, object?> { ["enabled"] = true });
        }
        else
        {
            form.Add("automatic_tax", new Dictionary<string, object?> { ["enabled"] = true });

            // DEC-21 / H3: automatic tax on an existing customer needs the address Checkout collects saved back
            // to the customer. Managed Payments forbids customer_update, so only this branch sends it.
            form.Add("customer_update", new Dictionary<string, object?> { ["address"] = "auto", ["name"] = "auto" });
        }

        if (BrandingSettings() is { } branding)
        {
            form.Add("branding_settings", branding);
        }

        JsonElement session = await _http.PostAsync("/v1/checkout/sessions", form, null, ct);
        string? url = StripeEventParser.Str(session, "url");
        string? id = StripeEventParser.Str(session, "id");
        if (url is null || id is null)
        {
            throw new HttpRequestException("Stripe returned no checkout URL.");
        }

        return new CheckoutSession(url, id, customerId);
    }

    /// <summary>The public origin for return URLs; a missing one is a 503 "not configured", never an unhandled exception.</summary>
    private string ReturnBase()
        => string.IsNullOrWhiteSpace(Stripe.ReturnBaseUrl)
            ? throw new BillingProviderException("stripe_not_configured", 503, "Billing:Stripe:ReturnBaseUrl is not set.")
            : Stripe.ReturnBaseUrl.TrimEnd('/');

    private async Task<string> CreateCustomerAsync(CheckoutRequest request, CancellationToken ct)
    {
        StripeForm form = new StripeForm()
            .Add("email", string.IsNullOrWhiteSpace(request.Email) ? null : request.Email)
            .Add("metadata", new Dictionary<string, object?> { [StripeMetadataKeys.OrganizationId] = request.OrganizationId.ToString() });
        JsonElement customer = await _http.PostAsync("/v1/customers", form, $"customer:{request.OrganizationId}:{EmailHash(request.Email)}", ct);
        return StripeEventParser.Str(customer, "id") ?? throw new HttpRequestException("Stripe returned no customer id.");
    }

    /// <summary>First 16 hex chars of the SHA-256 of the lower-cased email (never the address itself) for the customer idempotency key.</summary>
    internal static string EmailHash(string? email)
        => Hex(SHA256.HashData(Encoding.UTF8.GetBytes((email ?? "").Trim().ToLowerInvariant())))[..16];

    private static string? MonthlyPriceId(Plan plan)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(plan.ProviderPriceIdsJson) ? "{}" : plan.ProviderPriceIdsJson);
            if (StripeEventParser.Str(StripeEventParser.Prop(doc.RootElement, "stripe"), "monthly") is { } monthly) return monthly;
        }
        catch (JsonException)
        {
            // Falls through to the generic reader, which treats a malformed column as "no ids".
        }

        return BillingEventProcessor.PriceIdFor(plan, ProviderName, BillingInterval.Monthly);
    }

    // ------------------------------------------------------------------ subscriptions (live lookups)

    public async Task<bool> HasLiveSubscriptionAsync(string providerCustomerId, Guid organizationId, CancellationToken ct)
        => await FindLiveSubscriptionIdAsync(providerCustomerId, organizationId, ct) is not null;

    public async Task<string?> FindLiveSubscriptionIdAsync(string providerCustomerId, Guid organizationId, CancellationToken ct)
    {
        string org = organizationId.ToString();
        string? unnamed = null;
        string? startingAfter = null;
        while (true)
        {
            StripeForm query = new StripeForm().Add("customer", providerCustomerId).Add("status", "all").Add("limit", 100).Add("starting_after", startingAfter);
            JsonElement page = await _http.GetAsync("/v1/subscriptions", query, ct);
            if (StripeEventParser.Prop(page, "data") is not { ValueKind: JsonValueKind.Array } data) return unnamed;

            foreach (JsonElement sub in data.EnumerateArray())
            {
                if (StripeEventParser.Str(sub, "status") is not { } status || !LiveStatuses.Contains(status)) continue;
                string? id = StripeEventParser.Str(sub, "id");
                if (id is null) continue;
                string? named = StripeEventParser.Str(StripeEventParser.Prop(sub, "metadata"), StripeMetadataKeys.OrganizationId);
                if (named is null) unnamed ??= id;
                else if (string.Equals(named, org, StringComparison.OrdinalIgnoreCase)) return id;
            }

            if (StripeEventParser.Prop(page, "has_more") is { ValueKind: JsonValueKind.True } && data.GetArrayLength() > 0)
            {
                startingAfter = StripeEventParser.Str(data[data.GetArrayLength() - 1], "id");
                if (startingAfter is null) return unnamed;
            }
            else
            {
                return unnamed;
            }
        }
    }

    // ------------------------------------------------------------------ portal

    public async Task<PortalSession> CreatePortalSessionAsync(string providerCustomerId, string? providerSubscriptionId, CancellationToken ct)
    {
        string returnUrl = $"{ReturnBase()}/app/settings/billing";
        string? configuration = string.IsNullOrWhiteSpace(Stripe.PortalConfigurationId) ? null : Stripe.PortalConfigurationId;
        if (configuration is null && Interlocked.Exchange(ref _portalConfigWarned, 1) == 0)
        {
            _logger?.LogWarning("Billing:Stripe:PortalConfigurationId is not set; the Stripe account's default portal configuration applies (plan switching and quantity updates may be on).");
        }

        StripeForm Base() => new StripeForm().Add("customer", providerCustomerId).Add("configuration", configuration).Add("return_url", returnUrl);

        string overview = await PortalUrlAsync(Base(), ct) ?? throw new HttpRequestException("Stripe returned no portal URL.");
        string? cancel = null;
        string? payment = null;
        if (!string.IsNullOrWhiteSpace(providerSubscriptionId))
        {
            cancel = await TryDeepLinkAsync(Base().Add("flow_data", new Dictionary<string, object?>
            {
                ["type"] = "subscription_cancel",
                ["subscription_cancel"] = new Dictionary<string, object?> { ["subscription"] = providerSubscriptionId }
            }), "subscription_cancel", ct);
            payment = await TryDeepLinkAsync(Base().Add("flow_data", new Dictionary<string, object?> { ["type"] = "payment_method_update" }), "payment_method_update", ct);
        }

        return new PortalSession(overview, cancel, payment);
    }

    /// <summary>The <c>branding_settings</c> object for a checkout session, or null when nothing is configured.</summary>
    private Dictionary<string, object?>? BrandingSettings()
    {
        StripeBrandingOptions b = Stripe.Branding;
        if (!b.Any)
        {
            return null;
        }

        var settings = new Dictionary<string, object?>();
        void Set(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) settings[key] = value.Trim();
        }

        void SetImage(string key, string? url)
        {
            if (!string.IsNullOrWhiteSpace(url)) settings[key] = new Dictionary<string, object?> { ["type"] = "url", ["url"] = url.Trim() };
        }

        Set("background_color", b.BackgroundColor);
        Set("button_color", b.ButtonColor);
        Set("border_style", b.BorderStyle);
        Set("font_family", b.FontFamily);
        Set("display_name", b.DisplayName);
        SetImage("logo", b.LogoUrl);
        SetImage("icon", b.IconUrl);
        return settings;
    }

    private async Task<string?> PortalUrlAsync(StripeForm form, CancellationToken ct)
        => StripeEventParser.Str(await _http.PostAsync("/v1/billing_portal/sessions", form, null, ct), "url");

    private async Task<string?> TryDeepLinkAsync(StripeForm form, string flow, CancellationToken ct)
    {
        try
        {
            return await PortalUrlAsync(form, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning("Stripe portal deep link {Flow} could not be minted ({Type}); the overview link still works.", flow, ex.GetType().Name);
            return null;
        }
    }

    // ------------------------------------------------------------------ subscription changes

    public async Task CancelSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
        => await _http.PostAsync($"/v1/subscriptions/{providerSubscriptionId}", new StripeForm().Add("cancel_at_period_end", true), null, ct);

    public async Task ResumeSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
        => await _http.PostAsync($"/v1/subscriptions/{providerSubscriptionId}", new StripeForm().Add("cancel_at_period_end", false), null, ct);

    public async Task ChangePlanAsync(string providerSubscriptionId, IReadOnlyList<BillingLineItem> items, PlanChangeTiming timing, PlanChangeDiscount discount, CancellationToken ct)
    {
        JsonElement current = await _http.GetAsync($"/v1/subscriptions/{providerSubscriptionId}", null, ct);
        List<(string Id, string Price, long Quantity)> existing = [];
        foreach (JsonElement? item in StripeEventParser.Items(current))
        {
            if (StripeEventParser.Str(item, "id") is { } id)
            {
                existing.Add((id, StripeEventParser.IdOf(StripeEventParser.Prop(item, "price")) ?? "", StripeEventParser.Long(item, "quantity") ?? 1));
            }
        }

        StripeForm form = new();
        int n = Math.Max(existing.Count, items.Count);
        for (int i = 0; i < n; i++)
        {
            string prefix = $"items[{i}]";
            if (i < items.Count)
            {
                if (i < existing.Count) form.Add($"{prefix}[id]", existing[i].Id);
                form.Add($"{prefix}[price]", items[i].PriceId).Add($"{prefix}[quantity]", items[i].Quantity);
            }
            else
            {
                form.Add($"{prefix}[id]", existing[i].Id).Add($"{prefix}[deleted]", true);
            }
        }

        form.Add("proration_behavior", timing == PlanChangeTiming.Immediately ? "always_invoice" : "none");
        if (timing == PlanChangeTiming.Immediately)
        {
            // DEC-6: an unpaid upgrade must fail instead of moving the subscription to the higher plan.
            form.Add("payment_behavior", "error_if_incomplete");
        }

        if (discount == PlanChangeDiscount.Remove) form.Add("discounts", "");

        // DEC-16 (revised): the update is declarative, so re-applying it is a no-op; the key only has to make the
        // StripeHttp retries inside THIS call safe. A fresh nonce per call means an A->B->A->B round trip can
        // never replay a stale cached response.
        string key = $"change:{providerSubscriptionId}:{Guid.NewGuid():N}";
        await _http.PostAsync($"/v1/subscriptions/{providerSubscriptionId}", form, key, ct);
    }

    public async Task<ProviderSubscriptionState> GetSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
    {
        JsonElement sub = await _http.GetAsync($"/v1/subscriptions/{providerSubscriptionId}", new StripeForm().Add("expand", new[] { "discounts" }), ct);
        List<BillingLineItem> items = ItemsOf(sub);
        JsonElement? first = StripeEventParser.Items(sub).FirstOrDefault();
        DateTime? end = StripeEventParser.Time(StripeEventParser.Long(first, "current_period_end") ?? StripeEventParser.Long(sub, "current_period_end"));
        DateTime? start = StripeEventParser.Time(StripeEventParser.Long(first, "current_period_start") ?? StripeEventParser.Long(sub, "current_period_start"));
        bool cancel = StripeEventParser.Prop(sub, "cancel_at_period_end") is { ValueKind: JsonValueKind.True } || StripeEventParser.Long(sub, "cancel_at") is not null;

        ProviderSubscriptionDiscount? discount = null;
        if (StripeEventParser.Prop(sub, "discounts") is { ValueKind: JsonValueKind.Array } ds && ds.GetArrayLength() > 0)
        {
            JsonElement d = ds[0];
            string? id = d.ValueKind == JsonValueKind.Object ? StripeEventParser.CouponIdOf(d) ?? StripeEventParser.Str(d, "id") : d.GetString();
            if (id is not null) discount = new ProviderSubscriptionDiscount(id, StripeEventParser.Time(StripeEventParser.Long(d.ValueKind == JsonValueKind.Object ? d : null, "end")));
        }

        return new ProviderSubscriptionState(StripeEventParser.Str(sub, "status") ?? "", items, cancel, end, start, discount);
    }

    private static List<BillingLineItem> ItemsOf(JsonElement sub)
    {
        List<BillingLineItem> items = [];
        foreach (JsonElement? item in StripeEventParser.Items(sub))
        {
            if (StripeEventParser.IdOf(StripeEventParser.Prop(item, "price")) is { } price)
            {
                items.Add(new BillingLineItem(price, (int)(StripeEventParser.Long(item, "quantity") ?? 1)));
            }
        }

        return items;
    }

    // ------------------------------------------------------------------ reconcile

    public async Task<BillingSnapshot> SnapshotSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
    {
        DateTime now = _time.GetUtcNow().UtcDateTime;
        JsonElement sub = await _http.GetAsync($"/v1/subscriptions/{providerSubscriptionId}", null, ct);
        string id = StripeEventParser.Str(sub, "id") ?? providerSubscriptionId;

        List<BillingLineItem> items = ItemsOf(sub);
        JsonElement? first = StripeEventParser.Items(sub).FirstOrDefault();
        string state = string.Join('|',
            StripeEventParser.Str(sub, "status"),
            string.Join(',', items.Select(i => $"{i.PriceId}:{i.Quantity}")),
            StripeEventParser.Long(first, "current_period_end") ?? StripeEventParser.Long(sub, "current_period_end"),
            StripeEventParser.Prop(sub, "cancel_at_period_end") is { ValueKind: JsonValueKind.True },
            StripeEventParser.Long(sub, "cancel_at"));
        string eventId = $"reconcile:sub:{id}:{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(state)))[..12]}:{now.Ticks}";

        BillingEvent evt = Parser.MapSubscriptionObject(sub, eventId, "subscription.reconciled", now);
        evt = evt with { ChangedAt = await UpgradeProrationStartAsync(id, items, ct) };
        return new BillingSnapshot(evt, Envelope(eventId, "subscription.reconciled", now, sub), null);
    }

    /// <summary>
    /// DEC-5: the proration start of the newest paid <c>subscription_update</c> invoice whose positive lines equal the
    /// subscription's current items, so a reconciler-healed upgrade still pairs with its payment. Null when none matches.
    /// </summary>
    private async Task<DateTime?> UpgradeProrationStartAsync(string subscriptionId, List<BillingLineItem> items, CancellationToken ct)
    {
        JsonElement page;
        try
        {
            page = await _http.GetAsync("/v1/invoices", new StripeForm().Add("subscription", subscriptionId).Add("status", "paid").Add("limit", 10), ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not System.Net.HttpStatusCode.TooManyRequests)
        {
            // Best effort: the snapshot still heals the plan; only the pairing of a paid proration is lost.
            _logger?.LogWarning("Stripe invoices of subscription {SubscriptionId} could not be listed for the reconcile ({Type}); ChangedAt stays null.", subscriptionId, ex.GetType().Name);
            return null;
        }

        if (StripeEventParser.Prop(page, "data") is not { ValueKind: JsonValueKind.Array } data) return null;

        string Key(IEnumerable<BillingLineItem> l) => string.Join(',', l.GroupBy(i => i.PriceId).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Sum(i => i.Quantity)}"));
        string current = Key(items);
        foreach (JsonElement inv in data.EnumerateArray().OrderByDescending(i => StripeEventParser.Long(i, "created") ?? 0))
        {
            if (StripeEventParser.Str(inv, "billing_reason") != "subscription_update" || (StripeEventParser.Long(inv, "amount_paid") ?? 0) <= 0) continue;
            BillingEvent parsed = Parser.MapInvoiceObject(inv, "reconcile:probe", "transaction.reconciled", _time.GetUtcNow().UtcDateTime);
            if (parsed.Kind == BillingEventKind.UpgradePaid && parsed.ProrationStartsAt is { } start && Key(parsed.Items) == current) return start;
        }

        return null;
    }

    public async Task<IReadOnlyList<BillingSnapshot>> CompletedUpgradeTransactionsAsync(string providerSubscriptionId, DateTime since, CancellationToken ct)
    {
        List<BillingSnapshot> result = [];
        string? startingAfter = null;
        while (true)
        {
            StripeForm query = new StripeForm()
                .Add("subscription", providerSubscriptionId)
                .Add("created[gte]", new DateTimeOffset(DateTime.SpecifyKind(since, DateTimeKind.Utc)).ToUnixTimeSeconds())
                .Add("status", "paid").Add("limit", 100).Add("starting_after", startingAfter);
            JsonElement page = await _http.GetAsync("/v1/invoices", query, ct);
            if (StripeEventParser.Prop(page, "data") is not { ValueKind: JsonValueKind.Array } data) return result;

            foreach (JsonElement inv in data.EnumerateArray())
            {
                if (StripeEventParser.Str(inv, "billing_reason") != "subscription_update" || (StripeEventParser.Long(inv, "amount_paid") ?? 0) <= 0) continue;
                string id = StripeEventParser.Str(inv, "id") ?? "";
                long? paidAt = StripeEventParser.Long(StripeEventParser.Prop(inv, "status_transitions"), "paid_at") ?? StripeEventParser.Long(inv, "created");
                DateTime occurred = StripeEventParser.Time(paidAt) ?? _time.GetUtcNow().UtcDateTime;
                string eventId = $"reconcile:txn:{id}";
                BillingEvent evt = Parser.MapInvoiceObject(inv, eventId, "transaction.reconciled", occurred);
                if (evt.Kind == BillingEventKind.UpgradePaid)
                {
                    result.Add(new BillingSnapshot(evt, Envelope(eventId, "transaction.reconciled", occurred, inv), null));
                }
            }

            if (StripeEventParser.Prop(page, "has_more") is { ValueKind: JsonValueKind.True } && data.GetArrayLength() > 0
                && StripeEventParser.Str(data[data.GetArrayLength() - 1], "id") is { } last)
            {
                startingAfter = last;
            }
            else
            {
                return result;
            }
        }
    }

    private string Envelope(string eventId, string type, DateTime at, JsonElement obj)
        => new JsonObject
        {
            ["id"] = eventId,
            ["object"] = "event",
            ["type"] = type,
            ["created"] = new DateTimeOffset(at, TimeSpan.Zero).ToUnixTimeSeconds(),
            ["livemode"] = Stripe.IsLiveMode,
            ["data"] = new JsonObject { ["object"] = JsonNode.Parse(obj.GetRawText()) }
        }.ToJsonString();

    // ------------------------------------------------------------------ confirm on return

    /// <inheritdoc />
    public async Task<CheckoutConfirmation> ConfirmCheckoutAsync(string checkoutId, Guid organizationId, CancellationToken ct)
    {
        CheckoutConfirmation notFound = new(CheckoutConfirmationStatus.NotFound, "", []);
        JsonElement session;
        try
        {
            session = await _http.GetAsync($"/v1/checkout/sessions/{Uri.EscapeDataString(checkoutId)}", null, ct);
        }
        catch (BillingProviderException ex) when (ex.Status is 404 or 400)
        {
            return notFound;
        }

        // The organization comes from what this platform wrote at checkout time, never from the caller.
        // Only metadata.organization_id (our checkout always sets it): a Payment Link lets a buyer choose client_reference_id.
        string? owner = StripeEventParser.Str(StripeEventParser.Prop(session, "metadata"), StripeMetadataKeys.OrganizationId);
        if (!Guid.TryParse(owner, out Guid sessionOrg) || sessionOrg != organizationId)
        {
            return notFound;
        }

        string kind = StripeEventParser.Str(session, "mode") == "subscription" ? "subscription" : "topUp";
        switch (StripeEventParser.Str(session, "status"))
        {
            case "expired":
                return new(CheckoutConfirmationStatus.Abandoned, kind, []);
            case "complete":
                break;
            default:
                return new(CheckoutConfirmationStatus.Pending, kind, []);
        }

        string? paymentStatus = StripeEventParser.Str(session, "payment_status");
        if (paymentStatus is not ("paid" or "no_payment_required"))
        {
            return new(CheckoutConfirmationStatus.Pending, kind, []);
        }

        DateTime now = _time.GetUtcNow().UtcDateTime;
        List<(string Id, string Type, JsonElement Obj)> objects = [];
        if (kind == "subscription")
        {
            string? subId = StripeEventParser.IdOf(StripeEventParser.Prop(session, "subscription"));
            if (subId is null) return new(CheckoutConfirmationStatus.Pending, kind, []);

            JsonElement sub = await _http.GetAsync($"/v1/subscriptions/{Uri.EscapeDataString(subId)}", null, ct);
            string state = string.Join('|',
                StripeEventParser.Str(sub, "status"),
                string.Join(',', ItemsOf(sub).Select(i => $"{i.PriceId}:{i.Quantity}")),
                StripeEventParser.Long(StripeEventParser.Items(sub).FirstOrDefault(), "current_period_end") ?? StripeEventParser.Long(sub, "current_period_end"),
                StripeEventParser.Prop(sub, "cancel_at_period_end") is { ValueKind: JsonValueKind.True },
                StripeEventParser.Long(sub, "cancel_at"));
            objects.Add(($"confirm:{subId}:{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(state)))[..12]}", "customer.subscription.updated", sub));

            // The session's own invoice; else the subscription's latest one only while it is the creation invoice (after a
            // renewal the latest is a different invoice). Only a paid invoice (a $0 first one is paid) becomes an event.
            string? sessionInvoice = StripeEventParser.IdOf(StripeEventParser.Prop(session, "invoice"));
            string? invoiceId = sessionInvoice ?? StripeEventParser.IdOf(StripeEventParser.Prop(sub, "latest_invoice"));
            if (invoiceId is not null)
            {
                JsonElement invoice = await _http.GetAsync($"/v1/invoices/{Uri.EscapeDataString(invoiceId)}", null, ct);
                bool ours = sessionInvoice is not null || StripeEventParser.Str(invoice, "billing_reason") == "subscription_create";
                if (ours && StripeEventParser.Str(invoice, "status") == "paid")
                {
                    objects.Add(($"confirm:{invoiceId}", "invoice.paid", invoice));
                }
            }
        }
        else
        {
            objects.Add(($"confirm:{checkoutId}", "checkout.session.completed", session));
        }

        StripeEventParser parser = Parser;
        List<BillingEvent> events = [];
        List<string> raws = [];
        foreach ((string id, string type, JsonElement obj) in objects)
        {
            string raw = new JsonObject
            {
                ["id"] = id,
                ["object"] = "event",
                ["api_version"] = StripeOptions.ApiVersion,
                ["type"] = type,
                ["created"] = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds(),
                ["livemode"] = Stripe.IsLiveMode,
                ["data"] = new JsonObject { ["object"] = JsonNode.Parse(obj.GetRawText()) }
            }.ToJsonString();
            using JsonDocument doc = JsonDocument.Parse(raw);
            BillingEvent evt = parser.ParseVerified(doc.RootElement);
            if (evt.OrganizationId is { } eventOrg && eventOrg != organizationId) return notFound;

            if (type == "invoice.paid") evt = evt with { CheckoutId = checkoutId };
            evt = await ResolveAsync(evt, ct);
            if (evt.OrganizationId is { } resolvedOrg && resolvedOrg != organizationId) return notFound;
            events.Add(evt);
            raws.Add(raw);
        }

        return new(CheckoutConfirmationStatus.Confirmed, kind, events, raws);
    }

    // ------------------------------------------------------------------ webhook completion (DEC-4, DEC-5)

    public async Task<BillingEvent> ResolveAsync(BillingEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case BillingEventKind.SubscriptionChanged when evt.SubscriptionId is { } subId:
                return await ResolveSubscriptionAsync(evt, subId, ct);

            case BillingEventKind.UpgradePaid or BillingEventKind.TransactionCompleted:
                if (evt.DiscountId is { } di && di.StartsWith("di_", StringComparison.Ordinal) && evt.TransactionId is { } inv)
                {
                    evt = evt with { DiscountId = await CouponForInvoiceDiscountAsync(inv, di, ct) ?? di };
                }

                if (evt is { FirstSubscriptionInvoice: true, CheckoutId: null, SubscriptionId: { } createdSub })
                {
                    evt = evt with { CheckoutId = await CheckoutSessionForSubscriptionAsync(createdSub, ct) };
                }

                return evt with { FeeMinor = evt.TransactionId is { } txn ? await InvoiceFeeAsync(txn, ct) : null };

            case BillingEventKind.TopUpPaid when evt.TransactionId is { } pi:
                return evt with { FeeMinor = await PaymentIntentFeeAsync(pi, ct) };

            case BillingEventKind.PaymentRefunded when evt.TransactionId is { } pi:
                return await ResolveRefundAsync(evt, pi, ct);

            default:
                return evt;
        }
    }

    private async Task<BillingEvent> ResolveSubscriptionAsync(BillingEvent evt, string subId, CancellationToken ct)
    {
        // DEC-5: the stale-guard clock is the moment BEFORE the read (the state we got is at least that new); when the
        // change really happened is the webhook's own created time, kept in ChangedAt for upgrade pairing.
        DateTime fetchedAt = _time.GetUtcNow().UtcDateTime;
        DateTime changedAt = evt.ChangedAt ?? evt.OccurredAt;
        JsonElement fresh;
        try
        {
            fresh = await _http.GetAsync($"/v1/subscriptions/{subId}", null, ct);
        }
        catch (BillingProviderException ex) when (ex.Status == 404 && (evt.Status == SubscriptionStatus.Canceled || evt.EventType.EndsWith(".deleted", StringComparison.Ordinal)))
        {
            return evt with { OccurredAt = fetchedAt, ChangedAt = changedAt };
        }

        bool deleted = evt.EventType.EndsWith(".deleted", StringComparison.Ordinal);
        BillingEvent rebuilt = Parser.MapSubscriptionObject(fresh, evt.EventId, evt.EventType, fetchedAt, deleted);
        return rebuilt with { OrganizationId = rebuilt.OrganizationId ?? evt.OrganizationId, ChangedAt = changedAt };
    }

    /// <summary>
    /// DEC-20: the Checkout Session that created a subscription, so the promotion reservation stored under the session id
    /// is completed by the first invoice. A failure is swallowed (logged, null): the reservation sweeper handles leftovers.
    /// </summary>
    private async Task<string?> CheckoutSessionForSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        try
        {
            JsonElement page = await _http.GetAsync("/v1/checkout/sessions", new StripeForm().Add("subscription", subscriptionId).Add("limit", 1), ct);
            return StripeEventParser.Prop(page, "data") is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0
                ? StripeEventParser.Str(a[0], "id")
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            _logger?.LogWarning("Stripe checkout session for subscription {SubscriptionId} could not be read ({Type}); a discount reservation is left to the sweeper.", subscriptionId, ex.GetType().Name);
            return null;
        }
    }

    private async Task<string?> CouponForInvoiceDiscountAsync(string invoiceId, string discountId, CancellationToken ct)
    {
        JsonElement inv = await _http.GetAsync($"/v1/invoices/{invoiceId}", new StripeForm().Add("expand", new[] { "discounts" }), ct);
        if (StripeEventParser.Prop(inv, "discounts") is not { ValueKind: JsonValueKind.Array } ds) return null;
        string? firstCoupon = null;
        foreach (JsonElement d in ds.EnumerateArray())
        {
            if (d.ValueKind != JsonValueKind.Object) continue;
            string? coupon = StripeEventParser.CouponIdOf(d);
            if (StripeEventParser.Str(d, "id") == discountId && coupon is not null) return coupon;
            firstCoupon ??= coupon;
        }

        return firstCoupon;
    }

    private async Task<long?> InvoiceFeeAsync(string invoiceId, CancellationToken ct)
    {
        try
        {
            // Stripe refuses an expansion deeper than four levels, so this is two reads: the invoice's payment,
            // then its PaymentIntent with the balance transaction (found against a real sandbox).
            JsonElement payments = await _http.GetAsync("/v1/invoice_payments", new StripeForm().Add("invoice", invoiceId).Add("status", "paid"), ct);
            JsonElement? first = StripeEventParser.Prop(payments, "data") is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0 ? a[0] : null;
            string? paymentIntentId = StripeEventParser.IdOf(StripeEventParser.Prop(StripeEventParser.Prop(first, "payment"), "payment_intent"));
            return paymentIntentId is null ? null : await PaymentIntentFeeAsync(paymentIntentId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            _logger?.LogInformation("Stripe fee for invoice {InvoiceId} could not be read ({Type}); FeeMinor stays null.", invoiceId, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Stripe's own fee in minor units. Under Managed Payments <c>balance_transaction.fee</c> also carries the
    /// sales tax Stripe withholds (a <c>withheld_tax</c> entry in <c>fee_details</c>, seen against a real sandbox:
    /// 393 = 75 + 32 + 286 tax), which is not a fee; it is left out. Without <c>fee_details</c> the total is used.
    /// </summary>
    internal static long? FeeWithoutWithheldTax(JsonElement? balanceTransaction)
    {
        long? total = StripeEventParser.Long(balanceTransaction, "fee");
        if (total is null || StripeEventParser.Prop(balanceTransaction, "fee_details") is not { ValueKind: JsonValueKind.Array } details || details.GetArrayLength() == 0) return total;
        long fee = 0;
        foreach (JsonElement d in details.EnumerateArray())
        {
            if (!string.Equals(StripeEventParser.Str(d, "type"), "withheld_tax", StringComparison.Ordinal)) fee += StripeEventParser.Long(d, "amount") ?? 0;
        }

        return fee;
    }

    private async Task<long?> PaymentIntentFeeAsync(string paymentIntentId, CancellationToken ct)
    {
        try
        {
            JsonElement pi = await _http.GetAsync($"/v1/payment_intents/{paymentIntentId}", new StripeForm().Add("expand", new[] { "latest_charge.balance_transaction" }), ct);
            return FeeWithoutWithheldTax(StripeEventParser.Prop(StripeEventParser.Prop(pi, "latest_charge"), "balance_transaction"));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            _logger?.LogInformation("Stripe fee for payment intent {PaymentIntentId} could not be read ({Type}); FeeMinor stays null.", paymentIntentId, ex.GetType().Name);
            return null;
        }
    }

    private async Task<BillingEvent> ResolveRefundAsync(BillingEvent evt, string paymentIntentId, CancellationToken ct)
    {
        JsonElement payments = await _http.GetAsync("/v1/invoice_payments", new StripeForm()
            .Add("payment[type]", "payment_intent").Add("payment[payment_intent]", paymentIntentId).Add("status", "paid"), ct);
        string? invoiceId = StripeEventParser.Prop(payments, "data") is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0
            ? StripeEventParser.IdOf(StripeEventParser.Prop(a[0], "invoice"))
            : null;

        string? subscriptionId = null;
        bool subscriptionInvoice = false;
        if (invoiceId is not null)
        {
            JsonElement inv = await _http.GetAsync($"/v1/invoices/{invoiceId}", null, ct);
            subscriptionId = StripeEventParser.IdOf(StripeEventParser.Prop(StripeEventParser.Prop(StripeEventParser.Prop(inv, "parent"), "subscription_details"), "subscription"));
            // The same rule the parser used when it recorded the payment: the invoice-level subscription, else the
            // first line's, and a subscription_* billing reason always means a subscription invoice.
            if (subscriptionId is null && StripeEventParser.Prop(StripeEventParser.Prop(inv, "lines"), "data") is { ValueKind: JsonValueKind.Array } lines)
            {
                foreach (JsonElement line in lines.EnumerateArray())
                {
                    subscriptionId = StripeEventParser.IdOf(StripeEventParser.Prop(StripeEventParser.Prop(StripeEventParser.Prop(line, "parent"), "subscription_item_details"), "subscription"));
                    if (subscriptionId is not null) break;
                }
            }

            subscriptionInvoice = subscriptionId is not null || (StripeEventParser.Str(inv, "billing_reason") is { } reason && reason.StartsWith("subscription_", StringComparison.Ordinal));
        }

        JsonElement intent = await _http.GetAsync($"/v1/payment_intents/{paymentIntentId}", new StripeForm().Add("expand", new[] { "latest_charge" }), ct);
        JsonElement? charge = StripeEventParser.Prop(intent, "latest_charge");
        long? charged = StripeEventParser.Long(charge, "amount");
        long? refunded = StripeEventParser.Long(charge, "amount_refunded");
        // A lost dispute (DEC-18) leaves amount_refunded at 0: it is full when the disputed amount covers the charge.
        // A dispute is recognised by its event type, not by its id prefix (real ids start with `du_`, the docs show `dp_`).
        bool full = evt.EventType.StartsWith("charge.dispute.", StringComparison.Ordinal)
            ? charged is { } dc && (refunded ?? 0) + evt.AmountMinor >= dc
            : charged is { } c && refunded is { } r && r >= c;

        return evt with
        {
            // Under Managed Payments a one-time payment (a top-up) has an invoice too (billing_reason manual): only a
            // subscription's invoice is the transaction we recorded; a top-up stays under its PaymentIntent.
            TransactionId = subscriptionInvoice && invoiceId is not null ? invoiceId : paymentIntentId,
            SubscriptionId = subscriptionId ?? evt.SubscriptionId,
            FullRefund = full
        };
    }

    // ------------------------------------------------------------------ discounts

    public async Task<string> CreateDiscountAsync(ProviderDiscountSpec spec, CancellationToken ct)
    {
        string couponId = $"rb_promo_{spec.PromotionId:D}";
        string name = spec.Description.Length <= 40 ? spec.Description : spec.Description[..40];

        string duration = !spec.Recur ? "once" : spec.MaximumRecurringIntervals is not null ? "repeating" : "forever";
        StripeForm form = new StripeForm()
            .Add("id", couponId)
            .Add("name", name)
            .Add("duration", duration)
            .Add("duration_in_months", duration == "repeating" ? spec.MaximumRecurringIntervals : null)
            .Add("max_redemptions", spec.UsageLimit)
            .Add("redeem_by", spec.ExpiresAt is { } exp ? new DateTimeOffset(DateTime.SpecifyKind(exp, DateTimeKind.Utc)).ToUnixTimeSeconds() : null)
            .Add("metadata", new Dictionary<string, object?> { ["promotion_id"] = spec.PromotionId.ToString("D"), ["reelbolt_catalog"] = "1" });

        if (spec.Type == PromotionDiscountType.Percentage)
        {
            if (!decimal.TryParse(spec.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal percent))
            {
                throw new ArgumentException("A percentage discount needs a numeric amount.", nameof(spec));
            }

            form.Add("percent_off", percent.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            if (!long.TryParse(spec.Amount, NumberStyles.None, CultureInfo.InvariantCulture, out long minor) || string.IsNullOrWhiteSpace(spec.CurrencyCode))
            {
                throw new ArgumentException("A flat discount needs a whole minor-unit amount and a currency.", nameof(spec));
            }

            form.Add("amount_off", minor).Add("currency", spec.CurrencyCode.ToLowerInvariant());
        }

        List<string> products = [];
        foreach (string priceId in spec.RestrictToPriceIds.Distinct(StringComparer.Ordinal))
        {
            JsonElement price = await _http.GetAsync($"/v1/prices/{priceId}", null, ct);
            if (StripeEventParser.IdOf(StripeEventParser.Prop(price, "product")) is { } product && !products.Contains(product)) products.Add(product);
        }

        if (products.Count > 0) form.Add("applies_to", new Dictionary<string, object?> { ["products"] = products });

        try
        {
            JsonElement coupon = await _http.PostAsync("/v1/coupons", form, null, ct);
            return StripeEventParser.Str(coupon, "id") ?? couponId;
        }
        catch (BillingProviderException ex) when (ex.Code == "resource_already_exists")
        {
            JsonElement existing = await _http.GetAsync($"/v1/coupons/{couponId}", null, ct);
            JsonElement? meta = StripeEventParser.Prop(existing, "metadata");
            if (StripeEventParser.Str(meta, "reelbolt_catalog") == "1" && StripeEventParser.Str(existing, "id") is { } id)
            {
                return id;
            }

            throw;
        }
    }

    public Task UpdateDiscountAsync(string discountId, bool? active, DateTime? expiresAt, bool clearExpiry, CancellationToken ct)
    {
        _logger?.LogInformation("Stripe coupons are immutable; ignoring the update of discount {DiscountId} (the platform enforces status and expiry itself).", discountId);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ preview

    public async Task<PricePreview> PreviewPriceAsync(IReadOnlyList<BillingLineItem> items, string? discountId, CancellationToken ct)
    {
        BillingLineItem line = items.Count > 0 ? items[0] : throw new ArgumentException("A preview needs at least one item.", nameof(items));
        JsonElement price = await _http.GetAsync($"/v1/prices/{line.PriceId}", null, ct);
        string currency = StripeEventParser.Str(price, "currency") ?? throw new HttpRequestException("Stripe returned a price without a currency.");

        StripeForm form = new StripeForm()
            .Add("currency", currency)
            .Add("subscription_details", new Dictionary<string, object?>
            {
                ["items"] = new[] { new Dictionary<string, object?> { ["price"] = line.PriceId, ["quantity"] = line.Quantity } }
            });
        if (!string.IsNullOrWhiteSpace(discountId))
        {
            form.Add("discounts", new[]
            {
                new Dictionary<string, object?> { [discountId.StartsWith("promo_", StringComparison.Ordinal) ? "promotion_code" : "coupon"] = discountId }
            });
        }

        JsonElement preview = await _http.PostAsync("/v1/invoices/create_preview", form, null, ct);
        string code = (StripeEventParser.Str(preview, "currency") ?? currency).ToUpperInvariant();
        long subtotal = StripeEventParser.Long(preview, "subtotal") ?? throw new HttpRequestException("Stripe returned a preview without a subtotal.");
        long total = StripeEventParser.Long(preview, "total") ?? throw new HttpRequestException("Stripe returned a preview without a total.");
        long? discount = SumAmounts(StripeEventParser.Prop(preview, "total_discount_amounts"));
        long? tax = SumAmounts(StripeEventParser.Prop(preview, "total_taxes"));

        long? unitSubtotal = null;
        long? unitTotal = null;
        if (StripeEventParser.Prop(StripeEventParser.Prop(preview, "lines"), "data") is { ValueKind: JsonValueKind.Array } lines && lines.GetArrayLength() > 0)
        {
            JsonElement l = lines[0];
            long quantity = StripeEventParser.Long(l, "quantity") ?? line.Quantity;
            long amount = StripeEventParser.Long(l, "amount") ?? subtotal;
            long lineDiscount = SumAmounts(StripeEventParser.Prop(l, "discount_amounts")) ?? 0;
            if (quantity > 0 && amount % quantity == 0) unitSubtotal = amount / quantity;
            if (quantity > 0 && (amount - lineDiscount) % quantity == 0) unitTotal = (amount - lineDiscount) / quantity;
        }

        string Show(long minor) => StripeMoneyFormatter.Format(minor, code);
        return new PricePreview(
            code,
            Show(subtotal),
            Show(discount ?? 0),
            Show(total),
            RawTax: tax?.ToString(CultureInfo.InvariantCulture),
            RawDiscount: discount?.ToString(CultureInfo.InvariantCulture),
            UnitSubtotal: unitSubtotal is { } us ? Show(us) : null,
            UnitTotal: unitTotal is { } ut ? Show(ut) : null,
            SubtotalMinor: subtotal,
            DiscountMinor: discount ?? 0,
            TotalMinor: total,
            UnitSubtotalMinor: unitSubtotal,
            UnitTotalMinor: unitTotal,
            MinorUnitExponent: StripeMoneyFormatter.Exponent(code));
    }

    private static long? SumAmounts(JsonElement? array)
    {
        if (array is not { ValueKind: JsonValueKind.Array } a) return null;
        long sum = 0;
        foreach (JsonElement e in a.EnumerateArray()) sum += StripeEventParser.Long(e, "amount") ?? 0;
        return sum;
    }

    // ------------------------------------------------------------------ transactions

    public async Task<ProviderTransactionCancel> CancelTransactionAsync(string providerTransactionId, CancellationToken ct)
    {
        // Only a Checkout Session can be expired; any other id is a caller bug, not a 400/404 to swallow as "not cancellable".
        if (!providerTransactionId.StartsWith("cs_", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only a Stripe Checkout Session (cs_...) can be cancelled.", nameof(providerTransactionId));
        }

        try
        {
            await _http.PostAsync($"/v1/checkout/sessions/{providerTransactionId}/expire", new StripeForm(), null, ct);
            return ProviderTransactionCancel.Cancelled;
        }
        catch (BillingProviderException ex) when (ex.Status is >= 400 and < 500 and not (401 or 403 or 429))
        {
            _logger?.LogInformation("Stripe checkout session {SessionId} cannot be expired ({Code}); left alone.", providerTransactionId, ex.Code);
            return ProviderTransactionCancel.NotCancellable;
        }
    }

    public async Task<ProviderRefund> RefundTransactionAsync(string providerTransactionId, long? amountMinor, string reason, string idempotencyKey, CancellationToken ct)
    {
        string paymentIntent = await PaymentIntentOfAsync(providerTransactionId, ct);
        StripeForm form = new StripeForm()
            .Add("payment_intent", paymentIntent)
            .Add("amount", amountMinor)
            .Add("reason", "requested_by_customer")
            .Add("metadata", new Dictionary<string, object?> { ["reelbolt_reason"] = reason });
        JsonElement refund = await _http.PostAsync("/v1/refunds", form, idempotencyKey, ct);
        return new ProviderRefund(
            StripeEventParser.Str(refund, "id") ?? throw new HttpRequestException("Stripe returned no refund id."),
            StripeEventParser.Long(refund, "amount") ?? amountMinor ?? 0,
            (StripeEventParser.Str(refund, "currency") ?? "").ToUpperInvariant(),
            StripeEventParser.Str(refund, "status") ?? "");
    }

    private async Task<string> PaymentIntentOfAsync(string transactionId, CancellationToken ct)
    {
        string? pi = null;
        if (transactionId.StartsWith("pi_", StringComparison.Ordinal))
        {
            pi = transactionId;
        }
        else if (transactionId.StartsWith("cs_", StringComparison.Ordinal))
        {
            JsonElement session = await _http.GetAsync($"/v1/checkout/sessions/{transactionId}", null, ct);
            pi = StripeEventParser.IdOf(StripeEventParser.Prop(session, "payment_intent"));
        }
        else if (transactionId.StartsWith("in_", StringComparison.Ordinal))
        {
            JsonElement payments = await _http.GetAsync("/v1/invoice_payments", new StripeForm().Add("invoice", transactionId).Add("status", "paid"), ct);
            if (StripeEventParser.Prop(payments, "data") is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0)
            {
                pi = StripeEventParser.IdOf(StripeEventParser.Prop(StripeEventParser.Prop(a[0], "payment"), "payment_intent"));
            }
        }

        return pi ?? throw new BillingProviderException("payment_not_found", 404, "No paid Stripe payment was found for the transaction.");
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
