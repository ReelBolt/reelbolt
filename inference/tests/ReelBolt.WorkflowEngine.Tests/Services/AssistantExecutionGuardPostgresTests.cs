#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// S-8, S-9, S-10 (billing-concurrency-followups T-003): the hourly run limit of
/// <see cref="AssistantExecutionGuard"/> (counted from <c>run_rate_events</c>, created by the real API migrations) on a real PostgreSQL. Two "replicas" are two
/// <see cref="InferenceApiDbContext"/>s on two distinct <see cref="NpgsqlDataSource"/>s, each with its
/// own process-local semaphores, so only the database lock can serialise them. Env-gated
/// (<see cref="PostgresTestEnvironment"/>): without a server every test returns early.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class AssistantExecutionGuardPostgresTests
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

            string name = "reelbolt_guard_test_" + Guid.NewGuid().ToString("N")[..12];
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

        public async Task<int> RowCountAsync(Guid user, Guid project)
        {
            await using InferenceApiDbContext db = NewDb();
            return await db.RunRateEvents.CountAsync(e => e.UserId == user && e.ProjectId == project);
        }

        /// <summary>Advisory locks held by any session on this test's database.</summary>
        public async Task<long> AdvisoryLockCountAsync()
        {
            await using NpgsqlDataSource source = NpgsqlDataSource.Create(_connectionString);
            await using NpgsqlCommand cmd = source.CreateCommand(
                "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = current_database())");
            return (long)(await cmd.ExecuteScalarAsync())!;
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

    /// <summary>Fails every statement whose SQL contains <c>match</c>, as a broken lock call would.</summary>
    private sealed class FailAdvisoryLockInterceptor(string match = "advisory") : DbCommandInterceptor
    {
        private void Fail(DbCommand command)
        {
            if (command.CommandText.Contains(match, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("forced advisory lock failure");
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static IConfiguration Config(int max, int? lockWaitMs = null)
    {
        Dictionary<string, string?> values = new() { ["Assistant:MaxPaidExecutionsPerHour"] = max.ToString() };
        if (lockWaitMs is { } ms)
        {
            values["Assistant:ExecutionGuardLockWaitMilliseconds"] = ms.ToString();
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static RunRateEvent Event(Guid user, Guid project, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = user,
        ProjectId = project,
        Bucket = RunRateEvent.PaidBucket,
        Source = RunRateEvent.ExecuteSource,
        CreatedAt = createdAt,
    };

    private static Task<RunRateDecision> Acquire(AssistantExecutionGuard guard, Guid user, Guid project, Func<Task<Guid?>> callback, CancellationToken ct = default) =>
        guard.TryAcquireAsync(user, project, RunRateBucket.Paid, RunRateEvent.ExecuteSource, callback, ct);

    /// <summary>A guard with its own process-local semaphores, i.e. a separate replica.</summary>
    private static AssistantExecutionGuard Replica(InferenceApiDbContext db, IConfiguration config)
        => new(db, config, logger: null, new AssistantExecutionGuardProcessLocks());

    [Fact]
    public async Task Two_guards_on_separate_pools_racing_20_calls_insert_exactly_max_rows()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null)
        {
            return;
        }

        Guid user = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        IConfiguration config = Config(max: 3);
        NpgsqlDataSource[] replicas = [pg.NewDataSource(), pg.NewDataSource()];
        AssistantExecutionGuardProcessLocks[] replicaLocks = [new(), new()];

        using Barrier barrier = new(20);
        Task<RunRateDecision>[] calls = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            int replica = i % 2;
            await using InferenceApiDbContext db = pg.NewDb(replicas[replica]);
            AssistantExecutionGuard guard = new(db, config, logger: null, replicaLocks[replica]);
            barrier.SignalAndWait();
            return await Acquire(guard, user, project, async () =>
            {
                // Widen the count-then-insert window, so an unserialised pair would both insert.
                await Task.Delay(50);
                return Guid.NewGuid();
            });
        })).ToArray();

        RunRateDecision[] results = await Task.WhenAll(calls);

        results.Count(r => r.Allowed).Should().Be(3);
        (await pg.RowCountAsync(user, project)).Should().Be(3);
    }

    [Fact]
    public async Task Events_in_the_window_count_toward_the_limit()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null)
        {
            return;
        }

        Guid user = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        await using (InferenceApiDbContext seed = pg.NewDb())
        {
            for (int i = 0; i < 3; i++)
            {
                seed.RunRateEvents.Add(Event(user, project, DateTime.UtcNow.AddMinutes(-10)));
            }

            await seed.SaveChangesAsync();
        }

        await using InferenceApiDbContext db = pg.NewDb();
        bool invoked = false;
        RunRateDecision decision = await Acquire(Replica(db, Config(max: 3)), user, project, () =>
        {
            invoked = true;
            return Task.FromResult<Guid?>(Guid.NewGuid());
        });

        decision.Allowed.Should().BeFalse();
        decision.Used.Should().Be(3);
        decision.RetryAfterSeconds.Should().BeInRange(49 * 60, 50 * 60 + 1);
        invoked.Should().BeFalse();
    }

    [Fact]
    public async Task A_lock_failure_inserts_nothing_and_opens_no_transaction()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null)
        {
            return;
        }

        Guid user = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        await using InferenceApiDbContext db = pg.NewDb(source: null, new FailAdvisoryLockInterceptor());
        bool invoked = false;
        bool? allowed = null;
        Exception? thrown = null;
        try
        {
            allowed = (await Acquire(Replica(db, Config(max: 3)), user, project, () =>
            {
                invoked = true;
                return Task.FromResult<Guid?>(Guid.NewGuid());
            })).Allowed;
        }
        catch (Exception e)
        {
            thrown = e;
        }

        (thrown is not null || allowed == false).Should().BeTrue("a failed lock must throw or refuse, never allow");
        invoked.Should().BeFalse();
        db.Database.CurrentTransaction.Should().BeNull();
        (await pg.RowCountAsync(user, project)).Should().Be(0);
    }

    [Fact]
    public async Task A_lock_held_past_the_wait_fails_closed()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null)
        {
            return;
        }

        Guid user = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        NpgsqlDataSource other = pg.NewDataSource();
        await using NpgsqlConnection holder = await other.OpenConnectionAsync();
        await using (NpgsqlCommand take = new("SELECT pg_advisory_lock(@k)", holder))
        {
            take.Parameters.AddWithValue("k", AssistantExecutionGuard.ComputeLockKey(user, project));
            await take.ExecuteNonQueryAsync();
        }

        await using InferenceApiDbContext db = pg.NewDb();
        bool invoked = false;
        RunRateDecision decision = await Acquire(Replica(db, Config(max: 3, lockWaitMs: 200)), user, project, () =>
        {
            invoked = true;
            return Task.FromResult<Guid?>(Guid.NewGuid());
        });

        decision.Allowed.Should().BeFalse();
        decision.Busy.Should().BeTrue();
        invoked.Should().BeFalse();
    }

    [Fact]
    public async Task The_event_is_committed_when_the_call_returns()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null)
        {
            return;
        }

        Guid user = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        await using InferenceApiDbContext db = pg.NewDb();
        int seenByAnotherConnection = -1;
        RunRateDecision decision = await Acquire(Replica(db, Config(max: 3)), user, project, async () =>
        {
            // This is where the real callback publishes. The run's own row is committed by its service
            // before it returns; the guard records the event right after, under the same lock.
            seenByAnotherConnection = await pg.RowCountAsync(user, project);
            return Guid.NewGuid();
        });

        decision.Allowed.Should().BeTrue();
        seenByAnotherConnection.Should().Be(0);
        (await pg.RowCountAsync(user, project)).Should().Be(1, "the event is committed when the call returns");
    }

    [Fact]
    public async Task The_session_lock_is_released_after_the_call()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null)
        {
            return;
        }

        Guid user = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        NpgsqlDataSource source = pg.NewDataSource();

        await using (InferenceApiDbContext db = pg.NewDb(source))
        {
            long heldDuringCallback = -1;
            RunRateDecision decision = await Acquire(Replica(db, Config(max: 3)), user, project, async () =>
            {
                heldDuringCallback = await pg.AdvisoryLockCountAsync();
                return Guid.NewGuid();
            });

            decision.Allowed.Should().BeTrue();
            heldDuringCallback.Should().Be(1, "the guard holds its session lock while the callback runs");
        }

        (await pg.AdvisoryLockCountAsync()).Should().Be(0);

        await using (InferenceApiDbContext db = pg.NewDb(source))
        {
            Func<Task> call = () => Acquire(
                Replica(db, Config(max: 3)), user, project, () => throw new InvalidOperationException("callback failed"));
            await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("callback failed");
        }

        (await pg.AdvisoryLockCountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unlock_failure_does_not_leave_the_session_lock_in_the_pool(bool onDataSource)
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null)
        {
            return;
        }

        Guid user = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        NpgsqlDataSource? source = onDataSource ? pg.NewDataSource() : null;
        await using (InferenceApiDbContext db = pg.NewDb(source, new FailAdvisoryLockInterceptor("pg_advisory_unlock")))
        {
            RunRateDecision decision = await Acquire(Replica(db, Config(max: 3)), user, project, () => Task.FromResult<Guid?>(Guid.NewGuid()));

            decision.Allowed.Should().BeTrue("the run is committed; a failed unlock must not turn it into an error");
        }

        // The pooled session that still holds the lock must be discarded, not reused.
        long held = -1;
        for (int i = 0; i < 40 && held != 0; i++)
        {
            held = await pg.AdvisoryLockCountAsync();
            if (held != 0)
            {
                await Task.Delay(50);
            }
        }

        held.Should().Be(0);
        (await pg.RowCountAsync(user, project)).Should().Be(1);
    }
}
