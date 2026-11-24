namespace ReelBolt.Shared.Moderation;

/// <summary>Where a moderation decision was made. Persisted by name in the audit row.</summary>
public enum ModerationSurface
{
    /// <summary>A media-generation prompt, before any budget is reserved or vendor is called.</summary>
    GenerationPrompt,

    /// <summary>An uploaded file, during ingest.</summary>
    Ingest,

    /// <summary>Generated text (script, caption, overlay) before render.</summary>
    GeneratedText,

    /// <summary>Egress: publish links, render downloads and exports.</summary>
    Publish
}

/// <summary>
/// What the policy says to do. <see cref="Review"/> never blocks; it writes an audit row.
/// <see cref="Unavailable"/> means the response could not be classified at all (no scores and no
/// flags): it is never "clean", so only the fail-open surfaces may treat it as allowed and the
/// publish surface fails closed.
/// </summary>
public enum ModerationAction
{
    Allow,
    Block,
    Review,

    /// <summary>Appended: a response with no usable signal. Never persisted as an Allow.</summary>
    Unavailable
}

/// <summary>The kind of content classified. Video and audio are sampled into frames/text first.</summary>
public enum ModerationContentKind
{
    Text,
    Image,
    Video,
    Audio
}

/// <summary>One image attached to a moderation call.</summary>
public sealed record ModerationImageInput(string ContentType, byte[] Bytes);

/// <summary>
/// The content one call classifies: text and/or images. A video is represented by sampled frames
/// plus its transcript, never by the original media.
/// </summary>
public sealed record ModerationClassifyRequest(
    string? Text,
    IReadOnlyList<ModerationImageInput> Images,
    string Model);

/// <summary>The union of <c>results[]</c> in an OpenAI moderation response.</summary>
public sealed record ModerationClassification(
    bool Flagged,
    IReadOnlyDictionary<string, double> Scores,
    IReadOnlyDictionary<string, bool> FlaggedCategories,
    IReadOnlyList<string> InputTypes);

/// <summary>The policy's answer for one classification, with the message shown to the user.</summary>
public sealed record ModerationPolicyVerdict(
    ModerationAction Action,
    string? TopCategory,
    double TopScore,
    string? ReasonCode,
    string UserMessage);

/// <summary>Who and what a moderation call is attributed to in the audit row and usage event.</summary>
public sealed record ModerationAttribution(
    Guid OrganizationId,
    Guid? ProjectId,
    Guid? UserId,
    Guid? ExecutionId,
    Guid? StepResultId,
    ModerationSurface Surface,
    ModerationContentKind Kind,
    string? SourceVersion = null);
