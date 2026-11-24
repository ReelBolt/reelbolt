using System.Globalization;
using System.Text;

namespace ReelBolt.Shared.Workflows;

/// <summary>Validation and normalisation for project pronunciation glossary entries.</summary>
public static class PronunciationGlossaryValidator
{
    public const int MinTermLength = 2;
    public const int MaxTermLength = 60;
    public const int MaxSpellingLength = 120;

    /// <summary>NFC-normalises and trims a term or spelling.</summary>
    public static string Normalize(string? value) =>
        (value ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();

    /// <summary>The errors for one entry (already-normalised values expected); empty when valid.</summary>
    public static IReadOnlyList<string> Validate(string? term, string? spelling)
    {
        List<string> errors = [];
        string t = Normalize(term);
        string s = Normalize(spelling);

        if (t.Length < MinTermLength || t.Length > MaxTermLength
            || t.Any(IsControlOrFormat))
            errors.Add($"term must be {MinTermLength} to {MaxTermLength} characters, not blank, with no control or format characters.");

        if (s.Length < 1 || s.Length > MaxSpellingLength
            || s.Any(IsControlOrFormat) || !s.All(IsAllowedSpellingChar))
            errors.Add($"spelling must be 1 to {MaxSpellingLength} characters of letters, accents, stress marks, spaces, '-' or an apostrophe (no digits, links or markup).");

        // A spelling is bounded relative to its term and must hold a letter: it is a way of saying the word, not a
        // sentence. (What it says is checked by content moderation when it is saved and again, in the text that is
        // spoken, before synthesis.)
        if (errors.Count == 0 && (!s.Any(char.IsLetter) || s.Length > MaxSpellingLengthFor(t)))
            errors.Add($"spelling must have at least one letter and be at most {MaxSpellingLengthFor(t)} characters for this word (twice the word plus two).");

        return errors;
    }

    /// <summary>The longest spelling accepted for a term: twice its length plus two, never over <see cref="MaxSpellingLength"/>.</summary>
    public static int MaxSpellingLengthFor(string? term) =>
        Math.Min(MaxSpellingLength, (Normalize(term).Length * 2) + 2);

    private static bool IsControlOrFormat(char c) =>
        char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;

    private static bool IsAllowedSpellingChar(char c)
    {
        if (c is ' ' or '-' or '\'' or '’')
            return true;
        if (char.IsLetter(c))
            return true;
        UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            return true;
        // IPA stress and length marks (letters, IPA letters included, were accepted above).
        return c is 'ˈ' or 'ˌ' or 'ː' or 'ˑ';
    }
}
