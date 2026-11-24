#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Segmentation;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The browser-decodable mask preview of a segmentation track: the API route (404 / 202 / 200, Range, tenancy),
/// the pure encode argv, the cache identity, and the engine producer's hold and hash refusals. Written under the
/// test freeze: not yet run.
/// </summary>
public class SegmentationMaskPreviewTests
{
    private static readonly string Hash = new('a', 64);

    private static string ArtifactOf(Guid project, params (string Id, string MaskKey, string MaskHash, int Frames)[] tracks) =>
        "{\"segmentationTracks\":[" + string.Join(",", tracks.Select(t =>
            "{\"id\":\"" + t.Id + "\",\"prompt\":\"the dog\",\"sourceIndex\":0,\"startSec\":1,\"endSec\":3,\"fps\":30,\"frameCount\":" + t.Frames
            + ",\"maskStorageKey\":\"" + t.MaskKey + "\",\"maskContentHash\":\"" + t.MaskHash + "\"}")) + "]}";

    private static string GoodArtifact(Guid project) =>
        ArtifactOf(project, ("g1", $"projects/{project}/agentFiles/segmentation/m.mkv", Hash, 90));

    private static string AnalysisKey(Guid project) => $"projects/{project}/agentFiles/video-analysis/r/a.json";

    // ------------------------------------------------------------------------------------------
    // Cache identity (pure)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Cache_key_is_stable_and_depends_on_the_mask_hash_the_track_and_the_recipe()
    {
        string a = SegmentationMaskPreviewIdentity.CacheKey(Hash, "g1");

        SegmentationMaskPreviewIdentity.CacheKey(Hash, "g1").Should().Be(a);
        SegmentationMaskPreviewIdentity.CacheKey(Hash.ToUpperInvariant(), "g1").Should().Be(a, "the hash is compared case-insensitively");
        SegmentationMaskPreviewIdentity.CacheKey(new string('b', 64), "g1").Should().NotBe(a);
        SegmentationMaskPreviewIdentity.CacheKey(Hash, "g2").Should().NotBe(a);
        SegmentationMaskPreviewIdentity.CacheKey(Hash, "g1", SegmentationMaskPreviewIdentity.RecipeVersion + 1).Should().NotBe(a);
        a.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Components_cannot_trade_characters_across_the_key()
    {
        SegmentationMaskPreviewIdentity.CacheKey("ab", "c").Should().NotBe(SegmentationMaskPreviewIdentity.CacheKey("a", "bc"));
    }

    [Fact]
    public void Preview_lives_beside_never_inside_the_mask_directory_of_its_own_project()
    {
        Guid p = Guid.NewGuid();
        string key = SegmentationMaskPreviewIdentity.StorageKey(p, "k");

        key.Should().Be($"projects/{p:D}/agentFiles/segmentation-preview/k.mp4");
        key.Should().NotStartWith($"projects/{p:D}/{SegmentationMaskStore.KeyPrefix}");
        SegmentationMaskPreviewIdentity.NoneMarkerKey(p, "k").Should().Be($"projects/{p:D}/agentFiles/segmentation-preview/k.none");
        SegmentationMaskPreviewIdentity.MaskDirectory.Should().Be(SegmentationMaskStore.KeyPrefix);
    }

    // ------------------------------------------------------------------------------------------
    // Encoder argv (pure builder)
    // ------------------------------------------------------------------------------------------

    private static string After(string[] argv, string flag) => argv[Array.IndexOf(argv, flag) + 1];

    [Fact]
    public void Argv_pins_the_demuxer_the_protocol_and_the_input_before_the_output()
    {
        string[] argv = SegmentationMaskPreviewArgvBuilder.Build("/s/mask.mkv", "/s/out.mp4", 30, 90);

        After(argv, "-format_whitelist").Should().Be(MediaDerivativeInputFormats.Whitelist);
        After(argv, "-protocol_whitelist").Should().Be("file");
        Array.IndexOf(argv, "-format_whitelist").Should().BeLessThan(Array.IndexOf(argv, "-i"));
        After(argv, "-i").Should().Be("/s/mask.mkv");
        argv[^1].Should().Be("/s/out.mp4");
        argv.Should().Contain("-an");
    }

    [Fact]
    public void Argv_scales_to_a_640_long_side_without_upscaling_in_full_range_yuv420p()
    {
        string[] argv = SegmentationMaskPreviewArgvBuilder.Build("/s/m.mkv", "/s/o.mp4", 30, 90);
        string filter = After(argv, "-vf");

        filter.Should().Contain("min(640,iw)").And.Contain("min(640,ih)");
        filter.Should().Contain("force_original_aspect_ratio=decrease").And.Contain("force_divisible_by=2");
        filter.Should().Contain("in_range=full").And.Contain("out_range=full").And.EndWith("format=yuv420p");
        After(argv, "-pix_fmt").Should().Be("yuv420p");
        After(argv, "-color_range").Should().Be("pc");
        After(argv, "-x264-params").Should().Be("fullrange=on");
        After(argv, "-c:v").Should().Be("libx264");
        After(argv, "-movflags").Should().Be("+faststart");
        After(argv, "-f").Should().Be("mp4");
    }

    [Theory]
    [InlineData(30.0, 30)]
    [InlineData(29.97, 30)]
    [InlineData(24.0, 24)]
    [InlineData(60.0, 60)]
    [InlineData(0.5, 1)]
    public void Argv_keeps_the_masks_own_fps_and_frame_count_with_a_keyframe_at_least_every_second(double fps, int gop)
    {
        string[] argv = SegmentationMaskPreviewArgvBuilder.Build("/s/m.mkv", "/s/o.mp4", fps, 123);

        After(argv, "-fps_mode").Should().Be("cfr");
        After(argv, "-r").Should().Be(fps.ToString(System.Globalization.CultureInfo.InvariantCulture));
        After(argv, "-frames:v").Should().Be("123");
        After(argv, "-g").Should().Be(gop.ToString(System.Globalization.CultureInfo.InvariantCulture));
        int.Parse(After(argv, "-g"), System.Globalization.CultureInfo.InvariantCulture).Should().BeLessThanOrEqualTo((int)Math.Ceiling(Math.Max(fps, 1)));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(double.NaN, 10)]
    [InlineData(30, 0)]
    public void Argv_refuses_a_mask_it_cannot_hold_to_a_frame_rate_and_count(double fps, int frames)
    {
        Action act = () => SegmentationMaskPreviewArgvBuilder.Build("/s/m.mkv", "/s/o.mp4", fps, frames);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ------------------------------------------------------------------------------------------
    // Previewable track
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_track_is_previewable_only_with_its_own_projects_mask_a_sha256_and_a_frame_count()
    {
        Guid p = Guid.NewGuid();
        ReelBolt.Shared.Workflows.VideoSegmentationTrack Make(string key, string hash, int frames, double fps = 30) =>
            new("g1", 0, "x", 0, 1, frames, fps, 0.1, 0.9, key, hash, "m");
        string own = $"projects/{p}/agentFiles/segmentation/m.mkv";

        SegmentationMaskPreviewIdentity.IsPreviewable(p, Make(own, Hash, 90)).Should().BeTrue();
        SegmentationMaskPreviewIdentity.IsPreviewable(p, Make($"projects/{Guid.NewGuid()}/agentFiles/segmentation/m.mkv", Hash, 90)).Should().BeFalse();
        SegmentationMaskPreviewIdentity.IsPreviewable(p, Make($"projects/{p}/agentFiles/segmentation/../x.mkv", Hash, 90)).Should().BeFalse();
        SegmentationMaskPreviewIdentity.IsPreviewable(p, Make(own, "abc", 90)).Should().BeFalse();
        SegmentationMaskPreviewIdentity.IsPreviewable(p, Make(own, Hash, 0)).Should().BeFalse();
        SegmentationMaskPreviewIdentity.IsPreviewable(p, Make(own, Hash, 90, fps: 0)).Should().BeFalse();
    }

    // ------------------------------------------------------------------------------------------
    // API route
    // ------------------------------------------------------------------------------------------

    private sealed class Fixture : IDisposable
    {
        public required InferenceApiDbContext Db { get; init; }
        public Guid ProjectId { get; init; }
        public Guid OtherProjectId { get; init; }
        public Guid OwnerId { get; init; }
        private Guid _definitionId;

        public Mock<IFileStorageService> Storage { get; } = new();
        public Mock<IAmazonS3> S3 { get; } = new();
        public Mock<IPublishEndpoint> Publish { get; } = new();
        public HashSet<string> Stored { get; } = [];
        public byte[] Whole { get; set; } = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();

        public static async Task<Fixture> CreateAsync()
        {
            InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Guid owner = Guid.NewGuid();
            Project Make() => new() { Id = Guid.NewGuid(), Name = "P", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Project a = Make(), b = Make();
            db.Projects.AddRange(a, b);
            await db.SaveChangesAsync();
            return new Fixture { Db = db, ProjectId = a.Id, OtherProjectId = b.Id, OwnerId = owner };
        }

        public async Task<Guid> AddResultAsync(Guid projectId, StepType type, string key, DateTime? executedAt = null, DateTime? supersededAt = null)
        {
            if (_definitionId == Guid.Empty)
            {
                _definitionId = Guid.NewGuid();
                Db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = _definitionId, ProjectId = ProjectId, Name = "w", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            }

            Guid exec = Guid.NewGuid(), step = Guid.NewGuid(), result = Guid.NewGuid();
            Db.WorkflowExecutions.Add(new WorkflowExecution { Id = exec, WorkflowDefinitionId = _definitionId, ProjectId = projectId, OrganizationId = TestTenancy.OrgOf(OwnerId), StartedAt = executedAt ?? default, FirstStartedAt = executedAt });
            Db.WorkflowSteps.Add(new WorkflowStep { Id = step, WorkflowDefinitionId = _definitionId, StepOrder = 1, StepType = type });
            Db.WorkflowStepResults.Add(new WorkflowStepResult { Id = result, WorkflowExecutionId = exec, WorkflowStepId = step, ArtifactStorageKey = key, ExecutedAt = executedAt ?? default, SupersededAt = supersededAt });
            await Db.SaveChangesAsync();
            return result;
        }

        public ReelBolt.Inference.Api.Services.Auth.IProjectAccessService Access(InferenceApiDbContext? db = null)
        {
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(OwnerId);
            user.SetupGet(u => u.Email).Returns("t@example.com");
            user.SetupGet(u => u.IsAuthenticated).Returns(true);
            user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(OwnerId));
            user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);
            return TestTenancy.Access(db ?? Db, user.Object);
        }

        public SegmentationMaskPreviewController Controller(string artifact, InferenceApiDbContext? db = null, bool unreadable = false)
        {
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(OwnerId);
            user.SetupGet(u => u.Email).Returns("t@example.com");
            user.SetupGet(u => u.IsAuthenticated).Returns(true);
            user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(OwnerId));
            user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);

            Storage.SetupGet(s => s.BucketName).Returns("bucket");
            Storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(() => unreadable
                    ? throw new IOException("gone")
                    : Task.FromResult((Stream)new MemoryStream(Encoding.UTF8.GetBytes(artifact))));
            Storage.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, string key, CancellationToken _) =>
                    Stored.Contains(key) ? new StoredObjectMetadata(Whole.Length, "video/mp4") : null);
            // RangedObjectStreamer reads the length through the (bucket, key) overload.
            S3.Setup(c => c.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new GetObjectMetadataResponse { ContentLength = Whole.Length });
            S3.Setup(c => c.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new GetObjectMetadataResponse { ContentLength = Whole.Length });
            S3.Setup(c => c.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream(Whole) });

            return new SegmentationMaskPreviewController(
                db ?? Db, TestTenancy.Access(db ?? Db, user.Object), Storage.Object, S3.Object, Publish.Object,
                NullLogger<SegmentationMaskPreviewController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Foreign_project_unknown_result_wrong_step_wrong_prefix_unknown_track_and_unpreviewable_track_are_all_404()
    {
        using Fixture f = await Fixture.CreateAsync();
        string artifact = GoodArtifact(f.ProjectId);
        Guid foreign = await f.AddResultAsync(f.OtherProjectId, StepType.VideoAnalyze, AnalysisKey(f.OtherProjectId));
        Guid wrongType = await f.AddResultAsync(f.ProjectId, StepType.Agent, AnalysisKey(f.ProjectId));
        Guid wrongPrefix = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, AnalysisKey(f.OtherProjectId));
        Guid good = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, AnalysisKey(f.ProjectId));

        foreach (Guid id in new[] { foreign, Guid.NewGuid(), wrongType, wrongPrefix })
        {
            (await f.Controller(artifact).Get(f.ProjectId, id, "g1", default)).Should().BeOfType<NotFoundResult>();
        }

        (await f.Controller(artifact).Get(f.ProjectId, good, "nope", default)).Should().BeOfType<NotFoundResult>();

        // a mask under another project's directory, a bad hash and no frames are not previewable
        string foreignMask = ArtifactOf(f.ProjectId, ("g1", $"projects/{f.OtherProjectId}/agentFiles/segmentation/m.mkv", Hash, 90));
        string badHash = ArtifactOf(f.ProjectId, ("g1", $"projects/{f.ProjectId}/agentFiles/segmentation/m.mkv", "abc", 90));
        string noFrames = ArtifactOf(f.ProjectId, ("g1", $"projects/{f.ProjectId}/agentFiles/segmentation/m.mkv", Hash, 0));
        foreach (string a in new[] { foreignMask, badHash, noFrames })
        {
            // A fresh result per artifact: the controller caches a result's track keys by (project, result, artifact key).
            Guid own = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, AnalysisKey(f.ProjectId));
            (await f.Controller(a).Get(f.ProjectId, own, "g1", default)).Should().BeOfType<NotFoundResult>();
        }

        f.Publish.Verify(p => p.Publish(It.IsAny<SegmentationMaskPreviewRequested>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_unreadable_artifact_is_404()
    {
        using Fixture f = await Fixture.CreateAsync();
        Guid id = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, AnalysisKey(f.ProjectId) + "x");

        (await f.Controller(GoodArtifact(f.ProjectId), unreadable: true).Get(f.ProjectId, id, "g1", default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_quarantined_projects_earlier_or_superseded_result_is_a_404_and_a_later_one_is_served()
    {
        using Fixture f = await Fixture.CreateAsync();
        DateTime marker = DateTime.UtcNow.AddHours(-3);
        (await f.Db.Projects.FindAsync(f.ProjectId))!.ContentQuarantinedAt = marker;
        await f.Db.SaveChangesAsync();
        string key = AnalysisKey(f.ProjectId);
        Guid earlier = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, key + "e1", executedAt: marker.AddHours(-1));
        Guid superseded = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, key + "e2", executedAt: marker.AddHours(1), supersededAt: DateTime.UtcNow);
        Guid later = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, key + "e3", executedAt: marker.AddHours(1));

        (await f.Controller(GoodArtifact(f.ProjectId)).Get(f.ProjectId, earlier, "g1", default)).Should().BeOfType<NotFoundResult>();
        (await f.Controller(GoodArtifact(f.ProjectId)).Get(f.ProjectId, superseded, "g1", default)).Should().BeOfType<NotFoundResult>();
        (await f.Controller(GoodArtifact(f.ProjectId)).Get(f.ProjectId, later, "g1", default)).Should().BeOfType<ObjectResult>("a result after the marker is served (here: pending)");
        f.Publish.Verify(p => p.Publish(It.Is<SegmentationMaskPreviewRequested>(m => m.StepResultId != later), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_result_of_another_project_is_a_plain_404_even_when_that_project_holds_its_output()
    {
        using Fixture f = await Fixture.CreateAsync();
        (await f.Db.Projects.FindAsync(f.OtherProjectId))!.CsamOutputHoldAt = DateTime.UtcNow.AddHours(-1);
        await f.Db.SaveChangesAsync();
        Guid foreign = await f.AddResultAsync(f.OtherProjectId, StepType.VideoAnalyze, AnalysisKey(f.OtherProjectId), executedAt: DateTime.UtcNow.AddHours(-5));

        (await f.Controller(GoodArtifact(f.OtherProjectId)).Get(f.ProjectId, foreign, "g1", default)).Should().BeOfType<NotFoundResult>("not the hold body, which would confirm the id exists");
    }

    [Fact]
    public async Task Not_ready_is_202_pending_and_queues_one_request_then_ready_is_200_video()
    {
        using Fixture f = await Fixture.CreateAsync();
        Guid id = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, AnalysisKey(f.ProjectId) + "y");
        SegmentationMaskPreviewController controller = f.Controller(GoodArtifact(f.ProjectId));

        IActionResult first = await controller.Get(f.ProjectId, id, "g1", default);

        ObjectResult accepted = first.Should().BeOfType<ObjectResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        JsonSerializer.Serialize(accepted.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Should().Be("{\"state\":\"pending\"}");
        f.Publish.Verify(
            p => p.Publish(It.Is<SegmentationMaskPreviewRequested>(m => m.ProjectId == f.ProjectId && m.StepResultId == id && m.TrackId == "g1"), It.IsAny<CancellationToken>()),
            Times.Once);

        // the engine stored the preview under the identity the API computes
        f.Stored.Add(SegmentationMaskPreviewIdentity.StorageKey(f.ProjectId, SegmentationMaskPreviewIdentity.CacheKey(Hash, "g1")));
        IActionResult second = await controller.Get(f.ProjectId, id, "g1", default);

        FileStreamResult file = second.Should().BeOfType<FileStreamResult>().Subject;
        file.ContentType.Should().Be("video/mp4");
        controller.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
        controller.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        f.Publish.Verify(p => p.Publish(It.IsAny<SegmentationMaskPreviewRequested>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_range_request_is_answered_206_with_the_content_range()
    {
        using Fixture f = await Fixture.CreateAsync();
        Guid id = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, AnalysisKey(f.ProjectId) + "z");
        f.Stored.Add(SegmentationMaskPreviewIdentity.StorageKey(f.ProjectId, SegmentationMaskPreviewIdentity.CacheKey(Hash, "g1")));
        SegmentationMaskPreviewController controller = f.Controller(GoodArtifact(f.ProjectId));
        controller.Request.Headers.Range = "bytes=0-99";

        IActionResult result = await controller.Get(f.ProjectId, id, "g1", default);

        result.Should().BeOfType<FileStreamResult>();
        controller.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        controller.Response.Headers.ContentRange.ToString().Should().Be("bytes 0-99/1000");
    }

    [Fact]
    public async Task An_unverified_mask_marker_is_404_and_queues_nothing()
    {
        using Fixture f = await Fixture.CreateAsync();
        Guid id = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, AnalysisKey(f.ProjectId) + "m");
        f.Stored.Add(SegmentationMaskPreviewIdentity.NoneMarkerKey(f.ProjectId, SegmentationMaskPreviewIdentity.CacheKey(Hash, "g1")));

        (await f.Controller(GoodArtifact(f.ProjectId)).Get(f.ProjectId, id, "g1", default)).Should().BeOfType<NotFoundResult>();
        f.Publish.Verify(p => p.Publish(It.IsAny<SegmentationMaskPreviewRequested>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_tracks_list_carries_the_preview_state_without_any_key()
    {
        using Fixture f = await Fixture.CreateAsync();
        string artifact = ArtifactOf(
            f.ProjectId,
            ("g1", $"projects/{f.ProjectId}/agentFiles/segmentation/a.mkv", Hash, 90),
            ("g2", $"projects/{f.ProjectId}/agentFiles/segmentation/b.mkv", new string('b', 64), 90),
            ("g3", $"projects/{f.ProjectId}/agentFiles/segmentation/c.mkv", new string('c', 64), 90),
            ("g4", "projects/other/agentFiles/segmentation/d.mkv", Hash, 90));
        Guid id = await f.AddResultAsync(f.ProjectId, StepType.VideoAnalyze, AnalysisKey(f.ProjectId) + "l");
        f.Stored.Add(SegmentationMaskPreviewIdentity.StorageKey(f.ProjectId, SegmentationMaskPreviewIdentity.CacheKey(Hash, "g1")));
        f.Stored.Add(SegmentationMaskPreviewIdentity.NoneMarkerKey(f.ProjectId, SegmentationMaskPreviewIdentity.CacheKey(new string('c', 64), "g3")));
        f.Controller(artifact); // wires the storage mock the tracks controller below shares

        SegmentationTracksController tracks = new(f.Db, f.Access(), f.Storage.Object, NullLogger<SegmentationTracksController>.Instance);
        ActionResult<IReadOnlyList<SegmentationTrackResponse>> r = await tracks.Get(f.ProjectId, id, default);

        IReadOnlyList<SegmentationTrackResponse> list = (IReadOnlyList<SegmentationTrackResponse>)r.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        list.ToDictionary(t => t.TrackId, t => t.MaskPreview.State).Should().Equal(new Dictionary<string, string>
        {
            ["g1"] = "ready",
            ["g2"] = "pending",
            ["g3"] = "none",
            ["g4"] = "none"
        });
        string json = JsonSerializer.Serialize(list, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().Contain("\"maskPreview\":{\"state\":\"ready\"}");
        json.ToLowerInvariant().Should().NotContain("bucket").And.NotContain("projects/").And.NotContain(Hash);
    }

    // ------------------------------------------------------------------------------------------
    // Engine producer
    // ------------------------------------------------------------------------------------------

    private sealed class OptionsFactory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }

    private sealed class EngineWorld : IDisposable
    {
        public Guid ProjectId { get; } = Guid.NewGuid();
        public Guid HeldResult { get; private set; }
        public Guid ServedResult { get; private set; }
        public string Scratch { get; } = Path.Combine(Path.GetTempPath(), "mask-preview-tests-" + Guid.NewGuid().ToString("N"));
        public IDbContextFactory<WorkflowEngineDbContext> Factory { get; private set; } = null!;
        public Mock<IProjectFileWorkspace> Files { get; } = new();
        public Mock<IVideoToolRunner> Runner { get; } = new();
        public byte[] MaskBytes { get; set; } = Encoding.UTF8.GetBytes("pretend-ffv1");
        public string MaskHash { get; set; } = "";
        public bool PreviewStored { get; set; }
        public List<string[]> Argvs { get; } = [];
        public List<string> Uploads { get; } = [];

        public static async Task<EngineWorld> CreateAsync(bool hold, bool quarantine = false)
        {
            EngineWorld w = new();
            w.Factory = new OptionsFactory(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            DateTime marker = DateTime.UtcNow.AddHours(-3);
            await using (WorkflowEngineDbContext db = w.Factory.CreateDbContext())
            {
                Guid step = Guid.NewGuid();
                db.Projects.Add(new Project { Id = w.ProjectId, Name = "p", CsamOutputHoldAt = hold ? marker : null, ContentQuarantinedAt = quarantine ? marker : null });
                db.WorkflowSteps.Add(new WorkflowStep { Id = step, WorkflowDefinitionId = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoAnalyze });
                w.HeldResult = AddResult(db, w.ProjectId, step, marker.AddHours(-1));
                w.ServedResult = AddResult(db, w.ProjectId, step, marker.AddHours(1));
                await db.SaveChangesAsync();
            }

            w.Files.Setup(f => f.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, string _, string path, long _, CancellationToken _) =>
                {
                    File.WriteAllText(path, GoodArtifact(w.ProjectId).Replace(Hash, w.MaskHash.Length == 0 ? Hash : w.MaskHash));
                    return Task.CompletedTask;
                });
            w.Files.Setup(f => f.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, string _, string path, CancellationToken _) =>
                {
                    File.WriteAllBytes(path, w.MaskBytes);
                    return Task.CompletedTask;
                });
            w.Files.Setup(f => f.ArtifactExistsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, string key, CancellationToken _) => w.PreviewStored && key.EndsWith(".mp4", StringComparison.Ordinal));
            w.Files.Setup(f => f.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid _, string _, string name, string _, CancellationToken _, string _) =>
                {
                    w.Uploads.Add(name);
                    return name;
                });
            w.Runner.Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), VideoToolPriority.Low))
                .ReturnsAsync((IReadOnlyList<string> args, TimeSpan _, CancellationToken _, VideoToolPriority _) =>
                {
                    w.Argvs.Add(args.ToArray());
                    File.WriteAllBytes(args[^1], [1, 2, 3]);
                    return new VideoToolResult(0, "", "", false);
                });
            return w;
        }

        private static Guid AddResult(WorkflowEngineDbContext db, Guid projectId, Guid step, DateTime at)
        {
            Guid exec = Guid.NewGuid(), result = Guid.NewGuid();
            db.WorkflowExecutions.Add(new WorkflowExecution { Id = exec, ProjectId = projectId, WorkflowDefinitionId = Guid.NewGuid(), StartedAt = at, FirstStartedAt = at });
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = result,
                WorkflowExecutionId = exec,
                WorkflowStepId = step,
                ExecutedAt = at,
                ArtifactStorageKey = AnalysisKey(projectId)
            });
            return result;
        }

        public SegmentationMaskPreviewService Service() => new(
            Factory, Files.Object, Runner.Object,
            Options.Create(new VideoEditingOptions { ScratchPath = Scratch }),
            NullLogger<SegmentationMaskPreviewService>.Instance);

        public SegmentationMaskPreviewRequested Request(Guid result, string track = "g1") => new(ProjectId, result, track, DateTime.UtcNow);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Scratch, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    [Fact]
    public async Task A_result_inside_the_output_hold_is_refused_and_nothing_is_read_or_encoded()
    {
        using EngineWorld w = await EngineWorld.CreateAsync(hold: true);

        (await w.Service().ProduceAsync(w.Request(w.HeldResult), default)).Should().Be(SegmentationMaskPreviewOutcome.Refused);

        w.Files.Verify(f => f.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        w.Runner.Verify(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<VideoToolPriority>()), Times.Never);
        w.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_result_that_predates_the_quarantine_marker_is_refused_and_a_later_one_is_not()
    {
        using EngineWorld w = await EngineWorld.CreateAsync(hold: false, quarantine: true);
        w.PreviewStored = true;

        (await w.Service().ProduceAsync(w.Request(w.HeldResult), default)).Should().Be(SegmentationMaskPreviewOutcome.Refused);
        (await w.Service().ProduceAsync(w.Request(w.ServedResult), default)).Should().Be(SegmentationMaskPreviewOutcome.CacheHit);
    }

    [Fact]
    public async Task A_superseded_result_is_refused_once_the_project_is_quarantined_but_not_before()
    {
        foreach (bool quarantine in new[] { true, false })
        {
            using EngineWorld w = await EngineWorld.CreateAsync(hold: false, quarantine: quarantine);
            w.PreviewStored = true;
            await using (WorkflowEngineDbContext db = w.Factory.CreateDbContext())
            {
                (await db.WorkflowStepResults.FindAsync(w.ServedResult))!.SupersededAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            (await w.Service().ProduceAsync(w.Request(w.ServedResult), default))
                .Should().Be(quarantine ? SegmentationMaskPreviewOutcome.Refused : SegmentationMaskPreviewOutcome.CacheHit);
        }
    }

    [Fact]
    public async Task A_result_produced_after_the_hold_is_not_refused_by_it()
    {
        using EngineWorld w = await EngineWorld.CreateAsync(hold: true);
        w.PreviewStored = true;

        (await w.Service().ProduceAsync(w.Request(w.ServedResult), default)).Should().Be(SegmentationMaskPreviewOutcome.CacheHit);
    }

    [Fact]
    public async Task An_unknown_track_a_foreign_project_and_empty_ids_are_refused()
    {
        using EngineWorld w = await EngineWorld.CreateAsync(hold: false);
        SegmentationMaskPreviewService service = w.Service();

        (await service.ProduceAsync(w.Request(w.ServedResult, "nope"), default)).Should().Be(SegmentationMaskPreviewOutcome.Refused);
        (await service.ProduceAsync(new SegmentationMaskPreviewRequested(Guid.NewGuid(), w.ServedResult, "g1", DateTime.UtcNow), default)).Should().Be(SegmentationMaskPreviewOutcome.Refused);
        (await service.ProduceAsync(w.Request(Guid.Empty), default)).Should().Be(SegmentationMaskPreviewOutcome.Refused);
        (await service.ProduceAsync(w.Request(w.ServedResult, " "), default)).Should().Be(SegmentationMaskPreviewOutcome.Refused);
    }

    [Fact]
    public async Task A_mask_that_fails_its_hash_check_gets_no_preview_and_a_marker()
    {
        using EngineWorld w = await EngineWorld.CreateAsync(hold: false);
        w.MaskBytes = Encoding.UTF8.GetBytes("not the mask the artifact recorded");

        (await w.Service().ProduceAsync(w.Request(w.ServedResult), default)).Should().Be(SegmentationMaskPreviewOutcome.Unverified);

        w.Runner.Verify(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<VideoToolPriority>()), Times.Never);
        w.Uploads.Should().ContainSingle().Which.Should().EndWith(".none");
    }

    [Fact]
    public async Task A_verified_mask_is_encoded_with_the_recipe_and_uploaded_under_the_cache_key()
    {
        using EngineWorld w = await EngineWorld.CreateAsync(hold: false);
        w.MaskHash = Convert.ToHexStringLower(SHA256.HashData(w.MaskBytes));

        (await w.Service().ProduceAsync(w.Request(w.ServedResult), default)).Should().Be(SegmentationMaskPreviewOutcome.Produced);

        string[] argv = w.Argvs.Should().ContainSingle().Subject;
        After(argv, "-format_whitelist").Should().Be(MediaDerivativeInputFormats.Whitelist);
        After(argv, "-frames:v").Should().Be("90");
        After(argv, "-r").Should().Be("30");
        w.Uploads.Should().ContainSingle().Which.Should()
            .Be(SegmentationMaskPreviewIdentity.UploadFileName(SegmentationMaskPreviewIdentity.CacheKey(w.MaskHash, "g1")));
    }

    [Fact]
    public async Task A_stored_preview_is_a_cache_hit_that_encodes_and_uploads_nothing()
    {
        using EngineWorld w = await EngineWorld.CreateAsync(hold: false);
        w.PreviewStored = true;

        (await w.Service().ProduceAsync(w.Request(w.ServedResult), default)).Should().Be(SegmentationMaskPreviewOutcome.CacheHit);

        w.Argvs.Should().BeEmpty();
        w.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task The_dispatcher_queues_a_request_once_while_it_is_in_flight()
    {
        using EngineWorld w = await EngineWorld.CreateAsync(hold: false);
        SegmentationMaskPreviewDispatcher dispatcher = new(w.Service(), NullLogger<SegmentationMaskPreviewDispatcher>.Instance);
        SegmentationMaskPreviewRequested request = w.Request(w.ServedResult);

        dispatcher.TryEnqueue(request).Should().BeTrue();
        dispatcher.TryEnqueue(request).Should().BeFalse("the editor polls, so the same request arrives again while it is queued");
        dispatcher.TryEnqueue(w.Request(w.ServedResult, "g2")).Should().BeTrue();
    }
}
