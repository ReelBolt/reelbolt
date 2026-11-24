using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>An in-memory object store whose ETag is a hash of the bytes, so an overwrite changes it.</summary>
internal sealed class HistoryTestStore : IFileStorageService, IObjectListing
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public int Copies { get; private set; }
    public Exception? CopyFailure { get; set; }
    public Action<int>? OnETagCall { get; set; }
    public Func<Task>? BeforeMove { get; set; }

    /// <summary>Set to make <see cref="DeleteManyAsync"/> report fewer deletes than asked.</summary>
    public int DeleteManyShortfall { get; set; }

    private int _etagCalls;

    public string BucketName => "history-test";

    public static string ETagOf(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    public void Put(string key, string content) => Objects[key] = System.Text.Encoding.UTF8.GetBytes(content);

    public string ETagAt(string key) => ETagOf(Objects[key]);

    public Task<StoredFileObject> UploadAsync(
        Guid projectId, Guid fileId, Stream content, string fileName, string contentType,
        IReadOnlyDictionary<string, string>? metadata, CancellationToken ct,
        string category = "userFiles", string? originalPath = null)
    {
        S3FileStorageService.UploadTarget target = S3FileStorageService.BuildUploadTarget(
            projectId, fileId, fileName, S3FileStorageService.NormalizeCategory(category), originalPath);
        using MemoryStream copy = new();
        content.CopyTo(copy);
        Objects[target.StorageKey] = copy.ToArray();
        return Task.FromResult(new StoredFileObject(
            target.StorageKey, BucketName, target.StoragePrefix, null, category,
            target.NormalizedFileName, target.NormalizedOriginalPath, target.DirectoryPath, target.StorageFileName));
    }

    public Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        Task.FromResult<Stream>(new MemoryStream(Objects[storageKey], writable: false));

    /// <summary>The 1-based DeleteAsync call (counted from <see cref="DeleteCalls"/> = 0) that throws.</summary>
    public int? FailDeleteOnCall { get; set; }

    public int DeleteCalls { get; private set; }

    /// <summary>Runs at the start of every DeleteAsync, before it can fail or remove anything.</summary>
    public Action<string>? OnDelete { get; set; }

    public Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        DeleteCalls++;
        OnDelete?.Invoke(storageKey);
        if (FailDeleteOnCall == DeleteCalls)
            throw new IOException("storage unavailable");

        Objects.Remove(storageKey);
        return Task.CompletedTask;
    }

    public Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        Task.FromResult(Objects.TryGetValue(storageKey, out byte[]? bytes)
            ? new StoredObjectMetadata(bytes.Length, null, ETagOf(bytes))
            : null);

    public Task<int> DeleteManyAsync(Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct)
    {
        int deleted = storageKeys.Count(k => Objects.Remove(k));
        return Task.FromResult(Math.Max(0, deleted - DeleteManyShortfall));
    }

    public Task MoveAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
    {
        BeforeMove?.Invoke().GetAwaiter().GetResult();
        byte[] bytes = Objects[sourceStorageKey];
        Objects.Remove(sourceStorageKey);
        Objects[destinationStorageKey] = bytes;
        return Task.CompletedTask;
    }

    public Task<bool> MoveIfMatchAsync(
        Guid projectId, string sourceStorageKey, string destinationStorageKey, string expectedETag, CancellationToken ct)
    {
        if (!Objects.TryGetValue(sourceStorageKey, out byte[]? bytes)
            || !string.Equals(ETagOf(bytes), expectedETag, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        BeforeMove?.Invoke().GetAwaiter().GetResult();
        Objects.Remove(sourceStorageKey);
        Objects[destinationStorageKey] = bytes;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<StoredObjectEntry>> ListAsync(Guid projectId, string category, CancellationToken ct)
    {
        string prefix = $"projects/{projectId}/{category}/";
        IReadOnlyList<StoredObjectEntry> entries = Objects.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => new StoredObjectEntry(k, DateTime.UtcNow))
            .ToList();
        return Task.FromResult(entries);
    }

    public Task CopyAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
    {
        if (CopyFailure is not null)
            throw CopyFailure;
        Objects[destinationStorageKey] = Objects[sourceStorageKey];
        Copies++;
        return Task.CompletedTask;
    }

    public Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        OnETagCall?.Invoke(++_etagCalls);
        return Task.FromResult(Objects.TryGetValue(storageKey, out byte[]? bytes) ? ETagOf(bytes) : null);
    }
}

/// <summary>A DI container with the in-memory database and the fake store, as the decorator sees them.</summary>
internal sealed class HistoryHarness : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _dbName;

    public HistoryHarness()
    {
        string dbName = _dbName = $"history-protect-{Guid.NewGuid():N}";
        Store = new HistoryTestStore();
        ServiceCollection services = new();
        services.AddScoped(_ => new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(dbName).Options));
        services.AddSingleton<IFileStorageService>(Store);
        _provider = services.BuildServiceProvider();
        Decorator = new ProjectHistoryProtectingStorage(
            Store, _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProjectHistoryProtectingStorage>.Instance);
    }

    public HistoryTestStore Store { get; }
    public ProjectHistoryProtectingStorage Decorator { get; }
    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

    public InferenceApiDbContext NewDb() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options);

    public void Dispose() => _provider.Dispose();

    /// <summary>
    /// Seeds a project, a live file at <paramref name="key"/> and a non-expired version whose file item
    /// captured it. Returns (project id, file id).
    /// </summary>
    public async Task<(Guid ProjectId, Guid FileId, string Key)> SeedVersionedFileAsync(
        string content = "v1", string category = "userFiles", string? contentIdOverride = null, int versions = 1)
    {
        using IServiceScope scope = ScopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        db.Projects.Add(new Project { Id = projectId, Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() });
        string key = $"projects/{projectId}/{category}/{fileId:N}.txt";
        Store.Put(key, content);
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = fileId,
            ProjectId = projectId,
            Category = category,
            OriginalFileName = "a.txt",
            OriginalPath = "a.txt",
            StorageKey = key,
            StorageBucket = "t",
            MimeType = "text/plain",
            SizeBytes = content.Length
        });
        string etag = Store.ETagAt(key);
        string contentId = contentIdOverride ?? ContentId.FromEtag(etag, content.Length);
        for (int i = 0; i < versions; i++)
            AddVersion(db, projectId, i + 1, fileId, key, etag, contentId, content.Length);
        await db.SaveChangesAsync();
        return (projectId, fileId, key);
    }

    public static ProjectVersion AddVersion(
        InferenceApiDbContext db, Guid projectId, int number, Guid fileId, string key, string? etag,
        string contentId, long size, DateTime? expiresAt = null, DateTime? erasedAt = null)
    {
        ProjectVersion version = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Number = number,
            Cause = ProjectVersionCause.Auto,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(10),
            ManifestDigest = new string('a', 64),
            ItemCount = 1
        };
        db.ProjectVersions.Add(version);
        db.ProjectVersionItems.Add(new ProjectVersionItem
        {
            Id = Guid.NewGuid(),
            VersionId = version.Id,
            ProjectId = projectId,
            Kind = ProjectVersionItemKind.File,
            ItemKey = fileId.ToString("D"),
            Name = "a.txt",
            Category = "userFiles",
            SizeBytes = size,
            ContentId = contentId,
            StorageKey = key,
            StorageETag = etag,
            ErasedAt = erasedAt
        });
        return version;
    }
}
