using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.Enhance;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Enhance;

public class EnhanceFilterBuilderTests
{
    private static SourceFacts Facts(
        int width = 320,
        int height = 180,
        int fpsNum = 24,
        int fpsDen = 1,
        string? colorTransfer = null,
        int? bitDepth = 8,
        bool hasAudio = true) =>
        new(width, height, fpsNum, fpsDen, (int)(2 * fpsNum / (double)fpsDen), "yuv420p",
            colorTransfer, null, bitDepth, 2.0, hasAudio);

    private static EnhanceFilterPlan Ok(LocalEnhanceRequest request, SourceFacts source)
    {
        EnhanceBuildResult built = EnhanceFilterBuilder.Build(request, source);
        built.Succeeded.Should().BeTrue(built.Refusal?.Message);
        return built.Plan!;
    }

    private static EnhanceRefusal Refused(LocalEnhanceRequest request, SourceFacts source)
    {
        EnhanceBuildResult built = EnhanceFilterBuilder.Build(request, source);
        built.Succeeded.Should().BeFalse();
        return built.Refusal!;
    }

    [Fact]
    public void Denoise_light_is_byte_pinned()
    {
        EnhanceFilterPlan plan = Ok(new LocalEnhanceRequest
        {
            Op = VideoEnhanceOp.Denoise,
            DenoiseStrength = DenoiseStrength.Light
        }, Facts());
        plan.VideoFilter.Should().Be("hqdn3d=3:2:6:4.5,format=yuv420p");
        plan.Method.Should().Be("hqdn3d");
        plan.OutWidth.Should().Be(320);
        plan.OutHeight.Should().Be(180);
        plan.OutFps.Should().Be(24);
    }

    [Fact]
    public void Denoise_light_keep_grain_appends_the_noise_table()
    {
        Ok(new LocalEnhanceRequest
        {
            Op = VideoEnhanceOp.Denoise,
            DenoiseStrength = DenoiseStrength.Light,
            KeepGrain = true
        }, Facts()).VideoFilter.Should().Be("hqdn3d=3:2:6:4.5,noise=alls=5:allf=t,format=yuv420p");
    }

    [Fact]
    public void Denoise_strong_uses_nlmeans_up_to_1080p_and_hqdn3d_above()
    {
        Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.Denoise, DenoiseStrength = DenoiseStrength.Strong }, Facts())
            .VideoFilter.Should().Be("nlmeans=s=4:p=7:r=15,format=yuv420p");
        Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.Denoise, DenoiseStrength = DenoiseStrength.Strong },
            Facts(3840, 2160, bitDepth: 8)).VideoFilter.Should().Be("hqdn3d=4:3:8:6,format=yuv420p");
    }

    [Fact]
    public void Motion_blur_windows_follow_the_shutter_angle_byte_pinned()
    {
        string expected(int window) =>
            $"minterpolate=fps=192/1:mi_mode=mci:mc_mode=aobmc:vsbmc=1,tmix=frames={window},fps=24/1,format=yuv420p";

        Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.MotionBlur, ShutterAngle = 90 }, Facts())
            .VideoFilter.Should().Be(expected(2));
        Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.MotionBlur }, Facts())
            .VideoFilter.Should().Be(expected(4));
        Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.MotionBlur, ShutterAngle = 360 }, Facts())
            .VideoFilter.Should().Be(expected(8));
        Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.MotionBlur }, Facts()).OutFps.Should().Be(24);
    }

    [Fact]
    public void Motion_blur_internal_rate_is_capped_at_480()
    {
        // 120 fps x 8 would be 960; the multiplier drops to 4 (480 fps) and the window scales with it.
        Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.MotionBlur, ShutterAngle = 180 },
            Facts(640, 360, 120, 1)).VideoFilter
            .Should().Be("minterpolate=fps=480/1:mi_mode=mci:mc_mode=aobmc:vsbmc=1,tmix=frames=2,fps=120/1,format=yuv420p");
    }

    [Fact]
    public void Upscale_by_factor_and_by_target_height_byte_pinned()
    {
        EnhanceFilterPlan byFactor = Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.Upscale, UpscaleFactor = 2 }, Facts());
        byFactor.VideoFilter.Should().Be("scale=-2:360:flags=lanczos,cas=strength=0.3,format=yuv420p");
        byFactor.OutWidth.Should().Be(640);
        byFactor.OutHeight.Should().Be(360);

        EnhanceFilterPlan byHeight = Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.Upscale, TargetHeight = 720 }, Facts());
        byHeight.VideoFilter.Should().Be("scale=-2:720:flags=lanczos,cas=strength=0.3,format=yuv420p");
        byHeight.OutWidth.Should().Be(1280);
        byHeight.OutHeight.Should().Be(720);
        byHeight.OutFps.Should().Be(24);
    }

    [Fact]
    public void Upscale_width_matches_what_ffmpeg_scale_minus_2_produces()
    {
        // Measured against ffmpeg: scale=-2:1080 gives 1067x600 -> 1920 (1920.6 truncates to 1920),
        // 1001x600 -> 1802 (1801.8 truncates to 1801, then rounds up to even) and 641x360 -> 1924.
        Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.Upscale, TargetHeight = 1080 }, Facts(1067, 600))
            .OutWidth.Should().Be(1920);
        Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.Upscale, TargetHeight = 1080 }, Facts(1001, 600))
            .OutWidth.Should().Be(1802);
        Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.Upscale, TargetHeight = 1080 }, Facts(641, 360))
            .OutWidth.Should().Be(1924);
    }

    [Fact]
    public void Upscale_odd_aspect_gets_an_even_width()
    {
        EnhanceFilterPlan plan = Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.Upscale, UpscaleFactor = 2 },
            Facts(321, 181));
        plan.OutWidth.Should().Be(642);
        plan.OutHeight.Should().Be(362);
    }

    [Fact]
    public void Interpolate_multiplies_the_exact_rational_rate()
    {
        Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.Interpolate, FrameMultiplier = 2 }, Facts())
            .VideoFilter.Should().Be("minterpolate=fps=48/1:mi_mode=mci:mc_mode=aobmc:vsbmc=1,format=yuv420p");
        Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.Interpolate, FrameMultiplier = 4 }, Facts(1920, 1080, 30000, 1001))
            .VideoFilter.Should().Be("minterpolate=fps=120000/1001:mi_mode=mci:mc_mode=aobmc:vsbmc=1,format=yuv420p");
        EnhanceFilterPlan eight = Ok(new LocalEnhanceRequest { Op = VideoEnhanceOp.Interpolate, FrameMultiplier = 8 }, Facts());
        eight.OutFps.Should().Be(192);
        eight.OutWidth.Should().Be(320);
        eight.OutHeight.Should().Be(180);
    }

    [Fact]
    public void Interpolate_above_240_fps_is_refused()
    {
        Refused(new LocalEnhanceRequest { Op = VideoEnhanceOp.Interpolate, FrameMultiplier = 8 }, Facts(640, 360, 60, 1))
            .Code.Should().Be(EnhanceFilterBuilder.FpsTooHigh);
    }

    [Fact]
    public void Missing_or_conflicting_parameters_are_typed_refusals()
    {
        Refused(new LocalEnhanceRequest { Op = VideoEnhanceOp.Denoise }, Facts())
            .Should().BeEquivalentTo(new { Code = EnhanceFilterBuilder.InvalidRequest, Field = "denoiseStrength" });
        Refused(new LocalEnhanceRequest { Op = VideoEnhanceOp.Interpolate }, Facts())
            .Code.Should().Be(EnhanceFilterBuilder.InvalidRequest);
        Refused(new LocalEnhanceRequest { Op = VideoEnhanceOp.Upscale, UpscaleFactor = 2, TargetHeight = 720 }, Facts())
            .Code.Should().Be(EnhanceFilterBuilder.InvalidRequest);
        Refused(new LocalEnhanceRequest { Op = VideoEnhanceOp.Upscale }, Facts())
            .Code.Should().Be(EnhanceFilterBuilder.InvalidRequest);
        Refused(new LocalEnhanceRequest { Op = VideoEnhanceOp.MotionBlur, ShutterAngle = 45 }, Facts())
            .Code.Should().Be(EnhanceFilterBuilder.InvalidRequest);
        Refused(new LocalEnhanceRequest { Op = VideoEnhanceOp.MotionBlur, ShutterAngle = 361 }, Facts())
            .Code.Should().Be(EnhanceFilterBuilder.InvalidRequest);
        Refused(new LocalEnhanceRequest { Op = VideoEnhanceOp.Upscale, UpscaleFactor = 3 }, Facts())
            .Code.Should().Be(EnhanceFilterBuilder.InvalidRequest);
    }

    [Fact]
    public void Upscale_never_downscales_and_respects_the_size_caps()
    {
        Refused(new LocalEnhanceRequest { Op = VideoEnhanceOp.Upscale, TargetHeight = 720 }, Facts(1920, 1080))
            .Code.Should().Be(EnhanceFilterBuilder.NotLarger);
        Refused(new LocalEnhanceRequest { Op = VideoEnhanceOp.Upscale, UpscaleFactor = 4 }, Facts(1280, 720))
            .Code.Should().Be(EnhanceFilterBuilder.OutputTooLarge);
        Refused(new LocalEnhanceRequest { Op = VideoEnhanceOp.Upscale, TargetHeight = 2160 }, Facts(3840, 1920))
            .Code.Should().Be(EnhanceFilterBuilder.OutputTooLarge);
    }

    [Fact]
    public void Hdr_source_prepends_the_shared_tonemap_and_warns()
    {
        EnhanceFilterPlan plan = Ok(new LocalEnhanceRequest
        {
            Op = VideoEnhanceOp.Denoise,
            DenoiseStrength = DenoiseStrength.Light
        }, Facts(colorTransfer: "smpte2084", bitDepth: 10));

        plan.VideoFilter.Should().StartWith("zscale=t=linear:npl=203,");
        plan.VideoFilter.Should().EndWith("hqdn3d=3:2:6:4.5,format=yuv420p");
        plan.Warnings.Should().ContainSingle().Which.Should().Be("hdr_converted_to_sdr");
    }

    [Fact]
    public void Ten_bit_sdr_source_warns_and_still_ends_at_8_bit_yuv420p()
    {
        EnhanceFilterPlan plan = Ok(new LocalEnhanceRequest
        {
            Op = VideoEnhanceOp.Interpolate,
            FrameMultiplier = 2
        }, Facts(bitDepth: 10));

        plan.VideoFilter.Should().NotStartWith("zscale=");
        plan.VideoFilter.Should().EndWith(",format=yuv420p");
        plan.Warnings.Should().ContainSingle().Which.Should().Be("hdr_converted_to_sdr");
    }
}
