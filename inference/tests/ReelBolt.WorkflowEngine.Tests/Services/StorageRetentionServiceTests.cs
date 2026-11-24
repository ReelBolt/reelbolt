using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// F10 slice 2 acceptance: with a seeded org and a fake object store, the retention sweep must mark
/// (and, after the grace period, delete) only the expired generated keys, leave <c>userFiles</c>
/// alone, and clear the references the Inference API owns — while never writing the engine-owned
/// <c>workflow_step_results</c> table whose keys it therefore cannot clear.
/// </summary>
public class StorageRetentionServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>Plan lookup: one retention-days value per organization.</summary>
    private sealed class PlanStub(Dictionary<Guid, int> retentionDays) : IOrganizationEntitlements
    {
        public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            new(new OrganizationEntitlementSet
            {
                RetentionDays = retentionDays.TryGetValue(organizationId, out int days) ? days : 30
            });
    }

    private static (StorageRetentionService Service, IServiceProvider Services, FakeFileStorage Storage) Build(
        Dictionary<Guid, int> retentionDays, bool enabled = true, bool orphanSweep = false, FailProjectFileReads? readFailure = null)
    {
        string dbName = Guid.NewGuid().ToString();
        // Contexts with their own EF provider still share this database through the root.
        var databaseRoot = new Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot();
        var storage = new FakeFileStorage();
        var services = new ServiceCollection();
        services.AddDbContext<InferenceApiDbContext>(o =>
        {
            o.UseInMemoryDatabase(dbName, databaseRoot)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            if (readFailure is not null)
            {
                o.EnableServiceProviderCaching(false)
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                    .AddInterceptors(readFailure);
            }
        });
        services.AddSingleton<IFileStorageService>(storage);
        if (orphanSweep)
            services.AddSingleton<IObjectListing>(storage);
        // The production tenant accessor and its AsyncLocal scopes, so the per-organization scoping is
        // exercised rather than stubbed away.
        services.AddSingleton<ITenantContext>(new TenantContext(new HttpContextAccessor()));
        services.AddSingleton<IOrganizationEntitlements>(new PlanStub(retentionDays));
        IServiceProvider sp = services.BuildServiceProvider();
        IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:StorageRetention:Enabled"] = enabled ? "true" : "false"
        }).Build();
        var service = new StorageRetentionService(
            sp.GetRequiredService<IServiceScopeFactory>(), cfg, NullLogger<StorageRetentionService>.Instance);
        return (service, sp, storage);
    }

    [Fact]
    public async Task A_quarantined_generated_file_is_never_marked_or_purged()
    {
        Guid organization = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        string key = $"projects/{project}/outputFiles/old.mp4";
        var (service, sp, _) = Build(new Dictionary<Guid, int> { [organization] = 30 });
        ProjectFile row = ProjectFileRow(project, key, "outputFiles", Now.AddDays(-40));
        row.CsamStatus = CsamScanStatus.Quarantined;
        row.CsamQuarantineKey = key;
        await SeedProjectAsync(sp, organization, project, new[] { row });

        StorageRetentionRunResult run = await service.RunOnceAsync(Now, CancellationToken.None);

        run.MarkedObjects.Should().Be(0, "a quarantined object is evidence and must never be marked");
        (await MarksAsync(sp)).Should().BeEmpty();
        (await ProjectFileKeysAsync(sp)).Should().Contain(key);
    }

    [Fact]
    public async Task A_project_with_an_output_marker_is_skipped_while_others_in_the_same_run_are_processed()
    {
        Guid organization = Guid.NewGuid();
        Guid kept = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        string keptKey = $"projects/{kept}/outputFiles/old.mp4";
        string otherKey = $"projects/{other}/outputFiles/old.mp4";
        var (service, sp, _) = Build(new Dictionary<Guid, int> { [organization] = 30 });
        await SeedProjectAsync(sp, organization, kept, new[] { ProjectFileRow(kept, keptKey, "outputFiles", Now.AddDays(-40)) });
        await SeedProjectAsync(sp, organization, other, new[] { ProjectFileRow(other, otherKey, "outputFiles", Now.AddDays(-40)) });
        using (IServiceScope scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            (await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == kept)).CsamOutputHoldAt = Now.AddDays(-1);
            // A mark that is already due must not be executed for the kept project either.
            db.StorageRetentionMarks.Add(new StorageRetentionMark
            {
                Id = Guid.NewGuid(),
                OrganizationId = organization,
                ProjectId = kept,
                StorageKey = $"projects/{kept}/outputFiles/due.mp4",
                Category = "outputFiles",
                MarkedAt = Now.AddDays(-30)
            });
            await db.SaveChangesAsync();
        }

        StorageRetentionRunResult run = await service.RunOnceAsync(Now, CancellationToken.None);

        run.MarkedObjects.Should().Be(1, "only the other project's file is marked");
        List<StorageRetentionMark> marks = await MarksAsync(sp);
        marks.Should().Contain(m => m.StorageKey == otherKey);
        marks.Should().NotContain(m => m.StorageKey == keptKey);
        marks.Single(m => m.ProjectId == kept).PurgedAt.Should().BeNull("a due mark of a kept project is not executed");
        (await ProjectFileKeysAsync(sp)).Should().Contain(keptKey);
    }

    [Fact]
    public async Task An_unreadable_marker_keeps_every_project_untouched()
    {
        Guid organization = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        string key = $"projects/{project}/outputFiles/old.mp4";
        var failure = new FailProjectFileReads();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [organization] = 30 }, readFailure: failure);
        await SeedProjectAsync(sp, organization, project, new[] { ProjectFileRow(project, key, "outputFiles", Now.AddDays(-40)) });
        using (IServiceScope scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.StorageRetentionMarks.Add(new StorageRetentionMark
            {
                Id = Guid.NewGuid(),
                OrganizationId = organization,
                ProjectId = project,
                StorageKey = $"projects/{project}/outputFiles/due.mp4",
                Category = "outputFiles",
                MarkedAt = Now.AddDays(-30)
            });
            await db.SaveChangesAsync();
        }
        failure.Armed = true;

        StorageRetentionRunResult run = await service.RunOnceAsync(Now, CancellationToken.None);

        failure.Armed = false;

        run.MarkedObjects.Should().Be(0);
        run.PurgedObjects.Should().Be(0);
        (await MarksAsync(sp)).Should().ContainSingle().Which.PurgedAt.Should().BeNull();
        (await ProjectFileKeysAsync(sp)).Should().Contain(key);
    }

    private static ProjectFile ProjectFileRow(Guid projectId, string storageKey, string category, DateTime uploadedAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            StorageKey = storageKey,
            Category = category,
            OriginalFileName = storageKey[(storageKey.LastIndexOf('/') + 1)..],
            StorageBucket = "fake-bucket",
            MimeType = "video/mp4",
            SizeBytes = 1024,
            UploadedAt = uploadedAt
        };

    /// <summary>Seeds one organization's project, its project-file rows, and one completed execution.</summary>
    private static async Task SeedProjectAsync(
        IServiceProvider sp,
        Guid organizationId,
        Guid projectId,
        IEnumerable<ProjectFile> files,
        string? artifactStorageKey = null,
        DateTime? artifactExecutedAt = null,
        string? outputStorageKey = null,
        string? timelineStorageKey = null)
    {
        using IServiceScope scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        var workflowId = Guid.NewGuid();
        db.Projects.Add(new Project { Id = projectId, Name = $"project-{projectId:N}", OrganizationId = organizationId });
        db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = workflowId, ProjectId = projectId, Name = "wf" });
        db.ProjectFiles.AddRange(files);

        if (artifactStorageKey is not null)
        {
            var executionId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            db.WorkflowSteps.Add(new WorkflowStep { Id = stepId, WorkflowDefinitionId = workflowId, StepOrder = 1, Label = "VideoAnalyze" });
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = executionId,
                WorkflowDefinitionId = workflowId,
                ProjectId = projectId,
                OrganizationId = organizationId,
                Status = ExecutionStatus.Passed,
                StartedAt = artifactExecutedAt,
                CompletedAt = artifactExecutedAt
            });
            // A bare artifact: no project-file row, reachable only through the step result's key.
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = stepId,
                Status = StepStatus.Completed,
                ExecutedAt = artifactExecutedAt!.Value,
                OutputStorageKey = outputStorageKey,
                ArtifactStorageKey = artifactStorageKey,
                TimelineStorageKey = timelineStorageKey
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<List<StorageRetentionMark>> MarksAsync(IServiceProvider sp)
    {
        using IServiceScope scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>()
            .StorageRetentionMarks.IgnoreQueryFilters().ToListAsync();
    }

    private static async Task<List<string>> ProjectFileKeysAsync(IServiceProvider sp)
    {
        using IServiceScope scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>()
            .ProjectFiles.IgnoreQueryFilters().Select(f => f.StorageKey).ToListAsync();
    }

    private static async Task<List<WorkflowStepResult>> StepResultsAsync(IServiceProvider sp)
    {
        using IServiceScope scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>()
            .WorkflowStepResults.IgnoreQueryFilters().ToListAsync();
    }

    [Fact]
    public async Task Marks_expired_generated_storage_and_purges_it_only_after_the_grace_period()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 });

        string oldRender = $"projects/{project}/outputFiles/e1/old-render.mp4";
        string newRender = $"projects/{project}/outputFiles/e1/new-render.mp4";
        string userUpload = $"projects/{project}/userFiles/upload.png";
        string analysis = $"projects/{project}/agentFiles/video-analysis/e1/step-1-analysis.json";
        string timeline = $"projects/{project}/agentFiles/video-analysis/e1/step-1-timeline.json";
        foreach (string key in new[] { oldRender, newRender, userUpload, analysis, timeline })
            storage.Objects[key] = new byte[] { 1 };

        // The expired render is a saved take: a project_files row AND the step result's OutputStorageKey.
        // The analysis document and the timeline are bare artifacts with only a step-result key.
        await SeedProjectAsync(
            sp, org, project,
            new[]
            {
                ProjectFileRow(project, oldRender, "outputFiles", Now.AddDays(-40)),
                ProjectFileRow(project, newRender, "outputFiles", Now.AddDays(-2)),
                ProjectFileRow(project, userUpload, "userFiles", Now.AddDays(-400)),
            },
            artifactStorageKey: analysis,
            artifactExecutedAt: Now.AddDays(-40),
            outputStorageKey: oldRender,
            timelineStorageKey: timeline);

        // --- Pass 1: mark. The grace period has not elapsed, so nothing leaves the object store. ---
        StorageRetentionRunResult first = await service.RunOnceAsync(Now, CancellationToken.None);

        first.MarkedObjects.Should().Be(3, "the expired render and the two expired step artifacts");
        first.PurgedObjects.Should().Be(0);
        first.ClearedReferences.Should().Be(0);
        storage.DeletedKeys.Should().BeEmpty("every marked object is inside the grace period");
        storage.Objects.Should().ContainKeys(oldRender, newRender, userUpload, analysis, timeline);
        storage.DeleteBatchSizes.Should().BeEmpty();

        // (c) the reference the Inference API owns — the project_files row — is cleared at mark time,
        // and the object it named is remembered by a durable mark instead.
        (await ProjectFileKeysAsync(sp)).Should().BeEquivalentTo(new[] { newRender, userUpload });
        List<StorageRetentionMark> marked = await MarksAsync(sp);
        marked.Select(m => m.StorageKey).Should().BeEquivalentTo(new[] { oldRender, analysis, timeline });
        marked.Should().OnlyContain(m => m.PurgedAt == null && m.OrganizationId == org && m.ProjectId == project);

        // Nothing is deleted yet, so the step result's keys are left alone.
        WorkflowStepResult beforePurge = (await StepResultsAsync(sp)).Single();
        beforePurge.OutputStorageKey.Should().Be(oldRender);
        beforePurge.ArtifactStorageKey.Should().Be(analysis);
        beforePurge.TimelineStorageKey.Should().Be(timeline);

        // --- Pass 2, eight days later: the grace period has elapsed, so the marks are purged. ---
        StorageRetentionRunResult second = await service.RunOnceAsync(Now.AddDays(8), CancellationToken.None);

        second.MarkedObjects.Should().Be(0, "what survives is inside retention and the artifacts are already marked");
        second.PurgedObjects.Should().Be(3);
        second.ClearedReferences.Should().Be(3, "all three keys named a deleted object");

        // (a) exactly the expired generated keys went, and (b) userFiles survived.
        storage.DeletedKeys.Should().BeEquivalentTo(new[] { oldRender, analysis, timeline });
        storage.Objects.Should().ContainKeys(userUpload, newRender);
        storage.Objects.Should().NotContainKey(oldRender);

        // The purge cleared every workflow_step_results key naming a deleted object, so no row is left
        // pointing at one (OutputsController lists takes on OutputStorageKey != null).
        List<WorkflowStepResult> after = await StepResultsAsync(sp);
        after.Should().HaveCount(1);
        after[0].OutputStorageKey.Should().BeNull();
        after[0].ArtifactStorageKey.Should().BeNull();
        after[0].TimelineStorageKey.Should().BeNull();
        after.Should().OnlyContain(r => r.OutputStorageKey == null, "no row may name a deleted render");

        // (c) the project_files rows the API owns are unchanged by the purge: the fresh render and the
        // user's upload are untouched, and the expired render's row went at mark time.
        (await ProjectFileKeysAsync(sp)).Should().BeEquivalentTo(new[] { newRender, userUpload });

        // With its references cleared, the purged mark has done its job and self-cleans in the same run.
        (await MarksAsync(sp)).Should().BeEmpty();

        // A third run is a no-op: nothing is re-marked and nothing is deleted twice.
        StorageRetentionRunResult third = await service.RunOnceAsync(Now.AddDays(16), CancellationToken.None);
        third.Should().Be(new StorageRetentionRunResult(0, 0, 0));
        storage.DeletedKeys.Should().HaveCount(3);
    }

    [Fact]
    public async Task Clears_every_step_result_sharing_a_deleted_key()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 });

        // A cache hit reuses an earlier run's object, so two step results can name the SAME key.
        // Both must be cleared, or one of them keeps listing a take whose object is gone.
        string shared = $"projects/{project}/outputFiles/e1/shared-render.mp4";
        storage.Objects[shared] = new byte[] { 1 };

        using (IServiceScope scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            var workflowId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            db.Projects.Add(new Project { Id = project, Name = "shared", OrganizationId = org });
            db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = workflowId, ProjectId = project, Name = "wf" });
            db.WorkflowSteps.Add(new WorkflowStep { Id = stepId, WorkflowDefinitionId = workflowId, StepOrder = 1 });
            db.ProjectFiles.Add(ProjectFileRow(project, shared, "outputFiles", Now.AddDays(-40)));
            for (int i = 0; i < 2; i++)
            {
                var executionId = Guid.NewGuid();
                db.WorkflowExecutions.Add(new WorkflowExecution
                {
                    Id = executionId,
                    WorkflowDefinitionId = workflowId,
                    ProjectId = project,
                    OrganizationId = org,
                    Status = ExecutionStatus.Passed,
                    StartedAt = Now.AddDays(-40),
                    CompletedAt = Now.AddDays(-40)
                });
                db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = executionId,
                    WorkflowStepId = stepId,
                    Status = StepStatus.Completed,
                    ExecutedAt = Now.AddDays(-40),
                    OutputStorageKey = shared,
                    FromCache = i == 1
                });
            }

            await db.SaveChangesAsync();
        }

        (await service.RunOnceAsync(Now, CancellationToken.None)).MarkedObjects.Should().Be(1);
        StorageRetentionRunResult purge = await service.RunOnceAsync(Now.AddDays(8), CancellationToken.None);

        purge.PurgedObjects.Should().Be(1);
        purge.ClearedReferences.Should().Be(2, "both step results named the same deleted object");
        (await StepResultsAsync(sp)).Should().OnlyContain(r => r.OutputStorageKey == null);
        (await MarksAsync(sp)).Should().BeEmpty();
    }

    [Fact]
    public async Task Per_plan_retention_decides_what_expires_per_organization()
    {
        Guid shortPlan = Guid.NewGuid(), longPlan = Guid.NewGuid(), unlimitedPlan = Guid.NewGuid();
        Guid shortProject = Guid.NewGuid(), longProject = Guid.NewGuid(), unlimitedProject = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int>
        {
            [shortPlan] = 30,
            [longPlan] = 90,
            [unlimitedPlan] = OrganizationEntitlementSet.UnlimitedInt
        });

        string shortKey = $"projects/{shortProject}/outputFiles/e1/render.mp4";
        string longKey = $"projects/{longProject}/outputFiles/e1/render.mp4";
        string unlimitedKey = $"projects/{unlimitedProject}/outputFiles/e1/render.mp4";
        foreach (string key in new[] { shortKey, longKey, unlimitedKey })
            storage.Objects[key] = new byte[] { 1 };

        // All three renders are 60 days old: past Trial/Creator (30), inside Pro/Team (90), and
        // "keep forever" for Enterprise (int.MaxValue).
        DateTime sixtyDaysAgo = Now.AddDays(-60);
        await SeedProjectAsync(sp, shortPlan, shortProject, new[] { ProjectFileRow(shortProject, shortKey, "outputFiles", sixtyDaysAgo) });
        await SeedProjectAsync(sp, longPlan, longProject, new[] { ProjectFileRow(longProject, longKey, "outputFiles", sixtyDaysAgo) });
        await SeedProjectAsync(sp, unlimitedPlan, unlimitedProject, new[] { ProjectFileRow(unlimitedProject, unlimitedKey, "outputFiles", sixtyDaysAgo) });

        (await service.RunOnceAsync(Now, CancellationToken.None)).MarkedObjects.Should().Be(1);
        (await MarksAsync(sp)).Select(m => m.StorageKey).Should().BeEquivalentTo(new[] { shortKey });

        (await service.RunOnceAsync(Now.AddDays(8), CancellationToken.None)).PurgedObjects.Should().Be(1);
        storage.DeletedKeys.Should().BeEquivalentTo(new[] { shortKey });
        storage.Objects.Should().ContainKeys(longKey, unlimitedKey);
        (await ProjectFileKeysAsync(sp)).Should().BeEquivalentTo(new[] { longKey, unlimitedKey });
    }

    [Fact]
    public async Task Purges_in_batches_of_one_thousand()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 });

        const int objectCount = 1500;
        var rows = new List<ProjectFile>();
        for (int i = 0; i < objectCount; i++)
        {
            string key = $"projects/{project}/outputFiles/e1/clip-{i:D5}.mp4";
            storage.Objects[key] = new byte[] { 1 };
            rows.Add(ProjectFileRow(project, key, "outputFiles", Now.AddDays(-40)));
        }

        await SeedProjectAsync(sp, org, project, rows);

        (await service.RunOnceAsync(Now, CancellationToken.None)).MarkedObjects.Should().Be(objectCount);
        StorageRetentionRunResult purge = await service.RunOnceAsync(Now.AddDays(8), CancellationToken.None);

        purge.PurgedObjects.Should().Be(objectCount);
        storage.DeleteBatchSizes.Should().Equal(StorageRetentionService.BatchSize, objectCount - StorageRetentionService.BatchSize);
        storage.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task A_batch_the_object_store_did_not_fully_delete_keeps_its_marks_and_is_retried()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 });

        string key = $"projects/{project}/outputFiles/e1/render.mp4";
        storage.Objects[key] = new byte[] { 1 };
        await SeedProjectAsync(sp, org, project, new[] { ProjectFileRow(project, key, "outputFiles", Now.AddDays(-40)) });

        (await service.RunOnceAsync(Now, CancellationToken.None)).MarkedObjects.Should().Be(1);

        // The object is already gone when the purge runs: the delete reports one fewer than it asked
        // for, so the mark stays un-stamped and the next run retries it (S3 deletes are idempotent).
        storage.Objects.Clear();
        (await service.RunOnceAsync(Now.AddDays(8), CancellationToken.None)).PurgedObjects.Should().Be(0);
        (await MarksAsync(sp)).Should().OnlyContain(m => m.PurgedAt == null);

        storage.Objects[key] = new byte[] { 1 };
        (await service.RunOnceAsync(Now.AddDays(9), CancellationToken.None)).PurgedObjects.Should().Be(1);
        (await MarksAsync(sp)).Should().OnlyContain(m => m.PurgedAt != null);
    }

    /// <summary>
    /// SQCR-S-09: an object in <c>userFiles</c> that no row and no mark names — an orphan from an
    /// import whose cleanup delete failed — is found by the orphan pass and purged after the (short)
    /// delete grace, so nothing is left uncounted forever.
    /// </summary>
    [Fact]
    public async Task An_orphaned_user_file_with_no_row_and_no_mark_is_marked_then_purged()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 }, orphanSweep: true);

        string orphan = $"projects/{project}/userFiles/imported.mp4";
        storage.Objects[orphan] = new byte[] { 1 };
        storage.ObjectLastModified[orphan] = Now.AddDays(-2);   // older than the 24h gate
        await SeedProjectAsync(sp, org, project, []);           // a project row, no file rows

        StorageRetentionRunResult first = await service.RunOnceAsync(Now, CancellationToken.None);

        first.OrphanedObjects.Should().Be(1);
        StorageRetentionMark mark = (await MarksAsync(sp)).Should().ContainSingle().Subject;
        mark.StorageKey.Should().Be(orphan);
        mark.Category.Should().Be("userFiles");
        mark.PurgedAt.Should().BeNull("the mark is inside the delete grace");
        storage.DeletedKeys.Should().BeEmpty();

        // Two hours later the delete-written mark is past DeleteGraceMinutes (60) and the purge takes it.
        StorageRetentionRunResult second = await service.RunOnceAsync(Now.AddHours(2), CancellationToken.None);

        second.PurgedObjects.Should().Be(1);
        storage.Objects.Should().NotContainKey(orphan);
        (await MarksAsync(sp)).Should().NotContain(m => m.PurgedAt == null, "nothing is left waiting for the purge");
    }

    /// <summary>
    /// The engine's analysis pass cache (frame grids hold small pictures of a video) has no rows and its entry keys
    /// do not say which file they belong to, so the sweep removes entries by age alone: an entry of a file that
    /// was deleted long ago does not stay until the account is purged.
    /// </summary>
    [Fact]
    public async Task Analysis_cache_entries_past_their_age_are_deleted_and_younger_ones_and_other_agent_files_are_kept()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 }, orphanSweep: true);

        string old = $"projects/{project}/agentFiles/analysis-cache/{new string('a', 64)}.bin";
        string young = $"projects/{project}/agentFiles/analysis-cache/{new string('b', 64)}.bin";
        string mask = $"projects/{project}/agentFiles/segmentation/{new string('c', 64)}.mkv";
        foreach (string key in new[] { old, young, mask })
            storage.Objects[key] = new byte[] { 1 };
        storage.ObjectLastModified[old] = Now.AddDays(-(StorageRetentionService.DefaultAnalysisCacheMaxAgeDays + 1));
        storage.ObjectLastModified[young] = Now.AddDays(-(StorageRetentionService.DefaultAnalysisCacheMaxAgeDays - 1));
        storage.ObjectLastModified[mask] = Now.AddDays(-400);
        await SeedProjectAsync(sp, org, project, []);

        StorageRetentionRunResult run = await service.RunOnceAsync(Now, CancellationToken.None);

        run.PurgedObjects.Should().Be(1);
        storage.Objects.Keys.Should().BeEquivalentTo(new[] { young, mask });
        (await MarksAsync(sp)).Should().BeEmpty("a cache entry is deleted directly: nothing is lost with it, so no mark and no grace");

        // The same key written again later (the engine made the entry again) ages out again.
        storage.Objects[old] = new byte[] { 2 };
        storage.ObjectLastModified[old] = Now.AddDays(1);
        (await service.RunOnceAsync(Now.AddDays(2), CancellationToken.None)).PurgedObjects.Should().Be(1, "the other entry is past its age now");
        storage.Objects.Keys.Should().BeEquivalentTo(new[] { old, mask });
        (await service.RunOnceAsync(Now.AddDays(40), CancellationToken.None)).PurgedObjects.Should().Be(1);
        storage.Objects.Keys.Should().BeEquivalentTo(new[] { mask });
    }

    [Fact]
    public async Task Analysis_cache_entries_of_a_project_whose_outputs_are_held_are_left_alone()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 }, orphanSweep: true);
        string entry = $"projects/{project}/agentFiles/analysis-cache/{new string('a', 64)}.bin";
        storage.Objects[entry] = new byte[] { 1 };
        storage.ObjectLastModified[entry] = Now.AddDays(-90);
        await SeedProjectAsync(sp, org, project, []);
        using (IServiceScope scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            (await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == project)).CsamOutputHoldAt = Now.AddDays(-1);
            await db.SaveChangesAsync();
        }

        (await service.RunOnceAsync(Now, CancellationToken.None)).PurgedObjects.Should().Be(0);

        storage.Objects.Should().ContainKey(entry);
    }

    [Fact]
    public void The_sweep_and_the_engine_name_the_same_analysis_cache_prefix()
    {
        (StorageRetentionService.AnalysisCacheCategory + "/")
            .Should().Be(ReelBolt.WorkflowEngine.Services.Video.Caching.SourcePassCache.KeyPrefix);
    }

    /// <summary>
    /// The age sweep deletes with no mark and no grace, so it must only ever touch what the cache wrote. A project
    /// file can be given a path under the same folder (an uploaded repository with an <c>analysis-cache/</c>
    /// directory that an agent edits): such an object is not a cache entry, whatever its age.
    /// </summary>
    [Fact]
    public async Task The_analysis_cache_sweep_deletes_only_keys_shaped_like_an_entry_that_no_file_row_names()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        // Unlimited plan retention: nothing but the cache sweep could remove an old agent file here.
        var (service, sp, storage) = Build(
            new Dictionary<Guid, int> { [org] = OrganizationEntitlementSet.UnlimitedInt }, orphanSweep: true);

        string folder = $"projects/{project}/agentFiles/analysis-cache";
        string entry = $"{folder}/{new string('a', 64)}.bin";
        string agentFile = $"{folder}/notes.md";
        string nested = $"{folder}/src/{new string('b', 64)}.bin";
        string upperCase = $"{folder}/{new string('C', 64)}.bin";
        string shortName = $"{folder}/{new string('d', 63)}.bin";
        string otherExtension = $"{folder}/{new string('e', 64)}.json";
        string namedByARow = $"{folder}/{new string('f', 64)}.bin";
        string[] all = [entry, agentFile, nested, upperCase, shortName, otherExtension, namedByARow];
        foreach (string key in all)
        {
            storage.Objects[key] = new byte[] { 1 };
            storage.ObjectLastModified[key] = Now.AddDays(-400);
        }

        await SeedProjectAsync(sp, org, project,
        [
            ProjectFileRow(project, agentFile, "agentFiles", Now.AddDays(-400)),
            ProjectFileRow(project, namedByARow, "agentFiles", Now.AddDays(-400))
        ]);

        StorageRetentionRunResult run = await service.RunOnceAsync(Now, CancellationToken.None);

        run.PurgedObjects.Should().Be(1);
        storage.Objects.Keys.Should().BeEquivalentTo(all.Where(k => k != entry));
        (await ProjectFileKeysAsync(sp)).Should().BeEquivalentTo(new[] { agentFile, namedByARow });
        (await MarksAsync(sp)).Should().BeEmpty();
    }

    [Fact]
    public void An_entry_key_the_engine_makes_has_the_shape_the_sweep_deletes()
    {
        Guid project = Guid.NewGuid();
        string made = ReelBolt.WorkflowEngine.Services.Video.Caching.SourcePassCache.EntryKey(
            new ReelBolt.WorkflowEngine.Services.Video.Caching.SourcePassIdentity(project, $"projects/{project}/userFiles/a.mp4", "v1"),
            "grid", "1", "fps=2");

        StorageRetentionService.IsAnalysisCacheEntry(project, made).Should().BeTrue();
        StorageRetentionService.IsAnalysisCacheEntry(Guid.NewGuid(), made).Should().BeFalse("another project's key");
        StorageRetentionService.IsAnalysisCacheEntry(project, made + "x").Should().BeFalse();
        StorageRetentionService.IsAnalysisCacheEntry(project, made.Replace("analysis-cache", "analysis-cache/sub", StringComparison.Ordinal)).Should().BeFalse();
        StorageRetentionService.IsAnalysisCacheEntry(project, null).Should().BeFalse();
        StorageRetentionService.IsAnalysisCacheEntry(project, "").Should().BeFalse();
    }

    /// <summary>
    /// The age sweep only lists projects that still exist. A frame grid entry holds small pictures of a video, so
    /// a project delete marks its entries itself, and the purge then removes them.
    /// </summary>
    [Fact]
    public async Task Deleting_a_project_marks_its_analysis_cache_entries_and_the_purge_then_removes_them()
    {
        Guid org = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 }, orphanSweep: true);

        string folder = $"projects/{projectId}/agentFiles/analysis-cache";
        string first = $"{folder}/{new string('a', 64)}.bin";
        string second = $"{folder}/{new string('b', 64)}.bin";
        string notAnEntry = $"{folder}/notes.md";
        foreach (string key in new[] { first, second, notAnEntry })
        {
            storage.Objects[key] = new byte[] { 1 };
            storage.ObjectLastModified[key] = Now.AddHours(-1);
        }

        await SeedProjectAsync(sp, org, projectId, []);

        using (IServiceScope scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            Project project = await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == projectId);

            // No listing, or one that fails: nothing is marked and nothing throws.
            await StorageRetentionService.MarkAnalysisCacheForProjectDeleteAsync(db, project, null, CancellationToken.None);
            await StorageRetentionService.MarkAnalysisCacheForProjectDeleteAsync(db, project, new ThrowingListing(), CancellationToken.None);
            db.ChangeTracker.Entries<StorageRetentionMark>().Should().BeEmpty();

            // As the delete route does: the marks and the removal of the project in one save.
            await StorageRetentionService.MarkAnalysisCacheForProjectDeleteAsync(db, project, storage, CancellationToken.None);
            db.WorkflowDefinitions.RemoveRange(db.WorkflowDefinitions.IgnoreQueryFilters().Where(w => w.ProjectId == projectId));
            db.Projects.Remove(project);
            await db.SaveChangesAsync();
        }

        List<StorageRetentionMark> marks = await MarksAsync(sp);
        marks.Select(m => m.StorageKey).Should().BeEquivalentTo(new[] { first, second });
        marks.Should().OnlyContain(m => m.ProjectId == projectId && m.OrganizationId == org && m.Category == "agentFiles");

        // The project is gone, so the age sweep never lists it again. The marks are what removes the entries.
        (await service.RunOnceAsync(Now, CancellationToken.None)).PurgedObjects.Should().Be(0, "a mark waits out its grace first");
        StorageRetentionRunResult later = await service.RunOnceAsync(
            DateTime.UtcNow.AddDays(StorageRetentionService.DefaultGraceDays + 1), CancellationToken.None);

        later.PurgedObjects.Should().Be(2);
        storage.Objects.Keys.Should().BeEquivalentTo(new[] { notAnEntry });
    }

    /// <summary>
    /// The orphan pass leaves a live file alone (a row names it) and a just-written object alone (its
    /// age is inside the gate), so an upload that is still being recorded cannot be swept.
    /// </summary>
    [Fact]
    public async Task The_orphan_pass_leaves_referenced_and_recent_objects_alone()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 }, orphanSweep: true);

        string referenced = $"projects/{project}/userFiles/kept.mp4";
        string recent = $"projects/{project}/userFiles/just-uploaded.mp4";
        storage.Objects[referenced] = new byte[] { 1 };
        storage.Objects[recent] = new byte[] { 1 };
        storage.ObjectLastModified[referenced] = Now.AddDays(-2);
        storage.ObjectLastModified[recent] = Now.AddMinutes(-5);
        await SeedProjectAsync(sp, org, project, new[] { ProjectFileRow(project, referenced, "userFiles", Now.AddDays(-2)) });

        StorageRetentionRunResult run = await service.RunOnceAsync(Now, CancellationToken.None);

        run.OrphanedObjects.Should().Be(0);
        (await MarksAsync(sp)).Should().BeEmpty();
        storage.DeletedKeys.Should().BeEmpty();
    }

    /// <summary>
    /// QA-SQC-6: a mark a delete wrote is due after the short delete grace, while a mark the sweep
    /// itself wrote (a retention mark) keeps the full grace — so the recoverability window a mis-set
    /// plan needs is preserved, and an explicit delete is not made to wait a week.
    /// </summary>
    [Fact]
    public async Task A_delete_written_mark_is_purged_after_the_short_delete_grace_not_the_full_one()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 });

        string deleted = $"projects/{project}/userFiles/deleted.png";
        string retained = $"projects/{project}/agentFiles/video-analysis/e1/step-1-analysis.json";
        storage.Objects[deleted] = new byte[] { 1 };
        storage.Objects[retained] = new byte[] { 1 };
        await SeedProjectAsync(sp, org, project, []);
        using (IServiceScope scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.StorageRetentionMarks.AddRange(
                new StorageRetentionMark
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = org,
                    ProjectId = project,
                    StorageKey = deleted,
                    Category = "userFiles",
                    MarkedAt = Now.AddHours(-2)
                },
                new StorageRetentionMark
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = org,
                    ProjectId = project,
                    StorageKey = retained,
                    Category = "agentFiles",
                    MarkedAt = Now.AddHours(-2)
                });
            await db.SaveChangesAsync();
        }

        StorageRetentionRunResult run = await service.RunOnceAsync(Now, CancellationToken.None);

        run.PurgedObjects.Should().Be(1);
        storage.Objects.Should().NotContainKey(deleted, "a delete mark is due after DeleteGraceMinutes");
        storage.Objects.Should().ContainKey(retained, "a retention mark keeps the full grace");
        List<StorageRetentionMark> marks = await MarksAsync(sp);
        marks.Should().NotContain(m => m.StorageKey == deleted, "a purged mark nothing references is garbage-collected");
        marks.Single(m => m.StorageKey == retained).PurgedAt.Should().BeNull();
    }

    /// <summary>
    /// SQCR-S-17: the orphan pass also scans <c>agentFiles/render-events/</c> — an orphaned sidecar is
    /// marked then purged, while a sidecar whose mirrored render is still named by a step result is left
    /// alone (a live render must never lose its event anchors).
    /// </summary>
    [Fact]
    public async Task The_orphan_pass_sweeps_an_orphaned_sidecar_and_spares_a_live_renders_events()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 }, orphanSweep: true);

        string liveRender = $"projects/{project}/outputFiles/e1/final-cut.mp4";
        string liveSidecar = $"projects/{project}/agentFiles/render-events/e1/final-cut.mp4.events.json";
        string orphanSidecar = $"projects/{project}/agentFiles/render-events/e1/gone.mp4.events.json";
        foreach (string key in new[] { liveSidecar, orphanSidecar })
        {
            storage.Objects[key] = new byte[] { 1 };
            storage.ObjectLastModified[key] = Now.AddDays(-2);
        }

        // The step result names the live render, so its sidecar is referenced; nothing names the other.
        await SeedProjectAsync(
            sp, org, project, [],
            artifactStorageKey: $"projects/{project}/agentFiles/video-analysis/e1/step-1.json",
            artifactExecutedAt: Now.AddDays(-2),
            outputStorageKey: liveRender);

        StorageRetentionRunResult first = await service.RunOnceAsync(Now, CancellationToken.None);

        first.OrphanedObjects.Should().Be(1);
        List<StorageRetentionMark> marks = await MarksAsync(sp);
        marks.Should().ContainSingle().Which.StorageKey.Should().Be(orphanSidecar);
        marks.Should().NotContain(m => m.StorageKey == liveSidecar, "a live render's events are not an orphan");

        StorageRetentionRunResult second = await service.RunOnceAsync(Now.AddDays(8), CancellationToken.None);

        second.PurgedObjects.Should().Be(1);
        storage.Objects.Should().NotContainKey(orphanSidecar);
        storage.Objects.Should().ContainKey(liveSidecar, "the live render's events survive");
    }

    /// <summary>
    /// Avatars phase 3: the orphan pass also scans <c>agentFiles/avatars/</c>. A still whose avatar is gone (one uploaded
    /// while its avatar was being deleted, or one the delete could not list) is marked and purged; a live avatar's still,
    /// and a key that is not a still at all, are left alone.
    /// </summary>
    [Fact]
    public async Task The_orphan_pass_sweeps_the_still_of_a_deleted_avatar_and_spares_a_live_avatars()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 }, orphanSweep: true);
        await SeedProjectAsync(sp, org, project, []);
        Guid liveAvatar;
        using (IServiceScope scope = sp.CreateScope())
        {
            liveAvatar = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarSeed.Attach(
                scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>(), project, "live").Id;
        }

        string liveStill = AvatarStillStorage.ObjectPrefix(project, liveAvatar) + "a.img";
        string orphanStill = AvatarStillStorage.ObjectPrefix(project, Guid.NewGuid()) + "b.img";
        string notAStill = $"projects/{project}/agentFiles/avatars/readme.txt";
        foreach (string key in new[] { liveStill, orphanStill, notAStill })
        {
            storage.Objects[key] = new byte[] { 1 };
            storage.ObjectLastModified[key] = Now.AddDays(-2);
        }

        StorageRetentionRunResult first = await service.RunOnceAsync(Now, CancellationToken.None);

        first.OrphanedObjects.Should().Be(1);
        StorageRetentionMark mark = (await MarksAsync(sp)).Should().ContainSingle().Subject;
        mark.StorageKey.Should().Be(orphanStill);
        mark.Category.Should().Be("agentFiles");

        await service.RunOnceAsync(Now.AddDays(8), CancellationToken.None);
        storage.Objects.Should().NotContainKey(orphanStill);
        storage.Objects.Should().ContainKey(liveStill, "a live avatar's still is not an orphan");
        storage.Objects.Should().ContainKey(notAStill, "a key that is not a still is never swept by this pass");
    }

    /// <summary>
    /// The strips a describing analysis keeps for <c>LookAtMoment</c> are frames of the customer's video that no row
    /// names. The orphan pass marks a strip once its analysis document is no longer named by a step result, or has
    /// itself been marked as expired; a strip of a live document and every other object of the folder are left alone.
    /// </summary>
    [Fact]
    public async Task The_orphan_pass_sweeps_event_strips_with_their_analysis_document_and_nothing_else_in_the_folder()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 }, orphanSweep: true);

        string folder = $"projects/{project}/agentFiles/video-analysis";
        string liveDocument = $"{folder}/run-1/step-1-analysis.json";
        string liveStrip = VideoEventStrips.KeyFor(liveDocument, "e3")!;
        string orphanStrip = VideoEventStrips.KeyFor($"{folder}/run-gone/step-1-analysis.json", "e0")!;
        string keyframe = $"{folder}/run-1/step-1-keyframes/s0.jpg";
        string lookalike = $"{folder}/run-gone/step-1-event-strips/notes.jpg";
        foreach (string key in new[] { liveDocument, liveStrip, orphanStrip, keyframe, lookalike })
        {
            storage.Objects[key] = new byte[] { 1 };
            storage.ObjectLastModified[key] = Now.AddDays(-2);
        }

        await SeedProjectAsync(sp, org, project, [], artifactStorageKey: liveDocument, artifactExecutedAt: Now.AddDays(-2));

        StorageRetentionRunResult first = await service.RunOnceAsync(Now, CancellationToken.None);

        first.OrphanedObjects.Should().Be(1);
        StorageRetentionMark mark = (await MarksAsync(sp)).Should().ContainSingle().Subject;
        mark.StorageKey.Should().Be(orphanStrip, "no step result names its analysis document any more");
        mark.Category.Should().Be("agentFiles");

        await service.RunOnceAsync(Now.AddDays(8), CancellationToken.None);
        storage.Objects.Should().NotContainKey(orphanStrip);
        storage.Objects.Should().ContainKey(liveStrip, "its analysis document is still in use");
        storage.Objects.Should().ContainKey(keyframe, "this pass owns the strips only");
        storage.Objects.Should().ContainKey(lookalike, "a file that is not named like a strip is never swept");

        // The plan's retention expires the document: its strip is marked in the same run and purged with it.
        StorageRetentionRunResult expiry = await service.RunOnceAsync(Now.AddDays(40), CancellationToken.None);
        expiry.OrphanedObjects.Should().Be(1);
        (await MarksAsync(sp)).Select(m => m.StorageKey).Should().Contain([liveDocument, liveStrip]);

        await service.RunOnceAsync(Now.AddDays(50), CancellationToken.None);
        storage.Objects.Should().NotContainKey(liveStrip, "the strip does not outlive its analysis document");
        storage.Objects.Should().ContainKey(keyframe);
    }

    /// <summary>
    /// A deleted project takes its step results with it, and the orphan pass only reaches projects that exist: the
    /// delete marks the strips itself, so the purge removes them.
    /// </summary>
    [Fact]
    public async Task Deleting_a_project_marks_its_event_strips_and_never_fails_for_a_listing_that_does()
    {
        Guid org = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        var (_, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 }, orphanSweep: true);

        string folder = $"projects/{projectId}/agentFiles/video-analysis";
        string document = $"{folder}/run-1/step-2-analysis.json";
        string strip = VideoEventStrips.KeyFor(document, "e12")!;
        string keyframe = $"{folder}/run-1/step-2-keyframes/s0.jpg";
        foreach (string key in new[] { document, strip, keyframe })
        {
            storage.Objects[key] = new byte[] { 1 };
            storage.ObjectLastModified[key] = Now.AddHours(-1);
        }

        await SeedProjectAsync(sp, org, projectId, [], artifactStorageKey: document, artifactExecutedAt: Now.AddHours(-1));

        using (IServiceScope scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            Project project = await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == projectId);

            // No listing, or one that fails: nothing is marked and nothing throws.
            await StorageRetentionService.MarkEventStripsForProjectDeleteAsync(db, project, null, CancellationToken.None);
            await StorageRetentionService.MarkEventStripsForProjectDeleteAsync(db, project, new ThrowingListing(), CancellationToken.None);
            db.ChangeTracker.Entries<StorageRetentionMark>().Should().BeEmpty();

            await StorageRetentionService.MarkEventStripsForProjectDeleteAsync(db, project, storage, CancellationToken.None);
            // Asked twice in one request: the second call must not add the same key again once saved.
            await db.SaveChangesAsync();
            await StorageRetentionService.MarkEventStripsForProjectDeleteAsync(db, project, storage, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        StorageRetentionMark mark = (await MarksAsync(sp)).Should().ContainSingle().Subject;
        mark.StorageKey.Should().Be(strip);
        mark.ProjectId.Should().Be(projectId);
        mark.OrganizationId.Should().Be(org);
        mark.Category.Should().Be("agentFiles");
    }

    private sealed class ThrowingListing : IObjectListing
    {
        public Task<IReadOnlyList<StoredObjectEntry>> ListAsync(Guid projectId, string category, CancellationToken ct) =>
            throw new InvalidOperationException("the store cannot list");
    }

    [Theory]
    [InlineData("projects/p/agentFiles/video-analysis/x/step-3-event-strips/e12.jpg", "projects/p/agentFiles/video-analysis/x/step-3-analysis.json")]
    [InlineData("projects/p/agentFiles/video-analysis/x/step-3-event-strips/e12.png", null)]
    [InlineData("projects/p/agentFiles/video-analysis/x/step-3-event-strips/notes.jpg", null)]
    [InlineData("projects/p/agentFiles/video-analysis/x/step-3-event-strips/sub/e1.jpg", null)]
    [InlineData("projects/p/agentFiles/video-analysis/x/step-3-keyframes/e1.jpg", null)]
    [InlineData("projects/p/agentFiles/video-analysis/x/step-3-analysis.json", null)]
    [InlineData("-event-strips/e1.jpg", null)]
    [InlineData("e1.jpg", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_a_key_made_for_a_strip_names_an_analysis_document(string? stripKey, string? expectedDocument) =>
        VideoEventStrips.ArtifactKeyOf(stripKey).Should().Be(expectedDocument);

    [Fact]
    public async Task Is_disabled_by_default()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 1 }, enabled: false);

        string key = $"projects/{project}/outputFiles/e1/render.mp4";
        storage.Objects[key] = new byte[] { 1 };
        await SeedProjectAsync(sp, org, project, new[] { ProjectFileRow(project, key, "outputFiles", Now.AddDays(-40)) });

        await service.StartAsync(CancellationToken.None);

        // Disabled: ExecuteAsync returns before entering the daily loop, so its task finishes at once.
        // An enabled service would instead be parked on the delay to the next daily hour, which this
        // 5-second wait would not survive.
        service.ExecuteTask.Should().NotBeNull();
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        (await MarksAsync(sp)).Should().BeEmpty();
        (await ProjectFileKeysAsync(sp)).Should().BeEquivalentTo(new[] { key });
        storage.DeletedKeys.Should().BeEmpty();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_purged_object_takes_its_moderation_verdict_with_it()
    {
        Guid org = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        var (service, sp, storage) = Build(new Dictionary<Guid, int> { [org] = 30 });
        string expired = $"projects/{project}/outputFiles/e1/old-render.mp4";
        string kept = $"projects/{project}/outputFiles/e2/new-render.mp4";
        storage.Objects[expired] = new byte[] { 1 };
        storage.Objects[kept] = new byte[] { 1 };
        await SeedProjectAsync(sp, org, project, [
            ProjectFileRow(project, expired, "outputFiles", Now.AddDays(-40)),
            ProjectFileRow(project, kept, "outputFiles", Now.AddDays(-1))]);
        using (IServiceScope scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            foreach (string key in new[] { expired, kept })
            {
                db.OutputModerationVerdicts.Add(new ReelBolt.Inference.Api.Services.Moderation.OutputModerationVerdict
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = org,
                    ProjectId = project,
                    StorageKey = key,
                    SourceVersion = "e1:1",
                    State = "Allow",
                    ModelSnapshot = "omni-moderation-2024-09-26"
                });
            }

            await db.SaveChangesAsync();
        }

        await service.RunOnceAsync(Now, CancellationToken.None);
        (await service.RunOnceAsync(Now.AddDays(8), CancellationToken.None)).PurgedObjects.Should().Be(1);

        using IServiceScope check = sp.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        (await after.OutputModerationVerdicts.Select(v => v.StorageKey).ToListAsync()).Should().Equal(kept);
    }

    [Fact]
    public async Task An_expired_output_an_avatar_lists_is_neither_marked_nor_dropped()
    {
        Guid organization = Guid.NewGuid();
        Guid project = Guid.NewGuid();
        string key = $"projects/{project}/outputFiles/presenter.mp4";
        var (service, sp, _) = Build(new Dictionary<Guid, int> { [organization] = 30 });
        ProjectFile row = ProjectFileRow(project, key, "outputFiles", Now.AddDays(-40));
        await SeedProjectAsync(sp, organization, project, new[] { row },
            artifactStorageKey: $"projects/{project}/agentFiles/a.json", artifactExecutedAt: Now.AddDays(-40), outputStorageKey: key);
        using (IServiceScope scope = sp.CreateScope())
            ReelBolt.WorkflowEngine.Tests.Avatars.AvatarSeed.Attach(scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>(), project, "Anna", row);

        await service.RunOnceAsync(Now, CancellationToken.None);

        (await MarksAsync(sp)).Select(m => m.StorageKey).Should().NotContain(key, "the step result's key is the same object");
        (await ProjectFileKeysAsync(sp)).Should().Contain(key);
    }
}
