namespace ReelBolt.Shared.Storage;

/// <summary>
/// The one rule for the <c>.ts</c> family, whose extension is shared by TypeScript sources and MPEG
/// transport streams. The common extension maps (the Windows registry a browser reads,
/// <c>FileExtensionContentTypeProvider</c>, OS MIME databases) label every <c>.ts</c> as
/// <c>video/vnd.dlna.mpeg-tts</c> or <c>video/mp2t</c>, so a repository's TypeScript sources were
/// stored as video, counted as video clips and offered to video steps. For these names the bytes
/// decide: an MPEG-TS packet pattern keeps a video type, readable text becomes text, and anything
/// else keeps what was declared. Every other name is returned unchanged, so this never re-types a
/// file outside the family.
/// </summary>
public static class TransportStreamMimeSniffer
{
    /// <summary>The MIME a file of the family is stored under when its bytes are a transport stream and nothing specific was declared.</summary>
    public const string TransportStreamMimeType = "video/mp2t";

    /// <summary>
    /// The MIME a file of the family is stored under when its bytes are text. The shared text policy
    /// maps <c>.ts</c> to <c>text/plain</c>, and every text reader keys on <c>text/*</c>, so the same
    /// value is used here rather than a TypeScript-specific type no reader expects.
    /// </summary>
    public const string TextMimeType = "text/plain";

    /// <summary>MPEG-TS packet size, and the M2TS (Blu-ray/AVCHD) packet size with its 4-byte timecode prefix.</summary>
    private const int TsPacket = 188;
    private const int M2tsPacket = 192;
    private const byte SyncByte = 0x47;

    /// <summary>
    /// Extensions shared between TypeScript and transport streams: <c>.ts</c>, and <c>.mts</c>/<c>.cts</c>
    /// (ES-module and CommonJS TypeScript; <c>.mts</c> is also AVCHD camcorder video). <c>.m2ts</c> is
    /// only ever video and <c>.tsx</c> only ever TypeScript, so neither is here.
    /// </summary>
    public static IReadOnlySet<string> AmbiguousExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ts", ".mts", ".cts" };

    /// <summary>The types the extension maps hand a <c>.ts</c> file.</summary>
    private static readonly HashSet<string> TransportStreamMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp2t",
        "video/vnd.dlna.mpeg-tts",
        "video/mp2ts",
        "video/x-mp2t",
        "video/avchd-stream",
        "video/x-m2ts",
    };

    /// <summary>True when the name is in the ambiguous family.</summary>
    public static bool IsAmbiguousName(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && AmbiguousExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>True for a transport-stream MIME (parameters ignored).</summary>
    public static bool IsTransportStreamMimeType(string? mimeType) =>
        !string.IsNullOrWhiteSpace(mimeType) && TransportStreamMimeTypes.Contains(mimeType.Split(';')[0].Trim());

    /// <summary>
    /// True when the declared type cannot be trusted for this name and the bytes must be read: the name
    /// is in the family and the type is a transport stream, generic or absent. A specific type that is
    /// neither (a client that already said <c>text/plain</c>) is left alone and costs no read.
    /// </summary>
    public static bool NeedsSniff(string? declaredMimeType, string? fileName)
    {
        if (!IsAmbiguousName(fileName))
            return false;
        if (string.IsNullOrWhiteSpace(declaredMimeType))
            return true;
        string mime = declaredMimeType.Split(';')[0].Trim();
        return IsTransportStreamMimeType(mime)
            || mime.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the leading bytes are an MPEG transport stream: the sync byte <c>0x47</c> at offset 0
    /// and every 188 bytes, or at offset 4 and every 192 bytes (M2TS). At least two packets must be in
    /// view so a text file that merely starts with <c>G</c> is never taken for one; up to eight are checked.
    /// </summary>
    public static bool LooksLikeTransportStream(ReadOnlySpan<byte> head) =>
        HasSyncPattern(head, 0, TsPacket) || HasSyncPattern(head, 4, M2tsPacket);

    private static bool HasSyncPattern(ReadOnlySpan<byte> head, int offset, int stride)
    {
        int packets = 0;
        for (int at = offset; at < head.Length && packets < 8; at += stride)
        {
            if (head[at] != SyncByte)
                return false;
            packets++;
        }

        return packets >= 2;
    }

    /// <summary>
    /// The MIME to store for a file whose leading bytes (up to <see cref="ProjectFileTextPolicy.SniffBytes"/>)
    /// are <paramref name="head"/>. Outside the family, or when <see cref="NeedsSniff"/> says the
    /// declaration stands, the declared type is returned as is. In the family: a transport stream keeps a
    /// declared transport-stream type (else <see cref="TransportStreamMimeType"/>); readable text becomes
    /// <see cref="TextMimeType"/>; any other bytes keep the declared type.
    /// </summary>
    public static string? Resolve(string? declaredMimeType, string? fileName, ReadOnlySpan<byte> head)
    {
        if (!NeedsSniff(declaredMimeType, fileName))
            return declaredMimeType;

        if (LooksLikeTransportStream(head))
            return IsTransportStreamMimeType(declaredMimeType) ? declaredMimeType : TransportStreamMimeType;

        if (ProjectFileTextPolicy.LooksLikeText(head))
            return TextMimeType;

        return declaredMimeType;
    }

    /// <summary>
    /// <see cref="Resolve(string?, string?, ReadOnlySpan{byte})"/> over a stream: reads the head only when
    /// the name needs it, and rewinds a seekable stream so the caller can still store every byte.
    /// </summary>
    public static async Task<string?> ResolveAsync(string? declaredMimeType, string? fileName, Stream content, CancellationToken ct)
    {
        if (!NeedsSniff(declaredMimeType, fileName))
            return declaredMimeType;

        long start = content.CanSeek ? content.Position : 0;
        byte[] head = await ProjectFileTextPolicy.ReadHeadAsync(content, ct);
        if (content.CanSeek)
            content.Position = start;
        return Resolve(declaredMimeType, fileName, head);
    }
}
