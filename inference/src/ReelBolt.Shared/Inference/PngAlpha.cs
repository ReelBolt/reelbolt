using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ReelBolt.Shared.Inference;

/// <summary>One alpha value (0 = transparent, 255 = opaque) per pixel of a picture.</summary>
public sealed class AlphaPlane
{
    private bool? _hasTransparency;

    public AlphaPlane(int width, int height, byte[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "A plane needs at least one pixel.");
        if (values.LongLength != (long)width * height) throw new ArgumentException("values must hold width * height bytes.", nameof(values));

        Width = width;
        Height = height;
        Values = values;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major, one byte per pixel.</summary>
    public byte[] Values { get; }

    /// <summary>True when at least one pixel is not fully opaque.</summary>
    public bool HasTransparency => _hasTransparency ??= Values.AsSpan().IndexOfAnyExcept((byte)255) >= 0;

    /// <summary>
    /// This plane resampled (bilinear, pixel centres aligned) to <paramref name="width"/> x
    /// <paramref name="height"/>, one row at a time into <paramref name="row"/>. Cheap enough to call per
    /// output row: the horizontal weights are computed once by <see cref="Sampler"/>.
    /// </summary>
    public Sampler SamplerFor(int width, int height) => new(this, width, height);

    /// <summary>
    /// The pixel-wise product with <paramref name="other"/> (resampled to this plane's size): transparent
    /// wherever either is. What a canvas shows for a bitmap drawn <c>destination-in</c> a matte.
    /// </summary>
    public AlphaPlane Multiply(AlphaPlane other) => Combine(other, static (a, b) => (byte)((a * b + 127) / 255));

    /// <summary>This plane resampled (bilinear) to <paramref name="width"/> x <paramref name="height"/>.</summary>
    public AlphaPlane Resize(int width, int height)
    {
        if (width == Width && height == Height) return this;

        byte[] result = new byte[(long)width * height];
        Sampler sampler = SamplerFor(width, height);
        for (int y = 0; y < height; y++) sampler.Row(y, result.AsSpan(y * width, width));
        return new AlphaPlane(width, height, result);
    }

    /// <summary>The pixel-wise maximum with <paramref name="other"/> (resampled to this plane's size).</summary>
    public AlphaPlane Max(AlphaPlane other) => Combine(other, static (a, b) => Math.Max(a, b));

    private AlphaPlane Combine(AlphaPlane other, Func<byte, byte, byte> op)
    {
        ArgumentNullException.ThrowIfNull(other);
        byte[] result = new byte[Values.Length];
        Sampler sampler = other.SamplerFor(Width, Height);
        byte[] row = new byte[Width];
        for (int y = 0; y < Height; y++)
        {
            sampler.Row(y, row);
            int offset = y * Width;
            for (int x = 0; x < Width; x++) result[offset + x] = op(Values[offset + x], row[x]);
        }

        return new AlphaPlane(Width, Height, result);
    }

    /// <summary>Bilinear sampling of a plane onto a target grid, row by row.</summary>
    public sealed class Sampler
    {
        private readonly AlphaPlane _plane;
        private readonly int _height;
        private readonly int[] _x0;
        private readonly int[] _x1;
        private readonly int[] _fx; // 0..256

        internal Sampler(AlphaPlane plane, int width, int height)
        {
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
            _plane = plane;
            _height = height;
            _x0 = new int[width];
            _x1 = new int[width];
            _fx = new int[width];
            for (int x = 0; x < width; x++)
            {
                (int a, int b, int f) = Weights(x, width, plane.Width);
                _x0[x] = a;
                _x1[x] = b;
                _fx[x] = f;
            }
        }

        /// <summary>Fills <paramref name="row"/> (target width) with the samples of target row <paramref name="y"/>.</summary>
        public void Row(int y, Span<byte> row)
        {
            (int y0, int y1, int fy) = Weights(y, _height, _plane.Height);
            ReadOnlySpan<byte> top = _plane.Values.AsSpan(y0 * _plane.Width, _plane.Width);
            ReadOnlySpan<byte> bottom = _plane.Values.AsSpan(y1 * _plane.Width, _plane.Width);
            for (int x = 0; x < row.Length; x++)
            {
                int a = _x0[x];
                int b = _x1[x];
                int f = _fx[x];
                int upper = (top[a] * (256 - f)) + (top[b] * f);
                int lower = (bottom[a] * (256 - f)) + (bottom[b] * f);
                row[x] = (byte)(((upper * (256 - fy)) + (lower * fy) + 32768) >> 16);
            }
        }

        private static (int Low, int High, int Fraction) Weights(int target, int targetSize, int sourceSize)
        {
            if (sourceSize == 1) return (0, 0, 0);
            double position = ((target + 0.5) * sourceSize / targetSize) - 0.5;
            if (position <= 0) return (0, 0, 0);
            if (position >= sourceSize - 1) return (sourceSize - 1, sourceSize - 1, 0);
            int low = (int)Math.Floor(position);
            return (low, low + 1, (int)Math.Round((position - low) * 256));
        }
    }
}

/// <summary>
/// The minimum PNG reading and writing the picture editor needs to keep a cut-out's transparency
/// through an AI edit, with nothing but <see cref="ZLibStream"/>: read a PNG's alpha (or, for a mask,
/// its brightness), write a PNG whose alpha is multiplied by a plane, and write a matte.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Most image models answer with an opaque RGB picture whatever they were given:
/// an instruction edit of a cut-out (an RGBA PNG whose background is transparent) came back with that
/// background painted black, and the layer the editor stored was exactly that. The fix is to carry the
/// source's alpha over to the result, which needs pixels on the server — and the server deliberately has
/// no raster library. This is not one: it decodes every standard non-interlaced PNG (all five colour
/// types, bit depths 1 to 16, <c>PLTE</c> and <c>tRNS</c>) one row at a time and writes 8-bit RGBA, so
/// the memory a result needs is a row, not a frame. Interlaced (Adam7) PNGs and every other format are
/// declined with <c>null</c>, and the caller falls back to storing a separate matte, which the browser
/// applies.
/// </para>
/// <para>
/// CRCs are written but not verified on read: the inputs are this platform's own uploads and a vendor's
/// answer fetched over TLS, and a corrupt stream still fails in the inflater.
/// </para>
/// </remarks>
public static class PngAlpha
{
    /// <summary>Largest picture (in pixels) this will decode: an 8000 x 8000 frame.</summary>
    public const long MaxPixels = 64_000_000;

    /// <summary>Largest side accepted, so a 1 x 64M "picture" cannot force a huge per-row buffer.</summary>
    public const int MaxSide = 16_384;

    /// <summary>True when a picture of this size is within every cap this reader applies.</summary>
    public static bool WithinCaps(long width, long height) =>
        width >= 1 && height >= 1 && width <= MaxSide && height <= MaxSide && width * height <= MaxPixels;

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>True when <paramref name="bytes"/> starts with the PNG signature.</summary>
    public static bool IsPng(ReadOnlySpan<byte> bytes) => bytes.Length >= 8 && bytes[..8].SequenceEqual(Signature);

    /// <summary>
    /// The picture's alpha channel, or <c>null</c> when it is not a PNG this can read. A PNG without an
    /// alpha channel or <c>tRNS</c> answers a fully opaque plane (check <see cref="AlphaPlane.HasTransparency"/>).
    /// </summary>
    public static AlphaPlane? DecodeAlpha(byte[] png) => DecodePlane(png, luminance: false);

    /// <summary>
    /// The picture's brightness times its alpha — how a white-on-black mask reads — or <c>null</c> when
    /// it is not a PNG this can read.
    /// </summary>
    public static AlphaPlane? DecodeMask(byte[] png) => DecodePlane(png, luminance: true);

    /// <summary>
    /// <paramref name="png"/> re-encoded as 8-bit RGBA with each pixel's alpha multiplied by
    /// <paramref name="alpha"/> (resampled to the picture's size). Colour is left untouched. <c>null</c> when
    /// <paramref name="png"/> is not a PNG this can read.
    /// </summary>
    public static byte[]? ApplyAlpha(byte[] png, AlphaPlane alpha)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentNullException.ThrowIfNull(alpha);
        if (!TryReadHeader(png, out Header header, out byte[] compressed)) return null;

        try
        {
            AlphaPlane.Sampler sampler = alpha.SamplerFor(header.Width, header.Height);
            byte[] rgba = new byte[header.Width * 4];
            byte[] samples = new byte[header.Width];
            using RgbaWriter writer = new(header.Width, header.Height);

            foreach (byte[] row in ReadRows(header, compressed))
            {
                ToRgba(header, row, rgba);
                sampler.Row(writer.RowsWritten, samples);
                for (int x = 0; x < header.Width; x++)
                {
                    int i = (x * 4) + 3;
                    rgba[i] = (byte)(((rgba[i] * samples[x]) + 127) / 255);
                }

                writer.WriteRow(rgba);
            }

            return writer.RowsWritten == header.Height ? writer.Finish() : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// A matte for the browser's compositor: white everywhere, with <paramref name="alpha"/> as its alpha
    /// channel (the editor draws a cut-out <c>destination-in</c> its matte, which reads exactly that).
    /// </summary>
    public static byte[] EncodeMatte(AlphaPlane alpha)
    {
        ArgumentNullException.ThrowIfNull(alpha);
        byte[] rgba = new byte[alpha.Width * 4];
        using RgbaWriter writer = new(alpha.Width, alpha.Height);
        for (int y = 0; y < alpha.Height; y++)
        {
            int offset = y * alpha.Width;
            for (int x = 0; x < alpha.Width; x++)
            {
                int i = x * 4;
                rgba[i] = rgba[i + 1] = rgba[i + 2] = 255;
                rgba[i + 3] = alpha.Values[offset + x];
            }

            writer.WriteRow(rgba);
        }

        return writer.Finish();
    }

    /// <summary>Encodes 8-bit RGBA pixels (row-major, 4 bytes each) as a PNG. Used by tests and fixtures.</summary>
    public static byte[] EncodeRgba(int width, int height, byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.LongLength != (long)width * height * 4) throw new ArgumentException("rgba must hold width * height * 4 bytes.", nameof(rgba));

        using RgbaWriter writer = new(width, height);
        byte[] row = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(rgba, y * width * 4, row, 0, row.Length);
            writer.WriteRow(row);
        }

        return writer.Finish();
    }

    /// <summary>Decodes any readable PNG to 8-bit RGBA, or <c>null</c>. Used by tests; production code streams.</summary>
    public static (int Width, int Height, byte[] Rgba)? DecodeRgba(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (!TryReadHeader(png, out Header header, out byte[] compressed)) return null;

        try
        {
            byte[] all = new byte[(long)header.Width * header.Height * 4];
            byte[] rgba = new byte[header.Width * 4];
            int y = 0;
            foreach (byte[] row in ReadRows(header, compressed))
            {
                ToRgba(header, row, rgba);
                Array.Copy(rgba, 0, all, (long)y * rgba.Length, rgba.Length);
                y++;
            }

            return y == header.Height ? (header.Width, header.Height, all) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The alpha-weighted mean LINEAR-light colour (each channel 0..1) of the pixels whose alpha is at least
    /// <paramref name="minAlpha"/>, streamed row by row. <c>null</c> when the PNG is unreadable (interlaced,
    /// outside the caps, corrupt) or holds no such pixel.
    /// </summary>
    public static (double R, double G, double B)? MeanOpaqueLinearColor(byte[] png, byte minAlpha = 32)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (!TryReadHeader(png, out Header header, out byte[] compressed)) return null;

        try
        {
            double[] lut = new double[256];
            for (int v = 0; v < 256; v++)
            {
                double c = v / 255.0;
                lut[v] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            double r = 0, g = 0, b = 0, weight = 0;
            byte[] rgba = new byte[header.Width * 4];
            int y = 0;
            foreach (byte[] row in ReadRows(header, compressed))
            {
                ToRgba(header, row, rgba);
                for (int x = 0; x < header.Width; x++)
                {
                    int i = x * 4;
                    byte a = rgba[i + 3];
                    if (a < minAlpha) continue;
                    r += lut[rgba[i]] * a;
                    g += lut[rgba[i + 1]] * a;
                    b += lut[rgba[i + 2]] * a;
                    weight += a;
                }

                y++;
            }

            if (y != header.Height || weight <= 0) return null;
            return (r / weight, g / weight, b / weight);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            return null;
        }
    }

    /// <summary>The pixel cap used when profiling logos: logos are small, anything bigger is not measured.</summary>
    public const long LogoProfileMaxPixels = 4_000_000;

    /// <summary>
    /// Like <see cref="MeanOpaqueLinearColor"/> but plate-aware, for logo profiling. A fully opaque PNG
    /// (no pixel below <paramref name="minAlpha"/>) whose four corners and edge midpoints agree on one
    /// colour is a mark on a uniform plate: only the pixels that differ from the plate (the ink) are
    /// measured. When nothing differs, the samples disagree or the PNG has transparency, the whole image
    /// is measured. Two streaming passes (samples, then accumulation). <c>null</c> when unreadable or
    /// above <paramref name="maxPixels"/>.
    /// </summary>
    public static (double R, double G, double B)? MeanLogoLinearColor(
        byte[] png, byte minAlpha = 32, long maxPixels = LogoProfileMaxPixels)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (!TryReadHeader(png, out Header header, out byte[] compressed)) return null;
        if ((long)header.Width * header.Height > maxPixels) return null;

        try
        {
            (byte R, byte G, byte B)? plate = FindUniformPlate(header, compressed, minAlpha);
            if (plate is not null)
            {
                var ink = AccumulateMean(header, compressed, minAlpha, plate);
                if (ink is not null) return ink;
            }

            return AccumulateMean(header, compressed, minAlpha, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            return null;
        }
    }

    private const int PlateTolerance = 12;
    private const int InkDistance = 24;

    private static int ColorDistance(byte[] rgba, int i, (byte R, byte G, byte B) c) =>
        Math.Max(Math.Abs(rgba[i] - c.R), Math.Max(Math.Abs(rgba[i + 1] - c.G), Math.Abs(rgba[i + 2] - c.B)));

    private static (byte R, byte G, byte B)? FindUniformPlate(Header header, byte[] compressed, byte minAlpha)
    {
        int w = header.Width, h = header.Height;
        if (w < 3 || h < 3) return null;
        var samples = new List<(byte R, byte G, byte B)>(8);
        byte[] rgba = new byte[w * 4];
        int y = 0, midRow = h / 2;
        foreach (byte[] row in ReadRows(header, compressed))
        {
            ToRgba(header, row, rgba);
            for (int x = 0; x < w; x++)
                if (rgba[x * 4 + 3] < minAlpha) return null;

            if (y == 0 || y == h - 1)
            {
                samples.Add((rgba[0], rgba[1], rgba[2]));
                int l = (w - 1) * 4;
                samples.Add((rgba[l], rgba[l + 1], rgba[l + 2]));
                int m = (w / 2) * 4;
                samples.Add((rgba[m], rgba[m + 1], rgba[m + 2]));
            }

            if (y == midRow)
            {
                int l = (w - 1) * 4;
                samples.Add((rgba[0], rgba[1], rgba[2]));
                samples.Add((rgba[l], rgba[l + 1], rgba[l + 2]));
            }

            y++;
        }

        if (y != h || samples.Count == 0) return null;
        var plate = samples[0];
        foreach (var s in samples)
            if (Math.Max(Math.Abs(s.R - plate.R), Math.Max(Math.Abs(s.G - plate.G), Math.Abs(s.B - plate.B))) > PlateTolerance)
                return null;

        return plate;
    }

    private static (double R, double G, double B)? AccumulateMean(
        Header header, byte[] compressed, byte minAlpha, (byte R, byte G, byte B)? plate)
    {
        double[] lut = new double[256];
        for (int v = 0; v < 256; v++)
        {
            double c = v / 255.0;
            lut[v] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        double r = 0, g = 0, b = 0, weight = 0;
        byte[] rgba = new byte[header.Width * 4];
        int y = 0;
        foreach (byte[] row in ReadRows(header, compressed))
        {
            ToRgba(header, row, rgba);
            for (int x = 0; x < header.Width; x++)
            {
                int i = x * 4;
                byte a = rgba[i + 3];
                if (a < minAlpha) continue;
                if (plate is { } p && ColorDistance(rgba, i, p) <= InkDistance) continue;
                r += lut[rgba[i]] * a;
                g += lut[rgba[i + 1]] * a;
                b += lut[rgba[i + 2]] * a;
                weight += a;
            }

            y++;
        }

        if (y != header.Height || weight <= 0) return null;
        return (r / weight, g / weight, b / weight);
    }

    // ------------------------------------------------------------------ crop and mask (designed graphics, assistant masks)

    /// <summary>
    /// The smallest rectangle holding every pixel whose alpha exceeds <paramref name="threshold"/>, or
    /// <c>null</c> when none does (an empty, fully transparent picture). Used to crop a designed graphic
    /// to what was actually drawn on its transparent frame.
    /// </summary>
    public static (int X, int Y, int Width, int Height)? OpaqueBounds(AlphaPlane alpha, byte threshold = 0)
    {
        ArgumentNullException.ThrowIfNull(alpha);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < alpha.Height; y++)
        {
            ReadOnlySpan<byte> row = alpha.Values.AsSpan(y * alpha.Width, alpha.Width);
            int first = -1;
            for (int x = 0; x < row.Length; x++)
            {
                if (row[x] > threshold) { first = x; break; }
            }

            if (first < 0) continue;
            int last = first;
            for (int x = row.Length - 1; x > first; x--)
            {
                if (row[x] > threshold) { last = x; break; }
            }

            minX = Math.Min(minX, first);
            maxX = Math.Max(maxX, last);
            minY = Math.Min(minY, y);
            maxY = y;
        }

        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// <paramref name="png"/> cropped to the rectangle (clamped to the picture), re-encoded as 8-bit RGBA one
    /// row at a time, or <c>null</c> when it is not a PNG this can read or the rectangle is empty.
    /// </summary>
    public static byte[]? Crop(byte[] png, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (!TryReadHeader(png, out Header header, out byte[] compressed)) return null;

        int left = Math.Clamp(x, 0, header.Width);
        int top = Math.Clamp(y, 0, header.Height);
        int right = Math.Clamp(x + width, 0, header.Width);
        int bottom = Math.Clamp(y + height, 0, header.Height);
        if (right <= left || bottom <= top) return null;

        try
        {
            int cropWidth = right - left;
            byte[] rgba = new byte[header.Width * 4];
            byte[] slice = new byte[cropWidth * 4];
            using RgbaWriter writer = new(cropWidth, bottom - top);
            int row = 0;
            foreach (byte[] scanline in ReadRows(header, compressed))
            {
                if (row >= top && row < bottom)
                {
                    ToRgba(header, scanline, rgba);
                    Buffer.BlockCopy(rgba, left * 4, slice, 0, slice.Length);
                    writer.WriteRow(slice);
                }

                row++;
                if (row >= bottom) break;
            }

            return writer.RowsWritten == bottom - top ? writer.Finish() : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// A white-on-black inpainting mask of <paramref name="width"/> x <paramref name="height"/> with the
    /// rectangle white (the area to act on, the editor's mask polarity) and everything else opaque black,
    /// written one row at a time. The platform assistant uses it to erase or fill a described area.
    /// </summary>
    public static byte[] EncodeRectangleMask(int width, int height, int x, int y, int rectWidth, int rectHeight)
    {
        if (!WithinCaps(width, height)) throw new ArgumentOutOfRangeException(nameof(width), "The mask is outside the PNG caps.");
        int left = Math.Clamp(x, 0, width);
        int top = Math.Clamp(y, 0, height);
        int right = Math.Clamp(x + rectWidth, 0, width);
        int bottom = Math.Clamp(y + rectHeight, 0, height);

        byte[] black = new byte[width * 4];
        for (int i = 3; i < black.Length; i += 4) black[i] = 255;
        byte[] marked = (byte[])black.Clone();
        for (int px = left; px < right; px++)
        {
            int i = px * 4;
            marked[i] = marked[i + 1] = marked[i + 2] = 255;
        }

        using RgbaWriter writer = new(width, height);
        for (int row = 0; row < height; row++) writer.WriteRow(row >= top && row < bottom ? marked : black);
        return writer.Finish();
    }

    /// <summary>The pixel size a PNG's header declares, or <c>null</c> when it is not a PNG.</summary>
    public static (int Width, int Height)? ReadSize(ReadOnlySpan<byte> png)
    {
        if (!IsPng(png) || png.Length < 24) return null;
        return (BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4)));
    }

    // ------------------------------------------------------------------ reading

    private sealed record Header(int Width, int Height, int BitDepth, int ColorType, byte[]? Palette, byte[]? Transparency)
    {
        public int Channels => ColorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        public int Stride => (int)((((long)Width * Channels * BitDepth) + 7) / 8);
        public int FilterUnit => Math.Max(1, Channels * BitDepth / 8);
    }

    private static AlphaPlane? DecodePlane(byte[] png, bool luminance)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (!TryReadHeader(png, out Header header, out byte[] compressed)) return null;

        try
        {
            byte[] values = new byte[(long)header.Width * header.Height];
            byte[] rgba = new byte[header.Width * 4];
            int y = 0;
            foreach (byte[] row in ReadRows(header, compressed))
            {
                ToRgba(header, row, rgba);
                int offset = y * header.Width;
                for (int x = 0; x < header.Width; x++)
                {
                    int i = x * 4;
                    if (luminance)
                    {
                        int brightest = Math.Max(rgba[i], Math.Max(rgba[i + 1], rgba[i + 2]));
                        values[offset + x] = (byte)(((brightest * rgba[i + 3]) + 127) / 255);
                    }
                    else
                    {
                        values[offset + x] = rgba[i + 3];
                    }
                }

                y++;
            }

            return y == header.Height ? new AlphaPlane(header.Width, header.Height, values) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            return null;
        }
    }

    private static bool TryReadHeader(byte[] png, out Header header, out byte[] compressed)
    {
        header = null!;
        compressed = [];
        if (!IsPng(png)) return false;

        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        bool sawHeader = false;
        byte[]? palette = null;
        byte[]? transparency = null;
        using MemoryStream idat = new();

        int position = 8;
        while (position + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position, 4));
            string type = Encoding.ASCII.GetString(png, position + 4, 4);
            int data = position + 8;
            if (length < 0 || data + (long)length + 4 > png.Length) return false;

            ReadOnlySpan<byte> body = png.AsSpan(data, length);
            switch (type)
            {
                case "IHDR" when length >= 13:
                    width = BinaryPrimitives.ReadInt32BigEndian(body[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(body.Slice(4, 4));
                    bitDepth = body[8];
                    colorType = body[9];
                    if (body[10] != 0 || body[11] != 0) return false; // compression / filter method
                    interlace = body[12];
                    sawHeader = true;
                    break;
                case "PLTE":
                    palette = body.ToArray();
                    break;
                case "tRNS":
                    transparency = body.ToArray();
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
                case "IEND":
                    position = png.Length;
                    continue;
            }

            position = data + length + 4;
        }

        // Side and stride caps as well as the pixel cap: each row allocates a stride-sized buffer, and a
        // header is attacker-shaped (a vendor or a user upload), so no dimension is trusted on its own.
        if (!sawHeader || !WithinCaps(width, height)) return false;
        if (interlace != 0) return false; // Adam7 is declined; the caller falls back to a matte.

        bool validDepth = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => bitDepth is 8 or 16,
            _ => false
        };
        if (!validDepth || (colorType == 3 && palette is null) || idat.Length == 0) return false;

        header = new Header(width, height, bitDepth, colorType, palette, transparency);
        compressed = idat.ToArray();
        return true;
    }

    /// <summary>Inflates and unfilters one scanline at a time. The yielded array is reused: copy what you keep.</summary>
    private static IEnumerable<byte[]> ReadRows(Header header, byte[] compressed)
    {
        int stride = header.Stride;
        int unit = header.FilterUnit;
        byte[] previous = new byte[stride];
        byte[] current = new byte[stride];
        byte[] filter = new byte[1];

        using MemoryStream source = new(compressed, writable: false);
        using ZLibStream inflater = new(source, CompressionMode.Decompress);

        for (int y = 0; y < header.Height; y++)
        {
            inflater.ReadExactly(filter);
            inflater.ReadExactly(current);
            Unfilter(filter[0], current, previous, unit);
            yield return current;
            (previous, current) = (current, previous);
        }
    }

    private static void Unfilter(byte type, byte[] row, byte[] previous, int unit)
    {
        switch (type)
        {
            case 0:
                return;
            case 1:
                for (int i = unit; i < row.Length; i++) row[i] = (byte)(row[i] + row[i - unit]);
                return;
            case 2:
                for (int i = 0; i < row.Length; i++) row[i] = (byte)(row[i] + previous[i]);
                return;
            case 3:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= unit ? row[i - unit] : 0;
                    row[i] = (byte)(row[i] + ((left + previous[i]) >> 1));
                }

                return;
            case 4:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= unit ? row[i - unit] : 0;
                    int upLeft = i >= unit ? previous[i - unit] : 0;
                    row[i] = (byte)(row[i] + Paeth(left, previous[i], upLeft));
                }

                return;
            default:
                throw new InvalidDataException("Unknown PNG filter type " + type + ".");
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>One unfiltered scanline to 8-bit RGBA.</summary>
    private static void ToRgba(Header h, byte[] row, byte[] rgba)
    {
        int width = h.Width;

        // The two shapes almost every real picture is, without the per-sample arithmetic below.
        if (h.BitDepth == 8 && h.ColorType == 6)
        {
            Buffer.BlockCopy(row, 0, rgba, 0, width * 4);
            return;
        }

        if (h.BitDepth == 8 && h.ColorType == 2 && h.Transparency is null)
        {
            for (int x = 0, s = 0, d = 0; x < width; x++, s += 3, d += 4)
            {
                rgba[d] = row[s];
                rgba[d + 1] = row[s + 1];
                rgba[d + 2] = row[s + 2];
                rgba[d + 3] = 255;
            }

            return;
        }

        int channels = h.Channels;
        for (int x = 0; x < width; x++)
        {
            int d = x * 4;
            int first = x * channels;
            switch (h.ColorType)
            {
                case 0:
                    {
                        int raw = Sample(row, first, h.BitDepth);
                        byte gray = To8(raw, h.BitDepth);
                        rgba[d] = rgba[d + 1] = rgba[d + 2] = gray;
                        rgba[d + 3] = h.Transparency is { Length: >= 2 } t && raw == ((t[0] << 8) | t[1]) ? (byte)0 : (byte)255;
                        break;
                    }

                case 2:
                    {
                        int r = Sample(row, first, h.BitDepth);
                        int g = Sample(row, first + 1, h.BitDepth);
                        int b = Sample(row, first + 2, h.BitDepth);
                        rgba[d] = To8(r, h.BitDepth);
                        rgba[d + 1] = To8(g, h.BitDepth);
                        rgba[d + 2] = To8(b, h.BitDepth);
                        rgba[d + 3] = h.Transparency is { Length: >= 6 } t
                                      && r == ((t[0] << 8) | t[1]) && g == ((t[2] << 8) | t[3]) && b == ((t[4] << 8) | t[5])
                            ? (byte)0
                            : (byte)255;
                        break;
                    }

                case 3:
                    {
                        int index = Sample(row, first, h.BitDepth);
                        byte[] palette = h.Palette!;
                        if (index * 3 + 2 >= palette.Length) throw new InvalidDataException("PNG palette index out of range.");
                        rgba[d] = palette[index * 3];
                        rgba[d + 1] = palette[(index * 3) + 1];
                        rgba[d + 2] = palette[(index * 3) + 2];
                        rgba[d + 3] = h.Transparency is { } t && index < t.Length ? t[index] : (byte)255;
                        break;
                    }

                case 4:
                    {
                        byte gray = To8(Sample(row, first, h.BitDepth), h.BitDepth);
                        rgba[d] = rgba[d + 1] = rgba[d + 2] = gray;
                        rgba[d + 3] = To8(Sample(row, first + 1, h.BitDepth), h.BitDepth);
                        break;
                    }

                default:
                    rgba[d] = To8(Sample(row, first, h.BitDepth), h.BitDepth);
                    rgba[d + 1] = To8(Sample(row, first + 1, h.BitDepth), h.BitDepth);
                    rgba[d + 2] = To8(Sample(row, first + 2, h.BitDepth), h.BitDepth);
                    rgba[d + 3] = To8(Sample(row, first + 3, h.BitDepth), h.BitDepth);
                    break;
            }
        }
    }

    /// <summary>The raw value of sample <paramref name="index"/> in a row of <paramref name="bitDepth"/>-bit samples.</summary>
    private static int Sample(byte[] row, int index, int bitDepth)
    {
        switch (bitDepth)
        {
            case 8:
                return row[index];
            case 16:
                return (row[index * 2] << 8) | row[(index * 2) + 1];
            default:
                {
                    int perByte = 8 / bitDepth;
                    int shift = 8 - (bitDepth * ((index % perByte) + 1));
                    return (row[index / perByte] >> shift) & ((1 << bitDepth) - 1);
                }
        }
    }

    private static byte To8(int value, int bitDepth) => bitDepth switch
    {
        8 => (byte)value,
        16 => (byte)(value >> 8),
        _ => (byte)(value * 255 / ((1 << bitDepth) - 1))
    };

    // ------------------------------------------------------------------ writing

    /// <summary>Streams 8-bit RGBA rows into a PNG, choosing a filter per row (the usual minimum-sum heuristic).</summary>
    private sealed class RgbaWriter : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly MemoryStream _compressed = new();
        private readonly ZLibStream _deflater;
        private byte[] _previous;
        private readonly byte[] _candidate;
        private readonly byte[] _best;

        public RgbaWriter(int width, int height)
        {
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
            _width = width;
            _height = height;
            _deflater = new ZLibStream(_compressed, CompressionLevel.Optimal, leaveOpen: true);
            _previous = new byte[width * 4];
            _candidate = new byte[(width * 4) + 1];
            _best = new byte[(width * 4) + 1];
        }

        public int RowsWritten { get; private set; }

        public void WriteRow(byte[] rgba)
        {
            if (RowsWritten >= _height) throw new InvalidOperationException("Every row has already been written.");

            long bestScore = long.MaxValue;
            for (byte type = 0; type <= 4; type++)
            {
                long score = Filter(type, rgba, _previous, _candidate);
                if (score < bestScore)
                {
                    bestScore = score;
                    Buffer.BlockCopy(_candidate, 0, _best, 0, _candidate.Length);
                }
            }

            _deflater.Write(_best, 0, _best.Length);
            Buffer.BlockCopy(rgba, 0, _previous, 0, _previous.Length);
            RowsWritten++;
        }

        public byte[] Finish()
        {
            if (RowsWritten != _height) throw new InvalidOperationException("Not every row was written.");
            _deflater.Dispose();

            using MemoryStream file = new();
            file.Write(Signature);

            byte[] ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), _width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), _height);
            ihdr[8] = 8; // bit depth
            ihdr[9] = 6; // RGBA
            WriteChunk(file, "IHDR", ihdr);

            byte[] data = _compressed.ToArray();
            const int ChunkSize = 1 << 18;
            for (int offset = 0; offset < data.Length; offset += ChunkSize)
            {
                WriteChunk(file, "IDAT", data.AsSpan(offset, Math.Min(ChunkSize, data.Length - offset)));
            }

            WriteChunk(file, "IEND", []);
            return file.ToArray();
        }

        public void Dispose()
        {
            _deflater.Dispose();
            _compressed.Dispose();
        }

        private static long Filter(byte type, byte[] row, byte[] previous, byte[] output)
        {
            output[0] = type;
            long score = 0;
            for (int i = 0; i < row.Length; i++)
            {
                int left = i >= 4 ? row[i - 4] : 0;
                int up = previous[i];
                int upLeft = i >= 4 ? previous[i - 4] : 0;
                int predicted = type switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) >> 1,
                    _ => Paeth(left, up, upLeft)
                };

                byte value = (byte)(row[i] - predicted);
                output[i + 1] = value;
                score += value < 128 ? value : 256 - value;
            }

            return score;
        }

        private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
            stream.Write(buffer);

            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);

            uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
            BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
            stream.Write(buffer);
        }
    }

    private static class Crc32
    {
        private static readonly uint[] Table = Build();

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] Build()
        {
            uint[] table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }

            return table;
        }
    }
}
