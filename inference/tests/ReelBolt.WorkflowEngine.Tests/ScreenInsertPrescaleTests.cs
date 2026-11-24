using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Tests.InsertJitter;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// F3 of the insert-tracking-smoothness initiative: the composite prescales content and the white
/// mask to the largest quad of the insert instead of minifying a full-canvas picture ~2:1 through
/// <c>perspective</c>, smears the content's edge instead of multiplying a black rim into the alpha
/// edge, and samples with <c>interpolation=cubic</c>. These tests pin the sizing, the pinning
/// geometry, the filter-string shape (flat, matted and mesh), the 2000-keyframe path, and — against
/// a real ffmpeg — the rendered wobble floor, edge rim and sharpness.
/// </summary>
public class ScreenInsertPrescaleTests
{
    private const int W = 1920, H = 1080;

    private static InsertQuadFrame Rect(long frame, double x, double y, double w, double h) =>
        new(frame, x, y, x + w, y, x, y + h, x + w, y + h);

    private static ResolvedScreenInsert Insert(IReadOnlyList<InsertQuadFrame> keys, InsertPrescale? prescale = null,
        IReadOnlyList<InsertQuadFrame>? plate = null, VideoInsertChromaKey? matte = null, double start = 0, double end = 4) =>
        new("r0", "/scratch/c.mp4", start, end, keys, 24, 1, Matte: matte, ContentKeyframes: plate, Prescale: prescale);

    private static string[] Segments(ResolvedScreenInsert insert, int w = W, int h = H) =>
        ScreenInsertFilterBuilder.BuildFilterChain("[vcut]", [insert], w, h, _ => 1).Split(';');

    // ---------------------------------------------------------------------------------- sizing

    [Fact]
    public void Prescale_size_is_largest_quad_plus_one_clamped()
    {
        // The peak (600 x 340) is mid-series, not at either end; a smaller quad bookends it.
        IReadOnlyList<InsertQuadFrame> keys = [Rect(0, 100, 100, 500, 300), Rect(10, 100, 100, 600, 340), Rect(20, 100, 100, 550, 320)];

        // Largest edge + 1 px, rounded down to even: 601 -> 600 is below the 600 edge? No: ceil(600) + 1 = 601 -> 600 (never under the edge).
        ScreenInsertFilterBuilder.PrescaleSize(keys, W, H).Should().Be((600, 340));

        // A fractional edge is covered whole: ceil(600.2) + 1 = 602.
        ScreenInsertFilterBuilder.PrescaleSize([Rect(0, 0, 0, 600.2, 340.2)], W, H).Should().Be((602, 342));

        // Clamped to the canvas's inner size (the 1 px black mask border stays): a quad bigger than the canvas.
        ScreenInsertFilterBuilder.PrescaleSize([Rect(0, -50, -50, 4000, 2500)], W, H).Should().Be((W - 2, H - 2));
        ScreenInsertFilterBuilder.PrescaleSize([Rect(0, 0, 0, W - 2, H - 2)], W, H).Should().Be((W - 2, H - 2));

        // Tiny plates keep a mask edge to resolve.
        ScreenInsertFilterBuilder.PrescaleSize([Rect(0, 10, 10, 3, 2)], W, H).Should().Be((ScreenInsertFilterBuilder.MinPrescalePx, ScreenInsertFilterBuilder.MinPrescalePx));

        // The longest of the four edges counts, not one of them: a trapezoid wider at the bottom.
        var trapezoid = new InsertQuadFrame(0, 300, 100, 700, 100, 200, 500, 800, 500);
        ScreenInsertFilterBuilder.PrescaleSize([trapezoid], W, H).W.Should().Be(600);
    }

    [Fact]
    public void Prescale_never_exceeds_the_canvas_on_a_small_canvas()
    {
        ScreenInsertFilterBuilder.PrescaleSize([Rect(0, 0, 0, 5000, 5000)], 640, 360).Should().Be((638, 358));
        ScreenInsertFilterBuilder.PrescaleSize([Rect(0, 0, 0, 100, 60)], 6, 6).Should().Be((4, 4), "even a canvas smaller than the minimum prescale never gets a region larger than itself");
    }

    // ---------------------------------------------------------------------------------- pinning

    [Fact]
    public void The_pinned_region_lands_exactly_on_the_tracked_quad_including_a_foreshortened_one()
    {
        var keys = new[] { new InsertQuadFrame(0, 520, 210, 1180, 230, 505, 760, 1210, 800) };
        ScreenInsertFilterBuilder.PrescaleRegion region = ScreenInsertFilterBuilder.RegionFor(ScreenInsertFilterBuilder.PrescaleSize(keys, W, H), W, H);

        IReadOnlyList<InsertQuadFrame>? pinned = ScreenInsertFilterBuilder.PinThroughRegion(keys, region, W, H);

        pinned.Should().NotBeNull();
        InsertQuadFrame c = pinned![0];
        // Map the REGION's four corners through the homography that takes the canvas corners to the pinned points: they must be the quad.
        var h = InsertSurfaceMesh.UnitSquareHomography.Solve((c.X0, c.Y0), (c.X1, c.Y1), (c.X2, c.Y2), (c.X3, c.Y3))!;
        (double X, double Y) P(double fx, double fy) => h.Map(fx / W, fy / H);
        void Near((double X, double Y) got, double x, double y)
        {
            got.X.Should().BeApproximately(x, 1e-6);
            got.Y.Should().BeApproximately(y, 1e-6);
        }

        Near(P(region.OffsetX, region.OffsetY), 520, 210);
        Near(P(region.OffsetX + region.Width, region.OffsetY), 1180, 230);
        Near(P(region.OffsetX, region.OffsetY + region.Height), 505, 760);
        Near(P(region.OffsetX + region.Width, region.OffsetY + region.Height), 1210, 800);
    }

    [Fact]
    public void A_quad_whose_horizon_lies_inside_the_canvas_extrapolation_keeps_the_original_full_canvas_recipe()
    {
        // A tiny, hugely foreshortened plate: the far edge is a twenty-sixth of the near edge, so the projective map's
        // horizon falls between the plate and the corners of even the tight warp window and extrapolating out to them would fold over.
        var wedge = new InsertQuadFrame(0, 900, 500, 920, 500, 700, 560, 1230, 560);
        ScreenInsertFilterBuilder.PinThroughRegion([wedge], ScreenInsertFilterBuilder.RegionFor(ScreenInsertFilterBuilder.PrescaleSize([wedge], W, H), W, H), W, H)
            .Should().BeNull();

        string[] segments = Segments(Insert([wedge]));
        segments[0].Should().StartWith("[1:v]scale=1918:1078:flags=bilinear,fps=24/1,format=yuv444p,pad=1920:1080:1:1:black,perspective=")
            .And.NotContain("fillborders").And.NotContain("interpolation");
        segments.Last().Should().Contain("overlay=x=0:y=0:", "the original recipe warps the whole canvas and lays it at the origin");
    }

    // ---------------------------------------------------------------------------------- warp window

    [Fact]
    public void Warp_window_is_the_union_bounding_box_of_every_quad_plus_a_margin_on_an_even_integer_origin()
    {
        // A fractional track that wanders: the union of all quads is 311.25..1288.75 x 197.5..812.1.
        IReadOnlyList<InsertQuadFrame> keys =
        [
            new(0, 400.5, 200.25, 800.5, 197.5, 401.25, 600.75, 799.5, 604.0),
            new(10, 520.0, 260.0, 1288.75, 262.0, 511.0, 812.1, 1280.0, 805.0),
            new(20, 311.25, 240.0, 700.0, 241.0, 315.0, 700.0, 702.0, 702.0)
        ];
        InsertBounds? bounds = ScreenInsertFilterBuilder.QuadBounds(keys);
        bounds.Should().Be(new InsertBounds(311, 197, 1289, 813), "floor of the minima, ceiling of the maxima");

        ScreenInsertFilterBuilder.PrescaleRegion window = ScreenInsertFilterBuilder.WarpRegionFor(bounds, 10, 10, W, H);
        (window.OffsetX % 2).Should().Be(0);
        (window.OffsetY % 2).Should().Be(0);
        window.OffsetX.Should().BeLessThanOrEqualTo(311 - ScreenInsertFilterBuilder.WarpMarginPx);
        window.OffsetY.Should().BeLessThanOrEqualTo(197 - ScreenInsertFilterBuilder.WarpMarginPx);
        (window.OffsetX + window.Width).Should().BeGreaterThanOrEqualTo(1289 + ScreenInsertFilterBuilder.WarpMarginPx);
        (window.OffsetY + window.Height).Should().BeGreaterThanOrEqualTo(813 + ScreenInsertFilterBuilder.WarpMarginPx);
        window.Width.Should().BeLessThan(W, "a plate that never fills the frame is not warped over the whole frame");
    }

    [Fact]
    public void Warp_window_grows_to_hold_the_prescaled_pictures_and_stays_inside_the_canvas()
    {
        // A thin diagonal-ish sliver: its bounding box is smaller than the prescaled picture it must hold.
        ScreenInsertFilterBuilder.PrescaleRegion grown = ScreenInsertFilterBuilder.WarpRegionFor(new InsertBounds(900, 500, 1000, 520), 300, 200, W, H);
        grown.Width.Should().BeGreaterThanOrEqualTo(300);
        grown.Height.Should().BeGreaterThanOrEqualTo(200);
        grown.OffsetX.Should().BeLessThanOrEqualTo(900 - 4 + 0, "grown about the centre of the bounds, not pinned to a corner");
        (grown.OffsetX + grown.Width).Should().BeLessThanOrEqualTo(W);

        // A quad hanging off the canvas is clamped to it; one covering everything is the whole canvas.
        ScreenInsertFilterBuilder.PrescaleRegion clamped = ScreenInsertFilterBuilder.WarpRegionFor(new InsertBounds(-300, -20, 700, 400), 10, 10, W, H);
        clamped.OffsetX.Should().Be(0);
        clamped.OffsetY.Should().Be(0);
        ScreenInsertFilterBuilder.WarpRegionFor(new InsertBounds(-10, -10, W + 10, H + 10), 10, 10, W, H).Should().Be(new ScreenInsertFilterBuilder.PrescaleRegion(W, H, 0, 0));
        ScreenInsertFilterBuilder.WarpRegionFor(null, 10, 10, W, H).Should().Be(new ScreenInsertFilterBuilder.PrescaleRegion(W, H, 0, 0), "unknown bounds mean the whole canvas");

        ScreenInsertFilterBuilder.QuadBounds([new InsertQuadFrame(0, double.NaN, 0, 1, 0, 0, 1, 1, 1)]).Should().BeNull("a corner that is not finite gives no bounds");
    }

    [Fact]
    public void The_window_pins_the_content_region_onto_the_quad_with_sub_pixel_exact_positions()
    {
        // A fractional, foreshortened quad. Shifting the window by an integer origin must not move it by a fraction of a pixel:
        // the four corners of the content region, mapped through the homography the window corners define, plus the origin, are the quad.
        var quad = new InsertQuadFrame(0, 520.37, 210.61, 1180.25, 230.5, 505.75, 760.125, 1210.9, 800.33);
        ScreenInsertFilterBuilder.PrescalePlan plan = ScreenInsertFilterBuilder.PlanPrescale(Insert([quad]), W, H)!;
        InsertQuadFrame c = plan.ContentKeys[0];
        var h = InsertSurfaceMesh.UnitSquareHomography.Solve((c.X0, c.Y0), (c.X1, c.Y1), (c.X2, c.Y2), (c.X3, c.Y3))!;
        double ww = plan.Warp.Width, wh = plan.Warp.Height;
        (double X, double Y) P(double fx, double fy)
        {
            (double x, double y) = h.Map(fx / ww, fy / wh);
            return (x + plan.Warp.OffsetX, y + plan.Warp.OffsetY);
        }

        void Near((double X, double Y) got, double x, double y)
        {
            got.X.Should().BeApproximately(x, 1e-6);
            got.Y.Should().BeApproximately(y, 1e-6);
        }

        ScreenInsertFilterBuilder.PrescaleRegion r = plan.Content;
        Near(P(r.OffsetX, r.OffsetY), quad.X0, quad.Y0);
        Near(P(r.OffsetX + r.Width, r.OffsetY), quad.X1, quad.Y1);
        Near(P(r.OffsetX, r.OffsetY + r.Height), quad.X2, quad.Y2);
        Near(P(r.OffsetX + r.Width, r.OffsetY + r.Height), quad.X3, quad.Y3);
        plan.Warp.Width.Should().BeLessThan(W / 2, "the window is the quad's own bounding box, not the frame");
    }

    [Fact]
    public void A_window_covering_the_canvas_overlays_at_the_origin_and_a_smaller_one_overlays_at_its_own_integer_origin()
    {
        string[] full = Segments(Insert([Rect(0, -5, -5, W + 10, H + 10)]));
        full.Last().Should().Contain("overlay=x=0:y=0:");
        full[0].Should().Contain($"pad={W}:{H}:");

        string[] small = Segments(Insert([Rect(0, 500, 300, 700, 400)]));
        small.Last().Should().MatchRegex(@"overlay=x=\d*[02468]:y=\d*[02468]:", "an even integer origin keeps the 4:2:0 chroma grid aligned");
        small.Last().Should().NotContain("overlay=x=0:y=0:");
    }

    [Fact]
    public void A_matted_insert_crops_the_whole_frame_matte_to_the_warp_window_before_multiplying_it_into_the_mask()
    {
        var matte = new VideoInsertChromaKey(0, 232, 0, 0.30, 0.10, 0.9);
        string[] segments = Segments(Insert([Rect(0, 380, 180, 440, 440)], plate: [Rect(0, 400, 200, 400, 400)], matte: matte), 1280, 720);

        // Window: union of the expanded quad (380..820 x 180..620) + 4 px, on an even origin.
        string crop = segments.Single(s => s.Contains("crop=w=", StringComparison.Ordinal) && s.EndsWith("[mtw0]", StringComparison.Ordinal));
        crop.Should().StartWith("[mta0]crop=w=448:h=448:x=376:y=176:exact=1[mtw0]");
        segments.Should().Contain("[sim0][mtw0]blend=all_mode=multiply[simm0]");
        segments.Single(s => s.StartsWith("[mtbase][sia0]overlay", StringComparison.Ordinal) || s.Contains("[sia0]overlay=", StringComparison.Ordinal))
            .Should().Contain("overlay=x=376:y=176:");
    }

    // ---------------------------------------------------------------------------------- flat path

    [Fact]
    public void Content_branch_has_no_black_pad_rim()
    {
        string[] segments = Segments(Insert([Rect(0, 500, 300, 700, 400), Rect(30, 560, 300, 700, 400)]));

        string content = segments[0];
        content.Should().Contain("fillborders=").And.Contain("mode=smear");
        content.Should().NotContain(":1:1:black", "a black rim is multiplied into the alpha edge by alphamerge");
        // 701 -> 700 x 400 region, centred in the 768 x 408 warp window (the union of the quads, 496..1264 x 296..704): the pad places it, the smear fills everything around it.
        content.Should().StartWith("[1:v]scale=700:400:flags=bilinear,fps=24/1,format=yuv444p,pad=768:408:34:4:black,fillborders=left=34:right=34:top=4:bottom=4:mode=smear,perspective=");
        content.Should().EndWith("setpts=PTS-STARTPTS+0/TB[sic0]");

        // The MASK keeps its black pad: outside the region it must be zero, which is what makes everything beyond the quad transparent.
        segments[1].Should().StartWith("color=c=white:size=700x400:rate=24/1:duration=5,format=gray,pad=768:408:34:4:black,perspective=")
            .And.NotContain("fillborders");
    }

    [Fact]
    public void Content_and_mask_share_exactly_the_same_corner_expressions_but_only_the_content_samples_cubic()
    {
        string[] segments = Segments(Insert([Rect(0, 500, 300, 700, 400), Rect(30, 560, 310, 704, 402)]));

        string Perspective(string s) => s[s.IndexOf("perspective=", StringComparison.Ordinal)..s.IndexOf(",setpts", StringComparison.Ordinal)];
        Perspective(segments[0]).Should().EndWith(":sense=destination:eval=frame:interpolation=cubic");
        Perspective(segments[1]).Should().EndWith(":sense=destination:eval=frame", "the mask's edge decides the quad: cubic there measured as wobble, not sharpness");
        Perspective(segments[0]).Replace(ScreenInsertFilterBuilder.ContentInterpolation, string.Empty).Should().Be(Perspective(segments[1]),
            "a different corner series would warp the mask differently from the content and leak the plate at the edges");
    }

    [Fact]
    public void Perspective_in_stays_one_based_so_keyframe_zero_is_written_at_in_one()
    {
        string chain = string.Join(";", Segments(Insert([Rect(0, 500, 300, 700, 400), Rect(60, 560, 300, 700, 400)])));

        chain.Should().Contain("if(lt(in,61),", "keyframe 60 sits at in = 61: PerspectiveFirstFrame is still 1");
    }

    [Fact]
    public void A_supplied_prescale_measured_over_every_keyframe_overrides_the_thinned_series_the_builder_sees()
    {
        // The kept keyframes are all 700 wide; the real track peaked at 760 between two of them.
        IReadOnlyList<InsertQuadFrame> kept = [Rect(0, 500, 300, 700, 400), Rect(60, 500, 300, 700, 400)];
        Segments(Insert(kept))[0].Should().Contain("scale=700:400:");
        // The caller also measured the union of the quads over every keyframe (460..1220 x 290..710): that, not the kept keyframes, sizes the warp window.
        var measured = new InsertPrescale(760, 420, 760, 420, new InsertBounds(460, 290, 1220, 710));
        Segments(Insert(kept, measured))[0].Should().Contain("scale=760:420:").And.Contain("pad=768:428:4:4:black");
        Segments(Insert(kept, measured))[1].Should().Contain("size=760x420");
        string.Join(";", Segments(Insert(kept, measured))).Should().Contain("overlay=x=456:y=286:");
    }

    [Fact]
    public void A_matted_insert_pins_its_content_to_the_plate_and_its_mask_to_the_expanded_quad_each_at_its_own_size()
    {
        var plate = new[] { Rect(0, 400, 200, 400, 400) };
        var expanded = new[] { Rect(0, 380, 180, 440, 440) };
        string[] segments = Segments(Insert(expanded, plate: plate, matte: new VideoInsertChromaKey(0, 232, 0, 0.30, 0.10, 0.9)), 1280, 720);

        string content = segments.Single(s => s.StartsWith("[1:v]", StringComparison.Ordinal));
        content.Should().Contain("scale=400:400:").And.Contain("fillborders=").And.NotContain(":1:1:black");
        string mask = segments.Single(s => s.StartsWith("color=c=white", StringComparison.Ordinal) && s.EndsWith("[sim0]", StringComparison.Ordinal));
        mask.Should().Contain("size=440x440").And.NotContain("fillborders");
    }

    [Fact]
    public void The_matted_expanded_mask_still_tpads_from_zero_and_perspective_still_runs_before_the_pad()
    {
        string chain = string.Join(";", Segments(Insert([Rect(0, 380, 180, 440, 440)], plate: [Rect(0, 400, 200, 400, 400)],
            matte: new VideoInsertChromaKey(0, 232, 0, 0.30, 0.10, 0.9), start: 3, end: 6), 1280, 720));

        chain.Should().NotContain("PTS-STARTPTS+3/TB");
        foreach (string branch in chain.Split(';').Where(b => b.EndsWith("[sic0]", StringComparison.Ordinal) || b.EndsWith("[sim0]", StringComparison.Ordinal)))
        {
            branch.Should().Contain("tpad=start=72");
            branch.LastIndexOf("perspective=", StringComparison.Ordinal).Should().BeLessThan(branch.IndexOf("tpad=", StringComparison.Ordinal));
        }
    }

    // ---------------------------------------------------------------------------------- 2000 keyframes

    [Fact]
    public void Long_series_of_2000_keyframes_still_builds()
    {
        // 2000 keyframes (an 80 s insert at 25 fps), growing: the peak is the LAST keyframe.
        InsertQuadFrame[] keys = Enumerable.Range(0, 2000)
            .Select(i => Rect(i, 300 + i * 0.1, 200 + Math.Sin(i / 9.0) * 3, 600 + i * 0.1, 340 + i * 0.05)).ToArray();

        string[] segments = Segments(Insert(keys, end: 80));

        // Sized from the peak (799.9 x 439.95), not from a thinned sample.
        segments[0].Should().Contain("scale=800:440:");
        // Past 96 keyframes the expression is the balanced tree (log depth), not a 2000-deep chain.
        int depth = 0, max = 0;
        foreach (char ch in segments[0])
        {
            depth += ch == '(' ? 1 : ch == ')' ? -1 : 0;
            max = Math.Max(max, depth);
        }

        depth.Should().Be(0);
        max.Should().BeLessThan(40);
        segments[0].Should().Contain("interpolation=cubic");
        segments[1].Should().NotContain("interpolation");
    }

    [Fact]
    public void The_executor_sizes_the_prescale_over_every_keyframe_before_the_expression_cap_thins_them()
    {
        const int n = 2000, cap = 1500;
        const double fps = 25;
        // Width ramps up then spikes at one keyframe that uniform downsampling to `cap` drops.
        int spike = 1001;
        var media = new VideoAnalysisMedia(n / fps, 25, 1, W, H);
        var keyframes = Enumerable.Range(0, n).Select(i =>
        {
            double w = i == spike ? 0.45 : 0.30;
            return new VideoInsertQuadKeyframe(i / fps, 0.2, 0.2, 0.2 + w, 0.2, 0.2, 0.6, 0.2 + w, 0.6);
        }).ToList();
        var track = new VideoInsertRegionTrack("r0", null, 0, n / fps, keyframes, 1.0, 0.3, 1.7, "Static", "green");
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan> { new(0, n / fps, 0, n / fps, 0, n) };
        OutputTimeline timeline = OutputTimeline.Build(spans, VideoCompileStepExecutor.BuildHardCutSeamsForFallback(spans.Count));
        (double Start, double End) window = timeline.MapWindowToOutput(0, n / fps, 0)!.Value;
        var fit = VideoCompileStepExecutor.SourceCanvasFit.Identity(media);

        IReadOnlyList<InsertQuadFrame> thinned = VideoCompileStepExecutor.BuildInsertKeyframes(track, timeline, media, window, cap, 0, fit);
        thinned.Should().HaveCount(cap);
        thinned.Max(k => k.X1 - k.X0).Should().BeApproximately(0.30 * W, 0.01, "the spike keyframe was dropped by the uniform downsample");

        InsertPrescale prescale = VideoCompileStepExecutor.ComputeInsertPrescale(track, timeline, media, window, 0, smearOverscan: false, fit);

        prescale.MaskWidth.Should().Be(ScreenInsertFilterBuilder.PrescaleSize([Rect(0, 0, 0, 0.45 * W, 0.4 * H)], W, H).W);
        prescale.MaskWidth.Should().BeGreaterThan(864 - 2, "sized from the 0.45 x 1920 = 864 px spike");
        prescale.ContentWidth.Should().Be(prescale.MaskWidth);

        // The warp window is bounded the same way: the spike's right edge (0.2 + 0.45 = 0.65 x 1920 = 1248) is inside the measured bounds
        // even though the kept keyframes never reach it.
        prescale.Bounds.Should().NotBeNull();
        prescale.Bounds!.Value.X1.Should().BeGreaterThanOrEqualTo((int)Math.Ceiling(0.65 * W));
        thinned.Max(k => k.X1).Should().BeLessThan(0.65 * W - 1, "the kept keyframes alone would have left the window too narrow");
    }

    // ---------------------------------------------------------------------------------- mesh cells

    [Fact]
    public void Mesh_cells_smear_the_content_edge_instead_of_a_black_rim_keep_the_mask_border_and_sample_the_content_cubic()
    {
        InsertMeshPlan mesh = new(1, 2,
        [
            new InsertMeshFrame(0,
            [
                [(500.0, 200.0), (800.0, 210.0), (1100.0, 200.0)],
                [(500.0, 700.0), (800.0, 690.0), (1100.0, 700.0)]
            ])
        ]);
        var insert = new ResolvedScreenInsert("r0", "/scratch/c.mp4", 1, 4, [Rect(0, 500, 200, 600, 500)], 24, 1, Mesh: mesh);

        var segments = new List<string>();
        ScreenInsertMeshFilterBuilder.AppendInsert(segments, "[vcut]", insert, mesh, 0, 1, "[vout]");

        foreach (string cell in segments.Where(s => s.StartsWith("[mi0c", StringComparison.Ordinal)))
        {
            cell.Should().Contain("pad=").And.Contain(":1:1:black,fillborders=left=1:right=1:top=1:bottom=1:mode=smear,perspective=");
            cell.Should().Contain(":sense=destination:eval=frame:interpolation=cubic");
        }

        foreach (string mask in segments.Where(s => s.StartsWith("color=c=white", StringComparison.Ordinal)))
        {
            mask.Should().Contain(":1:1:black,perspective=").And.NotContain("fillborders").And.NotContain("interpolation");
        }
    }

    // ---------------------------------------------------------------------------------- real ffmpeg

    private static RigInput SceneInput(string scene, int frames, string content = "0xFFFFFF")
    {
        JitterScene s = JitterScenes.Get(scene);
        CompileStage.Result compiled = RenderScenes.Compile(s, RenderScenes.PerfectTrack(s));
        RigInput full = RenderScenes.Input(s, compiled, content, flatBase: true);
        return full with { Frames = frames, Keyframes = full.Keyframes.Take(frames).ToList(), Commanded = full.Commanded.Take(frames).ToList() };
    }

    /// <summary>SlipRms (source px) of the rendered quad against the truth pushed through the same overscan, perfect track, flat base, integrating meter.</summary>
    private static double RenderSlip(string scene, int frames, RigVariant variant)
    {
        JitterScene s = JitterScenes.Get(scene);
        CompileStage.Result compiled = RenderScenes.Compile(s, RenderScenes.PerfectTrack(s));
        RigInput input = SceneInput(scene, frames);
        RigResult r = RenderRig.Run(input, variant, RenderStage.White, runProd: false, runMeter: true);
        r.MeterFailed.Should().Be(0);
        return RenderScenes.Score(r.Meter, compiled.IdealSourcePx.Take(frames).ToList(), s.Fps).SlipRms;
    }

    private static readonly RigVariant Shipped = new("shipped", "shipped F3 chain", Production: true);
    private static readonly RigVariant PreF3 = new("base", "pre-F3 chain");

    [FfmpegFact]
    public void Perfect_track_render_slip_on_slow_push_is_below_0_15()
    {
        double slip = RenderSlip("slow-push", 48, Shipped);
        double baseline = RenderSlip("slow-push", 48, PreF3);

        slip.Should().BeLessThan(0.15, "AC2: the perfect-track render floor on slow-push");
        slip.Should().BeLessThan(baseline * 0.5, $"the pre-F3 chain measured {baseline:0.000} on the same 48 frames");
    }

    [FfmpegFact]
    public void Perfect_track_render_slip_on_a_ten_second_slow_push_is_below_0_15()
    {
        // The whole 240-frame push (every sub-pixel phase the plate drifts through); measured 0.0935 shipped vs 0.438 before F3.
        RenderSlip("slow-push", 240, Shipped).Should().BeLessThan(0.15);
    }

    [FfmpegFact]
    public void Warping_only_the_bounding_window_renders_the_same_pixels_as_warping_the_whole_canvas()
    {
        // The window is an optimisation, not a change of picture: shifting the corners by the window's integer origin must leave every
        // sub-pixel position, and so every pixel, where the full-canvas warp (the T-010 shape) put it. testsrc2 content, so any sampling
        // difference shows; a slow push, so many sub-pixel phases.
        JitterScene s = JitterScenes.Get("slow-push");
        CompileStage.Result compiled = RenderScenes.Compile(s, RenderScenes.PerfectTrack(s));
        RigInput full = RenderScenes.Input(s, compiled, "testsrc2=s=1344x768:r=24:d=12", flatBase: true);
        const int frames = 24;
        RigInput input = full with { Frames = frames, Keyframes = full.Keyframes.Take(frames).ToList(), Commanded = full.Commanded.Take(frames).ToList() };

        List<byte[]> windowed = RenderRig.RenderRaw(input, RegionCostBench.RegionCubic);
        List<byte[]> wholeCanvas = RenderRig.RenderRaw(input, RegionCostBench.T010(input.W, input.H));

        windowed.Should().HaveCount(frames);
        wholeCanvas.Should().HaveCount(frames);
        long differing = 0, total = 0;
        int worst = 0;
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < windowed[f].Length; i++)
            {
                int d = Math.Abs(windowed[f][i] - wholeCanvas[f][i]);
                worst = Math.Max(worst, d);
                differing += d > 0 ? 1 : 0;
                total++;
            }
        }

        worst.Should().BeLessThanOrEqualTo(2, $"{differing} of {total} bytes differ; the window changes nothing but the area `perspective` works on");
        (differing / (double)total).Should().BeLessThan(0.001);
    }

    [FfmpegFact]
    public void Perfect_track_render_slip_on_microshake_and_a_static_plate_stays_at_the_floor()
    {
        RenderSlip("microshake", 48, Shipped).Should().BeLessThan(0.02, "measured 0.003; 0.485 before F3");
        RenderSlip("static-1344", 24, Shipped).Should().BeLessThan(0.005);
    }

    [FfmpegFact]
    public void Edge_rim_is_flat_and_sharpness_holds_at_a_half_pixel_offset()
    {
        // testsrc2 pinned into a 700x420 quad at integer and at half-pixel positions, against the same content lanczos-resized and pasted.
        const int w = 1344, h = 768;
        float[] ideal = Luma(IdealFrame(w, h));
        double idealGradient = Gradient(ideal, w, 308, 108, 992, 512);

        // Integer-aligned: the outermost 2 px just inside the quad must not be darker than the content by more than 2 levels (T-005: -15.7 before F3).
        float[] aligned = Luma(RenderOne(Quad(300, 100)));
        RimMean(aligned, ideal, w).Should().BeInRange(-2.0, 2.0, "AC2: edge rim |signed mean| <= 2 luma levels (the black pad measured -15.7)");

        // Half-pixel: sharpness as a ratio to the lanczos ideal's.
        float[] half = Luma(RenderOne(Quad(300.5, 100.5)));
        (Gradient(half, w, 309, 109, 991, 511) / idealGradient).Should().BeGreaterThanOrEqualTo(0.88, "AC2 / D3: sharpness ratio at a half-pixel offset");
    }

    [FfmpegFact]
    public void A_curved_mesh_composites_with_no_hairline_at_the_cell_seams_and_no_dark_or_light_rim_at_the_outer_edge()
    {
        // A 2 x 3 mesh with a 12 px bow, white content over a black base: every pixel well inside the plate, the pixels straddling the
        // interior seams included, must be full white; the pixels just outside the outer boundary must still be the base.
        const int w = 640, h = 480;
        (double X, double Y)[][] grid = new (double, double)[3][];
        for (int i = 0; i <= 2; i++)
        {
            grid[i] = new (double, double)[4];
            for (int j = 0; j <= 3; j++)
            {
                double u = j / 3.0, v = i / 2.0;
                grid[i][j] = (200 + u * 240, 120 + v * 240 + 12 * 4 * u * (1 - u) * (1 - 2 * v) * -1);
            }
        }

        var mesh = new InsertMeshPlan(2, 3, [new InsertMeshFrame(0, grid)]);
        var insert = new ResolvedScreenInsert("r0", "c", 0, 1, [Rect(0, 200, 120, 240, 240)], 24, 1, Mesh: mesh);
        string chain = ScreenInsertFilterBuilder.BuildFilterChain("[0:v]", [insert], w, h, _ => 1, "[o]") + ";[o]format=rgb24[f]";

        byte[] frame = FfmpegSolid(chain, w, h);
        int Luma(int x, int y) => frame[(y * w + x) * 3 + 1];

        // Seams at x = 280 and 360 (column boundaries), y = 240 (row boundary): sample a band across each, away from the bowed top and bottom.
        foreach (int x in new[] { 278, 279, 280, 281, 282, 358, 359, 360, 361, 362 })
            for (int y = 150; y < 330; y += 5)
                Luma(x, y).Should().BeGreaterThanOrEqualTo(250, $"a seam hairline at x={x}, y={y} would let the black base through");
        for (int y = 238; y <= 242; y++)
            for (int x = 215; x < 425; x += 5)
                Luma(x, y).Should().BeGreaterThanOrEqualTo(250, $"a seam hairline at y={y}, x={x}");

        // Outer edge, middle of the left side (u = 0 is straight): one pixel inside is content, two outside is the base.
        Luma(202, 240).Should().BeGreaterThanOrEqualTo(250);
        Luma(197, 240).Should().BeLessThanOrEqualTo(5);
    }

    [FfmpegFact]
    public void A_matted_flat_insert_with_smeared_overscan_still_fills_the_plate_and_leaves_the_scenery_alone()
    {
        // Grey scenery, a green plate at (200,100)-(440,260), a 10% overscanned warp target and the content pinned to the plate itself,
        // rendered through the executor-style Prescale: the plate must be wholly the content's colour (no green rim), the scenery untouched.
        const int w = 480, h = 360;
        var basePixels = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool plate = x is >= 200 and < 440 && y is >= 100 and < 260;
                int i = (y * w + x) * 3;
                (basePixels[i], basePixels[i + 1], basePixels[i + 2]) = plate ? ((byte)0, (byte)200, (byte)0) : ((byte)110, (byte)110, (byte)110);
            }
        }

        InsertQuadFrame plateQuad = Rect(0, 200, 100, 240, 160);
        InsertQuadFrame expanded = Rect(0, 188, 92, 264, 176);
        var key = new VideoInsertChromaKey(0, 200, 0, 0.3, 0, 0.95, PlateDominance: 200);
        var insert = new ResolvedScreenInsert("r0", "c", 0, 1, [expanded], 24, 1, Matte: key, ContentKeyframes: [plateQuad]);
        string chain = ScreenInsertFilterBuilder.BuildFilterChain("[0:v]", [insert], w, h, _ => 1, "[o]") + ";[o]format=rgb24[f]";

        byte[] frame = FfmpegWithBase(basePixels, w, h, chain);
        (byte R, byte G, byte B) Px(int x, int y) => (frame[(y * w + x) * 3], frame[(y * w + x) * 3 + 1], frame[(y * w + x) * 3 + 2]);

        foreach ((int x, int y) in new[] { (200, 100), (201, 101), (439, 100), (200, 259), (439, 259), (320, 180), (200, 180), (439, 180) })
        {
            Px(x, y).R.Should().BeGreaterThan(240, $"plate pixel ({x},{y}) carries the red content");
            Px(x, y).G.Should().BeLessThan(20, $"plate pixel ({x},{y}) shows no green");
        }

        // Scenery beside and above the plate: the overscan band is clipped to the plate by the matte (grey 110, +-2 for the 8-bit round trip).
        foreach ((int x, int y) in new[] { (190, 180), (320, 95), (450, 180), (320, 265) })
            Px(x, y).R.Should().BeInRange(108, 112, $"scenery ({x},{y}) is not touched by the overscan band");
    }

    private static byte[] FfmpegSolid(string chain, int w, int h) => FfmpegRun(
        ["-f", "lavfi", "-i", $"color=c=black:s={w}x{h}:r=24:d=1", "-f", "lavfi", "-i", "color=c=white:s=320x240:r=24:d=1"], chain, w * h * 3);

    private static byte[] FfmpegWithBase(byte[] rgb, int w, int h, string chain)
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "insert-prescale-base-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string input = System.IO.Path.Combine(dir, "base.rgb");
            System.IO.File.WriteAllBytes(input, rgb);
            return FfmpegRun(
                ["-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{w}x{h}", "-r", "24", "-i", input, "-f", "lavfi", "-i", "color=c=red:s=320x240:r=24:d=1"], chain, w * h * 3);
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] FfmpegRun(string[] inputs, string graph, int frameBytes)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-nostdin" }.Concat(inputs).Concat(["-filter_complex", graph, "-map", "[f]", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]))
            psi.ArgumentList.Add(a);
        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        using var ms = new System.IO.MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(err.Result);
        ms.Length.Should().Be(frameBytes);
        return ms.ToArray();
    }

    private static InsertQuadFrame Quad(double x, double y) => Rect(0, x, y, 700, 420);

    private static byte[] RenderOne(InsertQuadFrame k)
    {
        var quad = new Quad(0, k.X0, k.Y0, k.X1, k.Y1, k.X2, k.Y2, k.X3, k.Y3);
        var input = new RigInput(1344, 768, 24, 1, 1, [k], [quad], "testsrc2=s=1344x768:r=24:d=3", null, "0x404040");
        return RenderRig.RenderRaw(input, Shipped)[0];
    }

    private static byte[] IdealFrame(int w, int h)
    {
        string graph = $"color=c=0x404040:s={w}x{h}:r=24:d=1[b];testsrc2=s={w}x{h}:r=24:d=1,scale=700:420:flags=lanczos[c];[b][c]overlay=x=300:y=100,format=rgb24[o]";
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-nostdin", "-filter_complex", graph, "-map", "[o]", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" })
            psi.ArgumentList.Add(a);
        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        using var ms = new System.IO.MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        return p.ExitCode == 0 ? ms.ToArray() : throw new InvalidOperationException(err.Result);
    }

    private static float[] Luma(byte[] rgb)
    {
        var l = new float[rgb.Length / 3];
        for (int i = 0, o = 0; i < l.Length; i++, o += 3)
            l[i] = (float)(0.2126 * rgb[o] + 0.7152 * rgb[o + 1] + 0.0722 * rgb[o + 2]);
        return l;
    }

    private static double Gradient(float[] l, int w, int x0, int y0, int x1, int y1)
    {
        double sum = 0;
        int n = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                double gx = (l[y * w + x + 1] - l[y * w + x - 1]) / 2.0, gy = (l[(y + 1) * w + x] - l[(y - 1) * w + x]) / 2.0;
                sum += Math.Sqrt(gx * gx + gy * gy);
                n++;
            }
        }

        return sum / n;
    }

    /// <summary>Mean signed luma error (candidate minus ideal) over the outermost two pixels just inside the quad (300,100)-(1000,520).</summary>
    private static double RimMean(float[] l, float[] ideal, int w)
    {
        double sum = 0;
        int m = 0;
        for (int k = 0; k < 2; k++)
        {
            for (int x = 320; x < 980; x++)
            {
                foreach (int y in new[] { 100 + k, 519 - k })
                {
                    sum += l[y * w + x] - ideal[y * w + x];
                    m++;
                }
            }

            for (int y = 120; y < 500; y++)
            {
                foreach (int x in new[] { 300 + k, 999 - k })
                {
                    sum += l[y * w + x] - ideal[y * w + x];
                    m++;
                }
            }
        }

        return sum / m;
    }
}
