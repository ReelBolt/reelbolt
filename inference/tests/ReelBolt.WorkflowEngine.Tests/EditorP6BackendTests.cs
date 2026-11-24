#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Interchange;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Editor v2, P6 (workflow speed) — the server half: interchange writers and the OTIO reader, the
/// smart-bin filter, the annotation endpoints, smart bins through the bins controller, and the
/// import/export endpoints.
/// </summary>
public class EditorP6BackendTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid FileA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid FileB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly AssetRef AssetA = new(AssetRefKind.ProjectFile, FileA);
    private static readonly AssetRef AssetB = new(AssetRefKind.ProjectFile, FileB);
    private static readonly EditTimelineSettings Settings = new(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static MediaClip Clip(string id, string name, long recordIn, long recordOut, AssetRef asset, long sourceIn = 0) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-{id}{id}{id}{id}-4{id}{id}{id}-8{id}{id}{id}-{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}"), name, recordIn, recordOut, asset, sourceIn);

    private static Track Track(string id, TrackKind kind, params Item[] items) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-0000-4000-8000-00000000000{(kind == TrackKind.Video ? 0 : 1)}"), kind, kind == TrackKind.Video ? $"V{id}" : $"A{id}", null, 64, false, true, false, false, null, items);

    private static EditTimeline Doc(params Track[] tracks) =>
        new(Guid.Parse("22222222-2222-4222-8222-222222222222"), ProjectId, "Teaser cut", Settings, tracks, [], null, null);

    // ── Interchange ──────────────────────────────────────────────────────────

    /// <summary>The CMX3600 fixture: a cut, a 30-frame dissolve into the second clip, CRLF line ends.</summary>
    private const string ExpectedEdl =
        "TITLE: Teaser cut\r\n" +
        "FCM: NON-DROP FRAME\r\n\r\n" +
        "001  AX       V     C        00:00:00:15 00:00:02:15 00:00:00:00 00:00:02:00\r\n" +
        "* FROM CLIP NAME: Opening\r\n" +
        "* SOURCE FILE: reelbolt://project-file/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\r\n\r\n" +
        "002  AX       V     D 030     00:00:04:00 00:00:08:00 00:00:02:00 00:00:06:00\r\n" +
        "* FROM CLIP NAME: Interview\r\n" +
        "* SOURCE FILE: reelbolt://project-file/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb\r\n" +
        "* EFFECT NAME: WIPELEFT\r\n\r\n";

    private static EditTimeline InterchangeDoc() => Doc(
        Track("1", TrackKind.Video,
            Clip("1", "Opening", 0, 60, AssetA, sourceIn: 15),
            new Transition(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"), "x", 60, 90, "wipeleft"),
            Clip("2", "Interview", 90, 180, AssetB, sourceIn: 150)),
        Track("2", TrackKind.Video,
            new Caption(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), "cap", 30, 75, "Hello there"),
            new Caption(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"), "cap2", 100, 160, "Second line")),
        Track("3", TrackKind.Audio, Clip("3", "Music", 0, 180, AssetA)));

    [Fact]
    public void Edl_matches_the_CMX3600_fixture_byte_for_byte()
    {
        TimelineInterchange.WriteEdl(InterchangeDoc()).Should().Be(ExpectedEdl);
    }

    [Fact]
    public void Srt_and_Vtt_carry_the_captions_in_order_with_millisecond_clocks()
    {
        string srt = TimelineInterchange.WriteSrt(InterchangeDoc());
        srt.Should().Be("1\n00:00:01,000 --> 00:00:02,500\nHello there\n\n2\n00:00:03,333 --> 00:00:05,333\nSecond line\n\n");
        string vtt = TimelineInterchange.WriteVtt(InterchangeDoc());
        vtt.Should().StartWith("WEBVTT\n\n00:00:01.000 --> 00:00:02.500\nHello there\n\n");
        vtt.Should().Contain("00:00:03.333 --> 00:00:05.333\nSecond line");
    }

    [Fact]
    public void Otio_round_trips_a_ReelBolt_document_losslessly_and_reads_as_plain_cuts_without_the_metadata()
    {
        EditTimeline original = InterchangeDoc();
        string otio = TimelineInterchange.WriteOtio(original);
        using JsonDocument parsed = JsonDocument.Parse(otio);
        JsonElement root = parsed.RootElement;
        root.GetProperty("OTIO_SCHEMA").GetString().Should().Be("Timeline.1");
        JsonElement video = root.GetProperty("tracks").GetProperty("children")[0];
        video.GetProperty("kind").GetString().Should().Be("Video");
        video.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("OTIO_SCHEMA").GetString()).Should().Equal("Clip.1", "Transition.1", "Clip.1");
        root.GetProperty("tracks").GetProperty("children")[1].GetProperty("children").EnumerateArray().Select(c => c.GetProperty("OTIO_SCHEMA").GetString()).Should().Equal("Gap.1", "Gap.1", "Gap.1", "Gap.1");
        video.GetProperty("children")[0].GetProperty("media_reference").GetProperty("target_url").GetString().Should().Be("reelbolt://project-file/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

        Guid newId = Guid.NewGuid();
        AssetRef? Resolve(string url) => TimelineInterchange.TryParseAssetUrl(url);
        TimelineInterchange.OtioImportResult restored = TimelineInterchange.ReadOtio(root, ProjectId, newId, "Restored", Resolve);
        restored.Succeeded.Should().BeTrue(string.Join("; ", restored.Errors.Concat(restored.UnresolvedUrls)));
        restored.FromReelBoltMetadata.Should().BeTrue();
        restored.Document!.Id.Should().Be(newId);
        restored.Document.Name.Should().Be("Restored");
        restored.Document.Tracks.Should().HaveCount(3);
        restored.Document.Tracks[1].Items.Should().HaveCount(2, "the captions survive through the metadata block");

        // The same file with the metadata stripped: another tool's OTIO. Cuts and the transition import; captions cannot.
        using JsonDocument stripped = JsonDocument.Parse(StripMetadata(root));
        TimelineInterchange.OtioImportResult plain = TimelineInterchange.ReadOtio(stripped.RootElement, ProjectId, newId, "Plain", Resolve);
        plain.Succeeded.Should().BeTrue(string.Join("; ", plain.Errors.Concat(plain.UnresolvedUrls)));
        plain.FromReelBoltMetadata.Should().BeFalse();
        IReadOnlyList<Item> items = plain.Document!.Tracks[0].Items;
        items.Select(i => i.GetType().Name).Should().Equal("MediaClip", "Transition", "MediaClip");
        items[0].RecordOut.Should().Be(60);
        items[1].RecordIn.Should().Be(60);
        items[1].RecordOut.Should().Be(90);
        items[2].RecordIn.Should().Be(90);
        ((MediaClip)items[2]).SourceIn.Should().Be(150);
        ((MediaClip)items[0]).Provenance!.Origin.Should().Be(ProvenanceOrigin.Imported);
        plain.Document.Tracks[1].Items.Should().BeEmpty("a caption is only a gap to another tool");
        plain.Document.Settings.Rate.Should().Be(new Rational(30, 1));
    }

    [Fact]
    public void Otio_import_refuses_media_this_project_does_not_hold_and_names_every_url()
    {
        string otio = TimelineInterchange.WriteOtio(InterchangeDoc());
        using JsonDocument parsed = JsonDocument.Parse(StripMetadata(JsonDocument.Parse(otio).RootElement));
        TimelineInterchange.OtioImportResult result = TimelineInterchange.ReadOtio(parsed.RootElement, ProjectId, Guid.NewGuid(), "x", url => url.Contains("aaaaaaaa") ? AssetA : null);
        result.Succeeded.Should().BeFalse();
        result.Document.Should().BeNull();
        result.UnresolvedUrls.Should().Equal("reelbolt://project-file/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    }

    [Fact]
    public void Otio_from_another_tool_imports_by_file_name_at_a_different_rate()
    {
        const string foreign = """
        {"OTIO_SCHEMA":"Timeline.1","name":"From Resolve","global_start_time":{"OTIO_SCHEMA":"RationalTime.1","rate":24,"value":0},
         "tracks":{"OTIO_SCHEMA":"Stack.1","children":[
           {"OTIO_SCHEMA":"Track.1","kind":"Video","name":"V1","children":[
             {"OTIO_SCHEMA":"Gap.1","source_range":{"OTIO_SCHEMA":"TimeRange.1","start_time":{"OTIO_SCHEMA":"RationalTime.1","rate":24,"value":0},"duration":{"OTIO_SCHEMA":"RationalTime.1","rate":24,"value":24}}},
             {"OTIO_SCHEMA":"Clip.1","name":"shot","source_range":{"OTIO_SCHEMA":"TimeRange.1","start_time":{"OTIO_SCHEMA":"RationalTime.1","rate":24,"value":48},"duration":{"OTIO_SCHEMA":"RationalTime.1","rate":24,"value":72}},
              "media_reference":{"OTIO_SCHEMA":"ExternalReference.1","target_url":"file:///Volumes/footage/interview.mov"}}
           ]}]}}
        """;
        using JsonDocument parsed = JsonDocument.Parse(foreign);
        TimelineInterchange.OtioImportResult result = TimelineInterchange.ReadOtio(parsed.RootElement, ProjectId, Guid.NewGuid(), "x", url => url.EndsWith("interview.mov", StringComparison.Ordinal) ? AssetB : null);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors.Concat(result.UnresolvedUrls)));
        result.Document!.Settings.Rate.Should().Be(new Rational(24, 1));
        MediaClip clip = result.Document.Tracks[0].Items.Should().ContainSingle().Which.Should().BeOfType<MediaClip>().Subject;
        (clip.RecordIn, clip.RecordOut, clip.SourceIn).Should().Be((24L, 96L, 48L));
        clip.AssetRef.Should().Be(AssetB);
    }

    private static string StripMetadata(JsonElement root)
    {
        System.Text.Json.Nodes.JsonObject node = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(root.GetRawText())!;
        node.Remove("metadata");
        return node.ToJsonString();
    }

    // ── Smart filter ─────────────────────────────────────────────────────────

    [Fact]
    public void Smart_filter_clauses_AND_together_and_the_closed_words_are_enforced()
    {
        MediaSmartFilter filter = new(Keywords: ["Interview", " b-roll "], Kinds: ["video"], Favourite: true, NameContains: "cam");
        filter.Validate().Should().BeNull();
        MediaSmartFilter normalized = filter.Normalized();
        normalized.Keywords.Should().Equal("interview", "b-roll");

        MediaAnnotationView tagged = new(["interview", "b-roll", "day1"], "red", Favourite: true, Flag: false);
        normalized.Matches("video", "CAM-A.mp4", tagged).Should().BeTrue();
        normalized.Matches("audio", "CAM-A.mp4", tagged).Should().BeFalse("kind");
        normalized.Matches("video", "drone.mp4", tagged).Should().BeFalse("name fragment");
        normalized.Matches("video", "CAM-A.mp4", tagged with { Favourite = false }).Should().BeFalse("favourite");
        normalized.Matches("video", "CAM-A.mp4", tagged with { Keywords = ["interview"] }).Should().BeFalse("every keyword is required");
        normalized.Matches("video", "CAM-A.mp4", null).Should().BeFalse("keywords need an annotation");
        new MediaSmartFilter().Matches("image", "x.png", null).Should().BeTrue("an empty filter matches everything");

        new MediaSmartFilter(Kinds: ["film"]).Validate().Should().Contain("kind");
        new MediaSmartFilter(ColorLabel: "pink").Validate().Should().Contain("colorLabel");
        new MediaSmartFilter(Keywords: Enumerable.Repeat("k", 33).ToList()).Validate().Should().Contain("at most 32");
        MediaSmartFilter.Parse(new MediaSmartFilter(Flag: true, Kinds: ["audio"]).ToJson()).Should().BeEquivalentTo(new MediaSmartFilter(Flag: true, Kinds: ["audio"]));
    }

    // ── Controllers ──────────────────────────────────────────────────────────

    private static InferenceApiDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(databaseName).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static ICurrentUser Caller(Guid userId)
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

    private static Project SeedProject(InferenceApiDbContext db, Guid ownerId)
    {
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    private static ProjectFile SeedFile(InferenceApiDbContext db, Guid projectId, string fileName, string mime = "video/mp4")
    {
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = fileName,
            StorageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}/{fileName}",
            StorageBucket = "reelforge",
            Category = "userFiles",
            MimeType = mime,
            SizeBytes = 1024,
            UploadedAt = DateTime.UtcNow
        };
        db.ProjectFiles.Add(file);
        db.SaveChanges();
        return file;
    }

    private static MediaController Media(InferenceApiDbContext db, Guid userId)
    {
        MediaController controller = new(db, TestTenancy.Access(db, Caller(userId)), new Mock<IFileStorageService>().Object, new Mock<Amazon.S3.IAmazonS3>().Object, new Mock<IPublishEndpoint>().Object, NullLogger<MediaController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static MediaBinsController Bins(InferenceApiDbContext db, Guid userId) => new(db, TestTenancy.Access(db, Caller(userId)));

    private static EditTimelinesController Timelines(InferenceApiDbContext db, Guid userId)
    {
        ICurrentUser caller = Caller(userId);
        EditTimelinesController controller = new(db, caller, TestTenancy.Access(db, caller), new Mock<IFileStorageService>().Object, new Mock<IWorkflowExecutionService>().Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public async Task Annotations_upsert_then_list_normalise_keywords_and_refuse_foreign_or_unknown_sources()
    {
        using InferenceApiDbContext db = CreateContext(Guid.NewGuid().ToString());
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId);
        ProjectFile file = SeedFile(db, project.Id, "cam-a.mp4");
        Project foreign = SeedProject(db, Guid.NewGuid());
        ProjectFile foreignFile = SeedFile(db, foreign.Id, "theirs.mp4");
        MediaController controller = Media(db, userId);

        ActionResult<MediaAnnotationResponse> created = await controller.UpsertAnnotation(project.Id, $"file:{file.Id:D}",
            new UpsertMediaAnnotationRequest([" Interview", "b-roll", "interview"], "Good take", Json("\"red\""), true, null), CancellationToken.None);
        ObjectResult createdResult = created.Result.Should().BeOfType<ObjectResult>().Subject;
        createdResult.StatusCode.Should().Be(StatusCodes.Status201Created);
        MediaAnnotationResponse body = createdResult.Value.Should().BeOfType<MediaAnnotationResponse>().Subject;
        body.Keywords.Should().Equal("interview", "b-roll");
        (body.Note, body.ColorLabel, body.Favourite, body.Flag).Should().Be(("Good take", "red", true, false));

        // A second write keeps what it does not name (keywords, note) and clears the label with an explicit null.
        ActionResult<MediaAnnotationResponse> updated = await controller.UpsertAnnotation(project.Id, $"file:{file.Id:D}",
            new UpsertMediaAnnotationRequest(null, null, Json("null"), null, true), CancellationToken.None);
        MediaAnnotationResponse second = updated.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<MediaAnnotationResponse>().Subject;
        (second.Keywords.Count, second.Note, second.ColorLabel, second.Favourite, second.Flag).Should().Be((2, "Good take", null, true, true));

        ActionResult<List<MediaAnnotationResponse>> list = await controller.ListAnnotations(project.Id, CancellationToken.None);
        list.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<List<MediaAnnotationResponse>>().Subject.Should().ContainSingle().Which.MediaRef.Should().Be($"file:{file.Id:D}");

        (await controller.UpsertAnnotation(project.Id, $"file:{foreignFile.Id:D}", new UpsertMediaAnnotationRequest(["x"], null, default, null, null), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.UpsertAnnotation(project.Id, $"file:{file.Id:D}", new UpsertMediaAnnotationRequest(null, null, Json("\"pink\""), null, null), CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
        db.MediaAnnotations.Count().Should().Be(1);
    }

    [Fact]
    public async Task A_smart_bin_lists_the_matching_pool_sources_and_refuses_pins()
    {
        using InferenceApiDbContext db = CreateContext(Guid.NewGuid().ToString());
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId);
        ProjectFile interview = SeedFile(db, project.Id, "interview-cam-a.mp4");
        ProjectFile broll = SeedFile(db, project.Id, "drone.mp4");
        ProjectFile music = SeedFile(db, project.Id, "bed.mp3", "audio/mpeg");
        Project foreign = SeedProject(db, Guid.NewGuid());
        SeedFile(db, foreign.Id, "interview-theirs.mp4");
        db.MediaAnnotations.Add(new MediaAnnotation { Id = Guid.NewGuid(), ProjectId = project.Id, SourceKind = MediaSourceKind.ProjectFile, SourceProjectFileId = interview.Id, KeywordsJson = "[\"interview\"]", Favourite = true });
        db.MediaAnnotations.Add(new MediaAnnotation { Id = Guid.NewGuid(), ProjectId = project.Id, SourceKind = MediaSourceKind.ProjectFile, SourceProjectFileId = broll.Id, KeywordsJson = "[\"b-roll\"]" });
        db.SaveChanges();
        MediaBinsController bins = Bins(db, userId);

        ActionResult<MediaBinSummaryResponse> created = await bins.CreateBin(project.Id, new CreateMediaBinRequest("Favourite video", null, new MediaSmartFilter(Kinds: ["video"], Favourite: true)), CancellationToken.None);
        MediaBinSummaryResponse summary = created.Result.Should().BeOfType<ObjectResult>().Subject.Value.Should().BeOfType<MediaBinSummaryResponse>().Subject;
        summary.IsSmart.Should().BeTrue();
        summary.SmartFilter!.Kinds.Should().Equal("video");

        ActionResult<List<MediaBinItemDetailResponse>> listed = await bins.ListItems(project.Id, summary.Id, CancellationToken.None);
        List<MediaBinItemDetailResponse> rows = listed.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<List<MediaBinItemDetailResponse>>().Subject;
        rows.Should().ContainSingle().Which.MediaRef.Should().Be($"file:{interview.Id:D}");
        rows[0].FileName.Should().Be("interview-cam-a.mp4");
        rows[0].Kind.Should().Be(MediaKind.Video);

        // A name-fragment filter over the whole pool: audio included, foreign project excluded.
        ActionResult<MediaBinSummaryResponse> byName = await bins.CreateBin(project.Id, new CreateMediaBinRequest("Anything with e", null, new MediaSmartFilter(NameContains: "e")), CancellationToken.None);
        Guid byNameId = ((MediaBinSummaryResponse)((ObjectResult)byName.Result!).Value!).Id;
        List<MediaBinItemDetailResponse> all = (List<MediaBinItemDetailResponse>)((OkObjectResult)(await bins.ListItems(project.Id, byNameId, CancellationToken.None)).Result!).Value!;
        all.Select(r => r.FileName).Should().BeEquivalentTo("interview-cam-a.mp4", "drone.mp4", "bed.mp3");

        // Pins are refused; an ordinary bin still takes them.
        (await bins.AddItem(project.Id, summary.Id, new AddMediaBinItemRequest($"file:{music.Id:D}"), CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();

        // Clearing the filter through PATCH turns it into an ordinary (now empty) bin; an invalid filter is refused.
        ActionResult<MediaBinSummaryResponse> cleared = await bins.PatchBin(project.Id, summary.Id, new PatchMediaBinRequest { SmartFilter = Json("null") }, CancellationToken.None);
        ((MediaBinSummaryResponse)((OkObjectResult)cleared.Result!).Value!).IsSmart.Should().BeFalse();
        (await bins.PatchBin(project.Id, summary.Id, new PatchMediaBinRequest { SmartFilter = Json("{\"kinds\":[\"film\"]}") }, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await bins.CreateBin(project.Id, new CreateMediaBinRequest("bad", null, new MediaSmartFilter(ColorLabel: "pink")), CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Export_serves_each_format_as_a_download_and_import_restores_a_ReelBolt_file_or_refuses_unknown_media()
    {
        using InferenceApiDbContext db = CreateContext(Guid.NewGuid().ToString());
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId);
        ProjectFile a = SeedFile(db, project.Id, "a.mp4");
        ProjectFile b = SeedFile(db, project.Id, "b.mp4");
        EditTimeline document = Doc(Track("1", TrackKind.Video, Clip("1", "Opening", 0, 60, new AssetRef(AssetRefKind.ProjectFile, a.Id)), Clip("2", "Second", 60, 120, new AssetRef(AssetRefKind.ProjectFile, b.Id), sourceIn: 10)), Track("2", TrackKind.Video, new Caption(Guid.NewGuid(), "c", 0, 30, "Hi")));
        document = document with { ProjectId = project.Id };
        EditTimelineEntity row = new() { Id = document.Id, ProjectId = project.Id, Name = "Teaser cut", DocumentJson = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Revision = 4, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.EditTimelines.Add(row);
        db.SaveChanges();
        EditTimelinesController controller = Timelines(db, userId);

        FileContentResult edl = (await controller.ExportEditTimeline(project.Id, row.Id, "edl", CancellationToken.None)).Should().BeOfType<FileContentResult>().Subject;
        edl.FileDownloadName.Should().Be("Teaser cut.edl");
        Encoding.UTF8.GetString(edl.FileContents).Should().StartWith("TITLE: Teaser cut\r\n");
        FileContentResult srt = (await controller.ExportEditTimeline(project.Id, row.Id, "srt", CancellationToken.None)).Should().BeOfType<FileContentResult>().Subject;
        Encoding.UTF8.GetString(srt.FileContents).Should().Contain("00:00:00,000 --> 00:00:01,000\nHi");
        FileContentResult otio = (await controller.ExportEditTimeline(project.Id, row.Id, null, CancellationToken.None)).Should().BeOfType<FileContentResult>().Subject;
        otio.ContentType.Should().StartWith("application/json");
        (await controller.ExportEditTimeline(project.Id, row.Id, "aaf", CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>("FCPXML joined the formats in P9; AAF is still not one");
        (await controller.ExportEditTimeline(project.Id, Guid.NewGuid(), "otio", CancellationToken.None)).Should().BeOfType<NotFoundResult>();

        // Import what we exported: a new timeline at revision 1 with the same cuts.
        JsonElement exported = Json(Encoding.UTF8.GetString(otio.FileContents));
        ActionResult<EditTimelineResponse> imported = await controller.ImportEditTimeline(project.Id, new ImportEditTimelineRequest("Copy from OTIO", exported), CancellationToken.None);
        EditTimelineResponse created = imported.Result.Should().BeOfType<ObjectResult>(JsonSerializer.Serialize((imported.Result as ObjectResult)?.Value)).Subject.Value.Should().BeOfType<EditTimelineResponse>().Subject;
        created.Revision.Should().Be(1);
        created.Name.Should().Be("Copy from OTIO");
        created.Document.Tracks[0].Items.Should().HaveCount(2);
        created.Document.Tracks[1].Items.Should().ContainSingle().Which.Should().BeOfType<Caption>();
        created.Id.Should().NotBe(row.Id);
        db.EditTimelineSnapshots.Count(s => s.TimelineId == created.Id).Should().Be(1);

        // The same file imported into a project that lacks the media is refused with the URLs named.
        Project other = SeedProject(db, userId);
        ActionResult<EditTimelineResponse> refused = await controller.ImportEditTimeline(other.Id, new ImportEditTimelineRequest(null, exported), CancellationToken.None, strict: true);
        EditTimelineImportRefusedResponse reason = refused.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value.Should().BeOfType<EditTimelineImportRefusedResponse>().Subject;
        reason.UnresolvedUrls.Should().HaveCount(2);
        (await controller.ImportEditTimeline(project.Id, new ImportEditTimelineRequest("x", Json("[1,2]")), CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task T020_ImportWithMissingMedia_MakesOfflineMarkers_AndStrictStill400s_AndScopeIsServed()
    {
        using InferenceApiDbContext db = CreateContext(Guid.NewGuid().ToString());
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId);
        ProjectFile a = SeedFile(db, project.Id, "a.mp4");
        ProjectFile b = SeedFile(db, project.Id, "b.mp4");
        EditTimeline document = Doc(Track("1", TrackKind.Video, Clip("1", "Opening", 0, 60, new AssetRef(AssetRefKind.ProjectFile, a.Id)), Clip("2", "Second", 60, 120, new AssetRef(AssetRefKind.ProjectFile, b.Id))));
        document = document with { ProjectId = project.Id };
        EditTimelineEntity row = new() { Id = document.Id, ProjectId = project.Id, Name = "T", DocumentJson = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Revision = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.EditTimelines.Add(row);
        db.SaveChanges();
        EditTimelinesController controller = Timelines(db, userId);
        FileContentResult otio = (await controller.ExportEditTimeline(project.Id, row.Id, "otio", CancellationToken.None)).Should().BeOfType<FileContentResult>().Subject;
        JsonElement exported = Json(Encoding.UTF8.GetString(otio.FileContents));

        // Same-project round trip: nothing offline, both clips back.
        EditTimelineResponse same = (await controller.ImportEditTimeline(project.Id, new ImportEditTimelineRequest("Copy", exported), CancellationToken.None)).Result.Should().BeOfType<ObjectResult>().Subject.Value.Should().BeOfType<EditTimelineResponse>().Subject;
        same.Offline.Should().BeEmpty();
        same.Document.Tracks[0].Items.OfType<MediaClip>().Select(c => c.Name).Should().Equal("Opening", "Second");

        // A project that lacks the media: lenient import keeps the timeline, no clips, one marker per missing clip.
        Project other = SeedProject(db, userId);
        EditTimelineResponse lenient = (await controller.ImportEditTimeline(other.Id, new ImportEditTimelineRequest(null, exported), CancellationToken.None)).Result.Should().BeOfType<ObjectResult>().Subject.Value.Should().BeOfType<EditTimelineResponse>().Subject;
        lenient.Offline.Should().HaveCount(2);
        lenient.Offline!.Select(o => o.Name).Should().Equal("Opening", "Second");
        lenient.Document.Markers.Select(m => m.Name).Should().Equal("Offline: Opening", "Offline: Second");
        lenient.Document.Markers.Select(m => m.Id).Should().BeEquivalentTo(lenient.Offline.Select(o => o.ItemId));
        lenient.Document.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().Should().BeEmpty();

        // Strict keeps the old 400 with the URLs.
        EditTimelineImportRefusedResponse strict = (await controller.ImportEditTimeline(other.Id, new ImportEditTimelineRequest(null, exported), CancellationToken.None, strict: true)).Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value.Should().BeOfType<EditTimelineImportRefusedResponse>().Subject;
        strict.UnresolvedUrls.Should().HaveCount(2);

        // A third-party OTIO with one resolvable and one missing clip: the resolvable one imports, the other is a marker.
        JsonElement third = Json("{\"OTIO_SCHEMA\":\"Timeline.1\",\"name\":\"t\",\"tracks\":{\"OTIO_SCHEMA\":\"Stack.1\",\"children\":[{\"OTIO_SCHEMA\":\"Track.1\",\"kind\":\"Video\",\"children\":["
            + "{\"OTIO_SCHEMA\":\"Clip.2\",\"name\":\"Have\",\"source_range\":{\"start_time\":{\"value\":0,\"rate\":30},\"duration\":{\"value\":30,\"rate\":30}},\"media_reference\":{\"OTIO_SCHEMA\":\"ExternalReference.1\",\"target_url\":\"a.mp4\"}},"
            + "{\"OTIO_SCHEMA\":\"Clip.2\",\"name\":\"Lost\",\"source_range\":{\"start_time\":{\"value\":0,\"rate\":30},\"duration\":{\"value\":45,\"rate\":30}},\"media_reference\":{\"OTIO_SCHEMA\":\"ExternalReference.1\",\"target_url\":\"gone.mp4\"}}]}]}}");
        EditTimelineResponse mixed = (await controller.ImportEditTimeline(project.Id, new ImportEditTimelineRequest(null, third), CancellationToken.None)).Result.Should().BeOfType<ObjectResult>().Subject.Value.Should().BeOfType<EditTimelineResponse>().Subject;
        mixed.Document.Tracks[0].Items.OfType<MediaClip>().Should().ContainSingle().Which.Name.Should().Be("Have");
        mixed.Offline.Should().ContainSingle().Which.Name.Should().Be("Lost");
        mixed.Document.Markers.Should().ContainSingle().Which.Frame.Should().Be(30);

        // Scope: served from the server; unknown format 400; foreign/missing timeline 404.
        InterchangeScopeResponse scope = (await controller.GetExportScope(project.Id, row.Id, "edl", CancellationToken.None)).Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<InterchangeScopeResponse>().Subject;
        scope.Carries.Should().NotBeEmpty();
        scope.Dropped.Should().Contain("Audio tracks");
        scope.MediaMode.Should().Be("reelbolt-url");
        (await controller.GetExportScope(project.Id, row.Id, "aaf", CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await controller.GetExportScope(project.Id, Guid.NewGuid(), "otio", CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await controller.GetExportScope(other.Id, row.Id, "otio", CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task T011_ExportScope_Lists_The_Camera_Profile_As_Dropped_Everywhere_But_Otio()
    {
        using InferenceApiDbContext db = CreateContext(Guid.NewGuid().ToString());
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId);
        ProjectFile file = SeedFile(db, project.Id, "cam.mp4");
        MediaClip clip = new(Guid.NewGuid(), "log", 0, 30, new AssetRef(AssetRefKind.ProjectFile, file.Id), InputProfile: "sonySLog3");
        EditTimeline document = Doc(Track("1", TrackKind.Video, clip)) with { ProjectId = project.Id };
        EditTimelineEntity row = new()
        {
            Id = document.Id,
            ProjectId = project.Id,
            Name = "T",
            DocumentJson = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Revision = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.EditTimelines.Add(row);
        db.SaveChanges();
        EditTimelinesController controller = Timelines(db, userId);

        const string reason = "Camera profile conversion is not carried; apply it before importing";
        foreach (string format in new[] { "edl", "srt", "vtt", "fcpxml" })
        {
            InterchangeScopeResponse scope = (await controller.GetExportScope(project.Id, row.Id, format, CancellationToken.None))
                .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<InterchangeScopeResponse>().Subject;
            scope.Dropped.Should().Contain(reason, $"{format} cannot carry a camera log conversion");
        }

        InterchangeScopeResponse otio = (await controller.GetExportScope(project.Id, row.Id, "otio", CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<InterchangeScopeResponse>().Subject;
        otio.Dropped.Should().NotContain(reason, "OTIO carries the lossless ReelBolt document");
    }

    [Fact]
    public void T020_FcpxmlFlags_DropAudioAndRoles_AndStayWellFormed()
    {
        Guid file = Guid.NewGuid();
        AssetRef asset = new(AssetRefKind.ProjectFile, file);
        EditTimeline document = Doc(
            Track("1", TrackKind.Video, Clip("1", "V", 0, 60, asset)),
            Track("2", TrackKind.Audio, Clip("2", "A", 0, 60, asset)));

        string full = FcpxmlWriter.Write(document);
        string noAudio = FcpxmlWriter.Write(document, new FcpxmlOptions(IncludeAudio: false));
        string noRoles = FcpxmlWriter.Write(document, new FcpxmlOptions(Roles: false));

        full.Should().Contain("audioRole=\"dialogue\"").And.Contain("lane=\"-1\"");
        noAudio.Should().NotContain("lane=\"-1\"").And.Contain("hasAudio=\"0\"");
        noRoles.Should().Contain("lane=\"-1\"").And.NotContain("audioRole");
        foreach (string xml in new[] { full, noAudio, noRoles })
        {
            // Apple's FCPXML DTD is not redistributable, so: well-formed, and the structural skeleton the DTD requires.
            System.Xml.Linq.XDocument parsed = System.Xml.Linq.XDocument.Parse(xml.Replace("<!DOCTYPE fcpxml>", string.Empty));
            parsed.Root!.Name.LocalName.Should().Be("fcpxml");
            parsed.Root.Attribute("version")!.Value.Should().Be("1.10");
            parsed.Root.Elements().Select(e => e.Name.LocalName).Should().Equal("resources", "library");
            parsed.Descendants("spine").Should().ContainSingle();
        }
    }
}
