using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A StreamCopy compile through the REAL <see cref="VideoCompileStepExecutor"/> and real ffmpeg, on small synthetic sources with
/// B-frames (and an open-GOP x265 one). A cut of more than one span is re-encoded at the planned spans: the file is the planned
/// length, no two video packets print one time, and the real <see cref="FfmpegModerationMediaSampler"/> accepts it. A single
/// continuous cut stays a lossless copy: its bytes equal the cut-and-copy the executor always made. No-ops on a host without
/// ffmpeg/ffprobe (or without the encoder asked for).
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public sealed class VideoCompileStreamCopyRealFfmpegTests : IDisposable
{
    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";
    private const int ShotCount = 6;
    private const double ShotSec = 2.0;

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stream-copy-real-" + Guid.NewGuid().ToString("N"));

    public VideoCompileStreamCopyRealFfmpegTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(d => File.Exists(Path.Combine(d, tool)));

    private static (int Code, string Output) Run(string tool, IEnumerable<string> args)
    {
        ProcessStartInfo info = new(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) info.ArgumentList.Add(a);
        using Process p = Process.Start(info)!;
        Task<string> error = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + error.Result);
    }

    private static void RunOk(string tool, IEnumerable<string> args)
    {
        (int code, string output) = Run(tool, args);
        if (code != 0) throw new InvalidOperationException($"{tool} exited {code}: {output}");
    }

    private static bool Available(string encoder)
    {
        if (!OnPath("ffmpeg") || !OnPath("ffprobe")) return false;
        (int code, string output) = Run("ffmpeg", ["-hide_banner", "-encoders"]);
        return code == 0 && output.Contains(" " + encoder + " ", StringComparison.Ordinal);
    }

    private static int DuplicateVideoTimes(string path)
    {
        (_, string output) = Run("ffprobe", ["-v", "error", "-select_streams", "v", "-show_entries", "packet=pts_time", "-of", "csv=p=0", path]);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).GroupBy(l => l.Trim()).Count(g => g.Count() > 1);
    }

    private static string Probe(string path, string stream, string entries)
    {
        (_, string output) = Run("ffprobe", ["-v", "error", "-select_streams", stream, "-show_entries", entries, "-of", "csv=p=0", path]);
        return output.Trim();
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>A 12 s, 320x240, 30 fps source with B-frames and AAC, long GOPs so a cut starts mid-GOP.</summary>
    private string MakeSource(string codec, bool openGop, bool withMovText)
    {
        string path = Path.Combine(_dir, $"source-{codec}-{openGop}-{withMovText}.mp4");
        List<string> args = ["-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30:duration=12",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=12"];
        if (withMovText)
        {
            string srt = Path.Combine(_dir, "subs.srt");
            File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:03,000\nhello\n\n2\n00:00:05,000 --> 00:00:07,000\nthere\n");
            args.AddRange(["-i", srt]);
        }

        args.AddRange(codec == "libx265"
            ? ["-c:v", "libx265", "-x265-params", $"keyint=45:min-keyint=45:bframes=3:open-gop={(openGop ? 1 : 0)}:log-level=error"]
            : ["-c:v", "libx264", "-g", "45", "-bf", "3"]);
        args.AddRange(["-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"]);
        if (withMovText) args.AddRange(["-c:s", "mov_text"]);
        args.Add(path);
        RunOk("ffmpeg", args);
        return path;
    }

    private sealed record Compiled(StepExecutionResult Result, string Produced, List<IReadOnlyList<string>> Commands);

    /// <summary>Runs the real executor with Mode=StreamCopy over <paramref name="source"/>, keeping the given shots (each ShotSec long).</summary>
    private async Task<Compiled> CompileAsync(string source, int[] keepShots)
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        string root = Path.Combine(_dir, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<VideoEditingOptions> options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = Path.Combine(root, "scratch"),
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 120,
            CompileTimeoutSeconds = 300
        });
        FfmpegVideoToolRunner real = new(options, NullLogger<FfmpegVideoToolRunner>.Instance);

        VideoAnalysisArtifact artifact = new(
            Version: 1,
            Media: new VideoAnalysisMedia(ShotCount * ShotSec, 30, 1, 320, 240),
            Shots: Enumerable.Range(0, ShotCount).Select(i => new VideoAnalysisShot($"s{i}", i * ShotSec, (i + 1) * ShotSec)).ToList(),
            SilenceSpans: [], Segments: [], Words: [],
            OfferedIds: Enumerable.Range(0, ShotCount).Select(i => $"s{i}").ToList(),
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Placements: null, OfferedPlacementIds: null);
        string artifactJson = JsonSerializer.Serialize(artifact, JsonOptions());
        string decisionJson = JsonSerializer.Serialize(
            new { keep = keepShots.Select(i => new { fromId = $"s{i}", toId = $"s{i}", reason = "keep" }).ToArray(), editRationale = "t", suggestedTitle = "T" },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Mock<IProjectFileWorkspace> projectFiles = new();
        projectFiles.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, key, destination, _) =>
            {
                if (key == AnalysisKey) File.WriteAllText(destination, artifactJson);
                else File.Copy(source, destination, overwrite: true);
                return Task.CompletedTask;
            });
        projectFiles.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((Guid pid, string _, string fileName, string _, CancellationToken _, string category) => "projects/" + pid + "/" + category + "/" + fileName);

        string preserved = Path.Combine(root, "preserved");
        VideoCompileRealFfmpegWorkspaceTests.RecordingRunner recorder = new(real, preserved);
        FfprobeMediaProbe probe = new(real, options, NullLogger<FfprobeMediaProbe>.Instance);

        VideoCompileStepConfig config = new VideoCompileStepConfig(
            Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2), AnalysisStepOrder: 1) with
        {
            Mode = VideoCompileMode.StreamCopy,
            AllowKeyframeSnapping = true,
            RegisterProjectFile = false,
            LoudnessTarget = ProgramLoudness.Off
        };
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = JsonSerializer.Serialize(config, JsonOptions())
        };
        WorkflowStep analyze = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(
                new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput)), JsonOptions())
        };
        StepExecutionContext context = new()
        {
            Execution = new WorkflowExecution { Id = executionId, ProjectId = projectId },
            Step = step,
            AllSteps = [analyze, step],
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
        VideoCompileStepExecutor executor = new(recorder, probe, projectFiles.Object, new Mock<IServiceScopeFactory>().Object,
            options, NullLogger<VideoCompileStepExecutor>.Instance);

        StepExecutionResult result = await executor.ExecuteAsync(context);
        foreach (IReadOnlyList<string> c in recorder.Commands) _output.WriteLine("ARGV " + string.Join(' ', c.Select(x => x.Replace(root, "{root}"))));
        return new Compiled(result, Path.Combine(preserved, "edited.mp4"), recorder.Commands);
    }

    private static ContentModerationOptions ModerationOptions() => new()
    {
        SampleImageMaxLongSide = 512,
        VideoFrameIntervalSeconds = 2,
        VideoMaxFrames = 20
    };

    [Theory]
    [InlineData("libx264", false, new[] { 0, 2 })]
    [InlineData("libx264", false, new[] { 0, 2, 4 })]
    [InlineData("libx265", false, new[] { 0, 2, 4 })]
    [InlineData("libx265", true, new[] { 0, 2, 4 })]
    public async Task A_cut_of_more_than_one_span_is_re_encoded_at_the_planned_length_and_the_sampler_accepts_it(string codec, bool openGop, int[] keep)
    {
        if (!Available(codec)) return;
        string source = MakeSource(codec, openGop, withMovText: false);

        Compiled run = await CompileAsync(source, keep);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        List<string> encode = run.Commands.Last(a => a.Count > 0 && a[^1].EndsWith("edited.mp4", StringComparison.Ordinal)).ToList();
        encode.Should().Contain("-c:v", "a cut with more than one part is re-encoded");
        encode.Should().NotContain("concat").And.NotContain("-c", "nothing is copied or joined");
        run.Commands.SelectMany(c => c).Should().NotContain(a => a.EndsWith(".mkv", StringComparison.Ordinal));

        // The planned length is the sum of the spans the executor itself resolved (padded and frame-quantized), read off its select terms.
        string graph = encode[encode.IndexOf("-filter_complex") + 1];
        double planned = Regex.Matches(graph.Split("[vout]")[0], @"gte\(t,([0-9.]+)\)\*lt\(t,([0-9.]+)\)")
            .Sum(m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        Regex.Matches(graph.Split("[vout]")[0], "gte").Count.Should().Be(keep.Length, "one planned span per kept shot");
        // The picture's own length: the container's is the longest stream, and this check is about the cut, not the audio filter.
        double length = double.Parse(Probe(run.Produced, "v", "stream=duration"), CultureInfo.InvariantCulture);
        length.Should().BeApproximately(planned, 0.1, "frame-accurate at the planned spans");
        Probe(run.Produced, "a", "stream=codec_name").Should().Be("aac", "the source's audio is kept");
        DuplicateVideoTimes(run.Produced).Should().Be(0, "no two video packets print one time, so there is no packet pair");

        for (int seed = 1; seed <= 3; seed++)
        {
            ModerationMediaSample sample = await new FfmpegModerationMediaSampler(
                    new SystemProcessRunner(), ModerationOptions(), new Random(seed), FfmpegModerationMediaSampler.TotalDeadline)
                .SampleAsync(File.OpenRead(run.Produced), "video/mp4", CancellationToken.None);
            sample.Failure.Should().BeNull($"the sampler accepts the output (seed {seed})");
        }

        _output.WriteLine($"{codec} openGop={openGop} spans={keep.Length}: planned {planned:F2}s, file {length:F2}s (measured)");
    }

    [Fact]
    public async Task One_continuous_span_stays_a_lossless_copy_with_the_same_bytes_as_the_cut_and_copy_always_made()
    {
        if (!Available("libx264")) return;
        string source = MakeSource("libx264", false, withMovText: false);

        Compiled run = await CompileAsync(source, [1, 2]);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        run.Commands.Any(a => a.Contains("-c:v")).Should().BeFalse("one span is not re-encoded");
        run.Commands.Any(a => a.Contains("concat")).Should().BeTrue("the single piece still goes through the copy and the one-file concat it always did");

        // The two calls the executor made before this change, run by hand on the same bytes with the span it resolved.
        List<string> cut = run.Commands.First(c => c.Contains("-ss")).ToList();
        string from = cut[cut.IndexOf("-ss") + 1], to = cut[cut.IndexOf("-to") + 1];
        cut.Where(x => x == "-c").Should().HaveCount(1);
        string piece = Path.Combine(_dir, "expected-seg.mp4");
        RunOk("ffmpeg", ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-protocol_whitelist", "file",
            "-ss", from, "-to", to, "-i", source, "-c", "copy", piece]);
        string list = Path.Combine(_dir, "expected-concat.txt");
        File.WriteAllText(list, $"file '{piece}'\n");
        string expected = Path.Combine(_dir, "expected.mp4");
        RunOk("ffmpeg", ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-protocol_whitelist", "file,concat",
            "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", expected]);
        Sha(run.Produced).Should().Be(Sha(expected), "a single-span StreamCopy is byte-identical to before");
    }

    [Fact]
    public async Task A_source_with_a_mov_text_track_compiles_with_one_span_as_before_and_with_more_than_one()
    {
        if (!Available("libx264")) return;
        string source = MakeSource("libx264", false, withMovText: true);
        Probe(source, "s", "stream=codec_name").Should().Be("mov_text");

        Compiled one = await CompileAsync(source, [1, 2]);
        one.Result.Status.Should().Be(StepStatus.Completed, because: one.Result.ErrorDetails ?? one.Result.Output);

        Compiled many = await CompileAsync(source, [0, 2, 4]);
        many.Result.Status.Should().Be(StepStatus.Completed, because: many.Result.ErrorDetails ?? many.Result.Output);
        many.Commands.SelectMany(c => c).Should().NotContain(a => a.EndsWith(".mkv", StringComparison.Ordinal));
        DuplicateVideoTimes(many.Produced).Should().Be(0);
    }
}
