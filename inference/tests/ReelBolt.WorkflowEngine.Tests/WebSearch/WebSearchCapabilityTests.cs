using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.WebSearch;

/// <summary>Resolution, admin boundary and metering of the WebSearch capability. Offline.</summary>
public sealed class WebSearchCapabilityTests
{
    // ------------------------------------------------------------ resolver

    [Fact]
    public async Task Explicit_row_wins_over_the_default()
    {
        ModelCapabilityView def = Row(isDefault: true, "default");
        ModelCapabilityView explicitRow = Row(isDefault: false, "explicit");

        ResolvedWebSearchProvider? r = await Resolver([def, explicitRow]).ResolveWebSearchAsync(explicitRow.Id, CancellationToken.None);

        r!.ProviderId.Should().Be(explicitRow.Id);
        r.Name.Should().Be("explicit");
        r.Kind.Should().Be(InferenceProviderKind.DeepSeek);
        r.ModelName.Should().Be("deepseek-search");
        r.IsPlatformManaged.Should().BeTrue();
    }

    [Fact]
    public async Task Default_is_used_without_an_explicit_id_and_a_chat_id_falls_through()
    {
        ModelCapabilityView chat = Row(isDefault: true, "chat", InferenceProviderCapability.Chat);
        ModelCapabilityView def = Row(isDefault: true, "default");

        (await Resolver([chat, def]).ResolveWebSearchAsync(null, CancellationToken.None))!.ProviderId.Should().Be(def.Id);
        (await Resolver([chat, def]).ResolveWebSearchAsync(chat.Id, CancellationToken.None))!.ProviderId.Should().Be(def.Id);
    }

    [Fact]
    public async Task Null_when_nothing_serves_WebSearch_and_the_key_is_redacted()
    {
        ModelCapabilityView chat = Row(isDefault: true, "chat", InferenceProviderCapability.Chat);
        (await Resolver([chat]).ResolveWebSearchAsync(null, CancellationToken.None)).Should().BeNull();

        ModelCapabilityView def = Row(isDefault: true, "default", apiKey: "sk-secret-value");
        ResolvedWebSearchProvider r = (await Resolver([def]).ResolveWebSearchAsync(null, CancellationToken.None))!;
        r.ApiKey.Should().Be("sk-secret-value");
        r.ToString().Should().NotContain("sk-secret-value");
        r.CacheKey.Should().NotContain("sk-secret-value");
    }

    // ------------------------------------------------------------ admin boundary

    public static IEnumerable<object[]> Combinations()
    {
        foreach (InferenceProviderKind kind in ProviderCapabilityUsage.Kinds)
        {
            bool allowedByKind = kind is InferenceProviderKind.DeepSeek or InferenceProviderKind.Gemini
                or InferenceProviderKind.OpenAICompatible;
            yield return [kind, "https://api.openai.com/v1", allowedByKind];
        }

        yield return [InferenceProviderKind.OpenAICompatible, "https://litellm.example.test/v1", false];
        yield return [InferenceProviderKind.OpenAICompatible, "https://api.openai.com.evil.test/v1", false];
        yield return [InferenceProviderKind.OpenAICompatible, "http://api.openai.com/v1", false];
        yield return [InferenceProviderKind.OpenAICompatible, "https://API.OPENAI.COM/v1", true];
        yield return [InferenceProviderKind.DeepSeek, "https://api.deepseek.com", true];
        yield return [InferenceProviderKind.Gemini, "https://generativelanguage.googleapis.com", true];
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void WebSearch_allowlist_table(InferenceProviderKind kind, string endpoint, bool allowed)
    {
        bool rejected = InferenceProvidersController.IsUnsupportedCombination(
            kind, InferenceProviderCapability.WebSearch, endpoint, out string error);
        rejected.Should().Be(!allowed, $"{kind} @ {endpoint}: {error}");
        if (rejected) error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Other_capabilities_of_these_kinds_are_unchanged_by_the_endpoint_overload()
    {
        InferenceProvidersController.IsUnsupportedCombination(
            InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Chat, "https://litellm.example.test/v1", out _)
            .Should().BeFalse();
        InferenceProvidersController.IsUnsupportedCombination(
            InferenceProviderKind.DeepSeek, InferenceProviderCapability.Embedding, "https://api.deepseek.com", out _)
            .Should().Be(InferenceProvidersController.IsUnsupportedCombination(
                InferenceProviderKind.DeepSeek, InferenceProviderCapability.Embedding, out _));
    }

    // ------------------------------------------------------------ metering

    private static readonly WebSearchResponse OpenAiResponse = new(
        "openai", [], "a", ["q"], 2, null, false, InputTokens: 120, OutputTokens: 30);

    [Fact]
    public async Task Metering_records_units_and_tokens_for_a_platform_row()
    {
        RecordingRecorder rec = new();
        ResolvedWebSearchProvider p = new(Guid.NewGuid(), "ws", InferenceProviderKind.OpenAICompatible,
            "https://api.openai.com/v1", "gpt-4o-mini", "k", 20, AccountId: Guid.NewGuid());
        MeteringWebSearchClient client = new(new FakeClient(OpenAiResponse), p, rec);

        await client.SearchAsync(new WebSearchRequest("q", 3), default);

        rec.Requests.Select(r => (r.Kind, r.Quantity, r.Capability)).Should().Equal(
            (UsageKind.WebSearches, 2m, (InferenceProviderCapability?)InferenceProviderCapability.WebSearch),
            (UsageKind.LlmInputTokens, 120m, InferenceProviderCapability.Chat),
            (UsageKind.LlmOutputTokens, 30m, InferenceProviderCapability.Chat));
        rec.Requests.Should().OnlyContain(r => r.BillingMode == UsageBillingMode.Platform && r.ModelName == "gpt-4o-mini"
                                               && r.AccountId == p.AccountId);
    }

    [Fact]
    public async Task Metering_bills_byo_rows_as_byo_and_skips_zero_units()
    {
        RecordingRecorder rec = new();
        ResolvedWebSearchProvider p = new(Guid.NewGuid(), "ws", InferenceProviderKind.DeepSeek, "", "deepseek-search", "k", 20,
            OrganizationId: Guid.NewGuid(), IsPlatformManaged: false);

        await new MeteringWebSearchClient(new FakeClient(new("deepseek", [], null, [], 0, null, false)), p, rec)
            .SearchAsync(new WebSearchRequest("q", 3), default);
        rec.Requests.Should().BeEmpty("an empty DeepSeek result is not billed");

        await new MeteringWebSearchClient(new FakeClient(new("deepseek", [], null, [], 1, null, false)), p, rec)
            .SearchAsync(new WebSearchRequest("q", 3), default);
        rec.Requests.Should().ContainSingle().Which.BillingMode.Should().Be(UsageBillingMode.Byo);
    }

    [Fact]
    public async Task A_recorder_failure_never_fails_the_search_but_the_inner_failure_propagates()
    {
        ResolvedWebSearchProvider p = new(Guid.NewGuid(), "ws", InferenceProviderKind.OpenAICompatible,
            "https://api.openai.com/v1", "gpt-4o-mini", "k", 20);
        MeteringWebSearchClient ok = new(new FakeClient(OpenAiResponse), p, new RecordingRecorder { Throw = true });

        WebSearchResponse r = await ok.SearchAsync(new WebSearchRequest("q", 3), default);
        r.Should().BeSameAs(OpenAiResponse);

        RecordingRecorder rec = new();
        MeteringWebSearchClient failing = new(
            new FakeClient(null, new WebSearchException(WebSearchException.RateLimited, "x")), p, rec);
        Func<Task> act = () => failing.SearchAsync(new WebSearchRequest("q", 3), default);
        await act.Should().ThrowAsync<WebSearchException>();
        rec.Requests.Should().BeEmpty("a failed search is never metered");
    }

    [Fact]
    public async Task Cancellation_from_the_recorder_is_rethrown()
    {
        ResolvedWebSearchProvider p = new(Guid.NewGuid(), "ws", InferenceProviderKind.DeepSeek, "", "m", "k", 20);
        MeteringWebSearchClient client = new(
            new FakeClient(OpenAiResponse), p, new RecordingRecorder { Throw = true, Exception = new OperationCanceledException() });
        Func<Task> act = () => client.SearchAsync(new WebSearchRequest("q", 3), default);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void The_factory_returns_the_metered_client_when_a_recorder_exists()
    {
        ResolvedWebSearchProvider p = new(Guid.NewGuid(), "ws", InferenceProviderKind.DeepSeek, "", "m", "k", 20);
        new WebSearchClientFactory(new RecordingRecorder()).Create(p).Should().BeOfType<MeteringWebSearchClient>();
    }

    [Fact]
    public void Prices_exist_for_the_web_search_units_and_the_capability_table_lists_them()
    {
        ProviderCapabilityUsage.ByCapability[InferenceProviderCapability.WebSearch].Should().Equal(UsageKind.WebSearches);
        ModelPriceCatalog.Priced
            .Where(m => m.Capability == InferenceProviderCapability.WebSearch)
            .Select(m => m.ProviderKind).Distinct()
            .Should().BeEquivalentTo(new[]
            {
                InferenceProviderKind.DeepSeek, InferenceProviderKind.OpenAICompatible, InferenceProviderKind.Gemini
            });
    }

    private sealed class FakeClient(WebSearchResponse? response, Exception? error = null) : IWebSearchClient
    {
        public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken ct) =>
            error is not null ? Task.FromException<WebSearchResponse>(error) : Task.FromResult(response!);
    }

    private sealed class RecordingRecorder : IUsageRecorder
    {
        public List<UsageRecordRequest> Requests { get; } = [];
        public bool Throw { get; init; }
        public Exception Exception { get; init; } = new InvalidOperationException("db down");

        public ValueTask<long> RecordAsync(UsageRecordRequest request, CancellationToken ct = default)
        {
            if (Throw) throw Exception;
            Requests.Add(request);
            return ValueTask.FromResult(1L);
        }
    }

    // ------------------------------------------------------------ helpers

    private static ModelCapabilityView Row(
        bool isDefault, string name, InferenceProviderCapability capability = InferenceProviderCapability.WebSearch,
        string? apiKey = null) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = InferenceProviderKind.DeepSeek,
            Endpoint = "https://api.deepseek.com",
            ModelName = "deepseek-search",
            ApiKeyEncrypted = apiKey,
            IsDefault = isDefault,
            IsEnabled = true,
            Capability = capability
        };

    private static InferenceProviderResolver Resolver(IReadOnlyList<ModelCapabilityView> providers)
    {
        ServiceCollection services = new();
        services.AddSingleton<IInferenceProviderStore>(new FakeStore(providers));
        ServiceProvider sp = services.BuildServiceProvider();
        return new InferenceProviderResolver(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            new PassthroughSecretProtector(),
            NullLogger<InferenceProviderResolver>.Instance);
    }

    private sealed class FakeStore(IReadOnlyList<ModelCapabilityView> providers) : IInferenceProviderStore
    {
        public Task<IReadOnlyList<ModelCapabilityView>> LoadEnabledAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelCapabilityView>>(
                providers.Where(p => p.OrganizationId is null || p.OrganizationId == organizationId).ToList());

        public Task<IReadOnlyDictionary<Guid, Guid?>> LoadAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid?>>(new Dictionary<Guid, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadOrgAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadBuiltInAgentOverridesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlySet<Guid>> LoadUnpricedCapabilityIdsAsync(DateTime at, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    }

    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = ciphertext ?? string.Empty;
            return ciphertext != null;
        }
    }
}
