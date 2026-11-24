#nullable enable
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
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// "Trimming inside a shot": the author can use only part of a source (<c>VideoSourceRef.InSec</c>/
/// <c>OutSec</c>), and the analyze step can split a long shot into sub-shots
/// (<c>MaxShotSec</c>) so the editor keeps part of it by id.
/// </summary>
public class VideoAnalyzeTrimAndSplitTests
{
    private const string SourceKey = "projects/p/userFiles/take.mp4";
    private const string WindowKey = "projects/p/agentFiles/video-analysis/e/step-1-src0-window.mp4";

    // ---- ShotSplitter (pure) -------------------------------------------------------------------

    [Fact]
    public void A_shot_within_the_limit_is_left_alone_and_a_long_one_splits_evenly_without_any_hint()
    {
        (List<(double StartSec, double EndSec)> spans, List<string?> parts) =
            ShotSplitter.Split([(0, 3), (3, 12)], maxShotSec: 4, motion: null, silences: []);

        spans.Should().Equal((0.0, 3.0), (3.0, 6.0), (6.0, 9.0), (9.0, 12.0));
        parts.Should().Equal(null, "1/3", "2/3", "3/3");
    }

    [Fact]
    public void A_split_prefers_the_stillest_moment_then_a_pause_near_the_even_point()
    {
        // 0..10 s at limit 5: one split, even point 5, searched within 3.5..6.5.
        var motion = new ShotSplitter.MotionCurve(
            [3.0, 4.0, 4.5, 5.0, 5.5, 6.0, 7.0],
            [0.0, 0.3, 0.3, 0.3, 0.3, 0.02, 0.0]);
        ShotSplitter.Split([(0, 10)], 5, motion, []).Spans.Should().Equal((0.0, 6.0), (6.0, 10.0));

        ShotSplitter.Split([(0, 10)], 5, motion: null, silences: [(4.0, 4.4), (8.0, 9.0)]).Spans
            .Should().Equal((0.0, 4.2), (4.2, 10.0)); // the pause inside the search window, not the one outside it
    }

    [Fact]
    public void Motion_is_measured_between_consecutive_grid_frames()
    {
        // Six 2x1 frames at 2 fps: three identical, a full change, three identical again.
        byte[] F(byte v) => [v, v, v, v, v, v];
        var grid = new FrameGridResult([.. F(10), .. F(10), .. F(10), .. F(250), .. F(250), .. F(250)], 2, 1, 2.0, 6);

        ShotSplitter.MotionCurve curve = ShotSplitter.MotionCurve.FromGrid(grid);

        curve.Count.Should().Be(5);
        curve.StillestIn(0, 0.3).Should().Be(0.25);
        curve.StillestIn(0.5, 2.5).Should().Be(2.25, "away from the change between frames 2 and 3, where it is still");
    }

    // ---- Analyze executor ---------------------------------------------------------------------

    [Fact]
    public async Task A_long_shot_becomes_sub_shots_the_editor_can_keep_by_id()
    {
        var run = new Run { Shots = [(0.0, 10.0)], Silences = [(3.3, 3.7)] };
        StepExecutionResult result = await run.ExecuteAsync(c => c with { MaxShotSec = 4, AnalyzeVisuals = false });

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonArray shots = JsonNode.Parse(result.Output)!["view"]!["shots"]!.AsArray();
        shots.Select(s => s!["id"]!.GetValue<string>()).Should().Equal("s0", "s1", "s2");
        shots.Select(s => s!["part"]!.GetValue<string>()).Should().Equal("1/3", "2/3", "3/3");
        shots[0]!["endSec"]!.GetValue<double>().Should().Be(3.5, "the first split moved onto the pause");
        run.Artifact!["shots"]![1]!["part"]!.GetValue<string>().Should().Be("2/3");
    }

    [Fact]
    public async Task A_step_with_no_segmentation_target_carries_no_warnings_and_no_segmentation_meta()
    {
        // The additive property the mask-gap self-check has to keep: the warnings array and the
        // segmentation meta node exist ONLY for a step that has a segmentation target, so every other
        // workflow's analyze envelope is byte-identical to what it was before the feature.
        var run = new Run { Shots = [(0.0, 3.0)] };

        StepExecutionResult result = await run.ExecuteAsync(c => c with { AnalyzeVisuals = false });

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject envelope = JsonNode.Parse(result.Output!)!.AsObject();
        envelope["warnings"].Should().BeNull("nothing about a mask can be wrong when no mask was asked for");
        envelope["meta"]!["segmentation"].Should().BeNull("a step with no segmentation target keeps its exact envelope");
    }

    [Fact]
    public async Task Three_silent_ten_second_sources_split_into_four_part_sub_shots_with_no_audio_or_transcript_ids()
    {
        // A silent single-shot clip: no audio stream, so no silence spans (g) and no transcript (t).
        var run = new Run { Shots = [(0.0, 10.0)], SourceCount = 3, DurationSec = 10, Silent = true };
        StepExecutionResult result = await run.ExecuteAsync(c => c with { MaxShotSec = 3, AnalyzeVisuals = false });

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonNode root = JsonNode.Parse(result.Output)!;
        JsonArray shots = root["view"]!["shots"]!.AsArray();
        shots.Select(s => s!["id"]!.GetValue<string>()).Should().Equal(Enumerable.Range(0, 12).Select(i => $"s{i}"));
        shots.Select(s => s!["part"]!.GetValue<string>()).Should().Equal(
            "1/4", "2/4", "3/4", "4/4", "1/4", "2/4", "3/4", "4/4", "1/4", "2/4", "3/4", "4/4");
        shots.Select(s => s!["src"]!.GetValue<int>()).Should().Equal(0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2);
        // Each 10 s source splits into four 2.5 s parts (no motion or pause to move the split onto).
        shots.Select(s => s!["endSec"]!.GetValue<double>()).Should().Equal(
            2.5, 5.0, 7.5, 10.0, 2.5, 5.0, 7.5, 10.0, 2.5, 5.0, 7.5, 10.0);
        AllIds(root).Where(id => id.StartsWith('g') || id.StartsWith('t')).Should().BeEmpty(
            "a silent clip has no silence spans and no transcript");
    }

    // ---- Content sub-shots (VideoSubShotMode.Content) -------------------------------------------

    [Fact]
    public async Task Content_mode_cuts_a_long_shot_where_the_movement_pauses_and_records_how()
    {
        var run = new Run { Shots = [(0.0, 10.0)], DurationSec = 10, Silent = true, Grid = PanWithHoldAt(3.75, 10) };
        StepExecutionResult result = await run.ExecuteAsync(c => c with { SubShotMode = VideoSubShotMode.Content, AnalyzeVisuals = false });

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        run.GridFps.Should().Equal([SubShotPlanner.SampleFps], "one content signal pass at the planner's rate");
        JsonNode root = JsonNode.Parse(result.Output)!;
        JsonArray shots = root["view"]!["shots"]!.AsArray();
        shots.Select(s => s!["id"]!.GetValue<string>()).Should().Equal("s0", "s1", "s2");
        shots.Select(s => s!["part"]!.GetValue<string>()).Should().Equal("1/3", "2/3", "3/3");
        shots[0]!["endSec"]!.GetValue<double>().Should().BeApproximately(3.75, 0.3, "the first cut is the hold, not 10/3");

        JsonNode subShots = root["meta"]!["subShots"]!;
        subShots["content"]!.GetValue<int>().Should().Be(1);
        subShots["evenFallback"]!.GetValue<int>().Should().Be(0);
        JsonNode shot = subShots["shots"]![0]!;
        shot["src"]!.GetValue<int>().Should().Be(0);
        shot["parts"]!.GetValue<int>().Should().Be(3);
        shot["method"]!.GetValue<string>().Should().Be("content");
        shot["cuts"]!.AsArray().Select(c => c!.GetValue<string>()).Should().Equal("motion_pause", "even");
    }

    [Fact]
    public async Task Content_mode_with_no_signal_falls_back_to_an_even_split_and_says_so()
    {
        var run = new Run { Shots = [(0.0, 10.0)], DurationSec = 10, Silent = true, GridFails = true };
        StepExecutionResult result = await run.ExecuteAsync(c => c with { SubShotMode = VideoSubShotMode.Content, AnalyzeVisuals = false });

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonNode root = JsonNode.Parse(result.Output)!;
        List<double> ends = root["view"]!["shots"]!.AsArray().Select(s => s!["endSec"]!.GetValue<double>()).ToList();
        ends.Should().HaveCount(3);
        ends[0].Should().BeApproximately(10 / 3.0, 0.01);
        ends[1].Should().BeApproximately(20 / 3.0, 0.01);
        root["meta"]!["subShots"]!["evenFallback"]!.GetValue<int>().Should().Be(1);
        root["meta"]!["subShots"]!["shots"]![0]!["method"]!.GetValue<string>().Should().Be("even_fallback");
    }

    [Fact]
    public async Task Even_mode_without_MaxShotSec_splits_at_MaxSubShotSec_like_MaxShotSec_would()
    {
        var run = new Run { Shots = [(0.0, 10.0)], DurationSec = 10, Silent = true };
        StepExecutionResult result = await run.ExecuteAsync(c => c with { SubShotMode = VideoSubShotMode.Even, AnalyzeVisuals = false });

        JsonNode root = JsonNode.Parse(result.Output)!;
        root["view"]!["shots"]!.AsArray().Select(s => s!["part"]!.GetValue<string>()).Should().Equal("1/3", "2/3", "3/3");
        root["meta"]!.AsObject().ContainsKey("subShots").Should().BeFalse("Even is today's split: nothing new in meta");
    }

    [Fact]
    public async Task By_default_no_content_pass_runs_and_meta_keeps_its_shape()
    {
        var run = new Run { Shots = [(0.0, 10.0)], DurationSec = 10, Silent = true, Grid = PanWithHoldAt(3.75, 10) };
        StepExecutionResult result = await run.ExecuteAsync(c => c with { AnalyzeVisuals = false });

        run.GridFps.Should().BeEmpty();
        JsonNode root = JsonNode.Parse(result.Output)!;
        root["meta"]!.AsObject().ContainsKey("subShots").Should().BeFalse();
        root["view"]!["shots"]![0]!.AsObject().ContainsKey("part").Should().BeFalse();
    }

    /// <summary>A 32x18 grid at the planner's rate: a smooth pattern panning 1 px per frame, holding for 0.5 s around <paramref name="holdSec"/>.</summary>
    private static FrameGridResult PanWithHoldAt(double holdSec, double seconds)
    {
        const int w = 32, h = 18;
        double fps = SubShotPlanner.SampleFps;
        int frames = (int)Math.Round(seconds * fps);
        byte[] data = new byte[frames * w * h * 3];
        for (int f = 0; f < frames; f++)
        {
            double t = f / fps;
            double offset = t < holdSec - 0.25 ? f : t < holdSec + 0.25 ? (holdSec - 0.25) * fps : f - 0.5 * fps;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double luma = 128 + 90 * Math.Sin(2 * Math.PI * (x + offset) / 32.0) + 10 * Math.Sin(2 * Math.PI * y / 18.0);
                    byte v = (byte)Math.Clamp(Math.Round(luma), 0, 255);
                    int i = ((f * h + y) * w + x) * 3;
                    data[i] = data[i + 1] = data[i + 2] = v;
                }
        }

        return new FrameGridResult(data, w, h, fps, frames);
    }

    /// <summary>Every "id" string anywhere in the view, depth first.</summary>
    private static IEnumerable<string> AllIds(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in obj)
            {
                if (kv.Key == "id" && kv.Value is JsonValue value && value.TryGetValue(out string? id))
                    yield return id!;
                foreach (string nested in AllIds(kv.Value))
                    yield return nested;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode? item in arr)
                foreach (string nested in AllIds(item))
                    yield return nested;
        }
    }

    [Fact]
    public async Task Without_MaxShotSec_no_shot_carries_a_part()
    {
        var run = new Run { Shots = [(0.0, 10.0)] };
        StepExecutionResult result = await run.ExecuteAsync(c => c with { AnalyzeVisuals = false });

        JsonNode.Parse(result.Output)!["view"]!["shots"]![0]!.AsObject().ContainsKey("part").Should().BeFalse();
    }

    [Fact]
    public async Task A_trimmed_source_is_cut_once_analysed_as_the_window_and_recorded_as_the_source()
    {
        var run = new Run { Shots = [(0.0, 4.0)] };
        StepExecutionResult result = await run.ExecuteAsync(c => c with
        {
            Source = new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: Run.FileId, InSec: 2, OutSec: 6),
            AnalyzeVisuals = false
        });

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        List<string> cut = run.FfmpegCalls.Single();
        cut.Should().ContainInOrder("-ss", "2", "-i");
        cut.Should().ContainInOrder("-t", "4");
        run.ProbedPaths.Should().Contain(p => p.EndsWith("source-window.mp4", StringComparison.Ordinal),
            "the analysis reads the window, not the whole file");

        JsonObject source = run.Artifact!["sources"]![0]!.AsObject();
        source["storageKey"]!.GetValue<string>().Should().Be(WindowKey, "every later step reads the window as the source");
        source["trimInSec"]!.GetValue<double>().Should().Be(2);
        source["trimOutSec"]!.GetValue<double>().Should().Be(6);
        JsonNode window = JsonNode.Parse(result.Output)!["meta"]!["sourceWindows"]![0]!;
        (window["inSec"]!.GetValue<double>(), window["outSec"]!.GetValue<double>()).Should().Be((2.0, 6.0));
    }

    [Fact]
    public async Task A_damaged_upload_is_the_customers_source_invalid_with_the_damaged_file_message()
    {
        var run = new Run { Shots = [(0.0, 4.0)], UnreadableSuffix = "source.mp4" };
        StepExecutionResult result = await run.ExecuteAsync(c => c with
        {
            Source = new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: Run.FileId, InSec: 2, OutSec: 6),
            AnalyzeVisuals = false
        });

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain(UserMediaInputFormats.RefusedCode);
        (result.ErrorDetails + result.Output).Should().Contain("damaged or incomplete").And.NotContain("playlist");
    }

    [Fact]
    public async Task An_unreadable_trimmed_window_is_a_platform_failure_not_the_customers_source_invalid()
    {
        var run = new Run { Shots = [(0.0, 4.0)], UnreadableSuffix = "window.mp4" };
        StepExecutionResult result = await run.ExecuteAsync(c => c with
        {
            Source = new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: Run.FileId, InSec: 2, OutSec: 6),
            AnalyzeVisuals = false
        });

        result.Status.Should().Be(StepStatus.Failed);
        (result.ErrorDetails + result.Output).Should().NotContain(UserMediaInputFormats.RefusedCode,
            "the window is the engine's own output: a broken one is refunded, not charged to the customer");
        run.ProbedPaths.Should().Contain(p => p.EndsWith("window.mp4", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(5.0, 5.0)]
    [InlineData(5.0, 2.0)]
    [InlineData(40.0, null)]
    [InlineData(-1.0, 3.0)]
    public async Task An_empty_or_impossible_window_fails_clearly(double inSec, double? outSec)
    {
        var run = new Run { Shots = [(0.0, 4.0)] };
        StepExecutionResult result = await run.ExecuteAsync(c => c with
        {
            Source = new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: Run.FileId, InSec: inSec, OutSec: outSec)
        });

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("SOURCE_TRIM_INVALID");
        run.FfmpegCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Beat_facts_reach_the_editor_on_offered_tracks_and_on_the_track_the_edit_is_cut_to()
    {
        var run = new Run { Shots = [(0.0, 4.0)], MusicWav = BeatSyncTests.Wav(MusicBeatAnalyzerTests.ClickTrack(128, 0.1, 20)) };
        StepExecutionResult result = await run.ExecuteAsync(c => c with
        {
            AnalyzeVisuals = false,
            OfferMusicTracks = true,
            AnalyzeMusicBeats = true,
            MusicBeatTrackProjectFileId = Run.MusicId
        });

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonNode view = JsonNode.Parse(result.Output)!["view"]!;
        JsonNode offered = view["musicTracks"]![0]!["beat"]!;
        offered["bpm"]!.GetValue<double>().Should().BeApproximately(128, 0.5);
        offered["tempo"]!.GetValue<string>().Should().Be("fast");
        offered["barSec"]!.GetValue<double>().Should().BeApproximately(1.875, 0.01);
        view["music"]!["name"]!.GetValue<string>().Should().Be("the-last-point.mp3");
        run.Artifact!["music"]!["beat"]!["bpm"]!.GetValue<double>().Should().BeApproximately(128, 0.5);
    }

    [Fact]
    public async Task Without_AnalyzeMusicBeats_offered_tracks_carry_no_beat()
    {
        var run = new Run { Shots = [(0.0, 4.0)], MusicWav = BeatSyncTests.Wav(MusicBeatAnalyzerTests.ClickTrack(128, 0.1, 20)) };
        StepExecutionResult result = await run.ExecuteAsync(c => c with { AnalyzeVisuals = false, OfferMusicTracks = true });

        JsonNode view = JsonNode.Parse(result.Output)!["view"]!;
        view["musicTracks"]![0]!.AsObject().ContainsKey("beat").Should().BeFalse();
        view.AsObject().ContainsKey("music").Should().BeFalse();
    }

    [Fact]
    public void The_cut_keeps_audio_when_there_is_any_and_re_encodes_frame_accurately()
    {
        VideoAnalyzeStepExecutor.BuildTrimArgs("in.mp4", "out.mp4", 1.25, 3.5).Should().Equal(
            "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
            "-protocol_whitelist", "file", "-format_whitelist", UserMediaInputFormats.Whitelist,
            "-ss", "1.25", "-i", "in.mp4", "-t", "2.25",
            "-map", "0:v:0", "-map", "0:a:0?",
            "-c:v", "libx264", "-crf", "16", "-preset", "veryfast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "out.mp4");
    }

    /// <summary>One analyze run against mocks.</summary>
    private sealed class Run
    {
        public static readonly Guid FileId = Guid.NewGuid();
        private readonly Guid[] _fileIds = [FileId, Guid.NewGuid(), Guid.NewGuid()];
        public required IReadOnlyList<(double, double)> Shots { get; init; }
        public IReadOnlyList<(double, double)> Silences { get; init; } = [];
        /// <summary>How many project files are analysed (1 = the classic single source; up to 3). Sources after the first are passed as <c>Sources</c>.</summary>
        public int SourceCount { get; init; } = 1;
        /// <summary>Duration every source probes at (the window probe is 4 s).</summary>
        public double DurationSec { get; init; } = 30;
        /// <summary>When true every source probes with no audio stream, like a silent clip.</summary>
        public bool Silent { get; init; }
        public IReadOnlyList<Guid> FileIds => _fileIds[..SourceCount];
        public static readonly Guid MusicId = Guid.NewGuid();
        /// <summary>When set, the project also holds an audio file whose decode yields these WAV bytes.</summary>
        public byte[]? MusicWav { get; init; }
        /// <summary>The grid every frame-grid sample returns (null: the sampler is left unconfigured).</summary>
        public FrameGridResult? Grid { get; init; }
        /// <summary>When true every frame-grid sample fails, as a broken decode would.</summary>
        public bool GridFails { get; init; }
        /// <summary>The sample rate of every frame-grid sample taken.</summary>
        public List<double> GridFps { get; } = [];
        public List<List<string>> FfmpegCalls { get; } = [];
        public List<string> ProbedPaths { get; } = [];
        /// <summary>When set, probing a path that ends with it fails as content no whitelisted demuxer recognises.</summary>
        public string? UnreadableSuffix { get; init; }
        public JsonObject? Artifact { get; private set; }

        public async Task<StepExecutionResult> ExecuteAsync(Func<VideoAnalyzeStepConfig, VideoAnalyzeStepConfig> configure)
        {
            Guid projectId = Guid.NewGuid();
            var workspace = new Mock<IProjectFileWorkspace>();
            workspace.Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var list = new List<ProjectWorkspaceFile>();
                    for (int i = 0; i < SourceCount; i++)
                    {
                        string name = i == 0 ? "take.mp4" : $"take{i}.mp4";
                        string key = i == 0 ? SourceKey : $"projects/p/userFiles/{name}";
                        list.Add(new ProjectWorkspaceFile(FileIds[i], projectId, name, null, "userFiles", key, "video/mp4", 3, DateTime.UnixEpoch, null));
                    }
                    if (MusicWav is not null)
                        list.Add(new ProjectWorkspaceFile(MusicId, projectId, "the-last-point.mp3", null, "userFiles", "projects/p/userFiles/the-last-point.mp3", "audio/mpeg", 3, DateTime.UnixEpoch, null));
                    return list;
                });
            workspace.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) =>
                {
                    File.WriteAllBytes(dest, [0, 1, 2]);
                    return Task.CompletedTask;
                });
            workspace.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid _, string local, string name, string _, CancellationToken _, string _) =>
                {
                    if (name.EndsWith("analysis.json", StringComparison.Ordinal))
                    {
                        Artifact = JsonNode.Parse(File.ReadAllText(local))!.AsObject();
                        return "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
                    }

                    return name.EndsWith("window.mp4", StringComparison.Ordinal) ? WindowKey : "projects/p/agentFiles/other";
                });

            var probe = new Mock<IMediaProbe>();
            probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IMediaWorkspace _, string path, CancellationToken _) =>
                {
                    ProbedPaths.Add(path);
                    if (UnreadableSuffix is not null && path.EndsWith(UnreadableSuffix, StringComparison.Ordinal))
                        throw new MediaFormatNotAllowedException(path, unrecognised: true);
                    return new MediaProbeResult(path.EndsWith("window.mp4", StringComparison.Ordinal) ? 4 : DurationSec, 30, 1, 1920, 1080, "h264", Silent ? null : "aac", Silent ? null : 48000);
                });
            var silence = new Mock<ISilenceDetector>();
            silence.Setup(s => s.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Silences);
            var shots = new Mock<IShotDetector>();
            shots.Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Shots);
            var audio = new Mock<IAudioExtractor>();
            audio.Setup(a => a.ExtractWavAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<IMediaWorkspace, string, string, CancellationToken>(async (ws, _, outPath, ct) =>
                {
                    await ws.WriteBytesAsync(outPath, MusicWav is not null && Path.GetFileName(outPath).StartsWith("beat-", StringComparison.Ordinal) ? MusicWav : new byte[100], ct);
                });
            var runner = new Mock<IVideoToolRunner>();
            // The executor now runs ffmpeg through the workspace (the whole point of this WP), and
            // the fake answers that arm. The argv is unchanged: the last element is still the output
            // path, which for this local workspace is the same absolute scratch path it always was.
            runner.Setup(r => r.RunFfmpegAsync(
                    It.IsAny<IMediaWorkspace>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IMediaWorkspace _, IReadOnlyList<string> args, TimeSpan _, CancellationToken _) =>
                {
                    FfmpegCalls.Add(args.ToList());
                    File.WriteAllBytes(args[^1], [0, 1, 2]);
                    return new VideoToolResult(0, "", "", false);
                });

            var gridSampler = new Mock<IFrameGridSampler>();
            if (Grid is not null || GridFails)
            {
                gridSampler.Setup(g => g.SampleAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(),
                        It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((IMediaWorkspace _, string _, double fps, int _, int _, double _, int _, CancellationToken _) =>
                    {
                        GridFps.Add(fps);
                        return GridFails ? throw new InvalidOperationException("ffmpeg grid sampling failed") : Grid!;
                    });
            }

            var executor = new VideoAnalyzeStepExecutor(
                probe.Object, silence.Object, shots.Object, audio.Object, gridSampler.Object,
                new Mock<IKeyframeExtractor>().Object, new Mock<ISharpnessSampler>().Object, new Mock<IShotCaptioner>().Object,
                workspace.Object, new Mock<ITranscriptionClientFactory>().Object, new Mock<IInferenceProviderResolver>().Object,
                Options.Create(new VideoEditingOptions
                {
                    ScratchPath = Path.Combine(Path.GetTempPath(), "video-analyze-trim-tests", Guid.NewGuid().ToString("N")),
                    MaxConcurrentJobs = 1,
                    AnalyzeTimeoutSeconds = 30,
                    CompileTimeoutSeconds = 30
                }),
                NullLogger<VideoAnalyzeStepExecutor>.Instance,
                videoToolRunner: runner.Object);

            VideoAnalyzeStepConfig config = configure(new VideoAnalyzeStepConfig(
                Version: 1,
                Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: FileId),
                Transcription: VideoTranscriptionMode.Off,
                Sources: SourceCount > 1
                    ? FileIds.Select(id => new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: id)).ToList()
                    : null));
            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 1,
                StepType = StepType.VideoAnalyze,
                VideoAnalyzeConfigJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } })
            };

            return await executor.ExecuteAsync(new StepExecutionContext
            {
                Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = projectId },
                Step = step,
                AllSteps = [step],
                AccumulatedOutput = string.Empty,
                StepOutputHistory = [],
                CurrentStepIndex = 0,
                IterationCount = 0,
                CorrelationId = "test",
                CancellationToken = CancellationToken.None
            });
        }
    }
}