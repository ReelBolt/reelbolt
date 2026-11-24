#nullable enable
using System;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// One definition of "engine-taken snapshot" (T-003/T-004): a kind-5 row owned by the run. A kind-5 row with no
/// owner is a T-002 legacy archive and is never resumed or retried from. The engine's helper and the API call the
/// same predicate.
/// </summary>
public sealed class ExecutionSnapshotOwnershipTests
{
    [Fact]
    public void A_kind_5_row_owned_by_the_run_is_engine_taken()
    {
        Guid run = Guid.NewGuid();

        ExecutionSnapshotOwnership.IsEngineTakenSnapshot(run, WorkflowDefinitionKind.ExecutionSnapshot, run).Should().BeTrue();
    }

    [Fact]
    public void A_legacy_archive_owned_by_no_run_is_not_engine_taken()
    {
        ExecutionSnapshotOwnership.IsEngineTakenSnapshot(Guid.NewGuid(), WorkflowDefinitionKind.ExecutionSnapshot, null).Should().BeFalse();
    }

    [Fact]
    public void Another_runs_snapshot_or_a_non_snapshot_kind_is_not_engine_taken()
    {
        Guid run = Guid.NewGuid();

        ExecutionSnapshotOwnership.IsEngineTakenSnapshot(run, WorkflowDefinitionKind.ExecutionSnapshot, Guid.NewGuid()).Should().BeFalse();
        ExecutionSnapshotOwnership.IsEngineTakenSnapshot(run, WorkflowDefinitionKind.Standard, run).Should().BeFalse();
    }

    [Theory]
    [InlineData(WorkflowDefinitionKind.ExecutionSnapshot, true, true)]
    [InlineData(WorkflowDefinitionKind.ExecutionSnapshot, false, false)]
    [InlineData(WorkflowDefinitionKind.Standard, true, false)]
    public void The_engine_helper_answers_exactly_what_the_shared_predicate_answers(WorkflowDefinitionKind kind, bool owned, bool expected)
    {
        Guid run = Guid.NewGuid();
        Guid? owner = owned ? run : null;

        ResumeCursor.IsEngineTakenSnapshot(run, kind, owner).Should().Be(expected)
            .And.Be(ExecutionSnapshotOwnership.IsEngineTakenSnapshot(run, kind, owner));
    }
}
