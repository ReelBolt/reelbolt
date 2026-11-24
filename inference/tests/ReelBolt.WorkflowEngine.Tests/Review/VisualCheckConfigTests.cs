using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// <see cref="VisualCheckConfig"/>, appended to <see cref="ReviewLoopStepConfig"/> as its last positional
/// member: off by default, absent means off, raw values round-trip and only the effective values clamp.
/// </summary>
public class VisualCheckConfigTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Defaults_match_the_documented_values_and_are_off()
    {
        VisualCheckConfig config = new();

        config.Enabled.Should().BeFalse();
        config.Frames.Should().Be(8);
        config.SafeMarginPercent.Should().Be(5);
        config.MinTextHeightPercent.Should().Be(2.5);
        config.FailScoreCap.Should().Be(6);
        config.VisionProviderId.Should().BeNull();
    }

    [Fact]
    public void A_review_loop_config_without_the_member_has_no_visual_check()
    {
        ReviewLoopStepExecutor.TryReadConfig("""{"enabled":true}""")!.VisualCheck.Should().BeNull();
        new ReviewLoopStepConfig().VisualCheck.Should().BeNull();
    }

    [Fact]
    public void The_member_reads_from_the_step_json_and_keeps_raw_values()
    {
        ReviewLoopStepConfig? config = ReviewLoopStepExecutor.TryReadConfig(
            """{"visualCheck":{"enabled":true,"frames":40,"safeMarginPercent":-3,"minTextHeightPercent":0,"failScoreCap":11}}""");

        VisualCheckConfig visual = config!.VisualCheck!;
        visual.Enabled.Should().BeTrue();
        visual.Frames.Should().Be(40, "the raw value is what round-trips");
        visual.ClampedFrames.Should().Be(16);
        visual.ClampedSafeMarginPercent.Should().Be(0);
        visual.ClampedMinTextHeightPercent.Should().Be(VisualCheckConfig.MinTextHeightPercentFloor);
        visual.ClampedFailScoreCap.Should().Be(10);
        config.Enabled.Should().BeFalse("the visual check does not turn the early-stop gate on");
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(8, 8)]
    [InlineData(17, 16)]
    public void Frames_clamp_to_two_through_sixteen(int raw, int effective) =>
        new VisualCheckConfig(Frames: raw).ClampedFrames.Should().Be(effective);

    [Fact]
    public void The_effective_values_stay_out_of_json()
    {
        string json = JsonSerializer.Serialize(new VisualCheckConfig(Enabled: true), Web);

        json.Should().NotContain("clamped", "derived values are not inputs");
        json.Should().Contain("\"frames\":8");
    }

    [Fact]
    public void The_step_schema_exposes_visual_check()
    {
        StepConfigSchemaResult result = new StepConfigSchemaExporter().GetSchema(StepType.ReviewLoop);

        string schema = result.Schema!.ToJsonString();
        schema.Should().Contain("visualCheck");
        schema.Should().Contain("safeMarginPercent").And.Contain("failScoreCap").And.Contain("visionProviderId");
        schema.Should().NotContain("clampedFrames");
    }
}
