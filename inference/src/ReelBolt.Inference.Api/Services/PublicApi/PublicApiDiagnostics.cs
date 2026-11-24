using System.Diagnostics.Metrics;

namespace ReelBolt.Inference.Api.Services.PublicApi;

/// <summary>Metrics of the public API's shared plumbing (audit shedding, rate-limit degradation).</summary>
public static class PublicApiDiagnostics
{
    /// <summary>Meter name; the Inference API's telemetry registration adds it with <c>AddMeter</c>.</summary>
    public const string MeterName = "ReelBolt.Inference.PublicApi";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// One per audit event the bounded channel could not accept. Audit never blocks a request, so a full
    /// channel sheds rows instead; an increasing counter means the writer is falling behind.
    /// </summary>
    public static readonly Counter<long> AuditDropped =
        Meter.CreateCounter<long>("reelbolt.public_api.audit_dropped", "events", "API-key audit events shed by the full bounded channel");

    /// <summary>One per request where the rate counter's database failed and the request was let through.</summary>
    public static readonly Counter<long> RateLimitFailOpen =
        Meter.CreateCounter<long>("reelbolt.public_api.rate_limit_fail_open", "requests", "Public API requests admitted because the rate counter database failed");

    /// <summary>One per request refused by the per-key rate limit, tagged <c>limit</c> = requests | writes.</summary>
    public static readonly Counter<long> RateLimitRefused =
        Meter.CreateCounter<long>("reelbolt.public_api.rate_limit_refused", "requests", "Public API requests refused by the per-key fixed-window limit");
}
