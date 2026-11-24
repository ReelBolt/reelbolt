#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The storage quota across replicas on the in-memory provider (T-114, plan Task 3 step 3). Two replicas
/// are two <see cref="OrganizationStorageLock"/> instances over one store, each driving its own
/// <see cref="ProjectFilesController"/>; the in-memory provider has no advisory lock, so the per-replica
/// semaphore is the only serialisation here and these pin the failure modes the plan's review focus names:
/// a refusal changes nothing and releases the organisation, two replicas deleting one file leave one copy
/// per picture with no orphan, a cancelled acquire leaves the organisation acquirable, and a folder delete
/// that fails part-way keeps the workspace and releases the guard.
/// </summary>
public class StorageQuotaCrossReplicaTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_refused_delete_leaves_no_copy_and_no_row_change_and_unlocks_the_organisation()
    {
        byte[] photo = EditImageDocumentControllersTests.PngOfSize(400, 300);
        Fixture fixture = new();
        Guid projectId = await fixture.SeedProjectAsync();
        (Guid fileId, string originalKey) = await fixture.SeedFileAsync(projectId, "shared.png", photo);
        fixture.Storage.Objects[originalKey] = photo;
        for (int i = 0; i < 3; i++) await fixture.AddPictureAsync(projectId, fileId, originalKey, "shared.png");
        (Guid lonely, string lonelyKey) = await fixture.SeedFileAsync(projectId, "lonely.png", photo);
        fixture.Storage.Objects[lonelyKey] = photo;

        OrganizationStorageLock replica = new();
        await using InferenceApiDbContext db = fixture.NewDb();
        // three pictures need three copies; deleting the original frees one, so the NET growth is two photos
        ProjectFilesController files = fixture.Files(db, replica, fixture.Storage, new RefusingGate(bytesLeft: (2 * photo.Length) - 1));

        (int status, JsonElement body) = await Delete(files, projectId, fileId);

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be(PictureSourceCopies.OverQuotaCode);
        fixture.Storage.Copies.Should().BeEmpty("nothing is copied before the refusal");
        fixture.Storage.Objects.Should().ContainKey(originalKey, "and nothing is deleted");
        await using (InferenceApiDbContext read = fixture.NewDb())
        {
            (await read.ProjectFiles.AnyAsync(f => f.Id == fileId)).Should().BeTrue();
            (await read.EditImageLayers
                .Where(l => read.EditImageDocuments.Any(d => d.Id == l.DocumentId && d.ProjectId == projectId))
                .AllAsync(l => l.StorageKey == originalKey && l.CopySizeBytes == null)).Should().BeTrue("no row changed");
        }

        // The refusal returned before the commit and released the organisation: the very next delete of the
        // same organisation completes (a leaked semaphore would hang it past the 2 s token).
        using CancellationTokenSource next = new(TimeSpan.FromSeconds(2));
        (await Delete(files, projectId, lonely, next.Token)).Status.Should().Be(204);
    }

    [Fact]
    public async Task Two_replicas_deleting_the_same_file_leave_one_copy_that_exists()
    {
        byte[] photo = EditImageDocumentControllersTests.PngOfSize(400, 300);
        Fixture fixture = new();
        Guid projectId = await fixture.SeedProjectAsync();
        (Guid fileId, string originalKey) = await fixture.SeedFileAsync(projectId, "shared.png", photo);
        fixture.Storage.Objects[originalKey] = photo;
        for (int i = 0; i < 2; i++) await fixture.AddPictureAsync(projectId, fileId, originalKey, "shared.png");

        // A quota that fits one delete exactly: the winner's two copies are what the loser's check then sees.
        TwoReplicaGate gate = new(() => fixture.NewDb(), limitBytes: 2L * photo.Length);
        OrganizationStorageLock lockA = new();
        OrganizationStorageLock lockB = new();
        await using InferenceApiDbContext dbA = fixture.NewDb();
        await using InferenceApiDbContext dbB = fixture.NewDb();
        ProjectFilesController filesA = fixture.Files(dbA, lockA, fixture.Storage, gate);
        ProjectFilesController filesB = fixture.Files(dbB, lockB, fixture.Storage, gate);

        // Replica A reaches the check first and waits there; replica B reads the same file and reaches the
        // check second, seeing the layers still on the original key. Only then does A's delete commit.
        Task<(int Status, JsonElement Body)> a = Delete(filesA, projectId, fileId);
        await gate.FirstArrived;
        Task<(int Status, JsonElement Body)> b = Delete(filesB, projectId, fileId);
        await gate.SecondArrived;

        gate.ReleaseFirst();
        (int statusA, _) = await a;
        statusA.Should().Be(204, "the first replica's delete fits the quota");

        gate.ReleaseSecond();
        (int statusB, JsonElement bodyB) = await b;
        statusB.Should().Be(409, "the second replica's check now sees the first replica's committed copies");
        bodyB.GetProperty("code").GetString().Should().Be(PictureSourceCopies.OverQuotaCode);

        fixture.Storage.Copies.Should().HaveCount(2).And.OnlyContain(c => c.From == originalKey);
        fixture.Storage.Objects.Should().NotContainKey(originalKey, "the winning replica deleted the original");
        await using (InferenceApiDbContext read = fixture.NewDb())
        {
            List<EditImageLayer> bitmap = await read.EditImageLayers
                .Where(l => l.Kind != EditImageLayerKind.Text
                            && read.EditImageDocuments.Any(d => d.Id == l.DocumentId && d.ProjectId == projectId))
                .ToListAsync();
            bitmap.Should().NotBeEmpty();
            bitmap.Should().OnlyContain(l => fixture.Storage.Objects.ContainsKey(l.StorageKey), "every layer points at a copy that exists");
            List<string> copies = bitmap.Select(l => l.StorageKey).Distinct().ToList();
            copies.Should().HaveCount(2, "one copy per picture, shared by its layers");
            fixture.Storage.Objects.Keys.Where(k => k.Contains("/agentFiles/image-editor/"))
                .Should().BeEquivalentTo(copies, "no copy is orphaned");
            (await read.ProjectFiles.AnyAsync(f => f.Id == fileId)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task A_cancelled_acquire_leaves_the_organisation_acquirable()
    {
        Fixture fixture = new();
        Guid projectId = await fixture.SeedProjectAsync();
        (Guid fileId, string key) = await fixture.SeedFileAsync(projectId, "lonely.png", EditImageDocumentControllersTests.PngOfSize(400, 300));
        fixture.Storage.Objects[key] = EditImageDocumentControllersTests.PngOfSize(400, 300);
        OrganizationStorageLock replica = new();

        await using InferenceApiDbContext held = fixture.NewDb();
        await using OrganizationStorageGuard guard = await replica.AcquireAsync(held, fixture.OrganizationId, CancellationToken.None);

        await using InferenceApiDbContext waiting = fixture.NewDb();
        using CancellationTokenSource cancelled = new(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => replica.AcquireAsync(waiting, fixture.OrganizationId, cancelled.Token));

        await guard.DisposeAsync();

        await using InferenceApiDbContext db = fixture.NewDb();
        ProjectFilesController files = fixture.Files(db, replica, fixture.Storage);
        using CancellationTokenSource token = new(TimeSpan.FromSeconds(2));
        (await Delete(files, projectId, fileId, token.Token)).Status.Should().Be(
            204, "a cancelled acquire left the organisation acquirable");
    }

    [Fact]
    public async Task A_folder_delete_that_fails_part_way_keeps_the_workspace_and_releases_the_guard()
    {
        byte[] photo = EditImageDocumentControllersTests.PngOfSize(400, 300);
        Fixture fixture = new();
        Guid projectId = await fixture.SeedProjectAsync();
        (Guid fileId, string originalKey) = await fixture.SeedFileAsync(projectId, "shoot/a.png", photo, directory: "shoot");
        for (int i = 0; i < 3; i++) await fixture.AddPictureAsync(projectId, fileId, originalKey, "a.png");

        // The second picture's copy throws: the first copy has already been made, so the cleanup must remove it.
        TestStorage storage = new(failCopyOn: 2);
        storage.Objects[originalKey] = photo;
        OrganizationStorageLock replica = new();
        await using InferenceApiDbContext db = fixture.NewDb();
        ProjectFilesController files = fixture.Files(db, replica, storage);

        await Assert.ThrowsAsync<InvalidOperationException>(() => files.DeleteFolder(
            projectId, new FolderDeleteRequest("shoot", Recursive: true), CancellationToken.None));

        storage.Copies.Should().HaveCount(1, "the first copy was made before the second picture failed");
        storage.Objects.Keys.Should().NotContain(k => k.Contains("/agentFiles/image-editor/"), "the first copy was removed again");
        await using (InferenceApiDbContext read = fixture.NewDb())
        {
            (await read.ProjectFiles.AnyAsync(f => f.Id == fileId)).Should().BeTrue("the file row is still there");
            (await read.EditImageLayers
                .Where(l => read.EditImageDocuments.Any(d => d.Id == l.DocumentId && d.ProjectId == projectId))
                .AllAsync(l => l.StorageKey == originalKey && l.CopySizeBytes == null)).Should().BeTrue("no layer was repointed");
        }

        // The failed delete released the guard (a leaked one would hang this past the 2 s token).
        await using InferenceApiDbContext again = fixture.NewDb();
        using CancellationTokenSource token = new(TimeSpan.FromSeconds(2));
        await using OrganizationStorageGuard released = await replica.AcquireAsync(again, fixture.OrganizationId, token.Token);
        released.Should().NotBeNull();
    }

    /// <summary>
    /// T-106 finding 3: <c>_fileStorage.DeleteAsync(original)</c> runs before the row save and the commit, so a
    /// client that disconnects the moment the object is gone must not roll the rows back — that would leave a
    /// file row pointing at a deleted object. The tail save and the commit are uncancellable once the objects
    /// are deleted, so the delete still completes.
    /// </summary>
    [Fact]
    public async Task A_delete_whose_object_delete_cancels_the_request_still_removes_the_row()
    {
        byte[] photo = EditImageDocumentControllersTests.PngOfSize(400, 300);
        Fixture fixture = new();
        Guid projectId = await fixture.SeedProjectAsync();
        (Guid fileId, string originalKey) = await fixture.SeedFileAsync(projectId, "shot.png", photo);
        for (int i = 0; i < 2; i++) await fixture.AddPictureAsync(projectId, fileId, originalKey, "shot.png");

        using CancellationTokenSource disconnect = new();
        TestStorage storage = new() { CancelOnDelete = disconnect };
        storage.Objects[originalKey] = photo;
        OrganizationStorageLock replica = new();
        await using InferenceApiDbContext db = fixture.NewDb();
        ProjectFilesController files = fixture.Files(db, replica, storage);

        (int status, _) = await Delete(files, projectId, fileId, disconnect.Token);

        status.Should().Be(204, "the row removal must not be rolled back by the disconnect that followed the object delete");
        disconnect.IsCancellationRequested.Should().BeTrue("the fake cancelled the token exactly when the object was deleted");
        await using (InferenceApiDbContext read = fixture.NewDb())
        {
            (await read.ProjectFiles.AnyAsync(f => f.Id == fileId)).Should().BeFalse("the row went with the object it pointed at");
            List<EditImageLayer> bitmap = await read.EditImageLayers
                .Where(l => l.Kind != EditImageLayerKind.Text
                            && read.EditImageDocuments.Any(d => d.Id == l.DocumentId && d.ProjectId == projectId))
                .ToListAsync();
            bitmap.Should().NotBeEmpty();
            bitmap.Should().OnlyContain(l => storage.Objects.ContainsKey(l.StorageKey), "every layer points at a copy that exists");
        }
    }

    private static async Task<(int Status, JsonElement Body)> Delete(
        ProjectFilesController files, Guid projectId, Guid fileId, CancellationToken ct = default) =>
        Read(await files.Delete(projectId, fileId, ct));

    private static (int Status, JsonElement Body) Read(IActionResult result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, JsonSerializer.SerializeToElement(o.Value, Web)),
        NoContentResult => (204, default),
        StatusCodeResult s => (s.StatusCode, default),
        _ => throw new InvalidOperationException($"Unexpected result {result.GetType().Name}")
    };

    /// <summary>One in-memory database shared by every context of the test, with the seed helpers the delete path needs.</summary>
    /// <summary>
    /// T-106 follow-up: the indexing-delete event is the only record that a deleted file's vector chunks must
    /// go with it, so a client that disconnects after the commit must not drop it. The publish is uncancellable
    /// for the same reason the row save and the commit are.
    /// </summary>
    [Fact]
    public async Task A_delete_whose_request_token_is_cancelled_after_the_commit_still_publishes_the_indexing_delete()
    {
        byte[] photo = EditImageDocumentControllersTests.PngOfSize(400, 300);
        Fixture fixture = new();
        Guid projectId = await fixture.SeedProjectAsync();
        (Guid fileId, string originalKey) = await fixture.SeedFileAsync(projectId, "shot.png", photo);
        await fixture.AddPictureAsync(projectId, fileId, originalKey, "shot.png");

        List<ProjectFileIndexingRequested> published = [];
        Mock<IPublishEndpoint> publish = new();
        // Token-aware on purpose: the real endpoint refuses a cancelled token, so a mock that ignored it would
        // pass whether the call site publishes uncancellably or not.
        publish.Setup(p => p.Publish(It.IsAny<ProjectFileIndexingRequested>(), It.IsAny<CancellationToken>()))
            .Returns((ProjectFileIndexingRequested message, CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                published.Add(message);
                return Task.CompletedTask;
            });

        using CancellationTokenSource disconnect = new();
        TestStorage storage = new() { CancelOnDelete = disconnect };
        storage.Objects[originalKey] = photo;
        OrganizationStorageLock replica = new();
        await using InferenceApiDbContext db = fixture.NewDb();
        ProjectFilesController files = fixture.Files(db, replica, storage, publish: publish.Object);

        (int status, _) = await Delete(files, projectId, fileId, disconnect.Token);

        status.Should().Be(204);
        disconnect.IsCancellationRequested.Should().BeTrue("the fake cancelled the token when the object went");
        published.Should().ContainSingle("the invalidation is the one thing a disconnect must not drop");
        published[0].FileId.Should().Be(fileId);
        published[0].Operation.Should().Be("Delete");
    }


    /// <summary>
    /// T-106 follow-up: a delete whose object removal fails transiently still answers success, and it may only
    /// do so because the deletion mark written in the same transaction as the row removal is durable - the
    /// retention sweep retries from it. Without the mark the success would hide surviving bytes.
    /// </summary>
    [Fact]
    public async Task A_delete_whose_object_removal_fails_transiently_leaves_a_durable_mark()
    {
        byte[] photo = EditImageDocumentControllersTests.PngOfSize(400, 300);
        Fixture fixture = new();
        Guid projectId = await fixture.SeedProjectAsync();
        (Guid fileId, string originalKey) = await fixture.SeedFileAsync(projectId, "shot.png", photo);
        TestStorage storage = new()
        {
            DeleteFailure = new Amazon.S3.AmazonS3Exception("throttled") { StatusCode = System.Net.HttpStatusCode.ServiceUnavailable }
        };
        storage.Objects[originalKey] = photo;
        OrganizationStorageLock replica = new();
        await using InferenceApiDbContext db = fixture.NewDb();
        ProjectFilesController files = fixture.Files(db, replica, storage);

        (int status, _) = await Delete(files, projectId, fileId, CancellationToken.None);

        status.Should().Be(204, "the rows are gone, and the mark is what makes that answer honest");
        await using InferenceApiDbContext read = fixture.NewDb();
        StorageRetentionMark mark = await read.StorageRetentionMarks.SingleAsync(m => m.StorageKey == originalKey);
        mark.PurgedAt.Should().BeNull("the sweep retries an unpurged mark");
        mark.Category.Should().Be("userFiles", "a mark written by a user's delete is honoured whatever its category");
    }

    /// <summary>
    /// T-106 follow-up: a scope error is not a transient provider failure. Swallowing it would report a delete
    /// whose object may survive with no durable record, so it surfaces.
    /// </summary>
    [Fact]
    public async Task A_delete_whose_object_removal_is_a_scope_error_does_not_answer_success()
    {
        byte[] photo = EditImageDocumentControllersTests.PngOfSize(400, 300);
        Fixture fixture = new();
        Guid projectId = await fixture.SeedProjectAsync();
        (Guid fileId, string originalKey) = await fixture.SeedFileAsync(projectId, "shot.png", photo);
        TestStorage storage = new() { DeleteFailure = new UnauthorizedAccessException("outside the project's storage scope") };
        storage.Objects[originalKey] = photo;
        OrganizationStorageLock replica = new();
        await using InferenceApiDbContext db = fixture.NewDb();
        var publish = new Mock<IPublishEndpoint>();
        publish.Setup(p => p.Publish(It.IsAny<ProjectFileIndexingRequested>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        ProjectFilesController files = fixture.Files(db, replica, storage, publish: publish.Object);

        await FluentActions.Awaiting(() => Delete(files, projectId, fileId, CancellationToken.None))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        // The row is already gone, so the vector chunks must still go: the index delete is queued even
        // when the object delete throws (the row is what would otherwise be the only handle on them).
        publish.Verify(p => p.Publish(
            It.Is<ProjectFileIndexingRequested>(e => e.Operation == "Delete" && e.FileId == fileId),
            It.IsAny<CancellationToken>()), Times.Once);
    }


    private sealed class Fixture
    {
        private readonly string _database = Guid.NewGuid().ToString();

        public Guid OwnerId { get; } = Guid.NewGuid();

        public Guid OrganizationId => TestTenancy.OrgOf(OwnerId);

        public TestStorage Storage { get; } = new();

        public InferenceApiDbContext NewDb() => new(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(_database).Options);

        public ProjectFilesController Files(
            InferenceApiDbContext db, OrganizationStorageLock storageLock, IFileStorageService storage,
            IPlanGateService? gate = null, IPublishEndpoint? publish = null)
        {
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(OwnerId);
            user.SetupGet(u => u.IsAuthenticated).Returns(true);
            user.SetupGet(u => u.OrganizationId).Returns(OrganizationId);
            user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);
            return new ProjectFilesController(
                db, user.Object, TestTenancy.Access(db, user.Object), storage,
                new ChannelBackgroundTaskQueue<FileSummarizationTask>(16), publish ?? Mock.Of<IPublishEndpoint>(),
                new VectorSearchQueryService(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(), Mock.Of<IVectorIndexService>()),
                gate, storageLock: storageLock)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        public async Task<Guid> SeedProjectAsync()
        {
            await using InferenceApiDbContext db = NewDb();
            Project project = new()
            {
                Id = Guid.NewGuid(),
                Name = "P",
                OwnerId = OwnerId,
                OrganizationId = OrganizationId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            return project.Id;
        }

        public async Task<(Guid Id, string Key)> SeedFileAsync(Guid projectId, string path, byte[] bytes, string? directory = null)
        {
            await using InferenceApiDbContext db = NewDb();
            string name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                OriginalFileName = name,
                OriginalPath = path,
                DirectoryPath = directory,
                StorageKey = $"projects/{projectId}/userFiles/{path}",
                StorageBucket = "test-bucket",
                MimeType = "image/png",
                SizeBytes = bytes.Length,
                UploadedAt = DateTime.UtcNow
            };
            db.ProjectFiles.Add(file);
            await db.SaveChangesAsync();
            return (file.Id, file.StorageKey);
        }

        /// <summary>A picture (image-editor document) with one Source layer on the file's storage key.</summary>
        public async Task AddPictureAsync(Guid projectId, Guid fileId, string storageKey, string fileName)
        {
            await using InferenceApiDbContext db = NewDb();
            Guid id = Guid.NewGuid();
            EditImageDocument document = new()
            {
                Id = id,
                ProjectId = projectId,
                SourceProjectFileId = fileId,
                CanvasWidth = 400,
                CanvasHeight = 300,
                Revision = 1
            };
            document.Layers.Add(new EditImageLayer
            {
                Id = Guid.NewGuid(),
                DocumentId = id,
                ZOrder = 1,
                Role = EditImageLayerRoles.Background,
                Kind = EditImageLayerKind.Source,
                Label = fileName,
                SourceProjectFileId = fileId,
                StorageKey = storageKey,
                StorageBucket = "test-bucket",
                MimeType = "image/png",
                TransformJson = "{}"
            });
            db.EditImageDocuments.Add(document);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// A usage-aware gate for two replicas: it lets the first arrival through only once the second has
    /// reached the check, then holds the second until the test releases it — so the second replica's check
    /// runs after the first replica's delete has committed, the order the advisory lock makes on PostgreSQL.
    /// The second's wait is bounded (never an unbounded wait, which the fixed code would deadlock).
    /// </summary>
    private sealed class TwoReplicaGate(Func<InferenceApiDbContext> newDb, long limitBytes) : IPlanGateService
    {
        private int _arrivals;
        private readonly TaskCompletionSource _firstArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstArrived => _firstArrived.Task;

        public Task SecondArrived => _secondArrived.Task;

        public void ReleaseFirst() => _firstReleased.TrySetResult();

        public void ReleaseSecond() => _secondReleased.TrySetResult();

        public async Task<PlanGateViolation?> CheckUploadAsync(Guid organizationId, long incomingBytes, CancellationToken ct)
        {
            bool first = Interlocked.Increment(ref _arrivals) == 1;
            (first ? _firstArrived : _secondArrived).TrySetResult();
            await Task.WhenAny((first ? _firstReleased : _secondReleased).Task, Task.Delay(TimeSpan.FromSeconds(2)));

            await using InferenceApiDbContext db = newDb();
            long used = await new StorageUsageService(db, Mock.Of<IOrganizationEntitlements>()).GetUsedBytesAsync(organizationId, ct);
            return used + incomingBytes > limitBytes
                ? new PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "full", 413)
                : null;
        }

        public Task<PlanGateViolation?> CheckStepsAsync(Guid organizationId, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckExecuteAsync(Guid organizationId, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckResolutionAsync(Guid organizationId, int? chosenShortSide, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);
    }

    /// <summary>The <c>QuotaLeftGate</c> of the picture-source tests: refuses when the asked growth exceeds <paramref name="bytesLeft"/>.</summary>
    private sealed class RefusingGate(long bytesLeft) : IPlanGateService
    {
        public Task<PlanGateViolation?> CheckUploadAsync(Guid organizationId, long incomingBytes, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(incomingBytes > bytesLeft
                ? new PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "full", 413)
                : null);

        public Task<PlanGateViolation?> CheckStepsAsync(Guid organizationId, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckExecuteAsync(Guid organizationId, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckResolutionAsync(Guid organizationId, int? chosenShortSide, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);
    }

    /// <summary>Records copies and objects, and (optionally) throws on the <paramref name="failCopyOn"/>-th copy.</summary>
    private sealed class TestStorage(int? failCopyOn = null) : IFileStorageService
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public List<(string From, string To)> Copies { get; } = [];

        /// <summary>When set, the first <see cref="DeleteAsync"/> cancels it (a client that disconnected the moment the object went).</summary>
        public CancellationTokenSource? CancelOnDelete { get; init; }

        private int _copyCalls;

        public string BucketName => "test-bucket";

        public Task<StoredFileObject> UploadAsync(
            Guid projectId, Guid fileId, Stream content, string fileName, string contentType,
            IReadOnlyDictionary<string, string>? metadata, CancellationToken ct, string category = "userFiles",
            string? originalPath = null)
        {
            using MemoryStream copy = new();
            content.CopyTo(copy);
            string key = $"projects/{projectId}/{category}/{fileId}{Path.GetExtension(fileName)}";
            Objects[key] = copy.ToArray();
            return Task.FromResult(new StoredFileObject(
                key, BucketName, $"projects/{projectId}/{category}/", null, category, fileName, fileName, null, fileName));
        }

        public Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Objects.TryGetValue(storageKey, out byte[]? bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes))
                : throw new AmazonS3Exception("The specified key does not exist.")
                {
                    StatusCode = System.Net.HttpStatusCode.NotFound,
                    ErrorCode = "NoSuchKey"
                };

        public Task CopyAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
        {
            if (++_copyCalls == failCopyOn)
            {
                throw new InvalidOperationException($"storage copy {_copyCalls} failed");
            }

            if (!Objects.TryGetValue(sourceStorageKey, out byte[]? bytes))
            {
                throw new AmazonS3Exception("The specified key does not exist.")
                {
                    StatusCode = System.Net.HttpStatusCode.NotFound,
                    ErrorCode = "NoSuchKey"
                };
            }

            Copies.Add((sourceStorageKey, destinationStorageKey));
            Objects[destinationStorageKey] = bytes;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            if (DeleteFailure is not null) throw DeleteFailure;
            Objects.Remove(storageKey);
            CancelOnDelete?.Cancel();
            return Task.CompletedTask;
        }

        /// <summary>When set, <see cref="DeleteAsync"/> throws it instead of removing the object.</summary>
        public Exception? DeleteFailure { get; init; }

        public Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult(Objects.TryGetValue(storageKey, out byte[]? bytes)
                ? new StoredObjectMetadata(bytes.Length, "image/png")
                : null);

        public Task<int> DeleteManyAsync(Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct) =>
            Task.FromResult(storageKeys.Count(k => Objects.Remove(k)));

        public Task MoveAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
        {
            Objects[destinationStorageKey] = Objects[sourceStorageKey];
            Objects.Remove(sourceStorageKey);
            return Task.CompletedTask;
        }

        public Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult<string?>(Objects.ContainsKey(storageKey) ? $"\"{storageKey.GetHashCode():x8}\"" : null);
    }
}
