#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.CloudStorage.Core;

/// <summary>
/// The background half (docs/design/cloud-drive-sync.md "Core services"): one queue entry per link,
/// the 20-second manual cooldown, a rerun after a change made mid-sync, the worker running each sync in
/// its own scope with the stopping token, and the sweep choosing the due links.
/// </summary>
public class CloudSyncQueueTests
{
    private sealed class RecordingSyncService : ICloudSyncService
    {
        public List<(Guid LinkId, bool Force, CancellationToken Token)> Calls { get; } = [];
        public Func<Task>? During { get; set; }

        public async Task<CloudSyncOutcome> SyncAsync(Guid linkId, bool force, CancellationToken ct)
        {
            Calls.Add((linkId, force, ct));
            if (During is not null) await During();
            return new CloudSyncOutcome("synced");
        }
    }

    private static Guid Drain(CloudSyncQueue queue)
    {
        queue.Reader.TryRead(out Guid id).Should().BeTrue();
        return id;
    }

    [Fact]
    public void A_link_already_queued_is_not_queued_twice()
    {
        CloudSyncQueue queue = new(new ManualTimeProvider());
        Guid link = Guid.NewGuid();

        queue.Enqueue(link, force: false, manual: false, out _).Should().Be(CloudSyncEnqueueResult.Queued);
        queue.Enqueue(link, force: true, manual: false, out _).Should().Be(CloudSyncEnqueueResult.Queued);
        queue.Enqueue(link, force: true, manual: true, out _).Should().Be(CloudSyncEnqueueResult.AlreadySyncing);

        Drain(queue).Should().Be(link);
        queue.Reader.TryRead(out _).Should().BeFalse("one entry per link");
        queue.TryBegin(link, out bool force).Should().BeTrue();
        force.Should().BeTrue("a forced request folded into a queued one keeps its force");
        queue.IsSyncing(link).Should().BeTrue();

        queue.Complete(link);
        queue.IsSyncing(link).Should().BeFalse();
    }

    [Fact]
    public void A_manual_sync_has_a_20_second_cooldown_per_link()
    {
        ManualTimeProvider clock = new();
        CloudSyncQueue queue = new(clock);
        Guid link = Guid.NewGuid();
        Guid other = Guid.NewGuid();

        queue.Enqueue(link, force: true, manual: true, out _).Should().Be(CloudSyncEnqueueResult.Queued);
        queue.TryBegin(Drain(queue), out _);
        queue.Complete(link);
        clock.Advance(TimeSpan.FromSeconds(5));

        queue.Enqueue(link, force: true, manual: true, out TimeSpan retryAfter).Should().Be(CloudSyncEnqueueResult.TooSoon);
        retryAfter.Should().Be(TimeSpan.FromSeconds(15));
        queue.Enqueue(other, force: true, manual: true, out _).Should().Be(CloudSyncEnqueueResult.Queued, "the cooldown is per link");
        queue.Enqueue(link, force: true, manual: false, out _).Should().Be(CloudSyncEnqueueResult.Queued, "only a manual sync is throttled");
        queue.TryBegin(Drain(queue), out _);
        queue.TryBegin(Drain(queue), out _);
        queue.Complete(link);
        queue.Complete(other);

        clock.Advance(TimeSpan.FromSeconds(16));
        queue.Enqueue(link, force: true, manual: true, out _).Should().Be(CloudSyncEnqueueResult.Queued);
    }

    [Fact]
    public void A_change_made_while_a_sync_runs_makes_it_run_once_more_but_a_manual_sync_does_not()
    {
        CloudSyncQueue queue = new(new ManualTimeProvider());
        Guid link = Guid.NewGuid();
        queue.Enqueue(link, force: false, manual: false, out _);
        queue.TryBegin(Drain(queue), out _).Should().BeTrue();

        queue.Enqueue(link, force: true, manual: true, out _).Should().Be(CloudSyncEnqueueResult.AlreadySyncing);
        queue.Complete(link);
        queue.Reader.TryRead(out _).Should().BeFalse("'Sync now' during a sync is answered by that sync");

        queue.Enqueue(link, force: false, manual: false, out _);
        queue.TryBegin(Drain(queue), out _);
        queue.Enqueue(link, force: true, manual: false, out _).Should().Be(CloudSyncEnqueueResult.Queued);
        queue.Complete(link);

        Drain(queue).Should().Be(link, "the selection changed mid-sync, so it runs again");
        queue.TryBegin(link, out bool force).Should().BeTrue();
        force.Should().BeTrue();
        queue.IsSyncing(link).Should().BeTrue();
    }

    [Fact]
    public async Task The_worker_runs_each_sync_in_its_own_scope_with_the_stopping_token_and_always_completes_it()
    {
        RecordingSyncService sync = new();
        ServiceCollection services = new();
        services.AddScoped<ICloudSyncService>(_ => sync);
        services.AddSingleton<ITenantContext>(new ReelBolt.WorkflowEngine.Tests.GitHub.TestTenant());
        await using ServiceProvider provider = services.BuildServiceProvider();
        CloudSyncQueue queue = new(new ManualTimeProvider());
        CloudSyncWorker worker = new(queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CloudSyncWorker>.Instance);

        Guid link = Guid.NewGuid();
        queue.Enqueue(link, force: true, manual: true, out _);
        bool syncingDuringRun = false;
        sync.During = () =>
        {
            syncingDuringRun = queue.IsSyncing(link);
            return Task.CompletedTask;
        };
        using CancellationTokenSource stopping = new();

        await worker.ProcessAsync(Drain(queue), stopping.Token);

        sync.Calls.Should().ContainSingle();
        sync.Calls[0].LinkId.Should().Be(link);
        sync.Calls[0].Force.Should().BeTrue();
        sync.Calls[0].Token.Should().Be(stopping.Token);
        syncingDuringRun.Should().BeTrue();
        queue.IsSyncing(link).Should().BeFalse();

        sync.During = () => throw new InvalidOperationException("boom");
        queue.Enqueue(link, force: false, manual: false, out _);
        await worker.ProcessAsync(Drain(queue), stopping.Token);
        queue.IsSyncing(link).Should().BeFalse("a failing sync still releases its entry");
    }

    [Fact]
    public async Task The_sweep_queues_only_due_links_with_a_working_connection()
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection good = await f.AddConnectionAsync();
        CloudStorageConnection broken = await f.AddConnectionAsync();
        broken.LastError = "reconnect";
        await f.Db.SaveChangesAsync();

        ProjectCloudLink never = await f.AddLinkAsync(project, good);
        ProjectCloudLink stale = await f.AddLinkAsync(await f.AddProjectAsync(), good);
        stale.LastSyncedAt = f.Clock.UtcNow.AddMinutes(-31);
        ProjectCloudLink fresh = await f.AddLinkAsync(await f.AddProjectAsync(), good);
        fresh.LastSyncedAt = f.Clock.UtcNow.AddMinutes(-5);
        ProjectCloudLink orphan = await f.AddLinkAsync(await f.AddProjectAsync(), null);
        ProjectCloudLink needsReconnect = await f.AddLinkAsync(await f.AddProjectAsync(), broken);
        await f.Db.SaveChangesAsync();

        ServiceCollection services = new();
        services.AddScoped(_ => f.NewContext());
        services.AddSingleton<ICloudStorageProviderRegistry>(f.Registry);
        services.AddSingleton<ITenantContext>(f.Tenant);
        await using ServiceProvider provider = services.BuildServiceProvider();
        CloudSyncQueue queue = new(f.Clock);
        CloudSyncSweeper sweeper = new(
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CloudStorageOptions { SweepIntervalMinutes = 30 }),
            NullLogger<CloudSyncSweeper>.Instance,
            f.Clock);

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(2);
        queue.IsSyncing(never.Id).Should().BeTrue();
        queue.IsSyncing(stale.Id).Should().BeTrue();
        queue.IsSyncing(fresh.Id).Should().BeFalse();
        queue.IsSyncing(orphan.Id).Should().BeFalse();
        queue.IsSyncing(needsReconnect.Id).Should().BeFalse();

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(0, "links already queued are not queued again");

        CloudSyncSweeper disabled = new(
            new CloudSyncQueue(f.Clock),
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CloudStorageOptions { SweepIntervalMinutes = 0 }),
            NullLogger<CloudSyncSweeper>.Instance,
            f.Clock);
        (await disabled.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
    }
}
