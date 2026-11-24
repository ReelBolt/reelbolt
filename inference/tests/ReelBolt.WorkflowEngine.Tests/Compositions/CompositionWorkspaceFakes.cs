using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compositions;
using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Tests.Compositions;

/// <summary>
/// The composition-revision library wired over an in-memory database, an in-memory object store and
/// fake sandbox sessions that move REAL tar.gz archives (System.Formats.Tar), so a capture → store →
/// restore round trip exercises the same bytes-in/bytes-out contract the sandbox-executor serves.
/// </summary>
internal sealed class CompositionHarness : IDisposable
{
    public CompositionHarness()
    {
        Scratch = Path.Combine(Path.GetTempPath(), "rb-composition-tests-" + Guid.NewGuid().ToString("N"));
        Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CompositionWorkspaces:ScratchPath"] = Scratch,
                ["MinIO:BucketName"] = "test-bucket"
            })
            .Build();

        string dbName = "compositions-" + Guid.NewGuid().ToString("N");
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName));
        Provider = services.BuildServiceProvider();

        Mock<IAmazonS3> s3 = new();
        s3.Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PutObjectRequest, CancellationToken>(async (req, ct) =>
            {
                using MemoryStream buffer = new();
                await req.InputStream.CopyToAsync(buffer, ct);
                Objects[req.Key] = buffer.ToArray();
                return new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK };
            });
        s3.Setup(c => c.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((_, key, _) =>
                Objects.TryGetValue(key, out byte[]? bytes)
                    ? Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream(bytes), HttpStatusCode = HttpStatusCode.OK })
                    : throw new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" });
        S3 = s3.Object;

        ILoggerFactory logs = Provider.GetRequiredService<ILoggerFactory>();
        IServiceScopeFactory scopes = Provider.GetRequiredService<IServiceScopeFactory>();
        Store = new CompositionWorkspaceStore(scopes, S3, Configuration, logs.CreateLogger<CompositionWorkspaceStore>());
        Capture = new CompositionWorkspaceCapture(Store, scopes, Configuration, logs.CreateLogger<CompositionWorkspaceCapture>());
        Restorer = new CompositionWorkspaceRestorer(Store, Configuration, logs.CreateLogger<CompositionWorkspaceRestorer>());
    }

    public string Scratch { get; }
    public IConfiguration Configuration { get; }
    public ServiceProvider Provider { get; }
    public IAmazonS3 S3 { get; }
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
    public CompositionWorkspaceStore Store { get; }
    public CompositionWorkspaceCapture Capture { get; }
    public CompositionWorkspaceRestorer Restorer { get; }

    public WorkflowEngineDbContext Db()
        => Provider.CreateScope().ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

    public async Task SeedExecutionAsync(Guid executionId, Guid projectId, Guid? userId = null, DateTime? startedAt = null)
    {
        using WorkflowEngineDbContext db = Db();
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            ProjectId = projectId,
            WorkflowDefinitionId = Guid.NewGuid(),
            InitiatedByUserId = userId,
            StartedAt = startedAt,
            FirstStartedAt = startedAt,
            Status = ExecutionStatus.Passed
        });
        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        Provider.Dispose();
        try
        {
            Directory.Delete(Scratch, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

/// <summary>A sandbox whose workspace is a dictionary of files, transferred as a deterministic tar.gz.</summary>
internal sealed class FakeWorkspaceSession(Guid executionId) : ISandboxSession
{
    public Guid ExecutionId { get; } = executionId;

    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public bool Exists { get; set; } = true;

    public List<string> ExtraDependencies { get; } = [];

    public Exception? InstallFailure { get; set; }

    public List<IReadOnlyList<string>> Installs { get; } = [];

    public int Imports { get; private set; }

    public static byte[] Pack(IReadOnlyDictionary<string, byte[]> files)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Optimal, leaveOpen: true))
        using (TarWriter tar = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach ((string path, byte[] bytes) in files.Where(f => !f.Key.StartsWith("node_modules/", StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                PaxTarEntry entry = new(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(bytes),
                    ModificationTime = DateTimeOffset.UnixEpoch
                };
                tar.WriteEntry(entry);
            }
        }

        return output.ToArray();
    }

    public Task<SandboxWorkspaceArchive> ExportWorkspaceAsync(int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        if (!Exists)
            throw new SandboxRequestFailedException(HttpStatusCode.NotFound, "Sandbox API request failed (404): sandbox not found");
        byte[] archive = Pack(Files);
        return Task.FromResult(new SandboxWorkspaceArchive(new MemoryStream(archive), Files.Count, Files.Sum(f => f.Value.LongLength), 0));
    }

    public async Task<SandboxWorkspaceImport> ImportWorkspaceAsync(Stream archive, long length, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        Imports++;
        foreach (string key in Files.Keys.Where(k => !k.StartsWith("node_modules/", StringComparison.Ordinal)).ToList())
            Files.Remove(key);

        using MemoryStream buffer = new();
        await archive.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        int files = 0;
        await using (GZipStream gzip = new(buffer, CompressionMode.Decompress))
        await using (TarReader tar = new(gzip))
        {
            while (await tar.GetNextEntryAsync(copyData: true, cancellationToken) is { } entry)
            {
                using MemoryStream content = new();
                if (entry.DataStream is not null)
                    await entry.DataStream.CopyToAsync(content, cancellationToken);
                Files[entry.Name] = content.ToArray();
                files++;
            }
        }

        return new SandboxWorkspaceImport(files, 0, length, true, ExtraDependencies.Count == 0, [.. ExtraDependencies]);
    }

    public Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken)
        => Task.FromResult(new SandboxInfo("sbx", ExecutionId.ToString(), "rf-sbx", "/ws", DateTime.UtcNow, DateTime.UtcNow));

    public Task<SandboxExecResponse> InstallPackagesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken)
    {
        Installs.Add(packages);
        if (InstallFailure is not null)
            throw InstallFailure;
        return Task.FromResult(new SandboxExecResponse("installed", null));
    }

    public Task<SandboxInfo> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    /// <summary>When true, listing fails the way an unreachable sandbox does (the source digest cannot be read).</summary>
    public bool ListingFails { get; set; }

    public Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken)
    {
        if (ListingFails)
            throw new SandboxRequestFailedException(HttpStatusCode.BadGateway, "Sandbox API request failed (502): unreachable");
        string prefix = path.TrimEnd('/') + "/";
        Dictionary<string, SandboxFileEntry> entries = new(StringComparer.Ordinal);
        foreach ((string file, byte[] content) in Files)
        {
            if (!file.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            string rest = file[prefix.Length..];
            int slash = rest.IndexOf('/');
            string name = slash < 0 ? rest : rest[..slash];
            entries[name] = new SandboxFileEntry(name, slash >= 0, slash < 0 ? content.LongLength : 0, DateTime.UnixEpoch);
        }

        return Task.FromResult<IReadOnlyList<SandboxFileEntry>>([.. entries.Values]);
    }

    public Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken) =>
        Files.TryGetValue(path, out byte[]? bytes)
            ? Task.FromResult(new SandboxTextFile(path, System.Text.Encoding.UTF8.GetString(bytes)))
            : throw new SandboxRequestFailedException(HttpStatusCode.NotFound, "Sandbox API request failed (404): file not found");

    public Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxActionResult> DeletePathAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxExecResult> ExecAsync(string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxActionResult> CompleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken) => Task.FromResult(new SandboxPublishRoute(false, null, null));
}

/// <summary>A session that keeps every pre-existing default: the workspace transfer is unsupported.</summary>
internal sealed class TransferlessSession(Guid executionId) : ISandboxSession
{
    public Guid ExecutionId { get; } = executionId;
    public Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxInfo> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxActionResult> DeletePathAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxExecResult> ExecAsync(string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxExecResponse> InstallPackagesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxActionResult> CompleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class DictionarySessionFactory : ISandboxSessionFactory
{
    public Dictionary<Guid, ISandboxSession> Sessions { get; } = new();

    public ISandboxSession Create(Guid executionId) =>
        Sessions.TryGetValue(executionId, out ISandboxSession? session)
            ? session
            : Sessions[executionId] = new FakeWorkspaceSession(executionId) { Exists = false };
}

/// <summary>
/// A stand-in for CompositionReviserAgent: runs a callback against the ambient execution context (where
/// the real agent's tools would edit files and render), then answers with a fixed output.
/// </summary>
internal sealed class ScriptedReviser(
    IWorkflowExecutionContextAccessor accessor,
    Func<WorkflowExecutionContext, string, Task> onRun,
    bool success = true) : IReelBoltAgent
{
    public string? LastPrompt { get; private set; }
    public List<string> Prompts { get; } = [];
    public Guid? AgentId => null;
    public string Name => "CompositionReviser";
    public string Description => "test";
    public string SystemPrompt => string.Empty;
    public AgentType AgentType => AgentType.CompositionReviser;
    public IReadOnlyList<AIFunction> Tools => [];
    public string? OutputSchemaJson => null;
    public Type? OutputSchemaType => null;

    public async Task<AgentRunResult> RunAsync(string prompt, Guid? agentDefinitionId = null, CancellationToken ct = default)
    {
        LastPrompt = prompt;
        Prompts.Add(prompt);
        await onRun(accessor.Current!, prompt);
        return new AgentRunResult
        {
            Output = "{\"summary\":\"Bigger intro title.\",\"changedFiles\":[\"src/Intro.tsx\"],\"changedScenes\":[\"Intro\"],\"compositionId\":\"FinalVideo\",\"rendered\":true,\"notes\":\"\"}",
            Success = success,
            FailureReason = success ? null : "model gave up",
            TokensUsed = 1234
        };
    }
}

/// <summary>
/// The platform's fallback render, scripted: records each call and, unless told to fail, records the
/// output exactly where the real render tool does (the scope's <c>PendingOutputStorageKey</c>).
/// </summary>
internal sealed class ScriptedRevisionRenderer(IWorkflowExecutionContextAccessor accessor) : ICompositionRevisionRenderer
{
    public List<(string CompositionId, string OutputFileName)> Calls { get; } = [];
    public Exception? Throws { get; set; }
    public string? FailsWith { get; set; }

    /// <summary>Errors the next calls fail with, one per call, before <see cref="FailsWith"/> applies.</summary>
    public Queue<string> FailNext { get; } = new();

    public Task<CompositionRevisionRenderResult> RenderAsync(string compositionId, string outputFileName, CancellationToken ct)
    {
        Calls.Add((compositionId, outputFileName));
        if (Throws is not null)
            throw Throws;
        if (FailNext.TryDequeue(out string? next))
            return Task.FromResult(new CompositionRevisionRenderResult(false, next));
        if (FailsWith is not null)
            return Task.FromResult(new CompositionRevisionRenderResult(false, FailsWith));

        WorkflowExecutionContext ctx = accessor.Current!;
        ctx.PendingOutputStorageKey = $"projects/{ctx.ProjectId}/outputFiles/{ctx.ExecutionId}/{outputFileName}";
        return Task.FromResult(new CompositionRevisionRenderResult(true, null));
    }
}

internal sealed class SingleAgentRegistry(IReelBoltAgent? agent) : IAgentRegistry
{
    public IReelBoltAgent? GetByType(AgentType agentType, Guid? agentId = null) => agentType == AgentType.CompositionReviser ? agent : null;
    public IReadOnlyList<IReelBoltAgent> GetAll() => agent is null ? [] : [agent];
}
