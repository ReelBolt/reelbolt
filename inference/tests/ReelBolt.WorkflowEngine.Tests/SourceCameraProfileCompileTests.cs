#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
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
/// log-footage-color T-013: a REAL VideoCompile run (real ffmpeg) of a tiny synthetic clip. An explicit camera profile
/// changes the picture, tags the output BT.709 and records meta.inputProfiles; a compile without the field has neither
/// the meta key nor a warning (the output JSON is unchanged when unused).
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class SourceCameraProfileCompileTests
{
    private static readonly Guid ProjectId = Guid.Parse("88888888-8888-4888-8888-888888888888");
    private const string SourceKey = "projects/p/userFiles/log.mp4";

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [FfmpegFact]
    public async Task An_explicit_profile_converts_the_source_tags_the_output_and_is_reported_only_when_applied()
    {
        string root = Path.Combine(Path.GetTempPath(), "src-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "log.mp4");
            // A flat, low-contrast grey ramp, as untagged log footage looks.
            Ff("-f", "lavfi", "-i", "gradients=size=320x180:rate=30:duration=2:c0=0x6a6a6a:c1=0x8a8a8a:nb_colors=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "12", "-c:a", "aac", "-shortest", source);

            (JsonNode plain, string plainOut) = await CompileAsync(root, "plain", source, null);
            (JsonNode profiled, string profiledOut) = await CompileAsync(root, "profiled", source, new System.Collections.Generic.Dictionary<int, string> { [0] = "appleLog" });

            plain["meta"].Should().BeNull("a compile that names no profile keeps its exact output JSON");
            plain["warnings"].Should().BeNull();
            JsonArray applied = profiled["meta"]!["inputProfiles"]!.AsArray();
            applied.Should().ContainSingle();
            applied[0]!["sourceIndex"]!.GetValue<int>().Should().Be(0);
            applied[0]!["profile"]!.GetValue<string>().Should().Be("appleLog");
            applied[0]!["source"]!.GetValue<string>().Should().Be("explicit");

            Probe(profiledOut, "color_transfer").Should().Be("bt709");
            Probe(profiledOut, "color_space").Should().Be("bt709");
            Math.Abs(MeanLuma(plainOut) - MeanLuma(profiledOut)).Should().BeGreaterThan(3, "the profile table moves log mid-greys");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [FfmpegFact]
    public async Task An_hdr_source_ignores_the_profile_with_a_warning_and_records_nothing_applied()
    {
        string root = Path.Combine(Path.GetTempPath(), "src-profile-hdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "hlg.mp4");
            Ff("-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-vf", "setparams=colorspace=bt2020nc:color_primaries=bt2020:color_trc=arib-std-b67", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "20", "-c:a", "aac", "-shortest", source);

            (JsonNode output, _) = await CompileAsync(root, "hdr", source, new System.Collections.Generic.Dictionary<int, string> { [0] = "appleLog" });

            output["meta"].Should().BeNull("nothing was applied to an HDR source");
            output["warnings"]!.AsArray().Select(w => w!["code"]!.GetValue<string>()).Should().Contain("camera_profile_ignored_hdr");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private async Task<(JsonNode Output, string Path)> CompileAsync(string root, string name, string input, System.Collections.Generic.IReadOnlyDictionary<int, string>? profiles)
    {
        string analysisKey = $"projects/p/agentFiles/video-analysis/e/{name}-analysis.json";
        string work = Path.Combine(root, name);
        Directory.CreateDirectory(work);
        var options = Options.Create(new VideoEditingOptions { ScratchPath = Path.Combine(work, "scratch"), MaxConcurrentJobs = 1, AnalyzeTimeoutSeconds = 120, CompileTimeoutSeconds = 300 });
        var real = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
        var probe = new FfprobeMediaProbe(real, options, NullLogger<FfprobeMediaProbe>.Instance);
        VideoAnalysisArtifact analysis = new(
            Version: 1, Media: new VideoAnalysisMedia(2.0, 30, 1, 320, 180),
            Shots: [new VideoAnalysisShot("s0", 0, 2)], SilenceSpans: [], Segments: [], Words: [], OfferedIds: ["s0"],
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false), Placements: null, OfferedPlacementIds: null);
        var files = new Mock<IProjectFileWorkspace>();
        files.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, key, destination, _) =>
            {
                if (key == analysisKey) File.WriteAllText(destination, JsonSerializer.Serialize(analysis, JsonOptions()));
                else File.Copy(input, destination, overwrite: true);
                return Task.CompletedTask;
            });
        files.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((Guid pid, string path, string fileName, string _, CancellationToken _, string category) => "projects/" + pid + "/" + category + "/" + fileName);
        var recorder = new VideoCompileRealFfmpegWorkspaceTests.RecordingRunner(real, Path.Combine(work, "preserved"));
        VideoCompileStepConfig config = new(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2), AnalysisStepOrder: 1) { RegisterProjectFile = false, LoudnessTarget = ProgramLoudness.Off, SourceInputProfiles = profiles };
        string decision = JsonSerializer.Serialize(new { keep = new[] { new { fromId = "s0", toId = "s0", reason = "all" } }, editRationale = "whole", suggestedTitle = "T" },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var analyzeStep = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoAnalyze, VideoAnalyzeConfigJson = JsonSerializer.Serialize(new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput)), JsonOptions()) };
        var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile, VideoCompileConfigJson = JsonSerializer.Serialize(config, JsonOptions()) };
        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decision,
            StepOutputHistory =
            [
                new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceKey, ArtifactStorageKey: null),
                new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: analysisKey),
                new StepOutputHistoryEntry(2, "StoryEditor", decision, OutputStorageKey: null, ArtifactStorageKey: null)
            ],
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
        var executor = new VideoCompileStepExecutor(recorder, probe, files.Object, new Mock<IServiceScopeFactory>().Object, options, new CaptureLogger());
        StepExecutionResult result = await executor.ExecuteAsync(context);
        result.Status.Should().Be(StepStatus.Completed, because: (result.ErrorDetails ?? result.Output) + " " + CaptureLogger.Last);
        string produced = Path.Combine(work, "preserved", "edited.mp4");
        File.Exists(produced).Should().BeTrue();
        return (JsonNode.Parse(result.Output)!, produced);
    }

    private sealed class CaptureLogger : Microsoft.Extensions.Logging.ILogger<VideoCompileStepExecutor>
    {
        public static string Last = "";
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Last = exception.ToString();
        }
    }

    private static string Probe(string path, string entry) => Run("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", $"stream={entry}", "-of", "default=nw=1:nk=1", path).Trim();

    private static double MeanLuma(string path)
    {
        string text = Run("ffmpeg", "-hide_banner", "-i", path, "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-", "-f", "null", "-");
        var values = text.Split('\n').Where(l => l.Contains("YAVG=", StringComparison.Ordinal)).Select(l => double.Parse(l[(l.IndexOf('=') + 1)..].Trim(), CultureInfo.InvariantCulture)).ToList();
        values.Should().NotBeEmpty();
        return values.Average();
    }

    private static void Ff(params string[] args) => Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", .. args]);

    private static string Run(string exe, params string[] args)
    {
        ProcessStartInfo psi = new(exe) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, stderr);
        return stdout;
    }
}
