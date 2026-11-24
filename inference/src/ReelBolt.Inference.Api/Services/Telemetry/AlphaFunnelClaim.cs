namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// One claimed alpha funnel event (alpha-launch T-018). The primary key is (UserId, Event): a person
/// has at most one row per event, and an insert that conflicts is how the server knows the event was
/// already counted.
/// </summary>
/// <remarks>
/// This is the durable half of the funnel's "once per user". The browser's marker stops a double
/// send from one device; this stops a caller who ignores it — an authenticated caller cannot drive
/// the counter without bound, because the counter only moves when a row is newly claimed. No tenant
/// filter: the row is keyed by person, not by organization.
/// </remarks>
public class AlphaFunnelClaim
{
    /// <summary>The person the event was claimed for.</summary>
    public Guid UserId { get; set; }

    /// <summary>The funnel event name, one of <see cref="AlphaFunnelDiagnostics.Events"/>.</summary>
    public string Event { get; set; } = string.Empty;

    /// <summary>When the claim was first written.</summary>
    public DateTimeOffset ClaimedAt { get; set; }
}
