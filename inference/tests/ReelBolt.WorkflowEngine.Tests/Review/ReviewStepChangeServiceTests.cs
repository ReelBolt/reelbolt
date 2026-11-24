using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Review;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Replay;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Services.Review;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using ReelBolt.WorkflowEngine.Tests.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// Change requests a review asks for INSIDE its own run, through the REAL <see cref="WorkflowExecutorService"/>
/// (docs/design/review-loop-adjustments.md "Change requests inside the run"): the paid step re-runs within the run,
/// carrying out the change through the customer change-request path, holds and is charged once per re-run step result,
/// and a credit refusal stops the loop cleanly with the best pass kept.
/// </summary>
public class ReviewStepChangeServiceTests
{
    private const string Voice = "default-voice";

    /// <summary>Pass 1: asks for line vo2 to be re-made with new words and loops back; pass 2: passes.</summary>
    private sealed class ChangingReviewExecutor(Func<StepExecutionContext, IReadOnlyList<ReviewAdjustmentProposal>> proposals) : IStepExecutor
    {
        public StepType StepType => StepType.ReviewLoop;
        public List<string> Inputs { get; } = [];

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

            ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(ReviewLoopStepExecutor.AdjustmentRequest(context, loopsBack: true), proposals(context));
            int next = plan.RerunFromStepOrder is { } from
                ? context.AllSteps.FindIndex(s => s.StepOrder == from)
                : context.AllSteps.FindIndex(s => s.StepOrder == context.Step.LoopTargetStepOrder);
            return Task.FromResult(new StepExecutionResult
            {
                Output = ReviewAdjustmentReport.AttachToOutput("""{"score":6,"passesReview":false,"summary":"line 3 is cut"}""", plan, AgentType.VideoReviewAgent),
                NextStepIndex = next,
                NewIterationCount = iteration,
                IterationNumber = iteration,
                Status = StepStatus.Completed,
                ReviewAdjustments = plan
            });
        }
    }

    private sealed class Counting(StepType type, string output) : IStepExecutor
    {
        public StepType StepType => type;
        public int Calls;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            Calls++;
            return Task.FromResult(new StepExecutionResult
            {
                Output = output,
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed
            });
        }
    }

    private sealed class NoAlignment : IVoiceoverAlignmentService
    {
        public Task<VoiceoverAlignmentResult> AlignAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult(VoiceoverAlignmentResult.NotApplied(VoiceoverAlignmentReasons.NoTranscriptionProvider));
    }

    private sealed class NoVoices : IProjectVoiceResolver
    {
        public Task<ResolvedProjectVoice> ResolveAsync(Guid projectId, Guid projectVoiceId, CancellationToken ct) =>
            Task.FromResult(ResolvedProjectVoice.NotFound(projectVoiceId));

        public Task<IReadOnlyList<string>> FindRefusedVendorVoiceIdsAsync(Guid projectId, IReadOnlyCollection<string> remoteVoiceIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<ResolvedProjectVoice>> ListSelectableAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ResolvedProjectVoice>>([]);
    }

    private sealed record Setup(WorkflowEngineDbContext Db, Guid ExecutionId, WorkflowStep Voiceover, WorkflowStep Compile, WorkflowStep Review);

    /// <summary>Voiceover(1) -> VideoCompile(2, loop target) -> ReviewLoop(3, adjustments on).</summary>
    private static async Task<Setup> SeedAsync(Guid? organizationId = null)
    {
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        WorkflowEngineDbContext db = new(options);
        Guid projectId = Guid.NewGuid();
        Guid definitionId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();
        AgentDefinition transform = new() { Id = Guid.NewGuid(), Name = "Transform", AgentType = AgentType.VideoTransform, SystemPrompt = "" };
        AgentDefinition reviewer = new() { Id = Guid.NewGuid(), Name = "Review", AgentType = AgentType.VideoReviewAgent, SystemPrompt = "" };
        WorkflowStep S(int order, StepType type, AgentDefinition agent) => new()
        {
            Id = Guid.NewGuid(),
            StepOrder = order,
            StepType = type,
            AgentDefinitionId = agent.Id,
            AgentDefinition = agent,
            WorkflowDefinitionId = definitionId
        };
        WorkflowStep voiceover = S(1, StepType.Voiceover, transform);
        voiceover.VoiceoverConfigJson = JsonSerializer.Serialize(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, [new VoiceoverLine("Line zero.", 0), new VoiceoverLine("Line one.", 2), new VoiceoverLine("Line two.", 4)]),
            ProviderId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Model: "s2.1-pro-free",
            DefaultVoiceId: Voice,
            MaxBytes: 2_000_000,
            NarrationFitPolicy: NarrationFitPolicy.Report), VoiceoverStepConfig.JsonOptions);
        WorkflowStep compile = S(2, StepType.VideoCompile, transform);
        compile.VideoCompileConfigJson = """{"version":1}""";
        WorkflowStep review = S(3, StepType.ReviewLoop, reviewer);
        review.LoopTargetStepOrder = 2;
        review.MaxIterations = 3;
        review.MinScore = 8;
        review.ReviewLoopConfigJson = """{"allowAdjustments":true}""";
        WorkflowDefinition definition = new() { Id = definitionId, Name = "narrated", ProjectId = projectId, Steps = [voiceover, compile, review] };
        db.AgentDefinitions.AddRange(transform, reviewer);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = definitionId,
            ProjectId = projectId,
            OrganizationId = organizationId ?? Guid.Empty,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        });
        await db.SaveChangesAsync();
        return new Setup(db, executionId, voiceover, compile, review);
    }

    private static WorkflowExecutorService Service(WorkflowEngineDbContext db, IEnumerable<IStepExecutor> executors, ICreditGate? gate = null) => new(
        scopeFactory: new FakeScopeFactory(db),
        eventPublisher: new NoOpPublisher(),
        logger: NullLogger<WorkflowExecutorService>.Instance,
        executors: executors,
        rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
        hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
        cancellationRegistry: new ExecutionCancellationRegistry(),
        creditGate: gate);

    private static ReviewAdjustmentProposal LineChange() =>
        new(1, "text", JsonValue.Create("A new third line."), "the third line is cut mid-word", "vo2");

    [Fact]
    public async Task A_voiceover_line_change_is_carried_out_inside_the_run_and_only_that_line_is_spoken_again()
    {
        Setup run = await SeedAsync();
        using WorkflowEngineDbContext db = run.Db;
        VoiceoverCrossExecutionArtifactTests.InMemoryObjectStoreWorkspace store = new();
        VoiceoverCrossExecutionArtifactTests.CountingSpeechSynthesisClient speech = new();
        var provider = new ResolvedSpeechSynthesisProvider(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "fish", InferenceProviderKind.FishAudio,
            "https://api.fish.audio", "s2.1-pro-free", "test-key", 30);
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(provider);
        var factory = new Mock<ISpeechSynthesisClientFactory>();
        factory.Setup(x => x.Get(It.IsAny<ResolvedSpeechSynthesisProvider>())).Returns(speech);
        var probe = new Mock<IMediaProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(1.75, 30, 1, 0, 0, null, "pcm_s16le", 44100));
        VoiceoverStepExecutor voiceover = new(
            factory.Object, resolver.Object, probe.Object, store, new NoAlignment(),
            NullLogger<VoiceoverStepExecutor>.Instance, new NoVoices(),
            changeRequests: new StepChangeRequestContext(new FakeScopeFactory(db)));
        Counting compiler = new(StepType.VideoCompile, """{"status":"completed"}""");
        ChangingReviewExecutor reviewer = new(_ => [LineChange()]);

        await Service(db, [voiceover, compiler, reviewer]).ExecuteAsync(run.ExecutionId, "corr", CancellationToken.None);

        WorkflowExecution done = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == run.ExecutionId);
        done.Status.Should().Be(ExecutionStatus.Passed);
        speech.CallCount.Should().Be(4, "three lines at first, then only the changed line inside the same run");
        speech.Requests.Last().Text.Should().Be("A new third line.");
        compiler.Calls.Should().Be(2);

        Guid snapshotVoiceoverId = await db.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowDefinitionId == done.SnapshotDefinitionId && s.StepOrder == 1).Select(s => s.Id).SingleAsync();
        List<WorkflowStepResult> rows = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == run.ExecutionId && r.WorkflowStepId == snapshotVoiceoverId)
            .OrderBy(r => r.ExecutedAt).ToListAsync();
        rows.Should().HaveCount(2, "the change is a re-run inside this run, not a new linked run");
        JsonElement lines = JsonDocument.Parse(rows[1].Output).RootElement.GetProperty("view").GetProperty("lines");
        lines[2].GetProperty("text").GetString().Should().Be("A new third line.");
        JsonDocument.Parse(rows[1].Output).RootElement.GetProperty("meta").GetProperty("changeRequest")
            .GetProperty("reusedLineCount").GetInt32().Should().Be(2);
        (await db.WorkflowExecutions.AsNoTracking().CountAsync()).Should().Be(1, "no linked run was started");

        // The saved workflow is untouched, and the registry holds nothing once the dispatch ended.
        ReviewStepChangeRegistry.Instance.Find(run.ExecutionId, 1).Should().BeNull();

        // The review after the re-run records what it did, step by step.
        WorkflowStepResult lastReview = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == run.ExecutionId && r.IterationNumber == 2 && r.Output.Contains("previousAdjustmentRerun"))
            .SingleAsync();
        JsonNode rerun = JsonNode.Parse(lastReview.Output)![ReviewAdjustmentReport.RerunChargesNodeName]!;
        rerun["steps"]!.AsArray().Select(s => (s!["stepOrder"]!.GetValue<int>(), s["outcome"]!.GetValue<string>()))
            .Should().Equal((1, "changed"), (2, "rerun"));
    }

    private static Mock<ICreditGate> Gate(Func<int, bool> allow, List<Guid> reservedFor, long chargeMillis = 1_000)
    {
        int calls = 0;
        Mock<ICreditGate> gate = new();
        gate.Setup(g => g.ReserveAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<Guid?>(),
                It.IsAny<CreditEntitlement>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<long?>(), It.IsAny<Guid?>()))
            .ReturnsAsync((Guid org, Guid exec, Guid stepResultId, long estimate, Guid? project, CreditEntitlement requires, int attempt, CancellationToken ct, long? basis, Guid? apiKey) =>
            {
                calls++;
                if (!allow(calls))
                    return new CreditReservation(CreditReserveOutcome.InsufficientCredits, null, "INSUFFICIENT_CREDITS", estimate);
                reservedFor.Add(stepResultId);
                return new CreditReservation(CreditReserveOutcome.Allowed, Guid.NewGuid(), null, estimate, 1_000_000);
            });
        gate.Setup(g => g.SettleAsync(It.IsAny<Guid?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditSettlement(true, null, chargeMillis));
        gate.Setup(g => g.ReleaseAsync(It.IsAny<Guid?>(), It.IsAny<FailureClass>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditSettlement(true));
        return gate;
    }

    [Fact]
    public async Task Each_re_run_step_holds_once_and_is_charged_once_and_the_next_review_records_the_charges()
    {
        Setup run = await SeedAsync(Guid.NewGuid());
        using WorkflowEngineDbContext db = run.Db;
        List<Guid> reservedFor = [];
        Mock<ICreditGate> gate = Gate(_ => true, reservedFor);
        Counting voice = new(StepType.Voiceover, """{"view":{"lines":[{"id":"vo0","status":"ok","text":"a"},{"id":"vo2","status":"ok","text":"b"}]},"meta":{}}""");
        Counting compiler = new(StepType.VideoCompile, """{"status":"completed"}""");

        await Service(db, [voice, compiler, new ChangingReviewExecutor(_ => [LineChange()])], gate.Object)
            .ExecuteAsync(run.ExecutionId, "corr", CancellationToken.None);

        (await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == run.ExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        voice.Calls.Should().Be(2, "the paid narration step is re-entered by the review's change");
        List<WorkflowStepResult> rows = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == run.ExecutionId).ToListAsync();
        rows.Should().HaveCount(6, "two passes of three steps");
        reservedFor.Should().OnlyHaveUniqueItems().And.HaveCount(6, "one hold per step result, re-run steps included");
        reservedFor.Should().BeEquivalentTo(rows.Select(r => r.Id));
        rows.Should().OnlyContain(r => r.CreditsChargedMillis == 1_000, "each step result is charged once, normally");

        // The engine's record stays in the stored text the engine reads on resume, never in the JSON that is served and parsed.
        WorkflowStepResult firstReview = rows.Single(r => r.Output.Contains(ReviewOutputNodes.RecordMarker));
        firstReview.OutputJson.Should().NotContain(ReviewOutputNodes.RecordMarker);
        JsonNode.Parse(firstReview.OutputJson!)!["adjustmentRerun"].Should().NotBeNull();
        ReviewAdjustmentReport.ReadStepChanges(firstReview.Output, AgentType.VideoReviewAgent).Should().ContainSingle();

        WorkflowStepResult secondReview = rows.Single(r => r.Output.Contains(ReviewAdjustmentReport.RerunChargesNodeName));
        JsonNode rerun = JsonNode.Parse(secondReview.Output)![ReviewAdjustmentReport.RerunChargesNodeName]!;
        rerun["steps"]!.AsArray().Select(s => s!["creditsCharged"]!.GetValue<double>()).Should().Equal(1.0, 1.0);
        rerun["creditsCharged"]!.GetValue<double>().Should().Be(2.0);
    }

    [Fact]
    public async Task A_credit_refusal_during_the_re_run_stops_the_loop_keeps_the_best_pass_and_never_fails_the_run()
    {
        Setup run = await SeedAsync(Guid.NewGuid());
        using WorkflowEngineDbContext db = run.Db;
        List<Guid> reservedFor = [];
        // The first pass (three steps) is affordable; the re-run's first hold is refused.
        Mock<ICreditGate> gate = Gate(call => call <= 3, reservedFor);
        Counting voice = new(StepType.Voiceover, """{"view":{"lines":[{"id":"vo2","status":"ok","text":"b"}]},"meta":{}}""");
        Counting compiler = new(StepType.VideoCompile, """{"status":"completed","pass":1}""");

        await Service(db, [voice, compiler, new ChangingReviewExecutor(_ => [LineChange()])], gate.Object)
            .ExecuteAsync(run.ExecutionId, "corr", CancellationToken.None);

        WorkflowExecution done = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == run.ExecutionId);
        done.Status.Should().Be(ExecutionStatus.Passed, "running out of credits stops the loop, not the run");
        done.AttentionJson.Should().NotBeNullOrEmpty("the run says the review stopped below its minimum");
        voice.Calls.Should().Be(1);
        compiler.Calls.Should().Be(1);
        List<WorkflowStepResult> rows = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == run.ExecutionId).ToListAsync();
        rows.Should().ContainSingle(r => r.Status == StepStatus.Failed)
            .Which.FailureCode.Should().Be("INSUFFICIENT_CREDITS");
    }

    /// <summary>A paid step whose second call is refused by a daily spend cap, the way VideoGenerate's budget gate refuses.</summary>
    private sealed class CapRefusedOnRerun(StepType type, string output) : IStepExecutor
    {
        public StepType StepType => type;
        public int Calls;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            Calls++;
            return Task.FromResult(Calls == 1
                ? new StepExecutionResult
                {
                    Output = output,
                    NextStepIndex = context.CurrentStepIndex + 1,
                    NewIterationCount = context.IterationCount,
                    Status = StepStatus.Completed
                }
                : new StepExecutionResult
                {
                    Output = """{"error":{"code":"BUDGET_EXCEEDED","message":"Daily video budget reached."}}""",
                    ErrorDetails = "Daily video budget reached.",
                    FailureCode = "BUDGET_EXCEEDED",
                    FailureClass = FailureClass.UserInput,
                    NextStepIndex = context.CurrentStepIndex + 1,
                    NewIterationCount = context.IterationCount,
                    Retryable = false,
                    Status = StepStatus.Failed
                });
        }
    }

    [Fact]
    public async Task A_BUDGET_EXCEEDED_refusal_during_the_re_run_keeps_the_film_and_says_why()
    {
        // QA pin (run 21bb6f46): the review loop re-ran a paid step and the daily video cap refused it; the finished film
        // must stay, the run ends Passed with attention, and the kept review records the refusal.
        Setup run = await SeedAsync(Guid.NewGuid());
        using WorkflowEngineDbContext db = run.Db;
        CapRefusedOnRerun voice = new(StepType.Voiceover, """{"view":{"lines":[{"id":"vo2","status":"ok","text":"b"}]},"meta":{}}""");
        Counting compiler = new(StepType.VideoCompile, """{"status":"completed","pass":1}""");

        await Service(db, [voice, compiler, new ChangingReviewExecutor(_ => [LineChange()])])
            .ExecuteAsync(run.ExecutionId, "corr", CancellationToken.None);

        WorkflowExecution done = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == run.ExecutionId);
        done.Status.Should().Be(ExecutionStatus.Passed, "a refused paid re-run stops the loop, not the run");
        done.AttentionJson.Should().NotBeNullOrEmpty();
        voice.Calls.Should().Be(2);
        compiler.Calls.Should().Be(1, "the finished film from the first pass is kept, nothing is rendered again");
        List<WorkflowStepResult> rows = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == run.ExecutionId).ToListAsync();
        rows.Should().ContainSingle(r => r.Status == StepStatus.Failed).Which.FailureCode.Should().Be("BUDGET_EXCEEDED");
        WorkflowStepResult keptReview = rows.Single(r => r.Status == StepStatus.Completed
            && r.OutputJson != null && r.OutputJson.Contains("\"refused\""));
        JsonNode.Parse(keptReview.OutputJson!)!["adjustmentRerun"]!["refused"]!["code"]!.GetValue<string>().Should().Be("BUDGET_EXCEEDED");
    }

    [Fact]
    public async Task When_the_loop_stops_the_passes_after_the_best_one_no_longer_count()
    {
        Setup run = await SeedAsync(Guid.NewGuid());
        using WorkflowEngineDbContext db = run.Db;
        List<Guid> reservedFor = [];
        // The first pass (three steps) and the re-made narration are affordable; the next hold is refused.
        Mock<ICreditGate> gate = Gate(call => call <= 4, reservedFor);
        Counting voice = new(StepType.Voiceover, """{"view":{"lines":[{"id":"vo2","status":"ok","text":"b"}]},"meta":{}}""");
        Counting compiler = new(StepType.VideoCompile, """{"status":"completed","pass":1}""");

        await Service(db, [voice, compiler, new ChangingReviewExecutor(_ => [LineChange()])], gate.Object)
            .ExecuteAsync(run.ExecutionId, "corr", CancellationToken.None);

        List<WorkflowStepResult> rows = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == run.ExecutionId).OrderBy(r => r.ExecutedAt).ToListAsync();
        rows.Should().HaveCount(5, "the first pass, the re-made narration and the refused compile");
        rows.Take(3).Should().OnlyContain(r => r.SupersededAt == null, "the best pass stays the newest");
        rows.Skip(3).Should().OnlyContain(r => r.SupersededAt != null, "what the stopped re-run made is set aside");
    }

    [Fact]
    public async Task With_adjustments_off_a_credit_refusal_still_fails_the_run_as_before()
    {
        Setup run = await SeedAsync(Guid.NewGuid());
        using WorkflowEngineDbContext db = run.Db;
        WorkflowStep review = await db.WorkflowSteps.SingleAsync(s => s.Id == run.Review.Id);
        review.ReviewLoopConfigJson = null;
        await db.SaveChangesAsync();
        List<Guid> reservedFor = [];
        Mock<ICreditGate> gate = Gate(call => call <= 3, reservedFor);
        Counting voice = new(StepType.Voiceover, """{"view":{"lines":[]},"meta":{}}""");
        Counting compiler = new(StepType.VideoCompile, """{"status":"completed"}""");

        Func<Task> act = () => Service(db, [voice, compiler, new ChangingReviewExecutor(_ => [])], gate.Object)
            .ExecuteAsync(run.ExecutionId, "corr", CancellationToken.None);

        await act.Should().ThrowAsync<CreditRefusalException>();
        (await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == run.ExecutionId)).Status.Should().Be(ExecutionStatus.Failed);
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
