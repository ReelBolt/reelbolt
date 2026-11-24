using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ApiKeys;

/// <summary>Per-key credit caps: window math, the ledger's locked reserve, the engine gate, the API preflight and the dashboard usage.</summary>
[Collection("Postgres")]
public sealed class ApiKeyCapsTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero); // a Wednesday
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class EngineFactory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:;Foreign Keys=False");
    private readonly Clock _clock = new();
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _key = Guid.NewGuid();

    public ApiKeyCapsTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = _org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.SaveChanges();
        SeedKey(_key, daily: 10_000);
        Grant(1_000_000);
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb() => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private void SeedKey(Guid id, long? daily = null, long? weekly = null, long? monthly = null, DateTime? expires = null, DateTime? revoked = null)
    {
        using InferenceApiDbContext db = NewDb();
        db.ApiKeys.Add(new ApiKey
        {
            Id = id,
            OrganizationId = _org,
            CreatedByUserId = Guid.NewGuid(),
            ServiceUserId = Guid.NewGuid(),
            Name = "k",
            KeyId = id.ToString("N")[..12],
            SecretHash = "h",
            LastFour = "abcd",
            Scopes = ["runs.read"],
            DailyCapMillis = daily,
            WeeklyCapMillis = weekly,
            MonthlyCapMillis = monthly,
            ExpiresAt = expires,
            RevokedAt = revoked
        });
        db.SaveChanges();
    }

    private void Grant(long millis)
    {
        using InferenceApiDbContext db = NewDb();
        new CreditLedger(db, _clock).GrantAsync(_org, CreditGrantSource.Plan, millis, null, Guid.NewGuid().ToString("N")).GetAwaiter().GetResult().Ok.Should().BeTrue();
    }

    private async Task<T> L<T>(Func<CreditLedger, Task<T>> action)
    {
        await using InferenceApiDbContext db = NewDb();
        return await action(new CreditLedger(db, _clock));
    }

    private static string K() => Guid.NewGuid().ToString("N");

    private Task<CreditLedgerResult> Reserve(long millis, Guid? key = null, string? idem = null)
        => L(l => l.ReserveAsync(_org, millis, idem ?? K(), new CreditLedgerContext(ApiKeyId: key ?? _key)));

    private List<ApiKeySpend> Spends() { using InferenceApiDbContext db = NewDb(); return db.ApiKeySpends.AsNoTracking().ToList(); }

    // ---------------------------------------------------------------- window math

    [Theory]
    [InlineData("2026-11-04T12:00:00Z", "2026-11-04", "2026-11-02", "2026-11-01")] // Wednesday
    [InlineData("2026-11-02T00:00:00Z", "2026-11-02", "2026-11-02", "2026-11-01")] // Monday midnight is already the new week
    [InlineData("2026-11-01T23:59:59Z", "2026-11-01", "2026-10-26", "2026-11-01")] // Sunday belongs to the week of the Monday before
    [InlineData("2026-12-31T23:59:59Z", "2026-12-31", "2026-12-28", "2026-12-01")] // year end
    [InlineData("2028-02-29T10:00:00Z", "2028-02-29", "2028-02-28", "2028-02-01")] // leap day
    public void Window_starts(string now, string day, string week, string month)
    {
        DateTime n = DateTime.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        ApiKeyBudgetWindows.WindowStart(ApiKeyBudgetWindow.Daily, n).Should().Be(DateTime.Parse(day + "T00:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        ApiKeyBudgetWindows.WindowStart(ApiKeyBudgetWindow.Weekly, n).Should().Be(DateTime.Parse(week + "T00:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        ApiKeyBudgetWindows.WindowStart(ApiKeyBudgetWindow.Monthly, n).Should().Be(DateTime.Parse(month + "T00:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal));
    }

    [Fact]
    public void Window_ends_cross_month_year_and_leap_boundaries()
    {
        DateTime NowOf(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        ApiKeyBudgetWindows.ResetsAt(ApiKeyBudgetWindow.Daily, NowOf("2026-12-31T23:59:59Z")).Should().Be(NowOf("2027-01-01T00:00:00Z"));
        ApiKeyBudgetWindows.ResetsAt(ApiKeyBudgetWindow.Weekly, NowOf("2026-12-31T10:00:00Z")).Should().Be(NowOf("2027-01-04T00:00:00Z"));
        ApiKeyBudgetWindows.ResetsAt(ApiKeyBudgetWindow.Monthly, NowOf("2028-02-29T10:00:00Z")).Should().Be(NowOf("2028-03-01T00:00:00Z"));
        ApiKeyBudgetWindows.ResetsAt(ApiKeyBudgetWindow.Monthly, NowOf("2026-12-15T10:00:00Z")).Should().Be(NowOf("2027-01-01T00:00:00Z"));
    }

    // ---------------------------------------------------------------- ledger

    [Fact]
    public async Task Reserve_under_the_cap_writes_an_open_spend_row()
    {
        CreditLedgerResult r = await Reserve(4_000);
        r.Ok.Should().BeTrue();
        ApiKeySpend row = Spends().Should().ContainSingle().Subject;
        row.Status.Should().Be(ApiKeySpendStatus.Open);
        row.HeldMillis.Should().Be(4_000);
        row.ReservationEntryId.Should().Be(r.Entry!.Id);
        row.CreatedAt.Should().Be(_clock.Now.UtcDateTime);
    }

    [Fact]
    public async Task Reserve_over_the_cap_is_refused_with_the_window_and_writes_nothing()
    {
        (await Reserve(6_000)).Ok.Should().BeTrue();
        CreditLedgerResult r = await Reserve(5_000);

        r.Ok.Should().BeFalse();
        r.Error.Should().Be(CreditLedgerErrors.ApiKeyBudgetExceeded);
        r.ApiKeyBudget.Should().Be(new ApiKeyBudgetRefusal(ApiKeyBudgetWindow.Daily, 10_000, 6_000, new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc)));
        Spends().Should().ContainSingle();
        r.ReservedMillis.Should().Be(6_000);
        using InferenceApiDbContext db = NewDb();
        db.CreditLedgerEntries.Count(e => e.Kind == CreditLedgerEntryKind.Reserve).Should().Be(1);
    }

    [Fact]
    public async Task The_tightest_failing_window_is_reported_daily_before_weekly_before_monthly()
    {
        Guid k = Guid.NewGuid();
        SeedKey(k, daily: 50_000, weekly: 2_000, monthly: 3_000);
        CreditLedgerResult r = await Reserve(2_500, k);
        r.ApiKeyBudget!.Window.Should().Be(ApiKeyBudgetWindow.Weekly);
        r.ApiKeyBudget.ResetsAt.Should().Be(new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc));

        Guid m = Guid.NewGuid();
        SeedKey(m, monthly: 2_000);
        CreditLedgerResult month = await Reserve(2_500, m);
        month.ApiKeyBudget!.Window.Should().Be(ApiKeyBudgetWindow.Monthly);
        month.ApiKeyBudget.ResetsAt.Should().Be(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Settle_counts_the_actual_charge_and_release_counts_nothing()
    {
        CreditLedgerResult a = await Reserve(6_000);
        CreditLedgerResult b = await Reserve(4_000);
        (await Reserve(1)).Ok.Should().BeFalse(); // 10_000 held of 10_000

        (await L(l => l.SettleAsync(_org, a.Entry!.Id, 2_000, K()))).Ok.Should().BeTrue();
        (await L(l => l.ReleaseAsync(_org, b.Entry!.Id, K()))).Ok.Should().BeTrue();

        List<ApiKeySpend> rows = Spends();
        rows.Single(s => s.ReservationEntryId == a.Entry.Id).Should().Match<ApiKeySpend>(s => s.Status == ApiKeySpendStatus.Settled && s.ChargedMillis == 2_000 && s.ClosedAt != null);
        rows.Single(s => s.ReservationEntryId == b.Entry.Id).Should().Match<ApiKeySpend>(s => s.Status == ApiKeySpendStatus.Released && s.ChargedMillis == 0);

        // used = 2_000, so 8_000 is left: 8_000 fits, one more millicredit does not.
        (await Reserve(8_000)).Ok.Should().BeTrue();
        (await Reserve(1)).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task A_settle_over_its_hold_counts_what_it_charged()
    {
        CreditLedgerResult a = await Reserve(4_000);
        (await L(l => l.SettleAsync(_org, a.Entry!.Id, 9_000, K()))).Ok.Should().BeTrue();
        (await Reserve(1_500)).Ok.Should().BeFalse();
        (await Reserve(1_000)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task A_spend_counts_in_the_window_it_was_held_in()
    {
        (await Reserve(10_000)).Ok.Should().BeTrue();
        _clock.Now = _clock.Now.AddDays(1); // next day, same week and month
        (await Reserve(10_000)).Ok.Should().BeTrue(); // the daily window is fresh
        Guid k = Guid.NewGuid();
        SeedKey(k, weekly: 15_000);
        (await Reserve(10_000, k)).Ok.Should().BeTrue();
        (await Reserve(10_000, k)).ApiKeyBudget!.Window.Should().Be(ApiKeyBudgetWindow.Weekly);
    }

    [Fact]
    public async Task A_revoked_expired_or_missing_key_is_refused_mid_run()
    {
        Guid revoked = Guid.NewGuid(), expired = Guid.NewGuid();
        SeedKey(revoked, revoked: _clock.Now.UtcDateTime.AddMinutes(-1));
        SeedKey(expired, expires: _clock.Now.UtcDateTime.AddMinutes(-1));
        foreach (Guid id in new[] { revoked, expired, Guid.NewGuid() })
        {
            CreditLedgerResult r = await Reserve(1_000, id);
            r.Ok.Should().BeFalse();
            r.Error.Should().Be(CreditLedgerErrors.ApiKeyInactive);
        }

        Spends().Should().BeEmpty();
        // A key that never expires (null expires_at) and has no caps reserves freely.
        Guid free = Guid.NewGuid();
        SeedKey(free);
        (await Reserve(500_000, free)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task A_replayed_idempotency_key_writes_no_second_row()
    {
        string idem = K();
        CreditLedgerResult first = await Reserve(4_000, idem: idem);
        CreditLedgerResult again = await Reserve(4_000, idem: idem);
        again.Replayed.Should().BeTrue();
        again.Entry!.Id.Should().Be(first.Entry!.Id);
        Spends().Should().ContainSingle();
    }

    [Fact]
    public async Task Without_a_key_the_ledger_behaves_exactly_as_before()
    {
        CreditLedgerResult r = await L(l => l.ReserveAsync(_org, 500_000, K()));
        r.Ok.Should().BeTrue();
        Spends().Should().BeEmpty();
        (await L(l => l.SettleAsync(_org, r.Entry!.Id, 100, K()))).Ok.Should().BeTrue();
        Spends().Should().BeEmpty();
    }

    // ---------------------------------------------------------------- engine gate

    [Fact]
    public async Task The_engine_gate_refuses_a_capped_keys_step_with_the_right_code_and_class()
    {
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_connection).Options;
        CreditGate gate = new(new EngineFactory(options), Options.Create(new CreditGateOptions { Enabled = true }), NullLogger<CreditGate>.Instance, null, _clock);
        Guid exec = Guid.NewGuid();

        CreditReservation ok = await gate.ReserveAsync(_org, exec, Guid.NewGuid(), 6_000, apiKeyId: _key);
        ok.Allowed.Should().BeTrue();
        CreditReservation refused = await gate.ReserveAsync(_org, exec, Guid.NewGuid(), 6_000, apiKeyId: _key);

        refused.Allowed.Should().BeFalse();
        refused.Reason.Should().Be("API_KEY_BUDGET_EXCEEDED");
        WorkflowStep step = new() { StepOrder = 2, StepType = StepType.Agent };
        CreditRefusalException ex = CreditRefusalException.For(refused, step);
        ex.FailureClass.Should().Be(FailureClass.InsufficientCredits);
        ex.Code.Should().Be("API_KEY_BUDGET_EXCEEDED");
        ex.Message.Should().Contain("daily limit of 10 credits").And.Contain("2026-11-05 00:00 UTC").And.NotContain(_key.ToString());
        FailureClassifier.Classify(FailureClass.None, "API_KEY_BUDGET_EXCEEDED", null).Should().Be(FailureClass.InsufficientCredits);
        FailureClassifier.Classify(FailureClass.None, "API_KEY_INACTIVE", null).Should().Be(FailureClass.InsufficientCredits);

        // Without a key the same gate holds freely: nothing changed for runs a person started.
        (await gate.ReserveAsync(_org, exec, Guid.NewGuid(), 60_000)).Allowed.Should().BeTrue();
    }

    // ---------------------------------------------------------------- billing off

    [Fact]
    public async Task With_billing_off_a_capped_keys_run_is_not_refused()
    {
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_connection).Options;
        CreditGate gate = new(new EngineFactory(options), Options.Create(new CreditGateOptions { Enabled = false }), NullLogger<CreditGate>.Instance, null, _clock);
        // No holds exist with billing off, so the caps have nothing to enforce.
        for (int i = 0; i < 3; i++)
        {
            (await gate.ReserveAsync(_org, Guid.NewGuid(), Guid.NewGuid(), 9_000, apiKeyId: _key)).Allowed.Should().BeTrue();
        }

        Spends().Should().BeEmpty();
        IApiKeyUsageReader reader = new ApiKeyUsageReader(NewDb(), Options.Create(new BillingOptions { Enabled = false }));
        reader.CapsEnforced.Should().BeFalse();
        (await reader.ReadAsync([await NewDb().ApiKeys.AsNoTracking().FirstAsync()], _clock.Now.UtcDateTime, default)).Should().BeNull();
    }

    // ---------------------------------------------------------------- preflight

    private sealed class FakeUser(Guid org) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "t@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => org;
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool IsPlatformAdmin => false;
    }

    [Fact]
    public async Task Preflight_refuses_402_with_the_window_shape_and_passes_without_a_key()
    {
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = _org };
        await using InferenceApiDbContext db = NewDb();
        _clock.Now = DateTimeOffset.UtcNow;
        await Reserve(3_000);
        Mock<IWorkflowCostEstimator> estimator = new();
        estimator.SetupGet(e => e.BillingEnabled).Returns(true);
        WorkflowCostEstimate estimate = new(true, false, 0, 0, 8_000, []);
        estimator.Setup(e => e.PreflightAsync(It.IsAny<Guid?>(), It.IsAny<IReadOnlyList<WorkflowStep>>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowPreflightResult(WorkflowPreflightOutcome.Allowed, estimate, 1_000_000));
        Mock<IDesignBuildEstimateGate> designBuild = new();
        designBuild.Setup(g => g.CheckAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DesignBuildGateResult.NotApplicable);
        RunStartGate gate = new(db, new FakeUser(_org), null, estimator.Object, designBuild.Object);

        ActionResult? refused = await gate.RefuseAsync(project, Guid.NewGuid(), null, RunStartPricing.AllOf([]), null, default, apiKeyId: _key);
        ObjectResult result = refused.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(402);
        System.Text.Json.JsonElement body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
        body.GetProperty("code").GetString().Should().Be("API_KEY_BUDGET_EXCEEDED");
        body.GetProperty("window").GetString().Should().Be("daily");
        body.GetProperty("capCredits").GetDecimal().Should().Be(10m);
        body.GetProperty("usedCredits").GetDecimal().Should().Be(3m);
        body.GetProperty("requiredCredits").GetDecimal().Should().Be(8m);
        body.GetProperty("resetsAt").GetDateTime().Should().Be(DateTime.UtcNow.Date.AddDays(1));
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();

        (await gate.RefuseAsync(project, Guid.NewGuid(), null, RunStartPricing.AllOf([]), null, default)).Should().BeNull();
    }

    // ---------------------------------------------------------------- dashboard usage

    [Fact]
    public async Task The_dashboard_list_carries_usage_per_key_from_one_grouped_read()
    {
        Guid weekly = Guid.NewGuid();
        SeedKey(weekly, weekly: 100_000);
        CreditLedgerResult a = await Reserve(4_000);
        await Reserve(2_500, weekly);
        (await L(l => l.SettleAsync(_org, a.Entry!.Id, 3_000, K()))).Ok.Should().BeTrue();

        await using InferenceApiDbContext db = NewDb();
        ApiKeyUsageReader reader = new(db, Options.Create(new BillingOptions { Enabled = true }));
        List<ApiKey> keys = await db.ApiKeys.AsNoTracking().ToListAsync();
        Dictionary<Guid, ApiKeyUsageView> usage = (await reader.ReadAsync(keys, _clock.Now.UtcDateTime, default))!;

        usage[_key].Daily.Should().Be(new ApiKeyWindowView(10m, 3m, new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc)));
        usage[_key].Weekly.CapCredits.Should().BeNull();
        usage[_key].Weekly.UsedCredits.Should().Be(3m); // a window with no cap still reports use
        usage[_key].Monthly.UsedCredits.Should().Be(3m);
        usage[weekly].Weekly.Should().Be(new ApiKeyWindowView(100m, 2.5m, new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc)));
    }

    // ---------------------------------------------------------------- race (real Postgres)

    [PostgresFact]
    public async Task Twenty_parallel_reserves_against_a_daily_cap_admit_exactly_the_cap()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_caps_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            string connection = PostgresTestEnvironment.ForDatabase(baseConnection, dbName);
            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options;
            Guid org = Guid.NewGuid(), key = Guid.NewGuid(), serviceUser = Guid.NewGuid();
            await using (InferenceApiDbContext setup = new(options))
            {
                // The new tables have no migration yet (the lead generates it last), so build the schema from the model.
                await setup.Database.EnsureCreatedAsync();
                setup.Organizations.Add(new Organization { Id = org, Name = "o", Slug = "o-" + org.ToString("N"), Kind = OrganizationKind.Team });
                setup.ApplicationUsers.Add(new ApplicationUser { Id = serviceUser, Email = $"{serviceUser:N}@example.com", DisplayName = "k" });
                setup.ApiKeys.Add(new ApiKey
                {
                    Id = key,
                    OrganizationId = org,
                    CreatedByUserId = serviceUser,
                    ServiceUserId = serviceUser,
                    Name = "k",
                    KeyId = key.ToString("N")[..12],
                    SecretHash = "h",
                    LastFour = "abcd",
                    Scopes = ["runs.read"],
                    DailyCapMillis = 100_000
                });
                await setup.SaveChangesAsync();
                (await new CreditLedger(setup).GrantAsync(org, CreditGrantSource.Plan, 10_000_000, null, K())).Ok.Should().BeTrue();
            }

            CreditLedgerResult[] results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            {
                await using InferenceApiDbContext db = new(options);
                return await new CreditLedger(db).ReserveAsync(org, 10_000, K(), new CreditLedgerContext(ApiKeyId: key));
            }));

            results.Count(r => r.Ok).Should().Be(10);
            results.Where(r => !r.Ok).Should().OnlyContain(r => r.Error == CreditLedgerErrors.ApiKeyBudgetExceeded);
            await using InferenceApiDbContext verify = new(options);
            (await verify.ApiKeySpends.SumAsync(s => s.HeldMillis)).Should().BeLessThanOrEqualTo(100_000);
            (await verify.ApiKeySpends.CountAsync()).Should().Be(10);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
