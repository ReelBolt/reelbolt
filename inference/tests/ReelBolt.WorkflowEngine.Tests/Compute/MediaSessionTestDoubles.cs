using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// The doubles D11's media-session tests share. Nothing here is a real gateway, a real object store or
/// a real ffmpeg: a remote media session is a TRANSPORT, so every test in this folder asserts on the
/// bytes that go out and the domain value that comes back.
/// </summary>
internal static class MediaSessionTestData
{
    public static readonly Guid Project = Guid.Parse("90000000-0000-4000-8000-000000000001");
    public static readonly Guid Job = Guid.Parse("1b000000-0000-4000-8000-000000000002");
    public static readonly Guid Session = Guid.Parse("5e000000-0000-4000-8000-000000000003");

    /// <summary>One op response body, exactly as the gateway composes it from the runner's op.response.</summary>
    public static string OpBody(string? resultJson, string opId = "op-1", bool ok = true, string? code = null, string? message = null, bool retryable = false)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            sessionId = Session,
            jobId = Job,
            opId,
            ok,
            result = ok && resultJson is not null ? JsonNode.Parse(resultJson) : null,
            error = code is null && message is null ? null : new { code, message, retryable },
            durationMs = 7
        });

    /// <summary>A refusal body, in the gateway's own <c>{error, reason, message}</c> shape.</summary>
    public static string RefusalBody(string error, string? reason = null, string? message = null)
        => System.Text.Json.JsonSerializer.Serialize(new { error, reason, message });
}

/// <summary>
/// A gateway whose session-op route is scripted per op, and which records every request so a test can
/// assert on the wire shape (the op name, the args, the opId and the timeout).
/// </summary>
internal sealed class ScriptedSessionGateway
{
    private readonly Dictionary<string, Func<JsonObject, HttpResponseMessage>> _byOp = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public Func<JsonObject, HttpResponseMessage>? Default { get; set; }

    public StubHttpMessageHandler Handler { get; }

    /// <summary>Every op request the workspace made, in order: the op name and the parsed args.</summary>
    public List<(string Op, JsonObject Args)> Ops { get; } = [];

    /// <summary>Every op request's opId, in order, so a retry can be shown to reuse one.</summary>
    public List<string> OpIds { get; } = [];

    public ScriptedSessionGateway()
    {
        Handler = new StubHttpMessageHandler(request =>
        {
            string body = Handler.Bodies.Count > 0 ? Handler.Bodies[^1] : "{}";
            JsonObject payload = JsonNode.Parse(body)!.AsObject();
            string op = payload["op"]!.GetValue<string>();
            JsonObject args = payload["args"]?.AsObject() ?? [];
            Ops.Add((op, args));
            OpIds.Add(payload["opId"]?.GetValue<string>() ?? string.Empty);
            _counts[op] = _counts.TryGetValue(op, out int n) ? n + 1 : 1;

            if (_byOp.TryGetValue(op, out Func<JsonObject, HttpResponseMessage>? scripted))
            {
                return Task.FromResult(scripted(args));
            }

            if (Default is not null)
            {
                return Task.FromResult(Default(args));
            }

            throw new InvalidOperationException("no scripted answer for op '" + op + "'");
        });
    }

    public int Count(string op) => _counts.TryGetValue(op, out int n) ? n : 0;

    public ScriptedSessionGateway On(string op, string resultJson)
        => Register(op, _ => ComputeTestFactory.Json(MediaSessionTestData.OpBody(resultJson)));

    public ScriptedSessionGateway On(string op, Func<JsonObject, HttpResponseMessage> answer)
        => Register(op, answer);

    public ScriptedSessionGateway OnOpError(string op, string code, string message, bool retryable = false, HttpStatusCode status = HttpStatusCode.OK)
        => Register(op, _ => ComputeTestFactory.Json(
            MediaSessionTestData.OpBody(null, ok: false, code: code, message: message, retryable: retryable), status));

    public ScriptedSessionGateway OnStatus(string op, HttpStatusCode status, string body)
        => Register(op, _ => ComputeTestFactory.Json(body, status));

    private ScriptedSessionGateway Register(string op, Func<JsonObject, HttpResponseMessage> answer)
    {
        _byOp[op] = answer;
        return this;
    }
}

/// <summary>An <see cref="IProjectFileWorkspace"/> that answers from a dictionary of storage key → bytes.</summary>
internal sealed class FakeProjectFileWorkspace : IProjectFileWorkspace
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>The <c>(storageKey, destinationPath)</c> of every download, in order.</summary>
    public List<(string Key, string Destination)> Downloads { get; } = [];

    /// <summary>The key of every bare artifact upload, in order.</summary>
    public List<string> Uploads { get; } = [];

    public Task<IReadOnlyList<ProjectWorkspaceFile>> ListFilesAsync(Guid projectId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProjectWorkspaceFile>>([]);

    public Task<string> ReadFileAsync(Guid projectId, string fileReference, CancellationToken ct)
        => Task.FromResult(string.Empty);

    public Task<ProjectWorkspaceFile> WriteTextFileAsync(Guid projectId, string fileName, string content, string contentType, CancellationToken ct, string category = "agentFiles", string? originalPath = null)
        => throw new NotSupportedException();

    public Task DownloadStorageKeyToFileAsync(Guid projectId, string storageKey, string destinationPath, CancellationToken ct)
    {
        Downloads.Add((storageKey, destinationPath));
        if (!Objects.TryGetValue(storageKey, out byte[]? bytes))
        {
            throw new FileNotFoundException("no such key", storageKey);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        return File.WriteAllBytesAsync(destinationPath, bytes, ct);
    }

    /// <summary>The bounded twin (S-67): checked here against the bytes this store holds, so a caller's cap means what it says.</summary>
    public Task DownloadStorageKeyToFileAsync(Guid projectId, string storageKey, string destinationPath, long maxBytes, CancellationToken ct)
    {
        if (Objects.TryGetValue(storageKey, out byte[]? bytes) && bytes.Length > maxBytes)
            throw new StorageObjectTooLargeException(maxBytes, bytes.Length);
        return DownloadStorageKeyToFileAsync(projectId, storageKey, destinationPath, ct);
    }

    public Task<ProjectWorkspaceFile> UploadBinaryFileAsync(Guid projectId, string localFilePath, string fileName, string contentType, SummaryStatus summaryStatus, FileIndexingStatus indexingStatus, CancellationToken ct, string category = "outputFiles", string? originalPath = null)
        => throw new NotSupportedException();

    /// <summary>
    /// Records the registration and answers with a row naming the key the caller already published —
    /// the real implementation's contract, minus the database. <see cref="Objects"/> is NOT touched:
    /// registration never creates an object, which is the whole reason this seam exists apart from
    /// <see cref="UploadBinaryFileAsync"/>.
    /// </summary>
    public Task<ProjectWorkspaceFile> RegisterStoredObjectAsync(Guid projectId, string storageKey, string fileName, string contentType, long sizeBytes, SummaryStatus summaryStatus, FileIndexingStatus indexingStatus, CancellationToken ct, string category = "outputFiles")
    {
        RegisteredObjects.Add((projectId, storageKey, fileName, contentType, category));
        var file = new ProjectWorkspaceFile(
            Guid.NewGuid(), projectId, fileName, fileName, category, storageKey, contentType,
            sizeBytes, DateTime.UtcNow, null);
        return Task.FromResult(file);
    }

    /// <summary>The <c>(projectId, storageKey, fileName, contentType, category)</c> of every registration, in order.</summary>
    public List<(Guid ProjectId, string StorageKey, string FileName, string ContentType, string Category)> RegisteredObjects { get; } = [];

    public async Task<string> UploadArtifactAsync(Guid projectId, string localFilePath, string fileName, string contentType, CancellationToken ct, string category = "agentFiles")
    {
        string key = ProjectFilePath.BuildStorageKey(projectId, category, directoryPath: null, storageFileName: fileName);
        Uploads.Add(key);
        Objects[key] = await File.ReadAllBytesAsync(localFilePath, ct);
        return key;
    }

    public Task<bool> ArtifactExistsAsync(Guid projectId, string storageKey, CancellationToken ct)
        => Task.FromResult(Objects.ContainsKey(storageKey));
}

/// <summary>A presigner that mints deterministic, recognisable URLs and records what it was asked for.</summary>
internal sealed class FakePresignedUrlService : IPresignedUrlService
{
    public List<(Guid ProjectId, string Key, TimeSpan Ttl)> Gets { get; } = [];

    public List<(Guid JobId, int Index, string ContentType, TimeSpan Ttl)> Puts { get; } = [];

    public Task<string> CreateGetAsync(Guid projectId, string key, TimeSpan ttl)
    {
        Gets.Add((projectId, key, ttl));
        return Task.FromResult("https://objects.test/get?key=" + Uri.EscapeDataString(key));
    }

    public Task<PresignedPut> CreatePutAsync(Guid jobId, int n, string contentType, TimeSpan ttl)
    {
        Puts.Add((jobId, n, contentType, ttl));
        return Task.FromResult(new PresignedPut(
            "https://objects.test/put?key=" + Uri.EscapeDataString(S3RunnerOutputStore.StagingKeyFor(jobId, n)),
            S3RunnerOutputStore.StagingKeyFor(jobId, n)));
    }

    /// <summary>A browser upload's presigned PUT, recorded separately from the runner's staging PUTs.</summary>
    public List<(Guid ProjectId, string Key, string ContentType, TimeSpan Ttl)> BrowserPuts { get; } = [];

    public Task<PresignedPut> CreateBrowserUploadPutAsync(Guid projectId, string key, string contentType, TimeSpan ttl)
    {
        BrowserPuts.Add((projectId, key, contentType, ttl));
        return Task.FromResult(new PresignedPut(
            "https://objects.test/browser-put?key=" + Uri.EscapeDataString(key),
            key));
    }

    public Task<long> PromoteAsync(string stagingKey, string finalKey, long expectedMaxBytes, CancellationToken cancellationToken = default)
        => Task.FromResult(0L);
}

/// <summary>An <see cref="IRunnerOutputStore"/> that answers from a dictionary, so verification runs with no object store.</summary>
internal sealed class FakeRunnerOutputStore : IRunnerOutputStore
{
    public Dictionary<string, long> Sizes { get; } = new(StringComparer.Ordinal);

    public List<string> Discarded { get; } = [];

    /// <summary>When set, the staging GET URL a test serves itself (a loopback listener); otherwise a fixed fake URL.</summary>
    public string? GetUrl { get; set; }

    public Task<long?> TryGetSizeAsync(string key, CancellationToken ct)
        => Task.FromResult(Sizes.TryGetValue(key, out long size) ? size : (long?)null);

    public Task<string> CreateStagingGetUrlAsync(string stagingKey, TimeSpan ttl, CancellationToken ct)
        => Task.FromResult(GetUrl ?? "https://objects.test/staging?key=" + Uri.EscapeDataString(stagingKey));

    public Task<long> PromoteAsync(string stagingKey, string finalKey, long maxBytes, CancellationToken ct)
        => Task.FromResult(Sizes.TryGetValue(stagingKey, out long size) ? size : 0L);

    public Task DiscardAsync(string key, CancellationToken ct)
    {
        Discarded.Add(key);
        return Task.CompletedTask;
    }

    public Task<string?> TryGetSha256Async(string key, long sizeBytes, long maxBytes, CancellationToken ct)
        => Task.FromResult<string?>(null);
}

/// <summary>An <see cref="IOutputVerifier"/> whose verdict the test states, recording what it was asked to verify.</summary>
internal sealed class FakeOutputVerifier : IOutputVerifier
{
    public List<RunnerOutputRequest> Requests { get; } = [];

    public OutputVerificationResult? Verdict { get; set; }

    public Task<OutputVerificationResult> VerifyAndPromoteAsync(RunnerOutputRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(Verdict ?? new OutputVerificationResult(
            Verified: true,
            FailureCode: null,
            FailureClass: FailureClass.None,
            Message: null,
            SizeBytes: 1234,
            Sha256: null,
            FinalKey: request.FinalKey));
    }
}
