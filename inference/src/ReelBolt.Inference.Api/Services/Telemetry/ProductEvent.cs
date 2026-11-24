namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// One row of the product funnel (initiative <c>product-events-funnel</c>): a single observation of a
/// named event, at a time, from a named source, about a person, an organization, or both.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only.</b> The table has no <c>UPDATE</c> and no <c>DELETE</c> path and no state machine:
/// a later decision is a later row, and the reader takes the latest. Nothing in the codebase mutates
/// one of these rows.
/// </para>
/// <para>
/// <b>No PII.</b> Ids, a closed event name, a timestamp and a <see cref="Source"/>. Never an email, a
/// display name, an IP, a user agent, free text or any request payload — the columns that exist are
/// the whole vocabulary, and there is nowhere else for a payload to hide.
/// </para>
/// <para>
/// <b>No tenant query filter.</b> The row is keyed by person, not by organization, and is not
/// addressable by a caller-chosen id through any route — exactly the rule that keeps
/// <see cref="AlphaFunnelClaim"/> unfiltered. It is registered in <c>TenantBoundaryInventoryTests</c>'
/// unfiltered-child theory by convention.
/// </para>
/// <para>
/// <b>Either subject, never neither.</b> <see cref="UserId"/> and <see cref="OrganizationId"/> are both
/// nullable and a <c>CHECK</c> requires at least one: a payment belongs to an organization and may have
/// no person, a client event belongs to a person and may have no organization. Inventing a
/// <c>NOT NULL</c> on either would force a lie.
/// </para>
/// </remarks>
public class ProductEvent
{
    /// <summary>The surrogate key. Identity, so the writer never supplies it.</summary>
    public long Id { get; set; }

    /// <summary>The person the event is about, when the transition is person-scoped.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The organization the event is about, when the transition is organization-scoped.</summary>
    public Guid? OrganizationId { get; set; }

    /// <summary>
    /// The event name, one of the funnel vocabulary in docs/observability.md. Bounded to 64 characters,
    /// so a new <em>name</em> costs no migration — only a new <see cref="Source"/> does.
    /// </summary>
    public string Event { get; set; } = string.Empty;

    /// <summary>When the event happened, as the emitter observed it.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// Who observed the event — one of <see cref="ProductEventSources"/>. This is the column that makes
    /// a deliberate absence distinguishable from a real drop-off, so its <c>CHECK</c> is the
    /// schema-level statement of the vocabulary: a fifth value needs a migration on purpose.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// A closed, sanitized token qualifying the event (a template key, or the acquisition source label of
    /// a visit). Never free text, never a request payload. Bounded to 64 characters.
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// The anonymous visitor id (random UUID v4) of a pre-account visit or signup-started row. Such a
    /// row carries no user and no organization.
    /// </summary>
    public Guid? VisitorId { get; set; }
}
