using System;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Exact filter-string tests for <see cref="OutputPolicyFilterBuilder"/> — the B8b trial-output
/// policy (see docs/billing.md): a 720p height cap that never upscales or distorts, plus an
/// optional ReelBolt watermark composite. Covers the no-op short-circuit (the input label is
/// returned unchanged when the cap does nothing and no watermark is requested), the
/// <c>scale=-2:min(ih,720)</c> cap fragment, the overlay-splicing contract inherited from
/// <see cref="OverlayAssetFilterBuilder"/> (base label in, <c>finalLabel</c> out, the caller's
/// <c>inputIndexForIndex</c> callback), the parameterised corner/margin/width-percent placement,
/// and the validation guards.
/// </summary>
public class OutputPolicyFilterBuilderTests
{
    // --------------------------------------------------------------------------- no-op cap

    [Theory]
    [InlineData(1080, 1920, 1080, false)]
    [InlineData(1920, 1080, 1080, false)]
    [InlineData(1440, 2560, 1080, true)]
    [InlineData(2560, 1440, 1080, true)]
    [InlineData(1080, 1920, 720, true)]
    public void The_policy_gate_and_the_builder_agree_on_whether_the_cap_applies(int width, int height, int cap, bool applies)
    {
        // Staging run 514ad632: the compile gated on the height alone (1920 > 1080) while the
        // builder measured the short side and returned the bare label, leaving "[vpol]" as a
        // filterless stage — ffmpeg's "No such filter: ''".
        new ResolvedOutputPolicy(MaxHeight: cap).CapsAt(width, height).Should().Be(applies);
        bool builderIsNoOp = OutputPolicyFilterBuilder.BuildFilterChain("[vpol]", width, height, maxHeight: cap) == "[vpol]";
        builderIsNoOp.Should().Be(!applies);
    }

    [Theory]
    [InlineData(1920, 1080, 720, 720)]
    [InlineData(1440, 2560, 1080, 1920)]
    [InlineData(1080, 1920, 1080, 1920)]
    public void The_recorded_output_height_is_what_the_scale_stage_produces(int width, int height, int cap, int expected)
    {
        var policy = new ResolvedOutputPolicy(MaxHeight: cap);
        policy.ToProvenance(width, height, heightCapApplied: policy.CapsAt(width, height), watermarkApplied: false)
            .OutputHeight.Should().Be(expected);
    }

    [Fact]
    public void A_portrait_reel_at_the_cap_is_left_alone_because_the_cap_names_the_short_side()
    {
        // Staging run 98e57ffd: a 1080x1920 reel under a 1080p cap came out 608x1080.
        OutputPolicyFilterBuilder.BuildFilterChain("[vcut]", 1080, 1920, maxHeight: 1080).Should().Be("[vcut]");
        OutputPolicyFilterBuilder.OutputWidth(1080, 1920, 1080).Should().Be(1080);
    }

    [Fact]
    public void A_portrait_frame_over_the_cap_is_capped_on_its_width()
    {
        string chain = OutputPolicyFilterBuilder.BuildFilterChain("[vcut]", 1440, 2560, maxHeight: 720);
        chain.Should().Contain("scale='min(iw,720)':-2");
        chain.Should().NotContain("min(ih,");
        OutputPolicyFilterBuilder.OutputWidth(1440, 2560, 720).Should().Be(720);
    }

    [Fact]
    public void No_watermark_and_cap_at_or_below_returns_the_input_label_unchanged()
    {
        // 720 is exactly at the 720p cap → no-op; the fragment is the input label itself, NOT
        // terminated by finalLabel — the caller continues the chain from it untouched.
        OutputPolicyFilterBuilder.BuildFilterChain("[vcut]", 1280, 720).Should().Be("[vcut]");
    }

    [Fact]
    public void No_watermark_and_cap_below_returns_the_input_label_unchanged()
    {
        // 480 is below the cap → still a no-op (never upscale).
        OutputPolicyFilterBuilder.BuildFilterChain("[vsrc]", 854, 480).Should().Be("[vsrc]");
    }

    [Fact]
    public void No_watermark_and_exact_custom_cap_returns_the_input_label_unchanged()
    {
        // probedHeight == maxHeight is a no-op even for a custom cap.
        OutputPolicyFilterBuilder.BuildFilterChain("[vcut]", 1920, 540, maxHeight: 540).Should().Be("[vcut]");
    }

    [Fact]
    public void No_watermark_and_cap_below_emits_no_scale_filter_at_all()
    {
        // Belt-and-suspenders on the "no-op" contract: a source already at or below the cap must
        // never be scaled (not even to force even parity) — no scale segment, no new label.
        OutputPolicyFilterBuilder.BuildFilterChain("[vcut]", 1280, 720)
            .Should().NotContain("scale")
            .And.NotContain("[vout]");
    }

    // ---------------------------------------------------------------------- height cap only

    [Fact]
    public void Cap_above_emits_only_the_scale_and_ends_at_vout()
    {
        // 1080 > 720 → a real downscale; the exact fragment is the canonical
        // scale=-2:min(ih,720), never upscaling, aspect-preserved, ending at the default label.
        OutputPolicyFilterBuilder.BuildFilterChain("[vcut]", 1920, 1080)
            .Should().Be("[vcut]scale=-2:'min(ih,720)':flags=lanczos[vout]");
    }

    [Fact]
    public void Cap_above_uses_a_custom_max_height_in_the_expression()
    {
        OutputPolicyFilterBuilder.BuildFilterChain("[vcut]", 1920, 1080, maxHeight: 540)
            .Should().Contain("scale=-2:'min(ih,540)':flags=lanczos");
    }

    [Fact]
    public void Cap_above_terminates_at_a_custom_final_label_when_supplied()
    {
        OutputPolicyFilterBuilder.BuildFilterChain("[vcut]", 1920, 1080, finalLabel: "[enc]")
            .Should().EndWith("[enc]");
    }

    // ---------------------------------------------------------- no cap + watermark only

    [Fact]
    public void No_cap_with_watermark_skips_the_video_scale_but_still_composites()
    {
        // 720 is a no-op cap, so there is NO video scale stage: the base label goes straight into
        // the overlay, the watermark's own input is scaled, and the chain still ends at [vout].
        string chain = OutputPolicyFilterBuilder.BuildFilterChain(
            "[vcut]", 1280, 720, applyWatermark: true, inputIndexForIndex: _ => 1);

        chain.Should()
            .NotContain("scale=-2") // no video cap
            .And.Contain("[1:v]format=rgba,scale=192:-1:flags=bilinear[wm]")
            .And.Contain("[vcut][wm]overlay=")
            .And.EndWith("[vout]");
    }

    // -------------------------------------------------------- cap + watermark (full chain)

    [Fact]
    public void Cap_and_watermark_produce_three_segments_in_order()
    {
        // 1080 > 720 caps to 720 (output width 1280); the watermark is 15% of that (192px) and is
        // composited bottom-right on the CAPPED frame, then the chain ends at [vout].
        string chain = OutputPolicyFilterBuilder.BuildFilterChain(
            "[vcut]", 1920, 1080, applyWatermark: true, inputIndexForIndex: _ => 1);

        chain.Should()
            .Contain("[vcut]scale=-2:'min(ih,720)':flags=lanczos[vcap]")
            .And.Contain("[1:v]format=rgba,scale=192:-1:flags=bilinear[wm]")
            .And.Contain("[vcap][wm]overlay=")
            .And.EndWith("[vout]");
    }

    [Fact]
    public void Overlay_composite_sits_on_the_capped_frame_not_the_raw_base()
    {
        // The overlay's main input must be the [vcap] label the scale produced, never the raw base
        // label — compositing on the raw (un-capped) frame would mark a taller picture.
        string chain = OutputPolicyFilterBuilder.BuildFilterChain(
            "[vcut]", 1920, 1080, applyWatermark: true, inputIndexForIndex: _ => 1);

        chain.Should().Contain("[vcap][wm]overlay=");
    }

    // --------------------------------------------------------------- input-index contract

    [Fact]
    public void Overlay_references_the_input_index_the_caller_supplied()
    {
        // Mirrors OverlayAssetFilterBuilder: the builder never names the watermark's own -i slot;
        // the caller resolves it through inputIndexForIndex and it appears verbatim as [idx:v].
        string chain = OutputPolicyFilterBuilder.BuildFilterChain(
            "[vcut]", 1920, 1080, applyWatermark: true, inputIndexForIndex: _ => 3);

        chain.Should().Contain("[3:v]format=rgba,scale=");
    }

    // ------------------------------------------------------------------- placement knobs

    [Fact]
    public void Watermark_width_scales_with_the_percentage_and_the_output_width()
    {
        // No cap: 1280 wide, 10% → 128px.
        OutputPolicyFilterBuilder.BuildFilterChain(
            "[vcut]", 1280, 720, applyWatermark: true, watermarkWidthPct: 10, inputIndexForIndex: _ => 1)
            .Should().Contain("scale=128:-1:flags=bilinear");

        // Cap: 1920x1080 caps to 1280 wide, 10% of THAT → still 128px (percentage of output, not
        // source width — a 1920 source at 10% would be 192, so this proves we use the capped width).
        OutputPolicyFilterBuilder.BuildFilterChain(
            "[vcut]", 1920, 1080, applyWatermark: true, watermarkWidthPct: 10, inputIndexForIndex: _ => 1)
            .Should().Contain("scale=128:-1:flags=bilinear");
    }

    [Fact]
    public void Watermark_margin_is_parameterised_in_both_axes()
    {
        OutputPolicyFilterBuilder.BuildFilterChain(
            "[vcut]", 1280, 720, applyWatermark: true, watermarkMargin: 40, inputIndexForIndex: _ => 1)
            .Should().Contain("overlay=x='main_w-overlay_w-40':y='main_h-overlay_h-40'");
    }

    [Theory]
    [InlineData(WatermarkCorner.BottomRight, "main_w-overlay_w-24", "main_h-overlay_h-24")]
    [InlineData(WatermarkCorner.TopRight, "main_w-overlay_w-24", "24")]
    [InlineData(WatermarkCorner.BottomLeft, "24", "main_h-overlay_h-24")]
    [InlineData(WatermarkCorner.TopLeft, "24", "24")]
    public void Overlay_expressions_pin_the_near_corner_for_every_corner(WatermarkCorner corner, string expectedX, string expectedY)
    {
        (string x, string y) = OutputPolicyFilterBuilder.OverlayExpression(corner, margin: 24);
        (x, y).Should().Be((expectedX, expectedY));
    }

    [Fact]
    public void Default_corner_is_bottom_right()
    {
        OutputPolicyFilterBuilder.DefaultCorner.Should().Be(WatermarkCorner.BottomRight);
        OutputPolicyFilterBuilder.BuildFilterChain(
            "[vcut]", 1280, 720, applyWatermark: true, inputIndexForIndex: _ => 1)
            .Should().Contain("overlay=x='main_w-overlay_w-24':y='main_h-overlay_h-24'");
    }

    // --------------------------------------------------------------------------- helpers

    [Fact]
    public void Output_width_is_aspect_preserved_and_even_when_capped()
    {
        // 1920x1080 → 720 tall: 1920 * 720 / 1080 = 1280 (already even).
        OutputPolicyFilterBuilder.OutputWidth(1920, 1080, 720).Should().Be(1280);
        // 1600x900 → 720 tall: 1600 * 720 / 900 = 1280.
        OutputPolicyFilterBuilder.OutputWidth(1600, 900, 720).Should().Be(1280);
    }

    [Fact]
    public void Output_width_is_the_source_width_when_not_capped()
    {
        // Below the cap: no scaling, so the width is the source width verbatim (even if odd).
        OutputPolicyFilterBuilder.OutputWidth(853, 480, 720).Should().Be(853);
    }

    [Fact]
    public void Output_width_is_even_when_the_arithmetic_rounds_odd()
    {
        // 1000x1000 → 720: 1000*720/1000 = 720 (even). Use a case that rounds to odd: 1001x1000
        // → 1001*720/1000 = 720.72 → rounds to 721 (odd) → bumped to even 722.
        int w = OutputPolicyFilterBuilder.OutputWidth(1001, 1000, 720);
        w.Should().Be(722);
    }

    // --------------------------------------------------------------------------- validation

    [Fact]
    public void Throws_when_a_watermark_is_requested_but_no_input_index_callback_is_supplied()
    {
        Action act = () => OutputPolicyFilterBuilder.BuildFilterChain("[vcut]", 1280, 720, applyWatermark: true);
        act.Should().Throw<ArgumentNullException>().WithParameterName("inputIndexForIndex");
    }

    [Fact]
    public void Throws_on_a_non_positive_probed_dimension()
    {
        Action a = () => OutputPolicyFilterBuilder.BuildFilterChain("[vcut]", 0, 720);
        a.Should().Throw<ArgumentOutOfRangeException>();
        Action b = () => OutputPolicyFilterBuilder.BuildFilterChain("[vcut]", 1280, -5);
        b.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Throws_on_a_non_positive_max_height()
    {
        Action act = () => OutputPolicyFilterBuilder.BuildFilterChain("[vcut]", 1920, 1080, maxHeight: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Throws_on_a_watermark_width_pct_outside_the_open_closed_range(int pct)
    {
        Action act = () => OutputPolicyFilterBuilder.BuildFilterChain(
            "[vcut]", 1280, 720, applyWatermark: true, watermarkWidthPct: pct, inputIndexForIndex: _ => 1);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("watermarkWidthPct");
    }

    [Fact]
    public void Throws_on_a_negative_margin()
    {
        Action act = () => OutputPolicyFilterBuilder.BuildFilterChain(
            "[vcut]", 1280, 720, applyWatermark: true, watermarkMargin: -1, inputIndexForIndex: _ => 1);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("watermarkMargin");
    }
}
