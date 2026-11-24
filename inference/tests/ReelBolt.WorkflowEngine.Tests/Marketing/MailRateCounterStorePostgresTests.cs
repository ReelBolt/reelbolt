#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>
/// A <see cref="FactAttribute"/> that skips unless <see cref="PostgresTestEnvironment.BaseConnectionString"/> finds an Npgsql
/// connection string for a PostgreSQL server whose user may create databases. Locally, start a
/// throwaway server
/// (<c>docker run --rm -d --tmpfs /var/lib/postgresql/data -e POSTGRES_PASSWORD=&lt;password&gt; -p 127.0.0.1:&lt;free port&gt;:5432 postgres:16-alpine</c>),
/// set <c>REELBOLT_TEST_DATABASE_URL="Host=127.0.0.1;Port=&lt;port&gt;;Username=postgres;Password=&lt;password&gt;"</c>,
/// and stop the container afterwards.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (PostgresTestEnvironment.BaseConnectionString() is null)
        {
            Skip = "Needs a real PostgreSQL: set " + PostgresTestEnvironment.PrimaryVariable + " or " + PostgresTestEnvironment.FallbackVariable + " (see MailRateCounterStorePostgresTests).";
        }
    }
}

/// <summary>One throwaway database per test class, migrated with the Inference API's real migrations.</summary>
public sealed class PostgresTestDatabase : IAsyncLifetime
{
    private string? _admin;
    private string? _name;

    public string ConnectionString { get; private set; } = string.Empty;

    public InferenceApiDbContext NewDb() => new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_Api"))
        .Options);

    public async Task InitializeAsync()
    {
        _admin = PostgresTestEnvironment.BaseConnectionString();
        if (_admin is null)
        {
            return;
        }

        _name = "reelbolt_t024_" + Guid.NewGuid().ToString("N")[..12];
        await using (NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(_admin))
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {_name}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        ConnectionString = PostgresTestEnvironment.ForDatabase(_admin, _name);
        await using InferenceApiDbContext db = NewDb();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_admin is null || _name is null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(_admin);
        await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// saas-promotions T-024 (Security N-10, contract C5): <see cref="PostgresMailRateCounterStore"/>
/// against a real PostgreSQL — the C2 upsert, the sliding-window decision, "refused increments
/// nothing", the per-bucket advisory locks under concurrent decisions on the same buckets, and the
/// consent service's claim + decision transaction. Gated by <see cref="PostgresFactAttribute"/>.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class MailRateCounterStorePostgresTests : IClassFixture<PostgresTestDatabase>
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgresTestDatabase _pg;

    public MailRateCounterStorePostgresTests(PostgresTestDatabase pg) => _pg = pg;

    private static string Unique(string prefix) => prefix + ":" + Guid.NewGuid().ToString("N");

    private async Task<int> CountAsync(string bucket, DateTime windowStart)
    {
        await using InferenceApiDbContext db = _pg.NewDb();
        DateTime start = DateTime.SpecifyKind(windowStart, DateTimeKind.Utc);
        return await db.MailRateCounters.AsNoTracking()
            .Where(c => c.Bucket == bucket && c.WindowStart == start)
            .Select(c => c.Count)
            .SingleOrDefaultAsync();
    }

    private async Task<MailRateDecision> DecideAsync(
        IReadOnlyList<MailRateLimit> limits, DateTimeOffset now, Func<IReadOnlyList<MailRateEstimate>, bool>? allow = null)
    {
        await using InferenceApiDbContext db = _pg.NewDb();
        return await new PostgresMailRateCounterStore(db).DecideAsync(
            limits, now, allow ?? (estimates => estimates.All(e => e.Admits)), CancellationToken.None);
    }

    [PostgresFact]
    public async Task The_C2_upsert_counts_up_per_bucket_and_window()
    {
        string bucket = Unique("c2");
        DateTime window = MailRateBuckets.WindowStart(Noon, TimeSpan.FromHours(1));
        await using InferenceApiDbContext db = _pg.NewDb();
        PostgresMailRateCounterStore store = new(db);

        (await store.IncrementAsync(bucket, window, CancellationToken.None)).Should().Be(1);
        (await store.IncrementAsync(bucket, window, CancellationToken.None)).Should().Be(2);
        (await store.IncrementAsync(bucket, window.AddHours(1), CancellationToken.None)).Should().Be(1);
    }

    [PostgresFact]
    public async Task An_allowed_decision_increments_each_distinct_bucket_once_and_a_refused_one_nothing()
    {
        string a = Unique("a");
        string shared = Unique("shared");
        TimeSpan hour = TimeSpan.FromHours(1);
        MailRateLimit[] limits =
        [
            new("a", a, hour, 2),
            new("ceiling", shared, hour, 10),
            new("alarm", shared, hour, 1, Alarm: true),
        ];
        DateTime window = MailRateBuckets.WindowStart(Noon, hour);

        (await DecideAsync(limits, Noon)).Allowed.Should().BeTrue();
        (await CountAsync(a, window)).Should().Be(1);
        (await CountAsync(shared, window)).Should().Be(1, "two limits on one bucket and window count one send once");

        MailRateDecision refused = await DecideAsync(limits, Noon); // the limit-1 one no longer admits
        refused.Allowed.Should().BeFalse();
        (await CountAsync(a, window)).Should().Be(1, "a refused decision increments nothing");
        (await CountAsync(shared, window)).Should().Be(1);
    }

    [PostgresFact]
    public async Task The_estimate_weighs_the_previous_window_by_the_unelapsed_fraction()
    {
        string bucket = Unique("slide");
        TimeSpan hour = TimeSpan.FromHours(1);
        DateTime previous = MailRateBuckets.WindowStart(Noon, hour).AddHours(-1);
        await using (InferenceApiDbContext db = _pg.NewDb())
        {
            db.MailRateCounters.Add(new MailRateCounter { Bucket = bucket, WindowStart = previous, Count = 8, UpdatedAt = previous });
            await db.SaveChangesAsync();
        }

        MailRateDecision decision = await DecideAsync([new("x", bucket, hour, 10)], Noon.AddMinutes(15));

        decision.Estimates.Single().Estimate.Should().BeApproximately(6, 0.0001, "0 + 8 × (1 − 0.25)");
    }

    [PostgresFact]
    public async Task A_decision_inside_a_callers_transaction_is_undone_by_its_rollback()
    {
        string bucket = Unique("tx");
        TimeSpan hour = TimeSpan.FromHours(1);
        await using (InferenceApiDbContext db = _pg.NewDb())
        {
            await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync();
            (await new PostgresMailRateCounterStore(db).DecideAsync([new("x", bucket, hour, 5)], Noon, _ => true, CancellationToken.None))
                .Allowed.Should().BeTrue();
            await tx.RollbackAsync();
        }

        (await CountAsync(bucket, MailRateBuckets.WindowStart(Noon, hour))).Should().Be(0);
    }

    [PostgresFact]
    public async Task Concurrent_decisions_on_the_same_bucket_never_overshoot_the_limit()
    {
        string bucket = Unique("race");
        TimeSpan hour = TimeSpan.FromHours(1);
        MailRateLimit[] limits = [new("x", bucket, hour, 5)];

        MailRateDecision[] decisions = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => DecideAsync(limits, Noon))));

        decisions.Count(d => d.Allowed).Should().Be(5);
        (await CountAsync(bucket, MailRateBuckets.WindowStart(Noon, hour))).Should().Be(5);
    }

    [PostgresFact]
    public async Task Concurrent_decisions_over_the_same_buckets_listed_in_opposite_orders_do_not_deadlock()
    {
        string a = Unique("a");
        string b = Unique("b");
        TimeSpan hour = TimeSpan.FromHours(1);
        MailRateLimit[] ab = [new("a", a, hour, 1000), new("b", b, hour, 1000)];
        MailRateLimit[] ba = [new("b", b, hour, 1000), new("a", a, hour, 1000)];

        MailRateDecision[] decisions = await Task.WhenAll(
            Enumerable.Range(0, 30).Select(i => Task.Run(() => DecideAsync(i % 2 == 0 ? ab : ba, Noon))))
            .WaitAsync(TimeSpan.FromSeconds(60));

        decisions.Should().AllSatisfy(d => d.Allowed.Should().BeTrue());
        DateTime window = MailRateBuckets.WindowStart(Noon, hour);
        (await CountAsync(a, window)).Should().Be(30);
        (await CountAsync(b, window)).Should().Be(30);
    }

    [PostgresFact]
    public async Task Only_one_concurrent_caller_claims_a_window_marker()
    {
        string bucket = Unique("mark");
        DateTime window = MailRateBuckets.WindowStart(Noon, TimeSpan.FromHours(1));

        bool[] claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            await using InferenceApiDbContext db = _pg.NewDb();
            return await new PostgresMailRateCounterStore(db).TryMarkOnceAsync(bucket, window, CancellationToken.None);
        })));

        claims.Count(c => c).Should().Be(1);
    }

    // --- The consent service over the real store ----------------------------------------------

    private sealed class CountingOutbox : IMarketingMailOutbox
    {
        private int _sent;

        public int Sent => Volatile.Read(ref _sent);

        public bool Enqueue(ReelBolt.Inference.Api.Services.Account.AccountMail mail, MarketingMailOrigin origin)
        {
            Interlocked.Increment(ref _sent);
            return true;
        }
    }

    private static readonly InMemorySecretStore Secrets = new();

    private static MarketingConsentService Service(
        InferenceApiDbContext db, CountingOutbox outbox, MarketingOptions options, ILogger<MarketingConsentService>? logger = null) => new(
        db,
        new MarketingLinkTokens(Secrets),
        new MarketingSuppressionHasher(Secrets),
        outbox,
        MarketingWording.LoadFile(MarketingWording.DefaultPath),
        Options.Create(options),
        Options.Create(new CloudStorageOptions()),
        new PostgresMailRateCounterStore(db),
        logger ?? NullLogger<MarketingConsentService>.Instance,
        new ManualClock(Noon));

    [PostgresFact]
    public async Task Concurrent_subscribes_from_one_ipv6_64_send_at_most_the_ip_limit_and_count_only_what_was_sent()
    {
        CountingOutbox outbox = new();
        MarketingOptions options = new() { PublicBaseUrl = "https://app.example.test" };
        string domain = Guid.NewGuid().ToString("N")[..10] + ".example";
        string prefix = "2001:db8:" + Random.Shared.Next(1, 0xffff).ToString("x") + ":1::";
        TimeSpan hour = TimeSpan.FromHours(1);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
        {
            await using InferenceApiDbContext db = _pg.NewDb();
            (await Service(db, outbox, options).SubscribeAsync(
                $"p{i}@{domain}", "2026-10-marketing-v1", MarketingConsentSource.SiteFooter,
                new MarketingRequestEvidence(prefix + (i + 1).ToString("x"), "PgTest"), CancellationToken.None))
                .Should().Be(MarketingSubscribeOutcome.Accepted);
        })));

        outbox.Sent.Should().Be(5);
        (await CountAsync(MailRateBuckets.ConfirmIp(prefix + "1"), MailRateBuckets.WindowStart(Noon, hour))).Should().Be(5);
        (await CountAsync(MailRateBuckets.ConfirmDomain(domain), MailRateBuckets.WindowStart(Noon, hour)))
            .Should().Be(5, "the refused seven incremented nothing");

        await using InferenceApiDbContext check = _pg.NewDb();
        (await check.MarketingConsents.CountAsync(c => c.Email.EndsWith("@" + domain) && c.LastConfirmationSentAt != null))
            .Should().Be(5, "a refused send's row claim was rolled back");
    }
    [PostgresFact]
    public async Task A_stream_of_refused_requests_logs_the_alarm_once_per_window()
    {
        // N-12: the once-per-window marker of a refused decision must survive that decision's rollback.
        CountingOutbox outbox = new();
        RecordingLogger<MarketingConsentService> log = new();
        MarketingOptions options = new()
        {
            PublicBaseUrl = "https://app.example.test",
            // Every direct request is over the global alarm: refused, and alarmed once.
            ConfirmGlobalPerDay = 1,
        };
        string run = Guid.NewGuid().ToString("N")[..8];

        for (int i = 0; i < 6; i++)
        {
            await using InferenceApiDbContext db = _pg.NewDb();
            await Service(db, outbox, options, log).SubscribeAsync(
                $"n{i}@{run}-{i}.example", "2026-10-marketing-v1", MarketingConsentSource.SiteFooter,
                new MarketingRequestEvidence($"203.0.113.{i + 1}", "PgTest"), CancellationToken.None);
        }

        outbox.Sent.Should().BeLessThanOrEqualTo(1);
        log.Entries.Where(e => e.EventId == MarketingConsentService.GlobalCapReached)
            .Should().ContainSingle("the marker written for the first refusal is not rolled back with it");
    }
}
