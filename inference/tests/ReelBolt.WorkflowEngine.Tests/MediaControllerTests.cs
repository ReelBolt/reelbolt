#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Workflows;
using Xunit;
// Aliased rather than imported by namespace: the WorkflowEngine's video services and the Inference
// API's DTO namespace both carry media vocabulary, and an alias makes it impossible for a future name
// collision to silently rebind one of these to the wrong type.
using FilmstripIndex = ReelBolt.WorkflowEngine.Services.Video.FilmstripIndex;
using FilmstripSheetFrames = ReelBolt.WorkflowEngine.Services.Video.FilmstripSheetFrames;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The media read surface's contract tests.
///
/// <para>
/// The single most important thing asserted here is <b>404, never 403</b>: every endpoint answers the
/// same empty not-found for a project that does not exist, a project that belongs to somebody else,
/// and a mediaRef or derivativeId that is not in this project. A <c>403</c> on any of those would
/// turn the media routes into an existence oracle, so several of the tests below assert the ABSENCE
/// of a <see cref="ForbidResult"/> explicitly rather than only the presence of a
/// <see cref="NotFoundResult"/>.
/// </para>
/// <para>
/// The second theme is that no response ever carries a storage key, a bucket name or an object URL —
/// asserted by serializing the real response body and searching it, not by inspecting the DTO
/// declarations.
/// </para>
/// </summary>
public class MediaControllerTests
{
    /// <summary>
    /// The API's real response serializer, mirrored exactly: web defaults and <b>no options-level
    /// enum converter</b>.
    /// </summary>
    /// <remarks>
    /// An options-level <see cref="JsonStringEnumConverter"/> takes precedence over the type-level
    /// attributes that carry the media contract's camelCase encoding, so adding one here would make
    /// the serializer tests pass while the API wrote PascalCase — the drift the attributes exist to
    /// prevent. The Inference API configures no such converter (nothing calls
    /// <c>AddJsonOptions</c>), which is why the attributes are load-bearing.
    /// </remarks>
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The engine's own artifact options (<c>VideoAnalyzeStepExecutor.ArtifactJsonOptions</c>), used
    /// to write the fixture artifact the way the real writer would.
    /// </summary>
    private static readonly JsonSerializerOptions ArtifactJson =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // -------------------------------------------------------------------------------------------
    // Harness
    // -------------------------------------------------------------------------------------------

    private static InferenceApiDbContext CreateContext()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InferenceApiDbContext(options);
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsAdmin { get; } = false;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    /// <summary>
    /// A controller over an in-memory database, a mocked object store and a capture-only publish
    /// endpoint, plus the project the current user owns.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            UserId = Guid.NewGuid();
            Db = CreateContext();
            Project = NewProject(UserId);
            Db.Projects.Add(Project);
            Db.SaveChanges();

            Publisher
                .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
                .Callback<MediaDerivativesRequested, CancellationToken>((message, _) => Published.Add(message))
                .Returns(Task.CompletedTask);

            S3 = StorageBackedS3.Over(Storage);

            Controller = new MediaController(
                Db,
                TestTenancy.Access(Db, new FakeCurrentUser(UserId)),
                Storage.Object,
                S3.Object,
                Publisher.Object,
                NullLogger<MediaController>.Instance);

            // Every media action writes a response header on the content path, and ControllerBase
            // throws when there is no HttpContext at all — so one is always attached, exactly as MVC
            // would do.
            Controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        }

        public Guid UserId { get; }

        public InferenceApiDbContext Db { get; }

        public Project Project { get; }

        public Mock<IFileStorageService> Storage { get; } = new();

        /// <summary>The object store the content routes stream from; serves whatever <see cref="Storage"/> serves.</summary>
        public Mock<IAmazonS3> S3 { get; }

        public Mock<IPublishEndpoint> Publisher { get; } = new();

        public MediaController Controller { get; }

        public List<MediaDerivativesRequested> Published { get; } = [];

        public Project NewForeignProject()
        {
            Project foreign = NewProject(Guid.NewGuid());
            Db.Projects.Add(foreign);
            Db.SaveChanges();
            return foreign;
        }

        public ProjectFile AddFile(
            string fileName,
            string mimeType,
            long sizeBytes = 1024,
            string category = "userFiles",
            string? storageKey = null,
            DateTime? uploadedAt = null,
            Guid? projectId = null,
            Guid? fileId = null,
            CsamScanStatus csam = CsamScanStatus.Clean)
        {
            Guid owningProject = projectId ?? Project.Id;
            ProjectFile file = new()
            {
                Id = fileId ?? Guid.NewGuid(),
                ProjectId = owningProject,
                OriginalFileName = fileName,
                StorageKey = storageKey ?? $"projects/{owningProject}/userFiles/{Guid.NewGuid():N}/{fileName}",
                StorageBucket = "reelforge",
                Category = category,
                MimeType = mimeType,
                SizeBytes = sizeBytes,
                UploadedAt = uploadedAt ?? DateTime.UtcNow,
                CsamStatus = csam
            };

            Db.ProjectFiles.Add(file);
            Db.SaveChanges();
            return file;
        }

        public WorkflowStepResult AddRender(
            string storageKey,
            DateTime? completedAt = null,
            Guid? projectId = null,
            string? artifactStorageKey = null,
            StepType? stepType = null)
        {
            Guid owningProject = projectId ?? Project.Id;
            WorkflowExecution execution = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                ProjectId = owningProject,
                Status = ExecutionStatus.Passed
            };

            // The step row carries the type the probe fallback filters on: a result with an
            // artifact is a VideoAnalyze step unless the test says otherwise (T-107).
            WorkflowStep step = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = execution.WorkflowDefinitionId,
                AgentDefinitionId = Guid.NewGuid(),
                StepOrder = 1,
                StepType = stepType ?? (artifactStorageKey is null ? StepType.VideoCompile : StepType.VideoAnalyze)
            };

            WorkflowStepResult result = new()
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execution.Id,
                WorkflowStepId = step.Id,
                ExecutedAt = completedAt ?? DateTime.UtcNow,
                CompletedAt = completedAt,
                OutputStorageKey = storageKey,
                ArtifactStorageKey = artifactStorageKey,
                Status = StepStatus.Completed
            };

            Db.WorkflowExecutions.Add(execution);
            Db.WorkflowSteps.Add(step);
            Db.WorkflowStepResults.Add(result);
            Db.SaveChanges();
            return result;
        }

        public MediaDerivative AddDerivative(
            MediaSourceKind sourceKind,
            Guid sourceId,
            MediaDerivativeKind kind = MediaDerivativeKind.Proxy,
            string? storageKey = null,
            Guid? projectId = null,
            string? mimeType = "video/mp4",
            double? durationSec = 5.5,
            int? width = 640,
            int? height = 360,
            long sizeBytes = 4096,
            string? indexStorageKey = null)
        {
            Guid owningProject = projectId ?? Project.Id;
            string suffix = Guid.NewGuid().ToString("N");

            MediaDerivative derivative = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = owningProject,
                SourceKind = sourceKind,
                SourceProjectFileId = sourceKind == MediaSourceKind.ProjectFile ? sourceId : null,
                SourceStepResultId = sourceKind == MediaSourceKind.StepResult ? sourceId : null,
                SourceStorageKey = $"projects/{owningProject}/userFiles/source-{suffix}",
                SourceETag = $"\"etag-{suffix}\"",
                ParamsHash = $"params-{suffix}",
                CacheKey = $"cache-{suffix}",
                BuilderVersion = 1,
                Kind = kind,
                // The leaf carries a real container extension, as the engine's own artifacts do
                // (a proxy is uploaded as proxy-<hash>.mp4 with content type video/mp4). The serving
                // policy only answers a derivative inline when the stored MIME type and the file
                // name's extension agree, so a fixture whose leaf and MIME disagree would describe a
                // row the writer never produces.
                StorageKey = storageKey
                    ?? $"projects/{owningProject}/derivatives/{suffix}/{kind}{MediaExtensionFor(mimeType)}",
                StorageBucket = "reelforge",
                // T-106's nullable column. The shipped writer DOES persist it on a filmstrip row
                // (MediaDerivativeDispatcher.ResolveIndexStorageKey, commit 45c3b6a9), but this
                // fixture still takes it as an argument rather than defaulting it, because a null
                // pointer is a shape the writer also produces — ResolveIndexStorageKey returns null
                // when the produced artifact is not the leaf FilmstripIndex predicts — and the
                // reader's behaviour for an unbound row has to stay testable. No test infers a
                // density from a sheet count or from a duration.
                IndexStorageKey = indexStorageKey,
                MimeType = mimeType,
                SizeBytes = sizeBytes,
                DurationSec = durationSec,
                Width = width,
                Height = height,
                CreatedAt = DateTime.UtcNow
            };

            Db.MediaDerivatives.Add(derivative);
            Db.SaveChanges();
            return derivative;
        }

        /// <summary>
        /// The extension an artifact of this content type is uploaded with, mirroring the engine's own
        /// leaves (proxy-<c>hash</c>.mp4, sheet <c>.jpg</c>, index <c>.json</c>). A type the engine
        /// never produces inline keeps the neutral <c>.bin</c>.
        /// </summary>
        private static string MediaExtensionFor(string? mimeType) => mimeType switch
        {
            "video/mp4" or "video/x-m4v" => ".mp4",
            "video/webm" => ".webm",
            "video/quicktime" => ".mov",
            "audio/mpeg" => ".mp3",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/mp4" => ".m4a",
            "audio/ogg" => ".ogg",
            "audio/aac" => ".aac",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => ".bin",
        };

        /// <summary>
        /// Adds a bin. <paramref name="deletedAt"/> writes the soft-delete marker directly, which is
        /// how a bin deleted by an earlier request is represented.
        /// </summary>
        public MediaBin AddBin(
            string name = "Bin",
            Guid? binId = null,
            Guid? projectId = null,
            Guid? parentBinId = null,
            int sortOrder = 0,
            DateTime? deletedAt = null)
        {
            Guid owningProject = projectId ?? Project.Id;

            MediaBin bin = new()
            {
                Id = binId ?? Guid.NewGuid(),
                ProjectId = owningProject,
                Name = name,
                ParentBinId = parentBinId,
                SortOrder = sortOrder,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                DeletedAt = deletedAt
            };

            Db.MediaBins.Add(bin);
            Db.SaveChanges();
            return bin;
        }

        /// <summary>
        /// Pins a source into a bin. <paramref name="itemId"/> is settable so a test can order two
        /// pins deterministically instead of depending on a generated Guid's collision-free order.
        /// </summary>
        public MediaBinItem AddBinItem(
            Guid binId,
            MediaSourceKind sourceKind,
            Guid sourceId,
            int sortOrder = 0,
            Guid? itemId = null)
        {
            MediaBinItem item = new()
            {
                Id = itemId ?? Guid.NewGuid(),
                BinId = binId,
                SourceKind = sourceKind,
                SourceProjectFileId = sourceKind == MediaSourceKind.ProjectFile ? sourceId : null,
                SourceStepResultId = sourceKind == MediaSourceKind.StepResult ? sourceId : null,
                SortOrder = sortOrder,
                CreatedAt = DateTime.UtcNow
            };

            Db.MediaBinItems.Add(item);
            Db.SaveChanges();
            return item;
        }

        /// <summary>
        /// Serves <paramref name="json"/> for one object key. A fresh stream per call, because the
        /// controller consumes (and disposes) what it is handed.
        /// </summary>
        public void SetupObject(string storageKey, string json) =>
            Storage
                .Setup(s => s.DownloadAsync(Project.Id, storageKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(json)));

        /// <summary>Stands in for the step result that a <c>VideoAnalyze</c> step wrote.</summary>
        public void SetupAnalysisArtifact(string artifactKey, params VideoAnalysisSourceInfo[] sources)
        {
            VideoAnalysisArtifact artifact = new(
                Version: 2,
                Media: sources.Length > 0 ? sources[0].Media : new VideoAnalysisMedia(1, 30, 1, 1920, 1080),
                Shots: [],
                SilenceSpans: [],
                Segments: [],
                Words: [],
                OfferedIds: [],
                Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
                Sources: sources);

            string json = JsonSerializer.Serialize(artifact, ArtifactJson);

            Storage
                .Setup(s => s.DownloadAsync(Project.Id, artifactKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(json)));
        }

        public void Dispose() => Db.Dispose();
    }

    private static Project NewProject(Guid ownerId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Media Project",
        OwnerId = ownerId,
        OrganizationId = TestTenancy.OrgOf(ownerId),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static VideoAnalysisMedia Media(double duration = 12.5, int fpsNum = 30, int fpsDen = 1, int width = 1920, int height = 1080) =>
        new(duration, fpsNum, fpsDen, width, height);

    /// <summary>
    /// A probed <see cref="VideoAnalysisMedia"/> carrying all six T-105 fields, so a test can pin each
    /// one's mapping without positional noise.
    /// </summary>
    private static VideoAnalysisMedia ProbedMedia(
        string? videoCodec = "h264",
        string? audioCodec = "aac",
        string? container = "mov,mp4,m4a,3gp,3g2,mj2",
        int? audioChannels = 2,
        int? audioSampleRateHz = 48000,
        long? bitrateBps = 8_500_000) =>
        new(12.5, 30, 1, 1920, 1080, videoCodec, audioCodec, container, audioChannels, audioSampleRateHz, bitrateBps);

    private static List<MediaPoolItemResponse> PoolOf(ActionResult<List<MediaPoolItemResponse>> result) =>
        ((OkObjectResult)result.Result!).Value.Should().BeOfType<List<MediaPoolItemResponse>>().Subject;

    /// <summary>A fresh <c>VideoAnalyze</c> artifact key under the prefix the pool only ever reads from.</summary>
    private static string ArtifactKey(Fixture fx) =>
        $"projects/{fx.Project.Id:D}/agentFiles/video-analysis/{Guid.NewGuid():D}/step-1-analysis.json";

    private static MediaFilmstripDocument FilmstripOf(ActionResult<MediaFilmstripResponse> result) =>
        ((OkObjectResult)result.Result!).Value.Should().BeOfType<MediaFilmstripResponse>().Subject.Filmstrip;

    private static List<MediaDerivativeResponse> DerivativesOf(ActionResult<List<MediaDerivativeResponse>> result) =>
        ((OkObjectResult)result.Result!).Value.Should().BeOfType<List<MediaDerivativeResponse>>().Subject;

    // =============================================================================================
    // 404 — never 403. The existence-oracle boundary.
    // =============================================================================================

    [Fact]
    public async Task ListMedia_ReturnsNotFound_WhenProjectDoesNotExist()
    {
        using Fixture fx = new();

        ActionResult<List<MediaPoolItemResponse>> result =
            await fx.Controller.ListMedia(Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ListMedia_ReturnsNotFound_NotForbid_WhenProjectBelongsToAnotherUser()
    {
        using Fixture fx = new();
        Project foreign = fx.NewForeignProject();

        ActionResult<List<MediaPoolItemResponse>> result =
            await fx.Controller.ListMedia(foreign.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        result.Result.Should().NotBeOfType<ForbidResult>();
    }

    [Fact]
    public async Task ListDerivatives_ReturnsNotFound_NotForbid_WhenProjectBelongsToAnotherUser()
    {
        using Fixture fx = new();
        Project foreign = fx.NewForeignProject();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4", projectId: foreign.Id);

        ActionResult<List<MediaDerivativeResponse>> result =
            await fx.Controller.ListDerivatives(foreign.Id, $"file:{file.Id:D}", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        result.Result.Should().NotBeOfType<ForbidResult>();
    }

    [Fact]
    public async Task ListDerivatives_ReturnsNotFound_WhenMediaRefIsNotInThisProject()
    {
        using Fixture fx = new();

        ActionResult<List<MediaDerivativeResponse>> result =
            await fx.Controller.ListDerivatives(fx.Project.Id, $"file:{Guid.NewGuid():D}", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        result.Result.Should().NotBeOfType<ForbidResult>();
    }

    [Fact]
    public async Task ListDerivatives_ReturnsNotFound_WhenMediaRefIsAnotherProjectsFile()
    {
        using Fixture fx = new();
        Project foreign = fx.NewForeignProject();
        ProjectFile foreignFile = fx.AddFile("clip.mp4", "video/mp4", projectId: foreign.Id);

        // The SAME id, asked for through the wrong project: the answer must not differ from an id
        // that does not exist at all.
        ActionResult<List<MediaDerivativeResponse>> result = await fx.Controller.ListDerivatives(
            fx.Project.Id,
            $"file:{foreignFile.Id:D}",
            CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ListDerivatives_ReturnsNotFound_WhenMediaRefHasNoParseableGuid()
    {
        using Fixture fx = new();

        // The route constraint accepts 36 characters of [0-9a-fA-F-], which this is — so the action
        // really is reached and really does have to reject it.
        ActionResult<List<MediaDerivativeResponse>> result = await fx.Controller.ListDerivatives(
            fx.Project.Id,
            $"file:{new string('-', 36)}",
            CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetDerivativeContent_ReturnsNotFound_NotForbid_WhenProjectBelongsToAnotherUser()
    {
        using Fixture fx = new();
        Project foreign = fx.NewForeignProject();
        ProjectFile foreignFile = fx.AddFile("clip.mp4", "video/mp4", projectId: foreign.Id);
        MediaDerivative derivative = fx.AddDerivative(MediaSourceKind.ProjectFile, foreignFile.Id, projectId: foreign.Id);

        IActionResult result = await fx.Controller.GetDerivativeContent(
            foreign.Id,
            derivative.Id,
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        result.Should().NotBeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetDerivativeContent_ReturnsNotFound_WhenDerivativeBelongsToAnotherProject()
    {
        using Fixture fx = new();
        Project foreign = fx.NewForeignProject();
        ProjectFile foreignFile = fx.AddFile("clip.mp4", "video/mp4", projectId: foreign.Id);
        MediaDerivative derivative = fx.AddDerivative(MediaSourceKind.ProjectFile, foreignFile.Id, projectId: foreign.Id);

        IActionResult result = await fx.Controller.GetDerivativeContent(
            fx.Project.Id,
            derivative.Id,
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        fx.Storage.Verify(
            s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetDerivativeContent_ReturnsNotFound_WhenDerivativeDoesNotExist()
    {
        using Fixture fx = new();

        IActionResult result = await fx.Controller.GetDerivativeContent(
            fx.Project.Id,
            Guid.NewGuid(),
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetDerivativeContent_ReturnsNotFound_WhenTheStoredKeyEscapesTheProjectPrefix()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        // A row whose key points at another project's object — a corrupted row, an imported one, or
        // an attack. It is a 404 and the object store is never asked.
        MediaDerivative derivative = fx.AddDerivative(
            MediaSourceKind.ProjectFile,
            file.Id,
            storageKey: $"projects/{Guid.NewGuid():D}/derivatives/elsewhere.bin");

        IActionResult result = await fx.Controller.GetDerivativeContent(
            fx.Project.Id,
            derivative.Id,
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        fx.Storage.Verify(
            s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(CsamScanStatus.Pending)]
    [InlineData(CsamScanStatus.Scanning)]
    public async Task RequestDerivatives_PublishesNothing_WhileTheCsamVerdictIsUnsettled(CsamScanStatus status)
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4", csam: status);

        IActionResult result = await fx.Controller.RequestDerivatives(
            fx.Project.Id, new RequestMediaDerivativesRequest($"file:{file.Id:D}"), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        fx.Published.Should().BeEmpty("no derivative is made before the CSAM verdict");
    }

    [Fact]
    public async Task RequestDerivatives_ReturnsNotFound_NotForbid_WhenProjectBelongsToAnotherUser()
    {
        using Fixture fx = new();
        Project foreign = fx.NewForeignProject();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4", projectId: foreign.Id);

        IActionResult result = await fx.Controller.RequestDerivatives(
            foreign.Id,
            new RequestMediaDerivativesRequest($"file:{file.Id:D}"),
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        result.Should().NotBeOfType<ForbidResult>();
        fx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestDerivatives_ReturnsNotFound_WhenMediaRefIsNotInThisProject()
    {
        using Fixture fx = new();

        IActionResult result = await fx.Controller.RequestDerivatives(
            fx.Project.Id,
            new RequestMediaDerivativesRequest($"step_result:{Guid.NewGuid():D}"),
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        fx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestDerivatives_ReturnsNotFound_WhenTheReferenceIsNotAMediaSource()
    {
        using Fixture fx = new();
        ProjectFile textFile = fx.AddFile("notes.md", "text/markdown");

        IActionResult result = await fx.Controller.RequestDerivatives(
            fx.Project.Id,
            new RequestMediaDerivativesRequest($"file:{textFile.Id:D}"),
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        fx.Published.Should().BeEmpty();
    }

    // =============================================================================================
    // The pool
    // =============================================================================================

    [Fact]
    public async Task ListMedia_ListsMediaProjectFiles_AndNotOthers()
    {
        using Fixture fx = new();
        fx.AddFile("clip.mp4", "video/mp4", sizeBytes: 11, uploadedAt: new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        fx.AddFile("music.mp3", "audio/mpeg", sizeBytes: 22, uploadedAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        fx.AddFile("poster.png", "image/png", sizeBytes: 33, uploadedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        fx.AddFile("notes.md", "text/markdown", uploadedAt: new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc));

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        pool.Select(i => i.FileName).Should().Equal("clip.mp4", "music.mp3", "poster.png");
        pool.Select(i => i.Kind).Should().Equal(MediaKind.Video, MediaKind.Audio, MediaKind.Image);
        pool.Select(i => i.SizeBytes).Should().Equal(11, 22, 33);
    }

    [Fact]
    public async Task ListMedia_ListsUnregisteredRenders_AsStepResultRefs()
    {
        using Fixture fx = new();
        string renderKey = $"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4";
        WorkflowStepResult render = fx.AddRender(renderKey, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        MediaPoolItemResponse item = pool.Should().ContainSingle().Subject;
        item.MediaRef.Should().Be($"step_result:{render.Id:D}");
        item.Kind.Should().Be(MediaKind.Video);
        item.FileName.Should().Be("render.mp4");
        item.MimeType.Should().Be("video/mp4");
        item.Category.Should().Be(MediaCategory.OutputFiles);
    }

    [Fact]
    public async Task ListMedia_DoesNotListARenderTwice_WhenItIsAlsoRegisteredAsAProjectFile()
    {
        using Fixture fx = new();
        string renderKey = $"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4";
        fx.AddRender(renderKey);
        ProjectFile registered = fx.AddFile("render.mp4", "video/mp4", category: "outputFiles", storageKey: renderKey);

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        MediaPoolItemResponse item = pool.Should().ContainSingle().Subject;
        item.MediaRef.Should().Be($"file:{registered.Id:D}");
    }

    [Fact]
    public async Task ListMedia_ExcludesAnotherProjectsFilesAndRenders()
    {
        using Fixture fx = new();
        Project foreign = fx.NewForeignProject();
        fx.AddFile("theirs.mp4", "video/mp4", projectId: foreign.Id);
        fx.AddRender($"projects/{foreign.Id:D}/outputFiles/{Guid.NewGuid():N}/theirs.mp4", projectId: foreign.Id);
        fx.AddFile("mine.mp4", "video/mp4");

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        pool.Should().ContainSingle().Subject.FileName.Should().Be("mine.mp4");
    }

    [Fact]
    public async Task ListMedia_ExcludesANonMediaObjectUnderOutputFiles()
    {
        using Fixture fx = new();
        fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/edl.json");

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        pool.Should().BeEmpty();
    }

    [Fact]
    public async Task ListMedia_OrdersNewestFirst()
    {
        using Fixture fx = new();
        fx.AddFile("old.mp4", "video/mp4", uploadedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        fx.AddRender(
            $"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/new.mp4",
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        pool.Select(i => i.FileName).Should().Equal("new.mp4", "old.mp4");
    }

    [Fact]
    public async Task ListMedia_CapsTheUnionAtThePoolCap()
    {
        using Fixture fx = new();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // More sources than the cap on BOTH sides, so the cap has to be applied to the union rather
        // than to each query independently. Renders are offset by half a second so the two sides
        // never tie on timestamp — the order under test is the union's, not a tie-break's.
        for (int i = 0; i < MediaController.PoolCap + 20; i++)
        {
            fx.AddFile($"file-{i:D4}.mp4", "video/mp4", uploadedAt: start.AddSeconds(i));
            fx.AddRender(
                $"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render-{i:D4}.mp4",
                start.AddSeconds(i).AddMilliseconds(500));
        }

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        pool.Should().HaveCount(MediaController.PoolCap);
        // Newest first: the very last render added is the newest source in the project.
        pool[0].FileName.Should().Be($"render-{MediaController.PoolCap + 19:D4}.mp4");
    }

    // =============================================================================================
    // Probe metadata — read from the newest VideoAnalyze artifact, never invented
    // =============================================================================================

    [Fact]
    public async Task ListMedia_LeavesProbeNull_WhenNoAnalysisArtifactExists()
    {
        using Fixture fx = new();
        fx.AddFile("clip.mp4", "video/mp4");

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        pool.Should().ContainSingle().Subject.Probe.Should().BeNull();
    }

    [Fact]
    public async Task ListMedia_ReadsProbeFromTheAnalysisArtifactSources()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        string artifactKey = $"projects/{fx.Project.Id:D}/agentFiles/video-analysis/{Guid.NewGuid():D}/step-1-analysis.json";
        fx.AddRender(
            $"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4",
            completedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            artifactStorageKey: artifactKey);
        fx.SetupAnalysisArtifact(artifactKey, new VideoAnalysisSourceInfo(0, file.StorageKey, Media(12.5, 30000, 1001, 1920, 1080)));

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        MediaPoolItemResponse item = pool.Single(i => i.FileName == "clip.mp4");
        item.Probe.Should().NotBeNull();
        item.Probe!.DurationSec.Should().Be(12.5);
        item.Probe.FpsNum.Should().Be(30000);
        item.Probe.FpsDen.Should().Be(1001);
        item.Probe.Width.Should().Be(1920);
        item.Probe.Height.Should().Be(1080);
    }

    [Fact]
    public async Task ListMedia_LeavesProbeNull_ForASourceTheArtifactDoesNotMention()
    {
        using Fixture fx = new();
        ProjectFile analysed = fx.AddFile("analysed.mp4", "video/mp4");
        fx.AddFile("never-analysed.mp4", "video/mp4");
        string artifactKey = $"projects/{fx.Project.Id:D}/agentFiles/video-analysis/{Guid.NewGuid():D}/step-1-analysis.json";
        fx.AddRender(
            $"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4",
            artifactStorageKey: artifactKey);
        fx.SetupAnalysisArtifact(artifactKey, new VideoAnalysisSourceInfo(0, analysed.StorageKey, Media()));

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        pool.Single(i => i.FileName == "analysed.mp4").Probe.Should().NotBeNull();
        pool.Single(i => i.FileName == "never-analysed.mp4").Probe.Should().BeNull();
    }

    [Fact]
    public async Task ListMedia_ReadsProbeFromTheNewestAnalysisArtifactOnly()
    {
        using Fixture fx = new();
        ProjectFile older = fx.AddFile("older.mp4", "video/mp4");
        ProjectFile newer = fx.AddFile("newer.mp4", "video/mp4");

        string olderKey = $"projects/{fx.Project.Id:D}/agentFiles/video-analysis/{Guid.NewGuid():D}/step-1-analysis.json";
        string newerKey = $"projects/{fx.Project.Id:D}/agentFiles/video-analysis/{Guid.NewGuid():D}/step-1-analysis.json";
        fx.AddRender(
            $"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/a.mp4",
            completedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            artifactStorageKey: olderKey);
        fx.AddRender(
            $"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/b.mp4",
            completedAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            artifactStorageKey: newerKey);

        fx.SetupAnalysisArtifact(olderKey, new VideoAnalysisSourceInfo(0, older.StorageKey, Media(duration: 1)));
        fx.SetupAnalysisArtifact(newerKey, new VideoAnalysisSourceInfo(0, newer.StorageKey, Media(duration: 2)));

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        pool.Single(i => i.FileName == "newer.mp4").Probe!.DurationSec.Should().Be(2);
        // Only the newest artifact is consulted — a source only an older artifact probed reads as
        // unprobed rather than wrong.
        pool.Single(i => i.FileName == "older.mp4").Probe.Should().BeNull();
    }

    [Fact]
    public async Task ListMedia_ReadsProbeFromTheVideoAnalyzeArtifact_NotANewerCompileEdl()
    {
        // T-107 (QA #323 info): a compile step's EDL lives under the same video-analysis/ prefix and
        // is written after the analysis; it has no sources[], so it must never be the one read.
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        Guid executionId = Guid.NewGuid();
        string analysisKey = $"projects/{fx.Project.Id:D}/agentFiles/video-analysis/{executionId:D}/step-1-analysis.json";
        string edlKey = $"projects/{fx.Project.Id:D}/agentFiles/video-analysis/{executionId:D}/step-2-edl.json";
        fx.AddRender(
            $"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/a.mp4",
            completedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            artifactStorageKey: analysisKey);
        fx.AddRender(
            $"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/b.mp4",
            completedAt: new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc),
            artifactStorageKey: edlKey,
            stepType: StepType.VideoCompile);
        fx.SetupAnalysisArtifact(analysisKey, new VideoAnalysisSourceInfo(0, file.StorageKey, Media(duration: 7)));
        fx.Storage
            .Setup(s => s.DownloadAsync(fx.Project.Id, edlKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("{\"version\":1,\"decisions\":[]}")));

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        pool.Single(i => i.FileName == "clip.mp4").Probe!.DurationSec.Should().Be(7);
    }

    [Fact]
    public async Task ListMedia_LeavesProbeNull_WhenTheArtifactCannotBeRead()
    {
        using Fixture fx = new();
        fx.AddFile("clip.mp4", "video/mp4");
        string artifactKey = $"projects/{fx.Project.Id:D}/agentFiles/video-analysis/{Guid.NewGuid():D}/step-1-analysis.json";
        fx.AddRender(
            $"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4",
            artifactStorageKey: artifactKey);

        fx.Storage
            .Setup(s => s.DownloadAsync(fx.Project.Id, artifactKey, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("gone") { StatusCode = HttpStatusCode.NotFound });

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        pool.Should().HaveCount(2);
        pool.Should().OnlyContain(i => i.Probe == null);
    }

    // =============================================================================================
    // Probe metadata, part two: the six T-105 fields
    // =============================================================================================

    [Fact]
    public async Task ListMedia_MapsAllSixProbeFields_FromTheArtifact()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        string artifactKey = ArtifactKey(fx);
        fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4", artifactStorageKey: artifactKey);
        fx.SetupAnalysisArtifact(artifactKey, new VideoAnalysisSourceInfo(0, file.StorageKey, ProbedMedia()));

        MediaPoolItemResponse item = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None))
            .Single(i => i.FileName == "clip.mp4");

        item.Probe.Should().NotBeNull();
        MediaProbeResponse probe = item.Probe!;

        // The five original fields keep their exact names, order and meaning.
        probe.DurationSec.Should().Be(12.5);
        probe.FpsNum.Should().Be(30);
        probe.FpsDen.Should().Be(1);
        probe.Width.Should().Be(1920);
        probe.Height.Should().Be(1080);

        // The six appended ones, mapped one-for-one.
        probe.VideoCodec.Should().Be("h264");
        probe.AudioCodec.Should().Be("aac");
        probe.Container.Should().Be("mov,mp4,m4a,3gp,3g2,mj2");
        probe.AudioChannels.Should().Be(2);
        probe.AudioSampleRateHz.Should().Be(48000);
        probe.BitrateBps.Should().Be(8_500_000);
    }

    [Fact]
    public async Task ListMedia_ReportsTheSixProbeFieldsAsNull_OnAnArtifactThatPredatesThem()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        string artifactKey = ArtifactKey(fx);
        fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4", artifactStorageKey: artifactKey);

        // Constructed through the five-argument form, which is exactly what an artifact persisted
        // before T-105 deserializes to: the six parameters are simply absent from its JSON.
        fx.SetupAnalysisArtifact(artifactKey, new VideoAnalysisSourceInfo(0, file.StorageKey, Media()));

        MediaPoolItemResponse item = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None))
            .Single(i => i.FileName == "clip.mp4");

        item.Probe.Should().NotBeNull();
        MediaProbeResponse probe = item.Probe!;

        probe.VideoCodec.Should().BeNull();
        probe.AudioCodec.Should().BeNull();
        probe.Container.Should().BeNull();
        probe.AudioChannels.Should().BeNull();
        probe.AudioSampleRateHz.Should().BeNull();
        probe.BitrateBps.Should().BeNull();
    }

    [Fact]
    public async Task ListMedia_ReportsABlankProbeValueAsNull_AndNeverAsAnEmptyString()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        string artifactKey = ArtifactKey(fx);
        fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4", artifactStorageKey: artifactKey);

        // An artifact whose codec/container strings are blank: no codec is named "" and no container
        // is named "  ", so the contract reports the absence it is rather than passing a value the
        // client would have to special-case.
        fx.SetupAnalysisArtifact(
            artifactKey,
            new VideoAnalysisSourceInfo(0, file.StorageKey, ProbedMedia(videoCodec: "", audioCodec: "   ", container: "")));

        MediaPoolItemResponse item = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None))
            .Single(i => i.FileName == "clip.mp4");

        item.Probe.Should().NotBeNull();
        MediaProbeResponse probe = item.Probe!;

        probe.VideoCodec.Should().BeNull();
        probe.AudioCodec.Should().BeNull();
        probe.Container.Should().BeNull();

        string probeJson = JsonSerializer.Serialize(probe, WireJson);
        probeJson.Should().NotContain("\"videoCodec\":\"\"");
        probeJson.Should().NotContain("\"container\":\"\"");
        // And never a zero in place of an unknown count or rate.
        probeJson.Should().NotContain("\"audioChannels\":0");
        probeJson.Should().NotContain("\"bitrateBps\":0");
    }

    // =============================================================================================
    // Probe at ingest: the probe the derivative run stored (W0.4, P2-META-01)
    // =============================================================================================

    private static MediaDerivative StoreProbe(Fixture fx, ProjectFile file, MediaSourceProbe probe, Guid? projectId = null)
    {
        MediaDerivative row = fx.AddDerivative(MediaSourceKind.ProjectFile, file.Id, projectId: projectId);
        row.SourceStorageKey = file.StorageKey;
        row.SourceProbeJson = probe.ToJson();
        fx.Db.SaveChanges();
        return row;
    }

    private static MediaSourceProbe StoredProbe(double durationSec = 12.0, bool hasAudio = true) =>
        new(durationSec, 30, 1, 1280, 720, "h264", hasAudio ? "aac" : null, "mov,mp4,m4a,3gp,3g2,mj2",
            hasAudio ? 2 : null, hasAudio ? 48000 : null, 2_000_000, hasAudio);

    [Fact]
    public async Task ListMedia_ReadsTheProbeTheDerivativeRunStored_WithNoAnalysisArtifact()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        StoreProbe(fx, file, StoredProbe(durationSec: 12.0));

        MediaProbeResponse probe = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None))
            .Single().Probe!;

        probe.Should().NotBeNull("the upload's derivative run probed this file; no analysis run is needed");
        probe.DurationSec.Should().Be(12.0);
        probe.FpsNum.Should().Be(30);
        probe.FpsDen.Should().Be(1);
        probe.Width.Should().Be(1280);
        probe.Height.Should().Be(720);
        probe.VideoCodec.Should().Be("h264");
        probe.AudioCodec.Should().Be("aac");
        probe.Container.Should().Be("mov,mp4,m4a,3gp,3g2,mj2");
        probe.AudioChannels.Should().Be(2);
        probe.AudioSampleRateHz.Should().Be(48000);
        probe.BitrateBps.Should().Be(2_000_000);
        probe.HasAudio.Should().BeTrue();
    }

    [Fact]
    public async Task ListMedia_PrefersTheStoredProbe_OverTheAnalysisArtifact()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        string artifactKey = ArtifactKey(fx);
        fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4", artifactStorageKey: artifactKey);
        fx.SetupAnalysisArtifact(artifactKey, new VideoAnalysisSourceInfo(0, file.StorageKey, Media(duration: 99)));
        StoreProbe(fx, file, StoredProbe(durationSec: 12.0, hasAudio: false));

        MediaProbeResponse probe = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None))
            .Single(i => i.FileName == "clip.mp4").Probe!;

        probe.DurationSec.Should().Be(12.0, "the stored probe is the file's own measurement; the artifact is the fallback");
        probe.HasAudio.Should().BeFalse();
    }

    [Fact]
    public async Task ListMedia_NeverReadsAStoredProbe_FromAnotherProjectsRow()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        Project foreign = fx.NewForeignProject();
        StoreProbe(fx, file, StoredProbe(), projectId: foreign.Id);

        PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None))
            .Single().Probe.Should().BeNull();
    }

    [Fact]
    public async Task ListMedia_DerivesHasAudio_FromAnArtifactThatProbedCodecs_AndLeavesItNullOnALegacyOne()
    {
        using Fixture fx = new();
        ProjectFile silent = fx.AddFile("silent.mp4", "video/mp4");
        ProjectFile legacy = fx.AddFile("legacy.mp4", "video/mp4");
        string artifactKey = ArtifactKey(fx);
        fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4", artifactStorageKey: artifactKey);
        fx.SetupAnalysisArtifact(
            artifactKey,
            new VideoAnalysisSourceInfo(0, silent.StorageKey, ProbedMedia(audioCodec: null, audioChannels: null, audioSampleRateHz: null)),
            new VideoAnalysisSourceInfo(1, legacy.StorageKey, Media()));

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        pool.Single(i => i.FileName == "silent.mp4").Probe!.HasAudio.Should().BeFalse(
            "the artifact named a video codec and no audio codec: the file was probed and has no sound");
        pool.Single(i => i.FileName == "legacy.mp4").Probe!.HasAudio.Should().BeNull(
            "an artifact that predates codec probing never said either way");
    }

    [Fact]
    public async Task ListMedia_WritesHasAudio_OnTheWire()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        StoreProbe(fx, file, StoredProbe());

        string json = JsonSerializer.Serialize(PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None)), WireJson);

        json.Should().Contain("\"hasAudio\":true");
        json.Should().Contain("\"durationSec\":12");
    }

    // =============================================================================================
    // Bin membership on the pool payload
    // =============================================================================================

    [Fact]
    public async Task ListMedia_CarriesTheBinsHoldingEachSource_AndAnEmptyListWhenUnbinned()
    {
        using Fixture fx = new();

        ProjectFile inTwo = fx.AddFile("in-two.mp4", "video/mp4");
        ProjectFile inOne = fx.AddFile("in-one.mp4", "video/mp4");
        fx.AddFile("unbinned.mp4", "video/mp4");

        // Fixed ids so the expected order is a fact about the contract rather than about Guid
        // generation. The two bins differ in their last byte only, which every plausible Guid
        // ordering agrees on.
        Guid binA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        Guid binB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

        fx.AddBin("A", binId: binA, sortOrder: 0);
        fx.AddBin("B", binId: binB, sortOrder: 1);

        // Deliberately pinned in the REVERSE of the reported order: the contract orders by binId,
        // not by sort order, not by creation order and not by the order the additions happened in.
        MediaBinItem secondPin = fx.AddBinItem(binB, MediaSourceKind.ProjectFile, inTwo.Id, sortOrder: 0);
        MediaBinItem firstPin = fx.AddBinItem(binA, MediaSourceKind.ProjectFile, inTwo.Id, sortOrder: 7);
        MediaBinItem singlePin = fx.AddBinItem(binB, MediaSourceKind.ProjectFile, inOne.Id);

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        pool.Single(i => i.FileName == "in-two.mp4").Bins
            .Should().Equal(
                new MediaBinMembershipResponse(binA, firstPin.Id),
                new MediaBinMembershipResponse(binB, secondPin.Id));

        pool.Single(i => i.FileName == "in-one.mp4").Bins
            .Should().Equal(new MediaBinMembershipResponse(binB, singlePin.Id));

        // Present AND empty — never null, so a client can map over it unconditionally.
        pool.Single(i => i.FileName == "unbinned.mp4").Bins.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task ListMedia_NeverCarriesABinOfAnotherProject_OrADeletedOne()
    {
        using Fixture fx = new();
        ProjectFile mine = fx.AddFile("mine.mp4", "video/mp4");
        Project foreign = fx.NewForeignProject();

        MediaBin live = fx.AddBin("Live");
        MediaBin deleted = fx.AddBin("Deleted", deletedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        MediaBin theirs = fx.AddBin("Theirs", projectId: foreign.Id);

        MediaBinItem kept = fx.AddBinItem(live.Id, MediaSourceKind.ProjectFile, mine.Id);
        fx.AddBinItem(deleted.Id, MediaSourceKind.ProjectFile, mine.Id);
        fx.AddBinItem(theirs.Id, MediaSourceKind.ProjectFile, mine.Id);

        MediaPoolItemResponse item = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None))
            .Should().ContainSingle().Subject;

        item.Bins.Should().Equal(new MediaBinMembershipResponse(live.Id, kept.Id));
    }

    [Fact]
    public async Task ListMedia_ResolvesBinsForAStepResultSource_WithoutCrossingOntoAProjectFileOfTheSameId()
    {
        using Fixture fx = new();
        WorkflowStepResult render = fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4");

        // A project file carrying the SAME Guid as the render: the ids come from different tables, so
        // the kind has to be part of the membership key or one source would show the other's bins.
        fx.AddFile("same-id.mp4", "video/mp4", fileId: render.Id);

        MediaBin bin = fx.AddBin("Bin");
        MediaBinItem renderPin = fx.AddBinItem(bin.Id, MediaSourceKind.StepResult, render.Id);

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        pool.Single(i => i.MediaRef == $"step_result:{render.Id:D}").Bins
            .Should().Equal(new MediaBinMembershipResponse(bin.Id, renderPin.Id));

        pool.Single(i => i.MediaRef == $"file:{render.Id:D}").Bins.Should().BeEmpty();
    }

    [Fact]
    public async Task ListMedia_AlwaysCarriesTheFilmstripUrl_ForEverySourceKind()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        WorkflowStepResult render = fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4");

        List<MediaPoolItemResponse> pool = PoolOf(await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None));

        pool.Single(i => i.MediaRef == $"file:{file.Id:D}").FilmstripUrl
            .Should().Be($"/api/v1/projects/{fx.Project.Id:D}/media/file:{file.Id:D}/filmstrip");

        pool.Single(i => i.MediaRef == $"step_result:{render.Id:D}").FilmstripUrl
            .Should().Be($"/api/v1/projects/{fx.Project.Id:D}/media/step_result:{render.Id:D}/filmstrip");
    }

    /// <summary>
    /// The <c>filmstripUrl</c> a pool item advertises is the route this controller actually serves:
    /// asserted by running the advertised string's own template through the framework's parser, so a
    /// URL that no route would match fails here rather than in the editor.
    /// </summary>
    [Fact]
    public void TheAdvertisedFilmstripUrl_MatchesTheFilmstripRouteConstraint()
    {
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();

        string url = $"/api/v1/projects/{projectId:D}/media/file:{fileId:D}/filmstrip";
        string prefix = $"/api/v1/projects/{projectId:D}/media/";

        url.Should().StartWith(prefix).And.EndWith("/filmstrip");

        string mediaRef = url[prefix.Length..^"/filmstrip".Length];
        AssertMatches(MediaController.FileMediaRefFilmstripRouteTemplate, [mediaRef], ["bins"]);
    }

    // =============================================================================================
    // Nothing leaks: no storage key, no bucket, no object URL
    // =============================================================================================

    [Fact]
    public async Task ListMedia_NeverSendsAStorageKeyBucketOrObjectUrl()
    {
        using Fixture fx = new();
        string secretKey = $"projects/{fx.Project.Id:D}/userFiles/{Guid.NewGuid():N}/clip.mp4";
        fx.AddFile("clip.mp4", "video/mp4", storageKey: secretKey);
        fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4");

        ActionResult<List<MediaPoolItemResponse>> result =
            await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None);

        AssertNoStorageLeak(JsonSerializer.Serialize(PoolOf(result), WireJson), secretKey);
    }

    [Fact]
    public async Task ListDerivatives_NeverSendsAStorageKeyBucketOrObjectUrl()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        MediaDerivative derivative = fx.AddDerivative(MediaSourceKind.ProjectFile, file.Id);

        ActionResult<List<MediaDerivativeResponse>> result =
            await fx.Controller.ListDerivatives(fx.Project.Id, $"file:{file.Id:D}", CancellationToken.None);

        string json = JsonSerializer.Serialize(DerivativesOf(result), WireJson);

        AssertNoStorageLeak(json, derivative.StorageKey);
        // The only URL a row may carry is this API's own content path.
        json.Should().Contain($"/api/v1/projects/{fx.Project.Id:D}/media/derivatives/{derivative.Id:D}/content");
    }

    private static void AssertNoStorageLeak(string json, params string[] forbiddenKeys)
    {
        json.Should().NotContain("storageKey");
        json.Should().NotContain("storageBucket");
        json.Should().NotContain("reelforge");
        json.Should().NotContain("X-Amz");
        json.Should().NotContain("http://");
        json.Should().NotContain("https://");

        foreach (string key in forbiddenKeys)
        {
            json.Should().NotContain(key);
        }
    }

    // =============================================================================================
    // Derivative rows
    // =============================================================================================

    [Fact]
    public async Task ListDerivatives_ReturnsOnlyTheSourcesRows_WithTheFullRowShape()
    {
        using Fixture fx = new();
        ProjectFile wanted = fx.AddFile("wanted.mp4", "video/mp4");
        ProjectFile other = fx.AddFile("other.mp4", "video/mp4");

        MediaDerivative proxy = fx.AddDerivative(
            MediaSourceKind.ProjectFile, wanted.Id, MediaDerivativeKind.Proxy,
            mimeType: "video/mp4", durationSec: 9.5, width: 640, height: 360, sizeBytes: 111);
        fx.AddDerivative(MediaSourceKind.ProjectFile, other.Id, MediaDerivativeKind.Thumbnail);

        ActionResult<List<MediaDerivativeResponse>> result =
            await fx.Controller.ListDerivatives(fx.Project.Id, $"file:{wanted.Id:D}", CancellationToken.None);

        MediaDerivativeResponse row = DerivativesOf(result).Should().ContainSingle().Subject;
        row.Id.Should().Be(proxy.Id);
        row.Kind.Should().Be(MediaDerivativeKind.Proxy);
        row.MimeType.Should().Be("video/mp4");
        row.SizeBytes.Should().Be(111);
        row.DurationSec.Should().Be(9.5);
        row.Width.Should().Be(640);
        row.Height.Should().Be(360);
    }

    [Fact]
    public async Task ListDerivatives_ReturnsAnEmptyList_WhenNoneExistYet()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        ActionResult<List<MediaDerivativeResponse>> result =
            await fx.Controller.ListDerivatives(fx.Project.Id, $"file:{file.Id:D}", CancellationToken.None);

        DerivativesOf(result).Should().BeEmpty();
    }

    [Fact]
    public async Task ListDerivatives_WorksForAStepResultSource()
    {
        using Fixture fx = new();
        WorkflowStepResult render = fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4");
        fx.AddDerivative(MediaSourceKind.StepResult, render.Id, MediaDerivativeKind.Thumbnail);
        // A project-file row for the same GUID must not be picked up: the reference kind decides.
        MediaDerivative fileRow = fx.AddDerivative(MediaSourceKind.ProjectFile, render.Id, MediaDerivativeKind.Proxy);

        ActionResult<List<MediaDerivativeResponse>> result = await fx.Controller.ListDerivatives(
            fx.Project.Id,
            $"step_result:{render.Id:D}",
            CancellationToken.None);

        DerivativesOf(result).Should().ContainSingle().Subject.Kind.Should().Be(MediaDerivativeKind.Thumbnail);
        fileRow.Should().NotBeNull();
    }

    [Fact]
    public async Task ListDerivatives_SerializesTheKindAsACamelCaseString()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        fx.AddDerivative(MediaSourceKind.ProjectFile, file.Id, MediaDerivativeKind.KeyframeIndex);

        ActionResult<List<MediaDerivativeResponse>> result =
            await fx.Controller.ListDerivatives(fx.Project.Id, $"file:{file.Id:D}", CancellationToken.None);

        string json = JsonSerializer.Serialize(DerivativesOf(result), WireJson);
        json.Should().Contain("\"kind\":\"keyframeIndex\"");
    }

    // =============================================================================================
    // Derivative content
    // =============================================================================================

    [Fact]
    public async Task GetDerivativeContent_WithNoRange_StreamsTheWholeObject_AndAdvertisesRanges()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        MediaDerivative derivative = fx.AddDerivative(MediaSourceKind.ProjectFile, file.Id, mimeType: "video/mp4");

        fx.Storage
            .Setup(s => s.DownloadAsync(fx.Project.Id, derivative.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("proxy-bytes")));

        IActionResult result = await fx.Controller.GetDerivativeContent(
            fx.Project.Id,
            derivative.Id,
            CancellationToken.None);

        FileStreamResult fileResult = result.Should().BeOfType<FileStreamResult>().Subject;
        fileResult.ContentType.Should().Be("video/mp4");
        (await new StreamReader(fileResult.FileStream).ReadToEndAsync()).Should().Be("proxy-bytes");
        fx.Controller.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
        fx.Controller.Response.ContentLength.Should().Be(11);
    }

    /// <summary>
    /// api D4: the proxy answered <c>Range: bytes=0-99</c> with 200 and the whole 19 MB object, so a
    /// video element had no <c>seekable</c> range and WebKit refused to play it.
    /// </summary>
    [Fact]
    public async Task GetDerivativeContent_HonoursARangeRequest_With206AndExactlyThoseBytes()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        MediaDerivative derivative = fx.AddDerivative(MediaSourceKind.ProjectFile, file.Id, mimeType: "video/mp4");

        byte[] whole = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();
        fx.Storage
            .Setup(s => s.DownloadAsync(fx.Project.Id, derivative.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(whole));
        fx.Controller.Request.Headers.Range = "bytes=0-99";

        IActionResult result = await fx.Controller.GetDerivativeContent(fx.Project.Id, derivative.Id, CancellationToken.None);

        FileStreamResult fileResult = result.Should().BeOfType<FileStreamResult>().Subject;
        using MemoryStream sent = new();
        await fileResult.FileStream.CopyToAsync(sent);

        fx.Controller.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        fx.Controller.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
        fx.Controller.Response.Headers.ContentRange.ToString().Should().Be("bytes 0-99/1000");
        fx.Controller.Response.ContentLength.Should().Be(100);
        sent.ToArray().Should().Equal(whole.Take(100));
    }

    [Fact]
    public async Task GetDerivativeContent_AnswersAnUnsatisfiableRangeWith416()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        MediaDerivative derivative = fx.AddDerivative(MediaSourceKind.ProjectFile, file.Id, mimeType: "video/mp4");
        fx.Storage
            .Setup(s => s.DownloadAsync(fx.Project.Id, derivative.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[10]));
        fx.Controller.Request.Headers.Range = "bytes=50-60";

        IActionResult result = await fx.Controller.GetDerivativeContent(fx.Project.Id, derivative.Id, CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status416RangeNotSatisfiable);
        fx.Controller.Response.Headers.ContentRange.ToString().Should().Be("bytes */10");
    }

    [Fact]
    public async Task GetDerivativeContent_ServesAMimeTypeOutsideThePolicyAllowlistAsAnAttachment()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        MediaDerivative derivative = fx.AddDerivative(MediaSourceKind.ProjectFile, file.Id, mimeType: "text/html");
        fx.Storage
            .Setup(s => s.DownloadAsync(fx.Project.Id, derivative.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes("<script>alert(1)</script>")));

        IActionResult result = await fx.Controller.GetDerivativeContent(
            fx.Project.Id, derivative.Id, CancellationToken.None);

        result.Should().BeOfType<FileStreamResult>().Which.ContentType.Should().Be("application/octet-stream");
        fx.Controller.Response.Headers["Content-Disposition"].ToString().Should().StartWith("attachment");
        fx.Controller.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
    }

    [Fact]
    public async Task GetDerivativeContent_ReturnsNotFound_WhenTheObjectIsGoneFromStorage()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        MediaDerivative derivative = fx.AddDerivative(MediaSourceKind.ProjectFile, file.Id);

        fx.Storage
            .Setup(s => s.DownloadAsync(fx.Project.Id, derivative.StorageKey, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound });

        IActionResult result = await fx.Controller.GetDerivativeContent(
            fx.Project.Id,
            derivative.Id,
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // =============================================================================================
    // The explicit derivatives request
    // =============================================================================================

    [Fact]
    public async Task RequestDerivatives_PublishesExactlyOneEvent_AndWritesNoRow()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        fx.Storage
            .Setup(s => s.GetObjectETagAsync(fx.Project.Id, file.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync("\"the-etag\"");

        int rowsBefore = fx.Db.MediaDerivatives.Count();

        IActionResult result = await fx.Controller.RequestDerivatives(
            fx.Project.Id,
            new RequestMediaDerivativesRequest($"file:{file.Id:D}"),
            CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();

        MediaDerivativesRequested published = fx.Published.Should().ContainSingle().Subject;
        published.ProjectId.Should().Be(fx.Project.Id);
        published.SourceRef.Should().Be(new MediaSourceRef(MediaSourceKind.ProjectFile, file.Id));
        published.SourceProjectFileId.Should().Be(file.Id);
        published.SourceStorageKey.Should().Be(file.StorageKey);
        published.SourceEtag.Should().Be("\"the-etag\"");
        published.MimeType.Should().Be("video/mp4");
        published.Kinds.Should().Equal(MediaDerivativeRequests.DefaultKinds);
        published.RequestedBy.Should().Be(MediaDerivativeRequests.RequestedByEditorOpen);
        published.RequestedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        fx.Db.MediaDerivatives.Count().Should().Be(rowsBefore);
        fx.Db.SaveChanges();
        fx.Db.MediaDerivatives.Count().Should().Be(0);
    }

    [Fact]
    public async Task RequestDerivatives_WithACameraProfile_PublishesTheProfilesProxyVariant()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        IActionResult result = await fx.Controller.RequestDerivatives(
            fx.Project.Id,
            new RequestMediaDerivativesRequest($"file:{file.Id:D}", CameraProfile: "sonySLog3"),
            CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        fx.Published.Should().ContainSingle().Subject.ProxyVariant.Should().Be(4);
    }

    [Fact]
    public async Task RequestDerivatives_WithoutACameraProfile_PublishesTheUnchangedPlainRequest()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        await fx.Controller.RequestDerivatives(
            fx.Project.Id, new RequestMediaDerivativesRequest($"file:{file.Id:D}"), CancellationToken.None);

        fx.Published.Should().ContainSingle().Subject.ProxyVariant.Should().Be(0);
    }

    [Fact]
    public async Task RequestDerivatives_RejectsAnUnknownCameraProfile_WithA400()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        IActionResult result = await fx.Controller.RequestDerivatives(
            fx.Project.Id,
            new RequestMediaDerivativesRequest($"file:{file.Id:D}", CameraProfile: "../etc/passwd"),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        fx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestDerivatives_RejectsAnAudioSource_AndAnHdrSource_WithTheStableReasonCode()
    {
        using Fixture fx = new();
        ProjectFile audio = fx.AddFile("song.mp3", "audio/mpeg");
        IActionResult audioResult = await fx.Controller.RequestDerivatives(
            fx.Project.Id,
            new RequestMediaDerivativesRequest($"file:{audio.Id:D}", CameraProfile: "appleLog"),
            CancellationToken.None);
        audioResult.Should().BeOfType<BadRequestObjectResult>();

        ProjectFile hdr = fx.AddFile("hdr.mp4", "video/mp4");
        MediaDerivative row = fx.AddDerivative(MediaSourceKind.ProjectFile, hdr.Id);
        row.SourceStorageKey = hdr.StorageKey;
        row.SourceProbeJson = new MediaSourceProbe(5, 30, 1, 640, 360, "hevc", null, null, null, null, null, false,
            ColorTransfer: "arib-std-b67", Hdr: "hlg").ToJson();
        fx.Db.SaveChanges();

        IActionResult hdrResult = await fx.Controller.RequestDerivatives(
            fx.Project.Id,
            new RequestMediaDerivativesRequest($"file:{hdr.Id:D}", CameraProfile: "appleLog"),
            CancellationToken.None);

        BadRequestObjectResult bad = hdrResult.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("camera_profile_not_applicable_hdr");
        fx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ListDerivatives_ReportsTheCameraProfileOfAConvertedProxyRowOnly()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        MediaDerivative plain = fx.AddDerivative(MediaSourceKind.ProjectFile, file.Id);
        MediaDerivative converted = fx.AddDerivative(MediaSourceKind.ProjectFile, file.Id);
        converted.ParamsJson = "{\"cameraProfile\":\"appleLog\",\"recipe\":\"camera-profile-v1:appleLog\"}";
        fx.Db.SaveChanges();

        ActionResult<List<MediaDerivativeResponse>> result = await fx.Controller.ListDerivatives(
            fx.Project.Id, $"file:{file.Id:D}", CancellationToken.None);

        List<MediaDerivativeResponse> rows = ((OkObjectResult)result.Result!).Value.Should().BeOfType<List<MediaDerivativeResponse>>().Subject;
        rows.Single(r => r.Id == plain.Id).CameraProfile.Should().BeNull();
        rows.Single(r => r.Id == converted.Id).CameraProfile.Should().Be("appleLog");
    }

    [Fact]
    public async Task RequestDerivatives_PublishesAnEmptyEtag_WhenTheHeadFails()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        fx.Storage
            .Setup(s => s.GetObjectETagAsync(fx.Project.Id, file.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        IActionResult result = await fx.Controller.RequestDerivatives(
            fx.Project.Id,
            new RequestMediaDerivativesRequest($"file:{file.Id:D}"),
            CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        fx.Published.Should().ContainSingle().Subject.SourceEtag.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestDerivatives_HonoursExplicitKinds()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        IActionResult result = await fx.Controller.RequestDerivatives(
            fx.Project.Id,
            new RequestMediaDerivativesRequest(
                $"file:{file.Id:D}",
                ["thumbnail", "Proxy", "thumbnail"],
                MediaDerivativeRequests.RequestedByRenderOutput),
            CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();

        MediaDerivativesRequested published = fx.Published.Should().ContainSingle().Subject;
        // Case-insensitive, de-duplicated, order preserved.
        published.Kinds.Should().Equal(MediaDerivativeKind.Thumbnail, MediaDerivativeKind.Proxy);
        published.RequestedBy.Should().Be(MediaDerivativeRequests.RequestedByRenderOutput);
    }

    [Fact]
    public async Task RequestDerivatives_RejectsAnUnknownKind()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        IActionResult result = await fx.Controller.RequestDerivatives(
            fx.Project.Id,
            new RequestMediaDerivativesRequest($"file:{file.Id:D}", ["Spectrogram"]),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        fx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestDerivatives_RejectsAnOrdinalKindName()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        // Enum.TryParse would accept this and resolve it against a C# declaration; the wire format
        // is a name, so "0" is a client error rather than an ambiguous proxy request.
        IActionResult result = await fx.Controller.RequestDerivatives(
            fx.Project.Id,
            new RequestMediaDerivativesRequest($"file:{file.Id:D}", ["0"]),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        fx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestDerivatives_WorksForAStepResult_WithNoSourceProjectFileId()
    {
        using Fixture fx = new();
        WorkflowStepResult render = fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4");

        IActionResult result = await fx.Controller.RequestDerivatives(
            fx.Project.Id,
            new RequestMediaDerivativesRequest($"step_result:{render.Id:D}"),
            CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();

        MediaDerivativesRequested published = fx.Published.Should().ContainSingle().Subject;
        published.SourceRef.Should().Be(new MediaSourceRef(MediaSourceKind.StepResult, render.Id));
        // The whole reason SourceRef exists: an unregistered render has no ProjectFile row to name.
        published.SourceProjectFileId.Should().BeNull();
        published.SourceStorageKey.Should().Be(render.OutputStorageKey);
        published.MimeType.Should().Be("video/mp4");
    }

    [Fact]
    public async Task RequestDerivatives_DoesNotTriggerWorkOnAGet()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        string artifactKey = $"projects/{fx.Project.Id:D}/agentFiles/video-analysis/{Guid.NewGuid():D}/step-1-analysis.json";
        fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4", artifactStorageKey: artifactKey);
        fx.SetupAnalysisArtifact(artifactKey, new VideoAnalysisSourceInfo(0, file.StorageKey, Media()));

        await fx.Controller.ListMedia(fx.Project.Id, CancellationToken.None);
        await fx.Controller.ListDerivatives(fx.Project.Id, $"file:{file.Id:D}", CancellationToken.None);

        // "Lazily on first open" is the POST, never a side effect of reading.
        fx.Published.Should().BeEmpty();
    }

    // =============================================================================================
    // Storage: the best-effort ETag HEAD
    // =============================================================================================

    private static S3FileStorageService CreateStorageService(Mock<IAmazonS3> s3)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" })
            .Build();

        return new S3FileStorageService(s3.Object, configuration, NullLogger<S3FileStorageService>.Instance);
    }

    [Fact]
    public async Task GetObjectETag_ReturnsTheEtagTheStoreReported()
    {
        Mock<IAmazonS3> s3 = new();
        s3.Setup(c => c.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse { ETag = "\"abc123\"" });

        Guid projectId = Guid.NewGuid();
        string? etag = await CreateStorageService(s3).GetObjectETagAsync(
            projectId,
            $"projects/{projectId:D}/userFiles/clip.mp4",
            CancellationToken.None);

        etag.Should().Be("\"abc123\"");
    }

    [Fact]
    public async Task GetObjectETag_ReturnsNull_OnAnyStorageFailure()
    {
        Mock<IAmazonS3> s3 = new();
        s3.Setup(c => c.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("head failed") { StatusCode = HttpStatusCode.InternalServerError });

        Guid projectId = Guid.NewGuid();

        // Never throws: the caller is an upload whose row is already committed.
        string? etag = await CreateStorageService(s3).GetObjectETagAsync(
            projectId,
            $"projects/{projectId:D}/userFiles/clip.mp4",
            CancellationToken.None);

        etag.Should().BeNull();
    }

    [Fact]
    public async Task GetObjectETag_ReturnsNull_ForAnotherProjectsKey_WithoutTouchingTheStore()
    {
        Mock<IAmazonS3> s3 = new();

        string? etag = await CreateStorageService(s3).GetObjectETagAsync(
            Guid.NewGuid(),
            $"projects/{Guid.NewGuid():D}/userFiles/clip.mp4",
            CancellationToken.None);

        etag.Should().BeNull();
        s3.Verify(
            c => c.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // =============================================================================================
    // Route shape
    // =============================================================================================

    /// <summary>
    /// The route templates on <c>ListDerivatives</c> really are regex-constrained, and the regex the
    /// framework compiles from them really does admit a mediaRef and reject <c>bins</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted through ASP.NET's OWN route-template parser and its OWN
    /// <see cref="RegexRouteConstraint"/>, not by re-running a copy of the pattern: a test that
    /// re-implemented the escaping could pass while the route table failed to build — which is
    /// exactly what the first version of this constraint did (<c>[0-9a-fA-F-]</c> was read as a
    /// token named <c>0-9a-fA-F-</c> and the whole application refused to start).
    /// </para>
    /// <para>
    /// The two-step un-escaping mirrors the framework's: MVC's token replacement turns <c>[[</c>
    /// into <c>[</c> before the template reaches the route-pattern parser, and that parser turns
    /// <c>{{</c> into <c>{</c>. The bracket step is applied here because the test starts from the
    /// attribute string; the brace step is the parser's own.
    /// </para>
    /// </remarks>
    [Fact]
    public void MediaRefRoutes_AreRegexConstrained_SoBinsCannotCollide()
    {
        MethodInfo method = typeof(MediaController).GetMethod(nameof(MediaController.ListDerivatives))!;

        string[] templates = method
            .GetCustomAttributes<HttpGetAttribute>()
            .Select(a => a.Template ?? string.Empty)
            .ToArray();

        templates.Should().Equal(
            MediaController.FileMediaRefRouteTemplate,
            MediaController.StepResultMediaRefRouteTemplate);

        string fileGuid = Guid.NewGuid().ToString("D");
        string stepGuid = Guid.NewGuid().ToString("D");

        ConstraintOf(templates[0]).Should().NotBeNull();
        AssertMatches(templates[0], [$"file:{fileGuid}"], [$"step_result:{stepGuid}", "bins", fileGuid]);
        AssertMatches(templates[1], [$"step_result:{stepGuid}"], [$"file:{fileGuid}", "bins", stepGuid]);
    }

    /// <summary>
    /// Runs a route template through the framework's parser and returns the single regex constraint
    /// it produced — or null when the template did not yield exactly one.
    /// </summary>
    private static RegexRouteConstraint? ConstraintOf(string template)
    {
        // The bracket un-escaping MVC's token replacement performs before parsing.
        RoutePattern pattern = RoutePatternFactory.Parse(template.Replace("[[", "[").Replace("]]", "]"));

        RoutePatternParameterPart parameter = pattern.Parameters.Should().ContainSingle().Subject;
        parameter.Name.Should().Be("fileId");

        string content = parameter.ParameterPolicies.Should().ContainSingle().Subject.Content;
        content.Should().StartWith("regex(").And.EndWith(")");

        return new RegexRouteConstraint(content["regex(".Length..^1]);
    }

    private static void AssertMatches(string template, string[] accepted, string[] rejected)
    {
        RegexRouteConstraint constraint = ConstraintOf(template)!;
        RoutePattern pattern = RoutePatternFactory.Parse(template.Replace("[[", "[").Replace("]]", "]"));
        string parameterName = pattern.Parameters[0].Name;

        bool Match(string value) => constraint.Match(
            new DefaultHttpContext(),
            new Mock<IRouter>().Object,
            parameterName,
            new RouteValueDictionary { [parameterName] = value },
            RouteDirection.IncomingRequest);

        foreach (string value in accepted)
        {
            Match(value).Should().BeTrue($"'{value}' is a valid mediaRef for {template}");
        }

        foreach (string value in rejected)
        {
            Match(value).Should().BeFalse($"'{value}' must not match {template}");
        }
    }

    [Theory]
    [InlineData("bins")]
    [InlineData("not-a-ref")]
    [InlineData("bin:00000000-0000-4000-8000-000000000001")]
    [InlineData("FILE:00000000-0000-4000-8000-000000000001")]
    [InlineData("file:00000000-0000-4000-8000-00000000000")]
    [InlineData("file:00000000-0000-4000-8000-0000000000011")]
    [InlineData("file:00000000-0000-4000-8000-000000000001/derivatives")]
    [InlineData("")]
    public async Task RequestDerivatives_RejectsEveryMalformedMediaRef(string mediaRef)
    {
        // The same accepted/rejected token set the route constraints above are checked against, here
        // through the action itself: a body-supplied mediaRef is guarded by this tokenizer alone, so
        // the two guards are asserted to agree rather than assumed to.
        using Fixture fx = new();

        IActionResult result = await fx.Controller.RequestDerivatives(
            fx.Project.Id,
            new RequestMediaDerivativesRequest(mediaRef),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        fx.Published.Should().BeEmpty();
    }

    // =============================================================================================
    // Event contract
    // =============================================================================================

    [Fact]
    public void MediaDerivativesRequested_CarriesAnOpaqueRef_NotARedeclaredEnum()
    {
        MediaSourceRef reference = new(MediaSourceKind.StepResult, Guid.NewGuid());

        reference.Kind.Should().Be(MediaSourceKind.StepResult);

        // The source discriminator has exactly ONE declaration in the solution: a second enum named
        // MediaSourceKind anywhere else would make every file importing both namespaces fail to
        // compile on an ambiguous reference.
        IEnumerable<Type> declarations = typeof(MediaSourceKind).Assembly
            .GetTypes()
            .Where(t => t.Name == nameof(MediaSourceKind) && t.IsEnum);

        declarations.Should().ContainSingle().Which.Should().Be(typeof(MediaSourceKind));
        typeof(MediaDerivativesRequested).Assembly.Should().BeSameAs(typeof(MediaSourceKind).Assembly);
    }

    // =============================================================================================
    // The filmstrip index: GET /media/{mediaRef}/filmstrip
    // =============================================================================================

    /// <summary>
    /// One seeded density: the object key its sidecar was written to, the shared directory every
    /// sheet of that density was uploaded into, the sheet leaves the sidecar names, and the ONE row
    /// that owns it (null when the fixture deliberately seeds no row).
    /// </summary>
    private sealed record Density(string IndexKey, string Directory, string[] Leaves, MediaDerivative? Row);

    /// <summary>
    /// Writes a real <c>FilmstripIndex.Build</c> sidecar, serves every sheet object that sidecar
    /// names, and creates the single <c>Filmstrip</c> row that owns the density — the exact layout
    /// <c>MediaDerivativeDispatcher</c> produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sidecar is produced by the WRITER's own builder — not by a hand-written JSON literal — so
    /// the fixture cannot drift from the document this endpoint actually has to read.
    /// </para>
    /// <para>
    /// <b>ONE row per density, whose <c>StorageKey</c> names the primary sheet.</b> That is what the
    /// dispatcher does (<c>BuildRow</c> with <c>SelectPrimaryArtifact</c>'s first image sheet), and it
    /// is why the sheets after the first have no row at all — seeding a row per sheet would test a
    /// shape the writer never produces and would hide exactly the bug this fixture exists to catch.
    /// Every sheet's object is uploaded into the row's own directory, again as the dispatcher does.
    /// </para>
    /// <para>
    /// <b><c>IndexStorageKey</c> is set explicitly, and the shipped writer persists exactly this.</b>
    /// <c>MediaDerivativeDispatcher.BuildRow</c> records the filmstrip's index-sidecar key on its
    /// density row, so the row seeded below is the shape a filmstrip run really leaves behind — no
    /// longer a half-landed column that only a fixture could produce. The unbound variant (a null
    /// pointer, which the same writer persists when the produced artifact is not the leaf
    /// <c>FilmstripIndex</c> predicts) is a separate fixture rather than an omission. No test infers
    /// a density from a sheet count, from a duration, or from a hardcoded 10x10 convention.
    /// </para>
    /// </remarks>
    private static Density SeedDensity(
        Fixture fx,
        MediaSourceKind sourceKind,
        Guid sourceId,
        string tag,
        string paramsHash,
        int densitySec,
        int thumbWidth,
        int thumbHeight,
        int[] thumbCounts,
        bool withRow = true,
        Guid? projectId = null)
    {
        Guid owningProject = projectId ?? fx.Project.Id;
        string directory = $"projects/{owningProject}/agentFiles/media-derivatives/{tag}/";
        string indexKey = directory + FilmstripIndex.IndexLeaf(paramsHash);

        string[] leaves = Enumerable.Range(1, thumbCounts.Length)
            .Select(sheetIndex => FilmstripIndex.SheetLeaf(paramsHash, sheetIndex))
            .ToArray();

        List<FilmstripSheetFrames> measured = leaves
            .Select((leaf, i) => new FilmstripSheetFrames(leaf, thumbCounts[i]))
            .ToList();

        // Plan() assigns the running thumbnail index and the source time; Build() serializes it. Both
        // are the writer's, so the numbers this endpoint must pass through are the writer's too.
        fx.SetupObject(indexKey, FilmstripIndex.Build(
            densitySec,
            thumbWidth,
            thumbHeight,
            FilmstripIndex.Plan(densitySec, measured)));

        // Every sheet of the density is a SIBLING of the row's own key — one shared directory, exactly
        // the layout UploadArtifactAsync produces.
        for (int i = 0; i < leaves.Length; i++)
        {
            fx.SetupObject(directory + leaves[i], SheetPayload(i));
        }

        MediaDerivative? row = withRow
            ? fx.AddDerivative(
                sourceKind,
                sourceId,
                MediaDerivativeKind.Filmstrip,
                storageKey: directory + leaves[0],
                indexStorageKey: indexKey,
                projectId: projectId)
            : null;

        return new Density(indexKey, directory, leaves, row);
    }

    /// <summary>The bytes served for sheet <paramref name="index"/> — distinctive, so a test can prove WHICH sheet it got.</summary>
    private static string SheetPayload(int index) => $"sheet-{index}-bytes";

    private static string SheetContentPath(Fixture fx, Guid derivativeId, int sheetIndex) =>
        $"/api/v1/projects/{fx.Project.Id:D}/media/derivatives/{derivativeId:D}/sheet/{sheetIndex}/content";

    /// <summary>Runs a sheet-content action, disposing the stream it opened and returning its bytes.</summary>
    private static async Task<(FileStreamResult File, string Body)> SheetContentOf(IActionResult result)
    {
        FileStreamResult file = result.Should().BeOfType<FileStreamResult>().Subject;

        await using Stream stream = file.FileStream;
        using StreamReader reader = new(stream, Encoding.UTF8);

        return (file, await reader.ReadToEndAsync());
    }

    private static void AssertNotFoundAndNeverForbidden(IActionResult? result)
    {
        result.Should().BeOfType<NotFoundResult>();
        // Never 403: a Forbid would confirm that somebody else's project or media exists.
        result.Should().NotBeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetFilmstrip_ResolvesTwoDensities_ToTheirOwnRows()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        // Seeded coarsest-first, so a response ordered by densitySec ascending cannot be an accident
        // of insertion order.
        Density coarse = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "coarse", "bbbbbbbbbbbbbbbb", 5, 96, 54, [30]);
        Density dense = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100, 55]);

        ActionResult<MediaFilmstripResponse> result =
            await fx.Controller.GetFilmstrip(fx.Project.Id, $"file:{file.Id:D}", CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        MediaFilmstripDocument document = FilmstripOf(result);

        document.Densities.Should().HaveCount(2);

        MediaFilmstripDensityResponse first = document.Densities[0];
        first.DensitySec.Should().Be(2);
        first.ThumbWidth.Should().Be(160);
        first.ThumbHeight.Should().Be(90);
        first.Cols.Should().Be(FilmstripIndex.Cols);
        first.Rows.Should().Be(FilmstripIndex.Rows);
        first.SheetCount.Should().Be(2);
        first.Sheets.Should().Equal(
            // firstThumbIndex/thumbCount/firstTimeSec are the writer's own numbers, passed through.
            new MediaFilmstripSheetResponse(0, 100, 0, SheetContentPath(fx, dense.Row!.Id, 0)),
            new MediaFilmstripSheetResponse(100, 55, 200, SheetContentPath(fx, dense.Row!.Id, 1)));

        MediaFilmstripDensityResponse second = document.Densities[1];
        second.DensitySec.Should().Be(5);
        second.ThumbWidth.Should().Be(96);
        second.ThumbHeight.Should().Be(54);
        second.SheetCount.Should().Be(1);
        second.Sheets.Should().Equal(
            new MediaFilmstripSheetResponse(0, 30, 0, SheetContentPath(fx, coarse.Row!.Id, 0)));
    }

    /// <summary>
    /// The regression this contract exists for: a density has ONE row, and every sheet of it —
    /// including the ones with no row of their own — still gets a working, distinct URL.
    /// </summary>
    [Fact]
    public async Task GetFilmstrip_GivesEverySheetOfOneDensityItsOwnIndex_ThoughOnlyThePrimarySheetHasARow()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        Density density = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100, 100, 100]);

        // Exactly one row for a three-sheet density — the writer's shape, and the whole reason a
        // leaf-to-row lookup could never have worked.
        fx.Db.MediaDerivatives.Should().ContainSingle();

        MediaFilmstripDensityResponse resolved = FilmstripOf(
                await fx.Controller.GetFilmstrip(fx.Project.Id, $"file:{file.Id:D}", CancellationToken.None))
            .Densities.Should().ContainSingle().Subject;

        resolved.SheetCount.Should().Be(3);
        resolved.Sheets.Should().HaveCount(3);
        resolved.Sheets.Select(sheet => sheet.ContentUrl).Should().Equal(
            SheetContentPath(fx, density.Row!.Id, 0),
            SheetContentPath(fx, density.Row!.Id, 1),
            SheetContentPath(fx, density.Row!.Id, 2));

        // And every one of those URLs really does stream ITS OWN sheet, not the primary one.
        IActionResult second = await fx.Controller.GetFilmstripSheetContent(
            fx.Project.Id, density.Row!.Id, 1, CancellationToken.None);

        (await SheetContentOf(second)).Body.Should().Be(SheetPayload(1));
    }

    [Fact]
    public async Task GetFilmstrip_ReturnsAnEmptyDensityList_WhenTheSourceHasNoIndex()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        // A source with derivatives, but none of them a filmstrip and no index pointer anywhere.
        fx.AddDerivative(MediaSourceKind.ProjectFile, file.Id, MediaDerivativeKind.Proxy);

        ActionResult<MediaFilmstripResponse> result =
            await fx.Controller.GetFilmstrip(fx.Project.Id, $"file:{file.Id:D}", CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        FilmstripOf(result).Densities.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task GetFilmstrip_ReturnsAnEmptyDensityList_ForASourceWhoseFilmstripRowsCarryNoIndexPointer()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        // A filmstrip row with NO IndexStorageKey — a shape the shipped writer still produces: when
        // the produced artifact is not the leaf FilmstripIndex predicts, ResolveIndexStorageKey
        // records a null pointer rather than naming an object the builder did not write. No density
        // is invented from the row's sheet count or from the source's duration: an unbound row is
        // simply not an index.
        fx.AddDerivative(
            MediaSourceKind.ProjectFile,
            file.Id,
            MediaDerivativeKind.Filmstrip,
            storageKey: $"projects/{fx.Project.Id:D}/agentFiles/media-derivatives/x/{FilmstripIndex.SheetLeaf("aaaaaaaaaaaaaaaa", 1)}");

        MediaFilmstripDocument document = FilmstripOf(
            await fx.Controller.GetFilmstrip(fx.Project.Id, $"file:{file.Id:D}", CancellationToken.None));

        document.Densities.Should().BeEmpty();
    }

    /// <summary>
    /// Two sources whose densities were uploaded under identical sheet leaf names must still resolve
    /// to their OWN rows — which index-addressing guarantees structurally and leaf matching could not.
    /// </summary>
    [Fact]
    public async Task GetFilmstrip_AddressesEachSourceThroughItsOwnRow()
    {
        using Fixture fx = new();
        ProjectFile first = fx.AddFile("first.mp4", "video/mp4");
        ProjectFile second = fx.AddFile("second.mp4", "video/mp4");

        // Same params hash and same density: the two densities produce BYTE-IDENTICAL leaf names.
        Density firstDensity = SeedDensity(fx, MediaSourceKind.ProjectFile, first.Id, "first", "cccccccccccccccc", 2, 160, 90, [100]);
        Density secondDensity = SeedDensity(fx, MediaSourceKind.ProjectFile, second.Id, "second", "cccccccccccccccc", 2, 160, 90, [100]);

        firstDensity.Leaves[0].Should().Be(secondDensity.Leaves[0]);
        firstDensity.Row!.Id.Should().NotBe(secondDensity.Row!.Id);

        MediaFilmstripSheetResponse firstSheet = FilmstripOf(
                await fx.Controller.GetFilmstrip(fx.Project.Id, $"file:{first.Id:D}", CancellationToken.None))
            .Densities.Single().Sheets.Single();

        MediaFilmstripSheetResponse secondSheet = FilmstripOf(
                await fx.Controller.GetFilmstrip(fx.Project.Id, $"file:{second.Id:D}", CancellationToken.None))
            .Densities.Single().Sheets.Single();

        firstSheet.ContentUrl.Should().Be(SheetContentPath(fx, firstDensity.Row!.Id, 0));
        secondSheet.ContentUrl.Should().Be(SheetContentPath(fx, secondDensity.Row!.Id, 0));
    }

    [Fact]
    public async Task GetFilmstrip_OmitsOnlyTheDensityWhoseSidecarCannotBeFetched()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        Density readable = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "readable", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100]);
        Density missing = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "missing", "bbbbbbbbbbbbbbbb", 5, 96, 54, [30]);

        fx.Storage
            .Setup(s => s.DownloadAsync(fx.Project.Id, missing.IndexKey, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("gone") { StatusCode = HttpStatusCode.NotFound });

        ActionResult<MediaFilmstripResponse> result =
            await fx.Controller.GetFilmstrip(fx.Project.Id, $"file:{file.Id:D}", CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        MediaFilmstripDocument document = FilmstripOf(result);

        // The readable density survives; the unreadable one is ABSENT rather than fabricated, and the
        // request as a whole is not a failure.
        MediaFilmstripDensityResponse survivor = document.Densities.Should().ContainSingle().Subject;
        survivor.DensitySec.Should().Be(2);
        survivor.Sheets.Single().ContentUrl.Should().Be(SheetContentPath(fx, readable.Row!.Id, 0));
    }

    [Fact]
    public async Task GetFilmstrip_OmitsOnlyTheDensityWhoseSidecarDoesNotParse()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        Density readable = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "readable", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100]);
        Density broken = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "broken", "bbbbbbbbbbbbbbbb", 5, 96, 54, [30]);

        // A truncated object: a real failure mode for a sidecar written while a bucket was full.
        fx.SetupObject(broken.IndexKey, "{\"densitySec\":5,\"thumbWidth\":96,");

        MediaFilmstripDocument document = FilmstripOf(
            await fx.Controller.GetFilmstrip(fx.Project.Id, $"file:{file.Id:D}", CancellationToken.None));

        MediaFilmstripDensityResponse survivor = document.Densities.Should().ContainSingle().Subject;
        survivor.DensitySec.Should().Be(2);
        survivor.Sheets.Single().ContentUrl.Should().Be(SheetContentPath(fx, readable.Row!.Id, 0));
    }

    [Fact]
    public async Task GetFilmstrip_WorksForAStepResultSource()
    {
        using Fixture fx = new();
        WorkflowStepResult render = fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/{Guid.NewGuid():N}/render.mp4");

        Density density = SeedDensity(fx, MediaSourceKind.StepResult, render.Id, "render", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100]);

        // A project-file filmstrip row with the SAME Guid as the render must not be picked up.
        fx.AddDerivative(
            MediaSourceKind.ProjectFile,
            render.Id,
            MediaDerivativeKind.Filmstrip,
            storageKey: $"projects/{fx.Project.Id:D}/agentFiles/media-derivatives/other/{density.Leaves[0]}",
            indexStorageKey: density.IndexKey);

        MediaFilmstripDocument document = FilmstripOf(
            await fx.Controller.GetFilmstrip(fx.Project.Id, $"step_result:{render.Id:D}", CancellationToken.None));

        document.Densities.Single().Sheets.Single().ContentUrl.Should().Be(SheetContentPath(fx, density.Row!.Id, 0));
    }

    [Fact]
    public async Task GetFilmstrip_NeverSendsAStorageKeyBucketOrObjectUrl()
    {
        using Fixture fx = new();
        string secretKey = $"projects/{fx.Project.Id:D}/userFiles/{Guid.NewGuid():N}/clip.mp4";
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4", storageKey: secretKey);
        Density density = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100, 55]);

        ActionResult<MediaFilmstripResponse> result =
            await fx.Controller.GetFilmstrip(fx.Project.Id, $"file:{file.Id:D}", CancellationToken.None);

        string json = JsonSerializer.Serialize(((OkObjectResult)result.Result!).Value, WireJson);

        AssertNoStorageLeak(json, secretKey, density.Directory, density.IndexKey, density.Leaves[0], density.Leaves[1]);
        // The only URLs a sheet may carry are this API's own sheet-content paths.
        json.Should().Contain(SheetContentPath(fx, density.Row!.Id, 0));
        json.Should().Contain(SheetContentPath(fx, density.Row!.Id, 1));
    }

    // =============================================================================================
    // The sheet route: GET /media/derivatives/{id}/sheet/{i}/content
    // =============================================================================================

    [Fact]
    public async Task GetFilmstripSheet_StreamsTheSheetAtThatIndex_WithRangeProcessing()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        Density density = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100, 55]);

        IActionResult result = await fx.Controller.GetFilmstripSheetContent(
            fx.Project.Id, density.Row!.Id, 1, CancellationToken.None);

        (FileStreamResult payload, string body) = await SheetContentOf(result);

        body.Should().Be(SheetPayload(1));
        payload.ContentType.Should().Be("image/jpeg");
        // Range support matters here for the same reason it does on the sibling content route.
        fx.Controller.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
    }

    [Fact]
    public async Task GetFilmstripSheet_HonoursARangeRequest_With206AndTheExactSlice()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        Density density = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100, 55]);
        string whole = SheetPayload(1);
        fx.Controller.Request.Headers.Range = "bytes=0-3";

        IActionResult result = await fx.Controller.GetFilmstripSheetContent(
            fx.Project.Id, density.Row!.Id, 1, CancellationToken.None);

        (_, string body) = await SheetContentOf(result);
        fx.Controller.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        fx.Controller.Response.Headers.ContentRange.ToString().Should().Be($"bytes 0-3/{Encoding.UTF8.GetByteCount(whole)}");
        body.Should().Be(whole[..4]);
    }

    [Fact]
    public async Task GetFilmstripSheet_AsksStorageForTheOwnersDirectoryPlusTheLeavesFileName()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        Density density = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100, 55]);

        await fx.Controller.GetFilmstripSheetContent(fx.Project.Id, density.Row!.Id, 1, CancellationToken.None);

        // The key is derived, not matched: the row's own directory plus that sheet's leaf — and it is
        // that object, once, whose bytes are fetched from the store.
        fx.S3.Verify(
            s => s.GetObjectAsync(
                It.Is<GetObjectRequest>(r => r.Key == density.Directory + density.Leaves[1]),
                It.IsAny<CancellationToken>()),
            Times.Once);
        fx.S3.Verify(
            s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetFilmstripSheet_ReturnsNotFound_ForAnOutOfRangeIndex()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        Density density = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100]);

        AssertNotFoundAndNeverForbidden(
            await fx.Controller.GetFilmstripSheetContent(fx.Project.Id, density.Row!.Id, 1, CancellationToken.None));

        AssertNotFoundAndNeverForbidden(
            await fx.Controller.GetFilmstripSheetContent(fx.Project.Id, density.Row!.Id, -1, CancellationToken.None));
    }

    [Fact]
    public async Task GetFilmstripSheet_ReturnsNotFound_WhenTheSheetObjectIsMissingFromStorage()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        Density density = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100, 55]);

        // The index describes sheet 1, but its object was never uploaded: a per-sheet absence that
        // must not cost the client sheet 0.
        fx.Storage
            .Setup(s => s.DownloadAsync(fx.Project.Id, density.Directory + density.Leaves[1], It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("gone") { StatusCode = HttpStatusCode.NotFound });

        AssertNotFoundAndNeverForbidden(
            await fx.Controller.GetFilmstripSheetContent(fx.Project.Id, density.Row!.Id, 1, CancellationToken.None));

        IActionResult sibling = await fx.Controller.GetFilmstripSheetContent(
            fx.Project.Id, density.Row!.Id, 0, CancellationToken.None);

        (await SheetContentOf(sibling)).Body.Should().Be(SheetPayload(0));
    }

    [Fact]
    public async Task GetFilmstripSheet_ReturnsNotFound_ForARowThatIsNotAFilmstrip()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        Density density = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100]);

        // A proxy row that happens to carry an index pointer is not a sprite-sheet owner.
        MediaDerivative proxy = fx.AddDerivative(
            MediaSourceKind.ProjectFile,
            file.Id,
            MediaDerivativeKind.Proxy,
            indexStorageKey: density.IndexKey);

        AssertNotFoundAndNeverForbidden(
            await fx.Controller.GetFilmstripSheetContent(fx.Project.Id, proxy.Id, 0, CancellationToken.None));
    }

    [Fact]
    public async Task GetFilmstripSheet_ReturnsNotFound_WhenTheIndexPointerIsMissingOrOutsideTheProject()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        Density density = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100], withRow: false);

        MediaDerivative unbound = fx.AddDerivative(
            MediaSourceKind.ProjectFile,
            file.Id,
            MediaDerivativeKind.Filmstrip,
            storageKey: density.Directory + density.Leaves[0]);

        MediaDerivative escaped = fx.AddDerivative(
            MediaSourceKind.ProjectFile,
            file.Id,
            MediaDerivativeKind.Filmstrip,
            storageKey: density.Directory + density.Leaves[0],
            indexStorageKey: $"projects/{Guid.NewGuid():D}/agentFiles/media-derivatives/x/index.json");

        AssertNotFoundAndNeverForbidden(
            await fx.Controller.GetFilmstripSheetContent(fx.Project.Id, unbound.Id, 0, CancellationToken.None));

        AssertNotFoundAndNeverForbidden(
            await fx.Controller.GetFilmstripSheetContent(fx.Project.Id, escaped.Id, 0, CancellationToken.None));
    }

    [Fact]
    public async Task GetFilmstripSheet_ReturnsNotFound_WhenTheRowItselfEscapesTheProjectPrefix()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        MediaDerivative escaped = fx.AddDerivative(
            MediaSourceKind.ProjectFile,
            file.Id,
            MediaDerivativeKind.Filmstrip,
            storageKey: $"projects/{Guid.NewGuid():D}/agentFiles/media-derivatives/x/{FilmstripIndex.SheetLeaf("aaaaaaaaaaaaaaaa", 1)}",
            indexStorageKey: $"projects/{fx.Project.Id:D}/agentFiles/media-derivatives/x/{FilmstripIndex.IndexLeaf("aaaaaaaaaaaaaaaa")}");

        AssertNotFoundAndNeverForbidden(
            await fx.Controller.GetFilmstripSheetContent(fx.Project.Id, escaped.Id, 0, CancellationToken.None));
    }

    [Fact]
    public async Task GetFilmstripSheet_ReturnsNotFound_WhenTheSidecarDoesNotParse()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        Density density = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100]);

        fx.SetupObject(density.IndexKey, "{\"sheets\":[");

        AssertNotFoundAndNeverForbidden(
            await fx.Controller.GetFilmstripSheetContent(fx.Project.Id, density.Row!.Id, 0, CancellationToken.None));
    }

    [Fact]
    public async Task GetFilmstripSheet_ReturnsNotFound_ForAnUnknownOrForeignDerivative()
    {
        using Fixture fx = new();
        Project foreign = fx.NewForeignProject();
        ProjectFile theirs = fx.AddFile("theirs.mp4", "video/mp4", projectId: foreign.Id);
        Density theirsDensity = SeedDensity(
            fx,
            MediaSourceKind.ProjectFile,
            theirs.Id,
            "theirs",
            "aaaaaaaaaaaaaaaa",
            2,
            160,
            90,
            [100],
            projectId: foreign.Id);

        AssertNotFoundAndNeverForbidden(
            await fx.Controller.GetFilmstripSheetContent(fx.Project.Id, Guid.NewGuid(), 0, CancellationToken.None));

        AssertNotFoundAndNeverForbidden(
            await fx.Controller.GetFilmstripSheetContent(fx.Project.Id, theirsDensity.Row!.Id, 0, CancellationToken.None));
    }

    [Theory]
    [InlineData("../../evil.jpg")]
    [InlineData("nested/evil.jpg")]
    [InlineData("..\\evil.jpg")]
    public async Task GetFilmstripSheet_RejectsALeafThatEscapesTheOwnersDirectory(string hostileLeaf)
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        Density density = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100]);

        // A sidecar naming a leaf that is not a bare file name. The row is real and its index parses;
        // only the leaf is hostile, so nothing but the leaf check stands between it and the store.
        fx.SetupObject(
            density.IndexKey,
            "{\"densitySec\":2,\"thumbWidth\":160,\"thumbHeight\":90,\"cols\":10,\"rows\":10,\"sheetCount\":1," +
            $"\"sheets\":[{{\"storageLeaf\":\"{hostileLeaf.Replace("\\", "\\\\")}\",\"firstThumbIndex\":0,\"thumbCount\":100,\"firstTimeSec\":0}}]}}");

        AssertNotFoundAndNeverForbidden(
            await fx.Controller.GetFilmstripSheetContent(fx.Project.Id, density.Row!.Id, 0, CancellationToken.None));

        // And the store was never asked for the key the leaf would have built.
        fx.Storage.Verify(
            s => s.DownloadAsync(It.IsAny<Guid>(), It.Is<string>(key => key.Contains("..")), It.IsAny<CancellationToken>()),
            Times.Never);

        fx.Storage.Verify(
            s => s.DownloadAsync(It.IsAny<Guid>(), It.Is<string>(key => key.Contains("evil")), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetFilmstripSheet_NeverSendsAStorageKeyBucketOrObjectUrl()
    {
        using Fixture fx = new();
        string secretKey = $"projects/{fx.Project.Id:D}/userFiles/{Guid.NewGuid():N}/clip.mp4";
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4", storageKey: secretKey);
        Density density = SeedDensity(fx, MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100]);

        IActionResult result = await fx.Controller.GetFilmstripSheetContent(
            fx.Project.Id, density.Row!.Id, 0, CancellationToken.None);

        FileStreamResult payload = (await SheetContentOf(result)).File;

        // The response is the object's BYTES and an inferred content type. No key, no bucket, no URL.
        payload.ContentType.Should().Be("image/jpeg");
        string leaf = density.Leaves[0];
        fx.Controller.Response.Headers["Content-Disposition"].ToString()
            .Should().Be($"inline; filename=\"{leaf}\"; filename*=UTF-8''{Uri.EscapeDataString(leaf)}");
        fx.Controller.Response.Headers["Content-Disposition"].ToString().Should().NotContain(secretKey);
    }

    // =============================================================================================
    // Filmstrip 404s — never 403, never 500
    // =============================================================================================

    [Fact]
    public async Task GetFilmstrip_ReturnsNotFound_NotForbid_WhenTheProjectDoesNotExist()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");

        AssertNotFoundAndNeverForbidden(
            (await fx.Controller.GetFilmstrip(Guid.NewGuid(), $"file:{file.Id:D}", CancellationToken.None)).Result);
    }

    [Fact]
    public async Task GetFilmstrip_ReturnsNotFound_NotForbid_WhenTheProjectBelongsToAnotherUser()
    {
        using Fixture fx = new();
        Project foreign = fx.NewForeignProject();
        ProjectFile theirs = fx.AddFile("theirs.mp4", "video/mp4", projectId: foreign.Id);

        AssertNotFoundAndNeverForbidden(
            (await fx.Controller.GetFilmstrip(foreign.Id, $"file:{theirs.Id:D}", CancellationToken.None)).Result);
    }

    [Fact]
    public async Task GetFilmstrip_ReturnsNotFound_WhenTheMediaRefIsNotInThisProject()
    {
        using Fixture fx = new();
        Project foreign = fx.NewForeignProject();
        ProjectFile theirs = fx.AddFile("theirs.mp4", "video/mp4", projectId: foreign.Id);

        // A real file, with a real filmstrip row, that belongs to somebody else's project.
        fx.AddDerivative(MediaSourceKind.ProjectFile, theirs.Id, MediaDerivativeKind.Filmstrip, projectId: foreign.Id);

        AssertNotFoundAndNeverForbidden(
            (await fx.Controller.GetFilmstrip(fx.Project.Id, $"file:{theirs.Id:D}", CancellationToken.None)).Result);
        AssertNotFoundAndNeverForbidden(
            (await fx.Controller.GetFilmstrip(fx.Project.Id, $"file:{Guid.NewGuid():D}", CancellationToken.None)).Result);
    }

    [Fact]
    public async Task GetFilmstrip_ReturnsNotFound_WhenTheReferenceIsAProjectFileThatIsNotMedia()
    {
        using Fixture fx = new();
        ProjectFile notes = fx.AddFile("notes.md", "text/markdown");
        fx.AddDerivative(MediaSourceKind.ProjectFile, notes.Id, MediaDerivativeKind.Filmstrip);

        AssertNotFoundAndNeverForbidden(
            (await fx.Controller.GetFilmstrip(fx.Project.Id, $"file:{notes.Id:D}", CancellationToken.None)).Result);
    }

    [Fact]
    public async Task GetFilmstrip_ReturnsNotFound_WhenTheStepResultCarriesNoMediaOutputKey()
    {
        using Fixture fx = new();

        // An unregistered render: no output key at all.
        WorkflowStepResult unregistered = fx.AddRender(storageKey: null!);
        // And one whose key sits outside this project's outputFiles/ prefix.
        WorkflowStepResult escaped = fx.AddRender($"projects/{Guid.NewGuid():D}/outputFiles/{Guid.NewGuid():N}/render.mp4");

        AssertNotFoundAndNeverForbidden(
            (await fx.Controller.GetFilmstrip(fx.Project.Id, $"step_result:{unregistered.Id:D}", CancellationToken.None)).Result);
        AssertNotFoundAndNeverForbidden(
            (await fx.Controller.GetFilmstrip(fx.Project.Id, $"step_result:{escaped.Id:D}", CancellationToken.None)).Result);
    }

    [Fact]
    public async Task GetFilmstrip_ReturnsNotFound_WhenTheMediaRefHasNoParseableGuid()
    {
        using Fixture fx = new();

        // The route constraint rejects these before the action runs, but the action is guarded too —
        // this is the same belt-and-braces the derivative list uses, asserted rather than assumed.
        AssertNotFoundAndNeverForbidden(
            (await fx.Controller.GetFilmstrip(fx.Project.Id, "file:------------------------------------", CancellationToken.None)).Result);
        AssertNotFoundAndNeverForbidden(
            (await fx.Controller.GetFilmstrip(fx.Project.Id, "bins", CancellationToken.None)).Result);
    }

    [Fact]
    public void FilmstripRoutes_AreRegexConstrained_SoBinsCannotCollide()
    {
        MethodInfo method = typeof(MediaController).GetMethod(nameof(MediaController.GetFilmstrip))!;

        string[] templates = method
            .GetCustomAttributes<HttpGetAttribute>()
            .Select(a => a.Template ?? string.Empty)
            .ToArray();

        // One template per mediaRef prefix, exactly like the two derivatives routes — the framework
        // cannot express the alternation in a single constraint (see the controller's own docs).
        templates.Should().Equal(
            MediaController.FileMediaRefFilmstripRouteTemplate,
            MediaController.StepResultMediaRefFilmstripRouteTemplate);

        string fileGuid = Guid.NewGuid().ToString("D");
        string stepGuid = Guid.NewGuid().ToString("D");

        ConstraintOf(templates[0]).Should().NotBeNull();
        AssertMatches(templates[0], [$"file:{fileGuid}"], [$"step_result:{stepGuid}", "bins", fileGuid]);
        AssertMatches(templates[1], [$"step_result:{stepGuid}"], [$"file:{fileGuid}", "bins", stepGuid]);
    }

    // =============================================================================================
    // The derivative row is untouched
    // =============================================================================================

    /// <summary>
    /// The frozen shape of <c>GET .../media/{mediaRef}/derivatives</c>'s row, pinned by name: the
    /// filmstrip work must not have added an <c>indexContentUrl</c>, an index pointer or anything
    /// else to it.
    /// </summary>
    [Fact]
    public void MediaDerivativeResponse_CarriesExactlyTheFrozenFields()
    {
        MediaDerivativeResponse row = new(
            Guid.NewGuid(),
            MediaDerivativeKind.Filmstrip,
            "image/jpeg",
            4096,
            null,
            160,
            90,
            "/api/v1/projects/p/media/derivatives/i/content");

        string json = JsonSerializer.Serialize(row, WireJson);

        using JsonDocument document = JsonDocument.Parse(json);

        document.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal(
            "id", "kind", "mimeType", "sizeBytes", "durationSec", "width", "height", "contentUrl", "cameraProfile");

        json.Should().NotContain("indexContentUrl");
        json.Should().NotContain("index");
        json.Should().NotContain("storageKey");
    }

    [Fact]
    public async Task ListDerivatives_ListsAStabilizedProxy_ByItsWireNameAndContentPath_WithNoStorageKey()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        MediaDerivative row = fx.AddDerivative(MediaSourceKind.ProjectFile, file.Id, MediaDerivativeKind.StabilizedProxy);

        ActionResult<List<MediaDerivativeResponse>> result =
            await fx.Controller.ListDerivatives(fx.Project.Id, $"file:{file.Id:D}", CancellationToken.None);

        MediaDerivativeResponse listed = DerivativesOf(result).Should().ContainSingle().Subject;
        listed.Id.Should().Be(row.Id);
        listed.Kind.Should().Be(MediaDerivativeKind.StabilizedProxy);
        string json = JsonSerializer.Serialize(listed, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new MediaWireEnumJsonConverter() }
        });
        json.Should().Contain("\"kind\":\"stabilizedProxy\"");
        json.Should().NotContain("storageKey");
    }

    [Fact]
    public async Task RequestDerivatives_ForAStabilizedProxy_PublishesThatKindOnly_AndAnUnknownKindIsRefused()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        fx.Storage
            .Setup(s => s.GetObjectETagAsync(fx.Project.Id, file.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync("\"the-etag\"");

        IActionResult refused = await fx.Controller.RequestDerivatives(
            fx.Project.Id, new RequestMediaDerivativesRequest($"file:{file.Id:D}", ["stabilisedProxy2"]), CancellationToken.None);
        refused.Should().BeOfType<BadRequestObjectResult>();
        fx.Published.Should().BeEmpty();

        IActionResult accepted = await fx.Controller.RequestDerivatives(
            fx.Project.Id, new RequestMediaDerivativesRequest($"file:{file.Id:D}", ["stabilizedProxy"]), CancellationToken.None);
        accepted.Should().BeOfType<AcceptedResult>();
        fx.Published.Should().ContainSingle().Subject.Kinds.Should().Equal(MediaDerivativeKind.StabilizedProxy);
    }
}
