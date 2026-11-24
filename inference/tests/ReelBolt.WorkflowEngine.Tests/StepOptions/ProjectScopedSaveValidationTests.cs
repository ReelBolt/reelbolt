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
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.StepOptions;

/// <summary>
/// T-014: every workflow save path scopes the two cross-run references to the workflow's PROJECT, not
/// merely its organization. A <c>continueCompositionJson</c> run and a <c>StepResult</c> video source that
/// belong to a sibling project of the same organization are refused, with one answer for "another project"
/// and "does not exist", and nothing is written. The MCP bridge path lives in
/// <c>AssistantToolBridgeControllerTests</c> (it needs that file's request-scope harness).
/// </summary>
public sealed class ProjectScopedSaveValidationTests : IDisposable
{
    private static readonly Guid Org = Guid.Parse("11111111-0000-0000-0000-0000000000c4");

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();

    public ProjectScopedSaveValidationTests()
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

    public const string Continue = "continue";
    public const string StepResult = "stepResult";
    /// <summary>T-106: a VideoCompile insertContentRef of From=StepResult.</summary>
    public const string InsertRef = "insertRef";

    // --- REST ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Continue)]
    [InlineData(StepResult)]
    [InlineData(InsertRef)]
    public async Task Rest_create_refuses_a_sibling_projects_source_and_writes_nothing(string kind)
    {
        World w = await SeedAsync();

        ActionResult<WorkflowDefinitionResponse> result = await Controller().Create(
            w.Project.Id, new CreateWorkflowRequest("wf", [w.SiblingStep(kind)]), CancellationToken.None);

        ErrorsOf(result.Result).Should().Contain(NotFoundMarker(kind));
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0, "nothing may be persisted for a refused save");
    }

    [Theory]
    [InlineData(Continue)]
    [InlineData(StepResult)]
    [InlineData(InsertRef)]
    public async Task Rest_update_refuses_a_sibling_projects_source_and_leaves_the_stored_step_untouched(string kind)
    {
        World w = await SeedAsync();
        ActionResult<WorkflowDefinitionResponse> created = await Controller().Create(
            w.Project.Id, new CreateWorkflowRequest("wf", [w.OwnStep(kind)]), CancellationToken.None);
        WorkflowDefinitionResponse workflow = (WorkflowDefinitionResponse)((ObjectResult)created.Result!).Value!;
        _db.ChangeTracker.Clear();

        ActionResult<WorkflowDefinitionResponse> result = await Controller().Update(
            w.Project.Id, workflow.Id, new UpdateWorkflowRequest("wf", [w.SiblingStep(kind)]), CancellationToken.None);

        ErrorsOf(result.Result).Should().Contain(NotFoundMarker(kind));
        _db.ChangeTracker.Clear();
        WorkflowStep stored = await _db.WorkflowSteps.SingleAsync();
        (stored.ContinueCompositionJson + stored.VideoAnalyzeConfigJson + stored.VideoCompileConfigJson).Should().NotContain(w.SiblingId(kind).ToString());
    }

    [Theory]
    [InlineData(Continue)]
    [InlineData(StepResult)]
    [InlineData(InsertRef)]
    public async Task Rest_create_accepts_the_projects_own_source(string kind)
    {
        World w = await SeedAsync();

        ActionResult<WorkflowDefinitionResponse> result = await Controller().Create(
            w.Project.Id, new CreateWorkflowRequest("wf", [w.OwnStep(kind)]), CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201, JsonSerializer.Serialize(((ObjectResult)result.Result!).Value));
    }

    [Fact]
    public async Task A_sibling_projects_step_result_and_a_missing_one_get_the_same_answer()
    {
        World w = await SeedAsync();

        string foreign = ErrorsOf((await Controller().Create(
            w.Project.Id, new CreateWorkflowRequest("wf", [AnalyzeStep(w.Author.Id, w.SiblingResult.Id)]), CancellationToken.None)).Result);
        Guid missingId = Guid.NewGuid();
        string missing = ErrorsOf((await Controller().Create(
            w.Project.Id, new CreateWorkflowRequest("wf", [AnalyzeStep(w.Author.Id, missingId)]), CancellationToken.None)).Result);

        foreign.Should().Contain("not found in this project");
        foreign.Replace(w.SiblingResult.Id.ToString(), "<id>").Should().Be(missing.Replace(missingId.ToString(), "<id>"));
    }

    [Theory]
    [InlineData(StepResult)]
    [InlineData(InsertRef)]
    public async Task ValidateProposedSteps_refuses_a_sibling_projects_step_result(string kind)
    {
        World w = await SeedAsync();

        WorkflowValidationResult result = new WorkflowEditDiffService(_db)
            .ValidateProposedSteps(w.Project.Id, [w.SiblingStep(kind)]);

        result.Valid.Should().BeFalse();
        result.Errors.Should().ContainMatch("*not found in this project*");
    }

    // --- WorkflowAssistantController.Apply ---------------------------------------------------------

    [Theory]
    [InlineData(Continue)]
    [InlineData(StepResult)]
    [InlineData(InsertRef)]
    public async Task Assistant_controller_apply_refuses_a_sibling_projects_source_and_writes_nothing(string kind)
    {
        World w = await SeedAsync();
        WorkflowAssistantController controller = AssistantController();
        List<CreateWorkflowStepRequest> steps = [w.SiblingStep(kind)];
        ActionResult<WorkflowEditDiff> proposed = await controller.Propose(
            w.Project.Id, new ProposeWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps), CancellationToken.None);
        WorkflowEditDiff diff = (WorkflowEditDiff)((OkObjectResult)proposed.Result!).Value!;

        ActionResult<WorkflowAssistantApplyResponse> result = await controller.Apply(
            w.Project.Id,
            new ApplyWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps, ConfirmationToken: diff.ConfirmationToken),
            CancellationToken.None);

        ErrorsOf(result.Result).Should().Contain(NotFoundMarker(kind));
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    // --- assistant chat ApplyWorkflow tool ---------------------------------------------------------

    [Theory]
    [InlineData(Continue)]
    [InlineData(StepResult)]
    [InlineData(InsertRef)]
    public async Task Assistant_chat_ApplyWorkflow_refuses_a_sibling_projects_source_and_writes_nothing(string kind)
    {
        World w = await SeedAsync();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(_db, new FakeCurrentUser(_userId, Org));
        var context = new AssistantToolContext(_userId, w.Project.Id);
        List<CreateWorkflowStepRequest> steps = [w.SiblingStep(kind)];

        string proposal = await InvokeAsync(provider, context, "ProposeWorkflow", ("workflowId", null), ("name", "wf"), ("steps", steps));
        string token = JsonDocument.Parse(proposal).RootElement.GetProperty("confirmationToken").GetString()!;
        string applied = await InvokeAsync(provider, context, "ApplyWorkflow",
            ("workflowId", null), ("name", "wf"), ("steps", steps), ("confirmationToken", token));

        using JsonDocument body = JsonDocument.Parse(applied);
        body.RootElement.TryGetProperty("workflowId", out _).Should().BeFalse(applied);
        body.RootElement.GetProperty("error").GetString().Should().Contain(NotFoundMarker(kind));
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0, "a refused apply writes nothing");
    }

    // ------------------------------------------------------------------------------------------

    private static string NotFoundMarker(string kind) =>
        kind == Continue ? StepOptionsSaveValidator.NotFoundCode : "not found in this project";

    private static string ErrorsOf(ActionResult? result)
    {
        BadRequestObjectResult bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(bad.Value));
        body.RootElement.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        return string.Join("\n", body.RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
    }

    private static string Text(object? raw) => raw switch
    {
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
        JsonElement e => e.GetRawText(),
        _ => raw?.ToString() ?? string.Empty
    };

    private static async Task<string> InvokeAsync(AssistantToolProvider provider, AssistantToolContext context, string tool, params (string, object?)[] args)
    {
        AIFunction function = provider.GetTools(context).Single(t => t.Name == tool);
        var arguments = new AIFunctionArguments();
        foreach ((string key, object? value) in args)
            arguments[key] = value;
        return Text(await function.InvokeAsync(arguments));
    }

    internal static CreateWorkflowStepRequest AnalyzeStep(Guid agentId, Guid stepResultId) =>
        new(agentId, 1, null, "Analyze", StepType: nameof(StepType.VideoAnalyze),
            VideoAnalyzeConfigJson: $$$"""{"version":1,"source":{"kind":"StepResult","stepResultId":"{{{stepResultId}}}"}}""");

    internal static CreateWorkflowStepRequest CompileStep(Guid agentId, Guid stepResultId) =>
        new(agentId, 1, null, "Compile", StepType: nameof(StepType.VideoCompile),
            VideoCompileConfigJson: $$$"""{"version":1,"decision":{"source":"Previous"},"insertContents":[{"insertContentRef":{"from":"StepResult","stepResultId":"{{{stepResultId}}}"}}]}""");

    internal static CreateWorkflowStepRequest AuthorStep(Guid agentId, Guid runId) =>
        new(agentId, 1, null, "Author", StepType: nameof(StepType.Agent),
            ContinueCompositionJson: $$"""{"executionId":"{{runId}}"}""");

    private sealed class FakeCurrentUser(Guid userId, Guid organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = OrganizationRole.Owner;
    }

    private WorkflowsController Controller()
    {
        var user = new FakeCurrentUser(_userId, Org);
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
        var user = new FakeCurrentUser(_userId, Org);
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

    private sealed record World(
        Project Project,
        AgentDefinition Author,
        WorkflowExecution OwnRun,
        WorkflowStepResult OwnResult,
        WorkflowExecution SiblingRun,
        WorkflowStepResult SiblingResult)
    {
        public Guid SiblingId(string kind) => kind == Continue ? SiblingRun.Id : SiblingResult.Id;

        public CreateWorkflowStepRequest SiblingStep(string kind) =>
            kind == Continue ? AuthorStep(Author.Id, SiblingRun.Id)
            : kind == InsertRef ? CompileStep(Author.Id, SiblingResult.Id)
            : AnalyzeStep(Author.Id, SiblingResult.Id);

        public CreateWorkflowStepRequest OwnStep(string kind) =>
            kind == Continue ? AuthorStep(Author.Id, OwnRun.Id)
            : kind == InsertRef ? CompileStep(Author.Id, OwnResult.Id)
            : AnalyzeStep(Author.Id, OwnResult.Id);
    }

    private async Task<World> SeedAsync()
    {
        Project project = NewProject();
        Project sibling = NewProject();
        AgentDefinition author = new()
        {
            Id = Guid.NewGuid(),
            Name = "Author",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };

        WorkflowExecution own = NewRun(project);
        WorkflowExecution siblingRun = NewRun(sibling);
        WorkflowStepResult ownResult = NewResult(own);
        WorkflowStepResult siblingResult = NewResult(siblingRun);

        _db.Projects.AddRange(project, sibling);
        _db.AgentDefinitions.Add(author);
        _db.WorkflowExecutions.AddRange(own, siblingRun);
        _db.WorkflowStepResults.AddRange(ownResult, siblingResult);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        return new World(project, author, own, ownResult, siblingRun, siblingResult);
    }

    private Project NewProject() => new()
    {
        Id = Guid.NewGuid(),
        Name = "p",
        OwnerId = _userId,
        OrganizationId = Org,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static WorkflowExecution NewRun(Project project) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = Guid.NewGuid(),
        ProjectId = project.Id,
        OrganizationId = project.OrganizationId,
        Status = ExecutionStatus.Passed
    };

    private static WorkflowStepResult NewResult(WorkflowExecution run) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowExecutionId = run.Id,
        WorkflowStepId = Guid.NewGuid(),
        OutputStorageKey = $"projects/{run.ProjectId}/outputFiles/{run.Id:D}/film.mp4",
        Status = StepStatus.Completed
    };
}
