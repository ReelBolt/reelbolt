using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Moderation;

/// <summary>
/// Builds a moderation client for a resolved provider. Both gates inject this interface so a test
/// can supply a fake <see cref="IContentModerationClient"/> without an HTTP stack. DI registration
/// lands with the gate implementations (Tasks 2 and 5).
/// </summary>
public interface IContentModerationClientFactory
{
    IContentModerationClient Create(ResolvedContentModerationProvider provider, ContentModerationOptions options);
}

/// <summary>The production factory: one OpenAI-compatible client per call (the provider's own HTTP client is optional).</summary>
public sealed class ContentModerationClientFactory : IContentModerationClientFactory
{
    public IContentModerationClient Create(ResolvedContentModerationProvider provider, ContentModerationOptions options) =>
        new ContentModerationClient(provider, options ?? new ContentModerationOptions());
}
