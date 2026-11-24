namespace ReelBolt.Shared.Moderation;

/// <summary>
/// Classifies text and/or images against a provider's moderation model. Implementations never
/// throw vendor exceptions at the caller: an outage, timeout or malformed answer surfaces as
/// <see cref="ContentModerationUnavailableException"/> so the gate can decide fail-open /
/// fail-closed policy, while a CANCELLED caller is re-thrown as <see cref="OperationCanceledException"/>.
/// </summary>
public interface IContentModerationClient
{
    Task<ModerationClassification> ClassifyAsync(ModerationClassifyRequest request, CancellationToken ct);
}

/// <summary>
/// Moderation could not be performed (non-2xx, invalid JSON, or the short per-call timeout). The
/// caller decides the stance: fail open on prompts/ingest/generated text, fail closed at publish.
/// </summary>
public sealed class ContentModerationUnavailableException : Exception
{
    public ContentModerationUnavailableException(string message)
        : base(message)
    {
    }

    public ContentModerationUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
