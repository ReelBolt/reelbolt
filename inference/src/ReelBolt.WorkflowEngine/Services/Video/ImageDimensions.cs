namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Reads the pixel size of a PNG or JPEG from its own header bytes (T-105): the ExtractFrame step reports
/// <c>{width,height}</c> without spawning a probe process. Total: anything that is not a well-formed PNG/JPEG
/// header answers null.
/// </summary>
public static class ImageDimensions
{
    public static (int Width, int Height)? TryRead(ReadOnlySpan<byte> bytes)
    {
        (int, int)? size = IsPng(bytes) ? ReadPng(bytes) : IsJpeg(bytes) ? ReadJpeg(bytes) : null;
        return size is { Item1: > 0, Item2: > 0 } ? size : null;
    }

    /// <summary><c>image/png</c> or <c>image/jpeg</c> when the bytes are a well-formed header of that kind; otherwise null.</summary>
    public static string? ContentTypeOf(ReadOnlySpan<byte> bytes) =>
        TryRead(bytes) is null ? null : IsPng(bytes) ? "image/png" : "image/jpeg";

    private static bool IsPng(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;

    private static bool IsJpeg(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8;

    private static (int, int)? ReadPng(ReadOnlySpan<byte> b)
    {
        // 8 signature + 4 length + "IHDR" + 4 width + 4 height.
        if (b.Length < 24 || b[12] != (byte)'I' || b[13] != (byte)'H' || b[14] != (byte)'D' || b[15] != (byte)'R')
            return null;
        int w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
        int h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
        return w > 0 && h > 0 ? (w, h) : null;
    }

    private static (int, int)? ReadJpeg(ReadOnlySpan<byte> b)
    {
        int i = 2;
        while (i + 3 < b.Length)
        {
            if (b[i] != 0xFF)
            {
                i++;
                continue;
            }

            byte marker = b[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            // Standalone markers carry no length.
            if (marker is 0x01 or (>= 0xD0 and <= 0xD9))
            {
                i += 2;
                continue;
            }

            int length = (b[i + 2] << 8) | b[i + 3];
            bool isSof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isSof)
            {
                if (i + 9 > b.Length)
                    return null;
                int h = (b[i + 5] << 8) | b[i + 6];
                int w = (b[i + 7] << 8) | b[i + 8];
                return (w, h);
            }

            if (length < 2)
                return null;
            i += 2 + length;
        }

        return null;
    }
}
