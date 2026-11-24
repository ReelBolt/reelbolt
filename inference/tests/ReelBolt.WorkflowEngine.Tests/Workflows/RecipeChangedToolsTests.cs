using System;
using System.Collections.Generic;
using System.Linq;
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
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Lint;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// The tools the recipe-first work CHANGED (docs/design/workflow-apps.md 7.2): ListWorkflowTemplates computes the fit to
/// a goal and a project, GetWorkflowTemplate adds the app manifest, the brief bindings and the patches, ValidateWorkflow
/// and ProposeWorkflow run the linter, and the run card says what the workflow cannot read.
/// </summary>
public class RecipeChangedToolsTests : IDisposable
{
    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Project _project;
    private readonly Project _foreign;
    private readonly AgentDefinition _agent;

    public RecipeChangedToolsTests()
    {
        foreach (AgentType type in Enum.GetValues<AgentType>())
            _db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = type.ToString(), AgentType = type, IsBuiltIn = true, CreatedAt = DateTime.UtcNow });
        _agent = _db.AgentDefinitions.Local.First(a => a.AgentType == AgentType.VideoTransform);
        _project = new Project { Id = Guid.NewGuid(), Name = "Spring promo", OwnerId = _owner, OrganizationId = TestTenancy.OrgOf(_owner) };
        Guid stranger = Guid.NewGuid();
        _foreign = new Project { Id = Guid.NewGuid(), Name = "Someone else's film", OwnerId = stranger, OrganizationId = TestTenancy.OrgOf(stranger) };
        _db.Projects.AddRange(_project, _foreign);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private AssistantToolProvider Provider(IWorkflowLinter? linter = null) =>
        AssistantToolTestFactory.CreateProvider(_db, new FakeCurrentUser(_owner), linter: linter);

    private async Task<JsonElement> CallAsync(AssistantToolProvider provider, string tool, params (string Key, object? Value)[] args)
    {
        AIFunction fn = provider.GetTools(new AssistantToolContext(_owner, _project.Id)).Single(t => t.Name == tool);
        AIFunctionArguments arguments = new();
        foreach ((string key, object? value) in args)
            arguments[key] = value;
        object? raw = await fn.InvokeAsync(arguments);
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Mock<IWorkflowLinter> Linter(params LintFinding[] findings)
    {
        Mock<IWorkflowLinter> linter = new();
        linter.Setup(l => l.LintAsync(It.IsAny<WorkflowLintRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowLintReport(findings));
        return linter;
    }

    private void AddFile(string name, string mime)
    {
        _db.ProjectFiles.Add(new ProjectFile { Id = Guid.NewGuid(), ProjectId = _project.Id, OriginalFileName = name, MimeType = mime, StorageKey = "k/" + name, Category = "userFiles" });
        _db.SaveChanges();
    }

    private static JsonElement Recipe(JsonElement list, string key) => list.EnumerateArray().Single(t => t.GetProperty("key").GetString() == key);

    private CreateWorkflowStepRequest AnalyzeStep(int order = 1) =>
        new(_agent.Id, order, null, "Analyze", StepType: "VideoAnalyze", VideoAnalyzeConfigJson: """{"version":1,"source":{"kind":"ProjectFile"}}""");

    // ------------------------------------------------------------------ ListWorkflowTemplates

    [Fact]
    public async Task Templates_without_a_project_carry_the_recipe_facts_and_no_fit()
    {
        _db.Projects.RemoveRange(_project, _foreign);
        _db.SaveChanges();
        AIFunction fn = Provider().GetTools(new AssistantToolContext(_owner, null)).Single(t => t.Name == "ListWorkflowTemplates");

        object? raw = await fn.InvokeAsync(new AIFunctionArguments());
        JsonElement list = JsonDocument.Parse(raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw!.ToString()!).RootElement;

        JsonElement derush = Recipe(list, "edit-my-video");
        derush.GetProperty("title").GetString().Should().Be("Edit my video");
        derush.GetProperty("forGoals").GetArrayLength().Should().BeGreaterThan(0);
        derush.GetProperty("needs").EnumerateArray().Select(n => n.GetString()).Should().Contain("video clips");
        derush.GetProperty("costClass").GetString().Should().NotBeNullOrWhiteSpace();
        derush.GetProperty("briefFieldsRead").EnumerateArray().Select(n => n.GetString()).Should().Contain(["lengthSec"]);
        derush.GetProperty("suggestedStylePreset").GetString().Should().Be("fast-social-reel");
        derush.GetProperty("satisfied").ValueKind.Should().Be(JsonValueKind.Null);
        derush.GetProperty("steps").GetArrayLength().Should().Be(ReelBolt.Shared.Workflows.WorkflowTemplateCatalog.GetByKey("edit-my-video")!.Steps.Count);
    }

    [Fact]
    public async Task Templates_for_a_goal_and_a_project_rank_the_fitting_recipe_first_and_compute_satisfied_and_missing()
    {
        JsonElement withoutClips = await CallAsync(Provider(), "ListWorkflowTemplates", ("goal", "cut down my raw footage, remove dead air"));

        withoutClips[0].GetProperty("key").GetString().Should().Be("edit-my-video");
        withoutClips[0].GetProperty("matchesGoal").GetBoolean().Should().BeTrue();
        JsonElement derush = Recipe(withoutClips, "edit-my-video");
        derush.GetProperty("satisfied").GetBoolean().Should().BeFalse();
        derush.GetProperty("missing").EnumerateArray().Select(m => m.GetProperty("need").GetString()).Should().Contain("video");

        AddFile("clip.mp4", "video/mp4");
        AddFile("track.mp3", "audio/mpeg");
        JsonElement withFiles = await CallAsync(Provider(), "ListWorkflowTemplates");
        Recipe(withFiles, "edit-my-video").GetProperty("missing").EnumerateArray().Select(m => m.GetProperty("need").GetString()).Should().NotContain("video");
        Recipe(withFiles, "edit-my-video").GetProperty("matchesGoal").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Templates_with_a_named_project_of_another_organization_read_like_a_missing_project()
    {
        JsonElement foreign = await CallAsync(Provider(), "ListWorkflowTemplates", ("projectId", _foreign.Id));
        JsonElement missing = await CallAsync(Provider(), "ListWorkflowTemplates", ("projectId", Guid.NewGuid()));

        foreign.GetProperty("error").GetString().Should().Be("project not found");
        foreign.GetRawText().Should().Be(missing.GetRawText());
    }

    // ------------------------------------------------------------------ GetWorkflowTemplate

    [Fact]
    public async Task GetTemplate_adds_the_manifest_the_brief_bindings_and_which_patches_fit()
    {
        JsonElement result = await CallAsync(Provider(), "GetWorkflowTemplate", ("templateKey", "edit-my-video"));

        result.GetProperty("appManifest").GetProperty("title").GetString().Should().Be("Edit my video");
        result.GetProperty("appManifest").GetProperty("inputs").GetArrayLength().Should().BeGreaterThan(0);
        JsonElement bindings = result.GetProperty("briefBindings");
        bindings.GetProperty("reads").EnumerateArray().Single(r => r.GetProperty("field").GetString() == "lengthSec")
            .GetProperty("readBySteps").EnumerateArray().Select(l => l.GetString()).Should().Contain("Compile the edited video");
        JsonElement patches = result.GetProperty("availablePatches");
        patches.GetArrayLength().Should().Be(8);

        JsonElement promo = await CallAsync(Provider(), "GetWorkflowTemplate", ("templateKey", "quick-win-promo-ds"));
        promo.GetProperty("availablePatches").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "addVoiceover")
            .GetProperty("fits").GetBoolean().Should().BeTrue();
        promo.GetProperty("steps").GetArrayLength().Should().Be(7);
    }

    // ------------------------------------------------------------------ ValidateWorkflow

    [Fact]
    public async Task Validate_returns_the_linters_findings_with_fixes_and_is_not_valid_while_one_is_an_error()
    {
        LintFinding error = new("WL001", LintSeverity.Error, 3, "The voiceover reads a script that comes later.");
        LintFinding silent = new("WL016", LintSeverity.Warning, null, "The film is silent.", new LintFix(LintFixKind.AddStep, "Add a voiceover step", "addVoiceover"));

        JsonElement result = await CallAsync(Provider(Linter(error, silent).Object), "ValidateWorkflow",
            ("steps", new List<CreateWorkflowStepRequest> { AnalyzeStep() }), ("brief", JsonSerializer.SerializeToElement(new { language = "it" })));

        result.GetProperty("valid").GetBoolean().Should().BeFalse();
        JsonElement findings = result.GetProperty("findings");
        findings.GetArrayLength().Should().Be(2);
        findings[0].GetProperty("ruleId").GetString().Should().Be("WL001");
        findings[1].GetProperty("fix").GetProperty("patchId").GetString().Should().Be("addVoiceover");
    }

    [Fact]
    public async Task Validate_with_only_warnings_is_valid_and_without_a_linter_keeps_the_old_shape()
    {
        LintFinding warning = new("WL011", LintSeverity.Info, null, "Language is not used by this app.");
        List<CreateWorkflowStepRequest> steps = [AnalyzeStep()];

        JsonElement linted = await CallAsync(Provider(Linter(warning).Object), "ValidateWorkflow", ("steps", steps));
        JsonElement plain = await CallAsync(Provider(), "ValidateWorkflow", ("steps", steps));

        linted.GetProperty("valid").GetBoolean().Should().BeTrue();
        linted.GetProperty("findings").GetArrayLength().Should().Be(1);
        plain.GetProperty("valid").GetBoolean().Should().BeTrue();
        plain.GetProperty("findings").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Validate_reports_errors_about_the_project_apart_so_zero_workflow_errors_is_reachable()
    {
        LintFinding noClips = new("WL010", LintSeverity.Error, 1, "The project has no video clips yet.");

        JsonElement result = await CallAsync(Provider(Linter(noClips).Object), "ValidateWorkflow", ("steps", new List<CreateWorkflowStepRequest> { AnalyzeStep() }));

        result.GetProperty("valid").GetBoolean().Should().BeTrue();
        result.GetProperty("findings").GetArrayLength().Should().Be(0);
        result.GetProperty("needsBeforeRunning").EnumerateArray().Single().GetProperty("ruleId").GetString().Should().Be("WL010");
    }

    [Fact]
    public async Task Validate_refuses_a_bad_brief_and_never_reveals_a_foreign_project()
    {
        List<CreateWorkflowStepRequest> steps = [AnalyzeStep()];

        (await CallAsync(Provider(Linter().Object), "ValidateWorkflow", ("steps", steps), ("brief", JsonSerializer.SerializeToElement(new { lengthSec = 1 }))))
            .GetProperty("error").GetString().Should().Contain("lengthSec");
        (await CallAsync(Provider(Linter().Object), "ValidateWorkflow", ("steps", steps), ("projectId", _foreign.Id)))
            .GetProperty("error").GetString().Should().Be("project not found");
    }

    // ------------------------------------------------------------------ ProposeWorkflow

    [Fact]
    public async Task Propose_refuses_a_structural_lint_error_with_the_findings_and_mints_no_token()
    {
        LintFinding error = new("WL003", LintSeverity.Error, 1, "The review loop does not point at the step that makes the film.");
        AssistantToolProvider provider = Provider(Linter(error).Object);

        JsonElement result = await CallAsync(provider, "ProposeWorkflow", ("workflowId", null), ("name", "Mine"), ("steps", new List<CreateWorkflowStepRequest> { AnalyzeStep() }));

        result.GetProperty("code").GetString().Should().Be("WORKFLOW_LINT_ERRORS");
        result.GetProperty("findings").EnumerateArray().Single().GetProperty("ruleId").GetString().Should().Be("WL003");
        result.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        provider.LastProposedDiff.Should().BeNull("a refused proposal is never handed to the page's review tray");
    }

    [Fact]
    public async Task Propose_still_proposes_when_the_only_errors_are_about_the_projects_state()
    {
        LintFinding noClips = new("WL010", LintSeverity.Error, 1, "The project has no video clips yet.");

        JsonElement result = await CallAsync(Provider(Linter(noClips).Object), "ProposeWorkflow", ("workflowId", null), ("name", "Mine"), ("steps", new List<CreateWorkflowStepRequest> { AnalyzeStep() }));

        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Propose_of_an_edit_is_not_blocked_by_an_error_the_stored_workflow_already_had()
    {
        Guid workflowId = Guid.NewGuid();
        _db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = workflowId, ProjectId = _project.Id, Name = "Old", Kind = WorkflowDefinitionKind.Standard });
        _db.WorkflowSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflowId, AgentDefinitionId = _agent.Id, StepOrder = 1, StepType = StepType.VideoAnalyze, Label = "Analyze", VideoAnalyzeConfigJson = """{"version":1,"source":{"kind":"ProjectFile"}}""" });
        _db.SaveChanges();
        LintFinding existing = new("WL003", LintSeverity.Error, 1, "The review loop does not point at the step that makes the film.");

        JsonElement result = await CallAsync(Provider(Linter(existing).Object), "ProposeWorkflow", ("workflowId", workflowId), ("name", "Old"),
            ("steps", new List<CreateWorkflowStepRequest> { AnalyzeStep() with { Label = "Analyze the clips" } }));

        result.TryGetProperty("code", out _).Should().BeFalse("the same error was already there before this edit");
        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ------------------------------------------------------------------ ExecuteWorkflow

    [Fact]
    public void Execute_documents_the_brief_and_the_run_card_note()
    {
        string description = Provider().GetTools(new AssistantToolContext(_owner, _project.Id)).Single(t => t.Name == "ExecuteWorkflow").Description;

        description.Should().Contain("brief argument").And.Contain("lengthSec").And.Contain("sourceFiles").And.Contain("Not used by this app");
    }

    private async Task<ApprovalCard> RunCardAsync(Guid workflowId, object brief)
    {
        FakeCurrentUser user = new(_owner);
        AssistantApprovalCardBuilder builder = new(_db, TestTenancy.Access(_db, user), user);
        AIFunctionArguments args = new() { ["workflowId"] = workflowId.ToString(), ["regenerateVideoClips"] = false, ["brief"] = JsonSerializer.SerializeToElement(brief) };
        ApprovalVerdict verdict = await builder.BuildAsync("ExecuteWorkflow", args, new AssistantToolContext(_owner, _project.Id), null, CancellationToken.None);
        return verdict.Card!;
    }

    private Guid SeedWorkflow(params StepType[] types)
    {
        Guid id = Guid.NewGuid();
        _db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = id, ProjectId = _project.Id, Name = "Launch film", Kind = WorkflowDefinitionKind.Standard });
        for (int i = 0; i < types.Length; i++)
            _db.WorkflowSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = id, AgentDefinitionId = _agent.Id, StepOrder = i + 1, StepType = types[i], Label = types[i].ToString() });
        _db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task The_run_card_lists_each_set_field_and_says_which_the_workflow_does_not_read()
    {
        Guid cutOnly = SeedWorkflow(StepType.VideoAnalyze, StepType.VideoCompile);

        ApprovalCard card = await RunCardAsync(cutOnly, new { language = "it", lengthSec = 45 });

        string[] lines = card.Changes!.Select(c => c.Text).ToArray();
        lines.Should().Contain("Language: it");
        lines.Should().Contain("Target length: 45 seconds");
        lines.Should().Contain("Not used by this app: language");
    }

    [Fact]
    public async Task The_run_card_has_no_not_used_line_when_every_field_is_read()
    {
        Guid narrated = SeedWorkflow(StepType.VideoAnalyze, StepType.Voiceover, StepType.VideoCompile);

        ApprovalCard card = await RunCardAsync(narrated, new { language = "it", lengthSec = 45 });

        card.Changes!.Select(c => c.Text).Should().NotContain(t => t.StartsWith("Not used by this app"));
    }
}
