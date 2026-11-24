using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Moderation;
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.Moderation.Mp4Forge;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// The frame check against files built to defeat it, with real ffmpeg. Every file hides a red scene behind an index that
/// lies (a false sync entry) or among packets a fast seek cannot tell apart. The rule: across seeds, the red is reached or
/// the file is refused. Never no red and no failure. Honest files (open GOP, B-frames, AVI, FLV, copy cuts) must pass.
/// </summary>
public sealed class ModerationMediaSamplerFrameTruthTests : IDisposable
{
    private const int Hold = 38_880_000; // 10.8 h in ms

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sampler-truth-" + Guid.NewGuid().ToString("N"));

    public ModerationMediaSamplerFrameTruthTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static bool Available =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(d => File.Exists(Path.Combine(d, "ffmpeg")) && File.Exists(Path.Combine(d, "ffprobe")));

    private static ContentModerationOptions Options() => new()
    {
        SampleImageMaxLongSide = 512,
        VideoFrameIntervalSeconds = 2,
        VideoMaxFrames = 20
    };

    private string Make(string name, string args)
    {
        string path = Path.Combine(_dir, name);
        using Process p = Process.Start(new ProcessStartInfo("ffmpeg", $"-y -loglevel error {args} \"{path}\"") { RedirectStandardError = true })!;
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException(error);
        return path;
    }

    private static Task<ModerationMediaSample> SampleSeededAsync(string path, string mime, int seed) =>
        new FfmpegModerationMediaSampler(new SystemProcessRunner(), Options(), new Random(seed), FfmpegModerationMediaSampler.TotalDeadline)
            .SampleAsync(File.OpenRead(path), mime, CancellationToken.None);

    private static readonly Dictionary<string, bool> RedCache = [];

    private static bool IsRed(byte[] jpeg)
    {
        string key = Convert.ToBase64String(jpeg);
        if (RedCache.TryGetValue(key, out bool known)) return known;

        string file = Path.Combine(Path.GetTempPath(), "sampler-red-" + Guid.NewGuid().ToString("N") + ".jpg");
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
            return RedCache[key] = rgb.Length >= 3 && rgb[0] > rgb[2] + 50;
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Samples with seeds 1..n and counts: red reached, refused, and missed (no red and no failure: the hole).</summary>
    private async Task<(int Reached, int Refused, int Missed)> RunAsync(string label, string path, int seeds)
    {
        int reached = 0, refused = 0, missed = 0;
        for (int seed = 1; seed <= seeds; seed++)
        {
            ModerationMediaSample sample = await SampleSeededAsync(path, "video/mp4", seed);
            if (sample.Failure is not null)
            {
                refused++;
                if (seed == 1) _output.WriteLine($"{label} seed 1 refused: {sample.Failure} (deterministic {sample.Deterministic})");
            }
            else if (sample.Images.Any(IsRed)) reached++;
            else
            {
                missed++;
                _output.WriteLine($"{label} seed {seed}: no red among {sample.Images.Count} frames");
            }
        }

        _output.WriteLine($"{label}: red reached {reached}/{seeds}, refused {refused}/{seeds}, missed {missed}/{seeds}");
        return (reached, refused, missed);
    }

    private static List<Fr> Tiny() => [B(1, true), .. Enumerable.Range(0, 9999).Select(_ => B(1))];

    private static List<Fr> Blue73() => [B(59000, true), .. Enumerable.Range(0, 72).Select(_ => B(59000))];

    private static List<Fr> Many(int n, Func<int, Fr> make) => [.. Enumerable.Range(0, n).Select(make)];

    private async Task<(int Reached, int Refused, int Missed)> BuiltAsync(string name, IReadOnlyList<Fr> frames, int seeds)
    {
        string path = Path.Combine(_dir, name + ".mp4");
        Encode(frames, path);
        (int reached, int refused, int missed) = await RunAsync(name, path, seeds);
        File.Delete(path);
        return (reached, refused, missed);
    }

    [Fact]
    public async Task Two_video_packets_with_one_printed_time_are_a_final_refusal_and_the_frames_are_kept()
    {
        if (!Available) return;

        // A blue frame of zero duration before the red one: both print the same time.
        string path = Path.Combine(_dir, "xdup.mp4");
        Encode([.. Enumerable.Range(0, 10).Select(_ => B(1000, true)), B(0, true), R(1000), .. Enumerable.Range(0, 9).Select(_ => B(1000, true))], path);
        for (int seed = 1; seed <= 3; seed++)
        {
            ModerationMediaSample sample = await SampleSeededAsync(path, "video/mp4", seed);
            sample.Failure.Should().Be(FfmpegModerationMediaSampler.DuplicateFrameTimesFailure, $"seed {seed}");
            sample.Deterministic.Should().BeTrue();
            sample.Images.Should().NotBeEmpty("the frames are kept for ingest");
        }
    }

    [Fact]
    public void A_packet_list_with_a_repeated_time_is_flagged_and_one_without_is_not()
    {
        FfmpegModerationMediaSampler.ParsePackets("0.000000,0.000000,1.0\n1.000000,1.000000,1.0\n1.000000,1.000000,0.0\n2.000000,2.000000,1.0\n")
            .DuplicateTimes.Should().BeTrue();
        FfmpegModerationMediaSampler.ParsePackets("0.000000,0.000000,1.0\n1.000000,1.000000,1.0\n1.001000,1.001000,1.0\n")
            .DuplicateTimes.Should().BeFalse();
    }

    [Fact]
    public async Task A_red_still_of_ten_hours_with_a_false_sync_entry_is_reached_or_refused()
    {
        if (!Available) return;

        // xredhold: 10,000 tiny blue packets, the red still of 10.8 h listed as a keyframe, a real keyframe after it.
        (await BuiltAsync("xredhold", [.. Tiny(), Rf(Hold), .. Blue73()], 8)).Missed.Should().Be(0);

        // The real keyframe six packets after the red: still the red or a refusal.
        List<Fr> far = [.. Enumerable.Range(0, 5).Select(_ => B(1000))];
        List<Fr> blue71 = [B(59000, true), .. Enumerable.Range(0, 70).Select(_ => B(59000))];
        (await BuiltAsync("xredhold_far", [.. Tiny(), Rf(Hold), .. far, .. blue71], 8)).Missed.Should().Be(0);
    }

    [Fact]
    public async Task Three_red_stills_with_false_sync_entries_and_a_video_red_throughout_are_reached_or_refused()
    {
        if (!Available) return;

        List<Fr> xred3 = [B(40, true), Rf(13_680_000), B(40, true), Rf(13_680_000), B(40, true), Rf(13_680_000), B(40, true), .. Many(10, _ => B(59000))];
        (await BuiltAsync("xred3", xred3, 8)).Missed.Should().Be(0);

        // Red throughout, a 1 ms blue real keyframe after every red packet; and the two-packet step form.
        List<Fr> alt = [B(1, true)];
        for (int i = 0; i < 1200; i++) alt.AddRange([Rf(999), B(1, true)]);
        (await BuiltAsync("xalt_all", alt, 8)).Missed.Should().Be(0);

        List<Fr> alt2 = [B(1, true)];
        for (int i = 0; i < 1200; i++) alt2.AddRange([Rf(998), B(1), B(1, true)]);
        (await BuiltAsync("xalt2", alt2, 8)).Missed.Should().Be(0);
    }

    [Fact]
    public async Task A_red_among_the_first_packets_and_a_red_as_the_last_packet_are_reached_or_refused()
    {
        if (!Available) return;

        List<Fr> far = [.. Enumerable.Range(0, 5).Select(_ => B(1000))];
        List<Fr> blue71 = [B(59000, true), .. Enumerable.Range(0, 70).Select(_ => B(59000))];
        (await BuiltAsync("first3_far2", [B(40, true), Rf(Hold), .. far, .. Tiny(), .. blue71], 8)).Missed.Should().Be(0);
        (await BuiltAsync("first3_two", [B(40, true), Rf(Hold / 2), Rf(Hold / 2), .. far, .. Blue73()], 8)).Missed.Should().Be(0);
        (await BuiltAsync("xlast", [B(1000, true), .. Many(30, _ => B(1000)), Rf(Hold)], 8)).Missed.Should().Be(0);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(30)]
    public async Task A_scene_of_only_near_misses_is_reached_exactly_as_often_as_with_a_true_index(int percent)
    {
        if (!Available) return;

        // 20 minutes at two packets a second; the scene is a share of it, each red packet followed by three tiny blue ones
        // and a real keyframe, so a fast seek lands a few packets off. The lying file lists every red packet as a keyframe.
        // A sample can miss a scene that is 5 percent of the file by chance, which is the draw and not the check, so the
        // same file with a true index is the control: the same seeds draw the same packets, and each draw must give the
        // same frame, so the counts of seeds that reach the red are equal and the lying file never misses more.
        const int total = 1_200_000;
        int start = total / 10, end = start + (total * percent / 100);
        List<Fr> Build(bool lie)
        {
            List<Fr> frames = [B(500, true)];
            int ms = 500;
            while (ms < start) { frames.Add(B(500)); ms += 500; }
            while (ms < end) { frames.AddRange([lie ? Rf(485) : R(485), B(5), B(5), B(5, true)]); ms += 500; }
            while (ms < total) { frames.Add(B(500)); ms += 500; }
            return frames;
        }

        (int reached, int refused, int missed) lying = await BuiltAsync($"nearb{percent:00}", Build(true), 8);
        (int reached, int refused, int missed) control = await BuiltAsync($"nearb{percent:00}_true_index", Build(false), 8);
        lying.refused.Should().Be(0, "nothing here is a lie about the frames, only about the index");
        lying.reached.Should().Be(control.reached, "a decode from the start gives the true frame for every draw");
        lying.missed.Should().Be(control.missed);
        if (percent >= 15) lying.missed.Should().Be(0);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(15)]
    public async Task A_red_block_with_a_false_entry_at_its_first_packet_is_reached_or_refused(int percent)
    {
        if (!Available) return;

        const int total = 1_200_000, frameMs = 500;
        int a = total / 10 / frameMs, b = (total / 10 + (total * percent / 100)) / frameMs, n = total / frameMs;
        List<Fr> frames =
        [
            .. Enumerable.Range(0, a).Select(i => B(frameMs, i == 0)),
            Rf(frameMs),
            .. Enumerable.Range(0, b - a - 1).Select(_ => R(frameMs)),
            B(frameMs, true),
            .. Enumerable.Range(0, n - b - 1).Select(_ => B(frameMs))
        ];
        (await BuiltAsync($"xblock{percent}", frames, 6)).Missed.Should().Be(0);
    }

    [Fact]
    public async Task A_short_red_block_with_a_false_entry_is_reached_or_refused()
    {
        if (!Available) return;

        // xblock40s: 40 s at 10 fps, red from 12 s to 16 s.
        List<Fr> frames =
        [
            .. Enumerable.Range(0, 120).Select(i => B(100, i == 0)),
            Rf(100),
            .. Enumerable.Range(0, 39).Select(_ => R(100)),
            B(100, true),
            .. Enumerable.Range(0, 239).Select(_ => B(100))
        ];
        (await BuiltAsync("xblock40s", frames, 8)).Missed.Should().Be(0);
    }

    [Fact]
    public async Task A_file_whose_first_packets_are_before_anything_it_decodes_is_redrawn_not_refused()
    {
        if (!Available) return;

        // The first keyframe is dropped from a stream copy, so the 74 packets after it (3 s) are listed but never decode: the
        // first frame the file decodes is the keyframe at 3 s.
        string src = Make("lead-src.mp4", "-f lavfi -i testsrc=size=96x64:rate=25:d=40 -c:v libx264 -pix_fmt yuv420p -g 75 -bf 0 -sc_threshold 0");
        string cut = Make("lead.mkv", $"-i \"{src}\" -c copy -bsf:v noise=drop=eq(n\\,0)");
        for (int seed = 1; seed <= 6; seed++)
        {
            ModerationMediaSample sample = await SampleSeededAsync(cut, "video/x-matroska", seed);
            sample.Failure.Should().BeNull($"seed {seed}: packets before anything playable are redrawn, not refused");
            sample.Images.Should().HaveCount(20);
        }
    }

    [Theory]
    [InlineData("x265_open_gop_2fps.mp4", "-f lavfi -i testsrc=size=96x64:rate=2:d=240 -c:v libx265 -x265-params open-gop=1:keyint=20:min-keyint=20:bframes=4:log-level=error -pix_fmt yuv420p -tag:v hvc1")]
    [InlineData("x265_bframes8.mp4", "-f lavfi -i testsrc=size=96x64:rate=25:d=60 -c:v libx265 -x265-params bframes=8:b-adapt=0:keyint=100:open-gop=1:log-level=error -pix_fmt yuv420p -tag:v hvc1")]
    [InlineData("h264_open_gop_frag.mp4", "-f lavfi -i testsrc=size=96x64:rate=25:d=40 -c:v libx264 -pix_fmt yuv420p -bf 3 -x264-params open-gop=1:keyint=50:min-keyint=50 -movflags frag_keyframe+empty_moov+default_base_moof")]
    [InlineData("h264_bf.avi", "-f lavfi -i testsrc=size=96x64:rate=25:d=30 -c:v libx264 -pix_fmt yuv420p -bf 2")]
    [InlineData("s.avi", "-f lavfi -i testsrc=size=96x64:rate=25:d=30 -c:v mpeg4 -g 250")]
    [InlineData("fr60.avi", "-f lavfi -i testsrc=size=96x64:rate=60:d=20 -c:v mpeg4")]
    [InlineData("kf.mxf", "-f lavfi -i testsrc=size=96x64:rate=25:d=20 -c:v mpeg2video -g 12 -pix_fmt yuv422p -f mxf")]
    [InlineData("bpyramid.mp4", "-f lavfi -i testsrc=size=96x64:rate=25:d=40 -c:v libx264 -pix_fmt yuv420p -bf 4 -b_strategy 2 -x264-params b-pyramid=normal:keyint=100")]
    [InlineData("vfr.mkv", "-f lavfi -i testsrc=size=96x64:rate=25:d=60 -vf select='not(mod(n\\,3))+gt(mod(n\\,17)\\,12)' -fps_mode vfr -c:v libx264 -pix_fmt yuv420p")]
    [InlineData("flv.flv", "-f lavfi -i testsrc=size=96x64:rate=25:d=30 -c:v libx264 -pix_fmt yuv420p -f flv")]
    public async Task Honest_files_pass_on_every_seed(string name, string encode)
    {
        if (!Available) return;

        string path = Make(name, encode);
        int failed = 0;
        for (int seed = 1; seed <= 6; seed++)
        {
            ModerationMediaSample sample = await SampleSeededAsync(path, "video/mp4", seed);
            if (sample.Failure is not null)
            {
                failed++;
                _output.WriteLine($"{name} seed {seed}: {sample.Failure}");
            }
        }

        _output.WriteLine($"{name}: refused {failed}/6");
        failed.Should().Be(0, name);
    }

    [Theory]
    [InlineData(15.5, 6)]
    [InlineData(15.0, 6)]
    [InlineData(14.0, 6)]
    [InlineData(8.0, 12)]
    public async Task A_copy_cut_whose_first_keyframe_comes_late_passes_on_every_seed(double start, int seconds)
    {
        if (!Available) return;

        // A 16 s group at 25 fps; the cut starts inside it, so the first keyframe shown comes 0.5, 1, 2 or 8 s into the file.
        string src = Make($"late-src-{start}.mp4", "-f lavfi -i testsrc=size=96x64:rate=25:d=40 -c:v libx264 -pix_fmt yuv420p -g 400 -bf 0 -sc_threshold 0");
        string cut = Make($"late-{start}.mp4", $"-ss {start.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i \"{src}\" -t {seconds} -c copy");
        int failed = 0;
        for (int seed = 1; seed <= 6; seed++)
        {
            ModerationMediaSample sample = await SampleSeededAsync(cut, "video/mp4", seed);
            if (sample.Failure is not null) failed++;
            sample.Images.Should().NotBeEmpty();
        }

        failed.Should().Be(0);
    }
}
