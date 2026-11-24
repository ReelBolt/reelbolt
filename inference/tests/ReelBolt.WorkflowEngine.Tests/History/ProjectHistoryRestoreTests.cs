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
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.History;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>Restore part 1 (contract section 8): File, Pronunciation and Project kinds, guards, controller.</summary>
public class ProjectHistoryRestoreTests
{
    private static readonly IReadOnlyList<ProjectHistoryRestoreItemRef> NoItems = [];

    [Fact]
    public async Task File_round_trip_restores_bytes_and_resets_scan_state()
    {
        using Fixture f = new();
        ProjectFile file = f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);

        f.Overwrite(file, "two");
        file.ModerationStatus = FileModerationStatus.Clean;
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Item,
            [new(ProjectVersionItemKind.File, file.Id.ToString("D"))], f.Project.OwnerId, CancellationToken.None))!;

        result.Restored.Should().ContainSingle(e => e.Key == file.Id.ToString("D"));
        Encoding.UTF8.GetString(f.Store.Objects[file.StorageKey]).Should().Be("one");
        ProjectFile live = await f.Db.ProjectFiles.AsNoTracking().SingleAsync(x => x.Id == file.Id);
        live.ModerationStatus.Should().Be(FileModerationStatus.Unmoderated);
        live.CsamStatus.Should().Be(CsamScanStatus.Pending);
    }

    [Fact]
    public async Task Restoring_a_library_sound_row_clears_its_quota_exemption()
    {
        using Fixture f = new();
        ProjectFile file = f.AddFile("a.txt", "one");
        file.SourceKind = "sfx_library";
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);

        f.Overwrite(file, "two-longer");
        await f.Db.SaveChangesAsync();
        StorageUsageService usage = new(f.Db, Mock.Of<IOrganizationEntitlements>());
        long exempt = await usage.GetUsedBytesAsync(f.Project.OrganizationId, CancellationToken.None);

        await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Item,
            [new(ProjectVersionItemKind.File, file.Id.ToString("D"))], f.Project.OwnerId, CancellationToken.None);

        ProjectFile live = await f.Db.ProjectFiles.AsNoTracking().SingleAsync(x => x.Id == file.Id);
        live.SourceKind.Should().BeNull();
        long used = await usage.GetUsedBytesAsync(f.Project.OrganizationId, CancellationToken.None);
        (used - exempt).Should().Be(live.SizeBytes).And.BeGreaterThan(0);
    }

    [Fact]
    public async Task Restoring_over_an_exempt_library_row_is_charged_the_full_restored_size()
    {
        Mock<IPlanGateService> gate = new();
        List<long> checks = [];
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, long, CancellationToken>((_, n, _) => checks.Add(n))
            .ReturnsAsync(new PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "full", 402));
        using Fixture f = new(planGate: gate.Object);
        ProjectFile file = f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);
        long restoredSize = file.SizeBytes;
        file.SourceKind = "sfx_library";
        f.Overwrite(file, "two-longer-than-before");
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Item,
            [new(ProjectVersionItemKind.File, file.Id.ToString("D"))], f.Project.OwnerId, CancellationToken.None))!;

        checks.Should().Equal([restoredSize]);
        result.Restored.Should().BeEmpty();
    }

    [Fact]
    public async Task Restoring_keeps_a_non_library_source_kind()
    {
        using Fixture f = new();
        ProjectFile file = f.AddFile("a.txt", "one");
        file.SourceKind = "github";
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);
        f.Overwrite(file, "two-longer");
        await f.Db.SaveChangesAsync();

        await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Item,
            [new(ProjectVersionItemKind.File, file.Id.ToString("D"))], f.Project.OwnerId, CancellationToken.None);

        (await f.Db.ProjectFiles.AsNoTracking().SingleAsync(x => x.Id == file.Id)).SourceKind.Should().Be("github");
    }

    [Fact]
    public async Task Deleted_file_is_recreated_with_same_id_and_path()
    {
        using Fixture f = new();
        ProjectFile file = f.AddFile("docs/gone.txt", "bytes");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);
        f.Db.ProjectFiles.Remove(file);
        f.Store.Objects.Remove(file.StorageKey);
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Item,
            [new(ProjectVersionItemKind.File, file.Id.ToString("D"))], f.Project.OwnerId, CancellationToken.None))!;

        result.Restored.Should().ContainSingle();
        ProjectFile back = await f.Db.ProjectFiles.AsNoTracking().SingleAsync(x => x.Id == file.Id);
        back.OriginalPath.Should().Be("docs/gone.txt");
        Encoding.UTF8.GetString(f.Store.Objects[back.StorageKey]).Should().Be("bytes");
    }

    [Fact]
    public async Task Project_scope_removes_added_file_and_restores_project_and_pronunciation()
    {
        using Fixture f = new();
        ProjectFile kept = f.AddFile("kept.txt", "k");
        f.Db.ProjectPronunciationEntries.Add(new ProjectPronunciationEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Term = "SQL",
            Spelling = "sequel",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, kept);

        ProjectFile added = f.AddFile("added.txt", "x");
        f.Project.Name = "Renamed";
        f.Db.ProjectPronunciationEntries.RemoveRange(f.Db.ProjectPronunciationEntries);
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Project, NoItems, f.Project.OwnerId, CancellationToken.None))!;

        result.Removed.Should().ContainSingle(e => e.Key == added.Id.ToString("D"));
        (await f.Db.ProjectFiles.AnyAsync(x => x.Id == added.Id)).Should().BeFalse();
        (await f.Db.ProjectFiles.AnyAsync(x => x.Id == kept.Id)).Should().BeTrue();
        (await f.Db.Projects.AsNoTracking().SingleAsync(p => p.Id == f.Project.Id)).Name.Should().Be("P");
        (await f.Db.ProjectPronunciationEntries.SingleAsync()).Term.Should().Be("SQL");
    }

    [Fact]
    public async Task Safety_version_is_taken_and_restoring_it_undoes_the_restore()
    {
        using Fixture f = new();
        ProjectFile file = f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);
        f.Overwrite(file, "two");
        f.Project.Name = "Two";
        await f.Db.SaveChangesAsync();
        // Stand in for copy-on-change: the safety version's blob of "two" is kept as the decorator would.
        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Project, NoItems, f.Project.OwnerId, CancellationToken.None))!;

        ProjectVersion safety = await f.Db.ProjectVersions.AsNoTracking().SingleAsync(v => v.Id == result.SafetyVersionId);
        safety.Cause.Should().Be(ProjectVersionCause.BeforeRestore);

        (await f.Db.Projects.AsNoTracking().SingleAsync(p => p.Id == f.Project.Id)).Name.Should().Be("P");
        f.KeepBlob(safety, file, "two");
        ProjectHistoryRestoreResult undo = (await f.Restore.RestoreAsync(
            f.Project.Id, safety.Id, ProjectHistoryRestoreScope.Project, NoItems, f.Project.OwnerId, CancellationToken.None))!;

        undo.SafetyVersionId.Should().NotBe(result.SafetyVersionId);
        (await f.Db.Projects.AsNoTracking().SingleAsync(p => p.Id == f.Project.Id)).Name.Should().Be("Two");
        Encoding.UTF8.GetString(f.Store.Objects[file.StorageKey]).Should().Be("two");
    }

    [Fact]
    public async Task Run_active_blocks_project_scope_with_409_and_writes_nothing()
    {
        using Fixture f = new();
        f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.Db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Status = ExecutionStatus.Running
        });
        await f.Db.SaveChangesAsync();
        int versions = await f.Db.ProjectVersions.CountAsync();

        Func<Task> act = () => f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Project, NoItems, f.Project.OwnerId, CancellationToken.None);

        (await act.Should().ThrowAsync<ProjectHistoryRestoreConflictException>())
            .Which.Code.Should().Be("PROJECT_RUN_ACTIVE");
        (await f.Db.ProjectVersions.CountAsync()).Should().Be(versions);
    }

    [Fact]
    public async Task Quarantined_file_is_skipped_and_workflow_kind_is_not_yet_supported()
    {
        using Fixture f = new();
        ProjectFile bad = f.AddFile("bad.txt", "x");
        bad.ModerationStatus = FileModerationStatus.Quarantined;
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        // A quarantined file is not captured, so an item restore naming only it has nothing to restore.
        Func<Task> act = async () => await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Item,
            [new(ProjectVersionItemKind.File, bad.Id.ToString("D"))], f.Project.OwnerId, CancellationToken.None);
        await act.Should().ThrowAsync<ProjectHistoryNothingToRestoreException>();
    }

    [Fact]
    public async Task Preview_writes_nothing_and_reports_changes()
    {
        using Fixture f = new();
        ProjectFile file = f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);
        f.Overwrite(file, "two");
        await f.Db.SaveChangesAsync();
        int versions = await f.Db.ProjectVersions.CountAsync();

        ProjectHistoryRestorePreview preview = (await f.Restore.PreviewAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Project, NoItems, CancellationToken.None))!;

        preview.Changes.Should().Contain(c => c.Key == file.Id.ToString("D"));
        (await f.Db.ProjectVersions.CountAsync()).Should().Be(versions);
        Encoding.UTF8.GetString(f.Store.Objects[file.StorageKey]).Should().Be("two");
    }

    [Fact]
    public async Task Controller_member_not_creator_is_403_on_project_scope_but_ok_on_item_scope()
    {
        using Fixture f = new();
        ProjectFile file = f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);
        Guid member = Guid.NewGuid();

        ActionResult<ProjectHistoryRestoreResponse> project = await f.Controller(member, OrganizationRole.Member, f.Project.OrganizationId).Restore(
            f.Project.Id, v1.Id, new ProjectHistoryRestoreRequest("project", null), CancellationToken.None);
        ActionResult<ProjectHistoryRestoreResponse> item = await f.Controller(member, OrganizationRole.Member, f.Project.OrganizationId).Restore(
            f.Project.Id, v1.Id,
            new ProjectHistoryRestoreRequest("item", [new ProjectHistoryRestoreItemRequest("File", file.Id.ToString("D"))]),
            CancellationToken.None);

        StatusOf(project).Should().Be(403);
        StatusOf(item).Should().Be(200);
    }

    [Fact]
    public async Task Controller_cross_org_is_404_and_bad_body_is_400()
    {
        using Fixture f = new();
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        ActionResult<ProjectHistoryRestoreResponse> other = await f.Controller(Guid.NewGuid(), OrganizationRole.Owner).Restore(
            f.Project.Id, v1.Id, new ProjectHistoryRestoreRequest("project", null), CancellationToken.None);
        ActionResult<ProjectHistoryRestoreResponse> bad = await f.Controller().Restore(
            f.Project.Id, v1.Id, new ProjectHistoryRestoreRequest("item", []), CancellationToken.None);

        StatusOf(other).Should().Be(404);
        StatusOf(bad).Should().Be(400);
    }

    [Fact]
    public async Task Quota_refusal_skips_the_file_with_STORAGE_QUOTA_EXCEEDED()
    {
        Mock<IPlanGateService> gate = new();
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "full", 402));
        using Fixture f = new(planGate: gate.Object);
        ProjectFile file = f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);
        f.Db.ProjectFiles.Remove(file);
        f.Store.Objects.Remove(file.StorageKey);
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Item,
            [new(ProjectVersionItemKind.File, file.Id.ToString("D"))], f.Project.OwnerId,
            CancellationToken.None))!;

        result.Restored.Should().BeEmpty();
        ProjectHistoryRestoreSkipped skipped = result.Skipped
            .Should().ContainSingle(s => s.Key == file.Id.ToString("D")).Subject;
        skipped.Reason.Should().Be(ProjectHistoryRestoreReasons.StorageQuotaExceeded);
        (await f.Db.ProjectFiles.AnyAsync(x => x.Id == file.Id)).Should().BeFalse("nothing was written");
    }

    [Fact]
    public async Task Quarantine_after_capture_skips_the_file_with_its_QUARANTINED_reason()
    {
        using Fixture f = new();
        ProjectFile file = f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);

        file.ModerationStatus = FileModerationStatus.Quarantined;
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Item,
            [new(ProjectVersionItemKind.File, file.Id.ToString("D"))], f.Project.OwnerId,
            CancellationToken.None))!;

        result.Restored.Should().BeEmpty();
        result.Skipped.Should().ContainSingle(s =>
            s.Kind == ProjectVersionItemKind.File
            && s.Key == file.Id.ToString("D")
            && s.Reason == ProjectHistoryRestoreReasons.Quarantined);
    }

    [Fact]
    public async Task Design_system_handler_restores_the_pinned_version_and_skips_when_it_is_current()
    {
        using Fixture f = new();
        Guid versionId = Guid.NewGuid();
        DesignSystem system = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Kind = DesignSystemKinds.Design,
            Name = "Design",
            CreatedByUserId = f.Project.OwnerId
        };
        f.Db.DesignSystems.Add(system);
        f.Db.DesignSystemVersions.Add(new DesignSystemVersion
        {
            Id = versionId,
            DesignSystemId = system.Id,
            VersionNumber = 1,
            State = DesignVersionState.Approved,
            DocumentJson = "{}",
            DocumentDigest = new string('a', 64),
            CreatedByUserId = f.Project.OwnerId
        });
        await f.Db.SaveChangesAsync();

        Mock<IDesignSystemService> designSystems = new();
        designSystems.Setup(s => s.RestoreAsync(
                f.Project.Id, versionId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DesignSystemWriteResult(DesignSystemWriteStatus.Ok));
        DesignSystemRestoreKindHandler handler = new(f.Db, designSystems.Object);
        ProjectVersionItem item = new()
        {
            Id = Guid.NewGuid(),
            VersionId = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Kind = ProjectVersionItemKind.DesignSystem,
            ItemKey = "design-system",
            Name = "Design",
            DesignSystemVersionId = versionId
        };
        ProjectRestoreWriteContext context = new(
            f.Project,
            new ProjectVersion
            {
                Id = item.VersionId,
                ProjectId = f.Project.Id,
                Number = 1,
                Cause = ProjectVersionCause.Named,
                ManifestDigest = new string('a', 64),
                ItemCount = 1
            },
            [item],
            f.Project.OwnerId,
            new ProjectHistoryContentGuard(f.Db, f.Project.Id));

        ProjectRestoreOutcome restored = await handler.ApplyAsync(context, item, CancellationToken.None);
        restored.Disposition.Should().Be(ProjectRestoreDisposition.Restored);
        designSystems.Verify(s => s.RestoreAsync(
            f.Project.Id, versionId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);

        system.CurrentVersionId = versionId;
        await f.Db.SaveChangesAsync();

        ProjectRestoreOutcome current = await handler.ApplyAsync(context, item, CancellationToken.None);
        current.Disposition.Should().Be(ProjectRestoreDisposition.Skipped);
        current.Reason.Should().Be(ProjectHistoryRestoreReasons.AlreadyCurrent);
        designSystems.Verify(s => s.RestoreAsync(
            f.Project.Id, versionId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Controller_answers_409_PROJECT_RUN_ACTIVE()
    {
        using Fixture f = new();
        f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.Db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Status = ExecutionStatus.Running
        });
        await f.Db.SaveChangesAsync();

        ActionResult<ProjectHistoryRestoreResponse> result = await f.Controller().Restore(
            f.Project.Id, v1.Id, new ProjectHistoryRestoreRequest("project", null), CancellationToken.None);

        StatusOf(result).Should().Be(409);
        CodeOf(result).Should().Be(ProjectHistoryRestoreCodes.ProjectRunActive);
    }

    [Fact]
    public async Task Controller_answers_409_WORKFLOW_RUN_ACTIVE()
    {
        using Fixture f = new();
        f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        Guid workflowId = Guid.NewGuid();
        f.Db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = workflowId,
            Name = "W",
            ProjectId = f.Project.Id,
            CreatedVia = WorkflowCreatedVia.User
        });
        await f.Db.SaveChangesAsync();
        ProjectVersion withWorkflow = await f.CaptureAsync();
        f.Db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            WorkflowDefinitionId = workflowId,
            Status = ExecutionStatus.Running
        });
        await f.Db.SaveChangesAsync();

        ActionResult<ProjectHistoryRestoreResponse> result = await f.Controller().Restore(
            f.Project.Id, withWorkflow.Id,
            new ProjectHistoryRestoreRequest(
                "item", [new ProjectHistoryRestoreItemRequest("Workflow", workflowId.ToString("D"))]),
            CancellationToken.None);

        StatusOf(result).Should().Be(409);
        CodeOf(result).Should().Be(ProjectHistoryRestoreCodes.WorkflowRunActive);
    }

    [Fact]
    public async Task Item_restore_naming_nothing_in_the_version_answers_400_before_a_safety_version()
    {
        using Fixture f = new();
        f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        int versionsBefore = await f.Db.ProjectVersions.CountAsync();

        ActionResult<ProjectHistoryRestoreResponse> result = await f.Controller().Restore(
            f.Project.Id, v1.Id,
            new ProjectHistoryRestoreRequest(
                "item", [new ProjectHistoryRestoreItemRequest("File", Guid.NewGuid().ToString("D"))]),
            CancellationToken.None);

        StatusOf(result).Should().Be(400);
        (result.Result as BadRequestObjectResult)!.Value!.GetType().GetProperty("code")!
            .GetValue((result.Result as BadRequestObjectResult)!.Value).Should().Be("NOTHING_TO_RESTORE");
        (await f.Db.ProjectVersions.CountAsync()).Should().Be(versionsBefore);
    }

    [Fact]
    public async Task Controller_answers_409_RESTORE_IN_PROGRESS_while_another_restore_holds_the_lock()
    {
        InProcessProjectRestoreLock restoreLock = new();
        using Fixture f = new(restoreLock: restoreLock);
        f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        await using IAsyncDisposable claim =
            (await restoreLock.TryAcquireAsync(f.Project.Id, CancellationToken.None))!;

        ActionResult<ProjectHistoryRestoreResponse> result = await f.Controller().Restore(
            f.Project.Id, v1.Id, new ProjectHistoryRestoreRequest("project", null), CancellationToken.None);

        StatusOf(result).Should().Be(409);
        CodeOf(result).Should().Be(ProjectHistoryRestoreCodes.RestoreInProgress);
    }

    [Fact]
    public async Task Restore_and_preview_reject_a_bad_body_before_any_access_call()
    {
        using Fixture f = new();
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        Guid stranger = Guid.NewGuid();
        CountingAccess access = new(
            TestTenancy.Access(f.Db, f.BuildUser(stranger, OrganizationRole.Owner)));
        ProjectHistoryController controller = f.Controller(
            userId: stranger, role: OrganizationRole.Owner, access: access);

        ProjectHistoryRestoreRequest[] bodies =
        [
            new(null, null),
            new("bogus", null),
            new("item", []),
            new("item", Enumerable.Range(0, 51)
                .Select(_ => new ProjectHistoryRestoreItemRequest("File", Guid.NewGuid().ToString("D")))
                .ToList()),
            new("item", [new ProjectHistoryRestoreItemRequest("Nope", "key")])
        ];

        foreach (ProjectHistoryRestoreRequest body in bodies)
        {
            StatusOf(await controller.Restore(f.Project.Id, v1.Id, body, CancellationToken.None))
                .Should().Be(400);
            StatusOf(await controller.PreviewRestore(f.Project.Id, v1.Id, body, CancellationToken.None))
                .Should().Be(400);
        }

        access.GetAsyncCalls.Should().Be(0, "the body is checked before any permission decision");
    }

    [Fact]
    public async Task Valid_restore_and_preview_each_make_exactly_one_access_call()
    {
        using Fixture f = new();
        ProjectFile file = f.AddFile("a.txt", "one");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        f.KeepBlob(v1, file);

        Guid member = Guid.NewGuid();
        CountingAccess access = new(TestTenancy.Access(
            f.Db, f.BuildUser(member, OrganizationRole.Member, f.Project.OrganizationId)));
        ProjectHistoryController controller = f.Controller(
            userId: member, role: OrganizationRole.Member,
            orgId: f.Project.OrganizationId, access: access);
        ProjectHistoryRestoreRequest body = new(
            "item", [new ProjectHistoryRestoreItemRequest("File", file.Id.ToString("D"))]);

        StatusOf(await controller.PreviewRestore(f.Project.Id, v1.Id, body, CancellationToken.None))
            .Should().Be(200);
        access.GetAsyncCalls.Should().Be(1);

        StatusOf(await controller.Restore(f.Project.Id, v1.Id, body, CancellationToken.None))
            .Should().Be(200);
        access.GetAsyncCalls.Should().Be(2, "one call per action");
    }

    private static string? CodeOf<T>(ActionResult<T> result)
    {
        if (result.Result is not ConflictObjectResult { Value: { } body })
            return null;

        return body.GetType().GetProperty("code")?.GetValue(body) as string;
    }

    private sealed class CountingAccess(IProjectAccessService inner) : IProjectAccessService
    {
        public int GetAsyncCalls { get; private set; }

        public Task<ProjectAccessResult> GetAsync(
            Guid projectId, ProjectPermission permission, CancellationToken ct)
        {
            GetAsyncCalls++;
            return inner.GetAsync(projectId, permission, ct);
        }

        public ProjectAccessResult Get(Guid projectId, ProjectPermission permission) =>
            inner.Get(projectId, permission);

        public ProjectAccessOutcome AuthorizeOrganization(ProjectPermission permission) =>
            inner.AuthorizeOrganization(permission);
    }

    private static int StatusOf<T>(ActionResult<T> result) =>
        result.Result switch
        {
            ForbidResult => 403,
            Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult s => s.StatusCode ?? 200,
            _ => 200
        };

    private sealed class Fixture : IDisposable
    {
        public Fixture(
            IProjectRestoreLock? restoreLock = null,
            IPlanGateService? planGate = null,
            IDesignSystemService? designSystems = null)
        {
            Guid owner = Guid.NewGuid();
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"project-history-restore-{Guid.NewGuid():N}").Options);
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
            Lock = restoreLock ?? new InProcessProjectRestoreLock();
            Restore = new ProjectHistoryRestoreService(
                Db, Service, Lock,
                [
                    new FileRestoreKindHandler(Db, Store, planGate),
                    new ProjectRestoreKindHandler(Db),
                    new PronunciationRestoreKindHandler(Db),
                    new DesignSystemRestoreKindHandler(Db, designSystems)
                ],
                NullLogger<ProjectHistoryRestoreService>.Instance);
        }

        public InferenceApiDbContext Db { get; }
        public Project Project { get; }
        public HistoryTestStore Store { get; } = new();
        public ProjectHistoryService Service { get; }
        public IProjectRestoreLock Lock { get; }
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

        public void Overwrite(ProjectFile file, string content)
        {
            Store.Put(file.StorageKey, content);
            file.SizeBytes = content.Length;
        }

        /// <summary>Stands in for copy-on-change: keeps the bytes the version captured as its blob.</summary>
        public void KeepBlob(ProjectVersion version, ProjectFile file, string? content = null)
        {
            ProjectVersionItem item = Db.ProjectVersionItems.Single(
                i => i.VersionId == version.Id && i.ItemKey == file.Id.ToString("D"));
            string blobKey = $"projects/{Project.Id}/history/blobs/{Guid.NewGuid():N}";
            Store.Objects[blobKey] = content is null
                ? Store.Objects[file.StorageKey]
                : Encoding.UTF8.GetBytes(content);
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

        public ICurrentUser BuildUser(
            Guid? userId = null, OrganizationRole role = OrganizationRole.Owner, Guid? orgId = null)
        {
            Guid id = userId ?? Project.OwnerId;
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(id);
            user.SetupGet(u => u.Email).Returns("restore@example.com");
            user.SetupGet(u => u.IsAuthenticated).Returns(true);
            user.SetupGet(u => u.OrganizationId).Returns(orgId ?? TestTenancy.OrgOf(id));
            user.SetupGet(u => u.OrgRole).Returns(role);
            return user.Object;
        }

        public ProjectHistoryController Controller(
            Guid? userId = null,
            OrganizationRole role = OrganizationRole.Owner,
            Guid? orgId = null,
            IProjectAccessService? access = null)
        {
            Guid id = userId ?? Project.OwnerId;
            ICurrentUser user = BuildUser(id, role, orgId);
            ProjectHistoryEraser eraser = new(Db, Store, NullLogger<ProjectHistoryEraser>.Instance);
            return new ProjectHistoryController(
                access ?? TestTenancy.Access(Db, user), Service, eraser, user, Restore)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        public void Dispose() => Db.Dispose();
    }
}
