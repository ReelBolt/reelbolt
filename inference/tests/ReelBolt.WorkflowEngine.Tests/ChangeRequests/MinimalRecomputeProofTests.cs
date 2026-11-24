using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.ChangeRequests;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Replay;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using ReelBolt.WorkflowEngine.Tests.Compositions;
using ReelBolt.WorkflowEngine.Tests.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>
/// The maintainer's claim, proven end to end: "reused results are free; only the changed step and what depends on it re-run;
/// the estimate is shown before confirming". The REAL <see cref="StepChangeRequestService"/> plans, prices and
/// snapshots a change on a finished promo-shaped run; the REAL <see cref="WorkflowExecutorService.ExecuteAsync"/> then
/// replays the queued snapshot over the SAME in-memory store (so the service's rows are exactly what the engine reads),
/// through the REAL <see cref="CompositionReplaySession"/>, the REAL <see cref="StepChangeRequestContext"/>, and a
/// recording credit gate. Steps: 1 analyze -> 2 scriptwriter -> 3 Voiceover (5 lines) -> 4 VideoGenerate (3 clips) ->
/// 5 VideoCompile -> 6 review, plus 7, an analysis that reads only step 1.
///
/// Covered precisely: Voiceover is the REAL <see cref="VoiceoverStepExecutor"/> (fake speech client, fake object store,
/// counting alignment). VideoGenerate / compile / review / analyze / agent are COUNTING FAKE executors - the real
/// VideoGenerate carry-over is proven in <see cref="VideoGenerateChangeRequestTests"/>; here the fake proves the engine
/// hands it the right scope (item ids and the previous manifest) and that exactly one clip is submitted for it.
/// No network, no paid call.
/// </summary>
public class MinimalRecomputeProofTests
{
    private static readonly Guid ProviderId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly string[] Texts = ["Line zero.", "Line one.", "Line two.", "Line three.", "Line four."];

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    // ------------------------------------------------------------------ fakes

    /// <summary>Prices 1 / 3 / 3 credits per CHARGED step and remembers which steps and scales it was asked about.</summary>
    private sealed class RecordingEstimator(bool refuse = false) : IWorkflowCostEstimator
    {
        public bool BillingEnabled => true;
        public List<int> Charged { get; } = [];
        public IReadOnlyDictionary<int, double>? LastScales { get; private set; }

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(Estimate(steps));

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowPreflightResult(
                refuse ? WorkflowPreflightOutcome.InsufficientCredits : WorkflowPreflightOutcome.Allowed, Estimate(steps), 50_000));

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default)
        {
            Remember(chargedStepOrders);
            return EstimateAsync(organizationId, [.. runSteps.Where(s => chargedStepOrders.Contains(s.StepOrder))], ct);
        }

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default)
        {
            Remember(chargedStepOrders);
            return PreflightAsync(organizationId, [.. runSteps.Where(s => chargedStepOrders.Contains(s.StepOrder))], ct);
        }

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, int? outputResolution, IReadOnlyDictionary<int, double>? stepScales, CancellationToken ct = default)
        {
            LastScales = stepScales;
            return PreflightChargedAsync(organizationId, runSteps, chargedStepOrders, ct);
        }

        private void Remember(IReadOnlyCollection<int> orders)
        {
            Charged.Clear();
            Charged.AddRange(orders.OrderBy(o => o));
        }

        private static WorkflowCostEstimate Estimate(IReadOnlyList<WorkflowStep> steps) =>
            new(true, false, steps.Count * 1_000, steps.Count * 3_000, steps.Count * 3_000, []);
    }

    /// <summary>Every reserve and settle the engine asks the ledger for. A step that is copied must never appear here.</summary>
    private sealed class RecordingGate : ICreditGate
    {
        public List<(Guid StepResultId, long Millis, Guid ReservationId)> Reserved { get; } = [];
        public List<Guid> Settled { get; } = [];
        public List<Guid> Released { get; } = [];

        public Task<CreditReservation> ReserveAsync(Guid organizationId, Guid executionId, Guid stepResultId, long estimateMillis,
            Guid? projectId = null, CreditEntitlement requires = CreditEntitlement.None, int attempt = 0, CancellationToken ct = default, long? overdraftBasisMillis = null, Guid? apiKeyId = null)
        {
            Guid id = Guid.NewGuid();
            Reserved.Add((stepResultId, estimateMillis, id));
            return Task.FromResult(new CreditReservation(CreditReserveOutcome.Allowed, id, null, estimateMillis, 10_000_000));
        }

        public Task<CreditSettlement> SettleAsync(Guid? reservationId, long actualMillis, CancellationToken ct = default)
        {
            if (reservationId is { } id)
                Settled.Add(id);
            return Task.FromResult(new CreditSettlement(true, null, actualMillis));
        }

        public Task<CreditSettlement> ReleaseAsync(Guid? reservationId, FailureClass failureClass, long actualMillis = 0, CancellationToken ct = default)
        {
            if (reservationId is { } id)
                Released.Add(id);
            return Task.FromResult(new CreditSettlement(true));
        }

        public Task<int> ReleaseExecutionAsync(Guid executionId, CancellationToken ct = default) => Task.FromResult(0);

        public Task<CreditSettlement> SettleOpenAsync(Guid reserveId, long? recordedMillis, CancellationToken ct = default) =>
            Task.FromResult(new CreditSettlement(true));

        public Task<CreditSettlement> ReleaseOpenAsync(Guid reserveId, FailureClass failureClass, long? recordedMillis, CancellationToken ct = default) =>
            Task.FromResult(new CreditSettlement(true));
    }

    private sealed class CountingAlignment : IVoiceoverAlignmentService
    {
        public int Calls;

        public Task<VoiceoverAlignmentResult> AlignAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(VoiceoverAlignmentResult.NotApplied(VoiceoverAlignmentReasons.NoTranscriptionProvider));
        }
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

    /// <summary>A registry entry that counts how often each step order went through its executor.</summary>
    private sealed class Counting(StepType type, List<int> executed, Func<StepExecutionContext, Task<StepExecutionResult>> run) : IStepExecutor
    {
        public StepType StepType { get; } = type;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            lock (executed)
                executed.Add(context.Step.StepOrder);
            return run(context);
        }
    }

    private static Task<StepExecutionResult> Done(StepExecutionContext c, string output, string? outputKey = null) =>
        Task.FromResult(new StepExecutionResult
        {
            Output = output,
            NextStepIndex = c.CurrentStepIndex + 1,
            NewIterationCount = c.IterationCount,
            Status = StepStatus.Completed,
            OutputStorageKey = outputKey
        });

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

    // ------------------------------------------------------------------ the world

    private sealed class World
    {
        public string DbName { get; } = "minimal-recompute-" + Guid.NewGuid().ToString("N");
        public InferenceApiDbContext ApiDb { get; }
        public WorkflowEngineDbContext EngineDb { get; }
        public Mock<IPublishEndpoint> Bus { get; } = new();
        public Project Project { get; }
        public Guid User { get; } = Guid.NewGuid();
        public RecordingEstimator Estimator { get; }
        public StepChangeRequestService Service { get; }
        public WorkflowExecution Run { get; }
        public List<WorkflowStep> Steps { get; }
        public Dictionary<int, WorkflowStepResult> Source { get; }

        public VoiceoverCrossExecutionArtifactTests.InMemoryObjectStoreWorkspace Store { get; } = new();
        public VoiceoverCrossExecutionArtifactTests.CountingSpeechSynthesisClient Speech { get; set; } = new();
        public CountingAlignment Alignment { get; set; } = new();
        public string OriginalVoiceoverOutput { get; }
        public string OriginalManifest { get; }

        public World(bool refuse = false)
        {
            ApiDb = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(DbName + "-api").Options);
            EngineDb = new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(DbName + "-engine").Options);
            Estimator = new RecordingEstimator(refuse);
            Project = new Project { Id = Guid.NewGuid(), Name = "Promo", OwnerId = User, OrganizationId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            ApiDb.Projects.Add(Project);

            string voiceoverConfig = JsonSerializer.Serialize(new VoiceoverStepConfig(
                Source: new VoiceoverSource(VoiceoverSourceKind.Inline, Texts.Select((t, i) => new VoiceoverLine(t, i * 2.0)).ToList()),
                ProviderId: ProviderId,
                Model: "s2.1-pro-free",
                DefaultVoiceId: "default-voice",
                MaxBytes: 2_000_000,
                NarrationFitPolicy: NarrationFitPolicy.Report), VoiceoverStepConfig.JsonOptions);
            string generateConfig = JsonSerializer.Serialize(new VideoGenerateStepConfig
            {
                PlanSource = VideoGeneratePlanSource.StepRef,
                Plan = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
                Model = "MiniMax-H3",
                Resolution = "768P",
                Ratio = "16:9",
                DurationSeconds = 6,
                Takes = 1,
                MaxClips = 4,
                MaxSpendCredits = 100_000m,
                TimeoutSeconds = 60,
                PollIntervalSeconds = 1
            }, ConfigJsonOptions);
            const string analyzeOfFile = """{"version":1,"source":{"kind":"ProjectFile","projectFileId":"f6681b28-8488-4738-ae1f-83a7ba40d4e0"}}""";
            const string analyzeOfStep1 = """{"version":1,"source":{"kind":"StepOutput","stepOrder":1}}""";
            const string compileConfig = """{"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"generatedClips":{"from":"Step","stepOrder":4},"enableVoiceover":true,"voiceoverStepOrder":3}""";

            Steps =
            [
                ReplayShapes.Step(1, AgentType.VideoTransform, StepType.VideoAnalyze, analyze: analyzeOfFile),
                ReplayShapes.Step(2, AgentType.ScriptwriterAgent, mode: AgentInputContextMode.PreviousStepOnly),
                ReplayShapes.Step(3, AgentType.VideoTransform, StepType.Voiceover, voiceover: voiceoverConfig),
                ReplayShapes.Step(4, AgentType.VideoTransform, StepType.VideoGenerate, generate: generateConfig),
                ReplayShapes.Step(5, AgentType.VideoTransform, StepType.VideoCompile, compile: compileConfig),
                ReplayShapes.Step(6, AgentType.VideoReviewAgent, StepType.ReviewLoop, mode: AgentInputContextMode.FullWorkflow, loopTarget: 2),
                ReplayShapes.Step(7, AgentType.VideoTransform, StepType.VideoAnalyze, analyze: analyzeOfStep1)
            ];
            ApiDb.AgentDefinitions.AddRange(Steps.Select(s => s.AgentDefinition));
            WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Promo", ProjectId = Project.Id };
            foreach (WorkflowStep s in Steps)
                s.WorkflowDefinitionId = workflow.Id;
            ApiDb.WorkflowDefinitions.Add(workflow);
            ApiDb.WorkflowSteps.AddRange(Steps);

            Run = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflow.Id,
                ProjectId = Project.Id,
                OrganizationId = Project.OrganizationId,
                Status = ExecutionStatus.Passed,
                UserRequest = "A promo reel",
                StartedAt = DateTime.UtcNow.AddHours(-1),
                CompletedAt = DateTime.UtcNow
            };
            ApiDb.WorkflowExecutions.Add(Run);

            // The original run's voiceover is a REAL run of the executor: five lines, five speech and five alignment calls.
            OriginalVoiceoverOutput = RunVoiceoverAsync(Steps[2], null, new Execution(Run.Id, Project.Id)).GetAwaiter().GetResult().Output!;
            Speech.CallCount.Should().Be(5);
            Alignment.Calls.Should().Be(5);

            OriginalManifest = JsonSerializer.Serialize(new
            {
                version = 1,
                clips = new[] { "v1", "v2", "v3" }.Select((id, i) => new
                {
                    clipId = id,
                    shotIndex = i + 1,
                    take = 1,
                    description = $"Shot {i + 1}",
                    durationSeconds = 9,
                    status = "succeeded",
                    storageKey = $"projects/{Project.Id}/agentFiles/generated/{Run.Id}/step-4-{id}.mp4"
                }),
                meta = new { }
            });

            Source = Steps.ToDictionary(s => s.StepOrder, s => new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = Run.Id,
                WorkflowStepId = s.Id,
                Status = StepStatus.Completed,
                ExecutedAt = DateTime.UtcNow.AddMinutes(-60 + s.StepOrder),
                Output = s.StepOrder switch { 3 => OriginalVoiceoverOutput, 4 => OriginalManifest, _ => $"{{\"step\":{s.StepOrder}}}" },
                OutputJson = s.StepOrder switch { 3 => OriginalVoiceoverOutput, 4 => OriginalManifest, _ => null },
                CreditsChargedMillis = 5_000
            });
            ApiDb.WorkflowStepResults.AddRange(Source.Values);
            ApiDb.SaveChanges();
            ApiDb.ChangeTracker.Clear();

            Service = new StepChangeRequestService(
                ApiDb,
                new WorkflowExecutionService(ApiDb, Bus.Object, NullLogger<WorkflowExecutionService>.Instance),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CompositionRevisions:MaxPerHourPerUserProject"] = "10" }).Build(),
                NullLogger<StepChangeRequestService>.Instance,
                Estimator);
        }

        /// <summary>
        /// The API and the engine are two services over ONE Postgres database; the in-memory provider gives each model its own
        /// store, so what the service wrote (snapshot, request row, queued run) is copied across verbatim before the engine starts.
        /// </summary>
        public async Task HandOverToEngineAsync()
        {
            EngineDb.AgentDefinitions.AddRange(await ApiDb.AgentDefinitions.AsNoTracking().ToListAsync());
            EngineDb.WorkflowDefinitions.AddRange(await ApiDb.WorkflowDefinitions.AsNoTracking().ToListAsync());
            EngineDb.WorkflowSteps.AddRange(await ApiDb.WorkflowSteps.AsNoTracking().ToListAsync());
            EngineDb.WorkflowExecutions.AddRange(await ApiDb.WorkflowExecutions.AsNoTracking().ToListAsync());
            EngineDb.WorkflowStepResults.AddRange(await ApiDb.WorkflowStepResults.AsNoTracking().ToListAsync());
            EngineDb.StepChangeRequests.AddRange(await ApiDb.StepChangeRequests.AsNoTracking().ToListAsync());
            await EngineDb.SaveChangesAsync();
            EngineDb.ChangeTracker.Clear();
        }

        public CreateStepChangeRequest SlowerSecondLine() => new(
            3, null, "Slower on the third line", ["vo2"],
            new() { ["vo2"] = new() { ["speed"] = JsonDocument.Parse("0.9").RootElement.Clone() } });

        public CreateStepChangeRequest RedoSecondClip() => new(
            4, null, "Another take of shot two", ["v2"],
            new() { ["v2"] = new() { ["retake"] = JsonDocument.Parse("true").RootElement.Clone() } });

        public Task<StepChangeRequestResult> Estimate(CreateStepChangeRequest body) =>
            Service.EstimateAsync(Project, Run.Id, Project.OrganizationId, body, CancellationToken.None);

        public Task<StepChangeRequestResult> Request(CreateStepChangeRequest body) =>
            Service.RequestAsync(Project, Run.Id, User, Project.OrganizationId, body, CancellationToken.None);

        public sealed record Execution(Guid Id, Guid ProjectId);

        /// <summary>The real VoiceoverStepExecutor over the fakes; <paramref name="changeRequests"/> null = an ordinary run.</summary>
        public Task<StepExecutionResult> RunVoiceoverAsync(WorkflowStep step, IStepChangeRequestContext? changeRequests, Execution execution) =>
            BuildVoiceover(changeRequests).ExecuteAsync(new StepExecutionContext
            {
                Execution = new WorkflowExecution { Id = execution.Id, ProjectId = execution.ProjectId },
                Step = step,
                AllSteps = [step],
                AccumulatedOutput = string.Empty,
                StepOutputHistory = [],
                CurrentStepIndex = 0,
                IterationCount = 0,
                CorrelationId = "minimal-recompute-original",
                CancellationToken = CancellationToken.None
            });

        public VoiceoverStepExecutor BuildVoiceover(IStepChangeRequestContext? changeRequests)
        {
            ResolvedSpeechSynthesisProvider provider = new(ProviderId, "fish", InferenceProviderKind.FishAudio, "https://api.fish.audio", "s2.1-pro-free", "test-key", 30);
            Mock<IInferenceProviderResolver> resolver = new();
            resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(provider);
            Mock<ISpeechSynthesisClientFactory> factory = new();
            factory.Setup(x => x.Get(It.IsAny<ResolvedSpeechSynthesisProvider>())).Returns(() => Speech);
            Mock<IMediaProbe> probe = new();
            probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(1.75, 30, 1, 0, 0, null, "pcm_s16le", 44100));
            return new VoiceoverStepExecutor(
                factory.Object, resolver.Object, probe.Object, Store, Alignment,
                NullLogger<VoiceoverStepExecutor>.Instance, new NoVoices(), changeRequests: changeRequests);
        }
    }

    private sealed record Replayed(
        World World,
        StepChangeRequestResponse Created,
        List<int> Executed,
        RecordingGate Gate,
        Dictionary<int, WorkflowStepResult> Rows,
        List<StepChangeRequestScope> ScopesSeenByGenerate,
        List<string> SubmittedClips);

    /// <summary>Request the change through the real service, then replay the queued snapshot through the real executor service.</summary>
    private static async Task<Replayed> RequestAndReplayAsync(World w, CreateStepChangeRequest body)
    {
        StepChangeRequestResult result = await w.Request(body);
        result.StatusCode.Should().Be(201, result.Code);
        StepChangeRequestResponse created = result.Created!;

        await w.HandOverToEngineAsync();
        List<int> executed = [];
        List<StepChangeRequestScope> scopes = [];
        List<string> submitted = [];
        RecordingGate gate = new();
        StepChangeRequestContext context = new(new FakeScopeFactory(w.EngineDb));
        IStepExecutor[] executors =
        [
            new Counting(StepType.Agent, executed, c => Done(c, "{\"agent\":true}")),
            new Counting(StepType.VideoAnalyze, executed, c => Done(c, "{\"analysis\":true}")),
            new Counting(StepType.Voiceover, executed, c => w.BuildVoiceover(context).ExecuteAsync(c)),
            new Counting(StepType.VideoGenerate, executed, async c =>
            {
                // The seam the real VideoGenerate executor reads: the scope the engine finds for this step.
                StepChangeRequestScope? scope = await context.FindAsync(c.Execution, c.Step, CancellationToken.None);
                scope.Should().NotBeNull("the engine finds the request for its target step");
                scopes.Add(scope!);
                JsonObject manifest = JsonNode.Parse(scope!.PreviousOutputJson!)!.AsObject();
                foreach (JsonObject clip in manifest["clips"]!.AsArray().Select(n => n!.AsObject()))
                {
                    if (scope.Payload.ItemIds!.Contains(clip["clipId"]!.GetValue<string>()))
                    {
                        submitted.Add(clip["clipId"]!.GetValue<string>());
                        clip["storageKey"] = clip["storageKey"]!.GetValue<string>().Replace(".mp4", "-retake.mp4", StringComparison.Ordinal);
                    }
                }

                return await Done(c, manifest.ToJsonString());
            }),
            new Counting(StepType.VideoCompile, executed, c => Done(c, "{\"status\":\"completed\"}", "projects/p/outputFiles/replay.mp4")),
            new Counting(StepType.ReviewLoop, executed, c => Done(c, "{\"score\":9,\"passesReview\":true}"))
        ];

        WorkflowExecutorService service = new(
            scopeFactory: new FakeScopeFactory(w.EngineDb),
            eventPublisher: new NoOpPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: executors,
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry(),
            creditGate: gate,
            creditOptions: Options.Create(new StepCreditOptions { StepReserveMillisOverride = 10_000 }));
        await service.ExecuteAsync(created.ExecutionId, "corr", CancellationToken.None);

        WorkflowExecution done = await w.EngineDb.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == created.ExecutionId);
        done.Status.Should().Be(ExecutionStatus.Passed, done.ErrorMessage);

        Dictionary<Guid, int> orderOfStep = await w.EngineDb.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowDefinitionId == done.WorkflowDefinitionId).ToDictionaryAsync(s => s.Id, s => s.StepOrder);
        List<WorkflowStepResult> rows = await w.EngineDb.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == created.ExecutionId).ToListAsync();
        rows.Select(r => orderOfStep[r.WorkflowStepId]).Should().OnlyHaveUniqueItems("every step has exactly one row in the change run");
        return new Replayed(w, created, executed, gate, rows.ToDictionary(r => orderOfStep[r.WorkflowStepId]), scopes, submitted);
    }

    private static (int Order, string Disposition)[] Table(IReadOnlyList<StepChangeReplayStepResponse> replay) =>
        [.. replay.Select(s => (s.StepOrder, s.Disposition))];

    // ------------------------------------------------------------------ voiceover: change one line of five

    [Fact]
    public async Task One_voiceover_line_prices_only_the_changed_step_and_what_depends_on_it_before_the_user_confirms()
    {
        World w = new();
        (int, string)[] expected = [(1, "Reuse"), (2, "Reuse"), (3, "Revise"), (4, "Reuse"), (5, "Run"), (6, "Run"), (7, "Reuse")];

        // Shown BEFORE confirming: the estimate endpoint writes nothing.
        StepChangeRequestResult preview = await w.Estimate(w.SlowerSecondLine());

        preview.StatusCode.Should().Be(200);
        Table(preview.Preview!.Replay.Steps).Should().Equal(expected, "the exact disposition table of a Voiceover change on the promo shape");
        w.Estimator.Charged.Should().Equal([3, 5, 6], "only the Revise step and the steps that depend on it are priced; 1, 2, 4 and 7 are free");
        preview.Preview.Estimate!.LowCredits.Should().Be(3, "1 credit x 3 priced steps, not 7");
        preview.Preview.Estimate.HighCredits.Should().Be(9);
        preview.Preview.Estimate.RequiredCredits.Should().Be(9);
        (await w.ApiDb.StepChangeRequests.CountAsync()).Should().Be(0, "the estimate writes nothing");
        (await w.ApiDb.WorkflowExecutions.CountAsync()).Should().Be(1);

        // The stored scope is the line's cost share (characters: "Line two." of all five lines), and the estimate was scaled by it.
        StepChangeRequestResult created = await w.Request(w.SlowerSecondLine());
        created.StatusCode.Should().Be(201);
        double share = Texts[2].Length / (double)Texts.Sum(t => t.Length);
        StepChangeRequest row = await w.ApiDb.StepChangeRequests.SingleAsync();
        row.ScopeFraction.Should().BeApproximately(share, 1e-9);
        share.Should().BeApproximately(9d / 49d, 1e-9);
        w.Estimator.LastScales.Should().NotBeNull().And.ContainKey(3).WhoseValue.Should().BeApproximately(share, 1e-9);
        w.Estimator.LastScales!.Keys.Should().Equal(3);
        Table(created.Created!.Replay!.Steps).Should().Equal(expected, "the request carries the same table the estimate showed");
        CompositionReplayPlan.TryParse(row.ReplayPlanJson)!.Steps.Select(s => (s.StepOrder, s.Disposition.ToString())).Should().Equal(expected);
        created.Created.Estimate!.RequiredCredits.Should().Be(preview.Preview.Estimate.RequiredCredits, "what was shown is what is confirmed");

        // The 402 carries the very figure the estimate showed.
        World poor = new(refuse: true);
        StepChangeRequestResult refused = await poor.Request(poor.SlowerSecondLine());
        refused.StatusCode.Should().Be(402);
        refused.Extra!["required"].Should().Be(preview.Preview.Estimate.RequiredCredits);
        (await poor.ApiDb.StepChangeRequests.CountAsync()).Should().Be(0, "a refused request queues nothing");
    }

    [Fact]
    public async Task One_voiceover_line_runs_one_speech_call_and_the_dependents_once_and_everything_else_is_a_free_copy()
    {
        World w = new();
        w.Speech = new();
        w.Alignment = new();

        Replayed r = await RequestAndReplayAsync(w, w.SlowerSecondLine());

        // Reused steps: copied rows, no cost, no executor, no ledger entry.
        foreach (int order in new[] { 1, 2, 4, 7 })
        {
            WorkflowStepResult row = r.Rows[order];
            row.ReusedFromStepResultId.Should().Be(w.Source[order].Id, $"step {order} is a copy of the source result");
            row.Output.Should().Be(w.Source[order].Output);
            row.CreditsChargedMillis.Should().Be(0, $"reusing step {order} is free (the source run charged {w.Source[order].CreditsChargedMillis})");
            row.TokensUsed.Should().Be(0);
        }

        r.Executed.OrderBy(o => o).Should().Equal([3, 5, 6], "only the Voiceover, the compile that mixes it, and the review ran; the sibling analysis (7) and 1, 2, 4 did not");
        foreach (int order in new[] { 3, 5, 6 })
            r.Rows[order].ReusedFromStepResultId.Should().BeNull($"step {order} really ran");

        Dictionary<Guid, int> orderOfResult = r.Rows.ToDictionary(kv => kv.Value.Id, kv => kv.Key);
        r.Gate.Reserved.Select(x => orderOfResult[x.StepResultId]).OrderBy(o => o).Should().Equal([3, 5, 6], "the ledger was asked to hold credits for exactly the steps that ran");
        r.Gate.Reserved.Select(x => x.StepResultId).Should().NotIntersectWith(new[] { 1, 2, 4, 7 }.Select(o => r.Rows[o].Id), "a copied step never reaches the ledger");
        long fullHold = 10_000;
        long voiceHold = r.Gate.Reserved.Single(x => orderOfResult[x.StepResultId] == 3).Millis;
        voiceHold.Should().Be((long)Math.Ceiling(fullHold * (9d / 49d)), "the Voiceover hold is the line's share of the full-step hold, not the whole step");
        voiceHold.Should().BeLessThan(fullHold);
        r.Gate.Reserved.Where(x => orderOfResult[x.StepResultId] != 3).Select(x => x.Millis).Should().OnlyContain(m => m == fullHold, "dependents hold their whole step");
        HashSet<Guid> reservations = [.. r.Gate.Reserved.Select(x => x.ReservationId)];
        r.Gate.Settled.Should().BeSubsetOf(reservations, "nothing but a held step is ever settled");
        r.Gate.Settled.Should().HaveCount(3, "one settle for each step that ran, none for a copy");

        // The real Voiceover executor re-made exactly the changed line.
        w.Speech.CallCount.Should().Be(1, "one speech call for one line, not five");
        w.Speech.Requests.Single().Text.Should().Be("Line two.");
        w.Alignment.Calls.Should().Be(1, "one alignment call for one line");
        JsonElement lines = JsonDocument.Parse(r.Rows[3].Output!).RootElement.GetProperty("view").GetProperty("lines");
        JsonElement original = JsonDocument.Parse(w.OriginalVoiceoverOutput).RootElement.GetProperty("view").GetProperty("lines");
        lines.GetArrayLength().Should().Be(5);
        foreach (int unchanged in new[] { 0, 1, 3, 4 })
            lines[unchanged].GetRawText().Should().Be(original[unchanged].GetRawText(), $"vo{unchanged} is carried over verbatim");
        JsonDocument.Parse(r.Rows[3].Output!).RootElement.GetProperty("meta").GetProperty("changeRequest").GetProperty("reusedLineCount").GetInt32().Should().Be(4);
    }

    // ------------------------------------------------------------------ video generate: change clip 2 of three

    [Fact]
    public async Task One_clip_of_three_submits_once_keeps_the_other_manifest_entries_and_leaves_the_voiceover_alone()
    {
        World w = new();
        (int, string)[] expected = [(1, "Reuse"), (2, "Reuse"), (3, "Reuse"), (4, "Revise"), (5, "Run"), (6, "Run"), (7, "Reuse")];

        StepChangeRequestResult preview = await w.Estimate(w.RedoSecondClip());

        preview.StatusCode.Should().Be(200, preview.Code + JsonSerializer.Serialize(preview.Extra));
        Table(preview.Preview!.Replay.Steps).Should().Equal(expected, "the voiceover is independent of the clips, so it is a free copy");
        w.Estimator.Charged.Should().Equal([4, 5, 6]);
        preview.Preview.Estimate!.RequiredCredits.Should().Be(9);

        Replayed r = await RequestAndReplayAsync(w, w.RedoSecondClip());

        (await w.ApiDb.StepChangeRequests.SingleAsync()).ScopeFraction.Should().BeApproximately(1d / 3d, 1e-9, "one clip of three equal clips");
        Table(r.Created.Replay!.Steps).Should().Equal(expected);
        r.Executed.OrderBy(o => o).Should().Equal([4, 5, 6], "the generate step, the compile that cuts it and the review ran once each");
        foreach (int order in new[] { 1, 2, 3, 7 })
        {
            r.Rows[order].ReusedFromStepResultId.Should().Be(w.Source[order].Id);
            r.Rows[order].CreditsChargedMillis.Should().Be(0);
        }

        w.Speech.CallCount.Should().Be(5, "only the original run spoke; the change run made no speech call (5 from the original run, none new)");
        r.Rows[3].Output.Should().Be(w.OriginalVoiceoverOutput, "the voiceover is the source run's, byte for byte");

        r.ScopesSeenByGenerate.Should().ContainSingle();
        r.ScopesSeenByGenerate[0].Payload.ItemIds.Should().Equal("v2");
        r.ScopesSeenByGenerate[0].PreviousOutputJson.Should().Be(w.OriginalManifest, "the engine hands the executor the finished step's manifest");
        r.SubmittedClips.Should().Equal("v2");
        JsonElement clips = JsonDocument.Parse(r.Rows[4].Output!).RootElement.GetProperty("clips");
        JsonElement before = JsonDocument.Parse(w.OriginalManifest).RootElement.GetProperty("clips");
        clips[0].GetRawText().Should().Be(before[0].GetRawText());
        clips[2].GetRawText().Should().Be(before[2].GetRawText());
        clips[1].GetProperty("storageKey").GetString().Should().NotBe(before[1].GetProperty("storageKey").GetString());

        Dictionary<Guid, int> orderOfResult = r.Rows.ToDictionary(kv => kv.Value.Id, kv => kv.Key);
        r.Gate.Reserved.Select(x => orderOfResult[x.StepResultId]).OrderBy(o => o).Should().Equal([4, 5, 6]);
        r.Gate.Reserved.Single(x => orderOfResult[x.StepResultId] == 4).Millis.Should().Be((long)Math.Ceiling(10_000 / 3d), "the generate hold is one clip's third of the step");
    }

    // ------------------------------------------------------------------ drift guard

    [Fact]
    public void Every_step_type_declares_whether_it_supports_change_requests()
    {
        foreach (StepType type in Enum.GetValues<StepType>())
        {
            StepChangeCapability capability = StepChangeCapabilities.For(type);
            capability.Should().NotBeNull($"{type} must declare change-request support (full guard: StepChangeCapabilitiesDriftTests)");
        }
    }
}
