using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>
/// T-011: every history path that serves a file's bytes honours the CSAM output hold and the
/// content-quarantine marker for the live file and the historical version alike.
/// </summary>
public class ProjectHistoryContentGuardTests
{
    [Fact]
    public async Task Held_live_output_is_unavailable_for_diff_content_restore_and_restorable()
    {
        using Fixture f = new();
        DateTime now = DateTime.UtcNow;
        f.Project.CsamOutputHoldAt = now.AddHours(-2);
        ProjectFile file = f.AddFile("a.txt", "one", outputProducedAt: now.AddHours(-4));
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);
        f.Overwrite(file, "two");
        await f.Db.SaveChangesAsync();

        string key = file.Id.ToString("D");
        ProjectHistoryItemDiff diff = (await f.Service.ComputeItemDiffAsync(
            f.Project.Id, v1.Id, ProjectVersionItemKind.File, key, null, CancellationToken.None))!;
        diff.Mode.Should().Be("unavailable");
        diff.Hunks.Should().BeNull();

        (await f.Service.GetFileContentAsync(
            f.Project.Id, v1.Id, ProjectVersionItemKind.File, key, CancellationToken.None))
            .Should().BeNull();

        ProjectHistoryRestoreResult restored = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Item,
            [new ProjectHistoryRestoreItemRef(ProjectVersionItemKind.File, key)],
            userId: null, CancellationToken.None))!;
        restored.Skipped.Should().ContainSingle(s => s.Key == key)
            .Which.Reason.Should().Be(ProjectHistoryRestoreReasons.OutputHeld);

        ProjectHistoryVersionDetail detail = (await f.Service.GetVersionAsync(
            f.Project.Id, v1.Id, CancellationToken.None))!;
        detail.Items.Single(i => i.Key == key).Restorable.Should().BeFalse();
        detail.Items.Single(i => i.Key == key).NotRestorableReason
            .Should().Be(ProjectHistoryRestoreReasons.OutputHeld);
    }

    [Fact]
    public async Task A_users_own_upload_stays_available_under_the_output_hold()
    {
        // OutputGate's rule: the hold covers platform-made files; a user's own upload stays available.
        using Fixture f = new();
        DateTime now = DateTime.UtcNow;
        f.Project.CsamOutputHoldAt = now.AddHours(-2);
        ProjectFile file = f.AddFile("notes.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);
        f.Overwrite(file, "two");
        await f.Db.SaveChangesAsync();

        string key = file.Id.ToString("D");
        ProjectHistoryItemDiff diff = (await f.Service.ComputeItemDiffAsync(
            f.Project.Id, v1.Id, ProjectVersionItemKind.File, key, null, CancellationToken.None))!;
        diff.Mode.Should().NotBe("unavailable");

        ProjectHistoryVersionDetail detail = (await f.Service.GetVersionAsync(
            f.Project.Id, v1.Id, CancellationToken.None))!;
        detail.Items.Single(i => i.Key == key).Restorable.Should().BeTrue();
    }

    [Fact]
    public async Task Version_captured_before_the_hold_is_unavailable_everywhere()
    {
        using Fixture f = new();
        // A marker in the future makes every version captured now predate it.
        f.Project.CsamOutputHoldAt = DateTime.UtcNow.AddDays(1);
        ProjectFile file = f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);
        f.Overwrite(file, "two");
        await f.Db.SaveChangesAsync();

        string key = file.Id.ToString("D");
        ProjectHistoryItemDiff diff = (await f.Service.ComputeItemDiffAsync(
            f.Project.Id, v1.Id, ProjectVersionItemKind.File, key, null, CancellationToken.None))!;
        diff.Mode.Should().Be("unavailable");
        diff.Hunks.Should().BeNull();

        (await f.Service.GetFileContentAsync(
            f.Project.Id, v1.Id, ProjectVersionItemKind.File, key, CancellationToken.None))
            .Should().BeNull();

        ProjectHistoryRestoreResult restored = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Item,
            [new ProjectHistoryRestoreItemRef(ProjectVersionItemKind.File, key)],
            userId: null, CancellationToken.None))!;
        restored.Skipped.Should().ContainSingle(s => s.Key == key)
            .Which.Reason.Should().Be(ProjectHistoryRestoreReasons.QuarantineMarker);

        ProjectHistoryVersionDetail detail = (await f.Service.GetVersionAsync(
            f.Project.Id, v1.Id, CancellationToken.None))!;
        detail.Items.Single(i => i.Key == key).Restorable.Should().BeFalse();
        detail.Items.Single(i => i.Key == key).NotRestorableReason
            .Should().Be(ProjectHistoryRestoreReasons.QuarantineMarker);
    }

    [Fact]
    public async Task Content_quarantine_marker_on_the_live_output_holds()
    {
        using Fixture f = new();
        DateTime now = DateTime.UtcNow;
        f.Project.ContentQuarantinedAt = now.AddHours(-2);
        ProjectFile file = f.AddFile("a.txt", "one", outputProducedAt: now.AddHours(-4));
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);
        f.Overwrite(file, "two");
        await f.Db.SaveChangesAsync();

        string key = file.Id.ToString("D");
        ProjectHistoryItemDiff diff = (await f.Service.ComputeItemDiffAsync(
            f.Project.Id, v1.Id, ProjectVersionItemKind.File, key, null, CancellationToken.None))!;
        diff.Mode.Should().Be("unavailable");

        (await f.Service.GetFileContentAsync(
            f.Project.Id, v1.Id, ProjectVersionItemKind.File, key, CancellationToken.None))
            .Should().BeNull();

        ProjectHistoryVersionDetail detail = (await f.Service.GetVersionAsync(
            f.Project.Id, v1.Id, CancellationToken.None))!;
        detail.Items.Single(i => i.Key == key).NotRestorableReason
            .Should().Be(ProjectHistoryRestoreReasons.OutputHeld);
    }

    [Fact]
    public async Task Failed_hold_read_holds_and_a_missing_live_row_judges_by_version_and_hold()
    {
        Guid projectId = Guid.NewGuid();
        using InferenceApiDbContext db = NewDb();
        ProjectHistoryContentGuard healthy = new(db, projectId);
        (await healthy.RefusalAsync(project: null, DateTime.UtcNow, live: null, CancellationToken.None))
            .Should().BeNull();

        // A disposed context makes the marker read throw; the helper then reports Failed, which holds.
        InferenceApiDbContext brokenDb = NewDb();
        brokenDb.Dispose();
        ProjectHistoryContentGuard broken = new(brokenDb, projectId);
        (await broken.RefusalAsync(project: null, DateTime.UtcNow, live: null, CancellationToken.None))
            .Should().Be(ProjectHistoryRestoreReasons.QuarantineMarker);

        // A historical item whose file row is gone is judged by the version time and the hold alone.
        Project held = new() { Id = Guid.NewGuid(), CsamOutputHoldAt = DateTime.UtcNow.AddDays(1) };
        (await new ProjectHistoryContentGuard(db, held.Id)
                .RefusalAsync(held, DateTime.UtcNow, live: null, CancellationToken.None))
            .Should().Be(ProjectHistoryRestoreReasons.QuarantineMarker);

        Project clean = new() { Id = Guid.NewGuid() };
        (await new ProjectHistoryContentGuard(db, clean.Id)
                .RefusalAsync(clean, DateTime.UtcNow, live: null, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_historical_platform_made_item_is_judged_by_its_own_stamp_when_the_live_row_is_gone()
    {
        using InferenceApiDbContext db = NewDb();
        DateTime now = DateTime.UtcNow;
        Project held = new() { Id = Guid.NewGuid(), CsamOutputHoldAt = now.AddHours(-2) };
        Project contentMarked = new() { Id = Guid.NewGuid(), ContentQuarantinedAt = now.AddHours(-2) };
        Project clean = new() { Id = Guid.NewGuid() };
        db.Projects.AddRange(held, contentMarked, clean);
        await db.SaveChangesAsync();

        // The version is captured after the marker (so the capture-time rule does not answer); the item's
        // own production stamp alone decides, with no live row to judge.
        (await new ProjectHistoryContentGuard(db, held.Id).RefusalAsync(
                held, now.AddHours(-1), live: null, CancellationToken.None,
                platformMade: true, outputProducedAt: now.AddHours(-4)))
            .Should().Be(ProjectHistoryRestoreReasons.OutputHeld,
                "a held output stays held through its historical copy after its live row is gone");

        (await new ProjectHistoryContentGuard(db, held.Id).RefusalAsync(
                held, now.AddHours(-1), live: null, CancellationToken.None,
                platformMade: true, outputProducedAt: now.AddMinutes(-1)))
            .Should().BeNull("an output produced after the marker is not held");

        (await new ProjectHistoryContentGuard(db, contentMarked.Id).RefusalAsync(
                contentMarked, now.AddHours(-1), live: null, CancellationToken.None,
                platformMade: true, outputProducedAt: now.AddHours(-4)))
            .Should().Be(ProjectHistoryRestoreReasons.OutputHeld,
                "the content-quarantine marker is judged on the item's production stamp");

        (await new ProjectHistoryContentGuard(db, held.Id).RefusalAsync(
                held, now.AddHours(-1), live: null, CancellationToken.None,
                platformMade: false, outputProducedAt: now.AddHours(-4)))
            .Should().BeNull("a user's own upload is not held by the output stamp");
    }

    [Fact]
    public async Task A_held_output_survives_a_project_restore_and_stays_refused_through_the_safety_version()
    {
        // The verdict's chain: hold set; project restore of a version lacking held output F; the safety
        // version holds F; its content, diff and restore must all refuse F, and F must never be deleted.
        using Fixture f = new();
        DateTime now = DateTime.UtcNow;
        f.Project.CsamOutputHoldAt = now.AddHours(-2);
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        ProjectFile held = f.AddFile("out.mp4", "bytes", outputProducedAt: now.AddHours(-4));
        await f.Db.SaveChangesAsync();
        string key = held.Id.ToString("D");

        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Project, [], userId: null, CancellationToken.None))!;

        result.Removed.Should().NotContain(e => e.Key == key);
        result.Skipped.Should().ContainSingle(s => s.Key == key)
            .Which.Reason.Should().Be(ProjectHistoryRestoreReasons.OutputHeld);
        f.Db.ChangeTracker.Clear();
        (await f.Db.ProjectFiles.AnyAsync(x => x.Id == held.Id)).Should().BeTrue("a held file is never deleted");

        ProjectVersion safety = await f.Db.ProjectVersions.SingleAsync(v => v.Id == result.SafetyVersionId);
        ProjectFile live = await f.Db.ProjectFiles.SingleAsync(x => x.Id == held.Id);
        f.KeepBlob(safety, live);

        (await f.Service.GetFileContentAsync(
            f.Project.Id, safety.Id, ProjectVersionItemKind.File, key, CancellationToken.None)).Should().BeNull();
        (await f.Service.ComputeItemDiffAsync(
            f.Project.Id, safety.Id, ProjectVersionItemKind.File, key, null, CancellationToken.None))!
            .Mode.Should().Be("unavailable");

        // Even with the live row gone, the item's own stamp keeps it refused.
        f.Db.ProjectFiles.Remove(live);
        await f.Db.SaveChangesAsync();
        (await f.Service.GetFileContentAsync(
            f.Project.Id, safety.Id, ProjectVersionItemKind.File, key, CancellationToken.None)).Should().BeNull();
        ProjectHistoryRestoreResult again = (await f.Restore.RestoreAsync(
            f.Project.Id, safety.Id, ProjectHistoryRestoreScope.Item,
            [new ProjectHistoryRestoreItemRef(ProjectVersionItemKind.File, key)],
            userId: null, CancellationToken.None))!;
        again.Skipped.Should().ContainSingle(s => s.Key == key)
            .Which.Reason.Should().Be(ProjectHistoryRestoreReasons.OutputHeld);
        (await f.Db.ProjectFiles.AnyAsync(x => x.Id == held.Id)).Should().BeFalse("nothing was recreated");
    }

    private static InferenceApiDbContext NewDb() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"project-history-guard-{Guid.NewGuid():N}").Options);

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Guid owner = Guid.NewGuid();
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"project-history-guard-{Guid.NewGuid():N}").Options);
            Project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "P",
                OwnerId = owner,
                OrganizationId = TestTenancy.OrgOf(owner)
            };
            Db.Projects.Add(Project);
            Db.SaveChanges();
            Service = new ProjectHistoryService(
                Db, Store, Options.Create(new ProjectHistoryOptions()), NullLogger<ProjectHistoryService>.Instance);
            Restore = new ProjectHistoryRestoreService(
                Db, Service, InProcessProjectRestoreLock.Shared,
                [new FileRestoreKindHandler(Db, Store)],
                NullLogger<ProjectHistoryRestoreService>.Instance);
        }

        public InferenceApiDbContext Db { get; }
        public Project Project { get; }
        public HistoryTestStore Store { get; } = new();
        public ProjectHistoryService Service { get; }
        public ProjectHistoryRestoreService Restore { get; }

        public ProjectFile AddFile(string path, string content, DateTime? outputProducedAt = null)
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
                SizeBytes = content.Length,
                OutputProducedAt = outputProducedAt
            };
            Db.ProjectFiles.Add(file);
            Store.Put(file.StorageKey, content);
            return file;
        }

        public void Overwrite(ProjectFile file, string content)
        {
            Store.Put(file.StorageKey, content);
            file.SizeBytes = content.Length;
        }

        /// <summary>Stands in for copy-on-change: keeps the captured bytes as the version's blob.</summary>
        public void KeepBlob(ProjectVersion version, ProjectFile file)
        {
            ProjectVersionItem item = Db.ProjectVersionItems.Single(
                i => i.VersionId == version.Id && i.ItemKey == file.Id.ToString("D"));
            string blobKey = $"projects/{Project.Id}/history/blobs/{Guid.NewGuid():N}";
            Store.Objects[blobKey] = Store.Objects[file.StorageKey];
            Db.ProjectHistoryBlobs.Add(new ProjectHistoryBlob
            {
                ProjectId = Project.Id,
                ContentId = item.ContentId!,
                StorageKey = blobKey,
                SizeBytes = file.SizeBytes
            });
            Db.SaveChanges();
        }

        public async Task<ProjectVersion> CaptureAsync() =>
            (await Service.CaptureAsync(
                Project.Id, ProjectVersionCause.Named, Guid.NewGuid().ToString("N")[..8], null,
                CancellationToken.None))!;

        public void Dispose() => Db.Dispose();
    }
}
