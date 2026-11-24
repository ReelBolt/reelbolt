#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Archives;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Tests.Archives;

/// <summary>An object store holding real bytes in memory, recording every delete; an upload can be made to fail.</summary>
internal sealed class UnpackObjectStore : IFileStorageService
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public List<string> DeletedKeys { get; } = [];
    public int FailUploadAfter { get; set; } = int.MaxValue;
    private int _uploads;

    public string BucketName => "unpack-test";

    public Task<StoredFileObject> UploadAsync(
        Guid projectId, Guid fileId, Stream content, string fileName, string contentType,
        IReadOnlyDictionary<string, string>? metadata, CancellationToken ct, string category = "userFiles", string? originalPath = null)
    {
        if (++_uploads > FailUploadAfter) throw new InvalidOperationException("store is down");
        using MemoryStream copy = new();
        content.CopyTo(copy);
        string storageFileName = ReelBolt.Shared.ProjectFilePath.BuildStorageFileName(fileId, fileName);
        string? dir = originalPath is null ? null : ReelBolt.Shared.ProjectFilePath.GetDirectoryPath(originalPath);
        string key = ReelBolt.Shared.ProjectFilePath.BuildStorageKey(projectId, category, dir, storageFileName);
        Objects[key] = copy.ToArray();
        return Task.FromResult(new StoredFileObject(
            key, BucketName, ReelBolt.Shared.ProjectFilePath.BuildStoragePrefix(projectId, category), null, category,
            fileName, originalPath ?? fileName, dir, storageFileName));
    }

    public Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        Task.FromResult<Stream>(new MemoryStream(Objects.TryGetValue(storageKey, out byte[]? b) ? b : []));

    public Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        DeletedKeys.Add(storageKey);
        Objects.Remove(storageKey);
        return Task.CompletedTask;
    }

    public Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        Task.FromResult<StoredObjectMetadata?>(null);

    public Task<int> DeleteManyAsync(Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task MoveAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
    {
        Objects[destinationStorageKey] = Objects[sourceStorageKey];
        Objects.Remove(sourceStorageKey);
        return Task.CompletedTask;
    }

    public Task CopyAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<bool> MoveIfMatchAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, string expectedETag, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<StoredObjectEntry>> ListAsync(Guid projectId, string category, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>A gateway that answers with a scripted session (and records how many times it was opened).</summary>
internal sealed class ScriptedGateway : IArchiveUnpackGateway
{
    public Func<ArchiveUnpackSession> Next { get; set; } = () => throw new InvalidOperationException("not scripted");
    public int Opened { get; private set; }

    public Task<ArchiveUnpackSession> OpenAsync(Stream zip, long length, CancellationToken ct)
    {
        Opened++;
        return Task.FromResult(Next());
    }

    public static ArchiveUnpackSession Ok(
        IReadOnlyDictionary<string, int>? skipped = null, params (string Path, byte[] Data)[] files)
    {
        List<UnpackManifestEntry> entries = [.. files.Select(f =>
            new UnpackManifestEntry(f.Path, f.Data.Length, Convert.ToHexString(SHA256.HashData(f.Data)).ToLowerInvariant()))];
        MemoryStream body = new();
        foreach ((string _, byte[] data) in files) body.Write(data);
        body.Position = 0;
        UnpackManifest manifest = new(entries, skipped ?? new Dictionary<string, int>(), [], false, null);
        return new ArchiveUnpackSession(ArchiveUnpackStatus.Ok, manifest, null, body, null);
    }

    public static ArchiveUnpackSession Status(ArchiveUnpackStatus status, int retryAfter = 0) => new(status, retryAfter);

    public static ArchiveUnpackSession Aborted(string reason) =>
        new(ArchiveUnpackStatus.Aborted, new UnpackManifest([], new Dictionary<string, int>(), [], true, reason), null, null, null);
}

internal sealed class UnpackHarness
{
    public InferenceApiDbContext Db { get; }
    public UnpackObjectStore Store { get; } = new();
    public ScriptedGateway Gateway { get; } = new();
    public Mock<IPlanGateService> PlanGate { get; } = new();
    public Mock<IPublishEndpoint> Publish { get; } = new();
    public Project Project { get; }
    public Guid UserId { get; } = Guid.NewGuid();
    public DateTime Now { get; set; } = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    public UnpackHarness()
    {
        _options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"unpack-{Guid.NewGuid():N}")
            .Options;
        Db = new InferenceApiDbContext(_options);
        Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
        Db.Projects.Add(Project);
        Db.SaveChanges();
        PlanGate.Setup(p => p.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null);
    }

    /// <summary>The CSAM matcher the processor scans with; null (the default) leaves rows as the unscanned writer leaves them.</summary>
    public ICsmaHashMatcher? Csam { get; set; }

    /// <summary>The quarantine mover for a held zip; null leaves the zip's object where it is.</summary>
    public ICsamQuarantineService? CsamQuarantine { get; set; }

    public IUserActivityNotifier? Notifier { get; set; }

    private readonly DbContextOptions<InferenceApiDbContext> _options;

    /// <summary>A second context on the same store, as a second API replica would have.</summary>
    public InferenceApiDbContext NewDb() => new(_options);

    public ArchiveUnpackProcessor Processor() => Processor(Db, Gateway);

    public ArchiveUnpackProcessor Processor(InferenceApiDbContext db, IArchiveUnpackGateway gateway) => new(
        db, Store, new OrganizationStorageLock(), PlanGate.Object, gateway, Publish.Object, null, null, Notifier, new FixedClock(this), Csam, CsamQuarantine);

    private sealed class FixedClock(UnpackHarness h) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(h.Now, TimeSpan.Zero);
    }

    /// <summary>Stores a zip row and its bytes.</summary>
    public ProjectFile AddZip(string name = "app.zip", string? directory = null, int size = 1000)
    {
        Guid id = Guid.NewGuid();
        string key = $"projects/{Project.Id}/userFiles/{id}.zip";
        Store.Objects[key] = new byte[size];
        ProjectFile zip = new()
        {
            Id = id,
            ProjectId = Project.Id,
            OriginalFileName = name,
            OriginalPath = directory is null ? name : $"{directory}/{name}",
            DirectoryPath = directory,
            Category = "userFiles",
            StorageKey = key,
            StorageBucket = Store.BucketName,
            MimeType = "application/zip",
            SizeBytes = size,
            SummaryStatus = SummaryStatus.Done,
            CsamStatus = CsamScanStatus.Clean,
        };
        Db.ProjectFiles.Add(zip);
        Db.SaveChanges();
        return zip;
    }

    public ProjectArchiveUnpack AddUnpack(ProjectFile zip, ArchiveUnpackRunStatus status = ArchiveUnpackRunStatus.Pending)
    {
        ProjectArchiveUnpack row = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Project.Id,
            OrganizationId = Project.OrganizationId,
            SourceFileId = zip.Id,
            ArchiveName = zip.OriginalFileName,
            ArchiveSizeBytes = zip.SizeBytes,
            Status = status,
            RequestedByUserId = UserId,
            CreatedAt = Now,
        };
        Db.ProjectArchiveUnpacks.Add(row);
        Db.SaveChanges();
        return row;
    }

    /// <summary>Claims and processes the next due unpack; returns it reloaded.</summary>
    public async Task<ProjectArchiveUnpack?> RunNextAsync()
    {
        ArchiveUnpackProcessor processor = Processor();
        Guid? id = await processor.ClaimNextAsync(CancellationToken.None);
        if (id is null) return null;
        await processor.ProcessAsync(id.Value, CancellationToken.None);
        Db.ChangeTracker.Clear();
        return await Db.ProjectArchiveUnpacks.AsNoTracking().FirstAsync(u => u.Id == id.Value);
    }

    public static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);
}
