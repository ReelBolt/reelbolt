using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Enhance;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Enhance;

public class LocalVideoEnhancerTests
{
    private static SourceFacts Facts(bool hasAudio = true, int fpsNum = 24) =>
        new(320, 180, fpsNum, 1, 2 * fpsNum, "yuv420p", null, null, 8, 2.0, hasAudio);

    private static EnhanceFilterPlan Plan() =>
        new("hqdn3d=3:2:6:4.5,format=yuv420p", 320, 180, 24, "hqdn3d", []);

    [Fact]
    public void BuildArgs_copies_compatible_source_audio()
    {
        LocalVideoEnhancer.BuildArgs("/s/in.mp4", "/s/out.mp4", Plan(), Facts(), "aac").Should().Equal(
            "-hide_banner", "-nostdin", "-y", "-v", "error",
            "-progress", "pipe:1", "-nostats",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", "/s/in.mp4",
            "-map", "0:v:0",
            "-map", "0:a", "-c:a", "copy",
            "-vf", "hqdn3d=3:2:6:4.5,format=yuv420p",
            "-c:v", "libx264", "-crf", "20", "-preset", "veryfast",
            "-t", "2.000000", "-frames:v", "49",
            "-movflags", "+faststart", "-f", "mp4", "/s/out.mp4");
    }

    [Fact]
    public void BuildArgs_re_encodes_an_incompatible_source_audio_and_drops_missing_audio()
    {
        LocalVideoEnhancer.BuildArgs("/s/in.mp4", "/s/out.mp4", Plan(), Facts(), "pcm_s16le").Should().Equal(
            "-hide_banner", "-nostdin", "-y", "-v", "error",
            "-progress", "pipe:1", "-nostats",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", "/s/in.mp4",
            "-map", "0:v:0",
            "-map", "0:a", "-c:a", "aac", "-b:a", "192k",
            "-vf", "hqdn3d=3:2:6:4.5,format=yuv420p",
            "-c:v", "libx264", "-crf", "20", "-preset", "veryfast",
            "-t", "2.000000", "-frames:v", "49",
            "-movflags", "+faststart", "-f", "mp4", "/s/out.mp4");

        string[] silent = LocalVideoEnhancer.BuildArgs("/s/in.mp4", "/s/out.mp4", Plan(), Facts(hasAudio: false), null);
        silent.Should().Contain("-an");
        silent.Should().NotContain("0:a");
        silent.Should().NotContain("-c:a");
    }

    [Fact]
    public void Remux_build_args_take_picture_video_and_source_audio()
    {
        SourceFacts source = Facts();
        SourceFacts picture = Facts(hasAudio: false);

        EnhancedAudioRemux.BuildArgs("/s/src.mp4", "/s/pic.mp4", "/s/out.mp4", source, picture,
                new RemuxTarget(320, 180, 24, 1), "aac").Should().Equal(
            "-hide_banner", "-nostdin", "-y", "-v", "error",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", "/s/src.mp4",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", "/s/pic.mp4",
            "-map", "1:v:0",
            "-map", "0:a", "-c:a", "copy",
            "-vf", "fps=24/1,format=yuv420p",
            "-c:v", "libx264", "-crf", "20", "-preset", "veryfast",
            "-t", "2.000000",
            // 2 s at 24 fps is 48 frames, plus the two-frame allowance.
            "-frames:v", "50",
            "-movflags", "+faststart", "-f", "mp4", "/s/out.mp4");
    }

    [Fact]
    public void Remux_conforms_only_when_the_picture_differs()
    {
        SourceFacts source = Facts();
        SourceFacts picture = Facts(hasAudio: false);

        string[] args = EnhancedAudioRemux.BuildArgs("/s/src.mp4", "/s/pic.mp4", "/s/out.mp4", source, picture,
            new RemuxTarget(640, 360, 24, 1), "aac");
        args.Should().Contain("fps=24/1,scale=-2:360:flags=lanczos,format=yuv420p");
        args.Should().NotContain("fps=24/1,format=yuv420p");
    }

    [Fact]
    public void Remux_delivers_exactly_the_target_size_when_the_vendor_picture_has_another_shape()
    {
        SourceFacts source = Facts();
        // A vendor picture 2048x720 (a wider frame than the 2688x1080 canvas that was priced and plan-gated).
        SourceFacts vendor = new(2048, 720, 24, 1, 48, "yuv420p", null, null, 8, 2.0, false);

        string vf = System.Linq.Enumerable.First(System.Linq.Enumerable.Skip(System.Linq.Enumerable.SkipWhile((string[])EnhancedAudioRemux.BuildArgs("/s/src.mp4", "/s/pic.mp4", "/s/out.mp4", source, vendor,
            new RemuxTarget(2688, 1080, 24, 1), "aac"), x => x != "-vf"), 1));

        vf.Should().Contain("scale=2688:1080:force_original_aspect_ratio=decrease").And.Contain("pad=2688:1080:(ow-iw)/2:(oh-ih)/2:black");
        vf.Should().NotContain("scale=-2", "the vendor's shape is not kept");

        // A picture that already has the target's shape keeps the plain scale rule.
        SourceFacts sameShape = new(1344, 540, 24, 1, 48, "yuv420p", null, null, 8, 2.0, false);
        string[] plainArgs = EnhancedAudioRemux.BuildArgs("/s/src.mp4", "/s/pic.mp4", "/s/out.mp4", source, sameShape,
            new RemuxTarget(2688, 1080, 24, 1), "aac");
        string plain = plainArgs[Array.IndexOf(plainArgs, "-vf") + 1];
        plain.Should().Contain("scale=-2:1080").And.NotContain("pad=");
    }

    [Fact]
    public void Remux_delivers_exactly_the_canvas_for_a_picture_a_hair_off_its_shape()
    {
        SourceFacts source = Facts();
        // 1596x720 against a 1600x720 canvas: the plain -2:720 scale would deliver 1596 wide.
        SourceFacts nearShape = new(1596, 720, 24, 1, 48, "yuv420p", null, null, 8, 2.0, false);
        string[] args = EnhancedAudioRemux.BuildArgs("/s/src.mp4", "/s/pic.mp4", "/s/out.mp4", source, nearShape,
            new RemuxTarget(1600, 720, 24, 1), "aac");
        string vf = args[Array.IndexOf(args, "-vf") + 1];
        vf.Should().Contain("scale=1600:720:flags=lanczos").And.NotContain("scale=-2").And.NotContain("pad=");

        // An exact-shape picture keeps the plain rule (the width follows the height and equals the canvas).
        SourceFacts exact = new(800, 360, 24, 1, 48, "yuv420p", null, null, 8, 2.0, false);
        string[] plainArgs = EnhancedAudioRemux.BuildArgs("/s/src.mp4", "/s/pic.mp4", "/s/out.mp4", source, exact,
            new RemuxTarget(1600, 720, 24, 1), "aac");
        plainArgs[Array.IndexOf(plainArgs, "-vf") + 1].Should().Contain("scale=-2:720:flags=lanczos");
    }

    [Fact]
    public void Pad_repair_pads_the_picture_only_and_copies_every_audio_stream()
    {
        string[] args = EnhanceDuration.BuildPadArgs("/s/o.mp4", "/s/f.mp4", 2.0, 1.0);
        args.Should().ContainInOrder("-map", "0:v:0", "-map", "0:a?");
        args.Should().ContainInOrder("-c:a", "copy");
        args.Should().NotContain("-af");
        args.Should().NotContain("aac");
        args.Should().NotContain(a => a.Contains("apad", StringComparison.Ordinal));
        args.Should().Contain(a => a.StartsWith("tpad=", StringComparison.Ordinal));
        EnhanceDuration.BuildTrimArgs("/s/o.mp4", "/s/f.mp4", 2.0)
            .Should().ContainInOrder("-map", "0:v:0", "-map", "0:a?");
    }

    [Fact]
    public async Task Remux_reads_the_picture_facts_from_the_whitelisted_probe_only()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rb-enhance-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string output = System.IO.Path.Combine(dir, "out.mp4");
            System.IO.File.WriteAllText(output, "x");
            var runner = new Mock<IVideoToolRunner>();
            runner.Setup(r => r.RunFfprobeAsync(
                    It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VideoToolResult(
                    0,
                    "{\"streams\":[{\"codec_type\":\"video\",\"width\":320,\"height\":180,"
                    + "\"avg_frame_rate\":\"24/1\",\"pix_fmt\":\"yuv420p\"}],\"format\":{\"duration\":\"2.0\"}}",
                    "", false));
            runner.Setup(r => r.RunFfmpegAsync(
                    It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VideoToolResult(0, "", "", false));
            var probe = new Mock<IMediaProbe>();
            probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(2.0, 24, 1, 320, 180, "h264", "aac", 48000));

            var remux = new EnhancedAudioRemux(
                runner.Object, probe.Object, Options.Create(new LocalEnhanceOptions()));
            LocalEnhanceResult result = await remux.RemuxAsync(
                "/s/src.mp4", "/s/pic.mp4", output, null, CancellationToken.None);

            result.OutWidth.Should().Be(320);
            result.OutHeight.Should().Be(180);
            probe.Verify(p => p.ProbeAsync("/s/pic.mp4", It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Interpolate_bound_scales_the_frame_cap_by_the_factor()
    {
        EnhanceFilterPlan plan = new("minterpolate=fps=96/1", 320, 180, 96, "minterpolate", []);
        string[] args = LocalVideoEnhancer.BuildArgs("/s/in.mp4", "/s/out.mp4", plan, Facts(), "aac");
        args.Should().ContainInOrder("-t", "2.000000", "-frames:v", "193", "-movflags");
    }

    [Fact]
    public void Every_enhance_argv_pins_both_whitelists_before_each_input()
    {
        SourceFacts facts = Facts();
        var argvs = new List<string[]>
        {
            LocalVideoEnhancer.BuildArgs("/s/in.mp4", "/s/out.mp4", Plan(), facts, "aac"),
            EnhancedAudioRemux.BuildArgs(
                "/s/src.mp4", "/s/pic.mp4", "/s/out.mp4", facts, facts, new RemuxTarget(320, 180, 24, 1), "aac"),
            EnhanceDuration.BuildTrimArgs("/s/o.mp4", "/s/f.mp4", 2.0),
            EnhanceDuration.BuildPadArgs("/s/o.mp4", "/s/f.mp4", 2.0, 1.0),
            LocalVideoEnhancer.BuildFixArgs("/s/o.mp4", "/s/f.mp4", facts, 3.0),
            LocalVideoEnhancer.BuildFixArgs("/s/o.mp4", "/s/f.mp4", facts, 1.0),
        };

        foreach (string[] argv in argvs)
        {
            int inputs = 0;
            int segmentStart = 0;
            for (int i = 0; i < argv.Length; i++)
            {
                if (argv[i] != "-i")
                {
                    continue;
                }

                inputs++;
                string[] segment = argv[segmentStart..i];
                int p = Array.IndexOf(segment, "-protocol_whitelist");
                int f = Array.IndexOf(segment, "-format_whitelist");
                p.Should().BeGreaterThanOrEqualTo(0, string.Join(' ', argv));
                segment[p + 1].Should().Be("file");
                f.Should().BeGreaterThanOrEqualTo(0, string.Join(' ', argv));
                segment[f + 1].Should().Be(MediaDerivativeInputFormats.Whitelist);
                segmentStart = i + 2;
            }

            inputs.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task A_stream_duration_over_the_cap_is_refused_even_when_the_format_is_under_it()
    {
        var runner = new Mock<IVideoToolRunner>();
        runner.Setup(r => r.RunFfprobeAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(
                0, "{\"streams\":[{\"codec_type\":\"video\",\"duration\":\"9000.0\",\"nb_frames\":\"216000\"}]}", "", false));
        var probe = new Mock<IMediaProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(10.0, 24, 1, 320, 180, "h264", "aac", 48000));

        var enhancer = new LocalVideoEnhancer(
            runner.Object, probe.Object, Options.Create(new LocalEnhanceOptions()));

        Func<Task> act = () => enhancer.EnhanceAsync(
            "/s/in.mp4", "/s/out.mp4",
            new LocalEnhanceRequest { Op = VideoEnhanceOp.Denoise, DenoiseStrength = DenoiseStrength.Light },
            null, CancellationToken.None);

        (await act.Should().ThrowAsync<EnhanceException>()).Which.Code.Should().Be("ENHANCE_SOURCE_TOO_LONG");
        runner.Verify(r => r.RunFfmpegAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(),
            It.IsAny<Action<string>>(), It.IsAny<VideoToolPriority>()), Times.Never);
    }

    [Fact]
    public async Task A_picture_that_fails_the_probe_is_refused_before_the_remux_runs()
    {
        var runner = new Mock<IVideoToolRunner>();
        runner.Setup(r => r.RunFfprobeAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> argv, TimeSpan _, CancellationToken _) => argv[^1] == "/s/pic.mp4"
                ? new VideoToolResult(1, "", "invalid data", false)
                : new VideoToolResult(0, "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":320,\"height\":180,\"r_frame_rate\":\"24/1\",\"duration\":\"2.0\",\"nb_frames\":\"48\"},{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"sample_rate\":\"48000\",\"channels\":2}],\"format\":{\"duration\":\"2.0\"}}", "", false));
        var probe = new Mock<IMediaProbe>();

        var remux = new EnhancedAudioRemux(
            runner.Object, probe.Object, Options.Create(new LocalEnhanceOptions()));

        Func<Task> act = () => remux.RemuxAsync("/s/src.mp4", "/s/pic.mp4", "/s/out.mp4", null, CancellationToken.None);

        (await act.Should().ThrowAsync<EnhanceException>()).Which.Code.Should().Be("ENHANCE_PICTURE_INVALID");
        runner.Verify(r => r.RunFfmpegAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_picture_with_no_video_stream_is_refused()
    {
        var runner = new Mock<IVideoToolRunner>();
        runner.Setup(r => r.RunFfprobeAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "{\"streams\":[{\"codec_type\":\"audio\"}]}", "", false));
        var probe = new Mock<IMediaProbe>();
        var remux = new EnhancedAudioRemux(
            runner.Object, probe.Object, Options.Create(new LocalEnhanceOptions()));

        Func<Task> act = () => remux.RemuxAsync("/s/src.mp4", "/s/pic.mp4", "/s/out.mp4", null, CancellationToken.None);

        (await act.Should().ThrowAsync<EnhanceException>()).Which.Code.Should().Be("ENHANCE_PICTURE_INVALID");
    }

    [Fact]
    public async Task Forced_duration_mismatch_is_a_typed_failure()
    {
        var runner = new Mock<IVideoToolRunner>();
        runner.Setup(r => r.RunFfprobeAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> argv, TimeSpan _, CancellationToken _) => new VideoToolResult(0, argv[^1] == "/s/out.mp4" ? "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":320,\"height\":180,\"r_frame_rate\":\"24/1\",\"duration\":\"10.2\",\"nb_frames\":\"240\"},{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"sample_rate\":\"48000\",\"channels\":2}],\"format\":{\"duration\":\"10.2\"}}" : "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":320,\"height\":180,\"r_frame_rate\":\"24/1\",\"duration\":\"10.0\",\"nb_frames\":\"240\"},{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"sample_rate\":\"48000\",\"channels\":2}],\"format\":{\"duration\":\"10.0\"}}", "", false));
        runner.Setup(r => r.RunFfmpegAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>>(), It.IsAny<VideoToolPriority>()))
            .ReturnsAsync(new VideoToolResult(0, "", "", false));
        runner.Setup(r => r.RunFfmpegAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "", "", false));

        // The source (10 s) and the OUTPUT (10.2 s: a mismatch) are both measured through the whitelisted runner probe.
        var probe = new Mock<IMediaProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(10.2, 24, 1, 320, 180, "h264", "aac", 48000));

        var enhancer = new LocalVideoEnhancer(
            runner.Object, probe.Object, Options.Create(new LocalEnhanceOptions()));

        Func<Task> act = () => enhancer.EnhanceAsync(
            "/s/in.mp4", "/s/out.mp4",
            new LocalEnhanceRequest { Op = VideoEnhanceOp.Denoise, DenoiseStrength = DenoiseStrength.Light },
            null, CancellationToken.None);

        (await act.Should().ThrowAsync<EnhanceException>()).Which.Code.Should().Be("ENHANCE_DURATION_MISMATCH");
    }

    [Fact]
    public async Task Refusal_from_the_builder_is_a_typed_failure()
    {
        var runner = new Mock<IVideoToolRunner>();
        runner.Setup(r => r.RunFfprobeAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":1920,\"height\":1080,\"r_frame_rate\":\"24/1\",\"duration\":\"2.0\",\"nb_frames\":\"48\"},{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"sample_rate\":\"48000\",\"channels\":2}],\"format\":{\"duration\":\"2.0\"}}", "", false));
        var probe = new Mock<IMediaProbe>();

        var enhancer = new LocalVideoEnhancer(
            runner.Object, probe.Object, Options.Create(new LocalEnhanceOptions()));

        Func<Task> act = () => enhancer.EnhanceAsync(
            "/s/in.mp4", "/s/out.mp4",
            new LocalEnhanceRequest { Op = VideoEnhanceOp.Upscale, TargetHeight = 720 },
            null, CancellationToken.None);

        (await act.Should().ThrowAsync<EnhanceException>()).Which.Code.Should().Be("ENHANCE_NOT_LARGER");
    }

    [Fact]
    public async Task A_source_whose_stream_cannot_be_measured_is_refused_not_trusted_on_its_container_length()
    {
        var runner = new Mock<IVideoToolRunner>();
        runner.Setup(r => r.RunFfprobeAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(1, "", "boom", false));
        var probe = new Mock<IMediaProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(10.0, 24, 1, 320, 180, "h264", "aac", 48000));
        var enhancer = new LocalVideoEnhancer(runner.Object, probe.Object, Options.Create(new LocalEnhanceOptions()));

        Func<Task> act = () => enhancer.EnhanceAsync(
            "/s/in.mp4", "/s/out.mp4",
            new LocalEnhanceRequest { Op = VideoEnhanceOp.Denoise, DenoiseStrength = DenoiseStrength.Light }, null, CancellationToken.None);

        (await act.Should().ThrowAsync<EnhanceException>()).Which.Code.Should().Be("ENHANCE_PROBE_FAILED");
        runner.Verify(r => r.RunFfmpegAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(),
            It.IsAny<Action<string>>(), It.IsAny<VideoToolPriority>()), Times.Never);
    }

    [Fact]
    public async Task The_stream_probe_and_the_whitelisted_follow_up_probe_both_carry_both_whitelists()
    {
        List<string[]> argvs = [];
        var runner = new Mock<IVideoToolRunner>();
        runner.Setup(r => r.RunFfprobeAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((a, _, _) => argvs.Add([.. a]))
            .ReturnsAsync(new VideoToolResult(0,
                "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":320,\"height\":180,\"r_frame_rate\":\"24/1\",\"duration\":\"2.0\"}],\"format\":{\"duration\":\"2.0\"}}", "", false));

        await EnhanceInputGuards.ProbeStreamAsync(runner.Object, "/s/in.mp4", TimeSpan.FromSeconds(5), CancellationToken.None);
        await EnhanceInputGuards.ProbeWhitelistedAsync(runner.Object, "/s/in2.mp4", "source", CancellationToken.None);

        argvs.Should().HaveCount(2);
        foreach (string[] argv in argvs)
        {
            // An ffprobe argv has no -i: the path is the last argument and the whitelists come right before it.
            string[] before = argv[..^1];
            before.Should().Contain("-protocol_whitelist").And.Contain("-format_whitelist");
            before[Array.IndexOf(before, "-protocol_whitelist") + 1].Should().Be("file");
            before[Array.IndexOf(before, "-format_whitelist") + 1].Should().Be(MediaDerivativeInputFormats.Whitelist);
        }
    }

    [Fact]
    public void The_hosted_remux_is_bounded_by_frames_as_well_as_by_time()
    {
        SourceFacts source = new(320, 180, 24, 1, 48, "yuv420p", null, null, 8, 2.0, true);
        string[] args = EnhancedAudioRemux.BuildArgs("/s/src.mp4", "/s/pic.mp4", "/s/out.mp4", source, source, new RemuxTarget(320, 180, 60, 1), "aac");

        args[Array.IndexOf(args, "-frames:v") + 1].Should().Be("122", "2 s at 60 fps plus two frames");
        Array.IndexOf(args, "-frames:v").Should().BeLessThan(Array.IndexOf(args, "/s/out.mp4"));
    }

    [Fact]
    public async Task The_output_verification_probe_carries_both_whitelists_and_a_zero_length_output_is_a_failed_probe()
    {
        List<string[]> argvs = [];
        var runner = new Mock<IVideoToolRunner>();
        runner.Setup(r => r.RunFfprobeAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> argv, TimeSpan _, CancellationToken _) =>
            {
                argvs.Add([.. argv]);
                string duration = argv[^1] == "/s/out.mp4" ? "0.0" : "2.0";
                return new VideoToolResult(0,
                    "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":320,\"height\":180,\"r_frame_rate\":\"24/1\",\"duration\":\"2.0\",\"nb_frames\":\"48\"}],\"format\":{\"duration\":\"" + duration + "\"}}",
                    "", false);
            });
        runner.Setup(r => r.RunFfmpegAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>>(), It.IsAny<VideoToolPriority>()))
            .ReturnsAsync(new VideoToolResult(0, "", "", false));
        var enhancer = new LocalVideoEnhancer(runner.Object, new Mock<IMediaProbe>(MockBehavior.Strict).Object, Options.Create(new LocalEnhanceOptions()));

        Func<Task> act = () => enhancer.EnhanceAsync(
            "/s/in.mp4", "/s/out.mp4",
            new LocalEnhanceRequest { Op = VideoEnhanceOp.Denoise, DenoiseStrength = DenoiseStrength.Light }, null, CancellationToken.None);

        (await act.Should().ThrowAsync<EnhanceException>()).Which.Code.Should().Be("ENHANCE_PROBE_FAILED");
        string[] outputProbe = argvs[^1];
        outputProbe[^1].Should().Be("/s/out.mp4");
        string[] before = outputProbe[..^1];
        before[Array.IndexOf(before, "-protocol_whitelist") + 1].Should().Be("file");
        before[Array.IndexOf(before, "-format_whitelist") + 1].Should().Be(MediaDerivativeInputFormats.Whitelist);
    }
}
