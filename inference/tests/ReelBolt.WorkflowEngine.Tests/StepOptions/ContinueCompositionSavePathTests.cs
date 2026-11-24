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

namespace ReelBolt.WorkflowEngine.Tests.StepOptions;

/// <summary>
/// The save-time half of the two step options. <c>continueCompositionJson</c> is authorization
/// sensitive: a source run or version outside the workflow's own project is refused with ONE answer
/// whether it belongs to someone else or does not exist (no existence oracle), on every save path.
/// <c>registerOutputAsProjectFile</c> is only accepted on a step that produces a playable output.
/// </summary>
public sealed class ContinueCompositionSavePathTests : IDisposable
{
    private static readonly Guid Org = Guid.Parse("11111111-0000-0000-0000-0000000000a1");
    private static readonly Guid OtherOrg = Guid.Parse("33333333-0000-0000-0000-0000000000b3");

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();

    public ContinueCompositionSavePathTests()
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

    // --- continueCompositionJson ------------------------------------------------------------------

    [Fact]
    public async Task Create_accepts_an_authoring_agent_continuing_a_run_of_the_same_project_and_returns_both_fields()
    {
        World w = await SeedAsync();

        ActionResult<WorkflowDefinitionResponse> result = await Controller().Create(
            w.Project.Id, new CreateWorkflowRequest("wf", [AuthorStep(w.Author.Id, Exec(w.OwnRun.Id), register: true)]), CancellationToken.None);

        ObjectResult created = (ObjectResult)result.Result!;
        created.StatusCode.Should().Be(201);
        WorkflowStepResponse step = created.Value.Should().BeOfType<WorkflowDefinitionResponse>().Subject.Steps.Single();
        step.ContinueCompositionJson.Should().Contain(w.OwnRun.Id.ToString());
        step.RegisterOutputAsProjectFile.Should().BeTrue();

        _db.ChangeTracker.Clear();
        WorkflowStep stored = await _db.WorkflowSteps.SingleAsync();
        stored.ContinueCompositionJson.Should().Contain(w.OwnRun.Id.ToString());
        stored.RegisterOutputAsProjectFile.Should().BeTrue();
    }

    [Fact]
    public async Task Create_accepts_a_version_of_the_same_project()
    {
        World w = await SeedAsync();

        ActionResult<WorkflowDefinitionResponse> result = await Controller().Create(
            w.Project.Id, new CreateWorkflowRequest("wf", [AuthorStep(w.Author.Id, Version(w.OwnVersion.Id))]), CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task Create_refuses_a_run_of_another_project_with_the_same_answer_as_a_missing_run()
    {
        World w = await SeedAsync();

        string foreign = await RefusalAsync(w, AuthorStep(w.Author.Id, Exec(w.SiblingProjectRun.Id)));
        string otherTenant = await RefusalAsync(w, AuthorStep(w.Author.Id, Exec(w.OtherOrgRun.Id)));
        string missing = await RefusalAsync(w, AuthorStep(w.Author.Id, Exec(Guid.NewGuid())));

        foreign.Should().Contain("CONTINUE_COMPOSITION_NOT_FOUND");
        otherTenant.Should().Be(foreign, "another tenant's run must be indistinguishable from a missing one");
        missing.Should().Be(foreign, "a missing run must be indistinguishable from a foreign one");
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0, "nothing may be persisted for a refused save");
    }

    [Fact]
    public async Task Create_refuses_a_version_of_another_project_with_the_same_answer_as_a_missing_version()
    {
        World w = await SeedAsync();

        string foreign = await RefusalAsync(w, AuthorStep(w.Author.Id, Version(w.OtherOrgVersion.Id)));
        string missing = await RefusalAsync(w, AuthorStep(w.Author.Id, Version(Guid.NewGuid())));

        foreign.Should().Contain("CONTINUE_COMPOSITION_NOT_FOUND");
        missing.Should().Be(foreign);
    }

    [Fact]
    public async Task Create_refuses_continue_composition_on_an_agent_without_sandbox_authoring()
    {
        World w = await SeedAsync();

        string refusal = await RefusalAsync(w, AuthorStep(w.Reviewer.Id, Exec(w.OwnRun.Id)));

        refusal.Should().Contain("CONTINUE_COMPOSITION_REQUIRES_AUTHORING_AGENT");
    }

    [Fact]
    public async Task Create_refuses_continue_composition_on_a_non_agent_step()
    {
        World w = await SeedAsync();
        CreateWorkflowStepRequest compile = new(w.Author.Id, 1, null, "Compile", StepType: nameof(StepType.VideoCompile),
            VideoCompileConfigJson: "{}", ContinueCompositionJson: Exec(w.OwnRun.Id));

        string refusal = await RefusalAsync(w, compile);

        refusal.Should().Contain("CONTINUE_COMPOSITION_REQUIRES_AUTHORING_AGENT");
    }

    [Fact]
    public async Task Create_refuses_malformed_continue_composition_json()
    {
        World w = await SeedAsync();

        string refusal = await RefusalAsync(w, AuthorStep(w.Author.Id, "{\"executionId\":null,\"versionId\":null}"));

        refusal.Should().Contain("exactly one");
    }

    [Fact]
    public async Task Update_refuses_a_foreign_run_and_leaves_the_stored_step_untouched()
    {
        World w = await SeedAsync();
        ActionResult<WorkflowDefinitionResponse> created = await Controller().Create(
            w.Project.Id, new CreateWorkflowRequest("wf", [AuthorStep(w.Author.Id, Exec(w.OwnRun.Id))]), CancellationToken.None);
        WorkflowDefinitionResponse workflow = (WorkflowDefinitionResponse)((ObjectResult)created.Result!).Value!;

        ActionResult<WorkflowDefinitionResponse> result = await Controller().Update(
            w.Project.Id, workflow.Id, new UpdateWorkflowRequest("wf", [AuthorStep(w.Author.Id, Exec(w.SiblingProjectRun.Id))]), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _db.ChangeTracker.Clear();
        (await _db.WorkflowSteps.SingleAsync()).ContinueCompositionJson.Should().Contain(w.OwnRun.Id.ToString());
    }

    [Fact]
    public async Task ValidateProposedSteps_refuses_another_projects_run()
    {
        World w = await SeedAsync();

        WorkflowValidationResult result = new WorkflowEditDiffService(_db)
            .ValidateProposedSteps(w.Project.Id, [AuthorStep(w.Author.Id, Exec(w.SiblingProjectRun.Id))]);

        result.Valid.Should().BeFalse();
        result.Errors.Should().ContainMatch("*CONTINUE_COMPOSITION_NOT_FOUND*");
    }

    [Fact]
    public async Task ValidateProposedSteps_accepts_the_projects_own_run()
    {
        World w = await SeedAsync();

        WorkflowValidationResult result = new WorkflowEditDiffService(_db)
            .ValidateProposedSteps(w.Project.Id, [AuthorStep(w.Author.Id, Exec(w.OwnRun.Id))]);

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Assistant_apply_refuses_another_tenants_run()
    {
        World w = await SeedAsync();
        WorkflowAssistantController controller = AssistantController();
        List<CreateWorkflowStepRequest> steps = [AuthorStep(w.Author.Id, Exec(w.OtherOrgRun.Id))];

        ActionResult<WorkflowEditDiff> proposed = await controller.Propose(
            w.Project.Id, new ProposeWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps), CancellationToken.None);
        WorkflowEditDiff diff = (WorkflowEditDiff)((OkObjectResult)proposed.Result!).Value!;

        ActionResult<WorkflowAssistantApplyResponse> result = await controller.Apply(
            w.Project.Id,
            new ApplyWorkflowRequest(WorkflowId: null, Name: "wf", Steps: steps, ConfirmationToken: diff.ConfirmationToken),
            CancellationToken.None);

        BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("CONTINUE_COMPOSITION_NOT_FOUND");
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    // --- registerOutputAsProjectFile --------------------------------------------------------------

    [Fact]
    public async Task Register_output_is_refused_on_a_step_that_produces_no_playable_output()
    {
        World w = await SeedAsync();
        CreateWorkflowStepRequest extract = new(w.Author.Id, 1, null, "Extract", StepType: nameof(StepType.Extract),
            ExtractConfigJson: "{}", RegisterOutputAsProjectFile: true);

        string refusal = await RefusalAsync(w, extract);

        refusal.Should().Contain("REGISTER_OUTPUT_REQUIRES_PRODUCING_STEP");
    }

    [Fact]
    public async Task Register_output_is_refused_on_an_agent_that_does_not_render()
    {
        World w = await SeedAsync();
        CreateWorkflowStepRequest review = new(w.Reviewer.Id, 1, null, "Review", StepType: nameof(StepType.Agent),
            RegisterOutputAsProjectFile: true);

        string refusal = await RefusalAsync(w, review);

        refusal.Should().Contain("REGISTER_OUTPUT_REQUIRES_PRODUCING_STEP");
    }

    [Theory]
    [InlineData(nameof(StepType.VideoGenerate))]
    [InlineData(nameof(StepType.TimelineRender))]
    [InlineData(nameof(StepType.VideoCompile))]
    public void Register_output_is_accepted_by_the_static_rules_on_every_producing_step_type(string stepType)
    {
        CreateWorkflowStepRequest step = new(Guid.NewGuid(), 1, null, "Out", StepType: stepType, RegisterOutputAsProjectFile: true);

        StepConfigSaveValidator.ValidateStep(step).Should().NotContainMatch("*REGISTER_OUTPUT_REQUIRES_PRODUCING_STEP*");
    }

    [Fact]
    public async Task Register_output_is_accepted_on_a_rendering_agent()
    {
        World w = await SeedAsync();
        CreateWorkflowStepRequest author = new(w.Author.Id, 1, null, "Author", StepType: nameof(StepType.Agent),
            RegisterOutputAsProjectFile: true);

        ActionResult<WorkflowDefinitionResponse> result = await Controller().Create(
            w.Project.Id, new CreateWorkflowRequest("wf", [author]), CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
    }

    // ------------------------------------------------------------------------------------------

    private static string Exec(Guid id) => $$"""{"executionId":"{{id}}"}""";

    private static string Version(Guid id) => $$"""{"versionId":"{{id}}"}""";

    private static CreateWorkflowStepRequest AuthorStep(Guid agentId, string continueJson, bool register = false) =>
        new(agentId, 1, null, "Author", StepType: nameof(StepType.Agent),
            ContinueCompositionJson: continueJson, RegisterOutputAsProjectFile: register);

    private static CreateWorkflowStepRequest SeededAnalyze(Guid agentId, string target) =>
        new(agentId, 1, null, "Analyze", StepType: nameof(StepType.VideoAnalyze),
            VideoAnalyzeConfigJson: "{\"version\":1,\"source\":{\"kind\":\"PreviousStepOutput\"},\"allowSourceMediaEgress\":true,\"trackObjects\":[" + target + "]}");

    [Theory]
    [InlineData("{\"label\":\"x\",\"method\":\"Segmentation\",\"seedPoint\":{\"x\":0.5,\"y\":0.5}}")]
    [InlineData("{\"label\":\"x\",\"method\":\"Segmentation\",\"noWords\":true}")]
    public async Task A_workflow_you_author_cannot_carry_a_tapped_target_on_REST_or_the_assistant_path(string target)
    {
        World w = await SeedAsync();
        CreateWorkflowStepRequest step = SeededAnalyze(w.Author.Id, target);

        (await RefusalAsync(w, step)).Should().Contain("tap");

        new WorkflowEditDiffService(_db).ValidateProposedSteps(w.Project.Id, [step]).Errors
            .Should().Contain(e => e.Contains("tap"), "the assistant's propose/apply path runs the same save rules");
        (await _db.WorkflowDefinitions.CountAsync()).Should().Be(0);

        StepConfigSaveValidator.ValidateStep(step, allowSeededTargets: true).Should().NotContain(e => e.Contains("tap"), "the hidden object-analysis build is the one place it is allowed");
    }

    [Fact]
    public async Task An_untapped_target_still_saves()
    {
        World w = await SeedAsync();
        StepConfigSaveValidator.ValidateStep(SeededAnalyze(w.Author.Id, "{\"label\":\"x\",\"method\":\"Segmentation\",\"noWords\":false}"))
            .Should().NotContain(e => e.Contains("tap"));
    }

    private async Task<string> RefusalAsync(World w, CreateWorkflowStepRequest step)
    {
        ActionResult<WorkflowDefinitionResponse> result = await Controller().Create(
            w.Project.Id, new CreateWorkflowRequest("wf", [step]), CancellationToken.None);
        BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(bad.Value));
        body.RootElement.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        return string.Join("\n", body.RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
    }

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
        AgentDefinition Reviewer,
        WorkflowExecution OwnRun,
        CompositionWorkspaceVersion OwnVersion,
        WorkflowExecution SiblingProjectRun,
        WorkflowExecution OtherOrgRun,
        CompositionWorkspaceVersion OtherOrgVersion);

    private async Task<World> SeedAsync()
    {
        Project project = NewProject(Org);
        Project sibling = NewProject(Org);
        Project otherTenant = NewProject(OtherOrg);
        AgentDefinition author = NewAgent("Author", AgentType.AuthorAgent);
        AgentDefinition reviewer = NewAgent("Reviewer", AgentType.ReviewAgent);

        WorkflowExecution own = NewRun(project);
        WorkflowExecution siblingRun = NewRun(sibling);
        WorkflowExecution otherRun = NewRun(otherTenant);
        (CompositionWorkspace ownWorkspace, CompositionWorkspaceVersion ownVersion) = NewWorkspace(project, own);
        (CompositionWorkspace siblingWorkspace, CompositionWorkspaceVersion siblingVersion) = NewWorkspace(sibling, siblingRun);
        (CompositionWorkspace otherWorkspace, CompositionWorkspaceVersion otherVersion) = NewWorkspace(otherTenant, otherRun);

        _db.Projects.AddRange(project, sibling, otherTenant);
        _db.AgentDefinitions.AddRange(author, reviewer);
        _db.WorkflowExecutions.AddRange(own, siblingRun, otherRun);
        _db.CompositionWorkspaces.AddRange(ownWorkspace, siblingWorkspace, otherWorkspace);
        _db.CompositionWorkspaceVersions.AddRange(ownVersion, siblingVersion, otherVersion);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        return new World(project, author, reviewer, own, ownVersion, siblingRun, otherRun, otherVersion);
    }

    private Project NewProject(Guid organizationId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "p",
        OwnerId = _userId,
        OrganizationId = organizationId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private AgentDefinition NewAgent(string name, AgentType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = "d",
        SystemPrompt = "p",
        AgentType = type,
        IsBuiltIn = true,
        CreatedAt = DateTime.UtcNow
    };

    private static WorkflowExecution NewRun(Project project) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = Guid.NewGuid(),
        ProjectId = project.Id,
        OrganizationId = project.OrganizationId,
        Status = ExecutionStatus.Passed
    };

    private static (CompositionWorkspace, CompositionWorkspaceVersion) NewWorkspace(Project project, WorkflowExecution run)
    {
        CompositionWorkspace workspace = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            OriginExecutionId = run.Id,
            LatestVersionNumber = 1
        };
        CompositionWorkspaceVersion version = new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            VersionNumber = 1,
            StorageKey = $"projects/{project.Id}/workspaces/{workspace.Id}/v1.tar.gz",
            Sha256 = "00",
            CreatedByExecutionId = run.Id
        };
        return (workspace, version);
    }
}
