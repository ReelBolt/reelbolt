using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// A custom agent that a past run's ExecutionSnapshot (or a legacy archive) uses is still referenced by
/// those kind-5 steps (<c>workflow_steps.agent_definition_id</c> is ON DELETE RESTRICT), so deleting it
/// used to fail inside the database with an unexplained error. It is now refused up front with
/// <c>409 AGENT_IN_USE_BY_RUNS</c> and the number of runs that keep it; the run, its steps and its
/// results are untouched. An agent that only Standard steps use behaves exactly as before.
/// SQLite with foreign keys ON, so the RESTRICT is the real one.
/// </summary>
public sealed class AgentDeleteInUseByRunsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly InferenceApiDbContext _db;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _org = Guid.NewGuid();

    public AgentDeleteInUseByRunsTests()
    {
        _connection.Open();
        Exec("PRAGMA foreign_keys = OFF;");
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        using var engine = new ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext(
            new DbContextOptionsBuilder<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>().UseSqlite(_connection).Options);
        engine.GetService<IRelationalDatabaseCreator>().CreateTables();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void Exec(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class Owner(Guid userId, Guid organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "owner@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = OrganizationRole.Owner;
    }

    private AgentsController Controller()
    {
        Owner user = new(_owner, _org);
        return new AgentsController(_db, user, new ReelBolt.Inference.Api.Services.Auth.ProjectAccessService(_db, user));
    }

    private sealed record Seeded(AgentDefinition Agent, WorkflowDefinition Workflow, WorkflowExecution Run, WorkflowDefinition? Snapshot);

    /// <summary>
    /// A project with one workflow on <c>agent</c>; with <paramref name="snapshotted"/> a run of it executed
    /// from its own kind-5 snapshot (whose step uses the agent too) with a result on the clone.
    /// </summary>
    private async Task<Seeded> SeedAsync(bool snapshotted, bool keepStandardStep = true)
    {
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = _owner, OrganizationId = _org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "Writer", Description = "d", SystemPrompt = "p", AgentType = AgentType.Custom, OwnerId = _owner, OrganizationId = _org, CreatedAt = DateTime.UtcNow };
        AgentDefinition other = new() { Id = Guid.NewGuid(), Name = "Other", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Launch reel", ProjectId = project.Id };
        // The user's workflow uses the agent, or (after an edit) another one.
        workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = keepStandardStep ? agent.Id : other.Id, StepOrder = 1, StepType = StepType.Agent });
        WorkflowExecution run = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id, OrganizationId = _org, Status = ExecutionStatus.Passed };
        _db.Projects.Add(project);
        _db.AgentDefinitions.AddRange(agent, other);
        _db.WorkflowDefinitions.Add(workflow);
        _db.WorkflowExecutions.Add(run);

        WorkflowDefinition? snapshot = null;
        if (snapshotted)
        {
            snapshot = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Launch reel (run snapshot)", ProjectId = project.Id, Kind = WorkflowDefinitionKind.ExecutionSnapshot };
            WorkflowStep clone = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = snapshot.Id, AgentDefinitionId = agent.Id, StepOrder = 1, StepType = StepType.Agent };
            snapshot.Steps.Add(clone);
            _db.WorkflowDefinitions.Add(snapshot);
            run.SnapshotDefinitionId = snapshot.Id;
            _db.WorkflowStepResults.Add(new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = run.Id, WorkflowStepId = clone.Id, Status = StepStatus.Completed, Output = "{}", ExecutedAt = DateTime.UtcNow });
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        Exec("PRAGMA foreign_keys = ON;");
        return new Seeded(agent, workflow, run, snapshot);
    }

    [Fact]
    public async Task An_agent_a_past_runs_snapshot_uses_is_refused_with_409_naming_the_runs_and_the_run_is_intact()
    {
        Seeded s = await SeedAsync(snapshotted: true, keepStandardStep: false);

        IActionResult result = await Controller().Delete(s.Agent.Id, CancellationToken.None);

        ConflictObjectResult conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        string body = System.Text.Json.JsonSerializer.Serialize(conflict.Value);
        body.Should().Contain("\"code\":\"AGENT_IN_USE_BY_RUNS\"").And.Contain("\"runCount\":1");
        _db.ChangeTracker.Clear();
        (await _db.AgentDefinitions.AnyAsync(a => a.Id == s.Agent.Id)).Should().BeTrue();
        (await _db.WorkflowSteps.CountAsync(x => x.WorkflowDefinitionId == s.Snapshot!.Id)).Should().Be(1, "the run keeps the steps it executed");
        (await _db.WorkflowStepResults.CountAsync(r => r.WorkflowExecutionId == s.Run.Id)).Should().Be(1);
    }

    [Fact]
    public async Task An_agent_only_standard_steps_use_is_protected_exactly_as_before()
    {
        Seeded s = await SeedAsync(snapshotted: false);

        Func<Task> act = () => Controller().Delete(s.Agent.Id, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>("the RESTRICT from a Standard step is unchanged");
        _db.ChangeTracker.Clear();
        (await _db.AgentDefinitions.AnyAsync(a => a.Id == s.Agent.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task An_agent_nothing_uses_is_deleted()
    {
        Seeded s = await SeedAsync(snapshotted: false, keepStandardStep: false);

        IActionResult result = await Controller().Delete(s.Agent.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _db.ChangeTracker.Clear();
        (await _db.AgentDefinitions.AnyAsync(a => a.Id == s.Agent.Id)).Should().BeFalse();
    }
}
