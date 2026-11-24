#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>Security R1: the per-org claim that serializes subscription change requests.</summary>
public sealed class BillingChangeLockTests
{
    [Fact]
    public async Task InProcess_ASecondClaimForTheSameOrg_IsRefused_UntilTheFirstIsReleased()
    {
        InProcessBillingChangeLock locks = new();
        Guid org = Guid.NewGuid();

        IAsyncDisposable? first = await locks.TryAcquireAsync(org, default);
        first.Should().NotBeNull();
        (await locks.TryAcquireAsync(org, default)).Should().BeNull();

        await first!.DisposeAsync();
        IAsyncDisposable? again = await locks.TryAcquireAsync(org, default);
        again.Should().NotBeNull();
        await again!.DisposeAsync();
    }

    [Fact]
    public async Task InProcess_DifferentOrgs_AreIndependent()
    {
        InProcessBillingChangeLock locks = new();

        await using IAsyncDisposable? a = await locks.TryAcquireAsync(Guid.NewGuid(), default);
        await using IAsyncDisposable? b = await locks.TryAcquireAsync(Guid.NewGuid(), default);

        a.Should().NotBeNull();
        b.Should().NotBeNull();
    }

    [Fact]
    public async Task InProcess_ReleasingTwice_DoesNotFreeSomeoneElsesClaim()
    {
        InProcessBillingChangeLock locks = new();
        Guid org = Guid.NewGuid();
        IAsyncDisposable first = (await locks.TryAcquireAsync(org, default))!;
        await first.DisposeAsync();
        IAsyncDisposable second = (await locks.TryAcquireAsync(org, default))!;

        await first.DisposeAsync();

        (await locks.TryAcquireAsync(org, default)).Should().BeNull();
        await second.DisposeAsync();
    }

    [Fact]
    public void AdvisoryKey_IsTheFirstEightBytesOfSha256_OfThePrefixedOrgId()
    {
        Guid org = Guid.Parse("4f1c2e3a-0000-4000-8000-000000000001");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("billing-change:" + org));

        long key = PostgresBillingChangeLock.AdvisoryKey(org);

        key.Should().Be(BitConverter.ToInt64(hash, 0));
        PostgresBillingChangeLock.AdvisoryKey(org).Should().Be(key, "the key must be stable across processes");
        PostgresBillingChangeLock.AdvisoryKey(Guid.NewGuid()).Should().NotBe(key);
    }

    // ------------------------------------------------------------------ S-4/S-23: the connection cap

    /// <summary>
    /// Stands in for "open a lock connection and try the advisory lock": counts the opens, and answers a
    /// held session, a refusal (another replica holds the org) or a failure, as the next attempt is told to.
    /// </summary>
    private sealed class FakeSessions
    {
        private int _opens;
        private int _open;

        public int Opens => Volatile.Read(ref _opens);

        /// <summary>Sessions opened and not yet disposed.</summary>
        public int Open => Volatile.Read(ref _open);

        public Func<IAsyncDisposable?>? Next { get; set; }

        public Task<IAsyncDisposable?> TryAsync(long key, CancellationToken ct)
        {
            Interlocked.Increment(ref _opens);
            if (Next is { } next)
            {
                Next = null;
                return Task.FromResult(next());
            }

            Interlocked.Increment(ref _open);
            return Task.FromResult<IAsyncDisposable?>(new Session(this));
        }

        private sealed class Session(FakeSessions owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                Interlocked.Decrement(ref owner._open);
                return ValueTask.CompletedTask;
            }
        }
    }

    private static PostgresBillingChangeLock CappedLock(FakeSessions sessions, int maxConnections, int slotWaitMilliseconds)
        => new(
            "test-gate:" + Guid.NewGuid().ToString("N"),
            new BillingChangeLockOptions { MaxConnections = maxConnections, SlotWaitMilliseconds = slotWaitMilliseconds },
            sessions.TryAsync);

    [Fact]
    public async Task Same_org_second_attempt_in_process_is_refused_without_opening_a_connection()
    {
        FakeSessions sessions = new();
        PostgresBillingChangeLock locks = CappedLock(sessions, maxConnections: 4, slotWaitMilliseconds: 0);
        Guid org = Guid.NewGuid();

        await using IAsyncDisposable? first = await locks.TryAcquireAsync(org, CancellationToken.None);
        IAsyncDisposable? second = await locks.TryAcquireAsync(org, CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().BeNull("this process already holds the org's claim");
        sessions.Opens.Should().Be(1, "a same-org contender in this process never connects");
    }

    [Fact]
    public async Task Over_cap_attempt_throws_capacity_after_the_wait()
    {
        FakeSessions sessions = new();
        PostgresBillingChangeLock locks = CappedLock(sessions, maxConnections: 1, slotWaitMilliseconds: 150);
        await using IAsyncDisposable? held = await locks.TryAcquireAsync(Guid.NewGuid(), CancellationToken.None);
        held.Should().NotBeNull();

        Stopwatch waited = Stopwatch.StartNew();
        Func<Task> act = () => locks.TryAcquireAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<BillingChangeLockCapacityException>();
        waited.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(100, "the attempt waits for a slot before giving up");
        sessions.Opens.Should().Be(1, "the over-cap attempt never connects");
    }

    [Fact]
    public async Task Slot_is_returned_on_refusal_on_exception_and_on_dispose()
    {
        FakeSessions sessions = new();
        PostgresBillingChangeLock locks = CappedLock(sessions, maxConnections: 1, slotWaitMilliseconds: 0);
        Guid refusedOrg = Guid.NewGuid();
        Guid failedOrg = Guid.NewGuid();
        Guid heldOrg = Guid.NewGuid();

        // Refusal: another replica holds the org.
        sessions.Next = () => null;
        (await locks.TryAcquireAsync(refusedOrg, CancellationToken.None)).Should().BeNull();

        // Exception: the connection could not be opened.
        sessions.Next = () => throw new InvalidOperationException("connect failed");
        Func<Task> failing = () => locks.TryAcquireAsync(failedOrg, CancellationToken.None);
        await failing.Should().ThrowAsync<InvalidOperationException>();

        // Dispose: a held claim is released (twice must not free a second slot).
        IAsyncDisposable held = (await locks.TryAcquireAsync(heldOrg, CancellationToken.None))!;
        await held.DisposeAsync();
        await held.DisposeAsync();
        sessions.Open.Should().Be(0);

        // Every org's in-process claim and the single slot are free again.
        foreach (Guid org in new[] { refusedOrg, failedOrg, heldOrg })
        {
            IAsyncDisposable? again = await locks.TryAcquireAsync(org, CancellationToken.None);
            again.Should().NotBeNull("the slot and the in-process claim were returned");
            await again!.DisposeAsync();
        }

        // The double dispose above did not leave a second slot: one held, the next is over cap.
        await using IAsyncDisposable? one = await locks.TryAcquireAsync(Guid.NewGuid(), CancellationToken.None);
        Func<Task> overCap = () => locks.TryAcquireAsync(Guid.NewGuid(), CancellationToken.None);
        await overCap.Should().ThrowAsync<BillingChangeLockCapacityException>();
    }

    [Fact]
    public void Options_default_to_four_connections_and_a_two_second_wait()
    {
        BillingChangeLockOptions defaults = new();

        BillingChangeLockOptions.SectionName.Should().Be("Billing:ChangeLock");
        defaults.MaxConnections.Should().Be(4);
        defaults.SlotWaitMilliseconds.Should().Be(2000);
    }

    [Theory]
    [InlineData("MaxConnections", "0")]
    [InlineData("SlotWaitMilliseconds", "-1")]
    public void A_bad_option_fails_startup_naming_only_the_key(string key, string value)
    {
        using var root = CompositionRootHarness.Build(
            "s4-lock-options",
            (services, configuration) => services.AddInferenceApiServices(configuration),
            new Dictionary<string, string?> { [$"Billing:ChangeLock:{key}"] = value });

        Action act = () => root.Services.GetRequiredService<IStartupValidator>().Validate();

        OptionsValidationException failure = act.Should().Throw<OptionsValidationException>().Which;
        failure.Message.Should().Contain($"Billing:ChangeLock:{key}");
        failure.Message.Should().NotContain(value);
    }
}
