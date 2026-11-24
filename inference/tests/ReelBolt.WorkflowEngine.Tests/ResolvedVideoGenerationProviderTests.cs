using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Covers <see cref="ResolvedVideoGenerationProvider"/> cache key stability and credential redaction.
/// </summary>
public class ResolvedVideoGenerationProviderTests
{
    [Fact]
    public void Two_instances_with_identical_Kind_Endpoint_ModelName_ApiKey_TimeoutSeconds_produce_the_same_CacheKey()
    {
        var provider1 = new ResolvedVideoGenerationProvider(
            ProviderId: System.Guid.NewGuid(),
            Name: "Provider 1",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "https://api.example.com",
            ModelName: "video-gen-1",
            ApiKey: "test-key-abc123",
            TimeoutSeconds: 3600);

        var provider2 = new ResolvedVideoGenerationProvider(
            ProviderId: System.Guid.NewGuid(),
            Name: "Provider 2",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "https://api.example.com",
            ModelName: "video-gen-1",
            ApiKey: "test-key-abc123",
            TimeoutSeconds: 3600);

        provider1.CacheKey.Should().Be(provider2.CacheKey);
    }

    [Fact]
    public void Changing_only_ApiKey_changes_CacheKey()
    {
        var providerId = System.Guid.NewGuid();

        var provider1 = new ResolvedVideoGenerationProvider(
            ProviderId: providerId,
            Name: "Provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "https://api.example.com",
            ModelName: "video-gen-1",
            ApiKey: "test-key-123",
            TimeoutSeconds: 3600);

        var provider2 = new ResolvedVideoGenerationProvider(
            ProviderId: providerId,
            Name: "Provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "https://api.example.com",
            ModelName: "video-gen-1",
            ApiKey: "test-key-456",
            TimeoutSeconds: 3600);

        provider1.CacheKey.Should().NotBe(provider2.CacheKey);
    }

    [Fact]
    public void ToString_output_never_contains_the_raw_ApiKey_substring_for_a_nontrivial_key_value()
    {
        const string apiKey = "test-key-abcdefghijk123456789";
        var provider = new ResolvedVideoGenerationProvider(
            ProviderId: System.Guid.NewGuid(),
            Name: "Test Provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "https://api.example.com",
            ModelName: "video-gen-1",
            ApiKey: apiKey,
            TimeoutSeconds: 3600);

        string toString = provider.ToString();

        toString.Should().NotContain(apiKey);
        // ToString should contain Name and Kind/ModelName info
        toString.Should().Contain("Test Provider");
        toString.Should().Contain("video-gen-1");
    }
}
