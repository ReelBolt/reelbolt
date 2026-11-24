using System.Text;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Indexing;

/// <summary>What a project file's bytes are, as far as summarizing and embedding care.</summary>
public enum ProjectFileContentKind
{
    /// <summary>Human-readable text: summarized by the text summarizer and embedded as-is.</summary>
    Text,

    /// <summary>A raster image a vision provider accepts (PNG, JPEG, GIF, WEBP): described once, and the description is embedded.</summary>
    Image,

    /// <summary>Anything else (video, audio, fonts, PDFs, archives, binaries, SVG/HEIC/TIFF/...): never summarized or embedded.</summary>
    Other
}

/// <summary>
/// Pure decision of <see cref="ProjectFileContentKind"/> from the stored MIME type, the file name and
/// the first <see cref="SniffBytes"/> bytes. Both the summarizer and the indexing consumer classify
/// before any model work, so every way a file enters a project is covered.
/// </summary>
public static class ProjectFileContentClassifier
{
    /// <summary>How many leading bytes the classifier looks at. The shared <see cref="ProjectFileTextPolicy"/> owns the value.</summary>
    public const int SniffBytes = ProjectFileTextPolicy.SniffBytes;

    /// <summary>The largest image sent to a vision provider; larger ones are skipped.</summary>
    public const long MaxVisionImageBytes = 20 * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp"
    };

    private static readonly HashSet<string> BinaryApplicationTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/zip",
        "application/x-zip-compressed",
        "application/gzip",
        "application/x-gzip",
        "application/x-tar",
        "application/x-gtar",
        "application/x-7z-compressed",
        "application/x-rar-compressed",
        "application/vnd.rar",
        "application/x-bzip",
        "application/x-bzip2",
        "application/x-xz",
        "application/zstd",
        "application/java-archive",
        "application/wasm",
    };

    /// <summary>
    /// MPEG transport-stream types. The <c>.ts</c> extension is shared with TypeScript, and the common
    /// extension maps (OS MIME databases, <c>FileExtensionContentTypeProvider</c>) label TypeScript with
    /// these, so for them the text sniff decides instead of the <c>video/</c> prefix. A real transport
    /// stream is binary and fails the sniff.
    /// </summary>
    private static readonly HashSet<string> TransportStreamTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp2t",
        "video/vnd.dlna.mpeg-tts",
    };

    public static ProjectFileContentKind Classify(string? mimeType, string? fileName, ReadOnlySpan<byte> head)
    {
        string mime = NormalizeMime(mimeType);
        string? signature = VisionMimeType(head);

        if (mime.StartsWith("image/", StringComparison.Ordinal))
            return signature is not null ? ProjectFileContentKind.Image : ProjectFileContentKind.Other;

        if (signature is not null && (mime.Length == 0 || mime == "application/octet-stream"))
        {
            string extension = Path.GetExtension(fileName ?? string.Empty);
            if (ImageExtensions.Contains(extension))
                return ProjectFileContentKind.Image;
        }

        bool transportStream = TransportStreamTypes.Contains(mime);
        if (!transportStream &&
            (mime.StartsWith("video/", StringComparison.Ordinal) ||
             mime.StartsWith("audio/", StringComparison.Ordinal) ||
             mime.StartsWith("font/", StringComparison.Ordinal) ||
             BinaryApplicationTypes.Contains(mime)))
        {
            return ProjectFileContentKind.Other;
        }

        return DetectTextEncoding(head) is not null ? ProjectFileContentKind.Text : ProjectFileContentKind.Other;
    }

    /// <summary>
    /// The encoding a readable text file is decoded with, from its first bytes; null when the bytes are
    /// not text. Delegated to the shared <see cref="ProjectFileTextPolicy"/>, which owns the sniff.
    /// </summary>
    public static Encoding? DetectTextEncoding(ReadOnlySpan<byte> head) =>
        ProjectFileTextPolicy.DetectTextEncoding(head);

    /// <summary>The MIME type a vision provider should be told, from the image's magic bytes; null when none matches.</summary>
    public static string? VisionMimeType(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return "image/png";
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return "image/jpeg";
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
            return "image/gif";
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    /// <summary>Reads up to <see cref="SniffBytes"/> from the start of the stream. Delegated to the shared policy.</summary>
    public static Task<byte[]> ReadHeadAsync(Stream stream, CancellationToken ct) =>
        ProjectFileTextPolicy.ReadHeadAsync(stream, ct);

    /// <summary>
    /// Decodes a text file whose first bytes were already taken by <see cref="ReadHeadAsync"/>, in the
    /// encoding the sniff picks from the head. Delegated to the shared <see cref="ProjectFileTextPolicy"/>.
    /// </summary>
    internal static Task<(string Text, bool Truncated)> ReadTextAsync(byte[] head, Stream rest, int maxBytes, CancellationToken ct) =>
        ProjectFileTextPolicy.ReadTextAsync(head, rest, maxBytes, ct);

    private static string NormalizeMime(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return string.Empty;
        string value = mimeType;
        int semicolon = value.IndexOf(';');
        if (semicolon >= 0)
            value = value[..semicolon];
        return value.Trim().ToLowerInvariant();
    }
}
