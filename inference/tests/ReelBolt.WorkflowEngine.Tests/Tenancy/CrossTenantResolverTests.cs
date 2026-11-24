using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Tenancy;

/// <summary>
/// WP A14, the resolution seam: <b>the real store driving the real resolver, with two organizations in
/// one database.</b>
///
/// <para>
/// <c>InferenceProviderResolverTests</c> already pins the ladder's RULES against a hand-built snapshot
/// (<c>Org_cannot_resolve_another_orgs_provider_by_explicit_id</c>,
/// <c>Org_default_beats_platform_default_...</c>, <c>Concurrent_org_scopes_never_leak_...</c>). What it
/// cannot see is the half that decides what a snapshot even CONTAINS: the store. This file therefore
/// wires <see cref="InferenceApiProviderStore"/> — the real one, reading a real
/// <see cref="InferenceApiDbContext"/> — into a real <see cref="InferenceProviderResolver"/> and asks
/// the four rungs (explicit id, agent override, organization default, platform default) with the rows
/// actually in the database.
/// </para>
///
/// <para>
/// The agent-override rung is the one nobody asserts end to end: an
/// <see cref="AgentDefinition.InferenceProviderId"/> pointing at another organization's provider. The
/// store DOES load that override (the agent belongs to the caller's organization, so it passes the
/// store's own filter), and the resolver must still refuse to hand the foreign row over — because the
/// provider is absent from the snapshot, not because anything validated the pointer.
/// </para>
/// </summary>
public sealed class CrossTenantResolverTests : IDisposable
{
    private readonly InferenceApiDbContext _db;
    private readonly Guid _orgA = Guid.NewGuid();
    private readonly Guid _orgB = Guid.NewGuid();
    private readonly Guid _orgC = Guid.NewGuid();

    public CrossTenantResolverTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"cross-tenant-resolver-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------

    private sealed class MutableTenantAccessor : InferenceTenantAccessorBase
    {
        public Guid? Org { get; set; }

        protected override Guid? AmbientOrganizationId => Org;
    }

    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = ciphertext ?? string.Empty;
            return ciphertext is not null;
        }
    }

    /// <summary>
    /// The real store over the real context, plus a real resolver. A fresh resolver per call keeps the
    /// 60-second snapshot cache from hiding a seeding mistake — a test that had to remember
    /// <c>Invalidate</c> would be testing the cache, not the boundary.
    /// </summary>
    private (InferenceProviderResolver Resolver, MutableTenantAccessor Tenant) CreateResolver()
    {
        ServiceCollection services = new();
        services.AddSingleton<IInferenceProviderStore>(new InferenceApiProviderStore(_db));
        ServiceProvider provider = services.BuildServiceProvider();

        var tenant = new MutableTenantAccessor();
        var resolver = new InferenceProviderResolver(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            new PassthroughSecretProtector(),
            NullLogger<InferenceProviderResolver>.Instance,
            tenant);

        return (resolver, tenant);
    }

    private ModelCapabilityView Provider(string name, Guid? organizationId, bool isDefault) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Kind = InferenceProviderKind.OpenAICompatible,
        Capability = InferenceProviderCapability.Chat,
        OrganizationId = organizationId,
        Endpoint = "https://provider.example/v1",
        ModelName = "m",
        IsEnabled = true,
        IsDefault = isDefault,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private AgentDefinition CustomAgent(Guid organizationId, Guid? providerOverride) => new()
    {
        Id = Guid.NewGuid(),
        Name = "custom",
        Description = "d",
        SystemPrompt = "p",
        AgentType = AgentType.Custom,
        IsBuiltIn = false,
        OwnerId = Guid.NewGuid(),
        OrganizationId = organizationId,
        InferenceProviderId = providerOverride,
        CreatedAt = DateTime.UtcNow
    };

    private sealed record Seeded(
        ModelCapabilityView PlatformDefault,
        ModelCapabilityView OrgADefault,
        ModelCapabilityView OrgBDefault,
        ModelCapabilityView OrgBPick,
        ModelCapabilityView PlatformPick);

    private Seeded Seed()
    {
        Seeded seeded = new(
            PlatformDefault: Provider("platform-default", organizationId: null, isDefault: true),
            OrgADefault: Provider("org-a-default", _orgA, isDefault: true),
            OrgBDefault: Provider("org-b-default", _orgB, isDefault: true),
            OrgBPick: Provider("org-b-pick", _orgB, isDefault: false),
            PlatformPick: Provider("platform-pick", organizationId: null, isDefault: false));

        _db.AddFlat(
            seeded.PlatformDefault, seeded.OrgADefault, seeded.OrgBDefault, seeded.OrgBPick, seeded.PlatformPick);
        _db.SaveChanges();
        return seeded;
    }

    // ---------------------------------------------------------------------------------------------------
    // The four rungs
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_explicit_id_resolves_only_inside_the_calling_organization()
    {
        Seeded seeded = Seed();
        (InferenceProviderResolver resolver, MutableTenantAccessor tenant) = CreateResolver();

        tenant.Org = _orgA;
        (await resolver.ResolveChatAsync(seeded.OrgBPick.Id, CancellationToken.None)).Should().BeNull(
            "a pin the caller's organization cannot see is treated as missing, never served");

        tenant.Org = _orgB;
        (await resolver.ResolveChatAsync(seeded.OrgBPick.Id, CancellationToken.None))!.ProviderId
            .Should().Be(seeded.OrgBPick.Id, "the same id is the owning organization's own row");
    }

    /// <summary>
    /// The rung nobody else drives: <c>AgentDefinition.InferenceProviderId</c> pointing across the
    /// boundary. The store loads the override (the agent is the caller's), the snapshot does not
    /// contain the provider, and the ladder must continue to the organization's own default rather
    /// than handing over a row the caller never had access to.
    /// </summary>
    [Fact]
    public async Task An_agent_override_naming_another_organizations_provider_falls_through_to_the_organizations_default()
    {
        Seeded seeded = Seed();
        AgentDefinition agent = CustomAgent(_orgA, providerOverride: seeded.OrgBPick.Id);
        _db.AgentDefinitions.Add(agent);
        await _db.SaveChangesAsync();

        (InferenceProviderResolver resolver, MutableTenantAccessor tenant) = CreateResolver();
        tenant.Org = _orgA;

        ResolvedInferenceProvider resolved = await resolver.ResolveAsync(AgentType.Custom, agent.Id, CancellationToken.None);

        resolved.ProviderId.Should().Be(seeded.OrgADefault.Id,
            "the override names a provider this organization cannot see, so resolution continues to "
            + "the rung below it — the agent being writable did not make the provider reachable");
        resolved.ProviderId.Should().NotBe(seeded.OrgBPick.Id);
        resolved.OrganizationId.Should().Be(_orgA);
    }

    [Fact]
    public async Task An_agent_override_inside_the_same_organization_is_honoured()
    {
        Seeded seeded = Seed();
        AgentDefinition agent = CustomAgent(_orgA, providerOverride: seeded.PlatformPick.Id);
        _db.AgentDefinitions.Add(agent);
        await _db.SaveChangesAsync();

        (InferenceProviderResolver resolver, MutableTenantAccessor tenant) = CreateResolver();
        tenant.Org = _orgA;

        ResolvedInferenceProvider resolved = await resolver.ResolveAsync(AgentType.Custom, agent.Id, CancellationToken.None);

        // The positive control for the test above: the rung works, so the miss there was the
        // boundary and not a broken override lookup.
        resolved.ProviderId.Should().Be(seeded.PlatformPick.Id);
    }

    [Fact]
    public async Task Each_organizations_own_default_serves_it_and_the_platform_default_serves_everyone_else()
    {
        Seeded seeded = Seed();
        (InferenceProviderResolver resolver, MutableTenantAccessor tenant) = CreateResolver();

        tenant.Org = _orgA;
        (await resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, null, CancellationToken.None)).ProviderId
            .Should().Be(seeded.OrgADefault.Id);

        tenant.Org = _orgB;
        (await resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, null, CancellationToken.None)).ProviderId
            .Should().Be(seeded.OrgBDefault.Id);

        tenant.Org = _orgC;   // an organization with no provider rows at all
        ResolvedInferenceProvider fallback = await resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, null, CancellationToken.None);
        fallback.ProviderId.Should().Be(seeded.PlatformDefault.Id);
        fallback.OrganizationId.Should().BeNull();
        fallback.IsPlatformManaged.Should().BeTrue();
    }

    [Fact]
    public async Task With_no_tenant_scope_only_the_platform_rows_are_resolvable()
    {
        Seeded seeded = Seed();
        (InferenceProviderResolver resolver, MutableTenantAccessor tenant) = CreateResolver();

        tenant.Org = null;

        (await resolver.ResolveAsync(AgentType.CodeStructureAnalyzer, null, CancellationToken.None)).ProviderId
            .Should().Be(seeded.PlatformDefault.Id);

        (await resolver.ResolveChatAsync(seeded.OrgADefault.Id, CancellationToken.None)).Should().BeNull();
        (await resolver.ResolveChatAsync(seeded.OrgBDefault.Id, CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// Every capability, not just chat: a capability the caller's organization has no row for must
    /// come back as "nothing", never as the other organization's row of that capability.
    /// </summary>
    [Fact]
    public async Task A_foreign_row_is_unreachable_in_every_capability()
    {
        ModelCapabilityView foreignTranscription = Provider("t-b", _orgB, isDefault: true);
        foreignTranscription.Capability = InferenceProviderCapability.Transcription;
        ModelCapabilityView foreignSpeech = Provider("s-b", _orgB, isDefault: true);
        foreignSpeech.Capability = InferenceProviderCapability.SpeechSynthesis;
        ModelCapabilityView foreignEmbedding = Provider("e-b", _orgB, isDefault: true);
        foreignEmbedding.Capability = InferenceProviderCapability.Embedding;
        _db.AddFlat(foreignTranscription, foreignSpeech, foreignEmbedding);
        await _db.SaveChangesAsync();

        (InferenceProviderResolver resolver, MutableTenantAccessor tenant) = CreateResolver();
        tenant.Org = _orgA;

        (await resolver.ResolveTranscriptionAsync(foreignTranscription.Id, CancellationToken.None)).Should().BeNull();
        (await resolver.ResolveSpeechSynthesisAsync(foreignSpeech.Id, CancellationToken.None)).Should().BeNull();
        (await resolver.ResolveEmbeddingAsync(foreignEmbedding.Id, CancellationToken.None)).Should().BeNull();

        tenant.Org = _orgB;
        (await resolver.ResolveEmbeddingAsync(foreignEmbedding.Id, CancellationToken.None))!.ProviderId
            .Should().Be(foreignEmbedding.Id);
    }

    // ---------------------------------------------------------------------------------------------------
    // The store: what a snapshot is even allowed to contain
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The store is the half the resolver's own tests fake out, and it is the half that decides the
    /// boundary. It deliberately reads with <c>IgnoreQueryFilters</c> (the resolver is a singleton and
    /// refreshes under whatever flow missed the cache), so its scope is the explicit
    /// <c>organization_id</c> predicate below — and that predicate is what this test pins.
    /// </summary>
    [Fact]
    public async Task The_store_returns_platform_rows_plus_only_the_requested_organizations_own()
    {
        Seeded seeded = Seed();
        AgentDefinition agentA = CustomAgent(_orgA, providerOverride: null);
        AgentDefinition agentB = CustomAgent(_orgB, providerOverride: null);
        AgentDefinition builtIn = new()
        {
            Id = Guid.NewGuid(),
            Name = "builtin",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            OwnerId = null,
            OrganizationId = null,
            InferenceProviderId = seeded.PlatformPick.Id,
            CreatedAt = DateTime.UtcNow
        };
        _db.AgentDefinitions.AddRange(agentA, agentB, builtIn);
        _db.OrganizationAgentProviderOverrides.AddRange(
            new OrganizationAgentProviderOverride
            {
                OrganizationId = _orgA,
                AgentType = AgentType.Colorist,
                InferenceProviderId = seeded.OrgADefault.Id
            },
            new OrganizationAgentProviderOverride
            {
                OrganizationId = _orgB,
                AgentType = AgentType.Colorist,
                InferenceProviderId = seeded.OrgBDefault.Id
            });
        await _db.SaveChangesAsync();

        var store = new InferenceApiProviderStore(_db);

        // Enabled providers, per organization.
        (await store.LoadEnabledAsync(_orgA, CancellationToken.None)).Select(p => p.Name)
            .Should().BeEquivalentTo("platform-default", "platform-pick", "org-a-default");
        (await store.LoadEnabledAsync(_orgB, CancellationToken.None)).Select(p => p.Name)
            .Should().BeEquivalentTo("platform-default", "platform-pick", "org-b-default", "org-b-pick");
        (await store.LoadEnabledAsync(null, CancellationToken.None)).Select(p => p.Name)
            .Should().BeEquivalentTo("platform-default", "platform-pick");

        // Custom-agent overrides, per organization.
        (await store.LoadAgentOverridesAsync(_orgA, CancellationToken.None)).Keys.Should().Equal(agentA.Id);
        (await store.LoadAgentOverridesAsync(_orgB, CancellationToken.None)).Keys.Should().Equal(agentB.Id);
        (await store.LoadAgentOverridesAsync(null, CancellationToken.None)).Should().BeEmpty();

        // Organization-level built-in overrides, per organization.
        (await store.LoadOrgAgentOverridesAsync(_orgA, CancellationToken.None))
            .Should().ContainKey(AgentType.Colorist).WhoseValue.Should().Be(seeded.OrgADefault.Id);
        (await store.LoadOrgAgentOverridesAsync(_orgA, CancellationToken.None))
            .Should().NotContainValue(seeded.OrgBDefault.Id);

        // Built-in definitions only: a custom agent never contributes here, whatever organization it is in.
        (await store.LoadBuiltInAgentOverridesAsync(CancellationToken.None))
            .Should().ContainKey(AgentType.AuthorAgent).WhoseValue.Should().Be(seeded.PlatformPick.Id);
    }
}
