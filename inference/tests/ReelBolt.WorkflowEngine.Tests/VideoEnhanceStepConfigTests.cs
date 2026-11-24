using System;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The config's own rules (save and run use the same <c>Validate()</c>), the camelCase wire shape, and the credit-side readings of it.</summary>
public sealed class VideoEnhanceStepConfigTests
{
    private const string Src = """{"kind":"ProjectFile","projectFileId":"11111111-1111-1111-1111-111111111111"}""";

    private static VideoEnhanceStepConfig Parse(string json) =>
        JsonSerializer.Deserialize<VideoEnhanceStepConfig>(json, VideoEnhanceStepConfig.JsonOptions)!;

    private static string Json(string op, string engine, string extra = "") =>
        $$"""{"op":"{{op}}","engine":"{{engine}}","source":{{Src}}{{extra}}}""";

    [Theory]
    [InlineData("Upscale", "Local", ",\"upscaleFactor\":2")]
    [InlineData("Upscale", "Local", ",\"targetHeight\":1080")]
    [InlineData("Upscale", "Hosted", ",\"upscaleFactor\":4,\"allowSourceMediaEgress\":true")]
    [InlineData("Interpolate", "Local", ",\"frameMultiplier\":8")]
    [InlineData("Interpolate", "Hosted", ",\"frameMultiplier\":4")]
    [InlineData("Denoise", "Local", ",\"denoiseStrength\":\"Strong\",\"keepGrain\":true")]
    [InlineData("MotionBlur", "Local", ",\"shutterAngle\":270")]
    [InlineData("MotionBlur", "Local", "")]
    [InlineData("Extend", "Hosted", ",\"targetAspect\":\"16:9\",\"allowSourceMediaEgress\":true")]
    [InlineData("Extend", "Hosted", ",\"targetAspect\":\"4:5\",\"prompt\":\"more sky\"")]
    [InlineData("Extend", "Hosted", ",\"marginLeft\":0.5,\"marginRight\":0.5")]
    [InlineData("Extend", "Hosted", ",\"marginLeft\":0.2,\"marginTop\":0.2,\"marginRight\":0")]
    public void A_well_formed_config_validates(string op, string engine, string extra) =>
        Parse(Json(op, engine, extra)).Validate().Should().BeEmpty();

    [Theory]
    [InlineData("Upscale", "Local", "", "exactly one of upscaleFactor")]
    [InlineData("Upscale", "Local", ",\"upscaleFactor\":2,\"targetHeight\":1080", "exactly one of upscaleFactor")]
    [InlineData("Upscale", "Local", ",\"upscaleFactor\":3", "UpscaleFactor must be 2 or 4")]
    [InlineData("Upscale", "Local", ",\"targetHeight\":900", "TargetHeight must be 720, 1080, 1440 or 2160")]
    [InlineData("Upscale", "Local", ",\"upscaleFactor\":2,\"frameMultiplier\":2", "FrameMultiplier is not valid with op=upscale")]
    [InlineData("Interpolate", "Local", "", "frameMultiplier 2, 4 or 8")]
    [InlineData("Interpolate", "Hosted", ",\"frameMultiplier\":8", "8x is local only")]
    [InlineData("Denoise", "Hosted", ",\"denoiseStrength\":\"Light\"", "local operation")]
    [InlineData("MotionBlur", "Hosted", "", "local operation")]
    [InlineData("Denoise", "Local", "", "denoiseStrength")]
    [InlineData("MotionBlur", "Local", ",\"shutterAngle\":45", "ShutterAngle must be between 90 and 360")]
    [InlineData("Upscale", "Local", ",\"upscaleFactor\":2,\"allowSourceMediaEgress\":true", "AllowSourceMediaEgress is only valid with engine=Hosted")]
    [InlineData("Upscale", "Local", ",\"upscaleFactor\":2,\"providerId\":\"22222222-2222-2222-2222-222222222222\"", "ProviderId is only valid with engine=Hosted")]
    [InlineData("Upscale", "Hosted", ",\"upscaleFactor\":2,\"maxSourceSeconds\":121", "hosted operation must be between 1 and 120")]
    [InlineData("Upscale", "Local", ",\"upscaleFactor\":2,\"maxSourceSeconds\":601", "MaxSourceSeconds must be between 1 and 600")]
    [InlineData("Extend", "Local", ",\"targetAspect\":\"16:9\"", "Extend is a hosted operation")]
    [InlineData("Extend", "Hosted", "", "exactly one of targetAspect")]
    [InlineData("Extend", "Hosted", ",\"targetAspect\":\"16:9\",\"marginLeft\":0.1", "exactly one of targetAspect")]
    [InlineData("Extend", "Hosted", ",\"targetAspect\":\"21:9\"", "TargetAspect must be 16:9, 9:16, 1:1 or 4:5")]
    [InlineData("Extend", "Hosted", ",\"marginLeft\":0.51", "MarginLeft must be between 0 and 0.5")]
    [InlineData("Extend", "Hosted", ",\"marginTop\":-0.1", "MarginTop must be between 0 and 0.5")]
    [InlineData("Extend", "Hosted", ",\"marginLeft\":0", "must add something")]
    [InlineData("Extend", "Hosted", ",\"marginLeft\":0.1,\"marginRight\":0.3", "one amount on the sides you pick")]
    [InlineData("Extend", "Hosted", ",\"marginLeft\":0.2,\"marginTop\":0.1", "one amount on the sides you pick")]
    [InlineData("Extend", "Hosted", ",\"targetAspect\":\"16:9\",\"upscaleFactor\":2", "UpscaleFactor is not valid with op=extend")]
    [InlineData("Upscale", "Local", ",\"upscaleFactor\":2,\"prompt\":\"x\"", "Prompt is not valid with op=upscale")]
    [InlineData("Interpolate", "Hosted", ",\"frameMultiplier\":2,\"targetAspect\":\"1:1\"", "TargetAspect is not valid with op=interpolate")]
    public void A_bad_field_is_refused_naming_it(string op, string engine, string extra, string expected) =>
        Parse(Json(op, engine, extra)).Validate().Should().Contain(e => e.Contains(expected, StringComparison.Ordinal), string.Join("; ", Parse(Json(op, engine, extra)).Validate()));

    [Theory]
    [InlineData(",\"sourceWidth\":1920,\"sourceHeight\":1080,\"sourceFps\":30", true)]
    [InlineData(",\"sourceWidth\":1920", false)]
    [InlineData(",\"sourceWidth\":1920,\"sourceHeight\":1080", false)]
    [InlineData(",\"sourceWidth\":0,\"sourceHeight\":1080,\"sourceFps\":30", false)]
    [InlineData(",\"sourceWidth\":99999,\"sourceHeight\":1080,\"sourceFps\":30", false)]
    [InlineData(",\"sourceWidth\":1920,\"sourceHeight\":1080,\"sourceFps\":0", false)]
    [InlineData(",\"sourceWidth\":1920,\"sourceHeight\":1080,\"sourceFps\":999", false)]
    public void The_source_hints_are_all_three_or_none_and_bounded(string hints, bool valid) =>
        (Parse(Json("Upscale", "Hosted", ",\"upscaleFactor\":2,\"allowSourceMediaEgress\":true" + hints)).Validate().Count == 0).Should().Be(valid);

    [Fact]
    public void Source_hints_are_refused_on_a_local_operation()
    {
        Parse(Json("Denoise", "Local", ",\"denoiseStrength\":\"Light\",\"sourceWidth\":1920,\"sourceHeight\":1080,\"sourceFps\":30")).Validate()
            .Should().Contain(e => e.Contains("only valid with engine=Hosted"));
    }

    [Fact]
    public void A_source_that_is_not_one_existing_video_is_refused()
    {
        Parse("""{"op":"Denoise","engine":"Local","denoiseStrength":"Light","source":{"kind":"GeneratedClips"}}""").Validate()
            .Should().Contain(e => e.Contains("GeneratedClips"));
        Parse("""{"op":"Denoise","engine":"Local","denoiseStrength":"Light","source":{"kind":"BriefFiles"}}""").Validate()
            .Should().Contain(e => e.Contains("BriefFiles"));
        Parse("""{"op":"Denoise","engine":"Local","denoiseStrength":"Light","source":{"kind":"ProjectFile","projectFileId":"11111111-1111-1111-1111-111111111111","inSec":1,"outSec":3}}""")
            .Validate().Should().Contain(e => e.Contains("InSec/OutSec"));
    }

    [Fact]
    public void Only_a_hosted_operation_calls_a_provider_and_names_a_capability()
    {
        Parse(Json("Upscale", "Local", ",\"upscaleFactor\":2")).CallsAnyProvider.Should().BeFalse();
        VideoEnhanceStepConfig up = Parse(Json("Upscale", "Hosted", ",\"upscaleFactor\":2"));
        up.CallsAnyProvider.Should().BeTrue();
        up.HostedCapability.Should().Be(InferenceProviderCapability.VideoUpscale);
        Parse(Json("Interpolate", "Hosted", ",\"frameMultiplier\":2")).HostedCapability.Should().Be(InferenceProviderCapability.FrameInterpolation);
        Parse(Json("Denoise", "Local", ",\"denoiseStrength\":\"Light\"")).HostedCapability.Should().BeNull();
    }

    [Fact]
    public void The_step_can_only_lower_the_deployments_cap()
    {
        VideoEnhanceStepConfig none = Parse(Json("Upscale", "Hosted", ",\"upscaleFactor\":2"));
        none.EffectiveMaxSourceSeconds(120).Should().Be(120);
        none.EffectiveMaxSourceSeconds(60).Should().Be(60);
        Parse(Json("Upscale", "Hosted", ",\"upscaleFactor\":2,\"maxSourceSeconds\":30")).EffectiveMaxSourceSeconds(120).Should().Be(30);
        Parse(Json("Upscale", "Hosted", ",\"upscaleFactor\":2,\"maxSourceSeconds\":100")).EffectiveMaxSourceSeconds(60).Should().Be(60);
    }

    [Fact]
    public void The_wire_shape_is_camelCase_with_the_consent_flag_under_its_guard_name()
    {
        VideoEnhanceStepConfig config = Parse(Json("Upscale", "Hosted", ",\"upscaleFactor\":2,\"allowSourceMediaEgress\":true"));
        string json = JsonSerializer.Serialize(config, VideoEnhanceStepConfig.JsonOptions);
        json.Should().Contain("\"allowSourceMediaEgress\":true").And.Contain("\"op\":\"Upscale\"").And.Contain("\"engine\":\"Hosted\"")
            .And.Contain("\"registerProjectFile\":true");
        config.RegisterProjectFile.Should().BeTrue("the default");
    }

    [Fact]
    public void The_credit_side_reads_the_engine_deny_by_default()
    {
        StepCreditDefaults.IsLocalVideoEnhance(Json("Denoise", "Local", ",\"denoiseStrength\":\"Light\"")).Should().BeTrue();
        StepCreditDefaults.IsLocalVideoEnhance(Json("Upscale", "Hosted", ",\"upscaleFactor\":2")).Should().BeFalse();
        StepCreditDefaults.IsLocalVideoEnhance(null).Should().BeFalse();
        StepCreditDefaults.IsLocalVideoEnhance("").Should().BeFalse();
        StepCreditDefaults.IsLocalVideoEnhance("{not json").Should().BeFalse();
        StepCreditDefaults.IsComputeOnly(new WorkflowStep { StepType = StepType.VideoEnhance, VideoEnhanceConfigJson = Json("MotionBlur", "Local") }).Should().BeTrue();
        StepCreditDefaults.IsComputeOnly(new WorkflowStep { StepType = StepType.VideoEnhance, VideoEnhanceConfigJson = Json("Interpolate", "Hosted", ",\"frameMultiplier\":2") }).Should().BeFalse();
        StepCreditDefaults.MillisFor(StepType.VideoEnhance, 1.5m, computeOnly: true).Should().Be((0, 0));
        StepCreditDefaults.MillisFor(StepType.VideoEnhance, 1.5m).Low.Should().BeGreaterThan(0, "the cold-start row for a hosted operation is never zero");
    }

    [Fact]
    public void The_save_validator_reports_a_bad_field_with_the_step_prefix_and_requires_a_config()
    {
        StepConfigSaveValidator.ValidateVideoEnhanceConfig(null).Should().Equal("VideoEnhanceConfigJson is required.");
        StepConfigSaveValidator.ValidateVideoEnhanceConfig("{oops").Single().Should().StartWith("VideoEnhanceConfigJson is not valid JSON");
        StepConfigSaveValidator.ValidateVideoEnhanceConfig("null").Should().Equal("VideoEnhanceConfigJson must be a JSON object.");
        StepConfigSaveValidator.ValidateVideoEnhanceConfig(Json("Upscale", "Local", ",\"upscaleFactor\":3")).Should().ContainSingle(e => e.Contains("UpscaleFactor"));
        StepConfigSaveValidator.ValidateVideoEnhanceConfig(Json("Upscale", "Local", ",\"upscaleFactor\":2")).Should().BeEmpty();
    }

    [Fact]
    public void An_extend_prompt_over_500_characters_is_refused_and_a_blank_one_uses_the_neutral_default()
    {
        VideoEnhanceStepConfig tooLong = Parse(Json("Extend", "Hosted", ",\"targetAspect\":\"16:9\",\"prompt\":\"" + new string('a', 501) + "\""));
        tooLong.Validate().Should().Contain(e => e.Contains("at most 500", StringComparison.Ordinal));

        VideoEnhanceStepConfig blank = Parse(Json("Extend", "Hosted", ",\"targetAspect\":\"16:9\",\"prompt\":\"  \""));
        blank.Validate().Should().BeEmpty();
        blank.EffectiveExtendPrompt().Should().Be(VideoEnhanceStepConfig.DefaultExtendPrompt);
        Parse(Json("Extend", "Hosted", ",\"targetAspect\":\"16:9\",\"prompt\":\" more sky \"")).EffectiveExtendPrompt().Should().Be("more sky");
    }

    [Fact]
    public void Extend_reads_the_outpaint_capability_and_sizes_its_canvas()
    {
        VideoEnhanceStepConfig config = Parse(Json("Extend", "Hosted", ",\"targetAspect\":\"16:9\""));
        config.HostedCapability.Should().Be(InferenceProviderCapability.VideoOutpaint);
        config.CallsAnyProvider.Should().BeTrue();

        // 1080x1080 to 16:9 adds 0.389 of the width on each side; the canvas is even and 1920 wide.
        VideoEnhanceSizing.Output output = VideoEnhanceSizing.OutputFor(config, 1080, 1080, 24);
        (output.Width, output.Height, output.Fps).Should().Be((1920, 1080, 24d));

        // A portrait clip cannot reach 16:9 inside the 0.5-per-side bound: no canvas, never a clamp.
        config.ExtendCanvas(1080, 1920).Should().BeNull();
        Parse(Json("Extend", "Hosted", ",\"marginLeft\":0.25,\"marginRight\":0.25")).ExtendCanvas(1000, 500)!.Width.Should().Be(1500);
        Parse(Json("Extend", "Hosted", ",\"marginLeft\":0.1,\"marginRight\":0.3")).ExtendCanvas(1920, 1080).Should().BeNull("uneven margins have no canvas");
    }

    [Fact]
    public void A_hosted_extend_hold_is_priced_from_the_outpaint_rows_and_an_unofferable_canvas_is_unpriced()
    {
        VideoEnhanceCreditPricing.OutpaintRateFor(VideoEnhanceModelCatalog.Instance.DefaultFor(InferenceProviderCapability.VideoOutpaint), 720)
            .Should().Be(("720p", 0.08m));
        VideoEnhanceCreditPricing.OutpaintRateFor(VideoEnhanceModelCatalog.Instance.DefaultFor(InferenceProviderCapability.VideoOutpaint), 580)
            .Should().Be(("580p", 0.06m));
        VideoEnhanceCreditPricing.OutpaintRateFor(VideoEnhanceModelCatalog.Instance.DefaultFor(InferenceProviderCapability.VideoOutpaint), 480)
            .Should().Be(("480p", 0.04m));
        (string? lumaVariant, decimal lumaRate) = VideoEnhanceCreditPricing.OutpaintRateFor(
            VideoEnhanceModelCatalog.Instance.Find(InferenceProviderCapability.VideoOutpaint, VideoEnhanceModelCatalog.LumaReframeId)!, 1080)!.Value;
        lumaVariant.Should().BeNull();
        lumaRate.Should().Be(0.2m);
    }
}
