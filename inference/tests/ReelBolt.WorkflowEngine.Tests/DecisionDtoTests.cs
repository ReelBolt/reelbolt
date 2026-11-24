using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class DecisionDtoTests
{
    [Fact]
    public void DecisionAnswer_Noul_with_null_Confidence_can_be_constructed()
    {
        // Arrange & Act
        var answer = new DecisionAnswer(
            Name: "test_decision",
            Kind: DecisionKind.Noul,
            Choice: null,
            Score: null,
            Noul: 0.5,
            Confidence: null,
            Probabilities: new Dictionary<string, double>());

        // Assert
        answer.Should().NotBeNull();
        answer.Kind.Should().Be(DecisionKind.Noul);
        answer.Confidence.Should().BeNull();
        answer.Noul.Should().Be(0.5);
    }

    [Fact]
    public void ResolvedDecisionProvider_with_different_ApiKeys_produces_different_CacheKeys()
    {
        // Arrange
        var provider1 = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "test_provider",
            Kind: InferenceProviderKind.TypeSafe,
            Endpoint: "https://api.example.com",
            ModelName: "decision-model-v1",
            ApiKey: "secret-key-1",
            TimeoutSeconds: 30);

        var provider2 = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "test_provider",
            Kind: InferenceProviderKind.TypeSafe,
            Endpoint: "https://api.example.com",
            ModelName: "decision-model-v1",
            ApiKey: "secret-key-2",
            TimeoutSeconds: 30);

        // Act & Assert
        provider1.CacheKey.Should().NotBe(provider2.CacheKey);
    }

    [Fact]
    public void ResolvedDecisionProvider_ToString_never_contains_raw_ApiKey()
    {
        // Arrange
        const string apiKey = "super-secret-key-12345";
        var provider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "test_provider",
            Kind: InferenceProviderKind.TypeSafe,
            Endpoint: "https://api.example.com",
            ModelName: "decision-model-v1",
            ApiKey: apiKey,
            TimeoutSeconds: 30);

        // Act
        var toStringOutput = provider.ToString();

        // Assert
        toStringOutput.Should().NotContain(apiKey);
        toStringOutput.Should().Contain("test_provider");
        toStringOutput.Should().Contain("TypeSafe");
        toStringOutput.Should().Contain("decision-model-v1");
    }
}
