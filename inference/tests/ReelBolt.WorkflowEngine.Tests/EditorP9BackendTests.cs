#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Editor v2, P9 (polish) — the server half: FCPXML export, project presets (validated against the
/// timeline's own catalogues), per-user preferences (keymap + layouts, shape- and size-bounded) and
/// timecoded review comments (author vs editor rights, one-level threads, soft delete).
/// </summary>
public class EditorP9BackendTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly AssetRef AssetA = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null);
    private static readonly AssetRef AssetB = new(AssetRefKind.ProjectFile, Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), null);

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");

    private static EditTimeline FcpDoc(Rational rate) => new(
        Id(1), Id(2), "Promo <cut> & co",
        new EditTimelineSettings(1920, 1080, rate, 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
        [
            new Track(Id(10), TrackKind.Video, "V1", null, 64, false, true, false, false, null, [
                new MediaClip(Id(100), "Interview", 0, 60, AssetA, 30),
                new Transition(Id(101), "x", 60, 70, "dissolve"),
                new MediaClip(Id(102), "B-roll", 70, 120, AssetB, 0),
                new MediaClip(Id(103), "Interview 2", 150, 200, AssetA, 300),
            ]),
            new Track(Id(11), TrackKind.Video, "V2", null, 64, false, true, false, false, null, [new MediaClip(Id(110), "Logo", 10, 40, AssetB, 5)]),
            new Track(Id(12), TrackKind.Audio, "A1", null, 64, false, true, false, false, null, [new MediaClip(Id(120), "Music", 0, 200, AssetB, 0)]),
        ],
        [new Marker(Id(200), 80, "Check this", null, "too fast", null)],
        null, null);

    [Fact]
    public void FCPXML_writes_a_contiguous_spine_connected_lanes_markers_and_exact_rational_times()
    {
        string xml = FcpxmlWriter.Write(FcpDoc(new Rational(30000, 1001)));
        xml.Should().StartWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE fcpxml>");
        XDocument doc = XDocument.Parse(xml);
        XElement root = doc.Root!;
        root.Attribute("version")!.Value.Should().Be("1.10");
        root.Descendants("format").Single().Attribute("frameDuration")!.Value.Should().Be("1001/30000s");

        XElement[] assets = root.Descendants("asset").ToArray();
        assets.Should().HaveCount(2);
        assets.Select(a => a.Element("media-rep")!.Attribute("src")!.Value).Should().Equal(
            "reelbolt://project-file/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "reelbolt://project-file/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        // The furthest source frame read from A is 300 + 50 = 350 frames.
        assets[0].Attribute("duration")!.Value.Should().Be(FcpxmlWriter.Time(350, new Rational(30000, 1001)));

        XElement spine = root.Descendants("spine").Single();
        XElement[] elements = spine.Elements().ToArray();
        elements.Select(e => e.Name.LocalName).Should().Equal("asset-clip", "transition", "asset-clip", "gap", "asset-clip");
        // Contiguity: each element starts where the previous one ended.
        Rational rate = new(30000, 1001);
        long cursor = 0;
        foreach ((XElement e, (long start, long length)) in elements.Zip(new[] { (0L, 60L), (60L, 10L), (70L, 50L), (120L, 30L), (150L, 50L) }))
        {
            e.Attribute("offset")!.Value.Should().Be(FcpxmlWriter.Time(start, rate));
            e.Attribute("duration")!.Value.Should().Be(FcpxmlWriter.Time(length, rate));
            start.Should().Be(cursor);
            cursor = start + length;
        }

        elements[0].Attribute("start")!.Value.Should().Be(FcpxmlWriter.Time(30, rate), "an asset-clip's start is its source in");
        elements[1].Attribute("name")!.Value.Should().Be("Cross Dissolve");

        // Connected clips sit on the spine element they start over, offset in THAT element's time base.
        XElement logo = elements[0].Elements("asset-clip").Single(c => c.Attribute("lane")!.Value == "1");
        logo.Attribute("offset")!.Value.Should().Be(FcpxmlWriter.Time(30 + 10, rate));
        XElement music = elements[0].Elements("asset-clip").Single(c => c.Attribute("lane")!.Value == "-1");
        music.Attribute("duration")!.Value.Should().Be(FcpxmlWriter.Time(200, rate));
        // The marker at frame 80 falls in the B-roll clip (source start 0, local 10).
        XElement marker = elements[2].Element("marker")!;
        marker.Attribute("start")!.Value.Should().Be(FcpxmlWriter.Time(10, rate));
        marker.Attribute("value")!.Value.Should().Be("Check this");
        root.Descendants("project").Single().Attribute("name")!.Value.Should().Be("Promo <cut> & co", "names are XML-escaped, not dropped");
    }

    [Fact]
    public void FCPXML_times_are_reduced_rationals_and_integer_rates_give_whole_seconds()
    {
        FcpxmlWriter.Time(0, new Rational(30, 1)).Should().Be("0s");
        FcpxmlWriter.Time(60, new Rational(30, 1)).Should().Be("2s");
        FcpxmlWriter.Time(45, new Rational(30, 1)).Should().Be("3/2s");
        FcpxmlWriter.Time(1, new Rational(24000, 1001)).Should().Be("1001/24000s");
        FcpxmlWriter.Time(24, new Rational(24000, 1001)).Should().Be("1001/1000s");
        string xml = FcpxmlWriter.Write(FcpDoc(new Rational(25, 1)));
        XDocument.Parse(xml).Root!.Descendants("sequence").Single().Attribute("duration")!.Value.Should().Be("8s");
    }

    [Fact]
    public void Presets_are_checked_against_the_timeline_catalogues_and_ranges()
    {
        static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
        EditorPresetValidator.Validate("transition", "Soft", J("""{"transitionType":"dissolve","easing":"easeInOutQuad","framesBefore":6,"framesAfter":6}""")).Should().BeEmpty();
        EditorPresetValidator.Validate("transition", "Bad", J("""{"transitionType":"sparkle","framesBefore":-1,"framesAfter":6,"x":1}"""))
            .Should().HaveCount(3);
        EditorPresetValidator.Validate("effect", "Look", J("""{"effects":[{"type":"vignette","amount":0.4},{"type":"grain","amount":0.2}]}""")).Should().BeEmpty();
        EditorPresetValidator.Validate("effect", "Bad", J("""{"effects":[{"type":"warp","amount":2}]}""")).Should().HaveCount(2);
        EditorPresetValidator.Validate("mastering", "Podcast", J("""{"targetLufs":-16,"truePeakDbtp":-1.5,"loudnessRange":11}""")).Should().BeEmpty();
        EditorPresetValidator.Validate("mastering", "Loud", J("""{"targetLufs":-2,"truePeakDbtp":-1}""")).Should().ContainSingle();
        EditorPresetValidator.Validate("filter", "", J("[]")).Should().HaveCount(3);
    }

    [Fact]
    public void Preferences_are_bounded_in_shape_and_size()
    {
        static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
        EditorPreferencesValidator.Validate(J("""{"blade":"B","transport.play":"Space","undo":""}"""), J("""[{"name":"Colour","workspace":"color","sizes":[20,60,20]}]""")).Should().BeEmpty();
        EditorPreferencesValidator.Validate(J("""{"bad id!":"B","ok":"\u0007"}"""), J("""[{"workspace":"color"}]""")).Should().HaveCount(3);
        string huge = "{" + string.Join(',', Enumerable.Range(0, 201).Select(i => $"\"a{i}\":\"K\"")) + "}";
        EditorPreferencesValidator.Validate(J(huge), J("[]")).Should().ContainSingle().Which.Should().Contain("at most 200");
    }

    // ── Controllers ───────────────────────────────────────────────────────────

    private static InferenceApiDbContext CreateContext(string name) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(name).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static ICurrentUser Caller(Guid userId, Guid orgId, string email = "a@example.com")
    {
        Mock<ICurrentUser> user = new();
        user.SetupGet(u => u.UserId).Returns(userId);
        user.SetupGet(u => u.Email).Returns(email);
        user.SetupGet(u => u.IsAuthenticated).Returns(true);
        user.SetupGet(u => u.OrganizationId).Returns(orgId);
        user.SetupGet(u => u.OrgRole).Returns(ReelBolt.Shared.Tenancy.OrganizationRole.Member);
        return user.Object;
    }

    private static EditTimelinesController Controller(InferenceApiDbContext db, ICurrentUser caller) =>
        new(db, caller, TestTenancy.Access(db, caller), new Mock<IFileStorageService>().Object, new Mock<IWorkflowExecutionService>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static (InferenceApiDbContext Db, Project Project, EditTimelineEntity Timeline, Guid Owner) Seed()
    {
        Guid owner = Guid.NewGuid();
        InferenceApiDbContext db = CreateContext(Guid.NewGuid().ToString());
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        EditTimelineEntity timeline = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "T", DocumentJson = JsonSerializer.Serialize(FcpDoc(new Rational(30, 1)) with { ProjectId = project.Id }, Json), Revision = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.EditTimelines.Add(timeline);
        db.SaveChanges();
        return (db, project, timeline, owner);
    }

    [Fact]
    public async Task Review_comments_thread_one_level_and_only_the_author_rewrites_text()
    {
        (InferenceApiDbContext db, Project project, EditTimelineEntity timeline, Guid owner) = Seed();
        using (db)
        {
            ICurrentUser author = Caller(owner, project.OrganizationId, "author@example.com");
            EditTimelinesController asAuthor = Controller(db, author);
            ObjectResult created = (await asAuthor.CreateComment(project.Id, timeline.Id, new CreateEditTimelineCommentRequest("Cut this pause", 90, 15, null), CancellationToken.None)).Result.Should().BeOfType<ObjectResult>().Subject;
            created.StatusCode.Should().Be(201);
            EditTimelineCommentResponse root = created.Value.Should().BeOfType<EditTimelineCommentResponse>().Subject;
            root.AuthorName.Should().Be("author@example.com");

            // A colleague in the same organization replies (the reply takes the root's frame) and resolves it.
            Guid colleagueId = Guid.NewGuid();
            ICurrentUser colleague = Caller(colleagueId, project.OrganizationId, "colleague@example.com");
            EditTimelinesController asColleague = Controller(db, colleague);
            EditTimelineCommentResponse reply = (EditTimelineCommentResponse)((ObjectResult)(await asColleague.CreateComment(project.Id, timeline.Id, new CreateEditTimelineCommentRequest("Agreed", 5, null, root.Id), CancellationToken.None)).Result!).Value!;
            reply.Frame.Should().Be(90);
            reply.ParentId.Should().Be(root.Id);
            (await asColleague.CreateComment(project.Id, timeline.Id, new CreateEditTimelineCommentRequest("nested", 0, null, reply.Id), CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();

            (await asColleague.UpdateComment(project.Id, timeline.Id, root.Id, new UpdateEditTimelineCommentRequest("rewritten", null), CancellationToken.None)).Result
                .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
            EditTimelineCommentResponse resolved = ((OkObjectResult)(await asColleague.UpdateComment(project.Id, timeline.Id, root.Id, new UpdateEditTimelineCommentRequest(null, true), CancellationToken.None)).Result!).Value.Should().BeOfType<EditTimelineCommentResponse>().Subject;
            resolved.Resolved.Should().BeTrue();
            resolved.Body.Should().Be("Cut this pause");

            // Validation reports every problem; a foreign project is 404.
            BadRequestObjectResult bad = (await asAuthor.CreateComment(project.Id, timeline.Id, new CreateEditTimelineCommentRequest("  ", -1, 0, null), CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>().Subject;
            JsonSerializer.Serialize(bad.Value).Should().Contain("body must not be empty").And.Contain("frame must be").And.Contain("durationFrames");
            ICurrentUser stranger = Caller(Guid.NewGuid(), Guid.NewGuid());
            (await Controller(db, stranger).ListComments(project.Id, timeline.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
            (await Controller(db, stranger).DeleteComment(project.Id, timeline.Id, root.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();

            // Soft delete keeps the reply; the list omits the removed root.
            (await asAuthor.DeleteComment(project.Id, timeline.Id, root.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
            IReadOnlyList<EditTimelineCommentResponse> list = (IReadOnlyList<EditTimelineCommentResponse>)((OkObjectResult)(await asAuthor.ListComments(project.Id, timeline.Id, CancellationToken.None)).Result!).Value!;
            list.Should().ContainSingle().Which.Id.Should().Be(reply.Id);
            db.EditTimelineComments.IgnoreQueryFilters().Count().Should().Be(2);
        }
    }

    [Fact]
    public async Task Presets_round_trip_per_project_and_refuse_a_kind_change_or_a_hostile_payload()
    {
        (InferenceApiDbContext db, Project project, _, Guid owner) = Seed();
        using (db)
        {
            EditTimelinesController controller = Controller(db, Caller(owner, project.OrganizationId));
            JsonElement payload = JsonDocument.Parse("""{"targetLufs":-14,"truePeakDbtp":-1}""").RootElement;
            EditorPresetResponse saved = (EditorPresetResponse)((ObjectResult)(await controller.CreatePreset(project.Id, new SaveEditorPresetRequest("mastering", " Social ", payload), CancellationToken.None)).Result!).Value!;
            saved.Name.Should().Be("Social");
            saved.Payload.GetProperty("targetLufs").GetDouble().Should().Be(-14);

            (await controller.CreatePreset(project.Id, new SaveEditorPresetRequest("mastering", "Too loud", JsonDocument.Parse("""{"targetLufs":0,"truePeakDbtp":-1}""").RootElement), CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
            (await controller.UpdatePreset(project.Id, saved.Id, new SaveEditorPresetRequest("effect", "x", payload), CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
            EditorPresetResponse renamed = ((OkObjectResult)(await controller.UpdatePreset(project.Id, saved.Id, new SaveEditorPresetRequest(null, "Social media", payload), CancellationToken.None)).Result!).Value.Should().BeOfType<EditorPresetResponse>().Subject;
            renamed.Name.Should().Be("Social media");

            IReadOnlyList<EditorPresetResponse> list = (IReadOnlyList<EditorPresetResponse>)((OkObjectResult)(await controller.ListPresets(project.Id, "mastering", CancellationToken.None)).Result!).Value!;
            list.Should().ContainSingle();
            ((IReadOnlyList<EditorPresetResponse>)((OkObjectResult)(await controller.ListPresets(project.Id, "transition", CancellationToken.None)).Result!).Value!).Should().BeEmpty();
            (await Controller(db, Caller(Guid.NewGuid(), Guid.NewGuid())).ListPresets(project.Id, null, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
            (await controller.DeletePreset(project.Id, saved.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
            db.EditorPresets.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Preferences_belong_to_the_caller_only_and_a_bad_blob_writes_nothing()
    {
        using InferenceApiDbContext db = CreateContext(Guid.NewGuid().ToString());
        Guid me = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        EditorPreferencesController mine = new(db, Caller(me, Guid.NewGuid()));
        EditorPreferencesResponse empty = ((OkObjectResult)(await mine.Get(CancellationToken.None)).Result!).Value.Should().BeOfType<EditorPreferencesResponse>().Subject;
        empty.Keymap.ValueKind.Should().Be(JsonValueKind.Object);
        empty.UpdatedAt.Should().BeNull();

        JsonElement keymap = JsonDocument.Parse("""{"blade":"C","transport.play":"Space"}""").RootElement;
        JsonElement layouts = JsonDocument.Parse("""[{"name":"Grading","workspace":"color"}]""").RootElement;
        (await mine.Put(new SaveEditorPreferencesRequest(keymap, layouts), CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
        (await mine.Put(new SaveEditorPreferencesRequest(JsonDocument.Parse("[]").RootElement, layouts), CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();

        EditorPreferencesResponse saved = ((OkObjectResult)(await mine.Get(CancellationToken.None)).Result!).Value.Should().BeOfType<EditorPreferencesResponse>().Subject;
        saved.Keymap.GetProperty("blade").GetString().Should().Be("C");
        EditorPreferencesResponse theirs = ((OkObjectResult)(await new EditorPreferencesController(db, Caller(other, Guid.NewGuid())).Get(CancellationToken.None)).Result!).Value.Should().BeOfType<EditorPreferencesResponse>().Subject;
        theirs.UpdatedAt.Should().BeNull("another user's GET never sees my row");
    }

    [Fact]
    public async Task The_export_endpoint_serves_fcpxml()
    {
        (InferenceApiDbContext db, Project project, EditTimelineEntity timeline, Guid owner) = Seed();
        using (db)
        {
            FileContentResult file = (await Controller(db, Caller(owner, project.OrganizationId)).ExportEditTimeline(project.Id, timeline.Id, "fcpxml", CancellationToken.None)).Should().BeOfType<FileContentResult>().Subject;
            file.ContentType.Should().StartWith("application/xml");
            file.FileDownloadName.Should().EndWith(".fcpxml");
            XDocument.Parse(System.Text.Encoding.UTF8.GetString(file.FileContents)).Root!.Name.LocalName.Should().Be("fcpxml");
        }
    }

    // ── HSL qualifiers and tracked power windows ──────────────────────────────

    private static GradeStack Secondary(GradeQualifier? q, GradeWindow? w) =>
        new(true, [new GradeNode(Id(900), "Skin", true, new GradePrimaries(Saturation: 1.3), Qualifier: q, Window: w)]);

    [Fact]
    public void A_whole_frame_or_select_all_node_keeps_the_P7_bytes()
    {
        List<GradeFilterBuilder.LutRequest> luts = [];
        int index = 0;
        string p7 = GradeFilterBuilder.Build(new GradeStack(true, [new GradeNode(Id(900), "Skin", true, new GradePrimaries(Saturation: 1.3))]), i => $"/l{i}", luts, ref index);
        GradeFilterBuilder.Build(Secondary(new GradeQualifier(), null), i => $"/l{i}", luts, ref index).Should().Be(p7, "a qualifier that selects everything is the identity");
        p7.Should().Be("hue=s=1.3");
        GradeFilterBuilder.Build(Secondary(new GradeQualifier(HueCenter: 25, HueWidth: 20), null), i => $"/l{i}", luts, ref index, "L").Should().NotBe(p7);
    }

    [Fact]
    public void A_secondary_splits_grades_a_copy_and_merges_through_the_matte_of_the_ungraded_pixels()
    {
        List<GradeFilterBuilder.LutRequest> luts = [];
        int index = 0;
        GradeWindow window = new(new ShapeMask("ellipse", 0.5, 0.4, 0.3, 0.4, 0.05), new Keyframed<double>(0, [new Keyframe<double>(30, 0, InterpKind.Linear), new Keyframe<double>(60, 0.2, InterpKind.Linear)]));
        string chain = GradeFilterBuilder.Build(Secondary(new GradeQualifier(HueCenter: 25, HueWidth: 20, LumMin: 0.2, Softness: 0.05), window), i => $"/l{i}", luts, ref index, "v0c", 30, 30);
        chain.Should().StartWith("format=gbrap,split=3[v0c0o][v0c0c][v0c0m];[v0c0c]hue=s=1.3,format=gbrap[v0c0g];[v0c0m]geq=r='255*");
        chain.Should().EndWith("[v0c0o][v0c0g][v0c0k]maskedmerge");
        chain.Should().Contain("atan2(1.7320508*").And.Contain("abs(mod(").And.Contain("-25+540,360)-180)");
        // The tracked window's centre follows the offset keys, over geq's T, from the region start.
        chain.Should().Contain("if(lt(T,0)").And.Contain("clip((T-0)/(1-0),0,1)");
        chain.Should().NotContain("st(").And.NotContain("ld(");
    }

    [Fact]
    public void A_qualifier_bound_at_the_end_of_the_scale_adds_no_soft_edge()
    {
        string Matte(GradeQualifier q) => GradeFilterBuilder.Matte(new GradeNode(Id(901), "n", true, new GradePrimaries(Saturation: 0), Qualifier: q), 0, 30)!;
        int Edges(string matte) => System.Text.RegularExpressions.Regex.Matches(matte, @"clip\(").Count;
        // Default satMax 1, lumMin 0, lumMax 1: a pure colour sits ON the bound and must stay fully selected, so the
        // hue term and the satMin term are the only soft edges.
        Edges(Matte(new GradeQualifier(HueCenter: 120, HueWidth: 15, SatMin: 0.15))).Should().Be(2);
        // A real upper limit still gets its edge, and so does every other real limit.
        Edges(Matte(new GradeQualifier(HueCenter: 120, HueWidth: 15, SatMin: 0.15, SatMax: 0.9))).Should().Be(3);
        Edges(Matte(new GradeQualifier(HueCenter: 120, HueWidth: 15, SatMin: 0.15, SatMax: 0.9, LumMin: 0.1, LumMax: 0.95))).Should().Be(5);
    }

    [Fact]
    public void Qualifier_and_window_values_are_range_checked_by_the_validator()
    {
        MediaClip clip = new(Id(300), "c", 0, 30, AssetA, Grade: Secondary(
            new GradeQualifier(HueCenter: 400, HueWidth: 0, SatMin: 0.8, SatMax: 0.2, Softness: 0.9),
            new GradeWindow(new ShapeMask("star", 2, 0.5, 0.5, 0.5), new Keyframed<double>(3))));
        EditTimeline doc = FcpDoc(new Rational(30, 1)) with { Tracks = [new Track(Id(10), TrackKind.Video, "V1", null, 64, false, true, false, false, null, [clip])], Markers = [] };
        ReelBolt.Shared.Editing.ValidationResult result = EditTimelineValidator.Validate(doc, new EditTimelineValidationContext(Id(2), new Dictionary<AssetRef, long>(), new Dictionary<Guid, Guid> { [AssetA.ProjectFileId!.Value] = Id(2) }, new Dictionary<Guid, Guid>()));
        string[] paths = result.Violations.Select(v => v.Path).ToArray();
        paths.Should().Contain(p => p.EndsWith(".qualifier.hueCenter"));
        paths.Should().Contain(p => p.EndsWith(".qualifier.hueWidth"));
        paths.Should().Contain(p => p.EndsWith(".qualifier.satMin"));
        paths.Should().Contain(p => p.EndsWith(".qualifier.softness"));
        paths.Should().Contain(p => p.EndsWith(".window.shape.kind"));
        paths.Should().Contain(p => p.EndsWith(".window.shape.x"));
        paths.Should().Contain(p => p.Contains(".window.offsetX"));
    }

    [FfmpegFact]
    public void A_secondary_with_a_tracked_window_runs_through_the_real_ffmpeg()
    {
        List<GradeFilterBuilder.LutRequest> luts = [];
        int index = 0;
        GradeWindow window = new(new ShapeMask("rect", 0.5, 0.5, 0.4, 0.4, 0.1), new Keyframed<double>(0, [new Keyframe<double>(0, -0.1, InterpKind.Linear), new Keyframe<double>(10, 0.1, InterpKind.EaseInOutQuad)]));
        string chain = GradeFilterBuilder.Build(
            new GradeStack(true, [new GradeNode(Id(900), "Skin", true, new GradePrimaries(Saturation: 1.5, Exposure: 0.5), Qualifier: new GradeQualifier(HueCenter: 25, HueWidth: 30, LumMin: 0.1, Invert: true), Window: window)]),
            i => $"/l{i}", luts, ref index, "t0c", 0, 10);
        System.Diagnostics.ProcessStartInfo info = new("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string arg in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=d=1:s=96x54:r=10", "-filter_complex", $"[0:v]{chain},format=yuv420p[out]", "-map", "[out]", "-f", "null", "-" })
            info.ArgumentList.Add(arg);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info)!;
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        process.ExitCode.Should().Be(0, stderr);
    }
}
