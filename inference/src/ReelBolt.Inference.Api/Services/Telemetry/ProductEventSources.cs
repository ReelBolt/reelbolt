namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// The <c>product_events.source</c> vocabulary, spelled in exactly one place. The database holds the
/// same three values in a <c>CHECK</c> constraint (<c>ck_product_events_source</c>); this type is what
/// the C# emitters write, so a typo is a compile-time miss rather than a row the reader cannot
/// classify.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Server"/> — a backend observation.</b> Written by a service that saw the transition
/// happen. It exists for every user the transition happened to, and consent cannot erase it: the
/// transition is a fact of the system, not a fact about the browser.
/// </para>
/// <para>
/// <b><see cref="Client"/> — a browser observation.</b> Written only when the person has granted
/// telemetry consent, so a missing client row is unmeasurable until a <see cref="Consent"/> row says
/// otherwise.
/// </para>
/// <para>
/// <b><see cref="Consent"/> — the person's telemetry choice.</b> Written whenever the person has made
/// a choice, <em>granted or declined</em>, whether or not consent was given. This is the one
/// transmission a declining browser still makes, because recording the choice is how the choice is
/// honoured: without it a decliner is indistinguishable from a drop-off.
/// </para>
/// </remarks>
public static class ProductEventSources
{
    /// <summary>A backend service observed the transition.</summary>
    public const string Server = "server";

    /// <summary>The browser observed the event, under granted telemetry consent.</summary>
    public const string Client = "client";

    /// <summary>The browser delivered the person's telemetry choice, granted or declined.</summary>
    public const string Consent = "consent";
}
