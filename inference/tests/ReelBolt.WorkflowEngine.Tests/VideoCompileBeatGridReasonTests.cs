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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The compile step's <c>beatSync.reason</c>: a beat measurement that FAILED is <c>beat_grid_failed:&lt;cause&gt;</c>,
/// never the plain <c>no_beat_grid</c> that means "decoded, no detectable beat".
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VideoCompileBeatGridReasonTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid MusicId = Guid.NewGuid();
    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";
    private const string MusicKey = "projects/p/userFiles/track.mp3";

    private static JsonSerializerOptions Json() => new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_failed_beat_measurement_is_reported_as_beat_grid_failed_with_its_cause()
    {
        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
        VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();

        var toolRunner = new Mock<IVideoToolRunner>();
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "... drawtext ...", string.Empty, false));
        // The decode "succeeds" but writes no WAV, so the read of it fails: a failed measurement, not a beatless track.
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        // The amix probe answers "normalize" (the static cache must never be left saying "unavailable" for a neighbour).
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => a.Contains("filter=amix")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "... normalize ...", string.Empty, false));
        var probe = new Mock<IMediaProbe>();
        probe
            .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", "aac", 48000));

        VideoAnalysisArtifact artifact = new(
            Version: 1, Media: new VideoAnalysisMedia(10.0, 30, 1, 1920, 1080),
            Shots: [new VideoAnalysisShot("s0", 0.0, 10.0)], SilenceSpans: [], Segments: [], Words: [], OfferedIds: ["s0"],
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false), Placements: null, OfferedPlacementIds: null);
        string artifactJson = JsonSerializer.Serialize(artifact, Json());
        string decisionJson = JsonSerializer.Serialize(
            new { keep = new[] { new { fromId = "s0", toId = "s0", reason = "keep" } }, editRationale = "t", suggestedTitle = "T" },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var files = new Mock<IProjectFileWorkspace>();
        files.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, key, dest, _) =>
            {
                File.WriteAllText(dest, key == AnalysisKey ? artifactJson : "x");
                return Task.CompletedTask;
            });
        files.Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProjectWorkspaceFile(MusicId, ProjectId, "track.mp3", null, "userFiles", MusicKey, "audio/mpeg", 4096, DateTime.UnixEpoch, null)]);
        files.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((Guid pid, string _, string name, string _, CancellationToken _, string category) => "projects/" + pid + "/" + category + "/" + name);
        files.Setup(w => w.UploadBinaryFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid pid, string _, string name, string mime, SummaryStatus _, FileIndexingStatus _, CancellationToken _, string category, string? _) =>
                new ProjectWorkspaceFile(Guid.NewGuid(), pid, name, null, category, "projects/" + pid + "/" + category + "/" + Guid.NewGuid().ToString("D") + ".mp4", mime, 1024, DateTime.UtcNow, null));
        files.SetupRegisteredRenderPublish();

        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = Path.Combine(Path.GetTempPath(), "compile-beat-reason-tests", Guid.NewGuid().ToString("N")),
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 30,
            CompileTimeoutSeconds = 30
        });
        VideoCompileStepConfig config = new VideoCompileStepConfig(
            Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2), AnalysisStepOrder: 1) with
        {
            Mode = VideoCompileMode.Reencode,
            LoudnessTarget = ProgramLoudness.Off,
            EnableMusic = true,
            MusicTrackProjectFileId = MusicId,
            BeatSync = VideoBeatSync.Beat
        };
        var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile, VideoCompileConfigJson = JsonSerializer.Serialize(config, Json()) };
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput)), Json())
        };
        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory =
            [
                new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
                new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
                new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null)
            ],
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };

        var executor = new VideoCompileStepExecutor(
            toolRunner.Object, probe.Object, files.Object, new Mock<IServiceScopeFactory>().Object, options,
            NullLogger<VideoCompileStepExecutor>.Instance);
        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject beatSync = JsonNode.Parse(result.Output)!["beatSync"]!.AsObject();
        beatSync["applied"]!.GetValue<bool>().Should().BeFalse();
        beatSync["reason"]!.GetValue<string>().Should().StartWith("beat_grid_failed:").And.NotBe("no_beat_grid");

        // The same loss reaches the customer's warnings[] with the stable code and the cause token.
        JsonArray warnings = JsonNode.Parse(result.Output)!["warnings"]!.AsArray();
        JsonObject beatWarning = warnings.Select(w => w!.AsObject()).Single(w => w["code"]!.GetValue<string>() == "beat_grid_unavailable");
        beatSync["reason"]!.GetValue<string>().Should().EndWith(beatWarning["cause"]!.GetValue<string>());

        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
        VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();
    }
}
