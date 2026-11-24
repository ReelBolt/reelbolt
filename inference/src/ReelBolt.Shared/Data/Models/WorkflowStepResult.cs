namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// The result of executing a single workflow step.
/// </summary>
public class WorkflowStepResult
{
    public Guid Id { get; set; }
    public Guid WorkflowExecutionId { get; set; }
    public Guid WorkflowStepId { get; set; }
    public string Output { get; set; } = string.Empty;
    public int TokensUsed { get; set; }

    /// <summary>
    /// Input (prompt) tokens the step consumed, persisted from <c>StepExecutionResult.InputTokens</c>.
    /// Null means "not reported", never zero; a step-cache hit records 0 because it consumed nothing.
    /// Together with <see cref="OutputTokens"/> this is the split the usage ledger prices (B3b).
    /// </summary>
    public int? InputTokens { get; set; }

    /// <summary>Output (completion) tokens the step consumed. Null means "not reported". See <see cref="InputTokens"/>.</summary>
    public int? OutputTokens { get; set; }

    /// <summary>
    /// The inference provider row that served the step's primary model call (an agent step's agent, a
    /// room's director synthesis). Null when the step made no chat call, the provider came from the
    /// legacy config fallback (no row), or it could not be resolved.
    /// </summary>
    public Guid? PrimaryProviderId { get; set; }

    /// <summary>The model name the primary call ran against (see <see cref="PrimaryProviderId"/>).</summary>
    public string? PrimaryModelName { get; set; }
    public long DurationMs { get; set; }
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;

    // Enhanced fields
    public string? InputJson { get; set; }
    public string? OutputJson { get; set; }
    public StepStatus Status { get; set; } = StepStatus.Pending;
    public string? ErrorDetails { get; set; }
    public int? IterationNumber { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// S3/MinIO storage key for a video or image artifact produced by this step.
    /// Null when the step produces no media output.
    /// Key format: projects/{projectId}/outputFiles/{executionId}/{fileName}
    /// </summary>
    public string? OutputStorageKey { get; set; }

    /// <summary>
    /// S3/MinIO storage key for a large, non-playable JSON artifact produced by this step (e.g.
    /// a full video analysis document, or a compile step's edit-decision-list audit trail).
    /// Deliberately a SEPARATE column from <see cref="OutputStorageKey"/>: OutputsController
    /// treats any outputFiles-prefixed OutputStorageKey as a playable video, so a JSON artifact
    /// must never land there. Null when the step produces no such artifact.
    /// Key format: projects/{projectId}/agentFiles/video-analysis/{executionId}/...
    /// </summary>
    public string? ArtifactStorageKey { get; set; }

    /// <summary>
    /// S3/MinIO storage key for a timeline manifest produced by this step (e.g.
    /// a VideoCompile step's timeline document).
    /// Null when the step produces no timeline artifact.
    /// Key format: projects/{projectId}/agentFiles/video-analysis/{executionId}/...
    /// </summary>
    public string? TimelineStorageKey { get; set; }

    /// <summary>
    /// Tool calls made during this step, persisted from <c>StepExecutionResult.ToolCalls</c> at
    /// completion so reopening the execution page later shows the same history a live
    /// SSE-connected tab saw. Array of the same shape <c>WorkflowStepToolCalled</c> publishes per
    /// call (tool name, arguments, result) — see WorkflowEvents.cs. Null when no tool calls were
    /// made during this step.
    /// </summary>
    public string? ToolCallsJson { get; set; }

    /// <summary>
    /// Reasoning/thinking text captured during this step, persisted from
    /// <c>StepExecutionResult.Reasoning</c> at completion, same rationale as
    /// <see cref="ToolCallsJson"/>. Null when no reasoning text was captured during this step.
    /// </summary>
    public string? ReasoningJson { get; set; }

    /// <summary>
    /// For an EditRoom step only: the full room transcript (all turns, in order), persisted at
    /// completion so reopening the execution page shows the complete discussion immediately
    /// instead of only whatever a live SSE-connected tab happened to see. Per-turn shape mirrors
    /// <c>WorkflowStepChatTurn</c>'s fields (turnIndex, speaker, speakerRole, text, truncated,
    /// idsMentioned, totalTurns) but with the FULL untruncated text, not the ~600-char-truncated
    /// SSE broadcast version. Null for every non-EditRoom step type.
    /// </summary>
    public string? ChatTranscriptJson { get; set; }

    /// <summary>
    /// True when this result was served from the cross-execution step-result cache
    /// (ReelBolt.WorkflowEngine.Execution.Caching.IStepResultCache) instead of re-running the
    /// step. A cache hit still takes the exact same persistence/publish path as a normal result —
    /// this flag is the only thing that distinguishes the two after the fact.
    /// </summary>
    public bool FromCache { get; set; }

    /// <summary>
    /// Set only when <see cref="FromCache"/> is true: the token count the ORIGINAL (cache-miss)
    /// execution spent producing the cached output. A cache-hit step itself consumes 0 tokens
    /// now (<see cref="TokensUsed"/> is 0 for a hit) — this field is purely so the UI can report
    /// how many tokens the cache hit saved relative to actually re-running the step.
    /// </summary>
    public int? CachedTokensSaved { get; set; }

    /// <summary>
    /// Set when this result was COPIED from an earlier run instead of produced by running the step: a
    /// composition revision's replay reuses every step that does not depend on the re-rendered film, and
    /// every editorial decision it keeps (docs/design/composition-revisions.md). The id of the result it was
    /// copied from. A copied row carries the original's output and storage keys so the revision run is a
    /// complete record a later revision can replay from; it charged nothing, and it is never listed as a
    /// take of its own (its render, if any, belongs to the row it was copied from). Engine migration
    /// <c>AddStepResultReusedFrom</c>.
    /// </summary>
    public Guid? ReusedFromStepResultId { get; set; }

    /// <summary>
    /// The project owner pinned this result's render as a take to keep: "delete earlier takes" and a
    /// single-take delete never remove it (see docs/video-editing.md "Takes: the latest render first").
    /// Set only by the Inference API's outputs endpoints; the engine never writes it.
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// B6b/D8: who bore the cost of the work a FAILED step consumed — the input to the credit gate's
    /// refund decision (platform-caused classes refund, user classes keep what was consumed).
    /// <see cref="FailureClass.None"/> on a step that completed, and on a step that failed without
    /// any class we could establish (the executor classifies every failure it can, so a <c>None</c>
    /// here is a real gap rather than the normal case). Persisted as a string, append-only.
    /// </summary>
    public FailureClass FailureClass { get; set; } = FailureClass.None;

    /// <summary>The machine-readable failure code that came with <see cref="FailureClass"/>, e.g. <c>ENCODE_FAILED</c>. Null when the step carried no code.</summary>
    public string? FailureCode { get; set; }

    /// <summary>
    /// Millicredits this step actually charged the organization (B6b): what the gate settled, or
    /// what it kept on a user-caused failure. 0 for a cache hit, for a step that reserved nothing
    /// (BYO-only, or <c>Billing:Enabled=false</c>), and for a platform-caused failure that was
    /// refunded in full. Never negative — a refund is the absence of a charge, not a charge here.
    /// </summary>
    public long CreditsChargedMillis { get; set; }

    /// <summary>
    /// S-3: millicredits this step metered but could not get settled (or, for a failed step with a
    /// user-borne class, kept) because every in-process settle attempt failed. Its hold is then still
    /// open; <c>UnsettledHoldSweeper</c> (and a later run failure) closes it at this amount. Null when
    /// the step's hold was closed normally, and on every result written before S-3.
    /// Column <c>credits_unsettled_millis</c>, engine-owned.
    /// </summary>
    public long? CreditsUnsettledMillis { get; set; }

    /// <summary>
    /// D12: where this step's work actually ran. <see cref="ComputeTargetKind.Local"/> for every
    /// pre-D6 result and every install with no compute fabric.
    /// </summary>
    public ComputeTargetKind ComputeTarget { get; set; } = ComputeTargetKind.Local;

    /// <summary>
    /// D12: true when the bytes this result points at were produced by the ORGANIZATION'S OWN runner
    /// — a machine the customer controls, whose binary may have been modified — rather than by this
    /// process (<see cref="ComputeTargetKind.Local"/>) or by one of our own pool workers
    /// (<see cref="ComputeTargetKind.CloudPool"/>).
    /// <para>
    /// It is persisted as its own column rather than derived from <see cref="ComputeTarget"/> at read
    /// time because it is the row's provenance for RECORDS that outlive the enum's meaning — a
    /// historical "was this verified-customer-hardware output?" query must not change its answer
    /// because a later release reinterprets a numeric value. The engine derives it from
    /// <see cref="ComputeTarget"/> at write time (see <c>StepExecutionResult.FromRunner</c>), so the
    /// two can never disagree in a freshly written row.
    /// </para>
    /// <para>
    /// The step-result cache excludes a result with this set (see <c>IStepCachePolicy.IsAdmissible</c>):
    /// a cache hit would serve one customer's machine-produced bytes to a later run, and would do so
    /// without any executor having verified them for that run.
    /// </para>
    /// </summary>
    public bool FromRunner { get; set; }

    /// <summary>
    /// D12: the <c>runner_devices</c> row that produced this result, when a runner did. Null for a
    /// local or (until D6 records it) any other result.
    /// </summary>
    public Guid? RunnerId { get; set; }

    /// <summary>
    /// D12: the <c>runner_jobs</c> row whose completion produced this result, when a runner did. Null
    /// otherwise. Billing (B) reads the job spec, never <c>result_json</c> — this column is what makes
    /// that traceable from a step result.
    /// </summary>
    public Guid? RunnerJobId { get; set; }

    /// <summary>
    /// When a Retry of the run replaced this result: the row is kept for audit and billing
    /// (its credit keys are its own id, so nothing re-settles) but is ignored by resume matching, the
    /// run's live view and lineage. Null for every current result. Engine migration
    /// <c>AddExecutionSnapshots</c>.
    /// </summary>
    public DateTime? SupersededAt { get; set; }

    public WorkflowExecution WorkflowExecution { get; set; } = null!;
    public WorkflowStep WorkflowStep { get; set; } = null!;
}
