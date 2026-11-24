using System;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>QA-P2-2: a thumbnail whose Source names another step's output depends on that step, so a re-render there changes the cache key.</summary>
public sealed class ThumbnailGenerateDependencyGraphTests
{
    private static WorkflowStep Step(int order, StepType type, string? thumbnailJson = null) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = type,
        ThumbnailGenerateConfigJson = thumbnailJson
    };

    [Fact]
    public void A_StepOutput_source_adds_the_step_it_names()
    {
        WorkflowStep thumbnail = Step(4, StepType.ThumbnailGenerate,
            """{"analysisStepOrder":1,"selection":"AtTime","source":{"kind":"StepOutput","stepOrder":3}}""");

        var graph = WorkflowStepDependencyGraph.Build([Step(1, StepType.VideoAnalyze), Step(2, StepType.VideoGenerate), Step(3, StepType.VideoCompile), thumbnail]);

        graph.DependsOn(4).Keys.Should().Contain([1, 3]);
        graph.DependsOn(4).Keys.Should().NotContain(2);
    }

    [Fact]
    public void A_previous_output_source_depends_on_the_earlier_steps()
    {
        WorkflowStep thumbnail = Step(4, StepType.ThumbnailGenerate,
            """{"analysisStepOrder":1,"source":{"kind":"PreviousStepOutput"}}""");

        var graph = WorkflowStepDependencyGraph.Build([Step(1, StepType.VideoAnalyze), Step(2, StepType.VideoGenerate), Step(3, StepType.VideoCompile), thumbnail]);

        graph.DependsOn(4).Keys.Should().Contain([1, 3]);
    }

    [Fact]
    public void Without_a_source_only_the_analysis_is_a_dependency()
    {
        WorkflowStep thumbnail = Step(4, StepType.ThumbnailGenerate, """{"analysisStepOrder":1}""");

        var graph = WorkflowStepDependencyGraph.Build([Step(1, StepType.VideoAnalyze), Step(2, StepType.VideoGenerate), Step(3, StepType.VideoCompile), thumbnail]);

        graph.DependsOn(4).Keys.Should().BeEquivalentTo([1]);
    }
}
