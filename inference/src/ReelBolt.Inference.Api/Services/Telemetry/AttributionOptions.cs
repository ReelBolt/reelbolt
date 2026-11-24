namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// Binding for the <c>Attribution</c> configuration section (new-user funnel,
/// docs/design/new-user-funnel.md section 6).
/// </summary>
public sealed class AttributionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Attribution";

    /// <summary>
    /// Whether the Go API records the campaign parameters carried in a signup request when there is no
    /// consented first-touch cookie (<c>user_attribution.capture = 'url'</c>). The Go API reads env
    /// <c>ATTRIBUTION_URL_CAPTURE_ENABLED</c> and acts on it; this API only MIRRORS the same variable
    /// (compose <c>Attribution__UrlCaptureEnabled</c>) so the platform-admin links funnel can say whether
    /// its account counts per link are complete. Default false. Setting it here alone captures nothing.
    /// </summary>
    public bool UrlCaptureEnabled { get; set; }
}
