using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class EmbeddingCapabilityTests
{
    [Theory]
    [InlineData(InferenceProviderKind.AzureOpenAI)]
    [InlineData(InferenceProviderKind.OpenAICompatible)]
    public void Embedding_is_allowed_for_Azure_and_OpenAICompatible(InferenceProviderKind kind)
    {
        InferenceProvidersController.IsUnsupportedCombination(kind, InferenceProviderCapability.Embedding, out _)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(InferenceProviderKind.DeepSeek, "no embeddings endpoint")]
    [InlineData(InferenceProviderKind.Anthropic, "no embeddings API")]
    [InlineData(InferenceProviderKind.TypeSafe, "does not serve embeddings")]
    [InlineData(InferenceProviderKind.FishAudio, "does not serve embeddings")]
    public void Embedding_is_rejected_for_other_kinds(InferenceProviderKind kind, string reason)
    {
        InferenceProvidersController.IsUnsupportedCombination(kind, InferenceProviderCapability.Embedding, out string error)
            .Should().BeTrue();
        error.Should().Contain(reason);
    }

    [Fact]
    public void Factory_rejects_DeepSeek_with_an_explanation()
    {
        ResolvedEmbeddingProvider p = new(null, "ds", InferenceProviderKind.DeepSeek, "https://api.deepseek.com", "m", "k", 30);
        Action act = () => new EmbeddingClientFactory().Get(p);
        act.Should().Throw<NotSupportedException>().WithMessage("*DeepSeek*no embeddings endpoint*");
    }

    [Fact]
    public void Factory_builds_and_caches_an_OpenAICompatible_generator_without_a_key()
    {
        ResolvedEmbeddingProvider p = new(null, "local", InferenceProviderKind.OpenAICompatible, "http://embeddings:80", "Qwen/Qwen3-Embedding-0.6B", "", 30);
        EmbeddingClientFactory f = new();
        f.Get(p).Should().BeSameAs(f.Get(p));
    }

    [Fact]
    public void Collection_name_differs_per_model_and_dimension_and_is_project_scoped()
    {
        Guid project = Guid.NewGuid();
        string a = EmbeddingSpace.CollectionName(project, "Qwen/Qwen3-Embedding-0.6B", 1024);
        string b = EmbeddingSpace.CollectionName(project, "text-embedding-3-large", 3072);
        string c = EmbeddingSpace.CollectionName(project, "text-embedding-3-large", 1536);

        new[] { a, b, c }.Distinct().Should().HaveCount(3);
        a.Should().Be(EmbeddingSpace.CollectionName(project, "Qwen/Qwen3-Embedding-0.6B", 1024));
        a.Should().MatchRegex("^[0-9a-f]{32}_[a-z0-9-]+_[0-9a-f]{8}_1024$");
        EmbeddingSpace.BelongsToProject(project, a).Should().BeTrue();
        EmbeddingSpace.BelongsToProject(project, project.ToString("N")).Should().BeTrue();
        EmbeddingSpace.BelongsToProject(Guid.NewGuid(), a).Should().BeFalse();
    }
}
