namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// First-touch acquisition attribution for one user (product-analytics). Written once by the Go API at
/// signup from the sanitized <c>rb_ft</c> cookie (first touch wins); this API only owns the schema and
/// the BI grant. Every column is a sanitized closed-shape token: no IP, no user agent, no free text.
/// Unfiltered by tenant: keyed by person and never addressable through any route.
/// </summary>
public class UserAttribution
{
    /// <summary>The user this attribution belongs to (primary key, cascades on user delete).</summary>
    public Guid UserId { get; set; }

    /// <summary><c>utm_source ?? referrerHost ?? "direct"</c>.</summary>
    public string SourceLabel { get; set; } = string.Empty;

    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? UtmContent { get; set; }
    public string? UtmTerm { get; set; }
    public string? ReferrerHost { get; set; }
    public string? LandingPath { get; set; }
    public Guid? VisitorId { get; set; }
    public DateTimeOffset? FirstTouchAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// How the row was learned (<c>AcquisitionVocabulary.AttributionCaptures</c>): <c>cookie</c> for the
    /// consented first-touch cookie, <c>url</c> for parameters carried in the signup request with no cookie.
    /// Null on rows written before the column existed.
    /// </summary>
    public string? Capture { get; set; }
}
