namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// Binding for the <c>Telemetry</c> configuration section (alpha-launch T-018). Every switch here
/// defaults to today's behaviour, so a deployment that has not opted in is unchanged: with
/// <see cref="ClientIngestEnabled"/> false the client telemetry endpoints answer <c>404</c>, exactly
/// as they did before they existed.
/// </summary>
public sealed class AlphaTelemetryOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Telemetry";

    /// <summary>
    /// Whether the browser may report the alpha funnel events and client errors to
    /// <c>POST /api/v1/telemetry/*</c>. The alpha environment sets this true; the default (false)
    /// keeps a self-host deployment identical to before.
    /// </summary>
    public bool ClientIngestEnabled { get; set; }

    /// <summary>
    /// Whether the anonymous <c>POST /api/v1/telemetry/visit</c> endpoint accepts visits (env
    /// <c>TELEMETRY_VISIT_INGEST_ENABLED</c>). Default false: the endpoint answers 404.
    /// </summary>
    public bool VisitIngestEnabled { get; set; }

    /// <summary>Months a visitor-only product_events row is kept before the retention sweep deletes it.</summary>
    public int VisitRetentionMonths { get; set; } = 13;

    /// <summary>
    /// Months account-linked <c>product_events</c> rows (except consent-choice evidence) and
    /// <c>feedback_reports</c> are kept before the retention sweep deletes them. Minimum 1.
    /// </summary>
    public int RetentionMonths { get; set; } = 12;

    /// <summary>Hostnames that are the site itself; a referrer on one of them is dropped as a self-referral.</summary>
    public string[] OwnHosts { get; set; } = ["reelbolt.ai"];
}
