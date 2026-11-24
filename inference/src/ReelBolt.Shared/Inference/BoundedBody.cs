using System.Text;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// S-52: reads a vendor (or BYO endpoint) response body as text, never more than a fixed number of bytes. A hostile
/// endpoint can answer an endless or multi-gigabyte body; <c>ReadAsStringAsync</c> would buffer all of it in engine
/// memory. Reading stops at the cap and the rest is never pulled off the wire (disposing the response aborts it).
/// </summary>
/// <remarks>
/// Pair it with <see cref="HttpCompletionOption.ResponseHeadersRead"/>: the default completion option makes
/// <see cref="HttpClient"/> buffer the whole body before the caller sees it, which defeats any cap applied later.
/// </remarks>
internal static class BoundedBody
{
    /// <summary>Error, probe and status bodies: far above any real vendor error envelope.</summary>
    public const int MaxErrorBytes = 64 * 1024;

    /// <summary>Queue submit/status/result documents (JSON naming URLs, never media bytes).</summary>
    public const int MaxDocumentBytes = 8 * 1024 * 1024;

    /// <summary>
    /// At most <paramref name="maxBytes"/> of the body, decoded as UTF-8. A body over the cap is truncated (a JSON
    /// document so cut fails to parse, which every caller already treats as an unreadable answer).
    /// </summary>
    public static async Task<string> ReadAsync(HttpContent? content, int maxBytes, CancellationToken ct)
    {
        if (content is null) return string.Empty;
        if (content.Headers.ContentLength is 0) return string.Empty;

        await using Stream source = await content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[Math.Min(maxBytes, (int)Math.Min(content.Headers.ContentLength ?? maxBytes, maxBytes))];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await source.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>An error or probe body: <see cref="MaxErrorBytes"/>.</summary>
    public static Task<string> ReadErrorAsync(HttpContent? content, CancellationToken ct) => ReadAsync(content, MaxErrorBytes, ct);

    /// <summary>A queue document: <see cref="MaxDocumentBytes"/>.</summary>
    public static Task<string> ReadDocumentAsync(HttpContent? content, CancellationToken ct) => ReadAsync(content, MaxDocumentBytes, ct);
}
