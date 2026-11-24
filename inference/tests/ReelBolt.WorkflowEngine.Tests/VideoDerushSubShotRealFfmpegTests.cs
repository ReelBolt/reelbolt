using System;
using System.Collections.Generic;
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
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The footage-derush fix end to end on REAL ffmpeg: three silent portrait clips of 10 s each are
/// analysed by <see cref="VideoAnalyzeStepExecutor"/> (real probe, real shot detector, real silence
/// detector, which skips a clip with no audio stream), so a long single shot is split into parts
/// only when <c>MaxShotSec</c> is set. The story editor's decision is scripted, and the real
/// <see cref="VideoCompileStepExecutor"/> cuts and encodes it with <c>TransitionPolicy.Auto</c>.
///
/// <para>
/// Storage keys are built the way production builds them (real Guid ids interpolated into the
/// path), never written as literals.
/// </para>
///
/// <para>No-ops through <see cref="FfmpegToolsFactAttribute"/> on a host without ffmpeg/ffprobe.</para>
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VideoDerushSubShotRealFfmpegTests
{
    private const int ClipCount = 3;
    private const double ClipSec = 10.0;
    private const int Fps = 30;

    /// <summary>
    /// A 10 s portrait clip: a 30 px white box is overlaid on black and sweeps the width at a steady pace
    /// (about 52 px/s, a turn every 2.67 s of movement) and stops for exactly 1 s at t=2.5..3.5 and
    /// t=6.5..7.5, so the pause centres are 3.0 and 7.0. The sweep runs on the clock with the holds removed:
    /// tau = t - hold(t, 2.5) - hold(t, 6.5). Overlay, not drawbox: only overlay re-evaluates x per frame.
    /// </summary>
    private const string PauseGraph =
        "color=c=black:s=180x340:r=30:d=10[bg];color=c=white:s=30x30:r=30:d=10[bx];" +
        "[bg][bx]overlay=x='5+140*(1-abs(mod((t-min(max(t-2.5,0),1)-min(max(t-6.5,0),1))/2.6667,2)-1))':y=155";

    /// <summary>A solid, still 10 s portrait clip: no motion and no layout change anywhere.</summary>
    private const string StaticGraph = "color=c=0x808080:s=180x340:r=30:d=10";

    private static string Testsrc() => $"testsrc=size=180x340:rate={Fps}:duration={ClipSec.ToString(CultureInfo.InvariantCulture)}";

    [FfmpegToolsFact]
    public async Task Whole_silent_clips_without_MaxShotSec_compile_to_about_thirty_seconds_by_ffprobe()
    {
        using Harness harness = await Harness.CreateAsync();

        JsonNode view = await harness.AnalyzeAsync(maxShotSec: 0);
        view["shots"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()).Should().Equal("s0", "s1", "s2");
        view["shots"]!.AsArray().Should().AllSatisfy(s => s!.AsObject().ContainsKey("part").Should().BeFalse());

        Harness.Output output = await harness.CompileAsync(Harness.Decision("s0", "s1", "s2"));
        Console.WriteLine($"BEFORE whole clips (maxShotSec 0): ids=3 ffprobe_duration={output.DurationSec:F3}s frames={output.Frames}");

        output.DurationSec.Should().BeApproximately(30.0, 1.0, "three whole 10 s clips");
    }

    [FfmpegToolsFact]
    public async Task Two_parts_of_each_silent_ten_second_clip_compile_to_about_fifteen_seconds_with_no_dropped_or_duplicated_frame()
    {
        using Harness harness = await Harness.CreateAsync();

        JsonNode view = await harness.AnalyzeAsync(maxShotSec: 3);
        JsonArray shots = view["shots"]!.AsArray();
        shots.Select(s => s!["id"]!.GetValue<string>()).Should().HaveCount(12);

        // Source 0 holds s0..s3, source 1 s4..s7, source 2 s8..s11: keep the first two parts of each,
        // as six separate spans, so each pair of adjacent parts meets at a seam.
        string[] kept = ["s0", "s1", "s4", "s5", "s8", "s9"];
        double expectedFrames = kept
            .Select(id => shots.Single(s => s!["id"]!.GetValue<string>() == id)!)
            .Sum(s => Math.Round((s["endSec"]!.GetValue<double>() - s["startSec"]!.GetValue<double>()) * Fps));

        Harness.Output output = await harness.CompileAsync(Harness.Decision(kept));
        Console.WriteLine($"AFTER 6 parts (maxShotSec 3): ids=12 ffprobe_duration={output.DurationSec:F3}s " +
                          $"frames={output.Frames} expected_from_spans={expectedFrames}");

        output.DurationSec.Should().BeApproximately(15.0, 2.0, "six 2.5 s parts");
        output.Frames.Should().Be((int)expectedFrames,
            "every kept frame is written once: a seam that drops or duplicates a frame changes this count");
    }

    [FfmpegToolsFact]
    public async Task Content_mode_cuts_a_moving_clip_at_the_centres_of_its_two_motion_pauses_within_a_tenth_of_a_second()
    {
        using Harness harness = await Harness.CreateFromLavfiAsync(PauseGraph);

        Harness.Analysis analysis = await harness.RunAnalyzeAsync(maxShotSec: 0, VideoSubShotMode.Content);
        List<(double Sec, string Reason)> cuts = ContentCuts(analysis, src: 0);
        Console.WriteLine("CONTENT pause clip (holds at 3.0 and 7.0): " + string.Join(", ", cuts.Select(c => $"{c.Sec:F3}s {c.Reason}")));

        cuts.Should().Contain(c => Math.Abs(c.Sec - 3.0) <= 0.1 && c.Reason == "motion_pause",
            "the first 1 s hold is centred at 3.0 s, so a motion-pause cut must land within 0.1 s of its centre");
        cuts.Should().Contain(c => Math.Abs(c.Sec - 7.0) <= 0.1 && c.Reason == "motion_pause",
            "the second 1 s hold is centred at 7.0 s, so a motion-pause cut must land within 0.1 s of its centre");
    }

    [FfmpegToolsFact]
    public async Task Content_mode_on_a_static_clip_falls_back_to_even_and_records_even_fallback_in_meta()
    {
        using Harness harness = await Harness.CreateFromLavfiAsync(StaticGraph);

        Harness.Analysis analysis = await harness.RunAnalyzeAsync(maxShotSec: 0, VideoSubShotMode.Content);

        JsonObject split = SplitOf(analysis, src: 0);
        split["method"]!.GetValue<string>().Should().Be("even_fallback", "a still frame has no content cut to offer");
        split["parts"]!.GetValue<int>().Should().Be(3, "a 10 s shot at a 4 s maximum");
        split["cuts"]!.AsArray().Select(c => c!.GetValue<string>()).Should().OnlyContain(r => r == "even");
        analysis.Meta["subShots"]!["evenFallback"]!.GetValue<int>().Should().Be(1);
        analysis.Meta["subShots"]!["content"]!.GetValue<int>().Should().Be(0);
        PartStarts(analysis, src: 0).Should().HaveCount(3);
    }

    [FfmpegToolsFact]
    public async Task Content_mode_parts_keep_adjacent_and_non_adjacent_spans_with_no_dropped_or_duplicated_frame()
    {
        using Harness harness = await Harness.CreateFromLavfiAsync(PauseGraph, PauseGraph);

        Harness.Analysis analysis = await harness.RunAnalyzeAsync(maxShotSec: 0, VideoSubShotMode.Content);
        JsonArray shots = analysis.View["shots"]!.AsArray();
        string[] first = IdsOf(shots, src: 0), second = IdsOf(shots, src: 1);
        first.Length.Should().BeGreaterThanOrEqualTo(3, "the pause clip is cut at both holds, so it has three parts");
        second.Length.Should().BeGreaterThanOrEqualTo(3);

        // Source 0 keeps parts 0 and 1, which meet at a content cut (adjacent). Source 1 keeps parts 0 and 2,
        // with part 1 dropped between them (non-adjacent).
        string[] kept = [first[0], first[1], second[0], second[2]];
        double expectedFrames = ExpectedFrames(shots, kept);

        Harness.Output output = await harness.CompileAsync(Harness.Decision(kept));
        Console.WriteLine($"SEAM content parts: kept={string.Join(",", kept)} ffprobe_frames={output.Frames} expected_from_spans={expectedFrames}");

        output.Frames.Should().Be((int)expectedFrames,
            "every kept frame of every content part is written once: a seam that drops or duplicates a frame changes this count");
    }

    /// <summary>The content cut points of one source: each boundary's time (the part start) with the reason meta gives for it.</summary>
    private static List<(double Sec, string Reason)> ContentCuts(Harness.Analysis analysis, int src)
    {
        string[] reasons = SplitOf(analysis, src)["cuts"]!.AsArray().Select(c => c!.GetValue<string>()).ToArray();
        List<double> starts = PartStarts(analysis, src);
        starts.Should().HaveCount(reasons.Length + 1, "one part start per cut, plus the shot's own start");
        return starts.Skip(1).Zip(reasons, (sec, reason) => (sec, reason)).ToList();
    }

    /// <summary>The single split shot meta records for a source.</summary>
    private static JsonObject SplitOf(Harness.Analysis analysis, int src)
    {
        List<JsonNode?> splits = analysis.Meta["subShots"]!["shots"]!.AsArray()
            .Where(s => (s!["src"]?.GetValue<int>() ?? 0) == src).ToList();
        splits.Should().ContainSingle("one split shot is expected in source " + src);
        return splits[0]!.AsObject();
    }

    private static List<double> PartStarts(Harness.Analysis analysis, int src) =>
        analysis.View["shots"]!.AsArray()
            .Where(s => (s!["src"]?.GetValue<int>() ?? 0) == src)
            .Select(s => s!["startSec"]!.GetValue<double>()).ToList();

    private static string[] IdsOf(JsonArray shots, int src) =>
        shots.Where(s => (s!["src"]?.GetValue<int>() ?? 0) == src).Select(s => s!["id"]!.GetValue<string>()).ToArray();

    /// <summary>
    /// The frames the kept parts OWN, ideally: every part boundary is a source frame edge, so a part
    /// [start, end) owns exactly <c>round(end * fps) - round(start * fps)</c> frames, adjacent parts share
    /// their edge, and the sum over the kept parts is what a seam that neither drops nor repeats a frame
    /// writes. Each boundary is also asserted to be on a frame edge, by the compile's own floor (start)
    /// and ceiling (end).
    /// </summary>
    private static double ExpectedFrames(JsonArray shots, IReadOnlyList<string> kept)
    {
        double owned = 0;
        foreach (string id in kept)
        {
            JsonNode span = shots.Single(s => s!["id"]!.GetValue<string>() == id)!;
            double start = span["startSec"]!.GetValue<double>(), end = span["endSec"]!.GetValue<double>();
            Math.Floor(start * Fps).Should().Be(Math.Round(start * Fps), $"{id} starts on a frame edge");
            Math.Ceiling(end * Fps).Should().Be(Math.Round(end * Fps), $"{id} ends on a frame edge");
            owned += Math.Round(end * Fps) - Math.Round(start * Fps);
        }

        return owned;
    }

    /// <summary>One scratch workspace, the generated clips, and the real executors wired to them.</summary>
    internal sealed class Harness : IDisposable
    {
        private readonly string _root;
        private readonly string _preserved;
        private readonly string _analysisCopy;
        private readonly IVideoToolRunner _real;
        private readonly IOptions<VideoEditingOptions> _options;
        private readonly Guid _projectId = Guid.NewGuid();
        private readonly Guid _analyzeExecutionId = Guid.NewGuid();
        private readonly Guid[] _fileIds;
        private readonly Dictionary<string, string> _clipPathByKey = new(StringComparer.Ordinal);

        /// <summary>The view and the step meta of one VideoAnalyze run.</summary>
        public sealed record Analysis(JsonNode View, JsonNode Meta);

        private Harness(string root, int clipCount)
        {
            _root = root;
            _fileIds = Enumerable.Range(0, clipCount).Select(_ => Guid.NewGuid()).ToArray();
            _preserved = Path.Combine(root, "preserved");
            _analysisCopy = Path.Combine(root, "analysis.json");
            _options = Options.Create(new VideoEditingOptions
            {
                ScratchPath = Path.Combine(root, "scratch"),
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 120,
                CompileTimeoutSeconds = 300
            });
            _real = new FfmpegVideoToolRunner(_options, NullLogger<FfmpegVideoToolRunner>.Instance);
        }

        /// <summary>The analysis artifact key, built as VideoAnalyzeStepExecutor builds it (step order 1).</summary>
        private string AnalysisKey => $"projects/{_projectId:D}/agentFiles/video-analysis/{_analyzeExecutionId:D}/step-1-analysis.json";

        /// <summary>A project file's storage key, built from its real Guid id.</summary>
        private string ClipKey(int i) => $"projects/{_projectId:D}/userFiles/{_fileIds[i]:D}.mp4";

        /// <summary>Three generated testsrc clips (the T-001 fixture).</summary>
        public static Task<Harness> CreateAsync() => CreateFromLavfiAsync(Enumerable.Repeat(Testsrc(), ClipCount).ToArray());

        /// <summary>One generated silent portrait clip per lavfi graph, in order.</summary>
        public static async Task<Harness> CreateFromLavfiAsync(params string[] lavfiGraphs)
        {
            VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
            VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();

            string root = Path.Combine(Path.GetTempPath(), "video-derush-subshot-real", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var harness = new Harness(root, lavfiGraphs.Length);
            for (int i = 0; i < lavfiGraphs.Length; i++)
            {
                string path = Path.Combine(root, $"clip{i}.mp4");
                // Silent (-an): no audio stream, so no silence spans and no transcript.
                VideoToolResult made = await harness._real.RunFfmpegAsync(
                    [
                        "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
                        "-f", "lavfi", "-i", lavfiGraphs[i],
                        "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "veryfast", "-crf", "28",
                        path
                    ],
                    TimeSpan.FromMinutes(2), CancellationToken.None);
                made.Succeeded.Should().BeTrue("the fixture is built with the host's own ffmpeg: " + made.StdErr);
                harness._clipPathByKey[harness.ClipKey(i)] = path;
            }

            return harness;
        }

        /// <summary>Real clips already on disk (the evidence run), read in place: no copy, no generated fixture.</summary>
        public static Task<Harness> FromFilesAsync(IReadOnlyList<string> paths)
        {
            var harness = new Harness(Path.Combine(Path.GetTempPath(), "video-derush-subshot-real", Guid.NewGuid().ToString("N")), paths.Count);
            Directory.CreateDirectory(harness._root);
            for (int i = 0; i < paths.Count; i++)
                harness._clipPathByKey[harness.ClipKey(i)] = paths[i];
            return Task.FromResult(harness);
        }

        /// <summary>Runs the real analyze step over the clips and returns its view.</summary>
        public async Task<JsonNode> AnalyzeAsync(double maxShotSec, VideoSubShotMode subShotMode = VideoSubShotMode.Off) =>
            (await RunAnalyzeAsync(maxShotSec, subShotMode)).View;

        /// <summary>Runs the real analyze step over the clips and returns its view and its step meta.</summary>
        public async Task<Analysis> RunAnalyzeAsync(double maxShotSec, VideoSubShotMode subShotMode = VideoSubShotMode.Off)
        {
            var workspace = new Mock<IProjectFileWorkspace>();
            workspace.Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Enumerable.Range(0, _fileIds.Length)
                    .Select(i => new ProjectWorkspaceFile(_fileIds[i], _projectId, $"clip{i}.mp4", null, "userFiles", ClipKey(i), "video/mp4", 3, DateTime.UnixEpoch, null))
                    .ToList());
            workspace.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, key, dest, _) =>
                {
                    File.Copy(_clipPathByKey[key], dest, overwrite: true);
                    return Task.CompletedTask;
                });
            workspace.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid _, string local, string name, string _, CancellationToken _, string _) =>
                {
                    if (!name.EndsWith("analysis.json", StringComparison.Ordinal))
                        return $"projects/{_projectId:D}/agentFiles/{name}";
                    File.Copy(local, _analysisCopy, overwrite: true);
                    return AnalysisKey;
                });

            var executor = new VideoAnalyzeStepExecutor(
                new FfprobeMediaProbe(_real, _options, NullLogger<FfprobeMediaProbe>.Instance),
                new FfmpegSilenceDetector(_real, _options, NullLogger<FfmpegSilenceDetector>.Instance),
                new FfmpegShotDetector(_real, _options, NullLogger<FfmpegShotDetector>.Instance),
                new Mock<IAudioExtractor>().Object,
                new FfmpegFrameGridSampler(_real, _options, NullLogger<FfmpegFrameGridSampler>.Instance),
                new Mock<IKeyframeExtractor>().Object,
                new Mock<ISharpnessSampler>().Object,
                new Mock<IShotCaptioner>().Object,
                workspace.Object,
                new Mock<ITranscriptionClientFactory>().Object,
                new Mock<IInferenceProviderResolver>().Object,
                _options,
                NullLogger<VideoAnalyzeStepExecutor>.Instance,
                videoToolRunner: _real);

            var config = new VideoAnalyzeStepConfig(
                Version: 1,
                Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: _fileIds[0]),
                Transcription: VideoTranscriptionMode.Off,
                Sources: _fileIds.Select(id => new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: id)).ToList()) with
            {
                MaxShotSec = maxShotSec,
                SubShotMode = subShotMode,
                AnalyzeVisuals = false
            };
            var analyzeStep = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 1,
                StepType = StepType.VideoAnalyze,
                VideoAnalyzeConfigJson = JsonSerializer.Serialize(config, JsonOptions())
            };

            StepExecutionResult result = await executor.ExecuteAsync(new StepExecutionContext
            {
                Execution = new WorkflowExecution { Id = _analyzeExecutionId, ProjectId = _projectId },
                Step = analyzeStep,
                AllSteps = [analyzeStep],
                AccumulatedOutput = string.Empty,
                StepOutputHistory = [],
                CurrentStepIndex = 0,
                IterationCount = 0,
                CorrelationId = "test",
                CancellationToken = CancellationToken.None
            });
            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
            JsonNode envelope = JsonNode.Parse(result.Output)!;
            return new Analysis(envelope["view"]!, envelope["meta"]!);
        }

        /// <summary>Runs the real compile step over the analysed artifact with a scripted decision.</summary>
        public async Task<Output> CompileAsync(string decisionJson)
        {
            var projectFiles = new Mock<IProjectFileWorkspace>();
            projectFiles.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, key, dest, _) =>
                {
                    File.Copy(key == AnalysisKey ? _analysisCopy : _clipPathByKey[key], dest, overwrite: true);
                    return Task.CompletedTask;
                });
            projectFiles.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid pid, string _, string fileName, string _, CancellationToken _, string category) =>
                    $"projects/{pid:D}/{category}/{fileName}");

            var config = new VideoCompileStepConfig(
                Version: 1,
                Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
                AnalysisStepOrder: 1) with
            {
                Mode = VideoCompileMode.Reencode,
                RegisterProjectFile = false,
                LoudnessTarget = ProgramLoudness.Off,
                TransitionPolicy = VideoTransitionPolicy.Auto,
                // Default padding (80 ms before, 120 ms after) keeps a few frames of the next shot at
                // each run's end. Zero here, so the output frame count is compared with the kept spans alone.
                PrePaddingMs = 0,
                PostPaddingMs = 0
            };
            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 3,
                StepType = StepType.VideoCompile,
                VideoCompileConfigJson = JsonSerializer.Serialize(config, JsonOptions())
            };
            var analyzeStep = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 1,
                StepType = StepType.VideoAnalyze,
                VideoAnalyzeConfigJson = JsonSerializer.Serialize(
                    new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput)),
                    JsonOptions())
            };

            var preserving = new PreservingRunner(_real, _preserved);
            var probe = new FfprobeMediaProbe(_real, _options, NullLogger<FfprobeMediaProbe>.Instance);
            var executor = new VideoCompileStepExecutor(
                preserving, probe, projectFiles.Object, new Mock<IServiceScopeFactory>().Object,
                _options, NullLogger<VideoCompileStepExecutor>.Instance);

            StepExecutionResult result = await executor.ExecuteAsync(new StepExecutionContext
            {
                Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = _projectId },
                Step = step,
                AllSteps = [analyzeStep, step],
                AccumulatedOutput = decisionJson,
                StepOutputHistory =
                [
                    new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
                    new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null)
                ],
                CurrentStepIndex = 2,
                IterationCount = 0,
                CorrelationId = "test",
                CancellationToken = CancellationToken.None
            });
            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

            string produced = Path.Combine(_preserved, "edited.mp4");
            File.Exists(produced).Should().BeTrue("the encode writes the program");
            MediaProbeResult probed = await probe.ProbeAsync(produced, CancellationToken.None);
            return new Output(probed.DurationSec, await FrameCountAsync(produced));
        }

        /// <summary>A story-editor decision keeping the given ids, in order, one span each.</summary>
        public static string Decision(params string[] ids) => JsonSerializer.Serialize(new
        {
            keep = ids.Select(id => new { fromId = id, toId = id, reason = "keep" }).ToArray(),
            editRationale = "test",
            suggestedTitle = "Test"
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        private async Task<int> FrameCountAsync(string path)
        {
            VideoToolResult counted = await _real.RunFfprobeAsync(
                [
                    "-v", "error", "-count_frames", "-select_streams", "v:0",
                    "-show_entries", "stream=nb_read_frames", "-of", "default=nokey=1:noprint_wrappers=1",
                    path
                ],
                TimeSpan.FromMinutes(2), CancellationToken.None);
            counted.Succeeded.Should().BeTrue(counted.StdErr);
            return int.Parse(counted.StdOut.Trim(), CultureInfo.InvariantCulture);
        }

        private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };

        public void Dispose()
        {
            VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
            VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a temp directory the OS still holds open is not a test failure.
            }
        }

        public sealed record Output(double DurationSec, int Frames);
    }

    /// <summary>Forwards every call to the real runner and keeps each successfully written edited.mp4.</summary>
    private sealed class PreservingRunner(IVideoToolRunner inner, string preservedDirectory) : IVideoToolRunner
    {
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
            => Keep(args, inner.RunFfmpegAsync(args, timeout, ct));

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
            => Keep(args, inner.RunFfmpegAsync(args, timeout, ct, onStdOutLine));

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
            => Keep(args, inner.RunFfmpegAsync(args, timeout, ct, priority));

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority)
            => Keep(args, inner.RunFfmpegAsync(args, timeout, ct, onStdOutLine, priority));

        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
            => Keep(args, inner.RunFfmpegAsync(workspace, args, timeout, ct));

        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
            => Keep(args, inner.RunFfmpegAsync(workspace, args, timeout, ct, onStdOutLine));

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
            => inner.RunFfprobeAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
            => inner.RunFfprobeAsync(args, timeout, ct, priority);

        private async Task<VideoToolResult> Keep(IReadOnlyList<string> args, Task<VideoToolResult> call)
        {
            VideoToolResult result = await call;
            if (result.Succeeded && args.Count > 0 && Path.GetFileName(args[^1]) == "edited.mp4" && File.Exists(args[^1]))
            {
                Directory.CreateDirectory(preservedDirectory);
                File.Copy(args[^1], Path.Combine(preservedDirectory, "edited.mp4"), overwrite: true);
            }

            return result;
        }
    }
}
