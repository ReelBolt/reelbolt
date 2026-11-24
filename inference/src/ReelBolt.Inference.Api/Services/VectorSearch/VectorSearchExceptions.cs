namespace ReelBolt.Inference.Api.Services.VectorSearch;

public class VectorSearchException : Exception
{
    public VectorSearchException(string message)
        : base(message)
    {
    }

    public VectorSearchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class IndexNotReadyException : VectorSearchException
{
    public IndexNotReadyException(string message)
        : base(message)
    {
    }

    public IndexNotReadyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// No usable embedding backend: no <c>Embedding</c>-capability provider row (default or explicit)
/// and no legacy <c>AzureOpenAI:*</c> configuration, or the resolved provider could not be built.
/// Surfaces as "index not ready" on search rather than a 500.
/// </summary>
public sealed class EmbeddingProviderUnavailableException : VectorSearchException
{
    public EmbeddingProviderUnavailableException(string message)
        : base(message)
    {
    }

    public EmbeddingProviderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
