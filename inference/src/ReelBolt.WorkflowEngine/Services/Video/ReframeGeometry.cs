using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// A reframe's framing at one instant: <see cref="Zoom"/> (1 = the full frame) and the window's
/// centre as fractions of the source frame.
/// </summary>
public readonly record struct ReframeWindow(double Zoom, double CenterX, double CenterY)
{
    /// <summary>The whole frame — what every moment outside a reframe shows.</summary>
    public static readonly ReframeWindow Identity = new(1, 0.5, 0.5);
}

/// <summary>The integer source-pixel window the <c>zoompan</c> stage crops and scales back to the full frame.</summary>
public readonly record struct ReframePixelWindow(int X, int Y, int Width, int Height);

/// <summary>
/// A <see cref="ReframeAnchor.Visible"/> framing split into today's zoompan <see cref="Window"/> plus
/// where the cover crop sits inside the zoomed frame: <see cref="StripX"/>/<see cref="StripY"/> are the
/// centre of the canvas strip as fractions of the zoomed (reframed) frame.
/// </summary>
public readonly record struct ReframeStrip(ReframeWindow Window, double StripX, double StripY);

/// <summary>
/// The arithmetic of keyframed reframing (docs/video-editing.md "Keyframed reframing"), shared by
/// the filter (<see cref="ReframeFilterBuilder"/>, which writes the same piecewise curves as
/// ffmpeg expressions) and every piece of geometry that has to land on the reframed picture
/// (<see cref="VideoCompileStepExecutor.SourceCanvasFit.At"/>: tracked inserts, plate guards,
/// censor masks, overlays). Pure; times are SOURCE seconds.
/// </summary>
public static class ReframeGeometry
{
    /// <summary>
    /// The eased progress (0..1) at linear progress <paramref name="u"/> (0..1) of a move that
    /// leaves a keyframe with <paramref name="easing"/>. <see cref="ReframeEasing.Hold"/> is 0 —
    /// the framing holds until the next keyframe.
    /// </summary>
    public static double Ease(ReframeEasing easing, double u)
    {
        u = Math.Clamp(u, 0, 1);
        return easing switch
        {
            ReframeEasing.EaseIn => u * u,
            ReframeEasing.EaseOut => u * (2 - u),
            ReframeEasing.EaseInOut => u * u * (3 - 2 * u),
            ReframeEasing.Hold => 0,
            _ => u
        };
    }

    /// <summary>
    /// The reframe of <paramref name="reframes"/> (one source's, validated) that covers
    /// <paramref name="sourceSec"/> — <c>[StartSec, ToSec)</c> — or null.
    /// </summary>
    public static VideoReframe? Covering(IReadOnlyList<VideoReframe> reframes, double sourceSec)
    {
        foreach (VideoReframe r in reframes)
        {
            if (sourceSec >= r.StartSec && (r.ToSec is not { } to || sourceSec < to))
                return r;
        }

        return null;
    }

    /// <summary>
    /// The framing at <paramref name="sourceSec"/>: the covering reframe's keyframes interpolated
    /// (first framing before the first keyframe, last after the last), or <see cref="ReframeWindow.Identity"/>
    /// outside every reframe.
    /// </summary>
    public static ReframeWindow WindowAt(IReadOnlyList<VideoReframe> reframes, double sourceSec)
    {
        if (Covering(reframes, sourceSec) is not { } reframe)
            return ReframeWindow.Identity;

        IReadOnlyList<ReframeKeyframe> keys = reframe.Keyframes;
        double t = sourceSec - reframe.StartSec;
        if (t < keys[0].AtSec)
            return Window(keys[0]);
        for (int i = 0; i < keys.Count - 1; i++)
        {
            ReframeKeyframe a = keys[i], b = keys[i + 1];
            if (t >= b.AtSec)
                continue;
            double e = Ease(a.Easing, (t - a.AtSec) / (b.AtSec - a.AtSec));
            return new ReframeWindow(
                a.Zoom + (b.Zoom - a.Zoom) * e,
                a.CenterX + (b.CenterX - a.CenterX) * e,
                a.CenterY + (b.CenterY - a.CenterY) * e);
        }

        return Window(keys[^1]);
    }

    /// <summary>The anchor of the reframe covering <paramref name="sourceSec"/>; <see cref="ReframeAnchor.Window"/> outside every reframe.</summary>
    public static ReframeAnchor AnchorAt(IReadOnlyList<VideoReframe> reframes, double sourceSec) =>
        Covering(reframes, sourceSec)?.Anchor ?? ReframeAnchor.Window;

    /// <summary>
    /// Splits a visible framing (<paramref name="visible"/>: zoom relative to the fit, centre = centre of
    /// what the canvas shows) into the zoompan window (centre clamped to <c>[0.5/z, 1-0.5/z]</c>) and the
    /// strip centre inside the zoomed frame, <c>0.5 + (c - Wc) * z</c> per axis, clamped to
    /// <c>[V/2, 1-V/2]</c>. <paramref name="visibleX"/>/<paramref name="visibleY"/> are the visible fraction
    /// of the frame under the clip's fit (1 on an axis the fit does not crop).
    /// </summary>
    public static ReframeStrip Decompose(ReframeWindow visible, double visibleX = 1, double visibleY = 1)
    {
        double zoom = Math.Clamp(visible.Zoom, 1, 10);
        double half = 0.5 / zoom;
        double wx = Math.Clamp(visible.CenterX, half, 1 - half);
        double wy = Math.Clamp(visible.CenterY, half, 1 - half);
        visibleX = Math.Clamp(visibleX, 0, 1);
        visibleY = Math.Clamp(visibleY, 0, 1);
        double sx = Math.Clamp(0.5 + (visible.CenterX - wx) * zoom, visibleX / 2, 1 - visibleX / 2);
        double sy = Math.Clamp(0.5 + (visible.CenterY - wy) * zoom, visibleY / 2, 1 - visibleY / 2);
        return new ReframeStrip(new ReframeWindow(zoom, wx, wy), sx, sy);
    }

    /// <summary>
    /// <paramref name="fit"/> (a covering fit of the zoomed frame, centred or focused) with its cover crop
    /// moved to <paramref name="stripX"/>/<paramref name="stripY"/>: mirrors <c>crop=cw:ch:x:y</c> with
    /// <c>x = clamp(s * iw - ow / 2, 0, iw - ow)</c> floored onto the 2-pixel chroma grid. An axis the fit
    /// does not overflow is left alone.
    /// </summary>
    internal static VideoCompileStepExecutor.SourceCanvasFit MoveCrop(
        VideoCompileStepExecutor.SourceCanvasFit fit, double stripX, double stripY, int canvasWidth, int canvasHeight)
    {
        double ox = fit.OffsetX, oy = fit.OffsetY;
        if (fit.Width > canvasWidth + 1e-6)
            ox = -Corner(stripX, fit.Width, canvasWidth);
        if (fit.Height > canvasHeight + 1e-6)
            oy = -Corner(stripY, fit.Height, canvasHeight);
        return fit with { OffsetX = ox, OffsetY = oy };

        static double Corner(double strip, double frame, int canvas) =>
            2 * Math.Floor(Math.Clamp(strip * frame - canvas / 2.0, 0, Math.Max(0, frame - canvas)) / 2);
    }

    /// <summary>
    /// The source pixels <c>zoompan</c> keeps for <paramref name="window"/>, mirroring both the
    /// filter's own arithmetic (window size <c>(int)(iw * (1 / zoom))</c>, top-left clipped to
    /// <c>[0, iw − w]</c>) and the expression <see cref="ReframeFilterBuilder"/> feeds it (the
    /// top-left floored onto the 2-pixel chroma grid, which also makes the result independent of
    /// the decoded pixel format). Measured against ffmpeg 8 on a luma ramp.
    /// </summary>
    public static ReframePixelWindow PixelWindow(ReframeWindow window, int sourceWidth, int sourceHeight)
    {
        double zoom = Math.Clamp(window.Zoom, 1, 10);
        int w = (int)(sourceWidth * (1.0 / zoom));
        int h = (int)(sourceHeight * (1.0 / zoom));
        return new ReframePixelWindow(
            Corner(window.CenterX, sourceWidth, zoom, w),
            Corner(window.CenterY, sourceHeight, zoom, h),
            w, h);

        static int Corner(double centre, int size, double zoom, int kept)
        {
            double wanted = 2 * Math.Floor(Math.Max(0, centre * size - size / zoom / 2) / 2);
            int clipped = (int)Math.Clamp(wanted, 0, Math.Max(size - kept, 0));
            return clipped & ~1;
        }
    }

    /// <summary>
    /// <paramref name="fit"/> (where the whole source frame lands on the canvas) re-expressed for a
    /// frame reframed to <paramref name="window"/>: the window is scaled back up to the full frame
    /// before the fit, so its left edge lands where the frame's left edge did.
    /// </summary>
    internal static VideoCompileStepExecutor.SourceCanvasFit Apply(
        VideoCompileStepExecutor.SourceCanvasFit fit, ReframePixelWindow window, int sourceWidth, int sourceHeight)
    {
        if (window.Width <= 0 || window.Height <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return fit;
        double sx = sourceWidth / (double)window.Width;
        double sy = sourceHeight / (double)window.Height;
        return new VideoCompileStepExecutor.SourceCanvasFit(
            fit.OffsetX - window.X / (double)window.Width * fit.Width,
            fit.OffsetY - window.Y / (double)window.Height * fit.Height,
            fit.Width * sx,
            fit.Height * sy);
    }

    private static ReframeWindow Window(ReframeKeyframe k) => new(k.Zoom, k.CenterX, k.CenterY);
}
