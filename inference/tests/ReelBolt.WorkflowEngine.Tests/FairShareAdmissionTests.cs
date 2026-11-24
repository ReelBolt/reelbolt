using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class FairShareAdmissionTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private const int NoCap = int.MaxValue;

    [Fact]
    public async Task Org_with_a_large_backlog_does_not_starve_a_small_org()
    {
        var admission = new FairShareAdmission(2);
        IDisposable a1 = await admission.AcquireAsync(OrgA, NoCap, CancellationToken.None);
        IDisposable a2 = await admission.AcquireAsync(OrgA, NoCap, CancellationToken.None);

        var aBacklog = new Task<IDisposable>[10];
        for (int i = 0; i < aBacklog.Length; i++)
        {
            aBacklog[i] = admission.AcquireAsync(OrgA, NoCap, CancellationToken.None);
        }

        // B arrives after all ten of A's waiters.
        Task<IDisposable> b = admission.AcquireAsync(OrgB, NoCap, CancellationToken.None);

        a1.Dispose();

        using IDisposable bLease = await b.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (Task<IDisposable> queued in aBacklog)
        {
            queued.IsCompleted.Should().BeFalse("B had 0 running against A's 1, so B takes the slot");
        }

        a2.Dispose();
        using IDisposable next = await aBacklog[0].WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Ties_on_running_count_are_first_come_first_served()
    {
        var admission = new FairShareAdmission(1);
        IDisposable held = await admission.AcquireAsync(OrgA, NoCap, CancellationToken.None);

        Task<IDisposable> b = admission.AcquireAsync(OrgB, NoCap, CancellationToken.None);
        Task<IDisposable> a = admission.AcquireAsync(OrgA, NoCap, CancellationToken.None);

        held.Dispose();

        using IDisposable bLease = await b.WaitAsync(TimeSpan.FromSeconds(5));
        a.IsCompleted.Should().BeFalse("both orgs run 0; B arrived first");
    }

    [Fact]
    public async Task Org_cap_is_respected_even_with_free_slots()
    {
        var admission = new FairShareAdmission(4);
        IDisposable a1 = await admission.AcquireAsync(OrgA, 1, CancellationToken.None);

        Task<IDisposable> a2 = admission.AcquireAsync(OrgA, 1, CancellationToken.None);
        await Task.Delay(100);
        a2.IsCompleted.Should().BeFalse("org A is at its cap of 1");
        admission.RunningCount.Should().Be(1);

        // Another org still gets in immediately.
        using IDisposable b = await admission.AcquireAsync(OrgB, 1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        a1.Dispose();
        using IDisposable a2Lease = await a2.WaitAsync(TimeSpan.FromSeconds(5));
        admission.RunningFor(OrgA).Should().Be(1);
    }

    [Fact]
    public async Task Cancelling_a_waiter_releases_nothing_it_does_not_own()
    {
        var admission = new FairShareAdmission(1);
        using IDisposable held = await admission.AcquireAsync(OrgA, NoCap, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        Task<IDisposable> waiting = admission.AcquireAsync(OrgB, NoCap, cts.Token);
        admission.WaitingCount.Should().Be(1);

        await cts.CancelAsync();
        await ((Func<Task>)(() => waiting)).Should().ThrowAsync<OperationCanceledException>();

        admission.RunningCount.Should().Be(1, "the cancelled waiter never owned a slot");
        admission.WaitingCount.Should().Be(0);
        admission.RunningFor(OrgA).Should().Be(1);
    }

    [Fact]
    public async Task Lease_dispose_is_idempotent()
    {
        var admission = new FairShareAdmission(1);
        IDisposable lease = await admission.AcquireAsync(OrgA, NoCap, CancellationToken.None);
        Task<IDisposable> next = admission.AcquireAsync(OrgB, NoCap, CancellationToken.None);

        lease.Dispose();
        lease.Dispose();

        using IDisposable b = await next.WaitAsync(TimeSpan.FromSeconds(5));
        admission.RunningCount.Should().Be(1, "a double dispose must not free a second slot");
    }

    [Fact]
    public async Task Already_cancelled_token_throws_without_registering()
    {
        var admission = new FairShareAdmission(1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => admission.AcquireAsync(OrgA, NoCap, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        admission.WaitingCount.Should().Be(0);
        admission.RunningCount.Should().Be(0);
    }
}
