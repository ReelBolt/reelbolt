using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// P2-20 behaviour coverage for the SPEC 9.4 <c>POST /design-system/builds</c> route: the estimate
/// cap, the caller's acceptance, the shared affordability preflight (402) and the one happy path that
/// queues the run and leaves an execution row against the provisioned template workflow. Also pins
/// the Edit requirement and the cross-tenant 404. Direct controller construction on an InMemory
/// context, the same harness the other design-system controller tests use.
/// </summary>
public sealed class BuildsRouteTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("cccccccc-0000-0000-0000-000000000021");
    private static readonly Guid ForeignOrg = Guid.Parse("dddddddd-0000-0000-0000-000000000022");

    private const string FixedRequestText = "Build the brand and product profile for this project.";

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _billingId = Guid.NewGuid();
    private readonly Guid _strangerId = Guid.NewGuid();

    public BuildsRouteTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-system-builds-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------------------------------------------
    // 1. The 402 / 409 / 409 / 202 ladder
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unaffordable_build_returns_402_INSUFFICIENT_CREDITS()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();
        FakeEstimator estimator = Cheapest();
        estimator.RequiredMillis = 10_000;
        estimator.Unaffordable = true;

        ActionResult<BuildResponse> response = await Controller(Owner(), estimator)
            .Builds(project.Id, new BuildRequest(AcceptEstimateCredits: 10), CancellationToken.None);

        ObjectResult result = response.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
        Body(result.Value)["code"]!.GetValue<string>().Should().Be("INSUFFICIENT_CREDITS");
        Body(result.Value)["message"]!.GetValue<string>().Should().Contain("more are needed").And.Contain("returned when the run finishes");
        Body(result.Value)["missing"]!.GetValue<double>().Should().BeGreaterThan(0);
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Reference_priced_from_the_seed_constants_returns_503_ESTIMATE_UNAVAILABLE_and_queues_nothing()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();
        FakeEstimator estimator = Cheapest();
        estimator.ReferencePricedFromSeed = true;

        ActionResult<BuildResponse> response = await Controller(Owner(), estimator)
            .Builds(project.Id, new BuildRequest(AcceptEstimateCredits: 10), CancellationToken.None);

        ObjectResult result = response.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        Body(result.Value)["code"]!.GetValue<string>().Should().Be("ESTIMATE_UNAVAILABLE");
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Estimate_over_cap_returns_409_ESTIMATE_OVER_CAP()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();
        FakeEstimator estimator = new() { LowMillis = 20_000, HighMillis = 301_000, RequiredMillis = 20_000 };

        ActionResult<BuildResponse> response = await Controller(Owner(), estimator)
            .Builds(project.Id, new BuildRequest(AcceptEstimateCredits: 301), CancellationToken.None);

        ConflictObjectResult conflict = response.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        JsonObject body = Body(conflict.Value);
        body["code"]!.GetValue<string>().Should().Be("ESTIMATE_OVER_CAP");
        body["capCredits"]!.GetValue<double>().Should().BeApproximately(300.3, 0.001);
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Builds_refuses_with_409_TEMPLATE_MODIFIED_when_the_existing_workflow_lost_the_build_shape()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();
        await new WorkflowTemplateProvisioningService(
                _db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build())
            .CreateFromTemplateAsync(project.Id, WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey, true, CancellationToken.None);
        foreach (WorkflowStep step in _db.WorkflowSteps.Where(s => s.DesignSystemConfigJson != null && s.DesignSystemConfigJson.Contains("Compose")))
            step.DesignSystemConfigJson = step.DesignSystemConfigJson!.Replace("Compose", "Parse");
        _db.SaveChanges();

        ActionResult<BuildResponse> response = await Controller(Owner(), new FakeEstimator { LowMillis = 0, HighMillis = 999_000 })
            .Builds(project.Id, new BuildRequest(AcceptEstimateCredits: 5000), CancellationToken.None);

        ConflictObjectResult conflict = response.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        Body(conflict.Value)["code"]!.GetValue<string>().Should().Be("TEMPLATE_MODIFIED");
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Cold_start_high_estimate_231_passes_the_dynamic_cap_of_300_3_and_starts_once_accepted()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();
        FakeEstimator estimator = new() { LowMillis = 0, HighMillis = 231_000, RequiredMillis = 0 };

        ActionResult<BuildResponse> response = await Controller(Owner(), estimator)
            .Builds(project.Id, new BuildRequest(AcceptEstimateCredits: 231), CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        _db.WorkflowExecutions.Should().ContainSingle();
    }

    [Fact]
    public async Task Estimate_not_accepted_returns_409_with_the_estimate_and_cap()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();

        ActionResult<BuildResponse> response = await Controller(Owner(), Cheapest())
            .Builds(project.Id, new BuildRequest(), CancellationToken.None);

        ConflictObjectResult conflict = response.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);

        JsonObject body = Body(conflict.Value);
        body["code"]!.GetValue<string>().Should().Be("ESTIMATE_NOT_ACCEPTED");
        body["capCredits"]!.GetValue<double>().Should().BeApproximately(300.3, 0.001);
        JsonObject estimate = body["estimate"]!.AsObject();
        estimate["low"]!.GetValue<double>().Should().Be(5);
        estimate["high"]!.GetValue<double>().Should().Be(10);
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    /// <summary>
    /// QA T-102: a project still holding the v1 copy of the profile template builds the current version (the template
    /// is provisioned by its versioned name), whose product-only fallback never reaches the style extractor. The v1
    /// copy and its past runs stay as they are.
    /// </summary>
    [Fact]
    public async Task A_build_on_a_project_holding_the_v1_copy_runs_the_current_version()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();
        Guid agentId = _db.AgentDefinitions.First().Id;
        WorkflowDefinition v1 = Templates.ProfileWorkflowFixtures.V1(project.Id, agentId);
        _db.WorkflowDefinitions.Add(v1);
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            WorkflowDefinitionId = v1.Id,
            Status = ExecutionStatus.Passed,
            StartedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        Templates.ProfileWorkflowFixtures.ProductOnlyFallbackPath(v1.Steps).Should().Contain(8, "v1's product-only fallback runs the style extractor");

        ActionResult<BuildResponse> response = await Controller(Owner(), Cheapest())
            .Builds(project.Id, new BuildRequest(AcceptEstimateCredits: 10), CancellationToken.None);

        BuildResponse build = response.Result.Should().BeOfType<ObjectResult>().Subject.Value.Should().BeOfType<BuildResponse>().Subject;
        build.WorkflowId.Should().NotBe(v1.Id);
        WorkflowDefinition ran = await _db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync(w => w.Id == build.WorkflowId);
        ran.Name.Should().Be(WorkflowTemplateCatalog.BuildVersionedWorkflowName(Templates.ProfileWorkflowFixtures.Template));
        int style = ran.Steps.Single(s => s.AgentDefinitionId == _db.AgentDefinitions.Single(a => a.AgentType == AgentType.StyleAndThemeExtractor).Id).StepOrder;
        Templates.ProfileWorkflowFixtures.ProductOnlyFallbackPath(ran.Steps).Should().NotContain(style, "no style extractor on a product-only fallback");
        (await _db.WorkflowExecutions.CountAsync(e => e.WorkflowDefinitionId == v1.Id)).Should().Be(1, "v1's past runs stay visible");
    }

    [Fact]
    public async Task Accepted_build_returns_202_and_queues_the_template_workflow()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();

        ActionResult<BuildResponse> response = await Controller(Owner(), Cheapest())
            .Builds(project.Id, new BuildRequest(AcceptEstimateCredits: 10), CancellationToken.None);

        ObjectResult accepted = response.Result.Should().BeOfType<ObjectResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);

        string workflowName = WorkflowTemplateCatalog.BuildVersionedWorkflowName(
            WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey)!);
        WorkflowDefinition workflow = await _db.WorkflowDefinitions.SingleAsync(w => w.ProjectId == project.Id && w.Name == workflowName);

        BuildResponse build = accepted.Value.Should().BeOfType<BuildResponse>().Subject;
        build.WorkflowId.Should().Be(workflow.Id);
        build.Estimate.High.Should().Be(10);

        WorkflowExecution execution = await _db.WorkflowExecutions.SingleAsync();
        execution.WorkflowDefinitionId.Should().Be(workflow.Id);
        execution.UserRequest.Should().Be(FixedRequestText);
        build.ExecutionId.Should().Be(execution.Id);
    }

    // ---------------------------------------------------------------------------------------------------
    // 2. Billing off, the template bypass, tenancy and permission
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Billing_disabled_skips_the_estimate_and_queues_a_zero_cost_build()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();
        FakeEstimator estimator = new() { BillingEnabled = false, LowMillis = 0, HighMillis = 0, RequiredMillis = 0 };

        ActionResult<BuildResponse> response = await Controller(Owner(), estimator)
            .Builds(project.Id, new BuildRequest(), CancellationToken.None);

        ObjectResult accepted = response.Result.Should().BeOfType<ObjectResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        BuildResponse build = accepted.Value.Should().BeOfType<BuildResponse>().Subject;
        build.Estimate.Low.Should().Be(0);
        build.Estimate.High.Should().Be(0);
        _db.WorkflowExecutions.Should().HaveCount(1);
    }

    [Fact]
    public async Task Billing_enabled_but_estimator_returns_a_disabled_estimate_returns_503_ESTIMATE_UNAVAILABLE()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();
        FakeEstimator estimator = Cheapest();
        estimator.ReturnDisabledEstimate = true;

        ActionResult<BuildResponse> response = await Controller(Owner(), estimator)
            .Builds(project.Id, new BuildRequest(AcceptEstimateCredits: 0), CancellationToken.None);

        ObjectResult result = response.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        Body(result.Value)["code"]!.GetValue<string>().Should().Be("ESTIMATE_UNAVAILABLE");
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Billing_configured_on_with_no_estimator_returns_503_ESTIMATE_UNAVAILABLE()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Billing:Enabled"] = "true" })
            .Build();

        ActionResult<BuildResponse> response = await Controller(Owner(), null, config)
            .Builds(project.Id, new BuildRequest(AcceptEstimateCredits: 0), CancellationToken.None);

        ObjectResult result = response.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        Body(result.Value)["code"]!.GetValue<string>().Should().Be("ESTIMATE_UNAVAILABLE");
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Billing_off_with_no_estimator_still_queues_the_build()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();

        ActionResult<BuildResponse> response = await Controller(Owner(), null)
            .Builds(project.Id, new BuildRequest(), CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
    }

    [Fact]
    public async Task Product_profile_template_provisions_even_when_advanced_templates_are_disabled()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowTemplates:EnableAdvancedTemplates"] = "false",
            })
            .Build();

        ActionResult<BuildResponse> response = await Controller(Owner(), Cheapest(), configuration)
            .Builds(project.Id, new BuildRequest(AcceptEstimateCredits: 10), CancellationToken.None);

        ObjectResult accepted = response.Result.Should().BeOfType<ObjectResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);

        string workflowName = WorkflowTemplateCatalog.BuildVersionedWorkflowName(
            WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey)!);
        _db.WorkflowDefinitions.Should().Contain(w => w.ProjectId == project.Id && w.Name == workflowName);
    }

    [Fact]
    public async Task Billing_role_cannot_start_a_build_returns_403()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();

        ActionResult<BuildResponse> response = await Controller(
                new FakeUser(_billingId, HomeOrg, OrganizationRole.Billing), Cheapest())
            .Builds(project.Id, new BuildRequest(AcceptEstimateCredits: 10), CancellationToken.None);

        response.Result.Should().BeOfType<ForbidResult>();
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Project_in_another_org_returns_404()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedTemplateAgents();

        ActionResult<BuildResponse> response = await Controller(
                new FakeUser(_strangerId, ForeignOrg, OrganizationRole.Owner), Cheapest())
            .Builds(project.Id, new BuildRequest(AcceptEstimateCredits: 10), CancellationToken.None);

        response.Result.Should().BeOfType<NotFoundResult>();
        _db.WorkflowDefinitions.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------

    private sealed class FakeUser(Guid userId, Guid? organizationId, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    /// <summary>A 5–10 credit estimate; every gate passes unless the test says otherwise.</summary>
    private static FakeEstimator Cheapest() => new() { LowMillis = 5_000, HighMillis = 10_000, RequiredMillis = 5_000 };

    private ICurrentUser Owner() => new FakeUser(_ownerId, HomeOrg, OrganizationRole.Owner);

    private DesignSystemsController Controller(
        ICurrentUser user, FakeEstimator? estimator, IConfiguration? configuration = null)
    {
        IConfiguration config = configuration ?? new ConfigurationBuilder().Build();
        WorkflowTemplateProvisioningService templates = new(
            _db, NullLogger<WorkflowTemplateProvisioningService>.Instance, config);
        IWorkflowExecutionService executions = new WorkflowExecutionService(
            _db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance);

        return new DesignSystemsController(
            _db,
            user,
            new ProjectAccessService(_db, user),
            new DesignSystemService(
                _db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(),
                NullLogger<DesignSystemService>.Instance),
            Mock.Of<IDesignSystemIngestService>(),
            NullLogger<DesignSystemsController>.Instance,
            config,
            templates,
            executions,
            Mock.Of<ICreditAccountProvisioner>(),
            estimator);
    }

    private void SeedTemplateAgents()
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog
            .GetByKey(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey)!;

        IEnumerable<AgentType> required = template.Steps
            .Select(s => s.AgentType)
            .Concat(template.Steps
                .Where(s => s.ParallelAgentTypes is { Count: > 0 })
                .SelectMany(s => s.ParallelAgentTypes!))
            .Distinct();

        foreach (AgentType agentType in required)
        {
            _db.AgentDefinitions.Add(new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = agentType.ToString(),
                Description = "d",
                SystemPrompt = "p",
                AgentType = agentType,
                IsBuiltIn = true,
                CreatedAt = DateTime.UtcNow,
            });
        }

        _db.SaveChanges();
    }

    private Project SeedProject(Guid organizationId, Guid ownerId)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Project",
            OwnerId = ownerId,
            OrganizationId = organizationId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(project);
        _db.SaveChanges();
        return project;
    }

    private static JsonObject Body(object? value) => JsonNode
        .Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!
        .AsObject();

    /// <summary>
    /// An estimator whose estimate and preflight verdict the test sets directly, so the route's own
    /// gate ordering is what is under test, not the ledger arithmetic (covered by the billing tests).
    /// </summary>
    private sealed class FakeEstimator : IWorkflowCostEstimator
    {
        public bool BillingEnabled { get; set; } = true;
        public bool ReturnDisabledEstimate { get; set; }
        public long LowMillis { get; set; }
        public long HighMillis { get; set; }
        public long RequiredMillis { get; set; }

        /// <summary>The cold-start HIGH of the reference template the gate prices with a null organization (231 = the cap's 300 credits).</summary>
        public long ReferenceHighMillis { get; set; } = 231_000;

        /// <summary>The reference had to be priced from the seed constants.</summary>
        public bool ReferencePricedFromSeed { get; set; }
        public bool Unaffordable { get; set; }

        public Task<WorkflowCostEstimate> EstimateAsync(
            Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default)
            => Task.FromResult(organizationId is null ? Reference() : CurrentEstimate());

        public Task<WorkflowPreflightResult> PreflightAsync(
            Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default)
        {
            WorkflowPreflightOutcome outcome = Unaffordable
                ? WorkflowPreflightOutcome.InsufficientCredits
                : WorkflowPreflightOutcome.Allowed;

            return Task.FromResult(new WorkflowPreflightResult(outcome, CurrentEstimate(), Unaffordable ? 0 : HighMillis));
        }

        public Task<WorkflowCostEstimate> EstimateChargedAsync(
            Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default)
            => EstimateAsync(organizationId, runSteps, ct);

        public Task<WorkflowPreflightResult> PreflightChargedAsync(
            Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default)
            => PreflightAsync(organizationId, runSteps, ct);

        private WorkflowCostEstimate Reference() => BillingEnabled && !ReturnDisabledEstimate
            ? new WorkflowCostEstimate(true, false, 0, ReferenceHighMillis, 0, []) { PricedFromSeedFallback = ReferencePricedFromSeed }
            : WorkflowCostEstimate.Disabled;

        private WorkflowCostEstimate CurrentEstimate() => BillingEnabled && !ReturnDisabledEstimate
            ? new WorkflowCostEstimate(true, false, LowMillis, HighMillis, RequiredMillis, [])
            : WorkflowCostEstimate.Disabled;
    }
}
