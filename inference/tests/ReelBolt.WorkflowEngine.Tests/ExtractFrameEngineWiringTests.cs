using System;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-105: the engine-wide decisions every new step type needs (cache, credits, failures, dependencies).</summary>
public sealed class ExtractFrameEngineWiringTests
{
    private static WorkflowStep Frame(int order, string? configJson) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = StepType.ExtractFrame,
        ExtractFrameConfigJson = configJson
    };

    [Fact]
    public void ExtractFrame_is_cacheable_and_has_a_deterministic_revision()
    {
        new StepCachePolicy().IsCacheable(Frame(2, "{}")).Should().BeTrue();
        DeterministicStepRevisions.For(StepType.ExtractFrame).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ExtractFrame_is_free_so_it_holds_nothing_and_is_never_charged()
    {
        StepCreditDefaults.MillisFor(StepType.ExtractFrame).Should().Be((0L, 0L));
        StepCreditDefaults.IsNotCharged(StepType.ExtractFrame).Should().BeTrue();
    }

    [Theory]
    [InlineData("FRAME_EXTRACTION_FAILED", FailureClass.PlatformInternal)]
    [InlineData("FRAME_EMPTY", FailureClass.PlatformInternal)]
    [InlineData("EXTRACT_FRAME_FAILED", FailureClass.PlatformInternal)]
    [InlineData("FRAME_OUT_OF_RANGE", FailureClass.UserInput)]
    [InlineData("FRAME_INPUT_NOT_FOUND", FailureClass.UserInput)]
    [InlineData("FRAME_INPUT_INVALID", FailureClass.UserInput)]
    [InlineData("SOURCE_NOT_FOUND", FailureClass.UserInput)]
    [InlineData("SOURCE_UNRESOLVED", FailureClass.UserInput)]
    public void The_new_failure_codes_are_classified_deliberately(string code, FailureClass expected) =>
        FailureClassifier.ForCode(code).Should().Be(expected);

    [Fact]
    public void The_cache_key_is_unchanged_when_there_is_no_ExtractFrame_config_and_moves_when_there_is_one()
    {
        var builder = new StepCacheKeyBuilder();
        StepCacheKeyInputs inputs = new()
        {
            ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StepType = StepType.ExtractFrame,
            AgentType = AgentType.VideoStoryEditor,
            AgentDefinitionId = Guid.Empty,
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
            ProjectFileFingerprint = "fp",
            MaxIterations = 3
        };

        string without = builder.Build(inputs);
        builder.Build(inputs with { ExtractFrameConfigJson = null }).Should().Be(without);
        builder.Build(inputs with { ExtractFrameConfigJson = """{"at":"Last"}""" }).Should().NotBe(without);
        builder.Build(inputs with { ExtractFrameConfigJson = """{"at":"First"}""" })
            .Should().NotBe(builder.Build(inputs with { ExtractFrameConfigJson = """{"at":"Last"}""" }));
    }

    [Fact]
    public void ExtractFrame_depends_on_the_step_its_source_names()
    {
        WorkflowStep video = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = """{"inlinePrompt":"x"}"""
        };
        WorkflowStep frame = Frame(2, """{"source":{"kind":"StepOutput","stepOrder":1},"at":"Last"}""");

        var graph = WorkflowStepDependencyGraph.Build([video, frame]);

        graph.DependsOn(2).Keys.Should().Contain(1);
    }

    [Fact]
    public void A_VideoGenerate_frame_input_depends_on_the_step_it_reads()
    {
        WorkflowStep frame = Frame(2, """{"source":{"kind":"StepOutput","stepOrder":1},"at":"Last"}""");
        WorkflowStep video = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = """{"inlinePrompt":"x","firstFrame":{"from":"Step","stepOrder":2}}"""
        };

        var graph = WorkflowStepDependencyGraph.Build([Frame(1, null), frame, video]);

        graph.DependsOn(3).Keys.Should().Contain(2);
    }
}
