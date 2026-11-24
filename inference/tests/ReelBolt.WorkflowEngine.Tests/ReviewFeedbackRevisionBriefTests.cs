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
using ReelBolt.WorkflowEngine.Execution.Context;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The review loop's feedback path to the Author (saas-quality-fixes T-004). In the Luna promo runs
/// iteration 2's render was byte-identical to iteration 1's: the Author was handed only the reviewer's
/// summary and improvement areas, under "Retry Guidance", followed by a sentence telling it to fix its
/// JSON. These tests pin what a loop-back Author now reads: its own heading with the score and the
/// pass mark, every criterion's feedback, the scene list of the render that was reviewed, and no
/// schema-retry sentence.
/// </summary>
public class ReviewFeedbackRevisionBriefTests
{
    /// <summary>A ReviewAgent verdict in the ReviewOutput shape, written out by hand.</summary>
    private const string ReviewerJson = """
        {
          "overallScore": 6,
          "criteria": [
            { "name": "narrativeClarity", "score": 5, "feedback": "The hook and the payoff show the same dashboard mockup, so the viewer never sees what changed between scene 1 and scene 4." },
            { "name": "visualAccuracy", "score": 7, "feedback": "The invoice table matches the real screen, but the sidebar labels are placeholder text." },
            { "name": "timing", "score": 6, "feedback": "Scene 3 holds a static frame for 4.5 s with no motion; cut it to about 2 s or animate the chart." },
            { "name": "completeness", "score": 6, "feedback": "The brief asks for a call to action and the end card shows the logo only." }
          ],
          "strengths": ["Consistent brand colours"],
          "improvementAreas": [
            "Give each scene its own screen instead of reusing the hero mockup",
            "End on a card with the call to action 'Start free'"
          ],
          "passesReview": false,
          "summary": "Clean and on-brand, but the same mockup repeats in every scene and the film ends without a call to action."
        }
        """;

    /// <summary>The Author's RenderManifestOutput for the render the reviewer judged.</summary>
    private const string AuthorManifestJson = """
        {
          "projectName": "Ledgerly promo",
          "video": { "width": 1080, "height": 1920, "fps": 30, "durationInFrames": 450 },
          "compositions": [
            { "id": "Hook", "componentName": "HookScene", "durationInFrames": 90, "props": {}, "script": { "voiceover": "", "captions": ["Invoices, done."] } },
            { "id": "Dashboard", "componentName": "DashboardScene", "durationInFrames": 120, "props": {}, "script": { "voiceover": "", "captions": [] } },
            { "id": "Chart", "componentName": "ChartScene", "durationInFrames": 135, "props": {}, "script": { "voiceover": "", "captions": [] } },
            { "id": "EndCard", "componentName": "EndCardScene", "durationInFrames": 105, "props": {}, "script": { "voiceover": "", "captions": [] } }
          ],
          "assets": [ { "id": "final", "type": "video", "path": "projects/p/outputFiles/e/promo.mp4", "properties": {} } ],
          "metadata": { "finalRenderCompositionId": "Main" }
        }
        """;

    /// <summary>The Director's output the Author keeps as its upstream context.</summary>
    private const string DirectorOutput = "{\"shots\":[{\"scene\":\"Hook\"},{\"scene\":\"EndCard\"}]}";

    private const string SchemaSentence = "Return output that strictly matches the expected JSON schema and is valid JSON.";

    private const string Heading =
        "Reviewer feedback on your previous render (score 6/10, needs 8): revise the composition to address every point";

    // ------------------------------------------------------------------
    // ExtractFeedbackSummary
    // ------------------------------------------------------------------

    [Fact]
    public void A_ReviewAgent_verdict_keeps_the_score_the_pass_mark_and_every_criterion()
    {
        string? feedback = ReviewLoopStepExecutor.ExtractFeedbackSummary(ReviewerJson, minScore: 8);

        feedback.Should().NotBeNull();
        feedback.Should().Contain("Overall score: 6/10 (pass mark 8).");
        feedback.Should().Contain("Summary: Clean and on-brand");
        feedback.Should().Contain("- narrativeClarity 5/10: The hook and the payoff show the same dashboard mockup");
        feedback.Should().Contain("- visualAccuracy 7/10: The invoice table matches the real screen");
        feedback.Should().Contain("- timing 6/10: Scene 3 holds a static frame for 4.5 s");
        feedback.Should().Contain("- completeness 6/10: The brief asks for a call to action");
        feedback.Should().Contain("- Give each scene its own screen instead of reusing the hero mockup");
        feedback.Should().Contain("- End on a card with the call to action 'Start free'");
    }

    [Fact]
    public void A_VideoReviewAgent_verdict_keeps_its_one_line_summary_and_issues()
    {
        string? feedback = ReviewLoopStepExecutor.ExtractFeedbackSummary(
            """{"score":4,"passesReview":false,"issues":["ends mid-sentence"],"summary":"Cut ends mid-sentence."}""",
            minScore: 8);

        feedback.Should().Be("Cut ends mid-sentence. Issues: ends mid-sentence");
    }

    /// <summary>A compile output whose top-level interleave node is required and unsatisfied, so the interleave cap fires.</summary>
    private const string UnusedClipsCompileJson = """{"interleave":{"required":true,"satisfied":false},"segments":[]}""";

    /// <summary>A VideoReviewAgent verdict that scores 9 before any cap.</summary>
    private const string VideoNineJson =
        """{"score":9,"passesReview":true,"issues":["ends on a clean cut"],"summary":"Tight edit, uses the clips well."}""";

    [Fact]
    public void A_capped_review_scores_the_capped_number_and_names_the_cap()
    {
        ReviewLoopStepExecutor.CappedReviewScore scored = ReviewLoopStepExecutor.ScoreReview(VideoNineJson, UnusedClipsCompileJson);

        scored.ReviewerScore.Should().Be(9);
        scored.Score.Should().Be(6);
        scored.CapReason.Should().Be("offered clips left unused");
    }

    [Fact]
    public void An_uncapped_review_keeps_its_score_and_names_no_cap()
    {
        ReviewLoopStepExecutor.CappedReviewScore scored = ReviewLoopStepExecutor.ScoreReview(ReviewerJson, AuthorManifestJson);

        scored.ReviewerScore.Should().Be(6);
        scored.Score.Should().Be(6);
        scored.CapReason.Should().BeNull();
    }

    [Fact]
    public void A_capped_ReviewOutput_states_the_effective_score_and_the_cap_on_its_overall_line()
    {
        string reviewerNine = ReviewerJson.Replace("\"overallScore\": 6,", "\"overallScore\": 9,");
        ReviewLoopStepExecutor.CappedReviewScore scored = ReviewLoopStepExecutor.ScoreReview(reviewerNine, UnusedClipsCompileJson);

        string? feedback = ReviewLoopStepExecutor.ExtractFeedbackSummary(reviewerNine, minScore: 8, scored: scored);

        feedback.Should().Contain("Overall score: 6/10 (pass mark 8) (capped from 9: offered clips left unused).");
        feedback.Should().NotContain("Overall score: 9/10");
    }

    [Fact]
    public void A_capped_VideoReviewAgent_verdict_says_it_was_capped_ahead_of_its_summary()
    {
        ReviewLoopStepExecutor.CappedReviewScore scored = ReviewLoopStepExecutor.ScoreReview(VideoNineJson, UnusedClipsCompileJson);

        string? feedback = ReviewLoopStepExecutor.ExtractFeedbackSummary(VideoNineJson, minScore: 8, scored: scored);

        feedback.Should().Be("Score capped from 9: offered clips left unused. Tight edit, uses the clips well. Issues: ends on a clean cut");
    }

    // ------------------------------------------------------------------
    // BuildPreviousManifestNote
    // ------------------------------------------------------------------

    [Fact]
    public void The_previous_manifest_note_lists_each_composition_with_its_duration()
    {
        string? note = ReviewLoopStepExecutor.BuildPreviousManifestNote(AuthorManifestJson);

        note.Should().Be(
            "The render the reviewer judged (\"Ledgerly promo\", 450 frames at 30 fps = 15.0 s) was built from these compositions:\n" +
            "1. Hook (HookScene): 90 frames = 3.0 s\n" +
            "2. Dashboard (DashboardScene): 120 frames = 4.0 s\n" +
            "3. Chart (ChartScene): 135 frames = 4.5 s\n" +
            "4. EndCard (EndCardScene): 105 frames = 3.5 s");
    }

    [Fact]
    public void The_previous_manifest_note_is_bounded()
    {
        string compositions = string.Join(",", Enumerable.Range(0, 300).Select(i =>
            $"{{\"id\":\"Scene{i}-{new string('x', 40)}\",\"componentName\":\"Scene{i}Component\",\"durationInFrames\":30}}"));
        string manifest = $"{{\"projectName\":\"Big\",\"video\":{{\"fps\":30,\"durationInFrames\":9000}},\"compositions\":[{compositions}]}}";

        string? note = ReviewLoopStepExecutor.BuildPreviousManifestNote(manifest);

        note.Should().NotBeNull();
        note!.Length.Should().BeLessThanOrEqualTo(ReviewLoopStepExecutor.MaxPreviousManifestNoteChars);
        note.Should().Contain("more not listed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"score":3,"summary":"a review, not a manifest"}""")]
    [InlineData("""{"projectName":"x","compositions":[]}""")]
    public void Anything_that_is_not_a_manifest_with_compositions_gives_no_note(string? output)
    {
        ReviewLoopStepExecutor.BuildPreviousManifestNote(output).Should().BeNull();
    }

    // ------------------------------------------------------------------
    // StepExecutionContext rendering
    // ------------------------------------------------------------------

    private static StepExecutionContext Context(
        IReadOnlyList<StepOutputHistoryEntry>? history = null,
        AgentInputBudgetOptions? budget = null) => new()
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = new WorkflowStep { StepOrder = 10, StepType = StepType.Agent, AgentInputContextMode = AgentInputContextMode.FullWorkflow },
            AllSteps = [],
            AccumulatedOutput = "",
            CurrentStepIndex = 0,
            IterationCount = 1,
            CorrelationId = "test",
            StepOutputHistory = history ?? [],
            BudgetOptions = budget
        };

    [Fact]
    public void Review_feedback_has_its_own_heading_and_no_schema_sentence()
    {
        StepExecutionContext ctx = Context();
        ctx.RecordReviewFeedback(6, 8, "Overall score: 6/10 (pass mark 8).");

        string prompt = ctx.WithRetryGuidance("BASE");

        prompt.Should().Be("BASE\n\n---\n" + Heading + "\nOverall score: 6/10 (pass mark 8).");
    }

    [Fact]
    public void A_schema_retry_keeps_the_retry_guidance_block_and_its_schema_sentence()
    {
        StepExecutionContext ctx = Context();
        ctx.RecordRetryFeedback(2, "Output is not valid JSON.");

        ctx.WithRetryGuidance("BASE").Should().Be(
            "BASE\n\n---\nRetry Guidance:\n- Attempt 2: Output is not valid JSON.\n" + SchemaSentence);
    }

    [Fact]
    public void Review_feedback_and_a_schema_retry_compose_with_the_schema_sentence_only_after_the_schema_retry()
    {
        StepExecutionContext ctx = Context();
        ctx.RecordReviewFeedback(6, 8, "Overall score: 6/10 (pass mark 8).");
        ctx.RecordRetryFeedback(1, "Output is not valid JSON.");

        string prompt = ctx.WithRetryGuidance("BASE");

        prompt.Should().Be(
            "BASE\n\n---\n" + Heading + "\nOverall score: 6/10 (pass mark 8)." +
            "\n\n---\nRetry Guidance:\n- Attempt 1: Output is not valid JSON.\n" + SchemaSentence);
    }

    [Fact]
    public void Review_feedback_is_capped_at_6000_chars_and_schema_feedback_stays_at_2000()
    {
        StepExecutionContext ctx = Context();
        ctx.RecordReviewFeedback(6, 8, new string('r', 7000));
        ctx.RecordRetryFeedback(1, new string('s', 3000));

        string prompt = ctx.WithRetryGuidance("BASE");

        prompt.Should().Contain(new string('r', 6000));
        prompt.Should().NotContain(new string('r', 6001));
        prompt.Should().Contain(new string('s', 2000));
        prompt.Should().NotContain(new string('s', 2001));
    }

    [Fact]
    public void The_manifest_note_follows_the_review_feedback_and_is_bounded()
    {
        StepExecutionContext ctx = Context();
        ctx.RecordReviewFeedback(6, 8, "Overall score: 6/10 (pass mark 8).", new string('m', 5000));

        string prompt = ctx.WithRetryGuidance("BASE");

        prompt.Should().StartWith("BASE\n\n---\n" + Heading + "\nOverall score: 6/10 (pass mark 8).\n\n");
        prompt.Should().Contain(new string('m', ReviewLoopStepExecutor.MaxPreviousManifestNoteChars));
        prompt.Should().NotContain(new string('m', ReviewLoopStepExecutor.MaxPreviousManifestNoteChars + 1));
    }

    [Fact]
    public void Review_feedback_is_added_after_the_context_budget_so_the_budget_never_trims_it()
    {
        List<StepOutputHistoryEntry> history = Enumerable.Range(1, 6)
            .Select(i => new StepOutputHistoryEntry(i, $"Step {i}", new string((char)('a' + i), 5000)))
            .ToList();
        StepExecutionContext ctx = Context(history, new AgentInputBudgetOptions { Enabled = true, MaxInputChars = 8000, MaxDigestedStepChars = 500 });
        string feedback = "Overall score: 6/10 (pass mark 8).\n" + new string('r', 5500);
        ctx.RecordReviewFeedback(6, 8, feedback);

        string input = ctx.BuildAgentInput();

        ctx.LastBudgetSummary.Should().NotBeNull("the history is over budget, so the budget acted");
        input.Should().EndWith("\n\n---\n" + Heading + "\n" + feedback);
    }

    [Fact]
    public void The_RetryGuidance_property_carries_review_feedback_for_steps_that_read_it_directly()
    {
        StepExecutionContext ctx = Context();
        ctx.RecordReviewFeedback(6, 8, "Overall score: 6/10 (pass mark 8).");

        ctx.RetryGuidance.Should().Be(Heading + "\nOverall score: 6/10 (pass mark 8).");
    }

    // ------------------------------------------------------------------
    // The real loop: what the Author is actually handed on iteration 2
    // ------------------------------------------------------------------

    private sealed class FixedOutputExecutor(StepType stepType, string output) : IStepExecutor
    {
        public StepType StepType { get; } = stepType;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) =>
            Task.FromResult(new StepExecutionResult
            {
                Output = output,
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed
            });
    }

    /// <summary>The Author: records the input it was built exactly as AgentStepExecutor builds it.</summary>
    private sealed class RecordingAuthorExecutor(string output) : IStepExecutor
    {
        public List<string> ReceivedInputs { get; } = new();
        public StepType StepType => StepType.Agent;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            ReceivedInputs.Add(context.BuildAgentInput());
            return Task.FromResult(new StepExecutionResult
            {
                Output = output,
                OutputStorageKey = "projects/p/outputFiles/e/promo.mp4",
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed
            });
        }
    }

    /// <summary>The reviewer: fails the first render (6 &lt; 8) and loops back once, then passes through.</summary>
    private sealed class FailingOnceReviewExecutor(string reviewerOutput) : IStepExecutor
    {
        public StepType StepType => StepType.ReviewLoop;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            int newIteration = context.IterationCount + 1;
            bool loopBack = newIteration < context.Step.MaxIterations;
            int target = context.AllSteps.FindIndex(s => s.StepOrder == context.Step.LoopTargetStepOrder);
            return Task.FromResult(new StepExecutionResult
            {
                Output = reviewerOutput,
                NextStepIndex = loopBack ? target : context.CurrentStepIndex + 1,
                NewIterationCount = newIteration,
                Status = StepStatus.Completed,
                IterationNumber = newIteration
            });
        }
    }

    /// <summary>
    /// Runs the quick-win-promo tail (director, Author, ReviewLoop) once through
    /// <see cref="WorkflowExecutorService"/> and returns the input the Author was built with on each run.
    /// </summary>
    private static async Task<List<string>> RunPromoLoopAsync(
        string authorOutput, string reviewerOutput, AgentType reviewAgentType = AgentType.ReviewAgent)
    {
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using WorkflowEngineDbContext db = new(options);

        Guid projectId = Guid.NewGuid();
        Guid workflowDefId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        AgentDefinition director = new() { Id = Guid.NewGuid(), Name = "Director", AgentType = AgentType.DirectorAgent, SystemPrompt = "" };
        AgentDefinition author = new() { Id = Guid.NewGuid(), Name = "Author", AgentType = AgentType.AuthorAgent, SystemPrompt = "" };
        AgentDefinition review = new() { Id = Guid.NewGuid(), Name = "Review", AgentType = reviewAgentType, SystemPrompt = "" };

        // The quick-win-promo tail: (director) -> Author (order 10) -> ReviewLoop (order 11, MinScore 8,
        // MaxIterations 2, loop target 10). The director stands on a non-Agent step type only so the
        // stub executors can be told apart by type.
        WorkflowStep directorStep = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 9,
            StepType = StepType.VideoAnalyze,
            AgentDefinitionId = director.Id,
            AgentDefinition = director
        };
        WorkflowStep authorStep = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 10,
            StepType = StepType.Agent,
            AgentDefinitionId = author.Id,
            AgentDefinition = author,
            AgentInputContextMode = AgentInputContextMode.FullWorkflow
        };
        WorkflowStep reviewStep = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 11,
            StepType = StepType.ReviewLoop,
            AgentDefinitionId = review.Id,
            AgentDefinition = review,
            LoopTargetStepOrder = 10,
            MaxIterations = 2,
            MinScore = 8,
            AgentInputContextMode = AgentInputContextMode.FullWorkflow
        };

        WorkflowDefinition definition = new()
        {
            Id = workflowDefId,
            Name = "promo",
            ProjectId = projectId,
            Steps = new List<WorkflowStep> { directorStep, authorStep, reviewStep }
        };
        WorkflowExecution execution = new()
        {
            Id = executionId,
            WorkflowDefinitionId = workflowDefId,
            ProjectId = projectId,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        };

        db.AgentDefinitions.AddRange(director, author, review);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();

        RecordingAuthorExecutor authorExecutor = new(authorOutput);
        IStepExecutor[] executors =
        [
            new FixedOutputExecutor(StepType.VideoAnalyze, DirectorOutput),
            authorExecutor,
            new FailingOnceReviewExecutor(reviewerOutput)
        ];

        WorkflowExecutorService service = new(
            scopeFactory: new FakeScopeFactory(db),
            eventPublisher: new NoOpEventPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: executors,
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry());

        await service.ExecuteAsync(executionId, "corr-1", CancellationToken.None);

        return authorExecutor.ReceivedInputs;
    }

    [Fact]
    public async Task The_loop_back_Author_reads_the_full_review_and_the_scene_list_of_the_render_it_replaces()
    {
        List<string> received = await RunPromoLoopAsync(AuthorManifestJson, ReviewerJson);

        received.Should().HaveCount(2);
        received[0].Should().NotContain("Reviewer feedback");

        string loopBackInput = received[1];

        // The exact revision brief the Author reads after its own context.
        string expectedBrief =
            "\n\n---\n" + Heading + "\n" +
            "Overall score: 6/10 (pass mark 8).\n" +
            "Summary: Clean and on-brand, but the same mockup repeats in every scene and the film ends without a call to action.\n" +
            "Per criterion:\n" +
            "- narrativeClarity 5/10: The hook and the payoff show the same dashboard mockup, so the viewer never sees what changed between scene 1 and scene 4.\n" +
            "- visualAccuracy 7/10: The invoice table matches the real screen, but the sidebar labels are placeholder text.\n" +
            "- timing 6/10: Scene 3 holds a static frame for 4.5 s with no motion; cut it to about 2 s or animate the chart.\n" +
            "- completeness 6/10: The brief asks for a call to action and the end card shows the logo only.\n" +
            "Improvement areas:\n" +
            "- Give each scene its own screen instead of reusing the hero mockup\n" +
            "- End on a card with the call to action 'Start free'\n" +
            "\n" +
            "The render the reviewer judged (\"Ledgerly promo\", 450 frames at 30 fps = 15.0 s) was built from these compositions:\n" +
            "1. Hook (HookScene): 90 frames = 3.0 s\n" +
            "2. Dashboard (DashboardScene): 120 frames = 4.0 s\n" +
            "3. Chart (ChartScene): 135 frames = 4.5 s\n" +
            "4. EndCard (EndCardScene): 105 frames = 3.5 s";

        loopBackInput.Should().Contain(DirectorOutput, "the Author keeps its upstream context");
        loopBackInput.Should().EndWith(expectedBrief);
        loopBackInput.Should().NotContain(SchemaSentence);
        loopBackInput.Should().NotContain("Retry Guidance");
    }

    [Fact]
    public async Task A_video_review_capped_by_unused_clips_loops_back_with_the_capped_score_and_its_reason()
    {
        // T-019: the video review step is a VideoReviewAgent; a ReviewAgent step reads only overallScore.
        List<string> received = await RunPromoLoopAsync(UnusedClipsCompileJson, VideoNineJson, AgentType.VideoReviewAgent);

        received.Should().HaveCount(2);
        received[1].Should().Contain(Heading + "\nScore capped from 9: offered clips left unused. Tight edit, uses the clips well.");
        received[1].Should().NotContain("score 9/10");
    }

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
}
