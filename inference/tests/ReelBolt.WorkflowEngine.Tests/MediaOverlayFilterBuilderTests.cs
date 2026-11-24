using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="MediaOverlayFilterBuilder"/> — the exact ffmpeg filter strings for pictures and videos
/// over the film (docs/design/picture-overlay.md §6.1), and the safe-area geometry
/// (<see cref="MediaOverlayFilterBuilder.ComputeInsetBox"/>/<see cref="MediaOverlayFilterBuilder.ComputeCardBox"/>)
/// that feeds it. Every expected string and every box is hard-coded from the arithmetic, so a change
/// in either the recipe or the geometry is a visible failure rather than a tautology.
/// </summary>
public class MediaOverlayFilterBuilderTests
{
    private static ResolvedMediaOverlay Card(
        string token = "mo0",
        int inputIndex = 3,
        double startSec = 1.2,
        double endSec = 6.4,
        double opacity = 1.0) => new(
            token, MediaOverlayForm.Card, inputIndex,
            BoxX: 0, BoxY: 0, BoxW: 1920, BoxH: 1080,
            Opacity: opacity, Border: MediaOverlayBorder.None,
            OutputStartSec: startSec, OutputEndSec: endSec,
            EndsEarly: false, ContentName: "testimonial.png");

    private static ResolvedMediaOverlay Inset(
        string token = "mo1",
        int inputIndex = 3,
        int x = 1206, int y = 678, int w = 636, int h = 358,
        double opacity = 1.0,
        MediaOverlayBorder border = MediaOverlayBorder.None,
        double startSec = 7.0,
        double endSec = 13.0) => new(
            token, MediaOverlayForm.PictureInPicture, inputIndex,
            BoxX: x, BoxY: y, BoxW: w, BoxH: h,
            Opacity: opacity, Border: border,
            OutputStartSec: startSec, OutputEndSec: endSec,
            EndsEarly: true, ContentName: "demo.mp4");

    private static string Chain(
        IReadOnlyList<ResolvedMediaOverlay> overlays,
        int canvasWidth = 1920,
        int canvasHeight = 1080,
        Func<int, int>? inputIndexForIndex = null,
        string finalLabel = "[vout]",
        int fpsNum = 30,
        int fpsDen = 1) =>
        string.Join(";", MediaOverlayFilterBuilder.BuildFilterChain(
            "[vcut]", overlays, canvasWidth, canvasHeight,
            inputIndexForIndex ?? (i => i + 1), finalLabel, fpsNum, fpsDen));

    [Fact]
    public void Card_is_an_exact_two_segment_recipe_that_fills_the_frame_and_ends_at_vout()
    {
        Chain([Card()]).Should().Be(
            "[1:v]format=rgba,fps=30/1,trim=duration=5.2,scale=1920:1080:force_original_aspect_ratio=decrease," +
            "pad=1920:1080:(ow-iw)/2:(oh-ih)/2:black,colorchannelmixer=aa=1,setpts=PTS+1.2/TB[mosrc0];" +
            "[vcut][mosrc0]overlay=x=0:y=0:eof_action=pass:enable='between(t,1.2,6.4)'[vout]");
    }

    [Fact]
    public void Inset_is_an_exact_two_segment_recipe_without_a_border()
    {
        Chain([Inset()]).Should().Be(
            "[1:v]format=rgba,fps=30/1,trim=duration=6,scale=636:358:force_original_aspect_ratio=decrease," +
            "pad=636:358:0:0:black,colorchannelmixer=aa=1,setpts=PTS+7/TB[mosrc0];" +
            "[vcut][mosrc0]overlay=x=1206:y=678:eof_action=pass:enable='between(t,7,13)'[vout]");
    }

    [Fact]
    public void Format_rgba_is_emitted_before_scale()
    {
        string chain = Chain([Inset()]);

        chain.Should().Contain("[1:v]format=rgba,fps=30/1,trim=duration=6,scale=636:358");
        chain.IndexOf("format=rgba", StringComparison.Ordinal)
            .Should().BeLessThan(chain.IndexOf("scale=", StringComparison.Ordinal));
        chain.IndexOf("trim=duration=6", StringComparison.Ordinal)
            .Should().BeLessThan(chain.IndexOf("scale=", StringComparison.Ordinal));
    }

    [Fact]
    public void The_program_frame_rate_is_what_the_overlay_is_normalized_to()
    {
        Chain([Inset()], fpsNum: 24, fpsDen: 1).Should().Contain("format=rgba,fps=24/1,");
        Chain([Inset()], fpsNum: 24000, fpsDen: 1001).Should().Contain("format=rgba,fps=24000/1001,");
    }

    [Fact]
    public void A_token_that_looks_like_a_filter_is_never_interpolated()
    {
        // The token is diagnostics only; labels are generated from the overlay index.
        string chain = Chain([Inset() with { Token = "x]; drop" }]);

        chain.Should().Contain("[mosrc0]");
        chain.Should().Contain("[mosrc0]");
        chain.Should().NotContain("x]; drop");
        chain.Should().NotContain("]; drop");
    }

    [Theory]
    [InlineData("[vcut]; drop")]
    [InlineData("vcut")]
    [InlineData("[v cut]")]
    [InlineData("[1vcut]")]
    public void An_invalid_program_label_is_refused(string label)
    {
        Action act = () => MediaOverlayFilterBuilder.BuildFilterChain(
            label, [Inset()], 1920, 1080, i => i + 1);
        act.Should().Throw<ArgumentException>();

        Action final = () => MediaOverlayFilterBuilder.BuildFilterChain(
            "[vcut]", [Inset()], 1920, 1080, i => i + 1, finalLabel: label);
        final.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(MediaOverlayAnchor.TopLeft, 76, 44)]
    [InlineData(MediaOverlayAnchor.Top, 641, 44)]
    [InlineData(MediaOverlayAnchor.TopRight, 1206, 44)]
    [InlineData(MediaOverlayAnchor.Left, 76, 361)]
    [InlineData(MediaOverlayAnchor.Center, 641, 361)]
    [InlineData(MediaOverlayAnchor.Right, 1206, 361)]
    [InlineData(MediaOverlayAnchor.BottomLeft, 76, 678)]
    [InlineData(MediaOverlayAnchor.Bottom, 641, 678)]
    [InlineData(MediaOverlayAnchor.BottomRight, 1206, 678)]
    public void Every_anchor_places_the_resolved_box_at_its_hard_coded_position(
        MediaOverlayAnchor anchor, int expectedX, int expectedY)
    {
        (int x, int y, int w, int h) = MediaOverlayFilterBuilder.ComputeInsetBox(
            anchor, MediaOverlaySize.Medium, 0.04, contentWidth: 640, contentHeight: 360,
            canvasWidth: 1920, canvasHeight: 1080);

        (x, y, w, h).Should().Be((expectedX, expectedY, 636, 358));
    }

    [Theory]
    [InlineData(MediaOverlaySize.Small, 442, 248)]
    [InlineData(MediaOverlaySize.Medium, 636, 358)]
    [InlineData(MediaOverlaySize.Large, 884, 498)]
    public void Size_is_the_fraction_of_the_safe_width_and_the_height_follows_the_content_aspect(
        MediaOverlaySize size, int expectedW, int expectedH)
    {
        (_, _, int w, int h) = MediaOverlayFilterBuilder.ComputeInsetBox(
            MediaOverlayAnchor.TopLeft, size, 0.04, contentWidth: 640, contentHeight: 360,
            canvasWidth: 1920, canvasHeight: 1080);

        (w, h).Should().Be((expectedW, expectedH));
    }

    [Fact]
    public void Opacity_50_percent_is_emitted_as_the_0_to_1_alpha()
    {
        Chain([Card(opacity: 0.5)]).Should().Contain("colorchannelmixer=aa=0.5,");
    }

    [Theory]
    [InlineData(MediaOverlayBorder.Light, "pad=640:362:2:2:white", 1204, 676)]
    [InlineData(MediaOverlayBorder.Dark, "pad=640:362:2:2:black", 1204, 676)]
    public void Border_light_is_a_white_hairline_and_dark_a_black_one_around_the_content(
        MediaOverlayBorder border, string expectedPad, int expectedX, int expectedY)
    {
        string chain = Chain([Inset(border: border)]);

        chain.Should().Contain(expectedPad);
        chain.Should().Contain($"overlay=x={expectedX}:y={expectedY}:");
    }

    [Fact]
    public void Two_overlays_are_chained_in_order_and_the_second_consumes_the_first_output_node()
    {
        string chain = Chain([Card(), Inset(opacity: 0.5)]);

        string[] segments = chain.Split(';');
        segments.Should().HaveCount(4);
        segments[0].Should().StartWith("[1:v]format=rgba,fps=30/1,trim=duration=5.2,scale=1920:1080:");
        segments[1].Should().Be(
            "[vcut][mosrc0]overlay=x=0:y=0:eof_action=pass:enable='between(t,1.2,6.4)'[mo0]");
        segments[2].Should().StartWith("[2:v]format=rgba,fps=30/1,trim=duration=6,scale=636:358:");
        segments[3].Should().Be(
            "[mo0][mosrc1]overlay=x=1206:y=678:eof_action=pass:enable='between(t,7,13)'[vout]");
    }

    [Fact]
    public void Four_by_five_content_in_a_sixteen_by_nine_canvas_clamps_the_height_and_the_width_follows()
    {
        (_, _, int w, int h) = MediaOverlayFilterBuilder.ComputeInsetBox(
            MediaOverlayAnchor.BottomRight, MediaOverlaySize.Large, 0.04,
            contentWidth: 800, contentHeight: 1000, canvasWidth: 1920, canvasHeight: 1080);

        (w, h).Should().Be((794, 992));
        (w <= 1766 && h <= 992).Should().BeTrue("the box stays inside the safe rectangle");
    }

    [Fact]
    public void Odd_content_dimensions_are_rounded_to_even()
    {
        (_, _, int w, int h) = MediaOverlayFilterBuilder.ComputeInsetBox(
            MediaOverlayAnchor.Center, MediaOverlaySize.Medium, 0.04,
            contentWidth: 321, contentHeight: 241, canvasWidth: 1920, canvasHeight: 1080);

        (w, h).Should().Be((636, 478));
        (w % 2).Should().Be(0);
        (h % 2).Should().Be(0);
    }

    [Fact]
    public void The_card_box_and_its_scale_target_are_even_for_odd_canvas_dimensions()
    {
        MediaOverlayFilterBuilder.ComputeCardBox(1921, 1081).Should().Be((0, 0, 1920, 1080));

        Chain([Card()], canvasWidth: 1921, canvasHeight: 1081).Should()
            .Contain("scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:");
    }

    [Fact]
    public void finalLabel_override_terminates_the_last_overlay()
    {
        string chain = Chain([Inset()], finalLabel: "[vfinal]");

        chain.Should().EndWith("[vcut][mosrc0]overlay=x=1206:y=678:eof_action=pass:enable='between(t,7,13)'[vfinal]");
    }

    [Fact]
    public void An_empty_overlay_list_throws_because_the_caller_never_asks_for_nothing()
    {
        Action act = () => MediaOverlayFilterBuilder.BuildFilterChain(
            "[vcut]", [], 1920, 1080, i => i + 1);

        act.Should().Throw<ArgumentException>().WithParameterName("overlays");
    }

    [Fact]
    public void The_default_fps_is_the_pinned_program_rate()
    {
        MediaOverlayFilterBuilder.DefaultFpsNum.Should().Be(30);
        MediaOverlayFilterBuilder.DefaultFpsDen.Should().Be(1);
        Chain([Card()]).Should().Contain("fps=30/1,");
    }
}
