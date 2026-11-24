using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Pins the grid decode filter strings. The default decode is what the Phase 1 visual grid has always
/// used and must stay byte-identical; the full-chroma decode is the insert tracker's alone (T-008, F1):
/// the source's 4:2:0 chroma is upsampled bilinearly to 4:4:4 BEFORE the box-average downscale, so the
/// grid's chroma sits at the same positions as its luma instead of being nearest-neighbour smeared.
/// </summary>
public class FfmpegArgvBuilderGridTests
{
    private static string VfOf(string[] args)
    {
        int i = Array.IndexOf(args, "-vf");
        i.Should().BeGreaterThanOrEqualTo(0);
        return args[i + 1];
    }

    [Fact]
    public void Grid_args_default_are_byte_identical()
    {
        string[] implicitDefault = FfmpegArgvBuilder.BuildGridSampleArgs("in.mp4", "out.raw", 2.0, 320, 180, 3);
        string[] explicitDefault = FfmpegArgvBuilder.BuildGridSampleArgs("in.mp4", "out.raw", 2.0, 320, 180, 3, GridChroma.Default);

        VfOf(implicitDefault).Should().Be("fps=2,scale=320:180:flags=area,format=rgb24");
        explicitDefault.Should().Equal(implicitDefault);

        // The whole argv, not only -vf: nothing else may move for the default decode.
        string[] tail = implicitDefault[^9..^1];
        tail.Should().Equal("-an", "-sn", "-vf", "fps=2,scale=320:180:flags=area,format=rgb24", "-f", "rawvideo", "-pix_fmt", "rgb24");
        implicitDefault[^1].Should().Be("out.raw");
    }

    [Theory]
    [InlineData(GridChroma.Default)]
    [InlineData(GridChroma.FullChroma)]
    public void Grid_args_with_a_frame_cap_add_only_the_cap_before_the_output_format(GridChroma chroma)
    {
        string[] plain = FfmpegArgvBuilder.BuildGridSampleArgs("in.mp4", "out.raw", 2.0, 320, 180, 3, chroma);
        string[] capped = FfmpegArgvBuilder.BuildGridSampleArgs("in.mp4", "out.raw", 2.0, 320, 180, 3, chroma, 4088);

        int at = Array.IndexOf(capped, "-frames:v");
        at.Should().Be(Array.IndexOf(capped, "-vf") + 2, "the cap follows the filter");
        capped[at + 1].Should().Be("4088");
        capped.Where((_, i) => i != at && i != at + 1).Should().Equal(plain, "nothing else moves");
        // Security ENG-3: the colour grid reads the customer's file like every other pass, so it carries the
        // user-media demuxer whitelist with or without the cap (the cap itself still adds nothing else).
        capped.Should().ContainInOrder("-format_whitelist", UserMediaInputFormats.Whitelist);
        FfmpegArgvBuilder.BuildGridSampleArgs("in.mp4", "out.raw", 2.0, 320, 180, 3, chroma, 0).Should().Equal(plain, "0 is no cap");
    }

    [Fact]
    public void Grid_args_full_chroma()
    {
        string[] args = FfmpegArgvBuilder.BuildGridSampleArgs("in.mp4", "out.raw", 29.97, 256, 144, 3, GridChroma.FullChroma);

        VfOf(args).Should().Be(
            "fps=29.97,scale=iw:ih:flags=bilinear+full_chroma_inp,format=yuv444p," +
            "scale=256:144:flags=area+full_chroma_int,format=rgb24");

        // Everything outside -vf is the same as the default decode.
        string[] defaults = FfmpegArgvBuilder.BuildGridSampleArgs("in.mp4", "out.raw", 29.97, 256, 144, 3);
        int vf = Array.IndexOf(args, "-vf");
        args.Length.Should().Be(defaults.Length);
        for (int k = 0; k < args.Length; k++)
        {
            if (k != vf + 1)
                args[k].Should().Be(defaults[k]);
        }
    }

    [Fact]
    public void Grid_args_full_chroma_formats_numbers_with_invariant_culture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string[] args = FfmpegArgvBuilder.BuildGridSampleArgs("in.mp4", "out.raw", 23.976, 320, 180, 3, GridChroma.FullChroma);
            VfOf(args).Should().StartWith("fps=23.976,");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Gray_args_are_unchanged()
    {
        string[] args = FfmpegArgvBuilder.BuildGraySampleArgs("in.mp4", "out.raw", 2.0, 320, 180, 3, 100);
        VfOf(args).Should().Be("fps=2,scale=320:180:flags=bicubic,format=gray");
    }

    [Fact]
    public void Gray_args_whitelist_the_input_and_cap_the_output_by_frames_and_bytes()
    {
        string[] args = FfmpegArgvBuilder.BuildGraySampleArgs("in.mp4", "out.raw", 2.0, 320, 180, 3, 100);

        args.Should().Equal(
            "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-threads", "3",
            "-i", "in.mp4",
            "-an", "-sn",
            "-vf", "fps=2,scale=320:180:flags=bicubic,format=gray",
            "-frames:v", "100",
            "-fs", "5760000",
            "-f", "rawvideo", "-pix_fmt", "gray",
            "out.raw");
        // the whitelist is an input option: it must come before the input it guards
        Array.IndexOf(args, "-format_whitelist").Should().BeLessThan(Array.IndexOf(args, "-i"));
    }

    [Fact]
    public void Gray_args_refuse_a_cap_of_no_frames()
    {
        Action act = () => FfmpegArgvBuilder.BuildGraySampleArgs("in.mp4", "out.raw", 2.0, 320, 180, 3, 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(100, 110)]
    [InlineData(3000, 3068)]
    [InlineData(0, 4088)]
    [InlineData(-5, 4088)]
    public void The_gray_frame_cap_is_what_the_caller_asked_for_plus_a_small_margin(int asked, long cap)
    {
        FfmpegFrameGridSampler.PlanMaxGrayFrames(asked).Should().Be(cap);
        cap.Should().BeGreaterThan(Math.Max(asked, 1));
    }

    [Fact]
    public async Task The_gray_pass_caps_its_output_at_the_planned_frames_and_never_asks_for_a_local_reservation_on_a_remote_workspace()
    {
        var (_, workspace, argvs) = SamplerRig();
        Mock<IScratchDiskGuard> guard = new(MockBehavior.Strict);
        FfmpegFrameGridSampler sampler = new(
            new Mock<IVideoToolRunner>().Object, Options.Create(new VideoEditingOptions()),
            NullLogger<FfmpegFrameGridSampler>.Instance, guard.Object);

        // 600 s at 12 fps would be 7,200 frames: the caller's cap of 100 lowers the rate and bounds the output.
        FrameGridResult grid = await sampler.SampleGrayAsync(workspace.Object, "source.mp4", 12.0, 4, 3, 600.0, 100, CancellationToken.None);

        argvs.Should().ContainSingle();
        string[] argv = argvs[0];
        argv[Array.IndexOf(argv, "-frames:v") + 1].Should().Be("110");
        argv[Array.IndexOf(argv, "-fs") + 1].Should().Be((110 * 4 * 3).ToString(CultureInfo.InvariantCulture));
        argv[Array.IndexOf(argv, "-format_whitelist") + 1].Should().Be(MediaDerivativeInputFormats.Whitelist);
        grid.FrameCount.Should().Be(6);
    }

    // ---- the sampler routes the chroma choice into the argv ------------------------------------

    private static (FfmpegFrameGridSampler Sampler, Mock<IMediaWorkspace> Workspace, List<string[]> Argvs) SamplerRig()
    {
        List<string[]> argvs = [];
        Mock<IMediaWorkspace> workspace = new();
        workspace.Setup(w => w.MaterializeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string v, CancellationToken _) => "/scratch/" + v);
        workspace.Setup(w => w.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<string> argv, TimeSpan _, CancellationToken _) => argvs.Add([.. argv]))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        workspace.Setup(w => w.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[4 * 3 * 3 * 2]);
        FfmpegFrameGridSampler sampler = new(
            new Mock<IVideoToolRunner>().Object,
            Options.Create(new VideoEditingOptions()),
            NullLogger<FfmpegFrameGridSampler>.Instance);
        return (sampler, workspace, argvs);
    }

    [Fact]
    public async Task Sampler_without_a_chroma_argument_decodes_with_the_default_filter()
    {
        var (sampler, workspace, argvs) = SamplerRig();

        await sampler.SampleAsync(workspace.Object, "source.mp4", 2.0, 4, 3, 10.0, 100, CancellationToken.None);

        argvs.Should().ContainSingle();
        VfOf(argvs[0]).Should().Be("fps=2,scale=4:3:flags=area,format=rgb24");
    }

    [Fact]
    public async Task Sampler_with_full_chroma_decodes_with_the_full_chroma_filter()
    {
        var (sampler, workspace, argvs) = SamplerRig();

        FrameGridResult grid = await sampler.SampleAsync(
            workspace.Object, "source.mp4", 2.0, 4, 3, 10.0, 100, GridChroma.FullChroma, CancellationToken.None);

        argvs.Should().ContainSingle();
        VfOf(argvs[0]).Should().Be(
            "fps=2,scale=iw:ih:flags=bilinear+full_chroma_inp,format=yuv444p,scale=4:3:flags=area+full_chroma_int,format=rgb24");
        grid.FrameCount.Should().Be(2);
    }
}
