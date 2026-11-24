using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// T-202: the three run-start surfaces (REST execute, assistant/execute, the ExecuteWorkflow tool) accept an
/// optional briefFileId, hand the resolved text to the execution as its userRequest, and refuse a foreign,
/// missing, binary or oversized file once, before anything is queued. A retry reuses the stored text.
/// </summary>
public class BriefFileRunTests : IDisposable
{
    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase("brief-run-" + Guid.NewGuid().ToString("N")).Options);

    private readonly FakeFileStorage _storage = new();
    private readonly Guid _user = Guid.NewGuid();
    private readonly Project _project;
    private readonly Project _foreign;
    private readonly Guid _workflow = Guid.NewGuid();
    private readonly Mock<IWorkflowExecutionService> _executions = new();
    private readonly List<string?> _queued = [];

    public BriefFileRunTests()
    {
        _project = NewProject(_user);
        Guid stranger = Guid.NewGuid();
        _foreign = NewProject(stranger);
        _db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = _workflow, Name = "Film", ProjectId = _project.Id });
        _db.SaveChanges();
        _executions
            .Setup(s => s.ExecuteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<ExecutionInitiator>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>()))
            .ReturnsAsync((Guid p, Guid w, Guid u, string? req, ExecutionInitiator i, CancellationToken _, bool _, RunChoices? _) =>
            {
                _queued.Add(req);
                return new WorkflowExecutionResponse(Guid.NewGuid(), w, "Queued", DateTime.UtcNow, null, 0, null, null, null, [], [], req);
            });
    }

    private Project NewProject(Guid owner)
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Projects.Add(project);
        _db.SaveChanges();
        return project;
    }

    private Guid File(Project project, string name, string mime, byte[] content)
    {
        Guid id = Guid.NewGuid();
        string key = $"projects/{project.Id}/userFiles/{id}";
        _storage.Objects[key] = content;
        _db.ProjectFiles.Add(new ProjectFile { Id = id, ProjectId = project.Id, OriginalFileName = name, MimeType = mime, StorageKey = key, SizeBytes = content.Length });
        _db.SaveChanges();
        return id;
    }

    private Guid Brief(string text = "Launch film for Acme.") => File(_project, "brief.md", "text/markdown", Encoding.UTF8.GetBytes(text));

    private BriefFileResolver Resolver() => new(_db, _storage);

    private WorkflowAssistantController AssistantController()
    {
        var user = new FakeCurrentUser(_user);
        return new WorkflowAssistantController(
            _db, user, TestTenancy.Access(_db, user), new WorkflowEditDiffService(_db),
            new AssistantExecutionGuard(_db, new ConfigurationBuilder().Build()), _executions.Object, briefFiles: Resolver())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private WorkflowsController RestController(IWorkflowExecutionService? executions = null)
    {
        var user = new FakeCurrentUser(_user);
        return new WorkflowsController(
            _db, user, TestTenancy.Access(_db, user), Mock.Of<MassTransit.IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(_db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            executions ?? _executions.Object, NullLogger<WorkflowsController>.Instance, briefFiles: Resolver())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private async Task<JsonElement> ToolAsync(Guid? briefFileId, string? userRequest = null)
    {
        var user = new FakeCurrentUser(_user);
        var guard = new AssistantExecutionGuard(_db, new ConfigurationBuilder().Build());
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(
            _db, user, executionGuard: guard, executionService: _executions.Object, briefFiles: Resolver());
        AIFunction tool = provider.GetTools(new AssistantToolContext(_user, _project.Id)).Single(t => t.Name == "ExecuteWorkflow");
        var args = new AIFunctionArguments { ["workflowId"] = _workflow, ["regenerateVideoClips"] = false };
        if (userRequest is not null) args["userRequest"] = userRequest;
        if (briefFileId is not null) args["briefFileId"] = briefFileId;
        object? raw = await tool.InvokeAsync(args);
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString()! : raw!.ToString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public void The_rest_dto_carries_an_appended_optional_brief_file_id()
    {
        new ExecuteWorkflowRequest().BriefFileId.Should().BeNull();
        // Append-only record: BriefFileId was appended before the run brief (`Brief`), which is now last.
        var parameters = typeof(ExecuteWorkflowRequest).GetConstructors().Single().GetParameters();
        parameters[^1].Name.Should().Be("Brief");
        parameters[^2].Name.Should().Be("BriefFileId");
    }

    [Fact]
    public async Task Rest_execute_runs_with_the_file_text_as_the_brief()
    {
        Guid file = Brief();

        ActionResult<WorkflowExecutionResponse> result = await RestController().Execute(_project.Id, _workflow, new ExecuteWorkflowRequest(BriefFileId: file), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
        _queued.Should().ContainSingle().Which.Should().Be("Launch film for Acme.");
    }

    [Fact]
    public async Task Rest_execute_appends_the_request_as_notes_after_the_file()
    {
        Guid file = Brief("The brief.");

        await RestController().Execute(_project.Id, _workflow, new ExecuteWorkflowRequest(UserRequest: "Make it punchy.", BriefFileId: file), CancellationToken.None);

        _queued.Should().ContainSingle().Which.Should().Be("The brief.\n\nNotes for this run:\nMake it punchy.");
    }

    [Fact]
    public async Task Rest_execute_refuses_a_foreign_file_with_400_and_queues_nothing()
    {
        Guid foreign = File(_foreign, "theirs.txt", "text/plain", Encoding.UTF8.GetBytes("secret"));

        ActionResult<WorkflowExecutionResponse> result = await RestController().Execute(_project.Id, _workflow, new ExecuteWorkflowRequest(BriefFileId: foreign), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _queued.Should().BeEmpty();
    }

    [Fact]
    public async Task Rest_execute_refuses_a_binary_file()
    {
        Guid png = File(_project, "logo.png", "image/png", [0x89, 0x50]);

        ActionResult<WorkflowExecutionResponse> result = await RestController().Execute(_project.Id, _workflow, new ExecuteWorkflowRequest(BriefFileId: png), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _queued.Should().BeEmpty();
    }

    [Fact]
    public async Task Rest_execute_refuses_a_file_over_the_cap_naming_it()
    {
        Guid big = File(_project, "big.txt", "text/plain", new byte[BriefFileResolver.MaxBytes + 1]);

        ActionResult<WorkflowExecutionResponse> result = await RestController().Execute(_project.Id, _workflow, new ExecuteWorkflowRequest(BriefFileId: big), CancellationToken.None);

        JsonSerializer.Serialize(result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value).Should().Contain("64 KiB");
        _queued.Should().BeEmpty();
    }

    [Fact]
    public async Task Assistant_execute_runs_with_the_file_text_and_refuses_a_foreign_one()
    {
        Guid file = Brief();
        Guid foreign = File(_foreign, "theirs.txt", "text/plain", Encoding.UTF8.GetBytes("secret"));

        ActionResult<WorkflowExecutionResponse> ok = await AssistantController().Execute(_project.Id, _workflow, new ExecuteWorkflowRequest(BriefFileId: file), CancellationToken.None);
        ActionResult<WorkflowExecutionResponse> refused = await AssistantController().Execute(_project.Id, _workflow, new ExecuteWorkflowRequest(BriefFileId: foreign), CancellationToken.None);

        ok.Result.Should().NotBeOfType<BadRequestObjectResult>();
        refused.Result.Should().BeOfType<BadRequestObjectResult>();
        _queued.Should().ContainSingle().Which.Should().Be("Launch film for Acme.");
    }

    [Fact]
    public async Task The_tool_runs_with_the_file_text_and_appends_notes()
    {
        Guid file = Brief("The brief.");

        JsonElement result = await ToolAsync(file, userRequest: "Make it punchy.");

        result.TryGetProperty("error", out _).Should().BeFalse(result.GetRawText());
        _queued.Should().ContainSingle().Which.Should().Be("The brief.\n\nNotes for this run:\nMake it punchy.");
    }

    [Fact]
    public async Task The_tool_refuses_a_foreign_file_once_and_queues_nothing()
    {
        Guid foreign = File(_foreign, "theirs.txt", "text/plain", Encoding.UTF8.GetBytes("secret"));

        JsonElement result = await ToolAsync(foreign);

        result.GetProperty("error").GetString().Should().Be(BriefFileResolver.NotFound);
        result.GetRawText().Should().NotContain("secret");
        _queued.Should().BeEmpty();
        _db.RunRateEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retry_without_a_body_keeps_the_stored_brief_text()
    {
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = _workflow,
            ProjectId = _project.Id,
            OrganizationId = _project.OrganizationId,
            Status = ExecutionStatus.Failed,
            UserRequest = "Launch film for Acme.",
            InitiatedByUserId = _user,
        };
        _db.WorkflowExecutions.Add(execution);
        _db.SaveChanges();
        var real = new WorkflowExecutionService(_db, Mock.Of<MassTransit.IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance);

        await RestController(real).RetryExecution(_project.Id, _workflow, execution.Id, null, CancellationToken.None);

        (await _db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == execution.Id)).UserRequest.Should().Be("Launch film for Acme.");
    }

    public void Dispose() => _db.Dispose();
}
