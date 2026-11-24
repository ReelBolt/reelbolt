using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-105: ConformHeight defaults to the run's chosen output short side, never beyond it.</summary>
public sealed class VideoGenerateConformHeightDefaultTests
{
    [Fact]
    public void An_explicit_ConformHeight_always_wins()
    {
        VideoGenerateStepConfig.ResolveConformHeight(720, 2160, "16:9").Should().Be(new ConformHeightResolution(720, "config"));
    }

    [Fact]
    public void No_run_resolution_leaves_the_clip_alone()
    {
        VideoGenerateStepConfig.ResolveConformHeight(0, null, "16:9").Should().Be(new ConformHeightResolution(0, "none"));
    }

    [Theory]
    [InlineData(1080, "16:9", 1080)]
    [InlineData(720, "4:3", 720)]
    [InlineData(1080, "1:1", 1080)]
    [InlineData(2160, "16:9", 2160)]
    public void A_landscape_or_square_clip_is_conformed_to_the_run_short_side_as_its_height(int shortSide, string ratio, int height)
    {
        VideoGenerateStepConfig.ResolveConformHeight(0, shortSide, ratio).Should().Be(new ConformHeightResolution(height, "run"));
    }

    [Theory]
    [InlineData(1080, "9:16", 1920)]
    [InlineData(720, "3:4", 960)]
    [InlineData(2160, "9:16", 3840)]
    public void A_portrait_clips_short_side_is_its_width_so_the_height_is_scaled_up_by_the_ratio(int shortSide, string ratio, int height)
    {
        VideoGenerateStepConfig.ResolveConformHeight(0, shortSide, ratio).Should().Be(new ConformHeightResolution(height, "run"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("wide")]
    [InlineData("0:0")]
    [InlineData(null)]
    public void An_unreadable_ratio_falls_back_to_the_short_side(string? ratio)
    {
        VideoGenerateStepConfig.ResolveConformHeight(0, 1080, ratio).Should().Be(new ConformHeightResolution(1080, "run"));
    }

    [Fact]
    public void The_height_never_exceeds_the_configs_own_upper_bound()
    {
        VideoGenerateStepConfig.ResolveConformHeight(0, 2160, "1:9").Height.Should().BeLessThanOrEqualTo(4320);
    }
}
