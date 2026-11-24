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
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// End-to-end regression coverage for bug group A (StepOutputHistory duplication on a ReviewLoop
/// loop-back — see CLAUDE.md "Video Editing" and docs/video-editing.md "Review loop"). Builds the
/// exact shape of the shipped video-derush-edit template's tail — StepType.Agent
/// (AgentType.VideoStoryEditor, AgentInputContextMode.PreviousStepOnly) -> StepType.VideoCompile ->
/// StepType.ReviewLoop (AgentType.VideoReviewAgent) looping back to the story editor — with stub
/// executors, and runs it through the REAL WorkflowExecutorService.ExecuteAsync. This exercises
/// StepOutputHistory pruning (WorkflowExecutorService, bug A.3) and PreviousStepOnly resolution
/// (StepExecutionContext, bug A.1) together, exactly as they run in production — not just each
/// fix in isolation.
/// </summary>
public class WorkflowExecutorServiceReviewLoopTests
{
    private const string AnalysisView = "{\"view\":{\"shots\":[{\"id\":\"s1\"},{\"id\":\"s2\"}]},\"meta\":{}}";

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

    // Step 1 (VideoAnalyze): always returns the same fixed "analysis view" output — deterministic,
    // never re-run on loop-back in the real template either (LoopTargetStepOrder points at the
    // story editor, step 2, not the analyze step).
    private sealed class FixedOutputExecutor : IStepExecutor
    {
        private readonly string _output;
        public FixedOutputExecutor(StepType stepType, string output) { StepType = stepType; _output = output; }
        public StepType StepType { get; }

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) =>
            Task.FromResult(new StepExecutionResult
            {
                Output = _output,
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed
            });
    }

    // Step 2 (Agent/VideoStoryEditor, PreviousStepOnly): records the resolved input it was
    // actually given on each call (via the real BuildAgentInput, exactly as AgentStepExecutor
    // does), so the test can assert what the RE-ENTERED story editor received on iteration 2.
    private sealed class RecordingAgentExecutor : IStepExecutor
    {
        public List<string> ReceivedInputs { get; } = new();
        public StepType StepType => StepType.Agent;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            ReceivedInputs.Add(context.BuildAgentInput());
            return Task.FromResult(new StepExecutionResult
            {
                Output = "{\"keep\":[{\"fromId\":\"s1\",\"toId\":\"s1\"}]}",
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed
            });
        }
    }

    // Step 4 (ReviewLoop/VideoReviewAgent): loops back to step 2 (index 1) on the first pass,
    // mirroring MinScore never being met once — then passes through on the second.
    private sealed class LoopingReviewExecutor : IStepExecutor
    {
        public StepType StepType => StepType.ReviewLoop;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            int newIteration = context.IterationCount + 1;
            bool loopBack = newIteration < 2;
            return Task.FromResult(new StepExecutionResult
            {
                Output = "{\"score\":3,\"passesReview\":false,\"summary\":\"needs another pass\"}",
                NextStepIndex = loopBack ? 1 : context.CurrentStepIndex + 1,
                NewIterationCount = newIteration,
                Status = StepStatus.Completed,
                IterationNumber = newIteration
            });
        }
    }

    [Fact]
    public async Task ReviewLoop_loop_back_feeds_the_re_entered_agent_the_original_analysis_view_not_the_reviews_own_output()
    {
        var options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new WorkflowEngineDbContext(options);

        Guid projectId = Guid.NewGuid();
        Guid workflowDefId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        AgentDefinition analyzeAgent = new()
        { Id = Guid.NewGuid(), Name = "Analyze", AgentType = AgentType.VideoTransform, SystemPrompt = "" };
        AgentDefinition storyEditorAgent = new()
        { Id = Guid.NewGuid(), Name = "StoryEditor", AgentType = AgentType.VideoStoryEditor, SystemPrompt = "" };
        AgentDefinition compileAgent = new()
        { Id = Guid.NewGuid(), Name = "Compile", AgentType = AgentType.VideoTransform, SystemPrompt = "" };
        AgentDefinition reviewAgent = new()
        { Id = Guid.NewGuid(), Name = "Review", AgentType = AgentType.VideoReviewAgent, SystemPrompt = "" };

        WorkflowStep step1 = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            AgentDefinitionId = analyzeAgent.Id,
            AgentDefinition = analyzeAgent
        };
        WorkflowStep step2 = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.Agent,
            AgentDefinitionId = storyEditorAgent.Id,
            AgentDefinition = storyEditorAgent,
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly
        };
        WorkflowStep step3 = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoCompile,
            AgentDefinitionId = compileAgent.Id,
            AgentDefinition = compileAgent
        };
        WorkflowStep step4 = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.ReviewLoop,
            AgentDefinitionId = reviewAgent.Id,
            AgentDefinition = reviewAgent,
            LoopTargetStepOrder = 2,
            MaxIterations = 2,
            MinScore = 7,
            AgentInputContextMode = AgentInputContextMode.FullWorkflow
        };

        WorkflowDefinition definition = new()
        {
            Id = workflowDefId,
            Name = "test",
            ProjectId = projectId,
            Steps = new List<WorkflowStep> { step1, step2, step3, step4 }
        };

        WorkflowExecution execution = new()
        {
            Id = executionId,
            WorkflowDefinitionId = workflowDefId,
            ProjectId = projectId,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        };

        db.AgentDefinitions.AddRange(analyzeAgent, storyEditorAgent, compileAgent, reviewAgent);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();

        var scopeFactory = new FakeScopeFactory(db);
        var recordingAgentExecutor = new RecordingAgentExecutor();
        IStepExecutor[] executors =
        {
            new FixedOutputExecutor(StepType.VideoAnalyze, AnalysisView),
            recordingAgentExecutor,
            new FixedOutputExecutor(StepType.VideoCompile, "{\"status\":\"completed\"}"),
            new LoopingReviewExecutor()
        };

        var service = new WorkflowExecutorService(
            scopeFactory: scopeFactory,
            eventPublisher: new NoOpEventPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: executors,
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
                cancellationRegistry: new ReelBolt.WorkflowEngine.Execution.ExecutionCancellationRegistry());

        await service.ExecuteAsync(executionId, "corr-1", CancellationToken.None);

        // The story editor (step 2) must have run exactly twice: once per ReviewLoop iteration.
        recordingAgentExecutor.ReceivedInputs.Should().HaveCount(2);

        // BOTH iterations must resolve PreviousStepOnly to the VideoAnalyze step's ORIGINAL
        // output — never the ReviewLoop step's own JSON verdict from the prior iteration. This is
        // the actual bug: without the fix, the second call's history lookup would instead hand the
        // story editor the review's {"score":3,...} output (either because StepOutputHistory still
        // held the stale duplicate, or because "most recently appended" resolved to whatever ran
        // last rather than "the step immediately before me").
        recordingAgentExecutor.ReceivedInputs[0].Should().Contain(AnalysisView);
        recordingAgentExecutor.ReceivedInputs[1].Should().StartWith(AnalysisView);
        recordingAgentExecutor.ReceivedInputs[1].Should().NotContain("passesReview");

        WorkflowExecution? completed = await db.WorkflowExecutions.FindAsync(executionId);
        completed.Should().NotBeNull();
        completed!.Status.Should().Be(ExecutionStatus.Passed);

        // The compile step (step 3) also ran twice — once per iteration — and its own
        // StepOutputHistory-based resolution (AnalysisStepOrder=1) must likewise still see the
        // VideoAnalyze step's single original entry, not a stale/duplicate one.
        // The run executes its own snapshot of the workflow, so its results reference the snapshot's
        // copy of step 3 (same order), not step3.Id.
        Guid runStep3Id = await db.WorkflowSteps
            .Where(s => s.WorkflowDefinitionId == completed.SnapshotDefinitionId && s.StepOrder == step3.StepOrder)
            .Select(s => s.Id)
            .SingleAsync();
        List<WorkflowStepResult> compileResults = await db.WorkflowStepResults
            .Where(r => r.WorkflowExecutionId == executionId && r.WorkflowStepId == runStep3Id)
            .ToListAsync();
        compileResults.Should().HaveCount(2);
    }

    // A step that forgets to carry the iteration count — exactly what the Voiceover step did.
    private sealed class ForgetfulExecutor : IStepExecutor
    {
        public StepType StepType => StepType.VideoCompile;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) =>
            Task.FromResult(new StepExecutionResult
            {
                Output = "{\"status\":\"completed\"}",
                NextStepIndex = context.CurrentStepIndex + 1,
                Status = StepStatus.Completed
            });
    }

    // A reviewer that never passes, with the real executor's rule: loop back while the new
    // iteration is under the step's own MaxIterations.
    private sealed class NeverPassingReviewExecutor : IStepExecutor
    {
        public StepType StepType => StepType.ReviewLoop;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            int newIteration = context.IterationCount + 1;
            bool loopBack = newIteration < context.Step.MaxIterations;
            return Task.FromResult(new StepExecutionResult
            {
                Output = "{\"score\":3,\"passesReview\":false}",
                NextStepIndex = loopBack ? 1 : context.CurrentStepIndex + 1,
                NewIterationCount = newIteration,
                Status = StepStatus.Completed,
                IterationNumber = newIteration
            });
        }
    }

    private sealed class CountingAgentExecutor : IStepExecutor
    {
        public int Calls { get; private set; }
        public StepType StepType => StepType.Agent;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            // Safety stop so the pre-fix behaviour fails the test instead of hanging the suite.
            if (++Calls > 10)
                throw new InvalidOperationException("ReviewLoop never stopped: the iteration cap was not enforced.");
            return Task.FromResult(new StepExecutionResult
            {
                Output = "{\"lines\":[]}",
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed
            });
        }
    }

    [Fact]
    public async Task ReviewLoop_stops_at_its_MaxIterations_even_when_a_step_inside_the_loop_drops_the_count()
    {
        // Observed live: a narrated workflow (… → NarrationWriter → Voiceover → VideoCompile →
        // ReviewLoop MaxIterations 2) went round ~35 times. The Voiceover step returned
        // NewIterationCount 0 and the runner took it, so every pass restarted the count.
        var options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new WorkflowEngineDbContext(options);
        Guid projectId = Guid.NewGuid(), workflowDefId = Guid.NewGuid(), executionId = Guid.NewGuid();

        AgentDefinition plain = new() { Id = Guid.NewGuid(), Name = "Plain", AgentType = AgentType.VideoTransform, SystemPrompt = "" };
        AgentDefinition writer = new() { Id = Guid.NewGuid(), Name = "Writer", AgentType = AgentType.NarrationWriter, SystemPrompt = "" };
        AgentDefinition reviewer = new() { Id = Guid.NewGuid(), Name = "Review", AgentType = AgentType.VideoReviewAgent, SystemPrompt = "" };
        var steps = new List<WorkflowStep>
        {
            new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoAnalyze, AgentDefinitionId = plain.Id, AgentDefinition = plain },
            new() { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.Agent, AgentDefinitionId = writer.Id, AgentDefinition = writer },
            new() { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile, AgentDefinitionId = plain.Id, AgentDefinition = plain },
            new()
            {
                Id = Guid.NewGuid(), StepOrder = 4, StepType = StepType.ReviewLoop, AgentDefinitionId = reviewer.Id,
                AgentDefinition = reviewer, LoopTargetStepOrder = 2, MaxIterations = 2, MinScore = 7
            }
        };
        WorkflowDefinition definition = new() { Id = workflowDefId, Name = "test", ProjectId = projectId, Steps = steps };
        db.AgentDefinitions.AddRange(plain, writer, reviewer);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = workflowDefId,
            ProjectId = projectId,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        });
        await db.SaveChangesAsync();

        var agent = new CountingAgentExecutor();
        var service = new WorkflowExecutorService(
            scopeFactory: new FakeScopeFactory(db),
            eventPublisher: new NoOpEventPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: [new FixedOutputExecutor(StepType.VideoAnalyze, AnalysisView), agent, new ForgetfulExecutor(), new NeverPassingReviewExecutor()],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ReelBolt.WorkflowEngine.Execution.ExecutionCancellationRegistry());

        await service.ExecuteAsync(executionId, "corr-1", CancellationToken.None);

        agent.Calls.Should().Be(2, "MaxIterations 2 allows the first pass and one loop back");
        (await db.WorkflowExecutions.FindAsync(executionId))!.Status.Should().NotBe(ExecutionStatus.Running);
    }

    private sealed class ExhaustedReviewExecutor(int score) : IStepExecutor
    {
        public StepType StepType => StepType.ReviewLoop;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) =>
            Task.FromResult(new StepExecutionResult
            {
                Output = "{\"score\":" + score + "}",
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = 3,
                IterationNumber = 3,
                Status = StepStatus.Completed,
                ReviewExhausted = score < 8 ? new ReviewExhausted(context.Step.StepOrder, score, 8, 3) : null
            });
    }

    private static async Task<WorkflowExecution> RunReviewAsync(int score, string? staleAttention, int? resumeScore = null, bool invalidateReview = false)
    {
        var options = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new WorkflowEngineDbContext(options);
        Guid projectId = Guid.NewGuid(), workflowDefId = Guid.NewGuid(), executionId = Guid.NewGuid();
        AgentDefinition a1 = new() { Id = Guid.NewGuid(), Name = "A", AgentType = AgentType.VideoTransform, SystemPrompt = "" };
        AgentDefinition a2 = new() { Id = Guid.NewGuid(), Name = "R", AgentType = AgentType.VideoReviewAgent, SystemPrompt = "" };
        WorkflowStep s1 = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoAnalyze, AgentDefinitionId = a1.Id, AgentDefinition = a1 };
        WorkflowStep s2 = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.ReviewLoop,
            AgentDefinitionId = a2.Id,
            AgentDefinition = a2,
            LoopTargetStepOrder = 1,
            MaxIterations = 3,
            MinScore = 8,
            AgentInputContextMode = AgentInputContextMode.FullWorkflow
        };
        WorkflowStep s3 = new() { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoAnalyze, AgentDefinitionId = a1.Id, AgentDefinition = a1 };
        WorkflowDefinition definition = new() { Id = workflowDefId, Name = "t", ProjectId = projectId, Steps = resumeScore is not null ? [s1, s2, s3] : [s1, s2] };
        db.AgentDefinitions.AddRange(a1, a2);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = workflowDefId,
            ProjectId = projectId,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition,
            AttentionJson = staleAttention
        });
        await db.SaveChangesAsync();

        WorkflowExecutorService NewService(int reviewScore) => new(
            scopeFactory: new FakeScopeFactory(db),
            eventPublisher: new NoOpEventPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: [new FixedOutputExecutor(StepType.VideoAnalyze, AnalysisView), new ExhaustedReviewExecutor(reviewScore)],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry());
        await NewService(score).ExecuteAsync(executionId, "corr", CancellationToken.None);
        if (resumeScore is { } second)
        {
            // Resume the finished run: its recorded results are reused until the first the new review score replaces.
            WorkflowExecution done = (await db.WorkflowExecutions.FindAsync(executionId))!;
            done.Status = ExecutionStatus.Queued;
            done.PendingResume = true;
            if (invalidateReview)
            {
                foreach (WorkflowStepResult r in db.WorkflowStepResults.Where(r => r.WorkflowExecutionId == executionId && r.Status == StepStatus.Completed).ToList()
                    .Where(r => r.IterationNumber is not null))
                    r.Status = StepStatus.Failed;
            }

            await db.SaveChangesAsync();
            await NewService(second).ExecuteAsync(executionId, "corr", CancellationToken.None);
        }
        return (await db.WorkflowExecutions.FindAsync(executionId))!;
    }

    [Fact]
    public async Task An_exhausted_review_still_writes_Passed_and_records_attention_json()
    {
        WorkflowExecution run = await RunReviewAsync(score: 5, staleAttention: null);

        run.Status.Should().Be(ExecutionStatus.Passed);
        run.AttentionJson.Should().Be("""{"reason":"review_below_minimum","stepOrder":2,"score":5,"minScore":8,"iterations":3}""");
    }

    [Fact]
    public async Task A_passing_review_leaves_attention_null_and_a_new_attempt_clears_a_stale_value()
    {
        WorkflowExecution run = await RunReviewAsync(score: 9,
            staleAttention: """{"reason":"review_below_minimum","stepOrder":2,"score":1,"minScore":8,"iterations":3}""");

        run.Status.Should().Be(ExecutionStatus.Passed);
        run.AttentionJson.Should().BeNull();
    }

    private const string Prior = """{"reason":"review_below_minimum","stepOrder":2,"score":5,"minScore":8,"iterations":3}""";

    [Fact]
    public async Task A_resume_that_reuses_the_exhausted_review_keeps_the_attention()
    {
        WorkflowExecution run = await RunReviewAsync(score: 5, staleAttention: null, resumeScore: 3);

        run.Status.Should().Be(ExecutionStatus.Passed);
        run.AttentionJson.Should().Be(Prior);
    }

    [Fact]
    public async Task A_resume_that_re_runs_the_review_and_passes_clears_the_attention()
    {
        WorkflowExecution run = await RunReviewAsync(score: 5, staleAttention: null, resumeScore: 9, invalidateReview: true);

        run.Status.Should().Be(ExecutionStatus.Passed);
        run.AttentionJson.Should().BeNull();
    }

    [Fact]
    public async Task A_retry_clears_the_attention_even_when_its_rows_exist()
    {
        WorkflowExecution run = await RunReviewAsync(score: 9, staleAttention: Prior);

        run.AttentionJson.Should().BeNull();
    }

    // ==========================================================================================
    // narration_incomplete — a Passed run whose compile shipped missing narration
    // ==========================================================================================

    private const string CompileOutputWithHalfTheNarration =
        """{"status":"completed","voiceover":{"applied":true,"lineCount":4,"appliedLineCount":2,"reanchoredLineCount":1}}""";

    private static async Task<WorkflowExecution> RunCompileThenReviewAsync(string compileOutput, int reviewScore)
    {
        var options = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new WorkflowEngineDbContext(options);
        Guid projectId = Guid.NewGuid(), workflowDefId = Guid.NewGuid(), executionId = Guid.NewGuid();
        AgentDefinition a1 = new() { Id = Guid.NewGuid(), Name = "A", AgentType = AgentType.VideoTransform, SystemPrompt = "" };
        AgentDefinition a2 = new() { Id = Guid.NewGuid(), Name = "R", AgentType = AgentType.VideoReviewAgent, SystemPrompt = "" };
        WorkflowStep s1 = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoCompile, AgentDefinitionId = a1.Id, AgentDefinition = a1 };
        WorkflowStep s2 = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.ReviewLoop,
            AgentDefinitionId = a2.Id,
            AgentDefinition = a2,
            LoopTargetStepOrder = 1,
            MaxIterations = 3,
            MinScore = 8,
            AgentInputContextMode = AgentInputContextMode.FullWorkflow
        };
        WorkflowDefinition definition = new() { Id = workflowDefId, Name = "t", ProjectId = projectId, Steps = [s1, s2] };
        db.AgentDefinitions.AddRange(a1, a2);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = workflowDefId,
            ProjectId = projectId,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        });
        await db.SaveChangesAsync();

        var service = new WorkflowExecutorService(
            scopeFactory: new FakeScopeFactory(db),
            eventPublisher: new NoOpEventPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: [new FixedOutputExecutor(StepType.VideoCompile, compileOutput), new ExhaustedReviewExecutor(reviewScore)],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry());

        await service.ExecuteAsync(executionId, "corr", CancellationToken.None);
        return (await db.WorkflowExecutions.FindAsync(executionId))!;
    }

    [Fact]
    public async Task A_passed_run_with_two_of_four_narration_lines_spoken_records_narration_incomplete_attention()
    {
        WorkflowExecution run = await RunCompileThenReviewAsync(CompileOutputWithHalfTheNarration, reviewScore: 9);

        run.Status.Should().Be(ExecutionStatus.Passed, "a mostly-spoken film still ships; it must not read as a clean pass though");
        ExecutionAttention? attention = ExecutionAttention.TryParse(run.AttentionJson);
        attention.Should().NotBeNull();
        attention!.Reason.Should().Be(ExecutionAttention.NarrationIncompleteReason);
        attention.StepOrder.Should().Be(1, "the VideoCompile step that produced the voiceover node");
        attention.Message.Should().Be(
            "Only 2 of 4 narration lines were spoken in the film. 1 of them was re-anchored to a nearby kept clip. "
            + "The film is ready but the narration is incomplete.");
        attention.Message.Should().NotContain("did not reach the quality bar");
    }

    [Fact]
    public async Task A_passed_review_over_complete_narration_still_leaves_attention_null()
    {
        WorkflowExecution run = await RunCompileThenReviewAsync(
            """{"status":"completed","voiceover":{"applied":true,"lineCount":4,"appliedLineCount":4}}""", reviewScore: 9);

        run.Status.Should().Be(ExecutionStatus.Passed);
        run.AttentionJson.Should().BeNull();
    }

    [Fact]
    public async Task Narration_loss_wins_the_attention_slot_over_a_review_exhaustion()
    {
        WorkflowExecution run = await RunCompileThenReviewAsync(CompileOutputWithHalfTheNarration, reviewScore: 5);

        run.Status.Should().Be(ExecutionStatus.Passed);
        ExecutionAttention? attention = ExecutionAttention.TryParse(run.AttentionJson);
        attention!.Reason.Should().Be(ExecutionAttention.NarrationIncompleteReason, "the missing script is the real problem");
        attention.Score.Should().Be(5, "the review score is still carried in the same field");
    }

    // ==========================================================================================
    // Only the NEWEST compile's narration counts: an older incomplete compile must not override
    // the render that actually shipped (QA finding on T-011).
    // ==========================================================================================

    private const string CompileOutputWithAllTheNarration =
        """{"status":"completed","voiceover":{"applied":true,"lineCount":4,"appliedLineCount":4}}""";

    private sealed class PerStepOutputExecutor(StepType stepType, IReadOnlyDictionary<int, string> outputByStepOrder) : IStepExecutor
    {
        public StepType StepType => stepType;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) =>
            Task.FromResult(new StepExecutionResult
            {
                Output = outputByStepOrder[context.Step.StepOrder],
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed
            });
    }

    /// <summary>
    /// A workflow with TWO VideoCompile steps (1 and 3) around a passing ReviewLoop (2), so history
    /// holds both compile outputs in completion order — exactly the shape a second compile or an
    /// earlier attempt leaves behind.
    /// </summary>
    private static async Task<WorkflowExecution> RunTwoCompilesAsync(
        string firstCompileOutput, string secondCompileOutput, int reviewScore)
    {
        var options = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new WorkflowEngineDbContext(options);
        Guid projectId = Guid.NewGuid(), workflowDefId = Guid.NewGuid(), executionId = Guid.NewGuid();
        AgentDefinition a1 = new() { Id = Guid.NewGuid(), Name = "A", AgentType = AgentType.VideoTransform, SystemPrompt = "" };
        AgentDefinition a2 = new() { Id = Guid.NewGuid(), Name = "R", AgentType = AgentType.VideoReviewAgent, SystemPrompt = "" };
        WorkflowStep s1 = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoCompile, AgentDefinitionId = a1.Id, AgentDefinition = a1 };
        WorkflowStep s2 = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.ReviewLoop,
            AgentDefinitionId = a2.Id,
            AgentDefinition = a2,
            LoopTargetStepOrder = 1,
            MaxIterations = 3,
            MinScore = 8,
            AgentInputContextMode = AgentInputContextMode.FullWorkflow
        };
        WorkflowStep s3 = new() { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile, AgentDefinitionId = a1.Id, AgentDefinition = a1 };
        WorkflowDefinition definition = new() { Id = workflowDefId, Name = "t", ProjectId = projectId, Steps = [s1, s2, s3] };
        db.AgentDefinitions.AddRange(a1, a2);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = workflowDefId,
            ProjectId = projectId,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        });
        await db.SaveChangesAsync();

        var service = new WorkflowExecutorService(
            scopeFactory: new FakeScopeFactory(db),
            eventPublisher: new NoOpEventPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors:
            [
                new PerStepOutputExecutor(StepType.VideoCompile, new Dictionary<int, string>
                {
                    [1] = firstCompileOutput,
                    [3] = secondCompileOutput
                }),
                new ExhaustedReviewExecutor(reviewScore)
            ],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry());

        await service.ExecuteAsync(executionId, "corr", CancellationToken.None);
        return (await db.WorkflowExecutions.FindAsync(executionId))!;
    }

    [Fact]
    public async Task An_older_incomplete_compile_does_not_override_a_complete_newest_compile()
    {
        WorkflowExecution run = await RunTwoCompilesAsync(
            firstCompileOutput: CompileOutputWithHalfTheNarration,
            secondCompileOutput: CompileOutputWithAllTheNarration,
            reviewScore: 9);

        run.Status.Should().Be(ExecutionStatus.Passed);
        run.AttentionJson.Should().BeNull(
            "the newest compile is the film that shipped, and it spoke every declared line");
    }

    [Fact]
    public async Task An_incomplete_newest_compile_still_records_attention_from_that_compile()
    {
        WorkflowExecution run = await RunTwoCompilesAsync(
            firstCompileOutput: CompileOutputWithAllTheNarration,
            secondCompileOutput: CompileOutputWithHalfTheNarration,
            reviewScore: 9);

        run.Status.Should().Be(ExecutionStatus.Passed);
        ExecutionAttention? attention = ExecutionAttention.TryParse(run.AttentionJson);
        attention.Should().NotBeNull();
        attention!.Reason.Should().Be(ExecutionAttention.NarrationIncompleteReason);
        attention.StepOrder.Should().Be(3, "the counts come from the NEWEST compile, not an earlier one");
        attention.Message.Should().Contain("Only 2 of 4 narration lines were spoken");
    }
}
