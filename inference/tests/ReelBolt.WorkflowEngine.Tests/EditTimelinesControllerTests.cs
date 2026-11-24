#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Editing.Interchange;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="EditTimelinesController"/> — the four v2 edit-timeline endpoints.
///
/// <para>
/// The harness mirrors <c>TimelinesControllerTests</c> (EFCore.InMemory + a stubbed
/// <see cref="ICurrentUser"/>) and adds a stubbed <see cref="IFileStorageService"/> for the seeded
/// create path.
/// </para>
/// <para>
/// <b>The two failure tests prove the absence of a write, not merely a status code.</b> Both the
/// stale-revision (Review Focus) and the invalid-document case assert the response AND then re-read
/// the timeline through the API on a FRESH <c>DbContext</c> over the same in-memory database. A
/// fresh context is what makes the check meaningful: the context that served the request would
/// answer from its own identity map, so a mutated-but-unsaved entity would look written.
/// </para>
/// </summary>
public class EditTimelinesControllerTests
{
    /// <summary>
    /// The harness's stand-in for the production document encoding: Web defaults and NOTHING else.
    /// The enum encoding is owned by the enum TYPES — the five edit-v2 contract enums carry
    /// <c>[JsonConverter(typeof(EditTimelineEnumJsonConverter))]</c> on their declarations, so an
    /// options object with no converter at all is exactly what production passes.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately converter-free, and a converter here would be actively harmful.</b> An
    /// options-level converter takes precedence over a type-level attribute, so adding one (as this
    /// harness used to) would make the harness agree with production for the wrong reason while
    /// silently masking the attribute — the harness would pass even if the attribute were removed.
    /// </remarks>
    private static readonly JsonSerializerOptions DocumentJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The PRE-T-022 writer, held separately from <see cref="DocumentJsonOptions"/> on purpose: an
    /// explicit naming-policy-less <see cref="JsonStringEnumConverter"/> is what
    /// <c>EditTimelinesController.DocumentJsonOptions</c> used to carry, and it writes PASCALCASE
    /// (<c>"kind":"Video"</c>). Reproducing the old encoding here is the only way to seed a
    /// genuinely legacy <c>document_json</c> row and MEASURE how the current read path treats it.
    /// </summary>
    private static readonly JsonSerializerOptions LegacyPascalCaseDocumentJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };

    // ==========================================================================================
    // REVIEW FOCUS — stale baseRevision: 409 + currentRevision, and NOTHING is written.
    // ==========================================================================================

    [Fact]
    public async Task PatchEditTimeline_WithStaleBaseRevision_Returns409WithCurrentRevisionAndWritesNothing()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;

        // ---- Arrange: a timeline already at revision 3 -------------------------------
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;

            EditTimelineEntity timeline = await AddTimelineAsync(
                db, project.Id, "Original name", BlankDocument(project.Id, Guid.NewGuid(), "Original name"), revision: 3);
            timelineId = timeline.Id;
        }

        // ---- Act: a concurrent second writer submits a VALID document edited from
        //      revision 1, while the row has already advanced to 3. The document is valid
        //      on purpose: the stale revision is the only reason this request must fail.
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);
            EditTimeline staleSubmission = BlankDocument(projectId, timelineId, "STALE WRITER WON");
            PatchEditTimelineRequest request = new(
                BaseRevision: 1,
                Ops: JsonDocument.Parse("""[{"op":"trim","trackId":"V1"}]""").RootElement.Clone(),
                Document: staleSubmission);

            ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
                projectId, timelineId, request, CancellationToken.None);

            ConflictObjectResult conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
            conflict.StatusCode.Should().Be(409);
            conflict.Value.Should().BeOfType<EditTimelineConflictResponse>().Subject
                .CurrentRevision.Should().Be(3, "the 409 body must carry the revision the caller has to rebase onto");
        }

        // ---- Assert the no-write, through a FOLLOW-UP GET on a fresh context ---------
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController reader = CreateController(db, ownerId);

            ActionResult<EditTimelineResponse> followUp = await reader.GetEditTimeline(
                projectId, timelineId, CancellationToken.None);

            OkObjectResult ok = followUp.Result.Should().BeOfType<OkObjectResult>().Subject;
            EditTimelineResponse body = ok.Value.Should().BeOfType<EditTimelineResponse>().Subject;

            body.Revision.Should().Be(3, "a rejected PATCH must not advance the revision");
            body.Document.Name.Should().Be("Original name", "the stale writer's document must not have landed");
            body.Document.Name.Should().NotBe("STALE WRITER WON");

            // No side rows either: a rejected PATCH appends no op and writes no snapshot.
            (await db.EditTimelineOps.CountAsync()).Should().Be(0);
            (await db.EditTimelineSnapshots.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task PatchEditTimeline_WithStaleBaseRevision_DoesNotDisturbTheStoredDocumentJson()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;
        string storedJsonBefore;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;

            EditTimelineEntity timeline = await AddTimelineAsync(
                db, project.Id, "Kept", BlankDocument(project.Id, Guid.NewGuid(), "Kept"), revision: 2);
            timelineId = timeline.Id;
            storedJsonBefore = timeline.DocumentJson;
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            await controller.PatchEditTimeline(
                projectId,
                timelineId,
                new PatchEditTimelineRequest(99, null, BlankDocument(projectId, timelineId, "Nope")),
                CancellationToken.None);
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelineEntity reloaded = await db.EditTimelines.AsNoTracking()
                .SingleAsync(t => t.Id == timelineId);

            reloaded.Revision.Should().Be(2);
            reloaded.DocumentJson.Should().Be(storedJsonBefore, "the stored bytes must be untouched");
        }
    }

    // ==========================================================================================
    // PATCH — invalid document: 400 with the violation list, and NOTHING is written.
    // ==========================================================================================

    [Fact]
    public async Task PatchEditTimeline_WithInvalidDocument_Returns400WithViolationsAndWritesNothing()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;

            EditTimelineEntity timeline = await AddTimelineAsync(
                db, project.Id, "Original name", BlankDocument(project.Id, Guid.NewGuid(), "Original name"), revision: 4);
            timelineId = timeline.Id;
        }

        // A MediaClip whose AssetRef names a project file that does not exist — an unresolvable
        // asset identity, which the validator rejects (ownership is never assumed from absence).
        Guid unknownFileId = Guid.NewGuid();
        EditTimeline invalid = DocumentWithMediaClip(
            projectId, timelineId, "INVALID SUBMISSION", unknownFileId, recordIn: 0, recordOut: 30);

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
                projectId, timelineId, new PatchEditTimelineRequest(4, null, invalid), CancellationToken.None);

            BadRequestObjectResult badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
            badRequest.StatusCode.Should().Be(400);

            List<EditTimelineViolation> violations = badRequest.Value
                .Should().BeAssignableTo<IReadOnlyList<EditTimelineViolation>>().Subject.ToList();

            violations.Should().NotBeEmpty("a 400 must tell the caller what is wrong");
            violations.Should().ContainSingle(v => v.Path == "tracks[0].items[0].assetRef");
            violations.Single(v => v.Path == "tracks[0].items[0].assetRef").Message
                .Should().Contain(unknownFileId.ToString());
        }

        // Follow-up GET on a fresh context: the invalid document never landed.
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController reader = CreateController(db, ownerId);

            OkObjectResult ok = (await reader.GetEditTimeline(projectId, timelineId, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>().Subject;
            EditTimelineResponse body = ok.Value.Should().BeOfType<EditTimelineResponse>().Subject;

            body.Revision.Should().Be(4, "a rejected PATCH must not advance the revision");
            body.Document.Name.Should().Be("Original name");
            body.Document.Tracks[0].Items.Should().BeEmpty("the rejected document must not have been persisted");

            (await db.EditTimelineOps.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task PatchEditTimeline_RejectsAForeignProjectAssetRef_WithoutWriting()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;
        Guid foreignFileId;
        Guid otherProjectId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Mine");
            Project other = await AddProjectAsync(db, "Somebody else's");
            ownerId = project.OwnerId;
            projectId = project.Id;
            otherProjectId = other.Id;

            foreignFileId = (await AddProjectFileAsync(db, other.Id, "secret.mp4")).Id;

            EditTimelineEntity timeline = await AddTimelineAsync(
                db, project.Id, "Mine", BlankDocument(project.Id, Guid.NewGuid(), "Mine"), revision: 1);
            timelineId = timeline.Id;
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            // The file EXISTS — it just belongs to another project. The ownership map must resolve
            // its REAL owner (which is why the query carries no project filter) and reject it.
            EditTimeline leaked = DocumentWithMediaClip(
                projectId, timelineId, "Leak", foreignFileId, recordIn: 0, recordOut: 30);

            ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
                projectId, timelineId, new PatchEditTimelineRequest(1, null, leaked), CancellationToken.None);

            BadRequestObjectResult badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
            List<EditTimelineViolation> violations = badRequest.Value
                .Should().BeAssignableTo<IReadOnlyList<EditTimelineViolation>>().Subject.ToList();

            violations.Should().Contain(v =>
                v.Path == "tracks[0].items[0].assetRef" && v.Message.Contains("not found in this project")
                    && !v.Message.Contains("belongs to project") && !v.Message.Contains(otherProjectId.ToString()));
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelineEntity reloaded = await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timelineId);
            reloaded.Revision.Should().Be(1);
            (await db.EditTimelineOps.CountAsync()).Should().Be(0);
        }
    }

    // ==========================================================================================
    // Provenance ownership wiring — QA condition 1 / F6.
    //
    // BuildValidationContextAsync collects an item's Provenance.StepResultId into the same
    // step-result ownership map an AssetRefKind.StepResult uses. QA deleted exactly that collection
    // block, re-ran the suite, and it stayed at 2092/0/1: nothing pinned the wiring. These three
    // tests are that pin, at the controller (the wiring, not the transport, is what was missing), and
    // the third walks the real seeded path QA named.
    // ==========================================================================================

    [Fact]
    public async Task ImportEditTimeline_ForeignAndUnknownProvenanceStepResultIds_Give400sWithEqualMessagesAndNoOwnerGuid()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid ownerId;
        Guid foreignProjectId;
        Guid foreignStepResultId;
        Guid ownedFileId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Mine");
            Project other = await AddProjectAsync(db, "Somebody else's");
            ownerId = project.OwnerId;
            projectId = project.Id;
            foreignProjectId = other.Id;
            WorkflowExecution foreignExecution = await AddExecutionAsync(db, other.Id);
            foreignStepResultId = (await AddStepResultAsync(db, foreignExecution, timelineStorageKey: null)).Id;
            ownedFileId = (await AddProjectFileAsync(db, project.Id, "mine.mp4")).Id;
        }

        async Task<List<EditTimelineViolation>> ImportWithProvenance(Guid stepResultId)
        {
            EditTimeline doc = DocumentWithMediaClip(projectId, Guid.NewGuid(), "Imp", ownedFileId, 0, 30);
            MediaClip clip = new(
                Id: Guid.NewGuid(),
                Name: "clip",
                RecordIn: 0,
                RecordOut: 30,
                AssetRef: new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: ownedFileId),
                Provenance: new Provenance(ProvenanceOrigin.Generated, StepResultId: stepResultId));
            doc = doc with { Tracks = [doc.Tracks[0] with { Items = [clip] }, doc.Tracks[1]] };
            JsonElement otio = JsonDocument.Parse(TimelineInterchange.WriteOtio(doc)).RootElement.Clone();

            using InferenceApiDbContext db = CreateContext(databaseName);
            EditTimelinesController controller = CreateController(db, ownerId);
            ActionResult<EditTimelineResponse> result = await controller.ImportEditTimeline(
                projectId, new ImportEditTimelineRequest("Imp", otio), CancellationToken.None);
            BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
            return bad.Value.Should().BeAssignableTo<IReadOnlyList<EditTimelineViolation>>().Subject.ToList();
        }

        Guid unknownId = Guid.NewGuid();
        List<EditTimelineViolation> foreign = await ImportWithProvenance(foreignStepResultId);
        List<EditTimelineViolation> unknown = await ImportWithProvenance(unknownId);

        foreign.Should().ContainSingle();
        unknown.Should().ContainSingle();
        foreign[0].Message.Replace(foreignStepResultId.ToString(), "ID")
            .Should().Be(unknown[0].Message.Replace(unknownId.ToString(), "ID"));
        JsonSerializer.Serialize(foreign).Should().NotContain(foreignProjectId.ToString()).And.NotContain("belongs to project");
    }

    [Fact]
    public async Task PatchEditTimeline_WithAnOwnedProvenanceStepResultId_IsAcceptedAndAdvancesTheRevision()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;
        Guid stepResultId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;

            // The step result the item's provenance will name, IN this project — so the correct
            // answer is "resolvable and owned", and the only thing that can make it not so is the
            // collection block being absent.
            WorkflowExecution execution = await AddExecutionAsync(db, project.Id);
            stepResultId = (await AddStepResultAsync(db, execution, timelineStorageKey: null)).Id;

            EditTimelineEntity timeline = await AddTimelineAsync(
                db, project.Id, "Provenance", BlankDocument(project.Id, Guid.NewGuid(), "Provenance"), revision: 1);
            timelineId = timeline.Id;
        }

        EditTimeline submitted = DocumentWithProvenance(
            projectId,
            timelineId,
            "Provenance",
            new Provenance(ProvenanceOrigin.Generated, StepResultId: stepResultId));

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
                projectId, timelineId, new PatchEditTimelineRequest(1, null, submitted), CancellationToken.None);

            OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            EditTimelineResponse body = ok.Value.Should().BeOfType<EditTimelineResponse>().Subject;

            body.Revision.Should().Be(
                2,
                "an OWNED provenance step result id resolves through the map, so nothing blocks the write");
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelineEntity reloaded = await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timelineId);
            reloaded.Revision.Should().Be(2);
            (await db.EditTimelineOps.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task PatchEditTimeline_RejectsForeignAndUnknownProvenanceStepResultIdsWithA400AndWritesNothing()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;
        Guid foreignProjectId;
        Guid foreignStepResultId;
        Guid unknownStepResultId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Mine");
            Project other = await AddProjectAsync(db, "Somebody else's");
            ownerId = project.OwnerId;
            projectId = project.Id;
            foreignProjectId = other.Id;

            WorkflowExecution foreignExecution = await AddExecutionAsync(db, other.Id);
            foreignStepResultId = (await AddStepResultAsync(db, foreignExecution, timelineStorageKey: null)).Id;

            EditTimelineEntity timeline = await AddTimelineAsync(
                db, project.Id, "Mine", BlankDocument(project.Id, Guid.NewGuid(), "Mine"), revision: 1);
            timelineId = timeline.Id;
        }

        unknownStepResultId = Guid.NewGuid();

        // TWO defects on TWO tracks, each reported under its own path, so one violation cannot stand
        // in for the other: track 0 names a step result that EXISTS and belongs to another project,
        // track 1 names one that exists nowhere. A map that only ever held "absent" would report both
        // as unknown; a map without the provenance ids at all would report both as unknown too — so
        // requiring the FOREIGN one to name its real owner is what proves the query really resolves
        // owners rather than defaulting.
        EditTimeline blank = BlankDocument(projectId, timelineId, "Both");
        EditTimeline submitted = blank with
        {
            Tracks =
            [
                blank.Tracks[0] with
                {
                    Items =
                    [
                        new TitleClip(
                            Id: Guid.NewGuid(),
                            Name: "foreign",
                            RecordIn: 0,
                            RecordOut: 30,
                            Text: "foreign",
                            Provenance: new Provenance(ProvenanceOrigin.Generated, StepResultId: foreignStepResultId))
                    ]
                },
                blank.Tracks[1] with
                {
                    Items =
                    [
                        new TitleClip(
                            Id: Guid.NewGuid(),
                            Name: "unknown",
                            RecordIn: 0,
                            RecordOut: 30,
                            Text: "unknown",
                            Provenance: new Provenance(ProvenanceOrigin.Generated, StepResultId: unknownStepResultId))
                    ]
                }
            ]
        };

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
                projectId, timelineId, new PatchEditTimelineRequest(1, null, submitted), CancellationToken.None);

            BadRequestObjectResult badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
            List<EditTimelineViolation> violations = badRequest.Value
                .Should().BeAssignableTo<IReadOnlyList<EditTimelineViolation>>().Subject.ToList();

            // Single() on each path: present exactly once, or the test fails.
            violations.Single(v => v.Path == "tracks[0].items[0].provenance.stepResultId").Message
                .Should().Contain("not found in this project")
                .And.NotContain(foreignProjectId.ToString(), "the owning project must never be revealed")
                .And.NotContain("belongs to project");

            violations.Single(v => v.Path == "tracks[1].items[0].provenance.stepResultId").Message
                .Should().Contain("not found in this project");

            // Foreign and unknown produce the same text (ids masked) and no body leaks the owner.
            string foreignMsg = violations.Single(v => v.Path == "tracks[0].items[0].provenance.stepResultId").Message;
            string unknownMsg = violations.Single(v => v.Path == "tracks[1].items[0].provenance.stepResultId").Message;
            foreignMsg.Replace(foreignStepResultId.ToString(), "ID")
                .Should().Be(unknownMsg.Replace(unknownStepResultId.ToString(), "ID"));
            System.Text.Json.JsonSerializer.Serialize(violations).Should().NotContain(foreignProjectId.ToString());
        }

        // A FRESH context: the request's own context would answer from its identity map.
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelineEntity reloaded = await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timelineId);
            reloaded.Revision.Should().Be(1, "a rejected PATCH must not advance the revision");
            (await db.EditTimelineOps.CountAsync()).Should().Be(0, "no row in edit_timeline_ops");
        }
    }

    /// <summary>
    /// The REAL path QA condition 1 named: a document the seeder produced from a manifest that
    /// carries a <c>StepResultId</c>, PATCHed straight back. <see cref="EditTimelineSeeder"/> copies
    /// the manifest item's <c>StepResultId</c> onto <see cref="Provenance.StepResultId"/>, and that
    /// step result is necessarily one this project owns — so the controller must resolve it and must
    /// NOT report it as unknown.
    /// </summary>
    [Fact]
    public async Task PatchEditTimeline_OfASeededDocumentWithProvenance_IsAcceptedAndNotReportedAsUnknown()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid ownerId;
        Guid stepResultId;
        EditTimeline seeded;
        int revision;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;

            ProjectFile videoFile = await AddProjectFileAsync(db, project.Id, "shoot.mp4", storageKey: "sources/shoot.mp4");
            ProjectFile musicFile = await AddProjectFileAsync(db, project.Id, "bed.mp3", storageKey: "audio/xyz.mp3");

            WorkflowExecution execution = await AddExecutionAsync(db, project.Id);
            string manifestKey = $"projects/{project.Id}/outputFiles/manifest.json";
            WorkflowStepResult stepResult = await AddStepResultAsync(db, execution, manifestKey);
            stepResultId = stepResult.Id;

            TimelineManifest manifest = BuildManifest(
                project.Id,
                execution.Id,
                videoFile.StorageKey,
                musicFile.OriginalFileName,
                itemStepResultId: stepResultId);

            IFileStorageService storage = StubStorage(
                project.Id, manifestKey, JsonSerializer.Serialize(manifest, DocumentJsonOptions));

            EditTimelinesController creator = CreateController(db, project.OwnerId, storage);

            ActionResult<EditTimelineResponse> created = await creator.CreateEditTimeline(
                project.Id, new CreateEditTimelineRequest("Seeded", stepResult.Id), CancellationToken.None);

            ObjectResult createdResult = created.Result.Should().BeOfType<ObjectResult>().Subject;
            createdResult.StatusCode.Should().Be(
                201, "the seeded create must succeed before anything about its provenance can be tested");
            EditTimelineResponse body = createdResult.Value.Should().BeOfType<EditTimelineResponse>().Subject;

            // The premise: the seeder really did carry the manifest's StepResultId onto the mapped
            // item, so the document under test genuinely has a provenance id to resolve.
            Track video = body.Document.Tracks.Single(t => t.Id == EditTimelineSeeder.TrackId("V1"));
            video.Items.Single().Provenance!.StepResultId.Should().Be(
                stepResultId, "the seeder copies the manifest's StepResultId onto Provenance.StepResultId");

            seeded = body.Document;
            revision = body.Revision;
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
                projectId, seeded.Id, new PatchEditTimelineRequest(revision, null, seeded), CancellationToken.None);

            // The positive half FIRST, so a regression that turns the seeded provenance into an
            // unknown-id rejection names itself instead of only moving the status code.
            if (result.Result is BadRequestObjectResult rejected)
            {
                List<EditTimelineViolation> violations = rejected.Value
                    .Should().BeAssignableTo<IReadOnlyList<EditTimelineViolation>>().Subject.ToList();

                violations.Should().NotContain(
                    v => v.Message.Contains("not found in this project", StringComparison.OrdinalIgnoreCase),
                    "an id copied from THIS project's own step result is not unknown to anything");
            }

            OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>(
                "a document seeded from one of this project's own step results must survive a PATCH; "
                + "classifying its provenance id as unknown is the regression under test").Subject;

            EditTimelineResponse patched = ok.Value.Should().BeOfType<EditTimelineResponse>().Subject;
            patched.Revision.Should().Be(revision + 1);
        }
    }

    // ==========================================================================================
    // PATCH — happy path, ops, snapshots.
    // ==========================================================================================

    [Fact]
    public async Task PatchEditTimeline_OnHappyPath_AdvancesTheRevisionAndAppendsOneOpRow()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        ProjectFile file = await AddProjectFileAsync(db, project.Id, "clip.mp4");
        EditTimelineEntity timeline = await AddTimelineAsync(
            db, project.Id, "Cut", BlankDocument(project.Id, Guid.NewGuid(), "Cut"), revision: 1);

        EditTimelinesController controller = CreateController(db, project.OwnerId);

        EditTimeline submitted = DocumentWithMediaClip(
            project.Id, timeline.Id, "Cut v2", file.Id, recordIn: 0, recordOut: 30);
        JsonElement ops = JsonDocument.Parse("""[{"op":"addClip","id":"c1"}]""").RootElement.Clone();

        ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
            project.Id, timeline.Id, new PatchEditTimelineRequest(1, ops, submitted), CancellationToken.None);

        ObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        EditTimelineResponse body = ok.Value.Should().BeOfType<EditTimelineResponse>().Subject;
        body.Revision.Should().Be(2);
        body.Document.Name.Should().Be("Cut v2");

        EditTimelineOp op = await db.EditTimelineOps.AsNoTracking().SingleAsync();
        op.TimelineId.Should().Be(timeline.Id);
        op.Revision.Should().Be(2);
        op.OpsJson.Should().Be("""[{"op":"addClip","id":"c1"}]""", "ops are stored verbatim, never interpreted");

        // Revision 2 is not a snapshot revision.
        (await db.EditTimelineSnapshots.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PatchEditTimeline_AtEveryTwentiethRevision_WritesExactlyOneSnapshot()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        EditTimelineEntity timeline = await AddTimelineAsync(
            db, project.Id, "Long runner", BlankDocument(project.Id, Guid.NewGuid(), "Long runner"), revision: 19);

        EditTimelinesController controller = CreateController(db, project.OwnerId);

        ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
            project.Id,
            timeline.Id,
            new PatchEditTimelineRequest(
                19, JsonDocument.Parse("[]").RootElement.Clone(), BlankDocument(project.Id, timeline.Id, "At twenty")),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<EditTimelineResponse>().Subject.Revision.Should().Be(20);

        EditTimelineSnapshot snapshot = await db.EditTimelineSnapshots.AsNoTracking().SingleAsync();
        snapshot.TimelineId.Should().Be(timeline.Id);
        snapshot.Revision.Should().Be(20);
        snapshot.DocumentJson.Should().Be(
            (await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timeline.Id)).DocumentJson,
            "the snapshot captures the same document the row now stores");
    }

    [Fact]
    public async Task PatchEditTimeline_Returns404_ForATimelineInAnotherProject()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Mine");
        Project other = await AddProjectAsync(db, "Theirs");
        EditTimelineEntity foreign = await AddTimelineAsync(
            db, other.Id, "Theirs", BlankDocument(other.Id, Guid.NewGuid(), "Theirs"), revision: 1);

        EditTimelinesController controller = CreateController(db, project.OwnerId);

        ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
            project.Id,
            foreign.Id,
            new PatchEditTimelineRequest(1, null, BlankDocument(project.Id, foreign.Id, "Hijack")),
            CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>("cross-project access is 404, never 403 or 409");

        EditTimelineEntity untouched = await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == foreign.Id);
        untouched.Revision.Should().Be(1);
        untouched.Name.Should().Be("Theirs");
    }

    [Fact]
    public async Task PatchEditTimeline_Returns404_ForASoftDeletedTimeline()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        EditTimelineEntity timeline = await AddTimelineAsync(
            db, project.Id, "Deleted", BlankDocument(project.Id, Guid.NewGuid(), "Deleted"), revision: 1);
        timeline.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        EditTimelinesController controller = CreateController(db, project.OwnerId);

        ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
            project.Id, timeline.Id, new PatchEditTimelineRequest(1, null, null), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task PatchEditTimeline_Returns413_WhenTheSerializedDocumentExceedsTheDocumentedCeiling()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        EditTimelineEntity timeline = await AddTimelineAsync(
            db, project.Id, "Big", BlankDocument(project.Id, Guid.NewGuid(), "Big"), revision: 1);

        EditTimelinesController controller = CreateController(db, project.OwnerId);

        // One TitleClip whose text is past the ceiling, so the guard is exercised by real content
        // rather than by a mock: MaxDocumentBytes is public so the boundary cannot drift.
        string oversizedText = new('a', EditTimelinesController.MaxDocumentBytes + 1);
        EditTimeline oversized = DocumentWithTitleClip(project.Id, timeline.Id, "Big", oversizedText);

        ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
            project.Id, timeline.Id, new PatchEditTimelineRequest(1, null, oversized), CancellationToken.None);

        ObjectResult tooLarge = result.Result.Should().BeOfType<ObjectResult>().Subject;
        tooLarge.StatusCode.Should().Be(413);

        EditTimelineEntity reloaded = await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timeline.Id);
        reloaded.Revision.Should().Be(1);
        (await db.EditTimelineOps.CountAsync()).Should().Be(0);
    }

    // ==========================================================================================
    // POST — blank and seeded creation.
    // ==========================================================================================

    [Fact]
    public async Task CreateEditTimeline_BlankWithChosenSettings_StoresThem()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        EditTimelinesController controller = CreateController(db, project.OwnerId);
        EditTimelineSettings chosen = new(1080, 1920, new Rational(24, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

        ActionResult<EditTimelineResponse> result = await controller.CreateEditTimeline(
            project.Id, new CreateEditTimelineRequest("Vertical", null, chosen), CancellationToken.None);

        ObjectResult created = result.Result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(201);
        EditTimelineResponse body = created.Value.Should().BeOfType<EditTimelineResponse>().Subject;
        body.Document.Settings.Width.Should().Be(1080);
        body.Document.Settings.Height.Should().Be(1920);
        body.Document.Settings.Rate.Should().Be(new Rational(24, 1));
    }

    [Fact]
    public async Task CreateEditTimeline_BlankWithInvalidSettings_IsA400()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        EditTimelinesController controller = CreateController(db, project.OwnerId);
        EditTimelineSettings bad = new(0, 0, new Rational(0, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

        ActionResult<EditTimelineResponse> result = await controller.CreateEditTimeline(
            project.Id, new CreateEditTimelineRequest("Bad", null, bad), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.EditTimelines.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateEditTimeline_WithoutASeed_CreatesTheDocumentedBlankTimeline()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        EditTimelinesController controller = CreateController(db, project.OwnerId);

        ActionResult<EditTimelineResponse> result = await controller.CreateEditTimeline(
            project.Id, new CreateEditTimelineRequest("My timeline", null), CancellationToken.None);

        ObjectResult created = result.Result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(201);

        EditTimelineResponse body = created.Value.Should().BeOfType<EditTimelineResponse>().Subject;
        body.Name.Should().Be("My timeline");
        body.ProjectId.Should().Be(project.Id);
        body.Revision.Should().Be(1);
        body.SeedStepResultId.Should().BeNull();

        EditTimeline document = body.Document;
        document.SchemaVersion.Should().Be(2);
        document.Id.Should().Be(body.Id);
        document.ProjectId.Should().Be(project.Id);

        document.Settings.Width.Should().Be(1920);
        document.Settings.Height.Should().Be(1080);
        document.Settings.Rate.Should().Be(new Rational(30, 1));
        document.Settings.SampleRate.Should().Be(48000);
        document.Settings.PixelAspect.Should().Be(new Rational(1, 1));
        document.Settings.AspectPolicy.Should().Be(AspectPolicy.Fit);
        document.Settings.StartTimecodeFrames.Should().Be(0);

        document.Tracks.Should().HaveCount(2);
        document.Tracks[0].Kind.Should().Be(TrackKind.Video);
        document.Tracks[0].Name.Should().Be("Video");
        document.Tracks[0].Items.Should().BeEmpty();
        document.Tracks[1].Kind.Should().Be(TrackKind.Audio);
        document.Tracks[1].Name.Should().Be("Audio");
        document.Tracks[1].Items.Should().BeEmpty();

        // Blank track ids come from the seeder's own label → id function, so a blank timeline and a
        // seeded one address the video track identically.
        document.Tracks[0].Id.Should().Be(EditTimelineSeeder.TrackId("V1"));

        // Persisted, and readable back through GET with the same document.
        EditTimelinesController reader = CreateController(db, project.OwnerId);
        OkObjectResult ok = (await reader.GetEditTimeline(project.Id, body.Id, CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject;
        EditTimelineResponse reread = ok.Value.Should().BeOfType<EditTimelineResponse>().Subject;
        reread.Revision.Should().Be(1);
        reread.Name.Should().Be("My timeline");
        reread.Document.Id.Should().Be(document.Id);
        reread.Document.Name.Should().Be(document.Name);
        reread.Document.Settings.Should().Be(document.Settings, "the stored settings round-trip through jsonb unchanged");
        reread.Document.Tracks.Should().HaveCount(2);
        reread.Document.Tracks[0].Id.Should().Be(EditTimelineSeeder.TrackId("V1"));
        reread.Document.Tracks[1].Id.Should().Be(EditTimelineSeeder.TrackId("A1"));
    }

    [Fact]
    public async Task CreateEditTimeline_FromASeedStepResult_MapsTheManifestAndResolvesRealAssets()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");

        // Two files, matched by the two different identifier kinds a manifest carries: a cut names
        // its source by storage key, a music bed names its track by file name.
        ProjectFile videoFile = await AddProjectFileAsync(db, project.Id, "shoot.mp4", storageKey: "sources/shoot.mp4");
        ProjectFile musicFile = await AddProjectFileAsync(db, project.Id, "bed.mp3", storageKey: "audio/xyz.mp3");

        WorkflowExecution execution = await AddExecutionAsync(db, project.Id);
        string manifestKey = $"projects/{project.Id}/outputFiles/manifest.json";
        WorkflowStepResult stepResult = await AddStepResultAsync(db, execution, manifestKey);

        TimelineManifest manifest = BuildManifest(project.Id, execution.Id, videoFile.StorageKey, musicFile.OriginalFileName);
        string manifestJson = JsonSerializer.Serialize(manifest, DocumentJsonOptions);
        IFileStorageService storage = StubStorage(project.Id, manifestKey, manifestJson);

        EditTimelinesController controller = CreateController(db, project.OwnerId, storage);

        ActionResult<EditTimelineResponse> result = await controller.CreateEditTimeline(
            project.Id, new CreateEditTimelineRequest("Seeded", stepResult.Id), CancellationToken.None);

        ObjectResult created = result.Result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(201);
        EditTimelineResponse body = created.Value.Should().BeOfType<EditTimelineResponse>().Subject;

        body.Revision.Should().Be(1);
        body.SeedStepResultId.Should().Be(stepResult.Id);
        body.Name.Should().Be("Seeded", "the caller's name wins over the seeder's manifest-derived one");
        body.Document.ProjectId.Should().Be(project.Id);
        body.Document.Seed!.ExecutionId.Should().Be(execution.Id);
        body.Document.Settings.Rate.Should().Be(new Rational(30, 1));

        // At least one REAL mapped item: the cut resolved its source storage key to a ProjectFile.
        Track video = body.Document.Tracks.Single(t => t.Id == EditTimelineSeeder.TrackId("V1"));
        MediaClip cut = video.Items.Should().ContainSingle().Subject.Should().BeOfType<MediaClip>().Subject;
        cut.AssetRef.Kind.Should().Be(AssetRefKind.ProjectFile);
        cut.AssetRef.ProjectFileId.Should().Be(videoFile.Id);
        cut.RecordOut.Should().Be(120, "4s at 30fps");

        // And the audio bed resolved through OriginalFileName, the manifest's other identifier kind.
        Track audio = body.Document.Tracks.Single(t => t.Id == EditTimelineSeeder.TrackId("A3"));
        MediaClip music = audio.Items.Should().ContainSingle().Subject.Should().BeOfType<MediaClip>().Subject;
        music.AssetRef.ProjectFileId.Should().Be(musicFile.Id);

        // No probe says the source has sound, so no camera audio is assumed.
        body.Document.Tracks.Should().NotContain(t => t.Id == EditTimelineSeeder.TrackId("A1"));
    }

    [Fact]
    public async Task CreateEditTimeline_WhenTheSeedStillFailsValidation_ReturnsTheRedactedRuleInThe500Body()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        ProjectFile videoFile = await AddProjectFileAsync(db, project.Id, "shoot.mp4", storageKey: "sources/shoot.mp4");

        // The ingest probe knows the source is one second long, while the manifest's single cut
        // claims four: the seeder's own output provably runs past its source, so BOTH the seeder and
        // the validator are exercised and the controller takes its 500 branch.
        db.MediaDerivatives.Add(new MediaDerivative
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            SourceProjectFileId = videoFile.Id,
            SourceStorageKey = videoFile.StorageKey,
            Kind = MediaDerivativeKind.Proxy,
            StorageKey = "derivatives/proxy.mp4",
            CacheKey = "proxy-cache-key",
            SourceProbeJson = new MediaSourceProbe(
                1.0, 30, 1, 1920, 1080, "h264", null, "mp4", null, null, null, HasAudio: false).ToJson()
        });
        await db.SaveChangesAsync();

        WorkflowExecution execution = await AddExecutionAsync(db, project.Id);
        string manifestKey = $"projects/{project.Id}/outputFiles/manifest.json";
        WorkflowStepResult stepResult = await AddStepResultAsync(db, execution, manifestKey);

        // The label is a customer caption and must never reach the response; the rule must.
        TimelineManifest manifest = new(
            Version: 1,
            ProjectId: project.Id,
            ExecutionId: execution.Id,
            OutputVideo: new TimelineOutputVideo(1920, 1080, 30, 4, "renders/out.mp4"),
            Sources: [new TimelineSource(0, videoFile.StorageKey, 30, 1920, 1080, 30)],
            Tracks:
            [
                new TimelineTrack("track-video", "video", "Video", 0,
                [
                    new TimelineItem(
                        Id: "cut-1",
                        TimelineStartSec: 0,
                        TimelineEndSec: 4,
                        SourceIndex: 0,
                        SourceStartSec: 0,
                        SourceEndSec: 4,
                        Label: "Interview with \"Jane\"")
                ])
            ]);

        IFileStorageService storage = StubStorage(
            project.Id, manifestKey, JsonSerializer.Serialize(manifest, DocumentJsonOptions));

        ActionResult<EditTimelineResponse> result = await CreateController(db, project.OwnerId, storage)
            .CreateEditTimeline(project.Id, new CreateEditTimelineRequest("Seeded", stepResult.Id), CancellationToken.None);

        ObjectResult failed = result.Result.Should().BeOfType<ObjectResult>().Subject;
        failed.StatusCode.Should().Be(500, "a server-built document that fails its own validator is a server-side defect");
        EditTimelineSeedInvalidResponse body =
            failed.Value.Should().BeOfType<EditTimelineSeedInvalidResponse>().Subject;

        body.Violations.Should().NotBeEmpty("the rule must be named, not dropped");
        body.Violations.Should().Contain(v =>
            v.Path.EndsWith(".sourceIn", StringComparison.Ordinal) &&
            v.Message.Contains("runs past the end of its source", StringComparison.Ordinal) &&
            v.Message.Contains("\"<clip>\"", StringComparison.Ordinal));
        body.Violations.Should().OnlyContain(v => !v.Message.Contains("Jane", StringComparison.Ordinal),
            "the customer's caption is redacted from every violation");
        body.Message.Should().StartWith(EditTimelinesController.SeedFailedMessage);
        body.Message.Should().Contain("runs past the end of its source",
            "the message names the first rule in plain words");
    }

    [Fact]
    public async Task CreateEditTimeline_FromASeed_GivesACutOfASourceWithStoredAudioItsCameraAudio()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        ProjectFile videoFile = await AddProjectFileAsync(db, project.Id, "shoot.mp4", storageKey: "sources/shoot.mp4");
        ProjectFile musicFile = await AddProjectFileAsync(db, project.Id, "bed.mp3", storageKey: "audio/xyz.mp3");

        // The probe the derivative run stored for the source at ingest says it has an audio stream.
        db.MediaDerivatives.Add(new MediaDerivative
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            SourceProjectFileId = videoFile.Id,
            SourceStorageKey = videoFile.StorageKey,
            Kind = MediaDerivativeKind.Proxy,
            StorageKey = "derivatives/proxy.mp4",
            CacheKey = "proxy-cache-key",
            SourceProbeJson = new MediaSourceProbe(
                4, 30, 1, 1920, 1080, "h264", "aac", "mp4", 2, 48000, null, HasAudio: true).ToJson()
        });
        await db.SaveChangesAsync();

        WorkflowExecution execution = await AddExecutionAsync(db, project.Id);
        string manifestKey = $"projects/{project.Id}/outputFiles/manifest.json";
        WorkflowStepResult stepResult = await AddStepResultAsync(db, execution, manifestKey);
        TimelineManifest manifest = BuildManifest(project.Id, execution.Id, videoFile.StorageKey, musicFile.OriginalFileName);
        IFileStorageService storage = StubStorage(project.Id, manifestKey, JsonSerializer.Serialize(manifest, DocumentJsonOptions));

        ActionResult<EditTimelineResponse> result = await CreateController(db, project.OwnerId, storage)
            .CreateEditTimeline(project.Id, new CreateEditTimelineRequest("Seeded", stepResult.Id), CancellationToken.None);

        EditTimelineResponse body = result.Result.Should().BeOfType<ObjectResult>().Subject
            .Value.Should().BeOfType<EditTimelineResponse>().Subject;
        Track cameraAudio = body.Document.Tracks.Single(t => t.Id == EditTimelineSeeder.TrackId("A1"));
        cameraAudio.Kind.Should().Be(TrackKind.Audio);
        MediaClip companion = cameraAudio.Items.Should().ContainSingle().Subject.Should().BeOfType<MediaClip>().Subject;
        companion.AssetRef.ProjectFileId.Should().Be(videoFile.Id);
        companion.RecordOut.Should().Be(120);
    }

    [Theory]
    [InlineData("aac", true)]
    [InlineData(null, false)]
    public async Task CreateEditTimeline_FromASeed_AnUnprobedSourceFallsBackToTheAnalysisArtifactsAudioFacts(
        string? analysedAudioCodec, bool expectCameraAudio)
    {
        // QA C-323-1: no derivative run has probed the source yet, so the VideoAnalyze artifact of
        // the seed's own execution decides. A named audio codec is sound; a named video codec with
        // no audio codec is a silent file.
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        ProjectFile videoFile = await AddProjectFileAsync(db, project.Id, "shoot.mp4", storageKey: "sources/shoot.mp4");
        ProjectFile musicFile = await AddProjectFileAsync(db, project.Id, "bed.mp3", storageKey: "audio/xyz.mp3");

        WorkflowExecution execution = await AddExecutionAsync(db, project.Id);
        string artifactKey = $"projects/{project.Id:D}/agentFiles/video-analysis/{execution.Id:D}/step-1-analysis.json";
        WorkflowStepResult analysis = await AddStepResultAsync(
            db, execution, timelineStorageKey: null, StepType.VideoAnalyze);
        analysis.ArtifactStorageKey = artifactKey;
        await db.SaveChangesAsync();

        string manifestKey = $"projects/{project.Id}/outputFiles/manifest.json";
        WorkflowStepResult stepResult = await AddStepResultAsync(db, execution, manifestKey);
        TimelineManifest manifest = BuildManifest(project.Id, execution.Id, videoFile.StorageKey, musicFile.OriginalFileName);

        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.DownloadAsync(project.Id, manifestKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, DocumentJsonOptions))));
        storage.Setup(s => s.DownloadAsync(project.Id, artifactKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(
                AnalysisArtifactJson(videoFile.StorageKey, "h264", analysedAudioCodec))));

        ActionResult<EditTimelineResponse> result = await CreateController(db, project.OwnerId, storage.Object)
            .CreateEditTimeline(project.Id, new CreateEditTimelineRequest("Seeded", stepResult.Id), CancellationToken.None);

        EditTimelineResponse body = result.Result.Should().BeOfType<ObjectResult>().Subject
            .Value.Should().BeOfType<EditTimelineResponse>().Subject;
        if (expectCameraAudio)
        {
            MediaClip companion = body.Document.Tracks.Single(t => t.Id == EditTimelineSeeder.TrackId("A1"))
                .Items.Should().ContainSingle().Subject.Should().BeOfType<MediaClip>().Subject;
            companion.AssetRef.ProjectFileId.Should().Be(videoFile.Id);
        }
        else
        {
            body.Document.Tracks.Should().NotContain(t => t.Id == EditTimelineSeeder.TrackId("A1"));
        }
    }

    [Fact]
    public async Task CreateEditTimeline_FromASeed_ReadsTheVideoAnalyzeArtifactNotTheCompileStepsNewerEdl()
    {
        // QA C-323-1 (T-107): the seed row is a real VideoCompile result whose ArtifactStorageKey is
        // its EDL, under the same video-analysis/ prefix and written after the analysis. The EDL has
        // no sources[], so reading "the newest artifact under the prefix" seeds an unprobed source
        // silent. Only a VideoAnalyze step's artifact speaks for the sources.
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        ProjectFile videoFile = await AddProjectFileAsync(db, project.Id, "shoot.mp4", storageKey: "sources/shoot.mp4");
        ProjectFile musicFile = await AddProjectFileAsync(db, project.Id, "bed.mp3", storageKey: "audio/xyz.mp3");

        WorkflowExecution execution = await AddExecutionAsync(db, project.Id);
        string analysisKey = $"projects/{project.Id:D}/agentFiles/video-analysis/{execution.Id:D}/step-1-analysis.json";
        WorkflowStepResult analysis = await AddStepResultAsync(
            db, execution, timelineStorageKey: null, StepType.VideoAnalyze);
        analysis.ArtifactStorageKey = analysisKey;
        analysis.CompletedAt = DateTime.UtcNow.AddMinutes(-5);

        string manifestKey = $"projects/{project.Id:D}/agentFiles/video-analysis/{execution.Id:D}/step-2-timeline.json";
        string edlKey = $"projects/{project.Id:D}/agentFiles/video-analysis/{execution.Id:D}/step-2-edl.json";
        WorkflowStepResult compile = await AddStepResultAsync(db, execution, manifestKey, StepType.VideoCompile);
        compile.ArtifactStorageKey = edlKey;
        compile.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        TimelineManifest manifest = BuildManifest(project.Id, execution.Id, videoFile.StorageKey, musicFile.OriginalFileName);

        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.DownloadAsync(project.Id, manifestKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, DocumentJsonOptions))));
        storage.Setup(s => s.DownloadAsync(project.Id, edlKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("{\"version\":1,\"decisions\":[]}")));
        storage.Setup(s => s.DownloadAsync(project.Id, analysisKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(
                AnalysisArtifactJson(videoFile.StorageKey, "h264", "aac"))));

        ActionResult<EditTimelineResponse> result = await CreateController(db, project.OwnerId, storage.Object)
            .CreateEditTimeline(project.Id, new CreateEditTimelineRequest("Seeded", compile.Id), CancellationToken.None);

        EditTimelineResponse body = result.Result.Should().BeOfType<ObjectResult>().Subject
            .Value.Should().BeOfType<EditTimelineResponse>().Subject;
        MediaClip companion = body.Document.Tracks.Single(t => t.Id == EditTimelineSeeder.TrackId("A1"))
            .Items.Should().ContainSingle().Subject.Should().BeOfType<MediaClip>().Subject;
        companion.AssetRef.ProjectFileId.Should().Be(videoFile.Id);
        storage.Verify(s => s.DownloadAsync(project.Id, edlKey, It.IsAny<CancellationToken>()), Times.Never,
            "the compile step's EDL carries no audio facts");
    }

    [Fact]
    public async Task CreateEditTimeline_FromASeed_TheStoredProbeWinsOverTheAnalysisArtifact()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        ProjectFile videoFile = await AddProjectFileAsync(db, project.Id, "shoot.mp4", storageKey: "sources/shoot.mp4");
        ProjectFile musicFile = await AddProjectFileAsync(db, project.Id, "bed.mp3", storageKey: "audio/xyz.mp3");
        db.MediaDerivatives.Add(new MediaDerivative
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            SourceProjectFileId = videoFile.Id,
            SourceStorageKey = videoFile.StorageKey,
            Kind = MediaDerivativeKind.Proxy,
            StorageKey = "derivatives/proxy.mp4",
            CacheKey = "proxy-cache-key",
            SourceProbeJson = new MediaSourceProbe(
                4, 30, 1, 1920, 1080, "h264", null, "mp4", null, null, null, HasAudio: false).ToJson()
        });
        await db.SaveChangesAsync();

        WorkflowExecution execution = await AddExecutionAsync(db, project.Id);
        string artifactKey = $"projects/{project.Id:D}/agentFiles/video-analysis/{execution.Id:D}/step-1-analysis.json";
        WorkflowStepResult analysis = await AddStepResultAsync(
            db, execution, timelineStorageKey: null, StepType.VideoAnalyze);
        analysis.ArtifactStorageKey = artifactKey;
        await db.SaveChangesAsync();
        string manifestKey = $"projects/{project.Id}/outputFiles/manifest.json";
        WorkflowStepResult stepResult = await AddStepResultAsync(db, execution, manifestKey);
        TimelineManifest manifest = BuildManifest(project.Id, execution.Id, videoFile.StorageKey, musicFile.OriginalFileName);

        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.DownloadAsync(project.Id, manifestKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, DocumentJsonOptions))));
        storage.Setup(s => s.DownloadAsync(project.Id, artifactKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(
                AnalysisArtifactJson(videoFile.StorageKey, "h264", "aac"))));

        ActionResult<EditTimelineResponse> result = await CreateController(db, project.OwnerId, storage.Object)
            .CreateEditTimeline(project.Id, new CreateEditTimelineRequest("Seeded", stepResult.Id), CancellationToken.None);

        EditTimelineResponse body = result.Result.Should().BeOfType<ObjectResult>().Subject
            .Value.Should().BeOfType<EditTimelineResponse>().Subject;
        body.Document.Tracks.Should().NotContain(t => t.Id == EditTimelineSeeder.TrackId("A1"),
            "the file's own ingest probe says it is silent");
        storage.Verify(s => s.DownloadAsync(project.Id, artifactKey, It.IsAny<CancellationToken>()), Times.Never,
            "no artifact is read when every source already has a stored probe");
    }

    [Fact]
    public async Task CreateEditTimeline_FromASeed_AnUnreadableAnalysisArtifactSeedsWithoutCameraAudio()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        ProjectFile videoFile = await AddProjectFileAsync(db, project.Id, "shoot.mp4", storageKey: "sources/shoot.mp4");
        ProjectFile musicFile = await AddProjectFileAsync(db, project.Id, "bed.mp3", storageKey: "audio/xyz.mp3");
        WorkflowExecution execution = await AddExecutionAsync(db, project.Id);
        string artifactKey = $"projects/{project.Id:D}/agentFiles/video-analysis/{execution.Id:D}/step-1-analysis.json";
        WorkflowStepResult analysis = await AddStepResultAsync(
            db, execution, timelineStorageKey: null, StepType.VideoAnalyze);
        analysis.ArtifactStorageKey = artifactKey;
        await db.SaveChangesAsync();
        string manifestKey = $"projects/{project.Id}/outputFiles/manifest.json";
        WorkflowStepResult stepResult = await AddStepResultAsync(db, execution, manifestKey);
        TimelineManifest manifest = BuildManifest(project.Id, execution.Id, videoFile.StorageKey, musicFile.OriginalFileName);

        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.DownloadAsync(project.Id, manifestKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, DocumentJsonOptions))));
        storage.Setup(s => s.DownloadAsync(project.Id, artifactKey, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("storage unavailable"));

        ActionResult<EditTimelineResponse> result = await CreateController(db, project.OwnerId, storage.Object)
            .CreateEditTimeline(project.Id, new CreateEditTimelineRequest("Seeded", stepResult.Id), CancellationToken.None);

        ObjectResult created = result.Result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(201, "the artifact is only a hint; without it the seed is today's");
        created.Value.Should().BeOfType<EditTimelineResponse>().Subject
            .Document.Tracks.Should().NotContain(t => t.Id == EditTimelineSeeder.TrackId("A1"));
    }

    private static string AnalysisArtifactJson(string sourceStorageKey, string? videoCodec, string? audioCodec)
    {
        VideoAnalysisMedia media = new(10, 30, 1, 1920, 1080, VideoCodec: videoCodec, AudioCodec: audioCodec);
        VideoAnalysisArtifact artifact = new(
            Version: 2,
            Media: media,
            Shots: [],
            SilenceSpans: [],
            Segments: [],
            Words: [],
            OfferedIds: [],
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Sources: [new VideoAnalysisSourceInfo(0, sourceStorageKey, media)]);
        return JsonSerializer.Serialize(artifact, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateEditTimeline_AnUnreadableSeedManifest_Answers424WithAPlainMessageAndNoStorageKey(bool storageFails)
    {
        // Security MI-3: this used to answer 502 (whose body Cloudflare replaces) carrying the
        // manifest's storage key and the raw exception text.
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        WorkflowExecution execution = await AddExecutionAsync(db, project.Id);
        string manifestKey = $"projects/{project.Id}/outputFiles/manifest.json";
        WorkflowStepResult stepResult = await AddStepResultAsync(db, execution, manifestKey);

        const string internalDetail = "bucket reelforge-prod refused GetObject from 10.0.3.7";
        Mock<IFileStorageService> storage = new();
        if (storageFails)
        {
            storage.Setup(s => s.DownloadAsync(project.Id, manifestKey, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException($"{internalDetail} for '{manifestKey}'"));
        }
        else
        {
            storage.Setup(s => s.DownloadAsync(project.Id, manifestKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("{ not json")));
        }

        ActionResult<EditTimelineResponse> result = await CreateController(db, project.OwnerId, storage.Object)
            .CreateEditTimeline(project.Id, new CreateEditTimelineRequest("Seeded", stepResult.Id), CancellationToken.None);

        ObjectResult failed = result.Result.Should().BeOfType<ObjectResult>().Subject;
        failed.StatusCode.Should().Be(StatusCodes.Status424FailedDependency);

        string body = JsonSerializer.Serialize(failed.Value);
        body.Should().NotContain(manifestKey);
        body.Should().NotContain("projects/");
        body.Should().NotContain("outputFiles");
        body.Should().NotContain(internalDetail);
        body.Should().NotContain("not json");
        body.Should().Contain("could not be read");

        (await db.EditTimelines.CountAsync()).Should().Be(0, "nothing is created when the seed cannot be read");
    }

    [Fact]
    public async Task CreateEditTimeline_Returns404_WhenTheSeedStepResultBelongsToAnotherProject()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Mine");
        Project other = await AddProjectAsync(db, "Theirs");
        WorkflowExecution foreignExecution = await AddExecutionAsync(db, other.Id);
        WorkflowStepResult foreignStepResult = await AddStepResultAsync(
            db, foreignExecution, $"projects/{other.Id}/outputFiles/manifest.json");

        EditTimelinesController controller = CreateController(db, project.OwnerId);

        ActionResult<EditTimelineResponse> result = await controller.CreateEditTimeline(
            project.Id, new CreateEditTimelineRequest("Steal", foreignStepResult.Id), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        (await db.EditTimelines.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateEditTimeline_Returns404_WhenTheSeedStepResultDoesNotExist()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        EditTimelinesController controller = CreateController(db, project.OwnerId);

        ActionResult<EditTimelineResponse> result = await controller.CreateEditTimeline(
            project.Id, new CreateEditTimelineRequest("Seeded", Guid.NewGuid()), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task CreateEditTimeline_Returns400_WhenTheSeedStepResultCarriesNoTimelineStorageKey()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        WorkflowExecution execution = await AddExecutionAsync(db, project.Id);
        WorkflowStepResult stepResult = await AddStepResultAsync(db, execution, timelineStorageKey: null);

        EditTimelinesController controller = CreateController(db, project.OwnerId);

        ActionResult<EditTimelineResponse> result = await controller.CreateEditTimeline(
            project.Id, new CreateEditTimelineRequest("Seeded", stepResult.Id), CancellationToken.None);

        BadRequestObjectResult badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(400);
        (await db.EditTimelines.CountAsync()).Should().Be(0, "no timeline is created without a manifest");
    }

    [Fact]
    public async Task CreateEditTimeline_Returns424_WhenTheManifestCannotBeDownloaded()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        WorkflowExecution execution = await AddExecutionAsync(db, project.Id);
        WorkflowStepResult stepResult = await AddStepResultAsync(
            db, execution, $"projects/{project.Id}/outputFiles/gone.json");

        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.DownloadAsync(project.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("storage unreachable"));

        EditTimelinesController controller = CreateController(db, project.OwnerId, storage.Object);

        ActionResult<EditTimelineResponse> result = await controller.CreateEditTimeline(
            project.Id, new CreateEditTimelineRequest("Seeded", stepResult.Id), CancellationToken.None);

        ObjectResult failedDependency = result.Result.Should().BeOfType<ObjectResult>().Subject;
        failedDependency.StatusCode.Should().Be(
            424, "a storage failure is not the caller's fault, and a 502 body would be replaced by Cloudflare");
        (await db.EditTimelines.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateEditTimeline_Returns400_WhenTheNameIsBlank()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        EditTimelinesController controller = CreateController(db, project.OwnerId);

        ActionResult<EditTimelineResponse> result = await controller.CreateEditTimeline(
            project.Id, new CreateEditTimelineRequest("   ", null), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.StatusCode.Should().Be(400);
    }

    // ==========================================================================================
    // GET — single item.
    // ==========================================================================================

    [Fact]
    public async Task GetEditTimeline_ReturnsTheDocumentAndRevision()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        EditTimelineEntity timeline = await AddTimelineAsync(
            db, project.Id, "Cut", BlankDocument(project.Id, Guid.NewGuid(), "Cut"), revision: 7);

        EditTimelinesController controller = CreateController(db, project.OwnerId);

        ActionResult<EditTimelineResponse> result = await controller.GetEditTimeline(
            project.Id, timeline.Id, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        EditTimelineResponse body = ok.Value.Should().BeOfType<EditTimelineResponse>().Subject;
        body.Id.Should().Be(timeline.Id);
        body.ProjectId.Should().Be(project.Id);
        body.Name.Should().Be("Cut");
        body.Revision.Should().Be(7);
        body.Document.Tracks.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetEditTimeline_Returns404_ForATimelineInAnotherProject()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Mine");
        Project other = await AddProjectAsync(db, "Theirs");
        EditTimelineEntity foreign = await AddTimelineAsync(
            db, other.Id, "Theirs", BlankDocument(other.Id, Guid.NewGuid(), "Theirs"), revision: 1);

        EditTimelinesController controller = CreateController(db, project.OwnerId);

        ActionResult<EditTimelineResponse> result = await controller.GetEditTimeline(
            project.Id, foreign.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>("existence in another project must never leak");
    }

    [Fact]
    public async Task GetEditTimeline_Returns404_ForASoftDeletedTimeline()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        EditTimelineEntity timeline = await AddTimelineAsync(
            db, project.Id, "Deleted", BlankDocument(project.Id, Guid.NewGuid(), "Deleted"), revision: 1);
        timeline.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        EditTimelinesController controller = CreateController(db, project.OwnerId);

        ActionResult<EditTimelineResponse> result = await controller.GetEditTimeline(
            project.Id, timeline.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetEditTimeline_Returns500_WhenTheStoredDocumentCannotBeParsed()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        EditTimelineEntity timeline = await AddTimelineAsync(
            db, project.Id, "Corrupt", BlankDocument(project.Id, Guid.NewGuid(), "Corrupt"), revision: 1);
        timeline.DocumentJson = "{not json";
        await db.SaveChangesAsync();

        EditTimelinesController controller = CreateController(db, project.OwnerId);

        ActionResult<EditTimelineResponse> result = await controller.GetEditTimeline(
            project.Id, timeline.Id, CancellationToken.None);

        ObjectResult error = result.Result.Should().BeOfType<ObjectResult>().Subject;
        error.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task GetEditTimeline_Returns404_WhenTheProjectDoesNotExist()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        EditTimelinesController controller = CreateController(db, Guid.NewGuid());

        ActionResult<EditTimelineResponse> result = await controller.GetEditTimeline(
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    // ==========================================================================================
    // GET — list.
    // ==========================================================================================

    [Fact]
    public async Task ListEditTimelines_ReturnsSummariesWithoutDocuments_NewestUpdatedFirst()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");

        EditTimelineEntity older = await AddTimelineAsync(
            db, project.Id, "Older", BlankDocument(project.Id, Guid.NewGuid(), "Older"), revision: 4);
        older.UpdatedAt = DateTime.UtcNow.AddHours(-2);
        EditTimelineEntity newer = await AddTimelineAsync(
            db, project.Id, "Newer", BlankDocument(project.Id, Guid.NewGuid(), "Newer"), revision: 6);
        newer.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        EditTimelinesController controller = CreateController(db, project.OwnerId);

        ActionResult<List<EditTimelineSummaryResponse>> result = await controller.ListEditTimelines(
            project.Id, null, CancellationToken.None);

        List<EditTimelineSummaryResponse> summaries = result.Result
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<List<EditTimelineSummaryResponse>>().Subject;

        summaries.Should().HaveCount(2);
        summaries[0].Id.Should().Be(newer.Id);
        summaries[1].Id.Should().Be(older.Id);
        summaries[0].Revision.Should().Be(6);
        summaries[0].Name.Should().Be("Newer");

        // The summary is a summary: it carries no document at all.
        summaries[0].GetType().GetProperties().Should().NotContain(p => p.Name == "Document");
    }

    [Fact]
    public async Task ListEditTimelines_ExcludesSoftDeletedTimelinesAndOtherProjects()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Mine");
        Project other = await AddProjectAsync(db, "Theirs");

        EditTimelineEntity kept = await AddTimelineAsync(
            db, project.Id, "Kept", BlankDocument(project.Id, Guid.NewGuid(), "Kept"), revision: 1);
        EditTimelineEntity deleted = await AddTimelineAsync(
            db, project.Id, "Deleted", BlankDocument(project.Id, Guid.NewGuid(), "Deleted"), revision: 1);
        deleted.DeletedAt = DateTime.UtcNow;
        await AddTimelineAsync(db, other.Id, "Theirs", BlankDocument(other.Id, Guid.NewGuid(), "Theirs"), revision: 1);
        await db.SaveChangesAsync();

        EditTimelinesController controller = CreateController(db, project.OwnerId);

        ActionResult<List<EditTimelineSummaryResponse>> result = await controller.ListEditTimelines(
            project.Id, null, CancellationToken.None);

        List<EditTimelineSummaryResponse> summaries = result.Result
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<List<EditTimelineSummaryResponse>>().Subject;

        summaries.Should().ContainSingle();
        summaries[0].Id.Should().Be(kept.Id);
    }

    // ==========================================================================================
    // Ownership on every endpoint.
    // ==========================================================================================

    [Fact]
    public async Task EveryEndpoint_ReturnsNotFound_WhenTheProjectIsInAnotherOrganization()
    {
        using InferenceApiDbContext db = CreateContext(NewDatabaseName());
        Project project = await AddProjectAsync(db, "Proj");
        EditTimelineEntity timeline = await AddTimelineAsync(
            db, project.Id, "Cut", BlankDocument(project.Id, Guid.NewGuid(), "Cut"), revision: 1);

        EditTimelinesController controller = CreateController(db, Guid.NewGuid());

        (await controller.ListEditTimelines(project.Id, null, CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
        (await controller.GetEditTimeline(project.Id, timeline.Id, CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
        (await controller.CreateEditTimeline(
            project.Id, new CreateEditTimelineRequest("Nope", null), CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
        (await controller.PatchEditTimeline(
            project.Id, timeline.Id, new PatchEditTimelineRequest(1, null, null), CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();

        (await db.EditTimelines.CountAsync()).Should().Be(1, "no non-owner call creates or changes anything");
    }

    // ==========================================================================================
    // FIX A — the PATCH body ceiling is a REAL pre-parse control, not just a post-parse check.
    // ==========================================================================================

    /// <summary>
    /// Pins the wiring, not the doc comment: the action must carry a
    /// <see cref="RequestSizeLimitAttribute"/> that, when MVC turns it into its filter, sets
    /// <c>IHttpMaxRequestBodySizeFeature.MaxRequestBodySize</c> to exactly
    /// <see cref="EditTimelinesController.MaxRequestBodyBytes"/>. The attribute keeps its value
    /// private, so the assertion is made by running the filter it creates — the same feature write
    /// MVC performs on a real request, and the exact one Kestrel enforces. Both filter stages below
    /// run BEFORE model binding, which is what makes the bound pre-parse.
    /// </summary>
    [Fact]
    public void PatchEditTimeline_WiresThePreParseBodyLimitToMaxRequestBodyBytes()
    {
        MethodInfo action = typeof(EditTimelinesController)
            .GetMethod(nameof(EditTimelinesController.PatchEditTimeline))!;

        RequestSizeLimitAttribute attribute = action
            .GetCustomAttributes<RequestSizeLimitAttribute>(inherit: true)
            .Should().ContainSingle(
                "the pre-parse bound must be WIRED to the action — a documented constant enforces nothing")
            .Subject;

        IFilterMetadata filter = attribute is IFilterFactory factory
            ? factory.CreateInstance(MvcFilterServiceProvider())
            : attribute;

        StubMaxRequestBodySizeFeature feature = new();
        DefaultHttpContext httpContext = new();
        httpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        ActionContext actionContext = new(httpContext, new RouteData(), new ActionDescriptor());

        // The framework is free to move this between filter stages across versions; both stages it
        // has ever used are pre-binding, which is the property this test actually depends on.
        switch (filter)
        {
            case IAuthorizationFilter authorizationFilter:
                authorizationFilter.OnAuthorization(
                    new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>()));
                break;

            case IResourceFilter resourceFilter:
                resourceFilter.OnResourceExecuting(new ResourceExecutingContext(
                    actionContext,
                    new List<IFilterMetadata>(),
                    new List<IValueProviderFactory>()));
                break;

            default:
                throw new InvalidOperationException(
                    $"{filter.GetType()} is neither an authorization nor a resource filter, so it " +
                    "cannot be shown to run before model binding");
        }

        feature.MaxRequestBodySize.Should().Be(
            EditTimelinesController.MaxRequestBodyBytes,
            "the wired ceiling must be the documented one, not a copy that can drift away from it");

        // The relationship that matters between the transport ceiling and the two per-part ceilings
        // is ORDERING, not arithmetic: the transport must let a body through to the guard that was
        // written for it. A per-part bound at or above the transport bound would mean a document
        // exactly at MaxDocumentBytes was refused by Kestrel with the transport's 413, so the
        // document guard's own contract would be unreachable — and no test asserts the transport
        // constant's literal sum, which is why restating it proved nothing.
        EditTimelinesController.MaxRequestBodyBytes.Should().BeGreaterThan(
            EditTimelinesController.MaxDocumentBytes,
            "a document exactly at the document ceiling must still reach the document guard");
        EditTimelinesController.MaxRequestBodyBytes.Should().BeGreaterThan(
            EditTimelinesController.MaxOpsBytes,
            "an ops blob exactly at the ops ceiling must still reach the ops guard");
    }

    /// <summary>
    /// The genuine hosted proof: a real Kestrel, this controller mounted through MVC, and an
    /// <see cref="HttpClient"/> that cannot reach the action at all.
    ///
    /// <para>
    /// The over-limit body is deliberately <b>not</b> a bindable <c>PatchEditTimelineRequest</c>: it
    /// would earn a <c>400</c> (or a <c>409</c>) if MVC had bound and dispatched it, so a
    /// <c>413</c> can only be Kestrel refusing the body while it was still arriving — before any
    /// binding, allocation or deserialization. The second half of the test is the other side of the
    /// same bound: an ordinary under-limit body through the same host still succeeds, so the guard
    /// rejects size and nothing else.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PatchEditTimeline_OverRealKestrel_Returns413PastTheCeilingAndStillServesAnUnderLimitBody()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;

            EditTimelineEntity timeline = await AddTimelineAsync(
                db, project.Id, "Hosted", BlankDocument(project.Id, Guid.NewGuid(), "Hosted"), revision: 1);
            timelineId = timeline.Id;
        }

        await using WebApplication host = await StartHostedApiAsync(databaseName, ownerId);
        using HttpClient client = new()
        {
            BaseAddress = new Uri(HostAddress(host)),
            Timeout = TimeSpan.FromSeconds(120)
        };

        string route = $"api/v1/projects/{projectId}/edit-timelines/{timelineId}";

        // ---- Over the ceiling ----------------------------------------------------------------
        using HttpRequestMessage oversized = new(HttpMethod.Patch, route)
        {
            Content = new StringContent(
                UnbindableBodyOfExactByteCount(EditTimelinesController.MaxRequestBodyBytes + 1),
                Encoding.UTF8,
                "application/json")
        };

        // Expect: 100-continue is what makes this assertion deterministic rather than a race. The
        // declared Content-Length is already past the action's limit, so Kestrel refuses the body
        // when MVC first asks for it and answers 413 without the client ever streaming 9.5 MiB into
        // a socket the server has stopped reading — which is exactly the pre-parse behaviour under
        // test, observed at the transport.
        oversized.Headers.ExpectContinue = true;

        using HttpResponseMessage tooLarge = await client.SendAsync(oversized);

        tooLarge.StatusCode.Should().Be(
            HttpStatusCode.RequestEntityTooLarge,
            "Kestrel must refuse a body past [RequestSizeLimit] before MVC ever binds it");

        // ---- Under the ceiling ---------------------------------------------------------------
        EditTimeline submitted = BlankDocument(projectId, timelineId, "Hosted v2");
        using HttpRequestMessage accepted = new(HttpMethod.Patch, route)
        {
            Content = new StringContent(UnderLimitBody(1, submitted), Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage ok = await client.SendAsync(accepted);

        ok.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"a body under the ceiling must still be served: {await ok.Content.ReadAsStringAsync()}");
        (await ok.Content.ReadAsStringAsync()).Should().Contain("Hosted v2");

        // ---- And exactly one write landed: the rejected one left nothing behind ---------------
        using InferenceApiDbContext verifier = CreateContext(databaseName);
        EditTimelineEntity reloaded = await verifier.EditTimelines.AsNoTracking()
            .SingleAsync(t => t.Id == timelineId);

        reloaded.Revision.Should().Be(2, "only the accepted request may advance the revision");
        (await verifier.EditTimelineOps.CountAsync()).Should().Be(
            1, "the 413 past the transport ceiling must not have appended an op row");
    }

    // ==========================================================================================
    // The null ELEMENT reaches the validator — a wire fact, so over real Kestrel.
    //
    // BuildValidationContextAsync walks the caller-controlled document to collect referenced ids.
    // A null element in `tracks` or in a track's `items` is a thing the CALLER can put on the wire,
    // and the validator has a branch for each ("track must not be null" / "item must not be null")
    // whose whole contract is a 400 with no write. Dereferencing either before Validate runs turns
    // that contract into an unhandled 500 — which is exactly why these two tests assert the
    // VALIDATOR'S OWN violation text and not merely "not 500": a binding failure also answers 400,
    // so the status code alone would not prove the action ever ran.
    //
    // The null-TRACK half is PRE-EXISTING (the original loop read `track.Items` unconditionally).
    // The null-ITEM half is the REGRESSION the T-021 condition-C fix introduced, when provenance
    // collection added a second dereference of `item` to a loop that already had one.
    // ==========================================================================================

    [Fact]
    public async Task PatchEditTimeline_OverRealKestrel_RejectsANullItemWithTheValidators400_AndWritesNothing()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;

            EditTimelineEntity timeline = await AddTimelineAsync(
                db, project.Id, "Nulls", BlankDocument(project.Id, Guid.NewGuid(), "Nulls"), revision: 1);
            timelineId = timeline.Id;
        }

        // A document that GENUINELY holds the null element, so the serialized wire text really is
        // `"items":[null]`. Hand-writing the JSON string would prove nothing about what MVC does with
        // a bound null item, and the whole point here is that binding SUCCEEDS and the action is what
        // has to survive it.
        EditTimeline blank = BlankDocument(projectId, timelineId, "Null item");
        EditTimeline document = blank with
        {
            Tracks =
            [
                blank.Tracks[0] with { Items = [null!] },
                blank.Tracks[1]
            ]
        };

        string body = UnderLimitBody(1, document);

        body.Should().Contain(
            "\"items\":[null]",
            "the wire text must really carry the null element, or this test silently degrades into a "
            + "duplicate of the happy path and proves nothing");

        await using WebApplication host = await StartHostedApiAsync(databaseName, ownerId);
        using HttpClient client = new()
        {
            BaseAddress = new Uri(HostAddress(host)),
            Timeout = TimeSpan.FromSeconds(120)
        };

        using HttpRequestMessage request = new(
            HttpMethod.Patch, $"api/v1/projects/{projectId}/edit-timelines/{timelineId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage response = await client.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "a null item is the VALIDATOR's violation and the validator answers 400; a 500 here is "
            + "the regression under test, because a NullReferenceException in the ownership-map "
            + "builder escapes before Validate is ever reached: {0}",
            responseBody);

        // The discriminating half: the validator's OWN path and text. A model-binding 400 would
        // satisfy the status code above and fail here.
        responseBody.Should().Contain("tracks[0].items[0]");
        responseBody.Should().Contain("item must not be null");

        using InferenceApiDbContext verifier = CreateContext(databaseName);
        EditTimelineEntity reloaded = await verifier.EditTimelines.AsNoTracking()
            .SingleAsync(t => t.Id == timelineId);
        reloaded.Revision.Should().Be(1, "a rejected PATCH must not advance the revision");
        (await verifier.EditTimelineOps.CountAsync()).Should().Be(0, "no row in edit_timeline_ops");
    }

    [Fact]
    public async Task PatchEditTimeline_OverRealKestrel_RejectsANullTrackWithTheValidators400_AndWritesNothing()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;

            EditTimelineEntity timeline = await AddTimelineAsync(
                db, project.Id, "Nulls", BlankDocument(project.Id, Guid.NewGuid(), "Nulls"), revision: 1);
            timelineId = timeline.Id;
        }

        EditTimeline blank = BlankDocument(projectId, timelineId, "Null track");
        EditTimeline document = blank with { Tracks = [null!] };

        string body = UnderLimitBody(1, document);

        body.Should().Contain(
            "\"tracks\":[null]",
            "the wire text must really carry the null element, or this test silently degrades into a "
            + "duplicate of the happy path and proves nothing");

        await using WebApplication host = await StartHostedApiAsync(databaseName, ownerId);
        using HttpClient client = new()
        {
            BaseAddress = new Uri(HostAddress(host)),
            Timeout = TimeSpan.FromSeconds(120)
        };

        using HttpRequestMessage request = new(
            HttpMethod.Patch, $"api/v1/projects/{projectId}/edit-timelines/{timelineId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage response = await client.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "a null track is the VALIDATOR's violation and the validator answers 400; a 500 here is "
            + "the PRE-EXISTING half of the same defect, where the ownership-map builder dereferences "
            + "a caller-controlled null before Validate is ever reached: {0}",
            responseBody);

        responseBody.Should().Contain("tracks[0]");
        responseBody.Should().Contain("track must not be null");

        using InferenceApiDbContext verifier = CreateContext(databaseName);
        EditTimelineEntity reloaded = await verifier.EditTimelines.AsNoTracking()
            .SingleAsync(t => t.Id == timelineId);
        reloaded.Revision.Should().Be(1, "a rejected PATCH must not advance the revision");
        (await verifier.EditTimelineOps.CountAsync()).Should().Be(0, "no row in edit_timeline_ops");
    }

    // ==========================================================================================
    // FIX B — the caller-supplied ops blob is bounded BEFORE any write.
    // ==========================================================================================

    [Fact]
    public async Task PatchEditTimeline_Returns413_WhenTheOpsBlobExceedsMaxOpsBytes_AndWritesNothing()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;
        string storedJsonBefore;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;

            EditTimelineEntity timeline = await AddTimelineAsync(
                db, project.Id, "Oped", BlankDocument(project.Id, Guid.NewGuid(), "Oped"), revision: 3);
            timelineId = timeline.Id;
            storedJsonBefore = timeline.DocumentJson;
        }

        string oversizedOps = OpsBlobOfExactByteCount(EditTimelinesController.MaxOpsBytes + 1);

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            // The document is small AND valid, and this is a direct controller call with no
            // transport — so the only ceiling that can produce this 413 is the OPS one, which is
            // precisely what isolates the post-parse guard from Kestrel's body limit.
            ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
                projectId,
                timelineId,
                new PatchEditTimelineRequest(
                    3,
                    JsonDocument.Parse(oversizedOps).RootElement.Clone(),
                    BlankDocument(projectId, timelineId, "OVER")),
                CancellationToken.None);

            ObjectResult tooLarge = result.Result.Should().BeOfType<ObjectResult>().Subject;
            tooLarge.StatusCode.Should().Be(413);

            JsonElement body = JsonSerializer.SerializeToElement(tooLarge.Value, tooLarge.Value!.GetType());
            body.GetProperty("maxOpsBytes").GetInt32().Should().Be(
                EditTimelinesController.MaxOpsBytes,
                "the 413 must name the OPS ceiling, not the document one");
        }

        // A FRESH context: the request's own context would answer from its identity map and a
        // mutated-but-unsaved entity would look written.
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelineEntity reloaded = await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timelineId);
            reloaded.Revision.Should().Be(3, "an oversized-ops PATCH must not advance the revision");
            reloaded.DocumentJson.Should().Be(storedJsonBefore, "and must not change the document");

            (await db.EditTimelineOps.CountAsync()).Should().Be(0, "no row in edit_timeline_ops");
            (await db.EditTimelineSnapshots.CountAsync()).Should().Be(0);
        }
    }

    /// <summary>
    /// QA condition 2 / F7: the <c>MaxOpsBytes</c> guard's comment claims it runs <b>before</b> the
    /// stale-revision check, but the test above sends a MATCHING <c>baseRevision</c>, so the relative
    /// order of the two guards was never observed — either order passes it.
    /// </summary>
    /// <remarks>
    /// This sends BOTH defects in ONE request and asserts the outcome the implementation actually
    /// produces. <b>OBSERVED: 413.</b> The ops guard is at <c>:388</c> and
    /// <c>timeline.Revision != request.BaseRevision</c> at <c>:400</c>, so a caller who is both
    /// oversized and stale learns about the size first and the comment is accurate.
    /// </remarks>
    [Fact]
    public async Task PatchEditTimeline_WithOversizedOpsAndAStaleBaseRevision_ReturnsTheOpsCeilingNotTheStaleRevision()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;

            EditTimelineEntity timeline = await AddTimelineAsync(
                db, project.Id, "Oped", BlankDocument(project.Id, Guid.NewGuid(), "Oped"), revision: 3);
            timelineId = timeline.Id;
        }

        string oversizedOps = OpsBlobOfExactByteCount(EditTimelinesController.MaxOpsBytes + 1);

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            // BOTH defects at once: baseRevision 1 against a timeline at revision 3, and an ops blob
            // past the ceiling. The document is small and valid, so only those two guards can answer —
            // and which one answers is exactly the fact under test.
            ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
                projectId,
                timelineId,
                new PatchEditTimelineRequest(
                    1,
                    JsonDocument.Parse(oversizedOps).RootElement.Clone(),
                    BlankDocument(projectId, timelineId, "BOTH")),
                CancellationToken.None);

            // Conflict() returns a ConflictObjectResult and StatusCode() an ObjectResult, so this
            // cast does not pre-judge which one answered — the assertion below does.
            ObjectResult observed = result.Result.Should().BeOfType<ObjectResult>().Subject;

            observed.StatusCode.Should().Be(
                413,
                "the ops ceiling is evaluated BEFORE the stale-revision check, so a caller who is "
                + "both oversized and stale must learn about the SIZE first; a 409 would mean the "
                + "controller comment describes an order the code does not implement");
            observed.StatusCode.Should().NotBe(409);

            JsonElement responseBody = JsonSerializer.SerializeToElement(observed.Value, observed.Value!.GetType());
            responseBody.GetProperty("maxOpsBytes").GetInt32().Should().Be(
                EditTimelinesController.MaxOpsBytes,
                "the 413 must name the OPS ceiling, not the document one");
            responseBody.TryGetProperty("currentRevision", out _).Should().BeFalse(
                "a 409 body carries currentRevision; this response must not be one");
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelineEntity reloaded = await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timelineId);
            reloaded.Revision.Should().Be(3, "an oversized-and-stale PATCH must not advance the revision");
            (await db.EditTimelineOps.CountAsync()).Should().Be(0, "no row in edit_timeline_ops");
        }
    }

    /// <summary>
    /// The boundary itself: exactly <see cref="EditTimelinesController.MaxOpsBytes"/> is ACCEPTED,
    /// and the bytes that were measured are the bytes that were persisted. Without this the guard
    /// could be <c>&gt;=</c>, or could measure one blob and store another, and the test above would
    /// not notice.
    /// </summary>
    [Fact]
    public async Task PatchEditTimeline_AtExactlyMaxOpsBytes_AcceptsAndPersistsThoseExactBytes()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;

            EditTimelineEntity timeline = await AddTimelineAsync(
                db, project.Id, "AtLimit", BlankDocument(project.Id, Guid.NewGuid(), "AtLimit"), revision: 1);
            timelineId = timeline.Id;
        }

        string atLimitOps = OpsBlobOfExactByteCount(EditTimelinesController.MaxOpsBytes);

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
                projectId,
                timelineId,
                new PatchEditTimelineRequest(
                    1,
                    JsonDocument.Parse(atLimitOps).RootElement.Clone(),
                    BlankDocument(projectId, timelineId, "AT LIMIT")),
                CancellationToken.None);

            result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<EditTimelineResponse>().Subject.Revision.Should().Be(2);
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelineOp op = await db.EditTimelineOps.AsNoTracking().SingleAsync();
            op.OpsJson.Should().Be(atLimitOps, "the measured bytes must be the persisted bytes");
            Encoding.UTF8.GetByteCount(op.OpsJson).Should().Be(EditTimelinesController.MaxOpsBytes);
        }
    }

    // ==========================================================================================
    // FIX F4 — the DbUpdateConcurrencyException -> 409 race branch, GENUINELY exercised.
    // ==========================================================================================

    /// <summary>
    /// The second half of PATCH's double concurrency protection, hit for real: the pre-check passes
    /// (the caller's <c>baseRevision</c> matches what this context read) and the failure happens at
    /// the commit, exactly where a writer that raced between the check and the UPDATE would surface
    /// it. The substituted <c>SaveChangesAsync</c> throws the same
    /// <see cref="DbUpdateConcurrencyException"/> EF Core throws when the
    /// <c>WHERE id = @id AND revision = @original</c> UPDATE matches zero rows.
    /// </summary>
    [Fact]
    public async Task PatchEditTimeline_WhenTheCommitRaces_Returns409WithCurrentRevisionAndWritesNothing()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;
        string storedJsonBefore;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;

            EditTimelineEntity timeline = await AddTimelineAsync(
                db, project.Id, "Raced", BlankDocument(project.Id, Guid.NewGuid(), "Raced"), revision: 7);
            timelineId = timeline.Id;
            storedJsonBefore = timeline.DocumentJson;
        }

        using (ConcurrencyLosingDbContext db = new(CreateOptions(databaseName)))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
                projectId,
                timelineId,
                new PatchEditTimelineRequest(7, null, BlankDocument(projectId, timelineId, "LOSER")),
                CancellationToken.None);

            db.SaveAttempts.Should().Be(
                1, "the write must genuinely have been attempted, and this is the one that threw");

            ConflictObjectResult conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
            conflict.StatusCode.Should().Be(409);
            conflict.Value.Should().BeOfType<EditTimelineConflictResponse>().Subject
                .CurrentRevision.Should().Be(
                    7, "the 409 must report the revision still stored, read back through AsNoTracking");
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelineEntity reloaded = await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timelineId);
            reloaded.Revision.Should().Be(7, "a rolled-back PATCH must not advance the revision");
            reloaded.DocumentJson.Should().Be(storedJsonBefore, "the losing writer's document must not have landed");

            (await db.EditTimelineOps.CountAsync()).Should().Be(0, "the rolled-back op row must not survive");
            (await db.EditTimelineSnapshots.CountAsync()).Should().Be(0);
        }
    }

    // ==========================================================================================
    // T-022 — the edit-v2 contract enums are camelCase STRINGS on the wire and in document_json.
    //
    // The schema (EditTimeline.schema.json) is the canonical contract and its enum literals are
    // camelCase strings. Before T-022 neither path agreed with it: MVC had no enum converter at
    // all, so a PATCH bound enums as INTEGERS, and the controller's persistence options used a
    // naming-policy-less JsonStringEnumConverter, so document_json stored PASCALCASE. Both are
    // fixed by ONE thing — a type-level [JsonConverter(typeof(EditTimelineEnumJsonConverter))] on
    // the five declarations — which is why D3 removes the controller's converter instead of
    // adding a second one: an options-level converter wins over the attribute, so leaving it there
    // would silently keep storing PascalCase while every test that used the harness's own
    // (converter-carrying) options still passed.
    // ==========================================================================================

    /// <summary>
    /// The whole contract over a real Kestrel: <c>POST</c> → <c>GET</c> → <c>PATCH</c> → <c>GET</c>.
    ///
    /// <para>
    /// <b>The PATCH body is a HAND-WRITTEN JSON literal</b>, never C#-serialized. That is the point
    /// of the test: a body produced by <c>JsonSerializer.Serialize</c> would prove only that the
    /// serializer agrees with itself, and would keep passing if the type-level attribute were
    /// removed. A literal written the way the SCHEMA says is what proves the server accepts the
    /// canonical wire format — and the literal carries all five contract enums, so no one of them
    /// can be dropped from the contract without this failing.
    /// </para>
    /// <para>
    /// Both <c>GET</c> bodies (before and after the PATCH) are scanned for enum-typed properties
    /// and every one must be a JSON STRING — a bare integer enum fails the scan wherever it hides.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PatchEditTimeline_OverRealKestrel_BindsASchemaConformantCamelCaseBodyAndRoundTrips()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid ownerId;
        Guid projectFileId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;

            // The hand-written literal names this file, so the validator's ownership check has
            // something real to resolve against.
            projectFileId = (await AddProjectFileAsync(db, projectId, "source.mp4")).Id;
        }

        await using WebApplication host = await StartHostedApiAsync(databaseName, ownerId);
        using HttpClient client = new()
        {
            BaseAddress = new Uri(HostAddress(host)),
            Timeout = TimeSpan.FromSeconds(120)
        };

        string collectionRoute = $"api/v1/projects/{projectId}/edit-timelines";

        // ---- POST: a blank timeline, so the round trip starts from a server-built document ------
        using HttpResponseMessage created = await client.PostAsync(
            collectionRoute,
            new StringContent("""{"name":"Camel contract"}""", Encoding.UTF8, "application/json"));

        created.StatusCode.Should().Be(
            HttpStatusCode.Created, $"the blank create must succeed: {await created.Content.ReadAsStringAsync()}");

        JsonElement createdBody = ParseJson(await created.Content.ReadAsStringAsync());
        Guid timelineId = createdBody.GetProperty("id").GetGuid();
        createdBody.GetProperty("revision").GetInt32().Should().Be(1);

        // The server's own POST response is already the contract: camelCase strings, no integers.
        AssertEveryContractEnumIsAString(createdBody, "$(POST)");
        createdBody.GetProperty("document").GetProperty("settings").GetProperty("aspectPolicy")
            .GetString().Should().Be("fit");

        string itemRoute = $"{collectionRoute}/{timelineId}";

        // ---- GET before the PATCH ---------------------------------------------------------------
        JsonElement before = await GetJsonAsync(client, itemRoute);
        AssertEveryContractEnumIsAString(before, "$(GET before)");

        // ---- PATCH with the hand-written, schema-conformant literal -----------------------------
        string patchBody = SchemaConformantPatchBody(
            timelineId,
            projectId,
            videoTrackId: Guid.NewGuid(),
            audioTrackId: Guid.NewGuid(),
            itemId: Guid.NewGuid(),
            projectFileId: projectFileId,
            baseRevision: 1);

        using HttpRequestMessage patch = new(HttpMethod.Patch, itemRoute)
        {
            Content = new StringContent(patchBody, Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage patched = await client.SendAsync(patch);

        patched.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"the schema-conformant literal must bind over real MVC: {await patched.Content.ReadAsStringAsync()}");

        ParseJson(await patched.Content.ReadAsStringAsync())
            .GetProperty("revision").GetInt32().Should().Be(2);

        // ---- GET after the PATCH: the same encoding, and every enum a camelCase string ----------
        JsonElement after = await GetJsonAsync(client, itemRoute);
        AssertEveryContractEnumIsAString(after, "$(GET after)");

        JsonElement document = after.GetProperty("document");
        document.GetProperty("settings").GetProperty("aspectPolicy").GetString().Should().Be("fit");

        JsonElement tracks = document.GetProperty("tracks");
        tracks.GetArrayLength().Should().Be(2);
        tracks[0].GetProperty("kind").GetString().Should().Be("video");
        tracks[1].GetProperty("kind").GetString().Should().Be("audio");

        JsonElement item = tracks[0].GetProperty("items")[0];
        item.GetProperty("type").GetString().Should().Be("insertClip");
        item.GetProperty("assetRef").GetProperty("kind").GetString().Should().Be("projectFile");
        item.GetProperty("assetRef").GetProperty("projectFileId").GetGuid().Should().Be(projectFileId);
        item.GetProperty("provenance").GetProperty("origin").GetString().Should().Be("manual");
        item.GetProperty("quad").GetProperty("keys")[0].GetProperty("interp").GetString()
            .Should().Be("linear");

        // And the same encoding is what reached the database, not merely what came back.
        using InferenceApiDbContext verifier = CreateContext(databaseName);
        (await verifier.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timelineId))
            .DocumentJson.Should().Contain("\"aspectPolicy\":\"fit\"");
    }

    /// <summary>
    /// An integer enum is a <b>binding failure</b>, and the endpoint answers its ordinary
    /// <c>400</c> — never a <c>500</c>, never a silent accept. The action is therefore never
    /// entered and nothing is written.
    /// </summary>
    /// <remarks>
    /// This is D4, pinned. The old integer form is not "also acceptable": the wire format IS the
    /// schema, and an ambiguous <c>0</c> that has to be resolved against a C# declaration is not
    /// that format. Accepted deliberately, as the cost of a contract that is unambiguous in both
    /// directions.
    /// </remarks>
    [Fact]
    public async Task PatchEditTimeline_OverRealKestrel_RejectsAnIntegerEnumBodyWithA400AndWritesNothing()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid ownerId;
        Guid projectFileId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;
            projectFileId = (await AddProjectFileAsync(db, projectId, "source.mp4")).Id;
        }

        await using WebApplication host = await StartHostedApiAsync(databaseName, ownerId);
        using HttpClient client = new()
        {
            BaseAddress = new Uri(HostAddress(host)),
            Timeout = TimeSpan.FromSeconds(120)
        };

        string collectionRoute = $"api/v1/projects/{projectId}/edit-timelines";

        using HttpResponseMessage created = await client.PostAsync(
            collectionRoute,
            new StringContent("""{"name":"Integer enums"}""", Encoding.UTF8, "application/json"));

        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        Guid timelineId = ParseJson(await created.Content.ReadAsStringAsync()).GetProperty("id").GetGuid();
        string itemRoute = $"{collectionRoute}/{timelineId}";

        // The ONLY difference from the accepted literal: aspectPolicy is the old integer form.
        string patchBody = SchemaConformantPatchBody(
            timelineId,
            projectId,
            videoTrackId: Guid.NewGuid(),
            audioTrackId: Guid.NewGuid(),
            itemId: Guid.NewGuid(),
            projectFileId: projectFileId,
            baseRevision: 1,
            aspectPolicyJson: "0");

        using HttpRequestMessage patch = new(HttpMethod.Patch, itemRoute)
        {
            Content = new StringContent(patchBody, Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage rejected = await client.SendAsync(patch);
        string rejectionBody = await rejected.Content.ReadAsStringAsync();

        rejected.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            $"an integer enum must be a client error, not a server one and not an accept: {rejectionBody}");
        rejected.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        rejected.StatusCode.Should().NotBe(HttpStatusCode.OK);
        rejected.Content.Headers.ContentType!.MediaType.Should().Be(
            "application/problem+json",
            "the rejection must be the endpoint's normal client-error shape, i.e. MVC's problem details");

        // ---- And the 400 must be ABOUT the integer, not about the rest of the document ----------
        // Without this, the test passes for the wrong reason. With the defect restored, MVC has no
        // enum converter at all, so the literal's *camelCase strings* fail to bind and the very
        // same 400 comes back — a rejection that would prove the OPPOSITE of what this test
        // claims. Attribution is what separates the two: in the fixed world everything else binds
        // and `aspectPolicy` is the only thing MVC can complain about, while the other contract
        // enums must be absent from the response entirely.
        string rejectionLower = rejectionBody.ToLowerInvariant();
        rejectionLower.Should().Contain(
            "aspectpolicy",
            $"the 400 must be attributed to the integer enum: {rejectionBody}");
        rejectionLower.Should().NotContain(
            "interp",
            $"the camelCase enums must have bound, so nothing may be attributed to them: {rejectionBody}");
        rejectionLower.Should().NotContain(
            "origin",
            $"the camelCase enums must have bound, so nothing may be attributed to them: {rejectionBody}");
        rejectionLower.Should().NotContain(
            "kind",
            $"the camelCase enums must have bound, so nothing may be attributed to them: {rejectionBody}");

        // A FRESH context: the request's own context would answer from its identity map, so a
        // mutated-but-unsaved entity would look written.
        using InferenceApiDbContext verifier = CreateContext(databaseName);
        EditTimelineEntity reloaded = await verifier.EditTimelines.AsNoTracking()
            .SingleAsync(t => t.Id == timelineId);

        reloaded.Revision.Should().Be(1, "a body that never bound must not advance the revision");
        (await verifier.EditTimelineOps.CountAsync()).Should().Be(0, "and must append no op row");
        // The timeline above was created through the real POST, which (since P3) writes exactly one
        // snapshot — revision 1, at birth. The rejected PATCH must not have added a second one.
        (await verifier.EditTimelineSnapshots.CountAsync()).Should().Be(1,
            "only the revision-1 birth snapshot the POST wrote; a body that never bound snapshots nothing");
        (await verifier.EditTimelineSnapshots.SingleAsync()).Revision.Should().Be(1);
    }

    /// <summary>
    /// Every one of the five contract enums, asserted on the <b>STORED TEXT</b> rather than on a
    /// deserialized object — a round trip through the same reader would launder a wrong encoding
    /// back into a right-looking value.
    /// </summary>
    /// <remarks>
    /// The absence half is non-vacuous by construction: <see cref="ContractDocument"/> names its
    /// tracks <c>"V"</c>/<c>"A"</c> and its timeline <c>"Contract"</c>, so a bare quoted
    /// <c>"Video"</c>/<c>"Fit"</c>/<c>"Linear"</c>/<c>"Manual"</c>/<c>"ProjectFile"</c> cannot
    /// appear as a legitimate name and its absence really does mean "no PascalCase enum was
    /// written".
    /// </remarks>
    [Fact]
    public async Task PatchEditTimeline_PersistsEveryContractEnumAsACamelCaseString()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;
        Guid projectFileId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;
            projectFileId = (await AddProjectFileAsync(db, projectId, "source.mp4")).Id;

            EditTimelineEntity timeline = await AddTimelineAsync(
                db, projectId, "Contract", BlankDocument(projectId, Guid.NewGuid(), "Contract"), revision: 1);
            timelineId = timeline.Id;
        }

        EditTimeline submitted = ContractDocument(projectId, timelineId, projectFileId);

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
                projectId, timelineId, new PatchEditTimelineRequest(1, null, submitted), CancellationToken.None);

            result.Result.Should().BeOfType<OkObjectResult>(
                "the all-five-enums contract document must be valid; a rejection here would mean the " +
                "fixture, not the encoding, is what is under test");
        }

        string storedJson;
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            storedJson = (await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timelineId)).DocumentJson;
        }

        // ---- Present: the schema's camelCase literals, for all five enums ----------------------
        storedJson.Should().Contain("\"aspectPolicy\":\"fit\"", "AspectPolicy");
        storedJson.Should().Contain("\"kind\":\"video\"", "TrackKind");
        storedJson.Should().Contain("\"kind\":\"audio\"", "TrackKind");
        storedJson.Should().Contain("\"kind\":\"projectFile\"", "AssetRefKind");
        storedJson.Should().Contain("\"origin\":\"manual\"", "ProvenanceOrigin");
        storedJson.Should().Contain("\"interp\":\"linear\"", "InterpKind");

        // ---- Absent: the PascalCase literals the old naming-policy-less converter wrote --------
        storedJson.Should().NotContain("\"Video\"");
        storedJson.Should().NotContain("\"Fit\"");
        storedJson.Should().NotContain("\"Linear\"");
        storedJson.Should().NotContain("\"Manual\"");
        storedJson.Should().NotContain("\"ProjectFile\"");
        AssertNoPascalCaseEnumEncoding(storedJson);

        // ---- And the controller's own GET reads back exactly what was written ------------------
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController reader = CreateController(db, ownerId);

            OkObjectResult ok = (await reader.GetEditTimeline(projectId, timelineId, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>().Subject;
            EditTimelineResponse body = ok.Value.Should().BeOfType<EditTimelineResponse>().Subject;

            body.Revision.Should().Be(2);
            body.Document.Should().BeEquivalentTo(
                submitted, "what a GET returns must equal what the PATCH wrote, enum by enum");
        }
    }

    /// <summary>
    /// A <b>legacy PascalCase row is MEASURED, not assumed</b> (D5). The brief predicted such a row
    /// would stop deserializing once the encoding changed; the measurement says otherwise, because
    /// <see cref="JsonStringEnumConverter"/> reads enum NAMES case-insensitively. This test asserts
    /// what is actually observed — the row still reads, and the next PATCH rewrites it camelCase —
    /// rather than forcing the predicted failure into the read path.
    /// </summary>
    /// <remarks>
    /// <b>No migration is written, and none is needed.</b> A legacy row is readable, so nothing has
    /// to be rewritten in place; it converges on the contract the first time anybody edits it. The
    /// genuinely unparseable case is a different defect with its own existing answer — the
    /// <c>500</c>-with-a-message raised by <c>GetEditTimeline_Returns500_WhenTheStoredDocumentCannotBeParsed</c>
    /// — and this test deliberately leaves that path alone.
    /// </remarks>
    [Fact]
    public async Task GetEditTimeline_ReadsBackALegacyPascalCaseRow_AndPatchRewritesItAsCamelCase()
    {
        string databaseName = NewDatabaseName();
        Guid projectId;
        Guid timelineId;
        Guid ownerId;
        Guid projectFileId;

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            Project project = await AddProjectAsync(db, "Proj");
            ownerId = project.OwnerId;
            projectId = project.Id;
            projectFileId = (await AddProjectFileAsync(db, projectId, "source.mp4")).Id;

            EditTimeline legacyDocument = ContractDocument(projectId, Guid.NewGuid(), projectFileId);

            // The PRE-T-022 writer, held separately from the production options on purpose.
            EditTimelineEntity timeline = await AddTimelineAsync(
                db,
                projectId,
                "Legacy",
                legacyDocument,
                revision: 1,
                documentJsonOptions: LegacyPascalCaseDocumentJsonOptions);
            timelineId = timeline.Id;

            // Proof the fixture really is legacy: the old writer DID persist PascalCase.
            timeline.DocumentJson.Should().Contain("\"kind\":\"Video\"");
            timeline.DocumentJson.Should().Contain("\"aspectPolicy\":\"Fit\"");
            timeline.DocumentJson.Should().Contain("\"interp\":\"Linear\"");
            timeline.DocumentJson.Should().Contain("\"origin\":\"Manual\"");
            timeline.DocumentJson.Should().Contain("\"kind\":\"ProjectFile\"");
        }

        // ---- MEASURED: the read path still serves it, names matching case-insensitively ---------
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController reader = CreateController(db, ownerId);

            OkObjectResult ok = (await reader.GetEditTimeline(projectId, timelineId, CancellationToken.None))
                .Result.Should()
                .BeOfType<OkObjectResult>(
                    "the measured behaviour is that a PascalCase document_json STILL PARSES — " +
                    "JsonStringEnumConverter reads enum names case-insensitively")
                .Subject;

            EditTimelineResponse body = ok.Value.Should().BeOfType<EditTimelineResponse>().Subject;

            body.Revision.Should().Be(1);
            body.Document.Settings.AspectPolicy.Should().Be(AspectPolicy.Fit);
            body.Document.Tracks[0].Kind.Should().Be(TrackKind.Video);
            body.Document.Tracks[1].Kind.Should().Be(TrackKind.Audio);

            InsertClip legacyInsert = body.Document.Tracks[0].Items[0]
                .Should().BeOfType<InsertClip>().Subject;
            legacyInsert.AssetRef!.Kind.Should().Be(AssetRefKind.ProjectFile);
            legacyInsert.Provenance!.Origin.Should().Be(ProvenanceOrigin.Manual);
        }

        // ---- And a later PATCH rewrites it camelCase -------------------------------------------
        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            EditTimelinesController controller = CreateController(db, ownerId);

            ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(
                projectId,
                timelineId,
                new PatchEditTimelineRequest(1, null, ContractDocument(projectId, timelineId, projectFileId)),
                CancellationToken.None);

            result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<EditTimelineResponse>().Subject.Revision.Should().Be(2);
        }

        using (InferenceApiDbContext db = CreateContext(databaseName))
        {
            string rewritten = (await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timelineId)).DocumentJson;

            rewritten.Should().Contain("\"kind\":\"video\"", "the PATCH rewrites the legacy row camelCase");
            rewritten.Should().Contain("\"aspectPolicy\":\"fit\"");
            rewritten.Should().NotContain("\"kind\":\"Video\"");
            AssertNoPascalCaseEnumEncoding(rewritten);
        }
    }

    /// <summary>
    /// The seeded-from-<c>TimelineManifest</c> POST path persists camelCase too. The v1 seeder is a
    /// different producer from a client PATCH — it builds the document server-side — so a fix that
    /// only covered the PATCH path would leave seeded timelines storing PascalCase while every
    /// PATCH test passed.
    /// </summary>
    [Fact]
    public async Task CreateEditTimeline_FromASeedStepResult_PersistsItsDocumentAsCamelCaseStrings()
    {
        string databaseName = NewDatabaseName();
        Guid timelineId;

        using InferenceApiDbContext db = CreateContext(databaseName);
        Project project = await AddProjectAsync(db, "Proj");

        ProjectFile videoFile = await AddProjectFileAsync(
            db, project.Id, "shoot.mp4", storageKey: "sources/shoot.mp4");
        ProjectFile musicFile = await AddProjectFileAsync(
            db, project.Id, "bed.mp3", storageKey: "audio/xyz.mp3");

        WorkflowExecution execution = await AddExecutionAsync(db, project.Id);
        string manifestKey = $"projects/{project.Id}/outputFiles/manifest.json";
        WorkflowStepResult stepResult = await AddStepResultAsync(db, execution, manifestKey);

        TimelineManifest manifest = BuildManifest(
            project.Id, execution.Id, videoFile.StorageKey, musicFile.OriginalFileName);

        EditTimelinesController controller = CreateController(
            db,
            project.OwnerId,
            StubStorage(project.Id, manifestKey, JsonSerializer.Serialize(manifest, DocumentJsonOptions)));

        ActionResult<EditTimelineResponse> result = await controller.CreateEditTimeline(
            project.Id, new CreateEditTimelineRequest("Seeded", stepResult.Id), CancellationToken.None);

        EditTimelineResponse body = result.Result.Should().BeOfType<ObjectResult>().Subject
            .Value.Should().BeOfType<EditTimelineResponse>().Subject;
        timelineId = body.Id;

        string storedJson = (await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timelineId))
            .DocumentJson;

        storedJson.Should().Contain("\"aspectPolicy\":\"fit\"", "AspectPolicy");
        storedJson.Should().Contain("\"kind\":\"video\"", "TrackKind, from the seeder's TrackMap");
        storedJson.Should().Contain("\"kind\":\"audio\"", "TrackKind, from the seeder's TrackMap");
        storedJson.Should().Contain("\"kind\":\"projectFile\"", "AssetRefKind, resolved by resolveAsset");
        storedJson.Should().Contain("\"origin\":\"generated\"", "ProvenanceOrigin, set by the seeder");
        AssertNoPascalCaseEnumEncoding(storedJson);

        // InterpKind is deliberately NOT asserted here: the seeder emits keyframes only when the
        // manifest carries a ducking envelope or a tracked quad, and BuildManifest (a fixture other
        // tests share) carries neither — so this manifest stores "gainDb":null and no "interp" key
        // exists to check. InterpKind's encoding is covered by the PATCH-side tests above, where a
        // keyframe is actually present; widening the shared fixture to reach it here would change
        // what those other tests are testing for no gain.

        // The seeder names its VIDEO track "Video" — a legitimate name that must not be confused
        // with an enum literal, which is exactly why the PascalCase checks above are anchored on
        // the property-level signature ("kind":"Video") rather than on a bare quoted word.
        storedJson.Should().Contain("\"name\":\"Video\"");
    }

    // ==========================================================================================
    // Harness — T-022 additions: the hand-written contract literal and its assertions.
    // ==========================================================================================

    /// <summary>
    /// A document carrying all five edit-v2 contract enums, built in C# so it can be handed
    /// straight to a controller call: <see cref="AspectPolicy.Fit"/>, <see cref="TrackKind"/>,
    /// <see cref="AssetRefKind.ProjectFile"/>, <see cref="ProvenanceOrigin.Manual"/> and
    /// <see cref="InterpKind.Linear"/>.
    /// </summary>
    private static EditTimeline ContractDocument(Guid projectId, Guid timelineId, Guid projectFileId)
    {
        InsertClip insert = new(
            Id: Guid.NewGuid(),
            Name: "insert",
            RecordIn: 0,
            RecordOut: 30,
            AssetRef: new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: projectFileId),
            Quad: new Keyframed<QuadCorners>(
                Value: SampleCorners(),
                Keys: [new Keyframe<QuadCorners>(Frame: 0, Value: SampleCorners(), Interp: InterpKind.Linear)]),
            Enabled: true,
            Provenance: new Provenance(ProvenanceOrigin.Manual));

        return new EditTimeline(
            id: timelineId,
            projectId: projectId,
            name: "Contract",
            settings: new EditTimelineSettings(
                Width: 1920,
                Height: 1080,
                Rate: new Rational(30, 1),
                SampleRate: 48000,
                PixelAspect: new Rational(1, 1),
                AspectPolicy: AspectPolicy.Fit,
                StartTimecodeFrames: 0),
            tracks:
            [
                // Named "V"/"A", never "Video"/"Audio": the absence assertions look for the quoted
                // PascalCase enum literals and a track named "Video" would defeat them.
                new Track(
                    Guid.NewGuid(), TrackKind.Video, "V", null, 100,
                    false, true, false, false, null, [insert]),
                new Track(
                    Guid.NewGuid(), TrackKind.Audio, "A", null, 100,
                    false, true, false, false, null, Array.Empty<Item>())
            ],
            markers: Array.Empty<Marker>());
    }

    private static QuadCorners SampleCorners() =>
        new(new QuadPoint(0, 0), new QuadPoint(1, 0), new QuadPoint(1, 1), new QuadPoint(0, 1));

    /// <summary>
    /// A fully hand-written PATCH body in the SCHEMA's own encoding, with all five contract enums
    /// in camelCase string form: <c>aspectPolicy</c>, the two track <c>kind</c>s, the asset ref's
    /// <c>kind</c>, a keyframe's <c>interp</c> and a provenance <c>origin</c>.
    /// </summary>
    /// <param name="aspectPolicyJson">
    /// The raw JSON token for <c>settings.aspectPolicy</c>, defaulting to the schema-conformant
    /// <c>"fit"</c>. Passing <c>0</c> produces the deliberately malformed body the integer-rejection
    /// test needs, with everything else byte-identical — so that test varies exactly one thing.
    /// </param>
    private static string SchemaConformantPatchBody(
        Guid timelineId,
        Guid projectId,
        Guid videoTrackId,
        Guid audioTrackId,
        Guid itemId,
        Guid projectFileId,
        int baseRevision,
        string aspectPolicyJson = "\"fit\"") =>
        $$"""
        {
          "baseRevision": {{baseRevision}},
          "ops": [{"op": "insert", "trackId": "V1"}],
          "document": {
            "schemaVersion": 2,
            "id": "{{timelineId}}",
            "projectId": "{{projectId}}",
            "name": "Camel contract",
            "settings": {
              "width": 1920,
              "height": 1080,
              "rate": {"num": 30, "den": 1},
              "sampleRate": 48000,
              "pixelAspect": {"num": 1, "den": 1},
              "aspectPolicy": {{aspectPolicyJson}},
              "startTimecodeFrames": 0
            },
            "tracks": [
              {
                "id": "{{videoTrackId}}",
                "kind": "video",
                "name": "V",
                "color": null,
                "height": 100.0,
                "locked": false,
                "enabled": true,
                "mute": false,
                "solo": false,
                "busId": null,
                "items": [
                  {
                    "type": "insertClip",
                    "id": "{{itemId}}",
                    "name": "insert",
                    "recordIn": 0,
                    "recordOut": 30,
                    "assetRef": {
                      "kind": "projectFile",
                      "projectFileId": "{{projectFileId}}",
                      "stepResultId": null
                    },
                    "quad": {
                      "value": {
                        "topLeft": {"x": 0.0, "y": 0.0},
                        "topRight": {"x": 1.0, "y": 0.0},
                        "bottomRight": {"x": 1.0, "y": 1.0},
                        "bottomLeft": {"x": 0.0, "y": 1.0}
                      },
                      "keys": [
                        {
                          "frame": 0,
                          "value": {
                            "topLeft": {"x": 0.0, "y": 0.0},
                            "topRight": {"x": 1.0, "y": 0.0},
                            "bottomRight": {"x": 1.0, "y": 1.0},
                            "bottomLeft": {"x": 0.0, "y": 1.0}
                          },
                          "interp": "linear"
                        }
                      ]
                    },
                    "enabled": true,
                    "provenance": {
                      "origin": "manual",
                      "stepResultId": null,
                      "agentDefinitionId": null,
                      "providerId": null,
                      "offeredId": null,
                      "rationale": "hand-written contract literal",
                      "editedByUser": false,
                      "agentName": null,
                      "providerName": null
                    }
                  }
                ]
              },
              {
                "id": "{{audioTrackId}}",
                "kind": "audio",
                "name": "A",
                "color": null,
                "height": 100.0,
                "locked": false,
                "enabled": true,
                "mute": false,
                "solo": false,
                "busId": null,
                "items": []
              }
            ],
            "markers": []
          }
        }
        """;

    /// <summary>
    /// The four property names the edit-v2 contract types as enums. Scanning for them BY NAME (and
    /// recursively, at any depth) is what makes "zero integer enums" a check on the whole body
    /// rather than on the handful of paths a test happened to think of.
    /// </summary>
    private static readonly string[] ContractEnumPropertyNames =
        ["kind", "aspectPolicy", "interp", "origin"];

    /// <summary>
    /// Walks a response body and asserts every property named like a contract enum is a JSON
    /// STRING. A bare integer enum — the pre-T-022 wire format — fails here wherever it appears.
    /// </summary>
    private static void AssertEveryContractEnumIsAString(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (ContractEnumPropertyNames.Contains(property.Name))
                    {
                        property.Value.ValueKind.Should().Be(
                            JsonValueKind.String,
                            $"{path}.{property.Name} is a contract enum and must travel as a camelCase " +
                            "string, never as a bare integer");
                    }

                    AssertEveryContractEnumIsAString(property.Value, $"{path}.{property.Name}");
                }

                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement child in element.EnumerateArray())
                {
                    AssertEveryContractEnumIsAString(child, $"{path}[{index++}]");
                }

                break;
        }
    }

    /// <summary>
    /// The property-level PascalCase signatures the old naming-policy-less
    /// <see cref="JsonStringEnumConverter"/> wrote. Anchored on <c>"property":"Value"</c> pairs
    /// rather than on bare quoted words, so a track legitimately NAMED "Video" cannot produce a
    /// false positive — the seeder emits exactly that.
    /// </summary>
    private static void AssertNoPascalCaseEnumEncoding(string documentJson)
    {
        documentJson.Should().NotContain("\"kind\":\"Video\"");
        documentJson.Should().NotContain("\"kind\":\"Audio\"");
        documentJson.Should().NotContain("\"kind\":\"ProjectFile\"");
        documentJson.Should().NotContain("\"kind\":\"StepResult\"");
        documentJson.Should().NotContain("\"aspectPolicy\":\"Fit\"");
        documentJson.Should().NotContain("\"aspectPolicy\":\"Fill\"");
        documentJson.Should().NotContain("\"aspectPolicy\":\"Stretch\"");
        documentJson.Should().NotContain("\"interp\":\"Linear\"");
        documentJson.Should().NotContain("\"interp\":\"EaseInQuad\"");
        documentJson.Should().NotContain("\"origin\":\"Manual\"");
        documentJson.Should().NotContain("\"origin\":\"Generated\"");
        documentJson.Should().NotContain("\"origin\":\"Imported\"");
        documentJson.Should().NotContain("\"origin\":\"Analyzed\"");
    }

    private static JsonElement ParseJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string route)
    {
        using HttpResponseMessage response = await client.GetAsync(route);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return ParseJson(await response.Content.ReadAsStringAsync());
    }

    // ==========================================================================================
    // Harness.
    // ==========================================================================================

    private static string NewDatabaseName() => Guid.NewGuid().ToString();

    private static DbContextOptions<InferenceApiDbContext> CreateOptions(string databaseName) =>
        new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(databaseName)
            // PATCH wraps its write in an explicit transaction, which is the contract; the
            // in-memory provider has no transactions and reports that as a THROWN warning by
            // default, so the harness ignores exactly that event rather than dropping the
            // transaction from the controller to suit the test.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static InferenceApiDbContext CreateContext(string databaseName) =>
        new(CreateOptions(databaseName));

    private static ICurrentUser StubCurrentUser(Guid userId)
    {
        Mock<ICurrentUser> currentUser = new();
        currentUser.SetupGet(u => u.UserId).Returns(userId);
        currentUser.SetupGet(u => u.Email).Returns("test@example.com");
        currentUser.SetupGet(u => u.IsAuthenticated).Returns(true);
        currentUser.SetupGet(u => u.IsPlatformAdmin).Returns(false);
        currentUser.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(userId));
        currentUser.SetupGet(u => u.OrgRole).Returns(ReelBolt.Shared.Tenancy.OrganizationRole.Member);

        return currentUser.Object;
    }

    private static EditTimelinesController CreateController(
        InferenceApiDbContext db,
        Guid userId,
        IFileStorageService? storage = null) =>
        new(db, StubCurrentUser(userId), TestTenancy.Access(db, StubCurrentUser(userId)), storage ?? new Mock<IFileStorageService>().Object, new Mock<ReelBolt.Inference.Api.Services.Workflows.IWorkflowExecutionService>().Object);

    /// <summary>
    /// A body of exactly <paramref name="byteCount"/> ASCII bytes that CANNOT bind as a
    /// <c>PatchEditTimelineRequest</c>. That is the point: if MVC had received and bound it, the
    /// response would be a validation error rather than a <c>413</c>, so a <c>413</c> proves the
    /// rejection happened at the transport, before binding.
    /// </summary>
    private static string UnbindableBodyOfExactByteCount(int byteCount)
    {
        string prefix = "{\"baseRevision\":1,\"ops\":[],\"document\":{\"name\":\"";
        string suffix = "\"}}";

        int padding = byteCount - prefix.Length - suffix.Length;
        padding.Should().BeGreaterThan(0, "the requested size must leave room for the JSON envelope");

        return prefix + new string('a', padding) + suffix;
    }

    /// <summary>
    /// A <c>{"pad":"aaa…"}</c> document of exactly <paramref name="byteCount"/> bytes — all ASCII, so
    /// characters and bytes coincide, and <see cref="JsonElement.GetRawText"/> gives the string back
    /// verbatim, which is what <c>SerializeOps</c> stores.
    /// </summary>
    private static string OpsBlobOfExactByteCount(int byteCount)
    {
        const int wrapperLength = 10; // {"pad":""}

        return "{\"pad\":\"" + new string('a', byteCount - wrapperLength) + "\"}";
    }

    /// <summary>
    /// A bindable, valid PATCH body under every ceiling. Serialized with plain web defaults and NO
    /// converter, because the enum encoding comes from the enum types' own <c>[JsonConverter]</c>
    /// attribute — so this writes the schema's camelCase strings (<c>"kind":"video"</c>) with
    /// nothing registered anywhere, exactly as MVC's own JSON options do on the wire.
    /// </summary>
    /// <remarks>
    /// <b>Corrected by T-022.</b> This comment used to claim "MVC's own JSON options carry no
    /// string-enum converter, so enums travel as numbers on the wire". That described the defect,
    /// not the contract: it is now false in both directions — MVC still registers no converter, and
    /// enums still do not travel as numbers, because the type-level attribute supplies the encoding
    /// without any options object at all.
    /// </remarks>
    private static string UnderLimitBody(int baseRevision, EditTimeline document) =>
        JsonSerializer.Serialize(
            new
            {
                baseRevision,
                ops = Array.Empty<object>(),
                document
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>
    /// A real Kestrel on a loopback port-0 socket with this controller mounted through MVC, a
    /// <see cref="DbSet"/>-backed in-memory database and a trivial test authentication scheme so
    /// <c>[Authorize]</c> passes. Nothing here is stubbed that the limit under test depends on: the
    /// body limit is Kestrel's own, driven by the action's
    /// <see cref="RequestSizeLimitAttribute"/>.
    /// </summary>
    private static async Task<WebApplication> StartHostedApiAsync(string databaseName, Guid ownerId)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));

        builder.Services
            .AddAuthentication(TestAuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationScheme, configureOptions: null);
        builder.Services.AddControllers().AddApplicationPart(typeof(EditTimelinesController).Assembly);

        builder.Services.AddScoped(_ => CreateContext(databaseName));
        builder.Services.AddScoped(_ => StubCurrentUser(ownerId));
        // A7b: the controller resolves project access from DI now, so the hosted app has to supply it
        // too. Real service over the same in-memory context and the same stub caller as the action.
        builder.Services.AddScoped<IProjectAccessService>(sp =>
            TestTenancy.Access(sp.GetRequiredService<InferenceApiDbContext>(), sp.GetRequiredService<ICurrentUser>()));
        builder.Services.AddSingleton(_ => new Mock<IFileStorageService>().Object);
        builder.Services.AddSingleton(_ => new Mock<ReelBolt.Inference.Api.Services.Workflows.IWorkflowExecutionService>().Object);

        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

        await app.StartAsync();
        return app;
    }

    private static string HostAddress(WebApplication app) =>
        app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

    private const string TestAuthenticationScheme = "EditTimelinesTestScheme";

    /// <summary>Authenticates every request, so <c>[Authorize]</c> passes without a real JWT.</summary>
    private sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            ClaimsIdentity identity = new(
                [new Claim(ClaimTypes.NameIdentifier, "test-user")],
                TestAuthenticationScheme);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestAuthenticationScheme)));
        }
    }

    /// <summary>
    /// The one feature write <see cref="RequestSizeLimitAttribute"/> performs, captured so the
    /// attribute's private byte count can be asserted through behaviour instead of reflection on a
    /// field name.
    /// </summary>
    private sealed class StubMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; }
    }

    /// <summary>
    /// A throwaway container with MVC's own registrations, because
    /// <see cref="RequestSizeLimitAttribute"/> is an <see cref="IFilterFactory"/> that resolves its
    /// filter from the request services. Building the real registration is what keeps this
    /// assertion about the framework's behaviour rather than about a hand-made stand-in.
    /// </summary>
    private static IServiceProvider MvcFilterServiceProvider()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddControllers();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// <see cref="InferenceApiDbContext"/> with the commit failing exactly the way a racing writer
    /// makes it fail. Reads are untouched, so the <c>409</c> body's <c>CurrentRevision</c> is still
    /// served from the database.
    /// </summary>
    private sealed class ConcurrencyLosingDbContext : InferenceApiDbContext
    {
        public ConcurrencyLosingDbContext(DbContextOptions<InferenceApiDbContext> options)
            : base(options)
        {
        }

        public int SaveAttempts { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            return Task.FromException<int>(new DbUpdateConcurrencyException(
                "simulated racing writer: the revision token moved between the check and the commit"));
        }
    }

    private static IFileStorageService StubStorage(Guid projectId, string storageKey, string content)
    {
        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.DownloadAsync(projectId, storageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(content)));

        return storage.Object;
    }

    private static async Task<Project> AddProjectAsync(InferenceApiDbContext db, string name)
    {
        Guid ownerId = Guid.NewGuid();
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project;
    }

    private static async Task<ProjectFile> AddProjectFileAsync(
        InferenceApiDbContext db,
        Guid projectId,
        string originalFileName,
        string? storageKey = null)
    {
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = originalFileName,
            StorageKey = storageKey ?? $"projects/{projectId}/userFiles/{originalFileName}",
            MimeType = "video/mp4",
            SizeBytes = 1024,
            UploadedAt = DateTime.UtcNow
        };

        db.ProjectFiles.Add(file);
        await db.SaveChangesAsync();
        return file;
    }

    private static async Task<WorkflowExecution> AddExecutionAsync(InferenceApiDbContext db, Guid projectId)
    {
        WorkflowDefinition definition = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = "wf",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definition.Id,
            ProjectId = projectId
        };

        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();
        return execution;
    }

    private static async Task<WorkflowStepResult> AddStepResultAsync(
        InferenceApiDbContext db,
        WorkflowExecution execution,
        string? timelineStorageKey,
        StepType stepType = StepType.Agent)
    {
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = execution.WorkflowDefinitionId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = stepType
        };

        WorkflowStepResult stepResult = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = step.Id,
            TimelineStorageKey = timelineStorageKey,
            ExecutedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        };

        db.WorkflowSteps.Add(step);
        db.WorkflowStepResults.Add(stepResult);
        await db.SaveChangesAsync();
        return stepResult;
    }

    /// <summary>
    /// Seeds an <c>edit_timelines</c> row, serializing the document with
    /// <paramref name="documentJsonOptions"/> — <see cref="DocumentJsonOptions"/> by default, or
    /// <see cref="LegacyPascalCaseDocumentJsonOptions"/> to plant a pre-T-022 row whose stored
    /// encoding is the old PascalCase one.
    /// </summary>
    private static async Task<EditTimelineEntity> AddTimelineAsync(
        InferenceApiDbContext db,
        Guid projectId,
        string name,
        EditTimeline document,
        int revision,
        JsonSerializerOptions? documentJsonOptions = null)
    {
        EditTimelineEntity timeline = new()
        {
            Id = document.Id,
            ProjectId = projectId,
            Name = name,
            DocumentJson = JsonSerializer.Serialize(document, documentJsonOptions ?? DocumentJsonOptions),
            Revision = revision,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.EditTimelines.Add(timeline);
        await db.SaveChangesAsync();
        return timeline;
    }

    /// <summary>
    /// The blank document <c>POST</c> builds, reproduced for arrangement: two empty tracks, the
    /// documented 1920×1080/30/48000/1:1 placeholder settings, seeder-derived track ids.
    /// </summary>
    private static EditTimeline BlankDocument(Guid projectId, Guid timelineId, string name) =>
        new(
            id: timelineId,
            projectId: projectId,
            name: name,
            settings: new EditTimelineSettings(
                Width: 1920,
                Height: 1080,
                Rate: new Rational(30, 1),
                SampleRate: 48000,
                PixelAspect: new Rational(1, 1),
                AspectPolicy: AspectPolicy.Fit,
                StartTimecodeFrames: 0),
            tracks:
            [
                new Track(
                    EditTimelineSeeder.TrackId("V1"), TrackKind.Video, "Video", null, 100,
                    false, true, false, false, null, Array.Empty<Item>()),
                new Track(
                    EditTimelineSeeder.TrackId("A1"), TrackKind.Audio, "Audio", null, 100,
                    false, true, false, false, null, Array.Empty<Item>())
            ],
            markers: Array.Empty<Marker>());

    /// <summary>
    /// A document whose single video-track item is a <see cref="MediaClip"/> referencing
    /// <paramref name="projectFileId"/> — the shape that exercises the validator's ownership check.
    /// </summary>
    private static EditTimeline DocumentWithMediaClip(
        Guid projectId,
        Guid timelineId,
        string name,
        Guid projectFileId,
        long recordIn,
        long recordOut)
    {
        EditTimeline blank = BlankDocument(projectId, timelineId, name);
        MediaClip clip = new(
            Id: Guid.NewGuid(),
            Name: "clip",
            RecordIn: recordIn,
            RecordOut: recordOut,
            AssetRef: new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: projectFileId));

        return blank with
        {
            Tracks =
            [
                blank.Tracks[0] with { Items = [clip] },
                blank.Tracks[1]
            ]
        };
    }

    /// <summary>
    /// A document whose single video-track item is a <see cref="TitleClip"/> carrying
    /// <paramref name="provenance"/> on track 0 — the fixture for the provenance ownership wiring.
    /// </summary>
    /// <remarks>
    /// A title clip needs no asset, so this isolates <see cref="Provenance.StepResultId"/> from
    /// <c>AssetRef</c> resolution entirely: whatever the validator says about this document is about
    /// provenance and nothing else. The two-track variant used by the foreign/unknown test is built
    /// inline from <see cref="BlankDocument"/> instead, because it deliberately puts a second,
    /// DIFFERENT provenance on the other track.
    /// </remarks>
    private static EditTimeline DocumentWithProvenance(
        Guid projectId,
        Guid timelineId,
        string name,
        Provenance provenance)
    {
        EditTimeline blank = BlankDocument(projectId, timelineId, name);
        TitleClip title = new(
            Id: Guid.NewGuid(),
            Name: "titled",
            RecordIn: 0,
            RecordOut: 30,
            Text: "titled",
            Provenance: provenance);

        return blank with
        {
            Tracks =
            [
                blank.Tracks[0] with { Items = [title] },
                blank.Tracks[1]
            ]
        };
    }

    /// <summary>
    /// A document whose single item is a <see cref="TitleClip"/> — the payload-size guard's fixture,
    /// since a clip's text is the cheapest way to reach a given serialized size.
    /// </summary>
    private static EditTimeline DocumentWithTitleClip(
        Guid projectId,
        Guid timelineId,
        string name,
        string text)
    {
        EditTimeline blank = BlankDocument(projectId, timelineId, name);
        TitleClip title = new(
            Id: Guid.NewGuid(),
            Name: "title",
            RecordIn: 0,
            RecordOut: 30,
            Text: text);

        return blank with
        {
            Tracks =
            [
                blank.Tracks[0] with { Items = [title] },
                blank.Tracks[1]
            ]
        };
    }

    /// <summary>
    /// A one-video-track + one-music-track v1 manifest: the cut names its source by storage key, the
    /// music bed by file name, so one seeded create exercises both halves of <c>resolveAsset</c>.
    /// </summary>
    /// <param name="itemStepResultId">
    /// Optional <see cref="TimelineItem.StepResultId"/> planted on the CUT item, so a caller can seed
    /// a document whose mapped provenance names a real step result — the input the controller's
    /// provenance ownership map has to resolve. Optional, so the existing call sites that do not care
    /// about provenance are unchanged.
    /// </param>
    private static TimelineManifest BuildManifest(
        Guid projectId,
        Guid executionId,
        string videoStorageKey,
        string musicFileName,
        Guid? itemStepResultId = null) =>
        new(
            Version: 1,
            ProjectId: projectId,
            ExecutionId: executionId,
            OutputVideo: new TimelineOutputVideo(1920, 1080, 30, 8, "renders/out.mp4"),
            Sources: [new TimelineSource(0, videoStorageKey, 10, 1920, 1080, 30)],
            Tracks:
            [
                new TimelineTrack(
                    "track-video", "video", "Video", 0,
                    [
                        new TimelineItem(
                            Id: "cut-1",
                            TimelineStartSec: 0,
                            TimelineEndSec: 4,
                            SourceIndex: 0,
                            SourceStartSec: 0,
                            SourceEndSec: 4,
                            StepResultId: itemStepResultId)
                    ]),
                new TimelineTrack(
                    "track-music", "music", "Music", 1,
                    [
                        new TimelineItem(
                            Id: "music-1",
                            TimelineStartSec: 0,
                            TimelineEndSec: 8,
                            TrackName: musicFileName)
                    ])
            ]);
}
