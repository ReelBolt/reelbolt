using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Replay;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>
/// A change request on a VideoGenerate step (docs/change-requests.md "VideoGenerate"): the clips the request does not
/// name are carried over from the previous manifest with no ledger row, no budget, no submit; the ones it names go
/// through the ordinary path. Fake client, fake object store and the real budget gate over an in-memory ledger:
/// no network or paid call is reachable.
/// </summary>
public class VideoGenerateChangeRequestTests
{
    private const string Model = "MiniMax-H3";
    private const string Resolution = "768P";
    private const string Ratio = "16:9";
    private const int DurationSeconds = 6;
    private static readonly Guid ProviderId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid ProjectId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid SourceExecutionId = Guid.Parse("99999999-8888-7777-6666-555555555555");
    private static readonly Guid ExecutionId = Guid.Parse("77777777-8888-7777-6666-555555555555");
    private static readonly Guid RequestId = Guid.Parse("12121212-1212-1212-1212-121212121212");
    private const int PlanStepOrder = 2;
    private const int TargetStepOrder = 3;

    private static readonly string ManifestKey =
        $"projects/{ProjectId}/agentFiles/video-analysis/{SourceExecutionId}/step-3-generated-clips.json";

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly MiniMaxVideoGenerationClient PricingOnlyMiniMax = new(new Mock<System.Net.Http.HttpClient>().Object, "key123");

    private static readonly string[] Prompts = ["Shot one", "Shot two", "Shot three"];

    // ------------------------------------------------------------------ fixtures

    private sealed class FixedContext(StepChangeRequestScope? scope) : IStepChangeRequestContext
    {
        public Task<StepChangeRequestScope?> FindAsync(WorkflowExecution execution, WorkflowStep step, CancellationToken ct) =>
            Task.FromResult(scope);
    }

    private sealed class CountingGate(IVideoGenerationBudgetGate inner) : IVideoGenerationBudgetGate
    {
        public List<VideoGenerationReservationRequest> Requests { get; } = [];

        public Task<VideoGenerationReservation> ReserveAsync(VideoGenerationReservationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return inner.ReserveAsync(request, ct);
        }
    }

    private sealed class FakeModeration(Func<string, bool> blocks) : IContentModerationGate
    {
        public List<string> Texts { get; } = [];

        public Task<ModerationDecision> ModerateAsync(ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
        {
            Texts.Add(input.Text);
            bool block = blocks(input.Text);
            return Task.FromResult(new ModerationDecision(
                block ? ModerationAction.Block : ModerationAction.Allow, true, false,
                block ? "violence" : null, block ? "This prompt cannot be used for video generation." : string.Empty, "hash"));
        }

        public Task<bool> ScreenGenerationPromptAsync(string text, IReadOnlyList<ModerationImageInput> images, ModerationAttribution attribution, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool> ScreenGeneratedTextAsync(string text, string site, ModerationAttribution attribution, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool> ScreenPublishAsync(ModerationInput input, ModerationAttribution attribution, CancellationToken ct) =>
            Task.FromResult(true);
    }

    private sealed class Rig
    {
        public SharedInMemoryDbFactory Db { get; } = new();
        public List<VideoGenerationRequest> Submitted { get; } = [];
        public CountingGate Gate { get; }
        public Mock<IProjectFileWorkspace> Workspace { get; } = new();
        public string? UploadedManifest { get; private set; }
        public Mock<IVideoGenerationClient> Client { get; } = new();
        public FakeModeration? Moderation { get; set; }
        public int Shots { get; set; } = 3;
        public int Takes { get; set; } = 1;
        public decimal StepCap { get; set; } = 100_000m;
        public JsonObject PreviousManifest { get; set; } = null!;
        public string? PreviousKeyOverride { get; set; }

        public Rig()
        {
            Gate = new CountingGate(VideoGenerateStepExecutorTests.RealGate(Db));
            Client.Setup(c => c.Describe(It.IsAny<string>())).Returns((string _) => PricingOnlyMiniMax.Describe(Model));
            int n = 0;
            Client.Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
                .Callback<VideoGenerationRequest, CancellationToken>((r, _) => Submitted.Add(r))
                .ReturnsAsync(() => new ExternalJobHandle($"remote-{++n}"));
            Client.Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ExternalJobHandle h, CancellationToken _) =>
                    new ExternalJobStatus(ExternalJobState.Succeeded, $"https://example.invalid/{h.RemoteJobId}.mp4", null));
            Client.Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream([1, 2, 3, 4]));

            Workspace.Setup(w => w.ArtifactExistsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Workspace.Setup(w => w.UploadArtifactAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .Callback<Guid, string, string, string, CancellationToken, string>((_, local, _, contentType, _, _) =>
                {
                    if (contentType == "application/json")
                    {
                        UploadedManifest = File.ReadAllText(local);
                    }
                })
                .ReturnsAsync((Guid p, string _, string name, string _, CancellationToken _, string _) => $"projects/{p:D}/agentFiles/{name}");
            Workspace.Setup(w => w.DownloadStorageKeyToFileAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, string _, string dest, long _, CancellationToken _) =>
                    File.WriteAllTextAsync(dest, PreviousManifest.ToJsonString()));
        }

        public string PreviousKey(string clipId) =>
            PreviousKeyOverride ?? $"projects/{ProjectId}/agentFiles/generated/{SourceExecutionId}/step-3-{clipId}.mp4";

        /// <summary>The finished step's manifest: one succeeded clip per shot (and take), as the real executor writes it.</summary>
        public void SeedPreviousManifest()
        {
            JsonArray clips = [];
            int ordinal = 0;
            for (int shot = 1; shot <= Shots; shot++)
            {
                for (int take = 1; take <= Takes; take++)
                {
                    ordinal++;
                    string id = Takes == 1 ? $"v{shot}" : $"v{shot}t{take}";
                    clips.Add(new JsonObject
                    {
                        ["clipId"] = id,
                        ["shotIndex"] = shot,
                        ["take"] = take,
                        ["purpose"] = "Cutaway",
                        ["anchor"] = "s1",
                        ["firstFrame"] = null,
                        ["lastFrame"] = null,
                        ["camera"] = "",
                        ["duration"] = "Medium",
                        ["description"] = Prompts[shot - 1],
                        ["durationSeconds"] = 9,
                        ["status"] = "succeeded",
                        ["storageKey"] = PreviousKey(id),
                        ["cost"] = 120m,
                        ["costBasis"] = "actual",
                        ["keyframeRequested"] = false,
                        ["keyframeApplied"] = false,
                        ["keyframeReason"] = null,
                        ["failureReason"] = null,
                        ["detail"] = null,
                        ["skipReason"] = null,
                        ["reused"] = false,
                        ["lookWords"] = null,
                        ["cameraDefaulted"] = false
                    });
                }
            }

            PreviousManifest = new JsonObject { ["version"] = 1, ["clips"] = clips, ["meta"] = new JsonObject() };
        }

        public (VideoGenerateStepExecutor Executor, StepExecutionContext Context) Build(StepChangeRequestScope? scope)
        {
            var provider = new ResolvedVideoGenerationProvider(ProviderId, "MiniMax", InferenceProviderKind.MiniMax, "https://api.minimax.io", Model, "key123", 1800);
            var resolver = new Mock<IInferenceProviderResolver>();
            resolver.Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(provider);
            var clientFactory = new Mock<IVideoGenerationClientFactory>();
            clientFactory.Setup(f => f.Get(provider)).Returns(Client.Object);
            var normalizer = new GeneratedClipNormalizer(
                WorkingRunner().Object, Options.Create(new VideoEditingOptions { AnalyzeTimeoutSeconds = 10, CompileTimeoutSeconds = 60 }),
                NullLogger<GeneratedClipNormalizer>.Instance);

            var executor = new VideoGenerateStepExecutor(
                Gate, resolver.Object, clientFactory.Object, Workspace.Object, Db, normalizer,
                Options.Create(new VideoGenerationOptions()), NullLogger<VideoGenerateStepExecutor>.Instance,
                new Mock<IMediaWorkspaceFactory>().Object, new Mock<IKeyframeExtractor>().Object,
                moderation: Moderation, changeRequests: new FixedContext(scope));

            string config = JsonSerializer.Serialize(new VideoGenerateStepConfig
            {
                PlanSource = VideoGeneratePlanSource.StepRef,
                Plan = new ExtractInputRef(ExtractInputSource.Step, StepOrder: PlanStepOrder),
                Model = Model,
                Resolution = Resolution,
                Ratio = Ratio,
                DurationSeconds = DurationSeconds,
                Takes = Takes,
                MaxClips = 4,
                MaxSpendCredits = StepCap,
                TimeoutSeconds = 60,
                PollIntervalSeconds = 1
            }, ConfigJsonOptions);
            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                StepOrder = TargetStepOrder,
                StepType = StepType.VideoGenerate,
                VideoGenerateConfigJson = config
            };
            string plan = JsonSerializer.Serialize(new GeneratedShotPlanOutput
            {
                Shots = Prompts.Take(Shots).Select(p => new ShotPlan
                {
                    Purpose = "Cutaway",
                    Anchor = "s1",
                    Camera = "",
                    Duration = "Medium",
                    Prompt = p,
                    Reason = "test"
                }).ToList(),
                PlanRationale = "test"
            }, ConfigJsonOptions);
            var context = new StepExecutionContext
            {
                StepResultId = Guid.NewGuid(),
                Execution = new WorkflowExecution { Id = ExecutionId, ProjectId = ProjectId, Status = ExecutionStatus.Running },
                Step = step,
                AllSteps = [step],
                AccumulatedOutput = plan,
                CurrentStepIndex = 1,
                IterationCount = 0,
                CorrelationId = "test-correlation",
                StepOutputHistory = [new StepOutputHistoryEntry(PlanStepOrder, "ShotDirector", plan, OutputStorageKey: null, ArtifactStorageKey: null)],
                CancellationToken = CancellationToken.None
            };
            return (executor, context);
        }

        public async Task<StepExecutionResult> RunAsync(ChangeRequestPayload? payload, string? artifactKey = null, bool ordinary = false, bool malformed = false)
        {
            StepChangeRequestScope? scope = ordinary || payload is null
                ? null
                : new StepChangeRequestScope(RequestId, payload, null, artifactKey ?? ManifestKey) { Malformed = malformed };
            var (executor, context) = Build(scope);
            return await executor.ExecuteAsync(context);
        }

        public string Hash(string prompt, int ordinal) => HashOf(prompt, ordinal);

        public async Task<ExternalGenerationJob> SeedSucceededAsync(string prompt, int ordinal, string storageKey)
        {
            var job = new ExternalGenerationJob
            {
                Id = Guid.NewGuid(),
                ExecutionId = SourceExecutionId,
                StepResultId = Guid.NewGuid(),
                ProjectId = ProjectId,
                ClipId = $"v{ordinal}",
                RequestHash = HashOf(prompt, ordinal),
                ProviderId = ProviderId,
                RemoteJobId = "old-remote",
                Status = ExternalGenerationJobStatus.Succeeded,
                CostEstimate = 120m,
                ActualCost = 120m,
                StorageKey = storageKey,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await using var db = Db.CreateDbContext();
            db.ExternalGenerationJobs.Add(job);
            await db.SaveChangesAsync();
            return job;
        }
    }

    private static string HashOf(string prompt, int ordinal) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{Model}|{Resolution}|{Ratio}|{DurationSeconds}|{ordinal}|{prompt}")));

    private static Mock<IVideoToolRunner> WorkingRunner()
    {
        const string probeJson = """
            { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 640, "height": 480,
              "r_frame_rate": "30/1", "duration": "6" } ], "format": { "duration": "6" } }
            """;
        var runner = new Mock<IVideoToolRunner>();
        runner.Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: probeJson, StdErr: "", TimedOut: false));
        runner.Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: "", StdErr: "", TimedOut: false));
        runner.Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: "", StdErr: "", TimedOut: false));
        return runner;
    }

    private static ChangeRequestPayload Request(string[]? ids, params (string Item, string Key, object Value)[] overrides)
    {
        Dictionary<string, IReadOnlyDictionary<string, JsonElement>> map = [];
        foreach (var group in overrides.GroupBy(o => o.Item))
        {
            map[group.Key] = group.ToDictionary(o => o.Key, o => JsonSerializer.SerializeToElement(o.Value));
        }

        return new ChangeRequestPayload("redo", ids, map);
    }

    private static JsonObject Output(StepExecutionResult r) => (JsonObject)JsonNode.Parse(r.Output)!;

    private static JsonObject Manifest(Rig rig) => (JsonObject)JsonNode.Parse(rig.UploadedManifest!)!;

    private static int LedgerRows(Rig rig)
    {
        using var db = rig.Db.CreateDbContext();
        return db.ExternalGenerationJobs.Count();
    }

    // ------------------------------------------------------------------ carry-over

    [Fact]
    public async Task Changing_shot_two_buys_one_clip_and_carries_the_other_manifest_entries_verbatim_in_order()
    {
        var rig = new Rig();
        rig.SeedPreviousManifest();

        StepExecutionResult result = await rig.RunAsync(Request(["v2"], ("v2", "prompt", "A brand new second shot")));

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        rig.Gate.Requests.Should().ContainSingle("only the redone clip is reserved");
        rig.Gate.Requests[0].Takes.Should().ContainSingle().Which.ClipId.Should().Be("v2");
        rig.Submitted.Should().ContainSingle().Which.PromptText.Should().Be("A brand new second shot");
        LedgerRows(rig).Should().Be(1);

        JsonArray clips = (JsonArray)Manifest(rig)["clips"]!;
        clips.Select(c => c!["clipId"]!.GetValue<string>()).Should().Equal("v1", "v2", "v3");
        foreach (int i in new[] { 0, 2 })
        {
            JsonObject expected = (JsonObject)rig.PreviousManifest["clips"]![i]!.DeepClone();
            expected["cost"] = 0m;
            expected["costBasis"] = "carried";
            JsonNode.DeepEquals(clips[i], expected).Should().BeTrue($"clip {i} is carried over verbatim, but this run paid nothing for it");
        }
        clips[1]!["description"]!.GetValue<string>().Should().Be("A brand new second shot");
        clips[1]!["storageKey"]!.GetValue<string>().Should().NotBe(rig.PreviousKey("v2")).And.StartWith($"projects/{ProjectId}/agentFiles/generated/");

        JsonObject change = (JsonObject)Output(result)["meta"]!["changeRequest"]!;
        change["requestId"]!.GetValue<string>().Should().Be(RequestId.ToString("D"));
        change["redoneClipIds"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("v2");
        change["reusedClipCount"]!.GetValue<int>().Should().Be(2);
        Output(result)["clips"]!.AsArray().Select(c => c!["clipId"]!.GetValue<string>()).Should().Equal("v1", "v2", "v3");
        result.OutputStorageKey.Should().Be(rig.PreviousKey("v1"), "the first delivered clip stays the first");
        ((JsonObject)Output(result)["meta"]!)["totalCost"]!.GetValue<decimal>().Should().BeLessThan(240m, "the carried clips were paid for in the finished run");
    }

    [Fact]
    public async Task A_retake_with_an_unchanged_prompt_makes_a_new_ledger_row_and_a_fresh_idempotency_key()
    {
        var rig = new Rig();
        rig.SeedPreviousManifest();
        ExternalGenerationJob earlier = await rig.SeedSucceededAsync("Shot two", 2, rig.PreviousKey("v2"));

        StepExecutionResult result = await rig.RunAsync(Request(["v2"], ("v2", "retake", true)));

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        rig.Gate.Requests.Should().ContainSingle();
        rig.Submitted.Should().ContainSingle();
        rig.Submitted[0].PromptText.Should().Be("Shot two");
        rig.Submitted[0].IdempotencyKey.Should().NotBe(earlier.Id.ToString("D"), "a retake is a new purchase");
        using var db = rig.Db.CreateDbContext();
        db.ExternalGenerationJobs.Count().Should().Be(2);
        db.ExternalGenerationJobs.Single(j => j.Id != earlier.Id).RequestHash.Should().NotBe(earlier.RequestHash);
    }

    [Fact]
    public async Task An_unchanged_re_request_without_retake_is_the_free_content_hit()
    {
        var rig = new Rig();
        rig.SeedPreviousManifest();
        ExternalGenerationJob earlier = await rig.SeedSucceededAsync("Shot two", 2, rig.PreviousKey("v2"));

        StepExecutionResult result = await rig.RunAsync(Request(["v2"], ("v2", "retake", false)));

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        rig.Gate.Requests.Should().BeEmpty("nothing to buy: the identical clip already exists");
        rig.Submitted.Should().BeEmpty();
        LedgerRows(rig).Should().Be(1, "reuse writes no ledger row");
        JsonNode clip = ((JsonArray)Manifest(rig)["clips"]!)[1]!;
        clip["reused"]!.GetValue<bool>().Should().BeTrue();
        clip["storageKey"]!.GetValue<string>().Should().Be(earlier.StorageKey);
        Output(result)["meta"]!["changeRequest"]!["redoneClipIds"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("v2");
    }

    [Fact]
    public async Task A_star_retake_redoes_every_clip_in_one_reservation()
    {
        var rig = new Rig();
        rig.SeedPreviousManifest();

        StepExecutionResult result = await rig.RunAsync(Request(null, ("*", "retake", true)));

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        rig.Gate.Requests.Should().ContainSingle().Which.Takes.Should().HaveCount(3);
        rig.Submitted.Should().HaveCount(3);
        Output(result)["meta"]!["changeRequest"]!["reusedClipCount"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task One_named_take_of_a_multi_take_step_is_the_only_clip_bought()
    {
        var rig = new Rig { Shots = 2, Takes = 2 };
        rig.SeedPreviousManifest();

        StepExecutionResult result = await rig.RunAsync(Request(null, ("v2t2", "prompt", "Another take of the second shot")));

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        rig.Gate.Requests.Should().ContainSingle().Which.Takes.Should().ContainSingle().Which.ClipId.Should().Be("v2t2");
        rig.Submitted.Should().ContainSingle();
        ((JsonArray)Manifest(rig)["clips"]!).Select(c => c!["clipId"]!.GetValue<string>()).Should().Equal("v1t1", "v1t2", "v2t1", "v2t2");
        Output(result)["meta"]!["changeRequest"]!["reusedClipCount"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task Naming_a_shot_covers_all_of_its_takes()
    {
        var rig = new Rig { Shots = 2, Takes = 2 };
        rig.SeedPreviousManifest();

        StepExecutionResult result = await rig.RunAsync(Request(["v1"], ("v1", "retake", true)));

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        rig.Gate.Requests.Should().ContainSingle().Which.Takes.Select(t => t.ClipId).Should().Equal("v1t1", "v1t2");
    }

    // ------------------------------------------------------------------ the prompt, moderation, budget

    [Fact]
    public async Task The_new_prompt_goes_through_the_sanitiser()
    {
        var rig = new Rig();
        rig.SeedPreviousManifest();

        StepExecutionResult result = await rig.RunAsync(Request(["v2"], ("v2", "prompt", "A\u0007 new\u0001 prompt")));

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        rig.Submitted.Should().ContainSingle().Which.PromptText.Should().Be("A new prompt");
    }

    [Fact]
    public async Task An_over_long_new_prompt_is_refused_before_anything_is_reserved_and_never_replaces_a_good_clip()
    {
        var rig = new Rig();
        rig.SeedPreviousManifest();

        StepExecutionResult result = await rig.RunAsync(Request(["v2"], ("v2", "prompt", new string('a', 7001))));

        result.Status.Should().Be(StepStatus.Failed);
        Output(result)["meta"]!["failed"]!.GetValue<string>().Should().Be("INVALID_PROMPT");
        rig.Gate.Requests.Should().BeEmpty();
        rig.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_blocked_new_prompt_stops_before_any_reservation_and_the_carried_prompts_are_not_screened_again()
    {
        var rig = new Rig { Moderation = new FakeModeration(t => t.Contains("BAD", StringComparison.Ordinal)) };
        rig.SeedPreviousManifest();

        StepExecutionResult result = await rig.RunAsync(Request(["v2"], ("v2", "prompt", "something BAD")));

        result.Status.Should().Be(StepStatus.Failed);
        Output(result)["meta"]!["failed"]!.GetValue<string>().Should().Be("CONTENT_BLOCKED");
        rig.Moderation!.Texts.Should().Equal("something BAD");
        rig.Gate.Requests.Should().BeEmpty();
        rig.Submitted.Should().BeEmpty();
        LedgerRows(rig).Should().Be(0);
    }

    [Fact]
    public async Task The_step_cap_counts_only_the_new_takes()
    {
        // Each Medium shot costs 120 credits: three shots (360) exceed a 150 cap, one (120) does not.
        var change = new Rig { StepCap = 150m };
        change.SeedPreviousManifest();
        StepExecutionResult redone = await change.RunAsync(Request(["v2"], ("v2", "retake", true)));
        redone.Status.Should().Be(StepStatus.Completed, redone.Output);
        change.Gate.Requests.Should().ContainSingle();

        var ordinary = new Rig { StepCap = 150m };
        ordinary.SeedPreviousManifest();
        StepExecutionResult whole = await ordinary.RunAsync(null, ordinary: true);
        whole.Status.Should().Be(StepStatus.Failed);
        Output(whole)["meta"]!["failed"]!.GetValue<string>().Should().Be("BUDGET_EXCEEDED");
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public async Task A_malformed_stored_request_fails_CHANGE_NOT_APPLIED_and_buys_nothing()
    {
        var rig = new Rig();
        rig.SeedPreviousManifest();

        StepExecutionResult result = await rig.RunAsync(Request(null), malformed: true);

        result.Status.Should().Be(StepStatus.Failed);
        Output(result)["meta"]!["failed"]!.GetValue<string>().Should().Be("CHANGE_NOT_APPLIED");
        rig.Gate.Requests.Should().BeEmpty();
        rig.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_request_that_matches_no_clip_fails_CHANGE_NOT_APPLIED_and_buys_nothing()
    {
        var rig = new Rig();
        rig.SeedPreviousManifest();

        StepExecutionResult result = await rig.RunAsync(Request(["v9"], ("v9", "retake", true)));

        result.Status.Should().Be(StepStatus.Failed);
        Output(result)["meta"]!["failed"]!.GetValue<string>().Should().Be("CHANGE_NOT_APPLIED");
        rig.Gate.Requests.Should().BeEmpty();
        rig.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_foreign_manifest_key_is_refused_and_never_read()
    {
        var rig = new Rig();
        rig.SeedPreviousManifest();
        string foreign = $"projects/{Guid.NewGuid()}/agentFiles/video-analysis/{SourceExecutionId}/step-3-generated-clips.json";

        StepExecutionResult result = await rig.RunAsync(Request(["v2"], ("v2", "retake", true)), artifactKey: foreign);

        result.Status.Should().Be(StepStatus.Failed);
        Output(result)["meta"]!["failed"]!.GetValue<string>().Should().Be("CHANGE_NOT_APPLIED");
        rig.Workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Gate.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_carried_clip_whose_stored_key_is_outside_this_project_is_refused_before_anything_is_bought()
    {
        var rig = new Rig { PreviousKeyOverride = $"projects/{Guid.NewGuid()}/agentFiles/generated/x/step-3-v.mp4" };
        rig.SeedPreviousManifest();

        StepExecutionResult result = await rig.RunAsync(Request(["v2"], ("v2", "retake", true)));

        result.Status.Should().Be(StepStatus.Failed);
        Output(result)["meta"]!["failed"]!.GetValue<string>().Should().Be("CHANGE_NOT_APPLIED");
        rig.Gate.Requests.Should().BeEmpty();
        rig.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_without_a_change_request_is_unchanged()
    {
        var rig = new Rig();
        rig.SeedPreviousManifest();

        StepExecutionResult result = await rig.RunAsync(null, ordinary: true);

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        rig.Gate.Requests.Should().ContainSingle().Which.Takes.Should().HaveCount(3);
        Output(result)["meta"]!["changeRequest"].Should().BeNull();
    }

    // ------------------------------------------------------------------ the registry

    [Fact]
    public void The_items_are_the_shots_and_their_takes_with_status_duration_and_the_prompt_used()
    {
        string oneTake = JsonSerializer.Serialize(new
        {
            clips = new object[]
            {
                new { clipId = "v1", status = "succeeded", description = "First", durationSeconds = 9 },
                new { clipId = "v2", status = "failed", failureReason = "X" }
            }
        });
        IReadOnlyList<StepItem> items = StepChangeCapabilities.ListItems(StepType.VideoGenerate, oneTake);
        items.Select(i => i.Id).Should().Equal("v1", "v2");
        items[0].Should().Be(new StepItem("v1", "succeeded", 9, "First"));
        items[1].Status.Should().Be("failed");

        string twoTakes = JsonSerializer.Serialize(new
        {
            clips = new object[]
            {
                new { clipId = "v1t1", status = "failed" },
                new { clipId = "v1t2", status = "succeeded", description = new string('p', 500) },
                new { clipId = "v2t1", status = "succeeded" }
            }
        });
        IReadOnlyList<StepItem> multi = StepChangeCapabilities.ListItems(StepType.VideoGenerate, twoTakes);
        multi.Select(i => i.Id).Should().Equal("v1", "v1t1", "v1t2", "v2", "v2t1");
        multi.Single(i => i.Id == "v1").Status.Should().Be("succeeded", "the shot reads as its best take");
        multi.Single(i => i.Id == "v1t2").Text!.Length.Should().BeLessThan(300, "the item text is the prompt, shortened");

        StepChangeCapabilities.ListItems(StepType.VideoGenerate, "not json").Should().BeEmpty();
        StepChangeCapabilities.ListItems(StepType.VideoGenerate, """{"clips":"x"}""").Should().BeEmpty();
        StepChangeCapabilities.ListItems(StepType.VideoGenerate, null).Should().BeEmpty();
    }

    [Fact]
    public void The_validator_takes_a_prompt_and_a_retake_flag_per_shot_or_take()
    {
        string output = JsonSerializer.Serialize(new { clips = new object[] { new { clipId = "v1t1", status = "succeeded" }, new { clipId = "v1t2", status = "succeeded" } } });

        ChangeRequestValidator.Validate(StepType.VideoGenerate, Request(["v1"], ("v1", "retake", true)), output).Should().BeEmpty();
        ChangeRequestValidator.Validate(StepType.VideoGenerate, Request(null, ("v1t2", "prompt", "New")), output).Should().BeEmpty();
        ChangeRequestValidator.Validate(StepType.VideoGenerate, Request(null, ("*", "retake", true)), output).Should().BeEmpty();
        ChangeRequestValidator.Validate(StepType.VideoGenerate, Request(null, ("v1", "retake", "yes")), output)
            .Should().ContainSingle().Which.Path.Should().Be("overrides.v1.retake");
        ChangeRequestValidator.Validate(StepType.VideoGenerate, Request(null, ("v1", "prompt", new string('a', 2001))), output)
            .Should().ContainSingle().Which.Path.Should().Be("overrides.v1.prompt");
        ChangeRequestValidator.Validate(StepType.VideoGenerate, Request(null, ("v7", "retake", true)), output)
            .Should().ContainSingle().Which.Message.Should().Be("unknown item");
        ChangeRequestValidator.Validate(StepType.VideoGenerate, Request(null, ("v1", "voiceId", "x")), output)
            .Should().ContainSingle().Which.Message.Should().Be("unknown override");
    }
}
