using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Compositions;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The project's durable quarantine marker and what it closes: the state outlives the file rows, and a stored source
/// from a run that began at or before it is refused by both resolvers while newer work keeps working.
/// </summary>
public sealed class QuarantineMarkerTests
{
    private static readonly DateTime Marker = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static WorkflowEngineDbContext NewDb(string? name = null) =>
        new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);

    private static async Task SeedProjectAsync(WorkflowEngineDbContext db, Guid projectId, DateTime? marker)
    {
        db.Projects.Add(new Project { Id = projectId, Name = "p", ContentQuarantinedAt = marker });
        await db.SaveChangesAsync();
    }

    private static ProjectFile Quarantined(Guid projectId) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        OriginalFileName = "clip.mp4",
        StorageKey = $"projects/{projectId}/userFiles/clip.mp4",
        MimeType = "video/mp4",
        CsamStatus = CsamScanStatus.Quarantined
    };

    // ------------------------------------------------------------------ the state

    [Fact]
    public async Task The_state_is_null_only_with_no_rows_and_no_marker()
    {
        Guid projectId = Guid.NewGuid();
        using WorkflowEngineDbContext db = NewDb();
        await SeedProjectAsync(db, projectId, null);

        (await ProjectQuarantineState.ReadAsync(db, projectId, CancellationToken.None)).Should().BeNull();
        (await ProjectQuarantineState.ReadMarkerAsync(db, projectId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_the_quarantined_row_keeps_the_state_and_the_marker()
    {
        Guid projectId = Guid.NewGuid();
        string name = Guid.NewGuid().ToString();
        ProjectFile file = Quarantined(projectId);
        using (WorkflowEngineDbContext db = NewDb(name))
        {
            await SeedProjectAsync(db, projectId, Marker);
            db.ProjectFiles.Add(file);
            await db.SaveChangesAsync();
        }

        using WorkflowEngineDbContext read = NewDb(name);
        string? withRow = await ProjectQuarantineState.ReadAsync(read, projectId, CancellationToken.None);
        read.ProjectFiles.Remove(await read.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == file.Id));
        await read.SaveChangesAsync();

        string? afterDelete = await ProjectQuarantineState.ReadAsync(read, projectId, CancellationToken.None);
        withRow.Should().NotBeNull();
        afterDelete.Should().NotBeNull();
        (await ProjectQuarantineState.ReadMarkerAsync(read, projectId, CancellationToken.None)).Should().Be(Marker);
    }

    [Fact]
    public async Task Releasing_the_row_keeps_the_state_non_null()
    {
        Guid projectId = Guid.NewGuid();
        string name = Guid.NewGuid().ToString();
        ProjectFile file = Quarantined(projectId);
        using WorkflowEngineDbContext db = NewDb(name);
        await SeedProjectAsync(db, projectId, Marker);
        db.ProjectFiles.Add(file);
        await db.SaveChangesAsync();

        file.CsamStatus = CsamScanStatus.Clean;
        await db.SaveChangesAsync();

        (await ProjectQuarantineState.ReadAsync(db, projectId, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_quarantined_row_with_no_marker_reads_as_now()
    {
        Guid projectId = Guid.NewGuid();
        using WorkflowEngineDbContext db = NewDb();
        db.ProjectFiles.Add(Quarantined(projectId));
        await db.SaveChangesAsync();

        DateTime before = DateTime.UtcNow;
        DateTime? effective = await ProjectQuarantineState.ReadMarkerAsync(db, projectId, CancellationToken.None);

        effective.Should().NotBeNull().And.BeOnOrAfter(before);
    }

    [Fact]
    public void A_start_at_or_before_the_marker_or_no_start_predates_it()
    {
        ProjectQuarantineState.Predates(Marker, Marker.AddSeconds(-1)).Should().BeTrue();
        ProjectQuarantineState.Predates(Marker, Marker).Should().BeTrue();
        ProjectQuarantineState.Predates(Marker, (DateTime?)null).Should().BeTrue();
        ProjectQuarantineState.Predates(Marker).Should().BeTrue("nothing bounds the output's start");
        ProjectQuarantineState.Predates(Marker, Marker + ProjectQuarantineState.ClockSkewMargin).Should().BeTrue("the other host's clock may be behind");
        ProjectQuarantineState.Predates(Marker, Marker + ProjectQuarantineState.ClockSkewMargin + TimeSpan.FromSeconds(1)).Should().BeFalse();
        ProjectQuarantineState.Predates(null, (DateTime?)null).Should().BeFalse();
    }

    [Fact]
    public void One_stamp_before_the_marker_is_enough()
    {
        DateTime before = Marker.AddHours(-1), after = Marker.AddHours(1);
        ProjectQuarantineState.Predates(Marker, before, after).Should().BeTrue("a Retry moved the run's start past an old output");
        ProjectQuarantineState.Predates(Marker, after, before).Should().BeTrue("a resume keeps the run's old start");
        ProjectQuarantineState.Predates(Marker, after, after).Should().BeFalse();
    }

    // ------------------------------------------------------------------ stored sources

    private static async Task<Guid> SeedResultAsync(WorkflowEngineDbContext db, Guid projectId, DateTime? startedAt, DateTime? executedAt = null)
    {
        WorkflowExecution run = new() { Id = Guid.NewGuid(), ProjectId = projectId, StartedAt = startedAt, FirstStartedAt = startedAt };
        WorkflowStepResult row = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowExecution = run,
            OutputStorageKey = $"projects/{projectId}/outputFiles/{Guid.NewGuid():D}/film.mp4",
            Status = StepStatus.Completed,
            ExecutedAt = executedAt ?? startedAt ?? DateTime.UtcNow
        };
        db.WorkflowExecutions.Add(run);
        db.WorkflowStepResults.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>The two stamps a result's start can be judged by: old row under a retried run, new row under a resumed one.</summary>
    public static TheoryData<int, int, bool> RunAndResultStamps() => new()
    {
        { 3600, -3600, false }, // Retry: the run's start was overwritten, the output is still the old one.
        { -3600, 3600, false }, // Resume: the run keeps its start, and this step may read what it made before.
        { 3600, 60, false },    // Within the clock margin.
        { 3600, 3600, true }
    };

    [Theory]
    [MemberData(nameof(RunAndResultStamps))]
    public async Task Both_resolvers_judge_a_result_by_its_own_start_and_its_runs(int runOffset, int resultOffset, bool allowed)
    {
        Guid projectId = Guid.NewGuid();
        using WorkflowEngineDbContext db = NewDb();
        await SeedProjectAsync(db, projectId, Marker);
        Guid id = await SeedResultAsync(db, projectId, Marker.AddSeconds(runOffset), Marker.AddSeconds(resultOffset));

        (await ResolveAsync(db, projectId, id)).StorageKey.Should().Match<string?>(k => (k != null) == allowed);
        (string? key, _) = await TimelineRenderStepExecutor.ResolveStorageKeyAsync(
            db, projectId, new AssetRef(AssetRefKind.StepResult, StepResultId: id), CancellationToken.None);
        key.Should().Match<string?>(k => (k != null) == allowed);
    }

    [Theory]
    [InlineData(3600, -3600, false)]
    [InlineData(-3600, 3600, false)]
    [InlineData(3600, 60, false)]
    [InlineData(3600, 3600, true)]
    public async Task A_stored_version_is_judged_by_its_creation_and_its_runs_start(int runOffset, int versionOffset, bool allowed)
    {
        Guid projectId = Guid.NewGuid();
        using WorkflowEngineDbContext db = NewDb();
        await SeedProjectAsync(db, projectId, Marker);
        WorkflowExecution run = new() { Id = Guid.NewGuid(), ProjectId = projectId, StartedAt = Marker.AddSeconds(runOffset), FirstStartedAt = Marker.AddSeconds(runOffset) };
        db.WorkflowExecutions.Add(run);
        await db.SaveChangesAsync();

        CompositionWorkspaceVersion version = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            CreatedByExecutionId = run.Id,
            CreatedAt = Marker.AddSeconds(versionOffset)
        };

        (await CompositionVersionQuarantine.PredatesAsync(db, version, CancellationToken.None)).Should().Be(!allowed);
    }

    private static Task<StepResultSourceResolution> ResolveAsync(WorkflowEngineDbContext db, Guid projectId, Guid id) =>
        StepResultSourceResolver.ResolveAsync(db, projectId,
            new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: id), allowImages: false, CancellationToken.None);

    [Theory]
    [InlineData(-3600, false)]
    [InlineData(0, false)]
    [InlineData(3600, true)]
    public async Task The_step_result_resolver_refuses_a_result_of_a_run_that_began_at_or_before_the_marker(int offsetSeconds, bool allowed)
    {
        Guid projectId = Guid.NewGuid();
        using WorkflowEngineDbContext db = NewDb();
        await SeedProjectAsync(db, projectId, Marker);
        Guid id = await SeedResultAsync(db, projectId, Marker.AddSeconds(offsetSeconds));

        StepResultSourceResolution r = await ResolveAsync(db, projectId, id);

        if (allowed)
        {
            r.StorageKey.Should().NotBeNull();
        }
        else
        {
            r.StorageKey.Should().BeNull();
            r.Code.Should().Be(StepResultSourceResolver.NotFoundCode);
            r.Message.Should().Be(StepResultSourceResolver.NotFoundMessage).And.NotContainEquivalentOf("quarantin");
        }
    }

    [Fact]
    public async Task A_result_of_a_run_with_no_start_is_refused_once_the_project_has_a_marker()
    {
        Guid projectId = Guid.NewGuid();
        using WorkflowEngineDbContext db = NewDb();
        await SeedProjectAsync(db, projectId, Marker);
        Guid id = await SeedResultAsync(db, projectId, null);

        (await ResolveAsync(db, projectId, id)).Code.Should().Be(StepResultSourceResolver.NotFoundCode);
    }

    [Fact]
    public async Task The_step_result_resolver_answers_not_found_when_the_state_cannot_be_read_and_lets_cancellation_through()
    {
        Guid projectId = Guid.NewGuid();
        string name = Guid.NewGuid().ToString();
        var failure = new FailProjectFileReads();
        using WorkflowEngineDbContext db = new(FailProjectFileReads.Options(name, failure));
        Guid id = await SeedResultAsync(db, projectId, Marker.AddHours(1));
        (await ResolveAsync(db, projectId, id)).StorageKey.Should().NotBeNull("with a readable state, a newer result resolves");

        failure.ArmFor(db);
        (await ResolveAsync(db, projectId, id)).Code.Should().Be(StepResultSourceResolver.NotFoundCode);

        failure.Armed = false;
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        Func<Task> cancelled = () => ResolveAsync(db, projectId, id, cts.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    private static Task<StepResultSourceResolution> ResolveAsync(WorkflowEngineDbContext db, Guid projectId, Guid id, CancellationToken ct) =>
        StepResultSourceResolver.ResolveAsync(db, projectId,
            new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: id), allowImages: false, ct);

    [Theory]
    [InlineData(-3600, false)]
    [InlineData(3600, true)]
    public async Task The_timeline_render_resolver_refuses_an_older_result_and_stages_a_newer_one(int offsetSeconds, bool allowed)
    {
        Guid projectId = Guid.NewGuid();
        using WorkflowEngineDbContext db = NewDb();
        await SeedProjectAsync(db, projectId, Marker);
        Guid id = await SeedResultAsync(db, projectId, Marker.AddSeconds(offsetSeconds));

        (string? key, _) = await TimelineRenderStepExecutor.ResolveStorageKeyAsync(
            db, projectId, new AssetRef(AssetRefKind.StepResult, StepResultId: id), CancellationToken.None);

        if (allowed) key.Should().NotBeNull();
        else key.Should().BeNull("the caller answers ASSET_UNRESOLVED");
    }

    [Fact]
    public async Task The_timeline_render_resolver_refuses_when_the_state_cannot_be_read()
    {
        Guid projectId = Guid.NewGuid();
        var failure = new FailProjectFileReads();
        using WorkflowEngineDbContext db = new(FailProjectFileReads.Options(Guid.NewGuid().ToString(), failure));
        await SeedProjectAsync(db, projectId, null);
        Guid id = await SeedResultAsync(db, projectId, DateTime.UtcNow);
        AssetRef reference = new(AssetRefKind.StepResult, StepResultId: id);
        (string? ok, _) = await TimelineRenderStepExecutor.ResolveStorageKeyAsync(db, projectId, reference, CancellationToken.None);
        ok.Should().NotBeNull();

        failure.ArmFor(db);
        (string? key, _) = await TimelineRenderStepExecutor.ResolveStorageKeyAsync(db, projectId, reference, CancellationToken.None);

        key.Should().BeNull();
    }
}
