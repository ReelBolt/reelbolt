using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Agents;
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
/// A VideoGenerate step whose config fails <c>VideoGenerateStepConfig.Validate()</c> must be
/// rejected by every assistant save path (the Apply endpoint, both branches, and the ApplyWorkflow
/// tool) before anything is written, and ValidateProposedSteps must report the same errors so
/// ValidateWorkflow and ApplyWorkflow never disagree. SQLite because the update branches use
/// ExecuteDelete, which the InMemory provider does not support.
/// </summary>
public sealed class WorkflowAssistantVideoGenerateValidationTests : IDisposable
{
    private const string ValidConfig =
        """{"inlinePrompt":"a red fox running through snow","durationSeconds":6,"takes":1,"maxSpendCredits":2.5}""";

    private const string ZeroSpendConfig =
        """{"inlinePrompt":"a red fox running through snow","durationSeconds":6,"takes":1,"maxSpendCredits":0}""";

    private const string ZeroSpendError = "Step 1 (VideoGenerate): MaxSpendCredits must be positive (greater than 0)";

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;

    public WorkflowAssistantVideoGenerateValidationTests()
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
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
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
        public System.Guid? OrganizationId => ReelBolt.WorkflowEngine.Tests.TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private WorkflowAssistantController CreateController(Guid ownerId)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:MaxExecutionsPerHour"] = "10" })
            .Build();

        return new WorkflowAssistantController(
            _db,
            new FakeCurrentUser(ownerId), TestTenancy.Access(_db, new FakeCurrentUser(ownerId)),
            new WorkflowEditDiffService(_db),
            new AssistantExecutionGuard(_db, config),
            new WorkflowExecutionService(_db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance));
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

    private async Task<WorkflowDefinition> SeedWorkflowAsync(Guid projectId, Guid agentId)
    {
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "wf",
            ProjectId = projectId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        workflow.Steps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = agentId,
            StepOrder = 1,
            Label = "Generate",
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = ValidConfig
        });
        _db.WorkflowDefinitions.Add(workflow);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return workflow;
    }

    private static List<CreateWorkflowStepRequest> Steps(Guid agentId, string? config) =>
    [
        new(agentId, StepOrder: 1, EdgeConditionJson: null, Label: "Generate",
            StepType: nameof(StepType.VideoGenerate), VideoGenerateConfigJson: config)
    ];

    private string TokenFor(Guid? workflowId, IReadOnlyList<CreateWorkflowStepRequest> steps)
    {
        List<WorkflowStep> current = workflowId is null
            ? []
            : _db.WorkflowSteps.AsNoTracking().Where(s => s.WorkflowDefinitionId == workflowId).ToList();
        return new WorkflowEditDiffService(_db).ComputeDiff(workflowId, current, steps).ConfirmationToken;
    }

    private static string[] ReadErrors(IActionResult? result, out string message)
    {
        BadRequestObjectResult badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonElement body = JsonSerializer.SerializeToElement(badRequest.Value);
        message = body.GetProperty("message").GetString()!;
        return body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    private void AssertStoredConfigIsStillValid(Guid workflowId)
    {
        _db.ChangeTracker.Clear();
        WorkflowDefinition stored = _db.WorkflowDefinitions.AsNoTracking().Single(w => w.Id == workflowId);
        stored.Name.Should().Be("wf");
        WorkflowStep step = _db.WorkflowSteps.AsNoTracking().Single(s => s.WorkflowDefinitionId == workflowId);
        JsonNode.DeepEquals(JsonNode.Parse(step.VideoGenerateConfigJson!), JsonNode.Parse(ValidConfig)).Should().BeTrue();
    }

    [Fact]
    public async Task Apply_create_with_MaxSpendCredits_zero_returns_400_and_writes_nothing()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        List<CreateWorkflowStepRequest> steps = Steps(agent.Id, ZeroSpendConfig);
        ApplyWorkflowRequest request = new(null, "newwf", steps, TokenFor(null, steps));

        var result = await CreateController(ownerId).Apply(project.Id, request, CancellationToken.None);

        string[] errors = ReadErrors(result.Result, out string message);
        errors.Should().Equal(ZeroSpendError);
        message.Should().Be(ZeroSpendError);
        _db.ChangeTracker.Clear();
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        (await _db.WorkflowSteps.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Apply_update_with_MaxSpendCredits_zero_returns_400_and_leaves_workflow_unchanged()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        WorkflowDefinition workflow = await SeedWorkflowAsync(project.Id, agent.Id);
        List<CreateWorkflowStepRequest> steps = Steps(agent.Id, ZeroSpendConfig);
        ApplyWorkflowRequest request = new(workflow.Id, "renamed", steps, TokenFor(workflow.Id, steps));

        var result = await CreateController(ownerId).Apply(project.Id, request, CancellationToken.None);

        ReadErrors(result.Result, out _).Should().Equal(ZeroSpendError);
        AssertStoredConfigIsStillValid(workflow.Id);
    }

    [Fact]
    public async Task Apply_create_with_valid_config_still_returns_201()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        List<CreateWorkflowStepRequest> steps = Steps(agent.Id, ValidConfig);
        ApplyWorkflowRequest request = new(null, "newwf", steps, TokenFor(null, steps));

        var result = await CreateController(ownerId).Apply(project.Id, request, CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task ValidateProposedSteps_reports_the_same_error_Apply_rejects_with()
    {
        (_, Project project, AgentDefinition agent) = await SeedAsync();

        WorkflowValidationResult validation = new WorkflowEditDiffService(_db)
            .ValidateProposedSteps(project.Id, Steps(agent.Id, ZeroSpendConfig));

        validation.Valid.Should().BeFalse();
        validation.Errors.Should().Contain(ZeroSpendError);
    }

    [Fact]
    public async Task ValidateProposedSteps_treats_a_blank_VideoGenerate_config_as_an_error()
    {
        (_, Project project, AgentDefinition agent) = await SeedAsync();
        var service = new WorkflowEditDiffService(_db);

        service.ValidateProposedSteps(project.Id, Steps(agent.Id, null)).Valid.Should().BeFalse();
        service.ValidateProposedSteps(project.Id, Steps(agent.Id, ValidConfig)).Valid.Should().BeTrue();
        // A non-VideoGenerate step is unaffected by the VideoGenerate rules — provided it carries its
        // own required config (a VideoCompile step with none is now an error of its own, since its
        // executor hard-fails it at run time).
        service.ValidateProposedSteps(project.Id,
            [new(agent.Id, 1, null, "Compile", StepType: nameof(StepType.VideoCompile),
                VideoCompileConfigJson: """{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1}""")])
            .Valid.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyWorkflow_tool_returns_an_error_and_writes_nothing()
    {
        (Guid ownerId, Project project, AgentDefinition agent) = await SeedAsync();
        WorkflowDefinition workflow = await SeedWorkflowAsync(project.Id, agent.Id);
        var provider = AssistantToolTestFactory.CreateProvider(_db, new FakeCurrentUser(ownerId));
        AIFunction apply = provider.GetTools(new AssistantToolContext(ownerId, project.Id))
            .Single(t => t.Name == "ApplyWorkflow");

        foreach (Guid? workflowId in new Guid?[] { null, workflow.Id })
        {
            List<CreateWorkflowStepRequest> steps = Steps(agent.Id, ZeroSpendConfig);
            object? raw = await apply.InvokeAsync(new AIFunctionArguments
            {
                ["workflowId"] = workflowId,
                ["name"] = "renamed",
                ["steps"] = steps,
                ["confirmationToken"] = TokenFor(workflowId, steps)
            });

            string text = raw is JsonElement el && el.ValueKind == JsonValueKind.String ? el.GetString()! : raw!.ToString()!;
            using JsonDocument doc = JsonDocument.Parse(text);
            doc.RootElement.GetProperty("error").GetString().Should().Contain(ZeroSpendError);
            doc.RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).Should().Equal(ZeroSpendError);
        }

        _db.ChangeTracker.Clear();
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(1);
        AssertStoredConfigIsStillValid(workflow.Id);
    }
}
