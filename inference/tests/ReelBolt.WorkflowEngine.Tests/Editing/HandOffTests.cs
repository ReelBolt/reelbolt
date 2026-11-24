#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Interchange;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Editing;

/// <summary>R12.2 hand-off package and R12.3 safe import matching.</summary>
public class HandOffTests
{
    // ── HandOffNaming ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("clip.mp4", "clip.mp4")]
    [InlineData("a/b\\c.mp4", "c.mp4")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..", "media")]
    [InlineData("", "media")]
    [InlineData(null, "media")]
    [InlineData("CON.mp4", "_CON.mp4")]
    [InlineData("nul", "_nul")]
    [InlineData("lpt1.txt", "_lpt1.txt")]
    [InlineData("clip. ", "clip")]
    [InlineData("a<b>:c.mp4", "a_b__c.mp4")]
    [InlineData("tab\there.mp4", "tabhere.mp4")]
    [InlineData("a..b.mp4", "a.b.mp4")]
    [InlineData(".mp4", "media.mp4")]
    [InlineData("evil\u202egnp.exe", "evilgnp.exe")]
    [InlineData("e\u0301.mp4", "\u00e9.mp4")]
    public void Sanitize_makes_a_safe_name(string? input, string expected)
    {
        HandOffNaming.Sanitize(input).Should().Be(expected);
    }

    [Fact]
    public void Sanitize_caps_the_length_and_keeps_the_extension()
    {
        string name = HandOffNaming.Sanitize(new string('a', 300) + ".mp4");
        name.Length.Should().Be(HandOffNaming.MaxNameLength);
        name.Should().EndWith(".mp4");
    }

    [Fact]
    public void Assign_de_duplicates_without_regard_to_case_and_keeps_the_extension()
    {
        AssetRef a = new(AssetRefKind.ProjectFile, Guid.NewGuid());
        AssetRef b = new(AssetRefKind.ProjectFile, Guid.NewGuid());
        AssetRef c = new(AssetRefKind.ProjectFile, Guid.NewGuid());
        AssetRef d = new(AssetRefKind.StepResult, StepResultId: Guid.NewGuid());
        IReadOnlyDictionary<AssetRef, string> names = HandOffNaming.Assign(
            [(a, "Clip.mp4"), (b, "clip.MP4"), (c, "Clip.mp4"), (a, "ignored.mp4"), (d, null)]);
        names[a].Should().Be("Clip.mp4");
        names[b].Should().Be("clip (2).MP4");
        names[c].Should().Be("Clip (3).mp4");
        names[d].Should().Be("media");
        names.Should().HaveCount(4);

        string longName = new string('x', 200) + ".mov";
        IReadOnlyDictionary<AssetRef, string> longNames = HandOffNaming.Assign([(a, longName), (b, longName)]);
        longNames[b].Length.Should().BeLessThanOrEqualTo(HandOffNaming.MaxNameLength);
        longNames[b].Should().EndWith(" (2).mov");
        longNames[a].Should().NotBe(longNames[b]);
    }

    // ── Writer option ────────────────────────────────────────────────────────

    private static readonly Guid FileA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid FileB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly AssetRef AssetA = new(AssetRefKind.ProjectFile, FileA);
    private static readonly AssetRef AssetB = new(AssetRefKind.ProjectFile, FileB);
    private static readonly EditTimelineSettings Settings = new(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static MediaClip Clip(string id, string name, long recordIn, long recordOut, AssetRef asset, long sourceIn = 0) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-{id}{id}{id}{id}-4{id}{id}{id}-8{id}{id}{id}-{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}"), name, recordIn, recordOut, asset, sourceIn);

    private static Track VideoTrack(string id, params Item[] items) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-0000-4000-8000-000000000000"), TrackKind.Video, "V" + id, null, 64, false, true, false, false, null, items);

    private static EditTimeline Doc(Guid projectId, params Track[] tracks) =>
        new(Guid.Parse("22222222-2222-4222-8222-222222222222"), projectId, "Teaser cut", Settings, tracks, [], null, null);

    private static EditTimeline TwoClipDoc(Guid projectId, AssetRef first, AssetRef second) => Doc(projectId,
        VideoTrack("1",
            Clip("1", "Opening", 0, 60, first, sourceIn: 15),
            Clip("2", "Interview", 60, 150, second)));

    private static HandOffMediaNames NamesFor(params (AssetRef Ref, string Name, long? Size, double? Duration)[] entries) =>
        new(entries.ToDictionary(e => e.Ref, e => new HandOffMediaEntry(e.Name, e.Size, e.Duration)));

    [Fact]
    public void Default_output_is_byte_identical_without_the_option()
    {
        EditTimeline document = TwoClipDoc(Guid.NewGuid(), AssetA, AssetB);
        TimelineInterchange.WriteOtio(document, null).Should().Be(TimelineInterchange.WriteOtio(document));
        TimelineInterchange.WriteEdl(document, null).Should().Be(TimelineInterchange.WriteEdl(document));
        FcpxmlWriter.Write(document, new FcpxmlOptions()).Should().Be(FcpxmlWriter.Write(document, new FcpxmlOptions(HandOff: null)));
        TimelineInterchange.WriteEdl(document).Should().Contain("* SOURCE FILE: reelbolt://project-file/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        TimelineInterchange.WriteOtio(document).Should().Contain("\"target_url\": \"reelbolt://project-file/");
    }

    [Fact]
    public void Writers_reference_media_by_relative_name_and_keep_reelbolt_urls_in_metadata_only()
    {
        EditTimeline document = TwoClipDoc(Guid.NewGuid(), AssetA, AssetB);
        HandOffMediaNames names = NamesFor((AssetA, "Opening Shot.mp4", 1234, 10.0), (AssetB, "interview.mov", null, null));

        JsonNode otio = JsonNode.Parse(TimelineInterchange.WriteOtio(document, names))!;
        List<JsonNode> clips = otio["tracks"]!["children"]!.AsArray()[0]!["children"]!.AsArray().Where(c => c!["OTIO_SCHEMA"]!.GetValue<string>() == "Clip.1").Select(c => c!).ToList();
        clips[0]["media_reference"]!["target_url"]!.GetValue<string>().Should().Be("media/Opening%20Shot.mp4");
        clips[0]["media_reference"]!["available_range"]!["duration"]!["value"]!.GetValue<double>().Should().Be(300);
        clips[0]["media_reference"]!["metadata"]!["reelbolt"]!["assetUrl"]!.GetValue<string>().Should().Be("reelbolt://project-file/" + FileA);
        clips[0]["media_reference"]!["metadata"]!["reelbolt"]!["sizeBytes"]!.GetValue<long>().Should().Be(1234);
        clips[1]["media_reference"]!["target_url"]!.GetValue<string>().Should().Be("media/interview.mov");
        clips[1]["media_reference"]!.AsObject().ContainsKey("available_range").Should().BeFalse();

        // Outside metadata.reelbolt no reelbolt:// URL remains.
        StripMetadata(otio);
        otio.ToJsonString().Should().NotContain("reelbolt://");

        string edl = TimelineInterchange.WriteEdl(document, names);
        edl.Should().Contain("* SOURCE FILE: media/Opening Shot.mp4").And.Contain("* SOURCE FILE: media/interview.mov").And.NotContain("reelbolt://");

        string fcpxml = FcpxmlWriter.Write(document, new FcpxmlOptions(HandOff: names));
        fcpxml.Should().Contain("src=\"media/Opening%20Shot.mp4\"").And.Contain("src=\"media/interview.mov\"").And.NotContain("reelbolt://");
        System.Xml.Linq.XDocument.Parse(fcpxml.Replace("<!DOCTYPE fcpxml>", string.Empty)).Descendants("media-rep").Should().HaveCount(2);
    }

    private static void StripMetadata(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                o.Remove("metadata");
                foreach (KeyValuePair<string, JsonNode?> pair in o.ToList()) StripMetadata(pair.Value);
                break;
            case JsonArray a:
                foreach (JsonNode? child in a.ToList()) StripMetadata(child);
                break;
        }
    }

    // ── Package builder ──────────────────────────────────────────────────────

    private static HandOffSource Source(string name, string? key, long? size = 100, string safe = "", double? duration = 5, params string[] usedBy) =>
        new(new AssetRef(AssetRefKind.ProjectFile, Guid.NewGuid()), name, safe.Length == 0 ? name : safe, key, "video/mp4", size, null, duration, usedBy);

    private static HandOffPackageInput Input(IReadOnlyList<HandOffSource> sources, bool includeMedia = true) =>
        new(Guid.NewGuid(), "Teaser cut", "otio", "teaser-cut.otio", "{\"timeline\":true}", sources, includeMedia,
            includeMedia ? null : "You chose a package without the media files.", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

    private static Mock<IFileStorageService> FakeStorage(Dictionary<string, byte[]> objects)
    {
        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, CancellationToken>((_, key, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (!objects.TryGetValue(key, out byte[]? bytes)) throw new FileNotFoundException("gone");
                return Task.FromResult<Stream>(new MemoryStream(bytes));
            });
        return storage;
    }

    private static ZipArchive Open(byte[] zip) => new(new MemoryStream(zip), ZipArchiveMode.Read);

    private static async Task<byte[]> Build(HandOffPackageInput input, Mock<IFileStorageService> storage, CancellationToken ct = default)
    {
        // A non-seekable sink, like a response body, so the zip must stream.
        using NonSeekable sink = new();
        await HandOffPackage.WriteAsync(sink, input, storage.Object, NullLogger.Instance, ct);
        return sink.ToArray();
    }

    private sealed class NonSeekable : Stream
    {
        private readonly MemoryStream _inner = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public byte[] ToArray() => _inner.ToArray();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task Zip_has_the_timeline_file_the_media_stored_and_the_manifest_last()
    {
        byte[] big = Enumerable.Repeat((byte)'A', 20_000).ToArray();
        HandOffSource one = Source("cam a.mp4", "projects/p/userFiles/k1", size: big.Length, usedBy: ["Opening"]);
        HandOffSource two = Source("music.mp3", "projects/p/userFiles/k2", size: 5, duration: null);
        Mock<IFileStorageService> storage = FakeStorage(new() { ["projects/p/userFiles/k1"] = big, ["projects/p/userFiles/k2"] = [1, 2, 3, 4, 5] });

        using ZipArchive zip = Open(await Build(Input([one, two]), storage));

        zip.Entries.Select(e => e.FullName).Should().Equal("teaser-cut.otio", "media/cam a.mp4", "media/music.mp3", "README.txt", "manifest.json");
        ZipArchiveEntry media = zip.GetEntry("media/cam a.mp4")!;
        media.Length.Should().Be(big.Length);
        media.CompressedLength.Should().Be(big.Length, "media entries are Stored (no compression)");
        using (Stream s = media.Open()) { using MemoryStream copy = new(); s.CopyTo(copy); copy.ToArray().Should().Equal(big); }

        string manifest = ReadText(zip, "manifest.json");
        JsonNode m = JsonNode.Parse(manifest)!;
        m["mediaIncluded"]!.GetValue<bool>().Should().BeTrue();
        JsonArray files = m["files"]!.AsArray();
        files[0]!["name"]!.GetValue<string>().Should().Be("cam a.mp4");
        files[0]!["path"]!.GetValue<string>().Should().Be("media/cam a.mp4");
        files[0]!["status"]!.GetValue<string>().Should().Be("included");
        files[0]!["inPackage"]!.GetValue<bool>().Should().BeTrue();
        files[0]!["sizeBytes"]!.GetValue<long>().Should().Be(big.Length);
        files[0]!["durationSec"]!.GetValue<double>().Should().Be(5);
        files[0]!["usedBy"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("Opening");
        manifest.Should().NotContain("projects/p").And.NotContain("reelforge");
        ReadText(zip, "README.txt").Should().Contain("Premiere").And.Contain("Resolve").And.Contain("Final Cut");
    }

    [Fact]
    public async Task A_missing_or_unreadable_object_is_listed_and_the_zip_still_completes()
    {
        HandOffSource ok = Source("ok.mp4", "k-ok");
        HandOffSource gone = Source("gone.mp4", "k-gone");
        HandOffSource unresolved = Source("elsewhere.mp4", null);
        Mock<IFileStorageService> storage = FakeStorage(new() { ["k-ok"] = [1, 2, 3] });

        using ZipArchive zip = Open(await Build(Input([ok, gone, unresolved]), storage));

        zip.Entries.Select(e => e.FullName).Should().Equal("teaser-cut.otio", "media/ok.mp4", "README.txt", "manifest.json");
        JsonArray files = JsonNode.Parse(ReadText(zip, "manifest.json"))!["files"]!.AsArray();
        files.Select(f => f!["status"]!.GetValue<string>()).Should().Equal("included", "missing", "missing");
        files[1]!["inPackage"]!.GetValue<bool>().Should().BeFalse();
        files[1]!["reason"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        ReadText(zip, "README.txt").Should().Contain("2 could not be included");
    }

    [Fact]
    public async Task A_failure_part_way_through_a_file_is_reported_as_incomplete()
    {
        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), "k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new BrokenStream());

        using ZipArchive zip = Open(await Build(Input([Source("half.mp4", "k")]), storage));

        JsonNode.Parse(ReadText(zip, "manifest.json"))!["files"]![0]!["status"]!.GetValue<string>().Should().Be("incomplete");
        zip.Entries.Last().FullName.Should().Be("manifest.json");
    }

    private sealed class BrokenStream : Stream
    {
        private int _reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ > 0) throw new IOException("connection reset");
            buffer.Span[..10].Fill(7);
            return ValueTask.FromResult(10);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Manifest_only_package_reads_no_storage_and_says_why()
    {
        Mock<IFileStorageService> storage = FakeStorage([]);
        using ZipArchive zip = Open(await Build(Input([Source("a.mp4", "k-a"), Source("b.mp4", "k-b")], includeMedia: false), storage));

        zip.Entries.Select(e => e.FullName).Should().Equal("teaser-cut.otio", "README.txt", "manifest.json");
        JsonNode m = JsonNode.Parse(ReadText(zip, "manifest.json"))!;
        m["mediaIncluded"]!.GetValue<bool>().Should().BeFalse();
        m["mediaNotIncludedReason"]!.GetValue<string>().Should().Contain("without the media");
        m["files"]!.AsArray().Select(f => f!["status"]!.GetValue<string>()).Should().OnlyContain(s => s == "notIncluded");
        storage.Verify(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancellation_stops_reading_storage()
    {
        using CancellationTokenSource cts = new();
        Mock<IFileStorageService> storage = FakeStorage(new() { ["k1"] = [1], ["k2"] = [2] });
        storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), "k1", It.IsAny<CancellationToken>()))
            .Returns<Guid, string, CancellationToken>((_, _, _) => { cts.Cancel(); return Task.FromResult<Stream>(new MemoryStream([1])); });

        Func<Task> act = () => Build(Input([Source("a.mp4", "k1"), Source("b.mp4", "k2")]), storage, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        storage.Verify(s => s.DownloadAsync(It.IsAny<Guid>(), "k2", It.IsAny<CancellationToken>()), Times.Never);
    }

    private static string ReadText(ZipArchive zip, string name)
    {
        using StreamReader reader = new(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Planner_ExcludesQuarantinedFiles_AndListsThemMissing()
    {
        World w = NewWorld();
        ProjectFile csam = NewFile(w.Db, w.Project, "csam.mp4");
        csam.CsamStatus = CsamScanStatus.Quarantined;
        ProjectFile moderated = NewFile(w.Db, w.Project, "mod.mp4");
        moderated.ModerationStatus = FileModerationStatus.Quarantined;
        ProjectFile moved = NewFile(w.Db, w.Project, "moved.mp4");
        moved.StorageKey = $"projects/{w.Project.Id:D}/quarantine/x/moved.mp4";
        w.Db.SaveChanges();
        EditTimeline doc = Doc(w.Project.Id, VideoTrack("9",
            Clip("a", "ok", 0, 60, new AssetRef(AssetRefKind.ProjectFile, w.Cam.Id)),
            Clip("b", "a", 60, 120, new AssetRef(AssetRefKind.ProjectFile, csam.Id)),
            Clip("c", "b", 120, 180, new AssetRef(AssetRefKind.ProjectFile, moderated.Id)),
            Clip("d", "c", 180, 240, new AssetRef(AssetRefKind.ProjectFile, moved.Id))));

        HandOffPlan plan = await HandOffPlanner.PlanAsync(w.Db, w.Storage.Object, w.Project.Id, doc, 50, CancellationToken.None);

        Assert.Equal(1, plan.MediaFiles);
        Assert.Equal(3, plan.Sources.Count(s => !s.Resolvable));
        Assert.DoesNotContain(plan.Sources, s => s.StorageKey is { } k && k.Contains("quarantine"));
    }

    // ── Controller ───────────────────────────────────────────────────────────

    private sealed record World(
        InferenceApiDbContext Db, Project Project, Project Other, ProjectFile Cam, ProjectFile Music, ProjectFile Foreign,
        EditTimelineEntity Timeline, Dictionary<string, byte[]> Objects, Mock<IFileStorageService> Storage);

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ICurrentUser User(Guid userId)
    {
        Mock<ICurrentUser> user = new();
        user.SetupGet(u => u.UserId).Returns(userId);
        user.SetupGet(u => u.Email).Returns("handoff@example.com");
        user.SetupGet(u => u.IsAuthenticated).Returns(true);
        user.SetupGet(u => u.IsPlatformAdmin).Returns(false);
        user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(userId));
        user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);
        return user.Object;
    }

    private static Project NewProject(InferenceApiDbContext db, Guid ownerId)
    {
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        return project;
    }

    private static ProjectFile NewFile(InferenceApiDbContext db, Project project, string name, long size = 10_000, string mime = "video/mp4")
    {
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = name,
            StorageKey = $"projects/{project.Id:D}/userFiles/{Guid.NewGuid():N}/{name}",
            StorageBucket = "secret-bucket",
            Category = "userFiles",
            MimeType = mime,
            SizeBytes = size,
            UploadedAt = DateTime.UtcNow
        };
        db.ProjectFiles.Add(file);
        return file;
    }

    private static EditTimelineEntity NewTimeline(InferenceApiDbContext db, Project project, EditTimeline document, string name = "Teaser Cut")
    {
        EditTimelineEntity row = new()
        {
            Id = document.Id,
            ProjectId = project.Id,
            Name = name,
            DocumentJson = JsonSerializer.Serialize(document, EditTimelineJson.Options),
            Revision = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.EditTimelines.Add(row);
        return row;
    }

    private static World NewWorld(bool foreignClip = false)
    {
        InferenceApiDbContext db = NewDb();
        Guid owner = Guid.NewGuid();
        Project project = NewProject(db, owner);
        Project other = NewProject(db, Guid.NewGuid());
        ProjectFile cam = NewFile(db, project, "cam a.mp4");
        ProjectFile music = NewFile(db, project, "music.mp3", 10_000, "audio/mpeg");
        ProjectFile foreign = NewFile(db, other, "theirs.mp4");
        Guid timelineId = Guid.NewGuid();
        List<Item> items =
        [
            new MediaClip(Guid.NewGuid(), "Opening", 0, 60, new AssetRef(AssetRefKind.ProjectFile, cam.Id), 15),
            new MediaClip(Guid.NewGuid(), "Bed", 60, 150, new AssetRef(AssetRefKind.ProjectFile, music.Id))
        ];
        if (foreignClip) items.Add(new MediaClip(Guid.NewGuid(), "Sneaky", 150, 200, new AssetRef(AssetRefKind.ProjectFile, foreign.Id)));
        EditTimeline document = new(timelineId, project.Id, "Teaser Cut", Settings,
            [new Track(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null, items)], [], null, null);
        EditTimelineEntity row = NewTimeline(db, project, document);
        db.SaveChanges();

        Dictionary<string, byte[]> objects = new()
        {
            [cam.StorageKey] = Enumerable.Repeat((byte)'V', 10_000).ToArray(),
            [music.StorageKey] = Enumerable.Repeat((byte)'M', 10_000).ToArray(),
            [foreign.StorageKey] = Enumerable.Repeat((byte)'X', 10_000).ToArray()
        };
        return new World(db, project, other, cam, music, foreign, row, objects, FakeStorage(objects));
    }

    private sealed class CountingAccess(IProjectAccessService inner) : IProjectAccessService
    {
        public List<ProjectPermission> Calls { get; } = [];

        public Task<ProjectAccessResult> GetAsync(Guid projectId, ProjectPermission permission, CancellationToken ct)
        {
            Calls.Add(permission);
            if (permission != ProjectPermission.View) throw new InvalidOperationException("a hand-off only needs View");
            return inner.GetAsync(projectId, permission, ct);
        }

        public ProjectAccessResult Get(Guid projectId, ProjectPermission permission) => inner.Get(projectId, permission);

        public ProjectAccessOutcome AuthorizeOrganization(ProjectPermission permission) => inner.AuthorizeOrganization(permission);
    }

    private static EditTimelinesController Controller(
        World w, Guid? userId = null, HandOffOptions? options = null, HandOffLimiter? limiter = null, IProjectAccessService? access = null, PublishModeration? moderation = null)
    {
        ICurrentUser caller = User(userId ?? w.Project.OwnerId);
        HandOffOptions opts = options ?? new HandOffOptions();
        EditTimelinesController controller = new(
            w.Db, caller, access ?? TestTenancy.Access(w.Db, caller), w.Storage.Object, new Mock<IWorkflowExecutionService>().Object,
            publishModeration: moderation, handOffOptions: Options.Create(opts), handOffLimiter: limiter ?? new HandOffLimiter(Options.Create(opts)));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static async Task<(DefaultHttpContext Http, byte[] Body)> Execute(IActionResult result, CancellationToken aborted = default)
    {
        DefaultHttpContext http = new();
        http.Response.Body = new MemoryStream();
        http.RequestAborted = aborted;
        await result.ExecuteResultAsync(new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
        return (http, ((MemoryStream)http.Response.Body).ToArray());
    }

    [Fact]
    public async Task Hand_off_streams_a_zip_whose_references_are_its_entries()
    {
        World w = NewWorld();
        CountingAccess access = new(TestTenancy.Access(w.Db, User(w.Project.OwnerId)));

        IActionResult result = await Controller(w, access: access).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "otio", null, null, CancellationToken.None);

        access.Calls.Should().Equal(ProjectPermission.View);
        (DefaultHttpContext http, byte[] body) = await Execute(result);
        http.Response.ContentType.Should().Be("application/zip");
        http.Response.Headers["Content-Disposition"].ToString().Should().Contain("teaser-cut-hand-off.zip").And.Contain("filename*=UTF-8''teaser-cut-hand-off.zip");
        http.Response.Headers["Cache-Control"].ToString().Should().Be("no-store");
        http.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");

        using ZipArchive zip = Open(body);
        zip.Entries.Select(e => e.FullName).Should().Equal("teaser-cut.otio", "media/cam a.mp4", "media/music.mp3", "README.txt", "manifest.json");
        JsonNode otio = JsonNode.Parse(ReadText(zip, "teaser-cut.otio"))!;
        List<string> targets = otio["tracks"]!["children"]![0]!["children"]!.AsArray().Select(c => c!["media_reference"]?["target_url"]?.GetValue<string>()).Where(t => t is not null).Select(t => Uri.UnescapeDataString(t!)).ToList();
        targets.Should().Equal("media/cam a.mp4", "media/music.mp3");
        targets.Should().OnlyContain(t => zip.GetEntry(t) != null, "every reference is exactly a zip entry");
        foreach (string text in new[] { ReadText(zip, "manifest.json"), ReadText(zip, "README.txt"), http.Response.Headers.ToString() })
        {
            text.Should().NotContain(w.Cam.StorageKey).And.NotContain("secret-bucket").And.NotContain("projects/");
        }
    }

    [Fact]
    public async Task Hand_off_writes_edl_and_fcpxml_with_the_same_entry_names()
    {
        World w = NewWorld();
        foreach ((string format, string needle) in new[] { ("edl", "* SOURCE FILE: media/cam a.mp4"), ("fcpxml", "src=\"media/cam%20a.mp4\"") })
        {
            IActionResult result = await Controller(w).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, format, "zip", null, CancellationToken.None);
            (_, byte[] body) = await Execute(result);
            using ZipArchive zip = Open(body);
            zip.Entries[0].FullName.Should().Be("teaser-cut." + format);
            ReadText(zip, zip.Entries[0].FullName).Should().Contain(needle);
            zip.GetEntry("media/cam a.mp4").Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Hand_off_answers_404_across_tenants_and_for_an_unknown_timeline_and_400_for_bad_input()
    {
        World w = NewWorld();
        Guid stranger = Guid.NewGuid();
        (await Controller(w, userId: stranger).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "otio", null, null, CancellationToken.None))
            .Should().BeAssignableTo<Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult>().Which.StatusCode.Should().Be(404);
        (await Controller(w).HandOffEditTimeline(w.Project.Id, Guid.NewGuid(), "otio", null, null, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
        (await Controller(w).HandOffEditTimeline(w.Other.Id, w.Timeline.Id, "otio", null, null, CancellationToken.None))
            .Should().BeAssignableTo<Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult>().Which.StatusCode.Should().Be(404);
        (await Controller(w).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "srt", null, null, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await Controller(w).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "otio", "tar", null, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await Controller(w).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "edl", "zip", "not-a-track", CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        w.Storage.Verify(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_reference_into_another_project_is_listed_missing_and_never_read()
    {
        World w = NewWorld(foreignClip: true);

        (_, byte[] body) = await Execute(await Controller(w).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "otio", null, null, CancellationToken.None));

        using ZipArchive zip = Open(body);
        zip.Entries.Select(e => e.FullName).Should().NotContain(n => n.Contains("theirs"));
        JsonArray files = JsonNode.Parse(ReadText(zip, "manifest.json"))!["files"]!.AsArray();
        files.Should().HaveCount(3);
        files.Single(f => f!["status"]!.GetValue<string>() == "missing")!["name"]!.GetValue<string>().Should().Be("Sneaky");
        w.Storage.Verify(s => s.DownloadAsync(It.IsAny<Guid>(), w.Foreign.StorageKey, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Over_the_cap_the_zip_answers_409_and_manifest_only_still_works()
    {
        World w = NewWorld();
        HandOffOptions small = new() { MaxMediaBytes = 100 };

        IActionResult tooBig = await Controller(w, options: small).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "otio", "zip", null, CancellationToken.None);
        HandOffTooLargeResponse body = tooBig.Should().BeOfType<ConflictObjectResult>().Subject.Value.Should().BeOfType<HandOffTooLargeResponse>().Subject;
        body.Code.Should().Be("HANDOFF_TOO_LARGE");
        body.TotalBytes.Should().Be(20_000);
        body.CapBytes.Should().Be(100);
        body.Message.Should().NotBeNullOrWhiteSpace().And.NotContain("—");

        (await Controller(w, options: new HandOffOptions { MaxFiles = 1 }).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "otio", "zip", null, CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();
        (await Controller(w, options: new HandOffOptions { MaxMediaBytes = 0 }).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "otio", "zip", null, CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>("0 means manifest only");

        IActionResult manifestOnly = await Controller(w, options: small).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "otio", "manifest", null, CancellationToken.None);
        (_, byte[] zipBytes) = await Execute(manifestOnly);
        using ZipArchive zip = Open(zipBytes);
        zip.Entries.Select(e => e.FullName).Should().Equal("teaser-cut.otio", "README.txt", "manifest.json");
        w.Storage.Verify(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Too_many_open_streams_for_one_user_answers_429_until_one_finishes()
    {
        World w = NewWorld();
        HandOffOptions one = new() { MaxConcurrentPerUser = 1 };
        HandOffLimiter limiter = new(Options.Create(one));

        IActionResult first = await Controller(w, options: one, limiter: limiter).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "otio", null, null, CancellationToken.None);
        first.Should().BeOfType<HandOffStreamResult>();

        IActionResult busy = await Controller(w, options: one, limiter: limiter).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "otio", null, null, CancellationToken.None);
        ObjectResult refused = busy.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(429);
        JsonNode.Parse(JsonSerializer.Serialize(refused.Value))!["code"]!.GetValue<string>().Should().Be("HANDOFF_BUSY");

        await Execute(first);
        (await Controller(w, options: one, limiter: limiter).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "otio", null, null, CancellationToken.None))
            .Should().BeOfType<HandOffStreamResult>();
    }

    [Fact]
    public async Task The_publish_moderation_gate_blocks_a_hand_off_fail_closed()
    {
        World w = NewWorld();
        Mock<IContentModerationGate> gate = new();
        gate.Setup(g => g.ModerateAsync(It.IsAny<ModerationInput>(), It.IsAny<ModerationAttribution>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModerationDecision(ModerationAction.Block, true, false, "violence", "This content cannot be shared.", "hash"));
        Mock<IInferenceProviderResolver> providers = new();
        providers.Setup(p => p.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedTranscriptionProvider?)null);
        PublishModeration moderation = new(
            gate.Object, w.Storage.Object, new Mock<IModerationMediaSampler>().Object, providers.Object, new Mock<ITranscriptionClientFactory>().Object, w.Db,
            Options.Create(new ContentModerationOptions { Enabled = true }), NullLogger<PublishModeration>.Instance, new MemoryCache(new MemoryCacheOptions()),
            csamOptions: Options.Create(new ReelBolt.Inference.Api.Services.Csam.CsamOptions()));

        IActionResult result = await Controller(w, moderation: moderation).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "otio", "zip", null, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        w.Storage.Verify(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_clip_that_can_never_be_checked_refuses_the_hand_off_naming_the_clip_with_final_true_and_what_to_do()
    {
        World w = NewWorld();
        w.Storage.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(10_000, "video/mp4", "\"e1\""));
        w.Db.OutputModerationVerdicts.Add(new ReelBolt.Inference.Api.Services.Moderation.OutputModerationVerdict
        {
            Id = Guid.NewGuid(),
            OrganizationId = w.Project.OrganizationId,
            ProjectId = w.Project.Id,
            StorageKey = w.Cam.StorageKey,
            SourceVersion = "e1:10000",
            State = nameof(ModerationAction.Unavailable),
            ReasonCode = ModerationReasonCodes.Unclassified,
            ModelSnapshot = "m",
            NextAttemptAt = ReelBolt.Inference.Api.Services.Moderation.OutputModerationBackoff.Never
        });
        w.Db.SaveChanges();
        Mock<IContentModerationGate> gate = new();
        gate.Setup(g => g.ModerateAsync(It.IsAny<ModerationInput>(), It.IsAny<ModerationAttribution>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModerationDecision(ModerationAction.Allow, true, false, null, string.Empty, "hash"));
        PublishModeration moderation = new(
            gate.Object, w.Storage.Object, new Mock<IModerationMediaSampler>().Object,
            new Mock<IInferenceProviderResolver>().Object, new Mock<ITranscriptionClientFactory>().Object, w.Db,
            Options.Create(new ContentModerationOptions { Enabled = true }), NullLogger<PublishModeration>.Instance, new MemoryCache(new MemoryCacheOptions()),
            csamOptions: Options.Create(new ReelBolt.Inference.Api.Services.Csam.CsamOptions()));

        IActionResult result = await Controller(w, moderation: moderation).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "otio", "zip", null, CancellationToken.None);

        ObjectResult refusal = result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(409);
        string json = System.Text.Json.JsonSerializer.Serialize(refusal.Value);
        json.Should().Contain("\"final\":true");
        json.Should().Contain("cam a.mp4", "the clip is named");
        json.Should().Contain("Re-export it in a common format").And.Contain("package without media");
    }

    [Fact]
    public async Task Export_scope_reports_what_a_hand_off_would_carry()
    {
        World w = NewWorld();

        InterchangeScopeResponse scope = (await Controller(w).GetExportScope(w.Project.Id, w.Timeline.Id, "otio", CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<InterchangeScopeResponse>().Subject;
        scope.HandOff.Should().Be(new HandOffScopeResponse(true, 2, 20_000, false, new HandOffOptions().CapBytes));

        InterchangeScopeResponse capped = (await Controller(w, options: new HandOffOptions { MaxMediaBytes = 5 }).GetExportScope(w.Project.Id, w.Timeline.Id, "fcpxml", CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<InterchangeScopeResponse>().Subject;
        capped.HandOff!.WouldExceedCap.Should().BeTrue();
        capped.HandOff.CapBytes.Should().Be(5);

        InterchangeScopeResponse captions = (await Controller(w).GetExportScope(w.Project.Id, w.Timeline.Id, "srt", CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<InterchangeScopeResponse>().Subject;
        captions.HandOff!.Supported.Should().BeFalse();
    }

    // ── Import matching ──────────────────────────────────────────────────────

    private static readonly Guid Interview = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid Drone1 = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid Drone2 = Guid.Parse("10000000-0000-4000-8000-000000000003");
    private static readonly Guid Solo = Guid.Parse("10000000-0000-4000-8000-000000000004");

    private static ImportMediaMatcher Matcher() => new(
        [
            new MediaCandidate(Interview, "Interview.mp4", 10.0, 500),
            new MediaCandidate(Drone1, "drone.mp4", 5.0, 100),
            new MediaCandidate(Drone2, "drone.mp4", 8.0, 200),
            new MediaCandidate(Solo, "solo.mp4", null, 50)
        ],
        owned => owned.ProjectFileId is { } id && (id == Interview || id == Drone1 || id == Drone2 || id == Solo));

    [Theory]
    [InlineData("reelbolt://project-file/10000000-0000-4000-8000-000000000001", null, null, "id", "10000000-0000-4000-8000-000000000001")]
    [InlineData("file:///Users/x/INTERVIEW.MP4", null, null, "name", "10000000-0000-4000-8000-000000000001")]
    [InlineData("C:\\Footage\\interview.mp4", null, null, "name", "10000000-0000-4000-8000-000000000001")]
    [InlineData("media/Interview.mp4", 10.03, null, "nameAndDuration", "10000000-0000-4000-8000-000000000001")]
    [InlineData("media/drone.mp4", 8.0, null, "nameAndDuration", "10000000-0000-4000-8000-000000000003")]
    [InlineData("media/drone.mp4", null, 100L, "name", "10000000-0000-4000-8000-000000000002")]
    [InlineData("media/solo.mp4", 42.0, null, "name", "10000000-0000-4000-8000-000000000004")]
    public void Matching_prefers_id_then_name_with_duration(string url, double? duration, long? size, string how, string fileId)
    {
        MediaMatchResult result = Matcher().Resolve(new MediaMatchQuery(url, "clip", AvailableDurationSec: duration, SizeBytes: size, FrameSeconds: 1.0 / 25));
        result.How.Should().Be(how);
        result.Asset!.ProjectFileId.Should().Be(Guid.Parse(fileId));
    }

    [Theory]
    [InlineData("media/Interview.mp4", 12.0, "durationMismatch")]
    [InlineData("media/Interview.mp4", 10.1, "durationMismatch")]
    [InlineData("media/drone.mp4", null, "ambiguous")]
    [InlineData("media/drone.mp4", 6.5, "durationMismatch")]
    [InlineData("media/nothing.mov", null, "notFound")]
    [InlineData("reelbolt://project-file/99999999-0000-4000-8000-000000000001", null, "notFound")]
    [InlineData("", null, "notFound")]
    public void Matching_never_guesses(string url, double? duration, string reason)
    {
        MediaMatchResult result = Matcher().Resolve(new MediaMatchQuery(url, "clip", AvailableDurationSec: duration, FrameSeconds: 1.0 / 25));
        result.Asset.Should().BeNull();
        result.Reason.Should().Be(reason);
        if (reason == "ambiguous") result.Candidates!.Select(c => c.FileId).Should().BeEquivalentTo([Drone1, Drone2]);
    }

    [Fact]
    public void Matching_a_foreign_asset_url_falls_back_to_the_file_name_in_the_reference()
    {
        MediaMatchResult result = Matcher().Resolve(new MediaMatchQuery(
            "media/Interview.mp4", "clip", AssetUrl: "reelbolt://project-file/99999999-0000-4000-8000-000000000001", AvailableDurationSec: 10.0));
        result.Asset!.ProjectFileId.Should().Be(Interview);
        result.How.Should().Be("nameAndDuration");
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string PlainOtio(params (string Name, string Url, int Frames, double? AvailableSec)[] clips)
    {
        JsonArray children = [];
        foreach ((string name, string url, int frames, double? available) in clips)
        {
            JsonObject reference = new() { ["OTIO_SCHEMA"] = "ExternalReference.1", ["target_url"] = url };
            if (available is { } sec)
                reference["available_range"] = new JsonObject { ["OTIO_SCHEMA"] = "TimeRange.1", ["start_time"] = Rt(0), ["duration"] = Rt(sec * 30) };
            children.Add(new JsonObject
            {
                ["OTIO_SCHEMA"] = "Clip.1",
                ["name"] = name,
                ["source_range"] = new JsonObject { ["OTIO_SCHEMA"] = "TimeRange.1", ["start_time"] = Rt(0), ["duration"] = Rt(frames) },
                ["media_reference"] = reference
            });
        }

        return new JsonObject
        {
            ["OTIO_SCHEMA"] = "Timeline.1",
            ["name"] = "Hand back",
            ["global_start_time"] = Rt(0),
            ["tracks"] = new JsonObject { ["OTIO_SCHEMA"] = "Stack.1", ["children"] = new JsonArray(new JsonObject { ["OTIO_SCHEMA"] = "Track.1", ["kind"] = "Video", ["children"] = children }) }
        }.ToJsonString();

        static JsonObject Rt(double value) => new() { ["OTIO_SCHEMA"] = "RationalTime.1", ["rate"] = 30.0, ["value"] = value };
    }

    private static async Task<EditTimelineResponse> Import(EditTimelinesController controller, Guid projectId, string otio, bool strict = false)
    {
        ActionResult<EditTimelineResponse> result = await controller.ImportEditTimeline(projectId, new ImportEditTimelineRequest(null, Parse(otio)), CancellationToken.None, strict);
        return result.Result.Should().BeOfType<ObjectResult>().Subject.Value.Should().BeOfType<EditTimelineResponse>().Subject;
    }

    [Fact]
    public async Task Import_reports_matches_ambiguity_and_duration_mismatch_and_strict_mode_still_refuses()
    {
        World w = NewWorld();
        ProjectFile droneA = NewFile(w.Db, w.Project, "drone.mp4", 100);
        ProjectFile droneB = NewFile(w.Db, w.Project, "drone.mp4", 200);
        await w.Db.SaveChangesAsync();
        string otio = PlainOtio(("Cam", "file:///x/CAM A.mp4", 60, null), ("Drone", "file:///x/drone.mp4", 30, null), ("Bed", "file:///x/nothing.wav", 30, null));
        EditTimelinesController controller = Controller(w);

        EditTimelineResponse lenient = await Import(controller, w.Project.Id, otio);

        lenient.Matched.Should().ContainSingle().Which.Should().Be(new EditTimelineMatchedMedia("cam a.mp4", w.Cam.Id, "name"));
        lenient.Offline!.Select(o => o.Reason).Should().Equal("ambiguous", "notFound");
        lenient.Offline!.All(o => !string.IsNullOrWhiteSpace(o.Message)).Should().BeTrue();
        EditTimelineAmbiguousMedia ambiguous = lenient.Ambiguous.Should().ContainSingle().Subject;
        ambiguous.Name.Should().Be("drone.mp4");
        ambiguous.Candidates.Select(c => c.FileId).Should().BeEquivalentTo([droneA.Id, droneB.Id]);
        ambiguous.Candidates.Select(c => c.SizeBytes).Should().BeEquivalentTo([100L, 200L]);
        lenient.Document.Markers.Select(m => m.Name).Should().Equal("Offline: Drone", "Offline: Bed");
        lenient.Document.Tracks.Single().Items.Should().ContainSingle();

        ActionResult<EditTimelineResponse> strict = await controller.ImportEditTimeline(w.Project.Id, new ImportEditTimelineRequest(null, Parse(otio)), CancellationToken.None, strict: true);
        strict.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeOfType<EditTimelineImportRefusedResponse>()
            .Which.UnresolvedUrls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Import_refuses_a_name_whose_stated_length_disagrees_with_the_probe()
    {
        World w = NewWorld();
        w.Db.MediaDerivatives.Add(new MediaDerivative
        {
            Id = Guid.NewGuid(),
            ProjectId = w.Project.Id,
            SourceStorageKey = w.Cam.StorageKey,
            ParamsHash = "p",
            CreatedAt = DateTime.UtcNow,
            SourceProbeJson = new MediaSourceProbe(10.0, 30, 1, 1920, 1080, "h264", "aac", "mp4", 2, 48000, null, true).ToJson()
        });
        await w.Db.SaveChangesAsync();
        EditTimelinesController controller = Controller(w);

        EditTimelineResponse agree = await Import(controller, w.Project.Id, PlainOtio(("Cam", "media/cam%20a.mp4", 60, 10.0)));
        agree.Matched.Should().ContainSingle().Which.How.Should().Be("nameAndDuration");

        EditTimelineResponse differs = await Import(controller, w.Project.Id, PlainOtio(("Cam", "media/cam%20a.mp4", 60, 14.0)));
        differs.Offline.Should().ContainSingle().Which.Reason.Should().Be("durationMismatch");
        differs.Matched.Should().BeEmpty();
    }

    [Fact]
    public async Task A_hand_off_otio_round_trips_into_the_same_project_and_into_one_holding_the_same_file_names()
    {
        World w = NewWorld();
        (_, byte[] body) = await Execute(await Controller(w).HandOffEditTimeline(w.Project.Id, w.Timeline.Id, "otio", null, null, CancellationToken.None));
        string otio;
        using (ZipArchive zip = Open(body)) otio = ReadText(zip, "teaser-cut.otio");
        EditTimeline original = JsonSerializer.Deserialize<EditTimeline>(w.Timeline.DocumentJson, EditTimelineJson.Options)!;

        EditTimelineResponse same = await Import(Controller(w), w.Project.Id, otio);
        same.Offline.Should().BeEmpty();
        same.Matched!.Select(m => m.How).Should().OnlyContain(h => h == "id");
        same.Matched!.Select(m => m.Name).Should().BeEquivalentTo("cam a.mp4", "music.mp3");
        Cuts(same.Document).Should().Equal(Cuts(original));

        // A second project that holds files with the same names but other ids: matched by name, same cuts.
        ProjectFile cam2 = NewFile(w.Db, w.Other, "CAM A.mp4");
        ProjectFile music2 = NewFile(w.Db, w.Other, "music.mp3", mime: "audio/mpeg");
        await w.Db.SaveChangesAsync();
        EditTimelinesController otherOwner = Controller(w, userId: w.Other.OwnerId);
        EditTimelineResponse moved = await Import(otherOwner, w.Other.Id, otio);
        moved.Offline.Should().BeEmpty();
        moved.Matched!.Select(m => m.How).Should().OnlyContain(h => h == "name");
        moved.Matched!.Select(m => m.FileId).Should().BeEquivalentTo([cam2.Id, music2.Id]);
        Cuts(moved.Document).Select(c => (c.In, c.Out, c.SourceIn)).Should().Equal(Cuts(original).Select(c => (c.In, c.Out, c.SourceIn)));
        moved.Document.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().Select(c => c.AssetRef.ProjectFileId).Should().BeEquivalentTo([cam2.Id, music2.Id]);

        // Strict mode with a project that has neither file still refuses.
        Project bare = NewProject(w.Db, Guid.NewGuid());
        await w.Db.SaveChangesAsync();
        ActionResult<EditTimelineResponse> refused = await Controller(w, userId: bare.OwnerId).ImportEditTimeline(bare.Id, new ImportEditTimelineRequest(null, Parse(otio)), CancellationToken.None, strict: true);
        refused.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    private static List<(long In, long Out, long SourceIn)> Cuts(EditTimeline document) =>
        document.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().Select(c => (c.RecordIn, c.RecordOut, c.SourceIn)).ToList();
}
