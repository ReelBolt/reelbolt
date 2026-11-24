using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.GitHub;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Drift;

/// <summary>
/// P3-02: the drift queue (dedupe, 60 s debounce, bounded), the worker (own scope, system tenant,
/// swallows failures, always releases its entry) and the sweeper (only projects with a current version,
/// plus the proposal TTL).
/// </summary>
public sealed class DesignDriftQueueTests
{
    internal sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    // ---------------------------------------------------------------------------------------------
    // Queue
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_project_is_deduped_while_live_and_a_request_inside_the_window_re_runs_after_it()
    {
        FakeClock clock = new();
        DesignDriftQueue queue = new(clock);
        Guid project = Guid.NewGuid();

        queue.Enqueue(project, DesignDriftTrigger.GitHubPush, ["a.css"])
            .Should().Be(DesignDriftEnqueueResult.Queued);
        queue.Enqueue(project, DesignDriftTrigger.CloudSync, ["b.css"])
            .Should().Be(DesignDriftEnqueueResult.Coalesced, "a live entry absorbs the second request");

        queue.Reader.TryRead(out DesignDriftRequest request).Should().BeTrue();
        request.ProjectId.Should().Be(project);
        queue.Reader.TryRead(out _).Should().BeFalse("one entry per project");

        queue.TryBegin(project, out DesignDriftRequest current).Should().BeTrue();
        current.Trigger.Should().Be(DesignDriftTrigger.GitHubPush);
        current.ChangedPaths.Should().BeEquivalentTo(["a.css", "b.css"]);
        queue.IsQueued(project).Should().BeTrue();
        queue.Complete(project);
        queue.IsQueued(project).Should().BeFalse();

        // The finished check ran before this change landed. It is not queued inside the window, but it
        // is not lost either: it re-runs once the window has passed (trailing-edge debounce).
        queue.Enqueue(project, DesignDriftTrigger.CloudSync, ["late.css"])
            .Should().Be(DesignDriftEnqueueResult.Debounced, "inside the debounce window");
        queue.Reader.TryRead(out _).Should().BeFalse("nothing is queued inside the window");
        queue.FlushDue();
        queue.Reader.TryRead(out _).Should().BeFalse("the window has not passed yet");

        clock.Advance(TimeSpan.FromSeconds(61));
        queue.FlushDue();
        queue.Reader.TryRead(out DesignDriftRequest trailing).Should().BeTrue("the change inside the window re-runs");
        trailing.Trigger.Should().Be(DesignDriftTrigger.CloudSync);
        trailing.ChangedPaths.Should().BeEquivalentTo(["late.css"]);
        queue.TryBegin(project, out _).Should().BeTrue();
        queue.Complete(project);

        clock.Advance(TimeSpan.FromSeconds(61));
        queue.Enqueue(project, DesignDriftTrigger.Sweep, null)
            .Should().Be(DesignDriftEnqueueResult.Queued, "the window has passed");
    }

    [Fact]
    public void Several_requests_inside_the_window_fold_into_one_trailing_run()
    {
        FakeClock clock = new();
        DesignDriftQueue queue = new(clock);
        Guid project = Guid.NewGuid();

        queue.Enqueue(project, DesignDriftTrigger.GitHubPush, ["a.css"]);
        queue.Reader.TryRead(out _);
        queue.TryBegin(project, out _);
        queue.Complete(project);

        queue.Enqueue(project, DesignDriftTrigger.CloudSync, ["b.css"]).Should().Be(DesignDriftEnqueueResult.Debounced);
        queue.Enqueue(project, DesignDriftTrigger.FolderSync, ["c.css"]).Should().Be(DesignDriftEnqueueResult.Debounced);

        clock.Advance(TimeSpan.FromSeconds(61));
        queue.FlushDue();

        queue.Reader.TryRead(out _).Should().BeTrue();
        queue.TryBegin(project, out DesignDriftRequest current).Should().BeTrue();
        current.ChangedPaths.Should().BeEquivalentTo(["b.css", "c.css"]);
        queue.Reader.TryRead(out _).Should().BeFalse("one trailing run, not one per request");
    }

    [Fact]
    public void A_manual_check_bypasses_the_debounce_window()
    {
        DesignDriftQueue queue = new(new FakeClock());
        Guid project = Guid.NewGuid();

        queue.Enqueue(project, DesignDriftTrigger.GitHubPush, null);
        queue.Reader.TryRead(out _);
        queue.TryBegin(project, out _);
        queue.Complete(project);

        queue.Enqueue(project, DesignDriftTrigger.Manual, null)
            .Should().Be(DesignDriftEnqueueResult.Queued, "'Check now' must never answer debounced");
        queue.Reader.TryRead(out DesignDriftRequest request).Should().BeTrue();
        request.Trigger.Should().Be(DesignDriftTrigger.Manual);
    }

    [Fact]
    public void A_sweep_inside_the_window_is_not_re_run_later()
    {
        FakeClock clock = new();
        DesignDriftQueue queue = new(clock);
        Guid project = Guid.NewGuid();

        queue.Enqueue(project, DesignDriftTrigger.GitHubPush, null);
        queue.Reader.TryRead(out _);
        queue.TryBegin(project, out _);
        queue.Complete(project);

        queue.Enqueue(project, DesignDriftTrigger.Sweep, null).Should().Be(DesignDriftEnqueueResult.Debounced);
        clock.Advance(TimeSpan.FromSeconds(61));
        queue.FlushDue();
        queue.Reader.TryRead(out _).Should().BeFalse("a sweep is periodic, not a change to catch up on");
    }

    [Fact]
    public void Coalesced_changed_paths_are_capped_and_collapse_to_many()
    {
        DesignDriftQueue queue = new(new FakeClock());
        Guid project = Guid.NewGuid();

        queue.Enqueue(project, DesignDriftTrigger.GitHubPush, ["first.css"]);
        for (int i = 0; i < DesignDriftQueue.MaxTrackedPaths * 3; i++)
            queue.Enqueue(project, DesignDriftTrigger.GitHubPush, [$"file-{i}.css"]).Should().Be(DesignDriftEnqueueResult.Coalesced);

        queue.Reader.TryRead(out _);
        queue.TryBegin(project, out DesignDriftRequest current).Should().BeTrue();
        current.ChangedPaths!.Count.Should().BeLessThanOrEqualTo(DesignDriftQueue.MaxTrackedPaths + 1);
        current.ChangedPaths.Should().Contain(DesignDriftQueue.ManyPaths);
    }

    [Fact]
    public void One_organization_cannot_fill_the_queue_for_the_others()
    {
        DesignDriftQueue queue = new(new FakeClock());
        Guid greedy = Guid.NewGuid();
        Guid other = Guid.NewGuid();

        for (int i = 0; i < DesignDriftQueue.PerOrganizationLimit; i++)
        {
            queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, greedy)
                .Should().Be(DesignDriftEnqueueResult.Queued);
        }

        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, greedy)
            .Should().Be(DesignDriftEnqueueResult.OrganizationBusy);
        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, other)
            .Should().Be(DesignDriftEnqueueResult.Queued, "another organization still has room");
    }

    [Fact]
    public void An_organization_slot_is_released_when_its_check_completes()
    {
        DesignDriftQueue queue = new(new FakeClock());
        Guid org = Guid.NewGuid();
        Guid first = Guid.NewGuid();

        queue.Enqueue(first, DesignDriftTrigger.GitHubPush, null, org);
        for (int i = 1; i < DesignDriftQueue.PerOrganizationLimit; i++)
            queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, org);

        queue.Reader.TryRead(out _);
        queue.TryBegin(first, out _);
        queue.Complete(first);

        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, org)
            .Should().Be(DesignDriftEnqueueResult.Queued);
    }

    [Fact]
    public void Sweeps_leave_room_in_the_lane_for_source_triggered_checks()
    {
        DesignDriftQueue queue = new(new FakeClock());
        Guid org = Guid.NewGuid();

        for (int i = 0; i < DesignDriftQueue.SweepLimitPerOrganization; i++)
        {
            queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.Sweep, null, org)
                .Should().Be(DesignDriftEnqueueResult.Queued);
        }

        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.Sweep, null, org).Should().Be(DesignDriftEnqueueResult.OrganizationBusy);
        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, org)
            .Should().Be(DesignDriftEnqueueResult.Queued, "a push still gets a slot when sweeps are saturated");
    }

    [Fact]
    public void A_change_during_a_running_check_makes_it_run_once_more()
    {
        DesignDriftQueue queue = new(new FakeClock());
        Guid project = Guid.NewGuid();

        queue.Enqueue(project, DesignDriftTrigger.GitHubPush, null);
        queue.Reader.TryRead(out _);
        queue.TryBegin(project, out _).Should().BeTrue();

        queue.Enqueue(project, DesignDriftTrigger.FolderSync, ["clip.mp4"])
            .Should().Be(DesignDriftEnqueueResult.Coalesced);
        queue.Complete(project);

        queue.Reader.TryRead(out DesignDriftRequest rerun).Should().BeTrue("the change made it run again");
        rerun.Trigger.Should().Be(DesignDriftTrigger.FolderSync);
        queue.TryBegin(project, out DesignDriftRequest current).Should().BeTrue();
        current.ChangedPaths.Should().BeEquivalentTo(["clip.mp4"]);
    }

    [Fact]
    public void A_full_lane_refuses_the_newest_request_instead_of_blocking()
    {
        DesignDriftQueue queue = new(new FakeClock());
        Guid org = Guid.NewGuid();

        for (int i = 0; i < DesignDriftQueue.PerOrganizationLimit; i++)
        {
            queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, org)
                .Should().Be(DesignDriftEnqueueResult.Queued);
        }

        queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, org)
            .Should().Be(DesignDriftEnqueueResult.OrganizationBusy);
    }

    // ---------------------------------------------------------------------------------------------
    // Worker
    // ---------------------------------------------------------------------------------------------

    private sealed class RecordingDriftService : IDesignSystemDriftService
    {
        public List<(Guid ProjectId, DesignDriftTrigger Trigger, IReadOnlyCollection<string>? Paths, CancellationToken Token)> Calls { get; } = [];
        public Func<Task>? During { get; set; }

        public async Task<DesignDriftOutcome> CheckAsync(
            Guid projectId, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths, CancellationToken ct)
        {
            Calls.Add((projectId, trigger, changedPaths, ct));
            if (During is not null)
                await During();
            return new DesignDriftOutcome("nochange", null);
        }
    }

    [Fact]
    public async Task The_worker_runs_one_item_in_its_own_scope_and_swallows_failures()
    {
        RecordingDriftService drift = new();
        ServiceCollection services = new();
        services.AddScoped<IDesignSystemDriftService>(_ => drift);
        services.AddSingleton<ITenantContext>(new TestTenant());
        await using ServiceProvider provider = services.BuildServiceProvider();

        DesignDriftQueue queue = new(new FakeClock());
        DesignSystemDriftWorker worker = new(
            queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DesignSystemDriftWorker>.Instance);

        Guid project = Guid.NewGuid();
        queue.Enqueue(project, DesignDriftTrigger.GitHubPush, ["a.css"]);
        queue.Reader.TryRead(out DesignDriftRequest request).Should().BeTrue();

        using CancellationTokenSource stopping = new();
        await worker.ProcessAsync(request, stopping.Token);

        drift.Calls.Should().ContainSingle();
        drift.Calls[0].ProjectId.Should().Be(project);
        drift.Calls[0].Trigger.Should().Be(DesignDriftTrigger.GitHubPush);
        drift.Calls[0].Token.Should().Be(stopping.Token);
        queue.IsQueued(project).Should().BeFalse("a finished check releases its entry");

        drift.During = () => throw new InvalidOperationException("boom");
        Guid broken = Guid.NewGuid();
        queue.Enqueue(broken, DesignDriftTrigger.CloudSync, null);
        queue.Reader.TryRead(out DesignDriftRequest brokenRequest).Should().BeTrue();

        await worker.ProcessAsync(brokenRequest, stopping.Token);

        queue.IsQueued(broken).Should().BeFalse("a failing check still releases its entry");
    }

    private sealed class HangingDriftService : IDesignSystemDriftService
    {
        public CancellationToken Token { get; private set; }

        public async Task<DesignDriftOutcome> CheckAsync(
            Guid projectId, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths, CancellationToken ct)
        {
            Token = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return new DesignDriftOutcome("nochange", null);
        }
    }

    [Fact]
    public async Task A_hung_check_is_cancelled_by_the_per_item_timeout_and_releases_its_entry()
    {
        HangingDriftService drift = new();
        ServiceCollection services = new();
        services.AddScoped<IDesignSystemDriftService>(_ => drift);
        services.AddSingleton<ITenantContext>(new TestTenant());
        await using ServiceProvider provider = services.BuildServiceProvider();

        DesignDriftQueue queue = new(new FakeClock());
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DesignSystem:Drift:ItemTimeoutSeconds"] = "0.2" })
            .Build();
        DesignSystemDriftWorker worker = new(
            queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DesignSystemDriftWorker>.Instance,
            configuration);

        Guid project = Guid.NewGuid();
        queue.Enqueue(project, DesignDriftTrigger.GitHubPush, null);
        queue.Reader.TryRead(out DesignDriftRequest request).Should().BeTrue();

        using CancellationTokenSource stopping = new();
        Task run = worker.ProcessAsync(request, stopping.Token);
        (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(run, "the timeout must unstick the worker");
        await run;

        drift.Token.IsCancellationRequested.Should().BeTrue();
        stopping.IsCancellationRequested.Should().BeFalse("it was the item timeout, not a shutdown");
        queue.IsQueued(project).Should().BeFalse("a timed-out check still releases its entry");
    }

    // ---------------------------------------------------------------------------------------------
    // Sweeper
    // ---------------------------------------------------------------------------------------------

    private static DbContextOptions<InferenceApiDbContext> SeedCoveredProjects(
        int count, Guid organizationId, out List<Guid> projectIds)
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-drift-sweep-page-{Guid.NewGuid():N}")
            .Options;

        using InferenceApiDbContext seed = new(options);
        projectIds = [];
        for (int i = 0; i < count; i++)
        {
            Project project = NewProject();
            project.OrganizationId = organizationId;
            projectIds.Add(project.Id);
            seed.Projects.Add(project);
            seed.DesignSystems.Add(new DesignSystem
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Kind = DesignSystemKinds.Design,
                Name = "Brand",
                CurrentVersionId = Guid.NewGuid(),
                CreatedByUserId = Guid.NewGuid(),
            });
        }

        seed.SaveChanges();
        projectIds.Sort();
        return options;
    }

    private static DesignSystemDriftSweeper NewSweeper(
        DbContextOptions<InferenceApiDbContext> options, DesignDriftQueue queue, string? batchSize, out ServiceProvider provider)
    {
        ServiceCollection services = new();
        services.AddScoped(_ => new InferenceApiDbContext(options));
        services.AddSingleton<ITenantContext>(new TestTenant());
        provider = services.BuildServiceProvider();

        Dictionary<string, string?> values = new() { ["DesignSystem:Drift:SweepIntervalMinutes"] = "360" };
        if (batchSize is not null)
            values["DesignSystem:Drift:SweepBatchSize"] = batchSize;
        return new DesignSystemDriftSweeper(
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            NullLogger<DesignSystemDriftSweeper>.Instance);
    }

    private static List<Guid> Drain(DesignDriftQueue queue)
    {
        List<Guid> drained = [];
        while (queue.Reader.TryRead(out DesignDriftRequest request))
        {
            drained.Add(request.ProjectId);
            queue.TryBegin(request.ProjectId, out _);
            queue.Complete(request.ProjectId);
        }

        return drained;
    }

    [Fact]
    public async Task The_sweep_pages_in_project_id_order_so_later_projects_are_not_starved()
    {
        DbContextOptions<InferenceApiDbContext> options = SeedCoveredProjects(5, Guid.NewGuid(), out List<Guid> ids);
        FakeClock clock = new();
        DesignDriftQueue queue = new(clock);
        DesignSystemDriftSweeper sweeper = NewSweeper(options, queue, "2", out ServiceProvider provider);
        await using ServiceProvider _ = provider;
        sweeper.Cursor = Guid.Empty;

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(2);
        sweeper.HasMore.Should().BeTrue();
        List<Guid> first = Drain(queue);

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(2);
        sweeper.HasMore.Should().BeTrue();
        List<Guid> second = Drain(queue);

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(1);
        sweeper.HasMore.Should().BeFalse("the cycle is complete");
        List<Guid> third = Drain(queue);

        first.Concat(second).Concat(third).Should().Equal(ids, "every project is swept once, in id order");

        clock.Advance(TimeSpan.FromMinutes(2));
        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(2, "the next cycle starts over");
    }

    [Fact]
    public async Task A_saturated_organization_is_skipped_and_the_cursor_moves_on_to_the_other_tenants()
    {
        // Three organizations' projects interleave by id. One is held at its cap by other work.
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-drift-sweep-pinned-{Guid.NewGuid():N}").Options;
        Guid busy = Guid.NewGuid();
        Guid[] others = [Guid.NewGuid(), Guid.NewGuid()];
        List<Guid> otherProjects = [];
        await using (InferenceApiDbContext seed = new(options))
        {
            for (int i = 0; i < 90; i++)
            {
                Guid org = i % 3 == 0 ? busy : others[i % 3 - 1];
                Project project = NewProject();
                project.OrganizationId = org;
                if (org != busy)
                    otherProjects.Add(project.Id);
                seed.Projects.Add(project);
                seed.DesignSystems.Add(new DesignSystem
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    Kind = DesignSystemKinds.Design,
                    Name = "B",
                    CurrentVersionId = Guid.NewGuid(),
                    CreatedByUserId = Guid.NewGuid(),
                });
            }

            await seed.SaveChangesAsync();
        }

        FakeClock clock = new();
        DesignDriftQueue queue = new(clock);
        List<Guid> external = [];
        for (int i = 0; i < DesignDriftQueue.PerOrganizationLimit; i++)
        {
            Guid id = Guid.NewGuid();
            external.Add(id);
            queue.Enqueue(id, DesignDriftTrigger.GitHubPush, null, busy);
        }

        DesignSystemDriftSweeper sweeper = NewSweeper(options, queue, "20", out ServiceProvider provider);
        await using ServiceProvider _ = provider;

        for (int i = 0; i < 5; i++)
            await sweeper.SweepOnceAsync(CancellationToken.None);

        otherProjects.Should().OnlyContain(p => queue.IsQueued(p), "the other tenants' projects were all enqueued");

        // The busy tenant's skipped projects are retried, not forgotten: once its lane drains, they go in.
        foreach (Guid id in external)
        {
            queue.TryBegin(id, out DesignDriftRequest begun);
            queue.Complete(id);
        }

        int drained = 0;
        do
        {
            await sweeper.SweepOnceAsync(CancellationToken.None);
            drained++;
        }
        while (sweeper.HasMore && drained < 20);

        sweeper.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Every_covered_project_of_a_big_organization_is_eventually_swept()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-drift-sweep-big-{Guid.NewGuid():N}").Options;
        Guid big = Guid.NewGuid();
        Guid[] small = [Guid.NewGuid(), Guid.NewGuid()];
        List<Guid> all = [];
        await using (InferenceApiDbContext seed = new(options))
        {
            for (int i = 0; i < 86; i++)
            {
                Project project = NewProject();
                project.OrganizationId = i < 80 ? big : small[i % 2];
                all.Add(project.Id);
                seed.Projects.Add(project);
                seed.DesignSystems.Add(new DesignSystem
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    Kind = DesignSystemKinds.Design,
                    Name = "B",
                    CurrentVersionId = Guid.NewGuid(),
                    CreatedByUserId = Guid.NewGuid(),
                });
            }

            await seed.SaveChangesAsync();
        }

        DesignDriftQueue queue = new(new FakeClock());
        DesignSystemDriftSweeper sweeper = NewSweeper(options, queue, null, out ServiceProvider provider);
        await using ServiceProvider _ = provider;

        HashSet<Guid> swept = [];
        int sweeps = 0;
        do
        {
            await sweeper.SweepOnceAsync(CancellationToken.None);
            sweeps++;
            if (sweeps == 1)
            {
                all.Count(queue.IsQueued).Should().Be(
                    DesignDriftQueue.SweepLimitPerOrganization + 6, "the big org takes its half-lane share and does not block the small ones");
            }

            swept.UnionWith(Drain(queue));
        }
        while (swept.Count < all.Count && sweeps < 20);

        sweeper.HasMore.Should().BeFalse("the cycle terminates");
        swept.Should().BeEquivalentTo(all, "every covered project is swept (deferred ones are retried first in the next cycle)");
    }

    [Fact]
    public async Task A_tenant_that_never_drains_cannot_stop_the_cursor_resetting_for_everyone_else()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-drift-sweep-poc-{Guid.NewGuid():N}").Options;
        Guid hog = Guid.NewGuid();
        Guid victim = Guid.NewGuid();
        List<Guid> victimProjects = [];
        await using (InferenceApiDbContext seed = new(options))
        {
            for (int i = 0; i < 155; i++)
            {
                Project project = NewProject();
                project.OrganizationId = i < 150 ? hog : victim;
                if (i >= 150)
                    victimProjects.Add(project.Id);
                seed.Projects.Add(project);
                seed.DesignSystems.Add(new DesignSystem
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    Kind = DesignSystemKinds.Design,
                    Name = "B",
                    CurrentVersionId = Guid.NewGuid(),
                    CreatedByUserId = Guid.NewGuid(),
                });
            }

            await seed.SaveChangesAsync();
        }

        FakeClock clock = new();
        DesignDriftQueue queue = new(clock);
        for (int i = 0; i < DesignDriftQueue.PerOrganizationLimit; i++)
            queue.Enqueue(Guid.NewGuid(), DesignDriftTrigger.GitHubPush, null, hog);

        DesignSystemDriftSweeper sweeper = NewSweeper(options, queue, "20", out ServiceProvider provider);
        await using ServiceProvider _ = provider;

        Dictionary<Guid, int> swept = victimProjects.ToDictionary(p => p, _ => 0);
        int resets = 0;
        for (int batch = 0; batch < 50; batch++)
        {
            await sweeper.SweepOnceAsync(CancellationToken.None);
            if (sweeper.Cursor == Guid.Empty)
                resets++;

            // The worker drains the victim's lane; the hog's work never finishes.
            foreach (Guid p in victimProjects.Where(queue.IsQueued))
            {
                swept[p]++;
                queue.TryBegin(p, out DesignDriftRequest begun);
                queue.Complete(p);
            }

            clock.Advance(TimeSpan.FromSeconds(61));

            sweeper.Deferred.Select(d => d.Project).Distinct().Count().Should().Be(sweeper.Deferred.Count, "no duplicates");
            sweeper.Deferred.Count.Should().BeLessThanOrEqualTo(DesignSystemDriftSweeper.MaxDeferred);
            sweeper.Deferred.GroupBy(d => d.Organization).Should().OnlyContain(g => g.Count() <= DesignSystemDriftSweeper.MaxDeferredPerOrganization);
        }

        resets.Should().BeGreaterThanOrEqualTo(2, "the cursor resets at the end of every cycle even though the hog keeps its deferred set");
        swept.Values.Should().OnlyContain(n => n >= 2, "the victim is re-swept every cycle");
    }

    [Fact]
    public async Task The_sweep_queues_only_projects_with_a_current_version_and_expires_stale_proposals()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-drift-sweep-{Guid.NewGuid():N}")
            .Options;

        Project covered = NewProject();
        Project uncovered = NewProject();
        Guid coveredSystem = Guid.NewGuid();
        DesignSystemChangeProposal stale = NewProposal(coveredSystem, DesignProposalStatus.Pending, DateTime.UtcNow.AddDays(-40));
        DesignSystemChangeProposal fresh = NewProposal(coveredSystem, DesignProposalStatus.Pending, DateTime.UtcNow.AddDays(-1));
        DesignSystemChangeProposal alreadyExpired = NewProposal(coveredSystem, DesignProposalStatus.Expired, DateTime.UtcNow.AddDays(-40));

        await using (InferenceApiDbContext seed = new(options))
        {
            seed.Projects.AddRange(covered, uncovered);
            seed.DesignSystems.Add(new DesignSystem
            {
                Id = coveredSystem,
                ProjectId = covered.Id,
                Kind = DesignSystemKinds.Design,
                Name = "Brand",
                CurrentVersionId = Guid.NewGuid(),
                CreatedByUserId = Guid.NewGuid(),
            });
            seed.DesignSystems.Add(new DesignSystem
            {
                Id = Guid.NewGuid(),
                ProjectId = uncovered.Id,
                Kind = DesignSystemKinds.Design,
                Name = "No version",
                CurrentVersionId = null,
                CreatedByUserId = Guid.NewGuid(),
            });
            seed.DesignSystemChangeProposals.AddRange(stale, fresh, alreadyExpired);
            await seed.SaveChangesAsync();
        }

        ServiceCollection services = new();
        services.AddScoped(_ => new InferenceApiDbContext(options));
        services.AddSingleton<ITenantContext>(new TestTenant());
        await using ServiceProvider provider = services.BuildServiceProvider();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DesignSystem:Drift:SweepIntervalMinutes"] = "360",
                ["DesignSystem:Drift:ProposalTtlDays"] = "30",
            })
            .Build();

        DesignDriftQueue queue = new(new FakeClock());
        DesignSystemDriftSweeper sweeper = new(
            queue, provider.GetRequiredService<IServiceScopeFactory>(), configuration,
            NullLogger<DesignSystemDriftSweeper>.Instance);

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(1);
        queue.IsQueued(covered.Id).Should().BeTrue();
        queue.IsQueued(uncovered.Id).Should().BeFalse("it has no current version");

        await using (InferenceApiDbContext check = new(options))
        {
            (await check.DesignSystemChangeProposals.SingleAsync(p => p.Id == stale.Id)).Status.Should().Be(DesignProposalStatus.Expired);
            (await check.DesignSystemChangeProposals.SingleAsync(p => p.Id == fresh.Id)).Status.Should().Be(DesignProposalStatus.Pending);
            (await check.DesignSystemChangeProposals.SingleAsync(p => p.Id == alreadyExpired.Id)).Status.Should().Be(DesignProposalStatus.Expired);
        }

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(0, "the project is already queued");

        DesignSystemDriftSweeper disabled = new(
            new DesignDriftQueue(new FakeClock()),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["DesignSystem:Drift:SweepIntervalMinutes"] = "0" }).Build(),
            NullLogger<DesignSystemDriftSweeper>.Instance);
        (await disabled.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Expiring_a_stale_proposal_rejects_its_draft()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-drift-expire-{Guid.NewGuid():N}")
            .Options;

        Guid systemId = Guid.NewGuid();
        DesignSystemVersion draft = new()
        {
            Id = Guid.NewGuid(),
            DesignSystemId = systemId,
            VersionNumber = 2,
            State = DesignVersionState.Draft,
            Origin = DesignVersionOrigin.StaticExtract,
            DocumentJson = "{}",
            DocumentDigest = new string('a', 64),
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow.AddDays(-40),
        };
        DesignSystemChangeProposal stale = NewProposal(systemId, DesignProposalStatus.Pending, DateTime.UtcNow.AddDays(-40));
        stale.ProposedVersionId = draft.Id;

        await using (InferenceApiDbContext seed = new(options))
        {
            seed.DesignSystemVersions.Add(draft);
            seed.DesignSystemChangeProposals.Add(stale);
            await seed.SaveChangesAsync();
        }

        ServiceCollection services = new();
        services.AddScoped(_ => new InferenceApiDbContext(options));
        services.AddSingleton<ITenantContext>(new TestTenant());
        await using ServiceProvider provider = services.BuildServiceProvider();
        DesignSystemDriftSweeper sweeper = new(
            new DesignDriftQueue(new FakeClock()),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DesignSystem:Drift:SweepIntervalMinutes"] = "360",
                ["DesignSystem:Drift:ProposalTtlDays"] = "30",
            }).Build(),
            NullLogger<DesignSystemDriftSweeper>.Instance);

        await sweeper.SweepOnceAsync(CancellationToken.None);

        await using InferenceApiDbContext check = new(options);
        (await check.DesignSystemChangeProposals.SingleAsync(p => p.Id == stale.Id)).Status
            .Should().Be(DesignProposalStatus.Expired);
        (await check.DesignSystemVersions.SingleAsync(v => v.Id == draft.Id)).State
            .Should().Be(DesignVersionState.Rejected, "an expired proposal must not leave a Draft behind");
    }

    private static Project NewProject() => new()
    {
        Id = Guid.NewGuid(),
        Name = "P",
        OwnerId = Guid.NewGuid(),
        OrganizationId = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static DesignSystemChangeProposal NewProposal(Guid systemId, DesignProposalStatus status, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        DesignSystemId = systemId,
        BaseVersionId = Guid.NewGuid(),
        ProposedVersionId = Guid.NewGuid(),
        Trigger = DesignProposalTrigger.Scheduled,
        Status = status,
        ProposedDigest = Guid.NewGuid().ToString("N"),
        CreatedAt = createdAt,
    };
}
