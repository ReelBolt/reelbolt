using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// D10a — "Split VideoAnalyze into extraction and inference (in place)".
///
/// <para>
/// The split is only worth anything if the step's OUTPUT does not move, so that is what this file
/// pins: the same fixtures are run twice — once through <c>ExecuteAsync</c> (extraction and
/// inference back to back, the pre-split call shape) and once through <c>ExtractAsync</c> followed
/// by <c>InferAsync</c> (the split, driven explicitly) — and the published envelope and the
/// persisted artifact must be identical.
/// </para>
/// <para>
/// The second half of the contract is the boundary itself: <c>ExtractAsync</c> must be
/// runner-eligible, which for v1 means it must reach NO inference provider at all, and everything
/// the inference phase needs from it must be in the returned <see cref="VideoAnalysisExtract"/>
/// (the merged analysis, each source's facts, and the caption keyframes).
/// </para>
/// </remarks>
public class VideoAnalyzeExtractionInferenceSplitTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";

    // =======================================================================
    // Parity: the split reproduces the monolithic step byte for byte
    // =======================================================================

    [Fact]
    public async Task The_split_phases_reproduce_the_monolithic_step_output_and_artifact()
    {
        var artifacts = new List<string>();
        Fixture fixture = CreateFixture(artifacts);

        // ---- (1) The step as it has always been called: extraction + inference in one go. ----
        StepExecutionResult monolithic = await fixture.Executor.ExecuteAsync(fixture.Context);

        monolithic.Status.Should().Be(StepStatus.Completed, because: monolithic.ErrorDetails ?? monolithic.Output);

        // ---- (2) The same fixtures through the two phases, driven explicitly. ----
        StepExecutionResult split = await RunSplitPhasesAsync(fixture);

        split.Status.Should().Be(StepStatus.Completed, because: split.ErrorDetails ?? split.Output);
        split.Output.Should().Be(
            monolithic.Output,
            "the split must not change the step's observable output — the {view, meta} envelope is what a user's workflow consumes");
        split.ArtifactStorageKey.Should().Be(monolithic.ArtifactStorageKey);

        artifacts.Should().HaveCount(2, "each run publishes exactly one analysis artifact");
        NormalizeArtifact(artifacts[1]).Should().Be(
            NormalizeArtifact(artifacts[0]),
            "the persisted VideoAnalysisArtifact must be identical either side of the split");
        artifacts[0].Should().Contain("\"offeredIds\"", because: "the captured artifact is the real thing, not an empty envelope");
    }

    [Fact]
    public async Task Extraction_calls_no_inference_provider_and_hands_over_everything_inference_needs()
    {
        Fixture fixture = CreateFixture(new List<string>());

        VideoAnalysisExtractOutcome outcome = await RunExtractionAsync(fixture);

        outcome.Extract.Should().NotBeNull(outcome.ErrorMessage);
        VideoAnalysisExtract extract = outcome.Extract!;

        // ---- The boundary: nothing that leaves the host ran on the extraction side. ----
        fixture.ProviderResolver.Verify(r => r.ResolveVisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.ProviderResolver.Verify(r => r.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.TranscriptionFactory.Verify(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>()), Times.Never);
        fixture.ShotCaptioner.Invocations.Should().BeEmpty("captioning is the inference phase's provider call");
        fixture.ObjectLocator.Invocations.Should().BeEmpty();

        // ---- What crosses the boundary: per-source facts, the merged analysis, the keyframes. ----
        extract.Sources.Should().ContainSingle();
        VideoAnalysisExtractSource source = extract.Sources[0];
        source.Shots.Should().HaveCount(2, "extraction detected both shots");
        source.Shots.Select(s => s.Id).Should().Equal(
            new[] { "s0", "s1" }, "a source carries its shots in their LOCAL id space");

        extract.Deterministic.Shots.Should().HaveCount(2);
        extract.Deterministic.Shots.Should().OnlyContain(s => s.SourceIndex == 0);
        extract.Deterministic.Shots.Select(s => s.Id).Should().Equal(new[] { "s0", "s1" });
        extract.Deterministic.SourceInfos.Should().ContainSingle();
        extract.Deterministic.Provenance.VisualAnalysisApplied.Should().BeTrue();
        extract.Deterministic.Provenance.TranscriptionApplied.Should().BeFalse(
            "extraction cannot have transcribed — transcription is the inference phase's call");
        extract.Deterministic.Provenance.TranscriptionMode.Should().Be(VideoTranscriptionMode.Required);

        // The caption keyframes are extraction's product too: one per shot the deterministic
        // selector picked, named session-relatively rather than by an absolute local path.
        extract.Keyframes.Should().HaveCount(2, "one keyframe per selected shot");
        extract.Keyframes[0].ShotId.Should().Be("s0");
        extract.Keyframes[0].VirtualPath.Should().Be("keyframe-s0.jpg");
        Path.IsPathRooted(extract.Keyframes[0].VirtualPath).Should().BeFalse(
            "everything the inference phase names must be session-relative, or a runner could never serve it");

        // The WAV is the one extraction product the inference phase cannot re-derive, so it is
        // extracted once and published under this step's own artifact prefix.
        source.AudioVirtualPath.Should().Be("audio-full.wav");
        source.AudioBytes.Should().Be(100, "the fake audio extractor writes a 100-byte WAV");
        source.AudioStorageKey.Should().NotBeNull();
        source.HasAudioStream.Should().BeTrue();
    }

    // =======================================================================
    // Driving the two phases the way ExecuteAsync does
    // =======================================================================

    private static async Task<VideoAnalysisExtractOutcome> RunExtractionAsync(Fixture fixture)
    {
        using VideoScratchSpace scratch = VideoScratchSpace.Create(
            fixture.Options, fixture.Context.Execution.Id, fixture.Context.Step.Id, NullLogger<VideoAnalyzeStepExecutor>.Instance);

        // The scratch directory is still created because the workspace wraps it (a local workspace
        // IS this directory) - the extraction phase itself no longer takes it, which is exactly what
        // this WP changed.
        return await fixture.Executor.ExtractAsync(
            fixture.Context,
            CreateWorkspace(fixture, scratch),
            fixture.Config,
            fixture.Sources,
            CreateProgressPlan(fixture.Sources.Count),
            CreateSharpnessBudget(fixture.Config),
            GridWidth(fixture.Config),
            GridHeight(fixture.Config),
            fixture.Context.CancellationToken);
    }

    private static async Task<StepExecutionResult> RunSplitPhasesAsync(Fixture fixture)
    {
        using VideoScratchSpace scratch = VideoScratchSpace.Create(
            fixture.Options, fixture.Context.Execution.Id, fixture.Context.Step.Id, NullLogger<VideoAnalyzeStepExecutor>.Instance);

        IMediaWorkspace workspace = CreateWorkspace(fixture, scratch);

        VideoAnalysisExtractOutcome outcome = await fixture.Executor.ExtractAsync(
            fixture.Context,
            workspace,
            fixture.Config,
            fixture.Sources,
            CreateProgressPlan(fixture.Sources.Count),
            CreateSharpnessBudget(fixture.Config),
            GridWidth(fixture.Config),
            GridHeight(fixture.Config),
            fixture.Context.CancellationToken);

        outcome.Extract.Should().NotBeNull(outcome.ErrorMessage);

        return await fixture.Executor.InferAsync(
            fixture.Context,
            workspace,
            scratch,
            fixture.Config,
            fixture.Sources,
            outcome.Extract!,
            CreateProgressPlan(fixture.Sources.Count),
            GridWidth(fixture.Config),
            GridHeight(fixture.Config),
            System.Diagnostics.Stopwatch.StartNew(),
            fixture.Context.CancellationToken);
    }

    /// <summary>
    /// A workspace rooted where <c>ExecuteAsync</c> roots its own, so both runs stage into the same
    /// directory and every path either phase computes is the one it always was.
    /// </summary>
    private static IMediaWorkspace CreateWorkspace(Fixture fixture, VideoScratchSpace scratch) =>
        LocalMediaWorkspace.Create(
            scratch, new Mock<IVideoToolRunner>().Object, fixture.Workspace.Object, ProjectId,
            NullLogger<VideoAnalyzeStepExecutor>.Instance);

    /// <summary>
    /// Every stage enabled: this run's percentages are not the subject of the test (the output does
    /// not depend on them), so the plan is built from the enum rather than mirroring ExecuteAsync's
    /// conditional list.
    /// </summary>
    private static VideoAnalyzeProgressPlan CreateProgressPlan(int sourceCount) =>
        new(Enum.GetValues<VideoAnalyzeProgressPlan.Stage>(), sourceCount);

    private static VideoAnalyzeStepExecutor.StepBudget CreateSharpnessBudget(VideoAnalyzeStepConfig config) =>
        new() { Remaining = Math.Max(0, config.MaxSharpnessShots) };

    private static int GridWidth(VideoAnalyzeStepConfig config) => Math.Clamp(config.VisualGridWidth, 8, 256);

    private static int GridHeight(VideoAnalyzeStepConfig config) => Math.Clamp(config.VisualGridHeight, 8, 256);

    /// <summary>
    /// Drops the one genuinely non-deterministic artifact field — the provenance's analysis
    /// timestamp — so two runs of the same fixture can be compared. Everything else, including
    /// every id, every numeric descriptor and the whole provenance shape, must match exactly.
    /// </summary>
    private static string NormalizeArtifact(string artifactJson)
    {
        JsonNode node = JsonNode.Parse(artifactJson)!;
        if (node["provenance"] is JsonObject provenance)
            provenance["analyzedAt"] = "normalized";

        return node.ToJsonString();
    }

    // =======================================================================
    // Fixture
    // =======================================================================

    private sealed record Fixture(
        VideoAnalyzeStepExecutor Executor,
        StepExecutionContext Context,
        VideoAnalyzeStepConfig Config,
        IReadOnlyList<VideoSourceRef> Sources,
        VideoEditingOptions Options,
        Mock<IProjectFileWorkspace> Workspace,
        Mock<IInferenceProviderResolver> ProviderResolver,
        Mock<ITranscriptionClientFactory> TranscriptionFactory,
        Mock<IShotCaptioner> ShotCaptioner,
        Mock<IObjectLocator> ObjectLocator);

    /// <summary>
    /// One source, shots, silence, visuals, a transcript and vision captioning — i.e. every stage
    /// that made the pre-split executor one long method, so a parity assertion here covers the
    /// whole step rather than its easy half.
    /// </summary>
    private static Fixture CreateFixture(List<string> capturedArtifacts)
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        var probe = new Mock<IMediaProbe>();
        var silence = new Mock<ISilenceDetector>();
        var shotDetector = new Mock<IShotDetector>();
        var transcriptionFactory = new Mock<ITranscriptionClientFactory>();
        var providerResolver = new Mock<IInferenceProviderResolver>();
        var shotCaptioner = new Mock<IShotCaptioner>();
        var objectLocator = new Mock<IObjectLocator>();

        VideoAnalyzeStepConfig config = new VideoAnalyzeStepConfig(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput)) with
        {
            DetectSilence = true,
            DetectShots = true,
            Transcription = VideoTranscriptionMode.Required,
            AnalyzeAudioLevels = true,
            AnalyzeVisuals = true,
            DetectNearDuplicates = true,
            EmitOverlayPlacements = true,
            Vision = VideoVisionMode.Required,
            MaxCaptionedShots = 10,
            MinCaptionShotSeconds = 0.0,
            OfferMusicTracks = true,
            OfferSfxClips = true
        };

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter() }
            })
        };

        string scratchRoot = Path.Combine(Path.GetTempPath(), "video-analyze-split-tests", Guid.NewGuid().ToString("N"));
        var options = new VideoEditingOptions
        {
            ScratchPath = scratchRoot,
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 30,
            CompileTimeoutSeconds = 30
        };

        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null)],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };

        // ---- Storage: the source download writes a real file (the executor sizes it), every
        // upload succeeds, and every analysis artifact is captured for comparison. ----
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(Guid.NewGuid(), ProjectId, "bed.mp3", null, "userFiles", "projects/p/userFiles/bed.mp3", "audio/mpeg", 4096, DateTime.UtcNow, null)
            });
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, path, fileName, _, _, _) =>
            {
                if (fileName.EndsWith("-analysis.json", StringComparison.Ordinal))
                    capturedArtifacts.Add(File.ReadAllText(path));
            })
            .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-1-analysis.json");

        // ---- Extraction's own stages. ----
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(4, 30, 1, 32, 18, "h264", "aac", 48000));
        silence.Setup(s => s.DetectAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { (3.0, 3.5) });
        shotDetector
            .Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([(0.0, 1.0), (1.0, 2.0)]);

        var audioExtractor = new Mock<IAudioExtractor>();
        audioExtractor
            .Setup(a => a.ExtractWavAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, CancellationToken>(async (ws, _, outPath, ct) =>
            {
                await ws.WriteBytesAsync(outPath, new byte[100], ct);
            });

        var frameGridSampler = new Mock<IFrameGridSampler>();
        frameGridSampler
            .Setup(g => g.SampleAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildGrid(32, 18, frameCount: 8, fps: 2.0));

        // ---- Inference's provider calls. ----
        var visionProvider = new ResolvedInferenceProvider(
            Guid.NewGuid(), "vision-test", InferenceProviderKind.OpenAICompatible, "http://localhost:9999", "vision-1", "", 30);
        providerResolver
            .Setup(r => r.ResolveVisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(visionProvider);
        providerResolver
            .Setup(r => r.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedTranscriptionProvider(
                ProviderId: Guid.NewGuid(), Name: "whisper-local", Kind: InferenceProviderKind.OpenAICompatible,
                Endpoint: "http://localhost:9999", ModelName: "whisper-1", ApiKey: "", TimeoutSeconds: 30));

        shotCaptioner
            .Setup(c => c.CaptionAsync(It.IsAny<ResolvedInferenceProvider>(), It.IsAny<ShotCaptionRequest>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedInferenceProvider _, ShotCaptionRequest r, int _, CancellationToken _) => new VideoShotCaption(
                r.ShotId, "a caption", [], "action", "setting", "mood", "Medium", "Eye level", [], [],
                "Unknown", "flat", "neutral", "centered", []));

        var transcriptionClient = new Mock<ITranscriptionClient>();
        transcriptionClient
            .Setup(c => c.TranscribeAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptResult(
                "hello world",
                [new TranscriptSegment("hello world", 0.5, 1.5)],
                [new TranscriptWord("hello", 0.5, 1.0)],
                "en"));
        transcriptionFactory
            .Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>()))
            .Returns(transcriptionClient.Object);

        var executor = new VideoAnalyzeStepExecutor(
            probe.Object,
            silence.Object,
            shotDetector.Object,
            audioExtractor.Object,
            frameGridSampler.Object,
            new Mock<IKeyframeExtractor>().Object,
            new Mock<ISharpnessSampler>().Object,
            shotCaptioner.Object,
            workspace.Object,
            transcriptionFactory.Object,
            providerResolver.Object,
            Options.Create(options),
            NullLogger<VideoAnalyzeStepExecutor>.Instance,
            objectLocator: objectLocator.Object);

        IReadOnlyList<VideoSourceRef> sources = [config.Source];

        return new Fixture(
            executor, context, config, sources, options, workspace, providerResolver, transcriptionFactory,
            shotCaptioner, objectLocator);
    }

    /// <summary>A synthetic RGB grid with genuinely varying content, so the visual stage produces real descriptors.</summary>
    private static FrameGridResult BuildGrid(int gridWidth, int gridHeight, int frameCount, double fps)
    {
        int frameSize = gridWidth * gridHeight * 3;
        var pixels = new byte[frameCount * frameSize];
        var rnd = new Random(42);

        for (int f = 0; f < frameCount; f++)
        {
            int brightRow = f % gridHeight;
            for (int y = 0; y < gridHeight; y++)
            {
                for (int x = 0; x < gridWidth; x++)
                {
                    int idx = (f * frameSize) + (y * gridWidth + x) * 3;
                    byte baseVal = (byte)(40 + rnd.Next(0, 10));
                    byte val = y == brightRow ? (byte)220 : baseVal;
                    pixels[idx] = val;
                    pixels[idx + 1] = val;
                    pixels[idx + 2] = val;
                }
            }
        }

        return new FrameGridResult(pixels, gridWidth, gridHeight, fps, frameCount);
    }
}