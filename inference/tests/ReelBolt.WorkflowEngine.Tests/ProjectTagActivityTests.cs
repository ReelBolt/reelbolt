using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Projects;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Per-customer activity by project tag: sums, double counting, untagged, window, tenancy, billing-off, no cost.</summary>
public sealed class ProjectTagActivityTests : IDisposable
{
    private static readonly Guid OrgA = Guid.Parse("11111111-0000-0000-0000-0000000000a1");
    private static readonly Guid OrgB = Guid.Parse("22222222-0000-0000-0000-0000000000b2");
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;
    private readonly TestTenancy.MutableTenant _tenant = new();
    private readonly Guid _userId = Guid.NewGuid();

    public ProjectTagActivityTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
        }
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options, _tenant);
        _db.Database.EnsureCreated();
        using var engineDb = new ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext(
            new DbContextOptionsBuilder<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>().UseSqlite(_connection).Options);
        engineDb.GetService<IRelationalDatabaseCreator>().CreateTables();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }

    private sealed class FakeCurrentUser(Guid userId, Guid? organizationId, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = true;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private ProjectTagActivityController Act(Guid org, bool billing = true, bool member = true, OrganizationRole role = OrganizationRole.Member)
    {
        _tenant.ActAsOrganization(org);
        _db.ChangeTracker.Clear();
        return new ProjectTagActivityController(
            new ProjectTagActivityService(_db, new FixedTime(Now)),
            new FakeCurrentUser(_userId, org, member ? role : null),
            Options.Create(new BillingOptions { Enabled = billing }));
    }

    private Project Project(Guid org, string name)
    {
        Project p = new() { Id = Guid.NewGuid(), Name = name, OwnerId = _userId, OrganizationId = org, CreatedAt = Old, UpdatedAt = Old };
        _db.Projects.Add(p);
        _db.SaveChanges();
        return p;
    }

    private ProjectTag Tag(Guid org, string name)
    {
        ProjectTag t = new() { Id = Guid.NewGuid(), OrganizationId = org, Name = name, NameKey = name.ToLowerInvariant(), Color = "blue", CreatedAt = Old };
        _db.ProjectTags.Add(t);
        _db.SaveChanges();
        return t;
    }

    private void Link(Project p, ProjectTag t)
    {
        _db.ProjectTagLinks.Add(new ProjectTagLink { ProjectId = p.Id, TagId = t.Id });
        _db.SaveChanges();
    }

    private WorkflowExecution Run(Project p, ExecutionStatus status, DateTime startedAt, long creditsMillis = 0, string workflow = "wf")
    {
        WorkflowDefinition def = new() { Id = Guid.NewGuid(), Name = workflow, ProjectId = p.Id, CreatedAt = Old, UpdatedAt = Old };
        _db.WorkflowDefinitions.Add(def);
        WorkflowExecution e = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = def.Id,
            ProjectId = p.Id,
            OrganizationId = p.OrganizationId,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMinutes(1)
        };
        _db.WorkflowExecutions.Add(e);
        if (creditsMillis != 0) Usage(p, e.Id, creditsMillis, startedAt);
        _db.SaveChanges();
        return e;
    }

    private void Usage(Project p, Guid? executionId, long millis, DateTime at)
    {
        _db.UsageEvents.Add(new UsageEvent
        {
            Id = Guid.NewGuid(),
            OrganizationId = p.OrganizationId,
            ProjectId = p.Id,
            ExecutionId = executionId,
            CreditsMillis = millis,
            CostUsd = 9.99m,
            OccurredAt = at,
            IdempotencyKey = Guid.NewGuid().ToString()
        });
        _db.SaveChanges();
    }

    private static T Body<T>(IActionResult result) where T : class
        => (T)result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    private async Task<TagActivityOverview> Overview(Guid org, int? days = null, bool billing = true)
        => Body<TagActivityOverview>(await Act(org, billing).Overview(days, CancellationToken.None));

    [Fact]
    public async Task Per_tag_sums_double_count_and_untagged()
    {
        Project p1 = Project(OrgA, "p1"), p2 = Project(OrgA, "p2"), p3 = Project(OrgA, "p3");
        ProjectTag acme = Tag(OrgA, "acme"), beta = Tag(OrgA, "beta");
        Link(p1, acme); Link(p2, acme); Link(p2, beta);
        Run(p1, ExecutionStatus.Passed, Now.AddDays(-1), 2000);
        Run(p2, ExecutionStatus.Failed, Now.AddDays(-2), 500);
        Run(p2, ExecutionStatus.Passed, Now.AddDays(-2), 1500);
        Run(p3, ExecutionStatus.Passed, Now.AddDays(-3), 4000);

        TagActivityOverview o = await Overview(OrgA);

        TagActivityRow a = o.Tags.Single(t => t.Name == "acme");
        a.Projects.Should().Be(2); a.Runs.Should().Be(3); a.Passed.Should().Be(2); a.Failed.Should().Be(1); a.Credits.Should().Be(4.0);
        TagActivityRow b = o.Tags.Single(t => t.Name == "beta");
        b.Projects.Should().Be(1); b.Runs.Should().Be(2); b.Credits.Should().Be(2.0);
        o.Workspace.Should().Be(new ActivityTotals(3, 4, 8.0));
        o.Untagged.Should().Be(new ActivityTotals(1, 1, 4.0));
        o.Tags.Sum(t => t.Runs).Should().BeGreaterThan(o.Workspace.Runs - o.Untagged.Runs);
        o.BillingEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Window_bounds_and_days_clamp()
    {
        Project p = Project(OrgA, "p");
        ProjectTag t = Tag(OrgA, "t");
        Link(p, t);
        Run(p, ExecutionStatus.Passed, Now.AddDays(-6), 1000);
        Run(p, ExecutionStatus.Passed, Now.AddDays(-8), 1000);
        Run(p, ExecutionStatus.Passed, Now.AddDays(-400), 1000);

        (await Overview(OrgA, 7)).Tags.Single().Runs.Should().Be(1);
        (await Overview(OrgA, 30)).Tags.Single().Runs.Should().Be(2);
        TagActivityOverview clampedHigh = await Overview(OrgA, 9999);
        (clampedHigh.WindowEnd - clampedHigh.WindowStart).TotalDays.Should().Be(365);
        clampedHigh.Tags.Single().Runs.Should().Be(2);
        TagActivityOverview clampedLow = await Overview(OrgA, -5);
        (clampedLow.WindowEnd - clampedLow.WindowStart).TotalDays.Should().Be(1);
        (await Overview(OrgA, null)).WindowStart.Should().Be(Now.AddDays(-30));
    }

    [Fact]
    public async Task Other_organizations_data_never_leaks()
    {
        Project a = Project(OrgA, "a"), b = Project(OrgB, "b");
        ProjectTag ta = Tag(OrgA, "shared-name"), tb = Tag(OrgB, "other");
        Link(a, ta); Link(b, tb);
        Run(a, ExecutionStatus.Passed, Now.AddDays(-1), 1000);
        Run(b, ExecutionStatus.Passed, Now.AddDays(-1), 9000);
        Run(b, ExecutionStatus.Passed, Now.AddDays(-1), 9000);

        TagActivityOverview o = await Overview(OrgA);
        o.Tags.Should().ContainSingle().Which.Runs.Should().Be(1);
        o.Workspace.Should().Be(new ActivityTotals(1, 1, 1.0));

        (await Act(OrgA).Detail(tb.Id, null, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await Act(OrgA).Detail(Guid.NewGuid(), null, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Billing_off_nulls_credits_but_keeps_runs()
    {
        Project p = Project(OrgA, "p");
        ProjectTag t = Tag(OrgA, "t");
        Link(p, t);
        Run(p, ExecutionStatus.Passed, Now.AddDays(-1), 3000);

        TagActivityOverview o = await Overview(OrgA, billing: false);
        o.BillingEnabled.Should().BeFalse();
        o.Tags.Single().Credits.Should().BeNull();
        o.Tags.Single().Runs.Should().Be(1);
        o.Workspace.Credits.Should().BeNull();
        o.Untagged.Credits.Should().BeNull();

        TagActivityDetail d = Body<TagActivityDetail>(await Act(OrgA, billing: false).Detail(t.Id, null, CancellationToken.None));
        d.Totals.Credits.Should().BeNull();
        d.Projects.Single().Credits.Should().BeNull();
        d.RecentRuns.Single().Credits.Should().BeNull();
    }

    [Fact]
    public async Task Json_never_carries_cost()
    {
        Project p = Project(OrgA, "p");
        ProjectTag t = Tag(OrgA, "t");
        Link(p, t);
        Run(p, ExecutionStatus.Passed, Now.AddDays(-1), 3000);

        string overview = JsonSerializer.Serialize(await Overview(OrgA));
        string detail = JsonSerializer.Serialize(Body<TagActivityDetail>(await Act(OrgA).Detail(t.Id, null, CancellationToken.None)));
        overview.ToLowerInvariant().Should().NotContain("cost").And.NotContain("usd");
        detail.ToLowerInvariant().Should().NotContain("cost").And.NotContain("usd");
    }

    [Fact]
    public async Task Billing_role_gets_detail_aggregates_only()
    {
        Project p = Project(OrgA, "alpha");
        ProjectTag t = Tag(OrgA, "t");
        Link(p, t);
        Run(p, ExecutionStatus.Passed, Now.AddDays(-1), 1000);

        TagActivityDetail d = Body<TagActivityDetail>(await Act(OrgA, role: OrganizationRole.Billing).Detail(t.Id, 30, CancellationToken.None));
        d.Totals.Should().Be(new TagDetailTotals(1, 1, 1, 0, 1.0));
        d.Projects.Should().BeEmpty();
        d.RecentRuns.Should().BeEmpty();

        TagActivityDetail member = Body<TagActivityDetail>(await Act(OrgA).Detail(t.Id, 30, CancellationToken.None));
        member.Projects.Should().HaveCount(1);
        member.RecentRuns.Should().HaveCount(1);
    }

    [Fact]
    public async Task Detail_lists_projects_and_newest_runs_with_credits()
    {
        Project p1 = Project(OrgA, "alpha"), p2 = Project(OrgA, "beta"), other = Project(OrgA, "untagged");
        ProjectTag t = Tag(OrgA, "t");
        Link(p1, t); Link(p2, t);
        WorkflowExecution older = Run(p1, ExecutionStatus.Passed, Now.AddDays(-5), 1000, "Older flow");
        WorkflowExecution newer = Run(p2, ExecutionStatus.Failed, Now.AddDays(-1), 2500, "Newer flow");
        Run(other, ExecutionStatus.Passed, Now.AddDays(-1), 7000);
        Usage(p1, null, 500, Now.AddDays(-2)); // project spend with no run behind it

        Guid newerDef = newer.WorkflowDefinitionId;
        TagActivityDetail d = Body<TagActivityDetail>(await Act(OrgA).Detail(t.Id, 30, CancellationToken.None));

        d.Tag.Name.Should().Be("t");
        d.Totals.Should().Be(new TagDetailTotals(2, 2, 1, 1, 4.0));
        d.Projects.Select(x => x.Name).Should().BeEquivalentTo("alpha", "beta");
        d.Projects.Single(x => x.Name == "alpha").Credits.Should().Be(1.5);
        d.RecentRuns.Select(r => r.ExecutionId).Should().Equal(newer.Id, older.Id);
        d.RecentRuns[0].Should().Match<TagRecentRun>(r => r.WorkflowId == newerDef && r.WorkflowName == "Newer flow" && r.ProjectName == "beta" && r.Status == "Failed" && r.Credits == 2.5);
    }

    [Fact]
    public async Task Recent_runs_are_capped_at_50()
    {
        Project p = Project(OrgA, "p");
        ProjectTag t = Tag(OrgA, "t");
        Link(p, t);
        for (int i = 0; i < 55; i++) Run(p, ExecutionStatus.Passed, Now.AddMinutes(-i - 1));

        TagActivityDetail d = Body<TagActivityDetail>(await Act(OrgA).Detail(t.Id, null, CancellationToken.None));
        d.RecentRuns.Should().HaveCount(50);
        d.Totals.Runs.Should().Be(55);
    }

    [Fact]
    public async Task Caller_without_a_membership_gets_404()
    {
        (await Act(OrgA, member: false).Overview(null, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }
}
