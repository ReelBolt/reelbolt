using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Marketplace;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketplace;

public sealed class MarketplaceRetentionSweeperTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static MarketplaceRetentionSweeper Sweeper(bool enabled = true, int interval = 1440) =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MarketplaceOptions { Enabled = enabled, RetentionSweepIntervalMinutes = interval }),
            NullLogger<MarketplaceRetentionSweeper>.Instance);

    private static MarketplaceReport Report(ReportState state, DateTime created, DateTime? handled = null) =>
        new() { Id = Guid.NewGuid(), RecipeId = Guid.NewGuid(), ReporterUserId = Guid.NewGuid(), State = state, CreatedAt = created, HandledAt = handled };

    [Fact]
    public async Task Old_audit_row_is_deleted_and_young_one_kept()
    {
        await using InferenceApiDbContext db = NewDb();
        db.MarketplaceAudit.Add(new MarketplaceAuditEntry { Id = Guid.NewGuid(), Action = "old", CreatedAt = Now.AddMonths(-13) });
        db.MarketplaceAudit.Add(new MarketplaceAuditEntry { Id = Guid.NewGuid(), Action = "young", CreatedAt = Now.AddMonths(-11) });
        await db.SaveChangesAsync();

        (int audit, _) = await Sweeper().SweepAsync(db, Now, default);

        audit.Should().Be(1);
        (await db.MarketplaceAudit.Select(a => a.Action).ToListAsync()).Should().Equal("young");
    }

    [Fact]
    public async Task Open_report_is_kept_and_old_closed_report_deleted()
    {
        await using InferenceApiDbContext db = NewDb();
        MarketplaceReport open = Report(ReportState.Open, Now.AddMonths(-20));
        MarketplaceReport closedOld = Report(ReportState.Actioned, Now.AddMonths(-20), Now.AddMonths(-14));
        MarketplaceReport closedRecent = Report(ReportState.Actioned, Now.AddMonths(-20), Now.AddMonths(-2));
        db.MarketplaceReports.AddRange(open, closedOld, closedRecent);
        await db.SaveChangesAsync();

        (_, int reports) = await Sweeper().SweepAsync(db, Now, default);

        reports.Should().Be(1);
        (await db.MarketplaceReports.Select(r => r.Id).ToListAsync()).Should().BeEquivalentTo(new[] { open.Id, closedRecent.Id });
    }

    [Fact]
    public async Task Disabled_marketplace_deletes_nothing()
    {
        await using InferenceApiDbContext db = NewDb();
        db.MarketplaceAudit.Add(new MarketplaceAuditEntry { Id = Guid.NewGuid(), Action = "old", CreatedAt = Now.AddMonths(-30) });
        await db.SaveChangesAsync();

        (int audit, int reports) = await Sweeper(enabled: false).SweepAsync(db, Now, default);

        (audit, reports).Should().Be((0, 0));
        (await db.MarketplaceAudit.CountAsync()).Should().Be(1);
    }

    private static MarketplaceAuditEntry Row(string action, DateTime created, Guid? recipe = null, Guid? version = null, string? detail = null) =>
        new() { Id = Guid.NewGuid(), Action = action, CreatedAt = created, RecipeId = recipe, VersionId = version, DetailJson = detail };

    [Fact]
    public async Task Safety_rows_and_the_whole_trail_of_their_recipe_are_kept()
    {
        await using InferenceApiDbContext db = NewDb();
        Guid incident = Guid.NewGuid();
        Guid removedForSafety = Guid.NewGuid();
        DateTime old = Now.AddMonths(-24);
        db.MarketplaceAudit.AddRange(
            Row(MarketplaceSafetyService.CsamMatchAudit, old, incident, Guid.NewGuid()),
            Row("version_submitted", old, incident),
            Row("removed", old, removedForSafety, null, "{\"reason\":\"safety\"}"),
            Row("version_approved", old, removedForSafety),
            Row("rejected", old, Guid.NewGuid(), null, "{\"reason\":\"unsafe\"}"),
            Row("version_submitted", old, Guid.NewGuid()));
        await db.SaveChangesAsync();

        (int audit, _) = await Sweeper().SweepAsync(db, Now, default);

        audit.Should().Be(1);
        (await db.MarketplaceAudit.CountAsync()).Should().Be(5);
    }

    [Fact]
    public async Task Dismissed_report_is_reduced_to_a_record_and_a_repeat_changes_nothing()
    {
        await using InferenceApiDbContext db = NewDb();
        MarketplaceReport r = Report(ReportState.Dismissed, Now.AddMonths(-20), Now.AddMonths(-14));
        r.Note = "details";
        r.HandledBy = Guid.NewGuid();
        r.Reason = ReportReason.Spam;
        db.MarketplaceReports.Add(r);
        await db.SaveChangesAsync();

        (_, int first) = await Sweeper().SweepAsync(db, Now, default);
        (int a2, int second) = await Sweeper().SweepAsync(db, Now, default);

        (first, second, a2).Should().Be((0, 0, 0));
        MarketplaceReport kept = await db.MarketplaceReports.SingleAsync();
        (kept.State, kept.Note, kept.HandledBy, kept.Reason, kept.RecipeId, kept.ReporterUserId)
            .Should().Be((ReportState.Dismissed, null, null, ReportReason.Other, r.RecipeId, r.ReporterUserId));
        kept.HandledAt.Should().Be(r.HandledAt);
    }

    [Fact]
    public async Task Closed_report_without_a_handled_date_ages_from_its_creation()
    {
        await using InferenceApiDbContext db = NewDb();
        db.MarketplaceReports.Add(Report(ReportState.Actioned, Now.AddMonths(-13)));
        db.MarketplaceReports.Add(Report(ReportState.Actioned, Now.AddMonths(-1)));
        await db.SaveChangesAsync();

        (_, int reports) = await Sweeper().SweepAsync(db, Now, default);

        reports.Should().Be(1);
    }

    [Fact]
    public async Task More_than_one_batch_is_removed()
    {
        await using InferenceApiDbContext db = NewDb();
        for (int i = 0; i < MarketplaceRetentionSweeper.BatchSize + 20; i++)
            db.MarketplaceAudit.Add(Row("version_submitted", Now.AddMonths(-13)));
        await db.SaveChangesAsync();

        (int audit, _) = await Sweeper().SweepAsync(db, Now, default);

        audit.Should().Be(MarketplaceRetentionSweeper.BatchSize + 20);
        (await db.MarketplaceAudit.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Interval_zero_does_nothing()
    {
        MarketplaceRetentionSweeper sweeper = Sweeper(interval: 0);
        await sweeper.StartAsync(default);
        await sweeper.ExecuteTask!;
        await sweeper.StopAsync(default);
    }

    [Theory]
    [InlineData("text_blocked")]
    [InlineData("preview_blocked")]
    [InlineData("content")]
    [InlineData("copyright")]
    [InlineData("illegal")]
    public async Task Moderation_findings_are_kept(string reason)
    {
        await using InferenceApiDbContext db = NewDb();
        string action = reason is "content" or "copyright" ? "removed" : "version_rejected";
        db.MarketplaceAudit.Add(Row(action, Now.AddMonths(-24), Guid.NewGuid(), null, "{\"reason\":\"" + reason + "\"}"));
        await db.SaveChangesAsync();

        (int audit, _) = await Sweeper().SweepAsync(db, Now, default);

        audit.Should().Be(0);
    }

    [Fact]
    public async Task A_technical_failure_and_an_ordinary_removal_are_routine()
    {
        await using InferenceApiDbContext db = NewDb();
        db.MarketplaceAudit.Add(Row("preview_failed", Now.AddMonths(-24), Guid.NewGuid(), null, "{\"reason\":\"timeout\"}"));
        db.MarketplaceAudit.Add(Row("removed", Now.AddMonths(-24), Guid.NewGuid(), null, "{\"reason\":\"low_quality\"}"));
        await db.SaveChangesAsync();

        (int audit, _) = await Sweeper().SweepAsync(db, Now, default);

        audit.Should().Be(2);
    }

    [Fact]
    public async Task A_row_sharing_only_the_version_with_a_finding_is_kept()
    {
        await using InferenceApiDbContext db = NewDb();
        Guid version = Guid.NewGuid();
        db.MarketplaceAudit.Add(Row(MarketplaceSafetyService.CsamMatchAudit, Now.AddMonths(-24), Guid.NewGuid(), version));
        db.MarketplaceAudit.Add(Row("version_submitted", Now.AddMonths(-24), null, version));
        await db.SaveChangesAsync();

        (int audit, _) = await Sweeper().SweepAsync(db, Now, default);

        audit.Should().Be(0);
    }

    [Fact]
    public async Task A_page_of_only_protected_rows_does_not_stop_the_sweep()
    {
        await using InferenceApiDbContext db = NewDb();
        Guid incident = Guid.NewGuid();
        db.MarketplaceAudit.Add(Row(MarketplaceSafetyService.CsamMatchAudit, Now.AddMonths(-30), incident, Guid.NewGuid()));
        for (int i = 0; i < MarketplaceRetentionSweeper.BatchSize + 5; i++)
            db.MarketplaceAudit.Add(Row("version_submitted", Now.AddMonths(-29), incident));
        db.MarketplaceAudit.Add(Row("version_submitted", Now.AddMonths(-13)));
        await db.SaveChangesAsync();

        (int audit, _) = await Sweeper().SweepAsync(db, Now, default);

        audit.Should().Be(1);
    }

    [Fact]
    public async Task A_young_dismissed_report_is_left_whole()
    {
        await using InferenceApiDbContext db = NewDb();
        MarketplaceReport r = Report(ReportState.Dismissed, Now.AddMonths(-5), Now.AddMonths(-4));
        r.Note = "details";
        r.Reason = ReportReason.Spam;
        db.MarketplaceReports.Add(r);
        await db.SaveChangesAsync();

        await Sweeper().SweepAsync(db, Now, default);

        MarketplaceReport kept = await db.MarketplaceReports.SingleAsync();
        (kept.Note, kept.Reason).Should().Be(("details", ReportReason.Spam));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1440, 1440)]
    [InlineData(1_000_000, 10080)]
    public void The_interval_is_capped_at_seven_days(int minutes, int expected) =>
        MarketplaceRetentionSweeper.ClampInterval(minutes).Should().Be(expected);
}
