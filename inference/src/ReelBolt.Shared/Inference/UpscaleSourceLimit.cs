namespace ReelBolt.Shared.Inference;

/// <summary>
/// The largest picture an upscale may take. An upscale is priced as x2 of the largest canvas preset (5040 x 2160),
/// so a larger source would bill below what its vendor charges. The editor's API and the assistant both refuse
/// with <see cref="TooLargeMessage"/> before any vendor call or charge, so the two cannot drift.
/// </summary>
public static class UpscaleSourceLimit
{
    public const int MaxWidth = 5040;

    public const int MaxHeight = 2160;

    /// <summary>The most pixels an upscale's source may have: 5040 x 2160 = 10,886,400.</summary>
    public const long MaxPixels = (long)MaxWidth * MaxHeight;

    /// <summary>The refusal shown to a person for a source larger than <see cref="MaxPixels"/>.</summary>
    public const string TooLargeMessage =
        "This picture is too large to upscale. The largest picture that can be upscaled is 5040 × 2160.";

    /// <summary>The refusal shown to a person for a source whose size cannot be established (no recorded size and no readable header).</summary>
    public const string UnreadableMessage = "This picture's size could not be read, so it cannot be upscaled.";

    /// <summary>True when a source of this size may be upscaled (a positive size no larger than <see cref="MaxPixels"/>).</summary>
    public static bool Allows(int width, int height) =>
        width > 0 && height > 0 && (long)width * height <= MaxPixels;
}
