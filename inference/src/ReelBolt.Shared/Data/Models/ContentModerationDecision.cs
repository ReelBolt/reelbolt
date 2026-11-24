namespace ReelBolt.Shared.Data.Models;

/// <summary>Where a review-only <see cref="ContentModerationDecision"/> stands in the admin queue.</summary>
public enum ModerationReviewState
{
    /// <summary>Allowed/blocked without a human review (the normal case).</summary>
    NotRequired,

    /// <summary>The mid band: the action proceeded, but a reviewer may look at the audit row.</summary>
    Pending,

    /// <summary>A reviewer acknowledged the row. Acknowledgement changes no user-visible state.</summary>
    Acknowledged
}

/// <summary>
/// One moderation decision, appended for audit. <b>Carries no raw content:</b> only category
/// flags/scores, input types, the action, the model/snapshot, the decision id and a SHA-256 of the
/// content. Original media never leaves the host and is never stored here.
/// </summary>
/// <remarks>
/// The Inference API owns the table (Task 2 maps it and generates the migration); the WorkflowEngine
/// maps it read-only with <c>ExcludeFromMigrations()</c>. This ticket creates the shared model only —
/// no DbContext is modified here. Enum-shaped fields are stored as strings so an append-only enum
/// change cannot silently reinterpret a stored row. <see cref="ExpiresAt"/> drives retention.
/// </remarks>
public class ContentModerationDecision
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ExecutionId { get; set; }
    public Guid? StepResultId { get; set; }

    /// <summary>
    /// The identity of the object this verdict was made about: the storage ETag plus size, or
    /// <c>len:{size}</c> when the store reports no ETag. A storage key is not an identity — a retry can
    /// rewrite the same execution-scoped key — so a cached verdict is reused only when the current
    /// object's version equals this value. Null on legacy rows, which are therefore cache misses.
    /// </summary>
    public string? SourceVersion { get; set; }

    /// <summary><see cref="Moderation.ModerationSurface"/> name (GenerationPrompt, Ingest, ...).</summary>
    public string Surface { get; set; } = string.Empty;

    /// <summary><see cref="Moderation.ModerationAction"/> name (Allow, Block, Review).</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary><see cref="Moderation.ModerationContentKind"/> name (Text, Image, Video, Audio).</summary>
    public string ContentKind { get; set; } = string.Empty;

    /// <summary>Hex SHA-256 of the classified content. The audit row stores this instead of the content.</summary>
    public string ContentSha256 { get; set; } = string.Empty;

    /// <summary>Length in bytes of the classified content (text length or sampled media size).</summary>
    public long ContentLength { get; set; }

    public Guid? ProviderId { get; set; }
    public Guid? AccountId { get; set; }

    /// <summary><see cref="InferenceProviderKind"/> name that served the call.</summary>
    public string ProviderKind { get; set; } = string.Empty;

    /// <summary>The model code requested (the provider row's model).</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>The pinned snapshot actually sent (e.g. <c>omni-moderation-2024-09-26</c>).</summary>
    public string ModelSnapshot { get; set; } = string.Empty;

    public string? TopCategory { get; set; }
    public double? TopScore { get; set; }

    /// <summary>jsonb: category → boolean, as returned by the model.</summary>
    public string FlaggedCategoriesJson { get; set; } = "{}";

    /// <summary>jsonb: category → maximum score across the request's inputs.</summary>
    public string ScoresJson { get; set; } = "{}";

    /// <summary>jsonb: the applied input types the model reported (text, image).</summary>
    public string InputTypesJson { get; set; } = "[]";

    /// <summary>The top category when a verdict was returned; null for Allow.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>The plain, score-free sentence shown to the user; null for Allow.</summary>
    public string? ReasonMessage { get; set; }

    /// <summary><see cref="ModerationReviewState"/> name (NotRequired, Pending, Acknowledged).</summary>
    public string ReviewState { get; set; } = nameof(ModerationReviewState.NotRequired);

    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When retention deletes this row (CreatedAt + the configured window).</summary>
    public DateTime ExpiresAt { get; set; }
}
