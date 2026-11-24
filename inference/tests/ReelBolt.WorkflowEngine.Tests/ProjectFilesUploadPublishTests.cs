#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The upload endpoint's post-commit epilogue: a <c>video/*</c>/<c>audio/*</c> upload requests its
/// derivative set by publishing <see cref="MediaDerivativesRequested"/> on the request path, and
/// that publish happens <b>after</b> the <c>project_files</c> row is committed.
///
/// <para>
/// That ordering is the whole point of this file. A broker outage must not be able to fail an upload
/// whose bytes are stored and whose row is durable — otherwise the client reads a 500 as "upload
/// failed", retries, and stores the same footage twice. So the two tests that matter here are
/// <see cref="Upload_WhenPublishThrows_StillReturns201AndCommitsTheProjectFileRow"/> (201 AND the
/// row is still there, read back through a <b>fresh</b> <see cref="DbContext"/>, not the controller's
/// own change tracker) and <see cref="Upload_WhenPublishSucceeds_PublishesExactlyOneRequestForTheUploadedFile"/>
/// (the working path is unchanged: exactly one event, tagged
/// <c>RequestedBy == "upload"</c>).
/// </para>
/// <para>
/// The third test pins the deliberate <b>exception</b> to that tolerance: a publish that throws
/// because the request was cancelled is rethrown, never converted into a success.
/// </para>
/// </summary>
public class ProjectFilesUploadPublishTests
{
    // -------------------------------------------------------------------------------------------
    // Harness
    // -------------------------------------------------------------------------------------------

    private static DbContextOptions<InferenceApiDbContext> OptionsFor(string databaseName) =>
        new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

    /// <summary>The storage key the mocked object store reports for every upload.</summary>
    private const string StoredKey = "projects/p/userFiles/rushcut-a.mp4";

    /// <summary>
    /// A context whose commit fails, so the acceptance clause "the catch must not swallow anything
    /// raised at <c>SaveChangesAsync</c>" can be proven against a real <see cref="DbUpdateException"/>
    /// rather than a stand-in.
    /// </summary>
    private sealed class FailingCommitContext(DbContextOptions<InferenceApiDbContext> options)
        : InferenceApiDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<int>(new DbUpdateException("project_files row could not be written"));
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
    /// The smallest honest <see cref="IFormFile"/>: the endpoint opens a read stream off it and the
    /// storage service is mocked, so nothing ever copies these bytes anywhere.
    /// </summary>
    private sealed class FakeFormFile : IFormFile
    {
        private readonly byte[] _content;

        public FakeFormFile(string fileName, string contentType, byte[]? content = null)
        {
            FileName = fileName;
            ContentType = contentType;
            _content = content ?? [0x00, 0x00, 0x00, 0x18];
        }

        public string ContentType { get; }

        public string ContentDisposition => $"form-data; name=\"file\"; filename=\"{FileName}\"";

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public long Length => _content.Length;

        public string Name => "file";

        public string FileName { get; }

        public Stream OpenReadStream() => new MemoryStream(_content, writable: false);

        public void CopyTo(Stream target) => throw new NotSupportedException();

        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(bool commitFails = false)
        {
            UserId = Guid.NewGuid();
            DatabaseName = $"project-files-upload-{Guid.NewGuid()}";

            Project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "Media Project",
                OwnerId = UserId,
                OrganizationId = TestTenancy.OrgOf(UserId),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Seeded through a THROWAWAY context: an in-memory query reads the store, not the change
            // tracker, so a row added-but-never-saved would make Upload 404 long before it could
            // reach the commit this fixture means to fail.
            using (InferenceApiDbContext seeder = new(OptionsFor(DatabaseName)))
            {
                seeder.Projects.Add(Project);
                seeder.SaveChanges();
            }

            if (commitFails)
            {
                FailingDb = new FailingCommitContext(OptionsFor(DatabaseName));
                Db = FailingDb;
            }
            else
            {
                Db = new InferenceApiDbContext(OptionsFor(DatabaseName));
            }

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
                    StoredKey,
                    "reelforge",
                    "projects/p/userFiles",
                    null,
                    "userFiles",
                    "rushcut-a.mp4",
                    "rushcut-a.mp4",
                    null,
                    "rushcut-a.mp4"));

            Storage
                .Setup(s => s.GetObjectETagAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("etag-abc123");

            Controller = new ProjectFilesController(
                Db,
                new FakeCurrentUser(UserId),
                TestTenancy.Access(Db, new FakeCurrentUser(UserId)),
                Storage.Object,
                SummarizationQueue.Object,
                Publisher.Object,
                // Upload never touches the vector-search read path; the ctor needs the instance only
                // because every other action on this controller does.
                null!,
                logger: NullLogger<ProjectFilesController>.Instance);

            // Request.Form is read for the optional relativePath, and ControllerBase throws without
            // an HttpContext — so one is always attached, with an empty form, exactly as MVC would.
            DefaultHttpContext httpContext = new();
            httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>());
            Controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        }

        public Guid UserId { get; }

        public string DatabaseName { get; }

        public InferenceApiDbContext Db { get; }

        /// <summary>Non-null only when this fixture deliberately fails every commit.</summary>
        public FailingCommitContext? FailingDb { get; }

        public Project Project { get; }

        public Mock<IFileStorageService> Storage { get; } = new();

        public Mock<IBackgroundTaskQueue<FileSummarizationTask>> SummarizationQueue { get; } = new();

        public Mock<IPublishEndpoint> Publisher { get; } = new();

        public List<MediaDerivativesRequested> Published { get; } = [];

        public ProjectFilesController Controller { get; }

        /// <summary>A second context over the same database — the only honest way to ask whether the
        /// row was really committed rather than merely tracked in memory.</summary>
        public InferenceApiDbContext NewContext() => new(OptionsFor(DatabaseName));

        public void PublishCapturesThenSucceeds() =>
            Publisher
                .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
                .Callback<MediaDerivativesRequested, CancellationToken>((message, _) => Published.Add(message))
                .Returns(Task.CompletedTask);

        public void PublishThrows(Exception failure) =>
            Publisher
                .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(failure);

        public void Dispose() => Db.Dispose();
    }

    private static FakeFormFile VideoFile() => new("rushcut-a.mp4", "video/mp4");

    private static async Task<ObjectResult> UploadAndReadResultAsync(Fixture fixture, CancellationToken ct = default)
    {
        ActionResult<ProjectFileResponse> result =
            await fixture.Controller.Upload(fixture.Project.Id, VideoFile(), ct);
        return result.Result.Should().BeOfType<ObjectResult>().Subject;
    }

    // -------------------------------------------------------------------------------------------
    // The defect: a broker outage must not fail a durable upload
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Upload_WhenPublishThrows_StillReturns201AndCommitsTheProjectFileRow()
    {
        using Fixture fixture = new();
        // Any throw from an awaited publish — the catch is deliberately type-agnostic, because a
        // transport outage can surface as any of several MassTransit/RabbitMQ exception types.
        fixture.PublishThrows(new InvalidOperationException("RabbitMQ broker unreachable"));

        ObjectResult result = await UploadAndReadResultAsync(fixture);

        result.StatusCode.Should().Be(201);
        ProjectFileResponse body = result.Value.Should().BeOfType<ProjectFileResponse>().Subject;
        body.MimeType.Should().Be("video/mp4");

        // Read back through a FRESH context: the row must be committed, not merely pending in the
        // controller's own change tracker.
        await using InferenceApiDbContext fresh = fixture.NewContext();
        ProjectFile? persisted = await fresh.ProjectFiles
            .FirstOrDefaultAsync(f => f.Id == body.Id && f.ProjectId == fixture.Project.Id);

        persisted.Should().NotBeNull();
        persisted!.StorageKey.Should().Be(StoredKey);
        persisted.MimeType.Should().Be("video/mp4");

        // The publish was attempted exactly once and simply lost — it is a hint, not durable state.
        fixture.Publisher.Verify(
            p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()),
            Times.Once);
        fixture.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_WhenPublishSucceeds_PublishesExactlyOneRequestForTheUploadedFile()
    {
        using Fixture fixture = new();
        fixture.PublishCapturesThenSucceeds();

        ObjectResult result = await UploadAndReadResultAsync(fixture);

        result.StatusCode.Should().Be(201);
        ProjectFileResponse body = result.Value.Should().BeOfType<ProjectFileResponse>().Subject;

        fixture.Publisher.Verify(
            p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()),
            Times.Once);
        MediaDerivativesRequested published = fixture.Published.Should().ContainSingle().Subject;
        published.RequestedBy.Should().Be(MediaDerivativeRequests.RequestedByUpload);
        published.RequestedBy.Should().Be("upload");
        published.ProjectId.Should().Be(fixture.Project.Id);
        published.SourceRef.Should().Be(new MediaSourceRef(MediaSourceKind.ProjectFile, body.Id));
        published.SourceProjectFileId.Should().Be(body.Id);
        published.SourceStorageKey.Should().Be(body.StorageKey);
        published.SourceEtag.Should().Be("etag-abc123");
        published.Kinds.Should().Equal(MediaDerivativeRequests.DefaultKinds);
    }

    // -------------------------------------------------------------------------------------------
    // The exception to the tolerance: cancellation is never a success
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Upload_WhenTheRequestIsCancelledDuringPublish_DoesNotReportSuccess()
    {
        using Fixture fixture = new();
        using CancellationTokenSource cts = new();

        // Cancelled by the publish itself, so the row is committed normally and the ONLY thing in
        // play is the media branch's cancellation guard.
        fixture.Publisher
            .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fixture.Controller.Upload(fixture.Project.Id, VideoFile(), cts.Token));
    }

    // -------------------------------------------------------------------------------------------
    // The catch is narrow: a failure BEFORE the commit is untouched by it
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Upload_WhenTheCommitFails_StillPropagatesAndPublishesNothing()
    {
        using Fixture fixture = new(commitFails: true);
        fixture.PublishCapturesThenSucceeds();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.Controller.Upload(fixture.Project.Id, VideoFile(), CancellationToken.None));

        fixture.Published.Should().BeEmpty();
    }
}
