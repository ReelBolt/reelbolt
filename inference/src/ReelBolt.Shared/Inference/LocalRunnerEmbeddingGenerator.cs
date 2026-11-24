using Microsoft.Extensions.AI;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Turns what the runner gateway's relay answers to an embeddings call into a
/// <see cref="LocalRunnerUnavailableException"/>, exactly as <see cref="LocalRunnerChatClient"/> does for chat.
/// </summary>
internal sealed class LocalRunnerEmbeddingGenerator : DelegatingEmbeddingGenerator<string, Embedding<float>>
{
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    public LocalRunnerEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, Microsoft.Extensions.Logging.ILogger? logger = null) : base(inner)
    {
        _logger = logger;
    }

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GenerateAsync(values, options, cancellationToken);
        }
        catch (Exception ex) when (LocalRunnerChatClient.Translate(ex, cancellationToken, _logger) is { } mapped)
        {
            throw mapped;
        }
    }
}
