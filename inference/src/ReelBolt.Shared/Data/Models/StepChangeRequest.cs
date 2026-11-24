namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A request to change one step of a finished run (docs/design/change-requests.md): a target step, an
/// optional item scope, an instruction and typed overrides. Its work is its own hidden
/// <see cref="WorkflowDefinitionKind.CompositionReplay"/> run (<see cref="ExecutionId"/>); status is read
/// from that execution, never copied here.
///
/// <para>
/// Owned by the Inference API (migration <c>AddStepChangeRequests</c>); mapped by the WorkflowEngine with
/// <c>ExcludeFromMigrations()</c>, like <see cref="CompositionRevision"/>. No foreign key to engine tables.
/// No tenant query filter: a project child reached only through a project-authorized route
/// (<c>TenantBoundaryInventoryTests</c>); <see cref="OrganizationId"/> is denormalized, never a guard.
/// </para>
/// </summary>
public class StepChangeRequest
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>The finished run the owner asked to change.</summary>
    public Guid SourceExecutionId { get; set; }

    public int TargetStepOrder { get; set; }

    /// <summary>The target step's latest finished result in the source run, when known.</summary>
    public Guid? TargetStepResultId { get; set; }

    /// <summary>The target step's <c>StepType</c> name.</summary>
    public string StepType { get; set; } = string.Empty;

    public string Instruction { get; set; } = string.Empty;

    /// <summary>Item ids the request is scoped to (jsonb array); null = the whole step.</summary>
    public string? ScopeJson { get; set; }

    /// <summary>Typed overrides (jsonb): itemId or "*" to key to value; null = none.</summary>
    public string? OverridesJson { get; set; }

    /// <summary>The request's own execution (unique).</summary>
    public Guid ExecutionId { get; set; }

    public Guid WorkflowDefinitionId { get; set; }

    /// <summary>
    /// The share of the target step's items this request re-makes, in (0, 1] (<c>StepChangeScope.Compute</c>); null =
    /// unknown, which prices the whole step. The target step's credit hold and the API estimate are multiplied by it.
    /// </summary>
    public double? ScopeFraction { get; set; }

    /// <summary>A serialized <see cref="ReelBolt.Shared.Workflows.Replay.CompositionReplayPlan"/> (jsonb).</summary>
    public string? ReplayPlanJson { get; set; }

    public Guid RequestedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
