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
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// F10 slice 3 acceptance: with two seeded organizations on different plans, the engine-side sweep
/// retires only the cache entries past the OWNING organization's retention, leaves entries inside
/// retention alone, never touches another organization's rows, and is disabled by default.
/// </summary>
public class StepCacheRetentionSweeperTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 4, 0, 0, DateTimeKind.Utc);

    /// <summary>Plan lookup: one retention-days value per organization.</summary>
    private sealed class PlanStub(Dictionary<Guid, int> retentionDays) : IOrganizationEntitlements
    {
        public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            new(new OrganizationEntitlementSet
            {
                RetentionDays = retentionDays.TryGetValue(organizationId, out int days) ? days : 30
            });
    }

    private static IServiceProvider BuildServices(Dictionary<Guid, int> retentionDays)
    {
        // The database name is captured ONCE, not inside the options lambda: a lambda-generated name
        // runs per context instance, which would give every scope its own empty database.
        string dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IOrganizationEntitlements>(new PlanStub(retentionDays));
        return services.BuildServiceProvider();
    }

    /// <summary>One configured sweeper over an existing service provider (and so over its database).</summary>
    private static StepCacheRetentionSweeper NewSweeper(
        IServiceProvider sp, Dictionary<string, string> settings = null, bool enabled = true)
    {
        var values = new Dictionary<string, string>
        {
            ["Billing:StepCacheRetention:Enabled"] = enabled ? "true" : "false"
        };
        if (settings is not null)
        {
            foreach (KeyValuePair<string, string> setting in settings)
                values[setting.Key] = setting.Value;
        }

        IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new StepCacheRetentionSweeper(
            sp.GetRequiredService<IServiceScopeFactory>(), cfg, NullLogger<StepCacheRetentionSweeper>.Instance);
    }

    /// <summary>Seeds one organization's project.</summary>
    private static async Task SeedProjectAsync(IServiceProvider sp, Guid organizationId, Guid projectId)
    {
        using IServiceScope scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        db.Projects.Add(new Project { Id = projectId, Name = $"project-{projectId:N}", OrganizationId = organizationId });
        await db.SaveChangesAsync();
    }

    private static async Task SeedEntryAsync(IServiceProvider sp, Guid projectId, string cacheKey, DateTime createdAt)
    {
        using IServiceScope scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        db.WorkflowStepCacheEntries.Add(new WorkflowStepCacheEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            CacheKey = cacheKey,
            WorkflowDefinitionId = Guid.NewGuid(),
            StepType = StepType.Extract,
            AgentType = AgentType.ExtractTransform,
            Output = "{\"result\":\"ok\"}",
            CreatedAt = createdAt
        });
        await db.SaveChangesAsync();
    }

    private static async Task<List<WorkflowStepCacheEntry>> EntriesAsync(IServiceProvider sp)
    {
        using IServiceScope scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>()
            .WorkflowStepCacheEntries.IgnoreQueryFilters().ToListAsync();
    }

    [Fact]
    public async Task Retires_only_the_entries_past_the_owning_organizations_retention()
    {
        Guid shortPlan = Guid.NewGuid(), longPlan = Guid.NewGuid();
        Guid shortProject = Guid.NewGuid(), longProject = Guid.NewGuid();
        IServiceProvider sp = BuildServices(new Dictionary<Guid, int> { [shortPlan] = 30, [longPlan] = 90 });
        StepCacheRetentionSweeper sweeper = NewSweeper(sp);

        await SeedProjectAsync(sp, shortPlan, shortProject);
        await SeedProjectAsync(sp, longPlan, longProject);

        // A 30-day plan and a 90-day plan, all four entries 60 days old: expired for the first,
        // well inside retention for the second.
        string expired = new('a', 64), fresh = new('b', 64), otherOrgOld = new('c', 64), otherOrgFresh = new('d', 64);
        await SeedEntryAsync(sp, shortProject, expired, Now.AddDays(-60));
        await SeedEntryAsync(sp, shortProject, fresh, Now.AddDays(-2));
        await SeedEntryAsync(sp, longProject, otherOrgOld, Now.AddDays(-60));
        await SeedEntryAsync(sp, longProject, otherOrgFresh, Now.AddDays(-2));

        StepCacheRetentionRunResult run = await sweeper.RunOnceAsync(Now, CancellationToken.None);

        run.Should().Be(new StepCacheRetentionRunResult(1, 2, true),
            "both projects were examined and exactly one row was past its own plan's window");
        (await EntriesAsync(sp)).Select(e => (e.ProjectId, e.CacheKey)).Should().BeEquivalentTo(new[]
        {
            (shortProject, fresh),
            (longProject, otherOrgOld),
            (longProject, otherOrgFresh)
        }, "the expired row went, the fresh one stayed, and the other organization's rows - one of them the same age as the deleted one - were never touched");
    }

    [Fact]
    public async Task Removes_more_than_one_batch_in_batches_of_one_thousand()
    {
        Guid plan = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        IServiceProvider sp = BuildServices(new Dictionary<Guid, int> { [plan] = 30 });
        StepCacheRetentionSweeper sweeper = NewSweeper(sp);

        await SeedProjectAsync(sp, plan, project);

        const int expiredCount = 1500;
        using (IServiceScope scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            for (int i = 0; i < expiredCount; i++)
            {
                db.WorkflowStepCacheEntries.Add(new WorkflowStepCacheEntry
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project,
                    CacheKey = i.ToString("x64"),
                    WorkflowDefinitionId = Guid.NewGuid(),
                    StepType = StepType.Extract,
                    AgentType = AgentType.ExtractTransform,
                    Output = "{}",
                    CreatedAt = Now.AddDays(-40).AddMinutes(i)
                });
            }

            // One row inside retention, which must survive a sweep that pages over all the others.
            db.WorkflowStepCacheEntries.Add(new WorkflowStepCacheEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = project,
                CacheKey = new string('z', 64),
                WorkflowDefinitionId = Guid.NewGuid(),
                StepType = StepType.Extract,
                AgentType = AgentType.ExtractTransform,
                Output = "{}",
                CreatedAt = Now.AddDays(-1)
            });

            await db.SaveChangesAsync();
        }

        StepCacheRetentionRunResult run = await sweeper.RunOnceAsync(Now, CancellationToken.None);

        run.RemovedEntries.Should().Be(expiredCount, "every expired row goes, across both batches of 1,000 and 500");
        run.Completed.Should().BeTrue();
        List<WorkflowStepCacheEntry> remaining = await EntriesAsync(sp);
        remaining.Should().HaveCount(1);
        remaining[0].CacheKey.Should().Be(new string('z', 64));
    }

    [Fact]
    public async Task Keep_forever_and_misconfigured_plans_are_skipped()
    {
        Guid unlimited = Guid.NewGuid(), misconfigured = Guid.NewGuid();
        Guid unlimitedProject = Guid.NewGuid(), misconfiguredProject = Guid.NewGuid();
        IServiceProvider sp = BuildServices(new Dictionary<Guid, int>
        {
            [unlimited] = OrganizationEntitlementSet.UnlimitedInt,
            [misconfigured] = 0
        });
        StepCacheRetentionSweeper sweeper = NewSweeper(sp);

        await SeedProjectAsync(sp, unlimited, unlimitedProject);
        await SeedProjectAsync(sp, misconfigured, misconfiguredProject);
        await SeedEntryAsync(sp, unlimitedProject, new string('a', 64), Now.AddDays(-4000));
        await SeedEntryAsync(sp, misconfiguredProject, new string('b', 64), Now.AddDays(-4000));

        StepCacheRetentionRunResult run = await sweeper.RunOnceAsync(Now, CancellationToken.None);

        // Neither organization is swept: "keep forever" is not an expiry, and 0 must never be read as
        // "delete everything now" - the same rule the Inference API's storage sweep applies.
        run.Should().Be(new StepCacheRetentionRunResult(0, 0, true));
        (await EntriesAsync(sp)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Stops_at_its_time_budget_and_finishes_the_work_on_the_next_run()
    {
        Guid plan = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        IServiceProvider sp = BuildServices(new Dictionary<Guid, int> { [plan] = 30 });

        await SeedProjectAsync(sp, plan, project);
        await SeedEntryAsync(sp, project, new string('a', 64), Now.AddDays(-60));

        // A zero-second budget: the pass yields before touching the first project, reports itself
        // incomplete and deletes nothing.
        StepCacheRetentionSweeper budgeted = NewSweeper(sp, new Dictionary<string, string>
        {
            ["Billing:StepCacheRetention:TimeBudgetSeconds"] = "0"
        });
        StepCacheRetentionRunResult stopped = await budgeted.RunOnceAsync(Now, CancellationToken.None);

        stopped.Should().Be(new StepCacheRetentionRunResult(0, 0, false));
        (await EntriesAsync(sp)).Should().HaveCount(1, "nothing may be deleted once the budget is spent");

        // The next run, with a budget that is not already spent, picks the work up.
        StepCacheRetentionSweeper next = NewSweeper(sp);
        StepCacheRetentionRunResult resumed = await next.RunOnceAsync(Now, CancellationToken.None);

        resumed.Should().Be(new StepCacheRetentionRunResult(1, 1, true));
        (await EntriesAsync(sp)).Should().BeEmpty();
    }

    [Fact]
    public void The_two_sweep_queries_translate_against_the_real_provider()
    {
        // The integration tests above run on EFCore.InMemory, which evaluates LINQ in memory and would
        // happily accept a predicate the real provider cannot translate. Composing the sweeper's two
        // queries against Npgsql proves both translate - the keyset page over projects (a Guid
        // comparison on a uuid column) and the per-project age filter - without a database.
        var options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseNpgsql("Host=localhost;Database=never-connected;Username=none;Password=none")
            .Options;
        using var db = new WorkflowEngineDbContext(options);
        Guid projectId = Guid.NewGuid();
        DateTime cutoff = Now.AddDays(-30);

        string entries = db.WorkflowStepCacheEntries
            .Where(e => e.ProjectId == projectId && e.CreatedAt < cutoff)
            .OrderBy(e => e.CreatedAt)
            .Take(StepCacheRetentionSweeper.BatchSize)
            .ToQueryString();

        string projects = db.Projects.AsNoTracking()
            .Where(p => p.Id > projectId)
            .OrderBy(p => p.Id)
            .Take(StepCacheRetentionSweeper.BatchSize)
            .Select(p => new { p.Id, p.OrganizationId })
            .ToQueryString();

        entries.Should().Contain("workflow_step_cache_entries").And.Contain("created_at");
        projects.Should().Contain("organization_id");
    }

    [Fact]
    public async Task Is_disabled_by_default()
    {
        Guid plan = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        IServiceProvider sp = BuildServices(new Dictionary<Guid, int> { [plan] = 30 });
        StepCacheRetentionSweeper sweeper = NewSweeper(sp, enabled: false);

        await SeedProjectAsync(sp, plan, project);
        await SeedEntryAsync(sp, project, new string('a', 64), Now.AddDays(-4000));

        await sweeper.StartAsync(CancellationToken.None);

        // Disabled: ExecuteAsync returns before entering the daily loop, so its task finishes at once.
        // An enabled sweep would instead be parked on the delay to the next daily hour, which this
        // 5-second wait would not survive.
        sweeper.ExecuteTask.Should().NotBeNull();
        await sweeper.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        (await EntriesAsync(sp)).Should().HaveCount(1, "a disabled sweep never deletes anything");
        await sweeper.StopAsync(CancellationToken.None);
    }
}
