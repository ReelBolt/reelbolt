using Microsoft.Extensions.AI;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.VectorSearch;

/// <summary>
/// Embedding generator that resolves its backend per call through
/// <see cref="IInferenceProviderResolver.ResolveEmbeddingAsync"/> (60s TTL-cached, so a provider
/// change in the admin UI takes effect without a restart) and stamps every returned
/// <see cref="Embedding{T}.ModelId"/> with the model that produced it. The vector index keys its
/// collections on that id plus the vector length, so switching provider/model never mixes vector
/// spaces or dimensions in one collection.
/// </summary>
/// <remarks>
/// Resolution happens lazily, inside <see cref="GenerateAsync"/>: a missing or invalid embedding
/// config must only fail vector search, never the file-management endpoints whose controller
/// constructs this transitively (found by e2e QA, see <c>ProjectFilesController</c>).
/// </remarks>
public sealed class ProviderEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IInferenceProviderResolver _resolver;
    private readonly IEmbeddingClientFactory _factory;

    public ProviderEmbeddingGenerator(IInferenceProviderResolver resolver, IEmbeddingClientFactory factory)
    {
        _resolver = resolver;
        _factory = factory;
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ResolvedEmbeddingProvider? resolved;
        try
        {
            resolved = await _resolver.ResolveEmbeddingAsync(null, cancellationToken);
        }
        catch (ModelUnpricedException unpriced)
        {
            // An unpriced Platform embedding model: vector search is unavailable, in the refusal's own words.
            throw new EmbeddingProviderUnavailableException(unpriced.Message, unpriced);
        }

        ResolvedEmbeddingProvider provider = resolved
            ?? throw new EmbeddingProviderUnavailableException(
                "Vector search is unavailable: no Embedding provider is configured. Add one in " +
                "Admin > Inference providers (Capability: Embedding, set as default), or set " +
                "AzureOpenAI:Endpoint/ApiKey. File upload/download/list/move/delete work without it.");

        IEmbeddingGenerator<string, Embedding<float>> client;
        try
        {
            client = _factory.Get(provider);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EmbeddingProviderUnavailableException(
                $"Embedding provider '{provider.Name}' could not be constructed: {ex.Message}", ex);
        }

        GeneratedEmbeddings<Embedding<float>> result = await client.GenerateAsync(values, options, cancellationToken);
        foreach (Embedding<float> embedding in result)
        {
            embedding.ModelId = provider.ModelName;
        }

        return result;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
