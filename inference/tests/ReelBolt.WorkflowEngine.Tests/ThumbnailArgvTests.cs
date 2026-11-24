using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The argv <c>ThumbnailGenerateStepExecutor</c> hands ffmpeg. Pinned separately from the executor's
/// own tests because the shape of this command is what the step's output actually is: a wrong
/// <c>force_original_aspect_ratio</c> silently letterboxes every cover, and a differently spelled
/// drawtext option silently drops the text.
/// </summary>
public class ThumbnailArgvTests
{
    [Fact]
    public void Cover_fills_the_box_and_crops_the_overflow()
    {
        string[] argv = FfmpegArgvBuilder.BuildThumbnailArgs("in.mp4", "out.jpg", 3.5, 1280, 720, cover: true, quality: 4);

        string filter = Filter(argv);
        filter.Should().Contain("scale=1280:720:force_original_aspect_ratio=increase");
        filter.Should().Contain("crop=1280:720");
        filter.Should().NotContain("pad=");
    }

    [Fact]
    public void Contain_fits_the_whole_frame_and_pads_the_excess_axis()
    {
        string[] argv = FfmpegArgvBuilder.BuildThumbnailArgs("in.mp4", "out.jpg", 0, 1080, 1080, cover: false, quality: 6);

        string filter = Filter(argv);
        filter.Should().Contain("force_original_aspect_ratio=decrease");
        filter.Should().Contain("pad=1080:1080:(ow-iw)/2:(oh-ih)/2:color=black");
        filter.Should().NotContain("crop=");
    }

    [Fact]
    public void The_moment_is_seeked_before_the_input_so_the_decode_stops_at_it()
    {
        string[] argv = FfmpegArgvBuilder.BuildThumbnailArgs("in.mp4", "out.jpg", 12.25, 640, 360, cover: true, quality: 4);
        var args = argv.ToList();

        int seek = args.IndexOf("-ss");
        seek.Should().BeGreaterThan(-1);
        args[seek + 1].Should().Be("12.25");
        args.IndexOf("-i").Should().Be(seek + 2, "-ss must precede -i, or ffmpeg decodes the whole file up to the seek");
        args[args.IndexOf("-frames:v") + 1].Should().Be("1");
    }

    [Fact]
    public void A_negative_seek_is_clamped_rather_than_passed_through()
    {
        string[] argv = FfmpegArgvBuilder.BuildThumbnailArgs("in.mp4", "out.jpg", -5, 640, 360, cover: true, quality: 4);
        argv[argv.ToList().IndexOf("-ss") + 1].Should().Be("0");
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(40, 31)]
    [InlineData(4, 4)]
    public void Quality_is_clamped_to_the_mjpeg_encoders_own_range(int asked, int expected)
    {
        string[] argv = FfmpegArgvBuilder.BuildThumbnailArgs("in.mp4", "out.jpg", 0, 640, 360, cover: true, quality: asked);
        argv[argv.ToList().IndexOf("-q:v") + 1].Should().Be(expected.ToString());
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(99999, 4096)]
    [InlineData(1280, 1280)]
    public void Dimensions_are_clamped_so_a_config_can_never_fan_out_the_argv(int asked, int expected)
    {
        string[] argv = FfmpegArgvBuilder.BuildThumbnailArgs("in.mp4", "out.jpg", 0, asked, asked, cover: true, quality: 4);
        Filter(argv).Should().Contain($"{expected}:{expected}");
    }

    [Fact]
    public void The_overlay_is_a_textfile_reference_with_expansion_disabled_never_the_text_itself()
    {
        var overlay = new ThumbnailOverlay(
            TextFilePath: "/session/thumbnail-overlay.txt",
            FontFilePath: "/usr/share/fonts/dejavu/DejaVuSans.ttf",
            FontSizePx: 65,
            FontColor: "white",
            BoxColor: "black@0.45",
            BandCenterFraction: 0.78);

        string filter = Filter(FfmpegArgvBuilder.BuildThumbnailArgs(
            "in.mp4", "out.jpg", 0, 1280, 720, cover: true, quality: 4, overlay));

        filter.Should().Contain("drawtext=fontfile='/usr/share/fonts/dejavu/DejaVuSans.ttf'");
        filter.Should().Contain("textfile='/session/thumbnail-overlay.txt'");
        filter.Should().Contain("expansion=none");
        filter.Should().Contain("fontsize=65");
        filter.Should().Contain("box=1");
        filter.Should().Contain("boxcolor=black@0.45");
        filter.Should().Contain("x=(w-text_w)/2");
        filter.Should().Contain("y=h*0.78-text_h/2");
    }

    [Fact]
    public void A_centred_overlay_is_vertically_centred_on_the_text_box_rather_than_the_canvas()
    {
        var overlay = new ThumbnailOverlay("/s/o.txt", "/f.ttf", 40, "white", "none", BandCenterFraction: 0);

        string filter = Filter(FfmpegArgvBuilder.BuildThumbnailArgs(
            "in.mp4", "out.jpg", 0, 640, 360, cover: true, quality: 4, overlay));

        filter.Should().Contain("y=(h-text_h)/2");
    }

    [Fact]
    public void A_none_box_colour_draws_no_box_instead_of_handing_ffmpeg_the_unparseable_colour_none()
    {
        // The reported FRAME_EXTRACTION_FAILED: 1920x1080, quality 2, overlayBoxColor "none".
        // ffmpeg's drawtext rejects boxcolor=none ("Cannot find color 'none'"), so "none" must mean box=0.
        var overlay = new ThumbnailOverlay("/s/o.txt", "/f.ttf", 40, "white", "none", BandCenterFraction: 0.78);

        string filter = Filter(FfmpegArgvBuilder.BuildThumbnailArgs(
            "in.mp4", "out.jpg", 0, 1920, 1080, cover: true, quality: 2, overlay));

        filter.Should().NotContain("boxcolor=none");
        filter.Should().NotContain("boxcolor=");
        filter.Should().Contain("box=0");
        filter.Should().NotContain("box=1");
    }

    [Fact]
    public void The_frame_path_reaches_ffmpeg_only_as_the_last_argument()
    {
        string[] argv = FfmpegArgvBuilder.BuildThumbnailArgs("in.mp4", "out.jpg", 0, 640, 360, cover: true, quality: 4);
        argv[^1].Should().Be("out.jpg");
        argv.ToList().IndexOf("out.jpg").Should().Be(argv.Length - 1);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_non_finite_moment_never_reaches_the_argv(double atSec)
    {
        Action act = () => FfmpegArgvBuilder.BuildThumbnailArgs("in.mp4", "out.jpg", atSec, 1280, 720, cover: true, quality: 4);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static string Filter(string[] argv) => argv[argv.ToList().IndexOf("-vf") + 1];
}
