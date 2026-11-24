namespace ReelBolt.Shared.Inference;

/// <summary>
/// Factory for creating <see cref="IDecisionClient"/> instances based on a resolved decision provider,
/// with caching by <see cref="ResolvedDecisionProvider.CacheKey"/>.
/// </summary>
public interface IDecisionClientFactory
{
    /// <summary>
    /// Gets or creates an <see cref="IDecisionClient"/> for the given provider, caching by CacheKey.
    /// </summary>
    /// <param name="provider">The resolved decision provider configuration.</param>
    /// <returns>An <see cref="IDecisionClient"/> suitable for the provider's kind.</returns>
    /// <exception cref="NotSupportedException">If the provider kind does not support decision-making.</exception>
    IDecisionClient Get(ResolvedDecisionProvider provider);
}
