using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// Pins the ffmpeg argv and the run plan without requiring ffmpeg: a fake <see cref="IProcessRunner"/>
/// records every call and writes the files the sampler expects. Also pins temp-directory cleanup.
/// </summary>
public sealed class ModerationMediaSamplerTests
{
    private static ContentModerationOptions Options() => new()
    {
        SampleImageMaxLongSide = 512,
        VideoFrameIntervalSeconds = 2,
        VideoMaxFrames = 20
    };

    [Fact]
    public async Task An_image_is_one_jpeg_downscaled_to_the_long_side_cap()
    {
        FakeRunner runner = new() { MeasureReport = "frame=    1 fps=0.0 time=00:00:00.04" };
        runner.Writes.Add(args => args.Any(a => a.Contains("scale='min(512,iw)':-2"))
            ? [1, 2, 3, 4]
            : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(
            new MemoryStream([0xFF, 0xD8]), "image/jpeg", CancellationToken.None);

        sample.Kind.Should().Be(ModerationContentKind.Image);
        sample.Failure.Should().BeNull();
        sample.Images.Should().ContainSingle().Which.Should().Equal(1, 2, 3, 4);
        sample.Audio.Should().BeNull();

        Call call = runner.Calls.Should().ContainSingle().Which;
        call.FileName.Should().Be("ffmpeg");
        call.Arguments.Should().ContainInOrder("-i", "-vf");
        call.Arguments.Should().Contain("scale='min(512,iw)':-2:flags=lanczos");
        call.Arguments.Should().ContainInOrder("-frames:v", "1");
        call.Arguments.Should().Contain("-format_whitelist");
        call.Arguments.Last().Should().EndWith(".jpg");
        call.Arguments[call.Arguments.ToList().IndexOf("-i") + 1].Should().NotEndWith(".jpg", "image2 is not on the whitelist");
    }

    [Fact]
    public void The_whitelist_admits_the_still_image_demuxers()
    {
        // Without these, ffmpeg refuses a .jpg/.png/.webp/.gif under -format_whitelist and the
        // image arm silently produces no sample.
        string[] demuxers = FfmpegModerationMediaSampler.MediaInputFormats.Split(',');
        foreach (string demuxer in new[] { "jpeg_pipe", "png_pipe", "gif", "webp_pipe", "bmp_pipe", "avif" })
            demuxers.Should().Contain(demuxer);
    }

    [Fact]
    public async Task A_real_ffmpeg_downscales_a_png_when_ffmpeg_is_available()
    {
        if (!OnPath("ffmpeg"))
            return; // The unit-test host may not ship the runtime image's ffmpeg.

        // 1x1 transparent PNG.
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        FfmpegModerationMediaSampler sampler = new(new SystemProcessRunner(), Options());
        ModerationMediaSample sample = await sampler.SampleAsync(
            new MemoryStream(png), "image/png", CancellationToken.None);

        sample.Failure.Should().BeNull("the image demuxer whitelist admits a PNG");
        sample.Images.Should().ContainSingle();
        sample.Images[0].Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_audio_sample_over_the_configured_byte_cap_is_a_failure_not_a_memory_hog()
    {
        ContentModerationOptions options = Options();
        options.MaxAudioBytes = 8;
        FakeRunner runner = new();
        runner.Writes.Add(args => args.Contains("-ar") ? new byte[9] : []);
        FfmpegModerationMediaSampler sampler = new(runner, options);

        ModerationMediaSample sample = await sampler.SampleAsync(
            new MemoryStream([1]), "audio/wav", CancellationToken.None);

        sample.Audio.Should().BeNull();
        sample.Failure.Should().Contain("more than");
    }

    private static bool OnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(directory => File.Exists(Path.Combine(directory, executable)));

    [Fact]
    public async Task A_video_samples_one_keyframe_per_slice_at_the_configured_interval_plus_a_16khz_mono_wav()
    {
        FakeRunner runner = new()
        {
            ProbeReport = VideoWithOneAudio.Replace("00:10:00.00", "00:00:10.00", StringComparison.Ordinal),
            MeasureReport = "frame=  250 fps=0 time=00:00:10.00"
        };
        // Each frame call writes one JPEG; the audio call writes a WAV.
        runner.Writes.Add(args => IsFrame(args) ? [11] : args.Contains("-ar") ? [21, 22] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options(), new Random(2), TimeSpan.FromMinutes(5));

        ModerationMediaSample sample = await sampler.SampleAsync(
            new MemoryStream([0, 0, 0]), "video/mp4", CancellationToken.None);

        sample.Kind.Should().Be(ModerationContentKind.Video);
        sample.Failure.Should().BeNull();
        sample.Images.Should().HaveCount(5, "10 s at one frame per 2 s");
        sample.Audio.Should().NotBeNull();

        List<Call> frames = runner.Calls.Where(c => IsFrame(c.Arguments)).ToList();
        frames.Should().HaveCount(5);
        List<double> drawn = frames.Select(f => double.Parse(SeekOf(f), CultureInfo.InvariantCulture)).ToList();
        drawn.Should().BeInAscendingOrder().And.OnlyContain(t => t >= 0 && t < 10);
        drawn.Count(t => t < 5).Should().BeInRange(2, 3, "drawn over the whole length, half by time and half by position");
        frames[0].Arguments.Should().NotContain("-noaccurate_seek", "the frame chosen is the frame returned, not the keyframe before it");
        frames[0].Arguments.Should().Contain(a => a.StartsWith("scale='min(512,iw)':-2:flags=lanczos", StringComparison.Ordinal));
        frames[0].Arguments.Should().ContainInOrder("-frames:v", "1");
        frames[0].Arguments.Last().Should().EndWith("frame_001.jpg");

        Call audio = runner.Calls.Single(c => c.Arguments.Contains("-ac"));
        audio.Arguments.Should().ContainInOrder("-ac", "1", "-ar", "16000", "-acodec", "pcm_s16le");
        audio.Arguments.Last().Should().EndWith(".wav");
    }

    [Fact]
    public async Task An_audio_file_samples_only_the_wav()
    {
        FakeRunner runner = new();
        runner.Writes.Add(args => args.Contains("-ar") ? [7, 8, 9] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(
            new MemoryStream([1, 2, 3]), "audio/mpeg", CancellationToken.None);

        sample.Kind.Should().Be(ModerationContentKind.Audio);
        sample.Images.Should().BeEmpty();
        sample.Audio.Should().Equal(7, 8, 9);
        runner.Calls.Should().ContainSingle().Which.Arguments.Should().ContainInOrder("-ar", "16000");
    }

    [Fact]
    public async Task The_temporary_directory_is_deleted_afterwards()
    {
        FakeRunner runner = new();
        runner.Writes.Add(_ => [1]);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        await sampler.SampleAsync(new MemoryStream([1]), "image/png", CancellationToken.None);

        string workDirectory = Path.GetDirectoryName(runner.Calls.Single().Arguments.Last())!;
        Directory.Exists(workDirectory).Should().BeFalse("the sampler cleans its temp directory in finally");
    }

    [Fact]
    public async Task A_failed_ffmpeg_pass_reports_the_failure_and_still_cleans_up()
    {
        FakeRunner runner = new() { Result = new ProcessRunResult(1, string.Empty, "bad input", false) };
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(
            new MemoryStream([1]), "image/jpeg", CancellationToken.None);

        sample.Images.Should().BeEmpty();
        sample.Failure.Should().NotBeNull().And.Contain("bad input");
        Directory.Exists(Path.GetDirectoryName(runner.Calls.Single().Arguments.Last())!).Should().BeFalse();
    }

    [Fact]
    public async Task An_unsupported_mime_type_is_refused_without_running_a_process()
    {
        FakeRunner runner = new();
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(
            new MemoryStream([1]), "application/pdf", CancellationToken.None);

        sample.Failure.Should().NotBeNull().And.Contain("Unsupported");
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Values_outside_the_safe_ranges_are_clamped_at_the_consumer()
    {
        ContentModerationOptions options = new()
        {
            SampleImageMaxLongSide = 0,
            VideoFrameIntervalSeconds = 0,
            VideoMaxFrames = 500
        };
        FakeRunner runner = new();
        runner.Writes.Add(args => IsFrame(args) ? [1] : []);
        FfmpegModerationMediaSampler sampler = new(runner, options);

        await sampler.SampleAsync(new MemoryStream([1]), "video/mp4", CancellationToken.None);

        // 500 asked for is 20 (the clamp), and an interval of 0 is 1 s: 600 s would be 600 frames, held at the cap
        // (a frame drawn twice is judged once, so a few fewer is possible, never more).
        runner.Calls.Count(c => IsFrame(c.Arguments)).Should().BeInRange(17, 20);
    }

    [Fact]
    public async Task A_cover_first_and_the_real_video_second_samples_the_real_video_stream()
    {
        FakeRunner runner = new()
        {
            ProbeReport = "Input #0, matroska,webm, from 'x':\n  Duration: 00:00:10.00\n" +
                          "  Stream #0:0: Video: mjpeg, yuvj420p, 200x200 (attached pic)\n" +
                          "  Stream #0:1: Video: h264, yuv420p, 160x120\n  Stream #0:2: Audio: aac\n",
            MeasureReport = "frame=  250 fps=0 time=00:00:10.00"
        };
        runner.Writes.Add(args => IsFrame(args) || args.Any(a => a.Contains("cover_")) ? [11] : args.Contains("-ar") ? [21] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([0]), "video/x-matroska", CancellationToken.None);

        sample.Failure.Should().BeNull();
        runner.Calls.Where(c => IsFrame(c.Arguments)).Should().OnlyContain(c => c.Arguments.Contains("0:1"));
        runner.Calls.Should().Contain(c => c.Arguments.Last().Contains("cover_") && c.Arguments.Contains("0:0"));
        runner.Probes.Where(c => c.Arguments.Contains("-select_streams"))
            .Where(c => c.Arguments[c.Arguments.ToList().IndexOf("-select_streams") + 1] != "a")
            .Should().OnlyContain(c => c.Arguments[c.Arguments.ToList().IndexOf("-select_streams") + 1] == "1", "the audio end is the only probe of another stream");
    }

    [Fact]
    public async Task An_image_demuxer_with_audio_or_an_unidentified_container_is_refused_without_sampling()
    {
        foreach (string report in new[]
                 {
                     "Input #0, gif, from 'x':\n  Stream #0:0: Video: gif, bgra\n  Stream #0:1: Audio: aac\n",
                     "Input #0, , from 'x':\n  Stream #0:0: Video: h264\n  Stream #0:1: Audio: aac\n"
                 })
        {
            FakeRunner runner = new() { ProbeReport = report };
            ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options())
                .SampleAsync(new MemoryStream([0]), "image/gif", CancellationToken.None);

            sample.Failure.Should().NotBeNull();
            sample.Deterministic.Should().BeTrue();
            sample.Images.Should().BeEmpty();
            runner.Calls.Should().BeEmpty();
        }
    }

    private const string VideoWithOneAudio =
        "  Duration: 00:10:00.00, start: 0.000000, bitrate: 900 kb/s\n" +
        "  Stream #0:0: Video: h264, yuv420p, 1920x1080\n  Stream #0:1: Audio: aac, 48000 Hz, stereo\n";

    [Fact]
    public async Task Frames_are_spread_over_the_whole_duration_not_the_first_forty_seconds()
    {
        FakeRunner runner = new() { ProbeReport = VideoWithOneAudio };
        runner.Writes.Add(args => IsFrame(args) ? [1] : args.Contains("-ar") ? [2] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options(), new Random(7), TimeSpan.FromMinutes(5));

        await sampler.SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        List<double> times = runner.Calls.Where(c => c.Arguments.Contains("-ss")).Select(c => double.Parse(SeekOf(c), CultureInfo.InvariantCulture)).ToList();
        times.Should().HaveCount(20, "the cap is kept");
        for (int block = 0; block < 10; block++)
            times.Count(t => t + 0.001 >= block * 60 && t + 0.001 < (block + 1) * 60).Should().Be(2, "one draw by time and one by position inside each 60 s tenth");
        times[^1].Should().BeGreaterThan(540);
    }

    [Fact]
    public async Task A_short_video_keeps_the_configured_interval()
    {
        FakeRunner runner = new()
        {
            ProbeReport = VideoWithOneAudio.Replace("00:10:00.00", "00:00:20.00"),
            MeasureReport = "frame=  500 fps=0 time=00:00:20.00"
        };
        runner.Writes.Add(args => IsFrame(args) ? [1] : args.Contains("-ar") ? [2] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        await sampler.SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        runner.Calls.Count(c => IsFrame(c.Arguments)).Should().Be(10, "20 s at the configured 2 s interval");
    }

    [Fact]
    public async Task Video_inside_an_audio_container_is_sampled_as_video()
    {
        FakeRunner runner = new() { ProbeReport = VideoWithOneAudio };
        runner.Writes.Add(args => IsFrame(args) ? [1] : args.Contains("-ar") ? [2] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([0]), "audio/mp4", CancellationToken.None);

        sample.Kind.Should().Be(ModerationContentKind.Video);
        sample.Images.Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_animated_image_is_sampled_as_video_not_on_one_frame()
    {
        FakeRunner runner = new()
        {
            ProbeReport = "  Duration: 00:00:04.00, start: 0.0\n  Stream #0:0: Video: gif, bgra, 200x200\n",
            MeasureReport = "frame=   20 fps=0 time=00:00:04.00"
        };
        runner.Writes.Add(args => IsFrame(args) ? [1, 2, 3] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([0]), "image/gif", CancellationToken.None);

        sample.Kind.Should().Be(ModerationContentKind.Video);
        sample.Images.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_still_image_stays_one_frame()
    {
        FakeRunner runner = new()
        {
            ProbeReport = "  Duration: N/A, bitrate: N/A\n  Stream #0:0: Video: png, rgba, 20x20\n",
            MeasureReport = "frame=    1 fps=0 time=00:00:00.04"
        };
        runner.Writes.Add(args => args.Any(a => a.Contains("scale=")) ? [1] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        (await sampler.SampleAsync(new MemoryStream([0]), "image/png", CancellationToken.None)).Kind.Should().Be(ModerationContentKind.Image);
    }

    [Fact]
    public async Task More_than_one_audio_track_is_a_deterministic_failure()
    {
        FakeRunner runner = new()
        {
            ProbeReport = VideoWithOneAudio + "  Stream #0:2: Audio: aac, 48000 Hz, stereo\n"
        };
        runner.Writes.Add(args => IsFrame(args) ? [1] : args.Contains("-ar") ? [2] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        sample.Failure.Should().Contain("audio track");
        sample.Deterministic.Should().BeTrue();
    }

    [Fact]
    public async Task A_video_whose_audio_track_cannot_be_extracted_is_a_failure_not_silence()
    {
        FakeRunner runner = new() { ProbeReport = VideoWithOneAudio };
        runner.Writes.Add(args => IsFrame(args) ? [1] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        sample.Audio.Should().BeNull();
        sample.Failure.Should().NotBeNull("the track exists but was not extracted");
    }

    [Fact]
    public async Task A_video_with_no_audio_stream_has_no_failure()
    {
        FakeRunner runner = new()
        {
            ProbeReport = "  Duration: 00:00:10.00\n  Stream #0:0: Video: h264, yuv420p\n",
            MeasureReport = "frame=  250 fps=0 time=00:00:10.00"
        };
        runner.Writes.Add(args => IsFrame(args) ? [1] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        (await sampler.SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None)).Failure.Should().BeNull();
    }

    [Fact]
    public async Task An_animated_image_under_a_fifth_of_a_second_still_goes_down_the_video_path_by_frame_count()
    {
        FakeRunner runner = new()
        {
            ProbeReport = "  Duration: 00:00:00.10\n  Stream #0:0: Video: gif, bgra, 20x20\n",
            MeasureReport = "frame=    2 fps=0 time=00:00:00.10"
        };
        runner.Writes.Add(args => IsFrame(args) ? [1, 2] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        (await sampler.SampleAsync(new MemoryStream([0]), "image/gif", CancellationToken.None)).Kind.Should().Be(ModerationContentKind.Video);
    }

    [Fact]
    public async Task A_crafted_short_declared_duration_does_not_keep_frames_at_the_start()
    {
        FakeRunner runner = new()
        {
            ProbeReport = "  Duration: 00:00:10.00\n  Stream #0:0: Video: h264\n  Stream #0:1: Audio: aac\n",
            MeasureReport = "frame=  15000 fps=0 time=00:10:00.00",
            FfprobeReport = DeclaredReport(10, 10)
        };
        runner.Writes.Add(args => IsFrame(args) ? [1] : args.Contains("-ar") ? [2] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        sample.Failure.Should().Contain("duration", "a declaration far from the real length is unclassified");
        sample.Deterministic.Should().BeTrue();
        sample.Images.Should().HaveCount(20, "the frames are kept and spread over the MEASURED length");
    }

    [Fact]
    public async Task A_video_that_cannot_be_measured_is_a_failure()
    {
        FakeRunner runner = new() { ProbeReport = VideoWithOneAudio, MeasureReport = null };
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        (await sampler.SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None)).Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task More_than_one_video_stream_is_a_deterministic_failure()
    {
        FakeRunner runner = new()
        {
            ProbeReport = VideoWithOneAudio + "  Stream #0:2: Video: h264, yuv420p\n"
        };
        runner.Writes.Add(args => IsFrame(args) ? [1] : args.Contains("-ar") ? [2] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        sample.Failure.Should().Contain("video stream");
        sample.Deterministic.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_probe_with_a_failed_audio_extraction_is_a_failure_never_frames_only()
    {
        FakeRunner runner = new() { ProbeReport = string.Empty };
        runner.Writes.Add(args => IsFrame(args) ? [1] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        sample.Images.Should().BeEmpty("an unreadable layout is not sampled");
        sample.Failure.Should().NotBeNull("with no stream report the layout is not identified");
    }

    [Fact]
    public async Task A_video_whose_streams_cannot_be_read_is_refused_even_when_frames_and_audio_came_out()
    {
        FakeRunner runner = new() { ProbeReport = string.Empty };
        runner.Writes.Add(args => IsFrame(args) ? [1] : args.Contains("-ar") ? [2] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        sample.Images.Should().BeEmpty("a layout that is not positively identified is not sampled");
        sample.Failure.Should().Contain("streams", "a one-pass allow needs a stream report");
        sample.Deterministic.Should().BeTrue();
    }

    [Fact]
    public async Task A_video_with_no_measured_time_is_refused_not_sampled_on_its_declared_length()
    {
        FakeRunner runner = new()
        {
            ProbeReport = VideoWithOneAudio,
            MeasureReport = "frame=  250 fps=0"
        };
        runner.Writes.Add(args => IsFrame(args) ? [1] : args.Contains("-ar") ? [2] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        sample.Failure.Should().Contain("length");
        sample.Deterministic.Should().BeTrue();
        // The declared ten minutes is never used to pick frames: with no packet times there are none.
        runner.Calls.Where(c => IsFrame(c.Arguments)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_audio_file_whose_streams_cannot_be_read_is_refused_never_a_one_pass_allow()
    {
        FakeRunner runner = new() { ProbeReport = string.Empty };
        runner.Writes.Add(args => args.Contains("-ar") ? [7, 8] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([1]), "audio/mpeg", CancellationToken.None);

        sample.Failure.Should().NotBeNull("two audio tracks cannot be ruled out without a stream report");
    }

    [Fact]
    public async Task One_failed_frame_keeps_the_others_and_reports_the_failure()
    {
        FakeRunner runner = new()
        {
            ProbeReport = VideoWithOneAudio.Replace("00:10:00.00", "00:00:10.00", StringComparison.Ordinal),
            MeasureReport = "frame=  250 fps=0 time=00:00:10.00"
        };
        int call = 0;
        runner.Writes.Add(args => IsFrame(args) ? (++call is 2 or 3 ? [] : [1]) : args.Contains("-ar") ? [2] : []);
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        sample.Images.Should().HaveCount(4);
        sample.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task A_real_ffmpeg_samples_an_animated_gif_as_video_and_a_still_png_as_an_image_when_ffmpeg_is_available()
    {
        if (!OnPath("ffmpeg")) return;

        string dir = Path.Combine(Path.GetTempPath(), "sampler-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            FfmpegModerationMediaSampler sampler = new(new SystemProcessRunner(), Options());

            string gif = Path.Combine(dir, "a.gif");
            RunFfmpeg($"-f lavfi -i testsrc=size=64x64:rate=5 -t 2 {gif}");
            ModerationMediaSample animated = await sampler.SampleAsync(File.OpenRead(gif), "image/gif", CancellationToken.None);
            animated.Kind.Should().Be(ModerationContentKind.Video);
            animated.Images.Should().NotBeEmpty();

            string png = Path.Combine(dir, "a.png");
            RunFfmpeg($"-f lavfi -i testsrc=size=64x64:rate=5 -frames:v 1 {png}");
            (await sampler.SampleAsync(File.OpenRead(png), "image/png", CancellationToken.None)).Kind.Should().Be(ModerationContentKind.Image);

            string bmp = Path.Combine(dir, "a.bmp");
            RunFfmpeg($"-f lavfi -i testsrc=size=64x64:rate=5 -frames:v 1 {bmp}");
            ModerationMediaSample still = await sampler.SampleAsync(File.OpenRead(bmp), "image/bmp", CancellationToken.None);
            still.Failure.Should().BeNull("the bmp demuxer is whitelisted");
            still.Images.Should().ContainSingle();

            string mp4 = Path.Combine(dir, "a.mp4");
            RunFfmpeg($"-f lavfi -i testsrc=size=320x240:rate=25 -f lavfi -i sine -t 4 -c:v libx264 -pix_fmt yuv420p -c:a aac {mp4}");
            ModerationMediaSample video = await sampler.SampleAsync(File.OpenRead(mp4), "video/mp4", CancellationToken.None);
            video.Kind.Should().Be(ModerationContentKind.Video);
            video.Failure.Should().BeNull();
            video.Images.Should().NotBeEmpty();
            video.Audio.Should().NotBeNull();

            string silent = Path.Combine(dir, "silent.mp4");
            RunFfmpeg($"-f lavfi -i testsrc=size=320x240:rate=25 -t 2 -c:v libx264 -pix_fmt yuv420p {silent}");
            ModerationMediaSample noAudio = await sampler.SampleAsync(File.OpenRead(silent), "video/mp4", CancellationToken.None);
            noAudio.Failure.Should().BeNull("a video with no audio stream is judged on its frames");
            noAudio.Audio.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        static void RunFfmpeg(string args)
        {
            using System.Diagnostics.Process p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("ffmpeg", "-y -loglevel error " + args) { RedirectStandardError = true })!;
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(p.StandardError.ReadToEnd());
        }
    }

    [Fact]
    public async Task An_image_with_a_video_stream_that_cannot_be_measured_is_a_failure_not_one_frame()
    {
        FakeRunner runner = new()
        {
            ProbeReport = "  Duration: N/A\n  Stream #0:0: Video: webp, none\n",
            MeasureReport = null
        };
        FfmpegModerationMediaSampler sampler = new(runner, Options());

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([0]), "image/webp", CancellationToken.None);

        sample.Failure.Should().NotBeNull();
        sample.Images.Should().BeEmpty();
    }

    [Fact]
    public async Task Real_animated_and_still_images_are_told_apart_by_decoded_frames_when_ffmpeg_is_available()
    {
        if (!OnPath("ffmpeg")) return;

        string dir = Path.Combine(Path.GetTempPath(), "sampler-anim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            FfmpegModerationMediaSampler sampler = new(new SystemProcessRunner(), Options());
            const string anim = "-f lavfi -i testsrc=size=64x64:rate=5 -t 2 ";
            const string still = "-f lavfi -i testsrc=size=64x64:rate=5 -frames:v 1 ";

            // Animated GIF and APNG are videos; their stills are images.
            foreach ((string name, string mime, string animArgs, string stillArgs) in new[]
            {
                ("g.gif", "image/gif", anim, still),
                ("p.png", "image/png", anim + "-f apng ", still + "-f apng ")
            })
            {
                string animated = Path.Combine(dir, "a-" + name);
                string single = Path.Combine(dir, "s-" + name);
                RunFfmpeg(animArgs + animated);
                RunFfmpeg(stillArgs + single);

                ModerationMediaSample a = await sampler.SampleAsync(File.OpenRead(animated), mime, CancellationToken.None);
                a.Kind.Should().Be(ModerationContentKind.Video, name + " animated");
                a.Images.Should().NotBeEmpty();
                ModerationMediaSample s1 = await sampler.SampleAsync(File.OpenRead(single), mime, CancellationToken.None);
                s1.Kind.Should().Be(ModerationContentKind.Image, name + " still");
                s1.Images.Should().ContainSingle();
            }

            // WebP: the still decodes. An animated WebP is never judged on one frame: this ffmpeg cannot decode
            // the animation chunks, so it is a failure (unclassified), not a single-frame image.
            string stillWebp = Path.Combine(dir, "s.webp");
            string animWebp = Path.Combine(dir, "a.webp");
            if (!TryFfmpeg(still + "-c:v libwebp " + stillWebp) || !TryFfmpeg(anim + "-c:v libwebp -loop 0 " + animWebp))
                return; // this ffmpeg cannot write WebP: nothing to test, and nothing is faked

            ModerationMediaSample ws = await sampler.SampleAsync(File.OpenRead(stillWebp), "image/webp", CancellationToken.None);
            ws.Failure.Should().BeNull("webp_pipe is whitelisted");
            ws.Images.Should().ContainSingle();
            ModerationMediaSample wa = await sampler.SampleAsync(File.OpenRead(animWebp), "image/webp", CancellationToken.None);
            (wa.Failure is not null || wa.Kind == ModerationContentKind.Video).Should().BeTrue("never one frame of an animation");
            (wa.Kind == ModerationContentKind.Image && wa.Images.Count == 1 && wa.Failure is null).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        static void RunFfmpeg(string args)
        {
            if (!TryFfmpeg(args)) throw new InvalidOperationException("ffmpeg failed: " + args);
        }

        static bool TryFfmpeg(string args)
        {
            using System.Diagnostics.Process p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("ffmpeg", "-y -loglevel error " + args) { RedirectStandardError = true })!;
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
    }

    private static byte[] Wav(double seconds) => new byte[ModerationMediaSample.WavHeaderBytes + (int)(seconds * ModerationMediaSample.WavBytesPerSecond)];

    private static string Packets(IEnumerable<double> times, double lastDuration = 0.033333)
    {
        List<double> list = times.ToList();
        StringBuilder csv = new();
        for (int i = 0; i < list.Count; i++)
        {
            double duration = i + 1 < list.Count ? list[i + 1] - list[i] : lastDuration;
            string t = list[i].ToString("0.000000", CultureInfo.InvariantCulture);
            csv.Append(t).Append(',').Append(t).Append(',').AppendLine(duration.ToString("0.000000", CultureInfo.InvariantCulture));
        }

        return csv.ToString();
    }

    private static FakeRunner VideoRunner(string packets, string duration = "00:10:00.00", string demuxer = "mov,mp4,m4a", string extra = "", string audio = "Audio: aac, 48000 Hz, mono")
    {
        FakeRunner runner = new()
        {
            ProbeReport = $"Input #0, {demuxer}, from 'x':\n  Duration: {duration}, start: 0.000000\n  Stream #0:0: Video: h264, yuv420p\n  Stream #0:1: {audio}\n{extra}",
            PacketReport = packets
        };
        runner.Writes.Add(args => IsFrame(args) ? [1] : args.Contains("-ar") ? Wav(1) : []);
        return runner;
    }

    private static List<double> Seeks(FakeRunner runner) =>
        runner.Calls.Where(c => c.Arguments.Contains("-ss")).Select(c => double.Parse(SeekOf(c), CultureInfo.InvariantCulture)).ToList();

    [Fact]
    public async Task A_timestamp_gap_does_not_hide_the_frames_before_it()
    {
        // 60 s at 30 fps, then the last frame an hour later (the one-line remux). By the clock all 20 samples fall in the gap.
        List<double> times = Enumerable.Range(0, 1800).Select(i => i / 30.0).Append(3660).ToList();
        FakeRunner runner = VideoRunner(Packets(times), "01:01:00.00");
        FfmpegModerationMediaSampler sampler = new(runner, Options(), new Random(3), TimeSpan.FromMinutes(5));

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        List<double> seeks = Seeks(runner);
        seeks.Where(t => t < 61).Should().HaveCountGreaterThan(8, "the frames are taken by packet position, so the first minute is seen");
        seeks.Distinct().Should().HaveCount(seeks.Count, "a frame drawn twice is judged once");
        seeks.Should().Contain(t => t > 59.9 && t < 60, "the frame that stays on screen for an hour is always judged");
        sample.Failure.Should().BeNull("a long hold is judged, not refused");
        sample.Images.Should().HaveCount(seeks.Count);
    }

    [Fact]
    public void Half_the_packets_are_drawn_by_time_slice_and_half_by_stratum_of_positions_and_a_new_draw_differs()
    {
        // Evenly spaced packets: a time slice and a position stratum are the same span, so every draw sits in its tenth.
        FfmpegModerationMediaSampler.PacketList packets = FfmpegModerationMediaSampler.ParsePackets(Packets(Enumerable.Range(0, 1000).Select(i => i * 0.6)));

        IReadOnlyList<int> first = FfmpegModerationMediaSampler.PickPackets(packets, 2, 20, new Random(11));
        IReadOnlyList<int> again = FfmpegModerationMediaSampler.PickPackets(packets, 2, 20, new Random(11));
        IReadOnlyList<int> other = FfmpegModerationMediaSampler.PickPackets(packets, 2, 20, new Random(12));

        first.Should().HaveCount(20);
        first.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        for (int i = 0; i < 10; i++)
            first.Count(p => p >= i * 100 && p < (i + 1) * 100).Should().Be(2, "one by time and one by position in each tenth");
        again.Should().Equal(first, "the same random source gives the same draw");
        other.Should().NotEqual(first, "a new judgement draws afresh");
    }

    [Fact]
    public void Few_packets_are_all_judged_and_a_short_video_keeps_the_interval()
    {
        FfmpegModerationMediaSampler.PickPackets(FfmpegModerationMediaSampler.ParsePackets(Packets([0])), 2, 20, new Random(1)).Should().Equal(0);
        FfmpegModerationMediaSampler.PickPackets(FfmpegModerationMediaSampler.ParsePackets(Packets(Enumerable.Range(0, 15).Select(i => i * 60.0))), 2, 20, new Random(1))
            .Should().HaveCount(15, "15 packets are fewer than the 20 frames, so none can hide");
    }

    [Fact]
    public async Task A_frame_other_than_the_one_drawn_is_a_final_failure_with_the_frames_kept_and_a_frame_or_two_late_passes()
    {
        string packets = Packets(Enumerable.Range(0, 1500).Select(i => i * 0.04));

        // An AVI or ASF can land a frame or two late: the fast seek is not the packet drawn, so it is decoded from the start and is.
        FakeRunner late = VideoRunner(packets, "00:01:00.00");
        int lateCalls = 0;
        late.FrameTime = seek => ++lateCalls <= 5 ? seek + 0.0002 + 0.08 : seek + 0.0002;
        ModerationMediaSample ok = await new FfmpegModerationMediaSampler(late, Options()).SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);
        ok.Failure.Should().BeNull("a frame late is decoded again from the start and is the drawn one");
        late.SlowCalls.Should().BeGreaterThan(0);

        // A keyframe table that lies hands back the keyframe 30 s away.
        FakeRunner lying = VideoRunner(packets, "00:01:00.00");
        lying.FrameTime = _ => 59.96;
        lying.SlowFrameTime = from => from is null ? 0 : 59.96;
        ModerationMediaSample bad = await new FfmpegModerationMediaSampler(lying, Options()).SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);
        bad.Failure.Should().Contain("other than the one drawn");
        bad.Deterministic.Should().BeTrue();
        bad.Images.Should().NotBeEmpty("ingest still judges the frames it got");

        // A frame that reports no time cannot be shown to be the one drawn.
        FfmpegModerationMediaSampler.FrameIsTheDrawnOne(FfmpegModerationMediaSampler.ParsePackets(packets), 10, null).Should().BeFalse();
        FfmpegModerationMediaSampler.ParseFrameTime("frame:0 pts:5 pts_time:1.5\nrb=1\nframe:1 pts_time:9").Should().Be(1.5);
        FfmpegModerationMediaSampler.ParseFrameTime("nothing").Should().BeNull();
    }

    [Fact]
    public async Task A_mismatched_frame_is_decoded_from_the_start_up_to_a_cap_and_more_than_the_cap_is_final()
    {
        string packets = Packets(Enumerable.Range(0, 1500).Select(i => i * 0.04));

        // The first few fast seeks come back late: each packet is decoded from the start, and the file passes with 20 frames.
        int calls = 0;
        FakeRunner few = VideoRunner(packets, "00:01:00.00");
        few.FrameTime = seek => ++calls <= FfmpegModerationMediaSampler.MaxSlowDecodes ? seek + 0.0002 + 0.12 : seek + 0.0002;
        ModerationMediaSample ok = await new FfmpegModerationMediaSampler(few, Options(), new Random(2), FfmpegModerationMediaSampler.TotalDeadline).SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);
        ok.Failure.Should().BeNull("a mismatch decoded from the start is the packet drawn");
        ok.Images.Should().HaveCount(20);
        few.SlowCalls.Should().Be(FfmpegModerationMediaSampler.MaxSlowDecodes);

        // One more than the cap is the failure path, final, and the number of decodes from the start stays at the cap.
        int again = 0;
        FakeRunner many = VideoRunner(packets, "00:01:00.00");
        many.FrameTime = seek => ++again <= FfmpegModerationMediaSampler.MaxSlowDecodes + 1 ? seek + 0.0002 + 0.12 : seek + 0.0002;
        ModerationMediaSample bad = await new FfmpegModerationMediaSampler(many, Options(), new Random(2), FfmpegModerationMediaSampler.TotalDeadline).SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);
        bad.Failure.Should().Contain("other than the one drawn");
        bad.Deterministic.Should().BeTrue();
        many.SlowCalls.Should().Be(FfmpegModerationMediaSampler.MaxSlowDecodes);

        // A frame from the start that is not the drawn one and not the file's first frame is final on its first occurrence, no redraw.
        FakeRunner far = VideoRunner(packets, "00:01:00.00");
        far.FrameTime = seek => seek + 0.0002 + 0.12;
        far.SlowFrameTime = from => from is null ? 0 : from.Value + 1000;
        ModerationMediaSample gone = await new FfmpegModerationMediaSampler(far, Options(), new Random(2), FfmpegModerationMediaSampler.TotalDeadline).SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);
        gone.Failure.Should().Contain("other than the one drawn");
        gone.Deterministic.Should().BeTrue();
        Seeks(far).Should().HaveCount(20, "no place is drawn again after a far mismatch");

        // A fast frame that is correct in time takes no decode from the start.
        FakeRunner fine = VideoRunner(packets, "00:01:00.00");
        (await new FfmpegModerationMediaSampler(fine, Options(), new Random(2), FfmpegModerationMediaSampler.TotalDeadline).SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None)).Failure.Should().BeNull();
        fine.SlowCalls.Should().Be(0);

        // The frame of the packet drawn is told from the one next to it, even 1 ms apart.
        FfmpegModerationMediaSampler.PacketList tight = FfmpegModerationMediaSampler.ParsePackets(Packets([0, 0.001, 0.002, 0.003]));
        FfmpegModerationMediaSampler.FrameIsTheDrawnOne(tight, 1, 0.001).Should().BeTrue();
        FfmpegModerationMediaSampler.FrameIsTheDrawnOne(tight, 1, 0.002).Should().BeFalse("packet 2 is the one it is");
        FfmpegModerationMediaSampler.FrameIsTheDrawnOne(tight, 1, 0.0035).Should().BeFalse();
    }

    [Fact]
    public async Task A_drawn_packet_before_anything_the_file_decodes_is_redrawn_and_a_later_first_frame_is_the_only_exception()
    {
        // The first 100 packets never decode (a packet-dropping filter): the first frame the file decodes is at 4.0 s.
        string packets = Packets(Enumerable.Range(0, 1500).Select(i => i * 0.04));
        FakeRunner lead = VideoRunner(packets, "00:01:00.00");
        lead.FirstFrameTime = 4.0;
        lead.FrameTime = seek => seek + 0.0002 < 3.99 ? 4.0 : seek + 0.0002;
        lead.SlowFrameTime = from => from is null ? 4.0 : from.Value < 3.99 ? 4.0 : from.Value;
        ModerationMediaSample ok = await new FfmpegModerationMediaSampler(lead, Options(), new Random(5), FfmpegModerationMediaSampler.TotalDeadline).SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);
        ok.Failure.Should().BeNull();
        ok.Images.Should().HaveCount(20);
    }

    [Fact]
    public async Task A_real_vp9_file_whose_keyframe_table_lies_is_decoded_from_the_start_and_passes_with_the_true_frames()
    {
        if (!OnPath("ffmpeg")) return;

        string dir = Path.Combine(Path.GetTempPath(), "sampler-stss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 12 s at 10 fps, 64x64: red, then one blue frame at the very end, those two being the only keyframes.
            string clean = Path.Combine(dir, "clean.mp4");
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg",
                       "-y -loglevel error -f lavfi -i color=c=red:s=64x64:r=10:d=11.9 -f lavfi -i color=c=blue:s=64x64:r=10:d=0.1 "
                       + "-filter_complex concat=n=2:v=1:a=0 -c:v libvpx-vp9 -g 9999 -keyint_min 9999 -force_key_frames 11.9 "
                       + "-deadline realtime -cpu-used 8 " + clean)
            { RedirectStandardError = true })!)
            {
                p.WaitForExit();
                if (p.ExitCode != 0) return; // this ffmpeg has no VP9 encoder: nothing to test, nothing faked
            }

            byte[] bytes = File.ReadAllBytes(clean);
            // Renaming the sync-sample table makes every packet claim to be a keyframe, so seeks land on the last one.
            byte[] lying = bytes.ToArray();
            int at = lying.AsSpan().IndexOf("stss"u8);
            at.Should().BeGreaterThan(0);
            "free"u8.CopyTo(lying.AsSpan(at));

            FfmpegModerationMediaSampler sampler = new(new SystemProcessRunner(), Options());
            ModerationMediaSample honest = await sampler.SampleAsync(new MemoryStream(bytes), "video/mp4", CancellationToken.None);
            honest.Failure.Should().BeNull("the table is intact, so every seek lands on the frame drawn");

            ModerationMediaSample refused = await sampler.SampleAsync(new MemoryStream(lying), "video/mp4", CancellationToken.None);
            refused.Failure.Should().BeNull("a seek that lands on the wrong frame is decoded again from the start, so the frames are the ones drawn");
            refused.Images.Should().HaveCount(6, "12 s at one frame every 2 s");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static FfmpegModerationMediaSampler.PacketList Padded(int tiny, double tinyStep, int red, double[] paddingSeconds)
    {
        // The simulated forms: tiny packets, `red` packets of 59 s each, the padding, then a closing frame.
        List<double> times = [];
        double t = 0;
        for (int i = 0; i < tiny; i++) { times.Add(t); t += tinyStep; }
        for (int i = 0; i < red; i++) { times.Add(t); t += 59; }
        foreach (double pad in paddingSeconds) { times.Add(t); t += pad; }
        times.Add(t);
        return FfmpegModerationMediaSampler.ParsePackets(Packets(times, 0.001));
    }

    [Theory]
    [InlineData(10000, 25, 1, 86400.0, 380)]
    [InlineData(10000, 25, 1, 604800.0, 380)]
    [InlineData(10000, 25, 11, 3600.0, 280)]
    public void Padding_a_video_with_long_stills_does_not_dilute_the_draws_that_reach_the_content(int tiny, int red, int stills, double stillSeconds, int atLeast)
    {
        FfmpegModerationMediaSampler.PacketList packets = Padded(tiny, 0.001, red, Enumerable.Repeat(stillSeconds, stills).ToArray());
        int reached = 0;
        for (int seed = 0; seed < 400; seed++)
        {
            IReadOnlyList<int> picked = FfmpegModerationMediaSampler.PickPackets(packets, 2, 20, new Random(seed));
            if (picked.Any(i => i >= tiny && i < tiny + red)) reached++;
        }

        reached.Should().BeGreaterThan(atLeast, "the 25 minutes that play are drawn from; ten stills are held, one more is only its real time (over 12 h it is refused)");
    }

    [Fact]
    public async Task An_audio_list_that_reaches_the_cap_says_the_video_is_too_long_and_one_under_it_passes()
    {
        string video = Packets(Enumerable.Range(0, 40).Select(i => (double)i));
        string audio = Packets(Enumerable.Range(0, 101).Select(i => i * 0.02));

        // Ten packets under the cap: judged. The cap of the test is 100; the real one is six million (12 h of AAC is 2.03 million).
        FakeRunner under = VideoRunner(video, "00:00:40.00");
        under.AudioPacketReport = string.Join('\n', audio.Split('\n').Take(90));
        ModerationMediaSample ok = await new FfmpegModerationMediaSampler(under, Options(), new Random(1), FfmpegModerationMediaSampler.TotalDeadline, 100)
            .SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);
        ok.Failure.Should().BeNull();

        // One more than the cap: the member is told the video is too long, the refusal is final, and it is flagged as length.
        FakeRunner over = VideoRunner(video, "00:00:40.00");
        over.AudioPacketReport = audio;
        ModerationMediaSample bad = await new FfmpegModerationMediaSampler(over, Options(), new Random(1), FfmpegModerationMediaSampler.TotalDeadline, 100)
            .SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);
        bad.Failure.Should().Be(FfmpegModerationMediaSampler.TooLongFailure);
        bad.Deterministic.Should().BeTrue();
        bad.TooLong.Should().BeTrue();

        // The list asked of ffprobe is one longer than the cap, so exactly the cap is told from more than it.
        over.Probes.Where(c => c.Arguments.Contains("-select_streams"))
            .Should().Contain(c => c.Arguments.Contains("%+#101"));

        // The production cap leaves room for 12 hours of the ordinary audio packets.
        FfmpegModerationMediaSampler.MaxPackets.Should().BeGreaterThan(2_160_000, "Opus at 20 ms is 2.16 million packets in 12 hours");
    }

    [Fact]
    public async Task A_packet_list_is_read_line_by_line_and_the_end_of_the_audio_needs_only_the_last_packets()
    {
        // The default streaming path of a runner that cannot stream splits the output; a streaming one hands the same lines.
        List<string> lines = [];
        ProcessRunResult result = await ((IProcessRunner)new FakeRunner { PacketReport = "0.0,0.0,0.02\n0.02,0.02,0.02\n" })
            .RunStreamingAsync("ffprobe", ["-show_entries", "packet=pts_time"], TimeSpan.FromSeconds(1), lines.Add, CancellationToken.None);
        result.StdOut.Should().BeEmpty("the lines were handed over, not kept");
        lines.Where(l => l.Length > 0).Should().Equal("0.0,0.0,0.02", "0.02,0.02,0.02");

        SystemProcessRunner real = new();
        List<string> printed = [];
        if (OnPath("sh"))
        {
            ProcessRunResult run = await real.RunStreamingAsync("sh", ["-c", "printf 'a\\nb\\nc\\n'"], TimeSpan.FromSeconds(10), printed.Add, CancellationToken.None);
            run.Succeeded.Should().BeTrue();
            run.StdOut.Should().BeEmpty();
            printed.Should().Equal("a", "b", "c");
        }
    }

    [Fact]
    public async Task A_video_longer_than_twelve_hours_is_a_final_refusal_but_is_still_sampled()
    {
        FakeRunner runner = VideoRunner(Packets([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 12 * 3600.0 + 100]), "00:10:00.00");
        ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options()).SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        sample.Failure.Should().Contain("longer than 12 hours");
        sample.Deterministic.Should().BeTrue();
        sample.Images.Should().NotBeEmpty("ingest judges what it got");
    }

    [Fact]
    public async Task A_long_hold_in_a_recording_slides_and_a_still_over_long_audio_are_still_judged()
    {
        // 11 hours of 30 s slides is under the limit and is judged, not refused.
        FakeRunner runner = VideoRunner(Packets(Enumerable.Range(0, 1320).Select(i => i * 30.0)), "11:00:00.00");
        ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options()).SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);
        sample.Failure.Should().BeNull();
        sample.Images.Should().HaveCount(20);
    }

    [Theory]
    [InlineData("00:00:01,000 --> 00:00:02,000", false)]
    [InlineData("00:00:01.000 --> 00:00:02.000 align:start position:10%", false)]
    [InlineData("00:00:01,000 --> 00:00:02,000 hidden words", true)]
    [InlineData("00:00:01,000 --> 00:00:02,000 align:start say this", true)]
    [InlineData("00:00:01,000 --> 00:00:02,000 hello:world", true)]
    [InlineData("00:00:01.000 --> 00:00:02.000 line:BUY size:DRUGS", true)]
    [InlineData("00:00:01.000 --> 00:00:02.000 align:SELL", true)]
    [InlineData("00:00:01.000 --> 00:00:02.000 line:-1 position:10% size:80% vertical:rl align:center", false)]
    [InlineData("00:00:01.000 --> 00:00:02.000 line:50%,center position:20%,line-left", false)]
    [InlineData("00:00:01.000 --> 00:00:02.000 region:buy", true)]
    public void Only_a_line_that_is_exactly_the_timing_format_is_dropped_from_subtitles(string line, bool kept)
    {
        string text = FfmpegModerationMediaSampler.SubtitlePlainText("1\n" + line + "\nspoken\n");
        text.Contains("spoken", StringComparison.Ordinal).Should().BeTrue();
        text.Contains("-->", StringComparison.Ordinal).Should().Be(kept, line);
    }

    [Fact]
    public void A_line_of_digits_is_dropped_only_as_a_cue_number_before_a_time_line()
    {
        string text = FfmpegModerationMediaSampler.SubtitlePlainText(
            "1\n00:00:01,000 --> 00:00:02,000\n5551234567\n\n2\n00:00:03,000 --> 00:00:04,000\ncall\n5559876543\n");
        text.Should().Contain("5551234567").And.Contain("5559876543").And.Contain("call");
        text.Split('\n').Select(l => l.Trim()).Should().NotContain("1").And.NotContain("2");
    }

    [Fact]
    public void Disk_fault_text_is_read_from_ffmpegs_own_error_not_from_printed_metadata()
    {
        string metadata = "Input #0, mp3, from 'x':\n  Metadata:\n    title           : No space left on device\nStream mapping:\nError opening input: Invalid data found";
        new ProcessRunResult(1, string.Empty, metadata, false).Transient.Should().BeFalse("the text is the file's title");
        new ProcessRunResult(1, string.Empty, "Input #0, mp3, from 'x':\nav_interleaved_write_frame(): No space left on device", false).Transient.Should().BeTrue();
    }

    [Fact]
    public async Task Variable_frame_rate_and_a_still_held_with_long_audio_keep_passing()
    {
        // Slides held 30 s; a screen recording that sat still for 70 s of 370; a still image over 3 minutes of audio.
        Dictionary<string, (IEnumerable<double> Times, double Last)> cases = new()
        {
            ["slides"] = (Enumerable.Range(0, 100).Select(i => i * 30.0), 30),
            ["screen"] = (Enumerable.Range(0, 300).Select(i => i * 1.0).Concat([370.0]), 0.5),
            ["still-1-packet"] = ([0.0], 180),
            ["still-1fps"] = (Enumerable.Range(0, 180).Select(i => (double)i), 1)
        };
        foreach ((string name, (IEnumerable<double> times, double last)) in cases)
        {
            List<double> list = times.ToList();
            double length = list[^1] + last;
            FakeRunner runner = VideoRunner(Packets(list, last), TimeSpan.FromSeconds(length).ToString(@"hh\:mm\:ss"));
            ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options()).SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

            sample.Failure.Should().BeNull(name + " is ordinary");
            sample.Images.Should().NotBeEmpty(name);
        }
    }

    [Theory]
    [InlineData("mpegts", true)]
    [InlineData("mpeg", true)]
    [InlineData("mxf", false)]
    [InlineData("matroska,webm", false)]
    public async Task Containers_whose_seek_returns_the_keyframe_are_refused_but_still_sampled(string demuxer, bool refused)
    {
        FakeRunner runner = VideoRunner(Packets(Enumerable.Range(0, 250).Select(i => (i * 0.04) + 1.4)), "00:00:10.00", demuxer);
        ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options()).SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        sample.Images.Should().NotBeEmpty("ingest judges what it got");
        if (refused)
        {
            sample.Failure.Should().Contain("keyframe");
            sample.Deterministic.Should().BeTrue();
        }
        else
        {
            sample.Failure.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_seek_counts_from_the_container_start_not_from_zero()
    {
        FakeRunner runner = VideoRunner(Packets(Enumerable.Range(0, 250).Select(i => (i * 0.04) + 3)), "00:00:10.00");
        runner.ProbeReport = runner.ProbeReport!.Replace("start: 0.000000", "start: 3.000000");
        runner.FrameStartOffset = 3;
        await new FfmpegModerationMediaSampler(runner, Options(), new Random(1), TimeSpan.FromMinutes(1)).SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        Seeks(runner).Should().OnlyContain(t => t >= 0 && t < 10, "-ss is relative to the container's start time");
    }

    [Fact]
    public async Task Stereo_with_an_audible_alternating_mix_also_hands_it_over_and_a_silent_one_does_not()
    {
        // The measure prints the loudest 1-second window of the alternating mix; above -50 dB it is transcribed too.
        foreach ((string output, bool side) in new[]
                 {
                     ("lavfi.astats.1.RMS_peak=-21.0\n", true),
                     ("lavfi.astats.1.RMS_peak=-49.0\n", true),
                     ("lavfi.astats.1.RMS_peak=-51.0\n", false),
                     ("lavfi.astats.1.RMS_peak=-inf\n", false)
                 })
        {
            FakeRunner runner = new() { ProbeReport = "Input #0, wav, from 'x':\n  Duration: 00:00:10.00\n  Stream #0:0: Audio: pcm_s16le, 44100 Hz, stereo\n", SideMidOutput = output };
            runner.Writes.Add(args => args.Contains("-ar") ? Wav(10) : []);
            ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options()).SampleAsync(new MemoryStream([1]), "audio/wav", CancellationToken.None);

            sample.Failure.Should().BeNull(output + ": flipped polarity is heard, not refused");
            (sample.SideAudio is not null).Should().Be(side, output);
            runner.Calls.Count(c => c.Arguments.Contains("pan=1c|c0=0.5*c0-0.5*c1")).Should().Be(side ? 1 : 0, output);
        }
    }

    [Theory]
    [InlineData("5.1", 6)]
    [InlineData("7.1", 8)]
    [InlineData("3 channels", 3)]
    public async Task Multichannel_audio_is_heard_channel_by_channel_and_only_audible_channels_are_extracted(string layout, int channels)
    {
        // The engine's own default compile keeps a 5.1 upload 5.1, so refusing it would refuse our own outputs.
        // Channel 0 is the main audio; channel 2 is audible and the rest are silent (the bass channel is not special).
        FakeRunner runner = new()
        {
            ProbeReport = $"Input #0, wav, from 'x':\n  Duration: 00:00:10.00\n  Stream #0:0: Audio: pcm_s16le, 44100 Hz, {layout}\n",
            SideMidOutput = string.Concat(Enumerable.Range(1, channels).Select(c => $"lavfi.astats.{c}.RMS_peak={(c == 3 ? "-20.0" : "-inf")}\n"))
        };
        runner.Writes.Add(args => args.Contains("-ar") ? Wav(10) : []);
        ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options(), new Random(5), FfmpegModerationMediaSampler.TotalDeadline).SampleAsync(new MemoryStream([1]), "audio/wav", CancellationToken.None);

        sample.Failure.Should().BeNull();
        sample.Audio.Should().NotBeNull();
        sample.ChannelAudio.Should().ContainSingle("only channel 2 is audible");
        sample.SideAudio.Should().BeNull();
        runner.Calls.Should().Contain(c => c.Arguments.Contains(FfmpegModerationMediaSampler.FullMixFilter(channels)), "the full mix of every channel is the main audio");
        runner.Calls.Should().Contain(c => c.Arguments.Contains(FfmpegModerationMediaSampler.ChannelFilter(2)), "the audible channel is read by its own index");
        runner.Calls.Should().NotContain(c => c.Arguments.Contains(FfmpegModerationMediaSampler.ChannelFilter(1)), "a silent channel is not transcribed");
        runner.Calls.Should().NotContain(c => c.Arguments.Contains(FfmpegModerationMediaSampler.ChannelFilter(0)), "a silent channel 0 is not transcribed either");
    }

    [Fact]
    public async Task More_than_eight_channels_is_a_final_refusal()
    {
        FakeRunner runner = new() { ProbeReport = "Input #0, wav, from 'x':\n  Duration: 00:00:10.00\n  Stream #0:0: Audio: pcm_s16le, 44100 Hz, 9 channels\n" };
        runner.Writes.Add(args => args.Contains("-ar") ? Wav(10) : []);
        ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options(), new Random(5), FfmpegModerationMediaSampler.TotalDeadline).SampleAsync(new MemoryStream([1]), "audio/wav", CancellationToken.None);

        sample.Failure.Should().Contain("more than 8 audio channels");
        sample.Deterministic.Should().BeTrue();
    }

    [Fact]
    public void Channels_are_read_by_their_own_index_and_measured_in_one_pass()
    {
        FfmpegModerationMediaSampler.ChannelFilter(4).Should().Be("pan=mono|c0=c4");
        FfmpegModerationMediaSampler.BuildChannelArgs("i", "o", 3).Should().ContainInOrder("-af", "pan=mono|c0=c3");
        FfmpegModerationMediaSampler.BuildChannelMeasureArgs("i", 600).Should().Contain(a => a.Contains("measure_perchannel=RMS_peak", StringComparison.Ordinal));
        double?[] peaks = FfmpegModerationMediaSampler.ParseChannelPeaks(
            "frame:0\nlavfi.astats.1.RMS_peak=-30.5\nlavfi.astats.2.RMS_peak=-inf\nlavfi.astats.1.RMS_peak=-20.5\nlavfi.astats.9.RMS_peak=-1\n", 3);
        peaks.Should().Equal(-20.5, -120, null);
        FfmpegModerationMediaSampler.ParseChannelPeaks("lavfi.astats.1.RMS_peak=-20\nlavfi.astats.4.RMS_peak=-20\n", 3, out bool extra);
        extra.Should().BeTrue("a reading for a channel above the probed count is a final failure");
    }

    [Fact]
    public async Task A_channel_above_the_probed_count_fails_the_file_for_good()
    {
        FakeRunner runner = new()
        {
            ProbeReport = "Input #0, wav, from 'x':\n  Duration: 00:00:10.00\n  Stream #0:0: Audio: pcm_s16le, 44100 Hz, 5.1\n",
            SideMidOutput = string.Concat(Enumerable.Range(1, 7).Select(c => $"lavfi.astats.{c}.RMS_peak=-20.0\n"))
        };
        runner.Writes.Add(args => args.Contains("-ar") ? Wav(10) : []);
        ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options(), new Random(5), FfmpegModerationMediaSampler.TotalDeadline).SampleAsync(new MemoryStream([1]), "audio/wav", CancellationToken.None);

        sample.Failure.Should().Contain("more channels than");
        sample.Deterministic.Should().BeTrue();
    }

    [Fact]
    public void A_wordless_multichannel_take_needs_the_mix_and_every_channel_transcript_empty()
    {
        string?[] all = ["", "", "", ""];
        all.Aggregate((string?)null, ModerationMediaSample.JoinTranscripts).Should().Be(string.Empty);
        string?[] one = ["", "", "said it", ""];
        one.Aggregate((string?)null, ModerationMediaSample.JoinTranscripts).Should().Be("said it");
    }

    [Fact]
    public void The_stereo_mixes_halve_every_channel_and_more_channels_use_the_full_mix()
    {
        FfmpegModerationMediaSampler.StereoMixFilter(false).Should().Be("pan=1c|c0=0.5*c0+0.5*c1");
        FfmpegModerationMediaSampler.StereoMixFilter(true).Should().Be("pan=1c|c0=0.5*c0-0.5*c1");
        FfmpegModerationMediaSampler.BuildAudioArgs("i", "o", 600, 1).Should().NotContain("-af", "mono stays one mix");
        FfmpegModerationMediaSampler.BuildAudioArgs("i", "o", 600, 2).Should().ContainInOrder("-af", FfmpegModerationMediaSampler.StereoMixFilter(false));
        FfmpegModerationMediaSampler.BuildAudioArgs("i", "o", 600, 6).Should().ContainInOrder("-af", FfmpegModerationMediaSampler.FullMixFilter(6));
        FfmpegModerationMediaSampler.FullMixFilter(3).Should().Be("pan=mono|c0=0.3333333*c0+0.3333333*c1+0.3333333*c2");
    }

    [Fact]
    public void The_pre_roll_left_out_of_the_draw_is_capped_so_a_lying_start_time_hides_nothing()
    {
        // An honest copy cut: 4 s of pre-roll, all excluded.
        FfmpegModerationMediaSampler.PreRollPackets(Enumerable.Range(0, 100).Select(i => -4 + (i * 0.04)).ToArray(), 0).Should().Be(100);
        // A start time of 3 hours on a 10-minute file: only the first 60 s are left out, the rest is drawable.
        double[] times = Enumerable.Range(0, 600).Select(i => (double)i).ToArray();
        FfmpegModerationMediaSampler.PreRollPackets(times, 10_800).Should().Be(60);
        FfmpegModerationMediaSampler.PreRollPackets(times, 20).Should().Be(20);
        FfmpegModerationMediaSampler.PreRollPackets([], 5).Should().Be(0);
    }

    [Fact]
    public void A_cue_whose_text_is_digits_or_has_an_arrow_is_judged_as_text()
    {
        string srt = "1\n00:00:01,000 --> 00:00:02,000\n5551234567\n\n"
            + "2\n00:00:03,000 --> 00:00:04,000\nbuy a --> b now\n\n"
            + "3\n00:00:05,000 --> 00:00:06,000\n00:00:09,000 --> 00:00:10,000\n";
        FfmpegModerationMediaSampler.SubtitlePlainText(srt).Split('\n').Select(l => l.Trim())
            .Should().Equal("5551234567", "buy a --> b now", "00:00:09,000 --> 00:00:10,000");
    }

    [Fact]
    public void A_wordless_take_needs_both_transcripts_empty()
    {
        ModerationMediaSample.JoinTranscripts("", "").Should().Be(string.Empty);
        ModerationMediaSample.JoinTranscripts("", null).Should().Be(string.Empty);
        ModerationMediaSample.JoinTranscripts("hello", "").Should().Be("hello");
        ModerationMediaSample.JoinTranscripts("", "hidden words").Should().Be("hidden words");
        ModerationMediaSample.JoinTranscripts("hello", "hidden words").Should().Be("hello\nhidden words");
        ModerationMediaSample.JoinTranscripts(null, null).Should().BeNull();
    }

    [Fact]
    public void A_subtitle_line_with_an_arrow_in_its_words_is_kept_and_only_the_timing_line_is_dropped()
    {
        string srt = "1\n00:00:01,000 --> 00:00:02,000\nGo --> there\n\n2\n00:00:03,000 --> 00:00:04,000 X1:100 X2:300\n--> alone\n\n3\n1:02:03.4 --> 1:02:04.5\ntail\n";

        FfmpegModerationMediaSampler.SubtitlePlainText(srt).Split('\n').Select(l => l.Trim()).Should()
            .Equal("Go --> there", "--> alone", "tail");
    }

    [Fact]
    public async Task A_one_frame_still_in_mkv_over_five_minutes_of_audio_passes()
    {
        FakeRunner runner = VideoRunner(Packets([0.0], 0.04), "00:05:00.00", "matroska,webm");
        runner.FfprobeReport = "{\"streams\":[{\"duration\":\"300.000000\"}]}";

        ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options()).SampleAsync(new MemoryStream([0]), "video/x-matroska", CancellationToken.None);

        sample.Failure.Should().BeNull("one packet has no length to compare with the declared one");
        sample.Images.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_declared_length_still_has_to_match_a_video_of_many_packets()
    {
        FakeRunner runner = VideoRunner(Packets(Enumerable.Range(0, 100).Select(i => i * 0.04)), "00:05:00.00", "matroska,webm");
        runner.FfprobeReport = "{\"streams\":[{\"duration\":\"300.000000\"}]}";

        ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options()).SampleAsync(new MemoryStream([0]), "video/x-matroska", CancellationToken.None);

        sample.Failure.Should().Contain("declared duration");
        sample.Deterministic.Should().BeTrue();
    }

    [Fact]
    public async Task A_name_that_tells_nothing_follows_the_probe_and_not_the_extension()
    {
        // A png in a .bin working file is an image; an mp4 with a track is a video; a file with no media stream is refused.
        FakeRunner image = new() { ProbeReport = null, MeasureReport = "frame=    1 fps=0.0 q=-0.0 Lsize=N/A time=00:00:00.04 bitrate=N/A" };
        image.Writes.Add(_ => [1]);
        ModerationMediaSample img = await new FfmpegModerationMediaSampler(image, Options()).SampleAsync(new MemoryStream([0]), FfmpegModerationMediaSampler.FollowProbeMimeType, CancellationToken.None);
        img.Kind.Should().Be(ModerationContentKind.Image);
        img.Failure.Should().BeNull();
        img.Images.Should().HaveCount(1);

        FakeRunner video = VideoRunner(Packets(Enumerable.Range(0, 250).Select(i => i * 0.04)), "00:00:10.00");
        ModerationMediaSample vid = await new FfmpegModerationMediaSampler(video, Options()).SampleAsync(new MemoryStream([0]), FfmpegModerationMediaSampler.FollowProbeMimeType, CancellationToken.None);
        vid.Kind.Should().Be(ModerationContentKind.Video);
        vid.Failure.Should().BeNull();
        vid.Images.Should().NotBeEmpty();

        FakeRunner audio = new() { ProbeReport = "Input #0, wav, from 'x':\n  Duration: 00:00:10.00\n  Stream #0:0: Audio: pcm_s16le, 44100 Hz, mono\n" };
        audio.Writes.Add(args => args.Contains("-ar") ? Wav(10) : []);
        ModerationMediaSample aud = await new FfmpegModerationMediaSampler(audio, Options()).SampleAsync(new MemoryStream([0]), FfmpegModerationMediaSampler.FollowProbeMimeType, CancellationToken.None);
        aud.Kind.Should().Be(ModerationContentKind.Audio);
        aud.Failure.Should().BeNull();

        FakeRunner nothing = new() { ProbeReport = string.Empty, ProbeExit = 1 };
        ModerationMediaSample none = await new FfmpegModerationMediaSampler(nothing, Options()).SampleAsync(new MemoryStream([0]), FfmpegModerationMediaSampler.FollowProbeMimeType, CancellationToken.None);
        none.Failure.Should().NotBeNull();
        none.Deterministic.Should().BeTrue();
    }

    [Fact]
    public void Every_decoding_pass_limits_its_threads()
    {
        string[][] argvs =
        [
            FfmpegModerationMediaSampler.BuildImageArgs("i", "o", 512),
            FfmpegModerationMediaSampler.BuildFrameAtArgs("i", "o", 1, 512),
            FfmpegModerationMediaSampler.BuildAnimatedFramesArgs("i", "o", [0], 512),
            FfmpegModerationMediaSampler.BuildCoverArgs("i", "o", 512),
            FfmpegModerationMediaSampler.BuildAudioArgs("i", "o"),
            FfmpegModerationMediaSampler.BuildSideMeasureArgs("i", 600),
            FfmpegModerationMediaSampler.BuildSideArgs("i", "o")
        ];
        foreach (string[] argv in argvs)
            argv.Should().ContainInOrder("-threads", FfmpegModerationMediaSampler.SamplingThreads, "-i", "i");
    }

    [Fact]
    public async Task A_full_disk_is_not_final_and_a_seek_failure_on_one_drawn_packet_tries_the_next()
    {
        new ProcessRunResult(1, string.Empty, "av_interleaved_write_frame(): No space left on device", false).Transient.Should().BeTrue();
        new ProcessRunResult(1, string.Empty, "Invalid data found", false).Transient.Should().BeFalse();

        // The first attempt at each frame fails like an AVI seek would; the next packet's seek succeeds.
        FakeRunner runner = VideoRunner(Packets(Enumerable.Range(0, 250).Select(i => i * 0.04)), "00:00:10.00");
        int attempts = 0;
        runner.Writes[0] = args => IsFrame(args) ? (attempts++ % 2 == 0 ? [] : [1]) : args.Contains("-ar") ? Wav(1) : [];
        ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options()).SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        sample.Failure.Should().BeNull();
        sample.Images.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Mono_audio_is_not_measured_for_cancellation()
    {
        FakeRunner runner = new() { ProbeReport = "Input #0, wav, from 'x':\n  Duration: 00:00:10.00\n  Stream #0:0: Audio: pcm_s16le, 44100 Hz, mono\n" };
        runner.Writes.Add(args => args.Contains("-ar") ? Wav(10) : []);
        await new FfmpegModerationMediaSampler(runner, Options()).SampleAsync(new MemoryStream([1]), "audio/wav", CancellationToken.None);

        runner.Probes.Should().NotContain(c => c.Arguments.Any(a => a.Contains("astats", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_alternating_mix_is_heard_above_minus_fifty_decibels_in_its_loudest_second()
    {
        FfmpegModerationMediaSampler.ParseSidePeak("frame:0\nlavfi.astats.1.RMS_peak=-30.5\nlavfi.astats.1.RMS_peak=-20.5\n")
            .Should().Be(-20.5, "the last (overall) value counts");
        FfmpegModerationMediaSampler.ParseSidePeak("lavfi.astats.1.RMS_peak=-inf\n").Should().Be(-120);
        FfmpegModerationMediaSampler.ParseSidePeak("nothing").Should().BeNull();
        FfmpegModerationMediaSampler.BuildSideMeasureArgs("i", 600).Should().Contain(a => a.Contains("astats=length=1", StringComparison.Ordinal));
        FfmpegModerationMediaSampler.SideAudible(-49).Should().BeTrue();
        FfmpegModerationMediaSampler.SideAudible(-51).Should().BeFalse("below -50 dB is silence");
        FfmpegModerationMediaSampler.SideAudible(-120).Should().BeFalse();
    }

    [Theory]
    [InlineData(5, 33, true)]
    [InlineData(33, 33, false)]
    [InlineData(28, 33, false)]
    [InlineData(0.5, 1.5, false)]
    public void Audio_heard_for_much_less_than_declared_is_short(double heard, double expected, bool isShort) =>
        FfmpegModerationMediaSampler.AudioHeardTooShort(heard, expected).Should().Be(isShort);

    [Fact]
    public async Task A_chained_ogg_heard_for_5_s_of_33_s_is_a_deterministic_failure_and_the_listening_limit_is_the_ceiling()
    {
        foreach ((string duration, double heard, bool fails) in new[]
                 {
                     ("00:00:33.00", 5.0, true), ("00:00:33.00", 33.0, false), ("01:00:00.00", 600.0, false), ("01:00:00.00", 100.0, true)
                 })
        {
            FakeRunner runner = new() { ProbeReport = $"Input #0, ogg, from 'x':\n  Duration: {duration}\n  Stream #0:0: Audio: vorbis, 44100 Hz, mono\n" };
            runner.Writes.Add(args => args.Contains("-ar") ? Wav(heard) : []);
            ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options()).SampleAsync(new MemoryStream([1]), "audio/ogg", CancellationToken.None);

            (sample.Failure is not null).Should().Be(fails, $"{duration} heard {heard}");
            if (fails) sample.Deterministic.Should().BeTrue();
        }
    }

    private const string SrtText = "1\n00:00:01,000 --> 00:00:02,000\nHello there\n\n2\n00:00:03,000 --> 00:00:04,000\nsecond line\n";

    [Fact]
    public async Task Text_subtitles_are_read_as_text_and_bitmap_or_unreadable_ones_fail()
    {

        // Text: read, plain, no cue numbers or time lines. A data stream beside it is ignored.
        FakeRunner text = VideoRunner(Packets(Enumerable.Range(0, 250).Select(i => i * 0.04)), "00:00:10.00", "matroska,webm", "  Stream #0:2: Subtitle: subrip\n  Stream #0:3: Data: bin_data\n");
        text.Writes[0] = args => IsFrame(args) ? [1] : args.Contains("-ar") ? Wav(1) : args.Last().EndsWith(".srt", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(SrtText) : [];
        ModerationMediaSample ok = await new FfmpegModerationMediaSampler(text, Options()).SampleAsync(new MemoryStream([0]), "video/x-matroska", CancellationToken.None);
        ok.Failure.Should().BeNull();
        ok.SubtitleText.Should().Be("Hello there" + Environment.NewLine + "second line");
        text.Calls.Single(c => c.Arguments.Last().EndsWith(".srt", StringComparison.Ordinal)).Arguments.Should().ContainInOrder("-map", "0:2");

        // Bitmap: a deterministic failure.
        FakeRunner bitmap = VideoRunner(Packets(Enumerable.Range(0, 250).Select(i => i * 0.04)), "00:00:10.00", "matroska,webm", "  Stream #0:2: Subtitle: hdmv_pgs_subtitle\n");
        ModerationMediaSample bad = await new FfmpegModerationMediaSampler(bitmap, Options()).SampleAsync(new MemoryStream([0]), "video/x-matroska", CancellationToken.None);
        bad.Failure.Should().Contain("subtitle");
        bad.Deterministic.Should().BeTrue();

        // Text that cannot be read (ffmpeg wrote nothing), and text over the cap.
        FakeRunner unreadable = VideoRunner(Packets(Enumerable.Range(0, 250).Select(i => i * 0.04)), "00:00:10.00", "matroska,webm", "  Stream #0:2: Subtitle: ass\n");
        ModerationMediaSample none = await new FfmpegModerationMediaSampler(unreadable, Options()).SampleAsync(new MemoryStream([0]), "video/x-matroska", CancellationToken.None);
        none.Failure.Should().Contain("subtitle");
        none.Deterministic.Should().BeTrue();

        FakeRunner huge = VideoRunner(Packets(Enumerable.Range(0, 250).Select(i => i * 0.04)), "00:00:10.00", "matroska,webm", "  Stream #0:2: Subtitle: webvtt\n");
        huge.Writes[0] = args => IsFrame(args) ? [1] : args.Contains("-ar") ? Wav(1) : args.Last().EndsWith(".srt", StringComparison.Ordinal) ? new byte[FfmpegModerationMediaSampler.MaxSubtitleBytes + 1] : [];
        ModerationMediaSample big = await new FfmpegModerationMediaSampler(huge, Options()).SampleAsync(new MemoryStream([0]), "video/x-matroska", CancellationToken.None);
        big.Failure.Should().Contain("subtitles");
        big.Deterministic.Should().BeTrue();
    }

    [Fact]
    public void Subtitle_text_is_joined_to_the_transcript()
    {
        ModerationMediaSample.WithSubtitles("spoken", "written").Should().Be("spoken\nwritten");
        ModerationMediaSample.WithSubtitles(null, "written").Should().Be("written");
        ModerationMediaSample.WithSubtitles("spoken", null).Should().Be("spoken");
        ModerationMediaSample.WithSubtitles(null, " ").Should().BeNull();
    }

    [Fact]
    public async Task One_total_deadline_covers_every_pass_and_a_late_pass_is_a_retryable_failure()
    {
        FakeRunner runner = VideoRunner(Packets(Enumerable.Range(0, 250).Select(i => i * 0.04)), "00:00:10.00");
        runner.Delay = TimeSpan.FromMilliseconds(120);
        FfmpegModerationMediaSampler sampler = new(runner, Options(), new Random(1), TimeSpan.FromMilliseconds(300));

        ModerationMediaSample sample = await sampler.SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        sample.Failure.Should().NotBeNull();
        sample.Deterministic.Should().BeFalse("running out of time says nothing about the bytes");
        (runner.Calls.Count + runner.Probes.Count).Should().BeLessThan(10, "the passes after the deadline never ran");
    }

    [Fact]
    public void A_signal_or_out_of_memory_exit_is_transient_and_a_clean_ffmpeg_error_is_final()
    {
        new ProcessRunResult(137, "", "", false).Transient.Should().BeTrue("SIGKILL by the OOM killer");
        new ProcessRunResult(139, "", "", false).Transient.Should().BeTrue("SIGSEGV");
        new ProcessRunResult(244, "", "", false).Transient.Should().BeTrue("AVERROR(ENOMEM)");
        new ProcessRunResult(-1, "", "", true).Transient.Should().BeTrue("a timeout");
        foreach (int exit in new[] { 1, 234, 255, 183 })
        {
            new ProcessRunResult(exit, "", "", false).Transient.Should().BeFalse($"exit {exit} is ffmpeg's own error");
            new ProcessRunResult(exit, "", "", false).Permanent.Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(137, false)]
    public async Task A_probe_that_was_killed_is_retried_and_one_that_failed_cleanly_is_final(int exit, bool final)
    {
        FakeRunner runner = new() { ProbeReport = string.Empty, ProbeExit = exit };
        ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options()).SampleAsync(new MemoryStream([0]), "video/mp4", CancellationToken.None);

        sample.Failure.Should().NotBeNull();
        sample.Deterministic.Should().Be(final);
    }

    [Fact]
    public void An_attached_gif_or_webp_is_not_a_cover()
    {
        foreach (string codec in new[] { "gif", "webp" })
        {
            string json = "{\"streams\":[{\"index\":0,\"codec_type\":\"audio\"},{\"index\":1,\"codec_type\":\"video\",\"codec_name\":\"" + codec
                          + "\",\"disposition\":{\"attached_pic\":1}}],\"format\":{\"format_name\":\"mp3\"}}";
            FfmpegModerationMediaSampler.DecideLayout(FfmpegModerationMediaSampler.ParseProbe(json)).Kind
                .Should().Be(FfmpegModerationMediaSampler.LayoutKind.Refused, codec);
        }
    }

    private static bool IsFrame(IReadOnlyList<string> args) =>
        args.Contains("-ss") || args.Contains("-copyts") || args.Any(a => a.StartsWith("select=", StringComparison.Ordinal));

    private static string SeekOf(Call call) => call.Arguments[call.Arguments.ToList().IndexOf("-ss") + 1];

    private static string DeclaredReport(double stream, double container) =>
        $"{{\"streams\":[{{\"duration\":\"{stream:0.0}\"}}],\"format\":{{\"duration\":\"{container:0.0}\"}}}}";

    internal sealed record Call(string FileName, IReadOnlyList<string> Arguments);

    /// <summary>Records each invocation and optionally writes bytes to the sampler's output path.</summary>
    private sealed class FakeRunner : IProcessRunner
    {
        public List<Func<IReadOnlyList<string>, byte[]>> Writes { get; } = [];

        public List<Call> Calls { get; } = [];

        /// <summary>The stream probe (<c>ffmpeg -i file</c>) is recorded apart so the pass assertions stay about passes.</summary>
        public List<Call> Probes { get; } = [];

        /// <summary>What the probe prints on stderr; empty means the streams are unknown and the file name decides.</summary>
        public string? ProbeReport { get; set; }

        /// <summary>What the stream-copy measuring pass prints; null makes that pass fail.</summary>
        /// <summary>What the stream-length probe prints on stdout (empty: nothing declared).</summary>
        public string FfprobeReport { get; set; } = string.Empty;

        /// <summary>The packet list ffprobe prints (csv: pts_time,dts_time,duration_time); null derives it from <see cref="MeasureReport"/>.</summary>
        public string? PacketReport { get; set; }

        /// <summary>The packet list of the audio stream (<c>-select_streams a</c>); null: the same list as the video's.</summary>
        public string? AudioPacketReport { get; set; }

        /// <summary>The channel count of every decoded audio frame ffprobe prints (one per line); empty: no frame differs.</summary>
        public string FrameChannelsReport { get; set; } = string.Empty;

        /// <summary>The exit code of the stream probe when its report is empty.</summary>
        public int ProbeExit { get; set; } = 1;

        /// <summary>Each process takes this long (the total deadline test).</summary>
        public TimeSpan Delay { get; set; }

        /// <summary>What the stereo measuring pass prints: ordinary stereo, mid 10 dB over the side.</summary>
        public string SideMidOutput { get; set; } = "lavfi.astats.1.RMS_peak=-80.0\n";

        /// <summary>The container start the frames' own times carry (the sampler seeks relative to it; <c>-copyts</c> prints them raw).</summary>
        public double FrameStartOffset { get; set; }

        /// <summary>The time a frame pass reports for the seek it was given; the default is the frame asked for.</summary>
        public Func<double, double>? FrameTime { get; set; }

        /// <summary>The time a decode from the start of the file reports for the time it is asked to start at (null: the first frame the file decodes). The default is the frame asked for.</summary>
        public Func<double?, double>? SlowFrameTime { get; set; }

        /// <summary>The first frame the file decodes at all.</summary>
        public double FirstFrameTime { get; set; }

        /// <summary>How many decodes from the start of the file ran.</summary>
        public int SlowCalls => Calls.Count(c => c.Arguments.Contains("-copyts") && !c.Arguments.Contains("-ss") && c.Arguments.Any(a => a.Contains("select='between(", StringComparison.Ordinal)));

        public string? MeasureReport { get; set; } = "frame=  300 fps=0.0 q=-1.0 Lsize=N/A time=00:10:00.00 bitrate=N/A";

        public ProcessRunResult Result { get; init; } = new(0, string.Empty, string.Empty, false);

        /// <summary>
        /// The report the probe prints. Null: a plain file whose kind is read from the working file's name. Empty: unreadable.
        /// A report with streams but no "Input" line gets the demuxer its first stream implies, as real ffmpeg prints one.
        /// </summary>
        private string BuildProbeReport(string inputPath)
        {
            if (ProbeReport is null)
            {
                string ext = Path.GetExtension(inputPath);
                if (ext == ".bin")
                    return "Input #0, png_pipe, from 'x':\n  Duration: N/A\n  Stream #0:0: Video: png, rgba, 20x20\n";
                if (ext is ".mp4" or ".mkv" or ".webm" or ".mov" or ".avi")
                    return "Input #0, mov,mp4,m4a, from 'x':\n  Duration: 00:10:00.00\n  Stream #0:0: Video: h264, yuv420p\n  Stream #0:1: Audio: aac\n";
                return "Input #0, wav, from 'x':\n  Duration: 00:00:10.00\n  Stream #0:0: Audio: pcm_s16le\n";
            }

            if (ProbeReport.Length == 0 || ProbeReport.Contains("Input #0,", StringComparison.Ordinal))
                return ProbeReport;
            string demuxer = ProbeReport.Contains("Video: gif", StringComparison.Ordinal) ? "gif"
                : ProbeReport.Contains("Video: webp", StringComparison.Ordinal) ? "webp_pipe"
                : ProbeReport.Contains("Video: png", StringComparison.Ordinal) ? "png_pipe"
                : ProbeReport.Contains("Video:", StringComparison.Ordinal) ? "mov,mp4,m4a" : "wav";
            return $"Input #0, {demuxer}, from 'x':\n" + ProbeReport;
        }

        /// <summary>Renders a readable stream listing (one "Stream #0:N: Kind: codec" per line) as ffprobe JSON.</summary>
        public static string ToProbeJson(string text)
        {
            string format = System.Text.RegularExpressions.Regex.Match(text, @"Input #0, (.*?), from").Groups[1].Value;
            string duration = System.Text.RegularExpressions.Regex.Match(text, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)") is { Success: true } m
                ? ((int.Parse(m.Groups[1].Value) * 3600) + (int.Parse(m.Groups[2].Value) * 60) + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "N/A";
            List<string> streams = [];
            foreach (System.Text.RegularExpressions.Match st in System.Text.RegularExpressions.Regex.Matches(
                         text, @"Stream #0:(\d+)[^:\n]*: (Video|Audio|Subtitle): ([^,\n ]+)([^\n]*)"))
            {
                bool pic = st.Groups[4].Value.Contains("attached pic", StringComparison.Ordinal);
                string tail = st.Groups[4].Value;
                (int channels, string layout) = tail.Contains("FC+LFE", StringComparison.Ordinal) ? (2, "2 channels (FC+LFE)")
                    : tail.Contains("5.1", StringComparison.Ordinal) ? (6, "5.1")
                    : tail.Contains("7.1", StringComparison.Ordinal) ? (8, "7.1")
                    : System.Text.RegularExpressions.Regex.Match(tail, @"(\d+) channels") is { Success: true } many ? (int.Parse(many.Groups[1].Value), many.Value)
                    : tail.Contains("stereo", StringComparison.Ordinal) ? (2, "stereo")
                    : tail.Contains("mono", StringComparison.Ordinal) ? (1, "mono")
                    : (0, string.Empty);
                streams.Add($"{{\"index\":{st.Groups[1].Value},\"codec_type\":\"{st.Groups[2].Value.ToLowerInvariant()}\",\"codec_name\":\"{st.Groups[3].Value}\",\"channels\":{channels},\"channel_layout\":\"{layout}\",\"disposition\":{{\"attached_pic\":{(pic ? 1 : 0)}}}}}");
            }

            string start = System.Text.RegularExpressions.Regex.Match(text, @"start: (-?\d+(?:\.\d+)?)") is { Success: true } sm ? sm.Groups[1].Value : "0";
            return $"{{\"streams\":[{string.Join(",", streams)}],\"format\":{{\"format_name\":\"{format}\",\"duration\":\"{duration}\",\"start_time\":\"{start}\"}}}}";
        }

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken ct)
        {
            if (Delay > TimeSpan.Zero)
                Thread.Sleep(Delay);

            if (fileName == "ffprobe" && arguments.Contains("frame=channels"))
            {
                Probes.Add(new Call(fileName, [.. arguments]));
                return Task.FromResult(new ProcessRunResult(0, FrameChannelsReport, string.Empty, false));
            }

            if (fileName == "ffprobe" && arguments.Any(a => a.StartsWith("packet=", StringComparison.Ordinal)))
            {
                Probes.Add(new Call(fileName, [.. arguments]));
                int selected = arguments.ToList().IndexOf("-select_streams");
                if (AudioPacketReport is not null && selected >= 0 && arguments[selected + 1] == "a")
                    return Task.FromResult(new ProcessRunResult(0, AudioPacketReport, string.Empty, false));
                if (PacketReport is not null)
                    return Task.FromResult(new ProcessRunResult(0, PacketReport, string.Empty, false));

                FfmpegModerationMediaSampler.MediaMeasure? measure = MeasureReport is null
                    ? null
                    : FfmpegModerationMediaSampler.ParseMeasure(MeasureReport);
                if (measure is null)
                    return Task.FromResult(new ProcessRunResult(1, string.Empty, string.Empty, false));

                // One packet per measured frame, evenly spread over the measured time (none when no time was printed).
                System.Text.StringBuilder csv = new();
                for (long i = 0; i < measure.Frames; i++)
                {
                    if (measure.Seconds is double total)
                    {
                        double step = total / measure.Frames;
                        string t = (i * step).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
                        csv.Append(t).Append(',').Append(t).Append(',').AppendLine(step.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        csv.AppendLine("N/A,N/A,N/A");
                    }
                }

                return Task.FromResult(new ProcessRunResult(0, csv.ToString(), string.Empty, false));
            }

            if (fileName == "ffprobe" && arguments.Contains("-show_streams"))
            {
                Probes.Add(new Call(fileName, [.. arguments]));
                string text = BuildProbeReport(arguments[^1]);
                return Task.FromResult(text.Length == 0
                    ? new ProcessRunResult(ProbeExit, string.Empty, string.Empty, false)
                    : new ProcessRunResult(0, ToProbeJson(text), string.Empty, false));
            }

            if (fileName == "ffprobe")
            {
                Probes.Add(new Call(fileName, [.. arguments]));
                return Task.FromResult(new ProcessRunResult(FfprobeReport.Length == 0 ? 1 : 0, FfprobeReport, string.Empty, false));
            }

            if (arguments[^1] == "-" && arguments.Contains("-map"))
            {
                Probes.Add(new Call(fileName, [.. arguments]));
                return Task.FromResult(MeasureReport is null
                    ? new ProcessRunResult(1, string.Empty, string.Empty, false)
                    : new ProcessRunResult(0, string.Empty, MeasureReport, false));
            }

            if (arguments.Any(a => a.Contains("astats", StringComparison.Ordinal)))
            {
                Probes.Add(new Call(fileName, [.. arguments]));
                return Task.FromResult(new ProcessRunResult(0, SideMidOutput, string.Empty, false));
            }

            Calls.Add(new Call(fileName, [.. arguments]));

            string output = arguments.Last();
            byte[] bytes = Writes.Count == 0 ? [] : Writes[0](arguments);
            if (bytes.Length > 0)
            {
                if (output.Contains('%'))
                {
                    for (int i = 0; i < bytes.Length; i++)
                        File.WriteAllBytes(output.Replace("%03d", (i + 1).ToString("000")), [bytes[i]]);
                }
                else
                {
                    File.WriteAllBytes(output, bytes);
                }
            }

            if (Result.ExitCode == 0 && arguments.Contains("-copyts") && arguments.Contains("-ss"))
            {
                double seek = double.Parse(arguments[arguments.ToList().IndexOf("-ss") + 1], CultureInfo.InvariantCulture);
                double at = FrameTime is null ? seek + 0.0002 + FrameStartOffset : FrameTime(seek);
                return Task.FromResult(new ProcessRunResult(
                    0, $"frame:0    pts:0  pts_time:{at.ToString("0.######", CultureInfo.InvariantCulture)}\nrb=1\n", string.Empty, false));
            }

            if (Result.ExitCode == 0 && arguments.Contains("-copyts") && !arguments.Contains("-ss"))
            {
                string? vf = arguments.FirstOrDefault(a => a.Contains("metadata=mode=print", StringComparison.Ordinal));
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(vf ?? string.Empty, @"between\(t,(-?[0-9.]+),(-?[0-9.]+)\)");
                double? lo = m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
                double? hi = m.Success ? double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : null;
                double? from = m.Success ? (lo + hi) / 2 : null;
                double at = SlowFrameTime is not null ? SlowFrameTime(from) : from ?? FirstFrameTime;
                if (lo is not null && (at < lo - 1e-9 || at > hi + 1e-9))
                {
                    // No frame has the time asked for: nothing is written.
                    File.Delete(output);
                    return Task.FromResult(Result);
                }

                return Task.FromResult(new ProcessRunResult(
                    0, $"frame:0    pts:0  pts_time:{at.ToString("0.######", CultureInfo.InvariantCulture)}\nrb=1\n", string.Empty, false));
            }

            return Task.FromResult(Result);
        }
    }
}
