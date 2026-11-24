using System.Text;
using Microsoft.AspNetCore.Http;

namespace ReelBolt.Inference.Api.Services.Storage;

/// <summary>
/// The content type and disposition a stored object is served with, decided by this policy and never
/// by the client or by the row's stored MIME alone.
/// </summary>
public sealed record ServedContent(string ContentType, bool Inline);

/// <summary>
/// One place decides how customer bytes leave the origin. An uploaded HTML or SVG file can carry a
/// lying MIME type; serving it inline under that type is a stored-XSS primitive, so a type is served
/// inline only when it is on a small allowlist AND the file name's extension agrees with it. Every
/// other type is <c>application/octet-stream</c> and an attachment.
/// </summary>
public static class SafeContentPolicy
{
    private static readonly Dictionary<string, string[]> InlineExtensions = new(StringComparer.Ordinal)
    {
        ["image/png"] = ["png"],
        ["image/jpeg"] = ["jpg", "jpeg"],
        ["image/gif"] = ["gif"],
        ["image/webp"] = ["webp"],
        ["video/mp4"] = ["mp4", "m4v"],
        ["video/webm"] = ["webm"],
        ["video/quicktime"] = ["mov"],
        ["audio/mpeg"] = ["mp3"],
        ["audio/wav"] = ["wav"],
        ["audio/x-wav"] = ["wav"],
        ["audio/ogg"] = ["ogg", "oga"],
        ["audio/aac"] = ["aac"],
        ["audio/mp4"] = ["m4a"],
        ["audio/webm"] = ["webm"]
    };

    private static readonly ServedContent Fallback = new("application/octet-stream", false);

    /// <summary>
    /// Inline only when the normalized MIME is on the allowlist and <paramref name="fileName"/>'s
    /// extension is one of that MIME's allowed extensions.
    /// </summary>
    public static ServedContent Decide(string? storedMimeType, string fileName)
    {
        string mime = Norm(storedMimeType);
        if (!InlineExtensions.TryGetValue(mime, out string[]? extensions)) return Fallback;

        string extension = Extension(fileName);
        return extensions.Contains(extension, StringComparer.Ordinal) ? new ServedContent(mime, true) : Fallback;
    }

    /// <summary>No file name is known (editor layers), so the MIME alone decides.</summary>
    public static ServedContent DecideByMime(string? mime)
    {
        string normalized = Norm(mime);
        return InlineExtensions.ContainsKey(normalized) ? new ServedContent(normalized, true) : Fallback;
    }

    /// <summary>
    /// Strips the header-injection primitives (a quote, a backslash, a slash, CR, LF and every control
    /// character), trims, caps at 150 characters and never returns an empty name.
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "file";

        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c is '"' or '\\' or '/') continue;
            if (c < 0x20 || c == 0x7F) continue;
            builder.Append(c);
        }

        string cleaned = builder.ToString().Trim();
        if (cleaned.Length > 150) cleaned = cleaned[..150];
        return cleaned.Length == 0 ? "file" : cleaned;
    }

    /// <summary>
    /// A download name the user chose, made safe and given the stored object's extension when it lacks it
    /// (<c>reel</c> → <c>reel.mp4</c>); a blank name falls back to the stored name.
    /// </summary>
    public static string WithExtensionOf(string? chosenName, string storedFileName)
    {
        string clean = SanitizeFileName(chosenName);
        if (string.IsNullOrWhiteSpace(chosenName)) return SanitizeFileName(storedFileName);
        string extension = Path.GetExtension(storedFileName);
        if (extension.Length == 0 || clean.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return clean;
        string joined = clean + extension;
        return joined.Length > 150 ? clean[..(150 - extension.Length)] + extension : joined;
    }

    /// <summary>
    /// Builds an RFC 6266/5987 disposition. The quoted <c>filename</c> is forced to ASCII (Kestrel
    /// throws on a non-ASCII header value) and the exact name travels in <c>filename*</c>.
    /// </summary>
    public static string BuildContentDisposition(bool inline, string? fileName)
    {
        string clean = SanitizeFileName(fileName);

        var asciiBuilder = new StringBuilder(clean.Length);
        foreach (char c in clean) asciiBuilder.Append(c > 0x7E ? '_' : c);
        string ascii = asciiBuilder.ToString();

        string disposition = inline ? "inline" : "attachment";
        return $"{disposition}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(clean)}";
    }

    /// <summary>
    /// Anti-sniffing and containment headers for a stream of customer bytes. SET, never appended, so a
    /// repeated call cannot leave two values.
    /// </summary>
    public static void ApplySecurityHeaders(HttpResponse response)
    {
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static string Norm(string? mime) =>
        mime?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;

    private static string Extension(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return string.Empty;
        return Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
    }
}
