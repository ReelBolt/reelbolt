using System.Globalization;

namespace ReelBolt.Shared.Inference;

/// <summary>What the user asked an expand for: pixels per side, or a shape to reach.</summary>
public abstract record ExpandTarget
{
    private ExpandTarget() { }

    /// <summary>Grow each side by this many pixels OF THE LAYER'S OWN PICTURE.</summary>
    public sealed record Margins(int Left, int Top, int Right, int Bottom) : ExpandTarget;

    /// <summary>
    /// Grow the picture, centred, until it has this shape (width : height), adding only what is missing:
    /// a landscape photo asked for 9:16 grows at the top and bottom.
    /// </summary>
    public sealed record Shape(int AspectWidth, int AspectHeight) : ExpandTarget
    {
        /// <summary>"9:16".</summary>
        public override string ToString() => AspectWidth.ToString(CultureInfo.InvariantCulture) + ":" +
                                             AspectHeight.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses <c>"W:H"</c> (two whole numbers from 1 to 10000, a ratio between 1:8 and 8:1), or returns
    /// <c>null</c>.
    /// </summary>
    public static Shape? ParseShape(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] parts = text.Trim().Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int w)
            || !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int h)
            || w is < 1 or > 10000 || h is < 1 or > 10000)
        {
            return null;
        }

        double ratio = (double)w / h;
        return ratio is < 0.125 or > 8 ? null : new Shape(w, h);
    }
}

/// <summary>What an expand endpoint can take, from its catalogue entry.</summary>
public sealed record ExpandLimits(
    FalExpandGeometry Geometry,
    int? MaxMarginPx,
    long? MaxOutputPixels,
    bool MaxOutputPixelsExclusive,
    long? MaxInputPixels)
{
    /// <summary>The limits of a catalogued expand endpoint.</summary>
    public static ExpandLimits For(FalImageEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.ExpandBy == FalExpandGeometry.None)
        {
            throw new ArgumentException("'" + endpoint.Id + "' is not an expand endpoint.", nameof(endpoint));
        }

        return new ExpandLimits(endpoint.ExpandBy, endpoint.MaxMarginPx, endpoint.MaxOutputPixels,
            endpoint.MaxOutputPixelsExclusive, endpoint.MaxInputPixels);
    }
}

/// <summary>Which picture an expand sends to the AI service.</summary>
public enum ExpandSource
{
    /// <summary>The layer's own picture, untouched. Always preferred.</summary>
    Original,
    /// <summary>
    /// The original is larger than the service takes: a copy at the size the layer APPEARS in the design
    /// (its pixel size times its scale on the canvas, never larger than the original).
    /// </summary>
    DisplayedSize,
    /// <summary>Even the displayed size is too large: the largest copy the service takes.</summary>
    Smaller,
}

/// <summary>
/// A planned expand: what to send, and where the original lands on the finished picture.
/// </summary>
/// <param name="Source">Whether the original is sent, or a copy (which the browser renders and uploads).</param>
/// <param name="SendWidth">The width of the picture to send: the original's, or the copy's.</param>
/// <param name="SendHeight">The height of the picture to send.</param>
/// <param name="Geometry">The finished canvas and the original's place on it, in the finished picture's pixels.</param>
public sealed record ExpandPlan(ExpandSource Source, int SendWidth, int SendHeight, ImageExpandGeometry Geometry)
{
    /// <summary>The plain sentence the editor shows when a copy was sent, or <c>null</c> for the original.</summary>
    public string? Notice => Source switch
    {
        ExpandSource.DisplayedSize =>
            "This picture was larger than the AI service accepts, so it used the picture at the size it appears in " +
            "your design (" + SendWidth + "×" + SendHeight + ").",
        ExpandSource.Smaller =>
            "This picture was larger than the AI service accepts, even at the size it appears in your design, so it " +
            "used a smaller copy (" + SendWidth + "×" + SendHeight + ").",
        _ => null
    };
}

/// <summary>A plan, or the plain sentence saying why there is none.</summary>
public sealed record ExpandPlanOutcome(ExpandPlan? Plan, string? Refusal)
{
    /// <summary>A refusal.</summary>
    public static ExpandPlanOutcome Refused(string why) => new(null, why);
}

/// <summary>
/// Turns "make this picture fit 9:16" or "add 300 px at the top" into a request an expand endpoint takes,
/// within its documented limits, and puts the result back where the original was. Pure: no I/O.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which picture is sent (owner decision).</b> The ORIGINAL whenever the endpoint's documented limits
/// allow it. A canvas endpoint (<c>fal-ai/bria/expand</c>) is told the size to draw the original at, so it
/// always gets the original and the finished canvas is simply kept under its area limit. A margins endpoint
/// (<c>fal-ai/flux-2-pro/outpaint</c>) keeps its input at the size it receives, so when the original (or the
/// picture it grows into) is over the endpoint's limits, a COPY is sent at the size the layer appears in the
/// design - its pixel size times its scale on the canvas, never larger than the original - and, only if
/// even that is too large, the largest copy the endpoint takes. Either way the user is told
/// (<see cref="ExpandPlan.Notice"/>).
/// </para>
/// <para>
/// <b>Units.</b> Margins are asked for in the layer's own pixels; the plan scales them to the picture that
/// is sent. Every number in <see cref="ImageExpandGeometry"/> is a whole pixel of the finished picture.
/// </para>
/// </remarks>
public static class ExpandPlanner
{
    /// <summary>The smallest side a sent picture may have; below it there is nothing left to expand.</summary>
    public const int MinSide = 64;

    /// <summary>
    /// Plans an expand of a <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/> picture shown at
    /// <paramref name="displayScale"/> on the canvas. <paramref name="copy"/> is the size of a copy the browser
    /// already uploaded; the plan is then made for that copy (and refused if it does not fit).
    /// </summary>
    public static ExpandPlanOutcome Plan(
        int sourceWidth,
        int sourceHeight,
        ExpandTarget target,
        ExpandLimits limits,
        double displayScale,
        (int Width, int Height)? copy = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(limits);
        if (sourceWidth < 1 || sourceHeight < 1)
        {
            return ExpandPlanOutcome.Refused("The size of this picture is not known, so it cannot be expanded.");
        }

        if (target is ExpandTarget.Margins m)
        {
            if (m.Left < 0 || m.Top < 0 || m.Right < 0 || m.Bottom < 0)
                return ExpandPlanOutcome.Refused("Margins cannot be negative.");
            if (m.Left + m.Top + m.Right + m.Bottom == 0)
                return ExpandPlanOutcome.Refused("Choose how far to expand on at least one side.");
        }

        ImageExpandGeometry full = GeometryAt(sourceWidth, sourceHeight, sourceWidth, sourceHeight, target);
        if (full.Left + full.Top + full.Right + full.Bottom == 0)
        {
            return ExpandPlanOutcome.Refused(
                "The picture already has the " + target + " shape, so there is nothing to expand. Choose another shape.");
        }

        if (limits.Geometry == FalExpandGeometry.Canvas)
        {
            // The original is always sent; the vendor draws it at the planned size.
            double k = LargestFitting(sourceWidth, sourceHeight, target, limits, 1.0, out ImageExpandGeometry? fitted);
            return fitted is null || k <= 0
                ? ExpandPlanOutcome.Refused(TooBig(limits))
                : new ExpandPlanOutcome(new ExpandPlan(ExpandSource.Original, sourceWidth, sourceHeight, fitted), null);
        }

        // ---- a margins endpoint: the input is drawn at the size it is sent.
        double shown = displayScale is > 0 and < 1 && double.IsFinite(displayScale) ? displayScale : 1.0;
        int shownWidth = Math.Max(1, (int)Math.Round(sourceWidth * shown));
        int shownHeight = Math.Max(1, (int)Math.Round(sourceHeight * shown));

        if (copy is { } c)
        {
            if (c.Width < 1 || c.Height < 1 || c.Width > sourceWidth || c.Height > sourceHeight)
            {
                return ExpandPlanOutcome.Refused("The smaller copy of the picture has the wrong size. Run the expand again.");
            }

            double expectedHeight = (double)sourceHeight * c.Width / sourceWidth;
            if (Math.Abs(c.Height - expectedHeight) > Math.Max(2, expectedHeight * 0.01))
            {
                return ExpandPlanOutcome.Refused(
                    "The smaller copy of the picture does not have the picture's shape. Run the expand again.");
            }

            ImageExpandGeometry atCopy = GeometryAt(sourceWidth, sourceHeight, c.Width, c.Height, target);
            if (!Fits(atCopy, limits))
            {
                return ExpandPlanOutcome.Refused(
                    "The smaller copy of the picture is still larger than the AI service accepts. Run the expand again.");
            }

            ExpandSource kind = Math.Abs(c.Width - shownWidth) <= 2 && Math.Abs(c.Height - shownHeight) <= 2
                ? ExpandSource.DisplayedSize
                : ExpandSource.Smaller;
            return new ExpandPlanOutcome(new ExpandPlan(kind, c.Width, c.Height, atCopy), null);
        }

        if (Fits(full, limits))
        {
            return new ExpandPlanOutcome(new ExpandPlan(ExpandSource.Original, sourceWidth, sourceHeight, full), null);
        }

        if (shown < 1)
        {
            ImageExpandGeometry atShown = GeometryAt(sourceWidth, sourceHeight, shownWidth, shownHeight, target);
            if (shownWidth >= MinSide && shownHeight >= MinSide && Fits(atShown, limits))
            {
                return new ExpandPlanOutcome(
                    new ExpandPlan(ExpandSource.DisplayedSize, shownWidth, shownHeight, atShown), null);
            }
        }

        double scale = LargestFitting(sourceWidth, sourceHeight, target, limits, shown, out ImageExpandGeometry? smaller);
        return smaller is null || scale <= 0
            ? ExpandPlanOutcome.Refused(TooBig(limits))
            : new ExpandPlanOutcome(
                new ExpandPlan(ExpandSource.Smaller, smaller.ImageWidth, smaller.ImageHeight, smaller), null);
    }

    /// <summary>
    /// The geometry of <paramref name="target"/> for the original drawn at
    /// <paramref name="imageWidth"/> x <paramref name="imageHeight"/> (margins are scaled from the original's
    /// pixels to those).
    /// </summary>
    public static ImageExpandGeometry GeometryAt(int sourceWidth, int sourceHeight, int imageWidth, int imageHeight, ExpandTarget target)
    {
        switch (target)
        {
            case ExpandTarget.Margins m:
                {
                    double sx = (double)imageWidth / sourceWidth;
                    double sy = (double)imageHeight / sourceHeight;
                    int left = (int)Math.Round(m.Left * sx);
                    int right = (int)Math.Round(m.Right * sx);
                    int top = (int)Math.Round(m.Top * sy);
                    int bottom = (int)Math.Round(m.Bottom * sy);
                    return new ImageExpandGeometry(imageWidth + left + right, imageHeight + top + bottom, left, top, imageWidth, imageHeight);
                }

            case ExpandTarget.Shape s:
                {
                    // Taller than the picture: keep the width, grow the height (split evenly, the odd pixel at
                    // the bottom). Wider: keep the height, grow the width. Never crop.
                    long tall = (long)s.AspectWidth * imageHeight;
                    long wide = (long)s.AspectHeight * imageWidth;
                    if (tall < wide)
                    {
                        int canvasHeight = Math.Max(imageHeight, (int)Math.Round((double)imageWidth * s.AspectHeight / s.AspectWidth));
                        int extra = canvasHeight - imageHeight;
                        return new ImageExpandGeometry(imageWidth, canvasHeight, 0, extra / 2, imageWidth, imageHeight);
                    }

                    if (tall > wide)
                    {
                        int canvasWidth = Math.Max(imageWidth, (int)Math.Round((double)imageHeight * s.AspectWidth / s.AspectHeight));
                        int extra = canvasWidth - imageWidth;
                        return new ImageExpandGeometry(canvasWidth, imageHeight, extra / 2, 0, imageWidth, imageHeight);
                    }

                    return new ImageExpandGeometry(imageWidth, imageHeight, 0, 0, imageWidth, imageHeight);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }
    }

    /// <summary>Whether <paramref name="g"/> is within every documented limit of the endpoint.</summary>
    public static bool Fits(ImageExpandGeometry g, ExpandLimits limits)
    {
        if (g.Left < 0 || g.Top < 0 || g.Right < 0 || g.Bottom < 0) return false;
        if (g.Left + g.Top + g.Right + g.Bottom == 0) return false;

        long canvas = (long)g.CanvasWidth * g.CanvasHeight;
        if (limits.MaxOutputPixels is { } maxOut && (limits.MaxOutputPixelsExclusive ? canvas >= maxOut : canvas > maxOut))
            return false;

        if (limits.Geometry == FalExpandGeometry.Margins)
        {
            if (limits.MaxMarginPx is { } maxMargin && Math.Max(Math.Max(g.Left, g.Right), Math.Max(g.Top, g.Bottom)) > maxMargin)
                return false;
            if (limits.MaxInputPixels is { } maxIn && (long)g.ImageWidth * g.ImageHeight > maxIn)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Where the expanded result goes on the canvas so the original's pixels stay exactly where they were:
    /// the result is scaled so that the original's part of it is as wide as the original was drawn, and moved
    /// up and left by the part added there. Uses the size the vendor actually delivered, so a vendor that
    /// rounded or resized is still lined up.
    /// </summary>
    /// <param name="x">The source layer's left edge on the canvas.</param>
    /// <param name="y">The source layer's top edge on the canvas.</param>
    /// <param name="scale">The source layer's scale.</param>
    /// <param name="sourceWidth">The source layer's own pixel width.</param>
    /// <param name="geometry">The planned geometry.</param>
    /// <param name="outputWidth">The width the vendor delivered.</param>
    /// <param name="outputHeight">The height the vendor delivered.</param>
    public static (double X, double Y, double Scale) PlaceResult(
        double x, double y, double scale, int sourceWidth, ImageExpandGeometry geometry, int outputWidth, int outputHeight)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        double qx = (double)outputWidth / geometry.CanvasWidth;
        double qy = (double)outputHeight / geometry.CanvasHeight;
        double next = scale * sourceWidth / (geometry.ImageWidth * qx);
        return (x - geometry.ImageX * qx * next, y - geometry.ImageY * qy * next, next);
    }

    /// <summary>
    /// The largest scale at or below <paramref name="ceiling"/> at which the target fits, with its geometry, or
    /// 0 and <c>null</c> when nothing at or above <see cref="MinSide"/> fits.
    /// </summary>
    private static double LargestFitting(int w0, int h0, ExpandTarget target, ExpandLimits limits, double ceiling, out ImageExpandGeometry? geometry)
    {
        ImageExpandGeometry full = GeometryAt(w0, h0, w0, h0, target);
        double k = ceiling;

        if (limits.MaxOutputPixels is { } maxOut)
        {
            double area = (double)full.CanvasWidth * full.CanvasHeight;
            k = Math.Min(k, Math.Sqrt((maxOut - (limits.MaxOutputPixelsExclusive ? 1 : 0)) / area));
        }

        if (limits.Geometry == FalExpandGeometry.Margins)
        {
            int biggest = Math.Max(Math.Max(full.Left, full.Right), Math.Max(full.Top, full.Bottom));
            if (limits.MaxMarginPx is { } maxMargin && biggest > 0) k = Math.Min(k, (double)maxMargin / biggest);
            if (limits.MaxInputPixels is { } maxIn) k = Math.Min(k, Math.Sqrt((double)maxIn / ((double)w0 * h0)));
        }

        // The closed form ignores rounding; step down until the whole-pixel geometry fits.
        for (int attempt = 0; attempt < 4000 && k > 0; attempt++)
        {
            int iw = k >= 1 ? w0 : Math.Max(1, (int)Math.Floor(w0 * k));
            int ih = k >= 1 ? h0 : Math.Max(1, (int)Math.Floor(h0 * k));

            // A small original is expanded as it is; only a SHRUNK picture has a floor.
            if (iw < w0 && (iw < MinSide || ih < MinSide)) break;

            ImageExpandGeometry candidate = GeometryAt(w0, h0, iw, ih, target);
            if (Fits(candidate, limits))
            {
                geometry = candidate;
                return k;
            }

            k *= 0.998;
        }

        geometry = null;
        return 0;
    }

    private static string TooBig(ExpandLimits limits) =>
        "This expand is larger than the AI service can make" +
        (limits.MaxOutputPixels is { } max
            ? " (at most " + (max / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " megapixels)"
            : string.Empty) +
        ", even from a smaller copy of the picture. Choose a smaller change.";
}
