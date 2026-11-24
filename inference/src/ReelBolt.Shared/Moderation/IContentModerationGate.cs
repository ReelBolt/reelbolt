namespace ReelBolt.Shared.Moderation;

/// <summary>The content one gate call classifies, with the surface it came from.</summary>
public sealed record ModerationInput(
    string? Text,
    IReadOnlyList<ModerationImageInput> Images,
    ModerationContentKind Kind);

/// <summary>
/// The gate's runtime answer. <see cref="Available"/> distinguishes "the policy allowed it" from
/// "moderation could not run"; <see cref="ReviewQueued"/> says a mid-band audit row was written
/// (a review never blocks); <see cref="ContentSha256"/> is the hash stored on the audit row.
/// </summary>
public sealed record ModerationDecision(
    ModerationAction Action,
    bool Available,
    bool ReviewQueued,
    string? ReasonCode,
    string UserMessage,
    string ContentSha256);

/// <summary>
/// The one shared moderation gate the Inference API and the WorkflowEngine both consume. This
/// ticket declares the contract only — the API implementation (provider resolution, usage event,
/// audit row) lands in Task 2.
/// </summary>
/// <remarks>
/// <b>Polarity is deliberate and differs per surface:</b>
/// <list type="bullet">
///   <item><description><see cref="ScreenGenerationPromptAsync"/> and
///   <see cref="ScreenGeneratedTextAsync"/> FAIL OPEN: they return <c>true</c> on Allow, on Review
///   and on Unavailable, and <c>false</c> only on Block. A moderation outage must not stop people
///   from writing.</description></item>
///   <item><description><see cref="ScreenPublishAsync"/> FAILS CLOSED: it returns <c>true</c> on
///   Allow and Review, and <c>false</c> on Block, on Unavailable, and when nothing is configured.
///   Only egress needs that stance.</description></item>
///   <item><description><see cref="ModerateAsync"/> is the neutral form for callers that need the
///   action/availability/review flags themselves.</description></item>
/// </list>
/// </remarks>
public interface IContentModerationGate
{
    Task<ModerationDecision> ModerateAsync(
        ModerationInput input,
        ModerationAttribution attribution,
        CancellationToken ct);

    /// <summary>Fail open: true on Allow/Review/Unavailable, false only on Block.</summary>
    Task<bool> ScreenGenerationPromptAsync(
        string text,
        IReadOnlyList<ModerationImageInput> images,
        ModerationAttribution attribution,
        CancellationToken ct);

    /// <summary>Fail open: true on Allow/Review/Unavailable, false only on Block.</summary>
    Task<bool> ScreenGeneratedTextAsync(
        string text,
        string site,
        ModerationAttribution attribution,
        CancellationToken ct);

    /// <summary>Fail closed: true on Allow/Review, false on Block/Unavailable.</summary>
    Task<bool> ScreenPublishAsync(
        ModerationInput input,
        ModerationAttribution attribution,
        CancellationToken ct);
}
