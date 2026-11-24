using System;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Security review for T-001/T-005: a compile's media-overlay plan and each
/// <c>mediaOverlays[].content</c> step reference are cache dependencies, exactly like the graphics
/// plan and the insert-content references they mirror.
/// </summary>
public sealed class MediaOverlayDependencyGraphTests
{
    private static WorkflowStep Step(int order, StepType type, string? compileJson = null) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = type,
        VideoCompileConfigJson = compileJson
    };

    [Fact]
    public void The_plan_step_and_each_step_content_are_dependencies()
    {
        WorkflowStep compile = Step(4, StepType.VideoCompile,
            """
            {
              "version": 1,
              "decision": { "from": "Step", "stepOrder": 3 },
              "analysisStepOrder": 1,
              "mediaOverlayPlan": { "from": "Step", "stepOrder": 2 },
              "mediaOverlays": [
                { "content": { "from": "Step", "stepOrder": 3 } },
                { "content": { "from": "ProjectFile", "projectFileId": "00000000-0000-0000-0000-000000000001" } }
              ]
            }
            """);

        var graph = WorkflowStepDependencyGraph.Build(
            [Step(1, StepType.VideoAnalyze), Step(2, StepType.Agent), Step(3, StepType.Agent), compile]);

        graph.DependsOn(4).Keys.Should().Contain(2, "the overlay plan is read from step 2");
        graph.DependsOn(4).Keys.Should().Contain(3, "the step-referenced picture is read from step 3");
    }

    [Fact]
    public void A_project_file_content_adds_no_step_dependency()
    {
        WorkflowStep compile = Step(3, StepType.VideoCompile,
            """
            {
              "version": 1,
              "decision": { "from": "Step", "stepOrder": 2 },
              "analysisStepOrder": 1,
              "mediaOverlays": [
                { "content": { "from": "ProjectFile", "projectFileId": "00000000-0000-0000-0000-000000000001" } }
              ]
            }
            """);

        var graph = WorkflowStepDependencyGraph.Build(
            [Step(1, StepType.VideoAnalyze), Step(2, StepType.Agent), compile]);

        graph.DependsOn(3).Keys.Should().BeEquivalentTo([1, 2]);
    }
}
