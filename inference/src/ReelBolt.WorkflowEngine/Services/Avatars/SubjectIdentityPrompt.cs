using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.WorkflowEngine.Services.Avatars;

/// <summary>
/// The identity facts of the objects a clip shows, as a first-party constraint on its video prompt
/// (docs/design/avatar-objects.md "Object fidelity"). Built only from the avatars' cleaned facts: no model text, no id,
/// no name. The prompt and the clause go through the same <see cref="GeneratedPromptSanitizer"/> pass as every prompt.
/// </summary>
public static class SubjectIdentityPrompt
{
    /// <summary>The clause for the OBJECTS among <paramref name="subjects"/> that have facts; null when none does.</summary>
    public static string? ClauseFor(IEnumerable<ResolvedAvatar> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        return AvatarIdentityFacts.Clause(subjects.Where(s => s.Usable && s.SubjectKind == AvatarSubjectKind.Object).Select(s => s.IdentityFacts));
    }

    /// <summary>
    /// <paramref name="prompt"/> followed by <paramref name="clause"/>, sanitized. When the clause is null, or the
    /// combined prompt is empty or over the provider's cap, the prompt alone is sanitized instead and
    /// <paramref name="applied"/> is false (the take check still runs).
    /// </summary>
    public static SanitizedPrompt Apply(string? prompt, string? clause, InferenceProviderKind kind, out bool applied)
    {
        SanitizedPrompt plain = GeneratedPromptSanitizer.Sanitize(prompt, kind);
        applied = false;
        if (clause is null || plain.IsEmpty || plain.ExceedsMaxLength) return plain;
        SanitizedPrompt combined = GeneratedPromptSanitizer.Sanitize(plain.Text.TrimEnd() + " " + clause, kind);
        if (combined.IsEmpty || combined.ExceedsMaxLength) return plain;
        applied = true;
        return combined;
    }
}
