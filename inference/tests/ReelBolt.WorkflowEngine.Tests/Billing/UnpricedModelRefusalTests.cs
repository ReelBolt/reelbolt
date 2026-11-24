using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// T-030: the API-side paths that resolve an embedding model with no request to answer (vector search, the docs
/// index's background loop) treat an unpriced Platform model (<see cref="ModelUnpricedException"/>) as "unavailable",
/// never as an exception that escapes: a background loop that threw would stop the host.
/// </summary>
public sealed class UnpricedModelRefusalTests
{
    private static readonly ModelUnpricedException Refusal = new(InferenceProviderCapability.Embedding, "Pricey embeddings");

    private static Mock<IInferenceProviderResolver> UnpricedEmbeddings()
    {
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveEmbeddingAsync(null, It.IsAny<CancellationToken>())).ThrowsAsync(Refusal);
        return resolver;
    }

    [Fact]
    public async Task Vector_search_with_an_unpriced_embedding_model_is_unavailable_and_says_why()
    {
        ProviderEmbeddingGenerator generator = new(UnpricedEmbeddings().Object, Mock.Of<IEmbeddingClientFactory>());

        Func<Task> embed = () => generator.GenerateAsync(["a question"]);

        (await embed.Should().ThrowAsync<EmbeddingProviderUnavailableException>())
            .Which.Message.Should().Be(Refusal.Message);
    }

    [Fact]
    public async Task The_docs_index_with_an_unpriced_embedding_model_reports_not_indexed_instead_of_throwing()
    {
        PlatformDocsCorpus corpus = new([new PlatformDocsChunk("c1", "a.md", PlatformDocsAudience.User, "T", "S", "/docs/a", "text")]);
        QdrantPlatformDocsIndex index = new(
            Options.Create(new VectorSearchOptions()),
            Options.Create(new PlatformDocsOptions()),
            new PlatformDocsCorpusProvider(corpus),
            UnpricedEmbeddings().Object,
            Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(),
            NullLogger<QdrantPlatformDocsIndex>.Instance);

        (await index.EnsureIndexedAsync(CancellationToken.None)).Should().BeFalse("the loop retries later, once a price is added");
    }
}
