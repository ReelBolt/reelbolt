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
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tests for the multi-source addition to the video-editing pipeline (docs/video-editing.md
/// "Multiple source clips"): analyzing several source clips into one merged
/// <see cref="VideoAnalysisArtifact"/> with a globally-unique id space, and compiling a cut list
/// whose Keep spans reference more than one distinct physical clip. Deliberately kept in its own
/// file rather than folded into <see cref="VideoAnalyzeStepExecutorTests"/>/
/// <see cref="VideoCompileStepExecutorTests"/> — those already cover the single-source pipeline
/// exhaustively; this file covers only what changes when more than one source is involved.
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VideoMultiSourceTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    // ---------------------------------------------------------------------
    // VideoAnalyzeStepExecutor: id uniqueness/namespacing across sources
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Two_sources_produce_globally_unique_contiguous_ids_tagged_with_their_own_SourceIndex()
    {
        Guid fileA = Guid.NewGuid();
        Guid fileB = Guid.NewGuid();
        const string keyA = "projects/p/files/clipA.mp4";
        const string keyB = "projects/p/files/clipB.mp4";

        // Source A: 3 shots. Source B: 2 shots. Shot ids must come out as s0,s1,s2 (source 0) then
        // s3,s4 (source 1) — one continuous counter across both clips, never restarting per clip.
        var shotsA = new[] { (0.0, 1.0), (1.0, 2.0), (2.0, 3.0) };
        var shotsB = new[] { (0.0, 1.0), (1.0, 2.0) };

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(fileA, ProjectId, "clipA.mp4", null, "uploads", keyA, "video/mp4", 1024, DateTime.UtcNow, null),
                new(fileB, ProjectId, "clipB.mp4", null, "uploads", keyB, "video/mp4", 1024, DateTime.UtcNow, null)
            });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), keyA, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, new byte[10]); return Task.CompletedTask; });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), keyB, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, new byte[10]); return Task.CompletedTask; });

        var probe = new Mock<IMediaProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => s.Contains("src0-")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(3, 30, 1, 1920, 1080, "h264", "aac", 48000));
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => s.Contains("src1-")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(2, 25, 1, 1280, 720, "h264", "aac", 48000));

        var silence = new Mock<ISilenceDetector>();
        silence.Setup(s => s.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(double, double)>());

        var shotDetector = new Mock<IShotDetector>();
        shotDetector
            .Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(p => p.Contains("src0-")), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(shotsA);
        shotDetector
            .Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(p => p.Contains("src1-")), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(shotsB);

        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA), // ignored — Sources wins
            DetectSilence: false,
            Transcription: VideoTranscriptionMode.Off,
            Sources:
            [
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileB)
            ]);

        StepExecutionContext context = CreateAnalyzeContext(config);
        VideoAnalyzeStepExecutor executor = CreateAnalyzeExecutor(workspace, probe, silence, shotDetector);

        string? capturedArtifactJson = null;
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) =>
                capturedArtifactJson = File.ReadAllText(path))
            .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-1-analysis.json");
        workspace.SetupRegisteredRenderPublish();

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedArtifactJson.Should().NotBeNull();

        using JsonDocument artifact = JsonDocument.Parse(capturedArtifactJson!);
        JsonElement shots = artifact.RootElement.GetProperty("shots");
        shots.GetArrayLength().Should().Be(5);

        List<string> ids = shots.EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToList();
        ids.Should().OnlyHaveUniqueItems("ids must be globally unique across every source in one artifact");
        ids.Should().BeEquivalentTo(["s0", "s1", "s2", "s3", "s4"], because: "ids are assigned contiguously across sources, never restarting per source");

        List<int> sourceIndices = shots.EnumerateArray().Select(s => s.GetProperty("sourceIndex").GetInt32()).ToList();
        sourceIndices.Should().BeEquivalentTo([0, 0, 0, 1, 1], options => options.WithStrictOrdering());

        JsonElement sources = artifact.RootElement.GetProperty("sources");
        sources.GetArrayLength().Should().Be(2);
        sources[0].GetProperty("storageKey").GetString().Should().Be(keyA);
        sources[1].GetProperty("storageKey").GetString().Should().Be(keyB);
        sources[0].GetProperty("media").GetProperty("width").GetInt32().Should().Be(1920);
        sources[1].GetProperty("media").GetProperty("width").GetInt32().Should().Be(1280);

        // Every offered id in the bounded view must also carry its own "src" now that more than
        // one source exists (gated — a single-source view never gains this key, see
        // VideoAnalyzeStepExecutorTests for that guarantee).
        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        JsonElement viewShots = outputDoc.RootElement.GetProperty("view").GetProperty("shots");
        viewShots.EnumerateArray().All(s => s.TryGetProperty("src", out _)).Should().BeTrue(
            "every shot in a multi-source view must carry its own 'src' key");
    }

    [Fact]
    public async Task Single_element_Sources_list_behaves_identically_to_the_legacy_Source_field()
    {
        // A one-element Sources list must be indistinguishable in outcome from the pre-multi-source
        // Source field: same ids, no "src"/"sources" keys in the view (isMultiSource gate).
        Guid fileA = Guid.NewGuid();
        const string keyA = "projects/p/files/clipA.mp4";

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(fileA, ProjectId, "clipA.mp4", null, "uploads", keyA, "video/mp4", 1024, DateTime.UtcNow, null)
            });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), keyA, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, new byte[10]); return Task.CompletedTask; });

        var probe = new Mock<IMediaProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(3, 30, 1, 1920, 1080, "h264", "aac", 48000));

        var silence = new Mock<ISilenceDetector>();
        silence.Setup(s => s.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(double, double)>());

        var shotDetector = new Mock<IShotDetector>();
        shotDetector
            .Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([(0.0, 1.0), (1.0, 2.0)]);

        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
            DetectSilence: false,
            Transcription: VideoTranscriptionMode.Off,
            Sources: [new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA)]);

        StepExecutionContext context = CreateAnalyzeContext(config);
        VideoAnalyzeStepExecutor executor = CreateAnalyzeExecutor(workspace, probe, silence, shotDetector);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement view = doc.RootElement.GetProperty("view");
        view.TryGetProperty("sources", out _).Should().BeFalse("a single source's view must not gain the multi-source 'sources' key");
        foreach (JsonElement shot in view.GetProperty("shots").EnumerateArray())
            shot.TryGetProperty("src", out _).Should().BeFalse("a single source's shots must not gain the multi-source 'src' key");
    }

    // ---------------------------------------------------------------------
    // VideoAnalyzeStepExecutor: Phase 4 §2.1 step-level vision hoist (multi-source)
    // ---------------------------------------------------------------------

    private static (Mock<IProjectFileWorkspace> Workspace, Mock<IMediaProbe> Probe, Mock<ISilenceDetector> Silence,
        Mock<IShotDetector> ShotDetector) TwoSourceFixture(
        Guid fileA, Guid fileB, string keyA, string keyB,
        IReadOnlyList<(double, double)> shotsA, IReadOnlyList<(double, double)> shotsB)
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(fileA, ProjectId, "clipA.mp4", null, "uploads", keyA, "video/mp4", 1024, DateTime.UtcNow, null),
                new(fileB, ProjectId, "clipB.mp4", null, "uploads", keyB, "video/mp4", 1024, DateTime.UtcNow, null)
            });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), keyA, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, new byte[10]); return Task.CompletedTask; });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), keyB, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, new byte[10]); return Task.CompletedTask; });

        var probe = new Mock<IMediaProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => s.Contains("src0-")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(60, 30, 1, 1920, 1080, "h264", "aac", 48000));
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => s.Contains("src1-")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(60, 25, 1, 1280, 720, "h264", "aac", 48000));

        var silence = new Mock<ISilenceDetector>();
        silence.Setup(s => s.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(double, double)>());

        var shotDetector = new Mock<IShotDetector>();
        shotDetector
            .Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(p => p.Contains("src0-")), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(shotsA);
        shotDetector
            .Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(p => p.Contains("src1-")), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(shotsB);

        return (workspace, probe, silence, shotDetector);
    }

    private static ResolvedInferenceProvider VisionProviderFixture() => new(
        Guid.NewGuid(), "vision-test", InferenceProviderKind.OpenAICompatible, "http://localhost:9999", "vision-1", "", 30);

    private static VideoShotCaption CaptionFor(string shotId) => new(
        shotId, "a caption", [], "action", "setting", "mood", "Medium", "Eye level", [], [],
        "Unknown", "flat", "neutral", "centered", []);

    [Fact]
    public async Task MaxCaptionedShots_is_a_step_wide_cap_across_every_source_not_a_per_source_one()
    {
        Guid fileA = Guid.NewGuid(), fileB = Guid.NewGuid();
        const string keyA = "projects/p/files/clipA.mp4", keyB = "projects/p/files/clipB.mp4";

        var shotsA = Enumerable.Range(0, 20).Select(i => ((double)(i * 2), (double)(i * 2 + 2))).ToList();
        var shotsB = Enumerable.Range(0, 20).Select(i => ((double)(i * 2), (double)(i * 2 + 2))).ToList();

        (Mock<IProjectFileWorkspace> workspace, Mock<IMediaProbe> probe, Mock<ISilenceDetector> silence, Mock<IShotDetector> shotDetector) =
            TwoSourceFixture(fileA, fileB, keyA, keyB, shotsA, shotsB);

        var providerResolver = new Mock<IInferenceProviderResolver>();
        providerResolver.Setup(r => r.ResolveVisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(VisionProviderFixture());

        var shotCaptioner = new Mock<IShotCaptioner>();
        shotCaptioner
            .Setup(c => c.CaptionAsync(It.IsAny<ResolvedInferenceProvider>(), It.IsAny<ShotCaptionRequest>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedInferenceProvider _, ShotCaptionRequest r, int _, CancellationToken _) => CaptionFor(r.ShotId));

        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
            DetectSilence: false,
            Transcription: VideoTranscriptionMode.Off,
            AnalyzeVisuals: false,
            AnalyzeAudioLevels: false,
            Vision: VideoVisionMode.Optional,
            MaxCaptionedShots: 10,
            MinCaptionShotSeconds: 0.0,
            Sources:
            [
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileB)
            ]);

        StepExecutionContext context = CreateAnalyzeContext(config);
        VideoAnalyzeStepExecutor executor = CreateAnalyzeExecutor(
            workspace, probe, silence, shotDetector, shotCaptioner: shotCaptioner, providerResolver: providerResolver);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        shotCaptioner.Invocations.Count(i => i.Method.Name == nameof(IShotCaptioner.CaptionAsync)).Should().Be(10);
    }

    [Fact]
    public async Task Captioning_runs_after_every_source_has_been_analyzed()
    {
        Guid fileA = Guid.NewGuid(), fileB = Guid.NewGuid();
        const string keyA = "projects/p/files/clipA.mp4", keyB = "projects/p/files/clipB.mp4";

        var shotsA = new List<(double, double)> { (0.0, 2.0) };
        var shotsB = new List<(double, double)> { (0.0, 2.0) };

        (Mock<IProjectFileWorkspace> workspace, Mock<IMediaProbe> probe, Mock<ISilenceDetector> silence, Mock<IShotDetector> shotDetector) =
            TwoSourceFixture(fileA, fileB, keyA, keyB, shotsA, shotsB);

        var callOrder = new List<string>();

        var frameGridSampler = new Mock<IFrameGridSampler>();
        frameGridSampler
            .Setup(g => g.SampleAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { callOrder.Add("grid"); return new FrameGridResult(new byte[4 * 4 * 3], 4, 4, 1.0, 1); });

        var providerResolver = new Mock<IInferenceProviderResolver>();
        providerResolver.Setup(r => r.ResolveVisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(VisionProviderFixture());

        var shotCaptioner = new Mock<IShotCaptioner>();
        shotCaptioner
            .Setup(c => c.CaptionAsync(It.IsAny<ResolvedInferenceProvider>(), It.IsAny<ShotCaptionRequest>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedInferenceProvider _, ShotCaptionRequest r, int _, CancellationToken _) =>
            {
                callOrder.Add("caption");
                return CaptionFor(r.ShotId);
            });

        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
            DetectSilence: false,
            Transcription: VideoTranscriptionMode.Off,
            AnalyzeVisuals: true,
            AnalyzeAudioLevels: false,
            DetectNearDuplicates: false,
            Vision: VideoVisionMode.Optional,
            MinCaptionShotSeconds: 0.0,
            Sources:
            [
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileB)
            ]);

        StepExecutionContext context = CreateAnalyzeContext(config);
        VideoAnalyzeStepExecutor executor = CreateAnalyzeExecutor(
            workspace, probe, silence, shotDetector,
            frameGridSampler: frameGridSampler, shotCaptioner: shotCaptioner, providerResolver: providerResolver);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        callOrder.Count(c => c == "grid").Should().Be(2);
        callOrder.Count(c => c == "caption").Should().BeGreaterThan(0);
        int lastGridIndex = callOrder.LastIndexOf("grid");
        int firstCaptionIndex = callOrder.IndexOf("caption");
        firstCaptionIndex.Should().BeGreaterThan(lastGridIndex,
            "every source's visual analysis must complete before step-level captioning begins");
    }

    [Fact]
    public async Task Keyframes_are_extracted_from_the_clip_each_shot_actually_came_from()
    {
        Guid fileA = Guid.NewGuid(), fileB = Guid.NewGuid();
        const string keyA = "projects/p/files/clipA.mp4", keyB = "projects/p/files/clipB.mp4";

        var shotsA = new List<(double, double)> { (0.0, 2.0) };
        var shotsB = new List<(double, double)> { (0.0, 2.0) };

        (Mock<IProjectFileWorkspace> workspace, Mock<IMediaProbe> probe, Mock<ISilenceDetector> silence, Mock<IShotDetector> shotDetector) =
            TwoSourceFixture(fileA, fileB, keyA, keyB, shotsA, shotsB);

        var providerResolver = new Mock<IInferenceProviderResolver>();
        providerResolver.Setup(r => r.ResolveVisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(VisionProviderFixture());

        var keyframeExtractor = new Mock<IKeyframeExtractor>();
        var capturedInputPaths = new List<string>();
        keyframeExtractor
            .Setup(k => k.ExtractKeyframeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, double, int, CancellationToken>((_, inputPath, _, _, _, _) =>
            {
                capturedInputPaths.Add(inputPath);
                return Task.CompletedTask;
            });

        var shotCaptioner = new Mock<IShotCaptioner>();
        shotCaptioner
            .Setup(c => c.CaptionAsync(It.IsAny<ResolvedInferenceProvider>(), It.IsAny<ShotCaptionRequest>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedInferenceProvider _, ShotCaptionRequest r, int _, CancellationToken _) => CaptionFor(r.ShotId));

        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
            DetectSilence: false,
            Transcription: VideoTranscriptionMode.Off,
            AnalyzeVisuals: false,
            AnalyzeAudioLevels: false,
            Vision: VideoVisionMode.Optional,
            MinCaptionShotSeconds: 0.0,
            Sources:
            [
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileB)
            ]);

        StepExecutionContext context = CreateAnalyzeContext(config);
        VideoAnalyzeStepExecutor executor = CreateAnalyzeExecutor(
            workspace, probe, silence, shotDetector,
            keyframeExtractor: keyframeExtractor, shotCaptioner: shotCaptioner, providerResolver: providerResolver);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedInputPaths.Should().HaveCount(2);
        capturedInputPaths.Should().ContainSingle(p => p.Contains("src0-"));
        capturedInputPaths.Should().ContainSingle(p => p.Contains("src1-"));
    }

    [Fact]
    public async Task Single_source_vision_behaviour_is_unchanged_by_the_step_level_hoist()
    {
        Guid fileA = Guid.NewGuid();
        const string keyA = "projects/p/files/clipA.mp4";

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(fileA, ProjectId, "clipA.mp4", null, "uploads", keyA, "video/mp4", 1024, DateTime.UtcNow, null)
            });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), keyA, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, new byte[10]); return Task.CompletedTask; });

        var probe = new Mock<IMediaProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", "aac", 48000));

        var silence = new Mock<ISilenceDetector>();
        silence.Setup(s => s.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(double, double)>());

        var shotDetector = new Mock<IShotDetector>();
        shotDetector
            .Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([(0.0, 2.0)]);

        var providerResolver = new Mock<IInferenceProviderResolver>();
        providerResolver.Setup(r => r.ResolveVisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(VisionProviderFixture());

        var keyframeExtractor = new Mock<IKeyframeExtractor>();
        var capturedPaths = new List<(string InputPath, string OutputPath)>();
        keyframeExtractor
            .Setup(k => k.ExtractKeyframeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, double, int, CancellationToken>((_, inputPath, outputPath, _, _, _) =>
            {
                capturedPaths.Add((inputPath, outputPath));
                return Task.CompletedTask;
            });

        var shotCaptioner = new Mock<IShotCaptioner>();
        shotCaptioner
            .Setup(c => c.CaptionAsync(It.IsAny<ResolvedInferenceProvider>(), It.IsAny<ShotCaptionRequest>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedInferenceProvider _, ShotCaptionRequest r, int _, CancellationToken _) => CaptionFor(r.ShotId));

        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
            DetectSilence: false,
            Transcription: VideoTranscriptionMode.Off,
            AnalyzeVisuals: false,
            AnalyzeAudioLevels: false,
            Vision: VideoVisionMode.Optional,
            MinCaptionShotSeconds: 0.0);
        // Sources left null/empty — falls back to the single legacy Source field (one-element list).

        StepExecutionContext context = CreateAnalyzeContext(config);
        VideoAnalyzeStepExecutor executor = CreateAnalyzeExecutor(
            workspace, probe, silence, shotDetector,
            keyframeExtractor: keyframeExtractor, shotCaptioner: shotCaptioner, providerResolver: providerResolver);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedPaths.Should().HaveCount(1);
        // No "srcN-" prefix at all for a genuinely single-source config (not even "src0-") — the
        // filename must be byte-identical to the pre-Phase-4/pre-multi-source single-frame path.
        Path.GetFileName(capturedPaths[0].OutputPath).Should().Be("keyframe-s0.jpg");

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement shots = doc.RootElement.GetProperty("view").GetProperty("shots");
        shots.GetArrayLength().Should().Be(1);
        shots[0].TryGetProperty("c", out _).Should().BeTrue("the single shot must have been captioned");
    }

    [Fact]
    public async Task MaxSharpnessShots_is_a_step_wide_cap_across_every_source()
    {
        Guid fileA = Guid.NewGuid(), fileB = Guid.NewGuid();
        const string keyA = "projects/p/files/clipA.mp4", keyB = "projects/p/files/clipB.mp4";

        var shotsA = Enumerable.Range(0, 10).Select(i => ((double)(i * 2), (double)(i * 2 + 2))).ToList();
        var shotsB = Enumerable.Range(0, 10).Select(i => ((double)(i * 2), (double)(i * 2 + 2))).ToList();

        (Mock<IProjectFileWorkspace> workspace, Mock<IMediaProbe> probe, Mock<ISilenceDetector> silence, Mock<IShotDetector> shotDetector) =
            TwoSourceFixture(fileA, fileB, keyA, keyB, shotsA, shotsB);

        var frameGridSampler = new Mock<IFrameGridSampler>();
        frameGridSampler
            .Setup(g => g.SampleAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrameGridResult(new byte[4 * 4 * 3 * 4], 4, 4, 1.0, 4));

        var sharpnessSampler = new Mock<ISharpnessSampler>();
        sharpnessSampler
            .Setup(s => s.MeasureAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.6);

        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
            DetectSilence: false,
            Transcription: VideoTranscriptionMode.Off,
            AnalyzeVisuals: true,
            AnalyzeAudioLevels: false,
            DetectNearDuplicates: false,
            DetectSharpness: true,
            MaxSharpnessShots: 8,
            Sources:
            [
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileB)
            ]);

        StepExecutionContext context = CreateAnalyzeContext(config);
        VideoAnalyzeStepExecutor executor = CreateAnalyzeExecutor(
            workspace, probe, silence, shotDetector, frameGridSampler: frameGridSampler, sharpnessSampler: sharpnessSampler);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        sharpnessSampler.Invocations.Count(i => i.Method.Name == nameof(ISharpnessSampler.MeasureAsync)).Should().Be(8);
    }

    [Fact]
    public async Task MaxSharpnessShots_budget_is_consumed_on_every_attempt_even_when_measurement_fails()
    {
        // MeasureAsync returns null on every attempt (ffmpeg failure/missing output/short read).
        // Before the fix, Remaining was only decremented on a SUCCESSFUL measurement, so a source
        // that failed every attempt left the budget untouched and the next source started with it
        // still fully intact — a 2-source step with budget 4 would make 4 calls PER source (8
        // total) instead of 4 total. Each source here has far more shots than the budget so
        // exhausting the budget (not running out of shots) is what stops the attempts.
        Guid fileA = Guid.NewGuid(), fileB = Guid.NewGuid();
        const string keyA = "projects/p/files/clipA.mp4", keyB = "projects/p/files/clipB.mp4";

        var shotsA = Enumerable.Range(0, 10).Select(i => ((double)(i * 2), (double)(i * 2 + 2))).ToList();
        var shotsB = Enumerable.Range(0, 10).Select(i => ((double)(i * 2), (double)(i * 2 + 2))).ToList();

        (Mock<IProjectFileWorkspace> workspace, Mock<IMediaProbe> probe, Mock<ISilenceDetector> silence, Mock<IShotDetector> shotDetector) =
            TwoSourceFixture(fileA, fileB, keyA, keyB, shotsA, shotsB);

        var frameGridSampler = new Mock<IFrameGridSampler>();
        frameGridSampler
            .Setup(g => g.SampleAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrameGridResult(new byte[4 * 4 * 3 * 4], 4, 4, 1.0, 4));

        var sharpnessSampler = new Mock<ISharpnessSampler>();
        sharpnessSampler
            .Setup(s => s.MeasureAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((double?)null);

        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
            DetectSilence: false,
            Transcription: VideoTranscriptionMode.Off,
            AnalyzeVisuals: true,
            AnalyzeAudioLevels: false,
            DetectNearDuplicates: false,
            DetectSharpness: true,
            MaxSharpnessShots: 4,
            Sources:
            [
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileB)
            ]);

        StepExecutionContext context = CreateAnalyzeContext(config);
        VideoAnalyzeStepExecutor executor = CreateAnalyzeExecutor(
            workspace, probe, silence, shotDetector, frameGridSampler: frameGridSampler, sharpnessSampler: sharpnessSampler);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        sharpnessSampler.Invocations.Count(i => i.Method.Name == nameof(ISharpnessSampler.MeasureAsync)).Should().Be(4,
            because: "the budget must be consumed step-wide on every ATTEMPT, not only on a successful measurement");
    }

    // ---------------------------------------------------------------------
    // VideoAnalyzeStepExecutor: per-source guardrail enforcement
    // ---------------------------------------------------------------------

    [Fact]
    public async Task MaxInputBytes_guardrail_is_enforced_per_source_not_against_the_combined_total()
    {
        // Source 0 is small (well under the cap); source 1 alone exceeds MaxInputBytes. If the
        // guardrail were checked against the SUM across sources, source 0 being small wouldn't
        // matter (the combined total still exceeds the cap) — but this test's real point is that
        // the step fails specifically once IT REACHES source 1, proving the check runs
        // independently per source rather than only once at the very end against a grand total.
        Guid fileA = Guid.NewGuid();
        Guid fileB = Guid.NewGuid();
        const string keyA = "projects/p/files/clipA.mp4";
        const string keyB = "projects/p/files/clipB.mp4";

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(fileA, ProjectId, "clipA.mp4", null, "uploads", keyA, "video/mp4", 50, DateTime.UtcNow, null),
                new(fileB, ProjectId, "clipB.mp4", null, "uploads", keyB, "video/mp4", 500, DateTime.UtcNow, null)
            });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), keyA, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, new byte[50]); return Task.CompletedTask; });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), keyB, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, new byte[500]); return Task.CompletedTask; });

        var probe = new Mock<IMediaProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(3, 30, 1, 1920, 1080, "h264", "aac", 48000));

        var silence = new Mock<ISilenceDetector>();
        silence.Setup(s => s.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(double, double)>());
        var shotDetector = new Mock<IShotDetector>();
        shotDetector
            .Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(double, double)>());

        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
            DetectSilence: false,
            DetectShots: false,
            Transcription: VideoTranscriptionMode.Off,
            MaxInputBytes: 100, // source A (50 bytes) passes; source B (500 bytes) must fail
            Sources:
            [
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileB)
            ]);

        StepExecutionContext context = CreateAnalyzeContext(config);
        VideoAnalyzeStepExecutor executor = CreateAnalyzeExecutor(workspace, probe, silence, shotDetector);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        string code = doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
        code.Should().Be("SOURCE_TOO_LARGE");
        workspace.Verify(
            w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), keyB, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a source over the limit is refused from its stored size, before any download");
        doc.RootElement.GetProperty("error").GetProperty("message").GetString()!
            .Should().Contain("Source 1", because: "the failure must identify WHICH source exceeded the guardrail");
    }

    /// <summary>
    /// Security ENG-3. A playlist names other local files, so a source named as one is refused as the
    /// author's input before it is downloaded; a list file under a media name is refused by the
    /// probe's demuxer whitelist, and answers the same coded error rather than an unexpected one.
    /// </summary>
    [Theory]
    [InlineData(".m3u8", false)]
    [InlineData(".mp4", true)]
    public async Task A_source_that_is_a_playlist_or_list_file_is_refused_as_the_authors_input(string extension, bool refusedByProbe)
    {
        Guid file = Guid.NewGuid();
        // The shape a real upload's object has, built by the same helper the platform uses.
        string stored = ReelBolt.Shared.ProjectFilePath.BuildStorageKey(ProjectId, "userFiles", null, "clipA" + extension);

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(file, ProjectId, "clipA" + extension, null, "userFiles", stored, "video/mp4", 50, DateTime.UtcNow, null)
            });
        string? stagedAs = null;
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), stored, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) =>
            {
                stagedAs = Path.GetFileName(dest);
                File.WriteAllBytes(dest, new byte[50]);
                return Task.CompletedTask;
            });

        var probe = new Mock<IMediaProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MediaFormatNotAllowedException("source.mp4"));

        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: file),
            DetectSilence: false,
            DetectShots: false,
            Transcription: VideoTranscriptionMode.Off);

        StepExecutionResult result = await CreateAnalyzeExecutor(workspace, probe, new Mock<ISilenceDetector>(), new Mock<IShotDetector>())
            .ExecuteAsync(CreateAnalyzeContext(config));

        result.Status.Should().Be(StepStatus.Failed);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement error = doc.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(UserMediaInputFormats.RefusedCode);
        error.GetProperty("message").GetString().Should().Be(UserMediaInputFormats.RefusedMessage);
        ReelBolt.WorkflowEngine.Services.Billing.FailureClassifier.ForCode(UserMediaInputFormats.RefusedCode).Should().Be(FailureClass.UserInput);
        if (refusedByProbe)
            stagedAs.Should().Be("source.mp4");
        else
            stagedAs.Should().BeNull("a playlist is refused before any download");
    }

    [Fact]
    public async Task MaxDurationSeconds_guardrail_is_enforced_per_source()
    {
        // Source 0's duration is fine; source 1's duration alone exceeds MaxDurationSeconds. Proves
        // the duration guardrail (checked before any decode) runs independently for each source,
        // not only once against some combined figure.
        Guid fileA = Guid.NewGuid();
        Guid fileB = Guid.NewGuid();
        const string keyA = "projects/p/files/clipA.mp4";
        const string keyB = "projects/p/files/clipB.mp4";

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(fileA, ProjectId, "clipA.mp4", null, "uploads", keyA, "video/mp4", 1024, DateTime.UtcNow, null),
                new(fileB, ProjectId, "clipB.mp4", null, "uploads", keyB, "video/mp4", 1024, DateTime.UtcNow, null)
            });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsIn(keyA, keyB), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, new byte[10]); return Task.CompletedTask; });

        var probe = new Mock<IMediaProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => s.Contains("src0-")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(5, 30, 1, 1920, 1080, "h264", "aac", 48000));
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => s.Contains("src1-")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(500, 30, 1, 1920, 1080, "h264", "aac", 48000));

        var silence = new Mock<ISilenceDetector>();
        silence.Setup(s => s.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(double, double)>());
        var shotDetector = new Mock<IShotDetector>();
        shotDetector
            .Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(double, double)>());

        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
            DetectSilence: false,
            DetectShots: false,
            Transcription: VideoTranscriptionMode.Off,
            MaxDurationSeconds: 60,
            Sources:
            [
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileA),
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: fileB)
            ]);

        StepExecutionContext context = CreateAnalyzeContext(config);
        VideoAnalyzeStepExecutor executor = CreateAnalyzeExecutor(workspace, probe, silence, shotDetector);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("SOURCE_TOO_LONG");
    }

    private static StepExecutionContext CreateAnalyzeContext(VideoAnalyzeStepConfig config)
    {
        string configJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        });

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = configJson
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private static VideoAnalyzeStepExecutor CreateAnalyzeExecutor(
        Mock<IProjectFileWorkspace> workspace, Mock<IMediaProbe> probe, Mock<ISilenceDetector> silence, Mock<IShotDetector> shotDetector,
        Mock<IFrameGridSampler>? frameGridSampler = null, Mock<IKeyframeExtractor>? keyframeExtractor = null,
        Mock<IShotCaptioner>? shotCaptioner = null, Mock<IInferenceProviderResolver>? providerResolver = null,
        Mock<ISharpnessSampler>? sharpnessSampler = null)
    {
        var audioExtractor = new Mock<IAudioExtractor>();
        audioExtractor
            .Setup(a => a.ExtractWavAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, CancellationToken>(async (ws, _, outPath, ct) => await ws.WriteBytesAsync(outPath, new byte[100], ct));

        frameGridSampler ??= new Mock<IFrameGridSampler>();
        keyframeExtractor ??= new Mock<IKeyframeExtractor>();
        shotCaptioner ??= new Mock<IShotCaptioner>();
        sharpnessSampler ??= new Mock<ISharpnessSampler>();
        var transcriptionFactory = new Mock<ITranscriptionClientFactory>();
        providerResolver ??= new Mock<IInferenceProviderResolver>();

        string tempScratchRoot = Path.Combine(Path.GetTempPath(), "video-multisource-tests", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = tempScratchRoot,
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 30,
            CompileTimeoutSeconds = 30
        });

        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-1-analysis.json");
        workspace.SetupRegisteredRenderPublish();

        return new VideoAnalyzeStepExecutor(
            probe.Object, silence.Object, shotDetector.Object, audioExtractor.Object,
            frameGridSampler.Object, keyframeExtractor.Object, sharpnessSampler.Object, shotCaptioner.Object,
            workspace.Object, transcriptionFactory.Object, providerResolver.Object,
            options, NullLogger<VideoAnalyzeStepExecutor>.Instance);
    }

    // ---------------------------------------------------------------------
    // VideoCompileStepExecutor: cross-source Keep-span validation
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Keep_span_running_forward_across_two_sources_is_split_into_one_span_per_clip()
    {
        VideoAnalysisArtifact artifact = BuildTwoSourceArtifact(out string keyA, out string keyB);
        // s0 is source 0 (clip A), s1 is source 1 (clip B). Read in the offered order, "s0 to s1"
        // means "all of clip A's offered run, then clip B's" — every still image is its own source,
        // so an editor writes exactly this across two photos. It compiles as two spans.
        string decisionJson = BuildDecisionJson(("s0", "s1", "runs on into the next clip"));

        StepExecutionContext context = CreateCompileContext(artifact, decisionJson, keyA, keyB, out Mock<IProjectFileWorkspace> workspace);
        VideoCompileStepExecutor executor = CreateCompileExecutor(workspace);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("spanNormalization").GetProperty("splitAcrossSources").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Keep_span_running_backward_across_two_sources_is_rejected()
    {
        VideoAnalysisArtifact artifact = BuildTwoSourceArtifact(out string keyA, out string keyB);
        string decisionJson = BuildDecisionJson(("s1", "s0", "backwards across clips (invalid)"));

        StepExecutionContext context = CreateCompileContext(artifact, decisionJson, keyA, keyB, out Mock<IProjectFileWorkspace> workspace);
        VideoCompileStepExecutor executor = CreateCompileExecutor(workspace);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("MIXED_SOURCE_SPAN");
    }

    [Fact]
    public async Task Multi_source_StreamCopy_is_rejected_with_a_clear_error()
    {
        VideoAnalysisArtifact artifact = BuildTwoSourceArtifact(out string keyA, out string keyB);
        // Two separate, valid single-clip spans — one per source — is a legitimate cross-clip edit,
        // but StreamCopy has no multi-input equivalent.
        string decisionJson = BuildDecisionJson(("s0", "s0", "from clip A"), ("s1", "s1", "from clip B"));

        StepExecutionContext context = CreateCompileContext(
            artifact, decisionJson, keyA, keyB, out Mock<IProjectFileWorkspace> workspace,
            cfg => cfg with { Mode = VideoCompileMode.StreamCopy, AllowKeyframeSnapping = true });
        VideoCompileStepExecutor executor = CreateCompileExecutor(workspace);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("MULTI_SOURCE_REQUIRES_REENCODE");
    }

    [Fact]
    public async Task Multi_source_reencode_downloads_both_clips_and_builds_a_multi_input_concat_filtergraph()
    {
        VideoAnalysisArtifact artifact = BuildTwoSourceArtifact(out string keyA, out string keyB);
        string decisionJson = BuildDecisionJson(("s0", "s0", "from clip A"), ("s1", "s1", "from clip B"));

        List<string>? capturedArgs = null;
        string? capturedFilterComplex = null;
        StepExecutionContext context = CreateCompileContext(artifact, decisionJson, keyA, keyB, out Mock<IProjectFileWorkspace> workspace);
        VideoCompileStepExecutor executor = CreateCompileExecutor(
            workspace,
            args =>
            {
                // Read the scripted filter_complex file INSIDE the callback, while the ffmpeg mock
                // is invoked — not after ExecuteAsync returns, at which point the executor's own
                // `finally { scratch?.Dispose(); }` has already recursively deleted the whole
                // scratch directory (by design, for a real run) and the file would be gone.
                capturedArgs = args.ToList();
                int idx = capturedArgs.IndexOf("-filter_complex_script");
                if (idx >= 0) capturedFilterComplex = File.ReadAllText(capturedArgs[idx + 1]);
            });

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedArgs.Should().NotBeNull();

        // Two distinct source inputs, one per clip, in deterministic (sorted-by-source-index) order.
        List<int> inputPositions = capturedArgs!
            .Select((a, i) => (a, i))
            .Where(t => t.a == "-i")
            .Select(t => t.i + 1)
            .ToList();
        inputPositions.Count.Should().BeGreaterThanOrEqualTo(2);
        capturedArgs![inputPositions[0]].Should().Contain("source-0");
        capturedArgs![inputPositions[1]].Should().Contain("source-1");

        // Multi-source always scripts the filtergraph to a file (never inline -filter_complex).
        capturedArgs.Should().Contain("-filter_complex_script");
        capturedArgs.Should().NotContain("-filter_complex");

        capturedFilterComplex.Should().NotBeNull();
        string filterComplex = capturedFilterComplex!;

        // Each span trims from ITS OWN input index: span 0 (source 0, ffmpeg input 0) must trim
        // "[0:v]"/"[0:a]"; span 1 (source 1, ffmpeg input 1) must trim "[1:v]"/"[1:a]".
        filterComplex.Should().Contain("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=");
        filterComplex.Should().Contain("[0:a]atrim=");
        filterComplex.Should().Contain("[1:v]fps=25/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=");
        filterComplex.Should().Contain("[1:a]atrim=");
        filterComplex.Should().Contain("concat=n=2:v=1:a=1");

        capturedArgs.Should().Contain("-map");
        capturedArgs.Should().Contain("[vout]");
        capturedArgs.Should().Contain("[aout]");
    }

    [Fact]
    public async Task Multi_source_compile_with_music_mixes_after_the_concat_stage_and_music_is_the_last_input()
    {
        // Background music (see docs/video-editing.md "Background music") interacting with the
        // multi-source concat path: the concat stage's audio output flips to [adial], music is
        // appended as the LAST ffmpeg input (after both source clips), and the mix stage produces
        // [aout]. A music-free multi-source compile (the test above) must remain unaffected.
        VideoAnalysisArtifact artifact = BuildTwoSourceArtifact(out string keyA, out string keyB);
        string decisionJson = BuildDecisionJson(("s0", "s0", "from clip A"), ("s1", "s1", "from clip B"));

        const string musicKey = "projects/p/userFiles/bed.mp3";
        Guid musicProjectFileId = Guid.NewGuid();

        List<string>? capturedArgs = null;
        string? capturedFilterComplex = null;
        StepExecutionContext context = CreateCompileContext(
            artifact, decisionJson, keyA, keyB, out Mock<IProjectFileWorkspace> workspace,
            cfg => cfg with { EnableMusic = true, MusicTrackProjectFileId = musicProjectFileId });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), musicKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, new byte[] { 0x00 }); return Task.CompletedTask; });
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(musicProjectFileId, ProjectId, "bed.mp3", null, "userFiles", musicKey, "audio/mpeg", 4096, DateTime.UtcNow, null)
            });

        VideoCompileStepExecutor executor = CreateCompileExecutor(
            workspace,
            args =>
            {
                // Read the scripted filter_complex file INSIDE the callback, while the ffmpeg mock
                // is invoked — not after ExecuteAsync returns, at which point the executor's own
                // `finally { scratch?.Dispose(); }` has already recursively deleted the whole
                // scratch directory (by design, for a real run) and the file would be gone.
                if (!args.Contains("-filter_complex_script")) return;
                capturedArgs = args.ToList();
                int idx = capturedArgs.IndexOf("-filter_complex_script");
                if (idx >= 0) capturedFilterComplex = File.ReadAllText(capturedArgs[idx + 1]);
            });

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedArgs.Should().NotBeNull();

        // Three inputs: source clip 0, source clip 1, then music LAST.
        List<int> inputPositions = capturedArgs!
            .Select((a, i) => (a, i))
            .Where(t => t.a == "-i")
            .Select(t => t.i + 1)
            .ToList();
        inputPositions.Should().HaveCount(3);
        capturedArgs![inputPositions[0]].Should().Contain("source-0");
        capturedArgs[inputPositions[1]].Should().Contain("source-1");
        capturedArgs[inputPositions[^1]].Should().Contain("music", "the music track must be the LAST ffmpeg input, after both source clips");

        capturedFilterComplex.Should().NotBeNull();
        string filterComplex = capturedFilterComplex!;

        filterComplex.Should().Contain("concat=n=2:v=1:a=1[vout][adial]", "the concat stage's audio output must flip to [adial] when music is enabled");
        filterComplex.Should().Contain("[2:a]atrim=end=", "music is ffmpeg input index 2 here (2 source clips + 0 asset overlays)");
        filterComplex.Should().Contain("[amus]");
        filterComplex.Should().Contain("[adial][amus]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]");
    }

    [Fact]
    public async Task Multi_source_compile_where_one_clip_has_no_audio_synthesizes_silence_for_that_segment_only()
    {
        // concat's own "a=" stream count must be uniform across every concatenated segment, so a
        // mix of audio-having and audio-less source clips can't simply omit the audio-less one's
        // own branch. The fix: synthesize a matching-duration anullsrc silence branch for the
        // audio-less segment while the audio-having segment keeps its real atrim branch — turning
        // "the whole video goes mute because one clip has no audio" into "only that segment is
        // silent". Source 0 (clip A) has real audio; source 1 (clip B) has none.
        VideoAnalysisArtifact artifact = BuildTwoSourceArtifact(out string keyA, out string keyB);
        string decisionJson = BuildDecisionJson(("s0", "s0", "from clip A"), ("s1", "s1", "from clip B"));

        List<string>? capturedArgs = null;
        string? capturedFilterComplex = null;
        StepExecutionContext context = CreateCompileContext(artifact, decisionJson, keyA, keyB, out Mock<IProjectFileWorkspace> workspace);
        VideoCompileStepExecutor executor = CreateCompileExecutor(
            workspace,
            args =>
            {
                // Read the scripted filter_complex file INSIDE the callback, while the ffmpeg
                // mock is invoked — not after ExecuteAsync returns, at which point the executor's
                // own `finally { scratch?.Dispose(); }` has already recursively deleted the whole
                // scratch directory (by design, for a real run) and the file would be gone.
                capturedArgs = args.ToList();
                int idx = capturedArgs.IndexOf("-filter_complex_script");
                if (idx >= 0) capturedFilterComplex = File.ReadAllText(capturedArgs[idx + 1]);
            },
            configureMediaProbe: mock => mock
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(path => path.Contains("source-1")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", null, null)));

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedArgs.Should().NotBeNull();
        capturedFilterComplex.Should().NotBeNull();
        string filterComplex = capturedFilterComplex!;

        filterComplex.Should().Contain("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=", "source 0's video still concatenates normally");
        filterComplex.Should().Contain("[1:v]fps=25/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=", "source 1's video still concatenates normally");
        filterComplex.Should().Contain("[0:a]atrim=", "source 0 has real audio and must keep its real atrim branch");
        filterComplex.Should().NotContain("[1:a]atrim=", "source 1 has no audio stream to atrim from");
        filterComplex.Should().Contain("anullsrc=r=48000:cl=stereo:d=", "source 1's segment gets synthesized silence instead");
        filterComplex.Should().Contain("concat=n=2:v=1:a=1", "audio is still present for the whole output — only one segment's own audio is synthetic");

        capturedArgs.Should().Contain("[aout]");
        capturedArgs.Should().Contain("-c:a");

        // The EDL/outputSummary audio block must honestly report that not every segment kept its
        // own original sound (see the "audio" node in VideoCompileStepExecutor.ExecuteAsync).
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement audio = doc.RootElement.GetProperty("audio");
        audio.GetProperty("applied").GetBoolean().Should().BeTrue();
        audio.GetProperty("syntheticSilenceSegmentCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Multi_source_compile_where_no_clip_has_audio_drops_audio_from_the_whole_output()
    {
        // Degenerate case: NOT ONE referenced source has an audio stream, so there is nothing real
        // to preserve anywhere — the whole output drops audio exactly as it did before per-segment
        // synthesis existed, rather than concatenating an all-synthetic silent track.
        VideoAnalysisArtifact artifact = BuildTwoSourceArtifact(out string keyA, out string keyB);
        string decisionJson = BuildDecisionJson(("s0", "s0", "from clip A"), ("s1", "s1", "from clip B"));

        List<string>? capturedArgs = null;
        string? capturedFilterComplex = null;
        StepExecutionContext context = CreateCompileContext(artifact, decisionJson, keyA, keyB, out Mock<IProjectFileWorkspace> workspace);
        VideoCompileStepExecutor executor = CreateCompileExecutor(
            workspace,
            args =>
            {
                capturedArgs = args.ToList();
                int idx = capturedArgs.IndexOf("-filter_complex_script");
                if (idx >= 0) capturedFilterComplex = File.ReadAllText(capturedArgs[idx + 1]);
            },
            configureMediaProbe: mock => mock
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", null, null)));

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedArgs.Should().NotBeNull();
        capturedFilterComplex.Should().NotBeNull();
        string filterComplex = capturedFilterComplex!;

        filterComplex.Should().Contain("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=", "source 0's video still concatenates normally");
        filterComplex.Should().Contain("[1:v]fps=25/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=", "source 1's video still concatenates normally");
        filterComplex.Should().NotContain("[0:a]atrim=");
        filterComplex.Should().NotContain("[1:a]atrim=");
        filterComplex.Should().NotContain("anullsrc=", "no source has any audio, so there is nothing to synthesize silence to match");
        filterComplex.Should().Contain("concat=n=2:v=1:a=0", "the concat filter itself must declare zero audio streams");

        capturedArgs.Should().NotContain("[aout]");
        capturedArgs.Should().NotContain("-c:a");

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement audio = doc.RootElement.GetProperty("audio");
        audio.GetProperty("applied").GetBoolean().Should().BeFalse();
        audio.GetProperty("reason").GetString().Should().Be("no_audio_stream_in_source");
    }

    [Fact]
    public async Task Single_distinct_source_referenced_by_a_multi_source_artifact_uses_the_original_single_input_path()
    {
        // The artifact declares two sources, but the decision only keeps material from one of
        // them — this must take the UNCHANGED single-input EncodeReencodeAsync path (only one -i),
        // never the multi-source concat path, even though the artifact itself is multi-source-capable.
        VideoAnalysisArtifact artifact = BuildTwoSourceArtifact(out string keyA, out string keyB);
        string decisionJson = BuildDecisionJson(("s0", "s0", "from clip A only"));

        List<string>? capturedArgs = null;
        StepExecutionContext context = CreateCompileContext(artifact, decisionJson, keyA, keyB, out Mock<IProjectFileWorkspace> workspace);
        VideoCompileStepExecutor executor = CreateCompileExecutor(workspace, args => capturedArgs = args.ToList());

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedArgs.Should().NotBeNull();
        capturedArgs!.Count(a => a == "-i").Should().Be(1);
        capturedArgs.Should().NotContain("-filter_complex_script");
        capturedArgs.Should().Contain("-filter_complex");
    }

    [Fact]
    public async Task EditRoom_shaped_decision_with_a_room_metadata_block_compiles_identically_to_a_solo_decision()
    {
        // A StepType.EditRoom step emits the exact VideoEditDecisionOutput shape a solo
        // VideoStoryEditor step does, plus an ADDITIVE sibling "room" object. Compile's Decision
        // resolution must consume it identically regardless of which step type produced it — the
        // extra key is ignored by deserialization (no UnmappedMemberHandling.Disallow), and a
        // multi-source cross-clip decision from a room must build the same two-input concat encode.
        VideoAnalysisArtifact artifact = BuildTwoSourceArtifact(out string keyA, out string keyB);

        using JsonDocument soloDoc = JsonDocument.Parse(
            BuildDecisionJson(("s0", "s0", "from clip A"), ("s1", "s1", "from clip B")));
        var roomShaped = JsonSerializer.SerializeToNode(soloDoc.RootElement)!.AsObject();
        roomShaped["room"] = new System.Text.Json.Nodes.JsonObject
        {
            ["seats"] = new System.Text.Json.Nodes.JsonArray("PacingEditor", "StoryEditor", "CraftEditor"),
            ["turnCount"] = 5,
            ["terminationReason"] = "sentinel",
            ["degraded"] = false
        };
        string decisionJson = roomShaped.ToJsonString();

        List<string>? capturedArgs = null;
        StepExecutionContext context = CreateCompileContext(artifact, decisionJson, keyA, keyB, out Mock<IProjectFileWorkspace> workspace);
        VideoCompileStepExecutor executor = CreateCompileExecutor(workspace, args => capturedArgs = args.ToList());

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedArgs.Should().NotBeNull();
        capturedArgs!.Count(a => a == "-i").Should().Be(2, "both clips' spans survived, so both must be inputs");
    }

    // ---------------------------------------------------------------------
    // B8b — the trial output policy is the LAST picture stage on this path too
    // ---------------------------------------------------------------------

    /// <summary>The organization the B8b multi-source test compiles for — never Guid.Empty.</summary>
    private static readonly Guid PolicyOrgId = Guid.Parse("b8b0b8b0-0000-4000-8000-00000000b8b1");

    /// <summary>A scope factory serving the Trial plan (720p cap, watermark required).</summary>
    private static IServiceScopeFactory PolicyScopeFactory(
        string tier = "trial", int maxOutputHeight = OutputPolicyFilterBuilder.DefaultMaxHeight, bool watermark = true)
    {
        var entitlements = new Mock<IOrganizationEntitlements>();
        entitlements
            .Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet
            {
                Tier = tier,
                MaxOutputHeight = maxOutputHeight,
                Watermark = watermark
            }));

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(p => p.GetService(typeof(IOrganizationEntitlements))).Returns(entitlements.Object);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);
        return factory.Object;
    }

    [Fact]
    public async Task Output_policy_caps_the_multi_source_concat_as_the_last_picture_stage()
    {
        // The segmented (concat/xfade) path has its own label plumbing, so the policy must own
        // [vout] there too: the concat hands off to [vpol] and the cap stage produces [vout]. The
        // canonical frame is 1080p (the first kept span's clip), so the Trial cap really does work.
        VideoAnalysisArtifact artifact = BuildTwoSourceArtifact(out string keyA, out string keyB);
        string decisionJson = BuildDecisionJson(("s0", "s0", "from clip A"), ("s1", "s1", "from clip B"));

        List<string>? capturedArgs = null;
        string? capturedFilterComplex = null;
        StepExecutionContext context = CreateCompileContext(
            artifact, decisionJson, keyA, keyB, out Mock<IProjectFileWorkspace> workspace,
            organizationId: PolicyOrgId);
        VideoCompileStepExecutor executor = CreateCompileExecutor(
            workspace,
            args =>
            {
                // Read the scripted filtergraph INSIDE the callback — the scratch space is disposed
                // as soon as ExecuteAsync returns.
                capturedArgs = args.ToList();
                int idx = capturedArgs.IndexOf("-filter_complex_script");
                if (idx >= 0) capturedFilterComplex = File.ReadAllText(capturedArgs[idx + 1]);
            },
            scopeFactory: PolicyScopeFactory());

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedFilterComplex.Should().NotBeNull();
        string filterComplex = capturedFilterComplex!;

        filterComplex.Should().Contain("concat=n=2:v=1:a=1[vpol]", "the concat must feed the policy, not [vout] directly");
        filterComplex.Should().Contain("[vpol]scale=-2:'min(ih,720)':flags=lanczos[vout]");
        filterComplex.Should().NotContain("concat=n=2:v=1:a=1[vout]",
            "the policy is the LAST picture stage, so nothing after the concat may claim [vout]");

        capturedArgs!.Should().Contain("[vout]");
        capturedArgs.Should().NotContain("-filter_complex", "multi-source always scripts the filtergraph to a file");
    }

    [Theory]
    [InlineData(1080, 1920, null)]
    [InlineData(1440, 2560, "[vpol]scale='min(iw,1080)':-2:flags=lanczos[vout]")]
    public async Task A_vertical_film_is_capped_on_its_short_side_and_never_left_a_filterless_stage(
        int width, int height, string? expectedCapStage)
    {
        // Staging run 514ad632 (and two before it): two 1080x1920 clips under a Pro plan's 1080p
        // cap. The gate saw 1920 > 1080 and routed the concat into [vpol]; the builder saw a short
        // side of 1080, emitted nothing, and the graph ended in a bare "[vpol]" — ffmpeg exits 8
        // with "No such filter: ''".
        VideoAnalysisArtifact artifact = BuildTwoSourceArtifact(
            out string keyA, out string keyB,
            new VideoAnalysisMedia(10, 30, 1, width, height),
            new VideoAnalysisMedia(4, 24, 1, width, height));
        string decisionJson = BuildDecisionJson(("s0", "s0", "from clip A"), ("s1", "s1", "from clip B"));

        string? capturedFilterComplex = null;
        StepExecutionContext context = CreateCompileContext(
            artifact, decisionJson, keyA, keyB, out Mock<IProjectFileWorkspace> workspace,
            organizationId: PolicyOrgId);
        VideoCompileStepExecutor executor = CreateCompileExecutor(
            workspace,
            args =>
            {
                List<string> list = args.ToList();
                int idx = list.IndexOf("-filter_complex_script");
                if (idx >= 0) capturedFilterComplex = File.ReadAllText(list[idx + 1]);
            },
            configureMediaProbe: probe => probe
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(10, 30, 1, width, height, "h264", "aac", 48000)),
            scopeFactory: PolicyScopeFactory(tier: "pro", maxOutputHeight: 1080, watermark: false));

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        string filterComplex = capturedFilterComplex!;
        filterComplex.Split(';')
            .Where(chain => System.Text.RegularExpressions.Regex.Replace(chain, @"\[[^\]]*\]", "").Trim().Length == 0)
            .Should().BeEmpty("every stage of the graph must carry a filter");
        if (expectedCapStage is null)
            filterComplex.Should().NotContain("[vpol]", "a frame already within the cap must not be routed into the policy stage");
        else
            filterComplex.Should().Contain(expectedCapStage);
    }

    // ---------------------------------------------------------------------
    // Shared multi-source compile fixtures
    // ---------------------------------------------------------------------

    private static VideoAnalysisArtifact BuildTwoSourceArtifact(
        out string keyA, out string keyB, VideoAnalysisMedia? sourceA = null, VideoAnalysisMedia? sourceB = null)
    {
        keyA = "projects/p/files/clipA.mp4";
        keyB = "projects/p/files/clipB.mp4";

        VideoAnalysisMedia mediaA = sourceA ?? new VideoAnalysisMedia(10, 30, 1, 1920, 1080);
        VideoAnalysisMedia mediaB = sourceB ?? new VideoAnalysisMedia(8, 25, 1, 1280, 720);

        List<VideoAnalysisShot> shots =
        [
            new VideoAnalysisShot("s0", 0.0, 4.0, SourceIndex: 0),
            new VideoAnalysisShot("s1", 0.0, 3.0, SourceIndex: 1)
        ];

        return new VideoAnalysisArtifact(
            Version: 2,
            Media: mediaA,
            Shots: shots,
            SilenceSpans: [],
            Segments: [],
            Words: [],
            OfferedIds: ["s0", "s1"],
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Sources:
            [
                new VideoAnalysisSourceInfo(0, keyA, mediaA),
                new VideoAnalysisSourceInfo(1, keyB, mediaB)
            ]);
    }

    private static StepExecutionContext CreateCompileContext(
        VideoAnalysisArtifact artifact, string decisionJson, string keyA, string keyB,
        out Mock<IProjectFileWorkspace> workspace,
        Func<VideoCompileStepConfig, VideoCompileStepConfig>? configOverride = null,
        // B8b: the run's organization, i.e. what the trial output policy is read from. Guid.Empty
        // (the default) is the executor's "no organization" sentinel.
        Guid organizationId = default)
    {
        workspace = new Mock<IProjectFileWorkspace>();
        const string analysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";

        string artifactJson = JsonSerializer.Serialize(artifact, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), analysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllText(dest, artifactJson); return Task.CompletedTask; });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), keyA, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, new byte[] { 0x00, 0x01 }); return Task.CompletedTask; });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), keyB, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) => { File.WriteAllBytes(dest, new byte[] { 0x02, 0x03 }); return Task.CompletedTask; });

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Previous),
            AnalysisStepOrder: 1);
        if (configOverride is not null)
            config = configOverride(config);

        string configJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        });

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = configJson
        };

        List<StepOutputHistoryEntry> history =
        [
            new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: analysisKey),
            new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null)
        ];

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId, OrganizationId = organizationId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private static VideoCompileStepExecutor CreateCompileExecutor(
        Mock<IProjectFileWorkspace> workspace, Action<IReadOnlyList<string>>? ffmpegArgsCaptured = null,
        bool amixNormalizeAvailable = true, Action<Mock<IMediaProbe>>? configureMediaProbe = null,
        // B8b: a real scope factory (with an entitlement service in it) arms the output policy;
        // the bare Mock leaves every existing multi-source test on the pre-B8b path.
        IServiceScopeFactory? scopeFactory = null)
    {
        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
        VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();

        var toolRunner = new Mock<IVideoToolRunner>();
        // The perspective/alphamerge probe (tracked screen inserts — see
        // ResolveInsertsAsync/IsPerspectiveAvailableAsync). Without this setup the probe throws
        // into its own catch and every insert is skipped as "filters unavailable", which would
        // make an insert test pass for entirely the wrong reason.
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "... drawtext perspective alphamerge ...", string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((args, _, _) => ffmpegArgsCaptured?.Invoke(args))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken, Action<string>>((args, _, _, _) => ffmpegArgsCaptured?.Invoke(args))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        // Applied after the two catch-all setups above so it wins for the amix probe specifically
        // (background music — see VideoCompileStepExecutorTests.CreateExecutor's identical setup).
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => a.Contains("filter=amix")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, amixNormalizeAvailable ? "... normalize ..." : "... (no normalize) ...", string.Empty, false));

        var mediaProbe = new Mock<IMediaProbe>();
        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", "aac", 48000));
        // Applied after the catch-all above, mirroring VideoCompileStepExecutorTests.CreateExecutor's
        // identical override precedence — a test-supplied probe (e.g. "this one source has no audio
        // stream") wins over the default success response.
        configureMediaProbe?.Invoke(mediaProbe);

        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-3-edl.json");
        workspace.SetupRegisteredRenderPublish();

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

        string tempScratchRoot = Path.Combine(Path.GetTempPath(), "video-multisource-compile-tests", Guid.NewGuid().ToString("N"));
        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = tempScratchRoot,
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 30,
            CompileTimeoutSeconds = 30
        });

        var bareScopeFactory = new Mock<IServiceScopeFactory>();

        return new VideoCompileStepExecutor(
            toolRunner.Object, mediaProbe.Object, workspace.Object, scopeFactory ?? bareScopeFactory.Object, options,
            NullLogger<VideoCompileStepExecutor>.Instance);
    }

    private static string ErrorCode(StepExecutionResult result)
    {
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString() ?? string.Empty;
    }

    private static string BuildDecisionJson(params (string FromId, string ToId, string Reason)[] spans)
    {
        var keep = spans.Select(s => new { fromId = s.FromId, toId = s.ToId, reason = s.Reason });
        return JsonSerializer.Serialize(new { keep, editRationale = "test", suggestedTitle = "Test Edit" });
    }

    // ---------------------------------------------------------------------
    // Tracked screen inserts on a multi-source compile (bug 3)
    //
    // Inserts used to be skipped outright whenever the compile routed to the segmented concat
    // encode, which meant a green-screen clip could not be used in ANY multi-source edit: the raw
    // flat plate simply appeared in the final cut. The corner-pin math never actually depended on
    // how the base video was assembled — every number comes from the tracked region mapped through
    // OutputTimeline, which is source-aware — so what was missing was encode-path plumbing.
    // ---------------------------------------------------------------------

    private static VideoInsertRegionTrack BuildInsertTrackOnSource(
        int sourceIndex, string id, string shotId, double startSec, double endSec) => new(
        Id: id, ShotId: shotId, StartSec: startSec, EndSec: endSec,
        Keyframes:
        [
            new VideoInsertQuadKeyframe(startSec, 0.30, 0.20, 0.60, 0.22, 0.31, 0.70, 0.61, 0.72),
            new VideoInsertQuadKeyframe(endSec, 0.35, 0.20, 0.65, 0.22, 0.36, 0.70, 0.66, 0.72)
        ],
        Confidence: 0.9, MeanAreaRatio: 0.12, MeanAspectRatio: 0.6,
        MotionClass: "Slow", ColorName: "green", SourceIndex: sourceIndex);

    private static string BuildInsertsPlanJson(params (string RegionId, string AssetKey)[] inserts) =>
        JsonSerializer.Serialize(new
        {
            overlays = Array.Empty<object>(),
            inserts = inserts.Select(i => new { regionId = i.RegionId, renderedAssetStorageKey = i.AssetKey, reason = "screen content" }),
            planRationale = "test"
        });

    /// <summary>
    /// Multi-source variant of <see cref="CreateCompileContext"/> that also supplies a graphics/
    /// insert plan as a third history entry, so <c>GraphicsPlan</c> can point at it by step order.
    /// </summary>
    private static StepExecutionContext CreateCompileContextWithInsertPlan(
        VideoAnalysisArtifact artifact, string decisionJson, string planJson, string keyA, string keyB,
        Guid executionId, IReadOnlyList<string> assetKeys,
        out Mock<IProjectFileWorkspace> workspace,
        Func<VideoCompileStepConfig, VideoCompileStepConfig>? configOverride = null)
    {
        StepExecutionContext baseContext = CreateCompileContext(
            artifact, decisionJson, keyA, keyB, out workspace,
            cfg =>
            {
                cfg = cfg with
                {
                    // Decision must name the story-editor step EXPLICITLY: with a plan step sitting
                    // between it and the compile step, `Previous` would resolve to the plan's own
                    // output instead — the same reason every graphics/music template pins a step
                    // order rather than using Previous (see docs/video-editing.md).
                    Decision = new ExtractInputRef(ExtractInputSource.Step, 2),
                    EnableGraphics = false,
                    EnableInserts = true,
                    GraphicsPlan = new ExtractInputRef(ExtractInputSource.Step, 3)
                };
                return configOverride is not null ? configOverride(cfg) : cfg;
            });

        Mock<IProjectFileWorkspace> ws = workspace;
        foreach (string assetKey in assetKeys)
        {
            ws.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), assetKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) =>
                {
                    File.WriteAllBytes(dest, new byte[] { 0x00, 0x01, 0x02 });
                    return Task.CompletedTask;
                });
        }

        var history = baseContext.StepOutputHistory.ToList();
        history.Add(new StepOutputHistoryEntry(3, "MotionGraphicsPlanner", planJson, OutputStorageKey: null, ArtifactStorageKey: null));

        // The compile step itself must sort after the plan step it references.
        baseContext.Step.StepOrder = 4;

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = executionId, ProjectId = ProjectId },
            Step = baseContext.Step,
            AllSteps = [baseContext.Step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = 3,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    [Fact]
    public async Task Multi_source_compile_applies_a_tracked_screen_insert_gated_to_its_own_source_s_output_window()
    {
        VideoAnalysisArtifact artifact = BuildTwoSourceArtifact(out string keyA, out string keyB);
        // The plate lives on source 1 (clip B), which contributes the SECOND output segment.
        artifact = artifact with
        {
            InsertRegions = [BuildInsertTrackOnSource(sourceIndex: 1, "r0", "s1", 0.5, 2.5)],
            OfferedInsertRegionIds = ["r0"]
        };

        // Span 0 = source 0's shot s0, span 1 = source 1's shot s1. After frame quantization span 0
        // runs [0, 4.133333) and span 1 [0, 3.12), so clip B occupies output time
        // [4.133333, 7.253333) and the plate's source window [0.5, 2.5) maps to
        // [4.633333, 6.633333) — clip A's own output time is untouched by it.
        string decisionJson = BuildDecisionJson(("s0", "s0", "from clip A"), ("s1", "s1", "from clip B"));
        const double ClipBOutputStart = 4.133333333333334;

        Guid executionId = Guid.NewGuid();
        string assetKey = $"projects/{ProjectId}/outputFiles/{executionId:D}/screen.mp4";
        StepExecutionContext context = CreateCompileContextWithInsertPlan(
            artifact, decisionJson, BuildInsertsPlanJson(("r0", assetKey)), keyA, keyB,
            executionId, [assetKey], out Mock<IProjectFileWorkspace> workspace);

        List<string>? capturedArgs = null;
        string? capturedFilterComplex = null;
        VideoCompileStepExecutor executor = CreateCompileExecutor(workspace, args =>
        {
            capturedArgs = args.ToList();
            int idx = capturedArgs.IndexOf("-filter_complex_script");
            if (idx >= 0) capturedFilterComplex = File.ReadAllText(capturedArgs[idx + 1]);
        });

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedFilterComplex.Should().NotBeNull();
        string filterComplex = capturedFilterComplex!;

        // The corner-pin chain is actually built — perspective + the white-plate alphamerge mask.
        filterComplex.Should().Contain("perspective=", "the insert must be corner-pinned, not skipped");
        filterComplex.Should().Contain("alphamerge");
        filterComplex.Should().Contain("sense=destination:eval=frame");

        // The insert's asset is its own ffmpeg input, AFTER both source clips (inputs 0 and 1).
        filterComplex.Should().Contain("[2:v]scale=", "the insert asset is ffmpeg input 2 — after both source inputs");
        List<int> inputPositions = capturedArgs!
            .Select((a, i) => (a, i)).Where(t => t.a == "-i").Select(t => t.i + 1).ToList();
        inputPositions.Should().HaveCount(3);
        capturedArgs![inputPositions[0]].Should().Contain("source-0");
        capturedArgs![inputPositions[1]].Should().Contain("source-1");
        capturedArgs![inputPositions[2]].Should().Contain("insert-asset-");

        // And it is INERT outside clip B's own output window: the enable gate covers exactly the
        // plate's source window mapped onto clip B's output segment, so during clip A's
        // [0, 4.133333) the overlay contributes nothing at all.
        filterComplex.Should().Contain("enable='between(t,4.633333333333334,6.633333333333334)'");

        // Stated structurally as well as by exact string, so the intent survives a fixture change:
        // the gate must open only after clip A's contribution has ended.
        string gate = filterComplex[filterComplex.IndexOf("enable='between(t,", StringComparison.Ordinal)..];
        gate = gate["enable='between(t,".Length..];
        double gateStart = double.Parse(gate[..gate.IndexOf(',')], System.Globalization.CultureInfo.InvariantCulture);
        gateStart.Should().BeGreaterThanOrEqualTo(ClipBOutputStart,
            "an insert tracked on clip B must never paint over output time that came from clip A");

        // The insert stage chains off the concat stage, not off a raw source input.
        filterComplex.Should().Contain("[vcat]");
        filterComplex.Should().Contain("[vout]");
    }

    [Fact]
    public void Insert_corners_are_mapped_through_the_letterbox_a_clip_gets_on_the_canonical_canvas()
    {
        // A tracked quad's corners are normalized to its OWN source frame. When several clips are
        // normalized into one canvas the encode applies
        // scale=cw:ch:force_original_aspect_ratio=decrease + centered pad, so a clip whose aspect
        // differs from the canvas occupies only part of it. Multiplying normalized corners straight
        // by the canvas size places the insert far outside the plate — caught on a real
        // three-source compile where a 1080x1920 phone clip pillarboxed into 1920x1080 had its
        // insert drawn ~3x too wide and spilling across the whole frame.
        var canvas = new VideoAnalysisMedia(10, 30, 1, 1920, 1080);
        var portraitSource = new VideoAnalysisMedia(8, 30, 1, 1080, 1920);

        // 1080x1920 into 1920x1080: scale factor is bounded by height (1080/1920 = 0.5625), so the
        // picture is 607.5 wide, centered => x offset (1920 - 607.5)/2 = 656.25.
        VideoCompileStepExecutor.SourceCanvasFit fit =
            VideoCompileStepExecutor.SourceCanvasFit.For(portraitSource, canvas);

        fit.Width.Should().BeApproximately(607.5, 0.01);
        fit.Height.Should().BeApproximately(1080.0, 0.01);
        fit.OffsetX.Should().BeApproximately(656.25, 0.01);
        fit.OffsetY.Should().BeApproximately(0.0, 0.01);

        // A clip that already matches the canvas gets the identity fit, so a single-source compile
        // is byte-identical to before this mapping existed.
        VideoCompileStepExecutor.SourceCanvasFit same =
            VideoCompileStepExecutor.SourceCanvasFit.For(canvas, canvas);
        same.Should().Be(new VideoCompileStepExecutor.SourceCanvasFit(0, 0, 1920, 1080));

        // And the corners actually land inside the pillarboxed strip, not across the whole frame.
        VideoInsertRegionTrack track = BuildInsertTrackOnSource(1, "r0", "s1", 0.0, 4.0);
        List<VideoCompileStepExecutor.ResolvedSpan> spans = [new(0, 4, 0, 4, 0, 120, SourceIndex: 1)];
        OutputTimeline timeline = OutputTimeline.Build(
            spans, [new SeamPlan(0, SeamTreatment.HardCut, 0, 0, "", 0, "test")]);

        IReadOnlyList<InsertQuadFrame> mapped = VideoCompileStepExecutor.BuildInsertKeyframes(
            track, timeline, canvas, (0.0, 4.0), 96, overscan: 0.0, sourceFit: fit);
        IReadOnlyList<InsertQuadFrame> unmapped = VideoCompileStepExecutor.BuildInsertKeyframes(
            track, timeline, canvas, (0.0, 4.0), 96, overscan: 0.0);

        mapped.Should().NotBeEmpty();
        foreach (InsertQuadFrame f in mapped)
        {
            foreach (double x in new[] { f.X0, f.X1, f.X2, f.X3 })
                x.Should().BeInRange(fit.OffsetX, fit.OffsetX + fit.Width,
                    "every corner must sit inside the strip this clip actually occupies");
        }

        // The regression it guards: without the fit, corners land far left of the strip.
        unmapped[0].X0.Should().BeLessThan(fit.OffsetX,
            "this is exactly the wrong placement the mapping exists to prevent");
    }

    [Fact]
    public async Task Multi_source_insert_on_a_clip_that_was_cut_away_is_dropped_not_composited_at_the_wrong_time()
    {
        VideoAnalysisArtifact artifact = BuildTwoSourceArtifact(out string keyA, out string keyB);
        // The plate is tracked on source 1, but the edit keeps only source 0 — so there is no
        // output time belonging to clip B for the insert to live in.
        artifact = artifact with
        {
            InsertRegions = [BuildInsertTrackOnSource(sourceIndex: 1, "r0", "s1", 0.5, 2.5)],
            OfferedInsertRegionIds = ["r0"]
        };

        string decisionJson = BuildDecisionJson(("s0", "s0", "from clip A only"));

        Guid executionId = Guid.NewGuid();
        string assetKey = $"projects/{ProjectId}/outputFiles/{executionId:D}/screen.mp4";
        StepExecutionContext context = CreateCompileContextWithInsertPlan(
            artifact, decisionJson, BuildInsertsPlanJson(("r0", assetKey)), keyA, keyB,
            executionId, [assetKey], out Mock<IProjectFileWorkspace> workspace);

        string? capturedFilterComplex = null;
        JsonElement edl = default;
        VideoCompileStepExecutor executor = CreateCompileExecutor(workspace, args =>
        {
            List<string> a = args.ToList();
            int idx = a.IndexOf("-filter_complex_script");
            if (idx >= 0) capturedFilterComplex = File.ReadAllText(a[idx + 1]);
            else
            {
                int inline = a.IndexOf("-filter_complex");
                if (inline >= 0) capturedFilterComplex = a[inline + 1];
            }
        });
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Returns<Guid, string, string, string, CancellationToken, string>((_, local, _, _, _, _) =>
            {
                if (local.EndsWith("edl.json", StringComparison.Ordinal))
                    edl = JsonDocument.Parse(File.ReadAllText(local)).RootElement.Clone();
                return Task.FromResult("projects/p/agentFiles/video-analysis/e/step-4-edl.json");
            });
        workspace.SetupRegisteredRenderPublish();

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        capturedFilterComplex.Should().NotBeNull();
        capturedFilterComplex.Should().NotContain("perspective=",
            "the tracked clip contributed no output time, so there is nowhere for the insert to go");

        edl.ValueKind.Should().Be(JsonValueKind.Object);
        JsonElement inserts = edl.GetProperty("inserts");
        inserts.GetProperty("applied").GetBoolean().Should().BeFalse();
        JsonElement dropped = inserts.GetProperty("droppedInserts");
        dropped.GetArrayLength().Should().Be(1);
        dropped[0].GetProperty("reason").GetString().Should().Be("cut_away");
    }

    // ---------------------------------------------------------------------
    // Sound from an inserted asset on the segmented (concat) encode — the single-source twins are
    // InsertAudioMixTests. docs/video-editing.md "Sound from an inserted asset".
    // ---------------------------------------------------------------------

    /// <summary>
    /// The concat-path argv and filtergraph of <see cref="RunTwoSourceInsertCompileAsync"/> with the
    /// flag off, captured from the executor BEFORE <c>MixInsertAudio</c> existed (commit 4c02c5c8c)
    /// and pinned verbatim, scratch paths reduced to their file names.
    /// </summary>
    private const string PreFeatureSegmentedArgv = "-nostdin\n-hide_banner\n-y\n-loglevel\nerror\n-progress\npipe:1\n-protocol_whitelist\nfile\n-format_whitelist\n" + UserMediaInputFormats.Whitelist + "\n-i\n<file:source-0.mp4>\n-protocol_whitelist\nfile\n-format_whitelist\n" + UserMediaInputFormats.Whitelist + "\n-i\n<file:source-1.mp4>\n-protocol_whitelist\nfile\n-format_whitelist\n" + UserMediaInputFormats.Whitelist + "\n-i\n<file:insert-asset-<guid>.mp4>\n-filter_complex_script\n<script>\n[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=4.2,trim=start=0:end=4.133333333333334,setpts=PTS-STARTPTS,scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30/1,tpad=stop_mode=clone:stop_duration=4.133333333333334,trim=end=4.133333333333334[v0];[0:a]atrim=start=0:end=4.133333333333334,asetpts=PTS-STARTPTS,aformat=sample_rates=48000:channel_layouts=stereo[a0];[1:v]fps=25/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=3.2,trim=start=0:end=3.12,setpts=PTS-STARTPTS,scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30/1,tpad=stop_mode=clone:stop_duration=3.12,trim=end=3.12[v1];[1:a]atrim=start=0:end=3.12,asetpts=PTS-STARTPTS,aformat=sample_rates=48000:channel_layouts=stereo[a1];[v0][a0][v1][a1]concat=n=2:v=1:a=1[vcat][aout];[2:v]scale=588:552:flags=bilinear,fps=30/1,format=yuv444p,pad=712:582:62:15:black,fillborders=left=62:right=62:top=15:bottom=15:mode=smear,perspective=x0='if(lt(in,61),-58.43356166814552+(in-1)*1.6,37.56643833185448)':y0='-12.906493345164101':x1='if(lt(in,61),652.9852138420586+(in-1)*1.6,748.9852138420586)':y1='13.7717107364685':x2='if(lt(in,61),-37.78521384205863+(in-1)*1.6,58.214786157941376)':y2='567.8282892635315':x3='if(lt(in,61),673.6335616681456+(in-1)*1.6000000000000019,769.6335616681457)':y3='594.5064933451641':sense=destination:eval=frame:interpolation=cubic,setpts=PTS-STARTPTS+4.633333333333334/TB[sic0];color=c=white:size=588x552:rate=30/1:duration=3,format=gray,pad=712:582:62:15:black,perspective=x0='if(lt(in,61),-58.43356166814552+(in-1)*1.6,37.56643833185448)':y0='-12.906493345164101':x1='if(lt(in,61),652.9852138420586+(in-1)*1.6,748.9852138420586)':y1='13.7717107364685':x2='if(lt(in,61),-37.78521384205863+(in-1)*1.6,58.214786157941376)':y2='567.8282892635315':x3='if(lt(in,61),673.6335616681456+(in-1)*1.6000000000000019,769.6335616681457)':y3='594.5064933451641':sense=destination:eval=frame,setpts=PTS-STARTPTS+4.633333333333334/TB[sim0];[sic0][sim0]alphamerge[sia0];[vcat][sia0]overlay=x=566:y=206:eof_action=pass:enable='between(t,4.633333333333334,6.633333333333334)'[vout]\n-map\n[vout]\n-map\n[aout]\n-c:v\nlibx264\n-crf\n20\n-preset\nveryfast\n-pix_fmt\nyuv420p\n-color_range\ntv\n-movflags\n+faststart\n-c:a\naac\n<file:edited.mp4>";

    [Fact]
    public async Task Multi_source_insert_compile_with_MixInsertAudio_off_is_byte_identical_to_before_the_feature()
    {
        (StepExecutionResult result, string argv) = await RunTwoSourceInsertCompileAsync(cfg => cfg);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        argv.Should().Be(PreFeatureSegmentedArgv);
    }

    /// <summary>
    /// The insert's own sound joins the SAME mix the sound-effects stage builds — one more cue in
    /// <c>sfx</c>, through <see cref="SfxMixFilterBuilder"/>'s branch and the existing
    /// <c>amix … duration=first:normalize=0</c> stage — on the segmented (concat) encode path too.
    /// The plate sits on clip B, whose output window is [4.633333, 6.633333) s: the sound starts
    /// where that picture starts and is TRIMMED to its 2 seconds (the content's own length is 10 s
    /// here), so it is heard only while the insert is on screen and never extends the film.
    /// </summary>
    [Fact]
    public async Task Multi_source_insert_compile_with_MixInsertAudio_on_mixes_the_inserts_sound_into_the_concat_mix()
    {
        (StepExecutionResult result, string argv) =
            await RunTwoSourceInsertCompileAsync(cfg => cfg with { MixInsertAudio = true });

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        using JsonDocument output = JsonDocument.Parse(result.Output);
        JsonElement audio = output.RootElement.GetProperty("inserts").GetProperty("audio");
        audio.GetProperty("requested").GetInt32().Should().Be(1);
        audio.GetProperty("mixed").GetInt32().Should().Be(1);
        audio.GetProperty("absent").GetInt32().Should().Be(0);
        JsonElement region = audio.GetProperty("regions")[0];
        region.GetProperty("regionId").GetString().Should().Be("r0");
        region.GetProperty("mixed").GetBoolean().Should().BeTrue();
        region.GetProperty("outputStartSec").GetDouble().Should().BeApproximately(4.633333333333334, 0.001);
        region.GetProperty("playSec").GetDouble().Should().BeApproximately(2.0, 0.001);

        string graph = SegmentedGraph(argv);
        // Picture and sound start together: the same moment the insert's picture is gated to.
        graph.Should().Contain("enable='between(t,4.633333333333334,6.633333333333334)'");
        graph.Should().Contain("adelay=4633|4633[sfx0]");
        // Trimmed to the window and played once (no -stream_loop anywhere in the argv).
        graph.Should().Contain("atrim=end=2,asetpts=N/SR/TB,aformat=sample_rates=48000:channel_layouts=stereo");
        graph.Should().Contain("amix=inputs=2:duration=first:dropout_transition=0:normalize=0");
        argv.Should().NotContain("-stream_loop");
        // The sound has its OWN -i, a second input on the same staged content file the picture
        // insert already reads (after every source and
        // voiceover input, exactly where a sound-effect clip's input sits).
        argv.Split('\n').Count(l => l.StartsWith("<file:insert-asset-", StringComparison.Ordinal)).Should().Be(2);
    }

    /// <summary>The filtergraph of a normalized argv — inline or read back from its script file.</summary>
    private static string SegmentedGraph(string normalizedArgv)
    {
        string[] lines = normalizedArgv.Split('\n');
        int i = Array.FindIndex(lines, l => l is "-filter_complex" or "<script>");
        i.Should().BeGreaterThan(-1, because: normalizedArgv);
        return lines[i + 1];
    }

    /// <summary>
    /// The two-clip compile of <see cref="Multi_source_compile_applies_a_tracked_screen_insert_gated_to_its_own_source_s_output_window"/>
    /// (plate on clip B, output window [4.633333, 6.633333) s), returning the encode's argv with
    /// every scratch path reduced to its file name and the filtergraph script inlined.
    /// </summary>
    private static async Task<(StepExecutionResult Result, string Argv)> RunTwoSourceInsertCompileAsync(
        Func<VideoCompileStepConfig, VideoCompileStepConfig> configOverride,
        Action<Mock<IMediaProbe>>? configureMediaProbe = null)
    {
        VideoAnalysisArtifact artifact = BuildTwoSourceArtifact(out string keyA, out string keyB);
        artifact = artifact with
        {
            InsertRegions = [BuildInsertTrackOnSource(sourceIndex: 1, "r0", "s1", 0.5, 2.5)],
            OfferedInsertRegionIds = ["r0"]
        };
        string decisionJson = BuildDecisionJson(("s0", "s0", "from clip A"), ("s1", "s1", "from clip B"));
        Guid executionId = Guid.Parse("6f1d2c3b-0000-4000-8000-000000000abc");
        string assetKey = $"projects/{ProjectId}/outputFiles/{executionId:D}/screen.mp4";
        StepExecutionContext context = CreateCompileContextWithInsertPlan(
            artifact, decisionJson, BuildInsertsPlanJson(("r0", assetKey)), keyA, keyB,
            executionId, [assetKey], out Mock<IProjectFileWorkspace> workspace, configOverride);

        string? argv = null;
        VideoCompileStepExecutor executor = CreateCompileExecutor(workspace, args =>
        {
            if (!args.Contains("-filter_complex") && !args.Contains("-filter_complex_script"))
                return;
            var parts = new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0 && args[i - 1] == "-filter_complex_script")
                {
                    parts.Add("<script>");
                    parts.Add(File.ReadAllText(args[i]));
                    continue;
                }

                parts.Add(Path.IsPathRooted(args[i])
                    ? $"<file:{System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(args[i]), "[0-9a-f]{32}", "<guid>")}>"
                    : args[i]);
            }

            argv = string.Join("\n", parts);
        }, configureMediaProbe: configureMediaProbe);

        StepExecutionResult result = await executor.ExecuteAsync(context);
        return (result, argv ?? string.Empty);
    }
}