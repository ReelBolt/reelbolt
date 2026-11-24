using System.Globalization;
using System.Text;

namespace ReelBolt.Inference.Api.Services.Editing.ReviewLinks;

/// <summary>
/// Normalisation of every string a guest supplies (names, notes, comment bodies). Guest text is stored as
/// plain text only: normalised to NFC, with control, bidirectional-override, zero-width and other invisible
/// format characters removed, so it cannot spoof another name, reorder surrounding text or hide in a mail
/// subject. React renders it as text; mail bodies are plain text.
/// </summary>
public static class ReviewGuestText
{
    /// <summary>
    /// A display name: one line, whitespace runs collapsed to a single space, trimmed. Null when nothing is left.
    /// </summary>
    public static string? NormalizeName(string? raw)
    {
        string? cleaned = Clean(raw, keepNewlines: false);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    /// <summary>
    /// A multi-line body: line breaks (CRLF, CR, NEL, LS, PS) become <c>\n</c>, runs of blank lines collapse to one,
    /// each line is trimmed. Null when nothing is left.
    /// </summary>
    public static string? NormalizeBody(string? raw)
    {
        string? cleaned = Clean(raw, keepNewlines: true);
        if (string.IsNullOrWhiteSpace(cleaned)) return null;
        return cleaned;
    }

    /// <summary>
    /// A short plain excerpt for a notification: whitespace collapsed to single spaces, cut to
    /// <paramref name="max"/> characters (with an ellipsis when cut).
    /// </summary>
    public static string Excerpt(string? text, int max = 80)
    {
        string flat = NormalizeName(text) ?? string.Empty;
        if (flat.Length <= max) return flat;
        int cut = max;
        // never split a surrogate pair
        if (cut > 0 && char.IsHighSurrogate(flat[cut - 1])) cut--;
        return flat[..cut].TrimEnd() + "…";
    }

    private static string? Clean(string? raw, bool keepNewlines)
    {
        if (raw is null) return null;

        // 1. Rebuild from runes so a lone surrogate (invalid UTF-16) is dropped instead of throwing later.
        StringBuilder sb = new(raw.Length);
        foreach (Rune rune in raw.EnumerateRunes())
        {
            if (rune.Value == 0xFFFD) continue; // the replacement of a lone surrogate (or a literal one): dropped
            sb.Append(rune.ToString());
        }

        string normalized;
        try { normalized = sb.ToString().Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return null; }

        // 2. Filter by Unicode category.
        StringBuilder outp = new(normalized.Length);
        bool pendingSpace = false;
        int newlineRun = 0;
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            int v = rune.Value;
            UnicodeCategory cat = Rune.GetUnicodeCategory(rune);

            bool isNewline = v is '\n' or '\r' or 0x85 or 0x2028 or 0x2029;
            if (isNewline)
            {
                if (keepNewlines)
                {
                    // CR LF is one break.
                    if (v == '\r') continue;
                    newlineRun++;
                    pendingSpace = false;
                }
                else
                {
                    pendingSpace = outp.Length > 0;
                }

                continue;
            }

            bool isSpace = v == ' ' || v == '\t' || cat == UnicodeCategory.SpaceSeparator;
            if (isSpace)
            {
                pendingSpace = outp.Length > 0 && newlineRun == 0;
                continue;
            }

            // Controls, format characters (zero-width, bidi marks and overrides, BOM, soft hyphen),
            // private use, unassigned and surrogates are dropped.
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate)
                continue;

            if (newlineRun > 0 && outp.Length > 0)
            {
                outp.Append('\n');
                if (newlineRun > 1) outp.Append('\n');
            }

            newlineRun = 0;
            if (pendingSpace && outp.Length > 0 && outp[^1] != '\n') outp.Append(' ');
            pendingSpace = false;
            outp.Append(rune.ToString());
        }

        return outp.ToString();
    }
}
