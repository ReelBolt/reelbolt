using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Issue #93 phases 4-5 on <see cref="VideoGenerateStepExecutor"/>'s plan-sourced path: look
/// matching appends the anchor shot's MEASURED look words to the prompt that leaves the building,
/// camera continuity fills a blank camera word from the anchor's measured move (never overriding a
/// word the planner chose), and a ComfyUI provider is budgeted in GPU-seconds through the same gate.
/// Every client is a Moq proxy; no vendor, no GPU, no credential.
/// </summary>
public class VideoGeneratePhase4ExecutorTests
{
    private static readonly Guid ProviderId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid ExecutionId = Guid.NewGuid();
    private static readonly Guid StepResultId = Guid.NewGuid();

    private const int PlanStepOrder = 2;
    private const int AnalysisStepOrder = 1;
    private static readonly string AnalysisStorageKey = $"projects/{ProjectId}/agentFiles/video-analysis/{ExecutionId}/step-1-analysis.json";

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    // ---- the analysis artifact: one measured shot s1 (Warm/Contrasty/Vivid, Handheld), one unmeasured s2 ----

    private const string ArtifactJson = """
        {
          "version": 2,
          "media": { "durationSec": 30.0, "fpsNum": 30, "fpsDen": 1, "width": 1920, "height": 1080 },
          "shots": [
            { "id": "s1", "startSec": 0.0, "endSec": 10.0, "visual": {
                "motionMean": 0.1, "motionPeak": 0.2, "motionStdDev": 0.05, "motionClass": "Slow", "cameraMove": "Handheld",
                "cameraMoveConfidence": 0.9, "headMotion": 0.1, "tailMotion": 0.1, "stillWindows": [], "brightnessMean": 0.5,
                "brightnessStdDev": 0.1, "contrastRms": 0.3, "clippedHighlightRatio": 0.0, "crushedBlackRatio": 0.0,
                "saturationMean": 0.6, "dominantColors": [], "regions": [], "bestOverlayRegion": null, "activeCrop": null,
                "sharpness": null, "kenBurnsCandidate": false, "kenBurnsReason": null, "duplicateGroupId": null,
                "groupRank": null, "isBestTake": false,
                "colorTemperatureClass": "Warm", "toneClass": "Contrasty", "saturationClass": "Vivid" } },
            { "id": "s2", "startSec": 10.0, "endSec": 20.0 }
          ],
          "silenceSpans": [],
          "segments": [],
          "words": [],
          "offeredIds": ["s1", "s2"],
          "provenance": { "transcriptionMode": "Off", "transcriptionApplied": false, "transcriptionDegraded": false }
        }
        """;

    [Fact]
    public async Task LookMatching_appends_the_anchor_shots_measured_look_words_to_the_submitted_prompt()
    {
        var submitted = new List<VideoGenerationRequest>();
        var (executor, context) = Build(
            MiniMaxSubmittingClient(submitted, "r1", "r2"),
            Config(lookMatching: true),
            PlanJson(Shot("A drone shot over a bay", anchor: "s1"), Shot("A close-up of a keyboard", anchor: "s2")));

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails ?? result.Output);
        submitted.Should().HaveCount(2);
        submitted[0].PromptText.Should().Be(
            "A drone shot over a bay Match the look of the surrounding footage: warm colour temperature with amber highlights, high contrast with deep blacks, vivid, saturated colours.");
        submitted[1].PromptText.Should().Be("A close-up of a keyboard", "an unmeasured anchor yields no clause — the prompt is byte-identical");
    }

    [Fact]
    public async Task LookMatching_off_leaves_the_prompt_byte_identical_even_with_a_measured_anchor()
    {
        var submitted = new List<VideoGenerationRequest>();
        var (executor, context) = Build(
            MiniMaxSubmittingClient(submitted, "r1"),
            Config(lookMatching: false),
            PlanJson(Shot("A drone shot over a bay", anchor: "s1")));

        await executor.ExecuteAsync(context);

        submitted.Single().PromptText.Should().Be("A drone shot over a bay");
    }

    [Fact]
    public async Task CameraContinuity_fills_a_blank_camera_word_from_the_anchor_and_never_overrides_a_chosen_one()
    {
        var submitted = new List<VideoGenerationRequest>();
        var log = new UploadLog();
        var (executor, context) = Build(
            MiniMaxSubmittingClient(submitted, "r1", "r2"),
            Config(cameraContinuity: true),
            PlanJson(Shot("Blank camera", anchor: "s1", camera: ""), Shot("Planner chose", anchor: "s1", camera: "PushIn")),
            log);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails ?? result.Output);
        // The continuity word is a planner camera word, so it goes through the same mapper and
        // lands in the prompt as the Handheld phrase; the planner's own PushIn is untouched.
        submitted[0].PromptText.Should().Contain(ShotPlanParameterMapper.CameraPhraseFor("Handheld")!);
        submitted[1].PromptText.Should().Contain(ShotPlanParameterMapper.CameraPhraseFor("PushIn")!);
        submitted[1].PromptText.Should().NotContain(ShotPlanParameterMapper.CameraPhraseFor("Handheld")!);

        JsonArray clips = ManifestRoot(log)!["clips"]!.AsArray();
        clips[0]!["cameraDefaulted"]!.GetValue<bool>().Should().BeTrue();
        clips[1]!["cameraDefaulted"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task A_ComfyUI_provider_is_budgeted_in_GPU_seconds_and_its_ledger_rows_say_so()
    {
        var submitted = new List<VideoGenerationRequest>();
        var log = new UploadLog();
        var factory = new SharedInMemoryDbFactory();
        var (executor, context) = Build(
            ComfyUiSubmittingClient(submitted, "11111111-1111-1111-1111-111111111111"),
            ComfyConfig(maxGpuMinutes: 10m),
            PlanJson(Shot("A lighthouse at dusk", anchor: "s2", duration: "Short")),
            log, factory,
            kind: InferenceProviderKind.ComfyUI, model: ComfyUiWorkflowTemplates.TextToVideoName, resolution: "480P");

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails ?? result.Output);
        JsonNode meta = JsonNode.Parse(result.Output)!["meta"]!;
        meta["costUnit"]!.GetValue<string>().Should().Be("gpuSeconds");

        using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Should().ContainSingle().Which.CostUnit.Should().Be(ExternalGenerationJob.GpuSecondsUnit);
        // Short at 480P on the template table: 1 s x 20 GPU-seconds/s — a GPU figure, not a price.
        db.ExternalGenerationJobs.Single().CostEstimate.Should().Be(20m);
    }

    [Fact]
    public async Task A_ComfyUI_plan_over_its_GPU_cap_fails_BUDGET_EXCEEDED_in_GPU_second_vocabulary_with_zero_submits()
    {
        var submitted = new List<VideoGenerationRequest>();
        var factory = new SharedInMemoryDbFactory();
        var (executor, context) = Build(
            ComfyUiSubmittingClient(submitted, "11111111-1111-1111-1111-111111111111"),
            // 0.1 GPU-minutes = 6 GPU-seconds; the cheapest shot costs 20.
            ComfyConfig(maxGpuMinutes: 0.1m),
            PlanJson(Shot("A lighthouse at dusk", anchor: "s2", duration: "Short")),
            factory: factory,
            kind: InferenceProviderKind.ComfyUI, model: ComfyUiWorkflowTemplates.TextToVideoName, resolution: "480P");

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        JsonNode meta = JsonNode.Parse(result.Output)!["meta"]!;
        meta["failed"]!.GetValue<string>().Should().Be("BUDGET_EXCEEDED");
        meta["error"]!.GetValue<string>().Should().Contain("GPU-minutes").And.NotContain("$");
        submitted.Should().BeEmpty();
        using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_ComfyUI_provider_with_no_GPU_cap_is_CONFIG_INVALID_before_anything_is_bought()
    {
        var submitted = new List<VideoGenerationRequest>();
        var (executor, context) = Build(
            ComfyUiSubmittingClient(submitted, "11111111-1111-1111-1111-111111111111"),
            // A USD cap only: meaningless for a $0 self-hosted server.
            ComfyConfig(maxGpuMinutes: 0m, stepCap: 5m),
            PlanJson(Shot("A lighthouse at dusk", anchor: "s2", duration: "Short")),
            kind: InferenceProviderKind.ComfyUI, model: ComfyUiWorkflowTemplates.TextToVideoName, resolution: "480P");

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        JsonNode.Parse(result.Output)!["meta"]!["failed"]!.GetValue<string>().Should().Be("CONFIG_INVALID");
        submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_submit_carries_the_ledger_row_id_as_its_idempotency_key()
    {
        var submitted = new List<VideoGenerationRequest>();
        var factory = new SharedInMemoryDbFactory();
        var (executor, context) = Build(
            MiniMaxSubmittingClient(submitted, "r1"),
            Config(),
            PlanJson(Shot("A drone shot over a bay", anchor: "s2")),
            factory: factory);

        await executor.ExecuteAsync(context);

        using var db = factory.CreateDbContext();
        Guid rowId = db.ExternalGenerationJobs.Single().Id;
        submitted.Single().IdempotencyKey.Should().Be(rowId.ToString("D"),
            "the durable ledger row id is what makes a retry re-present the SAME key instead of risking a second charge");
    }

    // ==========================================================================================
    // Harness (self-contained mirror of VideoGeneratePlanSourcedExecutorTests' own)
    // ==========================================================================================

    private static string Config(bool lookMatching = false, bool cameraContinuity = false) =>
        JsonSerializer.Serialize(new VideoGenerateStepConfig
        {
            PlanSource = VideoGeneratePlanSource.StepRef,
            Plan = new ExtractInputRef(ExtractInputSource.Step, StepOrder: PlanStepOrder),
            Analysis = new ExtractInputRef(ExtractInputSource.Step, StepOrder: AnalysisStepOrder),
            Model = "MiniMax-H3",
            Resolution = "768P",
            Ratio = "16:9",
            DurationSeconds = 6,
            Takes = 1,
            MaxClips = 4,
            MaxSpendCredits = 100000m,
            TimeoutSeconds = 60,
            PollIntervalSeconds = 1,
            LookMatching = lookMatching,
            CameraContinuity = cameraContinuity
        }, ConfigJsonOptions);

    private static string ComfyConfig(decimal maxGpuMinutes, decimal stepCap = 0m) =>
        JsonSerializer.Serialize(new VideoGenerateStepConfig
        {
            PlanSource = VideoGeneratePlanSource.StepRef,
            Plan = new ExtractInputRef(ExtractInputSource.Step, StepOrder: PlanStepOrder),
            Analysis = new ExtractInputRef(ExtractInputSource.Step, StepOrder: AnalysisStepOrder),
            Model = ComfyUiWorkflowTemplates.TextToVideoName,
            Resolution = "480P",
            Ratio = "16:9",
            DurationSeconds = 3,
            Takes = 1,
            MaxClips = 4,
            MaxSpendCredits = stepCap,
            MaxGpuMinutes = maxGpuMinutes,
            TimeoutSeconds = 60,
            PollIntervalSeconds = 1
        }, ConfigJsonOptions);

    private static ShotPlan Shot(string prompt, string anchor, string camera = "", string duration = "Medium") => new()
    {
        Purpose = "Cutaway",
        Anchor = anchor,
        Camera = camera,
        Duration = duration,
        Prompt = prompt,
        Reason = "test"
    };

    private static string PlanJson(params ShotPlan[] shots) =>
        JsonSerializer.Serialize(new GeneratedShotPlanOutput { Shots = shots.ToList(), PlanRationale = "test" }, ConfigJsonOptions);

    private static Mock<IVideoGenerationClient> MiniMaxSubmittingClient(List<VideoGenerationRequest> submitted, params string[] remoteIds)
    {
        var pricing = new MiniMaxVideoGenerationClient(new Mock<HttpClient>().Object, "key123");
        return SubmittingClient(submitted, remoteIds, model => pricing.Describe(model));
    }

    private static Mock<IVideoGenerationClient> ComfyUiSubmittingClient(List<VideoGenerationRequest> submitted, params string[] remoteIds) =>
        SubmittingClient(submitted, remoteIds, model =>
            ComfyUiWorkflowTemplates.TryParse(model, out ComfyUiWorkflowTemplate t)
                ? ComfyUiWorkflowTemplates.Describe(t)
                : throw new NotSupportedException(model));

    private static Mock<IVideoGenerationClient> SubmittingClient(
        List<VideoGenerationRequest> submitted, string[] remoteIds, Func<string, VideoGenerationCapabilities> describe)
    {
        var client = new Mock<IVideoGenerationClient>();
        int i = 0;
        client.Setup(c => c.Describe(It.IsAny<string>())).Returns(describe);
        client
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<VideoGenerationRequest, CancellationToken>((r, _) => submitted.Add(r))
            .ReturnsAsync(() => new ExternalJobHandle(remoteIds[i++]));
        client
            .Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalJobHandle h, CancellationToken _) =>
                new ExternalJobStatus(ExternalJobState.Succeeded, $"https://example.invalid/{h.RemoteJobId}.mp4", null));
        client
            .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3, 4]));
        return client;
    }

    private static Mock<IVideoToolRunner> WorkingRunner()
    {
        const string probeJson = """
            { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 640, "height": 480,
              "r_frame_rate": "30/1", "duration": "6" } ], "format": { "duration": "6" } }
            """;
        var runner = new Mock<IVideoToolRunner>();
        runner
            .Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: probeJson, StdErr: "", TimedOut: false));
        runner
            .Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: "", StdErr: "", TimedOut: false));
        runner
            .Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: "", StdErr: "", TimedOut: false));
        return runner;
    }

    private sealed class UploadLog
    {
        public string? ManifestJson { get; set; }
    }

    private static Mock<IProjectFileWorkspace> Workspace(UploadLog log)
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>(
                (_, localPath, _, contentType, _, _) =>
                {
                    if (contentType == "application/json")
                        log.ManifestJson = File.ReadAllText(localPath);
                })
            .ReturnsAsync((Guid projectId, string _, string name, string _, CancellationToken _, string _) =>
                $"projects/{projectId:D}/agentFiles/{name}");
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisStorageKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, ArtifactJson);
                return Task.CompletedTask;
            });
        return workspace;
    }

    private static JsonObject? ManifestRoot(UploadLog log) =>
        log.ManifestJson is null ? null : (JsonObject)JsonNode.Parse(log.ManifestJson)!;

    private static (VideoGenerateStepExecutor Executor, StepExecutionContext Context) Build(
        Mock<IVideoGenerationClient> client,
        string configJson,
        string planJson,
        UploadLog? log = null,
        SharedInMemoryDbFactory? factory = null,
        InferenceProviderKind kind = InferenceProviderKind.MiniMax,
        string model = "MiniMax-H3",
        string resolution = "768P")
    {
        _ = resolution;
        factory ??= new SharedInMemoryDbFactory();
        log ??= new UploadLog();

        var provider = new ResolvedVideoGenerationProvider(
            ProviderId, kind.ToString(), kind, kind == InferenceProviderKind.ComfyUI ? "http://comfyui:8188" : "https://api.minimax.io", model, "key123", 1800);

        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var clientFactory = new Mock<IVideoGenerationClientFactory>();
        clientFactory.Setup(f => f.Get(provider)).Returns(client.Object);

        var normalizer = new GeneratedClipNormalizer(
            WorkingRunner().Object,
            Options.Create(new VideoEditingOptions { AnalyzeTimeoutSeconds = 10, CompileTimeoutSeconds = 60 }),
            NullLogger<GeneratedClipNormalizer>.Instance);

        var executor = new VideoGenerateStepExecutor(
            new VideoGenerationBudgetGate(
                factory,
                Options.Create(new VideoGenerationOptions
                {
                    DailyBudgetCredits = 1_000_000m,
                    GlobalDailyBudgetCredits = 1_000_000m,
                    DailyGpuMinutes = 1000m,
                    GlobalDailyGpuMinutes = 1000m
                })),
            resolver.Object,
            clientFactory.Object,
            Workspace(log).Object,
            factory,
            normalizer,
            Options.Create(new VideoGenerationOptions()),
            NullLogger<VideoGenerateStepExecutor>.Instance,
            // These tests are about the PROMPT that leaves the building; no plan they build names a
            // frame, so both are strict: a frame request reaching either one fails loudly.
            new Mock<IMediaWorkspaceFactory>(MockBehavior.Strict).Object,
            new Mock<IKeyframeExtractor>(MockBehavior.Strict).Object);

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = configJson
        };

        var history = new List<StepOutputHistoryEntry>
        {
            new(AnalysisStepOrder, "Analyze", """{"view":{},"meta":{}}""", OutputStorageKey: null, ArtifactStorageKey: AnalysisStorageKey),
            new(PlanStepOrder, "ShotDirector", planJson, OutputStorageKey: null, ArtifactStorageKey: null)
        };

        var context = new StepExecutionContext
        {
            StepResultId = StepResultId,
            Execution = new WorkflowExecution { Id = ExecutionId, ProjectId = ProjectId, Status = ExecutionStatus.Running },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = planJson,
            CurrentStepIndex = history.Count,
            IterationCount = 0,
            CorrelationId = "test-correlation",
            StepOutputHistory = history,
            CancellationToken = CancellationToken.None
        };

        return (executor, context);
    }
}
