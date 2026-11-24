using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Issue #93 phase 5: the conform pass raises an open-model clip's frame rate and/or height to the
/// program's with ffmpeg alone (<c>minterpolate</c> motion-compensated interpolation and a lanczos
/// upscale — honestly NOT RIFE, no new dependency). It must be a strict NO-OP on conforming input:
/// a clip already at or above the target gets the exact pre-phase-5 filter chain, byte for byte.
/// </summary>
public class GeneratedClipConformTests
{
    private static MediaProbeResult Probe(int fpsNum, int fpsDen, int height) =>
        new(6.0, fpsNum, fpsDen, height * 16 / 9, height, "h264", null, null);

    [Fact]
    public void No_target_yields_the_pre_phase_5_chain()
    {
        GeneratedClipNormalizer.BuildVideoFilter(Probe(16, 1, 480), null)
            .Should().Be("fps=16/1,format=yuv420p");
        GeneratedClipNormalizer.BuildVideoFilter(Probe(16, 1, 480), GeneratedClipConformTarget.None)
            .Should().Be("fps=16/1,format=yuv420p");
    }

    [Fact]
    public void FromConfig_is_null_when_both_knobs_are_zero()
    {
        GeneratedClipConformTarget.FromConfig(0, 0).Should().BeNull();
        GeneratedClipConformTarget.FromConfig(30, 0).Should().Be(new GeneratedClipConformTarget(30, 0));
        GeneratedClipConformTarget.FromConfig(0, 1080).Should().Be(new GeneratedClipConformTarget(0, 1080));
    }

    [Fact]
    public void A_lower_fps_clip_is_interpolated_up_with_motion_compensation()
    {
        GeneratedClipNormalizer.BuildVideoFilter(Probe(16, 1, 480), new GeneratedClipConformTarget(30, 0))
            .Should().Be("minterpolate=fps=30:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1,format=yuv420p");
    }

    [Fact]
    public void A_clip_already_at_or_above_the_target_fps_is_left_at_its_own_rate()
    {
        GeneratedClipNormalizer.BuildVideoFilter(Probe(30, 1, 480), new GeneratedClipConformTarget(30, 0))
            .Should().Be("fps=30/1,format=yuv420p", "never downsample, never re-time a conforming clip");
        GeneratedClipNormalizer.BuildVideoFilter(Probe(60, 1, 480), new GeneratedClipConformTarget(30, 0))
            .Should().Be("fps=60/1,format=yuv420p");
    }

    [Fact]
    public void A_smaller_clip_is_upscaled_with_lanczos_keeping_aspect_and_even_width()
    {
        GeneratedClipNormalizer.BuildVideoFilter(Probe(30, 1, 480), new GeneratedClipConformTarget(0, 1080))
            .Should().Be("fps=30/1,scale=-2:1080:flags=lanczos,format=yuv420p");
    }

    [Fact]
    public void A_clip_already_at_or_above_the_target_height_is_not_scaled()
    {
        GeneratedClipNormalizer.BuildVideoFilter(Probe(30, 1, 1080), new GeneratedClipConformTarget(0, 1080))
            .Should().Be("fps=30/1,format=yuv420p");
        GeneratedClipNormalizer.BuildVideoFilter(Probe(30, 1, 2160), new GeneratedClipConformTarget(0, 1080))
            .Should().Be("fps=30/1,format=yuv420p");
    }

    [Fact]
    public void Both_axes_conform_together_in_a_fixed_order()
    {
        GeneratedClipNormalizer.BuildVideoFilter(Probe(24, 1, 704), new GeneratedClipConformTarget(60, 1080))
            .Should().Be("minterpolate=fps=60:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1,scale=-2:1080:flags=lanczos,format=yuv420p");
    }
}
