using System.Net.Http.Headers;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// The two things every image client has to do to a vendor's answer before it can hand a caller
/// pixels: decode a base64 payload, and fetch a URL through the SSRF guard. Shared so MiniMax,
/// OpenAI and BFL cannot drift apart on either.
/// </summary>
/// <remarks>
/// <b>The maximum one image may be.</b> A generated still is normally well under 10 MB, but a 4K
/// FLUX 3 Image is "about 16 megapixels" and a vendor is free to return a large PNG. The cap exists
/// so a hostile or broken endpoint cannot stream an unbounded body into engine memory — the same
/// reasoning (and the same 64 MB order of magnitude) as the generated-clip limit, applied to a
/// much smaller artefact on purpose, because a still is smaller than a clip.
/// </remarks>
internal static class ImagePayload
{
    /// <summary>The largest single image the platform will accept from a vendor: 48 MB.</summary>
    public const long MaxImageBytes = 48L * 1024L * 1024L;

    /// <summary>
    /// The media type of <paramref name="bytes"/>, sniffed from its magic number.
    /// </summary>
    /// <remarks>
    /// Sniffed rather than trusted from a header, because the two disagree: MiniMax documents its
    /// output as JPEG in one place and serves PNG in another, and none of the three vendors state a
    /// per-image media type in the JSON envelope at all. An unrecognised payload returns
    /// <c>"application/octet-stream"</c> rather than a guess — the caller stores the bytes either
    /// way, and a wrong <c>image/png</c> is a worse lie than an honest unknown.
    /// </remarks>
    public static string SniffContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        return "application/octet-stream";
    }

    /// <summary>
    /// Reads the pixel size out of a PNG, JPEG or WebP header, or returns <c>false</c> for anything else
    /// (including a truncated header). There is no raster library in either service and none is added:
    /// this reads a handful of header bytes and decodes no pixels.
    /// </summary>
    /// <remarks>
    /// Exists because several fal outputs state no width or height (the plain <c>File</c> object), and a
    /// layer stored without a size cannot be placed on a canvas. A vendor-stated size always wins over
    /// this; it is only the fallback.
    /// </remarks>
    public static bool TryReadDimensions(ReadOnlySpan<byte> bytes, out int width, out int height) =>
        ImageHeader.TryReadDimensions(bytes, out width, out height);

    /// <summary>Decodes a base64 payload into a <see cref="GeneratedImage"/>, or throws if it is not one.</summary>
    public static GeneratedImage FromBase64(string base64, string? vendorUrl = null)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("The provider returned an image payload that is not valid base64.", ex);
        }

        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("The provider returned an empty image payload.");
        }

        if (bytes.Length > MaxImageBytes)
        {
            throw new InvalidOperationException(
                $"The provider returned a {bytes.Length}-byte image, over the {MaxImageBytes}-byte limit.");
        }

        return new GeneratedImage(bytes, SniffContentType(bytes), VendorUrl: vendorUrl);
    }

    /// <summary>
    /// Fetches a vendor-supplied image URL through <paramref name="downloadClient"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="downloadClient"/> must be the one
    /// <see cref="GeneratedClipUrlGuard.CreateDownloadClient"/> builds: the URL is vendor-supplied
    /// and therefore untrusted, so the syntactic guard runs first and the connect-time DNS check
    /// covers rebinding and every redirect hop. Passing a plain <see cref="HttpClient"/> here turns
    /// the engine into an SSRF primitive aimed at whatever host the vendor's JSON names.
    /// </remarks>
    public static async Task<GeneratedImage> DownloadAsync(HttpClient downloadClient, string url, CancellationToken ct)
    {
        GeneratedClipUrlGuard.EnsureAllowed(url);

        using HttpResponseMessage response = await downloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Downloading the generated image returned HTTP {(int)response.StatusCode}.");
        }

        long? declared = response.Content.Headers.ContentLength;
        if (declared is > MaxImageBytes)
        {
            throw new InvalidOperationException(
                $"The generated image declares {declared.Value} bytes, over the {MaxImageBytes}-byte limit.");
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(ct);
        byte[] bytes = await ReadBoundedAsync(body, MaxImageBytes, ct);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("The generated image downloaded as zero bytes.");
        }

        // Sniffed, not taken from Content-Type: a CDN serves image/jpeg for a PNG often enough that
        // trusting the header would mislabel stored objects. The header is used only as a fallback
        // when the payload is not one of the three formats we can identify.
        string contentType = SniffContentType(bytes);
        if (contentType == "application/octet-stream")
        {
            string? declaredType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(declaredType)) contentType = declaredType;
        }

        return new GeneratedImage(bytes, contentType, VendorUrl: url) { Width = null, Height = null };
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes, throwing once past it.</summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, long limit, CancellationToken ct)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new InvalidOperationException($"The generated image exceeded the {limit}-byte limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// A short, key-scrubbed excerpt of a vendor error body, for an exception message or a probe
    /// reason. Bounded because a vendor is free to return an HTML gateway page, and scrubbed because
    /// some vendors echo the request — headers included — back in the error.
    /// </summary>
    public static string Bounded(string? body, int maxChars = 240)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        string collapsed = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= maxChars ? collapsed : collapsed[..maxChars] + "\u2026";
    }

    /// <summary>Sets a bearer <c>Authorization</c> header, tolerating a blank key by sending none.</summary>
    public static void SetBearer(HttpRequestMessage request, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "This provider has no API key. The stored key is sent verbatim and nothing on this " +
                "path reads an ambient environment variable, so a blank key would otherwise be sent " +
                "as 'Bearer ' and rejected by the vendor with a confusing 401.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }
}
