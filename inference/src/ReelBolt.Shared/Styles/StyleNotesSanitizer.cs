using System.Text;

namespace ReelBolt.Shared.Styles;

/// <summary>
/// Bounds and cleans a style's free-text notes before they are stored or shown to an agent.
///
/// <para>
/// Notes are the one part of a style a user writes freely, so they are never placed in a privileged
/// position: <c>StyleNudgeService</c> quotes them inside a delimited block that labels them as a
/// preference. This sanitizer makes that quoting hold — it removes the characters the block is
/// delimited with (square brackets, so the text cannot close or fake a <c>[STYLE INSTRUCTIONS]</c>
/// marker, and double quotes, so it cannot close the quotation), every control character, and
/// collapses whitespace to single spaces so the text cannot start a new markdown heading or fake a
/// new section on its own line. The result is capped at <see cref="MaxLength"/> characters.
/// </para>
/// </summary>
public static class StyleNotesSanitizer
{
    /// <summary>The longest note kept, in characters.</summary>
    public const int MaxLength = 500;

    /// <summary>The cleaned note, or null when nothing is left.</summary>
    public static string? Sanitize(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return null;

        var sb = new StringBuilder(Math.Min(notes.Length, MaxLength));
        bool pendingSpace = false;

        foreach (char c in notes)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (c is '[' or ']' or '"' or '`' or '<' or '>' or '{' or '}' or '\\' or '#')
            {
                // Dropped, not escaped: they carry no meaning in a stylistic preference, and each
                // could be used to break out of the quoted block or imitate markup.
                continue;
            }

            // Unicode format characters (zero-width joiners, bidi overrides) can disguise text.
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                continue;

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
            if (sb.Length >= MaxLength)
                break;
        }

        string result = sb.ToString().Trim();
        if (result.Length > MaxLength)
            result = result[..MaxLength].TrimEnd();
        return result.Length == 0 ? null : result;
    }
}
