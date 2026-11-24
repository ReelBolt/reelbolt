using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-105: the exact argv the ExtractFrame step hands ffmpeg.</summary>
public class ExtractFrameArgvTests
{
    private static string Filter(string[] argv) => argv[argv.ToList().IndexOf("-vf") + 1];

    [Fact]
    public void Time_seeks_before_the_input_and_takes_one_png_frame()
    {
        string[] argv = FfmpegArgvBuilder.BuildExtractFrameArgs("in.mp4", "out.png", FrameAt.Time, 12.25, "png", null, null);
        var args = argv.ToList();

        args[args.IndexOf("-ss") + 1].Should().Be("12.25");
        args.IndexOf("-i").Should().Be(args.IndexOf("-ss") + 2, "-ss before -i seeks instead of decoding up to the frame");
        args[args.IndexOf("-frames:v") + 1].Should().Be("1");
        args[args.IndexOf("-c:v") + 1].Should().Be("png");
        args.Should().NotContain("-sseof");
        args[^1].Should().Be("out.png");
        args.Should().ContainInOrder("-protocol_whitelist", "file");
    }

    [Fact]
    public void First_takes_the_first_frame_without_a_seek()
    {
        string[] argv = FfmpegArgvBuilder.BuildExtractFrameArgs("in.mp4", "out.png", FrameAt.First, null, "png", null, null);

        argv.Should().NotContain("-ss").And.NotContain("-sseof");
        argv[argv.ToList().IndexOf("-frames:v") + 1].Should().Be("1");
    }

    [Fact]
    public void Last_seeks_from_the_end_and_keeps_overwriting_one_image_so_the_final_frame_wins()
    {
        string[] argv = FfmpegArgvBuilder.BuildExtractFrameArgs("in.mp4", "out.png", FrameAt.Last, null, "png", null, null);
        var args = argv.ToList();

        args[args.IndexOf("-sseof") + 1].Should().Be("-1");
        args.IndexOf("-i").Should().Be(args.IndexOf("-sseof") + 2);
        args[args.IndexOf("-update") + 1].Should().Be("1");
        args.Should().NotContain("-frames:v");
    }

    [Fact]
    public void Jpeg_uses_mjpeg_full_range_and_a_high_quality_setting()
    {
        string[] argv = FfmpegArgvBuilder.BuildExtractFrameArgs("in.mp4", "out.jpg", FrameAt.First, null, "jpeg", null, null);
        var args = argv.ToList();

        args[args.IndexOf("-c:v") + 1].Should().Be("mjpeg");
        args[args.IndexOf("-q:v") + 1].Should().Be("2");
        Filter(argv).Should().Contain("format=yuvj420p");
    }

    [Fact]
    public void No_resize_or_crop_means_no_scale_or_crop_filter_for_png()
    {
        string[] argv = FfmpegArgvBuilder.BuildExtractFrameArgs("in.mp4", "out.png", FrameAt.First, null, "png", null, null);

        argv.Should().NotContain("-vf");
    }

    [Fact]
    public void MaxLongSide_downscales_the_longer_side_only_and_never_upscales()
    {
        string filter = Filter(FfmpegArgvBuilder.BuildExtractFrameArgs("in.mp4", "out.png", FrameAt.First, null, "png", 1280, null));

        filter.Should().Be("scale=w='if(gt(iw,ih),min(1280,iw),-2)':h='if(gt(iw,ih),-2,min(1280,ih))'");
    }

    [Fact]
    public void Crop_is_applied_before_the_downscale_and_is_expressed_in_fractions_of_the_frame()
    {
        string filter = Filter(FfmpegArgvBuilder.BuildExtractFrameArgs(
            "in.mp4", "out.png", FrameAt.First, null, "png", 640, new NormalizedCrop(0.25, 0.1, 0.5, 0.5)));

        filter.Should().StartWith("crop=w=trunc(iw*0.5/2)*2:h=trunc(ih*0.5/2)*2:x=trunc(iw*0.25/2)*2:y=trunc(ih*0.1/2)*2,scale=");
    }

    [Fact]
    public void Numbers_never_use_exponent_notation_and_a_negative_time_is_clamped()
    {
        string[] argv = FfmpegArgvBuilder.BuildExtractFrameArgs(
            "in.mp4", "out.png", FrameAt.Time, -5, "png", null, new NormalizedCrop(0.0000001, 0, 1, 1));
        argv[argv.ToList().IndexOf("-ss") + 1].Should().Be("0");
        Filter(argv).Should().NotContain("E-");
    }

    [Fact]
    public void An_unknown_format_is_refused_rather_than_passed_to_ffmpeg()
    {
        FluentActions.Invoking(() => FfmpegArgvBuilder.BuildExtractFrameArgs("in.mp4", "out.gif", FrameAt.First, null, "gif", null, null))
            .Should().Throw<System.ArgumentException>();
    }
}
