using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-208: ProposeGreenScreenInsert / ApplyGreenScreenInsert make (or update by name) the free green-screen workflow from
/// the template with the plate and content ids filled in, through the normal project-scoped save validation, and never
/// start a run. Real diff service, real token service, real save validators.
/// </summary>
public class GreenScreenToolsTests
{
    private sealed class Caller(Guid userId, bool emailVerified = true) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "test@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool EmailVerified { get; } = emailVerified;
    }

    private sealed class Rig : IDisposable
    {
        private readonly SqliteConnection _connection;

        public InferenceApiDbContext Db { get; }
        public Guid User { get; } = Guid.NewGuid();
        public Project Project { get; }
        public Project Other { get; }
        public Guid AgentId { get; } = Guid.NewGuid();
        public ProjectFile Plate { get; }
        public ProjectFile Screen { get; }
        public IConfirmationTokenService Tokens { get; } = ConfirmationTokenService.CreateEphemeral();

        /// <summary>
        /// The tool's update path replaces steps through <see cref="WorkflowStepReplacer"/>, whose archive runs
        /// real <c>ExecuteDelete</c>/<c>ExecuteUpdate</c> SQL that the in-memory provider refuses, so the rig
        /// runs on Sqlite.
        /// </summary>
        public Rig()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseSqlite(_connection)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options);
            Db.Database.EnsureCreated();
            // The engine context owns the workflow tables (workflow_definitions, …); create them on the same
            // connection, as WorkflowStepReplacerTests does.
            using (WorkflowEngineDbContext engine = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_connection).Options))
                engine.GetService<IRelationalDatabaseCreator>().CreateTables();
            // Sqlite enforces the FKs the in-memory provider ignored: seed the user and their org first.
            Db.ApplicationUsers.Add(new ApplicationUser { Id = User, Email = $"{User:N}@t.local", DisplayName = "owner", MustChangePassword = false });
            Db.Organizations.Add(new Organization { Id = TestTenancy.OrgOf(User), Name = "org", Slug = "org-" + User.ToString("N"), Kind = OrganizationKind.Personal });
            Db.SaveChanges();
            Project = NewProject("Launch");
            Other = NewProject("Elsewhere");
            Db.AgentDefinitions.Add(new AgentDefinition { Id = AgentId, Name = "VideoTransform", AgentType = AgentType.VideoTransform, IsBuiltIn = true, CreatedAt = DateTime.UtcNow });
            Plate = AddFile(Project, "cat-and-phone.mp4", "video/mp4");
            Screen = AddFile(Project, "app-demo.mp4", "video/mp4");
            Db.SaveChanges();
        }

        private Project NewProject(string name)
        {
            Project project = new() { Id = Guid.NewGuid(), Name = name, OwnerId = User, OrganizationId = TestTenancy.OrgOf(User), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(project);
            return project;
        }

        public ProjectFile AddFile(Project project, string name, string mime)
        {
            ProjectFile file = new() { Id = Guid.NewGuid(), ProjectId = project.Id, OriginalFileName = name, MimeType = mime, StorageKey = $"projects/{project.Id}/userFiles/{name}" };
            Db.ProjectFiles.Add(file);
            Db.SaveChanges();
            return file;
        }

        public Guid StepResultOf(Project project)
        {
            WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Old", ProjectId = project.Id };
            WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = AgentId, StepOrder = 1, StepType = StepType.VideoCompile };
            WorkflowExecution run = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = project.Id, OrganizationId = project.OrganizationId, Status = ExecutionStatus.Passed, StartedAt = DateTime.UtcNow };
            WorkflowStepResult result = new() { Id = Guid.NewGuid(), WorkflowExecutionId = run.Id, WorkflowStepId = step.Id, Status = StepStatus.Completed, OutputStorageKey = $"projects/{project.Id}/outputFiles/{run.Id}/render.mp4" };
            Db.WorkflowDefinitions.Add(workflow);
            Db.WorkflowSteps.Add(step);
            Db.WorkflowExecutions.Add(run);
            Db.WorkflowStepResults.Add(result);
            Db.SaveChanges();
            return result.Id;
        }

        public AssistantGreenScreenTools Tools(bool emailVerified = true)
        {
            Caller caller = new(User, emailVerified);
            return new AssistantGreenScreenTools(Db, TestTenancy.Access(Db, caller), new WorkflowEditDiffService(Db), Tokens, caller);
        }

        public async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args, AssistantGreenScreenTools? tools = null)
        {
            tools ??= Tools();
            AIFunction fn = tools.GetTools(new AssistantToolContext(User, Project.Id, ThreadId: Thread)).Single(t => t.Name == tool);
            object? raw = await fn.InvokeAsync(new AIFunctionArguments(args));
            string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public Dictionary<string, object?> Args(Guid plate, object content, string? name = null, string? token = null)
        {
            Dictionary<string, object?> args = new() { ["plateFileId"] = plate, ["content"] = JsonSerializer.SerializeToElement(content) };
            if (name is not null) args["name"] = name;
            if (token is not null) args["confirmationToken"] = token;
            return args;
        }

        public Guid Thread { get; } = Guid.NewGuid();

        public object ScreenContent => new { from = "ProjectFile", projectFileId = Screen.Id };

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    [Fact]
    public async Task Propose_describes_the_workflow_returns_a_token_and_writes_nothing()
    {
        using Rig rig = new();

        JsonElement result = await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent));

        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
        result.GetProperty("workflowName").GetString().Should().Be("Green-screen insert v1");
        result.GetProperty("action").GetString().Should().Be("create");
        result.GetProperty("summary").GetString().Should().Contain("cat-and-phone.mp4").And.Contain("app-demo.mp4").And.Contain("only processing time is charged").And.Contain("one flat colour, such as green or blue");
        rig.Db.WorkflowDefinitions.Should().BeEmpty();
        rig.Db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_creates_the_workflow_with_the_ids_filled_in_and_starts_no_run()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent))).GetProperty("confirmationToken").GetString()!;

        JsonElement result = await rig.CallAsync("ApplyGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent, token: token));

        result.GetProperty("workflowName").GetString().Should().Be("Green-screen insert v1");
        result.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("stepType").GetString()).Should().Equal("VideoAnalyze", "VideoCompile");
        result.GetProperty("hint").GetString().Should().Contain("ExecuteWorkflow");
        WorkflowDefinition workflow = rig.Db.WorkflowDefinitions.Single();
        workflow.Id.Should().Be(result.GetProperty("workflowId").GetGuid());
        workflow.ProjectId.Should().Be(rig.Project.Id);
        List<WorkflowStep> steps = rig.Db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == workflow.Id).OrderBy(s => s.StepOrder).ToList();
        steps.Should().OnlyContain(s => s.AgentDefinitionId == rig.AgentId);
        steps[0].VideoAnalyzeConfigJson.Should().Contain(rig.Plate.Id.ToString());
        steps[0].VideoAnalyzeConfigJson.Should().NotContain("placeholders", "the template's fill-in note is gone once the id is in");
        steps[1].VideoCompileConfigJson.Should().NotContain("placeholders");
        VideoAnalyzeStepConfig analyze = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(steps[0].VideoAnalyzeConfigJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        analyze.Source.ProjectFileId.Should().Be(rig.Plate.Id);
        analyze.DetectInsertRegions.Should().BeTrue();
        VideoCompileStepConfig compile = JsonSerializer.Deserialize<VideoCompileStepConfig>(steps[1].VideoCompileConfigJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        compile.InsertContentRef.Should().Be(new InsertContentRef("ProjectFile", ProjectFileId: rig.Screen.Id));
        compile.KeepWholeSources.Should().BeTrue();
        RunRateClassifier.Classify(steps).Should().Be(RunRateBucket.Free);
        rig.Db.WorkflowExecutions.Should().BeEmpty("ExecuteWorkflow stays the run gate");
    }

    [Fact]
    public async Task Apply_records_the_workflow_as_made_by_the_assistant_in_this_thread()
    {
        using var rig = new Rig();
        string token = (await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent))).GetProperty("confirmationToken").GetString()!;
        Guid id = (await rig.CallAsync("ApplyGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent, token: token))).GetProperty("workflowId").GetGuid();

        WorkflowDefinition row = rig.Db.WorkflowDefinitions.AsNoTracking().Single(w => w.Id == id);
        row.CreatedVia.Should().Be(WorkflowCreatedVia.Assistant);
        row.CreatedInThreadId.Should().Be(rig.Thread);
    }

    [Fact]
    public async Task Apply_with_the_name_of_an_existing_workflow_updates_it_in_place()
    {
        using Rig rig = new();
        Dictionary<string, object?> first = rig.Args(rig.Plate.Id, rig.ScreenContent, name: "Hero shot");
        string t1 = (await rig.CallAsync("ProposeGreenScreenInsert", first)).GetProperty("confirmationToken").GetString()!;
        Guid id = (await rig.CallAsync("ApplyGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent, "Hero shot", t1))).GetProperty("workflowId").GetGuid();
        ProjectFile other = rig.AddFile(rig.Project, "other-demo.mp4", "video/mp4");
        object newContent = new { from = "ProjectFile", projectFileId = other.Id };

        JsonElement proposal = await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, newContent, "Hero shot"));
        proposal.GetProperty("action").GetString().Should().Be("update");
        JsonElement applied = await rig.CallAsync("ApplyGreenScreenInsert", rig.Args(rig.Plate.Id, newContent, "Hero shot", proposal.GetProperty("confirmationToken").GetString()));

        applied.GetProperty("workflowId").GetGuid().Should().Be(id);
        rig.Db.WorkflowDefinitions.Should().ContainSingle();
        rig.Db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == id).Should().HaveCount(2);
        rig.Db.WorkflowSteps.Single(s => s.StepType == StepType.VideoCompile).VideoCompileConfigJson.Should().Contain(other.Id.ToString());
    }

    [Fact]
    public async Task Apply_over_a_workflow_whose_steps_a_past_run_used_archives_them_like_any_replacement()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent))).GetProperty("confirmationToken").GetString()!;
        Guid workflowId = (await rig.CallAsync("ApplyGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent, token: token))).GetProperty("workflowId").GetGuid();

        // A snapshot-less run of that workflow whose result references one of its stored steps: exactly the
        // shape WorkflowStepReplacer's archive exists for.
        Guid referencedStepId = rig.Db.WorkflowSteps.Single(s => s.WorkflowDefinitionId == workflowId && s.StepType == StepType.VideoCompile).Id;
        WorkflowExecution run = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflowId, ProjectId = rig.Project.Id, OrganizationId = rig.Project.OrganizationId, Status = ExecutionStatus.Passed, StartedAt = DateTime.UtcNow };
        WorkflowStepResult result = new() { Id = Guid.NewGuid(), WorkflowExecutionId = run.Id, WorkflowStepId = referencedStepId, Status = StepStatus.Completed, OutputStorageKey = $"projects/{rig.Project.Id}/outputFiles/{run.Id}/render.mp4" };
        rig.Db.WorkflowExecutions.Add(run);
        rig.Db.WorkflowStepResults.Add(result);
        rig.Db.SaveChanges();

        ProjectFile other = rig.AddFile(rig.Project, "other-demo.mp4", "video/mp4");
        object newContent = new { from = "ProjectFile", projectFileId = other.Id };
        JsonElement proposal = await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, newContent));
        proposal.GetProperty("action").GetString().Should().Be("update");

        await rig.CallAsync("ApplyGreenScreenInsert", rig.Args(rig.Plate.Id, newContent, token: proposal.GetProperty("confirmationToken").GetString()));
        // The replacer re-parents through raw SQL, so the tracked rows are stale; read from the store.
        rig.Db.ChangeTracker.Clear();

        // The workflow holds exactly the new steps...
        rig.Db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == workflowId).Should().HaveCount(2);
        // ...and the step the run's result points at was re-parented into an archive, not deleted. That is the
        // replacer's own guarantee; a raw RemoveRange leaves the result pointing at a gone step and writes no
        // archive at all.
        WorkflowStep archived = rig.Db.WorkflowSteps.Single(s => s.Id == referencedStepId);
        archived.WorkflowDefinitionId.Should().NotBe(workflowId);
        rig.Db.WorkflowDefinitions.Single(w => w.Id == archived.WorkflowDefinitionId).Kind.Should().Be(WorkflowDefinitionKind.ExecutionSnapshot);
        // The result survives and the run is attached to the archive, so its detail stays readable.
        rig.Db.WorkflowStepResults.Single(r => r.Id == result.Id).WorkflowStepId.Should().Be(referencedStepId);
        rig.Db.WorkflowExecutions.Single(e => e.Id == run.Id).SnapshotDefinitionId.Should().Be(archived.WorkflowDefinitionId);
    }

    [Fact]
    public async Task A_same_named_workflow_this_tool_did_not_make_is_never_replaced_a_suffixed_one_is_created()
    {
        using Rig rig = new();
        WorkflowDefinition mine = new() { Id = Guid.NewGuid(), Name = "Green-screen insert v1", ProjectId = rig.Project.Id };
        rig.Db.WorkflowDefinitions.Add(mine);
        rig.Db.WorkflowSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = mine.Id, AgentDefinitionId = rig.AgentId, StepOrder = 1, Label = "My own edit", StepType = StepType.VideoCompile, VideoCompileConfigJson = "{\"version\":1}" });
        rig.Db.SaveChanges();

        JsonElement proposal = await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent));
        proposal.GetProperty("action").GetString().Should().Be("create");
        proposal.GetProperty("workflowName").GetString().Should().Be("Green-screen insert v1 (2)");
        JsonElement applied = await rig.CallAsync("ApplyGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent, token: proposal.GetProperty("confirmationToken").GetString()));

        applied.GetProperty("workflowId").GetGuid().Should().NotBe(mine.Id);
        applied.GetProperty("workflowName").GetString().Should().Be("Green-screen insert v1 (2)");
        rig.Db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == mine.Id).Select(s => s.Label).Should().Equal("My own edit");
        rig.Db.WorkflowDefinitions.Should().HaveCount(2);
    }

    [Fact]
    public async Task Apply_refuses_when_the_existing_workflow_was_edited_after_the_proposal()
    {
        using Rig rig = new();
        string t1 = (await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent))).GetProperty("confirmationToken").GetString()!;
        Guid id = (await rig.CallAsync("ApplyGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent, token: t1))).GetProperty("workflowId").GetGuid();
        ProjectFile other = rig.AddFile(rig.Project, "other-demo.mp4", "video/mp4");
        object newContent = new { from = "ProjectFile", projectFileId = other.Id };
        JsonElement proposal = await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, newContent));
        proposal.GetProperty("action").GetString().Should().Be("update");

        // The user edits a setting of the stored workflow between the proposal and the apply.
        WorkflowStep compile = rig.Db.WorkflowSteps.Single(s => s.WorkflowDefinitionId == id && s.StepType == StepType.VideoCompile);
        compile.VideoCompileConfigJson = compile.VideoCompileConfigJson!.Replace("\"keepWholeSources\":true", "\"keepWholeSources\":false", StringComparison.Ordinal);
        rig.Db.SaveChanges();

        JsonElement result = await rig.CallAsync("ApplyGreenScreenInsert", rig.Args(rig.Plate.Id, newContent, token: proposal.GetProperty("confirmationToken").GetString()));

        result.GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        rig.Db.WorkflowSteps.Single(s => s.WorkflowDefinitionId == id && s.StepType == StepType.VideoCompile).VideoCompileConfigJson.Should().NotContain(other.Id.ToString());
    }

    [Fact]
    public async Task A_create_token_does_not_replace_a_workflow_that_appeared_after_the_proposal()
    {
        using Rig rig = new();
        string createToken = (await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent))).GetProperty("confirmationToken").GetString()!;
        string again = (await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent))).GetProperty("confirmationToken").GetString()!;
        await rig.CallAsync("ApplyGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent, token: again));

        JsonElement result = await rig.CallAsync("ApplyGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent, token: createToken));

        result.GetProperty("error").GetString().Should().Contain("stale confirmationToken", "the token said Create; the workflow now exists, so Apply would replace it");
    }

    [Fact]
    public async Task Apply_refuses_a_token_minted_for_other_content_and_writes_nothing()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent))).GetProperty("confirmationToken").GetString()!;
        ProjectFile other = rig.AddFile(rig.Project, "other-demo.mp4", "video/mp4");

        JsonElement result = await rig.CallAsync("ApplyGreenScreenInsert", rig.Args(rig.Plate.Id, new { from = "ProjectFile", projectFileId = other.Id }, token: token));

        result.GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        rig.Db.WorkflowDefinitions.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_refuses_an_unverified_email()
    {
        using Rig rig = new();
        string token = (await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent))).GetProperty("confirmationToken").GetString()!;

        JsonElement result = await rig.CallAsync("ApplyGreenScreenInsert", rig.Args(rig.Plate.Id, rig.ScreenContent, token: token), rig.Tools(emailVerified: false));

        result.GetProperty("code").GetString().Should().Be(EmailVerificationGate.ErrorCode);
        rig.Db.WorkflowDefinitions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plate_from_another_project_is_not_found()
    {
        using Rig rig = new();
        ProjectFile foreign = rig.AddFile(rig.Other, "foreign.mp4", "video/mp4");

        JsonElement result = await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(foreign.Id, rig.ScreenContent));

        result.GetProperty("error").GetString().Should().Contain("plate");
        result.TryGetProperty("confirmationToken", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_plate_that_is_not_a_video_is_refused()
    {
        using Rig rig = new();
        ProjectFile notes = rig.AddFile(rig.Project, "notes.txt", "text/plain");

        JsonElement result = await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(notes.Id, rig.ScreenContent));

        result.GetProperty("error").GetString().Should().Contain("video");
        result.TryGetProperty("confirmationToken", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Content_file_of_another_project_or_of_the_wrong_kind_is_refused()
    {
        using Rig rig = new();
        ProjectFile foreign = rig.AddFile(rig.Other, "foreign.mp4", "video/mp4");
        ProjectFile audio = rig.AddFile(rig.Project, "song.mp3", "audio/mpeg");

        foreach (ProjectFile bad in new[] { foreign, audio })
        {
            JsonElement result = await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, new { from = "ProjectFile", projectFileId = bad.Id }));
            result.GetProperty("error").GetString().Should().Contain("content");
            result.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task A_step_result_of_this_project_is_accepted_and_one_of_another_project_is_refused()
    {
        using Rig rig = new();
        Guid mine = rig.StepResultOf(rig.Project);
        Guid foreign = rig.StepResultOf(rig.Other);

        JsonElement ok = await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, new { from = "StepResult", stepResultId = mine }));
        JsonElement refused = await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, new { from = "StepResult", stepResultId = foreign }));

        ok.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
        refused.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        refused.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_step_reference_is_refused_because_the_workflow_has_no_earlier_render()
    {
        using Rig rig = new();

        JsonElement result = await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, new { from = "Step", stepOrder = 1 }));

        result.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        result.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_unknown_content_kind_is_refused()
    {
        using Rig rig = new();

        JsonElement result = await rig.CallAsync("ProposeGreenScreenInsert", rig.Args(rig.Plate.Id, new { from = "Url" }));

        result.GetProperty("error").GetString().Should().Contain("from");
    }

    [Fact]
    public void Both_tools_have_a_progress_caption()
    {
        AssistantGreenScreenTools.Captions.Keys.Should().BeEquivalentTo("ProposeGreenScreenInsert", "ApplyGreenScreenInsert");
        AssistantGreenScreenTools.Captions.Values.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c));
    }
}
