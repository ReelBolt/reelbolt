namespace ReelBolt.Shared.Inference;

/// <summary>
/// Reads the pixel size out of a PNG, JPEG, GIF or WebP header without decoding a pixel. There is no raster
/// library in either service and none is added: this walks a handful of header bytes.
/// </summary>
/// <remarks>
/// <para>
/// Public because the editor's operations controller needs the size of a layer whose row carries none
/// (a <c>Source</c> layer made from an uploaded photo stores no width or height) to plan an expand and to
/// place its result, and reads only the first bytes of the object to get it.
/// </para>
/// <para>
/// <b>EXIF orientation.</b> A phone JPEG often stores its pixels sideways and an EXIF <c>Orientation</c>
/// tag saying how to turn them. A browser draws the picture turned (CSS <c>image-orientation: from-image</c>
/// is the default), so the size the user sees, and the size the editor's canvas works in, is the turned one.
/// <see cref="TryReadDisplaySize"/> applies the tag (orientations 5 to 8 swap width and height);
/// <see cref="TryReadDimensions"/> reports the stored size, which is what a header states.
/// </para>
/// </remarks>
public static class ImageHeader
{
    /// <summary>How many leading bytes a caller should read to be sure of reaching a JPEG's frame header.</summary>
    /// <remarks>Phone JPEGs put EXIF (with a thumbnail), XMP and MPF segments before the frame; 2 MiB covers them.</remarks>
    public const int PrefixBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The stored pixel size (alias of <see cref="TryReadDimensions"/>, used by the designed-graphic and
    /// assistant paths to size a frame or a drawn mask).
    /// </summary>
    public static bool TryReadSize(ReadOnlySpan<byte> bytes, out int width, out int height) =>
        TryReadDimensions(bytes, out width, out height);

    /// <summary>The stored pixel size, or <c>false</c> for anything unreadable (including a truncated header).</summary>
    public static bool TryReadDimensions(ReadOnlySpan<byte> bytes, out int width, out int height) =>
        TryRead(bytes, out width, out height, out _);

    /// <summary>The most a JPEG is read to reach its frame header (<see cref="ReadDisplaySizeAsync"/>): 16 MiB.</summary>
    public const int MaxJpegScanBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The size a picture is SHOWN at, read from <paramref name="stream"/>: the first <see cref="PrefixBytes"/>, and for
    /// a JPEG whose frame header is not among them (large EXIF, XMP or MPF segments before it), more in
    /// <see cref="PrefixBytes"/> steps up to <paramref name="maxBytes"/>, so a long preamble cannot hide the size.
    /// Null when no size is found within the bound.
    /// </summary>
    public static async Task<(int Width, int Height)?> ReadDisplaySizeAsync(
        Stream stream, int maxBytes = MaxJpegScanBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        int limit = Math.Max(PrefixBytes, maxBytes);
        byte[] buffer = new byte[PrefixBytes];
        int filled = 0;
        while (true)
        {
            int target = Math.Min(buffer.Length, limit);
            int read;
            while (filled < target && (read = await stream.ReadAsync(buffer.AsMemory(filled, target - filled), ct).ConfigureAwait(false)) > 0)
            {
                filled += read;
            }

            if (TryReadDisplaySize(buffer.AsSpan(0, filled), out int width, out int height)) return (width, height);

            bool jpeg = filled >= 2 && buffer[0] == 0xFF && buffer[1] == 0xD8;
            if (!jpeg || filled < target || target >= limit) return null;

            Array.Resize(ref buffer, Math.Min(limit, buffer.Length + PrefixBytes));
        }
    }

    /// <summary>
    /// The size the picture is SHOWN at: the stored size, with width and height swapped when a JPEG's EXIF
    /// orientation turns it by 90 degrees (orientations 5 to 8).
    /// </summary>
    public static bool TryReadDisplaySize(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        if (!TryRead(bytes, out width, out height, out int orientation)) return false;
        if (orientation is >= 5 and <= 8) (width, height) = (height, width);
        return true;
    }

    private static bool TryRead(ReadOnlySpan<byte> bytes, out int width, out int height, out int orientation)
    {
        width = 0;
        height = 0;
        orientation = 1;

        // PNG: 8-byte signature, then the IHDR chunk whose first two fields are width and height as
        // big-endian uint32 at offsets 16 and 20.
        if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            uint w = (uint)(bytes[16] << 24 | bytes[17] << 16 | bytes[18] << 8 | bytes[19]);
            uint h = (uint)(bytes[20] << 24 | bytes[21] << 16 | bytes[22] << 8 | bytes[23]);
            if (w is 0 or > int.MaxValue || h is 0 or > int.MaxValue) return false;
            width = (int)w;
            height = (int)h;
            return true;
        }

        // JPEG: walk the marker segments to the first start-of-frame, whose payload is
        // precision(1) height(2) width(2), big-endian; an APP1 "Exif" segment on the way carries the
        // orientation.
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < bytes.Length)
            {
                if (bytes[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                byte marker = bytes[i + 1];
                if (marker == 0xFF)
                {
                    i++;
                    continue;
                }

                bool isSof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
                if (isSof)
                {
                    int h = bytes[i + 5] << 8 | bytes[i + 6];
                    int w = bytes[i + 7] << 8 | bytes[i + 8];
                    if (w == 0 || h == 0) return false;
                    width = w;
                    height = h;
                    return true;
                }

                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
                {
                    i += 2;
                    continue;
                }

                int length = bytes[i + 2] << 8 | bytes[i + 3];
                if (length < 2) return false;
                if (marker == 0xE1 && i + 4 + length - 2 <= bytes.Length)
                {
                    int? read = ReadExifOrientation(bytes.Slice(i + 4, length - 2));
                    if (read is { } o) orientation = o;
                }

                i += 2 + length;
            }

            return false;
        }

        // GIF: "GIF87a" or "GIF89a", then the logical screen width and height as little-endian uint16.
        if (bytes.Length >= 10 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == '8'
            && (bytes[4] == '7' || bytes[4] == '9') && bytes[5] == 'a')
        {
            width = bytes[6] | bytes[7] << 8;
            height = bytes[8] | bytes[9] << 8;
            return width > 0 && height > 0;
        }

        // WebP: RIFF....WEBP, then one of three chunk kinds.
        if (bytes.Length >= 30
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            if (bytes[12] == 'V' && bytes[13] == 'P' && bytes[14] == '8' && bytes[15] == 'X')
            {
                // Extended: canvas width-1 and height-1 as 24-bit little-endian at 24 and 27.
                width = (bytes[24] | bytes[25] << 8 | bytes[26] << 16) + 1;
                height = (bytes[27] | bytes[28] << 8 | bytes[29] << 16) + 1;
                return true;
            }

            if (bytes[12] == 'V' && bytes[13] == 'P' && bytes[14] == '8' && bytes[15] == ' '
                && bytes[23] == 0x9D && bytes[24] == 0x01 && bytes[25] == 0x2A)
            {
                // Lossy: after the 3-byte frame tag and 3-byte start code, two 14-bit little-endian sizes.
                width = (bytes[26] | bytes[27] << 8) & 0x3FFF;
                height = (bytes[28] | bytes[29] << 8) & 0x3FFF;
                return width > 0 && height > 0;
            }

            if (bytes[12] == 'V' && bytes[13] == 'P' && bytes[14] == '8' && bytes[15] == 'L' && bytes[20] == 0x2F)
            {
                // Lossless: signature byte 0x2F, then 14 bits of width-1 and 14 bits of height-1.
                uint bits = (uint)(bytes[21] | bytes[22] << 8 | bytes[23] << 16 | bytes[24] << 24);
                width = (int)(bits & 0x3FFF) + 1;
                height = (int)(bits >> 14 & 0x3FFF) + 1;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The <c>Orientation</c> tag (0x0112) of IFD0 inside an APP1 payload that starts with <c>Exif\0\0</c>,
    /// or <c>null</c> when there is none or the TIFF structure is not readable.
    /// </summary>
    private static int? ReadExifOrientation(ReadOnlySpan<byte> app1)
    {
        if (app1.Length < 14 || app1[0] != 'E' || app1[1] != 'x' || app1[2] != 'i' || app1[3] != 'f' || app1[4] != 0 || app1[5] != 0)
        {
            return null;
        }

        ReadOnlySpan<byte> tiff = app1[6..];
        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I') little = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') little = false;
        else return null;

        if (U16(tiff, 2, little) != 42) return null;
        long ifd = U32(tiff, 4, little);
        if (ifd < 8 || ifd + 2 > tiff.Length) return null;

        int count = U16(tiff, (int)ifd, little);
        for (int n = 0; n < count; n++)
        {
            int entry = (int)ifd + 2 + n * 12;
            if (entry + 12 > tiff.Length) return null;
            if (U16(tiff, entry, little) != 0x0112) continue;

            int value = U16(tiff, entry + 8, little);
            return value is >= 1 and <= 8 ? value : null;
        }

        return null;
    }

    private static int U16(ReadOnlySpan<byte> b, int at, bool little) =>
        little ? b[at] | b[at + 1] << 8 : b[at] << 8 | b[at + 1];

    private static long U32(ReadOnlySpan<byte> b, int at, bool little) => little
        ? (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24)
        : (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
}
