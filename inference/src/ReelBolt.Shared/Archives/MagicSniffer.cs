using System.Text;

namespace ReelBolt.Shared.Archives;

/// <summary>
/// What a byte prefix looks like once magic bytes and a bounded textual probe have been applied.
/// Ordering is intentionally arbitrary: callers map the possible kinds to a decision, they never
/// compare kinds for magnitude.
/// </summary>
public enum MagicKind
{
    Unknown,
    Png,
    Jpeg,
    WebP,
    Gif,
    Pdf,
    Zip,
    Gzip,
    SevenZip,
    Rar,
    Bzip2,
    Xz,
    Elf,
    PortableExecutable,
    MachO,
    Wasm,
    Html,
    Svg,
    Xml,
    Text,
    Woff,
    Woff2,
    TrueType,
    OpenType,
}

/// <summary>
/// Pure, allocation-light classification of a file's leading bytes. It is deliberately never
/// extension-aware: an attacker controls the name, so only the bytes decide. Textual detection skips
/// an optional UTF-8 BOM and ASCII whitespace, looks at a bounded prefix for markup, and otherwise
/// accepts the head only when it is valid UTF-8 with no NUL and no control byte beyond tab, LF and CR.
/// </summary>
public static class MagicSniffer
{
    private const int InspectBytes = 4096;
    private const int PrefixBytes = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Classifies <paramref name="head"/>. Pure: it never throws and never allocates the file.</summary>
    public static MagicKind Sniff(ReadOnlySpan<byte> head)
    {
        // Binary signatures first, exactly and only at offset zero.
        if (Matches(head, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return MagicKind.Png;
        if (Matches(head, 0xFF, 0xD8, 0xFF)) return MagicKind.Jpeg;
        if (IsWebP(head)) return MagicKind.WebP;
        if (MatchesAscii(head, "GIF8")) return MagicKind.Gif;
        if (MatchesAscii(head, "%PDF-")) return MagicKind.Pdf;
        if (Matches(head, 0x50, 0x4B, 0x03, 0x04) || Matches(head, 0x50, 0x4B, 0x05, 0x06)) return MagicKind.Zip;
        if (Matches(head, 0x1F, 0x8B)) return MagicKind.Gzip;
        if (Matches(head, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return MagicKind.SevenZip;
        if (MatchesAscii(head, "Rar!")) return MagicKind.Rar;
        if (MatchesAscii(head, "BZh")) return MagicKind.Bzip2;
        if (Matches(head, 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00)) return MagicKind.Xz;
        if (Matches(head, 0x7F, 0x45, 0x4C, 0x46)) return MagicKind.Elf;
        if (Matches(head, 0x4D, 0x5A)) return MagicKind.PortableExecutable;
        if (IsMachO(head)) return MagicKind.MachO;
        if (Matches(head, 0x00, 0x61, 0x73, 0x6D)) return MagicKind.Wasm;

        // Font containers are binary (and `wOFF`/`OTTO` would otherwise pass the plain-text probe).
        if (MatchesAscii(head, "wOFF")) return MagicKind.Woff;
        if (MatchesAscii(head, "wOF2")) return MagicKind.Woff2;
        if (Matches(head, 0x00, 0x01, 0x00, 0x00)) return MagicKind.TrueType;
        if (MatchesAscii(head, "OTTO")) return MagicKind.OpenType;

        MagicKind markup = SniffMarkup(head);
        if (markup != MagicKind.Unknown)
            return markup;

        return IsPlainText(head) ? MagicKind.Text : MagicKind.Unknown;
    }

    private static MagicKind SniffMarkup(ReadOnlySpan<byte> head)
    {
        int start = 0;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            start = 3;

        while (start < head.Length && IsAsciiWhitespace(head[start]))
            start++;

        ReadOnlySpan<byte> rest = head[start..];
        int take = Math.Min(rest.Length, PrefixBytes);
        Span<byte> lowered = stackalloc byte[PrefixBytes];
        for (int i = 0; i < take; i++)
            lowered[i] = ToLowerAscii(rest[i]);
        ReadOnlySpan<byte> prefix = lowered[..take];

        if (StartsWithAscii(prefix, "<!doctype html")
            || StartsWithAscii(prefix, "<html")
            || StartsWithAscii(prefix, "<head")
            || StartsWithAscii(prefix, "<body")
            || StartsWithAscii(prefix, "<script")
            || StartsWithAscii(prefix, "<iframe")
            || StartsWithAscii(prefix, "<?php"))
        {
            return MagicKind.Html;
        }

        if (StartsWithAscii(prefix, "<svg"))
            return MagicKind.Svg;

        if (StartsWithAscii(prefix, "<?xml"))
            return ContainsAsciiIgnoreCase(head, "<svg") ? MagicKind.Svg : MagicKind.Xml;

        return MagicKind.Unknown;
    }

    private static bool IsPlainText(ReadOnlySpan<byte> head)
    {
        int inspect = Math.Min(head.Length, InspectBytes);
        ReadOnlySpan<byte> candidate = head[..inspect];

        // A multi-byte character cut by the 4096-byte probe boundary is not an invalid file.
        if (!IsValidUtf8(candidate) && head.Length > InspectBytes)
        {
            for (int trim = 1; trim <= 3 && inspect - trim > 0; trim++)
            {
                if (IsValidUtf8(head[..(inspect - trim)]))
                {
                    candidate = head[..(inspect - trim)];
                    break;
                }
            }
        }

        if (!IsValidUtf8(candidate))
            return false;

        foreach (byte b in candidate)
        {
            if (b == 0x00)
                return false;
            if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D)
                return false;
            if (b == 0x7F)
                return false;
        }

        return true;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            StrictUtf8.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsWebP(ReadOnlySpan<byte> head)
        => head.Length >= 12
           && MatchesAscii(head, "RIFF")
           && head[8] == (byte)'W'
           && head[9] == (byte)'E'
           && head[10] == (byte)'B'
           && head[11] == (byte)'P';

    private static bool IsMachO(ReadOnlySpan<byte> head)
        => Matches(head, 0xCF, 0xFA, 0xED, 0xFE)
           || Matches(head, 0xCE, 0xFA, 0xED, 0xFE)
           || Matches(head, 0xFE, 0xED, 0xFA, 0xCE)
           || Matches(head, 0xFE, 0xED, 0xFA, 0xCF);

    private static bool Matches(ReadOnlySpan<byte> head, params byte[] signature)
    {
        if (head.Length < signature.Length)
            return false;
        for (int i = 0; i < signature.Length; i++)
        {
            if (head[i] != signature[i])
                return false;
        }

        return true;
    }

    private static bool MatchesAscii(ReadOnlySpan<byte> head, string value)
    {
        if (head.Length < value.Length)
            return false;
        for (int i = 0; i < value.Length; i++)
        {
            if (head[i] != (byte)value[i])
                return false;
        }

        return true;
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> head, string value)
    {
        if (head.Length < value.Length)
            return false;
        for (int i = 0; i < value.Length; i++)
        {
            if (head[i] != (byte)value[i])
                return false;
        }

        return true;
    }

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> head, string value)
    {
        int limit = Math.Min(head.Length, InspectBytes);
        for (int i = 0; i + value.Length <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < value.Length; j++)
            {
                if (ToLowerAscii(head[i + j]) != (byte)value[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return true;
        }

        return false;
    }

    private static byte ToLowerAscii(byte value)
        => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;

    private static bool IsAsciiWhitespace(byte value)
        => value is 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x20;
}
