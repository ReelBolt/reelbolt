#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Workflows;
using Xunit;
// Aliased rather than imported by namespace: the media DTO namespace and the WorkflowEngine's video
// services both carry filmstrip vocabulary, and an alias makes a future name collision impossible to
// rebind silently.
using FilmstripIndex = ReelBolt.WorkflowEngine.Services.Video.FilmstripIndex;
using FilmstripSheetFrames = ReelBolt.WorkflowEngine.Services.Video.FilmstripSheetFrames;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The P2 media contract gaps T-105..T-109 closed, verified independently of the code that closed
/// them.
///
/// <para>
/// <b>Every assertion below states what the CONTRACT requires, never what the shipped code happens to
/// do.</b> The file was written against the ticket's acceptance list and the doc comments on
/// <see cref="MediaController"/>, <see cref="MediaBinsController"/>, <see cref="MediaDtos"/> and
/// <see cref="MediaBinDtos"/> — the six probe fields, the pin membership list, the filmstrip index
/// and its sheet route, the 404-not-403 ownership boundary, the post-commit upload publish, and the
/// no-storage-key rule. No production file was read for a value to copy into an expected result.
/// </para>
/// <para>
/// <b>Six sections, one per gap:</b>
/// </para>
/// <list type="number">
/// <item><b>Probe completeness</b> — all six new fields map through; an absent artifact and one that
/// predates the fields both report <c>null</c>, and a numeric field is asserted at the JSON level to
/// be <c>null</c> rather than <c>0</c>.</item>
/// <item><b>Bin membership</b> — the pool's <c>bins[]</c> is exactly the live pins of that source; the
/// per-bin listing resolves the source's own fields and keeps a dangling pin with nulls.</item>
/// <item><b>Filmstrip index</b> — a density is ONE row, every sheet of it is still addressable through
/// that row plus its own index, a corrupt sidecar drops only itself, and the sheet route streams the
/// right bytes while answering <c>404</c> (never <c>403</c>, never <c>500</c>) to everything else.</item>
/// <item><b>Ownership</b> — a foreign project, a foreign <c>mediaRef</c> and a foreign derivative id
/// are all <c>404</c> and never a <see cref="ForbidResult"/>, and a refused call leaves the database
/// byte-identical.</item>
/// <item><b>Upload hardening</b> — a broker outage cannot fail a committed <c>video/*</c> upload, and
/// the happy path publishes exactly one request.</item>
/// <item><b>No leak</b> — no response body carries a storage key, a bucket name or an object URL, and
/// <see cref="MediaDerivativeResponse"/>'s field set is unchanged.</item>
/// </list>
/// <para>
/// The fixture's object store is keyed by the storage keys the code under test builds, so a
/// regression that changed the derived key would be a 404 rather than a false pass. Every write-nothing
/// claim is checked through a <b>fresh</b> <see cref="InferenceApiDbContext"/>, because the context
/// that served the request would answer from its own change tracker.
/// </para>
/// </summary>
public class MediaContractGapTests
{
    /// <summary>
    /// The API's real response serializer, mirrored exactly: web defaults and <b>no options-level
    /// enum converter</b> (the media contract's camelCase encoding rides on type-level attributes).
    /// </summary>
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The engine's artifact options (<c>VideoAnalyzeStepExecutor.ArtifactJsonOptions</c>), so the
    /// fixture writes artifacts the way the real producer does.
    /// </summary>
    private static readonly JsonSerializerOptions ArtifactJson =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>A marker planted in every fixture storage key, so a leak is unmissable.</summary>
    private const string KeyCanary = "leakcanarykey";

    /// <summary>A marker planted in every fixture bucket name.</summary>
    private const string BucketCanary = "leakcanarybucket";

    /// <summary>The bucket every fixture row records — the pool and the bins never send it.</summary>
    private const string Bucket = BucketCanary;

    // =============================================================================================
    // Harness
    // =============================================================================================

    private static DbContextOptions<InferenceApiDbContext> OptionsFor(string databaseName) =>
        new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

    private static Project NewProject(Guid ownerId, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        OwnerId = ownerId,
        OrganizationId = TestTenancy.OrgOf(ownerId),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private sealed class StubCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsAdmin { get; } = false;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    /// <summary>The smallest honest <see cref="IFormFile"/>; the storage service is mocked.</summary>
    private sealed class StubFormFile(string fileName, string contentType, byte[]? content = null) : IFormFile
    {
        private readonly byte[] _content = content ?? [0x00, 0x00, 0x00, 0x18];

        public string ContentType { get; } = contentType;

        public string ContentDisposition => $"form-data; name=\"file\"; filename=\"{fileName}\"";

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public long Length => _content.Length;

        public string Name => "file";

        public string FileName { get; } = fileName;

        public Stream OpenReadStream() => new MemoryStream(_content, writable: false);

        public void CopyTo(Stream target) => throw new NotSupportedException();

        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>One seeded filmstrip density: the row that owns it, its directory and its sheet leaves.</summary>
    private sealed record SeededDensity(MediaDerivative Row, string Directory, string IndexKey, string[] Leaves);

    /// <summary>
    /// A project owned by the current user, its foreign twin owned by somebody else, three controllers
    /// over one database, and a keyed in-memory object store.
    /// </summary>
    private sealed class GapFixture : IDisposable
    {
        /// <summary>Every object the fixture has "uploaded", by storage key.</summary>
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public GapFixture()
        {
            UserId = Guid.NewGuid();
            DatabaseName = $"media-contract-gap-{Guid.NewGuid()}";
            Db = new InferenceApiDbContext(OptionsFor(DatabaseName));

            Project = NewProject(UserId, "Owned Project");
            ForeignProject = NewProject(Guid.NewGuid(), "Somebody Else's Project");
            Db.Projects.AddRange(Project, ForeignProject);

            // A foreign project file and a foreign filmstrip row, so "another project's media" is a
            // real, resolvable thing rather than merely a missing id.
            ForeignFile = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = ForeignProject.Id,
                OriginalFileName = "foreign.mp4",
                StorageKey = $"projects/{ForeignProject.Id:D}/userFiles/{KeyCanary}/foreign.mp4",
                StorageBucket = Bucket,
                Category = "userFiles",
                MimeType = "video/mp4",
                SizeBytes = 2048,
                UploadedAt = DateTime.UtcNow
            };

            Db.ProjectFiles.Add(ForeignFile);
            Db.SaveChanges();

            Storage
                .Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, string key, CancellationToken _) => OpenAsync(key));

            Storage
                .Setup(s => s.GetObjectETagAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("etag-abc123");

            Storage
                .Setup(s => s.UploadAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<Guid>(),
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyDictionary<string, string>?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>()))
                .ReturnsAsync(new StoredFileObject(
                    UploadedKey,
                    Bucket,
                    $"projects/{Project.Id:D}/userFiles",
                    null,
                    "userFiles",
                    "rushcut-a.mp4",
                    "rushcut-a.mp4",
                    null,
                    "rushcut-a.mp4"));

            Publisher
                .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
                .Callback<MediaDerivativesRequested, CancellationToken>((message, _) => Published.Add(message))
                .Returns(Task.CompletedTask);

            Media = new MediaController(
                Db,
                TestTenancy.Access(Db, new StubCurrentUser(UserId)),
                Storage.Object,
                StorageBackedS3.Over(Storage).Object,
                Publisher.Object,
                NullLogger<MediaController>.Instance);

            Bins = new MediaBinsController(Db, TestTenancy.Access(Db, new StubCurrentUser(UserId)));

            Files = new ProjectFilesController(
                Db,
                new StubCurrentUser(UserId),
                TestTenancy.Access(Db, new StubCurrentUser(UserId)),
                Storage.Object,
                SummarizationQueue.Object,
                Publisher.Object,
                // The upload path never reaches vector search; the ctor needs the instance only
                // because the other actions on that controller do.
                null!,
                logger: NullLogger<ProjectFilesController>.Instance);

            // Both content routes append a Content-Disposition header, and MVC's base class throws
            // when there is no HttpContext at all — so one is always attached, exactly as MVC would.
            Media.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            Bins.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            DefaultHttpContext uploadContext = new();
            uploadContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>());
            Files.ControllerContext = new ControllerContext { HttpContext = uploadContext };
        }

        public Guid UserId { get; }

        public string DatabaseName { get; }

        public InferenceApiDbContext Db { get; }

        public Project Project { get; }

        public Project ForeignProject { get; }

        public ProjectFile ForeignFile { get; }

        public Mock<IFileStorageService> Storage { get; } = new();

        public Mock<IPublishEndpoint> Publisher { get; } = new();

        public Mock<IBackgroundTaskQueue<FileSummarizationTask>> SummarizationQueue { get; } = new();

        public MediaController Media { get; }

        public MediaBinsController Bins { get; }

        public ProjectFilesController Files { get; }

        public List<MediaDerivativesRequested> Published { get; } = [];

        /// <summary>The key the mocked upload reports — planted with the bucket canary's twin.</summary>
        public string UploadedKey => $"projects/{Project.Id:D}/userFiles/{KeyCanary}/rushcut-a.mp4";

        // --- object store ---------------------------------------------------------------------

        /// <summary>Registers the bytes served for one storage key.</summary>
        public void Put(string storageKey, string body) =>
            _objects[storageKey] = Encoding.UTF8.GetBytes(body);

        /// <summary>
        /// Forgets one object — how a sheet whose bytes were never uploaded (or were removed) is
        /// modelled, rather than by leaving a key merely unregistered.
        /// </summary>
        public void Remove(string storageKey) => _objects.Remove(storageKey);

        private Task<Stream> OpenAsync(string key) =>
            _objects.TryGetValue(key, out byte[]? bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
                // The object store's own answer for a key that is not there. The content routes are
                // required to turn exactly this into a 404 rather than letting it surface as a 500.
                : throw new AmazonS3Exception("NoSuchKey") { StatusCode = HttpStatusCode.NotFound };

        // --- seeders --------------------------------------------------------------------------

        public ProjectFile AddFile(
            string fileName = "clip.mp4",
            string mimeType = "video/mp4",
            long sizeBytes = 1024,
            Guid? projectId = null,
            string? storageKey = null,
            DateTime? uploadedAt = null)
        {
            Guid owningProject = projectId ?? Project.Id;

            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = owningProject,
                OriginalFileName = fileName,
                StorageKey = storageKey ?? $"projects/{owningProject:D}/userFiles/{KeyCanary}/{fileName}",
                StorageBucket = Bucket,
                Category = "userFiles",
                MimeType = mimeType,
                SizeBytes = sizeBytes,
                UploadedAt = uploadedAt ?? DateTime.UtcNow
            };

            Db.ProjectFiles.Add(file);
            Db.SaveChanges();
            return file;
        }

        /// <summary>
        /// A step result that carries an <c>outputFiles/</c> key — the pool's second source shape.
        /// </summary>
        public WorkflowStepResult AddRender(
            string fileName = "render.mp4",
            Guid? projectId = null,
            DateTime? completedAt = null,
            string? outputStorageKey = null,
            string? artifactStorageKey = null)
        {
            Guid owningProject = projectId ?? Project.Id;

            WorkflowExecution execution = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                ProjectId = owningProject,
                Status = ExecutionStatus.Passed
            };

            // The pool's probe fallback reads only a VideoAnalyze step's artifact (T-107), so a
            // result carrying one is seeded with a step row of that type.
            WorkflowStep step = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = execution.WorkflowDefinitionId,
                AgentDefinitionId = Guid.NewGuid(),
                StepOrder = 1,
                StepType = artifactStorageKey is null ? StepType.VideoCompile : StepType.VideoAnalyze
            };

            WorkflowStepResult result = new()
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execution.Id,
                WorkflowStepId = step.Id,
                ExecutedAt = completedAt ?? DateTime.UtcNow,
                CompletedAt = completedAt,
                OutputStorageKey = outputStorageKey ?? $"projects/{owningProject:D}/outputFiles/{fileName}",
                ArtifactStorageKey = artifactStorageKey,
                Status = StepStatus.Completed
            };

            Db.WorkflowExecutions.Add(execution);
            Db.WorkflowSteps.Add(step);
            Db.WorkflowStepResults.Add(result);
            Db.SaveChanges();
            return result;
        }

        /// <summary>
        /// Writes a real <c>VideoAnalyze</c> artifact (serialized with the producer's own options),
        /// registers its object and the step result whose <c>ArtifactStorageKey</c> points at it.
        /// </summary>
        public string AddAnalysisArtifact(params VideoAnalysisSourceInfo[] sources)
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

            return AddRawAnalysisArtifact(JsonSerializer.Serialize(artifact, ArtifactJson));
        }

        /// <summary>
        /// Writes an artifact body VERBATIM — the only honest way to model a document persisted before
        /// the six new fields existed, since the current producer's serializer would always emit them.
        /// </summary>
        public string AddRawAnalysisArtifact(string artifactJson)
        {
            string key =
                $"projects/{Project.Id:D}/agentFiles/video-analysis/{Guid.NewGuid():N}/step-1-analysis.json";

            Put(key, artifactJson);
            AddRender(artifactStorageKey: key);
            return key;
        }

        public MediaDerivative AddDerivative(
            MediaSourceKind sourceKind,
            Guid sourceId,
            MediaDerivativeKind kind,
            string? storageKey = null,
            string? indexStorageKey = null,
            Guid? projectId = null,
            string? mimeType = "image/jpeg",
            long sizeBytes = 4096,
            double? durationSec = 5.5,
            int? width = 160,
            int? height = 90)
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
                SourceStorageKey = $"projects/{owningProject:D}/userFiles/{KeyCanary}/source-{suffix}",
                SourceETag = $"\"etag-{suffix}\"",
                ParamsHash = $"params-{suffix}",
                CacheKey = $"cache-{suffix}",
                BuilderVersion = 1,
                Kind = kind,
                StorageKey = storageKey ?? $"projects/{owningProject:D}/derivatives/{suffix}/{kind}.bin",
                StorageBucket = Bucket,
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

        public MediaBin AddBin(string name = "Bin", DateTime? deletedAt = null, Guid? projectId = null)
        {
            MediaBin bin = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId ?? Project.Id,
                Name = name,
                SortOrder = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                DeletedAt = deletedAt
            };

            Db.MediaBins.Add(bin);
            Db.SaveChanges();
            return bin;
        }

        public MediaBinItem AddBinItem(Guid binId, MediaSourceKind sourceKind, Guid sourceId, int sortOrder = 0)
        {
            MediaBinItem item = new()
            {
                Id = Guid.NewGuid(),
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
        /// Seeds one filmstrip density exactly as the media writer lays it out: ONE
        /// <c>media_derivatives</c> row whose <c>StorageKey</c> names the primary sheet, a real
        /// <c>FilmstripIndex.Build</c> sidecar, and every sheet uploaded into the row's own directory
        /// as a sibling.
        /// </summary>
        /// <remarks>
        /// The sidecar comes from the WRITER's builder rather than a hand-written literal, so the
        /// fixture cannot drift from the document the reader has to parse. Seeding one row per sheet
        /// would test a shape no writer produces and would hide the very defect the sheet route exists
        /// to fix.
        /// </remarks>
        public SeededDensity SeedDensity(
            MediaSourceKind sourceKind,
            Guid sourceId,
            string tag,
            string paramsHash,
            int densitySec,
            int thumbWidth,
            int thumbHeight,
            params int[] thumbCounts) =>
            SeedDensityIn(Project.Id, sourceKind, sourceId, tag, paramsHash, densitySec, thumbWidth, thumbHeight, thumbCounts);

        /// <summary>The same seeding for a density another project owns.</summary>
        public SeededDensity SeedDensityIn(
            Guid owningProjectId,
            MediaSourceKind sourceKind,
            Guid sourceId,
            string tag,
            string paramsHash,
            int densitySec,
            int thumbWidth,
            int thumbHeight,
            int[] thumbCounts)
        {
            string directory = $"projects/{owningProjectId:D}/agentFiles/media-derivatives/{tag}/";
            string indexKey = directory + FilmstripIndex.IndexLeaf(paramsHash);

            string[] leaves = Enumerable.Range(1, thumbCounts.Length)
                .Select(sheetIndex => FilmstripIndex.SheetLeaf(paramsHash, sheetIndex))
                .ToArray();

            List<FilmstripSheetFrames> measured = leaves
                .Select((leaf, i) => new FilmstripSheetFrames(leaf, thumbCounts[i]))
                .ToList();

            Put(indexKey, FilmstripIndex.Build(
                densitySec,
                thumbWidth,
                thumbHeight,
                FilmstripIndex.Plan(densitySec, measured)));

            for (int i = 0; i < leaves.Length; i++)
            {
                Put(directory + leaves[i], SheetPayload(i));
            }

            MediaDerivative row = AddDerivative(
                sourceKind,
                sourceId,
                MediaDerivativeKind.Filmstrip,
                storageKey: directory + leaves[0],
                indexStorageKey: indexKey,
                projectId: owningProjectId);

            return new SeededDensity(row, directory, indexKey, leaves);
        }

        // --- assertions helpers ---------------------------------------------------------------

        /// <summary>
        /// A full text snapshot of every table these tests can write, read through a <b>fresh</b>
        /// context. A fresh context matters: the context that served the request would answer from its
        /// own identity map, so a mutated-but-unsaved entity would look written.
        /// </summary>
        public string StateSnapshot()
        {
            using InferenceApiDbContext fresh = new(OptionsFor(DatabaseName));

            List<MediaBin> bins = fresh.MediaBins.IgnoreQueryFilters().AsNoTracking().ToList();
            List<MediaBinItem> items = fresh.MediaBinItems.AsNoTracking().ToList();
            List<MediaDerivative> derivatives = fresh.MediaDerivatives.IgnoreQueryFilters().AsNoTracking().ToList();
            List<ProjectFile> files = fresh.ProjectFiles.AsNoTracking().ToList();

            return string.Join(
                "\n",
                "bins:" + string.Join("|", bins.OrderBy(b => b.Id).Select(b => $"{b.Id},{b.Name},{b.ParentBinId},{b.SortOrder},{b.DeletedAt}")),
                "items:" + string.Join("|", items.OrderBy(i => i.Id).Select(i => $"{i.Id},{i.BinId},{i.SourceKind},{i.SourceProjectFileId},{i.SourceStepResultId},{i.SortOrder}")),
                "derivatives:" + string.Join("|", derivatives.OrderBy(d => d.Id).Select(d => $"{d.Id},{d.ProjectId},{d.Kind},{d.StorageKey},{d.IndexStorageKey}")),
                "files:" + string.Join("|", files.OrderBy(f => f.Id).Select(f => $"{f.Id},{f.ProjectId},{f.StorageKey},{f.MimeType},{f.SizeBytes}")));
        }

        public void Dispose() => Db.Dispose();
    }

    /// <summary>
    /// The object bytes served for sheet <paramref name="index"/> — distinctive, so a test can prove
    /// WHICH sheet it received rather than merely that it received one.
    /// </summary>
    private static string SheetPayload(int index) => $"sheet-{index}-bytes";

    private static string SheetContentPath(Guid projectId, Guid derivativeId, int index) =>
        $"/api/v1/projects/{projectId:D}/media/derivatives/{derivativeId:D}/sheet/{index}/content";

    private static async Task<List<MediaPoolItemResponse>> PoolAsync(GapFixture fx)
    {
        ActionResult<List<MediaPoolItemResponse>> result =
            await fx.Media.ListMedia(fx.Project.Id, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<List<MediaPoolItemResponse>>().Subject;
    }

    private static MediaProbeResponse? ProbeOf(List<MediaPoolItemResponse> pool, string mediaRef) =>
        pool.Single(item => item.MediaRef == mediaRef).Probe;

    private static MediaFilmstripDocument FilmstripBody(ActionResult<MediaFilmstripResponse> result)
    {
        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<MediaFilmstripResponse>().Subject.Filmstrip;
    }

    private static async Task<List<MediaBinItemDetailResponse>> BinItemsAsync(GapFixture fx, Guid binId)
    {
        ActionResult<List<MediaBinItemDetailResponse>> result =
            await fx.Bins.ListItems(fx.Project.Id, binId, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<List<MediaBinItemDetailResponse>>().Subject;
    }

    private static async Task<(FileStreamResult File, string Body)> SheetBytesAsync(IActionResult result)
    {
        FileStreamResult file = result.Should().BeOfType<FileStreamResult>().Subject;

        await using Stream stream = file.FileStream;
        using StreamReader reader = new(stream, Encoding.UTF8);

        return (file, await reader.ReadToEndAsync());
    }

    /// <summary>
    /// The contract's one failure mode for a media route: <c>404</c>, and <b>never</b>
    /// <see cref="ForbidResult"/>, which would confirm that the project, the mediaRef or the
    /// derivative exists and belongs to somebody else.
    /// </summary>
    private static void ShouldBeNotFoundAndNeverForbidden(IActionResult? result)
    {
        result.Should().NotBeOfType<ForbidResult>();
        result.Should().BeOfType<NotFoundResult>();
    }

    private static string MediaRefOf(ProjectFile file) => $"file:{file.Id:D}";

    // =============================================================================================
    // 1. Probe completeness — the six T-105 fields
    // =============================================================================================

    [Fact]
    public async Task Pool_ReturnsAllSixProbeFields_WithTheArtifactsOwnValues()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();

        fx.AddAnalysisArtifact(new VideoAnalysisSourceInfo(
            SourceIndex: 0,
            StorageKey: file.StorageKey,
            Media: new VideoAnalysisMedia(
                12.5, 30, 1, 1920, 1080,
                VideoCodec: "h264",
                AudioCodec: "aac",
                Container: "mov,mp4,m4a,3gp,3g2,mj2",
                AudioChannels: 2,
                AudioSampleRateHz: 48000,
                BitrateBps: 8_500_000)));

        MediaProbeResponse probe = ProbeOf(await PoolAsync(fx), MediaRefOf(file))
            .Should().BeOfType<MediaProbeResponse>().Subject;

        // The five originals are unchanged, and the six additions carry the artifact's own values.
        probe.DurationSec.Should().Be(12.5);
        probe.FpsNum.Should().Be(30);
        probe.FpsDen.Should().Be(1);
        probe.Width.Should().Be(1920);
        probe.Height.Should().Be(1080);

        probe.VideoCodec.Should().Be("h264");
        probe.AudioCodec.Should().Be("aac");
        probe.Container.Should().Be("mov,mp4,m4a,3gp,3g2,mj2");
        probe.AudioChannels.Should().Be(2);
        probe.AudioSampleRateHz.Should().Be(48000);
        probe.BitrateBps.Should().Be(8_500_000);
    }

    [Fact]
    public async Task Pool_LeavesProbeNull_ForASourceWithNoAnalysisArtifact()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();

        MediaProbeResponse? probe = ProbeOf(await PoolAsync(fx), MediaRefOf(file));

        // Not an object full of zeroes and not a fabricated duration: a source that was never
        // analysed has no reading at all, and that is said as null.
        probe.Should().BeNull();
    }

    [Fact]
    public async Task Pool_ReturnsNullForAllSixFields_OnAnArtifactThatPredatesThem()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();

        // Written VERBATIM: an artifact persisted before these fields existed carries neither the
        // properties nor the values, and the producer's current serializer would always emit them.
        fx.AddRawAnalysisArtifact($$"""
        {
          "version": 2,
          "media": { "durationSec": 12.5, "fpsNum": 30, "fpsDen": 1, "width": 1920, "height": 1080 },
          "shots": [],
          "silenceSpans": [],
          "segments": [],
          "words": [],
          "offeredIds": [],
          "provenance": {
            "transcriptionMode": "Off",
            "transcriptionApplied": false,
            "transcriptionDegraded": false
          },
          "sources": [
            {
              "sourceIndex": 0,
              "storageKey": "{{file.StorageKey}}",
              "media": { "durationSec": 12.5, "fpsNum": 30, "fpsDen": 1, "width": 1920, "height": 1080 }
            }
          ]
        }
        """);

        MediaProbeResponse probe = ProbeOf(await PoolAsync(fx), MediaRefOf(file))
            .Should().BeOfType<MediaProbeResponse>().Subject;

        // The artifact DID mention this source, so the probe exists and its five original readings
        // survive — only the six fields it never probed are absent.
        probe.DurationSec.Should().Be(12.5);
        probe.Width.Should().Be(1920);

        probe.VideoCodec.Should().BeNull();
        probe.AudioCodec.Should().BeNull();
        probe.Container.Should().BeNull();
        probe.AudioChannels.Should().BeNull();
        probe.AudioSampleRateHz.Should().BeNull();
        probe.BitrateBps.Should().BeNull();
    }

    /// <summary>
    /// The sharpest form of the "not known" rule: on the wire an unprobed numeric field is
    /// <c>null</c>, never <c>0</c> — a <c>0</c> channel count or a <c>0</c> bitrate would state
    /// something ffprobe never said.
    /// </summary>
    [Theory]
    [InlineData("audioChannels")]
    [InlineData("audioSampleRateHz")]
    [InlineData("bitrateBps")]
    public async Task Pool_NeverReturnsZeroInPlaceOfNull_ForAnUnprobedNumericField(string field)
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();

        // Two absences in one body: a source with NO artifact at all, and a source whose artifact
        // predates the fields. Both must serialize the field as JSON null.
        ProjectFile legacy = fx.AddFile("legacy.mp4");
        fx.AddRawAnalysisArtifact($$"""
        {
          "version": 2,
          "media": { "durationSec": 4, "fpsNum": 25, "fpsDen": 1, "width": 1280, "height": 720 },
          "shots": [], "silenceSpans": [], "segments": [], "words": [], "offeredIds": [],
          "provenance": { "transcriptionMode": "Off", "transcriptionApplied": false, "transcriptionDegraded": false },
          "sources": [
            {
              "sourceIndex": 0,
              "storageKey": "{{legacy.StorageKey}}",
              "media": { "durationSec": 4, "fpsNum": 25, "fpsDen": 1, "width": 1280, "height": 720 }
            }
          ]
        }
        """);

        List<MediaPoolItemResponse> pool = await PoolAsync(fx);

        string body = JsonSerializer.Serialize(pool, WireJson);
        using JsonDocument document = JsonDocument.Parse(body);

        JsonElement legacyProbe = document.RootElement
            .EnumerateArray()
            .Single(item => item.GetProperty("mediaRef").GetString() == MediaRefOf(legacy))
            .GetProperty("probe");

        legacyProbe.GetProperty(field).ValueKind.Should().Be(JsonValueKind.Null);
        // A 0 would have been a Number here — that is the whole distinction being pinned.
        legacyProbe.GetProperty(field).ValueKind.Should().NotBe(JsonValueKind.Number);

        // And the never-analysed source reports no probe object at all, rather than one full of zeroes.
        JsonElement missingProbe = document.RootElement
            .EnumerateArray()
            .Single(item => item.GetProperty("mediaRef").GetString() == MediaRefOf(file))
            .GetProperty("probe");

        missingProbe.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Pool_ReturnsNullForAllSixFields_ForASourceTheNewestArtifactDoesNotMention()
    {
        using GapFixture fx = new();
        ProjectFile analysed = fx.AddFile("analysed.mp4");
        ProjectFile unmentioned = fx.AddFile("unmentioned.mp4");

        // The one artifact this project produced names only `analysed`, so the other source has a
        // missing reading rather than a wrong one.
        fx.AddAnalysisArtifact(new VideoAnalysisSourceInfo(
            0,
            analysed.StorageKey,
            new VideoAnalysisMedia(9, 24, 1, 1280, 720, "hevc", "opus", "matroska,webm", 6, 44100, 2_000_000)));

        List<MediaPoolItemResponse> pool = await PoolAsync(fx);

        ProbeOf(pool, MediaRefOf(analysed))!.VideoCodec.Should().Be("hevc");
        ProbeOf(pool, MediaRefOf(unmentioned)).Should().BeNull();
    }

    // =============================================================================================
    // 2. Bin membership
    // =============================================================================================

    [Fact]
    public async Task Pool_ReturnsExactlyTheTwoPins_ForASourcePinnedInTwoBins()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();
        ProjectFile other = fx.AddFile("other.mp4");

        MediaBin first = fx.AddBin("First");
        MediaBin second = fx.AddBin("Second");
        MediaBin unrelated = fx.AddBin("Unrelated");

        MediaBinItem firstPin = fx.AddBinItem(first.Id, MediaSourceKind.ProjectFile, file.Id);
        MediaBinItem secondPin = fx.AddBinItem(second.Id, MediaSourceKind.ProjectFile, file.Id);
        fx.AddBinItem(unrelated.Id, MediaSourceKind.ProjectFile, other.Id);

        MediaPoolItemResponse item = (await PoolAsync(fx)).Single(i => i.MediaRef == MediaRefOf(file));

        item.Bins.Should().HaveCount(2);
        item.Bins.Select(pin => (pin.BinId, pin.ItemId)).Should().BeEquivalentTo(
            new[] { (first.Id, firstPin.Id), (second.Id, secondPin.Id) });
        item.Bins.Should().NotContain(pin => pin.BinId == unrelated.Id);
    }

    [Fact]
    public async Task Pool_ReturnsAnEmptyNonNullMembershipList_ForAnUnpinnedSource()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();
        // A bin exists, so an empty list is a real answer about this source rather than "no bins at all".
        fx.AddBin("Empty");

        MediaPoolItemResponse item = (await PoolAsync(fx)).Single(i => i.MediaRef == MediaRefOf(file));

        // "In no bin" is a normal state a panel renders, not a missing value.
        item.Bins.Should().NotBeNull();
        item.Bins.Should().BeEmpty();
    }

    [Fact]
    public async Task Pool_DoesNotReportAPin_WhoseBinIsSoftDeleted()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();

        MediaBin live = fx.AddBin("Live");
        MediaBin deleted = fx.AddBin("Deleted", deletedAt: DateTime.UtcNow);

        MediaBinItem livePin = fx.AddBinItem(live.Id, MediaSourceKind.ProjectFile, file.Id);
        // The pin row itself still exists — the delete route soft-deletes bins and hard-deletes their
        // items, but a row left behind by any other path must not resurrect a deleted bin's contents.
        fx.AddBinItem(deleted.Id, MediaSourceKind.ProjectFile, file.Id);

        MediaPoolItemResponse item = (await PoolAsync(fx)).Single(i => i.MediaRef == MediaRefOf(file));

        item.Bins.Should().ContainSingle();
        item.Bins[0].Should().Be(new MediaBinMembershipResponse(live.Id, livePin.Id));
    }

    [Fact]
    public async Task BinItems_ResolvesNameKindMimeAndSize_ForAPinnedFile()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile("rushcut-a.mp4", "video/mp4", sizeBytes: 4096);
        MediaBin bin = fx.AddBin("Footage");
        MediaBinItem pin = fx.AddBinItem(bin.Id, MediaSourceKind.ProjectFile, file.Id, sortOrder: 0);

        MediaBinItemDetailResponse row = (await BinItemsAsync(fx, bin.Id)).Should().ContainSingle().Subject;

        // The add/move shape's six fields, unchanged, plus the source's own four.
        row.Id.Should().Be(pin.Id);
        row.BinId.Should().Be(bin.Id);
        row.MediaRef.Should().Be(MediaRefOf(file));
        row.SourceKind.Should().Be("ProjectFile");

        row.Kind.Should().Be(MediaKind.Video);
        row.FileName.Should().Be("rushcut-a.mp4");
        row.MimeType.Should().Be("video/mp4");
        row.SizeBytes.Should().Be(4096);
    }

    [Fact]
    public async Task BinItems_ReturnsADanglingPinWithNulls_RatherThanDroppingOrThrowing()
    {
        using GapFixture fx = new();
        MediaBin bin = fx.AddBin("Footage");

        // A pin whose source row was deleted: the id resolves to nothing, in this project or any other.
        Guid vanished = Guid.NewGuid();
        MediaBinItem pin = fx.AddBinItem(bin.Id, MediaSourceKind.ProjectFile, vanished);

        List<MediaBinItemDetailResponse> rows = await BinItemsAsync(fx, bin.Id);

        // The row is still returned, with its id and its removable mediaRef: a pin the client cannot
        // see is a pin the client cannot remove.
        MediaBinItemDetailResponse row = rows.Should().ContainSingle().Subject;
        row.Id.Should().Be(pin.Id);
        row.MediaRef.Should().Be($"file:{vanished:D}");

        row.Kind.Should().BeNull();
        row.FileName.Should().BeNull();
        row.MimeType.Should().BeNull();
        row.SizeBytes.Should().BeNull();
    }

    [Fact]
    public async Task BinItems_ResolvesAStepResultPin_FromItsOwnKey_AndReportsNoSize()
    {
        using GapFixture fx = new();
        WorkflowStepResult render = fx.AddRender("compiled.mp4");
        MediaBin bin = fx.AddBin("Renders");
        fx.AddBinItem(bin.Id, MediaSourceKind.StepResult, render.Id);

        MediaBinItemDetailResponse row = (await BinItemsAsync(fx, bin.Id)).Should().ContainSingle().Subject;

        row.SourceKind.Should().Be("StepResult");
        row.MediaRef.Should().Be($"step_result:{render.Id:D}");
        row.Kind.Should().Be(MediaKind.Video);
        row.FileName.Should().Be("compiled.mp4");
        row.MimeType.Should().Be("video/mp4");

        // A step result records no object size and this endpoint does not HEAD the store to invent
        // one, so the nullable field says "not known" exactly.
        row.SizeBytes.Should().BeNull();
    }

    // =============================================================================================
    // 3. The filmstrip index and its sheet route
    // =============================================================================================

    [Fact]
    public async Task Filmstrip_ListsEverySheetOfADensity_WithTheOwningRowsOwnContentUrl()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();

        // Seeded coarsest-first, so an answer ordered by densitySec ascending cannot be an accident
        // of insertion order.
        SeededDensity coarse = fx.SeedDensity(
            MediaSourceKind.ProjectFile, file.Id, "coarse", "bbbbbbbbbbbbbbbb", 5, 96, 54, 30);
        SeededDensity dense = fx.SeedDensity(
            MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, 100, 55);

        MediaFilmstripDocument document = FilmstripBody(
            await fx.Media.GetFilmstrip(fx.Project.Id, MediaRefOf(file), CancellationToken.None));

        document.Densities.Should().HaveCount(2);

        MediaFilmstripDensityResponse first = document.Densities[0];
        first.DensitySec.Should().Be(2);
        first.ThumbWidth.Should().Be(160);
        first.ThumbHeight.Should().Be(90);
        first.SheetCount.Should().Be(2);
        first.Sheets.Should().Equal(
            new MediaFilmstripSheetResponse(0, 100, 0, SheetContentPath(fx.Project.Id, dense.Row.Id, 0)),
            new MediaFilmstripSheetResponse(100, 55, 200, SheetContentPath(fx.Project.Id, dense.Row.Id, 1)));

        MediaFilmstripDensityResponse second = document.Densities[1];
        second.DensitySec.Should().Be(5);
        second.SheetCount.Should().Be(1);
        second.Sheets.Should().Equal(
            new MediaFilmstripSheetResponse(0, 30, 0, SheetContentPath(fx.Project.Id, coarse.Row.Id, 0)));

        // Each density is addressed through ITS OWN row, never a neighbour's.
        dense.Row.Id.Should().NotBe(coarse.Row.Id);
    }

    /// <summary>
    /// The defect that was caught and fixed: a density is ONE row, so sheets 1..N-1 have no row at
    /// all, and a leaf-to-row lookup would have found nothing for them. This proves the fix stays —
    /// both that every sheet is addressable and that each one really streams its own bytes.
    /// </summary>
    [Fact]
    public async Task Filmstrip_AddressesSheetsOneToNMinusOne_ThoughOnlyThePrimarySheetHasARow()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();

        SeededDensity density = fx.SeedDensity(
            MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, 100, 100, 100);

        // Exactly one row for a three-sheet density — the writer's shape, and the whole reason a
        // leaf-to-row match could never have worked.
        fx.Db.MediaDerivatives.Count(d => d.Kind == MediaDerivativeKind.Filmstrip).Should().Be(1);

        MediaFilmstripDensityResponse resolved = FilmstripBody(
                await fx.Media.GetFilmstrip(fx.Project.Id, MediaRefOf(file), CancellationToken.None))
            .Densities.Should().ContainSingle().Subject;

        resolved.SheetCount.Should().Be(3);
        resolved.Sheets.Should().HaveCount(3);
        resolved.Sheets.Select(sheet => sheet.ContentUrl).Should().Equal(
            SheetContentPath(fx.Project.Id, density.Row.Id, 0),
            SheetContentPath(fx.Project.Id, density.Row.Id, 1),
            SheetContentPath(fx.Project.Id, density.Row.Id, 2));

        // Addressable is not enough: every one of those URLs must serve ITS OWN sheet.
        for (int index = 0; index < 3; index++)
        {
            IActionResult result = await fx.Media.GetFilmstripSheetContent(
                fx.Project.Id, density.Row.Id, index, CancellationToken.None);

            (await SheetBytesAsync(result)).Body.Should().Be(SheetPayload(index));
        }
    }

    [Fact]
    public async Task Filmstrip_OmitsOnlyTheDensityWhoseSidecarIsCorrupt()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();

        SeededDensity good = fx.SeedDensity(
            MediaSourceKind.ProjectFile, file.Id, "good", "aaaaaaaaaaaaaaaa", 2, 160, 90, 100);
        SeededDensity corrupt = fx.SeedDensity(
            MediaSourceKind.ProjectFile, file.Id, "corrupt", "bbbbbbbbbbbbbbbb", 5, 96, 54, 30);

        // The row is intact and the sidecar is reachable; only its JSON is unreadable.
        fx.Put(corrupt.IndexKey, "{ this is not json");

        MediaFilmstripDocument document = FilmstripBody(
            await fx.Media.GetFilmstrip(fx.Project.Id, MediaRefOf(file), CancellationToken.None));

        // One unreadable sidecar must not cost the client the densities that ARE readable.
        MediaFilmstripDensityResponse only = document.Densities.Should().ContainSingle().Subject;
        only.DensitySec.Should().Be(2);
        only.Sheets[0].ContentUrl.Should().Be(SheetContentPath(fx.Project.Id, good.Row.Id, 0));
    }

    [Fact]
    public async Task Filmstrip_Returns200WithAnEmptyDensityList_WhenTheSourceHasNoFilmstripRows()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();
        // A derivative exists, so this is "no filmstrip" rather than "nothing at all".
        fx.AddDerivative(MediaSourceKind.ProjectFile, file.Id, MediaDerivativeKind.Proxy);

        ActionResult<MediaFilmstripResponse> result =
            await fx.Media.GetFilmstrip(fx.Project.Id, MediaRefOf(file), CancellationToken.None);

        MediaFilmstripDocument document = FilmstripBody(result);

        // An honest absence, stated as zero densities rather than as an error or a fabricated one.
        document.Densities.Should().NotBeNull();
        document.Densities.Should().BeEmpty();

        result.Result.Should().BeOfType<OkObjectResult>().Which.StatusCode.Should().Be(200);

        using JsonDocument wire = JsonDocument.Parse(
            JsonSerializer.Serialize(((OkObjectResult)result.Result!).Value, WireJson));

        wire.RootElement.GetProperty("filmstrip").GetProperty("densities").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public async Task Sheet_Returns404NeverForbiddenOrServerError_ForAnOutOfRangeIndex(int index)
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();
        SeededDensity density = fx.SeedDensity(
            MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, 100, 55);

        IActionResult result = await fx.Media.GetFilmstripSheetContent(
            fx.Project.Id, density.Row.Id, index, CancellationToken.None);

        // 404 rather than 500: a density whose sheets have not been uploaded yet is a normal state,
        // not a server fault.
        ShouldBeNotFoundAndNeverForbidden(result);
    }

    [Theory]
    [InlineData("../../evil.jpg")]
    [InlineData("nested/evil.jpg")]
    [InlineData("..\\evil.jpg")]
    public async Task Sheet_Returns404_ForALeafThatCarriesASeparatorOrDotDot(string hostileLeaf)
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();
        SeededDensity density = fx.SeedDensity(
            MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, 100);

        // The row is real and its index parses; only the leaf is hostile, so nothing but the leaf
        // check stands between a corrupt sidecar and an object outside the directory the row owns.
        string escapedLeaf = hostileLeaf.Replace("\\", "\\\\").Replace("\"", "\\\"");
        fx.Put(density.IndexKey, $$"""
        {"densitySec":2,"thumbWidth":160,"thumbHeight":90,"cols":10,"rows":10,"sheetCount":1,
         "sheets":[{"storageLeaf":"{{escapedLeaf}}","firstThumbIndex":0,"thumbCount":100,"firstTimeSec":0}]}
        """);

        IActionResult result = await fx.Media.GetFilmstripSheetContent(
            fx.Project.Id, density.Row.Id, 0, CancellationToken.None);

        ShouldBeNotFoundAndNeverForbidden(result);

        // And the store was never asked for anything the leaf would have built.
        fx.Storage.Verify(
            s => s.DownloadAsync(It.IsAny<Guid>(), It.Is<string>(key => key.Contains("evil")), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Sheet_Returns404_ForARowThatIsNotAFilmstrip()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();
        SeededDensity density = fx.SeedDensity(
            MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, 100);

        // A proxy row that happens to carry the very same index pointer: only a Filmstrip row owns a
        // density, and answering otherwise would be a second, accidental way to read an arbitrary
        // object.
        MediaDerivative proxy = fx.AddDerivative(
            MediaSourceKind.ProjectFile,
            file.Id,
            MediaDerivativeKind.Proxy,
            storageKey: density.Directory + density.Leaves[0],
            indexStorageKey: density.IndexKey,
            mimeType: "video/mp4");

        IActionResult result = await fx.Media.GetFilmstripSheetContent(
            fx.Project.Id, proxy.Id, 0, CancellationToken.None);

        ShouldBeNotFoundAndNeverForbidden(result);
    }

    [Fact]
    public async Task Sheet_Returns404_ForAForeignDerivative_NeverForbidden()
    {
        using GapFixture fx = new();
        ProjectFile foreignFile = fx.AddFile("foreign.mp4", projectId: fx.ForeignProject.Id);

        // A REAL row, addressed with THIS user's own project id: the route must scope the lookup to
        // the project in the path, not merely to a project the caller owns.
        SeededDensity foreign = fx.SeedDensityIn(
            fx.ForeignProject.Id, MediaSourceKind.ProjectFile, foreignFile.Id, "foreign", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100]);

        IActionResult foreignRow = await fx.Media.GetFilmstripSheetContent(
            fx.Project.Id, foreign.Row.Id, 0, CancellationToken.None);

        IActionResult unknownRow = await fx.Media.GetFilmstripSheetContent(
            fx.Project.Id, Guid.NewGuid(), 0, CancellationToken.None);

        // A row of another project is indistinguishable from a row that does not exist, and saying
        // "forbidden" would confirm it is real.
        ShouldBeNotFoundAndNeverForbidden(foreignRow);
        ShouldBeNotFoundAndNeverForbidden(unknownRow);
    }

    [Fact]
    public async Task Sheet_Returns404_WhenTheObjectIsMissingFromStorage()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();
        SeededDensity density = fx.SeedDensity(
            MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100, 55]);

        // The index describes sheet 1 but its object was never uploaded (or was removed): a per-sheet
        // absence that must not cost the client sheet 0 and must not surface as a 500.
        fx.Remove(density.Directory + density.Leaves[1]);

        ShouldBeNotFoundAndNeverForbidden(
            await fx.Media.GetFilmstripSheetContent(fx.Project.Id, density.Row.Id, 1, CancellationToken.None));

        IActionResult sibling = await fx.Media.GetFilmstripSheetContent(
            fx.Project.Id, density.Row.Id, 0, CancellationToken.None);

        (await SheetBytesAsync(sibling)).Body.Should().Be(SheetPayload(0));
    }

    // =============================================================================================
    // 4. Ownership — 404, never 403, and nothing written
    // =============================================================================================

    [Fact]
    public async Task EveryMediaReadRoute_Returns404NotForbidden_ForAForeignProject_AndWritesNothing()
    {
        using GapFixture fx = new();
        // Seeded so every route has something it COULD have returned had ownership not been checked.
        ProjectFile foreignFile = fx.AddFile("foreign.mp4", projectId: fx.ForeignProject.Id);
        MediaBin foreignBin = fx.AddBin("Foreign", projectId: fx.ForeignProject.Id);
        fx.AddBinItem(foreignBin.Id, MediaSourceKind.ProjectFile, foreignFile.Id);
        SeededDensity foreignDensity = fx.SeedDensity(
            MediaSourceKind.ProjectFile, foreignFile.Id, "foreign", "aaaaaaaaaaaaaaaa", 2, 160, 90, 100);

        string before = fx.StateSnapshot();

        ShouldBeNotFoundAndNeverForbidden(
            (await fx.Media.ListMedia(fx.ForeignProject.Id, CancellationToken.None)).Result);
        ShouldBeNotFoundAndNeverForbidden(
            (await fx.Media.GetFilmstrip(fx.ForeignProject.Id, MediaRefOf(foreignFile), CancellationToken.None)).Result);
        ShouldBeNotFoundAndNeverForbidden(
            (await fx.Media.ListDerivatives(fx.ForeignProject.Id, MediaRefOf(foreignFile), CancellationToken.None)).Result);
        ShouldBeNotFoundAndNeverForbidden(
            await fx.Media.GetDerivativeContent(fx.ForeignProject.Id, foreignDensity.Row.Id, CancellationToken.None));
        ShouldBeNotFoundAndNeverForbidden(
            await fx.Media.GetFilmstripSheetContent(fx.ForeignProject.Id, foreignDensity.Row.Id, 0, CancellationToken.None));
        ShouldBeNotFoundAndNeverForbidden(
            (await fx.Bins.ListItems(fx.ForeignProject.Id, foreignBin.Id, CancellationToken.None)).Result);

        // A refused call is a refusal to act: the whole database is byte-identical afterwards.
        fx.StateSnapshot().Should().Be(before);
        fx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task MediaRefRoutes_Return404NotForbidden_ForASourceThatBelongsToAnotherProject()
    {
        using GapFixture fx = new();
        // The current user owns `Project`; this file is real and resolvable, just not here.
        string foreignRef = MediaRefOf(fx.ForeignFile);

        string before = fx.StateSnapshot();

        ShouldBeNotFoundAndNeverForbidden(
            (await fx.Media.ListDerivatives(fx.Project.Id, foreignRef, CancellationToken.None)).Result);
        ShouldBeNotFoundAndNeverForbidden(
            (await fx.Media.GetFilmstrip(fx.Project.Id, foreignRef, CancellationToken.None)).Result);

        IActionResult requested = await fx.Media.RequestDerivatives(
            fx.Project.Id,
            new RequestMediaDerivativesRequest(foreignRef),
            CancellationToken.None);

        ShouldBeNotFoundAndNeverForbidden(requested);
        fx.Published.Should().BeEmpty();
        fx.StateSnapshot().Should().Be(before);
    }

    [Fact]
    public async Task DerivativeContentRoutes_Return404NotForbidden_ForAnotherProjectsDerivative()
    {
        using GapFixture fx = new();
        ProjectFile foreignFile = fx.AddFile("foreign.mp4", projectId: fx.ForeignProject.Id);

        MediaDerivative foreignProxy = fx.AddDerivative(
            MediaSourceKind.ProjectFile,
            foreignFile.Id,
            MediaDerivativeKind.Proxy,
            projectId: fx.ForeignProject.Id,
            mimeType: "video/mp4",
            storageKey: $"projects/{fx.ForeignProject.Id:D}/derivatives/{KeyCanary}/proxy.mp4");

        SeededDensity foreignFilmstrip = fx.SeedDensityIn(
            fx.ForeignProject.Id, MediaSourceKind.ProjectFile, foreignFile.Id, "foreign", "aaaaaaaaaaaaaaaa", 2, 160, 90, [100]);

        string before = fx.StateSnapshot();

        ShouldBeNotFoundAndNeverForbidden(
            await fx.Media.GetDerivativeContent(fx.Project.Id, foreignProxy.Id, CancellationToken.None));
        ShouldBeNotFoundAndNeverForbidden(
            await fx.Media.GetFilmstripSheetContent(fx.Project.Id, foreignFilmstrip.Row.Id, 0, CancellationToken.None));

        fx.StateSnapshot().Should().Be(before);
    }

    [Fact]
    public async Task RequestDerivatives_ForAForeignProject_Is404EvenWhenTheBodyIsMalformed_AndPublishesNothing()
    {
        using GapFixture fx = new();
        string before = fx.StateSnapshot();

        // Ownership is decided before the body is even looked at: a foreign project is 404, never the
        // 400 a malformed mediaRef would earn on a project the caller owns.
        IActionResult result = await fx.Media.RequestDerivatives(
            fx.ForeignProject.Id,
            new RequestMediaDerivativesRequest("not-a-media-ref"),
            CancellationToken.None);

        ShouldBeNotFoundAndNeverForbidden(result);
        fx.Published.Should().BeEmpty();
        fx.StateSnapshot().Should().Be(before);
    }

    // =============================================================================================
    // 5. Upload hardening — a committed upload survives a broker outage
    // =============================================================================================

    [Fact]
    public async Task Upload_WithAThrowingPublisher_StillReturns201AndCommitsTheProjectFileRow()
    {
        using GapFixture fx = new();
        fx.Publisher
            .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("RabbitMQ broker unreachable"));

        ActionResult<ProjectFileResponse> action =
            await fx.Files.Upload(fx.Project.Id, new StubFormFile("rushcut-a.mp4", "video/mp4"), CancellationToken.None);

        ObjectResult result = action.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(201);

        ProjectFileResponse body = result.Value.Should().BeOfType<ProjectFileResponse>().Subject;
        body.MimeType.Should().Be("video/mp4");

        // Read back through a FRESH context: the row must be committed, not merely pending in the
        // controller's own change tracker.
        await using InferenceApiDbContext fresh = new(OptionsFor(fx.DatabaseName));
        ProjectFile? persisted = await fresh.ProjectFiles
            .FirstOrDefaultAsync(f => f.Id == body.Id && f.ProjectId == fx.Project.Id);

        persisted.Should().NotBeNull();
        persisted!.StorageKey.Should().Be(fx.UploadedKey);
        persisted.MimeType.Should().Be("video/mp4");

        // The publish was attempted exactly once and simply lost: it is a hint, not durable state.
        fx.Publisher.Verify(
            p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()),
            Times.Once);
        fx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_WithAWorkingPublisher_PublishesExactlyOneMediaDerivativesRequested()
    {
        using GapFixture fx = new();

        ActionResult<ProjectFileResponse> action =
            await fx.Files.Upload(fx.Project.Id, new StubFormFile("rushcut-a.mp4", "video/mp4"), CancellationToken.None);

        ObjectResult result = action.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(201);
        ProjectFileResponse body = result.Value.Should().BeOfType<ProjectFileResponse>().Subject;

        fx.Publisher.Verify(
            p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()),
            Times.Once);

        MediaDerivativesRequested published = fx.Published.Should().ContainSingle().Subject;
        published.ProjectId.Should().Be(fx.Project.Id);
        published.SourceRef.Should().Be(new MediaSourceRef(MediaSourceKind.ProjectFile, body.Id));
        published.SourceProjectFileId.Should().Be(body.Id);
        published.SourceStorageKey.Should().Be(fx.UploadedKey);
        published.SourceEtag.Should().Be("etag-abc123");
        published.RequestedBy.Should().Be("upload");
        published.Kinds.Should().Equal(MediaDerivativeRequests.DefaultKinds);
    }

    // =============================================================================================
    // 6. No leak — no storage key, no bucket, no object URL, and a frozen derivative shape
    // =============================================================================================

    /// <summary>
    /// Asserts the invariant every response body in this feature shares: the serialized text carries
    /// no storage key, no bucket name and no object URL.
    /// </summary>
    private static void AssertNoStorageSecrets(string body)
    {
        body.Should().NotContain(KeyCanary);
        body.Should().NotContain(BucketCanary);
        body.Should().NotContain("storageKey");
        body.Should().NotContain("storageBucket");
        body.Should().NotContain("bucket");
        body.Should().NotContain("minio.internal");
        body.Should().NotContain("http://");
        body.Should().NotContain("https://");
    }

    [Fact]
    public async Task PoolBody_NeverCarriesAStorageKeyBucketOrObjectUrl()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();
        fx.AddBinItem(fx.AddBin("Footage").Id, MediaSourceKind.ProjectFile, file.Id);

        // A render too, whose storage key is its only identity.
        fx.AddRender("compiled.mp4");

        string body = JsonSerializer.Serialize(await PoolAsync(fx), WireJson);

        AssertNoStorageSecrets(body);
        // The paths it DOES send are this API's own.
        body.Should().Contain($"/api/v1/projects/{fx.Project.Id:D}/media/");
    }

    [Fact]
    public async Task FilmstripBody_NeverCarriesAStorageKeyBucketOrObjectUrl()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();
        fx.SeedDensity(MediaSourceKind.ProjectFile, file.Id, "dense", "aaaaaaaaaaaaaaaa", 2, 160, 90, 100, 55);

        ActionResult<MediaFilmstripResponse> result =
            await fx.Media.GetFilmstrip(fx.Project.Id, MediaRefOf(file), CancellationToken.None);

        string body = JsonSerializer.Serialize(((OkObjectResult)result.Result!).Value, WireJson);

        AssertNoStorageSecrets(body);
        // The sidecar's storageLeaf never leaves the service either — only the resolved route does.
        body.Should().NotContain("filmstrip-aaaaaaaaaaaaaaaa-sheet");
        body.Should().Contain("/sheet/0/content");
    }

    [Fact]
    public async Task DerivativeAndBinItemBodies_NeverCarryAStorageKeyBucketOrObjectUrl()
    {
        using GapFixture fx = new();
        ProjectFile file = fx.AddFile();

        MediaDerivative proxy = fx.AddDerivative(
            MediaSourceKind.ProjectFile,
            file.Id,
            MediaDerivativeKind.Proxy,
            storageKey: $"projects/{fx.Project.Id:D}/derivatives/{KeyCanary}/proxy.mp4",
            mimeType: "video/mp4");

        MediaBin bin = fx.AddBin("Footage");
        fx.AddBinItem(bin.Id, MediaSourceKind.ProjectFile, file.Id);

        ActionResult<List<MediaDerivativeResponse>> derivatives = await fx.Media.ListDerivatives(
            fx.Project.Id, MediaRefOf(file), CancellationToken.None);

        string derivativeBody = JsonSerializer.Serialize(
            ((OkObjectResult)derivatives.Result!).Value, WireJson);

        AssertNoStorageSecrets(derivativeBody);
        derivativeBody.Should().Contain($"/media/derivatives/{proxy.Id:D}/content");

        string binBody = JsonSerializer.Serialize(await BinItemsAsync(fx, bin.Id), WireJson);

        AssertNoStorageSecrets(binBody);
        // The file's display name is a base name, never the key it lives under.
        binBody.Should().Contain("clip.mp4");
    }

    /// <summary>
    /// The derivative row's shape is frozen: the filmstrip work must not have appended an index
    /// pointer, an index URL or anything else to the one response CLIENTS already parse.
    /// </summary>
    [Fact]
    public void MediaDerivativeResponse_CarriesExactlyTheFrozenFieldSet()
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

        // The kind still writes as the contract's camelCase string, not as an ordinal.
        document.RootElement.GetProperty("kind").GetString().Should().Be("filmstrip");
    }
}
