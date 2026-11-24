using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// THE D5b LANDMINE, and the proof that D6 fixes it.
/// <para>
/// <c>runner_sessions</c> carries a partial <c>UNIQUE(execution_id) WHERE closed_at IS NULL</c>, so
/// only ONE session can be open per execution. The gateway opens that row at assignment, tolerates a
/// violation with a savepoint and does not try to fix it, which leaves the engine to guarantee the
/// invariant. These tests are that guarantee: the gate hands out one lease per execution, the lease
/// covers the job's whole life, and the client cannot create a job without taking it.
/// </para>
/// </summary>
public class ExecutionSessionGateTests
{
    private static readonly Guid ExecutionA = Guid.Parse("e0000000-0000-4000-8000-00000000000a");
    private static readonly Guid ExecutionB = Guid.Parse("e0000000-0000-4000-8000-00000000000b");

    private static ExecutionSessionGate Gate(int waitSeconds = 900)
        => new(Options.Create(new ComputeOptions { SessionWaitSeconds = waitSeconds }));

    [Fact]
    public async Task ConcurrentAcquisitionsForOneExecution_NeverOverlap()
    {
        ExecutionSessionGate gate = Gate();
        int active = 0;
        int max = 0;
        List<int> completes = [];

        async Task Worker()
        {
            await using ExecutionSessionLease lease = await gate.AcquireAsync(ExecutionA);
            lease.Acquired.Should().BeTrue();

            int now = Interlocked.Increment(ref active);
            InterlockedMax(ref max, now);
            await Task.Delay(2);
            lock (completes)
            {
                completes.Add(now);
            }

            Interlocked.Decrement(ref active);
        }

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(Worker)));

        max.Should().Be(1, "runner_sessions allows exactly one open session per execution, so the engine must never have two in flight");
        completes.Should().HaveCount(32);
        gate.HeldExecutionCount.Should().Be(0, "every lease was disposed, so no entry is left behind");
    }

    [Fact]
    public async Task DifferentExecutions_AreNotSerialisedWithEachOther()
    {
        ExecutionSessionGate gate = Gate();
        int active = 0;
        int max = 0;

        Guid[] executions = Enumerable.Range(0, 8).Select(i => new Guid($"e0000000-0000-4000-8000-0000000000{i:D2}")).ToArray();

        async Task Worker(Guid execution)
        {
            await using ExecutionSessionLease lease = await gate.AcquireAsync(execution);
            int now = Interlocked.Increment(ref active);
            InterlockedMax(ref max, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref active);
        }

        await Task.WhenAll(executions.Select(execution => Task.Run(() => Worker(execution))));

        max.Should().Be(8, "the key is the execution, so one busy execution must never block another");
        gate.HeldExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task TheWaitIsBounded_AndAnExpiredWaitHandsBackAnUnacquiredLease()
    {
        ExecutionSessionGate gate = Gate();
        await using ExecutionSessionLease holder = await gate.AcquireAsync(ExecutionA);

        await using ExecutionSessionLease late = await gate.AcquireAsync(ExecutionA, TimeSpan.FromMilliseconds(50));

        late.Acquired.Should().BeFalse("the caller fails the step with RUNNER_SESSION_BUSY rather than waiting forever");
        gate.IsHeld(ExecutionA).Should().BeTrue("the original holder still owns the slot");

        await holder.DisposeAsync();
        gate.IsHeld(ExecutionA).Should().BeFalse();
        gate.HeldExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task AQueuedWaiterTakesTheSlot_AsSoonAsItIsReleased()
    {
        ExecutionSessionGate gate = Gate();
        ExecutionSessionLease first = await gate.AcquireAsync(ExecutionA);

        Task<ExecutionSessionLease> waiter = gate.AcquireAsync(ExecutionA, TimeSpan.FromSeconds(5)).AsTask();
        waiter.IsCompleted.Should().BeFalse("the slot is held");

        await first.DisposeAsync();
        ExecutionSessionLease second = await waiter;

        second.Acquired.Should().BeTrue();
        gate.IsHeld(ExecutionA).Should().BeTrue();
        await second.DisposeAsync();
        gate.HeldExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task AReEntrantAcquisitionAtTheSameTime_IsSerialisedLikeAnyOther()
    {
        // Deliberately NOT re-entrant: two sessions in one execution is precisely the state the
        // schema forbids, so the gate serialises them even when one call is nested inside the other.
        // That is why the wait is bounded — a flow that really needs both would otherwise deadlock.
        ExecutionSessionGate gate = Gate();
        await using ExecutionSessionLease outer = await gate.AcquireAsync(ExecutionA);

        await using ExecutionSessionLease inner = await gate.AcquireAsync(ExecutionA, TimeSpan.FromMilliseconds(50));

        inner.Acquired.Should().BeFalse();
        outer.Acquired.Should().BeTrue();
    }

    [Fact]
    public async Task CancellingTheWait_LeavesNothingHeld()
    {
        ExecutionSessionGate gate = Gate();
        await using ExecutionSessionLease holder = await gate.AcquireAsync(ExecutionA);

        using CancellationTokenSource cts = new();
        Task<ExecutionSessionLease> waiter = gate.AcquireAsync(ExecutionA, TimeSpan.FromSeconds(30), cts.Token).AsTask();
        cts.Cancel();

        await FluentActions.Awaiting(() => waiter).Should().ThrowAsync<OperationCanceledException>();

        await holder.DisposeAsync();
        gate.HeldExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task DisposeIsIdempotent_AndTryAcquireReportsBusy()
    {
        ExecutionSessionGate gate = Gate();

        ExecutionSessionLease lease = await gate.AcquireAsync(ExecutionA);
        (await gate.TryAcquireAsync(ExecutionA)).Should().BeNull("the slot is taken");

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        gate.HeldExecutionCount.Should().Be(0);
        await using ExecutionSessionLease again = (await gate.TryAcquireAsync(ExecutionA))!;
        again.Acquired.Should().BeTrue();
    }

    // --- The invariant at the level the engine actually uses it -------------

    [Fact]
    public async Task TwoSessionsOfTheSameExecution_AreOpenedOneAfterTheOther()
    {
        // The end-to-end proof: through RunnerGatewayClient, where creating the job and taking the
        // slot are one call. The fake gateway records how many job creations are outstanding for the
        // execution; if the gate were missing, both would be open at once and the second session row
        // would be the one D5b's savepoint silently drops.
        Guid execution = ExecutionA;
        int open = 0;
        int maxOpen = 0;
        object sync = new();

        StubHttpMessageHandler handler = new(async request =>
        {
            lock (sync)
            {
                int now = Interlocked.Increment(ref open);
                InterlockedMax(ref maxOpen, now);
            }

            await Task.Delay(30);
            return ComputeTestFactory.Json(ComputeTestFactory.JobBody(Guid.NewGuid(), target: "OrgRunner"), HttpStatusCode.Created);
        });

        ComputeOptions options = ComputeTestFactory.Options();
        ExecutionSessionGate gate = new(Options.Create(options));
        RunnerGatewayClient client = ComputeTestFactory.Gateway(handler, options, gate);

        async Task RunOne(string purpose)
        {
            await using RunnerJobSession session = await client.OpenJobAsync(new RunnerJobRequest(
                execution, Guid.NewGuid(), RunnerJobType.VideoCompile, RunnerJobTarget.OrgRunner,
                StepId: Guid.NewGuid(), Purpose: purpose));
            await Task.Delay(20);

            lock (sync)
            {
                Interlocked.Decrement(ref open);
            }
        }

        await Task.WhenAll(Task.Run(() => RunOne("video.compile")), Task.Run(() => RunOne("video.compile.retry")));

        maxOpen.Should().Be(1, "the engine must never have two runner sessions outstanding in one execution");
        handler.Requests.Should().HaveCount(2, "both jobs were created — serialised, not dropped");
        gate.HeldExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task TwoSessionsOfDifferentExecutions_Overlap()
    {
        int open = 0;
        int maxOpen = 0;
        object sync = new();

        StubHttpMessageHandler handler = new(async _ =>
        {
            lock (sync)
            {
                InterlockedMax(ref maxOpen, Interlocked.Increment(ref open));
            }

            await Task.Delay(40);
            return ComputeTestFactory.Json(ComputeTestFactory.JobBody(Guid.NewGuid()), HttpStatusCode.Created);
        });

        ComputeOptions options = ComputeTestFactory.Options();
        ExecutionSessionGate gate = new(Options.Create(options));
        RunnerGatewayClient client = ComputeTestFactory.Gateway(handler, options, gate);

        async Task RunOne(Guid execution)
        {
            await using RunnerJobSession session = await client.OpenJobAsync(new RunnerJobRequest(
                execution, Guid.NewGuid(), RunnerJobType.VideoCompile, RunnerJobTarget.CloudPool,
                StepId: Guid.NewGuid(), Purpose: "video.compile"));
            await Task.Delay(20);

            lock (sync)
            {
                Interlocked.Decrement(ref open);
            }
        }

        await Task.WhenAll(Task.Run(() => RunOne(ExecutionA)), Task.Run(() => RunOne(ExecutionB)));

        maxOpen.Should().Be(2, "different executions have different session rows and must not queue behind each other");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current = Volatile.Read(ref target);
        while (value > current)
        {
            int seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
