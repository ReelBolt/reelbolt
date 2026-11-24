using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Context;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Covers the ONE line of <see cref="AgentStepExecutor"/> this feature is allowed to change: the
/// agent's input is now built through <see cref="StepExecutionContext.BuildAgentInputAsync"/>, so
/// the relevance pass reaches the prompt. Everything here is about that line being invisible when
/// it should be — the executor's behaviour, and the exact prompt handed to the agent, must be
/// identical to the pre-existing synchronous path for every case except a genuine, trusted,
/// over-budget relevance ordering.
/// </summary>
public class AgentStepExecutorContextRelevanceTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static AgentStepExecutor CreateExecutor(IReelBoltAgent agent)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent);

        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext?)null);

        return new AgentStepExecutor(
            registry.Object,
            accessor.Object,
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            EmptyConfig(),
            Mock.Of<IDecisionGate>(),
            NullLogger<AgentStepExecutor>.Instance);
    }

    private static (Mock<IReelBoltAgent> Agent, List<string> Inputs) CreateRecordingAgent()
    {
        List<string> inputs = [];
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns("DirectorAgent");
        agent.Setup(a => a.AgentType).Returns(AgentType.DirectorAgent);
        agent.Setup(a => a.Tools).Returns(Array.Empty<Microsoft.Extensions.AI.AIFunction>());
        agent.Setup(a => a.OutputSchemaType).Returns((Type?)null);
        agent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback((string input, Guid? _, CancellationToken _) => inputs.Add(input))
            .ReturnsAsync(new AgentRunResult { Output = "{\"ok\":true}", Success = true, TokensUsed = 10, InputTokens = 6, OutputTokens = 4 });

        return (agent, inputs);
    }

    private sealed class FakeSelector(ContextRelevanceOrdering? result) : IContextRelevanceSelector
    {
        public int CallCount { get; private set; }

        public Task<ContextRelevanceOrdering?> SelectDropOrderAsync(
            Guid projectId,
            IReadOnlyList<ContextRelevanceCandidate> stepCandidates,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private static List<StepOutputHistoryEntry> History() =>
    [
        new StepOutputHistoryEntry(1, "Oldest", new string('a', 300)),
        new StepOutputHistoryEntry(2, "Middle", new string('b', 300)),
        new StepOutputHistoryEntry(3, "Newest Droppable", new string('c', 300)),
        new StepOutputHistoryEntry(4, "Protected", new string('d', 100))
    ];

    private static string DropMarker(int stepOrder, int outputChars) =>
        $"_[Step {stepOrder} output omitted by context budget ({outputChars} chars) " +
        "— re-read it with the project/file tools if you need it.]_";

    private static StepExecutionContext CreateContext(
        IContextRelevanceSelector? selector,
        int? maxInputChars,
        bool budgetEnabled = true)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 5,
            StepType = StepType.Agent,
            AgentInputContextMode = AgentInputContextMode.FullWorkflow,
            AgentDefinition = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = "DirectorAgent",
                AgentType = AgentType.DirectorAgent,
                Description = string.Empty,
                SystemPrompt = string.Empty,
                IsBuiltIn = true
            }
        };

        List<StepOutputHistoryEntry> history = History();
        int legacyLength = string.Join("\n\n---\n\n", history.Select(h => $"## Step {h.StepOrder}: {h.StepLabel}\n{h.Output}")).Length;

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = history,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None,
            BudgetOptions = new AgentInputBudgetOptions
            {
                Enabled = budgetEnabled,
                MaxInputChars = maxInputChars ?? legacyLength - 100,
                MaxDigestedStepChars = 300,
                RecentStepsVerbatim = 1,
                DigestMaxStringChars = 50,
                DigestMaxArrayItems = 5,
                DigestMaxDepth = 6,
                RelevanceSelector = selector
            }
        };
    }

    [Fact]
    public async Task The_agent_receives_the_relevance_reordered_prompt_when_the_input_is_over_budget()
    {
        (Mock<IReelBoltAgent> agent, List<string> inputs) = CreateRecordingAgent();
        var selector = new FakeSelector(new ContextRelevanceOrdering([3, 1, 2]));
        StepExecutionContext context = CreateContext(selector, maxInputChars: null);

        StepExecutionResult result = await CreateExecutor(agent.Object).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        selector.CallCount.Should().Be(1);
        inputs.Should().ContainSingle();
        inputs[0].Should().Contain(DropMarker(3, 300));
        inputs[0].Should().NotContain(DropMarker(1, 300));
        inputs[0].Should().Be(context.LastResolvedAgentInput);
    }

    [Fact]
    public async Task Without_a_selector_the_agent_receives_exactly_the_synchronous_prompt()
    {
        (Mock<IReelBoltAgent> agent, List<string> inputs) = CreateRecordingAgent();
        StepExecutionContext context = CreateContext(selector: null, maxInputChars: null);

        await CreateExecutor(agent.Object).ExecuteAsync(context);

        inputs.Should().ContainSingle();
        inputs[0].Should().Be(context.BuildAgentInput());
        inputs[0].Should().Contain(DropMarker(1, 300));
    }

    [Fact]
    public async Task Under_budget_the_agent_receives_the_unchanged_legacy_prompt_and_no_call_is_made()
    {
        (Mock<IReelBoltAgent> agent, List<string> inputs) = CreateRecordingAgent();
        var selector = new FakeSelector(new ContextRelevanceOrdering([3, 1, 2]));
        StepExecutionContext context = CreateContext(selector, maxInputChars: 1_000_000);

        await CreateExecutor(agent.Object).ExecuteAsync(context);

        selector.CallCount.Should().Be(0);
        inputs.Should().ContainSingle();
        inputs[0].Should().Be(context.BuildAgentInput());
        inputs[0].Should().NotContain("omitted by context budget");
    }

    [Fact]
    public async Task A_disabled_budget_leaves_the_agent_prompt_byte_identical()
    {
        (Mock<IReelBoltAgent> agent, List<string> inputs) = CreateRecordingAgent();
        var selector = new FakeSelector(new ContextRelevanceOrdering([3, 1, 2]));
        StepExecutionContext context = CreateContext(selector, maxInputChars: 100, budgetEnabled: false);

        await CreateExecutor(agent.Object).ExecuteAsync(context);

        selector.CallCount.Should().Be(0);
        inputs.Should().ContainSingle();
        inputs[0].Should().NotContain("omitted by context budget");
        inputs[0].Should().Be(context.BuildAgentInput());
    }

    [Fact]
    public async Task A_selector_that_returns_null_leaves_the_executor_result_and_prompt_unchanged()
    {
        (Mock<IReelBoltAgent> agentWithSelector, List<string> inputsWithSelector) = CreateRecordingAgent();
        (Mock<IReelBoltAgent> agentWithout, List<string> inputsWithout) = CreateRecordingAgent();

        StepExecutionContext withSelector = CreateContext(new FakeSelector(result: null), maxInputChars: null);
        StepExecutionContext without = CreateContext(selector: null, maxInputChars: null);

        StepExecutionResult resultWith = await CreateExecutor(agentWithSelector.Object).ExecuteAsync(withSelector);
        StepExecutionResult resultWithout = await CreateExecutor(agentWithout.Object).ExecuteAsync(without);

        inputsWithSelector[0].Should().Be(inputsWithout[0]);
        resultWith.Output.Should().Be(resultWithout.Output);
        resultWith.Status.Should().Be(resultWithout.Status);
        resultWith.TokensUsed.Should().Be(resultWithout.TokensUsed);
        resultWith.InputTokens.Should().Be(resultWithout.InputTokens);
        resultWith.OutputTokens.Should().Be(resultWithout.OutputTokens);
        resultWith.NextStepIndex.Should().Be(resultWithout.NextStepIndex);
        resultWith.NewIterationCount.Should().Be(resultWithout.NewIterationCount);
        resultWith.ErrorDetails.Should().Be(resultWithout.ErrorDetails);
    }
}
