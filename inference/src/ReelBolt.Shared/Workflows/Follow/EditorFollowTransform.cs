namespace ReelBolt.Shared.Workflows.Follow;

/// <summary>One editor transform key value for a follow: <c>transform.positionX/positionY/scale</c> of a clip.</summary>
public sealed record EditorTransformValue(double PositionX, double PositionY, double Scale);

/// <summary>
/// How a source fits a canvas for a follow: the renderer's CONTAIN factor, the extra factor that makes the contain fit cover the
/// canvas, and the largest zoom the editor's scale range allows on top of that. Pure.
/// </summary>
public sealed record EditorFollowFit(
    int SourceWidth, int SourceHeight, int CanvasWidth, int CanvasHeight, double ContainFactor, double CoverFactor, double MaxZoomLimit)
{
    /// <summary>The editor's scale ceiling (<c>transform.scale</c> is in (0, 8]).</summary>
    public const double MaxScale = 8.0;

    /// <summary>The editor's position range (fractions of the canvas), both axes.</summary>
    public const double MaxPosition = 2.0;

    /// <summary>The smallest scale the editor accepts (the range is open at zero).</summary>
    public const double MinScale = 0.01;

    public double SourceAspect => (double)SourceWidth / SourceHeight;

    public double CanvasAspect => (double)CanvasWidth / CanvasHeight;

    public FollowFraming Framing => FollowFraming.Cover(SourceAspect, CanvasAspect);
}

/// <summary>
/// Maps a follow camera key (centre as source fractions + zoom) to the editor's ordinary clip transform. The renderer
/// (<c>TransformFilterBuilder.BuildDepth</c>) fits the source by CONTAIN (<c>s0 = min(cw/sw, ch/sh)</c>), multiplies the fitted size by
/// <c>scale</c> and puts the picture's centre at <c>(W/2 + W*positionX, H/2 + H*positionY)</c>; the web preview matches. A follow
/// uses COVER framing, so at zoom z the visible window is <c>1/z</c> of the cover crop. Pure and deterministic.
/// </summary>
public static class EditorFollowTransform
{
    /// <summary>The fit of a <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/> picture on a canvas (all positive).</summary>
    public static EditorFollowFit Fit(int sourceWidth, int sourceHeight, int canvasWidth, int canvasHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || canvasWidth <= 0 || canvasHeight <= 0)
            throw new ArgumentException("Source and canvas sizes must be positive.");
        double s0 = Math.Min((double)canvasWidth / sourceWidth, (double)canvasHeight / sourceHeight);
        double cover = Math.Max(canvasWidth / (sourceWidth * s0), canvasHeight / (sourceHeight * s0));
        // A contained centre at zoom z lies in [0.5*V/z, 1 - 0.5*V/z] (V = visible fraction), so |position| peaks at
        // 0.5 * (z - V) * A with A = source size * cover * s0 / canvas size. Keep that within the editor's position range.
        FollowFraming v = FollowFraming.Cover((double)sourceWidth / sourceHeight, (double)canvasWidth / canvasHeight);
        double ax = sourceWidth * cover * s0 / canvasWidth, ay = sourceHeight * cover * s0 / canvasHeight;
        double posBound = Math.Min(v.VisibleWidth + 2 * EditorFollowFit.MaxPosition / ax, v.VisibleHeight + 2 * EditorFollowFit.MaxPosition / ay) - 1e-3;
        double limit = Math.Min(EditorFollowFit.MaxScale / cover, posBound);
        return new EditorFollowFit(sourceWidth, sourceHeight, canvasWidth, canvasHeight, s0, cover, limit);
    }

    /// <summary>
    /// The transform that puts source point (<see cref="CameraKey.CenterX"/>, <see cref="CameraKey.CenterY"/>) at the canvas centre at
    /// zoom <see cref="CameraKey.Zoom"/>. A value outside the editor's range is clamped and <paramref name="clamped"/> is set.
    /// </summary>
    public static EditorTransformValue ToTransform(EditorFollowFit fit, CameraKey key, out bool clamped)
    {
        double scale = key.Zoom * fit.CoverFactor;
        double k = scale * fit.ContainFactor; // output pixels per source pixel
        double px = (0.5 - key.CenterX) * fit.SourceWidth * k / fit.CanvasWidth;
        double py = (0.5 - key.CenterY) * fit.SourceHeight * k / fit.CanvasHeight;

        double cs = Math.Clamp(scale, EditorFollowFit.MinScale, EditorFollowFit.MaxScale);
        double cx = Math.Clamp(px, -EditorFollowFit.MaxPosition, EditorFollowFit.MaxPosition);
        double cy = Math.Clamp(py, -EditorFollowFit.MaxPosition, EditorFollowFit.MaxPosition);
        clamped = cs != scale || cx != px || cy != py;
        return new EditorTransformValue(Round(cx), Round(cy), Round(cs));
    }

    private static double Round(double v) => Math.Round(v, FollowTuning.Decimals, MidpointRounding.AwayFromZero);
}
