namespace ReelBolt.Shared.IntegrationEvents;

/// <summary>
/// Published by Inference API when user triggers workflow execution.
/// </summary>
public record WorkflowExecutionRequested
{
    public Guid ExecutionId { get; init; }
    public Guid WorkflowDefinitionId { get; init; }
    public Guid ProjectId { get; init; }

    /// <summary>
    /// The organization that owns the run. Additive: <see cref="Guid.Empty"/> on a message published
    /// before tenancy, in which case consumers read it from the execution row.
    /// </summary>
    public Guid OrganizationId { get; init; }
    public Guid InitiatedByUserId { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
    /// <summary>
    /// Optional free-text user request passed as context to all agents.
    /// Null when the workflow was executed without user input.
    /// </summary>
    public string? UserRequest { get; init; }

    /// <summary>
    /// When true, <c>VideoGenerate</c> steps pay for fresh clips instead of reusing identical,
    /// already-delivered ones. Default false: reuse.
    /// </summary>
    public bool RegenerateVideoClips { get; init; }

    /// <summary>
    /// The run's editing style snapshot (<c>RunStyleSnapshot</c> JSON), mirrored from
    /// <c>WorkflowExecution.StyleConfigJson</c> for consumers of this event. Informational: the
    /// engine reads the execution row, which is authoritative. Null when no style was chosen.
    /// </summary>
    public string? StyleConfigJson { get; init; }

    /// <summary>
    /// The per-run design-system pin (<c>DesignSystemPin</c> JSON, SPEC 3.5), mirrored from
    /// <c>WorkflowExecution.DesignSystemPinJson</c> for consumers of this event. Informational: the
    /// engine reads the execution row, which is authoritative. Null when nothing was approved.
    /// </summary>
    public string? DesignSystemPinJson { get; init; }

    /// <summary>
    /// The run's output-format override by enum name (<c>RunOutputFormat</c>), mirrored from
    /// <c>WorkflowExecution.OutputFormatOverride</c>. Null when the run keeps each compile step's own format.
    /// </summary>
    public string? OutputFormat { get; init; }

    /// <summary>
    /// The run's chosen output resolution (short side in pixels), mirrored from
    /// <c>WorkflowExecution.OutputResolution</c>. Informational like the two above: the engine reads the
    /// row. Null when the run chose Auto.
    /// </summary>
    public int? OutputResolution { get; init; }

    /// <summary>
    /// The run's stored brief (<c>StoredRunBrief</c> JSON), mirrored from <c>WorkflowExecution.RunBriefJson</c>.
    /// Informational like the style and pin: the engine reads the row. Null when the run has no brief.
    /// </summary>
    public string? RunBriefJson { get; init; }

    /// <summary>
    /// The run-rate bucket ("paid" or "free") the assistant/MCP execute route admitted this run under, classified
    /// inside the rate-limit lock (T-012). The engine re-classifies the steps it loads before the first step: a run
    /// admitted "free" whose loaded steps are paid fails <c>RUN_CLASS_CHANGED</c> before anything runs. Null (human
    /// REST routes, retries, revisions, messages published before this member) means no check. Additive, last member.
    /// </summary>
    public string? AdmittedRunRateBucket { get; init; }
}

/// <summary>
/// Published by WorkflowEngine on successful completion.
/// </summary>
public record WorkflowExecutionCompleted
{
    public Guid ExecutionId { get; init; }
    public Guid ProjectId { get; init; }
    public Guid? WorkflowDefinitionId { get; init; }
    public Guid? InitiatedByUserId { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public string FinalStatus { get; init; } = string.Empty;
    public string? ResultJson { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Published by WorkflowEngine when an execution transitions to Running.
/// </summary>
public record WorkflowExecutionRunning
{
    public Guid ExecutionId { get; init; }
    public Guid ProjectId { get; init; }
    public Guid? WorkflowDefinitionId { get; init; }
    public Guid? InitiatedByUserId { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Event produced when a user (or admin) requests that a running or queued
/// workflow execution be aborted. The WorkflowEngine will handle cancellation
/// and update the execution status accordingly.
/// </summary>
public record WorkflowExecutionStopRequested
{
    /// <summary>
    /// The plain-words cause the engine records on the cancellation instead of blaming the person who
    /// happened to be signed in (a revoked key stops the work it started; the user did not press Stop).
    /// </summary>
    public const string ApiKeyRevokedReason = "API key revoked";

    public Guid ExecutionId { get; init; }
    public Guid RequestedByUserId { get; init; }

    /// <summary>Optional cause. Null means the ordinary "user pressed Stop" path; the engine words it as before.</summary>
    public string? Reason { get; init; }

    public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Published per step for real-time tracking.
/// </summary>
public record WorkflowStepCompleted
{
    public Guid ExecutionId { get; init; }
    public Guid StepId { get; init; }
    public Guid StepResultId { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? WorkflowDefinitionId { get; init; }
    public int? StepOrder { get; init; }
    public string? StepLabel { get; init; }
    public string? StepType { get; init; }
    public int? IterationNumber { get; init; }
    public string? AgentType { get; init; }
    public string? AgentName { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public string StepStatus { get; init; } = string.Empty;
    public int TokensUsed { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public Guid? PrimaryProviderId { get; init; }
    public string? PrimaryModelName { get; init; }
    public int? AttemptCount { get; init; }
    public int? RetryCount { get; init; }
    public long DurationMs { get; init; }
    public int ToolCallCount { get; init; }
    public int ReasoningCount { get; init; }
    public string? ErrorDetails { get; init; }
    public string? OutputStorageKey { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Published as a lightweight, ephemeral progress signal while a long-running step (initially
/// <c>VideoAnalyze</c>/<c>VideoCompile</c>, but any step executor may opt in) is still executing —
/// e.g. "Downloading source", "Transcribing audio (chunk 2/5)", "Encoding" with a percent. This is
/// transient UI signal, not execution history: unlike <see cref="WorkflowStepStarted"/>/
/// <see cref="WorkflowStepCompleted"/> it is never persisted to <c>WorkflowStepResult</c> and
/// carries no EF Core migration, so consumers must treat a later progress event for the same
/// <see cref="StepResultId"/> as superseding any earlier one and must never rely on delivery —
/// <see cref="WorkflowStepCompleted"/>/<see cref="WorkflowExecutionFailed"/> remain the only
/// authoritative signal that a step is actually done.
/// </summary>
public record WorkflowStepProgress
{
    public Guid ExecutionId { get; init; }
    public Guid StepId { get; init; }
    public Guid StepResultId { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? WorkflowDefinitionId { get; init; }
    public int? StepOrder { get; init; }
    public string? StepLabel { get; init; }
    public string? StepType { get; init; }
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>Short human-readable stage label, e.g. "Downloading source" or "Encoding".</summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>0-100 when a real percentage is available (e.g. ffmpeg encode progress); null otherwise — the frontend falls back to showing just the stage label.</summary>
    public int? PercentComplete { get; init; }

    /// <summary>
    /// Running cumulative token total observed so far for this step, when a step executor has
    /// genuine mid-run visibility into token usage (e.g. per-turn usage in a <c>StepType.EditRoom</c>
    /// group-chat run). Optional and additive — every pre-existing publisher of this event leaves
    /// this (and <see cref="InputTokensSoFar"/>/<see cref="OutputTokensSoFar"/>) null, and the
    /// frontend must treat null as "no token data for this stage", not as zero.
    /// </summary>
    public int? TokensUsedSoFar { get; init; }

    /// <summary>Running cumulative input/prompt token total so far, mirroring <see cref="TokensUsedSoFar"/>'s optionality.</summary>
    public int? InputTokensSoFar { get; init; }

    /// <summary>Running cumulative output/completion token total so far, mirroring <see cref="TokensUsedSoFar"/>'s optionality.</summary>
    public int? OutputTokensSoFar { get; init; }

    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Published when an agent invokes a tool while executing a workflow step.
/// </summary>
public record WorkflowStepToolCalled
{
    public Guid ExecutionId { get; init; }
    public Guid StepId { get; init; }
    public Guid StepResultId { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? WorkflowDefinitionId { get; init; }
    public int? StepOrder { get; init; }
    public string? StepLabel { get; init; }
    public string? AgentType { get; init; }
    public string? AgentName { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public int Sequence { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public string? ArgumentsPreview { get; init; }
    public string? ResultPreview { get; init; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Published when model reasoning text is available for a workflow step.
/// </summary>
public record WorkflowStepReasoningCaptured
{
    public Guid ExecutionId { get; init; }
    public Guid StepId { get; init; }
    public Guid StepResultId { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? WorkflowDefinitionId { get; init; }
    public int? StepOrder { get; init; }
    public string? StepLabel { get; init; }
    public string? AgentType { get; init; }
    public string? AgentName { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public int Sequence { get; init; }
    public string Content { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Published once per completed turn in a <c>StepType.EditRoom</c> group-chat run (a seat's or the
/// director's turn). Structural twin of <see cref="WorkflowStepReasoningCaptured"/>: append-only
/// and sequence-numbered (<see cref="TurnIndex"/>), NOT a reuse of <see cref="WorkflowStepProgress"/>
/// — that event's "a later progress event supersedes any earlier one" semantics are wrong for a
/// transcript where every turn matters. Relayed by the Go API as an SSE event (a separate,
/// follow-up pass — not implemented by this event's addition) and rendered as a running transcript
/// card, mirroring how <see cref="WorkflowStepReasoningCaptured"/> becomes a "step.reasoning" card
/// today. <see cref="Text"/> is free-form model prose and is NEVER authoritative for the actual cut
/// decision — see docs/video-editing.md "The edit room" and <c>EditRoomStepExecutor</c>'s own
/// transcript-artifact doc comment for the same caveat.
/// </summary>
public record WorkflowStepChatTurn
{
    public Guid ExecutionId { get; init; }
    public Guid StepId { get; init; }
    public Guid StepResultId { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? WorkflowDefinitionId { get; init; }
    public int? StepOrder { get; init; }
    public string? StepLabel { get; init; }
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>0-based, append-only turn index within the room.</summary>
    public int TurnIndex { get; init; }

    /// <summary>The configured turn ceiling (<c>EditRoomStepConfig.MaxTurns</c>), for a "turn 3 of 8" display. Null when not known.</summary>
    public int? TotalTurns { get; init; }

    /// <summary>The seat/persona name that spoke, e.g. "PacingEditor" or "Director".</summary>
    public string Speaker { get; init; } = string.Empty;

    /// <summary>"editor" or "director".</summary>
    public string SpeakerRole { get; init; } = string.Empty;

    /// <summary>The turn's text, truncated to roughly 600 characters at a word boundary. Free-form model prose — never authoritative, see the type's doc comment.</summary>
    public string Text { get; init; } = string.Empty;

    public bool Truncated { get; init; }

    /// <summary>
    /// Offered shot/silence/segment ids this turn mentioned, server-extracted with the same regex
    /// <c>EditRoomGroupChatManager</c> uses for convergence checking, filtered against the id set
    /// actually offered to the room — display/audit only, never trusted as the decision itself.
    /// </summary>
    public IReadOnlyList<string> IdsMentioned { get; init; } = [];

    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Published when a step starts execution.
/// </summary>
public record WorkflowStepStarted
{
    public Guid ExecutionId { get; init; }
    public Guid StepId { get; init; }
    public Guid StepResultId { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? WorkflowDefinitionId { get; init; }
    public int? StepOrder { get; init; }
    public string? StepLabel { get; init; }
    public string? StepType { get; init; }
    public int? IterationNumber { get; init; }
    public string? AgentType { get; init; }
    public string? AgentName { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public string? InputPreview { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Published on unrecoverable failure.
/// </summary>
public record WorkflowExecutionFailed
{
    public Guid ExecutionId { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? WorkflowDefinitionId { get; init; }
    public Guid? InitiatedByUserId { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
    public DateTime FailedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Published by Inference API when a project file requires vector index update.
/// </summary>
public record ProjectFileIndexingRequested
{
    public Guid ProjectId { get; init; }
    public Guid FileId { get; init; }
    public string Operation { get; init; } = string.Empty; // Upsert | Delete
    public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
}
