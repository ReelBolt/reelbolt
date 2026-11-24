using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows.Follow;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Follow;

public class EditorFollowTransformTests
{
    /// <summary>The renderer's own placement (TransformFilterBuilder.BuildDepth): contain fit, times scale, centre at W/2 + W*position.</summary>
    private static (double VisibleLeft, double VisibleRight, double VisibleTop, double VisibleBottom) RenderedWindow(
        int sw, int sh, int cw, int ch, EditorTransformValue t)
    {
        double s0 = Math.Min((double)cw / sw, (double)ch / sh);
        double layerW = sw * s0 * t.Scale, layerH = sh * s0 * t.Scale;
        double centreX = cw / 2.0 + cw * t.PositionX, centreY = ch / 2.0 + ch * t.PositionY;
        double left = centreX - layerW / 2, top = centreY - layerH / 2;
        // Canvas edges expressed as fractions of the source picture.
        return ((0 - left) / layerW, (cw - left) / layerW, (0 - top) / layerH, (ch - top) / layerH);
    }

    [Theory]
    [InlineData(1920, 1080, 1080, 1920)] // 16:9 into 9:16
    [InlineData(1080, 1920, 1920, 1080)] // 9:16 into 16:9
    [InlineData(1920, 1080, 1280, 720)]  // same aspect
    [InlineData(1000, 1000, 1920, 1080)] // square into 16:9
    public void Window_is_centred_on_the_key_and_has_the_cover_size_over_zoom(int sw, int sh, int cw, int ch)
    {
        EditorFollowFit fit = EditorFollowTransform.Fit(sw, sh, cw, ch);
        foreach ((double cx, double cy, double z) in new[] { (0.5, 0.5, 1.0), (0.3, 0.6, 1.5), (0.7, 0.4, 2.0) })
        {
            EditorTransformValue t = EditorFollowTransform.ToTransform(fit, new CameraKey(0, cx, cy, z), out bool clamped);
            clamped.Should().BeFalse();
            (double l, double r, double top, double bottom) = RenderedWindow(sw, sh, cw, ch, t);

            ((l + r) / 2).Should().BeApproximately(cx, 1e-4);
            ((top + bottom) / 2).Should().BeApproximately(cy, 1e-4);
            FollowFraming cover = fit.Framing;
            (r - l).Should().BeApproximately(cover.VisibleWidth / z, 1e-4);
            (bottom - top).Should().BeApproximately(cover.VisibleHeight / z, 1e-4);
        }
    }

    [Fact]
    public void Golden_values_for_16_9_into_9_16()
    {
        EditorFollowFit fit = EditorFollowTransform.Fit(1920, 1080, 1080, 1920);
        fit.ContainFactor.Should().BeApproximately(0.5625, 1e-9);
        fit.CoverFactor.Should().BeApproximately(1920 / (1080 * 0.5625), 1e-9);
        fit.MaxZoomLimit.Should().BeInRange(1, 8 / fit.CoverFactor); // lowered so every contained centre stays within the editor position range

        EditorTransformValue centre = EditorFollowTransform.ToTransform(fit, new CameraKey(0, 0.5, 0.5, 1), out _);
        centre.PositionX.Should().Be(0);
        centre.PositionY.Should().Be(0);
        centre.Scale.Should().BeApproximately(3.16049, 1e-5);

        EditorTransformValue left = EditorFollowTransform.ToTransform(fit, new CameraKey(0, 0.25, 0.5, 1), out _);
        // (0.5 - 0.25) * 1920 * (3.16049 * 0.5625) / 1080
        left.PositionX.Should().BeApproximately(0.25 * 1920 * 3.16049 * 0.5625 / 1080, 1e-4);
    }

    [Fact]
    public void Same_aspect_at_zoom_one_is_the_identity()
    {
        EditorFollowFit fit = EditorFollowTransform.Fit(1920, 1080, 1280, 720);
        fit.CoverFactor.Should().BeApproximately(1, 1e-9);
        EditorTransformValue t = EditorFollowTransform.ToTransform(fit, new CameraKey(0, 0.5, 0.5, 1), out _);
        t.Should().Be(new EditorTransformValue(0, 0, 1));
    }

    [Fact]
    public void A_value_outside_the_editor_range_is_clamped_and_reported()
    {
        EditorFollowFit fit = EditorFollowTransform.Fit(1920, 1080, 1080, 1920);
        EditorTransformValue t = EditorFollowTransform.ToTransform(fit, new CameraKey(0, 0.5, 0.5, 5), out bool clamped);
        clamped.Should().BeTrue();
        t.Scale.Should().Be(8);
    }

    [Fact]
    public void Mapping_is_deterministic_and_stays_in_range_over_a_computed_path()
    {
        EditorFollowFit fit = EditorFollowTransform.Fit(1920, 1080, 1080, 1920);
        List<SubjectSample> samples = [];
        for (int i = 0; i <= 100; i++)
            samples.Add(new SubjectSample(i * 0.1, 0.2 + 0.6 * i / 100.0, 0.5, 0.1, 0.2, true));
        FollowOptions options = new(FollowTightness.Medium, FollowSmoothness.Normal, FollowOnLost.Hold, false, fit.Framing, 1080, 1920, 256, fit.MaxZoomLimit);

        List<EditorTransformValue> Run() => SubjectCameraPath.Compute(samples, 0, 10, options).Keys
            .Select(k => EditorFollowTransform.ToTransform(fit, k, out _)).ToList();

        List<EditorTransformValue> a = Run(), b = Run();
        a.Should().Equal(b);
        a.Should().OnlyContain(v => Math.Abs(v.PositionX) <= 2 && Math.Abs(v.PositionY) <= 2 && v.Scale > 0 && v.Scale <= 8);
        // Every key keeps the window inside the source (cover framing never shows the outside).
        foreach (CameraKey k in SubjectCameraPath.Compute(samples, 0, 10, options).Keys)
        {
            EditorTransformValue t = EditorFollowTransform.ToTransform(fit, k, out _);
            (double l, double r, double top, double bottom) = RenderedWindow(1920, 1080, 1080, 1920, t);
            l.Should().BeGreaterThanOrEqualTo(-1e-3);
            r.Should().BeLessThanOrEqualTo(1 + 1e-3);
            top.Should().BeGreaterThanOrEqualTo(-1e-3);
            bottom.Should().BeLessThanOrEqualTo(1 + 1e-3);
        }
    }
}
