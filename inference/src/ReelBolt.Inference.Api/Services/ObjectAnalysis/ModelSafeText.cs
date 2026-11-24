using System.Text;

namespace ReelBolt.Inference.Api.Services.ObjectAnalysis;

/// <summary>
/// Makes a user-owned name (file, timeline, clip, project) safe to echo into text the assistant reads or a card shows: it is
/// wrapped in curly quotes, so a name must not be able to close them. Straight and curly quotes, backticks, control characters,
/// and invisible or bidirectional formatting characters are removed; runs of spaces collapse.
/// </summary>
public static class ModelSafeText
{
    public const int DefaultMax = 80;

    /// <param name="quoteReplacement">When given, double and curly quotes become this character instead of vanishing (the approval card's convention); a straight apostrophe is then kept.</param>
    public static string Clean(string? value, int max = DefaultMax, char? quoteReplacement = null)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        StringBuilder sb = new(value.Length);
        bool space = false;
        foreach (char original in value)
        {
            char c = original;
            if (quoteReplacement is { } replacement && c is '"' or '\u201C' or '\u201D' or '\u201E' or '\u201F')
            {
                c = replacement;
            }
            else if (IsStripped(c) && !(quoteReplacement is not null && c == '\''))
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        string text = sb.ToString();
        return text.Length > max ? text[..max].TrimEnd() + "…" : text;
    }

    private static readonly HashSet<int> Stripped =
    [
        0x22, 0x27, 0x60, 0x201C, 0x201D, 0x2018, 0x2019, 0x201E, 0x201F, 0xAB, 0xBB, 0x2032, 0x2033,
        0x200B, 0x200C, 0x200D, 0x200E, 0x200F, 0x2028, 0x2029, 0xFEFF, 0xAD, 0x61C
    ];

    private static bool IsStripped(char c) =>
        Stripped.Contains(c)
        || (char.IsControl(c) && !char.IsWhiteSpace(c))
        || (c >= 0x202A && c <= 0x202E)
        || (c >= 0x2066 && c <= 0x2069);
}
