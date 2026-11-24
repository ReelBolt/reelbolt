using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// T-012: the execute routes classify the run INSIDE the guard's (user, project) lock, so the bucket that is
/// counted, recorded and stamped on the run is the one computed under the lock, never a guess made before it.
/// </summary>
public class AssistantExecutionGuardClassifyUnderLockTests
{
    private static (SqliteConnection connection, InferenceApiDbContext context) CreateSqliteContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
        }

        var context = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        using var engineDb = new ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext(
            new DbContextOptionsBuilder<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>().UseSqlite(connection).Options);
        engineDb.GetService<IRelationalDatabaseCreator>().CreateTables();
        return (connection, context);
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static async Task SeedAsync(InferenceApiDbContext db, Guid user, Guid project, string bucket, int count)
    {
        for (int i = 0; i < count; i++)
        {
            db.RunRateEvents.Add(new RunRateEvent
            {
                Id = Guid.NewGuid(),
                UserId = user,
                ProjectId = project,
                Bucket = bucket,
                Source = RunRateEvent.ExecuteSource,
                CreatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(10),
            });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_classify_delegate_that_returns_paid_is_counted_in_paid()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            Guid user = Guid.NewGuid(), project = Guid.NewGuid();
            // The paid bucket is full and the free bucket is empty: a caller that pre-guessed Free would be let in.
            await SeedAsync(context, user, project, RunRateEvent.PaidBucket, 10);
            bool started = false;

            RunRateDecision decision = await guard.TryAcquireAsync(user, project,
                () => Task.FromResult(RunRateBucket.Paid), RunRateEvent.ExecuteSource,
                () => { started = true; return Task.FromResult<Guid?>(Guid.NewGuid()); }, CancellationToken.None);

            decision.Allowed.Should().BeFalse();
            decision.Bucket.Should().Be(RunRateBucket.Paid);
            decision.Used.Should().Be(10);
            decision.Limit.Should().Be(10);
            started.Should().BeFalse();
        }
    }

    [Fact]
    public async Task The_event_is_recorded_in_the_bucket_the_classify_delegate_returned()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            Guid user = Guid.NewGuid(), project = Guid.NewGuid();

            RunRateDecision decision = await guard.TryAcquireAsync(user, project,
                () => Task.FromResult(RunRateBucket.Paid), RunRateEvent.ExecuteSource,
                () => Task.FromResult<Guid?>(Guid.NewGuid()), CancellationToken.None);

            decision.Should().Be(new RunRateDecision(true, RunRateBucket.Paid, 1, 10, 3600, 0));
            (await context.RunRateEvents.AsNoTracking().Select(e => e.Bucket).ToListAsync())
                .Should().Equal(RunRateEvent.PaidBucket);
        }
    }

    [Fact]
    public async Task Classification_runs_under_the_lock_so_a_second_acquisition_classifies_only_after_the_first_finished()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var processLocks = new AssistantExecutionGuardProcessLocks();
            var guard = new AssistantExecutionGuard(context, Config(), null, processLocks);
            Guid user = Guid.NewGuid(), project = Guid.NewGuid();
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstClassifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();

            Task<RunRateDecision> first = guard.TryAcquireAsync(user, project, async () =>
            {
                lock (order) order.Add("classify-1");
                firstClassifying.SetResult();
                await releaseFirst.Task;
                return RunRateBucket.Free;
            }, RunRateEvent.ExecuteSource, () =>
            {
                lock (order) order.Add("start-1");
                return Task.FromResult<Guid?>(Guid.NewGuid());
            }, CancellationToken.None);

            await firstClassifying.Task;

            // A second guard (same replica's semaphores) on its own context, as a concurrent request would be.
            using var secondContext = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(connection).Options);
            var secondGuard = new AssistantExecutionGuard(secondContext, Config(), null, processLocks);
            Task<RunRateDecision> second = secondGuard.TryAcquireAsync(user, project, () =>
            {
                lock (order) order.Add("classify-2");
                return Task.FromResult(RunRateBucket.Free);
            }, RunRateEvent.ExecuteSource, () => Task.FromResult<Guid?>(Guid.NewGuid()), CancellationToken.None);

            await Task.Delay(200);
            lock (order) order.Should().Equal("classify-1");

            releaseFirst.SetResult();
            await Task.WhenAll(first, second);

            order.Should().Equal("classify-1", "start-1", "classify-2");
        }
    }

    [Fact]
    public async Task The_admitted_bucket_is_visible_to_the_start_callback_and_cleared_after()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            RunRateBucket? seen = null;

            await guard.TryAcquireAsync(Guid.NewGuid(), Guid.NewGuid(),
                () => Task.FromResult(RunRateBucket.Free), RunRateEvent.ExecuteSource,
                () => { seen = RunRateAdmission.Current; return Task.FromResult<Guid?>(Guid.NewGuid()); }, CancellationToken.None);

            seen.Should().Be(RunRateBucket.Free);
            RunRateAdmission.Current.Should().BeNull();
        }
    }

    [Fact]
    public async Task The_fixed_bucket_overload_stamps_no_admitted_bucket()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            RunRateBucket? seen = RunRateBucket.Free;

            RunRateDecision decision = await guard.TryAcquireAsync(Guid.NewGuid(), Guid.NewGuid(), RunRateBucket.Paid, RunRateEvent.RevisionSource,
                () => { seen = RunRateAdmission.Current; return Task.FromResult<Guid?>(Guid.NewGuid()); }, CancellationToken.None);

            decision.Allowed.Should().BeTrue();
            seen.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_classify_delegate_that_throws_starts_nothing()
    {
        var (connection, context) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            var guard = new AssistantExecutionGuard(context, Config());
            bool started = false;

            Func<Task> act = () => guard.TryAcquireAsync(Guid.NewGuid(), Guid.NewGuid(),
                () => throw new InvalidOperationException("steps unreadable"), RunRateEvent.ExecuteSource,
                () => { started = true; return Task.FromResult<Guid?>(Guid.NewGuid()); }, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();
            started.Should().BeFalse();
            (await context.RunRateEvents.CountAsync()).Should().Be(0);
        }
    }
}
