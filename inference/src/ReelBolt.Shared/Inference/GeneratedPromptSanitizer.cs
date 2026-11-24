using System.Text;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// The result of running a model-authored prompt through <see cref="GeneratedPromptSanitizer"/>:
/// the cleaned text plus the length cap it was measured against.
/// </summary>
/// <param name="Text">
/// <paramref name="text"/> with every control character removed except <c>\n</c>, <c>\r</c> and
/// <c>\t</c>. Never <c>null</c> — a null input yields <see cref="string.Empty"/>.
/// </param>
/// <param name="MaxLength">The cap <see cref="Text"/> was checked against, carried alongside it so
/// the caller never has to re-derive which provider's limit applied.</param>
/// <remarks>
/// This type deliberately does NOT truncate. The prompt is free prose and the one field of a
/// planner's output that leaves the building, so silently cutting it would send a provider a
/// prompt the model never wrote — a half-sentence that still costs money, still produces a clip,
/// and gives the operator no signal that anything was lost. Phase 1's contract is that an
/// over-long prompt fails the step closed with <c>INVALID_PROMPT</c> instead; surfacing
/// <see cref="ExceedsMaxLength"/> is what preserves it.
/// </remarks>
public record SanitizedPrompt(string Text, int MaxLength)
{
    /// <summary>
    /// <c>true</c> when <see cref="Text"/> is empty or whitespace only: the prompt author
    /// produced nothing usable once control characters were stripped. Callers must treat this the
    /// same as a missing prompt rather than submitting a blank one.
    /// </summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    /// <summary>
    /// <c>true</c> when <see cref="Text"/> is longer than <see cref="MaxLength"/>. <see cref="Text"/>
    /// is left exactly as sanitized — the caller decides, and the deterministic generation step
    /// fails closed with <c>INVALID_PROMPT</c>.
    /// </summary>
    public bool ExceedsMaxLength => Text.Length > MaxLength;
}

/// <summary>
/// The single, shared prompt-hygiene pass for model-authored free prose that is about to be sent
/// to an external generation provider. Lifted out of phase 1's private
/// <c>VideoGenerateStepExecutor.SanitizePrompt</c> verbatim (same rule, same exceptions) so there
/// is exactly one implementation rather than a copy per call site.
/// </summary>
/// <remarks>
/// <para>
/// WHY this exists at all: the prompt is the only value a planning agent authors that leaves the
/// building. Every other field it emits is an opaque offered id or an enum word that first-party
/// C# resolves to a provider parameter — those cannot carry an injected instruction. Free prose
/// can, and control characters are the cheapest way to smuggle structure (line terminators that
/// fake a new section, NUL bytes that truncate a downstream parser's view of the string, terminal
/// escapes that render misleadingly in a log). Stripping them is hygiene, not a security boundary:
/// it is one of several layers, and it never claims to make the prompt trustworthy.
/// </para>
/// <para>
/// WHY the length cap is reported rather than applied: see <see cref="SanitizedPrompt"/>.
/// </para>
/// </remarks>
public static class GeneratedPromptSanitizer
{
    /// <summary>
    /// The default length cap, in characters. Matches phase 1's <c>MaxPromptLength</c> exactly, so
    /// lifting the private helper into this shared component changed no behaviour for MiniMax.
    /// </summary>
    public const int DefaultMaxPromptLength = 7000;

    /// <summary>
    /// The length cap for a prompt destined for <paramref name="kind"/>.
    /// </summary>
    /// <remarks>
    /// Total over EVERY <see cref="InferenceProviderKind"/> value on purpose. An enum-matched
    /// method that could fall through and return <c>0</c> would mark every prompt
    /// <see cref="SanitizedPrompt.ExceedsMaxLength"/>, and — because the caller fails closed —
    /// would turn a newly added provider kind into a pipeline that refuses all input instead of one
    /// that generates. A wrong-but-positive default degrades; a zero cap cannot.
    /// </remarks>
    public static int MaxPromptLengthFor(InferenceProviderKind kind) => kind switch
    {
        // Video generation is the only capability that sends model-authored prose outbound today, and
        // there are now TWO video-generation kinds — MiniMax and Higgsfield
        // (HiggsfieldVideoGenerationClient) — so the per-kind branch is currently an identity over
        // both. It is written out rather than collapsed because per-kind caps are the point of this
        // method and a future kind with a tighter limit belongs right here.
        InferenceProviderKind.MiniMax => DefaultMaxPromptLength,

        InferenceProviderKind.Higgsfield => DefaultMaxPromptLength,

        // A self-hosted ComfyUI's CLIPTextEncode truncates at the text encoder's own token window;
        // the character cap stays the shared default so the step's refusal behaviour is identical
        // across kinds, and the model's own truncation is what bounds the rest.
        InferenceProviderKind.ComfyUI => DefaultMaxPromptLength,

        _ => DefaultMaxPromptLength,
    };

    /// <summary>
    /// Removes every control character from <paramref name="text"/> except <c>\n</c>, <c>\r</c> and
    /// <c>\t</c> — phase 1's exact rule. Newlines and tabs are preserved because they are ordinary
    /// multi-line prompt formatting; every other control character is dropped.
    /// </summary>
    /// <param name="text">The raw text; <c>null</c> yields <see cref="string.Empty"/>.</param>
    /// <returns>The text with the offending characters omitted. Never <c>null</c>.</returns>
    public static string StripControlCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Sanitizes <paramref name="text"/> against the cap for <paramref name="kind"/>.
    /// </summary>
    /// <param name="text">The raw model-authored prompt; <c>null</c> or blank is allowed and yields
    /// <see cref="SanitizedPrompt.IsEmpty"/>.</param>
    /// <param name="kind">The provider kind the prompt is destined for.</param>
    public static SanitizedPrompt Sanitize(string? text, InferenceProviderKind kind)
        => Sanitize(text, MaxPromptLengthFor(kind));

    /// <summary>
    /// Sanitizes <paramref name="text"/> against an explicit cap.
    /// </summary>
    /// <param name="text">The raw model-authored prompt; <c>null</c> or blank is allowed and yields
    /// <see cref="SanitizedPrompt.IsEmpty"/>.</param>
    /// <param name="maxLength">The length cap to measure against.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxLength"/> is not positive. A non-positive cap is a caller bug, not a
    /// provider fact — accepting it would silently mark every prompt over-length and, because the
    /// caller fails closed, quietly disable the feature rather than surfacing the mistake.
    /// </exception>
    public static SanitizedPrompt Sanitize(string? text, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        return new SanitizedPrompt(StripControlCharacters(text), maxLength);
    }
}
