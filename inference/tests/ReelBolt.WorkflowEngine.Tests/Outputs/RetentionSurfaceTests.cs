#nullable enable
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
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// Storage retention (purge of due marks, orphan sweep) and the preview sweeper delete objects of a project with no
/// output marker, and leave a project that keeps its outputs alone, objects included.
/// </summary>
public sealed class RetentionSurfaceTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);

    private sealed class PlanStub : IOrganizationEntitlements
    {
        public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            new(new OrganizationEntitlementSet { RetentionDays = 30 });
    }

    private sealed class Rig
    {
        public StorageRetentionService Service { get; }
        public IServiceProvider Services { get; }
        public FakeFileStorage Storage { get; } = new();
        public Guid Org { get; } = Guid.NewGuid();

        public Rig(bool markerReadFails = false)
        {
            string dbName = Guid.NewGuid().ToString();
            ServiceCollection services = new();
            if (markerReadFails)
            {
                DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
                    .UseInMemoryDatabase(dbName)
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
                services.AddScoped<InferenceApiDbContext>(_ => new MarkerReadFailsContext(options));
            }
            else
            {
                services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(dbName)
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            }

            services.AddSingleton<IFileStorageService>(Storage);
            services.AddSingleton<IObjectListing>(Storage);
            services.AddSingleton<ITenantContext>(new TenantContext(new HttpContextAccessor()));
            services.AddSingleton<IOrganizationEntitlements>(new PlanStub());
            Services = services.BuildServiceProvider();
            IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Billing:StorageRetention:Enabled"] = "true",
            }).Build();
            Service = new StorageRetentionService(Services.GetRequiredService<IServiceScopeFactory>(), cfg, NullLogger<StorageRetentionService>.Instance);
        }

        public async Task<Guid> SeedProjectAsync(SurfaceState state)
        {
            Guid project = Guid.NewGuid();
            using IServiceScope scope = Services.CreateScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.Projects.Add(new Project
            {
                Id = project,
                Name = $"p-{project:N}",
                OrganizationId = Org,
                CsamOutputHoldAt = state == SurfaceState.Held ? Now.AddDays(-1) : null,
            });
            if (state == SurfaceState.ModerationOnly)
            {
                db.ProjectFiles.Add(new ProjectFile
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project,
                    StorageKey = $"projects/{project}/userFiles/flagged.mp4",
                    Category = "userFiles",
                    OriginalFileName = "flagged.mp4",
                    StorageBucket = "fake-bucket",
                    MimeType = "video/mp4",
                    SizeBytes = 8,
                    UploadedAt = Now.AddDays(-3),
                    ModerationStatus = FileModerationStatus.Quarantined,
                });
            }
            await db.SaveChangesAsync();
            return project;
        }

        public async Task AddDueMarkAsync(Guid project, string key)
        {
            Storage.Objects[key] = [1];
            using IServiceScope scope = Services.CreateScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.StorageRetentionMarks.Add(new StorageRetentionMark
            {
                Id = Guid.NewGuid(),
                OrganizationId = Org,
                ProjectId = project,
                StorageKey = key,
                Category = "outputFiles",
                MarkedAt = Now.AddDays(-30),
            });
            await db.SaveChangesAsync();
        }

        public async Task<List<StorageRetentionMark>> MarksAsync()
        {
            using IServiceScope scope = Services.CreateScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            return await db.StorageRetentionMarks.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        }
    }

    [Fact]
    public async Task A_due_mark_of_a_project_that_keeps_its_outputs_deletes_nothing()
    {
        Rig r = new();
        Guid kept = await r.SeedProjectAsync(SurfaceState.Held);
        Guid other = await r.SeedProjectAsync(SurfaceState.Clear);
        string keptKey = $"projects/{kept}/outputFiles/due.mp4", otherKey = $"projects/{other}/outputFiles/due.mp4";
        await r.AddDueMarkAsync(kept, keptKey);
        await r.AddDueMarkAsync(other, otherKey);

        await r.Service.RunOnceAsync(Now, CancellationToken.None);

        r.Storage.Objects.Should().ContainKey(keptKey).And.NotContainKey(otherKey);
        (await r.MarksAsync()).Where(m => m.ProjectId == kept).Should().OnlyContain(m => m.PurgedAt == null);
    }

    [Theory]
    [InlineData(SurfaceState.Clear)]
    [InlineData(SurfaceState.ModerationOnly)]
    public async Task A_due_mark_is_purged_when_the_project_keeps_nothing(SurfaceState state)
    {
        Rig r = new();
        Guid project = await r.SeedProjectAsync(state);
        string key = $"projects/{project}/outputFiles/due.mp4";
        await r.AddDueMarkAsync(project, key);

        await r.Service.RunOnceAsync(Now, CancellationToken.None);

        r.Storage.Objects.Should().NotContainKey(key);
    }

    [Fact]
    public async Task The_orphan_sweep_leaves_a_project_that_keeps_its_outputs_alone()
    {
        Rig r = new();
        Guid kept = await r.SeedProjectAsync(SurfaceState.Held);
        Guid other = await r.SeedProjectAsync(SurfaceState.Clear);
        foreach (Guid p in new[] { kept, other })
        {
            string orphan = $"projects/{p}/userFiles/stray.mp4";
            r.Storage.Objects[orphan] = [1];
            r.Storage.ObjectLastModified[orphan] = Now.AddDays(-2);
        }

        StorageRetentionRunResult run = await r.Service.RunOnceAsync(Now, CancellationToken.None);

        run.OrphanedObjects.Should().Be(1);
        (await r.MarksAsync()).Should().ContainSingle().Which.ProjectId.Should().Be(other);
    }

    [Theory]
    [InlineData(SurfaceState.Clear)]
    [InlineData(SurfaceState.ModerationOnly)]
    public async Task The_orphan_sweep_marks_a_stray_object_when_the_project_keeps_nothing(SurfaceState state)
    {
        Rig r = new();
        Guid project = await r.SeedProjectAsync(state);
        string orphan = $"projects/{project}/userFiles/stray.mp4";
        r.Storage.Objects[orphan] = [1];
        r.Storage.ObjectLastModified[orphan] = Now.AddDays(-2);

        StorageRetentionRunResult run = await r.Service.RunOnceAsync(Now, CancellationToken.None);

        run.OrphanedObjects.Should().Be(1);
    }

    // ---- preview sweeper -----------------------------------------------------------------------

    private static async Task<(InferenceApiDbContext Db, Guid Project, string Key)> PreviewAsync(SurfaceState state, SurfaceWorld? world = null)
    {
        SurfaceWorld w = world ?? new();
        w.In(state);
        WorkflowDefinition preview = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = w.Project.Id,
            Name = TimelinePreviewSweeper.WorkflowNamePrefix + "cut",
            Kind = WorkflowDefinitionKind.TimelineRender,
            CreatedAt = Now.AddDays(-30),
            UpdatedAt = Now.AddDays(-30),
        };
        WorkflowExecution run = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = preview.Id, ProjectId = w.Project.Id, Status = ExecutionStatus.Passed, StartedAt = Now.AddDays(-30) };
        string key = $"projects/{w.Project.Id}/outputFiles/preview.mp4";
        w.Db.WorkflowDefinitions.Add(preview);
        w.Db.WorkflowExecutions.Add(run);
        w.Db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = w.Step.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = key,
            ExecutedAt = Now.AddDays(-30),
        });
        await w.Db.SaveChangesAsync();
        return (w.Db, w.Project.Id, key);
    }

    [Fact]
    public async Task A_preview_of_a_project_that_keeps_its_outputs_is_not_deleted()
    {
        (InferenceApiDbContext db, _, string key) = await PreviewAsync(SurfaceState.Held);
        FakeFileStorage storage = new();
        storage.Objects[key] = [1];

        (int files, int workflows) = await TimelinePreviewSweeper.SweepOnceAsync(db, storage, Now, TimeSpan.FromDays(1), CancellationToken.None);

        (files, workflows).Should().Be((0, 0));
        storage.Objects.Should().ContainKey(key);
    }

    [Theory]
    [InlineData(SurfaceState.Clear)]
    [InlineData(SurfaceState.ModerationOnly)]
    public async Task A_preview_is_deleted_when_the_project_keeps_nothing(SurfaceState state)
    {
        (InferenceApiDbContext db, _, string key) = await PreviewAsync(state);
        FakeFileStorage storage = new();
        storage.Objects[key] = [1];

        (int files, int workflows) = await TimelinePreviewSweeper.SweepOnceAsync(db, storage, Now, TimeSpan.FromDays(1), CancellationToken.None);

        files.Should().Be(1);
        workflows.Should().Be(1);
        storage.Objects.Should().NotContainKey(key);
    }

    [Fact]
    public async Task A_due_mark_is_left_alone_when_the_marker_cannot_be_read()
    {
        Rig r = new(markerReadFails: true);
        Guid project = await r.SeedProjectAsync(SurfaceState.Clear);
        string key = $"projects/{project}/outputFiles/due.mp4";
        await r.AddDueMarkAsync(project, key);

        await r.Service.RunOnceAsync(Now, CancellationToken.None);

        r.Storage.Objects.Should().ContainKey(key);
        (await r.MarksAsync()).Should().OnlyContain(m => m.PurgedAt == null);
    }

    [Fact]
    public async Task The_orphan_sweep_marks_nothing_when_the_marker_cannot_be_read()
    {
        Rig r = new(markerReadFails: true);
        Guid project = await r.SeedProjectAsync(SurfaceState.Clear);
        string orphan = $"projects/{project}/userFiles/stray.mp4";
        r.Storage.Objects[orphan] = [1];
        r.Storage.ObjectLastModified[orphan] = Now.AddDays(-2);

        StorageRetentionRunResult run = await r.Service.RunOnceAsync(Now, CancellationToken.None);

        run.OrphanedObjects.Should().Be(0);
        (await r.MarksAsync()).Should().BeEmpty();
        r.Storage.Objects.Should().ContainKey(orphan);
    }

    [Fact]
    public async Task A_preview_is_not_deleted_when_the_marker_cannot_be_read()
    {
        SurfaceWorld world = new();
        (_, _, string key) = await PreviewAsync(SurfaceState.Clear, world);
        using MarkerReadFailsContext failing = world.MarkerFailingDb();
        FakeFileStorage storage = new();
        storage.Objects[key] = [1];

        (int files, int workflows) = await TimelinePreviewSweeper.SweepOnceAsync(failing, storage, Now, TimeSpan.FromDays(1), CancellationToken.None);

        (files, workflows).Should().Be((0, 0));
        storage.Objects.Should().ContainKey(key);
    }
}
