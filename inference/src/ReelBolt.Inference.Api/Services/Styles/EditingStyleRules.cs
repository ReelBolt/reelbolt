using ReelBolt.Shared.Styles;

namespace ReelBolt.Inference.Api.Services.Styles;

/// <summary>
/// The one set of rules a saved editing style must satisfy, shared by the REST controller and the
/// assistant's style tools so the two can never disagree about what is a valid style.
/// </summary>
public static class EditingStyleRules
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 500;

    /// <summary>How many saved styles one user may keep.</summary>
    public const int MaxStylesPerUser = 100;

    /// <summary>
    /// Normalizes and validates a style. Returns the cleaned name, description and config (notes
    /// sanitized), or a plain-language error.
    /// </summary>
    public static (string Name, string? Description, StyleConfig Config, string? Error) Normalize(
        string? name, string? description, StyleConfig? config)
    {
        string trimmedName = (name ?? string.Empty).Trim();
        string? trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        StyleConfig cleaned = (config ?? new StyleConfig()).Sanitized();
        bool languageRejected = !string.IsNullOrWhiteSpace(config?.Language) && cleaned.Language is null;

        if (trimmedName.Length == 0)
            return (trimmedName, trimmedDescription, cleaned, "A style needs a name.");
        if (trimmedName.Length > MaxNameLength)
            return (trimmedName, trimmedDescription, cleaned, $"A style name can be at most {MaxNameLength} characters.");
        if (trimmedDescription is { Length: > MaxDescriptionLength })
            return (trimmedName, trimmedDescription, cleaned, $"A style description can be at most {MaxDescriptionLength} characters.");
        if (languageRejected)
            return (trimmedName, trimmedDescription, cleaned, "The language must be a language tag such as 'it' or 'pt-BR', or a common language name such as 'Italian'.");
        if (cleaned.IsEmpty)
            return (trimmedName, trimmedDescription, cleaned, "Pick at least one style choice or write some style notes.");
        if (!AllDefined(cleaned))
            return (trimmedName, trimmedDescription, cleaned, "The style contains an unknown choice.");

        return (trimmedName, trimmedDescription, cleaned, null);
    }

    // Defence against a numeric enum value deserialized from JSON (e.g. "pacing": 42).
    private static bool AllDefined(StyleConfig c) =>
        Defined(c.Pacing) && Defined(c.Storytelling) && Defined(c.Editing) && Defined(c.Tone) &&
        Defined(c.Captions) && Defined(c.Color) && Defined(c.Music) && Defined(c.Graphics) && Defined(c.Dynamics);

    private static bool Defined<T>(T? value) where T : struct, Enum =>
        value is not { } v || Enum.IsDefined(v);
}
