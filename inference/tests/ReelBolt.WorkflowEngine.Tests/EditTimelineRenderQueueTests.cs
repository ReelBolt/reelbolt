#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The editor's render endpoint queues its hidden <see cref="WorkflowDefinitionKind.TimelineRender"/>
/// workflow through the REAL <see cref="WorkflowExecutionService"/>: a mocked service hid that the
/// public execute path looks workflows up as <see cref="WorkflowDefinitionKind.Standard"/> only, so
/// every accepted render answered 500. The public path must still refuse the hidden kind. The
/// request also binds its enums by name, which is what the web's Deliver workspace sends.
/// </summary>
public class EditTimelineRenderQueueTests
{
    [Fact]
    public async Task Accepted_render_is_queued_under_the_TimelineRender_kind()
    {
        Harness h = await Harness.CreateAsync();

        ActionResult<RenderEditTimelineResponse> result =
            await h.RenderAsync(new RenderEditTimelineRequest(Preset: TimelineRenderPreset.Hd1080p16x9));

        ObjectResult accepted = result.Result.Should().BeOfType<ObjectResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        RenderEditTimelineResponse body = accepted.Value.Should().BeOfType<RenderEditTimelineResponse>().Subject;

        WorkflowDefinition workflow = await h.Db.WorkflowDefinitions.SingleAsync(w => w.Id == body.WorkflowId);
        workflow.Kind.Should().Be(WorkflowDefinitionKind.TimelineRender);
        WorkflowExecution row = await h.Db.WorkflowExecutions.SingleAsync(e => e.Id == body.ExecutionId);
        row.WorkflowDefinitionId.Should().Be(workflow.Id);
        h.Publish.Verify(p => p.Publish(It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == body.ExecutionId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Trial_shape_preset_is_queued_for_the_engine_to_clamp()
    {
        Harness h = await Harness.CreateAsync(new OrganizationEntitlementSet { Tier = "trial", MaxOutputHeight = 720 });

        ActionResult<RenderEditTimelineResponse> result =
            await h.RenderAsync(new RenderEditTimelineRequest(Preset: TimelineRenderPreset.Hd1080p16x9));

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        (await h.Db.WorkflowExecutions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_public_execute_path_still_refuses_a_TimelineRender_workflow()
    {
        Harness h = await Harness.CreateAsync();
        ActionResult<RenderEditTimelineResponse> result =
            await h.RenderAsync(new RenderEditTimelineRequest(Preset: TimelineRenderPreset.Master));
        Guid workflowId = ((RenderEditTimelineResponse)((ObjectResult)result.Result!).Value!).WorkflowId;

        Func<Task> execute = () => h.Service.ExecuteAsync(
            h.ProjectId, workflowId, h.UserId, userRequest: null, ExecutionInitiator.Human, CancellationToken.None);

        await execute.Should().ThrowAsync<WorkflowNotFoundException>();
        (await h.Db.WorkflowExecutions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_unverified_email_cannot_queue_a_render()
    {
        Harness h = await Harness.CreateAsync(emailVerified: false);

        ActionResult<RenderEditTimelineResponse> result =
            await h.RenderAsync(new RenderEditTimelineRequest(Preset: TimelineRenderPreset.Hd1080p16x9));

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        (await h.Db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_concurrency_capped_org_gets_the_429_body_and_no_orphan_render_workflow()
    {
        PlanGateViolation busy = new(PlanGate.ConcurrencyCode, "concurrent_executions", "Your plan runs 1 workflow at a time.",
            StatusCodes.Status429TooManyRequests, 1, 1);
        Mock<IPlanGateService> gate = new();
        gate.Setup(g => g.CheckExecuteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(busy);
        Harness h = await Harness.CreateAsync(planGate: gate.Object);

        ActionResult<RenderEditTimelineResponse> result =
            await h.RenderAsync(new RenderEditTimelineRequest(Preset: TimelineRenderPreset.Hd1080p16x9));

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        (await h.Db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("\"Uhd4k16x9\"", TimelineRenderPreset.Uhd4k16x9)]
    [InlineData("\"hd1080p16x9\"", TimelineRenderPreset.Hd1080p16x9)]
    [InlineData("\"audioOnly\"", TimelineRenderPreset.AudioOnly)]
    public void Render_request_binds_the_preset_by_name(string json, TimelineRenderPreset expected)
    {
        RenderEditTimelineRequest? request = JsonSerializer.Deserialize<RenderEditTimelineRequest>(
            $"{{\"preset\":{json}}}", new JsonSerializerOptions(JsonSerializerDefaults.Web));

        request!.Preset.Should().Be(expected);
    }

    [Fact]
    public void Render_request_binds_the_encode_enums_by_name()
    {
        const string json = """
            {"preset":"Master","encode":{"width":1280,"height":720,"codec":"H265","crf":20,"speed":"Slow","audioBitrateKbps":192}}
            """;

        RenderEditTimelineRequest? request = JsonSerializer.Deserialize<RenderEditTimelineRequest>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        request!.Encode!.Codec.Should().Be(TimelineRenderVideoCodec.H265);
        request.Encode.Speed.Should().Be(TimelineRenderEncoderSpeed.Slow);
    }

    [Theory]
    [InlineData("""{"preset":1}""")]
    [InlineData("""{"preset":"Master","encode":{"codec":1}}""")]
    [InlineData("""{"preset":"Master","encode":{"speed":0}}""")]
    public void Render_request_refuses_an_integer_enum_as_a_binding_error(string json)
    {
        Action bind = () => JsonSerializer.Deserialize<RenderEditTimelineRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        bind.Should().Throw<JsonException>();
    }

    [Fact]
    public void A_stored_step_config_with_legacy_integer_enums_still_loads_in_the_engine()
    {
        string legacy = $$"""{"version":1,"editTimelineId":"{{Guid.NewGuid()}}","preset":5,"encode":{"codec":1,"speed":2,"crf":18,"audioBitrateKbps":192},"outputFileName":"a.mp4"}""";

        TimelineRenderStepConfig? config = JsonSerializer.Deserialize<TimelineRenderStepConfig>(
            legacy, ReelBolt.WorkflowEngine.Execution.StepExecutors.TimelineRenderStepExecutor.ConfigJsonOptions);

        config!.Preset.Should().Be(TimelineRenderPreset.Master);
        config.Encode!.Codec.Should().Be(TimelineRenderVideoCodec.H265);
        config.Encode.Speed.Should().Be(TimelineRenderEncoderSpeed.Slow);
        JsonSerializer.Serialize(config, ReelBolt.WorkflowEngine.Execution.StepExecutors.TimelineRenderStepExecutor.ConfigJsonOptions)
            .Should().Contain("\"preset\":\"Master\"");
    }

    [Fact]
    public async Task A_failure_after_the_workflow_insert_leaves_no_rows()
    {
        Mock<IPlanGateService> gate = new();
        gate.Setup(g => g.CheckExecuteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("forced"));
        Harness h = await Harness.CreateAsync(planGate: gate.Object);

        Func<Task> render = () => h.RenderAsync(new RenderEditTimelineRequest());

        await render.Should().ThrowAsync<InvalidOperationException>();
        h.Db.ChangeTracker.Clear();
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        (await h.Db.WorkflowSteps.CountAsync()).Should().Be(0);
        (await h.Db.WorkflowExecutions.CountAsync()).Should().Be(0);
        h.Publish.Verify(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_workflow_and_execution_are_committed_before_the_bus_message_goes_out()
    {
        Harness h = await Harness.CreateAsync();
        long committedWhenPublished = -1;
        h.Publish.Setup(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                // A second connection sees only committed rows: the engine must find the execution.
                using SqliteConnection other = new(h.ConnectionString);
                other.Open();
                using SqliteCommand count = other.CreateCommand();
                count.CommandText = "SELECT count(*) FROM workflow_executions";
                committedWhenPublished = (long)count.ExecuteScalar()!;
            })
            .Returns(Task.CompletedTask);

        ActionResult<RenderEditTimelineResponse> result = await h.RenderAsync(new RenderEditTimelineRequest());

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        committedWhenPublished.Should().Be(1);
    }

    [Theory]
    [InlineData("Café", "Cafe.mp4")]
    [InlineData("", "timeline-render.mp4")]
    [InlineData("   ", "timeline-render.mp4")]
    [InlineData("日本語", "timeline-render.mp4")]
    [InlineData("Ünïcödé — final cut!", "Unicode - final cut.mp4")]
    [InlineData("a..b", "a.b.mp4")]
    [InlineData("-intro", "intro.mp4")]
    [InlineData("take 2.", "take 2.mp4")]
    public void The_default_file_name_is_slugified_to_the_allowlist(string timelineName, string expected)
    {
        string name = EditTimelinesController.DefaultRenderFileName(timelineName);

        name.Should().Be(expected);
        new TimelineRenderStepConfig(Guid.NewGuid(), OutputFileName: name).Validate().Should().BeEmpty();
    }

    [Fact]
    public async Task A_non_ascii_timeline_name_renders_with_a_slugified_file_name()
    {
        Harness h = await Harness.CreateAsync(timelineName: "Café");

        ActionResult<RenderEditTimelineResponse> result = await h.RenderAsync(new RenderEditTimelineRequest(OutputFileName: ""));

        ObjectResult accepted = result.Result.Should().BeOfType<ObjectResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        accepted.Value.Should().BeOfType<RenderEditTimelineResponse>().Which.OutputFileName.Should().Be("Cafe.mp4");
    }

    private sealed class Harness
    {
        public required InferenceApiDbContext Db { get; init; }
        public required WorkflowExecutionService Service { get; init; }
        public required Mock<IPublishEndpoint> Publish { get; init; }
        public required EditTimelinesController Controller { get; init; }
        public required Guid ProjectId { get; init; }
        public required Guid TimelineId { get; init; }
        public required Guid UserId { get; init; }
        public required string ConnectionString { get; init; }

        public Task<ActionResult<RenderEditTimelineResponse>> RenderAsync(RenderEditTimelineRequest request) =>
            Controller.RenderEditTimeline(ProjectId, TimelineId, request, CancellationToken.None);

        public static async Task<Harness> CreateAsync(
            OrganizationEntitlementSet? plan = null, bool emailVerified = true, IPlanGateService? planGate = null,
            string timelineName = "doc")
        {
            // SQLite (shared in-memory, kept alive by the context's open connection) rather than
            // InMemory: the render start is one transaction, and InMemory ignores transactions.
            string connectionString = $"Data Source=render-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=False";
            SqliteConnection connection = new(connectionString);
            connection.Open();
            InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseSqlite(connection)
                .Options);
            db.Database.EnsureCreated();
            // The workflow_* tables are ExcludeFromMigrations in the API context; the engine owns them.
            using (ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext engineDb = new(
                new DbContextOptionsBuilder<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>().UseSqlite(connection).Options))
            {
                engineDb.GetService<IRelationalDatabaseCreator>().CreateTables();
            }

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
            EditTimeline document = new(timelineId, project.Id, timelineName,
                new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
                [], [], null, null);
            db.EditTimelines.Add(new EditTimelineEntity
            {
                Id = timelineId,
                ProjectId = project.Id,
                Name = timelineName,
                DocumentJson = JsonSerializer.Serialize(document, EditTimelineJson.Options),
                Revision = 1
            });
            db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "VideoTransform", AgentType = AgentType.VideoTransform, IsBuiltIn = true });
            await db.SaveChangesAsync();

            Mock<ICurrentUser> currentUser = new();
            currentUser.SetupGet(u => u.UserId).Returns(userId);
            currentUser.SetupGet(u => u.Email).Returns("test@example.com");
            currentUser.SetupGet(u => u.IsAuthenticated).Returns(true);
            currentUser.SetupGet(u => u.EmailVerified).Returns(emailVerified);
            currentUser.SetupGet(u => u.OrganizationId).Returns(orgId);
            currentUser.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);

            IOrganizationEntitlements? entitlements = null;
            if (plan is not null)
            {
                Mock<IOrganizationEntitlements> mock = new();
                mock.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .Returns(new ValueTask<OrganizationEntitlementSet>(plan));
                entitlements = mock.Object;
            }

            Mock<IPublishEndpoint> publish = new();
            WorkflowExecutionService service = new(db, publish.Object, NullLogger<WorkflowExecutionService>.Instance, planGate);

            EditTimelinesController controller = new(
                db, currentUser.Object, TestTenancy.Access(db, currentUser.Object),
                new Mock<IFileStorageService>().Object, service, entitlements: entitlements);

            return new Harness
            {
                Db = db,
                Service = service,
                Publish = publish,
                Controller = controller,
                ProjectId = project.Id,
                TimelineId = timelineId,
                UserId = userId,
                ConnectionString = connectionString
            };
        }
    }
}
