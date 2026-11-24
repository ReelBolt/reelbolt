using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Caching;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public sealed class ProjectOutputHoldTests
{
    private static readonly DateTime Marker = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private static WorkflowEngineDbContext NewDb() =>
        new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedAsync(
        WorkflowEngineDbContext db, DateTime? hold, CsamScanStatus csam = CsamScanStatus.Clean,
        FileModerationStatus moderation = FileModerationStatus.Unmoderated, bool withFile = true)
    {
        Guid id = Guid.NewGuid();
        db.Projects.Add(new Project { Id = id, Name = "p", CsamOutputHoldAt = hold });
        if (withFile)
        {
            db.ProjectFiles.Add(new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = id,
                OriginalFileName = "a.bin",
                StorageKey = $"projects/{id}/userFiles/a.bin",
                MimeType = "application/octet-stream",
                CsamStatus = csam,
                ModerationStatus = moderation
            });
        }
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Stored_marker_is_returned()
    {
        using var db = NewDb();
        Guid id = await SeedAsync(db, Marker);
        var state = await ProjectOutputHold.ReadAsync(db, id, CancellationToken.None);
        state.Marker.Should().Be(Marker);
        state.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task No_marker_and_no_flagged_row_is_inactive_and_holds_nothing()
    {
        using var db = NewDb();
        Guid id = await SeedAsync(db, null);
        var state = await ProjectOutputHold.ReadAsync(db, id, CancellationToken.None);
        state.IsActive.Should().BeFalse();
        state.HoldsOutput(null, null).Should().BeFalse();
        state.HoldsFile(null).Should().BeFalse();
    }

    [Fact]
    public async Task A_flagged_row_without_a_marker_holds()
    {
        using var db = NewDb();
        Guid id = await SeedAsync(db, null, csam: CsamScanStatus.Quarantined);
        var state = await ProjectOutputHold.ReadAsync(db, id, CancellationToken.None);
        state.IsActive.Should().BeTrue();
        state.HoldsFile(DateTime.UtcNow.AddMinutes(-1)).Should().BeTrue();
    }

    [Fact]
    public async Task A_moderation_only_row_is_ignored()
    {
        using var db = NewDb();
        Guid id = await SeedAsync(db, null, moderation: FileModerationStatus.Quarantined);
        var state = await ProjectOutputHold.ReadAsync(db, id, CancellationToken.None);
        state.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task A_throwing_context_is_unreadable_and_holds_everything()
    {
        var db = NewDb();
        db.Dispose();
        var state = await ProjectOutputHold.ReadAsync(db, Guid.NewGuid(), CancellationToken.None);
        state.Unreadable.Should().BeTrue();
        state.IsActive.Should().BeTrue();
        state.HoldsOutput(DateTime.UtcNow.AddYears(1), DateTime.UtcNow.AddYears(1)).Should().BeTrue();
        state.HoldsFile(DateTime.UtcNow.AddYears(1)).Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var db = NewDb();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> act = () => ProjectOutputHold.ReadAsync(db, Guid.NewGuid(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadMany_gives_each_project_its_own_state()
    {
        using var db = NewDb();
        Guid held = await SeedAsync(db, Marker);
        Guid rowOnly = await SeedAsync(db, null, csam: CsamScanStatus.Quarantined);
        Guid moderation = await SeedAsync(db, null, moderation: FileModerationStatus.Quarantined);
        Guid clean = await SeedAsync(db, null);
        Guid missing = Guid.NewGuid();

        var map = await ProjectOutputHold.ReadManyAsync(db, [held, rowOnly, moderation, clean, missing, held], CancellationToken.None);

        map[held].Marker.Should().Be(Marker);
        map[rowOnly].IsActive.Should().BeTrue();
        map[moderation].IsActive.Should().BeFalse();
        map[clean].IsActive.Should().BeFalse();
        map[missing].IsActive.Should().BeFalse();
        map.Should().HaveCount(5);
    }

    public static IEnumerable<object?[]> OutputCases()
    {
        DateTime after = Marker + Margin + TimeSpan.FromSeconds(1);
        DateTime edge = Marker + Margin;
        DateTime old = Marker.AddDays(-1);
        yield return [after, after, false];
        yield return [edge, after, true];            // margin edge counts as before
        yield return [after, edge, true];
        yield return [old, after, true];             // Retry shape: old output, new run start
        yield return [null, after, true];
        yield return [after, null, true];
        yield return [null, null, true];
    }

    [Theory]
    [MemberData(nameof(OutputCases))]
    public void HoldsOutput_truth_table(DateTime? executedAt, DateTime? runStartedAt, bool expected) =>
        ProjectOutputHoldState.At(Marker).HoldsOutput(executedAt, runStartedAt).Should().Be(expected);

    [Fact]
    public void HoldsFile_truth_table()
    {
        var s = ProjectOutputHoldState.At(Marker);
        s.HoldsFile(Marker + Margin + TimeSpan.FromSeconds(1)).Should().BeFalse();
        s.HoldsFile(Marker + Margin).Should().BeTrue();
        s.HoldsFile(Marker.AddDays(-1)).Should().BeTrue();
        s.HoldsFile(null).Should().BeTrue();
        ProjectOutputHoldState.None.HoldsFile(null).Should().BeFalse();
        ProjectOutputHoldState.Failed.HoldsFile(Marker.AddYears(1)).Should().BeTrue();
        ProjectOutputHoldState.None.HoldsOutput(null, null).Should().BeFalse();
    }

    [Fact]
    public void The_margin_is_shared_with_the_cache_check() =>
        ProjectQuarantineState.ClockSkewMargin.Should().Be(ContentQuarantineRule.ClockSkewMargin).And.Be(Margin);

    [Fact]
    public void Derived_output_files_are_outputs_generated_media_and_picture_exports()
    {
        Guid p = Guid.NewGuid();
        ProjectOutputKeys.IsDerivedOutputFile(p, $"projects/{p}/outputFiles/a.mp4").Should().BeTrue();
        ProjectOutputKeys.IsDerivedOutputFile(p, $"projects/{p}/agentFiles/generated/a.mp4").Should().BeTrue();
        ProjectOutputKeys.IsDerivedOutputFile(p, $"projects/{p}/userFiles/thumbnails/a.png").Should().BeTrue();
        ProjectOutputKeys.IsDerivedOutputFile(p, $"projects/{p}/userFiles/a.png").Should().BeFalse();
        ProjectOutputKeys.IsDerivedOutputFile(p, $"projects/{Guid.NewGuid()}/userFiles/thumbnails/a.png").Should().BeFalse();
        ProjectOutputKeys.IsDerivedOutputFile(p, $"projects/{p}/userFiles/thumbnails/../a.png").Should().BeFalse();
        ProjectOutputKeys.IsDerivedOutputFile(p, null).Should().BeFalse();
    }

    [Fact]
    public void Backfill_sql_names_real_columns_and_leaves_moderation_alone()
    {
        string dir = AppContext.BaseDirectory;
        while (!System.IO.File.Exists(System.IO.Path.Combine(dir, "ReelBolt.sln"))) dir = System.IO.Path.GetDirectoryName(dir)!;
        string migrations = System.IO.Path.Combine(dir, "src", "ReelBolt.Inference.Api", "Migrations");
        string sql = System.IO.File.ReadAllText(System.Linq.Enumerable.Single(
            System.IO.Directory.GetFiles(migrations, "*_AddProjectCsamOutputHold.cs")));
        sql.Should().Contain("csam_output_hold_at").And.Contain("f.csam_status = 'Quarantined'")
            .And.Contain("a.status = 'match'").And.Contain("a.project_id = projects.id");
        sql.Should().NotContain("moderation_status");
    }

    [Fact]
    public void Response_constants_are_neutral()
    {
        OutputHoldResponse.Code.Should().Be("OUTPUT_UNAVAILABLE");
        OutputHoldResponse.Message.Should().Be("This output is not available.");
    }
}
