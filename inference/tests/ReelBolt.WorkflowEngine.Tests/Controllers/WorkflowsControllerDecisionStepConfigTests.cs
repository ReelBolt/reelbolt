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
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// The decision-engine phase-2 step configs — <see cref="WorkflowStep.ConditionalConfigJson"/> and
/// <see cref="WorkflowStep.ReviewLoopConfigJson"/> — are mapped by the DbContext but were absent from
/// every step DTO and from the controller's create/update mapping, so no client could set them.
/// These tests go through the real controller mapping rather than constructing a DTO, so a
/// declared-but-unmapped field fails here instead of shipping.
///
/// <para>
/// The no-config cases are deliberate negative-space assertions: unlike the six config-bearing step
/// types, a Conditional or ReviewLoop step with no config is a <b>supported state</b> (today's exact
/// expression path, and today's exact reviewer path), so the save validator must keep accepting it.
/// </para>
///
/// <para>
/// Uses SQLite rather than InMemory because Update relies on ExecuteDeleteAsync, which the InMemory
/// provider does not support.
/// </para>
/// </summary>
public sealed class WorkflowsControllerDecisionStepConfigTests : IDisposable
{
    private const string ConditionalInitialConfig =
        """{"version":1,"mode":"Decision","decisionQuestion":"Is the branch ready?","threshold":0.6,"timeoutSeconds":5}""";

    private const string ConditionalUpdatedConfig =
        """{"version":1,"mode":"Decision","decisionQuestion":"Ship it?","threshold":0.9,"timeoutSeconds":8}""";

    // Same values as ConditionalInitialConfig, only reformatted and reordered — Postgres jsonb
    // normalizes both on store, so this is not a change.
    private const string ConditionalReformattedConfig =
        """{ "timeoutSeconds": 5, "threshold": 0.6, "decisionQuestion": "Is the branch ready?", "mode": "Decision", "version": 1 }""";

    private const string ReviewLoopInitialConfig =
        """{"version":1,"enabled":true,"acceptAt":0.95,"bimodalMargin":0.2,"timeoutSeconds":5}""";

    private const string ReviewLoopUpdatedConfig =
        """{"version":1,"enabled":true,"acceptAt":0.75,"bimodalMargin":0.3,"timeoutSeconds":10}""";

    private const string ReviewLoopReformattedConfig =
        """{ "timeoutSeconds": 5, "bimodalMargin": 0.2, "acceptAt": 0.95, "enabled": true, "version": 1 }""";

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;

    public WorkflowsControllerDecisionStepConfigTests()
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

        // The workflow_* tables are ExcludeFromMigrations in the API context; create them from the
        // engine's model (which owns them) on the same connection.
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
        public System.Guid? OrganizationId => ReelBolt.WorkflowEngine.Tests.TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private WorkflowsController CreateController(Guid ownerId)
    {
        WorkflowTemplateProvisioningService templateService = new(
            _db,
            NullLogger<WorkflowTemplateProvisioningService>.Instance,
            new ConfigurationBuilder().Build());

        IWorkflowExecutionService executionService = new WorkflowExecutionService(
            _db,
            Mock.Of<IPublishEndpoint>(),
            NullLogger<WorkflowExecutionService>.Instance);

        return new WorkflowsController(
            _db,
            new FakeCurrentUser(ownerId), TestTenancy.Access(_db, new FakeCurrentUser(ownerId)),
            Mock.Of<IPublishEndpoint>(),
            templateService,
            executionService,
            NullLogger<WorkflowsController>.Instance);
    }

    private async Task<(Guid ownerId, Project project, AgentDefinition agent)> SeedAsync()
    {
        Guid ownerId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "VideoTransform",
            Description = "d",
            SystemPrompt = "",
            AgentType = AgentType.VideoTransform,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Projects.Add(project);
        _db.AgentDefinitions.Add(agent);
        await _db.SaveChangesAsync();
        return (ownerId, project, agent);
    }

    private static void ShouldBeSameJson(string? actual, string expected)
    {
        actual.Should().NotBeNullOrWhiteSpace();
        JsonNode.DeepEquals(JsonNode.Parse(actual!), JsonNode.Parse(expected)).Should().BeTrue(
            because: $"expected config JSON {expected} but got {actual}");
    }

    [Fact]
    public async Task ConditionalConfigJson_survives_create_read_update_read()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        WorkflowsController controller = CreateController(ownerId);

        CreateWorkflowStepRequest stepReq = new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Decide",
            StepType: nameof(StepType.Conditional), ConditionalConfigJson: ConditionalInitialConfig);

        ActionResult<WorkflowDefinitionResponse> createResult = await controller.Create(
            project.Id, new CreateWorkflowRequest("wf", new List<CreateWorkflowStepRequest> { stepReq }), CancellationToken.None);
        ((ObjectResult)createResult.Result!).StatusCode.Should().Be(201);
        var created = ((ObjectResult)createResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        created.Steps.Single().StepType.Should().Be(nameof(StepType.Conditional));
        ShouldBeSameJson(created.Steps.Single().ConditionalConfigJson, ConditionalInitialConfig);

        _db.ChangeTracker.Clear();
        ActionResult<WorkflowDefinitionResponse> getResult = await controller.Get(project.Id, created.Id, CancellationToken.None);
        var fetched = ((OkObjectResult)getResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        ShouldBeSameJson(fetched.Steps.Single().ConditionalConfigJson, ConditionalInitialConfig);

        _db.ChangeTracker.Clear();
        CreateWorkflowStepRequest replacement = stepReq with { ConditionalConfigJson = ConditionalUpdatedConfig };
        ActionResult<WorkflowDefinitionResponse> updateResult = await controller.Update(
            project.Id, created.Id, new UpdateWorkflowRequest(null, new List<CreateWorkflowStepRequest> { replacement }), CancellationToken.None);
        var updated = ((OkObjectResult)updateResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        ShouldBeSameJson(updated.Steps.Single().ConditionalConfigJson, ConditionalUpdatedConfig);

        _db.ChangeTracker.Clear();
        WorkflowStep persisted = await _db.WorkflowSteps.AsNoTracking().SingleAsync(s => s.WorkflowDefinitionId == created.Id);
        ShouldBeSameJson(persisted.ConditionalConfigJson, ConditionalUpdatedConfig);

        ActionResult<WorkflowDefinitionResponse> getAgainResult = await controller.Get(project.Id, created.Id, CancellationToken.None);
        var fetchedAgain = ((OkObjectResult)getAgainResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        ShouldBeSameJson(fetchedAgain.Steps.Single().ConditionalConfigJson, ConditionalUpdatedConfig);
    }

    [Fact]
    public async Task ReviewLoopConfigJson_survives_create_read_update_read()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        WorkflowsController controller = CreateController(ownerId);

        CreateWorkflowStepRequest stepReq = new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Review",
            StepType: nameof(StepType.ReviewLoop), ReviewLoopConfigJson: ReviewLoopInitialConfig);

        ActionResult<WorkflowDefinitionResponse> createResult = await controller.Create(
            project.Id, new CreateWorkflowRequest("wf", new List<CreateWorkflowStepRequest> { stepReq }), CancellationToken.None);
        ((ObjectResult)createResult.Result!).StatusCode.Should().Be(201);
        var created = ((ObjectResult)createResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        created.Steps.Single().StepType.Should().Be(nameof(StepType.ReviewLoop));
        ShouldBeSameJson(created.Steps.Single().ReviewLoopConfigJson, ReviewLoopInitialConfig);

        _db.ChangeTracker.Clear();
        ActionResult<WorkflowDefinitionResponse> getResult = await controller.Get(project.Id, created.Id, CancellationToken.None);
        var fetched = ((OkObjectResult)getResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        ShouldBeSameJson(fetched.Steps.Single().ReviewLoopConfigJson, ReviewLoopInitialConfig);

        _db.ChangeTracker.Clear();
        CreateWorkflowStepRequest replacement = stepReq with { ReviewLoopConfigJson = ReviewLoopUpdatedConfig };
        ActionResult<WorkflowDefinitionResponse> updateResult = await controller.Update(
            project.Id, created.Id, new UpdateWorkflowRequest(null, new List<CreateWorkflowStepRequest> { replacement }), CancellationToken.None);
        var updated = ((OkObjectResult)updateResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        ShouldBeSameJson(updated.Steps.Single().ReviewLoopConfigJson, ReviewLoopUpdatedConfig);

        _db.ChangeTracker.Clear();
        WorkflowStep persisted = await _db.WorkflowSteps.AsNoTracking().SingleAsync(s => s.WorkflowDefinitionId == created.Id);
        ShouldBeSameJson(persisted.ReviewLoopConfigJson, ReviewLoopUpdatedConfig);

        ActionResult<WorkflowDefinitionResponse> getAgainResult = await controller.Get(project.Id, created.Id, CancellationToken.None);
        var fetchedAgain = ((OkObjectResult)getAgainResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        ShouldBeSameJson(fetchedAgain.Steps.Single().ReviewLoopConfigJson, ReviewLoopUpdatedConfig);
    }

    [Fact]
    public async Task Conditional_step_with_no_config_saves()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        WorkflowsController controller = CreateController(ownerId);

        // No ConditionalConfigJson and no ConditionExpression: ConditionMode.Expression over a null
        // expression is a supported, working state — the validator must not demand a config here.
        CreateWorkflowStepRequest stepReq = new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Decide",
            StepType: nameof(StepType.Conditional), ConditionExpression: null);

        ActionResult<WorkflowDefinitionResponse> result = await controller.Create(
            project.Id, new CreateWorkflowRequest("wf", new List<CreateWorkflowStepRequest> { stepReq }), CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
        var created = ((ObjectResult)result.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        created.Steps.Single().ConditionalConfigJson.Should().BeNull();

        _db.ChangeTracker.Clear();
        WorkflowStep persisted = await _db.WorkflowSteps.AsNoTracking().SingleAsync(s => s.WorkflowDefinitionId == created.Id);
        persisted.ConditionalConfigJson.Should().BeNull();
    }

    [Fact]
    public async Task ReviewLoop_step_with_no_config_saves()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        WorkflowsController controller = CreateController(ownerId);

        // No ReviewLoopConfigJson: a disabled/absent gate takes today's exact reviewer path.
        CreateWorkflowStepRequest stepReq = new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Review",
            StepType: nameof(StepType.ReviewLoop));

        ActionResult<WorkflowDefinitionResponse> result = await controller.Create(
            project.Id, new CreateWorkflowRequest("wf", new List<CreateWorkflowStepRequest> { stepReq }), CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
        var created = ((ObjectResult)result.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        created.Steps.Single().ReviewLoopConfigJson.Should().BeNull();

        _db.ChangeTracker.Clear();
        WorkflowStep persisted = await _db.WorkflowSteps.AsNoTracking().SingleAsync(s => s.WorkflowDefinitionId == created.Id);
        persisted.ReviewLoopConfigJson.Should().BeNull();
    }

    private static InferenceApiDbContext CreateDiffContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static WorkflowStep CurrentConditionalStep(Guid workflowId, Guid agentId, string config) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = workflowId,
        AgentDefinitionId = agentId,
        StepOrder = 1,
        Label = "Decide",
        StepType = StepType.Conditional,
        ConditionalConfigJson = config
    };

    private static WorkflowStep CurrentReviewLoopStep(Guid workflowId, Guid agentId, string config) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = workflowId,
        AgentDefinitionId = agentId,
        StepOrder = 1,
        Label = "Review",
        StepType = StepType.ReviewLoop,
        ReviewLoopConfigJson = config
    };

    [Fact]
    public void ComputeDiff_treats_semantically_identical_ConditionalConfigJson_as_unchanged()
    {
        using InferenceApiDbContext db = CreateDiffContext();
        WorkflowEditDiffService service = new(db);
        Guid workflowId = Guid.NewGuid();
        Guid agentId = Guid.NewGuid();

        var current = new List<WorkflowStep> { CurrentConditionalStep(workflowId, agentId, ConditionalInitialConfig) };
        var proposed = new List<CreateWorkflowStepRequest>
        {
            new(agentId, 1, null, "Decide", nameof(StepType.Conditional),
                ConditionalConfigJson: ConditionalReformattedConfig)
        };

        WorkflowEditDiff diff = service.ComputeDiff(workflowId, current, proposed);

        diff.Modifications.Should().BeEmpty();
    }

    [Fact]
    public void ComputeDiff_reports_a_change_to_only_ConditionalConfigJson_as_a_modification()
    {
        using InferenceApiDbContext db = CreateDiffContext();
        WorkflowEditDiffService service = new(db);
        Guid workflowId = Guid.NewGuid();
        Guid agentId = Guid.NewGuid();

        var current = new List<WorkflowStep> { CurrentConditionalStep(workflowId, agentId, ConditionalInitialConfig) };
        var proposed = new List<CreateWorkflowStepRequest>
        {
            new(agentId, 1, null, "Decide", nameof(StepType.Conditional),
                ConditionalConfigJson: ConditionalUpdatedConfig)
        };

        WorkflowEditDiff diff = service.ComputeDiff(workflowId, current, proposed);

        diff.Modifications.Should().ContainSingle().Which.StepOrder.Should().Be(1);
        diff.Additions.Should().BeEmpty();
        diff.Removals.Should().BeEmpty();
    }

    [Fact]
    public void ComputeDiff_treats_semantically_identical_ReviewLoopConfigJson_as_unchanged()
    {
        using InferenceApiDbContext db = CreateDiffContext();
        WorkflowEditDiffService service = new(db);
        Guid workflowId = Guid.NewGuid();
        Guid agentId = Guid.NewGuid();

        var current = new List<WorkflowStep> { CurrentReviewLoopStep(workflowId, agentId, ReviewLoopInitialConfig) };
        var proposed = new List<CreateWorkflowStepRequest>
        {
            new(agentId, 1, null, "Review", nameof(StepType.ReviewLoop),
                ReviewLoopConfigJson: ReviewLoopReformattedConfig)
        };

        WorkflowEditDiff diff = service.ComputeDiff(workflowId, current, proposed);

        diff.Modifications.Should().BeEmpty();
    }

    [Fact]
    public void ComputeDiff_reports_a_change_to_only_ReviewLoopConfigJson_as_a_modification()
    {
        using InferenceApiDbContext db = CreateDiffContext();
        WorkflowEditDiffService service = new(db);
        Guid workflowId = Guid.NewGuid();
        Guid agentId = Guid.NewGuid();

        var current = new List<WorkflowStep> { CurrentReviewLoopStep(workflowId, agentId, ReviewLoopInitialConfig) };
        var proposed = new List<CreateWorkflowStepRequest>
        {
            new(agentId, 1, null, "Review", nameof(StepType.ReviewLoop),
                ReviewLoopConfigJson: ReviewLoopUpdatedConfig)
        };

        WorkflowEditDiff diff = service.ComputeDiff(workflowId, current, proposed);

        diff.Modifications.Should().ContainSingle().Which.StepOrder.Should().Be(1);
        diff.Additions.Should().BeEmpty();
        diff.Removals.Should().BeEmpty();
    }

    [Fact]
    public void BuildStep_applies_ConditionalConfigJson()
    {
        using InferenceApiDbContext db = CreateDiffContext();
        WorkflowEditDiffService service = new(db);

        CreateWorkflowStepRequest req = new(Guid.NewGuid(), 1, null, "Decide", nameof(StepType.Conditional),
            ConditionalConfigJson: ConditionalUpdatedConfig);

        WorkflowStep step = service.BuildStep(Guid.NewGuid(), req);

        step.StepType.Should().Be(StepType.Conditional);
        JsonNode.DeepEquals(JsonNode.Parse(step.ConditionalConfigJson!), JsonNode.Parse(ConditionalUpdatedConfig)).Should().BeTrue();
    }

    [Fact]
    public void BuildStep_applies_ReviewLoopConfigJson()
    {
        using InferenceApiDbContext db = CreateDiffContext();
        WorkflowEditDiffService service = new(db);

        CreateWorkflowStepRequest req = new(Guid.NewGuid(), 1, null, "Review", nameof(StepType.ReviewLoop),
            ReviewLoopConfigJson: ReviewLoopUpdatedConfig);

        WorkflowStep step = service.BuildStep(Guid.NewGuid(), req);

        step.StepType.Should().Be(StepType.ReviewLoop);
        JsonNode.DeepEquals(JsonNode.Parse(step.ReviewLoopConfigJson!), JsonNode.Parse(ReviewLoopUpdatedConfig)).Should().BeTrue();
    }
}
