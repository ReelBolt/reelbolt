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

/// <summary>
/// Editor v2 Wave 1 (R02.5, R06.1, R06.7): <c>canEdit</c> on the document GET, the render summary's
/// facts, <c>render/estimate</c>, and the credit 402 of <c>render</c> (with billing on and off).
/// </summary>
public class EditTimelineRenderEstimateTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── R02.5 canEdit ────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_reports_canEdit_true_for_the_owner_and_false_for_a_caller_who_may_only_view()
    {
        using Harness h = await Harness.CreateAsync();

        EditTimelineResponse owner = (await h.Controller().GetEditTimeline(h.ProjectId, h.TimelineId, default))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<EditTimelineResponse>().Subject;
        owner.CanEdit.Should().BeTrue();

        EditTimelineResponse viewer = (await h.Controller(viewOnly: true).GetEditTimeline(h.ProjectId, h.TimelineId, default))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<EditTimelineResponse>().Subject;
        viewer.CanEdit.Should().BeFalse("the document still opens for a viewer, read-only");
        viewer.Document.Should().NotBeNull();
    }

    [Fact]
    public async Task Patch_by_a_caller_who_may_only_view_is_still_403()
    {
        using Harness h = await Harness.CreateAsync();

        ActionResult<EditTimelineResponse> patched = await h.Controller(viewOnly: true).PatchEditTimeline(
            h.ProjectId, h.TimelineId, new PatchEditTimelineRequest(1, null, h.Document), default);

        patched.Result.Should().BeOfType<ForbidResult>("PATCH still demands Edit");
    }

    // ── R06.7 estimate ───────────────────────────────────────────────────────

    [Fact]
    public async Task Estimate_with_billing_off_says_so_and_is_affordable()
    {
        using Harness h = await Harness.CreateAsync();

        EditTimelineRenderEstimateResponse body = await h.EstimateAsync(h.Controller(), new RenderEditTimelineRequest());

        body.Should().BeEquivalentTo(
            new EditTimelineRenderEstimateResponse(false, null, null, null, null, null, true, null),
            o => o.Excluding(r => r.EstimatedSizeBytes).Excluding(r => r.SizeIsApproximate));
        body.EstimatedSizeBytes.Should().BeGreaterThan(0, "the size estimate does not depend on billing");
    }

    [Fact]
    public async Task Estimate_with_billing_on_prices_the_resolution_tier_and_length_and_reads_the_balance()
    {
        using Harness h = await Harness.CreateAsync(balanceMillis: 20_000);
        h.SetPrice(7_500);

        // 10 s timeline (300 frames at 30 fps), 1080p preset.
        EditTimelineRenderEstimateResponse body = await h.EstimateAsync(
            h.Controller(billing: true), new RenderEditTimelineRequest(Preset: TimelineRenderPreset.Hd1080p16x9));

        body.BillingEnabled.Should().BeTrue();
        body.Credits.Should().Be(7.5);
        body.CreditsLow.Should().Be(7.5);
        body.CreditsHigh.Should().Be(7.5);
        body.BalanceCredits.Should().Be(20);
        body.Affordable.Should().BeTrue();
        body.EstimateSeconds.Should().Be(20, "10 s start-up + 10 s at 1x for 1080p, in steps of 5");
        h.LastPrice.Should().NotBeNull();
        h.LastPrice!.Variant.Should().Be(ComputeMetering.Tier1080);
        h.LastPrice.Quantity.Should().Be(10m);
        h.LastPrice.Unit.Should().Be(UsageKind.EncodeOutputSeconds);
    }

    [Fact]
    public async Task Estimate_prices_a_range_at_the_requested_preset_tier()
    {
        using Harness h = await Harness.CreateAsync(balanceMillis: 1_000);
        h.SetPrice(4_000);

        EditTimelineRenderEstimateResponse body = await h.EstimateAsync(
            h.Controller(billing: true),
            new RenderEditTimelineRequest(Range: new TimelineRenderRange(0, 90), Preset: TimelineRenderPreset.Vertical9x16));

        h.LastPrice!.Variant.Should().Be(ComputeMetering.Tier1080, "a 1080x1920 reel is 1080p by its short side");
        h.LastPrice.Quantity.Should().Be(3m);
        body.Affordable.Should().BeFalse("the balance of 1 credit does not cover 4");
        body.EstimateSeconds.Should().Be(15);
    }

    [Fact]
    public async Task Estimate_refuses_an_invalid_request_with_the_same_400_as_render()
    {
        using Harness h = await Harness.CreateAsync();

        ActionResult<EditTimelineRenderEstimateResponse> result = await h.Controller(billing: true).EstimateRender(
            h.ProjectId, h.TimelineId, new RenderEditTimelineRequest(Range: new TimelineRenderRange(50, 10)), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Estimate_for_a_size_the_plan_refuses_is_not_affordable_and_says_why()
    {
        using Harness h = await Harness.CreateAsync(balanceMillis: 100_000, maxOutputHeight: 1080);
        h.SetPrice(1_000);

        EditTimelineRenderEstimateResponse body = await h.EstimateAsync(
            h.Controller(billing: true), new RenderEditTimelineRequest(Preset: TimelineRenderPreset.Uhd4k16x9));

        body.Affordable.Should().BeFalse();
        body.UnavailableReason.Should().Contain("Your plan renders up to");
    }

    // ── R06.7 render 402 ─────────────────────────────────────────────────────

    [Fact]
    public async Task Render_with_too_few_credits_is_a_402_that_persists_nothing()
    {
        using Harness h = await Harness.CreateAsync(balanceMillis: 2_000);
        h.SetPrice(7_500);

        ActionResult<RenderEditTimelineResponse> result = await h.Controller(billing: true).RenderEditTimeline(
            h.ProjectId, h.TimelineId, new RenderEditTimelineRequest(), default);

        ObjectResult refused = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(refused.Value, Json));
        body.RootElement.GetProperty("code").GetString().Should().Be("INSUFFICIENT_CREDITS");
        body.RootElement.GetProperty("message").GetString().Should().Contain("credits");
        body.RootElement.GetProperty("missing").GetDouble().Should().Be(5.5, "7.5 needed, 2 on hand");

        h.Executions.Verify(
            e => e.QueueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>(), It.IsAny<RunChoices?>(), It.IsAny<WorkflowDefinitionKind>(), It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()),
            Times.Never);
        (await h.Db.WorkflowDefinitions.IgnoreQueryFilters().CountAsync()).Should().Be(0, "nothing persists before the credit check");
    }

    [Fact]
    public async Task Render_checks_the_plan_before_the_credits()
    {
        using Harness h = await Harness.CreateAsync(balanceMillis: 0, maxOutputHeight: 1080);
        h.SetPrice(9_000);

        ActionResult<RenderEditTimelineResponse> result = await h.Controller(billing: true).RenderEditTimeline(
            h.ProjectId, h.TimelineId, new RenderEditTimelineRequest(Preset: TimelineRenderPreset.Uhd4k16x9), default);

        ObjectResult refused = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(refused.Value, Json));
        body.RootElement.GetProperty("code").GetString().Should().Be("PLAN_NOT_ENTITLED");
    }

    [Fact]
    public async Task Render_with_billing_off_is_not_refused_for_credits()
    {
        using Harness h = await Harness.CreateAsync(balanceMillis: 0);
        h.SetPrice(9_000);

        // Billing off: the credit gate is skipped, so the request reaches the queue. The queue
        // stub throws a sentinel so the test can tell "got as far as queueing" from a 402.
        h.Executions.SetupSequence(e => e.QueueAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>(), It.IsAny<RunChoices?>(), It.IsAny<WorkflowDefinitionKind>(), It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()))
            .ThrowsAsync(new InvalidOperationException("reached the queue"));

        Func<Task> render = () => h.Controller(billing: false).RenderEditTimeline(h.ProjectId, h.TimelineId, new RenderEditTimelineRequest(), default);

        await render.Should().ThrowAsync<InvalidOperationException>().WithMessage("reached the queue");
    }

    [Theory]
    [InlineData(ComputeMetering.Tier720, 10, 15)]
    [InlineData(ComputeMetering.Tier1080, 10, 20)]
    [InlineData(ComputeMetering.TierUhd, 10, 40)]
    [InlineData(ComputeMetering.Tier1080, 0, 10)]
    public void The_time_estimate_is_start_up_plus_a_tier_factor_in_steps_of_five(string tier, double seconds, double expected) =>
        EditTimelinesController.EstimateRenderSeconds(seconds, tier).Should().Be(expected);

    // ── R06.1 render facts ───────────────────────────────────────────────────

    [Fact]
    public async Task A_finished_render_lists_its_length_size_registered_file_and_date_and_nulls_when_unknown()
    {
        using Harness h = await Harness.CreateAsync();
        AgentDefinition placeholder = await h.Db.AgentDefinitions.FirstAsync();

        ProjectFile file = new() { Id = Guid.NewGuid(), ProjectId = h.ProjectId, StorageKey = "projects/p/outputFiles/cut.mp4", SizeBytes = 4_321, OriginalFileName = "cut.mp4" };
        h.Db.ProjectFiles.Add(file);

        (WorkflowDefinition registered, WorkflowStep registeredStep) = AddRender(h, placeholder, "cut.mp4");
        (WorkflowDefinition bare, WorkflowStep bareStep) = AddRender(h, placeholder, "bare.mp4");
        DateTime queued = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        WorkflowExecution done = AddExecution(h, registered, queued);
        WorkflowExecution unknown = AddExecution(h, bare, queued.AddMinutes(-5));
        h.Db.WorkflowStepResults.Add(StepResult(done, registeredStep, file.StorageKey, """{"revision":1,"durationSec":12.5}"""));
        h.Db.WorkflowStepResults.Add(StepResult(unknown, bareStep, "projects/p/artifacts/x.mp4", """{"revision":1}"""));
        await h.Db.SaveChangesAsync();

        List<EditTimelineRenderResponse> renders = (await h.Controller().ListRenders(h.ProjectId, h.TimelineId, default))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<List<EditTimelineRenderResponse>>().Subject;

        EditTimelineRenderResponse withFile = renders.Single(r => r.OutputFileName == "cut.mp4");
        withFile.DurationSec.Should().Be(12.5);
        withFile.SizeBytes.Should().Be(4_321);
        withFile.OutputFileId.Should().Be(file.Id);
        withFile.CreatedAt.Should().Be(queued);

        EditTimelineRenderResponse without = renders.Single(r => r.OutputFileName == "bare.mp4");
        without.DurationSec.Should().BeNull();
        without.SizeBytes.Should().BeNull();
        without.OutputFileId.Should().BeNull();

        string wire = JsonSerializer.Serialize(withFile, Json);
        wire.Should().NotContain("projects/p/", "no storage key reaches a response");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static (WorkflowDefinition, WorkflowStep) AddRender(Harness h, AgentDefinition placeholder, string fileName)
    {
        TimelineRenderStepConfig config = new(EditTimelineId: h.TimelineId, Revision: 1, OutputFileName: fileName);
        WorkflowDefinition wf = new() { Id = Guid.NewGuid(), ProjectId = h.ProjectId, Name = "Render", Kind = WorkflowDefinitionKind.TimelineRender };
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = wf.Id,
            AgentDefinitionId = placeholder.Id,
            StepOrder = 1,
            StepType = StepType.TimelineRender,
            TimelineRenderConfigJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })
        };
        wf.Steps.Add(step);
        h.Db.WorkflowDefinitions.Add(wf);
        return (wf, step);
    }

    private static WorkflowExecution AddExecution(Harness h, WorkflowDefinition wf, DateTime queuedAt)
    {
        WorkflowExecution e = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = h.ProjectId,
            WorkflowDefinitionId = wf.Id,
            Status = ExecutionStatus.Passed,
            QueuedAt = queuedAt,
            StartedAt = queuedAt.AddSeconds(2),
            CompletedAt = queuedAt.AddMinutes(1)
        };
        h.Db.WorkflowExecutions.Add(e);
        return e;
    }

    private static WorkflowStepResult StepResult(WorkflowExecution e, WorkflowStep step, string outputKey, string outputJson) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowExecutionId = e.Id,
        WorkflowStepId = step.Id,
        Status = StepStatus.Completed,
        OutputStorageKey = outputKey,
        OutputJson = outputJson,
        ExecutedAt = DateTime.UtcNow,
        CompletedAt = DateTime.UtcNow
    };

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
                .UseInMemoryDatabase($"estimate-{Guid.NewGuid()}")
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

        public void Dispose() => Db.Dispose();
    }
}
