using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows.Follow;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Follow;

/// <summary>
/// QA (follow-subject, PR #955): the editor's position range (+-2 canvas) against the follow's zoom limit
/// (8 / cover). Under a strongly cropping fit the position needed to show the source edge at a high zoom
/// is above 2, and the clamp moves the visible window away from the subject.
/// </summary>
public class FollowQaEditorRangeTests
{
    private static (double L, double R, double T, double B) RenderedWindow(int sw, int sh, int cw, int ch, EditorTransformValue t)
    {
        double s0 = Math.Min((double)cw / sw, (double)ch / sh);
        double layerW = sw * s0 * t.Scale, layerH = sh * s0 * t.Scale;
        double centreX = cw / 2.0 + cw * t.PositionX, centreY = ch / 2.0 + ch * t.PositionY;
        double left = centreX - layerW / 2, top = centreY - layerH / 2;
        return ((0 - left) / layerW, (cw - left) / layerW, (0 - top) / layerH, (ch - top) / layerH);
    }

    [Theory]
    [InlineData(1920, 1080, 1080, 1920)] // 16:9 into 9:16 (the owner's case)
    [InlineData(1080, 1920, 1920, 1080)] // 9:16 into 16:9
    public void A_contained_key_at_the_editor_zoom_limit_stays_reachable(int sw, int sh, int cw, int ch)
    {
        EditorFollowFit fit = EditorFollowTransform.Fit(sw, sh, cw, ch);
        FollowFraming f = fit.Framing;
        double z = fit.MaxZoomLimit;
        // The most off-centre key the core's containment allows at this zoom.
        double cx = 0.5 * f.VisibleWidth / z + 1e-6, cy = 0.5 * f.VisibleHeight / z + 1e-6;
        EditorTransformValue t = EditorFollowTransform.ToTransform(fit, new CameraKey(0, cx, cy, z), out bool clamped);
        (double l, double r, double top, double bottom) = RenderedWindow(sw, sh, cw, ch, t);
        clamped.Should().BeFalse("a contained key inside the zoom limit should be expressible in the editor's +-2 position range");
        ((l + r) / 2).Should().BeApproximately(cx, 1e-3);
        ((top + bottom) / 2).Should().BeApproximately(cy, 1e-3);
    }

    /// <summary>Pins what the web mapping relies on: the path has a key exactly at the end of the clip's source range.</summary>
    [Fact]
    public void The_path_has_keys_at_both_ends_of_the_range()
    {
        EditorFollowFit fit = EditorFollowTransform.Fit(1920, 1080, 1080, 1920);
        List<SubjectSample> samples = [];
        for (int i = 0; i <= 140; i++)
            samples.Add(new SubjectSample(i * 0.1, 0.2 + 0.5 * i / 140.0, 0.45, 0.1, 0.2, true));
        FollowOptions options = new(FollowTightness.Medium, FollowSmoothness.Normal, FollowOnLost.Hold, false, fit.Framing, 1080, 1920, 256, fit.MaxZoomLimit);
        CameraPathResult path = SubjectCameraPath.Compute(samples, 2, 12, options);
        path.Keys[0].TimeSec.Should().Be(2);
        path.Keys[^1].TimeSec.Should().Be(12);
    }

    [Fact]
    public void Soft_zoom_follow_near_the_edge_keeps_the_subject_on_the_canvas()
    {
        const int sw = 3840, sh = 2160, cw = 1080, ch = 1920;
        EditorFollowFit fit = EditorFollowTransform.Fit(sw, sh, cw, ch);
        List<SubjectSample> samples = [];
        for (int i = 0; i <= 100; i++)
            samples.Add(new SubjectSample(i * 0.1, 0.06, 0.45, 0.04, 0.08, true));
        FollowOptions options = new(FollowTightness.Tight, FollowSmoothness.Normal, FollowOnLost.Hold, AllowSoftZoom: true,
            fit.Framing, sh, ch, 256, fit.MaxZoomLimit);

        CameraPathResult path = SubjectCameraPath.Compute(samples, 0, 10, options);
        int offCanvas = 0;
        foreach (CameraKey k in path.Keys)
        {
            EditorTransformValue t = EditorFollowTransform.ToTransform(fit, k, out _);
            (double l, double r, _, _) = RenderedWindow(sw, sh, cw, ch, t);
            double subject = 0.06 + 0.02;
            if (subject < l || subject > r) offCanvas++;
        }

        offCanvas.Should().Be(0, $"zoomCap={path.ZoomCap}, keys={path.Keys.Count}: the subject should stay inside the visible window");
    }
}
