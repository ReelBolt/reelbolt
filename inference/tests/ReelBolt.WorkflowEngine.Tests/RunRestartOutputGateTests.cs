using System;
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
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Retry and Resume of a run whose output is not served answer the neutral 404 and change nothing.</summary>
public sealed class RunRestartOutputGateTests
{
    private static readonly DateTime Marker = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Owner(Guid id) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(id);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool EmailVerified => true;
    }

    private sealed record Rig(WorkflowsController Controller, InferenceApiDbContext Db, Project Project, WorkflowDefinition Workflow);

    private static Rig NewRig(DateTime? marker)
    {
        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid owner = Guid.NewGuid();
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "p",
            OwnerId = owner,
            OrganizationId = TestTenancy.OrgOf(owner),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CsamOutputHoldAt = marker,
        };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "w", ProjectId = project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.SaveChanges();
        Owner user = new(owner);
        WorkflowsController controller = new(db, user, TestTenancy.Access(db, user), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            Mock.Of<IWorkflowExecutionService>(), NullLogger<WorkflowsController>.Instance);
        return new Rig(controller, db, project, workflow);
    }

    private static WorkflowExecution Run(Rig r, DateTime startedAt, DateTime? resultAt = null)
    {
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = r.Workflow.Id,
            ProjectId = r.Project.Id,
            OrganizationId = r.Project.OrganizationId,
            Status = ExecutionStatus.Failed,
            QueuedAt = startedAt,
            StartedAt = startedAt,
        };
        r.Db.WorkflowExecutions.Add(run);
        if (resultAt is { } at)
            r.Db.WorkflowStepResults.Add(new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = run.Id, WorkflowStepId = Guid.NewGuid(), ExecutedAt = at });
        r.Db.SaveChanges();
        return run;
    }

    private static void AssertUnavailable(ObjectResult? result)
    {
        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(404);
        result.Value!.GetType().GetProperty("code")!.GetValue(result.Value).Should().Be("OUTPUT_UNAVAILABLE");
    }

    [Fact]
    public async Task Retry_of_a_run_that_predates_the_marker_is_refused_and_changes_nothing()
    {
        Rig r = NewRig(Marker);
        WorkflowExecution run = Run(r, Marker.AddHours(-2), Marker.AddHours(-1));
        var result = await r.Controller.RetryExecution(r.Project.Id, r.Workflow.Id, run.Id, null, default);
        AssertUnavailable(result.Result as ObjectResult);
        (await r.Db.WorkflowExecutions.AsNoTracking().SingleAsync()).Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task Resume_of_a_run_that_predates_the_marker_is_refused()
    {
        Rig r = NewRig(Marker);
        WorkflowExecution run = Run(r, Marker.AddHours(-2), Marker.AddHours(-1));
        var result = await r.Controller.ResumeExecution(r.Project.Id, r.Workflow.Id, run.Id, null, default);
        AssertUnavailable(result.Result as ObjectResult);
    }

    [Fact]
    public async Task A_run_with_any_held_result_is_refused_for_Retry_and_Resume()
    {
        Rig r = NewRig(Marker);
        // The run began after the marker, but one of its results is stamped before it.
        WorkflowExecution run = Run(r, Marker.AddHours(1), Marker.AddHours(-1));
        AssertUnavailable((await r.Controller.RetryExecution(r.Project.Id, r.Workflow.Id, run.Id, null, default)).Result as ObjectResult);
        AssertUnavailable((await r.Controller.ResumeExecution(r.Project.Id, r.Workflow.Id, run.Id, null, default)).Result as ObjectResult);
    }

    [Fact]
    public async Task A_later_run_and_a_project_without_a_marker_are_not_refused_by_the_gate()
    {
        Rig later = NewRig(Marker);
        WorkflowExecution after = Run(later, Marker.AddHours(1), Marker.AddHours(2));
        (await later.Controller.ResumeExecution(later.Project.Id, later.Workflow.Id, after.Id, null, default)).Result
            .Should().BeOfType<ConflictObjectResult>("the run is simply not resumable, as before");

        Rig plain = NewRig(null);
        WorkflowExecution old = Run(plain, Marker.AddHours(-2), Marker.AddHours(-1));
        (await plain.Controller.ResumeExecution(plain.Project.Id, plain.Workflow.Id, old.Id, null, default)).Result
            .Should().BeOfType<ConflictObjectResult>();
    }
}
