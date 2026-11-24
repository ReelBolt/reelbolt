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
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compositions;

/// <summary>
/// A composition revision's replay through the REAL <see cref="WorkflowExecutorService.ExecuteAsync"/>,
/// on the staging workflow's shape (translator → strategy → director → author → analyze → edit room →
/// compile → review): what is reused, what runs, what the replayed compile sees, when the cut is kept and
/// when the review loops.
/// </summary>
public class CompositionReplayExecutionTests
{
    private const string Brief = "A two-minute vertical reel about affording an M2 at 23";
    private const string OriginalDecision = "{\"keep\":[{\"fromId\":\"s0\",\"toId\":\"s2\"},{\"fromId\":\"s3\",\"toId\":\"s3\"}]}";
    private const string NewRender = "projects/p/outputFiles/replay/revision.mp4";
    private const string NewAnalysisArtifact = "projects/p/agentFiles/video-analysis/replay/step-5-analysis.json";

    private sealed class Recorder
    {
        public int AgentCalls;
        public int ReviseCalls;
        public int AnalyzeCalls;
        public int EditRoomCalls;
        public int CompileCalls;
        public int ReviewCalls;
        public string? AnalyzedRenderKey;
        public string? CompileSawDecision;
        public string? CompileSawAnalysisArtifact;
        public string? CompileSawUserRequest;
    }

    private sealed class Executor(StepType type, Func<StepExecutionContext, StepExecutionResult> run) : IStepExecutor
    {
        public StepType StepType { get; } = type;
        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) => Task.FromResult(run(context));
    }

    private static StepExecutionResult Done(StepExecutionContext c, string output, string? outputKey = null, string? artifactKey = null) => new()
    {
        Output = output,
        NextStepIndex = c.CurrentStepIndex + 1,
        NewIterationCount = c.IterationCount,
        Status = StepStatus.Completed,
        OutputStorageKey = outputKey,
        ArtifactStorageKey = artifactKey
    };

    private sealed class Run
    {
        public required WorkflowEngineDbContext Db { get; init; }
        public required Guid ReplayExecutionId { get; init; }
        public required Dictionary<int, WorkflowStepResult> Source { get; init; }
        public required Recorder Recorder { get; init; }
        public required Dictionary<int, Guid> ReplayStepIds { get; init; }

        public List<WorkflowStepResult> Rows(int order) => [.. Db.WorkflowStepResults
            .Where(r => r.WorkflowExecutionId == ReplayExecutionId && r.WorkflowStepId == ReplayStepIds[order])
            .OrderBy(r => r.ExecutedAt)];
    }

    private static async Task<Run> ReplayAsync(string replayedAnalysis, CompositionCutPolicy policy,
        Action<Dictionary<int, WorkflowStepResult>>? tweakSource = null)
    {
        WorkflowEngineDbContext db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase("replay-" + Guid.NewGuid().ToString("N")).Options);
        Guid projectId = Guid.NewGuid();

        // The original run (1e9c441f's shape), every step finished.
        List<WorkflowStep> originalSteps = ReplayShapes.AnimatedFilmEditRoomCut();
        WorkflowDefinition original = new() { Id = Guid.NewGuid(), Name = "Animated film + edit room cut", ProjectId = projectId };
        foreach (WorkflowStep step in originalSteps)
            step.WorkflowDefinitionId = original.Id;
        WorkflowExecution sourceRun = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = original.Id,
            ProjectId = projectId,
            Status = ExecutionStatus.Passed,
            UserRequest = Brief
        };
        Dictionary<int, WorkflowStepResult> source = originalSteps.ToDictionary(s => s.StepOrder, s => new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = sourceRun.Id,
            WorkflowStepId = s.Id,
            Status = StepStatus.Completed,
            ExecutedAt = DateTime.UtcNow.AddMinutes(-60 + s.StepOrder),
            Output = s.StepOrder switch
            {
                5 => CutCompatibilityTests.Original,
                6 => OriginalDecision,
                _ => $"{{\"step\":{s.StepOrder}}}"
            },
            OutputStorageKey = s.StepOrder switch
            {
                4 => "projects/p/outputFiles/run1/film.mp4",
                7 => "projects/p/outputFiles/cut-v1.mp4",
                _ => null
            },
            ArtifactStorageKey = s.StepOrder == 5 ? "projects/p/agentFiles/video-analysis/run1/step-5-analysis.json" : null
        });

        // The revision's snapshot: same steps, the author replaced by a Revise step.
        tweakSource?.Invoke(source);
        CompositionReplayPlan plan = CompositionReplayPlanner.Plan(
            originalSteps, source.ToDictionary(kv => kv.Key, kv => kv.Value.Id), 4, policy, sourceRun.Id, original.Id);
        AgentDefinition reviser = new() { Id = Guid.NewGuid(), Name = "CompositionReviser", AgentType = AgentType.CompositionReviser, IsBuiltIn = true };
        WorkflowDefinition snapshot = new()
        {
            Id = Guid.NewGuid(),
            Name = "Changes to Animated film + edit room cut",
            ProjectId = projectId,
            Kind = WorkflowDefinitionKind.CompositionReplay
        };
        foreach (WorkflowStep step in originalSteps)
        {
            WorkflowStep copy = WorkflowStepCloner.Clone(step, snapshot.Id);
            copy.AgentDefinition = step.AgentDefinition;
            if (step.StepOrder == 4)
            {
                copy.StepType = StepType.Revise;
                copy.AgentDefinitionId = reviser.Id;
                copy.AgentDefinition = reviser;
            }

            snapshot.Steps.Add(copy);
        }

        WorkflowExecution replay = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = snapshot.Id,
            ProjectId = projectId,
            Status = ExecutionStatus.Queued,
            UserRequest = Brief,
            WorkflowDefinition = snapshot
        };
        CompositionRevision revision = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            WorkspaceId = Guid.NewGuid(),
            SourceExecutionId = sourceRun.Id,
            ExecutionId = replay.Id,
            WorkflowDefinitionId = snapshot.Id,
            Instruction = "More graphics, same timing",
            CutPolicy = policy,
            ReplayPlanJson = plan.Serialize()
        };

        db.AgentDefinitions.AddRange(originalSteps.Select(s => s.AgentDefinition).Append(reviser));
        db.WorkflowDefinitions.AddRange(original, snapshot);
        db.WorkflowSteps.AddRange(originalSteps);
        db.WorkflowExecutions.AddRange(sourceRun, replay);
        db.WorkflowStepResults.AddRange(source.Values);
        db.CompositionRevisions.Add(revision);
        await db.SaveChangesAsync();

        Recorder rec = new();
        IStepExecutor[] executors =
        [
            new Executor(StepType.Agent, c => { rec.AgentCalls++; return Done(c, "{}"); }),
            new Executor(StepType.Revise, c => { rec.ReviseCalls++; return Done(c, "{\"succeeded\":true}", NewRender); }),
            new Executor(StepType.VideoAnalyze, c =>
            {
                rec.AnalyzeCalls++;
                rec.AnalyzedRenderKey = c.StepOutputHistory.LastOrDefault(h => h.StepOrder == 4)?.OutputStorageKey;
                return Done(c, replayedAnalysis, artifactKey: NewAnalysisArtifact);
            }),
            new Executor(StepType.EditRoom, c => { rec.EditRoomCalls++; return Done(c, "{\"keep\":[{\"fromId\":\"s1\",\"toId\":\"s1\"}]}"); }),
            new Executor(StepType.VideoCompile, c =>
            {
                rec.CompileCalls++;
                rec.CompileSawDecision = c.StepOutputHistory.LastOrDefault(h => h.StepOrder == 6)?.Output;
                rec.CompileSawAnalysisArtifact = c.StepOutputHistory.LastOrDefault(h => h.StepOrder == 5)?.ArtifactStorageKey;
                rec.CompileSawUserRequest = c.UserRequest;
                return Done(c, "{\"status\":\"completed\"}", $"projects/p/outputFiles/cut-replay-{rec.CompileCalls}.mp4");
            }),
            new Executor(StepType.ReviewLoop, c =>
            {
                rec.ReviewCalls++;
                int iteration = c.IterationCount + 1;
                bool loop = iteration < 2;
                return new StepExecutionResult
                {
                    Output = "{\"score\":6,\"passesReview\":false}",
                    NextStepIndex = loop ? c.AllSteps.FindIndex(s => s.StepOrder == 6) : c.CurrentStepIndex + 1,
                    NewIterationCount = iteration,
                    IterationNumber = iteration,
                    Status = StepStatus.Completed
                };
            })
        ];

        WorkflowExecutorService service = new(
            scopeFactory: new FakeScopeFactory(db),
            eventPublisher: new NoOpPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: executors,
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry());
        await service.ExecuteAsync(replay.Id, "corr", CancellationToken.None);

        return new Run
        {
            Db = db,
            ReplayExecutionId = replay.Id,
            Source = source,
            Recorder = rec,
            ReplayStepIds = snapshot.Steps.ToDictionary(s => s.StepOrder, s => s.Id)
        };
    }

    [Fact]
    public async Task Keep_the_same_cut_reuses_the_steps_before_the_film_re_analyses_and_re_compiles_with_the_approved_decision()
    {
        Run run = await ReplayAsync(CutCompatibilityTests.Original, CompositionCutPolicy.KeepCut);

        WorkflowExecution done = await run.Db.WorkflowExecutions.SingleAsync(e => e.Id == run.ReplayExecutionId);
        done.Status.Should().Be(ExecutionStatus.Passed, done.ErrorMessage);

        run.Recorder.AgentCalls.Should().Be(0, "translator, strategy and director are reused, never re-run");
        foreach (int order in new[] { 1, 2, 3 })
        {
            WorkflowStepResult row = run.Rows(order).Single();
            row.ReusedFromStepResultId.Should().Be(run.Source[order].Id);
            row.Output.Should().Be(run.Source[order].Output);
            row.TokensUsed.Should().Be(0);
            row.CreditsChargedMillis.Should().Be(0);
        }

        run.Recorder.ReviseCalls.Should().Be(1);
        run.Recorder.AnalyzedRenderKey.Should().Be(NewRender, "the analysis reads the re-rendered film");

        run.Recorder.EditRoomCalls.Should().Be(0, "the film has the same scenes, so the approved cut is kept");
        run.Rows(6).Single().ReusedFromStepResultId.Should().Be(run.Source[6].Id);

        run.Recorder.CompileCalls.Should().Be(1);
        run.Recorder.CompileSawDecision.Should().Be(OriginalDecision);
        run.Recorder.CompileSawAnalysisArtifact.Should().Be(NewAnalysisArtifact, "the compile cuts the NEW film");
        run.Recorder.CompileSawUserRequest.Should().Be(Brief, "replayed steps see the original brief, not the change request");
        run.Rows(7).Single().OutputStorageKey.Should().Be("projects/p/outputFiles/cut-replay-1.mp4");

        run.Recorder.ReviewCalls.Should().Be(1, "the review is recorded once and does not loop into a kept cut");
        run.Rows(8).Single().Output.Should().Contain("loopBack");
    }

    [Fact]
    public async Task A_film_whose_scenes_changed_re_makes_the_edit_and_the_review_loops_normally()
    {
        string added = CutCompatibilityTests.View(("s0", 0, 0, 4.3), ("s1", 0, 4.3, 10.8), ("s2", 0, 10.8, 13.3), ("s3", 0, 13.3, 16), ("s4", 1, 0, 4));

        Run run = await ReplayAsync(added, CompositionCutPolicy.KeepCut);

        (await run.Db.WorkflowExecutions.SingleAsync(e => e.Id == run.ReplayExecutionId)).Status.Should().Be(ExecutionStatus.Passed);
        run.Recorder.EditRoomCalls.Should().Be(2, "re-made, then re-made again by the review's loop");
        run.Recorder.CompileCalls.Should().Be(2);
        run.Recorder.ReviewCalls.Should().Be(2);
        run.Rows(6).Should().OnlyContain(r => r.ReusedFromStepResultId == null);
        run.Recorder.CompileSawDecision.Should().NotBe(OriginalDecision);
    }

    [Fact]
    public async Task A_reused_step_does_not_inherit_the_source_registration_overQuota()
    {
        Run run = await ReplayAsync(CutCompatibilityTests.Original, CompositionCutPolicy.KeepCut,
            tweakSource: source => source[1].OutputJson = """{"step":1,"registration":{"registered":2,"overQuota":true}}""");

        WorkflowStepResult row = run.Rows(1).Single();
        row.ReusedFromStepResultId.Should().Be(run.Source[1].Id);
        row.OutputJson.Should().NotBeNull().And.NotContain("overQuota");
        JsonNode.Parse(row.OutputJson!)!["registration"]!["registered"]!.GetValue<int>()
            .Should().Be(2, "only registration.overQuota is stripped");
    }

    [Fact]
    public async Task Re_edit_re_makes_the_decision_even_when_the_scenes_did_not_change()
    {
        Run run = await ReplayAsync(CutCompatibilityTests.Original, CompositionCutPolicy.ReEdit);

        run.Recorder.EditRoomCalls.Should().Be(2);
        run.Recorder.AgentCalls.Should().Be(0);
        run.Rows(6).Should().OnlyContain(r => r.ReusedFromStepResultId == null);
    }

    [Fact]
    public async Task A_replay_workflow_without_a_plan_fails_instead_of_re_running_the_whole_pipeline()
    {
        WorkflowEngineDbContext db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase("replay-noplan-" + Guid.NewGuid().ToString("N")).Options);
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "Author", AgentType = AgentType.AuthorAgent };
        WorkflowDefinition snapshot = new() { Id = Guid.NewGuid(), Name = "x", ProjectId = Guid.NewGuid(), Kind = WorkflowDefinitionKind.CompositionReplay };
        snapshot.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, AgentDefinitionId = agent.Id, AgentDefinition = agent, WorkflowDefinitionId = snapshot.Id });
        WorkflowExecution replay = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = snapshot.Id, ProjectId = snapshot.ProjectId, Status = ExecutionStatus.Queued, WorkflowDefinition = snapshot };
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(snapshot);
        db.WorkflowExecutions.Add(replay);
        await db.SaveChangesAsync();
        int agentCalls = 0;

        WorkflowExecutorService service = new(
            scopeFactory: new FakeScopeFactory(db),
            eventPublisher: new NoOpPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: [new Executor(StepType.Agent, c => { agentCalls++; return Done(c, "{}"); })],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry());
        Func<Task> act = () => service.ExecuteAsync(replay.Id, "corr", CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        agentCalls.Should().Be(0);
        (await db.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Failed);
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
