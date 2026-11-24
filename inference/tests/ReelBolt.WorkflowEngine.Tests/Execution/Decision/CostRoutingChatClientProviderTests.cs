using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Execution.Decision.CostRouting;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.Decision;

/// <summary>
/// Coverage for the routing seam itself: the pass-through when nothing is routed (which is what
/// keeps every unrouted step byte-identical), the two tiers a decision can select, the fall-back
/// when a selected provider cannot be used, and the recording that feeds the observation's
/// downstream outcome.
/// </summary>
public class CostRoutingChatClientProviderTests
{
    private readonly Mock<IAgentChatClientProvider> _inner = new();
    private readonly Mock<IInferenceProviderResolver> _resolver = new();
    private readonly Mock<IChatClientFactory> _factory = new();
    private readonly CostRoutingAmbientState _ambient = new();

    private readonly Guid _cheapProviderId = Guid.NewGuid();
    private readonly Guid _expensiveProviderId = Guid.NewGuid();

    public CostRoutingChatClientProviderTests()
    {
        _inner
            .Setup(p => p.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<IChatClient>(new Mock<IChatClient>().Object));
    }

    private CostRoutingChatClientProvider CreateSut() => new(
        _inner.Object,
        _resolver.Object,
        _factory.Object,
        _ambient,
        NullLogger<CostRoutingChatClientProvider>.Instance);

    private static ResolvedInferenceProvider Provider(Guid id, string name, string model) =>
        new(id, name, InferenceProviderKind.OpenAICompatible, "http://provider.example", model, "key", 300);

    private void SetupPinned(Guid id, string name, string model)
    {
        ResolvedInferenceProvider resolved = Provider(id, name, model);
        _resolver
            .Setup(r => r.ResolveChatAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);
        _factory.Setup(f => f.Get(resolved)).Returns(new Mock<IChatClient>().Object);
    }

    private static CostRoutingRunState State(bool escalated, Guid? cheap, Guid? expensive) => new()
    {
        ObservationId = Guid.NewGuid(),
        ExecutionId = Guid.NewGuid(),
        StepOrder = 1,
        AgentType = AgentType.AuthorAgent,
        Escalated = escalated,
        CheapProviderId = cheap,
        ExpensiveProviderId = expensive,
    };

    // --- The default path must be untouched ---

    [Fact]
    public async Task With_no_routing_intent_the_inner_provider_is_used_and_no_pinned_provider_is_resolved()
    {
        IChatClient client = await CreateSut().GetAsync(AgentType.AuthorAgent, null, CancellationToken.None);

        client.Should().NotBeNull();
        _inner.Verify(p => p.GetAsync(AgentType.AuthorAgent, null, It.IsAny<CancellationToken>()), Times.Once);
        _resolver.Verify(r => r.ResolveChatAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_non_escalated_decision_without_a_cheap_provider_also_uses_the_inner_provider()
    {
        CostRoutingRunState state = State(escalated: false, cheap: null, expensive: _expensiveProviderId);
        using IDisposable _ = _ambient.Begin(state);

        await CreateSut().GetAsync(AgentType.AuthorAgent, null, CancellationToken.None);

        _inner.Verify(p => p.GetAsync(AgentType.AuthorAgent, null, It.IsAny<CancellationToken>()), Times.Once);
        _resolver.Verify(r => r.ResolveChatAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Tier selection ---

    [Fact]
    public async Task An_escalated_decision_resolves_the_expensive_provider_instead_of_the_default()
    {
        SetupPinned(_expensiveProviderId, "expensive", "big-model");
        CostRoutingRunState state = State(escalated: true, cheap: _cheapProviderId, expensive: _expensiveProviderId);
        using IDisposable _ = _ambient.Begin(state);

        await CreateSut().GetAsync(AgentType.AuthorAgent, null, CancellationToken.None);

        _resolver.Verify(r => r.ResolveChatAsync(_expensiveProviderId, It.IsAny<CancellationToken>()), Times.Once);
        _inner.Verify(p => p.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        state.RoutedProviderId.Should().Be(_expensiveProviderId);
        state.RoutedModel.Should().Be("big-model");
    }

    [Fact]
    public async Task A_confident_decision_with_a_cheap_provider_pins_that_provider()
    {
        SetupPinned(_cheapProviderId, "cheap", "small-model");
        CostRoutingRunState state = State(escalated: false, cheap: _cheapProviderId, expensive: _expensiveProviderId);
        using IDisposable _ = _ambient.Begin(state);

        await CreateSut().GetAsync(AgentType.AuthorAgent, null, CancellationToken.None);

        _resolver.Verify(r => r.ResolveChatAsync(_cheapProviderId, It.IsAny<CancellationToken>()), Times.Once);
        state.RoutedProviderId.Should().Be(_cheapProviderId);
        state.RoutedModel.Should().Be("small-model");
    }

    // --- Soft failure: an unusable pin falls back to the default path ---

    [Fact]
    public async Task An_escalation_whose_provider_no_longer_resolves_falls_back_to_the_default()
    {
        _resolver
            .Setup(r => r.ResolveChatAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedInferenceProvider)null);

        CostRoutingRunState state = State(escalated: true, cheap: _cheapProviderId, expensive: _expensiveProviderId);
        using IDisposable _ = _ambient.Begin(state);

        IChatClient client = await CreateSut().GetAsync(AgentType.AuthorAgent, null, CancellationToken.None);

        client.Should().NotBeNull();
        _inner.Verify(p => p.GetAsync(AgentType.AuthorAgent, null, It.IsAny<CancellationToken>()), Times.Once);
        state.RoutedProviderId.Should().BeNull("the observation must record the provider the run actually used");
    }

    [Fact]
    public async Task A_failure_building_the_pinned_provider_falls_back_to_the_default_instead_of_throwing()
    {
        ResolvedInferenceProvider resolved = Provider(_expensiveProviderId, "expensive", "big-model");
        _resolver
            .Setup(r => r.ResolveChatAsync(_expensiveProviderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);
        _factory.Setup(f => f.Get(resolved)).Throws(new InvalidOperationException("no key"));

        CostRoutingRunState state = State(escalated: true, cheap: null, expensive: _expensiveProviderId);
        using IDisposable _ = _ambient.Begin(state);

        Exception exception = await Record.ExceptionAsync(
            async () => await CreateSut().GetAsync(AgentType.AuthorAgent, null, CancellationToken.None));

        exception.Should().BeNull();
        _inner.Verify(p => p.GetAsync(AgentType.AuthorAgent, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_resolver_failure_falls_back_to_the_default_instead_of_throwing()
    {
        _resolver
            .Setup(r => r.ResolveChatAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("resolver exploded"));

        CostRoutingRunState state = State(escalated: true, cheap: null, expensive: _expensiveProviderId);
        using IDisposable _ = _ambient.Begin(state);

        Exception exception = await Record.ExceptionAsync(
            async () => await CreateSut().GetAsync(AgentType.AuthorAgent, null, CancellationToken.None));

        exception.Should().BeNull();
        _inner.Verify(p => p.GetAsync(AgentType.AuthorAgent, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Recording: the outcome the observation is later updated with ---

    [Fact]
    public async Task A_successful_answer_with_a_json_object_is_recorded_on_the_run_state()
    {
        CostRoutingRunState state = State(escalated: false, cheap: null, expensive: _expensiveProviderId);
        using IDisposable _ = _ambient.Begin(state);

        var inner = new Mock<IChatClient>();
        inner
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"look\":\"Warm\"}")));
        _inner
            .Setup(p => p.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<IChatClient>(inner.Object));

        IChatClient client = await CreateSut().GetAsync(AgentType.AuthorAgent, null, CancellationToken.None);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        state.ChatCalls.Should().Be(1);
        state.LastCallSucceeded.Should().BeTrue();
        state.LastResponseHadJsonObject.Should().BeTrue();
    }

    [Fact]
    public async Task A_failing_model_call_is_recorded_and_the_exception_is_still_thrown()
    {
        CostRoutingRunState state = State(escalated: true, cheap: null, expensive: _expensiveProviderId);
        using IDisposable _ = _ambient.Begin(state);

        var inner = new Mock<IChatClient>();
        inner
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("503 from the provider"));
        _inner
            .Setup(p => p.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<IChatClient>(inner.Object));

        IChatClient client = await CreateSut().GetAsync(AgentType.AuthorAgent, null, CancellationToken.None);

        HttpRequestException thrown = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        thrown.Message.Should().Contain("503");
        state.ChatCalls.Should().Be(1);
        state.LastCallSucceeded.Should().BeFalse();
        state.Error.Should().Contain("503");
    }

    [Fact]
    public async Task A_prose_only_answer_with_no_json_object_is_recorded_as_such()
    {
        CostRoutingRunState state = State(escalated: false, cheap: null, expensive: _expensiveProviderId);
        using IDisposable _ = _ambient.Begin(state);

        var inner = new Mock<IChatClient>();
        inner
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "I think warm would look nice.")));
        _inner
            .Setup(p => p.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<IChatClient>(inner.Object));

        IChatClient client = await CreateSut().GetAsync(AgentType.AuthorAgent, null, CancellationToken.None);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        state.LastCallSucceeded.Should().BeTrue();
        state.LastResponseHadJsonObject.Should().BeFalse(
            "the exact predicate AgentStepExecutor fails a schema-declaring step on");
    }

    // --- The additive resolver method the decorator depends on ---

    [Fact]
    public async Task ResolveChatAsync_returns_the_named_Chat_provider()
    {
        ModelCapabilityView provider = MakeProvider(InferenceProviderCapability.Chat, "cheap-chat");

        InferenceProviderResolver resolver = CreateResolver(new[] { provider });

        ResolvedInferenceProvider resolved = await resolver.ResolveChatAsync(provider.Id, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved.ProviderId.Should().Be(provider.Id);
        resolved.Name.Should().Be("cheap-chat");
    }

    [Fact]
    public async Task ResolveChatAsync_returns_null_for_an_unknown_or_disabled_id()
    {
        InferenceProviderResolver resolver = CreateResolver(Array.Empty<ModelCapabilityView>());

        ResolvedInferenceProvider resolved = await resolver.ResolveChatAsync(Guid.NewGuid(), CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task ResolveChatAsync_returns_null_for_a_non_Chat_capability()
    {
        // Same defense in depth as ResolveVisionAsync's by-id branch: a Transcription/Vision/
        // Decision/VideoGeneration row id must never reach a chat call.
        ModelCapabilityView transcription = MakeProvider(InferenceProviderCapability.Transcription, "asr");
        ModelCapabilityView vision = MakeProvider(InferenceProviderCapability.Vision, "vision");
        ModelCapabilityView defaultChat = MakeProvider(InferenceProviderCapability.Chat, "default-chat", isDefault: true);

        InferenceProviderResolver resolver = CreateResolver(new[] { transcription, vision, defaultChat });

        (await resolver.ResolveChatAsync(transcription.Id, CancellationToken.None)).Should().BeNull();
        (await resolver.ResolveChatAsync(vision.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ResolveChatAsync_never_falls_back_to_the_default_Chat_provider()
    {
        // Deliberate asymmetry with the other resolvers: cost routing has already chosen a tier, so
        // a pin that does not resolve must be reported as unresolvable rather than silently swapped
        // for a provider the routing decision never evaluated — otherwise a misconfigured tier is
        // indistinguishable from a working one.
        ModelCapabilityView defaultChat = MakeProvider(InferenceProviderCapability.Chat, "default-chat", isDefault: true);

        InferenceProviderResolver resolver = CreateResolver(new[] { defaultChat });

        ResolvedInferenceProvider resolved = await resolver.ResolveChatAsync(Guid.NewGuid(), CancellationToken.None);

        resolved.Should().BeNull();
    }

    private static ModelCapabilityView MakeProvider(
        InferenceProviderCapability capability, string name, bool isDefault = false) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = InferenceProviderKind.OpenAICompatible,
            Endpoint = "https://provider.example",
            ModelName = "model-x",
            IsDefault = isDefault,
            IsEnabled = true,
            Capability = capability,
        };

    private static InferenceProviderResolver CreateResolver(IReadOnlyList<ModelCapabilityView> providers)
    {
        ServiceCollection services = new();
        services.AddSingleton<IInferenceProviderStore>(new FakeInferenceProviderStore(providers));
        ServiceProvider serviceProvider = services.BuildServiceProvider();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>())
            .Build();

        return new InferenceProviderResolver(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            new PassthroughSecretProtector(),
            NullLogger<InferenceProviderResolver>.Instance);
    }

    private sealed class FakeInferenceProviderStore : IInferenceProviderStore
    {
        private readonly IReadOnlyList<ModelCapabilityView> _providers;

        public FakeInferenceProviderStore(IReadOnlyList<ModelCapabilityView> providers) => _providers = providers;

        public Task<IReadOnlyList<ModelCapabilityView>> LoadEnabledAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult(_providers);

        public Task<IReadOnlyDictionary<Guid, Guid?>> LoadAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid?>>(new Dictionary<Guid, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadOrgAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadBuiltInAgentOverridesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());
    }

    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public bool TryUnprotect(string ciphertext, out string plaintext)
        {
            plaintext = ciphertext ?? string.Empty;
            return ciphertext != null;
        }
    }
}
