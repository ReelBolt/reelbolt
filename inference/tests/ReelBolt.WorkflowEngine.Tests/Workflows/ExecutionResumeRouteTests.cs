#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// T-004: <c>POST …/executions/{id}/resume</c> and the Retry, at the controller boundary on SQLite (the
/// conditional UPDATEs are relational). Resume: same authority as Retry, 409 <c>RUN_NOT_RESUMABLE</c> for a run that
/// is not Failed/Cancelled, 409 <c>RESUME_UNAVAILABLE</c> without an engine-taken snapshot, the plan gate and the
/// 402 priced from the frontier, then one conditional UPDATE and a publish. Retry: the same gates with the full
/// estimate, and superseding instead of deleting.
/// </summary>
public sealed class ExecutionResumeRouteTests : IDisposable
{
    private static readonly DateTime OriginalStart = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;

    public ExecutionResumeRouteTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
        }

        _db = NewDb();
        _db.Database.EnsureCreated();

        // The workflow_* tables (with the engine's snapshot_of_execution_id) come from the engine's model.
        using WorkflowEngineDbContext engineDb = new(
            new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_connection).Options);
        engineDb.GetService<IRelationalDatabaseCreator>().CreateTables();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private InferenceApiDbContext NewDb(params IInterceptor[] interceptors)
    {
        DbContextOptionsBuilder<InferenceApiDbContext> builder = new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection);
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        return new InferenceApiDbContext(builder.Options);
    }

    private sealed class User(Guid userId, OrganizationRole role, bool emailVerified = true) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public bool EmailVerified { get; } = emailVerified;
        public string Email { get; } = "user@example.test";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole { get; } = role;
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

    private static WorkflowCostEstimator Estimator(InferenceApiDbContext db, bool billingEnabled) => new(
        new SingleContextScopes(db),
        Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = billingEnabled }),
        Microsoft.Extensions.Options.Options.Create(new StepCreditEstimateOptions()),
        NullLogger<WorkflowCostEstimator>.Instance);

    private WorkflowsController Controller(
        Guid userId, IPublishEndpoint? publish = null, OrganizationRole role = OrganizationRole.Owner,
        bool billingEnabled = false, InferenceApiDbContext? db = null, bool emailVerified = true,
        IPlanGateService? planGate = null, IDesignBuildEstimateGate? designBuildGate = null)
    {
        db ??= _db;
        User user = new(userId, role, emailVerified);
        return new WorkflowsController(
            db,
            user,
            TestTenancy.Access(db, user),
            publish ?? Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance,
            planGate: planGate,
            costEstimator: Estimator(db, billingEnabled),
            designBuildGate: designBuildGate);
    }

    public enum SnapshotShape
    {
        /// <summary>The engine took the snapshot for this run (kind 5, owned by the run), agents recorded.</summary>
        EngineTaken,

        /// <summary>A T-002 archive of a legacy run's steps: kind 5, owned by no run, no agent snapshot.</summary>
        LegacyArchive,

        /// <summary>Engine-taken, but the agent snapshot is missing.</summary>
        NoAgentSnapshot
    }

    private sealed record Seeded(Guid Owner, Project Project, WorkflowDefinition Workflow, WorkflowDefinition Snapshot, WorkflowExecution Run);

    /// <summary>
    /// A project with a two-step workflow (Agent, VideoCompile), the run's snapshot of it, and a run whose first
    /// step completed and whose second failed.
    /// </summary>
    private async Task<Seeded> SeedAsync(
        ExecutionStatus status = ExecutionStatus.Failed,
        SnapshotShape shape = SnapshotShape.EngineTaken,
        long? balanceMillis = null,
        bool workflowDeleted = false,
        Guid? ownerId = null)
    {
        Guid owner = ownerId ?? Guid.NewGuid();
        Guid org = TestTenancy.OrgOf(owner);
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = owner, OrganizationId = org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "AuthorAgent", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "w", ProjectId = project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = agent.Id });
        workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, StepOrder = 2, StepType = StepType.VideoCompile, AgentDefinitionId = agent.Id });
        WorkflowDefinition snapshot = new() { Id = Guid.NewGuid(), Name = "w (run snapshot)", ProjectId = project.Id, Kind = WorkflowDefinitionKind.ExecutionSnapshot, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowStep first = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = snapshot.Id, StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = agent.Id };
        WorkflowStep second = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = snapshot.Id, StepOrder = 2, StepType = StepType.VideoCompile, AgentDefinitionId = agent.Id };
        snapshot.Steps.Add(first);
        snapshot.Steps.Add(second);

        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            // After a workflow delete (T-002) the run is filed under its own snapshot.
            WorkflowDefinitionId = workflowDeleted ? snapshot.Id : workflow.Id,
            SnapshotDefinitionId = snapshot.Id,
            AgentSnapshotJson = shape == SnapshotShape.EngineTaken ? "{\"agents\":[]}" : null,
            ProjectId = project.Id,
            OrganizationId = org,
            InitiatedByUserId = owner,
            Status = status,
            ErrorMessage = status == ExecutionStatus.Failed ? "step 2 failed" : null,
            UserRequest = "make a reel",
            QueuedAt = OriginalStart.AddMinutes(-1),
            StartedAt = OriginalStart,
            CompletedAt = status is ExecutionStatus.Failed or ExecutionStatus.Cancelled or ExecutionStatus.Passed ? OriginalStart.AddMinutes(5) : null,
        };
        run.StepResults.Add(new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = run.Id, WorkflowStepId = first.Id, Status = StepStatus.Completed, Output = "one", ExecutedAt = OriginalStart.AddMinutes(1) });
        run.StepResults.Add(new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = run.Id, WorkflowStepId = second.Id, Status = StepStatus.Failed, Output = "", ExecutedAt = OriginalStart.AddMinutes(2) });

        _db.Projects.Add(project);
        _db.AgentDefinitions.Add(agent);
        if (!workflowDeleted)
            _db.WorkflowDefinitions.Add(workflow);
        _db.WorkflowDefinitions.Add(snapshot);
        _db.WorkflowExecutions.Add(run);
        if (balanceMillis is { } balance)
            _db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OrganizationId = org, BalanceMillis = balance });
        await _db.SaveChangesAsync();

        // The owner column is engine-only (a shadow property of the engine's model).
        if (shape != SnapshotShape.LegacyArchive)
            await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_definitions SET snapshot_of_execution_id = {run.Id} WHERE id = {snapshot.Id}");

        _db.ChangeTracker.Clear();
        return new Seeded(owner, project, workflow, snapshot, run);
    }

    private Task<ActionResult<WorkflowExecutionResponse>> ResumeAsync(Seeded s, WorkflowsController controller) =>
        controller.ResumeExecution(s.Project.Id, s.Run.WorkflowDefinitionId, s.Run.Id, null, CancellationToken.None);

    private Task<ActionResult<WorkflowExecutionResponse>> RetryAsync(Seeded s, WorkflowsController controller) =>
        controller.RetryExecution(s.Project.Id, s.Run.WorkflowDefinitionId, s.Run.Id, null, CancellationToken.None);

    private async Task<WorkflowExecution> ReloadAsync(Guid runId)
    {
        await using InferenceApiDbContext fresh = NewDb();
        return await fresh.WorkflowExecutions.AsNoTracking().Include(e => e.StepResults).SingleAsync(e => e.Id == runId);
    }

    private static string? CodeOf(IActionResult? result) =>
        result is ObjectResult { Value: { } value }
            ? JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.GetProperty("code").GetString()
            : null;

    private static void ShouldBeUnchanged(WorkflowExecution after, ExecutionStatus status)
    {
        after.Status.Should().Be(status);
        after.PendingResume.Should().BeFalse();
        after.ResumeCount.Should().Be(0);
        after.StepResults.Should().OnlyContain(r => r.SupersededAt == null);
    }

    /// <summary>The credits the steps from <paramref name="fromStepOrder"/> on hold, as the API's estimator plans them.</summary>
    private async Task<long> RequiredMillisAsync(Seeded s, int fromStepOrder)
    {
        List<WorkflowStep> steps = await _db.WorkflowSteps.AsNoTracking()
            .Where(x => x.WorkflowDefinitionId == s.Snapshot.Id).OrderBy(x => x.StepOrder).ToListAsync();
        WorkflowPreflightResult preflight = await Estimator(_db, billingEnabled: true).PreflightChargedAsync(
            s.Project.OrganizationId, steps, [.. steps.Where(x => x.StepOrder >= fromStepOrder).Select(x => x.StepOrder)], null);
        return preflight.Estimate.RequiredMillis;
    }

    // ---------------------------------------------------------------- review scores follow their step results

    [Fact]
    public async Task After_a_retry_the_run_shows_only_the_new_attempts_review_scores()
    {
        Seeded s = await SeedAsync();
        Guid reviewStep = s.Snapshot.Steps.Single(x => x.StepOrder == 1).Id;

        // The first attempt's review iteration: the engine sets the row's CompletedAt, then writes its score.
        DateTime oldReview = OriginalStart.AddMinutes(3);
        _db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = s.Run.Id,
            WorkflowStepId = reviewStep,
            Status = StepStatus.Completed,
            Output = "{}",
            IterationNumber = 1,
            ExecutedAt = oldReview.AddSeconds(-30),
            CompletedAt = oldReview
        });
        _db.ReviewScores.Add(new ReviewScore { Id = Guid.NewGuid(), WorkflowExecutionId = s.Run.Id, IterationNumber = 1, Score = 4, Comments = "old", CreatedAt = oldReview.AddMilliseconds(2) });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        ActionResult<WorkflowExecutionResponse> retried = await RetryAsync(s, Controller(s.Owner));
        ObjectResult accepted = retried.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        accepted.StatusCode.Should().Be(200);
        ((WorkflowExecutionResponse)accepted.Value!).ReviewScores.Should().BeEmpty("the retry superseded every earlier score");

        // The retry's own attempt reaches the same iteration and is scored again.
        DateTime newReview = DateTime.UtcNow;
        await using (InferenceApiDbContext engine = NewDb())
        {
            engine.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = s.Run.Id,
                WorkflowStepId = reviewStep,
                Status = StepStatus.Completed,
                Output = "{}",
                IterationNumber = 1,
                ExecutedAt = newReview.AddSeconds(-30),
                CompletedAt = newReview
            });
            engine.ReviewScores.Add(new ReviewScore { Id = Guid.NewGuid(), WorkflowExecutionId = s.Run.Id, IterationNumber = 1, Score = 8, Comments = "new", CreatedAt = newReview.AddMilliseconds(2) });
            await engine.SaveChangesAsync();
        }

        WorkflowsController controller = Controller(s.Owner);
        ActionResult<WorkflowExecutionResponse> detail = await controller.GetExecution(s.Project.Id, s.Run.Id, CancellationToken.None);
        WorkflowExecutionResponse run = (WorkflowExecutionResponse)((OkObjectResult)detail.Result!).Value!;
        run.ReviewScores.Select(x => (x.IterationNumber, x.Score)).Should().Equal([(1, 8)], "the replaced attempt's score is audit only");

        ActionResult<List<WorkflowExecutionResponse>> list = await controller.ListExecutions(s.Project.Id, s.Run.WorkflowDefinitionId, CancellationToken.None);
        List<WorkflowExecutionResponse> runs = (List<WorkflowExecutionResponse>)((OkObjectResult)list.Result!).Value!;
        runs.Single(x => x.Id == s.Run.Id).ReviewScores.Select(x => (x.IterationNumber, x.Score)).Should().Equal([(1, 8)]);

        await using InferenceApiDbContext check = NewDb();
        (await check.ReviewScores.CountAsync(x => x.WorkflowExecutionId == s.Run.Id)).Should().Be(2, "nothing is deleted");
    }

    // ---------------------------------------------------------------- Resume

    [Fact]
    public async Task Resume_queues_the_same_row_as_a_resume_keeps_StartedAt_and_publishes_the_request()
    {
        Seeded s = await SeedAsync(ExecutionStatus.Failed);
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await ResumeAsync(s, Controller(s.Owner, publish.Object));

        ObjectResult accepted = result.Result.Should().BeOfType<ObjectResult>().Subject;
        accepted.StatusCode.Should().Be(202);
        WorkflowExecutionResponse body = accepted.Value.Should().BeOfType<WorkflowExecutionResponse>().Subject;
        body.Status.Should().Be("Queued");
        body.ResumeCount.Should().Be(1);
        body.CanResume.Should().BeFalse();

        WorkflowExecution after = await ReloadAsync(s.Run.Id);
        after.Status.Should().Be(ExecutionStatus.Queued);
        after.PendingResume.Should().BeTrue();
        after.ResumeCount.Should().Be(1);
        after.ErrorMessage.Should().BeNull();
        after.CompletedAt.Should().BeNull();
        after.LeaseOwner.Should().BeNull();
        after.LeaseExpiresAt.Should().BeNull();
        after.StartedAt.Should().Be(OriginalStart);
        after.SnapshotDefinitionId.Should().Be(s.Snapshot.Id);
        after.StepResults.Should().HaveCount(2).And.OnlyContain(r => r.SupersededAt == null);

        publish.Verify(p => p.Publish(
            It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == s.Run.Id && m.ProjectId == s.Project.Id && m.WorkflowDefinitionId == s.Run.WorkflowDefinitionId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Resume_of_a_cancelled_run_whose_workflow_was_deleted_is_accepted()
    {
        Seeded s = await SeedAsync(ExecutionStatus.Cancelled, workflowDeleted: true);

        ActionResult<WorkflowExecutionResponse> result = await ResumeAsync(s, Controller(s.Owner));

        ((ObjectResult)result.Result!).StatusCode.Should().Be(202);
        (await ReloadAsync(s.Run.Id)).PendingResume.Should().BeTrue();
    }

    [Fact]
    public async Task Resume_of_another_orgs_run_is_not_found_and_changes_nothing()
    {
        Seeded s = await SeedAsync();

        ActionResult<WorkflowExecutionResponse> result = await ResumeAsync(s, Controller(Guid.NewGuid()));

        result.Result.Should().BeOfType<NotFoundResult>();
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), ExecutionStatus.Failed);
    }

    [Fact]
    public async Task Resume_by_a_role_without_edit_rights_is_forbidden_and_changes_nothing()
    {
        Seeded s = await SeedAsync();

        ActionResult<WorkflowExecutionResponse> result = await ResumeAsync(s, Controller(s.Owner, role: OrganizationRole.Billing));

        result.Result.Should().BeOfType<ForbidResult>();
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), ExecutionStatus.Failed);
    }

    [Fact]
    public async Task Retry_by_a_role_without_edit_rights_is_forbidden_and_changes_nothing()
    {
        Seeded s = await SeedAsync();
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await RetryAsync(s, Controller(s.Owner, publish.Object, role: OrganizationRole.Billing));

        result.Result.Should().BeOfType<ForbidResult>();
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), ExecutionStatus.Failed);
        publish.Invocations.Should().BeEmpty("a refused Retry queues nothing");
    }

    [Theory]
    [InlineData(ExecutionStatus.Passed)]
    [InlineData(ExecutionStatus.Running)]
    [InlineData(ExecutionStatus.Queued)]
    public async Task Resume_of_a_run_that_is_not_failed_or_cancelled_is_409_RUN_NOT_RESUMABLE(ExecutionStatus status)
    {
        Seeded s = await SeedAsync(status);
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await ResumeAsync(s, Controller(s.Owner, publish.Object));

        result.Result.Should().BeOfType<ConflictObjectResult>();
        CodeOf(result.Result).Should().Be("RUN_NOT_RESUMABLE");
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), status);
        publish.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SnapshotShape.LegacyArchive)]
    [InlineData(SnapshotShape.NoAgentSnapshot)]
    public async Task Resume_of_a_run_without_an_engine_taken_snapshot_is_409_RESUME_UNAVAILABLE_pointing_at_Retry(SnapshotShape shape)
    {
        Seeded s = await SeedAsync(ExecutionStatus.Failed, shape);
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await ResumeAsync(s, Controller(s.Owner, publish.Object));

        ConflictObjectResult conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        CodeOf(conflict).Should().Be("RESUME_UNAVAILABLE");
        JsonSerializer.Serialize(conflict.Value).Should().Contain("Retry");
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), ExecutionStatus.Failed);
        publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Resume_is_402_when_the_balance_cannot_cover_the_remaining_steps_and_changes_nothing()
    {
        Seeded s = await SeedAsync(balanceMillis: 0);
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await ResumeAsync(s, Controller(s.Owner, publish.Object, billingEnabled: true));

        ObjectResult refusal = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(402);
        CodeOf(refusal).Should().Be("INSUFFICIENT_CREDITS");
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), ExecutionStatus.Failed);
        publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Resume_prices_only_the_steps_from_the_frontier_on_while_Retry_prices_the_whole_run()
    {
        Seeded probe = await SeedAsync();
        long remaining = await RequiredMillisAsync(probe, fromStepOrder: 2);
        long whole = await RequiredMillisAsync(probe, fromStepOrder: 1);
        remaining.Should().BeGreaterThan(0).And.BeLessThan(whole);

        // Enough for step 2 (the frontier) alone, not for the whole run.
        Seeded s = await SeedAsync(balanceMillis: remaining);

        ActionResult<WorkflowExecutionResponse> retried = await RetryAsync(s, Controller(s.Owner, billingEnabled: true));
        ((ObjectResult)retried.Result!).StatusCode.Should().Be(402);

        ActionResult<WorkflowExecutionResponse> resumed = await ResumeAsync(s, Controller(s.Owner, billingEnabled: true));
        ((ObjectResult)resumed.Result!).StatusCode.Should().Be(202);
    }

    /// <summary>Runs <paramref name="race"/> once, just before this context's first UPDATE of workflow_executions.</summary>
    private sealed class BeforeFirstExecutionUpdate(Func<Task> race) : DbCommandInterceptor
    {
        private bool _ran;

        private async ValueTask RunOnceAsync(DbCommand command)
        {
            if (_ran
                || !command.CommandText.TrimStart().StartsWith("UPDATE \"workflow_executions\"", StringComparison.OrdinalIgnoreCase))
                return;
            _ran = true;
            await race();
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await RunOnceAsync(command);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await RunOnceAsync(command);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Two_concurrent_resumes_queue_the_run_once_and_the_loser_gets_409()
    {
        Seeded s = await SeedAsync();
        Mock<IPublishEndpoint> publish = new();
        ActionResult<WorkflowExecutionResponse>? winner = null;

        // The second resume passes every check, then the first lands its UPDATE in between.
        await using InferenceApiDbContext racing = NewDb(new BeforeFirstExecutionUpdate(async () =>
        {
            await using InferenceApiDbContext other = NewDb();
            winner = await ResumeAsync(s, Controller(s.Owner, publish.Object, db: other));
        }));
        ActionResult<WorkflowExecutionResponse> loser = await ResumeAsync(s, Controller(s.Owner, publish.Object, db: racing));

        ((ObjectResult)winner!.Result!).StatusCode.Should().Be(202);
        loser.Result.Should().BeOfType<ConflictObjectResult>();
        CodeOf(loser.Result).Should().Be("RUN_NOT_RESUMABLE");
        WorkflowExecution after = await ReloadAsync(s.Run.Id);
        after.Status.Should().Be(ExecutionStatus.Queued);
        after.ResumeCount.Should().Be(1);
        publish.Verify(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Runs <paramref name="sql"/> (with <c>@id</c> and <c>@snapshot</c> bound) on this context's own connection and
    /// transaction just before its first UPDATE of workflow_executions: what a concurrent resume, retry or workflow
    /// delete would have committed between the route's checks and its conditional UPDATE.
    /// </summary>
    private sealed class ChangeRowBeforeFirstExecutionUpdate(string sql, Guid runId, Guid snapshotId) : DbCommandInterceptor
    {
        private bool _ran;

        private async ValueTask RunOnceAsync(DbCommand command)
        {
            if (_ran || !command.CommandText.TrimStart().StartsWith("UPDATE \"workflow_executions\"", StringComparison.OrdinalIgnoreCase))
                return;
            _ran = true;
            await using DbCommand change = command.Connection!.CreateCommand();
            change.Transaction = command.Transaction;
            change.CommandText = sql;
            AddParameter(change, "@id", runId.ToString().ToUpperInvariant());
            AddParameter(change, "@snapshot", snapshotId.ToString().ToUpperInvariant());
            if (sql.Contains("@lease", StringComparison.Ordinal))
                AddParameter(change, "@lease", DateTime.UtcNow.AddSeconds(60));
            (await change.ExecuteNonQueryAsync()).Should().Be(1, "the simulated concurrent change must hit the run");
        }

        private static void AddParameter(DbCommand command, string name, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await RunOnceAsync(command);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    public enum ConcurrentChange
    {
        /// <summary>Another resume or retry queued the run first.</summary>
        RequeuedByAnother,

        /// <summary>The workflow was deleted: the run is filed under its own snapshot (T-002).</summary>
        WorkflowDeleted,

        /// <summary>The run itself is gone (a project or account purge).</summary>
        RunDeleted
    }

    private static string SqlFor(ConcurrentChange change) => change switch
    {
        ConcurrentChange.RequeuedByAnother => "UPDATE workflow_executions SET status = 'Queued', pending_resume = 1, resume_count = resume_count + 1 WHERE id = @id",
        ConcurrentChange.WorkflowDeleted => "UPDATE workflow_executions SET workflow_definition_id = @snapshot WHERE id = @id",
        _ => "DELETE FROM workflow_executions WHERE id = @id"
    };

    private static string MessageFragmentFor(ConcurrentChange change) => change switch
    {
        ConcurrentChange.RequeuedByAnother => "already queued again",
        ConcurrentChange.WorkflowDeleted => "workflow was deleted",
        _ => "no longer exists"
    };

    [Theory]
    [InlineData(false, ConcurrentChange.RequeuedByAnother, "RUN_CHANGED")]
    [InlineData(false, ConcurrentChange.WorkflowDeleted, "RUN_CHANGED")]
    [InlineData(false, ConcurrentChange.RunDeleted, "RUN_CHANGED")]
    [InlineData(true, ConcurrentChange.RequeuedByAnother, "RUN_NOT_RESUMABLE")]
    [InlineData(true, ConcurrentChange.WorkflowDeleted, "RUN_NOT_RESUMABLE")]
    [InlineData(true, ConcurrentChange.RunDeleted, "RUN_NOT_RESUMABLE")]
    public async Task A_retry_or_resume_that_loses_its_update_says_what_changed_never_that_the_run_is_still_stopping(
        bool resume, ConcurrentChange change, string expectedCode)
    {
        Seeded s = await SeedAsync();
        Mock<IPublishEndpoint> publish = new();
        await using InferenceApiDbContext racing = NewDb(new ChangeRowBeforeFirstExecutionUpdate(SqlFor(change), s.Run.Id, s.Snapshot.Id));
        WorkflowsController controller = Controller(s.Owner, publish.Object, db: racing);

        ActionResult<WorkflowExecutionResponse> result = resume ? await ResumeAsync(s, controller) : await RetryAsync(s, controller);

        ConflictObjectResult conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        CodeOf(conflict).Should().Be(expectedCode);
        JsonSerializer.Serialize(conflict.Value).Should().Contain(MessageFragmentFor(change)).And.NotContain("still stopping");
        publish.Invocations.Should().BeEmpty();
        // Nothing of the loser landed: no result was superseded by a retry that did not queue the run.
        (await _db.WorkflowStepResults.AsNoTracking().AnyAsync(r => r.WorkflowExecutionId == s.Run.Id && r.SupersededAt != null))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Resume_while_the_previous_run_still_holds_its_lease_is_409_RUN_STILL_STOPPING_and_changes_nothing()
    {
        Seeded s = await SeedAsync(ExecutionStatus.Cancelled);
        await _db.WorkflowExecutions.Where(e => e.Id == s.Run.Id).ExecuteUpdateAsync(set => set
            .SetProperty(e => e.LeaseOwner, "engine-a")
            .SetProperty(e => e.LeaseExpiresAt, (DateTime?)DateTime.UtcNow.AddSeconds(60)));
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await ResumeAsync(s, Controller(s.Owner, publish.Object));

        result.Result.Should().BeOfType<ConflictObjectResult>();
        CodeOf(result.Result).Should().Be("RUN_STILL_STOPPING");
        WorkflowExecution after = await ReloadAsync(s.Run.Id);
        ShouldBeUnchanged(after, ExecutionStatus.Cancelled);
        after.LeaseOwner.Should().Be("engine-a");
        publish.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_lease_renewed_between_the_checks_and_the_update_is_409_RUN_STILL_STOPPING_and_the_lease_is_kept(bool resume)
    {
        Seeded s = await SeedAsync(ExecutionStatus.Cancelled);
        Mock<IPublishEndpoint> publish = new();
        // The stopped run's engine renews its lease after the route's lease check, before its conditional UPDATE.
        await using InferenceApiDbContext racing = NewDb(new ChangeRowBeforeFirstExecutionUpdate(
            "UPDATE workflow_executions SET lease_owner = 'engine-a', lease_expires_at = @lease WHERE id = @id", s.Run.Id, s.Snapshot.Id));
        WorkflowsController controller = Controller(s.Owner, publish.Object, db: racing);

        ActionResult<WorkflowExecutionResponse> result = resume ? await ResumeAsync(s, controller) : await RetryAsync(s, controller);

        ConflictObjectResult conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        CodeOf(conflict).Should().Be("RUN_STILL_STOPPING");
        JsonSerializer.Serialize(conflict.Value).Should().Contain("still stopping");
        WorkflowExecution after = await ReloadAsync(s.Run.Id);
        ShouldBeUnchanged(after, ExecutionStatus.Cancelled);
        after.LeaseOwner.Should().Be("engine-a", "the renewed lease is never nulled");
        after.LeaseExpiresAt.Should().BeAfter(DateTime.UtcNow);
        publish.Invocations.Should().BeEmpty();
    }

    /// <summary>A plan at its concurrent-run cap (B8a): the resolution gate passes, the concurrency gate refuses.</summary>
    private static Mock<IPlanGateService> PlanAtConcurrencyCap()
    {
        Mock<IPlanGateService> plan = new();
        plan.Setup(p => p.CheckResolutionAsync(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null);
        plan.Setup(p => p.CheckExecuteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanGateViolation("PLAN_LIMIT_REACHED", "max_concurrent_executions", "Your plan runs 1 workflow at a time.", 429, 1, 1));
        return plan;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resume_and_Retry_are_429_at_the_plans_concurrency_cap_before_the_credit_check(bool resume)
    {
        // A zero balance with billing on: a refusal that came after the credit preflight would be its 402.
        Seeded s = await SeedAsync(balanceMillis: 0);
        Mock<IPlanGateService> plan = PlanAtConcurrencyCap();
        Mock<IPublishEndpoint> publish = new();
        WorkflowsController controller = Controller(s.Owner, publish.Object, billingEnabled: true, planGate: plan.Object);

        ActionResult<WorkflowExecutionResponse> result = resume ? await ResumeAsync(s, controller) : await RetryAsync(s, controller);

        ObjectResult refusal = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(429);
        CodeOf(refusal).Should().Be("PLAN_LIMIT_REACHED");
        plan.Verify(p => p.CheckExecuteAsync(s.Project.OrganizationId, It.IsAny<CancellationToken>()), Times.Once);
        WorkflowExecution after = await ReloadAsync(s.Run.Id);
        ShouldBeUnchanged(after, ExecutionStatus.Failed);
        after.StartedAt.Should().Be(OriginalStart);
        publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_deleted_between_the_checks_and_the_resume_update_is_409_RUN_NOT_RESUMABLE_not_a_500()
    {
        Seeded s = await SeedAsync();
        Mock<IPublishEndpoint> publish = new();

        // Every check passes, then the row goes (an account purge, a project delete) before the UPDATE.
        await using InferenceApiDbContext racing = NewDb(new BeforeFirstExecutionUpdate(async () =>
        {
            await using InferenceApiDbContext other = NewDb();
            await other.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM workflow_executions WHERE id = {s.Run.Id}");
        }));
        ActionResult<WorkflowExecutionResponse> result = await ResumeAsync(s, Controller(s.Owner, publish.Object, db: racing));

        result.Result.Should().BeOfType<ConflictObjectResult>();
        CodeOf(result.Result).Should().Be("RUN_NOT_RESUMABLE");
        publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Resume_of_a_run_of_a_stale_v1_profile_workflow_is_409_like_Execute_and_Retry()
    {
        Seeded s = await SeedAsync(ExecutionStatus.Failed, balanceMillis: 0);
        WorkflowDefinition stale = DesignSystems.Templates.ProfileWorkflowFixtures.V1(s.Project.Id, s.Workflow.Steps.First().AgentDefinitionId);
        _db.WorkflowDefinitions.Add(stale);
        await _db.SaveChangesAsync();
        await _db.WorkflowExecutions.Where(e => e.Id == s.Run.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.WorkflowDefinitionId, stale.Id));
        Mock<IPublishEndpoint> publish = new();

        // Billing on with a zero balance: a refusal that came after the preflight would be its 402.
        ActionResult<WorkflowExecutionResponse> result = await Controller(s.Owner, publish.Object, billingEnabled: true)
            .ResumeExecution(s.Project.Id, stale.Id, s.Run.Id, null, CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
        CodeOf(result.Result).Should().Be("PROFILE_WORKFLOW_OUTDATED");
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), ExecutionStatus.Failed);
        publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Resume_by_a_caller_whose_email_is_not_verified_is_403_EMAIL_NOT_VERIFIED_and_changes_nothing()
    {
        Seeded s = await SeedAsync();
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await ResumeAsync(s, Controller(s.Owner, publish.Object, emailVerified: false));

        ObjectResult refusal = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(403);
        CodeOf(refusal).Should().Be("EMAIL_NOT_VERIFIED");
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), ExecutionStatus.Failed);
        publish.Invocations.Should().BeEmpty();
    }

    /// <summary>A plan whose resolution cap refuses every run choice, and records what it was asked.</summary>
    private static Mock<IPlanGateService> RefusingPlan()
    {
        Mock<IPlanGateService> plan = new();
        plan.Setup(p => p.CheckResolutionAsync(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanGateViolation("PLAN_NOT_ENTITLED", "output_resolution", "Your plan renders up to 1080p.", 402));
        return plan;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resume_and_Retry_are_402_PLAN_NOT_ENTITLED_at_the_runs_own_resolution_before_the_credit_check(bool resume)
    {
        // A zero balance with billing on: the credit 402 would answer INSUFFICIENT_CREDITS, so the code tells the two apart.
        Seeded s = await SeedAsync(balanceMillis: 0);
        await _db.WorkflowExecutions.Where(e => e.Id == s.Run.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.OutputResolution, (int?)2160));
        Mock<IPlanGateService> plan = RefusingPlan();
        Mock<IPublishEndpoint> publish = new();
        WorkflowsController controller = Controller(s.Owner, publish.Object, billingEnabled: true, planGate: plan.Object);

        ActionResult<WorkflowExecutionResponse> result = resume ? await ResumeAsync(s, controller) : await RetryAsync(s, controller);

        ObjectResult refusal = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(402);
        CodeOf(refusal).Should().Be("PLAN_NOT_ENTITLED");
        plan.Verify(p => p.CheckResolutionAsync(s.Project.OrganizationId, 2160, It.IsAny<CancellationToken>()), Times.Once);
        WorkflowExecution after = await ReloadAsync(s.Run.Id);
        ShouldBeUnchanged(after, ExecutionStatus.Failed);
        after.StartedAt.Should().Be(OriginalStart);
        publish.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Another_projects_run_named_under_the_callers_own_project_is_404_on_Resume_and_Retry(bool resume, bool sameOrganization)
    {
        Seeded mine = await SeedAsync();
        // The other project is in the caller's own organization (Edit rights there too) or in another one.
        Seeded theirs = await SeedAsync(ownerId: sameOrganization ? mine.Owner : null);
        Mock<IPublishEndpoint> publish = new();
        WorkflowsController controller = Controller(mine.Owner, publish.Object);

        // The caller's own project in the URL, the other project's workflow and run ids after it.
        ActionResult<WorkflowExecutionResponse> result = resume
            ? await controller.ResumeExecution(mine.Project.Id, theirs.Run.WorkflowDefinitionId, theirs.Run.Id, null, CancellationToken.None)
            : await controller.RetryExecution(mine.Project.Id, theirs.Run.WorkflowDefinitionId, theirs.Run.Id, null, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        ShouldBeUnchanged(await ReloadAsync(theirs.Run.Id), ExecutionStatus.Failed);
        ShouldBeUnchanged(await ReloadAsync(mine.Run.Id), ExecutionStatus.Failed);
        publish.Invocations.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the build estimate gate on Resume

    /// <summary>
    /// The profile-build gate's estimator: the reference the cap is derived from (organization null, 231 credits high,
    /// so the cap is 300.3) and the organization's own estimate (<paramref name="highMillis"/>).
    /// </summary>
    private sealed class BuildEstimate(long highMillis) : IWorkflowCostEstimator
    {
        public bool BillingEnabled => true;

        private static WorkflowCostEstimate Of(long high) => new(true, false, 0, high, 0, []);

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default)
            => Task.FromResult(Of(organizationId is null ? 231_000 : highMillis));

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default)
            => throw new NotSupportedException("the gate never runs the preflight");

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default)
            => EstimateAsync(organizationId, runSteps, ct);

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default)
            => throw new NotSupportedException("the gate never runs the preflight");
    }

    private IDesignBuildEstimateGate BuildGate(long highMillis) =>
        new DesignBuildEstimateGate(_db, new BuildEstimate(highMillis), new ConfigurationBuilder().Build());

    /// <summary>The step that makes a workflow the profile build to the gate (with a paid Agent step): a DesignSystem Compose.</summary>
    private const string ComposeConfig =
        """{"op":"Compose","version":1,"compose":{"parseStepOrder":1,"designFallbackStepOrders":[8],"productFallbackStepOrders":[4,5,6,7],"ingest":true}}""";

    /// <summary>The built-in agents of the catalog profile-build template, which the gate's reference (the cap) is priced over.</summary>
    private async Task EnsureTemplateAgentsAsync()
    {
        WorkflowTemplateDefinition def = WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey)!;
        foreach (AgentType type in def.Steps.Select(x => x.AgentType)
            .Concat(def.Steps.Where(x => x.ParallelAgentTypes is { Count: > 0 }).SelectMany(x => x.ParallelAgentTypes!)).Distinct())
        {
            if (!await _db.AgentDefinitions.AnyAsync(a => a.IsBuiltIn && a.AgentType == type))
                _db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = type.ToString(), Description = "d", SystemPrompt = "p", AgentType = type, IsBuiltIn = true, CreatedAt = DateTime.UtcNow });
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task AddComposeStepAsync(Guid definitionId)
    {
        Guid agentId = await _db.WorkflowSteps.Where(x => x.WorkflowDefinitionId == definitionId).Select(x => x.AgentDefinitionId).FirstAsync();
        _db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definitionId,
            StepOrder = 3,
            StepType = StepType.DesignSystem,
            AgentDefinitionId = agentId,
            DesignSystemConfigJson = ComposeConfig
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    /// <summary>
    /// A Failed run with an engine-taken snapshot, where the RUN's snapshot (<paramref name="snapshotIsBuild"/>) and the
    /// workflow as it is NOW (<paramref name="sourceIsBuild"/>) each have the profile-build shape or not: the shape of an
    /// edit made after the run. <paramref name="workflowDeleted"/>: the run is filed under its snapshot (T-002).
    /// </summary>
    private async Task<Seeded> SeedProfileBuildRunAsync(
        long? balanceMillis = null, bool snapshotIsBuild = true, bool sourceIsBuild = true, bool workflowDeleted = false)
    {
        Seeded s = await SeedAsync(ExecutionStatus.Failed, balanceMillis: balanceMillis, workflowDeleted: workflowDeleted);
        await EnsureTemplateAgentsAsync();
        if (snapshotIsBuild)
            await AddComposeStepAsync(s.Snapshot.Id);
        if (sourceIsBuild && !workflowDeleted)
            await AddComposeStepAsync(s.Workflow.Id);
        return s;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_profile_build_run_whose_workflow_was_then_edited_into_an_ordinary_one_is_still_capped(bool resume)
    {
        Seeded s = await SeedProfileBuildRunAsync(snapshotIsBuild: true, sourceIsBuild: false);
        WorkflowsController controller = Controller(s.Owner, designBuildGate: BuildGate(231_000));

        ActionResult<WorkflowExecutionResponse> result = resume
            ? await controller.ResumeExecution(s.Project.Id, s.Run.WorkflowDefinitionId, s.Run.Id, null, CancellationToken.None)
            : await controller.RetryExecution(s.Project.Id, s.Run.WorkflowDefinitionId, s.Run.Id, null, CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
        CodeOf(result.Result).Should().Be("ESTIMATE_NOT_ACCEPTED", "the run executes its snapshot, which is the profile build");
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), ExecutionStatus.Failed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_ordinary_run_whose_workflow_was_then_edited_into_a_profile_build_is_not_gated(bool resume)
    {
        Seeded s = await SeedProfileBuildRunAsync(snapshotIsBuild: false, sourceIsBuild: true);
        // The live workflow would be refused at this estimate; the run's own snapshot is ordinary.
        WorkflowsController controller = Controller(s.Owner, designBuildGate: BuildGate(999_000));

        ActionResult<WorkflowExecutionResponse> result = resume
            ? await controller.ResumeExecution(s.Project.Id, s.Run.WorkflowDefinitionId, s.Run.Id, null, CancellationToken.None)
            : await controller.RetryExecution(s.Project.Id, s.Run.WorkflowDefinitionId, s.Run.Id, null, CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(resume ? 202 : 200);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_run_of_a_deleted_profile_build_workflow_is_still_capped_on_Retry_and_Resume(bool resume)
    {
        // After the delete the run is filed under its own (kind 5) snapshot, which is also the route's workflow id.
        Seeded s = await SeedProfileBuildRunAsync(snapshotIsBuild: true, workflowDeleted: true);
        s.Run.WorkflowDefinitionId.Should().Be(s.Snapshot.Id);
        WorkflowsController controller = Controller(s.Owner, designBuildGate: BuildGate(301_000));

        ActionResult<WorkflowExecutionResponse> result = resume
            ? await controller.ResumeExecution(s.Project.Id, s.Snapshot.Id, s.Run.Id, new ResumeExecutionRequest(301), CancellationToken.None)
            : await controller.RetryExecution(s.Project.Id, s.Snapshot.Id, s.Run.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 301), CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
        CodeOf(result.Result).Should().Be("ESTIMATE_OVER_CAP");
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), ExecutionStatus.Failed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_legacy_run_without_an_engine_snapshot_is_gated_on_the_live_workflow(bool buildShapedLive)
    {
        // A legacy run (archive snapshot, owned by no run): Retry judges the live workflow, as before.
        Seeded s = await SeedAsync(ExecutionStatus.Failed, SnapshotShape.LegacyArchive);
        await EnsureTemplateAgentsAsync();
        await AddComposeStepAsync(s.Snapshot.Id);
        if (buildShapedLive)
            await AddComposeStepAsync(s.Workflow.Id);

        ActionResult<WorkflowExecutionResponse> result = await Controller(s.Owner, designBuildGate: BuildGate(231_000))
            .RetryExecution(s.Project.Id, s.Workflow.Id, s.Run.Id, null, CancellationToken.None);

        if (buildShapedLive)
            CodeOf(result.Result).Should().Be("ESTIMATE_NOT_ACCEPTED");
        else
            ((ObjectResult)result.Result!).StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Resume_of_a_profile_build_run_over_the_cap_is_409_ESTIMATE_OVER_CAP_and_changes_nothing()
    {
        Seeded s = await SeedProfileBuildRunAsync();
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await Controller(s.Owner, publish.Object, designBuildGate: BuildGate(301_000))
            .ResumeExecution(s.Project.Id, s.Run.WorkflowDefinitionId, s.Run.Id, new ResumeExecutionRequest(301), CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
        CodeOf(result.Result).Should().Be("ESTIMATE_OVER_CAP");
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), ExecutionStatus.Failed);
        publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Resume_of_a_profile_build_run_without_acceptance_is_409_ESTIMATE_NOT_ACCEPTED_and_with_it_is_accepted()
    {
        Seeded s = await SeedProfileBuildRunAsync();
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> refused = await Controller(s.Owner, publish.Object, designBuildGate: BuildGate(231_000))
            .ResumeExecution(s.Project.Id, s.Run.WorkflowDefinitionId, s.Run.Id, null, CancellationToken.None);
        CodeOf(refused.Result).Should().Be("ESTIMATE_NOT_ACCEPTED");
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), ExecutionStatus.Failed);

        ActionResult<WorkflowExecutionResponse> accepted = await Controller(s.Owner, publish.Object, designBuildGate: BuildGate(231_000))
            .ResumeExecution(s.Project.Id, s.Run.WorkflowDefinitionId, s.Run.Id, new ResumeExecutionRequest(231), CancellationToken.None);
        ((ObjectResult)accepted.Result!).StatusCode.Should().Be(202);
        (await ReloadAsync(s.Run.Id)).PendingResume.Should().BeTrue();
    }

    [Fact]
    public async Task Resume_of_an_ordinary_run_ignores_the_estimate_gate()
    {
        Seeded s = await SeedAsync();

        // The gate would refuse the template at this estimate; an ordinary workflow is never priced by it.
        ActionResult<WorkflowExecutionResponse> result = await Controller(s.Owner, designBuildGate: BuildGate(999_000))
            .ResumeExecution(s.Project.Id, s.Run.WorkflowDefinitionId, s.Run.Id, null, CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(202);
    }

    [Fact]
    public async Task Resume_of_a_profile_build_run_answers_429_then_the_estimate_gate_then_the_credit_402()
    {
        // A zero balance with billing on, so the credit 402 is armed behind every step.
        Seeded s = await SeedProfileBuildRunAsync(balanceMillis: 0);

        // At the plan's concurrency cap, without acceptance: the 429 wins over the estimate gate.
        Mock<IPlanGateService> plan = new();
        plan.Setup(p => p.CheckExecuteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanGateViolation("PLAN_LIMIT_REACHED", "max_concurrent_executions", "One at a time.", 429, 1, 1));
        ActionResult<WorkflowExecutionResponse> atCap = await Controller(s.Owner, billingEnabled: true, planGate: plan.Object, designBuildGate: BuildGate(231_000))
            .ResumeExecution(s.Project.Id, s.Run.WorkflowDefinitionId, s.Run.Id, null, CancellationToken.None);
        ((ObjectResult)atCap.Result!).StatusCode.Should().Be(429);

        // Below the cap, without acceptance: the estimate gate wins over the 402.
        ActionResult<WorkflowExecutionResponse> notAccepted = await Controller(s.Owner, billingEnabled: true, designBuildGate: BuildGate(231_000))
            .ResumeExecution(s.Project.Id, s.Run.WorkflowDefinitionId, s.Run.Id, null, CancellationToken.None);
        CodeOf(notAccepted.Result).Should().Be("ESTIMATE_NOT_ACCEPTED");

        // Accepted: only now the credit preflight answers.
        ActionResult<WorkflowExecutionResponse> unaffordable = await Controller(s.Owner, billingEnabled: true, designBuildGate: BuildGate(231_000))
            .ResumeExecution(s.Project.Id, s.Run.WorkflowDefinitionId, s.Run.Id, new ResumeExecutionRequest(231), CancellationToken.None);
        ((ObjectResult)unaffordable.Result!).StatusCode.Should().Be(402);
        CodeOf(unaffordable.Result).Should().Be("INSUFFICIENT_CREDITS");
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), ExecutionStatus.Failed);
    }

    // ---------------------------------------------------------------- the execution DTO

    [Fact]
    public async Task The_execution_dto_says_canResume_only_for_a_failed_run_with_an_engine_taken_snapshot()
    {
        Seeded resumable = await SeedAsync(ExecutionStatus.Failed);
        Seeded legacy = await SeedAsync(ExecutionStatus.Failed, SnapshotShape.LegacyArchive);
        Seeded passed = await SeedAsync(ExecutionStatus.Passed);

        async Task<WorkflowExecutionResponse> GetAsync(Seeded s) =>
            (WorkflowExecutionResponse)((OkObjectResult)(await Controller(s.Owner).GetExecution(s.Project.Id, s.Run.Id, CancellationToken.None)).Result!).Value!;

        (await GetAsync(resumable)).CanResume.Should().BeTrue();
        (await GetAsync(legacy)).CanResume.Should().BeFalse();
        (await GetAsync(passed)).CanResume.Should().BeFalse();

        List<WorkflowExecutionResponse> listed = (List<WorkflowExecutionResponse>)((OkObjectResult)(await Controller(resumable.Owner)
            .ListExecutions(resumable.Project.Id, resumable.Workflow.Id, CancellationToken.None)).Result!).Value!;
        listed.Should().ContainSingle().Which.CanResume.Should().BeTrue();
        List<WorkflowExecutionResponse> listedLegacy = (List<WorkflowExecutionResponse>)((OkObjectResult)(await Controller(legacy.Owner)
            .ListExecutions(legacy.Project.Id, legacy.Workflow.Id, CancellationToken.None)).Result!).Value!;
        listedLegacy.Should().ContainSingle().Which.CanResume.Should().BeFalse();
    }

    // QA pin (PR #619 verification, uncommitted): the POST itself refuses a replay run, before any write or publish.
    [Fact]
    public async Task Qa_resume_of_a_replay_run_is_409_REVISION_RESUME_NOT_SUPPORTED_and_changes_nothing()
    {
        Seeded s = await SeedAsync(ExecutionStatus.Failed);
        (await _db.WorkflowDefinitions.SingleAsync(w => w.Id == s.Workflow.Id)).Kind = WorkflowDefinitionKind.CompositionReplay;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await ResumeAsync(s, Controller(s.Owner, publish.Object));

        ConflictObjectResult conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        CodeOf(conflict).Should().Be("REVISION_RESUME_NOT_SUPPORTED");
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), ExecutionStatus.Failed);
        publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_replay_run_is_never_canResume_in_the_list_or_the_detail()
    {
        Seeded s = await SeedAsync(ExecutionStatus.Failed);
        (await _db.WorkflowDefinitions.SingleAsync(w => w.Id == s.Workflow.Id)).Kind = WorkflowDefinitionKind.CompositionReplay;
        await _db.SaveChangesAsync();

        List<WorkflowExecutionResponse> listed = (List<WorkflowExecutionResponse>)((OkObjectResult)(await Controller(s.Owner)
            .ListExecutions(s.Project.Id, s.Workflow.Id, CancellationToken.None)).Result!).Value!;
        listed.Should().ContainSingle().Which.CanResume.Should().BeFalse();
        ((WorkflowExecutionResponse)((OkObjectResult)(await Controller(s.Owner).GetExecution(s.Project.Id, s.Run.Id, CancellationToken.None)).Result!).Value!)
            .CanResume.Should().BeFalse();
    }

    [Fact]
    public async Task The_execution_dtos_leave_out_superseded_results()
    {
        Seeded s = await SeedAsync(ExecutionStatus.Failed);
        await _db.WorkflowStepResults.Where(r => r.WorkflowExecutionId == s.Run.Id && r.Status == StepStatus.Failed)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.SupersededAt, (DateTime?)DateTime.UtcNow));

        WorkflowExecutionResponse got = (WorkflowExecutionResponse)((OkObjectResult)(await Controller(s.Owner)
            .GetExecution(s.Project.Id, s.Run.Id, CancellationToken.None)).Result!).Value!;
        List<WorkflowExecutionResponse> listed = (List<WorkflowExecutionResponse>)((OkObjectResult)(await Controller(s.Owner)
            .ListExecutions(s.Project.Id, s.Workflow.Id, CancellationToken.None)).Result!).Value!;

        got.StepResults.Should().ContainSingle().Which.Status.Should().Be("Completed");
        listed.Single().StepResults.Should().ContainSingle().Which.Status.Should().Be("Completed");
    }

    // ---------------------------------------------------------------- Retry (Retry)

    [Fact]
    public async Task Retry_supersedes_every_result_instead_of_deleting_and_restarts_from_step_1()
    {
        Seeded s = await SeedAsync(ExecutionStatus.Failed);
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await RetryAsync(s, Controller(s.Owner, publish.Object));

        ObjectResult ok = result.Result.Should().BeOfType<ObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        ok.Value.Should().BeOfType<WorkflowExecutionResponse>().Which.StepResults.Should().BeEmpty();

        WorkflowExecution after = await ReloadAsync(s.Run.Id);
        after.Status.Should().Be(ExecutionStatus.Queued);
        after.PendingResume.Should().BeFalse();
        after.ResumeCount.Should().Be(0, "a retry is a restart, not a resume");
        after.StartedAt.Should().BeNull();
        after.StepResults.Should().HaveCount(2).And.OnlyContain(r => r.SupersededAt != null);
        publish.Verify(p => p.Publish(It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == s.Run.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Retry_clears_a_pending_resume()
    {
        Seeded s = await SeedAsync(ExecutionStatus.Cancelled);
        await _db.WorkflowExecutions.Where(e => e.Id == s.Run.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.PendingResume, true));

        await RetryAsync(s, Controller(s.Owner));

        (await ReloadAsync(s.Run.Id)).PendingResume.Should().BeFalse();
    }

    [Fact]
    public async Task Retry_is_402_when_the_balance_cannot_cover_the_run_and_changes_nothing()
    {
        Seeded s = await SeedAsync(balanceMillis: 0);
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await RetryAsync(s, Controller(s.Owner, publish.Object, billingEnabled: true));

        ObjectResult refusal = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(402);
        CodeOf(refusal).Should().Be("INSUFFICIENT_CREDITS");
        WorkflowExecution after = await ReloadAsync(s.Run.Id);
        ShouldBeUnchanged(after, ExecutionStatus.Failed);
        after.StartedAt.Should().Be(OriginalStart);
        publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Retry_of_a_legacy_run_whose_workflow_is_still_there_is_accepted()
    {
        Seeded s = await SeedAsync(ExecutionStatus.Failed, SnapshotShape.LegacyArchive);

        ActionResult<WorkflowExecutionResponse> result = await RetryAsync(s, Controller(s.Owner));

        ((ObjectResult)result.Result!).StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Retry_of_a_deleted_workflows_run_runs_its_own_snapshot()
    {
        Seeded s = await SeedAsync(ExecutionStatus.Failed, workflowDeleted: true);

        ActionResult<WorkflowExecutionResponse> result = await RetryAsync(s, Controller(s.Owner));

        ((ObjectResult)result.Result!).StatusCode.Should().Be(200);
        (await ReloadAsync(s.Run.Id)).Status.Should().Be(ExecutionStatus.Queued);
    }

    [Fact]
    public async Task Retry_of_a_deleted_workflows_run_with_only_a_legacy_archive_is_409_RETRY_UNAVAILABLE()
    {
        Seeded s = await SeedAsync(ExecutionStatus.Failed, SnapshotShape.LegacyArchive, workflowDeleted: true);
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await RetryAsync(s, Controller(s.Owner, publish.Object));

        result.Result.Should().BeOfType<ConflictObjectResult>();
        CodeOf(result.Result).Should().Be("RETRY_UNAVAILABLE");
        ShouldBeUnchanged(await ReloadAsync(s.Run.Id), ExecutionStatus.Failed);
        publish.Invocations.Should().BeEmpty();
    }
}
