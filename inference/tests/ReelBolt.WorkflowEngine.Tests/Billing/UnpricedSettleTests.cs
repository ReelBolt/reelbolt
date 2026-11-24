#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The fail-closed pricing rule on the engine's step settle: a step that metered any PLATFORM unit it could not price is charged at least
/// its hold (max(metered, hold)), never the understated metered sum, whether the unpriced unit is known in-process (the
/// step's own usage scope) or already persisted (<c>usage_events.unpriced</c> on the step result). Steps without
/// unpriced usage settle exactly as before.
/// </summary>
public sealed class UnpricedSettleTests : IDisposable
{
    private sealed class Factory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:;Foreign Keys=False");
    private readonly Factory _factory;
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _exec = Guid.NewGuid();

    public UnpricedSettleTests()
    {
        _connection.Open();
        using (InferenceApiDbContext owner = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options))
        {
            owner.Database.EnsureCreated();
        }

        _factory = new Factory(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_connection).Options);
    }

    public void Dispose() => _connection.Dispose();

    private CreditGate Gate() => new(_factory, Options.Create(new CreditGateOptions { Enabled = true }), NullLogger<CreditGate>.Instance);

    private async Task<long> BalanceAsync()
    {
        await using WorkflowEngineDbContext db = _factory.CreateDbContext();
        return (await new CreditLedger(db).GetBalanceAsync(_org)).BalanceMillis;
    }

    private async Task<CreditReservation> ReserveAsync(CreditGate gate, Guid step, long hold)
    {
        await using (WorkflowEngineDbContext db = _factory.CreateDbContext())
        {
            (await new CreditLedger(db).GrantAsync(_org, CreditGrantSource.Plan, 100_000, null, Guid.NewGuid().ToString("N"))).Ok.Should().BeTrue();
        }

        return await gate.ReserveAsync(_org, _exec, step, hold);
    }

    /// <summary>Meters one platform unit under the current scope with no price at all (the unpriced branch).</summary>
    private static async Task MeterUnpricedAsync()
    {
        UsageRecorder recorder = new(new EmptyPriceService(), new DiscardSink(), NullLogger<UsageRecorder>.Instance);
        (await recorder.RecordAsync(new UsageRecordRequest(
            UsageKind.LlmInputTokens, 1_000, Guid.NewGuid(), InferenceProviderKind.OpenAICompatible, "unpriced-model",
            UsageBillingMode.Platform))).Should().Be(0);
    }

    [Fact]
    public async Task A_step_whose_scope_metered_an_unpriced_platform_unit_is_charged_at_least_its_hold()
    {
        CreditGate gate = Gate();
        Guid step = Guid.NewGuid();
        CreditReservation r = await ReserveAsync(gate, step, 4_000);
        long before = await BalanceAsync();

        CreditSettlement s;
        using (UsageScope.Begin(_org, null, _exec, step, null, UsageSource.Workflow))
        {
            await MeterUnpricedAsync();
            s = await gate.SettleAsync(r.ReservationId, UsageScope.RunningCreditsMillis);
        }

        s.Ok.Should().BeTrue();
        s.ChargedMillis.Should().Be(4_000, "an unpriced unit is never free: the hold is the floor");
        (before - await BalanceAsync()).Should().Be(4_000);
    }

    [Fact]
    public async Task A_step_with_a_persisted_unpriced_usage_row_is_charged_at_least_its_hold()
    {
        CreditGate gate = Gate();
        Guid step = Guid.NewGuid();
        CreditReservation r = await ReserveAsync(gate, step, 3_000);
        await using (WorkflowEngineDbContext db = _factory.CreateDbContext())
        {
            db.UsageEvents.Add(new UsageEvent
            {
                Id = Guid.NewGuid(),
                OrganizationId = _org,
                ExecutionId = _exec,
                StepResultId = step,
                Source = UsageSource.Workflow,
                Kind = UsageKind.LlmInputTokens,
                Quantity = 10,
                ModelName = "unpriced-model",
                BillingMode = UsageBillingMode.Platform,
                CreditsMillis = 0,
                Unpriced = true,
                IdempotencyKey = $"{step:N}:1:in"
            });
            await db.SaveChangesAsync();
        }

        (await gate.SettleAsync(r.ReservationId, 500)).ChargedMillis.Should().Be(3_000);
    }

    [Fact]
    public async Task Metered_credits_above_the_hold_are_still_charged_in_full()
    {
        CreditGate gate = Gate();
        Guid step = Guid.NewGuid();
        CreditReservation r = await ReserveAsync(gate, step, 2_000);

        using (UsageScope.Begin(_org, null, _exec, step, null, UsageSource.Workflow))
        {
            await MeterUnpricedAsync();
            (await gate.SettleAsync(r.ReservationId, 2_300)).ChargedMillis.Should().Be(2_300);
        }
    }

    [Fact]
    public async Task A_user_caused_failure_with_unpriced_usage_keeps_at_least_the_hold()
    {
        CreditGate gate = Gate();
        Guid step = Guid.NewGuid();
        CreditReservation r = await ReserveAsync(gate, step, 2_500);

        using (UsageScope.Begin(_org, null, _exec, step, null, UsageSource.Workflow))
        {
            await MeterUnpricedAsync();
            (await gate.ReleaseAsync(r.ReservationId, FailureClass.UserInput, 100)).ChargedMillis.Should().Be(2_500);
        }
    }

    [Fact]
    public async Task A_step_without_unpriced_usage_settles_at_what_it_metered()
    {
        CreditGate gate = Gate();
        Guid step = Guid.NewGuid();
        CreditReservation r = await ReserveAsync(gate, step, 4_000);

        using (UsageScope.Begin(_org, null, _exec, step, null, UsageSource.Workflow))
        {
            (await gate.SettleAsync(r.ReservationId, 1_200)).ChargedMillis.Should().Be(1_200);
        }
    }
}
