using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// <c>workflow_definitions.kind</c> is persisted as an integer, so two kinds sharing a value are the
/// same kind to every query and to the engine. <c>TimelineRender</c> and <c>CompositionReplay</c>
/// once both took 3: every editor render ran as a composition replay and failed. The values are
/// append-only and must stay distinct; migration <c>SplitTimelineRenderWorkflowKind</c> moved the
/// stored render workflows to 4.
/// </summary>
public class WorkflowDefinitionKindTests
{
    [Fact]
    public void Every_kind_has_its_own_stored_value()
    {
        int[] values = Enum.GetValues<WorkflowDefinitionKind>().Select(k => (int)k).ToArray();

        values.Should().OnlyHaveUniqueItems();
        Enum.GetNames<WorkflowDefinitionKind>().Should().HaveCount(values.Length);
    }

    [Fact]
    public void Stored_values_are_pinned()
    {
        ((int)WorkflowDefinitionKind.Standard).Should().Be(0);
        ((int)WorkflowDefinitionKind.CompositionRevision).Should().Be(1);
        ((int)WorkflowDefinitionKind.GraphicDesign).Should().Be(2);
        ((int)WorkflowDefinitionKind.CompositionReplay).Should().Be(3);
        ((int)WorkflowDefinitionKind.TimelineRender).Should().Be(4);
        ((int)WorkflowDefinitionKind.ExecutionSnapshot).Should().Be(5);
        ((int)WorkflowDefinitionKind.ObjectAnalysis).Should().Be(6);
        ((int)WorkflowDefinitionKind.AudioCleanup).Should().Be(7);
        ((int)WorkflowDefinitionKind.VideoEnhance).Should().Be(8);
        ((int)WorkflowDefinitionKind.SubjectClip).Should().Be(9);
        ((int)WorkflowDefinitionKind.AvatarBuild).Should().Be(10);
    }
}
