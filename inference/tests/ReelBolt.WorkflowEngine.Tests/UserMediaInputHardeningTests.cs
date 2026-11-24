#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Security ENG-3: a customer's media reaches ffmpeg/ffprobe in VideoAnalyze, VideoCompile and
/// TimelineRender. Every such input must carry <c>-protocol_whitelist file</c> and the demuxer
/// whitelist, and the staged copy must not keep a playlist extension, so an uploaded HLS playlist or
/// concat list cannot pull another local file into the customer's output.
/// </summary>
public class UserMediaInputHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eng3-" + Guid.NewGuid().ToString("N"));

    public UserMediaInputHardeningTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>
    /// Every <c>-i</c> that reads a file has both whitelists among its own input options. ffmpeg
    /// applies them to the next input only, so one pair at the front of the argv is not enough.
    /// An engine-authored <c>-f lavfi</c> or <c>-f concat</c> input is not user media.
    /// </summary>
    internal static void AssertEveryFileInputGuarded(IReadOnlyList<string> argv)
    {
        int inputs = 0;
        int segmentStart = 0;
        for (int i = 0; i < argv.Count; i++)
        {
            if (argv[i] != "-i")
                continue;

            List<string> options = argv.Skip(segmentStart).Take(i - segmentStart).ToList();
            segmentStart = i + 2;
            int forced = options.LastIndexOf("-f");
            if (forced >= 0 && forced + 1 < options.Count && options[forced + 1] is "lavfi" or "concat")
                continue;

            inputs++;
            string input = i + 1 < argv.Count ? argv[i + 1] : "<missing>";
            HasPair(options, "-protocol_whitelist", "file").Should().BeTrue($"input '{input}' needs its own -protocol_whitelist file");
            HasPair(options, "-format_whitelist", UserMediaInputFormats.Whitelist).Should().BeTrue($"input '{input}' needs its own -format_whitelist");
        }

        inputs.Should().BeGreaterThan(0, "the argv under test reads at least one file");
    }

    private static bool HasPair(List<string> options, string name, string value)
    {
        int at = options.LastIndexOf(name);
        return at >= 0 && at + 1 < options.Count && options[at + 1] == value;
    }

    public static IEnumerable<object[]> UserMediaArgv()
    {
        const string In = "/scratch/source.mp4";
        yield return ["motion", FfmpegArgvBuilder.BuildMotionArgs(In, 2)];
        yield return ["silence", FfmpegArgvBuilder.BuildSilenceDetectArgs(In, -30, 0.5)];
        yield return ["shots", FfmpegArgvBuilder.BuildShotDetectArgs(In, 0.3, 2)];
        yield return ["grid", FfmpegArgvBuilder.BuildGridSampleArgs(In, "/scratch/grid.rgb", 2, 32, 18, 2)];
        yield return ["audio", FfmpegArgvBuilder.BuildExtractAudioArgs(In, "/scratch/a.wav", 1, 2)];
        yield return ["keyframe", FfmpegArgvBuilder.BuildKeyframeArgs(In, "/scratch/k.jpg", 1, 640)];
        yield return ["cropped keyframe", FfmpegArgvBuilder.BuildCroppedKeyframeArgs(In, "/scratch/k.jpg", 1, 0.1, 0.1, 0.5, 0.5, 640)];
        yield return ["contact sheet", FfmpegArgvBuilder.BuildContactSheetArgs(In, "/scratch/c.jpg", [1, 2, 3], 960)];
        yield return ["sharpness", FfmpegArgvBuilder.BuildSharpnessPatchArgs(In, "/scratch/s.raw", 1, 64)];
        yield return ["thumbnail", FfmpegArgvBuilder.BuildThumbnailArgs(In, "/scratch/t.jpg", 1, 1280, 720, true, 3)];
        yield return ["extract frame", FfmpegArgvBuilder.BuildExtractFrameArgs(In, "/scratch/f.png", FrameAt.Time, 1, "png", null, null)];
        yield return ["scene cuts", SceneCutFinder.BuildArgs(In, 2, ["/scratch/a.txt", "/scratch/b.txt", "/scratch/c.txt"])];
        yield return ["analysis copy", AnalysisProxy.Build(In, "/scratch/copy.mp4", 2, null)];
        yield return ["placement still", PlacementStillArgvBuilder.Build(In, "/scratch/p.jpg", 1, 1920, 1080, true, [], "/fonts/f.ttf")];
        yield return ["caption band", CaptionPlacementProbe.BandArgs(In, 1, null, 0.7, 0.2).ToArray()];
        yield return ["still clip", StillImageClip.BuildArgs("/scratch/source.jpg", "/scratch/still.mp4", 1920, 1080).ToArray()];
        yield return ["source trim", VideoAnalyzeStepExecutor.BuildTrimArgs(In, "/scratch/window.mp4", 1, 4)];
    }

    [Theory]
    [MemberData(nameof(UserMediaArgv))]
    public void Every_user_media_input_of_the_analysis_and_compile_helpers_carries_both_whitelists(string name, string[] argv)
    {
        name.Should().NotBeEmpty();
        AssertEveryFileInputGuarded(argv);
    }

    [Fact]
    public void The_shared_probe_argv_carries_both_whitelists()
    {
        string[] argv = FfmpegArgvBuilder.BuildProbeArgs("/scratch/source.mp4");

        argv.Should().ContainInOrder("-protocol_whitelist", "file");
        argv.Should().ContainInOrder("-format_whitelist", UserMediaInputFormats.Whitelist);
        argv[^1].Should().Be("/scratch/source.mp4");
    }

    [Fact]
    public void The_whitelist_admits_media_and_pictures_and_no_demuxer_that_opens_other_files()
    {
        string[] formats = UserMediaInputFormats.Whitelist.Split(',');

        formats.Should().Contain(MediaDerivativeInputFormats.Whitelist.Split(','));
        formats.Should().Contain(["image2", "png_pipe", "jpeg_pipe", "webp_pipe", "gif"]);
        formats.Should().NotContain(["hls", "applehttp", "concat", "dash", "sdp", "rtsp", "lavfi", "imf", "webm_dash_manifest"]);
    }

    [Theory]
    [InlineData("projects/p/userFiles/a.mp4", ".bin", ".mp4")]
    [InlineData("projects/p/userFiles/a.MOV", ".bin", ".MOV")]
    [InlineData("projects/p/userFiles/a.jpg", ".bin", ".jpg")]
    [InlineData("projects/p/userFiles/a.png", ".mp4", ".png")]
    [InlineData("projects/p/userFiles/a.wav", ".mp3", ".wav")]
    [InlineData("projects/p/userFiles/a", ".mp4", ".mp4")]
    [InlineData("projects/p/userFiles/a", ".wav", ".wav")]
    [InlineData("projects/p/userFiles/a.m3u8", ".mp4", ".bin")]
    [InlineData("projects/p/userFiles/a.M3U", ".mp4", ".bin")]
    [InlineData("projects/p/userFiles/a.ffconcat", ".mp4", ".bin")]
    [InlineData("projects/p/userFiles/a.txt", ".wav", ".bin")]
    [InlineData("projects/p/userFiles/a.mpd", ".mp3", ".bin")]
    [InlineData("projects/p/userFiles/a.sdp", ".mp3", ".bin")]
    public void A_staged_copy_keeps_a_media_extension_and_never_a_playlist_one(string storageKey, string whenNone, string expected) =>
        UserMediaInputFormats.SafeExtension(storageKey, whenNone).Should().Be(expected);

    [Theory]
    [InlineData("projects/p/userFiles/a.m3u8", true)]
    [InlineData("projects/p/userFiles/a.M3U", true)]
    [InlineData("projects/p/userFiles/a.ffconcat", true)]
    [InlineData("projects/p/userFiles/a.mpd", true)]
    [InlineData("projects/p/userFiles/a.mp4", false)]
    [InlineData("projects/p/userFiles/a", false)]
    [InlineData(null, false)]
    public void A_playlist_name_is_recognised(string? storageKey, bool expected) =>
        UserMediaInputFormats.IsListFile(storageKey).Should().Be(expected);

    [Fact]
    public void A_refused_source_is_the_customers_input_not_a_refunded_platform_failure()
    {
        FailureClassifier.ForCode(UserMediaInputFormats.RefusedCode).Should().Be(FailureClass.UserInput);
        UserMediaInputFormats.RefusedMessage.Should().Contain("playlist");
    }

    // ---------------------------------------------------------------------------------------------
    // TimelineRender
    // ---------------------------------------------------------------------------------------------

    private static readonly AssetRef Asset = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null);
    private static readonly EditTimelineSettings Settings = new(108, 192, new Rational(10, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static EditTimeline Doc(TrackKind kind) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "doc", Settings,
            [new Track(Guid.Parse("22222222-0000-4000-8000-000000000000"), kind, kind == TrackKind.Video ? "V1" : "A1", null, 64, false, true, false, false, null,
                [new MediaClip(Guid.Parse("11111111-1111-4111-8111-111111111111"), "c", 0, 5, Asset)])],
            [], null, null);

    private static ResolvedRenderTarget Target() =>
        TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), Settings);

    private static Dictionary<AssetRef, LocalAsset> Assets() =>
        new() { [Asset] = new LocalAsset("/scratch/asset-000.mp4", "fp", new MediaProbeResult(1, 10, 1, 108, 192, "h264", "aac", 48000)) };

    [Fact]
    public void A_timeline_region_reads_its_clip_through_both_whitelists()
    {
        RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(
            SegmentPlanner.Plan(Doc(TrackKind.Video), null).Regions[0], Target(), Assets(), "/scratch/r.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 2);

        AssertEveryFileInputGuarded(argv.Args);
    }

    [Fact]
    public void The_timeline_audio_mix_reads_its_clip_through_both_whitelists()
    {
        EditTimeline doc = Doc(TrackKind.Audio);
        IReadOnlyList<string> argv = AudioMixBuilder.BuildMixArgs(SegmentPlanner.Plan(doc, null), Target(), Assets(), "/scratch/mix.wav", 1, doc.Tracks);

        AssertEveryFileInputGuarded(argv);
    }

    // ---------------------------------------------------------------------------------------------
    // The wave-1 proof of concept, against the real tools
    // ---------------------------------------------------------------------------------------------

    private FfmpegVideoToolRunner Runner() =>
        new(Options.Create(new VideoEditingOptions()), NullLogger<FfmpegVideoToolRunner>.Instance);

    /// <summary>Another tenant's media in another directory, and a playlist whose one segment names it by absolute path.</summary>
    private async Task<string> WritePlaylistAsync(FfmpegVideoToolRunner runner, string name)
    {
        string victimDir = Path.Combine(_dir, "victim");
        Directory.CreateDirectory(victimDir);
        string victim = Path.Combine(victimDir, "other-tenant-source.mp4");
        VideoToolResult made = await runner.RunFfmpegAsync(
            ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=64x64:r=10:d=1",
             "-c:v", "libx264", "-pix_fmt", "yuv420p", victim],
            TimeSpan.FromSeconds(60), CancellationToken.None);
        made.Succeeded.Should().BeTrue(made.StdErr);

        string playlist = Path.Combine(_dir, name);
        await File.WriteAllTextAsync(
            playlist,
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXTINF:1.0,\n" + victim + "\n#EXT-X-ENDLIST\n");
        return playlist;
    }

    [FfmpegToolsFact]
    public async Task The_probe_refuses_a_playlist_that_names_another_local_file()
    {
        FfmpegVideoToolRunner runner = Runner();
        string playlist = await WritePlaylistAsync(runner, "upload.m3u8");
        FfprobeMediaProbe probe = new(runner, Options.Create(new VideoEditingOptions()), NullLogger<FfprobeMediaProbe>.Instance);

        // The wave-1 shape: with the protocol whitelist alone ffprobe reads the playlist as HLS.
        VideoToolResult before = await runner.RunFfprobeAsync(
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-protocol_whitelist", "file", playlist],
            TimeSpan.FromSeconds(60), CancellationToken.None);
        before.Succeeded.Should().BeTrue("the fixture must be a working playlist, or the refusal below proves nothing");
        before.StdOut.Should().Contain("hls");

        Func<Task> act = () => probe.ProbeAsync(playlist, CancellationToken.None);

        await act.Should().ThrowAsync<MediaFormatNotAllowedException>();
    }

    [FfmpegToolsFact]
    public async Task A_guarded_input_refuses_a_playlist_and_a_concat_list_under_a_media_name()
    {
        FfmpegVideoToolRunner runner = Runner();
        string playlist = await WritePlaylistAsync(runner, "upload.m3u8");
        // A concat list is detected from its content, whatever it is called.
        string list = Path.Combine(_dir, "list.mp4");
        await File.WriteAllTextAsync(list, "ffconcat version 1.0\nfile 'victim/other-tenant-source.mp4'\n");

        foreach (string input in new[] { playlist, list })
        {
            string output = Path.Combine(_dir, "out-" + Guid.NewGuid().ToString("N") + ".mp4");
            VideoToolResult unguarded = await runner.RunFfmpegAsync(
                ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-i", input, "-c:v", "libx264", "-pix_fmt", "yuv420p", output],
                TimeSpan.FromSeconds(60), CancellationToken.None);
            unguarded.Succeeded.Should().BeTrue("without the whitelist the other file is transcoded into the output: " + unguarded.StdErr);
            File.Delete(output);

            VideoToolResult guarded = await runner.RunFfmpegAsync(
                ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", .. UserMediaInputFormats.InputOptions(), "-i", input,
                 "-c:v", "libx264", "-pix_fmt", "yuv420p", output],
                TimeSpan.FromSeconds(60), CancellationToken.None);
            guarded.Succeeded.Should().BeFalse();
            UserMediaInputFormats.IsWhitelistRefusal(guarded.StdErr).Should().BeTrue(guarded.StdErr);
            File.Exists(output).Should().BeFalse();
        }
    }

    [FfmpegToolsFact]
    public async Task The_whitelist_still_admits_the_media_and_pictures_these_steps_read()
    {
        FfmpegVideoToolRunner runner = Runner();
        FfprobeMediaProbe probe = new(runner, Options.Create(new VideoEditingOptions()), NullLogger<FfprobeMediaProbe>.Instance);
        (string Name, string[] Encode)[] fixtures =
        [
            ("v.mp4", ["-f", "lavfi", "-i", "testsrc2=s=64x64:r=10:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p"]),
            ("v.mkv", ["-f", "lavfi", "-i", "testsrc2=s=64x64:r=10:d=1", "-c:v", "ffv1"]),
            ("v.webm", ["-f", "lavfi", "-i", "testsrc2=s=64x64:r=10:d=1", "-c:v", "libvpx-vp9"]),
            ("p.png", ["-f", "lavfi", "-i", "testsrc2=s=64x64:r=10:d=1", "-frames:v", "1"]),
            ("p.jpg", ["-f", "lavfi", "-i", "testsrc2=s=64x64:r=10:d=1", "-frames:v", "1"]),
            ("p.webp", ["-f", "lavfi", "-i", "testsrc2=s=64x64:r=10:d=1", "-frames:v", "1"]),
            ("p.gif", ["-f", "lavfi", "-i", "testsrc2=s=64x64:r=10:d=1"]),
            ("a.wav", ["-f", "lavfi", "-i", "sine=d=1"]),
            ("a.mp3", ["-f", "lavfi", "-i", "sine=d=1"]),
            ("a.m4a", ["-f", "lavfi", "-i", "sine=d=1"]),
            ("a.ogg", ["-f", "lavfi", "-i", "sine=d=1"]),
            ("a.flac", ["-f", "lavfi", "-i", "sine=d=1"]),
        ];

        foreach ((string name, string[] encode) in fixtures)
        {
            string path = Path.Combine(_dir, name);
            VideoToolResult made = await runner.RunFfmpegAsync(
                ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", .. encode, path], TimeSpan.FromSeconds(60), CancellationToken.None);
            made.Succeeded.Should().BeTrue(name + ": " + made.StdErr);

            MediaProbeResult probed = await probe.ProbeAsync(path, CancellationToken.None);
            (probed.VideoCodec ?? probed.AudioCodec).Should().NotBeNull(name);

            // The same file under the neutral staging extension is still read from its content.
            string neutral = Path.Combine(_dir, name.Replace('.', '-') + "-neutral.bin");
            File.Copy(path, neutral);
            MediaProbeResult neutralProbe = await probe.ProbeAsync(neutral, CancellationToken.None);
            (neutralProbe.VideoCodec ?? neutralProbe.AudioCodec).Should().NotBeNull(name + " as .bin");
        }
    }
}
