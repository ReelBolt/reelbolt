using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Observability;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// First-party ingestion for browser telemetry (alpha-launch T-018; extended by product-events-funnel
/// T-002): the alpha funnel events, the telemetry consent decision and client error reports. All are
/// opt-in on the device and gated by the app-side consent surface, and all answer <c>404</c> until
/// <c>Telemetry:ClientIngestEnabled</c> is true, so a deployment that has not opted in behaves exactly
/// as it did before.
/// </summary>
/// <remarks>
/// <para>
/// An accepted funnel event now persists a <c>product_events</c> row (<c>source = 'client'</c>) in the
/// same statement that claims it, and moves the OTLP counter only when that claim is new. A consent
/// decision persists a row too (<c>source = 'consent'</c>) — granted or declined — and moves no
/// counter. A client error report still becomes only an OTLP counter and a span, never a row.
/// </para>
/// <para>
/// Nothing recorded here is PII: ids, a closed event name, a timestamp and a <c>source</c>. The user id
/// is taken from the authenticated caller and the organization id from
/// <see cref="ICurrentUser.OrganizationId"/>; the request body can carry neither.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/telemetry")]
public class TelemetryController(
    ICurrentUser currentUser,
    IOptions<AlphaTelemetryOptions> options,
    IConfiguration configuration,
    IProductEventLog productEvents) : ControllerBase
{
    /// <summary>
    /// The most events one request may carry. There are only three browser funnel events and two
    /// consent decisions, so this is generous and still bounded: the endpoint can never be used to
    /// drive unbounded work.
    /// </summary>
    public const int MaxEventsPerBatch = 16;

    private bool Disabled => !options.Value.ClientIngestEnabled;

    private string Environment => FirstNonBlank(
        configuration["OTEL_DEPLOYMENT_ENVIRONMENT"],
        configuration["ASPNETCORE_ENVIRONMENT"],
        configuration["DOTNET_ENVIRONMENT"]) ?? "Production";

    /// <summary>
    /// Records the funnel events and consent decisions the browser reports since the last flush.
    /// </summary>
    [HttpPost("events")]
    [Authorize]
    public async Task<IActionResult> Events([FromBody] ClientEventsDto body, CancellationToken ct)
    {
        if (Disabled || !currentUser.IsAuthenticated) return NotFound();

        var events = body.Events ?? [];
        if (events.Count > MaxEventsPerBatch) return BadRequest(new { error = "batch_too_large" });
        // Validate the whole batch first: a batch carrying an unknown name writes nothing at all, so a
        // caller can never half-write a batch or mint a metric series by inventing an event name.
        foreach (ClientFunnelEventDto entry in events)
        {
            if (ProductEventNames.IsUpgradeEvent(entry.Name))
            {
                if (!ProductEventNames.IsUpgradeSurface(entry.Detail)) return BadRequest(new { error = "invalid_detail" });
                continue;
            }

            if (!AlphaFunnelDiagnostics.IsClientEvent(entry.Name) && !AlphaFunnelDiagnostics.IsConsentEvent(entry.Name))
                return BadRequest(new { error = "unknown_event" });
            bool isClient = AlphaFunnelDiagnostics.IsClientEvent(entry.Name);
            if (isClient ? !AlphaFunnelDiagnostics.IsValidClientDetail(entry.Name, entry.Detail) : entry.Detail is not null)
                return BadRequest(new { error = "invalid_detail" });
        }

        Guid? organizationId = currentUser.OrganizationId;
        foreach (ClientFunnelEventDto entry in events)
        {
            // A consent decision is recorded whether granted or declined, and moves no counter: the row
            // IS the point — it is how a decline becomes a stated fact rather than a silent zero.
            //
            // organization_id is deliberately null here, unlike the client branch below: the choice is
            // about the person's own device, not an organizational fact, and the org id is one
            // identifier more than the distinction needs. product_events.organization_id is nullable
            // for exactly this kind of person-only row.
            if (AlphaFunnelDiagnostics.IsConsentEvent(entry.Name))
            {
                await productEvents.AppendAsync(
                    new ProductEvent
                    {
                        UserId = currentUser.UserId,
                        OrganizationId = null,
                        Event = entry.Name!,
                        OccurredAt = DateTimeOffset.UtcNow,
                        Source = ProductEventSources.Consent,
                    },
                    ct);
                continue;
            }

            // An upgrade event is one row per user per surface (the claim key carries the surface), source client.
            if (ProductEventNames.IsUpgradeEvent(entry.Name))
            {
                await productEvents.ClaimAndAppendWithDetailAsync(
                    currentUser.UserId, organizationId, entry.Name!, $"{entry.Name}:{entry.Detail}", entry.Detail,
                    ProductEventSources.Client, ct);
                continue;
            }

            // A client funnel event claims and appends in one statement, so a claim and its row can
            // never diverge. The counter moves only when the claim is NEW, so the browser's own
            // "once per user" is a convenience and not the guarantee: a caller that ignores it still
            // counts each event at most once.
            // Once per user per (event, detail): the claim key carries the detail token.
            bool claimed = entry.Detail is null
                ? await productEvents.ClaimAndAppendAsync(
                    currentUser.UserId, organizationId, entry.Name!, ProductEventSources.Client, ct)
                : await productEvents.ClaimAndAppendWithDetailAsync(
                    currentUser.UserId, organizationId, entry.Name!, $"{entry.Name}:{entry.Detail}", entry.Detail,
                    ProductEventSources.Client, ct);
            if (!claimed)
            {
                continue;
            }

            AlphaFunnelDiagnostics.FunnelEvents.Add(
                1,
                new KeyValuePair<string, object?>("event", entry.Name));
        }

        return NoContent();
    }

    /// <summary>Records one browser error report.</summary>
    [HttpPost("errors")]
    [Authorize]
    public IActionResult Errors([FromBody] ClientErrorDto body)
    {
        if (Disabled || !currentUser.IsAuthenticated) return NotFound();

        ErrorTrackingDiagnostics.RecordClient(body.Name, body.Route, Environment);

        return NoContent();
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
