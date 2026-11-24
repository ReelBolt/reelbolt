using ReelBolt.Shared.Styles;

namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// Represents a single run of a workflow.
/// </summary>
public class WorkflowExecution
{
    public Guid Id { get; set; }
    public Guid WorkflowDefinitionId { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>
    /// The organization that owns the run (denormalized from the project at queue time) so SSE,
    /// stats, metering and the budget gate need no join through projects.
    /// </summary>
    public Guid OrganizationId { get; set; }
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Queued;
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the run first started. Written once, in the claim, and never changed by a Retry, a resume or an API reset
    /// (StartedAt is overwritten by those). Gates on earlier output judge a run by this, not by StartedAt.
    /// </summary>
    public DateTime? FirstStartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Guid? CurrentStepId { get; set; }
    public int IterationCount { get; set; }
    public string? ResultJson { get; set; }

    // Enhanced fields
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
    public Guid? InitiatedByUserId { get; set; }
    public string? ErrorMessage { get; set; }
    public ExecutionInitiator Initiator { get; set; } = ExecutionInitiator.Human;

    /// <summary>
    /// Free-text context provided by the user at execution time.
    /// Null when the workflow was executed without user input.
    /// Passed as additional context to all agents during execution.
    /// </summary>
    public string? UserRequest { get; set; }

    /// <summary>
    /// The editing style chosen when this run was started, as a <see cref="RunStyleSnapshot"/> JSON
    /// snapshot (<see cref="StyleJson"/>) — copied, not referenced, so editing or deleting the saved
    /// style later never changes what this run meant. Null when no style was chosen, which keeps
    /// every agent prompt and every step-cache key byte-identical to a run made before styles
    /// existed. See docs/editing-styles.md.
    /// </summary>
    public string? StyleConfigJson { get; set; }

    /// <summary>
    /// The design-system pin this run was started against (SPEC §3.5): the exact design and
    /// product-map versions the run must resolve, or null when the run pinned nothing. Read verbatim
    /// by <c>StepType.DesignSystem</c>'s Require op; written when the run is queued.
    /// </summary>
    public string? DesignSystemPinJson { get; set; }

    /// <summary>
    /// The output shape chosen when this run was started, overriding <c>OutputFormat</c> on every
    /// <c>VideoCompile</c> step of the run. Null means no override (<see cref="RunOutputFormat.Original"/>
    /// is normalized to null). See docs/editing-styles.md "Quick format switch".
    /// </summary>
    public RunOutputFormat? OutputFormatOverride { get; set; }

    /// <summary>
    /// The output resolution chosen when this run was started, as the SHORT side in pixels (720, 1080 or
    /// 2160; see <c>OutputResolution</c>). Null means Auto: today's output, capped by the plan and never
    /// above 1080. Like the style and format, null keeps every prompt and step-cache key byte-identical.
    /// </summary>
    public int? OutputResolution { get; set; }

    /// <summary>
    /// The API key (<c>api_keys.id</c>) that started this run, or null for a run a person started. Plain
    /// scalar, no foreign key: the ledger checks the key's caps and status on every hold the run makes.
    /// Retry and resume reuse the row, so they keep it.
    /// </summary>
    public Guid? ApiKeyId { get; set; }

    /// <summary>
    /// The part of the run's typed brief that has no column of its own (language, source language, target
    /// length, design-system choice, voice, source files by role), as a <c>StoredRunBrief</c> JSON written when the
    /// run is queued (<c>run_brief_json</c>, engine migration <c>AddExecutionRunBrief</c>). Null when the run was
    /// started without any of those fields, which keeps it byte-identical to a run made before briefs existed.
    /// See docs/run-brief.md.
    /// </summary>
    public string? RunBriefJson { get; set; }

    /// <summary>
    /// The run-rate bucket ("paid" or "free") the assistant/MCP execute route admitted this run under, stamped by the
    /// Inference API when it queues the run (T-015; <c>admitted_run_rate_bucket</c>, engine migration
    /// <c>AddExecutionAdmittedRunRateBucket</c>). The engine's RUN_CLASS_CHANGED re-check reads it from here, not from the
    /// message, so a retry or a recovery re-publish keeps it. Null for every other start (no re-check: paid by default).
    /// </summary>
    public string? AdmittedRunRateBucket { get; set; }

    /// <summary>
    /// Set by the engine when a run reached <see cref="ExecutionStatus.Passed"/> but a review loop stopped below its
    /// minimum score (<c>attention_json</c>, engine migration <c>AddExecutionAttention</c>): {"reason":"review_below_minimum",
    /// "stepOrder","score","minScore","iterations"}. The status stays Passed (outputs, billing and resume gates are
    /// untouched); the API derives "needs attention" from this. Null when nothing needs attention.
    /// </summary>
    public string? AttentionJson { get; set; }

    /// <summary>
    /// When the run was last put on the queue (set by the Inference API at create and at retry,
    /// bumped by the engine when it re-publishes a stranded request). Lets the recovery sweeper age
    /// a <c>Queued</c> row; null on rows queued before leases existed.
    /// </summary>
    public DateTime? QueuedAt { get; set; }

    /// <summary>
    /// The engine instance (pod name) that holds the lease on this <c>Running</c> execution.
    /// See <c>ExecutionLeaseService</c>; null when not running or on a pre-lease row.
    /// </summary>
    public string? LeaseOwner { get; set; }

    /// <summary>
    /// The lease deadline. The owner renews it every ~15 s (lease 60 s); a <c>Running</c> row whose
    /// lease has lapsed belongs to an instance that is gone and is recovered by the sweeper.
    /// </summary>
    public DateTime? LeaseExpiresAt { get; set; }

    /// <summary>The last time the owner renewed the lease.</summary>
    public DateTime? HeartbeatAt { get; set; }

    /// <summary>
    /// The hidden <see cref="WorkflowDefinitionKind.ExecutionSnapshot"/> clone of the workflow this run
    /// actually executes, taken by the engine when a <see cref="WorkflowDefinitionKind.Standard"/> run
    /// starts. Its step results reference the clone's steps, so editing or deleting the user's
    /// workflow can neither change the run nor cascade-delete its results. Null for a run of a hidden
    /// system workflow (never snapshotted) and for every run that started before snapshots existed.
    /// <see cref="WorkflowDefinitionId"/> stays the source workflow (lists, URLs, step cache, estimates).
    /// Engine migration <c>AddExecutionSnapshots</c>; ON DELETE SET NULL.
    /// </summary>
    public Guid? SnapshotDefinitionId { get; set; }

    /// <summary>
    /// The behaviour fields of every agent the snapshot's steps use, as they were when the run started
    /// (<c>AgentSnapshot</c>, jsonb). The engine overlays them on the live agent rows for the whole run,
    /// so an agent edited mid-run does not reach the run's later steps. Null with no snapshot.
    /// </summary>
    public string? AgentSnapshotJson { get; set; }

    /// <summary>
    /// True while a queued dispatch of this row is a RESUME (continue from the first missing result)
    /// rather than a fresh start. Lives on the row so a sweeper re-publish can never turn a resume into
    /// a restart. Default false.
    /// </summary>
    public bool PendingResume { get; set; }

    /// <summary>How many times this run was resumed (manual and automatic). Default 0.</summary>
    public int ResumeCount { get; set; }

    /// <summary>How many of <see cref="ResumeCount"/> the recovery sweeper made on its own. Default 0.</summary>
    public int AutoResumeCount { get; set; }

    public WorkflowDefinition WorkflowDefinition { get; set; } = null!;
    public Project Project { get; set; } = null!;
    public WorkflowStep? CurrentStep { get; set; }
    public ICollection<WorkflowStepResult> StepResults { get; set; } = new List<WorkflowStepResult>();
    public ICollection<ReviewScore> ReviewScores { get; set; } = new List<ReviewScore>();
}
