using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The one definition of an engine-taken run snapshot, shared by the engine (<c>ResumeCursor</c>) and the API
/// (Resume/Retry gates, the execution DTO's <c>canResume</c>).
/// </summary>
public static class ExecutionSnapshotOwnership
{
    /// <summary>
    /// The engine-owned column of <c>workflow_definitions</c> naming the run an
    /// <see cref="WorkflowDefinitionKind.ExecutionSnapshot"/> belongs to (a shadow property of the engine's model;
    /// the API's model does not map it and reads it with SQL).
    /// </summary>
    public const string OwnerColumn = "snapshot_of_execution_id";

    /// <summary>
    /// True when a run's snapshot row is one the ENGINE took for that run: kind
    /// <see cref="WorkflowDefinitionKind.ExecutionSnapshot"/> AND owned by the run. A kind-5 row with no owner is a
    /// T-002 archive of a legacy run's edited steps: never resumed or run from.
    /// </summary>
    public static bool IsEngineTakenSnapshot(Guid executionId, WorkflowDefinitionKind snapshotKind, Guid? snapshotOfExecutionId) =>
        snapshotKind == WorkflowDefinitionKind.ExecutionSnapshot && snapshotOfExecutionId == executionId;
}
