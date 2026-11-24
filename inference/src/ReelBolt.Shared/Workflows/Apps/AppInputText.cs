using System.Globalization;
using System.Text;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// Cleaning of text an app input carries (docs/design/app-inputs.md 5.5 item 2): Unicode NFC; control, bidi,
/// zero-width, private-use and unassigned characters removed (a long text keeps its line breaks, as <c>\n</c>);
/// runs of variation selectors cut to one; trimmed. Never cuts to a length: a value over its cap is refused by the
/// caller, never silently shortened.
/// </summary>
public static class AppInputText
{
    /// <summary>The cleaned text; <paramref name="multiLine"/> keeps line breaks (at most two in a row).</summary>
    public static string Clean(string? text, bool multiLine)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Normalize(NormalizationForm.FormC);
        StringBuilder sb = new(normalized.Length);
        bool previousSelector = false;
        foreach (Rune r in normalized.EnumerateRunes())
        {
            bool selector = r.Value is >= 0xFE00 and <= 0xFE0F;
            bool drop = selector && previousSelector;
            previousSelector = selector;
            if (r.Value == '\n')
            {
                sb.Append(multiLine ? '\n' : ' ');
                continue;
            }

            if (r.Value == '\t')
            {
                sb.Append(' ');
                continue;
            }

            if (drop || IsHidden(r))
                continue;
            sb.Append(r.ToString());
        }

        string cleaned = sb.ToString();
        if (multiLine)
        {
            while (cleaned.Contains("\n\n\n", StringComparison.Ordinal))
                cleaned = cleaned.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        }

        return cleaned.Trim();
    }

    /// <summary>The length a cap is measured in: UTF-16 code units, as every vendor prompt cap in the configs.</summary>
    public static int Length(string text) => text.Length;

    private static bool IsHidden(Rune r) =>
        Rune.IsControl(r)
        || Rune.GetUnicodeCategory(r) is UnicodeCategory.Format or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.Surrogate
        || r.Value is >= 0xE0100 and <= 0xE01EF;
}
