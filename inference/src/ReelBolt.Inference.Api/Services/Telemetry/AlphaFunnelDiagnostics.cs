using System.Diagnostics.Metrics;

namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// The alpha funnel (alpha-launch T-018; extended by product-events-funnel T-002): the first-touch
/// events the alpha measures, the browser funnel events the ingest accepts, the consent decisions the
/// ingest records, and the client error reports — as counters on the watched OTLP destination
/// (docs/observability.md). The <c>event</c> and <c>kind</c> tags are closed sets — anything else is
/// refused by the controller, so no caller can mint a metric series.
/// </summary>
public static class AlphaFunnelDiagnostics
{
    /// <summary>Meter name; added to the exported meters in <c>AddInferenceApiServices</c>.</summary>
    public const string MeterName = "ReelBolt.Alpha.Funnel";

    /// <summary>
    /// The whole funnel vocabulary, in order: the steps the funnel is read as, and the steps the OTLP
    /// counter is tagged with. Every name here is observed exactly once per user on the real
    /// transition — <c>alpha.signup</c> by the Go API, the rest by the browser (see
    /// <see cref="ClientEventNames"/>). Documentation of record, not a validation list: the ingest
    /// validates against <see cref="ClientEventNames"/> and <see cref="ConsentEventNames"/>, and the
    /// two server-side steps the browser cannot observe are recorded by the services that see them.
    /// </summary>
    public static readonly string[] Events =
        ["alpha.signup", "alpha.first_project", "alpha.first_run", "alpha.first_export_initiated"];

    /// <summary>
    /// The funnel events the browser emits, and therefore the only funnel names the ingest accepts as a
    /// <c>client</c> observation. <c>alpha.signup</c> is deliberately absent: the signup endpoint answers
    /// a uniform <c>202</c> so the browser cannot observe it, and the Go API records it server-side.
    /// </summary>
    public static readonly string[] ClientEventNames =
        [
            "alpha.first_project", "alpha.first_run", "alpha.first_export_initiated",
            RunPageOpened, RunClicked, RunBlocked,
        ];

    /// <summary>The run page was opened (first-run help funnel). Carries no detail.</summary>
    public const string RunPageOpened = "alpha.run_page_opened";

    /// <summary>The Run control was clicked. Carries no detail.</summary>
    public const string RunClicked = "alpha.run_clicked";

    /// <summary>A run was blocked client-side or refused; the only event that carries a <c>detail</c>.</summary>
    public const string RunBlocked = "alpha.run_blocked";

    /// <summary>The closed reason tokens <see cref="RunBlocked"/> accepts as its <c>detail</c>.</summary>
    public static readonly string[] RunBlockedDetails =
        ["readiness", "notes_required", "plan", "credits", "estimate_gate", "rate_limited", "source", "other"];

    /// <summary>True when <paramref name="detail"/> is a valid detail for the client event <paramref name="name"/>.</summary>
    public static bool IsValidClientDetail(string? name, string? detail)
        => name == RunBlocked
            ? detail is not null && Array.IndexOf(RunBlockedDetails, detail) >= 0
            : detail is null;

    /// <summary>The name the browser sends when the person grants telemetry consent.</summary>
    public const string ConsentGranted = "telemetry.consent.granted";

    /// <summary>The name the browser sends when the person declines telemetry consent.</summary>
    public const string ConsentDeclined = "telemetry.consent.declined";

    /// <summary>
    /// The consent-decision names the ingest accepts. A row is written for either one — the choice is
    /// recorded whether consent was granted or declined, and moves no counter.
    /// </summary>
    public static readonly string[] ConsentEventNames = [ConsentGranted, ConsentDeclined];

    /// <summary>Client error reports from the browser, tagged <c>kind = client</c>.</summary>
    public const string ClientErrorKind = "client";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Alpha funnel events accepted from the browser, one per event, tagged <c>event</c>.</summary>
    public static readonly Counter<long> FunnelEvents =
        Meter.CreateCounter<long>("reelbolt.alpha.funnel", "events", "Alpha funnel events, tagged with the event name");

    /// <summary>True when <paramref name="name"/> is a funnel event the browser may report.</summary>
    public static bool IsClientEvent(string? name) => name is not null && Array.IndexOf(ClientEventNames, name) >= 0;

    /// <summary>True when <paramref name="name"/> is a telemetry consent decision.</summary>
    public static bool IsConsentEvent(string? name) => name is not null && Array.IndexOf(ConsentEventNames, name) >= 0;
}
