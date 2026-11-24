using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Briefs;

/// <summary>
/// The Inference API half of the typed run brief: the split into the run's homes, the 400s that cost nothing,
/// the stored column and event, retry, the composed response, the assistant tool, and a run with no brief
/// staying exactly as it was.
/// </summary>
public class RunBriefApiTests
{
    private static readonly OrganizationEntitlementSet Pro = new() { Tier = "pro", MaxOutputHeight = 2160 };

    private static RunBrief Italian => new(
        Language: "it", SourceLanguage: "en", LengthSec: 45, Format: "Vertical", Resolution: "720p",
        StylePreset: "fast-social-reel", Tone: StyleTone.Calm, DesignSystem: "none",
        Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated), Notes: "a launch film");

    [Fact]
    public async Task A_brief_is_split_into_the_existing_columns_and_the_new_one()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(
            h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(Brief: Italian), default);

        ObjectResult obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(201);
        WorkflowExecution row = await db.WorkflowExecutions.SingleAsync();
        row.OutputFormatOverride.Should().Be(RunOutputFormat.Vertical);
        row.OutputResolution.Should().Be(720);
        row.UserRequest.Should().Be("a launch film", "notes map to the existing user_request path");
        RunStyleSnapshot style = StyleJson.TryParseSnapshot(row.StyleConfigJson)!;
        style.PresetKey.Should().Be("fast-social-reel");
        style.Config.Tone.Should().Be(StyleTone.Calm, "tone is folded into the style snapshot, overriding the preset's axis");
        StoredRunBrief stored = RunBriefJson.TryParse(row.RunBriefJson)!;
        stored.Language.Should().Be("it");
        stored.SourceLanguage.Should().Be("en");
        stored.LengthSec.Should().Be(45);
        stored.DesignSystem.Should().Be("none");
        stored.Voice!.Mode.Should().Be(RunBriefVoiceMode.Narrated);
        row.RunBriefJson.Should().NotContain("Vertical").And.NotContain("calm", "style, format and resolution live in their own columns only");

        RunBrief view = ((WorkflowExecutionResponse)obj.Value!).Brief!;
        view.Language.Should().Be("it");
        view.Format.Should().Be("Vertical");
        view.Resolution.Should().Be("720p");
        view.StylePreset.Should().Be("fast-social-reel");
        view.Tone.Should().Be(StyleTone.Calm);
        h.Publish.Verify(p => p.Publish(
            It.Is<WorkflowExecutionRequested>(m => m.RunBriefJson == row.RunBriefJson && m.OutputResolution == 720),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_tone_alone_creates_a_one_axis_Custom_style()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);

        await h.Controller.Execute(h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(Brief: new RunBrief(Tone: StyleTone.Playful)), default);

        WorkflowExecution row = await db.WorkflowExecutions.SingleAsync();
        RunStyleSnapshot style = StyleJson.TryParseSnapshot(row.StyleConfigJson)!;
        style.Name.Should().Be("Custom");
        style.Config.Should().Be(new StyleConfig(Tone: StyleTone.Playful));
        row.RunBriefJson.Should().BeNull("a tone has no column of its own");
    }

    [Fact]
    public async Task A_run_with_no_brief_is_stored_and_published_exactly_as_before()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(
            h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(UserRequest: "go", OutputFormat: "Square", OutputResolution: "1080p", StylePresetKey: "fast-social-reel"), default);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
        WorkflowExecution row = await db.WorkflowExecutions.SingleAsync();
        row.RunBriefJson.Should().BeNull();
        row.UserRequest.Should().Be("go");
        row.OutputFormatOverride.Should().Be(RunOutputFormat.Square);
        row.OutputResolution.Should().Be(1080);
        StyleJson.TryParseSnapshot(row.StyleConfigJson)!.Config.Should().Be(StylePresetCatalog.Find("fast-social-reel")!.Config.Sanitized());
        h.Publish.Verify(p => p.Publish(It.Is<WorkflowExecutionRequested>(m => m.RunBriefJson == null), It.IsAny<CancellationToken>()), Times.Once);
        ((WorkflowExecutionResponse)((ObjectResult)result.Result!).Value!).Brief!.Language.Should().BeNull();
    }

    [Fact]
    public async Task A_run_with_nothing_chosen_has_no_brief_in_its_response()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(), default);

        ((WorkflowExecutionResponse)((ObjectResult)result.Result!).Value!).Brief.Should().BeNull();
        (await db.WorkflowExecutions.SingleAsync()).RunBriefJson.Should().BeNull();
    }

    [Theory]
    [InlineData("language", "not a language tag")]
    [InlineData("length", "between 5 and 600")]
    [InlineData("designSystem", "designSystem must be")]
    [InlineData("format", "Unknown output format")]
    [InlineData("resolution", "Unknown output resolution")]
    [InlineData("twoStyles", "Choose one style")]
    [InlineData("legacyFormat", "set both inside the brief")]
    [InlineData("legacyNotes", "set both inside the brief")]
    [InlineData("missingFile", "source files were not found")]
    public async Task A_bad_brief_is_a_400_that_writes_nothing_and_publishes_nothing(string which, string message)
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        ExecuteWorkflowRequest request = which switch
        {
            "language" => new(Brief: new RunBrief(Language: "italiano molto")),
            "length" => new(Brief: new RunBrief(LengthSec: 9000)),
            "designSystem" => new(Brief: new RunBrief(DesignSystem: "somebrand")),
            "format" => new(Brief: new RunBrief(Format: "Cinemascope")),
            "resolution" => new(Brief: new RunBrief(Resolution: "1440p")),
            "twoStyles" => new(StyleId: Guid.NewGuid(), Brief: new RunBrief(StylePreset: "fast-social-reel")),
            "legacyFormat" => new(OutputFormat: "Square", Brief: new RunBrief(Format: "Vertical")),
            "legacyNotes" => new(UserRequest: "a", Brief: new RunBrief(Notes: "b")),
            _ => new(Brief: new RunBrief(SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.Video, [Guid.NewGuid()])]))
        };

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(h.Project.Id, h.Workflow.Id, request, default);

        ObjectResult obj = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(obj.Value).Should().Contain(message);
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
        h.Publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Source_files_of_this_project_are_stored_by_role()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = h.Project.Id,
            OriginalFileName = "a.mp4",
            StorageKey = "k",
            MimeType = "video/mp4",
            UploadedAt = DateTime.UtcNow
        };
        db.ProjectFiles.Add(file);
        await db.SaveChangesAsync();

        await h.Controller.Execute(h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(
            Brief: new RunBrief(SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.Video, [file.Id, file.Id])])), default);

        StoredRunBrief stored = RunBriefJson.TryParse((await db.WorkflowExecutions.SingleAsync()).RunBriefJson)!;
        stored.SourceFiles!.Single().Role.Should().Be(RunBriefFileRole.Video);
        stored.SourceFiles!.Single().FileIds.Should().Equal(file.Id);
    }

    [Fact]
    public async Task Retry_republishes_the_rows_brief_and_the_list_and_detail_show_it()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = h.Workflow.Id,
            ProjectId = h.Project.Id,
            OrganizationId = h.Project.OrganizationId,
            Status = ExecutionStatus.Failed,
            QueuedAt = DateTime.UtcNow,
            RunBriefJson = "{\"version\":1,\"language\":\"it\",\"lengthSec\":45}"
        };
        db.WorkflowExecutions.Add(run);
        await db.SaveChangesAsync();

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.RetryExecution(h.Project.Id, h.Workflow.Id, run.Id, null, default);

        ((WorkflowExecutionResponse)((ObjectResult)result.Result!).Value!).Brief!.LengthSec.Should().Be(45);
        h.Publish.Verify(p => p.Publish(
            It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == run.Id && m.RunBriefJson == run.RunBriefJson), It.IsAny<CancellationToken>()), Times.Once);
        (await db.WorkflowExecutions.SingleAsync()).RunBriefJson.Should().Be(run.RunBriefJson, "a retry re-queues the same row, brief intact");
    }

    [Fact]
    public async Task A_retry_with_a_brief_in_the_body_is_a_400_and_publishes_nothing()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = h.Workflow.Id,
            ProjectId = h.Project.Id,
            OrganizationId = h.Project.OrganizationId,
            Status = ExecutionStatus.Failed,
            QueuedAt = DateTime.UtcNow
        };
        db.WorkflowExecutions.Add(run);
        await db.SaveChangesAsync();

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.RetryExecution(
            h.Project.Id, h.Workflow.Id, run.Id, new ExecuteWorkflowRequest(Brief: new RunBrief(Language: "it")), default);

        JsonSerializer.Serialize(result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value).Should().Contain("reuses the run\\u0027s own brief");
        h.Publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retry_republishes_every_run_start_property_of_the_row()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = h.Workflow.Id,
            ProjectId = h.Project.Id,
            OrganizationId = h.Project.OrganizationId,
            Status = ExecutionStatus.Failed,
            QueuedAt = DateTime.UtcNow
        };
        RunStateCopySiteDriftGuardTests.FillRunStartState(run);
        db.WorkflowExecutions.Add(run);
        await db.SaveChangesAsync();
        WorkflowExecutionRequested? sent = null;
        h.Publish.Setup(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowExecutionRequested, CancellationToken>((m, _) => sent = m).Returns(Task.CompletedTask);

        await h.Controller.RetryExecution(h.Project.Id, h.Workflow.Id, run.Id, null, default);

        sent.Should().NotBeNull();
        RunStateCopySiteDriftGuardTests.AssertEventMirrors(run, sent!);
    }

    [Fact]
    public async Task Design_system_none_writes_no_pin_while_project_keeps_it()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        var pins = new Mock<ReelBolt.Inference.Api.Services.DesignSystems.IDesignSystemPinService>();
        pins.Setup(p => p.BuildPinJsonAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync("{\"pin\":1}");
        var service = new WorkflowExecutionService(db, h.Publish.Object, NullLogger<WorkflowExecutionService>.Instance, h.Gate, null, pins.Object);

        await service.ExecuteAsync(h.Project.Id, h.Workflow.Id, h.Project.OwnerId, null, ExecutionInitiator.Human, default,
            runChoices: new RunChoices(null, null, null, new StoredRunBrief(DesignSystem: "none")));
        await service.ExecuteAsync(h.Project.Id, h.Workflow.Id, h.Project.OwnerId, null, ExecutionInitiator.Human, default,
            runChoices: new RunChoices(null, null, null, new StoredRunBrief(Language: "it")));

        List<WorkflowExecution> rows = await db.WorkflowExecutions.OrderBy(e => e.QueuedAt).ToListAsync();
        rows.Count(r => r.DesignSystemPinJson is null).Should().Be(1);
        rows.Single(r => r.RunBriefJson!.Contains("none")).DesignSystemPinJson.Should().BeNull();
        rows.Single(r => r.RunBriefJson!.Contains("\"it\"")).DesignSystemPinJson.Should().Be("{\"pin\":1}");
    }

    [Fact]
    public async Task Source_files_of_another_project_or_quarantined_are_refused()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        ProjectFile Make(Guid projectId, CsamScanStatus status = CsamScanStatus.Pending) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "a.mp4",
            StorageKey = "k",
            MimeType = "video/mp4",
            UploadedAt = DateTime.UtcNow,
            CsamStatus = status
        };
        ProjectFile foreign = Make(Guid.NewGuid());
        ProjectFile quarantined = Make(h.Project.Id, CsamScanStatus.Quarantined);
        db.ProjectFiles.AddRange(foreign, quarantined);
        await db.SaveChangesAsync();

        foreach (ProjectFile file in new[] { foreign, quarantined })
        {
            ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(h.Project.Id, h.Workflow.Id,
                new ExecuteWorkflowRequest(Brief: new RunBrief(SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.Video, [file.Id])])), default);
            result.Result.Should().BeOfType<BadRequestObjectResult>();
        }

        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Overlay_files_must_be_pictures_or_videos_and_in_the_project()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        ProjectFile Make(Guid projectId, string mimeType, CsamScanStatus status = CsamScanStatus.Pending) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "a",
            StorageKey = "k",
            MimeType = mimeType,
            UploadedAt = DateTime.UtcNow,
            CsamStatus = status
        };
        ProjectFile picture = Make(h.Project.Id, "image/png");
        ProjectFile video = Make(h.Project.Id, "video/mp4");
        ProjectFile audio = Make(h.Project.Id, "audio/mpeg");
        ProjectFile foreign = Make(Guid.NewGuid(), "image/png");
        ProjectFile quarantined = Make(h.Project.Id, "image/png", CsamScanStatus.Quarantined);
        db.ProjectFiles.AddRange(picture, video, audio, foreign, quarantined);
        await db.SaveChangesAsync();

        // A picture and a video of this project are accepted.
        ActionResult<WorkflowExecutionResponse> ok = await h.Controller.Execute(h.Project.Id, h.Workflow.Id,
            new ExecuteWorkflowRequest(Brief: new RunBrief(SourceFiles:
                [new RunBriefSourceFiles(RunBriefFileRole.Overlay, [picture.Id, video.Id])])), default);
        ok.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);

        foreach (ProjectFile file in new[] { audio, foreign, quarantined })
        {
            ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(h.Project.Id, h.Workflow.Id,
                new ExecuteWorkflowRequest(Brief: new RunBrief(SourceFiles:
                    [new RunBriefSourceFiles(RunBriefFileRole.Overlay, [file.Id])])), default);
            result.Result.Should().BeOfType<BadRequestObjectResult>(file.Id.ToString());
        }

        (await db.WorkflowExecutions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_assistant_rest_twin_splits_the_brief_too()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        var user = new FakeCurrentUser(h.Project.OwnerId);
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "Assistant:MaxExecutionsPerHour", "10" } }).Build();
        var twin = new WorkflowAssistantController(
            db, user, TestTenancy.Access(db, user), new WorkflowEditDiffService(db), new AssistantExecutionGuard(db, config),
            h.ExecutionService, planGate: h.Gate, costEstimator: h.Estimator);

        IActionResult bad = (await twin.Execute(h.Project.Id, h.Workflow.Id,
            new ExecuteWorkflowRequest(OutputFormat: "Square", Brief: new RunBrief(Format: "Vertical")), default)).Result!;
        bad.Should().BeOfType<BadRequestObjectResult>();

        await twin.Execute(h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(Brief: Italian), default);

        WorkflowExecution row = await db.WorkflowExecutions.SingleAsync();
        RunBriefJson.TryParse(row.RunBriefJson)!.Language.Should().Be("it");
        row.OutputFormatOverride.Should().Be(RunOutputFormat.Vertical);
        row.UserRequest.Should().Be("a launch film");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_assistant_tool_runs_the_same_lint_gate_enforced_or_warn_only(bool enforce)
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        WorkflowStep last = db.WorkflowSteps.Single(s => s.WorkflowDefinitionId == h.Workflow.Id && s.StepOrder == 2);
        last.StepType = StepType.ReviewLoop;
        last.LoopTargetStepOrder = 9; // a loop that goes nowhere: WL003, an error
        await db.SaveChangesAsync();
        IConfiguration flag = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Workflows:Lint:Enforce"] = enforce ? "true" : "false" }).Build();
        WorkflowLintGate lint = new(
            new WorkflowLinter(new ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit.FixedFactsSource(UnknownLintFacts.Instance)),
            db, flag, NullLogger<WorkflowLintGate>.Instance);
        AssistantToolProvider provider = ToolProvider(db, h, lintGate: lint);

        JsonElement result = await InvokeToolAsync(provider, new AssistantToolContext(h.Project.OwnerId, h.Project.Id), "ExecuteWorkflow",
            ("workflowId", h.Workflow.Id), ("regenerateVideoClips", false));

        if (enforce)
        {
            result.GetProperty("code").GetString().Should().Be("WORKFLOW_LINT_ERRORS");
            result.GetProperty("executed").GetBoolean().Should().BeFalse();
            result.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("ruleId").GetString()).Should().Contain("WL003");
            (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
        }
        else
        {
            (await db.WorkflowExecutions.CountAsync()).Should().Be(1, "warn-only never changes the answer");
        }
    }

    [Fact]
    public async Task The_assistant_tool_takes_a_brief_and_refuses_a_bad_one_before_anything_is_spent()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        AssistantToolProvider provider = ToolProvider(db, h);
        var context = new AssistantToolContext(h.Project.OwnerId, h.Project.Id);

        JsonElement unknown = await InvokeToolAsync(provider, context, "ExecuteWorkflow",
            ("workflowId", h.Workflow.Id), ("regenerateVideoClips", false), ("brief", JsonDocument.Parse("""{"langauge":"it"}""").RootElement.Clone()));
        unknown.GetProperty("error").GetString().Should().Contain("not recognised");

        JsonElement conflict = await InvokeToolAsync(provider, context, "ExecuteWorkflow",
            ("workflowId", h.Workflow.Id), ("regenerateVideoClips", false), ("outputFormat", "Square"),
            ("brief", JsonDocument.Parse("""{"format":"Vertical"}""").RootElement.Clone()));
        conflict.GetProperty("error").GetString().Should().Contain("set both inside the brief");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);

        await InvokeToolAsync(provider, context, "ExecuteWorkflow",
            ("workflowId", h.Workflow.Id), ("regenerateVideoClips", false),
            ("brief", JsonDocument.Parse("""{"language":"it","lengthSec":30,"tone":"Calm","notes":"hi"}""").RootElement.Clone()));
        WorkflowExecution row = await db.WorkflowExecutions.SingleAsync();
        RunBriefJson.TryParse(row.RunBriefJson)!.Language.Should().Be("it");
        row.UserRequest.Should().Be("hi");
        StyleJson.TryParseSnapshot(row.StyleConfigJson)!.Config.Tone.Should().Be(StyleTone.Calm);
    }

    // ---- A video step with no video chosen is refused before anything is queued (WL010.openVideoSlot) ----

    private const string OpenSlot = "{\"source\":{\"kind\":\"ProjectFile\"}}";

    /// <summary>A lint gate with the flag OFF: the open-slot refusal must not depend on <c>Workflows:Lint:Enforce</c>.</summary>
    private static WorkflowLintGate WarnOnlyLint(InferenceApiDbContext db) => new(
        new WorkflowLinter(new ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit.FixedFactsSource(UnknownLintFacts.Instance)),
        db, new ConfigurationBuilder().Build(), NullLogger<WorkflowLintGate>.Instance);

    private static async Task<WorkflowStep> AddOpenVideoStepAsync(InferenceApiDbContext db, Harness h, string? source = null)
    {
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = h.Workflow.Id,
            StepOrder = 3,
            Label = "Analyze source video",
            StepType = StepType.VideoAnalyze,
            AgentDefinitionId = h.Workflow.Steps.First().AgentDefinitionId,
            VideoAnalyzeConfigJson = source ?? OpenSlot
        };
        db.WorkflowSteps.Add(step);
        await db.SaveChangesAsync();
        return step;
    }

    private static void ShouldBeVideoNotChosen(ObjectResult? refusal)
    {
        refusal.Should().BeOfType<BadRequestObjectResult>().Which.StatusCode.Should().Be(400);
        string json = JsonSerializer.Serialize(refusal!.Value);
        json.Should().Contain("VIDEO_SOURCE_NOT_CHOSEN").And.Contain("Analyze source video").And.Contain("Choose a video");
        json.Should().NotMatchRegex("[0-9a-f]{8}-[0-9a-f]{4}-", "the sentence names the step, never an id");
    }

    [Fact]
    public async Task Execute_refuses_an_open_video_slot_with_no_video_in_the_brief_before_anything_is_queued()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        await AddOpenVideoStepAsync(db, h);
        // The project HAS a video: the engine still fills the slot only from the brief, so having one is not enough.
        db.ProjectFiles.Add(new ProjectFile { Id = Guid.NewGuid(), ProjectId = h.Project.Id, OriginalFileName = "a.mp4", StorageKey = "k", MimeType = "video/mp4", UploadedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var user = new FakeCurrentUser(h.Project.OwnerId);
        var controller = new WorkflowsController(
            db, user, TestTenancy.Access(db, user), h.Publish.Object,
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            h.ExecutionService, NullLogger<WorkflowsController>.Instance, planGate: h.Gate, costEstimator: h.Estimator, lintGate: WarnOnlyLint(db));

        foreach (RunBrief? brief in new RunBrief?[] { null, new RunBrief(Language: "it"), new RunBrief(SourceFiles: []) })
        {
            ActionResult<WorkflowExecutionResponse> result = await controller.Execute(h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(Brief: brief), default);
            ShouldBeVideoNotChosen(result.Result as ObjectResult);
        }

        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
        h.Publish.Verify(p => p.Publish(It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_run_whose_brief_names_a_video_or_whose_step_pins_one_starts_as_before()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        WorkflowStep step = await AddOpenVideoStepAsync(db, h);
        ProjectFile video = new() { Id = Guid.NewGuid(), ProjectId = h.Project.Id, OriginalFileName = "a.mp4", StorageKey = "k", MimeType = "video/mp4", UploadedAt = DateTime.UtcNow };
        db.ProjectFiles.Add(video);
        await db.SaveChangesAsync();
        var user = new FakeCurrentUser(h.Project.OwnerId);
        var controller = new WorkflowsController(
            db, user, TestTenancy.Access(db, user), h.Publish.Object,
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            h.ExecutionService, NullLogger<WorkflowsController>.Instance, planGate: h.Gate, costEstimator: h.Estimator, lintGate: WarnOnlyLint(db));

        ActionResult<WorkflowExecutionResponse> chosen = await controller.Execute(h.Project.Id, h.Workflow.Id,
            new ExecuteWorkflowRequest(Brief: new RunBrief(SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.Video, [video.Id])])), default);
        chosen.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);

        step.VideoAnalyzeConfigJson = "{\"source\":{\"kind\":\"ProjectFile\",\"projectFileId\":\"" + video.Id + "\"}}";
        await db.SaveChangesAsync();
        ActionResult<WorkflowExecutionResponse> pinned = await controller.Execute(h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(), default);
        pinned.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task The_assistant_rest_twin_and_the_assistant_tool_refuse_it_too()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        await AddOpenVideoStepAsync(db, h);
        var user = new FakeCurrentUser(h.Project.OwnerId);
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "Assistant:MaxExecutionsPerHour", "10" } }).Build();
        var twin = new WorkflowAssistantController(
            db, user, TestTenancy.Access(db, user), new WorkflowEditDiffService(db), new AssistantExecutionGuard(db, config),
            h.ExecutionService, planGate: h.Gate, costEstimator: h.Estimator, lintGate: WarnOnlyLint(db));

        ShouldBeVideoNotChosen((await twin.Execute(h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(), default)).Result as ObjectResult);

        AssistantToolProvider provider = ToolProvider(db, h, lintGate: WarnOnlyLint(db));
        JsonElement result = await InvokeToolAsync(provider, new AssistantToolContext(h.Project.OwnerId, h.Project.Id), "ExecuteWorkflow",
            ("workflowId", h.Workflow.Id), ("regenerateVideoClips", false));
        result.GetProperty("executed").GetBoolean().Should().BeFalse();
        result.GetProperty("code").GetString().Should().Be("VIDEO_SOURCE_NOT_CHOSEN");
        result.GetProperty("message").GetString().Should().Contain("Analyze source video").And.Contain("Choose a video");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Retry_and_resume_refuse_it_from_the_runs_own_steps_and_stored_brief_and_resume_judges_only_the_steps_it_runs()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        WorkflowStep step = await AddOpenVideoStepAsync(db, h);
        var user = new FakeCurrentUser(h.Project.OwnerId);
        RunStartGate gate = new(db, user, null, null, new ReelBolt.Inference.Api.Services.DesignSystems.DesignBuildEstimateGate(db, null, null), lintGate: WarnOnlyLint(db));
        List<WorkflowStep> steps = [.. db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == h.Workflow.Id)];
        RunStartRun run = new(Guid.NewGuid(), null);
        Guid fileId = Guid.NewGuid();
        RunBrief? stored = WorkflowLintGate.BriefFromStored(RunBriefJson.Serialize(new StoredRunBrief(SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.Video, [fileId])])));

        ShouldBeVideoNotChosen(await gate.RefuseAsync(h.Project, h.Workflow.Id, null, RunStartPricing.AllOf(steps), null, default, run, null) as ObjectResult);
        ShouldBeVideoNotChosen(await gate.RefuseAsync(h.Project, h.Workflow.Id, null, RunStartPricing.Charged(steps, [step.StepOrder]), null, default, run, null) as ObjectResult);
        (await gate.RefuseAsync(h.Project, h.Workflow.Id, null, RunStartPricing.Charged(steps, [1]), null, default, run, null))
            .Should().BeNull("the analyze step already ran, so it is not this resume's problem");
        (await gate.RefuseAsync(h.Project, h.Workflow.Id, null, RunStartPricing.AllOf(steps), null, default, run, stored))
            .Should().BeNull("the run's stored brief names the video");
    }

    [Fact]
    public async Task An_observing_lint_and_the_editor_lint_never_judge_the_open_slot()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        await AddOpenVideoStepAsync(db, h);

        (await WarnOnlyLint(db).CheckAsync(h.Project, h.Workflow.Id, null, null, null, default, atRunStart: false)).Should().BeNull();
        WorkflowLintReport editor = await new WorkflowLinter(new ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit.FixedFactsSource(UnknownLintFacts.Instance))
            .LintAsync(new WorkflowLintRequest(h.Project.OrganizationId, h.Project.Id, [.. db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == h.Workflow.Id)]), default);
        editor.Findings.Should().NotContain(f => f.MessageKey == "WL010.openVideoSlot");
    }

    private static async Task<WorkflowStep> AddVideoGenerateAsync(InferenceApiDbContext db, Harness h, string configJson)
    {
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = h.Workflow.Id,
            StepOrder = 3,
            Label = "Avatar clip",
            StepType = StepType.VideoGenerate,
            AgentDefinitionId = h.Workflow.Steps.First().AgentDefinitionId,
            VideoGenerateConfigJson = configJson
        };
        db.WorkflowSteps.Add(step);
        await db.SaveChangesAsync();
        return step;
    }

    private const string TalkingHead =
        "{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\",\"model\":\"minimax/h3-max/lip-sync/image-to-video\",\"maxSpendCredits\":10,\"avatarAudio\":{\"from\":\"Step\",\"stepOrder\":2}}";

    [Fact]
    public async Task A_talking_avatar_step_with_no_avatar_in_the_step_or_the_brief_is_refused_before_anything_is_queued()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        await AddVideoGenerateAsync(db, h, TalkingHead);
        var user = new FakeCurrentUser(h.Project.OwnerId);
        var controller = new WorkflowsController(
            db, user, TestTenancy.Access(db, user), h.Publish.Object,
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            h.ExecutionService, NullLogger<WorkflowsController>.Instance, planGate: h.Gate, costEstimator: h.Estimator, lintGate: WarnOnlyLint(db));

        ActionResult<WorkflowExecutionResponse> result = await controller.Execute(h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        string json = JsonSerializer.Serialize(((ObjectResult)result.Result!).Value);
        json.Should().Contain("AVATAR_NOT_CHOSEN").And.Contain("Avatar clip").And.Contain("Choose an avatar");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_talking_avatar_step_whose_run_names_an_avatar_is_not_refused_by_the_gate()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        await AddVideoGenerateAsync(db, h, TalkingHead);

        (await WarnOnlyLint(db).CheckAsync(h.Project, h.Workflow.Id, null, new RunBrief(Avatar: new RunBriefAvatar(Guid.NewGuid())), null, default))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_video_model_its_provider_does_not_support_is_refused_before_anything_is_queued_and_an_unknown_answer_is_not()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        await AddVideoGenerateAsync(db, h, TalkingHead.Replace(",\"avatarAudio\":{\"from\":\"Step\",\"stepOrder\":2}", ""));
        var facts = new Mock<IWorkflowLintFacts>();
        facts.Setup(f => f.VideoModelSupportedAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        WorkflowLintGate gate = new(
            new WorkflowLinter(new ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit.FixedFactsSource(facts.Object)),
            db, new ConfigurationBuilder().Build(), NullLogger<WorkflowLintGate>.Instance);

        ActionResult? refused = await gate.CheckAsync(h.Project, h.Workflow.Id, null, null, null, default);
        refused.Should().BeOfType<BadRequestObjectResult>();
        string json = JsonSerializer.Serialize(((ObjectResult)refused!).Value);
        json.Should().Contain("VIDEO_MODEL_UNSUPPORTED").And.Contain("minimax/h3-max/lip-sync/image-to-video").And.Contain("Avatar clip");

        facts.Setup(f => f.VideoModelSupportedAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((bool?)null);
        (await gate.CheckAsync(h.Project, h.Workflow.Id, null, null, null, default)).Should().BeNull("an unknown answer is never a finding");
        facts.Setup(f => f.VideoModelSupportedAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        (await gate.CheckAsync(h.Project, h.Workflow.Id, null, null, null, default)).Should().BeNull();
    }

    [Fact]
    public async Task The_run_page_estimate_prices_with_the_briefs_avatar_like_the_gate_and_the_hold()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        Guid avatar = Guid.NewGuid();
        var seen = new List<Guid?>();
        var estimator = new Mock<IWorkflowCostEstimator>();
        estimator.Setup(e => e.PreflightAsync(It.IsAny<Guid?>(), It.IsAny<IReadOnlyList<WorkflowStep>>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                seen.Add(BriefAvatarPricing.Current);
                return Task.FromResult(new WorkflowPreflightResult(WorkflowPreflightOutcome.Allowed, WorkflowCostEstimate.Disabled, 0));
            });
        var user = new FakeCurrentUser(h.Project.OwnerId);
        var controller = new WorkflowsController(
            db, user, TestTenancy.Access(db, user), h.Publish.Object,
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            h.ExecutionService, NullLogger<WorkflowsController>.Instance, planGate: h.Gate, costEstimator: estimator.Object);

        await controller.EstimateWithBrief(h.Project.Id, h.Workflow.Id,
            new EstimateWorkflowRequest(new RunBrief(Avatar: new RunBriefAvatar(avatar))), CancellationToken.None);
        await controller.EstimateWithBrief(h.Project.Id, h.Workflow.Id, new EstimateWorkflowRequest(new RunBrief(Language: "it")), CancellationToken.None);

        seen.Should().Equal(avatar, null);
    }

    [Fact]
    public async Task The_assistant_tool_exposes_brief_as_an_optional_argument()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        AIFunction tool = ToolProvider(db, h).GetTools(new AssistantToolContext(h.Project.OwnerId, h.Project.Id))
            .Single(t => t.Name == "ExecuteWorkflow");

        tool.JsonSchema.GetProperty("properties").TryGetProperty("brief", out _).Should().BeTrue();
        tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().NotContain("brief");
    }

    // --- helpers -------------------------------------------------------------------

    private sealed record Harness(
        WorkflowsController Controller,
        Project Project,
        WorkflowDefinition Workflow,
        Mock<IPublishEndpoint> Publish,
        IWorkflowExecutionService ExecutionService,
        WorkflowCostEstimator Estimator,
        PlanGateService Gate);

    private static InferenceApiDbContext NewContext() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IOrganizationEntitlements Fixed(OrganizationEntitlementSet set)
    {
        Mock<IOrganizationEntitlements> m = new();
        m.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<OrganizationEntitlementSet>(set));
        return m.Object;
    }

    private static WorkflowCostEstimateResponse Value(ActionResult<WorkflowCostEstimateResponse> result)
        => (WorkflowCostEstimateResponse)((OkObjectResult)result.Result!).Value!;

    /// <summary>A project with an Author agent step and a VideoCompile step, a funded account, and the plan's gate.</summary>
    private static async Task<Harness> HarnessAsync(InferenceApiDbContext db, OrganizationEntitlementSet plan, long balanceMillis = 100_000_000)
    {
        Guid owner = Guid.NewGuid();
        Guid org = TestTenancy.OrgOf(owner);
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = owner, OrganizationId = org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "AuthorAgent", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "w",
            ProjectId = project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Steps =
            [
                new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = agent.Id },
                new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoCompile, AgentDefinitionId = agent.Id }
            ]
        };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(workflow);
        db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OrganizationId = org, BalanceMillis = balanceMillis });
        await db.SaveChangesAsync();

        var publish = new Mock<IPublishEndpoint>();
        var gate = new PlanGateService(db, Fixed(plan));
        var execution = new WorkflowExecutionService(db, publish.Object, NullLogger<WorkflowExecutionService>.Instance, gate);
        WorkflowCostEstimator estimator = new(
            new SingleContextScopes(db),
            Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = true }),
            Microsoft.Extensions.Options.Options.Create(new StepCreditEstimateOptions()),
            NullLogger<WorkflowCostEstimator>.Instance);
        var user = new FakeCurrentUser(owner);
        var controller = new WorkflowsController(
            db, user, TestTenancy.Access(db, user), publish.Object,
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            execution, NullLogger<WorkflowsController>.Instance, planGate: gate, costEstimator: estimator);
        return new Harness(controller, project, workflow, publish, execution, estimator, gate);
    }

    private static AssistantToolProvider ToolProvider(InferenceApiDbContext db, Harness h, IPlanGateService? planGate = null, WorkflowLintGate? lintGate = null)
    {
        var user = new FakeCurrentUser(h.Project.OwnerId);
        IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "Assistant:MaxExecutionsPerHour", "10" } })
            .Build();
        return new AssistantToolProvider(
            db,
            new WorkflowEditDiffService(db),
            h.ExecutionService,
            new AssistantExecutionGuard(db, config),
            user,
            new AssistantProjectTools(db, tokens,
                new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
                user, TestTenancy.Access(db, user)),
            new AssistantFileTools(db, tokens, new ReelBolt.WorkflowEngine.Tests.Agents.FakeFileStorage(), Mock.Of<IPublishEndpoint>(), new ReelBolt.WorkflowEngine.Tests.Agents.RecordingSummarizationQueue(), TestTenancy.Access(db, user)),
            new AssistantWorkflowTools(db, tokens, TestTenancy.Access(db, user)),
            new AssistantAdminTools(db, tokens, user, Mock.Of<ISecretProtector>()),
            new AssistantKnowledgeTools(db, new ReelBolt.WorkflowEngine.Tests.Agents.FakePlatformDocsSearch(),
                new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build())),
            new AssistantStyleTools(db, tokens, TestTenancy.Access(db, user)),
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.DesignSystemTools(db, user, tokens),
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.PictureTools(db, user, tokens),
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P0Tools(db, user, tokens).Outputs,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P0Tools(db, user, tokens).Uploads,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P0Tools(db, user, tokens).Runs,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P0Tools(db, user, tokens).Revisions,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P0Tools(db, user, tokens).ChangeRequests,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P2Tools(db, user, tokens).Patch,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P2Tools(db, user, tokens).PictureDocuments,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P2Tools(db, user, tokens).GreenScreen,
            TestTenancy.Access(db, user),
            planGate,
            null,
            new WorkflowExecutionPreflight(db, h.Estimator),
            null, null, null,
            lintGate);
    }

    private static async Task<JsonElement> InvokeToolAsync(
        AssistantToolProvider provider, AssistantToolContext context, string toolName, params (string Key, object? Value)[] arguments)
    {
        AIFunction tool = provider.GetTools(context).Single(t => t.Name == toolName);
        var args = new AIFunctionArguments();
        foreach ((string key, object? value) in arguments)
            args[key] = value;

        object? raw = await tool.InvokeAsync(args);
        string text = raw switch
        {
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => raw!.ToString()!
        };
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = true;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
    }

    private sealed class SingleContextScopes(InferenceApiDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FixedScope(db);

        private sealed class FixedScope(InferenceApiDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new FixedProvider(db);
            public void Dispose()
            {
            }
        }

        private sealed class FixedProvider(InferenceApiDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(InferenceApiDbContext) ? db : null;
        }
    }
}
