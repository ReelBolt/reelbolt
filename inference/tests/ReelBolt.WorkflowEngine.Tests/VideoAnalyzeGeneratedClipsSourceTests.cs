using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Issue #93 phase 4 (dailies): a <see cref="VideoSourceKind.GeneratedClips"/> source expands into
/// one analyzed source per DELIVERED clip of the named <c>VideoGenerate</c> step's manifest, each
/// tagged with the manifest's own clip id in <c>view.sources[].clipId</c> — the ids a dailies room
/// chooses between. Everything else about the analysis is the multi-source path unchanged.
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VideoAnalyzeGeneratedClipsSourceTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private const int GenerateStepOrder = 4;
    private const string ManifestKey = "projects/p/agentFiles/video-analysis/e/step-4-generated-clips.json";
    private const string KeyA = "projects/p/agentFiles/generated/e/step-4-v1t1.mp4";
    private const string KeyB = "projects/p/agentFiles/generated/e/step-4-v2.mp4";

    private static string Manifest(params (string ClipId, string Status, string? Key)[] clips) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            clips = clips.Select(c => new { clipId = c.ClipId, status = c.Status, storageKey = c.Key, purpose = "Cutaway" })
        });

    [Fact]
    public async Task A_GeneratedClips_source_expands_to_one_tagged_source_per_delivered_clip()
    {
        // Three manifest rows: a delivered take, a failed take, a delivered single-take shot.
        string manifest = Manifest(("v1t1", "succeeded", KeyA), ("v1t2", "failed", null), ("v2", "succeeded", KeyB));
        var (workspace, probe, silence, shots) = Fixture(manifest);

        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.GeneratedClips, StepOrder: GenerateStepOrder),
            DetectSilence: false,
            Transcription: VideoTranscriptionMode.Off);

        string? artifactJson = null;
        workspace
            .Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) => artifactJson = File.ReadAllText(path))
            .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-5-analysis.json");

        StepExecutionResult result = await Executor(workspace, probe, silence, shots).ExecuteAsync(Context(config));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails ?? result.Output);

        using JsonDocument artifact = JsonDocument.Parse(artifactJson!);
        JsonElement sources = artifact.RootElement.GetProperty("sources");
        sources.GetArrayLength().Should().Be(2, "the failed take delivered nothing and is not a source");
        sources[0].GetProperty("storageKey").GetString().Should().Be(KeyA);
        sources[0].GetProperty("clipId").GetString().Should().Be("v1t1");
        sources[1].GetProperty("storageKey").GetString().Should().Be(KeyB);
        sources[1].GetProperty("clipId").GetString().Should().Be("v2");

        // The bounded view offers the take ids a dailies room may choose by.
        using JsonDocument output = JsonDocument.Parse(result.Output);
        JsonElement viewSources = output.RootElement.GetProperty("view").GetProperty("sources");
        viewSources.EnumerateArray().Select(s => s.GetProperty("clipId").GetString()).Should().Equal("v1t1", "v2");
        viewSources[0].GetProperty("src").GetInt32().Should().Be(0);
        viewSources[1].GetProperty("src").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_single_delivered_clip_still_emits_view_sources_with_its_clipId()
    {
        // A one-source view normally has no `sources` key at all; a generated take's clipId is the
        // one thing a dailies room needs, so the list is emitted for it even alone.
        var (workspace, probe, silence, shots) = Fixture(Manifest(("v1", "succeeded", KeyA)));
        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.GeneratedClips, StepOrder: GenerateStepOrder),
            DetectSilence: false,
            Transcription: VideoTranscriptionMode.Off);

        StepExecutionResult result = await Executor(workspace, probe, silence, shots).ExecuteAsync(Context(config));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails ?? result.Output);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        JsonElement viewSources = output.RootElement.GetProperty("view").GetProperty("sources");
        viewSources.GetArrayLength().Should().Be(1);
        viewSources[0].GetProperty("clipId").GetString().Should().Be("v1");
    }

    [Fact]
    public async Task A_manifest_with_no_delivered_clip_fails_SOURCE_UNRESOLVED()
    {
        var (workspace, probe, silence, shots) = Fixture(Manifest(("v1", "failed", null)));
        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.GeneratedClips, StepOrder: GenerateStepOrder),
            DetectSilence: false,
            Transcription: VideoTranscriptionMode.Off);

        StepExecutionResult result = await Executor(workspace, probe, silence, shots).ExecuteAsync(Context(config));

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("SOURCE_UNRESOLVED");
    }

    [Fact]
    public async Task A_GeneratedClips_source_naming_a_step_without_a_manifest_fails_SOURCE_UNRESOLVED()
    {
        var (workspace, probe, silence, shots) = Fixture(Manifest(("v1", "succeeded", KeyA)));
        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.GeneratedClips, StepOrder: 9),
            DetectSilence: false,
            Transcription: VideoTranscriptionMode.Off);

        StepExecutionResult result = await Executor(workspace, probe, silence, shots).ExecuteAsync(Context(config));

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("SOURCE_UNRESOLVED");
    }

    [Fact]
    public async Task Expansion_with_no_local_scratch_downloads_the_manifest_to_a_temp_file_and_removes_it()
    {
        // A remote (CloudPool/OrgRunner) workspace has no local scratch: scratch is null.
        string manifest = Manifest(("v1t1", "succeeded", KeyA), ("v1t2", "failed", null), ("v2", "succeeded", KeyB));
        var (workspace, probe, silence, shots) = Fixture(manifest);
        string? downloadedTo = null;
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), ManifestKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) =>
            {
                downloadedTo = dest;
                File.WriteAllText(dest, manifest);
                return Task.CompletedTask;
            });

        (IReadOnlyList<string?> clipIds, string? error) = await ExpandWithNullScratch(
            Executor(workspace, probe, silence, shots), Context(GeneratedClipsConfig()));

        error.Should().BeNull();
        clipIds.Should().Equal("v1t1", "v2");
        downloadedTo.Should().NotBeNull();
        File.Exists(downloadedTo).Should().BeFalse("the temp manifest is deleted after it is parsed");
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        Path.GetFullPath(downloadedTo!).Should().StartWith(tempRoot);
        downloadedTo.Should().NotContain("video-generated-clips-source-tests", "it is not inside any scratch dir");
    }

    [Fact]
    public async Task Expansion_with_no_local_scratch_returns_Fail_and_removes_the_temp_file_when_the_download_throws()
    {
        var (workspace, probe, silence, shots) = Fixture(Manifest(("v1", "succeeded", KeyA)));
        string? downloadedTo = null;
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), ManifestKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) =>
            {
                downloadedTo = dest;
                File.WriteAllText(dest, "partial");
                throw new IOException("boom");
            });

        (IReadOnlyList<string?> clipIds, string? error) = await ExpandWithNullScratch(
            Executor(workspace, probe, silence, shots), Context(GeneratedClipsConfig()));

        error.Should().Contain("could not be downloaded/parsed").And.Contain("boom");
        clipIds.Should().BeEmpty();
        downloadedTo.Should().NotBeNull();
        File.Exists(downloadedTo).Should().BeFalse("the temp file is removed even when the download fails");
    }

    private static VideoAnalyzeStepConfig GeneratedClipsConfig() => new(
        Version: 1,
        Source: new VideoSourceRef(VideoSourceKind.GeneratedClips, StepOrder: GenerateStepOrder),
        DetectSilence: false,
        Transcription: VideoTranscriptionMode.Off);

    // ExpandGeneratedClipSourcesAsync and its result record are private; reflect to call it with scratch == null.
    private static async Task<(IReadOnlyList<string?> ClipIds, string? Error)> ExpandWithNullScratch(
        VideoAnalyzeStepExecutor executor, StepExecutionContext context)
    {
        var method = typeof(VideoAnalyzeStepExecutor).GetMethod(
            "ExpandGeneratedClipSourcesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(executor, [context, null, new List<VideoSourceRef> { GeneratedClipsConfig().Source }])!;
        await task.ConfigureAwait(false);
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var type = result.GetType();
        var clipIds = (IReadOnlyList<string?>)type.GetProperty("ClipIds")!.GetValue(result)!;
        var error = (string?)type.GetProperty("Error")!.GetValue(result);
        return (clipIds, error);
    }

    // ---- harness ----

    private static (Mock<IProjectFileWorkspace> Workspace, Mock<IMediaProbe> Probe, Mock<ISilenceDetector> Silence, Mock<IShotDetector> Shots)
        Fixture(string manifestJson)
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>());
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), ManifestKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllText(dest, manifestJson); return Task.CompletedTask; });
        foreach (string key in new[] { KeyA, KeyB })
        {
            workspace
                .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), key, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, new byte[10]); return Task.CompletedTask; });
        }

        workspace
            .Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-5-analysis.json");

        // The executor runs every media tool through the media workspace (D11), so the workspace
        // overloads are what it calls; the path overloads are mocked too for the few helpers that
        // still take a host path.
        var probe = new Mock<IMediaProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(6, 30, 1, 1280, 720, "h264", null, null));
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(6, 30, 1, 1280, 720, "h264", null, null));

        var silence = new Mock<ISilenceDetector>();
        silence.Setup(s => s.DetectAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(double, double)>());
        silence.Setup(s => s.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(double, double)>());

        var shots = new Mock<IShotDetector>();
        shots.Setup(s => s.DetectShotsAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(double, double)> { (0.0, 6.0) });
        shots.Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(double, double)> { (0.0, 6.0) });

        return (workspace, probe, silence, shots);
    }

    private static StepExecutionContext Context(VideoAnalyzeStepConfig config)
    {
        string configJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        });

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 5,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = configJson
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory =
            [
                new StepOutputHistoryEntry(GenerateStepOrder, "VideoGenerate", "{}", OutputStorageKey: KeyA, ArtifactStorageKey: ManifestKey)
            ],
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private static VideoAnalyzeStepExecutor Executor(
        Mock<IProjectFileWorkspace> workspace, Mock<IMediaProbe> probe, Mock<ISilenceDetector> silence, Mock<IShotDetector> shots)
    {
        var audioExtractor = new Mock<IAudioExtractor>();
        audioExtractor
            .Setup(a => a.ExtractWavAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((_, outPath, _) => { File.WriteAllBytes(outPath, new byte[100]); return Task.CompletedTask; });
        audioExtractor
            .Setup(a => a.ExtractWavAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, CancellationToken>((ws, _, outPath, ct) => ws.WriteBytesAsync(outPath, new byte[100], ct));

        string scratch = Path.Combine(Path.GetTempPath(), "video-generated-clips-source-tests", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = scratch,
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 30,
            CompileTimeoutSeconds = 30
        });

        return new VideoAnalyzeStepExecutor(
            probe.Object, silence.Object, shots.Object, audioExtractor.Object,
            new Mock<IFrameGridSampler>().Object, new Mock<IKeyframeExtractor>().Object, new Mock<ISharpnessSampler>().Object,
            new Mock<IShotCaptioner>().Object, workspace.Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IInferenceProviderResolver>().Object, options, NullLogger<VideoAnalyzeStepExecutor>.Instance);
    }
}
