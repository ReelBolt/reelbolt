#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// saas-promotions T-023 (contract C8, "Postgres-backed tests"): the advisory-lock paths of the promotions
/// against a real PostgreSQL. Env-gated (<see cref="PostgresTestEnvironment"/>): set <c>REELBOLT_TEST_DATABASE_URL</c> to an Npgsql connection
/// string with CREATEDB rights (a throwaway <c>postgres:16-alpine</c> on a free loopback port); each test
/// migrates a database of its own and drops it afterwards. Without the variable every test returns early.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class PromotionPostgresConcurrencyTests
{
    private const int Contenders = 8;

    /// <summary>One migrated database with the creator plan, and helpers to add buyers.</summary>
    private sealed class Pg : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _admin;
        private readonly string _name;
        public DbContextOptions<InferenceApiDbContext> Options { get; }
        public BillingControllerTests.FakeProvider Provider { get; } = new();
        public PromotionCodeAttemptLimiter Limiter { get; } = new(TimeProvider.System);

        private Pg(NpgsqlDataSource admin, string name, string connectionString, DbContextOptions<InferenceApiDbContext> options)
        {
            _admin = admin;
            _name = name;
            ConnectionString = connectionString;
            Options = options;
        }

        /// <summary>The pooled connection string every <see cref="NewDb"/> context uses.</summary>
        public string ConnectionString { get; }

        /// <param name="maxPoolSize">The per-test pool cap.</param>
        /// <param name="poolTimeoutSeconds">Npgsql's <c>Timeout</c>: how long an open waits for a pool slot before it throws.</param>
        public static async Task<Pg?> CreateAsync(int maxPoolSize = PostgresTestEnvironment.DefaultMaxPoolSize, int? poolTimeoutSeconds = null)
        {
            string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
            if (string.IsNullOrWhiteSpace(baseConnection))
            {
                return null;
            }

            string name = "reelbolt_promo_test_" + Guid.NewGuid().ToString("N")[..12];
            NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {name}"))
            {
                await create.ExecuteNonQueryAsync();
            }

            // The default cap covers RaceAsync: contenders take their connection after the barrier, at most Contenders (8) at once.
            NpgsqlConnectionStringBuilder connection = new(PostgresTestEnvironment.ForDatabase(baseConnection, name, maxPoolSize));
            if (poolTimeoutSeconds is int timeout)
            {
                connection.Timeout = timeout;
            }

            Pg pg = new(admin, name, connection.ConnectionString, new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection.ConnectionString).Options);
            await using InferenceApiDbContext setup = pg.NewDb();
            await setup.GetService<IMigrator>().MigrateAsync();
            setup.Plans.Add(new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, PriceMonthlyUsd = 29, MonthlyCreditsMillis = 1_000_000, TopUpAllowed = true, SortOrder = 1, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_creator"}}""" });
            await setup.SaveChangesAsync();
            return pg;
        }

        public InferenceApiDbContext NewDb() => new(Options);

        public async Task<Guid> UserAsync()
        {
            await using InferenceApiDbContext db = NewDb();
            Guid id = Guid.NewGuid();
            db.ApplicationUsers.Add(new ApplicationUser { Id = id, Email = $"{id:N}@example.test", DisplayName = "buyer", EmailVerifiedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return id;
        }

        public async Task<Guid> OrgAsync(Guid owner)
        {
            await using InferenceApiDbContext db = NewDb();
            Guid id = Guid.NewGuid();
            db.Organizations.Add(new Organization { Id = id, Name = "o", Slug = "o-" + id.ToString("N"), Kind = OrganizationKind.Team });
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = id, UserId = owner, Role = OrganizationRole.Owner });
            await db.SaveChangesAsync();
            return id;
        }

        public async Task<Guid> PromotionAsync(PromotionKind kind, Action<Promotion> configure)
        {
            Promotion p = new()
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                Name = kind.ToString(),
                Status = PromotionStatus.Active,
                CreatedByUserId = Guid.NewGuid(),
            };
            configure(p);
            await using InferenceApiDbContext db = NewDb();
            db.Promotions.Add(p);
            await db.SaveChangesAsync();
            return p.Id;
        }

        public async Task CheckoutRowAsync(string txn, Guid org, Guid user, Guid bonus, DateTime createdAt, DateTime? supersededAt, bool withinCaps = true)
        {
            await using InferenceApiDbContext db = NewDb();
            db.PromotionCheckouts.Add(new PromotionCheckout
            {
                Id = Guid.NewGuid(),
                ProviderTransactionId = txn,
                OrganizationId = org,
                UserId = user,
                BonusPromotionId = bonus,
                PriceId = "pri_topup_500",
                TopUpCredits = 500,
                CreatedAt = createdAt,
                SupersededAt = supersededAt,
                WithinCapsAtReservation = withinCaps,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>Subscribes the org to creator through the processor, so it may buy top-ups.</summary>
        public Task SubscribeAsync(Guid org) => ProcessAsync(new BillingEvent
        {
            Provider = "fake",
            EventId = "evt_sub_" + org.ToString("N"),
            EventType = "subscription.created",
            OccurredAt = DateTime.UtcNow,
            Kind = BillingEventKind.SubscriptionChanged,
            OrganizationId = org,
            CustomerId = "ctm_" + org.ToString("N"),
            SubscriptionId = "sub_" + org.ToString("N"),
            Status = SubscriptionStatus.Active,
            PeriodStart = DateTime.UtcNow,
            PeriodEnd = DateTime.UtcNow.AddMonths(1),
            Items = [new BillingLineItem("pri_creator", 1)],
        });

        public PromotionService Service(InferenceApiDbContext db, PromotionOptions? options = null)
            => new(db, [Provider], Limiter, NullLogger<PromotionService>.Instance, options: options is null ? null : Microsoft.Extensions.Options.Options.Create(options));

        public BillingController Billing(InferenceApiDbContext db, Guid user, Guid org, PromotionOptions? options = null)
            => new(
                db,
                new PromoUser(user, org, OrganizationRole.Owner, false),
                Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = true }),
                [Provider],
                new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance),
                new BillingControllerTests.FakeTenant(),
                Microsoft.Extensions.Options.Options.Create(new PaddleOptions()),
                NullLogger<BillingController>.Instance,
                promotions: Service(db, options))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

        /// <summary>
        /// Holds <c>pg_advisory_xact_lock(hashtext('promo:' || id))</c> on an unpooled side connection (another
        /// replica's checkout) until the returned handle is disposed.
        /// </summary>
        public async Task<IAsyncDisposable> HoldPromotionLockAsync(Guid promotionId)
        {
            NpgsqlConnection side = new(new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ConnectionString);
            await side.OpenAsync();
            NpgsqlTransaction tx = await side.BeginTransactionAsync();
            await using (NpgsqlCommand lockCmd = new("SELECT pg_advisory_xact_lock(hashtext(@key))", side, tx))
            {
                lockCmd.Parameters.AddWithValue("key", PromotionLocks.PromotionKey(promotionId));
                await lockCmd.ExecuteNonQueryAsync();
            }

            return new SideLock(side, tx);
        }

        /// <summary>Holds <c>pg_advisory_xact_lock(hashtext(key))</c> on an unpooled side connection until disposed.</summary>
        public async Task<IAsyncDisposable> HoldAdvisoryLockAsync(string key)
        {
            NpgsqlConnection side = new(new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ConnectionString);
            await side.OpenAsync();
            NpgsqlTransaction tx = await side.BeginTransactionAsync();
            await using (NpgsqlCommand lockCmd = new("SELECT pg_advisory_xact_lock(hashtext(@key))", side, tx))
            {
                lockCmd.Parameters.AddWithValue("key", key);
                await lockCmd.ExecuteNonQueryAsync();
            }

            return new SideLock(side, tx);
        }

        /// <summary>Backends of the test database that are not idle: checked-out connections (and the side holder).</summary>
        public Task<long> BusyBackendsAsync() => CountAsync("state <> 'idle'");

        /// <summary>Backends of the test database waiting on a lock (an advisory lock here).</summary>
        public Task<long> LockWaitersAsync() => CountAsync("wait_event_type = 'Lock'");

        private async Task<long> CountAsync(string condition)
        {
            await using NpgsqlCommand count = _admin.CreateCommand($"SELECT count(*) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid() AND {condition}");
            count.Parameters.AddWithValue("db", _name);
            return (long)(await count.ExecuteScalarAsync())!;
        }

        private sealed class SideLock(NpgsqlConnection connection, NpgsqlTransaction tx) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await tx.RollbackAsync();
                await tx.DisposeAsync();
                await connection.DisposeAsync();
            }
        }

        public async Task<BillingEventOutcome> ProcessAsync(BillingEvent evt)
        {
            await using InferenceApiDbContext db = NewDb();
            return await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance).ProcessAsync(evt, "{}");
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using (NpgsqlCommand drop = _admin.CreateCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)"))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await _admin.DisposeAsync();
        }
    }

    private static BillingEvent PackPaid(string eventId, string txn, Guid org) => new()
    {
        Provider = "fake",
        EventId = eventId,
        EventType = "transaction.completed",
        OccurredAt = DateTime.UtcNow,
        Kind = BillingEventKind.TopUpPaid,
        OrganizationId = org,
        TransactionId = txn,
        TopUpCredits = 500,
        Items = [new BillingLineItem("pri_topup_500", 1)],
    };

    /// <summary>Starts every contender at once, each on its own DbContext (its own connection).</summary>
    private static async Task<T[]> RaceAsync<T>(int n, Func<int, Task<T>> body)
    {
        using Barrier start = new(n);
        return await Task.WhenAll(Enumerable.Range(0, n).Select(i => Task.Run(async () =>
        {
            start.SignalAndWait();
            return await body(i);
        })));
    }

    // ------------------------------------------------------------------ F-6 / N-11: the last bonus use under concurrent webhooks

    [Fact]
    public async Task Two_concurrent_webhooks_for_the_last_use_of_a_superseded_bonus_grant_exactly_once()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid bonus = await pg.PromotionAsync(PromotionKind.PackBonus, p => { p.BonusPercent = 20; p.MaxRedemptions = 1; p.TargetPriceIdsJson = """["pri_topup_500"]"""; });
        (Guid user, Guid org)[] buyers = new (Guid, Guid)[2];
        for (int i = 0; i < 2; i++)
        {
            Guid user = await pg.UserAsync();
            buyers[i] = (user, await pg.OrgAsync(user));
            // Superseded reservations are honoured only while the caps hold: exactly one may win.
            await pg.CheckoutRowAsync($"txn_{i}", buyers[i].org, user, bonus, DateTime.UtcNow, supersededAt: DateTime.UtcNow);
        }

        await RaceAsync(2, i => pg.ProcessAsync(PackPaid($"evt_{i}", $"txn_{i}", buyers[i].org)));

        await using InferenceApiDbContext db = pg.NewDb();
        (await db.PromotionRedemptions.CountAsync(r => r.PromotionId == bonus && r.Outcome == PromotionRedemptionOutcome.Granted)).Should().Be(1);
        (await db.PromotionRedemptions.SingleAsync(r => r.PromotionId == bonus && r.Outcome == PromotionRedemptionOutcome.NotGranted))
            .NotGrantedReason.Should().Be(PromotionNotGrantedReason.Superseded, "the loser is recorded for support (N-11)");
        (await db.Promotions.SingleAsync(p => p.Id == bonus)).RedemptionCount.Should().Be(1, "no double increment");
        (await db.CreditGrants.CountAsync(g => g.Source == CreditGrantSource.Promo)).Should().Be(1);
    }

    [Fact]
    public async Task Users_racing_for_the_last_global_use_get_at_most_cap_plus_one_with_overshoot_and_the_rest_not_granted()
    {
        // Contract C10 (F-8.4) replaces the round-3 rule that honoured every reserved payment past the cap.
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid bonus = await pg.PromotionAsync(PromotionKind.PackBonus, p => { p.BonusPercent = 20; p.MaxRedemptions = 1; p.TargetPriceIdsJson = """["pri_topup_500"]"""; });
        const int buyersCount = 3;
        (Guid user, Guid org)[] buyers = new (Guid, Guid)[buyersCount];
        for (int i = 0; i < buyersCount; i++)
        {
            Guid user = await pg.UserAsync();
            buyers[i] = (user, await pg.OrgAsync(user));
            // Each a live reservation that was within every cap when it was created.
            await pg.CheckoutRowAsync($"txn_{i}", buyers[i].org, user, bonus, DateTime.UtcNow, supersededAt: null);
        }

        // Plus a differently-numbered replay of the first payment, racing the others.
        await RaceAsync(buyersCount + 1, i => pg.ProcessAsync(PackPaid($"evt_{i}", $"txn_{i % buyersCount}", buyers[i % buyersCount].org)));

        await using InferenceApiDbContext db = pg.NewDb();
        List<PromotionRedemption> rows = await db.PromotionRedemptions.Where(r => r.PromotionId == bonus).ToListAsync();
        rows.Should().HaveCount(buyersCount, "one row per paid transaction, the replay adds none");
        List<PromotionRedemption> granted = rows.Where(r => r.Outcome == PromotionRedemptionOutcome.Granted).ToList();
        granted.Should().HaveCount(2, "cap 1 plus the global overshoot bound min(5, max(1, 1 / 100)) = 1");
        granted.Count(r => r.Overshoot).Should().Be(1);
        rows.Single(r => r.Outcome == PromotionRedemptionOutcome.NotGranted).NotGrantedReason.Should().Be(PromotionNotGrantedReason.OvershootBoundReached);
        (await db.Promotions.SingleAsync(p => p.Id == bonus)).RedemptionCount.Should().Be(2);
        (await db.CreditGrants.CountAsync(g => g.Source == CreditGrantSource.Promo)).Should().Be(2);
    }

    [Fact]
    public async Task F8_4_one_user_paying_five_concurrent_checkouts_of_a_per_user_cap_1_bonus_gets_exactly_one_bonus()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid bonus = await pg.PromotionAsync(PromotionKind.PackBonus, p => { p.BonusPercent = 20; p.MaxRedemptionsPerOrg = 1; p.TargetPriceIdsJson = """["pri_topup_500"]"""; });
        Guid user = await pg.UserAsync();
        Guid org = await pg.OrgAsync(user);
        await pg.SubscribeAsync(org);
        const int checkouts = 5;

        IActionResult[] opened = await RaceAsync(checkouts, async _ =>
        {
            await using InferenceApiDbContext db = pg.NewDb();
            return await pg.Billing(db, user, org).Checkout(new CheckoutRequestDto(null, null, 500), CancellationToken.None);
        });
        // F-5 (T-034): a checkout superseded before its transaction was attached answers 409 and cancels it.
        opened.Should().AllSatisfy(r => ((ObjectResult)r).StatusCode.Should().BeOneOf(200, 409));
        opened.Where(r => ((ObjectResult)r).StatusCode == 409).Should().OnlyContain(r => ErrorOf(r) == "promo_checkout_superseded");
        string[] transactions;
        await using (InferenceApiDbContext reservations = pg.NewDb())
        {
            transactions = await reservations.PromotionCheckouts.Where(c => c.UserId == user).Select(c => c.ProviderTransactionId!).ToArrayAsync();
        }

        transactions.Should().HaveCount(checkouts).And.OnlyContain(t => t != null, "every created transaction is recorded on its reservation");

        // Every one of them is paid (the provider cancel of the superseded ones lost the race), at once.
        await RaceAsync(checkouts, i => pg.ProcessAsync(PackPaid($"evt_pay_{i}", transactions[i], org)));

        await using InferenceApiDbContext check = pg.NewDb();
        (await check.CreditGrants.CountAsync(g => g.Source == CreditGrantSource.Promo)).Should().Be(1, "exactly one bonus");
        List<PromotionRedemption> rows = await check.PromotionRedemptions.Where(r => r.PromotionId == bonus).ToListAsync();
        rows.Count(r => r.Outcome == PromotionRedemptionOutcome.Granted).Should().Be(1);
        rows.Count(r => r.Outcome == PromotionRedemptionOutcome.NotGranted).Should().Be(checkouts - 1);
        (await check.Promotions.SingleAsync(p => p.Id == bonus)).RedemptionCount.Should().Be(1);
    }

    // ------------------------------------------------------------------ S-04: one user, N orgs, one credit code

    [Fact]
    public async Task Concurrent_redeems_of_a_once_per_user_code_from_N_orgs_of_one_user_grant_once()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        await pg.PromotionAsync(PromotionKind.CreditCode, p => { p.Code = "WELCOME50"; p.CreditsMillis = 50_000; p.CreditExpiryDays = 30; });
        Guid user = await pg.UserAsync();
        Guid[] orgs = new Guid[Contenders];
        for (int i = 0; i < Contenders; i++)
        {
            orgs[i] = await pg.OrgAsync(user);
        }

        PromotionOutcome<RedeemResult>[] results = await RaceAsync(Contenders, async i =>
        {
            await using InferenceApiDbContext db = pg.NewDb();
            return await pg.Service(db).RedeemAsync(orgs[i], user, "WELCOME50", CancellationToken.None);
        });

        results.Count(r => r.Error is null).Should().Be(1);
        results.Where(r => r.Error is not null).Select(r => r.Error!.Code).Should().AllBe("promo_already_redeemed");
        await using InferenceApiDbContext check = pg.NewDb();
        (await check.CreditGrants.CountAsync(g => g.Source == CreditGrantSource.Promo)).Should().Be(1);
        (await check.PromotionRedemptions.CountAsync()).Should().Be(1);
        (await check.Promotions.SingleAsync()).RedemptionCount.Should().Be(1);
    }

    // ------------------------------------------------------------------ F-5 / S-03: reservations under the promotion lock

    [Fact]
    public async Task N_concurrent_reservations_by_different_buyers_take_exactly_the_global_cap()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        const int cap = 3;
        Guid discount = await pg.PromotionAsync(PromotionKind.PlanDiscount, p =>
        {
            p.DiscountType = PromotionDiscountType.Percentage;
            p.DiscountAmount = "20";
            p.DurationKind = PromotionDuration.Once;
            p.TargetPriceIdsJson = """["pri_creator"]""";
            p.ProviderDiscountId = "dsc_plan";
            p.MaxRedemptions = cap;
        });
        (Guid user, Guid org)[] buyers = new (Guid, Guid)[Contenders];
        for (int i = 0; i < Contenders; i++)
        {
            Guid user = await pg.UserAsync();
            buyers[i] = (user, await pg.OrgAsync(user));
        }

        IActionResult[] results = await RaceAsync(Contenders, async i =>
        {
            await using InferenceApiDbContext db = pg.NewDb();
            return await pg.Billing(db, buyers[i].user, buyers[i].org).Checkout(new CheckoutRequestDto("creator", null, null), CancellationToken.None);
        });

        results.Should().AllSatisfy(r => ((ObjectResult)r).StatusCode.Should().Be(200));
        results.Count(r => JsonSerializer.SerializeToElement(((ObjectResult)r).Value).GetProperty("discount").ValueKind == JsonValueKind.Object)
            .Should().Be(cap);
        await using InferenceApiDbContext check = pg.NewDb();
        (await check.PromotionCheckouts.CountAsync(c => c.DiscountPromotionId == discount && c.SupersededAt == null)).Should().Be(cap);
    }

    [Fact]
    public async Task N_concurrent_checkouts_by_one_buyer_leave_exactly_one_open_reservation()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid discount = await pg.PromotionAsync(PromotionKind.PlanDiscount, p =>
        {
            p.DiscountType = PromotionDiscountType.Percentage;
            p.DiscountAmount = "20";
            p.DurationKind = PromotionDuration.Once;
            p.TargetPriceIdsJson = """["pri_creator"]""";
            p.ProviderDiscountId = "dsc_plan";
        });
        Guid user = await pg.UserAsync();
        Guid org = await pg.OrgAsync(user);

        await RaceAsync(Contenders, async _ =>
        {
            await using InferenceApiDbContext db = pg.NewDb();
            return await pg.Billing(db, user, org).Checkout(new CheckoutRequestDto("creator", null, null), CancellationToken.None);
        });

        await using InferenceApiDbContext check = pg.NewDb();
        (await check.PromotionCheckouts.CountAsync(c => c.DiscountPromotionId == discount && c.SupersededAt == null && c.CompletedAt == null))
            .Should().Be(1, "at most one open reservation per (promotion, user) and per (promotion, org)");
    }

    // ------------------------------------------------------------------ S-11: one discount row per (promotion, subscription)

    [Fact]
    public async Task Concurrent_discounted_payments_of_one_subscription_record_one_redemption()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid discount = await pg.PromotionAsync(PromotionKind.PlanDiscount, p =>
        {
            p.DiscountType = PromotionDiscountType.Percentage;
            p.DiscountAmount = "20";
            p.DurationKind = PromotionDuration.Forever;
            p.TargetPriceIdsJson = """["pri_creator"]""";
            p.ProviderDiscountId = "dsc_plan";
        });
        Guid user = await pg.UserAsync();
        Guid org = await pg.OrgAsync(user);

        BillingEventOutcome[] outcomes = await RaceAsync(Contenders, i => pg.ProcessAsync(new BillingEvent
        {
            Provider = "fake",
            EventId = $"evt_{i}",
            EventType = "transaction.completed",
            OccurredAt = DateTime.UtcNow,
            Kind = BillingEventKind.TransactionCompleted,
            OrganizationId = org,
            TransactionId = $"txn_{i}",
            SubscriptionId = "sub_1",
            DiscountId = "dsc_plan",
        }));

        outcomes.Should().OnlyContain(o => o == BillingEventOutcome.Applied);
        await using InferenceApiDbContext check = pg.NewDb();
        (await check.PromotionRedemptions.CountAsync(r => r.PromotionId == discount)).Should().Be(1);
        (await check.Promotions.SingleAsync(p => p.Id == discount)).RedemptionCount.Should().Be(1);
    }

    // ------------------------------------------------------------------ T-034 F-5: supersede vs attach, both orderings

    private static void PlanDiscount(Promotion p)
    {
        p.DiscountType = PromotionDiscountType.Percentage;
        p.DiscountAmount = "20";
        p.DurationKind = PromotionDuration.Once;
        p.TargetPriceIdsJson = """["pri_creator"]""";
        p.ProviderDiscountId = "dsc_plan";
    }

    private static async Task<IActionResult> CheckoutAsync(Pg pg, Guid user, Guid org)
    {
        await using InferenceApiDbContext db = pg.NewDb();
        return await pg.Billing(db, user, org).Checkout(new CheckoutRequestDto("creator", null, null), CancellationToken.None);
    }

    private static string? ErrorOf(IActionResult r)
        => JsonSerializer.SerializeToElement(((ObjectResult)r).Value).TryGetProperty("error", out JsonElement e) ? e.GetString() : null;

    private static async Task<PromotionCheckout> RowAsync(Pg pg, string txn)
    {
        await using InferenceApiDbContext db = pg.NewDb();
        return await db.PromotionCheckouts.AsNoTracking().SingleAsync(c => c.ProviderTransactionId == txn);
    }

    [Fact]
    public async Task F5_supersede_and_its_cancel_before_the_attach_answer_409_and_cancel_the_transaction()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        await pg.PromotionAsync(PromotionKind.PlanDiscount, PlanDiscount);
        Guid user = await pg.UserAsync();
        Guid org = await pg.OrgAsync(user);
        IActionResult? second = null;
        pg.Provider.BeforeCheckoutReturns = async n =>
        {
            if (n == 1)
            {
                second = await CheckoutAsync(pg, user, org);
            }
        };

        IActionResult first = await CheckoutAsync(pg, user, org);

        ((ObjectResult)second!).StatusCode.Should().Be(200);
        ((ObjectResult)first).StatusCode.Should().Be(409);
        ErrorOf(first).Should().Be("promo_checkout_superseded");
        pg.Provider.CancelCalls.Should().Equal("txn_1");
        (await RowAsync(pg, "txn_1")).ProviderCancelledAt.Should().NotBeNull();
        (await RowAsync(pg, "txn_2")).SupersededAt.Should().BeNull();
    }

    [Fact]
    public async Task F5_supersede_before_the_attach_and_its_cancel_after_it_still_answer_409()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        await pg.PromotionAsync(PromotionKind.PlanDiscount, PlanDiscount);
        Guid user = await pg.UserAsync();
        Guid org = await pg.OrgAsync(user);
        TaskCompletionSource secondCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IActionResult>? second = null;
        pg.Provider.BeforeCheckoutReturns = async n =>
        {
            if (n == 1)
            {
                second = Task.Run(() => CheckoutAsync(pg, user, org));
                await secondCreated.Task;
            }
            else
            {
                secondCreated.SetResult();
                await firstDone.Task;
            }
        };

        IActionResult first = await CheckoutAsync(pg, user, org);
        firstDone.SetResult();
        IActionResult secondResult = await second!;

        ((ObjectResult)secondResult).StatusCode.Should().Be(200);
        ((ObjectResult)first).StatusCode.Should().Be(409);
        ErrorOf(first).Should().Be("promo_checkout_superseded");
        (await RowAsync(pg, "txn_1")).ProviderCancelledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task F5_attach_before_the_supersede_keeps_200_and_the_supersede_cancels_the_transaction()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        await pg.PromotionAsync(PromotionKind.PlanDiscount, PlanDiscount);
        Guid user = await pg.UserAsync();
        Guid org = await pg.OrgAsync(user);
        // Request 1 completes (attached while live) before request 2 supersedes it.
        IActionResult first = await CheckoutAsync(pg, user, org);
        pg.Provider.CancelCalls.Should().BeEmpty();
        IActionResult second = await CheckoutAsync(pg, user, org);

        ((ObjectResult)second).StatusCode.Should().Be(200);
        ((ObjectResult)first).StatusCode.Should().Be(200, "it was attached while still live; the newer checkout cancelled it");
        pg.Provider.CancelCalls.Should().Equal("txn_1");
        (await RowAsync(pg, "txn_1")).ProviderCancelledAt.Should().NotBeNull();
    }

    // ------------------------------------------------------------------ T-036 F-5: the superseding request fails or is aborted

    private static async Task<IActionResult> CheckoutAsync(Pg pg, Guid user, Guid org, CancellationToken ct)
    {
        await using InferenceApiDbContext db = pg.NewDb();
        return await pg.Billing(db, user, org).Checkout(new CheckoutRequestDto("creator", null, null), ct);
    }

    private static async Task<List<PromotionCheckout>> RowsAsync(Pg pg)
    {
        await using InferenceApiDbContext db = pg.NewDb();
        return await db.PromotionCheckouts.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task F5_superseding_checkout_whose_provider_create_fails_still_cancels_the_superseded_transaction()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        await pg.PromotionAsync(PromotionKind.PlanDiscount, PlanDiscount);
        Guid user = await pg.UserAsync();
        Guid org = await pg.OrgAsync(user);
        IActionResult first = await CheckoutAsync(pg, user, org);
        ((ObjectResult)first).StatusCode.Should().Be(200, "attached while live: a payable discounted URL was handed out");

        pg.Provider.CheckoutFailure = new System.Net.Http.HttpRequestException("paddle 500");
        IActionResult second = await CheckoutAsync(pg, user, org);

        ((ObjectResult)second).StatusCode.Should().Be(502);
        pg.Provider.CancelCalls.Should().Equal(["txn_1"], "the reservation it superseded must not stay payable");
        List<PromotionCheckout> rows = await RowsAsync(pg);
        rows.Should().ContainSingle("the failed checkout's own reservation is released");
        rows[0].ProviderTransactionId.Should().Be("txn_1");
        rows[0].ProviderCancelledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task F5_superseding_checkout_aborted_during_the_provider_create_still_cancels_the_superseded_transaction()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        await pg.PromotionAsync(PromotionKind.PlanDiscount, PlanDiscount);
        Guid user = await pg.UserAsync();
        Guid org = await pg.OrgAsync(user);
        IActionResult first = await CheckoutAsync(pg, user, org);
        ((ObjectResult)first).StatusCode.Should().Be(200);

        using CancellationTokenSource aborted = new();
        pg.Provider.BeforeCheckoutReturns = n =>
        {
            if (n == 2)
            {
                // The client disconnects while the provider creates the transaction (RequestAborted).
                aborted.Cancel();
                aborted.Token.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        };

        Func<Task> second = () => CheckoutAsync(pg, user, org, aborted.Token);

        await second.Should().ThrowAsync<OperationCanceledException>();
        pg.Provider.CancelCalls.Should().Equal(["txn_1"], "an abort must not leave the superseded transaction payable");
        List<PromotionCheckout> rows = await RowsAsync(pg);
        rows.Should().ContainSingle("the aborted checkout's own reservation is released");
        rows[0].ProviderTransactionId.Should().Be("txn_1");
        rows[0].ProviderCancelledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task F5_superseding_checkout_aborted_after_the_provider_create_attaches_and_cancels_the_superseded_transaction()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        await pg.PromotionAsync(PromotionKind.PlanDiscount, PlanDiscount);
        Guid user = await pg.UserAsync();
        Guid org = await pg.OrgAsync(user);
        await CheckoutAsync(pg, user, org);

        using CancellationTokenSource aborted = new();
        pg.Provider.BeforeCheckoutReturns = n =>
        {
            if (n == 2)
            {
                // The transaction exists at the provider; the client is gone before the attach.
                aborted.Cancel();
            }

            return Task.CompletedTask;
        };

        IActionResult second = await CheckoutAsync(pg, user, org, aborted.Token);

        ((ObjectResult)second).StatusCode.Should().Be(200);
        pg.Provider.CancelCalls.Should().Equal(["txn_1"]);
        List<PromotionCheckout> rows = await RowsAsync(pg);
        rows.Single(c => c.ProviderTransactionId == "txn_1").ProviderCancelledAt.Should().NotBeNull();
        PromotionCheckout live = rows.Single(c => c.SupersededAt == null);
        live.ProviderTransactionId.Should().Be("txn_2", "the created transaction is attached despite the abort, so it is never orphaned");
    }

    /// <summary>
    /// N-22: a real guard, not a smoke test. Later checkouts answer the provider sooner, so most requests
    /// attach after a newer one superseded them (and after that newer one's cancel ran on a row with no
    /// transaction yet). Without the conditional attach each of them hands out a payable discounted URL.
    /// </summary>
    [Fact]
    public async Task F5_n_parallel_checkouts_by_one_buyer_never_return_a_payable_superseded_transaction()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        await pg.PromotionAsync(PromotionKind.PlanDiscount, PlanDiscount);
        Guid user = await pg.UserAsync();
        Guid org = await pg.OrgAsync(user);
        // Reverse the attach order against the reserve order: checkout n waits (Contenders - n) * 40 ms.
        pg.Provider.BeforeCheckoutReturns = n => Task.Delay(Math.Max(0, Contenders - n) * 40);

        IActionResult[] results = await RaceAsync(Contenders, _ => CheckoutAsync(pg, user, org));

        results.Should().OnlyContain(r => ((ObjectResult)r).StatusCode == 200 || ((ObjectResult)r).StatusCode == 409);
        await using InferenceApiDbContext check = pg.NewDb();
        List<PromotionCheckout> rows = await check.PromotionCheckouts.AsNoTracking().ToListAsync();
        foreach (IActionResult ok in results.Where(r => ((ObjectResult)r).StatusCode == 200))
        {
            string txn = JsonSerializer.SerializeToElement(((ObjectResult)ok).Value).GetProperty("transactionId").GetString()!;
            PromotionCheckout row = rows.Single(c => c.ProviderTransactionId == txn);
            (row.SupersededAt is null || row.ProviderCancelledAt is not null)
                .Should().BeTrue($"{txn} was handed out, so it must be live or already cancelled");
        }

        rows.Where(c => c.SupersededAt is not null).Should().OnlyContain(c => c.ProviderTransactionId != null && c.ProviderCancelledAt != null,
            "every superseded reservation's transaction is cancelled before the requests return");
        rows.Count(c => c.ProviderTransactionId != null && c.ProviderCancelledAt == null && !pg.Provider.CancelCalls.Contains(c.ProviderTransactionId))
            .Should().BeLessThanOrEqualTo(1, "at most one payable discounted transaction per buyer");
    }

    // ------------------------------------------------------------------ S-11 (billing-concurrency-followups): a bounded checkout lock

    private static Task<Guid> PlanDiscountAsync(Pg pg, int? cap = null) => pg.PromotionAsync(PromotionKind.PlanDiscount, p =>
    {
        p.DiscountType = PromotionDiscountType.Percentage;
        p.DiscountAmount = "20";
        p.DurationKind = PromotionDuration.Once;
        p.TargetPriceIdsJson = """["pri_creator"]""";
        p.ProviderDiscountId = "dsc_plan";
        p.MaxRedemptions = cap;
    });

    private static async Task<(Guid user, Guid org)[]> BuyersAsync(Pg pg, int n)
    {
        (Guid user, Guid org)[] buyers = new (Guid, Guid)[n];
        for (int i = 0; i < n; i++)
        {
            Guid user = await pg.UserAsync();
            buyers[i] = (user, await pg.OrgAsync(user));
        }

        return buyers;
    }

    [Fact]
    public async Task Thirty_concurrent_checkouts_of_one_promotion_on_a_5_connection_pool_never_throw()
    {
        // A 5-connection pool whose opens give up after 2 s: checkouts that queue on the promotion lock
        // while holding a pooled connection exhaust it, and the rest would throw (a 500).
        await using Pg? pg = await Pg.CreateAsync(maxPoolSize: 5, poolTimeoutSeconds: 2);
        if (pg is null) return;

        const int n = 30;
        const int cap = 10;
        Guid discount = await PlanDiscountAsync(pg, cap);
        (Guid user, Guid org)[] buyers = await BuyersAsync(pg, n);

        IActionResult[] results;
        // Another replica holds the promotion for 3 s while the burst arrives.
        IAsyncDisposable holder = await pg.HoldPromotionLockAsync(discount);
        Task release = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => holder.DisposeAsync().AsTask()).Unwrap();
        try
        {
            results = await RaceAsync(n, async i =>
            {
                await using InferenceApiDbContext db = pg.NewDb();
                return await pg.Billing(db, buyers[i].user, buyers[i].org).Checkout(new CheckoutRequestDto("creator", null, null), CancellationToken.None);
            });
        }
        finally
        {
            await release;
        }

        results.Should().AllSatisfy(r =>
        {
            int? status = ((ObjectResult)r).StatusCode;
            status.Should().BeOneOf(200, 429);
            if (status == 429)
            {
                ErrorOf(r).Should().Be("checkout_busy");
            }
        });
        int succeeded = results.Count(r => ((ObjectResult)r).StatusCode == 200);
        succeeded.Should().BeGreaterThan(0);
        int discounted = results.Count(r => ((ObjectResult)r).StatusCode == 200
            && JsonSerializer.SerializeToElement(((ObjectResult)r).Value).GetProperty("discount").ValueKind == JsonValueKind.Object);
        await using InferenceApiDbContext check = pg.NewDb();
        int open = await check.PromotionCheckouts.CountAsync(c => c.DiscountPromotionId == discount && c.SupersededAt == null);
        open.Should().Be(discounted, "every discounted checkout is exactly one open reservation");
        open.Should().Be(Math.Min(cap, succeeded), "the global cap stays exact");
    }

    [Fact]
    public async Task A_checkout_waiting_past_the_lock_wait_returns_checkout_busy_not_500()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid discount = await PlanDiscountAsync(pg);
        (Guid user, Guid org) = (await BuyersAsync(pg, 1))[0];
        PromotionOptions options = new() { CheckoutLockWaitMilliseconds = 1000 };

        IActionResult result;
        System.Diagnostics.Stopwatch elapsed;
        await using (await pg.HoldPromotionLockAsync(discount))
        {
            await using InferenceApiDbContext db = pg.NewDb();
            elapsed = System.Diagnostics.Stopwatch.StartNew();
            // The bound the fix must keep, well inside the guard that stops a hung checkout hanging the suite.
            result = await pg.Billing(db, user, org, options).Checkout(new CheckoutRequestDto("creator", null, null), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            elapsed.Stop();
        }

        ((ObjectResult)result).StatusCode.Should().Be(429);
        ErrorOf(result).Should().Be("checkout_busy");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(options.CheckoutLockWaitMilliseconds + 1000));
        await using InferenceApiDbContext check = pg.NewDb();
        (await check.PromotionCheckouts.CountAsync()).Should().Be(0, "a busy checkout inserts no reservation");
    }

    [Fact]
    public async Task Waiting_checkouts_in_one_process_hold_no_pooled_connection()
    {
        // A pool big enough that, unbounded, every waiter would hold a connection of its own.
        await using Pg? pg = await Pg.CreateAsync(maxPoolSize: 12);
        if (pg is null) return;

        const int n = 10;
        Guid discount = await PlanDiscountAsync(pg);
        (Guid user, Guid org)[] buyers = await BuyersAsync(pg, n);
        CheckoutTarget target = new("pri_creator", null);
        CheckoutPromotions[] promos = new CheckoutPromotions[n];
        for (int i = 0; i < n; i++)
        {
            await using InferenceApiDbContext db = pg.NewDb();
            promos[i] = (await pg.Service(db).ResolveCheckoutPromotionAsync(buyers[i].org, buyers[i].user, target, null, CancellationToken.None)).Value!;
            promos[i].Discount!.Id.Should().Be(discount);
        }

        PromotionOptions options = new() { CheckoutLockWaitMilliseconds = 30_000 };
        long busyWhileWaiting;
        Task<PromotionOutcome<CheckoutReservation>>[] checkouts;
        await using (IAsyncDisposable holder = await pg.HoldPromotionLockAsync(discount))
        {
            checkouts = Enumerable.Range(0, n).Select(i => Task.Run(async () =>
            {
                await using InferenceApiDbContext db = pg.NewDb();
                return await pg.Service(db, options).ReserveCheckoutAsync(buyers[i].org, buyers[i].user, target, promos[i], CancellationToken.None);
            })).ToArray();

            // Wait until a checkout queues on the database lock, then give the rest time to queue too.
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (await pg.LockWaitersAsync() == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
            busyWhileWaiting = await pg.BusyBackendsAsync();
        }

        PromotionOutcome<CheckoutReservation>[] outcomes = await Task.WhenAll(checkouts).WaitAsync(TimeSpan.FromSeconds(30));

        busyWhileWaiting.Should().BeLessThanOrEqualTo(2, "only one checkout waits in the database (plus the side holder); the rest wait in-process");
        outcomes.Should().AllSatisfy(o => o.Error.Should().BeNull());
        await using InferenceApiDbContext check = pg.NewDb();
        (await check.PromotionCheckouts.CountAsync(c => c.DiscountPromotionId == discount && c.SupersededAt == null)).Should().Be(n);
    }

    /// <summary>The checkout service over the real Postgres C2/C5 counters, as Program.cs wires it.</summary>
    private static PromotionService CountedService(Pg pg, InferenceApiDbContext db, PromotionOptions options)
        => new(db, [pg.Provider], pg.Limiter, NullLogger<PromotionService>.Instance, TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(options), new PostgresMailRateCounterStore(db));

    /// <summary>The C5 count of one bucket in <c>mail_rate_counters</c>, summed over its windows.</summary>
    private static async Task<long> CountedCheckoutsAsync(Pg pg, string bucket)
    {
        await using InferenceApiDbContext db = pg.NewDb();
        return await db.Database.SqlQuery<long>($"SELECT COALESCE(SUM(count), 0)::bigint AS \"Value\" FROM mail_rate_counters WHERE bucket = {bucket}")
            .SingleAsync();
    }

    [Fact]
    public async Task A_checkout_that_times_out_on_the_database_lock_is_busy_and_not_counted()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid discount = await PlanDiscountAsync(pg);
        (Guid user, Guid org) = (await BuyersAsync(pg, 1))[0];
        CheckoutTarget target = new("pri_creator", null);
        PromotionOptions options = new() { CheckoutLockWaitMilliseconds = 500, CheckoutsPerUserPerHour = 1 };
        string bucket = PromotionService.CheckoutBucket(user);

        PromotionOutcome<CheckoutPromotions> resolved;
        await using (InferenceApiDbContext db = pg.NewDb())
        {
            resolved = await pg.Service(db).ResolveCheckoutPromotionAsync(org, user, target, null, CancellationToken.None);
        }

        // Another replica holds the promotion's database lock. The in-process gate is free, so the wait is in Postgres.
        PromotionOutcome<CheckoutReservation> busy;
        await using (await pg.HoldPromotionLockAsync(discount))
        {
            await using InferenceApiDbContext db = pg.NewDb();
            busy = await CountedService(pg, db, options).ReserveCheckoutAsync(org, user, target, resolved.Value!, CancellationToken.None);
        }

        busy.Error.Should().Be(PromotionErrors.CheckoutBusy);
        (await CountedCheckoutsAsync(pg, bucket)).Should().Be(0, "a checkout answered busy is not counted");

        PromotionOutcome<CheckoutReservation> allowed;
        await using (InferenceApiDbContext db = pg.NewDb())
        {
            allowed = await CountedService(pg, db, options).ReserveCheckoutAsync(org, user, target, resolved.Value!, CancellationToken.None);
        }

        allowed.Error.Should().BeNull("the one checkout the hour allows was not spent by the busy attempt");
        (await CountedCheckoutsAsync(pg, bucket)).Should().Be(1);
    }

    [Fact]
    public async Task A_checkout_waiting_on_the_hourly_counter_lock_past_the_wait_is_busy_and_not_counted()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid discount = await PlanDiscountAsync(pg);
        (Guid user, Guid org) = (await BuyersAsync(pg, 1))[0];
        CheckoutTarget target = new("pri_creator", null);
        PromotionOptions options = new() { CheckoutLockWaitMilliseconds = 500 };
        string bucket = PromotionService.CheckoutBucket(user);

        PromotionOutcome<CheckoutPromotions> resolved;
        await using (InferenceApiDbContext db = pg.NewDb())
        {
            resolved = await pg.Service(db).ResolveCheckoutPromotionAsync(org, user, target, null, CancellationToken.None);
        }

        // Another replica holds this buyer's hourly counter lock (the C5 bucket). The promotion locks are free.
        PromotionOutcome<CheckoutReservation> busy;
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        await using (await pg.HoldAdvisoryLockAsync(bucket))
        {
            await using InferenceApiDbContext db = pg.NewDb();
            busy = await CountedService(pg, db, options).ReserveCheckoutAsync(org, user, target, resolved.Value!, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            elapsed.Stop();
        }

        busy.Error.Should().Be(PromotionErrors.CheckoutBusy);
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(options.CheckoutLockWaitMilliseconds + 1000));
        (await CountedCheckoutsAsync(pg, bucket)).Should().Be(0, "a checkout answered busy is not counted");

        PromotionOutcome<CheckoutReservation> allowed;
        await using (InferenceApiDbContext db = pg.NewDb())
        {
            allowed = await CountedService(pg, db, options).ReserveCheckoutAsync(org, user, target, resolved.Value!, CancellationToken.None);
        }

        allowed.Error.Should().BeNull("the lock is released and the checkout proceeds");
        (await CountedCheckoutsAsync(pg, bucket)).Should().Be(1);
    }

    [Fact]
    public async Task A_ten_checkout_burst_past_a_300_ms_lock_wait_answers_200_or_checkout_busy_never_500()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        const int n = 10;
        Guid discount = await PlanDiscountAsync(pg);
        (Guid user, Guid org)[] buyers = await BuyersAsync(pg, n);
        PromotionOptions options = new() { CheckoutLockWaitMilliseconds = 300 };

        IActionResult[] results;
        // Another replica holds the promotion for 2 s while the burst arrives: every wait is 300 ms, so none can win.
        IAsyncDisposable holder = await pg.HoldPromotionLockAsync(discount);
        Task release = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => holder.DisposeAsync().AsTask()).Unwrap();
        try
        {
            results = await RaceAsync(n, async i =>
            {
                await using InferenceApiDbContext db = pg.NewDb();
                return await pg.Billing(db, buyers[i].user, buyers[i].org, options).Checkout(new CheckoutRequestDto("creator", null, null), CancellationToken.None);
            });
        }
        finally
        {
            await release;
        }

        results.Should().AllSatisfy(r =>
        {
            int? status = ((ObjectResult)r).StatusCode;
            status.Should().BeOneOf(200, 429);
            if (status == 429)
            {
                ErrorOf(r).Should().Be("checkout_busy");
            }
        });
        results.Count(r => ((ObjectResult)r).StatusCode == 429).Should().BeGreaterThan(0, "at least one checkout waits past its 300 ms lock wait");
        int succeeded = results.Count(r => ((ObjectResult)r).StatusCode == 200);
        await using InferenceApiDbContext check = pg.NewDb();
        (await check.PromotionCheckouts.CountAsync(c => c.DiscountPromotionId == discount && c.SupersededAt == null)).Should().Be(succeeded);
    }
}
