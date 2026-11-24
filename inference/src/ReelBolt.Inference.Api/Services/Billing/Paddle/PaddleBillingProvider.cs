using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Services.Billing.Paddle;

/// <summary>
/// Paddle Billing (merchant of record, D11) over plain <see cref="HttpClient"/> and
/// <c>System.Text.Json</c>: no SDK. See <c>docs/billing.md</c> for the endpoints, the
/// <c>Paddle-Signature</c> scheme and the event mapping this class implements.
/// </summary>
public sealed class PaddleBillingProvider(
    HttpClient http,
    IOptions<PaddleOptions> options,
    TimeProvider? time = null,
    ILogger<PaddleBillingProvider>? logger = null) : IBillingProvider
{
    public const string ProviderName = "paddle";
    public const string SignatureHeader = "Paddle-Signature";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private PaddleOptions Paddle => options.Value.Paddle;

    public string Name => ProviderName;

    public string DisplayName => "Paddle";

    public int MaxSelfServeSeats => Paddle.MaxSelfServeSeats;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Paddle.ApiKey) && !string.IsNullOrWhiteSpace(Paddle.WebhookSecret);

    public bool OwnsTransaction(string providerTransactionId) => providerTransactionId.StartsWith("txn_", StringComparison.Ordinal);

    public IReadOnlyList<int> TopUpPacks => Paddle.TopUpPriceIds
        .Where(kv => int.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int c) && c > 0 && !string.IsNullOrWhiteSpace(kv.Value))
        .Select(kv => int.Parse(kv.Key, CultureInfo.InvariantCulture)).Order().ToList();

    public string? TopUpPriceId(int credits)
        => Paddle.TopUpPriceIds.TryGetValue(credits.ToString(CultureInfo.InvariantCulture), out string? id) && !string.IsNullOrWhiteSpace(id) ? id : null;

    // ------------------------------------------------------------------ API calls

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct)
    {
        string priceId;
        int quantity;
        JsonObject custom = new()
        {
            ["organization_id"] = request.OrganizationId.ToString(),
            ["user_id"] = request.UserId.ToString()
        };

        if (request.TopUpCredits is { } credits)
        {
            priceId = TopUpPriceId(credits) ?? throw new InvalidOperationException($"No Paddle price id is configured for the {credits}-credit top-up.");
            quantity = 1;
            custom["kind"] = "topup";
            custom["credits"] = credits;
        }
        else
        {
            Plan plan = request.Plan ?? throw new ArgumentException("A checkout needs a plan or a top-up.", nameof(request));
            priceId = Billing.BillingEventProcessor.PriceIdFor(plan, Name, request.Interval)
                ?? throw new InvalidOperationException($"Plan {plan.Key} has no Paddle price id for the {request.Interval} interval.");
            quantity = plan.Kind == PlanKind.PerSeat ? Math.Max(request.Seats, plan.MinSeats ?? 1) : 1;
            custom["kind"] = "subscription";
            custom["plan_key"] = plan.Key;
        }

        if (request.PromotionId is { } promotionId) custom["promotion_id"] = promotionId.ToString();
        if (request.BonusPromotionId is { } bonusId) custom["bonus_promotion_id"] = bonusId.ToString();

        JsonObject body = new()
        {
            ["items"] = new JsonArray(new JsonObject { ["price_id"] = priceId, ["quantity"] = quantity }),
            ["collection_mode"] = "automatic",
            ["custom_data"] = custom
        };
        if (request.DiscountId is { Length: > 0 } discountId)
        {
            body["discount_id"] = discountId;
        }
        string? customerId = await ResolveCustomerIdAsync(request, ct).ConfigureAwait(false);
        if (customerId is { Length: > 0 })
        {
            body["customer_id"] = customerId;
        }

        JsonElement data = await SendAsync(HttpMethod.Post, "/transactions", body, ct).ConfigureAwait(false);
        string id = data.GetProperty("id").GetString()!;
        string? url = data.TryGetProperty("checkout", out JsonElement c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("url", out JsonElement u)
            ? u.GetString()
            : null;
        return string.IsNullOrEmpty(url)
            ? throw new HttpRequestException("Paddle returned no checkout URL; set a default payment link in Paddle (Checkout settings).")
            : new CheckoutSession(url, id, string.IsNullOrEmpty(customerId) ? null : customerId);
    }

    /// <summary>
    /// The Paddle customer this checkout belongs to, creating one from the signed-in address when the
    /// organization has never bought before.
    /// </summary>
    /// <remarks>
    /// Paddle's create-transaction takes no address: the only field that prefills the hosted checkout
    /// is <c>customer_id</c> ("If omitted, transaction status is <c>draft</c>" — create-transaction
    /// reference). So a first-time buyer used to retype an email this platform already knew, because
    /// <see cref="CheckoutRequest.Email"/> was carried all the way here and then dropped.
    ///
    /// Pre-creating the customer is what makes the prefill possible, at the cost of a customer record
    /// for someone who abandons the checkout. That cost is accepted deliberately: the address is one
    /// the caller has already proven they own by signing in.
    ///
    /// A failure here degrades rather than aborts. Paddle asks for the address itself when a
    /// transaction has no customer, so losing the prefill costs the buyer one field — refusing the
    /// sale over it would cost the sale. That degradation is logged, never silent.
    /// </remarks>
    private async Task<string?> ResolveCustomerIdAsync(CheckoutRequest request, CancellationToken ct)
    {
        if (request.ExistingCustomerId is { Length: > 0 } existing)
        {
            return existing;
        }
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return null;
        }

        JsonObject body = new()
        {
            ["email"] = request.Email.Trim(),
            ["custom_data"] = new JsonObject { ["organization_id"] = request.OrganizationId.ToString() }
        };

        try
        {
            JsonElement data = await SendAsync(HttpMethod.Post, "/customers", body, ct).ConfigureAwait(false);
            return data.TryGetProperty("id", out JsonElement id) ? id.GetString() : null;
        }
        catch (BillingProviderException ex) when (ex.Status == 409)
        {
            // The address already has a Paddle customer (an abandoned checkout created it, and the id was
            // never stored). Reuse it only when it is this organization's: binding another workspace's
            // customer would hand it this workspace's invoices and portal.
            return await FindOwnCustomerAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex,
                "Could not pre-create a Paddle customer for {Email}; the checkout will ask for the address instead.",
                request.Email);
            return null;
        }
    }

    /// <summary><c>GET /customers?email={email}</c>: the customer whose <c>custom_data.organization_id</c> is this organization, or null.</summary>
    private async Task<string?> FindOwnCustomerAsync(CheckoutRequest request, CancellationToken ct)
    {
        try
        {
            JsonElement data = await SendAsync(HttpMethod.Get, $"/customers?email={Uri.EscapeDataString(request.Email!.Trim())}", null, ct).ConfigureAwait(false);
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement customer in data.EnumerateArray())
                {
                    if (OrganizationOf(customer) is { } owner
                        && Guid.TryParse(owner, out Guid ownerId) && ownerId == request.OrganizationId
                        && Str(customer, "id") is { Length: > 0 } id)
                    {
                        return id;
                    }
                }
            }

            logger?.LogWarning(
                "{Email} already has a Paddle customer, but none of organization {Org}; the checkout will ask for the address instead.",
                request.Email, request.OrganizationId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex,
                "Could not look up the existing Paddle customer for {Email}; the checkout will ask for the address instead.",
                request.Email);
        }

        return null;
    }

    /// <summary>
    /// <c>POST /customers/{id}/portal-sessions</c>. <c>data.urls.general.overview</c> is the portal home;
    /// <c>data.urls.subscriptions[]</c> carries, per requested subscription, the <c>cancel_subscription</c>
    /// and <c>update_subscription_payment_method</c> deep links. Missing deep links are null, not an error.
    /// </summary>
    public async Task<PortalSession> CreatePortalSessionAsync(string providerCustomerId, string? providerSubscriptionId, CancellationToken ct)
    {
        JsonObject body = [];
        if (providerSubscriptionId is { Length: > 0 })
        {
            body["subscription_ids"] = new JsonArray(providerSubscriptionId);
        }

        JsonElement data = await SendAsync(HttpMethod.Post, $"/customers/{Uri.EscapeDataString(providerCustomerId)}/portal-sessions", body, ct).ConfigureAwait(false);
        JsonElement urls = data.GetProperty("urls");
        string overview = urls.GetProperty("general").GetProperty("overview").GetString()
            ?? throw new HttpRequestException("Paddle returned no portal URL.");

        string? cancel = null, paymentMethod = null;
        if (providerSubscriptionId is { Length: > 0 }
            && urls.TryGetProperty("subscriptions", out JsonElement subs)
            && subs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement sub in subs.EnumerateArray())
            {
                if (sub.ValueKind == JsonValueKind.Object && Str(sub, "id") == providerSubscriptionId)
                {
                    cancel = Str(sub, "cancel_subscription");
                    paymentMethod = Str(sub, "update_subscription_payment_method");
                    break;
                }
            }
        }

        return new PortalSession(overview, cancel, paymentMethod);
    }

    /// <summary>
    /// <c>GET /subscriptions?customer_id={id}&amp;status=active,trialing,past_due,paused</c> (both filters take a
    /// comma list). A subscription counts when its <c>custom_data.organization_id</c> is this organization or
    /// absent; another organization's subscription on a shared customer never blocks this one.
    /// </summary>
    public async Task<bool> HasLiveSubscriptionAsync(string providerCustomerId, Guid organizationId, CancellationToken ct)
        => (await LiveSubscriptionsAsync(providerCustomerId, organizationId, ct).ConfigureAwait(false)).Count > 0;

    /// <summary>
    /// The same listing as <see cref="HasLiveSubscriptionAsync"/>: the id of a live subscription naming this
    /// organization, else of one naming none; null when there is none.
    /// </summary>
    public async Task<string?> FindLiveSubscriptionIdAsync(string providerCustomerId, Guid organizationId, CancellationToken ct)
    {
        List<(JsonElement Sub, bool Named)> live = await LiveSubscriptionsAsync(providerCustomerId, organizationId, ct).ConfigureAwait(false);
        return live.Where(l => l.Named).Concat(live.Where(l => !l.Named))
            .Select(l => Str(l.Sub, "id"))
            .FirstOrDefault(id => id is { Length: > 0 });
    }

    /// <summary>The customer's live subscriptions that count for <paramref name="organizationId"/>; <c>Named</c> when one names it.</summary>
    private async Task<List<(JsonElement Sub, bool Named)>> LiveSubscriptionsAsync(string providerCustomerId, Guid organizationId, CancellationToken ct)
    {
        JsonElement data = await SendAsync(HttpMethod.Get,
            $"/subscriptions?customer_id={Uri.EscapeDataString(providerCustomerId)}&status=active,trialing,past_due,paused&per_page=200",
            null, ct).ConfigureAwait(false);
        RequireList(data);
        List<(JsonElement, bool)> live = [];

        foreach (JsonElement sub in data.EnumerateArray())
        {
            string? org = OrganizationOf(sub);
            if (org is null)
            {
                live.Add((sub, false));
            }
            else if (Guid.TryParse(org, out Guid id) && id == organizationId)
            {
                live.Add((sub, true));
            }
        }

        return live;
    }

    /// <summary><c>custom_data.organization_id</c> of a Paddle entity; null when it carries none.</summary>
    private static string? OrganizationOf(JsonElement entity)
        => entity.ValueKind == JsonValueKind.Object
           && entity.TryGetProperty("custom_data", out JsonElement custom)
           && custom.ValueKind == JsonValueKind.Object
           && Str(custom, "organization_id") is { Length: > 0 } org
            ? org
            : null;

    /// <summary><c>POST /subscriptions/{id}/cancel</c> with <c>effective_from: next_billing_period</c>: a scheduled, undoable cancel.</summary>
    public async Task CancelSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
    {
        JsonObject body = new() { ["effective_from"] = "next_billing_period" };
        await SendAsync(HttpMethod.Post, $"/subscriptions/{Uri.EscapeDataString(providerSubscriptionId)}/cancel", body, ct).ConfigureAwait(false);
    }

    /// <summary><c>PATCH /subscriptions/{id}</c> with an explicit <c>"scheduled_change": null</c>, which removes a scheduled cancel.</summary>
    public async Task ResumeSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
    {
        JsonObject body = new() { ["scheduled_change"] = null };
        await SendAsync(HttpMethod.Patch, $"/subscriptions/{Uri.EscapeDataString(providerSubscriptionId)}", body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>PATCH /subscriptions/{id}</c> with the full new item list (and <c>"discount": null</c> when
    /// <paramref name="discount"/> is <see cref="PlanChangeDiscount.Remove"/>). An upgrade is <c>prorated_immediately</c>
    /// (charged now); a downgrade is <c>full_next_billing_period</c> (Paddle switches the items now and bills
    /// the new price from the next period — the platform keeps the old entitlements until then, see
    /// <see cref="BillingEventProcessor"/>).
    /// </summary>
    public async Task ChangePlanAsync(string providerSubscriptionId, IReadOnlyList<BillingLineItem> items, PlanChangeTiming timing, PlanChangeDiscount discount, CancellationToken ct)
    {
        JsonArray lines = [];
        foreach (BillingLineItem item in items)
        {
            lines.Add(new JsonObject { ["price_id"] = item.PriceId, ["quantity"] = item.Quantity });
        }

        JsonObject body = new()
        {
            ["items"] = lines,
            ["proration_billing_mode"] = timing == PlanChangeTiming.Immediately ? "prorated_immediately" : "full_next_billing_period"
        };
        if (discount == PlanChangeDiscount.Remove)
        {
            // An explicit JSON null removes the subscription's discount; omitting the key leaves it as is.
            body["discount"] = null;
        }

        await SendAsync(HttpMethod.Patch, $"/subscriptions/{Uri.EscapeDataString(providerSubscriptionId)}", body, ct).ConfigureAwait(false);
    }

    public async Task<string> CreateDiscountAsync(ProviderDiscountSpec spec, CancellationToken ct)
    {
        // No "code": codes are ours, validated server-side, and the discount id is passed on the transaction.
        JsonObject body = new()
        {
            ["description"] = spec.Description,
            ["type"] = spec.Type == PromotionDiscountType.Flat ? "flat" : "percentage",
            ["amount"] = spec.Amount,
            ["enabled_for_checkout"] = Paddle.DiscountsEnabledForCheckout,
            ["recur"] = spec.Recur,
            ["maximum_recurring_intervals"] = spec.MaximumRecurringIntervals,
            ["custom_data"] = new JsonObject { ["promotion_id"] = spec.PromotionId.ToString() }
        };
        if (spec.Type == PromotionDiscountType.Flat)
        {
            body["currency_code"] = string.IsNullOrWhiteSpace(spec.CurrencyCode)
                ? throw new ArgumentException("A flat discount needs a currency code.", nameof(spec))
                : spec.CurrencyCode.Trim().ToUpperInvariant();
        }
        if (spec.UsageLimit is { } limit) body["usage_limit"] = limit;
        if (spec.RestrictToPriceIds.Count > 0) body["restrict_to"] = new JsonArray(spec.RestrictToPriceIds.Select(id => (JsonNode?)id).ToArray());
        if (spec.ExpiresAt is { } expires) body["expires_at"] = Rfc3339(expires);

        JsonElement data = await SendAsync(HttpMethod.Post, "/discounts", body, ct).ConfigureAwait(false);
        return data.GetProperty("id").GetString()
            ?? throw new HttpRequestException("Paddle returned no discount id.");
    }

    public async Task UpdateDiscountAsync(string discountId, bool? active, DateTime? expiresAt, bool clearExpiry, CancellationToken ct)
    {
        JsonObject body = [];
        if (active is { } a) body["status"] = a ? "active" : "archived";
        if (expiresAt is { } e) body["expires_at"] = Rfc3339(e);
        else if (clearExpiry) body["expires_at"] = null;
        if (body.Count == 0)
        {
            return;
        }

        await SendAsync(HttpMethod.Patch, $"/discounts/{Uri.EscapeDataString(discountId)}", body, ct).ConfigureAwait(false);
    }

    public async Task<PricePreview> PreviewPriceAsync(IReadOnlyList<BillingLineItem> items, string? discountId, CancellationToken ct)
    {
        JsonObject body = new()
        {
            ["items"] = new JsonArray(items.Select(i => (JsonNode?)new JsonObject { ["price_id"] = i.PriceId, ["quantity"] = i.Quantity }).ToArray())
        };
        if (discountId is { Length: > 0 })
        {
            body["discount_id"] = discountId;
        }

        JsonElement data = await SendAsync(HttpMethod.Post, "/pricing-preview", body, ct).ConfigureAwait(false);
        JsonElement line = data.GetProperty("details").GetProperty("line_items")[0];
        JsonElement totals = line.GetProperty("formatted_totals");
        return new PricePreview(
            data.GetProperty("currency_code").GetString()!,
            totals.GetProperty("subtotal").GetString()!,
            totals.GetProperty("discount").GetString()!,
            totals.GetProperty("total").GetString()!,
            RawTotal(line, "tax"),
            RawTotal(line, "discount"),
            UnitFormatted(line, "subtotal"),
            UnitFormatted(line, "total"));
    }

    /// <summary>The line's <c>formatted_unit_totals.{name}</c> (one unit, e.g. one seat), or null when absent.</summary>
    private static string? UnitFormatted(JsonElement line, string name)
        => line.TryGetProperty("formatted_unit_totals", out JsonElement unit) && unit.ValueKind == JsonValueKind.Object
            && unit.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>The line's unformatted <c>totals.{name}</c>, or null when Paddle did not send it.</summary>
    private static string? RawTotal(JsonElement line, string name)
        => line.TryGetProperty("totals", out JsonElement raw) && raw.ValueKind == JsonValueKind.Object
            && raw.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// Paddle's error codes that prove a transaction can no longer be cancelled: <c>transaction_immutable</c>
    /// is its documented answer to changing a billed, paid, completed or canceled transaction (N-17).
    /// </summary>
    private static readonly HashSet<string> NotCancellableCodes = new(StringComparer.Ordinal) { "transaction_immutable" };

    /// <summary>
    /// <c>PATCH /transactions/{id}</c> with <c>{"status":"canceled"}</c> (contract C10). Paddle cancels only a
    /// transaction that can still be paid (draft or ready); its documented refusal for one past that point
    /// (<see cref="NotCancellableCodes"/>) is <see cref="ProviderTransactionCancel.NotCancellable"/> and the
    /// transaction is left alone. Every other answer throws, so the sweeper retries with backoff: auth,
    /// timeout, rate limit and 5xx as before, and (N-17) any other 4xx, which proves nothing about the
    /// transaction and is logged at Warning. Only the status and Paddle's error code are logged; the error
    /// detail can quote the buyer.
    /// </summary>
    public async Task<ProviderTransactionCancel> CancelTransactionAsync(string providerTransactionId, CancellationToken ct)
    {
        using HttpRequestMessage req = new(HttpMethod.Patch, $"{Paddle.ResolvedBaseUrl}/transactions/{Uri.EscapeDataString(providerTransactionId)}")
        {
            Content = JsonContent.Create(new JsonObject { ["status"] = "canceled" })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Paddle.ApiKey);
        req.Headers.Add("Paddle-Version", "1");
        using HttpResponseMessage res = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.IsSuccessStatusCode)
        {
            return ProviderTransactionCancel.Cancelled;
        }

        int status = (int)res.StatusCode;
        string code = ErrorCodeOf(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (status is >= 400 and < 500 && NotCancellableCodes.Contains(code))
        {
            logger?.LogInformation(
                "Paddle did not cancel transaction {Transaction} ({Status} {Code}); it is past cancelling and left alone.",
                providerTransactionId, status, code);
            return ProviderTransactionCancel.NotCancellable;
        }

        if (status is >= 400 and < 500 and not (401 or 403 or 408 or 429))
        {
            // Not Paddle's documented refusal: a contract or configuration error until shown otherwise. The
            // transaction may still be payable, so it is retried, never settled.
            logger?.LogWarning(
                "Paddle answered an unexpected {Status} {Code} cancelling transaction {Transaction}; it is not settled and is retried.",
                status, code, providerTransactionId);
        }

        throw new HttpRequestException($"Paddle {status} {code} cancelling a transaction", null, res.StatusCode);
    }

    /// <summary>Paddle's <c>error.code</c> (a fixed identifier, never buyer data), or <c>unknown</c>.</summary>
    private static string ErrorCodeOf(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out JsonElement code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() is { Length: > 0 and <= 64 } value
                && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // Not JSON: no code to report.
        }

        return "unknown";
    }

    /// <summary>RFC 3339 in UTC, to the second. An unspecified kind is taken as UTC (how the database returns it).</summary>
    private static string Rfc3339(DateTime value)
    {
        DateTime utc = value.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => value
        };
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary><c>GET /subscriptions/{id}</c>: Paddle's current items, status, scheduled cancel, period and discount.</summary>
    public async Task<ProviderSubscriptionState> GetSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
    {
        JsonElement data = await SendAsync(HttpMethod.Get, $"/subscriptions/{Uri.EscapeDataString(providerSubscriptionId)}", null, ct).ConfigureAwait(false);
        bool cancelScheduled = data.TryGetProperty("scheduled_change", out JsonElement sc)
            && sc.ValueKind == JsonValueKind.Object
            && Str(sc, "action") == "cancel";
        bool hasPeriod = data.TryGetProperty("current_billing_period", out JsonElement period) && period.ValueKind == JsonValueKind.Object;
        DateTime? periodEnd = hasPeriod && Str(period, "ends_at") is { } end ? ParseTime(end) : null;
        DateTime? periodStart = hasPeriod && Str(period, "starts_at") is { } start ? ParseTime(start) : null;
        ProviderSubscriptionDiscount? discount = data.TryGetProperty("discount", out JsonElement d)
            && d.ValueKind == JsonValueKind.Object
            && Str(d, "id") is { Length: > 0 } discountId
                ? new ProviderSubscriptionDiscount(discountId, Str(d, "ends_at") is { } endsAt ? ParseTime(endsAt) : null)
                : null;
        return new ProviderSubscriptionState(Str(data, "status") ?? "", Items(data), cancelScheduled, periodEnd, periodStart, discount);
    }

    // ------------------------------------------------------------------ reconcile reads

    /// <summary>
    /// <c>GET /subscriptions/{id}</c>, wrapped in a <c>subscription.updated</c> envelope and run through the webhook's
    /// own <see cref="Parse(string)"/>: the API's <c>data</c> has the webhook's shape, so plan, status, period, cancel
    /// and organization come out exactly as the webhook would give them.
    /// </summary>
    public async Task<BillingSnapshot> SnapshotSubscriptionAsync(string providerSubscriptionId, CancellationToken ct)
    {
        JsonElement data = await SendAsync(HttpMethod.Get, $"/subscriptions/{Uri.EscapeDataString(providerSubscriptionId)}", null, ct).ConfigureAwait(false);
        DateTime? updatedAt = Str(data, "updated_at") is { } u ? ParseTime(u) : null;
        DateTime occurredAt = updatedAt ?? _time.GetUtcNow().UtcDateTime;
        string eventId = $"reconcile:sub:{providerSubscriptionId}:{occurredAt.Ticks.ToString(CultureInfo.InvariantCulture)}";
        return Synthesize(eventId, "subscription.updated", "subscription.reconciled", occurredAt, data, updatedAt);
    }

    /// <summary>
    /// <c>GET /transactions?subscription_id={id}&amp;status=completed&amp;origin=subscription_update&amp;billed_at[GTE]={since}&amp;per_page=50</c>
    /// (first page only), each wrapped in a <c>transaction.completed</c> envelope and parsed like the webhook; only
    /// upgrade payments are kept, and only those whose own <c>status</c> is <c>completed</c> and whose
    /// <c>subscription_id</c> is <paramref name="providerSubscriptionId"/> (the query filters are not trusted alone).
    /// </summary>
    public async Task<IReadOnlyList<BillingSnapshot>> CompletedUpgradeTransactionsAsync(string providerSubscriptionId, DateTime since, CancellationToken ct)
    {
        DateTime sinceUtc = since.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(since, DateTimeKind.Utc) : since.ToUniversalTime();
        string billedSince = sinceUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        JsonElement data = await SendAsync(HttpMethod.Get,
            $"/transactions?subscription_id={Uri.EscapeDataString(providerSubscriptionId)}&status=completed&origin=subscription_update"
            + $"&{Uri.EscapeDataString("billed_at[GTE]")}={Uri.EscapeDataString(billedSince)}&per_page=50",
            null, ct).ConfigureAwait(false);

        RequireList(data);
        List<BillingSnapshot> found = [];

        foreach (JsonElement txn in data.EnumerateArray())
        {
            if (txn.ValueKind != JsonValueKind.Object || Str(txn, "id") is not { Length: > 0 } txnId)
            {
                continue;
            }

            DateTime? updatedAt = Str(txn, "updated_at") is { } u ? ParseTime(u) : null;
            DateTime occurredAt = (Str(txn, "billed_at") is { } b ? ParseTime(b) : (DateTime?)null) ?? updatedAt ?? _time.GetUtcNow().UtcDateTime;
            BillingSnapshot snapshot = Synthesize($"reconcile:txn:{txnId}", "transaction.completed", "transaction.reconciled", occurredAt, txn, updatedAt);
            if (snapshot.Event.Kind != BillingEventKind.UpgradePaid)
            {
                continue;
            }

            // The query already filters on both, but a grant is only ever fed for a paid transaction of this
            // subscription: an entity that is not (a billed or past_due one, another subscription's) is skipped.
            string? status = Str(txn, "status");
            if (status != "completed" || Str(txn, "subscription_id") != providerSubscriptionId)
            {
                logger?.LogWarning(
                    "Paddle listed transaction {TransactionId} (status {Status}) as a completed upgrade payment of the reconciled subscription, but it is not one; skipped.",
                    txnId, status);
                continue;
            }

            found.Add(snapshot);
        }

        return found;
    }

    /// <summary>
    /// The entity wrapped in the webhook envelope <c>{event_id, event_type, occurred_at, data}</c> and parsed by the
    /// webhook's <see cref="Parse(string)"/>; the result only renames its type to <paramref name="syntheticType"/>.
    /// </summary>
    private BillingSnapshot Synthesize(string eventId, string webhookType, string syntheticType, DateTime occurredAt, JsonElement data, DateTime? updatedAt)
    {
        JsonObject envelope = new()
        {
            ["event_id"] = eventId,
            ["event_type"] = webhookType,
            ["occurred_at"] = occurredAt.ToString("O", CultureInfo.InvariantCulture),
            ["data"] = JsonNode.Parse(data.GetRawText())
        };
        string raw = envelope.ToJsonString();
        BillingEvent parsed = Parse(raw) with { EventType = syntheticType };
        return new BillingSnapshot(parsed, raw, updatedAt);
    }

    /// <summary>
    /// A 2xx list whose <c>data</c> is not an array (S-4) is not "nothing listed": it fails like an unreadable
    /// provider (<see cref="HttpRequestError.InvalidResponse"/>), so checkout refuses (502) and reconcile applies nothing.
    /// </summary>
    private static void RequireList(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            throw new HttpRequestException(HttpRequestError.InvalidResponse, $"Paddle returned a list whose data is {data.ValueKind}, not an array.");
        }
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using HttpRequestMessage req = new(method, Paddle.ResolvedBaseUrl + path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Paddle.ApiKey);
        req.Headers.Add("Paddle-Version", "1");
        using HttpResponseMessage res = await http.SendAsync(req, ct).ConfigureAwait(false);
        string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            // The body names a Paddle error code, never a secret; keep it short for the log.
            if (ErrorOf(text) is { } error)
            {
                throw new BillingProviderException(error.Code, (int)res.StatusCode, error.Detail);
            }

            throw new HttpRequestException($"Paddle {(int)res.StatusCode}: {(text.Length > 300 ? text[..300] : text)}", null, res.StatusCode);
        }

        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("data").Clone();
    }

    /// <summary>Paddle's error envelope: <c>{"error":{"type","code","detail"}}</c>. Null when the body is not one.</summary>
    private static (string Code, string Detail)? ErrorOf(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.Object
                && Str(error, "code") is { Length: > 0 } code)
            {
                string detail = Str(error, "detail") ?? "";
                return (code, detail.Length > 300 ? detail[..300] : detail);
            }
        }
        catch (JsonException)
        {
            // Not JSON: the caller falls back to the raw text.
        }

        return null;
    }

    // ------------------------------------------------------------------ webhooks

    public BillingEvent VerifyAndParseWebhook(string rawBody, IReadOnlyDictionary<string, string> headers)
    {
        string? header = headers.FirstOrDefault(h => string.Equals(h.Key, SignatureHeader, StringComparison.OrdinalIgnoreCase)).Value;
        VerifySignature(rawBody, header);

        try
        {
            return Parse(rawBody);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new WebhookVerificationException("Malformed webhook body.");
        }
    }

    /// <summary>
    /// <c>Paddle-Signature: ts=1671552777;h1=…</c>. The signed payload is <c>"{ts}:{rawBody}"</c>, HMAC-SHA256 keyed
    /// with the endpoint secret, hex encoded; more than one <c>h1</c> is sent during secret rotation. The timestamp
    /// must be within <see cref="PaddleOptions.ToleranceSeconds"/> to bound replay.
    /// </summary>
    private void VerifySignature(string rawBody, string? header)
    {
        if (string.IsNullOrWhiteSpace(Paddle.WebhookSecret))
        {
            throw new WebhookVerificationException("No webhook secret configured.");
        }

        if (string.IsNullOrWhiteSpace(header))
        {
            throw new WebhookVerificationException("Missing Paddle-Signature header.");
        }

        string? ts = null;
        List<string> hashes = [];
        foreach (string part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq];
            string value = part[(eq + 1)..];
            if (key == "ts") ts = value;
            else if (key == "h1") hashes.Add(value);
        }

        if (ts is null || hashes.Count == 0 || !long.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
        {
            throw new WebhookVerificationException("Malformed Paddle-Signature header.");
        }

        long age = Math.Abs(_time.GetUtcNow().ToUnixTimeSeconds() - seconds);
        if (age > Math.Max(0, Paddle.ToleranceSeconds))
        {
            throw new WebhookVerificationException("Signature timestamp outside the tolerance window.");
        }

        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Paddle.WebhookSecret), Encoding.UTF8.GetBytes($"{ts}:{rawBody}"));
        foreach (string h1 in hashes)
        {
            byte[] given;
            try
            {
                given = Convert.FromHexString(h1);
            }
            catch (FormatException)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(expected, given))
            {
                return;
            }
        }

        throw new WebhookVerificationException("Signature mismatch.");
    }

    private BillingEvent Parse(string rawBody)
    {
        using JsonDocument doc = JsonDocument.Parse(rawBody);
        JsonElement root = doc.RootElement;
        string eventType = root.GetProperty("event_type").GetString()!;
        JsonElement data = root.GetProperty("data");

        BillingEvent baseEvent = new()
        {
            Provider = ProviderName,
            EventId = root.GetProperty("event_id").GetString()!,
            EventType = eventType,
            OccurredAt = ParseTime(root.GetProperty("occurred_at").GetString()!)
        };

        Guid? orgId = null;
        Guid? promotionId = null;
        Guid? bonusPromotionId = null;
        string? kind = null;
        if (data.TryGetProperty("custom_data", out JsonElement custom) && custom.ValueKind == JsonValueKind.Object)
        {
            if (custom.TryGetProperty("organization_id", out JsonElement o) && Guid.TryParse(o.GetString(), out Guid g)) orgId = g;
            if (custom.TryGetProperty("kind", out JsonElement k)) kind = k.GetString();
            promotionId = OptionalGuid(custom, "promotion_id");
            bonusPromotionId = OptionalGuid(custom, "bonus_promotion_id");
        }

        string? customerId = Str(data, "customer_id");

        switch (eventType)
        {
            case "subscription.created":
            case "subscription.activated":
            case "subscription.updated":
            case "subscription.canceled":
            case "subscription.past_due":
            case "subscription.paused":
            case "subscription.resumed":
            case "subscription.trialing":
                {
                    DateTime? start = null, end = null;
                    if (data.TryGetProperty("current_billing_period", out JsonElement period) && period.ValueKind == JsonValueKind.Object)
                    {
                        start = Str(period, "starts_at") is { } s ? ParseTime(s) : null;
                        end = Str(period, "ends_at") is { } e ? ParseTime(e) : null;
                    }

                    bool cancelAtEnd = data.TryGetProperty("scheduled_change", out JsonElement sc)
                        && sc.ValueKind == JsonValueKind.Object
                        && Str(sc, "action") == "cancel";
                    DateTime? cancelAt = cancelAtEnd && Str(sc, "effective_at") is { } at ? ParseTime(at) : null;
                    return baseEvent with
                    {
                        Kind = BillingEventKind.SubscriptionChanged,
                        OrganizationId = orgId,
                        CustomerId = customerId,
                        SubscriptionId = Str(data, "id"),
                        Status = MapStatus(Str(data, "status")),
                        PeriodStart = start,
                        PeriodEnd = end,
                        CancelAtPeriodEnd = cancelAtEnd,
                        CancelEffectiveAt = cancelAt,
                        Items = Items(data)
                    };
                }

            case "transaction.completed":
                {
                    // The money side is read once for every completed transaction, whatever its kind does to
                    // credits: the amount is what the provider charged, and only a stated one counts.
                    bool isPaid = TryGrandTotalMinor(data, out long amount);
                    long? fee = FeeMinor(data);
                    string? currency = CurrencyCode(data);

                    // The charge a prorated_immediately PATCH creates (an upgrade or a seat increase): the
                    // credit difference is granted only now that it is paid (F-1). A failed charge never
                    // completes (transaction.payment_failed is ignored), so it grants nothing.
                    if (Str(data, "origin") == "subscription_update" && Str(data, "subscription_id") is { } upgradedSubscription)
                    {
                        return baseEvent with
                        {
                            Kind = BillingEventKind.UpgradePaid,
                            OrganizationId = orgId,
                            CustomerId = customerId,
                            SubscriptionId = upgradedSubscription,
                            TransactionId = Str(data, "id"),
                            ProrationStartsAt = ProrationStart(data),
                            AmountMinor = amount,
                            CurrencyCode = currency,
                            FeeMinor = fee,
                            IsPaidTransaction = isPaid,
                            SubtotalMinor = TotalsMinor(data, "subtotal"),
                            DiscountMinor = TotalsMinor(data, "discount"),
                            TaxMinor = TotalsMinor(data, "tax"),
                            TotalMinor = TotalsMinor(data, "total"),
                            EarningsMinor = TotalsMinor(data, "earnings"),
                            Items = ChargedItems(data)
                        };
                    }

                    IReadOnlyList<BillingLineItem> items = Items(data);
                    int? credits = null;
                    foreach (BillingLineItem item in items)
                    {
                        foreach (int pack in TopUpPacks)
                        {
                            if (TopUpPriceId(pack) == item.PriceId) credits = pack;
                        }
                    }

                    // Subscription payments are handled through the subscription events; only a stand-alone
                    // payment for a configured top-up price is a top-up.
                    string? subscriptionId = Str(data, "subscription_id");
                    bool topUp = subscriptionId is null && credits is not null && kind != "subscription";
                    string? discountId = Str(data, "discount_id");
                    BillingEvent paid = baseEvent with
                    {
                        OrganizationId = orgId,
                        CustomerId = customerId,
                        SubscriptionId = subscriptionId,
                        TransactionId = Str(data, "id"),
                        Items = items,
                        DiscountId = discountId,
                        DiscountTotal = data.TryGetProperty("details", out JsonElement details) && details.ValueKind == JsonValueKind.Object
                            && details.TryGetProperty("totals", out JsonElement totals) && totals.ValueKind == JsonValueKind.Object
                                ? Str(totals, "discount")
                                : null,
                        CurrencyCode = currency,
                        AmountMinor = amount,
                        FeeMinor = fee,
                        IsPaidTransaction = isPaid,
                        SubtotalMinor = TotalsMinor(data, "subtotal"),
                        DiscountMinor = TotalsMinor(data, "discount"),
                        TaxMinor = TotalsMinor(data, "tax"),
                        TotalMinor = TotalsMinor(data, "total"),
                        EarningsMinor = TotalsMinor(data, "earnings"),
                        PromotionId = promotionId,
                        BonusPromotionId = bonusPromotionId,
                        BillingPeriodStart = subscriptionId is not null
                            && data.TryGetProperty("billing_period", out JsonElement billing) && billing.ValueKind == JsonValueKind.Object
                            && Str(billing, "starts_at") is { } billingStart
                                ? ParseTime(billingStart)
                                : null
                    };

                    // A discounted subscription payment is surfaced only so the promotion's redemption can be
                    // recorded; the subscription itself is still driven by the subscription events.
                    if (topUp) return paid with { Kind = BillingEventKind.TopUpPaid, TopUpCredits = credits };
                    if (subscriptionId is not null && discountId is not null) return paid with { Kind = BillingEventKind.TransactionCompleted };
                    // A plain subscription payment takes no credit action (Kind stays Ignored) but keeps the
                    // transaction's money fields, so the payment is still recorded.
                    return paid;
                }

            case "adjustment.created":
            case "adjustment.updated":
                {
                    // An approved chargeback returned the whole payment to the buyer through their bank:
                    // it is a full refund for credit purposes (top-up and pack bonus clawed back with the
                    // same refund keys), whatever "type" says. A "chargeback_reverse" (the dispute was won
                    // and the money came back) is deliberately not acted on: credits already clawed back
                    // are not re-granted automatically; support can grant them by hand. It is logged and
                    // the reason is kept on the webhook row.
                    string? action = Str(data, "action");
                    if (action == "chargeback_reverse")
                    {
                        return baseEvent with
                        {
                            TransactionId = Str(data, "transaction_id"),
                            AdjustmentId = Str(data, "id"),
                            IgnoredReason = "chargeback_reverse: credits clawed back by the chargeback are not re-granted automatically (grant them by hand if due)."
                        };
                    }

                    return action is "refund" or "chargeback" && Str(data, "status") == "approved"
                        ? baseEvent with
                        {
                            Kind = BillingEventKind.PaymentRefunded,
                            TransactionId = Str(data, "transaction_id"),
                            AdjustmentId = Str(data, "id"),
                            // Serialises the clawback with the upgrade/payment events of the same subscription.
                            SubscriptionId = Str(data, "subscription_id"),
                            CustomerId = customerId,
                            CurrencyCode = Str(data, "currency_code")
                                ?? (data.TryGetProperty("totals", out JsonElement adjustmentTotals) && adjustmentTotals.ValueKind == JsonValueKind.Object
                                    ? Str(adjustmentTotals, "currency_code")
                                    : null),
                            RefundAmountMinor = AdjustmentMinor(data, "total"),
                            RefundTaxMinor = AdjustmentMinor(data, "tax"),
                            FullRefund = action == "chargeback" || Str(data, "type") == "full"
                        }
                        : baseEvent;
                }

            default:
                return baseEvent;
        }
    }

    /// <summary>
    /// Paddle's status word. G-4: anything but the five documented words (unknown, empty, missing) is read as
    /// <see cref="SubscriptionStatus.PastDue"/>, never Active: the plan keeps working (as in dunning), but there
    /// is no period refill and a paid upgrade waits instead of granting, until a real <c>active</c> arrives.
    /// </summary>
    private static SubscriptionStatus MapStatus(string? status) => status switch
    {
        "active" => SubscriptionStatus.Active,
        "trialing" => SubscriptionStatus.Trialing,
        "past_due" => SubscriptionStatus.PastDue,
        "paused" => SubscriptionStatus.Paused,
        "canceled" => SubscriptionStatus.Canceled,
        _ => SubscriptionStatus.PastDue
    };

    private static Guid? OptionalGuid(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out Guid g) ? g : null;

    /// <summary>
    /// R-2: the lines of a proration transaction (<c>details.line_items</c>) that Paddle charged above 0 —
    /// the upgrade's new price(s) and quantity; the credits for removed items carry a total of 0 or less and
    /// are skipped. A line needs a <c>price_id</c>, a quantity of at least 1 and <c>totals.total</c> as an
    /// integer string. Empty when nothing qualifies, so the payment never pairs (fail closed).
    /// </summary>
    private static IReadOnlyList<BillingLineItem> ChargedItems(JsonElement data)
    {
        List<BillingLineItem> items = [];
        if (data.TryGetProperty("details", out JsonElement details) && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("line_items", out JsonElement lines) && lines.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement line in lines.EnumerateArray())
            {
                if (line.ValueKind == JsonValueKind.Object
                    && Str(line, "price_id") is { Length: > 0 } priceId
                    && line.TryGetProperty("quantity", out JsonElement q) && q.TryGetInt32(out int quantity) && quantity > 0
                    && line.TryGetProperty("totals", out JsonElement totals) && totals.ValueKind == JsonValueKind.Object
                    && long.TryParse(Str(totals, "total"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long total)
                    && total > 0)
                {
                    items.Add(new BillingLineItem(priceId, quantity));
                }
            }
        }

        return items;
    }

    private static IReadOnlyList<BillingLineItem> Items(JsonElement data)
    {
        List<BillingLineItem> items = [];
        if (data.TryGetProperty("items", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in arr.EnumerateArray())
            {
                string? priceId = item.TryGetProperty("price", out JsonElement price) && price.ValueKind == JsonValueKind.Object
                    ? Str(price, "id")
                    : Str(item, "price_id");
                if (priceId is not null)
                {
                    items.Add(new BillingLineItem(priceId, item.TryGetProperty("quantity", out JsonElement q) && q.TryGetInt32(out int n) ? n : 1));
                }
            }
        }

        return items;
    }

    /// <summary>
    /// The earliest <c>proration.billing_period.starts_at</c> on the transaction's items (then on
    /// <c>details.line_items</c>): the instant the provider prorated the charge from. Null when absent.
    /// </summary>
    private static DateTime? ProrationStart(JsonElement data)
    {
        DateTime? earliest = null;
        void Scan(JsonElement arr)
        {
            if (arr.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("proration", out JsonElement proration) && proration.ValueKind == JsonValueKind.Object
                    && proration.TryGetProperty("billing_period", out JsonElement period) && period.ValueKind == JsonValueKind.Object
                    && Str(period, "starts_at") is { } startsAt)
                {
                    DateTime at = ParseTime(startsAt);
                    earliest = earliest is { } e && e <= at ? e : at;
                }
            }
        }

        if (data.TryGetProperty("items", out JsonElement items))
        {
            Scan(items);
        }

        if (earliest is null
            && data.TryGetProperty("details", out JsonElement details) && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("line_items", out JsonElement lines))
        {
            Scan(lines);
        }

        return earliest;
    }

    /// <summary>
    /// <c>details.totals.grand_total</c>: what the transaction charged, in minor units. False when absent,
    /// unparseable or negative, so an unknown amount is never read as $0 (F-3).
    /// </summary>
    private static bool TryGrandTotalMinor(JsonElement data, out long amount)
    {
        amount = 0;
        return TotalsValue(data, "grand_total", out JsonElement value) && TryMinor(value, out amount);
    }

    /// <summary><c>details.totals.fee</c> in minor units; null when absent or unparseable.</summary>
    private static long? TotalsMinor(JsonElement data, string name)
        => TotalsValue(data, name, out JsonElement value) && TryMinor(value, out long minor) ? minor : null;

    /// <summary>A member of an adjustment's own <c>totals</c> object, as minor units; null when absent or not a non-negative integer.</summary>
    private static long? AdjustmentMinor(JsonElement data, string name)
        => data.TryGetProperty("totals", out JsonElement totals) && totals.ValueKind == JsonValueKind.Object
            && totals.TryGetProperty(name, out JsonElement value) && TryMinor(value, out long minor) ? minor : null;

    private static long? FeeMinor(JsonElement data)
        => TotalsValue(data, "fee", out JsonElement value) && TryMinor(value, out long fee) ? fee : null;

    /// <summary>The raw <c>details.totals</c> member, when the payload carries it as an object.</summary>
    private static bool TotalsValue(JsonElement data, string name, out JsonElement value)
    {
        value = default;
        return data.TryGetProperty("details", out JsonElement details) && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("totals", out JsonElement totals) && totals.ValueKind == JsonValueKind.Object
            && totals.TryGetProperty(name, out value);
    }

    /// <summary>
    /// A minor-unit money value as Paddle sends it: a string of an integer, and — so the live path reads what
    /// the jsonb backfill reads (<c>#&gt;&gt;</c> renders a JSON number to its text form just as happily) — a
    /// JSON number too. False for anything else, and for a negative, so an unknown amount is never read as $0.
    /// </summary>
    private static bool TryMinor(JsonElement value, out long amount)
    {
        amount = 0;
        if (value.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out amount);
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long parsed) && parsed >= 0)
        {
            amount = parsed;
            return true;
        }

        return false;
    }

    /// <summary>The transaction's currency: the top-level <c>currency_code</c>, else <c>details.totals</c>'.</summary>
    private static string? CurrencyCode(JsonElement data)
        => Str(data, "currency_code")
            ?? (data.TryGetProperty("details", out JsonElement details) && details.ValueKind == JsonValueKind.Object
                && details.TryGetProperty("totals", out JsonElement totals) && totals.ValueKind == JsonValueKind.Object
                    ? Str(totals, "currency_code")
                    : null);

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime ParseTime(string s)
        => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime;
}
