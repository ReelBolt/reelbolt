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
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// T-110: <c>DeclareRenderEvents</c> validates and writes <c>public/events.json</c>; a successful video render
/// uploads it as a non-playable <c>agentFiles/</c> sidecar keyed to the render's output and reports the count.
/// </summary>
public class ReactRemotionSandboxToolsRenderEventsTests
{
    private const string OutputFileName = "film.mp4";

    [Fact]
    public async Task DeclareRenderEvents_writes_the_validated_manifest_file()
    {
        Harness h = new();

        using JsonDocument result = JsonDocument.Parse(await h.Tool.DeclareRenderEvents(
        [
            new RenderEvent("stamp", 90, "Stamp lands"),
            new RenderEvent("typing", 10, "Typing starts")
        ]));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.RootElement.GetProperty("events").GetInt32().Should().Be(2);
        RenderEventManifest written = RenderEventManifest.Parse(h.Files[RenderEventManifest.SandboxPath]).Manifest!;
        written.Events.Select(e => e.Name).Should().Equal("typing", "stamp");
    }

    [Fact]
    public async Task DeclareRenderEvents_refuses_a_bad_declaration_and_writes_nothing()
    {
        Harness h = new();

        using JsonDocument result = JsonDocument.Parse(await h.Tool.DeclareRenderEvents([new RenderEvent("Bad Name", -1, "x")]));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.RootElement.GetProperty("errors").GetArrayLength().Should().Be(2);
        h.Files.Should().NotContainKey(RenderEventManifest.SandboxPath);
    }

    [Fact]
    public async Task A_video_render_uploads_the_manifest_as_an_agentFiles_sidecar_with_the_composition_fps()
    {
        Harness h = new();
        await h.Tool.DeclareRenderEvents([new RenderEvent("slam", 45, "Door slams"), new RenderEvent("ding", 5000, "Too late")]);

        JsonElement result = await h.RenderAsync();

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("events").GetInt32().Should().Be(1, "an event past the composition's last frame is not uploaded");
        result.GetProperty("eventWarnings").GetArrayLength().Should().Be(1);

        string renderKey = result.GetProperty("storageKey").GetString()!;
        string sidecarKey = RenderEventManifest.SidecarKeyFor(renderKey)!;
        sidecarKey.Should().Contain("/agentFiles/render-events/");
        h.Puts.Should().ContainKey(sidecarKey);
        RenderEventManifest sidecar = RenderEventManifest.Parse(h.Puts[sidecarKey]).Manifest!;
        sidecar.Fps.Should().Be(30);
        sidecar.Events.Should().ContainSingle().Which.Name.Should().Be("slam");
        h.PendingOutputKey.Should().Be(renderKey, "the playable output stays the render, never the sidecar");
    }

    [Fact]
    public async Task A_render_without_a_manifest_is_unchanged()
    {
        Harness h = new();

        JsonElement result = await h.RenderAsync();

        result.TryGetProperty("events", out _).Should().BeFalse();
        result.TryGetProperty("eventWarnings", out _).Should().BeFalse();
        h.Puts.Keys.Should().NotContain(k => k.Contains("render-events", StringComparison.Ordinal));
    }

    /// <summary>QA-P1-19 item 2: a render key is stable for the step result, so a re-render that declares no
    /// events must remove the sidecar the previous render left — otherwise the old <c>e{n}</c> anchors keep
    /// pointing at moments the new film does not have.</summary>
    [Fact]
    public async Task A_re_render_without_events_removes_the_previous_renders_sidecar()
    {
        Harness h = new();

        await h.RenderAsync();

        string sidecar = RenderEventManifest.SidecarKeyFor(h.PendingOutputKey!)!;
        h.Deletes.Should().Contain(sidecar, "a film with no events must not keep the previous render's anchors");
        h.Puts.Keys.Should().NotContain(k => k.Contains("render-events", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_hand_written_invalid_manifest_is_reported_and_not_uploaded()
    {
        Harness h = new();
        h.Files[RenderEventManifest.SandboxPath] = """{"events":[{"name":"NOPE","frame":3}]}""";

        JsonElement result = await h.RenderAsync();

        result.GetProperty("success").GetBoolean().Should().BeTrue("a bad manifest never fails the render");
        result.GetProperty("events").GetInt32().Should().Be(0);
        result.GetProperty("eventWarnings").GetArrayLength().Should().BeGreaterThan(0);
        h.Puts.Keys.Should().NotContain(k => k.Contains("render-events", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_still_render_never_uploads_events()
    {
        Harness h = new();
        await h.Tool.DeclareRenderEvents([new RenderEvent("slam", 1, "Door slams")]);

        string json = await h.Tool.RenderVideoAndUploadToStorage("Main", "poster.png");
        using JsonDocument doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("events", out _).Should().BeFalse();
        h.Puts.Keys.Should().NotContain(k => k.Contains("render-events", StringComparison.Ordinal));
    }

    [Fact]
    public void DeclareRenderEvents_rides_with_the_render_group_only()
    {
        ToolGroupCatalog.FunctionNamesFor(ToolGroup.SandboxRender).Should().Contain("DeclareRenderEvents");
        ToolGroupCatalog.FunctionNamesFor(ToolGroup.SandboxPreview).Should().NotContain("DeclareRenderEvents");
        ToolGroupCatalog.FunctionNamesFor(ToolGroup.SandboxAuthoring).Should().NotContain("DeclareRenderEvents");
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private sealed class Harness
    {
        private readonly Guid _executionId = Guid.NewGuid();
        private readonly Guid _projectId = Guid.NewGuid();
        private readonly WorkflowExecutionContextAccessor _accessor = new();

        public Harness()
        {
            WorkflowEngineDbContext db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
            db.WorkflowExecutions.Add(new WorkflowExecution { Id = _executionId, ProjectId = _projectId });
            db.SaveChanges();

            Mock<IAmazonS3> s3 = new();
            s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutObjectRequest, CancellationToken>((request, _) =>
                {
                    string body = string.Empty;
                    if (request.InputStream is { CanRead: true } stream)
                    {
                        using MemoryStream copy = new();
                        stream.CopyTo(copy);
                        body = Encoding.UTF8.GetString(copy.ToArray());
                    }
                    else if (request.ContentBody is not null)
                    {
                        body = request.ContentBody;
                    }

                    Puts[request.Key] = body;
                })
                .ReturnsAsync(new PutObjectResponse());

            s3.Setup(x => x.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
                .Callback<DeleteObjectRequest, CancellationToken>((request, _) => Deletes.Add(request.Key))
                .ReturnsAsync(new DeleteObjectResponse());

            _accessor.BeginScope(_executionId, _projectId, "correlation");

            Tool = new ReactRemotionSandboxTools(
                Mock.Of<IHttpClientFactory>(),
                _accessor,
                Mock.Of<IProjectFileWorkspace>(),
                s3.Object,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" })
                    .Build(),
                NullLogger<ReactRemotionSandboxTools>.Instance,
                new FakeScopeFactory(db),
                mediaProbe: null,
                usageRecorder: null,
                videoToolRunner: null,
                videoEditingOptions: null,
                sandboxSessions: new FixedSessionFactory(new FileTreeSession(_executionId, Files)));
        }

        public Dictionary<string, string> Files { get; } = new()
        {
            ["src/root.tsx"] = "export const Root = () => null;\n"
        };

        public Dictionary<string, string> Puts { get; } = new();

        /// <summary>QA-P1-19 item 2: the keys a render deleted, so a stale sidecar's removal is observable.</summary>
        public List<string> Deletes { get; } = [];

        public ReactRemotionSandboxTools Tool { get; }

        public string? PendingOutputKey => _accessor.Current?.PendingOutputStorageKey;

        public async Task<JsonElement> RenderAsync()
        {
            string json = await Tool.RenderVideoAndUploadToStorage("Main", OutputFileName);
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    private sealed class FixedSessionFactory(ISandboxSession session) : ISandboxSessionFactory
    {
        public ISandboxSession Create(Guid executionId) => session;
    }

    /// <summary>A local workspace session over an in-memory file tree; `compositions` lists one 30 fps, 300-frame film.</summary>
    private sealed class FileTreeSession(Guid executionId, Dictionary<string, string> files) : ISandboxSession
    {
        private static readonly byte[] RenderBytes = Encoding.ASCII.GetBytes("render");

        public Guid ExecutionId { get; } = executionId;

        public Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken)
        {
            string prefix = path.TrimEnd('/') + "/";
            List<SandboxFileEntry> entries = files.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => k[prefix.Length..])
                .Select(rest => rest.Contains('/')
                    ? new SandboxFileEntry(rest[..rest.IndexOf('/')], true, 0, DateTime.UnixEpoch)
                    : new SandboxFileEntry(rest, false, Encoding.UTF8.GetByteCount(files[prefix + rest]), DateTime.UnixEpoch))
                .DistinctBy(e => e.Name)
                .ToList();
            return Task.FromResult<IReadOnlyList<SandboxFileEntry>>(entries);
        }

        public Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken) =>
            files.TryGetValue(path, out string? text)
                ? Task.FromResult(new SandboxTextFile(path, text))
                : throw new SandboxRequestFailedException(HttpStatusCode.NotFound, "not found");

        public Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SandboxPublishRoute(false, null, null));

        public Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SandboxTransfer(1, null));

        public Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SandboxTransfer(4, null));

        public Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken)
        {
            HttpClient client = new();
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(RenderBytes) };
            return Task.FromResult(new SandboxRawFile(client, response, new MemoryStream(RenderBytes), RenderBytes.LongLength));
        }

        public Task<SandboxExecResult> ExecAsync(string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken)
        {
            string output = args.Contains("compositions")
                ? "The following compositions are available:\n\nMain    30      1920x1080      300 (10.00 sec)\n"
                : "rendered";
            return Task.FromResult(new SandboxExecResult(
                Succeeded: true,
                Payload: new SandboxExecResponse(output, null),
                StatusCode: 0,
                RawBody: JsonSerializer.Serialize(new { output }),
                RawError: string.Empty,
                TransportError: null));
        }

        public Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SandboxInfo("s", ExecutionId.ToString(), "c", ".", DateTime.UtcNow, DateTime.UtcNow));

        public Task<SandboxInfo> GetAsync(CancellationToken cancellationToken) => EnsureAsync(cancellationToken);

        public Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SandboxStatusResponse(true, true, true, true, "c", ".", DateTime.UtcNow, DateTime.UtcNow));

        public Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken)
        {
            files[path] = content;
            return Task.FromResult(new SandboxActionResult(true));
        }

        public Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<SandboxActionResult> DeletePathAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new SandboxActionResult(true));

        public Task<SandboxExecResponse> InstallPackagesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken)
            => Task.FromResult(new SandboxExecResponse(null, "not available"));

        public Task<SandboxActionResult> CompleteAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SandboxActionResult(true));
    }
}
