#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// The profile-build cap follows live model prices: cap = (cold-start HIGH of the REFERENCE template priced at the
/// live prices of the models the resolver picks) x <c>DesignSystem:Build:FallbackCapMargin</c> (1.3), optionally
/// lowered by <c>MaxFallbackCreditsHigh</c>. The reference is priced by the REAL estimator over the compiled-in
/// <see cref="ModelPriceCatalog"/> rows; when it cannot be priced by real prices (the seed constants must never
/// price it) the gate fails closed with 503. Figures at the catalog prices of 2026-10-17: ~49 / ~387 / ~773
/// credits on deepseek-flash / Sonnet 5.5 / Opus 5.5.
/// </summary>
public sealed class DesignBuildDynamicCapTests : IDisposable
{
    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase($"design-build-dynamic-cap-{Guid.NewGuid():N}").Options);

    private readonly Project _project;
    private readonly WorkflowDefinition _template;
    private readonly Dictionary<AgentType, Guid> _agents = [];

    public DesignBuildDynamicCapTests()
    {
        Guid owner = Guid.NewGuid();
        _project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Spring promo",
            OwnerId = owner,
            OrganizationId = TestTenancy.OrgOf(owner),
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

    // ---------------------------------------------------------------------------------------------
    // (a) and (b): the cap follows the price
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_cheap_default_model_prices_the_cold_start_build_near_49_and_passes_a_cap_near_64_once_accepted()
    {
        IDesignBuildEstimateGate gate = Gate(Catalog(InferenceProviderKind.DeepSeek, "deepseek-flash"));

        DesignBuildGateResult refused = await Check(gate, accepted: null);
        refused.Outcome.Should().Be(DesignBuildGateOutcome.NotAccepted);
        refused.High.Should().BeApproximately(49.23, 0.05);
        Cap(refused).Should().BeInRange(63.9, 64.1);

        DesignBuildGateResult ok = await Check(gate, accepted: refused.AcceptFigure);
        ok.Outcome.Should().Be(DesignBuildGateOutcome.Ok);
    }

    [Fact]
    public async Task An_expensive_default_model_passes_the_same_unmodified_template_because_the_cap_scales()
    {
        IDesignBuildEstimateGate gate = Gate(Catalog(InferenceProviderKind.Anthropic, "claude-opus-5-5"));

        DesignBuildGateResult refused = await Check(gate, accepted: null);
        refused.High.Should().BeApproximately(773.43, 0.5);
        Cap(refused).Should().BeInRange(1004, 1007);

        (await Check(gate, accepted: refused.AcceptFigure)).Outcome.Should().Be(DesignBuildGateOutcome.Ok);
    }

    [Fact]
    public async Task The_cap_on_a_mid_priced_model_is_the_reference_high_times_the_margin()
    {
        DesignBuildGateResult r = await Check(Gate(Catalog(InferenceProviderKind.Anthropic, "claude-sonnet-5-5")), accepted: null);

        r.High.Should().BeApproximately(386.72, 0.5);
        Cap(r).Should().BeApproximately(r.High * 1.3, 0.1);
    }

    // ---------------------------------------------------------------------------------------------
    // (c): an unpriced reference fails closed, never free, never the seed constants
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_default_model_with_no_price_row_answers_503_unavailable_instead_of_the_seed_constants()
    {
        // gemini-3.8-flash has no published price, so the catalog has no rows for it.
        IDesignBuildEstimateGate gate = Gate(Catalog(InferenceProviderKind.Gemini, "gemini-3.8-flash"));

        DesignBuildGateResult r = await Check(gate, accepted: 100_000);

        r.Outcome.Should().Be(DesignBuildGateOutcome.Unavailable);
        r.StatusCode.Should().Be(503);
        r.Code.Should().Be("ESTIMATE_UNAVAILABLE");
    }

    [Fact]
    public async Task A_resolver_that_finds_no_model_answers_503_unavailable()
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no default Chat model"));
        var prices = new ModelPriceService(new CatalogScopes.CatalogSource(Guid.NewGuid(), Guid.NewGuid(), InferenceProviderKind.DeepSeek, "deepseek-flash"));

        DesignBuildGateResult r = await Check(Gate(new CatalogScopes(_db, resolver.Object, prices)), accepted: 100_000);

        r.Outcome.Should().Be(DesignBuildGateOutcome.Unavailable);
    }

    [Fact]
    public async Task No_price_service_at_all_answers_503_unavailable()
    {
        DesignBuildGateResult r = await Check(Gate(new CatalogScopes(_db, CatalogScopes.Resolver(Guid.NewGuid(), Guid.NewGuid(), InferenceProviderKind.DeepSeek, "deepseek-flash"), null)), accepted: 100_000);

        r.Outcome.Should().Be(DesignBuildGateOutcome.Unavailable);
    }

    // ---------------------------------------------------------------------------------------------
    // (d): a workflow heavier than the reference is over the cap
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_template_plus_extra_paid_agent_steps_is_refused_over_cap_with_the_computed_cap()
    {
        IDesignBuildEstimateGate gate = Gate(Catalog(InferenceProviderKind.DeepSeek, "deepseek-flash"));
        WorkflowDefinition oversized = await LoadTemplateAsync();
        int order = oversized.Steps.Max(s => s.StepOrder);
        foreach (AgentType type in new[] { AgentType.StyleAndThemeExtractor, AgentType.StyleAndThemeExtractor, AgentType.StyleAndThemeExtractor })
        {
            oversized.Steps.Add(AgentStep(++order, type));
        }

        DesignBuildGateResult r = await gate.CheckAsync(_project.OrganizationId, oversized, 100_000, CancellationToken.None);

        r.Outcome.Should().Be(DesignBuildGateOutcome.OverCap);
        r.StatusCode.Should().Be(409);
        Cap(r).Should().BeInRange(63.9, 64.1);
        r.High.Should().BeGreaterThan(r.CapCredits);
        r.ToBody().Should().BeEquivalentTo(new
        {
            code = "ESTIMATE_OVER_CAP",
            message = r.Message,
            estimate = new { low = r.Low, high = r.High },
            capCredits = r.CapCredits,
        });
    }

    [Fact]
    public async Task The_analyzer_steps_swapped_for_the_heavy_style_extractor_is_refused_over_cap()
    {
        IDesignBuildEstimateGate gate = Gate(Catalog(InferenceProviderKind.DeepSeek, "deepseek-flash"));
        WorkflowDefinition heavy = await LoadTemplateAsync();
        foreach (WorkflowStep step in heavy.Steps.Where(s => s.StepType == global::ReelBolt.Shared.Data.Models.StepType.Agent))
        {
            step.AgentDefinitionId = _agents[AgentType.StyleAndThemeExtractor];
        }

        DesignBuildGateResult r = await gate.CheckAsync(_project.OrganizationId, heavy, 100_000, CancellationToken.None);

        r.Outcome.Should().Be(DesignBuildGateOutcome.OverCap);
        r.High.Should().BeGreaterThan(r.CapCredits);
    }

    // ---------------------------------------------------------------------------------------------
    // (e): configuration
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_ceiling_below_the_dynamic_cap_lowers_the_cap()
    {
        DesignBuildGateResult r = await Check(
            Gate(Catalog(InferenceProviderKind.DeepSeek, "deepseek-flash"), ("DesignSystem:Build:MaxFallbackCreditsHigh", "20")),
            accepted: 100_000);

        r.Outcome.Should().Be(DesignBuildGateOutcome.OverCap);
        Cap(r).Should().Be(20);
    }

    [Fact]
    public async Task A_ceiling_above_the_dynamic_cap_has_no_effect()
    {
        DesignBuildGateResult r = await Check(
            Gate(Catalog(InferenceProviderKind.DeepSeek, "deepseek-flash"), ("DesignSystem:Build:MaxFallbackCreditsHigh", "5000")),
            accepted: 100_000);

        r.Outcome.Should().Be(DesignBuildGateOutcome.Ok);
        Cap(r).Should().BeInRange(63.9, 64.1);
    }

    [Fact]
    public async Task The_margin_setting_changes_the_cap()
    {
        DesignBuildGateResult r = await Check(
            Gate(Catalog(InferenceProviderKind.DeepSeek, "deepseek-flash"), ("DesignSystem:Build:FallbackCapMargin", "2")),
            accepted: 100_000);

        Cap(r).Should().BeApproximately(r.High * 2, 0.02);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("abc")]
    public async Task An_invalid_margin_falls_back_to_1_3(string margin)
    {
        DesignBuildGateResult r = await Check(
            Gate(Catalog(InferenceProviderKind.DeepSeek, "deepseek-flash"), ("DesignSystem:Build:FallbackCapMargin", margin)),
            accepted: 100_000);

        Cap(r).Should().BeApproximately(r.High * 1.3, 0.02);
    }

    // ---------------------------------------------------------------------------------------------
    // BYO: a price of 0 is a real price, not a missing one
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_BYO_only_default_chat_provider_prices_the_reference_at_0_so_the_cap_is_0_and_a_zero_estimate_passes()
    {
        IDesignBuildEstimateGate gate = Gate(CatalogScopes.For(_db, InferenceProviderKind.Anthropic, "claude-sonnet-5-5", byoOrganizationId: _project.OrganizationId));

        DesignBuildGateResult r = await Check(gate, accepted: null);

        r.Outcome.Should().Be(DesignBuildGateOutcome.Ok);
        r.High.Should().Be(0);
        Cap(r).Should().Be(0);
        r.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Execute_of_the_build_template_on_a_BYO_only_default_provider_starts_the_run_instead_of_503()
    {
        CatalogScopes scopes = CatalogScopes.For(_db, InferenceProviderKind.Anthropic, "claude-sonnet-5-5", byoOrganizationId: _project.OrganizationId);
        // The credit-hold preflight is a separate check from the cap gate (the cold-start hold is not zero for a BYO
        // run), so the workspace is funded and what is pinned here is that the GATE lets the zero-cost build through.
        _db.Set<CreditAccount>().Add(new CreditAccount { Id = Guid.NewGuid(), OrganizationId = _project.OrganizationId, BalanceMillis = 1_000_000 });
        await _db.SaveChangesAsync();

        ActionResult<WorkflowExecutionResponse> r = await Ctl(Gate(scopes), Estimator(scopes))
            .Execute(_project.Id, _template.Id, null, CancellationToken.None);

        r.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().BeInRange(200, 299);
        _db.WorkflowExecutions.Should().ContainSingle();
    }

    [Fact]
    public async Task Execute_of_the_build_template_with_a_platform_default_and_no_price_row_still_returns_503()
    {
        CatalogScopes scopes = Catalog(InferenceProviderKind.Gemini, "gemini-3.8-flash");

        ActionResult<WorkflowExecutionResponse> r = await Ctl(Gate(scopes), Estimator(scopes))
            .Execute(_project.Id, _template.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 100_000), CancellationToken.None);

        r.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private static double Cap(DesignBuildGateResult r) => r.CapCredits;

    private Task<DesignBuildGateResult> Check(IDesignBuildEstimateGate gate, double? accepted)
        => gate.CheckAsync(_project.OrganizationId, _project.Id, _template.Id, accepted, CancellationToken.None);

    private async Task<WorkflowDefinition> LoadTemplateAsync()
        => await _db.WorkflowDefinitions.AsNoTracking().Include(w => w.Steps).SingleAsync(w => w.Id == _template.Id);

    private WorkflowStep AgentStep(int order, AgentType type) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = _template.Id,
        StepOrder = order,
        StepType = global::ReelBolt.Shared.Data.Models.StepType.Agent,
        AgentDefinitionId = _agents[type],
    };

    private CatalogScopes Catalog(InferenceProviderKind kind, string model) => CatalogScopes.For(_db, kind, model);

    private static WorkflowCostEstimator Estimator(CatalogScopes scopes) => new(
        scopes,
        Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
        Options.Create(new StepCreditEstimateOptions()),
        NullLogger<WorkflowCostEstimator>.Instance);

    private IDesignBuildEstimateGate Gate(CatalogScopes scopes, params (string Key, string Value)[] settings)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build();
        return new DesignBuildEstimateGate(_db, Estimator(scopes), config);
    }

    private WorkflowsController Ctl(IDesignBuildEstimateGate gate, IWorkflowCostEstimator estimator)
    {
        var user = new FakeUser(_project.OwnerId);
        return new WorkflowsController(
            _db, user, TestTenancy.Access(_db, user), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(
                _db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(_db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance,
            costEstimator: estimator,
            designBuildGate: gate);
    }

    private sealed class FakeUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = true;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }
}
