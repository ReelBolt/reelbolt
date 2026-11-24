using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;
using Xunit;
using ModelStepType = ReelBolt.Shared.Data.Models.StepType;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.StepType;

/// <summary>
/// Dependency-graph coverage for <see cref="ModelStepType.DesignSystem"/>: a Compose step reads the
/// parse artifact and every LLM fallback step it names; Parse/Require read no earlier step; and an
/// unreadable config falls back to "depends on every earlier step" (the graph's standing rule).
/// </summary>
public sealed class DesignSystemStepDependencyGraphTests
{
    private static WorkflowStep Step(int order, ModelStepType type, string? configJson = null, Guid? agentId = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = agentId ?? Guid.NewGuid(),
            StepOrder = order,
            StepType = type,
            DesignSystemConfigJson = configJson
        };

    [Fact]
    public void A_Parse_step_reads_nothing_from_earlier_steps()
    {
        List<WorkflowStep> steps =
        [
            Step(1, ModelStepType.Agent),
            Step(2, ModelStepType.Agent),
            Step(3, ModelStepType.DesignSystem, """{"op":"Parse","parse":{}}""")
        ];

        WorkflowStepDependencyGraph.Build(steps).DependsOn(3).Keys.Should().BeEmpty();
    }

    [Fact]
    public void A_Require_step_reads_nothing_from_earlier_steps()
    {
        List<WorkflowStep> steps =
        [
            Step(1, ModelStepType.Agent),
            Step(2, ModelStepType.Agent),
            Step(3, ModelStepType.DesignSystem, """{"op":"Require","require":{"requireDesign":true,"requireProductMap":true}}""")
        ];

        WorkflowStepDependencyGraph.Build(steps).DependsOn(3).Keys.Should().BeEmpty();
    }

    [Fact]
    public void A_Compose_step_reads_its_parse_artifact_and_every_fallback_step()
    {
        List<WorkflowStep> steps =
        [
            Step(1, ModelStepType.Agent),
            Step(2, ModelStepType.Agent),
            Step(3, ModelStepType.Agent),
            Step(4, ModelStepType.Agent),
            Step(5, ModelStepType.Agent),
            Step(6, ModelStepType.Agent),
            Step(7, ModelStepType.Agent),
            Step(8, ModelStepType.Agent),
            Step(9, ModelStepType.DesignSystem,
                """{"op":"Compose","compose":{"parseStepOrder":1,"designFallbackStepOrders":[8],"productFallbackStepOrders":[4,5,6,7]}}""")
        ];

        WorkflowStepDependencyGraph.Build(steps).DependsOn(9).Keys.Should().BeEquivalentTo([1, 4, 5, 6, 7, 8]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{bogus")]
    public void A_DesignSystem_step_with_an_unreadable_config_depends_on_every_earlier_step(string? configJson)
    {
        List<WorkflowStep> steps =
        [
            Step(1, ModelStepType.Agent),
            Step(2, ModelStepType.Agent),
            Step(3, ModelStepType.Agent),
            Step(4, ModelStepType.DesignSystem, configJson)
        ];

        WorkflowStepDependencyGraph.Build(steps).DependsOn(4).Keys.Should().BeEquivalentTo([1, 2, 3]);
    }

    [Fact]
    public void A_Compose_step_whose_compose_object_is_unreadable_depends_on_every_earlier_step()
    {
        List<WorkflowStep> steps =
        [
            Step(1, ModelStepType.Agent),
            Step(2, ModelStepType.Agent),
            Step(3, ModelStepType.DesignSystem, """{"op":"Compose","compose":"not an object"}""")
        ];

        WorkflowStepDependencyGraph.Build(steps).DependsOn(3).Keys.Should().BeEquivalentTo([1, 2]);
    }
}
