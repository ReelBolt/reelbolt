using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ReelBolt.Shared.Observability;

/// <summary>
/// The one OpenTelemetry registration both .NET services use (WP F9). Local instrumentation
/// (ASP.NET Core, the service's own sources and meters) is always registered, exactly as before.
/// Everything that leaves the process is gated on <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>: unset or blank
/// means no exporter, no extra instrumentation and no span processor, so a deployment that has not
/// opted in starts and behaves as it did before this package existed.
/// <para>
/// The exporter itself reads the standard <c>OTEL_EXPORTER_OTLP_*</c> variables (endpoint, headers,
/// protocol, timeout), so Grafana Cloud's <c>Authorization=Basic ...</c> header travels in
/// <c>OTEL_EXPORTER_OTLP_HEADERS</c> and never through an application setting.
/// </para>
/// </summary>
public static class ReelBoltTelemetry
{
    /// <summary>Span attribute carrying the tenant. Spans only: metrics must never carry it.</summary>
    public const string OrgIdTag = "reelbolt.org_id";

    /// <summary>Metric dimension: billing plan key, normalized to a small closed set.</summary>
    public const string PlanTierTag = "plan_tier";

    /// <summary>Metric dimension: where media work ran (<c>cloud</c>, <c>runner</c>, <c>unknown</c>).</summary>
    public const string ComputeTargetTag = "compute_target";

    /// <summary>MassTransit publishes its spans and metrics under this name.</summary>
    public const string MassTransitSourceName = "MassTransit";

    /// <summary>Npgsql (10.x) publishes its connection-pool and command metrics under this meter.</summary>
    public const string NpgsqlMeterName = "Npgsql";

    /// <summary>
    /// S-08: set on an outgoing request whose URL is the user's secret (a URL import: a signed URL's path
    /// and query are its credential). Its value — the request's origin — is all its client span exports.
    /// </summary>
    public static readonly HttpRequestOptionsKey<string> OriginOnlyUrl = new("ReelBolt.Telemetry.OriginOnlyUrl");

    /// <summary>True when the operator pointed the process at an OTLP collector.</summary>
    public static bool IsExportEnabled(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])
           || !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"])
           || !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]);

    /// <summary>
    /// Registers tracing + metrics. Returns whether OTLP export was enabled.
    /// </summary>
    /// <param name="serviceName">Resource <c>service.name</c> (also the service's own source and meter name).</param>
    /// <param name="organizationAccessor">Current organization for span tagging; may return null.</param>
    /// <param name="extraMeters">Additional meter names to collect.</param>
    public static bool AddReelBoltTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        Func<IServiceProvider, Guid?> organizationAccessor,
        params string[] extraMeters)
    {
        bool export = IsExportEnabled(configuration);

        string version = typeof(ReelBoltTelemetry).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? "0.0.0";
        string environment = FirstNonBlank(
            configuration["OTEL_DEPLOYMENT_ENVIRONMENT"],
            configuration["ASPNETCORE_ENVIRONMENT"],
            configuration["DOTNET_ENVIRONMENT"]) ?? "Production";

        services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: version)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment", environment)]))
            .WithTracing(tracing =>
            {
                // The redaction processor comes before every exporter's, so no exported span carries a
                // live output-link token (S-07) or a user import URL's path/query (S-08).
                tracing
                    .AddSource(serviceName)
                    .AddAspNetCoreInstrumentation()
                    .AddProcessor(new SensitiveUrlRedactionProcessor());
                if (!export) return;

                tracing
                    .AddHttpClientInstrumentation(o => o.EnrichWithHttpRequestMessage = SensitiveUrlRedactionProcessor.MarkOriginOnly)
                    .AddEntityFrameworkCoreInstrumentation()
                    .AddSource(MassTransitSourceName)
                    .AddProcessor(sp => new OrganizationSpanProcessor(() => organizationAccessor(sp)))
                    .AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(serviceName);
                // Npgsql's pool/command meter: db.client.connection.count{state=idle|used},
                // db.client.connection.max, db.client.connection.npgsql.pending_requests / .timeouts, ...
                // Every one carries db.client.connection.pool.name, which Npgsql derives from the
                // connection string (SSL Password, host, user, db) unless the data source is named:
                // every Npgsql data source a service creates must set NpgsqlDataSourceBuilder.Name.
                metrics.AddMeter(NpgsqlMeterName);
                foreach (string meter in extraMeters) metrics.AddMeter(meter);
                metrics.AddAspNetCoreInstrumentation();
                if (!export) return;

                metrics
                    .AddHttpClientInstrumentation()
                    .AddMeter(MassTransitSourceName)
                    .AddOtlpExporter();
            });

        return export;
    }

    /// <summary>
    /// Maps a plan key onto a bounded set so a typo, a custom plan or a hostile value can never
    /// mint a new metric series. Unknown keys collapse to <c>other</c>.
    /// </summary>
    public static string NormalizePlanTier(string? tier)
    {
        string t = tier?.Trim().ToLowerInvariant() ?? "";
        return t switch
        {
            "" => "unknown",
            "selfhost" or "trial" or "free" or "starter" or "creator" or "pro" or "team" or "business" or "enterprise" => t,
            _ => "other"
        };
    }

    /// <summary>Maps a compute target onto <c>cloud</c> / <c>runner</c> / <c>unknown</c>.</summary>
    public static string NormalizeComputeTarget(string? target) => target?.Trim().ToLowerInvariant() switch
    {
        "cloud" => "cloud",
        "runner" => "runner",
        _ => "unknown"
    };

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>
/// Removes URL secrets from spans before any exporter sees them (judged at the end of the span, after every
/// instrumentation has set its tags):
/// <list type="bullet">
/// <item>S-07: a server span whose <c>url.path</c> is under <c>/api/v1/output-links/</c> — the path IS the
/// bearer token — is rewritten to <see cref="OutputLinkPathRedacted"/>, and so is any display name quoting it.</item>
/// <item>S-08: a client span of a request marked <see cref="ReelBoltTelemetry.OriginOnlyUrl"/> (see
/// <see cref="MarkOriginOnly"/>) keeps only its origin in <c>url.full</c>; <c>url.path</c>/<c>url.query</c> go.</item>
/// </list>
/// </summary>
public sealed class SensitiveUrlRedactionProcessor : BaseProcessor<Activity>
{
    public const string OutputLinkPrefix = "/api/v1/output-links/";

    public const string OutputLinkPathRedacted = "/api/v1/output-links/{token}";

    /// <summary>Guest review links (Wave 4): the path segment after this prefix IS the capability token.</summary>
    public const string ReviewLinkPrefix = "/api/v1/review-links/";

    public const string ReviewLinkPathRedacted = "/api/v1/review-links/{token}";

    /// <summary>Custom property (never exported) carrying the origin a marked client span is reduced to.</summary>
    public const string OriginOnlyProperty = "reelbolt.origin_only_url";

    /// <summary>HttpClient instrumentation enrich hook: remembers a marked request's origin on its span.</summary>
    public static void MarkOriginOnly(Activity activity, HttpRequestMessage request)
    {
        if (request.Options.TryGetValue(ReelBoltTelemetry.OriginOnlyUrl, out string? origin) && !string.IsNullOrEmpty(origin))
        {
            activity.SetCustomProperty(OriginOnlyProperty, origin);
            ReduceToOrigin(activity, origin);
        }
    }

    public override void OnEnd(Activity data)
    {
        if (data.GetCustomProperty(OriginOnlyProperty) is string origin)
            ReduceToOrigin(data, origin);

        foreach (string tag in (string[])["url.path", "http.target", "http.url", "url.full"])
        {
            if (data.GetTagItem(tag) is string value && RedactOutputLink(value) is { } redacted)
                data.SetTag(tag, redacted);
        }

        if (RedactOutputLink(data.DisplayName) is { } name)
            data.DisplayName = name;
    }

    private static void ReduceToOrigin(Activity activity, string origin)
    {
        activity.SetTag("url.full", origin);
        if (activity.GetTagItem("http.url") is not null) activity.SetTag("http.url", origin);
        activity.SetTag("url.path", null);
        activity.SetTag("url.query", null);
        activity.SetTag("http.target", null);
    }

    /// <summary>The value with any output-link token replaced, or null when it quotes none.</summary>
    private static string? RedactOutputLink(string? value) =>
        RedactPrefix(value, OutputLinkPrefix, OutputLinkPathRedacted)
        ?? RedactPrefix(value, ReviewLinkPrefix, ReviewLinkPathRedacted);

    private static string? RedactPrefix(string? value, string prefix, string redacted)
    {
        if (string.IsNullOrEmpty(value)) return null;
        int at = value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        string rest = value[(at + prefix.Length)..];
        if (rest.StartsWith("{token}", StringComparison.Ordinal)) return null;
        int end = rest.IndexOfAny(['?', ' ', '#', '/']);
        string tail = end < 0 ? string.Empty : (rest[end] == '?' ? string.Empty : rest[end..]);
        return value[..at] + redacted + tail;
    }
}

/// <summary>
/// Stamps <c>reelbolt.org_id</c> on spans. Tried at start (background work already inside an
/// organization scope) and again at end (an ASP.NET Core span starts before the JWT is validated,
/// so the organization is only known by the time the request finishes). An explicit tag set by the
/// caller is never overwritten.
/// </summary>
public sealed class OrganizationSpanProcessor(Func<Guid?> organization) : BaseProcessor<Activity>
{
    public override void OnStart(Activity data) => Stamp(data);

    public override void OnEnd(Activity data) => Stamp(data);

    private void Stamp(Activity activity)
    {
        if (activity.GetTagItem(ReelBoltTelemetry.OrgIdTag) is not null) return;
        Guid? org;
        try { org = organization(); }
        catch { return; } // telemetry must never break the work it observes
        if (org is { } id && id != Guid.Empty)
            activity.SetTag(ReelBoltTelemetry.OrgIdTag, id.ToString());
    }
}
