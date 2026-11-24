namespace ReelBolt.Shared.Inference;

/// <summary>
/// Factory for constructing and caching <see cref="ISpeechSynthesisClient"/> instances.
/// </summary>
public interface ISpeechSynthesisClientFactory
{
    /// <summary>
    /// Gets or constructs an <see cref="ISpeechSynthesisClient"/> for the given provider.
    /// Caches by <see cref="ResolvedSpeechSynthesisProvider.CacheKey"/>, so repeated calls
    /// with equivalent providers reuse the same client.
    /// </summary>
    ISpeechSynthesisClient Get(ResolvedSpeechSynthesisProvider provider);
}
