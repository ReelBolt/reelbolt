using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>ConditionalStepConfig.SourceStepOrder</c>: a Conditional can evaluate its expression against a
/// named earlier step's output instead of the previous step's, so a branch placed after other steps can
/// still read the step that decided it (the profile template re-checks the Parse step's view after the
/// product analyzers). The step's own output stays the pass-through of the previous step's.
/// </summary>
public sealed class ConditionalSourceStepOrderTests
{
    private const string ParseOutput = """{"view":{"fallbackDesign":true}}""";
    private const string AnalyzerOutput = """{"routes":[]}""";

    [Fact]
    public async Task The_expression_reads_the_named_steps_output()
    {
        StepExecutionContext context = Context("""{"version":1,"sourceStepOrder":1}""", [new(1, "parse", ParseOutput, null, null)]);

        StepExecutionResult result = await Executor().ExecuteAsync(context);

        result.NextStepIndex.Should().Be(IndexOf(context, 9), "the Parse step's view says the design is missing");
        result.Output.Should().Be(AnalyzerOutput, "the Conditional still passes the previous step's output through");
    }

    [Fact]
    public async Task Without_a_source_the_expression_reads_the_previous_steps_output()
    {
        StepExecutionContext context = Context(null, [new(1, "parse", ParseOutput, null, null)]);

        StepExecutionResult result = await Executor().ExecuteAsync(context);

        result.NextStepIndex.Should().Be(IndexOf(context, 10), "the analyzer output has no view: unknown identifiers are false");
    }

    [Fact]
    public async Task A_source_step_that_did_not_run_evaluates_false()
    {
        StepExecutionContext context = Context("""{"version":1,"sourceStepOrder":1}""", []);

        StepExecutionResult result = await Executor().ExecuteAsync(context);

        result.NextStepIndex.Should().Be(IndexOf(context, 10));
        result.Status.Should().Be(StepStatus.Completed);
    }

    private static int IndexOf(StepExecutionContext context, int order) => context.AllSteps.FindIndex(s => s.StepOrder == order);

    private static ConditionalStepExecutor Executor() =>
        new(NullLogger<ConditionalStepExecutor>.Instance, Mock.Of<IDecisionGate>());

    private static StepExecutionContext Context(string? configJson, List<StepOutputHistoryEntry> history)
    {
        Guid workflowId = Guid.NewGuid();
        WorkflowStep Step(int order, StepType type) => new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = order,
            StepType = type,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.ExtractTransform, Name = "x" }
        };

        WorkflowStep conditional = Step(8, StepType.Conditional);
        conditional.ConditionExpression = "$.view.fallbackDesign";
        conditional.TrueBranchStepOrder = "9";
        conditional.FalseBranchStepOrder = "10";
        conditional.ConditionalConfigJson = configJson;
        List<WorkflowStep> steps = [Step(1, StepType.DesignSystem), Step(7, StepType.Agent), conditional, Step(9, StepType.Agent), Step(10, StepType.DesignSystem)];
        history.Add(new StepOutputHistoryEntry(7, "routes", AnalyzerOutput, null, null));

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = conditional,
            AllSteps = steps,
            AccumulatedOutput = AnalyzerOutput,
            StepOutputHistory = history,
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }
}
