using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ReelBolt.Inference.Api.Services.Billing.Stripe;

/// <summary>Configuration section <c>Billing:Stripe</c> (env <c>Billing__Stripe__*</c>). Secrets are never logged.</summary>
public sealed class StripeOptions
{
    /// <summary>Configuration section carrying the Stripe settings.</summary>
    public const string SectionName = "Billing:Stripe";

    /// <summary>The Stripe API version every request is pinned to and every webhook endpoint must be created with.</summary>
    public const string ApiVersion = "2026-09-30.endive";

    /// <summary>The accepted API key prefixes: secret or restricted key, test or live.</summary>
    public static readonly IReadOnlyList<string> KeyPrefixes = ["sk_test_", "sk_live_", "rk_test_", "rk_live_"];

    /// <summary>Server-side secret (<c>sk_…</c>) or restricted (<c>rk_…</c>) key. A secret.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The webhook endpoint's signing secret (<c>whsec_…</c>). A secret.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>The previous signing secret, kept valid during a rotation overlap. A secret.</summary>
    public string? WebhookSecretPrevious { get; set; }

    /// <summary>API base URL; override only for tests or stripe-mock.</summary>
    public string BaseUrl
    {
        get => _baseUrl;
        // One optional trailing slash is accepted and dropped, so every consumer sees the exact origin.
        set => _baseUrl = value is { Length: > 0 } && value[^1] == '/' ? value[..^1] : value;
    }

    private string _baseUrl = "https://api.stripe.com";

    /// <summary>Largest accepted distance between the signed timestamp and now (Stripe's libraries default to 300).</summary>
    public int ToleranceSeconds { get; set; } = 300;

    /// <summary>Whether Stripe Managed Payments (merchant of record) is on; otherwise Stripe Tax is used.</summary>
    public bool ManagedPayments { get; set; }

    /// <summary>Top-up pack size in credits to Stripe price id: <c>Billing__Stripe__TopUpPriceIds__1000=price_…</c>.</summary>
    public Dictionary<string, string> TopUpPriceIds { get; set; } = [];

    /// <summary>The dedicated customer-portal configuration (<c>bpc_…</c>) passed on every portal session.</summary>
    public string? PortalConfigurationId { get; set; }

    /// <summary>Largest seat count settable from the dashboard; bigger teams go through sales.</summary>
    public int MaxSelfServeSeats { get; set; } = 200;

    /// <summary>
    /// Public origin used to build the checkout success/cancel and portal return URLs. Mirrors the other
    /// public-origin settings (<c>CloudStorage:PublicBaseUrl</c>): an absolute http(s) URL, no trailing slash needed.
    /// </summary>
    public string? ReturnBaseUrl { get; set; }

    /// <summary>Per-session Checkout branding (<c>Billing:Stripe:Branding</c>); every value optional.</summary>
    public StripeBrandingOptions Branding { get; set; } = new();

    /// <summary>True when an API key and a webhook secret are both present (T-004 builds the provider's own check on this).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(WebhookSecret);

    /// <summary>True when the key is a live-mode key; false for a test-mode key or no key.</summary>
    public bool IsLiveMode => HasPrefix(ApiKey, "sk_live_") || HasPrefix(ApiKey, "rk_live_");

    /// <summary>True when the key is a test-mode (sandbox) key.</summary>
    public bool IsTestMode => HasPrefix(ApiKey, "sk_test_") || HasPrefix(ApiKey, "rk_test_");

    /// <summary>Every configured webhook signing secret (current first, then the rotation-overlap one).</summary>
    public IReadOnlyList<string> WebhookSecrets
        => new[] { WebhookSecret, WebhookSecretPrevious }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();

    private static bool HasPrefix(string? value, string prefix)
        => value is not null && value.StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>
    /// The configuration problem, in words (never echoing a key or secret), or null. Only a deployment that selected
    /// Stripe or set a key is checked, so an unconfigured install keeps booting.
    /// </summary>
    public string? DescribeProblem(bool stripeSelected)
    {
        bool intent = stripeSelected || !string.IsNullOrWhiteSpace(ApiKey);
        if (!intent) return null;

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            return "Billing:Provider is Stripe but Billing:Stripe:ApiKey is not set.";
        }

        if (!KeyPrefixes.Any(p => HasPrefix(ApiKey, p)))
        {
            return "Billing:Stripe:ApiKey is not a Stripe secret or restricted key (expected a value starting sk_test_, sk_live_, rk_test_ or rk_live_).";
        }

        if (string.IsNullOrWhiteSpace(WebhookSecret))
        {
            return "Billing:Stripe:WebhookSecret is not set (expected the endpoint signing secret, starting whsec_).";
        }

        if (!HasPrefix(WebhookSecret, "whsec_"))
        {
            return "Billing:Stripe:WebhookSecret is not a Stripe webhook signing secret (expected a value starting whsec_).";
        }

        if (!string.IsNullOrWhiteSpace(WebhookSecretPrevious) && !HasPrefix(WebhookSecretPrevious, "whsec_"))
        {
            return "Billing:Stripe:WebhookSecretPrevious is not a Stripe webhook signing secret (expected a value starting whsec_).";
        }

        foreach (string pack in TopUpPriceIds.Keys)
        {
            if (!int.TryParse(pack, NumberStyles.None, CultureInfo.InvariantCulture, out int credits) || credits <= 0)
            {
                return $"Billing:Stripe:TopUpPriceIds has the key '{pack}', which is not a positive whole number of credits.";
            }
        }

        if (ToleranceSeconds is < 30 or > 900)
        {
            return "Billing:Stripe:ToleranceSeconds must be between 30 and 900.";
        }

        if (!Uri.TryCreate(ReturnBaseUrl, UriKind.Absolute, out Uri? returnBase) || !IsHttpsOrLocalHttp(returnBase) || returnBase.UserInfo.Length > 0)
        {
            return "Billing:Stripe:ReturnBaseUrl must be an absolute https URL (http is accepted only for localhost or 127.0.0.1).";
        }

        if (IsLiveMode && !IsStripeApiOrigin(BaseUrl))
        {
            return "Billing:Stripe:BaseUrl must be https://api.stripe.com when a live API key is configured.";
        }

        return Branding.DescribeProblem();
    }

    private static bool IsHttpsOrLocalHttp(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1"));

    private static bool IsStripeApiOrigin(string? baseUrl)
        => string.Equals(baseUrl, "https://api.stripe.com", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Checkout branding sent as <c>branding_settings</c> on each session. Empty means "do not send": Stripe then uses the
/// Dashboard branding.
/// </summary>
public sealed partial class StripeBrandingOptions
{
    public const string SectionName = "Billing:Stripe:Branding";

    /// <summary>The accepted <c>BorderStyle</c> values.</summary>
    public static readonly IReadOnlyList<string> BorderStyles = ["pill", "rectangular", "rounded"];

    public string? BackgroundColor { get; set; }

    public string? ButtonColor { get; set; }

    public string? BorderStyle { get; set; }

    public string? FontFamily { get; set; }

    public string? DisplayName { get; set; }

    public string? LogoUrl { get; set; }

    public string? IconUrl { get; set; }

    /// <summary>True when at least one value is set.</summary>
    public bool Any => new[] { BackgroundColor, ButtonColor, BorderStyle, FontFamily, DisplayName, LogoUrl, IconUrl }.Any(v => !string.IsNullOrWhiteSpace(v));

    [GeneratedRegex("^#[0-9A-Fa-f]{6}\\z")]
    private static partial Regex HexColor();

    [GeneratedRegex("^[a-z0-9_]{1,40}$")]
    private static partial Regex FontToken();

    /// <summary>The problem in words (naming the setting, never echoing a URL), or null.</summary>
    public string? DescribeProblem()
    {
        if (!string.IsNullOrWhiteSpace(BackgroundColor) && !HexColor().IsMatch(BackgroundColor))
        {
            return "Billing:Stripe:Branding:BackgroundColor must be a hex colour like #0B0B12.";
        }

        if (!string.IsNullOrWhiteSpace(ButtonColor) && !HexColor().IsMatch(ButtonColor))
        {
            return "Billing:Stripe:Branding:ButtonColor must be a hex colour like #7C3AED.";
        }

        if (!string.IsNullOrWhiteSpace(BorderStyle) && !BorderStyles.Contains(BorderStyle, StringComparer.Ordinal))
        {
            return "Billing:Stripe:Branding:BorderStyle must be one of pill, rectangular, rounded.";
        }

        if (!string.IsNullOrWhiteSpace(FontFamily) && !FontToken().IsMatch(FontFamily))
        {
            return "Billing:Stripe:Branding:FontFamily must be a lowercase Stripe font token such as chakra_petch.";
        }

        if (!string.IsNullOrWhiteSpace(DisplayName) && (DisplayName.Length > 100 || DisplayName.Any(char.IsControl)))
        {
            return "Billing:Stripe:Branding:DisplayName must be 1 to 100 characters without control characters.";
        }

        if (!string.IsNullOrWhiteSpace(LogoUrl) && !IsPublicHttps(LogoUrl))
        {
            return "Billing:Stripe:Branding:LogoUrl must be an absolute https URL (at most 2048 characters, no credentials).";
        }

        if (!string.IsNullOrWhiteSpace(IconUrl) && !IsPublicHttps(IconUrl))
        {
            return "Billing:Stripe:Branding:IconUrl must be an absolute https URL (at most 2048 characters, no credentials).";
        }

        return null;
    }

    private static bool IsPublicHttps(string value)
        => value.Length <= 2048
            && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0;
}

/// <summary>
/// Rejects a misconfigured Stripe section while the service is starting. Enforced only when
/// <c>Billing:Provider = Stripe</c> or an API key is set, so an install that does not sell through Stripe boots unchanged.
/// </summary>
public sealed class StripeOptionsValidator(IOptions<BillingProviderOptions>? provider = null) : IValidateOptions<StripeOptions>
{
    public ValidateOptionsResult Validate(string? name, StripeOptions options)
    {
        string selected = provider?.Value.Selected ?? BillingProviderOptions.PaddleName;
        bool stripeSelected = string.Equals(selected, BillingProviderOptions.StripeName, StringComparison.OrdinalIgnoreCase);
        return options.DescribeProblem(stripeSelected) is { } problem
            ? ValidateOptionsResult.Fail(problem)
            : ValidateOptionsResult.Success;
    }
}

/// <summary>The metadata keys this platform writes at checkout (T-004) and reads back from webhooks.</summary>
public static class StripeMetadataKeys
{
    public const string OrganizationId = "organization_id";
    public const string UserId = "user_id";
    public const string PromotionId = "promotion_id";
    public const string BonusPromotionId = "bonus_promotion_id";
    public const string TopUpCredits = "topup_credits";
}
