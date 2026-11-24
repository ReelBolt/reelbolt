using Microsoft.Extensions.Options;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>Configuration section <c>Billing:Paddle</c> (env <c>Billing__Paddle__*</c>).</summary>
/// <remarks>
/// Deliberately NOT a nested member of a billing options class. The <c>Billing</c> section already has
/// an owner — <c>ReelBolt.Shared.Billing.BillingOptions</c> (B8a), which carries <c>Enabled</c>,
/// <c>TrialPlanKey</c> and <c>EntitlementCacheSeconds</c> and is registered by
/// <c>AddReelBoltEntitlements</c> in both services. B9b first declared a second <c>BillingOptions</c>
/// in this namespace carrying <c>Enabled</c> + <c>Paddle</c>, which collided with it on merge; binding
/// Paddle on its own section removes the duplicate type instead of papering over the ambiguity.
/// </remarks>
public sealed class PaddleOptions
{
    /// <summary>Configuration section carrying the Paddle settings.</summary>
    public const string SectionName = "Billing:Paddle";

    /// <summary>The two Paddle environments, and the only two accepted values.</summary>
    public const string SandboxEnvironment = "sandbox";
    public const string LiveEnvironment = "live";

    /// <summary>Legacy nested accessor kept so existing <c>options.Value.Paddle</c> call sites read unchanged.</summary>
    public PaddleOptions Paddle => this;
    /// <summary>Server-side API key (<c>pdl_sdbx_apikey_…</c> or <c>pdl_live_apikey_…</c>). A secret.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The notification destination's secret key. A secret.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// The CLIENT-side (publishable) token — <c>test_…</c> or <c>live_…</c>
    /// (<c>Billing__Paddle__ClientToken</c>). Deliberately NOT a secret: Paddle's browser library is
    /// initialized with it, so it is designed to be served to the buyer's browser, and it cannot
    /// read or spend anything on its own. The API key above is the credential that must never leave
    /// the server; this one is the only Paddle credential the dashboard is ever given.
    /// </summary>
    /// <remarks>
    /// Served to the dashboard from the billing summary rather than baked into the web bundle as a
    /// <c>NEXT_PUBLIC_*</c> value: the web image is built in Docker, so a build-time value cannot be
    /// rotated without a rebuild, and — more importantly — it would let the browser be initialized
    /// against a different Paddle environment than the server is billing through. One response
    /// carries both, so they cannot disagree.
    /// </remarks>
    public string? ClientToken { get; set; }

    /// <summary>
    /// <c>sandbox</c> or <c>live</c>, case-insensitive. There is deliberately **no default**: the value
    /// decides which Paddle account money moves through, so guessing it is the one mistake this type
    /// must not make quietly. See <see cref="DescribeProblem"/> and <see cref="ResolvedBaseUrl"/>.
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>Overrides the API base URL (tests only).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Largest accepted age of the signed timestamp. Paddle's SDKs default to 5 seconds.</summary>
    public int ToleranceSeconds { get; set; } = 5;

    /// <summary>
    /// Sent as <c>enabled_for_checkout</c> on every discount this platform creates. Default false: the
    /// server passes the discount id itself and buyers cannot type codes into Paddle's checkout.
    /// Whether a disabled discount survives <c>Checkout.open({transactionId})</c> is unverified (sandbox
    /// only); this switch is the escape hatch if it does not.
    /// </summary>
    public bool DiscountsEnabledForCheckout { get; set; }

    /// <summary>
    /// Largest seat count a per-seat subscription can be set to from the dashboard (<c>/seats</c> and
    /// <c>/change-plan</c> answer <c>400 too_many_seats</c> above it); bigger teams go through sales.
    /// Env <c>Billing__Paddle__MaxSelfServeSeats</c>.
    /// </summary>
    public int MaxSelfServeSeats { get; set; } = 200;

    /// <summary>Top-up pack size in credits to Paddle price id: <c>Billing__Paddle__TopUpPriceIds__1000=pri_…</c>.</summary>
    public Dictionary<string, string> TopUpPriceIds { get; set; } = [];

    /// <summary>Normalised environment, or null when unset or not one of the two accepted values.</summary>
    public string? EnvironmentKind
    {
        get
        {
            string value = Environment?.Trim() ?? string.Empty;
            if (value.Equals(SandboxEnvironment, StringComparison.OrdinalIgnoreCase)) return SandboxEnvironment;
            if (value.Equals(LiveEnvironment, StringComparison.OrdinalIgnoreCase)) return LiveEnvironment;
            return null;
        }
    }

    public bool IsLive => EnvironmentKind == LiveEnvironment;

    /// <summary>
    /// Whether this deployment bills through Paddle's sandbox. False when the environment is
    /// <c>live</c>; a null <see cref="EnvironmentKind"/> is "not configured" rather than either, which
    /// is why the dashboard reads a resolved environment and never this flag alone.
    /// </summary>
    public bool IsSandbox => EnvironmentKind == SandboxEnvironment;

    /// <summary>
    /// Whether an operator has configured billing at all. An API key is what marks the intent: an
    /// install with no Paddle credentials is simply not selling, which is a supported state and must
    /// keep booting. A self-host deployment relies on this.
    /// </summary>
    public bool IsConfiguredIntent => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// The configuration problem, in words, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Only a deployment that has already declared intent gets a complaint. The dangerous case this
    /// exists for is an API key with no <c>Environment</c>: the key alone does not say which account
    /// it belongs to, and before this the code answered <c>sandbox</c> for anything that was not the
    /// exact string <c>live</c> — so <c>Environment=production</c>, a typo, or a variable that never
    /// got delivered all silently pointed a live deployment at the sandbox API, where the mistake
    /// surfaces as a confusing auth failure rather than as the configuration error it is.
    /// </remarks>
    public string? DescribeProblem()
    {
        if (!IsConfiguredIntent) return null;
        if (EnvironmentKind is not null) return null;
        return string.IsNullOrWhiteSpace(Environment)
            ? "Billing:Paddle:ApiKey is set but Billing:Paddle:Environment is not. Set it to 'sandbox' or 'live' — the key alone does not say which Paddle account to use."
            : $"Billing:Paddle:Environment is '{Environment}', which is not a Paddle environment. The only accepted values are 'sandbox' and 'live'.";
    }

    public string ResolvedBaseUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(BaseUrl)) return BaseUrl!.TrimEnd('/');
            return EnvironmentKind switch
            {
                LiveEnvironment => "https://api.paddle.com",
                SandboxEnvironment => "https://sandbox-api.paddle.com",
                _ => throw new InvalidOperationException(
                    DescribeProblem()
                    ?? "Billing:Paddle:Environment is not set and Billing:Paddle:BaseUrl was not supplied. This code path is only reachable once billing is configured, which should have been rejected at startup.")
            };
        }
    }
}

/// <summary>
/// Rejects a misconfigured Paddle section while the service is still starting, so the operator gets
/// the sentence that says what to fix instead of a checkout that fails later or a silent wrong-account
/// call. Registered with <c>ValidateOnStart</c>; see <see cref="PaddleOptions.DescribeProblem"/> for
/// why an install with no API key is deliberately not an error.
/// </summary>
public sealed class PaddleOptionsValidator : IValidateOptions<PaddleOptions>
{
    public ValidateOptionsResult Validate(string? name, PaddleOptions options)
        => options.DescribeProblem() is { } problem
            ? ValidateOptionsResult.Fail(problem)
            : ValidateOptionsResult.Success;
}

/// <summary>
/// Which billing provider takes new checkouts: <c>Billing:Provider</c> (env <c>Billing__Provider</c>),
/// <c>Paddle</c> (default) or <c>Stripe</c>, case-insensitive. Bound on the <c>Billing</c> section like the
/// shared <c>ReelBolt.Shared.Billing.BillingOptions</c> (both read the same section; this one only the provider key).
/// Existing subscriptions keep using the provider named on their row regardless of this setting.
/// </summary>
public sealed class BillingProviderOptions
{
    /// <summary>Configuration section carrying <see cref="Provider"/>.</summary>
    public const string SectionName = "Billing";

    public const string PaddleName = "Paddle";
    public const string StripeName = "Stripe";

    /// <summary>The provider names accepted by <see cref="Provider"/>.</summary>
    public static readonly IReadOnlyList<string> Known = [PaddleName, StripeName];

    public string Provider { get; set; } = PaddleName;

    /// <summary>The selected provider's name; a blank value means the default (Paddle).</summary>
    public string Selected => string.IsNullOrWhiteSpace(Provider) ? PaddleName : Provider.Trim();

    /// <summary>The configuration problem, in words, or null when there is none.</summary>
    public string? DescribeProblem()
        => Known.Any(k => string.Equals(k, Selected, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"Billing:Provider is '{Provider}', which is not a billing provider. The only accepted values are 'Paddle' and 'Stripe'.";
}

/// <summary>Rejects an unknown <c>Billing:Provider</c> while the service is starting.</summary>
public sealed class BillingProviderOptionsValidator : IValidateOptions<BillingProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, BillingProviderOptions options)
        => options.DescribeProblem() is { } problem
            ? ValidateOptionsResult.Fail(problem)
            : ValidateOptionsResult.Success;
}
