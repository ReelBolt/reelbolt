namespace ReelBolt.Inference.Api.Services.Csam;

/// <summary>What a small head read says the bytes are.</summary>
internal enum CsamSniffedKind
{
    Image,
    Media,
    Text,
    UnknownBinary
}

/// <summary>
/// Magic-byte sniffing for the CSAM control. A caller-declared MIME type is a hint, never the
/// decision: an unknown or <c>application/octet-stream</c> upload is classified from its bytes, so a
/// lying type cannot keep content away from the provider. Unknown binary is treated as media — only
/// Project Arachnid ever sees it.
/// </summary>
internal static class CsamMimeSniffer
{
    /// <summary>How many bytes the matcher reads for a sniff. Enough for every magic below.</summary>
    internal const int HeadBytes = 8192;

    /// <summary>
    /// The content type the magic bytes name, used when the caller's declared type was absent or a
    /// lie. Null for unknown binary, which is sent as <c>application/octet-stream</c>.
    /// </summary>
    public static string? DetectContentType(ReadOnlySpan<byte> head)
    {
        if (IsPng(head)) return "image/png";
        if (IsJpeg(head)) return "image/jpeg";
        if (StartsWith(head, "GIF87a"u8) || StartsWith(head, "GIF89a"u8)) return "image/gif";
        if (StartsWith(head, "BM"u8)) return "image/bmp";
        if (StartsWith(head, [0x49, 0x49, 0x2A, 0x00]) || StartsWith(head, [0x4D, 0x4D, 0x00, 0x2A])) return "image/tiff";
        if (head.Length >= 12 && StartsWith(head, "RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8)) return "video/mp4";
        if (StartsWith(head, [0x1A, 0x45, 0xDF, 0xA3])) return "video/webm";
        if (head.Length >= 12 && StartsWith(head, "RIFF"u8) && head.Slice(8, 4).SequenceEqual("AVI "u8)) return "video/x-msvideo";
        if (head.Length >= 12 && StartsWith(head, "RIFF"u8) && head.Slice(8, 4).SequenceEqual("WAVE"u8)) return "audio/wav";
        if (StartsWith(head, [0x00, 0x00, 0x01, 0xBA]) || StartsWith(head, [0x00, 0x00, 0x01, 0xB3])) return "video/mpeg";
        if (StartsWith(head, "OggS"u8)) return "audio/ogg";
        if (StartsWith(head, "fLaC"u8)) return "audio/flac";
        if (StartsWith(head, "ID3"u8) || (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)) return "audio/mpeg";
        return null;
    }

    public static CsamSniffedKind Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0) return CsamSniffedKind.Text;

        if (IsImage(head)) return CsamSniffedKind.Image;
        if (IsMedia(head)) return CsamSniffedKind.Media;

        // PDF and the zip container (Office/open-document) are known non-media documents.
        if (StartsWith(head, "%PDF"u8) || StartsWith(head, "PK\x03\x04"u8)
            || StartsWith(head, "PK\x05\x06"u8) || StartsWith(head, "PK\x07\x08"u8))
        {
            return CsamSniffedKind.Text;
        }

        return LooksLikeText(head) ? CsamSniffedKind.Text : CsamSniffedKind.UnknownBinary;
    }

    private static bool IsJpeg(ReadOnlySpan<byte> head) => StartsWith(head, [0xFF, 0xD8, 0xFF]);

    private static bool IsPng(ReadOnlySpan<byte> head) =>
        StartsWith(head, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    private static bool IsImage(ReadOnlySpan<byte> head) =>
        IsJpeg(head)
        || IsPng(head)
        || StartsWith(head, "GIF87a"u8)
        || StartsWith(head, "GIF89a"u8)
        || StartsWith(head, "BM"u8)
        || StartsWith(head, [0x49, 0x49, 0x2A, 0x00])
        || StartsWith(head, [0x4D, 0x4D, 0x00, 0x2A])
        || (head.Length >= 12 && StartsWith(head, "RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8));

    private static bool IsMedia(ReadOnlySpan<byte> head) =>
        (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8))          // mp4 / mov
        || StartsWith(head, [0x1A, 0x45, 0xDF, 0xA3])                            // webm / mkv
        || (head.Length >= 12 && StartsWith(head, "RIFF"u8) && head.Slice(8, 4).SequenceEqual("AVI "u8))
        || (head.Length >= 12 && StartsWith(head, "RIFF"u8) && head.Slice(8, 4).SequenceEqual("WAVE"u8))
        || StartsWith(head, [0x00, 0x00, 0x01, 0xBA])
        || StartsWith(head, [0x00, 0x00, 0x01, 0xB3])
        || StartsWith(head, "OggS"u8)
        || StartsWith(head, "fLaC"u8)
        || StartsWith(head, "ID3"u8)
        || (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0);    // mp3 frame

    private static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        foreach (byte b in head)
        {
            if (b == 0) return false;
        }

        int printable = 0;
        foreach (byte b in head)
        {
            if (b is 9 or 10 or 13 || (b >= 0x20 && b <= 0x7E) || b >= 0x80) printable++;
        }

        return printable >= head.Length * 0.9;
    }

    private static bool StartsWith(ReadOnlySpan<byte> head, ReadOnlySpan<byte> magic) =>
        head.Length >= magic.Length && head[..magic.Length].SequenceEqual(magic);
}
