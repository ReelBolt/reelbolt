using System.Text;

namespace ReelBolt.Shared.IO;

/// <summary>
/// The bounded result of reading a stream as UTF-8 text: what was decoded, whether the stream held
/// more than the cap allowed, and how many bytes went into <see cref="Text"/>.
/// </summary>
public sealed record BoundedText(string Text, bool Truncated, long BytesRead);

/// <summary>
/// Reads a stream as UTF-8 text without ever holding more than a fixed number of bytes in memory.
/// </summary>
/// <remarks>
/// The upload limit is many terabytes, so a whole-file <c>ReadToEndAsync</c> lets one big file
/// exhaust the API process. This reader pulls at most <c>maxBytes + 1</c> bytes from the stream and
/// buffers at most that many, which is how it can still report truncation while keeping the cap.
/// Decoding replaces invalid bytes with U+FFFD and never throws; a leading UTF-8 BOM is dropped.
/// </remarks>
public static class BoundedTextReader
{
    private const int ChunkSize = 8192;

    public static async Task<BoundedText> ReadAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        int bufferSize = Math.Min(ChunkSize, maxBytes + 1);
        byte[] buffer = new byte[bufferSize];
        using MemoryStream buffered = new();

        long limit = (long)maxBytes + 1;
        long total = 0;

        while (total < limit)
        {
            int want = (int)Math.Min(buffer.Length, limit - total);
            int read = await stream.ReadAsync(buffer.AsMemory(0, want), ct);
            if (read == 0)
                break;

            buffered.Write(buffer, 0, read);
            total += read;
        }

        bool truncated = total > maxBytes;
        int use = (int)Math.Min(total, maxBytes);

        byte[] bytes = buffered.GetBuffer();
        int start = 0;
        if (use >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            start = 3;

        int length = use - start;
        string text = length > 0
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
                .GetString(bytes, start, length)
            : string.Empty;

        return new BoundedText(text, truncated, use);
    }
}
