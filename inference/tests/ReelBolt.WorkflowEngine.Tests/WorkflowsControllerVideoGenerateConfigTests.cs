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
using ReelBolt.Shared.Workflows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Covers the bug where a VideoGenerate step's <see cref="WorkflowStep.VideoGenerateConfigJson"/>
/// was silently dropped by the Inference API (missing from the step DTOs and the controller's
/// request-to-entity / entity-to-response mappings), so a saved step lost its config and failed
/// CONFIG_INVALID at run time. Uses SQLite rather than InMemory because Update relies on
/// ExecuteDeleteAsync, which the InMemory provider does not support.
/// </summary>
public sealed class WorkflowsControllerVideoGenerateConfigTests : IDisposable
{
    private const string InitialConfig =
        """{"inlinePrompt":"a red fox running through snow","durationSeconds":6,"resolution":"768P","ratio":"16:9","takes":1,"maxSpendCredits":2.5}""";

    private const string UpdatedConfig =
        """{"inlinePrompt":"a city at night, timelapse","durationSeconds":10,"resolution":"1080P","ratio":"9:16","takes":2,"maxClips":2,"maxSpendCredits":4}""";

    // Passes JSON schema shape but fails VideoGenerateStepConfig.Validate(): MaxSpendCredits must be > 0.
    private const string ZeroSpendConfig =
        """{"inlinePrompt":"a red fox running through snow","durationSeconds":6,"takes":1,"maxSpendCredits":0}""";

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;

    public WorkflowsControllerVideoGenerateConfigTests()
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

    private static void ShouldBeSameJson(string actual, string expected)
    {
        actual.Should().NotBeNullOrWhiteSpace();
        JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected)).Should().BeTrue(
            because: $"expected config JSON {expected} but got {actual}");
    }

    [Fact]
    public async Task Create_then_Get_roundtrips_VideoGenerateConfigJson()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        WorkflowsController controller = CreateController(ownerId);

        CreateWorkflowStepRequest stepReq = new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Generate",
            StepType: nameof(StepType.VideoGenerate), VideoGenerateConfigJson: InitialConfig);

        ActionResult<WorkflowDefinitionResponse> createResult = await controller.Create(
            project.Id, new CreateWorkflowRequest("wf", new List<CreateWorkflowStepRequest> { stepReq }), CancellationToken.None);
        ((ObjectResult)createResult.Result!).StatusCode.Should().Be(201);
        var created = ((ObjectResult)createResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        WorkflowStepResponse createdStep = created.Steps.Single();
        createdStep.StepType.Should().Be(nameof(StepType.VideoGenerate));
        ShouldBeSameJson(createdStep.VideoGenerateConfigJson, InitialConfig);

        _db.ChangeTracker.Clear();
        WorkflowStep persisted = await _db.WorkflowSteps.AsNoTracking().SingleAsync(s => s.WorkflowDefinitionId == created.Id);
        ShouldBeSameJson(persisted.VideoGenerateConfigJson, InitialConfig);

        ActionResult<WorkflowDefinitionResponse> getResult = await controller.Get(project.Id, created.Id, CancellationToken.None);
        var fetched = ((OkObjectResult)getResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        ShouldBeSameJson(fetched.Steps.Single().VideoGenerateConfigJson, InitialConfig);
    }

    [Fact]
    public async Task Update_replaces_VideoGenerateConfigJson()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        WorkflowsController controller = CreateController(ownerId);

        CreateWorkflowStepRequest initial = new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Generate",
            StepType: nameof(StepType.VideoGenerate), VideoGenerateConfigJson: InitialConfig);
        ActionResult<WorkflowDefinitionResponse> createResult = await controller.Create(
            project.Id, new CreateWorkflowRequest("wf", new List<CreateWorkflowStepRequest> { initial }), CancellationToken.None);
        var created = ((ObjectResult)createResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;

        _db.ChangeTracker.Clear();
        CreateWorkflowStepRequest replacement = initial with { VideoGenerateConfigJson = UpdatedConfig };
        ActionResult<WorkflowDefinitionResponse> updateResult = await controller.Update(
            project.Id, created.Id, new UpdateWorkflowRequest(null, new List<CreateWorkflowStepRequest> { replacement }), CancellationToken.None);
        var updated = ((OkObjectResult)updateResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        ShouldBeSameJson(updated.Steps.Single().VideoGenerateConfigJson, UpdatedConfig);

        _db.ChangeTracker.Clear();
        ActionResult<WorkflowDefinitionResponse> getResult = await controller.Get(project.Id, created.Id, CancellationToken.None);
        var fetched = ((OkObjectResult)getResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        ShouldBeSameJson(fetched.Steps.Single().VideoGenerateConfigJson, UpdatedConfig);
    }

    private static (string message, string[] errors) ReadBadRequest(IActionResult? result)
    {
        BadRequestObjectResult badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonElement body = JsonSerializer.SerializeToElement(badRequest.Value);
        string message = body.GetProperty("message").GetString()!;
        string[] errors = body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToArray();
        return (message, errors);
    }

    [Fact]
    public async Task Create_with_MaxSpendCredits_zero_returns_400_and_persists_nothing()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        WorkflowsController controller = CreateController(ownerId);

        CreateWorkflowStepRequest stepReq = new(agent.Id, StepOrder: 3, EdgeConditionJson: null, Label: "Generate",
            StepType: nameof(StepType.VideoGenerate), VideoGenerateConfigJson: ZeroSpendConfig);

        ActionResult<WorkflowDefinitionResponse> result = await controller.Create(
            project.Id, new CreateWorkflowRequest("wf", new List<CreateWorkflowStepRequest> { stepReq }), CancellationToken.None);

        (string message, string[] errors) = ReadBadRequest(result.Result);
        errors.Should().Equal("Step 3 (VideoGenerate): MaxSpendCredits must be positive (greater than 0)");
        message.Should().Be(string.Join("\n", errors));

        _db.ChangeTracker.Clear();
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        (await _db.WorkflowSteps.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Update_with_MaxSpendCredits_zero_returns_400_and_leaves_workflow_unchanged()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        WorkflowsController controller = CreateController(ownerId);

        CreateWorkflowStepRequest initial = new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Generate",
            StepType: nameof(StepType.VideoGenerate), VideoGenerateConfigJson: InitialConfig);
        ActionResult<WorkflowDefinitionResponse> createResult = await controller.Create(
            project.Id, new CreateWorkflowRequest("wf", new List<CreateWorkflowStepRequest> { initial }), CancellationToken.None);
        var created = ((ObjectResult)createResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;

        _db.ChangeTracker.Clear();
        CreateWorkflowStepRequest bad = initial with { VideoGenerateConfigJson = ZeroSpendConfig };
        ActionResult<WorkflowDefinitionResponse> updateResult = await controller.Update(
            project.Id, created.Id, new UpdateWorkflowRequest("renamed", new List<CreateWorkflowStepRequest> { bad }), CancellationToken.None);

        (string message, string[] errors) = ReadBadRequest(updateResult.Result);
        errors.Should().Equal("Step 1 (VideoGenerate): MaxSpendCredits must be positive (greater than 0)");
        message.Should().Be(errors.Single());

        _db.ChangeTracker.Clear();
        WorkflowDefinition persistedWorkflow = await _db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == created.Id);
        persistedWorkflow.Name.Should().Be("wf");
        WorkflowStep persisted = await _db.WorkflowSteps.AsNoTracking().SingleAsync(s => s.WorkflowDefinitionId == created.Id);
        ShouldBeSameJson(persisted.VideoGenerateConfigJson, InitialConfig);
    }

    [Fact]
    public async Task Create_reports_every_failing_step_as_one_line_each()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        WorkflowsController controller = CreateController(ownerId);

        var steps = new List<CreateWorkflowStepRequest>
        {
            new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "a",
                StepType: nameof(StepType.VideoGenerate), VideoGenerateConfigJson: ZeroSpendConfig),
            new(agent.Id, StepOrder: 2, EdgeConditionJson: null, Label: "b",
                StepType: nameof(StepType.VideoGenerate), VideoGenerateConfigJson: null),
        };

        ActionResult<WorkflowDefinitionResponse> result = await controller.Create(
            project.Id, new CreateWorkflowRequest("wf", steps), CancellationToken.None);

        (string message, string[] errors) = ReadBadRequest(result.Result);
        errors.Should().HaveCount(2);
        errors[0].Should().StartWith("Step 1 (VideoGenerate): ");
        errors[1].Should().StartWith("Step 2 (VideoGenerate): ");
        message.Split('\n').Should().Equal(errors);
    }

    [Fact]
    public void SaveValidator_flags_missing_blank_and_malformed_VideoGenerate_config()
    {
        StepConfigSaveValidator.ValidateVideoGenerateConfig(null).Should().ContainSingle();
        StepConfigSaveValidator.ValidateVideoGenerateConfig("   ").Should().ContainSingle();
        StepConfigSaveValidator.ValidateVideoGenerateConfig("{not json").Should().ContainSingle()
            .Which.Should().Contain("not valid JSON");
        StepConfigSaveValidator.ValidateVideoGenerateConfig("null").Should().ContainSingle();
    }

    [Fact]
    public void SaveValidator_uses_the_executor_options_so_string_enums_and_any_case_parse()
    {
        // JsonStringEnumConverter + Web defaults (case-insensitive) — the executor's options.
        StepConfigSaveValidator.ValidateVideoGenerateConfig(
            """{"PlanSource":"Inline","InlinePrompt":"x","MaxSpendCredits":1}""").Should().BeEmpty();
        StepConfigSaveValidator.ValidateVideoGenerateConfig(ZeroSpendConfig)
            .Should().Equal(new VideoGenerateStepConfig().Validate());
    }

    [Fact]
    public void SaveValidator_requires_a_config_for_every_config_bearing_step_type()
    {
        // A config-bearing step with no config is NOT "unconfigured but savable": its executor
        // hard-fails it at run time, so saving it only defers the failure — observed live as a
        // VideoCompile step that ran through ~82 minutes of analysis before dying on its last step.
        var steps = new List<CreateWorkflowStepRequest>
        {
            // No StepType at all: nothing to check.
            new(Guid.NewGuid(), 1, null, "no step type"),
            // Agent steps carry no config blob, so a stray VideoGenerateConfigJson is ignored.
            new(Guid.NewGuid(), 2, null, "agent", StepType: nameof(StepType.Agent), VideoGenerateConfigJson: "{bogus"),
            // Every one of these carries a required config.
            new(Guid.NewGuid(), 3, null, "extract", StepType: nameof(StepType.Extract)),
            new(Guid.NewGuid(), 4, null, "analyze", StepType: nameof(StepType.VideoAnalyze)),
            new(Guid.NewGuid(), 5, null, "compile", StepType: nameof(StepType.VideoCompile)),
            new(Guid.NewGuid(), 6, null, "edit room", StepType: nameof(StepType.EditRoom)),
            new(Guid.NewGuid(), 7, null, "graphics room", StepType: nameof(StepType.GraphicsRoom)),
            new(Guid.NewGuid(), 8, null, "grade room", StepType: nameof(StepType.ColorGradeRoom)),
        };

        IReadOnlyList<string> errors = StepConfigSaveValidator.Validate(steps);

        errors.Should().HaveCount(6);
        errors.Should().AllSatisfy(error => error.Should().Contain("is required."));
        errors.Should().Contain(error => error.Contains("ExtractConfigJson"));
        errors.Should().Contain(error => error.Contains("VideoAnalyzeConfigJson"));
        errors.Should().Contain(error => error.Contains("VideoCompileConfigJson"));
        errors.Should().Contain(error => error.Contains("EditRoomConfigJson"));
        errors.Should().Contain(error => error.Contains("GraphicsRoomConfigJson"));
        errors.Should().Contain(error => error.Contains("ColorGradeRoomConfigJson"));
        errors.Should().NotContain(error => error.Contains("Step 1 "));
        errors.Should().NotContain(error => error.Contains("Step 2 "));
    }

    [Fact]
    public void SaveValidator_rejects_a_config_that_is_not_a_JSON_object()
    {
        var steps = new List<CreateWorkflowStepRequest>
        {
            new(Guid.NewGuid(), 1, null, "a", StepType: nameof(StepType.VideoCompile), VideoCompileConfigJson: "{not json"),
            new(Guid.NewGuid(), 2, null, "b", StepType: nameof(StepType.VideoAnalyze), VideoAnalyzeConfigJson: "\"just a string\""),
            new(Guid.NewGuid(), 3, null, "c", StepType: nameof(StepType.Extract), ExtractConfigJson: "null"),
        };

        IReadOnlyList<string> errors = StepConfigSaveValidator.Validate(steps);

        errors.Should().HaveCount(3);
        errors.Should().Contain(error => error.Contains("VideoCompileConfigJson is not valid JSON"));
        errors.Should().Contain(error => error.Contains("VideoAnalyzeConfigJson must be a JSON object"));
        errors.Should().Contain(error => error.Contains("ExtractConfigJson must be a JSON object"));
    }

    [Fact]
    public void SaveValidator_accepts_a_present_object_config_for_non_VideoGenerate_steps()
    {
        var steps = new List<CreateWorkflowStepRequest>
        {
            new(Guid.NewGuid(), 1, null, "a", StepType: nameof(StepType.VideoCompile), VideoCompileConfigJson: """{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1}"""),
            new(Guid.NewGuid(), 2, null, "b", StepType: nameof(StepType.Extract), ExtractConfigJson: """{"operation":"Project"}"""),
            new(Guid.NewGuid(), 3, null, "c", StepType: nameof(StepType.VideoAnalyze), VideoAnalyzeConfigJson: """{"version":1,"source":{"kind":"ProjectFile"}}"""),
        };

        StepConfigSaveValidator.Validate(steps).Should().BeEmpty();
    }
}
