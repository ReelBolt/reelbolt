#nullable enable
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
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// agent-model-choice (#381) x the profile-build cap (T-032). A workspace can choose the model each built-in agent runs
/// on, and the REAL <see cref="InferenceProviderResolver"/> applies that choice for the ambient organization. The cap's
/// reference template must be priced with exactly the resolver inputs the real run uses for that organization (its
/// overrides included), only without that organization's run history (cold start) and without the workflow's extra steps.
/// These tests drive the real resolver, the real price service and the real estimator through the real gate.
/// </summary>
public sealed class DesignBuildOrgModelCapTests : IDisposable
{
    private static readonly Guid OrgA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid OrgB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase($"design-build-org-model-cap-{Guid.NewGuid():N}").Options);

    private readonly Project _project;
    private readonly WorkflowDefinition _template;
    private readonly Dictionary<AgentType, Guid> _agents = [];

    private readonly ModelCapabilityView _cheap = Model(InferenceProviderKind.DeepSeek, "deepseek-flash", isDefault: true);
    private readonly ModelCapabilityView _opus = Model(InferenceProviderKind.Anthropic, "claude-opus-5-5");
    private readonly ModelCapabilityView _unpriced = Model(InferenceProviderKind.Gemini, "gemini-3.8-flash");
    private readonly ModelCapabilityView _byo = Model(InferenceProviderKind.Anthropic, "claude-opus-5-5", organizationId: OrgA);

    public DesignBuildOrgModelCapTests()
    {
        Guid owner = Guid.NewGuid();
        _project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Spring promo",
            OwnerId = owner,
            OrganizationId = OrgA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(_project);

        WorkflowTemplateDefinition def = WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey)!;
        foreach (AgentType type in def.Steps.Select(s => s.AgentType).Append(AgentType.AuthorAgent).Distinct())
        {
            var agent = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = type.ToString(),
                Description = "d",
                SystemPrompt = "p",
                AgentType = type,
                IsBuiltIn = true,
                CreatedAt = DateTime.UtcNow,
            };
            _agents[type] = agent.Id;
            _db.AgentDefinitions.Add(agent);
        }

        _db.SaveChanges();
        _template = new WorkflowTemplateProvisioningService(
                _db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build())
            .CreateFromTemplateAsync(_project.Id, def.Key, skipIfExists: true, CancellationToken.None).GetAwaiter().GetResult()!;
    }

    public void Dispose() => _db.Dispose();

    private static AgentType[] ModelAgents(WorkflowTemplateDefinition d) =>
        [.. d.Steps.Select(s => s.AgentType).Distinct()];

    private static Dictionary<AgentType, Guid?> Override(Guid model)
    {
        WorkflowTemplateDefinition def = WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey)!;
        return ModelAgents(def).ToDictionary(t => t, _ => (Guid?)model);
    }

    [Fact]
    public async Task An_org_that_overrides_the_agents_to_an_expensive_model_passes_the_unmodified_template_with_the_cap_scaled_to_it()
    {
        Harness h = Build(orgChoices: new() { [OrgA] = Override(_opus.Id) });
        DesignBuildGateResult refused = await h.Check(OrgA, accepted: null);

        refused.Outcome.Should().Be(DesignBuildGateOutcome.NotAccepted);
        refused.High.Should().BeGreaterThan(300, "the template now runs on the pricier model");
        refused.CapCredits.Should().BeApproximately(refused.High * 1.3, 0.1);
        (await h.Check(OrgA, accepted: refused.AcceptFigure)).Outcome.Should().Be(DesignBuildGateOutcome.Ok);
    }

    [Fact]
    public async Task An_org_that_overrides_to_a_cheap_model_gets_the_cheap_models_cap_and_the_template_still_passes()
    {
        // The platform default is the expensive model; the org picks the cheap one.
        Harness h = Build(orgChoices: new() { [OrgA] = Override(_cheap.Id) }, defaultModel: _opus);
        DesignBuildGateResult r = await h.Check(OrgA, accepted: null);

        r.High.Should().BeApproximately(49.23, 0.05);
        r.CapCredits.Should().BeApproximately(r.High * 1.3, 0.1);
        (await h.Check(OrgA, accepted: r.AcceptFigure)).Outcome.Should().Be(DesignBuildGateOutcome.Ok);
    }

    [Fact]
    public async Task An_org_override_to_a_model_the_price_service_cannot_price_answers_503_unavailable()
    {
        // Not listed as unpriced by the store (a price that vanished between snapshot and quote), so the resolver
        // picks it and the quote fails: closed, never the seed constants.
        Harness h = Build(orgChoices: new() { [OrgA] = Override(_unpriced.Id) });

        DesignBuildGateResult r = await h.Check(OrgA, accepted: 100_000);

        r.Outcome.Should().Be(DesignBuildGateOutcome.Unavailable);
        r.StatusCode.Should().Be(503);
        r.Code.Should().Be("ESTIMATE_UNAVAILABLE");
    }

    [Fact]
    public async Task An_org_override_the_store_lists_as_unpriced_falls_back_to_the_default_exactly_as_the_run_does()
    {
        Harness h = Build(orgChoices: new() { [OrgA] = Override(_unpriced.Id) }, unpriced: [_unpriced.Id]);

        DesignBuildGateResult r = await h.Check(OrgA, accepted: null);

        r.High.Should().BeApproximately(49.23, 0.05, "the resolver treats the unpriced pick as absent, so the run uses the default");
    }

    [Fact]
    public async Task Another_org_without_overrides_is_priced_on_the_platform_defaults()
    {
        Harness h = Build(orgChoices: new() { [OrgA] = Override(_opus.Id) });

        DesignBuildGateResult other = await h.Check(OrgB, accepted: null);

        other.High.Should().BeApproximately(49.23, 0.05);
        other.CapCredits.Should().BeApproximately(other.High * 1.3, 0.1);
        (await h.Check(OrgB, accepted: other.AcceptFigure)).Outcome.Should().Be(DesignBuildGateOutcome.Ok);
    }

    [Fact]
    public async Task An_oversized_workflow_is_still_refused_whatever_the_org_override_is()
    {
        Harness h = Build(orgChoices: new() { [OrgA] = Override(_opus.Id) });
        WorkflowDefinition oversized = await _db.WorkflowDefinitions.AsNoTracking().Include(w => w.Steps).SingleAsync(w => w.Id == _template.Id);
        int order = oversized.Steps.Max(s => s.StepOrder);
        for (int i = 0; i < 10; i++)
        {
            oversized.Steps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = _template.Id,
                StepOrder = ++order,
                StepType = global::ReelBolt.Shared.Data.Models.StepType.Agent,
                AgentDefinitionId = _agents[AgentType.StyleAndThemeExtractor],
            });
        }

        h.Tenant.Org = OrgA;
        DesignBuildGateResult r = await h.Gate.CheckAsync(OrgA, oversized, 1_000_000, CancellationToken.None);

        r.Outcome.Should().Be(DesignBuildGateOutcome.OverCap);
        r.High.Should().BeGreaterThan(r.CapCredits);
    }

    [Fact]
    public async Task A_BYO_override_prices_the_agents_at_zero_and_the_template_passes()
    {
        Harness h = Build(orgChoices: new() { [OrgA] = Override(_byo.Id) });

        DesignBuildGateResult r = await h.Check(OrgA, accepted: null);
        DesignBuildGateResult accepted = await h.Check(OrgA, accepted: 100_000);

        r.High.Should().BeLessThan(49.23, "the BYO agents add no platform credits");
        accepted.Outcome.Should().Be(DesignBuildGateOutcome.Ok);
    }

    [Fact]
    public async Task The_reference_follows_the_org_of_each_call_and_is_never_reused_across_orgs()
    {
        Harness h = Build(orgChoices: new() { [OrgA] = Override(_opus.Id) });

        double a1 = (await h.Check(OrgA, accepted: null)).CapCredits;
        double b = (await h.Check(OrgB, accepted: null)).CapCredits;
        double a2 = (await h.Check(OrgA, accepted: null)).CapCredits;

        b.Should().BeLessThan(a1 / 5);
        a2.Should().Be(a1);
    }

    // ---------------------------------------------------------------------------------------------

    private Harness Build(
        Dictionary<Guid, Dictionary<AgentType, Guid?>>? orgChoices = null,
        ModelCapabilityView? defaultModel = null,
        Guid[]? unpriced = null)
    {
        ModelCapabilityView def = defaultModel ?? _cheap;
        List<ModelCapabilityView> all = [_cheap, _opus, _unpriced, _byo];
        foreach (ModelCapabilityView m in all)
        {
            m.IsDefault = m.Id == def.Id;
        }

        var tenant = new Tenant();
        ServiceCollection services = new();
        services.AddSingleton<IInferenceProviderStore>(new Store(all, orgChoices ?? [], unpriced ?? []));
        ServiceProvider sp = services.BuildServiceProvider();
        var resolver = new InferenceProviderResolver(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Billing:Enabled"] = "true" }).Build(),
            new Passthrough(),
            NullLogger<InferenceProviderResolver>.Instance,
            tenant);
        var prices = new ModelPriceService(new MultiSource(all));
        var scopes = new CatalogScopes(_db, resolver, prices);
        var estimator = new WorkflowCostEstimator(
            scopes,
            Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()),
            NullLogger<WorkflowCostEstimator>.Instance);
        IDesignBuildEstimateGate gate = new DesignBuildEstimateGate(_db, estimator, new ConfigurationBuilder().Build());
        return new Harness(gate, tenant, _project.Id, _template.Id);
    }

    private sealed class Harness(IDesignBuildEstimateGate gate, Tenant tenant, Guid projectId, Guid workflowId)
    {
        public IDesignBuildEstimateGate Gate { get; } = gate;

        public Tenant Tenant { get; } = tenant;

        /// <summary>One request: the ambient organization and the gate's organization argument are the same caller.</summary>
        public Task<DesignBuildGateResult> Check(Guid org, double? accepted)
        {
            Tenant.Org = org;
            return Gate.CheckAsync(org, projectId, workflowId, accepted, CancellationToken.None);
        }
    }

    private static ModelCapabilityView Model(
        InferenceProviderKind kind, string code, bool isDefault = false, Guid? organizationId = null) => new()
        {
            Id = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = code,
            Kind = kind,
            Capability = InferenceProviderCapability.Chat,
            Endpoint = "https://provider.example",
            ModelName = code,
            IsDefault = isDefault,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private sealed class Tenant : InferenceTenantAccessorBase
    {
        public Guid? Org { get; set; }

        protected override Guid? AmbientOrganizationId => Org;
    }

    private sealed class Passthrough : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = ciphertext ?? string.Empty;
            return ciphertext != null;
        }
    }

    private sealed class Store(
        IReadOnlyList<ModelCapabilityView> rows,
        Dictionary<Guid, Dictionary<AgentType, Guid?>> orgChoices,
        Guid[] unpriced) : IInferenceProviderStore
    {
        public Task<IReadOnlyList<ModelCapabilityView>> LoadEnabledAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelCapabilityView>>(
                rows.Where(r => r.OrganizationId is null || r.OrganizationId == organizationId).ToList());

        public Task<IReadOnlyDictionary<Guid, Guid?>> LoadAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid?>>(new Dictionary<Guid, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadOrgAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(
                organizationId is { } o && orgChoices.TryGetValue(o, out Dictionary<AgentType, Guid?>? mine)
                    ? mine
                    : new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadBuiltInAgentOverridesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlySet<Guid>> LoadUnpricedCapabilityIdsAsync(DateTime at, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(unpriced.ToHashSet());
    }

    /// <summary>The compiled-in catalog rows for each model (none for a model the catalog does not price).</summary>
    private sealed class MultiSource(IReadOnlyList<ModelCapabilityView> models) : IModelPriceSource
    {
        public Task<ModelPriceSnapshot> LoadAsync(CancellationToken ct)
        {
            DateTime effective = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            List<ModelCapabilityPrice> rows = [];
            foreach (ModelCapabilityView m in models)
            {
                ModelPrice? price = ModelPriceCatalog.All.FirstOrDefault(p =>
                    p.ProviderKind == m.Kind && p.Capability == InferenceProviderCapability.Chat && p.Model == m.ModelName);
                rows.AddRange((price?.UsdPerUnit ?? new Dictionary<UsageKind, decimal>()).Select(u => new ModelCapabilityPrice
                {
                    Id = Guid.NewGuid(),
                    ModelCapabilityId = m.Id,
                    Unit = u.Key,
                    CostUsdPerUnit = u.Value,
                    EffectiveFrom = effective,
                }));
            }

            List<RateMultiplier> multipliers =
            [
                new() { Id = Guid.NewGuid(), BillingMode = UsageBillingMode.Platform, Multiplier = 1m, EffectiveFrom = effective },
                new() { Id = Guid.NewGuid(), BillingMode = UsageBillingMode.Byo, Multiplier = 0m, EffectiveFrom = effective },
                new() { Id = Guid.NewGuid(), BillingMode = UsageBillingMode.Runner, Multiplier = 0m, EffectiveFrom = effective },
            ];
            return Task.FromResult(new ModelPriceSnapshot(
                [.. models.Select(m => new PricedCapability(m.Id, m.ProviderId, m.OrganizationId, m.Kind, InferenceProviderCapability.Chat, m.ModelName))],
                rows, multipliers));
        }
    }
}
