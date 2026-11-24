using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// D8: the byte-moving ops on <see cref="ISandboxSession"/> — <c>stage</c>, <c>publish</c>, the raw
/// read a publish implies, and the route that says where a publish may go. The two things these tests
/// exist to pin are the ones a reviewer should be able to check by reading them: a runner is handed a
/// PRESIGNED URL and nothing else (one object, one method), and the only object it may write into is
/// its own job's staging key space — a final key is reachable only through D12's verifier.
/// </summary>
public class RemoteSandboxStagePublishTests
{
    private static readonly Guid Execution = Guid.Parse("e0000000-0000-4000-8000-0000000000d8");
    private static readonly Guid Project = Guid.Parse("9a000000-0000-4000-8000-0000000000d8");
    private static readonly Guid Org = Guid.Parse("0a000000-0000-4000-8000-0000000000d8");

    [Fact]
    public async Task Stage_hands_the_runner_a_presigned_get_and_nothing_else()
    {
        Harness harness = Build();
        harness.Objects["projects/p/agentFiles/vo.wav"] = Encoding.ASCII.GetBytes("RIFF");

        SandboxTransfer transfer = await harness.Session.StageFromUrlAsync(
            new SandboxStageRequest(
                "https://objects.test/get?key=" + Uri.EscapeDataString("projects/p/agentFiles/vo.wav"),
                "voiceover/scene-0.wav",
                MaxBytes: 512L * 1024 * 1024,
                CommandTimeoutSeconds: 300),
            CancellationToken.None);

        Op op = harness.Ops.Single(o => o.Name == "stage");
        op.Args!["url"]!.GetValue<string>().Should().StartWith("https://objects.test/get?key=");
        op.Args["path"]!.GetValue<string>().Should().Be("voiceover/scene-0.wav");
        op.Args["maxBytes"]!.GetValue<long>().Should().Be(512L * 1024 * 1024);

        // The protocol's sha256 is optional, and an absent one must be ABSENT rather than null.
        op.Args.Should().NotContainKey("sha256");

        transfer.Bytes.Should().Be(4);
        transfer.Sha256.Should().Be(Hex(harness.Workspace["voiceover/scene-0.wav"]));
    }

    [Fact]
    public async Task Publish_puts_the_file_at_the_put_url_the_caller_minted_and_reports_the_runners_digest()
    {
        Harness harness = Build();
        byte[] render = Encoding.ASCII.GetBytes("a render");
        harness.Workspace["out/promo.mp4"] = render;

        string putUrl = "https://objects.test/put?key=" + Uri.EscapeDataString("runner-staging/" + JobId + "/1");
        SandboxTransfer transfer = await harness.Session.PublishToUrlAsync(
            new SandboxPublishRequest(putUrl, "out/promo.mp4", MaxBytes: 2048, ContentType: "video/mp4", CommandTimeoutSeconds: 600),
            CancellationToken.None);

        Op op = harness.Ops.Single(o => o.Name == "publish");
        op.Args!["putUrl"]!.GetValue<string>().Should().Be(putUrl);
        op.Args["path"]!.GetValue<string>().Should().Be("out/promo.mp4");
        op.Args["contentType"]!.GetValue<string>().Should().Be("video/mp4");

        transfer.Bytes.Should().Be(render.LongLength);
        transfer.Sha256.Should().Be(Hex(render));

        // The engine's own object store received the bytes under the staging key the URL named.
        harness.Objects["runner-staging/" + JobId + "/1"].Should().Equal(render);
    }

    [Fact]
    public async Task A_runner_backed_session_routes_a_publish_through_its_own_jobs_staging_key_space()
    {
        Harness harness = Build();

        SandboxPublishRoute route = await harness.Session.ResolvePublishRouteAsync(CancellationToken.None);

        route.RequiresVerification.Should().BeTrue();
        route.RunnerJobId.Should().Be(JobId);
        route.Target.Should().Be(RunnerJobTarget.CloudPool);
    }

    [Fact]
    public async Task A_local_session_does_not_require_verification()
    {
        // Compute:Mode=Fabric can still answer Local, and then the bytes never leave this host:
        // there is no boundary to verify across, and the caller writes its final key directly.
        Harness harness = Build(ComputeDecision.Local(ComputeDecisionReasons.FabricUnavailable));

        SandboxPublishRoute route = await harness.Session.ResolvePublishRouteAsync(CancellationToken.None);

        route.RequiresVerification.Should().BeFalse();
        route.RunnerJobId.Should().BeNull();
        route.Target.Should().BeNull();
    }

    [Fact]
    public async Task Reading_a_workspace_file_raw_publishes_it_into_staging_and_serves_the_bytes_back()
    {
        Harness harness = Build();
        byte[] render = new byte[150_000];
        for (int i = 0; i < render.Length; i++)
        {
            render[i] = (byte)(i % 251);
        }

        harness.Workspace["out/promo.mp4"] = render;

        await using (SandboxRawFile raw = await harness.Session.OpenRawFileAsync("out/promo.mp4", 600, CancellationToken.None))
        {
            raw.ContentLength.Should().Be(render.LongLength);
            using MemoryStream buffer = new();
            await raw.Content.CopyToAsync(buffer);
            buffer.ToArray().Should().Equal(render);
        }

        // The read was served by a publish into the job's own staging key space — never inline.
        Op publish = harness.Ops.Single(o => o.Name == "publish");
        publish.Args!["putUrl"]!.GetValue<string>().Should().Contain(Uri.EscapeDataString("runner-staging/" + JobId + "/"));

        // ...and the staged copy is gone once the caller is done with it.
        harness.Discarded.Should().ContainSingle(k => k.StartsWith("runner-staging/" + JobId + "/", StringComparison.Ordinal));
        harness.Objects.Should().NotContainKey("runner-staging/" + JobId + "/1");
    }

    [Fact]
    public async Task A_repeated_raw_read_addresses_a_DIFFERENT_staging_object()
    {
        Harness harness = Build();
        harness.Workspace["a.mp4"] = Encoding.ASCII.GetBytes("a");
        harness.Workspace["b.mp4"] = Encoding.ASCII.GetBytes("b");

        await using (SandboxRawFile first = await harness.Session.OpenRawFileAsync("a.mp4", 600, CancellationToken.None))
        {
            using MemoryStream drain = new();
            await first.Content.CopyToAsync(drain);
        }

        await using (SandboxRawFile second = await harness.Session.OpenRawFileAsync("b.mp4", 600, CancellationToken.None))
        {
            using MemoryStream drain = new();
            await second.Content.CopyToAsync(drain);
        }

        List<string> puts = harness.Ops
            .Where(o => o.Name == "publish")
            .Select(o => o.Args!["putUrl"]!.GetValue<string>())
            .ToList();

        puts.Should().HaveCount(2);
        puts.Distinct(StringComparer.Ordinal).Should().HaveCount(2);
    }

    [Fact]
    public async Task The_local_arm_of_stage_fetches_the_url_itself_and_pushes_the_bytes_into_the_workspace()
    {
        // The sandbox-executor's container has no egress, so on the local arm the ENGINE performs the
        // GET and issues the same writeFile call this feature has always used. The URL is still the
        // transport, and it is consumed exactly once.
        Harness harness = Build(ComputeDecision.Local(ComputeDecisionReasons.LocalMode));
        byte[] wav = Encoding.ASCII.GetBytes("RIFF....");
        harness.Objects["projects/" + Project.ToString("D") + "/agentFiles/vo.wav"] = wav;

        SandboxTransfer transfer = await harness.Session.StageFromUrlAsync(
            new SandboxStageRequest(
                harness.GetUrl("projects/" + Project.ToString("D") + "/agentFiles/vo.wav"),
                "voiceover/scene-0.wav",
                MaxBytes: 1024,
                CommandTimeoutSeconds: 60),
            CancellationToken.None);

        transfer.Bytes.Should().Be(wav.LongLength);
        harness.LocalWrites.Should().ContainKey("voiceover/scene-0.wav");
        harness.LocalWrites["voiceover/scene-0.wav"].Should().Equal(wav);
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private static readonly Guid JobId = Guid.Parse("7b000000-0000-4000-8000-0000000000d8");

    private sealed record Op(string Name, JsonObject? Args);

    private sealed class Harness
    {
        public required RemoteSandboxSession Session { get; init; }

        public required List<Op> Ops { get; init; }

        public required Dictionary<string, byte[]> Objects { get; init; }

        public required Dictionary<string, byte[]> Workspace { get; init; }

        public required List<string> Discarded { get; init; }

        public required Dictionary<string, byte[]> LocalWrites { get; init; }

        public string GetUrl(string key) => "https://objects.test/get?key=" + Uri.EscapeDataString(key);
    }

    private static Harness Build(ComputeDecision? decision = null)
    {
        List<Op> ops = [];
        Dictionary<string, byte[]> objects = new(StringComparer.Ordinal);
        Dictionary<string, byte[]> workspace = new(StringComparer.Ordinal)
        {
            ["out/promo.mp4"] = Encoding.ASCII.GetBytes("rendered")
        };
        List<string> discarded = [];
        Dictionary<string, byte[]> localWrites = new(StringComparer.Ordinal);

        StubHttpMessageHandler handler = new(request => Handle(request, ops, objects, workspace, localWrites));

        ComputeOptions compute = ComputeTestFactory.Options(sessionWaitSeconds: 10);
        ExecutionSessionGate gate = new(Options.Create(compute));
        RunnerGatewayClient gateway = ComputeTestFactory.Gateway(handler, compute, gate);

        LocalSandboxSessionFactory localFactory = new(
            new LocalStubFactory(handler),
            new ConfigurationBuilder().Build());

        FakeSandboxJournalStore store = new();
        RemoteSandboxSession session = new(
            Execution,
            new FixedComputeTarget(decision ?? ComputeDecision.Runner(RunnerJobTarget.CloudPool, ComputeDecisionReasons.PreferenceCloudOnly)),
            gateway,
            new FakeSandboxScopeResolver(Project, Org),
            new SandboxWorkspaceJournal(store),
            localFactory,
            new StubHttpClientFactory(handler),
            new FakePresignedUrlService(),
            new RecordingOutputStore(objects, discarded),
            Options.Create(compute),
            Options.Create(new SandboxSessionOptions()),
            NullLogger<RemoteSandboxSession>.Instance);

        return new Harness
        {
            Session = session,
            Ops = ops,
            Objects = objects,
            Workspace = workspace,
            Discarded = discarded,
            LocalWrites = localWrites
        };
    }

    /// <summary>
    /// The runner AND the object store, behind one handler because both are addressed by absolute URL
    /// from the same client: anything on <c>objects.test</c> is a presigned transfer, anything on the
    /// gateway's own base address is the gateway. The runner really executes <c>publish</c> and
    /// <c>stage</c> against the same dictionary the presigned GET is served from, so a URL that names
    /// an object nobody wrote FAILS here rather than quietly succeeding.
    /// </summary>
    private static async Task<HttpResponseMessage> Handle(
        HttpRequestMessage request,
        List<Op> ops,
        Dictionary<string, byte[]> objects,
        Dictionary<string, byte[]> workspace,
        Dictionary<string, byte[]> localWrites)
    {
        if (request.RequestUri!.AbsolutePath.StartsWith("/api/v1/sandboxes/", StringComparison.Ordinal))
        {
            // The self-host sandbox-executor own write route, which is how the LOCAL arm of stage
            // puts bytes into a workspace ("the Local session keeps the D0 streaming path").
            string staged = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync();
            using JsonDocument stagedBody = JsonDocument.Parse(staged);
            localWrites[PathOf(request.RequestUri)] = Convert.FromBase64String(
                stagedBody.RootElement.GetProperty("contentBase64").GetString() ?? string.Empty);
            return ComputeTestFactory.Json("{\"ok\":true}");
        }

        if (string.Equals(request.RequestUri!.Host, "objects.test", StringComparison.Ordinal))
        {
            string key = KeyOf(request.RequestUri);

            if (request.Method == HttpMethod.Get)
            {
                if (!objects.TryGetValue(key, out byte[]? content))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            }

            byte[] body = await request.Content!.ReadAsByteArrayAsync();
            objects[key] = body;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        string path = request.RequestUri.AbsolutePath;

        if (path.EndsWith("/jobs", StringComparison.Ordinal))
        {
            return ComputeTestFactory.Json(ComputeTestFactory.JobBody(
                JobId,
                sessionId: Guid.Parse("5e000000-0000-4000-8000-0000000000d8"),
                status: "Running",
                jobType: "remotion.workspace-session"));
        }

        string raw = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(raw);
        string op = document.RootElement.GetProperty("op").GetString() ?? string.Empty;
        JsonNode? args = document.RootElement.TryGetProperty("args", out JsonElement argsElement)
            ? JsonNode.Parse(argsElement.GetRawText())
            : null;

        lock (ops)
        {
            ops.Add(new Op(op, args as JsonObject));
        }

        JsonObject result;
        switch (op)
        {
            case "publish":
                {
                    string path_ = args!["path"]!.GetValue<string>();
                    byte[] content = workspace[path_];
                    objects[KeyOf(new Uri(args["putUrl"]!.GetValue<string>()))] = content;
                    result = new JsonObject { ["bytes"] = content.LongLength, ["sha256"] = Hex(content) };
                    break;
                }

            case "stage":
                {
                    string destination = args!["path"]!.GetValue<string>();
                    byte[] content = objects[KeyOf(new Uri(args["url"]!.GetValue<string>()))];
                    workspace[destination] = content;
                    result = new JsonObject { ["bytes"] = content.LongLength, ["sha256"] = Hex(content) };
                    break;
                }

            case "writeFile":
                {
                    string destination = args!["path"]!.GetValue<string>();
                    byte[] content = string.Equals(args["encoding"]?.GetValue<string>(), "base64", StringComparison.Ordinal)
                        ? Convert.FromBase64String(args["content"]!.GetValue<string>())
                        : Encoding.UTF8.GetBytes(args["content"]!.GetValue<string>());
                    workspace[destination] = content;
                    result = new JsonObject { ["bytes"] = content.LongLength, ["sha256"] = Hex(content) };
                    break;
                }

            case "status":
                result = new JsonObject { ["workspaceReady"] = true, ["diskUsedBytes"] = 0, ["openProcesses"] = 0 };
                break;

            case "close":
                result = new JsonObject();
                break;

            default:
                return ComputeTestFactory.Json(
                    JsonSerializer.Serialize(new { sessionId = Guid.Empty, jobId = JobId, opId = "x", ok = false, error = new { code = "unsupported_op", message = op, retryable = false } }));
        }

        return ComputeTestFactory.Json(JsonSerializer.Serialize(new
        {
            sessionId = Guid.Parse("5e000000-0000-4000-8000-0000000000d8"),
            jobId = JobId,
            opId = "x",
            ok = true,
            result
        }));
    }

    private static string PathOf(Uri uri)
    {
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "path")
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        throw new InvalidOperationException("the URL carries no sandbox path: " + uri);
    }

    private static string KeyOf(Uri uri)
    {
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "key")
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        throw new InvalidOperationException("the URL carries no key: " + uri);
    }

    private static string Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>The object store behind the staging key space, recording what was deleted.</summary>
    private sealed class RecordingOutputStore(Dictionary<string, byte[]> objects, List<string> discarded) : IRunnerOutputStore
    {
        public Task<long?> TryGetSizeAsync(string key, CancellationToken ct)
            => Task.FromResult(objects.TryGetValue(key, out byte[]? value) ? (long?)value.LongLength : null);

        public Task<string> CreateStagingGetUrlAsync(string stagingKey, TimeSpan ttl, CancellationToken ct)
            => Task.FromResult("https://objects.test/get?key=" + Uri.EscapeDataString(stagingKey));

        public Task<long> PromoteAsync(string stagingKey, string finalKey, long maxBytes, CancellationToken ct)
            => throw new NotSupportedException("promotion belongs to the verifier");

        public Task DiscardAsync(string key, CancellationToken ct)
        {
            discarded.Add(key);
            objects.Remove(key);
            return Task.CompletedTask;
        }

        public Task<string?> TryGetSha256Async(string key, long sizeBytes, long maxBytes, CancellationToken ct)
            => Task.FromResult(objects.TryGetValue(key, out byte[]? value) ? Hex(value) : null);
    }

    /// <summary>
    /// An <see cref="IHttpClientFactory"/> for the LOCAL sandbox-executor. It is the same handler as
    /// everything else, because the local arm of a staging op puts its bytes into the executor's own
    /// write route — the one route the harness serves on the sandbox.test host.
    /// </summary>
    private sealed class LocalStubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://sandbox.test") };
    }
}