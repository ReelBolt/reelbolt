using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// Tests for <see cref="AssistantExecutionGuard"/>.
/// </summary>
public class AssistantExecutionGuardTests
{
    private static (SqliteConnection connection, InferenceApiDbContext context) CreateSqliteContext(DbCommandInterceptor? interceptor = null)
    {
        var connectionString = $"Data Source=:memory:";
        var connection = new SqliteConnection(connectionString);
        connection.Open();

        // Disable foreign key constraints during test setup to simplify schema management
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
        }

        var builder = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(connection);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);

        var context = new InferenceApiDbContext(builder.Options);

        // Create all tables from the model
        context.Database.EnsureCreated();

        // Force-create WorkflowEngine-owned tables (workflow_executions, workflow_definitions, workflow_steps)
        // EnsureCreated() above only creates Inference API tables; EF's database existence check
        // prevents the Engine context's EnsureCreated() from running on a connection that already
        // has tables. Use IRelationalDatabaseCreator to bypass that check and create only missing tables.
        var engineOptions = new DbContextOptionsBuilder<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>()
            .UseSqlite(connection)
            .Options;
        using var engineDb = new ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext(engineOptions);
        var creator = engineDb.GetService<IRelationalDatabaseCreator>();
        creator.CreateTables();

        return (connection, context);
    }

    private static IConfiguration Config(int? paid = null, int? free = null, int? legacy = null, int? lockWaitMs = null)
    {
        var d = new Dictionary<string, string?>();
        if (paid is { } p) d["Assistant:MaxPaidExecutionsPerHour"] = p.ToString();
        if (free is { } f) d["Assistant:MaxFreeExecutionsPerHour"] = f.ToString();
        if (legacy is { } l) d["Assistant:MaxExecutionsPerHour"] = l.ToString();
        if (lockWaitMs is { } w) d["Assistant:ExecutionGuardLockWaitMilliseconds"] = w.ToString();
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    private static async Task SeedAsync(InferenceApiDbContext db, Guid user, Guid project, string bucket, int count, TimeSpan age, string source = "execute")
    {
        for (int i = 0; i < count; i++)
        {
            db.RunRateEvents.Add(new RunRateEvent
            {
                Id = Guid.NewGuid(),
                UserId = user,
                ProjectId = project,
                Bucket = bucket,
                Source = source,
                CreatedAt = DateTime.UtcNow - age,
            });
        }

        await db.SaveChangesAsync();
    }

    private static Task<RunRateDecision> AcquireAsync(
        AssistantExecutionGuard guard, Guid user, Guid project, RunRateBucket bucket, Func<Task<Guid?>>? callback = null, string source = "execute") =>
        guard.TryAcquireAsync(user, project, bucket, source, callback ?? (() => Task.FromResult<Guid?>(Guid.NewGuid())), CancellationToken.None);

    [Fact]
    public async Task The_tenth_paid_run_is_allowed_and_reports_ten_of_ten()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            await SeedAsync(context, user, project, "paid", 9, TimeSpan.FromMinutes(30));

            RunRateDecision decision = await AcquireAsync(guard, user, project, RunRateBucket.Paid);

            decision.Should().Be(new RunRateDecision(true, RunRateBucket.Paid, 10, 10, 3600, 0));
        }
    }

    [Fact]
    public async Task The_eleventh_paid_run_is_refused_with_used_limit_and_retry_after_from_the_oldest_event()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            await SeedAsync(context, user, project, "paid", 9, TimeSpan.FromMinutes(10));
            await SeedAsync(context, user, project, "paid", 1, TimeSpan.FromMinutes(48)); // the oldest: frees at +12 min
            bool callbackRan = false;

            RunRateDecision decision = await AcquireAsync(guard, user, project, RunRateBucket.Paid, () =>
            {
                callbackRan = true;
                return Task.FromResult<Guid?>(Guid.NewGuid());
            });

            decision.Allowed.Should().BeFalse();
            decision.Bucket.Should().Be(RunRateBucket.Paid);
            decision.Used.Should().Be(10);
            decision.Limit.Should().Be(10);
            decision.WindowSeconds.Should().Be(3600);
            decision.RetryAfterSeconds.Should().BeInRange(12 * 60 - 2, 12 * 60 + 1);
            callbackRan.Should().BeFalse();
            (await context.RunRateEvents.CountAsync()).Should().Be(10, "a refusal records nothing");
        }
    }

    [Fact]
    public async Task An_allowed_run_records_one_event_with_its_execution_id_and_source()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            var executionId = Guid.NewGuid();

            RunRateDecision decision = await AcquireAsync(guard, user, project, RunRateBucket.Free, () => Task.FromResult<Guid?>(executionId), "revision");

            decision.Allowed.Should().BeTrue();
            RunRateEvent row = await context.RunRateEvents.SingleAsync();
            row.UserId.Should().Be(user);
            row.ProjectId.Should().Be(project);
            row.Bucket.Should().Be("free");
            row.Source.Should().Be("revision");
            row.ExecutionId.Should().Be(executionId);
        }
    }

    [Fact]
    public async Task A_callback_that_started_no_run_consumes_nothing()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());

            RunRateDecision decision = await AcquireAsync(guard, Guid.NewGuid(), Guid.NewGuid(), RunRateBucket.Paid, () => Task.FromResult<Guid?>(null));

            decision.Allowed.Should().BeTrue();
            decision.Used.Should().Be(0, "the plan gate refused inside the callback; no run started");
            (await context.RunRateEvents.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task Free_runs_never_consume_the_paid_bucket_and_vice_versa()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            await SeedAsync(context, user, project, "free", 60, TimeSpan.FromMinutes(5));
            await SeedAsync(context, user, project, "paid", 10, TimeSpan.FromMinutes(5));

            RunRateDecision freeAtCap = await AcquireAsync(guard, user, project, RunRateBucket.Free);
            RunRateDecision paidAtCap = await AcquireAsync(guard, user, project, RunRateBucket.Paid);
            freeAtCap.Allowed.Should().BeFalse();
            freeAtCap.Limit.Should().Be(60);
            paidAtCap.Allowed.Should().BeFalse();

            var otherProject = Guid.NewGuid();
            await SeedAsync(context, user, otherProject, "free", 60, TimeSpan.FromMinutes(5));
            (await AcquireAsync(guard, user, otherProject, RunRateBucket.Paid)).Allowed.Should().BeTrue("60 free runs leave the paid bucket untouched");
            var third = Guid.NewGuid();
            await SeedAsync(context, user, third, "paid", 10, TimeSpan.FromMinutes(5));
            (await AcquireAsync(guard, user, third, RunRateBucket.Free)).Allowed.Should().BeTrue("10 paid runs leave the free bucket untouched");
        }
    }

    [Fact]
    public async Task The_sixty_first_free_run_is_refused()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            await SeedAsync(context, user, project, "free", 59, TimeSpan.FromMinutes(1));

            (await AcquireAsync(guard, user, project, RunRateBucket.Free)).Should().Match<RunRateDecision>(d => d.Allowed && d.Used == 60 && d.Limit == 60);
            RunRateDecision sixtyFirst = await AcquireAsync(guard, user, project, RunRateBucket.Free);

            sixtyFirst.Allowed.Should().BeFalse();
            sixtyFirst.Used.Should().Be(60);
        }
    }

    [Fact]
    public async Task Events_older_than_an_hour_do_not_count_and_are_other_users_and_projects_independent()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            await SeedAsync(context, user, project, "paid", 10, TimeSpan.FromMinutes(61));
            await SeedAsync(context, Guid.NewGuid(), project, "paid", 10, TimeSpan.FromMinutes(1));
            await SeedAsync(context, user, Guid.NewGuid(), "paid", 10, TimeSpan.FromMinutes(1));

            RunRateDecision decision = await AcquireAsync(guard, user, project, RunRateBucket.Paid);

            decision.Allowed.Should().BeTrue();
            decision.Used.Should().Be(1);
        }
    }

    [Fact]
    public async Task Events_older_than_a_day_are_pruned_when_a_run_is_recorded()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            await SeedAsync(context, user, project, "paid", 3, TimeSpan.FromHours(30));

            await AcquireAsync(guard, user, project, RunRateBucket.Paid);

            (await context.RunRateEvents.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task Human_executions_in_the_engine_table_no_longer_matter()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config(paid: 1));
            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            context.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                ProjectId = project,
                InitiatedByUserId = user,
                Initiator = ExecutionInitiator.Assistant,
                Status = ExecutionStatus.Queued,
                StartedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();

            (await AcquireAsync(guard, user, project, RunRateBucket.Paid)).Allowed.Should().BeTrue("the ledger is run_rate_events, not workflow_executions");
        }
    }

    [Fact]
    public async Task The_legacy_key_is_the_paid_limit_when_it_is_the_only_one_set()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            await SeedAsync(context, user, project, "paid", 3, TimeSpan.FromMinutes(5));

            RunRateDecision legacy = await AcquireAsync(new AssistantExecutionGuard(context, Config(legacy: 3)), user, project, RunRateBucket.Paid);
            RunRateDecision explicitWins = await AcquireAsync(new AssistantExecutionGuard(context, Config(paid: 5, legacy: 3)), user, project, RunRateBucket.Paid);

            legacy.Allowed.Should().BeFalse();
            legacy.Limit.Should().Be(3);
            explicitWins.Allowed.Should().BeTrue();
            explicitWins.Limit.Should().Be(5);
        }
    }

    [Fact]
    public async Task Fifteen_concurrent_paid_acquisitions_never_exceed_the_limit()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            int callbacks = 0;

            RunRateDecision[] results = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ =>
                AcquireAsync(guard, user, project, RunRateBucket.Paid, () =>
                {
                    Interlocked.Increment(ref callbacks);
                    return Task.FromResult<Guid?>(Guid.NewGuid());
                })));

            results.Count(r => r.Allowed).Should().Be(10);
            results.Count(r => !r.Allowed).Should().Be(5);
            callbacks.Should().Be(10);
            (await context.RunRateEvents.CountAsync()).Should().Be(10);
        }
    }

    /// <summary>
    /// S-10: off Postgres the guard serialises with its process semaphore only. It must not open a
    /// transaction the callback's SaveChanges would join and nothing would ever commit.
    /// </summary>
    [Fact]
    public async Task Sqlite_path_opens_no_transaction()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            IDbContextTransaction? duringCallback = null;

            RunRateDecision decision = await AcquireAsync(guard, Guid.NewGuid(), Guid.NewGuid(), RunRateBucket.Paid, () =>
            {
                duringCallback = context.Database.CurrentTransaction;
                return Task.FromResult<Guid?>(Guid.NewGuid());
            });

            decision.Allowed.Should().BeTrue();
            duringCallback.Should().BeNull();
            context.Database.CurrentTransaction.Should().BeNull();
        }
    }

    /// <summary>
    /// QA-8: the in-process (user, project) semaphore is bounded by <c>Assistant:ExecutionGuardLockWaitMilliseconds</c>
    /// too. A call that cannot take it in time is refused (fail closed) and its callback never runs.
    /// </summary>
    [Fact]
    public async Task A_waiter_past_the_lock_wait_on_the_process_semaphore_is_refused_as_busy()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            var config = Config(lockWaitMs: 200);

            // Two guards on one replica: they share the process semaphores, as the production singletons do.
            var processLocks = new AssistantExecutionGuardProcessLocks();
            var holder = new AssistantExecutionGuard(context, config, null, processLocks);
            var waiter = new AssistantExecutionGuard(context, config, null, processLocks);

            var entered = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            Task<RunRateDecision> first = AcquireAsync(holder, user, project, RunRateBucket.Paid, async () =>
            {
                entered.SetResult();
                await release.Task;
                return Guid.NewGuid();
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            bool waiterCallbackRan = false;
            Task<RunRateDecision> second = AcquireAsync(waiter, user, project, RunRateBucket.Paid, () =>
            {
                waiterCallbackRan = true;
                return Task.FromResult<Guid?>(Guid.NewGuid());
            });
            try
            {
                RunRateDecision decision = await second.WaitAsync(TimeSpan.FromSeconds(5));

                decision.Allowed.Should().BeFalse();
                decision.Busy.Should().BeTrue();
                decision.RetryAfterSeconds.Should().BeGreaterThanOrEqualTo(1);
                waiterCallbackRan.Should().BeFalse();
            }
            finally
            {
                release.SetResult();
                await first;
                await second;
            }
        }
    }

    /// <summary>S-11: the run's event is in the ledger before the callback queues and publishes it, so a request that is
    /// aborted after the publish cannot leave a started run uncounted.</summary>
    [Fact]
    public async Task The_event_is_recorded_before_the_callback_queues_and_publishes_the_run()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            int eventsDuringCallback = -1;

            await AcquireAsync(guard, user, project, RunRateBucket.Paid, async () =>
            {
                eventsDuringCallback = await context.RunRateEvents.AsNoTracking()
                    .CountAsync(e => e.UserId == user && e.ProjectId == project && e.Bucket == "paid");
                return Guid.NewGuid();
            });

            eventsDuringCallback.Should().Be(1, "the event is committed under the lock before anything is published");
        }
    }

    [Fact]
    public async Task A_callback_that_fails_to_queue_or_publish_leaves_no_event()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());

            Func<Task> act = () => AcquireAsync(guard, Guid.NewGuid(), Guid.NewGuid(), RunRateBucket.Paid,
                () => throw new InvalidOperationException("broker unreachable"));

            await act.Should().ThrowAsync<InvalidOperationException>();
            (await context.RunRateEvents.AsNoTracking().CountAsync()).Should().Be(0, "no run started, so none is counted");
        }
    }

    /// <summary>QA-P0-30: a request cancelled BEFORE the Queued row commits started no run, so its
    /// run-rate event must be released instead of counting against the user's own cancel for an hour.</summary>
    [Fact]
    public async Task A_request_cancelled_before_the_run_is_committed_releases_its_event()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());

            Func<Task> act = () => AcquireAsync(guard, Guid.NewGuid(), Guid.NewGuid(), RunRateBucket.Paid,
                () => throw new OperationCanceledException("client went away before the run was queued"));

            await act.Should().ThrowAsync<OperationCanceledException>();
            (await context.RunRateEvents.AsNoTracking().CountAsync()).Should().Be(0,
                "the Queued row never committed, so no run will ever start and the cancel consumes nothing");
        }
    }

    /// <summary>S-11 / QA-P0-30: a request cancelled AFTER the Queued row committed started a run the
    /// recovery sweeper will still execute, so its event stays counted and linked (fail closed).</summary>
    [Fact]
    public async Task A_request_cancelled_after_the_run_committed_keeps_its_event_counted()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            var executionId = Guid.NewGuid();

            Func<Task> act = () => AcquireAsync(guard, Guid.NewGuid(), Guid.NewGuid(), RunRateBucket.Paid, () =>
            {
                // What WorkflowExecutionService does right after the Queued row commits (S-11).
                RunRateAdmission.ReportCommitted(executionId);
                throw new OperationCanceledException("client went away after the publish");
            });

            await act.Should().ThrowAsync<OperationCanceledException>();
            RunRateEvent row = await context.RunRateEvents.AsNoTracking().SingleAsync();
            row.ExecutionId.Should().Be(executionId, "the run is committed, so it stays counted and linked");
        }
    }

    /// <summary>Fails the busy-path COUNT once armed, the way a database lock timeout would.</summary>
    private sealed class FailCountWhenArmedInterceptor : DbCommandInterceptor
    {
        private int _armed;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1
                && command.CommandText.Contains("run_rate_events", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("forced count failure");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>QA-P0-30: the busy refusal's count is informational, so a database failure while reading it
    /// must answer the busy refusal, never a 500 — the busy path is exactly where the database is contended.</summary>
    [Fact]
    public async Task A_busy_refusal_survives_a_count_that_fails()
    {
        var interceptor = new FailCountWhenArmedInterceptor();
        var (connection, context) = CreateSqliteContext(interceptor);
        using (connection)
        using (context)
        {
            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            var config = Config(lockWaitMs: 200);
            var processLocks = new AssistantExecutionGuardProcessLocks();
            var holder = new AssistantExecutionGuard(context, config, null, processLocks);
            var waiter = new AssistantExecutionGuard(context, config, null, processLocks);

            var entered = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            Task<RunRateDecision> first = AcquireAsync(holder, user, project, RunRateBucket.Paid, async () =>
            {
                entered.SetResult();
                await release.Task;
                return Guid.NewGuid();
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            try
            {
                // The holder's own count is done; from here the waiter's busy-path count is the only one.
                interceptor.Arm();
                RunRateDecision decision = await AcquireAsync(waiter, user, project, RunRateBucket.Paid).WaitAsync(TimeSpan.FromSeconds(5));

                decision.Busy.Should().BeTrue("a failed count must still answer the busy refusal");
                decision.Allowed.Should().BeFalse();
                decision.Used.Should().Be(0, "the count failed, so it falls back to zero rather than surfacing a 500");
            }
            finally
            {
                release.SetResult();
                await first;
            }
        }
    }

    /// <summary>QA-P0-14: a busy refusal reports the real count of the bucket, not used = limit.</summary>
    [Fact]
    public async Task A_busy_refusal_reports_the_real_count_not_a_full_bucket()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var user = Guid.NewGuid();
            var project = Guid.NewGuid();
            var config = Config(lockWaitMs: 200);
            await SeedAsync(context, user, project, "paid", 3, TimeSpan.FromMinutes(5));
            var processLocks = new AssistantExecutionGuardProcessLocks();
            var holder = new AssistantExecutionGuard(context, config, null, processLocks);
            var waiter = new AssistantExecutionGuard(context, config, null, processLocks);

            var entered = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            Task<RunRateDecision> first = AcquireAsync(holder, user, project, RunRateBucket.Paid, async () =>
            {
                entered.SetResult();
                await release.Task;
                return Guid.NewGuid();
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                RunRateDecision decision = await AcquireAsync(waiter, user, project, RunRateBucket.Paid).WaitAsync(TimeSpan.FromSeconds(5));

                decision.Busy.Should().BeTrue();
                decision.Allowed.Should().BeFalse();
                decision.Used.Should().Be(4, "three earlier runs plus the one being started; nothing was counted for the refused call");
                decision.Limit.Should().Be(10);
            }
            finally
            {
                release.SetResult();
                await first;
            }
        }
    }

    [Theory]
    [InlineData(1, "1 minute")]
    [InlineData(60, "1 minute")]
    [InlineData(61, "2 minutes")]
    [InlineData(754, "13 minutes")]
    public void The_wait_is_stated_in_whole_minutes_rounded_up(int seconds, string expected) =>
        RunRateRefusal.Wait(seconds).Should().Be(expected);

    [Theory]
    [InlineData(10, 10, 754, "paid", "run limit reached — 10 of 10 paid runs in the last hour on this project; try again in 13 minutes")]
    [InlineData(60, 60, 30, "free", "run limit reached — 60 of 60 free runs in the last hour on this project; try again in 1 minute")]
    public void The_refusal_sentence_is_plain_and_states_used_limit_and_wait(int used, int limit, int retry, string bucket, string expected)
    {
        var decision = new RunRateDecision(false, bucket == "paid" ? RunRateBucket.Paid : RunRateBucket.Free, used, limit, 3600, retry);

        RunRateRefusal.Sentence(decision).Should().Be(expected);
    }
}
