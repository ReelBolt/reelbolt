namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>One event from the browser (alpha-launch T-018): a funnel step or a consent decision.</summary>
public sealed class ClientFunnelEventDto
{
    /// <summary>
    /// A client funnel name (<c>AlphaFunnelDiagnostics.ClientEventNames</c>) or a consent-decision name
    /// (<c>AlphaFunnelDiagnostics.ConsentEventNames</c>). Any other value is refused with 400, and the
    /// whole batch with it. There is no field for a payload: the name is the entire vocabulary.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// A closed token, required on the two upgrade events (<c>ProductEventNames.UpgradeSurfaces</c>: the surface)
    /// and on <c>alpha.run_blocked</c> (<c>AlphaFunnelDiagnostics.RunBlockedDetails</c>: the reason). Any other
    /// event carrying a detail is refused.
    /// </summary>
    public string? Detail { get; set; }
}

/// <summary>The batch the web client posts to <c>POST /api/v1/telemetry/events</c>.</summary>
public sealed class ClientEventsDto
{
    /// <summary>
    /// The events observed since the last flush. A funnel event appears at most once per user; a
    /// consent decision may repeat, because a later choice is a later row.
    /// </summary>
    public List<ClientFunnelEventDto>? Events { get; set; }
}

/// <summary>
/// A browser error report (alpha-launch T-018). Exactly two fields, because those are the only two
/// the server keeps: the error's class name and the route it happened on. A client's message and
/// stack are deliberately absent — they can carry user content, and the server does not rely on a
/// client's own redaction. Any extra JSON field a caller sends is ignored by model binding.
/// </summary>
public sealed class ClientErrorDto
{
    /// <summary>The error's class name, e.g. <c>TypeError</c>. Bounded and recorded as the error type.</summary>
    public string? Name { get; set; }

    /// <summary>The route the error happened on. Redacted server-side before it is recorded.</summary>
    public string? Route { get; set; }
}

/// <summary>
/// The body of <c>POST /api/v1/telemetry/visit</c> (docs/design/new-user-funnel.md sections 4 and 6).
/// <see cref="Event"/> and <see cref="VisitorId"/> are validated, and so is <see cref="Detail"/> on the two
/// events that carry one of their own. The attribution fields are sanitized and dropped when malformed,
/// never refused: they are the visitor's first touch, stored once per visitor in <c>visitor_attribution</c>.
/// </summary>
public sealed class VisitEventDto
{
    /// <summary><c>site.visit</c>, <c>site.page_view</c>, <c>site.cta_click</c>, <c>signup.started</c> or <c>signup.submitted</c>.</summary>
    public string? Event { get; set; }

    /// <summary>Random UUID v4 identifying the browser.</summary>
    public string? VisitorId { get; set; }

    /// <summary>utm_source.</summary>
    public string? Source { get; set; }

    /// <summary>utm_medium.</summary>
    public string? Medium { get; set; }

    /// <summary>utm_campaign.</summary>
    public string? Campaign { get; set; }

    /// <summary>utm_content. On a tracked link this is the link code.</summary>
    public string? Content { get; set; }

    /// <summary>utm_term.</summary>
    public string? Term { get; set; }

    /// <summary>Landing path (query and fragment are stripped).</summary>
    public string? LandingPath { get; set; }

    /// <summary>Referrer hostname.</summary>
    public string? ReferrerHost { get; set; }

    /// <summary>
    /// The page key on <c>site.page_view</c> and the CTA location on <c>site.cta_click</c>: required there,
    /// one closed token. Ignored on the other three events, whose stored detail the server decides (the
    /// source label on <c>site.visit</c> and <c>signup.started</c>, nothing on <c>signup.submitted</c>).
    /// </summary>
    public string? Detail { get; set; }
}
