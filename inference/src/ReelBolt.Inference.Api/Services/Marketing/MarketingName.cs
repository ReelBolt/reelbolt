using System.Globalization;
using System.Text;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>Validation and splitting of the contact names kept on <c>marketing_consents</c>.</summary>
public static class MarketingName
{
    public const int MaxLength = 100;

    /// <summary>
    /// Trims, NFC-normalizes and collapses inner whitespace. Returns null for an empty value, a value over
    /// <see cref="MaxLength"/> after trimming (never truncated), or one with a control, format or bidi character.
    /// </summary>
    public static string? Clean(string? value) => CleanCore(value, 2);

    private static string? CleanCore(string? value, int maxMarkRun)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = trimmed.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var sb = new StringBuilder(normalized.Length);
        bool pendingSpace = false;
        int markRun = 0;
        bool anyBase = false;
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = true;
                markRun = 0;
                continue;
            }

            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                // More than the allowed number of combining marks in a row is stacked-mark abuse ("Zalgo"), not a name.
                if (++markRun > maxMarkRun)
                {
                    return null;
                }
            }
            else
            {
                markRun = 0;
                anyBase = true;
            }

            if (Rune.IsControl(rune) || category is UnicodeCategory.Format or UnicodeCategory.Control
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
            {
                return null;
            }

            if (pendingSpace && sb.Length > 0)
            {
                sb.Append(' ');
            }

            pendingSpace = false;
            sb.Append(rune.ToString());
        }

        if (sb.Length == 0 || sb.Length > MaxLength || !anyBase)
        {
            return null;
        }

        return sb.ToString();
    }

    /// <summary>Longest first name the anonymous subscribe form may carry.</summary>
    public const int MaxFormLength = 40;

    /// <summary>
    /// The strict rule for a name typed into the anonymous subscribe form. After <see cref="Clean"/> (which also refuses
    /// stacked combining marks) the name must: be at most <see cref="MaxFormLength"/> characters; start with a letter;
    /// consist only of letters, combining marks, single spaces, hyphen and apostrophe (' or U+2019); have every combining
    /// mark directly after a letter; contain no modifier letter (Lm, e.g. U+02BC, U+A78F) except U+30FC inside a Japanese name, and none of the Hangul fillers U+115F, U+1160, U+3164, U+FFA0; combining marks are U+0300-036F (not U+034F, U+0337, U+0338) or the marks of the same script as the previous letter, at most 2 in a row (3 for Hebrew and Arabic); and use letters of ONE
    /// script only, taken from an explicit block allowlist (Latin, Greek, Cyrillic, Armenian, Hebrew, Arabic, the Indic
    /// scripts Devanagari to Malayalam, Thai, Hangul, Georgian, and Han/Hiragana/Katakana, which may mix with each other).
    /// Letters outside the allowlist (Lisu, Canadian Syllabics, Cherokee, fullwidth and mathematical alphanumerics) and
    /// any digit or symbol (':' '/' '.' '@' '&lt;' '&gt;') are refused: no URL, no lookalike punctuation, no mixed-script
    /// spoof such as Latin plus Cyrillic. Admin and account names keep the looser <see cref="Clean"/> rules.
    /// </summary>
    public static string? CleanFormName(string? value)
    {
        // Hebrew and Arabic may carry 3 marks in a row; the per-script cap below brings every other script back to 2.
        string? cleaned = CleanCore(value, 3);
        if (cleaned is null || cleaned.Length > MaxFormLength)
        {
            return null;
        }

        int script = 0;
        int lastLetter = 0;
        bool afterLetter = false;
        bool first = true;
        int markRun = 0;
        foreach (Rune rune in cleaned.EnumerateRunes())
        {
            int v = rune.Value;
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (v is 0x3164 or 0x115F or 0x1160 or 0xFFA0)
            {
                return null; // Hangul fillers render as blank
            }

            if (v == 0x30FC && !first && script == JapaneseGroup)
            {
                // The katakana prolonged sound mark is the only modifier letter allowed, and only inside a Japanese name.
                afterLetter = false;
                markRun = 0;
                first = false;
                continue;
            }

            if (category == UnicodeCategory.ModifierLetter)
            {
                return null;
            }

            if (Rune.IsLetter(rune))
            {
                int id = ScriptOf(v);
                if (id == 0)
                {
                    return null;
                }

                if (script == 0)
                {
                    script = id;
                }
                else if (script != id && !(IsJapanese(script) && IsJapanese(id)))
                {
                    return null;
                }

                if (IsJapanese(id))
                {
                    script = JapaneseGroup;
                }

                lastLetter = id;
                afterLetter = true;
                markRun = 0;
            }
            else if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                if (!afterLetter)
                {
                    return null;
                }

                bool generic = v >= 0x300 && v <= 0x36F && v is not (0x34F or 0x337 or 0x338);
                if (!generic && MarkScriptOf(v) != lastLetter)
                {
                    return null;
                }

                if (++markRun > (lastLetter is 5 or 6 ? 3 : 2))
                {
                    return null;
                }
            }
            else if (v is ' ' or '\'' or '\u2019' or '-')
            {
                if (first)
                {
                    return null;
                }

                afterLetter = false;
                markRun = 0;
            }
            else
            {
                return null;
            }

            first = false;
        }

        return script == 0 ? null : cleaned;
    }

    /// <summary>The script id owning a script-specific combining mark (Indic signs, Thai, Hebrew points, Arabic marks); 0 otherwise.</summary>
    private static int MarkScriptOf(int c)
    {
        static bool In(int c, int lo, int hi) => c >= lo && c <= hi;

        if (In(c, 0x591, 0x5C7)) return 5; // Hebrew points
        if (In(c, 0x610, 0x61A) || In(c, 0x64B, 0x65F) || c == 0x670 || In(c, 0x6D6, 0x6ED)) return 6; // Arabic
        if (In(c, 0x900, 0x97F)) return 7;
        if (In(c, 0x980, 0x9FF)) return 8;
        if (In(c, 0xA00, 0xA7F)) return 9;
        if (In(c, 0xA80, 0xAFF)) return 10;
        if (In(c, 0xB00, 0xB7F)) return 11;
        if (In(c, 0xB80, 0xBFF)) return 12;
        if (In(c, 0xC00, 0xC7F)) return 13;
        if (In(c, 0xC80, 0xCFF)) return 14;
        if (In(c, 0xD00, 0xD7F)) return 15;
        if (c == 0xE31 || In(c, 0xE34, 0xE3A) || In(c, 0xE47, 0xE4E)) return 16; // Thai
        return 0;
    }

    private const int JapaneseGroup = 100;

    private static bool IsJapanese(int id) => id is JapaneseGroup or 101 or 102 or 103;

    /// <summary>The script id of a letter by explicit block allowlist; 0 when the letter is not allowed.</summary>
    private static int ScriptOf(int c)
    {
        static bool In(int c, int lo, int hi) => c >= lo && c <= hi;

        if (In(c, 0x41, 0x5A) || In(c, 0x61, 0x7A) || (In(c, 0xC0, 0xFF) && c != 0xD7 && c != 0xF7)
            || In(c, 0x100, 0x24F) || In(c, 0x1E00, 0x1EFF))
        {
            return 1; // Latin
        }

        if (In(c, 0x370, 0x3FF) || In(c, 0x1F00, 0x1FFF)) return 2; // Greek
        if (In(c, 0x400, 0x52F)) return 3; // Cyrillic
        if (In(c, 0x531, 0x587)) return 4; // Armenian
        if (In(c, 0x5D0, 0x5EA)) return 5; // Hebrew
        if (In(c, 0x620, 0x64A) || In(c, 0x66E, 0x6D3) || In(c, 0x6FA, 0x6FC)) return 6; // Arabic
        if (In(c, 0x900, 0x97F)) return 7; // Devanagari
        if (In(c, 0x980, 0x9FF)) return 8; // Bengali
        if (In(c, 0xA00, 0xA7F)) return 9; // Gurmukhi
        if (In(c, 0xA80, 0xAFF)) return 10; // Gujarati
        if (In(c, 0xB00, 0xB7F)) return 11; // Oriya
        if (In(c, 0xB80, 0xBFF)) return 12; // Tamil
        if (In(c, 0xC00, 0xC7F)) return 13; // Telugu
        if (In(c, 0xC80, 0xCFF)) return 14; // Kannada
        if (In(c, 0xD00, 0xD7F)) return 15; // Malayalam
        if (In(c, 0xE01, 0xE45)) return 16; // Thai letters (marks and the Lm maiyamok are handled apart)
        if (In(c, 0x10D0, 0x10FF)) return 17; // Georgian
        if (In(c, 0x1100, 0x11FF) || In(c, 0x3130, 0x318F) || In(c, 0xAC00, 0xD7AF)) return 18; // Hangul
        if (In(c, 0x3041, 0x3096)) return 101; // Hiragana
        if (In(c, 0x30A1, 0x30FA)) return 102; // Katakana
        if (In(c, 0x4E00, 0x9FFF) || In(c, 0x3400, 0x4DBF)) return 103; // Han
        return 0;
    }

    /// <summary>
    /// Splits an account display name into (first, last). Null/null when the name is blank, equals the email
    /// local part (the signup fallback), contains '@', or is not a valid name.
    /// </summary>
    public static (string? First, string? Last) FromDisplayName(string? displayName, string? email)
    {
        string? cleaned = Clean(displayName);
        if (cleaned is null || cleaned.Contains('@'))
        {
            return (null, null);
        }

        if (!string.IsNullOrEmpty(email))
        {
            int at = email.IndexOf('@');
            string local = at >= 0 ? email[..at] : email;
            if (string.Equals(cleaned, local.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return (null, null);
            }
        }

        int space = cleaned.IndexOf(' ');
        if (space < 0)
        {
            return (cleaned, null);
        }

        return (cleaned[..space], Clean(cleaned[(space + 1)..]));
    }
}
