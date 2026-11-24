#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
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
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// S-49, S-50 (billing-concurrency-followups round 3, T-018): <c>POST …/executions/{id}/retry</c> on a
/// real PostgreSQL. While the stopped run holds its lease the retry answers 409 <c>RUN_STILL_STOPPING</c>
/// and changes nothing; once the lease lapses it re-queues with one conditional UPDATE, and a lease that
/// is renewed between the check and that UPDATE is never nulled. Each test is a
/// <c>[PostgresFact]</c>: without a server it is skipped, not passed.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class RetryWhileStoppingPostgresTests
{
    /// <summary>One migrated database (API + engine), FKs of workflow_executions dropped so rows need no parents.</summary>
    private sealed class Pg : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _admin;
        private readonly string _name;
        private readonly string _connectionString;
        private readonly List<NpgsqlDataSource> _sources = new();

        private Pg(NpgsqlDataSource admin, string name, string connectionString)
        {
            _admin = admin;
            _name = name;
            _connectionString = connectionString;
        }

        public static async Task<Pg?> CreateAsync()
        {
            string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
            if (string.IsNullOrWhiteSpace(baseConnection))
            {
                return null;
            }

            string name = "reelbolt_retry_test_" + Guid.NewGuid().ToString("N")[..12];
            NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {name}"))
            {
                await create.ExecuteNonQueryAsync();
            }

            Pg pg = new(admin, name, PostgresTestEnvironment.ForDatabase(baseConnection, name, maxPoolSize: 5));
            await using (InferenceApiDbContext api = pg.NewDb())
            {
                await api.GetService<IMigrator>().MigrateAsync();
            }

            // workflow_executions is engine-owned (ExcludeFromMigrations in the API context).
            await using (WorkflowEngineDbContext engine = new(
                new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(pg._connectionString).Options))
            {
                await engine.Database.MigrateAsync();
            }

            await using (InferenceApiDbContext db = pg.NewDb())
            {
                await db.Database.ExecuteSqlRawAsync("""
                    DO $$ DECLARE r record; BEGIN
                      FOR r IN SELECT conname FROM pg_constraint
                               WHERE conrelid = 'workflow_executions'::regclass AND contype = 'f' LOOP
                        EXECUTE format('ALTER TABLE workflow_executions DROP CONSTRAINT %I', r.conname);
                      END LOOP;
                    END $$;
                    """);
            }

            return pg;
        }

        /// <summary>A fresh data source: a separate pool, as a second replica would have.</summary>
        public NpgsqlDataSource NewDataSource()
        {
            NpgsqlDataSource source = NpgsqlDataSource.Create(_connectionString);
            _sources.Add(source);
            return source;
        }

        public InferenceApiDbContext NewDb(NpgsqlDataSource? source = null, params IInterceptor[] interceptors)
        {
            DbContextOptionsBuilder<InferenceApiDbContext> builder = new();
            if (source is null)
            {
                builder.UseNpgsql(_connectionString);
            }
            else
            {
                builder.UseNpgsql(source);
            }

            if (interceptors.Length > 0)
            {
                builder.AddInterceptors(interceptors);
            }

            return new InferenceApiDbContext(builder.Options);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (NpgsqlDataSource source in _sources)
            {
                await source.DisposeAsync();
            }

            NpgsqlConnection.ClearAllPools();
            await using (NpgsqlCommand drop = _admin.CreateCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)"))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await _admin.DisposeAsync();
        }
    }

    /// <summary>
    /// Renews the lease on another pool just before the controller's first UPDATE of workflow_executions,
    /// as the engine's heartbeat would if the stopped run were still alive.
    /// </summary>
    private sealed class RenewBeforeUpdateInterceptor(NpgsqlDataSource other, Guid id) : DbCommandInterceptor
    {
        private bool _renewed;

        private async ValueTask RenewBeforeFirstUpdateAsync(DbCommand command)
        {
            if (_renewed
                || !command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !command.CommandText.Contains("workflow_executions", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _renewed = true;
            await using NpgsqlCommand renew = other.CreateCommand(
                "UPDATE workflow_executions SET lease_expires_at = now() + interval '60 seconds', heartbeat_at = now() WHERE id = @id AND lease_owner = 'engine-a'");
            renew.Parameters.AddWithValue("id", id);
            await renew.ExecuteNonQueryAsync();
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await RenewBeforeFirstUpdateAsync(command);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await RenewBeforeFirstUpdateAsync(command);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            await RenewBeforeFirstUpdateAsync(command);
            return await base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed record Seeded(Guid User, Guid Org, Guid Project, Guid Workflow, Guid Run);

    /// <summary>A Team org with an Owner, a project, and a Cancelled run held by engine-a until <paramref name="leaseExpiresAt"/>.</summary>
    private static async Task<Seeded> SeedStoppedRunAsync(Pg pg, DateTime leaseExpiresAt)
    {
        Guid user = Guid.NewGuid();
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        Guid workflow = Guid.NewGuid();
        Guid run = Guid.NewGuid();
        await using InferenceApiDbContext db = pg.NewDb();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = $"{user:N}@example.test", DisplayName = "owner", EmailVerifiedAt = DateTime.UtcNow });
        db.Organizations.Add(new Organization { Id = org, Name = "o", Slug = "o-" + org.ToString("N"), Kind = OrganizationKind.Team });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = org, UserId = user, Role = OrganizationRole.Owner });
        db.Projects.Add(new Project { Id = project, Name = "p", OwnerId = user, OrganizationId = org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = run,
            WorkflowDefinitionId = workflow,
            ProjectId = project,
            OrganizationId = org,
            InitiatedByUserId = user,
            Status = ExecutionStatus.Cancelled,
            ErrorMessage = "Stopped by user x",
            UserRequest = "r",
            QueuedAt = DateTime.UtcNow.AddMinutes(-5),
            StartedAt = DateTime.UtcNow.AddMinutes(-4),
            LeaseOwner = "engine-a",
            LeaseExpiresAt = leaseExpiresAt,
        });
        await db.SaveChangesAsync();
        return new Seeded(user, org, project, workflow, run);
    }

    /// <summary>The controller as the project's Owner, on the given context.</summary>
    private static WorkflowsController OwnerController(InferenceApiDbContext db, Guid user, Guid org, IPublishEndpoint publish)
    {
        var current = new OwnerUser(user, org);
        WorkflowTemplateProvisioningService templateService = new(
            db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build());
        IWorkflowExecutionService executionService = new WorkflowExecutionService(
            db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance);
        return new WorkflowsController(
            db, current, TestTenancy.Access(db, current), publish, templateService, executionService,
            NullLogger<WorkflowsController>.Instance);
    }

    private static readonly object RunStillStopping = new { code = "RUN_STILL_STOPPING", message = "This run is still stopping. Try again in a moment." };

    [PostgresFact]
    public async Task Retry_while_the_stopped_run_holds_its_lease_is_409_and_after_it_lapses_is_accepted()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null)
        {
            return;
        }

        Seeded s = await SeedStoppedRunAsync(pg, DateTime.UtcNow.AddSeconds(60));
        Mock<IPublishEndpoint> publish = new();

        await using (InferenceApiDbContext first = pg.NewDb())
        {
            ActionResult<WorkflowExecutionResponse> refused = await OwnerController(first, s.User, s.Org, publish.Object)
                .RetryExecution(s.Project, s.Workflow, s.Run, null, CancellationToken.None);

            refused.Result.Should().BeOfType<ConflictObjectResult>().Which.Value.Should().BeEquivalentTo(RunStillStopping);
        }

        await using (InferenceApiDbContext check = pg.NewDb())
        {
            WorkflowExecution row = await check.WorkflowExecutions.SingleAsync(e => e.Id == s.Run);
            row.Status.Should().Be(ExecutionStatus.Cancelled);
            row.LeaseOwner.Should().Be("engine-a");
            row.LeaseExpiresAt.Should().BeAfter(DateTime.UtcNow);
        }
        publish.Verify(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()), Times.Never);

        await using (InferenceApiDbContext lapse = pg.NewDb())
        {
            await lapse.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_executions SET lease_expires_at = now() - interval '1 second' WHERE id = {s.Run}");
        }

        await using (InferenceApiDbContext second = pg.NewDb())
        {
            ActionResult<WorkflowExecutionResponse> accepted = await OwnerController(second, s.User, s.Org, publish.Object)
                .RetryExecution(s.Project, s.Workflow, s.Run, null, CancellationToken.None);

            ((ObjectResult)accepted.Result!).StatusCode.Should().Be(200);
        }

        await using (InferenceApiDbContext check = pg.NewDb())
        {
            WorkflowExecution row = await check.WorkflowExecutions.SingleAsync(e => e.Id == s.Run);
            row.Status.Should().Be(ExecutionStatus.Queued);
            row.LeaseOwner.Should().BeNull();
            row.LeaseExpiresAt.Should().BeNull();
            row.ErrorMessage.Should().BeNull();
        }
        publish.Verify(p => p.Publish(It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == s.Run), It.IsAny<CancellationToken>()), Times.Once);
    }

    [PostgresFact]
    public async Task A_lease_renewed_between_the_check_and_the_write_is_never_nulled()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null)
        {
            return;
        }

        Seeded s = await SeedStoppedRunAsync(pg, DateTime.UtcNow.AddSeconds(-5));
        Mock<IPublishEndpoint> publish = new();
        NpgsqlDataSource other = pg.NewDataSource();

        await using InferenceApiDbContext db = pg.NewDb(source: null, new RenewBeforeUpdateInterceptor(other, s.Run));
        ActionResult<WorkflowExecutionResponse> result = await OwnerController(db, s.User, s.Org, publish.Object)
            .RetryExecution(s.Project, s.Workflow, s.Run, null, CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>().Which.Value.Should().BeEquivalentTo(RunStillStopping);
        await using (InferenceApiDbContext check = pg.NewDb())
        {
            WorkflowExecution row = await check.WorkflowExecutions.SingleAsync(e => e.Id == s.Run);
            row.Status.Should().Be(ExecutionStatus.Cancelled);
            row.LeaseOwner.Should().Be("engine-a");
            row.LeaseExpiresAt.Should().BeAfter(DateTime.UtcNow);
        }
        publish.Verify(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The project's Owner in its own Team organization.</summary>
    private sealed class OwnerUser(Guid userId, Guid organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "owner@example.test";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = OrganizationRole.Owner;
    }
}
