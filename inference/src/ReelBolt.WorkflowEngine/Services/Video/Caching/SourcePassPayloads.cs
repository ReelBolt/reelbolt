using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Workflows.VideoEvents;

namespace ReelBolt.WorkflowEngine.Services.Video.Caching;

/// <summary>
/// The stored form of each cached pass result. One layout for all of them: a short magic, the length of a JSON
/// header, the header, then raw bytes (only the frame grid has any), the whole thing gzipped. A payload is read
/// back in full and checked; anything that is not exactly the expected shape is a miss.
/// </summary>
public static class SourcePassPayloads
{
    /// <summary>The largest result that is stored, before compression. A larger one (a very large grid) is not cached.</summary>
    public const long MaxRawBytes = 256L * 1024 * 1024;

    private const int MaxHeaderBytes = 32 * 1024 * 1024;

    /// <summary>How much of a result is compressed between two looks at the size of the output.</summary>
    private const int EncodeChunkBytes = 1024 * 1024;

    private const string SpansKind = "spans";
    private const string GridKind = "grid";
    private const string EventsKind = "events";

    private static readonly byte[] Magic = "RBPC1\n"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Header(
        string Kind,
        double[][]? Spans = null,
        GridHeader? Grid = null,
        VideoEventSet? Events = null,
        long RawBytes = 0);

    private sealed record GridHeader(int GridWidth, int GridHeight, double EffectiveFps, int FrameCount);

    /// <summary>Time spans (silences, shots), or null when they cannot be stored.</summary>
    public static byte[]? EncodeSpans(IReadOnlyList<(double StartSec, double EndSec)> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        double[][] rows = new double[spans.Count][];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = [spans[i].StartSec, spans[i].EndSec];
        return Encode(new Header(SpansKind, Spans: rows), []);
    }

    public static bool TryDecodeSpans(byte[]? payload, out IReadOnlyList<(double StartSec, double EndSec)> spans)
    {
        spans = [];
        if (!TryDecode(payload, SpansKind, out Header? header, out byte[] raw) || header!.Spans is not { } rows || raw.Length != 0)
            return false;

        var result = new (double StartSec, double EndSec)[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] is not { Length: 2 } row || !double.IsFinite(row[0]) || !double.IsFinite(row[1]))
                return false;
            result[i] = (row[0], row[1]);
        }

        spans = result;
        return true;
    }

    /// <summary>
    /// A sampled colour frame grid (three bytes a cell) with its bytes, or null when it is too large to store:
    /// over <see cref="MaxRawBytes"/> before compression, or over <paramref name="maxStoredBytes"/> after it
    /// (the cache's entry size limit unless a test passes another). The compression stops as soon as the output
    /// passes that limit.
    /// </summary>
    public static byte[]? EncodeGrid(FrameGridResult grid, long maxStoredBytes = SourcePassCache.MaxPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (grid.PixelData is null || grid.PixelData.LongLength > MaxRawBytes)
            return null;
        return Encode(
            new Header(
                GridKind,
                Grid: new GridHeader(grid.GridWidth, grid.GridHeight, grid.EffectiveFps, grid.FrameCount),
                RawBytes: grid.PixelData.LongLength),
            grid.PixelData,
            maxStoredBytes);
    }

    public static bool TryDecodeGrid(byte[]? payload, out FrameGridResult? grid)
    {
        grid = null;
        if (!TryDecode(payload, GridKind, out Header? header, out byte[] raw) || header!.Grid is not { } g)
            return false;

        if (g.GridWidth <= 0 || g.GridHeight <= 0 || g.FrameCount < 0 || !double.IsFinite(g.EffectiveFps))
            return false;

        // The bytes must hold every frame the header counts, so no reader can run past the end. The header is
        // not trusted: the sizes are compared by division, so three large numbers cannot multiply past a long
        // and pass.
        long cells = (long)g.GridWidth * g.GridHeight;
        if (g.FrameCount > 0 && (cells > raw.LongLength / 3 || g.FrameCount > raw.LongLength / (cells * 3)))
            return false;

        grid = new FrameGridResult(raw, g.GridWidth, g.GridHeight, g.EffectiveFps, g.FrameCount);
        return true;
    }

    /// <summary>
    /// A detected event set with every member of every event, or null when it cannot be stored. A set that is
    /// missing a part (<see cref="VideoEventSet.Degraded"/>) is never stored: the next run finds that part again.
    /// </summary>
    public static byte[]? EncodeEvents(VideoEventSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return set.Events is null || set.Degraded ? null : Encode(new Header(EventsKind, Events: set), []);
    }

    public static bool TryDecodeEvents(byte[]? payload, out VideoEventSet? set)
    {
        set = null;
        if (!TryDecode(payload, EventsKind, out Header? header, out byte[] raw) || header!.Events is not { } events || raw.Length != 0)
            return false;
        if (events.Events is null || events.Events.Any(e => e is null))
            return false;

        set = events;
        return true;
    }

    private static byte[]? Encode(Header header, byte[] raw, long maxStoredBytes = SourcePassCache.MaxPayloadBytes)
    {
        byte[] json;
        try
        {
            json = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or JsonException)
        {
            // A number JSON cannot carry (NaN, infinity): the result is simply not cached.
            return null;
        }

        if (json.Length > MaxHeaderBytes || json.LongLength + raw.LongLength > MaxRawBytes)
            return null;

        using MemoryStream stored = new();
        using (GZipStream gzip = new(stored, CompressionLevel.Fastest, leaveOpen: true))
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, json.Length);
            gzip.Write(Magic);
            gzip.Write(length);
            gzip.Write(json);

            // In pieces, with a look at the output after each: a result that will not fit the entry size limit
            // is given up as soon as that shows, instead of being compressed to the end and then thrown away.
            for (int offset = 0; offset < raw.Length; offset += EncodeChunkBytes)
            {
                gzip.Write(raw, offset, Math.Min(EncodeChunkBytes, raw.Length - offset));
                if (stored.Length > maxStoredBytes)
                    return null;
            }
        }

        return stored.Length > maxStoredBytes ? null : stored.ToArray();
    }

    private static bool TryDecode(byte[]? payload, string kind, out Header? header, out byte[] raw)
    {
        header = null;
        raw = [];
        if (payload is not { Length: > 0 })
            return false;

        try
        {
            using MemoryStream stored = new(payload, writable: false);
            using GZipStream gzip = new(stored, CompressionMode.Decompress);

            byte[] prefix = new byte[Magic.Length + 4];
            if (!ReadExactly(gzip, prefix) || !prefix.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                return false;

            int headerLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(Magic.Length));
            if (headerLength <= 0 || headerLength > MaxHeaderBytes)
                return false;

            byte[] json = new byte[headerLength];
            if (!ReadExactly(gzip, json))
                return false;

            Header? parsed = JsonSerializer.Deserialize<Header>(json, JsonOptions);
            if (parsed is null || !string.Equals(parsed.Kind, kind, StringComparison.Ordinal) ||
                parsed.RawBytes < 0 || parsed.RawBytes > MaxRawBytes)
                return false;

            // Exactly the bytes the header counts: a short payload and a long one are both refused.
            byte[] body = new byte[parsed.RawBytes];
            if (!ReadExactly(gzip, body) || gzip.ReadByte() != -1)
                return false;

            header = parsed;
            raw = body;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not gzip, cut short, changed bytes, a header that is not ours: all the same miss.
            return false;
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
                return false;
            read += n;
        }

        return true;
    }
}
