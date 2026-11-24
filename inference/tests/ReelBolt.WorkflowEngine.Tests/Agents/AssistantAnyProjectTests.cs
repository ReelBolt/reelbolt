using ReelBolt.Shared.Workflows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// "Let the assistant access any project at any time": every project-scoped tool takes an optional
/// <c>projectId</c>, so a page with no project open (the home page) can still work on any project of the
/// workspace, the page's own project stays the default, and authority is exactly what it was — re-resolved
/// through <c>IProjectAccessService</c> on every call, a foreign project reading as a missing one.
///
/// <para>The tests in the matrix section discover the tools that take <c>projectId</c> from the provider's own
/// registration, so a tool added later that forgets the argument — or its refusal — is noticed here.</para>
/// </summary>
public class AssistantAnyProjectTests
{
    private const string NoProject = "no project in context";
    private const string NotFound = "project not found";

    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    private sealed class CountingDb(DbContextOptions<InferenceApiDbContext> options) : InferenceApiDbContext(options)
    {
        public int SaveCount { get; private set; }

        public void ResetSaveCount() => SaveCount = 0;

        public override int SaveChanges()
        {
            SaveCount++;
            return base.SaveChanges();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class Caller(Guid userId, Guid? organizationId, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
        public bool EmailVerified => true;
    }

    /// <summary>Two projects of the caller's workspace, one project of somebody else's, with a workflow, a run and a file each.</summary>
    private sealed class World : IDisposable
    {
        public CountingDb Db { get; } = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        public Guid Me { get; } = Guid.NewGuid();
        public Guid Stranger { get; } = Guid.NewGuid();
        public Caller User { get; }
        public FakeFileStorage Storage { get; } = new();

        public Project Alpha { get; }
        public Project Beta { get; }
        public Project Foreign { get; }
        public WorkflowDefinition AlphaCut { get; }
        public WorkflowDefinition BetaCut { get; }
        public WorkflowDefinition ForeignCut { get; }
        public ProjectFile Notes { get; }
        public WorkflowExecution AlphaRun { get; }
        public WorkflowExecution BetaRun { get; }

        public World()
        {
            User = new Caller(Me, TestTenancy.OrgOf(Me), OrganizationRole.Member);
            Alpha = SeedProject(Me, "Alpha");
            Beta = SeedProject(Me, "Spring Promo 2026");
            Foreign = SeedProject(Stranger, "Secret Foreign");
            AlphaCut = SeedWorkflow(Alpha, "Alpha cut");
            BetaCut = SeedWorkflow(Beta, "Beta cut");
            ForeignCut = SeedWorkflow(Foreign, "Foreign cut");

            Notes = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = Beta.Id,
                OriginalFileName = "notes.md",
                OriginalPath = "notes.md",
                StorageKey = $"projects/{Beta.Id}/userFiles/notes.md",
                StorageBucket = "bucket",
                MimeType = "text/markdown",
                SizeBytes = 5,
                UploadedAt = DateTime.UtcNow
            };
            Db.ProjectFiles.Add(Notes);
            Storage.Objects[Notes.StorageKey] = Encoding.UTF8.GetBytes("hello");

            AlphaRun = SeedRun(Alpha, AlphaCut, DateTime.UtcNow.AddHours(-2));
            BetaRun = SeedRun(Beta, BetaCut, DateTime.UtcNow.AddHours(-1));
            SeedRun(Foreign, ForeignCut, DateTime.UtcNow);
            Db.SaveChanges();
            Db.ChangeTracker.Clear();
            Db.ResetSaveCount();
        }

        private Project SeedProject(Guid owner, string name)
        {
            Project project = new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = $"{name} description",
                OwnerId = owner,
                OrganizationId = TestTenancy.OrgOf(owner),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            Db.Projects.Add(project);
            return project;
        }

        private WorkflowDefinition SeedWorkflow(Project project, string name)
        {
            WorkflowDefinition workflow = new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                ProjectId = project.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            Db.WorkflowDefinitions.Add(workflow);
            return workflow;
        }

        private WorkflowExecution SeedRun(Project project, WorkflowDefinition workflow, DateTime startedAt)
        {
            WorkflowExecution run = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                OrganizationId = project.OrganizationId,
                WorkflowDefinitionId = workflow.Id,
                Status = ExecutionStatus.Failed,
                StartedAt = startedAt,
                ErrorMessage = $"{workflow.Name} failed"
            };
            Db.WorkflowExecutions.Add(run);
            return run;
        }

        public AssistantToolProvider Provider(
            ICurrentUser? user = null,
            IAssistantExecutionGuard? guard = null,
            IWorkflowExecutionService? executionService = null) =>
            AssistantToolTestFactory.CreateProvider(
                Db, user ?? User, fileStorage: Storage, executionGuard: guard, executionService: executionService);

        /// <summary>The page the user is on when no project is open.</summary>
        public AssistantToolContext HomePage => new(Me);

        public void Dispose() => Db.Dispose();
    }

    private static string AsText(object? raw) => raw switch
    {
        null => string.Empty,
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString()!,
        JsonElement element => element.GetRawText(),
        _ => raw.ToString()!
    };

    private static async Task<string> InvokeTextAsync(
        AssistantToolProvider provider, AssistantToolContext context, string name, params (string Key, object? Value)[] args)
    {
        AIFunction tool = provider.GetTools(context).Single(t => t.Name == name);
        AIFunctionArguments arguments = new();
        foreach ((string key, object? value) in args)
            arguments[key] = value;

        return AsText(await tool.InvokeAsync(arguments));
    }

    private static async Task<JsonElement> InvokeAsync(
        AssistantToolProvider provider, AssistantToolContext context, string name, params (string Key, object? Value)[] args)
    {
        string text = await InvokeTextAsync(provider, context, name, args);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string? ErrorOf(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    /// <summary>A property by name, ignoring case (tool results serialize with either casing).</summary>
    private static JsonElement Prop(JsonElement element, string name) =>
        element.EnumerateObject().First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string Token(JsonElement proposal) => Prop(proposal, "confirmationToken").GetString()!;

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    // ---------------------------------------------------------------------
    // The registration: which tools take projectId, and what they tell the model
    // ---------------------------------------------------------------------

    private static bool TakesProjectId(AIFunction tool) =>
        tool.JsonSchema.TryGetProperty("properties", out JsonElement properties)
        && properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty("projectId", out _);

    private static bool IsPictureTool(AIFunction tool) =>
        (tool.Name.Contains("Picture") && !tool.Name.EndsWith("CreatePictureDocument", StringComparison.Ordinal))
        || tool.Name.Contains("Graphic") || tool.Name.Contains("TextLayer");

    private static bool IsRequired(AIFunction tool, string parameter) =>
        tool.JsonSchema.TryGetProperty("required", out JsonElement required)
        && required.ValueKind == JsonValueKind.Array
        && required.EnumerateArray().Any(r => r.GetString() == parameter);

    [Fact]
    public void Every_project_scoped_tool_takes_an_optional_projectId_and_says_so()
    {
        using World w = new();
        IReadOnlyList<AIFunction> tools = w.Provider().GetTools(w.HomePage);

        string[] expected =
        [
            // core
            "ListAgents", "ListProjectFiles", "ValidateWorkflow", "ProposeWorkflow", "ApplyWorkflow", "ExecuteWorkflow",
            // projects
            "GetProject", "ProposeUpdateProject", "ApplyUpdateProject", "ProposeDeleteProject", "ApplyDeleteProject",
            "ProposeSavePronunciation", "ApplySavePronunciation",
            // files
            "ReadProjectFile", "ProposeWriteProjectFile", "ApplyWriteProjectFile", "ProposeDeleteProjectFile", "ApplyDeleteProjectFile",
            "ReadProjectFileLines", "GetProjectFileOutline", "ProposeEditProjectFile", "ApplyEditProjectFile",
            "ProposeInsertProjectFileLines", "ApplyInsertProjectFileLines",
            // workflows and runs
            "ListWorkflows", "GetWorkflow", "ListWorkflowExecutions", "GetWorkflowExecution", "ProposeDeleteWorkflow", "ApplyDeleteWorkflow", "ProposeStopExecution", "ApplyStopExecution",
            // styles
            "ListStyles", "ProposeSaveStyle", "ApplySaveStyle",
            // design system
            "GetDesignSystem", "ListDesignChanges", "ProposeSaveDesignSystem", "ApplySaveDesignSystem",
            "ProposeDecideDesignChange", "ApplyDecideDesignChange",
            // the picture editor
            "DescribePictureDocument", "ListPictureTools", "LookAtPicture", "ProposeAddTextLayer", "ApplyAddTextLayer",
            "ProposeEditPictureLayers", "ApplyEditPictureLayers", "ProposeDeletePictureLayers", "ApplyDeletePictureLayers",
            "ProposeChangePictureCanvas", "ApplyChangePictureCanvas", "ProposeRunPictureTool", "ApplyRunPictureTool",
            "ProposeDesignGraphic", "ApplyDesignGraphic",
            // outputs, uploads, run waiting and video revisions (every one but GetCreditBalance works inside a project)
            "GetOutput", "GetMusicBeats", "ProposeSaveOutputAsFile", "ApplySaveOutputAsFile", "ProposeUploadFile", "ApplyUploadFile",
            "ProposeImportFileFromUrl", "ApplyImportFileFromUrl", "GetExecutionStatus",
            "ProposeReviseComposition", "ApplyReviseComposition",
            "GetChangeOptions", "ProposeStepChange", "ApplyStepChange",
            // looking at a render or an image
            "LookAtOutput",
            // workflow patches and clones, picture documents and the green-screen insert
            "ProposePatchWorkflow", "ApplyPatchWorkflow", "ProposeCloneWorkflow", "ApplyCloneWorkflow",
            "ProposeCreatePictureDocument", "ApplyCreatePictureDocument",
            "ProposeGreenScreenInsert", "ApplyGreenScreenInsert",
            "ProposeSetMediaOverlay", "ApplySetMediaOverlay",
            "ProposeCopyMarketplaceRecipe", "ApplyCopyMarketplaceRecipe",
            // object effects on an editor timeline
            "ProposeObjectEffect", "ApplyObjectEffect", "WritePostText",
            // social publishing: a post is scheduled for a video of a project (cancel finds its project through the post)
            "ProposeSchedulePost", "ApplySchedulePost",
            "ProposeCleanAudio", "ApplyCleanAudio",
            // video enhancement
            "ProposeEnhanceVideo", "ApplyEnhanceVideo",
            // a project's narration voices
            "ListProjectVoices", "SearchVoiceLibrary", "ProposeAddLibraryVoice", "ApplyAddLibraryVoice",
            // avatars and objects: the project's list and the workspace library (read-only), and the object, variant,
            // import and clip pairs (a person's avatar is made only in the Avatars tab)
            "ListProjectAvatars", "ListAvatarLibrary",
            "ProposeCreateObjectAvatar", "ApplyCreateObjectAvatar", "ProposeExtractObjectFromVideo", "ApplyExtractObjectFromVideo",
            "ProposeCreateAvatarVariant", "ApplyCreateAvatarVariant", "ProposeImportLibraryAvatar", "ApplyImportLibraryAvatar",
            "ProposeGenerateSubjectClip", "ApplyGenerateSubjectClip",
            // the events a video analysis found: two free, read-only windows (progressive context)
            "ListVideoEvents", "DescribeVideoWindow",
            // the built-in sound effects library add pair (SearchSoundEffects needs no project)
            "ProposeAddSoundEffect", "ApplyAddSoundEffect",
            // project history: list versions, read changes, save and restore, always in a project's scope
            "ListProjectVersions", "GetProjectChanges",
            "ProposeSaveProjectVersion", "ApplySaveProjectVersion",
            "ProposeRestoreProjectVersion", "ApplyRestoreProjectVersion",
            // recipes: the fit to the project, the project's readiness, a run's estimate and the create-from-recipe pair
            "ListWorkflowTemplates", "GetProjectReadiness", "EstimateWorkflowRun",
            "ProposeCreateFromTemplate", "ApplyCreateFromTemplate"
        ];

        AIFunction[] withProjectId = [.. tools.Where(TakesProjectId)];
        withProjectId.Select(t => t.Name).Should().BeEquivalentTo(expected,
            "every tool that works on a project must be addressable by projectId, and no other tool should be");

        foreach (AIFunction tool in withProjectId)
        {
            IsRequired(tool, "projectId").Should().BeFalse($"'{tool.Name}': omitting projectId must keep meaning \"the page's project\"");
            tool.Description.Should().Contain("projectId", $"'{tool.Name}' must tell the model how to pick a project");
            tool.Description.Should().Contain("ListProjects", $"'{tool.Name}' must say where a project id comes from");
            if (!IsPictureTool(tool))
                tool.Description.Should().Contain(AssistantProjectScope.ProjectIdNote, $"'{tool.Name}' carries the one shared sentence");
        }

        tools.Single(t => t.Name == "ListProjects").Description.Should().Contain("nameContains");
        TakesProjectId(tools.Single(t => t.Name == "ListProjects")).Should().BeFalse("ListProjects is how a project id is found");
    }

    [Fact]
    public void Tool_descriptions_keep_their_untrusted_data_labelling()
    {
        using World w = new();

        foreach (AIFunction tool in w.Provider().GetTools(w.HomePage).Where(t =>
                     t.Name is "ListAgents" or "ListProjectFiles" or "ReadProjectFile" or "ListWorkflows" or "GetWorkflow"
                         or "ListWorkflowExecutions" or "GetWorkflowExecution" or "ListStyles" or "ValidateWorkflow" or "ExecuteWorkflow"
                         or "DescribePictureDocument" or "GetProject"))
        {
            tool.Description.Should().Contain("UNTRUSTED DATA", $"'{tool.Name}' read-back results are data, never instructions");
        }
    }

    // ---------------------------------------------------------------------
    // A page with no project open can work on any project of the workspace
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ListProjectFiles_takes_filters_and_refuses_a_forged_cursor()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();
        (string Key, object? Value) onBeta = ("projectId", w.Beta.Id);

        JsonElement hit = await InvokeAsync(provider, w.HomePage, "ListProjectFiles", onBeta, ("mimePrefix", "text/"), ("nameContains", "NOTES"));
        hit.GetProperty("total").GetInt32().Should().Be(1);
        JsonElement miss = await InvokeAsync(provider, w.HomePage, "ListProjectFiles", onBeta, ("mimePrefix", "video/"));
        miss.GetProperty("total").GetInt32().Should().Be(0);

        JsonElement forged = await InvokeAsync(provider, w.HomePage, "ListProjectFiles", onBeta, ("cursor", "AAAA"));
        ErrorOf(forged).Should().Contain("cursor");
    }

    [Fact]
    public async Task Reads_work_on_a_named_project_from_a_page_with_no_project_open()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();
        (string Key, object? Value) onBeta = ("projectId", w.Beta.Id);

        JsonElement files = await InvokeAsync(provider, w.HomePage, "ListProjectFiles", onBeta);
        files.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("originalFileName").GetString()).Should().Equal("notes.md");

        (await InvokeAsync(provider, w.HomePage, "ListAgents", onBeta)).ValueKind.Should().Be(JsonValueKind.Array);

        JsonElement workflows = await InvokeAsync(provider, w.HomePage, "ListWorkflows", onBeta);
        workflows.EnumerateArray().Select(f => f.GetProperty("name").GetString()).Should().Equal("Beta cut");

        JsonElement workflow = await InvokeAsync(provider, w.HomePage, "GetWorkflow", ("workflowId", w.BetaCut.Id), onBeta);
        ErrorOf(workflow).Should().BeNull();
        workflow.GetProperty("name").GetString().Should().Be("Beta cut");
        workflow.GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");

        JsonElement run = await InvokeAsync(provider, w.HomePage, "GetWorkflowExecution", ("executionId", w.BetaRun.Id), onBeta);
        ErrorOf(run).Should().BeNull();
        run.GetProperty("workflowName").GetString().Should().Be("Beta cut");
        run.GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");

        JsonElement file = await InvokeAsync(provider, w.HomePage, "ReadProjectFile", ("fileId", w.Notes.Id), onBeta);
        ErrorOf(file).Should().BeNull();
        file.GetProperty("content").GetString().Should().Be("hello");
        file.GetProperty("fileName").GetString().Should().Be("notes.md");
        file.GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");

        JsonElement styles = await InvokeAsync(provider, w.HomePage, "ListStyles", onBeta);
        styles.GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");

        JsonElement validated = await InvokeAsync(provider, w.HomePage, "ValidateWorkflow",
            ("steps", JsonSerializer.SerializeToElement(Array.Empty<object>())), onBeta);
        ErrorOf(validated).Should().BeNull();

        JsonElement project = await InvokeAsync(provider, w.HomePage, "GetProject", onBeta);
        project.GetProperty("name").GetString().Should().Be("Spring Promo 2026");
    }

    [Fact]
    public async Task Without_a_project_anywhere_the_refusal_keeps_its_text_and_tells_the_model_what_to_do()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();

        foreach (string tool in new[] { "ListAgents", "ListProjectFiles", "ListWorkflows" })
        {
            JsonElement refusal = await InvokeAsync(provider, w.HomePage, tool);

            ErrorOf(refusal).Should().Be(NoProject, "the wire text existing clients and tests rely on");
            refusal.GetProperty("hint").GetString().Should().Contain("projectId").And.Contain("ListProjects");
        }
    }

    [Fact]
    public async Task A_project_named_in_the_arguments_beats_the_page_project()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();
        AssistantToolContext onAlpha = new(w.Me, w.Alpha.Id);

        string pageProject = await InvokeTextAsync(provider, onAlpha, "ListWorkflows");
        string named = await InvokeTextAsync(provider, onAlpha, "ListWorkflows", ("projectId", w.Beta.Id));

        pageProject.Should().Contain("Alpha cut").And.NotContain("Beta cut");
        named.Should().Contain("Beta cut").And.NotContain("Alpha cut");
    }

    [Fact]
    public async Task A_workflow_open_on_one_project_is_never_defaulted_onto_another()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();
        AssistantToolContext alphaOpen = new(w.Me, w.Alpha.Id, w.AlphaCut.Id);

        // On its own project the open workflow is the default, as it always was.
        JsonElement own = await InvokeAsync(provider, alphaOpen, "GetWorkflow");
        own.GetProperty("name").GetString().Should().Be("Alpha cut");
        (await InvokeAsync(provider, alphaOpen, "GetWorkflow", ("projectId", w.Alpha.Id)))
            .GetProperty("name").GetString().Should().Be("Alpha cut");

        // Naming another project: the page's workflow must NOT come along.
        string onBeta = await InvokeTextAsync(provider, alphaOpen, "GetWorkflow", ("projectId", w.Beta.Id));
        ErrorOf(JsonDocument.Parse(onBeta).RootElement).Should().Contain("workflowId is required");
        onBeta.Should().NotContain("Alpha cut");

        // And an explicit workflow of the OTHER project is not found inside this one.
        ErrorOf(await InvokeAsync(provider, alphaOpen, "GetWorkflow", ("workflowId", w.AlphaCut.Id), ("projectId", w.Beta.Id)))
            .Should().Be("workflow not found");
    }

    // ---------------------------------------------------------------------
    // Writes: the confirmation binds the project the call resolved to
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A_file_is_written_into_the_named_project_only_with_a_token_minted_for_that_project()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();
        (string, object?)[] write =
        [
            ("directoryPath", null), ("fileName", "plan.md"), ("mimeType", "text/markdown"), ("contentBase64", Base64("# plan"))
        ];

        JsonElement proposal = await InvokeAsync(provider, w.HomePage, "ProposeWriteProjectFile", [.. write, ("projectId", w.Beta.Id)]);
        ErrorOf(proposal).Should().BeNull();
        proposal.GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");
        w.Db.SaveCount.Should().Be(0, "a proposal writes nothing");

        // The same token against another project is refused: the payload binds the resolved project id.
        JsonElement wrongProject = await InvokeAsync(provider, w.HomePage, "ApplyWriteProjectFile",
            [.. write, ("confirmationToken", Token(proposal)), ("projectId", w.Alpha.Id)]);
        ErrorOf(wrongProject).Should().Be("stale confirmationToken, re-propose and try again");
        w.Storage.Uploads.Should().BeEmpty();

        JsonElement applied = await InvokeAsync(provider, w.HomePage, "ApplyWriteProjectFile",
            [.. write, ("confirmationToken", Token(proposal)), ("projectId", w.Beta.Id)]);
        ErrorOf(applied).Should().BeNull();
        applied.GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");
        applied.GetProperty("fileName").GetString().Should().Be("plan.md");
        w.Db.ProjectFiles.AsNoTracking().Single(f => f.OriginalFileName == "plan.md").ProjectId.Should().Be(w.Beta.Id);
    }

    [Fact]
    public async Task A_file_and_a_workflow_are_deleted_from_the_named_project_with_names_in_the_answers()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();

        JsonElement fileProposal = await InvokeAsync(provider, w.HomePage, "ProposeDeleteProjectFile",
            ("fileId", w.Notes.Id), ("projectId", w.Beta.Id));
        fileProposal.GetProperty("fileName").GetString().Should().Be("notes.md");
        fileProposal.GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");
        JsonElement fileDone = await InvokeAsync(provider, w.HomePage, "ApplyDeleteProjectFile",
            ("fileId", w.Notes.Id), ("confirmationToken", Token(fileProposal)), ("projectId", w.Beta.Id));
        fileDone.GetProperty("deleted").GetBoolean().Should().BeTrue();
        fileDone.GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");
        w.Db.ProjectFiles.AsNoTracking().Should().BeEmpty();

        // A workflow with past runs keeps them by re-pointing them in bulk (ExecuteUpdate), which the
        // in-memory store cannot run; that path is pinned on Sqlite and Postgres by WorkflowStepReplacerTests.
        // This test is about the project the delete lands in, so its workflow has no runs here.
        w.Db.WorkflowExecutions.Remove(await w.Db.WorkflowExecutions.SingleAsync(e => e.Id == w.BetaRun.Id));
        await w.Db.SaveChangesAsync();

        JsonElement workflowProposal = await InvokeAsync(provider, w.HomePage, "ProposeDeleteWorkflow",
            ("workflowId", w.BetaCut.Id), ("projectId", w.Beta.Id));
        workflowProposal.GetProperty("name").GetString().Should().Be("Beta cut");
        workflowProposal.GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");
        JsonElement workflowDone = await InvokeAsync(provider, w.HomePage, "ApplyDeleteWorkflow",
            ("workflowId", w.BetaCut.Id), ("confirmationToken", Token(workflowProposal)), ("projectId", w.Beta.Id));
        workflowDone.GetProperty("deleted").GetBoolean().Should().BeTrue();
        workflowDone.GetProperty("name").GetString().Should().Be("Beta cut");
        w.Db.WorkflowDefinitions.AsNoTracking().Select(x => x.Name).Should().BeEquivalentTo(["Alpha cut", "Foreign cut"]);
    }

    [Fact]
    public async Task A_workflow_is_proposed_and_created_in_the_named_project_and_the_answers_name_it()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();
        JsonElement noSteps = JsonSerializer.SerializeToElement(Array.Empty<object>());

        JsonElement proposal = await InvokeAsync(provider, w.HomePage, "ProposeWorkflow",
            ("workflowId", null), ("name", "Launch cut"), ("steps", noSteps), ("projectId", w.Beta.Id));
        ErrorOf(proposal).Should().BeNull();
        Prop(proposal, "projectName").GetString().Should().Be("Spring Promo 2026");
        Prop(proposal, "workflowName").GetString().Should().Be("Launch cut");

        JsonElement applied = await InvokeAsync(provider, w.HomePage, "ApplyWorkflow",
            ("workflowId", null), ("name", "Launch cut"), ("steps", noSteps),
            ("confirmationToken", Token(proposal)), ("projectId", w.Beta.Id));
        ErrorOf(applied).Should().BeNull();
        applied.GetProperty("workflowName").GetString().Should().Be("Launch cut");
        applied.GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");
        w.Db.WorkflowDefinitions.AsNoTracking().Single(x => x.Name == "Launch cut").ProjectId.Should().Be(w.Beta.Id);

        // Editing an existing workflow of ANOTHER project through this one is refused, not diffed.
        JsonElement crossed = await InvokeAsync(provider, w.HomePage, "ProposeWorkflow",
            ("workflowId", w.AlphaCut.Id), ("name", null), ("steps", noSteps), ("projectId", w.Beta.Id));
        ErrorOf(crossed).Should().Be("workflow not found");
    }

    [Fact]
    public async Task A_style_is_pinned_to_the_named_project_and_the_token_binds_it()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();
        StyleConfig style = new(Pacing: StylePacing.Fast);

        JsonElement proposal = await InvokeAsync(provider, w.HomePage, "ProposeSaveStyle",
            ("name", "Reel"), ("style", style), ("pinToProject", true), ("projectId", w.Beta.Id));
        ErrorOf(proposal).Should().BeNull();
        proposal.GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");
        proposal.GetProperty("summary").GetString().Should().Contain("Spring Promo 2026");

        JsonElement wrongProject = await InvokeAsync(provider, w.HomePage, "ApplySaveStyle",
            ("name", "Reel"), ("style", style), ("pinToProject", true), ("confirmationToken", Token(proposal)), ("projectId", w.Alpha.Id));
        ErrorOf(wrongProject).Should().Contain("stale");
        w.Db.EditingStyles.Should().BeEmpty();

        JsonElement applied = await InvokeAsync(provider, w.HomePage, "ApplySaveStyle",
            ("name", "Reel"), ("style", style), ("pinToProject", true), ("confirmationToken", Token(proposal)), ("projectId", w.Beta.Id));
        applied.GetProperty("saved").GetBoolean().Should().BeTrue();
        applied.GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");
        w.Db.EditingStyles.AsNoTracking().Single().ProjectId.Should().Be(w.Beta.Id);

        // Pinning to a project the caller cannot use is the same refusal as everywhere else.
        ErrorOf(await InvokeAsync(provider, w.HomePage, "ProposeSaveStyle",
                ("name", "Reel"), ("style", style), ("pinToProject", true), ("projectId", w.Foreign.Id)))
            .Should().Be(NotFound);
        ErrorOf(await InvokeAsync(provider, w.HomePage, "ProposeSaveStyle", ("name", "Reel"), ("style", style), ("pinToProject", true)))
            .Should().Be(NoProject);
    }

    [Fact]
    public async Task A_project_with_an_output_marker_is_not_deleted_by_the_assistant()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();
        JsonElement proposal = await InvokeAsync(provider, w.HomePage, "ProposeDeleteProject", ("projectId", w.Beta.Id));
        (await w.Db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == w.Beta.Id)).CsamOutputHoldAt = DateTime.UtcNow.AddHours(-1);
        await w.Db.SaveChangesAsync();

        JsonElement refused = await InvokeAsync(provider, w.HomePage, "ApplyDeleteProject",
            ("confirmationToken", Token(proposal)), ("projectId", w.Beta.Id));

        ErrorOf(refused).Should().Be("This project cannot be deleted right now.");
        (await w.Db.Projects.IgnoreQueryFilters().CountAsync(p => p.Id == w.Beta.Id)).Should().Be(1);
    }

    [Fact]
    public async Task A_project_found_by_name_is_acted_on_by_the_id_ListProjects_returned_and_shown_by_name()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();

        JsonElement found = await InvokeAsync(provider, w.HomePage, "ListProjects", ("nameContains", "spring"));
        Guid id = found.EnumerateArray().Single().GetProperty("id").GetGuid();
        id.Should().Be(w.Beta.Id);

        JsonElement proposal = await InvokeAsync(provider, w.HomePage, "ProposeDeleteProject", ("projectId", id));
        proposal.GetProperty("summary").GetString().Should().Contain("Spring Promo 2026").And.NotContain(id.ToString(),
            "the summary the model relays to the user names the project, it does not show its id");
    }

    // ---------------------------------------------------------------------
    // Authority: a foreign project is a missing project, on every tool, byte for byte
    // ---------------------------------------------------------------------

    /// <summary>A well-typed, inert argument set for <paramref name="tool"/>, built from the tool's own JSON schema.</summary>
    private static AIFunctionArguments ProbeArguments(AIFunction tool, Guid projectId)
    {
        AIFunctionArguments arguments = new() { ["projectId"] = projectId };

        if (tool.JsonSchema.TryGetProperty("required", out JsonElement required) && required.ValueKind == JsonValueKind.Array)
        {
            JsonElement properties = tool.JsonSchema.GetProperty("properties");
            foreach (JsonElement name in required.EnumerateArray())
            {
                string key = name.GetString()!;
                arguments[key] = properties.TryGetProperty(key, out JsonElement schema) ? InertValue(schema) : null;
            }
        }

        if (tool.Name is "ProposeSaveStyle" or "ApplySaveStyle")
        {
            arguments["pinToProject"] = true;
            arguments["style"] = new StyleConfig();
        }

        // Last, so a parameter the schema lists as required-but-nullable can never overwrite it.
        arguments["projectId"] = projectId;
        return arguments;
    }

    private static object? InertValue(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out JsonElement type))
            return null;

        string? declared = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(t => t.GetString()).FirstOrDefault(t => t != "null")
            : type.GetString();
        bool nullable = type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(t => t.GetString() == "null");
        if (nullable)
            return null;

        return declared switch
        {
            "array" => JsonSerializer.SerializeToElement(Array.Empty<object>()),
            "boolean" => JsonSerializer.SerializeToElement(false),
            "integer" => JsonSerializer.SerializeToElement(1),
            "string" => JsonSerializer.SerializeToElement(
                schema.TryGetProperty("format", out JsonElement format) && format.GetString() == "uuid" ? Guid.NewGuid().ToString() : "probe"),
            _ => null
        };
    }

    [Fact]
    public async Task A_project_of_another_organization_reads_exactly_like_a_missing_one_on_every_tool_that_takes_projectId()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();
        AssistantToolContext context = new(w.Me);
        AIFunction[] tools = [.. provider.GetTools(context).Where(TakesProjectId)];
        tools.Should().HaveCountGreaterThan(30);
        List<string> failures = [];

        foreach (AIFunction tool in tools)
        {
            string foreign = AsText(await tool.InvokeAsync(ProbeArguments(tool, w.Foreign.Id)));
            string missing = AsText(await tool.InvokeAsync(ProbeArguments(tool, Guid.NewGuid())));

            if (foreign != missing)
                failures.Add($"'{tool.Name}': a foreign project answered '{foreign}' but a missing one '{missing}'");

            JsonElement parsed = JsonDocument.Parse(foreign).RootElement;
            string? expected = IsPictureTool(tool) ? "no picture open" : NotFound;
            if (ErrorOf(parsed) != expected)
                failures.Add($"'{tool.Name}': a foreign project got '{ErrorOf(parsed)}' instead of '{expected}'");

            if (foreign.Contains("Secret Foreign", StringComparison.Ordinal) || foreign.Contains("Foreign cut", StringComparison.Ordinal))
                failures.Add($"'{tool.Name}': the refusal leaked the foreign project's content");
        }

        failures.Should().BeEmpty(string.Join("\n", failures));
        w.Db.SaveCount.Should().Be(0, "a refused call must write nothing");
    }

    [Fact]
    public async Task A_named_foreign_project_beats_a_valid_page_project_and_is_still_refused()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();
        AssistantToolContext onAlpha = new(w.Me, w.Alpha.Id);

        // The model naming a foreign id must not silently fall back to the page's project.
        string refused = await InvokeTextAsync(provider, onAlpha, "ListProjectFiles", ("projectId", w.Foreign.Id));

        ErrorOf(JsonDocument.Parse(refused).RootElement).Should().Be(NotFound);
        refused.Should().NotContain("notes.md");
    }

    [Fact]
    public async Task A_role_without_project_access_is_refused_on_a_named_project()
    {
        using World w = new();
        Caller billing = new(w.Me, TestTenancy.OrgOf(w.Me), OrganizationRole.Billing);
        AssistantToolProvider provider = w.Provider(billing);

        ErrorOf(await InvokeAsync(provider, w.HomePage, "ListProjectFiles", ("projectId", w.Beta.Id))).Should().Be(NotFound);
        ErrorOf(await InvokeAsync(provider, w.HomePage, "ProposeDeleteProjectFile", ("fileId", w.Notes.Id), ("projectId", w.Beta.Id)))
            .Should().Be(NotFound);
        w.Db.SaveCount.Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // Runs: with no project, across every project of the workspace
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ListWorkflowExecutions_with_no_project_lists_runs_across_the_workspace_with_names()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();

        JsonElement rows = await InvokeAsync(provider, w.HomePage, "ListWorkflowExecutions");

        JsonElement[] list = [.. rows.EnumerateArray()];
        list.Select(r => r.GetProperty("id").GetGuid()).Should().Equal([w.BetaRun.Id, w.AlphaRun.Id], "newest first");
        list[0].GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");
        list[0].GetProperty("projectId").GetGuid().Should().Be(w.Beta.Id);
        list[0].GetProperty("workflowName").GetString().Should().Be("Beta cut");
        list[1].GetProperty("projectName").GetString().Should().Be("Alpha");
        list[1].GetProperty("workflowName").GetString().Should().Be("Alpha cut");
        list[0].GetProperty("status").GetString().Should().Be("Failed");
        rows.GetRawText().Should().NotContain("Secret Foreign").And.NotContain("Foreign cut", "another workspace's runs are never listed");
    }

    [Fact]
    public async Task ListWorkflowExecutions_across_projects_keeps_the_limit_and_the_workflow_filter()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();

        (await InvokeAsync(provider, w.HomePage, "ListWorkflowExecutions", ("limit", 1))).GetArrayLength().Should().Be(1);
        (await InvokeAsync(provider, w.HomePage, "ListWorkflowExecutions", ("limit", 0))).GetArrayLength().Should().Be(1, "the limit is clamped to at least one");
        (await InvokeAsync(provider, w.HomePage, "ListWorkflowExecutions", ("limit", 5000))).GetArrayLength().Should().Be(2);

        JsonElement filtered = await InvokeAsync(provider, w.HomePage, "ListWorkflowExecutions", ("workflowId", w.AlphaCut.Id));
        filtered.EnumerateArray().Select(r => r.GetProperty("projectName").GetString()).Should().Equal("Alpha");
    }

    [Fact]
    public async Task ListWorkflowExecutions_with_a_project_open_or_named_stays_inside_that_project()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();

        JsonElement open = await InvokeAsync(provider, new AssistantToolContext(w.Me, w.Alpha.Id), "ListWorkflowExecutions");
        open.EnumerateArray().Select(r => r.GetProperty("projectName").GetString()).Should().Equal("Alpha");

        JsonElement named = await InvokeAsync(provider, new AssistantToolContext(w.Me, w.Alpha.Id), "ListWorkflowExecutions", ("projectId", w.Beta.Id));
        named.EnumerateArray().Select(r => r.GetProperty("projectName").GetString()).Should().Equal("Spring Promo 2026");

        // A page project the caller can no longer use keeps its refusal; it does not widen into "all projects".
        ErrorOf(await InvokeAsync(provider, new AssistantToolContext(w.Me, w.Foreign.Id), "ListWorkflowExecutions")).Should().Be(NoProject);
    }

    [Fact]
    public async Task ListWorkflowExecutions_across_projects_is_scoped_like_ListProjects()
    {
        using World w = new();

        // No workspace at all, and a role that may not see projects: nothing to list, not an error and not a leak.
        foreach (Caller caller in new[]
                 {
                     new Caller(w.Me, null, null),
                     new Caller(w.Me, TestTenancy.OrgOf(w.Me), OrganizationRole.Billing)
                 })
        {
            AssistantToolProvider provider = w.Provider(caller);

            (await InvokeAsync(provider, w.HomePage, "ListWorkflowExecutions")).GetArrayLength().Should().Be(0);
            (await InvokeAsync(provider, w.HomePage, "ListProjects")).GetArrayLength().Should().Be(0);
        }
    }

    // ---------------------------------------------------------------------
    // ListProjects(nameContains)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("spring", 1)]
    [InlineData("SPRING PROMO", 1)]
    [InlineData("  promo 2026  ", 1)]
    [InlineData("alpha", 1)]
    [InlineData("zzz", 0)]
    [InlineData("", 2)]
    [InlineData("   ", 2)]
    [InlineData(null, 2)]
    public async Task ListProjects_filters_by_a_case_insensitive_part_of_the_name(string? nameContains, int expected)
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();

        JsonElement rows = nameContains is null
            ? await InvokeAsync(provider, w.HomePage, "ListProjects")
            : await InvokeAsync(provider, w.HomePage, "ListProjects", ("nameContains", nameContains));

        rows.GetArrayLength().Should().Be(expected);
        rows.GetRawText().Should().NotContain("Secret Foreign", "another workspace's projects are never listed, filter or not");
        foreach (JsonElement row in rows.EnumerateArray())
        {
            row.EnumerateObject().Select(p => p.Name).Should().Equal("id", "name", "description", "status", "tags", "folder", "createdAt", "updatedAt");
        }
    }

    // ---------------------------------------------------------------------
    // ExecuteWorkflow: any project, and the answer names what ran
    // ---------------------------------------------------------------------

    private static (Mock<IAssistantExecutionGuard> Guard, Mock<IWorkflowExecutionService> Service) RunnableServices()
    {
        Mock<IAssistantExecutionGuard> guard = new();
        guard.Setup(g => g.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Func<Task<RunRateBucket>>>(), It.IsAny<string>(), It.IsAny<Func<Task<Guid?>>>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, Guid, Func<Task<RunRateBucket>>, string, Func<Task<Guid?>>, CancellationToken>(async (_, _, classify, _, insert, _) =>
            {
                RunRateBucket bucket = await classify();
                await insert();
                return new RunRateDecision(true, bucket, 1, 10, 3600, 0);
            });

        Mock<IWorkflowExecutionService> service = new();
        service.Setup(s => s.ExecuteAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<ExecutionInitiator>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>()))
            .Returns<Guid, Guid, Guid, string?, ExecutionInitiator, CancellationToken, bool, RunChoices?>(
                (_, workflow, _, request, _, _, _, _) => Task.FromResult(new WorkflowExecutionResponse(
                    Guid.NewGuid(), workflow, "Queued", DateTime.UtcNow, null, 0, null, "corr", null,
                    new List<StepResultResponse>(), new List<ReviewScoreResponse>(), request)));
        return (guard, service);
    }

    [Fact]
    public async Task ExecuteWorkflow_runs_a_workflow_of_the_named_project_and_names_it_without_dropping_a_field()
    {
        using World w = new();
        (Mock<IAssistantExecutionGuard> guard, Mock<IWorkflowExecutionService> service) = RunnableServices();
        AssistantToolProvider provider = w.Provider(guard: guard.Object, executionService: service.Object);

        JsonElement result = await InvokeAsync(provider, w.HomePage, "ExecuteWorkflow",
            ("workflowId", w.BetaCut.Id), ("userRequest", "make it pop"), ("regenerateVideoClips", false), ("projectId", w.Beta.Id));

        // The receipt carries the run's ids and state, and no longer echoes the request...
        result.GetProperty("status").GetString().Should().Be("Queued");
        result.TryGetProperty("executionId", out _).Should().BeTrue();
        result.TryGetProperty("UserRequest", out _).Should().BeFalse();
        result.TryGetProperty("userRequest", out _).Should().BeFalse();
        // ...plus the names to quote.
        result.GetProperty("workflowName").GetString().Should().Be("Beta cut");
        result.GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");

        guard.Verify(g => g.TryAcquireAsync(w.Me, w.Beta.Id, It.IsAny<Func<Task<RunRateBucket>>>(), It.IsAny<string>(), It.IsAny<Func<Task<Guid?>>>(), It.IsAny<CancellationToken>()), Times.Once,
            "the rate limit is taken per user and per project, on the project that was resolved");
        service.Verify(s => s.ExecuteAsync(
            w.Beta.Id, w.BetaCut.Id, w.Me, "make it pop", ExecutionInitiator.Assistant, It.IsAny<CancellationToken>(), false, It.IsAny<RunChoices?>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteWorkflow_on_a_foreign_project_is_refused_before_any_slot_is_taken()
    {
        using World w = new();
        (Mock<IAssistantExecutionGuard> guard, Mock<IWorkflowExecutionService> service) = RunnableServices();
        AssistantToolProvider provider = w.Provider(guard: guard.Object, executionService: service.Object);

        JsonElement result = await InvokeAsync(provider, w.HomePage, "ExecuteWorkflow",
            ("workflowId", w.ForeignCut.Id), ("userRequest", null), ("regenerateVideoClips", false), ("projectId", w.Foreign.Id));

        ErrorOf(result).Should().Be(NotFound);
        guard.Verify(g => g.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Func<Task<RunRateBucket>>>(), It.IsAny<string>(), It.IsAny<Func<Task<Guid?>>>(), It.IsAny<CancellationToken>()), Times.Never);
        service.Verify(s => s.ExecuteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<ExecutionInitiator>(),
            It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteWorkflow_asking_about_paid_clips_also_names_the_workflow_and_the_project()
    {
        using World w = new();
        w.Db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = w.BetaCut.Id,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = "{\"maxSpendUsd\":2}"
        });
        w.Db.SaveChanges();
        (Mock<IAssistantExecutionGuard> guard, Mock<IWorkflowExecutionService> service) = RunnableServices();
        AssistantToolProvider provider = w.Provider(guard: guard.Object, executionService: service.Object);

        JsonElement result = await InvokeAsync(provider, w.HomePage, "ExecuteWorkflow",
            ("workflowId", w.BetaCut.Id), ("userRequest", null), ("regenerateVideoClips", null), ("projectId", w.Beta.Id));

        result.GetProperty("needsDecision").GetBoolean().Should().BeTrue();
        result.GetProperty("workflowName").GetString().Should().Be("Beta cut");
        result.GetProperty("projectName").GetString().Should().Be("Spring Promo 2026");
        service.Verify(s => s.ExecuteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<ExecutionInitiator>(),
            It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>()), Times.Never);
    }

    // ---------------------------------------------------------------------
    // A project id never appears where the model would read it out as a name
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Results_that_concern_a_project_carry_its_name_beside_its_id()
    {
        using World w = new();
        AssistantToolProvider provider = w.Provider();
        AssistantToolContext home = w.HomePage;

        foreach ((string tool, (string, object?)[] args) in new (string, (string, object?)[])[]
                 {
                     ("GetProject", [("projectId", w.Beta.Id)]),
                     ("GetWorkflow", [("workflowId", w.BetaCut.Id), ("projectId", w.Beta.Id)]),
                     ("GetWorkflowExecution", [("executionId", w.BetaRun.Id), ("projectId", w.Beta.Id)]),
                     ("ReadProjectFile", [("fileId", w.Notes.Id), ("projectId", w.Beta.Id)]),
                     ("ProposeDeleteProjectFile", [("fileId", w.Notes.Id), ("projectId", w.Beta.Id)]),
                     ("ProposeDeleteWorkflow", [("workflowId", w.BetaCut.Id), ("projectId", w.Beta.Id)])
                 })
        {
            string text = await InvokeTextAsync(provider, home, tool, args);
            JsonElement result = JsonDocument.Parse(text).RootElement;

            ErrorOf(result).Should().BeNull($"'{tool}' should have worked: {text}");
            string key = tool == "GetProject" ? "name" : "projectName";
            result.GetProperty(key).GetString().Should().Be("Spring Promo 2026", tool);
        }
    }
}
