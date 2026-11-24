namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One "find this object and bind an effect to it" request from the editor or the assistant. Its work is a hidden
/// <see cref="WorkflowDefinitionKind.ObjectAnalysis"/> run (<see cref="ExecutionId"/>); the row keeps the intent
/// (prompt, effect, strength, timeline items) so a server-side finalizer can apply it when the run succeeds.
///
/// <para>
/// Owned by the Inference API (migration <c>AddObjectEffects</c>). <see cref="ExecutionId"/> is a plain Guid, no
/// foreign key: the executions table is engine-owned. No tenant query filter: a project child reached only through
/// a project-authorized route (<c>TenantBoundaryInventoryTests</c>); <see cref="OrganizationId"/> is denormalized,
/// never a guard.
/// </para>
/// </summary>
public class ObjectEffectRequest
{
    public const string SourceFile = "file";
    public const string SourceStepResult = "step_result";

    public const string StatusQueued = "queued";
    public const string StatusSucceeded = "succeeded";
    public const string StatusFailed = "failed";
    public const string StatusApplied = "applied";
    public const string StatusApplyFailed = "apply_failed";

    /// <summary>The timeline changed after the request was made; nothing was written and the editor offers apply or discard.</summary>
    public const string StatusNeedsReview = "needs_review";

    /// <summary>The user chose not to apply a finished result; its analysis stays reusable.</summary>
    public const string StatusDiscarded = "discarded";

    public const int MaxPromptLength = 80;

    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>The hidden analysis run. No foreign key (engine-owned table).</summary>
    public Guid ExecutionId { get; set; }

    /// <summary><see cref="SourceFile"/> or <see cref="SourceStepResult"/>.</summary>
    public string SourceKind { get; set; } = SourceFile;

    /// <summary>The project file id or the step result id the video comes from, as text.</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>The object, in the user's words (at most <see cref="MaxPromptLength"/> characters).</summary>
    public string Prompt { get; set; } = string.Empty;

    public string? MatchScope { get; set; }

    /// <summary>The effect word: Blur, Pixelate, Highlight, Spotlight, ColorPop or Outline.</summary>
    public string Effect { get; set; } = string.Empty;

    public double Strength { get; set; }
    public bool Invert { get; set; }
    public int FeatherPx { get; set; }

    /// <summary>The timeline the effect is to be applied to by the finalizer, when requested through the assistant.</summary>
    public Guid? TimelineId { get; set; }

    /// <summary>JSON array of timeline item ids the effect applies to (apply-to-timeline intent).</summary>
    public string? TimelineItemIdsJson { get; set; }

    /// <summary>queued | succeeded | failed | applied | apply_failed | needs_review | discarded.</summary>
    public string Status { get; set; } = StatusQueued;

    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FinalizedAt { get; set; }

    /// <summary>The timeline revision the finalizer wrote the effect at.</summary>
    public long? AppliedRevision { get; set; }
}
