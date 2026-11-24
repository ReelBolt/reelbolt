#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Activity;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.EditTimelines;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Qa;

/// <summary>
/// QA probe (background-activity T-031): the three Postgres-only paths the executors did not exercise - the activity feed's
/// queries, the applier's change signal, and the resolve route's Serializable transaction - on a real migrated schema.
/// Skipped unless <c>REELBOLT_TEST_PG</c> is set (inference/tests/run-postgres-tests.sh).
/// </summary>
[Collection("Postgres")]
public class QaBackgroundActivityPostgresTests
{
    private sealed class Db(string baseConnection, string name, string connection) : IAsyncDisposable
    {
        public string Connection { get; } = connection;
        public DbContextOptions<InferenceApiDbContext> Api { get; } = new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options;
        public DbContextOptions<WorkflowEngineDbContext> Engine { get; } = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options;

        public static async Task<Db> CreateAsync()
        {
            string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
            string name = "reelbolt_qa_activity_" + Guid.NewGuid().ToString("N")[..12];
            await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {name}"))
                await create.ExecuteNonQueryAsync();
            Db db = new(baseConnection, name, PostgresTestEnvironment.ForDatabase(baseConnection, name));
            await using (InferenceApiDbContext api = new(db.Api))
                await api.GetService<IMigrator>().MigrateAsync();
            await using (WorkflowEngineDbContext engine = new(db.Engine))
                await engine.GetService<IMigrator>().MigrateAsync();
            return db;
        }

        public InferenceApiDbContext Context() => new(Api);

        /// <summary>Saves entities with FK checks off (replica role), so engine/API parents need not all exist.</summary>
        public async Task SeedAsync(params object[] entities)
        {
            await using InferenceApiDbContext ctx = Context();
            await ctx.Database.OpenConnectionAsync();
            await ctx.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
            ctx.AddRange(entities);
            await ctx.SaveChangesAsync();
        }

        public async Task<int> ExecAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using NpgsqlConnection conn = new(Connection);
            await conn.OpenAsync();
            await using NpgsqlCommand cmd = new(sql, conn);
            foreach ((string n, object v) in parameters) cmd.Parameters.AddWithValue(n, v);
            return await cmd.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlDataSource cleanup = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = cleanup.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static Mock<ICurrentUser> User(Guid user, Guid org, OrganizationRole role)
    {
        Mock<ICurrentUser> u = new();
        u.SetupGet(x => x.UserId).Returns(user);
        u.SetupGet(x => x.Email).Returns("qa@example.com");
        u.SetupGet(x => x.IsAuthenticated).Returns(true);
        u.SetupGet(x => x.EmailVerified).Returns(true);
        u.SetupGet(x => x.OrganizationId).Returns(org);
        u.SetupGet(x => x.OrgRole).Returns(role);
        return u;
    }

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // ------------------------------------------------------------------ activity feed

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Qa_activity_feed_queries_run_on_postgres_scoped_windowed_and_ordered()
    {
        await using Db db = await Db.CreateAsync();
        DateTime now = DateTime.UtcNow;
        Guid me = Guid.NewGuid(), org = Guid.NewGuid(), otherOrg = Guid.NewGuid();
        Guid project = Guid.NewGuid(), foreignProject = Guid.NewGuid(), timeline = Guid.NewGuid(), file = Guid.NewGuid();

        List<object> seed =
        [
            new Project { Id = project, Name = "Launch film", OrganizationId = org, OwnerId = me, CreatedAt = now, UpdatedAt = now },
            new Project { Id = foreignProject, Name = "Not yours", OrganizationId = otherOrg, OwnerId = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now },
            new EditTimelineEntity { Id = timeline, ProjectId = project, Name = "Main cut" },
            new ProjectFile { Id = file, ProjectId = project, OriginalFileName = "beach.mp4", StorageKey = "k/beach.mp4", MimeType = "video/mp4" },
        ];

        Guid Run(WorkflowDefinitionKind kind, ExecutionStatus status, DateTime queued, DateTime? completed, Guid? proj = null, Guid? inOrg = null,
            string? renderConfig = null, Guid? snapshotOf = null, string name = "My workflow")
        {
            Guid def = Guid.NewGuid(), step = Guid.NewGuid(), exec = Guid.NewGuid();
            seed.Add(new WorkflowDefinition { Id = def, ProjectId = proj ?? project, Name = name, Kind = kind, CreatedAt = now, UpdatedAt = now });
            seed.Add(new WorkflowStep
            {
                Id = step,
                WorkflowDefinitionId = def,
                StepOrder = 1,
                StepType = kind == WorkflowDefinitionKind.TimelineRender ? StepType.TimelineRender : StepType.Agent,
                TimelineRenderConfigJson = renderConfig
            });
            seed.Add(new WorkflowExecution
            {
                Id = exec,
                WorkflowDefinitionId = def,
                ProjectId = proj ?? project,
                OrganizationId = inOrg ?? org,
                Status = status,
                QueuedAt = queued,
                StartedAt = status == ExecutionStatus.Queued ? null : queued,
                CompletedAt = completed,
                CurrentStepId = status is ExecutionStatus.Running ? step : null,
                InitiatedByUserId = me,
                SnapshotDefinitionId = snapshotOf
            });
            if (status == ExecutionStatus.Failed)
            {
                seed.Add(new WorkflowStepResult
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = exec,
                    WorkflowStepId = step,
                    Status = StepStatus.Failed,
                    FailureCode = "VENDOR_DOWN",
                    FailureClass = FailureClass.PlatformProviderError,
                    CreditsChargedMillis = 0,
                    ExecutedAt = completed ?? now,
                    CompletedAt = completed ?? now
                });
            }
            return exec;
        }

        Guid running = Run(WorkflowDefinitionKind.Standard, ExecutionStatus.Running, now.AddMinutes(-30), null);
        Guid waiting = Run(WorkflowDefinitionKind.TimelineRender, ExecutionStatus.Queued, now.AddMinutes(-2), null,
            renderConfig: JsonSerializer.Serialize(new TimelineRenderStepConfig(timeline, AudioJobKind: "loudness"), ConfigJson));
        Guid failed = Run(WorkflowDefinitionKind.Standard, ExecutionStatus.Failed, now.AddHours(-2), now.AddHours(-1));
        Guid done = Run(WorkflowDefinitionKind.GraphicDesign, ExecutionStatus.Passed, now.AddHours(-3), now.AddMinutes(-10));
        Guid hidden = Run((WorkflowDefinitionKind)7, ExecutionStatus.Running, now.AddMinutes(-1), null);
        Guid objectRun = Run(WorkflowDefinitionKind.ObjectAnalysis, ExecutionStatus.Running, now.AddMinutes(-5), null);
        Guid oldFinished = Run(WorkflowDefinitionKind.Standard, ExecutionStatus.Passed, now.AddDays(-3), now.AddDays(-2));
        Guid oldRunning = Run(WorkflowDefinitionKind.Standard, ExecutionStatus.Running, now.AddDays(-8), null);
        Guid foreign = Run(WorkflowDefinitionKind.Standard, ExecutionStatus.Running, now.AddMinutes(-1), null, proj: foreignProject, inOrg: otherOrg);
        Guid foreignOrgOwnProject = Run(WorkflowDefinitionKind.Standard, ExecutionStatus.Running, now.AddMinutes(-1), null, inOrg: otherOrg);
        seed.Add(new ObjectEffectRequest
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            ProjectId = project,
            ExecutionId = objectRun,
            SourceKind = ObjectEffectRequest.SourceFile,
            SourceId = file.ToString("D"),
            Prompt = "the license plate",
            MatchScope = "AllInstances",
            Effect = "Blur",
            Strength = 0.5,
            TimelineId = timeline,
            Status = ObjectEffectRequest.StatusQueued,
            CreatedByUserId = me,
            CreatedAt = now.AddMinutes(-5)
        });
        seed.Add(new MediaAnalysisRequest
        {
            Id = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            ProjectId = project,
            OrganizationId = org,
            MediaRef = "file:" + file,
            Kind = "silence",
            Language = "en",
            State = MediaAnalysisStates.Running,
            HoldMillis = 2500,
            RequestedByUserId = me,
            CreatedAt = now.AddMinutes(-4),
            UpdatedAt = now.AddMinutes(-3)
        });
        await db.SeedAsync([.. seed]);

        await using InferenceApiDbContext ctx = db.Context();
        ActivityFeedService feed = new(ctx, User(me, org, OrganizationRole.Member).Object, Options.Create(new BillingOptions { Enabled = true }));
        ActivityFeedDto dto = await feed.GetAsync(100, default);

        List<Guid?> ids = dto.Items.Select(i => i.ExecutionId).ToList();
        ids.Should().NotContain([foreign, foreignOrgOwnProject, oldFinished, oldRunning]);
        ids.Should().Contain([running, waiting, failed, done, hidden, objectRun]);
        dto.Items.Should().ContainSingle(i => i.Type == "mediaAnalysis").Which.Title.Should().Be("beach.mp4");
        dto.Items.Single(i => i.ExecutionId == hidden).Type.Should().Be("hiddenRun");
        dto.Items.Single(i => i.ExecutionId == hidden).Subject.KindNumber.Should().Be(7);
        dto.Items.Single(i => i.ExecutionId == waiting).Type.Should().Be("loudness");
        dto.Items.Single(i => i.ExecutionId == waiting).Title.Should().Be("Main cut");
        dto.Items.Single(i => i.ExecutionId == objectRun).Title.Should().Be("the license plate");
        ActivityItemDto f = dto.Items.Single(i => i.ExecutionId == failed);
        (f.Failure!.Code, f.Failure.Refunded).Should().Be(("VENDOR_DOWN", (bool?)true));
        dto.ActiveCount.Should().Be(5, "running, waiting, hidden, object run and the media analysis");
        dto.Items.Select(i => i.State).Should().Equal(["running", "running", "running", "running", "waiting", "done", "failed"]);
        dto.Items.All(i => i.CreatedAt.Kind == DateTimeKind.Utc).Should().BeTrue();

        // Billing sees nothing on the real schema either.
        ActivityFeedService billing = new(ctx, User(me, org, OrganizationRole.Billing).Object, Options.Create(new BillingOptions { Enabled = true }));
        (await billing.GetAsync(100, default)).Items.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ applier change signal

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Qa_applier_change_signal_translates_on_postgres_and_ignores_only_its_own_revisions()
    {
        await using Db db = await Db.CreateAsync();
        DateTime created = DateTime.UtcNow.AddMinutes(-10);
        Guid org = Guid.NewGuid(), project = Guid.NewGuid(), timeline = Guid.NewGuid();
        ObjectEffectRequest row = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            ProjectId = project,
            ExecutionId = Guid.NewGuid(),
            SourceKind = ObjectEffectRequest.SourceFile,
            SourceId = Guid.NewGuid().ToString("D"),
            Prompt = "plate",
            MatchScope = "AllInstances",
            Effect = "Blur",
            Strength = 0.5,
            TimelineId = timeline,
            Status = ObjectEffectRequest.StatusSucceeded,
            CreatedAt = created
        };
        await db.SeedAsync(
            new Project { Id = project, Name = "p", OrganizationId = org, OwnerId = Guid.NewGuid(), CreatedAt = created, UpdatedAt = created },
            new EditTimelineEntity { Id = timeline, ProjectId = project, Name = "t" },
            row,
            new EditTimelineOp { Id = Guid.NewGuid(), TimelineId = timeline, Revision = 2, OpsJson = "[]", CreatedAt = created.AddMilliseconds(300) });

        await using InferenceApiDbContext ctx = db.Context();
        ObjectEffectApplier applier = new(ctx, Mock.Of<IObjectAnalysisService>(), Mock.Of<IEditTimelineWriter>(), NullLogger<ObjectEffectApplier>.Instance);
        MethodInfo changed = typeof(ObjectEffectApplier).GetMethod("TimelineChangedSinceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Task<bool> Changed() => (Task<bool>)changed.Invoke(applier, [row, timeline, CancellationToken.None])!;

        (await Changed()).Should().BeFalse("the only op is inside the 1 s skew margin (empty 'ours' list on Postgres)");

        // A batch the finalizer itself wrote for another request on this timeline is not a change ...
        await db.SeedAsync(
            new ObjectEffectRequest
            {
                Id = Guid.NewGuid(),
                OrganizationId = org,
                ProjectId = project,
                ExecutionId = Guid.NewGuid(),
                SourceKind = ObjectEffectRequest.SourceFile,
                SourceId = Guid.NewGuid().ToString("D"),
                Prompt = "face",
                MatchScope = "AllInstances",
                Effect = "Blur",
                Strength = 0.5,
                TimelineId = timeline,
                Status = ObjectEffectRequest.StatusApplied,
                AppliedRevision = 3,
                CreatedAt = created
            },
            new EditTimelineOp { Id = Guid.NewGuid(), TimelineId = timeline, Revision = 3, OpsJson = "[]", CreatedAt = created.AddMinutes(2) });
        (await Changed()).Should().BeFalse();

        // ... a user's batch is.
        await db.SeedAsync(new EditTimelineOp { Id = Guid.NewGuid(), TimelineId = timeline, Revision = 4, OpsJson = "[]", CreatedAt = created.AddMinutes(3) });
        (await Changed()).Should().BeTrue();
    }

    // ------------------------------------------------------------------ resolve (Serializable)

    private static async Task<(Guid User, Guid Org, Guid Project, Guid File, ObjectEffectRequest Row)> SeedResolvableAsync(Db db, string status)
    {
        Guid user = Guid.NewGuid(), org = Guid.NewGuid(), project = Guid.NewGuid(), file = Guid.NewGuid();
        ObjectEffectRequest row = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            ProjectId = project,
            ExecutionId = Guid.NewGuid(),
            SourceKind = ObjectEffectRequest.SourceFile,
            SourceId = file.ToString("D"),
            Prompt = "plate",
            MatchScope = "AllInstances",
            Effect = "Blur",
            Strength = 0.5,
            TimelineId = Guid.NewGuid(),
            Status = status,
            CreatedByUserId = user,
            CreatedAt = DateTime.UtcNow
        };
        await db.SeedAsync(new Project { Id = project, Name = "p", OrganizationId = org, OwnerId = user, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }, row);
        return (user, org, project, file, row);
    }

    private static ObjectAnalysisController Controller(InferenceApiDbContext ctx, Guid user, Guid org, OrganizationRole role = OrganizationRole.Member)
    {
        Mock<ICurrentUser> u = User(user, org, role);
        return new ObjectAnalysisController(ctx, u.Object, new ProjectAccessService(ctx, u.Object), Mock.Of<IWorkflowExecutionService>(),
            Mock.Of<IFileStorageService>(), NullLogger<ObjectAnalysisController>.Instance);
    }

    private static async Task<string?> StatusAsync(Db db, Guid id)
    {
        await using InferenceApiDbContext ctx = db.Context();
        return await ctx.ObjectEffectRequests.AsNoTracking().Where(r => r.Id == id).Select(r => r.Status).SingleAsync();
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Qa_resolve_under_serializable_applies_and_discards_idempotently_and_scopes_the_tenant()
    {
        await using Db db = await Db.CreateAsync();
        var a = await SeedResolvableAsync(db, ObjectEffectRequest.StatusNeedsReview);
        var d = await SeedResolvableAsync(db, ObjectEffectRequest.StatusSucceeded);

        for (int i = 0; i < 2; i++)
        {
            await using InferenceApiDbContext ctx = db.Context();
            (await Controller(ctx, a.User, a.Org).Resolve(a.Project, "file:" + a.File, a.Row.Id, new ObjectRequestResolveBody("applied", 9), default))
                .Should().BeOfType<NoContentResult>();
        }

        for (int i = 0; i < 2; i++)
        {
            await using InferenceApiDbContext ctx = db.Context();
            (await Controller(ctx, d.User, d.Org).Resolve(d.Project, "file:" + d.File, d.Row.Id, new ObjectRequestResolveBody("discarded", null), default))
                .Should().BeOfType<NoContentResult>();
        }

        (await StatusAsync(db, a.Row.Id)).Should().Be(ObjectEffectRequest.StatusApplied);
        (await StatusAsync(db, d.Row.Id)).Should().Be(ObjectEffectRequest.StatusDiscarded);

        // Another organization's member: 404, and nothing moves. Billing in the right org: refused.
        var other = await SeedResolvableAsync(db, ObjectEffectRequest.StatusNeedsReview);
        await using (InferenceApiDbContext ctx = db.Context())
        {
            (await Controller(ctx, a.User, a.Org).Resolve(other.Project, "file:" + other.File, other.Row.Id, new ObjectRequestResolveBody("discarded", null), default))
                .Should().BeOfType<NotFoundResult>();
            (await Controller(ctx, other.User, other.Org, OrganizationRole.Billing).Resolve(other.Project, "file:" + other.File, other.Row.Id, new ObjectRequestResolveBody("discarded", null), default))
                .Should().NotBeOfType<NoContentResult>();
        }

        (await StatusAsync(db, other.Row.Id)).Should().Be(ObjectEffectRequest.StatusNeedsReview);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Qa_resolve_losing_a_race_to_the_sweeper_claim_answers_404_and_does_not_throw()
    {
        await using Db db = await Db.CreateAsync();
        var s = await SeedResolvableAsync(db, ObjectEffectRequest.StatusSucceeded);

        // The sweeper's claim (succeeded -> applied) holds the row lock, uncommitted, while the editor's discard arrives.
        await using NpgsqlConnection sweeper = new(db.Connection);
        await sweeper.OpenAsync();
        await using NpgsqlTransaction claim = await sweeper.BeginTransactionAsync();
        await using (NpgsqlCommand cmd = new("UPDATE object_effect_requests SET status = 'applied', finalized_at = now() WHERE id = @id AND status = 'succeeded'", sweeper, claim))
        {
            cmd.Parameters.AddWithValue("id", s.Row.Id);
            (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
        }

        await using InferenceApiDbContext ctx = db.Context();
        Task<IActionResult> resolve = Controller(ctx, s.User, s.Org)
            .Resolve(s.Project, "file:" + s.File, s.Row.Id, new ObjectRequestResolveBody("discarded", null), default);
        await Task.Delay(1500);
        resolve.IsCompleted.Should().BeFalse("the resolve UPDATE waits on the sweeper's row lock");
        await claim.CommitAsync();

        Exception? thrown = null;
        IActionResult? result = null;
        try
        {
            result = await resolve.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        thrown.Should().BeNull("the route's catch is meant to turn a lost race into a 404, not a 500 (got: {0})", thrown?.GetBaseException().Message);
        result.Should().BeOfType<NotFoundResult>();
        (await StatusAsync(db, s.Row.Id)).Should().Be(ObjectEffectRequest.StatusApplied, "the sweeper's claim stands");
    }
}
