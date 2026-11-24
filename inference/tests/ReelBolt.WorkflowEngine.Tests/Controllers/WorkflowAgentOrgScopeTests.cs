using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
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

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// WP A8, save half: a workflow may only reference a built-in agent or a custom agent belonging to
/// the project's organization. A cross-organization custom agent is refused when the workflow is
/// saved through either the REST controller or the assistant's apply path, and
/// <see cref="WorkflowEditDiffService.ValidateProposedSteps"/> reports it so a validate-then-apply
/// round trip cannot disagree with the write.
///
/// <para>
/// The engine's run-time half lives in
/// <see cref="ReelBolt.WorkflowEngine.Tests.AgentStepExecutorOrganizationScopeTests"/>.
/// </para>
/// </summary>
public sealed class WorkflowAgentOrgScopeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;

    public WorkflowAgentOrgScopeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
        }

        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(_connection)
            .Options);
        _db.Database.EnsureCreated();

        using var engineDb = new ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext(
            new DbContextOptionsBuilder<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>()
                .UseSqlite(_connection)
                .Options);
        engineDb.GetService<IRelationalDatabaseCreator>().CreateTables();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = true;
        public Guid? OrganizationId => ReelBolt.WorkflowEngine.Tests.TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Member;
    }

    private static Guid Org(Guid userId) => ReelBolt.WorkflowEngine.Tests.TestTenancy.OrgOf(userId);

    private WorkflowsController CreateWorkflowsController(Guid ownerId)
    {
        WorkflowTemplateProvisioningService templateService = new(
            _db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build());

        IWorkflowExecutionService executionService = new WorkflowExecutionService(
            _db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance);

        return new WorkflowsController(
            _db,
            new FakeCurrentUser(ownerId),
            TestTenancy.Access(_db, new FakeCurrentUser(ownerId)),
            Mock.Of<IPublishEndpoint>(),
            templateService,
            executionService,
            NullLogger<WorkflowsController>.Instance);
    }

    private WorkflowAssistantController CreateAssistantController(Guid ownerId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "Assistant:MaxExecutionsPerHour", "10" } })
            .Build();

        return new WorkflowAssistantController(
            _db,
            new FakeCurrentUser(ownerId),
            TestTenancy.Access(_db, new FakeCurrentUser(ownerId)),
            new WorkflowEditDiffService(_db),
            new AssistantExecutionGuard(_db, config),
            new WorkflowExecutionService(_db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance));
    }

    private async Task<(Guid ownerId, Project project, AgentDefinition mine, AgentDefinition theirs)> SeedAsync()
    {
        Guid ownerId = Guid.NewGuid();
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "p",
            OwnerId = ownerId,
            OrganizationId = Org(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        AgentDefinition mine = CustomAgent(Org(ownerId), ownerId, "Mine");
        AgentDefinition theirs = CustomAgent(Org(Guid.NewGuid()), Guid.NewGuid(), "Theirs");

        _db.Projects.Add(project);
        _db.AgentDefinitions.AddRange(mine, theirs);
        await _db.SaveChangesAsync();
        return (ownerId, project, mine, theirs);
    }

    private static AgentDefinition CustomAgent(Guid organizationId, Guid ownerId, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = "d",
        SystemPrompt = "p",
        AgentType = AgentType.Custom,
        IsBuiltIn = false,
        OwnerId = ownerId,
        OrganizationId = organizationId,
        CreatedAt = DateTime.UtcNow
    };

    private static List<CreateWorkflowStepRequest> Steps(Guid agentId) =>
        [new(agentId, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent")];

    [Fact]
    public async Task Create_refuses_a_custom_agent_from_another_organization()
    {
        (Guid ownerId, Project project, _, AgentDefinition theirs) = await SeedAsync();
        WorkflowsController controller = CreateWorkflowsController(ownerId);

        ActionResult<WorkflowDefinitionResponse> result = await controller.Create(
            project.Id, new CreateWorkflowRequest("wf", Steps(theirs.Id)), CancellationToken.None);

        BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("Invalid or unauthorized agent");
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0, "nothing may be persisted for a refused save");
    }

    [Fact]
    public async Task Create_accepts_a_custom_agent_from_the_projects_organization()
    {
        (Guid ownerId, Project project, AgentDefinition mine, _) = await SeedAsync();
        WorkflowsController controller = CreateWorkflowsController(ownerId);

        ActionResult<WorkflowDefinitionResponse> result = await controller.Create(
            project.Id, new CreateWorkflowRequest("wf", Steps(mine.Id)), CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task Update_refuses_a_custom_agent_from_another_organization()
    {
        (Guid ownerId, Project project, AgentDefinition mine, AgentDefinition theirs) = await SeedAsync();
        WorkflowsController controller = CreateWorkflowsController(ownerId);

        ActionResult<WorkflowDefinitionResponse> created = await controller.Create(
            project.Id, new CreateWorkflowRequest("wf", Steps(mine.Id)), CancellationToken.None);
        var workflow = ((ObjectResult)created.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;

        ActionResult<WorkflowDefinitionResponse> result = await controller.Update(
            project.Id, workflow.Id, new UpdateWorkflowRequest("wf2", Steps(theirs.Id)), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _db.ChangeTracker.Clear();
        (await _db.WorkflowSteps.CountAsync(s => s.WorkflowDefinitionId == workflow.Id)).Should().Be(1);
    }

    [Fact]
    public void ValidateProposedSteps_reports_a_custom_agent_from_another_organization()
    {
        Guid ownerId = Guid.NewGuid();
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "p",
            OwnerId = ownerId,
            OrganizationId = Org(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        AgentDefinition theirs = CustomAgent(Org(Guid.NewGuid()), Guid.NewGuid(), "Theirs");
        _db.Projects.Add(project);
        _db.AgentDefinitions.Add(theirs);
        _db.SaveChanges();

        WorkflowValidationResult result = new WorkflowEditDiffService(_db)
            .ValidateProposedSteps(project.Id, Steps(theirs.Id));

        result.Valid.Should().BeFalse();
        result.Errors.Should().ContainMatch("*does not belong to this project's organization*");
    }

    [Fact]
    public async Task Apply_refuses_a_custom_agent_from_another_organization()
    {
        (Guid ownerId, Project project, _, AgentDefinition theirs) = await SeedAsync();
        WorkflowAssistantController controller = CreateAssistantController(ownerId);
        List<CreateWorkflowStepRequest> steps = Steps(theirs.Id);

        // SQFU-S7: the propose step refuses it too, so no diff (and no token) is ever handed out for it.
        ActionResult<WorkflowEditDiff> proposed = await controller.Propose(
            project.Id, new ProposeWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps), CancellationToken.None);
        proposed.Result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value!.ToString().Should().Contain("Invalid or unauthorized agent");

        // Apply refuses on its own as well, given a token computed exactly as a proposal would.
        string token = new WorkflowEditDiffService(_db).ComputeDiff(null, [], steps).ConfirmationToken;
        ActionResult<WorkflowAssistantApplyResponse> result = await controller.Apply(
            project.Id,
            new ApplyWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps, ConfirmationToken: token),
            CancellationToken.None);

        BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("Invalid or unauthorized agent");
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Apply_accepts_a_custom_agent_from_the_projects_organization()
    {
        (Guid ownerId, Project project, AgentDefinition mine, _) = await SeedAsync();
        WorkflowAssistantController controller = CreateAssistantController(ownerId);
        List<CreateWorkflowStepRequest> steps = Steps(mine.Id);

        ActionResult<WorkflowEditDiff> proposed = await controller.Propose(
            project.Id, new ProposeWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps), CancellationToken.None);
        var diff = ((OkObjectResult)proposed.Result!).Value.Should().BeOfType<WorkflowEditDiff>().Subject;

        ActionResult<WorkflowAssistantApplyResponse> result = await controller.Apply(
            project.Id,
            new ApplyWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps, ConfirmationToken: diff.ConfirmationToken),
            CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(1);
    }
}
