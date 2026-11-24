using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>QA fix round, layer 2 (restore, diff, idle sweep, run-start capture).</summary>
public class ProjectHistoryQaRound2Tests
{
    // ── restore: partial success (finding 4) ──────────────────────────────────────────────────

    [Fact]
    public async Task A_handler_that_throws_is_skipped_RESTORE_FAILED_and_the_rest_still_apply()
    {
        using Fixture f = new(pronunciation: new ThrowingHandler(ProjectVersionItemKind.Pronunciation));
        ProjectFile file = f.AddFile("a.txt", "one");
        f.Db.ProjectPronunciationEntries.Add(new ProjectPronunciationEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Term = "SQL",
            Spelling = "sequel"
        });
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.Project.Name = "Renamed";
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Project, [], f.Project.OwnerId, CancellationToken.None))!;

        result.Skipped.Should().ContainSingle(s => s.Kind == ProjectVersionItemKind.Pronunciation
                                                  && s.Reason == ProjectHistoryRestoreReasons.RestoreFailed);
        result.Restored.Should().Contain(e => e.Kind == ProjectVersionItemKind.Project);
        result.Restored.Should().Contain(e => e.Kind == ProjectVersionItemKind.File && e.Key == file.Id.ToString("D"));
        (await f.Db.Projects.AsNoTracking().SingleAsync(p => p.Id == f.Project.Id)).Name.Should().Be("P");

        // The lock was released: a second restore is not refused.
        ProjectHistoryRestoreResult again = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Project, [], f.Project.OwnerId, CancellationToken.None))!;
        again.SafetyVersionId.Should().NotBe(result.SafetyVersionId);
    }

    [Fact]
    public async Task The_controller_answers_413_when_the_safety_capture_is_too_large()
    {
        using Fixture f = new();
        Mock<IProjectHistoryRestoreService> restore = new();
        restore.Setup(r => r.RestoreAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ProjectHistoryRestoreScope>(),
                It.IsAny<IReadOnlyList<ProjectHistoryRestoreItemRef>>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProjectHistoryTooLargeException(20001, 20000));
        ProjectHistoryController controller = f.Controller(restore.Object);

        ActionResult<ProjectHistoryRestoreResponse> response = await controller.Restore(
            f.Project.Id, Guid.NewGuid(), new ProjectHistoryRestoreRequest("project", null), CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(413);
    }

    // ── restore: unverified files (finding 9) ─────────────────────────────────────────────────

    [Fact]
    public async Task An_unverified_file_is_restored_only_while_unchanged_and_a_changed_one_keeps_its_row()
    {
        using Fixture f = new();
        ProjectFile unchanged = f.AddFile("same.txt", "abc");
        ProjectFile changed = f.AddFile("edited.txt", "abc");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        foreach (ProjectVersionItem item in f.Db.ProjectVersionItems.Where(i => i.VersionId == v1.Id && i.Kind == ProjectVersionItemKind.File))
        {
            item.ContentId = ContentId.Unverified(Guid.Parse(item.ItemKey));
            item.StorageETag = null;
        }

        await f.Db.SaveChangesAsync();

        changed.SizeBytes = 999;
        changed.MimeType = "image/png";
        changed.UploadedAt = v1.CreatedAt.AddMinutes(5);
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Item,
            [new(ProjectVersionItemKind.File, unchanged.Id.ToString("D")), new(ProjectVersionItemKind.File, changed.Id.ToString("D"))],
            f.Project.OwnerId, CancellationToken.None))!;

        result.Restored.Should().ContainSingle(e => e.Key == unchanged.Id.ToString("D"));
        result.Skipped.Should().ContainSingle(s => s.Key == changed.Id.ToString("D")
                                                  && s.Reason == ProjectHistoryRestoreReasons.Unverified);
        ProjectFile live = await f.Db.ProjectFiles.AsNoTracking().SingleAsync(x => x.Id == changed.Id);
        (live.SizeBytes, live.MimeType).Should().Be((999L, "image/png"), "a skipped item never overwrites the live row");
    }

    [Fact]
    public async Task A_file_an_avatar_lists_is_never_overwritten_by_a_restore()
    {
        using Fixture f = new();
        ProjectFile face = f.AddFile("face.png", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        ReelBolt.WorkflowEngine.Tests.Avatars.AvatarSeed.Attach(f.Db, f.Project.Id, "Anna", face);
        face.SizeBytes = 999;
        face.UploadedAt = v1.CreatedAt.AddMinutes(5);
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Item,
            [new(ProjectVersionItemKind.File, face.Id.ToString("D"))],
            f.Project.OwnerId, CancellationToken.None))!;

        result.Skipped.Should().ContainSingle(sk => sk.Key == face.Id.ToString("D")
                                                    && sk.Reason == ProjectHistoryRestoreReasons.FileInUseByAvatar);
        (await f.Db.ProjectFiles.AsNoTracking().SingleAsync(x => x.Id == face.Id)).SizeBytes.Should().Be(999);
    }

    // ── restore: path swaps (finding 10) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Project_scope_restores_a_two_file_path_swap_without_PATH_TAKEN()
    {
        using Fixture f = new();
        ProjectFile a = f.AddFile("a.txt", "A");
        ProjectFile b = f.AddFile("b.txt", "B");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        a.OriginalPath = "b.txt";
        b.OriginalPath = "a.txt";
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Project, [], f.Project.OwnerId, CancellationToken.None))!;

        result.Skipped.Should().BeEmpty();
        result.Restored.Should().Contain(e => e.Key == a.Id.ToString("D")).And.Contain(e => e.Key == b.Id.ToString("D"));
        Dictionary<Guid, string?> paths = await f.Db.ProjectFiles.AsNoTracking()
            .ToDictionaryAsync(x => x.Id, x => x.OriginalPath);
        paths[a.Id].Should().Be("a.txt");
        paths[b.Id].Should().Be("b.txt");
    }

    // ── diff: same-size edit (finding 11), cap, designSystem mode ─────────────────────────────

    [Fact]
    public async Task An_etag_side_and_a_sha_side_are_unchanged_only_when_both_are_the_live_bytes()
    {
        using Fixture f = new();
        ProjectFile edited = f.AddFile("edited.txt", "one");
        ProjectFile steady = f.AddFile("steady.txt", "two");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        v1.Cause.Should().Be(ProjectVersionCause.Named);

        // Same-size edit, then the hash sweeper vouches for the NEW bytes; the steady file is hashed as is.
        f.Store.Put(edited.StorageKey, "uno");
        edited.ContentSha256 = new string('a', 64);
        edited.ContentSha256ETag = f.Store.ETagAt(edited.StorageKey);
        steady.ContentSha256 = new string('b', 64);
        steady.ContentSha256ETag = f.Store.ETagAt(steady.StorageKey);
        await f.Db.SaveChangesAsync();
        ProjectVersion v2 = await f.CaptureAsync();

        ProjectHistoryChanges changes = (await f.Service.ComputeChangesAsync(
            f.Project.Id, v1.Id, v2.Id, CancellationToken.None))!;

        changes.Changes.Should().Contain(c => c.Key == edited.Id.ToString("D") && c.Change == "changed");
        changes.Changes.Should().NotContain(c => c.Key == steady.Id.ToString("D"),
            "the etag side's ETag is the one the live hash was taken at, so it is proven the same bytes");
    }

    [Fact]
    public async Task Changes_are_capped_at_5000_and_flagged_truncated()
    {
        using Fixture f = new();
        ProjectVersion version = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Number = 1,
            Cause = ProjectVersionCause.Named,
            Name = "big",
            ManifestDigest = new string('c', 64),
            ItemCount = ProjectHistoryService.MaxChanges + 1
        };
        f.Db.ProjectVersions.Add(version);
        for (int i = 0; i < ProjectHistoryService.MaxChanges + 1; i++)
        {
            f.Db.ProjectVersionItems.Add(new ProjectVersionItem
            {
                Id = Guid.NewGuid(),
                VersionId = version.Id,
                ProjectId = f.Project.Id,
                Kind = ProjectVersionItemKind.File,
                ItemKey = Guid.NewGuid().ToString("D"),
                Name = $"f{i}.txt",
                Category = "userFiles",
                ContentId = $"unverified:{i}",
                SizeBytes = 1
            });
        }

        await f.Db.SaveChangesAsync();

        ProjectHistoryChanges changes = (await f.Service.ComputeChangesAsync(
            f.Project.Id, version.Id, null, CancellationToken.None))!;

        changes.Changes.Should().HaveCount(ProjectHistoryService.MaxChanges);
        changes.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task A_design_system_item_diffs_in_designSystem_mode()
    {
        using Fixture f = new();
        Guid systemId = Guid.NewGuid();
        Guid vA = Guid.NewGuid();
        Guid vB = Guid.NewGuid();
        foreach ((Guid id, int n, string json) in new[] { (vA, 1, "{\"colors\":{\"primary\":\"#111111\"}}"), (vB, 2, "{\"colors\":{\"primary\":\"#222222\"}}") })
        {
            f.Db.DesignSystemVersions.Add(new DesignSystemVersion
            {
                Id = id,
                DesignSystemId = systemId,
                VersionNumber = n,
                DocumentJson = json,
                DocumentDigest = new string((char)('a' + n), 64)
            });
        }

        ProjectVersion Make(int number, Guid designVersion, string digest)
        {
            ProjectVersion v = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = f.Project.Id,
                Number = number,
                Cause = ProjectVersionCause.Named,
                Name = $"v{number}",
                ManifestDigest = new string('d', 64),
                ItemCount = 1
            };
            f.Db.ProjectVersions.Add(v);
            f.Db.ProjectVersionItems.Add(new ProjectVersionItem
            {
                Id = Guid.NewGuid(),
                VersionId = v.Id,
                ProjectId = f.Project.Id,
                Kind = ProjectVersionItemKind.DesignSystem,
                ItemKey = "design-system",
                Name = "Brand",
                DocumentSha256 = digest,
                DesignSystemVersionId = designVersion
            });
            return v;
        }

        ProjectVersion v1 = Make(1, vA, "x");
        ProjectVersion v2 = Make(2, vB, "y");
        await f.Db.SaveChangesAsync();

        ProjectHistoryItemDiff diff = (await f.Service.ComputeItemDiffAsync(
            f.Project.Id, v1.Id, ProjectVersionItemKind.DesignSystem, "design-system", v2.Id, CancellationToken.None))!;

        diff.Mode.Should().Be(ProjectHistoryDiffModes.DesignSystem);
        diff.DesignSystem.Should().NotBeNull();
    }

    // ── idle sweep (finding 12) ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_idle_sweep_ignores_activity_no_version_holds()
    {
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"qa2-idle-{Guid.NewGuid():N}").Options);
        DateTime stale = DateTime.UtcNow.AddDays(-3);
        Project outputsOnly = NewProject(stale);
        Project filesChanged = NewProject(stale);
        db.Projects.AddRange(outputsOnly, filesChanged);
        db.ProjectFiles.Add(NewFile(outputsOnly.Id, "outputFiles"));
        db.ProjectFiles.Add(NewFile(filesChanged.Id, "userFiles"));
        db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "hidden",
            ProjectId = outputsOnly.Id,
            Kind = WorkflowDefinitionKind.CompositionRevision,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-30)
        });
        await db.SaveChangesAsync();

        Mock<IProjectHistoryService> history = new();
        history.Setup(h => h.TryCaptureAutoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        ServiceCollection services = new();
        services.AddSingleton(db);
        services.AddSingleton(history.Object);
        Mock<IOptionsMonitor<ProjectHistoryOptions>> monitor = new();
        monitor.SetupGet(m => m.CurrentValue).Returns(new ProjectHistoryOptions());
        ProjectHistoryIdleCaptureService sweeper = new(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), monitor.Object,
            NullLogger<ProjectHistoryIdleCaptureService>.Instance);

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        history.Verify(h => h.TryCaptureAutoAsync(filesChanged.Id, ProjectVersionCause.Idle, null, It.IsAny<CancellationToken>()), Times.Once);
        history.VerifyNoOtherCalls();
    }

    // ── pronunciation notice (low) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pronunciation_entries_dropped_by_validation_add_a_notice()
    {
        using Fixture f = new();
        f.Db.ProjectPronunciationEntries.Add(new ProjectPronunciationEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Term = "SQL",
            Spelling = "sequel"
        });
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        // Replace the stored document with one holding a good and an unusable entry.
        string json = new JsonArray(
            new JsonObject { ["term"] = "SQL", ["spelling"] = "sequel", ["caseSensitive"] = false },
            new JsonObject { ["term"] = new string('x', 500), ["spelling"] = "long" }).ToJsonString();
        ProjectVersionItem item = await f.Db.ProjectVersionItems.SingleAsync(
            i => i.VersionId == v1.Id && i.Kind == ProjectVersionItemKind.Pronunciation);
        var doc = await f.Db.ProjectHistoryDocuments
            .SingleOrDefaultAsync(d => d.ProjectId == f.Project.Id && d.Sha256 == item.DocumentSha256);
        doc.Should().NotBeNull();
        doc!.Json = json;
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Item,
            [new(ProjectVersionItemKind.Pronunciation, item.ItemKey)], f.Project.OwnerId, CancellationToken.None))!;

        result.Restored.Should().ContainSingle();
        result.Notices.Should().ContainSingle(n => n.Code == ProjectHistoryRestoreNotices.PronunciationEntrySkipped);
        (await f.Db.ProjectPronunciationEntries.CountAsync(e => e.ProjectId == f.Project.Id)).Should().Be(1);
    }

    // ── beforeRun on Execute, Retry and Resume (contract section 9) ───────────────────────────

    [Fact]
    public async Task Every_REST_run_start_pricing_captures_beforeRun_once_after_the_gates_pass()
    {
        Guid userId = Guid.NewGuid();
        Guid orgId = TestTenancy.OrgOf(userId);
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"qa2-run-{Guid.NewGuid():N}").Options);
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = userId, OrganizationId = orgId };
        Guid workflowId = Guid.NewGuid();
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = workflowId, Name = "W", ProjectId = project.Id });
        await db.SaveChangesAsync();
        Mock<IDesignBuildEstimateGate> designBuild = new();
        designBuild
            .Setup(g => g.CheckAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DesignBuildGateResult.NotApplicable);
        Mock<ICurrentUser> user = new();
        user.SetupGet(u => u.UserId).Returns(userId);
        user.SetupGet(u => u.IsAuthenticated).Returns(true);
        user.SetupGet(u => u.OrganizationId).Returns(orgId);
        Mock<IProjectHistoryService> history = new();
        List<string> causes = [];
        history.Setup(h => h.TryCaptureAutoAsync(project.Id, It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, Guid?, CancellationToken>((_, cause, _, _) => causes.Add(cause))
            .Returns(Task.CompletedTask);
        RunStartGate gate = new(db, user.Object, null, null, designBuild.Object, history: history.Object);

        // Execute prices the saved workflow; Retry prices every step; Resume prices the charged steps.
        foreach (RunStartPricing pricing in new[]
                 {
                     RunStartPricing.SavedWorkflow,
                     RunStartPricing.AllOf([]),
                     RunStartPricing.Charged([], [])
                 })
        {
            (await gate.RefuseAsync(project, workflowId, null, pricing, null, CancellationToken.None)).Should().BeNull();
        }

        causes.Should().Equal(ProjectVersionCause.BeforeRun, ProjectVersionCause.BeforeRun, ProjectVersionCause.BeforeRun);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static Project NewProject(DateTime updatedAt) => new()
    {
        Id = Guid.NewGuid(),
        Name = "P",
        OwnerId = Guid.NewGuid(),
        OrganizationId = Guid.NewGuid(),
        UpdatedAt = updatedAt
    };

    private static ProjectFile NewFile(Guid projectId, string category) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Category = category,
        OriginalFileName = "f.txt",
        OriginalPath = "f.txt",
        StorageKey = $"projects/{projectId}/{category}/{Guid.NewGuid():N}",
        StorageBucket = "t",
        MimeType = "text/plain",
        SizeBytes = 1,
        UploadedAt = DateTime.UtcNow.AddMinutes(-30)
    };

    private sealed class ThrowingHandler(ProjectVersionItemKind kind) : IProjectRestoreKindHandler
    {
        public ProjectVersionItemKind Kind => kind;

        public Task<ProjectRestoreOutcome> ApplyAsync(
            ProjectRestoreWriteContext context, ProjectVersionItem item, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(IProjectRestoreKindHandler? pronunciation = null)
        {
            Guid owner = Guid.NewGuid();
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"qa2-{Guid.NewGuid():N}").Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner) };
            Db.Projects.Add(Project);
            Db.SaveChanges();
            Service = new ProjectHistoryService(
                Db, Store, Options.Create(new ProjectHistoryOptions()), NullLogger<ProjectHistoryService>.Instance);
            Restore = new ProjectHistoryRestoreService(
                Db, Service, new InProcessProjectRestoreLock(),
                [
                    new FileRestoreKindHandler(Db, Store),
                    new ProjectRestoreKindHandler(Db),
                    pronunciation ?? new PronunciationRestoreKindHandler(Db)
                ],
                NullLogger<ProjectHistoryRestoreService>.Instance);
        }

        public InferenceApiDbContext Db { get; }
        public Project Project { get; }
        public HistoryTestStore Store { get; } = new();
        public ProjectHistoryService Service { get; }
        public ProjectHistoryRestoreService Restore { get; }

        public ProjectFile AddFile(string path, string content)
        {
            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                Category = "userFiles",
                OriginalFileName = path,
                OriginalPath = path,
                StorageKey = $"projects/{Project.Id}/userFiles/{Guid.NewGuid():N}",
                StorageBucket = "t",
                MimeType = "text/plain",
                SizeBytes = content.Length
            };
            Db.ProjectFiles.Add(file);
            Store.Put(file.StorageKey, content);
            return file;
        }

        public async Task<ProjectVersion> CaptureAsync() =>
            (await Service.CaptureAsync(
                Project.Id, ProjectVersionCause.Named, Guid.NewGuid().ToString("N")[..8], null, CancellationToken.None))!;

        public ProjectHistoryController Controller(IProjectHistoryRestoreService restore)
        {
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(Project.OwnerId);
            user.SetupGet(u => u.Email).Returns("qa2@example.com");
            user.SetupGet(u => u.IsAuthenticated).Returns(true);
            user.SetupGet(u => u.OrganizationId).Returns(Project.OrganizationId);
            user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Owner);
            ProjectHistoryEraser eraser = new(Db, Store, NullLogger<ProjectHistoryEraser>.Instance);
            return new ProjectHistoryController(TestTenancy.Access(Db, user.Object), Service, eraser, user.Object, restore)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        public void Dispose() => Db.Dispose();
    }
}
