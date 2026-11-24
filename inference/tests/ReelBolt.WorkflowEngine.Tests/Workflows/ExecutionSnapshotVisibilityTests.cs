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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// A run's snapshot (<see cref="WorkflowDefinitionKind.ExecutionSnapshot"/>) is a system object: never
/// listed, fetched or edited as a workflow, through REST or the assistant. The run itself shows the steps
/// it actually executed — its snapshot's — not whatever the workflow holds now.
/// </summary>
public sealed class ExecutionSnapshotVisibilityTests : IDisposable
{
    private readonly InferenceApiDbContext _db = new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase("snapvis-" + Guid.NewGuid().ToString("N")).Options);

    public void Dispose() => _db.Dispose();

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = true;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Owner;
    }

    private sealed record Seeded(Guid OwnerId, Project Project, WorkflowDefinition Workflow, WorkflowDefinition Snapshot, WorkflowExecution Run);

    private async Task<Seeded> SeedAsync()
    {
        Guid ownerId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "Writer", Description = "d", SystemPrompt = "", AgentType = AgentType.Custom, OrganizationId = project.OrganizationId };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Launch reel", ProjectId = project.Id };
        workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 1, StepType = StepType.Agent, Label = "Edited later" });
        WorkflowDefinition snapshot = new() { Id = Guid.NewGuid(), Name = "Launch reel", ProjectId = project.Id, Kind = WorkflowDefinitionKind.ExecutionSnapshot };
        WorkflowStep ran = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = snapshot.Id, AgentDefinitionId = agent.Id, StepOrder = 1, StepType = StepType.Agent, Label = "What ran" };
        snapshot.Steps.Add(ran);
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            SnapshotDefinitionId = snapshot.Id,
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            Status = ExecutionStatus.Passed,
            StartedAt = DateTime.UtcNow
        };
        run.StepResults.Add(new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = run.Id, WorkflowStepId = ran.Id, Status = StepStatus.Completed, Output = "{}", ExecutedAt = DateTime.UtcNow });

        _db.Projects.Add(project);
        _db.AgentDefinitions.Add(agent);
        _db.WorkflowDefinitions.AddRange(workflow, snapshot);
        _db.WorkflowExecutions.Add(run);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return new Seeded(ownerId, project, workflow, snapshot, run);
    }

    private WorkflowsController Controller(Guid ownerId)
    {
        FakeCurrentUser user = new(ownerId);
        return new WorkflowsController(
            _db,
            user,
            TestTenancy.Access(_db, user),
            Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(_db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(_db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance);
    }

    private async Task<string> InvokeToolAsync(Guid ownerId, Guid projectId, string name, params (string Key, object? Value)[] arguments)
    {
        FakeCurrentUser user = new(ownerId);
        AssistantWorkflowTools tools = new(_db, ConfirmationTokenService.CreateEphemeral(), TestTenancy.Access(_db, user), user);
        AIFunction tool = tools.GetTools(new AssistantToolContext(ownerId, projectId)).Single(t => t.Name == name);
        AIFunctionArguments args = [];
        foreach ((string key, object? value) in arguments)
            args[key] = value;
        object? result = await tool.InvokeAsync(args);
        return result is JsonElement element ? element.GetString() ?? element.GetRawText() : result?.ToString() ?? string.Empty;
    }

    [Fact]
    public async Task The_workflow_list_never_shows_a_run_snapshot()
    {
        Seeded s = await SeedAsync();

        ActionResult<List<WorkflowDefinitionResponse>> listed = await Controller(s.OwnerId).List(s.Project.Id, CancellationToken.None);

        ((OkObjectResult)listed.Result!).Value.As<List<WorkflowDefinitionResponse>>()
            .Select(w => w.Id).Should().Equal(s.Workflow.Id);
    }

    [Fact]
    public async Task A_run_snapshot_cannot_be_fetched_or_have_its_runs_listed_as_a_workflow()
    {
        Seeded s = await SeedAsync();
        WorkflowsController controller = Controller(s.OwnerId);

        (await controller.Get(s.Project.Id, s.Snapshot.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.ListExecutions(s.Project.Id, s.Snapshot.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.Get(s.Project.Id, s.Workflow.Id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task The_assistant_neither_lists_nor_reads_a_run_snapshot()
    {
        Seeded s = await SeedAsync();

        string listed = await InvokeToolAsync(s.OwnerId, s.Project.Id, "ListWorkflows");
        listed.Should().Contain(s.Workflow.Id.ToString()).And.NotContain(s.Snapshot.Id.ToString());

        string read = await InvokeToolAsync(s.OwnerId, s.Project.Id, "GetWorkflow", ("workflowId", s.Snapshot.Id));
        read.Should().Contain("not found").And.NotContain("What ran");
    }

    [Fact]
    public async Task The_execution_detail_carries_the_steps_the_run_executed()
    {
        Seeded s = await SeedAsync();

        ActionResult<WorkflowExecutionResponse> got = await Controller(s.OwnerId).GetExecution(s.Project.Id, s.Run.Id, CancellationToken.None);

        WorkflowExecutionResponse run = (WorkflowExecutionResponse)((OkObjectResult)got.Result!).Value!;
        run.WorkflowDefinitionId.Should().Be(s.Workflow.Id, "the run stays filed under the user's workflow");
        run.Steps.Should().NotBeNull();
        run.Steps!.Select(x => x.Label).Should().Equal("What ran");
        run.StepResults.Select(r => r.WorkflowStepId).Should().BeSubsetOf(run.Steps.Select(x => x.Id));
    }

    [Fact]
    public async Task A_run_without_a_snapshot_carries_no_steps_of_its_own()
    {
        Seeded s = await SeedAsync();
        WorkflowExecution legacy = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = s.Workflow.Id, ProjectId = s.Project.Id, OrganizationId = s.Project.OrganizationId, Status = ExecutionStatus.Passed };
        _db.WorkflowExecutions.Add(legacy);
        await _db.SaveChangesAsync();

        ActionResult<WorkflowExecutionResponse> got = await Controller(s.OwnerId).GetExecution(s.Project.Id, legacy.Id, CancellationToken.None);

        ((WorkflowExecutionResponse)((OkObjectResult)got.Result!).Value!).Steps.Should().BeNull("the page falls back to the workflow's own steps");
    }

    // ---- a snapshot cannot be written, deleted or run through any door -----------------------------

    private async Task AssertSnapshotUntouchedAsync(Seeded s, string because)
    {
        _db.ChangeTracker.Clear();
        WorkflowDefinition snapshot = await _db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync(w => w.Id == s.Snapshot.Id);
        snapshot.Kind.Should().Be(WorkflowDefinitionKind.ExecutionSnapshot, because);
        snapshot.Name.Should().Be("Launch reel", because);
        snapshot.Steps.Select(x => x.Label).Should().Equal(["What ran"], because);
        (await _db.WorkflowExecutions.CountAsync()).Should().Be(1, "no run was queued: " + because);
    }

    [Fact]
    public async Task Put_on_a_run_snapshot_is_404_and_leaves_it_untouched()
    {
        Seeded s = await SeedAsync();
        Guid agentId = (await _db.AgentDefinitions.AsNoTracking().SingleAsync()).Id;

        ActionResult<WorkflowDefinitionResponse> put = await Controller(s.OwnerId).Update(
            s.Project.Id, s.Snapshot.Id,
            new UpdateWorkflowRequest("Renamed", [new CreateWorkflowStepRequest(agentId, StepOrder: 1, EdgeConditionJson: null, Label: "Hijacked", StepType: "Agent")]),
            CancellationToken.None);

        put.Result.Should().BeOfType<NotFoundResult>();
        await AssertSnapshotUntouchedAsync(s, "PUT");
    }

    [Fact]
    public async Task Delete_on_a_run_snapshot_is_404_and_leaves_it_in_place()
    {
        Seeded s = await SeedAsync();

        IActionResult deleted = await Controller(s.OwnerId).Delete(s.Project.Id, s.Snapshot.Id, CancellationToken.None);

        deleted.Should().BeOfType<NotFoundResult>();
        await AssertSnapshotUntouchedAsync(s, "DELETE");
        (await _db.WorkflowExecutions.SingleAsync()).SnapshotDefinitionId.Should().Be(s.Snapshot.Id, "the run still points at its snapshot");
    }

    [Fact]
    public async Task Execute_on_a_run_snapshot_is_404_and_queues_nothing()
    {
        Seeded s = await SeedAsync();

        ActionResult<WorkflowExecutionResponse> executed = await Controller(s.OwnerId).Execute(
            s.Project.Id, s.Snapshot.Id, new ExecuteWorkflowRequest(), CancellationToken.None);

        executed.Result.Should().BeOfType<NotFoundResult>();
        await AssertSnapshotUntouchedAsync(s, "Execute");
    }

    private sealed class GrantingGuard : IAssistantExecutionGuard
    {
        public async Task<RunRateDecision> TryAcquireAsync(Guid userId, Guid projectId, RunRateBucket bucket, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct)
        {
            await insertCallback();
            return new RunRateDecision(true, bucket, 1, 10, 3600, 0);
        }
    }

    private IReadOnlyList<AIFunction> AssistantTools(Seeded s) =>
        AssistantToolTestFactory.CreateProvider(_db, new FakeCurrentUser(s.OwnerId))
            .GetTools(new AssistantToolContext(s.OwnerId, s.Project.Id));

    private static async Task<string> RunToolAsync(IEnumerable<AIFunction> tools, string name, params (string Key, object? Value)[] arguments)
    {
        AIFunctionArguments args = [];
        foreach ((string key, object? value) in arguments)
            args[key] = value;
        object? result = await tools.Single(t => t.Name == name).InvokeAsync(args);
        return result is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString()! : result is JsonElement raw ? raw.GetRawText() : result?.ToString() ?? string.Empty;
    }

    [Fact]
    public async Task The_assistant_cannot_propose_apply_delete_or_execute_a_run_snapshot()
    {
        Seeded s = await SeedAsync();
        IReadOnlyList<AIFunction> tools = AssistantTools(s);
        Guid agentId = (await _db.AgentDefinitions.AsNoTracking().SingleAsync()).Id;
        List<CreateWorkflowStepRequest> steps = [new(agentId, StepOrder: 1, EdgeConditionJson: null, Label: "Hijacked", StepType: "Agent")];

        string proposed = await RunToolAsync(tools, "ProposeWorkflow", ("workflowId", s.Snapshot.Id), ("name", null), ("steps", steps));
        proposed.Should().Contain("workflow not found").And.NotContain("confirmationToken");

        string applied = await RunToolAsync(tools, "ApplyWorkflow", ("workflowId", s.Snapshot.Id), ("name", null), ("steps", steps), ("confirmationToken", "forged"));
        applied.Should().Contain("workflow not found");

        string proposeDelete = await RunToolAsync(tools, "ProposeDeleteWorkflow", ("workflowId", s.Snapshot.Id));
        proposeDelete.Should().Contain("not found").And.NotContain("confirmationToken");

        string applyDelete = await RunToolAsync(tools, "ApplyDeleteWorkflow", ("workflowId", s.Snapshot.Id), ("confirmationToken", "forged"));
        applyDelete.Should().Contain("not found");

        // Execute: the guard grants the slot and runs the insert callback, so the only thing standing between
        // the snapshot and a queued run is the execution service's own kind filter. The refusal surfaces as the
        // service's WorkflowNotFoundException (the same as for an id that does not exist) or as a tool error.
        IReadOnlyList<AIFunction> executeTools = AssistantToolTestFactory.CreateProvider(
                _db, new FakeCurrentUser(s.OwnerId),
                executionGuard: new GrantingGuard(),
                executionService: new WorkflowExecutionService(_db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance))
            .GetTools(new AssistantToolContext(s.OwnerId, s.Project.Id));
        string executed;
        try
        {
            executed = await RunToolAsync(executeTools, "ExecuteWorkflow", ("workflowId", s.Snapshot.Id), ("userRequest", "go"), ("regenerateVideoClips", false));
        }
        catch (WorkflowNotFoundException)
        {
            executed = "workflow not found";
        }
        executed.Should().Contain("not found").And.NotContain("workflowName", "no run was started");

        await AssertSnapshotUntouchedAsync(s, "assistant tools");
    }

    [Fact]
    public async Task The_workflow_assistant_endpoints_refuse_a_run_snapshot()
    {
        Seeded s = await SeedAsync();
        FakeCurrentUser user = new(s.OwnerId);
        WorkflowAssistantController controller = new(
            _db, user, TestTenancy.Access(_db, user), new WorkflowEditDiffService(_db),
            new AssistantExecutionGuard(_db, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(_db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance));
        Guid agentId = (await _db.AgentDefinitions.AsNoTracking().SingleAsync()).Id;
        List<CreateWorkflowStepRequest> steps = [new(agentId, StepOrder: 1, EdgeConditionJson: null, Label: "Hijacked", StepType: "Agent")];

        (await controller.Propose(s.Project.Id, new ProposeWorkflowRequest(s.Snapshot.Id, null, steps), CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>();
        (await controller.Apply(s.Project.Id, new ApplyWorkflowRequest(s.Snapshot.Id, null, steps, "forged"), CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>();

        await AssertSnapshotUntouchedAsync(s, "assistant REST");
    }
}
