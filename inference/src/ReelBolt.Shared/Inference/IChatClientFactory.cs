using Microsoft.Extensions.AI;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Builds (and caches) <see cref="IChatClient"/> instances for a resolved inference provider.
/// </summary>
public interface IChatClientFactory
{
    IChatClient Get(ResolvedInferenceProvider provider);
}
