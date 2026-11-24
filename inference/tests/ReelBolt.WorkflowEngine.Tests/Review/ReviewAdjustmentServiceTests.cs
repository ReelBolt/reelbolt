using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Review;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Services.Review;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// Review-loop adjustments through the REAL <see cref="WorkflowExecutorService"/> (docs/design/review-loop-adjustments.md):
/// an applied change lands on the run's own snapshot and never on the saved workflow, the loop re-runs from the changed
/// analysis, and the paid step in between is reused (a copied row), never re-run.
/// </summary>
public class ReviewAdjustmentServiceTests
{
    private sealed class CountingExecutor(StepType type, string output) : IStepExecutor
    {
        public StepType StepType => type;
        public List<string?> Configs { get; } = [];

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            Configs.Add(type == StepType.VideoAnalyze ? context.Step.VideoAnalyzeConfigJson : context.Step.VideoCompileConfigJson);
            return Task.FromResult(new StepExecutionResult
            {
                Output = output,
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed
            });
        }
    }

    /// <summary>First pass: asks for a bigger analysis budget and loops back; second pass: passes.</summary>
    private sealed class AdjustingReviewExecutor : IStepExecutor
    {
        public StepType StepType => StepType.ReviewLoop;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            int iteration = context.IterationCount + 1;
            if (iteration > 1)
            {
                return Task.FromResult(new StepExecutionResult
                {
                    Output = """{"score":9,"passesReview":true,"summary":"ok"}""",
                    NextStepIndex = context.CurrentStepIndex + 1,
                    NewIterationCount = iteration,
                    IterationNumber = iteration,
                    Status = StepStatus.Completed
                });
            }

            ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(
                ReviewLoopStepExecutor.AdjustmentRequest(context, loopsBack: true),
                [new ReviewAdjustmentProposal(1, "maxOutputChars", JsonValue.Create("60000"), "placements dropped for budget")]);
            int next = context.AllSteps.FindIndex(s => s.StepOrder == plan.RerunFromStepOrder);
            return Task.FromResult(new StepExecutionResult
            {
                Output = ReviewAdjustmentReport.AttachToOutput("""{"score":6,"passesReview":false,"summary":"no title"}""", plan, AgentType.VideoReviewAgent),
                NextStepIndex = next,
                NewIterationCount = iteration,
                IterationNumber = iteration,
                Status = StepStatus.Completed,
                ReviewAdjustments = plan
            });
        }
    }

    [Fact]
    public async Task An_applied_change_lands_on_the_run_snapshot_only_and_the_paid_step_between_is_reused()
    {
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using WorkflowEngineDbContext db = new(options);

        Guid projectId = Guid.NewGuid();
        Guid definitionId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();
        AgentDefinition transform = new() { Id = Guid.NewGuid(), Name = "Transform", AgentType = AgentType.VideoTransform, SystemPrompt = "" };
        AgentDefinition room = new() { Id = Guid.NewGuid(), Name = "Room", AgentType = AgentType.VideoEditDirector, SystemPrompt = "" };
        AgentDefinition reviewer = new() { Id = Guid.NewGuid(), Name = "Review", AgentType = AgentType.VideoReviewAgent, SystemPrompt = "" };
        const string analyzeConfig = """{"transcription":"Off","vision":"Off","maxOutputChars":24000}""";

        WorkflowStep S(int order, StepType type, AgentDefinition agent) => new()
        {
            Id = Guid.NewGuid(),
            StepOrder = order,
            StepType = type,
            AgentDefinitionId = agent.Id,
            AgentDefinition = agent,
            WorkflowDefinitionId = definitionId
        };
        WorkflowStep analyze = S(1, StepType.VideoAnalyze, transform);
        analyze.VideoAnalyzeConfigJson = analyzeConfig;
        WorkflowStep audio = S(2, StepType.AudioGenerate, transform);
        WorkflowStep editRoom = S(3, StepType.EditRoom, room);
        WorkflowStep compile = S(4, StepType.VideoCompile, transform);
        compile.VideoCompileConfigJson = """{"version":1}""";
        WorkflowStep review = S(5, StepType.ReviewLoop, reviewer);
        review.LoopTargetStepOrder = 3;
        review.MaxIterations = 3;
        review.MinScore = 8;
        review.ReviewLoopConfigJson = """{"allowAdjustments":true}""";

        WorkflowDefinition definition = new()
        {
            Id = definitionId,
            Name = "lume",
            ProjectId = projectId,
            Steps = [analyze, audio, editRoom, compile, review]
        };
        db.AgentDefinitions.AddRange(transform, room, reviewer);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = definitionId,
            ProjectId = projectId,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        });
        await db.SaveChangesAsync();

        CountingExecutor analyzer = new(StepType.VideoAnalyze, """{"view":{},"meta":{}}""");
        CountingExecutor audioGen = new(StepType.AudioGenerate, """{"takes":1}""");
        CountingExecutor rooms = new(StepType.EditRoom, """{"keep":[]}""");
        CountingExecutor compiler = new(StepType.VideoCompile, """{"status":"completed"}""");
        WorkflowExecutorService service = new(
            scopeFactory: new FakeScopeFactory(db),
            eventPublisher: new NoOpPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: [analyzer, audioGen, rooms, compiler, new AdjustingReviewExecutor()],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry());

        await service.ExecuteAsync(executionId, "corr", CancellationToken.None);

        WorkflowExecution done = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == executionId);
        done.Status.Should().Be(ExecutionStatus.Passed);

        // The analysis re-ran with the new budget; the paid step between it and the loop target ran once.
        analyzer.Configs.Should().HaveCount(2);
        JsonNode.Parse(analyzer.Configs[1]!)!["maxOutputChars"]!.GetValue<int>().Should().Be(60000);
        audioGen.Configs.Should().HaveCount(1, "a paid step is never re-entered by an adjustment");
        rooms.Configs.Should().HaveCount(2);

        // Snapshot only: the run's copy changed, the saved workflow did not.
        WorkflowStep savedAnalyze = await db.WorkflowSteps.AsNoTracking().SingleAsync(s => s.Id == analyze.Id);
        savedAnalyze.VideoAnalyzeConfigJson.Should().Be(analyzeConfig);
        WorkflowStep snapshotAnalyze = await db.WorkflowSteps.AsNoTracking()
            .SingleAsync(s => s.WorkflowDefinitionId == done.SnapshotDefinitionId && s.StepOrder == 1);
        JsonNode.Parse(snapshotAnalyze.VideoAnalyzeConfigJson!)!["maxOutputChars"]!.GetValue<int>().Should().Be(60000);

        // The reused step has a copied row pointing at the row it copies, and nothing was charged for it.
        Guid snapshotAudioId = await db.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowDefinitionId == done.SnapshotDefinitionId && s.StepOrder == 2).Select(s => s.Id).SingleAsync();
        List<WorkflowStepResult> audioRows = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == executionId && r.WorkflowStepId == snapshotAudioId).OrderBy(r => r.ExecutedAt).ToListAsync();
        audioRows.Should().HaveCount(2);
        audioRows[1].ReusedFromStepResultId.Should().Be(audioRows[0].Id);
        audioRows[1].CreditsChargedMillis.Should().Be(0);
    }

    private sealed class NoOpPublisher : IWorkflowEventPublisher
    {
        public Task PublishExecutionRunningAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepStartedAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string? inputPreview, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepCompletedAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepProgressAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string stage, int? percentComplete, CancellationToken ct, int? tokensUsedSoFar = null, int? inputTokensSoFar = null, int? outputTokensSoFar = null) => Task.CompletedTask;
        public Task PublishStepChatTurnAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, int turnIndex, int? totalTurns, string speaker, string speakerRole, string text, IReadOnlyList<string> idsMentioned, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepDiagnosticsAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionCompletedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionFailedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
    }
}
