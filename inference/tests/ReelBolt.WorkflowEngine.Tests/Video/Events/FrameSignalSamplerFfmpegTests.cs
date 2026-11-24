using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Video.Events;

/// <summary>
/// The sampler and the whole detector end to end on tiny clips that ffmpeg renders at test time, with exactly known
/// event frames (a box that appears at frame 45, a hard cut at frame 45). No-ops on a host without ffmpeg/ffprobe.
/// </summary>
public class FrameSignalSamplerFfmpegTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "events-fixture-" + Guid.NewGuid().ToString("N"));
    private readonly IOptions<VideoEditingOptions> _options = Options.Create(new VideoEditingOptions());
    private readonly FfmpegVideoToolRunner _runner;

    public FrameSignalSamplerFfmpegTests()
    {
        Directory.CreateDirectory(_dir);
        _runner = new FfmpegVideoToolRunner(_options, NullLogger<FfmpegVideoToolRunner>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<string> RenderAsync(string name, string graph)
    {
        string path = Path.Combine(_dir, name);
        VideoToolResult result = await _runner.RunFfmpegAsync(
            ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-f", "lavfi", "-i", graph, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "8", path],
            TimeSpan.FromSeconds(60), CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.StdErr);
        return name;
    }

    private const string BoxAppears =
        "color=c=0x202020:s=320x180:r=30:d=3,drawbox=x=100:y=60:w=110:h=70:color=white:t=fill:enable='gte(n,45)'";

    private const string HardCut =
        "color=c=red:s=320x180:r=30:d=1.5[a];color=c=blue:s=320x180:r=30:d=1.5[b];[a][b]concat=n=2:v=1:a=0";

    private sealed class Collector : IFrameConsumer
    {
        public List<byte[]> Frames { get; } = [];

        public void OnFrame(int index, ReadOnlySpan<byte> gray) => Frames.Add(gray.ToArray());
    }

    [FfmpegToolsFact]
    public async Task The_sampler_streams_every_frame_as_160x90_gray()
    {
        string clip = await RenderAsync("box.mp4", BoxAppears);
        LocalMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(_dir, _runner);
        FrameSignalSampler sampler = new(_options, NullLogger<FrameSignalSampler>.Instance);
        Collector collector = new();

        FrameSignalInfo info = await sampler.SampleAsync(workspace, clip, 30, 1, 3, collector, CancellationToken.None);

        info.Divisor.Should().Be(1);
        info.Fps.Should().BeApproximately(30, 1e-9);
        info.Width.Should().Be(160);
        info.Height.Should().Be(90);
        info.FrameCount.Should().Be(90);
        collector.Frames.Should().HaveCount(90).And.OnlyContain(f => f.Length == 160 * 90);
        collector.Frames[10].Average(b => (double)b).Should().BeApproximately(32, 3, "0x202020 is luma 32");
        collector.Frames[80][45 * 160 + 80].Should().BeGreaterThan(200, "the white box is on screen at the centre after frame 45");
    }

    [FfmpegToolsFact]
    public async Task A_box_drawn_from_frame_45_is_an_Appear_at_frame_45()
    {
        VideoEventSet set = await DetectAsync("box.mp4", BoxAppears);

        set.FrameCount.Should().Be(90);
        set.Fps.Should().BeApproximately(30, 1e-9);
        set.Events.Should().Contain(e => e.Type == VideoEventType.Appear && Math.Abs(e.Frame - 45) <= 2, Describe(set));
    }

    [FfmpegToolsFact]
    public async Task A_concatenated_hard_cut_is_a_Transition_at_frame_45()
    {
        VideoEventSet set = await DetectAsync("cut.mp4", HardCut);

        set.Events.Should().Contain(e => e.Type == VideoEventType.Transition && Math.Abs(e.Frame - 45) <= 2, Describe(set));
    }

    [FfmpegToolsFact]
    public async Task Two_end_to_end_runs_give_identical_events()
    {
        VideoEventSet first = await DetectAsync("box.mp4", BoxAppears);
        VideoEventSet second = await DetectAsync("box.mp4", BoxAppears, render: false);

        Describe(second).Should().Be(Describe(first));
        second.Events.Select(e => e.Strength).Should().Equal(first.Events.Select(e => e.Strength));
    }

    private sealed class Counter : IFrameConsumer
    {
        public int Frames;

        public void OnFrame(int index, ReadOnlySpan<byte> gray) => Frames++;
    }

    private string[] ScratchFiles() => Directory.GetFiles(_dir, "events-gray-*.raw");

    [FfmpegToolsFact]
    public async Task A_tiny_clip_with_three_frames_ten_minutes_apart_stays_within_the_duration_times_rate_cap()
    {
        string path = Path.Combine(_dir, "sparse.mkv");
        VideoToolResult made = await _runner.RunFfmpegAsync(
            ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:s=320x180:r=1:d=3,setpts=N*600/TB",
             "-fps_mode", "passthrough", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-f", "matroska", path],
            TimeSpan.FromSeconds(60), CancellationToken.None);
        made.Succeeded.Should().BeTrue(made.StdErr);
        new FileInfo(path).Length.Should().BeLessThan(20_000);
        LocalMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(_dir, _runner);
        MediaProbeResult probed = await new FfprobeMediaProbe(_runner, _options, NullLogger<FfprobeMediaProbe>.Instance)
            .ProbeAsync(workspace, "sparse.mkv", CancellationToken.None);
        (double fps, _) = FrameSignalSampler.PlanRate(SourceTimeoutPolicy.FpsOf(probed.FpsNum, probed.FpsDen));
        Counter counter = new();

        FrameSignalInfo info = await new FrameSignalSampler(_options, NullLogger<FrameSignalSampler>.Instance)
            .SampleAsync(workspace, "sparse.mkv", probed.FpsNum, probed.FpsDen, probed.DurationSec, counter, CancellationToken.None);

        counter.Frames.Should().Be(info.FrameCount);
        info.FrameCount.Should().BeLessThanOrEqualTo((int)FrameSignalSampler.PlanMaxFrames(probed.DurationSec, fps));
        ScratchFiles().Should().BeEmpty();
    }

    [FfmpegToolsFact]
    public async Task The_frame_cap_holds_even_when_the_stated_duration_is_far_shorter_than_the_clip()
    {
        string clip = await RenderAsync("long.mp4", BoxAppears);
        LocalMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(_dir, _runner);
        Counter counter = new();

        FrameSignalInfo info = await new FrameSignalSampler(_options, NullLogger<FrameSignalSampler>.Instance)
            .SampleAsync(workspace, clip, 30, 1, 1.0, counter, CancellationToken.None);

        info.FrameCount.Should().BeInRange(30, (int)FrameSignalSampler.PlanMaxFrames(1.0, 30));
        info.FrameCount.Should().BeLessThan(90);
        ScratchFiles().Should().BeEmpty();
    }

    [FfmpegToolsFact]
    public async Task The_scratch_file_is_deleted_when_the_consumer_throws_and_when_ffmpeg_fails()
    {
        string clip = await RenderAsync("fail.mp4", BoxAppears);
        LocalMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(_dir, _runner);
        FrameSignalSampler sampler = new(_options, NullLogger<FrameSignalSampler>.Instance);

        Func<Task> consumerFails = () => sampler.SampleAsync(workspace, clip, 30, 1, 3, new Throwing(), CancellationToken.None);
        await consumerFails.Should().ThrowAsync<InvalidOperationException>();
        ScratchFiles().Should().BeEmpty();

        Func<Task> ffmpegFails = () => sampler.SampleAsync(workspace, "missing.mp4", 30, 1, 3, new Counter(), CancellationToken.None);
        await ffmpegFails.Should().ThrowAsync<Exception>();
        ScratchFiles().Should().BeEmpty();
    }

    [FfmpegToolsFact]
    public async Task The_sparse_attack_stays_bounded_in_the_passthrough_path_too()
    {
        string path = Path.Combine(_dir, "sparse2.mkv");
        VideoToolResult made = await _runner.RunFfmpegAsync(
            ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:s=320x180:r=1:d=3,setpts=N*600/TB",
             "-fps_mode", "passthrough", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-f", "matroska", path],
            TimeSpan.FromSeconds(60), CancellationToken.None);
        made.Succeeded.Should().BeTrue(made.StdErr);
        string raw = Path.Combine(_dir, "sparse-raw.raw");
        long cap = FrameSignalSampler.PlanMaxFrames(1200, 1);

        VideoToolResult sampled = await _runner.RunFfmpegAsync(
            FrameSignalSampler.BuildArgs(path, raw, 1, 2, cap), TimeSpan.FromSeconds(60), CancellationToken.None);

        sampled.Succeeded.Should().BeTrue(sampled.StdErr);
        new FileInfo(raw).Length.Should().Be(3 * FrameSignalSampler.FrameBytes, "passthrough writes exactly the three decoded frames");
        VideoToolResult retimed = await _runner.RunFfmpegAsync(
            FrameSignalSampler.BuildArgs(path, raw, 1, 2, 50, 30, 1), TimeSpan.FromSeconds(60), CancellationToken.None);
        retimed.Succeeded.Should().BeTrue(retimed.StdErr);
        new FileInfo(raw).Length.Should().BeLessThanOrEqualTo(50L * FrameSignalSampler.FrameBytes, "the retiming path is held by -frames:v and -fs");
    }

    [FfmpegToolsFact]
    public async Task On_a_variable_rate_clip_an_event_at_a_known_time_lands_within_two_frames_of_its_true_frame()
    {
        // Frames 10..19 are dropped before the box appears at 1.5 s (true frame 45), leaving a 10-frame gap in the timestamps.
        string path = Path.Combine(_dir, "vfr.mp4");
        VideoToolResult made = await _runner.RunFfmpegAsync(
            ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-f", "lavfi", "-i", BoxAppears,
             "-vf", "select='lt(n,10)+gte(n,20)'", "-fps_mode", "passthrough", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "8", path],
            TimeSpan.FromSeconds(60), CancellationToken.None);
        made.Succeeded.Should().BeTrue(made.StdErr);

        VideoEventSet set = await DetectAsync("vfr.mp4", "", render: false);

        set.Events.Should().Contain(e => e.Type == VideoEventType.Appear && Math.Abs(e.Frame - 45) <= 2, Describe(set));
    }

    [FfmpegToolsFact]
    public async Task Decimating_a_120_fps_clip_by_selecting_every_second_frame_matches_the_fps_filter_in_frame_count()
    {
        string clip = await RenderAsync("fast.mp4", "color=c=0x202020:s=320x180:r=120:d=2,drawbox=x=100:y=60:w=110:h=70:color=white:t=fill:enable='gte(n,90)'");
        (double fps, int divisor) = FrameSignalPlan(120);
        string select = Path.Combine(_dir, "sel.raw"), rate = Path.Combine(_dir, "rate.raw");
        long cap = FrameSignalSampler.PlanMaxFrames(2, fps);

        VideoToolResult a = await _runner.RunFfmpegAsync(
            FrameSignalSampler.BuildArgs(Path.Combine(_dir, clip), select, divisor, 2, cap), TimeSpan.FromSeconds(60), CancellationToken.None);
        VideoToolResult b = await _runner.RunFfmpegAsync(
            FrameSignalSampler.BuildArgs(Path.Combine(_dir, clip), rate, divisor, 2, cap, 120, 1), TimeSpan.FromSeconds(60), CancellationToken.None);

        a.Succeeded.Should().BeTrue(a.StdErr);
        b.Succeeded.Should().BeTrue(b.StdErr);
        divisor.Should().Be(2);
        new FileInfo(select).Length.Should().Be(new FileInfo(rate).Length);
        new FileInfo(select).Length.Should().Be(120L * FrameSignalSampler.FrameBytes);
    }

    private static (double, int) FrameSignalPlan(double sourceFps) => FrameSignalSampler.PlanRate(sourceFps);

    private sealed class Throwing : IFrameConsumer
    {
        public void OnFrame(int index, ReadOnlySpan<byte> gray) => throw new InvalidOperationException("consumer failed");
    }

    private async Task<VideoEventSet> DetectAsync(string name, string graph, bool render = true)
    {
        string clip = render ? await RenderAsync(name, graph) : name;
        LocalMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(_dir, _runner);
        FfprobeMediaProbe probe = new(_runner, _options, NullLogger<FfprobeMediaProbe>.Instance);
        MediaProbeResult probed = await probe.ProbeAsync(workspace, clip, CancellationToken.None);
        VideoEventDetector detector = new(
            new FrameSignalSampler(_options, NullLogger<FrameSignalSampler>.Instance),
            new FfmpegSilenceDetector(_runner, _options, NullLogger<FfmpegSilenceDetector>.Instance),
            _options, NullLogger<VideoEventDetector>.Instance);
        return await detector.DetectAsync(workspace, clip, probed, ct: CancellationToken.None);
    }

    private static string Describe(VideoEventSet set) => string.Join(", ", set.Events.Select(e => $"{e.Type}@{e.Frame}"));

    // ---- a part that fails is left out, and the set says so (nothing may keep it as the answer for the file) ----

    private VideoEventDetector NewDetector(ISilenceDetector? silence = null) =>
        new(
            new FrameSignalSampler(_options, NullLogger<FrameSignalSampler>.Instance),
            silence ?? new FfmpegSilenceDetector(_runner, _options, NullLogger<FfmpegSilenceDetector>.Instance),
            _options, NullLogger<VideoEventDetector>.Instance);

    [FfmpegToolsFact]
    public async Task A_run_in_which_every_part_worked_is_not_marked_incomplete()
    {
        VideoEventSet set = await DetectAsync("complete.mp4", BoxAppears);

        set.Degraded.Should().BeFalse();
    }

    [FfmpegToolsFact]
    public async Task A_failed_silence_pass_leaves_out_the_speech_edges_and_marks_the_set_incomplete()
    {
        string clip = await RenderAsync("speech-fails.mp4", BoxAppears);
        LocalMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(_dir, _runner);
        var silence = new Mock<ISilenceDetector>();
        silence.Setup(s => s.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ffmpeg silencedetect failed (timedOut=True)"));
        MediaProbeResult probe = new(3, 30, 1, 320, 180, "h264", "aac", 48000);

        VideoEventSet set = await NewDetector(silence.Object)
            .DetectAsync(workspace, clip, probe, new VideoEventOptions { DetectBeats = false }, ct: CancellationToken.None);

        set.Events.Should().NotContain(e => e.Type == VideoEventType.SpeechStart || e.Type == VideoEventType.SpeechEnd);
        set.Events.Should().NotBeEmpty("the picture events are still returned");
        set.Degraded.Should().BeTrue();
    }

    [FfmpegToolsFact]
    public async Task A_failed_beat_decode_leaves_out_the_beats_and_marks_the_set_incomplete()
    {
        // The probe says there is sound, the file has none: the audio decode for the beats fails.
        string clip = await RenderAsync("beats-fail.mp4", BoxAppears);
        LocalMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(_dir, _runner);
        MediaProbeResult probe = new(3, 30, 1, 320, 180, "h264", "aac", 48000);

        VideoEventSet set = await NewDetector()
            .DetectAsync(workspace, clip, probe, new VideoEventOptions { DetectSpeech = false }, ct: CancellationToken.None);

        set.Events.Should().NotContain(e => e.Type == VideoEventType.Beat);
        set.Degraded.Should().BeTrue();
    }

    [FfmpegToolsFact]
    public async Task A_failed_frame_rate_check_reads_the_file_as_constant_rate_and_marks_the_set_incomplete()
    {
        string clip = await RenderAsync("rate-unknown.mp4", BoxAppears);
        var runner = new Mock<IVideoToolRunner>();
        runner.Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<string>, TimeSpan, CancellationToken>((args, timeout, ct) => _runner.RunFfmpegAsync(args, timeout, ct));
        runner.Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(-1, "", "", TimedOut: true));
        LocalMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(_dir, runner.Object);
        Counter counter = new();

        FrameSignalInfo info = await new FrameSignalSampler(_options, NullLogger<FrameSignalSampler>.Instance)
            .SampleAsync(workspace, clip, 30, 1, 3, counter, CancellationToken.None);
        info.RateUnknown.Should().BeTrue();
        info.FrameCount.Should().Be(90, "the frames are read as before");

        MediaProbeResult probe = new(3, 30, 1, 320, 180, "h264", null, null);
        VideoEventSet set = await NewDetector().DetectAsync(workspace, clip, probe, ct: CancellationToken.None);
        set.Degraded.Should().BeTrue();

        // With the check working, the same file gives a complete set.
        FrameSignalInfo known = await new FrameSignalSampler(_options, NullLogger<FrameSignalSampler>.Instance)
            .SampleAsync(LocalMediaWorkspace.CreateAt(_dir, _runner), clip, 30, 1, 3, new Counter(), CancellationToken.None);
        known.RateUnknown.Should().BeFalse();
    }
}
