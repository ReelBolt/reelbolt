using System;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-206: the engine-wide decisions for PinInsert (cache, credits, rate class, failures, dependencies, schema).</summary>
public sealed class PinInsertEngineWiringTests
{
    private static WorkflowStep Pin(int order, string? configJson) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = StepType.PinInsert,
        PinInsertConfigJson = configJson
    };

    [Fact]
    public void PinInsert_is_cacheable_and_has_a_deterministic_revision()
    {
        new StepCachePolicy().IsCacheable(Pin(3, "{}")).Should().BeTrue();
        DeterministicStepRevisions.For(StepType.PinInsert).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void PinInsert_is_free_holds_nothing_and_runs_in_the_free_rate_bucket()
    {
        StepCreditDefaults.MillisFor(StepType.PinInsert).Should().Be((0L, 0L));
        StepCreditDefaults.IsNotCharged(StepType.PinInsert).Should().BeTrue();
        RunRateClassifier.Classify([Pin(3, "{}")]).Should().Be(RunRateBucket.Free);
    }

    [Theory]
    [InlineData("PIN_INSERT_UNRESOLVED", FailureClass.UserInput)]
    [InlineData("PIN_INSERT_NO_REGIONS", FailureClass.UserInput)]
    [InlineData("PIN_INSERT_FAILED", FailureClass.PlatformInternal)]
    [InlineData("ANALYSIS_NOT_FOUND", FailureClass.UserInput)]
    public void The_failure_codes_are_classified_deliberately(string code, FailureClass expected) =>
        FailureClassifier.ForCode(code).Should().Be(expected);

    [Fact]
    public void The_cache_key_is_unchanged_without_a_PinInsert_config_and_moves_with_one()
    {
        var builder = new StepCacheKeyBuilder();
        StepCacheKeyInputs inputs = new()
        {
            ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StepType = StepType.PinInsert,
            AgentType = AgentType.VideoStoryEditor,
            AgentDefinitionId = Guid.Empty,
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
            ProjectFileFingerprint = "fp",
            MaxIterations = 3
        };

        string without = builder.Build(inputs);
        builder.Build(inputs with { PinInsertConfigJson = null }).Should().Be(without);
        builder.Build(inputs with { PinInsertConfigJson = """{"pins":[{"regionId":"r0"}]}""" }).Should().NotBe(without);
    }

    [Fact]
    public void PinInsert_depends_on_its_analysis_and_on_every_step_its_pins_name()
    {
        WorkflowStep analyze = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoAnalyze, VideoAnalyzeConfigJson = "{}" };
        WorkflowStep author = new() { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.Agent };
        WorkflowStep pin = Pin(3, """{"analysis":{"from":"Step","stepOrder":1},"pins":[{"content":{"from":"Step","stepOrder":2}}]}""");

        var graph = WorkflowStepDependencyGraph.Build([analyze, author, pin]);

        graph.DependsOn(3).Keys.Should().Contain([1, 2]);
    }

    [Fact]
    public void ListStepSchemas_exports_the_PinInsert_config()
    {
        StepConfigSchemaResult schema = new StepConfigSchemaExporter().GetSchema(StepType.PinInsert);

        schema.HasConfig.Should().BeTrue();
        schema.ConfigTypeName.Should().Be(nameof(PinInsertStepConfig));
        schema.Schema!.ToJsonString().Should().Contain("pins").And.Contain("requireApplied");
    }
}
