using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Editing;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>colorbalance</c> keeps the LAST <c>gm</c>, so the builder used to emit gamma-green
/// and tint as two <c>gm</c> options and tint silently replaced the gamma. A node carrying both now
/// emits ONE <c>gm</c> in the gamma-green slot; every stack with only one of them keeps the exact
/// filter text (and position) it had, so existing golden renders do not move.
/// </summary>
public class GradeFilterBuilderGmMergeTests
{
    private static string Chain(GradePrimaries primaries)
    {
        List<GradeFilterBuilder.LutRequest> luts = [];
        int index = 0;
        return GradeFilterBuilder.Build(new GradeStack(true, [new GradeNode(Guid.Empty, Primaries: primaries)]), _ => string.Empty, luts, ref index);
    }

    [Fact]
    public void Tint_alone_emits_exactly_the_old_gm_text()
    {
        Chain(new GradePrimaries(Tint: 0.5)).Should().Be("colorbalance=gm=-0.25");
    }

    [Fact]
    public void Gamma_green_alone_emits_exactly_the_old_gm_text()
    {
        Chain(new GradePrimaries(Gamma: new GradeWheel(G: 0.3))).Should().Be("colorbalance=gm=0.3");
    }

    [Fact]
    public void Gamma_green_with_other_channels_keeps_its_position()
    {
        Chain(new GradePrimaries(Gamma: new GradeWheel(R: 0.1, G: 0.3, B: 0.2)))
            .Should().Be("colorbalance=rm=0.1:gm=0.3:bm=0.2");
    }

    [Fact]
    public void Tint_and_gamma_green_merge_into_one_gm_in_the_gamma_slot()
    {
        string chain = Chain(new GradePrimaries(Gamma: new GradeWheel(G: 0.3), Tint: 0.5));
        chain.Should().Be("colorbalance=gm=0.04999999999999999", "0.3 + (-0.5 / 2), in the builder's round-trip double formatting");
        chain.Should().NotBe("colorbalance=gm=0.3:gm=-0.25", "that was the old text ffmpeg read as tint-only");
    }

    [Fact]
    public void Merged_gm_keeps_the_gamma_wheel_channel_order()
    {
        Chain(new GradePrimaries(Gamma: new GradeWheel(R: 0.1, G: 0.3, B: 0.2), Tint: 0.5))
            .Should().Be("colorbalance=rm=0.1:gm=0.04999999999999999:bm=0.2");
    }

    [Fact]
    public void A_merge_is_clamped_to_the_colorbalance_range()
    {
        Chain(new GradePrimaries(Gamma: new GradeWheel(G: 1), Tint: -1))
            .Should().Be("colorbalance=gm=1");
    }

    [Fact]
    public void Only_a_node_with_both_tint_and_gamma_green_is_marked_for_the_recipe_hash()
    {
        GradeFilterBuilder.MergesTintIntoGammaGreen(new GradePrimaries(Tint: 0.5, Gamma: new GradeWheel(G: 0.3))).Should().BeTrue();
        GradeFilterBuilder.MergesTintIntoGammaGreen(new GradePrimaries(Tint: 0.5)).Should().BeFalse();
        GradeFilterBuilder.MergesTintIntoGammaGreen(new GradePrimaries(Gamma: new GradeWheel(G: 0.3))).Should().BeFalse();
        GradeFilterBuilder.MergesTintIntoGammaGreen(new GradePrimaries(Tint: 0.5, Gamma: new GradeWheel(R: 0.3, B: 0.1))).Should().BeFalse();
    }
}
