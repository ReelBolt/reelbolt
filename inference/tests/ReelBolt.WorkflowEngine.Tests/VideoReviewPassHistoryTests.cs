using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Quality;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-007: the reviewer must see the numbers from EARLIER passes of the same run, so it can catch an
/// edit that keeps shrinking (the real run went 8 → 6 → 3 kept pieces while the score rose 6 → 7 →
/// 8). A loop-back prunes the earlier outputs from <c>StepOutputHistory</c> and the review feedback
/// stops before the review step, so without this the reviewer judges each pass against the
/// available material with no memory that the edit is being cut away.
///
/// <para>
/// These tests pin four things: the facts reach the reviewer's input on passes 2 and 3; the block
/// is bounded by pass count and characters; a one-pass run's prompt input is byte-identical; and
/// the rule lives in every copy of the prompt. The effect on real scores is UNMEASURED until a
/// real run — this file can only prove what the reviewer is told.
/// </para>
/// </summary>
public class VideoReviewPassHistoryTests
{
    private const string PlanPass1 =
        """{"status":"completed","segments":8,"outputDurationSec":74.3,"retainedRatio":0.412,"planOnly":true,"encoded":false}""";
    private const string PlanPass2 =
        """{"status":"completed","segments":6,"outputDurationSec":58.1,"retainedRatio":0.320,"planOnly":true,"encoded":false}""";
    private const string PlanPass3 =
        """{"status":"completed","segments":3,"outputDurationSec":30.2,"retainedRatio":0.160,"planOnly":true,"encoded":false}""";

    private const string Pass1Line = "- Pass 1: score 6/10; kept 8 pieces; output 74.3 s; retained 41.2% of source.";
    private const string Pass2Line = "- Pass 2: score 7/10; kept 6 pieces; output 58.1 s; retained 32.0% of source.";
    private const string HistoryHeading = "Earlier passes of this run — facts only";

    // =====================================================================================
    // The facts reach the reviewer's input on pass 2 and pass 3
    // =====================================================================================

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

    /// <summary>One plan-only compile per pass, each with a different (shrinking) cut.</summary>
    private sealed class ShrinkingPlanCompileExecutor : IStepExecutor
    {
        private readonly string[] _plans;
        public List<string> ReceivedInputs { get; } = new();
        public StepType StepType => StepType.VideoCompile;

        public ShrinkingPlanCompileExecutor(params string[] plans) => _plans = plans;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            ReceivedInputs.Add(context.BuildAgentInput());
            string plan = _plans[Math.Min(ReceivedInputs.Count - 1, _plans.Length - 1)];
            return Task.FromResult(new StepExecutionResult
            {
                Output = plan,
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed
            });
        }
    }

    private static IReelBoltAgent RecordingReviewer(List<string> received, int[] scores)
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns("VideoReview");
        agent.Setup(a => a.AgentType).Returns(AgentType.VideoReviewAgent);
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid?, CancellationToken>((input, _, _) => received.Add(input))
            .ReturnsAsync(() =>
            {
                int score = scores[Math.Min(received.Count - 1, scores.Length - 1)];
                return new AgentRunResult
                {
                    Output = $$"""{"score":{{score}}}""",
                    Success = true,
                    TokensUsed = 10
                };
            });
        return agent.Object;
    }

    private static Mock<IWorkflowExecutionContextAccessor> ContextAccessor()
    {
        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext?)null);
        return accessor;
    }

    [Fact]
    public async Task Earlier_pass_numbers_reach_the_reviewers_input_on_passes_two_and_three()
    {
        var options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new WorkflowEngineDbContext(options);

        Guid projectId = Guid.NewGuid();
        Guid workflowDefId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        AgentDefinition analyzeAgent = new() { Id = Guid.NewGuid(), Name = "Analyze", AgentType = AgentType.VideoTransform, SystemPrompt = "" };
        AgentDefinition editorAgent = new() { Id = Guid.NewGuid(), Name = "Editor", AgentType = AgentType.VideoStoryEditor, SystemPrompt = "" };
        AgentDefinition compileAgent = new() { Id = Guid.NewGuid(), Name = "Compile", AgentType = AgentType.VideoTransform, SystemPrompt = "" };
        AgentDefinition reviewAgent = new() { Id = Guid.NewGuid(), Name = "Review", AgentType = AgentType.VideoReviewAgent, SystemPrompt = "" };

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
            AgentDefinitionId = editorAgent.Id,
            AgentDefinition = editorAgent,
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
            MaxIterations = 3,
            MinScore = 8,
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

        db.AgentDefinitions.AddRange(analyzeAgent, editorAgent, compileAgent, reviewAgent);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();

        var received = new List<string>();
        var compile = new ShrinkingPlanCompileExecutor(PlanPass1, PlanPass2, PlanPass3);
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>()))
            .Returns(RecordingReviewer(received, [6, 7, 8]));

        IStepExecutor[] executors =
        {
            new FixedOutputExecutor(StepType.VideoAnalyze, """{"view":{"shots":[{"id":"s1"}]}}"""),
            new FixedOutputExecutor(StepType.Agent, """{"keep":[{"fromId":"s1","toId":"s1"}]}"""),
            compile,
            new ReviewLoopStepExecutor(
                registry.Object, ContextAccessor().Object, new Mock<IDecisionGate>().Object,
                NullLogger<ReviewLoopStepExecutor>.Instance)
        };

        var service = new WorkflowExecutorService(
            scopeFactory: new FakeScopeFactory(db),
            eventPublisher: new NoOpEventPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: executors,
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry());

        await service.ExecuteAsync(executionId, "corr-1", CancellationToken.None);

        // Three passes: 6 loops back, 7 loops back, 8 advances. The compile ran plan-only once per pass.
        received.Should().HaveCount(3);
        compile.ReceivedInputs.Should().HaveCount(3);

        // Pass 1 has no earlier pass: nothing is appended, so its input is what it was before T-007.
        received[0].Should().NotContain(HistoryHeading);
        received[0].Should().NotContain("- Pass 1:");

        // Pass 2 carries pass 1's numbers.
        received[1].Should().Contain(HistoryHeading);
        received[1].Should().Contain(Pass1Line);
        received[1].Should().NotContain("- Pass 2:");

        // Pass 3 carries pass 1 AND pass 2 — the run's visible memory of the shrinking edit.
        received[2].Should().Contain(HistoryHeading);
        received[2].Should().Contain(Pass1Line);
        received[2].Should().Contain(Pass2Line);
        received[2].Should().NotContain("- Pass 3:");

        // Numbers only: no footage ids, no storage keys, no transcript from the earlier cuts.
        received[2].Should().NotContain("outputStorageKey");
        received[2].Should().NotContain("timelineStorageKey");
    }

    // =====================================================================================
    // The bound: passes and characters
    // =====================================================================================

    [Fact]
    public void The_earlier_pass_block_is_capped_by_pass_count_and_characters()
    {
        List<ReviewLoopStepExecutor.ReviewPassFact> facts = Enumerable.Range(1, 12)
            .Select(i => new ReviewLoopStepExecutor.ReviewPassFact(i, 5, 10, 100 - i, 0.5))
            .ToList();

        string block = ReviewLoopStepExecutor.RenderReviewPassHistory(facts)!;

        block.Length.Should().BeLessThanOrEqualTo(ReviewLoopStepExecutor.MaxReviewPassFactsChars);
        block.Should().Contain("- Pass 8:", "only the most recent MaxReviewPassFacts=5 passes fit");
        block.Should().NotContain("- Pass 7:", "older passes are dropped, and the block says so");
        block.Should().Contain("Older passes omitted");

        // A character cap that cannot fit even the header truncates rather than exceeding the bound.
        string tiny = ReviewLoopStepExecutor.RenderReviewPassHistory(facts, maxPasses: 12, maxChars: 120)!;
        tiny.Length.Should().BeLessThanOrEqualTo(120);
    }

    // =====================================================================================
    // Measured size for a 3-pass run, and which pass it is added to
    // =====================================================================================

    [Fact]
    public void The_added_prompt_size_for_a_three_pass_run_is_measured()
    {
        // The real 3-pass shape of the reported run, rendered exactly as WorkflowExecutorService
        // renders it (score it gave, pieces kept, output length, retained ratio).
        var pass1 = new ReviewLoopStepExecutor.ReviewPassFact(1, 6, 8, 74.3, 0.412);
        var pass2 = new ReviewLoopStepExecutor.ReviewPassFact(2, 7, 6, 58.1, 0.320);

        string onPass2 = ReviewLoopStepExecutor.RenderReviewPassHistory([pass1])!;
        string onPass3 = ReviewLoopStepExecutor.RenderReviewPassHistory([pass1, pass2])!;

        // Pass 1 adds 0. Pass 2 adds 245 characters, pass 3 adds 323 — 568 across the run, and the
        // larger block is well inside the 600-character cap.
        onPass2.Length.Should().Be(245);
        onPass3.Length.Should().Be(323);
        (onPass2.Length + onPass3.Length).Should().Be(568);
        onPass3.Length.Should().BeLessThanOrEqualTo(ReviewLoopStepExecutor.MaxReviewPassFactsChars);
    }

    // =====================================================================================
    // One pass: prompt input, cache-key input and gate state unchanged
    // =====================================================================================

    private static StepExecutionContext ReviewContext(
        string priorOutput, string? reviewPassHistory, int iterationCount = 0)
    {
        WorkflowStep loopTarget = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.VideoCompile,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.VideoTransform, Name = "Compile" }
        };
        WorkflowStep review = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = loopTarget.WorkflowDefinitionId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.ReviewLoop,
            MinScore = 8,
            MaxIterations = 3,
            LoopTargetStepOrder = 2,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.VideoReviewAgent, Name = "VideoReview" }
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = review,
            AllSteps = [loopTarget, review],
            AccumulatedOutput = priorOutput,
            StepOutputHistory = [new StepOutputHistoryEntry(4, "VideoCompile", priorOutput)],
            ReviewPassHistory = reviewPassHistory,
            CurrentStepIndex = 1,
            IterationCount = iterationCount,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    [Fact]
    public async Task A_first_pass_prompt_input_is_byte_identical_with_and_without_the_feature()
    {
        var received = new List<string>();
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>()))
            .Returns(RecordingReviewer(received, [8]));

        var executor = new ReviewLoopStepExecutor(
            registry.Object, ContextAccessor().Object, new Mock<IDecisionGate>().Object,
            NullLogger<ReviewLoopStepExecutor>.Instance);

        // No earlier pass: ReviewPassHistory is null (what WorkflowExecutorService renders for an
        // empty list), so the executor appends nothing.
        StepExecutionContext context = ReviewContext(PlanPass1, reviewPassHistory: null);
        string baseline = context.BuildAgentInput();
        byte[] baselineHash = SHA256.HashData(Encoding.UTF8.GetBytes(baseline));

        await executor.ExecuteAsync(context);

        received.Should().HaveCount(1);
        received[0].Should().Be(baseline);
        SHA256.HashData(Encoding.UTF8.GetBytes(received[0])).Should().Equal(baselineHash);

        // The cache-key input (ResolveInputJsonForPersistence + AppendReviewPassHistory) is likewise
        // untouched when there is no earlier pass. A ReviewLoop's resolved input is the accumulated
        // compile plan (LastResolvedAgentInput is null for that step type).
        string cacheKeyInput = WorkflowExecutorService.ResolveInputJsonForPersistence(null, PlanPass1)!;
        cacheKeyInput.Should().Be(PlanPass1);
        WorkflowExecutorService.AppendReviewPassHistory(cacheKeyInput, null).Should().Be(PlanPass1);
        WorkflowExecutorService.AppendReviewPassHistory(cacheKeyInput, string.Empty).Should().Be(PlanPass1);

        // And the decision gate's state is built from the compile output alone, so it never carries
        // the earlier-pass block: a one-pass gate sees exactly what it saw before T-007.
        string? gateState = ReviewLoopStepExecutor.BuildReviewFacts(PlanPass1);
        gateState.Should().NotBeNull();
        gateState.Should().NotContain(HistoryHeading);
    }

    // =====================================================================================
    // The plan-only path (T-004): facts come from the plan, never from an encoded file
    // =====================================================================================

    [Fact]
    public void A_plan_only_compile_supplies_the_facts_without_an_encoded_file()
    {
        // T-004's plan-only output carries segments/outputDurationSec/retainedRatio and deliberately
        // no outputStorageKey. The facts must come from those numbers.
        ReviewLoopStepExecutor.ReviewPassFact? fact =
            ReviewLoopStepExecutor.TryReadReviewPassFact(PlanPass1, pass: 1, reviewerScore: 6);

        fact.Should().NotBeNull();
        fact!.Value.Pass.Should().Be(1);
        fact.Value.Score.Should().Be(6);
        fact.Value.KeptPieces.Should().Be(8);
        fact.Value.OutputLengthSec.Should().Be(74.3);
        fact.Value.RetainedRatio.Should().Be(0.412);

        // A non-compile prior output (the main promo pipeline's ReviewLoop) yields no fact, so its
        // reviewer prompt stays byte-identical.
        ReviewLoopStepExecutor.TryReadReviewPassFact("""{"overallScore":9,"criteria":[]}""", 1, 9)
            .Should().BeNull();
        ReviewLoopStepExecutor.TryReadReviewPassFact("not json", 1, 9).Should().BeNull();
    }

    // =====================================================================================
    // The rule is in EVERY copy of the prompt
    // =====================================================================================

    private const string FallbackSource = "VideoReviewAgentImpl.DefaultPrompt";
    private const string SeededSource = "DatabaseSeeder.BuiltInAgents[VideoReviewAgent]";

    private static string ReadPrompt(string source) => source switch
    {
        FallbackSource => (string)typeof(VideoReviewAgentImpl)
            .GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!,
        SeededSource => SeededPrompt(),
        _ => throw new Xunit.Sdk.XunitException($"unknown prompt source '{source}'")
    };

    private static string SeededPrompt()
    {
        FieldInfo? field = typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull("DatabaseSeeder must expose a private BuiltInAgents seed table");
        var agents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            field!.GetValue(null)!;
        agents.Should().ContainKey(AgentType.VideoReviewAgent);
        return agents[AgentType.VideoReviewAgent].SystemPrompt;
    }

    public static TheoryData<string> PromptSources() => new() { FallbackSource, SeededSource };

    [Theory]
    [MemberData(nameof(PromptSources))]
    public void The_prompt_rule_for_earlier_passes_is_in_every_copy(string source)
    {
        string prompt = Regex.Replace(ReadPrompt(source), @"\s+", " ");

        prompt.Should().Contain("Facts from earlier passes.");
        prompt.Should().Contain("Earlier passes of this run");
        prompt.Should().Contain("the score you gave it");
        prompt.Should().Contain("report it in `issues`");
        prompt.Should().Contain("judge what was LOST as well as what remains");
        prompt.Should().Contain("give no credit for an edit merely being shorter or keeping fewer pieces");
        prompt.Should().Contain("Do not invent a target or a fixed retained-ratio floor");
    }

    [Fact]
    public void The_two_prompt_copies_are_byte_identical()
    {
        // The raw-string indentation is stripped by the compiler, so this is the exact prompt text
        // both the in-process fallback and the seeded row run with.
        ReadPrompt(SeededSource).Should().Be(ReadPrompt(FallbackSource));
    }
}
