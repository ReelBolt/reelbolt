#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// agent-model-choice: <c>GET /api/v1/org/agent-models</c>, one row per built-in agent for the active workspace:
/// the platform recommendation (and why), the workspace's own choice, what the agent really resolves to, and which of the
/// three it is. Owner/Admin only, and it can only ever name models the caller's workspace can use.
/// </summary>
public sealed class OrgAgentModelsControllerTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private sealed class FakeUser(Guid? orgId, OrganizationRole? role, bool platformAdmin = false) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "t@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => orgId;
        public OrganizationRole? OrgRole => role;
        public bool IsPlatformAdmin => platformAdmin;
    }

    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options);

    private readonly Mock<IInferenceProviderResolver> _resolver = new();
    private readonly Mock<IOrganizationEntitlements> _entitlements = new();
    private readonly Dictionary<Guid, ModelCapabilityView> _rows = [];

    public OrgAgentModelsControllerTests()
    {
        _entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { ByoAllowed = true }));
    }

    private OrgAgentModelsController Controller(Guid? org = null, OrganizationRole? role = OrganizationRole.Owner, bool platformAdmin = false) =>
        new(_db, new FakeUser(org ?? OrgA, role, platformAdmin), _resolver.Object, _entitlements.Object);

    private ModelCapabilityView AddModel(string name, Guid? org = null, InferenceProviderKind kind = InferenceProviderKind.DeepSeek,
        string modelCode = "deepseek-flash", bool enabled = true, bool priced = true,
        InferenceProviderCapability capability = InferenceProviderCapability.Chat)
    {
        ModelCapabilityView row = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            ModelId = Guid.NewGuid(),
            OrganizationId = org,
            Name = name,
            Kind = kind,
            Capability = capability,
            Endpoint = "https://198.51.100.7/v1",
            ModelName = modelCode,
            ApiKeyEncrypted = "enc:k",
            IsEnabled = enabled,
        };
        _db.AddFlat(row);
        if (priced && org is null)
        {
            foreach (UsageKind unit in ProviderCapabilityUsage.ByCapability[capability])
            {
                _db.ModelCapabilityPrices.Add(new ModelCapabilityPrice
                {
                    Id = Guid.NewGuid(),
                    ModelCapabilityId = row.Id,
                    Unit = unit,
                    CostUsdPerUnit = 0.000001m,
                    EffectiveFrom = DateTime.UtcNow.AddDays(-1),
                });
            }
        }

        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        _rows[row.Id] = row;
        return row;
    }

    private void AddAgent(AgentType type, Guid? recommended = null)
    {
        _db.AgentDefinitions.Add(new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = type.ToString(),
            AgentType = type,
            IsBuiltIn = true,
            InferenceProviderId = recommended,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private void SetOverride(Guid org, AgentType type, Guid providerId)
    {
        _db.OrganizationAgentProviderOverrides.Add(new OrganizationAgentProviderOverride
        {
            OrganizationId = org,
            AgentType = type,
            InferenceProviderId = providerId,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    /// <summary>What the resolver answers for the caller's workspace: the given model for every agent.</summary>
    private void ResolvesTo(ModelCapabilityView? row, bool failClosed = true)
    {
        _resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(failClosed);
        if (row is null)
        {
            _resolver.Setup(r => r.ResolveAsync(It.IsAny<AgentType>(), null, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ModelUnpricedException(InferenceProviderCapability.Chat, "gone"));
            return;
        }

        _resolver.Setup(r => r.ResolveAsync(It.IsAny<AgentType>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(row.Id, row.Name, row.Kind, row.Endpoint, row.ModelName, "k", 60));
    }

    private async Task<List<OrgAgentModelResponse>> GetAsync(Guid? org = null)
    {
        ActionResult<List<OrgAgentModelResponse>> result = await Controller(org).List(CancellationToken.None);
        return result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<List<OrgAgentModelResponse>>().Subject;
    }

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(OrganizationRole.Billing)]
    public async Task A_member_or_billing_user_gets_403_and_nothing_is_resolved(OrganizationRole role)
    {
        AddAgent(AgentType.AuthorAgent);

        (await Controller(role: role).List(default)).Result.Should().BeOfType<ForbidResult>();

        _resolver.Verify(r => r.ResolveAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_platform_admin_who_is_only_a_member_of_the_workspace_gets_403()
    {
        AddModel("DeepSeek V4.1 Flash");

        (await Controller(role: OrganizationRole.Member, platformAdmin: true).List(default)).Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Without_an_active_workspace_it_is_404()
    {
        OrgAgentModelsController c = new(_db, new FakeUser(null, null), _resolver.Object, _entitlements.Object);

        (await c.List(default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Lists_every_built_in_agent_except_custom_and_no_custom_agents()
    {
        AddAgent(AgentType.ReviewAgent);
        AddAgent(AgentType.AuthorAgent);
        AddAgent(AgentType.Custom);
        _db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "mine", AgentType = AgentType.AuthorAgent, IsBuiltIn = false, OrganizationId = OrgA });
        _db.SaveChanges();
        ResolvesTo(AddModel("default"));

        List<OrgAgentModelResponse> rows = await GetAsync();

        rows.Select(r => r.AgentType).Should().Equal("AuthorAgent", "ReviewAgent");
    }

    [Fact]
    public async Task Recommended_shows_the_model_and_the_why_while_it_is_the_seeded_model()
    {
        ModelCapabilityView flash = AddModel("DeepSeek Flash");
        AddAgent(AgentType.AuthorAgent, flash.Id);
        ResolvesTo(flash);

        OrgAgentModelResponse row = (await GetAsync()).Single();

        row.RecommendedModelId.Should().Be(flash.Id);
        row.RecommendedModelName.Should().Be("DeepSeek Flash");
        row.Why.Should().Be(AgentRecommendationSeedWhy(AgentType.AuthorAgent));
        row.ChoiceModelId.Should().BeNull();
        row.EffectiveModelId.Should().Be(flash.Id);
        row.EffectiveModelName.Should().Be("DeepSeek Flash");
        row.Source.Should().Be("recommended");
    }

    [Fact]
    public async Task Why_is_dropped_when_an_admin_has_repointed_the_recommended_model_away_from_the_seed()
    {
        ModelCapabilityView other = AddModel("Something else", kind: InferenceProviderKind.OpenAICompatible, modelCode: "other");
        AddAgent(AgentType.AuthorAgent, other.Id);
        ResolvesTo(other);

        OrgAgentModelResponse row = (await GetAsync()).Single();

        row.RecommendedModelId.Should().Be(other.Id);
        row.Why.Should().BeNull();
    }

    [Fact]
    public async Task A_workspace_choice_that_the_agent_runs_on_is_source_workspace()
    {
        ModelCapabilityView flash = AddModel("DeepSeek Flash");
        ModelCapabilityView mine = AddModel("Pick", kind: InferenceProviderKind.Anthropic, modelCode: "claude-x");
        AddAgent(AgentType.AuthorAgent, flash.Id);
        SetOverride(OrgA, AgentType.AuthorAgent, mine.Id);
        ResolvesTo(mine);

        OrgAgentModelResponse row = (await GetAsync()).Single();

        row.ChoiceModelId.Should().Be(mine.Id);
        row.EffectiveModelId.Should().Be(mine.Id);
        row.RecommendedModelId.Should().Be(flash.Id);
        row.Source.Should().Be("workspace");
    }

    [Fact]
    public async Task No_recommendation_and_no_choice_is_source_default()
    {
        ModelCapabilityView fallback = AddModel("Default model", modelCode: "dflt");
        AddAgent(AgentType.AuthorAgent);
        ResolvesTo(fallback);

        OrgAgentModelResponse row = (await GetAsync()).Single();

        row.RecommendedModelId.Should().BeNull();
        row.Why.Should().BeNull();
        row.ChoiceModelId.Should().BeNull();
        row.EffectiveModelName.Should().Be("Default model");
        row.Source.Should().Be("default");
    }

    [Fact]
    public async Task A_choice_whose_model_is_no_longer_usable_is_not_reported_and_the_agent_falls_back()
    {
        ModelCapabilityView flash = AddModel("DeepSeek Flash");
        ModelCapabilityView gone = AddModel("Disabled pick", kind: InferenceProviderKind.Anthropic, modelCode: "c", enabled: false);
        AddAgent(AgentType.AuthorAgent, flash.Id);
        SetOverride(OrgA, AgentType.AuthorAgent, gone.Id);
        ResolvesTo(flash);

        OrgAgentModelResponse row = (await GetAsync()).Single();

        row.ChoiceModelId.Should().BeNull();
        row.Source.Should().Be("recommended");
    }

    [Fact]
    public async Task An_unpriced_recommended_model_is_not_offered_and_an_unresolvable_agent_has_no_effective_model()
    {
        ModelCapabilityView unpriced = AddModel("Unpriced", priced: false);
        AddAgent(AgentType.AuthorAgent, unpriced.Id);
        ResolvesTo(null);

        OrgAgentModelResponse row = (await GetAsync()).Single();

        row.RecommendedModelId.Should().BeNull();
        row.RecommendedModelName.Should().BeNull();
        row.Why.Should().BeNull();
        row.EffectiveModelId.Should().BeNull();
        row.EffectiveModelName.Should().BeNull();
        row.Source.Should().Be("default");
    }

    [Fact]
    public async Task A_byo_model_is_never_the_recommendation_even_if_an_admin_pointed_the_built_in_at_it()
    {
        ModelCapabilityView byo = AddModel("Their BYO", org: OrgA);
        ModelCapabilityView fallback = AddModel("Default model", modelCode: "dflt");
        AddAgent(AgentType.AuthorAgent, byo.Id);
        ResolvesTo(fallback);

        OrgAgentModelResponse row = (await GetAsync()).Single();

        row.RecommendedModelId.Should().BeNull();
        row.Source.Should().Be("default");
    }

    [Fact]
    public async Task Another_orgs_ids_and_names_never_appear()
    {
        ModelCapabilityView foreign = AddModel("Foreign secret model", org: OrgB);
        ModelCapabilityView fallback = AddModel("Default model", modelCode: "dflt");
        AddAgent(AgentType.AuthorAgent, foreign.Id);
        AddAgent(AgentType.ReviewAgent);
        SetOverride(OrgB, AgentType.ReviewAgent, foreign.Id);
        SetOverride(OrgA, AgentType.AuthorAgent, foreign.Id);
        ResolvesTo(fallback);

        List<OrgAgentModelResponse> rows = await GetAsync();

        string json = JsonSerializer.Serialize(rows);
        json.Should().NotContain(foreign.Id.ToString());
        json.Should().NotContain("Foreign secret model");
        rows.Should().OnlyContain(r => r.ChoiceModelId == null);
    }

    [Fact]
    public async Task The_workspaces_own_byo_choice_is_reported_but_a_lapsed_entitlement_hides_it()
    {
        ModelCapabilityView byo = AddModel("Their BYO", org: OrgA, kind: InferenceProviderKind.Anthropic, modelCode: "c");
        ModelCapabilityView fallback = AddModel("Default model", modelCode: "dflt");
        AddAgent(AgentType.AuthorAgent);
        SetOverride(OrgA, AgentType.AuthorAgent, byo.Id);
        ResolvesTo(byo);

        OrgAgentModelResponse row = (await GetAsync()).Single();
        row.ChoiceModelId.Should().Be(byo.Id);
        row.Source.Should().Be("workspace");

        _entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { ByoAllowed = false }));
        ResolvesTo(fallback);

        row = (await GetAsync()).Single();
        row.ChoiceModelId.Should().BeNull();
        row.Source.Should().Be("default");
    }

    private static string AgentRecommendationSeedWhy(AgentType type) =>
        ReelBolt.Inference.Api.Billing.AgentRecommendationSeed.LoadDefault().For(type)!.Why;
}
