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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The editor's render endpoint refuses only an EXPLICIT size above the plan (Review Focus 4):
/// <c>Uhd4k16x9</c>, or an <c>encode</c> width/height whose short side passes the plan's
/// <c>MaxOutputHeight</c>, gets the 402 <c>PLAN_NOT_ENTITLED</c> body before anything is written.
/// Shape presets and Master are accepted (the engine clamps them); unlimited plans and a host
/// without an entitlement service are never refused.
/// </summary>
public class EditTimelineRenderPlanGateTests
{
    private static OrganizationEntitlementSet Trial => new() { Tier = "trial", MaxOutputHeight = 720 };
    private static OrganizationEntitlementSet Creator => new() { Tier = "creator", MaxOutputHeight = 1080 };

    [Fact]
    public async Task Trial_Uhd4k_is_refused_with_the_402_body_and_nothing_is_written()
    {
        Harness h = await Harness.CreateAsync(Trial);

        ActionResult<RenderEditTimelineResponse> result = await h.RenderAsync(new RenderEditTimelineRequest(Preset: TimelineRenderPreset.Uhd4k16x9));

        ObjectResult refused = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(refused.Value));
        body.RootElement.GetProperty("code").GetString().Should().Be(PlanGate.NotEntitledCode);
        body.RootElement.GetProperty("feature").GetString().Should().Be(PlanGate.ResolutionFeature);
        body.RootElement.GetProperty("limit").GetInt64().Should().Be(720);
        body.RootElement.GetProperty("current").GetInt64().Should().Be(2160);
        body.RootElement.GetProperty("message").GetString().Should().Contain("720p").And.Contain("4K (2160p)");

        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        h.Executions.Verify(e => e.QueueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(),
            It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>(),
            It.IsAny<WorkflowDefinitionKind>(), It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()), Times.Never);
    }

    [Theory]
    [InlineData(TimelineRenderPreset.Hd1080p16x9)]
    [InlineData(TimelineRenderPreset.Vertical9x16)]
    [InlineData(TimelineRenderPreset.Square1x1)]
    [InlineData(TimelineRenderPreset.Portrait4x5)]
    [InlineData(TimelineRenderPreset.Master)]
    [InlineData(TimelineRenderPreset.AudioOnly)]
    public async Task Trial_shape_presets_are_accepted_for_the_engine_to_clamp(TimelineRenderPreset preset)
    {
        Harness h = await Harness.CreateAsync(Trial);

        ActionResult<RenderEditTimelineResponse> result = await h.RenderAsync(new RenderEditTimelineRequest(Preset: preset));

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Creator_explicit_encode_2560x1440_is_refused()
    {
        Harness h = await Harness.CreateAsync(Creator);

        ActionResult<RenderEditTimelineResponse> result = await h.RenderAsync(new RenderEditTimelineRequest(
            Preset: TimelineRenderPreset.Hd1080p16x9, Encode: new TimelineRenderEncodeSettings(2560, 1440)));

        ObjectResult refused = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(refused.Value));
        body.RootElement.GetProperty("limit").GetInt64().Should().Be(1080);
        body.RootElement.GetProperty("current").GetInt64().Should().Be(1440);
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Creator_explicit_encode_at_the_cap_is_accepted()
    {
        Harness h = await Harness.CreateAsync(Creator);

        ActionResult<RenderEditTimelineResponse> result = await h.RenderAsync(new RenderEditTimelineRequest(
            Preset: TimelineRenderPreset.Uhd4k16x9, Encode: new TimelineRenderEncodeSettings(1080, 1920)));

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
    }

    [Fact]
    public async Task An_unlimited_plan_is_never_refused()
    {
        Harness h = await Harness.CreateAsync(OrganizationEntitlementSet.Unlimited);

        ActionResult<RenderEditTimelineResponse> result = await h.RenderAsync(new RenderEditTimelineRequest(
            Preset: TimelineRenderPreset.Uhd4k16x9, Encode: new TimelineRenderEncodeSettings(7680, 4320)));

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
    }

    [Fact]
    public async Task Without_an_entitlement_service_4K_is_accepted()
    {
        Harness h = await Harness.CreateAsync(entitlements: null);

        ActionResult<RenderEditTimelineResponse> result = await h.RenderAsync(new RenderEditTimelineRequest(Preset: TimelineRenderPreset.Uhd4k16x9));

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
    }

    private sealed class Harness
    {
        public required InferenceApiDbContext Db { get; init; }
        public required Mock<IWorkflowExecutionService> Executions { get; init; }
        public required EditTimelinesController Controller { get; init; }
        public required Guid ProjectId { get; init; }
        public required Guid TimelineId { get; init; }

        public Task<ActionResult<RenderEditTimelineResponse>> RenderAsync(RenderEditTimelineRequest request) =>
            Controller.RenderEditTimeline(ProjectId, TimelineId, request, CancellationToken.None);

        public static Task<Harness> CreateAsync(OrganizationEntitlementSet plan)
        {
            Mock<IOrganizationEntitlements> entitlements = new();
            entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(new ValueTask<OrganizationEntitlementSet>(plan));
            return CreateAsync(entitlements.Object);
        }

        public static async Task<Harness> CreateAsync(IOrganizationEntitlements? entitlements)
        {
            InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

            Guid userId = Guid.NewGuid();
            Guid orgId = TestTenancy.OrgOf(userId);
            Project project = new()
            {
                Id = Guid.NewGuid(),
                Name = "p",
                OwnerId = userId,
                OrganizationId = orgId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Projects.Add(project);

            Guid timelineId = Guid.NewGuid();
            EditTimeline document = new(timelineId, project.Id, "doc",
                new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
                [], [], null, null);
            db.EditTimelines.Add(new EditTimelineEntity
            {
                Id = timelineId,
                ProjectId = project.Id,
                Name = "doc",
                DocumentJson = JsonSerializer.Serialize(document, EditTimelineJson.Options),
                Revision = 1
            });
            db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "VideoTransform", AgentType = AgentType.VideoTransform, IsBuiltIn = true });
            await db.SaveChangesAsync();

            Mock<ICurrentUser> currentUser = new();
            currentUser.SetupGet(u => u.UserId).Returns(userId);
            currentUser.SetupGet(u => u.Email).Returns("test@example.com");
            currentUser.SetupGet(u => u.IsAuthenticated).Returns(true);
            currentUser.SetupGet(u => u.EmailVerified).Returns(true);
            currentUser.SetupGet(u => u.OrganizationId).Returns(orgId);
            currentUser.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);

            Mock<IWorkflowExecutionService> executions = new();
            executions.Setup(e => e.QueueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(),
                    It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>(),
                    WorkflowDefinitionKind.TimelineRender, It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()))
                .ReturnsAsync((Guid _, Guid workflowId, Guid _, string? _, ExecutionInitiator _, CancellationToken _, bool _, RunChoices? _,
                    WorkflowDefinitionKind _, Guid? _, Func<CancellationToken, Task>? _) =>
                    new WorkflowExecutionResponse(Guid.NewGuid(), workflowId, "Queued", null, null, 0, null, null, null, [], []));

            EditTimelinesController controller = new(
                db, currentUser.Object, TestTenancy.Access(db, currentUser.Object),
                new Mock<IFileStorageService>().Object, executions.Object, entitlements: entitlements);

            return new Harness { Db = db, Executions = executions, Controller = controller, ProjectId = project.Id, TimelineId = timelineId };
        }
    }
}
