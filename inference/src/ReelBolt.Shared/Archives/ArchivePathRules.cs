using System.Globalization;
using System.Text;

namespace ReelBolt.Shared.Archives;

/// <summary>
/// The one definition of what a zip entry name may look like and when two names are the same file. Shared by the
/// import worker (<see cref="SafeZipReader"/>) and the API's check of the worker's answer, so both agree.
///
/// Names are full Unicode: accents, CJK, Cyrillic, emoji. What is refused is anything invisible or able to
/// reorder or hide text (Unicode categories Cc, Cf - which covers the bidi controls U+202A-202E and U+2066-2069,
/// zero-width characters and tag characters - plus the line/paragraph separators, unpaired surrogates and the
/// replacement character), and the structural shapes (traversal, drive letters, ADS colon, backslash, ...).
///
/// <see cref="Validate"/> needs no ICU and is used by both the worker and the API (which runs invariant). The
/// normalisation members below are for the worker's reader only: two names are one file when their NFC forms are
/// equal ignoring case, which needs normalisation data (ICU), so the reader fails closed: when the runtime has none,
/// <see cref="TryGetDedupeKey"/> returns false for any non-ASCII name and the entry is refused.
/// </summary>
public static class ArchivePathRules
{
    public const string PathTraversal = "PATH_TRAVERSAL";
    public const string PathInvalid = "PATH_INVALID";
    public const string PathTooDeep = "PATH_TOO_DEEP";

    /// <summary>The runtime cannot normalise, so a non-ASCII name cannot be de-duplicated safely and is refused.</summary>
    public const string NormalizationUnavailable = "NORMALIZATION_UNAVAILABLE";

    private const int MaxSegmentLength = 255;

    /// <summary>Longest whole path (UTF-16 units) the worker accepts; the API refuses more, so both cap alike.</summary>
    public const int MaxPathLength = 1024;

    /// <summary>Combining marks (Mn/Me/Mc) allowed in one segment; more is refused before any normalisation runs.</summary>
    private const int MaxMarksPerSegment = 32;

    /// <summary>Total characters of non-ASCII names the worker normalises for one archive.</summary>
    public const int MaxNormalizedCharactersPerArchive = 200_000;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// True when this runtime composes canonically-equivalent spellings. Probed by behaviour, not by reading a
    /// switch: <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c> (an environment variable) does not set the AppContext
    /// switch, while the behaviour is what actually matters.
    /// </summary>
    public static bool NormalizationAvailable { get; } = ProbeNormalization();

    private static bool ProbeNormalization()
    {
        try
        {
            return "é".Normalize(NormalizationForm.FormC) == "é"
                && "é".Normalize(NormalizationForm.FormD) == "é";
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsAscii(string value)
    {
        foreach (char c in value)
        {
            if (c > 0x7F)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Normalises <paramref name="path"/> to NFC. False when the path is non-ASCII and the runtime cannot
    /// normalise, or when normalisation rejects the text; never throws.
    /// </summary>
    public static bool TryNormalize(string path, out string normalized)
    {
        normalized = path;
        if (IsAscii(path))
            return true;
        if (!NormalizationAvailable)
            return false;

        try
        {
            normalized = path.Normalize(NormalizationForm.FormC);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The de-duplication key: NFC, then upper-cased with the invariant culture, then NFC again (upper-casing can
    /// decompose). Invariant case folding is deliberate: Turkish dotted and dotless i are NOT merged, so
    /// <c>i</c>, <c>U+0131</c> and <c>U+0130</c> stay three distinct names; a key must only merge names that
    /// every filesystem agrees are one name, and Turkish folding is locale-specific.
    /// </summary>
    public static bool TryGetDedupeKey(string path, out string key)
    {
        key = path;
        if (!TryNormalize(path, out string normalized))
            return false;

        if (IsAscii(normalized))
        {
            key = normalized.ToUpperInvariant();
            return true;
        }

        try
        {
            key = normalized.ToUpperInvariant().Normalize(NormalizationForm.FormC);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the refusal reason for <paramref name="name"/>, or null when it names a plain workspace file.
    /// Checks run in the documented order; the first match wins.
    /// </summary>
    public static string? Validate(string name, int maxDepth)
    {
        if (string.IsNullOrEmpty(name))
            return PathInvalid;

        // PATH_TRAVERSAL first: anything able to escape or to be re-interpreted by a host OS.
        if (name.Contains('\\'))
            return PathTraversal;
        if (name.Contains(':'))
            return PathTraversal;
        if (name.StartsWith('/'))
            return PathTraversal;

        string[] segments = name.Split('/');
        foreach (string segment in segments)
        {
            if (segment == "..")
                return PathTraversal;
        }

        // PATH_INVALID: characters a filesystem or terminal must never see, and invalid UTF-16.
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c < 0x20 || c == 0x7F)
                return PathInvalid;
            if (c <= 0x7F)
                continue;

            Rune rune;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= name.Length || !char.IsLowSurrogate(name[i + 1]))
                    return PathInvalid;
                rune = new Rune(c, name[i + 1]);
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return PathInvalid;
            }
            else
            {
                rune = new Rune(c);
            }

            // U+FFFD is what the BCL substitutes for bytes that were not valid UTF-8.
            if (rune.Value == 0xFFFD)
                return PathInvalid;

            if (IsInvisibleFiller(rune.Value))
                return PathInvalid;

            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.Surrogate:
                    return PathInvalid;
            }
        }

        if (name.Length > MaxPathLength)
            return PathInvalid;

        foreach (string segment in segments)
        {
            if (segment.Length == 0)
                return PathInvalid;
            if (segment == ".")
                return PathInvalid;
            if (segment.Length > MaxSegmentLength)
                return PathInvalid;
            if (segment.EndsWith('.') || IsSpaceSeparator(segment[^1]) || IsSpaceSeparator(segment[0]))
                return PathInvalid;
            if (CountMarks(segment) > MaxMarksPerSegment)
                return PathInvalid;

            string stem = segment;
            int firstDot = segment.IndexOf('.');
            if (firstDot >= 0)
                stem = segment[..firstDot];

            // Windows treats COM/LPT with superscript digits as devices too.
            if (ReservedDeviceNames.Contains(stem.Replace('\u00b9', '1').Replace('\u00b2', '2').Replace('\u00b3', '3')))
                return PathInvalid;
        }

        // PATH_TOO_DEEP last: only a well-formed path can be "too deep".
        if (segments.Length > maxDepth)
            return PathTooDeep;

        return null;
    }

    private static bool IsSpaceSeparator(char c) => c == ' ' || (c > 0x7F && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator);

    /// <summary>Characters that render as nothing or as a blank and are not Cc/Cf: fillers, Braille blank, CGJ, variation selectors.</summary>
    private static bool IsInvisibleFiller(int value) =>
        value is 0x115F or 0x1160 or 0x3164 or 0xFFA0 or 0x2800 or 0x034F
        || value is >= 0xFE00 and <= 0xFE0F
        || value is >= 0xE0100 and <= 0xE01EF;

    private static int CountMarks(string segment)
    {
        int marks = 0;
        foreach (char c in segment)
        {
            if (c < 0x300)
                continue;
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark)
                marks++;
        }

        return marks;
    }

    /// <summary>
    /// The name without trailing blanks and invisible characters (whitespace, Zs, Cc, Cf, fillers, marks that do not
    /// stand alone). Used only to read an extension, so a hidden trailing character cannot hide <c>.js</c> or <c>.html</c>.
    /// </summary>
    public static string TrimInvisibleEnd(string name)
    {
        int end = name.Length;
        while (end > 0)
        {
            char c = name[end - 1];
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            bool invisible = char.IsWhiteSpace(c)
                || IsInvisibleFiller(c)
                || category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.SpaceSeparator
                    or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.Surrogate;
            if (!invisible)
                break;
            end--;
        }

        return name[..end];
    }
}
