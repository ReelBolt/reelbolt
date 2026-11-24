using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// F11 acceptance for the switch reader itself. The property these tests exist to hold is the one the
/// WP is named after: <b>a switch must not fail open</b>. An unreadable settings store is a paused
/// platform — not a default, not the previous answer, and never a stale "not paused" served past the
/// cache's ten seconds.
/// </summary>
public class PlatformSwitchesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_empty_table_is_a_running_platform()
    {
        var (switches, _, _) = Build();

        PlatformSwitchState state = await switches.GetAsync();

        state.Known.Should().BeTrue("the read succeeded; nothing is paused");
        state.ComputePaused.Should().BeFalse();
        state.PlatformInferencePaused.Should().BeFalse();
        state.DisabledOrganizations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_paused_row_pauses_its_own_scope_only()
    {
        var (switches, _, _) = Build(rows: [Row(PlatformSetting.ComputeKey)]);

        PlatformSwitchState state = await switches.GetAsync();

        state.ComputePaused.Should().BeTrue();
        state.PlatformInferencePaused.Should().BeFalse();
    }

    [Fact]
    public async Task A_recorded_resume_pauses_nothing()
    {
        // A row with paused=false is a deliberate "I turned this back on", kept for its audit trail.
        // Treating a row's mere existence as a pause would make resuming impossible.
        var (switches, _, _) = Build(rows: [Row(PlatformSetting.InferenceKey, paused: false)]);

        PlatformSwitchState state = await switches.GetAsync();

        state.PlatformInferencePaused.Should().BeFalse();
    }

    [Fact]
    public async Task An_organisation_row_disables_that_organisation_only()
    {
        Guid disabled = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        var (switches, _, _) = Build(rows: [Row(PlatformSetting.OrganizationKey(disabled))]);

        PlatformSwitchState state = await switches.GetAsync();

        state.IsOrganizationDisabled(disabled).Should().BeTrue();
        state.IsOrganizationDisabled(other).Should().BeFalse();
        state.ComputePaused.Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_key_neither_pauses_nor_fails_the_read()
    {
        // A later work package will add keys this build does not know. Refusing to read the whole
        // table because of one of them would turn a forward-compatible deploy into a platform outage.
        var (switches, _, _) = Build(rows: [Row("some-later-feature"), Row(PlatformSetting.ComputeKey)]);

        PlatformSwitchState state = await switches.GetAsync();

        state.Known.Should().BeTrue();
        state.ComputePaused.Should().BeTrue();
    }

    [Fact]
    public async Task An_unreadable_store_is_a_paused_platform()
    {
        var (switches, _, _) = Build(reads: () => throw new InvalidOperationException("relation \"platform_settings\" does not exist"));

        PlatformSwitchState state = await switches.GetAsync();

        state.Known.Should().BeFalse();
        state.ComputePaused.Should().BeTrue("an unreadable store must never be read as 'nothing is paused'");
        state.PlatformInferencePaused.Should().BeTrue();
    }

    [Fact]
    public async Task A_cold_process_that_cannot_read_the_store_pauses_before_its_first_read()
    {
        // The state is initialized to Unknown, not to a default. This asserts the initialization
        // rather than the failure path: a store that is down at startup must not admit work.
        var (switches, store, _) = Build();

        store.Throw = true;

        (await switches.GetAsync()).ComputePaused.Should().BeTrue();
    }

    [Fact]
    public async Task A_successful_read_is_served_from_cache_for_ten_seconds()
    {
        var (switches, store, clock) = Build(rows: [Row(PlatformSetting.ComputeKey)]);

        await switches.GetAsync();
        clock.Advance(TimeSpan.FromSeconds(9));
        await switches.GetAsync();

        store.Reads.Should().Be(1, "the value is cached for its whole TTL");
    }

    [Fact]
    public async Task A_stale_not_paused_value_is_never_served_after_the_store_goes_away()
    {
        // THE test for the hazard this WP names: an operator turns the switch on, and a stale cache
        // would keep serving the old answer. Here the store is READABLE and says "not paused", then
        // disappears. Past the TTL the answer must be paused anyway.
        var (switches, store, clock) = Build();

        (await switches.GetAsync()).ComputePaused.Should().BeFalse();

        store.Throw = true;
        clock.Advance(TimeSpan.FromSeconds(11));

        PlatformSwitchState state = await switches.GetAsync();
        state.ComputePaused.Should().BeTrue("the cached value expires; a failed refresh must not extend it");
        state.Known.Should().BeFalse();
    }

    [Fact]
    public async Task A_resumed_platform_is_visible_within_the_cache_ttl()
    {
        // The other direction of the same property: flipping a switch OFF takes effect too, and only
        // after the TTL. Without this, "we don't serve stale values" could be satisfied by never
        // re-reading at all.
        var (switches, store, clock) = Build(rows: [Row(PlatformSetting.ComputeKey)]);

        (await switches.GetAsync()).ComputePaused.Should().BeTrue();

        store.Rows = [];
        clock.Advance(TimeSpan.FromSeconds(11));

        (await switches.GetAsync()).ComputePaused.Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_callers_share_one_read()
    {
        var (switches, store, _) = Build(rows: [Row(PlatformSetting.ComputeKey)]);

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => switches.GetAsync().AsTask()));

        store.Reads.Should().Be(1, "a stampede of admissions must not become a stampede of queries");
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_into_a_paused_platform()
    {
        var (switches, _, _) = Build();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await switches.GetAsync(cts.Token));
    }

    // --- harness ---------------------------------------------------------------------------------

    private static PlatformSetting Row(string key, bool paused = true) => new() { Key = key, Paused = paused };

    private static (PlatformSwitches Switches, FakeSettingsStore Store, FakeClock Clock) Build(
        IEnumerable<PlatformSetting>? rows = null, Func<Exception>? reads = null)
    {
        var store = new FakeSettingsStore { Rows = rows?.ToList() ?? [], Throw = reads is not null };
        var clock = new FakeClock(T0);
        var switches = new PlatformSwitches(
            new FakeDbContextFactory(store), NullLogger<PlatformSwitches>.Instance, clock);
        return (switches, store, clock);
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Stands in for the platform_settings table: rows, a read counter and a way to fail.</summary>
    private sealed class FakeSettingsStore
    {
        public List<PlatformSetting> Rows { get; set; } = [];

        public bool Throw { get; set; }

        public int Reads { get; private set; }

        public List<PlatformSetting> Read()
        {
            Reads++;
            if (Throw)
            {
                throw new InvalidOperationException("the settings store is unreachable");
            }

            return Rows.ToList();
        }
    }

    private sealed class FakeDbContextFactory(FakeSettingsStore store) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext()
        {
            // The list is read on creation, so a read that must fail fails here rather than silently
            // returning an empty table.
            List<PlatformSetting> rows = store.Read();
            string name = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseInMemoryDatabase(name)
                .Options;
            var db = new WorkflowEngineDbContext(options);
            db.PlatformSettings.AddRange(rows);
            db.SaveChanges();
            return db;
        }
    }
}
