#nullable enable
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
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// P3's three companions on <see cref="EditTimelinesController"/> — <c>history</c>,
/// <c>revisions/{rev}</c>, <c>duplicate</c> — plus the one write-path addition P3 made
/// (a revision-1 snapshot at birth) and the proof that restore is an ordinary PATCH.
///
/// <para>
/// The four P1 actions' observable step order is covered by <c>EditTimelinesControllerTests</c> and
/// <c>EditTimelineStepOrderTests</c>; nothing here re-tests it, but the restore test below runs the
/// real PATCH and asserts the same stale-revision <c>409</c> + no-write it always had, so a restore
/// cannot be a back door around optimistic concurrency.
/// </para>
/// </summary>
public class EditTimelineHistoryControllerTests
{
    private static readonly JsonSerializerOptions DocumentJsonOptions = new(JsonSerializerDefaults.Web);

    // ──────────────────────────────────────────────────────────────────────────
    // history
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHistory_ReturnsNewestFirst_WithOpsVerbatim_AndRestorableSet()
    {
        string databaseName = NewDatabaseName();
        Guid projectId, timelineId, ownerId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db);
            projectId = project.Id;
            ownerId = project.OwnerId;

            EditTimelineEntity timeline = await AddTimelineAsync(db, projectId, "T", revision: 41);
            timelineId = timeline.Id;

            // An op row per revision 2..41, a snapshot at 1, 20 and 40 (the PATCH cadence).
            for (int rev = 2; rev <= 41; rev++)
            {
                db.EditTimelineOps.Add(new EditTimelineOp
                {
                    Id = Guid.NewGuid(),
                    TimelineId = timelineId,
                    Revision = rev,
                    OpsJson = $$"""[{"type":"blade","itemId":"i{{rev}}","atFrame":{{rev}},"newItemId":"n{{rev}}"}]""",
                    CreatedAt = DateTime.UtcNow.AddSeconds(rev)
                });
            }

            foreach (int rev in new[] { 1, 20, 40 })
            {
                db.EditTimelineSnapshots.Add(new EditTimelineSnapshot
                {
                    Id = Guid.NewGuid(),
                    TimelineId = timelineId,
                    Revision = rev,
                    DocumentJson = timeline.DocumentJson,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync();
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            ActionResult<EditTimelineHistoryResponse> result =
                await controller.GetHistory(projectId, timelineId, limit: 10, before: null, CancellationToken.None);

            EditTimelineHistoryResponse body = result.Result.Should().BeOfType<OkObjectResult>().Subject
                .Value.Should().BeOfType<EditTimelineHistoryResponse>().Subject;

            body.CurrentRevision.Should().Be(41);
            body.Entries.Should().HaveCount(10);
            body.Entries.Select(e => e.Revision).Should().BeInDescendingOrder();
            body.Entries[0].Revision.Should().Be(41);
            body.Entries[9].Revision.Should().Be(32);

            // ops are handed back verbatim — the server never interprets them.
            body.Entries[0].Ops.ValueKind.Should().Be(JsonValueKind.Array);
            body.Entries[0].Ops[0].GetProperty("type").GetString().Should().Be("blade");
            body.Entries[0].Ops[0].GetProperty("itemId").GetString().Should().Be("i41");

            // The current revision is restorable (live row), 40 is (snapshot), 39 is not.
            body.Entries.Single(e => e.Revision == 41).HasSnapshot.Should().BeTrue();
            body.Entries.Single(e => e.Revision == 40).HasSnapshot.Should().BeTrue();
            body.Entries.Single(e => e.Revision == 39).HasSnapshot.Should().BeFalse();
            body.RestorableRevisions.Should().Equal(1, 20, 40, 41);

            // Paging: `before` continues from where the first page stopped.
            ActionResult<EditTimelineHistoryResponse> page2 =
                await controller.GetHistory(projectId, timelineId, limit: 10, before: 32, CancellationToken.None);
            EditTimelineHistoryResponse second = page2.Result.Should().BeOfType<OkObjectResult>().Subject
                .Value.Should().BeOfType<EditTimelineHistoryResponse>().Subject;
            second.Entries[0].Revision.Should().Be(31);
            second.Entries.Should().HaveCount(10);
        }
    }

    [Fact]
    public async Task GetHistory_ClampsLimit_AndIs404ForForeignOrMissingTimeline()
    {
        string databaseName = NewDatabaseName();
        Guid projectId, otherProjectId, timelineId, ownerId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db);
            Project other = await AddProjectAsync(db);
            projectId = project.Id;
            otherProjectId = other.Id;
            ownerId = project.OwnerId;
            timelineId = (await AddTimelineAsync(db, projectId, "T", revision: 3)).Id;
            for (int rev = 2; rev <= 3; rev++)
            {
                db.EditTimelineOps.Add(new EditTimelineOp { Id = Guid.NewGuid(), TimelineId = timelineId, Revision = rev, OpsJson = "[]" });
            }
            await db.SaveChangesAsync();
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            // A huge limit is clamped, not rejected.
            ActionResult<EditTimelineHistoryResponse> ok =
                await controller.GetHistory(projectId, timelineId, limit: 100_000, before: null, CancellationToken.None);
            ok.Result.Should().BeOfType<OkObjectResult>();

            // The same id through a project in another organization: the project access service answers
            // 404 (D5 — existence never leaks across tenants). Through a project the caller CAN see but
            // which does not hold the timeline: 404 too, indistinguishable from "no such timeline".
            ActionResult<EditTimelineHistoryResponse> missing =
                await controller.GetHistory(projectId, Guid.NewGuid(), null, null, CancellationToken.None);
            missing.Result.Should().BeOfType<NotFoundResult>();

            ActionResult<EditTimelineHistoryResponse> foreign =
                await controller.GetHistory(otherProjectId, timelineId, null, null, CancellationToken.None);
            foreign.Result.Should().BeOfType<NotFoundResult>();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // revisions/{rev}
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRevision_ServesCurrentFromRow_SnapshotFromSnapshots_And404WithRestorableSetOtherwise()
    {
        string databaseName = NewDatabaseName();
        Guid projectId, timelineId, ownerId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db);
            projectId = project.Id;
            ownerId = project.OwnerId;

            EditTimelineEntity timeline = await AddTimelineAsync(db, projectId, "Current name", revision: 25);
            timelineId = timeline.Id;

            EditTimeline old = BlankDocument(projectId, timelineId, "Name at revision 20");
            db.EditTimelineSnapshots.Add(new EditTimelineSnapshot
            {
                Id = Guid.NewGuid(),
                TimelineId = timelineId,
                Revision = 20,
                DocumentJson = JsonSerializer.Serialize(old, DocumentJsonOptions),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            EditTimelineRevisionResponse current = (await controller.GetRevision(projectId, timelineId, 25, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>().Subject
                .Value.Should().BeOfType<EditTimelineRevisionResponse>().Subject;
            current.Source.Should().Be("current");
            current.Revision.Should().Be(25);
            current.Document.Name.Should().Be("Current name");

            EditTimelineRevisionResponse snapshot = (await controller.GetRevision(projectId, timelineId, 20, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>().Subject
                .Value.Should().BeOfType<EditTimelineRevisionResponse>().Subject;
            snapshot.Source.Should().Be("snapshot");
            snapshot.Document.Name.Should().Be("Name at revision 20");

            NotFoundObjectResult unavailable = (await controller.GetRevision(projectId, timelineId, 23, CancellationToken.None))
                .Result.Should().BeOfType<NotFoundObjectResult>().Subject;
            EditTimelineRevisionUnavailableResponse body = unavailable.Value.Should()
                .BeOfType<EditTimelineRevisionUnavailableResponse>().Subject;
            body.RequestedRevision.Should().Be(23);
            body.RestorableRevisions.Should().Equal(20, 25);

            // Above the current revision, or below 1: a plain 404.
            (await controller.GetRevision(projectId, timelineId, 26, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
            (await controller.GetRevision(projectId, timelineId, 0, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // restore = ordinary PATCH
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_IsAnOrdinaryPatch_ProducingRevisionNPlusOne_AndStaleBaseRevisionIsStill409()
    {
        string databaseName = NewDatabaseName();
        Guid projectId, timelineId, ownerId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db);
            projectId = project.Id;
            ownerId = project.OwnerId;
            timelineId = (await AddTimelineAsync(db, projectId, "Edited", revision: 7)).Id;

            db.EditTimelineSnapshots.Add(new EditTimelineSnapshot
            {
                Id = Guid.NewGuid(),
                TimelineId = timelineId,
                Revision = 1,
                DocumentJson = JsonSerializer.Serialize(BlankDocument(projectId, timelineId, "Original"), DocumentJsonOptions),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        EditTimeline restored;
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);
            restored = (await controller.GetRevision(projectId, timelineId, 1, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>().Subject
                .Value.Should().BeOfType<EditTimelineRevisionResponse>().Subject.Document;
        }

        // A restore that does not carry the current revision is a stale writer, exactly as before.
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);
            ActionResult<EditTimelineResponse> stale = await controller.PatchEditTimeline(
                projectId, timelineId,
                new PatchEditTimelineRequest(BaseRevision: 1, Ops: null, Document: restored),
                CancellationToken.None);
            ConflictObjectResult conflict = stale.Result.Should().BeOfType<ConflictObjectResult>().Subject;
            conflict.Value.Should().BeOfType<EditTimelineConflictResponse>().Subject.CurrentRevision.Should().Be(7);
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            (await db.EditTimelines.SingleAsync(t => t.Id == timelineId)).Revision.Should().Be(7, "a stale restore writes nothing");
            (await db.EditTimelineOps.CountAsync(o => o.TimelineId == timelineId)).Should().Be(0);
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);
            ActionResult<EditTimelineResponse> ok = await controller.PatchEditTimeline(
                projectId, timelineId,
                new PatchEditTimelineRequest(
                    BaseRevision: 7,
                    Ops: JsonDocument.Parse("""[{"type":"restore","revision":1}]""").RootElement.Clone(),
                    Document: restored),
                CancellationToken.None);
            EditTimelineResponse response = ok.Result.Should().BeOfType<OkObjectResult>().Subject
                .Value.Should().BeOfType<EditTimelineResponse>().Subject;
            response.Revision.Should().Be(8, "restore appends; it never rewrites history");
            response.Document.Name.Should().Be("Original");
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelineOp op = await db.EditTimelineOps.SingleAsync(o => o.TimelineId == timelineId);
            op.Revision.Should().Be(8);
            op.OpsJson.Should().Contain("\"restore\"", "the client's ops are stored verbatim, restore included");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // create → revision-1 snapshot; duplicate
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WritesARevisionOneSnapshot_SoTheOriginalIsAlwaysRestorable()
    {
        string databaseName = NewDatabaseName();
        Guid projectId, ownerId;
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db);
            projectId = project.Id;
            ownerId = project.OwnerId;
        }

        Guid created;
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);
            ActionResult<EditTimelineResponse> result = await controller.CreateEditTimeline(
                projectId, new CreateEditTimelineRequest("Fresh", null), CancellationToken.None);
            created = result.Result.Should().BeOfType<ObjectResult>().Subject
                .Value.Should().BeOfType<EditTimelineResponse>().Subject.Id;
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelineSnapshot snapshot = await db.EditTimelineSnapshots.SingleAsync(s => s.TimelineId == created);
            snapshot.Revision.Should().Be(1);
            snapshot.DocumentJson.Should().Be((await db.EditTimelines.SingleAsync(t => t.Id == created)).DocumentJson);
        }
    }

    [Fact]
    public async Task Duplicate_CopiesTheCurrentDocument_AtRevisionOne_WithFreshIdAndItsOwnSnapshot()
    {
        string databaseName = NewDatabaseName();
        Guid projectId, timelineId, ownerId, seed = Guid.NewGuid();

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db);
            projectId = project.Id;
            ownerId = project.OwnerId;
            EditTimelineEntity source = await AddTimelineAsync(db, projectId, "Source", revision: 9);
            source.SeedStepResultId = seed;
            await db.SaveChangesAsync();
            timelineId = source.Id;
        }

        EditTimelineResponse copy;
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);
            ActionResult<EditTimelineResponse> result = await controller.DuplicateEditTimeline(
                projectId, timelineId, new DuplicateEditTimelineRequest(null), CancellationToken.None);
            ObjectResult created = result.Result.Should().BeOfType<ObjectResult>().Subject;
            created.StatusCode.Should().Be(201);
            copy = created.Value.Should().BeOfType<EditTimelineResponse>().Subject;
        }

        copy.Id.Should().NotBe(timelineId);
        copy.Revision.Should().Be(1);
        copy.Name.Should().Be("Source (copy)");
        copy.SeedStepResultId.Should().Be(seed);
        copy.Document.Id.Should().Be(copy.Id, "the document's own id follows the new row");
        copy.Document.ProjectId.Should().Be(projectId);

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            (await db.EditTimelines.SingleAsync(t => t.Id == timelineId)).Revision.Should().Be(9, "the source is untouched");
            (await db.EditTimelineSnapshots.SingleAsync(s => s.TimelineId == copy.Id)).Revision.Should().Be(1);
        }

        // A foreign/missing source is 404.
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);
            (await controller.DuplicateEditTimeline(projectId, Guid.NewGuid(), null, CancellationToken.None))
                .Result.Should().BeOfType<NotFoundResult>();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // harness
    // ──────────────────────────────────────────────────────────────────────────

    private static string NewDatabaseName() => Guid.NewGuid().ToString();

    private static InferenceApiDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static EditTimelinesController CreateController(InferenceApiDbContext db, Guid userId)
    {
        Mock<ICurrentUser> currentUser = new();
        currentUser.SetupGet(u => u.UserId).Returns(userId);
        currentUser.SetupGet(u => u.Email).Returns("test@example.com");
        currentUser.SetupGet(u => u.IsAuthenticated).Returns(true);
        currentUser.SetupGet(u => u.IsPlatformAdmin).Returns(false);
        currentUser.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(userId));
        currentUser.SetupGet(u => u.OrgRole).Returns(ReelBolt.Shared.Tenancy.OrganizationRole.Member);
        return new EditTimelinesController(
            db,
            currentUser.Object,
            TestTenancy.Access(db, currentUser.Object),
            new Mock<IFileStorageService>().Object,
            new Mock<ReelBolt.Inference.Api.Services.Workflows.IWorkflowExecutionService>().Object);
    }

    private static async Task<Project> AddProjectAsync(InferenceApiDbContext db)
    {
        Guid ownerId = Guid.NewGuid();
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project;
    }

    private static async Task<EditTimelineEntity> AddTimelineAsync(
        InferenceApiDbContext db, Guid projectId, string name, int revision)
    {
        Guid id = Guid.NewGuid();
        EditTimelineEntity timeline = new()
        {
            Id = id,
            ProjectId = projectId,
            Name = name,
            DocumentJson = JsonSerializer.Serialize(BlankDocument(projectId, id, name), DocumentJsonOptions),
            Revision = revision,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.EditTimelines.Add(timeline);
        await db.SaveChangesAsync();
        return timeline;
    }

    private static EditTimeline BlankDocument(Guid projectId, Guid timelineId, string name) =>
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
