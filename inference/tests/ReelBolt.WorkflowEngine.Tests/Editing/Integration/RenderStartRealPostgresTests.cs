#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
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
using ReelBolt.WorkflowEngine.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Editing.Integration;

/// <summary>
/// T0.2 (render half): <c>POST …/edit-timelines/{id}/render</c> against a real PostgreSQL through
/// the REAL <see cref="WorkflowExecutionService"/> — only the message bus is a fake. The audit found
/// the render path broken behind a mocked execution service (400 on every preset string, then 500
/// with an orphan hidden workflow per attempt). Runs only when <c>REELBOLT_TEST_PG</c> names a
/// connection string with CREATEDB (see <c>Billing/CreditAccountProvisionerTests</c>); each test gets
/// its own throwaway database, migrated by the API and engine contexts exactly as in production.
/// </summary>
public sealed class RenderStartRealPostgresTests
{
    /// <summary>The request body the Deliver workspace sends (RenderSettings.tsx), with every preset string it offers.</summary>
    [Theory]
    [InlineData("Hd1080p16x9")]
    [InlineData("Uhd4k16x9")]
    [InlineData("Vertical9x16")]
    [InlineData("Square1x1")]
    [InlineData("Portrait4x5")]
    [InlineData("Master")]
    [InlineData("AudioOnly")]
    public async Task Every_preset_string_the_web_sends_starts_a_render(string preset)
    {
        await RealPostgres.RunAsync(async pg =>
        {
            Seeded seeded = await pg.SeedAsync(timelineName: "Café");
            string body = "{\"revision\":1,\"preset\":\"" + preset + "\",\"range\":null,\"outputFileName\":null,"
                + "\"registerProjectFile\":true,\"loudness\":null,"
                + "\"encode\":{\"width\":null,\"height\":null,\"codec\":\"H264\",\"crf\":18,\"speed\":\"Medium\",\"audioBitrateKbps\":192}}";
            RenderEditTimelineRequest request = JsonSerializer.Deserialize<RenderEditTimelineRequest>(body, WebJson)!;

            (ActionResult<RenderEditTimelineResponse> result, Mock<IPublishEndpoint> publish) = await pg.RenderAsync(seeded, request);

            ObjectResult accepted = result.Result.Should().BeOfType<ObjectResult>().Subject;
            accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted, because: JsonSerializer.Serialize(accepted.Value));
            RenderEditTimelineResponse response = accepted.Value.Should().BeOfType<RenderEditTimelineResponse>().Subject;
            response.OutputFileName.Should().Be("Cafe.mp4");

            (long workflows, long steps, long executions) = await pg.CountRenderRowsAsync();
            workflows.Should().Be(1);
            steps.Should().Be(1);
            executions.Should().Be(1);
            publish.Verify(p => p.Publish(It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == response.ExecutionId),
                It.IsAny<CancellationToken>()), Times.Once);
        });
    }

    [Fact]
    public async Task A_failure_after_the_workflow_insert_leaves_no_rows()
    {
        await RealPostgres.RunAsync(async pg =>
        {
            Seeded seeded = await pg.SeedAsync(timelineName: "doc");
            Mock<IPlanGateService> gate = new();
            gate.Setup(g => g.CheckExecuteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("forced failure inside QueueAsync"));

            Func<Task> render = () => pg.RenderAsync(seeded, new RenderEditTimelineRequest(), gate.Object);

            await render.Should().ThrowAsync<InvalidOperationException>();
            (long workflows, long steps, long executions) = await pg.CountRenderRowsAsync();
            workflows.Should().Be(0);
            steps.Should().Be(0);
            executions.Should().Be(0);
        });
    }

    [Fact]
    public async Task A_concurrency_capped_org_gets_a_plain_429_and_no_rows()
    {
        await RealPostgres.RunAsync(async pg =>
        {
            Seeded seeded = await pg.SeedAsync(timelineName: "doc");
            PlanGateViolation busy = new(PlanGate.ConcurrencyCode, "concurrent_executions",
                "The trial plan allows 1 execution(s) at once and 1 are running. Wait for one to finish or upgrade your plan.",
                StatusCodes.Status429TooManyRequests, 1, 1);
            Mock<IPlanGateService> gate = new();
            gate.Setup(g => g.CheckExecuteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(busy);

            (ActionResult<RenderEditTimelineResponse> result, _) = await pg.RenderAsync(seeded, new RenderEditTimelineRequest(), gate.Object);

            ObjectResult refused = result.Result.Should().BeOfType<ObjectResult>().Subject;
            refused.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
            JsonSerializer.Serialize(refused.Value).Should().Contain("Wait for one to finish");
            (long workflows, long steps, long executions) = await pg.CountRenderRowsAsync();
            (workflows, steps, executions).Should().Be((0L, 0L, 0L));
        });
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web) { AllowOutOfOrderMetadataProperties = true };

    internal sealed record Seeded(Guid UserId, Guid OrganizationId, Guid ProjectId, Guid TimelineId);

    /// <summary>One throwaway, fully migrated database per test.</summary>
    internal sealed class RealPostgres
    {
        private RealPostgres(string connectionString) => ConnectionString = connectionString;

        public string ConnectionString { get; }

        public DbContextOptions<InferenceApiDbContext> ApiOptions =>
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(ConnectionString).Options;

        /// <summary>Runs <paramref name="body"/> against a fresh database; a no-op without <c>REELBOLT_TEST_PG</c>.</summary>
        public static async Task RunAsync(Func<RealPostgres, Task> body)
        {
            string? baseConnection = Environment.GetEnvironmentVariable("REELBOLT_TEST_PG");
            if (string.IsNullOrWhiteSpace(baseConnection))
            {
                return;
            }

            string dbName = "reelbolt_render_test_" + Guid.NewGuid().ToString("N")[..12];
            await using NpgsqlDataSource admin = NpgsqlDataSource.Create(baseConnection);
            await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
            {
                await create.ExecuteNonQueryAsync();
            }

            NpgsqlConnectionStringBuilder csb = new(baseConnection) { Database = dbName };
            try
            {
                await using (InferenceApiDbContext api = new(
                    new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options))
                {
                    await api.GetService<IMigrator>().MigrateAsync();
                }

                await using (WorkflowEngineDbContext engine = new(
                    new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(csb.ConnectionString).Options))
                {
                    await engine.GetService<IMigrator>().MigrateAsync();
                }

                await body(new RealPostgres(csb.ConnectionString));
            }
            finally
            {
                NpgsqlConnection.ClearAllPools();
                await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
                await drop.ExecuteNonQueryAsync();
            }
        }

        public async Task ExecAsync(string sql)
        {
            await using NpgsqlConnection c = new(ConnectionString);
            await c.OpenAsync();
            await using NpgsqlCommand cmd = new(sql, c);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using NpgsqlConnection c = new(ConnectionString);
            await c.OpenAsync();
            await using NpgsqlCommand cmd = new(sql, c);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        public async Task<(long Workflows, long Steps, long Executions)> CountRenderRowsAsync() => (
            await ScalarAsync("SELECT count(*) FROM workflow_definitions"),
            await ScalarAsync("SELECT count(*) FROM workflow_steps"),
            await ScalarAsync("SELECT count(*) FROM workflow_executions"));

        /// <summary>A user, their organization, a project, the VideoTransform placeholder agent and one timeline.</summary>
        public async Task<Seeded> SeedAsync(string timelineName)
        {
            Guid user = Guid.NewGuid();
            Guid org = TestTenancy.OrgOf(user);
            Guid project = Guid.NewGuid();
            await ExecAsync($"INSERT INTO application_users (id,email,display_name,created_at,must_change_password,is_admin) VALUES ('{user}','r{user:N}@t.local','r',now(),false,false)");
            await ExecAsync($"INSERT INTO organizations (id,name,slug,kind,created_at,updated_at,compute_preference) VALUES ('{org}','o','o-{org:N}','Team',now(),now(),'Auto')");
            await ExecAsync($"INSERT INTO projects (id,name,owner_id,organization_id,status,created_at,updated_at) VALUES ('{project}','p','{user}','{org}','Draft',now(),now())");

            Guid timelineId = Guid.NewGuid();
            await using InferenceApiDbContext db = new(ApiOptions);
            if (!await db.AgentDefinitions.AnyAsync(a => a.AgentType == AgentType.VideoTransform))
            {
                db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "VideoTransform", AgentType = AgentType.VideoTransform, IsBuiltIn = true });
            }

            EditTimeline document = new(timelineId, project, timelineName,
                new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
                [], [], null, null);
            db.EditTimelines.Add(new EditTimelineEntity
            {
                Id = timelineId,
                ProjectId = project,
                Name = timelineName,
                DocumentJson = JsonSerializer.Serialize(document, EditTimelineJson.Options),
                Revision = 1
            });
            await db.SaveChangesAsync();
            return new Seeded(user, org, project, timelineId);
        }

        /// <summary>The controller over a fresh context, the REAL execution service and a fake bus.</summary>
        public async Task<(ActionResult<RenderEditTimelineResponse>, Mock<IPublishEndpoint>)> RenderAsync(
            Seeded seeded, RenderEditTimelineRequest request, IPlanGateService? planGate = null)
        {
            await using InferenceApiDbContext db = new(ApiOptions);
            Mock<ICurrentUser> currentUser = new();
            currentUser.SetupGet(u => u.UserId).Returns(seeded.UserId);
            currentUser.SetupGet(u => u.Email).Returns("render@example.com");
            currentUser.SetupGet(u => u.IsAuthenticated).Returns(true);
            currentUser.SetupGet(u => u.EmailVerified).Returns(true);
            currentUser.SetupGet(u => u.OrganizationId).Returns(seeded.OrganizationId);
            currentUser.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);

            Mock<IPublishEndpoint> publish = new();
            WorkflowExecutionService service = new(db, publish.Object, NullLogger<WorkflowExecutionService>.Instance, planGate);
            EditTimelinesController controller = new(
                db, currentUser.Object, TestTenancy.Access(db, currentUser.Object),
                new Mock<IFileStorageService>().Object, service);

            ActionResult<RenderEditTimelineResponse> result =
                await controller.RenderEditTimeline(seeded.ProjectId, seeded.TimelineId, request, CancellationToken.None);
            return (result, publish);
        }
    }
}
