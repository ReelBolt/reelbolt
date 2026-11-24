using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>QA round 3, restore layer: per-file removal, run-start restore gate, parking undone, idle sweep digest.</summary>
public class ProjectHistoryQaRound3Tests
{
    // ── finding 26: project-scope removal is all-or-nothing per file ──────────────────────────

    [Fact]
    public async Task A_byte_delete_failure_after_the_row_removal_is_logged_and_does_not_fail_the_restore()
    {
        using Fixture f = new();
        f.AddFile("keep.txt", "k");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        ProjectFile[] extras = [f.AddFile("x1.txt", "1"), f.AddFile("x2.txt", "2"), f.AddFile("x3.txt", "3")];
        await f.Db.SaveChangesAsync();
        Dictionary<Guid, string> keys = extras.ToDictionary(e => e.Id, e => e.StorageKey);
        f.Store.FailDeleteOnCall = f.Store.DeleteCalls + 2;

        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Project, [], f.Project.OwnerId, CancellationToken.None))!;

        result.Skipped.Should().NotContain(s => s.Reason == ProjectHistoryRestoreReasons.RestoreFailed);
        result.Removed.Where(e => e.Kind == ProjectVersionItemKind.File).Should().HaveCount(3);
        List<Guid> rows = await f.Db.ProjectFiles.AsNoTracking().Select(x => x.Id).ToListAsync();
        rows.Should().NotContain(extras.Select(e => e.Id));
        f.Store.Objects.Count(o => keys.ContainsValue(o.Key)).Should().Be(1);
    }

    [Fact]
    public async Task A_removed_file_row_is_gone_before_its_bytes_are_deleted()
    {
        using Fixture f = new();
        f.AddFile("keep.txt", "k");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        ProjectFile extra = f.AddFile("x1.txt", "1");
        await f.Db.SaveChangesAsync();
        string key = extra.StorageKey;
        bool rowStillThereAtDelete = true;
        f.Store.OnDelete = k =>
        {
            if (k == key)
                rowStillThereAtDelete = f.Db.ProjectFiles.AsNoTracking().Any(x => x.Id == extra.Id);
        };

        await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Project, [], f.Project.OwnerId, CancellationToken.None);

        rowStillThereAtDelete.Should().BeFalse("the row removal is saved before the bytes are deleted");
        f.Store.Objects.Should().NotContainKey(key);
    }

    [Fact]
    public async Task The_test_hold_only_forces_the_projects_it_names_and_nests()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), other = Guid.NewGuid();
        using InferenceApiDbContext db = NewDb("qa3-hold-seam");
        using (RestoreRunGate.HoldForTests(a))
        {
            using (RestoreRunGate.HoldForTests(b))
            {
                (await RestoreRunGate.IsHeldAsync(db, a, CancellationToken.None)).Should().BeTrue();
                (await RestoreRunGate.IsHeldAsync(db, b, CancellationToken.None)).Should().BeTrue();
            }

            (await RestoreRunGate.IsHeldAsync(db, b, CancellationToken.None)).Should().BeFalse();
            // An unlisted project goes to the real probe (InMemory: no advisory locks, so not held).
            (await RestoreRunGate.IsHeldAsync(db, other, CancellationToken.None)).Should().BeFalse();
        }
    }

    // ── finding 28: a cancelled project restore never strands files at .restore-tmp ───────────

    [Fact]
    public async Task A_cancelled_project_restore_puts_parked_files_back()
    {
        using CancellationTokenSource cts = new();
        using Fixture f = new(cancelOnFirstFile: cts);
        ProjectFile a = f.AddFile("a.txt", "A");
        ProjectFile b = f.AddFile("b.txt", "B");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        a.OriginalPath = "b.txt";
        b.OriginalPath = "a.txt";
        await f.Db.SaveChangesAsync();

        Func<Task> act = () => f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Project, [], f.Project.OwnerId, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        Dictionary<Guid, string?> paths = await f.Db.ProjectFiles.AsNoTracking()
            .ToDictionaryAsync(x => x.Id, x => x.OriginalPath);
        paths.Values.Should().NotContain(p => p != null && p.StartsWith(".restore-tmp"));
        paths[a.Id].Should().Be("b.txt");
        paths[b.Id].Should().Be("a.txt");
    }

    // ── finding 27: every run start honours the restore lock ──────────────────────────────────

    [Fact]
    public async Task The_assistant_ExecuteWorkflow_tool_refuses_with_RESTORE_IN_PROGRESS_while_a_restore_holds_the_lock()
    {
        using InferenceApiDbContext db = NewDb("qa3-assistant");
        Guid owner = Guid.NewGuid();
        Project project = SeedProject(db, owner);
        Guid workflowId = Guid.NewGuid();
        db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = workflowId, Name = "W", ProjectId = project.Id });
        await db.SaveChangesAsync();
        Mock<IAssistantExecutionGuard> guard = new();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(
            db, new FakeCurrentUser(owner), executionGuard: guard.Object);
        AIFunction tool = provider.GetTools(new AssistantToolContext(owner, project.Id))
            .Single(t => t.Name == "ExecuteWorkflow");
        AIFunctionArguments args = new()
        {
            ["workflowId"] = workflowId,
            ["userRequest"] = null,
            ["regenerateVideoClips"] = null
        };

        using IDisposable held = RestoreRunGate.HoldForTests(project.Id);
        object? raw = await tool.InvokeAsync(args);

        string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()!
            : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;
        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("code").GetString().Should().Be(ProjectHistoryRestoreCodes.RestoreInProgress);
        doc.RootElement.GetProperty("executed").GetBoolean().Should().BeFalse();
        guard.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task The_audio_cleanup_hidden_run_refuses_with_409_while_a_restore_holds_the_lock()
    {
        using InferenceApiDbContext db = NewDb("qa3-cleanup");
        Guid owner = Guid.NewGuid();
        Project project = SeedProject(db, owner);
        Caller caller = new(owner);
        AudioCleanupService service = new(
            db, TestTenancy.Access(db, caller), caller,
            new Mock<IWorkflowExecutionService>().Object,
            new Mock<IAssistantExecutionGuard>().Object,
            new Mock<IInferenceProviderResolver>().Object);

        using IDisposable held = RestoreRunGate.HoldForTests(project.Id);
        AudioCleanupResult<AudioCleanupStartedResponse> result = await service.StartAsync(
            project.Id, "any-media", null, CancellationToken.None);

        result.Value.Should().BeNull();
        result.Failure.Should().NotBeNull();
        result.Failure!.Code.Should().Be(ProjectHistoryRestoreCodes.RestoreInProgress);
        result.Failure.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task The_REST_run_gate_still_refuses_through_the_shared_helper()
    {
        using InferenceApiDbContext db = NewDb("qa3-rest");
        Guid owner = Guid.NewGuid();
        Project project = SeedProject(db, owner);
        RunStartGate gate = new(db, new Caller(owner), null, null, new Mock<IDesignBuildEstimateGate>().Object);

        using IDisposable held = RestoreRunGate.HoldForTests(project.Id);
        ActionResult? refused = await gate.RefuseAsync(
            project, Guid.NewGuid(), null, RunStartPricing.SavedWorkflow, null, CancellationToken.None);

        refused.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
    }

    // ── F12: the idle sweep sees only versioned changes on the project row ────────────────────

    [Fact]
    public async Task A_folder_or_tag_change_alone_triggers_no_idle_capture()
    {
        using InferenceApiDbContext db = NewDb("qa3-idle-folder");
        Project project = SeedProject(db, Guid.NewGuid());
        project.UpdatedAt = DateTime.UtcNow.AddMinutes(-30);
        AddVersionHoldingProjectDocument(db, project, DateTime.UtcNow.AddMinutes(-60));
        await db.SaveChangesAsync();
        Mock<IProjectHistoryService> history = new();

        (await NewSweeper(db, history.Object).SweepOnceAsync(CancellationToken.None)).Should().Be(0);

        history.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_name_change_after_the_newest_version_triggers_an_idle_capture()
    {
        using InferenceApiDbContext db = NewDb("qa3-idle-rename");
        Project project = SeedProject(db, Guid.NewGuid());
        AddVersionHoldingProjectDocument(db, project, DateTime.UtcNow.AddMinutes(-60));
        project.Name = "Renamed";
        project.UpdatedAt = DateTime.UtcNow.AddMinutes(-30);
        await db.SaveChangesAsync();
        Mock<IProjectHistoryService> history = new();
        history.Setup(h => h.TryCaptureAutoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        (await NewSweeper(db, history.Object).SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        history.Verify(h => h.TryCaptureAutoAsync(project.Id, ProjectVersionCause.Idle, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static InferenceApiDbContext NewDb(string name) => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase($"{name}-{Guid.NewGuid():N}").Options);

    private static Project SeedProject(InferenceApiDbContext db, Guid owner)
    {
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = owner,
            OrganizationId = TestTenancy.OrgOf(owner),
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
        };
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    /// <summary>A version whose Project item holds the project's CURRENT name/description document.</summary>
    private static void AddVersionHoldingProjectDocument(InferenceApiDbContext db, Project project, DateTime createdAt)
    {
        ProjectVersion version = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Number = 1,
            Cause = ProjectVersionCause.Named,
            Name = "v1",
            ManifestDigest = new string('d', 64),
            ItemCount = 1,
            CreatedAt = createdAt
        };
        db.ProjectVersions.Add(version);
        db.ProjectVersionItems.Add(new ProjectVersionItem
        {
            Id = Guid.NewGuid(),
            VersionId = version.Id,
            ProjectId = project.Id,
            Kind = ProjectVersionItemKind.Project,
            ItemKey = "project",
            Name = project.Name,
            DocumentSha256 = ProjectHistoryDocuments.Sha256(ProjectHistoryDocuments.Project(project))
        });
    }

    private static ProjectHistoryIdleCaptureService NewSweeper(InferenceApiDbContext db, IProjectHistoryService history)
    {
        ServiceCollection services = new();
        services.AddSingleton(db);
        services.AddSingleton(history);
        Mock<IOptionsMonitor<ProjectHistoryOptions>> monitor = new();
        monitor.SetupGet(m => m.CurrentValue).Returns(new ProjectHistoryOptions());
        return new ProjectHistoryIdleCaptureService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), monitor.Object,
            NullLogger<ProjectHistoryIdleCaptureService>.Instance);
    }

    private sealed class Caller(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "test@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool EmailVerified => true;
    }

    /// <summary>Delegates to the real file handler but cancels the request on the first file it is asked to apply.</summary>
    private sealed class CancellingFileHandler(FileRestoreKindHandler inner, CancellationTokenSource cts) : IProjectRestoreKindHandler
    {
        public ProjectVersionItemKind Kind => inner.Kind;

        public Task<ProjectRestoreOutcome> ApplyAsync(
            ProjectRestoreWriteContext context, ProjectVersionItem item, CancellationToken ct)
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }

        public Task PrepareProjectScopeAsync(ProjectRestoreWriteContext context, CancellationToken ct) =>
            inner.PrepareProjectScopeAsync(context, ct);

        public Task CompleteProjectScopeAsync(ProjectRestoreWriteContext context, CancellationToken ct) =>
            inner.CompleteProjectScopeAsync(context, ct);

        public Task<IReadOnlyList<ProjectRestoreOutcome>> RemoveAbsentAsync(
            ProjectRestoreWriteContext context, IReadOnlySet<string> presentKeys, CancellationToken ct) =>
            inner.RemoveAbsentAsync(context, presentKeys, ct);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(CancellationTokenSource? cancelOnFirstFile = null)
        {
            Guid owner = Guid.NewGuid();
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"qa3-{Guid.NewGuid():N}").Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner) };
            Db.Projects.Add(Project);
            Db.SaveChanges();
            Service = new ProjectHistoryService(
                Db, Store, Options.Create(new ProjectHistoryOptions()), NullLogger<ProjectHistoryService>.Instance);
            FileRestoreKindHandler files = new(Db, Store);
            Restore = new ProjectHistoryRestoreService(
                Db, Service, new InProcessProjectRestoreLock(),
                [
                    cancelOnFirstFile is null ? files : new CancellingFileHandler(files, cancelOnFirstFile),
                    new ProjectRestoreKindHandler(Db),
                    new PronunciationRestoreKindHandler(Db)
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

        public void Dispose() => Db.Dispose();
    }
}
