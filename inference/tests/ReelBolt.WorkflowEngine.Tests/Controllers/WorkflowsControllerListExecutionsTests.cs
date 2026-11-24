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
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Workflows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// The run-history list (<c>GET .../workflows/{id}/executions</c>) carries each run's own fields and
/// each step's status, timing and compute target, never the step payloads. It used to return every
/// step's input, output, tool calls, reasoning and room transcript for every run — 9.8 MB for one
/// workflow on the test deployment, refetched on every focus of the execution page. The full run
/// stays on <c>GetExecution</c>.
/// </summary>
public class WorkflowsControllerListExecutionsTests
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

    private static readonly string Big = new('x', 50_000);

    private static async Task<(InferenceApiDbContext Db, WorkflowsController Controller, Project Project, WorkflowExecution Run)> ArrangeAsync()
    {
        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        Guid ownerId = Guid.NewGuid();
        FakeCurrentUser user = new(ownerId);

        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            Status = ExecutionStatus.Passed,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = DateTime.UtcNow,
            IterationCount = 1,
            ResultJson = Big,
            UserRequest = "make it pop",
            OutputFormatOverride = RunOutputFormat.Vertical,
        };
        WorkflowStepResult step = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = Guid.NewGuid(),
            Status = StepStatus.Completed,
            ExecutedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-4),
            TokensUsed = 1234,
            DurationMs = 60_000,
            Output = Big,
            InputJson = Big,
            OutputJson = Big,
            ToolCallsJson = Big,
            ReasoningJson = Big,
            ChatTranscriptJson = Big,
            ErrorDetails = Big,
            OutputStorageKey = $"projects/{project.Id}/outputFiles/{run.Id}/final.mp4",
            ComputeTarget = ComputeTargetKind.OrgRunner,
            FromRunner = true,
            RunnerId = Guid.NewGuid(),
        };
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(run);
        db.WorkflowStepResults.Add(step);
        db.ReviewScores.Add(new ReviewScore { Id = Guid.NewGuid(), WorkflowExecutionId = run.Id, IterationNumber = 1, Score = 8, Comments = Big });
        await db.SaveChangesAsync();

        WorkflowsController controller = new(
            db,
            user, TestTenancy.Access(db, user),
            Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance);
        return (db, controller, project, run);
    }

    [Fact]
    public async Task List_carries_what_run_history_reads_and_none_of_the_step_payloads()
    {
        var (db, controller, project, run) = await ArrangeAsync();
        using (db)
        {
            ActionResult<List<WorkflowExecutionResponse>> result = await controller.ListExecutions(project.Id, run.WorkflowDefinitionId, CancellationToken.None);
            WorkflowExecutionResponse item = ((OkObjectResult)result.Result!).Value.Should().BeOfType<List<WorkflowExecutionResponse>>().Subject.Single();

            item.Id.Should().Be(run.Id);
            item.Status.Should().Be("Passed");
            item.StartedAt.Should().Be(run.StartedAt);
            item.CompletedAt.Should().Be(run.CompletedAt);
            item.IterationCount.Should().Be(1);
            item.UserRequest.Should().Be("make it pop");
            item.OutputFormat.Should().Be("Vertical");
            item.ResultJson.Should().BeNull();

            StepResultResponse step = item.StepResults.Single();
            step.Status.Should().Be("Completed");
            step.ComputeTarget.Should().Be("OrgRunner");
            step.FromRunner.Should().BeTrue();
            step.TokensUsed.Should().Be(1234);
            step.OutputStorageKey.Should().EndWith("/final.mp4");
            step.Output.Should().BeEmpty();
            step.InputJson.Should().BeNull();
            step.OutputJson.Should().BeNull();
            step.ToolCallsJson.Should().BeNull();
            step.ReasoningJson.Should().BeNull();
            step.ChatTranscriptJson.Should().BeNull();
            step.ErrorDetails.Should().BeNull();

            item.ReviewScores.Single().Score.Should().Be(8);
            item.ReviewScores.Single().Comments.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Get_still_returns_the_whole_run()
    {
        var (db, controller, project, run) = await ArrangeAsync();
        using (db)
        {
            ActionResult<WorkflowExecutionResponse> result = await controller.GetExecution(project.Id, run.Id, CancellationToken.None);
            WorkflowExecutionResponse full = ((OkObjectResult)result.Result!).Value.Should().BeOfType<WorkflowExecutionResponse>().Subject;

            full.ResultJson.Should().Be(Big);
            full.StepResults.Single().OutputJson.Should().Be(Big);
            full.StepResults.Single().ChatTranscriptJson.Should().Be(Big);
        }
    }

    private const string AttentionJson = """{"reason":"review_below_minimum","stepOrder":4,"score":5,"minScore":8,"iterations":3}""";

    [Fact]
    public async Task A_passed_run_that_needs_attention_carries_it_on_the_list_and_on_get()
    {
        var (db, controller, project, run) = await ArrangeAsync();
        using (db)
        {
            run.AttentionJson = AttentionJson;
            await db.SaveChangesAsync();

            ActionResult<List<WorkflowExecutionResponse>> list = await controller.ListExecutions(project.Id, run.WorkflowDefinitionId, CancellationToken.None);
            WorkflowExecutionResponse item = ((OkObjectResult)list.Result!).Value.Should().BeOfType<List<WorkflowExecutionResponse>>().Subject.Single();
            ActionResult<WorkflowExecutionResponse> get = await controller.GetExecution(project.Id, run.Id, CancellationToken.None);
            WorkflowExecutionResponse full = ((OkObjectResult)get.Result!).Value.Should().BeOfType<WorkflowExecutionResponse>().Subject;

            item.Status.Should().Be("Passed");
            foreach (WorkflowExecutionResponse r in new[] { item, full })
            {
                r.NeedsAttention.Should().NotBeNull();
                r.NeedsAttention!.Score.Should().Be(5);
                r.NeedsAttention.MinScore.Should().Be(8);
                r.NeedsAttention.Iterations.Should().Be(3);
                r.NeedsAttention.Message.Should().Be(ExecutionAttention.BuildMessage(3, 5, 8));
            }
        }
    }

    [Fact]
    public async Task A_run_that_did_not_pass_never_carries_attention_even_with_a_stored_value()
    {
        var (db, controller, project, run) = await ArrangeAsync();
        using (db)
        {
            run.AttentionJson = AttentionJson;
            run.Status = ExecutionStatus.Failed;
            await db.SaveChangesAsync();

            ActionResult<List<WorkflowExecutionResponse>> list = await controller.ListExecutions(project.Id, run.WorkflowDefinitionId, CancellationToken.None);
            ((OkObjectResult)list.Result!).Value.Should().BeOfType<List<WorkflowExecutionResponse>>().Subject.Single().NeedsAttention.Should().BeNull();
            ActionResult<WorkflowExecutionResponse> get = await controller.GetExecution(project.Id, run.Id, CancellationToken.None);
            ((OkObjectResult)get.Result!).Value.Should().BeOfType<WorkflowExecutionResponse>().Subject.NeedsAttention.Should().BeNull();
        }
    }
}
