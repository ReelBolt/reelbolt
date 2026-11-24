using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class DecisionClientFactoryTests
{
    [Fact]
    public void Get_returns_a_TypeSafeDecisionClient_for_Kind_TypeSafe()
    {
        // Arrange
        var provider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "TypeSafe Provider",
            Kind: InferenceProviderKind.TypeSafe,
            Endpoint: "https://api.typesafe.ai/",
            ModelName: "jev-latest",
            ApiKey: "fake-key-12345",
            TimeoutSeconds: 30);

        var factory = new DecisionClientFactory();

        // Act
        var result = factory.Get(provider);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("TypeSafeDecisionClient", result.GetType().Name);
    }

    [Fact]
    public void Get_returns_a_LogprobDecisionClient_for_Kind_AzureOpenAI()
    {
        // Arrange
        var provider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "Azure OpenAI Provider",
            Kind: InferenceProviderKind.AzureOpenAI,
            Endpoint: "https://fake-deployment.openai.azure.com/",
            ModelName: "gpt-4",
            ApiKey: "fake-azure-key",
            TimeoutSeconds: 30);

        var factory = new DecisionClientFactory();

        // Act
        var result = factory.Get(provider);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("LogprobDecisionClient", result.GetType().Name);
    }

    [Fact]
    public void Get_returns_a_LogprobDecisionClient_for_Kind_OpenAICompatible()
    {
        // Arrange
        var provider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "OpenAI Compatible Provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "http://localhost:8000/v1/",
            ModelName: "model-name",
            ApiKey: "fake-key",
            TimeoutSeconds: 30);

        var factory = new DecisionClientFactory();

        // Act
        var result = factory.Get(provider);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("LogprobDecisionClient", result.GetType().Name);
    }

    [Fact]
    public void Get_returns_a_LogprobDecisionClient_for_Kind_DeepSeek()
    {
        // Arrange
        var provider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "DeepSeek Provider",
            Kind: InferenceProviderKind.DeepSeek,
            Endpoint: "https://api.deepseek.com/",
            ModelName: "deepseek-chat",
            ApiKey: "fake-deepseek-key",
            TimeoutSeconds: 30);

        var factory = new DecisionClientFactory();

        // Act
        var result = factory.Get(provider);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("LogprobDecisionClient", result.GetType().Name);
    }

    [Fact]
    public void Get_throws_NotSupportedException_for_Anthropic_and_Gemini()
    {
        // Arrange
        var anthropicProvider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "Anthropic Provider",
            Kind: InferenceProviderKind.Anthropic,
            Endpoint: "https://api.anthropic.com/",
            ModelName: "claude-3-sonnet",
            ApiKey: "fake-anthropic-key",
            TimeoutSeconds: 30);

        var geminiProvider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "Gemini Provider",
            Kind: InferenceProviderKind.Gemini,
            Endpoint: "https://generativelanguage.googleapis.com/v1beta/openai/",
            ModelName: "gemini-pro",
            ApiKey: "fake-gemini-key",
            TimeoutSeconds: 30);

        var factory = new DecisionClientFactory();

        // Act & Assert for Anthropic
        var anthropicException = Assert.Throws<NotSupportedException>(() => factory.Get(anthropicProvider));
        Assert.Contains("Anthropic", anthropicException.Message);
        Assert.Contains("Decision", anthropicException.Message);

        // Act & Assert for Gemini
        var geminiException = Assert.Throws<NotSupportedException>(() => factory.Get(geminiProvider));
        Assert.Contains("Gemini", geminiException.Message);
        Assert.Contains("Decision", geminiException.Message);
    }

    [Fact]
    public void Get_caches_by_CacheKey_and_returns_the_same_instance_for_an_identical_provider()
    {
        // Arrange
        var provider1 = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "TypeSafe Provider 1",
            Kind: InferenceProviderKind.TypeSafe,
            Endpoint: "https://api.typesafe.ai/",
            ModelName: "jev-latest",
            ApiKey: "same-key",
            TimeoutSeconds: 30);

        var provider2 = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "TypeSafe Provider 2",
            Kind: InferenceProviderKind.TypeSafe,
            Endpoint: "https://api.typesafe.ai/",
            ModelName: "jev-latest",
            ApiKey: "same-key",
            TimeoutSeconds: 30);

        // Verify they have the same cache key
        Assert.Equal(provider1.CacheKey, provider2.CacheKey);

        var factory = new DecisionClientFactory();

        // Act
        var result1 = factory.Get(provider1);
        var result2 = factory.Get(provider2);

        // Assert
        Assert.True(ReferenceEquals(result1, result2));
    }

    // --- OpenJev (self-hosted System One) ---

    private static ResolvedDecisionProvider OpenJevProvider(
        string endpoint = "http://openjev.local:8791",
        string apiKey = "stored-key",
        string name = "OpenJev Provider") =>
        new(
            ProviderId: Guid.NewGuid(),
            Name: name,
            Kind: InferenceProviderKind.OpenJev,
            Endpoint: endpoint,
            ModelName: "open-jev-2b",
            ApiKey: apiKey,
            TimeoutSeconds: 30);

    /// <summary>
    /// Reads the bearer token the factory actually put on the client it returned.
    /// <see cref="TypeSafeDecisionClient"/> exposes no test seam for its HttpClient and is
    /// deliberately left unchanged by this work, so the field is read by reflection — the
    /// credential rule is worth observing on the client that will send the request rather than
    /// only on the helper that computes the value.
    /// </summary>
    private static string? BearerTokenOf(IDecisionClient client) =>
        HttpClientOf(client).DefaultRequestHeaders.Authorization?.Parameter;

    private static HttpClient HttpClientOf(IDecisionClient client) =>
        (HttpClient?)typeof(TypeSafeDecisionClient)
            .GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(client)
        ?? throw new InvalidOperationException(
            "TypeSafeDecisionClient no longer exposes its HttpClient as '_httpClient' — update this test's seam.");

    [Fact]
    public void Get_returns_a_TypeSafeDecisionClient_for_Kind_OpenJev()
    {
        // Arrange — the client is reused from the TypeSafe arm, not forked: both post v1/systemone.
        var provider = OpenJevProvider();
        var factory = new DecisionClientFactory();

        // Act
        var result = factory.Get(provider);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("TypeSafeDecisionClient", result.GetType().Name);
    }

    [Fact]
    public void Get_OpenJev_points_the_reused_client_at_the_configured_endpoint()
    {
        // Arrange
        var provider = OpenJevProvider(endpoint: "http://openjev.local:8791/");
        var factory = new DecisionClientFactory();

        // Act
        var result = factory.Get(provider);

        // Assert — the client issues a RELATIVE "v1/systemone", so the base address is what
        // decides which server answers; it must be the operator's endpoint, never a vendor URL.
        Assert.Equal("http://openjev.local:8791/", HttpClientOf(result).BaseAddress!.ToString());
    }

    [Fact]
    public void Get_OpenJev_sends_the_stored_key_verbatim_as_the_bearer_token()
    {
        // Arrange
        var provider = OpenJevProvider(apiKey: "kev-local-secret");
        var factory = new DecisionClientFactory();

        // Act
        var result = factory.Get(provider);

        // Assert
        Assert.Equal("kev-local-secret", BearerTokenOf(result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Get_OpenJev_sends_the_NoKeyPlaceholder_for_a_blank_stored_key(string storedKey)
    {
        // Arrange — a loopback Open-Jev/Kev deployment is routinely unauthenticated, so a blank
        // key is a valid configuration; sending an empty bearer would throw inside the header.
        var provider = OpenJevProvider(apiKey: storedKey);
        var factory = new DecisionClientFactory();

        // Act
        var result = factory.Get(provider);

        // Assert
        Assert.Equal("not-required", BearerTokenOf(result));
    }

    [Fact]
    public void Get_OpenJev_ignores_an_ambient_OPENJEV_API_KEY_environment_variable()
    {
        // Arrange — the rule this kind exists to keep: no ambient credential, ever. A row with no
        // key must not spend whatever happens to sit in the container's environment, because
        // executing a workflow requires no admin rights.
        string? original = Environment.GetEnvironmentVariable("OPENJEV_API_KEY");
        Environment.SetEnvironmentVariable("OPENJEV_API_KEY", "ambient-key-that-must-not-be-sent");

        try
        {
            var provider = OpenJevProvider(apiKey: string.Empty);
            var factory = new DecisionClientFactory();

            // Act
            var result = factory.Get(provider);

            // Assert
            Assert.Equal("not-required", BearerTokenOf(result));
            Assert.Equal(
                "not-required",
                DecisionClientFactory.SelectApiKey(provider.ApiKey));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENJEV_API_KEY", original);
        }
    }

    [Fact]
    public void SelectApiKey_returns_the_stored_key_untouched()
    {
        // Act & Assert — verbatim, not trimmed: a leading/trailing space is the operator's key.
        Assert.Equal("  spaced-key  ", DecisionClientFactory.SelectApiKey("  spaced-key  "));
        Assert.Equal("env:", DecisionClientFactory.SelectApiKey("env:"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SelectApiKey_falls_back_to_the_NoKeyPlaceholder_only(string? storedKey)
    {
        // Assert — the fallback value is the shared placeholder and nothing else: no environment
        // lookup, no vendor default key.
        Assert.Equal("not-required", DecisionClientFactory.SelectApiKey(storedKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Get_OpenJev_throws_naming_the_provider_when_the_endpoint_is_blank(string endpoint)
    {
        // Arrange
        var provider = OpenJevProvider(endpoint: endpoint, name: "My Self-Hosted Jev");
        var factory = new DecisionClientFactory();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => factory.Get(provider));

        // Assert — it must never silently default to a vendor URL.
        Assert.Contains("My Self-Hosted Jev", exception.Message);
        Assert.Contains("OpenJev", exception.Message);
        Assert.Contains("no endpoint", exception.Message);
    }

    [Theory]
    [InlineData("openjev:8791")]
    [InlineData("not-a-url")]
    public void Get_OpenJev_throws_naming_the_provider_when_the_endpoint_is_not_an_absolute_http_url(string endpoint)
    {
        // Arrange
        var provider = OpenJevProvider(endpoint: endpoint, name: "My Self-Hosted Jev");
        var factory = new DecisionClientFactory();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => factory.Get(provider));

        // Assert — a bare UriFormatException from deep inside the factory, or an HttpClient that
        // fails later on a non-http scheme, would not say which row is misconfigured.
        Assert.Contains("My Self-Hosted Jev", exception.Message);
        Assert.Contains("http(s) URL", exception.Message);
    }
}
