using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Workflows;

/// <summary>One project-glossary entry: <see cref="Term"/> is spoken as <see cref="Spelling"/> (a written-accent form).</summary>
public sealed record GlossaryEntry(string Term, string Spelling, bool CaseSensitive);

/// <summary>An entry that matched at least once in a text, and how many times.</summary>
public sealed record AppliedEntry(string Term, string Spelling, int Count);

/// <summary>
/// Substitutes project-glossary written-accent spellings into the text sent to the speech provider and
/// NOTHING else (captions, on-screen text, timing and the saved line keep the original words).
/// Pure and deterministic: NFC on both sides, whole-word matches, one pass over the original text with
/// the longest term first so a replacement is never matched again.
/// </summary>
public static class PronunciationRespeller
{
    /// <summary>Most entries considered per call (the first ones, in the order given).</summary>
    public const int MaxEntries = 200;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static (string Text, IReadOnlyList<AppliedEntry> Applied) Apply(string text, IReadOnlyList<GlossaryEntry> entries)
    {
        string nfc = (text ?? string.Empty).Normalize(NormalizationForm.FormC);
        if (entries is null || entries.Count == 0 || nfc.Length == 0)
            return (nfc, []);

        List<GlossaryEntry> usable = Normalize(entries);
        if (usable.Count == 0)
            return (nfc, []);

        var sorted = usable.OrderByDescending(e => e.Term.Length).ThenBy(e => e.Term, StringComparer.Ordinal).ToList();
        string alternation = string.Join(
            "|",
            sorted.Select(e => (e.CaseSensitive ? "(?-i:" : "(?i:") + Regex.Escape(e.Term) + ")").Select(g => "(" + g + ")"));
        var regex = new Regex(
            @"(?<![\p{L}\p{Nd}])(?:" + alternation + @")(?![\p{L}\p{Nd}])",
            RegexOptions.CultureInvariant,
            RegexTimeout);

        var counts = new int[sorted.Count];
        string result = regex.Replace(nfc, m =>
        {
            for (int i = 0; i < sorted.Count; i++)
            {
                if (!m.Groups[i + 1].Success)
                    continue;
                counts[i]++;
                GlossaryEntry e = sorted[i];
                return e.CaseSensitive ? e.Spelling : AdaptCase(m.Value, e.Spelling);
            }
            return m.Value;
        });

        var applied = new List<AppliedEntry>();
        for (int i = 0; i < sorted.Count; i++)
            if (counts[i] > 0)
                applied.Add(new AppliedEntry(sorted[i].Term, sorted[i].Spelling, counts[i]));
        applied.Sort((a, b) => string.CompareOrdinal(a.Term, b.Term));
        return (result, applied);
    }

    /// <summary>
    /// A stable digest of the entries Apply would use, or null when there are none (so a project without a glossary
    /// adds nothing to a cache key).
    /// </summary>
    public static string? Digest(IReadOnlyList<GlossaryEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
            return null;
        List<GlossaryEntry> usable = Normalize(entries);
        if (usable.Count == 0)
            return null;

        var sb = new StringBuilder("pronunciation-glossary-v1");
        foreach (GlossaryEntry e in usable.OrderBy(e => e.Term, StringComparer.Ordinal).ThenBy(e => e.CaseSensitive))
        {
            sb.Append('\n').Append(e.Term.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(e.Term)
              .Append('\n').Append(e.Spelling.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(e.Spelling)
              .Append('\n').Append(e.CaseSensitive ? '1' : '0');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static List<GlossaryEntry> Normalize(IReadOnlyList<GlossaryEntry> entries) =>
        entries
            .Take(MaxEntries)
            .Select(e => new GlossaryEntry(
                (e.Term ?? string.Empty).Normalize(NormalizationForm.FormC),
                (e.Spelling ?? string.Empty).Normalize(NormalizationForm.FormC),
                e.CaseSensitive))
            .Where(e => e.Term.Length > 0 && e.Spelling.Length > 0)
            // Defence in depth for rows stored by any route: an entry that the save-time rules would refuse
            // (characters, length) is never substituted.
            .Where(e => ReelBolt.Shared.Workflows.PronunciationGlossaryValidator.Validate(e.Term, e.Spelling).Count == 0)
            .ToList();

    private static string AdaptCase(string matched, string spelling)
    {
        bool hasCased = matched.Any(char.IsLetter);
        if (hasCased && matched.Where(char.IsLetter).All(char.IsUpper) && matched.Count(char.IsLetter) > 1)
            return spelling.ToUpperInvariant();
        if (char.IsLower(matched[0]) && char.IsUpper(spelling[0]))
            return char.ToLowerInvariant(spelling[0]) + spelling[1..];
        return spelling;
    }
}
