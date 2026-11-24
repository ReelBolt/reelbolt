namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// First-touch acquisition attribution of one consented, anonymous visitor (table
/// <c>visitor_attribution</c>, docs/design/new-user-funnel.md section 5). Written by the visit beacon
/// the first time a visitor id is seen (<c>ON CONFLICT DO NOTHING</c>: first touch wins) and deleted by
/// the visitor retention sweep together with that visitor's <c>product_events</c> rows.
/// <para>
/// Every column is a sanitized closed-shape token (<see cref="AttributionSanitizer"/>): no IP, no user
/// agent, no free text. The visitor id is random and exists only after cookie consent. Unfiltered by
/// tenant: there is no account yet, and no route addresses a row.
/// </para>
/// </summary>
public class VisitorAttribution
{
    /// <summary>The random visitor id (primary key).</summary>
    public Guid VisitorId { get; set; }

    /// <summary><c>utm_source ?? referrerHost ?? "direct"</c>.</summary>
    public string SourceLabel { get; set; } = string.Empty;

    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }

    /// <summary>On a tracked link this is the link code.</summary>
    public string? UtmContent { get; set; }
    public string? UtmTerm { get; set; }
    public string? ReferrerHost { get; set; }
    public string? LandingPath { get; set; }

    /// <summary>When the visitor's first visit was recorded.</summary>
    public DateTimeOffset FirstTouchAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
