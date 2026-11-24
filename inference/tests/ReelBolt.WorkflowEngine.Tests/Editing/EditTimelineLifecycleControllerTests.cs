using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Editing;

/// <summary>
/// T-012: the PATCH request checks the audit found missing (d1 N7 missing <c>baseRevision</c>,
/// api D7 header <c>id</c>/<c>projectId</c>) and the W0.9 lifecycle endpoints
/// (<c>DELETE …/edit-timelines/{id}</c>, <c>PUT …/edit-timelines/{id}/name</c>).
/// </summary>
public class EditTimelineLifecycleControllerTests
{
    private static readonly JsonSerializerOptions DocumentJsonOptions = EditTimelineJson.Options;

    // ─── PATCH: baseRevision and header ───────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Patch_WithoutABaseRevision_Is400NamingTheField_NotA409(int baseRevision)
    {
        // A JSON body with no baseRevision binds it to 0; revisions start at 1, so 0 (or less) is
        // never a revision the caller edited from.
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).PatchEditTimeline(
            project.Id, row.Id, new PatchEditTimelineRequest(baseRevision, null, Blank(project.Id, row.Id, "T")), CancellationToken.None);

        BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeAssignableTo<IEnumerable<EditTimelineViolation>>().Subject
            .Should().ContainSingle(v => v.Path == "baseRevision");
        (await db.EditTimelines.AsNoTracking().SingleAsync()).Revision.Should().Be(1);
    }

    [Fact]
    public async Task Patch_WhoseDocumentNamesAnotherTimelineOrProject_Is400AtTheHeaderPaths()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);

        EditTimeline foreign = Blank(Guid.NewGuid(), Guid.NewGuid(), "T");
        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).PatchEditTimeline(
            project.Id, row.Id, new PatchEditTimelineRequest(1, null, foreign), CancellationToken.None);

        BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        List<EditTimelineViolation> violations = bad.Value.Should().BeAssignableTo<IEnumerable<EditTimelineViolation>>().Subject.ToList();
        violations.Select(v => v.Path).Should().Contain(["id", "projectId"]);
        (await db.EditTimelines.AsNoTracking().SingleAsync()).Revision.Should().Be(1);
    }

    [Fact]
    public async Task Patch_StaleBaseRevisionStillWinsOverAnInvalidHeader_The409ComesFirst()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 4);

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).PatchEditTimeline(
            project.Id, row.Id, new PatchEditTimelineRequest(2, null, Blank(Guid.NewGuid(), Guid.NewGuid(), "T")), CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    // ─── DELETE ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_SoftDeletes_204_ThenTheTimelineVanishesFromListGetAndRenders()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);
        EditTimelinesController controller = Controller(db, project.OwnerId);

        IActionResult deleted = await controller.DeleteEditTimeline(project.Id, row.Id, CancellationToken.None);

        deleted.Should().BeOfType<NoContentResult>();
        (await db.EditTimelines.IgnoreQueryFilters().AsNoTracking().SingleAsync()).DeletedAt.Should().NotBeNull(
            "the delete is soft: the row stays, filtered out by the soft-delete query filter");

        ActionResult<List<EditTimelineSummaryResponse>> list = await controller.ListEditTimelines(project.Id, null, CancellationToken.None);
        list.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<List<EditTimelineSummaryResponse>>()
            .Subject.Should().BeEmpty();
        (await controller.GetEditTimeline(project.Id, row.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.ListRenders(project.Id, row.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.DeleteEditTimeline(project.Id, row.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>(
            "a second delete finds nothing");
    }

    [Fact]
    public async Task Delete_IsNotFoundForAnUnknownIdAnotherProjectsTimelineAndAnotherTenant()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);
        (Project other, EditTimelineEntity otherRow) = await SeedAsync(db);

        (await Controller(db, project.OwnerId).DeleteEditTimeline(project.Id, Guid.NewGuid(), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
        (await Controller(db, project.OwnerId).DeleteEditTimeline(project.Id, otherRow.Id, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>("a timeline id from another project is not found under this one");
        (await Controller(db, project.OwnerId).DeleteEditTimeline(other.Id, otherRow.Id, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>("another tenant's project is 404, never 403");

        (await db.EditTimelines.AsNoTracking().CountAsync()).Should().Be(2, "nothing was deleted");
    }

    [Fact]
    public async Task Delete_NeedsEdit_ARoleWithoutItIsRefusedAndNothingChanges()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);

        IActionResult result = await Controller(db, project.OwnerId, OrganizationRole.Billing)
            .DeleteEditTimeline(project.Id, row.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        (await db.EditTimelines.AsNoTracking().SingleAsync()).DeletedAt.Should().BeNull();
    }

    // ─── DELETE racing another writer (the Revision concurrency token) ────────────

    [Fact]
    public async Task Delete_WhenAPatchCommitsBetweenItsReadAndItsWrite_StillDeletes_204()
    {
        string name = Guid.NewGuid().ToString();
        Project project;
        EditTimelineEntity row;
        using (InferenceApiDbContext seed = NewContext(name)) (project, row) = await SeedAsync(seed);

        // The racing PATCH advances the revision after the delete read the row, so the delete's
        // UPDATE (guarded by the Revision concurrency token) matches no row.
        using InferenceApiDbContext db = NewContext(name, new BeforeFirstSave(async () =>
        {
            using InferenceApiDbContext other = NewContext(name);
            EditTimelineEntity racing = await other.EditTimelines.SingleAsync();
            racing.Revision += 1;
            await other.SaveChangesAsync();
        }));

        IActionResult result = await Controller(db, project.OwnerId).DeleteEditTimeline(project.Id, row.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>("the delete wins over a PATCH that merely moved the revision");
        using InferenceApiDbContext check = NewContext(name);
        EditTimelineEntity stored = await check.EditTimelines.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        stored.DeletedAt.Should().NotBeNull();
        stored.Revision.Should().Be(3, "the racing PATCH took revision 2 and the delete advanced it past that");
    }

    [Fact]
    public async Task Delete_WhenAnotherDeleteCommitsBetweenItsReadAndItsWrite_Is404()
    {
        string name = Guid.NewGuid().ToString();
        Project project;
        EditTimelineEntity row;
        using (InferenceApiDbContext seed = NewContext(name)) (project, row) = await SeedAsync(seed);

        using InferenceApiDbContext db = NewContext(name, new BeforeFirstSave(async () =>
        {
            using InferenceApiDbContext other = NewContext(name);
            EditTimelineEntity racing = await other.EditTimelines.SingleAsync();
            racing.DeletedAt = DateTime.UtcNow;
            racing.Revision += 1;
            await other.SaveChangesAsync();
        }));

        IActionResult result = await Controller(db, project.OwnerId).DeleteEditTimeline(project.Id, row.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Patch_WhenADeleteCommitsBetweenItsReadAndItsWrite_Is404_NotA409()
    {
        string name = Guid.NewGuid().ToString();
        Project project;
        EditTimelineEntity row;
        using (InferenceApiDbContext seed = NewContext(name)) (project, row) = await SeedAsync(seed);

        using InferenceApiDbContext db = NewContext(name, new BeforeFirstSave(async () =>
        {
            using InferenceApiDbContext other = NewContext(name);
            EditTimelineEntity racing = await other.EditTimelines.SingleAsync();
            racing.DeletedAt = DateTime.UtcNow;
            racing.Revision += 1;
            await other.SaveChangesAsync();
        }));

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).PatchEditTimeline(
            project.Id, row.Id, new PatchEditTimelineRequest(1, null, Blank(project.Id, row.Id, "Late")), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>("there is no current revision to rebase onto: the timeline is gone");
    }

    // ─── PUT …/name ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rename_TrimsAndRenamesRowAndDocument_WithoutARevisionBumpOrAnOpRow()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db, revision: 7);

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).RenameEditTimeline(
            project.Id, row.Id, new RenameEditTimelineRequest("  Final cut  "), CancellationToken.None);

        EditTimelineResponse body = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<EditTimelineResponse>().Subject;
        body.Name.Should().Be("Final cut");
        body.Document.Name.Should().Be("Final cut");
        body.Revision.Should().Be(7);

        EditTimelineEntity stored = await db.EditTimelines.AsNoTracking().SingleAsync();
        stored.Name.Should().Be("Final cut");
        stored.Revision.Should().Be(7, "a rename is row metadata, not an edit: no revision bump");
        JsonSerializer.Deserialize<EditTimeline>(stored.DocumentJson, DocumentJsonOptions)!.Name.Should().Be("Final cut");
        (await db.EditTimelineOps.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(201)]
    public async Task Rename_RefusesABlankOrOverlongName_400AtTheNamePath(object? name)
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);
        string? requested = name is int length ? new string('x', length) : (string?)name;

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).RenameEditTimeline(
            project.Id, row.Id, new RenameEditTimelineRequest(requested), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<EditTimelineViolation>>().Subject
            .Should().ContainSingle(v => v.Path == "name");
        (await db.EditTimelines.AsNoTracking().SingleAsync()).Name.Should().Be("Seeded");
    }

    [Fact]
    public async Task Rename_AcceptsExactly200CharactersAfterTrimming()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);
        string name = new('y', 200);

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).RenameEditTimeline(
            project.Id, row.Id, new RenameEditTimelineRequest(" " + name + " "), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        (await db.EditTimelines.AsNoTracking().SingleAsync()).Name.Should().Be(name);
    }

    [Fact]
    public async Task Rename_IsNotFoundAcrossTenantsAndForADeletedTimeline()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);
        (Project other, EditTimelineEntity otherRow) = await SeedAsync(db);

        (await Controller(db, project.OwnerId).RenameEditTimeline(other.Id, otherRow.Id, new RenameEditTimelineRequest("Mine"), CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>();

        await Controller(db, project.OwnerId).DeleteEditTimeline(project.Id, row.Id, CancellationToken.None);
        (await Controller(db, project.OwnerId).RenameEditTimeline(project.Id, row.Id, new RenameEditTimelineRequest("Back"), CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Rename_WhenADeleteCommitsDuringItsLastRetry_Is404_NotA409AtRevisionZero()
    {
        string name = Guid.NewGuid().ToString();
        Project project;
        EditTimelineEntity row;
        using (InferenceApiDbContext seed = NewContext(name)) (project, row) = await SeedAsync(seed);

        // Two racing PATCHes move the revision under the first two tries; a racing delete lands
        // under the third, so after the last try there is no row left to report a revision of.
        using InferenceApiDbContext db = NewContext(name, new BeforeEachSave(async save =>
        {
            using InferenceApiDbContext other = NewContext(name);
            EditTimelineEntity racing = await other.EditTimelines.SingleAsync();
            if (save == 3) racing.DeletedAt = DateTime.UtcNow;
            racing.Revision += 1;
            await other.SaveChangesAsync();
        }));

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).RenameEditTimeline(
            project.Id, row.Id, new RenameEditTimelineRequest("Late"), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>("the timeline is gone, so there is no current revision to report");
    }

    // ─── Names refuse control characters (NUL is Postgres 22021; C0 breaks FCPXML) ──

    public static TheoryData<string> NamesWithAControlCharacter => new()
    {
        "Final\0cut",
        "Final\u0007cut",
        "Final\ncut",
        "Final\tcut",
        "Final\u001bcut",
        "Final\u007fcut",
        "Final\u0085cut",
    };

    [Theory]
    [MemberData(nameof(NamesWithAControlCharacter))]
    public async Task Rename_RefusesANameWithAControlCharacter_400AtTheNamePath_InAPlainSentence(string name)
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).RenameEditTimeline(
            project.Id, row.Id, new RenameEditTimelineRequest(name), CancellationToken.None);

        EditTimelineViolation violation = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<EditTimelineViolation>>().Subject
            .Should().ContainSingle(v => v.Path == "name").Subject;
        violation.Message.Should().Be(EditTimelinesController.NameHasControlCharacterMessage);
        (await db.EditTimelines.AsNoTracking().SingleAsync()).Name.Should().Be("Seeded");
    }

    [Theory]
    [MemberData(nameof(NamesWithAControlCharacter))]
    public async Task Create_RefusesANameWithAControlCharacter_400(string name)
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, _) = await SeedAsync(db);

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).CreateEditTimeline(
            project.Id, new CreateEditTimelineRequest(name, null), CancellationToken.None);

        JsonSerializer.Serialize(result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value)
            .Should().Contain("control characters");
        (await db.EditTimelines.AsNoTracking().CountAsync()).Should().Be(1, "nothing was created");
    }

    [Theory]
    [MemberData(nameof(NamesWithAControlCharacter))]
    public async Task Duplicate_RefusesANameWithAControlCharacter_400(string name)
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).DuplicateEditTimeline(
            project.Id, row.Id, new DuplicateEditTimelineRequest(name), CancellationToken.None);

        JsonSerializer.Serialize(result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value)
            .Should().Contain("control characters");
        (await db.EditTimelines.AsNoTracking().CountAsync()).Should().Be(1, "nothing was duplicated");
    }

    [Fact]
    public async Task Import_RefusesARequestedNameWithAControlCharacter_400()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, _) = await SeedAsync(db);

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).ImportEditTimeline(
            project.Id, new ImportEditTimelineRequest("Final\0cut", EmptyOtio("Fine")), CancellationToken.None);

        JsonSerializer.Serialize(result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value)
            .Should().Contain("control characters");
        (await db.EditTimelines.AsNoTracking().CountAsync()).Should().Be(1, "nothing was imported");
    }

    [Fact]
    public async Task Import_ANameTakenFromTheFileWithAControlCharacter_FallsBackToTheDefaultName()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, _) = await SeedAsync(db);

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).ImportEditTimeline(
            project.Id, new ImportEditTimelineRequest(null, EmptyOtio("Final\0cut")), CancellationToken.None);

        ObjectResult created = result.Result.Should().BeOfType<ObjectResult>(JsonSerializer.Serialize((result.Result as ObjectResult)?.Value)).Subject;
        created.Value.Should().BeOfType<EditTimelineResponse>().Subject.Name.Should().Be("Imported timeline");
    }

    // ─── Names are at most 200 characters (the varchar(200) column; over it was a 500) ──

    [Fact]
    public async Task Create_RefusesANameOver200CharactersAfterTrimming_400InAPlainSentence()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, _) = await SeedAsync(db);

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).CreateEditTimeline(
            project.Id, new CreateEditTimelineRequest(new string('x', 201), null), CancellationToken.None);

        JsonSerializer.Serialize(result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value)
            .Should().Contain(EditTimelinesController.NameTooLongMessage);
        (await db.EditTimelines.AsNoTracking().CountAsync()).Should().Be(1, "nothing was created");
    }

    [Fact]
    public async Task Create_AcceptsExactly200CharactersAfterTrimming()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, _) = await SeedAsync(db);

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).CreateEditTimeline(
            project.Id, new CreateEditTimelineRequest("  " + new string('x', 200) + "  ", null), CancellationToken.None);

        result.Result.Should().NotBeOfType<BadRequestObjectResult>();
        (await db.EditTimelines.AsNoTracking().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Import_RefusesARequestedNameOver200Characters_400InAPlainSentence()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, _) = await SeedAsync(db);

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).ImportEditTimeline(
            project.Id, new ImportEditTimelineRequest(new string('x', 201), EmptyOtio("Fine")), CancellationToken.None);

        JsonSerializer.Serialize(result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value)
            .Should().Contain(EditTimelinesController.NameTooLongMessage);
        (await db.EditTimelines.AsNoTracking().CountAsync()).Should().Be(1, "nothing was imported");
    }

    [Fact]
    public async Task Import_ANameTakenFromTheFileOver200Characters_FallsBackToTheDefaultName()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, _) = await SeedAsync(db);

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).ImportEditTimeline(
            project.Id, new ImportEditTimelineRequest(null, EmptyOtio(new string('x', 201))), CancellationToken.None);

        ObjectResult created = result.Result.Should().BeOfType<ObjectResult>(JsonSerializer.Serialize((result.Result as ObjectResult)?.Value)).Subject;
        created.Value.Should().BeOfType<EditTimelineResponse>().Subject.Name.Should().Be("Imported timeline");
    }

    [Fact]
    public async Task Duplicate_RefusesARequestedNameOver200Characters_400InAPlainSentence()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).DuplicateEditTimeline(
            project.Id, row.Id, new DuplicateEditTimelineRequest(new string('x', 201)), CancellationToken.None);

        JsonSerializer.Serialize(result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value)
            .Should().Contain(EditTimelinesController.NameTooLongMessage);
        (await db.EditTimelines.AsNoTracking().CountAsync()).Should().Be(1, "nothing was duplicated");
    }

    [Fact]
    public async Task Duplicate_OfA200CharacterName_TruncatesTheBaseSoTheCopySuffixFits200()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);
        string longName = new('y', 200);
        (await Controller(db, project.OwnerId).RenameEditTimeline(project.Id, row.Id, new RenameEditTimelineRequest(longName), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>();

        ActionResult<EditTimelineResponse> result = await Controller(db, project.OwnerId).DuplicateEditTimeline(
            project.Id, row.Id, null, CancellationToken.None);

        string copyName = ((EditTimelineResponse)((ObjectResult)result.Result!).Value!).Name;
        copyName.Should().HaveLength(EditTimelinesController.MaxNameLength);
        copyName.Should().Be(new string('y', 200 - " (copy)".Length) + " (copy)");
    }

    private static JsonElement EmptyOtio(string name)
    {
        Dictionary<string, object> videoTrack = new()
        {
            ["OTIO_SCHEMA"] = "Track.1",
            ["kind"] = "Video",
            ["name"] = "V1",
            ["children"] = Array.Empty<object>(),
        };
        Dictionary<string, object> stack = new()
        {
            ["OTIO_SCHEMA"] = "Stack.1",
            ["name"] = "tracks",
            ["children"] = new object[] { videoTrack },
        };
        return JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["OTIO_SCHEMA"] = "Timeline.1",
            ["name"] = name,
            ["tracks"] = stack,
        });
    }

    // ─── Comments follow their timeline into deletion ─────────────────────────────

    [Fact]
    public async Task CommentsOnADeletedTimeline_CannotBeListedAddedEditedResolvedOrDeleted_404()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);
        EditTimelinesController controller = Controller(db, project.OwnerId);
        EditTimelineCommentResponse comment = (EditTimelineCommentResponse)((ObjectResult)(await controller.CreateComment(
            project.Id, row.Id, new CreateEditTimelineCommentRequest("Tighten this", 30, null, null), CancellationToken.None)).Result!).Value!;

        (await controller.DeleteEditTimeline(project.Id, row.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();

        (await controller.ListComments(project.Id, row.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.CreateComment(project.Id, row.Id, new CreateEditTimelineCommentRequest("More", 0, null, null), CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>();
        (await controller.UpdateComment(project.Id, row.Id, comment.Id, new UpdateEditTimelineCommentRequest("Rewritten", null), CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>("the author cannot edit a comment on a deleted timeline");
        (await controller.UpdateComment(project.Id, row.Id, comment.Id, new UpdateEditTimelineCommentRequest(null, true), CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>("nor resolve it");
        (await controller.DeleteComment(project.Id, row.Id, comment.Id, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>("nor delete it");

        EditTimelineComment stored = await db.EditTimelineComments.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        stored.Body.Should().Be("Tighten this");
        stored.Resolved.Should().BeFalse();
        stored.DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task EditingAndDeletingAComment_EachConsultTheAccessServiceExactlyOnce()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);
        EditTimelineCommentResponse comment = (EditTimelineCommentResponse)((ObjectResult)(await Controller(db, project.OwnerId).CreateComment(
            project.Id, row.Id, new CreateEditTimelineCommentRequest("Once", 0, null, null), CancellationToken.None)).Result!).Value!;

        CountingAccess updateAccess = new(TestTenancy.Access(db, User(project.OwnerId, OrganizationRole.Member)));
        (await Controller(db, project.OwnerId, access: updateAccess)
            .UpdateComment(project.Id, row.Id, comment.Id, new UpdateEditTimelineCommentRequest(null, true), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>();
        updateAccess.Calls.Should().Equal(ProjectPermission.View);

        CountingAccess deleteAccess = new(TestTenancy.Access(db, User(project.OwnerId, OrganizationRole.Member)));
        (await Controller(db, project.OwnerId, access: deleteAccess)
            .DeleteComment(project.Id, row.Id, comment.Id, CancellationToken.None))
            .Should().BeOfType<NoContentResult>();
        deleteAccess.Calls.Should().Equal(ProjectPermission.View);
    }

    // ─── The access service is consulted exactly once per action ──────────────────

    [Fact]
    public async Task DeleteAndRename_EachConsultTheAccessServiceExactlyOnce_WithEdit()
    {
        using InferenceApiDbContext db = NewContext();
        (Project project, EditTimelineEntity row) = await SeedAsync(db);

        CountingAccess renameAccess = new(TestTenancy.Access(db, User(project.OwnerId, OrganizationRole.Member)));
        await Controller(db, project.OwnerId, access: renameAccess)
            .RenameEditTimeline(project.Id, row.Id, new RenameEditTimelineRequest("Once"), CancellationToken.None);
        renameAccess.Calls.Should().Equal(ProjectPermission.Edit);

        CountingAccess deleteAccess = new(TestTenancy.Access(db, User(project.OwnerId, OrganizationRole.Member)));
        await Controller(db, project.OwnerId, access: deleteAccess)
            .DeleteEditTimeline(project.Id, row.Id, CancellationToken.None);
        deleteAccess.Calls.Should().Equal(ProjectPermission.Edit);
    }

    // ─────────────────────────────────────────────────────────────────────────────

    private sealed class CountingAccess(IProjectAccessService inner) : IProjectAccessService
    {
        public List<ProjectPermission> Calls { get; } = [];

        public Task<ProjectAccessResult> GetAsync(Guid projectId, ProjectPermission permission, CancellationToken ct)
        {
            Calls.Add(permission);
            return inner.GetAsync(projectId, permission, ct);
        }

        public ProjectAccessResult Get(Guid projectId, ProjectPermission permission)
        {
            Calls.Add(permission);
            return inner.Get(projectId, permission);
        }

        public ProjectAccessOutcome AuthorizeOrganization(ProjectPermission permission) => inner.AuthorizeOrganization(permission);
    }

    private static InferenceApiDbContext NewContext() => NewContext(Guid.NewGuid().ToString());

    private static InferenceApiDbContext NewContext(string databaseName, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(interceptors)
            .Options);

    /// <summary>
    /// Runs <paramref name="race"/> once, just before the context's first <c>SaveChanges</c>: a
    /// deterministic stand-in for another request committing between this one's read and write.
    /// </summary>
    private sealed class BeforeFirstSave(Func<Task> race) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await race();
            }

            return result;
        }
    }

    /// <summary>
    /// Runs <paramref name="race"/> before every <c>SaveChanges</c> of the context, passing the
    /// 1-based save number: another request committing under each retry in turn.
    /// </summary>
    private sealed class BeforeEachSave(Func<int, Task> race) : SaveChangesInterceptor
    {
        private int _saves;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await race(++_saves);
            return result;
        }
    }

    private static async Task<(Project Project, EditTimelineEntity Row)> SeedAsync(InferenceApiDbContext db, int revision = 1)
    {
        Guid ownerId = Guid.NewGuid();
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "Lifecycle",
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        Guid timelineId = Guid.NewGuid();
        EditTimelineEntity row = new()
        {
            Id = timelineId,
            ProjectId = project.Id,
            Name = "Seeded",
            DocumentJson = JsonSerializer.Serialize(Blank(project.Id, timelineId, "Seeded"), DocumentJsonOptions),
            Revision = revision,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.EditTimelines.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (project, row);
    }

    private static ICurrentUser User(Guid userId, OrganizationRole role)
    {
        Mock<ICurrentUser> user = new();
        user.SetupGet(u => u.UserId).Returns(userId);
        user.SetupGet(u => u.Email).Returns("lifecycle@example.com");
        user.SetupGet(u => u.IsAuthenticated).Returns(true);
        user.SetupGet(u => u.IsPlatformAdmin).Returns(false);
        user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(userId));
        user.SetupGet(u => u.OrgRole).Returns(role);
        return user.Object;
    }

    private static EditTimelinesController Controller(
        InferenceApiDbContext db,
        Guid userId,
        OrganizationRole role = OrganizationRole.Member,
        IProjectAccessService? access = null) =>
        new(db, User(userId, role), access ?? TestTenancy.Access(db, User(userId, role)),
            new Mock<IFileStorageService>().Object, new Mock<IWorkflowExecutionService>().Object);

    private static EditTimeline Blank(Guid projectId, Guid timelineId, string name) =>
        new(
            id: timelineId,
            projectId: projectId,
            name: name,
            settings: new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
            tracks:
            [
                new Track(EditTimelineSeeder.TrackId("V1"), TrackKind.Video, "Video", null, 100, false, true, false, false, null, Array.Empty<Item>()),
                new Track(EditTimelineSeeder.TrackId("A1"), TrackKind.Audio, "Audio", null, 100, false, true, false, false, null, Array.Empty<Item>())
            ],
            markers: Array.Empty<Marker>());
}
