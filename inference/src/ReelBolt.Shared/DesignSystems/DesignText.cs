using System.Globalization;
using System.Text;

namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// Cleans free text before it is stored or shown to an agent: collapse whitespace, drop the
/// characters that could break out of a quoted block or imitate markup, drop Unicode format
/// characters, and cap the result. Never throws and never touches IO.
/// </summary>
public static class DesignText
{
    /// <summary>
    /// The cleaned text, or null when nothing is left (empty input, only dropped characters or a
    /// non-positive <paramref name="maxLength"/>). The result is never longer than
    /// <paramref name="maxLength"/>.
    /// </summary>
    public static string? Sanitize(string? input, int maxLength)
    {
        if (maxLength <= 0 || string.IsNullOrWhiteSpace(input))
            return null;

        var sb = new StringBuilder(Math.Min(input.Length, maxLength));
        bool pendingSpace = false;

        // Iterate by Rune: tag characters and extended variation selectors are surrogate pairs, so a
        // char-by-char scan would see two harmless-looking "Surrogate" halves and keep them.
        foreach (Rune rune in input.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (IsDroppedMarkup(rune) || IsHiddenRune(rune))
            {
                // Dropped, not escaped: they carry no meaning here and each could be used to
                // break out of a quoted block, imitate markup or smuggle invisible instructions.
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(rune.ToString());
            if (sb.Length >= maxLength)
                break;
        }

        string result = sb.ToString().Trim();
        if (result.Length > maxLength)
            result = result[..maxLength].TrimEnd();

        // A lone high surrogate would be an invalid UTF-16 string; drop it rather than keep it.
        if (result.Length > 0 && char.IsHighSurrogate(result[^1]))
            result = result[..^1];

        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// True for a rune that is invisible to a reader but can carry instructions or disguise text: Unicode
    /// format characters (zero-width, bidi), the tag block U+E0000..U+E007F, variation selectors
    /// (U+FE00..U+FE0F, U+E0100..U+E01EF) and a lone surrogate's replacement character.
    /// </summary>
    public static bool IsHiddenRune(Rune rune)
    {
        int value = rune.Value;
        if (value is >= 0xE0000 and <= 0xE007F)
            return true;

        if (value is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF)
            return true;

        if (value == 0xFFFD)
            return true;

        return Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format;
    }

    private static bool IsDroppedMarkup(Rune rune)
    {
        switch (rune.Value)
        {
            case '[' or ']' or '"' or '`' or '<' or '>' or '{' or '}' or '\\' or '#':
            // Fullwidth and CJK lookalikes of the same characters.
            case 0xFF3B or 0xFF3D or 0x3010 or 0x3011 or 0x3008 or 0x3009 or 0xFF1C or 0xFF1E
                or 0xFF5B or 0xFF5D or 0xFF02 or 0xFF40 or 0xFF03 or 0xFF3C:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="name"/> is a plausible font family: 1..80 characters from the
    /// conservative set <c>A-Z a-z 0-9 space _ . , ' &amp; + ( ) -</c>.
    /// </summary>
    public static bool IsSafeFontFamily(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 80)
            return false;

        foreach (char c in name)
        {
            bool allowed = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c is ' ' or '_' or '.' or ',' or '\'' or '&' or '+' or '(' or ')' or '-';

            if (!allowed)
                return false;
        }

        return true;
    }
}
