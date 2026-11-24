using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>T-004: manifest diff, per-item diff and the file content download (contract sections 5 and 7).</summary>
public class ProjectHistoryDiffTests
{
    [Fact]
    public async Task Changes_classifies_added_removed_changed_moved_and_counts_unchanged()
    {
        using Fixture f = new();
        ProjectFile same = f.AddFile("same.txt", "s");
        ProjectFile changed = f.AddFile("changed.txt", "one");
        ProjectFile gone = f.AddFile("gone.txt", "g");
        ProjectFile moved = f.AddFile("moved.txt", "m");
        ProjectFile both = f.AddFile("both.txt", "b1");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        f.Db.ProjectFiles.Remove(gone);
        f.Overwrite(changed, "two");
        moved.OriginalPath = "sub/moved.txt";
        f.Overwrite(both, "b2");
        both.OriginalPath = "sub/both.txt";
        ProjectFile added = f.AddFile("added.txt", "a");
        await f.Db.SaveChangesAsync();

        ProjectHistoryChanges result = (await f.Service.ComputeChangesAsync(
            f.Project.Id, v1.Id, null, CancellationToken.None))!;

        string ChangeOf(ProjectFile file) => result.Changes.Single(c => c.Key == file.Id.ToString("D")).Change;
        ChangeOf(added).Should().Be("added");
        ChangeOf(gone).Should().Be("removed");
        ChangeOf(changed).Should().Be("changed");
        ChangeOf(moved).Should().Be("moved");
        ChangeOf(both).Should().Be("changedAndMoved");
        result.Changes.Should().NotContain(c => c.Key == same.Id.ToString("D"));
        result.Changes.Single(c => c.Key == moved.Id.ToString("D")).PreviousName.Should().Be("moved.txt");
        result.UnchangedCount.Should().BeGreaterThanOrEqualTo(1);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Changes_between_two_versions_and_missing_version_is_null()
    {
        using Fixture f = new();
        ProjectFile file = f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.Overwrite(file, "two");
        await f.Db.SaveChangesAsync();
        ProjectVersion v2 = await f.CaptureAsync();

        ProjectHistoryChanges result = (await f.Service.ComputeChangesAsync(
            f.Project.Id, v1.Id, v2.Id, CancellationToken.None))!;
        result.Changes.Should().ContainSingle(c => c.Change == "changed");

        (await f.Service.ComputeChangesAsync(f.Project.Id, Guid.NewGuid(), null, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task Text_diff_reads_the_blob_and_the_live_object()
    {
        using Fixture f = new();
        ProjectFile file = f.AddFile("a.txt", "a\nb\nc\n");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);
        f.Overwrite(file, "a\nB\nc\n");
        await f.Db.SaveChangesAsync();

        ProjectHistoryItemDiff diff = (await f.Service.ComputeItemDiffAsync(
            f.Project.Id, v1.Id, ProjectVersionItemKind.File, file.Id.ToString("D"), null, CancellationToken.None))!;

        diff.Mode.Should().Be("text");
        diff.Hunks.Should().ContainSingle();
        diff.Hunks![0].Lines.Select(l => l.Op + l.Text).Should().Contain(["-b", "+B"]);
    }

    [Fact]
    public async Task Binary_and_too_large_files_report_sizes_only()
    {
        using Fixture f = new();
        ProjectFile bin = f.AddFile("a.bin", "x", mime: "application/octet-stream");
        ProjectFile big = f.AddFile("big.txt", "xx");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, bin);
        f.KeepBlob(v1, big);
        f.Overwrite(bin, "xyz", mime: "application/octet-stream");
        f.Overwrite(big, new string('y', 1024 * 1024 + 10));
        await f.Db.SaveChangesAsync();

        ProjectHistoryItemDiff binary = (await f.Service.ComputeItemDiffAsync(
            f.Project.Id, v1.Id, ProjectVersionItemKind.File, bin.Id.ToString("D"), null, CancellationToken.None))!;
        binary.Mode.Should().Be("binary");
        binary.FromSize.Should().Be(1);
        binary.ToSize.Should().Be(3);

        ProjectHistoryItemDiff large = (await f.Service.ComputeItemDiffAsync(
            f.Project.Id, v1.Id, ProjectVersionItemKind.File, big.Id.ToString("D"), null, CancellationToken.None))!;
        large.Mode.Should().Be("tooLarge");
        large.Hunks.Should().BeNull();
    }

    [Fact]
    public async Task Project_pronunciation_workflow_and_timeline_diffs()
    {
        using Fixture f = new();
        f.Project.Name = "Old";
        f.Db.ProjectPronunciationEntries.Add(new ProjectPronunciationEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Term = "keep",
            Spelling = "k"
        });
        ProjectPronunciationEntry edit = new() { Id = Guid.NewGuid(), ProjectId = f.Project.Id, Term = "edit", Spelling = "e1" };
        ProjectPronunciationEntry drop = new() { Id = Guid.NewGuid(), ProjectId = f.Project.Id, Term = "drop", Spelling = "d" };
        f.Db.ProjectPronunciationEntries.AddRange(edit, drop);
        WorkflowDefinition wf = new() { Id = Guid.NewGuid(), ProjectId = f.Project.Id, Name = "W", Kind = WorkflowDefinitionKind.Standard };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "Narrator",
            AgentType = AgentType.VideoTransform,
            SystemPrompt = string.Empty
        };
        f.Db.AgentDefinitions.Add(agent);
        wf.Steps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = wf.Id,
            AgentDefinitionId = agent.Id,
            StepOrder = 1,
            StepType = StepType.Agent
        });
        f.Db.WorkflowDefinitions.Add(wf);
        EditTimelineEntity tl = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Name = "T",
            Revision = 1,
            DocumentJson = """{"tracks":[{"items":[]}]}"""
        };
        f.Db.EditTimelines.Add(tl);
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        f.Project.Name = "New";
        edit.Spelling = "e2";
        f.Db.ProjectPronunciationEntries.Remove(drop);
        f.Db.ProjectPronunciationEntries.Add(new ProjectPronunciationEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Term = "fresh",
            Spelling = "f"
        });
        f.Db.RemoveRange(wf.Steps.ToList());
        wf.Steps.Clear();
        tl.Revision = 2;
        tl.DocumentJson = """{"tracks":[{"items":[{"type":"videoClip","recordOut":90}]},{"items":[]}]}""";
        await f.Db.SaveChangesAsync();

        async Task<ProjectHistoryItemDiff> Diff(ProjectVersionItemKind kind, string key) =>
            (await f.Service.ComputeItemDiffAsync(f.Project.Id, v1.Id, kind, key, null, CancellationToken.None))!;

        ProjectHistoryItemDiff project = await Diff(ProjectVersionItemKind.Project, "project");
        project.Mode.Should().Be("project");
        project.Project!.Name.Should().Be(new ProjectHistoryFieldChange("Old", "New"));
        project.Project.Description.Should().BeNull();

        ProjectHistoryItemDiff pron = await Diff(ProjectVersionItemKind.Pronunciation, "pronunciation");
        pron.Pronunciation!.Added.Should().Equal("fresh");
        pron.Pronunciation.Removed.Should().Equal("drop");
        pron.Pronunciation.Changed.Should().Equal("edit");

        ProjectHistoryItemDiff workflow = await Diff(ProjectVersionItemKind.Workflow, wf.Id.ToString("D"));
        workflow.Mode.Should().Be("workflow");
        workflow.Workflow!.Removed.Should().ContainSingle();

        ProjectHistoryItemDiff timeline = await Diff(ProjectVersionItemKind.Timeline, tl.Id.ToString("D"));
        timeline.Timeline!.FromRevision.Should().Be(1);
        timeline.Timeline.ToRevision.Should().Be(2);
        timeline.Timeline.ToSummary.Should().Be(new ProjectHistoryTimelineSummary(2, 1, 90));
    }

    [Fact]
    public async Task Content_route_serves_safe_headers_and_refuses_erased_and_quarantined()
    {
        using Fixture f = new();
        ProjectFile file = f.AddFile("note.txt", "hello");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        string key = file.Id.ToString("D");

        ProjectHistoryController controller = f.Controller();
        IActionResult ok = await controller.GetItemContent(f.Project.Id, v1.Id, "File", key, CancellationToken.None);
        ok.Should().BeOfType<FileStreamResult>();
        controller.Response.Headers.ContentDisposition.ToString().Should().StartWith("attachment");
        controller.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");

        (await f.Controller().GetItemContent(f.Project.Id, v1.Id, "Bogus", key, CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        (await f.Controller().GetItemContent(f.Project.Id, v1.Id, "Workflow", key, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();

        file.ModerationStatus = FileModerationStatus.Quarantined;
        await f.Db.SaveChangesAsync();
        (await f.Controller().GetItemContent(f.Project.Id, v1.Id, "File", key, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();

        file.ModerationStatus = FileModerationStatus.Clean;
        f.Db.ProjectVersionItems.Single(i => i.ItemKey == key).ErasedAt = DateTime.UtcNow;
        await f.Db.SaveChangesAsync();
        (await f.Controller().GetItemContent(f.Project.Id, v1.Id, "File", key, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Routes_reject_bad_target_and_kind_and_hide_foreign_projects()
    {
        using Fixture f = new();
        ProjectVersion v1 = await f.CaptureAsync();

        (await f.Controller().GetChanges(f.Project.Id, v1.Id, "nonsense", CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await f.Controller().GetItemDiff(f.Project.Id, v1.Id, "Bogus", "x", null, CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await f.Controller().GetChanges(f.Project.Id, v1.Id, "current", CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>();

        ProjectHistoryController foreign = f.Controller(Guid.NewGuid());
        (await foreign.GetChanges(f.Project.Id, v1.Id, null, CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>();
        (await foreign.GetItemContent(f.Project.Id, v1.Id, "File", "x", CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Guid owner = Guid.NewGuid();
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"project-history-diff-{Guid.NewGuid():N}").Options);
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
        }

        public InferenceApiDbContext Db { get; }
        public Project Project { get; }
        public HistoryTestStore Store { get; } = new();
        public ProjectHistoryService Service { get; }

        public ProjectFile AddFile(string path, string content, string mime = "text/plain")
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
                MimeType = mime,
                SizeBytes = content.Length
            };
            Db.ProjectFiles.Add(file);
            Store.Put(file.StorageKey, content);
            return file;
        }

        public void Overwrite(ProjectFile file, string content, string? mime = null)
        {
            Store.Put(file.StorageKey, content);
            file.SizeBytes = content.Length;
            if (mime is not null) file.MimeType = mime;
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
                Project.Id, ProjectVersionCause.Named, Guid.NewGuid().ToString("N")[..8], null, CancellationToken.None))!;

        public ProjectHistoryController Controller(Guid? userId = null)
        {
            Guid id = userId ?? Project.OwnerId;
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(id);
            user.SetupGet(u => u.Email).Returns("history@example.com");
            user.SetupGet(u => u.IsAuthenticated).Returns(true);
            user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(id));
            user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Owner);
            ProjectHistoryEraser eraser = new(Db, Store, NullLogger<ProjectHistoryEraser>.Instance);
            return new ProjectHistoryController(TestTenancy.Access(Db, user.Object), Service, eraser, user.Object, null!)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        public void Dispose() => Db.Dispose();
    }
}
