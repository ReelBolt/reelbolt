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
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// T-003: the REST step result carries what the assistant's run read model carries, so the dashboard and
/// the MCP never disagree about what a step cost, why it failed, or which step it was.
/// </summary>
public class WorkflowsControllerRunReadModelTests
{
    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Owner;
    }

    private static async Task<(InferenceApiDbContext Db, WorkflowsController Controller, Project Project, WorkflowExecution Run)> ArrangeAsync()
    {
        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid ownerId = Guid.NewGuid();
        FakeCurrentUser user = new(ownerId);
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId) };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf" };
        WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 3, Label = "Render film", StepType = StepType.VideoCompile };
        workflow.Steps.Add(step);
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            Status = ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            IterationCount = 1,
        };
        run.StepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = step.Id,
            Status = StepStatus.Failed,
            CreditsChargedMillis = 24_500,
            FailureCode = "ENCODE_FAILED",
            FailureClass = FailureClass.PlatformInternal,
        });
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(run);
        await db.SaveChangesAsync();

        WorkflowsController controller = new(
            db, user, TestTenancy.Access(db, user), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance);
        return (db, controller, project, run);
    }

    private static void AssertNewFields(StepResultResponse step)
    {
        step.CreditsCharged.Should().Be(24.5m);
        step.FailureCode.Should().Be("ENCODE_FAILED");
        step.FailureClass.Should().Be("PlatformInternal");
        step.StepOrder.Should().Be(3);
        step.StepLabel.Should().Be("Render film");
        step.StepType.Should().Be("VideoCompile");
    }

    [Fact]
    public async Task Get_carries_credits_failure_and_step_identity()
    {
        var (db, controller, project, run) = await ArrangeAsync();
        using (db)
        {
            ActionResult<WorkflowExecutionResponse> result = await controller.GetExecution(project.Id, run.Id, CancellationToken.None);
            AssertNewFields(((OkObjectResult)result.Result!).Value.Should().BeOfType<WorkflowExecutionResponse>().Subject.StepResults.Single());
        }
    }

    [Fact]
    public async Task List_carries_credits_failure_and_step_identity_too()
    {
        var (db, controller, project, run) = await ArrangeAsync();
        using (db)
        {
            ActionResult<List<WorkflowExecutionResponse>> result = await controller.ListExecutions(project.Id, run.WorkflowDefinitionId, CancellationToken.None);
            AssertNewFields(((OkObjectResult)result.Result!).Value.Should().BeOfType<List<WorkflowExecutionResponse>>().Subject.Single().StepResults.Single());
        }
    }
}
