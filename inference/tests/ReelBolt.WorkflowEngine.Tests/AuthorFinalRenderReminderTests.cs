using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Production run 36df4287: the Author edited and previewed for three attempts and never called
/// RenderVideoAndUploadToStorage. An Author that answers without a successful full render now gets ONE more
/// turn in the same attempt, told plainly to render; if it still does not, the step fails as before with a
/// message that says the agent never rendered the final video.
/// </summary>
public class AuthorFinalRenderReminderTests
{
    private const string StorageKey = "projects/p/outputFiles/e/promo.mp4";

    [Fact]
    public async Task An_author_that_renders_after_the_reminder_completes_with_its_film()
    {
        WorkflowExecutionContextAccessor accessor = new();
        List<string> prompts = [];
        int calls = 0;
        IReelBoltAgent agent = Agent(AgentType.AuthorAgent, (prompt, _) =>
        {
            prompts.Add(prompt);
            if (++calls == 2)
                accessor.Current!.PendingOutputStorageKey = StorageKey;
            return new AgentRunResult { Output = calls == 1 ? "Composition looks good." : "Rendered.", TokensUsed = 100, InputTokens = 80, OutputTokens = 20 };
        });

        StepExecutionResult result = await Executor(agent, accessor).ExecuteAsync(Context(AgentType.AuthorAgent));

        result.Status.Should().Be(StepStatus.Completed);
        result.OutputStorageKey.Should().Be(StorageKey);
        result.TokensUsed.Should().Be(200, "the reminder turn's usage is metered with the first");
        prompts.Should().HaveCount(2);
        prompts[1].Should().StartWith("[FINAL RENDER MISSING]");
        prompts[1].Should().Contain("RenderVideoAndUploadToStorage").And.Contain("Composition looks good.");
    }

    [Fact]
    public async Task An_author_that_still_does_not_render_fails_saying_it_never_rendered_after_exactly_one_reminder()
    {
        WorkflowExecutionContextAccessor accessor = new();
        int calls = 0;
        IReelBoltAgent agent = Agent(AgentType.AuthorAgent, (_, _) =>
        {
            calls++;
            return new AgentRunResult { Output = "Done editing." };
        });

        StepExecutionResult result = await Executor(agent, accessor).ExecuteAsync(Context(AgentType.AuthorAgent));

        calls.Should().Be(2, "one reminder, never more");
        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Be(AgentStepExecutor.AuthorNeverRenderedMessage);
        result.ErrorDetails.Should().Contain("never rendered the final video");
    }

    [Fact]
    public async Task An_author_that_rendered_the_first_time_gets_no_reminder()
    {
        WorkflowExecutionContextAccessor accessor = new();
        int calls = 0;
        IReelBoltAgent agent = Agent(AgentType.AuthorAgent, (_, _) =>
        {
            calls++;
            accessor.Current!.PendingOutputStorageKey = StorageKey;
            return new AgentRunResult { Output = "Rendered." };
        });

        StepExecutionResult result = await Executor(agent, accessor).ExecuteAsync(Context(AgentType.AuthorAgent));

        calls.Should().Be(1);
        result.Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task Another_agent_type_is_never_reminded_to_render()
    {
        WorkflowExecutionContextAccessor accessor = new();
        int calls = 0;
        IReelBoltAgent agent = Agent(AgentType.DirectorAgent, (_, _) =>
        {
            calls++;
            return new AgentRunResult { Output = "{\"plan\":\"ok\"}" };
        });

        await Executor(agent, accessor).ExecuteAsync(Context(AgentType.DirectorAgent));

        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_first_run_is_not_reminded()
    {
        WorkflowExecutionContextAccessor accessor = new();
        int calls = 0;
        IReelBoltAgent agent = Agent(AgentType.AuthorAgent, (_, _) =>
        {
            calls++;
            return new AgentRunResult { Output = "boom", Success = false, FailureReason = "boom" };
        });

        StepExecutionResult result = await Executor(agent, accessor).ExecuteAsync(Context(AgentType.AuthorAgent));

        calls.Should().Be(1);
        result.Status.Should().Be(StepStatus.Failed);
    }

    [Fact]
    public async Task A_reminder_turn_that_throws_keeps_the_existing_failure()
    {
        WorkflowExecutionContextAccessor accessor = new();
        int calls = 0;
        IReelBoltAgent agent = Agent(AgentType.AuthorAgent, (_, _) =>
        {
            if (++calls == 2)
                throw new TimeoutException("Agent 'Author' timed out");
            return new AgentRunResult { Output = "Done editing." };
        });

        StepExecutionResult result = await Executor(agent, accessor).ExecuteAsync(Context(AgentType.AuthorAgent));

        calls.Should().Be(2);
        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Be(AgentStepExecutor.AuthorNeverRenderedMessage);
    }

    [Fact]
    public void The_reminder_prompt_bounds_the_quoted_answer_and_keeps_the_step_input()
    {
        string prompt = AgentStepExecutor.BuildFinalRenderReminderPrompt("STEP INPUT", new string('x', 10_000));

        prompt.Should().StartWith("[FINAL RENDER MISSING]");
        prompt.Should().Contain("...[truncated]");
        prompt.Should().EndWith("STEP INPUT");
        prompt.Length.Should().BeLessThan(AgentStepExecutor.FinalRenderReminderAnswerChars + 2_000);
    }

    private static IReelBoltAgent Agent(AgentType type, Func<string, int, AgentRunResult> run)
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns(type.ToString());
        agent.Setup(a => a.AgentType).Returns(type);
        agent.Setup(a => a.Tools).Returns(Array.Empty<AIFunction>());
        agent.Setup(a => a.OutputSchemaType).Returns((Type?)null);
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string prompt, Guid? _, CancellationToken _) => run(prompt, 0));
        return agent.Object;
    }

    private static AgentStepExecutor Executor(IReelBoltAgent agent, WorkflowExecutionContextAccessor accessor)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent);
        return new AgentStepExecutor(
            registry.Object,
            accessor,
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            Mock.Of<IDecisionGate>(),
            NullLogger<AgentStepExecutor>.Instance);
    }

    private static StepExecutionContext Context(AgentType agentType)
    {
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 7,
            StepType = StepType.Agent,
            AgentInputContextMode = AgentInputContextMode.FullWorkflow,
            AgentDefinition = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = agentType.ToString(),
                AgentType = agentType,
                Description = string.Empty,
                SystemPrompt = string.Empty,
                IsBuiltIn = true
            }
        };

        Guid executionId = Guid.NewGuid();
        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = executionId, ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "correlation",
            CancellationToken = CancellationToken.None
        };
    }
}
