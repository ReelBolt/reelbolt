using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// The sweeper covers rows no request path reached and must span organizations: it runs with
/// IgnoreQueryFilters, so a row written by cloud/GitHub sync, the assistant or the engine in any
/// tenant is found.
/// </summary>
public class CsamScanSweeperTests
{
    [Fact]
    public async Task Sweeper_ScansPendingRowsAcrossTenants_AndSkipsYoungOrSettledRows()
    {
        Guid orgA = Guid.NewGuid();
        Guid orgB = Guid.NewGuid();
        string database = $"csam-sweeper-{Guid.NewGuid():N}";

        RecordingMatcher matcher = new();
        CsamOptions options = CsamTestSupport.EnabledOptions();
        options.Sweeper.BatchSize = 25;
        options.Sweeper.IntervalMinutes = 15;

        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(database));
        // Pinned to org A and NOT a system scope: selecting org B's row therefore proves the
        // IgnoreQueryFilters() call, not a system-scope bypass.
        services.AddSingleton<ITenantContext>(new CsamFixedTenant(orgA));
        services.AddSingleton<ICsmaHashMatcher>(matcher);
        services.AddSingleton(Options.Create(options));

        await using ServiceProvider provider = services.BuildServiceProvider();

        Guid pendingA = Guid.NewGuid();
        Guid pendingB = Guid.NewGuid();
        Guid young = Guid.NewGuid();
        Guid clean = Guid.NewGuid();
        using (IServiceScope seedScope = provider.CreateScope())
        {
            InferenceApiDbContext db = seedScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            Project projectA = new() { Id = Guid.NewGuid(), Name = "A", OrganizationId = orgA, OwnerId = Guid.NewGuid() };
            Project projectB = new() { Id = Guid.NewGuid(), Name = "B", OrganizationId = orgB, OwnerId = Guid.NewGuid() };
            db.Projects.AddRange(projectA, projectB);
            db.ProjectFiles.AddRange(
                Row(pendingA, projectA.Id, CsamScanStatus.Pending, DateTime.UtcNow.AddMinutes(-5)),
                Row(pendingB, projectB.Id, CsamScanStatus.Pending, DateTime.UtcNow.AddMinutes(-5)),
                Row(young, projectA.Id, CsamScanStatus.Pending, DateTime.UtcNow),
                Row(clean, projectA.Id, CsamScanStatus.Clean, DateTime.UtcNow.AddDays(-1)));
            await db.SaveChangesAsync();
        }

        CsamScanSweeper sweeper = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            NullLogger<CsamScanSweeper>.Instance);

        int scanned = await sweeper.SweepOnceAsync(CancellationToken.None);

        scanned.Should().Be(2);
        matcher.Scanned.Should().BeEquivalentTo(new[] { pendingA, pendingB });
        matcher.Scanned.Should().NotContain(young, "a row younger than the grace period may still be scanned in-request");
        matcher.Scanned.Should().NotContain(clean);
    }

    [Fact]
    public async Task ADisabledSweeper_DoesNothing()
    {
        CsamOptions options = CsamTestSupport.EnabledOptions();
        options.Sweeper.IntervalMinutes = 0;
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase($"csam-sweeper-off-{Guid.NewGuid():N}"));
        services.AddSingleton<ICsmaHashMatcher>(new RecordingMatcher());
        services.AddSingleton(Options.Create(options));
        await using ServiceProvider provider = services.BuildServiceProvider();

        CsamScanSweeper sweeper = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            NullLogger<CsamScanSweeper>.Instance);

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Sweep_WhenDisabled_SettlesPendingRowsAsNotScanned()
    {
        Guid org = Guid.NewGuid();
        string database = $"csam-sweeper-disabled-{Guid.NewGuid():N}";

        RecordingMatcher matcher = new();
        CsamOptions options = CsamTestSupport.EnabledOptions(o => o.Enabled = false);
        options.Sweeper.IntervalMinutes = 15;
        options.Sweeper.RescanNotScanned = true;

        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(database));
        services.AddSingleton<ITenantContext>(new CsamFixedTenant(org));
        services.AddSingleton<ICsmaHashMatcher>(matcher);
        services.AddSingleton(Options.Create(options));
        await using ServiceProvider provider = services.BuildServiceProvider();

        using (IServiceScope seedScope = provider.CreateScope())
        {
            InferenceApiDbContext db = seedScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            Project project = new() { Id = Guid.NewGuid(), Name = "A", OrganizationId = org, OwnerId = Guid.NewGuid() };
            db.Projects.Add(project);
            db.ProjectFiles.Add(Row(Guid.NewGuid(), project.Id, CsamScanStatus.Pending, DateTime.UtcNow.AddMinutes(-5)));
            await db.SaveChangesAsync();
        }

        CsamScanSweeper sweeper = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            NullLogger<CsamScanSweeper>.Instance);

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(1,
            "a disabled control must not leave a Pending row blocking moderation forever");
        matcher.Scanned.Should().BeEmpty("the inline post-add path is the one that records 'disabled'");

        using IServiceScope checkScope = provider.CreateScope();
        InferenceApiDbContext check = checkScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        check.ProjectFiles.IgnoreQueryFilters().Single().CsamStatus.Should().Be(CsamScanStatus.NotScanned);
        CsamScanAudit audit = check.CsamScanAudits.IgnoreQueryFilters().Single();
        audit.Status.Should().Be("disabled");
        audit.ErrorCode.Should().Be("DISABLED");
    }

    [Fact]
    public async Task DisabledSweep_SettlesOnlyAnOldPendingRow_AndNeverTouchesAQuarantinedOne()
    {
        Guid org = Guid.NewGuid();
        string database = $"csam-sweeper-disabled-age-{Guid.NewGuid():N}";
        CsamOptions options = CsamTestSupport.EnabledOptions(o => o.Enabled = false);
        options.Sweeper.IntervalMinutes = 15;

        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(database));
        services.AddSingleton<ITenantContext>(new CsamFixedTenant(org));
        services.AddSingleton<ICsmaHashMatcher>(new RecordingMatcher());
        services.AddSingleton(Options.Create(options));
        await using ServiceProvider provider = services.BuildServiceProvider();

        Guid oldPending = Guid.NewGuid();
        Guid freshPending = Guid.NewGuid();
        Guid quarantined = Guid.NewGuid();
        using (IServiceScope seedScope = provider.CreateScope())
        {
            InferenceApiDbContext db = seedScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            Project project = new() { Id = Guid.NewGuid(), Name = "A", OrganizationId = org, OwnerId = Guid.NewGuid() };
            db.Projects.Add(project);
            ProjectFile fresh = Row(freshPending, project.Id, CsamScanStatus.Pending, DateTime.UtcNow);
            ProjectFile held = Row(quarantined, project.Id, CsamScanStatus.Quarantined, DateTime.UtcNow.AddDays(-1));
            held.CsamQuarantineKey = $"projects/{project.Id}/quarantine/{quarantined}/a.jpg";
            db.ProjectFiles.AddRange(
                Row(oldPending, project.Id, CsamScanStatus.Pending, DateTime.UtcNow.AddMinutes(-5)),
                fresh, held);
            await db.SaveChangesAsync();
        }

        CsamScanSweeper sweeper = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options), NullLogger<CsamScanSweeper>.Instance);
        int settled = await sweeper.SweepOnceAsync(CancellationToken.None);

        settled.Should().Be(1, "only the stale Pending row is settled");
        using IServiceScope checkScope = provider.CreateScope();
        InferenceApiDbContext check = checkScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        Dictionary<Guid, CsamScanStatus> statuses = check.ProjectFiles.IgnoreQueryFilters()
            .ToDictionary(f => f.Id, f => f.CsamStatus);
        statuses[oldPending].Should().Be(CsamScanStatus.NotScanned);
        statuses[freshPending].Should().Be(CsamScanStatus.Pending, "a fresh row is still inside its grace window");
        statuses[quarantined].Should().Be(CsamScanStatus.Quarantined, "a held row is never touched");
    }

    [Fact]
    public async Task Sweeper_ClaimsPendingRowsAtomically()
    {
        Guid org = Guid.NewGuid();
        string database = $"csam-sweeper-claim-{Guid.NewGuid():N}";
        CsamOptions options = CsamTestSupport.EnabledOptions();
        options.Sweeper.BatchSize = 25;
        options.Sweeper.IntervalMinutes = 15;

        Guid id = Guid.NewGuid();
        RecordingMatcher first = new();
        RecordingMatcher second = new();
        await using ServiceProvider providerA = BuildProvider(database, org, options, first);
        await using ServiceProvider providerB = BuildProvider(database, org, options, second);

        using (IServiceScope seedScope = providerA.CreateScope())
        {
            InferenceApiDbContext db = seedScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            Project project = new() { Id = Guid.NewGuid(), Name = "A", OrganizationId = org, OwnerId = Guid.NewGuid() };
            db.Projects.Add(project);
            db.ProjectFiles.Add(Row(id, project.Id, CsamScanStatus.Pending, DateTime.UtcNow.AddMinutes(-5)));
            await db.SaveChangesAsync();
        }

        int firstRun = await BuildSweeper(providerA, options).SweepOnceAsync(CancellationToken.None);
        int secondRun = await BuildSweeper(providerB, options).SweepOnceAsync(CancellationToken.None);

        firstRun.Should().Be(1);
        secondRun.Should().Be(0, "the first sweeper claimed the row; a second instance must not scan it");
        first.Scanned.Should().Equal(id);
        second.Scanned.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweeper_RecoversAStaleScanningRow()
    {
        Guid org = Guid.NewGuid();
        string database = $"csam-sweeper-stale-{Guid.NewGuid():N}";
        CsamOptions options = CsamTestSupport.EnabledOptions();
        options.Sweeper.IntervalMinutes = 15;
        RecordingMatcher matcher = new();
        await using ServiceProvider provider = BuildProvider(database, org, options, matcher);

        Guid stale = Guid.NewGuid();
        Guid fresh = Guid.NewGuid();
        using (IServiceScope seedScope = provider.CreateScope())
        {
            InferenceApiDbContext db = seedScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            Project project = new() { Id = Guid.NewGuid(), Name = "A", OrganizationId = org, OwnerId = Guid.NewGuid() };
            db.Projects.Add(project);
            ProjectFile staleRow = Row(stale, project.Id, CsamScanStatus.Scanning, DateTime.UtcNow.AddMinutes(-5));
            staleRow.CsamScanClaimedAt = DateTime.UtcNow - CsamScanSweeper.ClaimStaleAfter - TimeSpan.FromMinutes(1);
            ProjectFile freshRow = Row(fresh, project.Id, CsamScanStatus.Scanning, DateTime.UtcNow.AddMinutes(-5));
            freshRow.CsamScanClaimedAt = DateTime.UtcNow;
            db.ProjectFiles.AddRange(staleRow, freshRow);
            await db.SaveChangesAsync();
        }

        int scanned = await BuildSweeper(provider, options).SweepOnceAsync(CancellationToken.None);

        scanned.Should().Be(1);
        matcher.Scanned.Should().Equal(stale);
        matcher.Scanned.Should().NotContain(fresh, "a fresh claim belongs to a live worker");
    }

    [Fact]
    public async Task Sweeper_ClaimsAStaleNotScannedRow_WhenRescanIsOn()
    {
        Guid org = Guid.NewGuid();
        string database = $"csam-sweeper-notscanned-{Guid.NewGuid():N}";
        CsamOptions options = CsamTestSupport.EnabledOptions();
        options.Sweeper.IntervalMinutes = 15;
        options.Sweeper.RescanNotScanned = true;
        RecordingMatcher matcher = new();
        await using ServiceProvider provider = BuildProvider(database, org, options, matcher);

        Guid stale = Guid.NewGuid();
        Guid fresh = Guid.NewGuid();
        using (IServiceScope seedScope = provider.CreateScope())
        {
            InferenceApiDbContext db = seedScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            Project project = new() { Id = Guid.NewGuid(), Name = "A", OrganizationId = org, OwnerId = Guid.NewGuid() };
            db.Projects.Add(project);
            ProjectFile staleRow = Row(stale, project.Id, CsamScanStatus.NotScanned, DateTime.UtcNow.AddDays(-2));
            staleRow.CsamScannedAt = DateTime.UtcNow - CsamScanSweeper.NotScannedRescanAge - TimeSpan.FromHours(1);
            ProjectFile freshRow = Row(fresh, project.Id, CsamScanStatus.NotScanned, DateTime.UtcNow);
            freshRow.CsamScannedAt = DateTime.UtcNow;
            db.ProjectFiles.AddRange(staleRow, freshRow);
            await db.SaveChangesAsync();
        }

        int scanned = await BuildSweeper(provider, options).SweepOnceAsync(CancellationToken.None);

        scanned.Should().Be(1);
        matcher.Scanned.Should().Equal(stale);
        matcher.Scanned.Should().NotContain(fresh, "a recent not-scanned row waits the full retry age");
    }

    // ISSUES-ig-reel-2026-11-03 #6: a project whose outputs wait on a pending scan is scanned at once, not at the
    // next fifteen-minute sweep, and the output-moderation lane is woken when the scan is done.

    [Fact]
    public async Task A_requested_project_is_scanned_now_without_the_grace_and_only_that_project()
    {
        Guid org = Guid.NewGuid();
        RecordingMatcher matcher = new();
        CsamOptions options = CsamTestSupport.EnabledOptions();
        options.Sweeper.IntervalMinutes = 15;
        await using ServiceProvider provider = BuildProvider($"csam-request-{Guid.NewGuid():N}", org, options, matcher);

        Guid projectA = Guid.NewGuid();
        Guid projectB = Guid.NewGuid();
        Guid justWritten = Guid.NewGuid();
        Guid older = Guid.NewGuid();
        Guid clean = Guid.NewGuid();
        Guid otherProject = Guid.NewGuid();
        using (IServiceScope seedScope = provider.CreateScope())
        {
            InferenceApiDbContext db = seedScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.Projects.AddRange(
                new Project { Id = projectA, Name = "A", OrganizationId = org, OwnerId = Guid.NewGuid() },
                new Project { Id = projectB, Name = "B", OrganizationId = org, OwnerId = Guid.NewGuid() });
            db.ProjectFiles.AddRange(
                Row(justWritten, projectA, CsamScanStatus.Pending, DateTime.UtcNow),
                Row(older, projectA, CsamScanStatus.Pending, DateTime.UtcNow.AddMinutes(-5)),
                Row(clean, projectA, CsamScanStatus.Clean, DateTime.UtcNow.AddMinutes(-5)),
                Row(otherProject, projectB, CsamScanStatus.Pending, DateTime.UtcNow.AddMinutes(-5)));
            await db.SaveChangesAsync();
        }

        OutputModerationWake wake = new();
        CsamScanSweeper sweeper = new(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
            NullLogger<CsamScanSweeper>.Instance, wake: wake);

        int scanned = await sweeper.ScanProjectAsync(projectA, CancellationToken.None);

        scanned.Should().Be(2);
        matcher.Scanned.Should().BeEquivalentTo([justWritten, older],
            "an engine-written row has no in-request scan to wait for, and the output it holds is waiting now");
        matcher.Scanned.Should().NotContain(otherProject);
        (await wake.WaitAsync(TimeSpan.Zero, CancellationToken.None)).Should().BeTrue("the held outputs can be judged now");
    }

    [Fact]
    public async Task A_requested_scan_never_calls_the_matcher_while_the_control_is_off_and_settles_the_row()
    {
        Guid org = Guid.NewGuid();
        RecordingMatcher matcher = new();
        CsamOptions options = CsamTestSupport.EnabledOptions(o => o.Enabled = false);
        await using ServiceProvider provider = BuildProvider($"csam-request-off-{Guid.NewGuid():N}", org, options, matcher);
        Guid project = Guid.NewGuid();
        using (IServiceScope seedScope = provider.CreateScope())
        {
            InferenceApiDbContext db = seedScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.Projects.Add(new Project { Id = project, Name = "A", OrganizationId = org, OwnerId = Guid.NewGuid() });
            db.ProjectFiles.Add(Row(Guid.NewGuid(), project, CsamScanStatus.Pending, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        (await BuildSweeper(provider, options).ScanProjectAsync(project, CancellationToken.None)).Should().Be(1, "a disabled control settles the row at once");
        matcher.Scanned.Should().BeEmpty();
    }

    [Fact]
    public async Task A_periodic_sweep_that_scanned_something_wakes_the_output_lane()
    {
        Guid org = Guid.NewGuid();
        CsamOptions options = CsamTestSupport.EnabledOptions();
        options.Sweeper.IntervalMinutes = 15;
        await using ServiceProvider provider = BuildProvider($"csam-wake-{Guid.NewGuid():N}", org, options, new RecordingMatcher());
        using (IServiceScope seedScope = provider.CreateScope())
        {
            InferenceApiDbContext db = seedScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            Guid project = Guid.NewGuid();
            db.Projects.Add(new Project { Id = project, Name = "A", OrganizationId = org, OwnerId = Guid.NewGuid() });
            db.ProjectFiles.Add(Row(Guid.NewGuid(), project, CsamScanStatus.Pending, DateTime.UtcNow.AddMinutes(-5)));
            await db.SaveChangesAsync();
        }

        OutputModerationWake wake = new();
        CsamScanSweeper sweeper = new(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
            NullLogger<CsamScanSweeper>.Instance, wake: wake);

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(1);
        (await wake.WaitAsync(TimeSpan.Zero, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task A_request_is_served_by_the_running_sweeper_long_before_its_interval()
    {
        Guid org = Guid.NewGuid();
        RecordingMatcher matcher = new();
        CsamOptions options = CsamTestSupport.EnabledOptions();
        options.Sweeper.IntervalMinutes = 15;
        await using ServiceProvider provider = BuildProvider($"csam-request-loop-{Guid.NewGuid():N}", org, options, matcher);
        Guid project = Guid.NewGuid();
        Guid file = Guid.NewGuid();
        using (IServiceScope seedScope = provider.CreateScope())
        {
            InferenceApiDbContext db = seedScope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.Projects.Add(new Project { Id = project, Name = "A", OrganizationId = org, OwnerId = Guid.NewGuid() });
            db.ProjectFiles.Add(Row(file, project, CsamScanStatus.Pending, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        CsamScanRequests requests = new();
        OutputModerationWake wake = new();
        CsamScanSweeper sweeper = new(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
            NullLogger<CsamScanSweeper>.Instance, requests: requests, wake: wake, startupDelay: TimeSpan.Zero);
        using CancellationTokenSource stop = new();
        await sweeper.StartAsync(stop.Token);

        requests.Request(project).Should().BeTrue();
        (await wake.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).Should().BeTrue();
        matcher.Scanned.Should().Contain(file);

        await stop.CancelAsync();
        await sweeper.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Requests_for_one_project_collapse_until_it_is_taken()
    {
        CsamScanRequests requests = new();
        Guid project = Guid.NewGuid();

        requests.Request(project).Should().BeTrue();
        requests.Request(project).Should().BeFalse("one waiting request per project is enough");
        requests.TryTake(out Guid taken).Should().BeTrue();
        taken.Should().Be(project);
        requests.Request(project).Should().BeTrue("taken: a new request may wait again");
    }

    private static ServiceProvider BuildProvider(
        string database, Guid organizationId, CsamOptions options, ICsmaHashMatcher matcher)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(database));
        services.AddSingleton<ITenantContext>(new CsamFixedTenant(organizationId));
        services.AddSingleton(matcher);
        services.AddSingleton(Options.Create(options));
        return services.BuildServiceProvider();
    }

    private static CsamScanSweeper BuildSweeper(IServiceProvider provider, CsamOptions options) => new(
        provider.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(options),
        NullLogger<CsamScanSweeper>.Instance);

    private static ProjectFile Row(Guid id, Guid projectId, CsamScanStatus status, DateTime uploadedAt) => new()
    {
        Id = id,
        ProjectId = projectId,
        OriginalFileName = $"{id:N}.jpg",
        Category = "userFiles",
        StorageKey = $"projects/{projectId}/userFiles/{id:N}.jpg",
        MimeType = "image/jpeg",
        SizeBytes = 4,
        UploadedAt = uploadedAt,
        CsamStatus = status
    };

    private sealed class RecordingMatcher : ICsmaHashMatcher
    {
        public List<Guid> Scanned { get; } = [];

        public Task<CsamScanOutcome> ScanAsync(ProjectFile file, Guid? actorUserId, CancellationToken ct)
        {
            Scanned.Add(file.Id);
            return Task.FromResult(new CsamScanOutcome(CsamScanStatus.Clean, "no-known-match", null, false, null));
        }
    }
}
