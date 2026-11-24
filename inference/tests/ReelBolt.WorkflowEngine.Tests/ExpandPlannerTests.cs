#nullable enable
using System;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The expand planner: shape to request, the endpoint's documented limits, which picture is sent (the
/// original whenever it fits, otherwise the size the layer appears in the design - the owner's decision) and
/// where the result lands. Pure, so the numbers are pinned exactly, starting from the case that failed on
/// staging: a 5931x3954 phone photo made tall enough for a 9:16 reel cover.
/// </summary>
public class ExpandPlannerTests
{
    private static readonly ExpandLimits Bria = ExpandLimits.For(FalAiImageClient.Catalog.Find("fal-ai/bria/expand")!);
    private static readonly ExpandLimits Flux = ExpandLimits.For(FalAiImageClient.Catalog.Find("fal-ai/flux-2-pro/outpaint")!);
    private static readonly ExpandTarget.Shape Reel = new(9, 16);

    private static ExpandPlan Planned(ExpandPlanOutcome outcome)
    {
        outcome.Refusal.Should().BeNull();
        return outcome.Plan!;
    }

    // ================================================================ shape to geometry

    [Fact]
    public void A_5931x3954_photo_to_9_16_grows_3295_px_at_the_top_and_bottom_at_full_size()
    {
        ImageExpandGeometry g = ExpandPlanner.GeometryAt(5931, 3954, 5931, 3954, Reel);

        g.Should().Be(new ImageExpandGeometry(5931, 10544, 0, 3295, 5931, 3954));
        g.Bottom.Should().Be(3295);
        g.Left.Should().Be(0);
        g.Right.Should().Be(0);
    }

    [Theory]
    [InlineData(1000, 1000, 16, 9, 1778, 1000, 389, 0)]
    [InlineData(1000, 1000, 4, 5, 1000, 1250, 0, 125)]
    [InlineData(1920, 1080, 1, 1, 1920, 1920, 0, 420)]
    [InlineData(1080, 1920, 16, 9, 3413, 1920, 1166, 0)]
    public void A_shape_only_ever_adds_never_crops(int w, int h, int aw, int ah, int cw, int ch, int x, int y)
    {
        ImageExpandGeometry g = ExpandPlanner.GeometryAt(w, h, w, h, new ExpandTarget.Shape(aw, ah));

        g.Should().Be(new ImageExpandGeometry(cw, ch, x, y, w, h));
    }

    [Fact]
    public void A_picture_that_already_has_the_shape_is_refused_in_plain_words()
    {
        ExpandPlanOutcome outcome = ExpandPlanner.Plan(1600, 900, new ExpandTarget.Shape(16, 9), Bria, 1);

        outcome.Plan.Should().BeNull();
        outcome.Refusal.Should().Contain("already has the 16:9 shape");
    }

    [Theory]
    [InlineData("9:16", 9, 16)]
    [InlineData(" 4 : 5 ", 4, 5)]
    [InlineData("1:1", 1, 1)]
    public void Shapes_parse(string text, int w, int h) =>
        ExpandTarget.ParseShape(text).Should().Be(new ExpandTarget.Shape(w, h));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wide")]
    [InlineData("9/16")]
    [InlineData("0:1")]
    [InlineData("-9:16")]
    [InlineData("1:9")]
    [InlineData("9:16:1")]
    public void Nonsense_is_not_a_shape(string? text) => ExpandTarget.ParseShape(text).Should().BeNull();

    // ================================================================ Bria (canvas): always the original

    [Fact]
    public void Bria_takes_the_23_MP_original_and_is_told_to_draw_it_on_a_canvas_under_25_MP()
    {
        ExpandPlan plan = Planned(ExpandPlanner.Plan(5931, 3954, Reel, Bria, displayScale: 0.21));

        plan.Source.Should().Be(ExpandSource.Original, "a canvas endpoint draws the original at the size it is told");
        (plan.SendWidth, plan.SendHeight).Should().Be((5931, 3954));
        plan.Notice.Should().BeNull();

        ImageExpandGeometry g = plan.Geometry;
        ((long)g.CanvasWidth * g.CanvasHeight).Should().BeLessThan(25_000_000);
        ((long)g.CanvasWidth * g.CanvasHeight).Should().BeGreaterThan(24_900_000, "as large as the vendor allows");
        ((double)g.CanvasWidth / g.CanvasHeight).Should().BeApproximately(9.0 / 16, 0.0005);
        g.ImageWidth.Should().Be(g.CanvasWidth);
        ((double)g.ImageWidth / g.ImageHeight).Should().BeApproximately(5931.0 / 3954, 0.001);
        g.Top.Should().BeCloseTo(g.Bottom, 1);
    }

    [Fact]
    public void Bria_keeps_a_small_original_at_its_own_size()
    {
        ExpandPlan plan = Planned(ExpandPlanner.Plan(1200, 800, Reel, Bria, 1));

        plan.Geometry.Should().Be(new ImageExpandGeometry(1200, 2133, 0, 666, 1200, 800));
    }

    // ================================================================ FLUX.2 (margins): the owner's decision

    [Fact]
    public void Original_fits_so_the_original_is_sent()
    {
        ExpandPlan plan = Planned(ExpandPlanner.Plan(1000, 667, Reel, Flux, displayScale: 0.3));

        plan.Source.Should().Be(ExpandSource.Original, "the original is preferred whenever the documented limits allow it");
        plan.Geometry.Should().Be(new ImageExpandGeometry(1000, 1778, 0, 555, 1000, 667));
        plan.Notice.Should().BeNull();
    }

    [Fact]
    public void Too_big_so_the_picture_is_sent_at_the_size_it_appears_in_the_design_with_a_warning()
    {
        // the owner's layer b4899f87: IMG_1801.jpg shown at scale 0.2102 on a 720x1280 canvas
        ExpandPlan plan = Planned(ExpandPlanner.Plan(5931, 3954, Reel, Flux, displayScale: 0.2101992914359187));

        plan.Source.Should().Be(ExpandSource.DisplayedSize);
        (plan.SendWidth, plan.SendHeight).Should().Be((1247, 831));
        plan.Geometry.Should().Be(new ImageExpandGeometry(1247, 2217, 0, 693, 1247, 831));
        ExpandPlanner.Fits(plan.Geometry, Flux).Should().BeTrue();
        plan.Notice.Should().Be(
            "This picture was larger than the AI service accepts, so it used the picture at the size it appears in your design (1247×831).");
    }

    [Fact]
    public void Too_big_even_at_the_displayed_size_so_the_largest_copy_the_service_takes_is_sent_and_said_so()
    {
        // the owner's other layer: shown at 0.616, i.e. 3652x2435 on the canvas - 23.7 MP once grown to 9:16
        ExpandPlan plan = Planned(ExpandPlanner.Plan(5931, 3954, Reel, Flux, displayScale: 0.6157837718315486));

        plan.Source.Should().Be(ExpandSource.Smaller);
        ImageExpandGeometry g = plan.Geometry;
        ExpandPlanner.Fits(g, Flux).Should().BeTrue();
        ((long)g.CanvasWidth * g.CanvasHeight).Should().BeLessThanOrEqualTo(2048L * 2048).And.BeGreaterThan(4_140_000);
        (plan.SendWidth, plan.SendHeight).Should().Be((g.ImageWidth, g.ImageHeight));
        plan.SendWidth.Should().BeLessThan(3652);
        plan.Notice.Should().StartWith("This picture was larger than the AI service accepts, even at the size it appears in your design")
            .And.Contain(plan.SendWidth + "×" + plan.SendHeight);
    }

    [Fact]
    public void The_request_that_failed_on_staging_is_planned_inside_every_documented_limit()
    {
        // top and bottom 2048 on the full 5931x3954 original: 5931x8050 = 47.7 MP, twelve times FLUX.2's 4 MP
        ExpandPlanner.Fits(new ImageExpandGeometry(5931, 8050, 0, 2048, 5931, 3954), Flux).Should().BeFalse();

        ExpandPlan plan = Planned(ExpandPlanner.Plan(5931, 3954, new ExpandTarget.Margins(0, 2048, 0, 2048), Flux, 1));

        plan.Source.Should().Be(ExpandSource.Smaller, "the layer is shown at full size, so no displayed-size copy is smaller");
        ExpandPlanner.Fits(plan.Geometry, Flux).Should().BeTrue();
        plan.Geometry.Top.Should().Be(plan.Geometry.Bottom);
        ((double)plan.Geometry.Top / plan.Geometry.ImageHeight).Should().BeApproximately(2048.0 / 3954, 0.005,
            "margins are scaled with the picture, so the result has the shape that was asked for");
    }

    [Fact]
    public void A_margin_over_the_per_side_cap_is_met_by_a_smaller_copy_not_refused()
    {
        ExpandPlan plan = Planned(ExpandPlanner.Plan(1000, 1000, new ExpandTarget.Margins(0, 3000, 0, 0), Flux, 1));

        plan.Source.Should().Be(ExpandSource.Smaller);
        plan.Geometry.Top.Should().BeLessThanOrEqualTo(2048);
        ExpandPlanner.Fits(plan.Geometry, Flux).Should().BeTrue();
    }

    [Fact]
    public void An_uploaded_copy_of_the_displayed_size_is_planned_as_it_is()
    {
        ExpandPlan plan = Planned(ExpandPlanner.Plan(5931, 3954, Reel, Flux, 0.2101992914359187, copy: (1247, 831)));

        plan.Source.Should().Be(ExpandSource.DisplayedSize);
        plan.Geometry.Should().Be(new ImageExpandGeometry(1247, 2217, 0, 693, 1247, 831));
    }

    [Theory]
    [InlineData(1247, 500)]
    [InlineData(6000, 4000)]
    [InlineData(3652, 2435)]
    public void A_copy_of_the_wrong_shape_or_size_is_refused(int w, int h)
    {
        ExpandPlanOutcome outcome = ExpandPlanner.Plan(5931, 3954, Reel, Flux, 0.21, copy: (w, h));

        outcome.Plan.Should().BeNull();
        outcome.Refusal.Should().Contain("copy");
    }

    [Fact]
    public void A_shrink_below_64_px_is_refused_rather_than_sent()
    {
        ExpandPlanOutcome outcome = ExpandPlanner.Plan(5000, 100, new ExpandTarget.Shape(1, 8), Flux, 1);

        outcome.Plan.Should().BeNull();
        outcome.Refusal.Should().Contain("larger than the AI service can make").And.Contain("4.2 megapixels");
    }

    // ================================================================ placement

    [Fact]
    public void The_result_lands_with_the_originals_pixels_exactly_where_they_were()
    {
        // 5931 px drawn at 0.2102 -> 1246.7 canvas px wide, top-left at (-305, 326)
        ImageExpandGeometry g = new(1247, 2217, 0, 693, 1247, 831);

        (double x, double y, double scale) = ExpandPlanner.PlaceResult(-305.03, 325.88, 0.2101993, 5931, g, 1247, 2217);

        (1247 * scale).Should().BeApproximately(5931 * 0.2101993, 1e-9, "the original's part is as wide as it was drawn");
        x.Should().BeApproximately(-305.03, 1e-9);
        y.Should().BeApproximately(325.88 - (693 * scale), 1e-9);
    }

    [Fact]
    public void A_vendor_that_delivers_a_different_size_is_still_lined_up()
    {
        ImageExpandGeometry g = new(250, 140, 50, 30, 200, 100);

        (double x, double y, double scale) = ExpandPlanner.PlaceResult(100, 200, 2, 200, g, 500, 280);

        scale.Should().Be(1);
        x.Should().Be(0);
        y.Should().Be(140);
    }

    // ================================================================ image headers

    [Fact]
    public void A_jpeg_turned_by_its_exif_orientation_reports_the_size_it_is_shown_at()
    {
        // SOI, APP1 Exif (big-endian TIFF, one IFD0 entry: Orientation = 6), SOF0 4032x3024 (stored landscape)
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xE1, 0x00, 0x22,
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x0B, 0xD0, 0x0F, 0xC0, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
            0xFF, 0xD9
        ];

        ImageHeader.TryReadDimensions(jpeg, out int sw, out int sh).Should().BeTrue();
        (sw, sh).Should().Be((4032, 3024));
        ImageHeader.TryReadDisplaySize(jpeg, out int w, out int h).Should().BeTrue();
        (w, h).Should().Be((3024, 4032), "orientation 6 is turned 90 degrees, as the browser draws it");
    }

    [Fact]
    public void A_header_that_stops_before_the_frame_is_unreadable_not_guessed()
    {
        byte[] truncated = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'];

        ImageHeader.TryReadDisplaySize(truncated, out _, out _).Should().BeFalse();
    }
}
