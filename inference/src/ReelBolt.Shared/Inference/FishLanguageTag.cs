using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Maps a BCP-47 language tag (from the step or the run brief) to the <c>language</c> value sent to Fish Audio's
/// <c>/v1/tts</c>. Fish uses it only to normalise numbers and dates; it does not change stress or accent. A tag is kept
/// only when its primary language is one Fish documents; anything else returns null (Fish auto-detects).
/// </summary>
public static partial class FishLanguageTag
{
    private const int MaxLength = 35;

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "en", "zh", "ja", "ko", "es", "fr", "de", "pt", "ru", "ar", "tr", "tl", "it"
    };

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    /// <summary>The canonical tag to send (for example <c>it-IT</c>, <c>zh-CN</c>), or null to omit the field.</summary>
    public static string? From(string? language)
    {
        string? tag = language?.Trim();
        if (string.IsNullOrEmpty(tag) || tag.Length > MaxLength || !TagPattern().IsMatch(tag))
            return null;

        string[] parts = tag.Split('-');
        string primary = parts[0].ToLowerInvariant();
        if (!Supported.Contains(primary))
            return null;

        for (int i = 1; i < parts.Length; i++)
        {
            parts[i] = parts[i].Length switch
            {
                2 when parts[i].All(char.IsLetter) => parts[i].ToUpperInvariant(),
                4 when parts[i].All(char.IsLetter) => char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant(),
                _ => parts[i].ToLowerInvariant()
            };
        }

        parts[0] = primary;
        return string.Join('-', parts);
    }
}
