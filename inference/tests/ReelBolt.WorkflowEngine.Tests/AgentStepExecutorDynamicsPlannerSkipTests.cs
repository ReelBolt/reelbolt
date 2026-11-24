using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Briefs;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The DynamicsPlanner step is a paid agent step that only matters for a dynamic edit: a run with no effective pace (no style dynamics and
/// no brief pace) gets a normal Skipped result with no model call; the compile then finds its DynamicsPlan unresolved (a soft path it
/// already has) and runs without one. A run with a pace runs the planner as before.
/// </summary>
public class AgentStepExecutorDynamicsPlannerSkipTests
{
    private static AgentStepExecutor CreateExecutor(IReelBoltAgent agent)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent);
        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>())).Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext?)null);
        return new AgentStepExecutor(
            registry.Object, accessor.Object, Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            new ConfigurationBuilder().Build(), Mock.Of<IDecisionGate>(), NullLogger<AgentStepExecutor>.Instance);
    }

    private static Mock<IReelBoltAgent> MockPlanner()
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns("DynamicsPlanner");
        agent.Setup(a => a.AgentType).Returns(AgentType.DynamicsPlanner);
        agent.Setup(a => a.Tools).Returns(Array.Empty<Microsoft.Extensions.AI.AIFunction>());
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = "{\"dynamics\":[]}", Success = true });
        return agent;
    }

    private static StepExecutionContext Context(string? stylePace, string? briefPace)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 6,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), Name = "a", AgentType = AgentType.DynamicsPlanner, IsBuiltIn = true }
        };
        StyleDynamics? styleDynamics = stylePace is null ? null : Enum.Parse<StyleDynamics>(stylePace);
        return new StepExecutionContext
        {
            Execution = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                OrganizationId = Guid.NewGuid(),
                StyleConfigJson = styleDynamics is null ? null : StyleJson.Serialize(new RunStyleSnapshot("s", null, null, new StyleConfig(Dynamics: styleDynamics))),
                RunBriefJson = briefPace is null ? null : RunBriefJson.Serialize(new StoredRunBrief(Pace: briefPace))
            },
            Step = step,
            AllSteps = new List<WorkflowStep> { step },
            AccumulatedOutput = "{\"previous\":true}",
            StepOutputHistory = [],
            CurrentStepIndex = 5,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    [Fact]
    public async Task A_run_with_no_pace_skips_the_planner_without_a_model_call()
    {
        Mock<IReelBoltAgent> planner = MockPlanner();
        StepExecutionResult result = await CreateExecutor(planner.Object).ExecuteAsync(Context(stylePace: null, briefPace: null));

        result.Status.Should().Be(StepStatus.Skipped);
        result.TokensUsed.Should().Be(0);
        result.NextStepIndex.Should().Be(6, "the run moves on to the compile, which reads its plan as unresolved");
        result.Output.Should().Be("{\"previous\":true}", "the skipped step adds nothing to the accumulated output");
        planner.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Medium", null)]
    [InlineData(null, "fast")]
    [InlineData("Calm", "fast")]
    public async Task A_run_with_a_style_or_brief_pace_runs_the_planner(string? stylePace, string? briefPace)
    {
        Mock<IReelBoltAgent> planner = MockPlanner();
        StepExecutionResult result = await CreateExecutor(planner.Object).ExecuteAsync(Context(stylePace, briefPace));

        result.Status.Should().NotBe(StepStatus.Skipped);
        planner.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_skipped_planner_is_not_recorded_in_the_step_output_history_but_a_run_planner_is()
    {
        StepExecutionResult skipped = await CreateExecutor(MockPlanner().Object).ExecuteAsync(Context(stylePace: null, briefPace: null));
        StepExecutionResult ran = await CreateExecutor(MockPlanner().Object).ExecuteAsync(Context(stylePace: "Medium", briefPace: null));

        skipped.Output.Should().NotBeNullOrEmpty("the chain still carries the previous output forward");
        WorkflowExecutorService.RecordsInOutputHistory(skipped, AgentType.DynamicsPlanner).Should().BeFalse(
            "the compile's DynamicsPlan must resolve to unresolved, not to the previous step's output");
        WorkflowExecutorService.RecordsInOutputHistory(ran, AgentType.DynamicsPlanner).Should().BeTrue();
    }

    [Fact]
    public void A_skipped_step_of_any_other_agent_keeps_its_history_behaviour()
    {
        var skipped = new StepExecutionResult { Output = "{\"previous\":true}", NextStepIndex = 1, Status = StepStatus.Skipped };
        WorkflowExecutorService.RecordsInOutputHistory(skipped, AgentType.AuthorAgent).Should().BeTrue();
        WorkflowExecutorService.RecordsInOutputHistory(skipped, null).Should().BeTrue("a Conditional step has no agent type to match");
    }

    [Fact]
    public void The_skip_rule_is_one_helper_that_pricing_and_the_executor_share()
    {
        ReelBolt.Shared.Metering.DynamicsPlannerPricing.Skips(AgentType.DynamicsPlanner, runHasPace: false).Should().BeTrue();
        ReelBolt.Shared.Metering.DynamicsPlannerPricing.Skips(AgentType.DynamicsPlanner, runHasPace: true).Should().BeFalse();
        ReelBolt.Shared.Metering.DynamicsPlannerPricing.Skips(AgentType.DynamicsPlanner, runHasPace: null).Should().BeFalse("unknown holds");
        ReelBolt.Shared.Metering.DynamicsPlannerPricing.Skips(AgentType.AuthorAgent, runHasPace: false).Should().BeFalse();
    }
}
