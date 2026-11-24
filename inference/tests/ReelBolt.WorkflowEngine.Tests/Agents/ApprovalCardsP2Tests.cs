using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The permission cards of the workflow patch and clone, picture document and green-screen insert tools: built
/// from the database, the exact arguments and (for a patch) the stored proposal, in the agreed words.
/// </summary>
public sealed class ApprovalCardsP2Tests : IDisposable
{
    private sealed class Caller(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "test@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool EmailVerified => true;
    }

    private readonly Guid _user = Guid.NewGuid();
    private readonly InferenceApiDbContext _db =
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Project _project;
    private readonly Project _other;
    private readonly WorkflowDefinition _workflow;
    private readonly ProjectFile _plate;

    public ApprovalCardsP2Tests()
    {
        _project = new Project { Id = Guid.NewGuid(), Name = "Launch Teaser", OwnerId = _user, OrganizationId = TestTenancy.OrgOf(_user) };
        _other = new Project { Id = Guid.NewGuid(), Name = "Spring Promo", OwnerId = _user, OrganizationId = TestTenancy.OrgOf(_user) };
        _workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Promo film", ProjectId = _project.Id, Kind = WorkflowDefinitionKind.Standard };
        _plate = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = _project.Id,
            OriginalFileName = "phone-plate.mp4",
            OriginalPath = "phone-plate.mp4",
            StorageKey = "k",
            StorageBucket = "b",
            MimeType = "video/mp4",
            SizeBytes = 10,
            UploadedAt = DateTime.UtcNow
        };
        _db.Projects.AddRange(_project, _other);
        _db.WorkflowDefinitions.Add(_workflow);
        _db.ProjectFiles.Add(_plate);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private AssistantApprovalCardBuilder Builder()
    {
        var caller = new Caller(_user);
        return new AssistantApprovalCardBuilder(_db, TestTenancy.Access(_db, caller), caller);
    }

    private Task<ApprovalVerdict> BuildAsync(string tool, ProposalRecord? proposal, params (string Key, object? Value)[] values)
    {
        var args = new AIFunctionArguments();
        foreach ((string key, object? value) in values)
        {
            if (value is not null)
                args[key] = value is Guid g ? g.ToString() : value;
        }

        return Builder().BuildAsync(tool, args, new AssistantToolContext(_user, _project.Id), proposal, CancellationToken.None);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("ApplyPatchWorkflow", "Change a workflow", "Saved.", AssistantActionRisk.Normal)]
    [InlineData("ApplyCloneWorkflow", "Copy a workflow", "Copied.", AssistantActionRisk.Normal)]
    [InlineData("ApplyCreatePictureDocument", "Open an image in the thumbnail studio", "Opened.", AssistantActionRisk.Normal)]
    [InlineData("ApplyGreenScreenInsert", "Set up a green-screen insert", "Workflow ready.", AssistantActionRisk.Normal)]
    [InlineData("ApplySetMediaOverlay", "Show a picture on the video", "Saved.", AssistantActionRisk.Normal)]
    [InlineData("ApplyCleanAudio", "Clean the audio", "Started.", AssistantActionRisk.Normal)]
    [InlineData("ApplyAddLibraryVoice", "Add a library voice", "Added.", AssistantActionRisk.Normal)]
    public void The_catalog_presents_each_new_write_in_the_agreed_words(string tool, string title, string done, string risk)
    {
        AssistantApprovalCatalog.IsGated(tool).Should().BeTrue();
        ApprovalToolInfo info = AssistantApprovalCatalog.Describe(tool);

        info.FallbackTitle.Should().Be(title);
        info.DoneText.Should().Be(done);
        info.Risk.Should().Be(risk);
    }

    [Fact]
    public async Task Patch_shows_each_changed_field_from_the_proposal()
    {
        var proposal = new ProposalRecord("ProposePatchWorkflow", Json("""
            {"changedSteps":[{"stepOrder":3,"label":"Cut scenes","stepType":"VideoCompile",
              "fields":[{"field":"configJson.musicGainDb","before":-6,"after":-12},{"field":"label","before":"Cut","after":"Cut scenes"}]}]}
            """));

        ApprovalVerdict verdict = await BuildAsync("ApplyPatchWorkflow", proposal, ("workflowId", _workflow.Id), ("confirmationToken", "t"));

        ApprovalCard card = verdict.Card!;
        card.Title.Should().Be("Change the workflow “Promo film”");
        card.ProjectName.Should().Be("Launch Teaser");
        card.Changes!.Select(c => c.Text).Should().Contain("Step 3: configJson.musicGainDb: -6 → -12")
            .And.Contain("Step 3: label: Cut → Cut scenes");
        card.Changes!.Should().OnlyContain(c => c.Kind == AssistantChangeKind.Change);
    }

    [Fact]
    public async Task Patch_shows_a_removed_and_a_retargeted_review_loop_before_the_field_lines()
    {
        var proposal = new ProposalRecord("ProposePatchWorkflow", Json("""
            {"changedSteps":[{"stepOrder":3,"label":"Cut scenes","stepType":"VideoCompile",
              "fields":[{"field":"label","before":"Cut","after":"Cut scenes"}]}],
             "normalization":[
               {"change":"removed","stepOrder":3,"label":"Final review","stepType":"ReviewLoop","fromStepOrder":2},
               {"change":"retargeted","stepOrder":4,"label":"Review","stepType":"ReviewLoop","fromStepOrder":2,"toStepOrder":1}]}
            """));

        ApprovalVerdict verdict = await BuildAsync("ApplyPatchWorkflow", proposal, ("workflowId", _workflow.Id), ("confirmationToken", "t"));

        ApprovalCard card = verdict.Card!;
        card.Changes!.Select(c => c.Text).Should().Equal(
            "Step 3: remove “Final review” — a review there would not change anything on a repeat; the steps after it move up one",
            "Step 4: the review goes back to step 1 instead of step 2",
            "Step 3: label: Cut → Cut scenes");
        card.Changes![0].Kind.Should().Be(AssistantChangeKind.Remove);
        card.Changes![1].Kind.Should().Be(AssistantChangeKind.Change);
        card.Changes![2].Kind.Should().Be(AssistantChangeKind.Change);
    }

    [Fact]
    public async Task Patch_renders_a_changed_step_at_its_new_order_with_the_old_one()
    {
        var proposal = new ProposalRecord("ProposePatchWorkflow", Json("""
            {"changedSteps":[{"stepOrder":3,"was":4,"label":"Compile the film","stepType":"VideoCompile",
              "fields":[{"field":"label","before":"Compile","after":"Compile the film"}]}]}
            """));

        ApprovalVerdict verdict = await BuildAsync("ApplyPatchWorkflow", proposal, ("workflowId", _workflow.Id), ("confirmationToken", "t"));

        verdict.Card!.Changes!.Select(c => c.Text).Should().Equal("Step 4 → 3: label: Compile → Compile the film");
    }

    [Fact]
    public async Task A_create_apply_card_renders_the_normalized_additions_not_the_raw_steps_argument()
    {
        var proposal = new ProposalRecord("ProposeWorkflow", Json("""
            {"additions":[{"stepOrder":1,"agentDefinitionId":"00000000-0000-0000-0000-000000000000","stepType":"VideoAnalyze","summary":"Analyze"}],
             "normalization":[{"change":"removed","stepOrder":2,"label":"Final review","stepType":"ReviewLoop","fromStepOrder":1}],
             "proposedSteps":[{"stepOrder":1,"label":"Analyze","stepType":"VideoAnalyze"}]}
            """));
        JsonElement rawSteps = Json("""[{"stepOrder":9,"label":"Raw step","stepType":"Agent"}]""");

        ApprovalVerdict verdict = await BuildAsync("ApplyWorkflow", proposal, ("name", "New film"), ("steps", rawSteps));

        ApprovalCard card = verdict.Card!;
        card.Title.Should().Be("Create the workflow “New film”");
        card.Summary.Should().Be("A new workflow with 1 step.", "the count is the written steps, not the card lines");
        card.Changes!.Select(change => change.Text).Should().Equal(
            "Step 2: remove “Final review” — a review there would not change anything on a repeat; the steps after it move up one",
            "Step 1: Analyze (video analyze)");
        card.Changes!.Should().NotContain(change => change.Text.Contains("Raw step", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Patch_without_a_remembered_proposal_describes_the_operations_it_was_given()
    {
        string operations = """
            [{"op":"renameStep","stepOrder":2,"label":"Intro"},{"op":"setStepConfig","stepOrder":3,"patch":{"musicGainDb":-12,"fade":null}},{"op":"setStepField","stepOrder":4,"field":"maxIterations","value":3}]
            """;

        ApprovalVerdict verdict = await BuildAsync("ApplyPatchWorkflow", null, ("workflowId", _workflow.Id), ("operations", Json(operations)));

        verdict.Card!.Changes!.Select(c => c.Text).Should().Equal(
            "Step 2: rename to “Intro”",
            "Step 3: set musicGainDb to -12, fade to none",
            "Step 4: set maxIterations to 3");
    }

    [Fact]
    public async Task Patch_operation_names_match_case_insensitively_like_the_tool()
    {
        string operations = """
            [{"op":"RenameStep","stepOrder":2,"label":"Intro"},{"op":"SETSTEPFIELD","stepOrder":4,"field":"maxIterations","value":5}]
            """;

        ApprovalVerdict verdict = await BuildAsync("ApplyPatchWorkflow", null, ("workflowId", _workflow.Id), ("operations", Json(operations)));

        verdict.Card!.Changes!.Select(c => c.Text).Should().Equal("Step 2: rename to “Intro”", "Step 4: set maxIterations to 5");
    }

    [Fact]
    public async Task ExecuteWorkflow_names_the_brief_file_of_the_project()
    {
        ProjectFile brief = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = _project.Id,
            OriginalFileName = "launch-brief.md",
            OriginalPath = "launch-brief.md",
            StorageKey = "k2",
            StorageBucket = "b",
            MimeType = "text/markdown",
            SizeBytes = 10,
            UploadedAt = DateTime.UtcNow
        };
        _db.ProjectFiles.Add(brief);
        _db.SaveChanges();

        ApprovalVerdict verdict = await BuildAsync("ExecuteWorkflow", null, ("workflowId", _workflow.Id), ("briefFileId", brief.Id));

        verdict.Card!.Changes!.Select(c => c.Text).Should().Contain("Brief: the file “launch-brief.md”");
    }

    [Fact]
    public async Task ExecuteWorkflow_with_a_brief_file_the_project_does_not_have_says_so()
    {
        ApprovalVerdict verdict = await BuildAsync("ExecuteWorkflow", null, ("workflowId", _workflow.Id), ("briefFileId", Guid.NewGuid()));

        verdict.Card!.Changes!.Select(c => c.Text).Should().Contain(t => t.StartsWith("This run will not start: the brief file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Patch_of_a_workflow_the_project_does_not_have_is_refused()
    {
        ApprovalVerdict verdict = await BuildAsync("ApplyPatchWorkflow", null, ("workflowId", Guid.NewGuid()));

        verdict.Card.Should().BeNull();
        verdict.Refusal.Should().NotBeNull();
    }

    [Fact]
    public async Task Clone_names_the_workflow_the_copy_and_both_projects()
    {
        ApprovalVerdict verdict = await BuildAsync(
            "ApplyCloneWorkflow", null, ("workflowId", _workflow.Id), ("name", "Promo v2"), ("targetProjectId", _other.Id));

        verdict.Card!.Summary.Should().Be("Copy “Promo film” from “Launch Teaser” into “Spring Promo” as “Promo v2”.");
        verdict.Card.ProjectName.Should().Be("Spring Promo");
    }

    [Fact]
    public async Task Clone_into_the_same_project_defaults_the_name()
    {
        ApprovalVerdict verdict = await BuildAsync("ApplyCloneWorkflow", null, ("workflowId", _workflow.Id));

        verdict.Card!.Summary.Should().Be("Copy “Promo film” in “Launch Teaser” as “Promo film (copy)”.");
    }

    [Fact]
    public async Task Clone_into_a_project_the_caller_cannot_see_is_refused()
    {
        ApprovalVerdict verdict = await BuildAsync("ApplyCloneWorkflow", null, ("workflowId", _workflow.Id), ("targetProjectId", Guid.NewGuid()));

        verdict.Card.Should().BeNull();
        verdict.Refusal.Should().NotBeNull();
    }

    [Fact]
    public async Task CreatePictureDocument_uses_the_proposals_image_name_and_the_project()
    {
        var proposal = new ProposalRecord("ProposeCreatePictureDocument", Json("""{"imageName":"frame.png","summary":"x"}"""));

        ApprovalVerdict verdict = await BuildAsync("ApplyCreatePictureDocument", proposal, ("fileId", Guid.NewGuid()), ("name", "Cover"));

        verdict.Card!.Summary.Should().Be("Open “frame.png” in the thumbnail studio as a new picture named “Cover” in “Launch Teaser”. It is free.");
    }

    [Fact]
    public async Task CreatePictureDocument_without_a_proposal_still_says_what_it_does()
    {
        ApprovalVerdict verdict = await BuildAsync("ApplyCreatePictureDocument", null, ("fileId", Guid.NewGuid()));

        verdict.Card!.Summary.Should().Be("Open an image in the thumbnail studio as a new picture in “Launch Teaser”. It is free.");
    }

    [Fact]
    public async Task GreenScreen_says_it_uses_no_model_starts_no_run_and_names_the_clip()
    {
        var proposal = new ProposalRecord("ProposeGreenScreenInsert", Json("""{"workflowName":"Phone insert","action":"create"}"""));

        ApprovalVerdict verdict = await BuildAsync(
            "ApplyGreenScreenInsert", proposal, ("plateFileId", _plate.Id), ("name", "Phone insert"), ("content", Json("""{"from":"ProjectFile","projectFileId":"00000000-0000-0000-0000-000000000001"}""")));

        verdict.Card!.Summary.Should().Be(
            "Create the workflow “Phone insert” in “Launch Teaser” to put your content on the green screen in “phone-plate.mp4”. "
            + "It uses no AI model; only processing time is charged. The clip needs a screen or panel filled with one flat colour, such as green or blue, that stays in view and is mostly uncovered. This does not start a run.");
    }

    private WorkflowDefinition AddWorkflow(string name, bool madeByTheTool)
    {
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = name, ProjectId = _project.Id, Kind = WorkflowDefinitionKind.Standard };
        _db.WorkflowDefinitions.Add(workflow);
        if (madeByTheTool)
        {
            _db.WorkflowSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, StepOrder = 1, Label = "Find the green screen", StepType = StepType.VideoAnalyze });
            _db.WorkflowSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, StepOrder = 2, Label = "Put the content on the screen", StepType = StepType.VideoCompile });
        }
        else
        {
            _db.WorkflowSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, StepOrder = 1, Label = "Hand-made", StepType = StepType.VideoCompile });
        }

        _db.SaveChanges();
        return workflow;
    }

    [Fact]
    public async Task GreenScreen_over_a_workflow_the_tool_made_says_replace_and_is_destructive_whatever_the_proposal_said()
    {
        AddWorkflow("Green-screen insert v1", madeByTheTool: true);
        var proposal = new ProposalRecord("ProposeGreenScreenInsert", Json("""{"workflowName":"Green-screen insert v1","action":"create"}"""));

        ApprovalVerdict verdict = await BuildAsync("ApplyGreenScreenInsert", proposal, ("plateFileId", _plate.Id));

        verdict.Card!.Title.Should().Be("Replace the steps of the existing workflow “Green-screen insert v1”");
        verdict.Card.Risk.Should().Be(AssistantActionRisk.Destructive);
    }

    [Fact]
    public async Task GreenScreen_never_offers_to_replace_a_same_named_workflow_it_did_not_make()
    {
        AddWorkflow("Green-screen insert v1", madeByTheTool: false);

        ApprovalVerdict verdict = await BuildAsync("ApplyGreenScreenInsert", null, ("plateFileId", _plate.Id));

        verdict.Card!.Summary.Should().StartWith("Create the workflow “Green-screen insert v1 (2)”");
        verdict.Card.Risk.Should().Be(AssistantActionRisk.Normal);
    }

    [Fact]
    public async Task GreenScreen_with_a_clip_the_project_does_not_have_is_refused()
    {
        ApprovalVerdict verdict = await BuildAsync("ApplyGreenScreenInsert", null, ("plateFileId", Guid.NewGuid()));

        verdict.Card.Should().BeNull();
        verdict.Refusal.Should().NotBeNull();
    }

    private static readonly System.Text.RegularExpressions.Regex AnyGuid =
        new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    private (WorkflowDefinition Workflow, ProjectFile Photo) AddMediaOverlayTargets()
    {
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Product reel", ProjectId = _project.Id, Kind = WorkflowDefinitionKind.Standard };
        var photo = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = _project.Id,
            OriginalFileName = "testimonial.png",
            OriginalPath = "testimonial.png",
            StorageKey = "k",
            StorageBucket = "b",
            MimeType = "image/png",
            SizeBytes = 10,
            UploadedAt = DateTime.UtcNow,
        };
        _db.WorkflowDefinitions.Add(workflow);
        _db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            StepOrder = 2,
            Label = "Render the film",
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = """{"version":1,"mode":"Reencode"}""",
        });
        _db.ProjectFiles.Add(photo);
        _db.SaveChanges();
        return (workflow, photo);
    }

    [Fact]
    public async Task MediaOverlay_card_names_the_file_form_span_opacity_and_sound_in_plain_words()
    {
        (WorkflowDefinition workflow, ProjectFile photo) = AddMediaOverlayTargets();

        ApprovalVerdict verdict = await BuildAsync("ApplySetMediaOverlay", null,
            ("mediaFileId", photo.Id), ("workflowId", workflow.Id), ("stepOrder", 2),
            ("form", "FullScreen"), ("startSec", 12), ("endSec", 18), ("opacity", 80), ("cardAudio", "Mute"));

        ApprovalCard card = verdict.Card!;
        card.Title.Should().Contain("testimonial.png");
        card.Title.Should().NotBe("Show a picture on the video", "the card is built for this file, not the generic fallback");
        card.Summary.Should().Contain("testimonial.png").And.Contain("a picture").And.Contain("full screen")
            .And.Contain("from 0:12 to 0:18").And.Contain("80% opacity").And.Contain("mutes the film");
        card.Summary.Should().NotContain("keeps the film's sound");
        AnyGuid.IsMatch(string.Join(" ", new[] { card.Title, card.Summary, card.ProjectName })).Should().BeFalse("a card never shows ids alone");
    }

    [Fact]
    public async Task MediaOverlay_card_is_unreachable_for_a_moderation_quarantined_file()
    {
        (WorkflowDefinition workflow, ProjectFile photo) = AddMediaOverlayTargets();
        photo.ModerationStatus = FileModerationStatus.Quarantined;
        await _db.SaveChangesAsync();

        ApprovalVerdict verdict = await BuildAsync("ApplySetMediaOverlay", null,
            ("mediaFileId", photo.Id), ("workflowId", workflow.Id), ("stepOrder", 2));

        verdict.Card.Should().BeNull();
    }

    [Fact]
    public async Task MediaOverlay_card_describes_an_inset_and_ignores_an_images_mixed_sound()
    {
        (WorkflowDefinition workflow, ProjectFile photo) = AddMediaOverlayTargets();

        ApprovalVerdict verdict = await BuildAsync("ApplySetMediaOverlay", null,
            ("mediaFileId", photo.Id), ("workflowId", workflow.Id), ("stepOrder", 2),
            ("form", "Inset"), ("anchor", "BottomRight"), ("size", "Small"), ("audio", "Mixed"), ("audioLevel", "Balanced"));

        ApprovalCard card = verdict.Card!;
        // The target here is a picture: an image has no sound, so the card must not claim one.
        card.Summary.Should().Contain("corner (bottom right)").And.Contain("for the whole film")
            .And.Contain("at full opacity").And.Contain("keeps the film's sound").And.NotContain("sound at Balanced");
        card.Risk.Should().Be(AssistantActionRisk.Normal);
        AnyGuid.IsMatch(card.Summary!).Should().BeFalse();
    }
}
