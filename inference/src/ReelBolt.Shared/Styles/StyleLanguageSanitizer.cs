using System.Globalization;
using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Styles;

/// <summary>
/// Validates and normalizes a run's language. The value is quoted into agent prompts, so only two
/// shapes survive: a BCP-47-ish tag (<c>it</c>, <c>pt-BR</c>, <c>zh-Hans-CN</c>) or a name from a short
/// closed list. Anything else yields null — never a free-text channel.
/// </summary>
public static partial class StyleLanguageSanitizer
{
    public const int MaxLength = 35;

    private static readonly string[] KnownNames =
    [
        "English", "Italian", "Spanish", "French", "German", "Portuguese", "Dutch", "Polish", "Russian",
        "Ukrainian", "Turkish", "Arabic", "Hebrew", "Hindi", "Japanese", "Korean", "Chinese", "Swedish",
        "Norwegian", "Danish", "Finnish", "Greek", "Czech", "Romanian", "Hungarian", "Indonesian", "Vietnamese", "Thai"
    ];

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    /// <summary>The canonical tag or language name, or null when blank or not acceptable.</summary>
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
            return null;

        foreach (string name in KnownNames)
        {
            if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        if (!TagPattern().IsMatch(trimmed))
            return null;

        string[] parts = trimmed.Split('-');
        parts[0] = parts[0].ToLowerInvariant();
        for (int i = 1; i < parts.Length; i++)
        {
            parts[i] = parts[i].Length switch
            {
                2 => parts[i].ToUpperInvariant(),
                4 => char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant(),
                _ => parts[i].ToLowerInvariant()
            };
        }

        return string.Join('-', parts);
    }

    /// <summary>The primary language subtag (<c>pt</c> of <c>pt-BR</c>), or null for a name/blank.</summary>
    public static string? PrimaryTag(string? sanitized)
    {
        if (string.IsNullOrEmpty(sanitized) || !TagPattern().IsMatch(sanitized) || KnownNames.Contains(sanitized, StringComparer.Ordinal))
            return null;
        return sanitized.Split('-')[0].ToLower(CultureInfo.InvariantCulture);
    }
}
