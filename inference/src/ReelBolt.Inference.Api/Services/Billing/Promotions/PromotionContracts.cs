using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Services.Billing.Promotions;

/// <summary>
/// Body of <c>POST /api/v1/billing/admin/promotions</c> (frozen contract). Enum fields are strings,
/// accepted case-insensitively (<c>planDiscount</c>, <c>PlanDiscount</c>); only the fields of the
/// chosen <see cref="Kind"/> are read.
/// </summary>
public sealed class CreatePromotionDto
{
    public string? Kind { get; set; }
    public string? Name { get; set; }
    public string? PublicHeadline { get; set; }
    public string? Code { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public int? MaxRedemptions { get; set; }
    public int? MaxRedemptionsPerOrg { get; set; }

    /// <summary>CreditCode: whole credits per redemption (1..100000).</summary>
    public int? Credits { get; set; }

    /// <summary>CreditCode: days until the granted credits expire (1..730, default 90).</summary>
    public int? CreditExpiryDays { get; set; }

    public string? DiscountType { get; set; }
    public string? DiscountAmount { get; set; }
    public string? CurrencyCode { get; set; }
    public string? Duration { get; set; }
    public int? DurationMonths { get; set; }
    public IReadOnlyList<string>? PlanKeys { get; set; }

    /// <summary>Plan discounts only: also target the plans' annual prices. Default false (monthly prices only).</summary>
    public bool? IncludeAnnual { get; set; }
    public IReadOnlyList<int>? PackCredits { get; set; }
    public int? BonusPercent { get; set; }
}

/// <summary>Body of <c>POST /api/v1/billing/promotions/redeem</c>.</summary>
public sealed record RedeemPromotionDto(string? Code);

/// <summary>Body of <c>POST /api/v1/billing/admin/orgs/{orgId}/credit-grants</c>.</summary>
public sealed record GrantCreditsDto(Guid RequestId, int? Credits, DateTime? ExpiresAt, string? Reason);

/// <summary>What a code redemption granted.</summary>
public sealed record RedeemResult(double Credits, DateTime ExpiresAt, string? Headline);

/// <summary>An admin credit grant, and whether it was an earlier request replayed.</summary>
public sealed record AdminGrantOutcome(AdminCreditGrant Grant, bool Replayed);

/// <summary>What a checkout buys, for matching promotions: the provider price id, and the pack size for a top-up.</summary>
public sealed record CheckoutTarget(string PriceId, int? PackCredits)
{
    public bool IsPack => PackCredits is not null;
}

/// <summary>
/// The promotions a checkout carries: a provider discount and, for a top-up, an independent bonus.
/// <see cref="CodePromotionId"/> is the one the buyer typed a code for (a code that stops applying is
/// refused; an automatic promotion that stops applying is dropped).
/// </summary>
public sealed record CheckoutPromotions(Promotion? Discount, Promotion? Bonus, Guid? CodePromotionId = null)
{
    public static readonly CheckoutPromotions None = new(null, null);

    public bool IsEmpty => Discount is null && Bonus is null;
}

/// <summary>
/// A checkout's reserved promotions (contract C8): the <see cref="PromotionCheckout"/> row id, or null
/// when nothing is reserved, and the promotions that survived the locked cap check. <paramref name="Superseded"/>
/// lists the caller's earlier reservations this one superseded, whose provider transactions are to be
/// cancelled once the new checkout exists (contract C10).
/// </summary>
public sealed record CheckoutReservation(Guid? Id, CheckoutPromotions Promotions, IReadOnlyList<Guid>? Superseded = null)
{
    public static readonly CheckoutReservation None = new(null, CheckoutPromotions.None);
}

/// <summary>
/// A refusal with its HTTP status. Rendered as <c>{ error, message }</c> (the shape the rest of
/// <c>/api/v1/billing</c> uses), or as <c>{ message, errors }</c> for a validation failure.
/// </summary>
/// <param name="RetryAfterSeconds">When set, the response carries <c>Retry-After</c> with this many seconds.</param>
public sealed record PromotionError(int Status, string Code, string Message, IReadOnlyList<string>? Errors = null, int? RetryAfterSeconds = null)
{
    public IActionResult ToResult()
    {
        if (Status == StatusCodes.Status404NotFound)
        {
            return new NotFoundResult();
        }

        object body = Errors is not null
            ? new { message = Message, errors = Errors }
            : Code == PromotionErrors.EmailNotVerifiedCode
                // The EMAIL_NOT_VERIFIED shape the rest of the API (and the web client) reads is { code, message }.
                ? new { code = Code, error = Code, message = Message }
                : new { error = Code, message = Message };
        return RetryAfterSeconds is int seconds
            ? new RetryAfterObjectResult(body, seconds) { StatusCode = Status }
            : new ObjectResult(body) { StatusCode = Status };
    }

    /// <summary>An <see cref="ObjectResult"/> that sets <c>Retry-After</c> as it is written.</summary>
    private sealed class RetryAfterObjectResult(object body, int seconds) : ObjectResult(body)
    {
        public override void OnFormatting(ActionContext context)
        {
            base.OnFormatting(context);
            context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static PromotionError Validation(IReadOnlyList<string> errors)
        => new(StatusCodes.Status400BadRequest, "validation_failed", "The request is not valid.", errors);
}

/// <summary>The fixed refusals. <see cref="Invalid"/> is one instance so every invalid reason renders byte-identically.</summary>
public static class PromotionErrors
{
    public static readonly PromotionError Invalid = new(StatusCodes.Status400BadRequest, "promo_code_invalid", "That code is not valid.");
    public static readonly PromotionError RateLimited = new(StatusCodes.Status429TooManyRequests, "promo_rate_limited", "Too many invalid code attempts. Try again later.");
    public static readonly PromotionError AlreadyRedeemed = new(StatusCodes.Status409Conflict, "promo_already_redeemed", "This organization has already redeemed that code.");
    public static readonly PromotionError CodeTaken = new(StatusCodes.Status409Conflict, "promo_code_taken", "Another promotion already uses that code.");
    public static readonly PromotionError Overlap = new(StatusCodes.Status409Conflict, "automatic_promotion_overlap", "An active automatic promotion of this kind already covers some of these targets in an overlapping window.");
    public static readonly PromotionError NotConfigured = new(StatusCodes.Status503ServiceUnavailable, "billing_not_configured", "No billing provider is configured.");
    public static readonly PromotionError ProviderError = new(StatusCodes.Status502BadGateway, "billing_provider_error", "The billing provider rejected the request. Nothing was saved.");
    public static readonly PromotionError NotFound = new(StatusCodes.Status404NotFound, "not_found", "Not found.");
    public const string EmailNotVerifiedCode = "EMAIL_NOT_VERIFIED";

    /// <summary>A promotion code (or a credit-code redeem) from an account whose email is not verified (C8).</summary>
    public static readonly PromotionError EmailNotVerified = new(StatusCodes.Status403Forbidden, EmailNotVerifiedCode, "Verify your email address before using a promotion. Check your inbox for the verification link.");

    /// <summary>More checkouts carrying a promotion than <c>Billing:Promotions:CheckoutsPerUserPerHour</c> (C8).</summary>
    public static readonly PromotionError CheckoutRateLimited = new(StatusCodes.Status429TooManyRequests, "promo_checkout_rate_limited", "Too many checkouts with a promotion. Try again later.");

    /// <summary>
    /// S-11: the checkout's promotions stayed locked by other checkouts past
    /// <c>Billing:Promotions:CheckoutLockWaitMilliseconds</c>. Nothing was reserved; retry shortly.
    /// </summary>
    public static readonly PromotionError CheckoutBusy = new(StatusCodes.Status429TooManyRequests, "checkout_busy", "Many checkouts with this promotion are in progress. Try again in a moment.", RetryAfterSeconds: 2);

    /// <summary>
    /// F-5: a newer checkout of the same user or org replaced this one while the provider was creating it.
    /// Its provider transaction is cancelled, so no payable discounted checkout is handed out.
    /// </summary>
    public static readonly PromotionError CheckoutSuperseded = new(StatusCodes.Status409Conflict, "promo_checkout_superseded", "A newer checkout with this promotion replaced this one. Use the newer checkout, or start again.");

    public static readonly PromotionError RequestIdConflict = new(StatusCodes.Status409Conflict, "request_id_conflict", "That requestId was already used for a different grant.");

    public static PromotionError TargetNotPurchasable(string target)
        => new(StatusCodes.Status400BadRequest, "target_not_purchasable", $"{target} has no provider price, so it cannot be targeted.");
}

/// <summary>A service answer: a value, or a refusal.</summary>
public sealed record PromotionOutcome<T>(T? Value, PromotionError? Error)
{
    public static PromotionOutcome<T> Success(T value) => new(value, null);

    public static implicit operator PromotionOutcome<T>(PromotionError error) => new(default, error);
}

/// <summary>The editable fields of <c>PATCH /api/v1/billing/admin/promotions/{id}</c>, parsed strictly.</summary>
public sealed record PromotionPatch(
    string? Name,
    bool HasHeadline,
    string? PublicHeadline,
    PromotionStatus? Status,
    DateTime? EndsAt,
    bool ClearEndsAt)
{
    private static readonly string[] Allowed = ["name", "publicHeadline", "status", "endsAt", "clearEndsAt"];

    /// <summary>Parses the body; any field other than the editable ones is an error (value fields are immutable).</summary>
    public static bool TryParse(JsonElement body, out PromotionPatch patch, out List<string> errors)
    {
        errors = [];
        patch = new PromotionPatch(null, false, null, null, null, false);
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors.Add("The body must be a JSON object.");
            return false;
        }

        string? name = null;
        bool hasHeadline = false;
        string? headline = null;
        PromotionStatus? status = null;
        DateTime? endsAt = null;
        bool clear = false;
        foreach (JsonProperty prop in body.EnumerateObject())
        {
            string? key = Allowed.FirstOrDefault(a => string.Equals(a, prop.Name, StringComparison.OrdinalIgnoreCase));
            JsonElement v = prop.Value;
            switch (key)
            {
                case "name":
                    name = v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : null;
                    if (string.IsNullOrEmpty(name) || name.Length > PromotionRules.MaxNameLength)
                    {
                        errors.Add($"name must be 1 to {PromotionRules.MaxNameLength} characters.");
                    }

                    break;
                case "publicHeadline":
                    hasHeadline = true;
                    headline = v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : null;
                    if (v.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || headline?.Length > PromotionRules.MaxNameLength)
                    {
                        errors.Add($"publicHeadline must be at most {PromotionRules.MaxNameLength} characters.");
                    }

                    headline = string.IsNullOrEmpty(headline) ? null : headline;
                    break;
                case "status":
                    if (v.ValueKind == JsonValueKind.String && PromotionRules.TryParseEnum(v.GetString(), out PromotionStatus s))
                    {
                        status = s;
                    }
                    else
                    {
                        errors.Add("status must be active, paused or archived.");
                    }

                    break;
                case "endsAt":
                    if (v.ValueKind == JsonValueKind.Null)
                    {
                        clear = true;
                    }
                    else if (v.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime e))
                    {
                        endsAt = DateTime.SpecifyKind(e, DateTimeKind.Utc);
                    }
                    else
                    {
                        errors.Add("endsAt must be an ISO date-time.");
                    }

                    break;
                case "clearEndsAt":
                    if (v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        clear = clear || v.GetBoolean();
                    }
                    else
                    {
                        errors.Add("clearEndsAt must be a boolean.");
                    }

                    break;
                default:
                    errors.Add($"{prop.Name} cannot be changed after create; only name, publicHeadline, status and endsAt can.");
                    break;
            }
        }

        if (clear && endsAt is not null)
        {
            errors.Add("Send either endsAt or clearEndsAt, not both.");
        }

        patch = new PromotionPatch(name, hasHeadline, headline, status, endsAt, clear);
        return errors.Count == 0;
    }
}

/// <summary>
/// <c>PromotionDto</c>: every promotion column in camelCase, the jsonb targets as arrays, credits as
/// whole credits, enums as camelCase strings, plus the redemption count.
/// </summary>
public sealed record PromotionDto(
    Guid Id,
    string Kind,
    string Name,
    string? PublicHeadline,
    string? Code,
    string Status,
    DateTime? StartsAt,
    DateTime? EndsAt,
    int? MaxRedemptions,
    int RedemptionCount,
    int MaxRedemptionsPerOrg,
    double? Credits,
    int? CreditExpiryDays,
    string? DiscountType,
    string? DiscountAmount,
    string? CurrencyCode,
    string? DurationKind,
    int? DurationMonths,
    IReadOnlyList<string> TargetPlanKeys,
    IReadOnlyList<int> TargetPackCredits,
    IReadOnlyList<string> TargetPriceIds,
    int? BonusPercent,
    string? ProviderDiscountId,
    DateTime CreatedAt,
    Guid CreatedByUserId,
    DateTime UpdatedAt,
    int Redemptions)
{
    public static PromotionDto From(Promotion p, int redemptions) => new(
        p.Id,
        PromotionRules.Camel(p.Kind),
        p.Name,
        p.PublicHeadline,
        p.Code,
        PromotionRules.Camel(p.Status),
        PromotionRules.Utc(p.StartsAt),
        PromotionRules.Utc(p.EndsAt),
        p.MaxRedemptions,
        p.RedemptionCount,
        p.MaxRedemptionsPerOrg,
        p.CreditsMillis / 1000.0,
        p.CreditExpiryDays,
        p.DiscountType is { } t ? PromotionRules.Camel(t) : null,
        p.DiscountAmount,
        p.CurrencyCode,
        p.DurationKind is { } d ? PromotionRules.Camel(d) : null,
        p.DurationMonths,
        PromotionRules.ReadStrings(p.TargetPlanKeysJson),
        PromotionRules.ReadInts(p.TargetPackCreditsJson),
        PromotionRules.ReadStrings(p.TargetPriceIdsJson),
        p.BonusPercent,
        p.ProviderDiscountId,
        PromotionRules.Utc(p.CreatedAt),
        p.CreatedByUserId,
        PromotionRules.Utc(p.UpdatedAt),
        redemptions);
}

/// <summary>Shared parsing and formatting helpers for promotions.</summary>
public static class PromotionRules
{
    public const int MaxNameLength = 120;

    /// <summary><c>CreditCode</c> → <c>creditCode</c>: how promotion enums are written to JSON.</summary>
    public static string Camel<T>(T value) where T : struct, Enum
    {
        string name = value.ToString();
        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>A member name in any case; numbers and unknown names are refused.</summary>
    public static bool TryParseEnum<T>(string? raw, out T value) where T : struct, Enum
    {
        value = default;
        string? s = raw?.Trim();
        if (string.IsNullOrEmpty(s) || !char.IsLetter(s[0]))
        {
            return false;
        }

        return Enum.TryParse(s, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    public static IReadOnlyList<string> ReadStrings(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<int> ReadInts(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<int>>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Stored timestamps are UTC; Sqlite and some providers read them back unspecified.</summary>
    public static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    /// <summary>A request timestamp as UTC (an unzoned value is taken as UTC).</summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
