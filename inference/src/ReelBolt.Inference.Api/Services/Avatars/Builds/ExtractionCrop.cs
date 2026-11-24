using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using SkiaSharp;

namespace ReelBolt.Inference.Api.Services.Avatars.Builds;

/// <summary>
/// Where an object pulled out of a video is cut (docs/design/avatar-objects.md "Pulling an object out of a video",
/// step 3): the box the segmentation model found, plus a margin of <see cref="MarginFraction"/> of the box on every
/// side, grown to at least <see cref="MinSidePx"/> pixels a side (never past the frame), moved inside the frame and
/// clamped to it. Pure: fractions of the frame in, fractions of the frame out.
/// </summary>
public static class ExtractionCrop
{
    /// <summary>The margin added on each side, as a fraction of the box's own width (left, right) and height (top, bottom).</summary>
    public const double MarginFraction = 0.12;

    /// <summary>The smallest side a crop has, in the frame's pixels (the whole side when the frame is smaller).</summary>
    public const int MinSidePx = 256;

    /// <summary>
    /// The crop of a box (<paramref name="box"/>, fractions of the frame, corners in any order) in a frame of
    /// <paramref name="frameWidth"/> x <paramref name="frameHeight"/> pixels. Null for a box with no area or a frame
    /// with no size (the whole frame is then used).
    /// </summary>
    public static NormalizedCrop? Compute(NormalizedBox box, int frameWidth, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(box);
        if (frameWidth <= 0 || frameHeight <= 0) return null;
        if (!double.IsFinite(box.XMin) || !double.IsFinite(box.YMin) || !double.IsFinite(box.XMax) || !double.IsFinite(box.YMax)) return null;

        // In pixels, corners in order, inside the frame.
        double x0 = Math.Clamp(Math.Min(box.XMin, box.XMax), 0, 1) * frameWidth;
        double x1 = Math.Clamp(Math.Max(box.XMin, box.XMax), 0, 1) * frameWidth;
        double y0 = Math.Clamp(Math.Min(box.YMin, box.YMax), 0, 1) * frameHeight;
        double y1 = Math.Clamp(Math.Max(box.YMin, box.YMax), 0, 1) * frameHeight;
        double w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return null;

        // The margin, then the minimum size, both around the box's centre.
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        double width = Math.Max(w * (1 + 2 * MarginFraction), Math.Min(MinSidePx, frameWidth));
        double height = Math.Max(h * (1 + 2 * MarginFraction), Math.Min(MinSidePx, frameHeight));
        width = Math.Min(width, frameWidth);
        height = Math.Min(height, frameHeight);

        // Moved inside the frame (never shrunk again by the move).
        double left = Math.Clamp(cx - width / 2, 0, frameWidth - width);
        double top = Math.Clamp(cy - height / 2, 0, frameHeight - height);

        // As fractions, rounded down so x + w and y + h never pass 1.
        double fx = Floor6(left / frameWidth), fy = Floor6(top / frameHeight);
        double fw = Floor6(Math.Min(width / frameWidth, 1 - fx)), fh = Floor6(Math.Min(height / frameHeight, 1 - fy));
        return new NormalizedCrop(fx, fy, Math.Max(ExtractFrameStepConfig.MinCropFraction, fw), Math.Max(ExtractFrameStepConfig.MinCropFraction, fh));
    }

    /// <summary>A vendor box in the segmentation model's convention (normalized centre-x, centre-y, width, height) as corners.</summary>
    public static NormalizedBox? FromCentre(IReadOnlyList<double>? cxCyWh)
    {
        if (cxCyWh is not { Count: 4 } b || b.Any(v => !double.IsFinite(v)) || b[2] <= 0 || b[3] <= 0) return null;
        return new NormalizedBox(b[0] - b[2] / 2, b[1] - b[3] / 2, b[0] + b[2] / 2, b[1] + b[3] / 2);
    }

    /// <summary>
    /// The box of what a mask covers, as fractions of the mask: the pixels that are opaque (a cut-out with transparency)
    /// or bright (a black-and-white matte). Null when nothing is covered or the picture cannot be read.
    /// </summary>
    public static NormalizedBox? FromMask(byte[]? mask)
    {
        if (mask is not { Length: > 0 }) return null;
        try
        {
            using SKBitmap? bitmap = SKBitmap.Decode(mask);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0) return null;

            // A picture with any transparent pixel is read by its alpha; a fully opaque one is a matte, read by brightness.
            bool byAlpha = false;
            for (int y = 0; y < bitmap.Height && !byAlpha; y++)
                for (int x = 0; x < bitmap.Width; x++)
                    if (bitmap.GetPixel(x, y).Alpha < 128)
                    {
                        byAlpha = true;
                        break;
                    }

            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    SKColor c = bitmap.GetPixel(x, y);
                    bool on = byAlpha ? c.Alpha >= 128 : (c.Red + c.Green + c.Blue) / 3 >= 128;
                    if (!on) continue;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            if (maxX < 0) return null;
            return new NormalizedBox(
                (double)minX / bitmap.Width, (double)minY / bitmap.Height, (double)(maxX + 1) / bitmap.Width, (double)(maxY + 1) / bitmap.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double Floor6(double value) => Math.Floor(value * 1_000_000) / 1_000_000;
}
