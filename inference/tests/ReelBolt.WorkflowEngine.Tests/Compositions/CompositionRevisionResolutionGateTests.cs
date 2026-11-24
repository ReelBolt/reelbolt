using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compositions;

/// <summary>
/// QA-RR-01 / QA-RR-04: a revision inherits its source run's output resolution, so an organization
/// downgraded since that run must be told PLAN_NOT_ENTITLED by POST /revisions — not a 500, and not
/// INSUFFICIENT_CREDITS for a render its plan would refuse anyway — and no revision row is written.
/// </summary>
public class CompositionRevisionResolutionGateTests
{
    private static readonly OrganizationEntitlementSet Creator = new() { Tier = "creator", MaxOutputHeight = 1080 };

    /// <summary>Refuses every preflight: the workspace "cannot afford" the run.</summary>
    private sealed class BrokeEstimator : IWorkflowCostEstimator
    {
        public bool BillingEnabled => true;

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(Estimate(steps));

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowPreflightResult(WorkflowPreflightOutcome.InsufficientCredits, Estimate(steps), 0));

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            EstimateAsync(organizationId, [.. runSteps.Where(s => chargedStepOrders.Contains(s.StepOrder))], ct);

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            PreflightAsync(organizationId, [.. runSteps.Where(s => chargedStepOrders.Contains(s.StepOrder))], ct);

        private static WorkflowCostEstimate Estimate(IReadOnlyList<WorkflowStep> steps) =>
            new(true, false, (steps.Count + 1) * 1_000, (steps.Count + 1) * 3_000, (steps.Count + 1) * 3_000, []);
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
    }

    [Fact]
    public async Task A_revision_of_a_2160p_run_after_a_downgrade_to_Creator_is_402_PLAN_NOT_ENTITLED_and_writes_no_row()
    {
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase("revision-gate-" + Guid.NewGuid().ToString("N")).Options);
        Guid owner = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        Guid reviser = Guid.NewGuid();
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Promo", ProjectId = project.Id };
        WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = reviser, StepOrder = 1, StepType = StepType.Agent };
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            Status = ExecutionStatus.Passed,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1),
            OutputResolution = 2160
        };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(new AgentDefinition { Id = reviser, Name = "CompositionReviser", AgentType = AgentType.CompositionReviser, IsBuiltIn = true });
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowSteps.Add(step);
        db.WorkflowExecutions.Add(run);
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = step.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{project.Id}/outputFiles/{run.Id}/promo.mp4"
        });
        await db.SaveChangesAsync();

        Mock<IOrganizationEntitlements> entitlements = new();
        entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(Creator));
        PlanGateService gate = new(db, entitlements.Object);
        CompositionRevisionService service = new(
            db,
            new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance, gate),
            new ConfigurationBuilder().Build(),
            NullLogger<CompositionRevisionService>.Instance,
            new BrokeEstimator(),
            planGate: gate);
        FakeCurrentUser user = new(owner);
        CompositionRevisionsController controller = new(TestTenancy.Access(db, user), service, user);

        ActionResult<CompositionRevisionResponse> result = await controller.Create(
            project.Id, run.Id, new CreateCompositionRevisionRequest("Bigger intro title"), CancellationToken.None);

        ObjectResult obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(402);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(obj.Value));
        body.RootElement.GetProperty("code").GetString().Should().Be("PLAN_NOT_ENTITLED");
        body.RootElement.GetProperty("feature").GetString().Should().Be("resolution");
        (await db.CompositionRevisions.CountAsync()).Should().Be(0);
        (await db.WorkflowExecutions.CountAsync()).Should().Be(1, "only the source run exists");
    }
}
