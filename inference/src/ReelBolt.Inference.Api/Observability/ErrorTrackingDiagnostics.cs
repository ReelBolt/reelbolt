using System.Diagnostics;
using System.Diagnostics.Metrics;
using ReelBolt.Inference.Api.Services.Telemetry;

namespace ReelBolt.Inference.Api.Observability;

/// <summary>
/// Error tracking (alpha-launch T-018). Every error the service records is counted on
/// <c>reelbolt.errors</c> and stamped on a span, so a failure reaches the same watched OTLP
/// destination as the traces and metrics (docs/observability.md). Deliberately vendor-neutral: there
/// is no third-party SDK, and the destination is whatever <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> names.
/// </summary>
public static class ErrorTrackingDiagnostics
{
    /// <summary>Meter name; added to the exported meters in <c>AddInferenceApiServices</c>.</summary>
    public const string MeterName = "ReelBolt.Errors";

    /// <summary>
    /// The source the service's own spans are published under. The name matches the resource
    /// <c>service.name</c>, which <c>AddReelBoltTelemetry</c> already registers, so a span started
    /// here is exported with the rest without a second registration.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("ReelBolt.Inference.Api");

    /// <summary>
    /// The closed set of <c>kind</c> values. A metric dimension must be bounded, so anything else
    /// collapses to <c>other</c> (the same rule as <c>ReelBoltTelemetry.NormalizePlanTier</c>).
    /// </summary>
    private static readonly string[] Kinds = ["unhandled", "handled", "upstream", "validation", "client"];

    /// <summary>Bounds for a client-supplied field, one per field (never a shared constant).</summary>
    public const int MaxNameLength = 128;

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Errors observed by the service, one per recorded error, tagged <c>kind</c>.</summary>
    public static readonly Counter<long> Errors =
        Meter.CreateCounter<long>("reelbolt.errors", "errors", "Errors observed by the service, tagged with kind");

    /// <summary>
    /// Counts one server error and stamps it on the active (request) span. Reporting only: it never
    /// refuses, rewrites or swallows anything, and never throws into the caller. Only the exception's
    /// TYPE is recorded, never its message, which can carry user prose.
    /// </summary>
    /// <param name="exception">The error to record.</param>
    /// <param name="kind">One of <c>unhandled</c>, <c>handled</c>, <c>upstream</c>, <c>validation</c>.</param>
    /// <param name="environment">The deployment environment, so a signal is attributable without a resource lookup.</param>
    public static void Record(Exception exception, string kind, string? environment = null)
    {
        try
        {
            string normalized = NormalizeKind(kind);
            Add(normalized, environment);

            Activity? activity = Activity.Current;
            if (activity is null) return;
            activity.SetTag("error.kind", normalized);
            activity.SetTag("error.type", exception.GetType().FullName);
            activity.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        }
        catch
        {
            // Telemetry must never break the work it observes.
        }
    }

    /// <summary>
    /// Records one browser error report (alpha-launch T-018). It opens its own span rather than
    /// marking the ingestion request failed: the request itself succeeded, and the caller's error is
    /// the thing being tracked.
    /// </summary>
    /// <remarks>
    /// The client is untrusted, so this collects the minimum and validates everything it is given.
    /// Only two fields are kept: the error's <b>type</b> (bounded by <see cref="MaxNameLength"/>) and
    /// a <b>redacted</b> route (<see cref="RouteRedactor"/>). The client's message and stack are
    /// <b>not collected at all</b> — they can carry user content, and a redaction the client claims
    /// is not a redaction the server can rely on. What is left is the type plus the route, which is
    /// the fingerprint an operator groups by.
    /// </remarks>
    public static void RecordClient(string? name, string? route, string? environment = null)
    {
        try
        {
            Add("client", environment);

            using Activity? activity = ActivitySource.StartActivity("client.error", ActivityKind.Internal);
            if (activity is null) return;
            string type = Truncate(name, MaxNameLength) ?? "Error";
            string path = RouteRedactor.Redact(route);
            activity.SetTag("error.kind", "client");
            activity.SetTag("error.type", type);
            if (path.Length > 0) activity.SetTag("http.route", path);
            activity.SetStatus(ActivityStatusCode.Error, type);
        }
        catch
        {
            // Telemetry must never break the work it observes.
        }
    }

    /// <summary>Maps a kind onto the closed set; unknown values collapse to <c>other</c>.</summary>
    public static string NormalizeKind(string? kind)
    {
        string value = kind?.Trim().ToLowerInvariant() ?? "";
        return Array.IndexOf(Kinds, value) >= 0 ? value : "other";
    }

    private static void Add(string kind, string? environment)
    {
        var tags = new TagList { { "kind", kind } };
        if (!string.IsNullOrWhiteSpace(environment)) tags.Add("environment", environment);
        Errors.Add(1, tags);
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
