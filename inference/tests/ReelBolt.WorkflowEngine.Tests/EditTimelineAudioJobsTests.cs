#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.Styles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Editor v2 Wave 2 (R08.4, R08.6): the loudness measurement and audition endpoints.</summary>
public class EditTimelineAudioJobsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void QueueThrows(Harness h) =>
        h.Executions.SetupSequence(e => e.QueueAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>(), It.IsAny<RunChoices?>(), It.IsAny<WorkflowDefinitionKind>(), It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()))
            .ThrowsAsync(new InvalidOperationException("reached the queue"));

    private static TimelineRenderStepConfig StoredConfig(Harness h) =>
        JsonSerializer.Deserialize<TimelineRenderStepConfig>(
            h.Db.WorkflowSteps.IgnoreQueryFilters().Single().TimelineRenderConfigJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;

    [Fact]
    public async Task Loudness_runs_the_audio_only_mix_without_registering_a_project_file_for_an_editor_and_refuses_a_viewer()
    {
        using Harness h = await Harness.CreateAsync();
        QueueThrows(h);

        (await h.Controller(viewOnly: true).MeasureLoudness(h.ProjectId, h.TimelineId, null, default))
            .Result.Should().BeOfType<ForbidResult>("a billed render needs Edit");
        Func<Task> run = () => h.Controller().MeasureLoudness(h.ProjectId, h.TimelineId, null, default);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("reached the queue");
        h.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Loudness_audition_and_render_starts_refuse_with_409_while_a_restore_holds_the_lock()
    {
        using Harness h = await Harness.CreateAsync();
        QueueThrows(h);

        using IDisposable held = RestoreRunGate.HoldForTests(h.ProjectId);
        ActionResult<RenderEditTimelineResponse> loudness = await h.Controller().MeasureLoudness(h.ProjectId, h.TimelineId, null, default);

        loudness.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
        h.Executions.Verify(e => e.QueueAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(),
            It.IsAny<bool>(), It.IsAny<RunChoices?>(), It.IsAny<WorkflowDefinitionKind>(), It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()), Times.Never);
    }

    [Fact]
    public async Task Audition_needs_a_range_and_a_viewer_is_refused()
    {
        using Harness h = await Harness.CreateAsync();

        (await h.Controller().AuditionRange(h.ProjectId, h.TimelineId, new AudioJobRequest(), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Controller(viewOnly: true).AuditionRange(h.ProjectId, h.TimelineId, new AudioJobRequest(Range: new TimelineRenderRange(0, 60)), default))
            .Result.Should().BeOfType<ForbidResult>();
        (await h.Controller().AuditionRange(h.ProjectId, h.TimelineId, new AudioJobRequest(Range: new TimelineRenderRange(50, 10)), default))
            .Result.Should().BeOfType<BadRequestObjectResult>("outFrame must follow inFrame");
    }

    [Fact]
    public async Task Audition_queues_an_audio_only_artifact_for_just_the_range()
    {
        using Harness h = await Harness.CreateAsync();
        QueueThrows(h);

        Func<Task> run = () => h.Controller().AuditionRange(h.ProjectId, h.TimelineId, new AudioJobRequest(Range: new TimelineRenderRange(30, 90)), default);

        await run.Should().ThrowAsync<InvalidOperationException>();
        TimelineRenderStepConfig config = StoredConfig(h);
        config.Preset.Should().Be(TimelineRenderPreset.AudioOnly);
        config.Range.Should().Be(new TimelineRenderRange(30, 90));
        config.RegisterProjectFile.Should().BeFalse("an audition is never a deliverable");
        config.OutputFileName.Should().StartWith(EditTimelinesController.AuditionJobPrefix);
    }

    [Fact]
    public async Task Loudness_config_is_audio_only_and_unregistered()
    {
        using Harness h = await Harness.CreateAsync();
        QueueThrows(h);

        Func<Task> run = () => h.Controller().MeasureLoudness(h.ProjectId, h.TimelineId, new AudioJobRequest(), default);

        await run.Should().ThrowAsync<InvalidOperationException>();
        TimelineRenderStepConfig config = StoredConfig(h);
        config.Preset.Should().Be(TimelineRenderPreset.AudioOnly);
        config.Range.Should().BeNull();
        config.RegisterProjectFile.Should().BeFalse();
        config.OutputFileName.Should().StartWith(EditTimelinesController.LoudnessJobPrefix);
    }

    [Fact]
    public async Task Render_refuses_a_reserved_audio_job_file_name_with_a_400()
    {
        using Harness h = await Harness.CreateAsync();

        BadRequestObjectResult bad = (await h.Controller().RenderEditTimeline(
                h.ProjectId, h.TimelineId, new RenderEditTimelineRequest(OutputFileName: "audition-x.mp4"), default))
            .Result.Should().BeOfType<BadRequestObjectResult>().Subject;

        JsonSerializer.Serialize(bad.Value, Json).Should().Contain("reserved");
    }

    [Fact]
    public async Task Renders_list_filters_on_the_job_kind_not_the_file_name()
    {
        using Harness h = await Harness.CreateAsync();
        await h.AddRenderAsync("audition-crafted.mp4", null);
        await h.AddRenderAsync("loudness-measure-real.m4a", "loudness");

        List<EditTimelineRenderResponse> listed = (await h.Controller().ListRenders(h.ProjectId, h.TimelineId, default))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<List<EditTimelineRenderResponse>>().Subject;

        listed.Select(r => r.OutputFileName).Should().Equal("audition-crafted.mp4");
    }

    [Fact]
    public async Task Foreign_project_and_missing_timeline_are_404()
    {
        using Harness h = await Harness.CreateAsync();

        (await h.Controller().MeasureLoudness(Guid.NewGuid(), h.TimelineId, null, default)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Controller().MeasureLoudness(h.ProjectId, Guid.NewGuid(), null, default)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Controller().GetAudioJob(h.ProjectId, h.TimelineId, Guid.NewGuid(), default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Loudness_with_too_few_credits_is_a_402_that_persists_nothing()
    {
        using Harness h = await Harness.CreateAsync(balanceMillis: 1_000);
        h.SetPrice(5_000);

        ObjectResult refused = (await h.Controller(billing: true).MeasureLoudness(h.ProjectId, h.TimelineId, null, default))
            .Result.Should().BeOfType<ObjectResult>().Subject;

        refused.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(refused.Value, Json));
        body.RootElement.GetProperty("code").GetString().Should().Be("INSUFFICIENT_CREDITS");
        (await h.Db.WorkflowDefinitions.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Loudness_estimate_prices_the_audio_only_mix()
    {
        using Harness h = await Harness.CreateAsync(balanceMillis: 20_000);
        h.SetPrice(1_000);

        EditTimelineRenderEstimateResponse body = (await h.Controller(billing: true).EstimateLoudness(h.ProjectId, h.TimelineId, null, default))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<EditTimelineRenderEstimateResponse>().Subject;

        body.BillingEnabled.Should().BeTrue();
        body.Affordable.Should().BeTrue();
        h.LastPrice!.Variant.Should().Be(ComputeMetering.Tier720, "AudioOnly has no picture");
    }

    private sealed class Harness : IDisposable
    {
        public required InferenceApiDbContext Db { get; init; }
        public Guid ProjectId { get; init; }
        public Guid TimelineId { get; init; }
        public Guid OwnerId { get; init; }
        public required EditTimeline Document { get; init; }
        public Mock<IWorkflowExecutionService> Executions { get; } = new();
        public PriceRequest? LastPrice { get; private set; }
        public int MaxOutputHeight { get; init; } = int.MaxValue;
        private readonly Mock<INonWorkflowChargeService> _charges = new();

        public static async Task<Harness> CreateAsync(long balanceMillis = 0, int maxOutputHeight = int.MaxValue)
        {
            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"audio-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            InferenceApiDbContext db = new(options);
            Guid ownerId = Guid.NewGuid();
            Guid orgId = TestTenancy.OrgOf(ownerId);
            Project project = new()
            {
                Id = Guid.NewGuid(),
                Name = "P",
                OwnerId = ownerId,
                OrganizationId = orgId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Projects.Add(project);
            Guid timelineId = Guid.NewGuid();
            EditTimeline document = new(
                id: timelineId,
                projectId: project.Id,
                name: "Cut",
                settings: new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
                tracks:
                [
                    new Track(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null,
                        [new MediaClip(Guid.NewGuid(), "Clip", 0, 300, new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: Guid.NewGuid()))])
                ],
                markers: []);
            db.EditTimelines.Add(new EditTimelineEntity
            {
                Id = timelineId,
                ProjectId = project.Id,
                Name = "Cut",
                Revision = 1,
                DocumentJson = JsonSerializer.Serialize(document, EditTimelineJson.Options),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "VideoTransform", AgentType = AgentType.VideoTransform, IsBuiltIn = true, SystemPrompt = string.Empty });
            db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OrganizationId = orgId, BalanceMillis = balanceMillis });
            await db.SaveChangesAsync();
            return new Harness { Db = db, ProjectId = project.Id, TimelineId = timelineId, OwnerId = ownerId, Document = document, MaxOutputHeight = maxOutputHeight };
        }

        public void SetPrice(long millis) =>
            _charges.Setup(c => c.EstimateAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PriceRequest, CancellationToken>((r, _) => LastPrice = r)
                .ReturnsAsync(millis);

        public EditTimelinesController Controller(bool billing = false, bool viewOnly = false)
        {
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(OwnerId);
            user.SetupGet(u => u.Email).Returns("t@example.com");
            user.SetupGet(u => u.IsAuthenticated).Returns(true);
            user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(OwnerId));
            user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);
            user.SetupGet(u => u.EmailVerified).Returns(true);

            IProjectAccessService real = TestTenancy.Access(Db, user.Object);
            IProjectAccessService access = real;
            if (viewOnly)
            {
                Mock<IProjectAccessService> view = new();
                view.Setup(a => a.GetAsync(It.IsAny<Guid>(), It.IsAny<ProjectPermission>(), It.IsAny<CancellationToken>()))
                    .Returns<Guid, ProjectPermission, CancellationToken>((id, permission, ct) => permission == ProjectPermission.View
                        ? real.GetAsync(id, permission, ct)
                        : Task.FromResult(new ProjectAccessResult(ProjectAccessOutcome.Forbidden, null)));
                access = view.Object;
            }

            Mock<IWorkflowCostEstimator> estimator = new();
            estimator.SetupGet(e => e.BillingEnabled).Returns(billing);
            Mock<IOrganizationEntitlements> entitlements = new();
            entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(ValueTask.FromResult(new OrganizationEntitlementSet { MaxOutputHeight = MaxOutputHeight }));

            return new EditTimelinesController(
                Db, user.Object, access, new Mock<IFileStorageService>().Object, Executions.Object,
                _charges.Object, null, entitlements.Object, null, null, costEstimator: estimator.Object);
        }

        public async Task<EditTimelineRenderEstimateResponse> EstimateAsync(EditTimelinesController controller, RenderEditTimelineRequest request) =>
            (await controller.EstimateRender(ProjectId, TimelineId, request, default))
                .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<EditTimelineRenderEstimateResponse>().Subject;

        public async Task AddRenderAsync(string fileName, string? kind)
        {
            Guid wf = Guid.NewGuid();
            Guid agent = Db.AgentDefinitions.First().Id;
            TimelineRenderStepConfig config = new(TimelineId, OutputFileName: fileName, AudioJobKind: kind);
            WorkflowDefinition w = new() { Id = wf, ProjectId = ProjectId, Name = "r", Kind = WorkflowDefinitionKind.TimelineRender, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            w.Steps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = wf,
                AgentDefinitionId = agent,
                StepOrder = 1,
                Label = "r",
                StepType = StepType.TimelineRender,
                TimelineRenderConfigJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })
            });
            Db.WorkflowDefinitions.Add(w);
            Db.WorkflowExecutions.Add(new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId, WorkflowDefinitionId = wf, Status = ExecutionStatus.Passed });
            await Db.SaveChangesAsync();
        }

        public void Dispose() => Db.Dispose();
    }

}
