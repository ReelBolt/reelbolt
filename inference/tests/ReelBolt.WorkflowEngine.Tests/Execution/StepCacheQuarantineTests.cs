using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Tests.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution;

/// <summary>
/// Earlier output must not be reused once a file of the project is quarantined: nothing records which files a result
/// used, so the step cache, resume and replay all judge it per project, and a failed read means no reuse.
/// </summary>
public class StepCacheQuarantineTests
{
    private static readonly StepCacheKeyInputs BaseInputs = new()
    {
        ProjectId = Guid.NewGuid(),
        StepType = StepType.VideoCompile,
        AgentType = AgentType.Custom,
        AgentDefinitionId = Guid.NewGuid(),
        AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
        MaxIterations = 0,
        VideoCompileConfigJson = "{}",
        // The run's memoized fingerprint: unchanged by a quarantine, which is the whole point.
        ProjectFileFingerprint = new string('f', 64)
    };

    private static readonly StepExecutionResult Completed = new()
    {
        Status = StepStatus.Completed,
        NextStepIndex = 1,
        Output = "{\"output\":{}}",
        OutputStorageKey = "projects/p/outputFiles/render.mp4"
    };

    private static Func<WorkflowEngineDbContext> NewDb()
    {
        string name = "quarantine-" + Guid.NewGuid().ToString("N");
        return () => new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(name).Options);
    }

    private static (string Name, InMemoryDatabaseRoot Root, Func<WorkflowEngineDbContext> Factory) NewNamedDb()
    {
        string name = "quarantine-" + Guid.NewGuid().ToString("N");
        InMemoryDatabaseRoot root = new();
        return (name, root, () => new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(name, root).Options));
    }

    /// <summary>What the API leaves behind: the project's marker is stamped, then the quarantined row goes away.</summary>
    private static async Task StampMarkerAndDeleteRowAsync(Func<WorkflowEngineDbContext> newDb, Guid projectId, Guid? fileId)
    {
        using WorkflowEngineDbContext db = newDb();
        Project? project = await db.Projects.SingleOrDefaultAsync(p => p.Id == projectId);
        if (project is null)
            db.Projects.Add(project = new Project { Id = projectId, Name = "p" });
        project.ContentQuarantinedAt = DateTime.UtcNow;
        if (fileId is { } id)
            db.ProjectFiles.Remove(await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == id));
        await db.SaveChangesAsync();
    }

    private static StepResultCache Cache(WorkflowEngineDbContext db) => new(
        db,
        new StepCacheKeyBuilder(),
        new StepCachePolicy(),
        Options.Create(new StepCacheOptions { Enabled = true, TtlHours = 24 }),
        NullLogger<StepResultCache>.Instance);

    private static ProjectFile File(Guid projectId, Action<ProjectFile>? tweak = null)
    {
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "clip.mp4",
            StorageKey = $"projects/{projectId}/userFiles/clip.mp4",
            MimeType = "video/mp4",
            UploadedAt = DateTime.UtcNow
        };
        tweak?.Invoke(file);
        return file;
    }

    private static async Task<Guid> AddFileAsync(Func<WorkflowEngineDbContext> newDb, Guid projectId, Action<ProjectFile>? tweak = null)
    {
        using WorkflowEngineDbContext db = newDb();
        ProjectFile file = File(projectId, tweak);
        db.ProjectFiles.Add(file);
        await db.SaveChangesAsync();
        return file.Id;
    }

    private static async Task QuarantineAsync(Func<WorkflowEngineDbContext> newDb, Guid fileId, bool csam = true)
    {
        using WorkflowEngineDbContext db = newDb();
        ProjectFile file = await db.ProjectFiles.SingleAsync(f => f.Id == fileId);
        if (csam)
            file.CsamStatus = CsamScanStatus.Quarantined;
        else
            file.ModerationStatus = FileModerationStatus.Quarantined;
        await db.SaveChangesAsync();
    }

    public static TheoryData<StepType, bool> ReusableSteps()
    {
        var data = new TheoryData<StepType, bool>();
        foreach (StepType type in new[]
        {
            StepType.VideoCompile, StepType.VideoAnalyze, StepType.Voiceover, StepType.EditRoom, StepType.ColorGradeRoom,
            StepType.VideoGenerate, StepType.ExtractFrame, StepType.Extract, StepType.Agent, StepType.AudioGenerate
        })
        {
            data.Add(type, true);
            data.Add(type, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ReusableSteps))]
    public async Task An_entry_stored_before_a_quarantine_is_not_served_after_it(StepType type, bool csam)
    {
        Func<WorkflowEngineDbContext> newDb = NewDb();
        StepCacheKeyInputs inputs = BaseInputs with { StepType = type };
        Guid fileId = await AddFileAsync(newDb, inputs.ProjectId);

        using (WorkflowEngineDbContext db = newDb())
            await Cache(db).StoreAsync(inputs, Completed, CancellationToken.None);
        using (WorkflowEngineDbContext db = newDb())
            (await Cache(db).TryGetAsync(inputs, CancellationToken.None)).Hit.Should().BeTrue("nothing is quarantined yet");

        await QuarantineAsync(newDb, fileId, csam);

        using (WorkflowEngineDbContext db = newDb())
            (await Cache(db).TryGetAsync(inputs, CancellationToken.None)).Hit.Should().BeFalse();
    }

    [Fact]
    public async Task A_run_that_is_already_going_stops_hitting_as_soon_as_the_file_is_quarantined()
    {
        Func<WorkflowEngineDbContext> newDb = NewDb();
        Guid fileId = await AddFileAsync(newDb, BaseInputs.ProjectId);
        using (WorkflowEngineDbContext db = newDb())
            await Cache(db).StoreAsync(BaseInputs, Completed, CancellationToken.None);

        using WorkflowEngineDbContext running = newDb();
        StepResultCache cache = Cache(running);
        (await cache.TryGetAsync(BaseInputs, CancellationToken.None)).Hit.Should().BeTrue();

        await QuarantineAsync(newDb, fileId);

        (await cache.TryGetAsync(BaseInputs, CancellationToken.None)).Hit.Should().BeFalse("the state is never memoized");
    }

    [Fact]
    public async Task A_step_that_finishes_after_a_quarantine_stores_nothing()
    {
        Func<WorkflowEngineDbContext> newDb = NewDb();
        Guid fileId = await AddFileAsync(newDb, BaseInputs.ProjectId);

        using (WorkflowEngineDbContext running = newDb())
        {
            StepResultCache cache = Cache(running);
            await cache.CaptureQuarantineBaselineAsync(BaseInputs.ProjectId, CancellationToken.None);
            await QuarantineAsync(newDb, fileId);
            await cache.StoreAsync(BaseInputs, Completed, CancellationToken.None);
        }

        using WorkflowEngineDbContext read = newDb();
        (await read.WorkflowStepCacheEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_project_with_an_old_quarantine_still_caches_runs_started_after_it()
    {
        Func<WorkflowEngineDbContext> newDb = NewDb();
        Guid fileId = await AddFileAsync(newDb, BaseInputs.ProjectId);
        await QuarantineAsync(newDb, fileId);

        using (WorkflowEngineDbContext db = newDb())
            await Cache(db).StoreAsync(BaseInputs, Completed, CancellationToken.None);
        using (WorkflowEngineDbContext db = newDb())
            (await Cache(db).TryGetAsync(BaseInputs, CancellationToken.None)).Hit.Should().BeTrue();

        Guid second = await AddFileAsync(newDb, BaseInputs.ProjectId);
        await QuarantineAsync(newDb, second, csam: false);
        using (WorkflowEngineDbContext db = newDb())
            (await Cache(db).TryGetAsync(BaseInputs, CancellationToken.None)).Hit.Should().BeFalse("a second quarantine moves the key again");
    }

    [Fact]
    public async Task A_quarantine_in_another_project_changes_nothing()
    {
        Func<WorkflowEngineDbContext> newDb = NewDb();
        Guid foreign = await AddFileAsync(newDb, Guid.NewGuid());
        using (WorkflowEngineDbContext db = newDb())
            await Cache(db).StoreAsync(BaseInputs, Completed, CancellationToken.None);

        await QuarantineAsync(newDb, foreign);

        using WorkflowEngineDbContext read = newDb();
        (await Cache(read).TryGetAsync(BaseInputs, CancellationToken.None)).Hit.Should().BeTrue();
    }

    [Fact]
    public void Keys_are_byte_identical_with_no_quarantine_and_differ_with_one()
    {
        var builder = new StepCacheKeyBuilder();
        builder.Build(BaseInputs with { QuarantineState = null }).Should().Be(builder.Build(BaseInputs));
        builder.Build(BaseInputs with { QuarantineState = "a" }).Should().NotBe(builder.Build(BaseInputs));
        builder.Build(BaseInputs with { QuarantineState = "a" }).Should().NotBe(builder.Build(BaseInputs with { QuarantineState = "b" }));
    }

    [Fact]
    public async Task The_state_digest_covers_a_csam_and_a_moderation_quarantine_and_ignores_clean_rows()
    {
        Func<WorkflowEngineDbContext> newDb = NewDb();
        Guid projectId = Guid.NewGuid();
        await AddFileAsync(newDb, projectId, f => f.CsamStatus = CsamScanStatus.Clean);
        async Task<string?> Read()
        {
            using WorkflowEngineDbContext db = newDb();
            return await ProjectQuarantineState.ReadAsync(db, projectId, CancellationToken.None);
        }

        (await Read()).Should().BeNull();
        await AddFileAsync(newDb, projectId, f => f.CsamStatus = CsamScanStatus.Quarantined);
        string? one = await Read();
        one.Should().NotBeNull();
        await AddFileAsync(newDb, projectId, f => f.ModerationStatus = FileModerationStatus.Quarantined);
        (await Read()).Should().NotBeNull().And.NotBe(one);
    }

    [Fact]
    public async Task A_failed_state_read_on_lookup_is_a_miss_even_with_a_live_entry()
    {
        string name = "quarantine-fail-" + Guid.NewGuid().ToString("N");
        InMemoryDatabaseRoot root = new();
        using (WorkflowEngineDbContext db = new(FailProjectFileReads.Options(name, new FailProjectFileReads(), root)))
            await Cache(db).StoreAsync(BaseInputs, Completed, CancellationToken.None);
        using (WorkflowEngineDbContext db = new(FailProjectFileReads.Options(name, new FailProjectFileReads(), root)))
            (await Cache(db).TryGetAsync(BaseInputs, CancellationToken.None)).Hit.Should().BeTrue("the entry is live while the read works");

        using WorkflowEngineDbContext read = new(FailProjectFileReads.Options(name, new FailProjectFileReads { Armed = true }, root));
        (await Cache(read).TryGetAsync(BaseInputs, CancellationToken.None)).Hit.Should().BeFalse();
    }

    [Fact]
    public async Task A_baseline_that_could_not_be_read_means_the_run_stores_nothing()
    {
        var failure = new FailProjectFileReads { Armed = true };
        using WorkflowEngineDbContext db = new(FailProjectFileReads.Options("quarantine-fail-" + Guid.NewGuid().ToString("N"), failure));
        StepResultCache cache = Cache(db);

        await cache.CaptureQuarantineBaselineAsync(BaseInputs.ProjectId, CancellationToken.None);
        failure.Armed = false;
        await cache.StoreAsync(BaseInputs, Completed, CancellationToken.None);

        (await db.WorkflowStepCacheEntries.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------ the run service

    private sealed class RecordingBaselineCache : IStepResultCache
    {
        public List<Guid> Baselines { get; } = [];

        public Task CaptureQuarantineBaselineAsync(Guid projectId, CancellationToken ct)
        {
            Baselines.Add(projectId);
            return Task.CompletedTask;
        }

        public Task<StepCacheLookupResult> TryGetAsync(StepCacheKeyInputs inputs, CancellationToken ct) => Task.FromResult(StepCacheLookupResult.Miss);
        public Task StoreAsync(StepCacheKeyInputs inputs, StepExecutionResult result, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task The_run_takes_its_baseline_before_the_first_step_runs()
    {
        Func<WorkflowEngineDbContext> newDb = NewDb();
        ExecutionResumeTests.Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = ExecutionResumeTests.Seed(seed, new ExecutionResumeTests.StepSpec(StepType.Agent, "A"));

        var cache = new RecordingBaselineCache();
        int baselinesWhenStepRan = -1;
        await ExecutionResumeTests.Service(new VoiceReuseGuardTests.DbScopes(newDb, cache), null, null, null, cached: true,
            new ExecutionResumeTests.Executor(StepType.Agent, c =>
            {
                baselinesWhenStepRan = cache.Baselines.Count;
                return Task.FromResult(ExecutionResumeTests.Done(c, "{}"));
            })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        baselinesWhenStepRan.Should().Be(1);
        cache.Baselines.Should().Equal(s.ProjectId);
    }

    private static async Task<List<string>> RunResumedAsync(bool quarantine, bool deleteRow = false, bool failRead = false)
    {
        (string dbName, InMemoryDatabaseRoot dbRoot, Func<WorkflowEngineDbContext> newDb) = NewNamedDb();
        ExecutionResumeTests.Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = ExecutionResumeTests.Seed(seed, new ExecutionResumeTests.StepSpec(StepType.VideoAnalyze, "V"), new ExecutionResumeTests.StepSpec(StepType.Agent, "A"));
        Guid fileId = await AddFileAsync(newDb, s.ProjectId);

        var scopes = new VoiceReuseGuardTests.DbScopes(newDb);
        Func<Task> first = () => ExecutionResumeTests.Service(scopes, null, null, null,
            new ExecutionResumeTests.Executor(StepType.VideoAnalyze, c => Task.FromResult(ExecutionResumeTests.Done(c, "{\"recorded\":true}"))),
            new ExecutionResumeTests.Executor(StepType.Agent, c => throw new InvalidOperationException("provider down")))
            .ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
        await first.Should().ThrowAsync<Exception>();

        if (quarantine)
            await QuarantineAsync(newDb, fileId);
        if (deleteRow)
            await StampMarkerAndDeleteRowAsync(newDb, s.ProjectId, fileId);

        await ExecutionResumeTests.RequeueAsync(newDb, s.ExecutionId, resume: true);
        List<string> ran = [];
        var failure = new FailProjectFileReads { Armed = failRead };
        DbContextOptions<WorkflowEngineDbContext> failing = FailProjectFileReads.Options(dbName, failure, dbRoot);
        var secondScopes = failRead
            ? new VoiceReuseGuardTests.DbScopes(() => new WorkflowEngineDbContext(failing))
            : scopes;
        await ExecutionResumeTests.Service(secondScopes, null, null, null,
            new ExecutionResumeTests.Executor(StepType.VideoAnalyze, c => { ran.Add("analyze"); return Task.FromResult(ExecutionResumeTests.Done(c, "{}")); }),
            new ExecutionResumeTests.Executor(StepType.Agent, c => { ran.Add("agent"); return Task.FromResult(ExecutionResumeTests.Done(c, "{}")); }))
            .ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
        return ran;
    }

    [Fact]
    public async Task Resume_reuses_the_recorded_result_when_nothing_is_quarantined()
        => (await RunResumedAsync(quarantine: false)).Should().Equal("agent");

    [Fact]
    public async Task Resume_runs_the_step_live_after_a_quarantine()
        => (await RunResumedAsync(quarantine: true)).Should().Equal("analyze", "agent");

    [Fact]
    public async Task Resume_still_runs_the_step_live_after_the_quarantined_row_is_deleted()
        => (await RunResumedAsync(quarantine: true, deleteRow: true)).Should().Equal("analyze", "agent");

    [Fact]
    public async Task Resume_runs_the_step_live_when_the_state_cannot_be_read()
        => (await RunResumedAsync(quarantine: false, failRead: true)).Should().Equal("analyze", "agent");

    private static async Task<List<string>> RunReplayAsync(bool quarantined, bool rowDeleted = false, bool failRead = false, DateTime? marker = null, DateTime? sourceFirstStartedAt = null)
    {
        (string dbName, InMemoryDatabaseRoot dbRoot, Func<WorkflowEngineDbContext> newDb) = NewNamedDb();
        ExecutionResumeTests.Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
        {
            s = ExecutionResumeTests.Seed(seed, new ExecutionResumeTests.StepSpec(StepType.VideoAnalyze, "V"), new ExecutionResumeTests.StepSpec(StepType.VideoCompile, "C"));
            if (marker is not null)
            {
                Project project = await seed.Projects.FirstOrDefaultAsync(p => p.Id == s.ProjectId) ?? seed.Add(new Project { Id = s.ProjectId, Name = "p" }).Entity;
                project.ContentQuarantinedAt = marker;
            }

            seed.ProjectFiles.Add(File(s.ProjectId, f =>
            {
                if (quarantined)
                    f.CsamStatus = CsamScanStatus.Quarantined;
            }));

            WorkflowExecution run = await seed.WorkflowExecutions.Include(e => e.WorkflowDefinition).SingleAsync(e => e.Id == s.ExecutionId);
            List<WorkflowStep> steps = await seed.WorkflowSteps.Where(x => x.WorkflowDefinitionId == run.WorkflowDefinitionId).OrderBy(x => x.StepOrder).ToListAsync();
            var source = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = run.WorkflowDefinitionId,
                ProjectId = s.ProjectId,
                OrganizationId = s.OrganizationId,
                Status = ExecutionStatus.Passed,
                QueuedAt = DateTime.UtcNow,
                FirstStartedAt = sourceFirstStartedAt
            };
            seed.Add(source);
            List<ReplayStepPlan> planned = [];
            foreach (WorkflowStep step in steps)
            {
                var row = new WorkflowStepResult
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = source.Id,
                    WorkflowStepId = step.Id,
                    Status = StepStatus.Completed,
                    Output = "{\"recorded\":true}",
                    ExecutedAt = DateTime.UtcNow
                };
                seed.Add(row);
                planned.Add(new ReplayStepPlan(step.StepOrder, ReplayStepDisposition.Reuse, row.Id, [], "does not depend on the film"));
            }

            run.WorkflowDefinition.Kind = WorkflowDefinitionKind.CompositionReplay;
            var plan = new CompositionReplayPlan(CompositionReplayPlan.CurrentVersion, source.Id, null, 99, CompositionCutPolicy.KeepCut, planned, []);
            seed.Add(new CompositionRevision
            {
                Id = Guid.NewGuid(),
                ProjectId = s.ProjectId,
                OrganizationId = s.OrganizationId,
                SourceExecutionId = source.Id,
                ExecutionId = run.Id,
                WorkflowDefinitionId = run.WorkflowDefinitionId,
                ReplayPlanJson = plan.Serialize()
            });
            await seed.SaveChangesAsync();
        }

        if (rowDeleted)
            await StampMarkerAndDeleteRowAsync(newDb, s.ProjectId, null);

        List<string> ran = [];
        var failure = new FailProjectFileReads { Armed = failRead };
        DbContextOptions<WorkflowEngineDbContext> failing = FailProjectFileReads.Options(dbName, failure, dbRoot);
        Func<WorkflowEngineDbContext> runDb = failRead
            ? () => new WorkflowEngineDbContext(failing)
            : newDb;
        await ExecutionResumeTests.Service(new VoiceReuseGuardTests.DbScopes(runDb), null, null, null,
            new ExecutionResumeTests.Executor(StepType.VideoAnalyze, c => { ran.Add("analyze"); return Task.FromResult(ExecutionResumeTests.Done(c, "{}")); }),
            new ExecutionResumeTests.Executor(StepType.VideoCompile, c => { ran.Add("compile"); return Task.FromResult(ExecutionResumeTests.Done(c, "{}")); }))
            .ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
        return ran;
    }

    [Fact]
    public async Task Replay_copies_the_source_run_when_nothing_is_quarantined()
        => (await RunReplayAsync(quarantined: false)).Should().BeEmpty();

    [Fact]
    public async Task Replay_copies_a_source_run_that_began_after_the_marker()
    {
        DateTime marker = DateTime.UtcNow.AddHours(-2);
        (await RunReplayAsync(quarantined: false, marker: marker, sourceFirstStartedAt: marker.AddHours(1))).Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_makes_a_source_run_that_began_before_the_marker_again()
    {
        DateTime marker = DateTime.UtcNow.AddHours(-2);
        (await RunReplayAsync(quarantined: false, marker: marker, sourceFirstStartedAt: marker.AddHours(-1))).Should().Equal("analyze", "compile");
    }

    [Fact]
    public async Task Replay_makes_every_step_again_after_a_quarantine()
        => (await RunReplayAsync(quarantined: true)).Should().Equal("analyze", "compile");

    [Fact]
    public async Task Replay_makes_every_step_again_after_the_quarantined_row_is_deleted()
        => (await RunReplayAsync(quarantined: false, rowDeleted: true)).Should().Equal("analyze", "compile");

    [Fact]
    public async Task Replay_makes_every_step_again_when_the_state_cannot_be_read()
        => (await RunReplayAsync(quarantined: false, failRead: true)).Should().Equal("analyze", "compile");

    // ------------------------------------------------------------------ the marker outlives the rows

    [Fact]
    public async Task A_deleted_row_does_not_reopen_an_entry_stored_before_the_quarantine()
    {
        Func<WorkflowEngineDbContext> newDb = NewDb();
        Guid fileId = await AddFileAsync(newDb, BaseInputs.ProjectId);
        using (WorkflowEngineDbContext db = newDb())
            await Cache(db).StoreAsync(BaseInputs, Completed, CancellationToken.None);

        await QuarantineAsync(newDb, fileId);
        await StampMarkerAndDeleteRowAsync(newDb, BaseInputs.ProjectId, fileId);

        using WorkflowEngineDbContext read = newDb();
        (await Cache(read).TryGetAsync(BaseInputs, CancellationToken.None)).Hit.Should().BeFalse();
    }

    [Fact]
    public async Task A_released_row_does_not_reopen_an_entry_stored_before_the_quarantine()
    {
        Func<WorkflowEngineDbContext> newDb = NewDb();
        Guid fileId = await AddFileAsync(newDb, BaseInputs.ProjectId);
        using (WorkflowEngineDbContext db = newDb())
            await Cache(db).StoreAsync(BaseInputs, Completed, CancellationToken.None);

        await QuarantineAsync(newDb, fileId);
        await StampMarkerAndDeleteRowAsync(newDb, BaseInputs.ProjectId, null);
        using (WorkflowEngineDbContext db = newDb())
        {
            (await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == fileId)).CsamStatus = CsamScanStatus.Clean;
            await db.SaveChangesAsync();
        }

        using WorkflowEngineDbContext read = newDb();
        (await Cache(read).TryGetAsync(BaseInputs, CancellationToken.None)).Hit.Should().BeFalse("old entries stay unreachable after a release");
    }

    [Fact]
    public async Task A_failed_state_read_at_store_time_stores_nothing()
    {
        string name = "quarantine-fail-" + Guid.NewGuid().ToString("N");
        var failure = new FailProjectFileReads();
        using WorkflowEngineDbContext db = new(FailProjectFileReads.Options(name, failure));
        StepResultCache cache = Cache(db);
        await cache.CaptureQuarantineBaselineAsync(BaseInputs.ProjectId, CancellationToken.None);

        failure.ArmFor(db);
        await cache.StoreAsync(BaseInputs, Completed, CancellationToken.None);

        failure.Armed = false;
        (await db.WorkflowStepCacheEntries.CountAsync()).Should().Be(0);
    }
}
