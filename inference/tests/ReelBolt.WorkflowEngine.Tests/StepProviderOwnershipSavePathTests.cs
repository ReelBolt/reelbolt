using System;
using System.Collections.Generic;
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
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// WP A10, wiring half: the provider-ownership check is not merely implemented, it is on every save
/// path — workflow create/update, the assistant's apply endpoint, and the diff service the assistant
/// validates through — so a cross-organization provider reference cannot reach the database.
/// </summary>
/// <remarks>
/// Companion to <see cref="StepProviderOwnershipValidatorTests"/> (the rules themselves) and
/// <see cref="ProjectTransferTests"/> (the transfer refusal).
/// </remarks>
public sealed class StepProviderOwnershipSavePathTests : IDisposable
{
    private static readonly Guid SourceOrg = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid OtherOrg = Guid.Parse("33333333-0000-0000-0000-000000000003");

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();

    public StepProviderOwnershipSavePathTests()
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

    [Fact]
    public async Task Create_refuses_a_step_naming_another_organizations_provider()
    {
        (Project project, AgentDefinition agent, ModelCapabilityView theirs) = await SeedAsync();

        ActionResult<WorkflowDefinitionResponse> result = await WorkflowsController()
            .Create(project.Id, new CreateWorkflowRequest("wf", VoiceoverSteps(agent.Id, theirs.Id)), CancellationToken.None);

        BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("is not available to this organization");
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0, "nothing may be persisted for a refused save");
    }

    [Fact]
    public async Task Create_accepts_a_step_naming_a_platform_provider()
    {
        (Project project, AgentDefinition agent, _) = await SeedAsync();
        ModelCapabilityView platform = Provider("platform-voice", organizationId: null);
        _db.AddFlat(platform);
        await _db.SaveChangesAsync();

        ActionResult<WorkflowDefinitionResponse> result = await WorkflowsController()
            .Create(project.Id, new CreateWorkflowRequest("wf", VoiceoverSteps(agent.Id, platform.Id)), CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task Create_tells_the_author_an_unfinished_or_non_video_step_result_source_apart_from_a_missing_one()
    {
        (Project project, AgentDefinition agent, _) = await SeedAsync();
        var exec = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = project.Id, WorkflowDefinitionId = Guid.NewGuid(), Status = ExecutionStatus.Passed };
        var failed = new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = exec.Id,
            WorkflowStepId = Guid.NewGuid(),
            Status = StepStatus.Failed,
            OutputStorageKey = $"projects/{project.Id}/outputFiles/{exec.Id}/half.mp4"
        };
        var document = new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = exec.Id,
            WorkflowStepId = Guid.NewGuid(),
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{project.Id}/outputFiles/{exec.Id}/report.json"
        };
        _db.WorkflowExecutions.Add(exec);
        _db.WorkflowStepResults.AddRange(failed, document);
        await _db.SaveChangesAsync();

        async Task<string> RefusalFor(Guid stepResultId)
        {
            List<CreateWorkflowStepRequest> steps =
            [
                new(agent.Id, 1, null, "Analyze", StepType: nameof(StepType.VideoAnalyze),
                    VideoAnalyzeConfigJson: $$$"""{"version":1,"source":{"kind":"StepResult","stepResultId":"{{{stepResultId}}}"}}""")
            ];
            ActionResult<WorkflowDefinitionResponse> result = await WorkflowsController()
                .Create(project.Id, new CreateWorkflowRequest("wf", steps), CancellationToken.None);
            return result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()!;
        }

        (await RefusalFor(failed.Id)).Should().Contain("has not finished successfully");
        (await RefusalFor(document.Id)).Should().Contain("did not produce a video");
        (await RefusalFor(Guid.NewGuid())).Should().Contain("not found in this project");
    }

    [Fact]
    public async Task Update_refuses_a_step_naming_another_organizations_provider()
    {
        (Project project, AgentDefinition agent, ModelCapabilityView theirs) = await SeedAsync();

        ActionResult<WorkflowDefinitionResponse> created = await WorkflowsController()
            .Create(project.Id, new CreateWorkflowRequest("wf", VoiceoverSteps(agent.Id, providerId: null)), CancellationToken.None);
        var workflow = ((ObjectResult)created.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;

        ActionResult<WorkflowDefinitionResponse> result = await WorkflowsController().Update(
            project.Id, workflow.Id, new UpdateWorkflowRequest("wf", VoiceoverSteps(agent.Id, theirs.Id)), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _db.ChangeTracker.Clear();
        WorkflowStep stored = await _db.WorkflowSteps.SingleAsync(s => s.WorkflowDefinitionId == workflow.Id);
        stored.VoiceoverConfigJson.Should().NotContain(theirs.Id.ToString(),
            "a refused update must leave the stored step exactly as it was");
    }

    [Fact]
    public async Task Apply_refuses_a_step_naming_another_organizations_provider()
    {
        (Project project, AgentDefinition agent, ModelCapabilityView theirs) = await SeedAsync();
        WorkflowAssistantController controller = AssistantController();
        List<CreateWorkflowStepRequest> steps = VoiceoverSteps(agent.Id, theirs.Id);

        ActionResult<WorkflowEditDiff> proposed = await controller.Propose(
            project.Id, new ProposeWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps), CancellationToken.None);
        var diff = ((OkObjectResult)proposed.Result!).Value.Should().BeOfType<WorkflowEditDiff>().Subject;

        ActionResult<WorkflowAssistantApplyResponse> result = await controller.Apply(
            project.Id,
            new ApplyWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps, ConfirmationToken: diff.ConfirmationToken),
            CancellationToken.None);

        BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("is not available to this organization");
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ValidateProposedSteps_reports_it_so_validate_and_apply_cannot_disagree()
    {
        (Project project, AgentDefinition agent, ModelCapabilityView theirs) = await SeedAsync();

        WorkflowValidationResult result = new WorkflowEditDiffService(_db)
            .ValidateProposedSteps(project.Id, VoiceoverSteps(agent.Id, theirs.Id));

        result.Valid.Should().BeFalse();
        result.Errors.Should().ContainMatch("Step 1 (Voiceover): providerId references an inference provider*");
    }

    // ------------------------------------------------------------------------------------------
    // SQFU-S3: agents named inside a step's config (a Parallel step's extra agents, a room's seat and
    // director agents) obey the same platform-or-own-organization rule as step providers.
    // ------------------------------------------------------------------------------------------

    private async Task<AgentDefinition> AgentAsync(Guid? organizationId, bool builtIn = false)
    {
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = builtIn ? "VideoStoryEditor" : "Custom",
            Description = "d",
            SystemPrompt = "p",
            AgentType = builtIn ? AgentType.VideoStoryEditor : AgentType.Custom,
            IsBuiltIn = builtIn,
            OrganizationId = organizationId,
            CreatedAt = DateTime.UtcNow
        };
        _db.AgentDefinitions.Add(agent);
        await _db.SaveChangesAsync();
        return agent;
    }

    private static List<CreateWorkflowStepRequest> ParallelSteps(Guid agentId, params Guid[] extra) =>
    [
        new(agentId, 1, null, "Fan out", StepType: nameof(StepType.Parallel),
            ParallelAgentIdsJson: System.Text.Json.JsonSerializer.Serialize(System.Linq.Enumerable.Prepend(extra, agentId)))
    ];

    private static string RoomConfig(Guid? seatAgent, Guid? director) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            seats = new object[] { new { name = "Skeptic", persona = "Doubts", agentDefinitionId = seatAgent }, new { name = "Builtin", persona = "Default seat" } },
            directorAgentDefinitionId = director
        });

    private static List<CreateWorkflowStepRequest> RoomSteps(StepType room, Guid agentId, Guid? seatAgent, Guid? director)
    {
        string config = RoomConfig(seatAgent, director);
        return
        [
            room switch
            {
                StepType.EditRoom => new(agentId, 1, null, "Room", StepType: nameof(StepType.EditRoom), EditRoomConfigJson: config),
                StepType.GraphicsRoom => new(agentId, 1, null, "Room", StepType: nameof(StepType.GraphicsRoom), GraphicsRoomConfigJson: config),
                StepType.ColorGradeRoom => new(agentId, 1, null, "Room", StepType: nameof(StepType.ColorGradeRoom), ColorGradeRoomConfigJson: config),
                _ => new(agentId, 1, null, "Room", StepType: nameof(StepType.DailiesRoom), DailiesRoomConfigJson: config)
            }
        ];
    }

    [Fact]
    public async Task Create_refuses_a_parallel_step_naming_another_organizations_agent()
    {
        (Project project, AgentDefinition agent, _) = await SeedAsync();
        AgentDefinition theirs = await AgentAsync(OtherOrg);

        ActionResult<WorkflowDefinitionResponse> result = await WorkflowsController()
            .Create(project.Id, new CreateWorkflowRequest("wf", ParallelSteps(agent.Id, theirs.Id)), CancellationToken.None);

        BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("references an agent that is not available to this organization");
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0, "nothing may be persisted for a refused save");
    }

    [Theory]
    [InlineData(StepType.EditRoom)]
    [InlineData(StepType.GraphicsRoom)]
    [InlineData(StepType.ColorGradeRoom)]
    [InlineData(StepType.DailiesRoom)]
    public async Task Apply_refuses_a_room_seat_naming_another_organizations_agent(StepType room)
    {
        (Project project, AgentDefinition agent, _) = await SeedAsync();
        AgentDefinition theirs = await AgentAsync(OtherOrg);
        WorkflowAssistantController controller = AssistantController();
        List<CreateWorkflowStepRequest> steps = RoomSteps(room, agent.Id, theirs.Id, director: null);

        ActionResult<WorkflowEditDiff> proposed = await controller.Propose(
            project.Id, new ProposeWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps), CancellationToken.None);
        var diff = ((OkObjectResult)proposed.Result!).Value.Should().BeOfType<WorkflowEditDiff>().Subject;
        ActionResult<WorkflowAssistantApplyResponse> result = await controller.Apply(
            project.Id,
            new ApplyWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps, ConfirmationToken: diff.ConfirmationToken),
            CancellationToken.None);

        BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("references an agent that is not available to this organization");
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ValidateProposedSteps_reports_a_foreign_director_and_an_unknown_one_with_the_same_message()
    {
        (Project project, AgentDefinition agent, _) = await SeedAsync();
        AgentDefinition theirs = await AgentAsync(OtherOrg);

        WorkflowValidationResult foreign = new WorkflowEditDiffService(_db)
            .ValidateProposedSteps(project.Id, RoomSteps(StepType.EditRoom, agent.Id, seatAgent: null, director: theirs.Id));
        WorkflowValidationResult unknown = new WorkflowEditDiffService(_db)
            .ValidateProposedSteps(project.Id, RoomSteps(StepType.EditRoom, agent.Id, seatAgent: null, director: Guid.NewGuid()));

        foreign.Valid.Should().BeFalse();
        foreign.Errors.Should().Contain("Step 1 (EditRoom): directorAgentDefinitionId references an agent that is not available to this organization.");
        unknown.Errors.Should().Equal(foreign.Errors, "D5: another tenant's agent and no agent at all read the same");
    }

    [Fact]
    public async Task Built_in_and_own_organization_agents_in_parallel_and_room_configs_save()
    {
        (Project project, AgentDefinition agent, _) = await SeedAsync();
        AgentDefinition mine = await AgentAsync(SourceOrg);
        AgentDefinition builtIn = await AgentAsync(organizationId: null, builtIn: true);

        ActionResult<WorkflowDefinitionResponse> parallel = await WorkflowsController()
            .Create(project.Id, new CreateWorkflowRequest("p", ParallelSteps(agent.Id, mine.Id, builtIn.Id)), CancellationToken.None);
        ActionResult<WorkflowDefinitionResponse> room = await WorkflowsController()
            .Create(project.Id, new CreateWorkflowRequest("r", RoomSteps(StepType.EditRoom, agent.Id, mine.Id, builtIn.Id)), CancellationToken.None);

        ((ObjectResult)parallel.Result!).StatusCode.Should().Be(201);
        ((ObjectResult)room.Result!).StatusCode.Should().Be(201);
    }

    // ------------------------------------------------------------------------------------------

    private sealed class FakeCurrentUser(Guid userId, Guid organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = OrganizationRole.Owner;
    }

    private WorkflowsController WorkflowsController()
    {
        var user = new FakeCurrentUser(_userId, SourceOrg);

        return new WorkflowsController(
            _db,
            user,
            new ReelBolt.Inference.Api.Services.Auth.ProjectAccessService(_db, user),
            Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(_db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(_db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance);
    }

    private WorkflowAssistantController AssistantController()
    {
        var user = new FakeCurrentUser(_userId, SourceOrg);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "Assistant:MaxExecutionsPerHour", "10" } })
            .Build();

        return new WorkflowAssistantController(
            _db,
            user,
            new ReelBolt.Inference.Api.Services.Auth.ProjectAccessService(_db, user),
            new WorkflowEditDiffService(_db),
            new AssistantExecutionGuard(_db, config),
            new WorkflowExecutionService(_db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance));
    }

    private async Task<(Project Project, AgentDefinition Agent, ModelCapabilityView Theirs)> SeedAsync()
    {
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "p",
            OwnerId = _userId,
            OrganizationId = SourceOrg,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "Custom",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.Custom,
            IsBuiltIn = false,
            OwnerId = _userId,
            OrganizationId = SourceOrg,
            CreatedAt = DateTime.UtcNow
        };

        ModelCapabilityView theirs = Provider("org-b-voice", OtherOrg);
        _db.Projects.Add(project);
        _db.AgentDefinitions.Add(agent);
        _db.AddFlat(theirs);
        await _db.SaveChangesAsync();

        return (project, agent, theirs);
    }

    private static ModelCapabilityView Provider(string name, Guid? organizationId) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Kind = InferenceProviderKind.FishAudio,
        Capability = InferenceProviderCapability.SpeechSynthesis,
        OrganizationId = organizationId,
        Endpoint = "https://provider.example",
        ModelName = "m1",
        IsEnabled = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    /// <summary>
    /// A Voiceover step whose config names <paramref name="providerId"/> — or nothing at all, which
    /// must save and keep resolving the capability's default.
    /// </summary>
    private static List<CreateWorkflowStepRequest> VoiceoverSteps(Guid agentId, Guid? providerId) =>
    [
        new(agentId, 1, null, "Narrate", StepType: nameof(StepType.Voiceover),
            VoiceoverConfigJson: providerId is { } id
                ? $$"""{"version":1,"source":{"kind":"Inline"},"providerId":"{{id}}"}"""
                : """{"version":1,"source":{"kind":"Inline"}}""")
    ];
}
