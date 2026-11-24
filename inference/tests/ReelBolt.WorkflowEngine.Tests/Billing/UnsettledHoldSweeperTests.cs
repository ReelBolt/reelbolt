#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.WorkflowEngine.Services;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// S-3: the sweep closes every hold a step left open once the step (or its run) is over: a completed
/// step is SETTLED (at what it recorded, else at least the hold), a failed one is closed by its class,
/// and nothing young or still running is touched.
/// </summary>
public sealed class UnsettledHoldSweeperTests : IDisposable
{
    private readonly UnsettledHoldTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    /// <summary>A sweeper whose clock is an hour ahead, so every hold made now is old enough.</summary>
    private UnsettledHoldSweeper Sweeper(TimeSpan? ahead = null, int maxPerSweep = 100, ICreditGate? gate = null, ExecutionLeaseService? leases = null)
        => new(
            _kit.Factory,
            gate ?? _kit.Gate(),
            Options.Create(new SettleSweepOptions { MinAgeSeconds = 600, MaxPerSweep = maxPerSweep }),
            Options.Create(new CreditGateOptions { Enabled = true }),
            NullLogger<UnsettledHoldSweeper>.Instance,
            new FixedTime(DateTimeOffset.UtcNow + (ahead ?? TimeSpan.FromHours(1))),
            leases);

    /// <summary>A lease service for this process, with no engine behind it: only its tracked ids are used.</summary>
    private static ExecutionLeaseService LeasesOf(string instance = "replica-a")
        => new(
            Mock.Of<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowEngine:InstanceId"] = instance }).Build(),
            NullLogger<ExecutionLeaseService>.Instance);

    private async Task<IReadOnlyList<CreditLedgerEntry>> ClosingOf(Guid reserve)
        => (await _kit.EntriesAsync()).Where(e => e.RefEntryId == reserve).ToList();

    [Fact]
    public async Task Sweep_settles_open_hold_of_completed_step_at_recorded_amount()
    {
        await _kit.GrantAsync(100_000);
        (_, _, Guid reserve) = await _kit.HoldAsync(StepStatus.Completed, ExecutionStatus.Passed, unsettledMillis: 4_500);

        (await Sweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        CreditLedgerEntry settle = (await ClosingOf(reserve)).Should().ContainSingle().Subject;
        settle.Kind.Should().Be(CreditLedgerEntryKind.Settle);
        settle.IdempotencyKey.Should().Be($"gate-settle:{reserve:N}");
        settle.BalanceDeltaMillis.Should().Be(-4_500);
        (await _kit.BalanceAsync()).ReservedMillis.Should().Be(0);
    }

    [Fact]
    public async Task Sweep_without_recorded_amount_charges_at_least_the_hold()
    {
        await _kit.GrantAsync(100_000);
        (Guid exec, Guid step, Guid reserve) = await _kit.HoldAsync(StepStatus.Completed, ExecutionStatus.Passed);
        await _kit.RecordUsageEventAsync(exec, step, 3_000);

        (await Sweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        CreditLedgerEntry settle = (await ClosingOf(reserve)).Should().ContainSingle().Subject;
        settle.Kind.Should().Be(CreditLedgerEntryKind.Settle);
        settle.BalanceDeltaMillis.Should().Be(-UnsettledHoldTestKit.HoldMillis, "the actual is unknown, so the hold is the floor");
    }

    [Fact]
    public async Task Sweep_without_recorded_amount_charges_metered_usage_above_the_hold()
    {
        await _kit.GrantAsync(100_000);
        (Guid exec, Guid step, Guid reserve) = await _kit.HoldAsync(StepStatus.Completed, ExecutionStatus.Passed);
        await _kit.RecordUsageEventAsync(exec, step, 15_000);
        await _kit.RecordUsageEventAsync(exec, step, 10_000);

        await Sweeper().SweepOnceAsync(CancellationToken.None);

        (await ClosingOf(reserve)).Single().BalanceDeltaMillis.Should().Be(-25_000);
    }

    [Fact]
    public async Task Sweep_skips_holds_of_running_steps_and_young_holds()
    {
        await _kit.GrantAsync(100_000);
        (_, _, Guid running) = await _kit.HoldAsync(StepStatus.Running, ExecutionStatus.Running);
        (_, _, Guid completed) = await _kit.HoldAsync(StepStatus.Completed, ExecutionStatus.Passed, unsettledMillis: 1_000);

        (await Sweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(1, "only the completed hold is old enough an hour on");
        (await ClosingOf(running)).Should().BeEmpty();
        (await ClosingOf(completed)).Should().ContainSingle();

        // Now a fresh completed hold, seen by a sweeper whose clock is only a minute ahead.
        (_, _, Guid fresh) = await _kit.HoldAsync(StepStatus.Completed, ExecutionStatus.Passed, unsettledMillis: 1_000);
        (await Sweeper(TimeSpan.FromMinutes(1)).SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        (await ClosingOf(fresh)).Should().BeEmpty();
        (await ClosingOf(running)).Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_closes_running_step_hold_once_its_execution_is_over_only_past_the_ceiling()
    {
        await _kit.GrantAsync(100_000);
        (_, _, Guid orphan) = await _kit.HoldAsync(StepStatus.Running, ExecutionStatus.Failed);

        (await Sweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(0, "a Running step is not swept before the ceiling");
        (await Sweeper(TimeSpan.FromHours(49)).SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        CreditLedgerEntry release = (await ClosingOf(orphan)).Should().ContainSingle().Subject;
        release.Kind.Should().Be(CreditLedgerEntryKind.Release);
        release.Note.Should().Be(nameof(FailureClass.PlatformInternal));
    }

    [Fact]
    public async Task Sweep_keeps_the_hold_of_a_running_step_under_a_cancelled_execution_while_its_lease_is_live()
    {
        await _kit.GrantAsync(100_000);
        (_, _, Guid reserve) = await _kit.HoldAsync(StepStatus.Running, ExecutionStatus.Cancelled,
            leaseOwner: "replica-a", leaseExpiresAt: DateTime.UtcNow.AddDays(3));

        (await Sweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        (await Sweeper(TimeSpan.FromHours(49)).SweepOnceAsync(CancellationToken.None)).Should().Be(0);

        (await ClosingOf(reserve)).Should().BeEmpty();
        (await _kit.BalanceAsync()).ReservedMillis.Should().Be(UnsettledHoldTestKit.HoldMillis);
    }

    [Fact]
    public async Task Running_step_under_a_cancelled_execution_settles_normally_after_the_sweep_left_it()
    {
        await _kit.GrantAsync(100_000);
        (_, _, Guid reserve) = await _kit.HoldAsync(StepStatus.Running, ExecutionStatus.Cancelled,
            leaseOwner: "replica-a", leaseExpiresAt: DateTime.UtcNow.AddDays(3));
        (await Sweeper(TimeSpan.FromHours(49)).SweepOnceAsync(CancellationToken.None)).Should().Be(0);

        (await _kit.Gate().SettleAsync(reserve, 4_500)).Ok.Should().BeTrue();

        CreditLedgerEntry settle = (await ClosingOf(reserve)).Should().ContainSingle().Subject;
        settle.Kind.Should().Be(CreditLedgerEntryKind.Settle);
        settle.IdempotencyKey.Should().Be($"gate-settle:{reserve:N}");
        settle.BalanceDeltaMillis.Should().Be(-4_500);
        (await _kit.EntriesAsync()).Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Release || e.Kind == CreditLedgerEntryKind.Refund);
    }

    [Fact]
    public async Task Sweep_releases_a_running_step_hold_only_once_its_lease_lapsed_and_it_is_older_than_the_ceiling()
    {
        await _kit.GrantAsync(100_000);
        (_, _, Guid reserve) = await _kit.HoldAsync(StepStatus.Running, ExecutionStatus.Cancelled,
            leaseOwner: "replica-a", leaseExpiresAt: DateTime.UtcNow.AddHours(-1));

        (await Sweeper(TimeSpan.FromHours(47)).SweepOnceAsync(CancellationToken.None)).Should().Be(0, "not yet past the ceiling");
        (await Sweeper(TimeSpan.FromHours(49)).SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        CreditLedgerEntry release = (await ClosingOf(reserve)).Should().ContainSingle().Subject;
        release.Kind.Should().Be(CreditLedgerEntryKind.Release);
        release.Note.Should().Be(nameof(FailureClass.PlatformInternal));
    }

    [Fact]
    public async Task Sweep_treats_a_lease_lapsed_less_than_min_age_ago_as_live()
    {
        await _kit.GrantAsync(100_000);
        DateTime atSweep = DateTime.UtcNow.AddHours(49);
        (_, _, Guid recent) = await _kit.HoldAsync(StepStatus.Running, ExecutionStatus.Cancelled,
            leaseOwner: "replica-a", leaseExpiresAt: atSweep.AddMinutes(-5));
        (_, _, Guid stale) = await _kit.HoldAsync(StepStatus.Running, ExecutionStatus.Cancelled,
            leaseOwner: "replica-a", leaseExpiresAt: atSweep.AddMinutes(-11));

        (await Sweeper(TimeSpan.FromHours(49)).SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        (await ClosingOf(recent)).Should().BeEmpty("its lease lapsed under MinAgeSeconds ago");
        (await ClosingOf(stale)).Should().ContainSingle();
    }

    [Fact]
    public async Task Sweep_never_closes_a_hold_of_an_execution_this_process_is_running()
    {
        await _kit.GrantAsync(100_000);
        (Guid exec, _, Guid reserve) = await _kit.HoldAsync(StepStatus.Running, ExecutionStatus.Cancelled,
            leaseOwner: "replica-a", leaseExpiresAt: DateTime.UtcNow.AddHours(-1));
        ExecutionLeaseService leases = LeasesOf();
        leases.Track(exec);

        (await Sweeper(TimeSpan.FromHours(49), leases: leases).SweepOnceAsync(CancellationToken.None)).Should().Be(0);

        (await ClosingOf(reserve)).Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_that_loses_the_race_to_a_late_settle_leaves_the_charge_alone()
    {
        await _kit.GrantAsync(100_000);
        (_, _, Guid reserve) = await _kit.HoldAsync(StepStatus.Running, ExecutionStatus.Cancelled,
            leaseOwner: "replica-a", leaseExpiresAt: DateTime.UtcNow.AddHours(-1));
        CreditGate real = _kit.Gate();
        var gate = new Mock<ICreditGate>();
        gate.Setup(g => g.ReleaseOpenAsync(reserve, It.IsAny<FailureClass>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid id, FailureClass cls, long? recorded, CancellationToken ct) =>
            {
                // The step's own settle lands between the sweep's read and its close.
                (await real.SettleAsync(id, 4_500, ct)).Ok.Should().BeTrue();
                return await real.ReleaseOpenAsync(id, cls, recorded, ct);
            });

        (await Sweeper(TimeSpan.FromHours(49), gate: gate.Object).SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        CreditLedgerEntry settle = (await ClosingOf(reserve)).Should().ContainSingle().Subject;
        settle.Kind.Should().Be(CreditLedgerEntryKind.Settle);
        settle.BalanceDeltaMillis.Should().Be(-4_500);
        (await _kit.BalanceAsync()).BalanceMillis.Should().Be(95_500);
    }

    [Fact]
    public async Task Release_open_on_a_settled_hold_is_a_noop_success()
    {
        await _kit.GrantAsync(100_000);
        (_, _, Guid reserve) = await _kit.HoldAsync(StepStatus.Completed, ExecutionStatus.Passed, unsettledMillis: 4_500);
        (await _kit.Gate().SettleAsync(reserve, 4_500)).Ok.Should().BeTrue();
        int before = (await _kit.EntriesAsync()).Count;

        CreditSettlement again = await _kit.Gate().ReleaseOpenAsync(reserve, FailureClass.PlatformInternal, null);

        again.Ok.Should().BeTrue();
        again.ChargedMillis.Should().Be(0);
        (await _kit.EntriesAsync()).Count.Should().Be(before);
        (await _kit.BalanceAsync()).BalanceMillis.Should().Be(95_500);
    }

    [Fact]
    public async Task Sweep_settles_a_failed_user_class_step_with_no_recorded_amount_at_its_metered_usage()
    {
        await _kit.GrantAsync(100_000);
        (Guid exec, Guid step, Guid reserve) = await _kit.HoldAsync(StepStatus.Failed, ExecutionStatus.Failed, failureClass: FailureClass.UserInput);
        await _kit.RecordUsageEventAsync(exec, step, 3_000);
        await _kit.RecordUsageEventAsync(exec, step, 2_500);

        (await Sweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        CreditLedgerEntry settle = (await ClosingOf(reserve)).Should().ContainSingle().Subject;
        settle.Kind.Should().Be(CreditLedgerEntryKind.Settle);
        settle.BalanceDeltaMillis.Should().Be(-5_500);
    }

    [Fact]
    public async Task Sweep_settles_a_failed_user_class_step_with_unpriced_usage_charges_at_least_the_hold()
    {
        await _kit.GrantAsync(100_000);
        (Guid exec, Guid step, Guid reserve) = await _kit.HoldAsync(StepStatus.Failed, ExecutionStatus.Failed, failureClass: FailureClass.UserInput);
        await _kit.RecordUsageEventAsync(exec, step, 3_000, unpriced: true);
        await _kit.RecordUsageEventAsync(exec, step, 2_500, unpriced: true);

        (await Sweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        (await ClosingOf(reserve)).Single().BalanceDeltaMillis.Should().Be(-UnsettledHoldTestKit.HoldMillis);
    }

    [Fact]
    public async Task Sweep_releases_a_failed_user_class_step_with_no_usage()
    {
        await _kit.GrantAsync(100_000);
        (_, _, Guid reserve) = await _kit.HoldAsync(StepStatus.Failed, ExecutionStatus.Failed, failureClass: FailureClass.UserInput);

        (await Sweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        CreditLedgerEntry release = (await ClosingOf(reserve)).Should().ContainSingle().Subject;
        release.Kind.Should().Be(CreditLedgerEntryKind.Release);
    }

    [Fact]
    public async Task Sweep_releases_open_hold_of_failed_step_by_its_failure_class()
    {
        await _kit.GrantAsync(100_000);
        (_, _, Guid platform) = await _kit.HoldAsync(StepStatus.Failed, ExecutionStatus.Failed, unsettledMillis: 2_000, failureClass: FailureClass.PlatformInternal);
        (_, _, Guid user) = await _kit.HoldAsync(StepStatus.Failed, ExecutionStatus.Failed, unsettledMillis: 3_000, failureClass: FailureClass.UserInput);

        (await Sweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(2);

        CreditLedgerEntry refunded = (await ClosingOf(platform)).Should().ContainSingle().Subject;
        refunded.Kind.Should().Be(CreditLedgerEntryKind.Release);
        refunded.Note.Should().Be(nameof(FailureClass.PlatformInternal));

        CreditLedgerEntry kept = (await ClosingOf(user)).Should().ContainSingle().Subject;
        kept.Kind.Should().Be(CreditLedgerEntryKind.Settle);
        kept.BalanceDeltaMillis.Should().Be(-3_000);
        kept.Note.Should().Be(nameof(FailureClass.UserInput));

        CreditLedgerResult balance = await _kit.BalanceAsync();
        balance.BalanceMillis.Should().Be(97_000);
        balance.ReservedMillis.Should().Be(0);
    }

    [Fact]
    public async Task Sweep_on_closed_reserve_is_a_noop()
    {
        await _kit.GrantAsync(100_000);
        (_, _, Guid reserve) = await _kit.HoldAsync(StepStatus.Completed, ExecutionStatus.Passed, unsettledMillis: 4_500);
        (await _kit.Gate().SettleAsync(reserve, 4_500)).Ok.Should().BeTrue();
        int before = (await _kit.EntriesAsync()).Count;

        (await Sweeper().SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        CreditSettlement again = await _kit.Gate().SettleOpenAsync(reserve, 9_999);

        again.Ok.Should().BeTrue();
        again.ChargedMillis.Should().Be(0);
        (await _kit.EntriesAsync()).Count.Should().Be(before);
        (await _kit.BalanceAsync()).BalanceMillis.Should().Be(95_500);
    }

    [Fact]
    public async Task Sweep_closes_oldest_first_up_to_max_per_sweep_and_one_failure_never_stops_the_pass()
    {
        await _kit.GrantAsync(100_000);
        (_, _, Guid first) = await _kit.HoldAsync(StepStatus.Completed, ExecutionStatus.Passed, unsettledMillis: 1_000);
        (_, _, Guid second) = await _kit.HoldAsync(StepStatus.Completed, ExecutionStatus.Passed, unsettledMillis: 1_000);
        (_, _, Guid third) = await _kit.HoldAsync(StepStatus.Completed, ExecutionStatus.Passed, unsettledMillis: 1_000);

        CreditGate real = _kit.Gate();
        var gate = new Mock<ICreditGate>();
        gate.Setup(g => g.SettleOpenAsync(first, It.IsAny<long?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
        gate.Setup(g => g.SettleOpenAsync(It.Is<Guid>(id => id != first), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, long? recorded, CancellationToken ct) => real.SettleOpenAsync(id, recorded, ct));

        (await Sweeper(maxPerSweep: 2, gate: gate.Object).SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        (await ClosingOf(first)).Should().BeEmpty();
        (await ClosingOf(second)).Should().ContainSingle();
        (await ClosingOf(third)).Should().BeEmpty("the pass took only the two oldest");
    }

    [Fact]
    public async Task Sweep_does_nothing_while_billing_is_off()
    {
        await _kit.GrantAsync(100_000);
        (_, _, Guid reserve) = await _kit.HoldAsync(StepStatus.Completed, ExecutionStatus.Passed, unsettledMillis: 1_000);
        UnsettledHoldSweeper off = new(
            _kit.Factory, _kit.Gate(), Options.Create(new SettleSweepOptions()), Options.Create(new CreditGateOptions { Enabled = false }),
            NullLogger<UnsettledHoldSweeper>.Instance, new FixedTime(DateTimeOffset.UtcNow.AddHours(1)));

        (await off.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        (await ClosingOf(reserve)).Should().BeEmpty();
    }

    [Fact]
    public void Settle_sweep_defaults_match_the_documented_values()
    {
        SettleSweepOptions o = Resolve([]);
        o.Enabled.Should().BeTrue();
        o.IntervalSeconds.Should().Be(300);
        o.StartupDelaySeconds.Should().Be(60);
        o.MinAgeSeconds.Should().Be(600);
        o.MaxPerSweep.Should().Be(100);
        o.RunningStepCeilingSeconds.Should().Be(172800);
    }

    [Theory]
    [InlineData("IntervalSeconds", "0", 1, 86400)]
    [InlineData("IntervalSeconds", "86401", 1, 86400)]
    [InlineData("StartupDelaySeconds", "0", 1, 3600)]
    [InlineData("StartupDelaySeconds", "3601", 1, 3600)]
    [InlineData("MinAgeSeconds", "0", 1, 86400)]
    [InlineData("MinAgeSeconds", "86401", 1, 86400)]
    [InlineData("MaxPerSweep", "0", 1, 10000)]
    [InlineData("MaxPerSweep", "10001", 1, 10000)]
    [InlineData("RunningStepCeilingSeconds", "129599", 129600, 1209600)]
    [InlineData("RunningStepCeilingSeconds", "1209601", 129600, 1209600)]
    public void Settle_sweep_value_outside_its_range_fails_startup_naming_only_the_key_and_range(string key, string value, int min, int max)
    {
        Action startup = () => StartUp(new Dictionary<string, string?> { [$"Billing:SettleSweep:{key}"] = value });

        startup.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Be($"Billing:SettleSweep:{key} must be between {min} and {max}.");
    }

    [Theory]
    [InlineData("IntervalSeconds", "1")]
    [InlineData("IntervalSeconds", "86400")]
    [InlineData("StartupDelaySeconds", "1")]
    [InlineData("StartupDelaySeconds", "3600")]
    [InlineData("MinAgeSeconds", "1")]
    [InlineData("MinAgeSeconds", "86400")]
    [InlineData("MaxPerSweep", "1")]
    [InlineData("MaxPerSweep", "10000")]
    [InlineData("RunningStepCeilingSeconds", "129600")]
    [InlineData("RunningStepCeilingSeconds", "1209600")]
    public void Settle_sweep_values_at_their_bounds_are_accepted(string key, string value)
    {
        Action startup = () => StartUp(new Dictionary<string, string?> { [$"Billing:SettleSweep:{key}"] = value });

        startup.Should().NotThrow();
    }

    /// <summary>The host's own startup validation, the same one a real boot runs (see <c>IStartupValidator</c>).</summary>
    private static void StartUp(Dictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        ServiceCollection services = new();
        services.AddSettleSweepOptions(configuration);
        using ServiceProvider sp = services.BuildServiceProvider();
        sp.GetRequiredService<IStartupValidator>().Validate();
    }

    private static SettleSweepOptions Resolve(Dictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        ServiceCollection services = new();
        services.AddSettleSweepOptions(configuration);
        using ServiceProvider sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IOptions<SettleSweepOptions>>().Value;
    }
}
