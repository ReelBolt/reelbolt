using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// Shared doubles for the CSAM tests: an in-memory object store that really moves bytes, a fake
/// Arachnid server (a real <see cref="HttpMessageHandler"/>, so the client code under test is the
/// production one), a logger that captures messages, and a database helper.
/// All tests are offline; nothing here reaches projectarachnid.com.
/// </summary>
internal static class CsamTestSupport
{
    public const string BaseUrl = "https://shield.test";
    public const string Username = "csam-test-user";
    public const string Password = "csam-test-password";

    public static CsamOptions EnabledOptions(Action<ArachnidOptions>? configure = null)
    {
        CsamOptions options = new()
        {
            Arachnid = new ArachnidOptions
            {
                Enabled = true,
                BaseUrl = BaseUrl,
                Username = Username,
                Password = Password,
                TimeoutSeconds = 10,
                MaxInlineHashBytes = 64 * 1024 * 1024
            }
        };
        configure?.Invoke(options.Arachnid);
        return options;
    }

    public static InferenceApiDbContext NewDb(ITenantContext? tenant = null) =>
        NewDb($"csam-{Guid.NewGuid():N}", tenant);

    /// <summary>Two contexts with the same name share the InMemory store, so a test can simulate a concurrent writer.</summary>
    public static InferenceApiDbContext NewDb(string databaseName, ITenantContext? tenant = null) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options,
            tenant);

    public static ProjectFile AddFile(
        InferenceApiDbContext db,
        string mimeType = "image/jpeg",
        string fileName = "photo.jpg",
        byte[]? bytes = null,
        long? sizeBytes = null,
        Guid? projectId = null)
    {
        Guid id = Guid.NewGuid();
        Guid project = projectId ?? Guid.NewGuid();
        string name = fileName;
        ProjectFile file = new()
        {
            Id = id,
            ProjectId = project,
            OriginalFileName = name,
            Category = "userFiles",
            StorageKey = $"projects/{project}/userFiles/{id}/{name}",
            StorageBucket = "csam-test",
            MimeType = mimeType,
            SizeBytes = sizeBytes ?? bytes?.LongLength ?? 16,
            UploadedAt = DateTime.UtcNow,
            CsamStatus = CsamScanStatus.Pending
        };
        db.ProjectFiles.Add(file);
        db.SaveChanges();
        return file;
    }

    public static ArachnidShieldClient NewClient(CsamOptions options, CsamFakeServer server) =>
        new(NewHttpClient(options, new CsamRecordingHandler(server)), Options.Create(options));

    public static HttpClient NewHttpClient(CsamOptions options, HttpMessageHandler handler) =>
        // The handler is shared between scans in some tests, so disposing one client must not
        // dispose it.
        new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(options.Arachnid.BaseUrl!.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(options.Arachnid.TimeoutSeconds)
        };

    public static CsamHashMatcher NewMatcher(
        InferenceApiDbContext db,
        IFileStorageService storage,
        IArachnidShieldClient client,
        CsamOptions options,
        IPdqHasher? hasher = null,
        ILogger<CsamHashMatcher>? logger = null) =>
        new(
            db,
            storage,
            hasher ?? new FixedPdqHasher(),
            client,
            new CsamQuarantineService(storage),
            Options.Create(options),
            logger);
}

/// <summary>An in-memory object store: <see cref="MoveAsync"/> really moves the bytes.</summary>
internal sealed class CsamTestStorage : IFileStorageService
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public List<(string From, string To)> Moves { get; } = [];
    public List<string> DeletedKeys { get; } = [];
    public int Downloads { get; private set; }
    public Exception? MoveFailure { get; set; }
    public Exception? DeleteFailure { get; set; }

    public string BucketName => "csam-test";

    public void Put(string key, byte[] bytes) => Objects[key] = bytes;

    public Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        Downloads++;
        if (!Objects.TryGetValue(storageKey, out byte[]? bytes))
            throw new FileNotFoundException(storageKey);
        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public Task MoveAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
    {
        if (MoveFailure is not null) throw MoveFailure;
        if (!Objects.Remove(sourceStorageKey, out byte[]? bytes))
            throw new InvalidOperationException($"No object at {sourceStorageKey}.");
        Objects[destinationStorageKey] = bytes;
        Moves.Add((sourceStorageKey, destinationStorageKey));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        if (DeleteFailure is not null) throw DeleteFailure;
        DeletedKeys.Add(storageKey);
        Objects.Remove(storageKey);
        return Task.CompletedTask;
    }

    public Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        Task.FromResult(Objects.TryGetValue(storageKey, out byte[]? bytes)
            ? new StoredObjectMetadata(bytes.Length, null)
            : null);

    public Task<int> DeleteManyAsync(Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct)
    {
        if (DeleteFailure is not null) throw DeleteFailure;
        int deleted = storageKeys.Count(key => Objects.Remove(key));
        DeletedKeys.AddRange(storageKeys);
        return Task.FromResult(deleted);
    }

    public Task CopyAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
    {
        if (!Objects.TryGetValue(sourceStorageKey, out byte[]? bytes))
            throw new InvalidOperationException($"No object at {sourceStorageKey}.");
        Objects[destinationStorageKey] = bytes;
        return Task.CompletedTask;
    }

    public Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public Task<StoredFileObject> UploadAsync(
        Guid projectId, Guid fileId, Stream content, string fileName, string contentType,
        IReadOnlyDictionary<string, string>? metadata, CancellationToken ct,
        string category = "userFiles", string? originalPath = null) =>
        throw new NotSupportedException("The CSAM tests never upload through the store.");
}

/// <summary>Returns a fixed canonical PDQ hash without decoding, for policy tests.</summary>
internal sealed class FixedPdqHasher : IPdqHasher
{
    public static readonly string Hash = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    public int Calls { get; private set; }

    public bool TryHash(Stream stream, out string base64Hash, out int quality)
    {
        Calls++;
        base64Hash = Hash;
        quality = 87;
        return true;
    }
}

/// <summary>Fails the decode, so the matcher falls back to <c>/v1/media</c>.</summary>
internal sealed class UndecodablePdqHasher : IPdqHasher
{
    public bool TryHash(Stream stream, out string base64Hash, out int quality)
    {
        base64Hash = string.Empty;
        quality = 0;
        return false;
    }
}

/// <summary>The fake Project Arachnid Shield server. Records every request; no network.</summary>
internal sealed class CsamFakeServer
{
    public string PdqClassification { get; set; } = "no-known-match";
    public string? PdqMatchType { get; set; }
    public int PdqNearMatches { get; set; }

    public string MediaClassification { get; set; } = "no-known-match";
    public string? MediaMatchType { get; set; }
    public int MediaNearMatches { get; set; }
    public string? MediaSha256Hex { get; set; } = "abc123";

    public Func<HttpRequestMessage, byte[]?, HttpResponseMessage>? Override { get; set; }

    public HttpResponseMessage Respond(HttpRequestMessage request, byte[]? body)
    {
        if (Override is not null) return Override(request, body);

        string path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/v1/version", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"last_updated_at":"2026-10-01T00:00:00Z"}""");
        }

        if (path.EndsWith("/v1/pdq", StringComparison.Ordinal))
        {
            string hash = ReadHash(body);
            string json = JsonSerializer.Serialize(new
            {
                scanned_hashes = new Dictionary<string, object>
                {
                    [hash] = new
                    {
                        classification = PdqClassification,
                        match_type = PdqMatchType,
                        near_match_details = Enumerable.Range(0, PdqNearMatches).Select(_ => new { }).ToArray()
                    }
                }
            });
            return Json(HttpStatusCode.OK, json);
        }

        if (path.EndsWith("/v1/media", StringComparison.Ordinal))
        {
            string json = JsonSerializer.Serialize(new
            {
                classification = MediaClassification,
                match_type = MediaMatchType,
                near_match_details = Enumerable.Range(0, MediaNearMatches).Select(_ => new { }).ToArray(),
                sha256_hex = MediaSha256Hex
            });
            return Json(HttpStatusCode.OK, json);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string ReadHash(byte[]? body)
    {
        using JsonDocument document = JsonDocument.Parse(body ?? []);
        return document.RootElement.GetProperty("hashes")[0].GetString()!;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class CsamRecordingHandler(CsamFakeServer? server = null) : HttpMessageHandler
{
    private readonly CsamFakeServer _server = server ?? new CsamFakeServer();

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<byte[]> Bodies { get; } = [];

    public List<string> Paths => Requests.Select(r => r.RequestUri!.AbsolutePath).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[] body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add(request);
        Bodies.Add(body);
        return _server.Respond(request, body);
    }
}

/// <summary>Captures rendered log messages, so a test can assert none carries content or a token.</summary>
internal sealed class CsamListLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}

/// <summary>
/// A tenant context pinned to one organization. <see cref="BeginSystemScope"/> deliberately does
/// not flip <see cref="IsSystem"/>: a test uses this to prove a query really called
/// <c>IgnoreQueryFilters()</c> rather than relying on the system scope.
/// </summary>
internal sealed class CsamFixedTenant(Guid? organizationId) : ITenantContext
{
    public Guid? OrganizationId { get; } = organizationId;
    public bool IsSystem => false;
    public IDisposable BeginSystemScope() => Noop.Instance;
    public IDisposable BeginOrganizationScope(Guid organizationId) => Noop.Instance;

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }
}
