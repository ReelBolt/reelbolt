#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>GET …/edit-timelines/{id}/renders</c> (editor v2, P4): the Deliver workspace's queue. Render
/// workflows are hidden from the workflow list, so this endpoint is the only way the editor sees
/// them; what it must get right is the match (this timeline's renders, nobody else's), the shape
/// per execution state, and the tolerance for output JSON it cannot read.
/// </summary>
public class EditTimelineRendersControllerTests
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Lists_only_this_timelines_renders_newest_first_with_the_finished_ones_carrying_regions()
    {
        string dbName = $"renders-{Guid.NewGuid()}";
        Guid projectId, timelineId, ownerId, finishedExecutionId, queuedExecutionId, stepResultId;
        using (InferenceApiDbContext db = CreateContext(dbName))
        {
            Project project = await AddProjectAsync(db);
            projectId = project.Id;
            ownerId = project.OwnerId;
            timelineId = (await AddTimelineAsync(db, projectId, "Cut")).Id;
            Guid otherTimelineId = (await AddTimelineAsync(db, projectId, "Other")).Id;
            AgentDefinition placeholder = await AddPlaceholderAsync(db);

            // A finished render of THIS timeline, pinned at revision 3, with two regions (one cached).
            (WorkflowDefinition finished, WorkflowStep finishedStep) = AddRenderWorkflow(db, projectId, placeholder, timelineId, revision: 3, TimelineRenderPreset.Vertical9x16, "reel.mp4");
            WorkflowExecution finishedExecution = AddExecution(db, project, finished, ExecutionStatus.Passed, startedAt: DateTime.UtcNow.AddMinutes(-10), completedAt: DateTime.UtcNow.AddMinutes(-8));
            finishedExecutionId = finishedExecution.Id;
            WorkflowStepResult result = new()
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = finishedExecution.Id,
                WorkflowStepId = finishedStep.Id,
                Status = StepStatus.Completed,
                OutputStorageKey = $"projects/{projectId}/outputFiles/reel.mp4",
                OutputJson = """{"status":"completed","revision":3,"regions":{"total":2,"cached":1,"list":[{"startFrame":0,"endFrame":30,"cached":true},{"startFrame":30,"endFrame":60,"cached":false}]}}""",
                ExecutedAt = DateTime.UtcNow.AddMinutes(-8),
                CompletedAt = DateTime.UtcNow.AddMinutes(-8)
            };
            stepResultId = result.Id;
            db.WorkflowStepResults.Add(result);

            // A render still queued (no step result yet) — newest, listed first, with no output.
            (WorkflowDefinition queued, _) = AddRenderWorkflow(db, projectId, placeholder, timelineId, revision: null, TimelineRenderPreset.Hd1080p16x9, "cut.mp4");
            queuedExecutionId = AddExecution(db, project, queued, ExecutionStatus.Queued, startedAt: null, completedAt: null).Id;

            // Another timeline's render: must not appear.
            (WorkflowDefinition foreign, _) = AddRenderWorkflow(db, projectId, placeholder, otherTimelineId, revision: 1, TimelineRenderPreset.Square1x1, "other.mp4");
            AddExecution(db, project, foreign, ExecutionStatus.Passed, DateTime.UtcNow, DateTime.UtcNow);

            await db.SaveChangesAsync();
        }

        using (InferenceApiDbContext db = CreateContext(dbName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);
            ActionResult<List<EditTimelineRenderResponse>> response = await controller.ListRenders(projectId, timelineId, CancellationToken.None);

            List<EditTimelineRenderResponse> renders = response.Result.Should().BeOfType<OkObjectResult>().Subject
                .Value.Should().BeOfType<List<EditTimelineRenderResponse>>().Subject;

            renders.Should().HaveCount(2, "the other timeline's render is not this timeline's");
            renders.Select(r => r.ExecutionId).Should().Equal(queuedExecutionId, finishedExecutionId);
            renders.Should().OnlyContain(r => r.TimelineId == timelineId);

            EditTimelineRenderResponse queuedRender = renders[0];
            queuedRender.Status.Should().Be("Queued");
            queuedRender.Preset.Should().Be("Hd1080p16x9");
            queuedRender.OutputFileName.Should().Be("cut.mp4");
            queuedRender.StepResultId.Should().BeNull();
            queuedRender.HasOutput.Should().BeFalse();
            queuedRender.Regions.Should().BeNull();
            queuedRender.Revision.Should().BeNull("a latest-revision render is pinned only once the step runs");

            EditTimelineRenderResponse finishedRender = renders[1];
            finishedRender.Status.Should().Be("Passed");
            finishedRender.Preset.Should().Be("Vertical9x16");
            finishedRender.Revision.Should().Be(3);
            finishedRender.StepResultId.Should().Be(stepResultId);
            finishedRender.HasOutput.Should().BeTrue();
            finishedRender.Regions.Should().NotBeNull();
            finishedRender.Regions!.Total.Should().Be(2);
            finishedRender.Regions.Cached.Should().Be(1);
            finishedRender.Regions.List.Should().HaveCount(2);
            finishedRender.Regions.List[0].Should().Be(new EditTimelineRenderRegionResponse(0, 30, true));
            finishedRender.Regions.List[1].Should().Be(new EditTimelineRenderRegionResponse(30, 60, false));
        }
    }

    [Fact]
    public async Task A_timeline_in_another_organization_is_404_and_a_timeline_with_no_renders_is_an_empty_list()
    {
        string dbName = $"renders-{Guid.NewGuid()}";
        Guid projectId, timelineId, ownerId, foreignProjectId, foreignTimelineId;
        using (InferenceApiDbContext db = CreateContext(dbName))
        {
            Project project = await AddProjectAsync(db);
            projectId = project.Id;
            ownerId = project.OwnerId;
            timelineId = (await AddTimelineAsync(db, projectId, "Quiet")).Id;

            Project foreign = await AddProjectAsync(db);
            foreignProjectId = foreign.Id;
            foreignTimelineId = (await AddTimelineAsync(db, foreignProjectId, "Theirs")).Id;
            await db.SaveChangesAsync();
        }

        using (InferenceApiDbContext db = CreateContext(dbName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            ActionResult<List<EditTimelineRenderResponse>> empty = await controller.ListRenders(projectId, timelineId, CancellationToken.None);
            empty.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<List<EditTimelineRenderResponse>>().Subject.Should().BeEmpty();

            ActionResult<List<EditTimelineRenderResponse>> foreignResult = await controller.ListRenders(foreignProjectId, foreignTimelineId, CancellationToken.None);
            foreignResult.Result.Should().BeOfType<NotFoundResult>("D5: another organization's project does not exist to this caller");

            ActionResult<List<EditTimelineRenderResponse>> missing = await controller.ListRenders(projectId, Guid.NewGuid(), CancellationToken.None);
            missing.Result.Should().BeOfType<NotFoundResult>();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"revision":"three","regions":"nope"}""")]
    [InlineData("""{"regions":{"list":[{"startFrame":"a"},{"startFrame":1,"endFrame":2}]}}""")]
    public void Unreadable_or_partial_render_output_degrades_to_nulls_rather_than_failing_the_listing(string? outputJson)
    {
        (int? revision, EditTimelineRenderRegionsResponse? regions) = EditTimelinesController.ReadRenderOutput(outputJson);
        revision.Should().BeNull();
        if (regions is not null)
        {
            // The one partially-readable shape: a list where only the well-formed region survives.
            regions.List.Should().OnlyContain(r => r.EndFrame > r.StartFrame);
        }
    }

    [Fact]
    public async Task A_finished_render_lists_what_it_skipped_and_its_warnings_without_reason_codes()
    {
        string dbName = $"renders-{Guid.NewGuid()}";
        Guid projectId, timelineId, ownerId, skippedItemId = Guid.NewGuid();
        using (InferenceApiDbContext db = CreateContext(dbName))
        {
            Project project = await AddProjectAsync(db);
            projectId = project.Id;
            ownerId = project.OwnerId;
            timelineId = (await AddTimelineAsync(db, projectId, "Cut")).Id;
            AgentDefinition placeholder = await AddPlaceholderAsync(db);
            (WorkflowDefinition wf, WorkflowStep step) = AddRenderWorkflow(db, projectId, placeholder, timelineId, revision: 2, TimelineRenderPreset.Vertical9x16, "reel.mp4");
            WorkflowExecution execution = AddExecution(db, project, wf, ExecutionStatus.Passed, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow);
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execution.Id,
                WorkflowStepId = step.Id,
                Status = StepStatus.Completed,
                OutputStorageKey = "projects/x/outputFiles/reel.mp4",
                OutputJson = $$"""{"revision":2,"skippedItems":[{"itemId":"{{skippedItemId}}","type":"text","reason":"unsupported-type","message":"A text title was left out of the render."}],"warnings":["A text title was left out of the render."]}""",
                ExecutedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using (InferenceApiDbContext db = CreateContext(dbName))
        {
            ActionResult<List<EditTimelineRenderResponse>> response = await CreateController(db, ownerId).ListRenders(projectId, timelineId, CancellationToken.None);
            EditTimelineRenderResponse render = response.Result.Should().BeOfType<OkObjectResult>().Subject
                .Value.Should().BeOfType<List<EditTimelineRenderResponse>>().Subject.Single();

            render.SkippedItems.Should().Equal(new EditTimelineRenderSkippedItemResponse(skippedItemId, "text", "A text title was left out of the render."));
            render.Warnings.Should().Equal("A text title was left out of the render.");
            JsonSerializer.Serialize(render, ConfigJson).Should().NotContain("unsupported-type", "reason codes are not plain copy");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"revision":2}""")]
    [InlineData("""{"skippedItems":"nope","warnings":{"a":1}}""")]
    [InlineData("""{"skippedItems":[1,"x",null,{"type":5}],"warnings":[1,null,{"a":1},"   "]}""")]
    public void Absent_or_malformed_skips_and_warnings_degrade_to_empty_lists(string? outputJson)
    {
        (IReadOnlyList<EditTimelineRenderSkippedItemResponse> skipped, IReadOnlyList<string> warnings) = EditTimelinesController.ReadRenderDiagnostics(outputJson);
        skipped.Should().BeEmpty();
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Skips_and_warnings_are_bounded_in_count_and_message_length()
    {
        string longText = new('a', 1000);
        string items = string.Join(",", Enumerable.Range(0, 120).Select(_ => $$"""{"itemId":"{{Guid.NewGuid()}}","type":"video","reason":"r","message":"{{longText}}"}"""));
        string warns = string.Join(",", Enumerable.Range(0, 120).Select(_ => $"\"{longText}\""));
        string json = $$"""{"skippedItems":[{{items}},{"itemId":"not-a-guid","message":"Kept without an id."}],"warnings":[{{warns}}]}""";

        (IReadOnlyList<EditTimelineRenderSkippedItemResponse> skipped, IReadOnlyList<string> warnings) = EditTimelinesController.ReadRenderDiagnostics(json);

        skipped.Should().HaveCount(EditTimelinesController.MaxRenderDiagnostics);
        skipped.Should().OnlyContain(s => s.Message.Length <= EditTimelinesController.MaxRenderDiagnosticLength);
        warnings.Should().HaveCount(EditTimelinesController.MaxRenderDiagnostics);
        warnings.Should().OnlyContain(w => w.Length <= EditTimelinesController.MaxRenderDiagnosticLength);

        (IReadOnlyList<EditTimelineRenderSkippedItemResponse> loose, _) = EditTimelinesController.ReadRenderDiagnostics(
            """{"skippedItems":[{"itemId":"not-a-guid","message":"Kept without an id."}]}""");
        loose.Should().Equal(new EditTimelineRenderSkippedItemResponse(null, null, "Kept without an id."));
    }

    // ── fixtures ────────────────────────────────────────────────────────────

    [Fact]
    public async Task R09_7_a_finished_render_lists_its_caption_files_as_project_files_without_storage_keys()
    {
        string dbName = $"renders-cap-{Guid.NewGuid()}";
        Guid projectId, timelineId, ownerId, srtId;
        using (InferenceApiDbContext db = CreateContext(dbName))
        {
            Project project = await AddProjectAsync(db);
            projectId = project.Id;
            ownerId = project.OwnerId;
            timelineId = (await AddTimelineAsync(db, projectId, "Cut")).Id;
            AgentDefinition placeholder = await AddPlaceholderAsync(db);
            (WorkflowDefinition wf, WorkflowStep step) = AddRenderWorkflow(db, projectId, placeholder, timelineId, 1, TimelineRenderPreset.Hd1080p16x9, "reel.mp4");
            WorkflowExecution execution = AddExecution(db, project, wf, ExecutionStatus.Passed, DateTime.UtcNow, DateTime.UtcNow);
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execution.Id,
                WorkflowStepId = step.Id,
                Status = StepStatus.Completed,
                OutputStorageKey = "k/reel.mp4",
                OutputJson = """{"revision":1,"captionSidecars":{"srtStorageKey":"k/reel.srt","vttStorageKey":"k/reel.vtt","cueCount":2}}""",
                ExecutedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            });
            srtId = Guid.NewGuid();
            db.ProjectFiles.Add(new ProjectFile { Id = srtId, ProjectId = projectId, OriginalFileName = "reel.srt", StorageKey = "k/reel.srt", StorageBucket = "b", MimeType = "application/x-subrip", SizeBytes = 42, UploadedAt = DateTime.UtcNow });
            db.ProjectFiles.Add(new ProjectFile { Id = Guid.NewGuid(), ProjectId = projectId, OriginalFileName = "reel.vtt", StorageKey = "k/reel.vtt", StorageBucket = "b", MimeType = "text/vtt", SizeBytes = 50, UploadedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        using InferenceApiDbContext read = CreateContext(dbName);
        ActionResult<List<EditTimelineRenderResponse>> response = await CreateController(read, ownerId).ListRenders(projectId, timelineId, CancellationToken.None);
        EditTimelineRenderResponse render = response.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<List<EditTimelineRenderResponse>>().Subject.Single();
        render.CaptionFiles.Should().Equal(
            new EditTimelineCaptionFileResponse("srt", "reel.srt", 42, srtId),
            new EditTimelineCaptionFileResponse("vtt", "reel.vtt", 50, render.CaptionFiles![1].FileId));
        JsonSerializer.Serialize(render).Should().NotContain("k/reel", "no storage key reaches the client");
    }

    [Fact]
    public void R09_7_the_render_request_binds_captions_by_name_and_rejects_integers()
    {
        JsonSerializerOptions web = new(JsonSerializerDefaults.Web);
        JsonSerializer.Deserialize<RenderEditTimelineRequest>("""{"captions":"burnIn"}""", web)!.Captions.Should().Be(TimelineRenderCaptions.BurnIn);
        JsonSerializer.Deserialize<RenderEditTimelineRequest>("""{"captions":"sidecar"}""", web)!.Captions.Should().Be(TimelineRenderCaptions.Sidecar);
        JsonSerializer.Deserialize<RenderEditTimelineRequest>("""{}""", web)!.Captions.Should().BeNull();
        Action integer = () => JsonSerializer.Deserialize<RenderEditTimelineRequest>("""{"captions":1}""", web);
        integer.Should().Throw<JsonException>();
        TimelineRenderStepConfig stored = JsonSerializer.Deserialize<TimelineRenderStepConfig>(
            JsonSerializer.Serialize(new TimelineRenderStepConfig(Guid.NewGuid(), Captions: TimelineRenderCaptions.Both), ConfigJson), ConfigJson)!;
        stored.Captions.Should().Be(TimelineRenderCaptions.Both);
    }

    private static DbContextOptions<InferenceApiDbContext> CreateOptions(string databaseName) =>
        new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static InferenceApiDbContext CreateContext(string databaseName) => new(CreateOptions(databaseName));

    private static EditTimelinesController CreateController(InferenceApiDbContext db, Guid userId)
    {
        Mock<ICurrentUser> currentUser = new();
        currentUser.SetupGet(u => u.UserId).Returns(userId);
        currentUser.SetupGet(u => u.Email).Returns("test@example.com");
        currentUser.SetupGet(u => u.IsAuthenticated).Returns(true);
        currentUser.SetupGet(u => u.IsPlatformAdmin).Returns(false);
        currentUser.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(userId));
        currentUser.SetupGet(u => u.OrgRole).Returns(ReelBolt.Shared.Tenancy.OrganizationRole.Member);
        return new EditTimelinesController(
            db,
            currentUser.Object,
            TestTenancy.Access(db, currentUser.Object),
            new Mock<IFileStorageService>().Object,
            new Mock<IWorkflowExecutionService>().Object);
    }

    private static async Task<Project> AddProjectAsync(InferenceApiDbContext db)
    {
        Guid ownerId = Guid.NewGuid();
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project;
    }

    private static async Task<EditTimelineEntity> AddTimelineAsync(InferenceApiDbContext db, Guid projectId, string name)
    {
        EditTimelineEntity timeline = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = name,
            DocumentJson = "{}",
            Revision = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.EditTimelines.Add(timeline);
        await db.SaveChangesAsync();
        return timeline;
    }

    private static async Task<AgentDefinition> AddPlaceholderAsync(InferenceApiDbContext db)
    {
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "VideoTransform",
            AgentType = AgentType.VideoTransform,
            IsBuiltIn = true,
            SystemPrompt = string.Empty
        };
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    private static (WorkflowDefinition Workflow, WorkflowStep Step) AddRenderWorkflow(
        InferenceApiDbContext db, Guid projectId, AgentDefinition placeholder, Guid timelineId, int? revision, TimelineRenderPreset preset, string fileName)
    {
        TimelineRenderStepConfig config = new(EditTimelineId: timelineId, Revision: revision, Preset: preset, OutputFileName: fileName);
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = "Render",
            Kind = WorkflowDefinitionKind.TimelineRender
        };
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = placeholder.Id,
            StepOrder = 1,
            StepType = StepType.TimelineRender,
            TimelineRenderConfigJson = JsonSerializer.Serialize(config, ConfigJson)
        };
        workflow.Steps.Add(step);
        db.WorkflowDefinitions.Add(workflow);
        return (workflow, step);
    }

    private static WorkflowExecution AddExecution(
        InferenceApiDbContext db, Project project, WorkflowDefinition workflow, ExecutionStatus status, DateTime? startedAt, DateTime? completedAt)
    {
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };
        db.WorkflowExecutions.Add(execution);
        return execution;
    }

    [Theory]
    [InlineData(TimelineRenderRateControl.Quality, 4000, "encode.videoBitrateKbps can only be set when rateControl is Capped or Average.")]
    [InlineData(TimelineRenderRateControl.Capped, null, "encode.videoBitrateKbps is required when rateControl is Capped.")]
    [InlineData(TimelineRenderRateControl.Average, 150, "encode.videoBitrateKbps must be between 200 and 100000, found 150.")]
    [InlineData(TimelineRenderRateControl.Average, 100001, "encode.videoBitrateKbps must be between 200 and 100000, found 100001.")]
    public async Task A_bad_rate_control_combination_is_a_400_on_the_estimate_route(
        TimelineRenderRateControl rateControl, int? kbps, string expected)
    {
        string dbName = $"rate-{Guid.NewGuid()}";
        using InferenceApiDbContext db = CreateContext(dbName);
        Project project = await AddProjectAsync(db);
        EditTimelineEntity timeline = await AddTimelineAsync(db, project.Id, "Cut");
        EditTimelinesController controller = CreateController(db, project.OwnerId);
        RenderEditTimelineRequest request = new(Encode: new TimelineRenderEncodeSettings(RateControl: rateControl, VideoBitrateKbps: kbps));

        ActionResult<EditTimelineRenderEstimateResponse> estimate = await controller.EstimateRender(project.Id, timeline.Id, request, CancellationToken.None);

        BadRequestObjectResult bad = estimate.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        System.Text.Json.JsonSerializer.Serialize(bad.Value).Should().Contain(expected);
    }
}
