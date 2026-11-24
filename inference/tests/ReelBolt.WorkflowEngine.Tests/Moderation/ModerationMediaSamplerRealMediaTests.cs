using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Moderation;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// The sampler against real ffmpeg with synthetic media (no customer content, no network). Each test
/// returns quietly when ffmpeg is not installed on the host.
/// </summary>
public sealed class ModerationMediaSamplerRealMediaTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sampler-media-" + Guid.NewGuid().ToString("N"));

    public ModerationMediaSamplerRealMediaTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static ContentModerationOptions Options() => new()
    {
        SampleImageMaxLongSide = 512,
        VideoFrameIntervalSeconds = 2,
        VideoMaxFrames = 20
    };

    private static bool Available =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(d => File.Exists(Path.Combine(d, "ffmpeg")) && File.Exists(Path.Combine(d, "ffprobe")));

    private string Make(string name, string args)
    {
        string path = Path.Combine(_dir, name);
        using Process p = Process.Start(new ProcessStartInfo("ffmpeg", $"-y -loglevel error {args} \"{path}\"")
        {
            RedirectStandardError = true
        })!;
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException(error);
        return path;
    }

    private static Task<ModerationMediaSample> SampleAsync(string path, string mime) =>
        new FfmpegModerationMediaSampler(new SystemProcessRunner(), Options())
            .SampleAsync(File.OpenRead(path), mime, CancellationToken.None);

    [Fact]
    public async Task A_real_jpeg_is_sampled_as_an_image_not_refused_for_its_extension()
    {
        if (!Available) return;

        string jpg = Make("a.jpg", "-f lavfi -i testsrc=size=320x240 -frames:v 1");
        ModerationMediaSample sample = await SampleAsync(jpg, "image/jpeg");

        sample.Failure.Should().BeNull();
        sample.Kind.Should().Be(ModerationContentKind.Image);
        sample.Images.Should().ContainSingle();
    }

    [Fact]
    public async Task A_video_with_a_longer_audio_track_is_not_a_duration_mismatch()
    {
        if (!Available) return;

        // 5 s of video, 30 s of audio: the container says 30 s, the video stream says 5 s.
        string mp4 = Make("longaudio.mp4",
            "-f lavfi -i testsrc=size=160x120:rate=25:duration=5 -f lavfi -i sine=duration=30 -c:v libx264 -pix_fmt yuv420p -c:a aac");
        ModerationMediaSample sample = await SampleAsync(mp4, "video/mp4");

        sample.Failure.Should().BeNull("the video stream is compared with its own declared length");
        sample.Images.Should().NotBeEmpty();
        sample.Audio.Should().NotBeNull();
    }

    [Fact]
    public async Task A_video_declaring_a_false_length_keeps_its_frames_and_reports_the_failure()
    {
        if (!Available) return;

        string mkv = Make("honest.mkv", "-f lavfi -i testsrc=size=160x120:rate=25:duration=10 -c:v libx264 -pix_fmt yuv420p");
        byte[] bytes = File.ReadAllBytes(mkv);

        // The segment duration (ms, big-endian double) and the stream's own DURATION tag both claim ten hours.
        int segment = IndexOf(bytes, [0x44, 0x89, 0x88]);
        segment.Should().BeGreaterThan(0);
        BitConverter.GetBytes(36_000_000.0).Reverse().ToArray().CopyTo(bytes, segment + 3);
        byte[] honest = Encoding.ASCII.GetBytes("00:00:10.000000000");
        int tag = IndexOf(bytes, honest);
        tag.Should().BeGreaterThan(0);
        Encoding.ASCII.GetBytes("10:00:00.000000000").CopyTo(bytes, tag);
        string lie = Path.Combine(_dir, "lie.mkv");
        File.WriteAllBytes(lie, bytes);

        ModerationMediaSample sample = await SampleAsync(lie, "video/x-matroska");

        sample.Failure.Should().Contain("declared duration");
        sample.Deterministic.Should().BeTrue("the same bytes will always lie");
        sample.Images.Should().HaveCount(5, "frames are taken over the MEASURED ten seconds, one per two");
    }

    [Fact]
    public async Task A_long_video_is_sampled_in_bounded_time_with_the_full_frame_cap()
    {
        if (!Available) return;

        // Two hours at one frame per second, sparse keyframes, plus audio: a whole-file decode would be slow.
        string mp4 = Make("long.mp4",
            "-f lavfi -i testsrc=size=160x120:rate=1:duration=7200 -f lavfi -i sine=duration=7200 -c:v libx264 -preset ultrafast -g 250 -pix_fmt yuv420p -c:a aac");

        Stopwatch watch = Stopwatch.StartNew();
        ModerationMediaSample sample = await SampleAsync(mp4, "video/mp4");
        watch.Stop();
        _output.WriteLine($"SAMPLING_TIMING_MS={watch.ElapsedMilliseconds} frames={sample.Images.Count} fileBytes={new FileInfo(mp4).Length}");

        sample.Failure.Should().BeNull();
        sample.Images.Should().HaveCount(20, "the cap is kept");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_one_and_a_half_second_gif_gives_every_frame_up_to_the_cap_not_one()
    {
        if (!Available) return;

        string gif = Make("short.gif", "-f lavfi -i testsrc=size=64x64:rate=10:duration=1.5");
        ModerationMediaSample sample = await SampleAsync(gif, "image/gif");

        sample.Kind.Should().Be(ModerationContentKind.Video);
        sample.Failure.Should().BeNull();
        sample.Images.Count.Should().BeGreaterThan(10, "fifteen decoded frames, all under the cap of twenty");
    }

    [Fact]
    public async Task A_long_gif_is_sampled_evenly_up_to_the_cap()
    {
        if (!Available) return;

        string gif = Make("long.gif", "-f lavfi -i testsrc=size=64x64:rate=10:duration=12");
        ModerationMediaSample sample = await SampleAsync(gif, "image/gif");

        sample.Failure.Should().BeNull();
        sample.Images.Should().HaveCount(20);
    }

    [Fact]
    public async Task An_audio_file_with_an_embedded_cover_picture_has_the_picture_judged_too()
    {
        if (!Available) return;

        string cover = Make("cover.jpg", "-f lavfi -i testsrc=size=200x200 -frames:v 1");
        string mp3 = Make("covered.mp3",
            $"-f lavfi -i sine=duration=2 -i \"{cover}\" -map 0 -map 1 -c:a libmp3lame -c:v mjpeg -id3v2_version 3 -disposition:v attached_pic");
        ModerationMediaSample sample = await SampleAsync(mp3, "audio/mpeg");

        sample.Kind.Should().Be(ModerationContentKind.Audio);
        sample.Failure.Should().BeNull();
        sample.Audio.Should().NotBeNull();
        sample.Images.Should().ContainSingle("the cover is a picture a person can see");
    }

    private string Cover(string name, string source = "testsrc") =>
        Make(name, $"-f lavfi -i {source}=size=200x200 -frames:v 1");

    private sealed class RecordingRunner : IProcessRunner
    {
        private readonly SystemProcessRunner _inner = new();

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add([.. arguments]);
            return _inner.RunAsync(fileName, arguments, timeout, ct);
        }
    }

    [Fact]
    public async Task A_plain_video_a_plain_audio_file_a_still_image_and_an_animation_all_pass()
    {
        if (!Available) return;

        string mp4 = Make("plain.mp4", "-f lavfi -i testsrc=size=160x120:rate=25:duration=4 -f lavfi -i sine=duration=4 -c:v libx264 -pix_fmt yuv420p -c:a aac");
        string wav = Make("plain.wav", "-f lavfi -i sine=duration=2");
        string png = Make("still.png", "-f lavfi -i testsrc=size=64x64 -frames:v 1");
        string gif = Make("anim.gif", "-f lavfi -i testsrc=size=64x64:rate=10:duration=1");

        ModerationMediaSample video = await SampleAsync(mp4, "video/mp4");
        video.Failure.Should().BeNull();
        video.Images.Should().NotBeEmpty();
        video.Audio.Should().NotBeNull();

        ModerationMediaSample audio = await SampleAsync(wav, "audio/wav");
        audio.Failure.Should().BeNull();
        audio.Images.Should().BeEmpty();
        audio.Audio.Should().NotBeNull();

        ModerationMediaSample still = await SampleAsync(png, "image/png");
        still.Failure.Should().BeNull();
        still.Kind.Should().Be(ModerationContentKind.Image);
        still.Images.Should().ContainSingle();

        ModerationMediaSample animation = await SampleAsync(gif, "image/gif");
        animation.Failure.Should().BeNull();
        animation.Images.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public void No_ffmpeg_muxer_can_put_audio_next_to_an_image_demuxer_so_that_refusal_is_proved_on_the_probe()
    {
        if (!Available) return;

        // GIF, APNG and PNG take one video stream and no audio: ffmpeg itself refuses to write the combination.
        foreach (string ext in new[] { "gif", "apng", "png" })
        {
            string path = Path.Combine(_dir, "audio." + ext);
            using Process p = Process.Start(new ProcessStartInfo("ffmpeg",
                $"-y -loglevel error -f lavfi -i testsrc=size=64x64:rate=10:duration=1 -f lavfi -i sine=duration=1 -map 0 -map 1 \"{path}\"")
            { RedirectStandardError = true })!;
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            p.ExitCode.Should().NotBe(0, ext);
        }

        FfmpegModerationMediaSampler.DecideLayout(new FfmpegModerationMediaSampler.MediaProbe(
                1, 1, 1, true, 0, false, [0], [], "gif")).Kind
            .Should().Be(FfmpegModerationMediaSampler.LayoutKind.Refused);
    }

    [Fact]
    public async Task A_matroska_attached_cover_is_judged_and_the_real_video_frames_target_the_real_stream()
    {
        if (!Available) return;

        string cover = Cover("mcover.jpg");
        string mkv = Make("att.mkv",
            $"-f lavfi -i testsrc=size=160x120:rate=25:duration=3 -f lavfi -i sine=duration=3 -map 0 -map 1 -c:v libx264 -pix_fmt yuv420p -attach \"{cover}\" -metadata:s:t mimetype=image/jpeg -metadata:s:t filename=cover.jpg");
        RecordingRunner runner = new();

        ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options())
            .SampleAsync(File.OpenRead(mkv), "video/x-matroska", CancellationToken.None);

        sample.Failure.Should().BeNull();
        sample.Audio.Should().NotBeNull();
        sample.Images.Should().HaveCount(2 + 1, "two frames (one per 2 s of 3 s) plus the one cover");
        runner.Calls.Should().Contain(c => c.Contains("-ss") && c.Contains("0:0"), "frames target the real video stream");
        runner.Calls.Should().Contain(c => c.Contains("0:2") && !c.Contains("-ss"), "the cover is read by its own stream index");
    }

    [Fact]
    public async Task An_mp4_with_an_attached_thumbnail_judges_the_video_and_the_thumbnail()
    {
        if (!Available) return;

        string cover = Cover("mp4cover.jpg");
        string mp4 = Make("thumb.mp4",
            $"-i \"{cover}\" -f lavfi -i testsrc=size=160x120:rate=25:duration=3 -map 0 -map 1 -c:v:0 mjpeg -c:v:1 libx264 -pix_fmt yuv420p -disposition:v:0 attached_pic");
        RecordingRunner runner = new();

        ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options())
            .SampleAsync(File.OpenRead(mp4), "video/mp4", CancellationToken.None);

        sample.Failure.Should().BeNull();
        sample.Images.Should().HaveCount(3, "two frames plus the thumbnail");
        runner.Calls.Should().Contain(c => c.Contains("0:1") && !c.Contains("-ss"), "the thumbnail is its own stream");
    }

    [Fact]
    public async Task Two_real_video_streams_fail_deterministically()
    {
        if (!Available) return;

        string mkv = Make("two.mkv",
            "-f lavfi -i testsrc=size=160x120:rate=25:duration=3 -f lavfi -i smptebars=size=160x120:rate=25:duration=3 -map 0 -map 1 -c:v libx264 -pix_fmt yuv420p");
        ModerationMediaSample sample = await SampleAsync(mkv, "video/x-matroska");

        sample.Failure.Should().Contain("more than one video stream");
        sample.Deterministic.Should().BeTrue();
    }

    [Fact]
    public async Task Audio_with_two_covers_judges_both_and_with_five_covers_fails()
    {
        if (!Available) return;

        string c1 = Cover("k1.jpg");
        string c2 = Cover("k2.jpg", "smptebars");
        string two = Make("two.mp3",
            $"-f lavfi -i sine=duration=2 -i \"{c1}\" -i \"{c2}\" -map 0 -map 1 -map 2 -c:a libmp3lame -c:v mjpeg -id3v2_version 3 -disposition:v attached_pic");
        ModerationMediaSample ok = await SampleAsync(two, "audio/mpeg");
        ok.Failure.Should().BeNull();
        ok.Images.Should().HaveCount(2);
        ok.Audio.Should().NotBeNull();

        string five = Make("five.mp3",
            $"-f lavfi -i sine=duration=2 -i \"{c1}\" -i \"{c1}\" -i \"{c1}\" -i \"{c1}\" -i \"{c1}\" -map 0 -map 1 -map 2 -map 3 -map 4 -map 5 -c:a libmp3lame -c:v mjpeg -id3v2_version 3 -disposition:v attached_pic");
        ModerationMediaSample refused = await SampleAsync(five, "audio/mpeg");
        refused.Failure.Should().Contain("attached pictures");
        refused.Deterministic.Should().BeTrue();
        refused.Images.Should().HaveCount(4, "the first four are still judged");
    }

    [Fact]
    public async Task Crafted_stream_metadata_cannot_make_a_real_video_read_as_a_cover_or_hide_it()
    {
        if (!Available) return;

        string fake = "x\nStream #0:1: Video: x (attached pic)\nStream #0:0: Video: h264 (attached pic)";
        string mp4 = Make("meta.mp4",
            "-f lavfi -i testsrc=size=160x120:rate=25:duration=6 -f lavfi -i sine=duration=6 -c:v libx264 -pix_fmt yuv420p -c:a aac " +
            $"-metadata:s:v:0 title=\"{fake}\" -metadata:s:v:0 handler_name=\"{fake}\" -metadata:s:v:0 language=\"Video\" -metadata:s:a:0 title=\"attached pic\" " +
            $"-metadata title=\"{fake}\" -tag:v avc1");
        RecordingRunner runner = new();
        ModerationMediaSample sample = await new FfmpegModerationMediaSampler(runner, Options())
            .SampleAsync(File.OpenRead(mp4), "video/mp4", CancellationToken.None);

        sample.Failure.Should().BeNull();
        sample.Kind.Should().Be(ModerationContentKind.Video);
        sample.Images.Should().HaveCount(3, "frames of the real video, not a cover");

        // The same text in an audio file's tags does not turn it into anything else.
        string mp3 = Make("meta.mp3", $"-f lavfi -i sine=duration=2 -c:a libmp3lame -metadata title=\"{fake}\" -metadata comment=\"{fake}\"");
        ModerationMediaSample audio = await SampleAsync(mp3, "audio/mpeg");
        audio.Failure.Should().BeNull();
        audio.Images.Should().BeEmpty();
        audio.Kind.Should().Be(ModerationContentKind.Audio);
    }

    [Fact]
    public async Task A_video_flagged_attached_pic_never_takes_the_audio_path_unless_it_is_a_single_still()
    {
        if (!Available) return;

        // ffmpeg will not write an h264 stream flagged attached_pic into MP4, and the MP4 muxer keeps one packet of a
        // flagged mjpeg stream: that is a real single-still cover next to audio, which is legitimately judged as one.
        string mp4 = Make("flagged.mp4",
            "-f lavfi -i testsrc=size=160x120:rate=25:duration=4 -f lavfi -i sine=duration=4 -c:v mjpeg -c:a aac -disposition:v:0 attached_pic");
        ModerationMediaSample sample = await SampleAsync(mp4, "audio/mp4");
        sample.Failure.Should().BeNull();
        sample.Images.Should().ContainSingle("the one stored still is judged as a cover");

        // The crafted layouts ffmpeg cannot write, from the probe's own JSON: a real codec or many frames is no cover.
        foreach (string stream in new[]
                 {
                     "{\"index\":1,\"codec_type\":\"video\",\"codec_name\":\"h264\",\"disposition\":{\"attached_pic\":1}}",
                     "{\"index\":1,\"codec_type\":\"video\",\"codec_name\":\"mjpeg\",\"nb_frames\":\"100\",\"disposition\":{\"attached_pic\":1}}"
                 })
        {
            FfmpegModerationMediaSampler.MediaLayout layout = FfmpegModerationMediaSampler.DecideLayout(FfmpegModerationMediaSampler.ParseProbe(
                "{\"streams\":[{\"index\":0,\"codec_type\":\"audio\"}," + stream + "],\"format\":{\"format_name\":\"mov,mp4,m4a,3gp,3g2,mj2\"}}"));
            layout.Kind.Should().Be(FfmpegModerationMediaSampler.LayoutKind.Refused);
            layout.Deterministic.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Video_declared_but_audio_detected_is_refused_and_audio_declared_with_video_is_video()
    {
        if (!Available) return;

        string wav = Make("declared.wav", "-f lavfi -i sine=duration=2");
        ModerationMediaSample refused = await SampleAsync(wav, "video/mp4");
        refused.Failure.Should().NotBeNull();
        refused.Deterministic.Should().BeTrue();

        string m4a = Make("hidden.m4a", "-f lavfi -i testsrc=size=160x120:rate=25:duration=3 -f lavfi -i sine=duration=3 -c:v libx264 -pix_fmt yuv420p -c:a aac");
        ModerationMediaSample video = await SampleAsync(m4a, "audio/mp4");
        video.Failure.Should().BeNull();
        video.Images.Should().NotBeEmpty();
    }

    [Fact]
    public void An_oversized_or_unreadable_probe_report_is_unparsed()
    {
        FfmpegModerationMediaSampler.ParseProbe("not json").Parsed.Should().BeFalse();
        FfmpegModerationMediaSampler.ParseProbe("{\"streams\":[{\"codec_type\":\"video\"}]}").Parsed.Should().BeFalse("no index");
        FfmpegModerationMediaSampler.ParseProbe(new string(' ', FfmpegModerationMediaSampler.MaxProbeReportChars + 1)).Parsed.Should().BeFalse();
        FfmpegModerationMediaSampler.DecideLayout(FfmpegModerationMediaSampler.ParseProbe(
                "{\"streams\":[{\"index\":0,\"codec_type\":\"audio\"}],\"format\":{\"format_name\":\"mp3\"}}")).Kind
            .Should().Be(FfmpegModerationMediaSampler.LayoutKind.Audio);
        FfmpegModerationMediaSampler.DecideLayout(FfmpegModerationMediaSampler.ParseProbe(
                "{\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"codec_name\":\"png\"}],\"format\":{\"format_name\":\"image2gif_fake\"}}")).Kind
            .Should().Be(FfmpegModerationMediaSampler.LayoutKind.Video, "format tokens are matched exactly, not by substring");
    }

    private static Task<ModerationMediaSample> SampleSeededAsync(string path, string mime, int seed) =>
        new FfmpegModerationMediaSampler(new SystemProcessRunner(), Options(), new Random(seed), FfmpegModerationMediaSampler.TotalDeadline)
            .SampleAsync(File.OpenRead(path), mime, CancellationToken.None);

    /// <summary>True when the frame is blue (the marker colour); the decode is cached per distinct JPEG.</summary>
    private static readonly Dictionary<string, bool> BlueCache = [];

    private static bool IsBlue(byte[] jpeg)
    {
        string key = Convert.ToBase64String(jpeg);
        if (BlueCache.TryGetValue(key, out bool known)) return known;

        string file = Path.Combine(Path.GetTempPath(), "sampler-px-" + Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(file, jpeg);
        try
        {
            using Process p = Process.Start(new ProcessStartInfo("ffmpeg", $"-loglevel error -i \"{file}\" -vf scale=1:1 -f rawvideo -pix_fmt rgb24 -")
            {
                RedirectStandardOutput = true
            })!;
            using MemoryStream pixel = new();
            p.StandardOutput.BaseStream.CopyTo(pixel);
            p.WaitForExit();
            byte[] rgb = pixel.ToArray();
            return BlueCache[key] = rgb.Length >= 3 && rgb[2] > rgb[0] + 50;
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_remux_that_moves_the_last_frames_an_hour_later_does_not_hide_the_first_minute()
    {
        if (!Available) return;

        // 60 s alternating blue and red, then 1 s green; the last 30 packets are moved 3,600 s later.
        string basePath = Make("gap-base.mp4",
            "-f lavfi -i color=red:s=128x96:r=30:d=61 -vf \"drawbox=x=0:y=0:w=iw:h=ih:color=blue:t=fill:enable='lt(mod(t,2),1)'\" -pix_fmt yuv420p -c:v libx264 -bf 0 -g 30");
        string gap = Make("gap.mp4",
            $"-i \"{basePath}\" -c copy -bsf:v \"setts=pts='if(gte(N,1800),PTS+3600/TB,PTS)':dts='if(gte(N,1800),DTS+3600/TB,DTS)'\"");

        ModerationMediaSample sample = await SampleSeededAsync(gap, "video/mp4", 5);

        sample.Images.Should().HaveCountGreaterThan(9, "the draws inside the hour-long hold are the one frame, judged once");
        sample.Images.Select(Convert.ToBase64String).Distinct().Count().Should().BeGreaterThan(1, "the samples are not one frame");
        sample.Images.Count(IsBlue).Should().BeInRange(3, 16, "the first minute alternates, and the samples reach it");
        sample.Failure.Should().BeNull("the hold is judged, not refused");
    }

    [Fact]
    public async Task A_flash_at_the_old_fixed_times_is_reached_by_some_draws_and_missed_by_others()
    {
        if (!Available) return;

        // 100 s, red, blue for 7% of the time centred on the old formula's times ((i + 0.5) x 5 s): the old sampler hit 20 of 20.
        string target = Make("target.mp4",
            "-f lavfi -i color=red:s=128x96:r=10:d=100 -vf \"drawbox=x=0:y=0:w=iw:h=ih:color=blue:t=fill:enable='lt(abs(mod(t,5)-2.5),0.175)'\" -pix_fmt yuv420p -c:v libx264 -g 1");

        List<int> hits = [];
        for (int seed = 1; seed <= 6; seed++)
        {
            ModerationMediaSample sample = await SampleSeededAsync(target, "video/mp4", seed);
            sample.Failure.Should().BeNull();
            sample.Images.Should().HaveCount(20);
            hits.Add(sample.Images.Count(IsBlue));
        }

        _output.WriteLine("BLUE_HITS_PER_JUDGEMENT=" + string.Join(",", hits));
        hits.Should().OnlyContain(h => h < 20, "the times are no longer the ones that hit 20 of 20");
        hits.Should().Contain(h => h > 0, "a 7% flash is reached by some draws");
        hits.Should().Contain(h => h == 0, "and missed by others: the times are not fixed");
    }

    [Theory]
    [InlineData("mpegts", "kf.ts", "-c:v libx264 -bf 0 -sc_threshold 0 -g 15 -f mpegts", true)]
    [InlineData("mp4", "kf.mp4", "-c:v libx264 -bf 0 -sc_threshold 0 -g 15", false)]
    [InlineData("mxf", "kf.mxf", "-c:v mpeg2video -bf 0 -g 15 -pix_fmt yuv422p -f mxf", false)]
    public async Task A_seek_in_mpeg_ts_returns_the_keyframe_and_is_refused_while_mp4_and_mxf_return_the_requested_frame(
        string name, string file, string encode, bool refused)
    {
        if (!Available) return;

        // 25 fps, a keyframe every 15 frames: the keyframes are blue, every other frame red.
        string path = Make(file,
            "-f lavfi -i color=red:s=128x96:r=25:d=60 -vf \"drawbox=x=0:y=0:w=iw:h=ih:color=blue:t=fill:enable='lt(mod(t,0.6),0.04)'\" "
            + (name == "mxf" ? "" : "-pix_fmt yuv420p ") + encode);

        ModerationMediaSample sample = await SampleSeededAsync(path, "video/mp4", 2);
        int blue = sample.Images.Count(IsBlue);
        _output.WriteLine($"{name}: blue={blue} of {sample.Images.Count} failure={sample.Failure}");

        sample.Images.Should().HaveCount(20, "ingest judges what it got");
        if (refused)
        {
            // The seek returns the keyframe, so the draws are decoded from the start (up to the cap); the demuxer still refuses.
            sample.Failure.Should().Contain("keyframe");
            sample.Deterministic.Should().BeTrue();
        }
        else
        {
            blue.Should().BeLessThan(7, "the requested frame comes back; a keyframe is only 1 frame in 15");
            sample.Failure.Should().BeNull();
        }
    }

    [Fact]
    public async Task Phase_inverted_stereo_passes_and_its_side_signal_is_handed_over_while_mono_compatible_stereo_has_none()
    {
        if (!Available) return;

        string inverted = Make("inv.mp3", "-f lavfi -i sine=f=440:d=10:sample_rate=44100 -af \"pan=stereo|c0=c0|c1=-1*c0\" -c:a libmp3lame");
        string wide = Make("wide.mp3",
            "-f lavfi -i sine=f=440:d=10:sample_rate=44100 -f lavfi -i sine=f=660:d=10:sample_rate=44100 -filter_complex \"[0][1]join=inputs=2:channel_layout=stereo\" -c:a libmp3lame");
        string same = Make("same.mp3", "-f lavfi -i sine=f=440:d=10:sample_rate=44100 -af \"pan=stereo|c0=c0|c1=c0\" -c:a libmp3lame");

        ModerationMediaSample flipped = await SampleAsync(inverted, "audio/mpeg");
        flipped.Failure.Should().BeNull("flipped polarity is heard through the side signal, not refused");
        flipped.SideAudio.Should().NotBeNull();
        flipped.SideAudio!.Length.Should().BeGreaterThan(ModerationMediaSample.WavBytesPerSecond * 9, "the side is the same 10 s");
        WavRmsDb(flipped.Audio!).Should().BeLessThan(WavRmsDb(flipped.SideAudio) - 15, "the mono downmix is nearly silent next to the side: that is the attack");

        (await SampleAsync(wide, "audio/mpeg")).Failure.Should().BeNull("wide stereo music is not cancelled");
        (await SampleAsync(same, "audio/mpeg")).SideAudio.Should().BeNull("a mono-compatible stereo file has no audible side");
    }

    [Fact]
    public async Task Inverted_speech_under_a_bed_and_in_one_part_of_a_file_has_its_side_signal_extracted()
    {
        if (!Available) return;

        // A centred bed all along; 3 s of a different tone in opposite phase in the middle only (a stand-in for speech).
        string bed = Make("bed.wav",
            "-f lavfi -i sine=f=200:d=12:sample_rate=16000 -f lavfi -i \"sine=f=900:d=3:sample_rate=16000\" "
            + "-filter_complex \"[1]adelay=5000|5000,apad=whole_dur=12[v];[v]asplit[a][b];[b]volume=-1[bn];[0]asplit[l0][r0];[l0][a]amix=inputs=2:normalize=0[l];[r0][bn]amix=inputs=2:normalize=0[r];[l][r]join=inputs=2:channel_layout=stereo\"");

        ModerationMediaSample sample = await SampleAsync(bed, "audio/wav");
        sample.Failure.Should().BeNull();
        sample.SideAudio.Should().NotBeNull("the side signal of the file is audible");
        WavRmsDb(sample.SideAudio!).Should().BeGreaterThan(-40);
    }

    [Fact]
    public async Task A_two_channel_file_that_is_not_left_and_right_and_a_5_1_file_are_judged_not_refused()
    {
        if (!Available) return;

        string fclfe = Make("fclfe.wav", "-f lavfi -i sine=d=4 -af \"pan=FC+LFE|c0=c0|c1=c0\" -c:a pcm_s16le");
        string surround = Make("surround.wav", "-f lavfi -i sine=d=4 -af \"pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0\" -c:a pcm_s16le");

        foreach (string path in new[] { fclfe, surround })
        {
            ModerationMediaSample sample = await SampleAsync(path, "audio/wav");
            sample.Failure.Should().BeNull(Path.GetFileName(path) + ": the engine's own 5.1 outputs must not be refused");
            sample.Audio.Should().NotBeNull();
            WavRmsDb(sample.Audio!).Should().BeGreaterThan(-40, "the tone in one channel is heard in the sum, not dropped by a downmix");
        }
    }

    [Fact]
    public async Task Speech_in_opposite_phase_on_a_back_pair_or_for_one_second_of_a_long_quiet_file_is_transcribed_from_the_alternating_mix()
    {
        if (!Available) return;

        // 5.1 whose only content is a tone on the two back channels in opposite phase: the sum cancels it, the alternating mix keeps it.
        string back = Make("back.wav", "-f lavfi -i sine=f=700:d=4:sample_rate=16000 -af \"pan=5.1|BL=c0|BR=-1*c0\" -c:a pcm_s16le");
        ModerationMediaSample backSample = await SampleAsync(back, "audio/wav");
        backSample.Failure.Should().BeNull();
        // Each back channel is read on its own, so the inverted pair is heard whole, never cancelled; channel 0 is silent.
        backSample.ChannelAudio.Should().NotBeNull();
        backSample.ChannelAudio!.Count.Should().Be(2, "only BL and BR carry anything");
        backSample.ChannelAudio.Should().OnlyContain(w => WavRmsDb(w) > -40);
        backSample.Audio!.Skip(100).Should().OnlyContain(x => x == 0, "the full mix of the opposite pair is silence");

        // 40 s of silence with one second of a quiet inverted tone: about -53 dB over the whole range (the old measure
        // called that silence) and -37 dB in its own second.
        string brief = Make("brief.wav",
            "-f lavfi -i \"aevalsrc='0.02*sin(2*PI*440*t)*between(t,20,21)|-0.02*sin(2*PI*440*t)*between(t,20,21)':s=8000:d=40\" -c:a pcm_s16le");
        ModerationMediaSample briefSample = await SampleAsync(brief, "audio/wav");
        briefSample.Failure.Should().BeNull();
        briefSample.SideAudio.Should().NotBeNull("one audible second of the alternating mix is enough");

        // Stereo whose sides are equal costs no second transcription; more than two channels do.
        string same = Make("same51.wav", "-f lavfi -i sine=f=440:d=4:sample_rate=16000 -af \"pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0\" -c:a pcm_s16le");
        // Every channel of identical content is audible: the full mix is the main audio and all six are transcribed too.
        (await SampleAsync(same, "audio/wav")).ChannelAudio.Should().HaveCount(6, "each audible channel is read by its own index");
    }

    /// <summary>The overall RMS level of a 16 kHz mono 16-bit WAV (as the sampler writes it), in dB below full scale.</summary>
    private static double WavRmsDb(byte[] wav)
    {
        double sum = 0;
        int n = (wav.Length - ModerationMediaSample.WavHeaderBytes) / 2;
        for (int i = 0; i < n; i++)
        {
            double v = BitConverter.ToInt16(wav, ModerationMediaSample.WavHeaderBytes + (i * 2)) / 32768.0;
            sum += v * v;
        }

        return n == 0 || sum == 0 ? -120 : 10 * Math.Log10(sum / n);
    }

    [Fact]
    public async Task A_chained_ogg_heard_for_5_s_of_33_s_fails_and_a_plain_ogg_passes()
    {
        if (!Available) return;

        string vorbis = Make("v.ogg", "-f lavfi -i sine=f=440:d=5:sample_rate=48000 -c:a libvorbis");
        string opus = Make("o.ogg", "-f lavfi -i sine=f=880:d=28:sample_rate=48000 -c:a libopus");
        string chained = Path.Combine(_dir, "chained.ogg");
        File.WriteAllBytes(chained, [.. File.ReadAllBytes(vorbis), .. File.ReadAllBytes(opus)]);

        ModerationMediaSample bad = await SampleAsync(chained, "audio/ogg");
        bad.Failure.Should().Contain("heard for");
        bad.Deterministic.Should().BeTrue();
        bad.AudioSeconds.Should().BeLessThan(10);

        (await SampleAsync(opus, "audio/ogg")).Failure.Should().BeNull();
    }

    [Fact]
    public async Task Subtitle_text_is_read_from_mkv_and_mp4_and_returned_as_text()
    {
        if (!Available) return;

        string srt = Path.Combine(_dir, "s.srt");
        File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:02,000\nhidden words here\n\n2\n00:00:03,000 --> 00:00:04,000\nsecond cue\n");

        foreach ((string name, string codec, string mime) in new[] { ("subs.mkv", "srt", "video/x-matroska"), ("subs.mp4", "mov_text", "video/mp4") })
        {
            string path = Make(name, $"-f lavfi -i testsrc=size=128x96:rate=10:d=5 -i \"{srt}\" -c:v libx264 -pix_fmt yuv420p -c:s {codec}");
            ModerationMediaSample sample = await SampleAsync(path, mime);

            sample.Failure.Should().BeNull(name);
            sample.SubtitleText.Should().Contain("hidden words here").And.Contain("second cue").And.NotContain("-->", name);
        }
    }

    [Fact]
    public async Task A_still_held_over_long_audio_passes()
    {
        if (!Available) return;

        // One frame every 30 s over 90 s of audio (three packets), and one frame a second over 60 s.
        string slow = Make("still-slow.mp4",
            "-loop 1 -framerate 1/30 -i \"" + Make("still.png", "-f lavfi -i testsrc=size=128x96 -frames:v 1")
            + "\" -f lavfi -i sine=d=90 -c:v libx264 -pix_fmt yuv420p -c:a aac -t 90");
        string fast = Make("still-fast.mp4",
            "-loop 1 -framerate 1 -i \"" + Path.Combine(_dir, "still.png") + "\" -f lavfi -i sine=d=60 -c:v libx264 -tune stillimage -pix_fmt yuv420p -c:a aac -t 60");

        foreach (string path in new[] { slow, fast })
        {
            ModerationMediaSample sample = await SampleAsync(path, "video/mp4");
            sample.Failure.Should().BeNull(Path.GetFileName(path));
            sample.Images.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task A_deadline_that_is_already_past_is_a_retryable_failure_on_real_media()
    {
        if (!Available) return;

        string mp4 = Make("deadline.mp4", "-f lavfi -i testsrc=size=128x96:rate=10:d=5 -c:v libx264 -pix_fmt yuv420p");
        ModerationMediaSample sample = await new FfmpegModerationMediaSampler(
                new SystemProcessRunner(), Options(), new Random(1), TimeSpan.Zero)
            .SampleAsync(File.OpenRead(mp4), "video/mp4", CancellationToken.None);

        sample.Failure.Should().NotBeNull();
        sample.Deterministic.Should().BeFalse();
    }

    [Fact]
    public async Task Ten_thousand_tiny_packets_cannot_hide_a_long_red_tail_from_the_draws()
    {
        if (!Available) return;

        // pos10k: 10,000 blue packets in the first 10 s, then 25 red frames of 59 s each (99% of the time is red).
        string blue = Make("pb.mkv", "-f lavfi -i color=blue:s=64x48:r=1000:d=10 -c:v libx264 -g 1000 -pix_fmt yuv420p");
        string red = Make("pr.mkv", "-f lavfi -i color=red:s=64x48:r=1/59:d=1475 -c:v libx264 -pix_fmt yuv420p -fps_mode vfr");
        string list = Path.Combine(_dir, "pos.txt");
        File.WriteAllText(list, $"file '{blue}'\nfile '{red}'\n");
        string pos10k = Make("pos10k.mkv", $"-f concat -safe 0 -i \"{list}\" -c copy");

        int reached = 0;
        const int seeds = 12;
        for (int seed = 1; seed <= seeds; seed++)
        {
            ModerationMediaSample sample = await SampleSeededAsync(pos10k, "video/x-matroska", seed);
            sample.Failure.Should().BeNull();
            if (sample.Images.Any(i => !IsBlue(i))) reached++;
        }

        _output.WriteLine($"POS10K_RED_REACHED={reached}/{seeds}");
        reached.Should().BeGreaterThanOrEqualTo(seeds - 1, "the time slices reach the red 99% of the video");
    }

    [Fact]
    public async Task A_variable_frame_rate_recording_with_a_long_still_passes_with_the_hold_judged_also_after_a_stream_copy()
    {
        if (!Available) return;

        // A 90 s slide in a 94 s video: frames listed with their durations (variable frame rate).
        string a = Make("slide.png", "-f lavfi -i color=red:s=128x96 -frames:v 1");
        string b = Make("tail.png", "-f lavfi -i color=blue:s=128x96 -frames:v 1");
        string list = Path.Combine(_dir, "slide.txt");
        File.WriteAllText(list, $"file '{a}'\nduration 90\nfile '{b}'\nduration 1\nfile '{b}'\nduration 1\nfile '{b}'\nduration 1\nfile '{b}'\nduration 1\nfile '{b}'\n");
        string vfr = Make("vfr.mp4", $"-f concat -safe 0 -i \"{list}\" -f lavfi -i sine=d=94 -fps_mode vfr -c:v libx264 -pix_fmt yuv420p -c:a aac -t 94");
        string copied = Make("vfr-copy.mkv", $"-i \"{vfr}\" -c copy");
        string copiedMp4 = Make("vfr-copy.mp4", $"-i \"{copied}\" -c copy -movflags +faststart");

        foreach ((string path, string mime) in new[] { (vfr, "video/mp4"), (copied, "video/x-matroska"), (copiedMp4, "video/mp4") })
        {
            ModerationMediaSample sample = await SampleSeededAsync(path, mime, 4);
            sample.Failure.Should().BeNull(Path.GetFileName(path));
            sample.Images.Should().NotBeEmpty();
            sample.Images.Should().Contain(i => !IsBlue(i), "the 90 s slide is the one frame on screen almost all the time: it is judged");
        }
    }

    [Fact]
    public async Task A_one_frame_still_in_mkv_over_five_minutes_of_audio_passes()
    {
        if (!Available) return;

        string png = Make("one.png", "-f lavfi -i testsrc=size=128x96 -frames:v 1");
        string mkv = Make("one.mkv", $"-loop 1 -framerate 1 -i \"{png}\" -f lavfi -i sine=d=300 -frames:v 1 -c:v libx264 -pix_fmt yuv420p -c:a aac -t 300");

        ModerationMediaSample sample = await SampleAsync(mkv, "video/x-matroska");
        sample.Failure.Should().BeNull();
        sample.Images.Should().ContainSingle();
    }

    [Fact]
    public async Task A_subtitle_line_with_an_arrow_in_its_words_is_text_not_a_timing_line()
    {
        if (!Available) return;

        string srt = Path.Combine(_dir, "arrow.srt");
        File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:02,000\nthe words v_arrow --> kept\n\n2\n00:00:03,000 --> 00:00:04,000\nsecond\n");
        string path = Make("arrow.mkv", $"-f lavfi -i testsrc=size=128x96:rate=10:d=5 -i \"{srt}\" -c:v libx264 -pix_fmt yuv420p -c:s srt");

        ModerationMediaSample sample = await SampleAsync(path, "video/x-matroska");
        sample.Failure.Should().BeNull();
        sample.SubtitleText.Should().Contain("the words v_arrow --> kept").And.Contain("second").And.NotContain("00:00");
    }

    [Fact]
    public async Task A_bin_name_follows_the_probe_and_real_media_is_judged()
    {
        if (!Available) return;

        string mp4 = Make("named.mp4", "-f lavfi -i testsrc=size=128x96:rate=10:d=5 -f lavfi -i sine=d=5 -c:v libx264 -pix_fmt yuv420p -c:a aac");
        string wav = Make("named.wav", "-f lavfi -i sine=d=3 -c:a pcm_s16le");
        string ts = Make("named.ts", "-f lavfi -i testsrc=size=128x96:rate=25:d=5 -c:v libx264 -pix_fmt yuv420p -f mpegts");
        string text = Path.Combine(_dir, "t.txt");
        File.WriteAllText(text, "just some words, not media");

        (await SampleAsync(mp4, FfmpegModerationMediaSampler.FollowProbeMimeType)).Kind.Should().Be(ModerationContentKind.Video);
        ModerationMediaSample audio = await SampleAsync(wav, FfmpegModerationMediaSampler.FollowProbeMimeType);
        audio.Kind.Should().Be(ModerationContentKind.Audio);
        audio.Failure.Should().BeNull();
        audio.AudioSeconds.Should().BeApproximately(3, 0.2);
        ModerationMediaSample refusedTs = await SampleAsync(ts, FfmpegModerationMediaSampler.FollowProbeMimeType);
        refusedTs.Failure.Should().Contain("keyframe", ".ts still refuses by its demuxer, whatever it is called");
        ModerationMediaSample notMedia = await SampleAsync(text, FfmpegModerationMediaSampler.FollowProbeMimeType);
        notMedia.Failure.Should().NotBeNull();
        notMedia.Deterministic.Should().BeTrue();
    }

    private static double Rms(byte[]? wav)
    {
        if (wav is null) return 0;
        int data = wav.AsSpan().IndexOf("data"u8);
        if (data < 0 || wav.Length <= data + 10) return 0;
        double sum = 0;
        int samples = (wav.Length - data - 8) / 2;
        for (int i = 0; i < samples; i++)
        {
            short v = BitConverter.ToInt16(wav, data + 8 + (i * 2));
            sum += (double)v * v;
        }

        return Math.Sqrt(sum / samples);
    }

    [Fact]
    public async Task A_red_still_of_ten_hours_among_ten_thousand_tiny_packets_is_always_judged()
    {
        if (!Available) return;

        // redhold (no B-frames in the stills: a stream-copy concat of reordered stills prints four packets at one time): 10,000 blue packets in 10 s, one red still of 10.8 h, then 73 blue stills of 59 s (43,197 s, under 12 h).
        string blue = Make("rb.mkv", "-f lavfi -i color=blue:s=64x48:r=1000:d=10 -c:v libx264 -g 1000 -bf 0 -pix_fmt yuv420p");
        string red = Make("rr.mkv", "-f lavfi -i color=red:s=64x48:r=1/38880:d=38880 -c:v libx264 -bf 0 -pix_fmt yuv420p -fps_mode vfr");
        string stills = Make("rs.mkv", "-f lavfi -i color=blue:s=64x48:r=1/59:d=4307 -c:v libx264 -bf 0 -pix_fmt yuv420p -fps_mode vfr");
        string list = Path.Combine(_dir, "redhold.txt");
        File.WriteAllText(list, $"file '{blue}'\nfile '{red}'\nfile '{stills}'\n");
        string redhold = Make("redhold.mkv", $"-f concat -safe 0 -i \"{list}\" -c copy");

        int reached = 0;
        for (int seed = 1; seed <= 10; seed++)
        {
            ModerationMediaSample sample = await SampleSeededAsync(redhold, "video/x-matroska", seed);
            sample.Failure.Should().BeNull();
            if (sample.Images.Any(i => !IsBlue(i))) reached++;
        }

        _output.WriteLine($"REDHOLD_RED_REACHED={reached}/10");
        reached.Should().Be(10);
    }

    [Fact]
    public async Task The_last_frame_under_a_longer_audio_track_is_judged_and_a_longer_audio_than_twelve_hours_refuses()
    {
        if (!Available) return;

        // tail: 300 blue frames, then one red frame, 10 s of video under 1 h of silent audio. tailtiny: 10,000 tiny blue packets.
        foreach ((string name, string blue) in new[]
                 {
                     ("tail", "color=blue:s=64x48:r=30:d=10"),
                     ("tailtiny", "color=blue:s=64x48:r=1000:d=10")
                 })
        {
            string path = Make(name + ".mp4",
                $"-f lavfi -i {blue} -f lavfi -i color=red:s=64x48:r=30:d=0.0334 -f lavfi -t 3600 -i anullsrc=r=8000:cl=mono "
                + "-filter_complex [0:v][1:v]concat=n=2:v=1:a=0[v] -map [v] -map 2:a -c:v libx264 -pix_fmt yuv420p -g 300 -c:a aac -b:a 8k");
            int reached = 0;
            for (int seed = 1; seed <= 8; seed++)
            {
                ModerationMediaSample sample = await SampleSeededAsync(path, "video/mp4", seed);
                sample.Failure.Should().BeNull(name);
                if (sample.Images.Any(i => !IsBlue(i))) reached++;
            }

            _output.WriteLine($"{name.ToUpperInvariant()}_RED_REACHED={reached}/8");
            reached.Should().Be(8, name);
        }

        // 13 hours of audio under a 3 s video: the longest stream is over the limit.
        string long13 = Make("long13.mp4",
            "-f lavfi -i color=blue:s=64x48:r=10:d=3 -f lavfi -t 46800 -i anullsrc=r=8000:cl=mono -map 0:v -map 1:a -c:v libx264 -pix_fmt yuv420p -c:a aac -b:a 8k");
        ModerationMediaSample refused = await SampleSeededAsync(long13, "video/mp4", 1);
        refused.Failure.Should().Contain("longer than 12 hours");
        refused.Deterministic.Should().BeTrue();
        refused.TooLong.Should().BeTrue("the refusal tells the member that length is the reason");
    }

    [Fact]
    public async Task A_false_sync_entry_is_decoded_from_the_start_so_every_frame_drawn_is_the_frame_drawn()
    {
        if (!Available) return;

        // 40 s at 1 fps, one real keyframe at the start and one at 8 s; the second sync entry is rewritten to sample 4 (a P-frame),
        // so a seek to packets 4..7 starts from a frame the decoder cannot use and returns the keyframe at 8 s, 1 to 4 s away.
        string src = Make("falsesync-src.mp4",
            "-f lavfi -i testsrc=size=64x48:rate=1:d=40 -c:v libx264 -pix_fmt yuv420p -bf 0 -g 1000 -sc_threshold 0 -force_key_frames 8");
        byte[] bytes = File.ReadAllBytes(src);
        int stss = IndexOf(bytes, Encoding.ASCII.GetBytes("stss"));
        stss.Should().BeGreaterThan(0);
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(stss + 8)).Should().Be(2, "two sync entries");
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(stss + 16), 4);
        string lie = Path.Combine(_dir, "falsesync.mp4");
        File.WriteAllBytes(lie, bytes);

        int refused = 0;
        for (int seed = 1; seed <= 12; seed++)
        {
            ModerationMediaSample sample = await SampleSeededAsync(lie, "video/mp4", seed);
            if (sample.Failure is not null) refused++;
            sample.Images.Should().HaveCount(20);
        }

        _output.WriteLine($"FALSE_SYNC_REFUSED={refused}/12");
        refused.Should().Be(0, "the lying index is ignored for any frame that does not come back as the drawn one");
    }

    [Theory]
    [InlineData("live")]
    [InlineData("low")]
    public async Task The_last_frame_under_a_webm_audio_track_is_judged_whatever_length_the_container_declares(string how)
    {
        if (!Available) return;

        // 10 s of blue and one red frame, under 300 s of opus. "live": no length at all (-live 1 to a pipe). "low": a length of 10 s.
        string args = "-f lavfi -i color=blue:s=64x48:r=30:d=10 -f lavfi -i color=red:s=64x48:r=30:d=0.0334 -f lavfi -t 300 -i anullsrc=r=8000:cl=mono "
            + "-filter_complex [0:v][1:v]concat=n=2:v=1:a=0[v] -map [v] -map 2:a -c:v libvpx -g 300 -pix_fmt yuv420p -c:a libopus -b:a 6k";
        string path = Path.Combine(_dir, $"tail_{how}.webm");
        if (how == "live")
        {
            using Process p = Process.Start(new ProcessStartInfo("ffmpeg", $"-y -loglevel error {args} -f webm -live 1 \"{path}\"") { RedirectStandardError = true })!;
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            p.ExitCode.Should().Be(0);
        }
        else
        {
            Make("tail_low.webm", args);
            byte[] bytes = File.ReadAllBytes(path);
            int duration = IndexOf(bytes, [0x44, 0x89, 0x88]);
            duration.Should().BeGreaterThan(0);
            BitConverter.GetBytes(10_000.0).Reverse().ToArray().CopyTo(bytes, duration + 3);
            File.WriteAllBytes(path, bytes);
        }

        int reached = 0;
        for (int seed = 1; seed <= 8; seed++)
        {
            ModerationMediaSample sample = await SampleSeededAsync(path, "video/webm", seed);
            sample.Failure.Should().BeNull(how);
            if (sample.Images.Any(i => !IsBlue(i))) reached++;
        }

        _output.WriteLine($"TAIL_{how.ToUpperInvariant()}_RED_REACHED={reached}/8");
        reached.Should().Be(8, how);
    }

    [Fact]
    public async Task A_video_only_file_with_a_lying_container_length_is_not_extended_to_it()
    {
        if (!Available) return;

        // 10 s of blue and a red last frame, no audio, the container length rewritten to 900 s: nothing plays for 900 s, so the
        // last frame has no more weight than its own packet, and the file is refused for the declared length it does not have.
        string mkv = Make("vo.mkv", "-f lavfi -i color=blue:s=64x48:r=30:d=10 -c:v libx264 -pix_fmt yuv420p");
        byte[] bytes = File.ReadAllBytes(mkv);
        int duration = IndexOf(bytes, [0x44, 0x89, 0x88]);
        duration.Should().BeGreaterThan(0);
        BitConverter.GetBytes(900_000.0).Reverse().ToArray().CopyTo(bytes, duration + 3);
        string lie = Path.Combine(_dir, "vo_lie.mkv");
        File.WriteAllBytes(lie, bytes);

        ModerationMediaSample sample = await SampleSeededAsync(lie, "video/x-matroska", 1);
        sample.TooLong.Should().BeFalse();
        sample.Images.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("mp4", "video/mp4", 7.5, 3, 200)]
    [InlineData("mov", "video/quicktime", 7.5, 3, 200)]
    [InlineData("mp4", "video/mp4", 1.8, 1, 50)]
    [InlineData("mov", "video/quicktime", 1.8, 1, 50)]
    public async Task A_copy_cut_shorter_than_its_own_pre_roll_is_not_refused(string ext, string mime, double start, int seconds, int gop)
    {
        if (!Available) return;

        // 25 fps, a keyframe every gop frames (8 s or 2 s): the cut starts inside a group, so the packets before it stay in the
        // file with negative times and the first shown packet is the cut's start.
        string src = Make($"short-src-{start}.{ext}", $"-f lavfi -i testsrc=size=96x64:rate=25:d=20 -c:v libx264 -pix_fmt yuv420p -g {gop} -bf 0 -sc_threshold 0");
        string cut = Make($"short-cut-{start}.{ext}", $"-ss {start} -i \"{src}\" -t {seconds} -c copy");
        int refused = 0;
        for (int seed = 1; seed <= 6; seed++)
        {
            ModerationMediaSample sample = await SampleSeededAsync(cut, mime, seed);
            if (sample.Failure is not null) refused++;
            sample.Images.Should().NotBeEmpty();
        }

        _output.WriteLine($"SHORT_CUT_{ext}_{start}_REFUSED={refused}/6");
        refused.Should().Be(0);
    }

    [Fact]
    public void The_end_of_the_audio_is_its_last_packet_and_its_duration()
    {
        FfmpegModerationMediaSampler.AudioEnd(FfmpegModerationMediaSampler.ParsePackets("0.0,0.0,0.02\n299.974,299.974,0.02\n299.994,299.994,0.02\n"))
            .Should().BeApproximately(300.014, 1e-9);
        FfmpegModerationMediaSampler.AudioEnd(FfmpegModerationMediaSampler.ParsePackets("0.0,0.0,N/A\n0.5,0.5,N/A\n"))
            .Should().BeApproximately(1.0, 1e-9, "no duration: the gap before the last packet");
        FfmpegModerationMediaSampler.AudioEnd(FfmpegModerationMediaSampler.ParsePackets(string.Empty)).Should().BeNull();
    }

    [Fact]
    public async Task A_51_file_whose_channels_cancel_in_any_mix_is_heard_channel_by_channel_including_the_bass_channel()
    {
        if (!Available) return;

        // FL = A, FC = -A, FR = B, LFE = -B: every fixed mix of them cancels; read one by one nothing does.
        string cancelling = Make("cancel51.wav",
            "-f lavfi -i sine=f=440:d=3 -f lavfi -i sine=f=660:d=3 -filter_complex "
            + "[0:a][1:a]amerge=inputs=2,pan=5.1|c0=c0|c1=c1|c2=-1*c0|c3=-1*c1|c4=0*c0|c5=0*c0 -c:a pcm_s16le");
        ModerationMediaSample sample = await SampleAsync(cancelling, "audio/wav");
        sample.Failure.Should().BeNull();
        Rms(sample.Audio).Should().BeLessThan(5, "the full mix of these channels cancels, which is why every channel is also read alone");
        sample.ChannelAudio.Should().HaveCount(4, "channels 0 to 3 are audible; 4 and 5 are silent");
        sample.ChannelAudio!.Should().OnlyContain(w => Rms(w) > 300);

        // Loud noise on the bass channel only is heard like any other channel: no layout name decides what is dropped.
        string bass = Make("bass51.wav",
            "-f lavfi -i sine=f=60:d=3 -filter_complex pan=5.1|c0=0*c0|c1=0*c0|c2=0*c0|c3=c0|c4=0*c0|c5=0*c0 -c:a pcm_s16le");
        ModerationMediaSample noise = await SampleAsync(bass, "audio/wav");
        noise.Failure.Should().BeNull();
        Rms(noise.Audio).Should().BeGreaterThan(30, "the full mix carries the bass channel too");
        noise.ChannelAudio.Should().ContainSingle().Which.Should().Match<byte[]>(w => Rms(w) > 300);

        // 9 channels is a final refusal.
        string nine = Make("nine.wav", string.Concat(Enumerable.Repeat("-f lavfi -i sine=d=2 ", 9)) + "-filter_complex amerge=inputs=9 -c:a pcm_s16le");
        ModerationMediaSample refused = await SampleAsync(nine, "audio/wav");
        refused.Failure.Should().NotBeNull();
        refused.Deterministic.Should().BeTrue();
    }

    [Theory]
    [InlineData("aac51.m4a", "-c:a aac", "audio/mp4")]
    [InlineData("ac351.ac3", "-c:a ac3", "audio/ac3")]
    public async Task A_51_aac_and_ac3_file_passes_and_every_audible_channel_is_handed_over(string name, string codec, string mime)
    {
        if (!Available) return;

        // A tone on all six channels at different pitches, 60 s: the cost of hearing each channel is printed.
        string path = Make(name, "-f lavfi -i sine=f=300:d=60 -f lavfi -i sine=f=400:d=60 -f lavfi -i sine=f=500:d=60 -f lavfi -i sine=f=60:d=60 "
            + "-f lavfi -i sine=f=700:d=60 -f lavfi -i sine=f=800:d=60 -filter_complex \"[0][1][2][3][4][5]amerge=inputs=6,channelmap=channel_layout=5.1\" " + codec);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        ModerationMediaSample sample = await SampleAsync(path, mime);
        _output.WriteLine($"{name}: {watch.ElapsedMilliseconds} ms, channel passes {sample.ChannelAudio?.Count}");
        sample.Failure.Should().BeNull();
        sample.Audio.Should().NotBeNull();
        sample.ChannelAudio.Should().HaveCount(6);
    }

    [Theory]
    [InlineData("1", "2")]
    [InlineData("2", "6")]
    [InlineData("6", "2")]
    [InlineData("2", "1")]
    public async Task A_change_of_the_audio_channel_count_is_a_final_refusal_and_an_unchanged_file_passes(string first, string second)
    {
        if (!Available) return;

        string Part(string name, string channels) => Make(name, "-f lavfi -i sine=f=500:d=3:sample_rate=48000 -af "
            + (channels == "6" ? "\"pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0\"" : "anull") + $" -ac {channels} -c:a ac3 -f ac3");
        string a = Part($"part-{first}-a.ac3", first);
        string b = Part($"part-{second}-b.ac3", second);
        string joined = Path.Combine(Path.GetDirectoryName(a)!, $"joined-{first}-{second}.ac3");
        File.WriteAllBytes(joined, [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);

        ModerationMediaSample changed = await SampleAsync(joined, "audio/ac3");
        changed.Failure.Should().Be(FfmpegModerationMediaSampler.ChannelChangeFailure);
        changed.Deterministic.Should().BeTrue();

        Stopwatch watch = Stopwatch.StartNew();
        ModerationMediaSample same = await SampleAsync(a, "audio/ac3");
        _output.WriteLine($"unchanged {first}-channel file: {watch.ElapsedMilliseconds} ms");
        same.Failure.Should().BeNull("an unchanged file of any layout passes");
    }

    [Theory]
    [InlineData("6,", 6, false)]
    [InlineData("2", 6, true)]
    [InlineData("6,", 2, true)]
    [InlineData("", 2, false)]
    public void A_decoded_frame_line_differs_only_when_its_count_is_not_the_declared_one(string line, int declared, bool differs) =>
        FfmpegModerationMediaSampler.FrameChannelsDiffer(line, declared).Should().Be(differs);

    [Fact]
    public void The_channel_meter_keeps_the_loudest_reading_of_each_channel_across_a_layout_reset()
    {
        string output = "lavfi.astats.1.RMS_peak=-20.0\nlavfi.astats.2.RMS_peak=-30.0\nlavfi.astats.1.RMS_peak=-90.0\nlavfi.astats.2.RMS_peak=-inf\n";
        double?[] peaks = FfmpegModerationMediaSampler.ParseChannelPeaks(output, 2);
        peaks[0].Should().Be(-20.0);
        peaks[1].Should().Be(-30.0);
    }

    [Fact]
    public async Task A_stream_copied_cut_in_the_middle_of_a_group_is_not_refused_for_its_pre_roll()
    {
        if (!Available) return;

        // 25 fps, a keyframe every 4 s, cut at 3.3 s with the stream copied: the 58 packets before the first shown time stay,
        // with negative times, and a seek to one returns the frame at 0.
        string src = Make("preroll-src.mp4", "-f lavfi -i testsrc=size=96x64:rate=25:d=20 -c:v libx264 -pix_fmt yuv420p -g 100 -bf 0");
        string cut = Make("preroll.mp4", $"-ss 3.3 -i \"{src}\" -c copy");
        for (int seed = 1; seed <= 12; seed++)
        {
            ModerationMediaSample sample = await SampleSeededAsync(cut, "video/mp4", seed);
            sample.Failure.Should().BeNull($"seed {seed}");
            sample.Images.Should().NotBeEmpty();
        }
    }

    [Theory]
    [InlineData("h264_bf.avi", "-c:v libx264 -pix_fmt yuv420p -bf 2 -r 25", "video/x-msvideo")]
    [InlineData("fr60.avi", "-c:v mpeg4 -r 60", "video/x-msvideo")]
    [InlineData("kf_blue.mxf", "-c:v mpeg2video -g 12 -r 25", "video/mxf")]
    public async Task Ordinary_avi_and_mxf_files_pass_whatever_few_frames_their_seek_lands_off(string name, string codec, string mime)
    {
        if (!Available) return;

        string path = Make(name, $"-f lavfi -i testsrc=size=96x64:rate=25:d=20 {codec}");
        int failed = 0;
        for (int seed = 1; seed <= 12; seed++)
        {
            ModerationMediaSample sample = await SampleSeededAsync(path, mime, seed);
            if (sample.Failure is not null) failed++;
            sample.Images.Should().NotBeEmpty();
        }

        _output.WriteLine($"{name}_REFUSED={failed}/12");
        failed.Should().Be(0, name);
    }

    [Fact]
    public async Task A_raw_ac3_file_is_audio_not_unreadable()
    {
        if (!Available) return;

        string ac3 = Make("raw.ac3", "-f lavfi -i sine=d=3 -c:a ac3");
        ModerationMediaSample sample = await SampleAsync(ac3, FfmpegModerationMediaSampler.FollowProbeMimeType);
        sample.Failure.Should().BeNull();
        sample.Kind.Should().Be(ModerationContentKind.Audio);
        Rms(sample.Audio).Should().BeGreaterThan(300);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }

        return -1;
    }
}
