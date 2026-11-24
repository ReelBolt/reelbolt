using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Execution;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>B8a: the plan-backed entitlements and every gate that reads them.</summary>
public class PlanEntitlementGateTests
{
    private static readonly OrganizationEntitlementSet TrialSet = new()
    {
        Tier = "trial",
        ByoAllowed = false,
        MaxConcurrentExecutions = 1,
        RoomsAllowed = false,
        VideoGenAllowed = false,
        StorageGb = 1
    };

    private static IOrganizationEntitlements Fixed(OrganizationEntitlementSet set)
    {
        Mock<IOrganizationEntitlements> m = new();
        m.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<OrganizationEntitlementSet>(set));
        return m.Object;
    }

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    // ---- plan-backed entitlements -------------------------------------------------------------

    private sealed class FakePlans : IPlanSource
    {
        public Plan? Subscribed { get; set; }
        public Plan? Trial { get; set; }
        public int Reads { get; private set; }

        public Task<Plan?> GetSubscribedPlanAsync(Guid organizationId, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Subscribed);
        }

        public Task<Plan?> GetPlanByKeyAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Trial);
    }

    private static Plan PlanRow(string key, string json) => new() { Id = Guid.NewGuid(), Key = key, EntitlementsJson = json };

    private static PlanBackedOrganizationEntitlements Build(FakePlans plans, bool billing, int ttl = 30) =>
        new(plans, Options.Create(new BillingOptions { Enabled = billing, EntitlementCacheSeconds = ttl }), new TenancyOptions());

    [Fact]
    public async Task Billing_disabled_is_all_allowed_selfhost()
    {
        PlanBackedOrganizationEntitlements sut = Build(new FakePlans { Trial = PlanRow("trial", """{"roomsAllowed":false}""") }, billing: false);

        OrganizationEntitlementSet set = await sut.GetAsync(Guid.NewGuid());

        set.Should().BeSameAs(OrganizationEntitlementSet.Unlimited);
        set.Tier.Should().Be("selfhost");
        PlanGate.CheckStep(StepType.EditRoom, set).Should().BeNull();
    }

    [Fact]
    public async Task No_subscription_falls_back_to_the_trial_plan()
    {
        PlanBackedOrganizationEntitlements sut = Build(
            new FakePlans { Trial = PlanRow("trial", """{"roomsAllowed":false,"videoGenAllowed":false,"storageGb":1,"maxConcurrentExecutions":1}""") }, billing: true);

        OrganizationEntitlementSet set = await sut.GetAsync(Guid.NewGuid());

        set.Tier.Should().Be("trial");
        set.RoomsAllowed.Should().BeFalse();
        set.StorageGb.Should().Be(1);
    }

    [Fact]
    public async Task Active_subscription_plan_wins_over_trial()
    {
        FakePlans plans = new()
        {
            Subscribed = PlanRow("pro", """{"roomsAllowed":true,"videoGenAllowed":true,"maxConcurrentExecutions":4}"""),
            Trial = PlanRow("trial", """{"roomsAllowed":false}""")
        };

        OrganizationEntitlementSet set = await Build(plans, billing: true).GetAsync(Guid.NewGuid());

        set.Tier.Should().Be("pro");
        set.RoomsAllowed.Should().BeTrue();
        set.MaxConcurrentExecutions.Should().Be(4);
    }

    [Fact]
    public async Task Result_is_cached_until_invalidated()
    {
        FakePlans plans = new() { Subscribed = PlanRow("creator", "{}") };
        PlanBackedOrganizationEntitlements sut = Build(plans, billing: true);
        Guid org = Guid.NewGuid();

        await sut.GetAsync(org);
        await sut.GetAsync(org);
        plans.Reads.Should().Be(1);

        plans.Subscribed = PlanRow("pro", "{}");
        (await sut.GetAsync(org)).Tier.Should().Be("creator");

        sut.Invalidate(org);
        (await sut.GetAsync(org)).Tier.Should().Be("pro");
    }

    [Fact]
    public async Task Zero_ttl_disables_the_cache()
    {
        FakePlans plans = new() { Subscribed = PlanRow("creator", "{}") };
        PlanBackedOrganizationEntitlements sut = Build(plans, billing: true, ttl: 0);
        Guid org = Guid.NewGuid();

        await sut.GetAsync(org);
        await sut.GetAsync(org);

        plans.Reads.Should().Be(2);
    }

    [Fact]
    public async Task Ef_plan_source_reads_entitling_subscription_and_ignores_canceled()
    {
        using InferenceApiDbContext db = NewDb();
        Guid org = Guid.NewGuid();
        Plan trial = PlanRow("trial", "{}");
        Plan pro = PlanRow("pro", "{}");
        db.Plans.AddRange(trial, pro);
        db.Subscriptions.Add(new Subscription { Id = Guid.NewGuid(), OrganizationId = org, PlanId = pro.Id, Status = SubscriptionStatus.Canceled });
        await db.SaveChangesAsync();

        ServiceProviderStub sp = new(db);
        EfPlanSource<InferenceApiDbContext> source = new(sp);

        (await source.GetSubscribedPlanAsync(org, default)).Should().BeNull();

        db.Subscriptions.Add(new Subscription { Id = Guid.NewGuid(), OrganizationId = org, PlanId = pro.Id, Status = SubscriptionStatus.Active });
        await db.SaveChangesAsync();
        (await source.GetSubscribedPlanAsync(org, default))!.Key.Should().Be("pro");
        (await source.GetPlanByKeyAsync("trial", default))!.Id.Should().Be(trial.Id);
    }

    private sealed class ServiceProviderStub : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private readonly InferenceApiDbContext _db;
        public ServiceProviderStub(InferenceApiDbContext db) => _db = db;
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => serviceType == typeof(InferenceApiDbContext) ? _db : null;
        public void Dispose() { }
    }

    // ---- pure gate rules ----------------------------------------------------------------------

    [Theory]
    [InlineData(StepType.EditRoom, "rooms")]
    [InlineData(StepType.GraphicsRoom, "rooms")]
    [InlineData(StepType.ColorGradeRoom, "rooms")]
    [InlineData(StepType.VideoGenerate, "video_generation")]
    public void Trial_plan_refuses_rooms_and_video_generation_with_402(StepType type, string feature)
    {
        PlanGateViolation? v = PlanGate.CheckStep(type, TrialSet);

        v.Should().NotBeNull();
        v!.Code.Should().Be("PLAN_NOT_ENTITLED");
        v.Feature.Should().Be(feature);
        v.StatusCode.Should().Be(402);
    }

    [Fact]
    public void Ungated_step_types_pass_on_trial()
    {
        PlanGate.CheckSteps([StepType.Agent, StepType.VideoAnalyze, StepType.VideoCompile], TrialSet).Should().BeNull();
    }

    [Fact]
    public void Concurrency_at_cap_is_429_and_below_cap_passes()
    {
        PlanGate.CheckConcurrency(1, TrialSet)!.StatusCode.Should().Be(429);
        PlanGate.CheckConcurrency(0, TrialSet).Should().BeNull();
    }

    [Fact]
    public void Storage_over_quota_is_413_and_unlimited_never_refuses()
    {
        const long gb = 1024L * 1024 * 1024;
        PlanGate.CheckStorage(gb - 10, 100, TrialSet)!.StatusCode.Should().Be(413);
        PlanGate.CheckStorage(gb - 100, 100, TrialSet).Should().BeNull();
        PlanGate.CheckStorage(long.MaxValue / 2, 1, OrganizationEntitlementSet.Unlimited).Should().BeNull();
    }

    // ---- save-time gate (HTTP) ----------------------------------------------------------------

    private sealed class FakeUser(
        Guid userId,
        Guid? organizationId = null,
        ReelBolt.Shared.Tenancy.OrganizationRole? orgRole = null) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "t@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => true;
        public Guid? OrganizationId => organizationId;
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => orgRole;
    }

    private static (WorkflowsController Controller, InferenceApiDbContext Db, Project Project, AgentDefinition Agent) Controller(OrganizationEntitlementSet set)
    {
        InferenceApiDbContext db = NewDb();
        Guid owner = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = owner, OrganizationId = organizationId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "a", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        db.SaveChanges();

        PlanGateService gate = new(db, Fixed(set));
        WorkflowExecutionService exec = new(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance, gate);
        FakeUser user = new(owner, organizationId, ReelBolt.Shared.Tenancy.OrganizationRole.Owner);
        WorkflowsController controller = new(
            db, user, TestTenancy.Access(db, user), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            exec, NullLogger<WorkflowsController>.Instance, planGate: gate);
        return (controller, db, project, agent);
    }

    [Fact]
    public async Task Create_with_a_room_step_on_trial_returns_402_PLAN_NOT_ENTITLED_and_writes_nothing()
    {
        (WorkflowsController c, InferenceApiDbContext db, Project project, AgentDefinition agent) = Controller(TrialSet);
        CreateWorkflowStepRequest step = new(agent.Id, 1, null, null, StepType: "EditRoom", EditRoomConfigJson: "{}");

        ActionResult<WorkflowDefinitionResponse> result = await c.Create(project.Id, new CreateWorkflowRequest("wf", [step]), default);

        ObjectResult obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(402);
        obj.Value!.ToString().Should().Contain("PLAN_NOT_ENTITLED").And.Contain("rooms");
        (await db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Update_with_a_room_step_on_trial_is_refused_before_the_stored_steps_are_touched()
    {
        (WorkflowsController c, InferenceApiDbContext db, Project project, AgentDefinition agent) = Controller(TrialSet);
        WorkflowDefinition wf = new() { Id = Guid.NewGuid(), Name = "wf", ProjectId = project.Id };
        wf.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = wf.Id, AgentDefinitionId = agent.Id, StepOrder = 1 });
        db.WorkflowDefinitions.Add(wf);
        await db.SaveChangesAsync();
        CreateWorkflowStepRequest step = new(agent.Id, 1, null, null, StepType: "ColorGradeRoom", ColorGradeRoomConfigJson: "{}");

        ActionResult<WorkflowDefinitionResponse> result = await c.Update(project.Id, wf.Id, new UpdateWorkflowRequest(null, [step]), default);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(402);
        (await db.WorkflowSteps.CountAsync(s => s.WorkflowDefinitionId == wf.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Create_with_a_room_step_is_allowed_when_billing_is_off()
    {
        (WorkflowsController c, _, Project project, AgentDefinition agent) = Controller(OrganizationEntitlementSet.Unlimited);
        CreateWorkflowStepRequest step = new(agent.Id, 1, null, null, StepType: "EditRoom", EditRoomConfigJson: "{}");

        ActionResult<WorkflowDefinitionResponse> result = await c.Create(project.Id, new CreateWorkflowRequest("wf", [step]), default);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
    }

    // ---- execute-time concurrency ------------------------------------------------------------

    [Fact]
    public async Task Execute_at_the_plan_concurrency_cap_returns_429_and_queues_nothing()
    {
        (WorkflowsController c, InferenceApiDbContext db, Project project, AgentDefinition agent) = Controller(TrialSet);
        WorkflowDefinition wf = new() { Id = Guid.NewGuid(), Name = "wf", ProjectId = project.Id };
        db.WorkflowDefinitions.Add(wf);
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = wf.Id,
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            Status = ExecutionStatus.Running,
            QueuedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        ActionResult<WorkflowExecutionResponse> result = await c.Execute(project.Id, wf.Id, null, default);

        ObjectResult obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(429);
        obj.Value!.ToString().Should().Contain("CONCURRENCY_LIMIT_REACHED");
        (await db.WorkflowExecutions.CountAsync(e => e.Status == ExecutionStatus.Queued)).Should().Be(0);
        _ = agent;
    }

    [Fact]
    public async Task Execute_below_the_cap_queues_normally()
    {
        (WorkflowsController c, InferenceApiDbContext db, Project project, _) = Controller(TrialSet);
        WorkflowDefinition wf = new() { Id = Guid.NewGuid(), Name = "wf", ProjectId = project.Id };
        db.WorkflowDefinitions.Add(wf);
        await db.SaveChangesAsync();

        ActionResult<WorkflowExecutionResponse> result = await c.Execute(project.Id, wf.Id, null, default);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
    }

    // ---- storage quota ------------------------------------------------------------------------

    [Fact]
    public async Task Upload_check_sums_live_project_file_sizes_across_the_orgs_projects()
    {
        using InferenceApiDbContext db = NewDb();
        Guid org = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        Project p1 = new() { Id = Guid.NewGuid(), Name = "a", OrganizationId = org };
        Project p2 = new() { Id = Guid.NewGuid(), Name = "b", OrganizationId = org };
        Project foreign = new() { Id = Guid.NewGuid(), Name = "c", OrganizationId = other };
        db.Projects.AddRange(p1, p2, foreign);
        const long gb = 1024L * 1024 * 1024;
        db.ProjectFiles.Add(new ProjectFile { Id = Guid.NewGuid(), ProjectId = p1.Id, SizeBytes = gb / 2 });
        db.ProjectFiles.Add(new ProjectFile { Id = Guid.NewGuid(), ProjectId = p2.Id, SizeBytes = gb / 2 - 50 });
        db.ProjectFiles.Add(new ProjectFile { Id = Guid.NewGuid(), ProjectId = foreign.Id, SizeBytes = 10 * gb });
        await db.SaveChangesAsync();
        PlanGateService gate = new(db, Fixed(TrialSet));

        (await gate.CheckUploadAsync(org, 40, default)).Should().BeNull();
        PlanGateViolation? over = await gate.CheckUploadAsync(org, 100, default);

        over.Should().NotBeNull();
        over!.Code.Should().Be("STORAGE_QUOTA_EXCEEDED");
        over.ToResult().StatusCode.Should().Be(413);
    }

    [Fact]
    public async Task Upload_check_is_skipped_for_unlimited_storage()
    {
        using InferenceApiDbContext db = NewDb();
        PlanGateService gate = new(db, Fixed(OrganizationEntitlementSet.Unlimited));

        (await gate.CheckUploadAsync(Guid.NewGuid(), long.MaxValue / 4, default)).Should().BeNull();
    }

    // ---- engine runtime re-check --------------------------------------------------------------

    private static IServiceProvider Services(IOrganizationEntitlements? e)
    {
        Mock<IServiceProvider> sp = new();
        sp.Setup(s => s.GetService(typeof(IOrganizationEntitlements))).Returns(e);
        return sp.Object;
    }

    [Theory]
    [InlineData(StepType.EditRoom)]
    [InlineData(StepType.GraphicsRoom)]
    [InlineData(StepType.ColorGradeRoom)]
    [InlineData(StepType.VideoGenerate)]
    public async Task Runtime_recheck_fails_a_gated_step_after_a_downgrade(StepType type)
    {
        WorkflowExecution exec = new() { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
        WorkflowStep step = new() { StepOrder = 3, StepType = type };

        Func<Task> act = () => WorkflowExecutorService.EnsurePlanAllowsStepAsync(Services(Fixed(TrialSet)), exec, step, default);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("PLAN_NOT_ENTITLED");
    }

    [Fact]
    public async Task Runtime_recheck_passes_when_entitled_ungated_or_without_a_service()
    {
        WorkflowExecution exec = new() { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
        await WorkflowExecutorService.EnsurePlanAllowsStepAsync(Services(Fixed(OrganizationEntitlementSet.Unlimited)), exec, new WorkflowStep { StepType = StepType.EditRoom }, default);
        await WorkflowExecutorService.EnsurePlanAllowsStepAsync(Services(Fixed(TrialSet)), exec, new WorkflowStep { StepType = StepType.Agent }, default);
        await WorkflowExecutorService.EnsurePlanAllowsStepAsync(Services(null), exec, new WorkflowStep { StepType = StepType.EditRoom }, default);
    }
}
