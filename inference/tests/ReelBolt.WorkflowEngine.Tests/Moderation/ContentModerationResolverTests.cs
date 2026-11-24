using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// Pins <see cref="IInferenceProviderResolver.ResolveContentModerationAsync"/> precedence:
/// explicit (only when it carries the capability) → the ContentModeration default → null, with no
/// legacy configuration fallback.
/// </summary>
public sealed class ContentModerationResolverTests
{
    [Fact]
    public async Task The_explicit_ContentModeration_row_wins_over_the_default()
    {
        ModelCapabilityView fallback = Provider(isDefault: true, "moderation-default");
        ModelCapabilityView explicitRow = Provider(isDefault: false, "moderation-explicit");

        ResolvedContentModerationProvider? resolved = await Resolver([fallback, explicitRow])
            .ResolveContentModerationAsync(explicitRow.Id, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(explicitRow.Id);
        resolved.Name.Should().Be("moderation-explicit");
        resolved.Kind.Should().Be(InferenceProviderKind.OpenAICompatible);
    }

    [Fact]
    public async Task Without_an_explicit_id_the_ContentModeration_default_is_used()
    {
        ModelCapabilityView fallback = Provider(isDefault: true, "moderation-default");

        ResolvedContentModerationProvider? resolved = await Resolver([fallback])
            .ResolveContentModerationAsync(null, CancellationToken.None);

        resolved!.ProviderId.Should().Be(fallback.Id);
        resolved.ModelName.Should().Be("omni-moderation-latest");
    }

    [Fact]
    public async Task A_chat_row_id_passed_as_explicit_falls_through_to_the_moderation_default()
    {
        ModelCapabilityView chat = Provider(isDefault: true, "chat", InferenceProviderCapability.Chat);
        ModelCapabilityView fallback = Provider(isDefault: true, "moderation-default");

        ResolvedContentModerationProvider? resolved = await Resolver([chat, fallback])
            .ResolveContentModerationAsync(chat.Id, CancellationToken.None);

        resolved!.ProviderId.Should().Be(fallback.Id,
            "a Chat id must never be handed to the moderation client");
    }

    [Fact]
    public async Task Returns_null_when_nothing_serves_ContentModeration_even_with_a_chat_default()
    {
        ModelCapabilityView chat = Provider(isDefault: true, "chat", InferenceProviderCapability.Chat);

        ResolvedContentModerationProvider? resolved = await Resolver([chat])
            .ResolveContentModerationAsync(null, CancellationToken.None);

        resolved.Should().BeNull("there is no legacy configuration fallback for moderation");
    }

    [Fact]
    public async Task An_unpriced_platform_default_fails_closed_when_billing_is_enabled()
    {
        ModelCapabilityView unpriced = Provider(isDefault: true, "unpriced-moderation");

        Func<Task> act = () => Resolver([unpriced], unpricedIds: [unpriced.Id],
                config: new Dictionary<string, string?> { ["Billing:Enabled"] = "true" })
            .ResolveContentModerationAsync(null, CancellationToken.None);

        await act.Should().ThrowAsync<ModelUnpricedException>();
    }

    [Fact]
    public async Task The_resolved_provider_redacts_its_api_key_and_hashes_its_cache_key()
    {
        ModelCapabilityView row = Provider(isDefault: true, "moderation-default", apiKeyEncrypted: "sk-secret-value");

        ResolvedContentModerationProvider? resolved = await Resolver([row])
            .ResolveContentModerationAsync(null, CancellationToken.None);

        resolved!.ApiKey.Should().Be("sk-secret-value");
        resolved.ToString().Should().NotContain("sk-secret-value");
        resolved.CacheKey.Should().HaveLength(64).And.NotContain("sk-secret-value");
    }

    private static readonly Guid Org = Guid.NewGuid();

    [Fact]
    public async Task An_organizations_own_default_never_answers_moderation_even_when_it_has_no_platform_row()
    {
        ModelCapabilityView own = Provider(isDefault: true, "own-moderation", organizationId: Org);

        ResolvedContentModerationProvider? resolved = await Resolver([own], organization: Org)
            .ResolveContentModerationAsync(null, CancellationToken.None);

        resolved.Should().BeNull("no platform row: unavailable, with the surface's existing stance");
    }

    [Fact]
    public async Task The_platform_default_answers_moderation_when_the_organization_has_its_own_default_too()
    {
        ModelCapabilityView own = Provider(isDefault: true, "own-moderation", organizationId: Org);
        ModelCapabilityView platform = Provider(isDefault: true, "platform-moderation");

        ResolvedContentModerationProvider? resolved = await Resolver([own, platform], organization: Org)
            .ResolveContentModerationAsync(own.Id, CancellationToken.None);

        resolved!.ProviderId.Should().Be(platform.Id, "an organization row named explicitly is refused too");
    }

    [Fact]
    public async Task An_organizations_own_transcription_default_never_transcribes_for_moderation()
    {
        ModelCapabilityView own = Provider(isDefault: true, "own-asr", InferenceProviderCapability.Transcription, organizationId: Org);
        InferenceProviderResolver onlyOwn = Resolver([own], organization: Org);
        (await onlyOwn.ResolvePlatformTranscriptionAsync(CancellationToken.None)).Should().BeNull();

        ModelCapabilityView platform = Provider(isDefault: true, "platform-asr", InferenceProviderCapability.Transcription);
        InferenceProviderResolver both = Resolver([own, platform], organization: Org);
        (await both.ResolvePlatformTranscriptionAsync(CancellationToken.None))!.ProviderId.Should().Be(platform.Id);

        // Run-step transcription is not touched: the organization's own default still wins there.
        (await both.ResolveTranscriptionAsync(null, CancellationToken.None))!.ProviderId.Should().Be(own.Id);
    }

    private sealed class FixedTenant(Guid? organization) : IInferenceTenantAccessor
    {
        public Guid? OrganizationId => organization;

        public IDisposable BeginPlatformScope() => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static ModelCapabilityView Provider(
        bool isDefault,
        string name,
        InferenceProviderCapability capability = InferenceProviderCapability.ContentModeration,
        string? apiKeyEncrypted = null,
        Guid? organizationId = null) => new()
        {
            OrganizationId = organizationId,
            Id = Guid.NewGuid(),
            Name = name,
            Kind = InferenceProviderKind.OpenAICompatible,
            Endpoint = "https://api.openai.com/v1",
            ModelName = "omni-moderation-latest",
            ApiKeyEncrypted = apiKeyEncrypted,
            IsDefault = isDefault,
            IsEnabled = true,
            Capability = capability
        };

    private static InferenceProviderResolver Resolver(
        IReadOnlyList<ModelCapabilityView> providers,
        IReadOnlyCollection<Guid>? unpricedIds = null,
        IDictionary<string, string?>? config = null,
        Guid? organization = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<IInferenceProviderStore>(new FakeStore(providers, unpricedIds));
        ServiceProvider serviceProvider = services.BuildServiceProvider();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
            .Build();

        return new InferenceProviderResolver(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            new PassthroughSecretProtector(),
            NullLogger<InferenceProviderResolver>.Instance,
            organization is null ? null : new FixedTenant(organization));
    }

    private sealed class FakeStore(
        IReadOnlyList<ModelCapabilityView> providers,
        IReadOnlyCollection<Guid>? unpricedIds) : IInferenceProviderStore
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
            Task.FromResult<IReadOnlySet<Guid>>((unpricedIds ?? []).ToHashSet());
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
