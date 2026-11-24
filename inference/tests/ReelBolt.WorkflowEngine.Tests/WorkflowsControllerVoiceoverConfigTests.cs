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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// QA condition 1 for the voiceover config wiring: a Voiceover step's
/// <see cref="WorkflowStep.VoiceoverConfigJson"/> must survive a real round trip through
/// <see cref="WorkflowsController"/>. Before T-067 neither step DTO carried the field, so the
/// builder's <c>voiceoverConfigJson</c> was silently dropped on save, both read projections had
/// nothing to return, and the step died at execution with
/// <c>CONFIG_INVALID: Voiceover step has no VoiceoverConfigJson configured.</c>
///
/// <para>
/// Two things here are deliberate and load-bearing, because the whole point of this file is that
/// it must have been RED before T-067:
/// <list type="number">
/// <item>The request body is built as RAW JSON and deserialized into the request DTO, never as a
/// <c>new CreateWorkflowStepRequest(… VoiceoverConfigJson: …)</c> initializer. An initializer would
/// not even compile against the pre-fix DTO, and a test that does not compile proves nothing about
/// the runtime bug — it just pins the new member. Raw JSON is also exactly what the real client
/// sends and what ASP.NET Core's model binder consumes, so an unknown member
/// (<c>voiceoverConfigJson</c> before T-067) is dropped by the binder precisely as it was in
/// production.</item>
/// <item>The assertions read <c>voiceoverConfigJson</c> out of the response's serialized JSON
/// rather than off a DTO property, for the same reason: no member of <c>WorkflowStepResponse</c>
/// is named in this file, so reverting the six T-067 source paths makes these tests FAIL at run
/// time instead of failing to build.</item>
/// </list>
/// </para>
///
/// <para>
/// SQLite (not InMemory) for the reason the sibling VideoGenerate round-trip test documents: the
/// controller's update path calls <c>ExecuteDeleteAsync</c>, which the InMemory provider does not
/// support.
/// </para>
/// </summary>
public sealed class WorkflowsControllerVoiceoverConfigTests : IDisposable
{
    private const string InitialConfig =
        """{"source":{"kind":"Inline","lines":[{"text":"Hello there.","startSec":0}]},"defaultVoiceId":"voice-a","maxBytes":2000000}""";

    private const string UpdatedConfig =
        """{"source":{"kind":"Inline","lines":[{"text":"A different line.","startSec":1.5}]},"defaultVoiceId":"voice-b","maxBytes":500000}""";

    // The ASP.NET Core model binder's own defaults (camelCase out, case-insensitive in).
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;

    public WorkflowsControllerVoiceoverConfigTests()
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
        // A Voiceover step is deterministic — it never calls a model — but the FK to
        // agent_definitions is non-nullable, so it carries the same non-LLM placeholder row the
        // other deterministic step types use.
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

    /// <summary>
    /// A step object exactly as the workflow builder's client would serialize it. Passing
    /// <paramref name="voiceoverConfigJson"/> as null omits the member entirely, which is the shape
    /// a client sends when it has no config to give.
    /// </summary>
    private static object VoiceoverStep(Guid agentId, int stepOrder, string? voiceoverConfigJson) =>
        voiceoverConfigJson is null
            ? new { agentDefinitionId = agentId, stepOrder, label = "Narrate", stepType = nameof(StepType.Voiceover) }
            : new { agentDefinitionId = agentId, stepOrder, label = "Narrate", stepType = nameof(StepType.Voiceover), voiceoverConfigJson };

    private static CreateWorkflowRequest CreateBody(string name, object step) =>
        JsonSerializer.Deserialize<CreateWorkflowRequest>(
            JsonSerializer.Serialize(new { name, steps = new[] { step } }, WebJson), WebJson)!;

    private static UpdateWorkflowRequest UpdateBody(string? name, object step) =>
        JsonSerializer.Deserialize<UpdateWorkflowRequest>(
            JsonSerializer.Serialize(new { name, steps = new[] { step } }, WebJson), WebJson)!;

    /// <summary>
    /// The <c>voiceoverConfigJson</c> a response carries for the given step, read from the
    /// serialized JSON so that this file names no <c>WorkflowStepResponse</c> member. Null means
    /// the field was absent or explicitly null — in both cases the config did not round-trip.
    /// </summary>
    private static string? VoiceoverConfigJsonOf(object workflowResponse, int stepOrder = 1)
    {
        JsonNode root = JsonNode.Parse(JsonSerializer.Serialize(workflowResponse, WebJson))!;
        JsonNode step = root["steps"]!.AsArray().Single(s => s!["stepOrder"]!.GetValue<int>() == stepOrder)!;
        return step["voiceoverConfigJson"]?.GetValue<string>();
    }

    private static void ShouldHaveRoundTripped(string? actual, string expected, string path)
    {
        actual.Should().NotBeNullOrWhiteSpace(
            because: $"{path} came back without the Voiceover step's config — a Voiceover step saved with a null config hard-fails at execution with CONFIG_INVALID");
        JsonNode.DeepEquals(JsonNode.Parse(actual!), JsonNode.Parse(expected)).Should().BeTrue(
            because: $"{path} returned {actual} but the request carried {expected}");
    }

    [Fact]
    public async Task Create_then_read_back_roundtrips_VoiceoverConfigJson()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        WorkflowsController controller = CreateController(ownerId);

        ActionResult<WorkflowDefinitionResponse> createResult = await controller.Create(
            project.Id, CreateBody("wf", VoiceoverStep(agent.Id, 1, InitialConfig)), CancellationToken.None);

        ((ObjectResult)createResult.Result!).StatusCode.Should().Be(201);
        var created = ((ObjectResult)createResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        ShouldHaveRoundTripped(VoiceoverConfigJsonOf(created), InitialConfig, "POST /workflows");

        // The row itself must carry it: a config that only survives in the response object would
        // still be lost by the time VoiceoverStepExecutor reads it off the step.
        _db.ChangeTracker.Clear();
        WorkflowStep persisted = await _db.WorkflowSteps.AsNoTracking().SingleAsync(s => s.WorkflowDefinitionId == created.Id);
        persisted.StepType.Should().Be(StepType.Voiceover);
        ShouldHaveRoundTripped(persisted.VoiceoverConfigJson, InitialConfig, "the persisted workflow_steps row");

        ActionResult<WorkflowDefinitionResponse> getResult = await controller.Get(project.Id, created.Id, CancellationToken.None);
        var fetched = ((OkObjectResult)getResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        ShouldHaveRoundTripped(VoiceoverConfigJsonOf(fetched), InitialConfig, "GET /workflows/{id}");

        // GET list is a separate projection (an EF query lambda, not MapWorkflowResponse), so it
        // needs its own assertion — that is exactly the kind of copy-paste the field was missing from.
        ActionResult<List<WorkflowDefinitionResponse>> listResult =
            await controller.List(project.Id, CancellationToken.None);
        var listed = ((OkObjectResult)listResult.Result!).Value
            .Should().BeOfType<List<WorkflowDefinitionResponse>>().Subject;
        ShouldHaveRoundTripped(VoiceoverConfigJsonOf(listed.Single(w => w.Id == created.Id)), InitialConfig, "GET /workflows");
    }

    [Fact]
    public async Task Update_replaces_VoiceoverConfigJson()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        WorkflowsController controller = CreateController(ownerId);

        ActionResult<WorkflowDefinitionResponse> createResult = await controller.Create(
            project.Id, CreateBody("wf", VoiceoverStep(agent.Id, 1, InitialConfig)), CancellationToken.None);
        var created = ((ObjectResult)createResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;

        _db.ChangeTracker.Clear();
        ActionResult<WorkflowDefinitionResponse> updateResult = await controller.Update(
            project.Id, created.Id, UpdateBody("wf", VoiceoverStep(agent.Id, 1, UpdatedConfig)), CancellationToken.None);
        var updated = ((OkObjectResult)updateResult.Result!).Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject;
        ShouldHaveRoundTripped(VoiceoverConfigJsonOf(updated), UpdatedConfig, "PUT /workflows/{id}");

        _db.ChangeTracker.Clear();
        WorkflowStep persisted = await _db.WorkflowSteps.AsNoTracking().SingleAsync(s => s.WorkflowDefinitionId == created.Id);
        ShouldHaveRoundTripped(persisted.VoiceoverConfigJson, UpdatedConfig, "the persisted workflow_steps row after an update");
    }

    [Fact]
    public async Task Create_with_a_Voiceover_step_that_has_no_config_returns_400()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        WorkflowsController controller = CreateController(ownerId);

        // The other half of the same bug: a Voiceover step whose config never made it out of the
        // body used to be accepted here and only fail ~minutes into a real execution run.
        ActionResult<WorkflowDefinitionResponse> result = await controller.Create(
            project.Id, CreateBody("wf", VoiceoverStep(agent.Id, 2, null)), CancellationToken.None);

        BadRequestObjectResult badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonElement body = JsonSerializer.SerializeToElement(badRequest.Value, WebJson);
        body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).Should()
            .Equal("Step 2 (Voiceover): VoiceoverConfigJson is required.");

        _db.ChangeTracker.Clear();
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        (await _db.WorkflowSteps.CountAsync()).Should().Be(0);
    }
}
