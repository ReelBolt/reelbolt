#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// T-004 on a real PostgreSQL: resume's conditional UPDATE (<c>WHERE status IN (Failed, Cancelled)</c>) lets
/// exactly one of many concurrent resumes, each on its own pool as separate replicas would be, queue the run; the
/// others answer 409 and resume_count moves by one. Also reads the engine-only snapshot_of_execution_id the API
/// has no mapping for. Each test is a <c>[PostgresFact]</c>: without a server it is skipped, not passed.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed partial class ExecutionResumeRoutePostgresTests
{
    private sealed class Pg : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _admin;
        private readonly string _name;
        private readonly List<NpgsqlDataSource> _sources = [];

        private Pg(NpgsqlDataSource admin, string name, string connectionString)
        {
            _admin = admin;
            _name = name;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<Pg?> CreateAsync()
        {
            string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
            if (string.IsNullOrWhiteSpace(baseConnection))
                return null;

            string name = "reelbolt_resume_test_" + Guid.NewGuid().ToString("N")[..12];
            NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {name}"))
            {
                await create.ExecuteNonQueryAsync();
            }

            Pg pg = new(admin, name, PostgresTestEnvironment.ForDatabase(baseConnection, name, maxPoolSize: 10));
            await using (InferenceApiDbContext api = pg.NewDb())
            {
                await api.GetService<IMigrator>().MigrateAsync();
            }

            await using (WorkflowEngineDbContext engine = new(
                new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(pg.ConnectionString).Options))
            {
                await engine.Database.MigrateAsync();
            }

            return pg;
        }

        public InferenceApiDbContext NewDb(NpgsqlDataSource? source = null, params IInterceptor[] interceptors)
        {
            DbContextOptionsBuilder<InferenceApiDbContext> builder = new();
            if (source is null)
                builder.UseNpgsql(ConnectionString);
            else
                builder.UseNpgsql(source);
            if (interceptors.Length > 0)
                builder.AddInterceptors(interceptors);
            return new InferenceApiDbContext(builder.Options);
        }

        /// <summary>A separate pool, as a second replica would have.</summary>
        public NpgsqlDataSource NewDataSource()
        {
            NpgsqlDataSource source = NpgsqlDataSource.Create(ConnectionString);
            _sources.Add(source);
            return source;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (NpgsqlDataSource source in _sources)
                await source.DisposeAsync();

            NpgsqlConnection.ClearAllPools();
            await using (NpgsqlCommand drop = _admin.CreateCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)"))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await _admin.DisposeAsync();
        }
    }

    private sealed class OwnerUser(Guid userId, Guid organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "owner@example.test";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = OrganizationRole.Owner;
    }

    private sealed record Seeded(Guid User, Guid Org, Guid Project, Guid Workflow, Guid Snapshot, Guid Run);

    /// <summary>A Team org's Owner, a two-step workflow, the run's engine-taken snapshot, and a Failed run of it.</summary>
    private static async Task<Seeded> SeedFailedRunAsync(Pg pg, bool engineTaken = true)
    {
        Guid user = Guid.NewGuid();
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        Guid agent = Guid.NewGuid();
        Guid workflow = Guid.NewGuid();
        Guid snapshot = Guid.NewGuid();
        Guid run = Guid.NewGuid();
        Guid firstStep = Guid.NewGuid();
        Guid secondStep = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

        await using InferenceApiDbContext db = pg.NewDb();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = $"{user:N}@example.test", DisplayName = "owner", EmailVerifiedAt = now });
        db.Organizations.Add(new Organization { Id = org, Name = "o", Slug = "o-" + org.ToString("N"), Kind = OrganizationKind.Team });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = org, UserId = user, Role = OrganizationRole.Owner });
        db.Projects.Add(new Project { Id = project, Name = "p", OwnerId = user, OrganizationId = org, CreatedAt = now, UpdatedAt = now });
        db.AgentDefinitions.Add(new AgentDefinition { Id = agent, Name = "AuthorAgent", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = now });
        db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = workflow, Name = "w", ProjectId = project, CreatedAt = now, UpdatedAt = now });
        WorkflowDefinition snap = new() { Id = snapshot, Name = "w (run snapshot)", ProjectId = project, Kind = WorkflowDefinitionKind.ExecutionSnapshot, CreatedAt = now, UpdatedAt = now };
        snap.Steps.Add(new WorkflowStep { Id = firstStep, WorkflowDefinitionId = snapshot, StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = agent });
        snap.Steps.Add(new WorkflowStep { Id = secondStep, WorkflowDefinitionId = snapshot, StepOrder = 2, StepType = StepType.Agent, AgentDefinitionId = agent });
        db.WorkflowDefinitions.Add(snap);
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = run,
            WorkflowDefinitionId = workflow,
            SnapshotDefinitionId = snapshot,
            AgentSnapshotJson = "{\"agents\":[]}",
            ProjectId = project,
            OrganizationId = org,
            InitiatedByUserId = user,
            Status = ExecutionStatus.Failed,
            ErrorMessage = "step 2 failed",
            UserRequest = "r",
            QueuedAt = now.AddMinutes(-5),
            StartedAt = now.AddMinutes(-4),
            CompletedAt = now.AddMinutes(-1),
        });
        db.WorkflowStepResults.Add(new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = run, WorkflowStepId = firstStep, Status = StepStatus.Completed, Output = "one", ExecutedAt = now.AddMinutes(-3) });
        db.WorkflowStepResults.Add(new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = run, WorkflowStepId = secondStep, Status = StepStatus.Failed, Output = "", ExecutedAt = now.AddMinutes(-2) });
        await db.SaveChangesAsync();
        if (engineTaken)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_definitions SET snapshot_of_execution_id = {run} WHERE id = {snapshot}");
        return new Seeded(user, org, project, workflow, snapshot, run);
    }

    private static WorkflowsController OwnerController(InferenceApiDbContext db, Seeded s, IPublishEndpoint publish)
    {
        OwnerUser current = new(s.User, s.Org);
        return new WorkflowsController(
            db, current, TestTenancy.Access(db, current), publish,
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance);
    }

    [PostgresFact]
    public async Task Concurrent_resumes_on_separate_pools_queue_the_run_exactly_once()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null)
            return;
        Seeded s = await SeedFailedRunAsync(pg);
        Mock<IPublishEndpoint> publish = new();
        const int callers = 6;
        using Barrier start = new(callers);

        Task<IActionResult?>[] calls = [.. Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            NpgsqlDataSource source = pg.NewDataSource();
            await using InferenceApiDbContext db = pg.NewDb(source);
            // Warm the pool so every caller reaches its reads at about the same moment.
            await db.Database.ExecuteSqlRawAsync("SELECT 1");
            start.SignalAndWait();
            ActionResult<WorkflowExecutionResponse> result = await OwnerController(db, s, publish.Object)
                .ResumeExecution(s.Project, s.Workflow, s.Run, null, CancellationToken.None);
            return (IActionResult?)result.Result;
        }))];
        IActionResult?[] results = await Task.WhenAll(calls);

        results.Count(r => r is ObjectResult { StatusCode: 202 }).Should().Be(1);
        results.Count(r => r is ConflictObjectResult).Should().Be(callers - 1);
        await using (InferenceApiDbContext check = pg.NewDb())
        {
            WorkflowExecution row = await check.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.Run);
            row.Status.Should().Be(ExecutionStatus.Queued);
            row.PendingResume.Should().BeTrue();
            row.ResumeCount.Should().Be(1);
        }

        publish.Verify(p => p.Publish(It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == s.Run), It.IsAny<CancellationToken>()), Times.Once);
    }

    [PostgresFact]
    public async Task The_engine_only_owner_column_decides_canResume_and_resume()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null)
            return;
        Seeded owned = await SeedFailedRunAsync(pg, engineTaken: true);
        Seeded archived = await SeedFailedRunAsync(pg, engineTaken: false);

        await using InferenceApiDbContext db = pg.NewDb();
        WorkflowExecutionResponse ownedDto = (WorkflowExecutionResponse)((OkObjectResult)(await OwnerController(db, owned, Mock.Of<IPublishEndpoint>())
            .GetExecution(owned.Project, owned.Run, CancellationToken.None)).Result!).Value!;
        List<WorkflowExecutionResponse> archivedList = (List<WorkflowExecutionResponse>)((OkObjectResult)(await OwnerController(db, archived, Mock.Of<IPublishEndpoint>())
            .ListExecutions(archived.Project, archived.Workflow, CancellationToken.None)).Result!).Value!;
        ActionResult<WorkflowExecutionResponse> refused = await OwnerController(db, archived, Mock.Of<IPublishEndpoint>())
            .ResumeExecution(archived.Project, archived.Workflow, archived.Run, null, CancellationToken.None);

        ownedDto.CanResume.Should().BeTrue();
        archivedList.Should().ContainSingle().Which.CanResume.Should().BeFalse();
        refused.Result.Should().BeOfType<ConflictObjectResult>()
            .Which.Value.Should().BeEquivalentTo(new { code = "RESUME_UNAVAILABLE" }, o => o.ExcludingMissingMembers());
    }

    [PostgresFact]
    public async Task Retry_supersedes_the_runs_results_in_place()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null)
            return;
        Seeded s = await SeedFailedRunAsync(pg);

        await using (InferenceApiDbContext db = pg.NewDb())
        {
            ActionResult<WorkflowExecutionResponse> result = await OwnerController(db, s, Mock.Of<IPublishEndpoint>())
                .RetryExecution(s.Project, s.Workflow, s.Run, null, CancellationToken.None);
            ((ObjectResult)result.Result!).StatusCode.Should().Be(200);
        }

        await using InferenceApiDbContext check = pg.NewDb();
        List<WorkflowStepResult> rows = await check.WorkflowStepResults.AsNoTracking().Where(r => r.WorkflowExecutionId == s.Run).ToListAsync();
        rows.Should().HaveCount(2).And.OnlyContain(r => r.SupersededAt != null);
        WorkflowExecution row = await check.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.Run);
        row.Status.Should().Be(ExecutionStatus.Queued);
        row.PendingResume.Should().BeFalse();
        row.StartedAt.Should().BeNull();
    }

    /// <summary>Releases its parties together once all of them have arrived.</summary>
    private sealed class Rendezvous(int parties)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) == parties)
                _all.TrySetResult();
            return _all.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// Holds this context's first UPDATE of workflow_executions (a resume's or a retry's conditional re-queue) until
    /// the other caller has reached its own, so the two really race at the UPDATE: both passed every check first.
    /// </summary>
    private sealed partial class MeetAtFirstExecutionUpdate(Rendezvous rendezvous) : DbCommandInterceptor
    {
        private bool _met;

        [GeneratedRegex(@"^\s*UPDATE\s+""?workflow_executions""?\s", RegexOptions.IgnoreCase)]
        private static partial Regex ExecutionUpdate();

        private async ValueTask MeetOnceAsync(DbCommand command)
        {
            if (_met || !ExecutionUpdate().IsMatch(command.CommandText))
                return;
            _met = true;
            await rendezvous.ArriveAsync();
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await MeetOnceAsync(command);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await MeetOnceAsync(command);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [PostgresFact]
    public async Task A_resume_racing_a_retry_at_the_update_leaves_exactly_one_of_them_whole_and_the_other_409()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null)
            return;

        // Which side wins is up to the server; the invariants hold for either. Several rounds, several orderings.
        for (int round = 0; round < 6; round++)
        {
            Seeded s = await SeedFailedRunAsync(pg);
            Mock<IPublishEndpoint> publish = new();
            Rendezvous rendezvous = new(2);

            async Task<IActionResult?> CallAsync(bool resume)
            {
                await using InferenceApiDbContext db = pg.NewDb(pg.NewDataSource(), new MeetAtFirstExecutionUpdate(rendezvous));
                WorkflowsController controller = OwnerController(db, s, publish.Object);
                ActionResult<WorkflowExecutionResponse> result = resume
                    ? await controller.ResumeExecution(s.Project, s.Workflow, s.Run, null, CancellationToken.None)
                    : await controller.RetryExecution(s.Project, s.Workflow, s.Run, null, CancellationToken.None);
                return result.Result;
            }

            Task<IActionResult?> resumeCall = Task.Run(() => CallAsync(resume: true));
            Task<IActionResult?> retryCall = Task.Run(() => CallAsync(resume: false));
            IActionResult? resumed = await resumeCall;
            IActionResult? retried = await retryCall;

            bool resumeWon = resumed is ObjectResult { StatusCode: 202 };
            bool retryWon = retried is ObjectResult { StatusCode: 200 };
            (resumeWon ^ retryWon).Should().BeTrue($"exactly one of resume ({resumed}) and retry ({retried}) queues the run");
            (resumeWon ? retried : resumed).Should().BeOfType<ConflictObjectResult>("the loser of the UPDATE answers 409")
                .Which.Value.Should().BeEquivalentTo(
                    new { code = resumeWon ? "RUN_CHANGED" : "RUN_NOT_RESUMABLE" }, o => o.ExcludingMissingMembers());

            await using InferenceApiDbContext check = pg.NewDb();
            WorkflowExecution row = await check.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.Run);
            List<WorkflowStepResult> results = await check.WorkflowStepResults.AsNoTracking().Where(r => r.WorkflowExecutionId == s.Run).ToListAsync();
            row.Status.Should().Be(ExecutionStatus.Queued);
            results.Should().HaveCount(2);
            if (resumeWon)
            {
                // A whole resume, and nothing of the retry: no superseding, the original start kept.
                row.PendingResume.Should().BeTrue();
                row.ResumeCount.Should().Be(1);
                row.StartedAt.Should().NotBeNull();
                results.Should().OnlyContain(r => r.SupersededAt == null);
            }
            else
            {
                // A whole retry, and nothing of the resume.
                row.PendingResume.Should().BeFalse();
                row.ResumeCount.Should().Be(0);
                row.StartedAt.Should().BeNull();
                results.Should().OnlyContain(r => r.SupersededAt != null);
            }

            publish.Verify(p => p.Publish(It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == s.Run), It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    /// <summary>Runs <paramref name="whilePaused"/> just before this context's first UPDATE of workflow_executions.</summary>
    private sealed class PauseAtFirstExecutionUpdate(Func<Task> whilePaused) : DbCommandInterceptor
    {
        private bool _paused;

        private async ValueTask PauseOnceAsync(DbCommand command)
        {
            if (_paused || !Regex.IsMatch(command.CommandText, @"^\s*UPDATE\s+""?workflow_executions""?\s", RegexOptions.IgnoreCase))
                return;
            _paused = true;
            await whilePaused();
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await PauseOnceAsync(command);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await PauseOnceAsync(command);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Security SQFU-S6: a workflow delete that has already found no Queued/Running run (it holds the definition
    /// FOR UPDATE and is about to re-point the runs) must not be overtaken by a Retry or Resume of one of them: the
    /// re-queue takes the definition FOR SHARE, waits for the delete, then finds the run filed under its snapshot
    /// and answers 409. Without the lock the run would be Queued under a deleted workflow.
    /// </summary>
    [PostgresFact]
    public async Task A_retry_or_resume_during_a_workflow_delete_waits_for_it_and_never_queues_a_run_of_the_deleted_workflow()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null)
            return;

        foreach (bool resume in new[] { true, false })
        {
            Seeded s = await SeedFailedRunAsync(pg);
            Mock<IPublishEndpoint> publish = new();
            NpgsqlDataSource observer = pg.NewDataSource();
            Task<IActionResult?>? requeue = null;

            await using (InferenceApiDbContext deleting = pg.NewDb(null, new PauseAtFirstExecutionUpdate(async () =>
            {
                requeue = Task.Run<IActionResult?>(async () =>
                {
                    await using InferenceApiDbContext db = pg.NewDb(pg.NewDataSource());
                    WorkflowsController controller = OwnerController(db, s, publish.Object);
                    ActionResult<WorkflowExecutionResponse> result = resume
                        ? await controller.ResumeExecution(s.Project, s.Workflow, s.Run, null, CancellationToken.None)
                        : await controller.RetryExecution(s.Project, s.Workflow, s.Run, null, CancellationToken.None);
                    return result.Result;
                });

                // Until the re-queue is blocked on the delete's lock (or, without the lock, has already finished).
                for (int i = 0; i < 200 && !requeue.IsCompleted; i++)
                {
                    await using NpgsqlCommand waiting = observer.CreateCommand(
                        "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'");
                    if ((long)(await waiting.ExecuteScalarAsync())! > 0)
                        break;
                    await Task.Delay(50);
                }
            })))
            {
                WorkflowDefinition workflow = await deleting.WorkflowDefinitions.SingleAsync(w => w.Id == s.Workflow);
                (await new WorkflowStepReplacer(deleting).DeleteAsync(workflow, CancellationToken.None)).Should().BeTrue();
            }

            IActionResult? answered = await requeue!;
            answered.Should().BeOfType<ConflictObjectResult>(resume ? "resume" : "retry")
                .Which.Value.Should().BeEquivalentTo(
                    new { code = resume ? "RUN_NOT_RESUMABLE" : "RUN_CHANGED" }, o => o.ExcludingMissingMembers());
            await using InferenceApiDbContext check = pg.NewDb();
            WorkflowExecution row = await check.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.Run);
            row.Status.Should().Be(ExecutionStatus.Failed);
            row.WorkflowDefinitionId.Should().Be(s.Snapshot, "the delete filed the run under its own snapshot");
            (await check.WorkflowDefinitions.AnyAsync(w => w.Id == s.Workflow)).Should().BeFalse();
            (await check.WorkflowStepResults.AnyAsync(r => r.WorkflowExecutionId == s.Run && r.SupersededAt != null)).Should().BeFalse();
            publish.Invocations.Should().BeEmpty();
        }
    }
}
