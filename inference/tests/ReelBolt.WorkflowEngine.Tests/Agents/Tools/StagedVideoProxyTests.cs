using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// The delivery copy of an oversized staged video (<see cref="StagedVideoProxy"/>) and the render's
/// <c>&lt;OffthreadVideo&gt;</c> cache sized from what was staged (runs 36df4287 / 9bf15ff8: five 2160x3840
/// clips at once, "Could not extract frame from compositor").
/// </summary>
public class StagedVideoProxyTests
{
    private static MediaProbeResult Probe(int width, int height, int fpsNum = 30, int fpsDen = 1, string pixFmt = "yuv420p", string? transfer = null) =>
        new(10, fpsNum, fpsDen, width, height, "h264", "aac", 48000, PixFmt: pixFmt, ColorTransfer: transfer);

    // --- The decision ---------------------------------------------------------------------------

    [Theory]
    [InlineData(2160, 3840, "public/a.mp4", true, StagedVideoProxy.Reasons.Oversized)]
    [InlineData(3840, 2160, "public/a.MOV", true, StagedVideoProxy.Reasons.Oversized)]
    [InlineData(1440, 2560, "public/a.m4v", true, StagedVideoProxy.Reasons.Oversized)]
    [InlineData(1080, 1920, "public/a.mp4", false, StagedVideoProxy.Reasons.WithinSize)]
    [InlineData(720, 1280, "public/a.mp4", false, StagedVideoProxy.Reasons.WithinSize)]
    [InlineData(2160, 3840, "public/a.webm", false, StagedVideoProxy.Reasons.Container)]
    [InlineData(2160, 3840, "public/a.mkv", false, StagedVideoProxy.Reasons.Container)]
    [InlineData(0, 0, "public/a.mp4", false, StagedVideoProxy.Reasons.NoVideo)]
    public void Only_an_oversized_mp4_family_video_gets_a_copy(int width, int height, string destination, bool copy, string reason)
    {
        StagedVideoProxy.Decision decision = StagedVideoProxy.Decide(Probe(width, height), destination, StagedVideoProxy.DefaultMaxLongSide);

        decision.MakeCopy.Should().Be(copy);
        decision.Reason.Should().Be(reason);
    }

    [Fact]
    public void A_transparent_video_is_never_copied()
    {
        StagedVideoProxy.Decide(Probe(3840, 2160, pixFmt: "yuva444p10le"), "public/a.mov", 1920).MakeCopy.Should().BeFalse();
        StagedVideoProxy.Decide(Probe(3840, 2160) with { AlphaModeTag = "1" }, "public/a.mp4", 1920).Reason
            .Should().Be(StagedVideoProxy.Reasons.HasAlpha);
    }

    [Fact]
    public void The_long_side_follows_the_runs_chosen_resolution_and_ignores_a_tiny_setting()
    {
        StagedVideoProxy.MaxLongSideFor(1920, null).Should().Be(1920);
        StagedVideoProxy.MaxLongSideFor(1920, 1080).Should().Be(1920);
        StagedVideoProxy.MaxLongSideFor(1920, 2160).Should().Be(3840, "a run rendered at 2160p keeps its 4K footage");
        StagedVideoProxy.MaxLongSideFor(2560, 720).Should().Be(2560);
        StagedVideoProxy.MaxLongSideFor(200, null).Should().Be(StagedVideoProxy.DefaultMaxLongSide);

        StagedVideoProxy.Decide(Probe(2160, 3840), "public/a.mp4", StagedVideoProxy.MaxLongSideFor(1920, 2160)).MakeCopy.Should().BeFalse();
    }

    [Theory]
    [InlineData(30, 1, 30)]
    [InlineData(60000, 1001, 60)]
    [InlineData(24000, 1001, 24)]
    [InlineData(25, 1, 25)]
    [InlineData(240, 1, 60)]
    [InlineData(0, 1, 30)]
    [InlineData(30, 0, 30)]
    public void The_gop_is_about_one_second(int num, int den, int expected) =>
        StagedVideoProxy.GopFrames(num, den).Should().Be(expected);

    // --- The argv -------------------------------------------------------------------------------

    [Fact]
    public void The_copy_keeps_every_frame_and_fixes_size_range_and_seeking()
    {
        string[] args = StagedVideoProxy.BuildArgs("/scratch/source", "/scratch/copy.mp4", Probe(2160, 3840, 60000, 1001, "yuvj420p"), 1920, null, 4);
        string joined = string.Join(' ', args);

        args.Should().ContainInOrder("-fps_mode", "passthrough");
        joined.Should().NotContain("fps=", "the frame rate is never resampled")
            .And.NotContain("-r ");
        string filter = args[Array.IndexOf(args, "-vf") + 1];
        filter.Should().Be(
            "scale=w='if(gte(iw,ih),min(iw,1920),-2)':h='if(gte(iw,ih),-2,min(ih,1920))':flags=lanczos:out_range=tv,format=yuv420p");
        args.Should().ContainInOrder("-c:v", "libx264").And.ContainInOrder("-pix_fmt", "yuv420p").And.ContainInOrder("-color_range", "tv");
        args.Should().ContainInOrder("-g", "60", "-keyint_min", "60", "-sc_threshold", "0");
        args.Should().ContainInOrder("-movflags", "+faststart").And.ContainInOrder("-f", "mp4");
        args.Should().ContainInOrder("-threads", "4");
        args[^1].Should().Be("/scratch/copy.mp4");
    }

    [Fact]
    public void The_input_is_held_to_the_file_protocol_and_the_demuxer_whitelist()
    {
        string[] args = StagedVideoProxy.BuildArgs("/scratch/source", "/scratch/copy.mp4", Probe(2160, 3840), 1920, null, 0);

        args.Should().ContainInOrder("-protocol_whitelist", "file", "-format_whitelist", MediaDerivativeInputFormats.Whitelist, "-i", "/scratch/source");
        Array.IndexOf(args, "-format_whitelist").Should().BeLessThan(Array.IndexOf(args, "-i"));
        args.Should().NotContain("-threads", "0 threads leaves ffmpeg's own default");
        args.Should().ContainInOrder("-map", "0:v:0", "-map", "0:a:0?");
    }

    [Fact]
    public void An_hdr_source_is_tone_mapped_first_and_tagged_bt709()
    {
        string[] args = StagedVideoProxy.BuildArgs("/s", "/c.mp4", Probe(2160, 3840, transfer: "arib-std-b67"), 1920, "hlg", 0);

        string filter = args[Array.IndexOf(args, "-vf") + 1];
        filter.Should().StartWith(ReelBolt.WorkflowEngine.Services.Video.TimelineRender.HdrToneMap.Filter("hlg") + ",scale=");
        args.Should().ContainInOrder("-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709");

        StagedVideoProxy.BuildArgs("/s", "/c.mp4", Probe(2160, 3840), 1920, null, 0).Should().NotContain("-color_trc");
    }

    // --- The cache key ----------------------------------------------------------------------------

    [Fact]
    public void The_cache_key_is_per_project_and_moves_with_the_object_and_the_size()
    {
        Guid project = Guid.NewGuid();
        string key = StagedVideoProxy.CacheKeyStem(project, $"projects/{project}/userFiles/a.mp4", 100, "\"abc\"", 1920);

        key.Should().StartWith($"projects/{project}/agentFiles/render-proxies/");
        key.Should().Be(StagedVideoProxy.CacheKeyStem(project, $"projects/{project}/userFiles/a.mp4", 100, "abc", 1920), "quotes around an ETag are not identity");
        new[]
        {
            StagedVideoProxy.CacheKeyStem(project, $"projects/{project}/userFiles/b.mp4", 100, "abc", 1920),
            StagedVideoProxy.CacheKeyStem(project, $"projects/{project}/userFiles/a.mp4", 101, "abc", 1920),
            StagedVideoProxy.CacheKeyStem(project, $"projects/{project}/userFiles/a.mp4", 100, "abd", 1920),
            StagedVideoProxy.CacheKeyStem(project, $"projects/{project}/userFiles/a.mp4", 100, null, 1920),
            StagedVideoProxy.CacheKeyStem(project, $"projects/{project}/userFiles/a.mp4", 100, "abc", 3840),
        }.Should().NotContain(key).And.OnlyHaveUniqueItems();
    }

    // --- Sizing the render's video cache ---------------------------------------------------------

    private const long Floor = 256L * 1024 * 1024;
    private const long Ceiling = 1L * 1024 * 1024 * 1024;

    [Fact]
    public void No_staged_video_keeps_the_configured_default()
    {
        ReactRemotionSandboxTools.SizeOffthreadVideoCache([], 2, Floor, Ceiling).Should().Be(Floor);
    }

    [Fact]
    public void One_small_video_keeps_the_default_floor()
    {
        long frame = 1080L * 1920 * ReactRemotionSandboxTools.OffthreadVideoBytesPerPixel;
        ReactRemotionSandboxTools.SizeOffthreadVideoCache([frame], 2, Floor, Ceiling).Should().Be(Floor);
    }

    [Fact]
    public void The_repro_grid_gets_room_for_every_clip_in_every_tab_up_to_the_ceiling()
    {
        // Six 1080x1920 copies at concurrency 2: 2 x 6 x 12 frames x 8.3 MB = 1.19 GB, held to the 1 GiB ceiling
        // (the size that rendered the repro frame for frame; 256 MiB stalled).
        long frame = 1080L * 1920 * ReactRemotionSandboxTools.OffthreadVideoBytesPerPixel;
        List<long> six = [.. Enumerable.Repeat(frame, 6)];
        ReactRemotionSandboxTools.SizeOffthreadVideoCache(six, 2, Floor, Ceiling).Should().Be(Ceiling);

        // Three copies at one tab: 3 x 12 x 8.3 MB = 299 MB, above the floor and below the ceiling.
        ReactRemotionSandboxTools.SizeOffthreadVideoCache([frame, frame, frame], 1, Floor, Ceiling)
            .Should().Be(3L * ReactRemotionSandboxTools.OffthreadVideoFramesPerVideo * frame);
    }

    [Fact]
    public void A_ceiling_below_the_configured_default_never_lowers_the_default()
    {
        long frame = 2160L * 3840 * 4;
        ReactRemotionSandboxTools.SizeOffthreadVideoCache([frame, frame], 2, Floor, 64L * 1024 * 1024).Should().Be(Floor);
        ReactRemotionSandboxTools.SizeOffthreadVideoCache([long.MaxValue, long.MaxValue], int.MaxValue, Floor, Ceiling).Should().Be(Ceiling);
    }

    // --- The failure names the video ---------------------------------------------------------------

    private const string DelayRenderTimeout =
        "An error occurred while rendering frame 25:\n Error  A delayRender() \"Fetching http://localhost:3000/proxy?src=http%3A%2F%2Flocalhost%3A3000%2Fpublic%2Fad-latte-art-topdown.mp4&time=0.8333&transparent=false&toneMapped=true from server\" was called but not cleared after 28000ms.";

    [Fact]
    public void A_frame_the_compositor_never_delivered_is_a_video_read_failure()
    {
        ReactRemotionSandboxTools.IsVideoFrameReadFailure(null, DelayRenderTimeout).Should().BeTrue();
        ReactRemotionSandboxTools.IsVideoFrameReadFailure("Could not extract frame from compositor", null).Should().BeTrue();
        ReactRemotionSandboxTools.IsVideoFrameReadFailure(null, "A delayRender() \"Loading <Img>\" was called but not cleared").Should().BeFalse();
    }

    [Fact]
    public void The_failing_videos_are_named_from_the_frame_urls_and_the_quoted_paths()
    {
        ReactRemotionSandboxTools.FailingVideoPaths(null, DelayRenderTimeout, ["public/ad-latte-art-topdown.mp4", "public/b.mp4"])
            .Should().Equal("public/ad-latte-art-topdown.mp4");

        ReactRemotionSandboxTools.FailingVideoPaths(
                "Could not extract frame from compositor: http://localhost:3000/public/clips/b.mp4", null,
                ["public/a.mp4", "public/clips/b.mp4"])
            .Should().Equal("public/clips/b.mp4");
    }
}
