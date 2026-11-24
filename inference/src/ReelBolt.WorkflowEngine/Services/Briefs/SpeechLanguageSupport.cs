using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Services.Briefs;

/// <summary>How a speech provider's published language list relates to a requested language.</summary>
public enum SpeechLanguageVerdict
{
    /// <summary>The provider states it speaks the language.</summary>
    Supported,

    /// <summary>The provider publishes a list and the language is not on it.</summary>
    NotListed,

    /// <summary>No published list (or no language given): pass the request through and claim nothing.</summary>
    Unknown
}

/// <summary>
/// Fact table: which primary language subtags a speech provider kind states it can speak. Only
/// providers that publish a list appear here; every other kind (and any model-dependent endpoint such
/// as an OpenAI-compatible one) is <see cref="SpeechLanguageVerdict.Unknown"/> and the request is
/// passed through, so the engine never claims support a provider does not state. A language NOT on a
/// published list is reported as a warning on the step, never a failure.
/// </summary>
public static class SpeechLanguageSupport
{
    /// <summary>Fish Audio's published language list (hosted API).</summary>
    private static readonly HashSet<string> FishAudio = new(StringComparer.Ordinal)
    {
        "en", "zh", "ja", "de", "fr", "es", "ko", "ar", "ru", "nl", "it", "pl", "pt"
    };

    private static readonly IReadOnlyDictionary<InferenceProviderKind, HashSet<string>> Published =
        new Dictionary<InferenceProviderKind, HashSet<string>>
        {
            [InferenceProviderKind.FishAudio] = FishAudio
        };

    /// <summary>The published language subtags of <paramref name="kind"/>, or null when it publishes none.</summary>
    public static IReadOnlyCollection<string>? PublishedLanguages(InferenceProviderKind kind) =>
        Published.TryGetValue(kind, out HashSet<string>? set) ? set : null;

    public static SpeechLanguageVerdict Check(InferenceProviderKind kind, string? language)
    {
        string? primary = RunBriefLanguages.Primary(language);
        if (primary is null || !Published.TryGetValue(kind, out HashSet<string>? set))
            return SpeechLanguageVerdict.Unknown;
        return set.Contains(primary) ? SpeechLanguageVerdict.Supported : SpeechLanguageVerdict.NotListed;
    }
}
