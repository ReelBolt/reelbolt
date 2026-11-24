using System.Buffers;
using System.Globalization;
using System.Text;

namespace ReelBolt.Shared.Storage;

/// <summary>
/// The one rule for which stored project files are text a tool may read, and the one place the
/// extension allowlist and the extension-to-MIME map live. The four project-file surfaces
/// (engine workspace, dashboard controller, assistant file tools, brief resolver) and the upload
/// MIME resolver all delegate here, so the read allowlist and the upload MIME map cannot drift.
/// </summary>
public static class ProjectFileTextPolicy
{
    /// <summary>
    /// The dotted, case-insensitive extensions admitted to the text path. This is the engine's
    /// original set plus SCSS/CSS-preprocessor sources, .NET project and template files, config and
    /// shell/sql sources, and the common scripting languages. <c>.svg</c> is deliberately absent: it
    /// is an image, and the <c>xml</c> MIME substring rule is what would otherwise admit it.
    /// </summary>
    public static IReadOnlySet<string> TextExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // The engine's original set.
        ".ts", ".tsx", ".js", ".jsx", ".json", ".md", ".txt", ".css", ".html", ".cs",
        ".yml", ".yaml", ".csv", ".xml",
        // SCSS / CSS-preprocessor sources (the reported defect).
        ".scss", ".sass", ".less",
        // .NET project, template and build files.
        ".csproj", ".razor", ".cshtml", ".config", ".props", ".targets", ".editorconfig",
        // Config, shell and data sources.
        ".sh", ".ps1", ".sql", ".toml", ".ini",
        // Markdown/JS variants and other text-only frontend sources.
        ".mdx", ".mjs", ".cjs", ".vue", ".svelte", ".astro",
        // Common scripting languages.
        ".py", ".rb", ".go", ".rs", ".java", ".kt", ".php", ".swift", ".dart", ".lua", ".r", ".fs", ".vb"
    };

    /// <summary>
    /// The script, build and configuration extensions the model-driven authoring tools must not
    /// author, even though a human may read and edit them through the dashboard: producing a build
    /// input or a shell script is a privilege a reading model does not have.
    /// </summary>
    private static readonly HashSet<string> WriteExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sh", ".ps1", ".csproj", ".props", ".targets", ".razor", ".cshtml", ".config"
    };

    /// <summary>
    /// The extensions the model-driven authoring tools (the engine's <c>EditProjectFile</c> and
    /// <c>WriteProjectFile</c>, and the assistant's write proposal) may write:
    /// <see cref="TextExtensions"/> minus <see cref="WriteExcludedExtensions"/>. Always a subset of
    /// the read set, so nothing a model authors is something a tool would then refuse to read.
    /// </summary>
    public static IReadOnlySet<string> WriteExtensions { get; } =
        new HashSet<string>(
            TextExtensions.Where(extension => !WriteExcludedExtensions.Contains(extension)),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The specific MIME for the families that have one. Every other allowlisted extension resolves
    /// to <c>text/plain</c> through the fallback in <see cref="MimeTypeForExtension"/>, so adding an
    /// extension to <see cref="TextExtensions"/> can never leave it without a MIME.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> ExtensionMimeTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // XML-shaped project and config files.
            [".csproj"] = "application/xml",
            [".props"] = "application/xml",
            [".targets"] = "application/xml",
            [".config"] = "application/xml",
            // JavaScript module variants.
            [".mjs"] = "text/javascript",
            [".cjs"] = "text/javascript",
            // Markdown variants.
            [".md"] = "text/markdown",
            [".mdx"] = "text/markdown",
        };

    /// <summary>
    /// True when a stored file's MIME and name say it is text. An explicit <c>text/*</c> is text; an
    /// audio/video/image MIME is refused before the JSON/XML/JavaScript substring rule so an image
    /// (notably <c>image/svg+xml</c>, which contains "xml") never enters the text path; then a
    /// JSON/XML/JavaScript MIME is text; otherwise the extension allowlist decides. A null or blank
    /// MIME falls through to the extension. The four delegating predicates answer exactly this.
    /// </summary>
    public static bool IsLikelyText(string? mimeType, string? fileName)
    {
        if (!string.IsNullOrEmpty(mimeType))
        {
            if (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                return true;
            if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return false;
            if (mimeType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                mimeType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
                mimeType.Contains("javascript", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return fileName is not null && TextExtensions.Contains(Path.GetExtension(fileName));
    }

    /// <summary>
    /// True when a model-driven authoring tool may write a file with this name: an <b>admission</b>
    /// check, not a deny list. The name must be the bare name the writer will actually store — a path
    /// separator means the gate would be inspecting a different string than the writer stores, so a
    /// path is refused outright — and its extension must be one of <see cref="WriteExtensions"/>. The
    /// MIME type is deliberately not a parameter: no caller-supplied <c>text/plain</c> (or
    /// <c>application/xml</c>) can admit a script, build or config name, and an extensionless name (a
    /// <c>Makefile</c>) is refused because an extension set cannot admit it. The read surfaces keep
    /// the broad <see cref="IsLikelyText"/> set — this narrower rule applies only to the model's
    /// authoring tools.
    /// </summary>
    public static bool IsWritableText(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return false;

        // The caller must pass the name that will actually be stored: a bare file name. A path
        // separator means the gate would be inspecting a different string than the writer stores.
        if (fileName.Contains('/') || fileName.Contains('\\'))
            return false;
        if (fileName is "." or "..")
            return false;

        string extension = Path.GetExtension(fileName);
        return extension.Length > 0 && WriteExtensions.Contains(extension);
    }

    /// <summary>
    /// The MIME to store a text file under: a specific one for a known family, else <c>text/plain</c>
    /// for any allowlisted extension, else null for an extension outside the allowlist.
    /// </summary>
    public static string? MimeTypeForExtension(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;

        string extension = Path.GetExtension(fileName);
        if (!TextExtensions.Contains(extension))
            return null;

        return ExtensionMimeTypes.TryGetValue(extension, out string? mime) ? mime : "text/plain";
    }

    /// <summary>The MIME prefixes of the unambiguous binary families.</summary>
    private static readonly string[] KnownBinaryMimePrefixes = ["audio/", "video/", "image/", "font/"];

    /// <summary>The archive and document application types that are never text.</summary>
    private static readonly HashSet<string> KnownBinaryMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "application/zip", "application/x-zip-compressed", "application/gzip",
        "application/x-gzip", "application/x-tar", "application/x-7z-compressed",
        "application/x-rar-compressed", "application/vnd.rar", "application/x-bzip2",
        "application/x-xz", "application/zstd", "application/java-archive", "application/wasm"
    };

    /// <summary>
    /// The compact, unambiguous binary extensions. Deliberately excludes every extension that can
    /// legitimately be text (<c>.map</c>, <c>.bin</c>, <c>.dat</c>, <c>.log</c>, <c>.md</c>, ...).
    /// </summary>
    private static readonly HashSet<string> KnownBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Audio.
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".oga",
        // Video.
        ".mp4", ".m4v", ".mov", ".webm", ".mkv", ".avi", ".wmv", ".flv",
        // Images.
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".heic", ".tiff", ".svg",
        // Archives and documents.
        ".pdf", ".zip", ".gz", ".tgz", ".tar", ".7z", ".rar", ".bz2", ".xz", ".zst", ".wasm",
        ".jar", ".class",
        // Fonts.
        ".ttf", ".otf", ".woff", ".woff2",
        // Office documents.
        ".docx", ".xlsx", ".pptx",
        // Executables and native libraries.
        ".exe", ".dll", ".so", ".dylib"
    };

    /// <summary>
    /// True when a file's declared MIME or name puts it in an unambiguous binary family, so it can be
    /// refused before a single byte is fetched. This is a declaration gate, not an admission rule: it
    /// only ever refuses, and the byte sniff still decides every file it does not refuse. A null or
    /// blank MIME falls through to the extension.
    /// </summary>
    public static bool IsKnownBinary(string? mimeType, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            string mime = mimeType.Split(';')[0].Trim();
            foreach (string prefix in KnownBinaryMimePrefixes)
            {
                if (mime.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (KnownBinaryMimeTypes.Contains(mime))
                return true;
        }

        return !string.IsNullOrEmpty(fileName) && KnownBinaryExtensions.Contains(Path.GetExtension(fileName));
    }

    /// <summary>How many leading bytes the sniff looks at.</summary>
    public const int SniffBytes = 8192;

    /// <summary>The most bytes <see cref="ReadTextAsync"/> will decode for one read (16 MiB).</summary>
    public const int MaxReadBytes = 16 * 1024 * 1024;

    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    private static readonly Encoding Utf32BigEndian = new UTF32Encoding(bigEndian: true, byteOrderMark: true);

    /// <summary>
    /// True when the leading bytes of a file look like human-readable text, by the same rule
    /// <see cref="DetectTextEncoding"/> uses: a recognised encoding is text, no encoding is binary. The
    /// extension is deliberately not consulted, so a text file whose extension nobody listed is admitted
    /// and a genuine binary wearing an allowlisted extension is refused.
    /// </summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> head) => DetectTextEncoding(head) is not null;

    /// <summary>
    /// True when a whole decoded string is plausible text: it holds no U+0000 and under 10% of its
    /// runes are implausible (see <see cref="IsImplausible"/>). The head sniff sees at most
    /// <see cref="SniffBytes"/>; this re-runs the same test over everything that was decoded, so a
    /// text head on a binary body is refused rather than returned whole.
    /// </summary>
    public static bool LooksLikeDecodedText(string text)
    {
        if (text.Contains('\0'))
            return false;

        int runes = 0;
        int bad = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            runes++;
            if (IsImplausible(rune))
                bad++;
        }

        return runes == 0 || bad * 10 < runes;
    }

    /// <summary>
    /// The encoding a readable text file is decoded with, from its first bytes; null when the bytes are
    /// not text. Order: a UTF-32/UTF-16/UTF-8 byte-order mark decides; else NUL bytes are allowed only
    /// as the zero half of BOM-less mostly-ASCII UTF-16 (LE or BE); else strict UTF-8; else
    /// Windows-1252. Every non-UTF-8 candidate must decode with no U+0000 and under 10% implausible
    /// code points (see <see cref="Readable"/>), so real binaries stay out.
    /// </summary>
    public static Encoding? DetectTextEncoding(ReadOnlySpan<byte> head)
    {
        if (head.IsEmpty)
            return Encoding.UTF8;

        bool full = head.Length >= SniffBytes;
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
            return Readable(Encoding.UTF32, head[4..], 4);
        if (head.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
            return Readable(Utf32BigEndian, head[4..], 4);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return Readable(Encoding.Unicode, head[2..], 2);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return Readable(Encoding.BigEndianUnicode, head[2..], 2);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return IsUtf8Text(head[3..], full) ? Encoding.UTF8 : null;

        if (head.Contains((byte)0))
        {
            Encoding? wide = Utf16WithoutBom(head);
            return wide is null ? null : Readable(wide, head, 2);
        }

        return IsUtf8Text(head, full) ? Encoding.UTF8 : Readable(Windows1252, head, 1);
    }

    /// <summary>Reads up to <see cref="SniffBytes"/> from the start of the stream, looping over short reads until full or EOF.</summary>
    public static async Task<byte[]> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[SniffBytes];
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0)
                break;
            filled += read;
        }

        return filled == buffer.Length ? buffer : buffer[..filled];
    }

    /// <summary>
    /// Decodes a text file whose first bytes were already taken by <see cref="ReadHeadAsync"/>: the
    /// head followed by the rest of the stream, read the same way a <see cref="StreamReader"/> over the
    /// whole file would, in the encoding <see cref="DetectTextEncoding"/> picks from the head (UTF-8
    /// when it picks none), BOM detection on. The result is UTF-8-ready .NET text whatever the source
    /// encoding was.
    /// </summary>
    public static async Task<(string Text, bool Truncated)> ReadTextAsync(byte[] head, Stream rest, int maxBytes, CancellationToken ct)
    {
        Encoding encoding = DetectTextEncoding(head) ?? Encoding.UTF8;
        using var whole = new MemoryStream();

        // Bound the read: never pull more than maxBytes total (head + rest) into memory.
        // The sniff head may itself exceed a small cap, so only its first maxBytes bytes
        // contribute to the content; the rest of the stream then fills the remainder.
        int headBytes = Math.Min(head.Length, maxBytes);
        bool truncated = head.Length > maxBytes;
        await whole.WriteAsync(head.AsMemory(0, headBytes), ct);

        long remaining = (long)maxBytes - headBytes;
        byte[] buffer = new byte[8192];
        long copied = 0;
        while (copied < remaining)
        {
            int want = (int)Math.Min(buffer.Length, remaining - copied);
            int read = await rest.ReadAsync(buffer.AsMemory(0, want), ct);
            if (read == 0)
                break;
            await whole.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
        }

        // If the budget is full, read one more byte to learn whether we actually truncated.
        if (copied >= remaining)
        {
            int extra = await rest.ReadAsync(buffer.AsMemory(0, 1), ct);
            truncated |= extra > 0;
        }

        whole.Position = 0;
        using StreamReader reader = new(whole, encoding, detectEncodingFromByteOrderMarks: true);
        string text = await reader.ReadToEndAsync(ct);
        return (text, truncated);
    }

    private static bool IsUtf8Text(ReadOnlySpan<byte> head, bool full)
    {
        if (head.Contains((byte)0))
            return false;

        int runes = 0;
        int controls = 0;
        while (!head.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf8(head, out Rune rune, out int consumed);
            if (status == OperationStatus.NeedMoreData && full)
                break; // a multi-byte sequence cut off by the end of the sniff window
            if (status != OperationStatus.Done)
                return false;

            runes++;
            if (IsDisallowedControl(rune))
                controls++;
            head = head[consumed..];
        }

        return runes == 0 || controls * 10 < runes;
    }

    /// <summary>
    /// UTF-16 without a byte-order mark, recognised only when it is mostly ASCII: at least 70% of the
    /// code units have a zero high byte on one side and almost none (at most 2%) on the other.
    /// </summary>
    private static Encoding? Utf16WithoutBom(ReadOnlySpan<byte> head)
    {
        int units = head.Length / 2;
        if (units == 0)
            return null;

        int evenZeros = 0;
        int oddZeros = 0;
        for (int i = 0; i < units; i++)
        {
            if (head[2 * i] == 0)
                evenZeros++;
            if (head[2 * i + 1] == 0)
                oddZeros++;
        }

        if (oddZeros * 10 >= units * 7 && evenZeros * 50 <= units)
            return Encoding.Unicode;
        if (evenZeros * 10 >= units * 7 && oddZeros * 50 <= units)
            return Encoding.BigEndianUnicode;
        return null;
    }

    /// <summary>
    /// Decodes <paramref name="bytes"/> (trimmed to whole code units) and accepts the encoding when the
    /// text has no U+0000 and under 10% implausible code points: controls, replacement characters (lone
    /// surrogates), private-use and unassigned ones. Random bytes read as UTF-16 land on those about
    /// one time in five; real text almost never. A code unit cut off by the end of the sniff window
    /// costs at most one replacement character.
    /// </summary>
    private static Encoding? Readable(Encoding encoding, ReadOnlySpan<byte> bytes, int unitBytes)
    {
        bytes = bytes[..(bytes.Length - bytes.Length % unitBytes)];
        string text = encoding.GetString(bytes);
        if (text.Contains('\0'))
            return null;

        int runes = 0;
        int bad = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            runes++;
            if (IsImplausible(rune))
                bad++;
        }

        return runes == 0 || bad * 10 < runes ? encoding : null;
    }

    /// <summary>
    /// A rune that is not plausible in human-readable text: a replacement character (a lone surrogate),
    /// a disallowed control, a private-use one or an unassigned one. Random bytes land on these about
    /// one time in five; real text almost never.
    /// </summary>
    private static bool IsImplausible(Rune rune) =>
        rune == Rune.ReplacementChar || IsDisallowedControl(rune) ||
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;

    private static bool IsDisallowedControl(Rune rune) =>
        Rune.IsControl(rune) && rune.Value is not ('\t' or '\n' or '\r' or '\f');
}
