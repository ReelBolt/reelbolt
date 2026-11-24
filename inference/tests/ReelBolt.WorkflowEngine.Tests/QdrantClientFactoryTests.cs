using System;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.VectorSearch;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class QdrantClientFactoryTests
{
    [Fact]
    public void Defaults_Are_Valid_And_Keyless()
    {
        VectorSearchOptions o = new();
        o.QdrantApiKey.Should().BeNull();
        QdrantClientFactory.Validate(o).Should().BeNull();
    }

    [Fact]
    public void Http_Without_Key_Is_Allowed()
    {
        QdrantClientFactory.Validate(new VectorSearchOptions { QdrantUrl = "http://qdrant:6333" }).Should().BeNull();
        QdrantClientFactory.Validate(new VectorSearchOptions { QdrantUrl = "http://qdrant:6333", QdrantApiKey = "  " }).Should().BeNull();
    }

    [Fact]
    public void Key_With_Http_Is_Rejected()
    {
        string? error = QdrantClientFactory.Validate(new VectorSearchOptions { QdrantUrl = "http://qdrant:6333", QdrantApiKey = "k" });
        error.Should().Contain("not https");
        Action create = () => QdrantClientFactory.Create(new VectorSearchOptions { QdrantUrl = "http://x:6333", QdrantApiKey = "k" });
        create.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Key_With_Https_Is_Valid_And_Builds()
    {
        VectorSearchOptions o = new() { QdrantUrl = "https://x.cloud.qdrant.io:6333", QdrantApiKey = "k" };
        QdrantClientFactory.Validate(o).Should().BeNull();
        QdrantClientFactory.Create(o).Should().NotBeNull();
    }

    [Fact]
    public void Relative_Url_Is_Rejected()
    {
        QdrantClientFactory.Validate(new VectorSearchOptions { QdrantUrl = "nonsense" }).Should().NotBeNull();
    }
}
