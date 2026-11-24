using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PlatformDocs;

/// <summary>The naming and hashing incremental re-indexing depends on (docs/platform-docs-search.md).</summary>
public class PlatformDocsIndexNamingTests
{
    private static PlatformDocsChunk Chunk(string text) =>
        new("id", "docs/a.md", PlatformDocsAudience.User, "Title", "Section", "/docs/a", text);

    [Fact]
    public void Every_corpus_version_of_one_model_shares_a_prefix_and_other_models_do_not()
    {
        string prefix = QdrantPlatformDocsIndex.ModelPrefix("Qwen/Qwen3-Embedding-0.6B");

        QdrantPlatformDocsIndex.CollectionName("Qwen/Qwen3-Embedding-0.6B", "aaaa").Should().StartWith(prefix);
        QdrantPlatformDocsIndex.CollectionName("Qwen/Qwen3-Embedding-0.6B", "bbbb").Should().StartWith(prefix);
        QdrantPlatformDocsIndex.CollectionName("text-embedding-3-small", "aaaa").Should().NotStartWith(prefix);
    }

    [Fact]
    public void Embed_hash_changes_with_the_embedded_text_only()
    {
        QdrantPlatformDocsIndex.EmbedHash(Chunk("same")).Should().Be(QdrantPlatformDocsIndex.EmbedHash(Chunk("same")));
        QdrantPlatformDocsIndex.EmbedHash(Chunk("same")).Should().NotBe(QdrantPlatformDocsIndex.EmbedHash(Chunk("edited")));
    }
}
