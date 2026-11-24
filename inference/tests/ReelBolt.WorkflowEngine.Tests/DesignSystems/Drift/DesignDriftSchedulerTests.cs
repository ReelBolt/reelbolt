using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Drift;

/// <summary>A clock whose timers fire only when the test advances it: no real sleeps.</summary>
internal sealed class FakeTimerClock : TimeProvider
{
    private sealed class FakeTimer(FakeTimerClock owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner._timers.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target = _now + by;
        while (true)
        {
            FakeTimer? next = _timers.Where(t => t.Due is { } due && due <= target).OrderBy(t => t.Due).FirstOrDefault();
            if (next is null)
                break;

            _now = next.Due!.Value;
            next.Due = null;
            next.Fire();
        }

        _now = target;
    }
}

/// <summary>
/// The per-organization round-robin scheduler (DRF-01..04, DRF-09): every organization has its own lane
/// with its own caps, pending set and dropped-memory, so no tenant's traffic can fill, starve or
/// postpone another's. All time is a fake clock.
/// </summary>
public sealed class DesignDriftSchedulerTests
{
    private static readonly TimeSpan Window = DesignDriftQueue.DebounceWindow;

    private static void Settle(DesignDriftQueue queue, Guid project)
    {
        queue.TryBegin(project, out _);
        queue.Complete(project);
    }

    private static List<Guid> DrainAll(DesignDriftQueue queue)
    {
        List<Guid> order = [];
        while (queue.Reader.TryRead(out DesignDriftRequest request))
        {
            order.Add(request.ProjectId);
            Settle(queue, request.ProjectId);
        }

        return order;
    }

    // (a) 11-org flood -------------------------------------------------------------------------

    [Fact]
    public void An_eleventh_organization_is_accepted_and_served_within_one_rotation_while_ten_flood()
    {
        DesignDriftQueue queue = new(new FakeTimerClock());
        for (int org = 0; org < 10; org++)
        {
            Guid id = Guid.NewGuid();
            for (int i = 0; i < 150; i++)
                queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, id);
        }

        Guid late = Guid.NewGuid();
        Guid lateProject = Guid.NewGuid();
        queue.Enqueue(lateProject, DesignDriftTrigger.GitHubPush, null, late)
            .Should().Be(DesignDriftEnqueueResult.Queued, "ten full organizations must not fill a shared capacity");

        List<Guid> firstRotation = [];
        for (int i = 0; i < 11; i++)
        {
            queue.Reader.TryRead(out DesignDriftRequest request).Should().BeTrue();
            firstRotation.Add(request.ProjectId);
        }

        firstRotation.Should().Contain(lateProject, "the worker serves one item per lane per turn");
    }

    // (g) round-robin order --------------------------------------------------------------------

    [Fact]
    public void A_five_thousand_project_organization_delays_a_one_project_organization_by_at_most_one_rotation()
    {
        DesignDriftQueue queue = new(new FakeTimerClock());
        Guid big = Guid.NewGuid();
        for (int i = 0; i < 5000; i++)
            queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, big);

        Guid small = Guid.NewGuid();
        Guid smallProject = Guid.NewGuid();
        queue.Enqueue(smallProject, DesignDriftTrigger.GitHubPush, null, small);

        queue.Reader.TryRead(out DesignDriftRequest first).Should().BeTrue();
        queue.Reader.TryRead(out DesignDriftRequest second).Should().BeTrue();
        new[] { first.ProjectId, second.ProjectId }.Should().Contain(smallProject);
    }

    [Fact]
    public void Lanes_alternate_one_item_per_turn()
    {
        DesignDriftQueue queue = new(new FakeTimerClock());
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        Guid[] aProjects = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        Guid[] bProjects = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        foreach (Guid p in aProjects)
            queue.Enqueue(p, DesignDriftTrigger.GitHubPush, null, a);
        foreach (Guid p in bProjects)
            queue.Enqueue(p, DesignDriftTrigger.GitHubPush, null, b);

        List<Guid> order = DrainAll(queue);

        order.Should().Equal(aProjects[0], bProjects[0], aProjects[1], bProjects[1], aProjects[2], bProjects[2]);
    }

    [Fact]
    public void A_manual_check_goes_to_the_front_of_its_own_lane_only()
    {
        DesignDriftQueue queue = new(new FakeTimerClock());
        Guid org = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Guid manual = Guid.NewGuid();
        Guid otherProject = Guid.NewGuid();
        queue.Enqueue(first, DesignDriftTrigger.GitHubPush, null, org);
        queue.Enqueue(second, DesignDriftTrigger.GitHubPush, null, org);
        queue.Enqueue(otherProject, DesignDriftTrigger.GitHubPush, null, other);
        queue.Enqueue(manual, DesignDriftTrigger.Manual, null, org).Should().Be(DesignDriftEnqueueResult.Queued);

        List<Guid> order = DrainAll(queue);

        order.IndexOf(manual).Should().Be(0, "manual jumps its own lane's queue");
        order.IndexOf(otherProject).Should().Be(1, "but it does not jump the other lane's turn");
    }

    [Fact]
    public void A_manual_check_obeys_its_lanes_cap()
    {
        DesignDriftQueue queue = new(new FakeTimerClock());
        Guid org = Guid.NewGuid();
        for (int i = 0; i < DesignDriftQueue.PerOrganizationLimit; i++)
            queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, org);

        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.Manual, null, org)
            .Should().Be(DesignDriftEnqueueResult.OrganizationBusy);
    }

    [Fact]
    public void An_unknown_organization_shares_one_capped_lane()
    {
        DesignDriftQueue queue = new(new FakeTimerClock());
        for (int i = 0; i < DesignDriftQueue.PerOrganizationLimit; i++)
            queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null).Should().Be(DesignDriftEnqueueResult.Queued);

        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null).Should().Be(DesignDriftEnqueueResult.OrganizationBusy);
        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, Guid.NewGuid())
            .Should().Be(DesignDriftEnqueueResult.Queued, "a named organization has its own lane");
    }

    [Fact]
    public void Sweeps_may_use_at_most_half_of_a_lane()
    {
        DesignDriftQueue queue = new(new FakeTimerClock());
        Guid org = Guid.NewGuid();
        for (int i = 0; i < DesignDriftQueue.PerOrganizationLimit / 2; i++)
            queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.Sweep, null, org).Should().Be(DesignDriftEnqueueResult.Queued);

        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.Sweep, null, org).Should().Be(DesignDriftEnqueueResult.OrganizationBusy);
        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, org)
            .Should().Be(DesignDriftEnqueueResult.Queued, "a push still has room when sweeps are saturated");
    }

    // (c) timer postponement -------------------------------------------------------------------

    [Fact]
    public void A_steady_stream_for_another_project_never_postpones_a_pending_trailing_run()
    {
        FakeTimerClock clock = new();
        DesignDriftQueue queue = new(clock);
        Guid org = Guid.NewGuid();
        Guid p1 = Guid.NewGuid();
        Guid p2 = Guid.NewGuid();

        queue.Enqueue(p1, DesignDriftTrigger.GitHubPush, null, org);
        queue.Reader.TryRead(out _);
        Settle(queue, p1);
        clock.Advance(TimeSpan.FromSeconds(5));
        queue.Enqueue(p1, DesignDriftTrigger.GitHubPush, ["late.css"], org).Should().Be(DesignDriftEnqueueResult.Debounced);
        DateTimeOffset p1Due = clock.GetUtcNow() - TimeSpan.FromSeconds(5) + Window;

        // P2 is accepted and debounced again every 20 s: each debounce used to push the single timer out.
        while (clock.GetUtcNow() < p1Due - TimeSpan.FromSeconds(1))
        {
            queue.Enqueue(p2, DesignDriftTrigger.GitHubPush, null, org);
            while (queue.Reader.TryRead(out DesignDriftRequest r))
            {
                if (r.ProjectId == p2)
                    Settle(queue, p2);
            }

            clock.Advance(TimeSpan.FromSeconds(20));
            if (clock.GetUtcNow() >= p1Due)
                break;
        }

        clock.Advance(p1Due - clock.GetUtcNow() + TimeSpan.FromSeconds(1) > TimeSpan.Zero
            ? p1Due - clock.GetUtcNow() + TimeSpan.FromSeconds(1)
            : TimeSpan.Zero);

        queue.IsQueued(p1).Should().BeTrue("P1's trailing re-run fires at its own due time, not P2's");
    }

    [Fact]
    public void Five_minutes_of_traffic_on_another_project_leaves_the_first_trailing_run_on_time()
    {
        FakeTimerClock clock = new();
        DesignDriftQueue queue = new(clock);
        Guid org = Guid.NewGuid();
        Guid p1 = Guid.NewGuid();
        Guid p2 = Guid.NewGuid();
        DateTimeOffset start = clock.GetUtcNow();

        queue.Enqueue(p1, DesignDriftTrigger.GitHubPush, null, org);
        queue.Reader.TryRead(out _);
        Settle(queue, p1);
        queue.Enqueue(p1, DesignDriftTrigger.GitHubPush, ["late.css"], org);

        DateTimeOffset? p1QueuedAt = null;
        for (int step = 0; step < 15; step++)
        {
            queue.Enqueue(p2, DesignDriftTrigger.GitHubPush, null, org);
            while (queue.Reader.TryRead(out DesignDriftRequest r))
            {
                if (r.ProjectId == p1)
                    p1QueuedAt ??= clock.GetUtcNow();
                Settle(queue, r.ProjectId);
            }

            clock.Advance(TimeSpan.FromSeconds(20));
        }

        p1QueuedAt.Should().NotBeNull();
        (p1QueuedAt!.Value - start).Should().BeLessThanOrEqualTo(Window + TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void A_tick_or_request_after_dispose_does_not_throw()
    {
        FakeTimerClock clock = new();
        DesignDriftQueue queue = new(clock);
        Guid org = Guid.NewGuid();
        Guid p = Guid.NewGuid();
        queue.Enqueue(p, DesignDriftTrigger.GitHubPush, null, org);
        queue.Reader.TryRead(out _);
        Settle(queue, p);
        queue.Enqueue(p, DesignDriftTrigger.GitHubPush, null, org);

        queue.Dispose();
        clock.Advance(Window + TimeSpan.FromSeconds(1));

        ((Action)queue.FlushDue).Should().NotThrow("a tick racing shutdown must not reach the disposed semaphore");
        ((Action)(() => queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, org))).Should().NotThrow();
        ((Action)(() => queue.Complete(p))).Should().NotThrow();
    }

    // (d) pending eviction ---------------------------------------------------------------------

    [Fact]
    public void One_organization_filling_its_pending_set_does_not_evict_anothers()
    {
        FakeTimerClock clock = new();
        DesignDriftQueue queue = new(clock);
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();

        for (int i = 0; i < 1100; i++)
        {
            Guid p = Guid.NewGuid();
            queue.Enqueue(p, DesignDriftTrigger.GitHubPush, null, a);
            queue.Reader.TryRead(out _);
            Settle(queue, p);
            queue.Enqueue(p, DesignDriftTrigger.GitHubPush, null, a);
        }

        Guid bProject = Guid.NewGuid();
        queue.Enqueue(bProject, DesignDriftTrigger.GitHubPush, null, b);
        queue.Reader.TryRead(out _);
        Settle(queue, bProject);
        queue.Enqueue(bProject, DesignDriftTrigger.GitHubPush, ["x.css"], b).Should().Be(DesignDriftEnqueueResult.Debounced);

        clock.Advance(Window + TimeSpan.FromSeconds(1));

        queue.IsQueued(bProject).Should().BeTrue("organization B's pending run is its own and was not evicted by A");
    }

    // (e) dropped memory -----------------------------------------------------------------------

    [Fact]
    public void A_request_refused_for_a_full_lane_is_re_enqueued_once_the_lane_drains()
    {
        DesignDriftQueue queue = new(new FakeTimerClock());
        Guid org = Guid.NewGuid();
        List<Guid> fill = Enumerable.Range(0, DesignDriftQueue.PerOrganizationLimit).Select(_ => Guid.NewGuid()).ToList();
        foreach (Guid p in fill)
            queue.Enqueue(p, DesignDriftTrigger.GitHubPush, null, org);

        Guid refused = Guid.NewGuid();
        queue.Enqueue(refused, DesignDriftTrigger.GitHubPush, ["a.css"], org).Should().Be(DesignDriftEnqueueResult.OrganizationBusy);
        queue.IsQueued(refused).Should().BeFalse();

        queue.Reader.TryRead(out DesignDriftRequest head).Should().BeTrue();
        Settle(queue, head.ProjectId);

        queue.IsQueued(refused).Should().BeTrue("the refused change is remembered and re-enqueued as space frees");
        queue.Reader.TryRead(out _);
        queue.TryBegin(refused, out DesignDriftRequest again).Should().BeTrue();
        again.ChangedPaths.Should().BeEquivalentTo(["a.css"]);
    }

    [Fact]
    public void Empty_lanes_are_dropped_once_their_debounce_memory_has_expired()
    {
        FakeTimerClock clock = new();
        DesignDriftQueue queue = new(clock);
        Guid p = Guid.NewGuid();
        queue.Enqueue(p, DesignDriftTrigger.GitHubPush, null, Guid.NewGuid());
        queue.Reader.TryRead(out _);
        Settle(queue, p);

        queue.LaneCount.Should().Be(1, "the window is still remembered");
        clock.Advance(Window + TimeSpan.FromSeconds(1));
        queue.FlushDue();
        queue.LaneCount.Should().Be(0);
    }

    [Fact]
    public void Changed_paths_stay_capped_per_request()
    {
        DesignDriftQueue queue = new(new FakeTimerClock());
        Guid org = Guid.NewGuid();
        string[] many = Enumerable.Range(0, 5000).Select(i => $"f{i}.css").ToArray();

        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, many, org);

        queue.Reader.TryRead(out DesignDriftRequest request).Should().BeTrue();
        request.ChangedPaths!.Count.Should().BeLessThanOrEqualTo(DesignDriftQueue.MaxTrackedPaths + 1);
    }
}
