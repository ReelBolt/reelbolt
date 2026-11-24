using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Golden-graph tests for <see cref="RegionEffectFilterBuilder"/> — the "model supplies masks only"
/// half of the region-effects contract (plan §5): an enum picks a constant graph, every number is
/// clamped here and formatted invaluantly, and no user text ever reaches an argv.
/// </summary>
public class RegionEffectFilterBuilderTests
{
    private const int Width = 1920;
    private const int Height = 1080;

    private static string Chain(RegionEffect effect, double strength = 1.0, int slot = 0) =>
        RegionEffectFilterBuilder.BuildEffectChain(
            "[base]", "[m]", "[out]", slot, effect, Width, Height, strength, "24/1", 12.5);

    [Fact]
    public void Blur_uses_one_and_a_half_percent_of_the_short_side_and_composites_through_alpha()
    {
        string graph = Chain(RegionEffect.Blur);

        // 1080 * 0.015 = 16.2 px of sigma at strength 1.
        graph.Should().Contain("gblur=sigma=16.2");
        graph.Should().Contain("[base]split=2[ree0b][ree0e]");
        graph.Should().Contain("[ree0a][m]alphamerge[ree0f]");
        graph.Should().Contain("[ree0b][ree0f]overlay=x=0:y=0:format=yuv420[out]");
        graph.Should().NotContain("maskedmerge");
    }

    [Fact]
    public void Pixelate_uses_two_percent_of_the_short_side_as_the_block()
    {
        string graph = Chain(RegionEffect.Pixelate);

        // 1080 * 0.02 = 21.6 -> 22 px blocks at strength 1.
        graph.Should().Contain("pixelize=w=22:h=22:mode=avg");
    }

    [Fact]
    public void Highlight_brightens_inside_the_mask_and_dims_outside_it()
    {
        string graph = Chain(RegionEffect.Highlight);

        graph.Should().Contain("eq=brightness=0.1:contrast=1.1");
        graph.Should().Contain("eq=brightness=-0.15");
        // Two composites: the brightened object, then the dimmed rest through the negated mask.
        graph.Split("alphamerge").Length.Should().Be(3);
        graph.Should().Contain("[ree0mo]negate[ree0mX]");
    }

    [Fact]
    public void Spotlight_dims_only_the_complement_of_the_mask()
    {
        string graph = Chain(RegionEffect.Spotlight);

        graph.Should().Contain("eq=brightness=-0.6");
        graph.Should().Contain("[m]negate[ree0mX]");
        graph.Should().Contain("[ree0s2][ree0mX]alphamerge[ree0s3]");
    }

    [Fact]
    public void ColorPop_desaturates_the_complement_and_leaves_the_object_in_colour()
    {
        string graph = Chain(RegionEffect.ColorPop);

        graph.Should().Contain("hue=s=0");
        graph.Should().Contain("[m]negate[ree0mX]");
        graph.Should().Contain("[ree0c2][ree0mX]alphamerge[ree0c3]");
    }

    [Fact]
    public void Outline_builds_the_band_by_dilating_one_pixel_per_band_pixel()
    {
        string graph = Chain(RegionEffect.Outline);

        // 1080 * 0.004 = 4.32 -> a 4 px band, i.e. exactly four dilation passes.
        graph.Split("dilation=coordinates=255").Length.Should().Be(5);
        graph.Should().Contain("blend=all_mode=multiply");
        graph.Should().Contain("color=c=white:s=1920x1080:r=24/1:d=12.5");
    }

    [Fact]
    public void Effects_are_deterministic_and_use_unique_label_slots()
    {
        Chain(RegionEffect.Blur, slot: 0).Should().Be(Chain(RegionEffect.Blur, slot: 0));

        string first = Chain(RegionEffect.Blur, slot: 0);
        string second = Chain(RegionEffect.Blur, slot: 1);
        first.Should().NotBe(second);
        second.Should().Contain("[ree1b]");
    }

    [Fact]
    public void Numbers_are_invariant_so_a_comma_decimal_culture_cannot_change_the_argv()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            string invariant = Chain(RegionEffect.Spotlight);

            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            string german = Chain(RegionEffect.Spotlight);

            german.Should().Be(invariant);
            german.Should().Contain("brightness=-0.6");
            german.Should().NotContain("0,6");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Strength_scales_each_effects_own_knob_and_is_clamped()
    {
        Chain(RegionEffect.Blur, strength: 0).Should().Contain($"gblur=sigma={FfmpegArgvFormat.Number(RegionEffectFilterBuilder.MinBlurSigma)}");
        // 10 is clamped to the 2.0 ceiling: 1080 * 0.02 * 2 = 43 px blocks, never an unbounded mosaic.
        Chain(RegionEffect.Pixelate, strength: 10).Should().Contain("pixelize=w=43:h=43");
        RegionEffectFilterBuilder.BlurSigma(1080, 2).Should().Be(32.4);
        RegionEffectFilterBuilder.PixelBlock(1080, 2).Should().Be(43);
        RegionEffectFilterBuilder.OutlineBand(1080, 2).Should().Be(9);
        RegionEffectFilterBuilder.OutlineBand(1080, 100).Should().Be(RegionEffectFilterBuilder.MaxOutlineBandPx);
        RegionEffectFilterBuilder.OutlineBand(10, 1).Should().Be(1);
    }

    [Fact]
    public void Mask_helper_chain_applies_invert_then_feather_then_opacity()
    {
        RegionEffectFilterBuilder.BuildMaskHelperChain("[in]", "[out]", invert: true, featherPx: 8, opacity: 0.5)
            .Should().Be("[in]negate,gblur=sigma=8,lut=y='val*0.5'[out]");

        RegionEffectFilterBuilder.BuildMaskHelperChain("[in]", "[out]", invert: false, featherPx: 0, opacity: 1)
            .Should().Be("[in]null[out]");

        RegionEffectFilterBuilder.BuildMaskHelperChain("[in]", "[out]", invert: false, featherPx: 500, opacity: 2)
            .Should().Be("[in]gblur=sigma=64[out]");
    }

    [Fact]
    public void Mask_helper_stages_fan_one_tracks_mask_out_to_every_entry()
    {
        (string filter, IReadOnlyList<string> labels) = RegionEffectFilterBuilder.BuildMaskHelperStages(
            "[rmk0]",
            new[]
            {
                new RegionMaskHelper(Invert: false, FeatherPx: 0, Opacity: 1),
                new RegionMaskHelper(Invert: true, FeatherPx: 4, Opacity: 1)
            },
            track: 0);

        filter.Should().Contain("[rmk0]split=2[rmt0s0][rmt0s1]");
        filter.Should().Contain("[rmt0s0]null[rmt0e0]");
        filter.Should().Contain("[rmt0s1]negate,gblur=sigma=4[rmt0e1]");
        labels.Should().Equal("[rmt0e0]", "[rmt0e1]");
    }

    [Fact]
    public void A_large_object_gets_a_blur_and_mosaic_sized_to_it_not_to_the_frame()
    {
        // Production run 3bc05f61: a near face (about 900 px on a 1080x1920 canvas) stayed readable at
        // Strength 2 with the frame-relative sigma (32.4). Sized to the face, Strength 1 is already 90.
        RegionEffectFilterBuilder.BlurSigma(1080, 1, objectSizePx: 900).Should().Be(90);
        RegionEffectFilterBuilder.BlurSigma(1080, 2, objectSizePx: 900).Should().Be(180);
        RegionEffectFilterBuilder.PixelBlock(1080, 1, objectSizePx: 900).Should().Be(112);
        // A small object keeps the frame-relative floor; no size is exactly the old value.
        RegionEffectFilterBuilder.BlurSigma(1080, 1, objectSizePx: 50).Should().Be(16.2);
        RegionEffectFilterBuilder.BlurSigma(1080, 2, objectSizePx: null).Should().Be(32.4);
        RegionEffectFilterBuilder.PixelBlock(1080, 2, objectSizePx: null).Should().Be(43);
        RegionEffectFilterBuilder.BuildEffectChain("[in]", "[m]", "[out]", 0, RegionEffect.Blur, 1080, 1920, 1, "30/1", 2, objectSizePx: 900)
            .Should().Contain("gblur=sigma=90,");
    }

    [Fact]
    public void The_object_size_is_the_90th_percentile_box_short_side_on_the_canvas()
    {
        var source = new VideoAnalysisMedia(4, 30, 1, 1280, 720);
        var canvas = new VideoAnalysisMedia(4, 30, 1, 1080, 1920);
        // Ten boxes, 0.1..1.0 of the height square-ish: short side is H * 720 for these (W*1280 is larger).
        var boxes = Enumerable.Range(1, 10).Select(i => new VideoSegmentationBoxFrame(i * 0.1, 0.1, 0.1, 0.9, i * 0.05)).ToList();
        var track = new VideoSegmentationTrack("g0", 0, "face", 0, 4, 120, 30, 0.1, 0.9, "k", "h", "m", BoxKeyframes: boxes);

        double? size = ReelBolt.WorkflowEngine.Execution.StepExecutors.VideoCompileStepExecutor.ObjectSizeOnCanvas(track, source, canvas, fitted: true, cover: true);

        // p90 = 9th of 10 = 0.45 * 720 = 324 source px; cover scale = max(1080/1280, 1920/720) = 2.6667.
        size.Should().BeApproximately(864, 0.1);
        ReelBolt.WorkflowEngine.Execution.StepExecutors.VideoCompileStepExecutor.ObjectSizeOnCanvas(track with { BoxKeyframes = null }, source, canvas, true, true)
            .Should().BeNull();
    }

    [Fact]
    public void Mask_stream_places_every_window_on_a_black_canvas_of_the_whole_program()
    {
        string graph = RegionEffectFilterBuilder.BuildMaskStream(
            inputIndex: 3,
            conformFilter: "scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080,setsar=1",
            fps: "24/1",
            totalDurationSec: 12.5,
            width: Width,
            height: Height,
            windows: new[]
            {
                new RegionMaskWindow(1.0, 2.5, 0.5, 2.0),
                new RegionMaskWindow(4.0, 5.0, 7.25, 8.25)
            },
            outputLabel: "[mask]");

        graph.Should().StartWith("[3:v]format=yuv420p,scale=1920:1080");
        graph.Should().Contain("color=c=black:s=1920x1080:r=24/1:d=12.5,format=yuv420p[rm0win0]");
        graph.Should().Contain("[rm0raw]trim=start=1:end=2.5,setpts=PTS-STARTPTS+0.5/TB[rm0seg0]");
        graph.Should().Contain("[rm0win0][rm0seg0]overlay=x=0:y=0:eof_action=pass:repeatlast=0:format=yuv420[rm0win1]");
        graph.Should().Contain("[rm0raw]trim=start=4:end=5,setpts=PTS-STARTPTS+7.25/TB[rm0seg1]");
        graph.Should().Contain("[rm0win1][rm0seg1]overlay=x=0:y=0:eof_action=pass:repeatlast=0:format=yuv420[rm0win2]");
        graph.Should().EndWith("[rm0win2]format=gray[mask]");
    }

    [Fact]
    public void Two_tracks_in_one_graph_never_share_an_internal_label()
    {
        IReadOnlyList<RegionMaskWindow> windows = new[] { new RegionMaskWindow(0, 1, 0, 1) };
        string first = RegionEffectFilterBuilder.BuildMaskStream(1, null, "24/1", 1, 64, 36, windows, "[a]", slot: 0);
        string second = RegionEffectFilterBuilder.BuildMaskStream(2, null, "24/1", 1, 64, 36, windows, "[b]", slot: 1);

        first.Should().Contain("[rm0raw]").And.Contain("[rm0win1]");
        second.Should().Contain("[rm1raw]").And.Contain("[rm1win1]");
        // No label may be DEFINED twice in the graph ffmpeg is handed — the last label of a stage is
        // the one it defines, and every definition here is a single-output stage.
        var defined = (first + ";" + second)
            .Split(';')
            .Select(stage => stage.TrimEnd())
            .Where(stage => stage.EndsWith(']'))
            .Select(stage => stage[(stage.LastIndexOf('[') + 1)..^1])
            .Where(name => name.Length > 0 && !name.All(char.IsDigit) && !name.Contains(':'))
            .ToList();
        defined.Should().Contain(new[] { "rm0raw", "rm0win1", "rm1raw", "rm1win1" });
        defined.GroupBy(l => l).Where(g => g.Count() > 1).Select(g => g.Key).Should().BeEmpty();
    }

    [Fact]
    public void Mask_stream_needs_at_least_one_window()
    {
        Action act = () => RegionEffectFilterBuilder.BuildMaskStream(
            0, null, "24/1", 1, Width, Height, Array.Empty<RegionMaskWindow>(), "[m]");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Every_effect_member_produces_a_graph()
    {
        foreach (RegionEffect effect in Enum.GetValues<RegionEffect>())
        {
            string graph = Chain(effect);
            graph.Should().NotBeNullOrWhiteSpace(because: effect.ToString());
            graph.Should().Contain("[out]");
            graph.Should().Contain("alphamerge");
        }
    }
}
