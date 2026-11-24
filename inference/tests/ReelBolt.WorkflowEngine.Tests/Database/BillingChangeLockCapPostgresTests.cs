#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Npgsql;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Database;

/// <summary>
/// Security S-4/S-23: the billing change lock's unpooled connections are capped per replica. The server's
/// own view (<c>pg_stat_activity</c>, by the lock's application name) is sampled while a burst runs.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class BillingChangeLockCapPostgresTests
{
    private static readonly TimeSpan HoldFor = TimeSpan.FromMilliseconds(300);

    /// <summary>Counts the lock's server sessions every 10 ms on its own connection until stopped; answers the most seen.</summary>
    private sealed class LockSessionSampler : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task<long> _run;

        public LockSessionSampler(string connectionString)
        {
            string sampler = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = "reelbolt-lock-cap-sampler", Pooling = false }.ConnectionString;
            _run = RunAsync(sampler, _stop.Token);
        }

        private static async Task<long> RunAsync(string connectionString, CancellationToken stop)
        {
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using NpgsqlCommand count = new(
                "SELECT count(*) FROM pg_stat_activity WHERE application_name = @name", connection);
            count.Parameters.AddWithValue("name", PostgresBillingChangeLock.ApplicationName);
            long max = 0;
            while (true)
            {
                max = Math.Max(max, (long)(await count.ExecuteScalarAsync(CancellationToken.None))!);
                if (stop.IsCancellationRequested) return max;
                try
                {
                    await Task.Delay(10, stop);
                }
                catch (OperationCanceledException)
                {
                    // One last sample after the stop, then answer.
                }
            }
        }

        public async Task<long> StopAsync()
        {
            await _stop.CancelAsync();
            return await _run;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await _run;
            }
            catch
            {
                // Already reported by StopAsync, or the test failed before it.
            }

            _stop.Dispose();
        }
    }

    private enum Ending { Claimed, Refused, Capacity }

    private static async Task<Ending> ClaimAndHoldAsync(PostgresBillingChangeLock locks, Guid organizationId)
    {
        try
        {
            await using IAsyncDisposable? claim = await locks.TryAcquireAsync(organizationId, CancellationToken.None);
            if (claim is null) return Ending.Refused;
            await Task.Delay(HoldFor);
            return Ending.Claimed;
        }
        catch (BillingChangeLockCapacityException)
        {
            return Ending.Capacity;
        }
    }

    [Fact]
    public async Task A_burst_of_50_change_claims_across_50_orgs_never_holds_more_than_the_cap()
    {
        string? baseConnectionString = PostgresTestEnvironment.BaseConnectionString();
        if (baseConnectionString is null)
        {
            return;
        }

        string cs = PostgresTestEnvironment.ForDatabase(baseConnectionString, "postgres", maxPoolSize: 2);
        PostgresBillingChangeLock locks = new(cs, new BillingChangeLockOptions { MaxConnections = 3, SlotWaitMilliseconds = 1000 });
        await using LockSessionSampler sampler = new(cs);

        Ending[] endings = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => Task.Run(() => ClaimAndHoldAsync(locks, Guid.NewGuid()))));
        long maxObserved = await sampler.StopAsync();

        maxObserved.Should().BeLessThanOrEqualTo(3, "the lock never holds more connections than Billing:ChangeLock:MaxConnections");
        maxObserved.Should().BeGreaterThan(0, "the sampler must see the lock's sessions at all");
        endings.Should().NotContain(Ending.Refused, "every org is distinct, so nothing holds its claim");
        endings.Count(e => e == Ending.Claimed).Should().BeGreaterThan(0);
        endings.Count(e => e == Ending.Capacity).Should().BeGreaterThan(0, "50 x 300 ms over 3 slots outlasts the 1 s slot wait");
    }

    [Fact]
    public async Task A_burst_of_20_claims_for_one_org_opens_at_most_one_connection()
    {
        string? baseConnectionString = PostgresTestEnvironment.BaseConnectionString();
        if (baseConnectionString is null)
        {
            return;
        }

        string cs = PostgresTestEnvironment.ForDatabase(baseConnectionString, "postgres", maxPoolSize: 2);
        PostgresBillingChangeLock locks = new(cs, new BillingChangeLockOptions { MaxConnections = 3, SlotWaitMilliseconds = 1000 });
        Guid organizationId = Guid.NewGuid();
        await using LockSessionSampler sampler = new(cs);

        TaskCompletionSource go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Ending>[] attempts = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            await go.Task;
            return await ClaimAndHoldAsync(locks, organizationId);
        })).ToArray();
        go.SetResult();
        Ending[] endings = await Task.WhenAll(attempts);
        long maxObserved = await sampler.StopAsync();

        maxObserved.Should().BeLessThanOrEqualTo(1, "a same-org contender in this process never connects");
        endings.Count(e => e == Ending.Claimed).Should().Be(1);
        endings.Count(e => e == Ending.Refused).Should().Be(19);
    }
}
