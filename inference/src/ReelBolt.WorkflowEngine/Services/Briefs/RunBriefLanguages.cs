using ReelBolt.Shared.Styles;

namespace ReelBolt.WorkflowEngine.Services.Briefs;

/// <summary>
/// The fixed table that turns a validated language tag into the language NAME an agent reads. The
/// raw brief string is never model-visible: the tag is re-validated here and rendered next to a
/// name from this closed list (an unknown but well-formed tag is quoted as a tag only).
/// </summary>
public static class RunBriefLanguages
{
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["en"] = "English",
        ["it"] = "Italian",
        ["es"] = "Spanish",
        ["fr"] = "French",
        ["de"] = "German",
        ["pt"] = "Portuguese",
        ["nl"] = "Dutch",
        ["pl"] = "Polish",
        ["ru"] = "Russian",
        ["uk"] = "Ukrainian",
        ["tr"] = "Turkish",
        ["ar"] = "Arabic",
        ["he"] = "Hebrew",
        ["hi"] = "Hindi",
        ["ja"] = "Japanese",
        ["ko"] = "Korean",
        ["zh"] = "Chinese",
        ["sv"] = "Swedish",
        ["no"] = "Norwegian",
        ["nb"] = "Norwegian",
        ["da"] = "Danish",
        ["fi"] = "Finnish",
        ["el"] = "Greek",
        ["cs"] = "Czech",
        ["ro"] = "Romanian",
        ["hu"] = "Hungarian",
        ["id"] = "Indonesian",
        ["vi"] = "Vietnamese",
        ["th"] = "Thai",
        ["ca"] = "Catalan",
        ["bg"] = "Bulgarian",
        ["hr"] = "Croatian",
        ["sk"] = "Slovak",
        ["sl"] = "Slovenian",
        ["sr"] = "Serbian",
        ["lt"] = "Lithuanian",
        ["lv"] = "Latvian",
        ["et"] = "Estonian",
        ["ms"] = "Malay",
        ["fa"] = "Persian",
        ["bn"] = "Bengali",
        ["ta"] = "Tamil",
        ["ur"] = "Urdu",
        ["sw"] = "Swahili",
        ["af"] = "Afrikaans"
    };

    /// <summary>The validated, canonical tag/name for <paramref name="value"/>, or null for blank or unacceptable input.</summary>
    public static string? Normalize(string? value) => StyleLanguageSanitizer.Sanitize(value);

    /// <summary>The primary subtag (<c>pt</c> of <c>pt-BR</c>), or null for a name or invalid input.</summary>
    public static string? Primary(string? value) => StyleLanguageSanitizer.PrimaryTag(Normalize(value));

    /// <summary>
    /// How a language is named in a prompt: <c>Italian (it)</c>, <c>Spanish (es-MX)</c>, <c>the language with tag "xx"</c>
    /// for a well-formed tag outside the table, or the closed-list name itself. Null when invalid.
    /// </summary>
    public static (string Name, string Label)? Describe(string? value)
    {
        string? normalized = Normalize(value);
        if (normalized is null)
            return null;
        string? primary = StyleLanguageSanitizer.PrimaryTag(normalized);
        if (primary is null)
            return (normalized, normalized);
        if (Names.TryGetValue(primary, out string? name))
            return (name, $"{name} ({normalized})");
        string unknown = $"the language with tag \"{normalized}\"";
        return (unknown, unknown);
    }
}
