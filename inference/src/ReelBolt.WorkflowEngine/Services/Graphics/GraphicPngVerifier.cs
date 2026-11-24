using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Inference;

namespace ReelBolt.WorkflowEngine.Services.Graphics;

/// <summary>
/// The gate between a still the sandbox rendered and a layer the person sees. Pure: bytes in, a verdict
/// and (on success) the cropped PNG out. Nothing the sandbox produced is trusted on its own word —
/// code inside it is treated as hostile, so the file is checked the way an upload would be.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><description>A byte ceiling (<see cref="MaxBytes"/>), checked by the caller while it reads and again here.</description></item>
/// <item><description>The PNG signature and header, and the header's size: within <see cref="PngAlpha"/>'s caps, within
/// <see cref="GraphicDesignRules.MaxSide"/>/<see cref="GraphicDesignRules.MaxPixels"/>, and exactly the composition's size
/// (<c>remotion still</c> renders at the composition's size; anything else is not the file we asked for).</description></item>
/// <item><description>The whole stream decodes (the alpha plane is read row by row through the inflater): a truncated or
/// corrupt file fails here, not in the browser.</description></item>
/// <item><description>Something was drawn: a fully transparent frame is <c>GRAPHIC_EMPTY</c>, never an invisible layer.</description></item>
/// <item><description>The transparent margins are cropped away (with <see cref="CropPadding"/> pixels of air), so the layer is
/// tight around the graphic; the crop's origin is returned so the layer lands exactly where it was drawn.</description></item>
/// </list>
/// </remarks>
public static class GraphicPngVerifier
{
    /// <summary>The largest still accepted from the sandbox: a 4096 x 4096 RGBA PNG compresses far below this.</summary>
    public const long MaxBytes = 48L * 1024 * 1024;

    /// <summary>Transparent pixels kept around the drawn area, so a soft shadow is not cut flush.</summary>
    public const int CropPadding = 2;

    /// <summary>The verdict. On success <see cref="Png"/> is the cropped picture and <see cref="CropX"/>/<see cref="CropY"/> its origin in the frame.</summary>
    public sealed record Result(
        bool Ok,
        string? Code,
        string? Message,
        byte[]? Png,
        int Width,
        int Height,
        int CropX,
        int CropY,
        int FrameWidth,
        int FrameHeight)
    {
        public static Result Fail(string code, string message) => new(false, code, message, null, 0, 0, 0, 0, 0, 0);
    }

    public const string InvalidOutput = "GRAPHIC_INVALID_OUTPUT";
    public const string Empty = "GRAPHIC_EMPTY";

    /// <param name="png">The rendered file.</param>
    /// <param name="expectedWidth">The composition's width, as <c>remotion compositions</c> reported it.</param>
    /// <param name="expectedHeight">The composition's height.</param>
    public static Result Verify(byte[] png, int expectedWidth, int expectedHeight)
    {
        ArgumentNullException.ThrowIfNull(png);

        if (png.LongLength == 0 || png.LongLength > MaxBytes)
            return Result.Fail(InvalidOutput, $"The rendered graphic was {png.LongLength} bytes; it must be between 1 byte and {MaxBytes / (1024 * 1024)} MiB.");

        if (!PngAlpha.IsPng(png) || PngAlpha.ReadSize(png) is not { } size)
            return Result.Fail(InvalidOutput, "The rendered graphic is not a PNG picture.");

        (int width, int height) = size;
        if (!PngAlpha.WithinCaps(width, height)
            || width > GraphicDesignRules.MaxSide || height > GraphicDesignRules.MaxSide
            || (long)width * height > GraphicDesignRules.MaxPixels)
        {
            return Result.Fail(InvalidOutput, $"The rendered graphic is {width}x{height} pixels, larger than a graphic may be.");
        }

        if (width != expectedWidth || height != expectedHeight)
        {
            return Result.Fail(InvalidOutput,
                $"The rendered graphic is {width}x{height} pixels but its composition is {expectedWidth}x{expectedHeight}.");
        }

        AlphaPlane? alpha = PngAlpha.DecodeAlpha(png);
        if (alpha is null)
            return Result.Fail(InvalidOutput, "The rendered graphic could not be read back (a damaged or unsupported PNG).");

        if (PngAlpha.OpaqueBounds(alpha) is not { } drawn)
            return Result.Fail(Empty, "The graphic came out completely transparent: nothing was drawn.");

        int x = Math.Max(0, drawn.X - CropPadding);
        int y = Math.Max(0, drawn.Y - CropPadding);
        int right = Math.Min(width, drawn.X + drawn.Width + CropPadding);
        int bottom = Math.Min(height, drawn.Y + drawn.Height + CropPadding);

        byte[]? cropped = x == 0 && y == 0 && right == width && bottom == height
            ? png
            : PngAlpha.Crop(png, x, y, right - x, bottom - y);
        if (cropped is null)
            return Result.Fail(InvalidOutput, "The rendered graphic could not be cropped (a damaged or unsupported PNG).");

        return new Result(true, null, null, cropped, right - x, bottom - y, x, y, width, height);
    }
}
