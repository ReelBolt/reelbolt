using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// T-006: generated text is screened immediately before it reaches drawtext/TTS. A high-confidence
/// Block fails the step with <c>CONTENT_BLOCKED</c> (UserInput) and nothing is rendered or spoken;
/// the mid band and an unavailable call both fail open on this surface, and an Allow changes nothing.
/// </summary>
public sealed class GeneratedTextModerationTests
{
    private const string BlockMessage = "This text cannot be rendered.";

    private sealed class FakeGate : IContentModerationGate
    {
        public FakeGate(ModerationAction action, bool available = true)
        {
            Decision = new ModerationDecision(
                action, available, action == ModerationAction.Review,
                action == ModerationAction.Block ? "violence" : null,
                action == ModerationAction.Block ? BlockMessage : string.Empty,
                "hash");
        }

        public ModerationDecision Decision { get; }
        public List<ModerationInput> Inputs { get; } = [];
        public List<ModerationAttribution> Attributions { get; } = [];

        public Task<ModerationDecision> ModerateAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
        {
            Inputs.Add(input);
            Attributions.Add(attribution);
            return Task.FromResult(Decision);
        }

        public Task<bool> ScreenGenerationPromptAsync(
            string text, IReadOnlyList<ModerationImageInput> images, ModerationAttribution attribution, CancellationToken ct)
            => Task.FromResult(Decision.Action != ModerationAction.Block);

        public Task<bool> ScreenGeneratedTextAsync(
            string text, string site, ModerationAttribution attribution, CancellationToken ct)
            => Task.FromResult(Decision.Action != ModerationAction.Block);

        public Task<bool> ScreenPublishAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
            => Task.FromResult(Decision.Available && Decision.Action != ModerationAction.Block);
    }

    // =====================================================================
    // VideoCompile overlays
    // =====================================================================

    [Fact]
    public async Task A_blocked_overlay_fails_the_compile_before_any_render_and_carries_the_plain_message()
    {
        var rig = new CompileRig();
        var gate = new FakeGate(ModerationAction.Block);

        StepExecutionResult result = await rig.RunOverlayCompileAsync(gate);

        result.Status.Should().Be(StepStatus.Failed);
        FailureClassifier.CodeFromOutput(result.Output).Should().Be("CONTENT_BLOCKED");
        result.ErrorDetails.Should().Be(BlockMessage);
        result.FailureClass.Should().Be(FailureClass.UserInput);

        rig.NonProbeFfmpegCalls.Should().Be(0, "a blocked overlay never reaches ffmpeg");
        gate.Inputs.Should().ContainSingle();
        gate.Inputs[0].Text.Should().Contain("Buy now").And.Contain("limited offer");
        gate.Attributions[0].Surface.Should().Be(ModerationSurface.GeneratedText);
        gate.Attributions[0].Kind.Should().Be(ModerationContentKind.Text);
        gate.Attributions[0].ExecutionId.Should().Be(rig.ExecutionId);
        gate.Attributions[0].OrganizationId.Should().NotBe(Guid.Empty);
    }

    [Theory]
    [InlineData(ModerationAction.Allow, true)]
    [InlineData(ModerationAction.Review, true)]
    [InlineData(ModerationAction.Unavailable, false)]
    public async Task Allow_review_and_unavailable_overlays_all_proceed(ModerationAction action, bool available)
    {
        var rig = new CompileRig();
        var gate = new FakeGate(action, available);

        StepExecutionResult result = await rig.RunOverlayCompileAsync(gate);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        FailureClassifier.CodeFromOutput(result.Output).Should().NotBe("CONTENT_BLOCKED");
        gate.Inputs.Should().ContainSingle();
    }

    private sealed class CompileRig
    {
        private static readonly Guid ProjectId = Guid.NewGuid();
        private static readonly string AnalysisKey =
            string.Join('/', new[] { "projects", "p", "agentFiles", "video-analysis", "e", "step-1-analysis.json" });
        private static readonly string SourceVideoKey =
            string.Join('/', new[] { "projects", "p", "outputFiles", "e", "render.mp4" });

        public Guid ExecutionId { get; } = Guid.NewGuid();
        public int NonProbeFfmpegCalls { get; private set; }

        public async Task<StepExecutionResult> RunOverlayCompileAsync(IContentModerationGate gate)
        {
            VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
            VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();

            var toolRunner = new Mock<IVideoToolRunner>();
            toolRunner
                .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VideoToolResult(0, "... drawtext ...", string.Empty, false));
            toolRunner
                .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Callback(() => NonProbeFfmpegCalls++)
                .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
            toolRunner
                .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
                .Callback(() => NonProbeFfmpegCalls++)
                .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));

            var mediaProbe = new Mock<IMediaProbe>();
            mediaProbe
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", "aac", 48000));

            var workspace = new Mock<IProjectFileWorkspace>();
            workspace
                .Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-4-edl.json");
            workspace
                .Setup(w => w.UploadBinaryFileAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(),
                    It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid projectId, string _, string fileName, string mime, SummaryStatus _, FileIndexingStatus _,
                    CancellationToken _, string category, string? _) =>
                    new ProjectWorkspaceFile(
                        Guid.NewGuid(), projectId, fileName, null, category,
                        $"projects/{projectId}/{category}/{Guid.NewGuid():D}.mp4", mime, 1024, DateTime.UtcNow, null));
            workspace.SetupRegisteredRenderPublish();

            var placement = new VideoAnalysisPlacement(
                "p0", "s0", "LowerThird", new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15), "ShotMiddle", 4.0, 6.0, 0.8, "Light");
            var artifact = new VideoAnalysisArtifact(
                Version: 1,
                Media: new VideoAnalysisMedia(10.0, 30, 1, 1920, 1080),
                Shots: [new VideoAnalysisShot("s0", 0.0, 10.0)],
                SilenceSpans: [],
                Segments: [],
                Words: [],
                OfferedIds: ["s0"],
                Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
                Placements: [placement],
                OfferedPlacementIds: ["p0"]);
            string artifactJson = JsonSerializer.Serialize(artifact, JsonOptions());

            workspace
                .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllText(dest, artifactJson); return Task.CompletedTask; });
            workspace
                .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, [0x00, 0x01, 0x02]); return Task.CompletedTask; });
            workspace
                .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ProjectWorkspaceFile>());

            string decisionJson = JsonSerializer.Serialize(new
            {
                keep = new[] { new { fromId = "s0", toId = "s0", reason = "keep" } },
                editRationale = "test",
                suggestedTitle = "Test"
            });
            string graphicsPlanJson = JsonSerializer.Serialize(new
            {
                overlays = new[]
                {
                    new
                    {
                        placementId = "p0", kind = "LowerThird", text = "Buy now",
                        subtext = "limited offer", duration = "Medium", emphasis = "Normal", reason = "test"
                    }
                },
                planRationale = "test"
            });

            var config = new VideoCompileStepConfig(
                Version: 1,
                Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
                AnalysisStepOrder: 1,
                EnableGraphics: true,
                GraphicsPlan: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 3));

            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 4,
                StepType = StepType.VideoCompile,
                VideoCompileConfigJson = JsonSerializer.Serialize(config, JsonOptions())
            };
            var analyzeStep = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 1,
                StepType = StepType.VideoAnalyze,
                VideoAnalyzeConfigJson = JsonSerializer.Serialize(
                    new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput)), JsonOptions())
            };

            List<StepOutputHistoryEntry> history =
            [
                new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
                new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
                new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null),
                new StepOutputHistoryEntry(3, "MotionGraphicsPlanner", graphicsPlanJson, OutputStorageKey: null, ArtifactStorageKey: null)
            ];

            var options = Options.Create(new VideoEditingOptions
            {
                ScratchPath = Path.Combine(Path.GetTempPath(), "moderation-compile-tests", Guid.NewGuid().ToString("N")),
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 30,
                CompileTimeoutSeconds = 30
            });

            var executor = new VideoCompileStepExecutor(
                toolRunner.Object, mediaProbe.Object, workspace.Object, new Mock<IServiceScopeFactory>().Object,
                options, NullLogger<VideoCompileStepExecutor>.Instance, moderation: gate);

            var execution = new WorkflowExecution
            {
                Id = ExecutionId,
                ProjectId = ProjectId,
                OrganizationId = Guid.NewGuid(),
                InitiatedByUserId = Guid.NewGuid(),
                Status = ExecutionStatus.Running
            };

            return await executor.ExecuteAsync(new StepExecutionContext
            {
                StepResultId = Guid.NewGuid(),
                Execution = execution,
                Step = step,
                AllSteps = [analyzeStep, step],
                AccumulatedOutput = decisionJson,
                StepOutputHistory = history,
                CurrentStepIndex = history.Count - 1,
                IterationCount = 0,
                CorrelationId = "moderation-test",
                CancellationToken = CancellationToken.None
            });
        }
    }

    // =====================================================================
    // ThumbnailGenerate overlay
    // =====================================================================

    [Fact]
    public async Task A_blocked_thumbnail_overlay_fails_before_the_overlay_ffmpeg_call()
    {
        var gate = new FakeGate(ModerationAction.Block);
        var runner = new Mock<IVideoToolRunner>(MockBehavior.Strict);
        var workspace = new Mock<IProjectFileWorkspace>();
        var config = new ThumbnailGenerateStepConfig(
            Version: 1, AnalysisStepOrder: 1,
            OverlayText: "Launch day", OverlayPosition: ThumbnailOverlayPosition.LowerThird);

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.ThumbnailGenerate,
            ThumbnailGenerateConfigJson = JsonSerializer.Serialize(config, JsonOptions())
        };

        var executor = new ThumbnailGenerateStepExecutor(
            runner.Object, workspace.Object, new Mock<IServiceScopeFactory>().Object,
            Options.Create(new VideoEditingOptions()), NullLogger<ThumbnailGenerateStepExecutor>.Instance,
            moderation: gate);

        StepExecutionResult result = await executor.ExecuteAsync(new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "moderation-test",
            CancellationToken = CancellationToken.None
        });

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("CONTENT_BLOCKED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        result.ErrorDetails.Should().Contain(BlockMessage);
        runner.Verify(r => r.RunFfmpegAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);

        gate.Inputs.Should().ContainSingle().Which.Text.Should().Contain("Launch day");
        gate.Attributions[0].Surface.Should().Be(ModerationSurface.GeneratedText);
    }

    // =====================================================================
    // Timeline render title / caption text
    // =====================================================================

    [Fact]
    public async Task Timeline_render_blocks_title_and_caption_text_before_the_region_pass()
    {
        var projectId = Guid.NewGuid();
        var asset = new AssetRef(AssetRefKind.ProjectFile, Guid.NewGuid(), null);
        var settings = new EditTimelineSettings(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);
        var clip = new MediaClip(Guid.NewGuid(), "clip", 0, 30, asset);
        var title = new TitleClip(Guid.NewGuid(), "Title", 0, 30, "A blocked title");
        var caption = new Caption(Guid.NewGuid(), "Caption", 30, 30, "A blocked caption");
        var track = new Track(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null,
            [clip, title, caption]);
        var document = new EditTimeline(Guid.NewGuid(), projectId, "doc", settings, [track], []);

        var gate = new FakeGate(ModerationAction.Block);
        var runner = new Mock<IVideoToolRunner>(MockBehavior.Strict);
        string scratch = Path.Combine(Path.GetTempPath(), "moderation-timeline-" + Guid.NewGuid().ToString("N"));
        var attribution = new ModerationAttribution(
            Guid.NewGuid(), projectId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ModerationSurface.GeneratedText, ModerationContentKind.Text);
        var env = new TimelineRenderEnvironment(
            runner.Object, scratch, "/fonts/missing.ttf", TimeSpan.FromMinutes(1), 1,
            NullRegionCache.Instance, projectId, Moderation: attribution);

        Func<Task> act = () => new TimelineRenderer(NullLogger.Instance, gate).RenderAsync(
            document, new TimelineRenderStepConfig(document.Id), new Dictionary<AssetRef, LocalAsset>(),
            Path.Combine(scratch, "out.mp4"), env, CancellationToken.None);

        await act.Should().ThrowAsync<TimelineContentBlockedException>().WithMessage(BlockMessage);
        runner.Verify(r => r.RunFfmpegAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        gate.Inputs.Should().ContainSingle();
        gate.Inputs[0].Text.Should().Contain("A blocked title").And.Contain("A blocked caption");
        gate.Attributions[0].Surface.Should().Be(ModerationSurface.GeneratedText);
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
