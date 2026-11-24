namespace ReelBolt.Shared.Data.Models;

/// <summary>Which subsystem produced a <see cref="UsageEvent"/>. Append-only, stored as the member name.</summary>
public enum UsageSource
{
    Workflow,
    Assistant,
    Indexing,
    DocsIndex,
    Storage,

    /// <summary>A metered call that ran outside any <c>UsageScope</c>; attributed to the platform org so it stays visible.</summary>
    Unattributed,

    /// <summary>
    /// The picture editor's AI tools (<c>POST .../image-documents/{id}/operations</c> and
    /// <c>.../subjects</c>), attributed to the project's organization and the user who ran them.
    /// Appended: stored as the member name.
    /// </summary>
    Editor,

    /// <summary>
    /// An assistant tool run through the MCP bridge (<c>POST /api/v1/assistant/tool-call</c>), attributed to the
    /// caller's active organization, the page's project when the caller may view it, and the caller. Appended.
    /// </summary>
    Mcp,

    /// <summary>
    /// The background file summarizer, attributed to the file's organization and project, with no user. Appended.
    /// </summary>
    FileSummary,

    /// <summary>
    /// A timeline AI-assist proposal (<c>POST .../edit-timelines/{id}/proposals</c>), attributed to the project's
    /// organization, the project and the caller. Appended.
    /// </summary>
    TimelineAssist,

    /// <summary>
    /// A content-moderation call (generation prompt, ingest, generated text or publish), attributed
    /// to the organization, project and user, with the execution/step when one exists. Appended.
    /// </summary>
    Moderation,

    /// <summary>
    /// The text written for a finished video's social posts (<c>POST .../outputs/{id}/post-texts</c>), attributed to the
    /// project's organization, the project and the caller. Appended.
    /// </summary>
    PostText
}

/// <summary>
/// Who pays the provider for a <see cref="UsageEvent"/>. Append-only, stored as the member name.
/// <c>Platform</c>: a platform-managed provider; <c>Byo</c>: the organization's own provider;
/// <c>Runner</c>: compute executed on the organization's own runner.
/// </summary>
public enum UsageBillingMode
{
    Platform,
    Byo,
    Runner
}

/// <summary>
/// One billable unit with org, project, execution, provider and model attribution and the credit
/// amount priced at write time (decisions D8/D10). Table <c>usage_events</c>, owned (migrated) by
/// the Inference API; the WorkflowEngine maps it read-only (<c>ExcludeFromMigrations()</c>) and its
/// metering recorder (B3b) writes rows through that mapping, so the schema must change only here.
/// Append-only: rows are never updated; a correction is a new compensating row.
/// </summary>
public class UsageEvent
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    // The attribution ids below are plain columns, not foreign keys, so ledger rows outlive
    // the execution/step/project they describe (same rationale as DecisionObservation).
    public Guid? ProjectId { get; set; }
    public Guid? ExecutionId { get; set; }
    public Guid? StepResultId { get; set; }
    public Guid? UserId { get; set; }

    public UsageSource Source { get; set; }
    public UsageKind Kind { get; set; }

    /// <summary>Amount in the unit of <see cref="Kind"/> (tokens, seconds, bytes, GB-days).</summary>
    public decimal Quantity { get; set; }

    /// <summary>The model capability that served the call (named a provider row before the model catalog; migrated ids are unchanged).</summary>
    public Guid? ProviderId { get; set; }
    public InferenceProviderKind ProviderKind { get; set; }
    public string ModelName { get; set; } = string.Empty;

    public UsageBillingMode BillingMode { get; set; } = UsageBillingMode.Platform;

    /// <summary>Credits charged, in integer millicredits (D8), priced at write time.</summary>
    public long CreditsMillis { get; set; }

    /// <summary>Our provider cost (COGS) in USD; zero when unknown or not ours (BYO).</summary>
    public decimal CostUsd { get; set; }

    /// <summary>
    /// History only: the rate card row that priced an event recorded before the model catalog. Never written again;
    /// <see cref="PriceId"/> replaced it.
    /// </summary>
    public Guid? RateCardId { get; set; }

    /// <summary>Soft reference to the <c>model_capability_prices</c> row that priced this event; null when none did.</summary>
    public Guid? PriceId { get; set; }

    /// <summary>The price variant used (resolution, image tier, compute tier); null for an unvaried unit.</summary>
    public string? Variant { get; set; }

    /// <summary>
    /// True when this was a PLATFORM-billed unit whose price lookup failed at metering time (no effective price row, or
    /// the lookup threw): the row keeps its quantity and model, <see cref="CreditsMillis"/> stays 0, and every settle
    /// path that consumes it charges at least the operation's hold instead (docs/metering-and-billing.md, "Unpriced
    /// models fail closed"). Never set for BYO or Runner units. Column <c>unpriced</c>, default false.
    /// </summary>
    public bool Unpriced { get; set; }

    /// <summary>Unique; makes a retried write a no-op (e.g. <c>stepResultId:callSeq</c>).</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
