#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>An object store that keeps the bytes it is given, at the key S3FileStorageService would use.</summary>
internal sealed class ImportObjectStore : IFileStorageService
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public List<string> DeletedKeys { get; } = new();

    public List<(string Key, string ContentType)> Uploads { get; } = new();

    /// <summary>When set, the upload reads this many bytes into the object and then throws (a storage outage mid-write).</summary>
    public int? FailAfterBytes { get; set; }

    /// <summary>Runs before a delete removes the object, so a test can observe what the caller held at that moment.</summary>
    public Func<Guid, string, Task>? OnDelete { get; set; }

    public string BucketName => "import-test-bucket";

    public async Task<StoredFileObject> UploadAsync(
        Guid projectId,
        Guid fileId,
        Stream content,
        string fileName,
        string contentType,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct,
        string category = "userFiles",
        string? originalPath = null)
    {
        string normalized = ProjectFilePath.NormalizeRelativePath(string.IsNullOrWhiteSpace(originalPath) ? fileName : originalPath);
        string name = ProjectFilePath.GetFileName(normalized);
        string? directory = ProjectFilePath.GetDirectoryPath(normalized);
        string storageFileName = ProjectFilePath.BuildStorageFileName(fileId, name);
        string key = ProjectFilePath.BuildStorageKey(projectId, category, directory, storageFileName);
        Uploads.Add((key, contentType));

        using MemoryStream copy = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            copy.Write(buffer, 0, read);
            if (FailAfterBytes is { } limit && copy.Length >= limit)
            {
                Objects[key] = copy.ToArray();
                throw new IOException("storage went away mid-write");
            }
        }

        Objects[key] = copy.ToArray();
        // The real S3 client (PutObjectAsync) disposes the InputStream it is handed; a caller must not touch it afterwards.
        await content.DisposeAsync();
        return new StoredFileObject(key, BucketName, ProjectFilePath.BuildStoragePrefix(projectId, category), "{}", category, name, normalized, directory, storageFileName);
    }

    public async Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        DeletedKeys.Add(storageKey);   // the attempt is recorded even when OnDelete makes it fail
        if (OnDelete is not null) await OnDelete(projectId, storageKey);
        Objects.Remove(storageKey);
    }

    public Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct) => Task.FromResult<string?>("etag-1");

    public Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        Task.FromResult(Objects.TryGetValue(storageKey, out byte[]? b) ? new StoredObjectMetadata(b.LongLength, null) : null);

    public Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct) => throw new NotSupportedException();

    public Task<int> DeleteManyAsync(Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct) => throw new NotSupportedException();

    public Task MoveAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct) => throw new NotSupportedException();

    public Task CopyAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>Records every file the post-add side effects were run for.</summary>
internal sealed class RecordingFileAddedEffects : IProjectFileAddedEffects
{
    public List<(Guid FileId, Guid UserId)> Runs { get; } = new();

    public Task RunAsync(ProjectFile file, Guid userId, CancellationToken ct)
    {
        Runs.Add((file.Id, userId));
        return Task.CompletedTask;
    }
}

/// <summary>A body stream that records whether anyone read it — "refused before the body was read" is then an assertion.</summary>
internal sealed class WatchedStream(byte[] bytes) : MemoryStream(bytes)
{
    public bool WasRead { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        WasRead = true;
        return base.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        WasRead = true;
        return base.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        WasRead = true;
        return base.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        WasRead = true;
        return base.ReadAsync(buffer, offset, count, cancellationToken);
    }
}

/// <summary>
/// The network, below the importer's own checks: answers per absolute URL and records every request
/// (so headers sent and hosts contacted are assertable). An unknown URL is a test bug (500).
/// </summary>
internal sealed class StubUpstream : HttpMessageHandler
{
    public Dictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> Routes { get; } = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = new();

    public void Serve(string url, byte[] body, string? contentType = "video/mp4", long? declaredLength = -1, Stream? stream = null) =>
        Routes[url] = (_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StreamContent(stream ?? new MemoryStream(body)) };
            if (contentType is not null) response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            response.Content.Headers.ContentLength = declaredLength == -1 ? body.LongLength : declaredLength;
            return Task.FromResult(response);
        };

    public void Redirect(string url, string location, HttpStatusCode status = HttpStatusCode.Found) =>
        Routes[url] = (_, _) =>
        {
            HttpResponseMessage response = new(status);
            response.Headers.TryAddWithoutValidation("Location", location);
            return Task.FromResult(response);
        };

    public void Status(string url, HttpStatusCode status) =>
        Routes[url] = (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("nope") });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var route)
            ? route(request, cancellationToken)
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
}

/// <summary>Keeps every formatted log message, so "the query string was never logged" is checkable.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
        if (exception is not null) Messages.Add(exception.ToString());
    }
}

/// <summary>A real <see cref="UrlImportService"/> over an in-memory database, the stub network and recording fakes.</summary>
internal sealed class UrlImportHarness
{
    public UrlImportHarness(
        long maxBytes = 1024 * 1024,
        double timeoutSeconds = 30,
        UrlImportLimiter? limiter = null,
        Guid? organizationId = null,
        IPlanGateService? planGate = null,
        Action<UrlImportOptions>? configure = null,
        OrganizationStorageLock? storageLock = null)
    {
        Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"url-import-{Guid.NewGuid():N}")
            .Options);
        Project = new Project { Id = Guid.NewGuid(), Name = "Launch Promo", OwnerId = Guid.NewGuid(), OrganizationId = organizationId ?? Guid.NewGuid() };
        Db.Projects.Add(Project);
        Db.SaveChanges();
        // A private limiter per harness unless a test shares one, so parallel test classes never contend.
        Options = new UrlImportOptions { MaxBytes = maxBytes, TimeoutSeconds = timeoutSeconds, SpoolMinFreeBytes = 0 };
        configure?.Invoke(Options);
        Limiter = limiter ?? new UrlImportLimiter();
        Service = new UrlImportService(Db, Storage, Effects, Microsoft.Extensions.Options.Options.Create(Options), Logger, planGate ?? PlanGate, Upstream, Limiter, storageLock);
    }

    public UrlImportLimiter Limiter { get; }

    public InferenceApiDbContext Db { get; }

    public Project Project { get; }

    public Guid UserId { get; } = Guid.NewGuid();

    public UrlImportOptions Options { get; }

    public ImportObjectStore Storage { get; } = new();

    public RecordingFileAddedEffects Effects { get; } = new();

    public UploadsPlanGate PlanGate { get; } = new();

    public StubUpstream Upstream { get; } = new();

    public CapturingLogger<UrlImportService> Logger { get; } = new();

    public UrlImportService Service { get; }

    public Task<UrlImportResult> Import(string url, string? fileName = null, string? directoryPath = null) =>
        Service.ImportAsync(Project, UserId, url, fileName, directoryPath, CancellationToken.None);

    /// <summary>Nothing was stored: no object left in storage and no row.</summary>
    public bool NothingStored => Storage.Objects.Count == 0 && !Db.ProjectFiles.Any();
}
