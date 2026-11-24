namespace ReelBolt.Inference.Api.Observability;

/// <summary>
/// The four product-health conditions the ambient detectors recognise (T-020). A closed set: a value
/// outside it is a bug in a detector, never something an operator asked for.
/// </summary>
public static class ProductHealthSignalKinds
{
    /// <summary>A tool call whose own result reports failure — a <c>{"ok": false}</c> payload.</summary>
    public const string ToolCallFailed = "tool_call_failed";

    /// <summary>A request the API refused with 402, 409 or 503.</summary>
    public const string RequestRefused = "request_refused";

    /// <summary>A workflow step that ran and failed (its persisted result says <c>Failed</c>).</summary>
    public const string WorkflowStepFailed = "workflow_step_failed";

    /// <summary>An assistant turn that ended on an error without producing a reply.</summary>
    public const string ConversationAbandonedAfterError = "conversation_abandoned_after_error";
}

/// <summary>
/// One ambient product-health observation, as the pinned T-020 interface
/// <c>{kind, source, occurredAt, orgId, userId, projectId?, executionId?, stepOrder?, code?}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Metadata only.</b> There is deliberately no message, prompt, tool argument or model-output field,
/// and no detector may add one: nothing that could carry user prose belongs here. The only free-ish text
/// is <see cref="Code"/>, which is a closed-set machine code the caller passes from its own refusal
/// constants — never text the user or the model produced.
/// </para>
/// <para>
/// <b>Reporting only.</b> A signal is emitted through <see cref="IProductHealthSignalSink"/> and nothing
/// reads it back into a decision. A detector that could change a response would be a defect, not a
/// feature (plan Global Constraints, "the ambient health signals are reporting only").
/// </para>
/// </remarks>
public sealed record ProductHealthSignal
{
    /// <summary>One of <see cref="ProductHealthSignalKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Where the condition was observed, e.g. <c>assistant.tool_result</c>; a stable, low-cardinality word.</summary>
    public required string Source { get; init; }

    /// <summary>When the detector observed the condition (UTC).</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The organization the observation belongs to; <see cref="Guid.Empty"/> when the seam cannot name one.</summary>
    public required Guid OrgId { get; init; }

    /// <summary>The acting user; <see cref="Guid.Empty"/> when the seam cannot name one.</summary>
    public required Guid UserId { get; init; }

    /// <summary>The project in context, when there is one.</summary>
    public Guid? ProjectId { get; init; }

    /// <summary>The workflow execution in context, when there is one.</summary>
    public Guid? ExecutionId { get; init; }

    /// <summary>The workflow step's order, when the observation is about one step.</summary>
    public int? StepOrder { get; init; }

    /// <summary>A closed-set machine code from the observing seam (e.g. a refusal's own code); never prose.</summary>
    public string? Code { get; init; }
}
