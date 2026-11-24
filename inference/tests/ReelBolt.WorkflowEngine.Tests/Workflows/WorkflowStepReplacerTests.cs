using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// T-002: no step-replacing or delete path can destroy a past run's results, including runs started before
/// snapshots existed (no <c>snapshot_definition_id</c>, results referencing the workflow's own steps). Every
/// scenario runs with foreign keys ENFORCED (the step_results → steps cascade is what destroyed them): on
/// Sqlite always, and on a real migrated Postgres when <c>REELBOLT_TEST_PG</c> is set.
/// </summary>
public abstract class WorkflowStepReplacerTestsBase : IAsyncLifetime
{
    protected abstract Action<DbContextOptionsBuilder>? Configure { get; }

    protected abstract Task CreateSchemaAsync();

    protected InferenceApiDbContext NewDb()
    {
        DbContextOptionsBuilder<InferenceApiDbContext> builder = new();
        Configure!(builder);
        builder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        return new InferenceApiDbContext(builder.Options);
    }

    private InferenceApiDbContext? _db;
    protected InferenceApiDbContext Db => _db!;

    public async Task InitializeAsync()
    {
        if (Configure is null) return;
        await CreateSchemaAsync();
        _db = NewDb();
    }

    public virtual Task DisposeAsync()
    {
        _db?.Dispose();
        return Task.CompletedTask;
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = true;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Owner;
    }

    protected sealed record Seeded(Guid OwnerId, Project Project, AgentDefinition Agent, WorkflowDefinition Workflow, WorkflowStep Step1, WorkflowStep Step2);

    /// <summary>A user, their organization, a project and a two-step Standard workflow, all FK-valid.</summary>
    protected async Task<Seeded> SeedAsync()
    {
        Guid ownerId = Guid.NewGuid();
        Guid orgId = TestTenancy.OrgOf(ownerId);
        Db.ApplicationUsers.Add(new ApplicationUser { Id = ownerId, Email = $"{ownerId:N}@t.local", DisplayName = "o", MustChangePassword = false });
        Db.Organizations.Add(new Organization { Id = orgId, Name = "o", Slug = "o-" + orgId.ToString("N"), Kind = ReelBolt.Shared.Tenancy.OrganizationKind.Personal });
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = orgId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "Writer", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Launch reel", ProjectId = project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowStep step1 = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 1, StepType = StepType.Agent, Label = "Write" };
        WorkflowStep step2 = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 2, StepType = StepType.Agent, Label = "Review" };
        workflow.Steps.Add(step1);
        workflow.Steps.Add(step2);
        Db.Projects.Add(project);
        Db.AgentDefinitions.Add(agent);
        Db.WorkflowDefinitions.Add(workflow);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return new Seeded(ownerId, project, agent, workflow, step1, step2);
    }

    /// <summary>A run of <paramref name="workflowId"/>, with one Completed result per step in <paramref name="ranSteps"/>.</summary>
    protected async Task<WorkflowExecution> AddRunAsync(Seeded s, Guid workflowId, ExecutionStatus status, Guid? snapshotId, params WorkflowStep[] ranSteps)
    {
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            SnapshotDefinitionId = snapshotId,
            ProjectId = s.Project.Id,
            OrganizationId = s.Project.OrganizationId,
            Status = status,
            StartedAt = DateTime.UtcNow
        };
        foreach (WorkflowStep step in ranSteps)
            run.StepResults.Add(new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = run.Id, WorkflowStepId = step.Id, Status = StepStatus.Completed, Output = "out of " + step.Label, ExecutedAt = DateTime.UtcNow });
        Db.WorkflowExecutions.Add(run);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return run;
    }

    /// <summary>An engine-taken snapshot of the workflow (kind 5, owned by <paramref name="runId"/>) holding a copy of step 1.</summary>
    private async Task<(WorkflowDefinition Snapshot, WorkflowStep Step)> AddEngineSnapshotAsync(Seeded s)
    {
        WorkflowDefinition snapshot = new() { Id = Guid.NewGuid(), Name = s.Workflow.Name, ProjectId = s.Project.Id, Kind = WorkflowDefinitionKind.ExecutionSnapshot, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowStep copy = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = snapshot.Id, AgentDefinitionId = s.Agent.Id, StepOrder = 1, StepType = StepType.Agent, Label = "Write" };
        snapshot.Steps.Add(copy);
        Db.WorkflowDefinitions.Add(snapshot);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return (snapshot, copy);
    }

    /// <summary>The engine's ownership link (shadow column the API context does not map).</summary>
    private Task OwnSnapshotAsync(Guid snapshotId, Guid runId) =>
        Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_definitions SET snapshot_of_execution_id = {runId} WHERE id = {snapshotId}");

    protected WorkflowsController Controller(Guid ownerId)
    {
        FakeCurrentUser user = new(ownerId);
        return new WorkflowsController(
            Db,
            user,
            TestTenancy.Access(Db, user),
            Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(Db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(Db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance);
    }

    private WorkflowAssistantController AssistantController(Guid ownerId)
    {
        FakeCurrentUser user = new(ownerId);
        return new WorkflowAssistantController(
            Db,
            user,
            TestTenancy.Access(Db, user),
            new WorkflowEditDiffService(Db),
            new AssistantExecutionGuard(Db, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(Db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance));
    }

    private static string Text(object? raw) => raw switch
    {
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
        JsonElement e => e.GetRawText(),
        _ => raw?.ToString() ?? string.Empty
    };

    private static async Task<string> InvokeAsync(IEnumerable<AIFunction> tools, string name, params (string Key, object? Value)[] args)
    {
        AIFunction function = tools.Single(t => t.Name == name);
        AIFunctionArguments arguments = [];
        foreach ((string key, object? value) in args)
            arguments[key] = value;
        return Text(await function.InvokeAsync(arguments));
    }

    protected static List<CreateWorkflowStepRequest> NewSteps(Guid agentId) =>
    [
        new(agentId, StepOrder: 1, EdgeConditionJson: null, Label: "Rewritten", StepType: "Agent")
    ];

    /// <summary>The legacy run's results, its steps, and where those steps now live.</summary>
    private async Task AssertLegacyRunSurvivedAsync(Seeded s, WorkflowExecution legacy)
    {
        List<WorkflowStepResult> results = await Db.WorkflowStepResults.AsNoTracking().Where(r => r.WorkflowExecutionId == legacy.Id).ToListAsync();
        results.Select(r => r.Output).Should().BeEquivalentTo(["out of Write", "out of Review"], "replacing the steps must not cascade-delete a past run's results");

        WorkflowExecution after = await Db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == legacy.Id);
        after.SnapshotDefinitionId.Should().NotBeNull("the legacy run is attached to the archive of its steps");
        WorkflowDefinition archive = await Db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == after.SnapshotDefinitionId);
        archive.Kind.Should().Be(WorkflowDefinitionKind.ExecutionSnapshot);
        archive.ProjectId.Should().Be(s.Project.Id);
        (await Db.WorkflowSteps.AsNoTracking().Where(st => st.WorkflowDefinitionId == archive.Id).Select(st => st.Id).ToListAsync())
            .Should().BeEquivalentTo(new[] { s.Step1.Id, s.Step2.Id }, "the steps the results reference are re-parented, not copied");

        (await Db.WorkflowSteps.AsNoTracking().Where(st => st.WorkflowDefinitionId == s.Workflow.Id).Select(st => st.Label).ToListAsync())
            .Should().Equal(["Rewritten"], "the workflow holds exactly the new steps");
    }

    [DatabaseFact]
    public async Task Put_over_a_legacy_run_keeps_its_results_attached_to_an_archive()
    {
        Seeded s = await SeedAsync();
        WorkflowExecution legacy = await AddRunAsync(s, s.Workflow.Id, ExecutionStatus.Passed, null, s.Step1, s.Step2);

        ActionResult<WorkflowDefinitionResponse> put = await Controller(s.OwnerId).Update(
            s.Project.Id, s.Workflow.Id, new UpdateWorkflowRequest("Launch reel v2", NewSteps(s.Agent.Id)), CancellationToken.None);

        put.Result.Should().BeOfType<OkObjectResult>();
        await AssertLegacyRunSurvivedAsync(s, legacy);
        (await Db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == s.Workflow.Id)).Name.Should().Be("Launch reel v2");

        ActionResult<List<WorkflowDefinitionResponse>> listed = await Controller(s.OwnerId).List(s.Project.Id, CancellationToken.None);
        ((OkObjectResult)listed.Result!).Value.As<List<WorkflowDefinitionResponse>>()
            .Select(w => w.Id).Should().Equal([s.Workflow.Id], "the archive is a hidden system object");
    }

    [DatabaseFact]
    public async Task Put_with_no_referenced_steps_deletes_the_old_steps_and_creates_no_archive()
    {
        Seeded s = await SeedAsync();
        int definitionsBefore = await Db.WorkflowDefinitions.CountAsync();

        ActionResult<WorkflowDefinitionResponse> put = await Controller(s.OwnerId).Update(
            s.Project.Id, s.Workflow.Id, new UpdateWorkflowRequest(null, NewSteps(s.Agent.Id)), CancellationToken.None);

        put.Result.Should().BeOfType<OkObjectResult>();
        (await Db.WorkflowDefinitions.CountAsync()).Should().Be(definitionsBefore);
        (await Db.WorkflowSteps.AnyAsync(st => st.Id == s.Step1.Id || st.Id == s.Step2.Id)).Should().BeFalse();
    }

    [DatabaseFact]
    public async Task Assistant_ApplyWorkflow_over_a_legacy_run_keeps_its_results_like_Put()
    {
        Seeded s = await SeedAsync();
        WorkflowExecution legacy = await AddRunAsync(s, s.Workflow.Id, ExecutionStatus.Failed, null, s.Step1, s.Step2);
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(Db, new FakeCurrentUser(s.OwnerId));
        IReadOnlyList<AIFunction> tools = provider.GetTools(new AssistantToolContext(s.OwnerId, s.Project.Id));
        List<CreateWorkflowStepRequest> steps = NewSteps(s.Agent.Id);

        string proposal = await InvokeAsync(tools, "ProposeWorkflow", ("workflowId", s.Workflow.Id), ("name", null), ("steps", steps));
        string token = JsonDocument.Parse(proposal).RootElement.GetProperty("confirmationToken").GetString()!;
        string applied = await InvokeAsync(tools, "ApplyWorkflow", ("workflowId", s.Workflow.Id), ("name", null), ("steps", steps), ("confirmationToken", token));

        JsonDocument.Parse(applied).RootElement.GetProperty("stepCount").GetInt32().Should().Be(1, applied);
        Db.ChangeTracker.Clear();
        await AssertLegacyRunSurvivedAsync(s, legacy);
    }

    [DatabaseFact]
    public async Task Assistant_controller_Apply_over_a_legacy_run_keeps_its_results_like_Put()
    {
        Seeded s = await SeedAsync();
        WorkflowExecution legacy = await AddRunAsync(s, s.Workflow.Id, ExecutionStatus.Passed, null, s.Step1, s.Step2);
        WorkflowAssistantController controller = AssistantController(s.OwnerId);
        List<CreateWorkflowStepRequest> steps = NewSteps(s.Agent.Id);

        ActionResult<WorkflowEditDiff> proposed = await controller.Propose(s.Project.Id, new ProposeWorkflowRequest(s.Workflow.Id, null, steps), CancellationToken.None);
        string token = ((WorkflowEditDiff)((OkObjectResult)proposed.Result!).Value!).ConfirmationToken;
        ActionResult<WorkflowAssistantApplyResponse> applied = await controller.Apply(
            s.Project.Id, new ApplyWorkflowRequest(s.Workflow.Id, null, steps, token), CancellationToken.None);

        applied.Result.Should().BeOfType<OkObjectResult>();
        Db.ChangeTracker.Clear();
        await AssertLegacyRunSurvivedAsync(s, legacy);
    }

    [DatabaseFact]
    public async Task Delete_while_a_run_is_Running_or_Queued_is_refused_with_409_and_writes_nothing()
    {
        Seeded s = await SeedAsync();
        await AddRunAsync(s, s.Workflow.Id, ExecutionStatus.Passed, null, s.Step1);
        WorkflowExecution running = await AddRunAsync(s, s.Workflow.Id, ExecutionStatus.Running, null, s.Step1);

        IActionResult deleted = await Controller(s.OwnerId).Delete(s.Project.Id, s.Workflow.Id, CancellationToken.None);

        ConflictObjectResult conflict = deleted.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value)).RootElement.GetProperty("code").GetString().Should().Be("WORKFLOW_RUN_ACTIVE");
        (await Db.WorkflowDefinitions.CountAsync()).Should().Be(1, "no archive was written");
        (await Db.WorkflowSteps.CountAsync(st => st.WorkflowDefinitionId == s.Workflow.Id)).Should().Be(2);
        (await Db.WorkflowExecutions.CountAsync(e => e.WorkflowDefinitionId == s.Workflow.Id && e.SnapshotDefinitionId == null)).Should().Be(2);

        await Db.WorkflowExecutions.Where(e => e.Id == running.Id).ExecuteUpdateAsync(x => x.SetProperty(e => e.Status, ExecutionStatus.Queued));
        (await Controller(s.OwnerId).Delete(s.Project.Id, s.Workflow.Id, CancellationToken.None)).Should().BeOfType<ConflictObjectResult>();
    }

    [DatabaseFact]
    public async Task Delete_with_past_runs_re_points_every_run_so_runs_and_results_survive_and_stay_readable()
    {
        Seeded s = await SeedAsync();
        (WorkflowDefinition snapshot, WorkflowStep snapshotStep) = await AddEngineSnapshotAsync(s);
        WorkflowExecution snapshotted = await AddRunAsync(s, s.Workflow.Id, ExecutionStatus.Passed, snapshot.Id, snapshotStep);
        await OwnSnapshotAsync(snapshot.Id, snapshotted.Id);
        WorkflowExecution legacy = await AddRunAsync(s, s.Workflow.Id, ExecutionStatus.Failed, null, s.Step1, s.Step2);
        WorkflowExecution neverStarted = await AddRunAsync(s, s.Workflow.Id, ExecutionStatus.Cancelled, null);

        IActionResult deleted = await Controller(s.OwnerId).Delete(s.Project.Id, s.Workflow.Id, CancellationToken.None);

        deleted.Should().BeOfType<NoContentResult>();
        Db.ChangeTracker.Clear();
        (await Db.WorkflowDefinitions.AnyAsync(w => w.Id == s.Workflow.Id)).Should().BeFalse();
        List<WorkflowExecution> runs = await Db.WorkflowExecutions.AsNoTracking().Where(e => e.ProjectId == s.Project.Id).ToListAsync();
        runs.Select(r => r.Id).Should().BeEquivalentTo(new[] { snapshotted.Id, legacy.Id, neverStarted.Id }, "no run is cascade-deleted with its workflow");
        runs.Should().AllSatisfy(r =>
        {
            r.SnapshotDefinitionId.Should().NotBeNull();
            r.WorkflowDefinitionId.Should().Be(r.SnapshotDefinitionId!.Value, "each run is filed under its own snapshot or the archive");
        });
        runs.Single(r => r.Id == snapshotted.Id).SnapshotDefinitionId.Should().Be(snapshot.Id, "an engine-taken snapshot is kept, not replaced");
        (await Db.WorkflowStepResults.CountAsync(r => r.WorkflowExecution.ProjectId == s.Project.Id)).Should().Be(3, "every result survives");

        // The execution detail still serves each re-pointed run, with the steps it ran.
        WorkflowsController controller = Controller(s.OwnerId);
        foreach (WorkflowExecution run in new[] { snapshotted, legacy, neverStarted })
        {
            ActionResult<WorkflowExecutionResponse> got = await controller.GetExecution(s.Project.Id, run.Id, CancellationToken.None);
            WorkflowExecutionResponse body = (WorkflowExecutionResponse)got.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;
            body.Steps.Should().NotBeNull();
            body.StepResults.Select(r => r.WorkflowStepId).Should().BeSubsetOf(body.Steps!.Select(x => x.Id));
            body.WorkflowName.Should().Be("Launch reel", "a run of a deleted workflow still shows the workflow's name");
        }

        // The project-wide run history (assistant, no project open) still lists them, under the source name.
        FakeCurrentUser user = new(s.OwnerId);
        AssistantWorkflowTools tools = new(Db, ConfirmationTokenService.CreateEphemeral(), TestTenancy.Access(Db, user), user);
        string listed = await InvokeAsync(tools.GetTools(new AssistantToolContext(s.OwnerId, null)), "ListWorkflowExecutions");
        listed.Should().Contain(snapshotted.Id.ToString()).And.Contain(legacy.Id.ToString()).And.Contain(neverStarted.Id.ToString());
        JsonDocument.Parse(listed).RootElement.EnumerateArray().Select(r => r.GetProperty("workflowName").GetString())
            .Should().AllBe("Launch reel", "never the snapshot's or the archive's system name");

        // The dashboard's recent runs too.
        ActionResult<DashboardOverviewResponse> overview = await new DashboardController(Db, user, TestTenancy.Access(Db, user)).Overview(CancellationToken.None);
        DashboardOverviewResponse dashboard = (DashboardOverviewResponse)((OkObjectResult)overview.Result!).Value!;
        dashboard.RecentExecutions.Select(r => r.Id).Should().BeEquivalentTo(new[] { snapshotted.Id, legacy.Id, neverStarted.Id });
        dashboard.RecentExecutions.Select(r => r.WorkflowName).Should().AllBe("Launch reel");

        // A re-pointed run can still be deleted on its own (its snapshot goes with it, nothing else does).
        await Db.WorkflowExecutions.Where(e => e.Id == snapshotted.Id).ExecuteDeleteAsync();
        (await Db.WorkflowExecutions.CountAsync(e => e.ProjectId == s.Project.Id)).Should().Be(2);
        (await Db.WorkflowStepResults.CountAsync(r => r.WorkflowExecutionId == legacy.Id)).Should().Be(2);
    }

    [DatabaseFact]
    public async Task Put_while_a_legacy_run_is_still_running_keeps_every_step_it_may_still_write_results_for()
    {
        Seeded s = await SeedAsync();
        WorkflowExecution live = await AddRunAsync(s, s.Workflow.Id, ExecutionStatus.Running, null, s.Step1);

        (await Controller(s.OwnerId).Update(s.Project.Id, s.Workflow.Id, new UpdateWorkflowRequest(null, NewSteps(s.Agent.Id)), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>();

        // The engine, still holding the old step ids in memory, writes step 2's result after the edit.
        Db.WorkflowStepResults.Add(new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = live.Id, WorkflowStepId = s.Step2.Id, Status = StepStatus.Completed, Output = "late", ExecutedAt = DateTime.UtcNow });
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();

        (await Db.WorkflowStepResults.CountAsync(r => r.WorkflowExecutionId == live.Id)).Should().Be(2);
        WorkflowExecution after = await Db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == live.Id);
        after.SnapshotDefinitionId.Should().NotBeNull();
        (await Db.WorkflowSteps.AsNoTracking().Where(st => st.WorkflowDefinitionId == after.SnapshotDefinitionId).Select(st => st.Id).ToListAsync())
            .Should().BeEquivalentTo(new[] { s.Step1.Id, s.Step2.Id }, "a running legacy run keeps all its steps, not only those with results so far");
    }

    [DatabaseFact]
    public async Task Archive_and_snapshot_rows_carry_the_plain_source_name()
    {
        Seeded s = await SeedAsync();
        await AddRunAsync(s, s.Workflow.Id, ExecutionStatus.Passed, null, s.Step1);

        await Controller(s.OwnerId).Update(s.Project.Id, s.Workflow.Id, new UpdateWorkflowRequest(null, NewSteps(s.Agent.Id)), CancellationToken.None);

        (await Db.WorkflowDefinitions.AsNoTracking().Where(w => w.Kind == WorkflowDefinitionKind.ExecutionSnapshot).Select(w => w.Name).ToListAsync())
            .Should().Equal(["Launch reel"]);
    }

    [DatabaseFact]
    public async Task Assistant_ApplyDeleteWorkflow_refuses_while_a_run_is_active_then_deletes_keeping_the_runs()
    {
        Seeded s = await SeedAsync();
        WorkflowExecution legacy = await AddRunAsync(s, s.Workflow.Id, ExecutionStatus.Passed, null, s.Step1, s.Step2);
        WorkflowExecution running = await AddRunAsync(s, s.Workflow.Id, ExecutionStatus.Running, null);
        FakeCurrentUser user = new(s.OwnerId);
        AssistantWorkflowTools workflowTools = new(Db, ConfirmationTokenService.CreateEphemeral(), TestTenancy.Access(Db, user), user);
        IReadOnlyList<AIFunction> tools = workflowTools.GetTools(new AssistantToolContext(s.OwnerId, s.Project.Id));

        string proposal = await InvokeAsync(tools, "ProposeDeleteWorkflow", ("workflowId", s.Workflow.Id));
        string token = JsonDocument.Parse(proposal).RootElement.GetProperty("confirmationToken").GetString()!;

        string refused = await InvokeAsync(tools, "ApplyDeleteWorkflow", ("workflowId", s.Workflow.Id), ("confirmationToken", token));
        JsonElement refusal = JsonDocument.Parse(refused).RootElement;
        refusal.GetProperty("code").GetString().Should().Be("WORKFLOW_RUN_ACTIVE", refused);
        refusal.TryGetProperty("deleted", out _).Should().BeFalse();
        Db.ChangeTracker.Clear();
        (await Db.WorkflowDefinitions.AnyAsync(w => w.Id == s.Workflow.Id)).Should().BeTrue("nothing was written");
        (await Db.WorkflowDefinitions.CountAsync()).Should().Be(1);

        await Db.WorkflowExecutions.Where(e => e.Id == running.Id).ExecuteUpdateAsync(x => x.SetProperty(e => e.Status, ExecutionStatus.Cancelled));
        string applied = await InvokeAsync(tools, "ApplyDeleteWorkflow", ("workflowId", s.Workflow.Id), ("confirmationToken", token));

        JsonDocument.Parse(applied).RootElement.GetProperty("deleted").GetBoolean().Should().BeTrue(applied);
        Db.ChangeTracker.Clear();
        (await Db.WorkflowDefinitions.AnyAsync(w => w.Id == s.Workflow.Id)).Should().BeFalse();
        (await Db.WorkflowExecutions.CountAsync(e => e.Id == legacy.Id || e.Id == running.Id)).Should().Be(2, "the delete tool keeps the runs too");
        (await Db.WorkflowStepResults.CountAsync(r => r.WorkflowExecutionId == legacy.Id)).Should().Be(2);
    }
}

/// <summary>The scenarios on Sqlite with <c>foreign_keys = ON</c>, so the cascades are real.</summary>
public sealed class WorkflowStepReplacerTests : WorkflowStepReplacerTestsBase
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public WorkflowStepReplacerTests()
    {
        _connection.Open();
        using SqliteCommand pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
    }

    protected override Action<DbContextOptionsBuilder>? Configure => o => o.UseSqlite(_connection);

    protected override Task CreateSchemaAsync()
    {
        using (InferenceApiDbContext api = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options))
        {
            api.Database.EnsureCreated();
        }

        using WorkflowEngineDbContext engine = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_connection).Options);
        engine.GetService<IRelationalDatabaseCreator>().CreateTables();
        return Task.CompletedTask;
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        _connection.Dispose();
    }
}

/// <summary>The same scenarios on a real Postgres migrated by both contexts (real FKs, cascades, ON DELETE SET NULL).</summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
[RequiresPostgres]
public sealed class WorkflowStepReplacerPostgresTests : WorkflowStepReplacerTestsBase
{
    private static readonly string? BaseConnection = PostgresTestEnvironment.BaseConnectionString();
    private readonly string? _connectionString;
    private readonly string _dbName = "reelbolt_t002_test_" + Guid.NewGuid().ToString("N")[..12];

    public WorkflowStepReplacerPostgresTests()
    {
        if (string.IsNullOrWhiteSpace(BaseConnection)) return;
        _connectionString = PostgresTestEnvironment.ForDatabase(BaseConnection, _dbName);
    }

    protected override Action<DbContextOptionsBuilder>? Configure
        => _connectionString is null ? null : o => o.UseNpgsql(_connectionString);

    protected override async Task CreateSchemaAsync()
    {
        await using (NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(BaseConnection!))
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {_dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using (InferenceApiDbContext api = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(_connectionString).Options))
        {
            await api.GetService<IMigrator>().MigrateAsync();
        }

        await using WorkflowEngineDbContext engine = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(_connectionString).Options);
        await engine.GetService<IMigrator>().MigrateAsync();
    }

    /// <summary>Waits until some backend of this test database is blocked on a lock (the call under test is waiting).</summary>
    private async Task WaitUntilALockIsAwaitedAsync()
    {
        await using NpgsqlConnection observer = new(_connectionString);
        await observer.OpenAsync();
        for (int i = 0; i < 200; i++)
        {
            await using NpgsqlCommand waiting = new("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", observer);
            if ((long)(await waiting.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(50);
        }

        throw new TimeoutException("the call under test never waited on a lock");
    }

    /// <summary>
    /// The DELETE race: a run being queued (its insert not yet committed) while the workflow is deleted. The
    /// insert's FK check holds FOR KEY SHARE on the workflow row, so the delete's FOR UPDATE waits for it, then
    /// sees the run and refuses; without the lock the delete's cascade would take the new run with it.
    /// </summary>
    [PostgresFact]
    public async Task Delete_racing_a_run_being_queued_waits_for_it_and_refuses()
    {
        Seeded s = await SeedAsync();
        // Another request queueing a run: its insert is written but not yet committed.
        await using InferenceApiDbContext queuer = NewDb();
        await using var queueing = await queuer.Database.BeginTransactionAsync();
        Guid queued = Guid.NewGuid();
        queuer.WorkflowExecutions.Add(new WorkflowExecution { Id = queued, WorkflowDefinitionId = s.Workflow.Id, ProjectId = s.Project.Id, OrganizationId = s.Project.OrganizationId, Status = ExecutionStatus.Queued });
        await queuer.SaveChangesAsync();

        Task<IActionResult> delete = Controller(s.OwnerId).Delete(s.Project.Id, s.Workflow.Id, CancellationToken.None);
        await WaitUntilALockIsAwaitedAsync();
        await queueing.CommitAsync();

        (await delete).Should().BeOfType<ConflictObjectResult>("the run queued meanwhile is active");
        Db.ChangeTracker.Clear();
        (await Db.WorkflowExecutions.AnyAsync(e => e.Id == queued)).Should().BeTrue();
        (await Db.WorkflowDefinitions.AnyAsync(w => w.Id == s.Workflow.Id)).Should().BeTrue();
    }

    /// <summary>
    /// The PUT race: a legacy run's result for step 2 whose insert is not yet committed when the steps are
    /// replaced. The insert's FK check holds FOR KEY SHARE on the step row, so the replacer's FOR UPDATE waits
    /// for it and then archives step 2 with its new result; without the lock the step delete would wait, then
    /// cascade-delete the result the moment it committed.
    /// </summary>
    [PostgresFact]
    public async Task Put_racing_an_uncommitted_result_waits_for_it_and_keeps_it()
    {
        Seeded s = await SeedAsync();
        WorkflowExecution legacy = await AddRunAsync(s, s.Workflow.Id, ExecutionStatus.Failed, null, s.Step1);
        // The engine writing that run's step 2 result: written but not yet committed.
        await using InferenceApiDbContext engine = NewDb();
        await using var writing = await engine.Database.BeginTransactionAsync();
        Guid late = Guid.NewGuid();
        engine.WorkflowStepResults.Add(new WorkflowStepResult { Id = late, WorkflowExecutionId = legacy.Id, WorkflowStepId = s.Step2.Id, Status = StepStatus.Completed, Output = "late", ExecutedAt = DateTime.UtcNow });
        await engine.SaveChangesAsync();

        Task<ActionResult<WorkflowDefinitionResponse>> put = Controller(s.OwnerId).Update(
            s.Project.Id, s.Workflow.Id, new UpdateWorkflowRequest(null, NewSteps(s.Agent.Id)), CancellationToken.None);
        await WaitUntilALockIsAwaitedAsync();
        await writing.CommitAsync();

        (await put).Result.Should().BeOfType<OkObjectResult>();
        Db.ChangeTracker.Clear();
        (await Db.WorkflowStepResults.AnyAsync(r => r.Id == late)).Should().BeTrue("a result committed during the edit survives it");
        (await Db.WorkflowSteps.AsNoTracking().SingleAsync(st => st.Id == s.Step2.Id)).WorkflowDefinitionId.Should().NotBe(s.Workflow.Id);
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (_connectionString is null) return;
        NpgsqlConnection.ClearAllPools();
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(BaseConnection!);
        await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }
}
