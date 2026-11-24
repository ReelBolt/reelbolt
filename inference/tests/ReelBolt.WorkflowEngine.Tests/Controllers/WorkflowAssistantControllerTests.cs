using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
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
/// Tests for WorkflowAssistantController covering Validate, Propose, and Apply endpoints
/// with both create (WorkflowId: null) and edit (WorkflowId set) request shapes.
/// </summary>
public class WorkflowAssistantControllerTests
{
    private static InferenceApiDbContext CreateContext()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InferenceApiDbContext(options);
    }

    internal static (Microsoft.Data.Sqlite.SqliteConnection connection, InferenceApiDbContext context) CreateSqliteContext()
    {
        // Create SQLite in-memory database that supports ExecuteDeleteAsync
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();

        // Disable foreign key constraints during test setup to simplify schema management
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        var context = new InferenceApiDbContext(options);
        context.Database.EnsureCreated();

        // Force-create WorkflowEngine-owned tables (workflow_definitions, workflow_steps)
        // EnsureCreated() above only creates Inference API tables; EF's database existence check
        // prevents the Engine context's EnsureCreated() from running on a connection that already
        // has tables. Use IRelationalDatabaseCreator to bypass that check and create only missing tables.
        var engineOptions = new DbContextOptionsBuilder<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>()
            .UseSqlite(connection)
            .Options;
        using var engineDb = new ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext(engineOptions);
        var creator = engineDb.GetService<IRelationalDatabaseCreator>();
        creator.CreateTables();

        return (connection, context);
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

    private static IConfiguration CreateConfiguration(int maxExecutionsPerHour = 10)
    {
        var configDict = new Dictionary<string, string?> { { "Assistant:MaxExecutionsPerHour", maxExecutionsPerHour.ToString() } };
        return new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();
    }

    private static WorkflowAssistantController CreateController(InferenceApiDbContext db, Guid ownerId)
    {
        var diffService = new WorkflowEditDiffService(db);
        var config = CreateConfiguration(maxExecutionsPerHour: 10);
        var guard = new AssistantExecutionGuard(db, config);
        var publishEndpoint = Moq.Mock.Of<MassTransit.IPublishEndpoint>();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowExecutionService>.Instance;
        var executionService = new WorkflowExecutionService(db, publishEndpoint, logger);

        return new WorkflowAssistantController(
            db,
            new FakeCurrentUser(ownerId), TestTenancy.Access(db, new FakeCurrentUser(ownerId)),
            diffService,
            guard,
            executionService);
    }

    /// <summary>
    /// Test: Validate with unknown agent id should return Valid: false with non-empty Errors list
    /// </summary>
    [Fact]
    public async Task Validate_WithUnknownAgentId_ReturnsInvalidWithErrors()
    {
        // Arrange
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();

        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        WorkflowAssistantController controller = CreateController(db, ownerId);

        // Use a non-existent agent ID
        var invalidAgentId = Guid.NewGuid();
        var steps = new List<CreateWorkflowStepRequest>
        {
            new(invalidAgentId, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent")
        };

        // Act
        var result = await controller.Validate(project.Id, steps, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var validationResult = Assert.IsType<WorkflowValidationResult>(okResult.Value);
        Assert.False(validationResult.Valid);
        Assert.NotEmpty(validationResult.Errors);
    }

    /// <summary>
    /// Test: Propose with null WorkflowId should return all steps as Additions
    /// </summary>
    [Fact]
    public async Task Propose_WithNullWorkflowId_ReturnsAllStepsAsAdditions()
    {
        // Arrange
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();

        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "TestAgent",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        WorkflowAssistantController controller = CreateController(db, ownerId);

        var steps = new List<CreateWorkflowStepRequest>
        {
            new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent"),
            new(agent.Id, StepOrder: 2, EdgeConditionJson: null, Label: "Step 2", StepType: "Agent")
        };
        var request = new ProposeWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps);

        // Act
        var result = await controller.Propose(project.Id, request, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var diff = Assert.IsType<WorkflowEditDiff>(okResult.Value);
        Assert.NotEmpty(diff.Additions);
        Assert.Equal(2, diff.Additions.Count);
        Assert.Empty(diff.Removals);
        Assert.Empty(diff.Modifications);
    }

    /// <summary>
    /// Test: Propose with a seeded workflow should compute diff against its current steps
    /// </summary>
    [Fact]
    public async Task Propose_WithExistingWorkflow_ComputesDiffAgainstCurrentSteps()
    {
        // Arrange
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();

        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "TestAgent",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);

        // Create a workflow with existing steps
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "existing",
            ProjectId = project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        workflow.Steps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = agent.Id,
            StepOrder = 1,
            Label = "Current Step 1",
            StepType = StepType.Agent
        });
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();

        WorkflowAssistantController controller = CreateController(db, ownerId);

        // Propose with a new step added
        var steps = new List<CreateWorkflowStepRequest>
        {
            new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Current Step 1", StepType: "Agent"),
            new(agent.Id, StepOrder: 2, EdgeConditionJson: null, Label: "New Step 2", StepType: "Agent")
        };
        var request = new ProposeWorkflowRequest(WorkflowId: workflow.Id, Name: null, Steps: steps);

        // Act
        var result = await controller.Propose(project.Id, request, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var diff = Assert.IsType<WorkflowEditDiff>(okResult.Value);
        Assert.Single(diff.Additions);
        Assert.Empty(diff.Removals);
        Assert.Empty(diff.Modifications);
        Assert.Equal(2, diff.Additions.First().StepOrder);
    }

    /// <summary>
    /// Test: Apply with null WorkflowId and matching token should create workflow and return 201
    /// </summary>
    [Fact]
    public async Task Apply_WithNullWorkflowIdAndMatchingToken_Creates201WithNewWorkflow()
    {
        // Arrange
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();

        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "TestAgent",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        WorkflowAssistantController controller = CreateController(db, ownerId);

        // First propose to get the confirmation token
        var steps = new List<CreateWorkflowStepRequest>
        {
            new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent")
        };
        var proposeRequest = new ProposeWorkflowRequest(WorkflowId: null, Name: "newwf", Steps: steps);
        var proposeResult = await controller.Propose(project.Id, proposeRequest, CancellationToken.None);
        var okProposeResult = Assert.IsType<OkObjectResult>(proposeResult.Result);
        var proposedDiff = Assert.IsType<WorkflowEditDiff>(okProposeResult.Value);
        var confirmationToken = proposedDiff.ConfirmationToken;

        // Now apply with the token
        var applyRequest = new ApplyWorkflowRequest(WorkflowId: null, Name: "newwf", Steps: steps, ConfirmationToken: confirmationToken);

        // Act
        var applyResult = await controller.Apply(project.Id, applyRequest, CancellationToken.None);

        // Assert
        var createdResult = Assert.IsType<ObjectResult>(applyResult.Result);
        Assert.Equal(201, createdResult.StatusCode);
        var response = Assert.IsType<WorkflowAssistantApplyResponse>(createdResult.Value);
        Assert.Equal(1, response.StepCount);

        // Verify the workflow was persisted to the database
        var persistedWorkflow = await db.WorkflowDefinitions
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == response.WorkflowId);
        Assert.NotNull(persistedWorkflow);
        Assert.Single(persistedWorkflow.Steps);
    }

    /// <summary>
    /// Test: Apply with stale confirmation token should return 409 Conflict
    /// </summary>
    [Fact]
    public async Task Apply_WithStaleConfirmationToken_Returns409Conflict()
    {
        // Arrange
        var (connection, db) = CreateSqliteContext();
        using (connection)
        using (db)
        {
            Guid ownerId = Guid.NewGuid();

            Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            AgentDefinition agent = new()
            {
                Id = Guid.NewGuid(),
                Name = "TestAgent",
                Description = "d",
                SystemPrompt = "p",
                AgentType = AgentType.AuthorAgent,
                IsBuiltIn = true,
                CreatedAt = DateTime.UtcNow
            };
            db.Projects.Add(project);
            db.AgentDefinitions.Add(agent);

            // Create a workflow with existing steps
            var workflow = new WorkflowDefinition
            {
                Id = Guid.NewGuid(),
                Name = "existing",
                ProjectId = project.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            workflow.Steps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflow.Id,
                AgentDefinitionId = agent.Id,
                StepOrder = 1,
                Label = "Step 1",
                StepType = StepType.Agent
            });
            db.WorkflowDefinitions.Add(workflow);
            await db.SaveChangesAsync();

            WorkflowAssistantController controller = CreateController(db, ownerId);

            // First propose to get a confirmation token
            var steps = new List<CreateWorkflowStepRequest>
            {
                new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent")
            };
            var proposeRequest = new ProposeWorkflowRequest(WorkflowId: workflow.Id, Name: null, Steps: steps);
            var proposeResult = await controller.Propose(project.Id, proposeRequest, CancellationToken.None);
            var okProposeResult = Assert.IsType<OkObjectResult>(proposeResult.Result);
            var proposedDiff = Assert.IsType<WorkflowEditDiff>(okProposeResult.Value);
            var staleToken = proposedDiff.ConfirmationToken;

            // Now mutate the workflow's steps directly via the db context to invalidate the token
            await db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == workflow.Id).ExecuteDeleteAsync();
            var newStep = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflow.Id,
                AgentDefinitionId = agent.Id,
                StepOrder = 2,
                Label = "Modified Step",
                StepType = StepType.Agent
            };
            db.WorkflowSteps.Add(newStep);
            await db.SaveChangesAsync();

            // Now try to apply with the stale token
            var applyRequest = new ApplyWorkflowRequest(WorkflowId: workflow.Id, Name: null, Steps: steps, ConfirmationToken: staleToken);

            // Act
            var applyResult = await controller.Apply(project.Id, applyRequest, CancellationToken.None);

            // Assert
            var conflictResult = Assert.IsType<ConflictObjectResult>(applyResult.Result);
            Assert.Equal(409, conflictResult.StatusCode);
        }
    }

    /// <summary>
    /// Test: Apply with current correct token should replace steps correctly
    /// </summary>
    [Fact]
    public async Task Apply_WithCorrectTokenAndWorkflowId_ReplacesStepsCorrectly()
    {
        // Arrange
        var (connection, db) = CreateSqliteContext();
        using (connection)
        using (db)
        {
            Guid ownerId = Guid.NewGuid();

            Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            AgentDefinition agent = new()
            {
                Id = Guid.NewGuid(),
                Name = "TestAgent",
                Description = "d",
                SystemPrompt = "p",
                AgentType = AgentType.AuthorAgent,
                IsBuiltIn = true,
                CreatedAt = DateTime.UtcNow
            };
            db.Projects.Add(project);
            db.AgentDefinitions.Add(agent);

            // Create a workflow with an existing step
            var workflow = new WorkflowDefinition
            {
                Id = Guid.NewGuid(),
                Name = "existing",
                ProjectId = project.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            workflow.Steps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflow.Id,
                AgentDefinitionId = agent.Id,
                StepOrder = 1,
                Label = "Old Step 1",
                StepType = StepType.Agent
            });
            db.WorkflowDefinitions.Add(workflow);
            await db.SaveChangesAsync();

            WorkflowAssistantController controller = CreateController(db, ownerId);

            // Propose with the current step + a new step
            var currentSteps = new List<CreateWorkflowStepRequest>
            {
                new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Old Step 1", StepType: "Agent"),
                new(agent.Id, StepOrder: 2, EdgeConditionJson: null, Label: "New Step 2", StepType: "Agent")
            };
            var proposeRequest = new ProposeWorkflowRequest(WorkflowId: workflow.Id, Name: null, Steps: currentSteps);
            var proposeResult = await controller.Propose(project.Id, proposeRequest, CancellationToken.None);
            var okProposeResult = Assert.IsType<OkObjectResult>(proposeResult.Result);
            var proposedDiff = Assert.IsType<WorkflowEditDiff>(okProposeResult.Value);
            var correctToken = proposedDiff.ConfirmationToken;

            // Now apply with the correct token
            var applyRequest = new ApplyWorkflowRequest(WorkflowId: workflow.Id, Name: "updated", Steps: currentSteps, ConfirmationToken: correctToken);

            // Act
            var applyResult = await controller.Apply(project.Id, applyRequest, CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(applyResult.Result);
            var response = Assert.IsType<WorkflowAssistantApplyResponse>(okResult.Value);
            Assert.Equal(2, response.StepCount);

            // Verify the workflow steps were updated in the database
            var updatedWorkflow = await db.WorkflowDefinitions
                .Include(w => w.Steps)
                .FirstOrDefaultAsync(w => w.Id == workflow.Id);
            Assert.NotNull(updatedWorkflow);
            Assert.Equal(2, updatedWorkflow.Steps.Count);
            Assert.Equal("updated", updatedWorkflow.Name);
        }
    }

    // ------------------------------------------------------------------------------------------
    // The "proposal is out of date, but the changes are already in" bug.
    //
    // The confirmation token hashes the workflow's CURRENT steps, so it stops verifying the moment
    // anything else changes them — including this very proposal having been applied (the assistant
    // applies in the same turn, or the page's Apply was clicked twice). Applying again must say
    // "already applied", not "out of date", and must write nothing.
    // ------------------------------------------------------------------------------------------

    private static async Task<(WorkflowDefinition workflow, AgentDefinition agent, Project project, Guid ownerId)> SeedWorkflowAsync(
        InferenceApiDbContext db, int existingMinScore = 5)
    {
        Guid ownerId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "TestAgent",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "existing",
            ProjectId = project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        workflow.Steps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = agent.Id,
            StepOrder = 1,
            Label = "Step 1",
            StepType = StepType.Agent,
            MinScore = existingMinScore
        });
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();
        return (workflow, agent, project, ownerId);
    }

    [Fact]
    public async Task Apply_OfAProposalThatIsAlreadyApplied_AnswersAlreadyAppliedAndWritesNothing()
    {
        var (connection, db) = CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (workflow, agent, project, ownerId) = await SeedWorkflowAsync(db);
            WorkflowAssistantController controller = CreateController(db, ownerId);
            var steps = new List<CreateWorkflowStepRequest>
            {
                new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent", MinScore: 5),
                new(agent.Id, StepOrder: 2, EdgeConditionJson: null, Label: "Review", StepType: "Agent")
            };
            var proposed = Assert.IsType<WorkflowEditDiff>(Assert.IsType<OkObjectResult>(
                (await controller.Propose(project.Id, new ProposeWorkflowRequest(workflow.Id, null, steps), CancellationToken.None)).Result).Value);

            // The first apply lands (this is the assistant applying in its own turn)...
            var first = Assert.IsType<OkObjectResult>(
                (await controller.Apply(project.Id, new ApplyWorkflowRequest(workflow.Id, null, steps, proposed.ConfirmationToken), CancellationToken.None)).Result);
            Assert.Null(Assert.IsType<WorkflowAssistantApplyResponse>(first.Value).AlreadyApplied);
            DateTime updatedAt = (await db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == workflow.Id)).UpdatedAt;

            // ...and the page's tray, still holding the same proposal, applies it a second time.
            var second = Assert.IsType<OkObjectResult>(
                (await controller.Apply(project.Id, new ApplyWorkflowRequest(workflow.Id, null, steps, proposed.ConfirmationToken), CancellationToken.None)).Result);

            var response = Assert.IsType<WorkflowAssistantApplyResponse>(second.Value);
            Assert.True(response.AlreadyApplied);
            Assert.Equal(2, response.StepCount);
            Assert.Equal(2, await db.WorkflowSteps.CountAsync(s => s.WorkflowDefinitionId == workflow.Id));
            Assert.Equal(updatedAt, (await db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == workflow.Id)).UpdatedAt);
        }
    }

    [Fact]
    public async Task Apply_WithAFabricatedTokenForStepsTheWorkflowDoesNotHold_IsStillAConflict()
    {
        var (connection, db) = CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (workflow, agent, project, ownerId) = await SeedWorkflowAsync(db);
            WorkflowAssistantController controller = CreateController(db, ownerId);
            var steps = new List<CreateWorkflowStepRequest>
            {
                new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent", MinScore: 5),
                new(agent.Id, StepOrder: 2, EdgeConditionJson: null, Label: "Brand new step", StepType: "Agent")
            };

            var result = await controller.Apply(
                project.Id, new ApplyWorkflowRequest(workflow.Id, null, steps, "not-a-token"), CancellationToken.None);

            Assert.IsType<ConflictObjectResult>(result.Result);
            Assert.Single(await db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == workflow.Id).ToListAsync());
        }
    }

    [Fact]
    public async Task Apply_WithAStaleTokenWhoseOnlyDifferenceIsASettingNotALabel_IsNotMistakenForAlreadyApplied()
    {
        var (connection, db) = CreateSqliteContext();
        using (connection)
        using (db)
        {
            // The stored step has MinScore 5; the proposal asks for 8 and nothing else differs. The diff used to
            // compare only agent/label/type/config blobs, so this read as "no change" — which would have let a
            // stale apply claim success for a change that was never made.
            var (workflow, agent, project, ownerId) = await SeedWorkflowAsync(db, existingMinScore: 5);
            WorkflowAssistantController controller = CreateController(db, ownerId);
            var steps = new List<CreateWorkflowStepRequest>
            {
                new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent", MinScore: 8)
            };

            var diff = Assert.IsType<WorkflowEditDiff>(Assert.IsType<OkObjectResult>(
                (await controller.Propose(project.Id, new ProposeWorkflowRequest(workflow.Id, null, steps), CancellationToken.None)).Result).Value);
            Assert.False(diff.ChangesNothing);
            Assert.Single(diff.Modifications);

            var result = await controller.Apply(
                project.Id, new ApplyWorkflowRequest(workflow.Id, null, steps, "stale"), CancellationToken.None);
            Assert.IsType<ConflictObjectResult>(result.Result);
            Assert.Equal(5, (await db.WorkflowSteps.AsNoTracking().SingleAsync(s => s.WorkflowDefinitionId == workflow.Id)).MinScore);
        }
    }

    [Fact]
    public async Task Apply_WithAStaleTokenAndANewName_IsNotMistakenForAlreadyApplied()
    {
        var (connection, db) = CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (workflow, agent, project, ownerId) = await SeedWorkflowAsync(db);
            WorkflowAssistantController controller = CreateController(db, ownerId);
            // The steps are exactly what is stored, so nothing "changes" — but the request renames the workflow,
            // and the token covers no name: "already applied" would be a lie about the rename.
            var steps = new List<CreateWorkflowStepRequest>
            {
                new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent", MinScore: 5)
            };

            var result = await controller.Apply(
                project.Id, new ApplyWorkflowRequest(workflow.Id, "A different name", steps, "stale"), CancellationToken.None);

            Assert.IsType<ConflictObjectResult>(result.Result);
            Assert.Equal("existing", (await db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == workflow.Id)).Name);
        }
    }

    [Fact]
    public async Task Propose_StatesWhichWorkflowItWasComputedFor_SoThePageAppliesItToTheRightOne()
    {
        var (connection, db) = CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (workflow, agent, project, ownerId) = await SeedWorkflowAsync(db);
            WorkflowAssistantController controller = CreateController(db, ownerId);
            var steps = new List<CreateWorkflowStepRequest> { new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent") };

            var edit = Assert.IsType<WorkflowEditDiff>(Assert.IsType<OkObjectResult>(
                (await controller.Propose(project.Id, new ProposeWorkflowRequest(workflow.Id, null, steps), CancellationToken.None)).Result).Value);
            var create = Assert.IsType<WorkflowEditDiff>(Assert.IsType<OkObjectResult>(
                (await controller.Propose(project.Id, new ProposeWorkflowRequest(null, "A new one", steps), CancellationToken.None)).Result).Value);

            Assert.Equal(workflow.Id, edit.WorkflowId);
            Assert.Null(create.WorkflowId);
        }
    }

    /// <summary>
    /// Test: Non-owner calling Validate with create shape (WorkflowId: null) should return 404 (cross-organization)
    /// </summary>
    [Fact]
    public async Task Validate_CreateShape_NonOwner_Returns404()
    {
        // Arrange
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Guid nonOwnerId = Guid.NewGuid();

        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "TestAgent",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        // Create controller with non-owner
        WorkflowAssistantController controller = CreateController(db, nonOwnerId);

        var steps = new List<CreateWorkflowStepRequest>
        {
            new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent")
        };

        // Act
        var result = await controller.Validate(project.Id, steps, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// Test: Non-owner calling Validate with edit shape (WorkflowId set) should return 404
    /// </summary>
    [Fact]
    public async Task Validate_EditShape_NonOwner_ReturnsNotFound()
    {
        // Note: Validate doesn't take WorkflowId, so we can't directly test the edit shape there.
        // This is more about project ownership check. If project is not owned by the caller,
        // they should get 404. This test documents that Validate is shape-agnostic.
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Guid nonOwnerId = Guid.NewGuid();

        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "TestAgent",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        WorkflowAssistantController controller = CreateController(db, nonOwnerId);

        var steps = new List<CreateWorkflowStepRequest>
        {
            new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent")
        };

        // Act
        var result = await controller.Validate(project.Id, steps, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// Test: Non-owner calling Propose with create shape (WorkflowId: null) should return 404 (cross-organization)
    /// </summary>
    [Fact]
    public async Task Propose_CreateShape_NonOwner_Returns404()
    {
        // Arrange
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Guid nonOwnerId = Guid.NewGuid();

        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "TestAgent",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        WorkflowAssistantController controller = CreateController(db, nonOwnerId);

        var steps = new List<CreateWorkflowStepRequest>
        {
            new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent")
        };
        var request = new ProposeWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps);

        // Act
        var result = await controller.Propose(project.Id, request, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// Test: Non-owner calling Propose with edit shape (WorkflowId set) should return 404
    /// </summary>
    [Fact]
    public async Task Propose_EditShape_NonOwner_Returns404()
    {
        // Arrange
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Guid nonOwnerId = Guid.NewGuid();

        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "TestAgent",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "existing",
            ProjectId = project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        workflow.Steps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = agent.Id,
            StepOrder = 1,
            Label = "Step 1",
            StepType = StepType.Agent
        });
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();

        WorkflowAssistantController controller = CreateController(db, nonOwnerId);

        var steps = new List<CreateWorkflowStepRequest>
        {
            new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent")
        };
        var request = new ProposeWorkflowRequest(WorkflowId: workflow.Id, Name: null, Steps: steps);

        // Act
        var result = await controller.Propose(project.Id, request, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// Test: Non-owner calling Apply with create shape (WorkflowId: null) should return 404 (cross-organization)
    /// </summary>
    [Fact]
    public async Task Apply_CreateShape_NonOwner_Returns404()
    {
        // Arrange
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Guid nonOwnerId = Guid.NewGuid();

        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "TestAgent",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        WorkflowAssistantController controller = CreateController(db, nonOwnerId);

        var steps = new List<CreateWorkflowStepRequest>
        {
            new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent")
        };
        var request = new ApplyWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps, ConfirmationToken: "token");

        // Act
        var result = await controller.Apply(project.Id, request, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// Test: Non-owner calling Apply with edit shape (WorkflowId set) should return 404
    /// </summary>
    [Fact]
    public async Task Apply_EditShape_NonOwner_Returns404()
    {
        // Arrange
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Guid nonOwnerId = Guid.NewGuid();

        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "TestAgent",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "existing",
            ProjectId = project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        workflow.Steps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = agent.Id,
            StepOrder = 1,
            Label = "Step 1",
            StepType = StepType.Agent
        });
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();

        WorkflowAssistantController controller = CreateController(db, nonOwnerId);

        var steps = new List<CreateWorkflowStepRequest>
        {
            new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Step 1", StepType: "Agent")
        };
        var request = new ApplyWorkflowRequest(WorkflowId: workflow.Id, Name: null, Steps: steps, ConfirmationToken: "token");

        // Act
        var result = await controller.Apply(project.Id, request, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }
}
