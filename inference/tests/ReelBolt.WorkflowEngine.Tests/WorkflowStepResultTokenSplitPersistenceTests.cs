using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// B3a: the input/output token split and the primary provider attribution a step reports must land on
/// the persisted <see cref="WorkflowStepResult"/> row, not only on the in-memory result. Runs the REAL
/// <see cref="WorkflowExecutorService.ExecuteAsync"/> with a stub executor per step type; the room
/// executors' own aggregation of the split is covered in EditRoomStepExecutorTests.
/// </summary>
public class WorkflowStepResultTokenSplitPersistenceTests
{
    private static readonly Guid ProviderId = Guid.NewGuid();

    private sealed class NoOpEventPublisher : IWorkflowEventPublisher
    {
        public Task PublishExecutionRunningAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;

        public Task PublishStepStartedAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string? inputPreview, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PublishStepCompletedAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult,
            StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;

        public Task PublishStepProgressAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult,
            string stage, int? percentComplete, CancellationToken ct,
            int? tokensUsedSoFar = null, int? inputTokensSoFar = null, int? outputTokensSoFar = null) => Task.CompletedTask;

        public Task PublishStepChatTurnAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult,
            int turnIndex, int? totalTurns, string speaker, string speakerRole, string text,
            IReadOnlyList<string> idsMentioned, CancellationToken ct) => Task.CompletedTask;

        public Task PublishStepDiagnosticsAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult,
            StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;

        public Task PublishExecutionCompletedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionFailedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class SplitReportingExecutor : IStepExecutor
    {
        private readonly int? _input;
        private readonly int? _output;
        public SplitReportingExecutor(StepType stepType, int? input, int? output)
        {
            StepType = stepType;
            _input = input;
            _output = output;
        }

        public StepType StepType { get; }

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) =>
            Task.FromResult(new StepExecutionResult
            {
                Output = "{\"ok\":true}",
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                TokensUsed = (_input ?? 0) + (_output ?? 0),
                InputTokens = _input,
                OutputTokens = _output,
                PrimaryProviderId = ProviderId,
                PrimaryModelName = "test-model",
                Status = StepStatus.Completed
            });
    }

    private static async Task<WorkflowStepResult> RunSingleStepAsync(StepType stepType, AgentType agentType, int? input, int? output)
    {
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new WorkflowEngineDbContext(options);

        Guid projectId = Guid.NewGuid();
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "A", AgentType = agentType, SystemPrompt = "" };
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = stepType,
            AgentDefinitionId = agent.Id,
            AgentDefinition = agent
        };
        WorkflowDefinition definition = new()
        {
            Id = Guid.NewGuid(),
            Name = "t",
            ProjectId = projectId,
            Steps = new List<WorkflowStep> { step }
        };
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definition.Id,
            ProjectId = projectId,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        };
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();

        var service = new WorkflowExecutorService(
            scopeFactory: new FakeScopeFactory(db),
            eventPublisher: new NoOpEventPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: [new SplitReportingExecutor(stepType, input, output)],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry());

        await service.ExecuteAsync(execution.Id, "corr", CancellationToken.None);

        return await db.WorkflowStepResults.SingleAsync(r => r.WorkflowExecutionId == execution.Id);
    }

    [Fact]
    public async Task Agent_step_persists_the_token_split_and_primary_provider()
    {
        WorkflowStepResult row = await RunSingleStepAsync(StepType.Agent, AgentType.VideoStoryEditor, 1200, 300);

        row.Status.Should().Be(StepStatus.Completed);
        row.TokensUsed.Should().Be(1500);
        row.InputTokens.Should().Be(1200);
        row.OutputTokens.Should().Be(300);
        row.PrimaryProviderId.Should().Be(ProviderId);
        row.PrimaryModelName.Should().Be("test-model");
    }

    [Fact]
    public async Task Room_step_persists_the_token_split_and_primary_provider()
    {
        WorkflowStepResult row = await RunSingleStepAsync(StepType.EditRoom, AgentType.VideoTransform, 5000, 800);

        row.TokensUsed.Should().Be(5800);
        row.InputTokens.Should().Be(5000);
        row.OutputTokens.Should().Be(800);
        row.PrimaryProviderId.Should().Be(ProviderId);
        row.PrimaryModelName.Should().Be("test-model");
    }

    [Fact]
    public async Task A_step_that_reports_no_usage_persists_null_not_zero()
    {
        WorkflowStepResult row = await RunSingleStepAsync(StepType.Agent, AgentType.VideoStoryEditor, null, null);

        row.InputTokens.Should().BeNull();
        row.OutputTokens.Should().BeNull();
    }
}
