namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One unit of work queued for a runner. Table <c>runner_jobs</c>, owned by the WorkflowEngine
/// migrations and written by the Go runner gateway through raw SQL (it never migrates).
/// The columns carry exactly what the gateway needs for matching, leases and fencing
/// (docs/runner-protocol.md "Job lifecycle, leases and fencing"). No provider key and no presigned
/// URL is ever stored here (ADR: URLs ride on ops).
/// </summary>
public class RunnerJob
{
    public Guid Id { get; set; }

    /// <summary>Unique; makes <c>POST /internal/v1/jobs</c> idempotent.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public Guid OrganizationId { get; set; }
    public Guid ExecutionId { get; set; }

    /// <summary>The workflow step the job serves; null for an execution-scoped session.</summary>
    public Guid? StepId { get; set; }

    public RunnerJobType Type { get; set; }
    public RunnerJobTarget Target { get; set; }

    /// <summary>Capability requirements the gateway filters on (jsonb).</summary>
    public string? RequiredCapsJson { get; set; }

    /// <summary>Job-type-specific assign spec (jsonb).</summary>
    public string? SpecJson { get; set; }

    public RunnerJobStatus Status { get; set; } = RunnerJobStatus.Queued;

    /// <summary>
    /// Claim order: higher first, then <see cref="CreatedAt"/>.
    /// <para>
    /// Carries the organization plan's queue-priority class verbatim (B8c,
    /// <c>ReelBolt.Shared.Billing.RunPriority</c>: the catalog's <c>entitlements.priority</c> - 0
    /// standard, 1 Pro and Team, 2 Enterprise), which the engine reads from
    /// <c>WorkflowExecutionContext.RunPriority</c>. Nothing else defines a run's priority and the
    /// value is deliberately not remapped for this queue, so the broker queue and this one order by
    /// the same scale.
    /// </para>
    /// </summary>
    public int Priority { get; set; }

    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 2;

    public Guid? LeaseOwnerRunnerId { get; set; }

    /// <summary>Fencing token: increases on every assignment.</summary>
    public long LeaseEpoch { get; set; }

    public DateTime? LeaseExpiresAt { get; set; }

    /// <summary>The gateway pod (pod IP) that holds the runner connection for this job.</summary>
    public string? GatewayInstance { get; set; }

    public string? ResultJson { get; set; }

    /// <summary><c>platform</c>, <c>runner</c> or <c>input</c> on failure (B-area classification).</summary>
    public string? ErrorClass { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public WorkflowExecution? Execution { get; set; }
}
