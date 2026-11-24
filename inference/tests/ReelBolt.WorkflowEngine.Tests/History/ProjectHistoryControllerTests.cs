using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>
/// T-003: the layer-1 project-history REST surface (contract section 5). Every action authorizes
/// the project exactly once; foreign/missing is the same 404 and a low role is 403. Restorability is
/// computed in the service per the section 5 codes.
/// </summary>
public class ProjectHistoryControllerTests
{
    // ── list ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListVersions_NewestFirstWithHistoryBytesAndRetention()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        SeedVersion(db, project, number: 1, ExpiresAt: null);
        SeedVersion(db, project, number: 2, ExpiresAt: null);
        db.ProjectHistoryBlobs.Add(new ProjectHistoryBlob
        {
            ProjectId = project.Id,
            ContentId = "sha256:" + new string('a', 64),
            StorageKey = "projects/x/history/blobs/a",
            SizeBytes = 100
        });
        db.ProjectHistoryDocuments.Add(new ProjectHistoryDocument
        {
            ProjectId = project.Id,
            Sha256 = new string('b', 64),
            Kind = ProjectVersionItemKind.Project,
            Json = "{}",
            SizeBytes = 25
        });
        await db.SaveChangesAsync();

        ActionResult<ProjectHistoryVersionsResponse> result =
            await Controller(db, new HistoryTestStore(), project.OwnerId, OrganizationRole.Owner)
                .ListVersions(project.Id, null, null, CancellationToken.None);

        ProjectHistoryVersionsResponse body = result.Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<ProjectHistoryVersionsResponse>().Subject;
        body.Versions.Select(v => v.Number).Should().Equal([2, 1]);
        body.HistoryBytes.Should().Be(125, "blobs and documents count against the quota");
        body.RetentionDays.Should().Be(new ProjectHistoryOptions().AutoRetentionDays);
    }

    [Fact]
    public async Task ListVersions_BeforePagesBackwardsAndExpiredRowsAreHidden()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        SeedVersion(db, project, number: 1, ExpiresAt: null);
        SeedVersion(db, project, number: 2, ExpiresAt: null);
        SeedVersion(db, project, number: 3, ExpiresAt: DateTime.UtcNow.AddMinutes(-1));
        await db.SaveChangesAsync();

        ActionResult<ProjectHistoryVersionsResponse> result =
            await Controller(db, new HistoryTestStore(), project.OwnerId, OrganizationRole.Owner)
                .ListVersions(project.Id, 50, 2, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ProjectHistoryVersionsResponse>().Subject
            .Versions.Select(v => v.Number).Should().Equal(1);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, false, true, true)]
    [InlineData(OrganizationRole.Admin, false, true, true)]
    [InlineData(OrganizationRole.Member, true, true, true)]
    [InlineData(OrganizationRole.Member, false, true, false)]
    public async Task ListVersions_ReportsCanEditAndCanDeleteFromTheAccessMatrix(
        OrganizationRole role, bool callerIsCreator, bool canEdit, bool canDelete)
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        SeedVersion(db, project, number: 1, ExpiresAt: null);
        await db.SaveChangesAsync();
        Guid userId = callerIsCreator ? project.OwnerId : Guid.NewGuid();

        ProjectHistoryVersionsResponse body =
            (await Controller(db, new HistoryTestStore(), userId, role, project.OrganizationId)
                .ListVersions(project.Id, null, null, CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ProjectHistoryVersionsResponse>().Subject;

        body.CanEdit.Should().Be(canEdit);
        body.CanDelete.Should().Be(canDelete);
    }

    // ── create ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVersion_Returns201NamedWithCreatedByName()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = project.OwnerId,
            Email = "ada@example.com",
            DisplayName = "Ada"
        });
        await db.SaveChangesAsync();
        HistoryTestStore store = new();
        ProjectHistoryController controller =
            Controller(db, store, project.OwnerId, OrganizationRole.Owner);

        ActionResult<ProjectHistoryVersionResponse> result = await controller.CreateVersion(
            project.Id, new CreateProjectHistoryVersionRequest("Cut 1"), CancellationToken.None);

        ProjectHistoryVersionResponse body = result.Result.Should().BeOfType<ObjectResult>()
            .Subject.Value.Should().BeOfType<ProjectHistoryVersionResponse>().Subject;
        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
        body.Name.Should().Be("Cut 1");
        body.Cause.Should().Be(ProjectVersionCause.Named);
        body.ExpiresAt.Should().BeNull("a named version is kept until deleted");
        body.CreatedByName.Should().Be("Ada");

        ProjectVersion stored = await db.ProjectVersions.AsNoTracking().SingleAsync();
        stored.Cause.Should().Be(ProjectVersionCause.Named);
        stored.Name.Should().Be("Cut 1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0001name")]
    [InlineData("bad​name")]
    public async Task CreateVersion_InvalidNameIs400(string name)
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        await db.SaveChangesAsync();

        ActionResult<ProjectHistoryVersionResponse> result =
            await Controller(db, new HistoryTestStore(), project.OwnerId, OrganizationRole.Owner)
                .CreateVersion(project.Id, new CreateProjectHistoryVersionRequest(name), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.ProjectVersions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateVersion_OverTheNamedCapIs409()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        SeedVersion(db, project, number: 1, ExpiresAt: null, name: "Taken");
        await db.SaveChangesAsync();
        ProjectHistoryOptions options = new() { MaxNamedVersions = 1 };

        ActionResult<ProjectHistoryVersionResponse> result =
            await Controller(db, new HistoryTestStore(), project.OwnerId, OrganizationRole.Owner, options: options)
                .CreateVersion(project.Id, new CreateProjectHistoryVersionRequest("Another"), CancellationToken.None);

        ConflictObjectResult conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(conflict.Value).Should().Contain(ProjectHistoryErrorCodes.NamedVersionLimit);
    }

    [Fact]
    public async Task CreateVersion_OverTheFileCapIs413()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        HistoryTestStore store = new();
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Category = "userFiles",
            OriginalFileName = "a.txt",
            StorageKey = $"projects/{project.Id}/userFiles/a.txt",
            StorageBucket = "t",
            MimeType = "text/plain",
            SizeBytes = 2
        };
        db.ProjectFiles.Add(file);
        store.Put(file.StorageKey, "v1");
        await db.SaveChangesAsync();
        ProjectHistoryOptions options = new() { MaxFilesPerVersion = 0 };

        ActionResult<ProjectHistoryVersionResponse> result =
            await Controller(db, store, project.OwnerId, OrganizationRole.Owner, options: options)
                .CreateVersion(project.Id, new CreateProjectHistoryVersionRequest("Too much"), CancellationToken.None);

        ObjectResult tooLarge = result.Result.Should().BeOfType<ObjectResult>().Subject;
        tooLarge.StatusCode.Should().Be(413);
        JsonSerializer.Serialize(tooLarge.Value).Should().Contain(ProjectHistoryErrorCodes.ProjectTooLargeToVersion);
    }

    // ── get ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetVersion_ReturnsItemsWithRestorableVerdict()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        HistoryTestStore store = new();
        (_, ProjectVersion version) = SeedFileVersion(db, store, project);
        db.ProjectHistoryDocuments.Add(new ProjectHistoryDocument
        {
            ProjectId = project.Id,
            Sha256 = new string('c', 64),
            Kind = ProjectVersionItemKind.Project,
            Json = "{}",
            SizeBytes = 2
        });
        db.ProjectVersionItems.Add(new ProjectVersionItem
        {
            Id = Guid.NewGuid(),
            VersionId = version.Id,
            ProjectId = project.Id,
            Kind = ProjectVersionItemKind.Project,
            ItemKey = "project",
            Name = "P",
            DocumentSha256 = new string('c', 64)
        });
        db.ProjectVersionItems.Add(new ProjectVersionItem
        {
            Id = Guid.NewGuid(),
            VersionId = version.Id,
            ProjectId = project.Id,
            Kind = ProjectVersionItemKind.DesignSystem,
            ItemKey = "design-system",
            Name = "P",
            DesignSystemVersionId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();

        ActionResult<ProjectHistoryVersionDetailResponse> result =
            await Controller(db, store, project.OwnerId, OrganizationRole.Owner)
                .GetVersion(project.Id, version.Id, CancellationToken.None);

        ProjectHistoryVersionDetailResponse body = result.Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<ProjectHistoryVersionDetailResponse>().Subject;
        body.Items.Should().HaveCount(3);
        ProjectHistoryItemResponse fileItem =
            body.Items.Single(i => i.Kind == nameof(ProjectVersionItemKind.File));
        fileItem.Restorable.Should().BeTrue();
        fileItem.NotRestorableReason.Should().BeNull();
        body.Items.Single(i => i.Kind == nameof(ProjectVersionItemKind.Project))
            .Restorable.Should().BeTrue();
        ProjectHistoryItemResponse designItem =
            body.Items.Single(i => i.Kind == nameof(ProjectVersionItemKind.DesignSystem));
        designItem.Restorable.Should().BeFalse();
        designItem.NotRestorableReason.Should().Be(ProjectHistoryRestoreReasons.DesignVersionGone);
    }

    [Fact]
    public async Task GetVersion_ContentGoneWhenLiveBytesDifferWithoutABlob()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        HistoryTestStore store = new();
        (ProjectFile file, ProjectVersion version) = SeedFileVersion(db, store, project);
        await db.SaveChangesAsync();
        store.Put(file.StorageKey, "now different bytes");

        ActionResult<ProjectHistoryVersionDetailResponse> result =
            await Controller(db, store, project.OwnerId, OrganizationRole.Owner)
                .GetVersion(project.Id, version.Id, CancellationToken.None);

        Item(result).NotRestorableReason.Should().Be(ProjectHistoryRestoreReasons.ContentGone);
    }

    [Fact]
    public async Task GetVersion_BlobMakesAnOverwrittenFileRestorable()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        HistoryTestStore store = new();
        (ProjectFile file, ProjectVersion version) = SeedFileVersion(db, store, project);
        await db.SaveChangesAsync();
        string contentId = (await db.ProjectVersionItems.AsNoTracking().SingleAsync()).ContentId!;
        db.ProjectHistoryBlobs.Add(new ProjectHistoryBlob
        {
            ProjectId = project.Id,
            ContentId = contentId,
            StorageKey = "projects/x/history/blobs/a",
            SizeBytes = 2
        });
        await db.SaveChangesAsync();
        store.Put(file.StorageKey, "now different bytes");

        ActionResult<ProjectHistoryVersionDetailResponse> result =
            await Controller(db, store, project.OwnerId, OrganizationRole.Owner)
                .GetVersion(project.Id, version.Id, CancellationToken.None);

        Item(result).Restorable.Should().BeTrue("the protected copy is the captured bytes");
    }

    [Fact]
    public async Task GetVersion_ErasedFileIsNotRestorable()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        HistoryTestStore store = new();
        (_, ProjectVersion version) = SeedFileVersion(db, store, project);
        await db.SaveChangesAsync();
        ProjectVersionItem item = await db.ProjectVersionItems.SingleAsync();
        item.ErasedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        ActionResult<ProjectHistoryVersionDetailResponse> result =
            await Controller(db, store, project.OwnerId, OrganizationRole.Owner)
                .GetVersion(project.Id, version.Id, CancellationToken.None);

        Item(result).NotRestorableReason.Should().Be(ProjectHistoryRestoreReasons.Erased);
    }

    [Fact]
    public async Task GetVersion_VersionAtOrBeforeTheQuarantineMarkerIsNotRestorable()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        HistoryTestStore store = new();
        (_, ProjectVersion version) = SeedFileVersion(db, store, project);
        version.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
        project.ContentQuarantinedAt = DateTime.UtcNow.AddMinutes(-9);
        await db.SaveChangesAsync();

        ActionResult<ProjectHistoryVersionDetailResponse> result =
            await Controller(db, store, project.OwnerId, OrganizationRole.Owner)
                .GetVersion(project.Id, version.Id, CancellationToken.None);

        Item(result).NotRestorableReason.Should().Be(ProjectHistoryRestoreReasons.QuarantineMarker);
    }

    [Fact]
    public async Task GetVersion_QuarantinedLiveFileIsNotRestorable()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        HistoryTestStore store = new();
        (ProjectFile file, ProjectVersion version) = SeedFileVersion(db, store, project);
        file.ModerationStatus = FileModerationStatus.Quarantined;
        await db.SaveChangesAsync();

        ActionResult<ProjectHistoryVersionDetailResponse> result =
            await Controller(db, store, project.OwnerId, OrganizationRole.Owner)
                .GetVersion(project.Id, version.Id, CancellationToken.None);

        Item(result).NotRestorableReason.Should().Be(ProjectHistoryRestoreReasons.Quarantined);
    }

    [Fact]
    public async Task GetVersion_UnverifiedItemIsNotRestorable()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        HistoryTestStore store = new();
        Guid fileId = Guid.NewGuid();
        (_, ProjectVersion version) = SeedFileVersion(
            db, store, project, contentId: ContentId.Unverified(fileId));
        await db.SaveChangesAsync();

        ActionResult<ProjectHistoryVersionDetailResponse> result =
            await Controller(db, store, project.OwnerId, OrganizationRole.Owner)
                .GetVersion(project.Id, version.Id, CancellationToken.None);

        Item(result).NotRestorableReason.Should().Be(ProjectHistoryRestoreReasons.Unverified);
    }

    // ── rename / un-name ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RenameVersion_NamesThenUnnamesBackToAutomatic()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        ProjectVersion version = SeedVersion(db, project, number: 1, ExpiresAt: DateTime.UtcNow.AddDays(5));
        await db.SaveChangesAsync();
        ProjectHistoryController controller =
            Controller(db, new HistoryTestStore(), project.OwnerId, OrganizationRole.Owner);

        ActionResult<ProjectHistoryVersionResponse> named = await controller.RenameVersion(
            project.Id, version.Id, new RenameProjectHistoryVersionRequest("Keep me"), CancellationToken.None);
        ProjectHistoryVersionResponse namedBody = named.Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<ProjectHistoryVersionResponse>().Subject;
        namedBody.Name.Should().Be("Keep me");
        namedBody.ExpiresAt.Should().BeNull();

        DateTime before = DateTime.UtcNow;
        ActionResult<ProjectHistoryVersionResponse> unnamed = await controller.RenameVersion(
            project.Id, version.Id, new RenameProjectHistoryVersionRequest(""), CancellationToken.None);
        ProjectHistoryVersionResponse unnamedBody = unnamed.Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<ProjectHistoryVersionResponse>().Subject;
        unnamedBody.Name.Should().BeNull();
        unnamedBody.Cause.Should().Be(ProjectVersionCause.Auto, "un-naming keeps the cause");
        unnamedBody.ExpiresAt.Should().NotBeNull();
        unnamedBody.ExpiresAt!.Value.Should().BeAfter(before);
    }

    [Fact]
    public async Task RenameVersion_NamingPastTheCapIs409()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        SeedVersion(db, project, number: 1, ExpiresAt: null, name: "Taken");
        ProjectVersion auto = SeedVersion(db, project, number: 2, ExpiresAt: DateTime.UtcNow.AddDays(5));
        await db.SaveChangesAsync();

        ActionResult<ProjectHistoryVersionResponse> result =
            await Controller(db, new HistoryTestStore(), project.OwnerId, OrganizationRole.Owner,
                    options: new ProjectHistoryOptions { MaxNamedVersions = 1 })
                .RenameVersion(project.Id, auto.Id, new RenameProjectHistoryVersionRequest("Nope"), CancellationToken.None);

        ConflictObjectResult conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(conflict.Value).Should().Contain(ProjectHistoryErrorCodes.NamedVersionLimit);
    }

    // ── delete version / erase file ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteVersion_204Then404()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        ProjectVersion version = SeedVersion(db, project, number: 1, ExpiresAt: null);
        await db.SaveChangesAsync();
        ProjectHistoryController controller =
            Controller(db, new HistoryTestStore(), project.OwnerId, OrganizationRole.Owner);

        (await controller.DeleteVersion(project.Id, version.Id, CancellationToken.None))
            .Should().BeOfType<NoContentResult>();
        (await db.ProjectVersions.CountAsync()).Should().Be(0);

        (await controller.DeleteVersion(project.Id, version.Id, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task DeleteVersion_OtherProjectsVersionIs404()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        Project other = SeedProject(db);
        ProjectVersion foreign = SeedVersion(db, other, number: 1, ExpiresAt: null);
        await db.SaveChangesAsync();

        IActionResult result = await Controller(db, new HistoryTestStore(), project.OwnerId, OrganizationRole.Owner)
            .DeleteVersion(project.Id, foreign.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        (await db.ProjectVersions.CountAsync()).Should().Be(1, "nothing was deleted");
    }

    [Fact]
    public async Task DeleteVersion_409OutputHeldWhenTheHoldIsSetAndItHoldsAPlatformMadeItem()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        HistoryTestStore store = new();
        (_, ProjectVersion version) = SeedFileVersion(db, store, project);
        project.CsamOutputHoldAt = DateTime.UtcNow;
        ProjectVersionItem item = db.ProjectVersionItems.Local.Single(i => i.VersionId == version.Id);
        item.PlatformMade = true;
        item.OutputProducedAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        ProjectHistoryController controller =
            Controller(db, store, project.OwnerId, OrganizationRole.Owner);

        IActionResult result = await controller.DeleteVersion(project.Id, version.Id, CancellationToken.None);

        ConflictObjectResult conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(conflict.Value).Should().Contain(ProjectHistoryErrorCodes.OutputHeld);
        (await db.ProjectVersions.CountAsync()).Should().Be(1, "the held version was not deleted");
    }

    [Fact]
    public async Task EraseFile_409OutputHeldWhenTheHoldIsSetAndTheFileIsPlatformMade()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        HistoryTestStore store = new();
        (ProjectFile file, ProjectVersion version) = SeedFileVersion(db, store, project);
        project.CsamOutputHoldAt = DateTime.UtcNow;
        ProjectVersionItem item = db.ProjectVersionItems.Local.Single(i => i.VersionId == version.Id);
        item.PlatformMade = true;
        item.OutputProducedAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        ProjectHistoryController controller =
            Controller(db, store, project.OwnerId, OrganizationRole.Owner);

        IActionResult result = await controller.EraseFile(project.Id, file.Id, CancellationToken.None);

        ConflictObjectResult conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(conflict.Value).Should().Contain(ProjectHistoryErrorCodes.OutputHeld);
        (await db.ProjectVersionItems.AsNoTracking().SingleAsync()).ErasedAt.Should().BeNull(
            "a held platform-made file is not erased from history");
    }

    [Fact]
    public async Task EraseFile_409OutputHeldWhenEveryContentToEraseIsHeldUnderAnotherKey()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        HistoryTestStore store = new();
        (ProjectFile file, _) = SeedFileVersion(db, store, project, contentId: "etag:shared");

        // Another file, under another key, captured the same content as a platform-made item.
        ProjectVersion other = SeedVersion(db, project, number: 2, ExpiresAt: DateTime.UtcNow.AddDays(10));
        Guid otherFileId = Guid.NewGuid();
        db.ProjectVersionItems.Add(new ProjectVersionItem
        {
            Id = Guid.NewGuid(),
            VersionId = other.Id,
            ProjectId = project.Id,
            Kind = ProjectVersionItemKind.File,
            ItemKey = otherFileId.ToString("D"),
            Name = "clip.mp4",
            Category = "agentFiles",
            SizeBytes = 2,
            ContentId = "etag:shared",
            StorageKey = $"projects/{project.Id}/agentFiles/{otherFileId:N}.mp4",
            PlatformMade = true,
            OutputProducedAt = DateTime.UtcNow.AddMinutes(-10)
        });
        project.CsamOutputHoldAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        ProjectHistoryController controller =
            Controller(db, store, project.OwnerId, OrganizationRole.Owner);

        IActionResult result = await controller.EraseFile(project.Id, file.Id, CancellationToken.None);

        ConflictObjectResult conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(conflict.Value).Should().Contain(ProjectHistoryErrorCodes.OutputHeld);
        (await db.ProjectVersionItems.AsNoTracking()
                .SingleAsync(i => i.ItemKey == file.Id.ToString("D"))).ErasedAt.Should().BeNull(
            "the only content the erase would touch is platform-made evidence");
    }

    [Fact]
    public async Task EraseFile_IsAllowedDuringAHoldForAPlainUserUpload()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        HistoryTestStore store = new();
        (ProjectFile file, _) = SeedFileVersion(db, store, project);
        project.CsamOutputHoldAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        ProjectHistoryController controller =
            Controller(db, store, project.OwnerId, OrganizationRole.Owner);

        (await controller.EraseFile(project.Id, file.Id, CancellationToken.None))
            .Should().BeOfType<NoContentResult>("the hold protects platform-made items, not the user's uploads");
        (await db.ProjectVersionItems.AsNoTracking().SingleAsync()).ErasedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task EraseFile_204ErasesEveryVersionAndIsIdempotent()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        HistoryTestStore store = new();
        (ProjectFile file, ProjectVersion first) = SeedFileVersion(db, store, project);
        SeedVersionForExistingFile(db, project, number: 2, file, store);
        await db.SaveChangesAsync();
        ProjectHistoryController controller =
            Controller(db, store, project.OwnerId, OrganizationRole.Owner);

        (await controller.EraseFile(project.Id, file.Id, CancellationToken.None))
            .Should().BeOfType<NoContentResult>();

        List<ProjectVersionItem> items = await db.ProjectVersionItems.AsNoTracking().ToListAsync();
        items.Should().OnlyContain(i => i.ErasedAt != null);
        items.Should().HaveCount(2);

        (await controller.EraseFile(project.Id, file.Id, CancellationToken.None))
            .Should().BeOfType<NoContentResult>();
    }

    // ── authority ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CrossOrganizationIs404_NotForbidden()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        ProjectVersion version = SeedVersion(db, project, number: 1, ExpiresAt: null);
        await db.SaveChangesAsync();
        Guid stranger = Guid.NewGuid();

        ProjectHistoryController controller =
            Controller(db, new HistoryTestStore(), stranger, OrganizationRole.Owner,
                organizationId: Guid.NewGuid());

        (await controller.ListVersions(project.Id, null, null, CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
        (await controller.GetVersion(project.Id, version.Id, CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
        (await controller.DeleteVersion(project.Id, version.Id, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task MemberWithoutDeleteIs403OnDeleteAndErase()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        HistoryTestStore store = new();
        (ProjectFile file, ProjectVersion version) = SeedFileVersion(db, store, project);
        await db.SaveChangesAsync();
        Guid memberId = Guid.NewGuid();
        ProjectHistoryController controller =
            Controller(db, store, memberId, OrganizationRole.Member,
                organizationId: project.OrganizationId);

        (await controller.DeleteVersion(project.Id, version.Id, CancellationToken.None))
            .Should().BeOfType<ForbidResult>();
        (await controller.EraseFile(project.Id, file.Id, CancellationToken.None))
            .Should().BeOfType<ForbidResult>();

        (await db.ProjectVersions.CountAsync()).Should().Be(1, "nothing was deleted");
        (await db.ProjectVersionItems.AsNoTracking().SingleAsync()).ErasedAt.Should().BeNull();
    }

    [Fact]
    public async Task BillingIs403InTheOrganizationAnd404OutsideIt()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        await db.SaveChangesAsync();

        ProjectHistoryController inside =
            Controller(db, new HistoryTestStore(), Guid.NewGuid(), OrganizationRole.Billing,
                organizationId: project.OrganizationId);
        (await inside.ListVersions(project.Id, null, null, CancellationToken.None)).Result
            .Should().BeOfType<ForbidResult>();

        ProjectHistoryController outside =
            Controller(db, new HistoryTestStore(), Guid.NewGuid(), OrganizationRole.Billing,
                organizationId: Guid.NewGuid());
        (await outside.ListVersions(project.Id, null, null, CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
    }

    // ── response shape ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResponseDtosNeverCarryStorageIdentities()
    {
        Guid id = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        ProjectHistoryVersionResponse version =
            new(id, 1, "n", ProjectVersionCause.Auto, now, null, "Ada", 1, 10);
        object[] dtos =
        [
            new ProjectHistoryVersionsResponse([version], 10, 30, true, true),
            new ProjectHistoryVersionDetailResponse(
                id, 1, "n", ProjectVersionCause.Auto, now, null, "Ada", 1, 10,
                [new ProjectHistoryItemResponse("File", "k", "a.txt", "userFiles", "text/plain", 10, true, null)]),
            new CreateProjectHistoryVersionRequest("n"),
            new RenameProjectHistoryVersionRequest("n")
        ];

        string[] forbidden = ["storagekey", "storageetag", "contentid", "digest", "bucket", "etag"];

        foreach (object dto in dtos)
        {
            string json = JsonSerializer.Serialize(dto, dto.GetType()).ToLowerInvariant();
            foreach (string member in forbidden)
                json.Should().NotContain(member, $"{dto.GetType().Name} must not expose {member}");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static ProjectHistoryItemResponse Item(ActionResult<ProjectHistoryVersionDetailResponse> result)
    {
        ProjectHistoryVersionDetailResponse body = result.Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<ProjectHistoryVersionDetailResponse>().Subject;
        return body.Items.Single(i => i.Kind == nameof(ProjectVersionItemKind.File));
    }

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"project-history-ctl-{Guid.NewGuid():N}")
            .Options);

    private static Project SeedProject(InferenceApiDbContext db)
    {
        Guid ownerId = Guid.NewGuid();
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId)
        };
        db.Projects.Add(project);
        return project;
    }

    private static ProjectVersion SeedVersion(
        InferenceApiDbContext db,
        Project project,
        int number,
        DateTime? ExpiresAt,
        string? name = null)
    {
        ProjectVersion version = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Number = number,
            Name = name,
            Cause = name is null ? ProjectVersionCause.Auto : ProjectVersionCause.Named,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            ExpiresAt = ExpiresAt,
            ManifestDigest = new string('a', 64)
        };
        db.ProjectVersions.Add(version);
        return version;
    }

    private static (ProjectFile File, ProjectVersion Version) SeedFileVersion(
        InferenceApiDbContext db,
        HistoryTestStore store,
        Project project,
        string content = "v1",
        string? contentId = null)
    {
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Category = "userFiles",
            OriginalFileName = "a.txt",
            OriginalPath = "a.txt",
            StorageKey = $"projects/{project.Id}/userFiles/{Guid.NewGuid():N}.txt",
            StorageBucket = "t",
            MimeType = "text/plain",
            SizeBytes = content.Length
        };
        db.ProjectFiles.Add(file);
        store.Put(file.StorageKey, content);
        string etag = store.ETagAt(file.StorageKey);
        string effectiveContentId = contentId ?? ContentId.FromEtag(etag, content.Length);

        ProjectVersion version = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Number = 1,
            Cause = ProjectVersionCause.Auto,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTime.UtcNow.AddDays(10),
            ManifestDigest = new string('a', 64),
            ItemCount = 1
        };
        db.ProjectVersions.Add(version);
        AddFileItem(db, project, version, file, etag, effectiveContentId, content.Length);
        return (file, version);
    }

    private static void SeedVersionForExistingFile(
        InferenceApiDbContext db,
        Project project,
        int number,
        ProjectFile file,
        HistoryTestStore store)
    {
        ProjectVersion version = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Number = number,
            Cause = ProjectVersionCause.Auto,
            CreatedAt = DateTime.UtcNow.AddMinutes(-4),
            ExpiresAt = DateTime.UtcNow.AddDays(10),
            ManifestDigest = new string('b', 64)
        };
        db.ProjectVersions.Add(version);
        AddFileItem(
            db, project, version, file, store.ETagAt(file.StorageKey),
            ContentId.FromEtag(store.ETagAt(file.StorageKey), file.SizeBytes), file.SizeBytes);
    }

    private static void AddFileItem(
        InferenceApiDbContext db,
        Project project,
        ProjectVersion version,
        ProjectFile file,
        string etag,
        string contentId,
        long size)
    {
        db.ProjectVersionItems.Add(new ProjectVersionItem
        {
            Id = Guid.NewGuid(),
            VersionId = version.Id,
            ProjectId = project.Id,
            Kind = ProjectVersionItemKind.File,
            ItemKey = file.Id.ToString("D"),
            Name = file.OriginalPath,
            Category = file.Category,
            MimeType = file.MimeType,
            SizeBytes = size,
            ContentId = contentId,
            StorageKey = file.StorageKey,
            StorageETag = etag
        });
    }

    private static ICurrentUser User(Guid userId, OrganizationRole role, Guid organizationId)
    {
        Mock<ICurrentUser> user = new();
        user.SetupGet(u => u.UserId).Returns(userId);
        user.SetupGet(u => u.Email).Returns("history@example.com");
        user.SetupGet(u => u.IsAuthenticated).Returns(true);
        user.SetupGet(u => u.IsPlatformAdmin).Returns(false);
        user.SetupGet(u => u.OrganizationId).Returns(organizationId);
        user.SetupGet(u => u.OrgRole).Returns(role);
        return user.Object;
    }

    private static ProjectHistoryController Controller(
        InferenceApiDbContext db,
        IFileStorageService storage,
        Guid userId,
        OrganizationRole role,
        Guid? organizationId = null,
        ProjectHistoryOptions? options = null)
    {
        ICurrentUser user = User(userId, role, organizationId ?? TestTenancy.OrgOf(userId));
        ProjectHistoryService history = new(
            db,
            storage,
            Options.Create(options ?? new ProjectHistoryOptions()),
            NullLogger<ProjectHistoryService>.Instance);
        ProjectHistoryEraser eraser = new(db, storage, NullLogger<ProjectHistoryEraser>.Instance);
        return new ProjectHistoryController(TestTenancy.Access(db, user), history, eraser, user, null!);
    }
}
