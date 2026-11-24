#nullable enable
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="ReframeGeometry"/>: easing curves, keyframe interpolation and the integer crop window
/// the <c>zoompan</c> stage really takes (docs/video-editing.md "Keyframed reframing").
/// </summary>
public class ReframeGeometryTests
{
    private static ReframeKeyframe Key(double at, double zoom, double cx = 0.5, double cy = 0.5, ReframeEasing easing = ReframeEasing.Linear) =>
        new(at, zoom, cx, cy, easing);

    [Theory]
    [InlineData(ReframeEasing.Linear, 0.25, 0.25)]
    [InlineData(ReframeEasing.EaseIn, 0.5, 0.25)]
    [InlineData(ReframeEasing.EaseOut, 0.5, 0.75)]
    [InlineData(ReframeEasing.EaseInOut, 0.25, 0.15625)]
    [InlineData(ReframeEasing.EaseInOut, 0.5, 0.5)]
    [InlineData(ReframeEasing.Hold, 0.99, 0.0)]
    public void Easing_curves_have_their_documented_shape(ReframeEasing easing, double u, double expected)
    {
        ReframeGeometry.Ease(easing, u).Should().BeApproximately(expected, 1e-12);
    }

    [Theory]
    [InlineData(ReframeEasing.Linear)]
    [InlineData(ReframeEasing.EaseIn)]
    [InlineData(ReframeEasing.EaseOut)]
    [InlineData(ReframeEasing.EaseInOut)]
    public void Every_moving_curve_starts_at_zero_ends_at_one_and_never_overshoots(ReframeEasing easing)
    {
        ReframeGeometry.Ease(easing, 0).Should().Be(0);
        ReframeGeometry.Ease(easing, 1).Should().Be(1);
        double previous = 0;
        for (int i = 1; i <= 100; i++)
        {
            double e = ReframeGeometry.Ease(easing, i / 100.0);
            e.Should().BeInRange(previous, 1.0);
            previous = e;
        }
    }

    [Fact]
    public void Keyframes_interpolate_with_the_easing_of_the_keyframe_they_leave()
    {
        // Span starts at 2 s of source time: keyframes at 2 s and 4 s of SOURCE time.
        var reframe = new VideoReframe(0, 2, 6, [Key(0, 3, 0.3, 0.7, ReframeEasing.EaseOut), Key(2, 1)]);

        ReframeWindow mid = ReframeGeometry.WindowAt([reframe], 3.0);

        // EaseOut(0.5) = 0.75 of the way from (3, .3, .7) to (1, .5, .5).
        mid.Zoom.Should().BeApproximately(1.5, 1e-12);
        mid.CenterX.Should().BeApproximately(0.45, 1e-12);
        mid.CenterY.Should().BeApproximately(0.55, 1e-12);
    }

    [Fact]
    public void At_the_span_boundaries_the_first_and_last_framings_hold_and_outside_it_the_frame_is_whole()
    {
        var reframe = new VideoReframe(0, 2, 6, [Key(0.5, 3, 0.3, 0.7), Key(2, 2)]);

        ReframeGeometry.WindowAt([reframe], 2.0).Should().Be(new ReframeWindow(3, 0.3, 0.7), "before the first keyframe its framing holds");
        ReframeGeometry.WindowAt([reframe], 2.5).Should().Be(new ReframeWindow(3, 0.3, 0.7), "exactly on a keyframe is that keyframe");
        ReframeGeometry.WindowAt([reframe], 4.0).Should().Be(new ReframeWindow(2, 0.5, 0.5));
        ReframeGeometry.WindowAt([reframe], 5.99).Should().Be(new ReframeWindow(2, 0.5, 0.5), "after the last keyframe its framing holds");
        ReframeGeometry.WindowAt([reframe], 6.0).Should().Be(ReframeWindow.Identity, "ToSec is exclusive");
        ReframeGeometry.WindowAt([reframe], 1.99).Should().Be(ReframeWindow.Identity);
        ReframeGeometry.WindowAt([], 3).Should().Be(ReframeWindow.Identity);
    }

    [Fact]
    public void A_hold_keeps_its_framing_until_the_next_keyframe_then_cuts()
    {
        var reframe = new VideoReframe(0, null, null, [Key(0, 2, easing: ReframeEasing.Hold), Key(1, 4)]);

        ReframeGeometry.WindowAt([reframe], 0.999).Zoom.Should().Be(2);
        ReframeGeometry.WindowAt([reframe], 1.0).Zoom.Should().Be(4);
    }

    [Theory]
    // Measured with ffmpeg 8 zoompan on a 256-wide luma ramp: first/last visible source column.
    [InlineData(2.3, 37.6, 36, 111)]
    [InlineData(1.7, 80.9, 80, 150)]
    [InlineData(3.1, 11.2, 10, 82)]
    public void The_pixel_window_is_the_one_zoompan_takes(double zoom, double left, int x, int w)
    {
        // Express the measured left edge as a centre: cx*iw - iw/zoom/2 = left.
        double cx = (left + 256 / zoom / 2) / 256;

        ReframePixelWindow px = ReframeGeometry.PixelWindow(new ReframeWindow(zoom, cx, 0.5), 256, 64);

        px.X.Should().Be(x);
        px.Width.Should().Be(w);
    }

    [Fact]
    public void The_pixel_window_never_leaves_the_frame_and_the_full_frame_is_the_identity()
    {
        ReframeGeometry.PixelWindow(ReframeWindow.Identity, 1920, 1080).Should().Be(new ReframePixelWindow(0, 0, 1920, 1080));
        ReframePixelWindow corner = ReframeGeometry.PixelWindow(new ReframeWindow(4, 1.0, 1.0), 1920, 1080);
        (corner.X + corner.Width).Should().BeLessThanOrEqualTo(1920);
        (corner.Y + corner.Height).Should().BeLessThanOrEqualTo(1080);
        (corner.X % 2).Should().Be(0, "zoompan snaps the window to the chroma grid");
    }

    [Fact]
    public void Applying_a_window_to_a_fit_maps_the_windows_edges_onto_the_fitted_picture()
    {
        var fit = new VideoCompileStepExecutor.SourceCanvasFit(10, 20, 960, 540);
        var px = new ReframePixelWindow(480, 270, 960, 540); // a 2x zoom on the centre of 1920x1080

        VideoCompileStepExecutor.SourceCanvasFit zoomed = ReframeGeometry.Apply(fit, px, 1920, 1080);

        // Source pixel 480 (normalized 0.25) -> the fitted picture's left edge; 1440 (0.75) -> its right edge.
        (zoomed.OffsetX + 0.25 * zoomed.Width).Should().BeApproximately(10, 1e-9);
        (zoomed.OffsetX + 0.75 * zoomed.Width).Should().BeApproximately(970, 1e-9);
        (zoomed.OffsetY + 0.25 * zoomed.Height).Should().BeApproximately(20, 1e-9);
        (zoomed.OffsetY + 0.75 * zoomed.Height).Should().BeApproximately(560, 1e-9);
        ReframeGeometry.Apply(fit, new ReframePixelWindow(0, 0, 1920, 1080), 1920, 1080).Should().Be(fit);
    }
}
