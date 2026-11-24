using Microsoft.Extensions.AI;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Builds (and caches) an <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> for a resolved
/// embedding provider. Mirrors <see cref="ITranscriptionClientFactory"/>.
/// </summary>
public interface IEmbeddingClientFactory
{
    IEmbeddingGenerator<string, Embedding<float>> Get(ResolvedEmbeddingProvider provider);
}
