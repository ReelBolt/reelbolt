#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Compute;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// An object store that records sizes instead of bytes. A direct upload's multipart path deals in
/// 256 MiB+ objects, and a test that allocated those to prove a HEAD would answer would be measuring
/// the test host's memory rather than the service. <see cref="Put"/> is therefore the HEAD answer, and
/// every delete is recorded so a refusal's cleanup is assertable.
/// </summary>
internal sealed class UploadsObjectStore : IFileStorageService
{
    /// <summary>The size <see cref="GetMetadataAsync"/> reports per key. A key absent here does not exist.</summary>
    public Dictionary<string, long> Sizes { get; } = new(StringComparer.Ordinal);

    public List<string> DeletedKeys { get; } = new();

    /// <summary>How many HEADs were served, so idempotency can be shown not to ask twice.</summary>
    public int MetadataReads { get; private set; }

    public string BucketName => "uploads-test-bucket";

    /// <summary>The bytes "the client PUT" to storage, at the key the ticket named.</summary>
    public void Put(string key, long sizeBytes)
    {
        Sizes[key] = sizeBytes;
        ETags[key] = $"etag-{++_etagCounter}";
    }

    private int _etagCounter;

    /// <summary>Real bytes for the few tests that need content (issue 8: a <c>.ts</c> is typed by its first bytes).</summary>
    public Dictionary<string, byte[]> Contents { get; } = new(StringComparer.Ordinal);

    /// <summary>How many downloads were served, so a test can show a non-<c>.ts</c> upload never reads its bytes.</summary>
    public int Downloads { get; private set; }

    /// <summary><see cref="Put(string, long)"/> with the bytes themselves.</summary>
    public void Put(string key, byte[] bytes)
    {
        Put(key, bytes.LongLength);
        Contents[key] = bytes;
    }

    /// <summary>The ETag <see cref="GetMetadataAsync"/> reports per key; every <see cref="Put"/> changes it.</summary>
    public Dictionary<string, string> ETags { get; } = new(StringComparer.Ordinal);

    /// <summary>When true, HEADs report no ETag (a store that cannot pin the object).</summary>
    public bool OmitETag { get; set; }

    /// <summary>When false, <see cref="MoveIfMatchAsync"/> answers false without moving (a store that cannot condition the copy).</summary>
    public bool CanConditionCopy { get; set; } = true;

    /// <summary>Runs after each HEAD answered (key passed in): a test re-PUTs the object here to model a swap between HEAD and copy.</summary>
    public Action<string>? AfterHead { get; set; }

    /// <summary>Every conditional move attempted, in order, with whether it moved.</summary>
    public List<(string From, string To, string ExpectedETag, bool Moved)> ConditionalMoves { get; } = new();

    /// <summary>When set, <see cref="GetMetadataAsync"/> cancels it and throws (the request aborted during the HEAD).</summary>
    public CancellationTokenSource? CancelDuringHead { get; set; }

    /// <summary>When set, <see cref="GetMetadataAsync"/> throws it (a storage outage during the HEAD: neither a
    /// cancellation nor a refusal — QA-P0-28).</summary>
    public Exception? HeadFailure { get; set; }

    /// <summary>When true, <see cref="CancelDuringHead"/> and <see cref="HeadFailure"/> only fire once a move has landed
    /// (the HEAD that measures the relocated copy), not on the staging HEAD that precedes the move.</summary>
    public bool HeadFaultsAfterMove { get; set; }

    public Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        MetadataReads++;
        bool faultsArmed = !HeadFaultsAfterMove || Moves.Count > 0;
        if (faultsArmed && HeadFailure is not null)
        {
            throw HeadFailure;
        }

        if (faultsArmed && CancelDuringHead is not null)
        {
            CancelDuringHead.Cancel();
            throw new OperationCanceledException(CancelDuringHead.Token);
        }

        StoredObjectMetadata? answer = Sizes.TryGetValue(storageKey, out long size)
            ? new StoredObjectMetadata(size, "application/octet-stream", OmitETag ? null : ETags.GetValueOrDefault(storageKey))
            : null;
        AfterHead?.Invoke(storageKey);
        return Task.FromResult(answer);
    }

    public Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        // A real S3 client refuses a cancelled token before sending anything.
        ct.ThrowIfCancellationRequested();
        DeletedKeys.Add(storageKey);
        Sizes.Remove(storageKey);
        ETags.Remove(storageKey);
        return Task.CompletedTask;
    }

    // The direct-upload service never transports bytes: it only HEADs, deletes and reads the bucket
    // name. The remaining members exist to satisfy the interface and would be a test bug if reached.
    public Task<StoredFileObject> UploadAsync(
        Guid projectId,
        Guid fileId,
        Stream content,
        string fileName,
        string contentType,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct,
        string category = "userFiles",
        string? originalPath = null) =>
        throw new NotSupportedException("A direct upload never posts bytes through this service.");

    public Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        // Only a .ts-family upload reads its head; any other download would be a test bug.
        if (!Contents.TryGetValue(storageKey, out byte[]? bytes))
            throw new NotSupportedException("A direct upload downloads only the head of a .ts-family file put with bytes.");
        Downloads++;
        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public Task<int> DeleteManyAsync(Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct) =>
        throw new NotSupportedException("A direct upload deletes one object at a time.");

    /// <summary>Every server-side move, in order (an assistant ticket's completion relocates its object).</summary>
    public List<(string From, string To)> Moves { get; } = new();

    /// <summary>When set, <see cref="MoveAsync"/> throws it before copying (S3's 5 GiB CopyObject ceiling).</summary>
    public Exception? MoveFailure { get; set; }

    /// <summary>When true, <see cref="MoveAsync"/> copies, then fails to delete the source (both objects remain).</summary>
    public bool MoveDeleteFails { get; set; }

    /// <summary>When set, <see cref="MoveAsync"/> copies, then the request is cancelled before the source delete
    /// (this source is cancelled and the call throws <see cref="OperationCanceledException"/>; both objects remain).</summary>
    public CancellationTokenSource? CancelDuringMove { get; set; }

    public Task MoveAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
    {
        if (!Sizes.TryGetValue(sourceStorageKey, out long size))
            throw new InvalidOperationException($"No object at {sourceStorageKey}.");
        if (MoveFailure is not null)
            throw MoveFailure;
        if (CancelDuringMove is not null)
        {
            Sizes[destinationStorageKey] = size;
            CancelDuringMove.Cancel();
            throw new OperationCanceledException(CancelDuringMove.Token);
        }
        if (MoveDeleteFails)
        {
            Sizes[destinationStorageKey] = size;
            throw new InvalidOperationException("The copy landed but the source delete failed.");
        }
        Moves.Add((sourceStorageKey, destinationStorageKey));
        Sizes.Remove(sourceStorageKey);
        Sizes[destinationStorageKey] = size;
        if (Contents.Remove(sourceStorageKey, out byte[]? bytes)) Contents[destinationStorageKey] = bytes;
        if (ETags.Remove(sourceStorageKey, out string? etag)) ETags[destinationStorageKey] = etag;
        return Task.CompletedTask;
    }

    public async Task<bool> MoveIfMatchAsync(
        Guid projectId, string sourceStorageKey, string destinationStorageKey, string expectedETag, CancellationToken ct)
    {
        if (!CanConditionCopy)
        {
            ConditionalMoves.Add((sourceStorageKey, destinationStorageKey, expectedETag, false));
            return false;
        }

        bool matches = ETags.TryGetValue(sourceStorageKey, out string? current)
            && string.Equals(current, expectedETag, StringComparison.Ordinal);
        if (!matches)
        {
            ConditionalMoves.Add((sourceStorageKey, destinationStorageKey, expectedETag, false));
            return false;
        }

        await MoveAsync(projectId, sourceStorageKey, destinationStorageKey, ct);
        ConditionalMoves.Add((sourceStorageKey, destinationStorageKey, expectedETag, true));
        return true;
    }

    public Task CopyAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct) =>
        throw new NotSupportedException("A direct upload never copies objects.");
}

/// <summary>
/// S3's multipart calls, in memory. Every call is recorded with its arguments, so "the ETags reached
/// storage" and "the abort happened" are assertions about what the service did rather than about what
/// it returned. <see cref="CompleteResult"/> stands in for storage that no longer knows the upload id.
/// </summary>
internal sealed class UploadsMultipartStore : IMultipartObjectStore
{
    public List<(Guid ProjectId, string Key, string ContentType)> Created { get; } = new();

    public List<(Guid ProjectId, string Key, string UploadId, int PartNumber, TimeSpan Ttl)> Presigned { get; } = new();

    public List<(Guid ProjectId, string Key, string UploadId, IReadOnlyList<DirectUploadCompletedPart> Parts)> Completed { get; } = new();

    public List<(Guid ProjectId, string Key, string UploadId)> Aborted { get; } = new();

    /// <summary>The answer <see cref="CompleteAsync"/> gives. False models an upload id storage forgot.</summary>
    public bool CompleteResult { get; set; } = true;

    /// <summary>When set, <see cref="CompleteAsync"/> throws it instead of answering.</summary>
    public Exception? CompleteFailure { get; set; }

    /// <summary>When set, <see cref="AbortAsync"/> throws it (the "already gone" shape).</summary>
    public Exception? AbortFailure { get; set; }

    public string NextUploadId { get; set; } = "upload-1";

    public Task<string> CreateAsync(Guid projectId, string key, string contentType, CancellationToken ct)
    {
        Created.Add((projectId, key, contentType));
        return Task.FromResult(NextUploadId);
    }

    public Task<string> PresignPartAsync(Guid projectId, string key, string uploadId, int partNumber, TimeSpan ttl)
    {
        Presigned.Add((projectId, key, uploadId, partNumber, ttl));
        return Task.FromResult($"https://objects.test/part/{partNumber}?uploadId={uploadId}&key={Uri.EscapeDataString(key)}");
    }

    public Task<bool> CompleteAsync(Guid projectId, string key, string uploadId, IReadOnlyList<DirectUploadCompletedPart> parts, CancellationToken ct)
    {
        Completed.Add((projectId, key, uploadId, parts));
        if (CompleteFailure is not null) throw CompleteFailure;
        return Task.FromResult(CompleteResult);
    }

    public Task AbortAsync(Guid projectId, string key, string uploadId, CancellationToken ct)
    {
        Aborted.Add((projectId, key, uploadId));
        if (AbortFailure is not null) throw AbortFailure;
        return Task.CompletedTask;
    }
}

/// <summary>A plan gate whose answers a test queues, and which records every growth it was asked about.</summary>
internal sealed class UploadsPlanGate : IPlanGateService
{
    public Queue<PlanGateViolation> Violations { get; } = new();

    /// <summary>The NET growth of every upload check, in order.</summary>
    public List<long> UploadChecks { get; } = new();

    /// <summary>Per-call failures, dequeued in order; a null entry lets that call through to the violations queue.</summary>
    public Queue<Exception?> Failures { get; } = new();

    /// <summary>When set, <see cref="CheckUploadAsync"/> cancels it and throws (the request aborted during the quota check).</summary>
    public CancellationTokenSource? CancelDuringCheck { get; set; }

    /// <summary>When set, <see cref="CheckUploadAsync"/> throws it (a database outage during the quota check: neither a
    /// cancellation nor a refusal — QA-P0-28).</summary>
    public Exception? CheckFailure { get; set; }

    public Task<PlanGateViolation?> CheckUploadAsync(Guid organizationId, long incomingBytes, CancellationToken ct)
    {
        UploadChecks.Add(incomingBytes);

        // Per-call failures, dequeued in order: a call whose entry is set throws it (a plan gate that is
        // unreachable on that call, and reachable on the others). A null entry lets the call through.
        if (Failures.Count > 0 && Failures.Dequeue() is { } failure)
        {
            throw failure;
        }

        if (CheckFailure is not null)
        {
            throw CheckFailure;
        }

        if (CancelDuringCheck is not null)
        {
            CancelDuringCheck.Cancel();
            throw new OperationCanceledException(CancelDuringCheck.Token);
        }
        return Task.FromResult(Violations.Count > 0 ? Violations.Dequeue() : null);
    }

    public Task<PlanGateViolation?> CheckStepsAsync(Guid organizationId, IEnumerable<ReelBolt.Inference.Api.Controllers.Dto.CreateWorkflowStepRequest> steps, CancellationToken ct) =>
        Task.FromResult<PlanGateViolation?>(null);

    public Task<PlanGateViolation?> CheckExecuteAsync(Guid organizationId, CancellationToken ct) =>
        Task.FromResult<PlanGateViolation?>(null);

    public Task<PlanGateViolation?> CheckResolutionAsync(Guid organizationId, int? chosenShortSide, CancellationToken ct) =>
        Task.FromResult<PlanGateViolation?>(null);
}

/// <summary>A fixed entitlement set, counting its reads so a check that should not consult the plan can be proved not to.</summary>
internal sealed class UploadsEntitlements(OrganizationEntitlementSet set) : IOrganizationEntitlements
{
    public int Reads { get; private set; }

    public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        Reads++;
        return new ValueTask<OrganizationEntitlementSet>(set);
    }
}

/// <summary>Cancels <see cref="CancelDuringSave"/> and throws before anything is written (the request aborted during SaveChanges).</summary>
internal sealed class UploadsSaveCanceller : SaveChangesInterceptor
{
    public CancellationTokenSource? CancelDuringSave { get; set; }

    /// <summary>When set, the save throws it instead of writing (a database failure during SaveChanges: neither a
    /// cancellation nor a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> — QA-P0-28).</summary>
    public Exception? SaveFailure { get; set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }

        if (CancelDuringSave is not null)
        {
            CancelDuringSave.Cancel();
            throw new OperationCanceledException(CancelDuringSave.Token);
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>When set, the write lands, then this is cancelled and the save throws (the acknowledgement was lost).</summary>
    public CancellationTokenSource? CancelAfterSave { get; set; }

    /// <summary>
    /// When set, the write lands and this is cancelled, but the save RETURNS: the request died just after the row
    /// was written, so the next storage call is the first one to observe the cancellation (S-80's window).
    /// </summary>
    public CancellationTokenSource? CancelAfterSaveQuietly { get; set; }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (CancelAfterSave is not null)
        {
            CancelAfterSave.Cancel();
            throw new OperationCanceledException(CancelAfterSave.Token);
        }

        CancelAfterSaveQuietly?.Cancel();
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class UploadsClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now = Now.Add(by);
}

/// <summary>
/// Everything a <see cref="DirectUploadService"/> test needs: a real service over an in-memory
/// database, a recording object store, a recording multipart store, a queued-answer plan gate and a
/// hand-moved clock. The ticket MAC is real (<see cref="ConfirmationTokenService.CreateEphemeral"/>),
/// because a forged ticket is exactly what these tests are about.
/// </summary>
internal sealed class DirectUploadHarness
{
    public DirectUploadHarness(bool withMultipart = true, bool withPlanGate = true)
    {
        Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"direct-upload-service-{Guid.NewGuid():N}")
            .AddInterceptors(SaveCanceller)
            .Options);
        Project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid()
        };
        Db.Projects.Add(Project);
        Db.SaveChanges();

        Publisher
            .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
            .Callback<MediaDerivativesRequested, CancellationToken>((message, _) => Published.Add(message))
            .Returns(Task.CompletedTask);

        Service = new DirectUploadService(
            Db, Storage, Presign, Tokens,
            withMultipart ? Multipart : null,
            withPlanGate ? PlanGate : null,
            Clock);
    }

    /// <summary>The broker: every derivative request the shared post-add path published, in order.</summary>
    public Mock<IPublishEndpoint> Publisher { get; } = new();

    public List<MediaDerivativesRequested> Published { get; } = new();

    /// <summary>
    /// A completion exactly as a route performs it: the service commits the row, then the one post-add
    /// path every caller shares (<see cref="ProjectFileAddedEffects"/>) applies the row's effects —
    /// which for a media row is the derivative request. A completion for a row that already exists is
    /// the retry of one that succeeded, so the route returns before the effects run
    /// (ProjectFilesController.CompleteUpload, DeviceFolderSyncController.CompleteUpload,
    /// AssistantUploadTools).
    /// </summary>
    public async Task<ProjectFile> CompleteThroughRouteAsync(DirectUploadTicket ticket, CancellationToken ct = default)
    {
        bool alreadyRecorded = await Db.ProjectFiles.AnyAsync(f => f.Id == ticket.FileId && f.ProjectId == ticket.ProjectId, ct);

        ProjectFile row = await Service.CompleteAsync(ticket, new DirectUploadCompletion([]), ct);

        if (!alreadyRecorded)
            await new ProjectFileAddedEffects(Publisher.Object, new ChannelBackgroundTaskQueue<FileSummarizationTask>(10), Storage)
                .RunAsync(row, Project.OwnerId, ct);

        return row;
    }

    public InferenceApiDbContext Db { get; }

    public UploadsSaveCanceller SaveCanceller { get; } = new();

    public Project Project { get; }

    public UploadsObjectStore Storage { get; } = new();

    public FakePresignedUrlService Presign { get; } = new();

    public ConfirmationTokenService Tokens { get; } = ConfirmationTokenService.CreateEphemeral();

    public UploadsMultipartStore Multipart { get; } = new();

    public UploadsPlanGate PlanGate { get; } = new();

    public UploadsClock Clock { get; } = new();

    public DirectUploadService Service { get; }

    /// <summary>The browser's caller key (a user id) and the desktop app's ("device:{id}").</summary>
    public string CallerKey { get; } = Guid.NewGuid().ToString("D");

    public string DeviceCallerKey { get; } = $"device:{Guid.NewGuid():N}";

    public DirectUploadRequest Request(
        long sizeBytes = 4,
        string fileName = "clip.mp4",
        string? relativePath = null,
        string? contentType = "video/mp4",
        string? category = "userFiles",
        string? sourceKind = null,
        string? sourceRevision = null,
        Guid? sourceLinkId = null,
        Guid? replacesFileId = null,
        string? callerKey = null,
        Guid? projectId = null,
        Guid? organizationId = null) =>
        new(
            projectId ?? Project.Id,
            organizationId ?? Project.OrganizationId,
            callerKey ?? CallerKey,
            fileName,
            relativePath,
            sizeBytes,
            contentType,
            category!,
            sourceKind,
            sourceRevision,
            sourceLinkId,
            replacesFileId);

    public Task<DirectUploadTicket> Create(DirectUploadRequest request) =>
        Service.CreateAsync(request, CancellationToken.None);

    public Task<DirectUploadTicket> Create(long sizeBytes = 4, string fileName = "clip.mp4", string? relativePath = null, string contentType = "video/mp4", string? sourceKind = null) =>
        Create(Request(sizeBytes, fileName, relativePath, contentType, sourceKind: sourceKind));

    /// <summary>The fields a client echoes back: every ticket field that is covered by the MAC.</summary>
    public static DirectUploadTicketEcho Echo(DirectUploadTicket ticket) =>
        new(
            ticket.UploadToken,
            ticket.Mode,
            ticket.ProjectId,
            ticket.FileId,
            ticket.Key,
            ticket.FileName,
            ticket.RelativePath,
            ticket.ContentType,
            ticket.SizeBytes,
            ticket.ExpiresAtUnix,
            ticket.UploadId,
            ticket.PartSizeBytes,
            ticket.PartCount,
            ticket.SourceKind,
            ticket.SourceRevision,
            ticket.SourceLinkId,
            ticket.ReplacesFileId)
        { RelocateOnComplete = ticket.RelocateOnComplete };

    /// <summary>Seeds a file a replacement would rewrite, and returns it.</summary>
    public ProjectFile SeedFile(long sizeBytes = 100, string storageKey = "projects/x/userFiles/old.mp4", string name = "old.mp4", Guid? projectId = null)
    {
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId ?? Project.Id,
            OriginalFileName = name,
            OriginalPath = name,
            StorageKey = storageKey,
            StorageBucket = Storage.BucketName,
            MimeType = "video/mp4",
            SizeBytes = sizeBytes
        };
        Db.ProjectFiles.Add(file);
        Db.SaveChanges();
        return file;
    }

    public static PlanGateViolation OverQuota(long used = 100, long limit = 99) =>
        new(ReelBolt.Shared.Billing.PlanGate.StorageCode, "storage", "This upload would exceed the plan's storage quota.", 413, limit, used);
}
